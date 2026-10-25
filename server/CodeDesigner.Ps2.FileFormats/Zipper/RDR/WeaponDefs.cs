using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CodeDesigner.Ps2.FileFormats.Zipper.RDR
{
    /// <summary>
    /// Weapon definitions from <c>RUN/ZWEAPON.ZAR › zweapon.rdr</c>: per weapon its model, the ZANIM effect it plays
    /// when fired (<c>FireAnimName</c>, e.g. <c>muzzle_m4</c> in CZANIM.ZAR), the delay between shots and the rest
    /// of its fields as stored. Records are flat [key, value, key, value ...] lists in the rdr tree.
    /// </summary>
    public sealed class WeaponDefs
    {
        public sealed class Weapon
        {
            public string InternalName { get; init; } = string.Empty;
            public string ModelName { get; init; } = string.Empty;
            public string FireAnimName { get; init; } = string.Empty;
            public float FireWait { get; init; }
            public string TracerTextureName { get; init; } = string.Empty;
            /// <summary>Every scalar field of the record (nested groups left out).</summary>
            public Dictionary<string, object> Fields { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        }

        public List<Weapon> Weapons { get; } = new();

        /// <summary>The weapon drawn with <paramref name="model"/> (first match), or null.</summary>
        public Weapon? ForModel(string model) =>
            Weapons.FirstOrDefault(w => w.ModelName.Equals(model, StringComparison.OrdinalIgnoreCase));

        public static WeaponDefs Parse(byte[] rdr)
        {
            var defs = new WeaponDefs();
            object root = RdrShape.Shape(RdrFile.Parse(rdr).BuildTree());
            void Visit(object node)
            {
                if (node is Dictionary<string, object> d) { foreach (var v in d.Values) Visit(v); return; }
                if (node is not List<object> list) return;
                if (TryRecord(list) is { } w) { defs.Weapons.Add(w); return; }
                foreach (var v in list) Visit(v);
            }
            Visit(root);
            return defs;
        }

        // A record: alternating string keys and values that include ModelName.
        private static Weapon? TryRecord(List<object> list)
        {
            if (list.Count < 4 || list.Count % 2 != 0) return null;
            var fields = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < list.Count; i += 2)
            {
                if (list[i] is not string key) return null;
                if (list[i + 1] is string or int or float) fields[key] = list[i + 1];
            }
            if (!fields.TryGetValue("ModelName", out var model) || model is not string m || m.Length == 0) return null;
            string S(string k) => fields.TryGetValue(k, out var v) && v is string s ? s : string.Empty;
            float F(string k) => fields.TryGetValue(k, out var v) ? Convert.ToSingle(v, CultureInfo.InvariantCulture) : 0f;
            return new Weapon
            {
                InternalName = S("InternalName"), ModelName = m, FireAnimName = S("FireAnimName"),
                FireWait = F("FireWait"), TracerTextureName = S("TracerTextureName"), Fields = fields,
            };
        }
    }
}
