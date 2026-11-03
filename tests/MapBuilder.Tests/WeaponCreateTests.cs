using System.Text.Json;
using CodeDesigner.Ps2.FileFormats.Imaging;
using CodeDesigner.Ps2.FileFormats.Zipper.Package;
using CodeDesigner.Ps2.FileFormats.Zipper.Texture;
using CodeDesigner.Ps2.FileFormats.Zipper.ZED;
using MapBuilder.Server;
using MapBuilder.Server.Services;
using Xunit;

namespace MapBuilder.Tests
{
    /// <summary>
    /// ZIPPER_CREATE_WEAPON on the disc layout (WEAP_MDL beside WEAP_TXR / WEAP_PAL). Game files are not in the repo:
    /// set MB_WEAP_DIR to a folder holding RUN/COMMON's WEAP_*.ZED to run these; without it they pass vacuously.
    /// </summary>
    public sealed class WeaponCreateTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "mb-weapc-" + Guid.NewGuid().ToString("N"));
        private readonly CommandRegistry _reg = new();
        private readonly bool _ready;

        public WeaponCreateTests()
        {
            string? source = Environment.GetEnvironmentVariable("MB_WEAP_DIR");
            if (string.IsNullOrEmpty(source) || !File.Exists(Path.Combine(source, "WEAP_MDL.ZED"))) return;
            Directory.CreateDirectory(_dir);
            foreach (string f in Directory.GetFiles(source, "WEAP_*.ZED")) File.Copy(f, Path.Combine(_dir, Path.GetFileName(f)));
            new ZipperWeaponsService().Register(_reg);
            _ready = true;
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch (IOException) { }
        }

        private string Mdl => Path.Combine(_dir, "WEAP_MDL.ZED");

        private CommandResponse Run(CommandMessage m)
        {
            Assert.True(_reg.TryGet(m.Command, out var h));
            return JsonSerializer.Deserialize<CommandResponse>(h!(m))!;
        }

        // The CLAMP register (0x08) of a texdat's GS tail, or null.
        private static ulong? Clamp(byte[] texdat)
        {
            var h = ZedTextureFile.ParseHeader(texdat);
            for (int o = ZedTextureFile.HeaderSize + h.PixelBytes; o + 16 <= texdat.Length; o += 16)
                if (BitConverter.ToUInt32(texdat, o + 8) == 0x08 && BitConverter.ToUInt32(texdat, o + 12) == 0) return BitConverter.ToUInt64(texdat, o);
            return null;
        }

        private static string RedPng()
        {
            byte[] rgba = new byte[32 * 32 * 4];
            for (int i = 0; i < rgba.Length; i += 4) { rgba[i] = 200; rgba[i + 1] = 20; rgba[i + 2] = 30; rgba[i + 3] = 255; }
            return Convert.ToBase64String(PngEncoder.EncodeRgba(rgba, 32, 32));
        }

        [Fact]
        public void A_skin_goes_into_the_TXR_and_PAL_beside_the_model_archive()
        {
            if (!_ready) return;
            string outDir = Path.Combine(_dir, "out");
            Directory.CreateDirectory(outDir);
            var r = Run(new CommandMessage
            {
                Command = "ZIPPER_CREATE_WEAPON", InputPath = Mdl, OutputPath = Path.Combine(outDir, "WEAP_MDL.ZED"),
                WeaponName = "desert_eagle", WeaponNewName = "raygun", WeaponImageData = RedPng(),
                WeaponMeshEditsJson = "{\"perChunk\":[{\"chunkIndex\":0,\"transform\":{\"scaleZ\":1.5}}]}",
            });
            Assert.True(r.Success, r.Error);
            Assert.Equal(new[] { "WEAP_MDL.ZED", "WEAP_PAL.ZED", "WEAP_TXR.ZED" }, Directory.GetFiles(outDir).Select(Path.GetFileName).OrderBy(n => n));

            // Read back as the game folder it would be: the copy keeps its chains and draws with the new skin.
            foreach (string f in new[] { "WEAP_GEO.ZED" }) File.Copy(Path.Combine(_dir, f), Path.Combine(outDir, f));
            var pkg = ZipperPackage.Open(outDir);
            var model = pkg.FindModel("WEAP_MDL.ZED", "raygun");
            Assert.NotNull(model);
            var decoded = pkg.DecodeModel(model!);
            Assert.All(decoded.Chunks.Where(c => c.TextureName != null), c => Assert.Equal("raygun.tif", c.TextureName));
            Assert.Contains(decoded.Chunks, c => c.HasVertexColors);
            var skin = pkg.DecodeTexture("raygun.tif", "weap");
            Assert.NotNull(skin);
            Assert.Equal(64, skin!.Value.Texture.Header.Width);
            Assert.True(skin.Value.Texture.PaletteResolved);
            // Built on the texture it replaces: it wraps (GS CLAMP) as the template's did.
            ulong? templateClamp = Clamp(pkg.FindTexture("deserteagle.tif", "weap")!.Value.Texdat);
            Assert.NotNull(templateClamp);
            Assert.Equal(templateClamp, Clamp(pkg.FindTexture("raygun.tif", "weap")!.Value.Texdat));

            // Originals untouched: the template still draws with its own texture.
            Assert.All(pkg.DecodeModel(pkg.FindModel("WEAP_MDL.ZED", "desert_eagle")!).Chunks.Where(c => c.TextureName != null),
                c => Assert.Equal("deserteagle.tif", c.TextureName));
        }

        [Fact]
        public void A_dry_run_writes_nothing_and_lists_every_file()
        {
            if (!_ready) return;
            var before = Directory.GetFiles(_dir).ToDictionary(f => f, File.ReadAllBytes);
            var r = Run(new CommandMessage { Command = "ZIPPER_CREATE_WEAPON", InputPath = Mdl, WeaponName = "desert_eagle", WeaponNewName = "raygun", WeaponImageData = RedPng(), DryRun = true });
            Assert.True(r.Success, r.Error);
            using var d = JsonDocument.Parse(r.Data!);
            Assert.Equal(3, d.RootElement.GetProperty("files").GetArrayLength());
            foreach (var (f, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(f));
        }

        [Fact]
        public void A_texture_name_longer_than_the_replaced_one_is_refused()
        {
            if (!_ready) return;
            var r = Run(new CommandMessage
            {
                Command = "ZIPPER_CREATE_WEAPON", InputPath = Mdl, WeaponName = "desert_eagle", WeaponNewName = "raygun",
                WeaponImageData = RedPng(), WeaponTextureEntryName = "a_much_longer_texture_name.tif", DryRun = true,
            });
            Assert.False(r.Success);
            Assert.Contains("at most 15 characters", r.Error);
        }

        [Fact]
        public void The_texture_tool_finds_a_weapon_skin_in_the_TXR_beside_it()
        {
            if (!_ready) return;
            var r = Run(new CommandMessage { Command = "ZIPPER_WEAPON_TEXTURE_PNG", InputPath = Mdl, WeaponName = "desert_eagle" });
            Assert.True(r.Success, r.Error);
            using var d = JsonDocument.Parse(r.Data!);
            Assert.Equal("deserteagle.tif", d.RootElement.GetProperty("textureEntry").GetString());
        }
    }
}
