using System.Buffers.Binary;

namespace CodeDesigner.Ps2.FileFormats.Gta;

/// <summary>Edit the primary texture coordinates of a rendered DFF mesh. Editor V increases upwards.</summary>
public static class GtaUvEdits
{
    public sealed record Vertex(int Index, float U, float V);
    public sealed record Chunk(int GeometryOffset, IReadOnlyList<Vertex> Vertices);

    public static byte[] Set(byte[] dff, IReadOnlyList<Chunk> changes)
    {
        if (changes.Count == 0) throw new ArgumentException("Select at least one UV to edit.");
        // Plain material splits share one vertex array. A native split can be displayed several times
        // (vehicle wheels), and its DMA batches can reference overlapping data.
        var sources = RwClump.Parse(dff).Geometries.SelectMany(g => g.Meshes)
            .Where(m => m.EditableUvOffset >= 0).DistinctBy(m => m.SourceOffset).ToDictionary(m => m.SourceOffset);
        byte[] edited = (byte[])dff.Clone();
        var written = new HashSet<int>();
        foreach (var chunk in changes)
        {
            if (!sources.TryGetValue(chunk.GeometryOffset, out var source)) throw new ArgumentException("Choose a DFF mesh with stored texture coordinates.");
            if (chunk.Vertices.Count == 0) throw new ArgumentException("Select at least one UV in each edited mesh.");
            int index = 0, first = 0;
            foreach (var vertex in chunk.Vertices.OrderBy(v => v.Index))
            {
                while (index < source.Batches.Count && vertex.Index - first >= source.Batches[index].Count)
                    first += source.Batches[index++].Count;
                if (vertex.Index < 0 || index >= source.Batches.Count) throw new ArgumentOutOfRangeException(nameof(changes), "UV vertex is outside the mesh.");
                var batch = source.Batches[index];
                var stream = batch.UvStream!;
                int i = vertex.Index - first, at = stream.Offset + stream.Stride * i;
                Write(at, stream, batch.Uvs[2 * i], vertex.U, invert: false);
                Write(at + stream.ComponentBytes, stream, batch.Uvs[2 * i + 1], vertex.V, invert: true);
            }
        }
        return edited;

        void Write(int at, RwClump.VectorStream stream, float stored, float requested, bool invert)
        {
            if (!float.IsFinite(requested)) throw new ArgumentException("UV coordinates must be finite.");
            Span<byte> encoded = stackalloc byte[4];
            var bytes = encoded[..stream.ComponentBytes];
            if (requested == (invert ? 1f - stored : stored))
            {
                // V -> (1 - V) -> V can round a float. Keep the exact source bits on an unchanged axis.
                dff.AsSpan(at, bytes.Length).CopyTo(bytes);
            }
            else
            {
                float value = invert ? 1f - requested : requested;
                if (stream.ComponentBytes == 4) BinaryPrimitives.WriteSingleLittleEndian(bytes, value);
                else
                {
                    double quantized = Math.Round((double)value / stream.Unit);
                    if (quantized < (stream.Unsigned ? 0 : short.MinValue) || quantized > (stream.Unsigned ? ushort.MaxValue : short.MaxValue))
                        throw new ArgumentException("UV exceeds the 16-bit range of this PS2 mesh.");
                    BinaryPrimitives.WriteUInt16LittleEndian(bytes, unchecked((ushort)(int)quantized));
                }
            }
            var destination = edited.AsSpan(at, bytes.Length);
            if (!written.Add(at) && !destination.SequenceEqual(bytes))
                throw new ArgumentException("The selection assigns different UVs to the same stored vertex.");
            bytes.CopyTo(destination);
        }
    }
}
