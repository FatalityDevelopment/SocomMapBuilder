using System.Text.RegularExpressions;
using CodeDesigner.Ps2.FileFormats.Zipper.Geo;
using CodeDesigner.Ps2.FileFormats.Zipper.Model;
using CodeDesigner.Ps2.FileFormats.Zipper.Texture;
using CodeDesigner.Ps2.FileFormats.Zipper.ZDB;
using CodeDesigner.Ps2.FileFormats.Zipper.ZED;

namespace CodeDesigner.Ps2.FileFormats.Zipper.Package
{
    /// <summary>
    /// A SOCOM II map package (a <c>.ZDB</c>, or a folder holding its unpacked files) indexed for editing: root
    /// archives by leaf name, parsed lazily; model assets by kind; GEO scenes by namespace (the archive prefix,
    /// <c>M51_GEO.ZED</c> → <c>m51</c>); textures resolved through their TEX0.CBP palette in their own TXR/PAL
    /// pair. Port of the webview's package handling (zipper-workbench) so bridge commands and MCP tools see the
    /// same assets as the editors.
    /// </summary>
    public sealed class ZipperPackage
    {
        private static readonly Regex ArchiveRx = new(@"^(?<ns>.+?)_(?<kind>MDL|TXR|PAL|GEO)(?:\([^)]+\))?\.ZED$", RegexOptions.IgnoreCase);
        private static readonly HashSet<string> SupportNamespaces = new(StringComparer.OrdinalIgnoreCase)
        {
            "weap", "flib", "clib", "effe", "alph", "font", "hud", "hud2", "hudw", "load", "junk", "objt",
        };

        private readonly Dictionary<string, RootEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ZedContainer> _containers = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, GeoScene> _scenes = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, List<ModelRef>>? _modelLookup;

        public string SourcePath { get; }
        public bool IsZdb { get; }
        /// <summary>The map's own namespace (<c>M51.ZDB</c> → <c>m51</c>), or null.</summary>
        public string? MapNamespace { get; }

        public sealed class RootEntry
        {
            public string Path { get; init; } = string.Empty;
            public string Leaf { get; init; } = string.Empty;
            public byte[] Data { get; set; } = Array.Empty<byte>();
            /// <summary>Archive prefix for *_MDL/TXR/PAL/GEO entries, lowercase; null otherwise.</summary>
            public string? Namespace { get; init; }
            /// <summary>"mdl", "txr", "pal", "geo" or null.</summary>
            public string? Kind { get; init; }
        }

        /// <summary>A model asset: a top-level record of a *_MDL archive (or WORL_MDL's worldmodel).</summary>
        public sealed class ModelRef
        {
            public string Archive { get; init; } = string.Empty;
            public string Namespace { get; init; } = string.Empty;
            public string Name { get; init; } = string.Empty;
            public int NodeIndex { get; init; }
            public int Size { get; init; }
            public int VisualCount { get; init; }
            /// <summary>weapon, character, world, sky, prop or library.</summary>
            public string Kind { get; init; } = string.Empty;
        }

        private ZipperPackage(string sourcePath, bool isZdb, IEnumerable<RootEntry> entries)
        {
            SourcePath = sourcePath;
            IsZdb = isZdb;
            foreach (RootEntry e in entries) _entries[e.Leaf] = e;
            string stem = System.IO.Path.GetFileNameWithoutExtension(sourcePath).ToLowerInvariant();
            MapNamespace = HasArchive(stem, "txr") ? stem
                : _entries.Values.Where(e => e.Kind == "geo" && e.Namespace != null && !SupportNamespaces.Contains(e.Namespace))
                    .Select(e => e.Namespace).FirstOrDefault(ns => ScenesHasWorld(ns!));
        }

