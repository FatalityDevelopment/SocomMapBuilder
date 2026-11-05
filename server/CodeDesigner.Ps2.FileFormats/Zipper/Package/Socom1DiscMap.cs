using CodeDesigner.Ps2.FileFormats.Zipper.ZED;

namespace CodeDesigner.Ps2.FileFormats.Zipper.Package
{
    /// <summary>
    /// A SOCOM U.S. Navy SEALs (SOCOM 1, SCUS-97134) map as the disc holds it: loose files in <c>RUN\MP\MP1\</c> or
    /// <c>RUN\SP\M5\</c>, with no .ZDB. The map's own <c>&lt;MAP&gt;.ZED</c> lists the libraries it loads under
    /// <c>assetlibs</c> (<c>//mp/mp1/clib</c>, <c>//common/assetlib/weapons</c> …), each a folder under <c>RUN\</c>.
    /// The archives inside are SOCOM II's formats under other names:
    /// <list type="bullet">
    /// <item><c>&lt;NS&gt;_MDL.ZED</c> holds the scene definitions (SOCOM II's <c>&lt;NS&gt;_GEO.ZED</c>);</item>
    /// <item><c>MODELS.ZAR</c> holds the model geometry (SOCOM II's <c>&lt;NS&gt;_MDL.ZED</c>), the map's own one
    /// with the <c>worldmodel</c> (SOCOM II keeps it in <c>WORL_MDL.ZED</c>);</item>
    /// <item><c>_TXR</c> / <c>_PAL</c>, <c>CLUTTER.ZAR</c>, <c>READERM.ZAR</c>, <c>MZANIM.ZAR</c> keep their names.</item>
    /// </list>
    /// The package indexes each file under its SOCOM II leaf name (<see cref="ZipperPackage.RootEntry.Leaf"/>) and
    /// keeps its disc path (<see cref="ZipperPackage.RootEntry.Path"/>), which saving writes back to.
    /// </summary>
    public static class Socom1DiscMap
    {
        /// <summary>
        /// The map's own <c>&lt;MAP&gt;.ZED</c> when <paramref name="path"/> is a SOCOM 1 map folder (or that file, or a
        /// file directly in that folder); null otherwise. A map folder holds <c>&lt;FOLDER&gt;.ZED</c> with an
        /// <c>assetlibs</c> list and an <c>ASSETLIB</c> subfolder.
        /// </summary>
        public static string? FindMapZed(string path)
        {
            string? dir = Directory.Exists(path) ? path : File.Exists(path) ? Path.GetDirectoryName(Path.GetFullPath(path)) : null;
            if (dir == null) return null;
            dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
            string name = Path.GetFileName(dir);
            if (string.IsNullOrEmpty(name)) return null;
            string zed = Path.Combine(dir, name + ".ZED");
            if (!File.Exists(zed) || !Directory.Exists(Path.Combine(dir, "ASSETLIB"))) return null;
            byte[] data = File.ReadAllBytes(zed);
            // The folder is laid out as a SOCOM 1 map, so an unreadable map file is a damaged copy, not another layout.
            if (LooksBlank(data)) throw new InvalidDataException($"{zed} is blank (all zeros): this copy of the disc is damaged");
            try { return AssetLibs(data).Count > 0 ? zed : null; }
            catch (InvalidDataException ex) { throw new InvalidDataException($"{zed} cannot be read ({ex.Message}): this copy of the disc may be damaged", ex); }
        }

        /// <summary>
        /// True when a .ZED / .ZAR's header is all zeros, as files read from bad disc sectors come back (a container
        /// always has a slot count and a string table). Damage further into a file is not detected here.
        /// </summary>
        public static bool LooksBlank(byte[] data) => data.Length > 0 && !data.AsSpan(0, Math.Min(data.Length, 0x64)).ContainsAnyExcept((byte)0);

        /// <summary>The <c>assetlibs</c> children of a map .ZED, as written (<c>//mp/mp1/flib</c>).</summary>
        public static IReadOnlyList<string> AssetLibs(byte[] mapZed)
        {
            var nodes = ZedContainer.FromBytes(mapZed).Nodes;
            int at = -1;
            for (int i = 0; i < nodes.Count; i++)
                if (nodes[i].Name.Equals("assetlibs", StringComparison.OrdinalIgnoreCase)) { at = i; break; }
            if (at < 0) return Array.Empty<string>();
            var libs = new List<string>();
            int count = (int)nodes[at].Type;
            for (int i = at + 1; i < nodes.Count && libs.Count < count; i++)
                if (nodes[i].Name.StartsWith("//", StringComparison.Ordinal)) libs.Add(nodes[i].Name);
            return libs;
        }

