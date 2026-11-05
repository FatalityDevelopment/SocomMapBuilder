using System.Text;
using CodeDesigner.Ps2.FileFormats.Zipper.Ca;
using Xunit;

namespace MapBuilder.Tests;

/// <summary>
/// SOCOM 3 / Combined Assault mission tables (CaMissionTables): actors (vehicles.rdr), character types (character.rdr),
/// the weapon table, the mission header (MISN) and valves (VALV). Synthetic always; the retail check runs when
/// MAPBUILDER_CA_DISC (the disc root, e.g. G:\) is set.
/// </summary>
public sealed class CaMissionTablesTests
{
    private static void Put(byte[] d, int at, uint v) => BitConverter.GetBytes(v).CopyTo(d, at);
    private static void Put(byte[] d, int at, string s) => Encoding.ASCII.GetBytes(s).CopyTo(d, at);

    [Fact]
    public void Actors_give_name_display_type_and_starts()
    {
        var d = new byte[0x14 + 2 * 0x1EC];
        Put(d, 0, 0x14u);
        Put(d, 4, (uint)d.Length);
        Put(d, 0x14, "player"); Put(d, 0x34, "_15020_SPECTER_OCN_MSG"); Put(d, 0x54, 0x9B290F55u);
        Put(d, 0x14 + 0x54, 1u); Put(d, 0x14 + 0x58, 1u); Put(d, 0x14 + 0x5C, "s_Able1"); Put(d, 0x14 + 0x5C + 28, 2u);
        int o = 0x14 + 0x1EC;
        Put(d, o, "tech_suv1"); Put(d, o + 0x40, 0xABCDu); Put(d, o + 0x54, 2u); Put(d, o + 0x58, 1u);   // an online start: empty

        var actors = CaMissionTables.ParseActors(d);

        Assert.Equal(2, actors.Count);
        Assert.Equal(("player", "_15020_SPECTER_OCN_MSG", 0x9B290F55u, 1), (actors[0].Name, actors[0].Display, actors[0].Type, actors[0].Group));
        Assert.Equal(new[] { new CaMissionTables.Start("s_Able1", 2) }, actors[0].Starts);
        Assert.Empty(actors[1].Starts);
        Assert.Throws<InvalidDataException>(() => CaMissionTables.ParseActors(d[..^4]));
    }

    [Fact]
    public void Character_types_give_models_display_and_weapons()
    {
        var d = new byte[0x20 + 0x440 + 8];
        Put(d, 8, 0x10u); Put(d, 12, 0x14u);         // one record offset
        Put(d, 0x10, 0x20u);
        int o = 0x20, list = 0x20 + 0x440;
        Put(d, o, 0xD018D5F2u); Put(d, o + 4, 0x66B89D0Bu); Put(d, o + 0x1F4, 0x61B7C4C4u);
        Put(d, o + 0x224, (uint)list); Put(d, o + 0x228, (uint)(list + 8));
        Put(d, list, 0x57D83981u); Put(d, list + 4, 0x24933A0Cu);
        Put(d, o + 0x404, "DEFENSE FORCE");

        var t = Assert.Single(CaMissionTables.ParseCharacterTypes(d));

        Assert.Equal((0xD018D5F2u, 0x66B89D0Bu, 0x61B7C4C4u, "DEFENSE FORCE"), (t.Hash, t.Model, t.LowModel, t.Display));
        Assert.Equal(new[] { 0x57D83981u, 0x24933A0Cu }, t.Weapons);
    }

    [Fact]
    public void Weapons_are_found_by_hash_in_either_record_shape()
    {
        var d = new byte[0x400];
        Put(d, 0x40, 0x57D83981u); Put(d, 0x4C, 0x0F974D33u); Put(d, 0x84, "ASSAULT RIFLE: AG-94"); Put(d, 0xA4, "_15564_Areplacementf_OCN_MSG");
        Put(d, 0x140, 0x10DA97EEu); Put(d, 0x17C, "ATTACHMENT: PISTOL SUPPRESSOR"); Put(d, 0x19C, "_17242_Thisnoisesupp_UI_MSG");
        Put(d, 0x240, 0xB6A2F3FBu); Put(d, 0x2A4, "_15607_Thisfragmentat_OCN_MSG");   // a grenade: no display name

        var w = CaMissionTables.FindWeapons(d, new[] { 0x57D83981u, 0x10DA97EEu, 0xB6A2F3FBu, 0x12345678u });

        Assert.Equal(("weapon", "ASSAULT RIFLE: AG-94", (uint?)0x0F974D33u), (w[0x57D83981].Kind, w[0x57D83981].Display, w[0x57D83981].Model));
        Assert.Equal(("item", "ATTACHMENT: PISTOL SUPPRESSOR"), (w[0x10DA97EE].Kind, w[0x10DA97EE].Display));
        Assert.Equal(("weapon", (string?)null, "_15607_Thisfragmentat_OCN_MSG"), (w[0xB6A2F3FB].Kind, w[0xB6A2F3FB].Display, w[0xB6A2F3FB].Key));
        Assert.False(w.ContainsKey(0x12345678));
    }

