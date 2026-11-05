using System.Text;
using System.Text.RegularExpressions;

namespace CodeDesigner.Ps2.FileFormats.Zipper.Ca
{
    /// <summary>
    /// A CA mission's tables, the counterpart of SOCOM II's READERM.ZAR: the actors (<c>vehicles.rdr</c>, tag
    /// <c>MSNv</c>), their character types (<c>character.rdr</c>, <c>CHAR</c>: model, display name, weapons), the mission
    /// header (<c>MISN</c>: map image, title and briefing keys, allowed weapons, valves), the valves' start values
    /// (<c>mission.rdr</c>, <c>VALV</c>) and the shared weapon table (<c>GLOBAL.ZDB › weapons/wep_weapon.rdr</c>,
    /// <c>WEAP</c>). Names are compiled out (CRC32 hashes) except actor and point names, display names and message keys.
    /// Layouts: docs/formats/ca-missions.md; observed on every retail mission, none written or tried in game.
    /// </summary>
    public static class CaMissionTables
    {
        /// <summary>One start of an actor: an AIMAPS point (mission.rdr) and its heading, 0–7 eighths of a turn clockwise
        /// from north, as SOCOM II's <c>setup</c>.</summary>
        public sealed record Start(string Point, int Heading, int RecordOffset);

        /// <summary>
        /// An actor: name, display message key, its type (a character type of the mission's character.rdr, or a vehicle
        /// or turret of GLOBAL's ai_vehicles / ai_turrets), its object kind at +0x54 and its starts.
        /// Online missions leave the start empty (players spawn at the game's team spawns).
        /// </summary>
        public sealed record Actor(int Row, string Name, string? Display, uint Type, int Group, List<Start> Starts)
        {
            public string Kind => Group switch { 1 => "character", 2 => "vehicle", 3 => "turret", _ => "other" };
        }

        /// <summary>A character type: hash, its high- and low-detail <c>clib</c> models (root hashes), its display name
        /// (the faction, "DEFENSE FORCE", or the squad mate, "BULLFROG") and its weapons (weapon table hashes).</summary>
        public sealed record CharacterType(uint Hash, uint Model, uint LowModel, string? Display, List<uint> Weapons);

        /// <summary>A weapon table entry: <c>weapon</c> (guns and grenades: <see cref="Model"/> is the GLOBAL weapon model's
        /// root hash) or <c>item</c> (attachments and gear), its display name ("ASSAULT RIFLE: M4A1"; grenades have none)
        /// and its description key.</summary>
        public sealed record Weapon(uint Hash, string Kind, string? Display, string? Key, uint? Model);

        /// <summary>A mission header: its strings (map image, message keys, loading textures) and the hashes it lists.</summary>
        public sealed record Header(List<string> Strings, List<uint> CharacterTypes, List<uint> Weapons, List<uint> Valves);

        private const int ActorSize = 0x1EC;

        private static uint U32(byte[] d, int o) => o >= 0 && o + 4 <= d.Length ? BitConverter.ToUInt32(d, o) : throw new InvalidDataException($"Offset 0x{o:X} is outside the table");

        private static string? Str(byte[] d, int o, int max)
        {
            int end = o;
            while (end < d.Length && end < o + max && d[end] != 0) end++;
            if (end == o || end > d.Length) return null;
            for (int i = o; i < end; i++) if (d[i] is < 0x20 or > 0x7E) return null;
            return Encoding.ASCII.GetString(d, o, end - o);
        }

        private static List<uint> U32s(byte[] d, int at, int count)
        {
            if (count < 0 || at < 0 || at + count * 4L > d.Length) throw new InvalidDataException($"List at 0x{at:X} is outside the table");
            var list = new List<uint>(count);
            for (int i = 0; i < count; i++) list.Add(BitConverter.ToUInt32(d, at + i * 4));
            return list;
        }

        /// <summary>
        /// vehicles.rdr: +0x00 / +0x04 the actor records' begin and end, 0x1EC bytes each: name char[32], display key
        /// char[32], type hash (+0x40), group (+0x54), start count (+0x58), starts from +0x5C (32 bytes: point char[28],
        /// heading u32).
        /// </summary>
        /// <remarks>Fireteam Bravo 2 (<paramref name="psp"/>): 0x1BC-byte records without the display key: character type (a character.rdr record hash; +0x20 is another hash)
        /// +0x24, group +0x30, start count +0x34, starts from +0x38. Its player actor leaves the start's name empty
        /// (a position follows instead), which is not read.</remarks>
        public static List<Actor> ParseActors(byte[] data, bool psp = false)
        {
            int size = psp ? PspActorSize : ActorSize;
            int hash = psp ? 0x24 : 0x40, group = psp ? 0x30 : 0x54, count = psp ? 0x34 : 0x58, first = psp ? 0x38 : 0x5C;
            int b = (int)U32(data, 0), e = (int)U32(data, 4);
            if (b < 8 || e > data.Length || e < b || (e - b) % size != 0) throw new InvalidDataException("vehicles.rdr: bad actor table");
            var list = new List<Actor>();
            for (int o = b, row = 0; o < e; o += size, row++)
            {
                int n = (int)Math.Min(U32(data, o + count), 8);
                var starts = new List<Start>();
                for (int i = 0; i < n; i++)
                {
                    int offset = o + first + i * 32;
                    if (Str(data, offset, 28) is { } point) starts.Add(new Start(point, (int)U32(data, offset + 28), offset));
                }
                list.Add(new Actor(row, Str(data, o, 32) ?? $"actor{row}", psp ? null : Str(data, o + 0x20, 32), U32(data, o + hash), (int)U32(data, o + group), starts));
            }
            return list;
        }

