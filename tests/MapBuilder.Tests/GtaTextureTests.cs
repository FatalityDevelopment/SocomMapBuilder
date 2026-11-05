using CodeDesigner.Ps2.FileFormats.Gta;
using Xunit;

namespace MapBuilder.Tests;

public sealed class GtaTextureTests
{
    [Fact]
    public void Loose_CJ_outfit_piece_uses_adjacent_rasters_only_for_its_verified_body_layout()
    {
        string? root = Environment.GetEnvironmentVariable("MAPBUILDER_GTA_DISC");
        if (string.IsNullOrEmpty(root)) return;
        string img = MapBuilder.Tests.Fixtures.DiscFiles.Gta(root, "MODELS/PLAYER.IMG");
        var player = new GtaImg(new FileSpan(img, 0, new FileInfo(img).Length));
        var folder = Directory.CreateTempSubdirectory("mb-gta-outfit-");
        try
        {
            string path = Path.Combine(folder.FullName, "vest.dff");
            byte[] source = player.Read("vest.dff")!;
            File.WriteAllBytes(path, source);
            File.WriteAllBytes(Path.Combine(folder.FullName, "vest.txd"), player.Read("vest.txd")!);
            var missingSkin = GtaPackage.Open(path);
            Assert.All(missingSkin.BuildMesh(Assert.Single(missingSkin.Models)).Chunks, chunk => Assert.True(string.IsNullOrEmpty(chunk.Texture)));

            File.WriteAllBytes(Path.Combine(folder.FullName, "player_torso.txd"), player.Read("player_torso.txd")!);
            var complete = GtaPackage.Open(path);
            var model = Assert.Single(complete.Models);
            var binding = Assert.Single(complete.BuildMesh(model).Chunks.Select(c => c.Texture).Distinct());
            Assert.Equal("LOOSE|player_torso/torso+LOOSE|vest/vest", binding);
            Assert.NotNull(complete.FindTexture(binding!));
            Assert.Contains(complete.TextureChoices(model), t => t.Dictionary == "player_torso" && t.Name == "torso");

            // The same name and even the same skinned triangles by themselves do not identify an outfit part.
            File.WriteAllBytes(path, source[..(12 + BitConverter.ToInt32(source, 4))]);
            var singleClump = GtaPackage.Open(path);
            Assert.All(singleClump.BuildMesh(Assert.Single(singleClump.Models)).Chunks, chunk => Assert.True(string.IsNullOrEmpty(chunk.Texture)));
        }
        finally { folder.Delete(true); }
    }

