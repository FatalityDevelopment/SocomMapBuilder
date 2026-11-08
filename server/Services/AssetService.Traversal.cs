using CodeDesigner.Ps2.FileFormats.Assets;
using System.Text.Json;
using CodeDesigner.Ps2.FileFormats.Zipper.ZED;

namespace MapBuilder.Server.Services;

internal sealed partial class ZipperAssetService
{
    // Complete four-point face profiles sampled on disc. Preserve the opaque fields as a unit.
    private static object EditTraversalFace(AssetSession s, JsonElement a)
    {
        CheckActionRevision(s, a);
        var (ns, node) = SceneNode(s, a);
        var map = s.Package.MapScene() ?? throw new InvalidOperationException("No map scene.");
        if (ns != map.Namespace || !ActionTargets(map.Scene.MapRoot).Any(t => t.Node.Index == node.Index))
            throw new ArgumentException("Select a placed map node.");
        var entry = s.Package.Entry(ns + "_GEO.ZED")!;
        var region = node.Child("di");
        int? index = OptInt(a, "face");
        var face = region?.Children.FirstOrDefault(n => n.Index == index && n.Name == "di");
        if (index.HasValue && (face?.Child("points")?.Data.Length != 64 || face.Child("params")?.Data.Length != 12))
            throw new ArgumentException("Select a four-point face on this placement.");
        byte[] profile = Str(a, "profile") switch
        {
            "climb" => Convert.FromHexString("0000000003000000121C4044"), // MP51 di_climb_uppy13_1
            "ladder" => Convert.FromHexString("000400000300000012002044"), // M51 ladderupdown
            _ => throw new ArgumentException("Choose climb or ladder.")
        };
        byte[] output;
        if (face != null)
            output = ZedSplice.Replace(entry.Data, face.Child("params")!.Index, profile);
        else
        {
            var (min, max) = node.Bounds ?? throw new ArgumentException("This node has no local bounds.");
            string side = Str(a, "side") ?? "";
            bool alongX = side is "+Z" or "-Z";
            if (!alongX && side is not ("+X" or "-X")) throw new ArgumentException("Choose a local side.");
            int across = alongX ? 0 : 2, normal = alongX ? 2 : 0;
            if (max[1] <= min[1] || max[across] <= min[across]) throw new ArgumentException("This side has no area.");
            var points = new byte[64];
            for (int i = 0; i < 4; i++)
            {
                float[] point = new float[4];
                point[normal] = side[0] == '+' ? max[normal] : min[normal];
                bool far = (i is 1 or 2) ^ (side is "+Z" or "-X");
                point[across] = far ? max[across] : min[across];
                point[1] = i < 2 ? max[1] : min[1];
                Buffer.BlockCopy(point, 0, points, i * 16, 16);
            }
            var container = ZedContainer.FromBytes(entry.Data);
            var polygon = new ZedTree.Record("di", [], [new("params", profile), new("points", points)]);
            if (region == null) ZedTree.AppendChild(container, node.Index, new("di", [], [polygon]));
            else ZedTree.AppendChild(container, region.Index, polygon);
            output = container.ToBytes();
        }
        return ApplyEdit(s, Str(a, "label") ?? "Edit traversal face", _ => new Dictionary<string, byte[]> { [entry.Leaf] = output });
    }

    // MP2's ladders (ladderdown, ladderdown_1 …): params of both faces.
    private static readonly byte[] LadderProfile = Convert.FromHexString("000000000300000012002844");

