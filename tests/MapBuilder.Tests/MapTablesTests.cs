using System.Text.Json;
using CodeDesigner.Ps2.FileFormats.Zipper.RDR;
using CodeDesigner.Ps2.FileFormats.Zipper.ZED;
using MapBuilder.Server;
using MapBuilder.Tests.Fixtures;
using Xunit;
using static MapBuilder.Tests.Fixtures.RdrBuilder;

namespace MapBuilder.Tests;

/// <summary>ASSET_MAP_TABLES and the mapTable / mapText edits, on synthetic mission tables shaped like M51's.</summary>
public sealed class MapTablesTests
{
    // Two actors share one accuracy value block, as retail vehicles.rdr does.
    private static readonly Node SharedAccuracy = L(0.4f);

    private static byte[] Objectives() => Build(L("HUD_OBJECTIVES", L(
        Obj(("NAME", "_1_Meet_MSG"), ("DESCRIPTION", "_2_Meet_Descr_MSG"), ("VALVE", "POBJ1"), ("LOCATION", L(2028.5f, 3328.75f)),
            ("ON_CALL_ANIMATION", "m51obj1"), ("NAV_POINTS", "Charlie")),
        Obj(("NAME", "_3_Unseen_MSG"), ("VALVE", "SOBJ1"), ("INVALID_VALVE", "SOBJ1Fail"), ("LOCATION", L(10f, -20f)), ("ISSECONDARY", L())))));

    private static byte[] Vehicles() => Build(L(L(
        L(F("name", "player"), F("display", "SPECTER"), F("character", "jungle_A51"), F("setup", L(L("sealstart", 4))), F("team", 1, 2, 31)),
        L(F("name", "Road1"), F("display", "Smuggler"), F("character", "m51_terror01"), F("setup", L(L("Road1", 7), L("Road1b", 6))), F("team", 5, 30),
            F("nofade", 1), F("ai_params", L(F("accuracy", SharedAccuracy), F("courage", 8.5f)))),
        L(F("name", "Road2"), F("display", "Smuggler"), F("character", "m51_terror02"), F("team", 5, 30), F("ai_params", L(F("accuracy", SharedAccuracy)))))));

    private static byte[] Mission() => Build(L(Obj(
        ("description", "_9_Mission_MSG"),
        ("elevation", Obj(("max", L(-322)), ("min", L(50)))),
        ("ai_params", Obj(("respawn_time", L(3)), ("respawn_rate", L(0.75f)))),
        ("Valves", L(Obj(("NAME", "Enable_M16"), ("VALUE", L(0))), Obj(("NAME", "Enable_mp5sd"), ("VALUE", L(1))))),
        ("SATCHEL_TIMER", L(-1)))));

    private static byte[] World() => Build(L(Obj(
        ("world_params", Obj(("name", "tst"), ("MetersPerUnit", L(0.1f)), ("GlobalLighting", Obj(("ambient", L(F("RGB", 16f, 15.5f, 15f))),
            ("light0", Obj(("RGB", L(172, 138, 103)), ("dir", L(0.1f, -0.1f, -0.2f)), ("active", L(1)))))))),
        ("camera", Obj(("clip", L(4, 850)), ("fog_standard", Obj(("enabled", L(1)), ("range", L(600, 875)), ("RGB", L(74, 74, 72)))))))));

    private static byte[] Extents() => Build(L("Map", Obj(("MinExtent", L(-1135f, -197.5f, -623.5f)), ("MaxExtent", L(5742f, 879f, 6254f)), ("NumFloors", L(0)), ("Floors", L()))));

    private static byte[] Language() => Build(L(
        Obj(("ID", L(1)), ("MSG_ID", "_1_Meet_MSG"), ("LOCALIZED", "MEET MALLARD")),
        Obj(("ID", L(2)), ("MSG_ID", "_2_Meet_Descr_MSG"), ("LOCALIZED", "Rendezvous with the informant."))));

    private static byte[] Zar(params (string Name, byte[] Data)[] records)
    {
        using var zar = ZedContainer.CreateNew();
        foreach (var (name, data) in records) zar.AddEntry(name, data);
        zar.RootValue = (uint)records.Length;
        return zar.ToBytes();
    }

