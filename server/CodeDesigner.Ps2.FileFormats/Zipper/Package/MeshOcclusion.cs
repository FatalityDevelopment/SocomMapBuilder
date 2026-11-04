using CodeDesigner.Ps2.FileFormats.Assets;

namespace CodeDesigner.Ps2.FileFormats.Zipper.Package
{
    /// <summary>
    /// Ambient occlusion baked into a static model's vertex colours. The game has no real lighting, so baked shading
    /// is what seats a prop: creases, the underside of a lid, the inside of a crate darken. Each vertex casts
    /// <see cref="Options.Rays"/> rays over the hemisphere round its normal (cosine-weighted, the same directions for
    /// every vertex so a bake is repeatable) against the model's own triangles, out to <see cref="Options.Distance"/>;
    /// the fraction that hits scales its colour by 1 − strength × fraction. Retail colours are kept and multiplied, so
    /// a second bake darkens further: bake once, after the geometry is final.
    /// </summary>
    public static class MeshOcclusion
    {
        public sealed record Options(int Rays = 32, float Distance = 0, float Strength = 0.6f, IReadOnlySet<int>? Chunks = null);

        public sealed record Baked(Dictionary<int, IReadOnlyList<ZipperModelMesh.VertexEdit>> Edits, int Vertices, float MeanOcclusion, float Distance);

        public static Baked Bake(ModelMesh mesh, Options options)
        {
            // Every triangle in model space (all chunks occlude, whichever are baked).
            var tris = new List<(float[] A, float[] B, float[] C)>();
            var min = new[] { float.MaxValue, float.MaxValue, float.MaxValue };
            var max = new[] { float.MinValue, float.MinValue, float.MinValue };
            foreach (var c in mesh.Chunks)
            {
                float[] P(int i) => new[] { c.Positions[i * 3], c.Positions[i * 3 + 1], c.Positions[i * 3 + 2] };
                for (int t = 0; t + 2 < c.Triangles.Length; t += 3)
                {
                    var tri = (P(c.Triangles[t]), P(c.Triangles[t + 1]), P(c.Triangles[t + 2]));
                    tris.Add(tri);
                    foreach (var p in new[] { tri.Item1, tri.Item2, tri.Item3 })
                        for (int k = 0; k < 3; k++) { min[k] = MathF.Min(min[k], p[k]); max[k] = MathF.Max(max[k], p[k]); }
                }
            }
            if (tris.Count == 0) throw new ArgumentException($"{mesh.Name} has no triangles");
            float extent = MathF.Sqrt(Enumerable.Range(0, 3).Sum(k => (max[k] - min[k]) * (max[k] - min[k])));
            float reach = options.Distance > 0 ? options.Distance : extent * 0.3f;
            var bvh = new Bvh(tris);
            var dirs = Hemisphere(Math.Clamp(options.Rays, 4, 256));
            float strength = Math.Clamp(options.Strength, 0, 1);
            float eps = MathF.Max(1e-4f, extent * 1e-4f);

            var edits = new Dictionary<int, IReadOnlyList<ZipperModelMesh.VertexEdit>>();
            int count = 0;
            double sum = 0;
            foreach (var c in mesh.Chunks)
            {
                if (c.Colors == null || (options.Chunks != null && !options.Chunks.Contains(c.GeometryOffset))) continue;
                var list = new List<ZipperModelMesh.VertexEdit>();
                for (int i = 0; i < c.VertexCount; i++)
                {
                    float[] p = { c.Positions[i * 3], c.Positions[i * 3 + 1], c.Positions[i * 3 + 2] };
                    // Chunk normals are local: through the placement into model space.
                    float[] m = c.Matrix;
                    float nx = c.Normals[i * 3], ny = c.Normals[i * 3 + 1], nz = c.Normals[i * 3 + 2];
                    float[] n = Normalize(new[] { nx * m[0] + ny * m[4] + nz * m[8], nx * m[1] + ny * m[5] + nz * m[9], nx * m[2] + ny * m[6] + nz * m[10] });
                    var (t1, t2) = Basis(n);
                    float[] o = { p[0] + n[0] * eps * 4, p[1] + n[1] * eps * 4, p[2] + n[2] * eps * 4 };
                    int hits = 0;
                    foreach (var d in dirs)
                    {
                        float[] w = { t1[0] * d[0] + t2[0] * d[1] + n[0] * d[2], t1[1] * d[0] + t2[1] * d[1] + n[1] * d[2], t1[2] * d[0] + t2[2] * d[1] + n[2] * d[2] };
                        if (bvh.Hits(o, w, reach, eps)) hits++;
                    }
                    float occ = hits / (float)dirs.Count;
                    sum += occ;
                    count++;
                    float f = 1 - strength * occ;
                    var col = c.Colors[(i * 4)..(i * 4 + 4)];
                    for (int k = 0; k < 3; k++) col[k] = (byte)Math.Clamp((int)MathF.Round(col[k] * f), 0, 255);
                    list.Add(new ZipperModelMesh.VertexEdit(i, Color: col));
                }
                if (list.Count > 0) edits[c.GeometryOffset] = list;
            }
            if (count == 0) throw new ArgumentException($"{mesh.Name} has no baked vertex colours to darken (characters and models without a colour block cannot take a bake)");
            return new Baked(edits, count, (float)(sum / count), reach);
        }