        /// <summary>Opens a .ZDB, or a loose folder (or any file inside one) holding the unpacked package.</summary>
        public static ZipperPackage Open(string path)
        {
            // .ZDB packages and .CDB memory-card packs (the editors' pack export) share the archive reader.
            if (File.Exists(path) && ZdbArchive.IsArchivePath(path))
            {
                using ZdbArchive zdb = ZdbArchive.Open(path);
                return new ZipperPackage(path, true, zdb.Entries.Select(e => MakeEntry(e.FullPath, e.Data)).ToList());
            }
            string dir = Directory.Exists(path) ? path : System.IO.Path.GetDirectoryName(path) ?? path;
            if (!Directory.Exists(dir)) throw new FileNotFoundException("Package not found", path);
            var files = Directory.GetFiles(dir).Select(f => MakeEntry(f, File.ReadAllBytes(f))).ToList();
            string name = Directory.Exists(path) ? System.IO.Path.GetFileName(dir.TrimEnd('\\', '/')) : System.IO.Path.GetFileName(path);
            return new ZipperPackage(System.IO.Path.Combine(dir, name), false, files);
        }

        private static RootEntry MakeEntry(string fullPath, byte[] data)
        {
            string leaf = fullPath.Split('\\', '/').Last();
            Match m = ArchiveRx.Match(leaf);
            string? ns = m.Success ? m.Groups["ns"].Value.ToLowerInvariant() : leaf.Equals("WORL_MDL.ZED", StringComparison.OrdinalIgnoreCase) ? "worl" : null;
            string? kind = m.Success ? m.Groups["kind"].Value.ToLowerInvariant() : ns == "worl" ? "mdl" : null;
            return new RootEntry { Path = fullPath, Leaf = leaf, Data = data, Namespace = ns, Kind = kind };
        }

        public IReadOnlyCollection<RootEntry> Entries => _entries.Values;

        public RootEntry? Entry(string leaf) => _entries.TryGetValue(leaf, out var e) ? e : null;

        public bool HasArchive(string ns, string kind) =>
            _entries.Values.Any(e => e.Kind == kind && string.Equals(e.Namespace, ns, StringComparison.OrdinalIgnoreCase));

        public RootEntry? Archive(string ns, string kind) =>
            _entries.Values.FirstOrDefault(e => e.Kind == kind && string.Equals(e.Namespace, ns, StringComparison.OrdinalIgnoreCase));

        /// <summary>The parsed container of a root ZED/ZAR (cached; edits made to it are what Save writes).</summary>
        public ZedContainer Container(string leaf)
        {
            if (_containers.TryGetValue(leaf, out var c)) return c;
            RootEntry e = Entry(leaf) ?? throw new KeyNotFoundException($"No root entry {leaf}");
            c = ZedContainer.FromBytes(e.Data);
            _containers[leaf] = c;
            return c;
        }

        /// <summary>Re-reads a root entry from new bytes (after an edit) and drops caches that depend on it.</summary>
        public void ReplaceEntry(string leaf, byte[] data)
        {
            RootEntry e = Entry(leaf) ?? throw new KeyNotFoundException($"No root entry {leaf}");
            e.Data = data;
            _containers.Remove(leaf);
            if (e.Kind == "geo" && e.Namespace != null) _scenes.Remove(e.Namespace);
            if (e.Kind == "mdl") _modelLookup = null;
        }

        /// <summary>Root leaves whose container has been parsed (and possibly edited) since opening.</summary>
        public IEnumerable<string> LoadedContainers => _containers.Keys;

        // ------------------------------------------------------------------ scenes

        public GeoScene? Scene(string ns)
        {
            if (_scenes.TryGetValue(ns, out var s)) return s;
            RootEntry? geo = Archive(ns, "geo");
            if (geo == null) return null;
            s = GeoScene.Parse(Container(geo.Leaf).Nodes);
            _scenes[ns] = s;
            return s;
        }

        private bool ScenesHasWorld(string ns) => Scene(ns)?.FindModel("worldmodel") != null;

        /// <summary>The map scene: the map namespace's GEO when it has a worldmodel, else any that does.</summary>
        public (string Namespace, GeoScene Scene)? MapScene()
        {
            if (MapNamespace != null && Scene(MapNamespace) is { } own && own.FindModel("worldmodel") != null)
                return (MapNamespace, own);
            foreach (RootEntry e in _entries.Values.Where(e => e.Kind == "geo" && e.Namespace != null))
                if (Scene(e.Namespace!) is { } s && s.FindModel("worldmodel") != null) return (e.Namespace!, s);
            return null;
        }

