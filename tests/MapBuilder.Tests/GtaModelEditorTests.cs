using System.Text.Json;
using CodeDesigner.Ps2.FileFormats.Gta;
using MapBuilder.Server;
using MapBuilder.Server.Services.Assets;
using MapBuilder.Tests.Fixtures;
using Xunit;

namespace MapBuilder.Tests;

[Collection(MapPackageCollection.Name)]
public sealed class GtaModelEditorTests
{
    [Fact]
    public void Default_pack_ids_distinguish_retail_models_that_previously_overwrote_each_other()
    {
        static string Pack(string archive, string name, int attempt = 0) =>
            GtaAssetService.DefaultPackName(new(name, "model", "", archive, -1), attempt);
        Assert.NotEqual(Pack("PLAYER.IMG", "coach"), Pack("GTA3.IMG", "coach"));
        Assert.NotEqual(Pack("GTA3.IMG", "lod_nw_bit_13"), Pack("GTA3.IMG", "road_sfw18"));
        Assert.Equal(Pack("PLAYER.IMG", "coach"), Pack("player.img", "COACH"));
        Assert.Matches("^m[a-z0-9]{6}$", Pack("GTA3.IMG", "coach"));
        Assert.NotEqual(Pack("GTA3.IMG", "coach"), Pack("GTA3.IMG", "coach", 1));
    }

    [Fact]
    public void Native_edits_are_undoable_and_the_saved_DFF_reopens_with_the_rendered_changes()
    {
        string? disc = Environment.GetEnvironmentVariable("MAPBUILDER_GTA_DISC");
        if (string.IsNullOrEmpty(disc)) return;
        var b = new Backend();
        const string archive = "GTA3.IMG", model = "infernus";
        string session = b.Open(disc);
        string folder = Directory.CreateTempSubdirectory("mb-gta-edit-").FullName;
        JsonElement Call(string command, object? args = null) => b.Ok(new CommandMessage { Command = command,
            AssetSession = session, AssetName = model, AssetArchive = archive, AssetJson = JsonSerializer.Serialize(args) });
        try
        {
            var info = Call("ASSET_DESCRIBE_MODEL").GetProperty("gta");
            var frame = info.GetProperty("frames").EnumerateArray().First(f => f.GetProperty("name").GetString() == "wheel_rf_dummy");
            var position = frame.GetProperty("position").EnumerateArray().Select(x => x.GetSingle()).ToArray();
            position[0] += 0.25f;
            var op = new { op = "gtaFrame", model, archive, frame = frame.GetProperty("index").GetInt32(), position, revision = 0 };
            b.Edit(session, op);
            Assert.Contains("changed", b.EditFails(session, op));
            b.Undo(session);
            Assert.Empty(b.Status(session).GetProperty("dirty").EnumerateArray());
            b.Redo(session);
            var mat = info.GetProperty("materials")[0];
            var color = new[] { 10, 70, 245, 255 };
            int geometry = mat.GetProperty("geometry").GetInt32(), material = mat.GetProperty("index").GetInt32();
            b.Edit(session, new { op = "gtaMaterial", model, archive, geometry, material, color, revision = 3 });
            b.Edit(session, new { op = "gtaGeometry", model, archive, geometry, factors = new[] { 1f, 1f, 1.05f }, offset = new[] { .03f, 0f, 0f }, revision = 4 });
            string path = Path.Combine(folder, "infernus.dff");
            Call("ASSET_SAVE", new { mode = "dff", outputPath = path, archive, model });
            var saved = GtaDffEdits.Describe(File.ReadAllBytes(path));
            Assert.Equal(position, saved.Frames[op.frame].Position);
            Assert.Equal(color, saved.Materials.First(m => m.Geometry == geometry && m.Index == material).Color);
            var reopened = b.Open(path);
            try
            {
                var mesh = b.Ok(new CommandMessage { Command = "ASSET_MODEL_MESH", AssetSession = reopened, AssetName = model, AssetJson = """{"arrays":false}""" });
                Assert.NotEmpty(mesh.GetProperty("chunks").EnumerateArray());
            }
            finally { b.Close(reopened); }
            for (int i = 0; i < 3; i++) b.Undo(session);
            Assert.Empty(b.Status(session).GetProperty("dirty").EnumerateArray());
        }
        finally { b.Close(session); Directory.Delete(folder, true); }
    }