    [Fact]
    public void A_header_lists_strings_types_weapons_and_valves_and_valves_carry_values()
    {
        var d = new byte[0x200];
        Put(d, 0x10, 1u); Put(d, 0x14, 0x100u); Put(d, 0x100, 0x9B290F55u);
        Put(d, 0x20, 2u); Put(d, 0x24, 0x110u);
        Put(d, 0x110, 1u); Put(d, 0x114, 0x180u); Put(d, 0x118, 2u); Put(d, 0x11C, 0x1A0u);
        Put(d, 0x180, @"RUN\RAW\MAPS\m_432.RAW"); Put(d, 0x1A0, "_37531_1Infiltrateh_UI_MSG");
        Put(d, 0x30, 1u); Put(d, 0x34, 0x104u); Put(d, 0x104, 0xD3E46CCEu);
        Put(d, 0x4C, 1u); Put(d, 0x50, 0x108u); Put(d, 0x108, 0x6C448E95u);

        var h = CaMissionTables.ParseHeader(d);
        Assert.Equal(new[] { @"RUN\RAW\MAPS\m_432.RAW", "_37531_1Infiltrateh_UI_MSG" }, h.Strings);
        Assert.Equal((0x9B290F55u, 0xD3E46CCEu, 0x6C448E95u), (h.CharacterTypes.Single(), h.Weapons.Single(), h.Valves.Single()));

        var v = new byte[16];
        Put(v, 0, 0x9BC1C1C3u); Put(v, 4, 0x03010002u); Put(v, 8, 0x6C448E95u); Put(v, 12, 0x0301000Fu);
        Assert.Equal(new[] { (0x9BC1C1C3u, 2), (0x6C448E95u, 15) }, CaMissionTables.ParseValves(v));

        Assert.Equal("JESTER", CaMissionTables.KeyText("_14867_JESTER_HUD_MSG"));
        Assert.Equal("M1114", CaMissionTables.KeyText("_16588_M1114_UI_MSG"));
    }

    [Fact]
    public void The_retail_compound_squad_and_rural_vehicles_resolve()
    {
        string? disc = Environment.GetEnvironmentVariable("MAPBUILDER_CA_DISC");
        string run = Path.Combine(disc ?? "", "RUN");
        if (string.IsNullOrEmpty(disc) || !File.Exists(Path.Combine(run, "9EA6A33E.ZAR"))) return;

        var compound = CaPackage.Open(Path.Combine(run, "9EA6A33E.ZAR"));
        var actors = CaMissionTables.ParseActors(compound.MissionResource("SP432", "MSNv")!.Data);
        Assert.Equal(new[] { "player", "able2", "bravo1", "bravo2", "ext_boat1" }, actors.Select(a => a.Name));
        Assert.Equal("s_Able2", actors[1].Starts.Single().Point);

        var types = CaMissionTables.ParseCharacterTypes(compound.MissionResource("SP432", "CHAR")!.Data);
        var player = types.Single(t => t.Hash == actors[0].Type);
        Assert.NotNull(compound.ModelByHash(player.Model, "character"));
        Assert.NotEmpty(player.Weapons);
        Assert.Contains(types, t => t.Display == "BULLFROG");

        // Every squad weapon is in GLOBAL's weapon table, and the guns name a model the package has.
        var weapons = CaMissionTables.FindWeapons(compound.GlobalResource("WEAP")!.Data, types.Where(t => t.Model != 0).SelectMany(t => t.Weapons));
        Assert.All(player.Weapons, h => Assert.True(weapons.ContainsKey(h), $"{h:x8}"));
        var guns = player.Weapons.Select(h => weapons[h]).Where(w => w.Display != null && w.Kind == "weapon").ToList();
        Assert.NotEmpty(guns);
        Assert.All(guns, g => Assert.NotNull(compound.ModelByHash(g.Model!.Value, "weapon")));

        var header = CaMissionTables.ParseHeader(compound.MissionResource("SP432", "MISN")!.Data);
        Assert.Contains(@"RUN\RAW\MAPS\m_432.RAW", header.Strings);
        Assert.Equal(header.Valves.Count, CaMissionTables.ParseValves(compound.MissionResource("SP432", "VALV")!.Data).Count);

        // POLAND_RURAL MP341CTR: its vehicles are GLOBAL ai_vehicles types, not characters.
        var rural = CaPackage.Open(Path.Combine(run, "055146D2.ZAR"));
        var gveh = rural.GlobalResource("GVEH")!.Data;
        var suv = CaMissionTables.ParseActors(rural.MissionResource("MP341CTR", "MSNv")!.Data).First(a => a.Name == "tech_suv1");
        Assert.True(gveh.AsSpan().IndexOf(BitConverter.GetBytes(suv.Type)) >= 0);
    }
}
