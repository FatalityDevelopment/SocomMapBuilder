using System.Text;
using CodeDesigner.Ps2.FileFormats.Swbf;
using Xunit;
using Xunit.Abstractions;

namespace MapBuilder.Tests;

/// <summary>
/// Star Wars: Battlefront (PS2) levels: ucfb chunks, run-length textures, quantised strip models and the package over
/// a loose .LVL. Synthetic always; the retail check runs when MAPBUILDER_SWBF_DISC (the disc image, e.g.
/// F:\Star Wars - Battlefront (USA).iso) is set.
/// </summary>
public sealed class SwbfTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mb-swbf-" + Guid.NewGuid().ToString("N")[..8]);

    public SwbfTests(ITestOutputHelper output)
    {
        _out = output;
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ------------------------------------------------------------------ builders

    private static byte[] Chunk(string tag, params byte[][] body)
    {
        var b = body.SelectMany(x => x).ToArray();
        var d = new List<byte>(Encoding.ASCII.GetBytes(tag));
        d.AddRange(BitConverter.GetBytes(b.Length));
        d.AddRange(b);
        while (d.Count % 4 != 0) d.Add(0);
        return d.ToArray();
    }

    private static byte[] Str(string s) => Encoding.ASCII.GetBytes(s + "\0");
    private static byte[] U32(params int[] v) => v.SelectMany(BitConverter.GetBytes).ToArray();
    private static byte[] F32(params float[] v) => v.SelectMany(BitConverter.GetBytes).ToArray();
    private static byte[] S16(params int[] v) => v.SelectMany(x => BitConverter.GetBytes((short)x)).ToArray();
    private static byte[] U16(params int[] v) => v.SelectMany(x => BitConverter.GetBytes((ushort)x)).ToArray();

    /// <summary>A quad (two triangles from one four-index strip) across the box (-2,0,-1)..(2,2,1), textured "crate".</summary>
    private static byte[] QuadModel(string name) => Chunk("modl",
        Chunk("NAME", Str(name)),
        Chunk("INFO", U32(0, 1, 0), F32(-2, 0, -1, 2, 2, 1), F32(-2, 0, -1, 2, 2, 1), U32(1, 4)),
        Chunk("segm",
            Chunk("INFO", U32(4, 4)),
            Chunk("MTRL", U32(0)),
            Chunk("RTYP", Str("Normal")),
            Chunk("TNAM", U32(0), Str("crate")),
            Chunk("STRP", U16(0x8000, 0x8001, 2, 3)),
            Chunk("POSI", U16(0, 0, 32768, 65535, 0, 32768, 0, 65535, 65535, 65535, 65535, 65535)),
            Chunk("NORM", new byte[] { 0, 127, 0, 0, 127, 0, 0, 127, 0, 0, 127, 0 }),
            Chunk("TEX0", S16(0, 0, 2048, 0, 0, 2048, 2048, 2048))));

    /// <summary>A 4x2 8-bit texture: palette {0: red, 1: half-transparent blue}, pixels 0 0 0 0 1 1 1 0 packed.</summary>
    private static byte[] CrateTexture() => Chunk("tex_",
        Chunk("NAME", Str("crate")),
        Chunk("INFO", U16(4, 2, 8, 0), F32(-1), U16(1)),
        Chunk("pal_", Chunk("INFO", U16(2, 32)), Chunk("BODY", new byte[] { 0x01, 0xFF, 0x00, 0x00, 0xFF, 0x40, 0xFF, 0x00, 0x00 })),
        Chunk("BODY", new byte[] { 0x83, 0x00, 0x82, 0x01, 0x00, 0x00 }));

    private string Level(params byte[][] chunks)
    {
        string path = Path.Combine(_root, "TEST.LVL");
        File.WriteAllBytes(path, Chunk("ucfb", chunks));
        return path;
    }

    // ------------------------------------------------------------------ synthetic

    [Fact]
    public void Unpack_RepeatsAndLiteralsByElement()
    {
        // 0x82 repeat 3 x {1 2}, 0x00 literal 1 x {9 8}
        var d = SwbfTexture.Unpack(new byte[] { 0x82, 1, 2, 0x00, 9, 8 }, 2, 8);
        Assert.Equal(new byte[] { 1, 2, 1, 2, 1, 2, 9, 8 }, d);
    }

    [Fact]
    public void Texture_DecodesPaletteAsAbgr()
    {
        var root = Ucfb.Root(Chunk("ucfb", CrateTexture()));
        var t = SwbfTexture.Decode(root.Children.Single());
        Assert.Equal((4, 2, 8), (t.Width, t.Height, t.Depth));
        Assert.Equal(new byte[] { 0xFF, 0, 0, 0xFF }, t.Rgba[..4]);                // index 0: red
        Assert.Equal(new byte[] { 0, 0, 0xFF, 0x40 }, t.Rgba[16..20]);             // pixel 4: index 1, blue, alpha 0x40
        Assert.Equal(new byte[] { 0xFF, 0, 0, 0xFF }, t.Rgba[28..32]);             // pixel 7: literal 0
        Assert.True(t.HasAlpha);
    }

    [Fact]
    public void Model_StripQuadInBox()
    {
        var root = Ucfb.Root(Chunk("ucfb", QuadModel("crate_box")));
        var mesh = SwbfModel.Build(root.Children.Single(), "TEST.LVL");
        var c = Assert.Single(mesh.Chunks);
        Assert.Equal(4, c.VertexCount);
        Assert.Equal(6, c.Triangles.Length);
        Assert.Equal("crate", c.Texture);
        Assert.Equal(-2f, c.Positions[0], 3);
        Assert.Equal(0f, c.Positions[1], 3);
        Assert.Equal(0f, c.Positions[2], 2);
        Assert.Equal(2f, c.Positions[9], 3);
        Assert.Equal(2f, c.Positions[10], 3);
        Assert.Equal(1f, c.Positions[11], 3);
        Assert.Equal(1f, c.Uvs[1], 3);                                              // v 0 (top) -> 1
        Assert.Equal(1f, c.Uvs[2], 3);
    }

    [Fact]
    public void Strip_SecondStripRestartsWinding()
    {
        var root = Ucfb.Root(Chunk("ucfb", Chunk("STRP", U16(0x8000, 0x8001, 2, 0x8003, 0x8004, 5, 6))));
        var tri = SwbfModel.Triangles(root.Children.Single(), 7);
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 5, 4, 6 }, tri);
    }

    [Fact]
    public void Package_OpensLooseLevelAndFindsTexturesInIt()
    {
        string path = Level(CrateTexture(), QuadModel("imp_inf_crate"), QuadModel("tat_prop_crate"),
            Chunk("lvl_", U32(0x1234, 0), QuadModel("imp_weap_crate")));
        Assert.True(SwbfPackage.IsSwbfPath(path));
        var pkg = SwbfPackage.Open(path);
        Assert.Equal(new[] { "imp_inf_crate", "tat_prop_crate", "imp_weap_crate" }, pkg.Models.Select(m => m.Name));
        Assert.Equal(new[] { "character", "prop", "weapon" }, pkg.Models.Select(m => m.Kind));
        var sub = pkg.FindModel("imp_weap_crate")!;
        Assert.NotEqual(-1, sub.Sub);
        Assert.Single(pkg.BuildMesh(sub).Chunks);
        Assert.Equal(4, pkg.FindTexture("crate", "TEST.LVL")!.Width);
    }

    [Fact]
    public void Fnv1a_MatchesGeometryNameKey() => Assert.Equal(0x47C86B4Au, SwbfWorld.GeometryName);

    // ------------------------------------------------------------------ retail

    private static SwbfPackage? _disc;
    private static SwbfPackage? Disc()
    {
        string? iso = Environment.GetEnvironmentVariable("MAPBUILDER_SWBF_DISC");
        if (string.IsNullOrEmpty(iso) || !File.Exists(iso)) return null;
        lock (typeof(SwbfTests)) return _disc ??= SwbfPackage.Open(iso);
    }

    [Fact]
    public void Retail_MapsResolveTerrainAndInstances()
    {
        if (Disc() is not { } pkg) return;
        _out.WriteLine($"{pkg.Worlds.Count} worlds");
        int placed = 0, resolved = 0;
        var missing = new Dictionary<string, int>();
        foreach (var w in pkg.Worlds)
        {
            var world = pkg.ReadWorld(w);
            int ok = 0;
            foreach (var i in world.Instances)
            {
                placed++;
                if (pkg.ModelForClass(i.Type, w.Level) != null) { ok++; resolved++; }
                else missing[i.Type] = missing.GetValueOrDefault(i.Type) + 1;
            }
            var t = w.Terrain != null ? pkg.Terrain(w.Terrain, w.Archive) : null;
            string ter = t == null ? "no terrain" : $"terrain {t.Name} {t.Size}x{t.GridUnit}m layers [{string.Join(",", t.Layers.Take(3))}] drawn {t.PatchMasks.Count(m => m != 0)}/{t.PatchMasks.Count} replaced {t.Replacements.Count(r => r != null)} light {t.Light != null}";
            Assert.Contains(pkg.Models, m => m.Level == w.Level && m.Kind == "sky");
            _out.WriteLine($"{w.Archive} {w.Name}: sky {w.Sky},{ok}/{world.Instances.Count} inst, {world.Regions.Count} regn; {ter}");
            if (t != null)
            {
                var mesh = pkg.BuildTerrainMesh(w.Archive, SwbfPackage.TerrainPrefix + t.Name)!;
                Assert.NotEmpty(mesh.Chunks);
                // Terrain faces up.
                long up = 0, down = 0;
                foreach (var c in mesh.Chunks)
                    for (int k = 0; k + 2 < c.Triangles.Length; k += 3)
                    {
                        int a = c.Triangles[k] * 3, b = c.Triangles[k + 1] * 3, d = c.Triangles[k + 2] * 3;
                        float ny = (c.Positions[d] - c.Positions[a]) * (c.Positions[b + 2] - c.Positions[a + 2]) - (c.Positions[d + 2] - c.Positions[a + 2]) * (c.Positions[b] - c.Positions[a]);
                        if (ny > 0) up++; else if (ny < 0) down++;
                    }
                Assert.True(up > down * 20, $"{w.Archive} terrain should face up ({up} up, {down} down)");
            }
        }
        _out.WriteLine($"instances resolved {resolved}/{placed}; unresolved classes: {string.Join(", ", missing.OrderByDescending(m => m.Value).Take(25).Select(m => $"{m.Key}x{m.Value}"))}");
        // The rest are sound emitters, vehicle spawns and particle effects (dust, mist, god rays): nothing to draw.
        Assert.True(resolved > placed * 0.8, "Most instances should resolve to a model");
    }

    [Fact]
    public void Retail_EveryModelAndTextureDecodes()
    {
        string? iso = Environment.GetEnvironmentVariable("MAPBUILDER_SWBF_DISC");
        if (string.IsNullOrEmpty(iso) || !File.Exists(iso)) return;
        Assert.True(SwbfPackage.IsSwbfPath(iso));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var pkg = SwbfPackage.Open(iso);
        _out.WriteLine($"indexed {pkg.Levels.Count} levels, {pkg.Models.Count} models, {pkg.Textures.Count} textures in {watch.ElapsedMilliseconds} ms");
        foreach (var g in pkg.Models.GroupBy(m => m.Kind)) _out.WriteLine($"  {g.Key}: {g.Count()}");
        Assert.True(pkg.Models.Count > 1000);

        var failures = new List<string>();
        int missingTextures = 0;
        long facing = 0, against = 0;
        foreach (var m in pkg.Models.OrderBy(m => m.Level))
        {
            try
            {
                var mesh = pkg.BuildMesh(m);
                if (mesh.Chunks.Count == 0 && m.Vertices > 0) failures.Add($"{m.Archive}:{m.Name}: no chunks");
                foreach (var c in mesh.Chunks)
                {
                    if (c.Positions.Any(float.IsNaN)) failures.Add($"{m.Archive}:{m.Name}: NaN");
                    if (c.Texture != null && pkg.FindTextureRef(c.Texture, m.Archive) == null) missingTextures++;
                    // Winding: a triangle's face normal should agree with its vertices' stored normals.
                    for (int t = 0; t + 2 < c.Triangles.Length; t += 3)
                    {
                        int a = c.Triangles[t] * 3, b = c.Triangles[t + 1] * 3, d = c.Triangles[t + 2] * 3;
                        float ux = c.Positions[b] - c.Positions[a], uy = c.Positions[b + 1] - c.Positions[a + 1], uz = c.Positions[b + 2] - c.Positions[a + 2];
                        float vx = c.Positions[d] - c.Positions[a], vy = c.Positions[d + 1] - c.Positions[a + 1], vz = c.Positions[d + 2] - c.Positions[a + 2];
                        float nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
                        float dot = nx * (c.Normals[a] + c.Normals[b] + c.Normals[d]) + ny * (c.Normals[a + 1] + c.Normals[b + 1] + c.Normals[d + 1]) + nz * (c.Normals[a + 2] + c.Normals[b + 2] + c.Normals[d + 2]);
                        if (dot > 0) facing++; else if (dot < 0) against++;
                    }
                }
            }
            catch (Exception e) { failures.Add($"{m.Archive}:{m.Name}: {e.Message}"); }
        }
        foreach (var t in pkg.Textures.OrderBy(t => t.Level))
        {
            try { SwbfTexture.Decode(new Ucfb(pkg.LevelBytes(t.Level), t.Offset, (int)BitConverter.ToUInt32(pkg.LevelBytes(t.Level), t.Offset + 4), "tex_")); }
            catch (Exception e) { failures.Add($"{t.Archive}:{t.Name}: {e.Message}"); }
        }
        _out.WriteLine($"missing texture references: {missingTextures}; triangles facing their normals {facing:N0}, against {against:N0}; decoded all in {watch.ElapsedMilliseconds} ms");
        Assert.True(facing > against * 9, "Most triangles should wind counter-clockwise around their normals");
        foreach (var f in failures.Take(40)) _out.WriteLine(f);
        Assert.Empty(failures);
    }
}
