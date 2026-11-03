using System.Text.Json;
using CodeDesigner.Ps2.FileFormats.Zipper.Package;
using Xunit;

namespace MapBuilder.Tests
{
    /// <summary>addMesh shapes: meshes from lathes, cylinders, rings, spheres and boxes, shaded from a materials skin.</summary>
    public sealed class MeshShapesTests
    {
        private static MeshTopology.InputMesh Build(string json) => MeshShapes.Build(JsonDocument.Parse(json).RootElement);

        private static float[] Centroid(MeshTopology.InputMesh m, (int A, int B, int C) t) =>
            Enumerable.Range(0, 3).Select(k => (m.Positions[t.A][k] + m.Positions[t.B][k] + m.Positions[t.C][k]) / 3).ToArray();

        private static float[] FaceNormal(MeshTopology.InputMesh m, (int A, int B, int C) t)
        {
            float[] a = m.Positions[t.A], b = m.Positions[t.B], c = m.Positions[t.C];
            float[] e1 = { b[0] - a[0], b[1] - a[1], b[2] - a[2] }, e2 = { c[0] - a[0], c[1] - a[1], c[2] - a[2] };
            return new[] { e1[1] * e2[2] - e1[2] * e2[1], e1[2] * e2[0] - e1[0] * e2[2], e1[0] * e2[1] - e1[1] * e2[0] };
        }

        // Every face of a closed convex shape points away from its centre.
        private static void AssertOutward(MeshTopology.InputMesh m, float[] centre)
        {
            Assert.NotEmpty(m.Triangles);
            foreach (var t in m.Triangles)
            {
                float[] n = FaceNormal(m, t), c = Centroid(m, t);
                float dot = n[0] * (c[0] - centre[0]) + n[1] * (c[1] - centre[1]) + n[2] * (c[2] - centre[2]);
                Assert.True(dot > 0, $"triangle {t} faces inward");
            }
        }

        [Fact]
        public void Spheres_boxes_and_capped_cylinders_face_outward_on_every_axis()
        {
            AssertOutward(Build("[{\"kind\":\"sphere\",\"center\":[1,2,3],\"radius\":0.5,\"segments\":12}]"), new[] { 1f, 2, 3 });
            AssertOutward(Build("[{\"kind\":\"box\",\"min\":[-1,-1,-1],\"max\":[1,2,1]}]"), new[] { 0f, 0.5f, 0 });
            foreach (string axis in new[] { "x", "y", "z" })
                AssertOutward(Build($"[{{\"kind\":\"cylinder\",\"axis\":\"{axis}\",\"start\":-1,\"end\":1,\"radius\":0.3,\"caps\":true}}]"), new[] { 0f, 0, 0 });
        }

        [Fact]
        public void A_lathe_follows_its_profile_along_the_axis()
        {
            var m = Build("[{\"kind\":\"lathe\",\"axis\":\"x\",\"center\":[0,0.5,0],\"profile\":[[-1,0.1],[0,0.4],[1,0.2]],\"segments\":8}]");
            Assert.Equal(3 * 8, m.Positions.Count);
            Assert.Equal(2 * 8 * 2, m.Triangles.Count);
            // The ring at along = 0 has radius 0.4 around the bore line y = 0.5.
            var mid = m.Positions.Where(p => MathF.Abs(p[0]) < 1e-5f).ToList();
            Assert.All(mid, p => Assert.Equal(0.4f, MathF.Sqrt((p[1] - 0.5f) * (p[1] - 0.5f) + p[2] * p[2]), 3));
        }

        [Fact]
        public void Materials_pick_their_strip_of_the_skin()
        {
            var m = Build("[{\"kind\":\"box\",\"min\":[0,0,0],\"max\":[1,1,1],\"material\":3}]");
            Assert.All(m.Uvs, uv => Assert.Equal((8 * 3 + 4) / 64f, uv[0], 5));
            byte[] skin = MeshShapes.SkinRgba(JsonDocument.Parse("[{\"color\":[255,0,0]},{\"light\":[0,255,0],\"dark\":[0,10,0]}]").RootElement);
            Assert.Equal(64 * 64 * 4, skin.Length);
            int Px(int x, int y) => (y * 64 + x) * 4;
            Assert.Equal(255, skin[Px(4, 63)]);                  // material 0 at its lightest: red
            Assert.Equal(255, skin[Px(12, 63) + 1]);             // material 1 lightest: green
            Assert.Equal(10, skin[Px(12, 0) + 1]);               // material 1 darkest: its dark colour
            Assert.Equal(skin[Px(12, 40)], skin[Px(60, 40)]);    // unused strips repeat the last material
        }

