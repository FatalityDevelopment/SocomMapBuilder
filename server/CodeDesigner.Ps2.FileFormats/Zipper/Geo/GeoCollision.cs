using CodeDesigner.Ps2.FileFormats.Assets;
using CodeDesigner.Ps2.FileFormats.Zipper.ZED;

namespace CodeDesigner.Ps2.FileFormats.Zipper.Geo
{
    /// <summary>
    /// A scene node's collision: its <c>di</c> region, a list of <c>di</c> faces each holding <c>params</c> (12 bytes)
    /// and <c>points</c> (16 bytes a corner: x, y, z in the node's own space and a zero). Every placed reference
    /// carries its own copy of its model's faces (MP2's barrels and containers repeat the definition's faces
    /// verbatim), and the world's floors and walls are faces on the scene nodes under <c>worldmodel</c>, so a node
    /// without a region is not solid. Layout: docs/formats/collision.md.
    /// </summary>
    public static class GeoCollision
    {
        /// <summary>
        /// How a face behaves: every params byte but the corner count. The surface material (u32 at +4; on MP2 3 the
        /// deck, 4 metal walkways, 7 the containers, 61 the barrels), the u32 at +0 (0x22 on some of MP2's deck faces,
        /// 0x400 on some of M51's) and the flag bytes at +9, +10 and +11 (0x44 on MP2, 0x04 on some of M51's faces).
        /// </summary>
        public sealed record Profile(uint Material, uint Prefix = 0, byte Flags9 = 0x64, byte Flags10 = 0, byte Flags11 = 0x44)
        {
            /// <summary>MP2's deck plating, the most common retail profile.</summary>
            public static Profile Default { get; } = new(3);
        }

        /// <summary>A convex polygon in node space (x, y, z per corner, counter-clockwise seen from outside).</summary>
        public sealed record Face(float[] Points, Profile Profile)
        {
            public int Count => Points.Length / 3;

            /// <summary>The face's unit normal (Newell's method), outward for the retail winding.</summary>
            public float[] Normal()
            {
                double nx = 0, ny = 0, nz = 0;
                int n = Count;
                for (int i = 0; i < n; i++)
                {
                    int j = (i + 1) % n;
                    float ax = Points[i * 3], ay = Points[i * 3 + 1], az = Points[i * 3 + 2];
                    float bx = Points[j * 3], by = Points[j * 3 + 1], bz = Points[j * 3 + 2];
                    nx += (ay - by) * (az + bz); ny += (az - bz) * (ax + bx); nz += (ax - bx) * (ay + by);
                }
                double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                return len > 0 ? new[] { (float)(nx / len), (float)(ny / len), (float)(nz / len) } : new float[3];
            }
        }

        // params byte 8: the corner count Ã— 4, plus 2, plus 1 on faces that look up (floors and up-slopes: every
        // retail face with a positive normal Y, and none other, has it).
        private const byte UpBit = 1;

        public static byte[] Params(Face face)
        {
            if (face.Count is < 3 or > 63) throw new ArgumentException("A collision face has 3-63 corners");
            var p = new byte[12];
            BitConverter.GetBytes(face.Profile.Prefix).CopyTo(p, 0);
            BitConverter.GetBytes(face.Profile.Material).CopyTo(p, 4);
            p[8] = (byte)(face.Count * 4 + 2 + (face.Normal()[1] > 0.01f ? UpBit : 0));
            p[9] = face.Profile.Flags9;
            p[10] = face.Profile.Flags10;
            p[11] = face.Profile.Flags11;
            return p;
        }

        public static Profile ProfileOf(byte[] parameters) => parameters.Length >= 12
            ? new Profile(BitConverter.ToUInt32(parameters, 4), BitConverter.ToUInt32(parameters, 0), parameters[9], parameters[10], parameters[11])
            : Profile.Default;

        /// <summary>A node's faces (its own <c>di</c> region; the region's faces without params or points are skipped).</summary>
        public static List<Face> Read(GeoNode node)
        {
            var faces = new List<Face>();
            if (node.Child("di") is not { } region) return faces;
            foreach (GeoNode f in region.Children.Where(c => c.Name == "di"))
            {
                if (f.Child("params")?.Data is not { Length: >= 12 } prm || f.Child("points")?.Data is not { Length: >= 48 } pts) continue;
                int n = pts.Length / 16;
                var points = new float[n * 3];
                for (int i = 0; i < n; i++)
                    for (int k = 0; k < 3; k++) points[i * 3 + k] = BitConverter.ToSingle(pts, i * 16 + k * 4);
                faces.Add(new Face(points, ProfileOf(prm)));
            }
            return faces;
        }

