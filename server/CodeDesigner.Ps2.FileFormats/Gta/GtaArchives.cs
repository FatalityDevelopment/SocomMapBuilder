using System.Text;

namespace CodeDesigner.Ps2.FileFormats.Gta
{
    /// <summary>A read-only view of a file: a span of a disc image or a loose file (opened per read, never held).</summary>
    public sealed record FileSpan(string Path, long Offset, long Length)
    {
        public byte[] Read(long at = 0, long? count = null)
        {
            long n = count ?? Length - at;
            if (at < 0 || at > Length || n < 0 || n > Length - at || n > Array.MaxLength)
                throw new InvalidDataException($"Read outside {Path} entry at {Offset}: {at} + {n} of {Length}");
            var b = new byte[(int)n];
            using var f = File.OpenRead(Path);
            f.Seek(Offset + at, SeekOrigin.Begin);
            f.ReadExactly(b);
            return b;
        }

        internal void CopyTo(Stream destination)
        {
            using var source = File.OpenRead(Path);
            source.Position = Offset;
            byte[] buffer = new byte[1024 * 1024];
            for (long remaining = Length; remaining > 0;)
            {
                int count = (int)Math.Min(buffer.Length, remaining);
                source.ReadExactly(buffer, 0, count);
                destination.Write(buffer, 0, count);
                remaining -= count;
            }
        }
    }

    /// <summary>The ISO-9660 directory of a disc image: file path (upper case, '/' separated) -> span.</summary>
    public static class Iso9660
    {
        public static Dictionary<string, FileSpan> Files(string iso)
        {
            var files = new Dictionary<string, FileSpan>(StringComparer.OrdinalIgnoreCase);
            using var f = File.OpenRead(iso);
            byte[] pvd = new byte[2048];
            f.Seek(16 * 2048, SeekOrigin.Begin);
            f.ReadExactly(pvd);
            if (Encoding.ASCII.GetString(pvd, 1, 5) != "CD001") throw new InvalidDataException($"{iso} is not an ISO-9660 image");
            void Walk(uint lba, uint size, string path, int depth)
            {
                if (depth > 16) return;
                var data = new byte[size];
                f.Seek((long)lba * 2048, SeekOrigin.Begin);
                f.ReadExactly(data);
                int o = 0;
                while (o < data.Length)
                {
                    int len = data[o];
                    if (len == 0) { o = (o / 2048 + 1) * 2048; continue; }
                    uint ext = BitConverter.ToUInt32(data, o + 2), sz = BitConverter.ToUInt32(data, o + 10);
                    int flags = data[o + 25], nameLen = data[o + 32];
                    string name = Encoding.ASCII.GetString(data, o + 33, nameLen);
                    if (name != "\0" && name != "\u0001")
                    {
                        string p = path + "/" + name.Split(';')[0];
                        if ((flags & 2) != 0) Walk(ext, sz, p, depth + 1);
                        else files[p.TrimStart('/')] = new FileSpan(iso, (long)ext * 2048, sz);
                    }
                    o += len;
                }
            }
            Walk(BitConverter.ToUInt32(pvd, 156 + 2), BitConverter.ToUInt32(pvd, 156 + 10), "", 0);
            return files;
        }
    }

    /// <summary>
    /// A GTA III-era streaming archive, version 2 (San Andreas): "VER2", entry count, then 32-byte entries
    /// {offset (sectors), streaming size (sectors), archive size (sectors), name[24]}; 2048-byte sectors.
    /// </summary>
    public sealed class GtaImg
    {
        public sealed record Entry(string Name, long Offset, long Size)
        {
            internal int DirectoryIndex { get; init; }
        }

        public FileSpan Source { get; }
        public IReadOnlyList<Entry> Entries { get; }
        private readonly Dictionary<string, Entry> _byName;
        private readonly byte[] _directory;