    [Fact]
    public void Saving_an_edited_retail_IMG_preserves_every_byte_outside_the_chosen_DFF()
    {
        string? disc = Environment.GetEnvironmentVariable("MAPBUILDER_GTA_DISC");
        if (string.IsNullOrEmpty(disc)) return;
        string input = Path.Combine(disc, "MODELS", "PLAYER.IMG");
        var img = new GtaImg(new FileSpan(input, 0, new FileInfo(input).Length));
        var entry = img.Find("vest.dff")!;
        var b = new Backend();
        string session = b.Open(input);
        var folder = Directory.CreateTempSubdirectory("mb-gta-img-");
        try
        {
            b.Edit(session, new { op = "gtaGeometry", archive = "PLAYER.IMG", model = "vest", geometry = 0,
                factors = new[] { 1f, 1f, 1f }, offset = new[] { 0.125f, 0f, 0f }, revision = 0 });
            const string copyName = "closet+copy.img";
            string output = Path.Combine(folder.FullName, copyName);
            b.Ok(new CommandMessage { Command = "ASSET_SAVE", AssetSession = session,
                AssetJson = JsonSerializer.Serialize(new { mode = "img", archive = "PLAYER.IMG", outputPath = output }) });
            var original = File.ReadAllBytes(input);
            var saved = File.ReadAllBytes(output);
            Assert.Equal(original.Length, saved.Length);
            int at = checked((int)entry.Offset), end = checked((int)(entry.Offset + entry.Size));
            Assert.True(original.AsSpan(0, at).SequenceEqual(saved.AsSpan(0, at)));
            Assert.True(original.AsSpan(end).SequenceEqual(saved.AsSpan(end)));
            var copy = GtaPackage.Open(output);
            var vest = copy.FindModel("vest", copyName)!;
            var cj = copy.FindModel(GtaPackage.CjModel, copyName);
            Assert.NotNull(cj);
            Assert.False(copy.IsEditable(cj));
            Assert.Equal(155, copy.Models.Count);
            var raw = RwClump.Parse(img.Read(entry)).Geometries[0].Meshes[0].Batches[0];
            var edited = RwClump.Parse(copy.ReadModelData(vest)).Geometries[0].Meshes[0].Batches[0];
            Assert.Equal(raw.Positions[0] + .125f, edited.Positions[0], 5);
            var vestTexture = Assert.Single(copy.BuildMesh(vest).Chunks.Select(c => c.Texture).Distinct());
            Assert.Equal($"{copyName}|player_torso/torso+{copyName}|vest/vest", vestTexture);
            Assert.NotNull(copy.FindTexture(vestTexture!));
            Assert.Contains(copy.TextureChoices(vest), t => t.Dictionary == "player_torso" && t.Name == "torso");
            Assert.All(copy.BuildMesh(cj).Chunks, chunk => Assert.NotNull(copy.FindTexture(chunk.Texture!)));
        }
        finally { b.Close(session); folder.Delete(true); }
    }

    [Fact]
    public void Replacing_a_retail_material_with_a_longer_dictionary_name_survives_both_copy_formats()
    {
        string? disc = Environment.GetEnvironmentVariable("MAPBUILDER_GTA_DISC");
        if (string.IsNullOrEmpty(disc)) return;
        const string archive = "CUTSCENE.IMG", model = "csbravura";
        string input = Path.Combine(disc, "MODELS", archive);
        var b = new Backend();
        string session = b.Open(input);
        var folder = Directory.CreateTempSubdirectory("mb-gta-material-");
        try
        {
            var description = b.Ok(new CommandMessage { Command = "ASSET_DESCRIBE_MODEL", AssetSession = session, AssetArchive = archive, AssetName = model });
            var info = description.GetProperty("gta");
            var material = info.GetProperty("materials").EnumerateArray().Where(m => m.GetProperty("texture").ValueKind == JsonValueKind.String)
                .MinBy(m => m.GetProperty("texture").GetString()!.Length);
            string original = material.GetProperty("texture").GetString()!;
            string texture = info.GetProperty("textureChoices").EnumerateArray().Select(m => m.GetProperty("name").GetString()!).MaxBy(n => n.Length)!;
            Assert.True(texture.Length > original.Length + 3);
            int geometry = material.GetProperty("geometry").GetInt32(), index = material.GetProperty("index").GetInt32();
            b.Edit(session, new { op = "gtaMaterial", archive, model, geometry, material = index, texture, revision = 0 });
            b.Undo(session);
            Assert.Empty(b.Status(session).GetProperty("dirty").EnumerateArray());
            b.Redo(session);
            byte[] SaveAndRead(string mode, string file)
            {
                string path = Path.Combine(folder.FullName, file);
                b.Ok(new CommandMessage { Command = "ASSET_SAVE", AssetSession = session,
                    AssetJson = JsonSerializer.Serialize(new { mode, model, archive, outputPath = path }) });
                var package = GtaPackage.Open(path);
                var m = package.FindModel(model)!;
                Assert.NotEmpty(package.BuildMesh(m).Chunks);
                return package.ReadModelData(m);
            }
            byte[] dff = SaveAndRead("dff", model + ".dff");
            byte[] img = SaveAndRead("img", archive);
            Assert.True(img.AsSpan(0, dff.Length).SequenceEqual(dff));
            Assert.Equal(texture, GtaDffEdits.Describe(img).Materials.Single(m => m.Geometry == geometry && m.Index == index).Texture);
        }
        finally { b.Close(session); folder.Delete(true); }
    }

