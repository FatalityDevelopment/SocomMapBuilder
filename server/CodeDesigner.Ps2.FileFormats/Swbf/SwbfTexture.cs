namespace CodeDesigner.Ps2.FileFormats.Swbf
{
    /// <summary>
    /// A Battlefront PS2 texture (a "tex_" chunk): NAME, INFO {u16 width, u16 height, u16 bits per pixel (4, 8 or 32),
    /// ..., u16 mip count at 12}, for 4 and 8 bits a "pal_" {INFO {u16 colours}, BODY}, then one BODY per mip level.
    /// Bodies are run-length packed: a control byte with the high bit set repeats the next element (low seven bits + 1)
    /// times, otherwise the next (control + 1) elements are literal. An element is one byte of indices (two 4-bit
    /// pixels, low nibble first) or one 4-byte colour stored A, B, G, R with full alpha 0xFF. Pixels are linear, rows
    /// top down.
    /// </summary>
    public sealed class SwbfTexture
    {
        public required string Name { get; init; }
        public required int Width { get; init; }
        public required int Height { get; init; }
        public required int Depth { get; init; }
        public int Mips { get; init; }
        /// <summary>Top mip level as RGBA8, rows top down.</summary>
        public required byte[] Rgba { get; init; }
        public bool HasAlpha { get; init; }

        public static SwbfTexture Decode(Ucfb tex)
        {
            string name = tex.ChildText("NAME") ?? "";
            var info = tex.Child("INFO") ?? throw new InvalidDataException($"Texture {name} has no INFO");
            int w = info.U16(0), h = info.U16(2), bpp = info.U16(4);
            int mips = info.Size >= 14 ? info.U16(12) : 1;
            if (w is <= 0 or > 4096 || h is <= 0 or > 4096) throw new InvalidDataException($"Texture {name} is {w}x{h}");
            var body = tex.Children.FirstOrDefault(c => c.Tag == "BODY");
            if (body.Data == null) throw new InvalidDataException($"Texture {name} has no pixels");
            var rgba = new byte[w * h * 4];
            switch (bpp)
            {
                case 4:
                case 8:
                {
                    int colours = bpp == 4 ? 16 : 256;
                    byte[] pal = new byte[colours * 4];
                    if (tex.Child("pal_") is { } p && p.Child("BODY") is { } pb)
                    {
                        int n = p.Child("INFO") is { } pi ? Math.Clamp(pi.U16(0), 1, colours) : colours;
                        pal = Unpack(pb.Bytes, 4, n * 4);
                        if (pal.Length < colours * 4) Array.Resize(ref pal, colours * 4);
                    }
                    byte[] idx = Unpack(body.Bytes, 1, bpp == 4 ? (w * h + 1) / 2 : w * h);
                    for (int i = 0; i < w * h; i++)
                    {
                        int k = bpp == 8 ? (i < idx.Length ? idx[i] : 0) : (i / 2 < idx.Length ? (i % 2 == 0 ? idx[i / 2] & 0xF : idx[i / 2] >> 4) : 0);
                        Abgr(pal, k * 4, rgba, i * 4);
                    }
                    break;
                }
                case 32:
                {
                    byte[] px = Unpack(body.Bytes, 4, w * h * 4);
                    for (int i = 0; i < w * h && i * 4 + 3 < px.Length; i++) Abgr(px, i * 4, rgba, i * 4);
                    break;
                }
                default:
                    throw new InvalidDataException($"Texture {name} has {bpp} bits per pixel");
            }
            bool alpha = false;
            for (int i = 3; i < rgba.Length; i += 4) if (rgba[i] < 0xF0) { alpha = true; break; }
            return new SwbfTexture { Name = name, Width = w, Height = h, Depth = bpp, Mips = mips, Rgba = rgba, HasAlpha = alpha };
        }

        public static (int Width, int Height, int Depth) Info(Ucfb tex)
        {
            var info = tex.Child("INFO");
            return info is { } i ? (i.U16(0), i.U16(2), i.U16(4)) : (0, 0, 0);
        }

        private static void Abgr(byte[] src, int at, byte[] dst, int to)
        {
            dst[to] = src[at + 3];
            dst[to + 1] = src[at + 2];
            dst[to + 2] = src[at + 1];
            dst[to + 3] = src[at];
        }

        /// <summary>Unpacks a run-length body of <paramref name="unit"/>-byte elements to at most <paramref name="bytes"/> bytes.</summary>
        public static byte[] Unpack(ReadOnlySpan<byte> src, int unit, int bytes)
        {
            var dst = new byte[bytes];
            int i = 0, o = 0;
            while (o < bytes && i < src.Length)
            {
                int c = src[i++];
                if ((c & 0x80) != 0)
                {
                    int n = (c & 0x7F) + 1;
                    if (i + unit > src.Length) break;
                    var e = src.Slice(i, unit);
                    i += unit;
                    for (int k = 0; k < n && o + unit <= bytes; k++, o += unit) e.CopyTo(dst.AsSpan(o));
                }
                else
                {
                    int n = Math.Min((c + 1) * unit, Math.Min(src.Length - i, bytes - o));
                    src.Slice(i, n).CopyTo(dst.AsSpan(o));
                    i += n;
                    o += n;
                }
            }
            return dst;
        }
    }
}
