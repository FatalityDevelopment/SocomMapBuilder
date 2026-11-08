using System.Text.Json;
using CodeDesigner.Ps2.FileFormats.Zipper.Geo;
using CodeDesigner.Ps2.FileFormats.Zipper.MPS;
using CodeDesigner.Ps2.FileFormats.Zipper.ZDB;
using CodeDesigner.Ps2.FileFormats.Zipper.ZED;
using MapBuilder.Server;
using MapBuilder.Tests.Fixtures;
using Xunit;

namespace MapBuilder.Tests;

/// <summary>
/// Map foundations: GEO <c>di</c> collision faces (written per model to its definition and every placed reference,
/// cleared from the world's own nodes) and AIMAPS points added and removed. Synthetic data shaped like MP2; the
/// retail check needs MAPBUILDER_DISC.
/// </summary>
public sealed class CollisionTests
{
    private static string Hex(byte[] b) => Convert.ToHexString(b);

    [Fact]
    public void A_box_matches_the_retail_container_profile_and_faces_outward()
    {
        // MP2's container05: material 7, flags 0x64 0x40; its top 0x13 (4 corners, looks up), its sides 0x12.
        var faces = GeoCollision.Box(new[] { -40f, 0, -10 }, new[] { 50f, 30, 30 }, new GeoCollision.Profile(7, 0, 0x64, 0x40));
        Assert.Equal(5, faces.Count);
        Assert.Equal("000000000700000013644044", Hex(GeoCollision.Params(faces[0])));
        Assert.All(faces.Skip(1), f => Assert.Equal("000000000700000012644044", Hex(GeoCollision.Params(f))));
        float[] centre = { 5, 15, 10 };
        foreach (var f in faces)
        {
            var n = f.Normal();
            float cx = 0, cy = 0, cz = 0;
            for (int i = 0; i < f.Count; i++) { cx += f.Points[i * 3]; cy += f.Points[i * 3 + 1]; cz += f.Points[i * 3 + 2]; }
            float dot = n[0] * (cx / f.Count - centre[0]) + n[1] * (cy / f.Count - centre[1]) + n[2] * (cz / f.Count - centre[2]);
            Assert.True(dot > 0, "every face points out of the box");
        }
        Assert.Equal(6, GeoCollision.Box(new[] { 0f, 0, 0 }, new[] { 1f, 1, 1 }, GeoCollision.Profile.Default, bottom: true).Count);
        Assert.Throws<ArgumentException>(() => GeoCollision.Box(new[] { 0f, 0, 0 }, new[] { 1f, 0, 1 }, GeoCollision.Profile.Default));
    }

    [Fact]
    public void Faces_round_trip_through_a_region()
    {
        var faces = GeoCollision.Box(new[] { 0f, 0, 0 }, new[] { 80f, 40, 80 }, new GeoCollision.Profile(3, 0x22, 0x64, 0));
        var c = ZedContainer.CreateNew();
        c.AddNode("models", Array.Empty<byte>(), 1);
        c.AddNode("slab", Array.Empty<byte>(), 1);
        c.AddNode("nparams", new byte[96]);
        ZedTree.AppendChild(c, 1, GeoCollision.Region(faces)!);
        c.RootValue = 1;
        var node = GeoScene.Parse(c.ToBytes()).FindAll("slab").Single();
        var read = GeoCollision.Read(node);
        Assert.Equal(faces.Count, read.Count);
        Assert.Equal(faces.Select(f => f.Points), read.Select(f => f.Points));
        Assert.All(read, f => Assert.Equal(new GeoCollision.Profile(3, 0x22, 0x64, 0), f.Profile));
        Assert.Null(GeoCollision.Region(Array.Empty<GeoCollision.Face>()));
    }

