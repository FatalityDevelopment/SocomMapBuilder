using CodeDesigner.Ps2.FileFormats.Assets;
using CodeDesigner.Ps2.FileFormats.Zipper.Anim;
using CodeDesigner.Ps2.FileFormats.Zipper.Geo;
using CodeDesigner.Ps2.FileFormats.Zipper.Package;
using CodeDesigner.Ps2.FileFormats.Zipper.ZDB;
using CodeDesigner.Ps2.FileFormats.Zipper.ZED;

namespace MapBuilder.Server.Services
{
    /// <summary>
    /// Asset Viewer previews that are editors of their own: a GEO archive's scene nodes, a package's clutter
    /// placements and ZANIM effect scripts. GEO and ZANIM edits are VIEWER_EDIT ops on the record (nparams,
    /// modelName, zanimField, zanimWord); clutter goes through the package session's moveClutter / addClutter /
    /// deleteClutter, as in the Map Builder.
    /// </summary>
    internal sealed partial class ZipperAssetService
    {
        // ------------------------------------------------------------------ GEO

        private static object GeoPreview(Place place, List<(string, string)> facts)
        {
            byte[] data = place.Loaded ?? place.Bytes();
            var scene = GeoScene.Parse(data);
            int count = 0;
            List<object> Collect(GeoNode n) => n.IsSceneNode ? new List<object> { Node(n) } : n.Children.Where(c => c.Name != "visuals").SelectMany(Collect).ToList();
            object Node(GeoNode n)
            {
                count++;
                var p = n.Child("nparams")!;
                byte[] d = p.Data;
                float F(int w) => d.Length >= (w + 1) * 4 ? BitConverter.ToSingle(d, w * 4) : 0f;
                var reference = n.Child("model_name");
                return new
                {
                    name = n.Name,
                    nparams = p.Index,
                    matrix = Enumerable.Range(0, 16).Select(F).ToArray(),
                    min = new[] { F(16), F(17), F(18) },
                    max = new[] { F(19), F(20), F(21) },
                    kind = d.Length >= 96 ? BitConverter.ToInt32(d, 88) : 0,
                    flags = d.Length >= 96 ? BitConverter.ToUInt32(d, 92) : 0u,
                    modelName = reference == null ? null : new { node = reference.Index, text = GeoNames.Ascii(reference.Data) },
                    visuals = n.Visuals.Count,
                    children = n.Children.Where(c => c.Name is not ("visuals" or "nparams" or "model_name")).SelectMany(Collect).ToList(),
                };
            }
            var models = scene.Root.Children.SelectMany(Collect).ToList();
            facts.Add(("Definitions", models.Count.ToString()));
            facts.Add(("Scene nodes", count.ToString()));
            if (scene.FindModel("worldmodel") != null) facts.Add(("Map scene", "worldmodel"));
            return new { type = "geo", name = place.Name, models, facts = Facts(facts) };
        }

        // ------------------------------------------------------------------ clutter

        private static object ClutterPreview(Place place, List<(string, string)> facts)
        {
            // The package's own CLUTTER.ZAR edits through its session; one inside another archive is only shown.
            bool rootEntry = ZdbArchive.IsArchivePath(place.Path) ? place.Inner.Length == 1 : place.Inner.Length == 0;
            AssetSession? s = rootEntry ? ViewerSession(EditPackageOf(place.Path)) : null;
            List<ClutterFile.Placement> placements;
            List<string> models;
            if (s != null)
                lock (s.Gate)
                {
                    placements = ClutterFile.Parse(s.Package.Container(s.Package.Entry(PackageEdits.ClutterLeaf)?.Leaf ?? place.Name));
                    models = s.Package.Models().Where(m => m.Kind is "prop" or "library").Select(m => m.Name).ToList();
                }
            else
            {
                placements = ClutterFile.Parse(place.Loaded ?? place.Bytes());
                models = new List<string>();
            }
            facts.Add(("Models", placements.Select(p => p.ModelName).Distinct(StringComparer.OrdinalIgnoreCase).Count().ToString()));
            facts.Add(("Placements", placements.Count.ToString()));
            return new
            {
                type = "clutter", name = place.Name, session = s?.Id,
                placements = placements.Select(p => new
                {
                    model = p.ModelName,
                    position = new[] { p.Matrix[12], p.Matrix[13], p.Matrix[14] },
                    yaw = Matrix4.YawOf(p.Matrix),
                    scale = Matrix4.ScaleOf(p.Matrix),
                    // Placements turned about more than Y lose that tilt when yaw or scale is set.
                    tilted = MathF.Abs(p.Matrix[1]) > 1e-3f || MathF.Abs(p.Matrix[9]) > 1e-3f || MathF.Abs(p.Matrix[4]) > 1e-3f || MathF.Abs(p.Matrix[6]) > 1e-3f,
                    layout = p.Layout.ToString().ToLowerInvariant(),
                }).ToList(),
                models = placements.Select(p => p.ModelName).Concat(models).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(),
                facts = Facts(facts),
            };
        }

        // ------------------------------------------------------------------ ZANIM

        private static object ZanimPreview(Place place, ViewerRequest? req, List<(string, string)> facts)
        {
            var file = ZanimFile.Parse(place.Loaded ?? place.Bytes());
            facts.Add(("Effects", file.Animations.Count.ToString()));
            facts.Add(("Sets", file.Animations.Select(a => a.Set).Distinct().Count().ToString()));
            object? effect = null;
            if (req?.Effect is int e)
            {
                if (e < 0 || e >= file.Animations.Count) throw new KeyNotFoundException($"No effect #{e}");
                effect = EffectDetail(file.Animations[e]);
            }
            return new
            {
                type = "zanim", name = place.Name,
                effects = file.Animations.Select(a => new
                {
                    a.Name, a.Set,
                    tracks = a.Tracks.Count,
                    instructions = a.Tracks.Sum(t => t.Instructions.Count),
                    spawns = a.Spawns.Distinct().ToList(),
                }).ToList(),
                effect,
                facts = Facts(facts),
            };
        }

        private static object EffectDetail(ZanimFile.Animation a)
        {
            var objects = new List<object> { new { value = 0, label = "none" }, new { value = ZanimFile.ObjectSelf, label = "self" }, new { value = ZanimFile.ObjectOwner, label = "owner" } };
            objects.AddRange(a.NodeRefs.Select((r, i) => (object)new { value = i, label = $"{r.Name} (#{i})" }).Where((_, i) => i != 0 && i != ZanimFile.ObjectSelf && i != ZanimFile.ObjectOwner));
            return new
            {
                a.Name, a.Set,
                sequence = a.SequenceIndex,
                names = a.Names,
                objects,
                tracks = a.Tracks.Select(t => new
                {
                    t.Target,
                    instructions = t.Instructions.Select(i => new
                    {
                        i.Offset, i.Op, i.Name, i.Depth, i.Condition,
                        words = i.Words.Select(w => w.ToString("X8")).ToList(),
                        fields = ZanimFile.Layout(i.Op, a.Sequence, i.Offset, i.Words.Length * 4).Select(f => new
                        {
                            f.Name,
                            kind = f.Kind.ToString().ToLowerInvariant(),
                            value = ZanimFile.RawValue(a.Sequence, i.Offset, f),
                            shown = ZanimFile.Format(i.Fields.GetValueOrDefault(f.Name) ?? ""),
                            readOnly = f.ReadOnly,
                        }).ToList(),
                    }).ToList(),
                }).ToList(),
            };
        }
    }
}
