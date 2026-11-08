using System.Text;
using CodeDesigner.Ps2.FileFormats.Zipper.Geo;
using CodeDesigner.Ps2.FileFormats.Zipper.Texture;
using CodeDesigner.Ps2.FileFormats.Zipper.ZED;

namespace CodeDesigner.Ps2.FileFormats.Zipper.Package
{
    /// <summary>
    /// Removes what a map no longer uses: props nothing places, and then the textures and palettes nothing draws
    /// with. Conservative by design. Only props and textures in the map's own archives are candidates (never
    /// characters, weapons, vehicles, the world, the sky or the shared libraries), and a name that appears anywhere else in the package
    /// (a mission table, a map animation, a texture scroll) keeps its asset. The authoring tables (the map's
    /// <c>_lib.rdr</c> asset library and <c>lod.rdr</c>) list every asset and are not counted; the plan reports the
    /// removed names they still list. Docs: docs/working-with-packages.md.
    /// </summary>
    public static class PackagePrune
    {
        public sealed class Plan
        {
            public List<(string Archive, string Name, int Bytes)> Models { get; } = new();
            public List<(string Archive, string Name, int Bytes)> Textures { get; } = new();
            public List<(string Archive, int Id, int Bytes)> Palettes { get; } = new();
            public int Bytes => Models.Sum(m => m.Bytes) + Textures.Sum(t => t.Bytes) + Palettes.Sum(p => p.Bytes);
            /// <summary>Removed names the authoring tables (<c>&lt;map&gt;_lib.rdr</c>, <c>lod.rdr</c>) still list.</summary>
            public List<string> StillListed { get; } = new();
        }

        private static readonly HashSet<string> Libraries = new(StringComparer.OrdinalIgnoreCase)
        {
            "weap", "flib", "clib", "effe", "alph", "font", "hud", "hud2", "hudw", "load", "junk", "objt", "worl",
        };

