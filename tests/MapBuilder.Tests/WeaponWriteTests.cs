using System.Text.Json;
using CodeDesigner.Ps2.FileFormats.Zipper.ZED;
using MapBuilder.Server;
using MapBuilder.Server.Services;
using Xunit;

namespace MapBuilder.Tests
{
    /// <summary>ZIPPER_WRITE_WEAPON (the last step of weapons_create): new entries only, never over a name in use; dry run writes nothing.</summary>
    public sealed class WeaponWriteTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "mb-weap-" + Guid.NewGuid().ToString("N"));
        private readonly string _zed;
        private readonly CommandRegistry _reg = new();

        public WeaponWriteTests()
        {
            Directory.CreateDirectory(_dir);
            _zed = Path.Combine(_dir, "WEAP_MDL.ZED");
            var c = ZedContainer.CreateNew();
            c.AddNode("gun", new byte[] { 1, 2, 3, 4 });
            c.AddNode("gun.tif", new byte[] { 5, 6, 7, 8 });
            c.RootValue = 2;
            File.WriteAllBytes(_zed, c.ToBytes());
            new ZipperWeaponsService().Register(_reg);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }

        private CommandResponse Write(string newName, string? output = null, bool dryRun = false, string? texture = null, string? textureEntry = null)
        {
            Assert.True(_reg.TryGet("ZIPPER_WRITE_WEAPON", out var h));
            return JsonSerializer.Deserialize<CommandResponse>(h!(new CommandMessage
            {
                Command = "ZIPPER_WRITE_WEAPON", InputPath = _zed, OutputPath = output, WeaponNewName = newName,
                WeaponModelData = Convert.ToBase64String(new byte[] { 9, 9, 9, 9 }), WeaponTextureData = texture, DryRun = dryRun,
                WeaponTextureEntryName = textureEntry,
            }))!;
        }

        private static string[] Names(string path) => ZedContainer.FromBytes(File.ReadAllBytes(path)).Nodes.Select(n => n.Name).ToArray();

        [Fact]
        public void Adds_new_entries_to_a_copy()
        {
            string copy = Path.Combine(_dir, "OUT.ZED");
            var r = Write("gun2", copy, texture: Convert.ToBase64String(new byte[] { 1, 1, 1, 1 }));
            Assert.True(r.Success, r.Error);
            Assert.Equal(new[] { "gun", "gun.tif", "gun2", "gun2.tif" }, Names(copy));
            Assert.Equal(new[] { "gun", "gun.tif" }, Names(_zed));
        }

        [Fact]
        public void Refuses_a_name_already_in_the_archive()
        {
            byte[] before = File.ReadAllBytes(_zed);
            var model = Write("gun");
            Assert.False(model.Success);
            Assert.Contains("already has an entry named gun", model.Error);
            // A new model whose texture would take an existing texture's name.
            var texture = Write("gun2", texture: Convert.ToBase64String(new byte[] { 1 }), textureEntry: "gun.tif");
            Assert.False(texture.Success);
            Assert.Contains("gun.tif", texture.Error);
            Assert.Equal(before, File.ReadAllBytes(_zed));
        }

        [Fact]
        public void A_dry_run_writes_nothing()
        {
            byte[] before = File.ReadAllBytes(_zed);
            var r = Write("gun2", dryRun: true);
            Assert.True(r.Success, r.Error);
            using var d = JsonDocument.Parse(r.Data!);
            Assert.True(d.RootElement.GetProperty("dryRun").GetBoolean());
            Assert.Equal(before, File.ReadAllBytes(_zed));
            Assert.False(File.Exists(_zed + ".bak"));
        }
    }
}
