using CodeDesigner.Ps2.FileFormats.Assets;
using CodeDesigner.Ps2.FileFormats.Gta;

namespace CodeDesigner.Ps2.FileFormats.Swbf
{
    /// <summary>
    /// Star Wars: Battlefront (PS2) as an asset package: the disc image (every DATA/_LVL_PS2/**.LVL but the sound and
    /// movie stores) or one loose .LVL. Each level is indexed once for its models, textures and skeletons (nested
    /// "lvl_" levels included); the level bytes are read again on demand, a few kept at a time. Read-only.
    /// </summary>
    public sealed class SwbfPackage
    {
        public const string LevelRoot = "DATA/_LVL_PS2/";

        /// <summary>A model: <see cref="Archive"/> is the level (e.g. TAT/TAT2.LVL), <see cref="Sub"/> the nested level's
        /// position in it (-1 for the top level).</summary>
        public sealed record Model(string Name, string Kind, string Archive, int Level, int Offset, int Sub, int Segments, int Vertices, int Triangles, bool Skinned, int Size);
        public sealed record TextureRef(string Name, string Archive, int Level, int Offset, int Sub, int Width, int Height, int Depth, int Size);
        public sealed record Level(string Name, FileSpan Span);
        /// <summary>A "wrld" chunk: a map (or a layer of one) in <see cref="Archive"/>.</summary>
        public sealed record WorldRef(string Name, string Archive, int Level, int Offset, string? Terrain, string? Sky, int Instances, int Regions);

        public string SourcePath { get; }
        public bool IsDisc { get; }
        public IReadOnlyList<Level> Levels => _levels;
        public IReadOnlyList<Model> Models => _models;
        public IReadOnlyList<TextureRef> Textures => _textures;
        public IReadOnlyList<WorldRef> Worlds => _worlds;
        /// <summary>The disc's files (path upper case, '/' separated), or empty for a loose level.</summary>
        public IReadOnlyDictionary<string, FileSpan> DiscFiles => _disc;

        private readonly Dictionary<string, FileSpan> _disc = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<Level> _levels = new();
        private readonly List<Model> _models = new();
        private readonly List<TextureRef> _textures = new();
        private readonly Dictionary<string, List<TextureRef>> _texByName = new(StringComparer.OrdinalIgnoreCase);
        // A model's skeleton ("skel" named after it) per level: (level, name) -> chunk offset.
        private readonly Dictionary<(int Level, string Name), int> _skels = new();
        private readonly List<WorldRef> _worlds = new();
        // "PATH" containers per level (chunk offsets).
        private readonly Dictionary<int, List<int>> _paths = new();
        // Terrains per level by lower-case name -> chunk offset; entity classes and geometry tables by lower-case name.
        private readonly Dictionary<(int Level, string Name), int> _terrains = new();
        private readonly Dictionary<string, List<(int Level, SwbfWorld.EntityClass Class)>> _classes = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<(int Level, string Model)>> _geometry = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ModelMesh> _meshes = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, SwbfTexture> _decoded = new(StringComparer.OrdinalIgnoreCase);
        private readonly LinkedList<(int Level, byte[] Data)> _bytes = new();
        private const int KeepLevels = 4;

        private SwbfPackage(string path, bool disc) { SourcePath = path; IsDisc = disc; }

        /// <summary>True for a Battlefront disc image (SWBFRONT.ELF and DATA/_LVL_PS2 inside) or a loose .LVL.</summary>
        public static bool IsSwbfPath(string path)
        {
            if (!File.Exists(path)) return false;
            if (path.EndsWith(".lvl", StringComparison.OrdinalIgnoreCase))
            {
                using var f = File.OpenRead(path);
                var h = new byte[8];
                return f.Read(h, 0, 8) == 8 && Ucfb.IsLevel(h);
            }
            if (!path.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)) return false;
            try { return IsSwbfDisc(Iso9660.Files(path)); }
            catch (Exception e) when (e is InvalidDataException or IOException or EndOfStreamException) { return false; }
        }

        public static bool IsSwbfDisc(IReadOnlyDictionary<string, FileSpan> files) =>
            files.ContainsKey("SWBFRONT.ELF") && files.Keys.Any(k => k.StartsWith(LevelRoot, StringComparison.OrdinalIgnoreCase));

