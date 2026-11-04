using System.Text.Json;
using MapBuilder.Server;
using MapBuilder.Server.Services;
using MapBuilder.Tests.Fixtures;
using Xunit;

namespace MapBuilder.Tests
{
    /// <summary>
    /// Character presets: the shipped ones list, a saved one round-trips, and applying one is a single edit. The apply
    /// tests need the real map (MB_MAP_ZDB = M51.ZDB); they share its collection, which also keeps the preset folder
    /// (a static) to one test at a time.
    /// </summary>
    [Collection(MapPackageCollection.Name)]
    public sealed class CharacterPresetTests : IDisposable
    {
        private const string Seal = "MESH_seal_A_woodland_LO";
        private readonly Backend _b = new();
        private readonly List<string> _sessions = new();
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "mb-presets-" + Guid.NewGuid().ToString("N"));
        private readonly string _was = ZipperAssetService.CharacterPresetFolder;

        public CharacterPresetTests() => ZipperAssetService.CharacterPresetFolder = _folder;

        public void Dispose()
        {
            foreach (string s in _sessions) _b.Close(s);
            ZipperAssetService.CharacterPresetFolder = _was;
            if (Directory.Exists(_folder)) Directory.Delete(_folder, true);
        }

        private JsonElement List() => _b.Ok(new CommandMessage { Command = "ASSET_LIST_CHARACTER_PRESETS" });

        [Fact]
        public void The_shipped_presets_list_and_a_saved_one_round_trips()
        {
            var names = List().GetProperty("presets").EnumerateArray().Select(p => p.GetProperty("name").GetString()).ToList();
            Assert.Contains("Heavy build", names);
            Assert.Contains("Plate carrier", names);

            var ops = new object[] { new { op = "scaleBones", archive = "X.ZED", model = "MESH_x", bones = new { head = 0.9 } } };
            var saved = _b.Ok(new CommandMessage { Command = "ASSET_SAVE_CHARACTER_PRESET", AssetJson = JsonSerializer.Serialize(new { name = "Small head", description = "test", ops }) });
            Assert.Equal(1, saved.GetProperty("ops").GetInt32());
            var full = _b.Ok(new CommandMessage { Command = "ASSET_LIST_CHARACTER_PRESETS", AssetName = "Small head" });
            var op = full.GetProperty("ops")[0];
            Assert.False(op.TryGetProperty("archive", out _));   // filled in when applied
            Assert.False(op.TryGetProperty("model", out _));

            Assert.Contains("exists", _b.Fails(new CommandMessage { Command = "ASSET_SAVE_CHARACTER_PRESET", AssetJson = JsonSerializer.Serialize(new { name = "Small head", ops }) }));
            Assert.Contains("a preset holds", _b.Fails(new CommandMessage { Command = "ASSET_SAVE_CHARACTER_PRESET", AssetJson = JsonSerializer.Serialize(new { name = "Map", ops = new[] { new { op = "addClutter", model = "x" } } }) }));
            Assert.Contains("ships with the app", _b.Fails(new CommandMessage { Command = "ASSET_DELETE_CHARACTER_PRESET", AssetName = "Heavy build" }));
            _b.Ok(new CommandMessage { Command = "ASSET_DELETE_CHARACTER_PRESET", AssetName = "Small head" });
            Assert.DoesNotContain("Small head", List().GetProperty("presets").EnumerateArray().Select(p => p.GetProperty("name").GetString()));
        }

        [Fact]
        public void A_body_preset_and_two_gear_presets_apply_as_one_undo_step_each()
        {
            string? path = Environment.GetEnvironmentVariable("MB_MAP_ZDB");
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            string s = _b.Open(path);
            _sessions.Add(s);
            byte[] mdl = Backend.Bytes(s, "CLIB_MDL.ZED");

            _b.Edit(s, new { op = "applyCharacterPreset", preset = "Heavy build", archive = "CLIB_MDL.ZED", model = Seal });
            Assert.NotEqual(mdl, Backend.Bytes(s, "CLIB_MDL.ZED"));
            _b.Undo(s);
            Assert.Equal(mdl, Backend.Bytes(s, "CLIB_MDL.ZED"));

            // The carrier brings its texture; the rucksack finds it there and skips adding it again.
            _b.Edit(s, new { op = "applyCharacterPreset", preset = "Plate carrier", archive = "CLIB_MDL.ZED", model = Seal });
            _b.Edit(s, new { op = "applyCharacterPreset", preset = "Rucksack", archive = "CLIB_MDL.ZED", model = Seal });
            var chunks = _b.Ok(new CommandMessage { Command = "ASSET_DESCRIBE_MODEL", AssetSession = s, AssetArchive = "CLIB_MDL.ZED", AssetName = Seal })
                .GetProperty("chunks").EnumerateArray().Count(c => c.GetProperty("texture").GetString() == "coyotegear1.tif");
            Assert.True(chunks > 10, $"{chunks} gear chunks");
            _b.Undo(s);
            _b.Undo(s);
            Assert.Equal(mdl, Backend.Bytes(s, "CLIB_MDL.ZED"));
        }
    }
}
