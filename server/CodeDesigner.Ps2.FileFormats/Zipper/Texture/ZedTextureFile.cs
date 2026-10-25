using System;
using System.Collections.Generic;
using System.IO;

namespace CodeDesigner.Ps2.FileFormats.Zipper.Texture
{
    /// <summary>
    /// Decoder/encoder for Zipper PS2 ".tif" texture records (texdat) packed
    /// inside ZED containers. Mirrors zed-texture-parser.ts on the webview
    /// side; this is the canonical C# port so MCP tools and the bridge can
    /// both encode and decode.
    ///
    /// texdat layout (verified by the SOCOM II pack builders in Mods/SOCOMCA,
    /// whose output renders in game):
    ///   +0x00 u16 width          +0x02 u16 height
    ///   +0x04 u32 pixel bytes    +0x08 u32 texture index (TBP0 slot, not a palette id)
    ///   +0x0C u8  bits per pixel +0x0D 0x06  +0x0E 0
    ///   +0x0F u8  0x10 direct / 0x12 16-bit palette / 0x13 32-bit palette
    ///   +0x20     pixels
    ///   then      a GIF packet: GIF tag, then A+D register pairs (ALPHA, TEX1,
    ///             TEX0, TEST, CLAMP ...). TEX0 carries the pixel format (PSM),
    ///             the log2 size and CBP = the palette id (texpal_&lt;CBP&gt; in the
    ///             PAL archive). The game binds palettes by that id.
    /// </summary>
    public static class ZedTextureFile
    {
        public const int HeaderSize = 0x20;
        public const int FooterSize = 0x80;

        // GS pixel storage modes.
        public const int PsmCt32 = 0x00;
        public const int PsmCt24 = 0x01;
        public const int PsmCt16 = 0x02;
        public const int PsmCt16S = 0x0A;
        public const int PsmT8 = 0x13;
        public const int PsmT4 = 0x14;
        public const int PsmT8H = 0x1B;
        public const int PsmT4HL = 0x24;
        public const int PsmT4HH = 0x2C;

        private const int GsRegTex0 = 0x06;
        private const int GsRegClamp = 0x08;
        private const int GsRegTest = 0x47;
        private const int GsRegAlpha = 0x42;

        /// <summary>GS registers read from the texdat tail.</summary>
        public sealed class GsRegisters
        {
            public uint Tex0Lo { get; set; }
            public uint Tex0Hi { get; set; }
            /// <summary>Pixel storage mode (PsmCt32, PsmT8 ...).</summary>
            public int Psm { get; set; }
            /// <summary>CLUT base pointer: the texpal id this texture binds to.</summary>
            public int Cbp { get; set; }
            /// <summary>CLUT pixel format: 0 = 32-bit RGBA, 2 = 16-bit A1B5G5R5.</summary>
            public int Cpsm { get; set; }
            /// <summary>CLUT storage mode: 0 = CSM1 (swizzled 8-bit layout), 1 = CSM2.</summary>
            public int Csm { get; set; }
            public int Log2Width { get; set; }
            public int Log2Height { get; set; }
            /// <summary>Texture function: 0 modulate, 1 decal, 2 highlight, 3 highlight2.</summary>
            public int Tfx { get; set; }
            /// <summary>From CLAMP_1: true = clamp, false = repeat.</summary>
            public bool ClampS { get; set; }
            public bool ClampT { get; set; }
            /// <summary>
            /// From TEST_1: alpha test threshold in PNG alpha space (0..1), or null
            /// when the alpha test is off / not a "keep opaque texels" comparison.
            /// </summary>
            public float? AlphaTest { get; set; }
            /// <summary>From ALPHA_1: true when the blend equation reads the frame buffer.</summary>
            public bool AlphaBlend { get; set; }
            /// <summary>Register addresses found in the tail.</summary>
            public List<int> Registers { get; } = new();
        }

        public sealed class Header
        {
            public ushort Width { get; set; }
            public ushort Height { get; set; }
            // Historical names kept for callers: the u16 halves of the u32
            // texture index at +0x08. They are NOT palette ids; use
            // ResolvePaletteId() which prefers TEX0.CBP.
            public ushort PaletteId { get; set; }
            public ushort PaletteAlt { get; set; }
            public ushort FormatA { get; set; }
            public ushort FormatB { get; set; }
            public int Bpp { get; set; }
            public bool IsIndexed { get; set; }
            /// <summary>Declared pixel byte count (+0x04) when sane, else derived.</summary>
            public int PixelBytes { get; set; }
            /// <summary>Texture index at +0x08 (TBP0 slot).</summary>
            public uint TextureIndex { get; set; }
            /// <summary>GS pixel storage mode of the pixel data.</summary>
            public int Psm { get; set; }
            /// <summary>Exact palette id from TEX0.CBP, or null when no TEX0 was found.</summary>
            public int? Cbp { get; set; }
            /// <summary>GS registers from the tail, or null when none were found.</summary>
            public GsRegisters? Gs { get; set; }
            // Raw header bytes preserved verbatim. Encoder copies these back so
            // any fields we don't model (texture flags, mip levels) survive.
            public byte[] RawHeader { get; set; } = Array.Empty<byte>();
            // Raw footer bytes (everything after the pixels) preserved verbatim
            // for the same reason.
            public byte[] RawFooter { get; set; } = Array.Empty<byte>();

