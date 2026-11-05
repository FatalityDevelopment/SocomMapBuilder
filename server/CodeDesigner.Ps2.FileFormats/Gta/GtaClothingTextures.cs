using System.Text;

namespace CodeDesigner.Ps2.FileFormats.Gta;

/// <summary>PLAYER.IMG's DFFs omit texture names. Choose a shop variant for each standalone preview and
/// follow the game's cutscene replacements. The CJ outfit has its own explicit selection.</summary>
internal sealed class GtaClothingTextures
{
    private sealed record Variant(string Txd, int Slot);
    private readonly Dictionary<string, List<Variant>> _variants = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _cutsceneBase = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Archive, string Part), string?> _selected = new();

    internal GtaClothingTextures(byte[]? shopping, byte[]? clothes)
    {
        bool shopSection = false;
        foreach (var row in Rows(shopping))
        {
            if (row[0] == "section") { shopSection = row.Length > 1 && row[1] is "clothes" or "haircuts"; continue; }
            if (row[0] == "end") { shopSection = false; continue; }
            if (shopSection && row.Length >= 4 && int.TryParse(row[3], out int slot))
                Add(row[2], new Variant(row[0], slot));
        }
        foreach (var row in Rows(clothes))
        {
            if (row[0] == "cuts" && row.Length == 3) _cutsceneBase[row[2]] = row[1];
            if (row[0] != "setc" || row.Length != 5) continue;
            string model = row[3];
            if (row[4] != "-") Add(model, new Variant(row[4], -1));
            else
            {
                // "-" carries over that component's texture. Select a known variant of the same slot.
                int slot = row[2] switch { "head" => 1, "legs" => 2, "necklace" => 13, "watch" => 14, _ => -1 };
                if (slot >= 0 && !_variants.ContainsKey(model) &&
                    _variants.Values.SelectMany(v => v).FirstOrDefault(v => v.Slot == slot) is { } carried) Add(model, carried);
            }
        }
    }

    private void Add(string model, Variant variant)
    {
        if (!_variants.TryGetValue(model, out var list)) _variants.Add(model, list = new());
        list.Add(variant);
    }

    private static IEnumerable<string[]> Rows(byte[]? data)
    {
        if (data == null) yield break;
        foreach (string line in Encoding.ASCII.GetString(data).Split('\n'))
        {
            var words = line.Split('#', 2)[0].ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length != 0) yield return words;
        }
    }

    internal string? For(string model, string archive, Func<string, RwTexDictionary?> txd)
    {
        string part = _cutsceneBase.GetValueOrDefault(model, model);
        var cacheKey = (archive.ToUpperInvariant(), part.ToUpperInvariant());
        if (_selected.TryGetValue(cacheKey, out var selected)) return selected;
        var variants = _variants.GetValueOrDefault(part) ?? new();
        // Prefer a variant named like the mesh, otherwise the first available in the game's shop list.
        foreach (var v in variants.OrderByDescending(v => v.Txd.Equals(part, StringComparison.OrdinalIgnoreCase)).Append(new Variant(part, -1)))
        {
            if (txd(v.Txd)?.Names.FirstOrDefault() is not { } texture) continue;
            string key = $"{v.Txd}/{texture}";
            string? skin = v.Slot switch { 0 => "player_torso/torso", 2 => "player_legs/legs", 3 => "player_feet/foot", _ => null };
            if (skin != null && !skin.StartsWith(v.Txd + "/", StringComparison.OrdinalIgnoreCase) && txd(skin.Split('/')[0]) != null)
                key = $"{skin}+{key}";
            return _selected[cacheKey] = key;
        }
        return _selected[cacheKey] = null;
    }
}
