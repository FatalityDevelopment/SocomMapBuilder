using CodeDesigner.Ps2.FileFormats.Assets;

namespace CodeDesigner.Ps2.FileFormats.Zipper.Package
{
    /// <summary>
    /// Where a model draws a texture, and painting by those places: the model's triangles that use the texture, in
    /// texture space, each labelled by its body part (a character's heaviest bone, else the visual). The renderer
    /// uploads the decoded rows top-down (flipY off) and samples at V' = 1 − V (viewport-core chunkGeometry), so a UV
    /// lands on pixel (u·W, (1 − v)·H), wrapping.
    /// </summary>
    public static class TexturePaint
    {
        public sealed record Triangle(string Label, float[] Uv);   // 6 floats: u0 v0 u1 v1 u2 v2

        /// <summary>A paint region: the parts' pixels, within <see cref="Uv"/> [u0, v0, u1, v1] when given (part of a
        /// bone's area, such as the lower face).</summary>
        public sealed record Region(IReadOnlySet<string> Labels, byte[]? Color, bool Fill = false, float Strength = 1, float[]? Uv = null, Grit? Grit = null);

        /// <summary>
        /// Wear painted over a region after its colour, each 0..1: <see cref="Weave"/> a fine nylon cross-hatch,
        /// <see cref="Grime"/> dark blotches of feature size <see cref="Scale"/> pixels, <see cref="Wear"/> its rims (the
        /// UV island edges: a pouch's corners and a strap's edges) lightened and scuffed, <see cref="Stitch"/> a dashed
        /// seam just inside them. Seeded, so the same op paints the same pixels.
        /// </summary>
        public sealed record Grit(float Weave = 0, float Grime = 0, float Wear = 0, float Stitch = 0, float Scale = 12, int Seed = 0);

        /// <summary>The model's triangles drawn with <paramref name="texture"/>, labelled.</summary>
        public static List<Triangle> Triangles(ModelMesh mesh, string texture)
        {
            var list = new List<Triangle>();
            foreach (var c in mesh.Chunks.Where(c => string.Equals(c.Texture, texture, StringComparison.OrdinalIgnoreCase)))
            {
                string[] labels = Enumerable.Range(0, c.VertexCount).Select(v => LabelOf(mesh, c, v)).ToArray();
                for (int t = 0; t + 2 < c.Triangles.Length; t += 3)
                {
                    int a = c.Triangles[t], b = c.Triangles[t + 1], d = c.Triangles[t + 2];
                    if (c.Uvs.Length < (Math.Max(a, Math.Max(b, d)) + 1) * 2) continue;
                    // The label most of its corners share (the first corner's on a three-way split).
                    string label = labels[b] == labels[d] ? labels[b] : labels[a];
                    list.Add(new Triangle(label, new[] { c.Uvs[a * 2], c.Uvs[a * 2 + 1], c.Uvs[b * 2], c.Uvs[b * 2 + 1], c.Uvs[d * 2], c.Uvs[d * 2 + 1] }));
                }
            }
            return list;
        }

        private static string LabelOf(ModelMesh mesh, ModelMesh.MeshChunk c, int v)
        {
            if (mesh.Skeleton == null || c.SkinBones == null || c.SkinWeights == null) return c.Visual ?? "model";
            int at = v * ModelMesh.SkinInfluences, best = at;
            for (int k = 1; k < ModelMesh.SkinInfluences; k++)
                if (c.SkinWeights[at + k] > c.SkinWeights[best]) best = at + k;
            int bone = c.SkinBones[best];
            return bone >= 0 && bone < mesh.Skeleton.Bones.Count ? mesh.Skeleton.Bones[bone].Name : "?";
        }