            /// <summary>
            /// The palette id the game binds: TEX0.CBP when the record carries
            /// its GS registers, else the legacy header slot.
            /// </summary>
            public int ResolvePaletteId() => Cbp ?? PaletteId;
        }

        public sealed class DecodedTexture
        {
            public Header Header { get; set; } = new Header();
            // RGBA8888 pixels, top-down (already row-flipped from PS2 bottom-up).
            public byte[] Rgba { get; set; } = Array.Empty<byte>();
            public bool PaletteResolved { get; set; }
            /// <summary>True when any decoded pixel is not fully opaque.</summary>
            public bool HasTransparency { get; set; }
        }

        public sealed class Palette
        {
            public int PaletteId { get; set; }
            // 256 RGBA8888 entries = 1024 bytes, already PS2-unswizzled.
            public byte[] Rgba { get; set; } = Array.Empty<byte>();
            /// <summary>True when the source CLUT was 16-bit A1B5G5R5 (512 bytes).</summary>
            public bool Is16Bit { get; set; }
        }

        private static int? BppForPsm(int psm) => psm switch
        {
            PsmCt32 => 32,
            PsmCt24 => 24,
            PsmCt16 or PsmCt16S => 16,
            PsmT8 or PsmT8H => 8,
            PsmT4 or PsmT4HL or PsmT4HH => 4,
            _ => null,
        };

        private static bool PsmIsIndexed(int psm) =>
            psm is PsmT8 or PsmT8H or PsmT4 or PsmT4HL or PsmT4HH;

        /// <summary>
        /// Scans the GIF A+D tail that follows the pixel data for the GS
        /// registers the game uploads with the texture. Null without a TEX0.
        /// </summary>
        public static GsRegisters? ParseGsRegisters(byte[] bytes, int tailStart)
        {
            if (tailStart < 0 || tailStart + 16 > bytes.Length) return null;
            var regs = new Dictionary<int, (uint lo, uint hi)>();
            int limit = Math.Min(bytes.Length - 16, tailStart + 16 * 16);
            for (int o = tailStart; o <= limit; o += 16)
            {
                uint regLo = ReadU32(bytes, o + 8);
                uint regHi = ReadU32(bytes, o + 12);
                if (regHi != 0 || regLo > 0x7f) continue;
                int reg = (int)regLo;
                if (!regs.ContainsKey(reg))
                    regs[reg] = (ReadU32(bytes, o), ReadU32(bytes, o + 4));
            }
            if (!regs.TryGetValue(GsRegTex0, out var tex0)) return null;

            int psm = (int)((tex0.lo >> 20) & 0x3f);
            int log2Width = (int)((tex0.lo >> 26) & 0xf);
            int log2Height = (int)(((tex0.lo >> 30) & 0x3) | ((tex0.hi & 0x3) << 2));
            if (BppForPsm(psm) == null || log2Width > 10 || log2Height > 10) return null;

            var gs = new GsRegisters
            {
                Tex0Lo = tex0.lo,
                Tex0Hi = tex0.hi,
                Psm = psm,
                Log2Width = log2Width,
                Log2Height = log2Height,
                Tfx = (int)((tex0.hi >> 3) & 0x3),
                Cbp = (int)((tex0.hi >> 5) & 0x3fff),
                Cpsm = (int)((tex0.hi >> 19) & 0xf),
                Csm = (int)((tex0.hi >> 23) & 0x1),
            };
            if (regs.TryGetValue(GsRegClamp, out var clamp))
            {
                // WMS bits 0-1, WMT bits 2-3: 0 REPEAT, 1 CLAMP, 2 REGION_CLAMP, 3 REGION_REPEAT.
                uint wms = clamp.lo & 0x3, wmt = (clamp.lo >> 2) & 0x3;
                gs.ClampS = wms == 1 || wms == 2;
                gs.ClampT = wmt == 1 || wmt == 2;
            }
            if (regs.TryGetValue(GsRegTest, out var test) && (test.lo & 0x1) != 0)
            {
                // ATST bits 1-3: 5 GEQUAL, 6 GREATER, 7 NOTEQUAL; AREF bits 4-11 (0x80 = 1.0).
                uint atst = (test.lo >> 1) & 0x7;
                uint aref = (test.lo >> 4) & 0xff;
                if (atst == 5 || atst == 6)
                    gs.AlphaTest = Math.Min(1f, Ps2AlphaToPngAlpha((byte)aref) / 255f);
                else if (atst == 7 && aref == 0)
                    gs.AlphaTest = 1f / 255f;
            }
            if (regs.TryGetValue(GsRegAlpha, out var alpha))
            {
                uint a = alpha.lo & 0x3, b = (alpha.lo >> 2) & 0x3, d = (alpha.lo >> 6) & 0x3;
                gs.AlphaBlend = b == 1 || d == 1 || (a == 1 && d != 1);
            }
            foreach (int key in regs.Keys) gs.Registers.Add(key);
            return gs;
        }

