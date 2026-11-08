using System.Text.RegularExpressions;
using CodeDesigner.Ps2.FileFormats.Zipper.Anim;
using CodeDesigner.Ps2.FileFormats.Zipper.RDR;

namespace MapBuilder.Server.Services;

/// <summary>
/// What a character wears and what its clips do as the game plays them, from the shared tables on the SOCOM II disc
/// (or a copy beside the package): <c>READERC.ZAR › character.rdr</c>'s <c>gear</c> table (each worn model with its
/// bone and offset) and the types' <c>default_gear</c>, and <c>motion.rdr</c>'s per-clip flags and ZANIM callbacks
/// (the effect, its time, and the objects it turns on or off). Layout: docs/formats/disc-survey.md.
/// </summary>
internal sealed partial class ZipperAssetService
{
    private static (CharacterTypes? Types, string? Source) CharacterTypesOf(AssetSession s) => SharedRecord(s, "character.rdr", CharacterTypes.Parse);

    private static MotionTable? MotionTableOf(AssetSession s) => SharedRecord(s, "motion.rdr", MotionTable.Parse).Value;

    // MESH_seal_A_woodland_1_LO / MESH_alban_ter01_1 are the lower-detail copies of seal_A_woodland_LO / alban_ter01.
    private static string CharacterBase(string mesh)
    {
        string name = Regex.Replace(mesh, "^MESH_", "", RegexOptions.IgnoreCase);
        return Regex.Replace(Regex.Replace(name, "_1_LO$", "_LO", RegexOptions.IgnoreCase), "_1$", "", RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// ASSET_CHARACTER_GEAR {model}: the character types drawn with this mesh (each with its default_gear), and the
    /// whole gear table, each item with the package model it draws as ("archive:name", null when the package lacks
    /// it), its bone and its offset (position in units, rotation in degrees).
    /// </summary>
    private static object CharacterGear(AssetSession s, string? model)
    {
        var (types, source) = CharacterTypesOf(s);
        if (types == null)
            return new { source = (string?)null, types = Array.Empty<object>(), gear = Array.Empty<object>(), note = "No READERC.ZAR › character.rdr beside the package or on a SOCOM II disc" };
        var models = PackageModels(s);
        string? want = string.IsNullOrEmpty(model) ? null : CharacterBase(model);
        var matched = types.Types.Select(t => types.Resolve(t.Name)!)
            .Where(r => want != null && r.Model != null && r.Model.Equals(want, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.Gear.Count > 0).ThenBy(r => r.Chain.Count).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Take(60)
            .Select(r => new { name = r.Name, chain = r.Chain, animSet = r.AnimSet, weapons = r.Weapons.Select(w => w.Name).ToList(), gear = r.Gear.Where(n => types.GearItemOf(n) is not { } g || !IsEyeball(g)).ToList() })
            .ToList();
        float k = types.Radians ? 180f / MathF.PI : 1f;
        var shown = ShownByCallbacks(s);
        var weaponNames = WeaponNamesOf(s);
        var gear = types.GearTable.Where(g => !IsEyeball(g)).Select(g => new
        {
            name = g.Name, modelFile = g.ModelFile,
            model = models.GetValueOrDefault(g.Model),
            bone = g.Bone, position = g.Position, rotation = g.Rotation.Select(v => v * k).ToArray(),
            // Worn but out of sight until a clip's callback turns it on (the smoking guard's cigarette).
            hiddenAtStart = shown.Any(o => GearObjectMatches(o, g.Name, g.Model)),
            // Named after a weapon (Satchel): worn only by characters that carry it.
            weapon = weaponNames.Contains(g.Name) ? g.Name : null,
        }).ToList();
        return new { source, types = matched, gear };
    }

    /// <summary>
    /// Eyeball gear (<c>left_eye*</c> / <c>right_eye*</c>). Left off characters: placed at the gear table's offsets they
    /// stand proud of the face, and no depth made them look right, so the heads are drawn without them.
    /// </summary>
    internal static bool IsEyeball(CharacterTypes.GearItem g) =>
        Regex.IsMatch(g.Model, "^(left|right)_eye", RegexOptions.IgnoreCase) || Regex.IsMatch(g.Name, "(^|_)(left|right)_eye", RegexOptions.IgnoreCase);

    /// <summary>
    /// The weapon table's names (ZWEAPON.ZAR). A gear item with a weapon's name is that weapon worn on the body: the
    /// <c>Satchel</c> bag is the satchel charge, and every terrorist and informant type lists it in default_gear, so it
    /// is drawn only on a character whose weapons include it.
    /// </summary>
    private static HashSet<string> WeaponNamesOf(AssetSession s)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in WeaponDefsOf(s)?.Weapons ?? new List<WeaponDefs.Weapon>()) if (w.InternalName.Length > 0) names.Add(w.InternalName);
        return names;
    }

    /// <summary>Whether a type wears a gear item: not an eyeball, and a weapon's gear only when it carries that weapon.</summary>
    internal static bool Wears(CharacterTypes.Resolved type, CharacterTypes.GearItem g, ISet<string> weaponNames) =>
        !IsEyeball(g) && (!weaponNames.Contains(g.Name) || type.Weapons.Any(w => w.Name.Equals(g.Name, StringComparison.OrdinalIgnoreCase)));

    /// <summary>An actor's worn gear: its chartype's default_gear items the package has a model for, with bone and offset.</summary>
    private static object[] ActorGear(CharacterTypes? types, string? chartype, IReadOnlyDictionary<string, string> models, HashSet<string> shown, ISet<string> weaponNames)
    {
        if (types?.Resolve(chartype) is not { } t) return Array.Empty<object>();
        float k = types.Radians ? 180f / MathF.PI : 1f;
        return t.Gear.Select(types.GearItemOf).Where(g => g != null && Wears(t, g, weaponNames) && models.ContainsKey(g.Model) && !shown.Any(o => GearObjectMatches(o, g.Name, g.Model)))
            .Select(g => (object)new { name = g!.Name, model = models[g.Model], bone = g.Bone, position = g.Position, rotation = g.Rotation.Select(v => v * k).ToArray() })
            .ToArray();
    }

    /// <summary>
    /// Whether a ZANIM object name stands for a worn item: its gear name or model, or the start of its name
    /// (<c>show_cigarette</c> turns on <c>cig</c> and <c>cigarette</c>; <c>hide_cigarette</c> only <c>cig</c>).
    /// </summary>
    internal static bool GearObjectMatches(string obj, string gear, string model) =>
        obj.Equals(gear, StringComparison.OrdinalIgnoreCase) || obj.Equals(model, StringComparison.OrdinalIgnoreCase)
        || (obj.Length >= 3 && gear.StartsWith(obj, StringComparison.OrdinalIgnoreCase));

    // Every object a motion.rdr callback turns on, in any clip.
    private static HashSet<string> ShownByCallbacks(AssetSession s)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        MotionTable? table;
        try { table = MotionTableOf(s); }
        catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentException) { return names; }
        var fx = EffectsOf(s);
        if (table == null || fx == null) return names;
        foreach (string effect in table.Entries.SelectMany(e => e.Callbacks).Select(c => c.Effect).Distinct(StringComparer.OrdinalIgnoreCase))
            if (fx.Find(effect) is { } a)
                foreach (var i in a.Tracks.SelectMany(t => t.Instructions).Where(i => i.Op == 0x11))
                    if (i.Fields.TryGetValue("object", out var o) && o is string name && i.Fields.TryGetValue("active", out var v) && v is true) names.Add(name);
        return names;
    }

    /// <summary>A clip's motion.rdr row: flags, values and its ZANIM callbacks with the objects each turns on / off.</summary>
    private static object? MotionOf(AssetSession s, string clip)
    {
        MotionTable? table;
        try { table = MotionTableOf(s); }
        catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentException) { return null; }
        var e = table?.Get(clip);
        if (e == null) return null;
        var fx = EffectsOf(s);
        return new
        {
            flags = e.Flags,
            values = e.Values,
            callbacks = e.Callbacks.Select(c =>
            {
                var a = fx?.Find(c.Effect);
                var shows = new List<string>();
                var hides = new List<string>();
                if (a != null)
                    foreach (var i in a.Tracks.SelectMany(t => t.Instructions).Where(i => i.Op == 0x11))
                    {
                        if (!i.Fields.TryGetValue("object", out var o) || o is not string name) continue;
                        bool on = i.Fields.TryGetValue("active", out var v) && v is true;
                        (on ? shows : hides).Add(name);
                    }
                return new { effect = c.Effect, time = c.Time, found = a != null, shows, hides, spawns = a?.Spawns.Distinct().ToList() ?? new List<string>() };
            }).ToList(),
        };
    }
}
