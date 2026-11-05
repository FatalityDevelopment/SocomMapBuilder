using CodeDesigner.Ps2.FileFormats.Assets;
using CodeDesigner.Ps2.FileFormats.Gta;

namespace MapBuilder.Server.Services.Assets;

internal static partial class GtaAssetService
{
    private static void MaybeMoveLod(Session s, GtaPackage.Placement before, GtaPackage.Placement after,
        List<Move> moves, List<string> warnings, IReadOnlySet<string> explicitMoves)
    {
        var lod = s.Package.LodOf(before);
        if (lod == null || (lod.File == before.File && lod.Row == before.Row) || before.Matrix.AsSpan().SequenceEqual(after.Matrix)) return;
        if (explicitMoves.Contains(InstanceId(lod))) return;
        if (s.Package.Placements.Any(p => p.Zone == before.Zone && p.LodRow == before.LodRow
            && (p.Row != before.Row || p.File != before.File)))
        {
            warnings.Add($"{before.Name} shares its distant model {lod.Name} with other placements. The distant model stays in place.");
            return;
        }

        // Preserve the LOD's offset and orientation relative to the detailed model, including turns around its pivot.
        var relative = Matrix4.Multiply(Matrix4.InverseAffine(before.Matrix), lod.Matrix);
        var moved = s.Package.MovePlacement(lod.File, lod.Row, Matrix4.Multiply(after.Matrix, relative));
        moves.Add(new Move(lod.File, lod.Row, lod, moved));
    }
}
