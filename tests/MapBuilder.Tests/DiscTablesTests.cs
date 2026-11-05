using System.Text;
using System.Text.Json;
using CodeDesigner.Ps2.FileFormats.Zipper.Geo;
using CodeDesigner.Ps2.FileFormats.Zipper.RDR;
using CodeDesigner.Ps2.FileFormats.Zipper.ZAR;
using CodeDesigner.Ps2.FileFormats.Zipper.ZDB;
using CodeDesigner.Ps2.FileFormats.Zipper.ZED;
using MapBuilder.Server;
using MapBuilder.Tests.Fixtures;
using Xunit;
using static MapBuilder.Tests.Fixtures.RdrBuilder;

namespace MapBuilder.Tests;

/// <summary>
/// Readers from the disc survey (docs/formats/disc-survey.md): character types with their weapons (READERC.ZAR ›
/// character.rdr), multiplayer team rosters (chartype.rdr), scrolling textures (the map .ZED's TextureScroll_Object),
/// buffer tables (DBUFFER.ZAR) and shared value blocks in RdrFile trees. Synthetic data always; the retail checks run
/// when MB_MAP_ZDB (M51.ZDB) or MAPBUILDER_DISC (the disc root, holding RUN/) is set.
/// </summary>
public sealed class DiscTablesTests
{
    private static readonly Node SharedWeapons = L(
        Obj(("wep_name", "9mm Sub"), ("ammo_count", L(30)), ("mag_count", L(2))),
        F("wep_name", "Model 18"));

    // Shaped like character.rdr: settings first, then the flat "characters" list (name, [":", parent], fields).
    internal static byte[] Characters() => Build(L(Obj(
        ("AngleUnits", "DEG"),
        ("characters", L(
            "guard_alb", Obj(("anim_set", "Guard anim set"), ("default_weapons", L(F("wep_name", "AK-47")))),
            "albanian_terrorist01", ":", "guard_alb", Obj(("model_name", "alban_ter01.xsi"), ("texture_asset", "xalbania")),
            "m51_terror01", ":", "albanian_terrorist01", Obj(("default_weapons", SharedWeapons)),
            "m51_terror02", ":", "albanian_terrorist01", Obj(("default_gear", L("seal_A_right_eye", "Satchel")), ("default_weapons", L("wep_name", L("HE")))),
            "loop_a", ":", "loop_b", Obj(("model_name", "a.mb")),
            "loop_b", ":", "loop_a", Obj()
        )))));

    [Fact]
    public void Character_types_inherit_model_and_weapons_from_their_parents()
    {
        var types = CharacterTypes.Parse(Characters());
        Assert.Equal(6, types.Types.Count);
        Assert.Equal("albanian_terrorist01", types.Get("m51_terror01")!.Parent);

        var t1 = types.Resolve("M51_TERROR01")!;
        Assert.Equal(new[] { "m51_terror01", "albanian_terrorist01", "guard_alb" }, t1.Chain);
        Assert.Equal("alban_ter01", t1.Model);
        Assert.Equal("xalbania", t1.TextureAsset);
        Assert.Equal("Guard anim set", t1.AnimSet);
        Assert.Equal(new[] { new CharacterTypes.Weapon("9mm Sub", 30, 2), new CharacterTypes.Weapon("Model 18", null, null) }, t1.Weapons);

        // Its own list replaces the parent's; a lone pair is one weapon.
        var t2 = types.Resolve("m51_terror02")!;
        Assert.Equal(new[] { new CharacterTypes.Weapon("HE", null, null) }, t2.Weapons);
        Assert.Equal(new[] { "seal_A_right_eye", "Satchel" }, t2.Gear);

        // No weapons of its own: the grandparent's.
        Assert.Equal("AK-47", types.Resolve("albanian_terrorist01")!.Weapons.Single().Name);
        // A parent loop ends; unknown names are null.
        Assert.Equal(new[] { "loop_a", "loop_b" }, types.Resolve("loop_a")!.Chain);
        Assert.Null(types.Resolve("nobody"));
        Assert.Null(types.Resolve(null));
    }

    [Fact]
    public void A_multiplayer_roster_lists_each_teams_characters()
    {
        byte[] rdr = Build(L(L(
            "navyseals", L(Obj(("character", "mp51_seal1"), ("name", "Seal1")), Obj(("character", "mp51_seal2"), ("name", "Seal2"))),
            "terrorists", L(Obj(("character", "mp51_terror1"), ("name", "Terrorist1"))))));
        var teams = CharacterTypes.ParseRoster(rdr);
        Assert.Equal(new[] { "navyseals", "terrorists" }, teams.Select(t => t.Team));
        Assert.Equal(("mp51_seal2", "Seal2"), teams[0].Members[1]);
        Assert.Single(teams[1].Members);
    }