    private static byte[] Txd(string name, byte red)
    {
        static byte[] Chunk(uint type, params byte[][] contents)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write(type);
            writer.Write(contents.Sum(c => c.Length));
            writer.Write(0x1803FFFF);
            foreach (var c in contents) writer.Write(c);
            return stream.ToArray();
        }
        var header = new byte[64];
        BitConverter.GetBytes(2).CopyTo(header, 0);
        BitConverter.GetBytes(1).CopyTo(header, 4);
        BitConverter.GetBytes(16).CopyTo(header, 8);
        BitConverter.GetBytes(2UL << 20).CopyTo(header, 16);
        BitConverter.GetBytes(4).CopyTo(header, 48);
        var raster = Chunk(Rw.Struct, Chunk(Rw.Struct, header), Chunk(Rw.Struct, new byte[] { red, 0, red, 128 }));
        var native = Chunk(Rw.TextureNative, Chunk(Rw.Struct), Chunk(Rw.String, System.Text.Encoding.ASCII.GetBytes(name + "\0")), Chunk(Rw.String), raster);
        return Chunk(Rw.TexDictionary, native);
    }

    [Fact]
    public void A_16_bit_raster_honours_the_PS2_alpha_bit()
    {
        var texture = new RwTexDictionary(Txd("test", 31)).Get("test");
        Assert.NotNull(texture);
        Assert.Equal(new byte[] { 255, 0, 0, 0, 255, 0, 0, 255 }, texture.Rgba);
        Assert.True(texture.HasAlpha);
        Assert.False(texture.HasTranslucency);
    }

    [Fact]
    public void Missing_world_materials_use_identical_pixels_across_sources_and_reject_conflicts_or_clothing()
    {
        static void WriteImg(string path, params (string Name, byte[] Data)[] entries)
        {
            var image = new byte[(entries.Length + 1) * 2048];
            "VER2"u8.CopyTo(image);
            BitConverter.GetBytes(entries.Length).CopyTo(image, 4);
            for (int i = 0; i < entries.Length; i++)
            {
                int at = 8 + i * 32;
                BitConverter.GetBytes(i + 1).CopyTo(image, at);
                BitConverter.GetBytes((ushort)1).CopyTo(image, at + 4);
                System.Text.Encoding.ASCII.GetBytes(entries[i].Name).CopyTo(image, at + 8);
                entries[i].Data.CopyTo(image, (i + 1) * 2048);
            }
            File.WriteAllBytes(path, image);
        }
        var root = Directory.CreateTempSubdirectory("gta-textures-");
        try
        {
            string models = root.CreateSubdirectory("MODELS").FullName;
            WriteImg(Path.Combine(models, "GTA3.IMG"));
            WriteImg(Path.Combine(models, "GTA_INT.IMG"), ("one.txd", Txd("shared", 12)), ("only1.txd", Txd("ambiguous", 3)));
            WriteImg(Path.Combine(models, "CUTSCENE.IMG"), ("two.txd", Txd("shared", 12)), ("only2.txd", Txd("ambiguous", 27)));
            WriteImg(Path.Combine(models, "PLAYER.IMG"), ("shirt.txd", Txd("fabric", 5)));
            var pkg = GtaPackage.Open(root.FullName);
            Assert.Equal(pkg.FindTexture("GTA_INT.IMG|one/shared")!.Rgba, pkg.FindTexture("GTA3.IMG|absent/shared")!.Rgba);
            Assert.Null(pkg.FindTexture("GTA3.IMG|absent/ambiguous"));
            Assert.Null(pkg.FindTexture("GTA3.IMG|shirt/fabric"));
            Assert.Null(pkg.FindTexture("PLAYER.IMG|one/shared"));
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public void Retail_cutscene_and_player_materials_use_their_own_dictionaries()
    {
        string? root = Environment.GetEnvironmentVariable("MAPBUILDER_GTA_DISC");
        if (string.IsNullOrEmpty(root)) return;
        var package = GtaPackage.Open(root);
        Assert.NotNull(package.FindTexture("CUTSCENE.IMG|csbravura/bravura92wheel64")); // TXDCUT.IDE parent
        foreach (string name in new[] { "copcarla92wheel64", "copcarla92interior128" })
            Assert.Equal(package.Txd("copcarla", "GTA3.IMG")!.Get(name)!.Rgba,
                package.FindTexture($"CUTSCENE.IMG|cscopcarla/{name}")!.Rgba);
        Assert.NotNull(package.FindTexture("PLAYER.IMG|coach/coach"));
        Assert.Null(package.FindTexture("GTA3.IMG|coach/coach")); // the bus's dictionary contains different pixels
        Assert.NotNull(package.FindTexture("GTA3.IMG|coach/coach92interior128"));

        var vest = package.FindModel("vest", "PLAYER.IMG");
        Assert.NotNull(vest);
        var textures = package.BuildMesh(vest).Chunks.Select(c => c.Texture).Distinct().ToList();
        Assert.NotEmpty(textures);
        Assert.All(textures, t =>
        {
            Assert.StartsWith("PLAYER.IMG|player_torso/torso+PLAYER.IMG|vest/", t);
            Assert.NotNull(package.FindTexture(t!));
        });
    }
}
