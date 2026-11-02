using System;
using System.Buffers.Binary;
using System.IO;
using System.Collections.Generic;
using System.Linq;
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
        /// <summary>Where the texture starts (just past the animation), whether or not the icon has one.</summary>
        public int TextureOffset { get; init; }
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
                Shapes = shapes, TextureType = texType, VertexCount = count, FrameCount = frames, TextureOffset = pos,
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

        /// <summary>
        /// The icon with a new texture: the picture (any size, scaled to 128×128 by nearest pixel) stored unpacked
        /// after the animation, texture type bits 2 (textured) set and 3 (packed) cleared. Transparent pixels
        /// (alpha below half) become colour 0; opaque black is kept just above it so it stays visible.
        /// </summary>
        public static byte[] WithTexture(byte[] icon, byte[] rgba, int width, int height)
        {
            var parsed = Parse(icon);
            if (rgba.Length != width * height * 4 || width <= 0 || height <= 0) throw new ArgumentException("The picture's size does not match its pixels.");
            var output = new byte[parsed.TextureOffset + TextureBytes];
            Buffer.BlockCopy(icon, 0, output, 0, parsed.TextureOffset);
            BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(8), (parsed.TextureType | 4) & ~8);
            for (int y = 0; y < TextureSize; y++)
                for (int x = 0; x < TextureSize; x++)
                {
                    int src = ((y * height / TextureSize) * width + x * width / TextureSize) * 4;
                    int r = rgba[src] >> 3, g = rgba[src + 1] >> 3, b = rgba[src + 2] >> 3;
                    int v = rgba[src + 3] < 128 ? 0 : (r | g << 5 | b << 10) is 0 ? 0x0421 : r | g << 5 | b << 10;
                    BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(parsed.TextureOffset + (y * TextureSize + x) * 2), (ushort)v);
                }
            return output;
        }

        /// <summary>An icon.sys ("PS2D") with a new two-line title (full-width Shift-JIS, 68 bytes with the line
        /// break word at 6) and icon file names (64 bytes each at 0x104, 0x144, 0x184).</summary>
        public static byte[] WithIconSys(byte[] sys, string line1, string line2, string[] icons)
        {
            if (ReadIconSys(sys) == null) throw new InvalidDataException("Not a PS2D icon.sys.");
            byte[] first = ToShiftJis(line1), second = ToShiftJis(line2);
            if (first.Length + second.Length > 66) throw new ArgumentException("The title is too long (33 characters across both lines).");
            var output = (byte[])sys.Clone();
            Array.Clear(output, 0xC0, 68);
            first.CopyTo(output, 0xC0);
            second.CopyTo(output, 0xC0 + first.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(6), (ushort)first.Length);
            for (int i = 0; i < 3; i++)
            {
                string name = i < icons.Length ? icons[i] ?? "" : "";
                if (name.Length > 63 || name.Any(c => c < 0x20 || c > 0x7E)) throw new ArgumentException($"\"{name}\" is not a file name (printable ASCII, up to 63 characters).");
                Array.Clear(output, 0x104 + i * 64, 64);
                Encoding.ASCII.GetBytes(name).CopyTo(output, 0x104 + i * 64);
            }
            return output;
        }

        // Printable ASCII to the full-width Shift-JIS the browser shows (the inverse of ShiftJisText).
        private static byte[] ToShiftJis(string text)
        {
            var bytes = new List<byte>();
            foreach (char c in text)
            {
                (int hi, int lo) = c switch
                {
                    ' ' => (0x81, 0x40), ':' => (0x81, 0x46), '.' => (0x81, 0x44), ',' => (0x81, 0x43), '-' => (0x81, 0x5B),
                    '(' => (0x81, 0x69), ')' => (0x81, 0x6A), '&' => (0x81, 0x95), '/' => (0x81, 0x5E), '!' => (0x81, 0x49), '?' => (0x81, 0x48),
                    >= '0' and <= '9' => (0x82, 0x4F + c - '0'),
                    >= 'A' and <= 'Z' => (0x82, 0x60 + c - 'A'),
                    >= 'a' and <= 'z' => (0x82, 0x81 + c - 'a'),
                    _ => throw new ArgumentException($"'{c}' cannot be written in a save title (letters, digits, space and : . , - ( ) & / ! ?)."),
                };
                bytes.Add((byte)hi);
                bytes.Add((byte)lo);
            }
            return bytes.ToArray();
        }

        public sealed record IconSys(string Line1, string Line2, string[] Icons)
        {
            public string Title => $"{Line1} {Line2}".Trim();
        }

        /// <summary>The two-line save title and icon file names of an icon.sys ("PS2D").</summary>
        public static IconSys? ReadIconSys(ReadOnlySpan<byte> data)
        {
            if (data.Length < 0x1C4 || !data[..4].SequenceEqual("PS2D"u8)) return null;
            // Word 6: where the title's second line starts, in bytes.
            int lineBreak = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]);
            var title = data.Slice(0xC0, 68);
            bool split = lineBreak is > 0 and < 68;
            string line1 = ShiftJisText(split ? title[..lineBreak] : title), line2 = split ? ShiftJisText(title[lineBreak..]) : "";
            return new IconSys(line1, line2, new[] { Ascii(data.Slice(0x104, 64)), Ascii(data.Slice(0x144, 64)), Ascii(data.Slice(0x184, 64)) });
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