        // Cosine-weighted directions over +Z (a Fibonacci spiral: even, and the same every bake).
        private static List<float[]> Hemisphere(int n)
        {
            var list = new List<float[]>(n);
            float golden = MathF.PI * (3 - MathF.Sqrt(5));
            for (int i = 0; i < n; i++)
            {
                float u = (i + 0.5f) / n, r = MathF.Sqrt(u), a = golden * i;
                list.Add(new[] { r * MathF.Cos(a), r * MathF.Sin(a), MathF.Sqrt(1 - u) });
            }
            return list;
        }

        private static (float[], float[]) Basis(float[] n)
        {
            float[] a = MathF.Abs(n[0]) < 0.9f ? new[] { 1f, 0, 0 } : new[] { 0f, 1, 0 };
            float[] t1 = Normalize(Cross(a, n));
            return (t1, Cross(n, t1));
        }

        private static float[] Cross(float[] a, float[] b) => new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };

        private static float[] Normalize(float[] v)
        {
            float l = MathF.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
            return l > 1e-12f ? new[] { v[0] / l, v[1] / l, v[2] / l } : new[] { 0f, 1, 0 };
        }

        /// <summary>A bounding-volume tree over triangles (median splits), answering whether a ray segment hits any.</summary>
        private sealed class Bvh
        {
            private readonly List<(float[] A, float[] B, float[] C)> _tris;
            private readonly List<(float[] Min, float[] Max, int Left, int Right, int Start, int Count)> _nodes = new();
            private readonly int[] _order;

            public Bvh(List<(float[] A, float[] B, float[] C)> tris)
            {
                _tris = tris;
                _order = Enumerable.Range(0, tris.Count).ToArray();
                Build(0, tris.Count);
            }

            private float Centre(int t, int axis) => (_tris[t].A[axis] + _tris[t].B[axis] + _tris[t].C[axis]) / 3;

            private int Build(int start, int count)
            {
                var min = new[] { float.MaxValue, float.MaxValue, float.MaxValue };
                var max = new[] { float.MinValue, float.MinValue, float.MinValue };
                for (int i = start; i < start + count; i++)
                    foreach (var p in new[] { _tris[_order[i]].A, _tris[_order[i]].B, _tris[_order[i]].C })
                        for (int k = 0; k < 3; k++) { min[k] = MathF.Min(min[k], p[k]); max[k] = MathF.Max(max[k], p[k]); }
                int index = _nodes.Count;
                _nodes.Add((min, max, -1, -1, start, count));
                if (count <= 4) return index;
                int axis = 0;
                for (int k = 1; k < 3; k++) if (max[k] - min[k] > max[axis] - min[axis]) axis = k;
                Array.Sort(_order, start, count, Comparer<int>.Create((x, y) => Centre(x, axis).CompareTo(Centre(y, axis))));
                int half = count / 2;
                int left = Build(start, half), right = Build(start + half, count - half);
                _nodes[index] = (min, max, left, right, start, 0);
                return index;
            }

            public bool Hits(float[] o, float[] d, float far, float near)
            {
                var stack = new Stack<int>();
                stack.Push(0);
                while (stack.Count > 0)
                {
                    var n = _nodes[stack.Pop()];
                    if (!Box(o, d, n.Min, n.Max, far)) continue;
                    if (n.Left < 0)
                    {
                        for (int i = n.Start; i < n.Start + n.Count; i++)
                        {
                            float t = Ray(o, d, _tris[_order[i]]);
                            if (t > near && t < far) return true;
                        }
                        continue;
                    }
                    stack.Push(n.Left);
                    stack.Push(n.Right);
                }
                return false;
            }

            private static bool Box(float[] o, float[] d, float[] min, float[] max, float far)
            {
                float lo = 0, hi = far;
                for (int k = 0; k < 3; k++)
                {
                    if (MathF.Abs(d[k]) < 1e-12f) { if (o[k] < min[k] || o[k] > max[k]) return false; continue; }
                    float a = (min[k] - o[k]) / d[k], b = (max[k] - o[k]) / d[k];
                    if (a > b) (a, b) = (b, a);
                    lo = MathF.Max(lo, a); hi = MathF.Min(hi, b);
                    if (lo > hi) return false;
                }
                return true;
            }

            // Möller–Trumbore, both sides; the distance along d, or -1.
            private static float Ray(float[] o, float[] d, (float[] A, float[] B, float[] C) t)
            {
                float[] e1 = { t.B[0] - t.A[0], t.B[1] - t.A[1], t.B[2] - t.A[2] }, e2 = { t.C[0] - t.A[0], t.C[1] - t.A[1], t.C[2] - t.A[2] };
                float[] p = Cross(d, e2);
                float det = e1[0] * p[0] + e1[1] * p[1] + e1[2] * p[2];
                if (MathF.Abs(det) < 1e-12f) return -1;
                float inv = 1 / det;
                float[] s = { o[0] - t.A[0], o[1] - t.A[1], o[2] - t.A[2] };
                float u = (s[0] * p[0] + s[1] * p[1] + s[2] * p[2]) * inv;
                if (u < 0 || u > 1) return -1;
                float[] q = Cross(s, e1);
                float v = (d[0] * q[0] + d[1] * q[1] + d[2] * q[2]) * inv;
                if (v < 0 || u + v > 1) return -1;
                return (e2[0] * q[0] + e2[1] * q[1] + e2[2] * q[2]) * inv;
            }
        }
    }
}
