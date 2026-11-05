using MapBuilder.Server;
using MapBuilder.Server.Services;
using MapBuilder.Tests.Fixtures;
using Xunit;

namespace MapBuilder.Tests;

/// <summary>ASSET_MAP_HUD: the game's HUD radar art and the map's tactical map for the Map Builder's minimap.</summary>
[Collection(MapPackageCollection.Name)]
public sealed class MapHudTests
{
    [Fact]
    public void A_package_without_HUD_art_lists_it_as_missing_and_has_no_tactical_map()
    {
        using var pkg = new SyntheticPackage();
        var b = new Backend();
        string s = b.Open(pkg.WriteZdb());
        try
        {
            var hud = b.Ok(new CommandMessage { Command = "ASSET_MAP_HUD", AssetSession = s });
            Assert.Empty(hud.GetProperty("icons").EnumerateObject());
            Assert.Equal(ZipperAssetService.HudTextures.Length, hud.GetProperty("missing").GetArrayLength());
            Assert.Equal(System.Text.Json.JsonValueKind.Null, hud.GetProperty("tacmap").ValueKind);
        }
        finally { b.Close(s); }
    }

    [Fact]
    public void Tactical_map_placements_are_read_from_the_data_file()
    {
        string file = Path.Combine(Path.GetTempPath(), "mb-tacmaps-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(file, "{ // comment\n \"m51\": { \"world\": [-78, 6926, 270, 4467] }, \"bad\": { \"world\": [1, 2] } }");
        try
        {
            var map = ZipperAssetService.ReadTacmaps(file);
            Assert.Equal(new ZipperAssetService.TacmapPlacement(-78, 6926, 270, 4467), map["M51"]);
            Assert.False(map.ContainsKey("bad"));
            Assert.Empty(ZipperAssetService.ReadTacmaps(file + ".none"));
            // The shipped file has M51.
            Assert.True(ZipperAssetService.ReadTacmaps(Path.Combine(AppContext.BaseDirectory, "data", "tacmaps.json")).ContainsKey("m51"));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void M51_has_the_radar_art_and_its_placed_tactical_map()
    {
        string? path = Environment.GetEnvironmentVariable("MB_MAP_ZDB");
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        var b = new Backend();
        string s = b.Open(path);
        try
        {
            var hud = b.Ok(new CommandMessage { Command = "ASSET_MAP_HUD", AssetSession = s });
            Assert.Equal("m51", hud.GetProperty("map").GetString());
            Assert.Equal(0, hud.GetProperty("missing").GetArrayLength());
            var icons = hud.GetProperty("icons");
            Assert.Equal(128, icons.GetProperty("compass_lo").GetProperty("width").GetInt32());
            Assert.Equal(64, icons.GetProperty("FOV").GetProperty("height").GetInt32());
            Assert.Equal(16, icons.GetProperty("ret_nav_24").GetProperty("width").GetInt32());
            var tacmap = hud.GetProperty("tacmap");
            Assert.Equal("map_m51_1", tacmap.GetProperty("name").GetString());
            var tiles = tacmap.GetProperty("tiles").EnumerateArray().ToArray();
            Assert.Equal(2, tiles.Length);
            Assert.All(tiles, t => Assert.Equal(512, t.GetProperty("width").GetInt32()));
            Assert.All(tiles, t => Assert.StartsWith("iVBORw0KGgo", t.GetProperty("png").GetString()));
            Assert.Equal(new[] { -78.0, 6926, 270, 4467 }, tacmap.GetProperty("world").EnumerateArray().Select(e => e.GetDouble()));
        }
        finally { b.Close(s); }
    }
}
