using CodeDesigner.Ps2.FileFormats.Assets;

namespace CodeDesigner.Ps2.FileFormats.Gta
{
    /// <summary>
    /// GTA: San Andreas (PS2) as an asset package: the disc image (or a loose MODELS/GTA3.IMG). Models come from the
    /// IMG archives (GTA3.IMG, GTA_INT.IMG), named and typed by the IDE files - DATA/PEDS.IDE (peds: characters),
    /// DEFAULT.IDE (weapons), VEHICLES.IDE (vehicles), DATA/MAPS/**.IDE (objects: props) - which also give each model's
    /// texture dictionary (and txdp parents). Read-only.
    /// </summary>
    public sealed class GtaPackage
    {
        public sealed record Model(string Name, string Kind, string Txd, string Archive, int Id);

        public string SourcePath { get; }
        public bool IsDisc { get; }
        private readonly Dictionary<string, FileSpan> _disc = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<GtaImg> _imgs = new();
        private readonly List<Model> _models = new();
        private readonly Dictionary<string, string> _txdParent = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, RwTexDictionary?> _txds = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ModelMesh> _meshes = new(StringComparer.OrdinalIgnoreCase);
        private List<GtaAnimations.Clip>? _clips;
        private Dictionary<int, string>? _boneNames;

        private GtaPackage(string path, bool disc) { SourcePath = path; IsDisc = disc; }

        /// <summary>True for a GTA disc image (MODELS/GTA3.IMG inside) or a VER2 IMG archive.</summary>
        public static bool IsGtaPath(string path)
        {
            if (!File.Exists(path)) return false;
            if (path.EndsWith(".img", StringComparison.OrdinalIgnoreCase))
            {
                using var f = File.OpenRead(path);
                var h = new byte[4];
                return f.Read(h, 0, 4) == 4 && h[0] == 'V' && h[1] == 'E' && h[2] == 'R' && h[3] == '2';
            }
            if (!path.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)) return false;
            try { return Iso9660.Files(path).ContainsKey("MODELS/GTA3.IMG"); }
            catch (Exception e) when (e is InvalidDataException or IOException) { return false; }
        }

        public static GtaPackage Open(string path)
        {
            bool disc = path.EndsWith(".iso", StringComparison.OrdinalIgnoreCase);
            var pkg = new GtaPackage(Path.GetFullPath(path), disc);
            if (disc)
            {
                foreach (var (k, v) in Iso9660.Files(path)) pkg._disc[k] = v;
                foreach (string img in new[] { "MODELS/GTA3.IMG", "MODELS/GTA_INT.IMG", "MODELS/PLAYER.IMG" })
                    if (pkg._disc.TryGetValue(img, out var span)) pkg._imgs.Add(new GtaImg(span));
            }
            else pkg._imgs.Add(new GtaImg(new FileSpan(path, 0, new FileInfo(path).Length)));
            pkg.LoadDefinitions();
            pkg.AddCj();
            return pkg;
        }

        public IReadOnlyList<Model> Models => _models;

        public IReadOnlyList<GtaImg> Archives => _imgs;

        public Model? FindModel(string name) => _models.FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        // ------------------------------------------------------------------ definitions

        private void LoadDefinitions()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Add(string name, string kind, string txd, int id)
            {
                if (!seen.Add(name)) return;
                string archive = ArchiveName(name + ".dff");
                if (archive.Length > 0) _models.Add(new Model(name, kind, txd, archive, id));
            }
            if (IsDisc)
            {
                foreach (var (path, span) in _disc.Where(f => f.Key.EndsWith(".IDE", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f.Key))
                {
                    string file = Path.GetFileName(path).ToUpperInvariant();
                    foreach (var (section, cols) in Ide(span.Read()))
                    {
                        if (cols.Length < 3) continue;
                        if (section == "txdp") { _txdParent[cols[0]] = cols[1]; continue; }
                        if (!int.TryParse(cols[0], out int id)) continue;
                        string kind = section switch
                        {
                            "peds" => "character",
                            "weap" => "weapon",
                            "cars" => "vehicle",
                            "objs" or "tobj" or "anim" => "prop",
                            _ => "",
                        };
                        if (kind.Length > 0) Add(cols[1], kind, cols[2], id);
                    }
                }
            }
            // Anything else in the archives (story/cutscene characters, CJ's body parts, cutscene props): by name, its own
            // .txd if there is one; skinned ones are characters.
            foreach (var img in _imgs)
                foreach (var e in img.Entries)
                    if (e.Name.EndsWith(".dff", StringComparison.OrdinalIgnoreCase) && !seen.Contains(e.Name[..^4]))
                    {
                        string n = e.Name[..^4];
                        byte[] dff = img.Read(e);
                        string kind = HasSkin(dff) ? "character" : HasFrame(dff, "chassis") ? "vehicle" : "model";
                        Add(n, kind, img.Find(n + ".txd") != null ? n : "", -1);
                    }
        }

