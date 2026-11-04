namespace CodeDesigner.Ps2.FileFormats.Zipper.Package
{
    /// <summary>
    /// A lower-detail copy of a mesh: edges collapsed cheapest first by quadric error (Garland–Heckbert) until the
    /// triangle count falls to <c>ratio</c> of what it was. Vertices are welded by position for the topology, so uv and
    /// normal seams collapse together and stay closed; each original vertex keeps its own uv, normal and colour and
    /// takes its welded group's final position. Open rims are guarded (their quadrics carry a plane square to the rim),
    /// and a collapse that would flip a triangle is refused, so silhouettes and gear edges hold.
    /// </summary>
    public static class MeshDecimate
    {
        public static MeshTopology.InputMesh Simplify(MeshTopology.InputMesh m, float ratio)
        {
            ratio = Math.Clamp(ratio, 0.05f, 1f);
            int target = Math.Max(4, (int)MathF.Round(m.Triangles.Count * ratio));
            if (target >= m.Triangles.Count) return m;

            // Welded vertices.
            var ids = new Dictionary<(int, int, int), int>();
            var weld = new int[m.Positions.Count];
            var pos = new List<double[]>();
            for (int i = 0; i < weld.Length; i++)
            {
                var p = m.Positions[i];
                var key = ((int)MathF.Round(p[0] * 1e4f), (int)MathF.Round(p[1] * 1e4f), (int)MathF.Round(p[2] * 1e4f));
                if (!ids.TryGetValue(key, out weld[i])) { ids[key] = weld[i] = pos.Count; pos.Add(new double[] { p[0], p[1], p[2] }); }
            }
            int n = pos.Count;
            var tris = m.Triangles.Select(t => new[] { weld[t.A], weld[t.B], weld[t.C] }).ToList();
            var alive = tris.Select(t => t[0] != t[1] && t[1] != t[2] && t[0] != t[2]).ToArray();
            var faces = Enumerable.Range(0, n).Select(_ => new HashSet<int>()).ToArray();
            for (int t = 0; t < tris.Count; t++) if (alive[t]) foreach (int v in tris[t]) faces[v].Add(t);

            // Quadrics: each face's plane; open edges add a heavy plane square to the face through the edge.
            var q = Enumerable.Range(0, n).Select(_ => new double[10]).ToArray();
            var edgeUse = new Dictionary<(int, int), int>();
            for (int t = 0; t < tris.Count; t++)
            {
                if (!alive[t]) continue;
                var (nrm, d) = Plane(pos, tris[t]);
                if (nrm == null) continue;
                foreach (int v in tris[t]) AddPlane(q[v], nrm, d, 1);
                for (int e = 0; e < 3; e++) { var k = Key(tris[t][e], tris[t][(e + 1) % 3]); edgeUse[k] = edgeUse.GetValueOrDefault(k) + 1; }
            }
            for (int t = 0; t < tris.Count; t++)
            {
                if (!alive[t]) continue;
                var (fn, _) = Plane(pos, tris[t]);
                if (fn == null) continue;
                for (int e = 0; e < 3; e++)
                {
                    int a = tris[t][e], b = tris[t][(e + 1) % 3];
                    if (edgeUse[Key(a, b)] != 1) continue;
                    double[] ab = Sub(pos[b], pos[a]);
                    var side = Normalize(Cross(ab, fn));
                    if (side == null) continue;
                    double sd = -Dot(side, pos[a]);
                    AddPlane(q[a], side, sd, 1000);
                    AddPlane(q[b], side, sd, 1000);
                }
            }

            var parent = Enumerable.Range(0, n).ToArray();
            int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
            var heap = new PriorityQueue<(int A, int B, int Version), double>();
            var version = new int[n];
            void Push(int a, int b)
            {
                var (cost, _) = Cost(q, pos, a, b);
                heap.Enqueue((a, b, version[a] + version[b]), cost);
            }
            foreach (var k in edgeUse.Keys) Push(k.Item1, k.Item2);

            int live = alive.Count(x => x);
            while (live > target && heap.TryDequeue(out var e, out _))
            {
                int a = Find(e.A), b = Find(e.B);
                if (a == b || a != e.A || b != e.B || version[a] + version[b] != e.Version) continue;
                var (_, at) = Cost(q, pos, a, b);
                // Refuse a collapse that would flip one of the surviving faces round a or b.
                bool flips = false;
                foreach (int v in new[] { a, b })
                    foreach (int t in faces[v])
                    {
                        if (!alive[t]) continue;
                        var tri = tris[t];
                        if (tri.Contains(a) && tri.Contains(b)) continue;
                        var (before, _) = Plane(pos, tri);
                        var moved = tri.Select(x => x == a || x == b ? at : pos[x]).ToArray();
                        var after = Normalize(Cross(Sub(moved[1], moved[0]), Sub(moved[2], moved[0])));
                        if (before != null && (after == null || Dot(before, after) < 0.2)) { flips = true; break; }
                    }
                if (flips) continue;

                pos[a] = at;
                parent[b] = a;
                for (int k = 0; k < 10; k++) q[a][k] += q[b][k];
                foreach (int t in faces[b])
                {
                    if (!alive[t]) continue;
                    for (int k = 0; k < 3; k++) if (tris[t][k] == b) tris[t][k] = a;
                    if (tris[t][0] == tris[t][1] || tris[t][1] == tris[t][2] || tris[t][0] == tris[t][2]) { alive[t] = false; live--; }
                    else faces[a].Add(t);
                }
                faces[b].Clear();
                version[a]++;
                var near = new HashSet<int>();
                foreach (int t in faces[a]) if (alive[t]) foreach (int v in tris[t]) if (v != a) near.Add(v);
                foreach (int v in near) Push(Math.Min(a, v), Math.Max(a, v));
            }

            // Back to attribute vertices: each keeps its uv / normal / colour at its group's final place.
            var o = new MeshTopology.InputMesh();
            var remap = new Dictionary<int, int>();
            bool colours = m.Colors.Count == m.Positions.Count, normals = m.Normals.Count == m.Positions.Count, uvs = m.Uvs.Count == m.Positions.Count;
            int V(int i)
            {
                if (remap.TryGetValue(i, out int r)) return r;
                var p = pos[Find(weld[i])];
                o.Positions.Add(new[] { (float)p[0], (float)p[1], (float)p[2] });
                if (normals) o.Normals.Add(m.Normals[i]);
                if (uvs) o.Uvs.Add(m.Uvs[i]);
                if (colours) o.Colors.Add(m.Colors[i]);
                return remap[i] = o.Positions.Count - 1;
            }
            foreach (var (a, b, c) in m.Triangles)
            {
                int wa = Find(weld[a]), wb = Find(weld[b]), wc = Find(weld[c]);
                if (wa == wb || wb == wc || wa == wc) continue;
                o.Triangles.Add((V(a), V(b), V(c)));
            }
            return o;
        }

        private static (int, int) Key(int a, int b) => a < b ? (a, b) : (b, a);

        // The cheapest place for the merged vertex: the quadric's minimum when it is well defined, else the better of
        // the two ends and their midpoint.
        private static (double Cost, double[] At) Cost(double[][] q, List<double[]> pos, int a, int b)
        {
            var s = new double[10];
            for (int k = 0; k < 10; k++) s[k] = q[a][k] + q[b][k];
            var best = (Cost: double.MaxValue, At: pos[a]);
            var mid = new[] { (pos[a][0] + pos[b][0]) / 2, (pos[a][1] + pos[b][1]) / 2, (pos[a][2] + pos[b][2]) / 2 };
            var candidates = new List<double[]> { pos[a], pos[b], mid };
            if (Solve(s) is { } opt && Dist(opt, mid) < 4 * Dist(pos[a], pos[b]) + 1e-9) candidates.Add(opt);
            foreach (var c in candidates)
            {
                double e = Error(s, c);
                if (e < best.Cost) best = (e, c);
            }
            return best;
        }

        // Symmetric 4x4 quadric as 10 numbers: a2 ab ac ad b2 bc bd c2 cd d2.
        private static void AddPlane(double[] q, double[] n, double d, double w)
        {
            double a = n[0], b = n[1], c = n[2];
            q[0] += w * a * a; q[1] += w * a * b; q[2] += w * a * c; q[3] += w * a * d;
            q[4] += w * b * b; q[5] += w * b * c; q[6] += w * b * d;
            q[7] += w * c * c; q[8] += w * c * d; q[9] += w * d * d;
        }

        private static double Error(double[] q, double[] v)
        {
            double x = v[0], y = v[1], z = v[2];
            return q[0] * x * x + 2 * q[1] * x * y + 2 * q[2] * x * z + 2 * q[3] * x + q[4] * y * y + 2 * q[5] * y * z + 2 * q[6] * y
                + q[7] * z * z + 2 * q[8] * z + q[9];
        }

        private static double[]? Solve(double[] q)
        {
            double a = q[0], b = q[1], c = q[2], d = q[4], e = q[5], f = q[7];
            double det = a * (d * f - e * e) - b * (b * f - e * c) + c * (b * e - d * c);
            if (Math.Abs(det) < 1e-10) return null;
            double r0 = -q[3], r1 = -q[6], r2 = -q[8];
            double x = (r0 * (d * f - e * e) - b * (r1 * f - e * r2) + c * (r1 * e - d * r2)) / det;
            double y = (a * (r1 * f - e * r2) - r0 * (b * f - e * c) + c * (b * r2 - r1 * c)) / det;
            double z = (a * (d * r2 - r1 * e) - b * (b * r2 - r1 * c) + r0 * (b * e - d * c)) / det;
            return new[] { x, y, z };
        }

        private static (double[]? N, double D) Plane(List<double[]> pos, int[] t)
        {
            var n = Normalize(Cross(Sub(pos[t[1]], pos[t[0]]), Sub(pos[t[2]], pos[t[0]])));
            return n == null ? (null, 0) : (n, -Dot(n, pos[t[0]]));
        }

        private static double[] Sub(double[] a, double[] b) => new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };
        private static double[] Cross(double[] a, double[] b) => new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };
        private static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
        private static double Dist(double[] a, double[] b) => Math.Sqrt(Dot(Sub(a, b), Sub(a, b)));

        private static double[]? Normalize(double[] v)
        {
            double l = Math.Sqrt(Dot(v, v));
            return l > 1e-12 ? new[] { v[0] / l, v[1] / l, v[2] / l } : null;
        }
    }
}