    [Fact]
    public void Shared_value_blocks_are_read_wherever_they_are_used()
    {
        // Two weapons point at one ModelName block, as zweapon.rdr's SR-25 and SR-25 SD do.
        var model = L("stoner_sr25");
        byte[] rdr = Build(L(L(
            L("InternalName", L("SR-25 SD"), "ModelName", model, "FireWait", L(0.2f)),
            L("InternalName", L("SR-25"), "ModelName", model, "FireWait", L(0.25f)),
            // M16A2's retail record holds a stray note between two fields.
            L("InternalName", L("M16A2"), "ImpactRadius", L(30), "adjusted", "from", 40, "Effective_Range", L(600), "ModelName", L("m16a2")))));
        var defs = WeaponDefs.Parse(rdr);
        Assert.Equal(new[] { "SR-25 SD", "SR-25", "M16A2" }, defs.Weapons.Select(w => w.InternalName));
        Assert.Equal(new[] { "stoner_sr25", "stoner_sr25", "m16a2" }, defs.Weapons.Select(w => w.ModelName));
        Assert.Equal(600, defs.Weapons[2].Fields["Effective_Range"]);
    }

    [Fact]
    public void Texture_scroll_rows_read_names_up_to_their_nul_and_both_speeds()
    {
        var rows = new[] { new TextureScroll.Row("m51_terrain", "riv1", 0.2f, 0f), new TextureScroll.Row("sky", "fogband1", 0f, -0.075f) };
        byte[] data = TextureScroll.Build(rows);
        Assert.Equal(2 * 520, data.Length);
        // Retail rows carry tool memory after each name's NUL.
        Encoding.ASCII.GetBytes("his poly").CopyTo(data, 20);
        Assert.Equal(rows, TextureScroll.Parse(data));
        Assert.Throws<InvalidDataException>(() => TextureScroll.Parse(new byte[519]));
    }

    [Fact]
    public void A_buffer_table_lists_each_archive_with_its_size()
    {
        using var zar = ZedContainer.CreateNew();
        var root = zar.AddEntry(BufferTable.RootName, Array.Empty<byte>(), 2);
        zar.AddEntry("M51_GEO.ZED", BitConverter.GetBytes(1020320u));
        zar.AddEntry("M51.ZED", BitConverter.GetBytes(8720u));
        zar.RootValue = 1;
        var rows = BufferTable.Parse(zar.ToBytes());
        Assert.Equal(new[] { ("M51_GEO.ZED", 1020320u), ("M51.ZED", 8720u) }, rows);
    }

    // ------------------------------------------------------------------ retail

    private static ZdbArchive? M51()
    {
        string? path = Environment.GetEnvironmentVariable("MB_MAP_ZDB");
        return path != null && File.Exists(path) && Path.GetFileName(path).Equals("M51.ZDB", StringComparison.OrdinalIgnoreCase) ? ZdbArchive.Open(path) : null;
    }

    private static byte[] Entry(ZdbArchive zdb, string fullPath) =>
        zdb.Entries.First(e => e.FullPath.Equals(fullPath, StringComparison.OrdinalIgnoreCase)).Data;

    [Fact]
    public void M51_scrolls_its_river_and_fog_bands()
    {
        using var zdb = M51();
        if (zdb == null) return;
        var node = ZedContainer.FromBytes(Entry(zdb, @"RUN\SP\M51\M51.ZED")).Nodes.Single(n => n.Name == "TextureScroll_Object");
        var rows = TextureScroll.Parse(node.Data);
        Assert.Equal(14, rows.Count);
        Assert.Equal(new TextureScroll.Row("m51_terrain", "riv1", 0.2f, 0f), rows[0]);
        Assert.Equal(12, rows.Count(r => r.Model == "m51_terrain" && r.U == 0.2f));
        Assert.Equal(new[] { "fogband1", "fogband2" }, rows.Where(r => r.Model == "sky").Select(r => r.Visual));
    }