        private void AddCj()
        {
            if (ArchiveName("head.dff") is { Length: > 0 } archive && FindModel(CjModel) == null)
                _models.Insert(0, new Model(CjModel, "character", "", archive, -1));
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

        /// <summary>The IMG (GTA3.IMG / GTA_INT.IMG) holding <paramref name="file"/>, or "".</summary>
        private string ArchiveName(string file)
        {
            foreach (var img in _imgs)
                if (img.Find(file) != null)
                    return IsDisc ? Path.GetFileName(_disc.First(kv => kv.Value == img.Source).Key) : Path.GetFileName(img.Source.Path);
            return "";
        }

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

        public byte[]? ReadFile(string name)
        {
            foreach (var img in _imgs) if (img.Read(name) is { } d) return d;
            return null;
        }

        // ------------------------------------------------------------------ textures

        /// <summary>A texture dictionary by name (IMG, then the disc's MODELS/**.TXD), cached.</summary>
        public RwTexDictionary? Txd(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (_txds.TryGetValue(name, out var hit)) return hit;
            byte[]? d = ReadFile(name + ".txd");
            if (d == null && IsDisc)
                d = _disc.FirstOrDefault(kv => Path.GetFileNameWithoutExtension(kv.Key).Equals(name, StringComparison.OrdinalIgnoreCase)
                    && kv.Key.EndsWith(".TXD", StringComparison.OrdinalIgnoreCase)).Value?.Read();
            RwTexDictionary? txd = null;
            if (d != null) try { txd = new RwTexDictionary(d); } catch (InvalidDataException) { }
            return _txds[name] = txd;
        }

        /// <summary>A texture named "&lt;txd&gt;/&lt;texture&gt;" (as the meshes name them): that dictionary, its txdp parents,
        /// then the generic vehicle dictionary.</summary>
        public RwTexDictionary.Texture? FindTexture(string name)
        {
            // "base+over": over alpha-blended onto base (CJ's clothes on his skin), at the larger size.
            int plus = name.IndexOf('+');
            if (plus > 0)
            {
                var under = FindTexture(name[..plus]);
                var over = FindTexture(name[(plus + 1)..]);
                return under == null || over == null ? under ?? over : Composite(name, under, over);
            }
            int slash = name.IndexOf('/');
            string txd = slash > 0 ? name[..slash] : "", tex = slash > 0 ? name[(slash + 1)..] : name;
            for (string? t = txd; !string.IsNullOrEmpty(t); t = _txdParent.GetValueOrDefault(t))
                if (Txd(t)?.Get(tex) is { } found) return found;
            return Txd("vehicle")?.Get(tex);
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
                    if (ReadFile(m.Name + ".dff") is not { } dff) continue;
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
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int alpha = Sample(b, x, y, 3);
                    for (int k = 0; k < 3; k++)
                        rgba[(y * w + x) * 4 + k] = (byte)((Sample(b, x, y, k) * alpha + Sample(a, x, y, k) * (255 - alpha) + 127) / 255);
                    rgba[(y * w + x) * 4 + 3] = Math.Max(Sample(a, x, y, 3), (byte)alpha);
                }
            return new RwTexDictionary.Texture(name, w, h, 32, rgba, rgba.Where((v, i) => i % 4 == 3).Any(v => v < 255));
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
                if (ReadFile(dff + ".dff") is not { } data) continue;
                parts.Add((RwClump.Parse(data), (g, m) => textures[Math.Clamp(m, 0, textures.Length - 1)]));
            }
            if (parts.Count == 0) throw new KeyNotFoundException("PLAYER.IMG's clothes are not in this package");
            return Skinned(model, parts[0].Item1, parts);
        }

        public ModelMesh BuildMesh(Model model)
        {
            if (_meshes.TryGetValue(model.Name, out var hit)) return hit;
            if (model.Name == CjModel) return _meshes[model.Name] = BuildCj(model);
            byte[] dff = ReadFile(model.Name + ".dff") ?? throw new KeyNotFoundException($"{model.Name}.dff is not in the archives");
            var clump = RwClump.Parse(dff);
            var mesh = clump.Geometries.Any(g => g.Skin != null) ? Skinned(model, clump) : Static(model, clump);
            return _meshes[model.Name] = mesh;
        }

        /// <summary>Every named frame's origin in the static model space (Y up), e.g. a weapon's "gunflash" muzzle.</summary>
        public Dictionary<string, float[]> FramePoints(Model model)
        {
            byte[] dff = ReadFile(model.Name + ".dff") ?? throw new KeyNotFoundException($"{model.Name}.dff is not in the archives");
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

        private string? TextureName(Model model, RwClump.Geometry g, int material) =>
            material >= 0 && material < g.Materials.Count && g.Materials[material] is { } tex ? $"{model.Txd}/{tex}" : null;

        /// <summary>Weapons, vehicles, objects: every atomic placed by its frame, Z up turned Y up. Damage states
        /// (*_dam), low LODs (*_vlo, *_lo) and muzzle flashes are left out.</summary>
        private ModelMesh Static(Model model, RwClump clump)
        {
            var mesh = new ModelMesh { Archive = model.Archive, Name = model.Name };
            var worlds = clump.FrameWorlds();
            foreach (var (frame, geometry) in clump.Atomics)
            {
                if (geometry < 0 || geometry >= clump.Geometries.Count) continue;
                string fname = frame >= 0 && frame < clump.Frames.Count ? clump.Frames[frame].Name : "";
                if (fname.EndsWith("_dam", StringComparison.OrdinalIgnoreCase) || fname.EndsWith("_vlo", StringComparison.OrdinalIgnoreCase)
                    || fname.EndsWith("_lo", StringComparison.OrdinalIgnoreCase) || fname.Contains("gunflash", StringComparison.OrdinalIgnoreCase)) continue;
                var world = Matrix4.Multiply(ZUpToYUp, frame >= 0 && frame < worlds.Count ? worlds[frame] : Matrix4.Identity());
                var g = clump.Geometries[geometry];
                foreach (var m in g.Meshes)
                    AddChunk(mesh, m, g, TextureName(model, g, m.Material), fname, (x, y, z) => Matrix4.Apply(world, x, y, z), world);
            }
            mesh.Recompute();
            return mesh;
        }

        private static void AddChunk(ModelMesh mesh, RwClump.Mesh m, RwClump.Geometry g, string? texture, string visual,
            Func<float, float, float, (float, float, float)> place, float[]? rotate)
        {
            var pos = new List<float>(); var uv = new List<float>(); var nrm = new List<float>(); var col = new List<byte>(); var tri = new List<int>();
            bool colours = m.Batches.Any(b => b.Colors != null);
            foreach (var b in m.Batches)
            {
                int baseIndex = pos.Count / 3;
                for (int i = 0; i < b.Count; i++)
                {
                    var (x, y, z) = place(b.Positions[3 * i], b.Positions[3 * i + 1], b.Positions[3 * i + 2]);
                    pos.Add(x); pos.Add(y); pos.Add(z);
                    uv.Add(b.Uvs.Length > 2 * i + 1 ? b.Uvs[2 * i] : 0);
                    uv.Add(b.Uvs.Length > 2 * i + 1 ? 1f - b.Uvs[2 * i + 1] : 0);          // RW V is top-down
                    float nx = 0, ny = 1, nz = 0;
                    if (b.Normals != null && b.Normals.Length >= 3 * i + 3) { nx = b.Normals[3 * i]; ny = b.Normals[3 * i + 1]; nz = b.Normals[3 * i + 2]; }
                    if (rotate != null) (nx, ny, nz) = (rotate[0] * nx + rotate[4] * ny + rotate[8] * nz, rotate[1] * nx + rotate[5] * ny + rotate[9] * nz, rotate[2] * nx + rotate[6] * ny + rotate[10] * nz);
                    nrm.Add(nx); nrm.Add(ny); nrm.Add(nz);
                    if (colours)
                        for (int k = 0; k < 4; k++)                                          // RW prelit 255 = 1.0, editors 0x80
                            col.Add(b.Colors != null && b.Colors.Length >= 4 * i + 4 ? (byte)(k < 3 ? b.Colors[4 * i + k] / 2 : b.Colors[4 * i + k] / 2) : (byte)0x80);
                }
                foreach (int t in RwClump.Triangles(b, g.TriStrip)) tri.Add(baseIndex + t);
            }
            if (tri.Count == 0) return;
            mesh.Chunks.Add(new ModelMesh.MeshChunk
            {
                Index = mesh.Chunks.Count, Visual = visual, Texture = texture, Unit = "Float", Scale = 1,
                VertexCount = pos.Count / 3, Positions = pos.ToArray(), Uvs = uv.ToArray(), Normals = nrm.ToArray(),
                Colors = colours ? col.ToArray() : null, Triangles = tri.ToArray(),
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
                        groups.Add(new SkinnedMesh.Group { Texture = textureOf(g, m.Material) ?? "", Section = m.Material, DrawOffset = groups.Count, Vertices = verts.ToArray(), Uvs = uvs.ToArray(), Triangles = tris.ToArray() });
                    }
                }
            }
            return ModelMesh.FromGroups(model.Archive, model.Name, skeleton, groups, "Float", 1f);
        }
    }
}
