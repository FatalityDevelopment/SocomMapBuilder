using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using CodeDesigner.Ps2.FileFormats.Zipper.ZDB;

namespace CodeDesigner.Ps2.FileFormats.Zipper.Ca
{
    /// <summary>
    /// The CA disc's RUN folder: each location is one archive named by the CRC32 of <c>RUN\&lt;LOCATION&gt;</c>
    /// (<c>RUN\POLAND_RURAL</c> → <c>055146D2.ZAR</c>). The location names come from <c>GLOBAL.ZDB › missionlist.rdr</c>,
    /// which lists every location folder, so a folder listing can name the archives and a location or mission path
    /// (<c>G:\RUN\BROKEN_ISLANDS</c>, <c>G:\RUN\BROKEN_ISLANDS\SP414.ZDB</c>) opens its archive.
    /// </summary>
    public static class CaDisc
    {
        private static readonly ConcurrentDictionary<string, (DateTime Time, IReadOnlyDictionary<string, string> Names)> Cache = new(StringComparer.OrdinalIgnoreCase);

        private static readonly Regex LocationString = new(@"RUN\\([A-Z0-9_]+)\\", RegexOptions.Compiled);

        /// <summary>
        /// The location archives present in <paramref name="runDir"/>: file name (<c>055146D2.ZAR</c>) → location
        /// (<c>POLAND_RURAL</c>). Empty when the folder has no CA GLOBAL.ZDB. Cached by GLOBAL.ZDB's write time.
        /// </summary>
        public static IReadOnlyDictionary<string, string> Locations(string runDir)
        {
            string global = Path.Combine(runDir, "GLOBAL.ZDB");
            if (!File.Exists(global)) return new Dictionary<string, string>();
            var time = File.GetLastWriteTimeUtc(global);
            if (Cache.TryGetValue(runDir, out var hit) && hit.Time == time) return hit.Names;
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                byte[] data = File.ReadAllBytes(global);
                if (data.Length >= 8 && BitConverter.ToUInt32(data, 0) == 0xC8 && BitConverter.ToUInt32(data, 4) == 0x58)
                {
                    using var zdb = ZdbArchive.FromBytes(data);
                    foreach (var e in zdb.Entries.Where(e => e.Tag == "MSNl"))
                        foreach (Match m in LocationString.Matches(System.Text.Encoding.ASCII.GetString(e.Data)))
                        {
                            string file = CaPackage.ArchiveName(m.Groups[1].Value);
                            if (File.Exists(Path.Combine(runDir, file))) names.TryAdd(file, m.Groups[1].Value);
                        }
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { }
            Cache[runDir] = (time, names);
            return names;
        }

        /// <summary>
        /// A location or mission path that is not on disk (<c>…\RUN\WHARF</c>, <c>…\RUN\WHARF\SP422.ZDB</c>, or with
        /// forward slashes) as its archive and mission id (null for a location); null when it names neither.
        /// </summary>
        public static (string Archive, string? Mission)? Resolve(string path)
        {
            var parts = path.Replace('/', '\\').TrimEnd('\\').Split('\\');
            for (int i = parts.Length - 1; i >= 1; i--)
            {
                if (!parts[i - 1].Equals("RUN", StringComparison.OrdinalIgnoreCase) || i < parts.Length - 2) continue;
                string run = string.Join('\\', parts.Take(i));
                string archive = Path.Combine(run, CaPackage.ArchiveName(parts[i]));
                if (!File.Exists(archive)) return null;
                string? mission = i + 1 < parts.Length ? Path.GetFileNameWithoutExtension(parts[i + 1]).ToUpperInvariant() : null;
                return (archive, mission);
            }
            return null;
        }
    }
}
