namespace CodeDesigner.Ps2.FileFormats.Gta;

/// <summary>Geometry-local scale and translation in the DFF's own axes. Keeps the PS2 packet layout,
/// strip flags, weights and frame hierarchy, and adjusts each morph target's bounding sphere.</summary>
public static class GtaGeometryEdits
{
    public static byte[] Transform(byte[] dff, int geometry, float[] scale, float[] offset)
    {
        if (scale.Length != 3 || offset.Length != 3 || !scale.All(v => float.IsFinite(v) && v > 0) || !offset.All(float.IsFinite))
            throw new ArgumentException("Geometry scale needs three finite positive factors and offset needs three finite coordinates.");
        var clump = RwClump.Parse(dff);
        if ((uint)geometry >= clump.Geometries.Count) throw new ArgumentOutOfRangeException(nameof(geometry));
        var g = clump.Geometries[geometry];
        if (g.VertexCount <= 0 || g.MorphCount <= 0 || g.SphereOffset <= 0)
            throw new InvalidDataException("The geometry has no editable vertices.");
        var bytes = (byte[])dff.Clone();
        bool reorient = scale[0] != scale[1] || scale[1] != scale[2];
        // Several batches can reference the same original PS2 vertex span. Every write is computed
        // from the source, so an overlapping DMA reference always gets the same transformation.
        void Positions(RwClump.VectorStream stream, int count)
        {
            for (int i = 0; i < count; i++)
                for (int k = 0; k < 3; k++)
                {
                    int at = stream.Offset + i * stream.Stride + k * stream.ComponentBytes;
                    if (stream.ComponentBytes == 4)
                        WriteFloat(bytes, at, (double)BitConverter.ToSingle(dff, at) * scale[k] + offset[k]);
                    else
                    {
                        double raw = Math.Round(BitConverter.ToInt16(dff, at) * (double)scale[k] + offset[k] / (double)stream.Unit);
                        if (raw < short.MinValue || raw > short.MaxValue) throw new ArgumentException("Position exceeds this mesh's signed 16-bit range.");
                        BitConverter.TryWriteBytes(bytes.AsSpan(at), (short)raw);
                    }
                }
        }
        void Normals(RwClump.VectorStream stream, int count)
        {
            if (!reorient) return;
            Span<double> vector = stackalloc double[3];
            for (int i = 0; i < count; i++)
            {
                int start = stream.Offset + i * stream.Stride;
                for (int k = 0; k < 3; k++)
                {
                    int at = start + stream.ComponentBytes * k;
                    double value = stream.ComponentBytes == 4 ? BitConverter.ToSingle(dff, at) : (sbyte)dff[at] / 127.0;
                    vector[k] = value / scale[k];
                }
                double length = Math.Sqrt(vector[0] * vector[0] + vector[1] * vector[1] + vector[2] * vector[2]);
                if (length == 0) continue;
                for (int k = 0; k < 3; k++)
                {
                    if (stream.ComponentBytes == 4) WriteFloat(bytes, start + k * 4, vector[k] / length);
                    else bytes[start + k] = unchecked((byte)(sbyte)Math.Round(127 * vector[k] / length));
                }
            }
        }

        bool native = (g.Flags & 0x01000000) != 0;
        if (native)
        {
            if (g.MorphCount != 1 || g.Meshes.Count == 0 || g.Meshes.Any(m => m.Batches.Count == 0))
                throw new InvalidDataException("Cannot edit a native geometry with missing vertex batches or multiple morph targets.");
            foreach (var batch in g.Meshes.SelectMany(m => m.Batches))
            {
                Positions(batch.PositionStream ?? throw new InvalidDataException("Missing native position stream"), batch.Count);
                if (batch.NormalStream is { } normals) Normals(normals, batch.Count);
            }
        }

        int p = g.SphereOffset;
        for (int morph = 0; morph < g.MorphCount; morph++)
        {
            if (p > g.StructEnd - 24) throw new InvalidDataException("Truncated DFF morph target");
            for (int k = 0; k < 3; k++)
                WriteFloat(bytes, p + k * 4, (double)BitConverter.ToSingle(dff, p + k * 4) * scale[k] + offset[k]);
            WriteFloat(bytes, p + 12, (double)BitConverter.ToSingle(dff, p + 12) * scale.Max());
            bool hasPositions = BitConverter.ToInt32(dff, p + 16) != 0, hasNormals = BitConverter.ToInt32(dff, p + 20) != 0;
            p += 24;
            if (hasPositions)
            {
                if (p + g.VertexCount * 12L > g.StructEnd) throw new InvalidDataException("Truncated morph target positions");
                Positions(new(p, 12, 4, 1), g.VertexCount);
                p += g.VertexCount * 12;
            }
            if (hasNormals)
            {
                if (p + g.VertexCount * 12L > g.StructEnd) throw new InvalidDataException("Truncated morph target normals");
                Normals(new(p, 12, 4, 1), g.VertexCount);
                p += g.VertexCount * 12;
            }
        }
        return bytes;
    }

    private static void WriteFloat(byte[] destination, int at, double value)
    {
        float result = (float)value;
        if (!float.IsFinite(result)) throw new ArgumentException("The transformed geometry is outside the DFF float range.");
        BitConverter.TryWriteBytes(destination.AsSpan(at), result);
    }
}
