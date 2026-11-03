using System.Text.Json;
using CodeDesigner.Ps2.FileFormats.Zipper.Package;
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
                // Textures take pack-unique names, so a map's own texture of the same name cannot stand in.
                string gear = r.GetProperty("renamed").GetProperty("packtest.tif").GetString()!;
                Assert.StartsWith("mtest1", gear);
                Assert.Contains(d.GetProperty("chunks").EnumerateArray(), c => c.GetProperty("texture").GetString() == gear);
                Assert.DoesNotContain(d.GetProperty("chunks").EnumerateArray(), c => c.GetProperty("texture").GetString() == "seal_A_woodland_torso_LO.tif");
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

        // The SEAL decoded (bind pose, model space) before and after a transform, chunk by chunk.
        private static (List<float[]> Before, List<float[]> After)? Transformed(float[] matrix, CharacterEdits.Selection sel)
        {
            string? path = Environment.GetEnvironmentVariable("MB_MAP_ZDB");
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            var pkg = ZipperPackage.Open(path);
            var model = pkg.FindModel("CLIB_MDL.ZED", Seal)!;
            var before = ZipperModelMesh.Build(pkg, model).Chunks.Select(c => c.Positions).ToList();
            var r = CharacterEdits.TransformVertices(pkg, "CLIB_MDL.ZED", Seal, matrix, sel);
            foreach (var (leaf, bytes) in r.Changed) pkg.ReplaceEntry(leaf, bytes);
            var after = ZipperModelMesh.Build(pkg, pkg.FindModel("CLIB_MDL.ZED", Seal)!).Chunks.Select(c => c.Positions).ToList();
            return (before, after);
        }

        [Fact]
        public void A_translation_moves_every_skinned_vertex_by_that_much()
        {
            float[] up = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0.25f, 0.5f, -0.125f, 1 };
            if (Transformed(up, new CharacterEdits.Selection()) is not var (before, after)) return;
            Assert.Equal(before.Count, after.Count);
            for (int c = 0; c < before.Count; c++)
                for (int i = 0; i < before[c].Length; i++)
                    Assert.InRange(after[c][i] - before[c][i] - up[12 + i % 3], -0.01f, 0.01f);
        }

        [Fact]
        public void A_bone_selection_leaves_the_rest_of_the_body_alone()
        {
            // Twice as big about the head bone: the head grows, the feet stay.
            float[] grow = { 2, 0, 0, 0, 0, 2, 0, 0, 0, 0, 2, 0, 0, -17.63f, -0.15f, 1 };
            if (Transformed(grow, new CharacterEdits.Selection(new HashSet<string> { "head" })) is not var (before, after)) return;
            int moved = 0, still = 0;
            for (int c = 0; c < before.Count; c++)
                for (int i = 1; i < before[c].Length; i += 3)
                {
                    float y0 = before[c][i], y1 = after[c][i];
                    if (y0 < 5) { Assert.InRange(y1 - y0, -0.01f, 0.01f); still++; }
                    if (y0 > 19 && y1 - y0 > 0.5f) moved++;
                }
            Assert.True(still > 0 && moved > 0, $"{still} foot vertices, {moved} moved head-top vertices");
        }

        // A tube around SEAL A's right arm, shoulder to wrist (bind pose), as an addCharacterMesh mesh.
        private static (List<float[]> Positions, List<int[]> Triangles) Sleeve()
        {
            float[][] line = { new[] { 2.02f, 15.9f, 0.91f }, new[] { 4.67f, 13.86f, 1.4f }, new[] { 6.87f, 12.13f, 0.99f } };
            var pos = new List<float[]>();
            var tris = new List<int[]>();
            const int Seg = 8, Rings = 9;
            for (int r = 0; r < Rings; r++)
            {
                float t = r / (float)(Rings - 1) * 2;
                int k = Math.Min(1, (int)t);
                float f = t - k;
                float[] c = { line[k][0] + (line[k + 1][0] - line[k][0]) * f, line[k][1] + (line[k + 1][1] - line[k][1]) * f, line[k][2] + (line[k + 1][2] - line[k][2]) * f };
                for (int s = 0; s < Seg; s++)
                {
                    float a = s * MathF.PI * 2 / Seg;
                    // Around the arm, which runs mostly along x: the ring lies in y / z.
                    pos.Add(new[] { c[0], c[1] + 0.9f * MathF.Cos(a), c[2] + 0.9f * MathF.Sin(a) });
                }
            }
            for (int r = 0; r + 1 < Rings; r++)
                for (int s = 0; s < Seg; s++)
                {
                    int a = r * Seg + s, b = r * Seg + (s + 1) % Seg, c = a + Seg, d = b + Seg;
                    tris.Add(new[] { a, c, b });
                    tris.Add(new[] { b, c, d });
                }
            return (pos, tris);
        }

        // ASSET_MODEL_MESH arrays: base64 little-endian float32.
        private static float[] Floats(JsonElement base64)
        {
            byte[] b = Convert.FromBase64String(base64.GetString()!);
            var f = new float[b.Length / 4];
            Buffer.BlockCopy(b, 0, f, 0, b.Length);
            return f;
        }

        [Fact]
        public void Gear_without_a_bone_takes_the_bodys_skin_and_bends_at_the_elbow()
        {
            if (OpenMap() is not { } s) return;
            _b.Edit(s, new { op = "addTexture", name = "packtest.tif", @namespace = "clib", materials = new[] { new { color = new[] { 80, 90, 50 } } } });
            var (positions, triangles) = Sleeve();
            var r = _b.Edit(s, new { op = "addCharacterMesh", archive = "CLIB_MDL.ZED", model = Seal, texture = "packtest.tif", mesh = new { positions, triangles } });
            Assert.Equal(JsonValueKind.Null, r.GetProperty("bone").ValueKind);
            var bones = r.GetProperty("bones").EnumerateArray().Select(b => b.GetString()).ToList();
            Assert.Contains("rbicep", bones);
            Assert.Contains("rforearm", bones);

            // Decoded back (the editors' decoder, which reads the batches as the game does), the sleeve sits where it was
            // given, its weights sum to 1, and some vertices blend two bones.
            var arrays = _b.Ok(new CommandMessage { Command = "ASSET_MODEL_MESH", AssetSession = s, AssetArchive = "CLIB_MDL.ZED", AssetName = Seal,
                AssetJson = JsonSerializer.Serialize(new { arrays = true }) });
            var sleeve = arrays.GetProperty("chunks").EnumerateArray().Where(c => c.GetProperty("texture").GetString() == "packtest.tif").ToList();
            Assert.NotEmpty(sleeve);
            int blended = 0;
            foreach (var c in sleeve)
            {
                var p = Floats(c.GetProperty("positions"));
                var w = Floats(c.GetProperty("skinWeights"));
                for (int v = 0; v < p.Length / 3; v++)
                {
                    float nearest = positions.Min(q => (q[0] - p[v * 3]) * (q[0] - p[v * 3]) + (q[1] - p[v * 3 + 1]) * (q[1] - p[v * 3 + 1]) + (q[2] - p[v * 3 + 2]) * (q[2] - p[v * 3 + 2]));
                    Assert.True(nearest < 0.01f * 0.01f * 4, $"a sleeve vertex decoded {MathF.Sqrt(nearest):0.000} from any given one");
                    var ws = w.Skip(v * 4).Take(4).ToArray();
                    Assert.InRange(ws.Sum(), 0.99f, 1.01f);
                    if (ws.Count(x => x > 0.05f) >= 2) blended++;
                }
            }
            Assert.True(blended > 0, "no sleeve vertex blends two bones");
        }

        [Fact]
        public void A_thicker_bicep_blends_into_the_elbow_and_shoulder_without_a_seam()
        {
            string? path = Environment.GetEnvironmentVariable("MB_MAP_ZDB");
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            var pkg = ZipperPackage.Open(path);
            var before = ZipperModelMesh.Build(pkg, pkg.FindModel("CLIB_MDL.ZED", Seal)!);
            var r = CharacterEdits.ScaleBones(pkg, "CLIB_MDL.ZED", Seal, new Dictionary<string, CharacterEdits.BoneScale> { ["rbicep"] = new(Thickness: 1.3f) });
            foreach (var (leaf, bytes) in r.Changed) pkg.ReplaceEntry(leaf, bytes);
            var after = ZipperModelMesh.Build(pkg, pkg.FindModel("CLIB_MDL.ZED", Seal)!);
            var shoulder = before.Skeleton!.Bones.First(b => b.Name == "rbicep").World;
            var elbow = before.Skeleton.Bones.First(b => b.Name == "rforearm").World;
            float[] axis = { elbow[12] - shoulder[12], elbow[13] - shoulder[13], elbow[14] - shoulder[14] };
            float len = MathF.Sqrt(axis.Sum(x => x * x));
            float Off(float[] q, int i)   // distance from the upper arm's line
            {
                float[] d = { q[i] - shoulder[12], q[i + 1] - shoulder[13], q[i + 2] - shoulder[14] };
                float along = (d[0] * axis[0] + d[1] * axis[1] + d[2] * axis[2]) / len;
                return MathF.Sqrt(MathF.Max(0, d.Sum(x => x * x) - along * along));
            }
            float worst = 0;
            int grew = 0, left = 0;
            for (int c = 0; c < before.Chunks.Count; c++)
            {
                float[] p0 = before.Chunks[c].Positions, p1 = after.Chunks[c].Positions;
                int[] t = before.Chunks[c].Triangles;
                for (int k = 0; k < t.Length; k++)
                {
                    int a = t[k] * 3, b = t[k - k % 3 + (k + 1) % 3] * 3;
                    float l0 = MathF.Sqrt(Enumerable.Range(0, 3).Sum(i => (p0[a + i] - p0[b + i]) * (p0[a + i] - p0[b + i])));
                    float l1 = MathF.Sqrt(Enumerable.Range(0, 3).Sum(i => (p1[a + i] - p1[b + i]) * (p1[a + i] - p1[b + i])));
                    if (l0 > 0.05f) worst = MathF.Max(worst, l1 / l0);
                }
                for (int i = 0; i < p0.Length; i += 3)
                {
                    if (p0[i] < -1) { Assert.InRange(p1[i] - p0[i], -0.01f, 0.01f); left++; }    // the left side
                    if (Off(p1, i) > Off(p0, i) * 1.2f && Off(p0, i) > 0.3f) grew++;
                }
            }
            Assert.True(worst < 1.36f, $"an edge stretched {worst:0.00}x");
            Assert.True(left > 0 && grew > 0, $"{left} left-side vertices, {grew} grown upper-arm vertices");
        }

        [Fact]
        public void Conform_lifts_gear_out_of_the_chest_and_leaves_what_already_clears_it()
        {
            string? path = Environment.GetEnvironmentVariable("MB_MAP_ZDB");
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            var pkg = ZipperPackage.Open(path);
            // A plate set into the chest (its front is about z −1.25 here) and, apart from it, a pouch well clear of it.
            var input = MeshShapes.Build(JsonDocument.Parse("[{\"kind\":\"box\",\"min\":[-0.8,13,-1.2],\"max\":[0.8,14.5,-1.0],\"radius\":0.05}," +
                                                            "{\"kind\":\"box\",\"min\":[1.5,13,-4],\"max\":[1.8,13.4,-3.7]}]").RootElement);
            var fit = CharacterEdits.Conform(pkg, "CLIB_MDL.ZED", Seal, input, 0.06f, 0.5f);
            Assert.True(fit.Moved > 0 && fit.MaxPush > 0.05f, $"moved {fit.Moved}, max push {fit.MaxPush}");
            for (int i = 0; i < input.Positions.Count; i++)
                if (input.Positions[i][2] < -3.5f) Assert.Equal(input.Positions[i], fit.Mesh.Positions[i]);
            // Fitted, it clears the surface: a second fit has next to nothing to do.
            var again = CharacterEdits.Conform(pkg, "CLIB_MDL.ZED", Seal, fit.Mesh, 0.06f, 0);
            Assert.True(again.MaxPush < 0.03f, $"a second fit still pushes {again.MaxPush}");
            Assert.Equal(input.Triangles, fit.Mesh.Triangles);
        }

        [Fact]
        public void ScaleBones_names_its_bones_and_keeps_vertices_in_reach()
        {
            if (OpenMap() is not { } s) return;
            byte[] mdl = Backend.Bytes(s, "CLIB_MDL.ZED");
            var r = _b.Edit(s, new { op = "scaleBones", archive = "CLIB_MDL.ZED", model = Seal, bones = new Dictionary<string, object> { ["head"] = 0.9f, ["lbicep"] = new { thickness = 1.2f } } });
            Assert.True(r.GetProperty("parts").GetInt32() > 0);
            Assert.Equal("MESH_seal_A_woodland_1_LO", r.GetProperty("lowDetail").GetProperty("model").GetString());
            _b.Undo(s);
            Assert.Equal(mdl, Backend.Bytes(s, "CLIB_MDL.ZED"));
            Assert.Contains("no skinning bone", _b.EditFails(s, new { op = "scaleBones", archive = "CLIB_MDL.ZED", model = Seal, bones = new { tail = 2 } }));
            Assert.Contains("at most 7.99", _b.EditFails(s, new { op = "scaleBones", archive = "CLIB_MDL.ZED", model = Seal, bones = new { rcalf = new { length = 3 } } }));
        }

        [Fact]
        public void The_check_flags_the_hard_edged_reshape_and_passes_the_blended_one()
        {
            if (OpenMap() is not { } s) return;
            const string Ter = "MESH_alban_ter01";
            // The reshape the user saw go wrong: arms and head scaled with transformCharacter's hard-edged selection.
            var bad = _b.Edit(s, new { op = "batch", ops = new object[]
            {
                new { op = "transformCharacter", archive = "CLIB_MDL.ZED", model = Ter, bones = new[] { "rbicep", "lbicep", "rforearm", "lforearm" }, scale = new[] { 1f, 1.3f, 1.3f } },
                new { op = "transformCharacter", archive = "CLIB_MDL.ZED", model = Ter, bones = new[] { "head" }, scale = 0.85f },
            } });
            var check = _b.Ok(new CommandMessage { Command = "ASSET_CHARACTER_CHECK", AssetSession = s, AssetJson = JsonSerializer.Serialize(new { model = Ter }) });
            Assert.False(check.GetProperty("ok").GetBoolean());
            var near = check.GetProperty("problems").EnumerateArray().Select(x => x.GetProperty("bone").GetString()).ToList();
            Assert.Contains(near, b => b is "rbicep" or "lbicep" or "rforearm" or "lforearm");
            Assert.Contains(near, b => b is "head" or "neck");
            _b.Undo(s);

            var good = _b.Edit(s, new { op = "scaleBones", archive = "CLIB_MDL.ZED", model = Ter,
                bones = new Dictionary<string, object> { ["rbicep"] = new { thickness = 1.25f }, ["lbicep"] = new { thickness = 1.25f }, ["head"] = 0.92f } });
            Assert.True(good.GetProperty("check").GetProperty("ok").GetBoolean(), good.GetProperty("check").GetProperty("warnings").ToString());
            Assert.True(good.GetProperty("check").GetProperty("changedVertices").GetInt32() > 0);
        }

        [Fact]
        public void The_check_flags_gear_floating_off_the_body()
        {
            if (OpenMap() is not { } s) return;
            _b.Edit(s, new { op = "addTexture", name = "packtest.tif", @namespace = "clib", materials = new[] { new { color = new[] { 80, 90, 50 } } } });
            var r = _b.Edit(s, new { op = "addCharacterMesh", archive = "CLIB_MDL.ZED", model = Seal, bone = "head", texture = "packtest.tif",
                primitive = new { kind = "cube", size = 1, center = new[] { 0f, 22.5f, 0f } } });
            var check = r.GetProperty("check");
            Assert.False(check.GetProperty("ok").GetBoolean());
            Assert.Contains("float", check.GetProperty("warnings").ToString());
            Assert.Contains("packtest.tif", check.GetProperty("addedTextures").ToString());
        }

        private const string Torso = "seal_A_woodland_torso_LO.tif";

        [Fact]
        public void Paint_lands_where_the_renderer_samples_v_flipped()
        {
            // The renderer samples a top-down texture at V' = 1 - V: a triangle near v = 0.9 is in the top rows.
            var top = new TexturePaint.Triangle("t", new[] { 0.1f, 0.9f, 0.4f, 0.9f, 0.25f, 0.97f });
            var rows = TexturePaint.Masks(new[] { top }, 16, 16, 0)["t"].Select(i => i / 16).ToList();
            Assert.NotEmpty(rows);
            Assert.All(rows, r => Assert.True(r <= 2, $"row {r}"));

            // A region's uv rectangle is in UV space too: only the part of the triangle with v >= 0.93 is painted.
            byte[] rgba = new byte[16 * 16 * 4];
            var painted = TexturePaint.Paint(rgba, 16, 16, new[] { top },
                new[] { new TexturePaint.Region(new HashSet<string> { "t" }, new byte[] { 255, 0, 0 }, Fill: true, Uv: new[] { 0f, 0.93f, 1f, 1f }) });
            Assert.True(painted[0] > 0);
            for (int i = 0; i < 16 * 16; i++)
                if (rgba[i * 4] > 0) Assert.True(i / 16 <= 1, $"pixel {i} (row {i / 16}) is below the rectangle");
        }

        [Fact]
        public void Grit_scuffs_a_regions_rims_lighter_and_grimes_its_middle_the_same_way_every_time()
        {
            // A square UV island in a 64x64 texture, plain grey.
            var a = new TexturePaint.Triangle("p", new[] { 0.1f, 0.1f, 0.9f, 0.1f, 0.9f, 0.9f });
            var b = new TexturePaint.Triangle("p", new[] { 0.1f, 0.1f, 0.9f, 0.9f, 0.1f, 0.9f });
            byte[] Paint(TexturePaint.Grit g)
            {
                byte[] rgba = Enumerable.Repeat((byte)128, 64 * 64 * 4).ToArray();
                TexturePaint.Paint(rgba, 64, 64, new[] { a, b }, new[] { new TexturePaint.Region(new HashSet<string> { "p" }, null, Grit: g) });
                return rgba;
            }
            var wear = Paint(new TexturePaint.Grit(Wear: 1));
            int Px(int x, int y) => wear[(y * 64 + x) * 4];
            Assert.Equal(128, Px(32, 32));                                                       // the middle is untouched
            Assert.Equal(128, Px(2, 2));                                                         // outside the island too
            Assert.Contains(Enumerable.Range(6, 52), x => Px(x, 6) > 140 || Px(x, 5) > 140);     // its top rim is lighter

            // A second island laid over the left half (an atlas shared by many pouches) keeps its own rim, at u 0.5.
            byte[] both = Enumerable.Repeat((byte)128, 64 * 64 * 4).ToArray();
            var c = new TexturePaint.Triangle("p", new[] { 0.12f, 0.12f, 0.5f, 0.12f, 0.5f, 0.88f });
            var d = new TexturePaint.Triangle("p", new[] { 0.12f, 0.12f, 0.5f, 0.88f, 0.12f, 0.88f });
            TexturePaint.Paint(both, 64, 64, new[] { a, b, c, d }, new[] { new TexturePaint.Region(new HashSet<string> { "p" }, null, Grit: new TexturePaint.Grit(Wear: 1)) });
            Assert.Contains(Enumerable.Range(20, 24), y => both[(y * 64 + 31) * 4] > 140 || both[(y * 64 + 32) * 4] > 140);

            var grime = Paint(new TexturePaint.Grit(Grime: 1, Weave: 0.5f, Stitch: 1, Seed: 7));
            var inside = Enumerable.Range(16, 32).SelectMany(y => Enumerable.Range(16, 32).Select(x => grime[(y * 64 + x) * 4])).ToList();
            Assert.True(inside.Min() < 110 && inside.Distinct().Count() > 4, "grime and weave vary the middle");
            Assert.Equal(grime, Paint(new TexturePaint.Grit(Grime: 1, Weave: 0.5f, Stitch: 1, Seed: 7)));
            Assert.NotEqual(grime, Paint(new TexturePaint.Grit(Grime: 1, Weave: 0.5f, Stitch: 1, Seed: 8)));
        }

        [Fact]
        public void The_uv_layout_names_the_body_parts_a_skin_covers()
        {
            if (OpenMap() is not { } s) return;
            var r = _b.Ok(new CommandMessage { Command = "ASSET_UV_LAYOUT", AssetSession = s, AssetJson = JsonSerializer.Serialize(new { model = Seal, texture = Torso }) });
            var parts = r.GetProperty("info").GetProperty("parts").EnumerateArray().Select(x => x.GetProperty("part").GetString()).ToList();
            Assert.Contains("spinehi", parts);
            Assert.Contains("rbicep", parts);
            Assert.DoesNotContain("rfoot", parts);
            byte[] png = Convert.FromBase64String(r.GetProperty("png").GetString()!);
            CodeDesigner.Ps2.FileFormats.Imaging.PngDecoder.DecodeRgba(png, out int w, out int h);
            Assert.True(Math.Max(w, h) >= 512);
        }

        [Fact]
        public void Painting_the_arms_red_leaves_the_chest_alone()
        {
            string? path = Environment.GetEnvironmentVariable("MB_MAP_ZDB");
            if (OpenMap() is not { } s || path == null) return;
            var arms = new[] { "rbicep", "rforearm", "lbicep", "lforearm" };
            // The places, from the same triangles the op paints.
            var pkg = ZipperPackage.Open(path);
            var mesh = ZipperModelMesh.Build(pkg, pkg.FindModel("CLIB_MDL.ZED", Seal)!);
            var tex = pkg.DecodeTexture(Torso, "clib")!.Value.Texture;
            int w = tex.Header.Width, h = tex.Header.Height;
            var masks = TexturePaint.Masks(TexturePaint.Triangles(mesh, Torso), w, h);
            var armPixels = arms.Where(masks.ContainsKey).SelectMany(a => masks[a]).ToHashSet();
            var chestOnly = masks["spinehi"].Where(i => !armPixels.Contains(i)).ToHashSet();
            Assert.NotEmpty(armPixels);
            Assert.NotEmpty(chestOnly);

            byte[] before = Backend.Bytes(s, "CLIB_TXR.ZED");
            var r = _b.Edit(s, new { op = "paintTexture", texture = Torso, model = Seal, regions = new[] { new { parts = arms, color = new[] { 200, 30, 30 } } } });
            Assert.True(r.GetProperty("pixels")[0].GetInt32() > 0);
            var after = _b.Ok(new CommandMessage { Command = "ASSET_TEXTURE", AssetSession = s, AssetName = Torso });
            byte[] rgba = CodeDesigner.Ps2.FileFormats.Imaging.PngDecoder.DecodeRgba(Convert.FromBase64String(after.GetProperty("png").GetString()!), out _, out _);
            double red = armPixels.Average(i => rgba[i * 4] - (rgba[i * 4 + 1] + rgba[i * 4 + 2]) / 2.0);
            double drift = chestOnly.Average(i => Math.Abs(rgba[i * 4] - tex.Rgba[i * 4]) + Math.Abs(rgba[i * 4 + 1] - tex.Rgba[i * 4 + 1]) + Math.Abs(rgba[i * 4 + 2] - tex.Rgba[i * 4 + 2]));
            Assert.True(red > 60, $"the arms are not red (red over green/blue {red:0})");
            Assert.True(drift < 12, $"the chest changed by {drift:0.0} per pixel");

            Assert.Contains("draws no part", _b.EditFails(s, new { op = "paintTexture", texture = Torso, model = Seal, regions = new[] { new { parts = new[] { "rfoot" }, color = new[] { 1, 2, 3 } } } }));
            _b.Undo(s);
            Assert.Equal(before, Backend.Bytes(s, "CLIB_TXR.ZED"));
        }

        [Fact]
        public void TransformCharacter_is_one_undo_step_on_both_meshes()
        {
            if (OpenMap() is not { } s) return;
            byte[] mdl = Backend.Bytes(s, "CLIB_MDL.ZED");
            var r = _b.Edit(s, new { op = "transformCharacter", archive = "CLIB_MDL.ZED", model = Seal, bones = new[] { "rbicep", "rforearm", "rhand" }, scale = 1.15f });
            Assert.True(r.GetProperty("vertices").GetInt32() > 0);
            Assert.Equal("MESH_seal_A_woodland_1_LO", r.GetProperty("lowDetail").GetProperty("model").GetString());
            Assert.NotEqual(mdl, Backend.Bytes(s, "CLIB_MDL.ZED"));
            _b.Undo(s);
            Assert.Equal(mdl, Backend.Bytes(s, "CLIB_MDL.ZED"));
            Assert.Contains("No vertex", _b.EditFails(s, new { op = "transformCharacter", archive = "CLIB_MDL.ZED", model = Seal, texture = "nothing.tif", scale = 2 }));
            Assert.Contains("at most 7.99", _b.EditFails(s, new { op = "transformCharacter", archive = "CLIB_MDL.ZED", model = Seal, scale = 6 }));
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
