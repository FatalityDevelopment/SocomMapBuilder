using System.Text;

namespace CodeDesigner.Ps2.FileFormats.Zipper.MPS
{
    /// <summary>
    /// The named points of AIMAPS.MPS: starts, hostage starts, waypoints (port of zed-spawn-points.ts). A table of
    /// <c>count</c> (u32), one more u32, then 24-byte records: name[16], team byte (0xFF on starts), kind byte
    /// (2 start, 1 waypoint, 0 spectator), two unused bytes, and a packed cell (u32: x = bits 6..18, z = bits 19..31)
    /// in the first AI map's grid (<c>min + (cell + 0.5) × scale</c>). <see cref="Move"/> rewrites the cell.
    /// </summary>
    public static class MapPoints
    {
        public sealed class Point
        {
            public string Name { get; init; } = string.Empty;
            public int Team { get; init; }
            /// <summary>start, waypoint, spectator or other.</summary>
            public string Kind { get; init; } = string.Empty;
            public int CellX { get; init; }
            public int CellZ { get; init; }
            public float X { get; init; }
            public float Z { get; init; }
            /// <summary>Offset of the 24-byte record in the MPS file.</summary>
            public int RecordOffset { get; init; }
        }

        public static List<Point> Parse(byte[] mps, MpsMap? grid = null)
        {
            grid ??= MpsFile.Parse(mps).Maps.FirstOrDefault();
            if (grid == null || mps.Length < 64) return new List<Point>();
            for (int o = 0; o + 8 + 24 <= mps.Length; o += 4)
            {
                uint count = BitConverter.ToUInt32(mps, o);
                if (count < 1 || count > 256) continue;
                int first = o + 8;
                if (first + count * 24 > mps.Length) continue;
                var points = new List<Point>();
                bool ok = true;
                for (int i = 0; i < count; i++)
                {
                    int r = first + i * 24;
                    string? name = ReadName(mps, r);
                    byte kind = mps[r + 0x11];
                    if (name == null || kind > 2) { ok = false; break; }
                    uint packed = BitConverter.ToUInt32(mps, r + 0x14);
                    int cx = (int)((packed >> 6) & 0x1FFF), cz = (int)((packed >> 19) & 0x1FFF);
                    if (packed != 0 && (cx >= grid.Width || cz >= grid.Height)) { ok = false; break; }
                    points.Add(new Point
                    {
                        Name = name,
                        Team = mps[r + 0x10],
                        Kind = kind == 2 ? "start" : kind == 1 ? "waypoint" : packed == 0 ? "spectator" : "other",
                        CellX = cx,
                        CellZ = cz,
                        X = grid.MinX + (cx + 0.5f) * grid.ScaleX,
                        Z = grid.MinZ + (cz + 0.5f) * grid.ScaleZ,
                        RecordOffset = r,
                    });
                }
                if (ok && points.Any(p => p.Kind is "start" or "waypoint")) return points.Where(p => p.Kind != "spectator").ToList();
            }
            return new List<Point>();
        }

        /// <summary>Moves a point to the grid cell holding (x, z); the packed word's other bits are kept.</summary>
        public static byte[] Move(byte[] mps, string name, float x, float z)
        {
            var grid = MpsFile.Parse(mps).Maps.FirstOrDefault() ?? throw new InvalidDataException("AIMAPS.MPS has no AI map grid");
            var point = Parse(mps, grid).FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? throw new KeyNotFoundException($"No point named {name}");
            int cx = (int)MathF.Floor((x - grid.MinX) / grid.ScaleX), cz = (int)MathF.Floor((z - grid.MinZ) / grid.ScaleZ);
            if (cx < 0 || cz < 0 || cx >= grid.Width || cz >= grid.Height)
                throw new ArgumentOutOfRangeException(nameof(x), $"({x}, {z}) is outside the AI map ({grid.MinX}..{grid.MaxX}, {grid.MinZ}..{grid.MaxZ})");
            var copy = (byte[])mps.Clone();
            uint packed = BitConverter.ToUInt32(copy, point.RecordOffset + 0x14);
            packed = (packed & 0x3Fu) | ((uint)cx << 6) | ((uint)cz << 19);
            BitConverter.GetBytes(packed).CopyTo(copy, point.RecordOffset + 0x14);
            return copy;
        }

        private static string? ReadName(byte[] b, int at)
        {
            var sb = new StringBuilder();
            bool ended = false;
            for (int i = 0; i < 16; i++)
            {
                byte c = b[at + i];
                if (ended) { if (c != 0) return null; continue; }
                if (c == 0) { ended = true; continue; }
                if (c < 0x21 || c > 0x7E) return null;
                sb.Append((char)c);
            }
            return sb.Length >= 2 ? sb.ToString() : null;
        }
    }
}
