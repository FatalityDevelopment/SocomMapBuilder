using System.Numerics;
using CodeDesigner.Ps2.FileFormats.Zipper.Texture;
using CodeDesigner.Ps2.FileFormats.Zipper.ZED;

namespace CodeDesigner.Ps2.FileFormats.Zipper.Package
{
    /// <summary>
    /// New textures and palettes from images. A new texture is an 8-bit indexed texdat built on one of the package's
    /// own (its header and GS register tail as the template, mipmapping off): new size, pixels, texture index
    /// (header +0x08 = TEX0.TBP0, the next of its TXR) and palette (TEX0.CBP). Its palette is made from the image
    /// (256 colours, median cut) under an id no palette of the package uses, or an existing palette is reused and
    /// the image matched to it. Palettes are 32-bit (alpha 0..0x80) unless the image is opaque and the template
    /// uses 16-bit A1B5G5R5 ones; a 32-bit palette needs header +0x0F = 0x13 and CPSM 0, a 16-bit one 0x12 / 2.
    /// </summary>
    public static class PackageTextures
    {
        private const int RegTex0 = 0x06, RegTex1 = 0x14, RegMipTbp1 = 0x34, RegMipTbp2 = 0x35;
        private const int PsmT8 = 0x13, CpsmCt32 = 0, CpsmCt16 = 2;

        public sealed class Result
        {
            public Dictionary<string, byte[]> Changed { get; } = new(StringComparer.OrdinalIgnoreCase);
            public string Archive { get; set; } = string.Empty;
            public string Texture { get; set; } = string.Empty;
            public int Width { get; set; }
            public int Height { get; set; }
            public int PaletteId { get; set; }
            public bool NewPalette { get; set; }
            public bool Palette32 { get; set; }
            public List<string> Warnings { get; } = new();
        }

        /// <summary>
        /// Adds <paramref name="name"/> to the TXR of <paramref name="ns"/> (default: the map's) from an RGBA image.
        /// The size is <paramref name="width"/> × <paramref name="height"/> (powers of two, 8..512) or the image's
        /// rounded to powers of two and capped at <paramref name="maxSize"/>. <paramref name="paletteId"/> reuses
        /// that palette instead of making one.
        /// </summary>
        public static Result AddTexture(ZipperPackage pkg, string name, string? ns, byte[] rgba, int srcWidth, int srcHeight,
            int? width = null, int? height = null, int maxSize = 256, int? paletteId = null)
        {
            name = TextureName(name);
            if (PackageImport.UsedTextureNames(pkg).Contains(name)) throw new ArgumentException($"This package already has a texture named {name}");
            ns = Namespace(pkg, ns);
            var txrEntry = pkg.Archive(ns, "txr")!;
            var palEntry = pkg.Archive(ns, "pal")!;
            var txr = ZedContainer.FromBytes(txrEntry.Data);
            var pal = ZedContainer.FromBytes(palEntry.Data);

            int w = width ?? Pow2(srcWidth, maxSize), h = height ?? Pow2(srcHeight, maxSize);
            if (!BitOperations.IsPow2(w) || !BitOperations.IsPow2(h) || w < 8 || h < 8 || w > 512 || h > 512)
                throw new ArgumentException($"A texture is 8..512 pixels a side, powers of two ({w}×{h})");

            byte[] template = Template(pkg, txr) ?? throw new InvalidOperationException("The package has no 8-bit indexed texture to build on");
            var templateHeader = ZedTextureFile.ParseHeader(template);
            bool template16 = template[0x0F] == 0x12;
            var result = new Result { Archive = txrEntry.Leaf, Texture = name, Width = w, Height = h };

            byte[]? paletteRgba = null;
            bool is16;
            int id;
            if (paletteId is int reuse)
            {
                var existing = pkg.FindPalette(ns, reuse) ?? pkg.Entries.Where(e => e.Kind == "pal" && e.Namespace != null)
                    .Select(e => pkg.FindPalette(e.Namespace!, reuse)).FirstOrDefault(p => p != null)
                    ?? throw new KeyNotFoundException($"No palette texpal_{reuse}");
                paletteRgba = existing.Rgba;
                is16 = existing.Is16Bit;
                id = reuse;
            }
            else
            {
                is16 = template16 && !HasPartialAlpha(rgba);
                id = PackageImport.FreePaletteId(PackageImport.UsedPaletteIds(pkg), PackageImport.PaletteBlock(pkg));
                result.NewPalette = true;
            }
            result.PaletteId = id;
            result.Palette32 = !is16;

            // The template's header and tail at the new size: the encoder writes them verbatim around the pixels.
            int index = PackageImport.NextTextureIndex(txr);
            byte[] shell = Shell(template, templateHeader, w, h, index, id, is16);
            var header = ZedTextureFile.ParseHeader(shell);
            byte[] texdat = ZedTextureFile.ResampleToTemplate(rgba, srcWidth, srcHeight, header, paletteRgba, out byte[]? generated);

            PackageImport.AppendToList(txr, "textures", new ZedTree.Record(name, Array.Empty<byte>(), new[] { new ZedTree.Record("texdat", texdat) }), txrEntry.Leaf);
            result.Changed[txrEntry.Leaf] = txr.ToBytes();
            if (result.NewPalette)
            {
                PackageImport.AppendToList(pal, "palettes", PaletteRecord(pkg, id, generated ?? throw new InvalidOperationException("No palette was generated"), is16), palEntry.Leaf);
                result.Changed[palEntry.Leaf] = pal.ToBytes();
            }
            if (name.Length > 15) result.Warnings.Add($"{name} is longer than 15 characters: models whose texture name fields are 16 bytes cannot be pointed at it");
            return result;
        }

