using System.Text;
using CodeDesigner.Ps2.FileFormats.Gta;
using Xunit;

namespace MapBuilder.Tests;

public sealed class GtaMaterialTests
{
    private static byte[] Chunk(uint type, params byte[][] contents)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(type);
        writer.Write(contents.Sum(b => b.Length));
        writer.Write(0x1803FFFF);
        foreach (var bytes in contents) writer.Write(bytes);
        return stream.ToArray();
    }

    [Fact]
    public void A_longer_texture_name_resizes_only_the_string_and_its_ancestor_chunk_lengths()
    {
        byte[] MakeMaterial(byte[] name) => Chunk(Rw.Material, Chunk(Rw.Struct, new byte[28]),
            Chunk(Rw.Texture, Chunk(Rw.Struct, new byte[4]), Chunk(Rw.String, name),
                Chunk(Rw.String, "mask\0\0\0\0"u8.ToArray()), Chunk(Rw.Extension)), Chunk(Rw.Extension));
        byte[] Build(byte[] name) => Chunk(Rw.Clump, Chunk(Rw.Struct, new byte[12]),
            Chunk(Rw.GeometryList, Chunk(Rw.Struct, BitConverter.GetBytes(1)), Chunk(Rw.Geometry, Chunk(Rw.Struct, new byte[16]),
                Chunk(Rw.MaterialList, Chunk(Rw.Struct, BitConverter.GetBytes(2), BitConverter.GetBytes(-1), BitConverter.GetBytes(-1)),
                    MakeMaterial(name), MakeMaterial("second\0\0"u8.ToArray())), Chunk(Rw.Extension, Chunk(0xDEAD, new byte[] { 4, 3, 2, 1 }))))).Concat(new byte[] { 8, 9, 10, 11 }).ToArray();
        byte[] original = Build("a\0\0\0"u8.ToArray());
        const string replacement = "replacement_diffuse";
        var expected = new byte[20];
        Encoding.ASCII.GetBytes(replacement).CopyTo(expected, 0);
        byte[] edited = GtaDffEdits.SetMaterial(original, 0, 0, null, replacement);
        Assert.Equal(Build(expected), edited);
        Assert.Equal("a", GtaDffEdits.Describe(original).Materials[0].Texture);
        Assert.Equal(new[] { replacement, "second" }, GtaDffEdits.Describe(edited).Materials.Select(m => m.Texture));
        Assert.Throws<ArgumentException>(() => GtaDffEdits.SetMaterial(original, 0, 0, null, new string('x', 32)));
    }
}