        /// <summary>What <see cref="Apply"/> would remove. <paramref name="keep"/> names models or textures to leave.</summary>
        public static Plan Find(ZipperPackage pkg, bool models = true, bool textures = true, IReadOnlyCollection<string>? keep = null)
        {
            var keepSet = new HashSet<string>(keep ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            // Only the map's own archives (MP2_MDL, MP2_TXR …): the shared ones (turrets, vehicles) serve other maps too.
            string? own = pkg.MapNamespace;
            var ownTextures = new HashSet<string>(new[] { own, own == null ? null : pkg.TextureNamespace(own) }.OfType<string>(), StringComparer.OrdinalIgnoreCase);
            var plan = new Plan();
            var removedModels = new HashSet<(string Archive, string Name)>();

            if (models)
            {
                var used = UsedModels(pkg);
                // Text outside the model, scene and texture archives (tables, scripts, the map .ZED): a name there keeps its model.
                string elsewhere = Text(OtherBlobs(pkg));
                foreach (var m in pkg.Models())
                {
                    if (m.Kind != "prop" || !string.Equals(m.Namespace, own, StringComparison.OrdinalIgnoreCase) || keepSet.Contains(m.Name)) continue;
                    if (used.Contains((m.Archive, m.Name)) || Mentions(elsewhere, m.Name)) continue;
                    plan.Models.Add((m.Archive, m.Name, m.Size));
                    removedModels.Add((m.Archive, m.Name));
                }
            }

            if (textures)
            {
                // Every texture name the kept models' payloads (and anything outside the texture archives) still carry.
                var blobs = new List<byte[]>();
                blobs.AddRange(OtherBlobs(pkg));
                foreach (var e in pkg.Entries.Where(e => e.Kind is "mdl" or "geo"))
                {
                    if (e.Kind == "geo") { blobs.Add(e.Data); continue; }
                    var c = pkg.Container(e.Leaf);
                    for (int i = 0; i < c.Nodes.Count; i = ZedTree.SubtreeEnd(c.Nodes, i))
                    {
                        if (removedModels.Contains((e.Leaf, c.Nodes[i].Name))) continue;
                        for (int k = i; k < ZedTree.SubtreeEnd(c.Nodes, i); k++) blobs.Add(c.Nodes[k].Data);
                    }
                }
                string text = Text(blobs);
                var usedPalettes = new HashSet<int>();
                var removedPalettes = new Dictionary<int, string>();
                foreach (var txr in pkg.Entries.Where(e => e.Kind == "txr"))
                {
                    var c = pkg.Container(txr.Leaf);
                    var records = Enumerable.Range(0, c.Nodes.Count).Where(i => c.Nodes[i].Name.EndsWith(".tif", StringComparison.OrdinalIgnoreCase)).ToList();
                    // A texture and its mip levels go together; a level is kept with its texture.
                    var keptRecords = new HashSet<int>();
                    var candidates = new List<int>();
                    foreach (int r in records)
                    {
                        string name = c.Nodes[r].Name;
                        bool library = txr.Namespace == null || Libraries.Contains(txr.Namespace) || !ownTextures.Contains(txr.Namespace);
                        if (library || keepSet.Contains(name) || Mentions(text, name)) keptRecords.UnionWith(PackageImport.MipGroup(c, r));
                        else candidates.Add(r);
                    }
                    foreach (int r in candidates.Where(r => !keptRecords.Contains(r)))
                    {
                        int end = ZedTree.SubtreeEnd(c.Nodes, r);
                        int bytes = Enumerable.Range(r, end - r).Sum(k => c.Nodes[k].Data.Length);
                        plan.Textures.Add((txr.Leaf, c.Nodes[r].Name, bytes));
                        if (PaletteOf(c, r) is int id) removedPalettes[id] = txr.Namespace!;
                    }
                    foreach (int r in keptRecords) if (PaletteOf(c, r) is int id) usedPalettes.Add(id);
                }
                // A palette goes when only removed textures used it.
                foreach (var (id, ns) in removedPalettes.Where(p => !usedPalettes.Contains(p.Key)))
                    if (pkg.Archive(ns, "pal") is { } pal && PackageImport.TopRecordIndex(pkg.Container(pal.Leaf), "texpal_" + id) is var r and >= 0)
                    {
                        var c = pkg.Container(pal.Leaf);
                        int end = ZedTree.SubtreeEnd(c.Nodes, r);
                        plan.Palettes.Add((pal.Leaf, id, Enumerable.Range(r, end - r).Sum(k => c.Nodes[k].Data.Length)));
                    }
            }
            string listed = Text(AuthoringBlobs(pkg));
            plan.StillListed.AddRange(plan.Models.Select(m => m.Name).Concat(plan.Textures.Select(t => t.Name)).Where(n => Mentions(listed, n)));
            return plan;
        }

        // The authoring tables: the map's asset library (mp2_lib.rdr: every model and texture with its export
        // settings) and the LOD groups (lod.rdr). They list every asset, so they would keep everything; the game draws
        // from the compiled archives.
        private static bool Authoring(string record) =>
            record.EndsWith("_lib.rdr", StringComparison.OrdinalIgnoreCase) || record.Equals("lod.rdr", StringComparison.OrdinalIgnoreCase);

        private static IEnumerable<(string Record, byte[] Data)> Records(ZipperPackage.RootEntry e)
        {
            if (e.Leaf.EndsWith(".ZAR", StringComparison.OrdinalIgnoreCase))
            {
                ZedContainer? c = null;
                try { c = ZedContainer.FromBytes(e.Data); } catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException) { }
                if (c != null) { foreach (var n in c.Nodes) yield return (n.Name, n.Data); yield break; }
            }
            yield return (e.Leaf, e.Data);
        }

        // Everything outside the model, scene and texture archives (tables, map animations, the map .ZED), but the authoring tables.
        private static IEnumerable<byte[]> OtherBlobs(ZipperPackage pkg) =>
            pkg.Entries.Where(e => e.Kind == null).SelectMany(Records).Where(r => !Authoring(r.Record)).Select(r => r.Data);

        private static IEnumerable<byte[]> AuthoringBlobs(ZipperPackage pkg) =>
            pkg.Entries.Where(e => e.Kind == null).SelectMany(Records).Where(r => Authoring(r.Record)).Select(r => r.Data);

