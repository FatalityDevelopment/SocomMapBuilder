using System.Text.Json;

namespace CodeDesigner.Ps2.FileFormats.Zipper.Package
{
    /// <summary>
    /// Meshes from a list of shapes (lathe, cylinder, sphere, box, ring, strap), each with a material index. Materials map
    /// to a material-strip skin (<see cref="SkinRgba"/>): a 64×64 texture whose column strip 8m..8m+7 is material m and
    /// whose row is a shade from dark (top) to light (bottom). Every vertex picks its shade from its normal against a
    /// light, so the model reads shaded with or without lighting. Up to 8 materials. Axes: a shape's axis is "x", "y" or
    /// "z" (default "x": weapons point +x, +y up).
    /// A shape with <c>uvRect</c> [u0, v0, u1, v1] maps into that rectangle of a painted texture instead (an atlas of
    /// webbing, stitched nylon, buckles): a box face, a strap's length or a lathe's unrolled side spans the rectangle,
    /// its top at v1 (the renderer draws v up: pixel row (1 − v)·H).
    /// </summary>
    public static class MeshShapes
    {
        public const int MaxMaterials = 8;
        private const int Size = 64, StripWidth = 8, Shades = 32;
        public static readonly float[] DefaultLight = { 0.35f, 0.85f, 0.45f };

