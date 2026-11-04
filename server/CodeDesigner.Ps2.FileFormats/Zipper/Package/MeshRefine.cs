namespace CodeDesigner.Ps2.FileFormats.Zipper.Package
{
    /// <summary>
    /// Softens a mesh: Loop subdivision that keeps creases, and noise displacement along the normals (wrinkles, sag).
    /// Vertices are welded by position for the topology, so uv and normal seams neither crack nor stop the smoothing:
    /// copies of a vertex get the same new position and keep their own uvs. An edge is a crease where its faces meet at
    /// more than <c>crease</c> degrees, or it has other than 2 faces (an open rim); crease edges stay sharp, and a vertex
    /// on more than 2 of them (a box corner) stays put. Normals are recomputed from the faces round each vertex that
    /// lie within the crease angle of its own, so hard edges stay hard.
    /// </summary>
    public static class MeshRefine
    {
        public sealed record Noise(float Amount, float Scale, int Seed);

        /// <param name="reshade">Gives a vertex its uv again from its new normal (the material strip shades by normal);
        /// null keeps the interpolated uvs.</param>
        public static MeshTopology.InputMesh Refine(MeshTopology.InputMesh m, int subdivide, float crease, Noise? noise,
            Func<float[], float[], float[]>? reshade = null)
        {
            float cosCrease = MathF.Cos(crease * MathF.PI / 180);
            for (int level = 0; level < subdivide; level++) m = Subdivide(m, cosCrease);
            if (noise is { Amount: not 0 } nz) Displace(m, nz, cosCrease);
            Renormal(m, cosCrease);
            if (reshade != null)
                for (int i = 0; i < m.Positions.Count; i++) m.Uvs[i] = reshade(m.Normals[i], m.Uvs[i]);
            return m;
        }

        // Welded ids: one per distinct position (to 1e-4).
        private static int[] Weld(MeshTopology.InputMesh m, out int count)
        {
            var ids = new Dictionary<(int, int, int), int>();
            var w = new int[m.Positions.Count];
            for (int i = 0; i < w.Length; i++)
            {
                var p = m.Positions[i];
                var key = ((int)MathF.Round(p[0] * 1e4f), (int)MathF.Round(p[1] * 1e4f), (int)MathF.Round(p[2] * 1e4f));
                if (!ids.TryGetValue(key, out w[i])) ids[key] = w[i] = ids.Count;
            }
            count = ids.Count;
            return w;
        }

        private static (int, int) Edge(int a, int b) => a < b ? (a, b) : (b, a);

        private static float[] FaceNormal(MeshTopology.InputMesh m, (int A, int B, int C) t)
        {
            float[] a = m.Positions[t.A], e1 = Sub(m.Positions[t.B], a), e2 = Sub(m.Positions[t.C], a);
            return new[] { e1[1] * e2[2] - e1[2] * e2[1], e1[2] * e2[0] - e1[0] * e2[2], e1[0] * e2[1] - e1[1] * e2[0] };
        }

        private static MeshTopology.InputMesh Subdivide(MeshTopology.InputMesh m, float cosCrease)
        {
            int[] w = Weld(m, out int nw);
            var wpos = new float[nw][];
            for (int i = 0; i < w.Length; i++) wpos[w[i]] ??= m.Positions[i];

            // Welded edges: their faces' opposite welded vertices and normals.
            var faces = new Dictionary<(int, int), List<(int Opp, float[] N)>>();
            foreach (var t in m.Triangles)
            {
                float[] fn = Normalise(FaceNormal(m, t));
                int a = w[t.A], b = w[t.B], c = w[t.C];
                foreach (var (x, y, opp) in new[] { (a, b, c), (b, c, a), (c, a, b) })
                {
                    var e = Edge(x, y);
                    if (!faces.TryGetValue(e, out var l)) faces[e] = l = new();
                    l.Add((opp, fn));
                }
            }
            bool Crease(List<(int Opp, float[] N)> l) => l.Count != 2 || Dot(l[0].N, l[1].N) < cosCrease;

            // Each welded vertex's new place: Loop's rule inside, the crease curve's on a crease line, fixed at a corner.
            var ring = new HashSet<int>[nw];
            var sharp = new List<int>[nw];
            for (int i = 0; i < nw; i++) { ring[i] = new(); sharp[i] = new(); }
            foreach (var (e, l) in faces)
            {
                ring[e.Item1].Add(e.Item2); ring[e.Item2].Add(e.Item1);
                if (Crease(l)) { sharp[e.Item1].Add(e.Item2); sharp[e.Item2].Add(e.Item1); }
            }
            var moved = new float[nw][];
            for (int v = 0; v < nw; v++)
            {
                float[] p = wpos[v], q;
                if (sharp[v].Count == 2) q = Mix(p, 0.75f, Add(wpos[sharp[v][0]], wpos[sharp[v][1]]), 0.125f);
                else if (sharp[v].Count > 0) q = p;
                else
                {
                    int n = ring[v].Count;
                    float beta = n == 3 ? 3f / 16 : 3f / (8 * n);
                    float[] sum = new float[3];
                    foreach (int u in ring[v]) sum = Add(sum, wpos[u]);
                    q = Mix(p, 1 - n * beta, sum, beta);
                }
                moved[v] = q;
            }

            var o = new MeshTopology.InputMesh();
            bool colours = m.Colors.Count == m.Positions.Count;
            for (int i = 0; i < m.Positions.Count; i++)
            {
                o.Positions.Add(moved[w[i]]);
                o.Normals.Add(m.Normals[i]);
                o.Uvs.Add(m.Uvs[i]);
                if (colours) o.Colors.Add(m.Colors[i]);
            }
            // Edge points, one per attribute edge (seam copies share the welded position).
            var mid = new Dictionary<(int, int), int>();
            int Mid(int a, int b)
            {
                var key = Edge(a, b);
                if (mid.TryGetValue(key, out int id)) return id;
                int wa = w[a], wb = w[b];
                var l = faces[Edge(wa, wb)];
                float[] p = Crease(l)
                    ? Mix(wpos[wa], 0.5f, wpos[wb], 0.5f)
                    : Add(Mix(wpos[wa], 0.375f, wpos[wb], 0.375f), Mix(wpos[l[0].Opp], 0.125f, wpos[l[1].Opp], 0.125f));
                o.Positions.Add(p);
                o.Normals.Add(Normalise(Add(m.Normals[a], m.Normals[b])));
                o.Uvs.Add(new[] { (m.Uvs[a][0] + m.Uvs[b][0]) / 2, (m.Uvs[a][1] + m.Uvs[b][1]) / 2 });
                if (colours) o.Colors.Add(m.Colors[a].Zip(m.Colors[b], (x, y) => (byte)((x + y + 1) / 2)).ToArray());
                return mid[key] = o.Positions.Count - 1;
            }
            foreach (var (a, b, c) in m.Triangles)
            {
                int ab = Mid(a, b), bc = Mid(b, c), ca = Mid(c, a);
                o.Triangles.Add((a, ab, ca));
                o.Triangles.Add((ab, b, bc));
                o.Triangles.Add((ca, bc, c));
                o.Triangles.Add((ab, bc, ca));
            }
            return o;
        }

        // Each welded vertex moves along its smooth normal by amount × noise(p / scale), so seams move together.
        private static void Displace(MeshTopology.InputMesh m, Noise nz, float cosCrease)
        {
            Renormal(m, cosCrease);
            int[] w = Weld(m, out int nw);
            var dir = new float[nw][];
            for (int i = 0; i < w.Length; i++) dir[w[i]] = dir[w[i]] == null ? m.Normals[i] : Add(dir[w[i]], m.Normals[i]);
            var done = new float[nw][];
            float s = Math.Max(1e-3f, nz.Scale);
            for (int i = 0; i < w.Length; i++)
            {
                int v = w[i];
                if (done[v] == null)
                {
                    float[] p = m.Positions[i], n = Normalise(dir[v]);
                    // Two octaves of value noise, −1..1.
                    float h = 0.7f * Value(p[0] / s, p[1] / s, p[2] / s, nz.Seed) + 0.3f * Value(p[0] * 2.3f / s, p[1] * 2.3f / s, p[2] * 2.3f / s, nz.Seed + 7919);
                    done[v] = Add(p, Scale(n, nz.Amount * h));
                }
                m.Positions[i] = done[v];
            }
        }

        // Smooth value noise on the integer lattice, −1..1.
        private static float Value(float x, float y, float z, int seed)
        {
            int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y), z0 = (int)MathF.Floor(z);
            float fx = Fade(x - x0), fy = Fade(y - y0), fz = Fade(z - z0);
            float L(int i, int j, int k) => Hash(x0 + i, y0 + j, z0 + k, seed);
            float Lerp(float a, float b, float t) => a + (b - a) * t;
            float x00 = Lerp(L(0, 0, 0), L(1, 0, 0), fx), x10 = Lerp(L(0, 1, 0), L(1, 1, 0), fx);
            float x01 = Lerp(L(0, 0, 1), L(1, 0, 1), fx), x11 = Lerp(L(0, 1, 1), L(1, 1, 1), fx);
            return Lerp(Lerp(x00, x10, fy), Lerp(x01, x11, fy), fz);
        }

        private static float Fade(float t) => t * t * (3 - 2 * t);

        private static float Hash(int x, int y, int z, int seed)
        {
            unchecked
            {
                uint h = (uint)(x * 73856093 ^ y * 19349663 ^ z * 83492791 ^ seed * 2654435761u);
                h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
                return (h & 0xFFFFFF) / (float)0x7FFFFF - 1;
            }
        }

        // A vertex's normal: the area-weighted faces round its position that lie within the crease angle of the faces
        // that use it directly.
        private static void Renormal(MeshTopology.InputMesh m, float cosCrease)
        {
            int[] w = Weld(m, out int nw);
            var own = new float[m.Positions.Count][];
            var round = new List<float[]>[nw];
            for (int i = 0; i < nw; i++) round[i] = new();
            foreach (var t in m.Triangles)
            {
                float[] f = FaceNormal(m, t);
                foreach (int i in new[] { t.A, t.B, t.C })
                {
                    own[i] = own[i] == null ? f : Add(own[i], f);
                    round[w[i]].Add(f);
                }
            }
            for (int i = 0; i < m.Positions.Count; i++)
            {
                if (own[i] == null) continue;
                float[] seed = Normalise(own[i]), sum = new float[3];
                foreach (var f in round[w[i]])
                    if (Dot(Normalise(f), seed) >= cosCrease) sum = Add(sum, f);
                m.Normals[i] = Normalise(Length(sum) > 1e-12f ? sum : seed);
            }
        }

        private static float Dot(float[] a, float[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
        private static float Length(float[] v) => MathF.Sqrt(Dot(v, v));
        private static float[] Normalise(float[] v) { float l = Length(v); return l > 1e-12f ? new[] { v[0] / l, v[1] / l, v[2] / l } : new[] { 0f, 1, 0 }; }
        private static float[] Sub(float[] a, float[] b) => new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };
        private static float[] Add(float[] a, float[] b) => new[] { a[0] + b[0], a[1] + b[1], a[2] + b[2] };
        private static float[] Scale(float[] a, float k) => new[] { a[0] * k, a[1] * k, a[2] * k };
        private static float[] Mix(float[] a, float ka, float[] b, float kb) => new[] { a[0] * ka + b[0] * kb, a[1] * ka + b[1] * kb, a[2] * ka + b[2] * kb };
    }
}
