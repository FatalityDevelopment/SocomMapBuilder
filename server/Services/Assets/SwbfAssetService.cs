using System.Collections.Concurrent;
using CodeDesigner.Ps2.FileFormats.Imaging;
using CodeDesigner.Ps2.FileFormats.Swbf;

namespace MapBuilder.Server.Services.Assets
{
    /// <summary>
    /// Star Wars: Battlefront asset sessions: the ASSET_* commands over a <see cref="SwbfPackage"/> (the disc image or
    /// a loose .LVL). Read-only: models (shown in their bind pose) and textures. Models and textures are named within
    /// their level, so a model's level travels as its archive (TAT/TAT2.LVL) and as its meshes' texture namespace.
    /// </summary>
    internal static class SwbfAssetService
    {
        internal sealed class Session
        {
            public required string Id { get; init; }
            public required SwbfPackage Package { get; init; }
            public object Gate { get; } = new();
            /// <summary>The map (a world's level, e.g. TAT/TAT2.LVL) Map Builder draws; null on a level with none.</summary>
            public string? Map { get; set; }
            public int Revision { get; set; }
        }

        private static readonly ConcurrentDictionary<string, Session> Sessions = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object OpenGate = new();

        public const string Game = "Star Wars: Battlefront";

        /// <summary>ASSET_OPEN for a Battlefront disc image or .LVL; null when the path is not one.</summary>
        public static string? TryOpen(string full)
        {
            if (!SwbfPackage.IsSwbfPath(full)) return null;
            var session = SessionFor(full, out bool reused);
            return AssetJson.Ok("ASSET_OPEN", new { reused, status = Status(session) });
        }

        /// <summary>The session for a disc image or .LVL, opened when there is none.</summary>
        public static Session SessionFor(string full, out bool reused)
        {
            lock (OpenGate)
            {
                Session? existing = Sessions.Values.FirstOrDefault(s => s.Package.SourcePath.Equals(full, StringComparison.OrdinalIgnoreCase));
                reused = existing != null;
                Session session = existing ?? new Session { Id = AssetJson.SessionId(full), Package = SwbfPackage.Open(full) };
                session.Map ??= session.Package.Worlds.FirstOrDefault()?.Archive;
                Sessions[session.Id] = session;
                return session;
            }
        }

        public static bool Close(string id) => Sessions.TryRemove(id, out _);

        public static IEnumerable<object> Statuses() => Sessions.Values.Select(Status);

        /// <summary>Answers <paramref name="command"/> when the message names a Battlefront session; null otherwise.</summary>
        public static string? Route(CommandMessage c, string command)
        {
            if (c.AssetSession == null || !Sessions.TryGetValue(c.AssetSession, out var s)) return null;
            try
            {
                object result;
                lock (s.Gate)
                {
                    result = command switch
                    {
                        "ASSET_STATUS" => Status(s),
                        "ASSET_LIST_MODELS" => ListModels(s, c.AssetJson),
                        "ASSET_DESCRIBE_MODEL" => Describe(s, c.AssetName, c.AssetArchive),
                        "ASSET_MODEL_MESH" => Mesh(s, c.AssetName, c.AssetArchive, AssetJson.Parse<AssetJson.MeshArgs>(c.AssetJson)?.Arrays ?? true),
                        "ASSET_TEXTURE" => Texture(s, c.AssetName, c.AssetArchive),
                        "ASSET_LIST_TEXTURES" => ListTextures(s, c.AssetArchive),
                        "ASSET_MAP_POINTS" => Points(s, c.AssetJson),
                        "ASSET_MAP_SCENE" => MapScene(s),
                        "ASSET_MAP_TABLES" => new { revision = s.Revision, missionId = s.Map, hasReader = false, objectives = Array.Empty<object>(), actors = Array.Empty<object>(),
                            loadoutSource = (string?)null, mission = (object?)null, world = (object?)null, extents = (object?)null, cams = Array.Empty<object>(),
                            valves = Array.Empty<object>(), texts = new Dictionary<string, string>() },
                        "ASSET_MAP_HUD" => new { revision = s.Revision, map = s.Map, icons = new Dictionary<string, object>(), missing = Array.Empty<string>(), tacmap = (object?)null },
                        "ASSET_HISTORY" => new { revision = s.Revision, undo = Array.Empty<object>(), redo = Array.Empty<object>(), dirty = Array.Empty<string>() },
                        "ASSET_VALIDATE" => new { ok = true, counts = new Dictionary<string, int>(), instances = 0, textures = 0, textureBytes = 0, problems = Array.Empty<object>() },
                        "ASSET_LIST_ANIMATIONS" => new { packageArchive = (string?)null, sharedPath = (string?)null, clips = Array.Empty<object>() },
                        "ASSET_WEAPONS" => Array.Empty<object>(),
                        "ASSET_WEAPON_FX" => new { weapon = c.AssetName, effect = (object?)null },
                        "ASSET_EFFECTS" => Array.Empty<object>(),
                        _ => throw new NotSupportedException($"{Game} sessions are read-only for now ({command} is not available)"),
                    };
                }
                return AssetJson.Ok(command, result);
            }
            catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException or InvalidOperationException or InvalidDataException or NotSupportedException or IOException)
            {
                return Responses.Error($"{command}: {ex.Message}");
            }
        }

