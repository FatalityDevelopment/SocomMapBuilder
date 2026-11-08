using System.Text;

namespace CodeDesigner.Ps2.FileFormats.Zipper.MPS
{
    /// <summary>
    /// The named points of AIMAPS.MPS: starts, hostage starts, waypoints (port of zed-spawn-points.ts). A table of
    /// <c>count</c> (u32), one more u32, then 24-byte records: name[16], team byte (0xFF on starts), kind byte
    /// (2 start, 1 waypoint, 0 spectator), two unused bytes, and a packed cell (u32: x = bits 6..18, z = bits 19..31)
    /// in the first AI map's grid (<c>min + (cell + 0.5) × scale</c>; the height is the cell's floor in that grid).
    /// <see cref="Move"/> rewrites the cell.
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
            /// <summary>The floor height of its cell in the AI grid (<see cref="MpsMap.HeightAt"/>), or null.</summary>
            public float? Y { get; init; }
            public float Z { get; init; }
            /// <summary>Offset of the 24-byte record in the MPS file.</summary>
            public int RecordOffset { get; init; }
        }

        public static List<Point> Parse(byte[] mps, MpsMap? grid = null) =>
            Table(mps, grid) is { } t ? t.Points.Where(p => p.Kind != "spectator").ToList() : new List<Point>();

        /// <summary>The point table: its offset (the count word), and every record, the spectator's included.</summary>
        private static (int Offset, List<Point> Points)? Table(byte[] mps, MpsMap? grid = null)
        {
            grid ??= MpsFile.Parse(mps).Maps.FirstOrDefault();
            if (grid == null || mps.Length < 64) return null;
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
                        Y = grid.HeightAt(mps, cx, cz),
                        Z = grid.MinZ + (cz + 0.5f) * grid.ScaleZ,
                        RecordOffset = r,
                    });
                }
                if (ok && points.Any(p => p.Kind is "start" or "waypoint")) return (o, points);
            }
            return null;
        }

        /// <summary>
        /// Adds a point at the end of the table: a copy of an existing record of the same kind (its unused bytes and
        /// the packed word's low bits), named, teamed and moved to the cell holding (x, z). The table is one of the
        /// file's count-prefixed tables read in order, with no offsets into the file after it, so the records after
        /// it move down by 24 bytes. Not yet tried in game.
        /// </summary>
        public static byte[] Add(byte[] mps, string name, string kind, int? team, float x, float z)
        {
            var (offset, points) = Table(mps) ?? throw new InvalidDataException("AIMAPS.MPS has no point table");
            CheckName(name, points, null);
            byte kindByte = KindByte(kind);
            if (points.Count >= 256) throw new InvalidOperationException("The point table holds at most 256 points");
            var template = points.FirstOrDefault(p => mps[p.RecordOffset + 0x11] == kindByte && p.Kind != "spectator") ?? points.First(p => p.Kind != "spectator");
            var record = new byte[24];
            Array.Copy(mps, template.RecordOffset, record, 0, 24);
            Array.Clear(record, 0, 16);
            Encoding.ASCII.GetBytes(name).CopyTo(record, 0);
            record[0x11] = kindByte;
            if (team is int t)
            {
                if (t is < 0 or > 255) throw new ArgumentOutOfRangeException(nameof(team), "team is a byte (0-255)");
                record[0x10] = (byte)t;
            }
            int at = offset + 8 + points.Count * 24;
            var copy = new byte[mps.Length + 24];
            Array.Copy(mps, copy, at);
            record.CopyTo(copy, at);
            Array.Copy(mps, at, copy, at + 24, mps.Length - at);
            BitConverter.GetBytes((uint)(points.Count + 1)).CopyTo(copy, offset);
            return Move(copy, name, x, z);
        }

        /// <summary>Removes a point (not the spectator) and closes the gap; the points after it move up one record.</summary>
        public static byte[] Remove(byte[] mps, string name)
        {
            var (offset, points) = Table(mps) ?? throw new InvalidDataException("AIMAPS.MPS has no point table");
            var point = points.FirstOrDefault(p => p.Kind != "spectator" && p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? throw new KeyNotFoundException($"No point named {name}");
            if (point.Kind is "start" or "waypoint" && points.Count(p => p.Kind is "start" or "waypoint") == 1)
                throw new InvalidOperationException($"{name} is the map's last start or waypoint");
            var copy = new byte[mps.Length - 24];
            Array.Copy(mps, copy, point.RecordOffset);
            Array.Copy(mps, point.RecordOffset + 24, copy, point.RecordOffset, mps.Length - point.RecordOffset - 24);
            BitConverter.GetBytes((uint)(points.Count - 1)).CopyTo(copy, offset);
            return copy;
        }

        private static byte KindByte(string kind) => kind.ToLowerInvariant() switch
        {
            "start" => 2,
            "waypoint" => 1,
            "other" => 0,
            _ => throw new ArgumentException("kind is start, waypoint or other"),
        };

        private static void CheckName(string name, IEnumerable<Point> points, Point? except)
        {
            if (name.Length is < 2 or > 15 || name.Any(c => c < 0x20 || c > 0x7E) || name[0] == ' ' || name[^1] == ' ')
                throw new ArgumentException("A point name is 2-15 printable characters (spaces only between words)");
            if (points.Any(p => p != except && p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"A point named {name} already exists");
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
            // The packed word holds each cell in 13 bits (x in 6-18, z in 19-31).
            if (cx > 0x1FFF || cz > 0x1FFF) throw new ArgumentOutOfRangeException(nameof(x), $"cell ({cx}, {cz}) does not fit the point record (at most 8191)");
            var copy = (byte[])mps.Clone();
            uint packed = BitConverter.ToUInt32(copy, point.RecordOffset + 0x14);
            packed = (packed & 0x3Fu) | ((uint)cx << 6) | ((uint)cz << 19);
            BitConverter.GetBytes(packed).CopyTo(copy, point.RecordOffset + 0x14);
            return copy;
        }

        /// <summary>
        /// Changes a point in place: a new name (2-15 printable characters, unique), team byte or kind (start,
        /// waypoint or other). Records keep their size and order (<see cref="Add"/> and
        /// <see cref="Remove"/> change the table).
        /// </summary>
        public static byte[] Update(byte[] mps, string name, string? newName, int? team, string? kind)
        {
            var points = Parse(mps);
            var point = points.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? throw new KeyNotFoundException($"No point named {name}");
            var copy = (byte[])mps.Clone();
            if (newName != null)
            {
                CheckName(newName, points, point);
                Array.Clear(copy, point.RecordOffset, 16);
                Encoding.ASCII.GetBytes(newName).CopyTo(copy, point.RecordOffset);
            }
            if (team is int t)
            {
                if (t is < 0 or > 255) throw new ArgumentOutOfRangeException(nameof(team), "team is a byte (0-255)");
                copy[point.RecordOffset + 0x10] = (byte)t;
            }
            if (kind != null) copy[point.RecordOffset + 0x11] = KindByte(kind);
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
                // Retail names hold spaces ("Demo Site 1", "Sniper Rifle" in M51), never at the start.
                if (c < 0x20 || c > 0x7E || (c == 0x20 && sb.Length == 0)) return null;
                sb.Append((char)c);
            }
            return sb.Length >= 2 && sb[^1] != ' ' ? sb.ToString() : null;
        }
    }
}
