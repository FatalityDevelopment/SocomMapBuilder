using CodeDesigner.Ps2.FileFormats.Assets;
using CodeDesigner.Ps2.FileFormats.Zipper.Geo;
using CodeDesigner.Ps2.FileFormats.Zipper.Model;
using CodeDesigner.Ps2.FileFormats.Zipper.ZED;
using Xunit;

namespace MapBuilder.Tests;

public sealed class VisualPlacementTests
{
    [Theory]
    [InlineData("N002_I006_V01_L", 2, 1)]
    [InlineData("N002_I006_V01", 2, 1)]
    [InlineData("N012_003", 12, 3)]
    [InlineData("n002_i006_v01_l", 2, 1)]
    public void Retail_visual_keys_keep_their_node_and_visual_indices(string key, int node, int visual)
        => Assert.Equal((node, visual), VisualPlacement.ParseKey(key));

    [Fact]
    public void Suffixed_visual_uses_its_parent_transform_and_position_unit()
    {
        var c = ZedContainer.CreateNew();
        c.AddNode("lamp", Array.Empty<byte>(), 2);
        c.AddNode("nparams", Transform(0, 0, 0));
        c.AddNode("children", Array.Empty<byte>(), 1);
        c.AddNode("fixture", Array.Empty<byte>(), 2);
        c.AddNode("nparams", Transform(10, 20, 30));
        c.AddNode("children", Array.Empty<byte>(), 1);
        c.AddNode("frame", Array.Empty<byte>(), 2);
        c.AddNode("nparams", Transform(1, 2, 3));
        c.AddNode("visuals", Array.Empty<byte>(), 1);
        c.AddNode("vis", Array.Empty<byte>(), 1);
        c.AddNode("vparams", BitConverter.GetBytes(0x20000u));
        var root = GeoScene.Parse(c.Nodes).FindModel("lamp")!;
        var placements = VisualPlacement.Build(root, new[] { "N000_I006_V00_L" });
        var placement = placements["N000_I006_V00_L"];
        Assert.Equal("frame", placement.NodeName);
        Assert.Equal(ZedModelFile.PositionUnit.Scale, placement.Unit);
        Assert.Equal((11f, 22f, 33f), Matrix4.Apply(placement.Matrix, 0, 0, 0));
    }

    private static byte[] Transform(float x, float y, float z)
    {
        float[] matrix = Matrix4.Identity();
        matrix[12] = x; matrix[13] = y; matrix[14] = z;
        byte[] bytes = new byte[64];
        Buffer.BlockCopy(matrix, 0, bytes, 0, bytes.Length);
        return bytes;
    }
}
