using System;
using System.IO;
using System.IO.Compression;

namespace CodeDesigner.Ps2.FileFormats.Imaging
{
    /// <summary>
    /// Minimal RGBA8888 → PNG encoder. Pure .NET; no SkiaSharp / ImageSharp
    /// dependency. Used to return weapon-thumbnail bytes from the bridge.
    /// </summary>
    public static class PngEncoder
    {
        private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

        public static byte[] EncodeRgba(byte[] rgba, int width, int height)
        {
            if (rgba.Length != width * height * 4)
                throw new ArgumentException("RGBA buffer size does not match width*height*4.", nameof(rgba));

            using var ms = new MemoryStream();
            ms.Write(Signature, 0, Signature.Length);

            // IHDR
            var ihdr = new byte[13];
            WriteU32BE(ihdr, 0, (uint)width);
            WriteU32BE(ihdr, 4, (uint)height);
            ihdr[8] = 8;     // bit depth
            ihdr[9] = 6;     // color type: RGBA
            ihdr[10] = 0;    // compression
            ihdr[11] = 0;    // filter
            ihdr[12] = 0;    // interlace
            WriteChunk(ms, "IHDR", ihdr);

            // IDAT: filter byte 0 per scanline, then zlib-deflate the lot.
            int stride = width * 4;
            var raw = new byte[(stride + 1) * height];
            for (int y = 0; y < height; y++)
            {
                raw[y * (stride + 1)] = 0;
                Buffer.BlockCopy(rgba, y * stride, raw, y * (stride + 1) + 1, stride);
            }

            byte[] compressed = ZlibCompress(raw);
            WriteChunk(ms, "IDAT", compressed);

            WriteChunk(ms, "IEND", Array.Empty<byte>());
            return ms.ToArray();
        }

        private static byte[] ZlibCompress(byte[] data)
        {
            using var outMs = new MemoryStream();
            // zlib header for deflate, default window, no preset dict.
            outMs.WriteByte(0x78);
            outMs.WriteByte(0x9c);
            using (var deflate = new DeflateStream(outMs, CompressionLevel.Fastest, leaveOpen: true))
                deflate.Write(data, 0, data.Length);

            uint adler = Adler32(data);
            outMs.WriteByte((byte)(adler >> 24));
            outMs.WriteByte((byte)(adler >> 16));
            outMs.WriteByte((byte)(adler >> 8));
            outMs.WriteByte((byte)adler);
            return outMs.ToArray();
        }

        private static uint Adler32(byte[] data)
        {
            const uint mod = 65521;
            uint a = 1, b = 0;
            for (int i = 0; i < data.Length; i++)
            {
                a = (a + data[i]) % mod;
                b = (b + a) % mod;
            }
            return (b << 16) | a;
        }

        private static void WriteChunk(Stream s, string type, byte[] data)
        {
            var len = new byte[4];
            WriteU32BE(len, 0, (uint)data.Length);
            s.Write(len, 0, 4);

            var typeBytes = new byte[4]
            {
                (byte)type[0], (byte)type[1], (byte)type[2], (byte)type[3]
            };
            s.Write(typeBytes, 0, 4);
            s.Write(data, 0, data.Length);

            // CRC32 over type+data.
            uint crc = Crc32Init();
            crc = Crc32Update(crc, typeBytes, 0, 4);
            crc = Crc32Update(crc, data, 0, data.Length);
            crc = Crc32Finish(crc);
            var crcBytes = new byte[4];
            WriteU32BE(crcBytes, 0, crc);
            s.Write(crcBytes, 0, 4);
        }

        private static void WriteU32BE(byte[] buf, int offset, uint value)
        {
            buf[offset + 0] = (byte)(value >> 24);
            buf[offset + 1] = (byte)(value >> 16);
            buf[offset + 2] = (byte)(value >> 8);
            buf[offset + 3] = (byte)value;
        }

        // -------------------------------------------------------------------
        // CRC32 (PNG spec polynomial 0xEDB88320, init/finish XOR 0xFFFFFFFF)
        // -------------------------------------------------------------------
        private static readonly uint[] CrcTable = BuildTable();

