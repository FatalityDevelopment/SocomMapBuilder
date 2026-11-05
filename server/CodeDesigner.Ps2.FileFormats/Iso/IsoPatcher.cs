using System.Text;

namespace CodeDesigner.Ps2.FileFormats.Iso
{
    /// <summary>
    /// Writes a copy of an ISO 9660 disc image with some of its files replaced. A file that still fits the sectors it
    /// had is written in place (and its size updated); a file that grew moves to the end of the image, its directory
    /// records (in the ISO 9660 tree and any Joliet tree) point at the new place, and the volume's size grows to match.
    /// Every other byte of the image is copied as it is. The PS2 and its emulators find files through the ISO 9660
    /// tree; a UDF tree, when the image has one, still describes the old files (reported as a warning).
    /// </summary>
    public static class IsoPatcher
    {
        public const int Sector = 2048;
        /// <summary>A single-layer DVD's capacity in bytes (larger images need a dual-layer disc or an emulator).</summary>
        public const long SingleLayerBytes = 4_700_372_992;

        /// <summary>A file in the image: its path (upper case, '/' separated, no version), extent and size, and every
        /// directory record that points at it (absolute byte offsets in the image).</summary>
        public sealed record Entry(string Path, uint Lba, uint Size, List<long> Records);

        public sealed record Result(string Path, List<(string DiscPath, bool Moved)> Files, List<string> Warnings);

        /// <summary>Every file of the image's ISO 9660 tree, with its Joliet records joined by extent.</summary>
        public static Dictionary<string, Entry> Entries(string iso)
        {
            using var f = File.OpenRead(iso);
            var entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            var byLba = new Dictionary<uint, Entry>();
            var joliet = new List<(uint Lba, long Record)>();
            var sector = new byte[Sector];
            for (int vd = 16; vd < 64; vd++)
            {
                f.Seek((long)vd * Sector, SeekOrigin.Begin);
                if (f.Read(sector, 0, Sector) < Sector || Encoding.ASCII.GetString(sector, 1, 5) != "CD001") break;
                int type = sector[0];
                if (type == 255) break;
                if (type != 1 && type != 2) continue;
                uint rootLba = BitConverter.ToUInt32(sector, 156 + 2), rootSize = BitConverter.ToUInt32(sector, 156 + 10);
                bool primary = type == 1;
                Walk(f, rootLba, rootSize, "", 0, (path, lba, size, record) =>
                {
                    if (primary)
                    {
                        var e = new Entry(path, lba, size, new List<long> { record });
                        entries[path] = e;
                        byLba.TryAdd(lba, e);
                    }
                    else joliet.Add((lba, record));
                });
            }
            if (entries.Count == 0) throw new InvalidDataException($"{iso} is not an ISO 9660 image");
            foreach (var (lba, record) in joliet)
                if (byLba.TryGetValue(lba, out var e)) e.Records.Add(record);
            return entries;
        }

        private static void Walk(FileStream f, uint lba, uint size, string path, int depth, Action<string, uint, uint, long> file)
        {
            if (depth > 16 || size > 1 << 24) return;
            var data = new byte[size];
            long start = (long)lba * Sector;
            f.Seek(start, SeekOrigin.Begin);
            f.ReadExactly(data);
            int o = 0;
            while (o < data.Length)
            {
                int len = data[o];
                if (len == 0) { o = (o / Sector + 1) * Sector; continue; }
                if (o + 33 > data.Length) break;
                uint ext = BitConverter.ToUInt32(data, o + 2), sz = BitConverter.ToUInt32(data, o + 10);
                int flags = data[o + 25], nameLen = data[o + 32];
                if (o + 33 + nameLen > data.Length) break;
                bool self = nameLen == 1 && (data[o + 33] == 0 || data[o + 33] == 1);
                if (!self)
                {
                    // ISO 9660 names are ASCII; Joliet's are UCS-2 big endian.
                    string name = nameLen % 2 == 0 && nameLen > 0 && data[o + 33] == 0
                        ? Encoding.BigEndianUnicode.GetString(data, o + 33, nameLen) : Encoding.ASCII.GetString(data, o + 33, nameLen);
                    string p = (path.Length == 0 ? "" : path + "/") + name.Split(';')[0].TrimEnd('.').ToUpperInvariant();
                    if ((flags & 2) != 0) Walk(f, ext, sz, p, depth + 1, file);
                    else file(p, ext, sz, start + o);
                }
                o += len;
            }
        }

