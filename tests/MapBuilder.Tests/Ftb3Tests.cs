using System.IO.Compression;
using CodeDesigner.Ps2.FileFormats.Ftb3;
using Xunit;

namespace MapBuilder.Tests;

/// <summary>
/// SOCOM: Fireteam Bravo 3 (PSP): .ssg files (SsgArchive), mesh pieces, textures and levels (Ftb3Package). Synthetic
/// always; the retail checks run when MAPBUILDER_FTB3_DISC (the UMD root, e.g. K:\) is set.
/// </summary>
public sealed class Ftb3Tests
{
    private static void Put(byte[] d, int at, uint v) => BitConverter.GetBytes(v).CopyTo(d, at);
    private static void PutF(byte[] d, int at, float v) => BitConverter.GetBytes(v).CopyTo(d, at);

    /// <summary>An .ssg file: entries (name, type, payload), the string table, the data (zlib when packed).</summary>
    private static byte[] Ssg(bool packed, params (string Name, byte Type, byte[] Data)[] entries)
    {
        var names = new List<byte>();
        var nameAt = new Dictionary<string, int>();
        foreach (var (n, _, _) in entries)
            if (!nameAt.ContainsKey(n)) { nameAt[n] = names.Count; names.AddRange(System.Text.Encoding.ASCII.GetBytes(n)); names.Add(0); }
        var data = new List<byte>();
        var table = new byte[entries.Length * 16];
        for (int i = 0; i < entries.Length; i++)
        {
            Put(table, 16 * i, (uint)nameAt[entries[i].Name]);
            Put(table, 16 * i + 4, (uint)entries[i].Data.Length);
            Put(table, 16 * i + 8, (uint)data.Count);
            table[16 * i + 12] = entries[i].Type; table[16 * i + 13] = 1;
            data.AddRange(entries[i].Data);
            while (data.Count % 16 != 0) data.Add(0);
        }
        byte[] body = data.ToArray();
        if (packed)
        {
            var ms = new MemoryStream();
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(body);
            body = ms.ToArray();
        }
        var head = new byte[0x20];
        Put(head, 0, 6); Put(head, 4, packed ? 1u : 0); Put(head, 8, (uint)table.Length); Put(head, 0xC, (uint)names.Count);
        Put(head, 0x10, (uint)data.Count); Put(head, 0x18, (uint)body.Length);
        return head.Concat(table).Concat(names).Concat(body).ToArray();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Ssg_entries_name_their_payloads_packed_or_not(bool packed)
    {
        var bytes = Ssg(packed, ("Grass_TallGrass_Tall", SsgArchive.Instance, new byte[] { 1, 2, 3 }), ("Wood_Log_01", SsgArchive.Texture, new byte[] { 9, 8 }));
        var a = SsgArchive.FromBytes(bytes);
        Assert.Equal(2, a.Entries.Count);
        Assert.Equal(("Grass_TallGrass_Tall", SsgArchive.Instance, 3), (a.Entries[0].Name, a.Entries[0].Type, a.Entries[0].Size));
        Assert.Equal(new byte[] { 9, 8 }, a.Data[a.Entries[1].Offset..(a.Entries[1].Offset + 2)]);
    }

    /// <summary>A mesh piece: texture hash, VTYPE 0x132 (u16 UV, RGB565, s8 normal, s16 position), UV scale 16 and
    /// offset -16, one strip of four vertices (a unit square at y = 1, in 1/128ths).</summary>
    private static byte[] Piece(uint texture)
    {
        var d = new byte[0x70 + 0x20 + 4 * 16];
        Put(d, 0x00, texture); Put(d, 0x4C, 0x12000132); Put(d, 0x50, 1); PutF(d, 0x58, 16); PutF(d, 0x5C, -16); Put(d, 0x68, 4);
        Put(d, 0x70 + 0x18, 4 * 16); Put(d, 0x70 + 0x1C, 0x04040004);
        short[,] xyz = { { 0, 128, 0 }, { 0, 128, 128 }, { 128, 128, 0 }, { 128, 128, 128 } };
        for (int i = 0; i < 4; i++)
        {
            int v = 0x90 + 16 * i;
            BitConverter.GetBytes((ushort)(0x8000 + 0x800 * (i >> 1))).CopyTo(d, v);   // u: 0x8000 or 0x8800, so 0 or 1 after the UV scale
            BitConverter.GetBytes((ushort)(0x8000 + 0x800 * (i & 1))).CopyTo(d, v + 2);
            BitConverter.GetBytes((ushort)0xFFFF).CopyTo(d, v + 4);                     // white
            d[v + 7] = 127;                                                              // normal +Y
            for (int k = 0; k < 3; k++) BitConverter.GetBytes(xyz[i, k]).CopyTo(d, v + 10 + 2 * k);
        }
        return d;
    }

    [Fact]
    public void A_mesh_piece_draws_its_strip_at_its_scale_with_its_uv_scale_and_offset()
    {
        var a = SsgArchive.FromBytes(Ssg(false, ("box", SsgArchive.MeshPiece, Piece(0xCAFEF00D))));
        var world = new[] { 256f, 0, 0, 0, 0, 256f, 0, 0, 0, 0, 256f, 0, 0, 0, 0, 1 };
        var c = Ftb3Package.Piece(a, a.Entries[0], world, 0, "tex")!;
        Assert.Equal(4, c.VertexCount);
        Assert.Equal(new[] { 0, 1, 2, 2, 1, 3 }, c.Triangles);
        Assert.Equal(1f, c.Positions[1], 2);          // 128/128 (s16 / 32767 × 256)
        Assert.Equal(1f, c.Positions[11], 2);
        Assert.Equal(0f, c.Uvs[0], 3);                // 0x8000 × 16 − 16 = 0, V flipped: 1 − 0
        Assert.Equal(1f, c.Uvs[1], 3);
        Assert.Equal(1f, c.Uvs[4], 3);                // 0x8800 → 1.0
        Assert.Equal(1f, c.Normals[1], 2);
    }

    [Fact]
    public void Textures_hold_their_clut_before_their_swizzled_pixels()
    {
        // A 32×8 CLUT4 texture: 16 entries (entry i is red 16·i), then 128 bytes of texels (one 16-byte × 8-row block).
        var d = new byte[0x20 + 0x40 + 128];
        Put(d, 0, 0x12345678); Put(d, 4, (8u << 16) | 32); Put(d, 8, 1); Put(d, 0xC, 0x00100001); Put(d, 0x10, 0x04000106);
        Put(d, 0x14, 0x40 + 128); Put(d, 0x18, 128);
        for (int i = 0; i < 16; i++) Put(d, 0x20 + 4 * i, 0xFF000000u | (uint)(i * 16));
        for (int row = 0; row < 8; row++)
            for (int x = 0; x < 16; x++) d[0x60 + row * 16 + x] = (byte)((x & 15) | ((x + 1) & 15) << 4);
        var a = SsgArchive.FromBytes(Ssg(false, ("Wood_Log_01", SsgArchive.Texture, d)));
        var t = Ftb3Package.DecodeTexture(a, a.Entries[0])!;
        Assert.Equal((32, 8, 4, 0x12345678u, false), (t.Width, t.Height, t.Depth, t.Hash, t.HasAlpha));
        Assert.Equal(0, t.Rgba[0]);               // texel 0: entry 0
        Assert.Equal(16, t.Rgba[4]);              // texel 1: entry 1
        Assert.Equal(255, t.Rgba[3]);
    }

    [Fact]
    public void A_level_places_its_props_and_draws_its_world_where_it_is()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ftb3-test-" + Guid.NewGuid().ToString("N")[..6], "Tiny_multi");
        Directory.CreateDirectory(dir);
        try
        {
            var model = new byte[0x70]; Put(model, 4, 1); PutF(model, 0x4C, 256);
            File.WriteAllBytes(Path.Combine(dir, "Tiny_multi.ssg"), Ssg(true,
                ("CrateCrate", SsgArchive.MeshPiece, Piece(0)), ("CrateCrate", SsgArchive.Model, model),
                ("[0]Tiny_terrain", SsgArchive.MeshPiece, Piece(0)), ("[0]Tiny_terrain", SsgArchive.Model, model)));
            var inst = new byte[0x70];
            for (int i = 0; i < 4; i++) PutF(inst, 20 * i, 1);
            PutF(inst, 0x30, 5); PutF(inst, 0x38, -3); Put(inst, 0x40, 1); Put(inst, 0x48, 0xFFFFFFFF);
            System.Text.Encoding.ASCII.GetBytes("CrateCrate").CopyTo(inst, 0x50);
            var marker = (byte[])inst.Clone(); Array.Clear(marker, 0x50, 0x20);
            System.Text.Encoding.ASCII.GetBytes("AlphaSealControl").CopyTo(marker, 0x50);
            File.WriteAllBytes(Path.Combine(dir, "Tiny_multi_inst.ssg"), Ssg(false, ("1|Crate", SsgArchive.Instance, inst), ("AlphaSealControl", SsgArchive.Instance, marker)));

            Assert.True(Ftb3Package.IsFtb3Path(Path.GetDirectoryName(dir)!));
            var pkg = Ftb3Package.Open(Path.Combine(dir, "Tiny_multi.ssg"));
            Assert.Equal("Tiny_multi", Assert.Single(pkg.Levels.Keys));
            Assert.Equal("MP", Ftb3Package.LevelKind("Tiny_multi"));
            var models = pkg.Models("Tiny_multi");
            Assert.Equal(new[] { ("CrateCrate", "prop"), ("[0]Tiny_terrain", "world") }, models.Select(m => (m.Name, m.Kind)));
            var placed = pkg.Instances("Tiny_multi");
            Assert.Equal(("CrateCrate", 5f, -3f), (placed[0].Model, placed[0].Matrix[12], placed[0].Matrix[14]));
            Assert.Null(placed[1].Model);
            Assert.Equal(2, pkg.BuildMesh(models[0]).Chunks[0].Triangles.Length / 3);
        }
        finally { Directory.Delete(Path.GetDirectoryName(dir)!, true); }
    }

