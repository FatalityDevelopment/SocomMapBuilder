using System.Text.Json;
using CodeDesigner.Ps2.FileFormats.Ftb3;

namespace MapBuilder.Server.Services.Assets
{
    /// <summary>
    /// Fireteam Bravo 3 map edits: moving, turning and scaling a level's placed props (an instance's 4×4 matrix in
    /// <c>_inst.ssg</c>) and moving its game-mode points (the object's matrix in <c>_coll.ssg</c> and its copy in
    /// <c>_full.ssg</c>). Every edit rewrites 64 bytes in place; a step keeps each patch's bytes before and after for undo
    /// and redo. Saving writes the edited files into a folder at their places under <c>levels</c>
    /// (<c>Valley_multi\Valley_multi_inst.ssg</c>), or a copy of the disc image with them in place.
    /// </summary>
    internal static partial class Ftb3AssetService
    {
        internal sealed record Patch(string Level, SsgArchive Archive, int At, byte[] Before, byte[] After);
        internal sealed record Step(string Description, List<Patch> Patches);

        private static object History(Session s)
        {
            object[] Steps(IEnumerable<Step> steps) => steps.Select(st => new { description = st.Description, archives = Archives(st) }).ToArray();
            return new { revision = s.Revision, undo = Steps(s.Undo), redo = Steps(s.Redo), dirty = s.Package.DirtyNames() };
        }

        private static string[] Archives(Step st) => st.Patches.Select(p => Path.GetFileName(p.Archive.SourcePath)).Distinct().ToArray();

        private static object Edit(Session s, string? json)
        {
            using var doc = JsonDocument.Parse(json ?? "{}");
            var a = doc.RootElement;
            if (a.TryGetProperty("revision", out var rev) && rev.ValueKind == JsonValueKind.Number && rev.GetInt32() != s.Revision)
                throw new InvalidOperationException("The map changed. Refresh before applying this edit.");
            if (a.TryGetProperty("mission", out var mis) && mis.ValueKind == JsonValueKind.String && !string.Equals(mis.GetString(), s.Level, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"The selected level changed to {s.Level}. Refresh before editing {mis.GetString()}.");
            var patches = new List<Patch>();
            var descriptions = new List<string>();
            var warnings = new List<string>();
            // Later ops in a batch see earlier ones: each patch is applied as it is made, and undone if one fails.
            try
            {
                if (Str(a, "op") == "batch")
                {
                    if (!a.TryGetProperty("ops", out var ops) || ops.ValueKind != JsonValueKind.Array) throw new ArgumentException("\"ops\" (an array) is required");
                    foreach (var op in ops.EnumerateArray()) descriptions.Add(One(s, op, patches, warnings));
                }
                else descriptions.Add(One(s, a, patches, warnings));
            }
            catch
            {
                Revert(s, patches);
                throw;
            }
            string description = Str(a, "label") ?? (descriptions.Count == 1 ? descriptions[0] : $"{descriptions.Count} edits");
            var changed = patches.Select(p => Path.GetFileName(p.Archive.SourcePath)).Distinct().ToArray();
            if (a.TryGetProperty("dryRun", out var dry) && dry.ValueKind == JsonValueKind.True)
            {
                Revert(s, patches);
                return new { dryRun = true, wouldDo = new { description, changed, warnings } };
            }
            if (patches.Count > 0)
            {
                s.Undo.Push(new Step(description, patches));
                s.Redo.Clear();
                s.Revision++;
            }
            return new { revision = s.Revision, description, changed, warnings = warnings.Distinct().ToArray() };
        }

        private static void Revert(Session s, List<Patch> patches)
        {
            for (int i = patches.Count - 1; i >= 0; i--) s.Package.Patch(patches[i].Level, patches[i].Archive, patches[i].At, patches[i].Before);
        }

