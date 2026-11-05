namespace CodeDesigner.Ps2.FileFormats.Zipper.Ca
{
    /// <summary>
    /// CA mission AI data: a mission's <c>mission.rdr</c> (<c>AIMm</c>: zones and their named points) and each
    /// island's AI grids (<c>AIMi</c>, <c>&lt;island&gt;.src</c>). Both are serialized C++ objects whose vectors are
    /// stored as (begin, end, capacity) byte offsets into the resource. Layouts are in docs/formats/ca-missions.md;
    /// checked on every mission on the retail disc (7,217 points: each lands inside its zone's bounds but one).
    /// </summary>
    public static class CaMissions
    {
        /// <summary>One AI grid: bounds, size in cells and cell size (10 units on every retail grid).</summary>
        public sealed record Grid(string Name, float MinX, float MinY, float MinZ, float MaxX, float MaxY, float MaxZ, int Width, int Height, float CellX, float CellZ);

        /// <summary>An island of the mission: CRC32 of &lt;island&gt;.src and the range of zones it owns.</summary>
        public sealed record Island(uint Hash, int FirstZone, int ZoneCount);

        /// <summary>
        /// A named point. <see cref="Marker"/> is set on the points the map screen names (navigation letters, objectives,
        /// extraction); <see cref="Y"/> is the point's own height; the cell is on its zone's grid.
        /// </summary>
        public sealed record Point(string Name, int Zone, int CellX, int CellZ, float Y, bool Marker, int RecordOffset);

        /// <summary>A zone: its bounds (the same as its grid's) and its points.</summary>
        public sealed record Zone(int Index, float[] Min, float[] Max, List<Point> Points);

        public sealed record Mission(List<Zone> Zones, List<Island> Islands);

        /// <summary>Byte size of a point record: packed cell, height, name[16], flags.</summary>
        public const int PointSize = 28;

        private static uint U32(byte[] d, int o) => o >= 0 && o <= d.Length - 4 ? BitConverter.ToUInt32(d, o) : throw new InvalidDataException($"Offset 0x{o:X} is outside the resource");

        /// <summary>A serialized vector at <paramref name="at"/>: (begin, end) checked against the data and the element size.</summary>
        private static (int Begin, int End) Vector(byte[] d, int at, int size)
        {
            uint b = U32(d, at), e = U32(d, at + 4);
            if (b > e || e > d.Length || (e - b) % size != 0) throw new InvalidDataException($"Bad vector at 0x{at:X}");
            return ((int)b, (int)e);
        }

        // Minimal point-only zones written by other tools may end before the later retail vectors.
        private static (int Begin, int End) ZoneVector(byte[] data, int zoneIndex, int field, int stride)
        {
            int root = (int)U32(data, 0), zone = (int)U32(data, root + 0x14 + zoneIndex * 4);
            uint pointBegin = U32(data, zone + 0x20);
            long end = (long)zone + field + 16;
            if (end > data.Length || (pointBegin > 0 && pointBegin < end)) return (0, 0);
            if (zoneIndex + 1 < U32(data, root))
            {
                uint next = U32(data, root + 0x14 + (zoneIndex + 1) * 4);
                if (next > zone && next < end) return (0, 0);
            }
            return Vector(data, zone + field, stride);
        }

        /// <summary>
        /// A mission.rdr: header +0x28 the zone bounds (32 bytes: pointer, 0, min xyz, max xyz), +0x38 the islands
        /// (24 bytes: hash, first zone, zone count, 0 × 3); word 0 the root: zone count, a vector, then one pointer per
        /// zone, whose +0x20 is its point vector.
        /// </summary>
        public static Mission ParseMission(byte[] data)
        {
            if (data.Length < 0x48) throw new InvalidDataException("mission.rdr is too small");
            var (bb, be) = Vector(data, 0x28, 32);
            var (ib, ie) = Vector(data, 0x38, 24);
            var islands = new List<Island>();
            for (int o = ib; o < ie; o += 24)
                islands.Add(new Island(U32(data, o), (int)U32(data, o + 4), (int)U32(data, o + 8)));
            int root = (int)U32(data, 0);
            int count = (int)U32(data, root);
            if (count != (be - bb) / 32) throw new InvalidDataException($"mission.rdr lists {count} zones but {(be - bb) / 32} zone bounds");
            var zones = new List<Zone>(count);
            for (int i = 0; i < count; i++)
            {
                int box = bb + i * 32;
                var min = new[] { F(data, box + 8), F(data, box + 12), F(data, box + 16) };
                var max = new[] { F(data, box + 20), F(data, box + 24), F(data, box + 28) };
                int zone = (int)U32(data, root + 0x14 + i * 4);
                var (pb, pe) = Vector(data, zone + 0x20, PointSize);
                var points = new List<Point>();
                for (int o = pb; o < pe; o += PointSize)
                {
                    uint cell = U32(data, o);
                    // Some MP321 and MP325 zones contain one unnamed slot among the named points.
                    if (data[o + 8] == 0) continue;
                    string name = Name(data, o + 8, 16) ?? throw new InvalidDataException($"Point at 0x{o:X} has an invalid name");
                    // The flags word's second byte is 1 on map markers; the rest of the word is uninitialised padding.
                    points.Add(new Point(name, (int)(cell & 0x3F), (int)((cell >> 6) & 0x1FFF), (int)(cell >> 19), F(data, o + 4), data[o + 25] == 1, o));
                }
                zones.Add(new Zone(i, min, max, points));
            }
            return new Mission(zones, islands);
        }

        /// <summary>
        /// An island's AIMi: section count, island hash, location hash, then the section offsets; each section is one
        /// zone's grid (in the island's zone order) and starts with min xyz, max xyz, width, height, cell x, cell z,
        /// 1/cell x, 1/cell z, a word, then the name (char[32]). What follows the header (cells) is not decoded.
        /// </summary>
        public static (uint Island, uint Location, List<Grid> Grids) ParseIslandAi(byte[] data)
        {
            int n = (int)U32(data, 0);
            if (n is < 1 or > 64) throw new InvalidDataException($"AIMi with {n} sections");
            var grids = new List<Grid>(n);
            for (int i = 0; i < n; i++)
            {
                int o = (int)U32(data, 12 + i * 4);
                if (o < 0 || o > data.Length - 0x54) throw new InvalidDataException($"AIMi section {i} is outside the resource");
                grids.Add(new Grid(Name(data, o + 0x34, 32) ?? "",
                    F(data, o), F(data, o + 4), F(data, o + 8), F(data, o + 12), F(data, o + 16), F(data, o + 20),
                    (int)U32(data, o + 24), (int)U32(data, o + 28), F(data, o + 32), F(data, o + 36)));
            }
            return (U32(data, 4), U32(data, 8), grids);
        }

        /// <summary>A point's world X / Z: the centre of its cell on <paramref name="grid"/>, else on its zone's bounds
        /// with the retail 10-unit cell.</summary>
        public static (float X, float Z) Position(Point p, Zone zone, Grid? grid)
        {
            float minX = grid?.MinX ?? zone.Min[0], minZ = grid?.MinZ ?? zone.Min[2];
            float cx = grid?.CellX ?? 10f, cz = grid?.CellZ ?? 10f;
            return (minX + (p.CellX + 0.5f) * cx, minZ + (p.CellZ + 0.5f) * cz);
        }

        public sealed record ObjectiveRegion(string Name, bool Secondary, int Zone, float X, float Z);

        /// <summary>World centres of explicitly named pObj_ and sObj_ regions. A zone's +0x50 vector holds
        /// 32-byte rectangular regions: minimum packed cell, width and depth in cells, two words and name[16].</summary>
        public static List<ObjectiveRegion> ObjectiveRegions(CaPackage package, string missionId)
        {
            var data = package.MissionResource(missionId, "AIMm")?.Data ?? throw new InvalidDataException($"Mission {missionId} has no point resource");
            var mission = ParseMission(data);
            var grids = ZoneGrids(mission, package.Resources);
            var result = new List<ObjectiveRegion>();
            foreach (var zone in mission.Zones)
            {
                var (begin, end) = ZoneVector(data, zone.Index, 0x50, 32);
                var grid = grids.GetValueOrDefault(zone.Index).Grid;
                for (int at = begin; at < end; at += 32)
                {
                    string? name = Name(data, at + 16, 16);
                    if (name == null) continue;
                    bool secondary = name.StartsWith("sObj_", StringComparison.OrdinalIgnoreCase);
                    if (!secondary && !name.StartsWith("pObj_", StringComparison.OrdinalIgnoreCase)) continue;
                    uint cell = U32(data, at), size = U32(data, at + 4);
                    float x = (grid?.MinX ?? zone.Min[0]) + (((cell >> 6) & 0x1FFF) + (size & 0xFFFF) * 0.5f) * (grid?.CellX ?? 10);
                    float z = (grid?.MinZ ?? zone.Min[2]) + ((cell >> 19) + (size >> 16) * 0.5f) * (grid?.CellZ ?? 10);
                    result.Add(new ObjectiveRegion(name, secondary, zone.Index, x, z));
                }
            }
            return result;
        }

        internal static List<(int Offset, uint Cell, uint Flags)> SpawnSlots(byte[] data, Mission mission)
        {
            var slots = new List<(int, uint, uint)>();
            foreach (var zone in mission.Zones)
            {
                var (begin, end) = ZoneVector(data, zone.Index, 0x64, 12);
                for (int at = begin; at < end; at += 12)
                    slots.Add((at, U32(data, at), U32(data, at + 4)));
            }
            return slots;
        }

        public sealed record TeamSpawn(string Name, string Faction, int Team, int Zone, string Island, int CellX, int CellZ, float X, float Z);

        /// <summary>Read-only default demolition team starts. Verify the slot layout and every faction assignment
        /// against both named bases before displaying the set. Other modes and alternate arrangements are unresolved.</summary>
        public static List<TeamSpawn> TeamSpawns(CaPackage package, string missionId, IReadOnlyList<CaPackage.MissionPoint> namedPoints)
        {
            if (package.FindMission(missionId) is not { Kind: "MP", Mode: "DEM" }) return new();
            var seals = namedPoints.Where(p => p.Name.Equals("seal_tent", StringComparison.OrdinalIgnoreCase)).ToList();
            var terrorists = namedPoints.Where(p => p.Name.Equals("terrorist_tent", StringComparison.OrdinalIgnoreCase)).ToList();
            if (seals.Count != 1 || terrorists.Count != 1) return new();

            byte[] data = package.MissionResource(missionId, "AIMm")!.Data;
            var mission = ParseMission(data);
            var grids = ZoneGrids(mission, package.Resources);
            var slots = SpawnSlots(data, mission).Where(s => ((s.Flags >> 16) & 255) == 0).ToList();
            // Each of the four groups has ordinal 0–35 exactly once. Bit 5 selects the faction.
            if (slots.Count != 144 || slots.Any(s => (s.Flags & 0x00FFFFC8) != 0 || s.Flags >> 24 >= 36)
                || slots.DistinctBy(s => (s.Flags >> 24, (s.Flags >> 4) & 3)).Count() != slots.Count) return new();
            var spawns = new List<TeamSpawn>(slots.Count);
            foreach (var slot in slots.OrderBy(s => s.Flags & 0x30).ThenBy(s => s.Flags >> 24))
            {
                int zone = (int)(slot.Cell & 63), cellX = (int)((slot.Cell >> 6) & 0x1FFF), cellZ = (int)(slot.Cell >> 19);
                if (!grids.TryGetValue(zone, out var g) || cellX >= g.Grid.Width || cellZ >= g.Grid.Height) return new();
                float x = g.Grid.MinX + (cellX + 0.5f) * g.Grid.CellX, z = g.Grid.MinZ + (cellZ + 0.5f) * g.Grid.CellZ;
                bool seal = (slot.Flags & 0x20) == 0;
                double Distance(CaPackage.MissionPoint tent) => Math.Pow(x - tent.X, 2) + Math.Pow(z - tent.Z, 2);
                if (!(seal ? Distance(seals[0]) < Distance(terrorists[0]) : Distance(terrorists[0]) < Distance(seals[0]))) return new();
                string faction = seal ? "SEAL" : "Terrorist";
                // The display label is longer than a named point's 16-byte field and cannot collide with a real name.
                string name = $"{faction} team spawn {1 + ((slot.Flags >> 4) & 1)}.{1 + (slot.Flags >> 24):D2}";
                spawns.Add(new(name, faction, seal ? 1 : 2, zone, g.Island, cellX, cellZ, x, z));
            }
            return spawns;
        }

        internal static Dictionary<int, (string Island, Grid Grid)> ZoneGrids(Mission mission, IEnumerable<CaPackage.Resource> resources)
        {
            var grids = new Dictionary<int, (string, Grid)>();
            foreach (var resource in resources.Where(r => r.Tag == "AIMi"))
            {
                (uint Island, uint Location, List<Grid> Grids) ai;
                try { ai = ParseIslandAi(resource.Data); }
                catch (InvalidDataException) { continue; }
                string name = Path.GetFileNameWithoutExtension(resource.Name);
                foreach (var island in mission.Islands.Where(i => i.Hash == ai.Island))
                    for (int i = 0; i < Math.Min(island.ZoneCount, ai.Grids.Count); i++)
                        grids.TryAdd(island.FirstZone + i, (name, ai.Grids[i]));
            }
            return grids;
        }

        private static float F(byte[] d, int o) => BitConverter.ToSingle(d, o);

        private static string? Name(byte[] d, int o, int max)
        {
            int end = o;
            while (end < o + max && end < d.Length && d[end] != 0) end++;
            if (end == o) return null;
            for (int i = o; i < end; i++) if (d[i] is < 0x20 or > 0x7E) return null;
            return System.Text.Encoding.ASCII.GetString(d, o, end - o);
        }
    }
}
