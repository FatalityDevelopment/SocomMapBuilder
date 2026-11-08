using System.Text.Json;
using CodeDesigner.Ps2.FileFormats.Zipper.Package;
using MapBuilder.Server;
using MapBuilder.Server.Services;
using MapBuilder.Tests.Fixtures;
using Xunit;

namespace MapBuilder.Tests;

/// <summary>bakeLighting: the map's GlobalLighting into vertex colours, checked against MP2's prelit crane.</summary>
public sealed class LightingTests
{
    [Fact]
    public void Disc_mp2s_crane_re_bakes_to_its_retail_colours()
    {
        string? disc = Environment.GetEnvironmentVariable("MAPBUILDER_DISC");
        string? zdb = string.IsNullOrEmpty(disc) ? null
            : new[] { Path.Combine(disc, "RUN", "MP2.ZDB"), Path.Combine(disc, "MP2.ZDB") }.FirstOrDefault(File.Exists);
        if (zdb == null) return;
        string copy = Path.Combine(Path.GetTempPath(), $"mb-{Guid.NewGuid():N}", "MP2.ZDB");
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        File.Copy(zdb, copy);
        var b = new Backend();
        string session = b.Open(copy);
        var pkg = ZipperAssetService.Find(session)!.Package;

        var lighting = MeshLighting.OfMap(pkg)!;
        Assert.Equal(new[] { 15f, 15, 17 }, lighting.Ambient);
        Assert.Equal(3, lighting.Lights.Count);
        Assert.Equal(new[] { 160f, 160, 163 }, lighting.Lights[0].Rgb);
        Assert.Equal(new[] { -0.81f, -0.2f, 0.55f }, lighting.Lights[0].Dir);

        var crane = pkg.Models().First(m => m.Name == "crane");
        byte[] Colours() => ZipperModelMesh.Build(pkg, crane).Chunks.Where(c => c.Colors != null).SelectMany(c => c.Colors!).ToArray();
        byte[] before = Colours();
        var r = b.Edit(session, new { op = "bakeLighting", archive = crane.Archive, model = "crane" });
        Assert.Equal(516, r.GetProperty("vertices").GetInt32());
        byte[] after = Colours();
        Assert.Equal(before.Length, after.Length);
        // Red is the light itself (the crane's yellow paint keeps all of it); green and blue are the paint's share.
        int worst = Enumerable.Range(0, before.Length / 4).Max(v => Math.Abs(before[v * 4] - after[v * 4]));
        Assert.True(worst <= 1, $"a red value moved by {worst}");
        Assert.All(Enumerable.Range(0, before.Length / 4), v => Assert.Equal(before[v * 4 + 3], after[v * 4 + 3]));
        // Paint only takes light away: no retail channel is brighter than the light (grey parts keep all of it).
        Assert.All(Enumerable.Range(0, before.Length), i => Assert.True(before[i] <= after[i] + 1 || i % 4 == 3));
        // Tinted with the yellow paint, the yellow parts' green follows too.
        b.Undo(session);
        b.Edit(session, new { op = "bakeLighting", archive = crane.Archive, model = "crane", tint = new[] { 1f, 0.86f, 0.29f } });
        byte[] tinted = Colours();
        var yellow = Enumerable.Range(0, before.Length / 4).Where(v => before[v * 4] > 20 && before[v * 4 + 2] < before[v * 4] / 2).ToList();
        Assert.True(yellow.Count > 50);
        int green = yellow.Max(v => Math.Abs(before[v * 4 + 1] - tinted[v * 4 + 1]));
        Assert.True(green <= 3, $"a yellow part's green moved by {green}");
    }

    [Fact]
    public void A_map_without_lighting_says_so()
    {
        using var pkg = new SyntheticPackage();
        var b = new Backend();
        string session = b.Open(pkg.Folder);
        Assert.Contains("GlobalLighting", b.EditFails(session, new { op = "bakeLighting", archive = "TST_MDL.ZED", model = "crate" }));
    }
}
