using System.Text.Json;
using CodeDesigner.Ps2.FileFormats.Zipper.Ca;
using CodeDesigner.Ps2.FileFormats.Zipper.ZDB;
using MapBuilder.Server;
using MapBuilder.Tests.Fixtures;
using Xunit;

namespace MapBuilder.Tests;

public sealed class CaRetailEditTests
{
    [Fact]
    public void Moving_the_Compound_start_only_changes_its_cell_and_checksum_on_the_disc_copy()
    {
        string? disc = Environment.GetEnvironmentVariable("MAPBUILDER_CA_DISC");
        if (string.IsNullOrEmpty(disc)) return;
        string run = Path.Combine(disc, "RUN");
        string source = Path.Combine(run, "9EA6A33E.ZAR");
        byte[] original = File.ReadAllBytes(source);
        var b = new Backend();
        string folder = Path.Combine(Path.GetTempPath(), "ca-retail-" + Guid.NewGuid().ToString("N"));
        string s = b.Open(Path.Combine(run, "COMPOUND", "SP432.ZDB"));
        try
        {
            var points = b.Points(s).GetProperty("points");
            Assert.Equal(73, points.GetArrayLength());
            var able = points.EnumerateArray().Single(p => p.GetProperty("name").GetString() == "s_Able1");
            float x = able.GetProperty("x").GetSingle(), y = able.GetProperty("y").GetSingle(), z = able.GetProperty("z").GetSingle();
            b.Edit(s, new { op = "movePoint", name = "s_Able1", position = new[] { x + 40f, y, z } });
            var movedPoints = b.Points(s).GetProperty("points");
            var moved = movedPoints.EnumerateArray().Single(p => p.GetProperty("name").GetString() == "s_Able1");
            Assert.True(moved.GetProperty("x").GetSingle() > x);
            b.Undo(s);
            Assert.Equal(points.GetRawText(), b.Points(s).GetProperty("points").GetRawText());
            b.Redo(s);
            Assert.Contains("output", b.Fails(new CommandMessage { Command = "ASSET_SAVE", AssetSession = s }));
            b.Ok(new CommandMessage { Command = "ASSET_SAVE", AssetSession = s, AssetJson = JsonSerializer.Serialize(new { outputPath = folder }) });
            string copy = Path.Combine(folder, Path.GetFileName(source));
            string reopened = b.Open(copy);
            try { Assert.Equal(movedPoints.GetRawText(), b.Points(reopened).GetProperty("points").GetRawText()); }
            finally { b.Close(reopened); }

            // SP432 mirrors the player's cell in squad start slot zero. Both cells and their resource CRC may change.
            using var outer = ZdbArchive.FromBytes(original);
            int fileIndex = outer.Entries.ToList().FindIndex(e => e.Name == "SP432.ZDB");
            Assert.True(fileIndex >= 0);
            int file = BitConverter.ToInt32(original, 0xA0 + fileIndex * 0x5C + 0x44);
            var innerBytes = outer.Entries[fileIndex].Data;
            using var inner = ZdbArchive.FromBytes(innerBytes);
            int aimIndex = inner.Entries.ToList().FindIndex(e => e.Tag == "AIMm");
            Assert.True(aimIndex >= 0);
            int row = 0xC8 + aimIndex * 0x58;
            var aim = inner.Entries[aimIndex].Data;
            var point = CaMissions.ParseMission(aim).Zones.SelectMany(zone => zone.Points).Single(p => p.Name == "s_Able1");
            int zone = BitConverter.ToInt32(aim, BitConverter.ToInt32(aim, 0) + 0x14 + point.Zone * 4);
            int resource = file + BitConverter.ToInt32(innerBytes, 0x0C) + BitConverter.ToInt32(innerBytes, row + 0x48);
            int cell = resource + point.RecordOffset;
            int squad = resource + BitConverter.ToInt32(aim, zone + 0x64);
            byte[] result = File.ReadAllBytes(copy);
            Assert.Equal(original.Length, result.Length);
            Assert.NotEqual(BitConverter.ToUInt32(original, cell), BitConverter.ToUInt32(result, cell));
            Assert.Equal(BitConverter.ToUInt32(original, cell), BitConverter.ToUInt32(original, squad));
            Assert.Equal(BitConverter.ToUInt32(result, cell), BitConverter.ToUInt32(result, squad));
            original.AsSpan(cell, 4).CopyTo(result.AsSpan(cell));
            original.AsSpan(squad, 4).CopyTo(result.AsSpan(squad));
            original.AsSpan(file + row + 0x50, 4).CopyTo(result.AsSpan(file + row + 0x50));
            Assert.True(original.AsSpan().SequenceEqual(result));
        }
        finally
        {
            b.Close(s);
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }
}
