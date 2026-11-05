using CodeDesigner.Ps2.FileFormats.Assets;

namespace CodeDesigner.Ps2.FileFormats.Zipper.Ca;

/// <summary>Moves an ISLE node by changing its local transform. All attached meshes and children follow.</summary>
public static class CaWorldEdits
{
    public const string SpatialWarning = "The AI grid and baked visibility still describe the original layout. Check navigation and visibility in game.";

    public static (CaPackage.Resource Resource, byte[] Data) Move(CaPackage package, string instance,
        Func<float[], float[]> placement)
    {
        var node = package.MapScene().FirstOrDefault(p => p.Instance == instance)
            ?? throw new KeyNotFoundException($"No world instance '{instance}' in this location");
        if (node.NodeOffset < 0 && node.InstanceRecord < 0)
            throw new NotSupportedException(package.IsPsp
                ? $"'{instance}' is part of the island's world-space terrain and buildings; its vertices would have to move"
                : $"'{instance}' is a scatter instance (rocks, trees, crates); only world nodes can be moved for now");
        var resource = package.Libraries[node.LibraryIndex].Res;
        if (resource.Tag != "ISLE") throw new InvalidDataException("World node does not belong to an ISLE resource");
        float[] world = placement((float[])node.Matrix.Clone());
        if (world.Length != 16 || !world.All(float.IsFinite) || world[3] != 0 || world[7] != 0 || world[11] != 0 || world[15] != 1)
            throw new ArgumentException("The world matrix must be a finite affine transform");
        if (world.AsSpan().SequenceEqual(node.Matrix)) return (resource, resource.Data);
        if (node.InstanceRecord >= 0)
        {
            // A Fireteam Bravo 2 instance: its 0x60-byte record starts with the world matrix (w column 0, 0, 0, 1).
            var record = (byte[])resource.Data.Clone();
            bool shift = world.AsSpan(0, 12).SequenceEqual(node.Matrix.AsSpan(0, 12));
            int from = shift ? 12 * sizeof(float) : 0;
            Buffer.BlockCopy(world, from, record, node.InstanceRecord + from, (shift ? 3 : 16) * sizeof(float));
            return (resource, record);
        }
        var local = Matrix4.Multiply(Matrix4.InverseAffine(node.ParentWorld), world);
        if (!local.All(float.IsFinite)) throw new ArgumentException("The local transform is outside the supported range");
        var data = (byte[])resource.Data.Clone();
        // A translation should preserve the stored axes, including their original floating point rounding.
        bool translationOnly = world.AsSpan(0, 12).SequenceEqual(node.Matrix.AsSpan(0, 12));
        int start = translationOnly ? 12 * sizeof(float) : 0;
        int length = (translationOnly ? 3 : 16) * sizeof(float);
        Buffer.BlockCopy(local, start, data, node.NodeOffset + 0x10 + start, length);
        return (resource, data);
    }
}
