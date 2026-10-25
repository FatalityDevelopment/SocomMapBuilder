using CodeDesigner.Ps2.FileFormats.Assets;
using System.Collections.Concurrent;
using System.Text.Json;
using CodeDesigner.Ps2.FileFormats.Imaging;
using CodeDesigner.Ps2.FileFormats.Zipper.Geo;
using CodeDesigner.Ps2.FileFormats.Zipper.Model;
using CodeDesigner.Ps2.FileFormats.Zipper.MPS;
using CodeDesigner.Ps2.FileFormats.Zipper.Package;
using CodeDesigner.Ps2.FileFormats.Zipper.Texture;

namespace MapBuilder.Server.Services
{
    /// <summary>
    /// Asset sessions: the editing surface for SOCOM II map packages. ASSET_OPEN indexes a .ZDB (or an unpacked
    /// folder) into a session; queries and edits act on the session; every edit bumps its revision so a client that
    /// polls ASSET_STATUS sees edits another client made. Saving (ZDB copy, memory-card pack) is a session operation too.
    /// </summary>
    internal sealed partial class ZipperAssetService : ICommandService
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        private static readonly ConcurrentDictionary<string, AssetSession> Sessions = new(StringComparer.OrdinalIgnoreCase);

        internal sealed class AssetSession
        {
            public required string Id { get; init; }
            public required ZipperPackage Package { get; init; }
            /// <summary>Last write time of the package on disk when the session read it.</summary>
            public DateTime SourceStamp { get; init; }
            public DateTime OpenedAt { get; } = DateTime.UtcNow;
            public int Revision { get; set; }
            /// <summary>Root leaves changed since opening (what a save writes).</summary>
            public HashSet<string> Dirty { get; } = new(StringComparer.OrdinalIgnoreCase);
            /// <summary>Undo log: root entry bytes before each edit, with a description.</summary>
            public Stack<(string Description, Dictionary<string, byte[]> Before)> Undo { get; } = new();
            public Stack<(string Description, Dictionary<string, byte[]> Before)> Redo { get; } = new();
            public object Gate { get; } = new();
        }

        public void Register(CommandRegistry reg)
        {
            reg.Add(c => HandleOpen(c.InputPath), "ASSET_OPEN");
            reg.Add(c => HandleClose(c.AssetSession), "ASSET_CLOSE");
            reg.Add(_ => HandleSessions(), "ASSET_SESSIONS");
            reg.Add(c => WithSession(c.AssetSession, "ASSET_STATUS", s => Status(s)), "ASSET_STATUS");
            reg.Add(c => WithSession(c.AssetSession, "ASSET_LIST_MODELS", s => ListModels(s, c.AssetJson)), "ASSET_LIST_MODELS");
            reg.Add(c => WithSession(c.AssetSession, "ASSET_DESCRIBE_MODEL", s => DescribeModel(s, c.AssetArchive, c.AssetName)), "ASSET_DESCRIBE_MODEL");
            reg.Add(c => WithSession(c.AssetSession, "ASSET_TEXTURE", s => Texture(s, c.AssetName, c.AssetArchive)), "ASSET_TEXTURE");
            reg.Add(c => WithSession(c.AssetSession, "ASSET_MODEL_MESH", s => Mesh(s, c.AssetArchive, c.AssetName, c.AssetJson)), "ASSET_MODEL_MESH");
            reg.Add(c => WithSession(c.AssetSession, "ASSET_MAP_SCENE", s => MapScene(s, c.AssetJson)), "ASSET_MAP_SCENE");
            reg.Add(c => WithSession(c.AssetSession, "ASSET_MAP_POINTS", s => MapPointsOf(s)), "ASSET_MAP_POINTS");
            reg.Add(c => WithSession(c.AssetSession, "ASSET_ENTRY_BYTES", s => EntryBytes(s, c.AssetArchive)), "ASSET_ENTRY_BYTES");
            reg.Add(c => WithSession(c.AssetSession, "ASSET_UNDO", s => UndoRedo(s, undo: true)), "ASSET_UNDO");
            reg.Add(c => WithSession(c.AssetSession, "ASSET_REDO", s => UndoRedo(s, undo: false)), "ASSET_REDO");
            reg.Add(c => WithSession(c.AssetSession, "ASSET_EDIT", s => Edit(s, c.AssetJson)), "ASSET_EDIT");
            reg.Add(c => WithSession(c.AssetSession, "ASSET_SAVE", s => Save(s, c.AssetJson)), "ASSET_SAVE");
        }

