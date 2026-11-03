using System.Text.Json;
using MapBuilder.Server;
using MapBuilder.Tests.Fixtures;
using Xunit;

namespace MapBuilder.Tests
{
    /// <summary>
    /// addCharacterMesh: rigid geometry appended to a skinned character as a new material section. The real-mesh tests
    /// need a SOCOM II map package with SEAL A (M51.ZDB): set MB_MAP_ZDB to its path; without it they pass vacuously.
    /// </summary>
    public sealed class CharacterEditTests : IDisposable
    {
        private const string Seal = "MESH_seal_A_woodland_LO";
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

        private JsonElement Describe(string session, string model) =>
            _b.Ok(new CommandMessage { Command = "ASSET_DESCRIBE_MODEL", AssetSession = session, AssetArchive = "CLIB_MDL.ZED", AssetName = model });

        private static object Pack => new
        {
            op = "addCharacterMesh", archive = "CLIB_MDL.ZED", model = Seal, bone = "spinehi", texture = "packtest.tif",
            shapes = new object[]
            {
                new { kind = "box", min = new[] { -1.7f, 12.2f, 1.45f }, max = new[] { 1.7f, 16f, 2.9f }, material = 0 },
                new { kind = "cylinder", axis = "x", start = -2.1f, end = 2.1f, radius = 0.65f, center = new[] { 0f, 16.55f, 2.2f }, segments = 10, material = 1, caps = true },
            },
        };

        [Fact]
        public void A_static_model_is_refused()
        {
            using var pkg = new SyntheticPackage();
            string s = _b.Open(pkg.Folder);
            _sessions.Add(s);
            string error = _b.EditFails(s, new { op = "addCharacterMesh", archive = "TST_MDL.ZED", model = "crate", bone = "head", texture = "x.tif", primitive = new { kind = "cube" } });
            Assert.Contains("not a skinned character mesh", error);
        }

        [Fact]
        public void Rigid_geometry_decodes_as_new_groups_on_its_bone()
        {
            if (OpenMap() is not { } s) return;
            var before = Describe(s, Seal);
            int chunks = before.GetProperty("chunks").GetArrayLength();
            Assert.Contains(before.GetProperty("skeleton").EnumerateArray(), b => b.GetProperty("name").GetString() == "spinehi");

            _b.Edit(s, new { op = "addTexture", name = "packtest.tif", @namespace = "clib", materials = new[] { new { color = new[] { 80, 90, 50 } }, new { color = new[] { 40, 40, 30 } } } });
            byte[] mdl = Backend.Bytes(s, "CLIB_MDL.ZED");
            var r = _b.Edit(s, Pack);
            Assert.Equal("spinehi", r.GetProperty("bone").GetString());
            int groups = r.GetProperty("groups").GetInt32();
            Assert.True(groups >= 1);

            var after = Describe(s, Seal);
            var added = after.GetProperty("chunks").EnumerateArray().Skip(chunks).ToList();
            Assert.Equal(groups, added.Count);
            Assert.All(added, c => Assert.Equal("packtest.tif", c.GetProperty("texture").GetString()));
            Assert.Equal(r.GetProperty("triangles").GetInt32(), added.Sum(c => c.GetProperty("triangles").GetInt32()));

            _b.Undo(s);
            Assert.Equal(mdl, Backend.Bytes(s, "CLIB_MDL.ZED"));
        }

        [Theory]
        [InlineData("MESH_seal_A_woodland_LO", "MESH_seal_A_woodland_1_LO")]
        [InlineData("MESH_alban_ter01", "MESH_alban_ter01_1")]
        public void The_lower_detail_mesh_is_named_as_the_disc_names_it(string mesh, string low) =>
            Assert.Equal(low, CodeDesigner.Ps2.FileFormats.Zipper.Package.CharacterEdits.LowDetailName(mesh));

        [Fact]
        public void Gear_goes_on_the_lower_detail_mesh_too_and_comes_off_by_texture()
        {
            if (OpenMap() is not { } s) return;
            const string Low = "MESH_seal_A_woodland_1_LO";
            int Chunks(string model) => Describe(s, model).GetProperty("chunks").GetArrayLength();
            int high = Chunks(Seal), low = Chunks(Low);
            _b.Edit(s, new { op = "addTexture", name = "packtest.tif", @namespace = "clib", materials = new[] { new { color = new[] { 80, 90, 50 } }, new { color = new[] { 40, 40, 30 } } } });

            var r = _b.Edit(s, Pack);
            Assert.Equal(Low, r.GetProperty("lowDetail").GetProperty("model").GetString());
            Assert.Equal(high + r.GetProperty("groups").GetInt32(), Chunks(Seal));
            Assert.Equal(low + r.GetProperty("lowDetail").GetProperty("groups").GetInt32(), Chunks(Low));

            foreach (string model in new[] { Seal, Low })
            {
                var removed = _b.Edit(s, new { op = "removeCharacterSection", archive = "CLIB_MDL.ZED", model, texture = "packtest.tif" });
                Assert.Single(removed.GetProperty("removed").EnumerateArray());
            }
            Assert.Equal(high, Chunks(Seal));
            Assert.Equal(low, Chunks(Low));
            Assert.DoesNotContain(Describe(s, Seal).GetProperty("chunks").EnumerateArray(), c => c.GetProperty("texture").GetString() == "packtest.tif");
            Assert.Contains("no section drawn with", _b.EditFails(s, new { op = "removeCharacterSection", archive = "CLIB_MDL.ZED", model = Seal, texture = "packtest.tif" }));
        }