        /// <summary>The <c>di</c> region record for <paramref name="faces"/> (null for none: a node without one is not solid).</summary>
        public static ZedTree.Record? Region(IReadOnlyList<Face> faces)
        {
            if (faces.Count == 0) return null;
            return new ZedTree.Record("di", Array.Empty<byte>(), faces.Select(f =>
            {
                var pts = new byte[f.Count * 16];
                for (int i = 0; i < f.Count; i++)
                    for (int k = 0; k < 3; k++) BitConverter.GetBytes(f.Points[i * 3 + k]).CopyTo(pts, i * 16 + k * 4);
                return new ZedTree.Record("di", Array.Empty<byte>(), new[] { new ZedTree.Record("params", Params(f)), new ZedTree.Record("points", pts) });
            }).ToList());
        }

        /// <summary>
        /// A box's faces, outward. Retail boxes (MP2's containers, the barrels' prisms) leave the bottom off, as
        /// nothing reaches it from below; <paramref name="bottom"/> adds it.
        /// </summary>
        public static List<Face> Box(float[] min, float[] max, Profile profile, bool bottom = false)
        {
            if (min.Length != 3 || max.Length != 3 || Enumerable.Range(0, 3).Any(i => !(max[i] > min[i])))
                throw new ArgumentException("A collision box needs min < max on every axis");
            float x0 = min[0], y0 = min[1], z0 = min[2], x1 = max[0], y1 = max[1], z1 = max[2];
            var faces = new List<Face>
            {
                new(new[] { x0, y1, z1, x1, y1, z1, x1, y1, z0, x0, y1, z0 }, profile), // top
                new(new[] { x0, y0, z1, x0, y1, z1, x0, y1, z0, x0, y0, z0 }, profile), // -x
                new(new[] { x1, y0, z0, x1, y1, z0, x1, y1, z1, x1, y0, z1 }, profile), // +x
                new(new[] { x0, y0, z0, x0, y1, z0, x1, y1, z0, x1, y0, z0 }, profile), // -z
                new(new[] { x1, y0, z1, x1, y1, z1, x0, y1, z1, x0, y0, z1 }, profile), // +z
            };
            if (bottom) faces.Add(new(new[] { x0, y0, z0, x1, y0, z0, x1, y0, z1, x0, y0, z1 }, profile));
            return faces;
        }

        /// <summary>
        /// One triangle face per drawn triangle of a mesh (in model space; zero-area triangles, such as a hidden
        /// visual's speck, are left out).
        /// </summary>
        public static List<Face> FromMesh(ModelMesh mesh, Profile profile)
        {
            var faces = new List<Face>();
            foreach (var c in mesh.Chunks)
            {
                var p = c.Positions;
                float[] At(int v) => Point(c.Matrix, p[v * 3], p[v * 3 + 1], p[v * 3 + 2]);
                for (int t = 0; t + 2 < c.Triangles.Length; t += 3)
                {
                    var a = At(c.Triangles[t]); var b = At(c.Triangles[t + 1]); var d = At(c.Triangles[t + 2]);
                    var face = new Face(new[] { a[0], a[1], a[2], b[0], b[1], b[2], d[0], d[1], d[2] }, profile);
                    if (Area(face) > 1e-4f) faces.Add(face);
                }
            }
            return faces;
        }

        private static float[] Point(float[] m, float x, float y, float z)
        {
            var (px, py, pz) = Matrix4.Apply(m, x, y, z);
            return new[] { px, py, pz };
        }

        private static float Area(Face f)
        {
            var p = f.Points;
            float ux = p[3] - p[0], uy = p[4] - p[1], uz = p[5] - p[2], vx = p[6] - p[0], vy = p[7] - p[1], vz = p[8] - p[2];
            float cx = uy * vz - uz * vy, cy = uz * vx - ux * vz, cz = ux * vy - uy * vx;
            return 0.5f * MathF.Sqrt(cx * cx + cy * cy + cz * cz);
        }

        /// <summary>A face's corners through <paramref name="matrix"/> (column-major), e.g. into the world.</summary>
        public static float[] Transform(Face face, float[] matrix)
        {
            var o = new float[face.Points.Length];
            for (int i = 0; i < face.Count; i++)
            {
                var w = Point(matrix, face.Points[i * 3], face.Points[i * 3 + 1], face.Points[i * 3 + 2]);
                w.CopyTo(o, i * 3);
            }
            return o;
        }
    }
}