        /// <summary>One edit: moveInstance {instance, matrix | position | yaw | scale} or movePoint {name, position}.</summary>
        private static string One(Session s, JsonElement a, List<Patch> patches, List<string> warnings)
        {
            switch (Str(a, "op"))
            {
                case "moveInstance":
                {
                    string id = Str(a, "instance") ?? throw new ArgumentException("\"instance\" (an id from the map scene) is required");
                    var inst = s.Package.Instances(s.Level).FirstOrDefault(i => i.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                        ?? throw new KeyNotFoundException($"No placed prop {id} on {s.Level} (the terrain and buildings built in place cannot move)");
                    var archive = s.Package.InstanceArchive(s.Level) ?? throw new InvalidOperationException("This level has no instance file");
                    Apply(s, archive, inst.At, Placement(a, inst.Matrix), patches);
                    return $"move {id}";
                }
                case "movePoint":
                {
                    string name = Str(a, "name") ?? throw new ArgumentException("\"name\" (a point) is required");
                    if (a.TryGetProperty("newName", out _) || a.TryGetProperty("team", out _) || a.TryGetProperty("kind", out _))
                        throw new NotSupportedException($"{Game} points can be moved; renaming them or changing their team is not supported yet.");
                    var point = s.Package.Points(s.Level).FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                        ?? throw new KeyNotFoundException($"No point {name} on {s.Level}");
                    var pos = Vec(a, "position", 3) ?? throw new ArgumentException("\"position\" [x, y, z] is required");
                    var m = (float[])point.Matrix.Clone();
                    m[12] = pos[0]; m[13] = pos[1]; m[14] = pos[2];
                    foreach (var (archive, at) in point.Places) Apply(s, archive, at, m, patches);
                    if (point.Places.Count < 2) warnings.Add($"{name} has no copy in the level's _full.ssg; only _coll.ssg changes.");
                    return $"move {name}";
                }
                default:
                    throw new NotSupportedException($"{Game} maps support moveInstance and movePoint (and batches of them). '{Str(a, "op")}' is unavailable.");
            }
        }

        private static void Apply(Session s, SsgArchive archive, int at, float[] matrix, List<Patch> patches)
        {
            if (at < 0) throw new InvalidOperationException("That placement has no matrix to edit.");
            var after = Ftb3Package.MatrixBytes(matrix);
            var before = Ftb3Package.ReadAt(archive, at, after.Length);
            if (before.AsSpan().SequenceEqual(after)) return;
            s.Package.Patch(s.Level, archive, at, after);
            patches.Add(new Patch(s.Level, archive, at, before, after));
        }

        /// <summary>The new matrix: "matrix" (16 numbers as the scene gives them), else "position", "yaw" (degrees about Y)
        /// and "scale" applied to the current one. Instances keep their own scale (grass and trees are scaled).</summary>
        private static float[] Placement(JsonElement a, float[] current)
        {
            float[] m;
            if (Vec(a, "matrix", 16) is { } given) m = given;
            else
            {
                m = (float[])current.Clone();
                float scale = MathF.Sqrt(m[0] * m[0] + m[1] * m[1] + m[2] * m[2]);
                if (a.TryGetProperty("scale", out var sc) && sc.ValueKind == JsonValueKind.Number)
                {
                    float k = sc.GetSingle() / (scale > 0 ? scale : 1);
                    for (int i = 0; i < 12; i++) if (i % 4 != 3) m[i] *= k;
                    scale = sc.GetSingle();
                }
                if (a.TryGetProperty("yaw", out var y) && y.ValueKind == JsonValueKind.Number)
                {
                    // Keep each row's length (the instance's scale per axis) and turn it about Y.
                    double r = y.GetDouble() * Math.PI / 180;
                    float c = (float)Math.Cos(r), sn = (float)Math.Sin(r);
                    float[] rows = { c, 0, -sn, 0, 1, 0, sn, 0, c };
                    for (int i = 0; i < 3; i++)
                    {
                        float len = MathF.Sqrt(m[i * 4] * m[i * 4] + m[i * 4 + 1] * m[i * 4 + 1] + m[i * 4 + 2] * m[i * 4 + 2]);
                        for (int k = 0; k < 3; k++) m[i * 4 + k] = rows[i * 3 + k] * len;
                    }
                }
                if (Vec(a, "position", 3) is { } pos) { m[12] = pos[0]; m[13] = pos[1]; m[14] = pos[2]; }
            }
            if (m.Any(v => !float.IsFinite(v))) throw new ArgumentException("The matrix must be finite numbers");
            m[3] = m[7] = m[11] = 0; m[15] = 1;
            return m;
        }

        private static object UndoRedo(Session s, bool undo)
        {
            var from = undo ? s.Undo : s.Redo;
            if (!from.TryPeek(out var step)) throw new InvalidOperationException(undo ? "Nothing to undo" : "Nothing to redo");
            var order = undo ? Enumerable.Reverse(step.Patches) : step.Patches;
            foreach (var p in order) s.Package.Patch(p.Level, p.Archive, p.At, undo ? p.Before : p.After);
            from.Pop();
            (undo ? s.Redo : s.Undo).Push(step);
            s.Revision++;
            return new
            {
                revision = s.Revision, description = step.Description, undone = undo ? step.Description : null, redone = undo ? null : step.Description,
                changed = Archives(step),
            };
        }

        private sealed record SaveArgs(string? OutputPath);

        /// <summary>
        /// Writes the edited files into a folder, each at its place under <c>levels</c>
        /// (<c>&lt;folder&gt;\Valley_multi\Valley_multi_inst.ssg</c>). Never over the source. A packed file
        /// (<c>_coll.ssg</c>, <c>_full.ssg</c>) is compressed again, so its size changes.
        /// </summary>
        private static object Save(Session s, string? json)
        {
            var args = AssetJson.Parse<SaveArgs>(json);
            if (!s.Package.IsDirty) throw new InvalidOperationException("No changes to save");
            if (string.IsNullOrWhiteSpace(args?.OutputPath)) throw new ArgumentException("Choose an output folder (or an .ISO) for the edited level files.");
            string folder = Path.GetFullPath(args.OutputPath);
            if (File.Exists(folder) || Path.HasExtension(folder) && !Directory.Exists(folder))
                throw new ArgumentException($"{Game} saves write a folder of level files: choose a folder (or a .ISO for a disc copy).");
            var written = new List<string>();
            foreach (var (rel, bytes) in s.Package.DirtyFiles())
            {
                string target = Path.GetFullPath(Path.Combine(folder, rel));
                string source = Path.GetFullPath(Path.Combine(s.Package.Levels[Path.GetDirectoryName(rel)!], Path.GetFileName(rel)));
                if (target.Equals(source, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Save to a different folder from the source.");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllBytes(target, bytes);
                written.Add(target);
            }
            return new { written, leaves = s.Package.DirtyNames(), revision = s.Revision };
        }

        private static string? Str(JsonElement a, string name) =>
            a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static float[]? Vec(JsonElement a, string name, int n)
        {
            if (!a.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) return null;
            var list = v.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.Number ? e.GetSingle() : throw new ArgumentException($"\"{name}\" holds numbers")).ToArray();
            if (list.Length != n) throw new ArgumentException($"\"{name}\" needs {n} numbers");
            return list;
        }
    }
}
