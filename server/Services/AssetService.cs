using CodeDesigner.Ps2.FileFormats.Assets;
using System.Collections.Concurrent;
using System.Text.Json;
using CodeDesigner.Ps2.FileFormats.Imaging;
using CodeDesigner.Ps2.FileFormats.Zipper.Anim;
using CodeDesigner.Ps2.FileFormats.Zipper.Geo;
using CodeDesigner.Ps2.FileFormats.Zipper.Model;
using CodeDesigner.Ps2.FileFormats.Zipper.MPS;
using CodeDesigner.Ps2.FileFormats.Zipper.Package;
using CodeDesigner.Ps2.FileFormats.Zipper.RDR;
using CodeDesigner.Ps2.FileFormats.Zipper.Texture;
using CodeDesigner.Ps2.FileFormats.Zipper.ZED;

namespace MapBuilder.Server.Services
{
    /// <summary>
    /// Asset sessions: the editing surface for SOCOM II packages, shared by every page (map, models, animations).
    /// ASSET_OPEN indexes a .ZDB (or an unpacked folder) into a session; queries and edits act on the session; every
    /// edit bumps its revision so a page that polls ASSET_STATUS shows edits another page made. Saving (pack export,
    /// ZDB copy) is a session operation too.
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
            reg.Add(c => CaRoute(c, "ASSET_STATUS") ?? WithSession(c.AssetSession, "ASSET_STATUS", s => Status(s)), "ASSET_STATUS");
            reg.Add(c => CaRoute(c, "ASSET_LIST_MODELS") ?? WithSession(c.AssetSession, "ASSET_LIST_MODELS", s => ListModels(s, c.AssetJson)), "ASSET_LIST_MODELS");
            reg.Add(c => CaRoute(c, "ASSET_DESCRIBE_MODEL") ?? WithSession(c.AssetSession, "ASSET_DESCRIBE_MODEL", s => DescribeModel(s, c.AssetArchive, c.AssetName)), "ASSET_DESCRIBE_MODEL");
            reg.Add(c => CaRoute(c, "ASSET_TEXTURE") ?? WithSession(c.AssetSession, "ASSET_TEXTURE", s => Texture(s, c.AssetName, c.AssetArchive)), "ASSET_TEXTURE");
            reg.Add(c => WithSession(c.AssetSession, "ASSET_LIST_TEXTURES", s => ListTextures(s)), "ASSET_LIST_TEXTURES");
            reg.Add(c => WithSession(c.AssetSession, "ASSET_LIST_PALETTES", s => ListPalettes(s)), "ASSET_LIST_PALETTES");
            reg.Add(c => CaRoute(c, "ASSET_MODEL_MESH") ?? WithSession(c.AssetSession, "ASSET_MODEL_MESH", s => Mesh(s, c.AssetArchive, c.AssetName, c.AssetJson)), "ASSET_MODEL_MESH");
            reg.Add(c => CaRoute(c, "ASSET_MAP_SCENE") ?? WithSession(c.AssetSession, "ASSET_MAP_SCENE", s => MapScene(s, c.AssetJson)), "ASSET_MAP_SCENE");
            reg.Add(c => CaRoute(c, "ASSET_MAP_POINTS") ?? WithSession(c.AssetSession, "ASSET_MAP_POINTS", s => MapPointsOf(s)), "ASSET_MAP_POINTS");
            reg.Add(c => CaRoute(c, "ASSET_LIST_ANIMATIONS") ?? WithSession(c.AssetSession, "ASSET_LIST_ANIMATIONS", s => ListAnimations(s, c.AssetJson)), "ASSET_LIST_ANIMATIONS");
            reg.Add(c => CaRoute(c, "ASSET_ANIMATION") ?? WithSession(c.AssetSession, "ASSET_ANIMATION", s => Animation(s, c.AssetName, c.AssetArchive)), "ASSET_ANIMATION");
            reg.Add(c => CaRoute(c, "ASSET_WEAPONS") ?? WithSession(c.AssetSession, "ASSET_WEAPONS", s => Weapons(s)), "ASSET_WEAPONS");
            reg.Add(c => CaRoute(c, "ASSET_WEAPON_FX") ?? WithSession(c.AssetSession, "ASSET_WEAPON_FX", s => WeaponFx(s, c.AssetName)), "ASSET_WEAPON_FX");
            reg.Add(c => CaRoute(c, "ASSET_EFFECTS") ?? WithSession(c.AssetSession, "ASSET_EFFECTS", s => Effects(s, c.AssetJson)), "ASSET_EFFECTS");
            reg.Add(c => CaRoute(c, "ASSET_EFFECT") ?? WithSession(c.AssetSession, "ASSET_EFFECT", s => Effect(s, c.AssetName)), "ASSET_EFFECT");
            reg.Add(c => CaRoute(c, "ASSET_ENTRY_BYTES") ?? WithSession(c.AssetSession, "ASSET_ENTRY_BYTES", s => EntryBytes(s, c.AssetArchive)), "ASSET_ENTRY_BYTES");
            reg.Add(c => CaRoute(c, "ASSET_UNDO") ?? WithSession(c.AssetSession, "ASSET_UNDO", s => UndoRedo(s, undo: true)), "ASSET_UNDO");
            reg.Add(c => CaRoute(c, "ASSET_REDO") ?? WithSession(c.AssetSession, "ASSET_REDO", s => UndoRedo(s, undo: false)), "ASSET_REDO");
            reg.Add(c => CaRoute(c, "ASSET_EDIT") ?? WithSession(c.AssetSession, "ASSET_EDIT", s => Edit(s, c.AssetJson)), "ASSET_EDIT");
            reg.Add(c => CaRoute(c, "ASSET_SAVE") ?? WithSession(c.AssetSession, "ASSET_SAVE", s => Save(s, c.AssetJson)), "ASSET_SAVE");
            reg.Add(c => CaRoute(c, "ASSET_EXPORT") ?? WithSession(c.AssetSession, "ASSET_EXPORT", s => ExportToCa(s, c.AssetArchive, c.AssetName, c.AssetJson)), "ASSET_EXPORT");
        }

        // ------------------------------------------------------------------ edit operations

        /// <summary>
        /// One edit, as JSON <c>{"op": ..., ...}</c>. Placements take either <c>matrix</c> (16 floats, column-major)
        /// or <c>position</c> [x,y,z] with optional <c>yaw</c> (degrees about Y) and <c>scale</c>; omitted parts keep
        /// the current values. Ops: moveClutter, addClutter, deleteClutter, moveInstance, duplicateInstance,
        /// deleteInstance, replaceTexture, retexture, tintVertexColors, importModel / importTexture (from another package),
        /// addTexture / addPalette (from an image).
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
                case "setVertices":
                {
                    string archive = Str(a, "archive") ?? throw new ArgumentException("\"archive\" is required");
                    string model = Str(a, "model") ?? throw new ArgumentException("\"model\" is required");
                    static List<ZipperModelMesh.VertexEdit> Edits(JsonElement holder)
                    {
                        if (!holder.TryGetProperty("vertices", out var vs) || vs.ValueKind != JsonValueKind.Array)
                            throw new ArgumentException("\"vertices\" [{i, position?, uv?, color?, normal?}] is required");
                        return vs.EnumerateArray().Select(v => new ZipperModelMesh.VertexEdit(
                            Int(v, "i"), Floats(v, "position"), Floats(v, "uv"),
                            Floats(v, "color")?.Select(x => (byte)Math.Clamp((int)MathF.Round(x), 0, 255)).ToArray(), Floats(v, "normal"))).ToList();
                    }
                    var byChunk = new Dictionary<int, IReadOnlyList<ZipperModelMesh.VertexEdit>>();
                    if (a.TryGetProperty("chunks", out var cl) && cl.ValueKind == JsonValueKind.Array)
                        foreach (var c in cl.EnumerateArray()) byChunk[Int(c, "geometryOffset")] = Edits(c);
                    else
                        byChunk[Int(a, "geometryOffset")] = Edits(a);
                    int count = byChunk.Values.Sum(l => l.Count);
                    string label = Str(a, "label") ?? $"edit {count} vertex(es) of {model}";
                    return ApplyEdit(s, label, p => ZipperModelMesh.SetVertices(p, archive, model, byChunk));
                }
                case "transformVertices":
                {
                    string archive = Str(a, "archive") ?? throw new ArgumentException("\"archive\" is required");
                    string model = Str(a, "model") ?? throw new ArgumentException("\"model\" is required");
                    List<int>? chunks = a.TryGetProperty("chunks", out var cs) && cs.ValueKind == JsonValueKind.Array
                        ? cs.EnumerateArray().Select(x => x.GetInt32()).ToList() : null;
                    Dictionary<int, int[]>? verts = null;
                    if (a.TryGetProperty("vertices", out var vv) && vv.ValueKind == JsonValueKind.Object)
                        verts = vv.EnumerateObject().ToDictionary(p => int.Parse(p.Name), p => p.Value.EnumerateArray().Select(x => x.GetInt32()).ToArray());
                    float[] m = Floats(a, "matrix") ?? VertexTransform(s, archive, model, a, chunks, verts);
                    return ApplyEdit(s, Str(a, "label") ?? $"transform {model}", p => ZipperModelMesh.TransformVertices(p, archive, model, m, chunks, verts));
                }
                case "deleteFaces":
                {
                    string archive = Str(a, "archive") ?? throw new ArgumentException("\"archive\" is required");
                    string model = Str(a, "model") ?? throw new ArgumentException("\"model\" is required");
                    var faces = FaceMap(a);
                    int n = faces.Values.Sum(f => f.Length);
                    return ApplyTopology(s, Str(a, "label") ?? $"delete {n} face(s) of {model}", p => MeshTopology.DeleteFaces(p, archive, model, faces));
                }
                case "duplicateFaces":
                {
                    string archive = Str(a, "archive") ?? throw new ArgumentException("\"archive\" is required");
                    string model = Str(a, "model") ?? throw new ArgumentException("\"model\" is required");
                    var faces = FaceMap(a);
                    float[] m = Floats(a, "matrix") ?? VertexTransform(s, archive, model, a, null, null);
                    return ApplyTopology(s, Str(a, "label") ?? $"duplicate {faces.Values.Sum(f => f.Length)} face(s) of {model}", p => MeshTopology.DuplicateFaces(p, archive, model, faces, m));
                }
                case "addMesh":
                {
                    string archive = Str(a, "archive") ?? throw new ArgumentException("\"archive\" is required");
                    string model = Str(a, "model") ?? throw new ArgumentException("\"model\" is required");
                    string visual = Str(a, "visual") ?? throw new ArgumentException("\"visual\" (an N<node>_<visual> key) is required");
                    string texture = Str(a, "texture") ?? throw new ArgumentException("\"texture\" is required");
                    bool replace = a.TryGetProperty("replace", out var rp) && rp.ValueKind == JsonValueKind.True;
                    MeshTopology.InputMesh input;
                    string what;
                    if (Str(a, "obj") is { } obj)
                    {
                        bool yUp = !(a.TryGetProperty("yUp", out var yu) && yu.ValueKind == JsonValueKind.False);
                        float scale = a.TryGetProperty("scale", out var sc) && sc.ValueKind == JsonValueKind.Number ? sc.GetSingle() : 1;
                        input = MeshTopology.ParseObj(obj, yUp, scale, Floats(a, "offset"));
                        what = "OBJ";
                    }
                    else if (a.TryGetProperty("primitive", out var pr) && pr.ValueKind == JsonValueKind.Object)
                    {
                        string kind = Str(pr, "kind") ?? "cube";
                        float size = pr.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number ? sz.GetSingle() : 10;
                        input = MeshTopology.Primitive(kind, size, Floats(pr, "center") ?? new float[3]);
                        what = kind;
                    }
                    else if (a.TryGetProperty("mesh", out var me) && me.ValueKind == JsonValueKind.Object)
                    {
                        input = ArraysMesh(me);
                        what = "mesh";
                    }
                    else throw new ArgumentException("addMesh needs \"obj\" (OBJ text), \"primitive\" {kind, size, center} or \"mesh\" {positions, triangles, normals?, uvs?, colors?}");
                    return ApplyTopology(s, Str(a, "label") ?? $"add {what} to {model} {visual}", p => MeshTopology.AddMesh(p, archive, model, visual, texture, input, replace));
                }
                case "importModel":
                {
                    // {sourceSession | source (a package path), archive, name, targetArchive?, newName?}
                    string archive = Str(a, "archive") ?? throw new ArgumentException("\"archive\" (the source model's *_MDL.ZED) is required");
                    string name = Str(a, "name") ?? throw new ArgumentException("\"name\" (the source model) is required");
                    ZipperPackage source = SourcePackage(Str(a, "sourceSession"), Str(a, "source"));
                    if (ReferenceEquals(source, pkg)) throw new ArgumentException("The source is this package; duplicate the model instead");
                    PackageImport.Result? imported = null;
                    var result = ApplyEdit(s, Str(a, "label") ?? $"import {name} from {Path.GetFileName(source.SourcePath)}",
                        p => (imported = PackageImport.ImportModel(p, source, archive, name, Str(a, "targetArchive"), Str(a, "newName"))).Changed);
                    return new
                    {
                        result,
                        archive = imported!.Archive,
                        name = imported.Name,
                        models = imported.Models,
                        textures = imported.Textures,
                        reusedTextures = imported.ReusedTextures,
                        renamedTextures = imported.RenamedTextures,
                        palettes = imported.Palettes.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
                        warnings = imported.Warnings,
                    };
                }
                case "addTexture":
                {
                    // {name, pngBase64 | imagePath, namespace?, width?, height?, maxSize?, paletteId?}
                    string tif = Str(a, "name") ?? throw new ArgumentException("\"name\" (the new .tif) is required");
                    byte[] rgba = ImageRgba(a, out int w, out int h);
                    PackageTextures.Result? added = null;
                    var result = ApplyEdit(s, Str(a, "label") ?? $"add texture {tif}", p => (added = PackageTextures.AddTexture(p, tif, Str(a, "namespace"), rgba, w, h,
                        OptInt(a, "width"), OptInt(a, "height"), OptInt(a, "maxSize") ?? 256, OptInt(a, "paletteId"))).Changed);
                    return new { result, archive = added!.Archive, texture = added.Texture, width = added.Width, height = added.Height, paletteId = added.PaletteId, newPalette = added.NewPalette, palette32 = added.Palette32, warnings = added.Warnings };
                }
                case "addPalette":
                {
                    // {pngBase64 | imagePath, namespace?, is16?}: a 256-colour palette from the image's colours
                    byte[] rgba = ImageRgba(a, out _, out _);
                    bool? is16 = a.TryGetProperty("is16", out var b16) && b16.ValueKind is JsonValueKind.True or JsonValueKind.False ? b16.GetBoolean() : null;
                    int id = 0;
                    string archive = string.Empty;
                    var result = ApplyEdit(s, Str(a, "label") ?? "add palette", p => { var r = PackageTextures.AddPalette(p, Str(a, "namespace"), rgba, is16); id = r.PaletteId; archive = r.Archive; return r.Changed; });
                    return new { result, archive, paletteId = id, palette = "texpal_" + id };
                }
                case "importTexture":
                {
                    // {sourceSession | source, name, sourceNamespace?, namespace?, newName?}
                    string tif = Str(a, "name") ?? throw new ArgumentException("\"name\" (the source .tif) is required");
                    ZipperPackage source = SourcePackage(Str(a, "sourceSession"), Str(a, "source"));
                    if (ReferenceEquals(source, pkg)) throw new ArgumentException("The source is this package");
                    PackageImport.Result? imported = null;
                    var result = ApplyEdit(s, Str(a, "label") ?? $"import texture {tif} from {Path.GetFileName(source.SourcePath)}",
                        p => (imported = PackageImport.ImportTexture(p, source, tif, Str(a, "sourceNamespace"), Str(a, "namespace"), Str(a, "newName"))).Changed);
                    return new
                    {
                        result,
                        archive = imported!.Archive,
                        texture = imported.Name,
                        textures = imported.Textures,
                        reusedTextures = imported.ReusedTextures,
                        palettes = imported.Palettes.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
                        warnings = imported.Warnings,
                    };
                }
                case "deleteInstance":
                {
                    var (ns, node) = SceneNode(s, a);
                    return ApplyEdit(s, Str(a, "label") ?? $"delete {node.Name}", p => PackageEdits.DeleteInstance(p, ns, node.Index));
                }
                case "replaceTexture":
                {
                    string tif = Str(a, "texture") ?? throw new ArgumentException("\"texture\" is required");
                    byte[] png = Str(a, "pngBase64") is { } b64 ? Convert.FromBase64String(b64)
                        : Str(a, "imagePath") is { } path ? File.ReadAllBytes(path)
                        : throw new ArgumentException("\"pngBase64\" or \"imagePath\" is required");
                    byte[] rgba = PngDecoder.DecodeRgba(png, out int w, out int h);
                    string? ns = Str(a, "namespace");
                    return ApplyEdit(s, $"replace {tif}", p => PackageEdits.ReplaceTexture(p, tif, ns, rgba, w, h));
                }
                case "addAnimation":
                {
                    string name = Str(a, "name") ?? throw new ArgumentException("\"name\" (the new clip) is required");
                    string from = Str(a, "from") ?? "seal_stand";
                    var clip = MotionClip.Parse(FindClip(s, from, Str(a, "fromSource")).Data);
                    ApplyClipShape(clip, a);
                    var result = ApplyEdit(s, Str(a, "label") ?? $"add animation {name} (from {from})", p => PackageAnimations.Add(p, name, clip, Str(a, "after")));
                    return new { result, name };
                }
                case "deleteAnimation":
                {
                    string name = Str(a, "name") ?? throw new ArgumentException("\"name\" is required");
                    return ApplyEdit(s, Str(a, "label") ?? $"delete animation {name}", p => PackageAnimations.Delete(p, name));
                }
                case "renameAnimation":
                {
                    string name = Str(a, "name") ?? throw new ArgumentException("\"name\" is required");
                    string newName = Str(a, "newName") ?? throw new ArgumentException("\"newName\" is required");
                    return ApplyEdit(s, Str(a, "label") ?? $"rename animation {name} to {newName}", p => PackageAnimations.Rename(p, name, newName));
                }
                case "setAnimation":
                {
                    string name = Str(a, "name") ?? throw new ArgumentException("\"name\" is required");
                    var clip = MotionClip.Parse(PackageClip(s, name).Data);
                    ApplyClipShape(clip, a);
                    if (a.TryGetProperty("bones", out var bones) && bones.ValueKind == JsonValueKind.Array)
                        foreach (var b in bones.EnumerateArray()) SetTrack(clip, b);
                    return ApplyEdit(s, Str(a, "label") ?? $"edit animation {name}", p => PackageAnimations.Replace(p, name, clip));
                }
                case "setAnimationKey":
                {
                    string name = Str(a, "name") ?? throw new ArgumentException("\"name\" is required");
                    string boneName = Str(a, "bone") ?? throw new ArgumentException("\"bone\" is required");
                    var clip = MotionClip.Parse(PackageClip(s, name).Data);
                    var track = clip.Find(boneName) ?? throw new ArgumentException($"{name} has no bone '{boneName}'");
                    bool all = a.TryGetProperty("allFrames", out var af) && af.ValueKind == JsonValueKind.True;
                    int frame = all ? 0 : Int(a, "frame");
                    if (frame < 0 || frame >= clip.Samples) throw new ArgumentOutOfRangeException(nameof(frame), $"frame must be 0..{clip.Samples - 1}");
                    if (Floats(a, "rotation") is { } q)
                    {
                        if (q.Length != 4) throw new ArgumentException("\"rotation\" needs [x, y, z, w]");
                        var key = MotionClip.QuaternionKey(q[0], q[1], q[2], q[3]);
                        if (all) track.Rotations = new List<short[]> { key };
                        else { Expand(track.Rotations, clip.Samples); track.Rotations[frame] = key; }
                    }
                    if (Floats(a, "position") is { } v)
                    {
                        if (v.Length != 3) throw new ArgumentException("\"position\" needs [x, y, z] (units)");
                        var key = MotionClip.PositionKey(v[0], v[1], v[2]);
                        if (all) track.Positions = new List<short[]> { new[] { key[0], key[1], key[2], MotionClip.StaticPositionW } };
                        else { Expand(track.Positions, clip.Samples); track.Positions[frame] = key; }
                    }
                    return ApplyEdit(s, Str(a, "label") ?? $"key {boneName} of {name}{(all ? "" : $" at frame {frame}")}", p => PackageAnimations.Replace(p, name, clip));
                }
                case "retexture":
                {
                    string archive = Str(a, "archive") ?? throw new ArgumentException("\"archive\" is required");
                    string model = Str(a, "model") ?? throw new ArgumentException("\"model\" is required");
                    string from = Str(a, "from") ?? throw new ArgumentException("\"from\" is required");
                    string to = Str(a, "to") ?? throw new ArgumentException("\"to\" is required");
                    string? visual = Str(a, "visual");
                    return ApplyEdit(s, $"retexture {model} {from} -> {to}", p => PackageEdits.RetextureModel(p, archive, model, from, to, visual));
                }
                case "tintVertexColors":
                {
                    string archive = Str(a, "archive") ?? throw new ArgumentException("\"archive\" is required");
                    string model = Str(a, "model") ?? throw new ArgumentException("\"model\" is required");
                    float[] mul = Floats(a, "multiply") ?? new[] { 1f, 1f, 1f };
                    float[]? add = Floats(a, "add");
                    float? alpha = a.TryGetProperty("alpha", out var al) && al.ValueKind == JsonValueKind.Number ? al.GetSingle() : null;
                    return ApplyEdit(s, $"tint {model}", p => PackageEdits.TintVertexColors(p, archive, model, mul, add, Str(a, "visual"), Str(a, "texture"), alpha));
                }
                default:
                    throw new ArgumentException($"Unknown op '{op}'");
            }
        }

        /// <summary>
        /// translate [x,y,z], rotate [x,y,z] degrees (applied X, then Y, then Z), scale (number or [x,y,z]) about
        /// pivot [x,y,z] (default: the centre of the affected vertices).
        /// </summary>
        private static float[] VertexTransform(AssetSession s, string archive, string model, JsonElement a, List<int>? chunks, Dictionary<int, int[]>? verts)
        {
            float[] t = Floats(a, "translate") ?? new float[3];
            float[] r = Floats(a, "rotate") ?? new float[3];
            float[] sc = a.TryGetProperty("scale", out var sv) && sv.ValueKind == JsonValueKind.Number
                ? new[] { sv.GetSingle(), sv.GetSingle(), sv.GetSingle() }
                : Floats(a, "scale") ?? new[] { 1f, 1f, 1f };
            if (t.Length != 3 || r.Length != 3 || sc.Length != 3) throw new ArgumentException("translate / rotate / scale need 3 values");
            float[]? pivot = Floats(a, "pivot");
            if (pivot == null)
            {
                var mesh = ZipperModelMesh.Build(s.Package, s.Package.FindModel(archive, model) ?? throw new KeyNotFoundException($"Model {archive}:{model} not found"));
                var min = new[] { float.MaxValue, float.MaxValue, float.MaxValue };
                var max = new[] { float.MinValue, float.MinValue, float.MinValue };
                foreach (var c in mesh.Chunks)
                {
                    bool chosen = (chunks is not { Count: > 0 } && verts == null) || (chunks?.Contains(c.GeometryOffset) ?? false) || (verts?.ContainsKey(c.GeometryOffset) ?? false);
                    if (!chosen) continue;
                    IEnumerable<int> which = verts != null && verts.TryGetValue(c.GeometryOffset, out var l) ? l : Enumerable.Range(0, c.VertexCount);
                    foreach (int i in which)
                        for (int k = 0; k < 3; k++) { min[k] = MathF.Min(min[k], c.Positions[i * 3 + k]); max[k] = MathF.Max(max[k], c.Positions[i * 3 + k]); }
                }
                pivot = min[0] <= max[0] ? new[] { (min[0] + max[0]) / 2, (min[1] + max[1]) / 2, (min[2] + max[2]) / 2 } : new float[3];
            }
            static float[] Rot(int axis, float deg)
            {
                float rad = deg * MathF.PI / 180, c = MathF.Cos(rad), sn = MathF.Sin(rad);
                return axis switch
                {
                    0 => new[] { 1, 0, 0, 0, 0, c, sn, 0, 0, -sn, c, 0, 0, 0, 0, 1f },
                    1 => new[] { c, 0, -sn, 0, 0, 1, 0, 0, sn, 0, c, 0, 0, 0, 0, 1f },
                    _ => new[] { c, sn, 0, 0, -sn, c, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1f },
                };
            }
            float[] toOrigin = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, -pivot[0], -pivot[1], -pivot[2], 1 };
            float[] scaleM = { sc[0], 0, 0, 0, 0, sc[1], 0, 0, 0, 0, sc[2], 0, 0, 0, 0, 1 };
            float[] back = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, pivot[0] + t[0], pivot[1] + t[1], pivot[2] + t[2], 1 };
            float[] rot = Matrix4.Multiply(Rot(2, r[2]), Matrix4.Multiply(Rot(1, r[1]), Rot(0, r[0])));
            return Matrix4.Multiply(back, Matrix4.Multiply(rot, Matrix4.Multiply(scaleM, toOrigin)));
        }

        private static object ApplyTopology(AssetSession s, string description, Func<ZipperPackage, MeshTopology.Result> edit)
        {
            List<int> newChunks = new();
            var result = ApplyEdit(s, description, p => { var r = edit(p); newChunks = r.NewChunks; return r.Changed; });
            return new { result, newChunks };
        }

        private static Dictionary<int, int[]> FaceMap(JsonElement a)
        {
            if (!a.TryGetProperty("faces", out var f) || f.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("\"faces\" {geometryOffset: [triangle indices]} is required");
            return f.EnumerateObject().ToDictionary(p => int.Parse(p.Name), p => p.Value.EnumerateArray().Select(x => x.GetInt32()).ToArray());
        }

        private static MeshTopology.InputMesh ArraysMesh(JsonElement me)
        {
            var m = new MeshTopology.InputMesh();
            List<float[]> Rows(string name) => me.TryGetProperty(name, out var r) && r.ValueKind == JsonValueKind.Array
                ? r.EnumerateArray().Select(x => x.EnumerateArray().Select(y => y.GetSingle()).ToArray()).ToList() : new List<float[]>();
            m.Positions.AddRange(Rows("positions"));
            m.Normals.AddRange(Rows("normals"));
            m.Uvs.AddRange(Rows("uvs"));
            m.Colors.AddRange(Rows("colors").Select(c => c.Select(x => (byte)Math.Clamp((int)MathF.Round(x), 0, 255)).ToArray()));
            foreach (var t in Rows("triangles")) m.Triangles.Add(((int)t[0], (int)t[1], (int)t[2]));
            if (m.Positions.Count == 0 || m.Triangles.Count == 0) throw new ArgumentException("mesh needs positions and triangles");
            if (m.Normals.Count != m.Positions.Count) m.Normals.Clear();
            return m;
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

        // ------------------------------------------------------------------ animations

        private static readonly Dictionary<string, (DateTime Stamp, IReadOnlyList<ZedNode> Nodes)> SharedClips = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>RUN/MOTION_P.ZAR next to the session's package (cached per file), or null.</summary>
        private static IReadOnlyList<ZedNode>? Shared(AssetSession s)
        {
            string? path = PackageAnimations.SharedPath(s.Package);
            if (path == null) return null;
            lock (SharedClips)
            {
                DateTime stamp = File.GetLastWriteTimeUtc(path);
                if (!SharedClips.TryGetValue(path, out var hit) || hit.Stamp != stamp)
                    SharedClips[path] = hit = (stamp, ZedContainer.FromBytes(File.ReadAllBytes(path)).Nodes.ToList());
                return hit.Nodes;
            }
        }

        private static PackageAnimations.ClipRef FindClip(AssetSession s, string name, string? source) =>
            PackageAnimations.Find(s.Package, Shared(s), name, source)
            ?? throw new KeyNotFoundException($"No animation '{name}'{(source != null ? " in " + source : "")} (see ASSET_LIST_ANIMATIONS)");

        private static PackageAnimations.ClipRef PackageClip(AssetSession s, string name) =>
            PackageAnimations.Find(s.Package, null, name)
            ?? throw new KeyNotFoundException($"The package has no animation '{name}'; shared MOTION_P clips are read-only (addAnimation with from copies one in)");

        /// <summary>Optional "frames" (frame intervals: resample) and "duration" (seconds) on a clip.</summary>
        private static void ApplyClipShape(MotionClip clip, JsonElement a)
        {
            if (a.TryGetProperty("frames", out var f) && f.ValueKind == JsonValueKind.Number) clip.Resample(f.GetInt32());
            if (a.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number)
            {
                float v = d.GetSingle();
                if (!(v > 0) || v > 600) throw new ArgumentException("\"duration\" must be 0-600 s");
                clip.Duration = v;
            }
        }

        /// <summary>A static track (one key) as one key per sample, so a single frame can change.</summary>
        private static void Expand(List<short[]> keys, int samples)
        {
            if (keys.Count == samples) return;
            short[] first = keys[0];
            keys.Clear();
            for (int i = 0; i < samples; i++) keys.Add((short[])first.Clone());
        }

        /// <summary>{name, positions?: [[x,y,z]] units, rotations?: [[x,y,z,w]]}: 1 key (static) or one per sample.</summary>
        private static void SetTrack(MotionClip clip, JsonElement b)
        {
            string bone = Str(b, "name") ?? throw new ArgumentException("bones[].name is required");
            var track = clip.Find(bone) ?? throw new ArgumentException($"The clip has no bone '{bone}'");
            List<float[]>? Keys(string prop) => b.TryGetProperty(prop, out var arr) && arr.ValueKind == JsonValueKind.Array
                ? arr.EnumerateArray().Select(k => k.EnumerateArray().Select(x => x.GetSingle()).ToArray()).ToList() : null;
            if (Keys("positions") is { } ps)
            {
                if (ps.Count != 1 && ps.Count != clip.Samples) throw new ArgumentException($"{bone}: {ps.Count} positions, expected 1 or {clip.Samples}");
                if (ps.Any(p => p.Length != 3)) throw new ArgumentException($"{bone}: a position needs 3 values");
                track.Positions = ps.Select(p => MotionClip.PositionKey(p[0], p[1], p[2])).ToList();
                if (track.Positions.Count == 1) track.Positions[0] = new[] { track.Positions[0][0], track.Positions[0][1], track.Positions[0][2], MotionClip.StaticPositionW };
            }
            if (Keys("rotations") is { } qs)
            {
                if (qs.Count != 1 && qs.Count != clip.Samples) throw new ArgumentException($"{bone}: {qs.Count} rotations, expected 1 or {clip.Samples}");
                if (qs.Any(q => q.Length != 4)) throw new ArgumentException($"{bone}: a rotation needs 4 values");
                track.Rotations = qs.Select(q => MotionClip.QuaternionKey(q[0], q[1], q[2], q[3])).ToList();
            }
        }

        private sealed record AnimationFilter(string? Query, string? Source);

        private static object ListAnimations(AssetSession s, string? json)
        {
            var f = Parse<AnimationFilter>(json) ?? new AnimationFilter(null, null);
            var clips = PackageAnimations.List(s.Package, Shared(s)).Where(c =>
                (f.Query == null || c.Name.Contains(f.Query, StringComparison.OrdinalIgnoreCase)) &&
                (f.Source == null || c.Source.Equals(f.Source, StringComparison.OrdinalIgnoreCase)));
            return new
            {
                packageArchive = s.Package.Entry(PackageAnimations.PackageLeaf)?.Leaf,
                sharedPath = PackageAnimations.SharedPath(s.Package),
                clips = clips.Select(c =>
                {
                    var clip = MotionClip.Parse(c.Data);
                    return new { c.Name, c.Source, c.Editable, clip.Duration, frames = clip.Intervals, samples = clip.Samples, fps = clip.FramesPerSecond, bones = clip.Tracks.Count };
                }).ToList(),
            };
        }

        /// <summary>A clip's tracks as units / unit quaternions per sample (one value = static).</summary>
        private static object Animation(AssetSession s, string? name, string? source)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("AssetName (the clip) is required");
            var c = FindClip(s, name, source);
            var clip = MotionClip.Parse(c.Data);
            return new
            {
                c.Name, c.Source, c.Editable, clip.Duration, frames = clip.Intervals, samples = clip.Samples, fps = clip.FramesPerSecond,
                revision = s.Revision,
                bones = clip.Tracks.Select(t => new
                {
                    t.Name, t.RawName, kind = $"0x{t.Kind:X2}",
                    positions = t.Positions.Select(MotionClip.PositionUnits).ToList(),
                    rotations = t.Rotations.Select(MotionClip.Quaternion).ToList(),
                }).ToList(),
            };
        }

        // ------------------------------------------------------------------ weapons + effects

        /// <summary>RUN/ZWEAPON.ZAR › zweapon.rdr next to the package, or null.</summary>
        private static WeaponDefs? WeaponDefsOf(AssetSession s)
        {
            string dir = s.Package.IsZdb ? Path.GetDirectoryName(s.Package.SourcePath) ?? "." : s.Package.SourcePath;
            string? path = new[] { Path.Combine(dir, "ZWEAPON.ZAR"), Path.Combine(Path.GetDirectoryName(dir) ?? dir, "ZWEAPON.ZAR") }.FirstOrDefault(File.Exists);
            if (path == null) return null;
            var rdr = ZedContainer.FromBytes(File.ReadAllBytes(path)).Nodes.FirstOrDefault(n => n.Name.EndsWith(".rdr", StringComparison.OrdinalIgnoreCase));
            return rdr == null ? null : WeaponDefs.Parse(rdr.Data);
        }

        private static ZanimFile? EffectsOf(AssetSession s) =>
            s.Package.Entry("CZANIM.ZAR") is { } e ? ZanimFile.Parse(e.Data) : null;

        /// <summary>A track with its instructions decoded (official opcode names, nesting, named arguments, raw words).</summary>
        private static object TrackObject(ZanimFile.Track t) => new
        {
            t.Target,
            headerFlags = $"0x{t.HeaderFlags:X}",
            instructions = t.Instructions.Select(i => new
            {
                op = $"{i.Op:X2}", i.Name, i.Depth, i.Condition, i.Flags,
                fields = i.Fields,
                words = string.Join(' ', i.Words.Select(x => x.ToString("x8"))),
            }).ToList(),
        };

        private sealed record EffectFilter(string? Query);

        /// <summary>Every ZANIM effect in the package's CZANIM.ZAR: name, what it spawns, its tracks and models.</summary>
        private static object Effects(AssetSession s, string? json)
        {
            var fx = EffectsOf(s) ?? throw new InvalidOperationException("The package has no CZANIM.ZAR");
            string? q = Parse<EffectFilter>(json)?.Query;
            return fx.Animations.Where(a => q == null || a.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).Select(a => new
            {
                a.Name,
                a.Set,
                tracks = a.Tracks.Select(t => t.Target).ToList(),
                spawns = a.Spawns.Distinct().ToList(),
                models = a.Names.Skip(2).Where(n => s.Package.ResolveModel(n) != null).ToList(),
                ops = a.Tracks.SelectMany(t => t.Instructions).Select(i => i.Name).Distinct().ToList(),
            }).ToList();
        }

        /// <summary>One ZANIM effect fully decoded: names, node refs (objects), tracks and instructions.</summary>
        private static object Effect(AssetSession s, string? name)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("AssetName (the effect) is required");
            var fx = EffectsOf(s) ?? throw new InvalidOperationException("The package has no CZANIM.ZAR");
            var a = fx.Find(name) ?? throw new KeyNotFoundException($"No effect '{name}' (see ASSET_EFFECTS)");
            return new
            {
                a.Name,
                a.Set,
                paramsHex = Convert.ToHexString(a.Params),
                names = a.Names,
                nodeRefs = a.NodeRefs.Select(r => new { r.Name, r.Flags, r.Parent }).ToList(),
                models = a.Names.Skip(2).Select(n => s.Package.ResolveModel(n)).Where(m => m != null).Select(m => new { archive = m!.Archive, name = m.Name }).ToList(),
                spawns = a.Spawns.Distinct().ToList(),
                tracks = a.Tracks.Select(TrackObject).ToList(),
            };
        }

        /// <summary>Weapons whose model is in the package, with what they play when fired.</summary>
        private static object Weapons(AssetSession s)
        {
            var defs = WeaponDefsOf(s) ?? throw new InvalidOperationException("No RUN/ZWEAPON.ZAR next to the package");
            return defs.Weapons
                .Select(w => (w, model: s.Package.Models().FirstOrDefault(m => m.Kind == "weapon" && m.Name.Equals(w.ModelName, StringComparison.OrdinalIgnoreCase))))
                .Where(x => x.model != null)
                .GroupBy(x => x.w.ModelName, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .Select(x => new { x.w.InternalName, model = x.model!.Name, archive = x.model.Archive, x.w.FireAnimName, x.w.FireWait, x.w.TracerTextureName })
                .ToList();
        }

        /// <summary>
        /// What firing a weapon plays: its definition and the ZANIM effect tree from FireAnimName (sub-effects it
        /// spawns, the package models each uses, e.g. muzzle_flash_hider / bullet_shell_9m in EFFE_MDL, and its
        /// tracks with their opcodes).
        /// </summary>
        private static object WeaponFx(AssetSession s, string? weapon)
        {
            if (string.IsNullOrEmpty(weapon)) throw new ArgumentException("AssetName (weapon model or internal name) is required");
            var defs = WeaponDefsOf(s) ?? throw new InvalidOperationException("No RUN/ZWEAPON.ZAR next to the package");
            var w = defs.ForModel(weapon) ?? defs.Weapons.FirstOrDefault(x => x.InternalName.Equals(weapon, StringComparison.OrdinalIgnoreCase))
                ?? throw new KeyNotFoundException($"No weapon '{weapon}' in zweapon.rdr");
            var fx = EffectsOf(s);
            object? Tree(string name, int depth)
            {
                var a = fx?.Find(name);
                if (a == null) return new { name, found = false };
                var models = a.Names.Skip(2).Select(n => s.Package.ResolveModel(n)).Where(m => m != null)
                    .Select(m => new { archive = m!.Archive, name = m.Name }).ToList();
                return new
                {
                    name, found = true, a.Set,
                    names = a.Names,
                    nodeRefs = a.NodeRefs.Select(r => new { r.Name, r.Flags, r.Parent }).ToList(),
                    models,
                    textures = a.Names.Where(n => n.EndsWith(".tif", StringComparison.OrdinalIgnoreCase)).ToList(),
                    tracks = a.Tracks.Select(TrackObject).ToList(),
                    spawns = depth < 4 ? a.Spawns.Distinct().Select(n => Tree(n, depth + 1)).ToList() : null,
                };
            }
            return new
            {
                weapon = new { w.InternalName, w.ModelName, w.FireAnimName, w.FireWait, w.TracerTextureName, fields = w.Fields },
                effect = string.IsNullOrEmpty(w.FireAnimName) ? null : Tree(w.FireAnimName, 0),
            };
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
        /// <summary>
        /// A SOCOM II model (character, weapon, vehicle, prop) as a SOCOM 3 / Combined Assault package: a ZDB v6
        /// &lt;outputDir&gt;/&lt;MODEL&gt;.ZDB with an ASST library, the chosen clips as a BONE bank and a NAMS name list.
        /// </summary>
        private static object ExportToCa(AssetSession s, string? archive, string? name, string? json)
        {
            if (string.IsNullOrEmpty(archive) || string.IsNullOrEmpty(name)) throw new ArgumentException("AssetArchive and AssetName (the model) are required");
            var model = s.Package.FindModel(archive, name) ?? throw new KeyNotFoundException($"Model {archive}:{name} not found");
            var a = Parse<CaExportArgs>(json) ?? new CaExportArgs(null, null, null);
            string modelName = a.ModelName ?? (model.Name.StartsWith("MESH_", StringComparison.OrdinalIgnoreCase) ? model.Name[5..] : model.Name);
            var r = CodeDesigner.Ps2.FileFormats.Zipper.Conversion.Socom2ToCa.Export(s.Package, model,
                new CodeDesigner.Ps2.FileFormats.Zipper.Conversion.Socom2ToCa.Options(modelName, a.OutputDir ?? DefaultExportDir(), a.Clips));
            var cdb = CodeDesigner.Ps2.FileFormats.Zipper.Ca.CaPackage.Open(r.Path);
            var opened = cdb.Models().FirstOrDefault(m => m.Name.Equals(r.Model, StringComparison.OrdinalIgnoreCase)) ?? cdb.Models().FirstOrDefault();
            return new
            {
                target = "SOCOM 3 / CA", path = r.Path, pack = Path.GetFileName(r.Path), model = r.Model, r.Kind, library = r.Resource, r.Triangles,
                r.Textures, r.Palettes, clips = r.Clips, r.Warnings,
                open = opened == null ? null : new { archive = $"{opened.File}|{opened.Resource}", name = opened.Name },
            };
        }

        private sealed record CaExportArgs(string? ModelName, string? OutputDir, string[]? Clips);

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

        /// <summary>An op's image as RGBA: "pngBase64" (the page converts JPG / BMP / WebP to PNG) or "imagePath" (a PNG).</summary>
        private static byte[] ImageRgba(JsonElement a, out int width, out int height)
        {
            byte[] png = Str(a, "pngBase64") is { } b64 ? Convert.FromBase64String(b64)
                : Str(a, "imagePath") is { } path ? File.ReadAllBytes(path)
                : throw new ArgumentException("\"pngBase64\" or \"imagePath\" (a PNG) is required");
            return PngDecoder.DecodeRgba(png, out width, out height);
        }

        private static int? OptInt(JsonElement a, string name) =>
            a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

        /// <summary>The package an import reads from: an open session (its unsaved edits included), else the file.</summary>
        private static ZipperPackage SourcePackage(string? sessionId, string? path)
        {
            if (sessionId != null) return (Find(sessionId) ?? throw new KeyNotFoundException($"No asset session '{sessionId}'")).Package;
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("\"sourceSession\" or \"source\" (a package path) is required");
            string full = Path.GetFullPath(path);
            return Sessions.Values.FirstOrDefault(x => x.Package.SourcePath.Equals(full, StringComparison.OrdinalIgnoreCase))?.Package
                ?? ZipperPackage.Open(full);
        }

        // ------------------------------------------------------------------ session lifecycle

        private static string HandleOpen(string? inputPath)
        {
            if (string.IsNullOrWhiteSpace(inputPath)) return Responses.Error("InputPath (a .ZDB or a package folder) is required");
            try
            {
                string full = Path.GetFullPath(inputPath);
                // SOCOM 3 / Combined Assault archives open as read-only CA sessions.
                if (TryOpenCa(full) is string ca) return ca;
                // GTA: San Andreas (the disc image or GTA3.IMG) opens as a read-only GTA session.
                if (Assets.GtaAssetService.TryOpen(full) is string gta) return gta;
                // One session per package path: the editor and an agent opening the same map share it.
                AssetSession? existing = Sessions.Values.FirstOrDefault(s => s.Package.SourcePath.Equals(full, StringComparison.OrdinalIgnoreCase));
                // A session whose file changed on disk since (a save over it) is replaced, unless it holds unsaved edits.
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

        private static string NewId(string path) => Assets.AssetJson.SessionId(path);

        private static string HandleClose(string? id)
        {
            if (id != null && (CloseCa(id) || Assets.GtaAssetService.Close(id))) return Ok("ASSET_CLOSE", new { session = id, discardedEdits = 0 });
            if (id == null || !Sessions.TryRemove(id, out var s)) return Responses.Error($"No asset session '{id}'");
            return Ok("ASSET_CLOSE", new { session = id, discardedEdits = s.Dirty.Count });
        }

        private static string HandleSessions() =>
            Ok("ASSET_SESSIONS", Sessions.Values.Select(StatusObject).Concat(CaSessions.Values.Select(CaStatus)).Concat(Assets.GtaAssetService.Statuses()).ToList());

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

        private static string Ok(string command, object data) => Responses.Ok(command, data);

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

        /// <summary>Every texture of the package: name, namespace, size, format and the palette it binds.</summary>
        private static object ListTextures(AssetSession s)
        {
            var list = new List<object>();
            foreach (var e in s.Package.Entries.Where(e => e.Kind == "txr" && e.Namespace != null).OrderBy(e => e.Leaf, StringComparer.OrdinalIgnoreCase))
            {
                IReadOnlyList<ZedNode> nodes;
                try { nodes = s.Package.Container(e.Leaf).Nodes; } catch (InvalidDataException) { continue; }
                for (int i = 0; i + 1 < nodes.Count; i++)
                {
                    if (!nodes[i].Name.EndsWith(".tif", StringComparison.OrdinalIgnoreCase) || nodes[i + 1].Name != "texdat") continue;
                    try
                    {
                        var h = ZedTextureFile.ParseHeader(nodes[i + 1].Data);
                        list.Add(new { name = nodes[i].Name, @namespace = e.Namespace, archive = e.Leaf, width = (int)h.Width, height = (int)h.Height, bpp = h.Bpp, paletteId = h.IsIndexed ? h.ResolvePaletteId() : (int?)null, size = nodes[i + 1].Data.Length });
                    }
                    catch (InvalidDataException) { }
                }
            }
            return list;
        }

        /// <summary>Every palette of the package: id, namespace, depth and how many textures bind it.</summary>
        private static object ListPalettes(AssetSession s)
        {
            var users = new Dictionary<int, int>();
            foreach (var e in s.Package.Entries.Where(e => e.Kind == "txr"))
            {
                IReadOnlyList<ZedNode> nodes;
                try { nodes = s.Package.Container(e.Leaf).Nodes; } catch (InvalidDataException) { continue; }
                foreach (var n in nodes.Where(n => n.Name == "texdat" && n.Data.Length >= ZedTextureFile.HeaderSize))
                    try { var h = ZedTextureFile.ParseHeader(n.Data); if (h.IsIndexed) { int id = h.ResolvePaletteId(); users[id] = users.GetValueOrDefault(id) + 1; } } catch (InvalidDataException) { }
            }
            var list = new List<object>();
            foreach (var e in s.Package.Entries.Where(e => e.Kind == "pal" && e.Namespace != null).OrderBy(e => e.Leaf, StringComparer.OrdinalIgnoreCase))
            {
                IReadOnlyList<ZedNode> nodes;
                try { nodes = s.Package.Container(e.Leaf).Nodes; } catch (InvalidDataException) { continue; }
                for (int i = 0; i < nodes.Count; i++)
                {
                    if (!nodes[i].Name.StartsWith("texpal_", StringComparison.Ordinal) || !int.TryParse(nodes[i].Name[7..], out int id)) continue;
                    var buf = nodes.Skip(i + 1).Take(3).FirstOrDefault(n => n.Name == "buf");
                    list.Add(new { id, name = nodes[i].Name, @namespace = e.Namespace, archive = e.Leaf, is16 = buf != null && buf.Data.Length < 1024, textures = users.GetValueOrDefault(id) });
                }
            }
            return list;
        }

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