        /// <summary>Builds the shapes into a mesh with uvs into the material-strip skin.</summary>
        public static MeshTopology.InputMesh Build(JsonElement shapes, float[]? light = null)
        {
            if (shapes.ValueKind != JsonValueKind.Array || shapes.GetArrayLength() == 0) throw new ArgumentException("shapes is a non-empty array");
            var b = new Builder(Normalise(light ?? DefaultLight));
            int index = 0;
            foreach (var s in shapes.EnumerateArray())
            {
                string kind = Str(s, "kind") ?? throw new ArgumentException($"shapes[{index}] needs a kind (lathe, cylinder, sphere, box, ring, strap)");
                int mat = Int(s, "material") ?? 0;
                if (mat < 0 || mat >= MaxMaterials) throw new ArgumentException($"shapes[{index}].material is 0..{MaxMaterials - 1}");
                int seg = Math.Clamp(Int(s, "segments") ?? 16, 3, 64);
                var axis = Axis(Str(s, "axis"));
                float[] center = Vec(s, "center", 3) ?? new float[3];
                float[] squash = Vec(s, "squash", 2) ?? new[] { 1f, 1f };
                b.Rect = Rect(s, "uvRect", index);
                switch (kind)
                {
                    case "lathe":
                    {
                        var profile = Pairs(s, "profile") ?? throw new ArgumentException($"shapes[{index}] (lathe) needs profile [[along, radius], ...]");
                        if (profile.Count < 2) throw new ArgumentException($"shapes[{index}].profile needs at least 2 points");
                        var (c0, c1) = Caps(s);
                        b.Lathe(profile, mat, axis, center, seg, squash, c0, c1);
                        break;
                    }
                    case "cylinder":
                    {
                        float start = Num(s, "start") ?? throw new ArgumentException($"shapes[{index}] (cylinder) needs start and end along its axis");
                        float end = Num(s, "end") ?? throw new ArgumentException($"shapes[{index}] (cylinder) needs end");
                        float r = Num(s, "radius") ?? throw new ArgumentException($"shapes[{index}] (cylinder) needs radius");
                        float r1 = Num(s, "radiusEnd") ?? r;
                        var (c0, c1) = Caps(s, true);
                        b.Lathe(new List<(float, float)> { (start, r), (end, r1) }, mat, axis, center, seg, squash, c0, c1);
                        break;
                    }
                    case "ring":
                    {
                        float at = Num(s, "at") ?? throw new ArgumentException($"shapes[{index}] (ring) needs at (position along its axis)");
                        float rin = Num(s, "inner") ?? throw new ArgumentException($"shapes[{index}] (ring) needs inner radius");
                        float rout = Num(s, "outer") ?? throw new ArgumentException($"shapes[{index}] (ring) needs outer radius");
                        float th = (Num(s, "thickness") ?? 0.05f) / 2;
                        b.Lathe(new List<(float, float)> { (at - th, rin), (at - th, rout), (at + th, rout), (at + th, rin), (at - th, rin) },
                            mat, axis, center, seg, squash, false, false);
                        break;
                    }
                    case "sphere":
                    {
                        float r = Num(s, "radius") ?? throw new ArgumentException($"shapes[{index}] (sphere) needs radius");
                        int rings = Math.Clamp(Int(s, "rings") ?? Math.Max(4, seg / 2), 3, 32);
                        float[] scale = Vec(s, "scale", 3) ?? new[] { 1f, 1f, 1f };
                        b.Sphere(center, r, mat, seg, rings, scale);
                        break;
                    }
                    case "box":
                    {
                        float[] lo = Vec(s, "min", 3) ?? throw new ArgumentException($"shapes[{index}] (box) needs min [x, y, z]");
                        float[] hi = Vec(s, "max", 3) ?? throw new ArgumentException($"shapes[{index}] (box) needs max [x, y, z]");
                        float lean = Num(s, "lean") ?? 0, radius = Math.Max(0, Num(s, "radius") ?? 0), puff = Num(s, "puff") ?? 0;
                        var faceUv = FaceRects(s, index);
                        if (radius <= 0 && puff == 0 && b.Rect == null && faceUv.Count == 0) b.Box(lo, hi, mat, lean);
                        else b.RoundedBox(lo, hi, mat, lean, radius, Math.Clamp(Int(s, "bevelSegments") ?? 1, 1, 4),
                            Math.Clamp(Int(s, "divisions") ?? (puff != 0 ? 2 : 1), 1, 8), puff, faceUv);
                        break;
                    }
                    case "strap":
                    {
                        var path = Points(s, "path") ?? throw new ArgumentException($"shapes[{index}] (strap) needs path [[x, y, z], ...]");
                        if (path.Count < 2) throw new ArgumentException($"shapes[{index}].path needs at least 2 points");
                        float width = Num(s, "width") ?? throw new ArgumentException($"shapes[{index}] (strap) needs width");
                        float thick = Num(s, "thickness") ?? 0.05f;
                        float[]? around = Vec(s, "around", 3), outward = Vec(s, "outward", 3);
                        if (around == null && outward == null)
                            throw new ArgumentException($"shapes[{index}] (strap) needs around [x, y, z] (a point it wraps round: a torso's centre) or outward [x, y, z] (the way its outer face looks)");
                        b.Strap(Smooth(path, Math.Clamp(Int(s, "steps") ?? 1, 1, 8)), width, thick, around, outward, mat,
                            !(s.TryGetProperty("caps", out var sc) && sc.ValueKind == JsonValueKind.False));
                        break;
                    }
                    default:
                        throw new ArgumentException($"shapes[{index}].kind '{kind}' is not lathe, cylinder, sphere, box, ring or strap");
                }
                index++;
            }
            return b.Mesh;
        }

        /// <summary>
        /// The material-strip skin as 64×64 RGBA: materials are [{color: [r,g,b]} | {light: [r,g,b], dark?: [r,g,b]}]
        /// (dark defaults to a quarter of light). Unused strips repeat the last material.
        /// </summary>
        public static byte[] SkinRgba(JsonElement materials)
        {
            if (materials.ValueKind != JsonValueKind.Array || materials.GetArrayLength() == 0) throw new ArgumentException("materials is a non-empty array");
            if (materials.GetArrayLength() > MaxMaterials) throw new ArgumentException($"At most {MaxMaterials} materials (a 256-colour palette holds {MaxMaterials} × {Shades} shades)");
            var mats = new List<(float[] Light, float[] Dark)>();
            int i = 0;
            foreach (var m in materials.EnumerateArray())
            {
                float[] light = Vec(m, "light", 3) ?? Vec(m, "color", 3) ?? throw new ArgumentException($"materials[{i}] needs color [r, g, b] (or light / dark)");
                float[] dark = Vec(m, "dark", 3) ?? light.Select(c => c * 0.25f).ToArray();
                mats.Add((light, dark));
                i++;
            }
            byte[] rgba = new byte[Size * Size * 4];
            for (int y = 0; y < Size; y++)
            {
                float t = MathF.Pow(y / 2 / (float)(Shades - 1), 0.8f);
                for (int x = 0; x < Size; x++)
                {
                    var (light, dark) = mats[Math.Min(x / StripWidth, mats.Count - 1)];
                    int o = (y * Size + x) * 4;
                    for (int k = 0; k < 3; k++) rgba[o + k] = (byte)Math.Clamp((int)MathF.Round(dark[k] + (light[k] - dark[k]) * t), 0, 255);
                    rgba[o + 3] = 255;
                }
            }
            return rgba;
        }

