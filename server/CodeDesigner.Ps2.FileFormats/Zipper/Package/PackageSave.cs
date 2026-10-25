using System.Text;
using System.Text.RegularExpressions;
using CodeDesigner.Ps2.FileFormats.Zipper.ZDB;

namespace CodeDesigner.Ps2.FileFormats.Zipper.Package
{
    /// <summary>
    /// Writing an edited package: back into a .ZDB (or the loose folder it came from), or as a memory-card pack
    /// (.CDB) the game mounts from <c>mc0:/BASCUS-97275&lt;NAME&gt;/&lt;NAME&gt;.CDB</c>. The CDB layout is the one
    /// Mods/SOCOM2/bots/build_charpack.py (cdb_write) writes, confirmed in game with <c>loadpack</c>.
    /// </summary>
    public static class PackageSave
    {
        /// <summary>
        /// Writes the package with <paramref name="leaves"/> replaced. A ZDB goes to <paramref name="outputPath"/>;
        /// writing over the source first keeps <c>&lt;name&gt;.bak</c> (once). A loose folder writes the files in place
        /// (each backed up the same way) when <paramref name="outputPath"/> is null. Returns the paths written.
        /// </summary>
        public static List<string> Write(ZipperPackage pkg, IEnumerable<string> leaves, string? outputPath)
        {
            var written = new List<string>();
            var targets = leaves.Select(l => pkg.Entry(l) ?? throw new KeyNotFoundException($"No root entry {l}")).ToList();
            if (pkg.IsZdb)
            {
                string output = Path.GetFullPath(outputPath ?? pkg.SourcePath);
                using ZdbArchive zdb = ZdbArchive.Open(pkg.SourcePath);
                foreach (var e in targets)
                {
                    int index = IndexOf(zdb, e.Path);
                    zdb.ReplaceEntryData(index, e.Data);
                }
                byte[] bytes = zdb.ToBytes();
                Backup(output);
                File.WriteAllBytes(output, bytes);
                written.Add(output);
                return written;
            }

            string dir = outputPath ?? Path.GetDirectoryName(pkg.SourcePath)!;
            Directory.CreateDirectory(dir);
            foreach (var e in targets)
            {
                string file = Path.Combine(dir, e.Leaf);
                Backup(file);
                File.WriteAllBytes(file, e.Data);
                written.Add(file);
            }
            return written;
        }

        private static int IndexOf(ZdbArchive zdb, string fullPath)
        {
            for (int i = 0; i < zdb.Entries.Count; i++)
                if (string.Equals(zdb.Entries[i].FullPath, fullPath, StringComparison.OrdinalIgnoreCase)) return i;
            throw new KeyNotFoundException($"{fullPath} is not in the ZDB");
        }

        private static void Backup(string file)
        {
            string bak = file + ".bak";
            if (File.Exists(file) && !File.Exists(bak)) File.Copy(file, bak);
        }

        // ------------------------------------------------------------------ memory-card pack

        public const int CdbHeader = 0x110, CdbEntry = 0x70, CdbFirst = 0xA0, CdbAlign = 0x800;

        /// <summary>
        /// Pack names must start with 'm' and be at most 7 letters/digits: the asset-lib loader keeps the directory
        /// only for "mp/m" / "sp/m" paths, and ZDB_Mount builds the card path in a 32-byte stack buffer.
        /// </summary>
        public static void CheckPackName(string name)
        {
            if (!Regex.IsMatch(name, "^[mM][A-Za-z0-9_]{1,6}$"))
                throw new ArgumentException($"Pack name '{name}' must start with 'm' and be at most 7 letters/digits (e.g. mhost)");
        }

        /// <summary>A CDB holding <paramref name="files"/> (archive path → bytes), stored uncompressed.</summary>
        public static byte[] BuildCdb(IReadOnlyList<(string Name, byte[] Data)> files)
        {
            int table = CdbFirst + CdbEntry * files.Count;
            var outBuf = new List<byte>(new byte[(table + CdbAlign - 1) & ~(CdbAlign - 1)]);
            byte[] head = outBuf.ToArray();
            BitConverter.GetBytes(CdbHeader).CopyTo(head, 0);
            BitConverter.GetBytes(1).CopyTo(head, 4);
            BitConverter.GetBytes(2).CopyTo(head, 12);
            BitConverter.GetBytes(files.Count).CopyTo(head, 0x98);
            BitConverter.GetBytes(CdbEntry).CopyTo(head, 0x9C);
            var body = new List<byte>(head);
            for (int i = 0; i < files.Count; i++)
            {
                var (name, data) = files[i];
                byte[] ascii = Encoding.ASCII.GetBytes(name);
                if (ascii.Length >= 0x40) throw new ArgumentException($"Archive name too long: {name}");
                int e = CdbFirst + CdbEntry * i;
                int offset = body.Count;
                Put(body, e, BitConverter.GetBytes(CdbEntry));
                Put(body, e + 4, ascii);
                Put(body, e + 0x44, BitConverter.GetBytes(offset));
                Put(body, e + 0x48, BitConverter.GetBytes(data.Length));
                body.AddRange(data);
                body.AddRange(new byte[(CdbAlign - data.Length % CdbAlign) % CdbAlign]);
            }
            return body.ToArray();
        }

        private static void Put(List<byte> buf, int at, byte[] bytes)
        {
            for (int i = 0; i < bytes.Length; i++) buf[at + i] = bytes[i];
        }

        /// <summary>
        /// Writes <paramref name="leaves"/> of the package as <c>&lt;outputDir&gt;/&lt;NAME&gt;.CDB</c> under their
        /// archive paths (<c>RUN\MP\MP53\MP53_GEO.ZED</c>) and, when <paramref name="folderCard"/> is a PCSX2 folder
        /// memory card, installs it as <c>BASCUS-97275&lt;NAME&gt;/&lt;NAME&gt;.CDB</c>. Returns the paths written.
        /// </summary>
        public static List<string> WritePack(ZipperPackage pkg, IEnumerable<string> leaves, string name, string outputDir, string? folderCard)
        {
            CheckPackName(name);
            string upper = name.ToUpperInvariant();
            var files = leaves.Select(l => pkg.Entry(l) ?? throw new KeyNotFoundException($"No root entry {l}"))
                .Select(e => (Name: e.Path.Contains('\\') ? e.Path : e.Leaf, e.Data)).ToList();
            if (files.Count == 0) throw new InvalidOperationException("Nothing to pack");
            byte[] cdb = BuildCdb(files);
            var written = new List<string>();
            Directory.CreateDirectory(outputDir);
            string path = Path.Combine(outputDir, upper + ".CDB");
            File.WriteAllBytes(path, cdb);
            written.Add(path);
            if (folderCard != null)
            {
                if (!Directory.Exists(folderCard)) throw new DirectoryNotFoundException($"{folderCard} is not a folder memory card");
                string dest = Path.Combine(folderCard, "BASCUS-97275" + upper);
                Directory.CreateDirectory(dest);
                string installed = Path.Combine(dest, upper + ".CDB");
                File.WriteAllBytes(installed, cdb);
                written.Add(installed);
            }
            return written;
        }
    }
}