        // -------------------------------------------------------------------
        // Decode
        // -------------------------------------------------------------------

        public static Header ParseHeader(byte[] bytes)
        {
            if (bytes.Length < HeaderSize)
                throw new InvalidDataException($"texture too small ({bytes.Length} bytes)");

            ushort width = BitConverter.ToUInt16(bytes, 0x00);
            ushort height = BitConverter.ToUInt16(bytes, 0x02);
            uint declaredPixelBytes = BitConverter.ToUInt32(bytes, 0x04);
            uint textureIndex = BitConverter.ToUInt32(bytes, 0x08);
            ushort paletteId = BitConverter.ToUInt16(bytes, 0x08);
            ushort paletteAlt = BitConverter.ToUInt16(bytes, 0x0A);
            ushort formatA = BitConverter.ToUInt16(bytes, 0x0C);
            ushort formatB = BitConverter.ToUInt16(bytes, 0x0E);
            int headerBpp = bytes[0x0C];

            int pixelCount = width * height;
            int available = bytes.Length - HeaderSize;
            int[] knownBpp = { 4, 8, 16, 24, 32 };
            bool declaredSane = declaredPixelBytes > 0 && declaredPixelBytes <= (uint)available && pixelCount > 0
                && Array.Exists(knownBpp, b => (long)pixelCount * b / 8 == declaredPixelBytes);

            // Prefer the GS registers in the tail: they say exactly how the
            // game interprets the pixels.
            GsRegisters? gs = null;
            if (declaredSane)
                gs = ParseGsRegisters(bytes, HeaderSize + (int)declaredPixelBytes);
            if (gs == null)
            {
                var candidates = new List<int>();
                if (bytes.Length >= HeaderSize + FooterSize) candidates.Add(bytes.Length - FooterSize);
                foreach (int b in new[] { 8, 32, 16, 4, 24 })
                {
                    long n = (long)pixelCount * b / 8;
                    if (n > 0 && n <= available && (pixelCount * b) % 8 == 0 && !candidates.Contains(HeaderSize + (int)n))
                        candidates.Add(HeaderSize + (int)n);
                }
                foreach (int start in candidates)
                {
                    gs = ParseGsRegisters(bytes, start);
                    if (gs != null) break;
                }
            }

            int bpp;
            bool isIndexed;
            int psm;
            if (gs != null)
            {
                psm = gs.Psm;
                bpp = BppForPsm(psm) ?? 8;
                isIndexed = PsmIsIndexed(psm);
                // CT24 pixels are stored either packed (3 bytes) or as 32-bit
                // words; the declared length tells which.
                if (psm == PsmCt24 && declaredSane && declaredPixelBytes == (uint)pixelCount * 4) bpp = 32;
            }
            else
            {
                int indexedSize = pixelCount + HeaderSize + FooterSize;
                int rgbaSize = pixelCount * 4 + HeaderSize + FooterSize;
                if (declaredSane && Array.IndexOf(knownBpp, headerBpp) >= 0 && (long)pixelCount * headerBpp / 8 == declaredPixelBytes)
                    bpp = headerBpp;
                else if (formatA == 0x0620 || bytes.Length == rgbaSize)
                    bpp = 32;
                else if (formatA == 0x0608 || bytes.Length == indexedSize)
                    bpp = 8;
                else if (formatA == 0x0610)
                    bpp = 16;
                else if (formatA == 0x0604)
                    bpp = 4;
                else if (Math.Abs(bytes.Length - rgbaSize) < Math.Abs(bytes.Length - indexedSize))
                    bpp = 32;
                else
                    bpp = 8;
                isIndexed = bpp == 8 || bpp == 4;
                psm = bpp switch { 32 => PsmCt32, 24 => PsmCt24, 16 => PsmCt16, 4 => PsmT4, _ => PsmT8 };
            }

            int pixelBytes = declaredSane
                ? (int)declaredPixelBytes
                : (int)Math.Min(available, ((long)pixelCount * bpp + 7) / 8);

            var rawHeader = new byte[HeaderSize];
            Buffer.BlockCopy(bytes, 0, rawHeader, 0, HeaderSize);

            // Everything after the pixels is the footer (GIF tag + registers).
            // Keep all of it so Encode reproduces the record verbatim.
            int footerStart = HeaderSize + pixelBytes;
            int footerLength = Math.Max(0, bytes.Length - footerStart);
            var rawFooter = new byte[footerLength];
            if (footerLength > 0)
                Buffer.BlockCopy(bytes, footerStart, rawFooter, 0, footerLength);

            return new Header
            {
                Width = width,
                Height = height,
                PaletteId = paletteId,
                PaletteAlt = paletteAlt,
                FormatA = formatA,
                FormatB = formatB,
                Bpp = bpp,
                IsIndexed = isIndexed,
                PixelBytes = pixelBytes,
                TextureIndex = textureIndex,
                Psm = psm,
                Cbp = gs?.Cbp,
                Gs = gs,
                RawHeader = rawHeader,
                RawFooter = rawFooter,
            };
        }