        public const int SkinSize = Size;

        // ------------------------------------------------------------------ building

        private sealed class Builder
        {
            private readonly float[] _light;
            public MeshTopology.InputMesh Mesh { get; } = new();
            /// <summary>The current shape's atlas rectangle [u0, v0, u1, v1], or null for the material strip.</summary>
            public float[]? Rect { get; set; }
            public Builder(float[] light) => _light = light;

            // (s, t) runs across the shape's rectangle, t down from its top; without one the shade strip of mat.
            private int V(float[] p, float[] n, int mat, float s = 0, float t = 0, float[]? rect = null)
            {
                n = Normalise(n);
                Mesh.Positions.Add(p);
                Mesh.Normals.Add(n);
                rect ??= Rect;
                if (rect != null)
                {
                    Mesh.Uvs.Add(new[] { rect[0] + (rect[2] - rect[0]) * s, rect[3] - (rect[3] - rect[1]) * t });
                    return Mesh.Positions.Count - 1;
                }
                float shade = Math.Max(0, n[0] * _light[0] + n[1] * _light[1] + n[2] * _light[2]) * 0.8f + 0.2f;
                int k = Math.Min(Shades - 1, (int)(shade * (Shades - 1)));
                Mesh.Uvs.Add(new[] { (StripWidth * mat + StripWidth / 2f) / Size, (2 * k + 1) / (float)Size });
                return Mesh.Positions.Count - 1;
            }

            // Winding from the geometry: each triangle faces the way its vertex normals point.
            private void Tri(int a, int b, int c)
            {
                if (a == b || b == c || a == c) return;
                float[] pa = Mesh.Positions[a], pb = Mesh.Positions[b], pc = Mesh.Positions[c];
                float[] e1 = Sub(pb, pa), e2 = Sub(pc, pa);
                float[] f = { e1[1] * e2[2] - e1[2] * e2[1], e1[2] * e2[0] - e1[0] * e2[2], e1[0] * e2[1] - e1[1] * e2[0] };
                if (f[0] * f[0] + f[1] * f[1] + f[2] * f[2] < 1e-14f) return;
                float[] n = Add(Add(Mesh.Normals[a], Mesh.Normals[b]), Mesh.Normals[c]);
                if (f[0] * n[0] + f[1] * n[1] + f[2] * n[2] < 0) Mesh.Triangles.Add((a, c, b));
                else Mesh.Triangles.Add((a, b, c));
            }

