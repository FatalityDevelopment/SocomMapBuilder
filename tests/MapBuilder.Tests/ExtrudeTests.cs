using System.Text.Json;
using MapBuilder.Server;
using MapBuilder.Tests.Fixtures;
using Xunit;

namespace MapBuilder.Tests
{
    /// <summary>
    /// extrudeFaces on a real static prop (M51's crate_small): set MB_MAP_ZDB to M51.ZDB; without it they pass vacuously.
    /// </summary>
    [Collection(MapPackageCollection.Name)]
    public sealed class ExtrudeTests : IDisposable
    {
        private readonly Backend _b = new();
        private readonly List<string> _sessions = new();

        public void Dispose()
        {
            foreach (string s in _sessions) _b.Close(s);
        }

        private string? OpenMap()
        {
            string? path = Environment.GetEnvironmentVariable("MB_MAP_ZDB");
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            string s = _b.Open(path);
            _sessions.Add(s);
            return s;
        }

        private static float[] Floats(JsonElement base64)
        {
            byte[] b = Convert.FromBase64String(base64.GetString()!);
            var f = new float[b.Length / 4];
            Buffer.BlockCopy(b, 0, f, 0, b.Length);
            return f;
        }

        private static uint[] Uints(JsonElement base64)
        {
            byte[] b = Convert.FromBase64String(base64.GetString()!);
            var f = new uint[b.Length / 4];
            Buffer.BlockCopy(b, 0, f, 0, b.Length);
            return f;
        }

        private JsonElement Mesh(string s, string archive, string model) => _b.Ok(new CommandMessage
        {
            Command = "ASSET_MODEL_MESH", AssetSession = s, AssetArchive = archive, AssetName = model, AssetJson = JsonSerializer.Serialize(new { arrays = true }),
        });

        [Fact]
        public void Extruding_a_face_pushes_it_out_with_walls_and_inset_keeps_it_in_its_plane()
        {
            if (OpenMap() is not { } s) return;
            var list = _b.Ok(new CommandMessage { Command = "ASSET_LIST_MODELS", AssetSession = s, AssetJson = JsonSerializer.Serialize(new { query = "crate_small" }) });
            var crate = list.EnumerateArray().First(m => m.GetProperty("name").GetString() == "crate_small");
            string archive = crate.GetProperty("archive").GetString()!;
            var before = Mesh(s, archive, "crate_small");
            int Triangles(JsonElement m) => m.GetProperty("chunks").EnumerateArray().Sum(c => Uints(c.GetProperty("triangles")).Length / 3);
            float MaxAlong(JsonElement m, int axis, int sign) => m.GetProperty("chunks").EnumerateArray()
                .SelectMany(c => Floats(c.GetProperty("positions")).Where((_, i) => i % 3 == axis)).Max(v => v * sign) * sign;

            // The top: the triangles of a chunk whose vertices are all at the crate's highest Y.
            float top = MaxAlong(before, 1, 1);
            JsonElement chunk = default;
            float[] pos = Array.Empty<float>();
            uint[] tris = Array.Empty<uint>();
            int[] topFaces = Array.Empty<int>();
            foreach (var c in before.GetProperty("chunks").EnumerateArray())
            {
                pos = Floats(c.GetProperty("positions"));
                tris = Uints(c.GetProperty("triangles"));
                var p = pos;
                var t3 = tris;
                topFaces = Enumerable.Range(0, t3.Length / 3).Where(t => Enumerable.Range(0, 3).All(e => MathF.Abs(p[t3[t * 3 + e] * 3 + 1] - top) < 0.05f)).ToArray();
                if (topFaces.Length > 0) { chunk = c; break; }
            }
            Assert.NotEmpty(topFaces);
            string offset = chunk.GetProperty("geometryOffset").GetInt32().ToString();
            byte[] mdl = Backend.Bytes(s, archive);

            _b.Edit(s, new { op = "extrudeFaces", archive, model = "crate_small", faces = new Dictionary<string, int[]> { [offset] = topFaces }, distance = 2f });
            var after = Mesh(s, archive, "crate_small");
            Assert.InRange(MaxAlong(after, 1, 1), top + 1.95f, top + 2.05f);
            // The cap replaces the faces; each open edge adds a wall of two triangles (a quad's top: 4 edges, 8 triangles).
            string Per(JsonElement m) => string.Join(",", m.GetProperty("chunks").EnumerateArray().Select(c => $"{c.GetProperty("geometryOffset").GetInt32()}:{Uints(c.GetProperty("triangles")).Length / 3}"));
            Assert.True(Triangles(after) >= Triangles(before) + 6, $"{Triangles(before)} -> {Triangles(after)}; picked {topFaces.Length} of {offset}; before {Per(before)}; after {Per(after)}");
            _b.Undo(s);
            Assert.Equal(mdl, Backend.Bytes(s, archive));

            // Inset: shrunk to half about the top's centre, no move: nothing rises, the width stays.
            float cx = topFaces.SelectMany(t => Enumerable.Range(0, 3).Select(e => pos[tris[t * 3 + e] * 3])).Average();
            float cz = topFaces.SelectMany(t => Enumerable.Range(0, 3).Select(e => pos[tris[t * 3 + e] * 3 + 2])).Average();
            float[] inset = { 0.5f, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0.5f, 0, cx * 0.5f, 0, cz * 0.5f, 1 };
            _b.Edit(s, new { op = "extrudeFaces", archive, model = "crate_small", faces = new Dictionary<string, int[]> { [offset] = topFaces }, matrix = inset });
            var insetMesh = Mesh(s, archive, "crate_small");
            Assert.InRange(MaxAlong(insetMesh, 1, 1), top - 0.01f, top + 0.01f);
            Assert.InRange(MaxAlong(insetMesh, 0, 1), MaxAlong(before, 0, 1) - 0.01f, MaxAlong(before, 0, 1) + 0.01f);
            _b.Undo(s);

            Assert.Contains("distance", _b.EditFails(s, new { op = "extrudeFaces", archive, model = "crate_small", faces = new Dictionary<string, int[]> { [offset] = topFaces } }));
        }
    }

    /// <summary>Tests that open the real map package: opening the same path joins one session, so they take turns
    /// (one class closing it would pull it from under another).</summary>
    [CollectionDefinition(Name)]
    public sealed class MapPackageCollection
    {
        public const string Name = "Real map package (MB_MAP_ZDB)";
    }
}
