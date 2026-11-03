using System.Text.Json;

namespace CodeDesigner.Ps2.FileFormats.Zipper.Package
{
    /// <summary>
    /// Meshes from a list of shapes (lathe, cylinder, sphere, box, ring), each with a material index. Materials map to a
    /// material-strip skin (<see cref="SkinRgba"/>): a 64×64 texture whose column strip 8m..8m+7 is material m and whose
    /// row is a shade from dark (top) to light (bottom). Every vertex picks its shade from its normal against a light, so
    /// the model reads shaded with or without lighting. Up to 8 materials. Axes: a shape's axis is "x", "y" or "z"
    /// (default "x": weapons point +x, +y up).
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
                string kind = Str(s, "kind") ?? throw new ArgumentException($"shapes[{index}] needs a kind (lathe, cylinder, sphere, box, ring)");
                int mat = Int(s, "material") ?? 0;
                if (mat < 0 || mat >= MaxMaterials) throw new ArgumentException($"shapes[{index}].material is 0..{MaxMaterials - 1}");
                int seg = Math.Clamp(Int(s, "segments") ?? 16, 3, 64);
                var axis = Axis(Str(s, "axis"));
                float[] center = Vec(s, "center", 3) ?? new float[3];
                float[] squash = Vec(s, "squash", 2) ?? new[] { 1f, 1f };
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
                        b.Box(lo, hi, mat, Num(s, "lean") ?? 0);
                        break;
                    }
                    default:
                        throw new ArgumentException($"shapes[{index}].kind '{kind}' is not lathe, cylinder, sphere, box or ring");
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
            public Builder(float[] light) => _light = light;

            private int V(float[] p, float[] n, int mat)
            {
                n = Normalise(n);
                Mesh.Positions.Add(p);
                Mesh.Normals.Add(n);
                float shade = Math.Max(0, n[0] * _light[0] + n[1] * _light[1] + n[2] * _light[2]) * 0.8f + 0.2f;
                int s = Math.Min(Shades - 1, (int)(shade * (Shades - 1)));
                Mesh.Uvs.Add(new[] { (StripWidth * mat + StripWidth / 2f) / Size, (2 * s + 1) / (float)Size });
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
                for (int i = 0; i < prof.Count; i++)
                {
                    var (a, r) = prof[i];
                    var (a0, r0) = prof[Math.Max(i - 1, 0)];
                    var (a1, r1) = prof[Math.Min(i + 1, prof.Count - 1)];
                    // Profile normal (slope along the axis, 1 outward); a step in radius at one position faces along the axis.
                    float da = a1 - a0;
                    float slope = -(r1 - r0) / (MathF.Abs(da) < 1e-6f ? 1e-6f : da);
                    var ring = new int[seg];
                    for (int k = 0; k < seg; k++)
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
                        ring[k] = V(p, n, mat);
                    }
                    rings.Add(ring);
                }
                for (int i = 0; i + 1 < rings.Count; i++)
                    for (int k = 0; k < seg; k++)
                    {
                        int a = rings[i][k], b = rings[i][(k + 1) % seg], c = rings[i + 1][k], d = rings[i + 1][(k + 1) % seg];
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
                int mid = V(c, n, mat);
                var edge = ring.Select(i => V(Mesh.Positions[i], n, mat)).ToArray();
                for (int k = 0; k < edge.Length; k++) Tri(mid, edge[k], edge[(k + 1) % edge.Length]);
            }

            public void Sphere(float[] c, float r, int mat, int seg, int rings, float[] scale)
            {
                var grid = new List<int[]>();
                for (int i = 0; i <= rings; i++)
                {
                    float phi = MathF.PI * i / rings;
                    var row = new int[seg];
                    for (int k = 0; k < seg; k++)
                    {
                        float th = 2 * MathF.PI * k / seg;
                        float[] u = { -MathF.Cos(phi), MathF.Sin(phi) * MathF.Cos(th), MathF.Sin(phi) * MathF.Sin(th) };
                        float[] p = { c[0] + u[0] * r * scale[0], c[1] + u[1] * r * scale[1], c[2] + u[2] * r * scale[2] };
                        row[k] = V(p, new[] { u[0] / scale[0], u[1] / scale[1], u[2] / scale[2] }, mat);
                    }
                    grid.Add(row);
                }
                for (int i = 0; i < rings; i++)
                    for (int k = 0; k < seg; k++)
                    {
                        int a = grid[i][k], b = grid[i][(k + 1) % seg], cc = grid[i + 1][k], d = grid[i + 1][(k + 1) % seg];
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

        private static float[] Normalise(float[] v)
        {
            float l = MathF.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
            return l > 0 ? new[] { v[0] / l, v[1] / l, v[2] / l } : new[] { 0f, 1f, 0f };
        }

        private static float[] Sub(float[] a, float[] b) => new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };
        private static float[] Add(float[] a, float[] b) => new[] { a[0] + b[0], a[1] + b[1], a[2] + b[2] };
    }
}
