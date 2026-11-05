using CodeDesigner.Ps2.FileFormats.Zipper.Ca;
using CodeDesigner.Ps2.FileFormats.Zipper.ZDB;
using MapBuilder.Server;
using MapBuilder.Tests.Fixtures;
using System.Text.Json;
using Xunit;

namespace MapBuilder.Tests;

/// <summary>
/// SOCOM 3 / Combined Assault mission AI data (CaMissions, CaPackage.MissionPoints, CaDisc): a mission.rdr's zones and
/// named points placed on their island's AIMi grids, location archives named by CRC32 of RUN\&lt;LOCATION&gt;, and
/// location / mission paths resolved to their archive. Synthetic always; the retail check runs when
/// MAPBUILDER_CA_DISC (the disc root, e.g. G:\) is set.
/// </summary>
public sealed class CaMissionsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mb-ca-" + Guid.NewGuid().ToString("N")[..8]);

    public CaMissionsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed record P(string Name, int CellX, int CellZ, float Y, bool Marker = false);

    /// <summary>A mission.rdr with one zone per entry of <paramref name="zones"/>, owned by islands of (hash, count).</summary>
    private static byte[] Mission((float[] Min, float[] Max, P[] Points)[] zones, (uint Hash, int Count)[] islands)
    {
        var d = new List<byte>(new byte[0x48]);
        void Put(int at, uint v) { var b = BitConverter.GetBytes(v); for (int i = 0; i < 4; i++) d[at + i] = b[i]; }
        void Add(uint v) => d.AddRange(BitConverter.GetBytes(v));
        void AddF(float v) => d.AddRange(BitConverter.GetBytes(v));
        void Vec(int at, int begin, int end) { Put(at, (uint)begin); Put(at + 4, (uint)end); Put(at + 8, (uint)end); Put(at + 12, (uint)(end - begin)); }

        int boxes = d.Count;
        foreach (var z in zones) { Add(0xDEAD0000); Add(0); foreach (var f in z.Min) AddF(f); foreach (var f in z.Max) AddF(f); }
        Vec(0x28, boxes, d.Count);
        int isl = d.Count, first = 0;
        foreach (var (hash, count) in islands) { Add(hash); Add((uint)first); Add((uint)count); Add(0); Add(0); Add(0); first += count; }
        Vec(0x38, isl, d.Count);
        int root = d.Count;
        Put(0, (uint)root);
        Add((uint)zones.Length);
        d.AddRange(new byte[16]);
        int ptrs = d.Count;
        d.AddRange(new byte[zones.Length * 4]);
        for (int i = 0; i < zones.Length; i++)
        {
            int zone = d.Count;
            Put(ptrs + i * 4, (uint)zone);
            d.AddRange(new byte[0x74]);
            int begin = d.Count;
            foreach (var p in zones[i].Points)
            {
                Add((uint)i | (uint)p.CellX << 6 | (uint)p.CellZ << 19);
                AddF(p.Y);
                var name = new byte[16];
                System.Text.Encoding.ASCII.GetBytes(p.Name).CopyTo(name, 0);
                d.AddRange(name);
                // Flags: byte 1 marks a map marker; the top half is padding the game left uninitialised.
                Add(0x7C160000u | (p.Marker ? 0x100u : 0u));
            }
            Vec(zone + 0x20, begin, d.Count);
        }
        return d.ToArray();
    }

    /// <summary>An AIMi with one grid section per (min, cell) entry.</summary>
    private static byte[] IslandAi(uint island, string name, params float[][] mins)
    {
        int n = mins.Length, header = 12 + 4 * n, size = 0x60;
        var d = new byte[header + n * size];
        BitConverter.GetBytes((uint)n).CopyTo(d, 0);
        BitConverter.GetBytes(island).CopyTo(d, 4);
        BitConverter.GetBytes(0x12345678u).CopyTo(d, 8);
        for (int i = 0; i < n; i++)
        {
            int o = header + i * size;
            BitConverter.GetBytes((uint)o).CopyTo(d, 12 + i * 4);
            float[] v = { mins[i][0], mins[i][1], mins[i][2], mins[i][0] + 1000, mins[i][1] + 100, mins[i][2] + 1000 };
            for (int k = 0; k < 6; k++) BitConverter.GetBytes(v[k]).CopyTo(d, o + k * 4);
            BitConverter.GetBytes(100u).CopyTo(d, o + 24);
            BitConverter.GetBytes(100u).CopyTo(d, o + 28);
            BitConverter.GetBytes(10f).CopyTo(d, o + 32);
            BitConverter.GetBytes(10f).CopyTo(d, o + 36);
            System.Text.Encoding.ASCII.GetBytes(name).CopyTo(d, o + 0x34);
        }
        return d;
    }

    private static readonly uint IslandA = CaNames.Crc32("isle_a.src"), IslandB = CaNames.Crc32("isle_b.src");

    private static byte[] TwoIslandMission() => Mission(new[]
    {
        (new[] { -500f, 0, -500 }, new[] { 500f, 50, 500 }, new[] { new P("s_Able1", 10, 20, 12.5f), new P("JULIET", 50, 50, 3, Marker: true) }),
        (new[] { 2000f, 0, 0 }, new[] { 2500f, 50, 500 }, new[] { new P("n_patrol1a", 1, 2, 7) }),
        (new[] { 4000f, 10, 4000 }, new[] { 4500f, 60, 4500 }, new[] { new P("spectator", 0, 0, 30), new P("i1_door_4", 5, 5, 31) }),
    }, new[] { (IslandA, 2), (IslandB, 1) });

    private CaPackage EditableMission(int squadSlots = 0) => EditableMission(_root, squadSlots);

    internal static CaPackage EditableMission(string folder, int squadSlots = 0)
    {
        string path = Path.Combine(folder, "SP414.ZDB");
        using var archive = ZdbArchive.CreateNew(1);
        var mission = TwoIslandMission();
        int zone0 = BitConverter.ToInt32(mission, BitConverter.ToInt32(mission, 0) + 0x14);
        int region = mission.Length;
        Array.Resize(ref mission, region + 32);
        Put(mission, zone0 + 0x50, (uint)region); Put(mission, zone0 + 0x54, (uint)mission.Length);
        Put(mission, region, (3u << 6) | (7u << 19)); Put(mission, region + 4, 6u | 10u << 16);
        System.Text.Encoding.ASCII.GetBytes("sObj_Rescue").CopyTo(mission, region + 16);
        if (squadSlots > 0)
        {
            int slots = mission.Length;
            Array.Resize(ref mission, slots + squadSlots * 12);
            Put(mission, zone0 + 0x64, (uint)slots); Put(mission, zone0 + 0x68, (uint)mission.Length);
            for (int i = 0; i < squadSlots; i++)
            {
                // Slot 0 shares s_Able1's (10,20) cell. Flags hold the slot ordinal and a heading.
                Put(mission, slots + i * 12, ((uint)(10 + i) << 6) | (20u << 19));
                Put(mission, slots + i * 12 + 4, ((uint)i << 24) | 3);
                Put(mission, slots + i * 12 + 8, 0xAABBCCDDu);
            }
        }
        archive.AddResource("mission.rdr", mission, "AIMm", 51);
        archive.AddResource("isle_a.src", IslandAi(IslandA, "isle_a", new[] { -500f, 0, -500 }, new[] { 2000f, 0, 0 }), "AIMi", 54);
        var actors = new byte[0x14 + 0x1EC];
        static void Put(byte[] d, int offset, uint value) => BitConverter.GetBytes(value).CopyTo(d, offset);
        Put(actors, 0, 0x14); Put(actors, 4, (uint)actors.Length);
        Put(actors, 0x14 + 0x58, 2);
        int start = 0x14 + 0x5C + 32; // the first slot is empty and omitted from the table shown in the editor
        System.Text.Encoding.ASCII.GetBytes("s_Able1").CopyTo(actors, start);
        Put(actors, start + 24, 0x12345678); Put(actors, start + 28, 5);
        archive.AddResource("vehicles.rdr", actors, "MSNv", 1);
        var header = new byte[0x80];
        Put(header, 0x30, 2); Put(header, 0x34, 0x60);
        Put(header, 0x60, 0x87654321); Put(header, 0x64, 0x89ABCDEF);
        Put(header, 0x4C, 2); Put(header, 0x50, 0x68);
        Put(header, 0x68, 0xA); Put(header, 0x6C, 0xB);
        archive.AddResource("SP414", header, "MISN", 1);
        var valves = new byte[16];
        Put(valves, 0, 0xB); Put(valves, 4, 0x030100FF); Put(valves, 8, 0xA); Put(valves, 12, 0x0301000F);
        archive.AddResource("mission.rdr", valves, "VALV", 1);
        archive.Save(path);
        return CaPackage.Open(path);
    }

    [Fact]
    public void Named_objective_regions_use_the_centre_of_their_own_grid_rectangle()
    {
        var package = EditableMission();
        var objective = Assert.Single(CaMissions.ObjectiveRegions(package, "SP414"));
        Assert.Equal(new CaMissions.ObjectiveRegion("sObj_Rescue", true, 0, -440f, -380f), objective);
    }

    [Fact]
    public void A_blank_point_slot_does_not_hide_its_neighbours()
    {
        var data = TwoIslandMission();
        var first = CaMissions.ParseMission(data).Zones[0].Points[0];
        Array.Clear(data, first.RecordOffset + 8, 16);
        var points = CaMissions.ParseMission(data).Zones[0].Points;
        Assert.Equal("JULIET", Assert.Single(points).Name);
    }

    [Fact]
    public void Move_and_rename_patch_only_the_point_and_actor_start()
    {
        var package = EditableMission();
        var aim = package.MissionResource("SP414", "AIMm")!;
        var actors = package.MissionResource("SP414", "MSNv")!;
        var sourcePoint = CaMissions.ParseMission(aim.Data).Zones[0].Points[0];
        var sourceStart = Assert.Single(CaMissionTables.ParseActors(actors.Data)).Starts.Single();
        var changes = CaMissionEdits.Point(package, "SP414", "s_able1", "s_new", new[] { -499.5f, 14, -479.5f });
        Assert.Equal(2, changes.Count);
        var p = CaMissions.ParseMission(changes[aim]).Zones[0].Points[0];
        Assert.Equal(("s_new", 0, 0, 2, 14f), (p.Name, p.Zone, p.CellX, p.CellZ, p.Y));
        var start = CaMissionTables.ParseActors(changes[actors]).Single().Starts.Single();
        Assert.Equal(("s_new", 5), (start.Point, start.Heading));
        Assert.Equal("s_Able1", CaMissions.ParseMission(aim.Data).Zones[0].Points[0].Name);
        Assert.Equal("s_Able1", CaMissionTables.ParseActors(actors.Data).Single().Starts.Single().Point);
        Assert.Equal(aim.Data[..sourcePoint.RecordOffset], changes[aim][..p.RecordOffset]);
        Assert.Equal(aim.Data[(sourcePoint.RecordOffset + 14)..], changes[aim][(p.RecordOffset + 14)..]);
        Assert.Equal(actors.Data[..sourceStart.RecordOffset], changes[actors][..start.RecordOffset]);
        Assert.Equal(actors.Data[(sourceStart.RecordOffset + 6)..], changes[actors][(start.RecordOffset + 6)..]);
    }

    [Fact]
    public void A_verified_squad_slot_moves_with_the_named_point_but_an_irregular_group_emits_a_warning()
    {
        foreach (int count in new[] { 4, 3 })
        {
            var package = EditableMission(count);
            var aim = package.MissionResource("SP414", "AIMm")!;
            var warnings = new List<string>();
            byte[] edit = CaMissionEdits.Point(package, "SP414", "s_Able1", null, new[] { -499f, 3f, -479f }, warnings)[aim];
            int zone0 = BitConverter.ToInt32(aim.Data, BitConverter.ToInt32(aim.Data, 0) + 0x14);
            int slots = BitConverter.ToInt32(aim.Data, zone0 + 0x64);
            if (count == 4)
            {
                Assert.Equal(2u << 19, BitConverter.ToUInt32(edit, slots));
                Assert.Empty(warnings);
            }
            else
            {
                Assert.Equal(BitConverter.ToUInt32(aim.Data, slots), BitConverter.ToUInt32(edit, slots));
                Assert.Contains("ambiguous", Assert.Single(warnings));
            }
            Assert.Equal(aim.Data[(slots + 4)..], edit[(slots + 4)..]);
        }
    }

    [Fact]
    public void Invalid_or_cross_zone_point_edits_are_rejected()
    {
        var package = EditableMission();
        void Move(float x, float z) => CaMissionEdits.Point(package, "SP414", "s_Able1", null, new[] { x, 2f, z });
        Assert.Contains("another zone", Assert.Throws<ArgumentException>(() => Move(2001, 1)).Message);
        Assert.Contains("outside", Assert.Throws<ArgumentOutOfRangeException>(() => Move(-501, -499)).Message);
        Assert.Throws<ArgumentException>(() => Move(float.NaN, 0));
        foreach (string invalid in new[] { "JuliEt", "0123456789ABCDEF", "non-ASCII é", " spaced" })
            Assert.Throws<ArgumentException>(() => CaMissionEdits.Point(package, "SP414", "s_Able1", invalid, null));
        Assert.Throws<InvalidOperationException>(() => CaMissionEdits.Point(package, "SP414", "spectator", null, new[] { 4001f, 30f, 4001f }));
    }

    [Fact]
    public void Table_edits_match_displayed_actor_slots_header_valve_order_and_weapon_offsets()
    {
        var package = EditableMission();
        byte[] Edit(string table, string path, string values)
        {
            using var p = System.Text.Json.JsonDocument.Parse(path);
            using var v = System.Text.Json.JsonDocument.Parse(values);
            return Assert.Single(CaMissionEdits.Table(package, "SP414", table, p.RootElement, v.RootElement)).Value;
        }
        byte[] actors = Edit("vehicles.rdr", """[0, "starts", 0]""", """["juliet", 7]""");
        var start = CaMissionTables.ParseActors(actors).Single().Starts.Single();
        Assert.Equal(("JULIET", 7, 0x14 + 0x5C + 32), (start.Point, start.Heading, start.RecordOffset));
        Assert.Equal(0x12345678u, BitConverter.ToUInt32(actors, start.RecordOffset + 24));
        byte[] valves = Edit("mission.rdr", """["Valves", 0, "VALUE"]""", "[65535]");
        Assert.Equal(new[] { (0xBu, 255), (0xAu, 65535) }, CaMissionTables.ParseValves(valves));
        Assert.Equal(0x0301FFFFu, BitConverter.ToUInt32(valves, 12));
        byte[] header = Edit("mission.rdr", """["Weapons", 1]""", "[2271560481]"); // 0x87654321
        Assert.Equal(new uint[] { 0x87654321, 0x87654321 }, CaMissionTables.ParseHeader(header).Weapons);
        Assert.Equal(package.MissionResource("SP414", "MISN")!.Data[..0x64], header[..0x64]);
        Assert.Equal(package.MissionResource("SP414", "MISN")!.Data[0x68..], header[0x68..]);
        Assert.Throws<KeyNotFoundException>(() => Edit("vehicles.rdr", """[0, "starts", 0]""", """["nowhere", 3]"""));
        Assert.Throws<ArgumentException>(() => Edit("vehicles.rdr", """[0, "starts", 0]""", """["JULIET", 8]"""));
        Assert.Throws<ArgumentException>(() => Edit("mission.rdr", """["Valves", 0, "VALUE"]""", "[65536]"));
        Assert.Throws<ArgumentException>(() => Edit("mission.rdr", """["Weapons", 2]""", "[2271560481]"));
        Assert.Throws<ArgumentException>(() => Edit("mission.rdr", """["Weapons", 0]""", "[999]"));
    }

    [Fact]
    public void A_mission_rdr_gives_zones_islands_and_points()
    {
        var m = CaMissions.ParseMission(TwoIslandMission());

        Assert.Equal(3, m.Zones.Count);
        Assert.Equal(new[] { (IslandA, 0, 2), (IslandB, 2, 1) }, m.Islands.Select(i => (i.Hash, i.FirstZone, i.ZoneCount)));
        var able = m.Zones[0].Points[0];
        Assert.Equal(("s_Able1", 0, 10, 20, 12.5f, false), (able.Name, able.Zone, able.CellX, able.CellZ, able.Y, able.Marker));
        Assert.True(m.Zones[0].Points[1].Marker);
        Assert.Equal(new[] { 2f, 0, 0 }.Select(v => v * 1000), m.Zones[1].Min.Select(v => v));
        Assert.Equal(2, m.Zones[2].Points[0].Zone);
    }

    [Fact]
    public void Points_land_on_their_own_zones_grid_section()
    {
        string zdb = Path.Combine(_root, "SP414.ZDB");
        using (var z = ZdbArchive.CreateNew(1))
        {
            z.AddResource("mission.rdr", TwoIslandMission(), "AIMm", 51);
            // Island A has two zones, so two grid sections; zone 1 is its second.
            z.AddResource("isle_a.src", IslandAi(IslandA, "isle_a", new[] { -500f, 0, -500 }, new[] { 2000f, 0, 0 }), "AIMi", 54);
            z.AddResource("isle_b.src", IslandAi(IslandB, "isle_b", new[] { 4000f, 10, 4000 }), "AIMi", 54);
            z.Save(zdb);
        }

        var pkg = CaPackage.Open(zdb);
        var mission = Assert.Single(pkg.Missions());
        Assert.Equal(("SP414", "SP", ""), (mission.Id, mission.Kind, mission.Mode));
        var points = pkg.MissionPoints("sp414").ToDictionary(p => p.Name);

        Assert.Equal((-500 + 10.5f * 10, 12.5f, -500 + 20.5f * 10, "start", "isle_a"), (points["s_Able1"].X, points["s_Able1"].Y, points["s_Able1"].Z, points["s_Able1"].Kind, points["s_Able1"].Island));
        Assert.Equal("marker", points["JULIET"].Kind);
        Assert.Equal((2015f, 25f, "waypoint"), (points["n_patrol1a"].X, points["n_patrol1a"].Z, points["n_patrol1a"].Kind));
        Assert.Equal((4005f, "spectator", "isle_b"), (points["spectator"].X, points["spectator"].Kind, points["spectator"].Island));
        Assert.Equal("other", points["i1_door_4"].Kind);
        Assert.Throws<KeyNotFoundException>(() => pkg.MissionPoints("MP999CTR"));
    }

    [Fact]
    public void CA_minimap_uses_scene_geometry_for_the_selected_mission()
    {
        var backend = new Backend();
        string session = backend.Open(EditableMission().SourcePath);
        try
        {
            var hud = backend.Ok(new CommandMessage { Command = "ASSET_MAP_HUD", AssetSession = session });
            Assert.Equal("SP414", hud.GetProperty("map").GetString());
            Assert.Empty(hud.GetProperty("icons").EnumerateObject());
            Assert.Contains("compass_lo", hud.GetProperty("missing").EnumerateArray().Select(v => v.GetString()));
            Assert.Equal(JsonValueKind.Null, hud.GetProperty("tacmap").ValueKind);
        }
        finally { backend.Close(session); }
    }

    [Fact]
    public void Session_edits_restore_all_resources_and_reopen_as_a_complete_v6_copy()
    {
        var pkg = EditableMission();
        var b = new Backend();
        string s = b.Open(pkg.SourcePath);
        JsonElement Command(string op, object? args = null) => b.Ok(new CommandMessage
            { Command = op, AssetSession = s, AssetJson = JsonSerializer.Serialize(args) });
        try
        {
            Assert.Equal(s, b.Open(pkg.SourcePath));
            Assert.False(b.Status(s).GetProperty("readOnly").GetBoolean());
            Assert.True(b.Edit(s, new { op = "setPoint", name = "s_Able1", newName = "DryRun", dryRun = true }).GetProperty("dryRun").GetBoolean());
            Assert.Equal(0, b.Status(s).GetProperty("revision").GetInt32());
            var edit = b.Edit(s, new { op = "setPoint", name = "s_Able1", newName = "Edited", mission = "SP414" });
            Assert.Equal(2, edit.GetProperty("changed").GetArrayLength());
            Assert.Equal(1, Command("ASSET_HISTORY").GetProperty("undo").GetArrayLength());
            b.Undo(s);
            Assert.Empty(b.Status(s).GetProperty("dirty").EnumerateArray());
            b.Redo(s);
            Assert.Contains("changed", b.EditFails(s, new { op = "movePoint", name = "Edited", position = new[] { 1, 2, 3 }, revision = 0 }));
            Assert.Contains("mission", b.EditFails(s, new { op = "setPoint", name = "Edited", newName = "Wrong", mission = "SP000" }));
            b.Edit(s, new { op = "mapTable", table = "mission.rdr", path = new object[] { "Valves", 0, "VALUE" }, values = new[] { 456 }, revision = 3 });
            b.Edit(s, new { op = "mapTable", table = "vehicles.rdr", path = new object[] { 0, "starts", 0 }, values = new object[] { "Edited", 7 }, revision = 4 });
            b.Edit(s, new { op = "mapTable", table = "mission.rdr", path = new object[] { "Weapons", 1 }, values = new[] { 0x87654321u }, revision = 5 });
            Assert.Equal(4, b.Status(s).GetProperty("dirty").GetArrayLength());
            string output = Path.Combine(_root, "output");
            string path = Command("ASSET_SAVE", new { outputPath = output }).GetProperty("written")[0].GetString()!;
            var copy = CaPackage.Open(path);
            Assert.Equal("Edited", copy.MissionPoints("SP414")[0].Name);
            var start = Assert.Single(Assert.Single(CaMissionTables.ParseActors(copy.MissionResource("SP414", "MSNv")!.Data)).Starts);
            Assert.Equal(("Edited", 7), (start.Point, start.Heading));
            Assert.Contains((0xAu, 456), CaMissionTables.ParseValves(copy.MissionResource("SP414", "VALV")!.Data));
            Assert.Equal(0x87654321u, CaMissionTables.ParseHeader(copy.MissionResource("SP414", "MISN")!.Data).Weapons[1]);
            for (int i = 0; i < 4; i++) b.Undo(s);
            Assert.Empty(b.Status(s).GetProperty("dirty").EnumerateArray());
            Assert.Equal(4, Command("ASSET_HISTORY").GetProperty("redo").GetArrayLength());
            Assert.Equal("s_Able1", CaPackage.Open(pkg.SourcePath).MissionPoints("SP414")[0].Name);
        }
        finally { b.Close(s); }
    }

    [Fact]
    public void A_zone_without_its_grid_falls_back_on_its_bounds()
    {
        var m = CaMissions.ParseMission(TwoIslandMission());
        var (x, z) = CaMissions.Position(m.Zones[1].Points[0], m.Zones[1], null);
        Assert.Equal((2015f, 25f), (x, z));
    }

    [Fact]
    public void Location_archives_are_named_by_crc32_and_paths_resolve_to_them()
    {
        Assert.Equal("055146D2.ZAR", CaPackage.ArchiveName("POLAND_RURAL"));
        Assert.Equal("9EA6A33E.ZAR", CaPackage.ArchiveName("compound"));

        string run = Path.Combine(_root, "RUN");
        Directory.CreateDirectory(run);
        File.WriteAllBytes(Path.Combine(run, "9EA6A33E.ZAR"), new byte[16]);
        Assert.Equal((Path.Combine(run, "9EA6A33E.ZAR"), (string?)null), CaDisc.Resolve(Path.Combine(run, "COMPOUND")));
        Assert.Equal((Path.Combine(run, "9EA6A33E.ZAR"), "SP432"), CaDisc.Resolve(Path.Combine(run, "COMPOUND", "sp432.zdb")));
        Assert.Null(CaDisc.Resolve(Path.Combine(run, "WHARF")));
        Assert.Null(CaDisc.Resolve(Path.Combine(_root, "COMPOUND")));
    }

    [Fact]
    public void A_complete_default_demolition_set_has_distinct_read_only_positions_for_both_sides()
    {
        var bounds = new[] { (new[] { 0f, 0, 0 }, new[] { 1000f, 100, 1000 }, new[]
        {
            new P("seal_tent", 10, 10, 3), new P("terrorist_tent", 90, 90, 4),
        }) };
        var data = Mission(bounds, new[] { (IslandA, 1) });
        int zone = BitConverter.ToInt32(data, BitConverter.ToInt32(data, 0) + 0x14), start = data.Length;
        Array.Resize(ref data, start + 144 * 12);
        void Put(int at, uint value) => BitConverter.GetBytes(value).CopyTo(data, at);
        Put(zone + 0x64, (uint)start); Put(zone + 0x68, (uint)data.Length);
        for (uint group = 0; group < 4; group++)
        for (uint slot = 0; slot < 36; slot++)
        {
            int at = start + (int)(group * 36 + slot) * 12;
            uint corner = group < 2 ? 5u : 80u;
            Put(at, (corner + slot % 6) << 6 | (corner + slot / 6) << 19);
            Put(at + 4, slot << 24 | group << 4);
        }
        string path = Path.Combine(_root, "MP999DEM.ZDB");
        using var archive = ZdbArchive.CreateNew(1);
        archive.AddResource("mission.rdr", data, "AIMm", 51);
        archive.AddResource("isle_a.src", IslandAi(IslandA, "isle_a", new[] { 0f, 0, 0 }), "AIMi", 54);
        archive.Save(path);
        var package = CaPackage.Open(path);
        var named = package.MissionPoints("MP999DEM");
        var spawns = CaMissions.TeamSpawns(package, "MP999DEM", named);
        Assert.Equal(2, named.Count);
        Assert.Equal(144, spawns.Count);
        Assert.Equal(("SEAL", 1, 55f, 55f), (spawns[0].Faction, spawns[0].Team, spawns[0].X, spawns[0].Z));
        Assert.Equal(("Terrorist", 2, 805f, 805f), (spawns[72].Faction, spawns[72].Team, spawns[72].X, spawns[72].Z));
        Assert.Empty(CaMissions.TeamSpawns(package, "MP999DEM", named.Select(p => p with { X = 0, Z = 0 }).ToList()));
    }

    [Fact]
    public void Retail_demolition_slots_match_both_faction_bases_in_two_locations()
    {
        string? disc = Environment.GetEnvironmentVariable("MAPBUILDER_CA_DISC");
        if (string.IsNullOrEmpty(disc)) return;
        foreach (var (archive, mission, sealZone, sealX, sealZ, terroristZone, terroristX, terroristZ) in new[]
        {
            ("055146D2.ZAR", "MP341DEM", 7, 118, 298, 0, 217, 85),
            ("474AA7A5.ZAR", "MP421DEM", 0, 64, 264, 15, 94, 208),
        })
        {
            var package = CaPackage.Open(Path.Combine(disc, "RUN", archive));
            var points = package.MissionPoints(mission);
            var slots = CaMissions.TeamSpawns(package, mission, points);
            Assert.Equal(144, slots.Count);
            Assert.Equal(72, slots.Count(s => s.Team == 1));
            Assert.Equal(72, slots.Count(s => s.Team == 2));
            Assert.Equal(slots.Count, slots.Select(s => s.Name).Distinct().Count());
            var seal = slots.Single(s => s.Name == "SEAL team spawn 1.01");
            var terrorist = slots.Single(s => s.Name == "Terrorist team spawn 1.01");
            Assert.Equal((sealZone, sealX, sealZ), (seal.Zone, seal.CellX, seal.CellZ));
            Assert.Equal((terroristZone, terroristX, terroristZ), (terrorist.Zone, terrorist.CellX, terrorist.CellZ));
            Assert.Empty(CaMissions.TeamSpawns(package, mission, points.Where(p => p.Name != "seal_tent").ToList()));
        }
        // On WHARF some default slots are closer to the opposite tent; the strict reader withholds the labels.
        var wharf = CaPackage.Open(Path.Combine(disc, "RUN", "7AEA19D9.ZAR"));
        Assert.Empty(CaMissions.TeamSpawns(wharf, "MP422DEM", wharf.MissionPoints("MP422DEM")));
    }

    [Fact]
    public void The_retail_disc_names_its_locations_and_places_mission_points_in_their_zones()
    {
        string? disc = Environment.GetEnvironmentVariable("MAPBUILDER_CA_DISC");
        string run = Path.Combine(disc ?? "", "RUN");
        if (string.IsNullOrEmpty(disc) || !File.Exists(Path.Combine(run, "GLOBAL.ZDB"))) return;

        var locations = CaDisc.Locations(run);
        Assert.Equal("POLAND_RURAL", locations["055146D2.ZAR"]);
        // Every hash-named archive is a location (24 maps and the front end, RUN\UI).
        int hashed = Directory.GetFiles(run, "*.ZAR").Count(f => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(f), "^[0-9A-F]{8}\\.ZAR$"));
        Assert.Equal(hashed, locations.Count);
        Assert.Equal("UI", locations["D6D6E01B.ZAR"]);

        var breakneck = CaPackage.Open(Path.Combine(run, "CEA9385F.ZAR"));
        Assert.Equal(11, breakneck.MissionPoints("MP321BRE").Count);
        Assert.Equal(9, breakneck.MissionPoints("MP325SUP").Count);

        // COMPOUND: one island; SP432's squad starts and markers.
        var compound = CaPackage.Open(Path.Combine(run, "9EA6A33E.ZAR"));
        Assert.Equal("COMPOUND", compound.Location);
        Assert.Equal(new[] { "SP432", "IA432HDQ" }, compound.Missions().Select(m => m.Id));
        var sp = compound.MissionPoints("SP432").ToDictionary(p => p.Name);
        var objectives = CaMissions.ObjectiveRegions(compound, "SP432");
        Assert.Equal(5, objectives.Count(o => !o.Secondary));
        Assert.Equal(6, objectives.Count(o => o.Secondary));
        var infiltrate = objectives.Single(o => o.Name == "pObj_infltrate1");
        Assert.InRange(infiltrate.X, 3.41f, 3.42f);
        Assert.InRange(infiltrate.Z, 37.16f, 37.17f);
        Assert.Equal("start", sp["s_Able1"].Kind);
        Assert.Equal("marker", sp["JULIET"].Kind);
        Assert.Equal("com_i1", sp["s_Able1"].Island);
        Assert.InRange(sp["s_Able1"].X, -372f, -371f);
        Assert.InRange(sp["s_Able1"].Z, -1978f, -1977f);

        // POLAND_RURAL MP341CTR: eight islands, 26 zones; every point on its zone's grid lies within the zone.
        var rural = CaPackage.Open(Path.Combine(run, "055146D2.ZAR"));
        var mission = CaMissions.ParseMission(rural.Resources.First(r => r.Tag == "AIMm" && r.File.EndsWith("MP341CTR.ZDB")).Data);
        var points = rural.MissionPoints("MP341CTR");
        Assert.Equal(mission.Zones.Sum(z => z.Points.Count), points.Count);
        Assert.True(points.Count > 100);
        Assert.All(points, p =>
        {
            var z = mission.Zones[p.Zone];
            Assert.NotNull(p.Island);
            Assert.InRange(p.X, z.Min[0] - 10, z.Max[0] + 10);
            Assert.InRange(p.Z, z.Min[2] - 10, z.Max[2] + 10);
        });
    }
}