        // Every edge of a closed surface is shared by two triangles, once each way (vertices welded by position).
        private static void AssertClosed(MeshTopology.InputMesh m)
        {
            var id = new Dictionary<(int, int, int), int>();
            int Key(int i)
            {
                var p = m.Positions[i];
                var k = ((int)MathF.Round(p[0] * 1e4f), (int)MathF.Round(p[1] * 1e4f), (int)MathF.Round(p[2] * 1e4f));
                return id.TryGetValue(k, out int v) ? v : id[k] = id.Count;
            }
            var edges = new Dictionary<(int, int), int>();
            foreach (var (a, b, c) in m.Triangles)
                foreach (var (x, y) in new[] { (Key(a), Key(b)), (Key(b), Key(c)), (Key(c), Key(a)) })
                    edges[(x, y)] = edges.GetValueOrDefault((x, y)) + 1;
            Assert.All(edges, e => Assert.Equal(1, edges.GetValueOrDefault((e.Key.Item2, e.Key.Item1))));
        }

        [Fact]
        public void A_rounded_box_stays_in_its_bounds_closed_and_facing_out()
        {
            var m = Build("[{\"kind\":\"box\",\"min\":[-1,0,-0.5],\"max\":[1,2,0.5],\"radius\":0.2,\"bevelSegments\":2}]");
            Assert.All(m.Positions, p =>
            {
                Assert.InRange(p[0], -1.0001f, 1.0001f);
                Assert.InRange(p[1], -0.0001f, 2.0001f);
                Assert.InRange(p[2], -0.5001f, 0.5001f);
            });
            AssertOutward(m, new[] { 0f, 1, 0 });
            AssertClosed(m);
            // The corners are cut: nothing reaches (1, 2, 0.5).
            Assert.DoesNotContain(m.Positions, p => p[0] > 0.99f && p[1] > 1.99f && p[2] > 0.49f);
            Assert.Contains(m.Positions, p => MathF.Abs(p[0] - 0.8f) < 1e-4f && MathF.Abs(p[1] - 2) < 1e-4f);   // the top is flat to its rim
        }

        [Fact]
        public void A_puffed_box_bulges_at_the_middle_of_each_face_and_stays_closed()
        {
            var m = Build("[{\"kind\":\"box\",\"min\":[-1,-1,-1],\"max\":[1,1,1],\"radius\":0.1,\"puff\":0.3}]");
            Assert.Contains(m.Positions, p => MathF.Abs(p[0]) < 1e-4f && MathF.Abs(p[1]) < 1e-4f && MathF.Abs(p[2] + 1.3f) < 1e-3f);
            Assert.All(m.Positions, p => Assert.True(p.Max(MathF.Abs) <= 1.3001f));
            AssertClosed(m);
        }

        [Fact]
        public void A_plain_box_is_as_it_was()
        {
            var plain = Build("[{\"kind\":\"box\",\"min\":[0,0,0],\"max\":[1,1,1]}]");
            var zero = Build("[{\"kind\":\"box\",\"min\":[0,0,0],\"max\":[1,1,1],\"radius\":0}]");
            Assert.Equal(24, plain.Positions.Count);
            Assert.Equal(plain.Positions.SelectMany(p => p), zero.Positions.SelectMany(p => p));
            Assert.Equal(plain.Uvs.SelectMany(p => p), zero.Uvs.SelectMany(p => p));
        }

        [Fact]
        public void A_uv_rect_maps_a_shape_into_its_rectangle_top_at_v1()
        {
            var m = Build("[{\"kind\":\"box\",\"min\":[0,0,0],\"max\":[1,1,1],\"radius\":0.1,\"uvRect\":[0.25,0.5,0.5,0.75],\"faceUv\":{\"-z\":[0,0,0.25,0.25]}}]");
            Assert.All(m.Uvs, uv => Assert.True((uv[0] >= 0.25f && uv[0] <= 0.5f && uv[1] >= 0.5f && uv[1] <= 0.75f) || (uv[0] <= 0.25f && uv[1] <= 0.25f)));
            // The front face's top edge (y 1) sits at the rectangle's top, v1; its bottom at v0.
            var flat = Build("[{\"kind\":\"box\",\"min\":[0,0,0],\"max\":[1,1,1],\"faceUv\":{\"-z\":[0,0,0.25,0.25]}}]");
            int front = 0;
            for (int i = 0; i < flat.Positions.Count; i++)
                if (flat.Normals[i][2] < -0.99f) { Assert.Equal(flat.Positions[i][1] * 0.25f, flat.Uvs[i][1], 4); front++; }
            Assert.Equal(4, front);
            var c = Build("[{\"kind\":\"cylinder\",\"axis\":\"y\",\"start\":0,\"end\":1,\"radius\":0.5,\"segments\":8,\"caps\":false,\"uvRect\":[0,0,1,1]}]");
            Assert.Equal(2 * 9, c.Positions.Count);   // a seam copy per ring
            Assert.Contains(c.Uvs, uv => uv[0] == 1);
        }

