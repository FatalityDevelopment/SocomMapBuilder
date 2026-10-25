using CodeDesigner.Ps2.FileFormats.Assets;
using System.Text;
using CodeDesigner.Ps2.FileFormats.Zipper.ZED;

namespace CodeDesigner.Ps2.FileFormats.Zipper.Geo
{
    /// <summary>
    /// A *_GEO.ZED scene graph. The container's records are a pre-order tree whose <c>Type</c> is the child count.
    /// Named scene nodes carry <c>nparams</c> (column-major 4x4 transform, then bounds min/max), <c>visuals</c> >
    /// <c>vis</c> > <c>vparams</c> (unit flags), <c>model_name</c> (a reference to another model) and
    /// <c>children</c>. Port of zed-geo-parser.ts / zed-scene-compose.ts; see docs/zipper-model-format.md.
    /// </summary>
    public sealed class GeoScene
    {
        public GeoNode Root { get; }
        private readonly Dictionary<string, List<GeoNode>> _byName;

        private GeoScene(GeoNode root, Dictionary<string, List<GeoNode>> byName)
        {
            Root = root;
            _byName = byName;
        }

        public static GeoScene Parse(IReadOnlyList<ZedNode> nodes)
        {
            var byName = new Dictionary<string, List<GeoNode>>(StringComparer.Ordinal);
            var root = new GeoNode("<root>", 0, -1, Array.Empty<byte>(), null);
            int cursor = 0;

            GeoNode Build(GeoNode parent)
            {
                ZedNode z = nodes[cursor];
                var node = new GeoNode(z.Name, z.Type, cursor, z.Data, parent);
                cursor++;
                for (uint k = 0; k < z.Type && cursor < nodes.Count; k++)
                    node.ChildList.Add(Build(node));
                if (!byName.TryGetValue(node.Name, out var list)) byName[node.Name] = list = new List<GeoNode>();
                list.Add(node);
                return node;
            }

            while (cursor < nodes.Count) root.ChildList.Add(Build(root));
            return new GeoScene(root, byName);
        }

        public static GeoScene Parse(byte[] geoZed) => Parse(ZedContainer.FromBytes(geoZed).Nodes);

        public IReadOnlyList<GeoNode> FindAll(string name) =>
            _byName.TryGetValue(name, out var list) ? list : Array.Empty<GeoNode>();

        /// <summary>The node named <paramref name="modelName"/>, preferring one that owns geometry.</summary>
        public GeoNode? FindModel(string modelName)
        {
            var targets = FindAll(modelName);
            if (targets.Count == 0) return null;
            return targets.FirstOrDefault(HasMeshData) ?? targets[0];
        }

        /// <summary>
        /// The node the whole map is composed from: <c>worldmodel</c>. The scene's <c>models</c> list also holds
        /// prototypes (trees, crates, windows) defined at the origin and drawn only where referenced.
        /// </summary>
        public GeoNode MapRoot => FindModel("worldmodel") ?? Root;

        private static bool HasMeshData(GeoNode node) =>
            node.Children.Any(c => c.Name == "visuals" || c.Name == "children" || HasMeshData(c));
    }

    public sealed class GeoNode
    {
        internal GeoNode(string name, uint type, int index, byte[] data, GeoNode? parent)
        {
            Name = name;
            Type = type;
            Index = index;
            Data = data;
            Parent = parent;
        }

        public string Name { get; }
        /// <summary>Child count as stored.</summary>
        public uint Type { get; }
        /// <summary>Index of the record in the GEO container (-1 for the synthetic root).</summary>
        public int Index { get; }
        public byte[] Data { get; }
        public GeoNode? Parent { get; }
        internal List<GeoNode> ChildList { get; } = new();
        public IReadOnlyList<GeoNode> Children => ChildList;

        public GeoNode? Child(string name) => ChildList.FirstOrDefault(c => c.Name == name);

        private static readonly HashSet<string> Containers = new(StringComparer.Ordinal)
        {
            "models", "children", "visuals", "vis", "nparams", "vparams", "model_name", "di", "params", "points",
            "matrix_id", "<root>",
        };

        /// <summary>True for a named scene node (not a container) that has an <c>nparams</c>.</summary>
        public bool IsSceneNode => !Containers.Contains(Name) && Child("nparams") != null;

        public bool HasVisuals => Child("visuals") != null;

        /// <summary>The <c>model_name</c> reference, cleaned (<c>{var} name</c> → <c>name</c>), or empty.</summary>
        public string ModelReference => Child("model_name") is { } r ? GeoNames.Clean(GeoNames.Ascii(r.Data)) : string.Empty;

        public string CleanName => GeoNames.Clean(Name);

        /// <summary>Local transform from <c>nparams</c> (column-major), identity when absent or implausible.</summary>
        public float[] Transform
        {
            get
            {
                byte[]? d = Child("nparams")?.Data;
                if (d == null || d.Length < 0x40) return Matrix4.Identity();
                var m = new float[16];
                for (int i = 0; i < 16; i++) m[i] = BitConverter.ToSingle(d, i * 4);
                return Matrix4.LooksLikeTransform(m) ? m : Matrix4.Identity();
            }
        }