            public void Lathe(List<(float A, float R)> prof, int mat, (int Along, int U, int W) ax, float[] centre, int seg, float[] squash, bool cap0, bool cap1)
            {
                var rings = new List<int[]>();
                // Mapped into a rectangle, each ring closes on a copy of its first vertex (u 1, not back to 0).
                int ringLength = Rect == null ? seg : seg + 1;
                for (int i = 0; i < prof.Count; i++)
                {
                    var (a, r) = prof[i];
                    var (a0, r0) = prof[Math.Max(i - 1, 0)];
                    var (a1, r1) = prof[Math.Min(i + 1, prof.Count - 1)];
                    // Profile normal (slope along the axis, 1 outward); a step in radius at one position faces along the axis.
                    float da = a1 - a0;
                    float slope = -(r1 - r0) / (MathF.Abs(da) < 1e-6f ? 1e-6f : da);
                    var ring = new int[ringLength];
                    for (int k = 0; k < ringLength; k++)
                    {
                        float th = 2 * MathF.PI * k / seg, c = MathF.Cos(th), s = MathF.Sin(th);
                        float[] p = (float[])centre.Clone();
                        p[ax.Along] += a;
                        p[ax.U] += r * c * squash[0];
                        p[ax.W] += r * s * squash[1];
                        float[] n = new float[3];
                        n[ax.Along] = slope;
                        n[ax.U] = c / squash[0];
                        n[ax.W] = s / squash[1];
                        ring[k] = V(p, n, mat, k / (float)seg, i / (float)(prof.Count - 1));
                    }
                    rings.Add(ring);
                }
                for (int i = 0; i + 1 < rings.Count; i++)
                    for (int k = 0; k < seg; k++)
                    {
                        int k1 = (k + 1) % ringLength;
                        int a = rings[i][k], b = rings[i][k1], c = rings[i + 1][k], d = rings[i + 1][k1];
                        Tri(a, c, b);
                        Tri(b, c, d);
                    }
                if (cap0) Cap(rings[0], prof[0].A, -1, mat, ax, centre);
                if (cap1) Cap(rings[^1], prof[^1].A, 1, mat, ax, centre);
            }

            private void Cap(int[] ring, float along, int sign, int mat, (int Along, int U, int W) ax, float[] centre)
            {
                float[] n = new float[3];
                n[ax.Along] = sign;
                float[] c = (float[])centre.Clone();
                c[ax.Along] += along;
                // In a rectangle, a cap is a disc across it.
                int mid = V(c, n, mat, 0.5f, 0.5f);
                int seg = Rect == null ? ring.Length : ring.Length - 1;
                var edge = ring.Select((i, k) => V(Mesh.Positions[i], n, mat,
                    0.5f + 0.5f * MathF.Cos(2 * MathF.PI * k / seg), 0.5f + 0.5f * MathF.Sin(2 * MathF.PI * k / seg))).ToArray();
                for (int k = 0; k < edge.Length; k++) Tri(mid, edge[k], edge[(k + 1) % edge.Length]);
            }

            public void Sphere(float[] c, float r, int mat, int seg, int rings, float[] scale)
            {
                var grid = new List<int[]>();
                int rowLength = Rect == null ? seg : seg + 1;
                for (int i = 0; i <= rings; i++)
                {
                    float phi = MathF.PI * i / rings;
                    var row = new int[rowLength];
                    for (int k = 0; k < rowLength; k++)
                    {
                        float th = 2 * MathF.PI * k / seg;
                        float[] u = { -MathF.Cos(phi), MathF.Sin(phi) * MathF.Cos(th), MathF.Sin(phi) * MathF.Sin(th) };
                        float[] p = { c[0] + u[0] * r * scale[0], c[1] + u[1] * r * scale[1], c[2] + u[2] * r * scale[2] };
                        row[k] = V(p, new[] { u[0] / scale[0], u[1] / scale[1], u[2] / scale[2] }, mat, k / (float)seg, i / (float)rings);
                    }
                    grid.Add(row);
                }
                for (int i = 0; i < rings; i++)
                    for (int k = 0; k < seg; k++)
                    {
                        int k1 = (k + 1) % rowLength;
                        int a = grid[i][k], b = grid[i][k1], cc = grid[i + 1][k], d = grid[i + 1][k1];
                        Tri(a, cc, b);
                        Tri(b, cc, d);
                    }
            }

