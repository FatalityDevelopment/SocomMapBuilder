using System.Text.Json;
using MapBuilder.Server;
using MapBuilder.Server.Services;
using MapBuilder.Tests.Fixtures;
using Xunit;

namespace MapBuilder.Tests
{
    /// <summary>Prefabs: saved from a selection, listed, placed as one undoable edit, deleted.</summary>
    [Collection(PrefabFolderCollection.Name)]
    public sealed class PrefabTests : IDisposable
    {
        private readonly SyntheticPackage _pkg = new();
        private readonly Backend _b = new();
        private readonly string _s;
        private readonly string _previousFolder;

        public PrefabTests()
        {
            _previousFolder = ZipperAssetService.PrefabFolder;
            ZipperAssetService.PrefabFolder = Path.Combine(Path.GetDirectoryName(_pkg.Folder)!, "prefabs");
            _s = _b.Open(_pkg.Folder);
        }

        public void Dispose()
        {
            _b.Close(_s);
            ZipperAssetService.PrefabFolder = _previousFolder;
            _pkg.Dispose();
        }

        private JsonElement Save(object args) =>
            _b.Ok(new CommandMessage { Command = "ASSET_SAVE_PREFAB", AssetSession = _s, AssetJson = JsonSerializer.Serialize(args) });

        private int GeoIndex(string instance) => _b.Scene(_s).GetProperty("instances").EnumerateArray()
            .Single(i => i.GetProperty("instance").GetString() == instance).GetProperty("geoIndex").GetInt32();

        private List<JsonElement> Kind(string kind) =>
            _b.Scene(_s).GetProperty("instances").EnumerateArray().Where(i => i.GetProperty("kind").GetString() == kind).ToList();

        private static float[] Pos(JsonElement i) => i.GetProperty("position").EnumerateArray().Select(x => x.GetSingle()).ToArray();

        [Fact]
        public void Save_then_place_copies_the_group_around_the_new_position()
        {
            // crate_1 at (10, 0, 20) and clutter #0 at (1, 0, 2) around origin (0, 0, 0).
            var saved = Save(new { name = "camp", geoIndices = new[] { GeoIndex("crate_1") }, clutterIndices = new[] { 0 }, origin = new[] { 0f, 0, 0 } });
            Assert.Equal(2, saved.GetProperty("items").GetInt32());

            var list = _b.Ok(new CommandMessage { Command = "ASSET_LIST_PREFABS" });
            Assert.Equal("camp", list.GetProperty("prefabs")[0].GetProperty("name").GetString());

            byte[] geo = Backend.Bytes(_s, "TST_GEO.ZED"), clutter = Backend.Bytes(_s, "CLUTTER.ZAR");
            _b.Edit(_s, new { op = "placePrefab", prefab = "camp", position = new[] { 100f, 0, 100 }, yaw = 90f });

            // Yaw 90° (Matrix4.Compose) takes local (x, z) to (z, -x) around the new origin.
            var refs = Kind("reference");
            Assert.Equal(3, refs.Count);
            float[] p = Pos(refs.Single(r => Pos(r)[0] > 50));
            Assert.Equal(120f, p[0], 3);
            Assert.Equal(90f, p[2], 3);
            float[] t = Pos(Kind("clutter").Single(c => Pos(c)[0] > 50));
            Assert.Equal(102f, t[0], 3);
            Assert.Equal(99f, t[2], 3);

            // One undo takes the whole prefab back.
            _b.Undo(_s);
            Assert.Equal(geo, Backend.Bytes(_s, "TST_GEO.ZED"));
            Assert.Equal(clutter, Backend.Bytes(_s, "CLUTTER.ZAR"));
        }

        [Fact]
        public void The_default_origin_is_the_centre_on_the_ground()
        {
            var saved = Save(new { name = "pair", clutterIndices = new[] { 0, 1 } });
            Assert.Equal(new[] { 2f, 0, 3 }, saved.GetProperty("origin").EnumerateArray().Select(x => x.GetSingle()).ToArray());
            var prefab = _b.Ok(new CommandMessage { Command = "ASSET_LIST_PREFABS", AssetName = "pair" });
            var first = prefab.GetProperty("items")[0].GetProperty("matrix").EnumerateArray().Select(x => x.GetSingle()).ToArray();
            Assert.Equal(-1f, first[12], 4);
            Assert.Equal(-1f, first[14], 4);
        }

