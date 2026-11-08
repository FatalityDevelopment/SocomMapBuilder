using System;
using System.Collections.Generic;
using System.Linq;

namespace CodeDesigner.Ps2.FileFormats.Zipper.RDR
{
    /// <summary>
    /// How the game plays each character clip, from <c>RUN/READERC.ZAR › motion.rdr</c>: the root's <c>animations</c>
    /// list holds one row per clip, a flat run of keys and value lists, e.g.
    /// <c>anim_name [guard_light_cigarette] SINGLEPLAYER [] NoFire [] looped [0] playback [8]
    /// zanim_callback [name [show_cigarette] time [0.05]] zanim_callback [name [cigarette_flame] time [0.656]]</c>.
    /// A key with an empty list is a flag (<c>NoFire</c>, <c>NoTurn</c>, <c>DropWeapon</c> …). Each
    /// <c>zanim_callback</c> runs a ZANIM effect (CZANIM.ZAR) at a time into the clip, in seconds: that is how the
    /// smoking guard's cigarette appears in his hand (<c>show_cigarette</c> turns the <c>cig</c> / <c>cigarette</c>
    /// gear objects on) and lights.
    /// </summary>
    public sealed class MotionTable
    {
        public sealed record Callback(string Effect, float Time);

        public sealed class Entry
        {
            public string Name { get; init; } = string.Empty;
            /// <summary>Keys stored with an empty value list.</summary>
            public IReadOnlyList<string> Flags { get; init; } = Array.Empty<string>();
            /// <summary>Single-number keys (<c>looped</c>, <c>playback</c>, <c>BlendTime</c>, <c>max_velocity</c> …).</summary>
            public IReadOnlyDictionary<string, float> Values { get; init; } = new Dictionary<string, float>();
            public IReadOnlyList<Callback> Callbacks { get; init; } = Array.Empty<Callback>();
        }

        private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyCollection<Entry> Entries => _entries.Values;

        public Entry? Get(string clip) => _entries.TryGetValue(clip, out var e) ? e : null;

        public static MotionTable Parse(byte[] rdr)
        {
            var doc = RdrDocument.Parse(rdr);
            var table = new MotionTable();
            int list = doc.Field(0, "animations");
            if (list < 0 || doc.TypeOf(list) != RdrDocument.List) return table;
            foreach (int row in doc.Children(list))
            {
                if (doc.TypeOf(row) != RdrDocument.List) continue;
                string? name = null;
                var flags = new List<string>();
                var values = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
                var callbacks = new List<Callback>();
                int[] items = doc.Children(row).ToArray();
                for (int i = 0; i + 1 < items.Length; i += 2)
                {
                    if (doc.ValueOf(items[i]) is not string key || doc.TypeOf(items[i + 1]) != RdrDocument.List) { i--; continue; }
                    int value = items[i + 1];
                    if (key == "anim_name") { name = doc.Values(value).FirstOrDefault() as string; continue; }
                    if (key == "zanim_callback")
                    {
                        string? effect = doc.Field(value, "name") is int n and >= 0 ? First(doc, n) as string : null;
                        float time = doc.Field(value, "time") is int t and >= 0 ? Number(First(doc, t)) ?? 0 : 0;
                        if (effect != null) callbacks.Add(new Callback(effect, time));
                        continue;
                    }
                    if (doc.CountOf(value) == 0) { flags.Add(key); continue; }
                    if (doc.CountOf(value) == 1 && Number(doc.Values(value)[0]) is float f) values[key] = f;
                }
                if (name == null) continue;
                table._entries[name] = new Entry { Name = name, Flags = flags, Values = values, Callbacks = callbacks.OrderBy(c => c.Time).ToList() };
            }
            return table;
        }

        private static object? First(RdrDocument doc, int atom) =>
            doc.TypeOf(atom) == RdrDocument.List ? doc.Values(atom).FirstOrDefault() : doc.ValueOf(atom);

        private static float? Number(object? v) => v switch { int i => i, float f => f, double d => (float)d, _ => null };
    }
}