        private const int PspActorSize = 0x1BC;

        /// <summary>
        /// character.rdr: +0x08 / +0x0C a list of record offsets. A record: hash, model (+0x04), low-detail model
        /// (+0x1F4), seven vectors from +0x1FC (20 bytes each: a stale pointer, then begin, end, capacity, size; the
        /// third is the weapons) and the display name at +0x404 (char[32]). Records for vehicles and turrets have no
        /// model.
        /// </summary>
        public static List<CharacterType> ParseCharacterTypes(byte[] data)
        {
            int lb = (int)U32(data, 8), le = (int)U32(data, 12);
            if (le < lb || (le - lb) % 4 != 0) throw new InvalidDataException("character.rdr: bad record list");
            var list = new List<CharacterType>();
            foreach (uint off in U32s(data, lb, (le - lb) / 4))
            {
                int o = (int)off;
                if (o + 0x424 > data.Length) throw new InvalidDataException($"character.rdr: record at 0x{o:X} is outside the table");
                int vb = (int)U32(data, o + 0x224), ve = (int)U32(data, o + 0x228);
                var weapons = ve > vb && ve <= data.Length && (ve - vb) % 4 == 0 ? U32s(data, vb, (ve - vb) / 4) : new List<uint>();
                list.Add(new CharacterType(U32(data, o), U32(data, o + 4), U32(data, o + 0x1F4), Str(data, o + 0x404, 32), weapons));
            }
            return list;
        }

        /// <summary>
        /// Fireteam Bravo 2's character.rdr: +0x24 / +0x28 a list of record offsets. A record (0x4D4 bytes): hash (what an
        /// actor's +0x24 names), the <c>clib</c> model's root hash (+0x04), and from +0x2C0 vectors of 20 bytes as in CA
        /// (a stale pointer, then begin, end, capacity, size); the one at +0x2FC (begin +0x300) is the loadout, the
        /// weapon table hashes of its slots. No display name is stored.
        /// </summary>
        public static List<CharacterType> ParsePspCharacterTypes(byte[] data)
        {
            int lb = (int)U32(data, 0x24), le = (int)U32(data, 0x28);
            if (le < lb || (le - lb) % 4 != 0) throw new InvalidDataException("character.rdr: bad record list");
            var list = new List<CharacterType>();
            foreach (uint off in U32s(data, lb, (le - lb) / 4))
            {
                int o = (int)off;
                if (o + 0x308 > data.Length) throw new InvalidDataException($"character.rdr: record at 0x{o:X} is outside the table");
                int vb = (int)U32(data, o + 0x300), ve = (int)U32(data, o + 0x304);
                var weapons = ve > vb && ve <= data.Length && (ve - vb) % 4 == 0 && ve - vb <= 64 ? U32s(data, vb, (ve - vb) / 4) : new List<uint>();
                list.Add(new CharacterType(U32(data, o), U32(data, o + 4), 0, null, weapons));
            }
            return list;
        }

        private static readonly Regex Loct = new(@"_LOCT\(""([^""]{1,60})""\s*(_\d+_\w+?_MSG)", RegexOptions.Compiled);

