using System.Text.Json;
using CodeDesigner.Ps2.FileFormats.Swbf;

namespace MapBuilder.Server.Services.Assets
{
    /// <summary>
    /// Battlefront map edits: moving and turning a world's placements (props, command posts, vehicle spawns). Each one
    /// rewrites the placement's 48-byte XFRM in its level's bytes, so no chunk changes size; a step keeps every patch's
    /// bytes before and after for undo and redo. Saving writes the edited levels as .LVL files, or a copy of the disc
    /// image with them written over their own bytes.
    /// </summary>
    internal static partial class SwbfAssetService
    {
        internal sealed record Patch(int Level, int At, byte[] Before, byte[] After);
        internal sealed record Step(string Description, List<Patch> Patches);

        private static string[] DirtyNames(Session s) => s.Package.DirtyLevels().Select(l => s.Package.Levels[l].Name).ToArray();

        private static object History(Session s)
        {
            object[] Steps(IEnumerable<Step> steps) => steps
                .Select(st => new { description = st.Description, archives = st.Patches.Select(p => s.Package.Levels[p.Level].Name).Distinct().ToArray() }).ToArray();
            return new { revision = s.Revision, undo = Steps(s.Undo), redo = Steps(s.Redo), dirty = DirtyNames(s) };
        }

