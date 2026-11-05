using CodeDesigner.Ps2.FileFormats.Gta;
using MapBuilder.Server;
using MapBuilder.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace MapBuilder.Tests;

// The retail session test shares the disc's asset session (one per path) with the other GTA session tests.
[Collection(MapPackageCollection.Name)]
public sealed class GtaMapTests
{
    private readonly ITestOutputHelper _out;

    public GtaMapTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Placement_rotation_turns_game_z_up_into_the_editors_y_up()
    {
        // No rotation: the game point (10, 20, 30) is the editors' (10, 30, -20).
        var m = GtaPackage.Transform(10, 20, 30, 0, 0, 0, 1);
        Assert.Equal(new[] { 10f, 30f, -20f }, new[] { m[12], m[13], m[14] });
        Assert.Equal(1f, m[0], 5); Assert.Equal(1f, m[5], 5); Assert.Equal(1f, m[10], 5);

        // A turn about the game's vertical Z is a turn about the editors' Y. SA stores the conjugate: the stored
        // quaternion (0, 0, sin 45, cos 45) places the model turned -90 degrees, its +X (game) along game -Y.
        float s = MathF.Sqrt(0.5f);
        var r = GtaPackage.Transform(0, 0, 0, 0, 0, s, s);
        Assert.Equal(1f, r[5], 5);
        // Model +X -> game -Y -> editors +Z.
        Assert.Equal(new[] { 0f, 0f, 1f }, new[] { r[0], r[1], r[2] }.Select(v => MathF.Round(v, 5)).ToArray());
    }

    [Fact]
    public void Retail_placements_resolve_to_models_and_group_into_areas()
    {
        string? root = Environment.GetEnvironmentVariable("MAPBUILDER_GTA_DISC");
        if (string.IsNullOrEmpty(root)) return;
        var pkg = GtaPackage.Open(root);
        var all = pkg.Placements;
        int text = all.Count(p => !p.File.Contains("_stream", StringComparison.OrdinalIgnoreCase));
        int resolved = all.Count(p => p.Model != null);
        double share = (double)resolved / all.Count;
        _out.WriteLine($"{all.Count} placements ({text} text, {all.Count - text} streamed), {resolved} resolved ({share:P2}), {all.Count(p => p.IsLod)} LOD rows, {pkg.MapAreas.Count} areas");
        foreach (var g in all.Where(p => p.Model == null).GroupBy(p => p.Name).OrderByDescending(g => g.Count()).Take(15))
            _out.WriteLine($"  unresolved {g.Key} x{g.Count()}");
        foreach (var a in pkg.MapAreas)
            _out.WriteLine($"  {a.Id,-24} {a.Label,-32} {a.Placements,6} {a.Resolved,6}  x {a.MinX:0}..{a.MaxX:0} z {a.MinZ:0}..{a.MaxZ:0}");

        Assert.True(all.Count > 30000, $"only {all.Count} placements");
        Assert.True(share > 0.97, $"only {share:P2} of placements resolve to a model");
        // Grove Street (game 2495, -1680) is in an LA zone; its cul-de-sac houses resolve.
        var grove = pkg.AreaAt(2495, -1680);
        Assert.NotNull(grove);
        _out.WriteLine($"Grove Street: {grove!.Id} ({grove.Label})");
        Assert.StartsWith("LA", grove.Label, StringComparison.OrdinalIgnoreCase);
        // Placements land near their model's stated LOD (both share a position in SA's data).
        var linked = all.Where(p => p.LodRow >= 0 && p.Model != null).Take(2000).Select(p => (p, lod: pkg.LodOf(p))).Where(x => x.lod != null).ToList();
        Assert.NotEmpty(linked);
        double near = linked.Count(x => MathF.Abs(x.p.X - x.lod!.X) < 300 && MathF.Abs(x.p.Y - x.lod.Y) < 300) / (double)linked.Count;
        _out.WriteLine($"LOD links within 300 m: {near:P1} of {linked.Count}");
        Assert.True(near > 0.95);
    }

    [Fact]
    public void Retail_map_builder_opens_on_grove_street_and_switches_areas()
    {
        string? root = Environment.GetEnvironmentVariable("MAPBUILDER_GTA_DISC");
        if (string.IsNullOrEmpty(root)) return;
        var b = new Backend();
        string session = b.Open(root);
        try
        {
            var status = b.Status(session);
            Assert.Equal("map", status.GetProperty("packageKind").GetString());
            Assert.Equal("lae2", status.GetProperty("mission").GetString());
            Assert.True(status.GetProperty("missions").GetArrayLength() > 30);

            var scene = FullScene(b, session);
            var instances = scene.GetProperty("instances").EnumerateArray().ToList();
            Assert.Contains(instances, i => i.GetProperty("model").GetString() == "carlshou1_lae2");
            // Every drawn source names a model the session's ASSET_MODEL_MESH can build.
            var sample = instances.Select(i => i.GetProperty("source").GetString()!).Distinct().Take(25).ToList();
            foreach (string source in sample)
            {
                int cut = source.IndexOf(':');
                var mesh = b.Ok(new CommandMessage { Command = "ASSET_MODEL_MESH", AssetSession = session, AssetArchive = source[..cut], AssetName = source[(cut + 1)..] });
                Assert.True(mesh.GetProperty("chunks").GetArrayLength() > 0, source);
            }
            int detail = scene.GetProperty("counts").GetProperty("placement").GetInt32(), distant = scene.GetProperty("counts").GetProperty("distant LOD").GetInt32();
            _out.WriteLine($"LAE2: {detail} placements, {distant} distant LOD blocks, {sample.Count} meshes checked");
            Assert.True(detail > 1000 && distant > 100);

            b.Ok(new CommandMessage { Command = "ASSET_MAP_POINTS", AssetSession = session, AssetJson = "{\"mission\":\"vegase\"}" });
            Assert.Equal("vegase", b.Status(session).GetProperty("mission").GetString());
            var vegas = FullScene(b, session).GetProperty("instances").EnumerateArray().Where(i => i.GetProperty("kind").GetString() == "reference").ToList();
            Assert.True(vegas.Count > 2000);
            // Las Venturas lies north-east: game y > 500, so the editors' z < -500.
            Assert.All(vegas, i => Assert.True(i.GetProperty("position")[2].GetSingle() < -400));
            b.Fails(new CommandMessage { Command = "ASSET_MAP_POINTS", AssetSession = session, AssetJson = "{\"mission\":\"nowhere\"}" });
        }
        finally { b.Close(session); }
    }

    private static System.Text.Json.JsonElement FullScene(Backend b, string session) =>
        b.Ok(new CommandMessage { Command = "ASSET_MAP_SCENE", AssetSession = session });
}
