using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace CodeDesigner.Ps2.FileFormats.Zipper.Ca;

/// <summary>Prepares same-size resource replacements. The caller applies the whole edit as one undo step.</summary>
public static class CaMissionEdits
{
    private static CaPackage.Resource Require(CaPackage package, string missionId, string tag) =>
        package.MissionResource(missionId, tag) ?? throw new InvalidDataException($"Mission {missionId} has no {tag} resource");

    private static bool SameName(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);

    private static (CaMissions.Zone Zone, CaMissions.Point Point) FindPoint(CaMissions.Mission mission, string name)
    {
        var matches = (from zone in mission.Zones from p in zone.Points where SameName(p.Name, name) select (zone, p)).Take(2).ToList();
        return matches.Count switch
        {
            0 => throw new KeyNotFoundException($"No point named {name} in the selected mission"),
            1 => matches[0],
            _ => throw new ArgumentException($"Several points are named {name} in this mission"),
        };
    }

    public static Dictionary<CaPackage.Resource, byte[]> Point(CaPackage package, string missionId, string name, string? newName, float[]? position, List<string>? warnings = null)
    {
        if (newName == null && position == null) throw new ArgumentException("Nothing to change: pass newName or position");
        var resource = Require(package, missionId, "AIMm");
        var mission = CaMissions.ParseMission(resource.Data);
        var (zone, point) = FindPoint(mission, name);
        var bytes = (byte[])resource.Data.Clone();
        var changes = new Dictionary<CaPackage.Resource, byte[]> { [resource] = bytes };
        if (position != null)
        {
            if (position.Length != 3 || !position.All(float.IsFinite))
                throw new ArgumentException("position must contain three finite coordinates");
            if (zone.Index != point.Zone)
                throw new InvalidDataException($"{point.Name} has a packed zone that differs from its point vector");
            var grids = CaMissions.ZoneGrids(mission, package.Resources);
            if (!grids.TryGetValue(zone.Index, out var own)) throw new InvalidOperationException("Open the location archive with this point's island AI grid to move it");
            if (!TryCell(own.Grid, position[0], position[2], out int x, out int z))
            {
                if (grids.Values.Any(g => TryCell(g.Grid, position[0], position[2], out _, out _)))
                    throw new ArgumentException($"Moving a point to another zone would resize its point vector. Keep it within zone {zone.Index}.");
                throw new ArgumentOutOfRangeException(nameof(position), "The position is outside the mission's AI grids");
            }
            uint packed = (uint)zone.Index | (uint)x << 6 | (uint)z << 19;
            uint oldCell = BitConverter.ToUInt32(resource.Data, point.RecordOffset);
            if (oldCell != packed && package.FindMission(missionId)?.Kind is "SP" or "IA")
                MoveSquadSlot(bytes, mission, point, oldCell, packed, warnings);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(point.RecordOffset), packed);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(point.RecordOffset + 4), position[1]);
        }
        if (newName != null && newName != point.Name)
        {
            if (newName.Length is < 2 or > 15 || newName.Any(c => c is < ' ' or > '~') || newName[0] == ' ' || newName[^1] == ' ')
                throw new ArgumentException("A point name is 2–15 printable ASCII characters, with spaces only between words");
            if (mission.Zones.SelectMany(z => z.Points).Any(p => p.RecordOffset != point.RecordOffset && SameName(p.Name, newName)))
                throw new ArgumentException($"A point named {newName} already exists in this mission");
            WriteName(bytes, point.RecordOffset + 8, newName);
            if (package.MissionResource(missionId, "MSNv") is { } actors)
            {
                var starts = CaMissionTables.ParseActors(actors.Data, package.IsPsp).SelectMany(a => a.Starts).Where(s => SameName(s.Point, point.Name)).ToList();
                if (starts.Count > 0)
                {
                    var actorBytes = (byte[])actors.Data.Clone();
                    foreach (var start in starts) WriteName(actorBytes, start.RecordOffset, newName);
                    changes.Add(actors, actorBytes);
                }
            }
        }
        return changes;
    }

    private static void MoveSquadSlot(byte[] data, CaMissions.Mission mission, CaMissions.Point point, uint from, uint to, List<string>? warnings)
    {
        var slots = CaMissions.SpawnSlots(data, mission);
        var matches = slots.Where(s => s.Cell == from).ToList();
        if (matches.Count == 0) return;
        bool standardSquad = slots.Count == 4 && slots.All(s => (s.Flags & 0xFFFFF8) == 0)
            && slots.Select(s => s.Flags >> 24).Order().SequenceEqual(new uint[] { 0, 1, 2, 3 });
        bool sharedPoint = mission.Zones[point.Zone].Points.Any(p => p.RecordOffset != point.RecordOffset &&
            p.CellX == point.CellX && p.CellZ == point.CellZ);
        if (standardSquad && matches.Count == 1 && !sharedPoint)
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(matches[0].Offset), to);
        else
            warnings?.Add($"A spawn slot also uses {point.Name}'s original cell. Its binding is ambiguous, so it stays there. Check the squad spawn in game.");
    }

    private static bool TryCell(CaMissions.Grid grid, float x, float z, out int cellX, out int cellZ)
    {
        cellX = (int)Math.Floor(((double)x - grid.MinX) / grid.CellX);
        cellZ = (int)Math.Floor(((double)z - grid.MinZ) / grid.CellZ);
        return grid.CellX > 0 && grid.CellZ > 0 && cellX is >= 0 and <= 0x1FFF && cellZ is >= 0 and <= 0x1FFF
            && cellX < grid.Width && cellZ < grid.Height;
    }

    // CA uses NUL-terminated ASCII. Preserve padding beyond the new terminator.
    private static void WriteName(byte[] bytes, int offset, string name)
    {
        int length = Encoding.ASCII.GetBytes(name, bytes.AsSpan(offset));
        bytes[offset + length] = 0;
    }

    public static Dictionary<CaPackage.Resource, byte[]> Table(CaPackage package, string missionId, string table, JsonElement path, JsonElement values)
    {
        if (path.ValueKind != JsonValueKind.Array || values.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("mapTable requires path and values arrays");
        bool Field(int i, string expected) => path[i].ValueKind == JsonValueKind.String && SameName(path[i].GetString()!, expected);
        static int Index(JsonElement e, int count, string label)
        {
            if (e.ValueKind != JsonValueKind.Number || !e.TryGetInt32(out int n) || n < 0 || n >= count)
                throw new ArgumentException($"{label} must be an integer in the range 0–{count - 1}");
            return n;
        }

        CaPackage.Resource resource;
        byte[] bytes;
        if (package.IsPsp && !(SameName(table, "vehicles.rdr") && path.GetArrayLength() == 3 && Field(1, "starts")))
            throw new NotSupportedException("Fireteam Bravo 2 table edits support actor starts only: its mission header is not read yet.");
        if (SameName(table, "vehicles.rdr") && path.GetArrayLength() == 3 && Field(1, "starts"))
        {
            if (values.GetArrayLength() != 2 || values[0].ValueKind != JsonValueKind.String)
                throw new ArgumentException("An actor start requires [point name, heading]");
            int heading = Index(values[1], 8, "Heading");
            var mission = CaMissions.ParseMission(Require(package, missionId, "AIMm").Data);
            var (_, point) = FindPoint(mission, values[0].GetString()!);
            resource = Require(package, missionId, "MSNv");
            var actors = CaMissionTables.ParseActors(resource.Data, package.IsPsp);
            var actor = actors[Index(path[0], actors.Count, "Actor row")];
            var start = actor.Starts[Index(path[2], actor.Starts.Count, "Start index")];
            bytes = (byte[])resource.Data.Clone();
            WriteName(bytes, start.RecordOffset, point.Name);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(start.RecordOffset + 28), (uint)heading);
        }
        else if (SameName(table, "mission.rdr") && path.GetArrayLength() == 3 && Field(0, "Valves") && Field(2, "VALUE"))
        {
            if (values.GetArrayLength() != 1) throw new ArgumentException("A valve requires one start value");
            int value = Index(values[0], 0x10000, "Valve value");
            var header = CaMissionTables.ParseHeader(Require(package, missionId, "MISN").Data);
            uint hash = header.Valves[Index(path[1], header.Valves.Count, "Valve index")];
            resource = Require(package, missionId, "VALV");
            var valves = CaMissionTables.ParseValves(resource.Data);
            int row = valves.FindIndex(v => v.Hash == hash);
            if (row < 0 || valves.FindLastIndex(v => v.Hash == hash) != row)
                throw new InvalidDataException($"Expected one VALV start value for v{hash:x8}");
            bytes = (byte[])resource.Data.Clone();
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(row * 8 + 4), (ushort)value);
        }
        else if (SameName(table, "mission.rdr") && path.GetArrayLength() == 2 && Field(0, "Weapons"))
        {
            if (values.GetArrayLength() != 1 || values[0].ValueKind != JsonValueKind.Number || !values[0].TryGetUInt32(out uint hash) || hash == 0)
                throw new ArgumentException("A weapon slot requires one nonzero unsigned 32-bit weapon or item hash");
            resource = Require(package, missionId, "MISN");
            var header = CaMissionTables.ParseHeader(resource.Data);
            int index = Index(path[1], header.Weapons.Count, "Weapon slot");
            if (!KnownWeapon(package, hash)) throw new ArgumentException($"Weapon or item {hash:x8} is unknown to this package and its shared weapon table");
            int listOffset = BitConverter.ToInt32(resource.Data, 0x34);
            bytes = (byte[])resource.Data.Clone();
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(listOffset + index * 4), hash);
        }
        else throw new ArgumentException("Editable Combined Assault fields are actor starts, valve start values and existing allowed-weapon slots");
        return new Dictionary<CaPackage.Resource, byte[]> { [resource] = bytes };
    }

    private static bool KnownWeapon(CaPackage package, uint hash) =>
        package.Resources.Where(r => r.Tag == "MISN").Any(r => CaMissionTables.ParseHeader(r.Data).Weapons.Contains(hash))
        || package.Resources.Where(r => r.Tag == "CHAR").Any(r => CaMissionTables.ParseCharacterTypes(r.Data).Any(t => t.Weapons.Contains(hash)))
        || (package.GlobalResource("WEAP") is { } table && CaMissionTables.FindWeapons(table.Data, new[] { hash }).ContainsKey(hash));
}