            public void Box(float[] lo, float[] hi, int mat, float lean)
            {
                // lean shifts x by lean × (y − max.y): a raked grip pivots about its top.
                float X(float x, float y) => x + lean * (y - hi[1]);
                float[] P(float x, float y, float z) => new[] { X(x, y), y, z };
                var faces = new (float[] N, float[][] Q)[]
                {
                    (new[] { 1f, -lean, 0 }, new[] { P(hi[0], lo[1], lo[2]), P(hi[0], hi[1], lo[2]), P(hi[0], hi[1], hi[2]), P(hi[0], lo[1], hi[2]) }),
                    (new[] { -1f, lean, 0 }, new[] { P(lo[0], lo[1], hi[2]), P(lo[0], hi[1], hi[2]), P(lo[0], hi[1], lo[2]), P(lo[0], lo[1], lo[2]) }),
                    (new[] { 0f, 1, 0 }, new[] { P(lo[0], hi[1], lo[2]), P(lo[0], hi[1], hi[2]), P(hi[0], hi[1], hi[2]), P(hi[0], hi[1], lo[2]) }),
                    (new[] { 0f, -1, 0 }, new[] { P(lo[0], lo[1], hi[2]), P(lo[0], lo[1], lo[2]), P(hi[0], lo[1], lo[2]), P(hi[0], lo[1], hi[2]) }),
                    (new[] { 0f, 0, 1 }, new[] { P(lo[0], lo[1], hi[2]), P(hi[0], lo[1], hi[2]), P(hi[0], hi[1], hi[2]), P(lo[0], hi[1], hi[2]) }),
                    (new[] { 0f, 0, -1 }, new[] { P(hi[0], lo[1], lo[2]), P(lo[0], lo[1], lo[2]), P(lo[0], hi[1], lo[2]), P(hi[0], hi[1], lo[2]) }),
                };
                foreach (var (n, q) in faces)
                {
                    int[] ids = q.Select(p => V(p, n, mat)).ToArray();
                    Tri(ids[0], ids[1], ids[2]);
                    Tri(ids[0], ids[2], ids[3]);
                }
            }

            // A box face: its normal, the axis its rectangle's s runs along and the one t runs down, and its faceUv key.
            private static readonly (int[] N, int[] S, int[] T, string Key)[] BoxFaces =
            {
                (new[] { 0, 0, -1 }, new[] { 1, 0, 0 }, new[] { 0, -1, 0 }, "-z"),
                (new[] { 0, 0, 1 }, new[] { -1, 0, 0 }, new[] { 0, -1, 0 }, "+z"),
                (new[] { 1, 0, 0 }, new[] { 0, 0, 1 }, new[] { 0, -1, 0 }, "+x"),
                (new[] { -1, 0, 0 }, new[] { 0, 0, -1 }, new[] { 0, -1, 0 }, "-x"),
                (new[] { 0, 1, 0 }, new[] { 1, 0, 0 }, new[] { 0, 0, 1 }, "+y"),
                (new[] { 0, -1, 0 }, new[] { 1, 0, 0 }, new[] { 0, 0, -1 }, "-y"),
            };