        /// <summary>Per label, the pixels its triangles cover (indices into a W×H image), grown by <paramref name="bleed"/>
        /// pixels so filtering at a UV edge does not pull in the old colour.</summary>
        public static Dictionary<string, HashSet<int>> Masks(IEnumerable<Triangle> triangles, int w, int h, int bleed = 1)
        {
            var masks = new Dictionary<string, HashSet<int>>();
            foreach (var t in triangles)
            {
                if (!masks.TryGetValue(t.Label, out var m)) masks[t.Label] = m = new HashSet<int>();
                float[] x = { t.Uv[0] * w, t.Uv[2] * w, t.Uv[4] * w }, y = { (1 - t.Uv[1]) * h, (1 - t.Uv[3]) * h, (1 - t.Uv[5]) * h };
                int x0 = (int)MathF.Floor(x.Min()) - bleed, x1 = (int)MathF.Ceiling(x.Max()) + bleed;
                int y0 = (int)MathF.Floor(y.Min()) - bleed, y1 = (int)MathF.Ceiling(y.Max()) + bleed;
                if ((long)(x1 - x0) * (y1 - y0) > 4L * w * h) continue;   // a degenerate or wildly tiled triangle
                for (int py = y0; py <= y1; py++)
                    for (int px = x0; px <= x1; px++)
                        if (Near(x, y, px + 0.5f, py + 0.5f, bleed))
                            m.Add(Mod(py, h) * w + Mod(px, w));
            }
            return masks;
        }

        private static int Mod(int a, int n) => ((a % n) + n) % n;

        private static bool InRect(float[] r, float u, float v) => u >= r[0] && u <= r[2] && v >= r[1] && v <= r[3];

        // Inside the triangle, or within `bleed` pixels of it.
        private static bool Near(float[] x, float[] y, float px, float py, int bleed)
        {
            float d = (y[1] - y[2]) * (x[0] - x[2]) + (x[2] - x[1]) * (y[0] - y[2]);
            if (MathF.Abs(d) < 1e-9f) return false;
            float a = ((y[1] - y[2]) * (px - x[2]) + (x[2] - x[1]) * (py - y[2])) / d;
            float b = ((y[2] - y[0]) * (px - x[2]) + (x[0] - x[2]) * (py - y[2])) / d;
            float c = 1 - a - b;
            if (a >= 0 && b >= 0 && c >= 0) return true;
            if (bleed == 0) return false;
            for (int e = 0; e < 3; e++)
                if (SegmentDistance(px, py, x[e], y[e], x[(e + 1) % 3], y[(e + 1) % 3]) <= bleed) return true;
            return false;
        }

        private static float SegmentDistance(float px, float py, float ax, float ay, float bx, float by)
        {
            float dx = bx - ax, dy = by - ay, l = dx * dx + dy * dy;
            float t = l < 1e-9f ? 0 : Math.Clamp(((px - ax) * dx + (py - ay) * dy) / l, 0, 1);
            float qx = ax + t * dx - px, qy = ay + t * dy - py;
            return MathF.Sqrt(qx * qx + qy * qy);
        }

        /// <summary>
        /// Paints <paramref name="rgba"/> (W×H, top-down) in place: each region's pixels take its colour. A tint keeps the
        /// texture's shading and detail (each pixel's brightness against the region's mean scales the colour); a fill
        /// replaces it. <paramref name="Region.Strength"/> blends with what was there. Answers the pixels each region painted.
        /// </summary>
        public static List<int> Paint(byte[] rgba, int w, int h, IReadOnlyList<Triangle> triangles, IReadOnlyList<Region> regions)
        {
            var masks = Masks(triangles, w, h);
            var counts = new List<int>();
            foreach (var r in regions)
            {
                var pixels = masks.Where(kv => r.Labels.Contains(kv.Key)).SelectMany(kv => kv.Value)
                    .Where(i => r.Uv == null || InRect(r.Uv, (i % w + 0.5f) / w, 1 - (i / w + 0.5f) / h))
                    .ToHashSet();
                counts.Add(pixels.Count);
                if (pixels.Count == 0) continue;
                if (r.Color != null) Colour(rgba, pixels, r);
                if (r.Grit != null) Roughen(rgba, w, h, pixels, triangles.Where(t => r.Labels.Contains(t.Label)).ToList(), r.Grit, r.Strength);
            }
            return counts;
        }

        private static void Colour(byte[] rgba, HashSet<int> pixels, Region r)
        {
                float Lum(int i) => 0.299f * rgba[i * 4] + 0.587f * rgba[i * 4 + 1] + 0.114f * rgba[i * 4 + 2];
                float mean = MathF.Max(1, pixels.Average(Lum));
                foreach (int i in pixels)
                {
                    float shade = r.Fill ? 1 : Lum(i) / mean;
                    for (int k = 0; k < 3; k++)
                    {
                        float target = Math.Clamp(r.Color![k] * shade, 0, 255);
                        rgba[i * 4 + k] = (byte)MathF.Round(rgba[i * 4 + k] + (target - rgba[i * 4 + k]) * r.Strength);
                    }
                }
        }

