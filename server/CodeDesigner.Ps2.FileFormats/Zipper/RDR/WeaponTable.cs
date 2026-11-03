using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CodeDesigner.Ps2.FileFormats.Zipper.RDR
{
    /// <summary>
    /// <c>zweapon.rdr</c> to edit: the ZWEAPON list (weapons) and the ZAMMO list (ammo types), each record a flat
    /// [key, [value], key, [value] ...] list. Values are set on the record's own atoms (<see cref="RdrDocument.Own"/>):
    /// retail records share value atoms (a weapon's InternalName and DisplayName, every "1"), so setting one in place
    /// would change them all. A weapon's AMMO_TYPES is [[NAME, [ammo InternalName]] ...]; damage lives on the ammo.
    /// </summary>
    public sealed class WeaponTable
    {
        public RdrDocument Document { get; }

        public WeaponTable(byte[] rdr) => Document = RdrDocument.Parse(rdr);

        public byte[] ToBytes() => Document.ToBytes();

        /// <summary>Weapon records (ZWEAPON) / ammo records (ZAMMO), as their list atoms.</summary>
        public IEnumerable<int> Weapons => Section("ZWEAPON");
        public IEnumerable<int> Ammo => Section("ZAMMO");

        private IEnumerable<int> Section(string name)
        {
            // Root: [WEAPON_GLOBAL, [...], ZAMMO, [...], ZWEAPON, [...]]
            var root = Document.Children(0).ToList();
            for (int i = 0; i + 1 < root.Count; i++)
                if (Document.ValueOf(root[i]) as string == name) return Document.Children(root[i + 1]).Where(r => Document.TypeOf(r) == RdrDocument.List).ToList();
            throw new InvalidOperationException($"zweapon.rdr has no {name} list");
        }

        /// <summary>The weapon whose InternalName, DisplayName or ModelName is <paramref name="name"/> (case-insensitive).</summary>
        public int FindWeapon(string name) =>
            Weapons.FirstOrDefault(r => new[] { "InternalName", "DisplayName", "ModelName" }.Any(k => string.Equals(Get(r, k) as string, name, StringComparison.OrdinalIgnoreCase)), -1)
            is int w and >= 0 ? w : throw new KeyNotFoundException($"No weapon named {name} in zweapon.rdr (InternalName, DisplayName or ModelName)");

        public int FindAmmo(string name) =>
            Ammo.FirstOrDefault(r => string.Equals(Get(r, "InternalName") as string, name, StringComparison.OrdinalIgnoreCase), -1)
            is int a and >= 0 ? a : throw new KeyNotFoundException($"No ammo type {name} in zweapon.rdr");

        /// <summary>The value list after <paramref name="key"/> in a record, or -1.</summary>
        public int ValueList(int record, string key)
        {
            var items = Document.Children(record).ToList();
            for (int i = 0; i + 1 < items.Count; i++)
                if (Document.ValueOf(items[i]) as string == key && Document.TypeOf(items[i + 1]) == RdrDocument.List) return items[i + 1];
            return -1;
        }

        /// <summary>A field's scalar value (the first item of its value list), or null.</summary>
        public object? Get(int record, string key)
        {
            int list = ValueList(record, key);
            return list < 0 ? null : Document.Children(list).Select(Document.ValueOf).FirstOrDefault();
        }

        /// <summary>Every scalar field of a record, by key.</summary>
        public Dictionary<string, object> Fields(int record)
        {
            var fields = new Dictionary<string, object>(StringComparer.Ordinal);
            var items = Document.Children(record).ToList();
            for (int i = 0; i + 1 < items.Count; i++)
                if (Document.ValueOf(items[i]) is string key && Document.TypeOf(items[i + 1]) == RdrDocument.List
                    && Document.Children(items[i + 1]).FirstOrDefault(-1) is int v and >= 0 && Document.ValueOf(v) is { } value)
                    fields[key] = value;
            return fields;
        }

        /// <summary>Sets a scalar field on the record's own atom, keeping its type (int, float or string).</summary>
        public void Set(int record, string key, object value)
        {
            int list = ValueList(record, key);
            if (list < 0 || Document.CountOf(list) < 1) throw new KeyNotFoundException($"The record has no {key} value");
            int atom = Document.Own(list);
            switch (Document.TypeOf(atom))
            {
                case RdrDocument.Int: Document.SetInt(atom, Convert.ToInt32(value, CultureInfo.InvariantCulture)); break;
                case RdrDocument.Float: Document.SetFloat(atom, Convert.ToSingle(value, CultureInfo.InvariantCulture)); break;
                case RdrDocument.String: Document.SetString(atom, Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty); break;
                default: throw new InvalidOperationException($"{key} is not a number or a string");
            }
        }

        /// <summary>The ammo type names a weapon fires (AMMO_TYPES › NAME).</summary>
        public List<string> AmmoOf(int weapon)
        {
            int list = ValueList(weapon, "AMMO_TYPES");
            if (list < 0) return new List<string>();
            return Document.Children(list).Where(e => Document.TypeOf(e) == RdrDocument.List)
                .Select(e => Get(e, "NAME") as string).Where(n => !string.IsNullOrEmpty(n)).Select(n => n!).ToList();
        }

        /// <summary>
        /// Gives <paramref name="weaponName"/> its own copy of its (first) ammo type named <paramref name="ammoName"/>
        /// with the next free ammo ID, so changing its damage leaves every other weapon firing the old ammo as it was.
        /// Answers the new ammo record.
        /// </summary>
        public int OwnAmmo(string weaponName, string ammoName)
        {
            int weapon = FindWeapon(weaponName);
            string old = AmmoOf(weapon).FirstOrDefault() ?? throw new InvalidOperationException($"{weaponName} fires no ammo type to copy");
            if (Ammo.Any(a => string.Equals(Get(a, "InternalName") as string, ammoName, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"zweapon.rdr already has an ammo type named {ammoName}");
            int nextId = Ammo.Select(a => Get(a, "ID") is int id ? id : 0).DefaultIfEmpty(0).Max() + 1;
            Document.Duplicate(FindAmmo(old));                     // indices after the copy shift: look records up again
            int copy = Ammo.Last();
            foreach (string key in new[] { "InternalName", "DisplayName", "Description" })
                if (ValueList(copy, key) >= 0) Set(copy, key, ammoName);
            Set(copy, "ID", nextId);
            weapon = FindWeapon(weaponName);
            // The weapon's AMMO_TYPES entry may be shared with other weapons: give the weapon its own entry, then its own name.
            Document.Own(ValueList(weapon, "AMMO_TYPES"));
            int entry = Document.Children(ValueList(weapon, "AMMO_TYPES")).First(e => Document.TypeOf(e) == RdrDocument.List);
            Document.Own(entry);
            Set(entry, "NAME", ammoName);
            return Ammo.First(a => string.Equals(Get(a, "InternalName") as string, ammoName, StringComparison.OrdinalIgnoreCase));
        }
    }
}
