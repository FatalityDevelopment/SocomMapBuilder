using CodeDesigner.Ps2.FileFormats.Assets;
using CodeDesigner.Ps2.FileFormats.Zipper.RDR;
using CodeDesigner.Ps2.FileFormats.Zipper.ZED;

namespace CodeDesigner.Ps2.FileFormats.Zipper.Package
{
    /// <summary>
    /// The map's lighting baked into a model's vertex colours, the way retail props are prelit. The map table
    /// (<c>READERM.ZAR › &lt;map&gt;.rdr › world_params.GlobalLighting</c>) gives an ambient colour and three
    /// directional lights, each <c>RGB</c> and <c>dir</c> (the way the light travels). A vertex's colour is half of
    /// ambient + Σ RGB × max(0, −normal · dir), times the material's colour, clamped (0x80 is full brightness).
    /// MP2's yellow crane, unshadowed, matches this exactly in red (within 1 over its 516 vertices), with green and
    /// blue its paint's share of it; props with baked shadows or occlusion are darker in places.
    /// </summary>
    public static class MeshLighting
    {
        public sealed record Light(float[] Rgb, float[] Dir);
        public sealed record Lighting(float[] Ambient, IReadOnlyList<Light> Lights);

        /// <summary>The map's GlobalLighting, or null when its table has none.</summary>
        public static Lighting? OfMap(ZipperPackage pkg)
        {
            if (pkg.Entry("READERM.ZAR") is not { } reader || pkg.MapNamespace is not { } ns) return null;
            var record = ZedContainer.FromBytes(reader.Data).Nodes.FirstOrDefault(n => n.Name.Equals(ns + ".rdr", StringComparison.OrdinalIgnoreCase));
            if (record == null) return null;
            object? global = Find(RdrShape.Shape(RdrFile.Parse(record.Data).BuildTree()), "GlobalLighting");
            var pairs = Pairs(global);
            if (pairs.Count == 0) return null;
            float[]? ambient = null;
            var lights = new List<Light>();
            foreach (var (key, body) in pairs)
            {
                var rgb = Floats(Value(body, "RGB"));
                if (key == "ambient") ambient = rgb;
                else if (key.StartsWith("light", StringComparison.Ordinal) && rgb != null && Floats(Value(body, "dir")) is { Length: 3 } dir
                    && !(Value(body, "active") is int active && active == 0))
                    lights.Add(new Light(rgb, dir));
            }
            return ambient == null && lights.Count == 0 ? null : new Lighting(ambient ?? new float[3], lights);
        }

        /// <summary>
        /// New vertex colours for every chunk that has colours (alpha kept), lit by each chunk's own normals. <paramref name="scale"/>
        /// is the light-to-colour factor (retail 0.5); <paramref name="chunks"/> limits it to those geometry offsets;
        /// <paramref name="tint"/> is the material's colour (0-1 a channel, white by default: textures carry the colour).
        /// </summary>
        public static Dictionary<int, IReadOnlyList<ZipperModelMesh.VertexEdit>> Bake(ModelMesh mesh, Lighting lighting, float scale = 0.5f, ISet<int>? chunks = null, float[]? tint = null)
        {
            tint ??= new[] { 1f, 1f, 1f };
            var edits = new Dictionary<int, IReadOnlyList<ZipperModelMesh.VertexEdit>>();
            foreach (var c in mesh.Chunks)
            {
                if (c.Colors == null || c.Normals.Length < c.VertexCount * 3 || (chunks != null && !chunks.Contains(c.GeometryOffset))) continue;
                var list = new List<ZipperModelMesh.VertexEdit>(c.VertexCount);
                for (int v = 0; v < c.VertexCount; v++)
                {
                    // The chunk's own normal: retail prelighting fits it unrotated (MP2's crane exactly), not the
                    // normal turned by the visual's GEO node.
                    float nx = c.Normals[v * 3], ny = c.Normals[v * 3 + 1], nz = c.Normals[v * 3 + 2];
                    float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                    if (len > 0) { nx /= len; ny /= len; nz /= len; }
                    var color = new byte[4];
                    for (int ch = 0; ch < 3; ch++)
                    {
                        float light = lighting.Ambient[ch];
                        foreach (var l in lighting.Lights) light += l.Rgb[ch] * MathF.Max(0, -(nx * l.Dir[0] + ny * l.Dir[1] + nz * l.Dir[2]));
                        color[ch] = (byte)Math.Clamp((int)MathF.Round(light * scale * tint[ch]), 0, 255);
                    }
                    color[3] = c.Colors[v * 4 + 3];
                    list.Add(new ZipperModelMesh.VertexEdit(v, Color: color));
                }
                if (list.Count > 0) edits[c.GeometryOffset] = list;
            }
            return edits;
        }

        // A table's key / value pairs: an alternating list or a shaped object.
        private static List<(string Key, object Value)> Pairs(object? node)
        {
            var pairs = new List<(string, object)>();
            if (node is IDictionary<string, object> d) foreach (var kv in d) pairs.Add((kv.Key, kv.Value));
            else if (node is List<object> list)
                for (int i = 0; i + 1 < list.Count; i++)
                    if (list[i] is string key) { pairs.Add((key, list[i + 1])); i++; }
            return pairs;
        }

        // The value after a key anywhere below.
        private static object? Find(object? node, string key)
        {
            foreach (var (k, v) in Pairs(node)) if (k == key) return v;
            IEnumerable<object> children = node is IDictionary<string, object> d ? d.Values : node as List<object> ?? new List<object>();
            foreach (var c in children) if (Find(c, key) is { } hit) return hit;
            return null;
        }

        private static object? Value(object fields, string key) => Pairs(fields).FirstOrDefault(p => p.Key == key).Value;

        private static float[]? Floats(object? v) => v is List<object> l && l.All(x => x is int or float or double)
            ? l.Select(x => Convert.ToSingle(x)).ToArray() : null;
    }
}
