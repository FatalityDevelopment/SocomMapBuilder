using CodeDesigner.Ps2.FileFormats.Assets;
using CodeDesigner.Ps2.FileFormats.Zipper.Geo;
using CodeDesigner.Ps2.FileFormats.Zipper.Model;
using CodeDesigner.Ps2.FileFormats.Zipper.Package;
using CodeDesigner.Ps2.FileFormats.Zipper.ZDB;

namespace CodeDesigner.Ps2.FileFormats.Zipper.Ca
{
    /// <summary>
    /// A SOCOM 3 / Combined Assault package: a location archive <c>RUN/&lt;hash&gt;.ZAR</c> (ZDB type-2 layout holding
    /// the location's ZDB v6 files) or a single ZDB v6 file. Every resource is indexed by (file, name, tag); the asset
    /// libraries (<c>ISLE</c> level data, <c>ASST</c> libraries: characters <c>clib</c>, vehicles, props, skies) give
    /// the models. In an <c>ISLE</c> library the model with the most nodes is the world (COMPOUND: 1,114 nodes, 740
    /// mesh instances); the others are prop prototypes.
    /// </summary>
    public sealed class CaPackage
    {
        public sealed record Resource(string File, string Name, string Tag, uint Version, byte[] Data);

        public sealed record Model(string Id, string Name, string Kind, string File, string Resource, int LibraryIndex, int Offset, int Nodes);

        public string SourcePath { get; }
        public List<Resource> Resources { get; } = new();
        private readonly List<(Resource Res, CaLibrary Lib)> _libraries = new();
        private List<Model>? _models;

        // Index by physical archive and resource ordinal. A v6 can use the same filename for many tags.
        // External GLOBAL resources and loose non-v6 files have no writable entry here.
        private readonly Dictionary<int, (int Outer, int Inner)> _entryLocations = new();
        private readonly byte[] _sourceBytes;

        private CaPackage(string path, byte[] data) { SourcePath = path; _sourceBytes = data; }

        /// <summary>True when the file is CA's: a .ZAR whose entries are ZDB v6, or a ZDB v6 (header 0xC8, 0x58).</summary>
        public static bool IsCaPath(string path)
        {
            if (Directory.Exists(path)) return PspMapFiles(path).Count > 0;
            if (!File.Exists(path)) return false;
            using var f = File.OpenRead(path);
            var head = new byte[8];
            if (f.Read(head, 0, 8) < 8) return false;
            uint magic = BitConverter.ToUInt32(head, 0), entry = BitConverter.ToUInt32(head, 4);
            if (magic == 0xC8 && entry == 0x58) return true;
            if (magic != 0xFC || !path.EndsWith(".zar", StringComparison.OrdinalIgnoreCase)) return false;
            try
            {
                using var zar = ZdbArchive.Open(path);
                return zar.Entries.Any(e => e.Data.Length >= 8 && BitConverter.ToUInt32(e.Data, 0) == 0xC8 && BitConverter.ToUInt32(e.Data, 4) == 0x58);
            }
            catch (InvalidDataException) { return false; }
        }

        /// <summary>True for SOCOM: Fireteam Bravo 2 (PSP): its ZDBs are version 7 and its libraries draw with the GE.</summary>
        public bool IsPsp { get; private set; }

        /// <summary>
        /// True for SOCOM: Fireteam Bravo (the first PSP game, UCUS-98615): version-7 ZDBs like Fireteam Bravo 2's, but
        /// older resources (ISLE 53, ASST 42; Fireteam Bravo 2: 70, 59), its own mesh object layout
        /// (<see cref="PspGe.Ftb1ObjectType"/>) and actor records, and one character.rdr in GLOBAL.ZDB.
        /// </summary>
        public bool IsFtb1 { get; private set; }

        /// <summary>A mission's character.rdr: its own (Fireteam Bravo 2, CA), or GLOBAL.ZDB's (Fireteam Bravo).</summary>
        public Resource? CharacterTable(string missionId) => IsFtb1 ? GlobalResource("CHAR") : MissionResource(missionId, "CHAR");

        /// <summary>
        /// The ZDB files of a Fireteam Bravo 2 map folder (<c>RUN\FORT_MP</c>): every version-7 ZDB in it. A map is the
        /// island (<c>ISLAND_F.ZDB</c>: the <c>ISLE</c> library), the world (<c>FORT_MP.ZDB</c>), the characters
        /// (<c>VCOMMON_*.ZDB</c>, one per game mode) and the missions (<c>DEM.ZDB</c>, <c>SUP.ZDB</c> ...). Empty when
        /// the folder holds no version-7 ZDB.
        /// </summary>
        public static List<string> PspMapFiles(string dir)
        {
            var list = new List<string>();
            if (!Directory.Exists(dir)) return list;
            foreach (string f in Directory.GetFiles(dir, "*.ZDB").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    using var s = File.OpenRead(f);
                    var head = new byte[12];
                    if (s.Read(head, 0, 12) == 12 && BitConverter.ToUInt32(head, 0) == 0xC8 && BitConverter.ToUInt32(head, 4) == 0x58
                        && BitConverter.ToUInt32(head, 8) >= 7) list.Add(Path.GetFullPath(f));
                }
                catch (IOException) { }
            }
            return list;
        }

        private static bool IsPspZdb(byte[] data) =>
            data.Length >= 12 && BitConverter.ToUInt32(data, 0) == 0xC8 && BitConverter.ToUInt32(data, 4) == 0x58 && BitConverter.ToUInt32(data, 8) >= 7;