        private static void Roughen(byte[] rgba, int w, int h, HashSet<int> pixels, List<Triangle> triangles, Grit g, float strength)
        {
            var edge = g.Wear > 0 || g.Stitch > 0 ? Rims(triangles, w, h) : new Dictionary<int, int>();
            float scale = MathF.Max(1, g.Scale);
            foreach (int i in pixels)
            {
                int x = i % w, y = i / w;
                float k = 1;
                // Nylon: alternating rows and columns, a little uneven.
                if (g.Weave > 0) k *= 1 + g.Weave * (0.10f * ((x + y) % 2 == 0 ? 1 : -1) + 0.08f * Noise(x, y, 1, g.Seed));
                // Grime: soft blotches, darker where the noise is high.
                if (g.Grime > 0) k *= 1 - g.Grime * 0.45f * MathF.Max(0, 0.6f * Noise(x, y, scale, g.Seed + 11) + 0.4f * Noise(x, y, scale / 3, g.Seed + 23));
                int d = edge.GetValueOrDefault(i, 0);   // the bleed ring outside every island is rim
                float lift = 0;
                // Wear: rims scuffed lighter, broken up by speckle.
                if (g.Wear > 0 && d <= 1) lift = g.Wear * (d == 0 ? 0.35f : 0.2f) * MathF.Max(0, 0.6f + Noise(x, y, 2, g.Seed + 37));
                // A dashed seam two pixels in.
                if (g.Stitch > 0 && d == 2 && (x + y) % 3 != 0) k *= 1 - g.Stitch * 0.4f;
                for (int c = 0; c < 3; c++)
                {
                    float v = rgba[i * 4 + c] * k;
                    v += (255 - v) * lift;
                    rgba[i * 4 + c] = (byte)MathF.Round(rgba[i * 4 + c] + (Math.Clamp(v, 0, 255) - rgba[i * 4 + c]) * strength);
                }
            }
        }

        /// <summary>
        /// Each covered pixel's distance (4-neighbour, wrapping, up to 4) from the rim of the UV island it lies in: a
        /// box face, a strap, a run of triangles sharing uvs. Islands that overlap in an atlas (every pouch mapped onto
        /// the same nylon) each keep their own rim, and a pixel takes the nearest.
        /// </summary>
        private static Dictionary<int, int> Rims(List<Triangle> triangles, int w, int h)
        {
            var key = new Dictionary<(int, int), int>();
            var parent = new List<int>();
            int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
            int Id(float u, float v)
            {
                var k = ((int)MathF.Round(u * 4096), (int)MathF.Round(v * 4096));
                if (!key.TryGetValue(k, out int id)) { key[k] = id = parent.Count; parent.Add(id); }
                return id;
            }
            var first = triangles.Select(t =>
            {
                int a = Id(t.Uv[0], t.Uv[1]), b = Id(t.Uv[2], t.Uv[3]), c = Id(t.Uv[4], t.Uv[5]);
                parent[Find(b)] = Find(a); parent[Find(c)] = Find(a);
                return a;
            }).ToList();
            var dist = new Dictionary<int, int>();
            foreach (var island in triangles.Select((t, i) => (t, i)).GroupBy(x => Find(first[x.i]), x => x.t with { Label = "" }))
            {
                var px = Masks(island, w, h, 0).GetValueOrDefault("") ?? new HashSet<int>();
                var d = new Dictionary<int, int>();
                var queue = new Queue<int>();
                int[] Near(int i) { int x = i % w, y = i / w; return new[] { y * w + Mod(x + 1, w), y * w + Mod(x - 1, w), Mod(y + 1, h) * w + x, Mod(y - 1, h) * w + x }; }
                foreach (int i in px)
                    if (Near(i).Any(n => !px.Contains(n))) { d[i] = 0; queue.Enqueue(i); }
                while (queue.Count > 0)
                {
                    int i = queue.Dequeue();
                    if (d[i] >= 4) continue;
                    foreach (int n in Near(i))
                        if (px.Contains(n) && !d.ContainsKey(n)) { d[n] = d[i] + 1; queue.Enqueue(n); }
                }
                foreach (int i in px)
                {
                    int di = d.GetValueOrDefault(i, 5);
                    dist[i] = dist.TryGetValue(i, out int was) ? Math.Min(was, di) : di;
                }
            }
            return dist;
        }

