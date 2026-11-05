using System.Text;
using CodeDesigner.Ps2.FileFormats.Gta;
using Xunit;

namespace MapBuilder.Tests;

public sealed class GtaPackageTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gta-package-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private static byte[] Img(string name, byte fill)
    {
        // Nonzero directory slack, payload padding and trailing sector are part of the original IMG.
        byte[] img = Enumerable.Repeat(fill, 3 * 2048).ToArray();
        "VER2"u8.CopyTo(img);
        BitConverter.GetBytes(1).CopyTo(img, 4);
        BitConverter.GetBytes(1).CopyTo(img, 8);
        BitConverter.GetBytes(1).CopyTo(img, 12);
        img.AsSpan(16, 24).Clear();
        Encoding.ASCII.GetBytes(name).CopyTo(img, 16);
        BitConverter.GetBytes(0x10).CopyTo(img, 2048);
        return img;
    }

    [Fact]
    public void Mounted_disc_preserves_archive_identity_and_neighbor_definitions()
    {
        string models = Directory.CreateDirectory(Path.Combine(_dir, "MODELS")).FullName;
        string vehicleImg = Path.Combine(models, "GTA3.IMG"), clothesImg = Path.Combine(models, "PLAYER.IMG");
        byte[] vehicle = Img("coach.dff", 2), clothes = Img("coach.dff", 3);
        File.WriteAllBytes(vehicleImg, vehicle);
        File.WriteAllBytes(clothesImg, clothes);
        string data = Directory.CreateDirectory(Path.Combine(_dir, "DATA")).FullName;
        File.WriteAllText(Path.Combine(data, "VEHICLES.IDE"), "cars\n437, coach, buspaint\nend\n");
        string loose = Path.Combine(Directory.CreateDirectory(Path.Combine(models, "GENERIC")).FullName, "ARROW.DFF");
        File.WriteAllBytes(loose, vehicle[2048..4096]);

        Assert.True(GtaPackage.IsGtaPath(_dir));
        Assert.True(GtaPackage.IsGtaPath(models));
        var package = GtaPackage.Open(_dir);
        var bus = package.FindModel("coach", "GTA3.IMG")!;
        var jacket = package.FindModel("coach", "PLAYER.IMG")!;
        Assert.Equal(3, package.Models.Count);
        Assert.Equal(("vehicle", "buspaint"), (bus.Kind, bus.Txd));
        Assert.Equal(-1, jacket.Id);
        Assert.Equal(vehicle[2048..4096], package.ReadModelData(bus));
        Assert.Equal(clothes[2048..4096], package.ReadModelData(jacket));
        var selected = GtaPackage.Open(vehicleImg);
        Assert.Equal(bus, Assert.Single(selected.Models));
        Assert.Equal("ARROW", GtaPackage.Open(loose).InitialModel);
        Assert.Equal("MODELS/GENERIC", Assert.Single(GtaPackage.Open(loose).Models).Archive);

        byte[] edit = package.ReadModelData(bus);
        edit[32]++;
        package.SetModelData(bus, edit);
        Assert.Throws<InvalidOperationException>(() => package.SaveImgCopy(bus.Archive, vehicleImg));
        Assert.Throws<InvalidOperationException>(() => package.SaveImgCopy(bus.Archive, clothesImg));
        Assert.Throws<InvalidOperationException>(() => package.SaveImgCopy(jacket.Archive, Path.Combine(_dir, "empty.img")));
        string output = Path.Combine(_dir, "copy", "GTA3.IMG");
        package.SaveImgCopy(bus.Archive, output);
        Assert.Equal(vehicle, File.ReadAllBytes(vehicleImg));
        vehicle[2048 + 32]++;
        Assert.Equal(vehicle, File.ReadAllBytes(output));
        Assert.Equal(edit, GtaPackage.Open(output).ReadFile("coach.dff"));
        package.SetModelData(bus, null);
        Assert.NotEqual(edit[32], package.ReadModelData(bus)[32]);
    }

    [Fact]
    public void Img_copy_from_iso_uses_offsets_relative_to_the_embedded_archive()
    {
        byte[] image = Img("wheel.dff", 9);
        byte[] iso = new byte[25 * 2048];
        // ISO9660 directory record, with the fields read by the primary volume descriptor.
        void Record(int at, string name, int sector, int size, bool directory)
        {
            var label = Encoding.ASCII.GetBytes(name);
            iso[at] = (byte)((34 + label.Length) & ~1);
            BitConverter.GetBytes(sector).CopyTo(iso, at + 2);
            BitConverter.GetBytes(size).CopyTo(iso, at + 10);
            iso[at + 25] = directory ? (byte)2 : (byte)0;
            iso[at + 32] = (byte)label.Length;
            label.CopyTo(iso, at + 33);
        }
        int pvd = 16 * 2048;
        iso[pvd] = 1;
        "CD001"u8.CopyTo(iso.AsSpan(pvd + 1));
        Record(pvd + 156, "\0", 20, 2048, true);
        Record(20 * 2048, "MODELS", 21, 2048, true);
        Record(21 * 2048, "GTA3.IMG;1", 22, image.Length, false);
        image.CopyTo(iso, 22 * 2048);
        string disc = Path.Combine(_dir, "game.iso");
        File.WriteAllBytes(disc, iso);
        Assert.True(GtaPackage.IsGtaPath(disc));
        var package = GtaPackage.Open(disc);
        var model = Assert.Single(package.Models);
        byte[] edit = package.ReadModelData(model);
        edit[64] ^= 0x40;
        package.SetModelData(model, edit);
        string copy = Path.Combine(_dir, "copy.img");
        package.SaveImgCopy(model.Archive, copy);
        image[2048 + 64] ^= 0x40;
        Assert.Equal(image, File.ReadAllBytes(copy));
        Assert.Equal(iso, File.ReadAllBytes(disc));
    }

    [Theory]
    [InlineData(1, 0)] // all retail SA entries
    [InlineData(0, 1)] // explicit on-disk size
    [InlineData(1, 1)]
    public void Larger_DFFs_append_past_a_trailing_partial_sector_and_reopen(int streamSize, int diskSize)
    {
        byte[] original = Img("wheel.dff", 0xA7);
        Array.Resize(ref original, original.Length + 11);
        original[^1] = 0xCA;
        BitConverter.GetBytes((ushort)streamSize).CopyTo(original, 12);
        BitConverter.GetBytes((ushort)diskSize).CopyTo(original, 14);
        string input = Path.Combine(_dir, "props.img"), output = Path.Combine(_dir, "edited.img");
        File.WriteAllBytes(input, original);
        var package = GtaPackage.Open(input);
        var model = Assert.Single(package.Models);
        var longer = package.ReadModelData(model).Concat(new byte[] { 10, 20, 30 }).ToArray();
        package.SetModelData(model, longer);
        package.SaveImgCopy(model.Archive, output);
        var result = File.ReadAllBytes(output);
        Assert.Equal(6 * 2048, result.Length);
        Assert.Equal(4u, BitConverter.ToUInt32(result, 8));
        Assert.Equal(streamSize * 2, (int)BitConverter.ToUInt16(result, 12));
        Assert.Equal(diskSize * 2, (int)BitConverter.ToUInt16(result, 14));
        Assert.True(original.AsSpan(16).SequenceEqual(result.AsSpan(16, original.Length - 16)));
        Assert.True(longer.AsSpan().SequenceEqual(result.AsSpan(4 * 2048, longer.Length)));
        Assert.All(result[(4 * 2048 + longer.Length)..], b => Assert.Equal((byte)0, b));
        var reopened = GtaPackage.Open(output);
        Assert.True(reopened.ReadModelData(Assert.Single(reopened.Models)).AsSpan(0, longer.Length).SequenceEqual(longer));
        Assert.Equal(original, File.ReadAllBytes(input));
        package.SetModelData(model, null);
        Assert.Equal(original[2048..4096], package.ReadModelData(model));
    }

    [Fact]
    public void A_shorter_DFF_keeps_its_offset_and_excludes_old_sectors_from_its_length()
    {
        byte[] original = Img("wheel.dff", 0x4F);
        BitConverter.GetBytes(2).CopyTo(original, 12);
        string input = Path.Combine(_dir, "props.img"), output = Path.Combine(_dir, "edited.img");
        File.WriteAllBytes(input, original);
        var package = GtaPackage.Open(input);
        var model = Assert.Single(package.Models);
        byte[] shorter = package.ReadModelData(model)[..91];
        package.SetModelData(model, shorter);
        package.SaveImgCopy(model.Archive, output);
        var result = File.ReadAllBytes(output);
        Assert.Equal(original.Length, result.Length);
        Assert.Equal(1, BitConverter.ToInt32(result, 8));
        Assert.Equal(1, BitConverter.ToInt32(result, 12));
        Assert.True(original.AsSpan(4096).SequenceEqual(result.AsSpan(4096)));
        var payload = GtaPackage.Open(output).ReadFile("wheel.dff")!;
        Assert.Equal(2048, payload.Length);
        Assert.True(shorter.AsSpan().SequenceEqual(payload.AsSpan(0, shorter.Length)));
        Assert.All(payload[shorter.Length..], b => Assert.Equal((byte)0, b));
    }

    [Fact]
    public void Two_entries_sharing_a_sector_do_not_overwrite_each_other_on_save()
    {
        var original = Img("wheel.dff", 0x4C);
        BitConverter.GetBytes(2).CopyTo(original, 4);
        original.AsSpan(8, 32).CopyTo(original.AsSpan(40));
        "t%st.txd\0"u8.CopyTo(original.AsSpan(48));
        string input = Path.Combine(_dir, "props.img"), output = Path.Combine(_dir, "edited.img");
        File.WriteAllBytes(input, original);
        var package = GtaPackage.Open(input);
        var model = Assert.Single(package.Models);
        byte[] edit = package.ReadModelData(model);
        edit[42]++;
        package.SetModelData(model, edit);
        package.SaveImgCopy(model.Archive, output);
        var reopened = GtaPackage.Open(output);
        Assert.Equal(original[2048..4096], reopened.ReadFile("t%st.txd"));
        Assert.Equal(edit, reopened.ReadFile("wheel.dff"));
    }

    [Fact]
    public void Retail_disc_exposes_all_model_sources_and_distinguishes_both_coach_models()
    {
        string? root = Environment.GetEnvironmentVariable("MAPBUILDER_GTA_DISC");
        if (string.IsNullOrEmpty(root)) return;
        var pkg = GtaPackage.Open(root);
        Assert.Equal(15209, pkg.Models.Count);
        Assert.Equal(12829, pkg.Models.Count(m => m.Archive == "GTA3.IMG"));
        Assert.Equal(316, pkg.Models.Count(m => m.Archive == "CUTSCENE.IMG"));
        Assert.Equal(1889, pkg.Models.Count(m => m.Archive == "GTA_INT.IMG"));
        Assert.Equal(155, pkg.Models.Count(m => m.Archive == "PLAYER.IMG"));
        Assert.Equal("vehicle", pkg.FindModel("coach", "GTA3.IMG")!.Kind);
        Assert.Equal("character", pkg.FindModel("coach", "PLAYER.IMG")!.Kind);
        Assert.NotNull(pkg.FindModel("arrow", "MODELS/GENERIC"));
        Assert.NotNull(pkg.FindModel("grass0_1", "MODELS/GRASS"));
        foreach (var (archive, name) in new[] { ("MODELS/GENERIC", "AIR_VLO"), ("PLAYER.IMG", "shirt"), ("PLAYER.IMG", "mohican") })
        {
            var mesh = pkg.BuildMesh(pkg.FindModel(name, archive)!);
            Assert.True(mesh.Chunks.Any(c => c.Triangles.Length >= 3), name);
        }
    }
}
