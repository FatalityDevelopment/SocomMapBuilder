using CodeDesigner.Ps2.FileFormats.Zipper.Conversion;
using CodeDesigner.Ps2.FileFormats.Zipper.Package;
using CodeDesigner.Ps2.FileFormats.Zipper.RDR;
using CodeDesigner.Ps2.FileFormats.Zipper.Texture;
using CodeDesigner.Ps2.FileFormats.Zipper.ZED;
using Xunit;

namespace MapBuilder.Tests
{
    /// <summary>
    /// Custom weapon packs from the disc's tables. Game files are not in the repo: set MAPBUILDER_DISC to the disc root
    /// (holding RUN/) and, for the pack, MB_WEAP_DIR to a folder with RUN/COMMON's WEAP_*.ZED; without them these pass
    /// vacuously.
    /// </summary>
    public sealed class WeaponPackTests
    {
        private static string? Run()
        {
            string? disc = Environment.GetEnvironmentVariable("MAPBUILDER_DISC");
            string run = Path.Combine(disc ?? "", "RUN");
            return !string.IsNullOrEmpty(disc) && File.Exists(Path.Combine(run, "ZWEAPON.ZAR")) ? run : null;
        }

        private static byte[] Record(string zar, string suffix) =>
            ZedContainer.FromBytes(File.ReadAllBytes(zar)).Nodes.First(n => n.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)).Data;

        [Fact]
        public void A_weapon_field_changes_without_touching_the_values_it_shared()
        {
            if (Run() is not { } run) return;
            var table = new WeaponTable(Record(Path.Combine(run, "ZWEAPON.ZAR"), "zweapon.rdr"));
            int w = table.FindWeapon("Model 18");
            var others = table.Weapons.Where(x => x != w).Select(x => table.Fields(x)).ToList();
            // InternalName and DisplayName share one atom on the disc; NumZoomModes shares the "1" with other records.
            table.Set(w, "DisplayName", "Ray Pistol");
            table.Set(w, "NumZoomModes", 2);
            table.Set(w, "FireWait", 0.25f);
            table = new WeaponTable(table.ToBytes());
            w = table.FindWeapon("Ray Pistol");
            Assert.Equal("Model 18", table.Get(w, "InternalName"));
            Assert.Equal(2, table.Get(w, "NumZoomModes"));
            Assert.Equal(0.25f, (float)table.Get(w, "FireWait")!, 5);
            Assert.Equal(others.Select(f => string.Join(",", f.Select(kv => $"{kv.Key}={kv.Value}"))),
                table.Weapons.Where(x => x != w).Select(x => string.Join(",", table.Fields(x).Select(kv => $"{kv.Key}={kv.Value}"))));
        }

        [Fact]
        public void A_weapon_gets_its_own_ammo_and_the_others_keep_theirs()
        {
            if (Run() is not { } run) return;
            var table = new WeaponTable(Record(Path.Combine(run, "ZWEAPON.ZAR"), "zweapon.rdr"));
            string old = table.AmmoOf(table.FindWeapon("Model 18")).First();
            var sharers = table.Weapons.Where(x => table.AmmoOf(x).Contains(old)).Select(x => table.Get(x, "InternalName") as string).ToList();
            float damage = (float)table.Get(table.FindAmmo(old), "ImpactDamage")!;
            int a = table.OwnAmmo("Model 18", "mrgun_ammo");
            table.Set(a, "ImpactDamage", 9.5f);
            table = new WeaponTable(table.ToBytes());
            Assert.Equal(new[] { "mrgun_ammo" }, table.AmmoOf(table.FindWeapon("Model 18")));
            Assert.Equal(9.5f, (float)table.Get(table.FindAmmo("mrgun_ammo"), "ImpactDamage")!, 5);
            Assert.Equal(damage, (float)table.Get(table.FindAmmo(old), "ImpactDamage")!, 5);
            foreach (string? name in sharers.Where(n => n != "Model 18"))
                Assert.Contains(old, table.AmmoOf(table.FindWeapon(name!)));
            // A fresh ID, used by no other ammo type (the disc itself repeats one ID, so only ours is checked).
            int mine = table.FindAmmo("mrgun_ammo");
            object? id = table.Get(mine, "ID");
            Assert.DoesNotContain(table.Ammo.Where(x => x != mine), x => Equals(table.Get(x, "ID"), id));
        }