        // ------------------------------------------------------------------ models

        public IReadOnlyList<ModelRef> Models()
        {
            var list = new List<ModelRef>();
            foreach (RootEntry e in _entries.Values.Where(e => e.Kind == "mdl").OrderBy(e => e.Leaf, StringComparer.OrdinalIgnoreCase))
            {
                ZedContainer c;
                try { c = Container(e.Leaf); } catch (InvalidDataException) { continue; }
                var nodes = c.Nodes;
                int i = 0;
                while (i < nodes.Count)
                {
                    ZedNode n = nodes[i];
                    int visuals = ZedModelFile.CollectChainOffsets(nodes, i).Count;
                    if (n.Data.Length > 0 || n.Type > 0)
                        list.Add(new ModelRef
                        {
                            Archive = e.Leaf,
                            Namespace = e.Namespace ?? string.Empty,
                            Name = n.Name,
                            NodeIndex = i,
                            Size = n.Data.Length,
                            VisualCount = visuals,
                            Kind = KindFor(e.Namespace ?? string.Empty, n.Name),
                        });
                    i = SkipSubtree(nodes, i);
                }
            }
            return list;
        }

        private string KindFor(string ns, string name)
        {
            if (ns == "weap") return "weapon";
            if (ns is "clib" or "flib" || name.StartsWith("MESH_", StringComparison.OrdinalIgnoreCase)) return "character";
            if (ns == "worl") return "world";
            if (Regex.IsMatch(name, "^(sky|thesun|flare|mist)", RegexOptions.IgnoreCase)) return "sky";
            if (ns.Equals(MapNamespace, StringComparison.OrdinalIgnoreCase)) return "prop";
            return "library";
        }

        public ModelRef? FindModel(string archiveLeaf, string name) =>
            Models().FirstOrDefault(m => m.Archive.Equals(archiveLeaf, StringComparison.OrdinalIgnoreCase) && m.Name == name);

        /// <summary>Resolves a scene or clutter name to a model by the loose name variants (GeoNames.Variants).</summary>
        public ModelRef? ResolveModel(string name)
        {
            if (_modelLookup == null)
            {
                _modelLookup = new Dictionary<string, List<ModelRef>>(StringComparer.Ordinal);
                foreach (ModelRef m in Models())
                    foreach (string k in GeoNames.Variants(m.Name))
                    {
                        if (!_modelLookup.TryGetValue(k, out var l)) _modelLookup[k] = l = new List<ModelRef>();
                        l.Add(m);
                    }
            }
            foreach (string k in GeoNames.Variants(name))
                if (_modelLookup.TryGetValue(k, out var hits) && hits.Count > 0)
                    return hits.FirstOrDefault(h => h.Kind is "prop" or "world") ?? hits[0];
            return null;
        }

        /// <summary>Decodes a model through its DMA chains (exact textures + vertex colours) for viewing.</summary>
        public ZedModelFile.Model DecodeModel(ModelRef model)
        {
            var nodes = Container(model.Archive).Nodes;
            byte[] bytes = nodes[model.NodeIndex].Data;
            return ZedModelFile.Parse(bytes, ZedModelFile.CollectChainOffsets(nodes, model.NodeIndex));
        }

        /// <summary>The GEO node defining a model's visuals: its own namespace's scene first, then any scene.</summary>
        public GeoNode? DefinitionNode(ModelRef model) => FindDefinition(model)?.Node;

        /// <summary>A character's GEO skeleton model by name: the mesh's own namespace first, then any GEO.</summary>
        public GeoNode? FindSkeleton(string preferredNamespace, string name)
        {
            if (Scene(preferredNamespace)?.FindModel(name) is { } own) return own;
            foreach (RootEntry e in _entries.Values.Where(e => e.Kind == "geo" && e.Namespace != null))
                if (Scene(e.Namespace!)?.FindModel(name) is { } any) return any;
            return null;
        }