        private JsonElement PackOf(string session, object args) =>
            _b.Ok(new CommandMessage { Command = "ASSET_CHARACTER_PACK", AssetSession = session, AssetJson = JsonSerializer.Serialize(args) });

        [Fact]
        public void A_pack_needs_a_skinned_mesh_and_a_pack_name()
        {
            using var pkg = new SyntheticPackage();
            string s = _b.Open(pkg.Folder);
            _sessions.Add(s);
            string Fails(object args) => _b.Fails(new CommandMessage { Command = "ASSET_CHARACTER_PACK", AssetSession = s, AssetJson = JsonSerializer.Serialize(args) });
            Assert.Contains("not a skinned character mesh", Fails(new { model = "crate", donor = "con_pow01", pack = "mtest", dryRun = true }));
            Assert.Contains("must start with 'm'", Fails(new { model = "crate", donor = "con_pow01", pack = "toolongname", dryRun = true }));
        }

        [Fact]
        public void A_packed_character_reads_back_under_the_donor_name_with_its_gear()
        {
            if (OpenMap() is not { } s) return;
            _b.Edit(s, new { op = "addTexture", name = "packtest.tif", @namespace = "clib", materials = new[] { new { color = new[] { 80, 90, 50 } }, new { color = new[] { 40, 40, 30 } } } });
            _b.Edit(s, Pack);
            string dir = Path.Combine(Path.GetTempPath(), "mb-charpack-" + Guid.NewGuid().ToString("N"));
            try
            {
                var r = PackOf(s, new { model = Seal, donor = "con_pow01", pack = "mtest1", outputDir = dir });
                Assert.Equal("MESH_con_pow01", r.GetProperty("mesh").GetString());
                Assert.Equal(new[] { "CLIB_GEO.ZED", "CLIB_MDL.ZED", "CLIB_PAL.ZED", "CLIB_TXR.ZED" },
                    r.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("path").GetString()!.Split('\\').Last()).OrderBy(x => x));
                Assert.True(File.Exists(Path.Combine(dir, "MTEST1.CDB")));
                int base_ = CodeDesigner.Ps2.FileFormats.Zipper.Conversion.CaToSocom2.PaletteBase("mtest1");
                Assert.All(r.GetProperty("palettes").EnumerateObject(), p => Assert.InRange(p.Value.GetInt32(), base_, base_ + 31));
                Assert.Empty(r.GetProperty("warnings").EnumerateArray());

                string packed = _b.Open(Path.Combine(dir, "MTEST1"));
                _sessions.Add(packed);
                var d = Describe(packed, "MESH_con_pow01");
                var source = Describe(s, Seal);
                Assert.Equal(source.GetProperty("chunks").GetArrayLength(), d.GetProperty("chunks").GetArrayLength());
                Assert.Contains(d.GetProperty("chunks").EnumerateArray(), c => c.GetProperty("texture").GetString() == "packtest.tif");
                Assert.All(d.GetProperty("textures").EnumerateArray(), t =>
                {
                    Assert.True(t.GetProperty("found").GetBoolean(), t.GetProperty("name").GetString());
                    Assert.True(t.GetProperty("paletteResolved").GetBoolean(), t.GetProperty("name").GetString());
                    Assert.InRange(t.GetProperty("palette").GetInt32(), base_, base_ + 31);
                });
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (IOException) { }
            }
        }

        [Fact]
        public void Geometry_out_of_a_bones_reach_is_refused()
        {
            if (OpenMap() is not { } s) return;
            _b.Edit(s, new { op = "addTexture", name = "packtest.tif", @namespace = "clib", materials = new[] { new { color = new[] { 80, 90, 50 } } } });
            string error = _b.EditFails(s, new { op = "addCharacterMesh", archive = "CLIB_MDL.ZED", model = Seal, bone = "rfoot", texture = "packtest.tif",
                primitive = new { kind = "cube", size = 1, center = new[] { 0f, 18f, 0f } } });
            Assert.Contains("at most 7.99", error);
        }
    }
}
