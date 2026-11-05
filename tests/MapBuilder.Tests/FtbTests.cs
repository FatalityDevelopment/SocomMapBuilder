using CodeDesigner.Ps2.FileFormats.Assets;
using CodeDesigner.Ps2.FileFormats.Zipper.Ca;
using CodeDesigner.Ps2.FileFormats.Zipper.ZDB;
using Xunit;

namespace MapBuilder.Tests;

/// <summary>
/// SOCOM: Fireteam Bravo 2 (PSP): version-7 ZDBs (ZdbArchive), GE display lists, vertex formats and swizzled CLUT
/// textures (PspGe), and maps opened through CaPackage. Synthetic always; the retail check runs when
/// MAPBUILDER_FTB2_DISC (the UMD root, e.g. K:\) is set.
/// </summary>
public sealed class FtbTests
{
    private static void Put(byte[] d, int at, uint v) => BitConverter.GetBytes(v).CopyTo(d, at);
    private static void PutF(byte[] d, int at, float v) => BitConverter.GetBytes(v).CopyTo(d, at);

    /// <summary>A version-7 ZDB: a DEP_ descriptor with a -1 size word, then one ISLE payload.</summary>
    private static byte[] V7Archive(byte[] payload)
    {
        const int files = 2, headers = 0xC8 + files * 0x58, start = (headers + 0x3F) & ~0x3F;
        int body = (payload.Length + 15) & ~15;
        var d = new byte[start + body];
        Put(d, 0x00, 0xC8); Put(d, 0x04, 0x58); Put(d, 0x08, 7); Put(d, 0x0C, 1);
        Put(d, 0x10, (uint)start); Put(d, 0x14, (uint)body); Put(d, 0x18, 0x45283DF1); Put(d, 0x20, 2);
        Put(d, 0xC4, files);
        System.Text.Encoding.ASCII.GetBytes("keystone.ZDB").CopyTo(d, 0xC8);
        System.Text.Encoding.ASCII.GetBytes("DEP_").CopyTo(d, 0xC8 + 0x40);
        Put(d, 0xC8 + 0x4C, 0xFFFFFFFF);
        int e = 0xC8 + 0x58;
        System.Text.Encoding.ASCII.GetBytes("island.src").CopyTo(d, e);
        System.Text.Encoding.ASCII.GetBytes("ISLE").CopyTo(d, e + 0x40);
        Put(d, e + 0x44, 70); Put(d, e + 0x48, 0); Put(d, e + 0x4C, (uint)payload.Length);
        Put(d, e + 0x50, 0x1234ABCD); // kept as read
        payload.CopyTo(d, start);
        return d;
    }

    [Fact]
    public void Version7_header_is_read_at_its_shifted_offsets_and_round_trips()
    {
        var payload = Enumerable.Range(0, 40).Select(i => (byte)i).ToArray();
        var bytes = V7Archive(payload);
        using var zdb = ZdbArchive.FromBytes(bytes);
        Assert.Equal(7u, zdb.Version);
        Assert.Equal(2, zdb.EntryCount);
        Assert.Equal("DEP_", zdb.GetEntry(0).Tag);
        Assert.Empty(zdb.GetEntry(0).Data);
        Assert.Equal("ISLE", zdb.GetEntry(1).Tag);
        Assert.Equal(payload, zdb.GetEntry(1).Data);
        Assert.Equal(bytes, zdb.ToBytes());

        // A size-changing edit rebuilds the archive in the version-7 layout and keeps the DEP_ size word.
        zdb.ReplaceEntryData(1, new byte[100]);
        var rebuilt = zdb.ToBytes();
        Assert.Equal(7u, BitConverter.ToUInt32(rebuilt, 0x08));
        Assert.Equal(1u, BitConverter.ToUInt32(rebuilt, 0x0C));
        Assert.Equal(0x180u, BitConverter.ToUInt32(rebuilt, 0x10));
        Assert.Equal(0xFFFFFFFFu, BitConverter.ToUInt32(rebuilt, 0xC8 + 0x4C));
        using var again = ZdbArchive.FromBytes(rebuilt);
        Assert.Equal(100, again.GetEntry(1).Data.Length);
    }

    [Theory]
    [InlineData(0x1BEu, 24, 0, 4, 8, 12)]  // u16 UV, RGBA8888, s8 normal, float position (world and props)
    [InlineData(0x1A2u, 20, 0, -1, 4, 8)]  // u16 UV, s8 normal, float position
    [InlineData(0x11Bu, 16, 0, 8, -1, 10)]  // float UV, RGBA4444, s16 position
    public void Vertex_formats_place_each_component_at_its_own_alignment(uint vtype, int stride, int uv, int colour, int normal, int position)
    {
        var fmt = new PspGe.VertexFormat(vtype);
        Assert.Equal((uv, colour, normal, position, stride), (fmt.UvAt, fmt.ColourAt, fmt.NormalAt, fmt.PositionAt, fmt.Stride));
    }

