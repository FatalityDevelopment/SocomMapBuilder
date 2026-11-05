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

    [Fact]
    public void Rgba5551_cluts_and_alpha_modes_make_cut_outs()
    {
        // A 16x8 CLUT4 texture whose 16-bit CLUT has the RGBA5551 bit (top byte 0x06): entry 0 clear, entry 1 opaque red.
        const int r = 0x40;
        var d = new byte[r + 0x30 + 64 + 32];
        Put(d, r + 0x0C, 0x30); Put(d, r + 0x10, 4); Put(d, r + 0x14, (8u << 16) | 16); Put(d, r + 0x18, 32);
        Put(d, r + 0x1C, 0x30 + 128); Put(d, r + 0x20, 1); Put(d, r + 0x24, 0x06010101);
        Array.Resize(ref d, r + 0x30 + 128 + 32);
        d[r + 0x30] = 0x10; // texel 0 → entry 0, texel 1 → entry 1
        BitConverter.GetBytes((ushort)0x0000).CopyTo(d, r + 0x30 + 128);
        BitConverter.GetBytes((ushort)0x801F).CopyTo(d, r + 0x30 + 128 + 2);
        var lib = new CaLibrary(d, vectorsAt: 0, psp: true);
        var t = PspGe.DecodeTexture(lib, r)!;
        Assert.Equal(0, t.Rgba[3]);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, t.Rgba[4..8]);
        Assert.Equal(0.5f, lib.TextureState(r)!.AlphaTest);
        Put(d, r + 0x20, 3);
        Assert.True(lib.TextureState(r)!.Blend);
    }

    [Fact]
    public void A_batch_without_back_face_culling_is_drawn_from_both_sides()
    {
        var d = new byte[0x400];
        int obj = 0x10, b = obj + PspGe.FirstBatch, dl = b + PspGe.BatchSize, vtx = dl + 0x20;
        Put(d, obj, PspGe.StaticObjectType); Put(d, obj + 0x70, 1);
        Put(d, b + 0x0C, 0x1D000000); // BCE off
        Put(d, b + 0xE0, (uint)(vtx - b)); Put(d, b + 0xE8, (uint)(dl - b)); Put(d, b + 0xF4, (3u << 16) | (24u << 8));
        Put(d, dl, 0x120001BE); Put(d, dl + 4, 0x01000000); Put(d, dl + 8, 0x04030003); Put(d, dl + 12, 0x0B000000);
        for (int i = 0; i < 3; i++) { PutF(d, vtx + 24 * i + 12, i); PutF(d, vtx + 24 * i + 20, i == 2 ? 1 : 0); }
        var lib = new CaLibrary(d, vectorsAt: 0x200, psp: true);
        Assert.Equal(new[] { 0, 1, 2, 0, 2, 1 }, Assert.Single(PspGe.Chunks(lib, obj, Matrix4.Identity(), "x", 0)).Triangles);
    }

    [Fact]
    public void Fireteam_Bravo_2_actors_have_no_display_key()
    {
        // One 0x1BC-byte actor at 0x10: name, character type +0x24, group +0x30, one start at +0x38.
        var d = new byte[0x10 + 0x1BC];
        Put(d, 0, 0x10); Put(d, 4, 0x10 + 0x1BC);
        System.Text.Encoding.ASCII.GetBytes("lonestar").CopyTo(d, 0x10);
        Put(d, 0x10 + 0x20, 0x11111111); Put(d, 0x10 + 0x24, 0x0490A2A8); Put(d, 0x10 + 0x30, 1); Put(d, 0x10 + 0x34, 1);
        System.Text.Encoding.ASCII.GetBytes("s_bravo2").CopyTo(d, 0x10 + 0x38); Put(d, 0x10 + 0x38 + 28, 3);
        var a = Assert.Single(CaMissionTables.ParseActors(d, psp: true));
        Assert.Equal(("lonestar", (string?)null, 0x0490A2A8u, "character"), (a.Name, a.Display, a.Type, a.Kind));
        Assert.Equal(("s_bravo2", 3), (a.Starts[0].Point, a.Starts[0].Heading));
    }

    [Fact]
    public void Fireteam_Bravo_2_character_types_list_their_model_and_loadout()
    {
        // The record list at +0x24, one 0x4D4-byte record at 0x40, its loadout vector at +0x300 → two hashes at 0x600.
        var d = new byte[0x700];
        Put(d, 0x24, 0x30); Put(d, 0x28, 0x34); Put(d, 0x30, 0x40);
        Put(d, 0x40, 0x65B66632); Put(d, 0x44, 0x69B16EB9);
        Put(d, 0x40 + 0x300, 0x600); Put(d, 0x40 + 0x304, 0x608); Put(d, 0x40 + 0x308, 0x608);
        Put(d, 0x600, 0xD3E46CCE); Put(d, 0x604, 0x7FCF62ED);
        var t = Assert.Single(CaMissionTables.ParsePspCharacterTypes(d));
        Assert.Equal((0x65B66632u, 0x69B16EB9u), (t.Hash, t.Model));
        Assert.Equal(new[] { 0xD3E46CCEu, 0x7FCF62EDu }, t.Weapons);
    }

    [Fact]
    public void Fireteam_Bravo_2_weapons_are_named_by_their_key_and_model()
    {
        // Entries: hash, offset of the strings (UI key at +0x0C), then the model's root hash among the next words.
        var d = new byte[0x400];
        void Key(int at, string key) => System.Text.Encoding.ASCII.GetBytes(key).CopyTo(d, at + 0x0C);
        Key(0x010, "_30660_ASSAULTRIFLE_UI_MSG");
        Key(0x040, "_39194_GRENADEM67_UI_MSG");
        System.Text.Encoding.ASCII.GetBytes("UI_NAME(_LOCT(\"GRENADE: M67\" _39194_GRENADEM67_UI_MSG ))").CopyTo(d, 0x80);
        Put(d, 0x200, 0xD3E46CCE); Put(d, 0x204, 0x010); Put(d, 0x210, 0xAAAA0001);
        Put(d, 0x280, 0xB6A2F3FB); Put(d, 0x284, 0x040); Put(d, 0x290, 0xAAAA0002);
        var models = new Dictionary<uint, string> { [0xAAAA0001] = "m4a1", [0xAAAA0002] = "w887d53fc" };
        var w = CaMissionTables.FindPspWeapons(d, new[] { 0xD3E46CCEu, 0xB6A2F3FBu }, models);
        Assert.Equal(("weapon", "M4A1", (uint?)0xAAAA0001u), (w[0xD3E46CCE].Kind, w[0xD3E46CCE].Display, w[0xD3E46CCE].Model));
        Assert.Equal(("item", "GRENADE: M67"), (w[0xB6A2F3FB].Kind, w[0xB6A2F3FB].Display));
    }

    [Fact]
    public void A_saved_map_zdb_finds_its_own_place_on_the_umd()
    {
        // Two maps hold an ISLAND_F.ZDB; the save's FORT_MP folder picks the right one.
        var entries = new Dictionary<string, CodeDesigner.Ps2.FileFormats.Iso.IsoPatcher.Entry>
        {
            ["PSP_GAME/USRDIR/RUN/FORT_MP/ISLAND_F.ZDB"] = new("PSP_GAME/USRDIR/RUN/FORT_MP/ISLAND_F.ZDB", 1, 1, new()),
            ["PSP_GAME/USRDIR/RUN/FORT_SP/ISLAND_F.ZDB"] = new("PSP_GAME/USRDIR/RUN/FORT_SP/ISLAND_F.ZDB", 2, 1, new()),
        };
        string scratch = Path.Combine(Path.GetTempPath(), "ftb2-scratch");
        Assert.Equal("PSP_GAME/USRDIR/RUN/FORT_MP/ISLAND_F.ZDB", MapBuilder.Server.Services.ZipperAssetService.DiscPathOf(
            Path.Combine(scratch, "FORT_MP", "ISLAND_F.ZDB"), scratch, Path.Combine("K:", "PSP_GAME", "USRDIR", "RUN", "FORT_MP"), entries, null));
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
        Assert.InRange(tris, 27000, 28500); // 21,848 drawn, plus the back faces of unculled batches (the trees)
        Assert.Empty(missing);
        Assert.Contains(scene, p => p.World == CaPackage.SkyWorld);
        // Every display list in the island belongs to a batch.
        var (_, lib) = pkg.Libraries.First(l => l.Res.Tag == "ISLE");
        int lists = lib.Objects.Sum(o => PspGe.Batches(lib, o).Count);
        Assert.Equal(683, lists);
        // Each game mode is a mission whose points are in CA's mission.rdr layout.
        Assert.Contains(pkg.Missions(), m => m.Id == "DEM" && m.Kind == "MP");
        Assert.Contains(pkg.MissionPoints("DEM"), p => p.Name == "seal_tent");
        // Village's squad: Wraith is a clib character carrying the M4A1.
        var village = CaPackage.Open(Path.Combine(run, "VILLAGE_SP2"));
        var types = CaMissionTables.ParsePspCharacterTypes(village.MissionResource("SP2", "CHAR")!.Data);
        var wraith = CaMissionTables.ParseActors(village.MissionResource("SP2", "MSNv")!.Data, psp: true).Single(a => a.Name == "wraith");
        var type = types.Single(t => t.Hash == wraith.Type);
        Assert.NotNull(village.ModelByHash(type.Model, "character"));
        var guns = CaMissionTables.FindPspWeapons(village.GlobalResource("WEAP")!.Data, type.Weapons, village.WeaponModelHashes());
        Assert.Equal("M4A1", guns[type.Weapons[0]].Display);
        var ak = pkg.Models().First(m => m.Name == "ak47");
        Assert.True(pkg.BuildMesh(ak).Chunks.Count > 0);
    }

    [Fact]
    public void Retail_characters_are_skinned_to_their_bones_and_placed_objects_are_props()
    {
        if (Disc is not string run) return;
        var pkg = CaPackage.Open(Path.Combine(run, "VILLAGE_SP2"));
        var character = pkg.Models().First(m => m.Kind == "character");
        var skinned = pkg.BuildMesh(character);
        Assert.NotNull(skinned.Skeleton);
        Assert.Contains(skinned.Skeleton!.Bones, b => b.Name == "leftElbow");
        // Posed in bind, the skinned mesh is where the unskinned batches put it.
        var lib = pkg.Libraries[character.LibraryIndex].Lib;
        var obj = lib.Nodes(character.Offset).SelectMany(n => n.Objects).First();
        var flat = PspGe.Chunks(lib, obj, CodeDesigner.Ps2.FileFormats.Assets.Matrix4.Identity(), "x", 0)[0];
        var first = skinned.Chunks[0];
        for (int i = 0; i < 9; i++) Assert.Equal(flat.Positions[i], first.Positions[i], 2);
        Assert.Contains(first.SkinBones!, b => b > 3);

        // The truck is a placed object, listed and built as a prop.
        var truck = pkg.Models().Single(m => m.Name == "island_village_SP2_obj_ff2c0");
        Assert.Equal("prop", truck.Kind);
        Assert.InRange(pkg.BuildMesh(truck).Chunks.Sum(c => c.Triangles.Length / 3), 700, 750);
    }

    [Fact]
    public void Retail_instance_and_point_moves_write_only_their_bytes()
    {
        if (Disc is not string run) return;
        var pkg = CaPackage.Open(Path.Combine(run, "FORT_MP"));
        var tree = pkg.MapScene().First(p => p.InstanceRecord >= 0 && p.World != CaPackage.SkyWorld);
        var (resource, data) = CaWorldEdits.Move(pkg, tree.Instance, m => { m[12] += 25; return m; });
        var replace = new Dictionary<int, byte[]> { [pkg.Resources.IndexOf(resource)] = data };
        foreach (var (r, b) in CaMissionEdits.Point(pkg, "DEM", "seal_tent", null, new[] { 1413.45f, -14.47f, 2057.93f }))
            replace[pkg.Resources.IndexOf(r)] = b;
        pkg.ReplaceResources(replace);
        Assert.Contains(pkg.MapScene(), p => p.Instance == tree.Instance && p.Matrix[12] == tree.Matrix[12] + 25);

        var copies = pkg.BuildPspCopies(replace.Keys);
        Assert.Equal(new[] { Path.Combine("FORT_MP", "DEM.ZDB"), Path.Combine("FORT_MP", "ISLAND_F.ZDB") }, copies.Keys.Order());
        var original = File.ReadAllBytes(Path.Combine(run, "FORT_MP", "ISLAND_F.ZDB"));
        var copy = copies[Path.Combine("FORT_MP", "ISLAND_F.ZDB")];
        Assert.Equal(original.Length, copy.Length);
        // The ISLE checksum word and the instance's x translation; nothing else.
        Assert.InRange(Enumerable.Range(0, copy.Length).Count(i => copy[i] != original[i]), 2, 8);
    }
}
