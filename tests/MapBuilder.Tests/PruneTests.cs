using System.Text.Json;
using MapBuilder.Server;
using MapBuilder.Tests.Fixtures;
using Xunit;

namespace MapBuilder.Tests;

/// <summary>pruneAssets: unplaced props of the map's own archives go, used ones and everything shared stay.</summary>
public sealed class PruneTests
{
    private static List<string> Models(Backend b, string session) =>
        b.Ok(new CommandMessage { Command = "ASSET_LIST_MODELS", AssetSession = session }).EnumerateArray().Select(m => m.GetProperty("name").GetString()!).ToList();

    [Fact]
    public void A_prop_nothing_places_is_removed_and_undo_brings_it_back()
    {
        using var pkg = new SyntheticPackage();
        var b = new Backend();
        string session = b.Open(pkg.Folder);

        // crate is placed and tree is clutter: nothing to prune.
        var none = b.Edit(session, new { op = "pruneAssets" });
        Assert.Empty(none.GetProperty("models").EnumerateArray());

        b.Edit(session, new { op = "deleteClutter", clutterIndex = 1 });
        b.Edit(session, new { op = "deleteClutter", clutterIndex = 0 });
        var kept = b.Edit(session, new { op = "pruneAssets", keep = new[] { "tree" } });
        Assert.Empty(kept.GetProperty("models").EnumerateArray());

        var pruned = b.Edit(session, new { op = "pruneAssets" });
        Assert.Equal("tree", Assert.Single(pruned.GetProperty("models").EnumerateArray()).GetProperty("name").GetString());
        Assert.DoesNotContain("tree", Models(b, session));
        Assert.Contains("crate", Models(b, session));
        b.Undo(session);
        Assert.Contains("tree", Models(b, session));
    }

    /// <summary>On a disc: MP2 cleared and pruned loses its rig props and their textures, keeps the world, sky and characters, and still validates.</summary>
    [Fact]
    public void Disc_mp2_cleared_then_pruned_still_validates()
    {
        string? disc = Environment.GetEnvironmentVariable("MAPBUILDER_DISC");
        string? zdb = string.IsNullOrEmpty(disc) ? null
            : new[] { Path.Combine(disc, "RUN", "MP2.ZDB"), Path.Combine(disc, "MP2.ZDB") }.FirstOrDefault(File.Exists);
        if (zdb == null) return;
        // A copy of its own: sessions are shared per path, and another disc test edits MP2 too.
        string copy = Path.Combine(Path.GetTempPath(), $"mb-{Guid.NewGuid():N}", "MP2.ZDB");
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        File.Copy(zdb, copy);
        var b = new Backend();
        string session = b.Open(copy);
        int before = Models(b, session).Count;
        b.Edit(session, new { op = "clearMap" });
        var r = b.Edit(session, new { op = "pruneAssets" });
        int models = r.GetProperty("models").GetArrayLength(), textures = r.GetProperty("textures").GetArrayLength();
        Assert.True(models > 20, $"{models} models pruned");
        Assert.True(textures > 10, $"{textures} textures pruned");
        Assert.True(r.GetProperty("bytes").GetInt32() > 100_000);
        var after = Models(b, session);
        Assert.Equal(before - models, after.Count);
        Assert.Contains("worldmodel", after);
        Assert.All(r.GetProperty("models").EnumerateArray(), m => Assert.StartsWith("MP2_", m.GetProperty("archive").GetString()));
        Assert.All(r.GetProperty("textures").EnumerateArray(), t => Assert.Contains("MP2", t.GetProperty("archive").GetString()));

        var v = b.Ok(new CommandMessage { Command = "ASSET_VALIDATE", AssetSession = session });
        Assert.True(v.GetProperty("ok").GetBoolean(), v.GetRawText()[..Math.Min(600, v.GetRawText().Length)]);
        // The world still draws: its mesh decodes with textures that are still in the package.
        var world = b.Ok(new CommandMessage { Command = "ASSET_MODEL_MESH", AssetSession = session, AssetArchive = "WORL_MDL.ZED", AssetName = "worldmodel", AssetJson = "{\"arrays\":false}" });
        Assert.True(world.GetProperty("chunks").GetArrayLength() > 0);
        // Nothing left to prune.
        Assert.Empty(b.Edit(session, new { op = "pruneAssets" }).GetProperty("models").EnumerateArray());
    }
}