    // ------------------------------------------------------------------ retail

    private static string? Disc => Environment.GetEnvironmentVariable("MAPBUILDER_FTB3_DISC") is { Length: > 0 } d
        && Directory.Exists(Path.Combine(d, "PSP_GAME", "USRDIR", "levels")) ? d : null;

    [Fact]
    public void Retail_levels_draw_with_their_textures()
    {
        if (Disc is not string disc) return;
        var pkg = Ftb3Package.Open(disc);
        Assert.Equal(26, pkg.Levels.Count);
        var models = pkg.Models("Valley_multi");
        Assert.Equal(107, models.Count);
        Assert.Equal(493, pkg.Instances("Valley_multi").Count(i => i.Model != null));
        int tris = 0;
        foreach (var m in models.Where(m => m.Kind != "lod"))
            foreach (var c in pkg.BuildMesh(m).Chunks)
            {
                tris += c.Triangles.Length / 3;
                if (c.Texture != "ftb3_00000000") Assert.NotNull(pkg.FindTexture("Valley_multi", c.Texture!));
            }
        Assert.InRange(tris, 55000, 56500);
        // A campaign level keeps most of its textures in its own .ssg.
        var base_ = pkg.Models("SovietBase_mis");
        int missing = base_.SelectMany(m => pkg.BuildMesh(m).Chunks).Select(c => c.Texture!).Distinct()
            .Count(t => t != "ftb3_00000000" && pkg.FindTexture("SovietBase_mis", t) == null);
        Assert.InRange(missing, 0, 1);
    }
}