    [Fact]
    public void SetCollision_gives_every_reference_the_faces_and_undoes()
    {
        using var pkg = new SyntheticPackage();
        var b = new Backend();
        string session = b.Open(pkg.Folder);
        JsonElement Collision(object? args = null) => b.Ok(new CommandMessage { Command = "ASSET_MAP_COLLISION", AssetSession = session, AssetJson = JsonSerializer.Serialize(args ?? new { }) });

        Assert.Equal(2, Collision().GetProperty("withoutCollisionCount").GetInt32());
        int NoCollision() => b.Ok(new CommandMessage { Command = "ASSET_VALIDATE", AssetSession = session }).GetProperty("problems").EnumerateArray()
            .Count(p => p.GetProperty("code").GetString() == "no-collision");
        Assert.Equal(2, NoCollision());
        var set =b.Edit(session, new { op = "setCollision", model = "crate", boxes = new[] { new { min = new[] { -1f, 0, -1 }, max = new[] { 1f, 2, 1 } } }, material = 7 });
        Assert.Equal(5, set.GetProperty("faces").GetInt32());
        Assert.Equal(2, set.GetProperty("references").GetInt32());

        var after = Collision();
        Assert.Equal(0, after.GetProperty("withoutCollisionCount").GetInt32());
        Assert.Equal(0, NoCollision());
        Assert.Equal(10, after.GetProperty("props").GetProperty("faces").GetInt32());
        // crate_1 stands at (10, 0, 20): its top face lies at y 2 around it, in world space.
        var top = after.GetProperty("faces").EnumerateArray().First(f => f.GetProperty("node").GetString() == "crate_1" && f.GetProperty("up").GetBoolean());
        var pts = top.GetProperty("points").EnumerateArray().Select(v => v.GetSingle()).ToArray();
        Assert.Equal(new[] { 9f, 2, 21, 11, 2, 21, 11, 2, 19, 9, 2, 19 }, pts);
        Assert.Equal(7u, top.GetProperty("material").GetUInt32());
        Assert.Equal(1, Collision(new { area = new[] { 0f, 0, 50, 50 } }).GetProperty("faces").EnumerateArray().Select(f => f.GetProperty("node").GetString()).Distinct().Count());

        b.Edit(session, new { op = "setCollision", model = "crate", shape = "none" });
        Assert.Equal(2, Collision().GetProperty("withoutCollisionCount").GetInt32());
        b.Undo(session);
        Assert.Equal(0, Collision().GetProperty("withoutCollisionCount").GetInt32());
        Assert.Contains("box, mesh, definition or none", b.EditFails(session, new { op = "setCollision", model = "crate", shape = "sphere" }));
    }

    [Fact]
    public void PlaceInstance_does_not_copy_the_templates_collision()
    {
        using var pkg = new SyntheticPackage();
        var b = new Backend();
        string session = b.Open(pkg.Folder);
        b.Edit(session, new { op = "setCollision", model = "crate", boxes = new[] { new { min = new[] { -1f, 0, -1 }, max = new[] { 1f, 2, 1 } } } });
        // tree has no definition with faces: placed from crate_1, it must not stand there as a solid crate.
        b.Edit(session, new { op = "placeInstance", model = "tree", position = new[] { 50f, 0, 50 } });
        var c = b.Ok(new CommandMessage { Command = "ASSET_MAP_COLLISION", AssetSession = session, AssetJson = "{}" });
        var bare = Assert.Single(c.GetProperty("withoutCollision").EnumerateArray());
        Assert.Equal("tree", bare.GetProperty("model").GetString());
        Assert.Equal(10, c.GetProperty("props").GetProperty("faces").GetInt32());
    }

    [Fact]
    public void ClearCollision_removes_the_worlds_faces_in_an_area()
    {
        using var pkg = new SyntheticPackage();
        // Give worldmodel (a world node, not a reference) a floor around the origin.
        string geo = Directory.GetFiles(pkg.Folder, "*_GEO.ZED").Single();
        var c = ZedContainer.FromBytes(File.ReadAllBytes(geo));
        ZedTree.AppendChild(c, 1, GeoCollision.Region(GeoCollision.Box(new[] { -5f, -1, -5 }, new[] { 5f, 0, 5 }, GeoCollision.Profile.Default))!);
        File.WriteAllBytes(geo, c.ToBytes());

        var b = new Backend();
        string session = b.Open(pkg.Folder);
        JsonElement World() => b.Ok(new CommandMessage { Command = "ASSET_MAP_COLLISION", AssetSession = session, AssetJson = "{}" }).GetProperty("world");
        Assert.Equal(5, World().GetProperty("faces").GetInt32());

        var none = b.Edit(session, new { op = "clearCollision", area = new[] { 100f, 100, 200, 200 } });
        Assert.Equal(0, none.GetProperty("nodes").GetInt32());
        var cleared = b.Edit(session, new { op = "clearCollision", area = new[] { -10f, -10, 10, 10 } });
        Assert.Equal(1, cleared.GetProperty("nodes").GetInt32());
        Assert.Equal(5, cleared.GetProperty("faces").GetInt32());
        Assert.Equal(0, World().GetProperty("faces").GetInt32());
        Assert.Contains("geoIndices, visuals, area or all", b.EditFails(session, new { op = "clearCollision" }));
    }