        /// <summary>The accumulated transform of the scene nodes above this one (world = this × local).</summary>
        public float[] ParentWorldTransform
        {
            get
            {
                var chain = new List<GeoNode>();
                for (GeoNode? p = Parent; p != null; p = p.Parent) if (p.IsSceneNode) chain.Add(p);
                float[] m = Matrix4.Identity();
                for (int i = chain.Count - 1; i >= 0; i--) m = Matrix4.Multiply(m, chain[i].Transform);
                return m;
            }
        }

        public float[] WorldTransform => Matrix4.Multiply(ParentWorldTransform, Transform);

        /// <summary>nparams floats 16-21: bounds min / max in units, or null.</summary>
        public (float[] Min, float[] Max)? Bounds
        {
            get
            {
                byte[]? d = Child("nparams")?.Data;
                if (d == null || d.Length < 22 * 4) return null;
                var min = new[] { BitConverter.ToSingle(d, 64), BitConverter.ToSingle(d, 68), BitConverter.ToSingle(d, 72) };
                var max = new[] { BitConverter.ToSingle(d, 76), BitConverter.ToSingle(d, 80), BitConverter.ToSingle(d, 84) };
                if (!min.Concat(max).All(float.IsFinite)) return null;
                return (min, max);
            }
        }

        /// <summary>The <c>vis</c> entries of this node's own <c>visuals</c> group.</summary>
        public IReadOnlyList<GeoNode> Visuals =>
            Child("visuals")?.Children.Where(c => c.Name == "vis").ToList() ?? (IReadOnlyList<GeoNode>)Array.Empty<GeoNode>();

        /// <summary>First vparams flags word under this node (own visuals first), or null.</summary>
        public uint? VisualFlags
        {
            get
            {
                foreach (GeoNode vis in Visuals)
                    if (vis.Child("vparams")?.Data is { Length: >= 4 } v) return BitConverter.ToUInt32(v, 0);
                foreach (GeoNode c in ChildList)
                    if (c.VisualFlags is uint f) return f;
                return null;
            }
        }

        /// <summary>Scene-node children (under this node's <c>children</c> group).</summary>
        public IReadOnlyList<GeoNode> SceneChildren =>
            Child("children")?.Children ?? (IReadOnlyList<GeoNode>)Array.Empty<GeoNode>();

        public override string ToString() => $"{Name} [{ChildList.Count}]";
    }

    /// <summary>Name cleaning and the loose name matching used to resolve scene names to MDL entries.</summary>
    public static class GeoNames
    {
        public static string Ascii(byte[] data)
        {
            var sb = new StringBuilder();
            foreach (byte b in data)
            {
                if (b == 0) break;
                if (b >= 0x20 && b < 0x7F) sb.Append((char)b);
            }
            return sb.ToString().Trim();
        }

        /// <summary><c>m51 {m51}</c> → <c>m51</c>; braces win over the text before them.</summary>
        public static string Clean(string value)
        {
            string s = (value ?? string.Empty).Trim();
            int brace = s.IndexOf('{');
            if (brace >= 0)
            {
                int end = s.IndexOf('}', brace + 1);
                s = end > brace ? s[(brace + 1)..end].Trim() : s[..brace].Trim();
            }
            return s;
        }

        public static string Compact(string value) =>
            new string(value.ToLowerInvariant().Where(c => c is >= '0' and <= '9' or >= 'a' and <= 'z').ToArray());

        public static string StripSuffix(string name)
        {
            string s = (name ?? string.Empty).Trim();
            if (s.EndsWith("_LO", StringComparison.OrdinalIgnoreCase)) s = s[..^3];
            int u = s.LastIndexOf('_');
            if (u >= 0 && u < s.Length - 1 && s[(u + 1)..].All(char.IsDigit)) s = s[..u];
            return s;
        }

        private static readonly Dictionary<string, string[]> Aliases = new(StringComparer.Ordinal)
        {
            // M51 packages place iw80a2 instances that resolve to m16a2 meshes.
            ["iw80a2"] = new[] { "m16a2", "m16", "mesh_m16a2", "mesh_m16" },
            ["mesh_iw80a2"] = new[] { "m16a2", "m16", "mesh_m16a2", "mesh_m16" },
        };

        /// <summary>Every lookup key a name may match an MDL entry by (lowercase, compact, suffix-stripped, MESH_).</summary>
        public static IReadOnlyList<string> Variants(string rawName)
        {
            string clean = Clean(rawName);
            var set = new List<string>();
            void Add(string v) { if (v.Length > 0 && !set.Contains(v)) set.Add(v); }
            void AddAll(string b)
            {
                string lower = b.ToLowerInvariant();
                if (lower.Length == 0) return;
                string stripped = StripSuffix(lower);
                Add(lower); Add(Compact(lower)); Add(stripped); Add(Compact(stripped));
                Add("mesh_" + lower); Add("mesh_" + stripped); Add(Compact("mesh" + lower)); Add(Compact("mesh" + stripped));
            }
            AddAll(rawName);
            AddAll(clean);
            foreach (string k in new[] { rawName.ToLowerInvariant(), clean.ToLowerInvariant() })
                if (Aliases.TryGetValue(k, out var al)) foreach (string a in al) AddAll(a);
            return set;
        }
    }
}