        public static DecodedTexture Parse(byte[] bytes, Palette? palette = null)
        {
            Header header = ParseHeader(bytes);
            int pixelCount = header.Width * header.Height;

            if (!header.IsIndexed)
            {
                byte[] direct = header.Bpp switch
                {
                    16 => DecodeRgb16(bytes, header),
                    24 => DecodeRgb24(bytes, header, 3),
                    _ when header.Psm == PsmCt24 => DecodeRgb24(bytes, header, 4),
                    _ => DecodeRgba32(bytes, header),
                };
                return new DecodedTexture
                {
                    Header = header,
                    Rgba = FlipRowsRgba(direct, header.Width, header.Height),
                    PaletteResolved = true,
                    HasTransparency = HasTransparentPixels(direct),
                };
            }

            bool is4Bit = header.Bpp == 4;
            int indexBytes = is4Bit ? (pixelCount + 1) / 2 : pixelCount;
            indexBytes = Math.Min(indexBytes, Math.Max(0, bytes.Length - HeaderSize));
            var indices = new byte[indexBytes];
            Buffer.BlockCopy(bytes, HeaderSize, indices, 0, indexBytes);

            var flat = new byte[pixelCount * 4];
            bool resolved = palette != null && palette.Rgba.Length >= 1024;
            for (int i = 0; i < pixelCount; i++)
            {
                int idx;
                if (is4Bit)
                {
                    if (i / 2 >= indices.Length) break;
                    byte b = indices[i / 2];
                    idx = (i & 1) == 0 ? (b & 0x0f) : (b >> 4);
                }
                else
                {
                    if (i >= indices.Length) break;
                    idx = indices[i];
                }

                if (resolved)
                {
                    // 4-bit textures use the first 16 CLUT entries in storage
                    // order; the decoded palette is in logical order, so map
                    // 8..15 back to 16..23.
                    int p = (is4Bit && idx >= 8 ? idx + 8 : idx) * 4;
                    flat[i * 4 + 0] = palette!.Rgba[p + 0];
                    flat[i * 4 + 1] = palette.Rgba[p + 1];
                    flat[i * 4 + 2] = palette.Rgba[p + 2];
                    flat[i * 4 + 3] = palette.Rgba[p + 3];
                }
                else
                {
                    // Grayscale fallback so callers without a palette still see
                    // something meaningful (matches the TS decoder).
                    byte v = (byte)(is4Bit ? idx * 17 : idx);
                    flat[i * 4 + 0] = v;
                    flat[i * 4 + 1] = v;
                    flat[i * 4 + 2] = v;
                    flat[i * 4 + 3] = 255;
                }
            }

            return new DecodedTexture
            {
                Header = header,
                Rgba = FlipRowsRgba(flat, header.Width, header.Height),
                PaletteResolved = resolved,
                HasTransparency = resolved && HasTransparentPixels(flat),
            };
        }

        public static Palette DecodeClutBuffer(byte[] buf, int paletteId)
        {
            byte[] rgba;
            bool is16Bit;
            if (buf.Length >= 1024)
            {
                rgba = UnswizzlePs2Clut8(Rgba32PaletteToRgba8888(buf, 0, 1024));
                is16Bit = false;
            }
            else if (buf.Length >= 512)
            {
                rgba = UnswizzlePs2Clut8(Rgba5551ToRgba8888(buf, 0, 512));
                is16Bit = true;
            }
            else
                throw new InvalidDataException($"CLUT too small ({buf.Length} bytes)");

            return new Palette { PaletteId = paletteId, Rgba = rgba, Is16Bit = is16Bit };
        }