        /// <summary>The <c>RUN</c> folder above a map folder (<c>RUN\MP\MP1</c> → <c>RUN</c>); two levels up otherwise.</summary>
        public static string RunRoot(string mapDir)
        {
            for (DirectoryInfo? d = new DirectoryInfo(mapDir); d != null; d = d.Parent)
                if (d.Name.Equals("RUN", StringComparison.OrdinalIgnoreCase)) return d.FullName;
            return new DirectoryInfo(mapDir).Parent?.Parent?.FullName ?? mapDir;
        }

        /// <summary>
        /// A library path from <c>assetlibs</c> (lowercase) as a folder under <paramref name="runRoot"/>, spelled as on
        /// disk (<c>COMMON\ASSETLIB\WEAPONS</c>) so saved copies keep the disc's names. Unmatched parts stay as written.
        /// </summary>
        public static string LibraryFolder(string runRoot, string lib)
        {
            string path = runRoot;
            foreach (string part in lib.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                string? real = Directory.Exists(path)
                    ? Directory.GetDirectories(path).FirstOrDefault(d => Path.GetFileName(d).Equals(part, StringComparison.OrdinalIgnoreCase))
                    : null;
                path = real ?? Path.Combine(path, part);
            }
            return path;
        }

        /// <summary>
        /// The map's files as (disc path, SOCOM II leaf): the map folder's own files, then each library's, in the
        /// <c>assetlibs</c> order. A library missing on disk is listed in <paramref name="missing"/>. When two files
        /// would share a leaf, the first is kept.
        /// </summary>
        public static List<(string Path, string Leaf)> Files(string mapZed, List<string> missing)
        {
            string mapDir = Path.GetDirectoryName(Path.GetFullPath(mapZed))!;
            string run = RunRoot(mapDir);
            var files = new List<(string Path, string Leaf)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Add(string path, string leaf)
            {
                if (seen.Add(leaf)) files.Add((path, leaf));
            }

            foreach (string f in Directory.GetFiles(mapDir).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                Add(f, Path.GetFileName(f));

            foreach (string lib in AssetLibs(File.ReadAllBytes(mapZed)))
            {
                string folder = LibraryFolder(run, lib);
                if (!Directory.Exists(folder)) { missing.Add(lib); continue; }
                foreach (var (path, leaf) in LibraryFiles(folder)) Add(path, leaf);
            }
            return files;
        }

        /// <summary>One library folder's files under their SOCOM II leaves (<c>MODELS.ZAR</c> → <c>&lt;NS&gt;_MDL.ZED</c>).</summary>
        public static IEnumerable<(string Path, string Leaf)> LibraryFiles(string folder)
        {
            string[] all = Directory.GetFiles(folder).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
            string? ns = all.Select(f => Path.GetFileName(f))
                .Select(f => System.Text.RegularExpressions.Regex.Match(f, @"^(?<ns>.+?)_(MDL|TXR|PAL)\.ZED$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                .Where(m => m.Success).Select(m => m.Groups["ns"].Value.ToUpperInvariant()).FirstOrDefault();
            foreach (string f in all)
            {
                string leaf = Path.GetFileName(f);
                if (ns != null && leaf.Equals("MODELS.ZAR", StringComparison.OrdinalIgnoreCase)) yield return (f, ns + "_MDL.ZED");
                else if (ns != null && leaf.Equals(ns + "_MDL.ZED", StringComparison.OrdinalIgnoreCase)) yield return (f, ns + "_GEO.ZED");
                else yield return (f, leaf);
            }
        }

        /// <summary>
        /// Where a saved file goes: its disc path when <paramref name="outputDir"/> is null, else the same path under
        /// <paramref name="outputDir"/> from <c>RUN\</c> down (<c>out\RUN\MP\MP1\ASSETLIB\MP1\MP1_MDL.ZED</c>).
        /// </summary>
        public static string SaveTarget(string runRoot, string discPath, string? outputDir)
        {
            if (outputDir == null) return discPath;
            string root = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(runRoot)))!;
            return Path.Combine(outputDir, Path.GetRelativePath(root, discPath));
        }
    }
}
