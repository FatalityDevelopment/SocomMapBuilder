using System.Text.Json;
using CodeDesigner.Ps2.FileFormats.Zipper.ZDB;
using CodeDesigner.Ps2.FileFormats.Zipper.ZED;

namespace MapBuilder.Server.Services
{
    /// <summary>
    /// Files of a package for the Asset Viewer: add, replace and remove the root entries of a .ZDB package or .CDB
    /// memory-card pack (the archives it holds: .ZED, .ZAR, .MPS), merge another package's files in, and add,
    /// replace or remove the records of a .ZAR / .ZED archive (an .RDR in READERM.ZAR, say) at any depth. The
    /// result is written to OutputPath (over the source keeps &lt;name&gt;.bak, once), never half-written.
    /// </summary>
    internal sealed class PackageFilesService : ICommandService
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        // What a package holds at its root (retail ZDBs: ZED, ZAR, AIMAPS.MPS).
        private static readonly string[] RootTypes = { ".zed", ".zar", ".mps" };

        public void Register(CommandRegistry reg)
        {
            reg.Add(c => Run("PACKAGE_EDIT", () => Edit(c.InputPath, c.OutputPath, c.Data)), "PACKAGE_EDIT");
        }

        private static string Run(string command, Func<object> body)
        {
            try { return Responses.Json(new CommandResponse { Success = true, Command = command, Data = JsonSerializer.Serialize(body(), JsonOptions) }); }
            catch (Exception ex) { return Responses.Error($"{command}: {ex.Message}"); }
        }

        private sealed class Report
        {
            public List<string> Added { get; } = new();
            public List<string> Replaced { get; } = new();
            public List<string> Removed { get; } = new();
        }

        /// <summary>
        /// Data: a list of operations.
        /// Package files: {op: "add" | "replace", path, file}, {op: "remove", path}, {op: "merge", file (.ZDB / .CDB), replace?}.
        /// Archive records: the same ops with "inside": the archive to change — in a package, the root entry's path
        /// first, then nested archive records [{name, index}]; in a loose .ZED / .ZAR, only the records (or nothing
        /// for the file itself). "add" takes {name?, file}; "replace" / "remove" take {index, name}.
        /// </summary>
        private static object Edit(string? input, string? output, string? json)
        {
            if (string.IsNullOrWhiteSpace(input) || !File.Exists(input)) throw new ArgumentException("InputPath (a .ZDB, .CDB, .ZAR or .ZED) is required");
            if (string.IsNullOrWhiteSpace(json)) throw new ArgumentException("Data (the operations) is required");
            string target = Path.GetFullPath(string.IsNullOrWhiteSpace(output) ? input : output);
            if (!Path.GetExtension(target).Equals(Path.GetExtension(input), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"OutputPath must be a {Path.GetExtension(input).ToUpperInvariant()} like the source");
            using var doc = JsonDocument.Parse(json);
            var report = new Report();
            byte[] bytes;

            if (ZdbArchive.IsArchivePath(input))
            {
                using ZdbArchive zdb = ZdbArchive.Open(input);
                foreach (var op in doc.RootElement.EnumerateArray())
                {
                    if (op.TryGetProperty("inside", out var inside) && inside.ValueKind == JsonValueKind.Array && inside.GetArrayLength() > 0)
                    {
                        var chain = inside.EnumerateArray().ToList();
                        string entryPath = Normal(chain[0].GetString() ?? throw new ArgumentException("inside[0] is the package file's path"));
                        int at = IndexOf(zdb, entryPath);
                        if (at < 0) throw new ArgumentException($"{entryPath} is not in the package");
                        zdb.ReplaceEntryData(at, InArchive(zdb.Entries[at].Data, chain.Skip(1).ToList(), op, entryPath, report));
                    }
                    else PackageOp(zdb, op, report);
                }
                if (zdb.EntryCount == 0) throw new InvalidOperationException("A package needs at least one file");
                bytes = zdb.ToBytes();
            }
            else
            {
                bytes = File.ReadAllBytes(input);
                foreach (var op in doc.RootElement.EnumerateArray())
                {
                    var chain = op.TryGetProperty("inside", out var inside) && inside.ValueKind == JsonValueKind.Array ? inside.EnumerateArray().ToList() : new List<JsonElement>();
                    bytes = InArchive(bytes, chain, op, Path.GetFileName(input), report);
                }
            }

            if (File.Exists(target) && !File.Exists(target + ".bak")) File.Copy(target, target + ".bak");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, bytes);
            return new { written = target, added = report.Added, replaced = report.Replaced, removed = report.Removed };
        }