        /// <summary>
        /// Inverse of <see cref="DecodeClutBuffer"/>: 256 logical RGBA8888 entries (PNG alpha) to a PAL <c>buf</c>,
        /// CSM1 storage order, as 32-bit (1024 bytes, GS alpha 0..0x80) or 16-bit A1B5G5R5 (512 bytes, STP on
        /// entries with alpha >= 0x80).
        /// </summary>
        public static byte[] EncodeClutBuffer(byte[] logicalRgba, bool is16Bit)
        {
            if (logicalRgba.Length < 1024) throw new ArgumentException("Palette must be 256 RGBA8888 entries.", nameof(logicalRgba));
            byte[] stored = UnswizzlePs2Clut8(logicalRgba); // the swap is its own inverse
            if (!is16Bit)
            {
                var buf = new byte[1024];
                for (int i = 0; i < 256; i++)
                {
                    buf[i * 4] = stored[i * 4];
                    buf[i * 4 + 1] = stored[i * 4 + 1];
                    buf[i * 4 + 2] = stored[i * 4 + 2];
                    buf[i * 4 + 3] = PngAlphaToPs2Alpha(stored[i * 4 + 3]);
                }
                return buf;
            }
            var buf16 = new byte[512];
            for (int i = 0; i < 256; i++)
            {
                int o = i * 4;
                int v = (stored[o] >> 3) | ((stored[o + 1] >> 3) << 5) | ((stored[o + 2] >> 3) << 10) | (stored[o + 3] >= 0x80 ? 0x8000 : 0);
                buf16[i * 2] = (byte)v;
                buf16[i * 2 + 1] = (byte)(v >> 8);
            }
            return buf16;
        }

        private static bool HasTransparentPixels(byte[] rgba)
        {
            for (int i = 3; i < rgba.Length; i += 4)
                if (rgba[i] < 255) return true;
            return false;
        }

        private static byte[] DecodeRgba32(byte[] bytes, Header header)
        {
            int pixelBytes = header.Width * header.Height * 4;
            var outBuf = new byte[pixelBytes];
            int n = Math.Min(pixelBytes, Math.Max(0, bytes.Length - HeaderSize));
            for (int i = 0; i + 3 < n; i += 4)
            {
                outBuf[i + 0] = bytes[HeaderSize + i + 0];
                outBuf[i + 1] = bytes[HeaderSize + i + 1];
                outBuf[i + 2] = bytes[HeaderSize + i + 2];
                outBuf[i + 3] = Ps2AlphaToPngAlpha(bytes[HeaderSize + i + 3]);
            }
            return outBuf;
        }

        // 24-bit colour: packed 3 bytes per pixel, or 32-bit words whose top
        // byte is unused (PSMCT24 in GS memory). Always opaque.
        private static byte[] DecodeRgb24(byte[] bytes, Header header, int stride)
        {
            int pixels = header.Width * header.Height;
            var outBuf = new byte[pixels * 4];
            for (int i = 0; i < pixels; i++)
            {
                int s = HeaderSize + i * stride;
                if (s + 2 >= bytes.Length) break;
                outBuf[i * 4 + 0] = bytes[s + 0];
                outBuf[i * 4 + 1] = bytes[s + 1];
                outBuf[i * 4 + 2] = bytes[s + 2];
                outBuf[i * 4 + 3] = 255;
            }
            return outBuf;
        }

        // A1B5G5R5 (PSMCT16): R in the low 5 bits, then G, B, and the STP/alpha
        // bit. Bit 15 maps through TEXA in the GS; SOCOM sets it on opaque
        // texels when the texture has any transparency at all and leaves it
        // clear everywhere on fully opaque textures. So: no texel with the bit
        // set means the image is opaque; otherwise a clear bit means transparent.
        private static byte[] DecodeRgb16(byte[] bytes, Header header)
        {
            int pixels = header.Width * header.Height;
            int count = Math.Min(pixels, Math.Max(0, (bytes.Length - HeaderSize) / 2));
            bool anyStp = false;
            for (int i = 0; i < count && !anyStp; i++)
                anyStp = (BitConverter.ToUInt16(bytes, HeaderSize + i * 2) & 0x8000) != 0;
            var outBuf = new byte[pixels * 4];
            for (int i = 0; i < count; i++)
            {
                ushort v = BitConverter.ToUInt16(bytes, HeaderSize + i * 2);
                outBuf[i * 4 + 0] = Expand5To8(v & 0x1f);
                outBuf[i * 4 + 1] = Expand5To8((v >> 5) & 0x1f);
                outBuf[i * 4 + 2] = Expand5To8((v >> 10) & 0x1f);
                outBuf[i * 4 + 3] = (byte)(!anyStp || (v & 0x8000) != 0 ? 255 : 0);
            }
            return outBuf;
        }

        // -------------------------------------------------------------------
        // Encode
        // -------------------------------------------------------------------

