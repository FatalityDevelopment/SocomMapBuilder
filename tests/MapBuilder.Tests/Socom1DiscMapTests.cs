using CodeDesigner.Ps2.FileFormats.Zipper.Package;
using CodeDesigner.Ps2.FileFormats.Zipper.ZED;
using Xunit;

namespace MapBuilder.Tests;

/// <summary>
/// SOCOM 1 maps opened from the disc layout (Socom1DiscMap): the map .ZED's assetlibs resolve to folders under RUN\,
/// each library's &lt;NS&gt;_MDL.ZED is read as the GEO and its MODELS.ZAR as the MDL, and saving writes the disc
/// names back. Synthetic always; the retail check runs when MAPBUILDER_SOCOM1_DISC (the disc root, e.g. D:\) is set.
/// </summary>
public sealed class Socom1DiscMapTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mb-socom1-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static byte[] Zed(params (string Name, byte[] Data, uint Children)[] nodes)
    {
        using var zed = ZedContainer.CreateNew();
        foreach (var n in nodes) zed.AddNode(n.Name, n.Data, n.Children);
        return zed.ToBytes();
    }

    /// <summary>A path written with backslashes, in this platform's separators (CI runs on Linux).</summary>
    private static string P(string path) => path.Replace('\\', Path.DirectorySeparatorChar);

    private void Put(string relative, byte[] data)
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
    }

    /// <summary>RUN\MP\MP9 listing its own library, CLIB, the common weapons and one library the disc lacks.</summary>
    private string BuildDisc()
    {
        var four = new byte[4];
        Put(P(@"RUN\MP\MP9\MP9.ZED"), Zed(
            ("MetersPerUnit", four, 0),
            ("assetlibs", Array.Empty<byte>(), 4),
            ("//mp/mp9/clib", four, 0),
            ("//common/assetlib/weapons", four, 0),
            ("//mp/mp9/missing", four, 0),
            ("//mp/mp9/assetlib/mp9", four, 0),
            ("world_tree", Array.Empty<byte>(), 0)));
        Put(P(@"RUN\MP\MP9\CLUTTER.ZAR"), Zed(("Clutter", four, 0)));
        Put(P(@"RUN\MP\MP9\ASSETLIB\MP9\MP9_MDL.ZED"), Zed(("models", Array.Empty<byte>(), 0)));
        Put(P(@"RUN\MP\MP9\ASSETLIB\MP9\MODELS.ZAR"), Zed(("worldmodel", four, 0)));
        Put(P(@"RUN\MP\MP9\ASSETLIB\MP9\MP9_TXR.ZED"), Zed(("textures", Array.Empty<byte>(), 0)));
        Put(P(@"RUN\MP\MP9\CLIB\CLIB_MDL.ZED"), Zed(("models", Array.Empty<byte>(), 0)));
        Put(P(@"RUN\MP\MP9\CLIB\MODELS.ZAR"), Zed(("MESH_seal", four, 0)));
        Put(P(@"RUN\COMMON\ASSETLIB\WEAPONS\WEAP_MDL.ZED"), Zed(("models", Array.Empty<byte>(), 0)));
        Put(P(@"RUN\COMMON\ASSETLIB\WEAPONS\MODELS.ZAR"), Zed(("m4a1", four, 0)));
        return Path.Combine(_root, P(@"RUN\MP\MP9"));
    }

    [Fact]
    public void A_socom1_map_folder_opens_with_its_libraries_under_socom2_names()
    {
        var pkg = ZipperPackage.Open(BuildDisc());

        Assert.True(pkg.IsSocom1DiscMap);
        Assert.Equal("mp9", pkg.MapNamespace);
        Assert.EndsWith(P(@"ASSETLIB\MP9\MP9_MDL.ZED"), pkg.Entry("MP9_GEO.ZED")!.Path);
        Assert.EndsWith(P(@"ASSETLIB\MP9\MODELS.ZAR"), pkg.Entry("MP9_MDL.ZED")!.Path);
        Assert.EndsWith(P(@"CLIB\MODELS.ZAR"), pkg.Entry("CLIB_MDL.ZED")!.Path);
        Assert.EndsWith(P(@"CLIB\CLIB_MDL.ZED"), pkg.Entry("CLIB_GEO.ZED")!.Path);
        Assert.EndsWith(P(@"COMMON\ASSETLIB\WEAPONS\MODELS.ZAR"), pkg.Entry("WEAP_MDL.ZED")!.Path);
        Assert.Equal("geo", pkg.Entry("MP9_GEO.ZED")!.Kind);
        Assert.NotNull(pkg.Entry("CLUTTER.ZAR"));
        Assert.NotNull(pkg.Entry("MP9.ZED"));
        Assert.Null(pkg.Entry("MODELS.ZAR"));
        Assert.Equal(new[] { "//mp/mp9/missing" }, pkg.MissingLibraries);
    }

    [Fact]
    public void The_map_zed_or_a_file_beside_it_opens_the_same_map()
    {
        string map = BuildDisc();
        Assert.True(ZipperPackage.Open(Path.Combine(map, "MP9.ZED")).IsSocom1DiscMap);
        Assert.True(ZipperPackage.Open(Path.Combine(map, "CLUTTER.ZAR")).IsSocom1DiscMap);
        // A library folder on its own is not a map.
        Assert.Null(Socom1DiscMap.FindMapZed(Path.Combine(map, "ASSETLIB", "MP9")));
    }

    [Fact]
    public void A_socom2_loose_folder_is_not_taken_for_a_socom1_map()
    {
        var four = new byte[4];
        Put(P(@"M51\M51.ZED"), Zed(("assetlibs", Array.Empty<byte>(), 1), ("//sp/m51/flib", four, 0)));
        Put(P(@"M51\M51_GEO.ZED"), Zed(("models", Array.Empty<byte>(), 0)));
        var pkg = ZipperPackage.Open(Path.Combine(_root, "M51"));
        Assert.False(pkg.IsSocom1DiscMap);
        Assert.NotNull(pkg.Entry("M51_GEO.ZED"));
    }

    [Fact]
    public void Blank_files_from_a_damaged_disc_copy_are_named_not_misread()
    {
        string map = BuildDisc();
        Put(P(@"RUN\MP\MP9\CLIB\MODELS.ZAR"), new byte[4096]);
        var pkg = ZipperPackage.Open(map);
        Assert.Single(pkg.BlankFiles, p => p.EndsWith(P(@"CLIB\MODELS.ZAR"), StringComparison.OrdinalIgnoreCase));

        // A blank map .ZED in a SOCOM 1 folder layout is reported as damage rather than opened as a loose folder.
        Put(P(@"RUN\MP\MP9\MP9.ZED"), new byte[1696]);
        var ex = Assert.Throws<InvalidDataException>(() => ZipperPackage.Open(map));
        Assert.Contains("damaged", ex.Message);
    }

    [Fact]
    public void Saving_writes_each_file_back_under_its_disc_name_and_folder()
    {
        var pkg = ZipperPackage.Open(BuildDisc());
        byte[] edited = Zed(("models", Array.Empty<byte>(), 0), ("extra", new byte[4], 0));
        pkg.ReplaceEntry("MP9_GEO.ZED", edited);
        pkg.ReplaceEntry("WEAP_MDL.ZED", edited);
        string output = Path.Combine(_root, "out");

        var written = PackageSave.Write(pkg, new[] { "MP9_GEO.ZED", "WEAP_MDL.ZED" }, output);

        string geo = Path.Combine(output, P(@"RUN\MP\MP9\ASSETLIB\MP9\MP9_MDL.ZED"));
        string weapons = Path.Combine(output, P(@"RUN\COMMON\ASSETLIB\WEAPONS\MODELS.ZAR"));
        Assert.Equal(new[] { geo, weapons }, written);
        Assert.Equal(edited, File.ReadAllBytes(geo));
        Assert.Equal(edited, File.ReadAllBytes(weapons));
        Assert.False(File.Exists(Path.Combine(output, "MP9_GEO.ZED")));
    }

    [Fact]
    public void The_retail_socom1_mp1_draws_its_world_props_and_characters()
    {
        string? disc = Environment.GetEnvironmentVariable("MAPBUILDER_SOCOM1_DISC");
        string map = Path.Combine(disc ?? "", "RUN", "MP", "MP1");
        if (string.IsNullOrEmpty(disc) || !Directory.Exists(map)) return;

        var pkg = ZipperPackage.Open(map);

        Assert.True(pkg.IsSocom1DiscMap);
        Assert.Empty(pkg.MissingLibraries);
        var scene = pkg.MapScene();
        Assert.NotNull(scene);
        Assert.Equal("mp1", scene!.Value.Namespace);
        var kinds = pkg.Models().GroupBy(m => m.Kind).ToDictionary(g => g.Key, g => g.Count());
        Assert.True(kinds.GetValueOrDefault("character") > 0);
        Assert.True(kinds.GetValueOrDefault("weapon") > 0);
        Assert.True(kinds.GetValueOrDefault("prop") > 0);
    }
}
