using System;
using System.Collections.Generic;
using System.Linq;

namespace CodeDesigner.Ps2.FileFormats.Zipper.RDR
{
    /// <summary>
    /// The game's character types, from <c>RUN/READERC.ZAR › character.rdr</c> (layout: docs/formats/disc-survey.md).
    /// The root object's <c>characters</c> list is flat: a type's name, then optionally <c>":"</c> and the type it
    /// inherits from, then its field object, e.g. <c>"m51_terror01", ":", "albanian_terrorist01", [default_weapons …]</c>.
    /// A field a type leaves out comes from its parent (<see cref="Resolve"/>). Map actors name a type
    /// (<c>vehicles.rdr</c> › <c>character</c>); weapons are named by zweapon.rdr's <c>InternalName</c>.
    /// </summary>
    public sealed class CharacterTypes
    {
        public sealed record Weapon(string Name, int? Ammo, int? Magazines);

        /// <summary>
        /// One worn item of the root <c>gear</c> table: <c>{name, model, ofs [bone, [x, y, z], [rx, ry, rz]]}</c>. The model
        /// is authored (<c>seal_black_hat.mb</c>); the package holds it in <c>FLIB_MDL.ZED</c> without the extension. The
        /// offset is in the bone's space, the angles in the table's <c>AngleUnits</c> (degrees on retail).
        /// </summary>
        public sealed record GearItem(string Name, string ModelFile, string Bone, float[] Position, float[] Rotation)
        {
            public string Model => StripExtension(ModelFile);
        }

        /// <summary>One type as stored (fields it does not set are null).</summary>
        public sealed class CharacterType
        {
            public string Name { get; init; } = string.Empty;
            public string? Parent { get; init; }
            /// <summary><c>model_name</c> as stored, e.g. <c>alban_ter01.xsi</c>.</summary>
            public string? ModelFile { get; init; }
            public string? TextureAsset { get; init; }
            public string? AnimSet { get; init; }
            public IReadOnlyList<Weapon>? Weapons { get; init; }
            public IReadOnlyList<string>? Gear { get; init; }
        }

        /// <summary>A type with its inherited fields filled in.</summary>
        public sealed class Resolved
        {
            public string Name { get; init; } = string.Empty;
            /// <summary>The type and its ancestors, nearest first.</summary>
            public IReadOnlyList<string> Chain { get; init; } = Array.Empty<string>();
            public string? ModelFile { get; init; }
            /// <summary>The model name without its extension (<c>alban_ter01</c>): the package draws it as <c>MESH_&lt;model&gt;</c>.</summary>
            public string? Model => ModelFile == null ? null : StripExtension(ModelFile);
            public string? TextureAsset { get; init; }
            public string? AnimSet { get; init; }
            public IReadOnlyList<Weapon> Weapons { get; init; } = Array.Empty<Weapon>();
            public IReadOnlyList<string> Gear { get; init; } = Array.Empty<string>();
        }

        private readonly Dictionary<string, CharacterType> _types = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyCollection<CharacterType> Types => _types.Values;

        private readonly Dictionary<string, GearItem> _gear = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The root <c>gear</c> table in file order (a later row of the same name replaces an earlier one).</summary>
        public IReadOnlyCollection<GearItem> GearTable => _gear.Values;

        public GearItem? GearItemOf(string name) => _gear.TryGetValue(name, out var g) ? g : null;

        /// <summary>Whether the table's angles are radians (<c>AngleUnits RAD</c>); retail says DEG.</summary>
        public bool Radians { get; private set; }

        public CharacterType? Get(string name) => _types.TryGetValue(name, out var t) ? t : null;

        public static CharacterTypes Parse(byte[] rdr)
        {
            var doc = RdrDocument.Parse(rdr);
            var result = new CharacterTypes();
            result.Radians = Text(doc, 0, "AngleUnits") is string units && units.StartsWith("RAD", StringComparison.OrdinalIgnoreCase);
            int gear = doc.Field(0, "gear");
            if (gear >= 0 && doc.TypeOf(gear) == RdrDocument.List)
                foreach (int row in doc.Children(gear))
                    if (doc.TypeOf(row) == RdrDocument.List && GearRow(doc, row) is { } item) result._gear[item.Name] = item;
            int list = doc.Field(0, "characters");
            if (list < 0 || doc.TypeOf(list) != RdrDocument.List) return result;
            int[] items = doc.Children(list).ToArray();
            for (int i = 0; i < items.Length; i++)
            {
                if (doc.ValueOf(items[i]) is not string name) continue;
                string? parent = null;
                if (i + 2 < items.Length && doc.ValueOf(items[i + 1]) is ":" && doc.ValueOf(items[i + 2]) is string p) { parent = p; i += 2; }
                int fields = i + 1 < items.Length && doc.TypeOf(items[i + 1]) == RdrDocument.List ? items[++i] : -1;
                // A later definition of the same name replaces an earlier one.
                result._types[name] = new CharacterType
                {
                    Name = name, Parent = parent,
                    ModelFile = fields < 0 ? null : Text(doc, fields, "model_name"),
                    TextureAsset = fields < 0 ? null : Text(doc, fields, "texture_asset"),
                    AnimSet = fields < 0 ? null : Text(doc, fields, "anim_set"),
                    Weapons = fields < 0 ? null : Weapons(doc, fields),
                    Gear = fields < 0 || doc.Field(fields, "default_gear") is not (int g and >= 0) ? null
                        : doc.Values(g).OfType<string>().ToArray(),
                };
            }
            return result;
        }

