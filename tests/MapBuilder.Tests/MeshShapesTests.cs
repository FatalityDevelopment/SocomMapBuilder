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
