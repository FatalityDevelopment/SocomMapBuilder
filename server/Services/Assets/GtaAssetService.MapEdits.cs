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
        internal sealed record Move(string File, int Row, GtaPackage.Placement? Before, GtaPackage.Placement? After);

        private static bool IsMapOp(JsonElement op) =>
            op.TryGetProperty("op", out var o) && o.GetString() is "moveInstance" or "placeInstance" or "deleteInstance" or "batch";

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

        private static string InstanceId(GtaPackage.Placement p) => $"{p.Zone}:{Path.GetFileNameWithoutExtension(p.File)}:{p.Row}";

        private static float[] ReadPlacementMatrix(JsonElement op, float[]? from = null)
        {
            if (op.TryGetProperty("scale", out var sc) && (!sc.TryGetSingle(out var scale) || !float.IsFinite(scale) || Math.Abs(scale - 1) > .001f))
                throw new NotSupportedException("GTA placements cannot be scaled (IPLs store a position and a rotation).");
            if (Vec(op, "matrix", 16) is { } matrix) return matrix;
            var position = Vec(op, "position", 3) ?? throw new ArgumentException("Supply a placement matrix or position.");
            var m = from == null ? CodeDesigner.Ps2.FileFormats.Assets.Matrix4.Identity() : (float[])from.Clone();
            if (op.TryGetProperty("yaw", out var heading))
            {
                var rad = heading.GetSingle() * (MathF.PI / 180);
                m = CodeDesigner.Ps2.FileFormats.Assets.Matrix4.Identity();
                m[0] = m[10] = MathF.Cos(rad); m[8] = MathF.Sin(rad); m[2] = -m[8];
            }
            m[12] = position[0]; m[13] = position[1]; m[14] = position[2];
            return m;
        }

        private static object MapEdit(Session s, JsonElement a)
        {
            if (!HasMap(s)) throw new InvalidOperationException("Open the GTA: San Andreas disc (or its mounted folder) to edit the map.");
            if (a.TryGetProperty("revision", out var rev) && rev.ValueKind == JsonValueKind.Number && rev.GetInt32() != s.Revision)
                throw new InvalidOperationException("The map changed. Refresh before applying this edit.");
            var ops = Str(a, "op") == "batch"
                ? (a.TryGetProperty("ops", out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().ToList() : throw new ArgumentException("Supply an ops array."))
                : new List<JsonElement> { a };
            var moves = new List<Move>();
            var warnings = new List<string>();
            var results = new List<object>();
            var explicitMoves = ops.Where(op => Str(op, "op") == "moveInstance").Select(op => Str(op, "instance")).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var explicitDeletes = ops.Where(op => Str(op, "op") == "deleteInstance").Select(op => Str(op, "instance")).ToHashSet(StringComparer.OrdinalIgnoreCase);
            string? newInstance = null;
            try
            {
                foreach (var op in ops)
                {
                    switch (Str(op, "op"))
                    {
                        case "placeInstance":
                            var model = Find(s, Str(op, "model"), op.TryGetProperty("archive", out var ar) ? ar.GetString() : null);
                            var template = Str(op, "templateInstance") is { Length: > 0 } id ? FindPlacement(s, id) : null;
                            var added = s.Package.AddPlacement(CurrentArea(s)!, model, ReadPlacementMatrix(op), template);
                            moves.Add(new Move(added.File, added.Row, null, added));
                            newInstance = InstanceId(added);
                            results.Add(new { newInstance });
                            break;
                        case "moveInstance":
                            var p = FindPlacement(s, Str(op, "instance"));
                            var moved = s.Package.MovePlacement(p.File, p.Row, ReadPlacementMatrix(op, p.Matrix));
                            moves.Add(new Move(p.File, p.Row, p, moved));
                            if (!op.TryGetProperty("followLod", out var follow) || follow.ValueKind != JsonValueKind.False)
                                MaybeMoveLod(s, p, moved, moves, warnings, explicitMoves);
                            results.Add(new { instance = InstanceId(moved) });
                            break;
                        case "deleteInstance":
                            var removed = FindPlacement(s, Str(op, "instance"));
                            var lod = s.Package.LodOf(removed);
                            Remove(removed);
                            // Remove the now-orphaned stand-in as well. Shared LODs still serve other buildings.
                            if (lod != null && !lod.IsLod && !explicitDeletes.Contains(InstanceId(lod))) Remove(lod);
                            results.Add(new { instance = InstanceId(removed) });
                            break;
                        default: throw new NotSupportedException($"Unsupported GTA map edit: {Str(op, "op")}");
                    }
                }
            }
            catch
            {
                Replay(s, moves, true);
                throw;
            }
            string description = Str(a, "label") is { Length: > 0 } l ? l : ops.Count == 1 ? Str(ops[0], "op") : $"edit {ops.Count} placements";
            string[] changed = moves.Select(m => s.Package.PlacementFileLabel(m.File)).Distinct().ToArray();
            if (a.TryGetProperty("dryRun", out var dry) && dry.ValueKind == JsonValueKind.True)
            {
                Replay(s, moves, true);
                return new { dryRun = true, wouldDo = new { description, changed, warnings } };
            }
            if (moves.Count > 0)
            {
                s.Undo.Push(new Step(null, description, null, null, moves));
                s.Redo.Clear();
                s.Overviews = null;
                s.Revision++;
            }
            return new { revision = s.Revision, description, changed, warnings = warnings.Distinct().ToArray(), newInstance, results };

            void Remove(GtaPackage.Placement row)
            {
                s.Package.SetPlacement(row.File, row.Row, null);
                moves.Add(new Move(row.File, row.Row, row, null));
            }
        }

        private static void Replay(Session s, IReadOnlyList<Move> moves, bool undo)
        {
            foreach (var m in undo ? moves.Reverse() : moves)
                s.Package.SetPlacement(m.File, m.Row, undo ? m.Before : m.After);
            s.Overviews = null;
        }

        private static void ApplyMoves(Session s, IReadOnlyList<Move> moves, bool undo)
        {
            Replay(s, moves, undo);
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