        /// <summary>
        /// Encodes a new TIF using the template header/footer verbatim, with
        /// new pixel data. Caller must supply RGBA pixels in top-down order
        /// matching the template's width*height. For indexed templates the
        /// caller can either pass <paramref name="paletteRgba"/> (256 RGBA8888
        /// entries) or omit it to have the encoder build one via median-cut.
        /// </summary>
        public static byte[] Encode(Header template, byte[] rgbaTopDown, byte[]? paletteRgba, out byte[]? generatedPalette)
        {
            if (template.RawHeader.Length != HeaderSize)
                throw new InvalidDataException("Template header is malformed.");
            if (template.RawFooter.Length < FooterSize)
                throw new InvalidDataException("Template footer is malformed.");

            int w = template.Width, h = template.Height;
            int expected = w * h * 4;
            if (rgbaTopDown.Length != expected)
                throw new ArgumentException($"Pixel buffer must be {expected} bytes for {w}x{h}.", nameof(rgbaTopDown));

            byte[] body;
            generatedPalette = null;

            if (template.IsIndexed && template.Bpp != 8)
                throw new NotSupportedException($"Encoding {template.Bpp}-bit indexed textures is not supported; convert the template to 8-bit.");
            if (!template.IsIndexed && template.Bpp == 24)
                throw new NotSupportedException("Encoding 24-bit textures is not supported.");

            if (template.IsIndexed)
            {
                byte[] palette = paletteRgba ?? MedianCutQuantize(rgbaTopDown, 256, out generatedPalette);
                if (palette.Length < 1024)
                    throw new ArgumentException("Indexed palette must be 256 RGBA8888 entries (1024 bytes).", nameof(paletteRgba));

                // Flip rows back to PS2 bottom-up before writing.
                byte[] flipped = FlipRowsRgba(rgbaTopDown, w, h);
                body = QuantizeWithPalette(flipped, palette);
            }
            else if (template.Bpp == 16)
            {
                // A1B5G5R5: row-flip back; the STP bit marks opaque texels
                // (alpha >= 0x80 after the PNG expansion) as the pack
                // builders do.
                byte[] flipped = FlipRowsRgba(rgbaTopDown, w, h);
                body = new byte[w * h * 2];
                for (int i = 0; i < w * h; i++)
                {
                    int o = i * 4;
                    int v = (flipped[o] >> 3) | ((flipped[o + 1] >> 3) << 5) | ((flipped[o + 2] >> 3) << 10)
                        | (flipped[o + 3] >= 0x80 ? 0x8000 : 0);
                    body[i * 2] = (byte)v;
                    body[i * 2 + 1] = (byte)(v >> 8);
                }
            }
            else
            {
                // RGBA32: row-flip back and compress alpha 0..255 → 0..128.
                byte[] flipped = FlipRowsRgba(rgbaTopDown, w, h);
                body = new byte[expected];
                for (int i = 0; i + 3 < flipped.Length; i += 4)
                {
                    body[i + 0] = flipped[i + 0];
                    body[i + 1] = flipped[i + 1];
                    body[i + 2] = flipped[i + 2];
                    body[i + 3] = PngAlphaToPs2Alpha(flipped[i + 3]);
                }
            }

            using var ms = new MemoryStream();
            ms.Write(template.RawHeader, 0, HeaderSize);
            ms.Write(body, 0, body.Length);
            ms.Write(template.RawFooter, 0, template.RawFooter.Length);
            return ms.ToArray();
        }

        /// <summary>
        /// Top-level entrypoint: takes an arbitrary RGBA image and the template
        /// it should match, bilinear-resamples to the template's dimensions and
        /// encodes through <see cref="Encode"/>. The returned bytes are a
        /// drop-in replacement for the template TIM record.
        /// </summary>
        public static byte[] ResampleToTemplate(
            byte[] srcRgba, int srcWidth, int srcHeight,
            Header template,
            byte[]? paletteRgba,
            out byte[]? generatedPalette)
        {
            if (srcRgba.Length != srcWidth * srcHeight * 4)
                throw new ArgumentException("Source pixel buffer size does not match width*height*4.", nameof(srcRgba));

            byte[] resampled = (srcWidth == template.Width && srcHeight == template.Height)
                ? srcRgba
                : BilinearResample(srcRgba, srcWidth, srcHeight, template.Width, template.Height);

            return Encode(template, resampled, paletteRgba, out generatedPalette);
        }

        // -------------------------------------------------------------------
        // CLUT decoders
        // -------------------------------------------------------------------

        private static byte[] Rgba5551ToRgba8888(byte[] clut, int offset, int length)
        {
            var outBuf = new byte[256 * 4];
            bool anyStp = false;
            for (int i = 0; i < 256 && !anyStp; i++)
                anyStp = (BitConverter.ToUInt16(clut, offset + i * 2) & 0x8000) != 0;
            for (int i = 0; i < 256; i++)
            {
                ushort v = BitConverter.ToUInt16(clut, offset + i * 2);
                byte r = Expand5To8(v & 0x1f);
                byte g = Expand5To8((v >> 5) & 0x1f);
                byte b = Expand5To8((v >> 10) & 0x1f);
                bool hasStp = (v & 0x8000) != 0;
                outBuf[i * 4 + 0] = r;
                outBuf[i * 4 + 1] = g;
                outBuf[i * 4 + 2] = b;
                // Bit 15 goes through TEXA/AEM in the GS. Palettes without any
                // STP bit are opaque apart from all-black entries; palettes
                // that use the bit keep coloured entries visible and only
                // drop black entries without it.
                bool coloured = r != 0 || g != 0 || b != 0;
                outBuf[i * 4 + 3] = (byte)((coloured || (anyStp && hasStp)) ? 255 : 0);
            }
            return outBuf;
        }

