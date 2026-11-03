using System.Text.RegularExpressions;
using CodeDesigner.Ps2.FileFormats.Imaging;
using CodeDesigner.Ps2.FileFormats.Ps2;
using CodeDesigner.Ps2.FileFormats.Zipper.Anim;
using CodeDesigner.Ps2.FileFormats.Zipper.MPS;
using CodeDesigner.Ps2.FileFormats.Zipper.Package;
using CodeDesigner.Ps2.FileFormats.Zipper.RDR;
using CodeDesigner.Ps2.FileFormats.Zipper.Texture;
using CodeDesigner.Ps2.FileFormats.Zipper.ZDB;
using CodeDesigner.Ps2.FileFormats.Zipper.ZED;

namespace MapBuilder.Server.Services
{
    /// <summary>
    /// The Asset Viewer: browse any file of a SOCOM II disc (or an unpacked package) and preview what it holds.
    /// A place is a path on disk plus the entry indices that lead into it (a .ZDB's root entry, then a record of that
    /// archive, and so on). Archives of a package (models, textures, palettes, GEO, animation clips) list what the
    /// editors list, from an asset session the viewer opens, so previews use the editors' own mesh and texture
    /// decoding (ASSET_MODEL_MESH, ASSET_TEXTURE ...). Everything else is decoded here: sounds, icons, screens, text.
    /// </summary>
    internal sealed partial class ZipperAssetService
    {
        // Sessions the viewer opened for browsing, oldest first. An editor's ASSET_OPEN of the same package takes it
        // over; the rest are closed (when clean) once the viewer has moved on past a couple of packages.
        private static readonly List<string> ViewerOwned = new();
        private const int ViewerKeep = 2;
        // Loose archives up to this size are read whole; bigger ones (the sound stores) through their record table.
        private const long ViewerReadWhole = 64L << 20;
        private const int HexBytes = 4096;

        private static readonly Regex FamilyRx = new(@"^(?<ns>.+?)_(?<kind>MDL|TXR|PAL|GEO)(?:\([^)]+\))?\.ZED$", RegexOptions.IgnoreCase);

        private sealed record ViewerRequest(int[]? Inner, int? Sample, int? Rate, string? Session, string? Archive, int? Palette, int? Effect);

        // EntryPath: a .ZDB root entry's path in the package (what PACKAGE_EDIT names it by).
        private sealed record ViewerNode(string Name, long Size, Func<byte[]> Bytes, byte[]? Loaded, string? EntryPath = null);

        private sealed class Place
        {
            public required string Path { get; init; }
            public required int[] Inner { get; init; }
            public required string Name { get; init; }
            public required long Size { get; init; }
            public required Func<byte[]> Bytes { get; init; }
            public byte[]? Loaded { get; init; }
        }

        private static void RegisterViewer(CommandRegistry reg)
        {
            reg.Add(c => Responses.Run("VIEWER_LIST", () => ViewerList(c.InputPath, Parse<ViewerRequest>(c.AssetJson))), "VIEWER_LIST");
            reg.Add(c => Responses.Run("VIEWER_PREVIEW", () => ViewerPreview(c.InputPath, Parse<ViewerRequest>(c.AssetJson))), "VIEWER_PREVIEW");
            RegisterViewerEdits(reg);
            reg.Add(c => Responses.Run("VIEWER_EXTRACT", () => ViewerExtract(c.InputPath, Parse<ViewerRequest>(c.AssetJson), c.OutputPath)), "VIEWER_EXTRACT");
        }

        // ------------------------------------------------------------------ places

        private static string FullPathOf(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("InputPath (a folder or file) is required");
            string full = Path.GetFullPath(path);
            if (!File.Exists(full) && !Directory.Exists(full)) throw new FileNotFoundException($"Not found: {full}", full);
            return full;
        }

        private static bool IsContainerFile(string path) =>
            path.EndsWith(".zed", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".zar", StringComparison.OrdinalIgnoreCase);

        private static Place Resolve(string full, int[]? inner)
        {
            inner ??= Array.Empty<int>();
            long size = new FileInfo(full).Length;
            var place = new Place { Path = full, Inner = Array.Empty<int>(), Name = System.IO.Path.GetFileName(full), Size = size, Bytes = () => File.ReadAllBytes(full) };
            // A loose file edited in its folder's session reads as edited.
            if (!ZdbArchive.IsArchivePath(full) && EditedSessionOf(full)?.Package.Entry(place.Name) is { } edited)
            {
                byte[] data = edited.Data;
                place = new Place { Path = full, Inner = Array.Empty<int>(), Name = place.Name, Size = data.Length, Bytes = () => data, Loaded = data };
            }
            for (int depth = 0; depth < inner.Length; depth++)
            {
                var children = Children(place) ?? throw new InvalidDataException($"{place.Name} is not an archive");
                int i = inner[depth];
                if (i < 0 || i >= children.Count) throw new KeyNotFoundException($"{place.Name} has no entry #{i}");
                var child = children[i];
                place = new Place { Path = full, Inner = inner[..(depth + 1)], Name = child.Name, Size = child.Size, Bytes = child.Bytes, Loaded = child.Loaded };
            }
            return place;
        }

