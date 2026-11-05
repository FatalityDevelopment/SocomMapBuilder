using System.Text;

namespace CodeDesigner.Ps2.FileFormats.Swbf
{
    /// <summary>
    /// A ZeroEngine (Star Wars: Battlefront) chunk: a four-character tag, a little-endian u32 body size and the body,
    /// padded to four bytes. A .LVL file is one "ucfb" chunk whose body is a run of chunks; container chunks
    /// (modl, segm, tex_, wrld ...) hold chunks of their own, leaf chunks (NAME, INFO, POSI ...) hold data.
    /// A "lvl_" chunk is a nested level: a name hash and a size, then a run of chunks.
    /// </summary>
    public readonly record struct Ucfb(byte[] Data, int Offset, int Size, string Tag)
    {
        /// <summary>The body's first byte.</summary>
        public int Body => Offset + 8;

        public ReadOnlySpan<byte> Bytes => Data.AsSpan(Body, Size);

        /// <summary>The root chunk of a .LVL file (its tag must be "ucfb").</summary>
        public static Ucfb Root(byte[] data)
        {
            if (!IsLevel(data)) throw new InvalidDataException("Not a ZeroEngine level (no ucfb header)");
            int size = (int)Math.Min(BitConverter.ToUInt32(data, 4), (uint)(data.Length - 8));
            return new Ucfb(data, 0, size, "ucfb");
        }

        public static bool IsLevel(ReadOnlySpan<byte> head) => head.Length >= 8 && head[..4].SequenceEqual("ucfb"u8);

        /// <summary>The chunks of a run starting at <paramref name="start"/> and ending at <paramref name="end"/>.</summary>
        public static IEnumerable<Ucfb> Run(byte[] data, int start, int end)
        {
            int o = start;
            while (o + 8 <= end)
            {
                int size = (int)BitConverter.ToUInt32(data, o + 4);
                if (size < 0 || o + 8 + size > end || !IsTag(data, o)) yield break;
                yield return new Ucfb(data, o, size, Encoding.ASCII.GetString(data, o, 4));
                o = (o + 8 + size + 3) & ~3;
            }
        }

        /// <summary>The chunks inside this one (for a "lvl_" level, the chunks after its hash and size).</summary>
        public IEnumerable<Ucfb> Children => Tag == "lvl_" ? Run(Data, Body + 8, Body + Size) : Run(Data, Body, Body + Size);

        public Ucfb? Child(string tag)
        {
            foreach (var c in Children) if (c.Tag == tag) return c;
            return null;
        }

        /// <summary>The body as a zero-terminated ASCII string.</summary>
        public string Text(int skip = 0)
        {
            var b = Bytes[Math.Min(skip, Size)..];
            int z = b.IndexOf((byte)0);
            return Encoding.ASCII.GetString(z < 0 ? b : b[..z]);
        }

        public string? ChildText(string tag, int skip = 0) => Child(tag)?.Text(skip);

        public int I32(int at) => BitConverter.ToInt32(Data, Body + at);
        public uint U32(int at) => BitConverter.ToUInt32(Data, Body + at);
        public ushort U16(int at) => BitConverter.ToUInt16(Data, Body + at);
        public short S16(int at) => BitConverter.ToInt16(Data, Body + at);
        public float F32(int at) => BitConverter.ToSingle(Data, Body + at);

        private static bool IsTag(byte[] d, int o)
        {
            for (int i = 0; i < 4; i++)
            {
                byte c = d[o + i];
                if (!(c is >= (byte)'0' and <= (byte)'9' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z' or (byte)'_' or (byte)'"' or (byte)'&' or (byte)'^')) return false;
            }
            return true;
        }
    }
}
