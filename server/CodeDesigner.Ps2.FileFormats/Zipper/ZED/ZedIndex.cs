using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CodeDesigner.Ps2.FileFormats.Zipper.ZED
{
    /// <summary>
    /// The record table of a ZED / ZAR file read from disk without loading its body: names, offsets and sizes, and
    /// one entry's bytes on request. For browsing archives too big to read whole (RUN/SOUNDS/VAGSTORE.ZAR is 550 MB);
    /// <see cref="ZedContainer"/> is the reader for everything that edits.
    /// </summary>
    public sealed class ZedIndex
    {
        private const int FixedHeaderSize = 0x64;
        private const int RecordSize = 0x10;

        public sealed record Entry(int Index, string Name, long Offset, int Size, uint Type);

        public string Path { get; }
        public long BodyStart { get; }
        public uint Alignment { get; }
        public IReadOnlyList<Entry> Entries { get; }

        private ZedIndex(string path, long bodyStart, uint alignment, IReadOnlyList<Entry> entries)
        {
            Path = path; BodyStart = bodyStart; Alignment = alignment; Entries = entries;
        }

        public static ZedIndex Open(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            long length = fs.Length;
            if (length < FixedHeaderSize) throw new InvalidDataException("Too short to be a ZED/ZAR container.");
            byte[] head = ReadAt(fs, 0, FixedHeaderSize);
            uint slots = BitConverter.ToUInt32(head, 0x04);
            uint stringSize = BitConverter.ToUInt32(head, 0x08);
            uint stringBase = BitConverter.ToUInt32(head, 0x0C);
            uint align = Math.Max(BitConverter.ToUInt32(head, 0x10), ZedContainer.DefaultDataAlignment);
            uint bodySize = BitConverter.ToUInt32(head, 0x54);
            if (slots == 0 || slots > 1_000_000) throw new InvalidDataException("Invalid slot count.");
            long recordStart = FixedHeaderSize + (long)stringSize + 0x10;
            long recordEnd = recordStart + (slots - 1) * (long)RecordSize;
            if (recordEnd > length) throw new InvalidDataException("Record table exceeds file size.");

            byte[] strings = ReadAt(fs, FixedHeaderSize, (int)stringSize);
            byte[] records = ReadAt(fs, recordStart, (int)(recordEnd - recordStart));
            long bodyStart = bodySize == 0 ? (recordEnd + align - 1) / align * align : length - bodySize;
            if (bodyStart < recordEnd) throw new InvalidDataException("Record table overlaps the body region.");

            var entries = new List<Entry>((int)slots - 1);
            for (int i = 0; i < slots - 1; i++)
            {
                int r = i * RecordSize;
                uint namePtr = BitConverter.ToUInt32(records, r);
                uint offset = BitConverter.ToUInt32(records, r + 4);
                uint size = BitConverter.ToUInt32(records, r + 8);
                uint type = BitConverter.ToUInt32(records, r + 12);
                long rel = (long)namePtr - stringBase;
                string name = rel >= 0 && rel < strings.Length ? CString(strings, (int)rel) : $"#{i}";
                if (size != 0 && bodyStart + offset + size > length) throw new InvalidDataException($"Node '{name}' points outside the body region.");
                entries.Add(new Entry(i, name, size == 0 ? 0 : bodyStart + offset, (int)size, type));
            }
            return new ZedIndex(path, bodyStart, align, entries);
        }

        /// <summary>One entry's bytes, read from disk.</summary>
        public byte[] Read(int index, int maxBytes = int.MaxValue)
        {
            var e = Entries[index];
            if (e.Size == 0) return Array.Empty<byte>();
            using var fs = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return ReadAt(fs, e.Offset, Math.Min(e.Size, maxBytes));
        }

        private static byte[] ReadAt(Stream s, long offset, int count)
        {
            var buf = new byte[count];
            s.Position = offset;
            int read = 0;
            while (read < count)
            {
                int n = s.Read(buf, read, count - read);
                if (n <= 0) throw new EndOfStreamException();
                read += n;
            }
            return buf;
        }

        private static string CString(byte[] data, int start)
        {
            int end = Array.IndexOf(data, (byte)0, start);
            if (end < 0) end = data.Length;
            return Encoding.ASCII.GetString(data, start, end - start);
        }
    }
}