        [Fact]
        public void A_strap_follows_its_path_and_its_outer_face_looks_away_from_what_it_wraps()
        {
            // A half-band round a torso at the origin: 0.3 wide, 0.1 thick, radius 2.
            var path = Enumerable.Range(0, 7).Select(k => { double a = Math.PI * k / 6; return $"[{2 * Math.Cos(a):0.####},1,{2 * Math.Sin(a):0.####}]"; });
            var m = Build($"[{{\"kind\":\"strap\",\"path\":[{string.Join(",", path)}],\"width\":0.3,\"thickness\":0.1,\"around\":[0,1,0]}}]");
            Assert.All(m.Positions, p =>
            {
                float r = MathF.Sqrt(p[0] * p[0] + p[2] * p[2]);
                Assert.InRange(r, 1.94f, 2.06f);
                Assert.InRange(p[1], 0.84f, 1.16f);
            });
            // Faces with a radial normal: the outer one (r > 2) looks out, the inner one in.
            for (int i = 0; i < m.Positions.Count; i++)
            {
                var p = m.Positions[i];
                float r = MathF.Sqrt(p[0] * p[0] + p[2] * p[2]);
                float radial = (m.Normals[i][0] * p[0] + m.Normals[i][2] * p[2]) / r;
                if (MathF.Abs(radial) > 0.9f) Assert.Equal(MathF.Sign(r - 2), MathF.Sign(radial));
            }
            AssertClosed(m);
            Assert.Contains("around", Assert.Throws<ArgumentException>(() => Build("[{\"kind\":\"strap\",\"path\":[[0,0,0],[1,0,0]],\"width\":0.2}]")).Message);
        }

        [Fact]
        public void Subdividing_a_rounded_box_smooths_it_keeps_it_closed_and_quadruples_its_triangles()
        {
            const string box = "{\"kind\":\"box\",\"min\":[-1,-1,-1],\"max\":[1,1,1],\"radius\":0.4";
            var coarse = Build($"[{box}}}]");
            var fine = Build($"[{box},\"subdivide\":1}}]");
            Assert.Equal(4 * coarse.Triangles.Count, fine.Triangles.Count);
            AssertClosed(fine);
            AssertOutward(fine, new[] { 0f, 0, 0 });
            // Smoothing pulls the corners in: nothing is further out than the coarse box reached.
            float Far(MeshTopology.InputMesh m) => m.Positions.Max(p => MathF.Sqrt(p[0] * p[0] + p[1] * p[1] + p[2] * p[2]));
            Assert.True(Far(fine) < Far(coarse));
            // A plain box's 90° edges are creases: subdivided, it is still the same cube.
            var cube = Build("[{\"kind\":\"box\",\"min\":[0,0,0],\"max\":[1,1,1],\"material\":2,\"subdivide\":2}]");
            Assert.Equal(12 * 16, cube.Triangles.Count);
            Assert.All(cube.Positions, p => Assert.Contains(p, c => MathF.Abs(c) < 1e-4f || MathF.Abs(c - 1) < 1e-4f));
            Assert.All(cube.Uvs, uv => Assert.Equal((8 * 2 + 4) / 64f, uv[0], 5));   // still material 2's strip
            AssertClosed(cube);
        }

        [Fact]
        public void Noise_roughens_a_surface_along_its_normals_without_opening_seams()
        {
            const string sphere = "{\"kind\":\"sphere\",\"center\":[0,0,0],\"radius\":1,\"segments\":16,\"uvRect\":[0,0,1,1]";
            var rough = Build($"[{sphere},\"noise\":{{\"amount\":0.08,\"scale\":0.3,\"seed\":3}}}}]");
            var radii = rough.Positions.Select(p => MathF.Sqrt(p[0] * p[0] + p[1] * p[1] + p[2] * p[2])).ToList();
            Assert.All(radii, r => Assert.InRange(r, 0.91f, 1.09f));
            Assert.True(radii.Max() - radii.Min() > 0.03f);
            AssertClosed(rough);
            // The same seed gives the same surface.
            var again = Build($"[{sphere},\"noise\":{{\"amount\":0.08,\"scale\":0.3,\"seed\":3}}}}]");
            Assert.Equal(rough.Positions.SelectMany(p => p), again.Positions.SelectMany(p => p));
            Assert.Contains("subdivide", Assert.Throws<ArgumentException>(() => Build("[{\"kind\":\"box\",\"min\":[0,0,0],\"max\":[1,1,1],\"subdivide\":3}]")).Message);
        }

        [Fact]
        public void Bad_shapes_say_what_is_wrong()
        {
            Assert.Contains("profile", Assert.Throws<ArgumentException>(() => Build("[{\"kind\":\"lathe\"}]")).Message);
            Assert.Contains("material", Assert.Throws<ArgumentException>(() => Build("[{\"kind\":\"box\",\"min\":[0,0,0],\"max\":[1,1,1],\"material\":9}]")).Message);
            Assert.Contains("cone", Assert.Throws<ArgumentException>(() => Build("[{\"kind\":\"cone\"}]")).Message);
            Assert.Contains("At most 8", Assert.Throws<ArgumentException>(() =>
                MeshShapes.SkinRgba(JsonDocument.Parse("[" + string.Join(",", Enumerable.Repeat("{\"color\":[1,2,3]}", 9)) + "]").RootElement)).Message);
        }
    }
}
