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

        public static CaPackage Open(string path)
        {
            byte[] data = File.ReadAllBytes(path);
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
                var lib = new CaLibrary(r.Data);
                if (lib.Version is < 1 or > 3 || lib.Models.Count == 0 && lib.Textures.Count == 0) continue;
                _libraries.Add((r, lib));
                // A version-3 header's second library (global_assets.rdr: every weapon, 88 models).
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

        /// <summary>One placed mesh object of a world: the node's world transform and the object.</summary>
        public sealed record Placement(string World, uint NodeHash, int LibraryIndex, int Object, float[] Matrix,
            string Instance, int NodeOffset, float[] ParentWorld);

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
            var objs = node.Objects.Where(o => lib.U32(o) == CaLibrary.StaticObjectType).ToList();
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

        /// <summary>Every placed object of every world piece of the location (UI scenes left out).</summary>
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
            }
            return list;
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
            if (model.Kind == "character") return BuildCharacter(model, lib, BoneName);
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
        public HashSet<int> SkippedNodes(Model model) => model.Kind is "prop" or "weapon"
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

        /// <summary>A texture by its <c>ca_&lt;hash&gt;</c> name (or hash): the first record with pixels in any library of the
        /// package (island records often only name a texture whose pixels are in a shared library such as farm_c1).</summary>
        public CaLibrary.Texture? FindTexture(string name)
        {
            string hex = name.StartsWith("ca_", StringComparison.OrdinalIgnoreCase) ? name[3..] : name;
            hex = Path.GetFileNameWithoutExtension(hex);
            if (!uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out uint hash)) return null;
            foreach (var (_, lib) in _libraries)
                foreach (int rec in lib.Textures)
                    if (lib.U32(rec + 0x4C) == hash && lib.DecodeTexture(rec) is { } t) return t;
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
            int Rank(string kind) => kind switch { "SP" => 0, "IA" => 1, "MP" => 2, "TR" => 3, _ => 4 };
            return _missions = Resources.Where(r => r.Tag == "AIMm")
                .Select(r =>
                {
                    string id = Path.GetFileNameWithoutExtension(r.File.Replace('\\', '/')).ToUpperInvariant();
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