    [Fact]
    public void Points_are_added_and_removed_and_the_file_after_the_table_moves_with_them()
    {
        byte[] mps = MapPointsTests.Mps();
        BitConverter.GetBytes(0xDEADBEEFu).CopyTo(mps, 0x150); // something after the table (at 0x100, 2 records)

        byte[] added = MapPoints.Add(mps, "Charlie", "waypoint", null, 55, 65);
        Assert.Equal(mps.Length + 24, added.Length);
        Assert.Equal(0xDEADBEEFu, BitConverter.ToUInt32(added, 0x150 + 24));
        var points = MapPoints.Parse(added);
        Assert.Equal(new[] { "Alpha1", "Bravo1", "Charlie" }, points.Select(p => p.Name));
        var charlie = points[2];
        Assert.Equal(("waypoint", 2, 5, 6), (charlie.Kind, charlie.Team, charlie.CellX, charlie.CellZ)); // team copied from Bravo1

        byte[] start = MapPoints.Add(added, "Alpha2", "start", 9, 15, 15);
        Assert.Contains(MapPoints.Parse(start), p => p.Name == "Alpha2" && p.Kind == "start" && p.Team == 9 && p.CellX == 1);

        byte[] removed = MapPoints.Remove(added, "bravo1");
        Assert.Equal(mps.Length, removed.Length);
        Assert.Equal(new[] { "Alpha1", "Charlie" }, MapPoints.Parse(removed).Select(p => p.Name));
        Assert.Equal(0xDEADBEEFu, BitConverter.ToUInt32(removed, 0x150));

        Assert.Throws<ArgumentException>(() => MapPoints.Add(mps, "Alpha1", "start", null, 5, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => MapPoints.Add(mps, "Far", "start", null, 5000, 5));
        Assert.Throws<KeyNotFoundException>(() => MapPoints.Remove(mps, "Nobody"));
        Assert.Throws<InvalidOperationException>(() => MapPoints.Remove(MapPoints.Remove(mps, "Bravo1"), "Alpha1"));
    }

    [Fact]
    public void Points_are_added_and_removed_through_the_backend()
    {
        using var pkg = new SyntheticPackage();
        var b = new Backend();
        string session = b.Open(pkg.Folder);
        b.Edit(session, new { op = "addPoint", name = "Delta", kind = "waypoint", position = new[] { 305f, 0, 405 } });
        b.Edit(session, new { op = "removePoint", name = "Alpha1" });
        var names = b.Points(session).GetProperty("points").EnumerateArray().Select(p => p.GetProperty("name").GetString()).ToList();
        Assert.Equal(new[] { "Bravo1", "Delta" }, names);
        b.Undo(session);
        b.Undo(session);
        Assert.Equal(2, b.Points(session).GetProperty("points").GetArrayLength());
    }

    [Fact]
    public void ClearMap_empties_the_map_in_one_undo_step()
    {
        using var pkg = new SyntheticPackage();
        var b = new Backend();
        string session = b.Open(pkg.Folder);
        int References() => b.Scene(session).GetProperty("instances").EnumerateArray().Count(i => i.GetProperty("kind").GetString() == "reference");
        int Clutter() => b.Scene(session).GetProperty("instances").EnumerateArray().Count(i => i.GetProperty("kind").GetString() == "clutter");
        Assert.Equal((2, 2), (References(), Clutter()));

        var kept = b.Edit(session, new { op = "clearMap", keepModels = new[] { "crate" } });
        Assert.Equal((0, 2), (kept.GetProperty("props").GetInt32(), kept.GetProperty("clutter").GetInt32()));
        Assert.Equal((2, 0), (References(), Clutter()));
        b.Undo(session);

        var cleared = b.Edit(session, new { op = "clearMap" });
        Assert.Equal(2, cleared.GetProperty("props").GetInt32());
        Assert.Equal((0, 0), (References(), Clutter()));
        b.Undo(session);
        Assert.Equal((2, 2), (References(), Clutter()));
        Assert.Equal(2, b.Points(session).GetProperty("points").GetArrayLength());
    }

    [Fact]
    public void AddLadder_builds_mp2s_ladder_faces()
    {
        using var pkg = new SyntheticPackage();
        var b = new Backend();
        string session = b.Open(pkg.Folder);
        // MP2's ladderdown: foot at (-412.5, 100, 54.405), climbing to 160, its face toward +Z.
        var r = b.Edit(session, new { op = "addLadder", position = new[] { -412.5f, 100, 54.405f }, top = 160, yaw = 0 });
        Assert.Equal("ladderdown_1", r.GetProperty("name").GetString());
        var c = b.Ok(new CommandMessage { Command = "ASSET_MAP_COLLISION", AssetSession = session, AssetJson = "{}" });
        var faces = c.GetProperty("faces").EnumerateArray().Where(f => f.GetProperty("node").GetString() == "ladderdown_1").ToList();
        Assert.Equal(2, faces.Count);
        float[] P(JsonElement f) => f.GetProperty("points").EnumerateArray().Select(v => v.GetSingle()).ToArray();
        Assert.Equal(new[] { -416.5f, 160, 54.405f, -416.5f, 100, 54.405f, -408.5f, 100, 54.405f, -408.5f, 160, 54.405f }, P(faces[0]));
        Assert.Equal(new[] { -408.5f, 160, 54.405f, -416.5f, 160, 54.405f, -416.5f, 170, 54.405f, -408.5f, 170, 54.405f }, P(faces[1]));
        Assert.All(faces, f => Assert.Equal(3u, f.GetProperty("material").GetUInt32()));
        Assert.Equal("ladderdown_2", b.Edit(session, new { op = "addLadder", position = new[] { 0f, 0, 0 }, top = 30, yaw = 90 }).GetProperty("name").GetString());
        Assert.Contains("above the foot", b.EditFails(session, new { op = "addLadder", position = new[] { 0f, 50, 0 }, top = 40 }));
    }

    /// <summary>
    /// On a disc: MP1's grid fitted to MP1's own collision changes only the cells retail authored differently (the
    /// walkable bit agrees for ~94% and the height for ~94%), a second fit changes nothing, and the points keep
    /// their floors.
    /// </summary>
    [Fact]
    public void Disc_mp1s_grid_fits_its_own_collision()
    {
        string? disc = Environment.GetEnvironmentVariable("MAPBUILDER_DISC");
        string? zdb = string.IsNullOrEmpty(disc) ? null
            : new[] { Path.Combine(disc, "RUN", "MP1.ZDB"), Path.Combine(disc, "MP1.ZDB") }.FirstOrDefault(File.Exists);
        if (zdb == null) return;
        string copy = Path.Combine(Path.GetTempPath(), $"mb-{Guid.NewGuid():N}", "MP1.ZDB");
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        File.Copy(zdb, copy);
        var b = new Backend();
        string session = b.Open(copy);
        var before = b.Points(session).GetProperty("points").EnumerateArray().ToDictionary(p => p.GetProperty("name").GetString()!, p => p.GetProperty("y").ValueKind == JsonValueKind.Number ? p.GetProperty("y").GetSingle() : float.NaN);

        var r = b.Edit(session, new { op = "fitGrid" });
        int cells = r.GetProperty("cells").GetInt32(), changed = r.GetProperty("changed").GetInt32();
        Assert.Equal(28805, cells);
        Assert.True(changed < cells / 6, $"{changed} of {cells} cells changed");
        Assert.True(r.GetProperty("walkable").GetInt32() > cells * 9 / 10);
        Assert.Equal(0, b.Edit(session, new { op = "fitGrid" }).GetProperty("changed").GetInt32());

        var after = b.Points(session).GetProperty("points").EnumerateArray().ToDictionary(p => p.GetProperty("name").GetString()!, p => p.GetProperty("y").ValueKind == JsonValueKind.Number ? p.GetProperty("y").GetSingle() : float.NaN);
        Assert.Equal(before.Keys.OrderBy(k => k), after.Keys.OrderBy(k => k));
        Assert.Equal(71.2f, after["StartBomb"], 1f); // terrain, as retail
        Assert.True(before.Count(kv => float.IsNaN(kv.Value) || MathF.Abs(kv.Value - after[kv.Key]) < 2) >= before.Count - 2);
    }

    /// <summary>On a disc: MP2 cleared keeps its sky and sea, hides the rig, and nothing of the rig stays solid.</summary>
    [Fact]
    public void Disc_mp2_clears_to_sky_and_sea()
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
        var size = b.Ok(new CommandMessage { Command = "ASSET_DESCRIBE_MODEL", AssetSession = session, AssetName = "container06" });
        Assert.Equal(9, size.GetProperty("collisionFaces").GetInt32());
        Assert.True(size.GetProperty("size3")[0].GetSingle() > 10);

        var r = b.Edit(session, new { op = "clearMap" });
        var keptVisuals = r.GetProperty("keptVisuals").EnumerateArray().Select(v => v.GetString()).ToList();
        Assert.Contains(keptVisuals, v => v!.StartsWith("N000"));
        Assert.True(r.GetProperty("hiddenVisuals").GetArrayLength() > 30);
        var c = b.Ok(new CommandMessage { Command = "ASSET_MAP_COLLISION", AssetSession = session, AssetJson = "{\"faces\":false}" });
        Assert.Equal(0, c.GetProperty("world").GetProperty("faces").GetInt32());
        Assert.Equal(0, c.GetProperty("props").GetProperty("faces").GetInt32());
        Assert.Equal(0, b.Scene(session).GetProperty("instances").EnumerateArray().Count(i => i.GetProperty("kind").GetString() == "reference"));
    }