        /// <summary>
        /// A Fireteam Bravo 2 map (a map folder, or any ZDB in one, which opens the whole folder), read-only: its own
        /// ZDBs, then <c>RUN\GLOBAL.ZDB</c> (weapons). Every game mode's <c>VCOMMON_*.ZDB</c> holds the same character
        /// library; only the first copy of an identical library is kept.
        /// </summary>
        private static CaPackage OpenPsp(string path)
        {
            string full = Path.GetFullPath(path);
            string dir = Directory.Exists(full) ? full : Path.GetDirectoryName(full) ?? ".";
            var files = PspMapFiles(dir);
            // A ZDB the folder scan did not pick up opens on its own.
            if (File.Exists(full) && !files.Contains(full, StringComparer.OrdinalIgnoreCase)) files = new List<string> { full };
            byte[] own = File.Exists(full) ? File.ReadAllBytes(full) : Array.Empty<byte>();
            var pkg = new CaPackage(full, own) { IsPsp = true };
            var seen = new HashSet<string>();
            void Add(string file)
            {
                using var zdb = ZdbArchive.FromBytes(File.ReadAllBytes(file));
                int fileIndex = pkg._pspFiles.Count;
                pkg._pspFiles.Add(file);
                for (int i = 0; i < zdb.EntryCount; i++)
                {
                    var e = zdb.GetEntry(i);
                    if (e.Tag == "DEP_") continue;
                    // The copies differ only in a header word, so match tag, name and size.
                    string key = $"{e.Tag}|{e.FullPath}|{e.Data.Length}";
                    if (e.Tag is "ASST" or "ISLE" && !seen.Add(key)) continue;
                    pkg._entryLocations.Add(pkg.Resources.Count, (fileIndex, i));
                    pkg.Resources.Add(new Resource(Path.GetFileName(file), e.FullPath, e.Tag, e.ResourceVersion, e.Data));
                }
            }
            foreach (string f in files) Add(f);
            string? run = Path.GetDirectoryName(dir);
            string global = run == null ? "" : Path.GetFullPath(Path.Combine(run, "GLOBAL.ZDB"));
            if (files.Count > 1 && !files.Contains(global, StringComparer.OrdinalIgnoreCase) && File.Exists(global)
                && PspMapFiles(run!).Contains(global, StringComparer.OrdinalIgnoreCase)) Add(global);
            pkg.IsFtb1 = pkg.Resources.Any(r => r.Tag == "ISLE" && r.Version < Ftb2IsleVersion || r.Tag == "ASST" && r.Version < Ftb2AsstVersion);
            pkg.IndexLibraries();
            return pkg;
        }

        // Fireteam Bravo 2's ISLE and ASST resource versions; Fireteam Bravo's are older (53, 42).
        private const uint Ftb2IsleVersion = 70, Ftb2AsstVersion = 59;

        public static CaPackage Open(string path)
        {
            if (Directory.Exists(path)) return OpenPsp(path);
            byte[] data = File.ReadAllBytes(path);
            if (IsPspZdb(data)) return OpenPsp(path);
            var pkg = new CaPackage(Path.GetFullPath(path), data);
            if (BitConverter.ToUInt32(data, 0) == 0xC8) pkg.AddV6(Path.GetFileName(path), data, -1);
            else
            {
                // Weapons are not in the location archives: RUN/GLOBAL.ZDB (global_assets.rdr) holds them all.
                string global = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".", "GLOBAL.ZDB");
                if (File.Exists(global)) pkg.AddV6(Path.GetFileName(global), File.ReadAllBytes(global));
                using var zar = ZdbArchive.FromBytes(data);
                for (int i = 0; i < zar.EntryCount; i++)
                {
                    var e = zar.GetEntry(i);
                    if (e.Data.Length >= 8 && BitConverter.ToUInt32(e.Data, 0) == 0xC8 && BitConverter.ToUInt32(e.Data, 4) == 0x58)
                        pkg.AddV6(e.FullPath, e.Data, i);
                    else
                        pkg.Resources.Add(new Resource(e.FullPath, e.Name, "FILE", 0, e.Data));
                }
            }
            pkg.IndexLibraries();
            // CodeDesigner's name list (packages converted from SOCOM II): the names behind this package's hashes.
            foreach (var r in pkg.Resources.Where(r => r.Tag == Conversion.Socom2ToCa.NamesTag))
                foreach (var line in System.Text.Encoding.ASCII.GetString(r.Data).Split((char)10, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    pkg._names.TryAdd(CaNames.Crc32(line), line);
            return pkg;
        }

        private void IndexLibraries()
        {
            _libraries.Clear();
            _models = null;
            _objectMeshes.Clear();
            foreach (var r in Resources.Where(r => r.Tag is "ISLE" or "ASST"))
            {
                if (r.Data.Length < 0x60) continue;
                var lib = new CaLibrary(r.Data, psp: IsPsp);
                if (lib.Version is < 1 or > 4 || lib.Models.Count == 0 && lib.Textures.Count == 0) continue;
                _libraries.Add((r, lib));
                // A version-3 (or 4) header's second library (global_assets.rdr: every weapon, 88 models).
                if (lib.Nested() is { } nested && nested.Models.Count > 0)
                    _libraries.Add((r with { Name = r.Name + NestedSuffix }, nested));
            }
        }

        private readonly Dictionary<uint, string> _names = new();

        /// <summary>The name a hash stands for, from the package's own name list (converted packages), or null.</summary>
        public string? KnownName(uint hash) => _names.TryGetValue(hash, out var n) ? n : null;

        /// <summary>A bone's name: the package's name list, else the known CA bones, else bone_&lt;hash&gt;.</summary>
        public string BoneName(uint hash) => KnownName(hash) ?? CaBones.Name(hash);

        private void AddV6(string file, byte[] data, int? outer = null)
        {
            using var zdb = ZdbArchive.FromBytes(data);
            for (int i = 0; i < zdb.EntryCount; i++)
            {
                var e = zdb.GetEntry(i);
                if (e.Tag == "DEP_") continue;
                if (outer.HasValue) _entryLocations.Add(Resources.Count, (outer.Value, i));
                Resources.Add(new Resource(file, e.FullPath, e.Tag, e.ResourceVersion, e.Data));
            }
        }

        /// <summary>Install a validated fixed-size edit or history step, keeping resource indices stable.</summary>
        public void ReplaceResources(IReadOnlyDictionary<int, byte[]> replacements)
        {
            foreach (var (index, bytes) in replacements)
                if (!_entryLocations.ContainsKey(index) || Resources[index].Data.Length != bytes.Length
                    || Resources[index].Tag is not ("AIMm" or "MSNv" or "MISN" or "VALV" or "ISLE"))
                    throw new InvalidOperationException("Only fixed-size edits to local CA mission and world resources are supported");

            foreach (var (index, bytes) in replacements)
                Resources[index] = Resources[index] with { Data = bytes };
            if (replacements.Keys.Any(i => Resources[i].Tag == "ISLE")) IndexLibraries();
        }

        // Fireteam Bravo 2: the ZDB files the package was read from (an entry location's Outer indexes this list).
        private readonly List<string> _pspFiles = new();

        /// <summary>
        /// Fireteam Bravo 2: a copy of every ZDB with an edited resource, keyed by its path under <c>RUN</c>
        /// (<c>FORT_MP\ISLAND_F.ZDB</c>). Each is re-read from disk and only its edited entries are replaced; same-size
        /// edits keep the file's layout byte for byte apart from them and their checksums.
        /// </summary>
        public Dictionary<string, byte[]> BuildPspCopies(IEnumerable<int> dirty)
        {
            var copies = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in dirty.GroupBy(i => _entryLocations[i].Outer))
            {
                string path = _pspFiles[file.Key];
                using var zdb = ZdbArchive.FromBytes(File.ReadAllBytes(path));
                foreach (int index in file) zdb.ReplaceEntryData(_entryLocations[index].Inner, Resources[index].Data);
                copies[Path.Combine(Path.GetFileName(Path.GetDirectoryName(path)) ?? "", Path.GetFileName(path))] = zdb.ToBytes();
            }
            return copies;
        }

