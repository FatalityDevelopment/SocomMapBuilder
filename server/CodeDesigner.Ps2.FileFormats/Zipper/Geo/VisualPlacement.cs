using CodeDesigner.Ps2.FileFormats.Assets;
using System.Text.RegularExpressions;
using CodeDesigner.Ps2.FileFormats.Zipper.Model;

namespace CodeDesigner.Ps2.FileFormats.Zipper.Geo
{
    /// <summary>
    /// Per-visual placement (port of buildVisualPlacementMap, zed-geo-parser.ts). A model's N&lt;node&gt;_&lt;visual&gt;
    /// stubs belong to one GEO node: &lt;node&gt; counts the nodes of the model's GEO subtree that own a
    /// <c>visuals</c> group in pre-order (root, then <c>children</c> recursively) and &lt;visual&gt; is the
    /// <c>vis</c> entry inside it. Chunk positions are in that node's space, so each visual gets the node's
    /// matrix accumulated from the model root and its own vparams position unit.
    /// </summary>
    public static class VisualPlacement
    {
        public sealed record Placement(float[] Matrix, ZedModelFile.PositionUnit? Unit, string NodeName);

        private static readonly Regex KeyRx = new(@"^N(\d+)(?:_[A-Z]*\d+)*_[A-Z]*(\d+)$", RegexOptions.IgnoreCase);

        public static (int Node, int Visual)? ParseKey(string key)
        {
            Match m = KeyRx.Match(key.Trim());
            return m.Success ? (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)) : null;
        }

        private sealed record Walked(GeoNode Node, float[] Matrix, IReadOnlyList<GeoNode> Vis);

        private static List<Walked> Walk(GeoNode root)
        {
            var list = new List<Walked>();
            void Visit(GeoNode n, float[] m)
            {
                list.Add(new Walked(n, m, n.Visuals));
                foreach (GeoNode c in n.SceneChildren) Visit(c, Matrix4.Multiply(m, c.Transform));
            }
            Visit(root, Matrix4.Identity());
            return list;
        }

        /// <summary>Placement per visual key; both numberings are tried, the one placing more keys wins.</summary>
        public static Dictionary<string, Placement> Build(GeoNode root, IEnumerable<string> keys)
        {
            var nodes = Walk(root);
            var visualNodes = nodes.Where(n => n.Vis.Count > 0).ToList();
            var unique = keys.Distinct().ToList();
            Dictionary<string, Placement> Attempt(List<Walked> list)
            {
                var map = new Dictionary<string, Placement>(StringComparer.OrdinalIgnoreCase);
                foreach (string key in unique)
                {
                    if (ParseKey(key) is not var (node, visual)) continue;
                    if (node >= list.Count || visual >= list[node].Vis.Count) continue;
                    byte[]? vp = list[node].Vis[visual].Child("vparams")?.Data;
                    ZedModelFile.PositionUnit? unit = vp is { Length: >= 4 } ? ZedModelFile.PositionUnitForFlags(BitConverter.ToUInt32(vp, 0)) : null;
                    map[key] = new Placement(list[node].Matrix, unit, list[node].Node.Name);
                }
                return map;
            }
            var byVisual = Attempt(visualNodes);
            var byAll = Attempt(nodes);
            return byAll.Count > byVisual.Count ? byAll : byVisual;
        }
    }
}