    /// <summary>
    /// addLadder {position, top, yaw?, width?, name?}: a ladder the way MP2 builds them, a bare scene node under
    /// worldmodel named ladderdown_N with two faces and nothing drawn: one from the foot to the top facing the
    /// climber (yaw: the way that face points, degrees about Y, 0 = +Z), and one from the top to 10 units above it
    /// facing away. Not yet tried in game.
    /// </summary>
    private static object AddLadder(AssetSession s, JsonElement a)
    {
        float[] foot = Vec(a, "position", 3) ?? throw new ArgumentException("\"position\" [x, y, z] (the ladder's foot, on the face it climbs) is required");
        float top = a.TryGetProperty("top", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetSingle() : throw new ArgumentException("\"top\" (the height it climbs to) is required");
        if (top - foot[1] < 4) throw new ArgumentException("\"top\" must be above the foot");
        float width = a.TryGetProperty("width", out var w) && w.ValueKind == JsonValueKind.Number ? w.GetSingle() : 8;
        if (width is <= 0 or > 64) throw new ArgumentException("\"width\" is 0-64 units (MP2's are 8)");
        float yaw = (a.TryGetProperty("yaw", out var y) && y.ValueKind == JsonValueKind.Number ? y.GetSingle() : 0) * MathF.PI / 180f;
        var map = s.Package.MapScene() ?? throw new InvalidOperationException("No map scene.");
        var world = map.Scene.MapRoot;
        var children = world.Child("children") ?? throw new InvalidOperationException("worldmodel has no children list");
        var names = new HashSet<string>(ActionTargets(world).Select(x => x.Node.Name), StringComparer.OrdinalIgnoreCase);
        string name = Str(a, "name") ?? Enumerable.Range(1, 9999).Select(i => $"ladderdown_{i}").First(n => !names.Contains(n));
        if (names.Contains(name)) throw new ArgumentException($"A scene node is already named {name}");

        // Into worldmodel's space; across the ladder is r, its face points along n.
        float[] inv = Matrix4.InverseAffine(world.WorldTransform);
        float rx = MathF.Cos(yaw), rz = -MathF.Sin(yaw);
        float[] Corner(float side, float height)
        {
            var (px, py, pz) = Matrix4.Apply(inv, foot[0] + rx * side * width / 2, height, foot[2] + rz * side * width / 2);
            return new[] { px, py, pz };
        }
        float[][] lower = { Corner(-1, top), Corner(-1, foot[1]), Corner(1, foot[1]), Corner(1, top) };
        float[][] upper = { Corner(1, top), Corner(-1, top), Corner(-1, top + 10), Corner(1, top + 10) };
        byte[] Points(float[][] corners)
        {
            var b = new byte[64];
            for (int i = 0; i < 4; i++) for (int k = 0; k < 3; k++) BitConverter.GetBytes(corners[i][k]).CopyTo(b, i * 16 + k * 4);
            return b;
        }
        var all = lower.Concat(upper).ToArray();
        var nparams = new byte[96];
        float[] identity = Matrix4.Identity();
        for (int k = 0; k < 16; k++) BitConverter.GetBytes(identity[k]).CopyTo(nparams, k * 4);
        for (int k = 0; k < 3; k++)
        {
            BitConverter.GetBytes(all.Min(c => c[k])).CopyTo(nparams, 64 + k * 4);
            BitConverter.GetBytes(all.Max(c => c[k])).CopyTo(nparams, 76 + k * 4);
        }
        BitConverter.GetBytes(1).CopyTo(nparams, 88);
        BitConverter.GetBytes(0x80001051u).CopyTo(nparams, 92);
        var record = new ZedTree.Record(name, [], [
            new("nparams", nparams),
            new("di", [], [
                new("di", [], [new("params", (byte[])LadderProfile.Clone()), new("points", Points(lower))]),
                new("di", [], [new("params", (byte[])LadderProfile.Clone()), new("points", Points(upper))]),
            ]),
        ]);
        var entry = s.Package.Archive(map.Namespace, "geo")!;
        var container = ZedContainer.FromBytes(entry.Data);
        int newIndex = ZedTree.AppendChild(container, children.Index, record);
        var result = ApplyEdit(s, Str(a, "label") ?? $"add {name}", _ => new Dictionary<string, byte[]> { [entry.Leaf] = container.ToBytes() });
        return new { result, newGeoIndex = newIndex, name };
    }
}