            /// <summary>
            /// A box with rounded edges and corners of <paramref name="radius"/>, inside min / max: each face a grid whose rim
            /// curls onto the rounding (<paramref name="bevel"/> rows per rim, <paramref name="divisions"/> across the flat),
            /// faces meeting at the 45° line so the surface is closed. <paramref name="puff"/> bulges each face's middle out
            /// by that much (a stuffed pouch, a pad), falling to 0 at its edges; negative dents it.
            /// </summary>
            public void RoundedBox(float[] lo, float[] hi, int mat, float lean, float radius, int bevel, int divisions, float puff,
                IReadOnlyDictionary<string, float[]> faceUv)
            {
                float[] c = new float[3], half = new float[3];
                for (int k = 0; k < 3; k++) { c[k] = (lo[k] + hi[k]) / 2; half[k] = Math.Max(1e-4f, MathF.Abs(hi[k] - lo[k]) / 2); }
                float r = Math.Min(radius, half.Min() * 0.95f);
                foreach (var (fn, fs, ft, key) in BoxFaces)
                {
                    float[]? rect = faceUv.GetValueOrDefault(key);
                    float hn = Dot(fn, half), hs = Dot(fs, half), ht = Dot(ft, half);
                    float[] sa = Steps(r / hs, bevel, divisions), ta = Steps(r / ht, bevel, divisions);
                    var grid = new int[ta.Length, sa.Length];
                    for (int j = 0; j < ta.Length; j++)
                        for (int i = 0; i < sa.Length; i++)
                        {
                            float a = sa[i], b = ta[j];
                            float[] p = new float[3], inner = new float[3], d = new float[3];
                            for (int k = 0; k < 3; k++)
                            {
                                p[k] = fn[k] * hn + fs[k] * a * hs + ft[k] * b * ht;
                                inner[k] = Math.Clamp(p[k], -(half[k] - r), half[k] - r);
                                d[k] = p[k] - inner[k];
                            }
                            float dl = MathF.Sqrt(d[0] * d[0] + d[1] * d[1] + d[2] * d[2]);
                            float[] n = dl > 1e-6f ? new[] { d[0] / dl, d[1] / dl, d[2] / dl } : new float[] { fn[0], fn[1], fn[2] };
                            float[] q = { c[0] + inner[0] + n[0] * r, c[1] + inner[1] + n[1] * r, c[2] + inner[2] + n[2] * r };
                            if (puff != 0)
                            {
                                // Height puff·(1 − a²)(1 − b²) along the face normal; the normal tilts down its slope.
                                float h = puff * (1 - a * a) * (1 - b * b), gs = 2 * a * puff * (1 - b * b) / hs, gt = 2 * b * puff * (1 - a * a) / ht;
                                for (int k = 0; k < 3; k++) { q[k] += fn[k] * h; n[k] += fs[k] * gs + ft[k] * gt; }
                            }
                            q[0] += lean * (q[1] - hi[1]);
                            grid[j, i] = V(q, n, mat, (a + 1) / 2, (b + 1) / 2, rect);
                        }
                    for (int j = 0; j + 1 < ta.Length; j++)
                        for (int i = 0; i + 1 < sa.Length; i++)
                        {
                            Tri(grid[j, i], grid[j, i + 1], grid[j + 1, i + 1]);
                            Tri(grid[j, i], grid[j + 1, i + 1], grid[j + 1, i]);
                        }
                }
            }

            // Grid positions across a face, −1..1: the rim (rounding width e of the half size) in `bevel` steps at each
            // end, the flat between in `divisions` steps.
            private static float[] Steps(float e, int bevel, int divisions)
            {
                e = Math.Clamp(e, 0, 0.95f);
                var list = new List<float> { -1 };
                if (e > 0) for (int i = 1; i <= bevel; i++) list.Add(-1 + e * i / bevel);
                for (int i = 1; i < divisions; i++) list.Add(-1 + e + (2 - 2 * e) * i / divisions);
                if (e > 0) for (int i = bevel; i >= 1; i--) list.Add(1 - e * i / bevel);
                list.Add(1);
                return list.ToArray();
            }

            private static float Dot(int[] axis, float[] v) => MathF.Abs(axis[0]) * v[0] + MathF.Abs(axis[1]) * v[1] + MathF.Abs(axis[2]) * v[2];

