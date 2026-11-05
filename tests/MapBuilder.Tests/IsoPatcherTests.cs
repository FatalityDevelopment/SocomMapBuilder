using System.Text;
using CodeDesigner.Ps2.FileFormats.Gta;
using CodeDesigner.Ps2.FileFormats.Iso;
using Xunit;

namespace MapBuilder.Tests;

/// <summary>Patching files into a copy of an ISO 9660 image: in place when they fit, moved to the end when they grow.</summary>
public sealed class IsoPatcherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mb-iso-" + Guid.NewGuid().ToString("N")[..8]);

    public IsoPatcherTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static byte[] Record(string name, uint lba, uint size, bool dir)
    {
        byte[] n = name == "\0" || name == "\u0001" ? new[] { (byte)name[0] } : Encoding.ASCII.GetBytes(name);
        int len = 33 + n.Length + (n.Length % 2 == 0 ? 1 : 0);
        var r = new byte[len];
        r[0] = (byte)len;
        Both(r, 2, lba);
        Both(r, 10, size);
        r[25] = (byte)(dir ? 2 : 0);
        r[32] = (byte)n.Length;
        n.CopyTo(r, 33);
        return r;
    }

    private static void Both(byte[] b, int at, uint v)
    {
        BitConverter.TryWriteBytes(b.AsSpan(at, 4), v);
        b[at + 4] = (byte)(v >> 24); b[at + 5] = (byte)(v >> 16); b[at + 6] = (byte)(v >> 8); b[at + 7] = (byte)v;
    }

    /// <summary>Sectors: 16 PVD, 17 terminator, 18 root {DIR, A.BIN (100 B at 20)}, 19 DIR {B.BIN (3000 B at 21-22)}.</summary>
    private string Image()
    {
        var img = new byte[23 * 2048];
        void Put(int sector, int at, byte[] b) => b.CopyTo(img, sector * 2048 + at);
        var pvd = new byte[2048];
        pvd[0] = 1; Encoding.ASCII.GetBytes("CD001").CopyTo(pvd, 1); pvd[6] = 1;
        Both(pvd, 80, 23);
        Record("\0", 18, 2048, true).CopyTo(pvd, 156);
        Put(16, 0, pvd);
        var term = new byte[7]; term[0] = 255; Encoding.ASCII.GetBytes("CD001").CopyTo(term, 1);
        Put(17, 0, term);
        int o = 0;
        foreach (var r in new[] { Record("\0", 18, 2048, true), Record("\u0001", 18, 2048, true), Record("DIR", 19, 2048, true), Record("A.BIN;1", 20, 100, false) })
        { Put(18, o, r); o += r.Length; }
        o = 0;
        foreach (var r in new[] { Record("\0", 19, 2048, true), Record("\u0001", 18, 2048, true), Record("B.BIN;1", 21, 3000, false) })
        { Put(19, o, r); o += r.Length; }
        for (int i = 0; i < 100; i++) img[20 * 2048 + i] = 0xAA;
        for (int i = 0; i < 3000; i++) img[21 * 2048 + i] = 0xBB;
        string path = Path.Combine(_root, "SRC.ISO");
        File.WriteAllBytes(path, img);
        return path;
    }

    private string Bytes(string name, int count, byte value)
    {
        string p = Path.Combine(_root, name);
        File.WriteAllBytes(p, Enumerable.Repeat(value, count).ToArray());
        return p;
    }

    [Fact]
    public void Patch_WritesInPlaceAndMovesGrownFilesToTheEnd()
    {
        string src = Image();
        var entries = IsoPatcher.Entries(src);
        Assert.Equal(new[] { "A.BIN", "DIR/B.BIN" }, entries.Keys.Order());

        string dst = Path.Combine(_root, "out", "EDIT.ISO");
        var result = IsoPatcher.Patch(src, dst, new Dictionary<string, string>
        {
            ["A.BIN"] = Bytes("a", 50, 0x11),          // smaller: stays at sector 20
            ["dir/b.bin"] = Bytes("b", 5000, 0x22),    // grows past its 2 sectors: moves to sector 23
        });
        Assert.Equal(new[] { ("A.BIN", false), ("DIR/B.BIN", true) }, result.Files);

        var files = Iso9660.Files(dst);
        Assert.Equal(20 * 2048, files["A.BIN"].Offset);
        Assert.Equal(Enumerable.Repeat((byte)0x11, 50), files["A.BIN"].Read());
        Assert.Equal(23 * 2048, files["DIR/B.BIN"].Offset);
        Assert.Equal(Enumerable.Repeat((byte)0x22, 5000), files["DIR/B.BIN"].Read());

        var image = File.ReadAllBytes(dst);
        Assert.Equal(26 * 2048, image.Length);
        Assert.Equal(26u, BitConverter.ToUInt32(image, 16 * 2048 + 80));             // volume size, little endian
        Assert.Equal(26, image[16 * 2048 + 87]);                                       // and big endian
        Assert.All(image.AsSpan(20 * 2048 + 50, 2048 - 50).ToArray(), b => Assert.Equal(0, b));   // the old tail is cleared
        Assert.Equal(23 * 2048, File.ReadAllBytes(src).Length);                      // the source is untouched
    }

    /// <summary>Every disc image in MAPBUILDER_ISO_DIR (e.g. F:\): the patcher's listing matches the reader's.</summary>
    [Fact]
    public void Retail_EntriesMatchTheReader()
    {
        string? dir = Environment.GetEnvironmentVariable("MAPBUILDER_ISO_DIR");
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        foreach (string iso in Directory.GetFiles(dir, "*.iso"))
        {
            var entries = IsoPatcher.Entries(iso);
            var files = Iso9660.Files(iso);
            Assert.Equal(files.Count, entries.Count);
            foreach (var (path, span) in files)
                Assert.Equal(span.Offset, (long)entries[path.ToUpperInvariant().TrimEnd('.')].Lba * 2048);   // "PAD." is PAD
            Console.WriteLine($"{Path.GetFileName(iso)}: {entries.Count} files, {entries.Values.Count(e => e.Records.Count > 1)} with Joliet records");
        }
    }

    /// <summary>
    /// End to end on SOCOM II (MAPBUILDER_SOCOM2_ISO, MAPBUILDER_ISO_OUT a folder with room for a disc copy): a map ZDB
    /// taken off the disc, opened, one placement moved, saved into a disc image; the image holds the same ZDB a normal
    /// save writes, at the map's own path.
    /// </summary>
    [Fact]
    public void Retail_Socom2MapSavesIntoItsDiscImage()
    {
        string? iso = Environment.GetEnvironmentVariable("MAPBUILDER_SOCOM2_ISO"), outDir = Environment.GetEnvironmentVariable("MAPBUILDER_ISO_OUT");
        if (string.IsNullOrEmpty(iso) || !File.Exists(iso) || string.IsNullOrEmpty(outDir)) return;
        var files = Iso9660.Files(iso);
        // The maps are RUN/<MAP>.ZDB (M51, MP1 ...): the smallest online one.
        var map = files.Where(f => System.Text.RegularExpressions.Regex.IsMatch(f.Key, @"^RUN/MP\d+\.ZDB$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            .OrderBy(f => f.Value.Length).First();
        string local = Path.Combine(_root, Path.GetFileName(map.Key));
        File.WriteAllBytes(local, map.Value.Read());

        var b = new MapBuilder.Tests.Fixtures.Backend();
        string session = b.Open(local);
        try
        {
            var inst = b.Scene(session).GetProperty("instances").EnumerateArray()
                .First(i => i.GetProperty("geoIndex").ValueKind == System.Text.Json.JsonValueKind.Number && i.GetProperty("kind").GetString() == "reference");
            var m = inst.GetProperty("matrix").EnumerateArray().Select(v => v.GetSingle()).ToArray();
            b.Edit(session, new { op = "moveInstance", geoIndex = inst.GetProperty("geoIndex").GetInt32(), position = new[] { m[12] + 1, m[13], m[14] } });

            string plain = Path.Combine(_root, "plain", Path.GetFileName(local));
            b.Ok(new MapBuilder.Server.CommandMessage { Command = "ASSET_SAVE", AssetSession = session, AssetJson = System.Text.Json.JsonSerializer.Serialize(new { mode = "zdb", outputPath = plain }) });
            string target = Path.Combine(outDir, "SOCOM2_EDIT.iso");
            var saved = b.Ok(new MapBuilder.Server.CommandMessage { Command = "ASSET_SAVE", AssetSession = session,
                AssetJson = System.Text.Json.JsonSerializer.Serialize(new { mode = "zdb", outputPath = target, sourceIso = iso }) });
            Console.WriteLine(saved.ToString());
            Assert.Equal(map.Key.ToUpperInvariant(), saved.GetProperty("patched")[0].GetProperty("discPath").GetString());
            var patched = Iso9660.Files(target);
            Assert.Equal(File.ReadAllBytes(plain), patched[map.Key].Read());
            Assert.Equal(files.Count, patched.Count);
            File.Delete(target);
        }
        finally { b.Close(session); }
    }

    /// <summary>GTA (MAPBUILDER_GTA_DISC an .ISO, MAPBUILDER_ISO_OUT): an edited model in the disc session saves its whole
    /// IMG into a copy of the disc, where it reads back like a plain IMG save.</summary>
    [Fact]
    public void Retail_GtaImgSavesIntoItsDiscImage()
    {
        string? iso = Environment.GetEnvironmentVariable("MAPBUILDER_GTA_DISC"), outDir = Environment.GetEnvironmentVariable("MAPBUILDER_ISO_OUT");
        if (string.IsNullOrEmpty(iso) || !iso.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(outDir)) return;
        var b = new MapBuilder.Tests.Fixtures.Backend();
        string session = b.Open(iso);
        try
        {
            string archive = b.Ok(new MapBuilder.Server.CommandMessage { Command = "ASSET_LIST_MODELS", AssetSession = session }).EnumerateArray()
                .Select(m => m.GetProperty("archive").GetString()!).First(a => a.EndsWith("PLAYER.IMG", StringComparison.OrdinalIgnoreCase));
            b.Edit(session, new { op = "gtaGeometry", archive, model = "vest", geometry = 0, factors = new[] { 1f, 1f, 1f }, offset = new[] { 0.125f, 0f, 0f }, revision = 0 });
            string plain = Path.Combine(_root, "PLAYER.IMG");
            b.Ok(new MapBuilder.Server.CommandMessage { Command = "ASSET_SAVE", AssetSession = session,
                AssetJson = System.Text.Json.JsonSerializer.Serialize(new { mode = "img", archive, outputPath = plain }) });
            string target = Path.Combine(outDir, "GTA_EDIT.iso");
            var saved = b.Ok(new MapBuilder.Server.CommandMessage { Command = "ASSET_SAVE", AssetSession = session,
                AssetJson = System.Text.Json.JsonSerializer.Serialize(new { mode = "img", archive, outputPath = target }) });
            Console.WriteLine(saved.ToString());
            Assert.Equal("MODELS/PLAYER.IMG", saved.GetProperty("patched")[0].GetProperty("discPath").GetString());
            Assert.Equal(File.ReadAllBytes(plain), Iso9660.Files(target)["MODELS/PLAYER.IMG"].Read());
            File.Delete(target);
        }
        finally { b.Close(session); }
    }

    [Fact]
    public void Patch_RefusesTheSourceAndUnknownPaths()
    {
        string src = Image();
        Assert.Throws<ArgumentException>(() => IsoPatcher.Patch(src, src, new Dictionary<string, string>()));
        Assert.Throws<KeyNotFoundException>(() => IsoPatcher.Patch(src, Path.Combine(_root, "x.iso"), new Dictionary<string, string> { ["NOPE.BIN"] = Bytes("n", 1, 1) }));
        Assert.False(File.Exists(Path.Combine(_root, "x.iso.tmp")));
    }
}
