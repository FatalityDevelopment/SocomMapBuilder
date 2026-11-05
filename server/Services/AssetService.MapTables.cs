using System.Globalization;
using System.Text.Json;
using CodeDesigner.Ps2.FileFormats.Zipper.RDR;
using CodeDesigner.Ps2.FileFormats.Zipper.ZED;

namespace MapBuilder.Server.Services;

/// <summary>
/// The map's mission tables (READERM.ZAR) decoded for the Map Builder (ASSET_MAP_TABLES): HUD objectives with their
/// map locations, actors (vehicles.rdr) with their start points and AI settings, mission settings (mission.rdr: AI
/// parameters, satchel timer, weapon and state valves), world settings (the map-named rdr: lighting, fog, clip) and the
/// map extents (map.rdr), with in-game text from the map's language tables (*LOC.ZAR). Layout: docs/formats/rdr.md.
/// Edits: <c>mapTable</c> sets the values of one field in place (same count, same types, unshared first) and
/// <c>mapText</c> changes a message's text in the map's language tables.
/// </summary>
internal sealed partial class ZipperAssetService
{
    // chartype -> character model name (drawn as MESH_<name>), shipped as data/chartypes.tsv (copied from the bots
    // tooling's Mods/SOCOM2/bots/chartypes.tsv). Nothing in a map package itself says which model a chartype is.
    private static readonly Lazy<Dictionary<string, string>> CharTypes = new(() => ReadCharTypes(Path.Combine(AppContext.BaseDirectory, "data", "chartypes.tsv")));

