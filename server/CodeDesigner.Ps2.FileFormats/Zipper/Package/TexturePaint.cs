using CodeDesigner.Ps2.FileFormats.Assets;

namespace CodeDesigner.Ps2.FileFormats.Zipper.Package
{
    /// <summary>
    /// Where a model draws a texture, and painting by those places: the model's triangles that use the texture, in
    /// texture space, each labelled by its body part (a character's heaviest bone, else the visual). The renderer
    /// samples rows top-down with V as given (flipY off), so a UV lands on pixel (u·W, v·H), wrapping.
    /// </summary>
    public static class TexturePaint
    {
        public sealed record Triangle(string Label, float[] Uv);   // 6 floats: u0 v0 u1 v1 u2 v2

        public sealed record Region(IReadOnlySet<string> Labels, byte[] Color, bool Fill = false, float Strength = 1);

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
                float[] x = { t.Uv[0] * w, t.Uv[2] * w, t.Uv[4] * w }, y = { t.Uv[1] * h, t.Uv[3] * h, t.Uv[5] * h };
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
                var pixels = masks.Where(kv => r.Labels.Contains(kv.Key)).SelectMany(kv => kv.Value).ToHashSet();
                counts.Add(pixels.Count);
                if (pixels.Count == 0) continue;
                float Lum(int i) => 0.299f * rgba[i * 4] + 0.587f * rgba[i * 4 + 1] + 0.114f * rgba[i * 4 + 2];
                float mean = MathF.Max(1, pixels.Average(Lum));
                foreach (int i in pixels)
                {
                    float shade = r.Fill ? 1 : Lum(i) / mean;
                    for (int k = 0; k < 3; k++)
                    {
                        float target = Math.Clamp(r.Color[k] * shade, 0, 255);
                        rgba[i * 4 + k] = (byte)MathF.Round(rgba[i * 4 + k] + (target - rgba[i * 4 + k]) * r.Strength);
                    }
                }
            }
            return counts;
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
                    Line(img, W, H, t.Uv[e * 2] * W, t.Uv[e * 2 + 1] * H, t.Uv[(e + 1) % 3 * 2] * W, t.Uv[(e + 1) % 3 * 2 + 1] * H, c);
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