        private static object Edit(Session s, string? json)
        {
            using var doc = JsonDocument.Parse(json ?? "{}");
            var a = doc.RootElement;
            if (s.Map == null) throw new InvalidOperationException("This level has no map to edit.");
            if (a.TryGetProperty("revision", out var rev) && rev.ValueKind == JsonValueKind.Number && rev.GetInt32() != s.Revision)
                throw new InvalidOperationException("The map changed. Refresh before applying this edit.");
            if (a.TryGetProperty("mission", out var mis) && mis.ValueKind == JsonValueKind.String && !string.Equals(mis.GetString(), s.Map, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"The selected map changed to {s.Map}. Refresh before editing {mis.GetString()}.");

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
                for (int i = patches.Count - 1; i >= 0; i--) s.Package.Patch(patches[i].Level, patches[i].At, patches[i].Before);
                throw;
            }
            string description = Str(a, "label") ?? (descriptions.Count == 1 ? descriptions[0] : $"{descriptions.Count} edits");
            var changed = patches.Select(p => s.Package.Levels[p.Level].Name).Distinct().ToArray();
            if (a.TryGetProperty("dryRun", out var dry) && dry.ValueKind == JsonValueKind.True)
            {
                for (int i = patches.Count - 1; i >= 0; i--) s.Package.Patch(patches[i].Level, patches[i].At, patches[i].Before);
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

        /// <summary>One edit: moveInstance {instance, matrix | position | yaw} or movePoint {name, position}.</summary>
        private static string One(Session s, JsonElement a, List<Patch> patches, List<string> warnings)
        {
            switch (Str(a, "op"))
            {
                case "moveInstance":
                {
                    string id = Str(a, "instance") ?? throw new ArgumentException("\"instance\" (an id from the map scene) is required");
                    var (record, level) = FindInstance(s, id);
                    Apply(s, level, record, Placement(a, record.Matrix), patches);
                    return $"move {id}";
                }
                case "movePoint":
                {
                    string name = Str(a, "name") ?? throw new ArgumentException("\"name\" (a point) is required");
                    if (a.TryGetProperty("newName", out _) || a.TryGetProperty("team", out _) || a.TryGetProperty("kind", out _))
                        throw new NotSupportedException("Battlefront points can be moved; renaming them or changing their team is not supported yet.");
                    var point = PointRefs(s).FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                        ?? throw new KeyNotFoundException($"No point {name} on {s.Map}");
                    var pos = Vec(a, "position", 3) ?? throw new ArgumentException("\"position\" [x, y, z] is required");
                    var m = (float[])point.Record.Matrix.Clone();
                    m[12] = pos[0]; m[13] = pos[1]; m[14] = pos[2];
                    Apply(s, point.Level, point.Record, m, patches);
                    if (point.Kind == "command post") warnings.Add("The command post's capture and control regions and its spawn path stay where they are.");
                    return $"move {name}";
                }
                default:
                    throw new NotSupportedException($"Battlefront maps support moveInstance and movePoint (and batches of them). '{Str(a, "op")}' is unavailable.");
            }
        }

        private static void Apply(Session s, int level, SwbfWorld.Record record, float[] matrix, List<Patch> patches)
        {
            if (record.XfrmAt < 0) throw new InvalidOperationException("That placement has no transform to edit.");
            var after = SwbfWorld.Xfrm(matrix);
            var before = s.Package.ReadAt(level, record.XfrmAt, 48);
            if (before.AsSpan().SequenceEqual(after)) return;
            s.Package.Patch(level, record.XfrmAt, after);
            patches.Add(new Patch(level, record.XfrmAt, before, after));
        }

        /// <summary>A world instance by its scene id (re-read, so it carries this session's earlier edits).</summary>
        private static (SwbfWorld.Record Record, int Level) FindInstance(Session s, string id)
        {
            foreach (var w in s.Package.WorldsIn(s.Map!))
            {
                int n = 0;
                foreach (var r in s.Package.ReadWorld(w).Instances)
                    if (InstanceId(r, n++).Equals(id, StringComparison.OrdinalIgnoreCase)) return (r, w.Level);
            }
            throw new KeyNotFoundException($"No instance {id} on {s.Map}");
        }

        /// <summary>The new matrix: "matrix" (16 numbers as the scene gives them), else "position" and / or "yaw"
        /// (degrees, turning about Y from the current matrix). Battlefront placements are not scaled.</summary>
        private static float[] Placement(JsonElement a, float[] current)
        {
            float[] m;
            if (Vec(a, "matrix", 16) is { } given) m = given;
            else
            {
                m = (float[])current.Clone();
                if (a.TryGetProperty("scale", out var sc) && sc.ValueKind == JsonValueKind.Number && Math.Abs(sc.GetSingle() - 1) > 1e-3)
                    throw new NotSupportedException("Battlefront placements cannot be scaled.");
                if (a.TryGetProperty("yaw", out var y) && y.ValueKind == JsonValueKind.Number)
                {
                    double r = y.GetDouble() * Math.PI / 180;
                    float c = (float)Math.Cos(r), sn = (float)Math.Sin(r);
                    float[] rot = { c, 0, -sn, 0, 1, 0, sn, 0, c };
                    for (int i = 0; i < 3; i++) for (int k = 0; k < 3; k++) m[i * 4 + k] = rot[i * 3 + k];
                }
                if (Vec(a, "position", 3) is { } pos) { m[12] = pos[0]; m[13] = pos[1]; m[14] = pos[2]; }
            }
            for (int r = 0; r < 3; r++)
            {
                float len = MathF.Sqrt(m[r * 4] * m[r * 4] + m[r * 4 + 1] * m[r * 4 + 1] + m[r * 4 + 2] * m[r * 4 + 2]);
                if (Math.Abs(len - 1) > 0.01f) throw new NotSupportedException("Battlefront placements cannot be scaled; move or turn them instead.");
            }
            if (m.Take(15).Any(v => !float.IsFinite(v))) throw new ArgumentException("The matrix must be finite numbers");
            return m;
        }

        private static object UndoRedo(Session s, bool undo)
        {
            var from = undo ? s.Undo : s.Redo;
            if (!from.TryPeek(out var step)) throw new InvalidOperationException(undo ? "Nothing to undo" : "Nothing to redo");
            var order = undo ? Enumerable.Reverse(step.Patches) : step.Patches;
            foreach (var p in order) s.Package.Patch(p.Level, p.At, undo ? p.Before : p.After);
            from.Pop();
            (undo ? s.Redo : s.Undo).Push(step);
            s.Revision++;
            return new
            {
                revision = s.Revision, description = step.Description, undone = undo ? step.Description : null, redone = undo ? null : step.Description,
                changed = step.Patches.Select(p => s.Package.Levels[p.Level].Name).Distinct().ToArray(),
            };
        }

        private sealed record SaveArgs(string? OutputPath);

        /// <summary>
        /// Writes the edited levels: to a folder (each at its disc path, e.g. TAT/TAT2.LVL, or its own name for a loose
        /// level), to one .LVL path when one level changed, or (from a disc) to a new .ISO copy of the disc with the
        /// edited levels written in place. Never over the source.
        /// </summary>
        private static object Save(Session s, string? json)
        {
            var args = AssetJson.Parse<SaveArgs>(json);
            var dirty = s.Package.DirtyLevels();
            if (dirty.Count == 0) throw new InvalidOperationException("No changes to save");
            if (string.IsNullOrWhiteSpace(args?.OutputPath)) throw new ArgumentException("Choose an output folder, .LVL or .ISO path for the copy.");
            string path = Path.GetFullPath(args.OutputPath);
            if (path.Equals(s.Package.SourcePath, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Save to a copy, not over the source.");
            var written = new List<string>();
            string ext = Path.GetExtension(path);
            if (ext.Equals(".iso", StringComparison.OrdinalIgnoreCase))
            {
                s.Package.WriteDisc(path);
                written.Add(path);
            }
            else if (ext.Equals(".lvl", StringComparison.OrdinalIgnoreCase))
            {
                if (dirty.Count > 1) throw new ArgumentException($"{dirty.Count} levels changed ({string.Join(", ", DirtyNames(s))}); save to a folder instead.");
                s.Package.WriteLevel(dirty[0], path);
                written.Add(path);
            }
            else
            {
                foreach (int level in dirty)
                {
                    string target = Path.GetFullPath(Path.Combine(path, s.Package.Levels[level].Name.Replace('/', Path.DirectorySeparatorChar)));
                    if (target.Equals(s.Package.SourcePath, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Save to a different folder from the source.");
                    s.Package.WriteLevel(level, target);
                    written.Add(target);
                }
            }
            return new { written, leaves = DirtyNames(s), revision = s.Revision };
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