    private static (SyntheticPackage Fixture, Backend B, string Session) Open()
    {
        var fixture = new SyntheticPackage();
        File.WriteAllBytes(Path.Combine(fixture.Folder, "READERM.ZAR"), Zar(("hudobjectives.rdr", Objectives()), ("vehicles.rdr", Vehicles()),
            ("mission.rdr", Mission()), ("tst.rdr", World()), ("map.rdr", Extents())));
        File.WriteAllBytes(Path.Combine(fixture.Folder, "TSTLOC.ZAR"), Zar(("tstLOC.rdr", Language())));
        var b = new Backend();
        return (fixture, b, b.Open(fixture.WriteZdb()));
    }

    private static JsonElement Tables(Backend b, string session) =>
        b.Ok(new CommandMessage { Command = "ASSET_MAP_TABLES", AssetSession = session });

    private static int Revision(Backend b, string session) => Tables(b, session).GetProperty("revision").GetInt32();

    [Fact]
    public void The_tables_decode_objectives_actors_mission_world_and_extents_with_their_text()
    {
        var (fixture, b, session) = Open();
        using var _ = fixture;
        try
        {
            var t = Tables(b, session);
            var objectives = t.GetProperty("objectives");
            Assert.Equal(2, objectives.GetArrayLength());
            Assert.Equal("_1_Meet_MSG", objectives[0].GetProperty("name").GetString());
            Assert.Equal(2028.5f, objectives[0].GetProperty("location")[0].GetSingle());
            Assert.Equal(3328.75f, objectives[0].GetProperty("location")[1].GetSingle());
            Assert.Equal("Charlie", objectives[0].GetProperty("navPoints")[0].GetString());
            Assert.True(objectives[1].GetProperty("secondary").GetBoolean());
            Assert.Equal("SOBJ1Fail", objectives[1].GetProperty("invalidValve").GetString());
            Assert.Equal("MEET MALLARD", t.GetProperty("texts").GetProperty("_1_Meet_MSG").GetString());
            Assert.Equal("Rendezvous with the informant.", t.GetProperty("texts").GetProperty("_2_Meet_Descr_MSG").GetString());

            var actors = t.GetProperty("actors");
            Assert.Equal(3, actors.GetArrayLength());
            Assert.Equal("SPECTER", actors[0].GetProperty("display").GetString());
            Assert.Equal("sealstart", actors[0].GetProperty("starts")[0].GetProperty("point").GetString());
            Assert.Equal(4, actors[0].GetProperty("starts")[0].GetProperty("value").GetInt32());
            Assert.Equal(new[] { 1, 2, 31 }, actors[0].GetProperty("teams").EnumerateArray().Select(x => x.GetInt32()));
            Assert.Equal(JsonValueKind.Null, actors[0].GetProperty("ai").ValueKind);
            Assert.Equal("Road1b", actors[1].GetProperty("starts")[1].GetProperty("point").GetString());
            Assert.Equal("nofade", actors[1].GetProperty("flags")[0].GetProperty("name").GetString());
            Assert.Equal(0.4f, actors[1].GetProperty("ai")[0].GetProperty("value").GetSingle());
            Assert.Equal(0.4f, actors[2].GetProperty("ai")[0].GetProperty("value").GetSingle());

            var mission = t.GetProperty("mission");
            Assert.Equal(-1, mission.GetProperty("satchelTimer").GetInt32());
            Assert.Equal(50, mission.GetProperty("elevation").GetProperty("min").GetSingle());
            Assert.Equal("respawn_rate", mission.GetProperty("ai")[1].GetProperty("name").GetString());
            Assert.Equal("Enable_mp5sd", mission.GetProperty("valves")[1].GetProperty("name").GetString());
            Assert.Equal(1, mission.GetProperty("valves")[1].GetProperty("value").GetInt32());

            var world = t.GetProperty("world");
            Assert.Equal(0.1f, world.GetProperty("metersPerUnit").GetSingle());
            Assert.Equal(15.5f, world.GetProperty("ambient")[1].GetSingle());
            Assert.Equal(138, world.GetProperty("lights")[0].GetProperty("rgb")[1].GetSingle());
            Assert.True(world.GetProperty("lights")[0].GetProperty("active").GetBoolean());
            Assert.Equal(850, world.GetProperty("clip")[1].GetSingle());
            Assert.Equal(875, world.GetProperty("fog").GetProperty("range")[1].GetSingle());
            Assert.Equal(6254, t.GetProperty("extents").GetProperty("max")[2].GetSingle());
        }
        finally { b.Close(session); }
    }