        /// <summary>A new palette (256 colours) from an image's colours, in the PAL of <paramref name="ns"/>; answers its id.</summary>
        public static (Dictionary<string, byte[]> Changed, int PaletteId, string Archive) AddPalette(ZipperPackage pkg, string? ns, byte[] rgba, bool? is16 = null)
        {
            ns = Namespace(pkg, ns);
            var palEntry = pkg.Archive(ns, "pal")!;
            var pal = ZedContainer.FromBytes(palEntry.Data);
            ZedTextureFile.MedianCutQuantize(rgba, 256, out byte[] palette);
            bool sixteen = is16 ?? !HasPartialAlpha(rgba);
            int id = PackageImport.FreePaletteId(PackageImport.UsedPaletteIds(pkg), PackageImport.PaletteBlock(pkg));
            PackageImport.AppendToList(pal, "palettes", PaletteRecord(pkg, id, palette, sixteen), palEntry.Leaf);
            return (new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase) { [palEntry.Leaf] = pal.ToBytes() }, id, palEntry.Leaf);
        }

        // ------------------------------------------------------------------ helpers

        private static string TextureName(string name)
        {
            name = name.Trim();
            if (!name.EndsWith(".tif", StringComparison.OrdinalIgnoreCase)) name += ".tif";
            if (name.Length > 31 || name.Length < 5 || name.Any(ch => ch < 0x21 || ch > 0x7E || ch is '\\' or '/' or ':'))
                throw new ArgumentException($"'{name}' is not a texture name (ASCII, no spaces or slashes, at most 31 characters)");
            return name;
        }

        private static string Namespace(ZipperPackage pkg, string? ns)
        {
            string chosen = !string.IsNullOrWhiteSpace(ns) ? ns.Trim().ToLowerInvariant()
                : pkg.MapNamespace ?? pkg.Entries.FirstOrDefault(e => e.Kind == "txr" && e.Namespace != null && pkg.HasArchive(e.Namespace, "pal"))?.Namespace
                ?? throw new ArgumentException("Pick the texture archive (namespace) to add to");
            if (!pkg.HasArchive(chosen, "txr") || !pkg.HasArchive(chosen, "pal")) throw new ArgumentException($"This package has no {chosen.ToUpperInvariant()}_TXR.ZED / _PAL.ZED pair");
            return chosen;
        }

        private static int Pow2(int size, int max)
        {
            int p = 8;
            while (p < size && p < max) p <<= 1;
            // Round to the nearer power: 200 → 256, 140 → 128.
            if (p > size && p / 2 >= 8 && size - p / 2 < p - size) p /= 2;
            return Math.Min(p, max);
        }

        private static bool HasPartialAlpha(byte[] rgba)
        {
            for (int i = 3; i < rgba.Length; i += 4)
                if (rgba[i] != 0 && rgba[i] != 255) return true;
            return false;
        }

        /// <summary>An 8-bit indexed texdat to build on: one without mip levels from this TXR first, else any TXR.</summary>
        private static byte[]? Template(ZipperPackage pkg, ZedContainer own)
        {
            IEnumerable<ZedContainer> archives = new[] { own }.Concat(pkg.Entries.Where(e => e.Kind == "txr").Select(e => pkg.Container(e.Leaf)));
            byte[]? fallback = null;
            foreach (var c in archives)
                foreach (int i in PackageImport.TextureNodes(c))
                {
                    byte[] d = c.Nodes[i].Data;
                    ZedTextureFile.Header h;
                    try { h = ZedTextureFile.ParseHeader(d); } catch (InvalidDataException) { continue; }
                    if (!h.IsIndexed || h.Bpp != 8 || h.Gs == null || d[0x0F] is not (0x12 or 0x13)) continue;
                    bool mips = PackageImport.Registers(d).Any(r => (r.Reg is RegMipTbp1 or RegMipTbp2 && r.Lo != 0) || (r.Reg == RegTex1 && ((r.Lo >> 2) & 7) != 0));
                    if (!mips) return d;
                    fallback ??= d;
                }
            return fallback;
        }