        // ------------------------------------------------------------------ package files (.ZDB / .CDB root entries)

        private static void PackageOp(ZdbArchive zdb, JsonElement op, Report report)
        {
            bool cdb = zdb.IsMemoryCardPack;
            string kind = Str(op, "op") ?? throw new ArgumentException("Each operation needs \"op\"");
            switch (kind)
            {
                case "add":
                case "replace":
                {
                    string path = Normal(Str(op, "path") ?? throw new ArgumentException($"{kind} needs \"path\" (where it goes in the package)"));
                    byte[] data = RootFile(path, File.ReadAllBytes(Str(op, "file") ?? throw new ArgumentException($"{kind} needs \"file\"")), cdb);
                    int at = IndexOf(zdb, path);
                    if (kind == "add" && at >= 0) throw new ArgumentException($"{path} is already in the package (replace it instead)");
                    if (kind == "replace" && at < 0) throw new ArgumentException($"{path} is not in the package");
                    if (at >= 0) { zdb.ReplaceEntryData(at, data); report.Replaced.Add(path); }
                    else { zdb.AddEntry(path, data); report.Added.Add(path); }
                    break;
                }
                case "remove":
                {
                    string path = Normal(Str(op, "path") ?? throw new ArgumentException("remove needs \"path\""));
                    if (zdb.RemoveEntry(path) == 0) throw new ArgumentException($"{path} is not in the package");
                    report.Removed.Add(path);
                    break;
                }
                case "merge":
                {
                    string file = Str(op, "file") ?? throw new ArgumentException("merge needs \"file\" (a .ZDB / .CDB)");
                    bool overwrite = !(op.TryGetProperty("replace", out var r) && r.ValueKind == JsonValueKind.False);
                    using ZdbArchive other = ZdbArchive.Open(file);
                    foreach (var e in other.Entries)
                    {
                        string path = Normal(e.FullPath);
                        if (!RootTypes.Contains(Path.GetExtension(path).ToLowerInvariant())) continue;
                        byte[] data = RootFile(path, e.Data, cdb);
                        int at = IndexOf(zdb, path);
                        if (at >= 0) { if (!overwrite) continue; zdb.ReplaceEntryData(at, data); report.Replaced.Add(path); }
                        else { zdb.AddEntry(path, data); report.Added.Add(path); }
                    }
                    break;
                }
                default:
                    throw new ArgumentException($"Unknown operation '{kind}' (add, replace, remove, merge)");
            }
        }