        /// <summary>Build a complete copy from the opened bytes, rewriting only the edited v6 entries.</summary>
        public byte[] BuildCopy(IEnumerable<int> dirty)
        {
            using var archive = ZdbArchive.FromBytes(_sourceBytes);
            foreach (var file in dirty.GroupBy(i => _entryLocations[i].Outer))
            {
                if (file.Key == -1)
                {
                    foreach (int index in file) archive.ReplaceEntryData(_entryLocations[index].Inner, Resources[index].Data);
                    continue;
                }
                using var inner = ZdbArchive.FromBytes(archive.GetEntry(file.Key).Data);
                foreach (int index in file) inner.ReplaceEntryData(_entryLocations[index].Inner, Resources[index].Data);
                archive.ReplaceEntryData(file.Key, inner.ToBytes());
            }
            return archive.ToBytes();
        }

        public IReadOnlyList<(Resource Res, CaLibrary Lib)> Libraries => _libraries;

        /// <summary>Resource-name suffix of a version-3 library's nested second library.</summary>
        public const string NestedSuffix = "#nested";

        public List<Model> Models()
        {
            if (_models != null) return _models;
            var list = new List<Model>();
            for (int li = 0; li < _libraries.Count; li++)
            {
                var (res, lib) = _libraries[li];
                var models = lib.Models;
                var sizes = models.Select(m => lib.Nodes(m).Count).ToList();
                int world = res.Tag == "ISLE" && sizes.Count > 0 ? sizes.IndexOf(sizes.Max()) : -1;
                string file = Path.GetFileName(res.File.Replace('\\', '/'));
                for (int i = 0; i < models.Count; i++)
                {
                    string kind = res.Tag == "ISLE"
                        ? (i == world ? (IsUiScene(res) ? "ui" : "world") : "prop")
                        : IsWeaponLibrary(res) ? "weapon"
                        : res.Name.Equals("clib", StringComparison.OrdinalIgnoreCase) ? "character"
                        : res.Name.Contains("sky", StringComparison.OrdinalIgnoreCase) ? "sky"
                        : "prop";
                    string name = res.Tag == "ISLE" && i == world ? Path.GetFileNameWithoutExtension(res.Name)
                        : KnownName(lib.ModelHash(models[i])) is { } known ? known
                        : kind == "weapon" ? WeaponName(lib, models[i])
                        : $"{Path.GetFileNameWithoutExtension(res.Name.Replace(NestedSuffix, ""))}_{lib.ModelHash(models[i]):x8}";
                    list.Add(new Model($"{file}|{res.Name}|{i}", name, kind, file, res.Name, li, models[i], sizes[i]));
                }
                // Fireteam Bravo 2 islands place most props (trees, rocks, vehicles) as mesh objects, not models: each
                // object an instance places is listed as a prop (Nodes -1, Offset the object).
                if (IsPsp && res.Tag == "ISLE")
                {
                    string island = Path.GetFileNameWithoutExtension(res.Name);
                    foreach (int obj in lib.Instances().Select(x => x.Object).Where(o => o > 0).Distinct().Order())
                        list.Add(new Model($"{file}|{res.Name}|{ObjectName(obj)}", $"{island}_{ObjectName(obj)}", "prop", file, res.Name, li, obj, -1));
                }
            }
            return _models = list;
        }

        /// <summary>A weapon's name: its root's or first mesh node's CRC32 cracked against weapon words
        /// (<see cref="CaNames"/>), else <c>w&lt;root hash&gt;</c>.</summary>
        private static string WeaponName(CaLibrary lib, int model)
        {
            if (CaNames.Weapon(lib.ModelHash(model)) is { } root) return root;
            foreach (var n in lib.Nodes(model))
                if (n.Objects.Count > 0 && CaNames.Weapon(n.Hash) is { } named) return named;
            return $"w{lib.ModelHash(model):x8}";
        }

        // Screen overlays stored as islands: the round / game complete cards, the ocean card, and the front-end menus.
        private static readonly HashSet<string> UiScenes = new(StringComparer.OrdinalIgnoreCase)
        {
            "OCN_2.src", "RoundComplete.src", "GameComplete.src", "UIPostMission.src", "CommonUIFrame.src",
        };

        private static bool IsUiScene(Resource r) =>
            UiScenes.Contains(r.Name) || r.File.Replace('/', '\\').Contains("\\UI\\", StringComparison.OrdinalIgnoreCase);

        // ------------------------------------------------------------------ map scene

        /// <summary>One placed mesh object of a world: the node's world transform and the object. A scatter instance's
        /// pieces have <paramref name="NodeOffset"/> -1 (they are not nodes and cannot be moved as one).</summary>
        public sealed record Placement(string World, uint NodeHash, int LibraryIndex, int Object, float[] Matrix,
            string Instance, int NodeOffset, float[] ParentWorld, int InstanceRecord = -1);

        private readonly Dictionary<(int Lib, int Obj), ModelMesh> _objectMeshes = new();