        private static uint[] BuildTable()
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : (c >> 1);
                t[n] = c;
            }
            return t;
        }

        private static uint Crc32Init() => 0xFFFFFFFFu;

        private static uint Crc32Update(uint crc, byte[] buf, int offset, int len)
        {
            for (int i = 0; i < len; i++)
                crc = CrcTable[(crc ^ buf[offset + i]) & 0xff] ^ (crc >> 8);
            return crc;
        }

        private static uint Crc32Finish(uint crc) => crc ^ 0xFFFFFFFFu;
    }

    /// <summary>
    /// Minimal PNG decoder for RGBA8888. Used by tools that accept user image
    /// paths (PNG) and need to feed bytes into <see cref="Texture.ZedTextureFile"/>.
    /// </summary>
    public static class PngDecoder
    {
        private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

        public static byte[] DecodeRgba(byte[] png, out int width, out int height)
        {
            for (int i = 0; i < Signature.Length; i++)
                if (png[i] != Signature[i])
                    throw new InvalidDataException("Not a PNG file.");

            int o = Signature.Length;
            byte[]? idat = null;
            int idatCap = 0;
            using var idatBuf = new MemoryStream();
            byte bitDepth = 0, colorType = 0;
            width = height = 0;

            while (o < png.Length)
            {
                int len = (int)ReadU32BE(png, o); o += 4;
                string type = System.Text.Encoding.ASCII.GetString(png, o, 4); o += 4;

                if (type == "IHDR")
                {
                    width = (int)ReadU32BE(png, o);
                    height = (int)ReadU32BE(png, o + 4);
                    bitDepth = png[o + 8];
                    colorType = png[o + 9];
                }
                else if (type == "IDAT")
                {
                    idatBuf.Write(png, o, len);
                }
                else if (type == "IEND") break;

                o += len + 4; // skip data + crc
            }

            if (width <= 0 || height <= 0) throw new InvalidDataException("PNG missing IHDR.");
            if (bitDepth != 8) throw new NotSupportedException("Only 8-bit PNGs supported.");

            int channels = colorType switch
            {
                0 => 1,            // grayscale
                2 => 3,            // RGB
                3 => 1,            // indexed (not fully supported here)
                4 => 2,            // grayscale + alpha
                6 => 4,            // RGBA
                _ => throw new NotSupportedException($"PNG color type {colorType} not supported.")
            };
            if (colorType == 3)
                throw new NotSupportedException("Indexed PNGs not supported. Re-export as RGBA.");

            byte[] raw = ZlibDecompress(idatBuf.ToArray());
            int stride = width * channels;
            var unfiltered = new byte[stride * height];
            int srcCursor = 0;
            byte[] prevRow = new byte[stride];

            for (int y = 0; y < height; y++)
            {
                byte filter = raw[srcCursor++];
                var row = new byte[stride];
                Buffer.BlockCopy(raw, srcCursor, row, 0, stride);
                srcCursor += stride;
                ApplyFilter(filter, row, prevRow, channels);
                Buffer.BlockCopy(row, 0, unfiltered, y * stride, stride);
                prevRow = row;
            }

            // Expand to RGBA.
            var rgba = new byte[width * height * 4];
            for (int i = 0; i < width * height; i++)
            {
                int s = i * channels;
                switch (channels)
                {
                    case 1:
                        rgba[i * 4 + 0] = unfiltered[s];
                        rgba[i * 4 + 1] = unfiltered[s];
                        rgba[i * 4 + 2] = unfiltered[s];
                        rgba[i * 4 + 3] = 255;
                        break;
                    case 2:
                        rgba[i * 4 + 0] = unfiltered[s];
                        rgba[i * 4 + 1] = unfiltered[s];
                        rgba[i * 4 + 2] = unfiltered[s];
                        rgba[i * 4 + 3] = unfiltered[s + 1];
                        break;
                    case 3:
                        rgba[i * 4 + 0] = unfiltered[s + 0];
                        rgba[i * 4 + 1] = unfiltered[s + 1];
                        rgba[i * 4 + 2] = unfiltered[s + 2];
                        rgba[i * 4 + 3] = 255;
                        break;
                    case 4:
                        rgba[i * 4 + 0] = unfiltered[s + 0];
                        rgba[i * 4 + 1] = unfiltered[s + 1];
                        rgba[i * 4 + 2] = unfiltered[s + 2];
                        rgba[i * 4 + 3] = unfiltered[s + 3];
                        break;
                }
            }
            return rgba;
        }

        private static void ApplyFilter(byte filter, byte[] row, byte[] prev, int bpp)
        {
            switch (filter)
            {
                case 0: return; // none
                case 1: // sub
                    for (int i = bpp; i < row.Length; i++) row[i] = (byte)(row[i] + row[i - bpp]);
                    return;
                case 2: // up
                    for (int i = 0; i < row.Length; i++) row[i] = (byte)(row[i] + prev[i]);
                    return;
                case 3: // average
                    for (int i = 0; i < row.Length; i++)
                    {
                        int left = i >= bpp ? row[i - bpp] : 0;
                        row[i] = (byte)(row[i] + (left + prev[i]) / 2);
                    }
                    return;
                case 4: // paeth
                    for (int i = 0; i < row.Length; i++)
                    {
                        int left = i >= bpp ? row[i - bpp] : 0;
                        int up = prev[i];
                        int upLeft = i >= bpp ? prev[i - bpp] : 0;
                        row[i] = (byte)(row[i] + Paeth(left, up, upLeft));
                    }
                    return;
                default: throw new InvalidDataException($"Unsupported PNG filter {filter}.");
            }
        }

        private static int Paeth(int a, int b, int c)
        {
            int p = a + b - c;
            int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
            if (pa <= pb && pa <= pc) return a;
            if (pb <= pc) return b;
            return c;
        }

        private static byte[] ZlibDecompress(byte[] data)
        {
            // Skip 2-byte zlib header, ignore trailing 4-byte adler32.
            using var input = new MemoryStream(data, 2, data.Length - 6);
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            using var outMs = new MemoryStream();
            deflate.CopyTo(outMs);
            return outMs.ToArray();
        }

        private static uint ReadU32BE(byte[] buf, int offset) =>
            ((uint)buf[offset + 0] << 24)
            | ((uint)buf[offset + 1] << 16)
            | ((uint)buf[offset + 2] << 8)
            | buf[offset + 3];
    }
}