        // 32-bit CLUT entries keep their real alpha (GS 0..0x80): foliage, grass
        // and tree facades cut their silhouettes from it, terrain blend layers
        // and shadow decals fade by it. Whether alpha is used at all is the
        // renderer's call, from the GS registers (matches zed-texture-parser.ts).
        private static byte[] Rgba32PaletteToRgba8888(byte[] clut, int offset, int length)
        {
            var outBuf = new byte[256 * 4];
            for (int i = 0; i < 256; i++)
            {
                int p = offset + i * 4;
                outBuf[i * 4 + 0] = clut[p + 0];
                outBuf[i * 4 + 1] = clut[p + 1];
                outBuf[i * 4 + 2] = clut[p + 2];
                outBuf[i * 4 + 3] = Ps2AlphaToPngAlpha(clut[p + 3]);
            }
            return outBuf;
        }

        private static byte[] UnswizzlePs2Clut8(byte[] rgba)
        {
            var outBuf = new byte[256 * 4];
            for (int i = 0; i < 256; i++)
            {
                int j = (i & 0xe7) | ((i & 0x08) << 1) | ((i & 0x10) >> 1);
                Buffer.BlockCopy(rgba, j * 4, outBuf, i * 4, 4);
            }
            return outBuf;
        }

        // -------------------------------------------------------------------
        // Pixel helpers
        // -------------------------------------------------------------------

        private static byte Expand5To8(int v) => (byte)(((v << 3) | (v >> 2)) & 0xff);

        private static uint ReadU32(byte[] bytes, int o) => BitConverter.ToUInt32(bytes, o);

        private static byte Ps2AlphaToPngAlpha(byte v) =>
            v <= 128 ? (byte)Math.Min(255, v * 2) : v;

        private static byte PngAlphaToPs2Alpha(byte v) =>
            // Inverse of Ps2AlphaToPngAlpha for the 0..128 range. Rounded
            // halving so even PNG values map back to their original PS2
            // alpha exactly (e.g. PNG 200 ↔ PS2 100, PNG 254 ↔ PS2 127).
            (byte)((v + 1) >> 1);

        private static byte[] FlipRowsRgba(byte[] src, int width, int height)
        {
            int stride = width * 4;
            var outBuf = new byte[stride * height];
            for (int y = 0; y < height; y++)
            {
                int srcRow = (height - 1 - y) * stride;
                Buffer.BlockCopy(src, srcRow, outBuf, y * stride, stride);
            }
            return outBuf;
        }

        // -------------------------------------------------------------------
        // Bilinear resample
        // -------------------------------------------------------------------

        private static byte[] BilinearResample(byte[] src, int sw, int sh, int dw, int dh)
        {
            var dst = new byte[dw * dh * 4];
            double fx = (double)sw / dw;
            double fy = (double)sh / dh;

            for (int y = 0; y < dh; y++)
            {
                double sy = (y + 0.5) * fy - 0.5;
                int y0 = Math.Max(0, (int)Math.Floor(sy));
                int y1 = Math.Min(sh - 1, y0 + 1);
                double wy = Math.Clamp(sy - y0, 0.0, 1.0);

                for (int x = 0; x < dw; x++)
                {
                    double sx = (x + 0.5) * fx - 0.5;
                    int x0 = Math.Max(0, (int)Math.Floor(sx));
                    int x1 = Math.Min(sw - 1, x0 + 1);
                    double wx = Math.Clamp(sx - x0, 0.0, 1.0);

                    int i00 = (y0 * sw + x0) * 4;
                    int i01 = (y0 * sw + x1) * 4;
                    int i10 = (y1 * sw + x0) * 4;
                    int i11 = (y1 * sw + x1) * 4;
                    int o = (y * dw + x) * 4;

                    for (int c = 0; c < 4; c++)
                    {
                        double top = src[i00 + c] * (1 - wx) + src[i01 + c] * wx;
                        double bot = src[i10 + c] * (1 - wx) + src[i11 + c] * wx;
                        dst[o + c] = (byte)Math.Clamp((int)Math.Round(top * (1 - wy) + bot * wy), 0, 255);
                    }
                }
            }
            return dst;
        }

        // -------------------------------------------------------------------
        // Median-cut palette generation + nearest-neighbor indexing
        // -------------------------------------------------------------------

