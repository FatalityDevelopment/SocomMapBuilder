using CodeDesigner.Ps2.FileFormats.Assets;

namespace CodeDesigner.Ps2.FileFormats.Gta
{
    /// <summary>
    /// GTA: San Andreas (PS2) as an asset package: the disc image (or a loose MODELS/GTA3.IMG). Models come from the
    /// IMG archives (GTA3.IMG, GTA_INT.IMG), named and typed by the IDE files - DATA/PEDS.IDE (peds: characters),
    /// DEFAULT.IDE (weapons), VEHICLES.IDE (vehicles), DATA/MAPS/**.IDE (objects: props) - which also give each model's
    /// texture dictionary (and txdp parents). Native edits are held in memory for DFF or IMG copy saves.
    /// </summary>
    public sealed partial class GtaPackage
    {
        public sealed record Model(string Name, string Kind, string Txd, string Archive, int Id);

        public string SourcePath { get; }
        public bool IsDisc { get; }
        private readonly Dictionary<string, FileSpan> _disc = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<GtaImg> _imgs = new();
        private readonly Dictionary<string, GtaImg> _imgByArchive = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _playerArchives = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<Model, FileSpan> _modelFiles = new();
        private readonly Dictionary<Model, byte[]> _edited = new();
        private Model? _assembledCj;
        private Model? _looseOutfitPart;
        public string? InitialModel { get; private set; }
        private readonly List<Model> _models = new();
        private readonly Dictionary<string, string> _txdParent = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, RwTexDictionary?> _txds = new(StringComparer.OrdinalIgnoreCase);
        private readonly Queue<string> _txdOrder = new();
        private readonly Dictionary<Model, ModelMesh> _meshes = new();
        private List<GtaAnimations.Clip>? _clips;
        private Dictionary<int, string>? _boneNames;

        private GtaPackage(string path, bool disc) { SourcePath = path; IsDisc = disc; }

