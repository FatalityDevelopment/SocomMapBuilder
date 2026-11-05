using System;
using System.Collections.Generic;
using System.Linq;

namespace CodeDesigner.Ps2.FileFormats.Zipper.RDR
{
    /// <summary>
    /// Each chartype's default weapons from <c>RUN/READERC.ZAR › character.rdr</c>. Its <c>characters</c> list holds
    /// <c>name, {fields}</c> or <c>name, ":", parent, {fields}</c>; a chartype without its own <c>default_weapons</c>
    /// takes its parent's (an empty list means none). Each weapon is a record whose <c>wep_name</c> is a zweapon.rdr
    /// <c>InternalName</c> ("9mm Sub", "M4A1 SD"), in the order the game lists them (the primary first). Each type
    /// also names its <c>anim_set</c> ("Seal anim set", "Guard anim set", "No_weapon anim set"; see <see cref="AnimSets"/>),
    /// inherited the same way.
    /// </summary>
    public sealed class CharacterLoadouts
    {
        private readonly Dictionary<string, (string? Parent, string[]? Weapons, string? AnimSet)> _types = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyCollection<string> Types => _types.Keys;

        public static CharacterLoadouts Parse(byte[] rdr)
        {
            var doc = RdrDocument.Parse(rdr);
            var result = new CharacterLoadouts();
            int list = doc.Field(0, "characters");
            if (list < 0) return result;
            int[] items = doc.Children(list).ToArray();
            for (int i = 0; i < items.Length; i++)
            {
                if (doc.ValueOf(items[i]) is not string name) continue;
                string? parent = null;
                int j = i + 1;
                if (j + 1 < items.Length && doc.ValueOf(items[j]) is ":" && doc.ValueOf(items[j + 1]) is string p) { parent = p; j += 2; }
                if (j >= items.Length || doc.TypeOf(items[j]) != RdrDocument.List) continue;
                int weapons = doc.Field(items[j], "default_weapons");
                string[]? names = weapons < 0 ? null : doc.Children(weapons)
                    .Select(w => doc.TypeOf(w) == RdrDocument.List && doc.Field(w, "wep_name") is int f and >= 0 ? doc.Values(f).FirstOrDefault() as string : null)
                    .Where(n => !string.IsNullOrEmpty(n)).Select(n => n!).ToArray();
                int anim = doc.Field(items[j], "anim_set");
                string? animSet = anim < 0 ? null : doc.Values(anim).FirstOrDefault() as string;
                result._types[name] = (parent, names, animSet);
                i = j;
            }
            return result;
        }

        /// <summary>The chartype's default weapons (its own, else the nearest parent's), or null when the type is unknown.</summary>
        public string[]? WeaponsOf(string? chartype)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? at = chartype;
            while (at != null && seen.Add(at) && _types.TryGetValue(at, out var t))
            {
                if (t.Weapons != null) return t.Weapons;
                at = t.Parent;
            }
            return chartype != null && _types.ContainsKey(chartype) ? Array.Empty<string>() : null;
        }

        /// <summary>The chartype's animation set (its own, else the nearest parent's), or null.</summary>
        public string? AnimSetOf(string? chartype)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? at = chartype;
            while (at != null && seen.Add(at) && _types.TryGetValue(at, out var t))
            {
                if (t.AnimSet != null) return t.AnimSet;
                at = t.Parent;
            }
            return null;
        }
    }
}