        // Builds an up-to-256-entry palette via median cut on the RGBA pixels,
        // unswizzles it to match PS2 CLUT ordering, and returns a 1024-byte
        // (256 entries, RGBA8888) palette buffer.
        /// <summary>RGBA (top-down) to 8-bit indices in the TIF's bottom-up row order plus the 256-entry logical
        /// RGBA8888 palette they index (median cut); <see cref="EncodeClutBuffer"/> turns the palette into a PAL buf.</summary>
        public static byte[] Quantize8(byte[] rgbaTopDown, int width, int height, out byte[] logicalPalette)
        {
            logicalPalette = MedianCutQuantize(rgbaTopDown, 256, out _);
            return QuantizeWithPalette(FlipRowsRgba(rgbaTopDown, width, height), logicalPalette);
        }

        public static byte[] MedianCutQuantize(byte[] rgbaTopDown, int maxColors, out byte[] paletteOut)
        {
            int pixelCount = rgbaTopDown.Length / 4;
            var box = new int[pixelCount];
            for (int i = 0; i < pixelCount; i++) box[i] = i;

            var boxes = new List<int[]> { box };
            while (boxes.Count < maxColors)
            {
                int idx = PickBoxToSplit(boxes, rgbaTopDown);
                if (idx < 0) break;
                int[] cur = boxes[idx];
                boxes.RemoveAt(idx);
                (int[] a, int[] b) = SplitBox(cur, rgbaTopDown);
                if (a.Length == 0 || b.Length == 0)
                {
                    // Can't split further — put it back and stop.
                    boxes.Add(cur);
                    break;
                }
                boxes.Add(a);
                boxes.Add(b);
            }

            var palette = new byte[256 * 4];
            for (int i = 0; i < boxes.Count && i < 256; i++)
            {
                int[] entries = boxes[i];
                long r = 0, g = 0, b = 0, a = 0;
                foreach (int p in entries)
                {
                    int o = p * 4;
                    r += rgbaTopDown[o + 0];
                    g += rgbaTopDown[o + 1];
                    b += rgbaTopDown[o + 2];
                    a += rgbaTopDown[o + 3];
                }
                int n = entries.Length;
                palette[i * 4 + 0] = (byte)(r / n);
                palette[i * 4 + 1] = (byte)(g / n);
                palette[i * 4 + 2] = (byte)(b / n);
                palette[i * 4 + 3] = (byte)(a / n);
            }

            paletteOut = palette;
            return palette;
        }

        private static int PickBoxToSplit(List<int[]> boxes, byte[] pixels)
        {
            int best = -1;
            long bestVol = -1;
            for (int i = 0; i < boxes.Count; i++)
            {
                int[] b = boxes[i];
                if (b.Length < 2) continue;
                BoxRange(b, pixels, out int rr, out int gr, out int br);
                long vol = (long)rr * gr * br;
                if (vol > bestVol) { bestVol = vol; best = i; }
            }
            return best;
        }

        private static (int[], int[]) SplitBox(int[] box, byte[] pixels)
        {
            BoxRange(box, pixels, out int rr, out int gr, out int br);
            int axis = rr >= gr && rr >= br ? 0 : (gr >= br ? 1 : 2);
            Array.Sort(box, (x, y) => pixels[x * 4 + axis].CompareTo(pixels[y * 4 + axis]));
            int mid = box.Length / 2;
            var left = new int[mid];
            var right = new int[box.Length - mid];
            Array.Copy(box, 0, left, 0, mid);
            Array.Copy(box, mid, right, 0, right.Length);
            return (left, right);
        }

        private static void BoxRange(int[] box, byte[] pixels, out int rr, out int gr, out int br)
        {
            int minR = 255, maxR = 0, minG = 255, maxG = 0, minB = 255, maxB = 0;
            foreach (int p in box)
            {
                int o = p * 4;
                byte r = pixels[o + 0], g = pixels[o + 1], b = pixels[o + 2];
                if (r < minR) minR = r; if (r > maxR) maxR = r;
                if (g < minG) minG = g; if (g > maxG) maxG = g;
                if (b < minB) minB = b; if (b > maxB) maxB = b;
            }
            rr = maxR - minR;
            gr = maxG - minG;
            br = maxB - minB;
        }

        private static byte[] QuantizeWithPalette(byte[] rgbaBottomUp, byte[] palette)
        {
            int pixelCount = rgbaBottomUp.Length / 4;
            var outBuf = new byte[pixelCount];
            for (int i = 0; i < pixelCount; i++)
            {
                int o = i * 4;
                outBuf[i] = (byte)NearestPaletteIndex(palette, rgbaBottomUp[o], rgbaBottomUp[o + 1], rgbaBottomUp[o + 2]);
            }
            return outBuf;
        }

        private static int NearestPaletteIndex(byte[] palette, byte r, byte g, byte b)
        {
            int best = 0;
            int bestDist = int.MaxValue;
            for (int i = 0; i < 256; i++)
            {
                int o = i * 4;
                int dr = palette[o + 0] - r;
                int dg = palette[o + 1] - g;
                int db = palette[o + 2] - b;
                int d = dr * dr + dg * dg + db * db;
                if (d < bestDist) { bestDist = d; best = i; if (d == 0) break; }
            }
            return best;
        }
    }
}