    [Fact]
    public void M51s_buffer_table_lists_each_map_archives_body_size()
    {
        using var zdb = M51();
        if (zdb == null) return;
        var rows = BufferTable.Parse(Entry(zdb, @"RUN\SP\M51\DBUFFER.ZAR"));
        Assert.Equal(23, rows.Count);
        int equal = 0;
        foreach (var (archive, size) in rows)
        {
            var file = zdb.Entries.Single(e => e.FullPath.Equals(@"RUN\SP\M51\" + archive, StringComparison.OrdinalIgnoreCase));
            if (BufferTable.BodySize(file.Data) == size) equal++;
        }
        // FLIB_MDL.ZED and WORL_MDL.ZED are a few hundred bytes larger than listed in the retail file.
        Assert.Equal(21, equal);
    }

    private static string? Run()
    {
        string? disc = Environment.GetEnvironmentVariable("MAPBUILDER_DISC");
        string run = Path.Combine(disc ?? "", "RUN");
        return !string.IsNullOrEmpty(disc) && File.Exists(Path.Combine(run, "READERC.ZAR")) ? run : null;
    }

    [Fact]
    public void The_discs_character_types_give_m51s_smugglers_their_models_and_weapons()
    {
        if (Run() is not { } run) return;
        var record = ZedContainer.FromBytes(File.ReadAllBytes(Path.Combine(run, "READERC.ZAR"))).Nodes.Single(n => n.Name == "character.rdr");
        var types = CharacterTypes.Parse(record.Data);
        Assert.True(types.Types.Count > 400);
        var t = types.Resolve("m51_terror01")!;
        Assert.Equal("alban_ter01", t.Model);
        Assert.Equal(new[] { "9mm Sub", "Model 18" }, t.Weapons.Select(w => w.Name));

        // Every weapon a type names is a zweapon.rdr weapon with a model.
        var defs = WeaponDefs.Parse(ZedContainer.FromBytes(File.ReadAllBytes(Path.Combine(run, "ZWEAPON.ZAR"))).Nodes.Single(n => n.Name == "zweapon.rdr").Data);
        Assert.Contains(defs.Weapons, w => w.InternalName == "Dragunov" && w.ModelName == "Dragunov");
        Assert.Contains(defs.Weapons, w => w.InternalName == "SR-25" && w.ModelName == "stoner_sr25");
        var named = types.Types.SelectMany(x => x.Weapons ?? Array.Empty<CharacterTypes.Weapon>()).Select(w => w.Name).Distinct().ToList();
        Assert.All(named, n => Assert.Contains(defs.Weapons, w => w.InternalName.Equals(n, StringComparison.OrdinalIgnoreCase)));
    }
}

/// <summary>ASSET_MAP_TABLES with the disc's shared tables beside the package: actors gain their character type.</summary>
public sealed class MapTablesDiscTests
{
    private static byte[] Zar(params (string Name, byte[] Data)[] records)
    {
        using var zar = ZedContainer.CreateNew();
        foreach (var (name, data) in records) zar.AddEntry(name, data);
        zar.RootValue = (uint)records.Length;
        return zar.ToBytes();
    }

    [Fact]
    public void Actors_carry_their_character_types_weapons_and_the_map_its_scrolling_textures()
    {
        using var fixture = new SyntheticPackage();
        byte[] vehicles = Build(L(L(
            L(F("name", "Road1"), F("character", "m51_terror01")),
            L(F("name", "ghost"), F("character", "no_such_type")))));
        File.WriteAllBytes(Path.Combine(fixture.Folder, "READERM.ZAR"), Zar(("vehicles.rdr", vehicles)));
        using (var world = ZedContainer.CreateNew())
        {
            world.AddEntry("TextureScroll_Object", TextureScroll.Build(new[] { new TextureScroll.Row("tst_terrain", "riv1", 0.2f, 0f) }));
            world.RootValue = 1;
            File.WriteAllBytes(Path.Combine(fixture.Folder, "TST.ZED"), world.ToBytes());
        }
        // RUN-level tables beside TST.ZDB.
        string run = Path.GetDirectoryName(fixture.Folder)!;
        File.WriteAllBytes(Path.Combine(run, "READERC.ZAR"), Zar(("character.rdr", DiscTablesTests.Characters())));
        File.WriteAllBytes(Path.Combine(run, "ZWEAPON.ZAR"), Zar(("zweapon.rdr", Build(L(L(
            L("InternalName", L("9mm Sub"), "ModelName", L("uzi")),
            L("InternalName", L("Model 18"), "ModelName", L("glock18"))))))));

        var b = new Backend();
        string session = b.Open(fixture.WriteZdb());
        try
        {
            var t = b.Ok(new CommandMessage { Command = "ASSET_MAP_TABLES", AssetSession = session });
            Assert.True(t.GetProperty("hasCharacterTypes").GetBoolean());
            var road = t.GetProperty("actors")[0].GetProperty("chartype");
            Assert.Equal("alban_ter01", road.GetProperty("model").GetString());
            var weapons = road.GetProperty("weapons");
            Assert.Equal(2, weapons.GetArrayLength());
            Assert.Equal("9mm Sub", weapons[0].GetProperty("name").GetString());
            Assert.Equal("uzi", weapons[0].GetProperty("model").GetString());
            Assert.Equal(30, weapons[0].GetProperty("ammo").GetInt32());
            Assert.Equal("glock18", weapons[1].GetProperty("model").GetString());
            Assert.Equal(JsonValueKind.Null, t.GetProperty("actors")[1].GetProperty("chartype").ValueKind);

            var scroll = t.GetProperty("textureScroll");
            Assert.Equal(1, scroll.GetArrayLength());
            Assert.Equal("riv1", scroll[0].GetProperty("visual").GetString());
            Assert.Equal(0.2f, scroll[0].GetProperty("u").GetSingle());
            Assert.Equal(0, t.GetProperty("mapImages").GetArrayLength());
        }
        finally { b.Close(session); }
    }
}
