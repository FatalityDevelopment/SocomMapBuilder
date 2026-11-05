using System;
using System.Collections.Generic;
using System.Linq;

namespace CodeDesigner.Ps2.FileFormats.Zipper.RDR
{
    /// <summary>
    /// Animation sets from <c>RUN/READERC.ZAR › animset.rdr</c>: a list of <c>{anim_set_name, include?, anim_set_anims}</c>;
    /// each anim is <c>{type_name, anim_name}</c> or <c>{type_name, modes: [["default", clip], {mode = [clip weight] …} …]}</c>.
    /// A set without an anim type of its own takes it from the sets it includes (in order). A character idles in its
    /// set's "Stand" clip: <c>seal_stand</c> for the Seal and Guard sets, <c>civ_stand</c> for No_weapon.
    /// </summary>
    public sealed class AnimSets
    {
        private readonly Dictionary<string, (string[] Includes, Dictionary<string, string> Anims)> _sets = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyCollection<string> Names => _sets.Keys;

        public static AnimSets Parse(byte[] rdr)
        {
            var doc = RdrDocument.Parse(rdr);
            var result = new AnimSets();
            void Visit(int list, int depth)
            {
                if (depth > 4 || doc.TypeOf(list) != RdrDocument.List) return;
                int name = doc.Field(list, "anim_set_name");
                if (name >= 0 && doc.Values(name).FirstOrDefault() is string setName)
                {
                    int include = doc.Field(list, "include");
                    string[] includes = include < 0 ? Array.Empty<string>() : doc.Values(include).OfType<string>().ToArray();
                    var anims = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    int list2 = doc.Field(list, "anim_set_anims");
                    if (list2 >= 0)
                        foreach (int a in doc.Children(list2))
                        {
                            if (doc.TypeOf(a) != RdrDocument.List) continue;
                            int type = doc.Field(a, "type_name");
                            if (type < 0 || doc.Values(type).FirstOrDefault() is not string typeName) continue;
                            string? clip = null;
                            int direct = doc.Field(a, "anim_name");
                            if (direct >= 0) clip = doc.Values(direct).FirstOrDefault() as string;
                            int modes = doc.Field(a, "modes");
                            if (clip == null && modes >= 0)
                                foreach (int m in doc.Children(modes))
                                    if (doc.TypeOf(m) == RdrDocument.List && doc.Values(m) is { Length: >= 2 } v && v[0] is "default" && v[1] is string c) { clip = c; break; }
                            if (clip != null) anims.TryAdd(typeName, clip);
                        }
                    result._sets[setName] = (includes, anims);
                    return;
                }
                foreach (int child in doc.Children(list)) Visit(child, depth + 1);
            }
            Visit(0, 0);
            return result;
        }

        /// <summary>The clip a set plays for an anim type ("Stand", "Crouch" …), from the set or what it includes, or null.</summary>
        public string? Clip(string? set, string type)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? Find(string s)
            {
                if (!seen.Add(s) || !_sets.TryGetValue(s, out var entry)) return null;
                if (entry.Anims.TryGetValue(type, out var clip)) return clip;
                foreach (string inc in entry.Includes) if (Find(inc) is { } hit) return hit;
                return null;
            }
            return set == null ? null : Find(set);
        }
    }
}