        /// <summary>
        /// Fireteam Bravo 2's weapon table entries for <paramref name="hashes"/> (CRC32 of the weapon's name, e.g.
        /// <c>M4A1</c>). An entry starts with its hash and, at +0x04, the offset of its strings, whose first (+0x0C) is the
        /// UI message key. The display name is the text the table's leftover source gives that key
        /// (<c>_LOCT("SNIPER RIFLE: M40A1" _30659_…)</c>) when it is there, else the model's name when it is known
        /// (<c>M4A1</c>). <paramref name="models"/> maps weapon model root hashes to names: an entry that holds one in its
        /// first 0x80 bytes uses that model (guns, grenades and attachments all have one). The key's category says
        /// whether it is a gun (<c>weapon</c>) or an <c>item</c>.
        /// </summary>
        public static Dictionary<uint, Weapon> FindPspWeapons(byte[] table, IEnumerable<uint> hashes, IReadOnlyDictionary<uint, string> models)
        {
            var want = hashes.Where(h => h != 0).ToHashSet();
            var display = new Dictionary<string, string>();
            foreach (Match m in Loct.Matches(Encoding.Latin1.GetString(table))) display.TryAdd(m.Groups[2].Value, m.Groups[1].Value);
            var found = new Dictionary<uint, Weapon>();
            for (int o = 0; o + 0x80 <= table.Length && found.Count < want.Count; o += 4)
            {
                uint h = BitConverter.ToUInt32(table, o);
                if (!want.Contains(h) || found.ContainsKey(h)) continue;
                int strings = (int)BitConverter.ToUInt32(table, o + 4);
                if (strings <= 0 || strings >= o || Str(table, strings + 0x0C, 64) is not { } key || !MessageKey.IsMatch(key)) continue;
                uint? model = null;
                for (int k = 8; k < 0x80 && model == null; k += 4)
                    if (models.ContainsKey(BitConverter.ToUInt32(table, o + k))) model = BitConverter.ToUInt32(table, o + k);
                string? name = display.GetValueOrDefault(key)
                    ?? (model is uint mh && !Regex.IsMatch(models[mh], "^w[0-9a-f]{8}$") ? models[mh].ToUpperInvariant() : null);
                found[h] = new Weapon(h, PspGun.IsMatch(key) ? "weapon" : "item", name, key, model);
            }
            return found;
        }

        private static readonly Regex PspGun = new(@"^_\d+_(ASSAULTRIFLE|SNIPERRIFLE|SMG|SHOTGUN|MACHINEGUN|PISTOL|LAUNCHER)", RegexOptions.Compiled);

        private static readonly Regex MessageKey = new(@"^_\d+_\w*_MSG$", RegexOptions.Compiled);

        private static bool IsKey(byte[] d, int o) => o + 8 <= d.Length && Str(d, o, 48) is { } s && MessageKey.IsMatch(s);

        /// <summary>
        /// The weapon table entries for <paramref name="hashes"/>. Entries vary in length and are found by their hash:
        /// a gun or grenade has its description key at +0x64, display name at +0x44 and model hash at +0x0C; an
        /// attachment or gear item its key at +0x5C and display at +0x3C (or, without a display name, its key at +0x3C).
        /// </summary>
        public static Dictionary<uint, Weapon> FindWeapons(byte[] table, IEnumerable<uint> hashes)
        {
            var want = hashes.Where(h => h != 0).ToHashSet();
            var found = new Dictionary<uint, Weapon>();
            for (int o = 0; o + 0x70 <= table.Length && found.Count < want.Count; o += 4)
            {
                uint h = BitConverter.ToUInt32(table, o);
                if (!want.Contains(h) || found.ContainsKey(h)) continue;
                if (IsKey(table, o + 0x64)) found[h] = new Weapon(h, "weapon", Str(table, o + 0x44, 32), Str(table, o + 0x64, 48), U32(table, o + 0x0C));
                else if (IsKey(table, o + 0x5C)) found[h] = new Weapon(h, "item", Str(table, o + 0x3C, 32), Str(table, o + 0x5C, 48), null);
                else if (IsKey(table, o + 0x3C)) found[h] = new Weapon(h, "item", null, Str(table, o + 0x3C, 48), null);
            }
            return found;
        }

        /// <summary>
        /// A mission header (MISN): (count, offset) pairs at +0x10 character types (u32 each), +0x20 strings (hash and
        /// string offset), +0x30 weapons and items the mission allows, +0x4C valves.
        /// </summary>
        public static Header ParseHeader(byte[] data)
        {
            List<uint> Table(int at) => U32s(data, (int)U32(data, at + 4), (int)U32(data, at));
            int ns = (int)U32(data, 0x20), os = (int)U32(data, 0x24);
            if (ns < 0 || os + ns * 8L > data.Length) throw new InvalidDataException("MISN: bad string table");
            var strings = new List<string>();
            for (int i = 0; i < ns; i++)
                if (Str(data, (int)U32(data, os + i * 8 + 4), 260) is { } s) strings.Add(s);
            return new Header(strings, Table(0x10), Table(0x30), Table(0x4C));
        }

        /// <summary>The valves' start values (VALV mission.rdr): 8 bytes each, the valve hash and a word whose low 16
        /// bits are the value (the high half is 0x0301 on every retail record).</summary>
        public static List<(uint Hash, int Value)> ParseValves(byte[] data)
        {
            var list = new List<(uint, int)>();
            for (int o = 0; o + 8 <= data.Length; o += 8) list.Add((U32(data, o), (int)(U32(data, o + 4) & 0xFFFF)));
            return list;
        }

        /// <summary>The words of a message key as a stand-in for its text (the game's text is not on the disc as plain
        /// strings): <c>_14867_JESTER_HUD_MSG</c> → <c>JESTER</c>.</summary>
        public static string KeyText(string key)
        {
            var m = Regex.Match(key, @"^_\d+_(.*?)_(?:UI|HUD|OCN|[A-Z]+)_MSG$");
            return m.Success && m.Groups[1].Value.Length > 0 ? m.Groups[1].Value.Replace('_', ' ') : key;
        }
    }
}