        // Smooth value noise, −1..1, of feature size `size` pixels.
        private static float Noise(int x, int y, float size, int seed)
        {
            float fx = x / size, fy = y / size;
            int x0 = (int)MathF.Floor(fx), y0 = (int)MathF.Floor(fy);
            float tx = fx - x0, ty = fy - y0;
            tx = tx * tx * (3 - 2 * tx); ty = ty * ty * (3 - 2 * ty);
            float L(int i, int j)
            {
                unchecked
                {
                    uint hsh = (uint)((x0 + i) * 73856093 ^ (y0 + j) * 19349663 ^ seed * 83492791);
                    hsh ^= hsh >> 13; hsh *= 0x5bd1e995; hsh ^= hsh >> 15;
                    return (hsh & 0xFFFFFF) / (float)0x7FFFFF - 1;
                }
            }
            float a = L(0, 0) + (L(1, 0) - L(0, 0)) * tx, b = L(0, 1) + (L(1, 1) - L(0, 1)) * tx;
            return a + (b - a) * ty;
        }

        /// <summary>A distinct colour per label (golden-angle hues), for the layout picture and its legend.</summary>
        public static Dictionary<string, byte[]> Colours(IEnumerable<string> labels)
        {
            var map = new Dictionary<string, byte[]>();
            int i = 0;
            foreach (string l in labels.Distinct().OrderBy(x => x, StringComparer.Ordinal))
            {
                float hue = (i++ * 137.508f) % 360 / 60f;
                float x = 1 - MathF.Abs(hue % 2 - 1);
                (float r, float g, float b) = (int)hue switch { 0 => (1f, x, 0f), 1 => (x, 1f, 0f), 2 => (0f, 1f, x), 3 => (0f, x, 1f), 4 => (x, 0f, 1f), _ => (1f, 0f, x) };
                map[l] = new[] { (byte)(r * 255), (byte)(g * 255), (byte)(b * 255) };
            }
            return map;
        }

        /// <summary>The texture, dimmed, with each label's triangles filled faintly and outlined in its colour,
        /// scaled to at least <paramref name="minSize"/> pixels on its long side (nearest) so outlines read.</summary>
        public static (byte[] Rgba, int Width, int Height) Layout(byte[] texture, int w, int h, IReadOnlyList<Triangle> triangles, IReadOnlyDictionary<string, byte[]> colours, int minSize = 512)
        {
            int scale = Math.Max(1, (minSize + Math.Max(w, h) - 1) / Math.Max(w, h));
            int W = w * scale, H = h * scale;
            var img = new byte[W * H * 4];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int s = ((y / scale) * w + x / scale) * 4, d = (y * W + x) * 4;
                    for (int k = 0; k < 3; k++) img[d + k] = (byte)(texture[s + k] * 0.55f);
                    img[d + 3] = 255;
                }
            foreach (var (label, pixels) in Masks(triangles, W, H, 0))
            {
                var c = colours[label];
                foreach (int i in pixels)
                    for (int k = 0; k < 3; k++) img[i * 4 + k] = (byte)(img[i * 4 + k] * 0.6f + c[k] * 0.4f);
            }
            foreach (var t in triangles)
            {
                var c = colours[t.Label];
                for (int e = 0; e < 3; e++)
                    Line(img, W, H, t.Uv[e * 2] * W, (1 - t.Uv[e * 2 + 1]) * H, t.Uv[(e + 1) % 3 * 2] * W, (1 - t.Uv[(e + 1) % 3 * 2 + 1]) * H, c);
            }
            return (img, W, H);
        }

        private static void Line(byte[] img, int w, int h, float x0, float y0, float x1, float y1, byte[] c)
        {
            int steps = (int)MathF.Ceiling(MathF.Max(MathF.Abs(x1 - x0), MathF.Abs(y1 - y0))) + 1;
            if (steps > 4 * (w + h)) return;
            for (int s = 0; s <= steps; s++)
            {
                int x = Mod((int)MathF.Floor(x0 + (x1 - x0) * s / steps), w), y = Mod((int)MathF.Floor(y0 + (y1 - y0) * s / steps), h);
                int i = (y * w + x) * 4;
                img[i] = c[0]; img[i + 1] = c[1]; img[i + 2] = c[2];
            }
        }
    }
}
