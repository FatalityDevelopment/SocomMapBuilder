using System.Collections.Concurrent;
using CodeDesigner.Ps2.FileFormats.Ftb3;
using CodeDesigner.Ps2.FileFormats.Imaging;

namespace MapBuilder.Server.Services.Assets
{
    /// <summary>
    /// SOCOM: Fireteam Bravo 3 asset sessions, read-only: the ASSET_* commands over an <see cref="Ftb3Package"/> (the
    /// disc, its levels folder or one level). Each level is a map to pick (its "mission"); its models are named within
    /// the level, so the level travels as their archive.
    /// </summary>
    internal static class Ftb3AssetService
    {
        internal sealed class Session
        {
            public required string Id { get; init; }
            public required Ftb3Package Package { get; init; }
            public object Gate { get; } = new();
            /// <summary>The level Map Builder draws.</summary>
            public required string Level { get; set; }
            public int Revision { get; set; }
        }

        private static readonly ConcurrentDictionary<string, Session> Sessions = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object OpenGate = new();

        public const string Game = Ftb3Package.Game;

        public static string? SourcePathOf(string id) => Sessions.TryGetValue(id, out var s) ? s.Package.SourcePath : null;

        /// <summary>ASSET_OPEN for a Fireteam Bravo 3 disc, levels folder, level folder or .ssg; null when the path is
        /// none of these. A level path opens that level.</summary>
        public static string? TryOpen(string full)
        {
            if (!Ftb3Package.IsFtb3Path(full)) return null;
            lock (OpenGate)
            {
                var existing = Sessions.Values.FirstOrDefault(s => s.Package.SourcePath.Equals(full, StringComparison.OrdinalIgnoreCase));
                var session = existing ?? new Session { Id = AssetJson.SessionId(full), Package = Ftb3Package.Open(full), Level = "" };
                if (existing == null) session.Level = session.Package.Levels.Keys.First();
                Sessions[session.Id] = session;
                return AssetJson.Ok("ASSET_OPEN", new { reused = existing != null, status = Status(session) });
            }
        }

        public static bool Close(string id) => Sessions.TryRemove(id, out _);