        /// <summary>The defining GEO node and the namespace of the GEO archive that holds it.</summary>
        public (GeoNode Node, string Namespace)? FindDefinition(ModelRef model)
        {
            string ns = model.Namespace == "worl" ? MapScene()?.Namespace ?? model.Namespace : model.Namespace;
            if (Scene(ns)?.FindModel(model.Name) is { } own) return (own, ns);
            foreach (RootEntry e in _entries.Values.Where(e => e.Kind == "geo" && e.Namespace != null))
                if (Scene(e.Namespace!)?.FindModel(model.Name) is { } any) return (any, e.Namespace!);
            return null;
        }

        // ------------------------------------------------------------------ textures

        /// <summary>The TXR namespace a model's textures live in (WORL_MDL uses the map's pair).</summary>
        public string? TextureNamespace(string modelNamespace) =>
            modelNamespace == "worl" ? MapNamespace : HasArchive(modelNamespace, "txr") ? modelNamespace : null;

        /// <summary>
        /// The texdat bytes of <paramref name="tifName"/> and the namespace that holds them: the preferred
        /// namespace first, then any TXR archive.
        /// </summary>
        public (byte[] Texdat, string Namespace)? FindTexture(string tifName, string? preferredNs)
        {
            IEnumerable<RootEntry> txrs = _entries.Values.Where(e => e.Kind == "txr" && e.Namespace != null)
                .OrderBy(e => string.Equals(e.Namespace, preferredNs, StringComparison.OrdinalIgnoreCase) ? 0 : 1);
            foreach (RootEntry e in txrs)
            {
                ZedContainer c;
                try { c = Container(e.Leaf); } catch (InvalidDataException) { continue; }
                var nodes = c.Nodes;
                for (int i = 0; i < nodes.Count; i++)
                {
                    if (!nodes[i].Name.Equals(tifName, StringComparison.OrdinalIgnoreCase)) continue;
                    for (int j = i + 1; j < Math.Min(i + 4, nodes.Count); j++)
                        if (nodes[j].Name == "texdat" && nodes[j].Data.Length > 0) return (nodes[j].Data, e.Namespace!);
                }
            }
            return null;
        }

        /// <summary>Palette <c>texpal_&lt;id&gt;</c> from a namespace's PAL archive, or null.</summary>
        public ZedTextureFile.Palette? FindPalette(string ns, int id)
        {
            RootEntry? pal = Archive(ns, "pal");
            if (pal == null) return null;
            var nodes = Container(pal.Leaf).Nodes;
            string name = "texpal_" + id;
            for (int i = 0; i < nodes.Count; i++)
            {
                if (nodes[i].Name != name) continue;
                for (int j = i + 1; j < Math.Min(i + 4, nodes.Count); j++)
                    if (nodes[j].Name == "buf" && nodes[j].Data.Length >= 512) return ZedTextureFile.DecodeClutBuffer(nodes[j].Data, id);
            }
            return null;
        }

        /// <summary>
        /// Decodes a texture with the palette the game binds: TEX0.CBP in the namespace that holds the pixels,
        /// then CBP in any PAL archive.
        /// </summary>
        public (ZedTextureFile.DecodedTexture Texture, string Namespace)? DecodeTexture(string tifName, string? preferredNs)
        {
            var found = FindTexture(tifName, preferredNs);
            if (found == null) return null;
            var header = ZedTextureFile.ParseHeader(found.Value.Texdat);
            ZedTextureFile.Palette? palette = null;
            if (header.IsIndexed)
            {
                int id = header.ResolvePaletteId();
                palette = FindPalette(found.Value.Namespace, id)
                    ?? _entries.Values.Where(e => e.Kind == "pal" && e.Namespace != null).Select(e => FindPalette(e.Namespace!, id)).FirstOrDefault(p => p != null);
            }
            return (ZedTextureFile.Parse(found.Value.Texdat, palette), found.Value.Namespace);
        }

        private static int SkipSubtree(IReadOnlyList<ZedNode> nodes, int index)
        {
            int next = index + 1;
            for (uint c = 0; c < nodes[index].Type && next < nodes.Count; c++) next = SkipSubtree(nodes, next);
            return next;
        }
    }
}