    /// <summary>
    /// On a disc (MAPBUILDER_DISC): every collision face of every map's own scene re-encodes to the params retail wrote
    /// (corner count, material, prefix and flags) apart from byte 8's low bit. That bit is authored: it is set on the
    /// faces that look up on MP2 without exception, and on the great majority of maps' faces, so new faces follow that.
    /// </summary>
    [Fact]
    public void Disc_faces_re_encode_to_their_retail_params()
    {
        string? disc = Environment.GetEnvironmentVariable("MAPBUILDER_DISC");
        if (string.IsNullOrEmpty(disc)) return;
        string run = Directory.Exists(Path.Combine(disc, "RUN")) ? Path.Combine(disc, "RUN") : disc;
        var maps = Directory.GetFiles(run, "*.ZDB", SearchOption.AllDirectories).Where(f => !Path.GetFileName(f).StartsWith("APACHE")).ToList();
        if (maps.Count == 0) return;
        int faces = 0, mismatched = 0, bitDiffers = 0;
        var examples = new List<string>();
        foreach (string zdb in maps)
        {
            using var z = ZdbArchive.Open(zdb);
            // The map's own scene (MP2_GEO.ZED in MP2.ZDB), not the shared weapon and vehicle libraries.
            string map = Path.GetFileNameWithoutExtension(zdb);
            foreach (var e in z.Entries.Where(e => e.Name.Equals(map + "_GEO.ZED", StringComparison.OrdinalIgnoreCase)))
            {
                var scene = GeoScene.Parse(e.Data);
                var stack = new Stack<GeoNode>(new[] { scene.Root });
                while (stack.Count > 0)
                {
                    var n = stack.Pop();
                    foreach (var c in n.Children) stack.Push(c);
                    if (n.Name != "di" || n.Parent?.Name == "di") continue;
                    foreach (var f in n.Children.Where(c => c.Name == "di"))
                    {
                        if (f.Child("params")?.Data is not { Length: 12 } prm || f.Child("points")?.Data is not { Length: >= 48 } pts) continue;
                        var points = new float[pts.Length / 16 * 3];
                        for (int i = 0; i < points.Length / 3; i++)
                            for (int k = 0; k < 3; k++) points[i * 3 + k] = BitConverter.ToSingle(pts, i * 16 + k * 4);
                        faces++;
                        byte[] mine = GeoCollision.Params(new GeoCollision.Face(points, GeoCollision.ProfileOf(prm)));
                        if ((mine[8] ^ prm[8]) == 1) { bitDiffers++; mine[8] = prm[8]; }
                        if (!mine.AsSpan().SequenceEqual(prm) && ++mismatched <= 5) examples.Add($"{Path.GetFileName(zdb)} {Hex(prm)} -> {Hex(mine)}");
                    }
                }
            }
        }
        Assert.True(faces > 10000, $"only {faces} faces");
        Assert.True(mismatched == 0, $"{mismatched} of {faces} faces differ: {string.Join("; ", examples)}");
        Assert.True(bitDiffers < faces / 5, $"the look-up bit differs on {bitDiffers} of {faces} faces");
    }
}
