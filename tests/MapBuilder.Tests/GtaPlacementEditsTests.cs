using System.Text;
using System.Text.Json;
using CodeDesigner.Ps2.FileFormats.Gta;
using MapBuilder.Server;
using MapBuilder.Tests.Fixtures;
using Xunit;

namespace MapBuilder.Tests;

public sealed class GtaPlacementEditsTests
{
    [Fact]
    public void Mixed_placement_batch_undoes_and_saves_both_IPL_formats_with_working_LODs()
    {
        var root = Directory.CreateTempSubdirectory("gta-place-");
        string data = Directory.CreateDirectory(Path.Combine(root.FullName, "DATA", "MAPS")).FullName;
        string models = Directory.CreateDirectory(Path.Combine(root.FullName, "MODELS")).FullName;
        File.WriteAllText(Path.Combine(data, "TEST.IDE"), "objs\n10, house, test\n11, distant, test\nend\n");
        File.WriteAllText(Path.Combine(data, "TEST.IPL"), "# source\r\ninst\r\n10, house, 0, 1, 2, 3, 0, 0, 0, 1, -1\r\n11, distant, 0, 4, 5, 6, 0, 0, 0, 1, -1\r\nend\r\n");
        var img = MakeArchive();
        File.WriteAllBytes(Path.Combine(models, "GTA3.IMG"), img);
        File.WriteAllBytes(Path.Combine(models, "GTA3_1.IMG"), img);
        var backend = new Backend();
        string session = backend.Open(root.FullName);
        try
        {
            const string first = "test:test:0", stream = "test:test_stream0:0";
            object Place(string template, int east) => new { op = "placeInstance", model = "house", archive = "GTA3.IMG", templateInstance = template, position = new[] { east, 8, 9 } };
            var ops = new object[] { Place(first, 55), Place(stream, 66), Place(stream, 77),
                new { op = "deleteInstance", instance = first }, new { op = "deleteInstance", instance = stream } };
            // An invalid final op must roll back both formats and restore the shared LOD reference.
            backend.EditFails(session, new { op = "batch", ops = ops.Append(new { op = "deleteInstance", instance = "bad" }) });
            Assert.Empty(backend.Status(session).GetProperty("dirty").EnumerateArray());
            var edits = backend.Edit(session, new { op = "batch", ops }).GetProperty("results");
            string newId = edits[1].GetProperty("newInstance").GetString()!;
            backend.Undo(session);
            Assert.Empty(backend.Status(session).GetProperty("dirty").EnumerateArray());
            backend.Redo(session);
            var scene = backend.Scene(session).GetProperty("instances").EnumerateArray();
            Assert.Contains(scene, i => i.GetProperty("instance").GetString() == newId);
            string output = Path.Combine(root.FullName, "saved");
            backend.Ok(new CommandMessage { Command = "ASSET_SAVE", AssetSession = session, AssetJson = JsonSerializer.Serialize(new { mode = "map", outputPath = output }) });
            var outputData = Path.Combine(output, "DATA", "MAPS");
            File.Copy(Path.Combine(data, "TEST.IDE"), Path.Combine(outputData, "TEST.IDE"));
            var reopened = GtaPackage.Open(output);
            var rows = reopened.Placements;
            Assert.Equal(5, rows.Count);
            Assert.Contains(rows, p => p.X == 55 && p.Row == 1 && p.File.EndsWith("TEST.IPL", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(rows, p => p.X == 66 && p.Row == 1 && p.File.Contains("_stream"));
            Assert.Contains(rows, p => p.X == 77 && p.Row == 2 && p.File.Contains("_stream"));
            var child = Assert.Single(rows, p => p.LodRow >= 0);
            Assert.Equal(0, child.LodRow);
            Assert.Equal("distant", reopened.LodOf(child)!.Name);
            var archive = reopened.Archives.Single();
            var binary = archive.Read("test_stream0.ipl")!;
            Assert.Equal(3, BitConverter.ToInt32(binary, 4));
            int cars = BitConverter.ToInt32(binary, 60);
            Assert.Equal(76 + 3 * 40, cars);
            // The car generator section, and the untouched model entry, survive the IMG rewrite.
            Assert.Equal(img.AsSpan(2048 + 76 + 80, 48).ToArray(), binary.AsSpan(cars, 48).ToArray());
            Assert.Equal(img.AsSpan(4096, 2048).ToArray(), archive.Read("house.dff"));
            Assert.Equal(File.ReadAllBytes(Path.Combine(output, "MODELS", "GTA3.IMG")), File.ReadAllBytes(Path.Combine(output, "MODELS", "GTA3_1.IMG")));
        }
        finally { backend.Close(session); root.Delete(true); }
    }

    [Fact]
    public void An_empty_interior_area_can_be_repopulated_with_the_correct_archive()
    {
        var root = Directory.CreateTempSubdirectory("gta-interior-");
        var b = new Backend();
        string? session = null;
        try
        {
            string data = Directory.CreateDirectory(Path.Combine(root.FullName, "DATA", "MAPS")).FullName;
            string models = Directory.CreateDirectory(Path.Combine(root.FullName, "MODELS")).FullName;
            File.WriteAllText(Path.Combine(data, "ROOM.IDE"), "objs\n10, house, tex\n11, distant, tex\nend\n");
            File.WriteAllText(Path.Combine(data, "ROOM.IPL"), "inst\n10, house, 259, 0, 0, 0, 0, 0, 0, 1, -1\nend\n");
            var archive = MakeArchive();
            File.WriteAllBytes(Path.Combine(models, "GTA3.IMG"), archive);
            File.WriteAllBytes(Path.Combine(models, "GTA_INT.IMG"), archive);
            session = b.Open(root.FullName);
            string area = b.Status(session).GetProperty("mission").GetString()!;
            Assert.Equal("room#interiors", area);
            b.Edit(session, new { op = "deleteInstance", instance = "room:room:0" });
            Assert.Empty(b.Scene(session).GetProperty("instances").EnumerateArray());
            var wrong = b.EditFails(session, new { op = "placeInstance", model = "house", archive = "GTA3.IMG", position = new[] { 8, 2, 1 } });
            Assert.Contains("IDE id 10", wrong);
            b.Edit(session, new { op = "placeInstance", model = "house", archive = "GTA_INT.IMG", position = new[] { 8, 2, 1 } });
            var row = Assert.Single(b.Scene(session).GetProperty("instances").EnumerateArray());
            Assert.Equal("GTA_INT.IMG:house", row.GetProperty("source").GetString());
            b.Undo(session);
            b.Undo(session);
            Assert.Empty(b.Status(session).GetProperty("dirty").EnumerateArray());
            Assert.Equal("room:ROOM:0", Assert.Single(b.Scene(session).GetProperty("instances").EnumerateArray()).GetProperty("instance").GetString());
        }
        finally
        {
            if (session != null) b.Close(session);
            root.Delete(true);
        }
    }

    private static byte[] MakeArchive()
    {
        byte[] img = new byte[4 * 2048];
        "VER2"u8.CopyTo(img);
        BitConverter.TryWriteBytes(img.AsSpan(4), 3);
        string[] names = { "test_stream0.ipl", "house.dff", "distant.dff" };
        for (int i = 0; i < names.Length; i++)
        {
            var entry = img.AsSpan(8 + i * 32, 32);
            BitConverter.TryWriteBytes(entry, i + 1);
            BitConverter.TryWriteBytes(entry[4..], (ushort)1);
            Encoding.ASCII.GetBytes(names[i]).CopyTo(entry[8..]);
        }
        var data = img.AsSpan(2048, 2048);
        "bnry"u8.CopyTo(data);
        BitConverter.TryWriteBytes(data[4..], 2);
        BitConverter.TryWriteBytes(data[20..], 1);
        BitConverter.TryWriteBytes(data[28..], 76);
        BitConverter.TryWriteBytes(data[60..], 76 + 80);
        for (int i = 0; i < 2; i++)
        {
            var row = data[(76 + i * 40)..];
            BitConverter.TryWriteBytes(row, i + 15f);
            BitConverter.TryWriteBytes(row[24..], 1f);
            BitConverter.TryWriteBytes(row[28..], 10);
            BitConverter.TryWriteBytes(row[36..], 1);
        }
        for (int i = 0; i < 48; i++) data[76 + 80 + i] = (byte)(200 + i);
        img.AsSpan(4096, 2048).Fill(0x0a);
        return img;
    }
}