        [Fact]
        public void Placing_a_prefab_whose_model_is_missing_changes_nothing()
        {
            Save(new { name = "trees", clutterIndices = new[] { 0, 1 } });
            string file = Path.Combine(ZipperAssetService.PrefabFolder, "trees.prefab.json");
            File.WriteAllText(file, File.ReadAllText(file).Replace("\"tree\"", "\"ghost\"", StringComparison.Ordinal));
            byte[] clutter = Backend.Bytes(_s, "CLUTTER.ZAR");
            Assert.Contains("No model 'ghost'", _b.EditFails(_s, new { op = "placePrefab", prefab = "trees", position = new[] { 5f, 0, 5 } }));
            Assert.Equal(clutter, Backend.Bytes(_s, "CLUTTER.ZAR"));
        }

        [Fact]
        public void Names_items_and_overwrites_are_checked()
        {
            Assert.Contains("must be 1-63", _b.Fails(new CommandMessage { Command = "ASSET_SAVE_PREFAB", AssetSession = _s, AssetJson = "{\"name\":\"../evil\",\"clutterIndices\":[0]}" }));
            Assert.Contains("at least one item", _b.Fails(new CommandMessage { Command = "ASSET_SAVE_PREFAB", AssetSession = _s, AssetJson = "{\"name\":\"empty\"}" }));
            Assert.Contains("not a placed reference", _b.Fails(new CommandMessage { Command = "ASSET_SAVE_PREFAB", AssetSession = _s, AssetJson = "{\"name\":\"x\",\"geoIndices\":[0]}" }));
            Save(new { name = "once", clutterIndices = new[] { 0 } });
            Assert.Contains("exists", _b.Fails(new CommandMessage { Command = "ASSET_SAVE_PREFAB", AssetSession = _s, AssetJson = "{\"name\":\"once\",\"clutterIndices\":[1]}" }));
            Save(new { name = "once", clutterIndices = new[] { 1 }, replace = true });
            Assert.Contains("No prefab", _b.EditFails(_s, new { op = "placePrefab", prefab = "nope", position = new[] { 0f, 0, 0 } }));
        }

        [Fact]
        public void Delete_removes_the_file()
        {
            Save(new { name = "gone", clutterIndices = new[] { 0 } });
            _b.Ok(new CommandMessage { Command = "ASSET_DELETE_PREFAB", AssetName = "gone" });
            Assert.Empty(_b.Ok(new CommandMessage { Command = "ASSET_LIST_PREFABS" }).GetProperty("prefabs").EnumerateArray());
        }

        [Fact]
        public void The_tools_save_list_and_place()
        {
            var mcp = new McpServer(_b.Registry, new UiBridge());
            JsonElement Call(string tool, object args)
            {
                using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = tool, arguments = args } }));
                return JsonDocument.Parse(JsonSerializer.Serialize(mcp.Dispatch(doc.RootElement).GetAwaiter().GetResult())).RootElement.GetProperty("result").Clone();
            }
            string Text(JsonElement r) => r.GetProperty("content")[0].GetProperty("text").GetString()!;

            var saved = Call("assets_save_prefab", new { session = _s, reason = "r", name = "tool", clutterIndices = new[] { 0 } });
            Assert.False(saved.GetProperty("isError").GetBoolean(), Text(saved));
            Assert.Contains("tool", Text(Call("assets_list_prefabs", new { reason = "r" })));
            var placed = Call("assets_edit", new { session = _s, reason = "r", op = new { op = "placePrefab", prefab = "tool", position = new[] { 9f, 0, 9 } } });
            Assert.False(placed.GetProperty("isError").GetBoolean(), Text(placed));
            Assert.Equal(3, Kind("clutter").Count);
        }
    }

    /// <summary>The prefab tests change the static prefab folder, so they do not run beside each other.</summary>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class PrefabFolderCollection
    {
        public const string Name = "prefab folder";
    }
}
