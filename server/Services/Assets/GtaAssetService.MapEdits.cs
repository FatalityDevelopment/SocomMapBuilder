using System.Text.Json;
using CodeDesigner.Ps2.FileFormats.Gta;

namespace MapBuilder.Server.Services.Assets
{
    /// <summary>
    /// Moving and turning San Andreas' map placements in the Map Builder: moveInstance {instance, matrix | position}
    /// and batches of them. A step keeps each placement's matrix before and after for undo and redo; saving writes the
    /// edited IPLs (see <see cref="GtaPackage.SaveMapEdits"/>) into a folder, or a disc image copy through the shared
    /// disc save.
    /// </summary>
    internal static partial class GtaAssetService
    {
        internal sealed record Move(string File, int Row, float[] Before, float[] After);

        private static bool IsMapOp(JsonElement op) =>
            op.TryGetProperty("op", out var o) && o.GetString() is "moveInstance" or "batch";

        private static string Str(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

        private static float[]? Vec(JsonElement e, string key, int n)
        {
            if (!e.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.Array) return null;
            var values = v.EnumerateArray().Select(x => x.GetSingle()).ToArray();
            return values.Length == n ? values : throw new ArgumentException($"\"{key}\" has {n} numbers");
        }

        /// <summary>A placement by its scene instance id ("zone:file:row", as ASSET_MAP_SCENE gives it).</summary>
        private static GtaPackage.Placement FindPlacement(Session s, string id)
        {
            var parts = id.Split(':');
            if (parts.Length != 3 || !int.TryParse(parts[2], out int row)) throw new ArgumentException($"\"{id}\" is not a placement id from the map scene");
            return s.Package.Placements.FirstOrDefault(p => p.Row == row && p.Zone.Equals(parts[0], StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileNameWithoutExtension(p.File).Equals(parts[1], StringComparison.OrdinalIgnoreCase))
                ?? throw new KeyNotFoundException($"No placement {id}");
        }

        private static object MapEdit(Session s, JsonElement a)
        {
            if (!HasMap(s)) throw new InvalidOperationException("Open the GTA: San Andreas disc (or its mounted folder) to edit the map.");
            if (a.TryGetProperty("revision", out var rev) && rev.ValueKind == JsonValueKind.Number && rev.GetInt32() != s.Revision)
                throw new InvalidOperationException("The map changed. Refresh before applying this edit.");
            var ops = Str(a, "op") == "batch"
                ? (a.TryGetProperty("ops", out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().ToList() : throw new ArgumentException("\"ops\" (an array) is required"))
                : new List<JsonElement> { a };
            var moves = new List<Move>();
            var warnings = new List<string>();
            try
            {
                foreach (var op in ops)
                {
                    if (Str(op, "op") != "moveInstance")
                        throw new NotSupportedException($"GTA maps support moveInstance (and batches of it). '{Str(op, "op")}' is unavailable: placements cannot be added or deleted yet.");
                    var p = FindPlacement(s, Str(op, "instance") is { Length: > 0 } id ? id : throw new ArgumentException("\"instance\" (an id from the map scene) is required"));
                    float[] after;
                    if (Vec(op, "matrix", 16) is { } m) after = m;
                    else if (Vec(op, "position", 3) is { } pos) { after = (float[])p.Matrix.Clone(); after[12] = pos[0]; after[13] = pos[1]; after[14] = pos[2]; }
                    else throw new ArgumentException("moveInstance needs \"matrix\" or \"position\"");
                    if (op.TryGetProperty("scale", out var sc) && sc.ValueKind == JsonValueKind.Number && Math.Abs(sc.GetSingle() - 1) > 1e-3)
                        throw new NotSupportedException("GTA placements cannot be scaled (IPLs store a position and a rotation).");
                    var moved = s.Package.MovePlacement(p.File, p.Row, after);
                    if (s.Package.LodOf(p) is { Model: { } lod })
                        warnings.Add($"{p.Name}'s low-detail model {lod.Name} stays where it was; the game shows it from a distance.");
                    moves.Add(new Move(p.File, p.Row, p.Matrix, moved.Matrix));
                }
            }
            catch
            {
                for (int i = moves.Count - 1; i >= 0; i--) s.Package.MovePlacement(moves[i].File, moves[i].Row, moves[i].Before);
                throw;
            }
            string description = Str(a, "label") is { Length: > 0 } l ? l : moves.Count == 1 ? $"move {Path.GetFileNameWithoutExtension(moves[0].File)}#{moves[0].Row}" : $"move {moves.Count} placements";
            var changed = moves.Select(m => s.Package.PlacementFileLabel(m.File)).Distinct().ToArray();
            if (a.TryGetProperty("dryRun", out var dry) && dry.ValueKind == JsonValueKind.True)
            {
                for (int i = moves.Count - 1; i >= 0; i--) s.Package.MovePlacement(moves[i].File, moves[i].Row, moves[i].Before);
                return new { dryRun = true, wouldDo = new { description, changed, warnings } };
            }
            if (moves.Count > 0)
            {
                s.Undo.Push(new Step(null, description, null, null, moves));
                s.Redo.Clear();
                s.Revision++;
            }
            return new { revision = s.Revision, description, changed, warnings = warnings.Distinct().ToArray() };
        }

        private static void ApplyMoves(Session s, IReadOnlyList<Move> moves, bool undo)
        {
            var order = undo ? moves.Reverse() : moves;
            foreach (var m in order) s.Package.MovePlacement(m.File, m.Row, undo ? m.Before : m.After);
            s.Revision++;
        }

        /// <summary>The edited IPLs as the session's "dirty" names.</summary>
        private static IEnumerable<string> DirtyPlacementFiles(Session s) =>
            s.Package.IsDisc && s.Package.MapAreas.Count > 0 ? s.Package.EditedPlacementFiles.Select(s.Package.PlacementFileLabel) : Array.Empty<string>();

        /// <summary>Writes the moved placements' IPLs (and the session's edited DFFs that share their IMG) under a folder.</summary>
        private static string[] SaveMap(Session s, string folder)
        {
            if (File.Exists(folder)) throw new ArgumentException("Choose a folder for the edited map files (they are written at their disc paths).");
            return s.Package.SaveMapEdits(folder).ToArray();
        }
    }
}