        private static object Status(Session s) => new
        {
            session = s.Id,
            path = s.Package.SourcePath,
            game = Game,
            readOnly = true,
            isZdb = true,
            packageKind = s.Package.Worlds.Count > 0 ? "map" : "assets",
            mapNamespace = s.Map == null ? null : s.Package.WorldsIn(s.Map).FirstOrDefault()?.Name,
            revision = s.Revision,
            location = s.Map,
            mission = s.Map,
            // Each map level as a "mission" to pick from; a level's hero (_H) copy is its own entry.
            missions = s.Package.Worlds.Select(w => w.Archive).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(a => new { id = a, kind = "", mode = "" }).ToList(),
            dirty = Array.Empty<string>(),
            canUndo = (string?)null,
            canRedo = (string?)null,
            entries = s.Package.Levels.Count,
            levels = s.Package.Levels.Count,
            textures = s.Package.Textures.Count,
            models = s.Package.Models.GroupBy(m => m.Kind).ToDictionary(g => g.Key, g => g.Count()),
        };

        private static SwbfPackage.Model Find(Session s, string? name, string? archive) =>
            string.IsNullOrEmpty(name) ? throw new ArgumentException("AssetName (the model) is required")
            : s.Package.FindModel(name, archive) ?? throw new KeyNotFoundException($"Model {name} not found");

        private static object ListModels(Session s, string? json)
        {
            var f = AssetJson.Parse<AssetJson.ModelFilter>(json) ?? new AssetJson.ModelFilter(null, null, null);
            return s.Package.Models
                .Where(m => (f.Kind == null || m.Kind.Equals(f.Kind, StringComparison.OrdinalIgnoreCase))
                    && (f.Query == null || m.Name.Contains(f.Query, StringComparison.OrdinalIgnoreCase) || m.Archive.Contains(f.Query, StringComparison.OrdinalIgnoreCase)))
                .Select(m => new { archive = m.Archive, name = m.Name, kind = m.Kind, @namespace = m.Archive, size = m.Size, visualCount = m.Segments })
                .ToList();
        }