        public static IEnumerable<object> Statuses() => Sessions.Values.Select(Status);

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
                        "ASSET_MAP_TABLES" => new { revision = s.Revision, missionId = s.Level, hasReader = false, objectives = Array.Empty<object>(), actors = Array.Empty<object>(),
                            loadoutSource = (string?)null, mission = (object?)null, world = (object?)null, extents = (object?)null, cams = Array.Empty<object>(),
                            valves = Array.Empty<object>(), texts = new Dictionary<string, string>() },
                        "ASSET_MAP_HUD" => new { revision = s.Revision, map = s.Level, icons = new Dictionary<string, object>(), missing = Array.Empty<string>(), tacmap = (object?)null },
                        "ASSET_HISTORY" => new { revision = s.Revision, undo = Array.Empty<object>(), redo = Array.Empty<object>(), dirty = Array.Empty<string>() },
                        "ASSET_EDIT" or "ASSET_SAVE" => throw new NotSupportedException($"{Game} levels are read-only for now."),
                        "ASSET_UNDO" => throw new InvalidOperationException("Nothing to undo"),
                        "ASSET_REDO" => throw new InvalidOperationException("Nothing to redo"),
                        "ASSET_VALIDATE" => new { ok = true, counts = new Dictionary<string, int>(), instances = 0, textures = 0, textureBytes = 0, problems = Array.Empty<object>() },
                        "ASSET_LIST_ANIMATIONS" => new { packageArchive = (string?)null, sharedPath = (string?)null, clips = Array.Empty<object>() },
                        "ASSET_WEAPONS" => Array.Empty<object>(),
                        "ASSET_WEAPON_FX" => new { weapon = c.AssetName, effect = (object?)null },
                        "ASSET_EFFECTS" => Array.Empty<object>(),
                        _ => throw new NotSupportedException($"{command} is not available for {Game}"),
                    };
                }
                return AssetJson.Ok(command, result);
            }
            catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException or InvalidOperationException or InvalidDataException or NotSupportedException or IOException
                or System.Text.Json.JsonException or UnauthorizedAccessException or FormatException)
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
            packageKind = "map",
            mapNamespace = s.Level,
            revision = s.Revision,
            location = s.Level,
            mission = s.Level,
            // Each level is a whole map to pick from.
            missions = s.Package.Levels.Keys.Select(l => new { id = l, kind = Ftb3Package.LevelKind(l), mode = "" }).ToList(),
            dirty = Array.Empty<string>(),
            canUndo = (string?)null,
            canRedo = (string?)null,
            entries = s.Package.Levels.Count,
            models = s.Package.Models(s.Level).GroupBy(m => m.Kind).ToDictionary(g => g.Key, g => g.Count()),
        };

        private static string LevelOf(Session s, string? archive) =>
            string.IsNullOrEmpty(archive) ? s.Level : s.Package.Levels.ContainsKey(archive) ? archive : throw new KeyNotFoundException($"No level {archive}");

        private static Ftb3Package.Model Find(Session s, string? name, string? archive) =>
            string.IsNullOrEmpty(name) ? throw new ArgumentException("AssetName (the model) is required")
            : s.Package.FindModel(LevelOf(s, archive), name) ?? throw new KeyNotFoundException($"Model {name} not found");

        private static object ListModels(Session s, string? json)
        {
            var f = AssetJson.Parse<AssetJson.ModelFilter>(json) ?? new AssetJson.ModelFilter(null, null, null);
            return s.Package.Models(LevelOf(s, f.Archive))
                .Where(m => (f.Kind == null || m.Kind.Equals(f.Kind, StringComparison.OrdinalIgnoreCase))
                    && (f.Query == null || m.Name.Contains(f.Query, StringComparison.OrdinalIgnoreCase)))
                .Select(m => new { archive = m.Level, name = m.Name, kind = m.Kind, @namespace = m.Level, size = m.Vertices, visualCount = m.Pieces })
                .ToList();
        }

        private static object ListTextures(Session s, string? archive)
        {
            string level = LevelOf(s, archive);
            return s.Package.TextureNames(level).Select(n => s.Package.FindTexture(level, n)).Where(t => t != null)
                .Select(t => new { name = t!.Name, archive = level, @namespace = level, width = t.Width, height = t.Height, bpp = t.Depth, size = t.Rgba.Length })
                .ToList();
        }

        private static object Mesh(Session s, string? name, string? archive, bool arrays)
        {
            var m = Find(s, name, archive);
            return AssetJson.Mesh(s.Package.BuildMesh(m), m.Level, m.Name, Game, arrays, textureNamespace: m.Level);
        }

        private sealed record PointArgs(string? Mission);

        /// <summary>Picks the level (ASSET_MAP_POINTS' mission). Its points (spawns, objectives) are not read yet.</summary>
        private static object Points(Session s, string? json)
        {
            if (AssetJson.Parse<PointArgs>(json)?.Mission is { Length: > 0 } pick)
            {
                string level = LevelOf(s, pick);
                if (!level.Equals(s.Level, StringComparison.OrdinalIgnoreCase)) { s.Level = level; s.Revision++; }
            }
            return new { mission = s.Level, points = new List<object>(), teamSpawns = Array.Empty<object>() };
        }

        private static readonly float[] Identity = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };

        /// <summary>
        /// The level: each instance that draws a model, placed by its matrix; then the models no instance places (terrain
        /// tiles, water, buildings built in place), whose vertices are already where the level has them; then the sky
        /// dome and parallax scenery. LOD copies are left out.
        /// </summary>
        private static object MapScene(Session s)
        {
            var pkg = s.Package;
            var instances = new List<object>();
            object Item(string model, string instance, string path, float[] m) => new
            {
                kind = "reference", model, instance, path, position = new[] { m[12], m[13], m[14] }, scale = 1f, matrix = m,
                geoIndex = (int?)null, clutterIndex = (int?)null, source = $"{s.Level}:{model}", caNode = false, multiMove = false,
            };
            var models = pkg.Models(s.Level);
            int props = 0, world = 0, sky = 0, markers = 0;
            foreach (var i in pkg.Instances(s.Level))
            {
                if (i.Model == null) { markers++; continue; }
                var m = pkg.FindModel(s.Level, i.Model)!;
                if (m.Kind == "sky") { sky++; instances.Add(Item(m.Name, i.Id, $"/sky/{i.Id}", i.Matrix)); continue; }
                props++;
                instances.Add(Item(m.Name, i.Id, $"/props/{i.Id}", i.Matrix));
            }
            var placed = pkg.Instances(s.Level).Where(i => i.Model != null).Select(i => i.Model!).ToHashSet(StringComparer.Ordinal);
            foreach (var m in models.Where(m => !placed.Contains(m.Name) && m.Kind is "world" or "sky"))
            {
                if (m.Kind == "sky") sky++; else world++;
                instances.Add(Item(m.Name, m.Name, m.Kind == "sky" ? $"/sky/{m.Name}" : $"/world/{m.Name}", Identity));
            }
            return new
            {
                sceneNamespace = s.Level,
                counts = new Dictionary<string, int> { ["prop"] = props, ["world"] = world, ["sky"] = sky, ["not drawn"] = markers },
                returned = instances.Count, instances, game = Game,
            };
        }

        private static object TextureInfo(Session s, string name, string level)
        {
            var t = s.Package.FindTexture(level, name);
            if (t == null) return new { name, found = false };
            return new
            {
                name, found = true, @namespace = level, width = t.Width, height = t.Height,
                psm = t.Depth == 8 ? "0x13" : "0x14", bpp = t.Depth,
                // Foliage, fences and wires fade out over soft 0-255 alphas (a conifer averages 55): blended with a low
                // cut-out, as a GE alpha test would, rather than halved at 0.5, which strips the trees to their trunks.
                palette = (int?)null, paletteResolved = true, hasTransparency = t.HasAlpha,
                alphaTest = t.HasAlpha ? 0.1f : (float?)null, alphaBlend = t.HasAlpha ? true : (bool?)null, clamp = (bool[]?)null,
                mips = 1, archive = level,
            };
        }

        private static object Describe(Session s, string? name, string? archive)
        {
            var m = Find(s, name, archive);
            var mesh = s.Package.BuildMesh(m);
            var chunks = mesh.Chunks.Select(c => new
            {
                index = c.Index, offset = c.GeometryOffset, visual = c.Visual, texture = c.Texture, binding = "ftb3",
                vertices = c.VertexCount, triangles = c.Triangles.Length / 3, scale = c.Scale, origin = c.Origin, vertexColors = c.Colors != null,
            }).ToList();
            return new
            {
                archive = m.Level, name = m.Name, kind = m.Kind, @namespace = m.Level, size = m.Vertices, nodeIndex = 0,
                textureNamespace = m.Level, geoArchive = (string?)null, fromChains = true, game = Game, skinned = false,
                visuals = chunks.Select(c => c.visual).Distinct().ToList(), chunks,
                textures = mesh.Chunks.Select(c => c.Texture).Where(t => t != null).Distinct().Select(t => TextureInfo(s, t!, m.Level)).ToList(),
                geo = (object?)null,
            };
        }

        private static object Texture(Session s, string? name, string? archive)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("AssetName (the texture) is required");
            string level = LevelOf(s, archive);
            var t = s.Package.FindTexture(level, name) ?? throw new KeyNotFoundException($"Texture '{name}' not found");
            return new { info = TextureInfo(s, name, level), png = Convert.ToBase64String(PngEncoder.EncodeRgba(t.Rgba, t.Width, t.Height)) };
        }
    }
}
