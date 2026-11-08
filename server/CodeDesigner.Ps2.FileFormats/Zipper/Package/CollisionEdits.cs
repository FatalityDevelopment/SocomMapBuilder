using CodeDesigner.Ps2.FileFormats.Assets;
using CodeDesigner.Ps2.FileFormats.Zipper.Geo;
using CodeDesigner.Ps2.FileFormats.Zipper.ZED;

namespace CodeDesigner.Ps2.FileFormats.Zipper.Package
{
    /// <summary>
    /// Collision edits (GEO <c>di</c> regions, see <see cref="GeoCollision"/>): a model's faces, written to its
    /// definition and to every placed reference of it (each reference carries its own copy), and the world's faces
    /// cleared by node, world-model visual or area. Each returns the changed GEO archives by leaf.
    /// </summary>
    public static class CollisionEdits
    {
        /// <summary>A model's collision faces in model space: its definition's region, or none.</summary>
        public static List<GeoCollision.Face> Faces(ZipperPackage pkg, ZipperPackage.ModelRef model) =>
            pkg.FindDefinition(model) is { } def ? GeoCollision.Read(def.Node) : new List<GeoCollision.Face>();

        /// <summary>
        /// Replaces <paramref name="modelName"/>'s collision with <paramref name="faces"/> (empty: not solid) on its
        /// definition and on every placed reference naming it. <paramref name="references"/> counts the references.
        /// </summary>
        public static Dictionary<string, byte[]> SetCollision(ZipperPackage pkg, string modelName, IReadOnlyList<GeoCollision.Face> faces, out int references)
        {
            var model = Exact(pkg, modelName);
            // Retail props all have a definition; without one only the placed references get the faces.
            var def = pkg.FindDefinition(model);
            var region = GeoCollision.Region(faces);
            var changed = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            int placed = 0;
            foreach (var geo in pkg.Entries.Where(e => e.Kind == "geo" && e.Namespace != null).ToList())
            {
                var container = ZedContainer.FromBytes(geo.Data);
                var scene = GeoScene.Parse(container.Nodes);
                var targets = new List<int>();
                if (def is { } d && string.Equals(geo.Namespace, d.Namespace, StringComparison.OrdinalIgnoreCase)) targets.Add(d.Node.Index);
                void Visit(GeoNode n)
                {
                    if (n.IsSceneNode && n.ModelReference.Length > 0 && pkg.ResolveModel(n.ModelReference) is { } r && r.Archive == model.Archive && r.Name == model.Name)
                    {
                        targets.Add(n.Index);
                        placed++;
                    }
                    foreach (var c in n.Children) Visit(c);
                }
                Visit(scene.Root);
                if (targets.Count == 0) continue;
                // Later records first: each edit shifts only the records after its own node.
                foreach (int index in targets.Distinct().OrderByDescending(i => i)) Replace(container, index, region);
                changed[geo.Leaf] = container.ToBytes();
            }
            if (changed.Count == 0) throw new InvalidOperationException($"{model.Name} has no GEO definition and is not placed: nothing holds its collision");
            references = placed;
            return changed;
        }