    [Fact]
    public void Retail_GTA_models_convert_to_reopenable_SOCOM_II_packs()
    {
        string? disc = Environment.GetEnvironmentVariable("MAPBUILDER_GTA_DISC");
        string? run = Environment.GetEnvironmentVariable("MAPBUILDER_SOCOM2_RUN");
        if (string.IsNullOrEmpty(disc) || string.IsNullOrEmpty(run)) return;
        var b = new Backend();
        string session = b.Open(disc);
        string folder = Directory.CreateTempSubdirectory("mb-gta-pack-").FullName;
        try
        {
            var models = b.Ok(new CommandMessage { Command = "ASSET_LIST_MODELS", AssetSession = session }).EnumerateArray().ToList();
            string interior = models.First(m => m.GetProperty("archive").GetString() == "GTA_INT.IMG" && m.GetProperty("kind").GetString() == "prop").GetProperty("name").GetString()!;
            string occupied = GtaAssetService.DefaultPackName(new("ak47", "weapon", "", "GTA3.IMG", -1));
            string priorPack = Path.Combine(Directory.CreateDirectory(Path.Combine(folder, occupied)).FullName, occupied.ToUpperInvariant() + ".CDB");
            File.WriteAllBytes(priorPack, "prior export"u8.ToArray());
            foreach (var (archive, name) in new[] { ("PLAYER.IMG", "cj"), ("GTA3.IMG", "ak47"), ("GTA3.IMG", "infernus"),
                ("CUTSCENE.IMG", "csbravura"), ("GTA_INT.IMG", interior) })
            {
                var converted = b.Ok(new CommandMessage { Command = "ASSET_EXPORT", AssetSession = session, AssetArchive = archive, AssetName = name,
                    AssetJson = JsonSerializer.Serialize(new { outputDir = folder, socom2Run = run, clips = name == "cj" ? new[] { "ped.ifp/idle_stance" } : Array.Empty<string>() }) });
                Assert.True(converted.GetProperty("triangles").GetInt32() > 0, name);
                if (name == "ak47")
                {
                    Assert.NotEqual(occupied, converted.GetProperty("pack").GetString());
                    Assert.Equal("prior export"u8.ToArray(), File.ReadAllBytes(priorPack));
                }
                if (name == "cj") Assert.NotEmpty(converted.GetProperty("clips").EnumerateArray());
                var opened = b.Open(converted.GetProperty("path").GetString()!);
                try
                {
                    var target = converted.GetProperty("open");
                    var mesh = b.Ok(new CommandMessage { Command = "ASSET_MODEL_MESH", AssetSession = opened, AssetArchive = target.GetProperty("archive").GetString(),
                        AssetName = target.GetProperty("name").GetString(), AssetJson = """{"arrays":false}""" });
                    Assert.True(mesh.GetProperty("chunks").EnumerateArray().Sum(c => c.GetProperty("triangleCount").GetInt32()) > 0, name);
                }
                finally { b.Close(opened); }
            }
        }
        finally { b.Close(session); Directory.Delete(folder, true); }
    }
}