        public static SwbfPackage Open(string path)
        {
            string full = Path.GetFullPath(path);
            bool disc = full.EndsWith(".iso", StringComparison.OrdinalIgnoreCase);
            var pkg = new SwbfPackage(full, disc);
            if (disc)
            {
                foreach (var (k, v) in Iso9660.Files(full)) pkg._disc[k] = v;
                foreach (var (k, v) in pkg._disc.Where(f => IsIndexedLevel(f.Key)).OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase))
                    pkg._levels.Add(new Level(k[LevelRoot.Length..], v));
            }
            else pkg._levels.Add(new Level(Path.GetFileName(full).ToUpperInvariant(), new FileSpan(full, 0, new FileInfo(full).Length)));
            for (int i = 0; i < pkg._levels.Count; i++) pkg.Index(i);
            return pkg;
        }

        /// <summary>Levels worth indexing: not the streamed sound banks or movies.</summary>
        public static bool IsIndexedLevel(string discPath) =>
            discPath.StartsWith(LevelRoot, StringComparison.OrdinalIgnoreCase) && discPath.EndsWith(".LVL", StringComparison.OrdinalIgnoreCase)
            && !discPath.StartsWith(LevelRoot + "SOUND/", StringComparison.OrdinalIgnoreCase)
            && !discPath.StartsWith(LevelRoot + "MOVIES/", StringComparison.OrdinalIgnoreCase);

        /// <summary>A streamed sound bank or movie store (DATA/_LVL_PS2/SOUND, MOVIES): not a level of models.</summary>
        public static bool IsStreamLevel(string path)
        {
            string p = "/" + path.Replace('\\', '/');
            return p.Contains("/SOUND/", StringComparison.OrdinalIgnoreCase) || p.Contains("/MOVIES/", StringComparison.OrdinalIgnoreCase);
        }

        public int LevelIndex(string archive) => _levels.FindIndex(l => l.Name.Equals(archive.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));

        // ------------------------------------------------------------------ index

        private void Index(int level)
        {
            byte[] data;
            try { data = LevelBytes(level); }
            catch (IOException) { return; }
            if (!Ucfb.IsLevel(data)) return;     // TEST/TESTH.LVL is a zip
            var root = Ucfb.Root(data);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Walk(IEnumerable<Ucfb> run, int sub)
            {
                foreach (var c in run)
                {
                    switch (c.Tag)
                    {
                        case "lvl_":
                            Walk(c.Children, c.Offset);
                            break;
                        case "modl":
                        {
                            string name = SwbfModel.Name(c);
                            if (name.Length == 0 || !seen.Add(name)) break;
                            var segs = SwbfModel.Segments(c).ToList();
                            bool skinned = segs.Any(s => s.Skinned);
                            _models.Add(new Model(name, KindOf(name, _levels[level].Name, skinned), _levels[level].Name, level, c.Offset, sub,
                                segs.Count, segs.Sum(s => s.Vertices), segs.Sum(s => Math.Max(0, s.Indices - 2)), skinned, c.Size));
                            break;
                        }
                        case "skel":
                        {
                            string name = c.Child("INFO") is { } si ? si.Text() : "";
                            if (name.Length > 0) _skels.TryAdd((level, name.ToLowerInvariant()), c.Offset);
                            break;
                        }
                        case "wrld":
                        {
                            var w = SwbfWorld.Parse(c);
                            _worlds.Add(new WorldRef(w.Name, _levels[level].Name, level, c.Offset, w.Terrain, w.Sky, w.Instances.Count, w.Regions.Count));
                            break;
                        }
                        case "PATH":
                            if (!_paths.TryGetValue(level, out var pathList)) _paths[level] = pathList = new();
                            pathList.Add(c.Offset);
                            break;
                        case "tern":
                        {
                            string name = c.ChildText("NAME") ?? "";
                            if (name.Length > 0 && SwbfTerrain.HasHeights(c)) _terrains.TryAdd((level, name.ToLowerInvariant()), c.Offset);
                            break;
                        }
                        case "entc":
                        {
                            var e = SwbfWorld.ParseClass(c);
                            if (e.Type.Length == 0) break;
                            if (!_classes.TryGetValue(e.Type, out var list)) _classes[e.Type] = list = new();
                            list.Add((level, e));
                            break;
                        }
                        case "gmod":
                        {
                            var (name, model) = SwbfWorld.ParseGeometry(c);
                            if (name.Length == 0 || model == null) break;
                            if (!_geometry.TryGetValue(name, out var list)) _geometry[name] = list = new();
                            list.Add((level, model));
                            break;
                        }
                        case "tex_":
                        {
                            string name = c.ChildText("NAME") ?? "";
                            if (name.Length == 0) break;
                            var (w, h, d) = SwbfTexture.Info(c);
                            var t = new TextureRef(name, _levels[level].Name, level, c.Offset, sub, w, h, d, c.Size);
                            _textures.Add(t);
                            if (!_texByName.TryGetValue(name, out var list)) _texByName[name] = list = new List<TextureRef>();
                            list.Add(t);
                            break;
                        }
                    }
                }
            }
            Walk(root.Children, -1);
        }

        /// <summary>A model's kind from Battlefront's naming (side_class_name) and where it lives.</summary>
        public static string KindOf(string name, string level, bool skinned)
        {
            string n = name.ToLowerInvariant();
            if (level.StartsWith("FPM/", StringComparison.OrdinalIgnoreCase)) return "first-person";
            if (n.Contains("_weap_") || n.StartsWith("weap_")) return "weapon";
            if (n.Contains("_inf_")) return "character";
            if (n.Contains("_hover_") || n.Contains("_walk_") || n.Contains("_fly_") || n.Contains("_tread_")) return "vehicle";
            // sky domes and their clouds, rocks and planets; not a skyway building
            if (System.Text.RegularExpressions.Regex.IsMatch(n, "(^|_)sky(?!way|line|light|scraper)")) return "sky";
            return "prop";
        }

        // ------------------------------------------------------------------ reads

        // ------------------------------------------------------------------ edits

        // Levels changed in this session: their whole bytes, edited in place (placements keep every chunk's size).
        private readonly Dictionary<int, byte[]> _edited = new();

        /// <summary>Overwrites <paramref name="bytes"/> at <paramref name="at"/> in a level (a copy of it, kept in memory).</summary>
        public void Patch(int level, int at, byte[] bytes)
        {
            lock (_edited)
            {
                if (!_edited.TryGetValue(level, out var data)) _edited[level] = data = (byte[])OriginalBytes(level).Clone();
                if (at < 0 || at + bytes.Length > data.Length) throw new ArgumentOutOfRangeException(nameof(at), "The edit is outside the level");
                bytes.CopyTo(data, at);
            }
        }

        public byte[] ReadAt(int level, int at, int count) => LevelBytes(level).AsSpan(at, count).ToArray();

        /// <summary>The levels whose bytes now differ from the disc or file.</summary>
        public List<int> DirtyLevels()
        {
            lock (_edited)
                return _edited.Where(e => !e.Value.AsSpan().SequenceEqual(OriginalBytes(e.Key))).Select(e => e.Key).OrderBy(l => l).ToList();
        }

        /// <summary>A level as it is on the disc or in its file, edits aside.</summary>
        public byte[] OriginalBytes(int level)
        {
            lock (_bytes)
                for (var n = _bytes.First; n != null; n = n.Next)
                    if (n.Value.Level == level) return n.Value.Data;
            byte[] data = _levels[level].Span.Read();
            lock (_bytes)
            {
                _bytes.AddFirst((level, data));
                while (_bytes.Count > KeepLevels) _bytes.RemoveLast();
            }
            return data;
        }

        /// <summary>Writes a level as it is now (with this session's edits).</summary>
        public void WriteLevel(int level, string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            string temp = path + ".tmp";
            File.WriteAllBytes(temp, LevelBytes(level));
            File.Move(temp, path, true);
        }

        /// <summary>A copy of the disc image with the edited levels written over their own bytes (an edit never changes
        /// a level's size, so every file keeps its place).</summary>
        public void WriteDisc(string path)
        {
            if (!IsDisc) throw new InvalidOperationException("Only a disc session saves a disc image");
            if (Path.GetFullPath(path).Equals(SourcePath, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Save the disc to a new file, not over the original");
            string temp = path + ".tmp";
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!);
            long need = new FileInfo(SourcePath).Length;
            if (drive.IsReady && drive.AvailableFreeSpace < need)
                throw new IOException($"The disc copy needs {need / (1 << 20):N0} MB; {drive.Name} has {drive.AvailableFreeSpace / (1 << 20):N0} MB free. Save the edited levels to a folder instead.");
            try
            {
                File.Copy(SourcePath, temp, true);
                using (var f = new FileStream(temp, FileMode.Open, FileAccess.Write))
                    foreach (int level in DirtyLevels())
                    {
                        var span = _levels[level].Span;
                        var data = LevelBytes(level);
                        if (data.Length != span.Length) throw new InvalidOperationException($"{_levels[level].Name} changed size");
                        f.Position = span.Offset;
                        f.Write(data);
                    }
                File.Move(temp, path, true);
            }
            catch
            {
                try { File.Delete(temp); } catch (IOException) { }
                throw;
            }
        }

        public byte[] LevelBytes(int level)
        {
            lock (_edited)
                if (_edited.TryGetValue(level, out var edited)) return edited;
            lock (_bytes)
            {
                for (var n = _bytes.First; n != null; n = n.Next)
                    if (n.Value.Level == level)
                    {
                        _bytes.Remove(n);
                        _bytes.AddFirst(n);
                        return n.Value.Data;
                    }
            }
            byte[] data = _levels[level].Span.Read();
            lock (_bytes)
            {
                _bytes.AddFirst((level, data));
                while (_bytes.Count > KeepLevels) _bytes.RemoveLast();
            }
            return data;
        }

        private Ucfb ChunkAt(int level, int offset)
        {
            byte[] data = LevelBytes(level);
            int size = (int)BitConverter.ToUInt32(data, offset + 4);
            return new Ucfb(data, offset, size, System.Text.Encoding.ASCII.GetString(data, offset, 4));
        }

        /// <summary>The model named <paramref name="name"/>, in <paramref name="archive"/> when given.</summary>
        public Model? FindModel(string name, string? archive = null)
        {
            if (!string.IsNullOrEmpty(archive))
            {
                string a = archive.Replace('\\', '/');
                var m = _models.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && x.Archive.Equals(a, StringComparison.OrdinalIgnoreCase));
                if (m != null) return m;
            }
            return _models.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        public ModelMesh BuildMesh(Model model)
        {
            string key = model.Archive + ":" + model.Name;
            lock (_meshes)
                if (_meshes.TryGetValue(key, out var hit)) return hit;
            Ucfb? skel = _skels.TryGetValue((model.Level, model.Name.ToLowerInvariant()), out int at) ? ChunkAt(model.Level, at) : null;
            var mesh = SwbfModel.Build(ChunkAt(model.Level, model.Offset), model.Archive, skel);
            lock (_meshes)
            {
                if (_meshes.Count > 64) _meshes.Clear();
                _meshes[key] = mesh;
            }
            return mesh;
        }

        // ------------------------------------------------------------------ maps

        /// <summary>The model name a terrain's mesh goes by (<see cref="BuildTerrainMesh"/>).</summary>
        public const string TerrainPrefix = "@terrain:";

        public SwbfWorld.World ReadWorld(WorldRef w) => SwbfWorld.Parse(ChunkAt(w.Level, w.Offset));

        /// <summary>The paths of a level (every PATH container in it, nested levels included).</summary>
        public List<SwbfWorld.Path> PathsOf(string archive)
        {
            int level = LevelIndex(archive);
            var list = new List<SwbfWorld.Path>();
            if (level >= 0 && _paths.TryGetValue(level, out var offsets))
                foreach (int at in offsets) list.AddRange(SwbfWorld.ParsePaths(ChunkAt(level, at)));
            return list;
        }

        /// <summary>The worlds of a level (by archive, e.g. TAT/TAT2.LVL).</summary>
        public IEnumerable<WorldRef> WorldsIn(string archive) =>
            _worlds.Where(w => w.Archive.Equals(archive.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));

        /// <summary>The model an entity class draws: its GeometryName (inherited through BASE), through a gmod's best
        /// LOD when one has that name; the class's own level first. Null when the class draws nothing found.</summary>
        public Model? ModelForClass(string cls, int level)
        {
            string archive = _levels[level].Name;
            string? geometry = null;
            string current = cls;
            for (int depth = 0; depth < 6 && geometry == null; depth++)
            {
                if (!_classes.TryGetValue(current, out var list) || list.Count == 0) break;
                var e = list.FirstOrDefault(x => x.Level == level).Class ?? list[0].Class;
                geometry = e.Geometry;
                if (e.Base.Length == 0 || e.Base.Equals(current, StringComparison.OrdinalIgnoreCase)) break;
                current = e.Base;
            }
            geometry ??= cls;
            string model = geometry;
            if (_geometry.TryGetValue(geometry, out var g) && g.Count > 0)
                model = (g.FirstOrDefault(x => x.Level == level).Model ?? g[0].Model);
            return FindModel(model, archive) ?? (model != geometry ? FindModel(geometry, archive) : null);
        }

        private readonly Dictionary<string, SwbfTerrain> _terrainCache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>A terrain by name, in <paramref name="archive"/>'s level first; null when no level has its heights.</summary>
        public SwbfTerrain? Terrain(string name, string archive)
        {
            int level = LevelIndex(archive);
            string key = name.ToLowerInvariant();
            int at;
            if (!_terrains.TryGetValue((level, key), out at))
            {
                var other = _terrains.FirstOrDefault(t => t.Key.Name == key);
                if (other.Key.Name == null) return null;
                (level, at) = (other.Key.Level, other.Value);
            }
            string cacheKey = level + ":" + key;
            lock (_terrainCache)
                if (_terrainCache.TryGetValue(cacheKey, out var hit)) return hit;
            var t = SwbfTerrain.Parse(ChunkAt(level, at));
            lock (_terrainCache)
            {
                if (_terrainCache.Count > 8) _terrainCache.Clear();
                _terrainCache[cacheKey] = t;
            }
            return t;
        }

        /// <summary>A terrain as a model of its level: archive + <see cref="TerrainPrefix"/> + terrain name.</summary>
        public ModelMesh? BuildTerrainMesh(string archive, string modelName)
        {
            if (!modelName.StartsWith(TerrainPrefix, StringComparison.OrdinalIgnoreCase)) return null;
            string key = archive.Replace('\\', '/') + ":" + modelName;
            lock (_meshes)
                if (_meshes.TryGetValue(key, out var hit)) return hit;
            var t = Terrain(modelName[TerrainPrefix.Length..], archive);
            if (t == null) return null;
            var mesh = t.Build(archive.Replace('\\', '/'), modelName);
            lock (_meshes)
            {
                if (_meshes.Count > 64) _meshes.Clear();
                _meshes[key] = mesh;
            }
            return mesh;
        }

        /// <summary>A texture by name: the copy in <paramref name="archive"/> (the level asking for it) first, then the
        /// shared levels (side and common), then any.</summary>
        public TextureRef? FindTextureRef(string name, string? archive = null)
        {
            if (!_texByName.TryGetValue(name, out var list) || list.Count == 0) return null;
            if (!string.IsNullOrEmpty(archive))
            {
                string a = archive.Replace('\\', '/');
                if (list.FirstOrDefault(t => t.Archive.Equals(a, StringComparison.OrdinalIgnoreCase)) is { } here) return here;
            }
            return list.FirstOrDefault(t => t.Archive.StartsWith("SIDE/", StringComparison.OrdinalIgnoreCase) || t.Archive.StartsWith("COMMON/", StringComparison.OrdinalIgnoreCase))
                ?? list[0];
        }

        public SwbfTexture? FindTexture(string name, string? archive = null)
        {
            var r = FindTextureRef(name, archive);
            if (r == null) return null;
            string key = r.Archive + ":" + r.Name;
            lock (_decoded)
                if (_decoded.TryGetValue(key, out var hit)) return hit;
            var t = SwbfTexture.Decode(ChunkAt(r.Level, r.Offset));
            lock (_decoded)
            {
                if (_decoded.Count > 256) _decoded.Clear();
                _decoded[key] = t;
            }
            return t;
        }
    }
}
