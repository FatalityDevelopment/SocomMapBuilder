namespace CodeDesigner.Ps2.FileFormats.Gta
{
    /// <summary>
    /// GS local memory addressing (the swizzle). PS2 games upload 4 / 8-bit textures as 16 / 32-bit images and the
    /// GS reads them back in the texture's own format; decoding replays that: write the upload with PSMCT32 / PSMCT16
    /// addressing, read texels with PSMT8 / PSMT4 addressing. Block and column tables as the GS manual / PCSX2.
    /// </summary>
    public static class GsMemory
    {
        private static readonly int[,] Block32 = { { 0, 1, 4, 5, 16, 17, 20, 21 }, { 2, 3, 6, 7, 18, 19, 22, 23 }, { 8, 9, 12, 13, 24, 25, 28, 29 }, { 10, 11, 14, 15, 26, 27, 30, 31 } };
        private static readonly int[,] Block16 = { { 0, 2, 8, 10 }, { 1, 3, 9, 11 }, { 4, 6, 12, 14 }, { 5, 7, 13, 15 }, { 16, 18, 24, 26 }, { 17, 19, 25, 27 }, { 20, 22, 28, 30 }, { 21, 23, 29, 31 } };
        private static readonly int[,] Block4 = Block16;
        private static readonly int[,] Column32 =
        {
            { 0, 1, 4, 5, 8, 9, 12, 13 }, { 2, 3, 6, 7, 10, 11, 14, 15 }, { 16, 17, 20, 21, 24, 25, 28, 29 }, { 18, 19, 22, 23, 26, 27, 30, 31 },
            { 32, 33, 36, 37, 40, 41, 44, 45 }, { 34, 35, 38, 39, 42, 43, 46, 47 }, { 48, 49, 52, 53, 56, 57, 60, 61 }, { 50, 51, 54, 55, 58, 59, 62, 63 },
        };
        private static readonly int[,] Column16 =
        {
            { 0, 2, 8, 10, 16, 18, 24, 26, 1, 3, 9, 11, 17, 19, 25, 27 }, { 4, 6, 12, 14, 20, 22, 28, 30, 5, 7, 13, 15, 21, 23, 29, 31 },
            { 32, 34, 40, 42, 48, 50, 56, 58, 33, 35, 41, 43, 49, 51, 57, 59 }, { 36, 38, 44, 46, 52, 54, 60, 62, 37, 39, 45, 47, 53, 55, 61, 63 },
            { 64, 66, 72, 74, 80, 82, 88, 90, 65, 67, 73, 75, 81, 83, 89, 91 }, { 68, 70, 76, 78, 84, 86, 92, 94, 69, 71, 77, 79, 85, 87, 93, 95 },
            { 96, 98, 104, 106, 112, 114, 120, 122, 97, 99, 105, 107, 113, 115, 121, 123 }, { 100, 102, 108, 110, 116, 118, 124, 126, 101, 103, 109, 111, 117, 119, 125, 127 },
        };
        private static readonly int[,] Column8 = BuildColumn8();
        private static readonly int[,] Column4 = BuildColumn4();

        private static int[,] BuildColumn8()
        {
            int[][] a =
            {
                new[] { 0, 4, 16, 20, 32, 36, 48, 52, 2, 6, 18, 22, 34, 38, 50, 54 }, new[] { 8, 12, 24, 28, 40, 44, 56, 60, 10, 14, 26, 30, 42, 46, 58, 62 },
                new[] { 33, 37, 49, 53, 1, 5, 17, 21, 35, 39, 51, 55, 3, 7, 19, 23 }, new[] { 41, 45, 57, 61, 9, 13, 25, 29, 43, 47, 59, 63, 11, 15, 27, 31 },
                new[] { 96, 100, 112, 116, 64, 68, 80, 84, 98, 102, 114, 118, 66, 70, 82, 86 }, new[] { 104, 108, 120, 124, 72, 76, 88, 92, 106, 110, 122, 126, 74, 78, 90, 94 },
                new[] { 65, 69, 81, 85, 97, 101, 113, 117, 67, 71, 83, 87, 99, 103, 115, 119 }, new[] { 73, 77, 89, 93, 105, 109, 121, 125, 75, 79, 91, 95, 107, 111, 123, 127 },
            };
            var t = new int[16, 16];
            for (int r = 0; r < 16; r++) for (int c = 0; c < 16; c++) t[r, c] = a[r % 8][c] + (r >= 8 ? 128 : 0);
            return t;
        }

        private static int[,] BuildColumn4()
        {
            int[][] a =
            {
                new[] { 0, 8, 32, 40, 64, 72, 96, 104, 2, 10, 34, 42, 66, 74, 98, 106, 4, 12, 36, 44, 68, 76, 100, 108, 6, 14, 38, 46, 70, 78, 102, 110 },
                new[] { 16, 24, 48, 56, 80, 88, 112, 120, 18, 26, 50, 58, 82, 90, 114, 122, 20, 28, 52, 60, 84, 92, 116, 124, 22, 30, 54, 62, 86, 94, 118, 126 },
                new[] { 65, 73, 97, 105, 1, 9, 33, 41, 67, 75, 99, 107, 3, 11, 35, 43, 69, 77, 101, 109, 5, 13, 37, 45, 71, 79, 103, 111, 7, 15, 39, 47 },
                new[] { 81, 89, 113, 121, 17, 25, 49, 57, 83, 91, 115, 123, 19, 27, 51, 59, 85, 93, 117, 125, 21, 29, 53, 61, 87, 95, 119, 127, 23, 31, 55, 63 },
                new[] { 192, 200, 224, 232, 128, 136, 160, 168, 194, 202, 226, 234, 130, 138, 162, 170, 196, 204, 228, 236, 132, 140, 164, 172, 198, 206, 230, 238, 134, 142, 166, 174 },
                new[] { 208, 216, 240, 248, 144, 152, 176, 184, 210, 218, 242, 250, 146, 154, 178, 186, 212, 220, 244, 252, 148, 156, 180, 188, 214, 222, 246, 254, 150, 158, 182, 190 },
                new[] { 129, 137, 161, 169, 193, 201, 225, 233, 131, 139, 163, 171, 195, 203, 227, 235, 133, 141, 165, 173, 197, 205, 229, 237, 135, 143, 167, 175, 199, 207, 231, 239 },
                new[] { 145, 153, 177, 185, 209, 217, 241, 249, 147, 155, 179, 187, 211, 219, 243, 251, 149, 157, 181, 189, 213, 221, 245, 253, 151, 159, 183, 191, 215, 223, 247, 255 },
            };
            var t = new int[16, 32];
            for (int r = 0; r < 16; r++) for (int c = 0; c < 32; c++) t[r, c] = a[r % 8][c] + (r >= 8 ? 256 : 0);
            return t;
        }

        // Addresses: PSMCT32 in words, PSMCT16 in halfwords, PSMT8 in bytes, PSMT4 in nibbles; bp in 256-byte blocks,
        // bw in 64-pixel units.
        public static long Addr32(int x, int y, int bp, int bw) => bp * 64L + ((y >> 5) * bw + (x >> 6)) * 2048L + Block32[(y & 31) >> 3, (x & 63) >> 3] * 64 + Column32[y & 7, x & 7];
        public static long Addr16(int x, int y, int bp, int bw) => bp * 128L + ((y >> 6) * bw + (x >> 6)) * 4096L + Block16[(y & 63) >> 3, (x & 63) >> 4] * 128 + Column16[y & 7, x & 15];
        public static long Addr8(int x, int y, int bp, int bw) => bp * 256L + ((y >> 6) * Math.Max(1, bw / 2) + (x >> 7)) * 8192L + Block32[(y & 63) >> 4, (x & 127) >> 4] * 256 + Column8[y & 15, x & 15];
        public static long Addr4(int x, int y, int bp, int bw) => bp * 512L + ((y >> 7) * Math.Max(1, bw / 2) + (x >> 7)) * 16384L + Block4[(y & 127) >> 4, (x & 127) >> 5] * 512 + Column4[y & 15, x & 31];

        /// <summary>Palette indices of a 4 / 8-bit texture uploaded as a 16 / 32-bit image.</summary>
        public static byte[] Unswizzle(byte[] upload, int uploadPsm, int rrw, int rrh, int psm, int width, int height, int tbw)
        {
            int dbw = Math.Max(1, tbw / 2);
            long size = Math.Max((long)upload.Length * 4, (long)width * height * 2) + 65536;
            var mem = new byte[size];
            if (uploadPsm == 0x02)
            {
                for (int y = 0, i = 0; y < rrh; y++)
                    for (int x = 0; x < rrw && 2 * i + 2 <= upload.Length; x++, i++)
                    {
                        long a = Addr16(x, y, 0, dbw) * 2;
                        if (a + 2 <= mem.Length) { mem[a] = upload[2 * i]; mem[a + 1] = upload[2 * i + 1]; }
                    }
            }
            else
            {
                for (int y = 0, i = 0; y < rrh; y++)
                    for (int x = 0; x < rrw && 4 * i + 4 <= upload.Length; x++, i++)
                    {
                        long a = Addr32(x, y, 0, dbw) * 4;
                        if (a + 4 <= mem.Length) Buffer.BlockCopy(upload, 4 * i, mem, (int)a, 4);
                    }
            }
            var idx = new byte[width * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    if (psm == 0x13) { long a = Addr8(x, y, 0, tbw); idx[y * width + x] = a < mem.Length ? mem[a] : (byte)0; }
                    else { long n = Addr4(x, y, 0, tbw); byte b = (n >> 1) < mem.Length ? mem[n >> 1] : (byte)0; idx[y * width + x] = (byte)((n & 1) != 0 ? b >> 4 : b & 15); }
                }
            return idx;
        }
    }

    /// <summary>
    /// A RenderWare texture dictionary (.txd) with PS2 native rasters (GTA: San Andreas): per texture a name, and a
    /// raster = 64-byte header (width, height, depth, raster format, TEX0, ...) then the pixel data and the palette,
    /// each as GIF packets (A+D TRXPOS / TRXREG / TRXDIR [/ BITBLTBUF], then IMAGE). 8-bit pixels are uploaded as
    /// PSMCT32, 4-bit as PSMCT16 (<see cref="GsMemory"/> unswizzles); 256-colour palettes in CSM1 order; alpha 0x80 = 1.
    /// </summary>
    public sealed class RwTexDictionary
    {
        public sealed record Texture(string Name, int Width, int Height, int Depth, byte[] Rgba, bool HasAlpha);

        private readonly byte[] _data;
        private readonly Dictionary<string, (int Raster, int Size, string Name)> _rasters = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Texture?> _decoded = new(StringComparer.OrdinalIgnoreCase);

        public IEnumerable<string> Names => _rasters.Values.Select(r => r.Name);

        public RwTexDictionary(byte[] d)
        {
            _data = d;
            var dict = Rw.Find(d, 0, d.Length, Rw.TexDictionary) ?? throw new InvalidDataException("Not a texture dictionary");
            foreach (var t in Rw.Chunks(d, dict.Data, dict.Data + dict.Size).Where(c => c.Type == Rw.TextureNative))
            {
                var parts = Rw.Chunks(d, t.Data, t.Data + t.Size).ToList();
                if (parts.Count < 4) continue;
                string name = Rw.ReadString(d, parts[1].Data, parts[1].Size);
                _rasters.TryAdd(name, (parts[3].Data, parts[3].Size, name));
            }
        }

        public Texture? Get(string name)
        {
            if (_decoded.TryGetValue(name, out var hit)) return hit;
            Texture? t = null;
            if (_rasters.TryGetValue(name, out var r))
                try { t = Decode(r.Name, r.Raster, r.Size); } catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException or InvalidDataException) { t = null; }
            return _decoded[name] = t;
        }

        private sealed record Gif(Dictionary<ulong, ulong> Regs, byte[] Image);

        private static List<Gif> Packets(byte[] d, int o, int end)
        {
            var list = new List<Gif>();
            while (o + 16 <= end)
            {
                ulong lo = BitConverter.ToUInt64(d, o), hi = BitConverter.ToUInt64(d, o + 8);
                int nloop = (int)(lo & 0x7FFF), flg = (int)((lo >> 58) & 3);
                if (flg != 0 || hi != 0xE) break;
                o += 16;
                var regs = new Dictionary<ulong, ulong>();
                for (int i = 0; i < nloop && o + 16 <= end; i++, o += 16) regs[BitConverter.ToUInt64(d, o + 8)] = BitConverter.ToUInt64(d, o);
                if (o + 16 > end) break;
                int qwc = (int)(BitConverter.ToUInt64(d, o) & 0x7FFF);
                o += 16;
                int len = Math.Min(qwc * 16, end - o);
                list.Add(new Gif(regs, d[o..(o + len)]));
                o += len;
            }
            return list;
        }

        private Texture? Decode(string name, int o, int size)
        {
            var inner = Rw.Chunks(_data, o, o + size).ToList();
            if (inner.Count < 2) return null;
            int h0 = inner[0].Data;
            int width = BitConverter.ToInt32(_data, h0), height = BitConverter.ToInt32(_data, h0 + 4), depth = BitConverter.ToInt32(_data, h0 + 8);
            ulong tex0 = BitConverter.ToUInt64(_data, h0 + 16);
            int pixSize = BitConverter.ToInt32(_data, h0 + 48), palSize = BitConverter.ToInt32(_data, h0 + 52);
            int psm = (int)((tex0 >> 20) & 0x3F), tbw = (int)((tex0 >> 14) & 0x3F);
            int p = inner[1].Data;
            var pixels = Packets(_data, p, p + pixSize);
            var palette = Packets(_data, p + pixSize, p + pixSize + palSize);
            byte[] image = pixels.Count > 0 ? pixels[0].Image : _data[p..(p + Math.Min(pixSize, width * height * depth / 8))];

            uint[]? colours = null;
            if (depth <= 8 && palette.Count > 0)
            {
                var pd = palette[0].Image;
                int n = depth == 8 ? 256 : 16;
                colours = new uint[n];
                bool wide = pd.Length >= n * 4;
                for (int i = 0; i < n; i++)
                {
                    int src = n == 256 ? (i & ~0x18) | ((i & 0x08) << 1) | ((i & 0x10) >> 1) : i;
                    colours[i] = wide ? BitConverter.ToUInt32(pd, 4 * src) : Rgba16(BitConverter.ToUInt16(pd, 2 * src));
                }
            }

            var rgba = new byte[width * height * 4];
            bool alpha = false;
            void Put(int i, uint c)
            {
                byte a = (byte)Math.Min(255, (int)(c >> 24) * 2);
                rgba[4 * i] = (byte)c; rgba[4 * i + 1] = (byte)(c >> 8); rgba[4 * i + 2] = (byte)(c >> 16); rgba[4 * i + 3] = a;
                if (a < 250) alpha = true;
            }
            if (depth is 4 or 8 && colours != null)
            {
                byte[] idx;
                if (pixels.Count > 0 && pixels[0].Regs.TryGetValue(0x52, out ulong trx))
                {
                    int rrw = (int)(trx & 0xFFF), rrh = (int)((trx >> 32) & 0xFFF);
                    int upload = pixels[0].Regs.TryGetValue(0x50, out ulong bitblt) ? (int)((bitblt >> 56) & 0x3F)
                        : image.Length >= rrw * rrh * 4 ? 0x00 : 0x02;
                    bool swizzled = upload != psm;
                    idx = swizzled ? GsMemory.Unswizzle(image, upload, rrw, rrh, psm, width, height, Math.Max(1, tbw)) : Linear(image, depth, width, height);
                }
                else idx = Linear(image, depth, width, height);
                for (int i = 0; i < width * height; i++) Put(i, colours[Math.Min(idx[i], colours.Length - 1)]);
            }
            else if (depth == 32)
                for (int i = 0; i < width * height && 4 * i + 4 <= image.Length; i++) Put(i, BitConverter.ToUInt32(image, 4 * i));
            else if (depth == 16)
                for (int i = 0; i < width * height && 2 * i + 2 <= image.Length; i++) Put(i, Rgba16(BitConverter.ToUInt16(image, 2 * i)));
            else return null;
            return new Texture(name, width, height, depth, rgba, alpha);
        }

        private static byte[] Linear(byte[] image, int depth, int w, int h)
        {
            var idx = new byte[w * h];
            for (int i = 0; i < w * h; i++)
                idx[i] = depth == 8 ? (i < image.Length ? image[i] : (byte)0) : (byte)(i / 2 < image.Length ? (i % 2 == 0 ? image[i / 2] & 15 : image[i / 2] >> 4) : 0);
            return idx;
        }

        // A1B5G5R5 -> RGBA8 with PS2 alpha (0x80 = opaque).
        private static uint Rgba16(ushort c) =>
            (uint)((c & 31) * 255 / 31) | (uint)((((c >> 5) & 31) * 255 / 31) << 8) | (uint)((((c >> 10) & 31) * 255 / 31) << 16) | ((c & 0x8000) != 0 ? 0x80000000u : 0x80000000u);
    }
}
