using CodeDesigner.Ps2.FileFormats.Zipper.Geo;
using CodeDesigner.Ps2.FileFormats.Zipper.MPS;

namespace CodeDesigner.Ps2.FileFormats.Zipper.Package
{
    /// <summary>
    /// An AI map's cells fitted to the map's collision (fitGrid): each stored cell gets the walkable bit and the floor
    /// height of what is solid under its centre now. The grid keeps its size, its cells and its row table (so nothing
    /// after it moves), and every other bit (cell byte 0's other bits, byte 3, the height word's top bits) stays as
    /// it was: those are authored and not decoded. Byte 0 is 0x01 on walkable cells and 0x0A on the rest. On MP1,
    /// MP2 and MP5 that matches "a floor face above the cell" for 92-94% of cells, and the stored height matches
    /// the floor there for 61-95% (maps with upper storeys keep them in their other AI maps). Docs: docs/formats/aimaps.md.
    /// </summary>
    public static class GridFit
    {
        public sealed record Result(byte[] Mps, int Cells, int Walkable, int Changed, int Clamped);

        public const byte WalkableFlags = 0x01, BlockedFlags = 0x0A;

        /// <param name="lowest">Take the lowest floor under a cell (the ground; default) rather than the highest (tops of props).</param>
        public static Result Fit(ZipperPackage pkg, int mapIndex = 0, bool lowest = true)
        {
            var entry = pkg.Entries.FirstOrDefault(e => e.Leaf.EndsWith(".MPS", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("The package has no AIMAPS.MPS");
            var map = pkg.MapScene() ?? throw new InvalidOperationException("The package has no map scene");
            byte[] mps = (byte[])entry.Data.Clone();
            var maps = MpsFile.Parse(mps).Maps;
            if (mapIndex < 0 || mapIndex >= maps.Count) throw new ArgumentOutOfRangeException(nameof(mapIndex), $"No AI map #{mapIndex} (the file has {maps.Count})");
            var g = maps[mapIndex];

            var floors = CollisionEdits.WorldFaces(map.Scene).Where(f => f.Face.Normal()[1] > 0.3f).Select(f => new Floor(f.World)).ToList();
            int sub = (int)g.Offset + 0x58;
            uint count = BitConverter.ToUInt32(mps, sub + 0x1C);
            long table = sub + 0x50, rows = table + count * 8L;
            if (rows + g.Height * 12L > mps.Length) throw new InvalidDataException("The AI map's cells run past the end of the file");
            int walkable = 0, changed = 0, clamped = 0;
            for (int z = 0; z < g.Height; z++)
            {
                long row = rows + z * 12L;
                int first = BitConverter.ToUInt16(mps, (int)row), end = BitConverter.ToUInt16(mps, (int)row + 2);
                uint index = BitConverter.ToUInt32(mps, (int)row + 4);
                for (int x = first; x < end; x++)
                {
                    int c = (int)(table + (index + (x - first)) * 8L);
                    float wx = g.MinX + (x + 0.5f) * g.ScaleX, wz = g.MinZ + (z + 0.5f) * g.ScaleZ;
                    float? y = null;
                    foreach (var f in floors)
                        if (f.HeightAt(wx, wz) is float h && (y == null || (lowest ? h < y : h > y))) y = h;
                    // Only the walkable bits (0x01 / 0x0A) change; byte 0's other bits stay.
                    byte b0 = (byte)((mps[c] & ~(WalkableFlags | BlockedFlags)) | (y == null ? BlockedFlags : WalkableFlags));
                    ushort word = BitConverter.ToUInt16(mps, c + 1);
                    if (y is float height)
                    {
                        walkable++;
                        float span = g.MaxY - g.MinY;
                        float t = span > 0 ? (height - g.MinY) / span : 0;
                        if (t < 0 || t > 1) { clamped++; t = Math.Clamp(t, 0, 1); }
                        float stored = g.MinY + (word & 0x7FF) / 2047f * span;
                        // Within half a unit is the same floor (the 11-bit height rounds); leave it.
                        if (MathF.Abs(stored - (g.MinY + t * span)) > 0.5f) word = (ushort)((word & ~0x7FF) | (int)MathF.Round(t * 2047));
                    }
                    if (mps[c] != b0 || BitConverter.ToUInt16(mps, c + 1) != word) changed++;
                    mps[c] = b0;
                    BitConverter.GetBytes(word).CopyTo(mps, c + 1);
                }
            }
            return new Result(mps, (int)count, walkable, changed, clamped);
        }

        private sealed class Floor
        {
            private readonly float[] _p;
            private readonly float _minX, _maxX, _minZ, _maxZ, _nx, _ny, _nz;

            public Floor(float[] world)
            {
                _p = world;
                int n = world.Length / 3;
                _minX = _maxX = world[0]; _minZ = _maxZ = world[2];
                for (int i = 1; i < n; i++)
                {
                    _minX = MathF.Min(_minX, world[i * 3]); _maxX = MathF.Max(_maxX, world[i * 3]);
                    _minZ = MathF.Min(_minZ, world[i * 3 + 2]); _maxZ = MathF.Max(_maxZ, world[i * 3 + 2]);
                }
                float bx = world[3] - world[0], by = world[4] - world[1], bz = world[5] - world[2];
                float cx = world[6] - world[0], cy = world[7] - world[1], cz = world[8] - world[2];
                _nx = by * cz - bz * cy; _ny = bz * cx - bx * cz; _nz = bx * cy - by * cx;
            }

            // The face's height at (x, z) when the point lies inside it seen from above.
            public float? HeightAt(float x, float z)
            {
                if (x < _minX || x > _maxX || z < _minZ || z > _maxZ || MathF.Abs(_ny) < 1e-6f) return null;
                int n = _p.Length / 3;
                bool inside = false;
                for (int i = 0, j = n - 1; i < n; j = i++)
                    if ((_p[i * 3 + 2] > z) != (_p[j * 3 + 2] > z)
                        && x < (_p[j * 3] - _p[i * 3]) * (z - _p[i * 3 + 2]) / (_p[j * 3 + 2] - _p[i * 3 + 2]) + _p[i * 3]) inside = !inside;
                if (!inside) return null;
                return _p[1] - (_nx * (x - _p[0]) + _nz * (z - _p[2])) / _ny;
            }
        }
    }
}
