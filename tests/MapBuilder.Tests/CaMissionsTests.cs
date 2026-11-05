using CodeDesigner.Ps2.FileFormats.Zipper.Ca;
using CodeDesigner.Ps2.FileFormats.Zipper.ZDB;
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
            d.AddRange(new byte[0x30]);
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

        // COMPOUND: one island; SP432's squad starts and markers.
        var compound = CaPackage.Open(Path.Combine(run, "9EA6A33E.ZAR"));
        Assert.Equal("COMPOUND", compound.Location);
        Assert.Equal(new[] { "SP432", "IA432HDQ" }, compound.Missions().Select(m => m.Id));
        var sp = compound.MissionPoints("SP432").ToDictionary(p => p.Name);
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