        private static object ListTextures(Session s, string? archive) =>
            s.Package.Textures
                .Where(t => string.IsNullOrEmpty(archive) || t.Archive.Equals(archive.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
                .Select(t => new { name = t.Name, archive = t.Archive, @namespace = t.Archive, width = t.Width, height = t.Height, bpp = t.Depth, size = t.Size })
                .ToList();

        private static object Mesh(Session s, string? name, string? archive, bool arrays)
        {
            if (name != null && name.StartsWith(SwbfPackage.TerrainPrefix, StringComparison.OrdinalIgnoreCase))
            {
                string level = archive ?? s.Map ?? throw new ArgumentException("AssetArchive (the terrain's level) is required");
                var terrain = s.Package.BuildTerrainMesh(level, name) ?? throw new KeyNotFoundException($"Terrain {name[SwbfPackage.TerrainPrefix.Length..]} not found");
                return AssetJson.Mesh(terrain, terrain.Archive, name, Game, arrays, textureNamespace: terrain.Archive);
            }
            var m = Find(s, name, archive);
            return AssetJson.Mesh(s.Package.BuildMesh(m), m.Archive, m.Name, Game, arrays, textureNamespace: m.Archive);
        }

        private sealed record PointArgs(string? Mission);

        /// <summary>Picks the map (ASSET_MAP_POINTS' mission); spawn points and regions come with wave 3.</summary>
        private static object Points(Session s, string? json)
        {
            if (AssetJson.Parse<PointArgs>(json)?.Mission is { Length: > 0 } pick)
            {
                var world = s.Package.WorldsIn(pick).FirstOrDefault() ?? throw new KeyNotFoundException($"No map in {pick}");
                if (!world.Archive.Equals(s.Map, StringComparison.OrdinalIgnoreCase)) { s.Map = world.Archive; s.Revision++; }
            }
            return new { mission = s.Map, points = Array.Empty<object>(), teamSpawns = Array.Empty<object>() };
        }

        private static readonly float[] Identity = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };

        /// <summary>
        /// The selected map: its terrain (one generated model, <see cref="SwbfPackage.TerrainPrefix"/>), the level's sky
        /// models at the origin, and each world instance that draws a model, placed by its stored matrix (row vectors,
        /// translation in [12..14], as the Map Builder reads them). Instances with nothing to draw (sound emitters,
        /// vehicle spawns, particle effects) are counted but not listed.
        /// </summary>
        private static object MapScene(Session s)
        {
            if (s.Map == null) throw new InvalidOperationException("This level has no map (no world). Open the disc or a map level such as TAT/TAT2.LVL.");
            var pkg = s.Package;
            var instances = new List<object>();
            object Item(string kind, string model, string instance, string path, float[] m, string source) => new
            {
                kind, model, instance, path, position = new[] { m[12], m[13], m[14] }, scale = 1f, matrix = m,
                geoIndex = (int?)null, clutterIndex = (int?)null, source,
            };
            var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int notDrawn = 0, props = 0, terrains = 0;
            float mapHalf = 0;
            string? worldName = null;
            foreach (var w in pkg.WorldsIn(s.Map))
            {
                worldName ??= w.Name;
                var data = pkg.ReadWorld(w);
                if (data.Terrain != null && pkg.Terrain(data.Terrain, w.Archive) is { } t && placed.Add(SwbfPackage.TerrainPrefix + t.Name))
                {
                    terrains++;
                    mapHalf = Math.Max(mapHalf, t.Size * t.GridUnit / 2);
                    instances.Add(Item("model", "terrain " + t.Name, "terrain", "/terrain", Identity, $"{w.Archive}:{SwbfPackage.TerrainPrefix}{t.Name}"));
                }
                int i = 0;
                foreach (var r in data.Instances)
                {
                    int n = i++;
                    if (pkg.ModelForClass(r.Type, w.Level) is not { } m) { notDrawn++; continue; }
                    placed.Add(m.Name);
                    props++;
                    string id = r.Name.Length > 0 ? r.Name : $"{r.Type}#{n}";
                    instances.Add(Item("reference", m.Name, id, $"/{w.Name}/{r.Type}/{id}", r.Matrix, $"{m.Archive}:{m.Name}"));
                }
            }
            // The game draws its sky models in a few-metre dome space that follows the camera; here they are scaled
            // once so the dome encloses the whole map (its corners, with room to spare), centred on the origin.
            int level = pkg.LevelIndex(s.Map);
            int skies = 0;
            float reach = mapHalf > 0 ? mapHalf * 1.6f : 1500f;
            foreach (var m in pkg.Models.Where(m => m.Level == level && m.Kind == "sky" && !placed.Contains(m.Name)))
            {
                var mesh = pkg.BuildMesh(m);
                float r = new[] { mesh.BoundsMin[0], mesh.BoundsMin[2], mesh.BoundsMax[0], mesh.BoundsMax[2] }.Max(MathF.Abs);
                float k = r > 0 ? reach / r : 1;
                skies++;
                instances.Add(Item("model", m.Name, m.Name, "/sky/" + m.Name, new[] { k, 0, 0, 0, 0, k, 0, 0, 0, 0, k, 0, 0, 0, 0, 1f }, $"{m.Archive}:{m.Name}"));
            }
            return new
            {
                sceneNamespace = worldName ?? s.Map,
                counts = new Dictionary<string, int> { ["terrain"] = terrains, ["prop"] = props, ["sky"] = skies, ["not drawn"] = notDrawn },
                returned = instances.Count, instances, game = Game,
            };
        }

        private static object TextureInfo(Session s, string name, string? archive)
        {
            var r = s.Package.FindTextureRef(name, archive);
            if (r == null) return new { name, found = false };
            var t = s.Package.FindTexture(name, archive)!;
            return new
            {
                name, found = true, @namespace = r.Archive, width = t.Width, height = t.Height,
                psm = t.Depth switch { 8 => "0x13", 4 => "0x14", _ => "0x00" }, bpp = t.Depth,
                palette = (int?)null, paletteResolved = true, hasTransparency = t.HasAlpha,
                alphaTest = t.HasAlpha ? 0.5f : (float?)null, alphaBlend = (bool?)null, clamp = (bool[]?)null,
                mips = t.Mips, archive = r.Archive,
            };
        }

        private static object Describe(Session s, string? name, string? archive)
        {
            var m = Find(s, name, archive);
            var mesh = s.Package.BuildMesh(m);
            var chunks = mesh.Chunks.Select(c => new
            {
                index = c.Index, offset = c.GeometryOffset, visual = c.Visual, texture = c.Texture, binding = "swbf",
                vertices = c.VertexCount, triangles = c.Triangles.Length / 3, scale = c.Scale, origin = c.Origin, vertexColors = c.Colors != null,
            }).ToList();
            return new
            {
                archive = m.Archive, name = m.Name, kind = m.Kind, @namespace = m.Archive, size = m.Size, nodeIndex = 0,
                textureNamespace = m.Archive, geoArchive = (string?)null, fromChains = true, game = Game, skinned = m.Skinned,
                visuals = chunks.Select(c => c.visual).Distinct().ToList(), chunks,
                textures = mesh.Chunks.Select(c => c.Texture).Where(t => t != null).Distinct().Select(t => TextureInfo(s, t!, m.Archive)).ToList(),
                geo = (object?)null,
            };
        }

        private static object Texture(Session s, string? name, string? archive)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("AssetName (the texture) is required");
            var t = s.Package.FindTexture(name, archive) ?? throw new KeyNotFoundException($"Texture '{name}' not found");
            return new { info = TextureInfo(s, name, archive), png = Convert.ToBase64String(PngEncoder.EncodeRgba(t.Rgba, t.Width, t.Height)) };
        }
    }
}