        /// <summary>
        /// Removes the collision of the world's own scene nodes (those under <c>worldmodel</c> that are not placed
        /// references) chosen by record index, by the world-model visual they draw (<c>N004</c>, <c>N012_0</c>) or by
        /// an area (x/z min and max: a node goes when any of its faces' centres falls inside), or all of them. With
        /// <paramref name="includeReferences"/> the placed props in the area lose theirs too. <paramref name="removed"/>
        /// lists the nodes cleared and <paramref name="faces"/> counts their faces.
        /// </summary>
        public static Dictionary<string, byte[]> ClearWorldCollision(ZipperPackage pkg, IReadOnlyCollection<int>? geoIndices, IReadOnlyCollection<string>? visuals,
            float[]? area, bool all, bool includeReferences, out List<string> removed, out int faces)
        {
            var (ns, _) = pkg.MapScene() ?? throw new InvalidOperationException("This package has no map scene (no worldmodel)");
            var entry = pkg.Archive(ns, "geo")!;
            var container = ZedContainer.FromBytes(entry.Data);
            var scene = GeoScene.Parse(container.Nodes);
            var world = scene.MapRoot;
            if (area != null && area.Length != 4) throw new ArgumentException("area is [minX, minZ, maxX, maxZ]");

            var wanted = new HashSet<int>(geoIndices ?? Array.Empty<int>());
            if (visuals is { Count: > 0 })
            {
                var byVisual = VisualNodes(world);
                foreach (string key in visuals)
                {
                    int? n = VisualPlacement.ParseKey(key.Contains('_') ? key : key + "_0") is var (node, _) ? node : null;
                    if (n is not int i || i < 0 || i >= byVisual.Count) throw new ArgumentException($"{key} is not a world-model visual (N000 … N{byVisual.Count - 1:000})");
                    wanted.Add(byVisual[i].Index);
                }
            }

            var hits = new List<GeoNode>();
            int count = 0;
            void Visit(GeoNode n, bool underWorld)
            {
                bool inWorld = underWorld || n == world;
                if (inWorld && n.IsSceneNode && n.Child("di") != null)
                {
                    bool reference = n.ModelReference.Length > 0;
                    var own = GeoCollision.Read(n);
                    bool pick = wanted.Contains(n.Index)
                        || (!reference || includeReferences) && (all || area != null && own.Any(f => Inside(GeoCollision.Transform(f, n.WorldTransform), area)));
                    if (pick) { hits.Add(n); count += own.Count; }
                }
                foreach (var c in n.Children) Visit(c, inWorld);
            }
            Visit(scene.Root, false);
            foreach (int bad in wanted.Where(i => hits.All(h => h.Index != i)))
                if (!(container.Nodes.Count > bad && bad >= 0)) throw new ArgumentException($"Record {bad} is not in {entry.Leaf}");
            foreach (var n in hits.OrderByDescending(h => h.Index)) Replace(container, n.Index, null);
            removed = hits.Select(h => h.Name).ToList();
            faces = count;
            return hits.Count == 0 ? new Dictionary<string, byte[]>() : new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase) { [entry.Leaf] = container.ToBytes() };
        }

        /// <summary>The world model's scene nodes that own visuals, in WORL_MDL's N-number order.</summary>
        public static List<GeoNode> VisualNodes(GeoNode world)
        {
            var list = new List<GeoNode>();
            void Visit(GeoNode n)
            {
                if (n.Visuals.Count > 0) list.Add(n);
                foreach (var c in n.SceneChildren) Visit(c);
            }
            Visit(world);
            return list;
        }

        /// <summary>Every face in the scene in world space, with the node that holds it (for drawing and checks).</summary>
        public static IEnumerable<(GeoNode Node, GeoCollision.Face Face, float[] World)> WorldFaces(GeoScene scene)
        {
            var stack = new Stack<GeoNode>();
            stack.Push(scene.MapRoot);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                if (n.IsSceneNode && n.Child("di") != null)
                {
                    float[] m = n.WorldTransform;
                    foreach (var f in GeoCollision.Read(n)) yield return (n, f, GeoCollision.Transform(f, m));
                }
                foreach (var c in n.SceneChildren) stack.Push(c);
            }
        }

        /// <summary>The <c>di</c> region record of a model's definition, to copy into a new reference (null: none).</summary>
        internal static ZedTree.Record? DefinitionRegion(ZipperPackage pkg, ZipperPackage.ModelRef model)
        {
            if (pkg.FindDefinition(model) is not { } def || def.Node.Child("di") is not { } region) return null;
            var geo = pkg.Archive(def.Namespace, "geo");
            return geo == null ? null : ZedTree.Copy(ZedContainer.FromBytes(geo.Data).Nodes, region.Index);
        }

        // A node's di region swapped for another (or removed), as its last child like retail.
        private static void Replace(ZedContainer container, int node, ZedTree.Record? region)
        {
            foreach (int c in ZedTree.Children(container.Nodes, node).Where(c => container.Nodes[c].Name == "di").OrderByDescending(c => c).ToList())
                ZedTree.Remove(container, c);
            if (region != null) ZedTree.AppendChild(container, node, region);
        }

        private static bool Inside(float[] world, float[] area)
        {
            int n = world.Length / 3;
            float x = 0, z = 0;
            for (int i = 0; i < n; i++) { x += world[i * 3]; z += world[i * 3 + 2]; }
            x /= n; z /= n;
            return x >= area[0] && x <= area[2] && z >= area[1] && z <= area[3];
        }

        private static ZipperPackage.ModelRef Exact(ZipperPackage pkg, string modelName) =>
            pkg.ResolveModel(modelName) is { } m && m.Name.Equals(modelName, StringComparison.OrdinalIgnoreCase) ? m
                : throw new ArgumentException($"No model '{modelName}' in the package (see ASSET_LIST_MODELS)");
    }
}
