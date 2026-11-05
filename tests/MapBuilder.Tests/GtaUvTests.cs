using System.Text.Json;
using CodeDesigner.Ps2.FileFormats.Gta;
using MapBuilder.Server;
using MapBuilder.Tests.Fixtures;
using Xunit;

namespace MapBuilder.Tests;

[Collection(MapPackageCollection.Name)]
public sealed class GtaUvTests
{
    private static byte[] Data(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        write(w);
        return stream.ToArray();
    }

    private static byte[] Chunk(uint type, params byte[][] children) => Data(w =>
    {
        w.Write(type); w.Write(children.Sum(b => b.Length)); w.Write(0x1803FFFF);
        foreach (var child in children) w.Write(child);
    });

    [Fact]
    public void Fixed_point_UV_edits_preserve_the_second_UV_set_and_reject_overflow_or_conflicting_aliases()
    {
        var packet = Data(w =>
        {
            w.Write(0x60000005); w.Write(new byte[12]);  // DMA RET with 5 quadwords
            w.Write(0x68030000); w.Write(new byte[36]);  // 3 xyz float positions
            w.Write(0x6D030001);                        // two UV sets of signed 16-bit values
            for (int i = 0; i < 3; i++)
            {
                w.Write((short)(i * 100)); w.Write((short)-512);
                w.Write((short)12345); w.Write((short)-12345);
            }
            w.Write(0x04000003); w.Write(0x17000000); w.Write(0);
        });
        var mesh = Data(w => { w.Write(packet.Length); w.Write(0x1234ABCD); w.Write(packet); });
        var header = Data(w => { w.Write(0x01000004); w.Write(1); w.Write(3); w.Write(1); w.Write(new byte[24]); });
        var native = Chunk(Rw.NativeData, Chunk(Rw.Struct, BitConverter.GetBytes(4), mesh));
        var dff = Chunk(Rw.Clump, Chunk(Rw.GeometryList, Chunk(Rw.Geometry, Chunk(Rw.Struct, header), Chunk(Rw.Extension, native))));
        int source = dff.AsSpan().IndexOf(mesh);
        Assert.True(source > 0);
        var edit = new GtaUvEdits.Chunk(source, new[] { new GtaUvEdits.Vertex(1, .3333f, .75f) });
        byte[] actual = GtaUvEdits.Set(dff, new[] { edit, edit });
        int at = dff.AsSpan().IndexOf(BitConverter.GetBytes(0x6D030001)) + 4 + 8;
        byte[] expected = (byte[])dff.Clone();
        BitConverter.GetBytes((short)1365).CopyTo(expected, at);
        BitConverter.GetBytes((short)1024).CopyTo(expected, at + 2);   // native V=.25
        Assert.Equal(expected, actual);
        Assert.Throws<ArgumentException>(() => GtaUvEdits.Set(dff, new[] { edit, new(source, new[] { new GtaUvEdits.Vertex(1, .5f, .75f) }) }));
        Assert.Throws<ArgumentException>(() => GtaUvEdits.Set(dff, new[] { new GtaUvEdits.Chunk(source, new[] { new GtaUvEdits.Vertex(0, 8, 0) }) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => GtaUvEdits.Set(dff, new[] { new GtaUvEdits.Chunk(source, new[] { new GtaUvEdits.Vertex(3, 0, 0) }) }));
    }

    [Fact]
    public void Plain_UV_edits_preserve_subpixel_source_bits_and_the_second_float_set()
    {
        var firstSet = Data(w =>
        {
            w.Write(.1875f); w.Write(1e-10f); w.Write(.25f); w.Write(.5f); w.Write(.75f); w.Write(1f);
        });
        var geometry = Data(w =>
        {
            w.Write(0x00020080); w.Write(1); w.Write(3); w.Write(1);  // two UV sets, one triangle, one morph
            w.Write(firstSet);
            foreach (float value in new[] { -.5f, .25f, 1.5f, .5f, 2f, 1f }) w.Write(value);
            foreach (ushort value in new ushort[] { 1, 0, 0, 2 }) w.Write(value);
            w.Write(new byte[16]); w.Write(1); w.Write(0); w.Write(new byte[36]);
        });
        var dff = Chunk(Rw.Clump, Chunk(Rw.GeometryList, Chunk(Rw.Geometry, Chunk(Rw.Struct, geometry))));
        int offset = dff.AsSpan().IndexOf(firstSet);
        Assert.True(offset > 0);
        var actual = GtaUvEdits.Set(dff, new[] { new GtaUvEdits.Chunk(offset, new[] {
            new GtaUvEdits.Vertex(0, .625f, 1f),   // displayed V rounds to 1; preserve the original 1e-10
            new GtaUvEdits.Vertex(2, .75f, .875f),
        }) });
        byte[] expected = (byte[])dff.Clone();
        BitConverter.GetBytes(.625f).CopyTo(expected, offset);
        BitConverter.GetBytes(.125f).CopyTo(expected, offset + 20);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Retail_UVs_edited_through_the_session_reopen_on_the_same_displayed_vertex()
    {
        string? disc = Environment.GetEnvironmentVariable("MAPBUILDER_GTA_DISC");
        if (string.IsNullOrEmpty(disc)) return;
        var service = new Backend();
        var dir = Directory.CreateTempSubdirectory("mb-gta-uv-");
        string session = service.Open(disc);
        JsonElement Command(string id, string cmd, string archive, string model, object args) => service.Ok(new CommandMessage {
            Command = cmd, AssetSession = id, AssetArchive = archive, AssetName = model, AssetJson = JsonSerializer.Serialize(args),
        });
        try
        {
            foreach (var (archive, name) in new[] { ("GTA3.IMG", "infernus"), ("GTA3.IMG", "lapd1"), ("PLAYER.IMG", "vest") })
            {
                var mesh = Command(session, "ASSET_MODEL_MESH", archive, name, new { arrays = true });
                var chunk = mesh.GetProperty("chunks").EnumerateArray().First(c => c.GetProperty("geometryOffset").GetInt32() >= 0);
                int source = chunk.GetProperty("geometryOffset").GetInt32(), vertex = chunk.GetProperty("vertexCount").GetInt32() - 1;
                var old = Convert.FromBase64String(chunk.GetProperty("uvs").GetString()!);
                float u = BitConverter.ToSingle(old, vertex * 8) == .75f ? .25f : .75f;
                float v = BitConverter.ToSingle(old, vertex * 8 + 4) == .875f ? .125f : .875f;
                var chunks = new[] { new { geometryOffset = source, vertices = new[] { new { i = vertex, uv = new[] { u, v } } } } };
                int revision = service.Status(session).GetProperty("revision").GetInt32();
                service.Edit(session, new { op = "gtaUv", archive, model = name, chunks, revision });
                service.Undo(session);
                Assert.Empty(service.Status(session).GetProperty("dirty").EnumerateArray());
                service.Redo(session);
                string file = Path.Combine(dir.FullName, name + ".dff");
                Command(session, "ASSET_SAVE", archive, name, new { mode = "dff", archive, model = name, outputPath = file });
                var copy = GtaPackage.Open(file);
                var rendered = copy.BuildMesh(Assert.Single(copy.Models)).Chunks.First(c => c.GeometryOffset == source);
                Assert.Equal(u, rendered.Uvs[vertex * 2]);
                Assert.Equal(v, rendered.Uvs[vertex * 2 + 1]);
                service.Undo(session);
            }
        }
        finally { service.Close(session); dir.Delete(true); }
    }
}
