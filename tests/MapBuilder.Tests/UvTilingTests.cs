using System.Text.Json;
using CodeDesigner.Ps2.FileFormats.Zipper.Package;
using MapBuilder.Tests.Fixtures;
using Xunit;

namespace MapBuilder.Tests;

/// <summary>
/// Repeating textures on new geometry (addMesh uvTile / uvScale) within the −8 … 8 repeats an int16 / 4096 UV holds,
/// and per-axis scale on placed props.
/// </summary>
public sealed class UvTilingTests
{
    // A 100 × 100 floor at y 0: two triangles sharing their corners.
    private static MeshTopology.InputMesh Floor()
    {
        var m = new MeshTopology.InputMesh();
        foreach (var p in new[] { new[] { 0f, 0, 0 }, new[] { 100f, 0, 0 }, new[] { 100f, 0, 100 }, new[] { 0f, 0, 100 } })
        {
            m.Positions.Add(p); m.Normals.Add(new[] { 0f, 1, 0 }); m.Uvs.Add(new[] { 0f, 0 });
        }
        m.Triangles.Add((0, 2, 1));
        m.Triangles.Add((0, 3, 2));
        return m;
    }

    private static float Area(MeshTopology.InputMesh m) => m.Triangles.Sum(t =>
    {
        var (a, b, c) = t;
        float[] p = m.Positions[a], q = m.Positions[b], r = m.Positions[c];
        float ux = q[0] - p[0], uz = q[2] - p[2], vx = r[0] - p[0], vz = r[2] - p[2];
        return MathF.Abs(ux * vz - uz * vx) / 2;
    });

    [Fact]
    public void A_big_floor_is_split_so_every_uv_stays_in_range_and_repeats_at_the_tile_size()
    {
        var tiled = UvTiling.Project(Floor(), 5); // 20 repeats across
        Assert.True(tiled.Triangles.Count > 2);
        Assert.Equal(10000f, Area(tiled), 1f);
        Assert.All(tiled.Uvs, uv => { Assert.InRange(uv[0], 0f, 8f); Assert.InRange(uv[1], 0f, 8f); });
        foreach (var (a, b, c) in tiled.Triangles)
        {
            // One repeat every 5 units: UV differences match position differences / 5.
            float du = tiled.Uvs[b][0] - tiled.Uvs[a][0], dx = tiled.Positions[b][0] - tiled.Positions[a][0];
            Assert.Equal(dx / 5, du, 3);
            float dv = tiled.Uvs[c][1] - tiled.Uvs[a][1], dz = tiled.Positions[c][2] - tiled.Positions[a][2];
            Assert.Equal(-dz / 5, dv, 3);
            // Each triangle's UVs start within the first repeat.
            Assert.InRange(new[] { a, b, c }.Min(i => tiled.Uvs[i][0]), 0f, 1f);
        }
        Assert.Equal(tiled.Positions.Count, tiled.Normals.Count);
    }

    [Fact]
    public void Walls_project_from_their_side_and_small_meshes_are_not_split()
    {
        var wall = new MeshTopology.InputMesh();
        foreach (var p in new[] { new[] { 0f, 0, 0 }, new[] { 0f, 10, 0 }, new[] { 0f, 10, 20 } }) { wall.Positions.Add(p); wall.Uvs.Add(new[] { 0f, 0 }); }
        wall.Triangles.Add((0, 1, 2));
        var tiled = UvTiling.Project(wall, 10);
        Assert.Single(tiled.Triangles);
        Assert.Equal(new[] { 0f, 1f }, tiled.Uvs[1]); // z 0, y 10
        Assert.Equal(new[] { 2f, 1f }, tiled.Uvs[2]); // z 20, y 10
    }

    [Fact]
    public void UvScale_multiplies_and_rejects_bad_values()
    {
        var m = Floor();
        m.Uvs[1] = new[] { 1f, 0 }; m.Uvs[2] = new[] { 1f, 1 }; m.Uvs[3] = new[] { 0f, 1 };
        var scaled = UvTiling.Scale(m, 12, 3);
        Assert.All(scaled.Uvs, uv => Assert.InRange(uv[0], 0f, 8f));
        Assert.Equal(10000f, Area(scaled), 1f);
        Assert.Throws<ArgumentException>(() => UvTiling.Scale(m, 0, 1));
        Assert.Throws<ArgumentException>(() => UvTiling.Project(m, -1));
    }

    [Fact]
    public void A_prop_scales_per_axis_and_clutter_refuses_to()
    {
        using var pkg = new SyntheticPackage();
        var b = new Backend();
        string session = b.Open(pkg.Folder);
        var crate = b.Scene(session).GetProperty("instances").EnumerateArray().First(i => i.GetProperty("instance").GetString() == "crate_1");
        int geo = crate.GetProperty("geoIndex").GetInt32();
        b.Edit(session, new { op = "moveInstance", geoIndex = geo, yaw = 90, scale = new[] { 2f, 1, 3 } });
        var m = b.Scene(session).GetProperty("instances").EnumerateArray().First(i => i.GetProperty("geoIndex").GetInt32() == geo)
            .GetProperty("matrix").EnumerateArray().Select(v => v.GetSingle()).ToArray();
        // Yaw 90: the x axis (length 2) points to -z, the z axis (length 3) to +x; the position stays.
        Assert.Equal(new[] { 0f, 0, -2 }, new[] { MathF.Round(m[0], 4), m[1], MathF.Round(m[2], 4) });
        Assert.Equal(1f, m[5], 4);
        Assert.Equal(new[] { 3f, 0, 0 }, new[] { MathF.Round(m[8], 4), m[9], MathF.Round(m[10], 4) });
        Assert.Equal(new[] { 10f, 0, 20 }, new[] { m[12], m[13], m[14] });

        // A later yaw keeps each axis's scale.
        b.Edit(session, new { op = "moveInstance", geoIndex = geo, yaw = 0 });
        m = b.Scene(session).GetProperty("instances").EnumerateArray().First(i => i.GetProperty("geoIndex").GetInt32() == geo)
            .GetProperty("matrix").EnumerateArray().Select(v => v.GetSingle()).ToArray();
        Assert.Equal((2f, 3f), (MathF.Round(m[0], 4), MathF.Round(m[10], 4)));

        Assert.Contains("one number", b.EditFails(session, new { op = "moveClutter", clutterIndex = 0, scale = new[] { 1f, 2, 1 } }));
        Assert.Contains("greater than 0", b.EditFails(session, new { op = "moveInstance", geoIndex = geo, scale = new[] { 1f, 0, 1 } }));
    }
}
