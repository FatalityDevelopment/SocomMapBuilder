using CodeDesigner.Ps2.FileFormats.Assets;
namespace CodeDesigner.Ps2.FileFormats.Zipper.Geo
{
    /// <summary>
    /// What a map scene places, port of collectSceneInstances (zed-scene-compose.ts). Walking from
    /// <see cref="GeoScene.MapRoot"/>: a node that is itself a model (worldmodel) is placed once and covers its
    /// descendants; a <c>model_name</c> reference is always placed; other nodes with visuals are tried by name.
    /// Clutter placements are appended by the caller (<see cref="ClutterFile"/>).
    /// </summary>
    public static class SceneInstances
    {
        public sealed class Instance
        {
            /// <summary>model, reference, visuals or clutter.</summary>
            public string Kind { get; init; } = string.Empty;
            public string ModelName { get; init; } = string.Empty;
            public string InstanceName { get; init; } = string.Empty;
            /// <summary>Slash path of scene node names (clutter: /clutter/&lt;model&gt;/&lt;n&gt;).</summary>
            public string Path { get; init; } = string.Empty;
            /// <summary>World transform, column-major.</summary>
            public float[] Matrix { get; init; } = Matrix4.Identity();
            /// <summary>The placing GEO node (null for clutter).</summary>
            public GeoNode? Node { get; init; }
            /// <summary>Clutter placement index in <see cref="ClutterFile.Parse(byte[])"/> order, else -1.</summary>
            public int ClutterIndex { get; init; } = -1;
            public bool Resolved { get; init; }
        }

        public static List<Instance> Collect(GeoNode root, Func<string, bool> resolves)
        {
            var outList = new List<Instance>();

            void Visit(GeoNode node, float[] parent, string trail, bool covered)
            {
                bool isScene = node.IsSceneNode;
                float[] matrix = isScene ? Matrix4.Multiply(parent, node.Transform) : parent;
                string path = isScene ? $"{trail}/{node.Name}" : trail;
                bool coveredBelow = covered;
                if (isScene)
                {
                    string name = node.CleanName;
                    string reference = node.ModelReference;
                    if (reference.Length > 0 && resolves(reference))
                        outList.Add(new Instance { Kind = "reference", ModelName = reference, InstanceName = name, Path = path, Matrix = matrix, Node = node, Resolved = true });
                    else if (!covered && resolves(name))
                    {
                        outList.Add(new Instance { Kind = "model", ModelName = name, InstanceName = name, Path = path, Matrix = matrix, Node = node, Resolved = true });
                        coveredBelow = true;
                    }
                    else if (!covered && node.HasVisuals)
                        outList.Add(new Instance { Kind = "visuals", ModelName = name, InstanceName = name, Path = path, Matrix = matrix, Node = node });
                    else if (reference.Length > 0 && !covered)
                        outList.Add(new Instance { Kind = "reference", ModelName = reference, InstanceName = name, Path = path, Matrix = matrix, Node = node });
                }
                foreach (GeoNode c in node.Children)
                {
                    if (c.Name is "nparams" or "visuals" or "model_name" or "di") continue;
                    Visit(c, matrix, path, coveredBelow);
                }
            }

            Visit(root, Matrix4.Identity(), string.Empty, false);
            return outList;
        }

        public static IEnumerable<Instance> FromClutter(IReadOnlyList<ClutterFile.Placement> placements, Func<string, bool> resolves)
        {
            var counters = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < placements.Count; i++)
            {
                var p = placements[i];
                counters.TryGetValue(p.ModelName, out int n);
                counters[p.ModelName] = n + 1;
                yield return new Instance
                {
                    Kind = "clutter",
                    ModelName = p.ModelName,
                    InstanceName = p.ModelName,
                    Path = $"/clutter/{p.ModelName}/{n}",
                    Matrix = p.Matrix,
                    ClutterIndex = i,
                    Resolved = resolves(p.ModelName),
                };
            }
        }
    }
}