    internal static Dictionary<string, string> ReadCharTypes(string path)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return map;
        foreach (string line in File.ReadLines(path))
        {
            if (line.StartsWith('#')) continue;
            string[] cols = line.Split('\t');
            if (cols.Length >= 2 && cols[0].Trim().Length > 0 && cols[1].Trim().Length > 0) map[cols[0].Trim()] = cols[1].Trim();
        }
        return map;
    }

    /// <summary>Every model record of the package's *_MDL.ZED archives, by name (case-insensitive) as "archive:name".</summary>
    private static Dictionary<string, string> PackageModels(AssetSession s)
    {
        var models = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var leaf in s.Package.Entries.Select(e => e.Leaf).Where(l => l.EndsWith("_MDL.ZED", StringComparison.OrdinalIgnoreCase)))
        foreach (var node in s.Package.Container(leaf).Nodes)
            models.TryAdd(node.Name, leaf + ":" + node.Name);
        return models;
    }

    /// <summary>The package model ("archive:name") a chartype draws as: its mapped model (MESH_ first), else one named like it.</summary>
    internal static string? CharacterModel(IReadOnlyDictionary<string, string> models, string? chartype, IReadOnlyDictionary<string, string> types)
    {
        if (string.IsNullOrWhiteSpace(chartype)) return null;
        var names = new List<string>();
        if (types.TryGetValue(chartype, out var model)) names.AddRange(new[] { "MESH_" + model, model });
        names.AddRange(new[] { "MESH_" + chartype, chartype });
        foreach (string name in names) if (models.TryGetValue(name, out var hit)) return hit;
        return null;
    }

    private static ZedNode? ReaderRecord(AssetSession s, string name) =>
        s.Package.Entry(MissionReader) == null ? null
            : s.Package.Container(MissionReader).Nodes.FirstOrDefault(n => n.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    // (key, value atom) of an object in either form on disc (pair items, or key strings followed by values).
    private static IEnumerable<(string Key, int Value)> Pairs(RdrDocument doc, int list)
    {
        int[] items = doc.Children(list).ToArray();
        if (items.Length == 1 && doc.TypeOf(items[0]) == RdrDocument.List && doc.CountOf(items[0]) != 2) { foreach (var p in Pairs(doc, items[0])) yield return p; yield break; }
        for (int i = 0; i < items.Length; i++)
        {
            int item = items[i];
            if (doc.TypeOf(item) == RdrDocument.String && i + 1 < items.Length) { yield return ((string)doc.ValueOf(item)!, items[i + 1]); i++; }
            else if (doc.TypeOf(item) == RdrDocument.List && doc.CountOf(item) == 2 && doc.TypeOf(doc.FirstChild(item)) == RdrDocument.String)
                yield return ((string)doc.ValueOf(doc.FirstChild(item))!, doc.FirstChild(item) + 1);
        }
    }

    private static object?[]? Vals(RdrDocument doc, int list, string key) => doc.Field(list, key) is int f and >= 0 ? doc.Values(f) : null;
    private static string? Text1(RdrDocument doc, int list, string key) => Vals(doc, list, key)?.FirstOrDefault() as string;
    private static float? Num1(RdrDocument doc, int list, string key) => Vals(doc, list, key)?.FirstOrDefault() switch
    {
        int i => i, float f => f, _ => null,
    };
    private static float[]? Nums(RdrDocument doc, int list, string key) =>
        Vals(doc, list, key) is { } v && v.All(x => x is int or float) ? v.Select(x => x is int i ? i : (float)x!).ToArray() : null;

    // Every message the map's language tables hold (the first table that has a key wins).
    private static Dictionary<string, string> MapTexts(AssetSession s)
    {
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in s.Package.Entries.Where(e => e.Leaf.EndsWith("LOC.ZAR", StringComparison.OrdinalIgnoreCase)))
        foreach (var node in s.Package.Container(entry.Leaf).Nodes.Where(n => n.Name.EndsWith(".rdr", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var table = new LanguageTable(node.Data);
                foreach (var (key, list) in table.Entries())
                    if (!texts.ContainsKey(key) && table.Document.Values(list).FirstOrDefault() is string text) texts[key] = text;
            }
            catch (InvalidDataException) { }
        }
        return texts;
    }

    private static object MapTables(AssetSession s)
    {
        var texts = MapTexts(s);
        var used = new HashSet<string>(StringComparer.Ordinal);
        string? Use(string? key) { if (key != null && texts.ContainsKey(key)) used.Add(key); return key; }

        var objectives = new List<object>();
        if (ReaderRecord(s, "hudobjectives.rdr") is { } hud)
        {
            var doc = RdrDocument.Parse(hud.Data);
            int list = doc.Field(0, "HUD_OBJECTIVES");
            int i = 0;
            if (list >= 0)
                foreach (int row in doc.Children(list))
                {
                    var location = Nums(doc, row, "LOCATION");
                    objectives.Add(new
                    {
                        row = i, path = new object[] { "HUD_OBJECTIVES", i },
                        name = Use(Text1(doc, row, "NAME")),
                        descriptions = new[] { "DESCRIPTION", "DESCRIPTION2", "DESCRIPTION3", "DESCRIPTION4" }.Select(k => Use(Text1(doc, row, k))).Where(k => k != null).ToArray(),
                        valve = Text1(doc, row, "VALVE"), displayValve = Text1(doc, row, "DISPLAY_VALVE"), invalidValve = Text1(doc, row, "INVALID_VALVE"),
                        location = location?.Length == 2 ? location : null,
                        animation = Text1(doc, row, "ON_CALL_ANIMATION"),
                        navPoints = Vals(doc, row, "NAV_POINTS")?.OfType<string>().ToArray() ?? Array.Empty<string>(),
                        secondary = doc.Field(row, "ISSECONDARY") >= 0, subObjective = doc.Field(row, "ISSUB_OBJECTIVE") >= 0,
                    });
                    i++;
                }
        }

        var actors = new List<object>();
        Dictionary<string, string>? models = null;
        var characterTypes = CharacterTypesBeside(s);
        var weaponDefs = characterTypes == null ? null : WeaponDefsOf(s);
        if (ReaderRecord(s, "vehicles.rdr") is { } vehicles)
        {
            var doc = RdrDocument.Parse(vehicles.Data);
            // The rows sit in a wrapper list under the root.
            int[] top = doc.Children(0).ToArray();
            bool wrapped = top.Length == 1 && doc.TypeOf(top[0]) == RdrDocument.List && doc.Children(top[0]).All(r => doc.TypeOf(r) == RdrDocument.List)
                && doc.Field(top[0], "name") < 0;
            int rows = wrapped ? top[0] : 0;
            int i = 0;
            foreach (int row in doc.Children(rows))
            {
                if (doc.TypeOf(row) != RdrDocument.List) { i++; continue; }
                object[] path = wrapped ? new object[] { 0, i } : new object[] { i };
                var known = new HashSet<string> { "name", "display", "character", "setup", "team", "ai_params" };
                var setup = doc.Field(row, "setup") is int st and >= 0 ? Pairs(doc, st).Select(p => new { point = p.Key, value = doc.ValueOf(p.Value) }).ToArray() : Array.Empty<object>();
                int ai = doc.Field(row, "ai_params");
                actors.Add(new
                {
                    row = i, path,
                    name = Text1(doc, row, "name"), display = Text1(doc, row, "display"), character = Text1(doc, row, "character"),
                    model = CharacterModel(models ??= PackageModels(s), Text1(doc, row, "character"), CharTypes.Value)
                        ?? CharacterModel(models, characterTypes?.Resolve(Text1(doc, row, "character"))?.Model, CharTypes.Value),
                    chartype = ActorCharacter(characterTypes, weaponDefs, Text1(doc, row, "character")),
                    starts = setup,
                    teams = Vals(doc, row, "team")?.OfType<int>().ToArray() ?? Array.Empty<int>(),
                    flags = Pairs(doc, row).Where(p => !known.Contains(p.Key)).Select(p => new { name = p.Key, values = doc.Values(p.Value) }).ToArray(),
                    ai = ai < 0 ? null : Pairs(doc, ai).Select(p => new { name = p.Key, value = doc.Values(p.Value).FirstOrDefault() }).ToArray(),
                });
                i++;
            }
        }

        object? mission = null;
        if (ReaderRecord(s, "mission.rdr") is { } missionRecord)
        {
            var doc = RdrDocument.Parse(missionRecord.Data);
            int ai = doc.Field(0, "ai_params"), elevation = doc.Field(0, "elevation"), valves = doc.Field(0, "Valves");
            var valveRows = new List<object>();
            if (valves >= 0)
            {
                int k = 0;
                foreach (int v in doc.Children(valves)) { valveRows.Add(new { index = k, name = Text1(doc, v, "NAME"), value = Vals(doc, v, "VALUE")?.FirstOrDefault() }); k++; }
            }
            mission = new
            {
                description = Use(Text1(doc, 0, "description")),
                satchelTimer = Vals(doc, 0, "SATCHEL_TIMER")?.FirstOrDefault(),
                elevation = elevation < 0 ? null : new { min = Num1(doc, elevation, "min"), max = Num1(doc, elevation, "max") },
                ai = ai < 0 ? Array.Empty<object>() : Pairs(doc, ai).Select(p => (object)new { name = p.Key, value = doc.Values(p.Value).FirstOrDefault() }).ToArray(),
                valves = valveRows,
            };
        }

        object? world = null;
        var map = s.Package.MapScene();
        if (map != null && ReaderRecord(s, map.Value.Namespace + ".rdr") is { } worldRecord)
        {
            var doc = RdrDocument.Parse(worldRecord.Data);
            int wp = doc.Field(0, "world_params"), camera = doc.Field(0, "camera");
            int lighting = wp < 0 ? -1 : doc.Field(wp, "GlobalLighting");
            int fog = camera < 0 ? -1 : doc.Field(camera, "fog_standard");
            int ambient = lighting < 0 ? -1 : doc.Field(lighting, "ambient");
            world = new
            {
                record = worldRecord.Name,
                metersPerUnit = wp < 0 ? null : Num1(doc, wp, "MetersPerUnit"),
                defaultMaterial = wp < 0 ? null : Text1(doc, wp, "DefaultMaterial"),
                nightMission = wp < 0 ? null : Num1(doc, wp, "NightMission"),
                nightVision = wp < 0 ? null : Nums(doc, wp, "LensFX_NVG_RGB"),
                shadow = wp < 0 ? null : Nums(doc, wp, "ShadowVector"),
                ambient = ambient < 0 ? null : Nums(doc, ambient, "RGB"),
                lights = lighting < 0 ? Array.Empty<object>() : Pairs(doc, lighting).Where(p => p.Key.StartsWith("light", StringComparison.Ordinal))
                    .Select(p => (object)new { name = p.Key, rgb = Nums(doc, p.Value, "RGB"), dir = Nums(doc, p.Value, "dir"), active = Num1(doc, p.Value, "active") == 1 }).ToArray(),
                clip = camera < 0 ? null : Nums(doc, camera, "clip"),
                fog = fog < 0 ? null : new { enabled = Num1(doc, fog, "enabled") == 1, range = Nums(doc, fog, "range"), rgb = Nums(doc, fog, "RGB") },
            };
        }

        object? extents = null;
        if (ReaderRecord(s, "map.rdr") is { } mapRecord)
        {
            var doc = RdrDocument.Parse(mapRecord.Data);
            int m = doc.Field(0, "Map");
            if (m >= 0) extents = new { min = Nums(doc, m, "MinExtent"), max = Nums(doc, m, "MaxExtent"), floors = Num1(doc, m, "NumFloors") };
        }

        var cams = new List<object>();
        if (ReaderRecord(s, "missioncams.rdr") is { } camRecord)
        {
            var doc = RdrDocument.Parse(camRecord.Data);
            int list = doc.Field(0, "MISSIONCAMS");
            if (list >= 0)
                foreach (int row in doc.Children(list))
                    cams.Add(new { id = Text1(doc, row, "IDENTIFIER"), description = Use(Text1(doc, row, "DESCRIPTION")), animation = Text1(doc, row, "ON_CALL_ANIMATION") });
        }

        var valveList = new List<object>();
        if (ReaderRecord(s, "valves.rdr") is { } valveRecord)
        {
            var doc = RdrDocument.Parse(valveRecord.Data);
            int list = doc.Field(0, "valves");
            if (list >= 0)
                foreach (int row in doc.Children(list))
                    valveList.Add(new { name = Text1(doc, row, "name"), id = Vals(doc, row, "id")?.FirstOrDefault(), value = Vals(doc, row, "value")?.FirstOrDefault() });
        }

        return new
        {
            revision = s.Revision, hasReader = s.Package.Entry(MissionReader) != null,
            objectives, actors, mission, world, extents, cams, valves = valveList,
            hasCharacterTypes = characterTypes != null,
            textureScroll = MapTextureScroll(s), mapImages = MapScreenImages(s),
            texts = used.ToDictionary(k => k, k => texts[k]),
        };
    }

    // mapTable {revision, table, path, values}: the values of one field, in place.
    private static object EditMapTable(AssetSession s, JsonElement a)
    {
        CheckActionRevision(s, a);
        string table = Str(a, "table") ?? throw new ArgumentException("\"table\" (an .rdr in READERM.ZAR) is required.");
        if (!table.EndsWith(".rdr", StringComparison.OrdinalIgnoreCase) || table.Equals(ActionRecord, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose a mission table (.rdr); actions are changed with mapAction.");
        var entry = s.Package.Entry(MissionReader) ?? throw new InvalidOperationException("This package has no mission reader.");
        var reader = ZedContainer.FromBytes(entry.Data);
        var record = reader.FindEntry(table) ?? throw new KeyNotFoundException($"READERM.ZAR has no {table}.");
        if (!a.TryGetProperty("path", out var p) || p.ValueKind != JsonValueKind.Array) throw new ArgumentException("\"path\" (field names and item numbers) is required.");
        var path = p.EnumerateArray().Select(x => x.ValueKind switch
        {
            JsonValueKind.String => (object)x.GetString()!,
            JsonValueKind.Number when x.TryGetInt32(out int n) => n,
            _ => throw new ArgumentException("A path step is a field name or an item number."),
        }).ToList();
        if (!a.TryGetProperty("values", out var v) || v.ValueKind != JsonValueKind.Array) throw new ArgumentException("\"values\" is required.");
        var texts = v.EnumerateArray().Select(x => x.ValueKind switch
        {
            JsonValueKind.Number => float.IsFinite((float)x.GetDouble()) ? x.GetDouble().ToString("R", CultureInfo.InvariantCulture) : throw new ArgumentException("Numbers must be finite."),
            JsonValueKind.String => x.GetString()!,
            _ => throw new ArgumentException("Values are numbers or text."),
        }).ToList();
        var doc = RdrDocument.Parse(record.Data);
        doc.SetValues(path, texts);
        byte[] bytes = ZedSplice.Replace(entry.Data, reader.Nodes.ToList().IndexOf(record), doc.ToBytes());
        return ApplyEdit(s, Str(a, "label") ?? $"Edit {table}", _ => new Dictionary<string, byte[]> { [entry.Leaf] = bytes });
    }

    // mapText {revision, key, text}: a message in the map's language tables (every table that has the key).
    private static object EditMapText(AssetSession s, JsonElement a)
    {
        CheckActionRevision(s, a);
        string key = Str(a, "key") ?? throw new ArgumentException("\"key\" is required.");
        string text = Str(a, "text") ?? throw new ArgumentException("\"text\" is required.");
        if (text.Length == 0 || text.Any(c => c < ' ' || c > '~')) throw new ArgumentException("Text must be printable ASCII and not empty.");
        var changes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in s.Package.Entries.Where(e => e.Leaf.EndsWith("LOC.ZAR", StringComparison.OrdinalIgnoreCase)))
        {
            byte[] data = entry.Data;
            var nodes = ZedContainer.FromBytes(data).Nodes;
            for (int i = 0; i < nodes.Count; i++)
            {
                if (!nodes[i].Name.EndsWith(".rdr", StringComparison.OrdinalIgnoreCase)) continue;
                LanguageTable table;
                try { table = new LanguageTable(nodes[i].Data); } catch (InvalidDataException) { continue; }
                if (!table.Entries().Any(e => e.Key == key)) continue;
                table.SetText(key, text);
                data = ZedSplice.Replace(data, i, table.ToBytes());
                nodes = ZedContainer.FromBytes(data).Nodes;
            }
            if (!ReferenceEquals(data, entry.Data)) changes[entry.Leaf] = data;
        }
        if (changes.Count == 0) throw new KeyNotFoundException($"No language table in this map has {key}.");
        return ApplyEdit(s, Str(a, "label") ?? "Edit mission text", _ => changes);
    }
}