    [Fact]
    public void A_batch_draws_its_strips_from_its_vertices_with_V_flipped_and_PS2_colours()
    {
        // One static object with one batch: a strip of 4 vertices (2 triangles).
        var d = new byte[0x400];
        int obj = 0x10, b = obj + PspGe.FirstBatch, dl = b + PspGe.BatchSize, vtx = dl + 0x20;
        Put(d, obj, PspGe.StaticObjectType);
        Put(d, obj + 0x70, 1);
        Put(d, b + 0x74, 0xCAFEF00D);
        Put(d, b + 0xE0, (uint)(vtx - b));
        Put(d, b + 0xE8, (uint)(dl - b));
        Put(d, b + 0xEC, 1);
        Put(d, b + 0xF4, (4u << 16) | (24u << 8));
        Put(d, dl, 0x120001BE); Put(d, dl + 4, 0x10000000); Put(d, dl + 8, 0x01000000); Put(d, dl + 12, 0x04040004); Put(d, dl + 16, 0x0B000000);
        for (int i = 0; i < 4; i++)
        {
            int v = vtx + 24 * i;
            BitConverter.GetBytes((ushort)(i * 0x2000)).CopyTo(d, v);      // u
            BitConverter.GetBytes((ushort)0x8000).CopyTo(d, v + 2);         // v = 1.0
            Put(d, v + 4, 0xFF7F7F7F);                                       // A = 1.0, grey
            d[v + 9] = 127;                                                   // normal +Y
            PutF(d, v + 12, i); PutF(d, v + 16, 2); PutF(d, v + 20, i % 2);
        }
        var lib = new CaLibrary(d, vectorsAt: 0x200, psp: true);
        var chunks = PspGe.Chunks(lib, obj, Matrix4.Identity(), "x", 0);
        var c = Assert.Single(chunks);
        Assert.Equal(4, c.VertexCount);
        Assert.Equal(new[] { 0, 1, 2, 2, 1, 3 }, c.Triangles);
        Assert.Equal("ca_cafef00d", c.Texture);
        Assert.Equal(0f, c.Uvs[1]);                       // 1 - 1.0
        Assert.Equal(0.25f, c.Uvs[2], 3);
        Assert.Equal(new byte[] { 0x40, 0x40, 0x40, 0x80 }, c.Colors![..4]);
        Assert.Equal(new[] { 3f, 2f, 1f }, c.Positions[9..12]);
        Assert.Equal(1f, c.Normals[1], 2);
    }

    [Fact]
    public void Clut8_textures_unswizzle_16_byte_by_8_row_blocks()
    {
        // A 32×8 CLUT8 texture: two 16×8 blocks, the second stored after the first. Palette entry i = (i, 0, 0, 255).
        const int r = 0x40; // the record (a library's header comes first)
        var d = new byte[r + 0x30 + 256 + 1024];
        Put(d, r + 0x08, 0x12345678); Put(d, r + 0x0C, 0x30); Put(d, r + 0x10, 5);
        Put(d, r + 0x14, (8u << 16) | 32); Put(d, r + 0x18, 32); Put(d, r + 0x1C, 0x30 + 256); Put(d, r + 0x24, 0x02010220);
        for (int block = 0; block < 2; block++)
            for (int row = 0; row < 8; row++)
                for (int x = 0; x < 16; x++)
                    d[r + 0x30 + block * 128 + row * 16 + x] = (byte)(block * 16 + x + row);
        for (int i = 0; i < 256; i++) Put(d, r + 0x30 + 256 + 4 * i, 0xFF000000u | (uint)i);
        var lib = new CaLibrary(d, vectorsAt: 0, psp: true);
        var t = PspGe.DecodeTexture(lib, r)!;
        Assert.Equal((32, 8, 0x12345678u), (t.Width, t.Height, t.Hash));
        int Red(int x, int y) => t.Rgba[(y * 32 + x) * 4];
        Assert.Equal(0, Red(0, 0));
        Assert.Equal(15, Red(15, 0));
        Assert.Equal(16, Red(16, 0));   // the second block, first row
        Assert.Equal(3 + 20, Red(20, 3));
        Assert.False(t.HasAlpha);
    }

    // ------------------------------------------------------------------ retail

    private static string? Disc => Environment.GetEnvironmentVariable("MAPBUILDER_FTB2_DISC") is { Length: > 0 } d
        && Directory.Exists(Path.Combine(d, "PSP_GAME", "USRDIR", "RUN")) ? Path.Combine(d, "PSP_GAME", "USRDIR", "RUN") : null;

    [Fact]
    public void Retail_zdbs_open_and_round_trip()
    {
        if (Disc is not string run) return;
        int count = 0;
        foreach (var f in Directory.GetFiles(run, "*.ZDB", SearchOption.AllDirectories))
        {
            var bytes = File.ReadAllBytes(f);
            using var zdb = ZdbArchive.FromBytes(bytes);
            Assert.True(zdb.ToBytes().AsSpan().SequenceEqual(bytes), f);
            count++;
        }
        Assert.Equal(393, count);
    }

    [Fact]
    public void Retail_fort_draws_its_world_with_every_texture()
    {
        if (Disc is not string run) return;
        var pkg = CaPackage.Open(Path.Combine(run, "FORT_MP"));
        Assert.True(pkg.IsPsp);
        var scene = pkg.MapScene();
        int tris = 0;
        var missing = new HashSet<string>();
        foreach (var p in scene)
            foreach (var c in pkg.ObjectMesh(p.LibraryIndex, p.Object).Chunks)
            {
                tris += c.Triangles.Length / 3;
                if (c.Texture != null && pkg.FindTexture(c.Texture) == null) missing.Add(c.Texture);
            }
        Assert.InRange(tris, 21000, 23000);
        Assert.Empty(missing);
        Assert.Contains(scene, p => p.World == CaPackage.SkyWorld);
        // Every display list in the island belongs to a batch.
        var (_, lib) = pkg.Libraries.First(l => l.Res.Tag == "ISLE");
        int lists = lib.Objects.Sum(o => PspGe.Batches(lib, o).Count);
        Assert.Equal(683, lists);
        var ak = pkg.Models().First(m => m.Name == "ak47");
        Assert.True(pkg.BuildMesh(ak).Chunks.Count > 0);
    }
}
