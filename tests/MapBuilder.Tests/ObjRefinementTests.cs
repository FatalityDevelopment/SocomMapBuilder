using System.Text.Json;
using CodeDesigner.Ps2.FileFormats.Zipper.Package;
using MapBuilder.Server;
using MapBuilder.Server.Services;
using Xunit;

namespace MapBuilder.Tests;

public class ObjRefinementTests
{
    [Fact]
    public void AdjacentObjectsKeepMaterialsAndShareTheirRoundedSeam()
    {
        const string input = """
            mtllib sample.mtl
            o left
            usemtl coat
            v 0 0 0
            v 1 0 0
            v 0 1 0
            vt 0 0
            f -3/1 -2/1 -1/1
            o right
            usemtl sleeve
            v 0 1 0
            v 1 0 0
            v 1 1 0.25
            vt 1 1
            f -3/2 -2/2 -1/2
            """;
        var reply = JsonSerializer.Deserialize<CommandResponse>(ObjRefinement.Run(new CommandMessage {
            Command = "MESH_REFINE_OBJ", Data = input, AssetJson = """{"brushes":[{"center":[0,0,0],"radius":[2,2,2],"scale":[1.2,1,1]}]}"""
        }))!;
        using var data = JsonDocument.Parse(reply.Data!);
        string output = data.RootElement.GetProperty("obj").GetString()!;
        var directives = new List<(int, string)>();
        var mesh = MeshTopology.ParseObj(output, onDirective: (face, line) => directives.Add((face, line)));
        Assert.Equal(8, mesh.Triangles.Count);
        Assert.Contains((0, "mtllib sample.mtl"), directives);
        Assert.Contains((0, "usemtl coat"), directives);
        Assert.Contains((4, "o right"), directives);
        Assert.Contains((4, "usemtl sleeve"), directives);
        var left = mesh.Positions.Where((_, i) => mesh.Uvs[i][0] == 0);
        var right = mesh.Positions.Where((_, i) => mesh.Uvs[i][0] == 1).ToList();
        Assert.Equal(3, left.Count(p => right.Any(q => q.SequenceEqual(p))));
    }
}