        /// <summary>"&lt;file&gt;|&lt;resource&gt;" of a library: how models and objects are addressed.</summary>
        public string LibraryArchive(int libraryIndex)
        {
            var res = _libraries[libraryIndex].Res;
            return $"{Path.GetFileName(res.File.Replace('\\', '/'))}|{res.Name}";
        }

        /// <summary>The mesh of one object in its own space (cached), addressed by the map scene's sources.</summary>
        public ModelMesh ObjectMesh(int libraryIndex, int obj)
        {
            if (_objectMeshes.TryGetValue((libraryIndex, obj), out var hit)) return hit;
            var lib = _libraries[libraryIndex].Lib;
            var mesh = new ModelMesh { Archive = LibraryArchive(libraryIndex), Name = ObjectName(obj) };
            mesh.Chunks.AddRange(lib.Chunks(obj, Matrix4.Identity(), ObjectName(obj), 0, UnitOf(libraryIndex)));
            mesh.Recompute();
            return _objectMeshes[(libraryIndex, obj)] = mesh;
        }

        public static string ObjectName(int obj) => $"obj_{obj:x}";

        /// <summary>Weapon libraries: global_assets' nested one, and weap_*.rdr (converted from SOCOM II).</summary>
        private static bool IsWeaponLibrary(Resource res) =>
            res.Name.StartsWith("global_assets", StringComparison.OrdinalIgnoreCase) && res.Name.EndsWith(NestedSuffix)
            || res.Name.StartsWith("weap_", StringComparison.OrdinalIgnoreCase);

        /// <summary>Weapons store chunk positions at the chunk's scale (as do scl_*.rdr libraries converted from SOCOM
        /// II models drawn that way); the rest 1/16.</summary>
        private ZedModelFile.PositionUnit UnitOf(int libraryIndex)
        {
            var res = _libraries[libraryIndex].Res;
            return IsWeaponLibrary(res) || res.Name.StartsWith("scl_", StringComparison.OrdinalIgnoreCase)
                ? ZedModelFile.PositionUnit.Scale : ZedModelFile.PositionUnit.Sixteenth;
        }