        // ------------------------------------------------------------------ edit operations

        /// <summary>
        /// One edit, as JSON <c>{"op": ..., ...}</c>. Placements take either <c>matrix</c> (16 floats, column-major)
        /// or <c>position</c> [x,y,z] with optional <c>yaw</c> (degrees about Y) and <c>scale</c>; omitted parts keep
        /// the current values. Ops: moveClutter, addClutter, deleteClutter, moveInstance, duplicateInstance,
        /// deleteInstance, movePoint, batch.
        /// </summary>
        private static object Edit(AssetSession s, string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) throw new ArgumentException("AssetJson with an \"op\" is required");
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement a = doc.RootElement;
            string op = Str(a, "op") ?? throw new ArgumentException("\"op\" is required");
            ZipperPackage pkg = s.Package;

            switch (op)
            {
                case "batch":
                    return Batch(s, a);
                case "moveClutter":
                {
                    int index = Int(a, "clutterIndex");
                    var current = ClutterFile.Parse(pkg.Container(pkg.Entry(PackageEdits.ClutterLeaf)?.Leaf ?? throw new InvalidOperationException("No CLUTTER.ZAR")));
                    if (index < 0 || index >= current.Count) throw new ArgumentOutOfRangeException(nameof(index), $"clutterIndex {index} out of range");
                    float[] m = Placement(a, current[index].Matrix);
                    return ApplyEdit(s, Str(a, "label") ?? $"move {current[index].ModelName} clutter #{index}", p => PackageEdits.MoveClutter(p, index, m));
                }
                case "addClutter":
                {
                    string model = Str(a, "model") ?? throw new ArgumentException("\"model\" is required");
                    float[] m = Placement(a, Matrix4.Identity());
                    int newIndex = -1;
                    var result = ApplyEdit(s, Str(a, "label") ?? $"add {model} clutter", p => PackageEdits.AddClutter(p, model, m, out newIndex));
                    return new { result, newClutterIndex = newIndex };
                }
                case "movePoint":
                {
                    string name = Str(a, "name") ?? throw new ArgumentException("\"name\" (a point from ASSET_MAP_POINTS) is required");
                    float[] pos = Floats(a, "position") ?? throw new ArgumentException("\"position\" [x, y, z] is required");
                    string leaf = s.Package.Entries.FirstOrDefault(e => e.Leaf.EndsWith(".MPS", StringComparison.OrdinalIgnoreCase))?.Leaf
                        ?? throw new InvalidOperationException("The package has no AIMAPS.MPS");
                    return ApplyEdit(s, Str(a, "label") ?? $"move point {name}", p => new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
                    {
                        [leaf] = MapPoints.Move(p.Entry(leaf)!.Data, name, pos[0], pos[2]),
                    });
                }
                case "deleteClutter":
                {
                    int index = Int(a, "clutterIndex");
                    return ApplyEdit(s, Str(a, "label") ?? $"delete clutter #{index}", p => PackageEdits.DeleteClutter(p, index));
                }
                case "moveInstance":
                case "duplicateInstance":
                {
                    var (ns, node) = SceneNode(s, a);
                    float[] world = Placement(a, node.WorldTransform);
                    float[] local = Matrix4.Multiply(Matrix4.InverseAffine(node.ParentWorldTransform), world);
                    if (op == "moveInstance")
                        return ApplyEdit(s, Str(a, "label") ?? $"move {node.Name}", p => PackageEdits.SetInstanceTransform(p, ns, node.Index, local));
                    string? name = Str(a, "name");
                    int newIndex = -1;
                    var result = ApplyEdit(s, Str(a, "label") ?? $"duplicate {node.Name}", p => PackageEdits.DuplicateInstance(p, ns, node.Index, local, name, out newIndex));
                    return new { result, newGeoIndex = newIndex };
                }
                case "deleteInstance":
                {
                    var (ns, node) = SceneNode(s, a);
                    return ApplyEdit(s, Str(a, "label") ?? $"delete {node.Name}", p => PackageEdits.DeleteInstance(p, ns, node.Index));
                }
                default:
                    throw new ArgumentException($"Unknown op '{op}'");
            }
        }

        /// <summary>
        /// {"op":"batch","label":..,"ops":[...]}: runs the ops in order as one undo step. A failing op undoes the
        /// ones before it. Answers {revision, description, results} with each op's own answer.
        /// </summary>
        private static object Batch(AssetSession s, JsonElement a)
        {
            if (!a.TryGetProperty("ops", out var ops) || ops.ValueKind != JsonValueKind.Array) throw new ArgumentException("\"ops\" [..] is required");
            int depth = s.Undo.Count;
            var results = new List<object>();
            try
            {
                foreach (var op in ops.EnumerateArray()) results.Add(Edit(s, op.GetRawText()));
            }
            catch
            {
                while (s.Undo.Count > depth) UndoRedo(s, undo: true);
                s.Redo.Clear();
                throw;
            }
            // Fold the ops' undo entries into one (the earliest snapshot of each archive wins).
            var merged = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            var labels = new List<string>();
            while (s.Undo.Count > depth)
            {
                var (description, before) = s.Undo.Pop();
                labels.Insert(0, description);
                foreach (var (leaf, bytes) in before) merged[leaf] = bytes;
            }
            string label = Str(a, "label") ?? string.Join("; ", labels);
            if (merged.Count > 0) s.Undo.Push((label, merged));
            return new { revision = s.Revision, description = label, results };
        }

        private static (string Namespace, GeoNode Node) SceneNode(AssetSession s, JsonElement a)
        {
            int geoIndex = Int(a, "geoIndex");
            string ns = Str(a, "scene") ?? s.Package.MapScene()?.Namespace ?? throw new InvalidOperationException("No map scene; pass \"scene\"");
            GeoScene scene = s.Package.Scene(ns) ?? throw new KeyNotFoundException($"No {ns}_GEO.ZED");
            GeoNode? node = null;
            void Find(GeoNode n) { if (n.Index == geoIndex) node = n; foreach (var c in n.Children) if (node == null) Find(c); }
            Find(scene.Root);
            if (node == null || !node.IsSceneNode) throw new ArgumentException($"geoIndex {geoIndex} is not a scene node of {ns}_GEO.ZED (see ASSET_MAP_SCENE)");
            return (ns, node);
        }

        /// <summary>A transform from <c>matrix</c>, or <c>position</c>/<c>yaw</c>/<c>scale</c> over <paramref name="current"/>.</summary>
        private static float[] Placement(JsonElement a, float[] current)
        {
            if (Floats(a, "matrix") is { } m)
            {
                if (m.Length != 16) throw new ArgumentException("\"matrix\" needs 16 floats");
                return m;
            }
            float[] pos = Floats(a, "position") ?? new[] { current[12], current[13], current[14] };
            if (pos.Length != 3) throw new ArgumentException("\"position\" needs [x, y, z]");
            bool rotates = a.TryGetProperty("yaw", out _) || a.TryGetProperty("scale", out _);
            if (!rotates)
            {
                var moved = (float[])current.Clone();
                moved[12] = pos[0]; moved[13] = pos[1]; moved[14] = pos[2];
                return moved;
            }
            float yaw = a.TryGetProperty("yaw", out var y) ? y.GetSingle() : Matrix4.YawOf(current);
            float scale = a.TryGetProperty("scale", out var sc) ? sc.GetSingle() : Matrix4.ScaleOf(current);
            return Matrix4.Compose(pos[0], pos[1], pos[2], yaw, scale);
        }

        private static string? Str(JsonElement a, string name) =>
            a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static int Int(JsonElement a, string name) =>
            a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : throw new ArgumentException($"\"{name}\" (a number) is required");

        private static float[]? Floats(JsonElement a, string name) =>
            a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Select(x => x.GetSingle()).ToArray() : null;

        // ------------------------------------------------------------------ save

        private sealed record SaveArgs(string? Mode, string? OutputPath, string? PackName, string? FolderCard, List<string>? Leaves);

        /// <summary>
        /// <c>{"mode":"zdb","outputPath":...}</c> writes the package (over the source keeps a .bak) ;
        /// <c>{"mode":"pack","packName":"mxxxx","outputPath":dir,"folderCard":dir}</c> writes a memory-card CDB of the
        /// changed archives. <c>leaves</c> defaults to every archive changed in the session.
        /// </summary>
        private static object Save(AssetSession s, string? json)
        {
            var args = Parse<SaveArgs>(json) ?? new SaveArgs("zdb", null, null, null, null);
            var leaves = args.Leaves is { Count: > 0 } l ? l : s.Dirty.ToList();
            if (leaves.Count == 0) throw new InvalidOperationException("No changes to save");
            List<string> written;
            switch ((args.Mode ?? "zdb").ToLowerInvariant())
            {
                case "zdb":
                    written = PackageSave.Write(s.Package, leaves, args.OutputPath);
                    break;
                case "pack":
                    string name = args.PackName ?? throw new ArgumentException("\"packName\" is required for a pack");
                    string dir = args.OutputPath ?? Path.Combine(Path.GetDirectoryName(s.Package.SourcePath)!, "packs", name);
                    written = PackageSave.WritePack(s.Package, leaves, name, dir, args.FolderCard);
                    break;
                default:
                    throw new ArgumentException($"Unknown save mode '{args.Mode}' (zdb or pack)");
            }
            return new { written, leaves, revision = s.Revision };
        }

        internal static AssetSession? Find(string? id) =>
            id != null && Sessions.TryGetValue(id, out var s) ? s : null;

        // ------------------------------------------------------------------ session lifecycle

        private static string HandleOpen(string? inputPath)
        {
            if (string.IsNullOrWhiteSpace(inputPath)) return Responses.Error("InputPath (a .ZDB or a package folder) is required");
            try
            {
                string full = Path.GetFullPath(inputPath);
                // One session per package path, so clients opening the same map share it. A session whose file
                // changed on disk since (a save over it) is replaced, unless it holds unsaved edits.
                AssetSession? existing = Sessions.Values.FirstOrDefault(s => s.Package.SourcePath.Equals(full, StringComparison.OrdinalIgnoreCase));
                if (existing != null && existing.Dirty.Count == 0 && existing.SourceStamp != SourceStampOf(full))
                {
                    Sessions.TryRemove(existing.Id, out _);
                    existing = null;
                }
                AssetSession session = existing ?? new AssetSession { Id = NewId(full), Package = ZipperPackage.Open(full), SourceStamp = SourceStampOf(full) };
                Sessions[session.Id] = session;
                return Ok("ASSET_OPEN", new { reused = existing != null, status = StatusObject(session) });
            }
            catch (Exception ex) { return Responses.Error($"ASSET_OPEN error: {ex.Message}"); }
        }

        private static DateTime SourceStampOf(string path) =>
            File.Exists(path) ? File.GetLastWriteTimeUtc(path) : Directory.GetLastWriteTimeUtc(path);

        private static string NewId(string path) =>
            $"{Path.GetFileNameWithoutExtension(path).ToLowerInvariant()}-{Guid.NewGuid().ToString("N")[..6]}";

        private static string HandleClose(string? id)
        {
            if (id == null || !Sessions.TryRemove(id, out var s)) return Responses.Error($"No asset session '{id}'");
            return Ok("ASSET_CLOSE", new { session = id, discardedEdits = s.Dirty.Count });
        }

        private static string HandleSessions() =>
            Ok("ASSET_SESSIONS", Sessions.Values.Select(StatusObject).ToList());

        private static string WithSession(string? id, string command, Func<AssetSession, object> body)
        {
            AssetSession? s = Find(id);
            if (s == null) return Responses.Error($"No asset session '{id}'. Call ASSET_OPEN first (ASSET_SESSIONS lists open ones).");
            try
            {
                object result;
                lock (s.Gate) { result = body(s); }
                return Ok(command, result);
            }
            catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException or InvalidOperationException or InvalidDataException or NotSupportedException)
            {
                return Responses.Error($"{command}: {ex.Message}");
            }
        }

        private static string Ok(string command, object data) =>
            Responses.Json(new CommandResponse { Success = true, Command = command, Data = JsonSerializer.Serialize(data, JsonOptions) });

        private static object Status(AssetSession s) => StatusObject(s);

        private static object StatusObject(AssetSession s)
        {
            var models = s.Package.Models();
            return new
            {
                session = s.Id,
                path = s.Package.SourcePath,
                isZdb = s.Package.IsZdb,
                mapNamespace = s.Package.MapNamespace,
                revision = s.Revision,
                dirty = s.Dirty.OrderBy(x => x).ToList(),
                canUndo = s.Undo.Count > 0 ? s.Undo.Peek().Description : null,
                canRedo = s.Redo.Count > 0 ? s.Redo.Peek().Description : null,
                entries = s.Package.Entries.Count,
                models = models.GroupBy(m => m.Kind).ToDictionary(g => g.Key, g => g.Count()),
            };
        }

        // ------------------------------------------------------------------ edits (shared plumbing)

        /// <summary>
        /// Runs an edit: snapshots the root entries it touches, applies it, and records undo. The edit returns
        /// the root leaves it changed with their new bytes.
        /// </summary>
        internal static object ApplyEdit(AssetSession s, string description, Func<ZipperPackage, IReadOnlyDictionary<string, byte[]>> edit)
        {
            var changed = edit(s.Package);
            if (changed.Count == 0) return new { revision = s.Revision, changed = Array.Empty<string>() };
            var before = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var (leaf, bytes) in changed)
            {
                before[leaf] = s.Package.Entry(leaf)!.Data;
                s.Package.ReplaceEntry(leaf, bytes);
                s.Dirty.Add(leaf);
            }
            s.Undo.Push((description, before));
            s.Redo.Clear();
            s.Revision++;
            return new { revision = s.Revision, description, changed = changed.Keys.ToList() };
        }

        private static object UndoRedo(AssetSession s, bool undo)
        {
            var from = undo ? s.Undo : s.Redo;
            var to = undo ? s.Redo : s.Undo;
            if (from.Count == 0) throw new InvalidOperationException(undo ? "Nothing to undo" : "Nothing to redo");
            var (description, restore) = from.Pop();
            var current = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var (leaf, bytes) in restore)
            {
                current[leaf] = s.Package.Entry(leaf)!.Data;
                s.Package.ReplaceEntry(leaf, bytes);
                s.Dirty.Add(leaf);
            }
            to.Push((description, current));
            s.Revision++;
            return new { revision = s.Revision, description, undone = undo ? description : null, redone = undo ? null : description, changed = restore.Keys.ToList() };
        }

        // ------------------------------------------------------------------ queries

        private sealed record ModelFilter(string? Kind, string? Query, string? Archive);

        private static object ListModels(AssetSession s, string? json)
        {
            var f = Parse<ModelFilter>(json) ?? new ModelFilter(null, null, null);
            var models = s.Package.Models().Where(m =>
                (f.Kind == null || m.Kind.Equals(f.Kind, StringComparison.OrdinalIgnoreCase)) &&
                (f.Archive == null || m.Archive.Equals(f.Archive, StringComparison.OrdinalIgnoreCase)) &&
                (f.Query == null || m.Name.Contains(f.Query, StringComparison.OrdinalIgnoreCase)));
            return models.Select(m => new { m.Archive, m.Name, m.Kind, m.Namespace, m.Size, m.VisualCount }).ToList();
        }

        private static object DescribeModel(AssetSession s, string? archive, string? name)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("AssetName (the model) is required");
            ZipperPackage.ModelRef model = (archive != null ? s.Package.FindModel(archive, name) : null)
                ?? s.Package.ResolveModel(name)
                ?? throw new KeyNotFoundException($"Model '{name}' not found{(archive != null ? " in " + archive : "")}");
            ZedModelFile.Model decoded = s.Package.DecodeModel(model);
            var definition = s.Package.FindDefinition(model);
            GeoNode? geo = definition?.Node;
            string? geoArchive = definition is { } d ? s.Package.Archive(d.Namespace, "geo")?.Leaf : null;
            string? texNs = s.Package.TextureNamespace(model.Namespace);

            if (decoded.Chunks.Count == 0 && ZipperModelMesh.TryBuild(s.Package, model) is { Skinned: true } skinned)
                return DescribeSkinned(s, model, skinned, geoArchive, texNs);

            var chunks = decoded.Chunks.Select((c, i) => new
            {
                index = i,
                offset = c.Offset,
                visual = c.VisualKey,
                texture = c.TextureName,
                binding = c.TextureBinding,
                vertices = c.VertexCount,
                triangles = c.TriangleCount,
                scale = c.Scale,
                origin = new[] { c.Origin.X, c.Origin.Y, c.Origin.Z },
                vertexColors = c.HasVertexColors,
            }).ToList();

            var textures = decoded.Chunks.Select(c => c.TextureName).Where(t => t != null).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(t => TextureInfo(s.Package, t!, texNs)).ToList();

            return new
            {
                model.Archive,
                model.Name,
                model.Kind,
                model.Namespace,
                size = model.Size,
                nodeIndex = model.NodeIndex,
                textureNamespace = texNs,
                geoArchive,
                fromChains = decoded.FromChains,
                visuals = chunks.Select(c => c.visual).Where(v => v != null).Distinct().ToList(),
                chunks,
                textures,
                geo = geo == null ? null : DescribeGeo(geo),
            };
        }

        /// <summary>A skinned character: chunks are its draw groups, the GEO is its skeleton.</summary>
        private static object DescribeSkinned(AssetSession s, ZipperPackage.ModelRef model, ModelMesh mesh, string? geoArchive, string? texNs)
        {
            var chunks = mesh.Chunks.Select(c => new
            {
                index = c.Index,
                offset = c.GeometryOffset,
                visual = c.Visual,
                texture = c.Texture,
                binding = "skin",
                vertices = c.VertexCount,
                triangles = c.Triangles.Length / 3,
                scale = c.Scale,
                origin = new[] { 0f, 0f, 0f },
                vertexColors = false,
            }).ToList();
            var textures = mesh.Chunks.Select(c => c.Texture).Where(t => !string.IsNullOrEmpty(t)).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(t => TextureInfo(s.Package, t!, texNs)).ToList();
            var skeleton = s.Package.FindSkeleton(model.Namespace, CharacterMesh.SkeletonName(model.Name));
            return new
            {
                model.Archive,
                model.Name,
                model.Kind,
                model.Namespace,
                size = model.Size,
                nodeIndex = model.NodeIndex,
                textureNamespace = texNs,
                geoArchive = geoArchive ?? (skeleton != null ? s.Package.Archive(model.Namespace, "geo")?.Leaf : null),
                fromChains = true,
                skinned = true,
                bones = mesh.Skeleton!.Bones.Count,
                visuals = chunks.Select(c => c.visual).Distinct().ToList(),
                chunks,
                textures,
                geo = skeleton == null ? null : DescribeGeo(skeleton),
            };
        }

        private static object DescribeGeo(GeoNode node)
        {
            var b = node.Bounds;
            return new
            {
                name = node.Name,
                index = node.Index,
                transform = node.Transform,
                boundsMin = b?.Min,
                boundsMax = b?.Max,
                vparamsFlags = node.VisualFlags is uint f ? $"0x{f:X8}" : null,
                positionUnit = node.VisualFlags is uint g ? ZedModelFile.PositionUnitForFlags(g).ToString() : null,
                visuals = node.Visuals.Count,
                reference = node.ModelReference.Length > 0 ? node.ModelReference : null,
                children = node.SceneChildren.Where(c => c.IsSceneNode).Select(c => c.Name).ToList(),
            };
        }

        private sealed record MeshArgs(bool? Arrays);

        /// <summary>
        /// The editable mesh in model space. Arrays are base64 little-endian: positions / uvs / normals float32,
        /// colors RGBA bytes (0x80 = 1.0), triangles uint32 vertex indices. {"arrays": false} returns only counts
        /// and bounds (cheap for agents).
        /// </summary>
        private static object Mesh(AssetSession s, string? archive, string? name, string? json)
        {
            if (string.IsNullOrEmpty(archive) || string.IsNullOrEmpty(name)) throw new ArgumentException("AssetArchive and AssetName (the model) are required");
            var model = s.Package.FindModel(archive, name) ?? throw new KeyNotFoundException($"Model {archive}:{name} not found");
            bool arrays = Parse<MeshArgs>(json)?.Arrays ?? true;
            var mesh = ZipperModelMesh.Build(s.Package, model);
            static string F(float[] a) { var bytes = new byte[a.Length * 4]; Buffer.BlockCopy(a, 0, bytes, 0, bytes.Length); return Convert.ToBase64String(bytes); }
            static string I(int[] a) { var bytes = new byte[a.Length * 4]; Buffer.BlockCopy(a, 0, bytes, 0, bytes.Length); return Convert.ToBase64String(bytes); }
            return new
            {
                archive = mesh.Archive,
                name = mesh.Name,
                revision = s.Revision,
                textureNamespace = s.Package.TextureNamespace(model.Namespace),
                boundsMin = mesh.BoundsMin,
                boundsMax = mesh.BoundsMax,
                chunks = mesh.Chunks.Select(c => new
                {
                    c.Index, c.GeometryOffset, c.Visual, node = c.Node, c.Texture, c.MaterialBlend, unit = c.Unit.ToString(), c.Matrix, c.Scale, c.Origin,
                    c.VertexCount, triangleCount = c.Triangles.Length / 3, hasColors = c.Colors != null,
                    positions = arrays ? F(c.Positions) : null,
                    uvs = arrays ? F(c.Uvs) : null,
                    normals = arrays ? F(c.Normals) : null,
                    colors = arrays && c.Colors != null ? Convert.ToBase64String(c.Colors) : null,
                    triangles = arrays ? I(c.Triangles) : null,
                    skinBones = arrays && c.SkinBones != null ? I(c.SkinBones) : null,
                    skinWeights = arrays && c.SkinWeights != null ? F(c.SkinWeights) : null,
                    skinPositions = arrays && c.SkinPositions != null ? F(c.SkinPositions) : null,
                    skinNormals = arrays && c.SkinNormals != null ? F(c.SkinNormals) : null,
                }).ToList(),
                skinInfluences = mesh.Skinned ? ModelMesh.SkinInfluences : (int?)null,
                locators = mesh.Skinned ? null : Locators(s.Package.DefinitionNode(model)),
                skeleton = mesh.Skeleton?.Bones.Select(b => new { b.Name, b.MatrixId, b.Parent, b.GeoIndex, b.Local, b.World }).ToList(),
            };
        }

        /// <summary>
        /// Scene nodes of a model that carry no geometry (firepoint, firepoint_shell, aimpoint ...), with their
        /// transform relative to the model (column-major): where the game spawns effects and aims.
        /// </summary>
        private static List<object>? Locators(GeoNode? definition)
        {
            if (definition == null) return null;
            var list = new List<object>();
            void Walk(GeoNode n, float[] parent)
            {
                foreach (GeoNode c in n.SceneChildren)
                {
                    if (!c.IsSceneNode) continue;
                    float[] m = Matrix4.Multiply(parent, c.Transform);
                    if (!c.HasVisuals && c.SceneChildren.Count == 0 && c.Child("di") == null) list.Add(new { name = c.Name, matrix = m });
                    Walk(c, m);
                }
            }
            Walk(definition, Matrix4.Identity());
            return list;
        }

        private static object TextureInfo(ZipperPackage pkg, string tif, string? preferredNs)
        {
            var decoded = pkg.DecodeTexture(tif, preferredNs);
            if (decoded == null) return new { name = tif, found = false };
            var h = decoded.Value.Texture.Header;
            return new
            {
                name = tif,
                found = true,
                @namespace = decoded.Value.Namespace,
                width = h.Width,
                height = h.Height,
                psm = $"0x{h.Psm:X2}",
                bpp = h.Bpp,
                palette = h.IsIndexed ? h.ResolvePaletteId() : (int?)null,
                paletteResolved = decoded.Value.Texture.PaletteResolved,
                hasTransparency = decoded.Value.Texture.HasTransparency,
                alphaTest = h.Gs?.AlphaTest,
                alphaBlend = h.Gs?.AlphaBlend,
                clamp = h.Gs == null ? null : new[] { h.Gs.ClampS, h.Gs.ClampT },
            };
        }

        private static object Texture(AssetSession s, string? tif, string? archive)
        {
            if (string.IsNullOrEmpty(tif)) throw new ArgumentException("AssetName (the .tif) is required");
            string? ns = archive == null ? null : s.Package.Entry(archive)?.Namespace ?? archive.ToLowerInvariant();
            var decoded = s.Package.DecodeTexture(tif, ns) ?? throw new KeyNotFoundException($"Texture '{tif}' not found");
            var h = decoded.Texture.Header;
            return new
            {
                info = TextureInfo(s.Package, tif, decoded.Namespace),
                png = Convert.ToBase64String(PngEncoder.EncodeRgba(decoded.Texture.Rgba, h.Width, h.Height)),
            };
        }

        private sealed record SceneFilter(string? Kind, string? Query, int? Limit, bool? IncludeUnresolved);

        private static object MapScene(AssetSession s, string? json)
        {
            var f = Parse<SceneFilter>(json) ?? new SceneFilter(null, null, null, null);
            var map = s.Package.MapScene() ?? throw new InvalidOperationException("The package has no map scene (no GEO with a worldmodel)");
            Func<string, bool> resolves = n => s.Package.ResolveModel(n) != null;
            var instances = SceneInstances.Collect(map.Scene.MapRoot, resolves);
            if (s.Package.Entry("CLUTTER.ZAR") is { } clutter)
                instances.AddRange(SceneInstances.FromClutter(ClutterFile.Parse(s.Package.Container(clutter.Leaf)), resolves));

            var counts = instances.GroupBy(i => i.Kind).ToDictionary(g => g.Key, g => g.Count());
            var selected = instances.Where(i =>
                    (f.IncludeUnresolved == true || i.Resolved) &&
                    (f.Kind == null || i.Kind.Equals(f.Kind, StringComparison.OrdinalIgnoreCase)) &&
                    (f.Query == null || i.ModelName.Contains(f.Query, StringComparison.OrdinalIgnoreCase) || i.Path.Contains(f.Query, StringComparison.OrdinalIgnoreCase)))
                .Take(f.Limit ?? 500)
                .Select(i =>
                {
                    var m = s.Package.ResolveModel(i.ModelName);
                    return new
                    {
                        i.Kind,
                        model = i.ModelName,
                        instance = i.InstanceName,
                        i.Path,
                        position = new[] { i.Matrix[12], i.Matrix[13], i.Matrix[14] },
                        scale = MathF.Sqrt(i.Matrix[0] * i.Matrix[0] + i.Matrix[1] * i.Matrix[1] + i.Matrix[2] * i.Matrix[2]),
                        matrix = i.Matrix,
                        geoIndex = i.Node?.Index,
                        clutterIndex = i.ClutterIndex >= 0 ? i.ClutterIndex : (int?)null,
                        source = m == null ? null : $"{m.Archive}:{m.Name}",
                    };
                }).ToList();
            return new { sceneNamespace = map.Namespace, counts, returned = selected.Count, instances = selected };
        }

        /// <summary>AIMAPS.MPS points (starts, waypoints) with world X / Z from the first AI map's grid.</summary>
        private static object MapPointsOf(AssetSession s)
        {
            var mps = s.Package.Entries.FirstOrDefault(e => e.Leaf.EndsWith(".MPS", StringComparison.OrdinalIgnoreCase));
            if (mps == null) return new { points = Array.Empty<object>() };
            var grid = MpsFile.Parse(mps.Data).Maps.FirstOrDefault();
            return new
            {
                grid = grid == null ? null : new { grid.Name, grid.MinX, grid.MinY, grid.MinZ, grid.MaxX, grid.MaxY, grid.MaxZ, grid.ScaleX, grid.ScaleZ, grid.Width, grid.Height },
                points = MapPoints.Parse(mps.Data, grid).Select(p => new { p.Name, p.Team, p.Kind, p.X, p.Z, p.CellX, p.CellZ }).ToList(),
            };
        }

        private static object EntryBytes(AssetSession s, string? leaf)
        {
            if (string.IsNullOrEmpty(leaf)) throw new ArgumentException("AssetArchive (the root entry leaf) is required");
            var e = s.Package.Entry(leaf) ?? throw new KeyNotFoundException($"No root entry {leaf}");
            return new { leaf = e.Leaf, revision = s.Revision, data = Convert.ToBase64String(e.Data) };
        }

        internal static T? Parse<T>(string? json) where T : class =>
            string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<T>(json, JsonOptions);
    }
}