        [Fact]
        public void The_language_table_finds_a_weapons_name_and_caption()
        {
            if (Run() is not { } run) return;
            var lang = new LanguageTable(Record(Path.Combine(run, "LOCALE", "STATES", "UIMNLOC.ZAR"), ".rdr"));
            Assert.Contains("_133_Model18_MSG", lang.KeysWithText("Model 18"));
            Assert.Contains(lang.Captions("MODEL 18"), c => c.Key == "_1076_AutoPistolModel18_MSG" && c.Kind == "AUTOMATIC PISTOL");
            lang.SetText("_1076_AutoPistolModel18_MSG", "RAY GUN: RAY PISTOL");
            Assert.Equal("RAY GUN: RAY PISTOL", new LanguageTable(lang.ToBytes()).Text("_1076_AutoPistolModel18_MSG"));
        }

        [Fact]
        public void A_custom_weapon_packs_its_model_icon_tables_and_texts()
        {
            if (Run() is not { } run) return;
            string? weap = Environment.GetEnvironmentVariable("MB_WEAP_DIR");
            if (string.IsNullOrEmpty(weap) || !File.Exists(Path.Combine(weap, "WEAP_MDL.ZED"))) return;
            string dir = Path.Combine(Path.GetTempPath(), "mb-wpack-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                foreach (string f in Directory.GetFiles(weap, "WEAP_*.ZED")) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
                var pkg = ZipperPackage.Open(dir);
                void Apply(IReadOnlyDictionary<string, byte[]> changed) { foreach (var (leaf, bytes) in changed) pkg.ReplaceEntry(leaf, bytes); }
                // As the MCP flow makes one: a copy of the desert eagle, a materials skin, shapes on both LODs, the rest hidden.
                Apply(PackageWeapons.Create(pkg, "WEAP_MDL.ZED", "desert_eagle", "raypistol").Changed);
                byte[] skin = MeshShapes.SkinRgba(System.Text.Json.JsonDocument.Parse("[{\"color\":[230,50,40]},{\"color\":[235,238,244]}]").RootElement);
                Apply(PackageTextures.AddTexture(pkg, "raypistol.tif", "weap", skin, 64, 64, 64, 64).Changed);
                var shapes = MeshShapes.Build(System.Text.Json.JsonDocument.Parse(
                    "[{\"kind\":\"lathe\",\"center\":[0,0.6,0],\"profile\":[[-1.2,0.05],[-0.9,0.3],[0.4,0.15]],\"material\":0}," +
                    "{\"kind\":\"cylinder\",\"center\":[0,0.6,0],\"start\":0.3,\"end\":1.9,\"radius\":0.08,\"material\":1,\"caps\":true}," +
                    "{\"kind\":\"box\",\"min\":[-0.8,-0.9,-0.12],\"max\":[-0.45,0.4,0.12],\"material\":0,\"lean\":0.2}]").RootElement);
                foreach (string visual in new[] { "N000_000", "N001_000" })
                    Apply(MeshTopology.AddMesh(pkg, "WEAP_MDL.ZED", "raypistol", visual, "raypistol.tif", shapes, true).Changed);
                foreach (string visual in new[] { "N000_001", "N001_001" })
                    Apply(MeshTopology.HideVisual(pkg, "WEAP_MDL.ZED", "raypistol", visual).Changed);

                var r = WeaponPack.Build(pkg, new WeaponPack.Options
                {
                    Pack = "mrgun", Model = "raypistol", Donor = "F57", Name = "Ray Pistol",
                    Stats = new() { ["FireWait"] = 0.3f, ["Ammo_Capacity"] = 20 },
                    Ammo = new() { ["ImpactDamage"] = 8f },
                }, new Socom2Templates(run));

                var files = r.Files.ToDictionary(f => f.Path, f => f.Data);
                foreach (string leaf in new[] { "WEAP_TXR", "WEAP_PAL", "WEAP_GEO", "WEAP_MDL", "HUDX_TXR", "HUDX_PAL", "HUDX_GEO", "HUDX_MDL" })
                    Assert.True(files.ContainsKey($"RUN\\MP\\MRGUN\\{leaf}.ZED"), leaf);
                Assert.True(files.ContainsKey("RUN\\MP\\MRGUN\\ZWEAPON.ZAR"));

                // The weapon library opens as a package of its own: the model with its GEO definition and textures.
                string lib = Path.Combine(dir, "lib");
                Directory.CreateDirectory(lib);
                foreach (var (path, data) in r.Files.Where(f => f.Path.Contains("\\WEAP_"))) File.WriteAllBytes(Path.Combine(lib, path.Split('\\').Last()), data);
                var packed = ZipperPackage.Open(lib);
                var model = packed.FindModel("WEAP_MDL.ZED", "raypistol");
                Assert.NotNull(model);
                Assert.NotNull(packed.FindDefinition(model!));
                Assert.All(packed.DecodeModel(model!).Chunks.Where(c => c.TextureName != null), c => Assert.NotNull(packed.DecodeTexture(c.TextureName!, "weap")));

                // The donor now draws the model and icon under its new name, with its own ammo.
                var table = new WeaponTable(ZedContainer.FromBytes(files["RUN\\MP\\MRGUN\\ZWEAPON.ZAR"]).Nodes.First(n => n.Name == "zweapon.rdr").Data);
                int w = table.FindWeapon("F57");
                Assert.Equal("raypistol", table.Get(w, "ModelName"));
                Assert.Equal("mrgun_icon.tif", table.Get(w, "IconTextureName"));
                Assert.Equal("Ray Pistol", table.Get(w, "DisplayName"));
                Assert.Equal(20, table.Get(w, "Ammo_Capacity"));
                Assert.Equal(8f, (float)table.Get(table.FindAmmo("mrgun_ammo"), "ImpactDamage")!, 5);

                // The icon: 128x32, drawn (some pixels set, some clear).
                var hud = ZedContainer.FromBytes(files["RUN\\MP\\MRGUN\\HUDX_TXR.ZED"]);
                var icon = ZedTextureFile.ParseHeader(hud.Nodes.First(n => n.Name == "texdat").Data);
                Assert.Equal((128, 32), ((int)icon.Width, (int)icon.Height));
                byte[] px = hud.Nodes.First(n => n.Name == "texdat").Data[ZedTextureFile.HeaderSize..(ZedTextureFile.HeaderSize + 4096)];
                Assert.Contains(px, p => p != 0);
                Assert.Contains(px, p => p == 0);
                // Rows are stored bottom-up: in picture order the lowest drawn row is the grip, at the gun's rear (left).
                int lowest = Enumerable.Range(0, 32).Last(y => Enumerable.Range(0, 128).Any(x => px[(31 - y) * 128 + x] != 0));
                var grip = Enumerable.Range(0, 128).Where(x => px[(31 - lowest) * 128 + x] != 0).ToList();
                Assert.True(grip.Average() < 64, $"the lowest drawn row centres at x {grip.Average():0} (expected the grip, left of centre)");

                // Only textures the model draws with: the hidden visuals' old desert eagle texture stays out.
                Assert.DoesNotContain("deserteagle.tif", r.Textures);

                // The armory texts follow the name.
                Assert.Contains(r.TextsChanged, t => t.Value == "Ray Pistol");
            }
            finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
        }
    }
}
