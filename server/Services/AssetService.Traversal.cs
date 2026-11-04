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
}