        /// <summary>
        /// Writes <paramref name="output"/>: <paramref name="source"/> with each disc path's file replaced by the given
        /// file on disk. Never writes over the source; a failed write leaves no output behind.
        /// </summary>
        public static Result Patch(string source, string output, IReadOnlyDictionary<string, string> replacements)
        {
            string src = System.IO.Path.GetFullPath(source), dst = System.IO.Path.GetFullPath(output);
            if (src.Equals(dst, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Write the patched disc to a new file, not over the source image.");
            var entries = Entries(src);
            var plan = new List<(Entry Entry, string File)>();
            foreach (var (path, file) in replacements)
            {
                string key = path.Replace('\\', '/').Trim('/').ToUpperInvariant();
                plan.Add((entries.TryGetValue(key, out var e) ? e : throw new KeyNotFoundException($"{path} is not on the disc image"), file));
            }
            var warnings = new List<string>();
            long sourceLength = new FileInfo(src).Length;
            long grow = plan.Sum(p => new FileInfo(p.File).Length > Allocated(p.Entry.Size) ? Round(new FileInfo(p.File).Length) : 0);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dst)!);
            var drive = new DriveInfo(System.IO.Path.GetPathRoot(dst)!);
            if (drive.IsReady && drive.AvailableFreeSpace < sourceLength + grow)
                throw new IOException($"The patched disc needs {(sourceLength + grow) >> 20:N0} MB; {drive.Name} has {drive.AvailableFreeSpace >> 20:N0} MB free.");

            string temp = dst + ".tmp";
            var files = new List<(string, bool)>();
            try
            {
                File.Copy(src, temp, true);
                using var f = new FileStream(temp, FileMode.Open, FileAccess.ReadWrite);
                long end = Round(f.Length);
                foreach (var (entry, file) in plan)
                {
                    byte[] data = File.ReadAllBytes(file);
                    bool moved = data.Length > Allocated(entry.Size);
                    long at = moved ? end : (long)entry.Lba * Sector;
                    f.Position = at;
                    f.Write(data);
                    // Zero the rest of the last sector (and, in place, the sectors the old file used beyond the new one).
                    long pad = (moved ? Round(data.Length) : Math.Max(Round(data.Length), Allocated(entry.Size))) - data.Length;
                    if (pad > 0) f.Write(new byte[pad]);
                    if (moved) end = at + Round(data.Length);
                    uint lba = (uint)(at / Sector);
                    foreach (long record in entry.Records) WriteRecord(f, record, lba, (uint)data.Length);
                    files.Add((entry.Path, moved));
                }
                if (f.Length < end) f.SetLength(end);
                // The primary volume's size (sectors), both byte orders.
                WriteBoth(f, 16L * Sector + 80, (uint)(f.Length / Sector));
                if (HasUdf(f) && files.Any(x => x.Item2))
                    warnings.Add("The image also has a UDF file system, which still lists the moved files at their old places; the PS2 and PCSX2 read the ISO 9660 tree.");
                if (f.Length > SingleLayerBytes)
                    warnings.Add($"The patched image is {f.Length >> 20:N0} MB, more than a single-layer DVD holds; it runs in an emulator.");
            }
            catch
            {
                try { File.Delete(temp); } catch (IOException) { }
                throw;
            }
            File.Move(temp, dst, true);
            return new Result(dst, files, warnings);
        }

        private static long Allocated(uint size) => Round(size);
        private static long Round(long n) => (n + Sector - 1) / Sector * Sector;

        private static void WriteRecord(FileStream f, long record, uint lba, uint size)
        {
            WriteBoth(f, record + 2, lba);
            WriteBoth(f, record + 10, size);
        }

        private static void WriteBoth(FileStream f, long at, uint value)
        {
            var b = new byte[8];
            BitConverter.TryWriteBytes(b.AsSpan(0, 4), value);
            b[4] = (byte)(value >> 24); b[5] = (byte)(value >> 16); b[6] = (byte)(value >> 8); b[7] = (byte)value;
            f.Position = at;
            f.Write(b);
        }

        private static bool HasUdf(FileStream f)
        {
            var b = new byte[6];
            for (int vd = 16; vd < 32; vd++)
            {
                f.Position = (long)vd * Sector;
                if (f.Read(b, 0, 6) < 6) break;
                string id = Encoding.ASCII.GetString(b, 1, 5);
                if (id is "NSR02" or "NSR03" or "BEA01") return true;
            }
            return false;
        }
    }
}
