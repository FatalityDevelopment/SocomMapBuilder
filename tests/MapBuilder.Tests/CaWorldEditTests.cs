using CodeDesigner.Ps2.FileFormats.Assets;
using CodeDesigner.Ps2.FileFormats.Zipper.Ca;
using CodeDesigner.Ps2.FileFormats.Zipper.ZDB;
using Xunit;

namespace MapBuilder.Tests;

public sealed class CaWorldEditTests
{
    [Fact]
    public void DuplicateNodeHashMovesTheSelectedBranchInWorldSpace()
    {
        var writer = new CaLibraryWriter();
        int mesh = writer.AddStaticObject(new[] { 2f, 5, 4 }, Array.Empty<IReadOnlyList<CaLibraryWriter.ChainTag>>(), Array.Empty<int>());
        var root = new CaLibraryWriter.NodeSpec { Hash = 7, Local = Matrix4.Compose(40, 2, 8, 90, 2) };
        var first = new CaLibraryWriter.NodeSpec { Hash = 8, Objects = { mesh } };
        var second = new CaLibraryWriter.NodeSpec { Hash = 8, Local = Matrix4.Compose(3, 2, 0), Objects = { mesh } };
        second.Children.Add(new CaLibraryWriter.NodeSpec { Hash = 9, Local = Matrix4.Compose(2, 0, 0), Objects = { mesh } });
        root.Children.Add(first);
        root.Children.Add(second);
        writer.AddModel(root);
        byte[] original = writer.ToBytes(100);
        string dir = Directory.CreateTempSubdirectory("mb-ca-world-").FullName;
        try
        {
            using var zdb = ZdbArchive.CreateNew(1);
            zdb.AddResource("island.src", original, "ISLE", 1);
            string file = Path.Combine(dir, "WORLD.ZDB");
            zdb.Save(file);
            var pkg = CaPackage.Open(file);
            var scene = pkg.MapScene();
            Assert.Equal(3, scene.Count);
            var node = scene[1];
            Assert.EndsWith("~1", node.Instance);
            var world = Matrix4.Compose(-15, 1, 22, 45, 1.5f);
            var (resource, edited) = CaWorldEdits.Move(pkg, node.Instance, _ => world);
            int at = node.NodeOffset + 0x10;
            Assert.True(original.AsSpan(0, at).SequenceEqual(edited.AsSpan(0, at)));
            Assert.True(original.AsSpan(at + 64).SequenceEqual(edited.AsSpan(at + 64)));
            Assert.Equal(original, resource.Data);
            int index = pkg.Resources.IndexOf(resource);
            pkg.ReplaceResources(new Dictionary<int, byte[]> { [index] = edited });
            string copy = Path.Combine(dir, "copy.zdb");
            File.WriteAllBytes(copy, pkg.BuildCopy(new[] { index }));
            var moved = CaPackage.Open(copy).MapScene();
            Assert.Equal(scene[0].Matrix, moved[0].Matrix);
            AssertTransform(world, moved[1].Matrix);
            AssertTransform(Matrix4.Multiply(world, second.Children[0].Local), moved[2].Matrix);
        }
        finally { Directory.Delete(dir, true); }
    }

    private static void AssertTransform(float[] expected, float[] actual)
    {
        for (int i = 0; i < 16; i++) Assert.InRange(Math.Abs(expected[i] - actual[i]), 0f, 0.0001f);
    }
}
