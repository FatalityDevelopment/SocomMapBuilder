using System.IO.Compression;
using System.Text;

namespace CodeDesigner.Ps2.FileFormats.Ftb3
{
    /// <summary>
    /// A SOCOM: U.S. Navy SEALs Fireteam Bravo 3 (PSP, UCUS-98716) <c>.ssg</c> file: a 0x20-byte header, a table of
    /// 16-byte entries, a string table and one data block (zlib when the header says so). See docs/formats/ftb3.md.
    /// </summary>
    /// <remarks>
    /// Header: +0x00 version (6), +0x04 1 when the data is zlib-compressed, +0x08 the entry table's size, +0x0C the
    /// string table's size, +0x10 the data's size unpacked, +0x18 its size packed. An entry: +0x00 its name (an offset
    /// into the string table), +0x04 size, +0x08 offset (in the data), +0x0C type, +0x0D flags, +0x0E a 16-bit hash.
    /// An object's resources share one name, which is often two names run together (the object's, then its shape's:
    /// <c>Grass_TallGrass_Tall</c>).
    /// </remarks>
    public sealed class SsgArchive
    {
        public sealed record Entry(int Index, string Name, int Size, int Offset, byte Type, byte Flags, ushort Hash);

        /// <summary>Entry types seen on the disc.</summary>
        public const byte MeshPiece = 0x00, Texture = 0x01, Instance = 0x07, Model = 0x0B;

        public string SourcePath { get; private init; } = "";
        public List<Entry> Entries { get; } = new();

        /// <summary>The data block, unpacked; entries index it. Edits write into it (<see cref="ToBytes"/>).</summary>
        public byte[] Data { get; private init; } = Array.Empty<byte>();

        /// <summary>True when the file's data is zlib-compressed.</summary>
        public bool Packed { get; private init; }

        // The header, entry table and string table, as read.
        private byte[] _head = Array.Empty<byte>();

        public const int HeaderSize = 0x20, Version = 6;

        /// <summary>True when <paramref name="path"/> is a file that starts like an .ssg (version 6, sane table sizes).</summary>
        public static bool IsSsg(string path)
        {
            if (!File.Exists(path)) return false;
            try
            {
                using var f = File.OpenRead(path);
                var h = new byte[HeaderSize];
                if (f.Read(h, 0, HeaderSize) < HeaderSize) return false;
                return LooksLikeHeader(h, f.Length);
            }
            catch (IOException) { return false; }
        }

        private static bool LooksLikeHeader(byte[] h, long length)
        {
            uint version = BitConverter.ToUInt32(h, 0), packed = BitConverter.ToUInt32(h, 4);
            long entries = BitConverter.ToUInt32(h, 8), strings = BitConverter.ToUInt32(h, 0xC);
            return version == Version && packed <= 1 && entries % 16 == 0 && HeaderSize + entries + strings <= length;
        }

        public static SsgArchive Open(string path) => FromBytes(File.ReadAllBytes(path), path);

        public static SsgArchive FromBytes(byte[] f, string path = "")
        {
            if (f.Length < HeaderSize || !LooksLikeHeader(f, f.Length)) throw new InvalidDataException($"{Path.GetFileName(path)} is not an .ssg file");
            int entries = (int)BitConverter.ToUInt32(f, 8), strings = (int)BitConverter.ToUInt32(f, 0xC), size = (int)BitConverter.ToUInt32(f, 0x10);
            int names = HeaderSize + entries, dataAt = names + strings;
            byte[] data;
            if (BitConverter.ToUInt32(f, 4) == 1)
            {
                using var z = new ZLibStream(new MemoryStream(f, dataAt, f.Length - dataAt), CompressionMode.Decompress);
                using var ms = new MemoryStream(Math.Max(size, 0));
                z.CopyTo(ms);
                data = ms.ToArray();
            }
            else data = f[dataAt..];
            var a = new SsgArchive { SourcePath = path, Data = data, Packed = BitConverter.ToUInt32(f, 4) == 1, _head = f[..dataAt] };
            for (int i = 0; i < entries / 16; i++)
            {
                int e = HeaderSize + 16 * i;
                int name = names + (int)BitConverter.ToUInt32(f, e);
                string n = "";
                if (name >= names && name < dataAt)
                {
                    int end = Array.IndexOf(f, (byte)0, name, dataAt - name);
                    n = Encoding.ASCII.GetString(f, name, (end < 0 ? dataAt : end) - name);
                }
                int offset = (int)BitConverter.ToUInt32(f, e + 8), length = (int)BitConverter.ToUInt32(f, e + 4);
                if (offset < 0 || length < 0 || (long)offset + length > data.Length) continue;
                a.Entries.Add(new Entry(i, n, length, offset, f[e + 12], f[e + 13], BitConverter.ToUInt16(f, e + 14)));
            }
            return a;
        }

        /// <summary>
        /// The file with the data as it is now: an unpacked file byte for byte as read (apart from edits), a packed one
        /// with its data compressed again (zlib, so the bytes differ from the disc's) and its packed size (+0x18) set.
        /// </summary>
        public byte[] ToBytes()
        {
            byte[] body = Data;
            if (Packed)
            {
                using var ms = new MemoryStream();
                using (var z = new ZLibStream(ms, CompressionLevel.SmallestSize, leaveOpen: true)) z.Write(Data);
                body = ms.ToArray();
            }
            var file = new byte[_head.Length + body.Length];
            _head.CopyTo(file, 0);
            BitConverter.GetBytes((uint)Data.Length).CopyTo(file, 0x10);
            if (Packed) BitConverter.GetBytes((uint)body.Length).CopyTo(file, 0x18);
            body.CopyTo(file, _head.Length);
            return file;
        }

        public uint U32(int o) => o >= 0 && o + 4 <= Data.Length ? BitConverter.ToUInt32(Data, o) : 0;

        public float F32(int o) => o >= 0 && o + 4 <= Data.Length ? BitConverter.ToSingle(Data, o) : 0;

        /// <summary>A null-terminated ASCII string in the data (at most <paramref name="max"/> bytes).</summary>
        public string Str(int o, int max)
        {
            if (o < 0 || o >= Data.Length) return "";
            int end = Array.IndexOf(Data, (byte)0, o, Math.Min(max, Data.Length - o));
            return Encoding.ASCII.GetString(Data, o, (end < 0 ? Math.Min(o + max, Data.Length) : end) - o);
        }
    }
}