    [Fact]
    public void Moving_an_objective_changes_only_its_location_and_undoes_to_the_same_bytes()
    {
        var (fixture, b, session) = Open();
        using var _ = fixture;
        try
        {
            byte[] before = Backend.Bytes(session, "READERM.ZAR");
            b.Edit(session, new { op = "mapTable", table = "hudobjectives.rdr", path = new object[] { "HUD_OBJECTIVES", 0, "LOCATION" }, values = new[] { 100.25, -7.5 }, revision = Revision(b, session) });
            var objectives = Tables(b, session).GetProperty("objectives");
            Assert.Equal(100.25f, objectives[0].GetProperty("location")[0].GetSingle());
            Assert.Equal(-7.5f, objectives[0].GetProperty("location")[1].GetSingle());
            Assert.Equal(10f, objectives[1].GetProperty("location")[0].GetSingle());
            Assert.Equal("POBJ1", objectives[0].GetProperty("valve").GetString());
            // The other tables in the reader are untouched.
            var reader = ZedContainer.FromBytes(Backend.Bytes(session, "READERM.ZAR"));
            Assert.Equal(Vehicles(), reader.FindEntry("vehicles.rdr")!.Data);
            b.Undo(session);
            Assert.Equal(before, Backend.Bytes(session, "READERM.ZAR"));
        }
        finally { b.Close(session); }
    }

