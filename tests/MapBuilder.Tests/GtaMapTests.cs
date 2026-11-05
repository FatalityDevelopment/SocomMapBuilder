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
    public void Game_values_round_trip_through_the_editors_matrix()
    {
        var rng = new Random(7);
        for (int i = 0; i < 200; i++)
        {
            float qx = (float)rng.NextDouble() * 2 - 1, qy = (float)rng.NextDouble() * 2 - 1, qz = (float)rng.NextDouble() * 2 - 1, qw = (float)rng.NextDouble() * 2 - 1;
            var m = GtaPackage.Transform(100 * i, -50 * i, 3 + i, qx, qy, qz, qw);
            var g = GtaPackage.GameTransform(m);
            Assert.Equal(100f * i, g.X, 2); Assert.Equal(-50f * i, g.Y, 2); Assert.Equal(3f + i, g.Z, 2);
            // q and -q are the same rotation: the matrices agree.
            var back = GtaPackage.Transform(g.X, g.Y, g.Z, g.Qx, g.Qy, g.Qz, g.Qw);
            for (int k = 0; k < 12; k++) Assert.Equal(m[k], back[k], 3);
        }
    }

    [Fact]
    public void Water_polygons_clip_to_an_area_rectangle()
    {
        // A 100 m square of sea at z 2, half inside a rectangle that ends at x = 50.
        float[] square = { 0, 0, 2, 100, 0, 2, 100, 100, 2, 0, 100, 2 };
        var clipped = GtaPackage.ClipToRectangle(square, -10, -10, 50, 200);
        Assert.Equal(12, clipped.Length);
        Assert.Equal(50f, Enumerable.Range(0, 4).Max(i => clipped[3 * i]));
        Assert.All(Enumerable.Range(0, 4), i => Assert.Equal(2f, clipped[3 * i + 2]));
        Assert.Empty(GtaPackage.ClipToRectangle(square, 200, 200, 300, 300));
    }

    [Fact]
    public void Area_clipping_interpolates_water_flow_and_wave_strengths()
    {
        GtaPackage.WaterVertex[] poly =
        {
            new(new(0, 0, 2), new(0, 0), new(1, 0)),
            new(new(10, 0, 4), new(2, -4), new(0, 0.5f)),
            new(new(0, 10, 2), new(0, 0), new(1, 0)),
        };
        var clipped = GtaPackage.ClipWater(poly, -1, -1, 5, 11);
        var shore = Assert.Single(clipped, p => p.Position.X == 5 && p.Position.Y == 0);
        Assert.Equal(3f, shore.Position.Z);
        Assert.Equal(new System.Numerics.Vector2(1, -2), shore.Flow);
        Assert.Equal(new System.Numerics.Vector2(0.5f, 0.25f), shore.Wave);
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

        // DATA/WATER.DAT: the sea and lakes, a few hundred quads and triangles.
        var water = pkg.WaterPolygons();
        _out.WriteLine($"{water.Count} water polygons");
        Assert.True(water.Count > 250);
        // Night-lit windows (tobj, 21:00 to 06:00) are off at noon and on at midnight.
        var windows = all.Select(p => p.Model).OfType<GtaPackage.Model>().Where(m => !pkg.ShowsAt(m, 12)).Distinct().ToList();
        _out.WriteLine($"{windows.Count} models off at noon, e.g. {string.Join(", ", windows.Take(3).Select(m => m.Name))}");
        Assert.NotEmpty(windows);
        Assert.All(windows, m => Assert.True(pkg.ShowsAt(m, 0)));
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
            int batchSize = scene.GetProperty("meshBatchSize").GetInt32();
            string[] grouped = sample.Take(batchSize - 1).Append("GTA3.IMG:__missing_model__").ToArray();
            CommandMessage Batch(string[] sources) => new() { Command = "ASSET_MODEL_MESH_BATCH", AssetSession = session,
                AssetJson = System.Text.Json.JsonSerializer.Serialize(new { sources }) };
            var batch = b.Ok(Batch(grouped));
            Assert.Equal(grouped.Length, batch.GetArrayLength());
            Assert.Equal(grouped, batch.EnumerateArray().Select(x => x.GetProperty("source").GetString()));
            Assert.False(string.IsNullOrEmpty(batch[grouped.Length - 1].GetProperty("error").GetString()));
            b.Fails(Batch(Enumerable.Repeat(sample[0], batchSize + 1).ToArray()));
            foreach (string source in sample)
            {
                int cut = source.IndexOf(':');
                var mesh = b.Ok(new CommandMessage { Command = "ASSET_MODEL_MESH", AssetSession = session, AssetArchive = source[..cut], AssetName = source[(cut + 1)..] });
                Assert.True(mesh.GetProperty("chunks").GetArrayLength() > 0, source);
                int index = Array.IndexOf(grouped, source);
                if (index >= 0) Assert.Equal(mesh.GetRawText(), batch[index].GetProperty("mesh").GetRawText());
            }
            int detail = scene.GetProperty("counts").GetProperty("placement").GetInt32(), distant = scene.GetProperty("counts").GetProperty("distant LOD").GetInt32();
            _out.WriteLine($"LAE2: {detail} placements, {distant} distant LOD blocks, {sample.Count} meshes checked");
            Assert.True(detail > 1000 && distant > 100);
            // Grove Street's zone reaches the sea: its water is one mesh the session builds.
            var water = Assert.Single(instances, i => i.GetProperty("model").GetString() == "water").GetProperty("source").GetString()!;
            var waterMesh = b.Ok(new CommandMessage { Command = "ASSET_MODEL_MESH", AssetSession = session, AssetArchive = water[..water.IndexOf(':')], AssetName = water[(water.IndexOf(':') + 1)..] });
            Assert.True(waterMesh.GetProperty("chunks")[0].GetProperty("triangles").GetString()!.Length > 0);
            var waterChunk = waterMesh.GetProperty("chunks")[0];
            Assert.Equal("MODELS|particle/waterclear256", waterChunk.GetProperty("texture").GetString());
            Assert.Equal(waterChunk.GetProperty("vertexCount").GetInt32() * 4 * sizeof(float),
                waterChunk.GetProperty("waterMotion").GetBytesFromBase64().Length);
            var tex = b.Ok(new CommandMessage { Command = "ASSET_TEXTURE", AssetSession = session, AssetName = waterChunk.GetProperty("texture").GetString() });
            Assert.NotEmpty(tex.GetProperty("png").GetString()!);

            Assert.Equal(12, scene.GetProperty("hour").GetInt32());
            var night = b.Ok(new CommandMessage { Command = "ASSET_MAP_SCENE", AssetSession = session, AssetJson = "{\"hour\":0}" });
            static bool NightLights(System.Text.Json.JsonElement i) =>
                i.GetProperty("model").GetString()!.StartsWith("nitelites", StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(instances, NightLights);
            Assert.Contains(night.GetProperty("instances").EnumerateArray(), NightLights);
            b.Fails(new CommandMessage { Command = "ASSET_MAP_SCENE", AssetSession = session, AssetJson = "{\"hour\":24}" });

            b.Ok(new CommandMessage { Command = "ASSET_MAP_POINTS", AssetSession = session, AssetJson = "{\"mission\":\"vegase\"}" });
            Assert.Equal("vegase", b.Status(session).GetProperty("mission").GetString());
            var vegas = FullScene(b, session).GetProperty("instances").EnumerateArray().Where(i => i.GetProperty("kind").GetString() == "reference").ToList();
            Assert.True(vegas.Count > 2000);
            // Las Venturas lies north-east: game y > 500, so the editors' z < -500.
            Assert.All(vegas, i => Assert.True(i.GetProperty("position")[2].GetSingle() < -400));
            b.Fails(new CommandMessage { Command = "ASSET_MAP_POINTS", AssetSession = session, AssetJson = "{\"mission\":\"nowhere\"}" });

            // The Los Santos overview: only LOD blocks (lod*, laelod* ...), all of them within Los Santos' corner of the map (game x > 0, y < 0).
            b.Ok(new CommandMessage { Command = "ASSET_MAP_POINTS", AssetSession = session, AssetJson = "{\"mission\":\"lod-la\"}" });
            var la = FullScene(b, session).GetProperty("instances").EnumerateArray().Where(i => i.GetProperty("model").GetString() != "water").ToList();
            _out.WriteLine($"Los Santos overview: {la.Count} LOD blocks");
            Assert.True(la.Count > 500);
            Assert.All(la, i => Assert.Contains("lod", i.GetProperty("model").GetString(), StringComparison.OrdinalIgnoreCase));
            Assert.True(la.Count(i => i.GetProperty("position")[0].GetSingle() > 0 && i.GetProperty("position")[2].GetSingle() > 0) > la.Count * 0.95);
        }
        finally { b.Close(session); }
    }

    [Fact]
    public void Retail_distant_model_follows_only_its_sole_owner_and_undo_restores_it()
    {
        string? root = Environment.GetEnvironmentVariable("MAPBUILDER_GTA_DISC");
        if (string.IsNullOrEmpty(root)) return;
        var pkg = GtaPackage.Open(root);
        var links = pkg.Placements.Where(p => p.Zone == "lae2" && p.LodRow >= 0).GroupBy(p => p.LodRow).ToList();
        var b = new Backend();
        string session = b.Open(root);
        try
        {
            foreach (bool shared in new[] { false, true })
            {
                var group = links.First(g => (g.Count() > 1) == shared
                    && g.All(p => !p.IsLod && !p.IsInterior && p.Model != null)
                    && pkg.LodOf(g.First()) is { IsInterior: false, Model: not null });
                var owner = group.First();
                var lod = pkg.LodOf(owner)!;
                static string Id(GtaPackage.Placement p) => $"{p.Zone}:{Path.GetFileNameWithoutExtension(p.File)}:{p.Row}";
                var delta = CodeDesigner.Ps2.FileFormats.Assets.Matrix4.Compose(5, 3, -7, 90);
                var moved = CodeDesigner.Ps2.FileFormats.Assets.Matrix4.Multiply(delta, owner.Matrix);
                Switch("lae2");
                var result = b.Edit(session, new { op = "moveInstance", instance = Id(owner), matrix = moved });
                Assert.Equal(shared, result.GetProperty("warnings").GetArrayLength() > 0);
                Switch("lod-la");
                var expected = shared ? lod.Matrix : CodeDesigner.Ps2.FileFormats.Assets.Matrix4.Multiply(delta, lod.Matrix);
                var actual = ReadLod();
                for (int i = 0; i < 16; ++i) Assert.InRange(Math.Abs(expected[i] - actual[i]), 0f, 0.003f);
                b.Undo(session);
                Assert.Equal(lod.Matrix, ReadLod());
                Assert.Empty(b.Status(session).GetProperty("dirty").EnumerateArray());

                float[] ReadLod() => FullScene(b, session).GetProperty("instances").EnumerateArray()
                    .Single(i => i.GetProperty("instance").GetString() == Id(lod))
                    .GetProperty("matrix").EnumerateArray().Select(n => n.GetSingle()).ToArray();
            }
        }
        finally { b.Close(session); }

        void Switch(string mission) => b.Ok(new CommandMessage { Command = "ASSET_MAP_POINTS", AssetSession = session,
            AssetJson = System.Text.Json.JsonSerializer.Serialize(new { mission }) });
    }

    [Fact]
    public void Retail_moved_placements_undo_and_save_their_text_and_stream_IPLs()
    {
        string? root = Environment.GetEnvironmentVariable("MAPBUILDER_GTA_DISC");
        if (string.IsNullOrEmpty(root)) return;
        // A GTA3.IMG copy is 480 MB (twice, with GTA3_1.IMG): write it where the disc cache lives when that is set.
        string baseDir = Environment.GetEnvironmentVariable("MAPBUILDER_DISC_CACHE") is { Length: > 0 } cache ? cache : Path.GetTempPath();
        string outDir = Path.Combine(baseDir, "gta-map-save-" + Guid.NewGuid().ToString("N")[..8]);
        var b = new Backend();
        string session = b.Open(root);
        try
        {
            b.Ok(new CommandMessage { Command = "ASSET_MAP_POINTS", AssetSession = session, AssetJson = "{\"mission\":\"lae2\"}" });
            var scene = FullScene(b, session).GetProperty("instances").EnumerateArray().ToList();
            var house = scene.First(i => i.GetProperty("model").GetString() == "carlshou1_lae2");
            Assert.True(house.GetProperty("caNode").GetBoolean());
            // A drawn placement from the zone's text IPL too (most text rows are LODs, which the scene hides).
            var text = scene.First(i => i.GetProperty("kind").GetString() == "reference" && !i.GetProperty("instance").GetString()!.Contains("_stream"));
            string houseId = house.GetProperty("instance").GetString()!, textId = text.GetProperty("instance").GetString()!;
            static float[] Matrix(System.Text.Json.JsonElement i) => i.GetProperty("matrix").EnumerateArray().Select(v => v.GetSingle()).ToArray();
            var houseBefore = Matrix(house);
            // CJ's house 5 m east (+x) and a quarter turn about the vertical; the text row 3 m up.
            float[] turned = { 0, 0, -1, 0, 0, 1, 0, 0, 1, 0, 0, 0, houseBefore[12] + 5, houseBefore[13], houseBefore[14], 1 };
            var textPos = new[] { Matrix(text)[12], Matrix(text)[13] + 3, Matrix(text)[14] };
            var edit = b.Edit(session, new { op = "batch", ops = new object[] {
                new { op = "moveInstance", instance = houseId, matrix = turned, followLod = false },
                new { op = "moveInstance", instance = textId, position = textPos, followLod = false } } });
            Assert.Equal(2, edit.GetProperty("changed").GetArrayLength());
            var dirty = b.Status(session).GetProperty("dirty").EnumerateArray().Select(d => d.GetString()!).ToList();
            Assert.Contains("GTA3.IMG/lae2_stream5.ipl", dirty);
            Assert.Contains(dirty, d => d.EndsWith(".IPL", StringComparison.OrdinalIgnoreCase) && d.StartsWith("DATA/MAPS/LA/", StringComparison.OrdinalIgnoreCase));
            b.Undo(session);
            Assert.Empty(b.Status(session).GetProperty("dirty").EnumerateArray());
            var undone = FullScene(b, session).GetProperty("instances").EnumerateArray().First(i => i.GetProperty("instance").GetString() == houseId);
            Assert.Equal(houseBefore, Matrix(undone));
            b.Redo(session);
            var moved = FullScene(b, session).GetProperty("instances").EnumerateArray().First(i => i.GetProperty("instance").GetString() == houseId);
            Assert.Equal(houseBefore[12] + 5, Matrix(moved)[12], 2);

            var saved = b.Ok(new CommandMessage { Command = "ASSET_SAVE", AssetSession = session, AssetJson = System.Text.Json.JsonSerializer.Serialize(new { mode = "zdb", outputPath = outDir }) });
            var written = saved.GetProperty("written").EnumerateArray().Select(w => w.GetString()!).ToList();
            _out.WriteLine(string.Join("\n", written));
            var discFiles = Iso9660.Files(root);

            // The text IPL: one line differs, and it holds the new height; every other line is the disc's.
            string iplPath = written.Single(w => w.EndsWith(".IPL", StringComparison.OrdinalIgnoreCase));
            string discKey = Path.GetRelativePath(outDir, iplPath).Replace('\\', '/');
            var before = System.Text.Encoding.ASCII.GetString(discFiles[discKey].Read()).Split('\n');
            var after = System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(iplPath)).Split('\n');
            Assert.Equal(before.Length, after.Length);
            var changedLines = Enumerable.Range(0, before.Length).Where(i => before[i] != after[i]).ToList();
            Assert.Single(changedLines);
            _out.WriteLine($"{discKey}: {before[changedLines[0]].Trim()}  ->  {after[changedLines[0]].Trim()}");
            var cols = after[changedLines[0]].Split(',').Select(c => c.Trim()).ToArray();
            var oldCols = before[changedLines[0]].Split(',').Select(c => c.Trim()).ToArray();
            Assert.Equal(oldCols[1], cols[1]);
            Assert.Equal(float.Parse(oldCols[5], System.Globalization.CultureInfo.InvariantCulture) + 3, float.Parse(cols[5], System.Globalization.CultureInfo.InvariantCulture), 2);

            // GTA3.IMG (and its twin GTA3_1.IMG): the stream IPL's record holds the new position and turn.
            string img = written.Single(w => Path.GetFileName(w).Equals("GTA3.IMG", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(written, w => Path.GetFileName(w).Equals("GTA3_1.IMG", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(discFiles["MODELS/GTA3.IMG"].Length, new FileInfo(img).Length);
            var copy = new GtaImg(new FileSpan(img, 0, new FileInfo(img).Length));
            var original = new GtaImg(discFiles["MODELS/GTA3.IMG"]);
            var stream = copy.Read("lae2_stream5.ipl")!;
            var source = original.Read("lae2_stream5.ipl")!;
            int row = int.Parse(houseId.Split(':')[2]), at = BitConverter.ToInt32(stream, 28) + 40 * row;
            var diff = Enumerable.Range(0, stream.Length).Where(i => stream[i] != source[i]).ToList();
            Assert.NotEmpty(diff);
            Assert.All(diff, i => Assert.InRange(i, at, at + 27));
            var g = GtaPackage.GameTransform(turned);
            Assert.Equal(g.X, BitConverter.ToSingle(stream, at), 2);
            var stored = GtaPackage.Transform(BitConverter.ToSingle(stream, at), BitConverter.ToSingle(stream, at + 4), BitConverter.ToSingle(stream, at + 8),
                BitConverter.ToSingle(stream, at + 12), BitConverter.ToSingle(stream, at + 16), BitConverter.ToSingle(stream, at + 20), BitConverter.ToSingle(stream, at + 24));
            for (int k = 0; k < 12; k++) Assert.Equal(turned[k], stored[k], 3);
            // A neighbouring entry is untouched.
            Assert.True(copy.Read("lae2_stream4.ipl")!.AsSpan().SequenceEqual(original.Read("lae2_stream4.ipl")));
        }
        finally
        {
            b.Close(session);
            if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
        }
    }

    private static System.Text.Json.JsonElement FullScene(Backend b, string session) =>
        b.Ok(new CommandMessage { Command = "ASSET_MAP_SCENE", AssetSession = session });
}
