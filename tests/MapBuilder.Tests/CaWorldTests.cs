using CodeDesigner.Ps2.FileFormats.Zipper.Ca;
using Xunit;

namespace MapBuilder.Tests;

/// <summary>
/// SOCOM 3 / Combined Assault worlds (CaLibrary, CaPackage.MapScene): an island's scatter instances (0x70-byte records
/// placing its prop models, as a matrix or a quaternion + position + scale) and the GS state that decides how a
/// surface draws (ALPHA_1 blend, TEST_1 alpha test, CLAMP_1). Synthetic always; the retail check runs when
/// MAPBUILDER_CA_DISC (the disc root, e.g. G:\) is set.
/// </summary>
public sealed class CaWorldTests
{
    private const uint MinusOne = 0xBF800000;

    private static void Put(byte[] d, int at, uint v) => BitConverter.GetBytes(v).CopyTo(d, at);
    private static void PutF(byte[] d, int at, float v) => BitConverter.GetBytes(v).CopyTo(d, at);
    private static void Vec(byte[] d, int at, int begin, int end) { Put(d, at, (uint)begin); Put(d, at + 4, (uint)end); Put(d, at + 8, (uint)end); }

    /// <summary>A version-1 library (vectors at +0x24) with a world model at 0x300 and a prop model at 0x400.</summary>
    private static byte[] Library()
    {
        var d = new byte[0x1000];
        Put(d, 0, 1);
        Put(d, 0x200, 0x300);
        Put(d, 0x204, 0x400);
        Vec(d, 0x24, 0x200, 0x208);
        return d;
    }

    private static void Record(byte[] d, int r, uint flags, int model, uint sentinel = MinusOne)
    {
        Put(d, r + 0x4C, flags);
        Put(d, r + 0x60, (uint)model);
        Put(d, r + 0x68, sentinel);
        Put(d, r + 0x6C, sentinel);
    }

    [Fact]
    public void Scatter_instances_decode_quaternion_and_matrix_records_once_each()
    {
        var d = Library();
        // Quaternion form (flag 0x02): 90° about Y, at (10, 20, 30), scale 2.
        int q = 0x500;
        Record(d, q, 0x8083, 0x400);
        float h = MathF.Sqrt(0.5f);
        PutF(d, q + 4, h); PutF(d, q + 12, h);
        PutF(d, q + 0x1C, 1);
        PutF(d, q + 0x20, 10); PutF(d, q + 0x24, 20); PutF(d, q + 0x28, 30); PutF(d, q + 0x2C, 2);
        // Matrix form: identity at (1, 2, 3).
        int m = q + CaLibrary.InstanceSize;
        Record(d, m, 0x8001, 0x400);
        PutF(d, m, 1); PutF(d, m + 0x14, 1); PutF(d, m + 0x28, 1);
        PutF(d, m + 0x30, 1); PutF(d, m + 0x34, 2); PutF(d, m + 0x38, 3); PutF(d, m + 0x3C, 1);
        // The island's vector, and a grid cell's sub-range of it: each record is taken once.
        Vec(d, 0x100, q, m + CaLibrary.InstanceSize);
        Vec(d, 0x110, m, m + CaLibrary.InstanceSize);
        // Not instances: a record whose sentinels are not -1.0.
        Record(d, 0x700, 0x8001, 0x400, sentinel: 0);
        Vec(d, 0x120, 0x700, 0x700 + CaLibrary.InstanceSize);

        var list = new CaLibrary(d).Instances();

        Assert.Equal(new[] { q, m }, list.Select(i => i.Offset));
        Assert.All(list, i => Assert.Equal(0x400, i.Model));
        var a = list[0].Matrix;
        // X turns onto -Z, Z onto +X, each scaled by 2; the translation is the position.
        Assert.Equal(new[] { 0f, 0f, -2f }, a[..3].Select(v => MathF.Round(v, 4)));
        Assert.Equal(new[] { 0f, 2f, 0f }, a[4..7].Select(v => MathF.Round(v, 4)));
        Assert.Equal(new[] { 2f, 0f, 0f }, a[8..11].Select(v => MathF.Round(v, 4)));
        Assert.Equal(new[] { 10f, 20f, 30f, 1f }, a[12..16]);
        Assert.Equal(new[] { 1f, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 1, 2, 3, 1 }, list[1].Matrix);
    }

    [Fact]
    public void Gs_state_reads_blend_alpha_test_and_clamp()
    {
        Assert.True(CaLibrary.IsBlend(0x44));          // (Cs - Cd) As + Cd: terrain layers, water
        Assert.True(CaLibrary.IsBlend(0x48));          // Cs As + Cd: additive
        Assert.False(CaLibrary.IsBlend(0x4000000000)); // Cs: foliage cut-outs
        Assert.Null(CaLibrary.AlphaTest(0x53000));     // test off
        Assert.Equal(0.5f, CaLibrary.AlphaTest(0x5040B)); // GEQUAL 0x40

        // A texture record whose GIF packet (+0x14) sets ALPHA_1, TEST_1 and CLAMP_1.
        var d = Library();
        int rec = 0x600, packet = 0x800;
        Put(d, rec + 0x14, (uint)packet);
        BitConverter.GetBytes(3UL | (1UL << 60)).CopyTo(d, packet);
        BitConverter.GetBytes(0xEUL).CopyTo(d, packet + 8);
        (ulong Value, ulong Reg)[] regs = { (0x44, CaLibrary.GsAlpha), (0x5040B, CaLibrary.GsTest), (5, CaLibrary.GsClamp) };
        for (int i = 0; i < regs.Length; i++)
        {
            BitConverter.GetBytes(regs[i].Value).CopyTo(d, packet + 16 + 16 * i);
            BitConverter.GetBytes(regs[i].Reg).CopyTo(d, packet + 24 + 16 * i);
        }

        var state = new CaLibrary(d).TextureState(rec);

        Assert.Equal(new CaLibrary.TextureGs(true, 0.5f, true, true), state);
        Assert.Null(new CaLibrary(d).TextureState(0x680));
    }

    [Fact]
    public void The_retail_disc_draws_scatter_props_and_blends_water()
    {
        string? disc = Environment.GetEnvironmentVariable("MAPBUILDER_CA_DISC");
        string run = Path.Combine(disc ?? "", "RUN");
        if (string.IsNullOrEmpty(disc) || !File.Exists(Path.Combine(run, "GLOBAL.ZDB"))) return;

        // RESERVOIR (Waterworks): 258 objects from the node tree, and 499 scatter instances of which two are parked
        // far off the island (y 3,280 and x -6,820) and not drawn.
        var reservoir = CaPackage.Open(Path.Combine(run, CaPackage.ArchiveName("RESERVOIR")));
        var island = reservoir.Libraries.Single(l => l.Res.Name == "res_i1.src").Lib;
        Assert.Equal(499, island.Instances().Count);
        var scene = reservoir.MapScene();
        Assert.Equal(258 + 497, scene.Count);

        // Its water (a translucent ripple texture over the reservoir) blends, with no alpha test.
        var water = reservoir.FindTextureState("ca_34c168b2");
        Assert.NotNull(water);
        Assert.True(water!.Blend);
        Assert.Null(water.AlphaTest);
        var surface = scene.Select(p => reservoir.ObjectMesh(p.LibraryIndex, p.Object))
            .SelectMany(m => m.Chunks).First(c => c.Texture == "ca_34c168b2");
        Assert.True(surface.MaterialBlend);
    }
}