        /// <summary>The type with every field it leaves out taken from the nearest ancestor that sets it; null when unknown.</summary>
        public Resolved? Resolve(string? name)
        {
            if (string.IsNullOrWhiteSpace(name) || !_types.TryGetValue(name, out var type)) return null;
            var chain = new List<CharacterType>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var t = type; t != null && seen.Add(t.Name); t = t.Parent == null ? null : Get(t.Parent)) chain.Add(t);
            T? First<T>(Func<CharacterType, T?> pick) where T : class => chain.Select(pick).FirstOrDefault(v => v != null);
            return new Resolved
            {
                Name = type.Name, Chain = chain.Select(t => t.Name).ToArray(),
                ModelFile = First(t => t.ModelFile), TextureAsset = First(t => t.TextureAsset), AnimSet = First(t => t.AnimSet),
                Weapons = First(t => t.Weapons) ?? Array.Empty<Weapon>(),
                Gear = First(t => t.Gear) ?? Array.Empty<string>(),
            };
        }

        /// <summary>
        /// A map's team roster (<c>READERM.ZAR › chartype.rdr</c>, on multiplayer maps): per team (<c>navyseals</c>,
        /// <c>terrorists</c>) the selectable characters, each <c>{character, name}</c>.
        /// </summary>
        public static IReadOnlyList<(string Team, IReadOnlyList<(string Character, string? Name)> Members)> ParseRoster(byte[] rdr)
        {
            var doc = RdrDocument.Parse(rdr);
            int root = 0;
            while (doc.CountOf(root) == 1 && doc.TypeOf(doc.FirstChild(root)) == RdrDocument.List) root = doc.FirstChild(root);
            var teams = new List<(string, IReadOnlyList<(string, string?)>)>();
            int[] items = doc.Children(root).ToArray();
            for (int i = 0; i + 1 < items.Length; i++)
            {
                if (doc.ValueOf(items[i]) is not string team || doc.TypeOf(items[i + 1]) != RdrDocument.List) continue;
                var members = doc.Children(items[i + 1]).Where(r => doc.TypeOf(r) == RdrDocument.List)
                    .Select(r => (Text(doc, r, "character") ?? string.Empty, Text(doc, r, "name"))).Where(m => m.Item1.Length > 0).ToList();
                teams.Add((team, members));
                i++;
            }
            return teams;
        }

        // {name [n], model [file], ofs [bone, [x y z], [rx ry rz]]}; a missing vector is zeros.
        private static GearItem? GearRow(RdrDocument doc, int row)
        {
            if (Text(doc, row, "name") is not string name || Text(doc, row, "model") is not string model) return null;
            int ofs = doc.Field(row, "ofs");
            string bone = "hips";
            float[] pos = new float[3], rot = new float[3];
            if (ofs >= 0 && doc.TypeOf(ofs) == RdrDocument.List)
            {
                int[] parts = doc.Children(ofs).ToArray();
                if (parts.Length > 0 && doc.ValueOf(parts[0]) is string b) bone = b;
                if (parts.Length > 1) pos = Vector(doc, parts[1]);
                if (parts.Length > 2) rot = Vector(doc, parts[2]);
            }
            return new GearItem(name, model, bone, pos, rot);
        }

        private static float[] Vector(RdrDocument doc, int atom)
        {
            var v = new float[3];
            if (doc.TypeOf(atom) != RdrDocument.List) return v;
            object?[] values = doc.Values(atom);
            for (int k = 0; k < 3 && k < values.Length; k++)
                v[k] = values[k] switch { int i => i, float f => f, double d => (float)d, _ => 0f };
            return v;
        }

        public static string StripExtension(string file)
        {
            int dot = file.LastIndexOf('.');
            return dot > 0 ? file[..dot] : file;
        }

        private static string? Text(RdrDocument doc, int list, string key) =>
            doc.Field(list, key) is int f and >= 0 ? (doc.TypeOf(f) == RdrDocument.List ? doc.Values(f).FirstOrDefault() : doc.ValueOf(f)) as string : null;

        private static int? Int(RdrDocument doc, int list, string key) =>
            doc.Field(list, key) is int f and >= 0 && doc.Values(f).FirstOrDefault() is int n ? n : null;

        // default_weapons: rows in the flat form {wep_name, ammo_count, mag_count} or a bare pair ["wep_name", [name]].
        private static IReadOnlyList<Weapon>? Weapons(RdrDocument doc, int fields)
        {
            int list = doc.Field(fields, "default_weapons");
            if (list < 0 || doc.TypeOf(list) != RdrDocument.List) return null;
            var weapons = new List<Weapon>();
            // A single weapon may be stored as the bare pair itself.
            if (doc.CountOf(list) == 2 && doc.ValueOf(doc.FirstChild(list)) is "wep_name")
                return Text(doc, list, "wep_name") is { } only ? new[] { new Weapon(only, null, null) } : weapons;
            foreach (int row in doc.Children(list))
                if (doc.TypeOf(row) == RdrDocument.List && Text(doc, row, "wep_name") is { } name)
                    weapons.Add(new Weapon(name, Int(doc, row, "ammo_count"), Int(doc, row, "mag_count")));
            return weapons;
        }
    }
}