        private static string? DiscRoot(string path)
        {
            var dir = new DirectoryInfo(Directory.Exists(path) ? path : Path.GetDirectoryName(path)!);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "MODELS", "GTA3.IMG"))) return dir.FullName;
                dir = dir.Parent;
            }
            return null;
        }

        public static bool IsGtaPath(string path)
        {
            if (Directory.Exists(path))
            {
                string dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
                string? root = DiscRoot(dir);
                return root != null && (dir.Equals(root, StringComparison.OrdinalIgnoreCase)
                    || dir.Equals(Path.Combine(root, "MODELS"), StringComparison.OrdinalIgnoreCase));
            }
            if (!File.Exists(path)) return false;
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".iso")
            {
                try { return Iso9660.Files(path).ContainsKey("MODELS/GTA3.IMG"); }
                catch (Exception e) when (e is InvalidDataException or IOException) { return false; }
            }
            if (ext != ".img" && ext != ".dff") return false;
            using var f = File.OpenRead(path);
            Span<byte> head = stackalloc byte[4];
            return f.Read(head) == 4 && (ext == ".img" ? head.SequenceEqual("VER2"u8) : BitConverter.ToUInt32(head) == Rw.Clump);
        }

        public static GtaPackage Open(string path)
        {
            string full = Path.GetFullPath(path);
            bool directory = Directory.Exists(full);
            string ext = directory ? "" : Path.GetExtension(full).ToLowerInvariant();
            string? root = ext == ".iso" ? null : DiscRoot(full);
            var pkg = new GtaPackage(full, ext == ".iso" || root != null);
            if (ext == ".iso")
                foreach (var (k, v) in Iso9660.Files(full)) pkg._disc[k] = v;
            if (root != null)
                foreach (string folder in new[] { "MODELS", "DATA", "ANIM" })
                {
                    string dir = Path.Combine(root, folder);
                    if (!Directory.Exists(dir)) continue;
                    foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories))
                        pkg._disc[Path.GetRelativePath(root, f.FullName).Replace('\\', '/')] = new FileSpan(f.FullName, 0, f.Length);
                }

            string? selected = ext == ".img" ? Path.GetFileName(full) : null;
            if (selected != null) pkg.AddArchive(selected, new FileSpan(full, 0, new FileInfo(full).Length));
            if (pkg.IsDisc)
            {
                // GTA3_1.IMG is a second on-disc copy of GTA3.IMG. Open it only when explicitly selected.
                foreach (var file in new[] { "MODELS/GTA3.IMG", "MODELS/GTA_INT.IMG", "MODELS/PLAYER.IMG", "MODELS/CUTSCENE.IMG", "ANIM/CUTS.IMG" })
                    if (pkg._disc.TryGetValue(file, out var span) && !pkg._imgByArchive.ContainsKey(Path.GetFileName(file)))
                        pkg.AddArchive(Path.GetFileName(file), span);
            }
            if (ext == ".dff")
            {
                pkg.InitialModel = Path.GetFileNameWithoutExtension(full);
                if (root == null)
                {
                    foreach (var txd in new DirectoryInfo(Path.GetDirectoryName(full)!).EnumerateFiles("*.txd"))
                        pkg._disc[txd.Name] = new FileSpan(txd.FullName, 0, txd.Length);
                }
                string key = root == null ? Path.GetFileName(full) : Path.GetRelativePath(root, full).Replace('\\', '/');
                pkg._disc[key] = new FileSpan(full, 0, new FileInfo(full).Length);
            }
            pkg.LoadDefinitions(selected, ext == ".dff" ? full : null);
            if (ext == ".dff" && pkg._models.FirstOrDefault() is { } part
                && CjOutfit.Any(o => o.Dff.Equals(part.Name, StringComparison.OrdinalIgnoreCase))
                && HasClothingLayout(pkg.ReadModelData(part))) pkg._looseOutfitPart = part;
            if (pkg._disc.TryGetValue("DATA/CARCOLS.DAT", out var paints)) pkg.LoadCarPaint(paints.Read());
            return pkg;
        }

        private void AddArchive(string name, FileSpan span)
        {
            var img = new GtaImg(span);
            _imgByArchive.Add(name, img);
            _imgs.Add(img);
            // An IMG has no header flag for its role. A renamed player archive still contains the five
            // outfit meshes and GTA's three base skin dictionaries. Keep its physical name for reads and saves.
            if (name.Equals("PLAYER.IMG", StringComparison.OrdinalIgnoreCase) || CjOutfit.All(p => img.Find(p.Dff + ".dff") != null)
                && img.Find("player_torso.txd") != null && img.Find("player_legs.txd") != null && img.Find("player_feet.txd") != null)
                _playerArchives.Add(name);
        }

        private bool IsPlayerArchive(string? archive) => archive != null && _playerArchives.Contains(archive);

        public IReadOnlyList<Model> Models => _models;
        public IReadOnlyList<GtaImg> Archives => _imgs;

        public Model? FindModel(string name, string? archive = null) => _models.FirstOrDefault(m =>
            m.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrEmpty(archive) || m.Archive.Equals(archive, StringComparison.OrdinalIgnoreCase)));

        private void LoadDefinitions(string? selectedArchive, string? selectedFile)
        {
            var definitions = new Dictionary<string, (string Kind, string Txd, int Id)>(StringComparer.OrdinalIgnoreCase);
            foreach (var (path, span) in _disc.Where(f => f.Key.EndsWith(".IDE", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f.Key))
                foreach (var (section, cols) in Ide(span.Read()))
                {
                    if (section == "txdp" && cols.Length >= 2) { _txdParent[cols[0]] = cols[1]; continue; }
                    if (cols.Length < 3 || !int.TryParse(cols[0], out int id)) continue;
                    string? kind = section switch
                    {
                        "peds" => "character", "weap" => "weapon", "cars" => "vehicle",
                        "objs" or "tobj" or "anim" => "prop", _ => null,
                    };
                    if (kind != null) definitions.TryAdd(cols[1], (kind, cols[2], id));
                    // A timed object (tobj) ends with the hours it shows from and to (night-lit windows: 21, 6).
                    if (section == "tobj" && cols.Length >= 6 && int.TryParse(cols[^2], out int on) && int.TryParse(cols[^1], out int off))
                        _timedHours.TryAdd(cols[1], (on, off));
                    if (section == "cars" && cols.Length > 13) RegisterWheelSizes(cols);
                }

            void Add(string name, string archive, FileSpan data)
            {
                // An identically named clothing or cutscene model is independent of the world model's IDE entry.
                bool useIde = !IsPlayerArchive(archive)
                    && !archive.Equals("CUTSCENE.IMG", StringComparison.OrdinalIgnoreCase);
                Model model;
                if (useIde && definitions.TryGetValue(name, out var d)) model = new Model(name, d.Kind, d.Txd, archive, d.Id);
                else
                {
                    byte[] dff = data.Read();
                    string kind = HasSkin(dff) ? "character" : HasFrame(dff, "chassis") ? "vehicle" : "model";
                    model = new Model(name, kind, name, archive, -1);
                }
                _models.Add(model);
                _modelFiles.Add(model, data);
            }
            if (selectedFile == null)
            {
                foreach (var (archive, img) in _imgByArchive)
                {
                    if (selectedArchive != null && !archive.Equals(selectedArchive, StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (var e in img.Entries.Where(e => e.Name.EndsWith(".dff", StringComparison.OrdinalIgnoreCase)))
                        Add(e.Name[..^4], archive, new FileSpan(img.Source.Path, img.Source.Offset + e.Offset, e.Size));
                    if (IsPlayerArchive(archive) && CjOutfit.All(p => img.Find(p.Dff + ".dff") != null)
                        && img.Find(CjModel + ".dff") == null)
                    {
                        _assembledCj = new Model(CjModel, "character", "", archive, -1);
                        _models.Insert(0, _assembledCj);
                    }
                }
            }
            if (selectedArchive != null) return;
            foreach (var (key, span) in _disc.Where(e => e.Key.EndsWith(".dff", StringComparison.OrdinalIgnoreCase)))
            {
                if (selectedFile != null && !selectedFile.Equals(span.Path, StringComparison.OrdinalIgnoreCase)) continue;
                int end = key.LastIndexOf('/');
                Add(Path.GetFileNameWithoutExtension(key), end < 0 ? "LOOSE" : key[..end].ToUpperInvariant(), span);
            }
        }

        /// <summary>True when a DFF's geometry carries a Skin plugin (a skinned character).</summary>
        private static bool HasSkin(byte[] dff)
        {
            for (int i = 0; i + 12 <= dff.Length; i++)
                if (BitConverter.ToUInt32(dff, i) == Rw.Skin && BitConverter.ToUInt32(dff, i + 8) == 0x1803FFFF) return true;
            return false;
        }

        /// <summary>True when a DFF names a frame <paramref name="name"/> (a vehicle's "chassis").</summary>
        private static bool HasFrame(byte[] dff, string name) =>
            dff.AsSpan().IndexOf(System.Text.Encoding.ASCII.GetBytes(name)) >= 0;

        /// <summary>IDE text: "section" lines open a section, "end" closes it; entries are comma-separated.</summary>
        private static IEnumerable<(string Section, string[] Columns)> Ide(byte[] data)
        {
            string? section = null;
            foreach (var raw in System.Text.Encoding.ASCII.GetString(data).Split('\n'))
            {
                string line = raw.Split('#')[0].Trim();
                if (line.Length == 0) continue;
                if (section == null) { section = line.ToLowerInvariant(); continue; }
                if (line.Equals("end", StringComparison.OrdinalIgnoreCase)) { section = null; continue; }
                yield return (section, line.Split(',').Select(c => c.Trim()).ToArray());
            }
        }

        public byte[]? ReadFile(string name, string? archive = null)
        {
            if (archive == null)
            {
                foreach (var img in _imgs) if (img.Read(name) is { } data) return data;
                return _disc.GetValueOrDefault(name)?.Read();
            }
            if (_imgByArchive.TryGetValue(archive, out var source)) return source.Read(name);
            string key = archive == "LOOSE" ? name : archive + "/" + name;
            return _disc.GetValueOrDefault(key)?.Read();
        }

        public bool IsEditable(Model model) => _modelFiles.ContainsKey(model);

        public bool IsSourceFile(string fullPath)
        {
            string full = Path.GetFullPath(fullPath);
            return full.Equals(SourcePath, StringComparison.OrdinalIgnoreCase)
                || _imgs.Any(i => full.Equals(i.Source.Path, StringComparison.OrdinalIgnoreCase))
                || _disc.Values.Any(file => full.Equals(file.Path, StringComparison.OrdinalIgnoreCase));
        }

        public byte[] ReadModelData(Model model) => _edited.TryGetValue(model, out var bytes) ? (byte[])bytes.Clone()
            : _modelFiles.TryGetValue(model, out var span) ? span.Read()
            : throw new InvalidOperationException("CJ is assembled from individual clothing models. Edit a source model instead.");

        public void SetModelData(Model model, byte[]? edited)
        {
            if (!_modelFiles.ContainsKey(model))
                throw new InvalidOperationException("Select a source DFF to edit.");
            if (edited == null) _edited.Remove(model);
            else _edited[model] = (byte[])edited.Clone();
            _meshes.Remove(model);
            if (_assembledCj != null && model.Archive == _assembledCj.Archive) _meshes.Remove(_assembledCj);
        }

        /// <summary>Stream the original IMG to an atomic copy and write the edited DFFs to their own slots, and any other
        /// <paramref name="entries"/> (by entry name, e.g. an edited stream IPL) over theirs.</summary>
        public void SaveImgCopy(string archive, string outputPath, IReadOnlyDictionary<string, byte[]>? entries = null)
        {
            if (!_imgByArchive.TryGetValue(archive, out var img))
                throw new ArgumentException($"No IMG archive named {archive} in this package");
            string full = Path.GetFullPath(outputPath);
            if (!Path.GetExtension(full).Equals(".img", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Choose an .IMG output file.");
            if (IsSourceFile(full))
                throw new InvalidOperationException("Choose a separate output file for the IMG copy.");
            var changes = _edited.Where(e => e.Key.Archive.Equals(archive, StringComparison.OrdinalIgnoreCase)).ToArray();
            var others = (entries ?? new Dictionary<string, byte[]>()).Select(kv => (Entry: img.Find(kv.Key) ?? throw new KeyNotFoundException($"{kv.Key} is not in {archive}"), Bytes: kv.Value)).ToArray();
            if (changes.Length == 0 && others.Length == 0) throw new InvalidOperationException($"No edited models in {archive}.");
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            string temp = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var writer = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                {
                    img.CopyTo(writer);
                    foreach (var (model, bytes) in changes)
                        img.WriteEntry(writer, img.Find(model.Name + ".dff")!, bytes);
                    foreach (var (entry, bytes) in others)
                        img.WriteEntry(writer, entry, bytes);
                    writer.Flush(flushToDisk: true);
                }
                File.Move(temp, full, overwrite: true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        // ------------------------------------------------------------------ textures

        public sealed record TextureChoice(string Name, string Dictionary);

        private IEnumerable<string> TextureChain(string name)
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (string? t = name; !string.IsNullOrEmpty(t) && visited.Add(t); t = _txdParent.GetValueOrDefault(t))
                yield return t;
        }

        /// <summary>Texture names available in the model's assigned and inherited dictionaries, in lookup order.</summary>
        public IReadOnlyList<TextureChoice> TextureChoices(Model model)
        {
            var names = new Dictionary<string, TextureChoice>(StringComparer.OrdinalIgnoreCase);
            void Add(string dictionary, string archive)
            {
                if (Txd(dictionary, archive) is not { } txd) return;
                foreach (string name in txd.Names)
                    if (name.Length is > 0 and <= 31) names.TryAdd(name, new TextureChoice(name, dictionary));
            }
            foreach (string dict in TextureChain(model.Txd)) Add(dict, model.Archive);
            if ((IsPlayerArchive(model.Archive) || model == _looseOutfitPart) && ClothingTexture(model, 0) is { } clothing)
                foreach (string layer in clothing.Split('+'))
                    Add(layer[..layer.IndexOf('/')], model.Archive);
            if (model.Kind == "vehicle") Add("vehicle", "MODELS/GENERIC");
            return names.Values.OrderBy(c => c.Dictionary, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>A texture dictionary by name (IMG, then the disc's MODELS/**.TXD), cached.</summary>
        public RwTexDictionary? Txd(string name, string? archive = null)
        {
            if (string.IsNullOrEmpty(name)) return null;
            string file = name + ".txd";
            string key = archive + "|" + name;
            if (_txds.TryGetValue(key, out var hit)) return hit;
            byte[]? d = archive == null ? ReadFile(file) : ReadFile(file, archive);
            // An IDE parent may live in a different world archive. Clothing uses a separate set of
            // names; never bind a world material to a same-named PLAYER.IMG dictionary or vice versa.
            if (d == null && !IsPlayerArchive(archive))
            {
                if (archive != null)
                    foreach (var (other, img) in _imgByArchive)
                        if (!IsPlayerArchive(other) && !other.Equals(archive, StringComparison.OrdinalIgnoreCase) && img.Read(file) is { } bytes)
                        { d = bytes; break; }
                d ??= _disc.FirstOrDefault(kv => Path.GetFileName(kv.Key).Equals(file, StringComparison.OrdinalIgnoreCase)).Value?.Read();
            }
            RwTexDictionary? txd = null;
            if (d != null) try { txd = new RwTexDictionary(d); } catch (InvalidDataException) { }
            // Decoded RGBA images are much larger than the indexed TXDs on disc. Keep only recently loaded dictionaries.
            if (_txdOrder.Count >= 24) _txds.Remove(_txdOrder.Dequeue());
            _txdOrder.Enqueue(key);
            return _txds[key] = txd;
        }

        /// <summary>A texture named "&lt;txd&gt;/&lt;texture&gt;" (as the meshes name them): that dictionary, its txdp parents,
        /// then the generic vehicle dictionary.</summary>
        public RwTexDictionary.Texture? FindTexture(string name)
        {
            // "base+over": over alpha-blended onto base (CJ's clothes on his skin), at the larger size.
            int pipe = name.IndexOf('|');
            // Clothing layers are qualified with the same source archive. '+' is also valid in a
            // user's archive filename and in a single raster name.
            int plus = pipe >= 0 ? name.IndexOf("+" + name[..(pipe + 1)], pipe + 1, StringComparison.OrdinalIgnoreCase)
                : name.IndexOf('+', name.IndexOf('/') + 1);
            if (plus > 0 && name.IndexOf('/', plus + 1) >= 0)
            {
                var under = FindTexture(name[..plus]);
                var over = FindTexture(name[(plus + 1)..]);
                return under == null || over == null ? under ?? over : Composite(name, under, over);
            }
            string? archive = pipe > 0 ? name[..pipe] : null;
            if (archive != null) name = name[(pipe + 1)..];
            int slash = name.IndexOf('/');
            string txd = slash >= 0 ? name[..slash] : "", tex = slash >= 0 ? name[(slash + 1)..] : name;
            foreach (string t in TextureChain(txd))
                if (Txd(t, archive)?.Get(tex) is { } found) return found;
            var sharedVehicle = IsPlayerArchive(archive) ? null : Txd("vehicle", "MODELS/GENERIC")?.Get(tex);
            return sharedVehicle ?? FindUnambiguousTexture(archive, tex);
        }

        // Prefer the model's own archive. If the name is absent there, look in the other mounted
        // model sources. Require matching dimensions and pixels across every candidate at that level.
        private sealed record TextureSource(string Archive, string Dictionary);
        private readonly Dictionary<string, Dictionary<string, List<TextureSource>>> _textureNames = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<(string Archive, string Name), TextureSource?> _fallbackTxd = new();
        private string[]? _sharedTextureArchives;

        private static string TextureFolder(string path)
        {
            int slash = path.LastIndexOf('/');
            return slash < 0 ? "LOOSE" : path[..slash];
        }

        private Dictionary<string, List<TextureSource>> TextureIndex(string archive)
        {
            if (_textureNames.TryGetValue(archive, out var index)) return index;
            index = new(StringComparer.OrdinalIgnoreCase);
            IEnumerable<string> files = _imgByArchive.TryGetValue(archive, out var img)
                ? img.Entries.Select(e => e.Name)
                : _disc.Keys.Where(f => TextureFolder(f).Equals(archive, StringComparison.OrdinalIgnoreCase));
            foreach (string file in files.Where(f => f.EndsWith(".txd", StringComparison.OrdinalIgnoreCase)))
            {
                string dictionary = Path.GetFileNameWithoutExtension(file);
                if (Txd(dictionary, archive) is not { } txd) continue;
                var source = new TextureSource(archive, dictionary);
                foreach (string texture in txd.Names)
                {
                    if (!index.TryGetValue(texture, out var sources)) index[texture] = sources = new();
                    sources.Add(source);
                }
            }
            return _textureNames[archive] = index;
        }

        private RwTexDictionary.Texture? FindUnambiguousTexture(string? archive, string name)
        {
            if (string.IsNullOrEmpty(archive) || string.IsNullOrEmpty(name)) return null;
            var key = (archive.ToUpperInvariant(), name.ToUpperInvariant());
            RwTexDictionary.Texture? Read(TextureSource src) => Txd(src.Dictionary, src.Archive)?.Get(name);
            if (_fallbackTxd.TryGetValue(key, out var known)) return known == null ? null : Read(known);
            IEnumerable<TextureSource> candidates = Array.Empty<TextureSource>();
            if (TextureIndex(archive).TryGetValue(name, out var local)) candidates = local;
            else if (!IsPlayerArchive(archive))
            {
                _sharedTextureArchives ??= _imgByArchive.Keys.Concat(_disc.Keys.Where(f => f.EndsWith(".txd", StringComparison.OrdinalIgnoreCase)).Select(TextureFolder))
                    .Where(a => !IsPlayerArchive(a)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                candidates = _sharedTextureArchives.Where(a => !a.Equals(archive, StringComparison.OrdinalIgnoreCase))
                    .SelectMany(a => TextureIndex(a).TryGetValue(name, out var hits) ? (IEnumerable<TextureSource>)hits : Array.Empty<TextureSource>());
            }
            RwTexDictionary.Texture? first = null;
            TextureSource? selected = null;
            foreach (var candidate in candidates)
            {
                var texture = Read(candidate);
                if (texture == null || first != null && (first.Width != texture.Width || first.Height != texture.Height
                    || !first.Rgba.AsSpan().SequenceEqual(texture.Rgba)))
                {
                    _fallbackTxd[key] = null;
                    return null;
                }
                first ??= texture;
                selected ??= candidate;
            }
            _fallbackTxd[key] = selected;
            return first;
        }

        // ------------------------------------------------------------------ animations

        /// <summary>Every clip: the disc's ANIM/PED.IFP, then the .ifp packages in the IMG archives (source = file).</summary>
        public IReadOnlyList<GtaAnimations.Clip> Animations()
        {
            if (_clips != null) return _clips;
            var clips = new List<GtaAnimations.Clip>();
            if (IsDisc && _disc.TryGetValue("ANIM/PED.IFP", out var ped)) clips.AddRange(GtaAnimations.Parse(ped.Read(), "ped.ifp"));
            foreach (var img in _imgs)
                foreach (var e in img.Entries.Where(e => e.Name.EndsWith(".ifp", StringComparison.OrdinalIgnoreCase)))
                    try { clips.AddRange(GtaAnimations.Parse(img.Read(e), e.Name.ToLowerInvariant())); }
                    catch (ArgumentException) { }
            return _clips = clips;
        }

        public GtaAnimations.Clip? FindAnimation(string name, string? source) =>
            Animations().FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                && (source == null || c.Source.Equals(source, StringComparison.OrdinalIgnoreCase)));

        /// <summary>The ped skeleton's bone name for an HAnim id (every SA ped shares one hierarchy; read from the first
        /// ped with one), else <paramref name="fallback"/>.</summary>
        public string BoneName(int id, string fallback)
        {
            if (_boneNames == null)
            {
                _boneNames = new Dictionary<int, string>();
                foreach (var m in _models.Where(m => m.Kind == "character").Take(8))
                {
                    if (!IsEditable(m)) continue;
                    byte[] dff = ReadModelData(m);
                    var clump = RwClump.Parse(dff);
                    foreach (var f in clump.Frames) if (f.BoneId is int b && !string.IsNullOrEmpty(f.Name)) _boneNames.TryAdd(b, f.Name.Trim());
                    if (_boneNames.Count > 0) break;
                }
            }
            return _boneNames.TryGetValue(id, out var n) ? n : fallback;
        }

        private static RwTexDictionary.Texture Composite(string name, RwTexDictionary.Texture a, RwTexDictionary.Texture b)
        {
            int w = Math.Max(a.Width, b.Width), h = Math.Max(a.Height, b.Height);
            byte Sample(RwTexDictionary.Texture t, int x, int y, int k) => t.Rgba[((y * t.Height / h) * t.Width + x * t.Width / w) * 4 + k];
            var rgba = new byte[w * h * 4];
            bool transparent = false, translucent = false;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int alpha = Sample(b, x, y, 3), underAlpha = Sample(a, x, y, 3);
                    int underWeight = underAlpha * (255 - alpha), overWeight = alpha * 255, total = underWeight + overWeight;
                    for (int k = 0; k < 3; k++)
                        rgba[(y * w + x) * 4 + k] = total == 0 ? (byte)0 :
                            (byte)((Sample(b, x, y, k) * overWeight + Sample(a, x, y, k) * underWeight + total / 2) / total);
                    int outputAlpha = (total + 127) / 255;
                    rgba[(y * w + x) * 4 + 3] = (byte)outputAlpha;
                    if (outputAlpha < 250) { transparent = true; if (outputAlpha > 0) translucent = true; }
                }
            return new RwTexDictionary.Texture(name, w, h, 32, rgba, transparent) { HasTranslucency = translucent };
        }

        // ------------------------------------------------------------------ meshes

        // GTA's world is Z up; the editors are Y up: (x, y, z) -> (x, z, -y).
        private static readonly float[] ZUpToYUp = { 1, 0, 0, 0, 0, 0, -1, 0, 0, 1, 0, 0, 0, 0, 0, 1 };

        // Half a turn about Y.
        private static readonly float[] TurnAround = { -1, 0, 0, 0, 0, 1, 0, 0, 0, 0, -1, 0, 0, 0, 0, 1 };

        // A ped's game space (Z up) to its bind model space: (x, y, z) -> (-x, z, y); its own inverse.
        private static readonly float[] GameToModel = { -1, 0, 0, 0, 0, 0, 1, 0, 0, 1, 0, 0, 0, 0, 0, 1 };

        /// <summary>CJ ("cj"): the player is dressed at run time from PLAYER.IMG's clothes (one skinned part per body
        /// slot, textured from its own TXD); his opening outfit: bald head, white vest, denim jeans, Binco sneakers.</summary>
        public const string CjModel = "cj";

        private static readonly (string Dff, string[] Textures)[] CjOutfit =
        {
            ("head", new[] { "bald/bald", "bald/bald" }),          // the head textures are whole heads; CJ starts shaved
            ("hands", new[] { "hands/torso8bit" }),
            // Clothes are drawn over CJ's skin (the game's clothes builder): "skin+clothes" composites the two.
            ("vest", new[] { "player_torso/torso+vest/vest" }),
            ("jeans", new[] { "player_legs/legs+jeansdenim/legs" }),
            ("sneaker", new[] { "player_feet/foot+sneakerbincblk/sneakerbincblk" }),
        };

        private ModelMesh BuildCj(Model model)
        {
            var parts = new List<(RwClump, Func<RwClump.Geometry, int, string?>)>();
            foreach (var (dff, textures) in CjOutfit)
            {
                if (FindModel(dff, model.Archive) is not { } part) continue;
                byte[] data = ReadModelData(part);
                var scoped = textures.Select(t => ScopeClothingTexture(model.Archive, t)).ToArray();
                parts.Add((RwClump.Parse(data), (g, m) => scoped[Math.Clamp(m, 0, scoped.Length - 1)]));
            }
            if (parts.Count == 0) throw new KeyNotFoundException("PLAYER.IMG's clothes are not in this package");
            return Skinned(model, parts[0].Item1, parts);
        }

        public ModelMesh BuildMesh(Model model)
        {
            if (_meshes.TryGetValue(model, out var hit)) return hit;
            // Keep repeated describe/mesh requests fast without retaining the whole disc as the user browses.
            if (_meshes.Count >= 8) _meshes.Remove(_meshes.Keys.First());
            if (model == _assembledCj) return _meshes[model] = BuildCj(model);
            byte[] dff = ReadModelData(model);
            var clump = RwClump.Parse(dff);
            var mesh = clump.Geometries.Any(g => g.Skin != null) ? Skinned(model, clump) : Static(model, clump);
            return _meshes[model] = mesh;
        }

        /// <summary>Every named frame's origin in the static model space (Y up), e.g. a weapon's "gunflash" muzzle.</summary>
        public Dictionary<string, float[]> FramePoints(Model model)
        {
            byte[] dff = ReadModelData(model);
            var clump = RwClump.Parse(dff);
            var worlds = clump.FrameWorlds();
            var points = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < clump.Frames.Count; i++)
            {
                if (string.IsNullOrEmpty(clump.Frames[i].Name)) continue;
                var (x, y, z) = Matrix4.Apply(ZUpToYUp, worlds[i][12], worlds[i][13], worlds[i][14]);
                points.TryAdd(clump.Frames[i].Name.Trim(), new[] { x, y, z });
            }
            return points;
        }

        private GtaClothingTextures? _clothing;

        // The outfit DFFs each contain three plain skinned clumps. All use the same 38-frame body
        // hierarchy, with the body variant named on frame 1. A loose model named "head" may be unrelated.
        private static bool HasClothingLayout(byte[] data)
        {
            var clumps = Rw.Chunks(data, 0, data.Length).Where(c => c.Type == Rw.Clump).Take(4).ToArray();
            if (clumps.Length != 3) return false;
            string[] variants = { "Ripped", "Fat", "Normal" };
            for (int i = 0; i < variants.Length; i++)
            {
                var c = clumps[i];
                if (Rw.Find(data, c.Data, c.Data + c.Size, Rw.FrameList) is not { } frames) return false;
                var header = Rw.Find(data, frames.Data, frames.Data + frames.Size, Rw.Struct);
                if (header is not { } h || h.Size < 4 + 38 * 56 || BitConverter.ToInt32(data, h.Data) != 38) return false;
                var ext = Rw.Chunks(data, frames.Data, frames.Data + frames.Size).Where(p => p.Type == Rw.Extension).Skip(1).FirstOrDefault();
                if (Rw.Find(data, ext.Data, ext.Data + ext.Size, Rw.NodeName) is not { } name
                    || Rw.ReadString(data, name.Data, name.Size).Trim() != variants[i]) return false;
            }
            var mesh = RwClump.Parse(data);
            return mesh.Geometries.Any(g => g.Skin != null && (g.Flags & 0x01000000) == 0);
        }

        private string? TextureName(Model model, RwClump.Geometry g, int material)
        {
            if (material < 0 || material >= g.Materials.Count) return null;
            if (g.Materials[material] is { Length: > 0 } name)
                return LodTextureName(model, name) ?? $"{model.Archive}|{model.Txd}/{name}";
            return (IsPlayerArchive(model.Archive) || model == _looseOutfitPart) && ClothingTexture(model, material) is { } layers
                ? ScopeClothingTexture(model.Archive, layers) : null;
        }

        private static string ScopeClothingTexture(string archive, string texture) =>
            string.Join("+", texture.Split('+').Select(layer => archive + "|" + layer));

        private string? ClothingTexture(Model model, int material)
        {
            if (model == _looseOutfitPart || !_disc.ContainsKey("DATA/SHOPPING.DAT"))
                foreach (var (dff, layers) in CjOutfit)
                    if (model.Name.Equals(dff, StringComparison.OrdinalIgnoreCase))
                    {
                        string texture = layers[Math.Clamp(material, 0, layers.Length - 1)];
                        if (model != _looseOutfitPart) return texture;
                        foreach (string layer in texture.Split('+'))
                        {
                            int slash = layer.IndexOf('/');
                            string dict = layer[..slash];
                            string file = (model.Archive == "LOOSE" ? "" : model.Archive + "/") + dict + ".txd";
                            if (!_disc.ContainsKey(file) || Txd(dict, model.Archive)?.Names.Contains(layer[(slash + 1)..], StringComparer.OrdinalIgnoreCase) != true) return null;
                        }
                        return texture;
                    }
            _clothing ??= new GtaClothingTextures(ReadFile("DATA/SHOPPING.DAT"), ReadFile("DATA/CLOTHES.DAT"));
            return _clothing.For(model.Name, model.Archive, name => _imgByArchive[model.Archive].Find(name + ".txd") != null ? Txd(name, model.Archive) : null);
        }

        /// <summary>Weapons, vehicles, objects: every atomic placed by its frame, Z up turned Y up. Damage states
        /// (*_dam), low LODs (*_vlo, *_lo) and muzzle flashes are left out.</summary>
        private ModelMesh Static(Model model, RwClump clump)
        {
            var mesh = new ModelMesh { Archive = model.Archive, Name = model.Name };
            var worlds = clump.FrameWorlds();
            var occupied = clump.Atomics.Select(a => a.Frame).ToHashSet();
            static bool IsAlternate(string name) => name.EndsWith("_dam", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("_vlo", StringComparison.OrdinalIgnoreCase) || name.EndsWith("_lo", StringComparison.OrdinalIgnoreCase)
                || name.Contains("gunflash", StringComparison.OrdinalIgnoreCase);
            bool hasPrimary = clump.Atomics.Any(a => (uint)a.Frame < clump.Frames.Count && !IsAlternate(clump.Frames[a.Frame].Name));
            var wheelSizes = WheelSizes(model);
            foreach (var (frame, geometry) in clump.Atomics)
            {
                if (geometry < 0 || geometry >= clump.Geometries.Count) continue;
                string fname = frame >= 0 && frame < clump.Frames.Count ? clump.Frames[frame].Name : "";
                if (hasPrimary && IsAlternate(fname)) continue;
                var g = clump.Geometries[geometry];
                void Draw(string visual, float[] frameWorld)
                {
                    var world = Matrix4.Multiply(ZUpToYUp, frameWorld);
                    foreach (var m in g.Meshes)
                        AddChunk(mesh, m, g, TextureName(model, g, m.Material), visual, world, MaterialColor(model, g, m.Material));
                }
                int parent = frame >= 0 && frame < clump.Frames.Count ? clump.Frames[frame].Parent : -1;
                if (model.Kind == "vehicle" && parent >= 0 && parent < worlds.Count && clump.Frames[parent].Name is "wheel_rf_dummy" or "wheel_rf1_dummy"
                    && fname.StartsWith("wheel", StringComparison.OrdinalIgnoreCase))
                {
                    // SA instances the right-front wheel mesh at the other wheel dummies. Opposite-side
                    // wheels turn 180 degrees about GTA's vertical Z axis so the rim faces outwards.
                    for (int i = 0; i < clump.Frames.Count; i++)
                    {
                        string mount = clump.Frames[i].Name;
                        if (!mount.StartsWith("wheel_", StringComparison.OrdinalIgnoreCase) || !mount.EndsWith("_dummy", StringComparison.OrdinalIgnoreCase)
                            || mount.Length < 14 || mount[6] is not ('r' or 'l') || mount[7] is not ('f' or 'm' or 'b')) continue;
                        if (i != parent && (occupied.Contains(i) || clump.Atomics.Any(a =>
                            a.Frame >= 0 && a.Frame < clump.Frames.Count && clump.Frames[a.Frame].Parent == i))) continue;
                        float size = mount[7] == 'f' ? wheelSizes.Front : wheelSizes.Rear;
                        var local = Matrix4.Compose(0, 0, 0, scale: size);
                        if (mount[6] == 'l') { local[0] *= -1; local[5] *= -1; }
                        Draw(mount, Matrix4.Multiply(worlds[i], Matrix4.Multiply(local, clump.Frames[frame].Local)));
                    }
                }
                else Draw(fname, frame >= 0 && frame < worlds.Count ? worlds[frame] : Matrix4.Identity());
            }
            mesh.Recompute();
            return mesh;
        }

        private byte[] MaterialColor(Model model, RwClump.Geometry g, int material) =>
            material >= 0 && material < g.MaterialColors.Count ? PreviewMaterialColor(model, g.MaterialColors[material]) : WhiteMaterial;

        private static readonly byte[] WhiteMaterial = { 255, 255, 255, 255 };

        // RenderWare material RGBA modulates per-vertex prelighting. Editor colours use 128 as 1.0.
        private static byte[]? DrawColors(RwClump.Mesh mesh, byte[] material)
        {
            if (material.All(c => c == 255) && mesh.Batches.All(b => b.Colors == null)) return null;
            var result = new byte[mesh.Batches.Sum(b => b.Count) * 4];
            int at = 0;
            foreach (var batch in mesh.Batches)
                for (int i = 0; i < batch.Count * 4; i++)
                {
                    int prelit = batch.Colors != null && i < batch.Colors.Length ? batch.Colors[i] : 255;
                    result[at++] = (byte)Math.Round(prelit * material[i % 4] * (128.0 / (255 * 255)));
                }
            return result;
        }

        private static void AddChunk(ModelMesh mesh, RwClump.Mesh m, RwClump.Geometry g, string? texture, string visual,
            float[] world, byte[] material)
        {
            var pos = new List<float>(); var uv = new List<float>(); var nrm = new List<float>(); var tri = new List<int>();
            foreach (var b in m.Batches)
            {
                int baseIndex = pos.Count / 3;
                for (int i = 0; i < b.Count; i++)
                {
                    var (x, y, z) = Matrix4.Apply(world, b.Positions[3 * i], b.Positions[3 * i + 1], b.Positions[3 * i + 2]);
                    pos.Add(x); pos.Add(y); pos.Add(z);
                    uv.Add(b.Uvs.Length > 2 * i + 1 ? b.Uvs[2 * i] : 0);
                    uv.Add(b.Uvs.Length > 2 * i + 1 ? 1f - b.Uvs[2 * i + 1] : 0);          // RW V is top-down
                    float nx = 0, ny = 1, nz = 0;
                    if (b.Normals != null && b.Normals.Length >= 3 * i + 3) { nx = b.Normals[3 * i]; ny = b.Normals[3 * i + 1]; nz = b.Normals[3 * i + 2]; }
                    (nx, ny, nz) = (world[0] * nx + world[4] * ny + world[8] * nz, world[1] * nx + world[5] * ny + world[9] * nz, world[2] * nx + world[6] * ny + world[10] * nz);
                    nrm.Add(nx); nrm.Add(ny); nrm.Add(nz);
                }
                foreach (int t in RwClump.Triangles(b, g.TriStrip)) tri.Add(baseIndex + t);
            }
            if (tri.Count == 0) return;
            mesh.Chunks.Add(new ModelMesh.MeshChunk
            {
                Index = mesh.Chunks.Count, GeometryOffset = m.EditableUvOffset, Visual = visual, Texture = texture, Unit = "Float", Scale = 1,
                VertexCount = pos.Count / 3, Positions = pos.ToArray(), Uvs = uv.ToArray(), Normals = nrm.ToArray(),
                Colors = DrawColors(m, material), MaterialBlend = material[3] < 255 ? true : null, Triangles = tri.ToArray(),
            });
        }

        /// <summary>
        /// Peds: the skeleton is the frame tree (world matrices put the body Y up), skin bone b is the frame whose HAnim
        /// id is hierarchy entry b; a vertex part's bone-local position is the vertex through bone b's inverse bind matrix.
        /// </summary>
        private ModelMesh Skinned(Model model, RwClump clump, IReadOnlyList<(RwClump Clump, Func<RwClump.Geometry, int, string?> Texture)>? parts = null)
        {
            // Turned to face +Z like the other games' characters (GTA peds face -Z once Y up): the turn goes on the root
            // frames, so bones, skin and root motion all follow.
            // The Root bone's bind rotation holds game space -> model space (GameToModel); clips key Root in game space
            // (Z up). So that conversion moves up onto the clump frame and Root's local becomes game space: the bind
            // pose is unchanged and an animated Root stands upright.
            var locals = clump.Frames.Select(f => f.Local).ToList();
            int top = -1;
            for (int i = 0; i < clump.Frames.Count; i++)
                if (clump.Frames[i].BoneId == 0 && clump.Frames[i].Parent is int p && p >= 0 && clump.Frames[p].Parent < 0) { top = p; locals[i] = Matrix4.Multiply(GameToModel, locals[i]); break; }
            for (int i = 0; i < locals.Count; i++)
                if (clump.Frames[i].Parent < 0) locals[i] = Matrix4.Multiply(TurnAround, i == top ? Matrix4.Multiply(GameToModel, locals[i]) : locals[i]);
            var worlds = new List<float[]>();
            for (int i = 0; i < locals.Count; i++)
            {
                int parent = clump.Frames[i].Parent;
                worlds.Add(parent >= 0 && parent < i ? Matrix4.Multiply(worlds[parent], locals[i]) : locals[i]);
            }
            var bones = clump.Frames.Select((f, i) => new CharacterSkeleton.Bone(string.IsNullOrEmpty(f.Name) ? $"frame{i}" : f.Name.Trim(), i, f.Parent, i,
                locals[i], worlds[i])).ToList();
            var skeleton = CharacterSkeleton.FromBones(bones);
            var groups = new List<SkinnedMesh.Group>();
            parts ??= new[] { (clump, (Func<RwClump.Geometry, int, string?>)((g, m) => TextureName(model, g, m))) };
            foreach (var (src, textureOf) in parts)
            {
                // A part's frames onto the skeleton's (by name; the clothes all carry the same 38-frame skeleton).
                var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < bones.Count; i++) byName.TryAdd(bones[i].Name, i);
                int ToSkeleton(int frame) => frame >= 0 && frame < src.Frames.Count && byName.TryGetValue(string.IsNullOrEmpty(src.Frames[frame].Name) ? $"frame{frame}" : src.Frames[frame].Name.Trim(), out int k) ? k : frame;
                var hierarchy = src.Frames.FirstOrDefault(f => f.Hierarchy != null)?.Hierarchy ?? new List<RwClump.HAnimBone>();
                var frameOfId = new Dictionary<int, int>();
                for (int i = 0; i < src.Frames.Count; i++) if (src.Frames[i].BoneId is int id) frameOfId.TryAdd(id, i);
                int FrameOfBone(int b) => b >= 0 && b < hierarchy.Count && frameOfId.TryGetValue(hierarchy[b].Id, out int f) ? f : 0;

                foreach (var (frame, gi) in src.Atomics)
                {
                    if (gi < 0 || gi >= src.Geometries.Count) continue;
                    var g = src.Geometries[gi];
                    if (g.Skin == null) continue;
                    foreach (var m in g.Meshes)
                    {
                        var verts = new List<List<SkinnedMesh.Part>>();
                        var uvs = new List<float>();
                        var tris = new List<int>();
                        foreach (var b in m.Batches)
                        {
                            int baseIndex = verts.Count;
                            for (int i = 0; i < b.Count; i++)
                            {
                                var infl = new List<SkinnedMesh.Part>();
                                float x = b.Positions[3 * i], y = b.Positions[3 * i + 1], z = b.Positions[3 * i + 2];
                                float nx = b.Normals?.Length >= 3 * i + 3 ? b.Normals[3 * i] : 0, ny = b.Normals?.Length >= 3 * i + 3 ? b.Normals[3 * i + 1] : 1, nz = b.Normals?.Length >= 3 * i + 3 ? b.Normals[3 * i + 2] : 0;
                                for (int k = 0; k < 4 && b.Weights != null; k++)
                                {
                                    int bone = b.Bones![4 * i + k];
                                    float w = b.Weights[4 * i + k];
                                    if (bone < 0 || w <= 0 || bone >= g.Skin.InverseBind.Count) continue;
                                    var inv = g.Skin.InverseBind[bone];
                                    var (lx, ly, lz) = Matrix4.Apply(inv, x, y, z);
                                    float lnx = inv[0] * nx + inv[4] * ny + inv[8] * nz, lny = inv[1] * nx + inv[5] * ny + inv[9] * nz, lnz = inv[2] * nx + inv[6] * ny + inv[10] * nz;
                                    infl.Add(new SkinnedMesh.Part(ToSkeleton(FrameOfBone(bone)), lx, ly, lz, lnx, lny, lnz, w));
                                }
                                if (infl.Count == 0)                       // unweighted: model space, under the turned clump frame
                                {
                                    int root = src.Frames.FindIndex(f => f.Parent < 0);
                                    var (mx, my, mz) = root == top ? Matrix4.Apply(GameToModel, x, y, z) : (x, y, z);
                                    var (mnx, mny, mnz) = root == top ? Matrix4.Apply(GameToModel, nx, ny, nz) : (nx, ny, nz);
                                    infl.Add(new SkinnedMesh.Part(ToSkeleton(Math.Max(0, root)), mx, my, mz, mnx, mny, mnz, 1));
                                }
                                verts.Add(infl);
                                uvs.Add(b.Uvs.Length > 2 * i + 1 ? b.Uvs[2 * i] : 0);
                                uvs.Add(b.Uvs.Length > 2 * i + 1 ? 1f - b.Uvs[2 * i + 1] : 0);
                            }
                            foreach (int t in RwClump.Triangles(b, g.TriStrip)) tris.Add(baseIndex + t);
                        }
                        var color = MaterialColor(model, g, m.Material);
                        groups.Add(new SkinnedMesh.Group { Texture = textureOf(g, m.Material) ?? "", Section = m.Material, DrawOffset = model == _assembledCj ? -1 : m.EditableUvOffset,
                            Vertices = verts.ToArray(), Uvs = uvs.ToArray(), Triangles = tris.ToArray(), Colors = DrawColors(m, color),
                            MaterialBlend = color[3] < 255 ? true : null });
                    }
                }
            }
            return ModelMesh.FromGroups(model.Archive, model.Name, skeleton, groups, "Float", 1f);
        }
    }
}