        /// <summary>The template resized to w × h (blank pixels) with the new index and palette in its registers.</summary>
        private static byte[] Shell(byte[] template, ZedTextureFile.Header th, int w, int h, int index, int paletteId, bool is16)
        {
            byte[] footer = th.RawFooter;
            byte[] data = new byte[ZedTextureFile.HeaderSize + w * h + footer.Length];
            Array.Copy(template, data, ZedTextureFile.HeaderSize);
            BitConverter.GetBytes((ushort)w).CopyTo(data, 0);
            BitConverter.GetBytes((ushort)h).CopyTo(data, 2);
            BitConverter.GetBytes((uint)(w * h)).CopyTo(data, 4);
            BitConverter.GetBytes((uint)index).CopyTo(data, 8);
            data[0x0F] = (byte)(is16 ? 0x12 : 0x13);
            int tail = ZedTextureFile.HeaderSize + w * h;
            footer.CopyTo(data, tail);

            int log2W = BitOperations.Log2((uint)w), log2H = BitOperations.Log2((uint)h);
            for (int o = tail; o + 16 <= data.Length; o += 16)
            {
                uint reg = BitConverter.ToUInt32(data, o + 8);
                if (BitConverter.ToUInt32(data, o + 12) != 0 || reg > 0x7F) continue;
                ulong v = BitConverter.ToUInt64(data, o);
                switch ((int)reg)
                {
                    case RegTex0:
                        v &= ~(0x3FFFUL | (0x3FUL << 14) | (0x3FUL << 20) | (0xFUL << 26) | (0xFUL << 30) | (0x3FFFUL << 37) | (0xFUL << 51));
                        v |= (ulong)index | ((ulong)Math.Max(1, w / 64) << 14) | ((ulong)PsmT8 << 20) | ((ulong)log2W << 26) | ((ulong)log2H << 30)
                            | ((ulong)paletteId << 37) | ((ulong)(is16 ? CpsmCt16 : CpsmCt32) << 51);
                        break;
                    case RegTex1:
                        v &= ~(7UL << 2); // MXL 0: no mip levels
                        break;
                    case RegMipTbp1:
                    case RegMipTbp2:
                        v = 0;
                        break;
                    default:
                        continue;
                }
                BitConverter.GetBytes(v).CopyTo(data, o);
            }
            return data;
        }

        /// <summary>texpal_&lt;id&gt; { par (id, type), buf }: the type word as the package's palettes of that depth carry it.</summary>
        private static ZedTree.Record PaletteRecord(ZipperPackage pkg, int id, byte[] logicalRgba, bool is16)
        {
            if (is16)
            {
                // A1B5G5R5 has no alpha but the STP bit: the GS (TEXA) reads a black entry without STP as clear, any
                // other as opaque. Clear entries are written black so cut-outs stay cut out.
                logicalRgba = (byte[])logicalRgba.Clone();
                for (int i = 0; i < 256; i++)
                    if (logicalRgba[i * 4 + 3] < 0x80) { logicalRgba[i * 4] = logicalRgba[i * 4 + 1] = logicalRgba[i * 4 + 2] = 0; }
            }
            byte[] buf = ZedTextureFile.EncodeClutBuffer(logicalRgba, is16);
            uint type = is16 ? 0x00020200u : 0x400u;
            foreach (var e in pkg.Entries.Where(e => e.Kind == "pal"))
            {
                var nodes = pkg.Container(e.Leaf).Nodes;
                for (int i = 0; i + 2 < nodes.Count; i++)
                    if (nodes[i].Name == "par" && nodes[i].Data.Length >= 8 && nodes[i + 1].Name == "buf" && nodes[i + 1].Data.Length == buf.Length)
                    {
                        type = BitConverter.ToUInt32(nodes[i].Data, 4);
                        goto found;
                    }
            }
            found:
            byte[] par = new byte[8];
            BitConverter.GetBytes((uint)id).CopyTo(par, 0);
            BitConverter.GetBytes(type).CopyTo(par, 4);
            return new ZedTree.Record("texpal_" + id, Array.Empty<byte>(), new[] { new ZedTree.Record("par", par), new ZedTree.Record("buf", buf) });
        }
    }
}