        public GtaImg(FileSpan source)
        {
            Source = source;
            byte[] head = source.Read(0, 8);
            if (Encoding.ASCII.GetString(head, 0, 4) != "VER2") throw new InvalidDataException($"{source.Path}: not a VER2 IMG archive");
            int n = BitConverter.ToInt32(head, 4);
            if (n < 0 || 32L * n > source.Length - 8)
                throw new InvalidDataException($"{source.Path}: IMG directory extends beyond the file");
            byte[] dir = _directory = source.Read(8, 32L * n);
            var list = new List<Entry>(n);
            for (int i = 0; i < n; i++)
            {
                int o = 32 * i;
                uint offset = BitConverter.ToUInt32(dir, o);
                ushort streaming = BitConverter.ToUInt16(dir, o + 4), archive = BitConverter.ToUInt16(dir, o + 6);
                int end = Array.IndexOf(dir, (byte)0, o + 8, 24);
                string name = Encoding.ASCII.GetString(dir, o + 8, (end < 0 ? o + 32 : end) - (o + 8));
                long start = offset * 2048L, size = (archive != 0 ? archive : streaming) * 2048L;
                if (start + size > source.Length || (size > 0 && start < 8 + 32L * n))
                    throw new InvalidDataException($"{source.Path}: IMG entry {name} lies outside the payload");
                list.Add(new Entry(name, start, size) { DirectoryIndex = i });
            }
            Entries = list;
            _byName = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in list) _byName.TryAdd(e.Name, e);
        }

        public Entry? Find(string name) => _byName.TryGetValue(name, out var e) ? e : null;

        public byte[] Read(Entry e) => Source.Read(e.Offset, e.Size);

        public byte[]? Read(string name) => Find(name) is { } e ? Read(e) : null;

        internal void CopyTo(FileStream output)
        {
            Source.CopyTo(output);
            // Loose archives may be repacked while a session is open. Never patch positions from an obsolete directory.
            output.Position = 0;
            Span<byte> head = stackalloc byte[8];
            output.ReadExactly(head);
            var current = new byte[_directory.Length];
            output.ReadExactly(current);
            if (!head[..4].SequenceEqual("VER2"u8) || BitConverter.ToInt32(head[4..]) != Entries.Count || !current.AsSpan().SequenceEqual(_directory))
                throw new InvalidOperationException("The source IMG directory changed. Reopen the archive before saving.");
        }

        /// <summary>Write an edited uncompressed DFF. Preserve other sectors including unused trailing data.
        /// A growing or shared slot is relocated to the next sector after the complete source image.</summary>
        internal void WriteEntry(FileStream output, Entry entry, byte[] data)
        {
            const int sector = 2048;
            int sectors = checked((int)((data.LongLength + sector - 1) / sector));
            if (sectors is 0 or > ushort.MaxValue) throw new InvalidDataException("DFF does not fit the IMG's 16-bit sector count.");
            long length = (long)sectors * sector, at = entry.Offset;
            if (length > entry.Size || Entries.Any(other => other != entry && other.Offset < at + length && at < other.Offset + other.Size))
            {
                at = checked((output.Length + sector - 1) / sector * sector);
                if (at / sector > uint.MaxValue) throw new InvalidDataException("Edited IMG exceeds its 32-bit sector address space.");
                output.SetLength(checked(at + length));
            }
            if (length != entry.Size || at != entry.Offset)
            {
                int row = 32 * entry.DirectoryIndex;
                ushort streamSize = BitConverter.ToUInt16(_directory, row + 4), diskSize = BitConverter.ToUInt16(_directory, row + 6);
                if (length != entry.Size && streamSize != 0 && diskSize != 0 && streamSize != diskSize)
                    throw new InvalidDataException($"Cannot resize {entry.Name}: its streaming and stored sizes differ.");
                Span<byte> header = stackalloc byte[8];
                _directory.AsSpan(row, 8).CopyTo(header);
                BitConverter.TryWriteBytes(header, checked((uint)(at / sector)));
                if (length != entry.Size)
                {
                    if (streamSize != 0 || diskSize == 0) BitConverter.TryWriteBytes(header[4..], (ushort)sectors);
                    if (diskSize != 0) BitConverter.TryWriteBytes(header[6..], (ushort)sectors);
                }
                output.Position = 8L + row;
                output.Write(header);
            }
            output.Position = at;
            output.Write(data);
            if (length > data.Length) output.Write(new byte[length - data.Length]);
        }
    }
}
