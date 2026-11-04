using System.Text.Json;
using CodeDesigner.Ps2.FileFormats.Zipper.ZED;
using MapBuilder.Server;
using MapBuilder.Tests.Fixtures;
using Xunit;

namespace MapBuilder.Tests;

public sealed class MapActionsTests
{
    private static JsonElement Actions(Backend b, string session) =>
        b.Ok(new CommandMessage { Command = "ASSET_MAP_ACTIONS", AssetSession = session });

    [Fact]
    public void Add_first_binding_set_surface_save_and_reopen()
    {
        using var fixture = new SyntheticPackage();
        using var reader = ZedContainer.CreateNew();
        reader.AddEntry("other.rdr", [17, 23]);
        reader.RootValue = 1;
        File.WriteAllBytes(Path.Combine(fixture.Folder, "READERM.ZAR"), reader.ToBytes());
        var b = new Backend();
        string session = b.Open(fixture.WriteZdb());
        try
        {
            var before = Actions(b, session);
            int revision = before.GetProperty("revision").GetInt32();
            var binding = new { op = "mapAction", row = -1, revision, fields = new { node = new[] { "crate_1" },
                type = new[] { "SWITCH" }, range = new[] { 30 }, anim = new[] { "first", "second" }, one_shot = Array.Empty<int>() } };
            b.Edit(session, binding);
            Assert.Contains("changed", b.EditFails(session, binding));
            var after = Actions(b, session);
            Assert.Equal("second", after.GetProperty("rows")[0].GetProperty("anim")[1].GetString());
            var archive = ZedContainer.FromBytes(Backend.Bytes(session,"READERM.ZAR"));
            Assert.Equal(2u, archive.RootValue);
            Assert.Equal(new byte[] {17, 23}, archive.FindEntry("other.rdr")!.Data);
            var target = after.GetProperty("targets")[0].GetProperty("index").GetInt32();
            b.Edit(session,new { op = "traversalFace", revision = after.GetProperty("revision"), geoIndex = target, profile = "ladder", side = "+Z" });
            var changed = Actions(b, session);
            int face = changed.GetProperty("targets")[0].GetProperty("faces")[0].GetInt32();
            b.Edit(session,new { op = "traversalFace", revision = changed.GetProperty("revision"), geoIndex = target, profile = "climb", face });
            b.Edit(session,new { op = "mapAction", row = 0, revision = Actions(b,session).GetProperty("revision") });
            Assert.Empty(Actions(b,session).GetProperty("rows").EnumerateArray());
            b.Undo(session);
            string copy = Path.Combine(fixture.Folder,"copy.zdb");
            b.Ok(new CommandMessage { Command = "ASSET_SAVE", AssetSession = session, AssetJson = JsonSerializer.Serialize(new { mode = "zdb", outputPath = copy }) });
            string reopened = b.Open(copy);
            try
            {
                var data = Actions(b,reopened);
                Assert.Equal("crate_1",data.GetProperty("rows")[0].GetProperty("node")[0].GetString());
                var geo = ZedContainer.FromBytes(Backend.Bytes(reopened,"TST_GEO.ZED"));
                Assert.Equal("0000000003000000121C4044",Convert.ToHexString(geo.Nodes[face+1].Data));
                Assert.Equal(64,geo.Nodes[face+2].Data.Length);
            }
            finally { b.Close(reopened); }
        }
        finally { b.Close(session); }
    }

    [Fact]
    public void Retail_door_edit_preserves_neighbor_and_undo_restores_reader_bytes()
    {
        string? disc = Environment.GetEnvironmentVariable("MAPBUILDER_DISC");
        if (disc == null) return;
        var b = new Backend();
        string session = b.Open(Path.Combine(disc, "RUN", "MP51.ZDB"));
        try
        {
            var original = Backend.Bytes(session,"READERM.ZAR");
            var catalog = Actions(b,session);
            var rows = catalog.GetProperty("rows").EnumerateArray().ToArray();
            int index = Array.FindIndex(rows, r => r.GetProperty("node")[0].GetString() == "door_ft_1");
            var fields = rows[index].Deserialize<Dictionary<string,JsonElement>>()!;
            fields["range"] = JsonSerializer.SerializeToElement(new[] { 45 });
            b.Edit(session,new { op = "mapAction", revision = catalog.GetProperty("revision"), row = index, fields });
            var edited = Actions(b,session).GetProperty("rows");
            Assert.Equal(45,edited[index].GetProperty("range")[0].GetInt32());
            Assert.Equal(rows[index+1].GetRawText(),edited[index+1].GetRawText());
            b.Undo(session);
            Assert.Equal(original,Backend.Bytes(session,"READERM.ZAR"));

            var placements = catalog.GetProperty("targets").EnumerateArray()
                .Where(t => t.GetProperty("model").GetString()!.Length > 0)
                .GroupBy(t => t.GetProperty("name").GetString()).First(group => group.Count() > 1).ToArray();
            int picked = placements[1].GetProperty("index").GetInt32();
            byte[] geometry = Backend.Bytes(session,"MP51_GEO.ZED");
            fields["node"] = JsonSerializer.SerializeToElement(new[] { "test_unique_action" });
            b.Edit(session, new { op = "mapAction", row = -1, fields, renamePlacement = picked,
                revision = b.Status(session).GetProperty("revision") });
            var renamed = Actions(b,session);
            var target = renamed.GetProperty("targets").EnumerateArray().Single(t => t.GetProperty("name").GetString() == "test_unique_action");
            Assert.Equal(picked, target.GetProperty("index").GetInt32());
            Assert.Equal(placements[1].GetProperty("model").GetString(), target.GetProperty("model").GetString());
            b.Undo(session);
            Assert.Equal(original,Backend.Bytes(session,"READERM.ZAR"));
            Assert.Equal(geometry,Backend.Bytes(session,"MP51_GEO.ZED"));
        }
        finally { b.Close(session); }
    }
}