        /// <summary>A file a package can hold: a root type, a ZED / ZAR that parses, a name the format can store.</summary>
        private static byte[] RootFile(string path, byte[] data, bool cdb)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (!RootTypes.Contains(ext)) throw new ArgumentException($"{Path.GetFileName(path)}: a package holds .ZED, .ZAR and .MPS files (other files go inside a .ZAR / .ZED)");
            if (cdb && path.Length >= 0x40) throw new ArgumentException($"{path} is too long for a memory-card pack (63 characters)");
            return Checked(Path.GetFileName(path), data);
        }

        // ------------------------------------------------------------------ archive records (.ZAR / .ZED, any depth)

        /// <summary>Applies <paramref name="op"/> to the archive <paramref name="chain"/> leads to inside <paramref name="archive"/>; answers its new bytes.</summary>
        private static byte[] InArchive(byte[] archive, List<JsonElement> chain, JsonElement op, string where, Report report)
        {
            ZedContainer c;
            try { c = ZedContainer.FromBytes(archive); }
            catch (Exception ex) { throw new ArgumentException($"{where} is not a ZED/ZAR archive ({ex.Message})"); }
            if (chain.Count > 0)
            {
                int i = Record(c, chain[0], where);
                string inner = $"{where} › {c.Nodes[i].Name}";
                c.ReplaceNodeData(i, InArchive(c.Nodes[i].Data, chain.Skip(1).ToList(), op, inner, report));
                return c.ToBytes();
            }

            string kind = Str(op, "op") ?? throw new ArgumentException("Each operation needs \"op\"");
            switch (kind)
            {
                case "add":
                {
                    string file = Str(op, "file") ?? throw new ArgumentException("add needs \"file\"");
                    string name = Str(op, "name") ?? Path.GetFileName(file);
                    if (string.IsNullOrWhiteSpace(name) || name.Length > 63) throw new ArgumentException($"'{name}' is not a record name (1..63 characters)");
                    if (TopLevel(c).Any(t => c.Nodes[t].Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                        throw new ArgumentException($"{where} already has {name} (replace it instead)");
                    c.InsertNode(c.Nodes.Count, name, Checked(name, File.ReadAllBytes(file)), 0);
                    c.RootValue++;
                    report.Added.Add($"{where} › {name}");
                    break;
                }
                case "replace":
                {
                    int i = Record(c, op, where);
                    string file = Str(op, "file") ?? throw new ArgumentException("replace needs \"file\"");
                    c.ReplaceNodeData(i, Checked(c.Nodes[i].Name, File.ReadAllBytes(file)));
                    report.Replaced.Add($"{where} › {c.Nodes[i].Name}");
                    break;
                }
                case "remove":
                {
                    int i = Record(c, op, where);
                    string name = c.Nodes[i].Name;
                    bool top = ZedTree.ParentIndices(c.Nodes)[i] < 0;
                    ZedTree.Remove(c, i);
                    if (top) c.RootValue--;
                    report.Removed.Add($"{where} › {name}");
                    break;
                }
                default:
                    throw new ArgumentException($"Unknown archive operation '{kind}' (add, replace, remove)");
            }
            return c.ToBytes();
        }

        /// <summary>A record by {index, name}: the index the Viewer listed, checked against the name.</summary>
        private static int Record(ZedContainer c, JsonElement e, string where)
        {
            string? name = Str(e, "name");
            if (e.TryGetProperty("index", out var ix) && ix.ValueKind == JsonValueKind.Number)
            {
                int i = ix.GetInt32();
                if (i >= 0 && i < c.Nodes.Count && (name == null || c.Nodes[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return i;
            }
            if (name != null)
            {
                for (int i = 0; i < c.Nodes.Count; i++)
                    if (c.Nodes[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return i;
            }
            throw new ArgumentException($"{where} has no record {name ?? "(unnamed)"}");
        }

        private static IEnumerable<int> TopLevel(ZedContainer c)
        {
            for (int i = 0; i < c.Nodes.Count; i = ZedTree.SubtreeEnd(c.Nodes, i)) yield return i;
        }

        /// <summary>Nested archives must parse; nothing may be empty.</summary>
        private static byte[] Checked(string name, byte[] data)
        {
            if (data.Length == 0) throw new ArgumentException($"{name} is empty");
            string ext = Path.GetExtension(name).ToLowerInvariant();
            if (ext is ".zed" or ".zar")
            {
                try { ZedContainer.FromBytes(data); }
                catch (Exception ex) { throw new ArgumentException($"{name} is not a ZED/ZAR archive ({ex.Message})"); }
            }
            return data;
        }

        private static int IndexOf(ZdbArchive zdb, string path)
        {
            for (int i = 0; i < zdb.Entries.Count; i++)
                if (Normal(zdb.Entries[i].FullPath).Equals(path, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        private static string Normal(string path) => path.Replace('/', '\\').Trim().TrimStart('\\');

        private static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }
}