            /// <summary>
            /// A flat strap (webbing, a belt, a sling, a cable) of <paramref name="width"/> × <paramref name="thick"/> swept
            /// along <paramref name="path"/>. Its outer face looks away from <paramref name="around"/> (a point it wraps round)
            /// or along <paramref name="outward"/>, square to the path. The rectangle runs across its width (s) and along its
            /// length (t); its edges take a sliver of it.
            /// </summary>
            public void Strap(List<float[]> path, float width, float thick, float[]? around, float[]? outward, int mat, bool caps)
            {
                int count = path.Count;
                var frames = new (float[] P, float[] N, float[] S, float T)[count];
                float total = 0;
                var lengths = new float[count];
                for (int i = 1; i < count; i++) lengths[i] = total += Length(Sub(path[i], path[i - 1]));
                float[] lastN = outward ?? new[] { 0f, 1f, 0f };
                for (int i = 0; i < count; i++)
                {
                    float[] tan = Normalise(Sub(path[Math.Min(i + 1, count - 1)], path[Math.Max(i - 1, 0)]));
                    float[] o = around != null ? Sub(path[i], around) : (float[])outward!.Clone();
                    float along = o[0] * tan[0] + o[1] * tan[1] + o[2] * tan[2];
                    for (int k = 0; k < 3; k++) o[k] -= along * tan[k];
                    // Where the outward hint runs along the path, keep the last frame's.
                    float[] n = Length(o) > 1e-5f ? Normalise(o) : lastN;
                    lastN = n;
                    float[] side = Normalise(Cross(tan, n));
                    frames[i] = (path[i], n, side, total > 0 ? lengths[i] / total : i / (float)(count - 1));
                }
                float hw = width / 2, ht = thick / 2;
                float[] At(int i, float sw, float sn) =>
                    new[] { frames[i].P[0] + frames[i].S[0] * sw * hw + frames[i].N[0] * sn * ht, frames[i].P[1] + frames[i].S[1] * sw * hw + frames[i].N[1] * sn * ht, frames[i].P[2] + frames[i].S[2] * sw * hw + frames[i].N[2] * sn * ht };
                float[] Scaled(float[] v, float k) => new[] { v[0] * k, v[1] * k, v[2] * k };
                // Outer and inner faces across the width; the two edges across the thickness, on a sliver of the rectangle.
                foreach (var (sn, isEdge, sign) in new[] { (1f, false, 1f), (-1f, false, -1f), (1f, true, 1f), (-1f, true, -1f) })
                {
                    var rows = new int[count][];
                    for (int i = 0; i < count; i++)
                        rows[i] = isEdge
                            ? new[] { V(At(i, sign, 1), Scaled(frames[i].S, sign), mat, sign > 0 ? 1 : 0, frames[i].T), V(At(i, sign, -1), Scaled(frames[i].S, sign), mat, sign > 0 ? 0.92f : 0.08f, frames[i].T) }
                            : new[] { V(At(i, -1, sn), Scaled(frames[i].N, sn), mat, 0, frames[i].T), V(At(i, 1, sn), Scaled(frames[i].N, sn), mat, 1, frames[i].T) };
                    for (int i = 0; i + 1 < count; i++)
                    {
                        Tri(rows[i][0], rows[i][1], rows[i + 1][1]);
                        Tri(rows[i][0], rows[i + 1][1], rows[i + 1][0]);
                    }
                }
                if (!caps) return;
                foreach (var (i, dir) in new[] { (0, -1f), (count - 1, 1f) })
                {
                    float[] tan = Scaled(Normalise(Sub(path[Math.Min(i + 1, count - 1)], path[Math.Max(i - 1, 0)])), dir);
                    int a = V(At(i, -1, -1), tan, mat, 0, frames[i].T), b = V(At(i, 1, -1), tan, mat, 1, frames[i].T);
                    int cc = V(At(i, 1, 1), tan, mat, 1, frames[i].T), d = V(At(i, -1, 1), tan, mat, 0, frames[i].T);
                    Tri(a, b, cc);
                    Tri(a, cc, d);
                }
            }
        }

        // ------------------------------------------------------------------ json / math helpers

        private static (int, int, int) Axis(string? a) => (a ?? "x").ToLowerInvariant() switch
        {
            "x" => (0, 1, 2),
            "y" => (1, 2, 0),
            "z" => (2, 0, 1),
            _ => throw new ArgumentException($"axis '{a}' is x, y or z"),
        };

        private static (bool, bool) Caps(JsonElement s, bool dflt = false)
        {
            if (!s.TryGetProperty("caps", out var c)) return (dflt, dflt);
            return c.ValueKind switch
            {
                JsonValueKind.True => (true, true),
                JsonValueKind.False => (false, false),
                JsonValueKind.String when c.GetString() == "start" => (true, false),
                JsonValueKind.String when c.GetString() == "end" => (false, true),
                _ => throw new ArgumentException("caps is true, false, \"start\" or \"end\""),
            };
        }