        public static int? ParseObjectName(string name) =>
            name.StartsWith("obj_", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(name[4..], System.Globalization.NumberStyles.HexNumber, null, out int o) ? o : null;

        /// <summary>
        /// The objects drawn for a node. A node's objects are usually separate pieces drawn together (terrain plus its
        /// road / decal overlay; a tree's trunk and its canopy), so all are drawn except a level-of-detail copy: an
        /// object another object of the node out-details (more triangles) with mostly the same textures and a bounding
        /// box within a quarter on every axis (the rule ca2s2static.auto_skip uses for vehicle LOD nodes).
        /// </summary>
        private List<int> Drawn(int libraryIndex, CaLibrary lib, CaLibrary.Node node)
        {
            var objs = node.Objects.Where(lib.IsStaticObject).ToList();
            if (objs.Count < 2) return objs;
            var info = objs.Select(o =>
            {
                var m = ObjectMesh(libraryIndex, o);
                var size = Enumerable.Range(0, 3).Select(i => m.BoundsMax[i] - m.BoundsMin[i]).ToArray();
                var textures = m.Chunks.Select(c => c.Texture).Where(t => t != null).ToHashSet();
                return (Obj: o, Tris: m.Chunks.Sum(c => c.Triangles.Length / 3), Size: size, Textures: textures);
            }).Where(x => x.Tris > 0).ToList();
            bool Copy((int Obj, int Tris, float[] Size, HashSet<string?> Textures) a) => info.Any(b =>
                b.Obj != a.Obj && b.Tris > a.Tris && a.Textures.Count > 0
                && a.Textures.Count(b.Textures.Contains) >= 0.6 * a.Textures.Count
                && Enumerable.Range(0, 3).All(i => Math.Abs(a.Size[i] - b.Size[i]) <= 0.25f * Math.Max(b.Size[i], 1f)));
            return info.Where(x => !Copy(x)).Select(x => x.Obj).ToList();
        }

        /// <summary>
        /// Every placed object of every world piece of the location (UI scenes left out): the world's node tree, then
        /// the island's scatter instances (<see cref="CaLibrary.Instances"/>: rocks, trees, crates — 497 of Reservoir's
        /// 755 objects), each drawn as its prop model without LOD copies.
        /// </summary>
        public List<Placement> MapScene()
        {
            var list = new List<Placement>();
            // Some nodes share a hash; a single node can also draw multiple mesh objects.
            var occurrences = new Dictionary<string, int>();
            foreach (var w in Models().Where(m => m.Kind == "world"))
            {
                var lib = _libraries[w.LibraryIndex].Lib;
                var nodes = lib.Nodes(w.Offset);
                foreach (var node in nodes)
                    foreach (int obj in Drawn(w.LibraryIndex, lib, node))
                    {
                        string key = $"{w.Name}/{node.Hash:x8}";
                        int count = occurrences.GetValueOrDefault(key);
                        occurrences[key] = count + 1;
                        list.Add(new Placement(w.Name, node.Hash, w.LibraryIndex, obj, node.World,
                            count == 0 ? key : $"{key}~{count}", node.Offset,
                            node.Parent < 0 ? Matrix4.Identity() : nodes[node.Parent].World));
                    }
                var prototypes = new Dictionary<int, List<(uint Hash, float[] World, List<int> Objects)>>();
                var (min, max) = WorldBounds(lib, w.Offset);
                foreach (var inst in lib.Instances())
                {
                    if (inst.Object > 0)
                    {
                        // A Fireteam Bravo 2 instance places one static object; the sun cards stand above 1,000 units.
                        if (Enumerable.Range(0, 3).Any(k => inst.Matrix[12 + k] < min[k] - ParkedMargin || inst.Matrix[12 + k] > max[k] + ParkedMargin)) continue;
                        list.Add(new Placement(inst.Matrix[13] > SkyHeight ? SkyWorld : w.Name, lib.U32(inst.Offset + 0x50), w.LibraryIndex, inst.Object, inst.Matrix,
                            $"{w.Name}/inst_{inst.Offset:x}", -1, Matrix4.Identity(), inst.Offset));
                        continue;
                    }
                    if (inst.Model == w.Offset) continue;
                    // A few records are parked far off the island (at the origin, or thousands of units out: 56 of the
                    // disc's 18,262); they are not drawn.
                    if (Enumerable.Range(0, 3).Any(k => inst.Matrix[12 + k] < min[k] - ParkedMargin || inst.Matrix[12 + k] > max[k] + ParkedMargin)) continue;
                    if (!prototypes.TryGetValue(inst.Model, out var parts))
                    {
                        var protoNodes = lib.Nodes(inst.Model);
                        var skip = LodSkip(w.LibraryIndex, lib, protoNodes);
                        prototypes[inst.Model] = parts = protoNodes.Where(n => !skip.Contains(n.Offset))
                            .Select(n => (n.Hash, n.World, Drawn(w.LibraryIndex, lib, n))).Where(p => p.Item3.Count > 0).ToList();
                    }
                    // Scatter pieces are not nodes (NodeOffset -1): their ids name the record, inst_<offset>.
                    string key = $"{w.Name}/inst_{inst.Offset:x}";
                    int piece = 0;
                    foreach (var (hash, world, objects) in parts)
                    {
                        float[] m = Matrix4.Multiply(inst.Matrix, world);
                        foreach (int obj in objects)
                            list.Add(new Placement(w.Name, hash, w.LibraryIndex, obj, m,
                                piece++ == 0 ? key : $"{key}~{piece - 1}", -1, inst.Matrix));
                    }
                }
                if (lib.Psp)
                {
                    list.AddRange(PspWorldObjects(w, lib, list));
                    MarkPspSky(list, w.Name);
                }
            }
            return list;
        }

        /// <summary>
        /// Moves a world's node-placed sky domes to <see cref="SkyWorld"/> (Fireteam Bravo places its domes from the
        /// world's node tree; Fireteam Bravo 2's are free objects, see <see cref="PspWorldObjects"/>): a placement
        /// whose bounds enclose all but at most two of the world's others on x and z, or that stands above
        /// <see cref="SkyHeight"/>.
        /// </summary>
        private void MarkPspSky(List<Placement> list, string world)
        {
            var mine = Enumerable.Range(0, list.Count).Where(i => list[i].World == world).ToList();
            if (mine.Count < 3) return;
            var boxes = mine.ToDictionary(i => i, i =>
            {
                var m = ObjectMesh(list[i].LibraryIndex, list[i].Object);
                var min = new[] { float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity };
                var max = new[] { float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity };
                for (int c = 0; c < 8; c++)
                {
                    var (x, y, z) = Matrix4.Apply(list[i].Matrix, (c & 1) == 0 ? m.BoundsMin[0] : m.BoundsMax[0],
                        (c & 2) == 0 ? m.BoundsMin[1] : m.BoundsMax[1], (c & 4) == 0 ? m.BoundsMin[2] : m.BoundsMax[2]);
                    float[] v = { x, y, z };
                    for (int k = 0; k < 3; k++) { min[k] = Math.Min(min[k], v[k]); max[k] = Math.Max(max[k], v[k]); }
                }
                return (Min: min, Max: max);
            });
            bool Encloses(int a, int b) =>
                boxes[a].Min[0] < boxes[b].Min[0] && boxes[a].Min[2] < boxes[b].Min[2] && boxes[a].Max[0] > boxes[b].Max[0] && boxes[a].Max[2] > boxes[b].Max[2];
            foreach (int i in mine.Where(i => list[i].NodeOffset >= 0).ToList())
                if (mine.Count(j => j != i && Encloses(i, j)) >= mine.Count - 3 || boxes[i].Min[1] > SkyHeight)
                    list[i] = list[i] with { World = SkyWorld };
        }

        /// <summary>
        /// Fireteam Bravo 2 islands draw most of their terrain and buildings from mesh objects that no node or instance
        /// places: their vertices are already in world space (the terrain tile nodes find them by area). Each is drawn
        /// where it is. Sky domes (an object whose bounds enclose the others' on x and z) and cloud layers (above 1,000
        /// units) are placed under the world name <see cref="SkyWorld"/>.
        /// </summary>
        private List<Placement> PspWorldObjects(Model w, CaLibrary lib, List<Placement> placed)
        {
            var used = placed.Where(p => p.LibraryIndex == w.LibraryIndex).Select(p => p.Object).ToHashSet();
            foreach (var m in Models().Where(m => m.LibraryIndex == w.LibraryIndex))
                if (m.Nodes >= 0) foreach (var n in lib.Nodes(m.Offset)) used.UnionWith(n.Objects);
            var free = lib.Objects.Where(o => !used.Contains(o) && lib.IsStaticObject(o))
                .Select(o => (Obj: o, Mesh: ObjectMesh(w.LibraryIndex, o))).Where(x => x.Mesh.Chunks.Count > 0).ToList();
            bool Encloses(ModelMesh a, ModelMesh b) =>
                a.BoundsMin[0] < b.BoundsMin[0] && a.BoundsMin[2] < b.BoundsMin[2] && a.BoundsMax[0] > b.BoundsMax[0] && a.BoundsMax[2] > b.BoundsMax[2];
            var list = new List<Placement>();
            foreach (var (obj, mesh) in free)
            {
                // A dome encloses all but at most the other domes.
                bool sky = free.Count > 2 && free.Count(o => o.Obj != obj && Encloses(mesh, o.Mesh)) >= free.Count - 2
                    || mesh.BoundsMin[1] > SkyHeight;
                list.Add(new Placement(sky ? SkyWorld : w.Name, lib.U32(obj + 0x08), w.LibraryIndex, obj, Matrix4.Identity(),
                    $"{w.Name}/{ObjectName(obj)}", -1, Matrix4.Identity()));
            }
            return list;
        }

        /// <summary>The world name of a Fireteam Bravo sky dome or cloud layer (the Map Builder's Sky layer).</summary>
        public const string SkyWorld = "sky";

        /// <summary>Fireteam Bravo 2 maps keep their playable ground well below this height; clouds and sun cards are above it.</summary>
        private const float SkyHeight = 1000;

        /// <summary>How far outside its island's bounds a scatter instance may stand and still be drawn.</summary>
        private const float ParkedMargin = 1000;

        /// <summary>A world root's bounds (+0xB0 min xyz, +0xBC max xyz); unbounded when they are not stored.</summary>
        private static (float[] Min, float[] Max) WorldBounds(CaLibrary lib, int root)
        {
            var min = new float[3]; var max = new float[3];
            // Fireteam Bravo 2 nodes hold one more word before the bounds (+0xB4 min, +0xC0 max).
            int at = lib.Psp ? 0xB4 : 0xB0;
            for (int k = 0; k < 3; k++)
            {
                min[k] = root + at + 0x18 <= lib.Data.Length ? BitConverter.ToSingle(lib.Data, root + at + 4 * k) : float.NaN;
                max[k] = root + at + 0x18 <= lib.Data.Length ? BitConverter.ToSingle(lib.Data, root + at + 0xC + 4 * k) : float.NaN;
            }
            bool valid = Enumerable.Range(0, 3).All(k => float.IsFinite(min[k]) && float.IsFinite(max[k]) && min[k] <= max[k]);
            return valid ? (min, max)
                : (new[] { float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity }, new[] { float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity });
        }

        private List<CaAnimations.Clip>? _clips;

        /// <summary>
        /// Every animation clip the package can play: its own <c>BONE</c> banks and the shared ones next to it
        /// (<c>RUN/AUX_*ZAN.ZDB</c>: general 460, scripted, vehicle). A clip hash appears once (the first bank wins).
        /// </summary>
        public List<CaAnimations.Clip> Animations()
        {
            if (_clips != null) return _clips;
            var banks = Resources.Where(r => r.Tag == "BONE").Select(r => (Bank: Path.GetFileNameWithoutExtension(r.File.Replace('\\', '/')), r.Data)).ToList();
            string dir = Path.GetDirectoryName(SourcePath) ?? ".";
            var shared = Directory.Exists(dir) ? Directory.GetFiles(dir, "AUX_*ZAN.ZDB") : Array.Empty<string>();
            foreach (string f in shared.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    using var zdb = ZdbArchive.FromBytes(File.ReadAllBytes(f));
                    foreach (var e in zdb.Entries.Where(e => e.Tag == "BONE"))
                        banks.Add((Path.GetFileNameWithoutExtension(f), e.Data));
                }
                catch (InvalidDataException) { }
            }
            var seen = new HashSet<uint>();
            var list = new List<CaAnimations.Clip>();
            foreach (var (bank, data) in banks)
                foreach (var c in CaAnimations.Parse(data, bank.ToLowerInvariant(), KnownName))
                    if (seen.Add(c.Hash)) list.Add(c);
            return _clips = list;
        }

