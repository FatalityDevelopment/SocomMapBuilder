using System.Buffers.Binary;
using System.Text;

namespace CodeDesigner.Ps2.FileFormats.Gta;

/// <summary>Native RenderWare edits. All frame positions are in the parent's GTA coordinate system.</summary>
public static class GtaDffEdits
{
    public sealed record Frame(int Index, string Name, float[] Position);
    public sealed record Material(int Geometry, int Index, int[] Color, string? Texture, int TextureCapacity);
    public sealed record ModelInfo(List<Frame> Frames, List<Material> Materials, int GeometryCount);
    private const int MaxTextureName = 31; // RenderWare texture name[32], including its terminator
    private sealed record MaterialSlot(int Geometry, int Index, int Flags, int Color, int Name, int Capacity, int[] ChunkSizes);

    private static (int Data, int Size) Child(byte[] d, (int Data, int Size) container, uint type) =>
        Rw.Find(d, container.Data, container.Data + container.Size, type)
        ?? throw new InvalidDataException($"Missing RenderWare chunk 0x{type:x}");

    private static (int Data, int Size) Clump(byte[] d) => Child(d, (0, d.Length), Rw.Clump);

    private static int FrameAt(byte[] d, int index)
    {
        var frames = Child(d, Child(d, Clump(d), Rw.FrameList), Rw.Struct);
        if (frames.Size < 4 || index < 0 || index >= BitConverter.ToInt32(d, frames.Data) || index >= (frames.Size - 4) / 56)
            throw new ArgumentException("Frame index is outside this model");
        return frames.Data + 4 + index * 56;
    }

    private static IEnumerable<MaterialSlot> Slots(byte[] d)
    {
        var clump = Clump(d);
        var list = Child(d, clump, Rw.GeometryList);
        int geometry = -1;
        foreach (var g in Rw.Chunks(d, list.Data, list.Data + list.Size).Where(c => c.Type == Rw.Geometry))
        {
            geometry++;
            var geoStruct = Child(d, (g.Data, g.Size), Rw.Struct);
            if (geoStruct.Size < 4) throw new InvalidDataException("Truncated geometry flags");
            if (Rw.Find(d, g.Data, g.Data + g.Size, Rw.MaterialList) is not { } materials) continue;
            int index = 0;
            foreach (var mat in Rw.Chunks(d, materials.Data, materials.Data + materials.Size).Where(c => c.Type == Rw.Material))
            {
                var header = Child(d, (mat.Data, mat.Size), Rw.Struct);
                if (header.Size < 8) throw new InvalidDataException("Truncated material color");
                var texture = Rw.Find(d, mat.Data, mat.Data + mat.Size, Rw.Texture);
                var name = texture is { } t ? Rw.Find(d, t.Data, t.Data + t.Size, Rw.String) : null;
                // The size word is eight bytes before each chunk's payload. All seven sizes cover the string.
                var sizes = texture is { } tx && name is { } n
                    ? new[] { clump.Data - 8, list.Data - 8, g.Data - 8, materials.Data - 8, mat.Data - 8, tx.Data - 8, n.Data - 8 }
                    : Array.Empty<int>();
                yield return new(geometry, index++, geoStruct.Data, header.Data + 4, name?.Data ?? 0, name?.Size ?? 0, sizes);
            }
        }
    }

    public static ModelInfo Describe(byte[] data)
    {
        var clump = RwClump.Parse(data);
        return new(clump.Frames.Select((f, i) => new Frame(i, f.Name, f.Local[12..15])).ToList(),
            Slots(data).Select(m => new Material(m.Geometry, m.Index, data[m.Color..(m.Color + 4)].Select(b => (int)b).ToArray(),
                m.Name > 0 ? Rw.ReadString(data, m.Name, m.Capacity) : null, m.Name > 0 ? MaxTextureName : 0)).ToList(), clump.Geometries.Count);
    }

    public static byte[] MoveFrame(byte[] data, int frame, float[] position)
    {
        if (position.Length != 3 || !position.All(float.IsFinite)) throw new ArgumentException("Enter three finite coordinates");
        int at = FrameAt(data, frame) + 36;
        var copy = (byte[])data.Clone();
        for (int k = 0; k < 3; k++) BinaryPrimitives.WriteSingleLittleEndian(copy.AsSpan(at + k * 4), position[k]);
        return copy;
    }

    public static byte[] SetMaterial(byte[] data, int geometry, int material, int[]? color, string? texture)
    {
        var slot = Slots(data).FirstOrDefault(s => s.Geometry == geometry && s.Index == material)
            ?? throw new ArgumentException("Material index is outside this geometry");
        if (color == null && texture == null) throw new ArgumentException("Supply a color or texture name");
        var copy = (byte[])data.Clone();
        if (color != null)
        {
            if (color.Length != 4 || color.Any(c => c < 0 || c > 255)) throw new ArgumentException("RGBA channels must be bytes");
            if (!color.Select(c => (byte)c).SequenceEqual(copy.AsSpan(slot.Color, 4).ToArray()))
            {
                for (int k = 0; k < 4; k++) copy[slot.Color + k] = (byte)color[k];
                copy[slot.Flags] |= 0x40; // rpGEOMETRYMODULATEMATERIALCOLOR
            }
        }
        if (texture != null && (slot.Name == 0 || texture != Rw.ReadString(data, slot.Name, slot.Capacity)))
        {
            if (slot.Name == 0 || texture.Length is 0 or > MaxTextureName || texture.Any(c => c < ' ' || c > '~' || c is '/' or '\\'))
                throw new ArgumentException($"Choose a textured material and a printable name of 1 to {MaxTextureName} characters");
            if (texture.Length >= slot.Capacity)
            {
                int size = (texture.Length + 4) & ~3;
                int growth = size - slot.Capacity;
                int tail = slot.Name + slot.Capacity;
                Array.Resize(ref copy, checked(copy.Length + growth));
                data.AsSpan(tail).CopyTo(copy.AsSpan(tail + growth));
                copy.AsSpan(slot.Name, size).Clear();
                foreach (int at in slot.ChunkSizes)
                    BinaryPrimitives.WriteInt32LittleEndian(copy.AsSpan(at), checked(BitConverter.ToInt32(data, at) + growth));
            }
            Encoding.ASCII.GetBytes(texture, copy.AsSpan(slot.Name));
            copy[slot.Name + texture.Length] = 0;
        }
        return copy;
    }
}