        private static string? Str(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        private static float? Num(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : null;
        private static int? Int(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

        private static float[]? Vec(JsonElement e, string n, int len)
        {
            if (!e.TryGetProperty(n, out var v) || v.ValueKind != JsonValueKind.Array) return null;
            var a = v.EnumerateArray().Select(x => x.GetSingle()).ToArray();
            if (a.Length != len) throw new ArgumentException($"{n} needs {len} numbers");
            return a;
        }

        private static List<(float, float)>? Pairs(JsonElement e, string n)
        {
            if (!e.TryGetProperty(n, out var v) || v.ValueKind != JsonValueKind.Array) return null;
            return v.EnumerateArray().Select(p =>
            {
                var xs = p.EnumerateArray().Select(x => x.GetSingle()).ToArray();
                if (xs.Length != 2) throw new ArgumentException($"{n} points are [along, radius]");
                return (xs[0], Math.Max(xs[1], 0f));
            }).ToList();
        }

        // An atlas rectangle [u0, v0, u1, v1] in 0..1 (u0 < u1, v0 < v1).
        private static float[]? Rect(JsonElement e, string n, int index)
        {
            var r = Vec(e, n, 4);
            if (r != null && (r[0] >= r[2] || r[1] >= r[3] || r.Any(x => x < 0 || x > 1)))
                throw new ArgumentException($"shapes[{index}].{n} is [u0, v0, u1, v1] within 0..1, u0 < u1 and v0 < v1");
            return r;
        }

        private static readonly string[] FaceKeys = { "+x", "-x", "+y", "-y", "+z", "-z" };

        // faceUv {"-z": rect, ...}: a box face's own rectangle (the front of a pouch on webbing, its sides on plain nylon).
        private static Dictionary<string, float[]> FaceRects(JsonElement s, int index)
        {
            var map = new Dictionary<string, float[]>();
            if (!s.TryGetProperty("faceUv", out var f) || f.ValueKind != JsonValueKind.Object) return map;
            foreach (var p in f.EnumerateObject())
            {
                if (!FaceKeys.Contains(p.Name)) throw new ArgumentException($"shapes[{index}].faceUv keys are {string.Join(", ", FaceKeys)} (the face's normal)");
                using var doc = JsonDocument.Parse($"{{\"r\":{p.Value.GetRawText()}}}");
                map[p.Name] = Rect(doc.RootElement, "r", index) ?? throw new ArgumentException($"shapes[{index}].faceUv.{p.Name} is [u0, v0, u1, v1]");
            }
            return map;
        }

        private static List<float[]>? Points(JsonElement e, string n)
        {
            if (!e.TryGetProperty(n, out var v) || v.ValueKind != JsonValueKind.Array) return null;
            return v.EnumerateArray().Select(p =>
            {
                var xs = p.EnumerateArray().Select(x => x.GetSingle()).ToArray();
                if (xs.Length != 3) throw new ArgumentException($"{n} points are [x, y, z]");
                return xs;
            }).ToList();
        }

        /// <summary>The path through its points as a Catmull-Rom curve, <paramref name="steps"/> pieces per span (1: as given).</summary>
        public static List<float[]> Smooth(List<float[]> path, int steps)
        {
            if (steps <= 1 || path.Count < 3) return path;
            var list = new List<float[]>();
            for (int i = 0; i + 1 < path.Count; i++)
            {
                float[] p0 = path[Math.Max(i - 1, 0)], p1 = path[i], p2 = path[i + 1], p3 = path[Math.Min(i + 2, path.Count - 1)];
                for (int k = 0; k < steps; k++)
                {
                    float t = k / (float)steps, t2 = t * t, t3 = t2 * t;
                    var q = new float[3];
                    for (int c = 0; c < 3; c++)
                        q[c] = 0.5f * (2 * p1[c] + (p2[c] - p0[c]) * t + (2 * p0[c] - 5 * p1[c] + 4 * p2[c] - p3[c]) * t2 + (3 * p1[c] - p0[c] - 3 * p2[c] + p3[c]) * t3);
                    list.Add(q);
                }
            }
            list.Add(path[^1]);
            return list;
        }

        private static float Length(float[] v) => MathF.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
        private static float[] Cross(float[] a, float[] b) => new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };

        private static float[] Normalise(float[] v)
        {
            float l = MathF.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
            return l > 0 ? new[] { v[0] / l, v[1] / l, v[2] / l } : new[] { 0f, 1f, 0f };
        }

        private static float[] Sub(float[] a, float[] b) => new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };
        private static float[] Add(float[] a, float[] b) => new[] { a[0] + b[0], a[1] + b[1], a[2] + b[2] };
    }
}