        // The records of an archive place, or null when it is not one.
        private static IReadOnlyList<ViewerNode>? Children(Place p)
        {
            if (p.Inner.Length == 0 && ZdbArchive.IsArchivePath(p.Path)) return ZdbNodes(p.Path);
            if (p.Inner.Length == 0 && IsContainerFile(p.Path) && p.Loaded == null) return LooseNodes(p.Path);
            byte[] data = p.Loaded ?? (p.Size <= ViewerReadWhole ? p.Bytes() : Array.Empty<byte>());
            if (!LooksLikeContainer(data)) return null;
            try { return ZedContainer.FromBytes(data).Nodes.Select(n => new ViewerNode(n.Name, n.Size, () => n.Data, n.Data)).ToList(); }
            catch (InvalidDataException) { return null; }
        }

        private static readonly Dictionary<string, (DateTime Stamp, List<ViewerNode> Nodes)> NodeCache = new(StringComparer.OrdinalIgnoreCase);

        private static List<ViewerNode> Cached(string path, Func<List<ViewerNode>> read)
        {
            DateTime stamp = File.GetLastWriteTimeUtc(path);
            lock (NodeCache)
            {
                if (NodeCache.TryGetValue(path, out var hit) && hit.Stamp == stamp) return hit.Nodes;
            }
            var nodes = read();
            lock (NodeCache)
            {
                // A few archives: the one being browsed and the ones just above it.
                if (NodeCache.Count >= 4) NodeCache.Remove(NodeCache.Keys.First());
                NodeCache[path] = (stamp, nodes);
            }
            return nodes;
        }

        // A package's files in the ZDB's own order (record positions must not change once it has edits), with the
        // bytes its session holds when it has edits; files the session added come last.
        private static List<ViewerNode> ZdbNodes(string path)
        {
            var disk = DiskZdbNodes(path);
            if (EditedSessionOf(path) is not { } s) return disk;
            lock (s.Gate)
            {
                var byPath = s.Package.Entries.GroupBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
                var list = disk.Select(n => n.EntryPath != null && byPath.TryGetValue(n.EntryPath, out var e) ? Node(e) : n).ToList();
                var known = new HashSet<string>(disk.Select(n => n.EntryPath ?? ""), StringComparer.OrdinalIgnoreCase);
                list.AddRange(s.Package.Entries.Where(e => !known.Contains(e.Path)).Select(Node));
                return list;
            }
            static ViewerNode Node(ZipperPackage.RootEntry e) { byte[] data = e.Data; return new ViewerNode(e.Leaf, data.Length, () => data, data, e.Path); }
        }

        private static List<ViewerNode> DiskZdbNodes(string path) => Cached(path, () =>
        {
            using var zdb = ZdbArchive.Open(path);
            return zdb.Entries.Select(e =>
            {
                string leaf = e.FullPath.Split('\\', '/').Last();
                byte[] data = e.Data;
                return new ViewerNode(leaf, data.Length, () => data, data, e.FullPath);
            }).ToList();
        });

        private static List<ViewerNode> LooseNodes(string path) => Cached(path, () =>
        {
            if (new FileInfo(path).Length <= ViewerReadWhole)
                return ZedContainer.FromBytes(File.ReadAllBytes(path)).Nodes.Select(n => new ViewerNode(n.Name, n.Size, () => n.Data, n.Data)).ToList();
            var index = ZedIndex.Open(path);
            return index.Entries.Select(e => new ViewerNode(e.Name, e.Size, () => index.Read(e.Index), null)).ToList();
        });

        // The ZED / ZAR header: a slot count and the 0x00020002 word at 0x60 (only the head may be at hand).
        private static bool LooksLikeContainer(ReadOnlySpan<byte> d) =>
            d.Length >= 0x74 && BitConverter.ToUInt32(d[0x60..]) == 0x00020002 && BitConverter.ToUInt32(d[4..]) is > 0 and < 1_000_000;

