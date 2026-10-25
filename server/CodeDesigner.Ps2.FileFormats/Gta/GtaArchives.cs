using System.Text;

namespace CodeDesigner.Ps2.FileFormats.Gta
{
    /// <summary>A read-only view of a file: a span of a disc image or a loose file (opened per read, never held).</summary>
    public sealed record FileSpan(string Path, long Offset, long Length)
    {
        public byte[] Read(long at = 0, long? count = null)
        {
            long n = Math.Min(count ?? Length - at, Length - at);
            var b = new byte[n];
            using var f = File.OpenRead(Path);
            f.Seek(Offset + at, SeekOrigin.Begin);
            f.ReadExactly(b);
            return b;
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
        public sealed record Entry(string Name, long Offset, long Size);

        public FileSpan Source { get; }
        public IReadOnlyList<Entry> Entries { get; }
        private readonly Dictionary<string, Entry> _byName;

        public GtaImg(FileSpan source)
        {
            Source = source;
            byte[] head = source.Read(0, 8);
            if (Encoding.ASCII.GetString(head, 0, 4) != "VER2") throw new InvalidDataException($"{source.Path}: not a VER2 IMG archive");
            int n = BitConverter.ToInt32(head, 4);
            byte[] dir = source.Read(8, 32L * n);
            var list = new List<Entry>(n);
            for (int i = 0; i < n; i++)
            {
                int o = 32 * i;
                uint offset = BitConverter.ToUInt32(dir, o);
                ushort streaming = BitConverter.ToUInt16(dir, o + 4), archive = BitConverter.ToUInt16(dir, o + 6);
                int end = Array.IndexOf(dir, (byte)0, o + 8, 24);
                string name = Encoding.ASCII.GetString(dir, o + 8, (end < 0 ? o + 32 : end) - (o + 8));
                list.Add(new Entry(name, offset * 2048L, (streaming != 0 ? streaming : archive) * 2048L));
            }
            Entries = list;
            _byName = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in list) _byName.TryAdd(e.Name, e);
        }

        public Entry? Find(string name) => _byName.TryGetValue(name, out var e) ? e : null;

        public byte[] Read(Entry e) => Source.Read(e.Offset, e.Size);

        public byte[]? Read(string name) => Find(name) is { } e ? Read(e) : null;
    }
}
