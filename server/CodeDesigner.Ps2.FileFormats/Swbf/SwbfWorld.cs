namespace CodeDesigner.Ps2.FileFormats.Swbf
{
    /// <summary>
    /// A Battlefront world (a "wrld" chunk): NAME, TNAM (its terrain), SNAM (its sky), then regn / inst / Hint / BARR
    /// records. A record's INFO holds TYPE (an inst's entity class, a regn's shape), NAME and XFRM (a 3x3 rotation, rows,
    /// then the translation: row vectors, v' = v * M, in the models' own frame); a regn's INFO also holds SIZE.
    /// PROP children are {u32 fnv1a(lower-case key), value}. An inst's class is an "entc" (TYPE, BASE, PROPs) whose
    /// GeometryName names a "gmod" (LOD0..LOWD model names) or directly a model. (Per openbf's research, MIT.)
    /// </summary>
    public static class SwbfWorld
    {
        public sealed record Prop(uint Key, string Value);

        /// <summary>A placed record: its row-vector 4x4 matrix as 16 floats.</summary>
        public sealed record Record(string Type, string Name, float[] Matrix, float[]? Size, IReadOnlyList<Prop> Props);

        public sealed record World(string Name, string? Terrain, string? Sky, IReadOnlyList<Record> Instances, IReadOnlyList<Record> Regions);

        public sealed record EntityClass(string Type, string Base, string? Geometry);

        public static uint Fnv1a(string key)
        {
            uint h = 2166136261;
            foreach (char ch in key.ToLowerInvariant()) { h ^= (byte)ch; h *= 16777619; }
            return h;
        }

        public static readonly uint GeometryName = Fnv1a("GeometryName");

        public static World Parse(Ucfb wrld)
        {
            var inst = new List<Record>();
            var regn = new List<Record>();
            foreach (var c in wrld.Children)
            {
                if (c.Tag != "inst" && c.Tag != "regn") continue;
                if (ReadRecord(c) is not { } r) continue;
                (c.Tag == "inst" ? inst : regn).Add(r);
            }
            return new World(wrld.ChildText("NAME") ?? "", Blank(wrld.ChildText("TNAM")), Blank(wrld.ChildText("SNAM")), inst, regn);
        }

        private static string? Blank(string? s) => string.IsNullOrEmpty(s) ? null : s;

        private static Record? ReadRecord(Ucfb rec)
        {
            if (rec.Child("INFO") is not { } info || info.Child("XFRM") is not { Size: >= 48 } x) return null;
            var m = new float[16];
            for (int r = 0; r < 3; r++) for (int k = 0; k < 3; k++) m[r * 4 + k] = x.F32((r * 3 + k) * 4);
            for (int k = 0; k < 3; k++) m[12 + k] = x.F32(36 + k * 4);
            m[15] = 1;
            float[]? size = info.Child("SIZE") is { Size: >= 12 } s ? new[] { s.F32(0), s.F32(4), s.F32(8) } : null;
            return new Record(info.ChildText("TYPE") ?? "", info.ChildText("NAME") ?? "", m, size, Props(rec));
        }

        public static List<Prop> Props(Ucfb parent)
        {
            var list = new List<Prop>();
            foreach (var p in parent.Children)
                if (p.Tag == "PROP" && p.Size >= 5) list.Add(new Prop(p.U32(0), p.Text(4)));
            return list;
        }

        public static EntityClass ParseClass(Ucfb entc) =>
            new(entc.ChildText("TYPE") ?? "", entc.ChildText("BASE") ?? "",
                Props(entc).FirstOrDefault(p => p.Key == GeometryName)?.Value is { Length: > 0 } g ? g : null);

        /// <summary>A "gmod": its NAME and the most detailed model it lists (LOD0, then LOD1, LOD2, LOWD).</summary>
        public static (string Name, string? Model) ParseGeometry(Ucfb gmod)
        {
            string? best = null;
            int rank = int.MaxValue;
            foreach (var c in gmod.Children)
            {
                int r = c.Tag switch { "LOD0" => 0, "LOD1" => 1, "LOD2" => 2, "LOWD" => 3, _ => -1 };
                if (r < 0 || r >= rank) continue;
                string name = c.Text();
                if (name.Length == 0) continue;
                best = name;
                rank = r;
            }
            return (gmod.ChildText("NAME") ?? "", best);
        }
    }
}