        private static byte[] Head(Place p, int count)
        {
            if (p.Loaded != null) return p.Loaded.Length <= count ? p.Loaded : p.Loaded[..count];
            if (p.Inner.Length == 0)
            {
                using var fs = new FileStream(p.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var buf = new byte[Math.Min(count, fs.Length)];
                fs.ReadExactly(buf);
                return buf;
            }
            byte[] all = p.Bytes();
            return all.Length <= count ? all : all[..count];
        }

        // ------------------------------------------------------------------ kinds

        /// <summary>What a file or record is, from its name and (when known) its first bytes.</summary>
        private static string KindOf(string name, ReadOnlySpan<byte> head)
        {
            string kind = KindByName(name);
            // A record named after an archive need not be one (UI/DBUFFER.ZAR holds each archive's buffer size).
            if (kind is "models" or "textures" or "palettes" or "geo" or "clips" or "archive" && head.Length > 0 && !LooksLikeContainer(head)) kind = "binary";
            // The HDD install's icon.sys is the text form ("PS2X", key = value lines).
            if (kind == "iconsys" && head.Length >= 4 && !head[..4].SequenceEqual("PS2D"u8)) kind = "binary";
            if (kind != "binary" || head.Length < 4) return kind;
            if (head[0] == 0x7F && head[1] == 'E' && head[2] == 'L' && head[3] == 'F') return "program";
            if (head[..4].SequenceEqual("VAGp"u8)) return "sound";
            if (head[..4].SequenceEqual("PS2D"u8)) return "iconsys";
            if (head.Length >= 0x1C && head.Slice(0x18, 4).SequenceEqual("SBlk"u8)) return "bank";
            if (Ps2Icon.IsIcon(head)) return "icon";
            if (LooksLikeContainer(head)) return "archive";
            if (DiscFileInfo.LooksLikeText(head)) return "text";
            return "binary";
        }

        private static string KindByName(string name)
        {
            string ext = System.IO.Path.GetExtension(name).ToUpperInvariant();
            if (ext is ".ZDB" or ".CDB") return "package";
            if (name.Equals("WORL_MDL.ZED", StringComparison.OrdinalIgnoreCase)) return "models";
            if (FamilyRx.Match(name) is { Success: true } m)
                return m.Groups["kind"].Value.ToUpperInvariant() switch { "MDL" => "models", "TXR" => "textures", "PAL" => "palettes", _ => "geo" };
            if (name.Equals(PackageAnimations.PackageLeaf, StringComparison.OrdinalIgnoreCase) || name.Equals(PackageAnimations.SharedFile, StringComparison.OrdinalIgnoreCase)) return "clips";
            if (name.Equals("ICON.SYS", StringComparison.OrdinalIgnoreCase)) return "iconsys";
            switch (ext)
            {
                case ".ZED": case ".ZAR": return "archive";
                case ".RDR": return "rdr";
                case ".MPS": return "mps";
                case ".VAG": return "sound";
                case ".BNK": case ".BD": return "bank";
                case ".PSS": return "movie";
                case ".RAW": case ".RGB": return "image";
                case ".ICO": return "icon";
                case ".ELF": case ".IRX": return "program";
            }
            return "binary";
        }

        private static string KindOf(Place p) => KindOf(p.Name, Head(p, 512));

        // ------------------------------------------------------------------ sessions

        private static AssetSession ViewerSession(string packagePath)
        {
            AssetSession s = OpenSession(Path.GetFullPath(packagePath), out bool reused);
            var close = new List<AssetSession>();
            lock (ViewerOwned)
            {
                // Ours if we opened it, or opened it before and no editor has taken it since.
                if (ViewerOwned.Remove(s.Id) || !reused) ViewerOwned.Add(s.Id);
                while (ViewerOwned.Count > ViewerKeep)
                {
                    string id = ViewerOwned[0];
                    ViewerOwned.RemoveAt(0);
                    if (Sessions.TryGetValue(id, out var old) && old.Dirty.Count == 0 && old.Undo.Count == 0) close.Add(old);
                }
            }
            foreach (var old in close) lock (old.Gate) Sessions.TryRemove(old.Id, out _);
            return s;
        }

        // The package whose session lists a place: the .ZDB holding a root entry, or the loose file itself (its
        // folder is the package). A loose MOTION_P.ZAR is shared by every map: a ZDB beside it lends a session.
        private static string? PackageOf(Place p, string kind)
        {
            if (ZdbArchive.IsArchivePath(p.Path)) return p.Inner.Length <= 1 ? p.Path : null;
            if (p.Inner.Length != 0) return null;
            if (kind == "clips")
            {
                string dir = Path.GetDirectoryName(p.Path)!;
                return Directory.GetFiles(dir, "*.ZDB").Where(f => !Path.GetFileName(f).StartsWith("APACHE", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => new FileInfo(f).Length).FirstOrDefault();
            }
            // A loose file's package is its folder (one session for every file in it, as edits use).
            return kind is "models" or "textures" or "palettes" or "geo" ? EditPackageOf(p.Path) : null;
        }

        // ------------------------------------------------------------------ VIEWER_LIST

        private static object ViewerList(string? path, ViewerRequest? req)
        {
            string full = FullPathOf(path);
            if (Directory.Exists(full)) return FolderList(full);

            var place = Resolve(full, req?.Inner);
            string kind = KindOf(place);
            var crumbs = Crumbs(full, place.Inner);
            string? pkg = PackageOf(place, kind);
            if (pkg != null && kind is "models" or "textures" or "palettes" or "geo" or "clips")
            {
                var s = ViewerSession(pkg);
                lock (s.Gate)
                {
                    var items = SemanticItems(s, place, kind);
                    return new { path = full, inner = place.Inner, name = place.Name, kind, size = place.Size, session = s.Id, package = s.OpenPath, crumbs, items, editable = false, editSession = EditedSessionOf(full)?.Id, editPackage = EditPackageOf(full) };
                }
            }

            var children = Children(place) ?? throw new InvalidDataException($"{place.Name} is a {kind} file, not an archive");
            string? session = kind == "package" ? ViewerSession(full).Id : null;
            var list = children.Select((n, i) =>
            {
                string k = KindOf(n.Name, n.Loaded is { } d ? d.AsSpan(0, Math.Min(d.Length, 512)) : ReadOnlySpan<byte>.Empty);
                // Empty records group the records after them (a ZED's "textures", a ZANIM's "dlgLoad.rdr" ...).
                if (n.Size == 0) k = "group";
                return (object)new { name = n.Name, kind = k, size = n.Size, path = full, inner = place.Inner.Append(i).ToArray(), entryPath = n.EntryPath };
            }).ToList();
            // Records of a package, or of a loose file its folder's session can hold, can be edited.
            bool editable = ZdbArchive.IsArchivePath(full) || LooseEditable(full);
            return new { path = full, inner = place.Inner, name = place.Name, kind, size = place.Size, session, package = session != null ? full : null, crumbs, items = list, editable, editSession = EditedSessionOf(full)?.Id, editPackage = EditPackageOf(full) };
        }

        private static object FolderList(string dir)
        {
            var info = new DirectoryInfo(dir);
            var items = new List<object>();
            foreach (var d in info.GetDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            {
                if ((d.Attributes & FileAttributes.Hidden) != 0 && d.Parent != null && d.Parent.Parent == null && d.Name.StartsWith('$')) continue;
                int count = 0;
                try { count = d.EnumerateFileSystemInfos().Count(); } catch (UnauthorizedAccessException) { }
                items.Add(new { name = d.Name, kind = "folder", size = 0L, detail = $"{count} item{(count == 1 ? "" : "s")}", path = d.FullName, inner = Array.Empty<int>() });
            }
            var edited = Sessions.Values.FirstOrDefault(x => x.OpenPath.Equals(Path.TrimEndingDirectorySeparator(dir), StringComparison.OrdinalIgnoreCase) && (x.Dirty.Count > 0 || x.Undo.Count > 0 || x.Redo.Count > 0));
            foreach (var f in info.GetFiles().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            {
                byte[] head = Array.Empty<byte>();
                try
                {
                    using var fs = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
                    head = new byte[Math.Min(512, fs.Length)];
                    fs.ReadExactly(head);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                // Files edited in the folder's session list their edited size.
                long size = edited?.Package.Entry(f.Name)?.Data.Length ?? f.Length;
                items.Add(new { name = f.Name, kind = KindOf(f.Name, head), size, path = f.FullName, inner = Array.Empty<int>(), editable = LooseEditable(f.FullName) });
            }
            return new { path = dir, inner = Array.Empty<int>(), name = info.Name, kind = "folder", size = 0L, session = (string?)null, package = (string?)null, crumbs = Crumbs(dir, Array.Empty<int>()), items, editable = false, editSession = edited?.Id, editPackage = dir };
        }

        // The path bar: the folders down to the file, then each archive entered.
        private static List<object> Crumbs(string full, int[] inner)
        {
            var list = new List<object>();
            var parts = new List<string>();
            for (var d = Directory.Exists(full) ? new DirectoryInfo(full) : new FileInfo(full).Directory; d != null; d = d.Parent) parts.Insert(0, d.FullName);
            foreach (string p in parts) list.Add(new { label = Path.GetFileName(p.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : p, path = p, inner = Array.Empty<int>() });
            if (File.Exists(full))
            {
                list.Add(new { label = Path.GetFileName(full), path = full, inner = Array.Empty<int>() });
                var place = new Place { Path = full, Inner = Array.Empty<int>(), Name = Path.GetFileName(full), Size = new FileInfo(full).Length, Bytes = () => File.ReadAllBytes(full) };
                for (int depth = 0; depth < inner.Length; depth++)
                {
                    var children = Children(place);
                    if (children == null || inner[depth] >= children.Count) break;
                    var c = children[inner[depth]];
                    place = new Place { Path = full, Inner = inner[..(depth + 1)], Name = c.Name, Size = c.Size, Bytes = c.Bytes, Loaded = c.Loaded };
                    list.Add(new { label = c.Name, path = full, inner = place.Inner, entryPath = c.EntryPath });
                }
            }
            return list;
        }

        // What the editors see in a package archive: its models, textures, palettes or clips.
        private static List<object> SemanticItems(AssetSession s, Place place, string kind)
        {
            string leaf = place.Name;
            string? ns = s.Package.Entry(leaf)?.Namespace ?? (FamilyRx.Match(leaf) is { Success: true } m ? m.Groups["ns"].Value.ToLowerInvariant() : null);
            switch (kind)
            {
                case "models":
                case "geo":
                    return s.Package.Models()
                        .Where(x => kind == "models" ? x.Archive.Equals(leaf, StringComparison.OrdinalIgnoreCase) : x.Namespace.Equals(ns, StringComparison.OrdinalIgnoreCase))
                        .Select(x => (object)new
                        {
                            name = x.Name, kind = "model", size = (long)x.Size,
                            detail = $"{x.Kind}{(x.VisualCount > 1 ? $", {x.VisualCount} visuals" : "")}",
                            path = place.Path, inner = place.Inner,
                            @ref = new { archive = x.Archive, name = x.Name, modelKind = x.Kind },
                        }).ToList();
                case "textures":
                {
                    var list = new List<object>();
                    var nodes = s.Package.Container(leaf).Nodes;
                    for (int i = 0; i + 1 < nodes.Count; i++)
                    {
                        if (!nodes[i].Name.EndsWith(".tif", StringComparison.OrdinalIgnoreCase) || nodes[i + 1].Name != "texdat") continue;
                        string detail = "";
                        try
                        {
                            var h = ZedTextureFile.ParseHeader(nodes[i + 1].Data);
                            detail = $"{h.Width}×{h.Height}, {h.Bpp}-bit{(h.IsIndexed ? $", palette {h.ResolvePaletteId()}" : "")}";
                        }
                        catch (InvalidDataException) { detail = "unreadable header"; }
                        list.Add(new { name = nodes[i].Name, kind = "texture", size = (long)nodes[i + 1].Size, detail, path = place.Path, inner = place.Inner, @ref = new { name = nodes[i].Name, @namespace = ns, archive = leaf } });
                    }
                    return list;
                }
                case "palettes":
                {
                    var list = new List<object>();
                    var nodes = s.Package.Container(leaf).Nodes;
                    for (int i = 0; i < nodes.Count; i++)
                    {
                        if (!nodes[i].Name.StartsWith("texpal_", StringComparison.Ordinal) || !int.TryParse(nodes[i].Name[7..], out int id)) continue;
                        var buf = nodes.Skip(i + 1).Take(3).FirstOrDefault(n => n.Name == "buf");
                        list.Add(new { name = nodes[i].Name, kind = "palette", size = (long)(buf?.Size ?? 0), detail = buf == null ? "no colours" : buf.Size < 1024 ? "16-bit, 256 colours" : "32-bit, 256 colours", path = place.Path, inner = place.Inner, @ref = new { archive = leaf, id, @namespace = ns } });
                    }
                    return list;
                }
                default: // clips
                {
                    string source = place.Name.Equals(PackageAnimations.SharedFile, StringComparison.OrdinalIgnoreCase) ? PackageAnimations.SharedFile : leaf;
                    return PackageAnimations.List(s.Package, Shared(s)).Where(c => c.Source.Equals(source, StringComparison.OrdinalIgnoreCase)).Select(c =>
                    {
                        var clip = MotionClip.Parse(c.Data);
                        return (object)new { name = c.Name, kind = "clip", size = (long)c.Data.Length, detail = $"{clip.Duration:0.00} s, {clip.Tracks.Count} bones", path = place.Path, inner = place.Inner, @ref = new { name = c.Name, source = c.Source } };
                    }).ToList();
                }
            }
        }

        // ------------------------------------------------------------------ VIEWER_PREVIEW

        private static object ViewerPreview(string? path, ViewerRequest? req)
        {
            if (req?.Palette is int paletteId && req.Session != null) return PalettePreview(req.Session, req.Archive, paletteId);
            string full = FullPathOf(path);
            if (Directory.Exists(full))
            {
                var d = new DirectoryInfo(full);
                return new { type = "folder", name = d.Name, facts = Facts(("Folders", d.GetDirectories().Length.ToString()), ("Files", d.GetFiles().Length.ToString()), ("Total size", Bytes(d.GetFiles().Sum(f => f.Length)))) };
            }
            var place = Resolve(full, req?.Inner);
            string kind = KindOf(place);
            var common = new List<(string, string)> { ("Size", Bytes(place.Size)) };
            if (place.Inner.Length > 0) common.Add(("In", Path.GetFileName(full)));
            if (place.Size == 0)
            {
                common.Add(("Contents", place.Inner.Length > 0 ? "none: the record groups the records after it" : "none (an empty file)"));
                return new { type = "info", name = place.Name, facts = Facts(common) };
            }
            try
            {
                // Clutter and effect scripts have editors of their own.
                if (place.Name.Equals(PackageEdits.ClutterLeaf, StringComparison.OrdinalIgnoreCase) && kind == "archive") return ClutterPreview(place, common);
                if (place.Name.EndsWith("ZANIM.ZAR", StringComparison.OrdinalIgnoreCase) && kind == "archive") return ZanimPreview(place, req, common);
                return kind switch
                {
                    "package" => PackagePreview(place, common),
                    "geo" => GeoPreview(place, common),
                    "models" or "textures" or "palettes" or "clips" or "archive" => ArchivePreview(place, kind, common),
                    "rdr" => RdrPreview(place, common),
                    "mps" => MpsPreview(place, common),
                    "sound" => SoundPreview(place, common),
                    "bank" => BankPreview(place, req, common),
                    "movie" when place.Inner.Length == 0 => MoviePreview(place, common),
                    "image" => ImagePreview(place, common),
                    "icon" => IconPreview(place, common),
                    "iconsys" => IconSysPreview(place, common),
                    "program" => ProgramPreview(place, common),
                    "text" => TextPreview(place, common),
                    _ => HexPreview(place, kind, common, null),
                };
            }
            // A file the decoder does not follow still shows its bytes.
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException or EndOfStreamException or OverflowException)
            {
                return HexPreview(place, kind, common, $"Could not decode as {kind}: {ex.Message}");
            }
        }

        private static object[] Facts(params (string Label, string Value)[] facts) => facts.Select(f => (object)new[] { f.Label, f.Value }).ToArray();
        private static object[] Facts(List<(string, string)> facts) => Facts(facts.ToArray());

        private static string Bytes(long n) => n switch
        {
            < 1024 => $"{n} B",
            < 1024 * 1024 => $"{n / 1024.0:0.#} KB",
            < 1024L * 1024 * 1024 => $"{n / (1024.0 * 1024):0.#} MB",
            _ => $"{n / (1024.0 * 1024 * 1024):0.##} GB",
        };

        private static object PackagePreview(Place place, List<(string, string)> facts)
        {
            var s = ViewerSession(place.Path);
            lock (s.Gate)
            {
                var models = s.Package.Models();
                facts.Add(("Entries", s.Package.Entries.Count.ToString()));
                foreach (var g in models.GroupBy(m => m.Kind).OrderBy(g => g.Key)) facts.Add(($"Models: {g.Key}", g.Count().ToString()));
                if (s.Package.MapNamespace != null) facts.Add(("Map", s.Package.MapNamespace.ToUpperInvariant()));
                bool hasMap = s.Package.MapScene() != null;
                int characters = models.Count(m => m.Kind == "character");
                return new { type = "package", name = place.Name, session = s.Id, package = s.OpenPath, hasMap, models = models.Count, characters, mapNamespace = s.Package.MapNamespace, facts = Facts(facts) };
            }
        }

        private static object ArchivePreview(Place place, string kind, List<(string, string)> facts)
        {
            var children = Children(place);
            facts.Add(("Records", children?.Count.ToString() ?? "-"));
            if (place.Inner.Length == 0 && place.Size > ViewerReadWhole) facts.Add(("Read", "record table only (large archive)"));
            if (children != null)
            {
                var kinds = children.Take(4000).GroupBy(n => KindOf(n.Name, n.Loaded is { } d ? d.AsSpan(0, Math.Min(d.Length, 512)) : ReadOnlySpan<byte>.Empty))
                    .Where(g => g.Key != "binary").OrderByDescending(g => g.Count()).Take(4);
                foreach (var g in kinds) facts.Add(($"Holds: {g.Key}", g.Count().ToString()));
            }
            return new { type = "archive", name = place.Name, kind, facts = Facts(facts) };
        }

        private static object RdrPreview(Place place, List<(string, string)> facts)
        {
            byte[] data = place.Bytes();
            var rdr = RdrFile.Parse(data);
            facts.Add(("RDR version", rdr.Version.ToString()));
            facts.Add(("Atoms", rdr.Atoms.Length.ToString()));
            facts.Add(("String pool", Bytes(rdr.StringPool.Length)));
            // [atom, type, value, children?] from the root: atoms are what VIEWER_EDIT's rdr ops name.
            return new { type = "rdr", name = place.Name, tree = RdrDocument.Parse(data).Tree(), facts = Facts(facts) };
        }

        private static object MpsPreview(Place place, List<(string, string)> facts)
        {
            var mps = MpsFile.Parse(place.Bytes());
            facts.Add(("AI maps", $"{mps.Maps.Count} (declared {mps.MapCount})"));
            var maps = mps.Maps.Select(m => new { m.Name, m.Width, m.Height, min = new[] { m.MinX, m.MinY, m.MinZ }, max = new[] { m.MaxX, m.MaxY, m.MaxZ }, scale = new[] { m.ScaleX, m.ScaleZ }, m.Offset }).ToList();
            // The package's AIMAPS (its first .MPS, a root file): its points, edited with ASSET_EDIT movePoint / setPoint.
            bool root = ZdbArchive.IsArchivePath(place.Path) ? place.Inner.Length == 1 : place.Inner.Length == 0 && LooseEditable(place.Path);
            if (root)
            {
                var s = ViewerSession(EditPackageOf(place.Path));
                lock (s.Gate)
                {
                    var first = s.Package.Entries.FirstOrDefault(e => e.Leaf.EndsWith(".MPS", StringComparison.OrdinalIgnoreCase));
                    if (first != null && first.Leaf.Equals(place.Name, StringComparison.OrdinalIgnoreCase))
                        return new { type = "mps", name = place.Name, maps, points = MapPointsOf(s), session = s.Id, facts = Facts(facts) };
                }
            }
            return new { type = "mps", name = place.Name, maps, points = (object?)null, session = (string?)null, facts = Facts(facts) };
        }

        // Long sounds are cut to this many seconds in the preview.
        private const int MaxPreviewSeconds = 120;

        private static object SoundPreview(Place place, List<(string, string)> facts)
        {
            byte[] data = place.Bytes();
            var vag = SpuAdpcm.ReadVag(data) ?? throw new InvalidDataException("No VAGp header");
            int rate = vag.SampleRate is > 1000 and < 96000 ? vag.SampleRate : 22050;
            var body = data.AsSpan(vag.DataOffset, Math.Min(vag.DataSize, data.Length - vag.DataOffset));
            short[] pcm = SpuAdpcm.Decode(body, rate * MaxPreviewSeconds);
            facts.Add(("Format", "VAG (SPU ADPCM, mono)"));
            facts.Add(("Sample rate", $"{rate} Hz"));
            facts.Add(("Length", $"{pcm.Length / (double)rate:0.00} s"));
            if (vag.Name.Length > 0) facts.Add(("Name", vag.Name));
            return new { type = "audio", name = place.Name, rate, seconds = pcm.Length / (double)rate, wav = Convert.ToBase64String(SpuAdpcm.Wav(pcm, rate)), facts = Facts(facts) };
        }

        private static object BankPreview(Place place, ViewerRequest? req, List<(string, string)> facts)
        {
            byte[] data = place.Bytes();
            var (offset, length) = SpuAdpcm.BankBody(data) ?? (0, data.Length);
            var body = data.AsSpan(offset, length);
            var samples = SpuAdpcm.SplitSamples(body);
            int rate = req?.Rate is > 1000 and < 96000 ? req.Rate.Value : 22050;
            int pick = Math.Clamp(req?.Sample ?? 0, 0, Math.Max(0, samples.Count - 1));
            facts.Add(("Format", SpuAdpcm.BankBody(data) != null ? "SBlk sound bank" : "SPU ADPCM sample body"));
            facts.Add(("Samples", samples.Count.ToString()));
            facts.Add(("Body", $"{Bytes(length)} at 0x{offset:X}"));
            string? wav = null;
            double seconds = 0;
            if (samples.Count > 0)
            {
                var (o, l) = samples[pick];
                short[] pcm = SpuAdpcm.Decode(body.Slice(o, l), rate * MaxPreviewSeconds);
                seconds = pcm.Length / (double)rate;
                wav = Convert.ToBase64String(SpuAdpcm.Wav(pcm, rate));
            }
            return new
            {
                type = "audio", name = place.Name, rate, seconds, wav, sample = pick,
                samples = samples.Select(x => new { offset = offset + x.Offset, length = x.Length, seconds = x.Length / 16.0 * 28 / rate }).ToList(),
                facts = Facts(facts),
            };
        }

        private static object MoviePreview(Place place, List<(string, string)> facts)
        {
            var movie = DiscFileInfo.ReadPss(place.Path);
            facts.Add(("Format", "PSS (MPEG-2 program stream)"));
            if (movie.Width > 0) facts.Add(("Picture", $"{movie.Width}×{movie.Height}"));
            if (movie.FrameRate > 0) facts.Add(("Frame rate", $"{movie.FrameRate:0.###} fps"));
            if (movie.Seconds > 0) facts.Add(("Length", TimeSpan.FromSeconds(movie.Seconds).ToString(@"m\:ss")));
            facts.Add(("Sound", movie.Sound ?? "none")); 
            // Browsers play it once VIEWER_MOVIE has converted it to MP4 (ffmpeg); a conversion is kept.
            string? cached = CachedMovie(place.Path);
            return new
            {
                type = "movie", name = place.Name, facts = Facts(facts),
                canConvert = cached != null || FindFfmpeg() != null,
                url = cached == null ? null : $"/api/viewer/movie/{Uri.EscapeDataString(Path.GetFileName(cached))}",
            };
        }

        private static object ImagePreview(Place place, List<(string, string)> facts)
        {
            byte[] data = place.Bytes();
            var g = RawScreen.GuessLayout(data) ?? throw new InvalidDataException("No screen size fits this file");
            facts.Add(("Picture", $"{g.Width}×{g.Height}"));
            facts.Add(("Pixels", g.BytesPerPixel == 4 ? "RGBX 32-bit (no header)" : "RGB 24-bit (no header)"));
            return new { type = "image", name = place.Name, width = g.Width, height = g.Height, png = Convert.ToBase64String(PngEncoder.EncodeRgba(RawScreen.ToRgba(data, g), g.Width, g.Height)), facts = Facts(facts) };
        }

        private static object IconPreview(Place place, List<(string, string)> facts)
        {
            var icon = Ps2Icon.Parse(place.Bytes());
            facts.Add(("Format", "Memory-card icon"));
            facts.Add(("Vertices", $"{icon.VertexCount} ({icon.VertexCount / 3} triangles)"));
            facts.Add(("Animation", icon.Shapes > 1 ? $"{icon.Shapes} shapes (the rest pose is shown)" : "none"));
            facts.Add(("Texture", icon.Texture == null ? "none" : (icon.TextureType & 8) != 0 ? "128×128, RLE packed" : "128×128"));
            string? png = icon.Texture == null ? null : Convert.ToBase64String(PngEncoder.EncodeRgba(icon.Texture, Ps2Icon.TextureSize, Ps2Icon.TextureSize));
            return new { type = "icon", name = place.Name, mesh = IconMesh(icon, png != null), png, facts = Facts(facts) };
        }

        // The icon in ASSET_MODEL_MESH's shape, so the page draws it with the Model Editor's viewport.
        private static object IconMesh(Ps2Icon icon, bool textured)
        {
            static string F(float[] a) { var b = new byte[a.Length * 4]; Buffer.BlockCopy(a, 0, b, 0, b.Length); return Convert.ToBase64String(b); }
            var tris = Enumerable.Range(0, icon.VertexCount / 3 * 3).Select(i => (uint)i).ToArray();
            var triBytes = new byte[tris.Length * 4];
            Buffer.BlockCopy(tris, 0, triBytes, 0, triBytes.Length);
            // The viewport flips V for top-down textures (V = 0 the top row, as icons store it): flip it first.
            var uvs = (float[])icon.Uvs.Clone();
            for (int i = 1; i < uvs.Length; i += 2) uvs[i] = 1f - uvs[i];
            // A textured icon is drawn unlit, texture only (vertex colours light untextured ones): neutral 0x80 colours.
            byte[] colors = textured ? Enumerable.Repeat((byte)0x80, icon.Colors.Length).ToArray() : icon.Colors;
            float[] min = { float.MaxValue, float.MaxValue, float.MaxValue }, max = { float.MinValue, float.MinValue, float.MinValue };
            for (int i = 0; i < icon.VertexCount; i++)
                for (int k = 0; k < 3; k++) { min[k] = Math.Min(min[k], icon.Positions[i * 3 + k]); max[k] = Math.Max(max[k], icon.Positions[i * 3 + k]); }
            return new
            {
                archive = "icon", name = "icon", revision = 0, textureNamespace = (string?)null, boundsMin = min, boundsMax = max,
                chunks = new[]
                {
                    new
                    {
                        index = 0, geometryOffset = 0, visual = (string?)null, node = (string?)null, texture = textured ? "icon" : null, materialBlend = (bool?)null,
                        unit = "icon", matrix = new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 }, scale = 1f, origin = new float[3],
                        vertexCount = icon.VertexCount, triangleCount = tris.Length / 3, hasColors = true,
                        positions = F(icon.Positions), uvs = F(uvs), normals = F(icon.Normals), colors = Convert.ToBase64String(colors), triangles = Convert.ToBase64String(triBytes),
                    },
                },
            };
        }

        private static object IconSysPreview(Place place, List<(string, string)> facts)
        {
            var sys = Ps2Icon.ReadIconSys(place.Bytes()) ?? throw new InvalidDataException("No PS2D header");
            facts.Add(("Format", "Memory-card save icon list"));
            facts.Add(("Title", sys.Title));
            string[] roles = { "Icon", "Copy icon", "Delete icon" };
            for (int i = 0; i < sys.Icons.Length; i++) if (sys.Icons[i].Length > 0) facts.Add((roles[i], sys.Icons[i]));
            return new { type = "iconsys", name = place.Name, line1 = sys.Line1, line2 = sys.Line2, icons = sys.Icons, facts = Facts(facts) };
        }

        private static object ProgramPreview(Place place, List<(string, string)> facts)
        {
            var elf = DiscFileInfo.ReadElf(place.Bytes()) ?? throw new InvalidDataException("No ELF header");
            facts.Add(("Format", $"ELF {elf.Kind}"));
            facts.Add(("Machine", elf.Machine));
            facts.Add(("Entry", $"0x{elf.Entry:X8}"));
            if (elf.IopModule != null) facts.Add(("IOP module", $"{elf.IopModule} v{elf.IopVersion}"));
            facts.Add(("Sections", elf.Sections.Count.ToString()));
            return new { type = "program", name = place.Name, sections = elf.Sections.Select(x => new { x.Name, x.Type, address = $"0x{x.Address:X8}", size = x.Size }).ToList(), facts = Facts(facts) };
        }

        private static object TextPreview(Place place, List<(string, string)> facts)
        {
            byte[] data = Head(place, 512 * 1024);
            facts.Add(("Format", "Text"));
            facts.Add(("Lines", (data.Count(b => b == '\n') + 1).ToString()));
            return new { type = "text", name = place.Name, text = System.Text.Encoding.ASCII.GetString(data), truncated = place.Size > data.Length, facts = Facts(facts) };
        }

        private static object HexPreview(Place place, string kind, List<(string, string)> facts, string? note)
        {
            byte[] head = Head(place, 1 << 20);
            if (kind == "movie") facts.Add(("Format", "PSS movie"));
            return new
            {
                type = "hex", name = place.Name, kind, note,
                bytes = Convert.ToBase64String(head.Length <= HexBytes ? head : head[..HexBytes]),
                strings = DiscFileInfo.Strings(head, 600),
                facts = Facts(facts),
            };
        }

        /// <summary>Writes a place's bytes, as stored, to a file.</summary>
        private static object ViewerExtract(string? path, ViewerRequest? req, string? output)
        {
            if (string.IsNullOrWhiteSpace(output)) throw new ArgumentException("OutputPath is required");
            string full = FullPathOf(path);
            if (Directory.Exists(full)) throw new ArgumentException("A folder has no bytes to extract");
            var place = Resolve(full, req?.Inner);
            if (place.Inner.Length == 0) throw new ArgumentException("That is the file itself; copy it instead");
            string target = Path.GetFullPath(output);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            byte[] data = place.Bytes();
            CodeDesigner.Ps2.FileFormats.SafeFile.Write(target, data);
            return new { written = target, size = data.Length };
        }

        private static object PalettePreview(string session, string? archive, int id)
        {
            var s = Find(session) ?? throw new KeyNotFoundException($"No asset session '{session}'");
            lock (s.Gate)
            {
                string ns = (archive != null ? s.Package.Entry(archive)?.Namespace : null) ?? throw new KeyNotFoundException($"No palette archive '{archive}'");
                var pal = s.Package.FindPalette(ns, id) ?? throw new KeyNotFoundException($"texpal_{id} not found in {archive}");
                return new { type = "palette", name = $"texpal_{id}", rgba = pal.Rgba.Take(1024).Select(b => (int)b).ToArray(), is16 = pal.Is16Bit, facts = Facts(("Colours", "256"), ("Depth", pal.Is16Bit ? "16-bit (A1B5G5R5)" : "32-bit RGBA")) };
            }
        }
    }
}