    [Fact]
    public void A_shared_value_changes_only_for_the_actor_edited()
    {
        var (fixture, b, session) = Open();
        using var _ = fixture;
        try
        {
            var actors = Tables(b, session).GetProperty("actors");
            var path = actors[1].GetProperty("path").EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number ? (object)x.GetInt32() : x.GetString()!)
                .Concat(new object[] { "ai_params", "accuracy" }).ToArray();
            b.Edit(session, new { op = "mapTable", table = "vehicles.rdr", path, values = new[] { 0.9 }, revision = Revision(b, session) });
            actors = Tables(b, session).GetProperty("actors");
            Assert.Equal(0.9f, actors[1].GetProperty("ai")[0].GetProperty("value").GetSingle());
            Assert.Equal(0.4f, actors[2].GetProperty("ai")[0].GetProperty("value").GetSingle());
            Assert.Equal(8.5f, actors[1].GetProperty("ai")[1].GetProperty("value").GetSingle());
            // A whole number keeps the field's type: a float field stays a float.
            var doc = RdrDocument.Parse(ZedContainer.FromBytes(Backend.Bytes(session, "READERM.ZAR")).FindEntry("vehicles.rdr")!.Data);
            int value = doc.FirstChild(doc.Resolve(path));
            Assert.Equal(RdrDocument.Float, doc.TypeOf(value));

            b.Edit(session, new { op = "mapTable", table = "mission.rdr", path = new object[] { "Valves", 0, "VALUE" }, values = new[] { 1 }, revision = Revision(b, session) });
            Assert.Equal(1, Tables(b, session).GetProperty("mission").GetProperty("valves")[0].GetProperty("value").GetInt32());
        }
        finally { b.Close(session); }
    }

    [Fact]
    public void Bad_table_edits_are_refused_and_change_nothing()
    {
        var (fixture, b, session) = Open();
        using var _ = fixture;
        try
        {
            byte[] before = Backend.Bytes(session, "READERM.ZAR");
            int revision = Revision(b, session);
            Assert.Contains("Expected 2", b.EditFails(session, new { op = "mapTable", table = "hudobjectives.rdr", path = new object[] { "HUD_OBJECTIVES", 0, "LOCATION" }, values = new[] { 1.0 }, revision }));
            Assert.Contains("No field", b.EditFails(session, new { op = "mapTable", table = "hudobjectives.rdr", path = new object[] { "HUD_OBJECTIVES", 0, "NOPE" }, values = new[] { 1.0 }, revision }));
            Assert.Contains("No item", b.EditFails(session, new { op = "mapTable", table = "hudobjectives.rdr", path = new object[] { "HUD_OBJECTIVES", 9, "LOCATION" }, values = new[] { 1.0, 2.0 }, revision }));
            Assert.Contains("whole number", b.EditFails(session, new { op = "mapTable", table = "mission.rdr", path = new object[] { "SATCHEL_TIMER" }, values = new[] { 1.5 }, revision }));
            Assert.Contains("mapAction", b.EditFails(session, new { op = "mapTable", table = "actions.rdr", path = new object[] { "actions" }, values = Array.Empty<int>(), revision }));
            Assert.Contains("changed", b.EditFails(session, new { op = "mapTable", table = "mission.rdr", path = new object[] { "SATCHEL_TIMER" }, values = new[] { 30 }, revision = revision + 5 }));
            Assert.Equal(before, Backend.Bytes(session, "READERM.ZAR"));
        }
        finally { b.Close(session); }
    }

    [Fact]
    public void Objective_text_changes_in_the_language_table()
    {
        var (fixture, b, session) = Open();
        using var _ = fixture;
        try
        {
            b.Edit(session, new { op = "mapText", key = "_1_Meet_MSG", text = "MEET THE INFORMANT", revision = Revision(b, session) });
            var texts = Tables(b, session).GetProperty("texts");
            Assert.Equal("MEET THE INFORMANT", texts.GetProperty("_1_Meet_MSG").GetString());
            Assert.Equal("Rendezvous with the informant.", texts.GetProperty("_2_Meet_Descr_MSG").GetString());
            Assert.Contains("No language table", b.EditFails(session, new { op = "mapText", key = "_404_MSG", text = "x", revision = Revision(b, session) }));
            b.Undo(session);
            Assert.Equal("MEET MALLARD", Tables(b, session).GetProperty("texts").GetProperty("_1_Meet_MSG").GetString());
        }
        finally { b.Close(session); }
    }

    [Fact]
    public void Paths_find_fields_in_both_forms_and_through_wrappers()
    {
        var doc = RdrDocument.Parse(Mission());
        Assert.Equal(new object?[] { 3 }, doc.Values(doc.Resolve(new object[] { "ai_params", "respawn_time" })));
        doc = RdrDocument.Parse(Vehicles());
        Assert.Equal(new object?[] { "Road1" }, doc.Values(doc.Resolve(new object[] { 0, 1, "name" })));
        Assert.Equal(-1, doc.Field(0, "missing"));
        // An unedited document saves to the same bytes; an edit leaves the shared block alone.
        Assert.Equal(Vehicles(), doc.ToBytes());
        doc.SetValues(new object[] { 0, 2, "ai_params", "accuracy" }, new[] { "0.1" });
        Assert.Equal(new object?[] { 0.4f }, doc.Values(doc.Resolve(new object[] { 0, 1, "ai_params", "accuracy" })));
        Assert.Equal(new object?[] { 0.1f }, doc.Values(doc.Resolve(new object[] { 0, 2, "ai_params", "accuracy" })));
    }

    [Fact]
    public void A_chartype_resolves_to_its_mapped_character_model_else_one_named_like_it()
    {
        var models = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["MESH_alban_ter01"] = "CLIB_MDL.ZED:MESH_alban_ter01", ["alban_ter02"] = "CLIB_MDL.ZED:alban_ter02", ["MESH_civ"] = "CLIB_MDL.ZED:MESH_civ",
        };
        var types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["m51_terror01"] = "alban_ter01", ["m51_terror02"] = "alban_ter02" };
        Assert.Equal("CLIB_MDL.ZED:MESH_alban_ter01", MapBuilder.Server.Services.ZipperAssetService.CharacterModel(models, "M51_TERROR01", types));
        Assert.Equal("CLIB_MDL.ZED:alban_ter02", MapBuilder.Server.Services.ZipperAssetService.CharacterModel(models, "m51_terror02", types));
        Assert.Equal("CLIB_MDL.ZED:MESH_civ", MapBuilder.Server.Services.ZipperAssetService.CharacterModel(models, "civ", types));
        Assert.Null(MapBuilder.Server.Services.ZipperAssetService.CharacterModel(models, "nobody", types));
        Assert.Null(MapBuilder.Server.Services.ZipperAssetService.CharacterModel(models, null, types));

        string file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "# comment\nbasic_seal\t\njungle_A51\tseal_A_woodland_LO\r\n");
            var read = MapBuilder.Server.Services.ZipperAssetService.ReadCharTypes(file);
            Assert.Equal("seal_A_woodland_LO", read["JUNGLE_A51"]);
            Assert.False(read.ContainsKey("basic_seal"));
        }
        finally { File.Delete(file); }
        // The shipped list is copied next to the server.
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "data", "chartypes.tsv")));
    }
}