        public Model? FindModel(string idOrName) =>
            Models().FirstOrDefault(m => m.Id.Equals(idOrName, StringComparison.OrdinalIgnoreCase))
            ?? Models().FirstOrDefault(m => m.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase));

        /// <summary>A model in model space: static meshes with node transforms applied (props / vehicles / weapons
        /// without their LOD copies and hull), or a skinned character in bind pose.</summary>
        public ModelMesh BuildMesh(Model model)
        {
            var lib = _libraries[model.LibraryIndex].Lib;
            if (model.Nodes < 0)
            {
                // A Fireteam Bravo 2 placed object, in its own space.
                var single = new ModelMesh { Archive = model.File, Name = model.Name };
                single.Chunks.AddRange(lib.Chunks(model.Offset, Matrix4.Identity(), ObjectName(model.Offset), 0));
                single.Recompute();
                return single;
            }
            if (model.Kind == "character") return IsPsp ? BuildPspCharacter(model, lib, BoneName) : BuildCharacter(model, lib, BoneName);
            var mesh = new ModelMesh { Archive = model.File, Name = model.Name };
            var nodes = lib.Nodes(model.Offset);
            var skip = model.Kind is "prop" or "weapon" ? LodSkip(model.LibraryIndex, lib, nodes) : new HashSet<int>();
            foreach (var node in nodes.Where(n => !skip.Contains(n.Offset)))
                foreach (int obj in Drawn(model.LibraryIndex, lib, node))
                    mesh.Chunks.AddRange(lib.Chunks(obj, node.World, $"n{node.Hash:x8}", mesh.Chunks.Count, UnitOf(model.LibraryIndex)));
            mesh.Recompute();
            return mesh;
        }

        /// <summary>
        /// Nodes of a prop / vehicle / weapon that CA draws only by distance (Mods/SOCOMCA/ca2s2static.auto_skip,
        /// confirmed on the Humvee in game): a lower-detail copy (>= 10 triangles, >= 60% of its textures shared with a
        /// node that has more triangles, bounds within 25% of that node's on every axis) and the enclosing hull (one
        /// texture of at most 16x16, bounds >= 50% of the most detailed node's on every axis). Node offsets.
        /// </summary>
        /// <summary>Node offsets of a prop / vehicle / weapon's LOD copies and hull (empty for other kinds).</summary>
        public HashSet<int> SkippedNodes(Model model) => model.Nodes >= 0 && model.Kind is "prop" or "weapon"
            ? LodSkip(model.LibraryIndex, _libraries[model.LibraryIndex].Lib, _libraries[model.LibraryIndex].Lib.Nodes(model.Offset))
            : new HashSet<int>();

        private HashSet<int> LodSkip(int libraryIndex, CaLibrary lib, List<CaLibrary.Node> nodes)
        {
            var info = new List<(int Node, HashSet<string> Textures, int Tris, float[] Size)>();
            foreach (var node in nodes)
            {
                var objs = Drawn(libraryIndex, lib, node);
                if (objs.Count == 0) continue;
                var min = new[] { float.MaxValue, float.MaxValue, float.MaxValue };
                var max = new[] { float.MinValue, float.MinValue, float.MinValue };
                var textures = new HashSet<string>();
                int tris = 0;
                foreach (int o in objs)
                {
                    var m = ObjectMesh(libraryIndex, o);
                    if (m.Chunks.Count == 0) continue;
                    for (int k = 0; k < 3; k++) { min[k] = MathF.Min(min[k], m.BoundsMin[k]); max[k] = MathF.Max(max[k], m.BoundsMax[k]); }
                    foreach (var c in m.Chunks) { tris += c.Triangles.Length / 3; if (c.Texture != null) textures.Add(c.Texture); }
                }
                if (tris > 0) info.Add((node.Offset, textures, tris, new[] { max[0] - min[0], max[1] - min[1], max[2] - min[2] }));
            }
            var skip = new HashSet<int>();
            if (info.Count < 2) return skip;
            var whole = info.MaxBy(x => x.Tris).Size;
            var texels = new Dictionary<string, long>();
            foreach (int rec in lib.Textures)
                if (lib.Tex0(rec) is ulong t0) texels[CaLibrary.TextureName(lib.U32(rec + 0x4C))] = (1L << (int)((t0 >> 26) & 0xF)) * (1L << (int)((t0 >> 30) & 0xF));
            foreach (var a in info)
            {
                if (a.Textures.Count == 1 && texels.GetValueOrDefault(a.Textures.First(), 1L << 20) <= 256
                    && Enumerable.Range(0, 3).All(k => a.Size[k] >= 0.5f * whole[k]))
                {
                    skip.Add(a.Node);
                    continue;
                }
                if (info.Any(b => b.Node != a.Node && a.Tris >= 10 && b.Tris > a.Tris && a.Textures.Count > 0
                        && a.Textures.Count(b.Textures.Contains) >= 0.6 * a.Textures.Count
                        && Enumerable.Range(0, 3).All(k => MathF.Abs(a.Size[k] - b.Size[k]) <= 0.25f * MathF.Max(b.Size[k], 1f))))
                    skip.Add(a.Node);
            }
            return skip;
        }

        /// <summary>
        /// A skinned character (clib model): the skeleton is the model's node tree (the root first, then every node in
        /// pre-order, named by hash - <see cref="CaBones"/> for the known ones - with the stored bind world matrices),
        /// the mesh every mesh object attached to its nodes. Vertex batches name bones by pre-order index without the
        /// root, so CA bone i is skeleton bone i + 1.
        /// </summary>
        private static ModelMesh BuildCharacter(Model model, CaLibrary lib, Func<uint, string> boneName)
        {
            var nodes = lib.Nodes(model.Offset);
            var skeleton = CharacterSkeleton.FromBones(nodes.Select((n, i) => new CharacterSkeleton.Bone(
                boneName(n.Hash), i, n.Parent, i, n.Local, n.World)));
            var groups = new List<SkinnedMesh.Group>();
            foreach (var node in nodes)
                foreach (int obj in node.Objects)
                {
                    int size = (int)lib.U32(obj + 0x1C);
                    if (size <= 0 || obj + 0x80 + size > lib.Data.Length) continue;
                    groups.AddRange(CharacterMesh.DecodeCa(lib.Data, obj, obj + 0x80 + size, 1, nodes.Count - 1, CaLibrary.TextureName));
                }
            return ModelMesh.FromGroups(model.File, model.Name, skeleton, groups);
        }

        /// <summary>
        /// A Fireteam Bravo 2 character: the skeleton as CA's (its node hashes are CA's bone names: hips, spinelo,
        /// leftElbow ...), and every skinned object of its nodes as weighted groups (<see cref="PspGe.Skinned"/>).
        /// </summary>
        private static ModelMesh BuildPspCharacter(Model model, CaLibrary lib, Func<uint, string> boneName)
        {
            var nodes = lib.Nodes(model.Offset);
            var skeleton = CharacterSkeleton.FromBones(nodes.Select((n, i) => new CharacterSkeleton.Bone(
                boneName(n.Hash), i, n.Parent, i, n.Local, n.World)));
            var worlds = nodes.Select(n => n.World).ToList();
            var groups = new List<SkinnedMesh.Group>();
            foreach (var node in nodes)
                foreach (int obj in node.Objects)
                    groups.AddRange(PspGe.Skinned(lib, obj, worlds));
            return ModelMesh.FromGroups(model.File, model.Name, skeleton, groups);
        }

        /// <summary>A texture by its <c>ca_&lt;hash&gt;</c> name (or hash): the first record with pixels in any library of the
        /// package (island records often only name a texture whose pixels are in a shared library such as farm_c1).</summary>
        public CaLibrary.Texture? FindTexture(string name)
        {
            string hex = name.StartsWith("ca_", StringComparison.OrdinalIgnoreCase) ? name[3..] : name;
            hex = Path.GetFileNameWithoutExtension(hex);
            if (!uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out uint hash)) return null;
            foreach (var (_, lib) in _libraries)
                foreach (int rec in lib.Textures)
                    if (lib.TextureHash(rec) == hash && lib.DecodeTexture(rec) is { } t) return t;
            return null;
        }

        /// <summary>How a texture draws (<see cref="CaLibrary.TextureState"/>): the first record of that name whose packet
        /// sets blend, alpha test or clamping, or null.</summary>
        public CaLibrary.TextureGs? FindTextureState(string name)
        {
            string hex = Path.GetFileNameWithoutExtension(name.StartsWith("ca_", StringComparison.OrdinalIgnoreCase) ? name[3..] : name);
            if (!uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out uint hash)) return null;
            foreach (var (_, lib) in _libraries)
                foreach (int rec in lib.Textures)
                    if (lib.TextureHash(rec) == hash && lib.TextureState(rec) is { } state) return state;
            return null;
        }

        // ------------------------------------------------------------------ location and missions

        /// <summary>
        /// The location folder the archive holds (<c>POLAND_RURAL</c> for <c>RUN\POLAND_RURAL\…</c>), from its entries'
        /// paths; a location archive is named by the CRC32 of <c>RUN\&lt;LOCATION&gt;</c> (<see cref="ArchiveName"/>).
        /// Null for a single ZDB.
        /// </summary>
        public string? Location => Resources
            .Select(r => r.File.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries))
            .Where(p => p.Length >= 3 && p[0].Equals("RUN", StringComparison.OrdinalIgnoreCase))
            .Select(p => p[1].ToUpperInvariant())
            .GroupBy(l => l).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();

        /// <summary>The file name of a location's archive: CRC32 of <c>RUN\&lt;LOCATION&gt;</c> in hex, plus .ZAR.</summary>
        public static string ArchiveName(string location) => $"{CaNames.Crc32(@"RUN\" + location.ToUpperInvariant()):X8}.ZAR";

        /// <summary>A mission of the location: id (its ZDB's name, <c>MP414CTR</c>), the file, its kind (SP campaign,
        /// MP online, IA instant action, TR training, or other) and game mode (the letters after the number).</summary>
        public sealed record MissionInfo(string Id, string File, string Kind, string Mode);

        private List<MissionInfo>? _missions;

        /// <summary>The missions with AI data (a <c>mission.rdr</c>), campaign first, then by id.</summary>
        public List<MissionInfo> Missions()
        {
            if (_missions != null) return _missions;
            // Fireteam Bravo 2: a map folder's game modes (DEM.ZDB, SUP.ZDB ...) or campaign missions (SP.ZDB,
            // DYNAMIC.ZDB) each hold a mission.rdr in CA's layout; the folder says which (FORT_SP, VILLAGE_SP2).
            // Fireteam Bravo: online folders start MP_ (MP_BANG_BUNKER: PM23.ZDB, PM23FFA.ZDB ...); the others are
            // campaign missions and their objective variants (BANG_BUNKER: P323.ZDB, P323_HE.ZDB ...).
            string folder = Path.GetFileName(Path.TrimEndingDirectorySeparator(Directory.Exists(SourcePath) ? SourcePath : Path.GetDirectoryName(SourcePath) ?? ""));
            string pspKind = IsFtb1
                ? folder.StartsWith("MP_", StringComparison.OrdinalIgnoreCase) ? "MP" : "SP"
                : System.Text.RegularExpressions.Regex.IsMatch(folder, "_SP[0-9]*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ? "SP" : "MP";
            int Rank(string kind) => kind switch { "SP" => 0, "IA" => 1, "MP" => 2, "TR" => 3, _ => 4 };
            return _missions = Resources.Where(r => r.Tag == "AIMm")
                .Select(r =>
                {
                    string id = Path.GetFileNameWithoutExtension(r.File.Replace('\\', '/')).ToUpperInvariant();
                    if (IsPsp) return new MissionInfo(id, r.File, pspKind,
                        IsFtb1 && System.Text.RegularExpressions.Regex.Match(id, @"^PM?\d+_?([A-Z]+)$") is { Success: true } mode ? mode.Groups[1].Value : "");
                    var m = System.Text.RegularExpressions.Regex.Match(id, @"^(SP|MP|IA|TR)\d+([A-Z]*)$");
                    return new MissionInfo(id, r.File, m.Success ? m.Groups[1].Value : "other", m.Success ? m.Groups[2].Value : "");
                })
                .GroupBy(m => m.Id).Select(g => g.First())
                .OrderBy(m => Rank(m.Kind)).ThenBy(m => m.Id, StringComparer.Ordinal).ToList();
        }

        /// <summary>A mission by id (case-insensitive) or null.</summary>
        public MissionInfo? FindMission(string id) => Missions().FirstOrDefault(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

        /// <summary>A mission's named point placed in the world: X / Z on its zone's grid, Y its own height.</summary>
        public sealed record MissionPoint(string Name, string Kind, int Zone, string? Island, int CellX, int CellZ, float X, float Y, float Z);

        /// <summary>
        /// The named points of a mission (mission.rdr) in world space. Each zone belongs to one island of the mission and
        /// is that island's AIMi grid section number (zone − the island's first zone). Kinds: <c>start</c> (s_ spawn
        /// points and squad starts), <c>waypoint</c> (n_ AI nav points), <c>marker</c> (named on the map screen),
        /// <c>spectator</c>, else <c>other</c> (doors, action and objective places).
        /// </summary>
        public List<MissionPoint> MissionPoints(string missionId)
        {
            var info = FindMission(missionId) ?? throw new KeyNotFoundException($"No mission {missionId} in this location");
            var res = Resources.First(r => r.Tag == "AIMm" && r.File == info.File);
            var mission = CaMissions.ParseMission(res.Data);
            var grids = CaMissions.ZoneGrids(mission, Resources);
            var list = new List<MissionPoint>();
            foreach (var zone in mission.Zones)
            {
                var (islandName, grid) = grids.GetValueOrDefault(zone.Index);
                foreach (var p in zone.Points)
                {
                    var (x, z) = CaMissions.Position(p, zone, grid);
                    list.Add(new MissionPoint(p.Name, PointKind(p), zone.Index, islandName, p.CellX, p.CellZ, x, p.Y, z));
                }
            }
            return list;
        }

        /// <summary>A model of <paramref name="kind"/> by its root hash (how character types and weapons name their
        /// models), or null.</summary>
        public Model? ModelByHash(uint hash, string kind) => hash == 0 ? null : Models()
            .FirstOrDefault(m => m.Kind == kind && _libraries[m.LibraryIndex].Lib.ModelHash(m.Offset) == hash);

        /// <summary>A mission's resource of <paramref name="tag"/> (in its own ZDB), or null.</summary>
        public Resource? MissionResource(string missionId, string tag)
        {
            var info = FindMission(missionId) ?? throw new KeyNotFoundException($"No mission {missionId} in this location");
            return Resources.FirstOrDefault(r => r.Tag == tag && r.File == info.File);
        }

        /// <summary>The package's weapon models by root hash: hash → name.</summary>
        public IReadOnlyDictionary<uint, string> WeaponModelHashes()
        {
            var map = new Dictionary<uint, string>();
            foreach (var m in Models().Where(m => m.Kind == "weapon")) map.TryAdd(_libraries[m.LibraryIndex].Lib.ModelHash(m.Offset), m.Name);
            return map;
        }

        /// <summary>A resource of GLOBAL.ZDB (the shared weapon, vehicle and turret tables), or null.</summary>
        public Resource? GlobalResource(string tag) =>
            Resources.FirstOrDefault(r => r.Tag == tag && Path.GetFileName(r.File).Equals("GLOBAL.ZDB", StringComparison.OrdinalIgnoreCase));

        private static string PointKind(CaMissions.Point p)
        {
            string n = p.Name;
            if (p.Marker) return "marker";
            if (n.Equals("spectator", StringComparison.OrdinalIgnoreCase)) return "spectator";
            if (n.StartsWith("s_", StringComparison.OrdinalIgnoreCase) || n.EndsWith("_Start", StringComparison.OrdinalIgnoreCase)
                || System.Text.RegularExpressions.Regex.IsMatch(n, @"^(able|bravo)\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return "start";
            if (n.StartsWith("n_", StringComparison.OrdinalIgnoreCase)) return "waypoint";
            return "other";
        }
    }
}