        /// <summary>Removes the planned records: models with their GEO definitions, textures with their mip levels, palettes.</summary>
        public static Dictionary<string, byte[]> Apply(ZipperPackage pkg, Plan plan)
        {
            var edits = new Dictionary<string, ZedContainer>(StringComparer.OrdinalIgnoreCase);
            ZedContainer Copy(string leaf) => edits.TryGetValue(leaf, out var c) ? c : edits[leaf] = ZedContainer.FromBytes(pkg.Entry(leaf)!.Data);
            var removals = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            void Remove(string leaf, int index) { if (!removals.TryGetValue(leaf, out var l)) removals[leaf] = l = new(); l.Add(index); }

            foreach (var (archive, name, _) in plan.Models)
            {
                var m = pkg.FindModel(archive, name) ?? throw new KeyNotFoundException($"No model {archive}:{name}");
                if (pkg.FindDefinition(m) is { } def && pkg.Archive(def.Namespace, "geo") is { } geo) Remove(geo.Leaf, def.Node.Index);
                Remove(archive, m.NodeIndex);
            }
            foreach (var (archive, name, _) in plan.Textures)
            {
                int r = PackageImport.TopRecordIndex(pkg.Container(archive), name);
                if (r >= 0) Remove(archive, r);
            }
            foreach (var (archive, id, _) in plan.Palettes)
            {
                int r = PackageImport.TopRecordIndex(pkg.Container(archive), "texpal_" + id);
                if (r >= 0) Remove(archive, r);
            }
            foreach (var (leaf, indices) in removals)
            {
                var c = Copy(leaf);
                var parents = ZedTree.ParentIndices(c.Nodes);
                // Later records first: each removal shifts only the records after it.
                foreach (int i in indices.Distinct().OrderByDescending(i => i))
                {
                    bool top = parents[i] < 0;
                    ZedTree.Remove(c, i);
                    if (top) c.RootValue--;
                }
            }
            return edits.ToDictionary(kv => kv.Key, kv => kv.Value.ToBytes(), StringComparer.OrdinalIgnoreCase);
        }

        // The models the map draws: placed references, clutter, props built from them (model_name inside definitions),
        // and every model a non-prop definition (characters, vehicles, weapons) is built from.
        private static HashSet<(string Archive, string Name)> UsedModels(ZipperPackage pkg)
        {
            var used = new HashSet<(string, string)>();
            var queue = new Queue<ZipperPackage.ModelRef>();
            void Use(string name)
            {
                if (pkg.ResolveModel(name) is { } m && used.Add((m.Archive, m.Name))) queue.Enqueue(m);
            }
            void References(GeoNode n)
            {
                if (n.ModelReference.Length > 0) Use(n.ModelReference);
                foreach (var c in n.Children) References(c);
            }
            if (pkg.MapScene() is { } map) References(map.Scene.MapRoot);
            if (pkg.Entry(PackageEdits.ClutterLeaf) is { } clutter)
                foreach (var p in ClutterFile.Parse(pkg.Container(clutter.Leaf))) Use(p.ModelName);
            foreach (var m in pkg.Models().Where(m => m.Kind != "prop")) { used.Add((m.Archive, m.Name)); queue.Enqueue(m); }
            while (queue.Count > 0)
                if (pkg.FindDefinition(queue.Dequeue()) is { } def) References(def.Node);
            return used;
        }

        private static int? PaletteOf(ZedContainer txr, int record)
        {
            if (PackageImport.TexdatIndexOf(txr, record) is not int t) return null;
            try
            {
                var h = ZedTextureFile.ParseHeader(txr.Nodes[t].Data);
                return h.IsIndexed ? h.ResolvePaletteId() : null;
            }
            catch (InvalidDataException) { return null; }
        }

        // Lower-case Latin-1 text of the blobs, NUL-separated, for name searches.
        private static string Text(IEnumerable<byte[]> blobs)
        {
            var sb = new StringBuilder();
            foreach (var b in blobs) { sb.Append(Encoding.Latin1.GetString(b).ToLowerInvariant()); sb.Append('\0'); }
            return sb.ToString();
        }

        // Whole-name match: the name not running on into more of an identifier (crate vs crategroup3).
        private static bool Mentions(string text, string name)
        {
            string n = name.ToLowerInvariant();
            for (int at = text.IndexOf(n, StringComparison.Ordinal); at >= 0; at = text.IndexOf(n, at + 1, StringComparison.Ordinal))
            {
                char after = at + n.Length < text.Length ? text[at + n.Length] : '\0';
                char before = at > 0 ? text[at - 1] : '\0';
                if (!char.IsLetterOrDigit(after) && after != '_' && !char.IsLetterOrDigit(before) && before != '_') return true;
            }
            return false;
        }
    }
}
