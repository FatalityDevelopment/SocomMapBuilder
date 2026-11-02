using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace CodeDesigner.Ps2.FileFormats.Ps2
{
    /// <summary>
    /// A memory-card icon (.ICO), read as CodeDesigner's memory-card and HDD browsers read it: a header (0x00010000,
    /// shape count, texture type, 1.0f, vertex count); per vertex the position in every animation shape, a normal
    /// (int16 ×4, 1/4096 units), a UV (int16 ×2, 1/4096) and an RGBA colour (0x80 = 1.0); the animation (a header,
    /// then frames of keys); then, when texture type bit 2 is set, a 128×128 A1B5G5R5 texture stored top row first
    /// (RLE packed behind a size word when bit 3 is set). Every three vertices are a triangle.
    /// Icons are Y down with their front toward -Z; the viewports are Y up with the front toward +Z, so positions
    /// turn half a turn about X.
    /// </summary>
    public sealed class Ps2Icon
    {
        public const int TextureSize = 128;
        private const int TextureBytes = TextureSize * TextureSize * 2;

        public int Shapes { get; init; }
        public int TextureType { get; init; }
        public int VertexCount { get; init; }
        public int FrameCount { get; init; }
        /// <summary>The first shape (the rest pose), Y up, front toward +Z.</summary>
        public float[] Positions { get; init; } = Array.Empty<float>();
        public float[] Normals { get; init; } = Array.Empty<float>();
        /// <summary>U and V as stored (V = 0 is the texture's top row).</summary>
        public float[] Uvs { get; init; } = Array.Empty<float>();
        public byte[] Colors { get; init; } = Array.Empty<byte>();
        /// <summary>128×128 RGBA, top row first, black (colour 0) transparent; null when the icon has none.</summary>
        public byte[]? Texture { get; init; }

        public static bool IsIcon(ReadOnlySpan<byte> data) =>
            data.Length >= 20 && BinaryPrimitives.ReadUInt32LittleEndian(data) == 0x00010000 && BinaryPrimitives.ReadSingleLittleEndian(data[12..]) == 1f;

        public static Ps2Icon Parse(byte[] data)
        {
            if (!IsIcon(data)) throw new InvalidDataException("Not a PS2 icon (no 0x00010000 header).");
            int shapes = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4));
            int texType = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(8));
            int count = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(16));
            if (shapes <= 0 || shapes > 128 || count <= 0 || count > 200_000) throw new InvalidDataException("Implausible icon header.");
            int stride = shapes * 8 + 8 + 4 + 4;
            int pos = 20;
            if (pos + (long)count * stride > data.Length) throw new InvalidDataException("Icon vertices run past the end of the file.");

            var positions = new float[count * 3];
            var normals = new float[count * 3];
            var uvs = new float[count * 2];
            var colors = new byte[count * 4];
            for (int v = 0; v < count; v++, pos += stride)
            {
                var s = data.AsSpan(pos);
                positions[v * 3] = I16(s, 0) / 4096f;
                positions[v * 3 + 1] = -I16(s, 2) / 4096f;
                positions[v * 3 + 2] = -I16(s, 4) / 4096f;
                var rest = s[(shapes * 8)..];
                normals[v * 3] = I16(rest, 0) / 4096f;
                normals[v * 3 + 1] = -I16(rest, 2) / 4096f;
                normals[v * 3 + 2] = -I16(rest, 4) / 4096f;
                uvs[v * 2] = I16(rest, 8) / 4096f;
                uvs[v * 2 + 1] = I16(rest, 10) / 4096f;
                rest.Slice(12, 4).CopyTo(colors.AsSpan(v * 4));
            }

            // The animation: magic 1, frame length, speed, play offset, frame count; each frame a shape id, a key
            // count and two words, then (keys - 1) time / value pairs.
            if (pos + 20 > data.Length || BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos)) != 1)
                throw new InvalidDataException("Icon animation header missing.");
            int frames = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(pos + 16));
            pos += 20;
            for (int f = 0; f < frames; f++)
            {
                if (pos + 16 > data.Length) throw new InvalidDataException("Icon animation frames run past the end of the file.");
                int keys = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(pos + 4));
                pos += 16 + Math.Max(0, keys - 1) * 8;
            }

            byte[]? texture = null;
            if ((texType & 4) != 0 && pos < data.Length)
            {
                byte[]? pixels = (texType & 8) != 0 ? Unpack(data, pos) : data.Length - pos >= TextureBytes ? data.AsSpan(pos, TextureBytes).ToArray() : null;
                if (pixels != null) texture = ToRgba(pixels);
            }

            return new Ps2Icon
            {
                Shapes = shapes, TextureType = texType, VertexCount = count, FrameCount = frames,
                Positions = positions, Normals = normals, Uvs = uvs, Colors = colors, Texture = texture,
            };
        }

        private static short I16(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadInt16LittleEndian(s[o..]);

        // A size word, then RLE of 16-bit pixels: a code with bit 15 set copies the next (0x10000 - code) pixels as
        // they are; any other code repeats the next pixel that many times.
        private static byte[]? Unpack(byte[] data, int pos)
        {
            if (pos + 4 > data.Length) return null;
            int size = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(pos));
            var src = data.AsSpan(pos + 4, Math.Min(Math.Max(0, size), data.Length - pos - 4));
            var dst = new byte[TextureBytes];
            int si = 0, di = 0;
            while (si + 2 <= src.Length && di < dst.Length)
            {
                int code = BinaryPrimitives.ReadUInt16LittleEndian(src[si..]);
                si += 2;
                if ((code & 0x8000) != 0)
                {
                    int n = Math.Min((0x10000 - code) * 2, Math.Min(src.Length - si, dst.Length - di));
                    src.Slice(si, n).CopyTo(dst.AsSpan(di));
                    si += n; di += n;
                }
                else
                {
                    if (si + 2 > src.Length) break;
                    for (int i = 0; i < code && di + 2 <= dst.Length; i++) { dst[di++] = src[si]; dst[di++] = src[si + 1]; }
                    si += 2;
                }
            }
            return di == dst.Length ? dst : null;
        }

        private static byte[] ToRgba(ReadOnlySpan<byte> a1b5g5r5)
        {
            var rgba = new byte[TextureSize * TextureSize * 4];
            for (int i = 0; i < TextureSize * TextureSize; i++)
            {
                int v = BinaryPrimitives.ReadUInt16LittleEndian(a1b5g5r5[(i * 2)..]);
                rgba[i * 4] = Expand5(v & 0x1F);
                rgba[i * 4 + 1] = Expand5((v >> 5) & 0x1F);
                rgba[i * 4 + 2] = Expand5((v >> 10) & 0x1F);
                // Black is the icons' see-through colour (the top bit is not used for alpha).
                rgba[i * 4 + 3] = (byte)((v & 0x7FFF) == 0 ? 0 : 255);
            }
            return rgba;
        }

        private static byte Expand5(int v) => (byte)((v << 3) | (v >> 2));

        /// <summary>The save title and icon file names of an icon.sys ("PS2D").</summary>
        public static (string Title, string[] Icons)? ReadIconSys(ReadOnlySpan<byte> data)
        {
            if (data.Length < 0x1C4 || !data[..4].SequenceEqual("PS2D"u8)) return null;
            // Word 6: where the title's second line starts, in bytes.
            int lineBreak = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]);
            string title = lineBreak is > 0 and < 68
                ? ShiftJisText(data.Slice(0xC0, lineBreak)) + " " + ShiftJisText(data.Slice(0xC0 + lineBreak, 68 - lineBreak))
                : ShiftJisText(data.Slice(0xC0, 68));
            var icons = new[] { Ascii(data.Slice(0x104, 64)), Ascii(data.Slice(0x144, 64)), Ascii(data.Slice(0x184, 64)) };
            return (title, icons);
        }

        private static string Ascii(ReadOnlySpan<byte> s)
        {
            int end = s.IndexOf((byte)0);
            return Encoding.ASCII.GetString(end < 0 ? s : s[..end]);
        }

        // Titles are full-width Shift-JIS: map the full-width Latin letters, digits and punctuation back to ASCII.
        private static string ShiftJisText(ReadOnlySpan<byte> s)
        {
            var sb = new StringBuilder();
            for (int i = 0; i + 1 < s.Length; i += 2)
            {
                int hi = s[i], lo = s[i + 1];
                if (hi == 0) break;
                if (hi < 0x80) { sb.Append((char)hi); i--; continue; }
                char c = (hi, lo) switch
                {
                    (0x81, 0x40) => ' ',
                    (0x81, 0x46) => ':', (0x81, 0x44) => '.', (0x81, 0x43) => ',', (0x81, 0x5B) => '-', (0x81, 0x7C) => '-',
                    (0x81, 0x69) => '(', (0x81, 0x6A) => ')', (0x81, 0x95) => '&', (0x81, 0x5E) => '/', (0x81, 0x49) => '!', (0x81, 0x48) => '?',
                    (0x82, >= 0x4F and <= 0x58) => (char)('0' + lo - 0x4F),
                    (0x82, >= 0x60 and <= 0x79) => (char)('A' + lo - 0x60),
                    (0x82, >= 0x81 and <= 0x9A) => (char)('a' + lo - 0x81),
                    _ => '?',
                };
                sb.Append(c);
            }
            return sb.ToString().Trim();
        }
    }
}
