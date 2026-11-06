using System.Collections.Concurrent;
using CodeDesigner.Ps2.FileFormats.Ftb3;
using CodeDesigner.Ps2.FileFormats.Imaging;

namespace MapBuilder.Server.Services.Assets
{
    /// <summary>
    /// SOCOM: Fireteam Bravo 3 asset sessions: the ASSET_* commands over an <see cref="Ftb3Package"/> (the disc, its
    /// levels folder or one level). Each level is a map to pick (its "mission"); its models are named within the level,
    /// so the level travels as their archive. Placed props and game-mode points move (Ftb3AssetService.Edits.cs).
    /// </summary>
    internal static partial class Ftb3AssetService
    {
        internal sealed class Session
        {
            public required string Id { get; init; }
            public required Ftb3Package Package { get; init; }
            public object Gate { get; } = new();
            /// <summary>The level Map Builder draws.</summary>
            public required string Level { get; set; }
            public int Revision { get; set; }
            public Stack<Step> Undo { get; } = new();
            public Stack<Step> Redo { get; } = new();
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
                        "ASSET_HISTORY" => History(s),
                        "ASSET_EDIT" => Edit(s, c.AssetJson),
                        "ASSET_UNDO" => UndoRedo(s, true),
                        "ASSET_REDO" => UndoRedo(s, false),
                        "ASSET_SAVE" => Save(s, c.AssetJson),
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
            readOnly = false,
            isZdb = true,
            packageKind = "map",
            mapNamespace = s.Level,
            revision = s.Revision,
            location = s.Level,
            mission = s.Level,
            // Each level is a whole map to pick from.
            missions = s.Package.Levels.Keys.Select(l => new { id = l, kind = Ftb3Package.LevelKind(l), mode = "" }).ToList(),
            dirty = s.Package.DirtyNames(),
            canUndo = s.Undo.TryPeek(out var undo) ? undo.Description : null,
            canRedo = s.Redo.TryPeek(out var redo) ? redo.Description : null,
            entries = s.Package.Levels.Count,
            models = s.Package.Models(s.Level).Select(m => m.Kind).Concat(s.Package.Characters().Select(_ => "character"))
                .GroupBy(k => k).ToDictionary(g => g.Key, g => g.Count()),
        };

        private static string LevelOf(Session s, string? archive) =>
            string.IsNullOrEmpty(archive) ? s.Level : s.Package.Levels.ContainsKey(archive) ? archive : throw new KeyNotFoundException($"No level {archive}");

        private static Ftb3Package.Model Find(Session s, string? name, string? archive) =>
            string.IsNullOrEmpty(name) ? throw new ArgumentException("AssetName (the model) is required")
            : s.Package.FindModel(LevelOf(s, archive), name) ?? throw new KeyNotFoundException($"Model {name} not found");

        private static bool IsCharacters(string? archive) => string.Equals(archive, Ftb3Package.CharacterArchive, StringComparison.OrdinalIgnoreCase);

        /// <summary>A character's texture namespace: <c>characters/&lt;name&gt;</c> (its textures are in its own part files).</summary>
        private static string CharacterNamespace(string name) => $"{Ftb3Package.CharacterArchive}/{name}";

        private static Ftb3Package.Character FindCharacter(Session s, string? name) =>
            s.Package.FindCharacter(name ?? "") ?? throw new KeyNotFoundException($"Character {name} not found");

        /// <summary>The level's models, then the disc's characters (archive <c>characters</c>); an archive picks one.</summary>
        private static object ListModels(Session s, string? json)
        {
            var f = AssetJson.Parse<AssetJson.ModelFilter>(json) ?? new AssetJson.ModelFilter(null, null, null);
            bool Wanted(string name, string kind) => (f.Kind == null || kind.Equals(f.Kind, StringComparison.OrdinalIgnoreCase))
                && (f.Query == null || name.Contains(f.Query, StringComparison.OrdinalIgnoreCase));
            var list = new List<object>();
            if (!IsCharacters(f.Archive))
                list.AddRange(s.Package.Models(LevelOf(s, f.Archive)).Where(m => Wanted(m.Name, m.Kind))
                    .Select(m => (object)new { archive = m.Level, name = m.Name, kind = m.Kind, @namespace = m.Level, size = m.Vertices, visualCount = m.Pieces }));
            if (f.Archive == null || IsCharacters(f.Archive))
                list.AddRange(s.Package.Characters().Where(c => Wanted(c.Name, "character"))
                    .Select(c => (object)new { archive = Ftb3Package.CharacterArchive, name = c.Name, kind = "character", @namespace = CharacterNamespace(c.Name), size = 0, visualCount = c.Files.Count }));
            return list;
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
            if (IsCharacters(archive))
            {
                var c = FindCharacter(s, name);
                return AssetJson.Mesh(s.Package.BuildCharacter(c), Ftb3Package.CharacterArchive, c.Name, Game, arrays, textureNamespace: CharacterNamespace(c.Name));
            }
            var m = Find(s, name, archive);
            return AssetJson.Mesh(s.Package.BuildMesh(m), m.Level, m.Name, Game, arrays, textureNamespace: m.Level);
        }

        private sealed record PointArgs(string? Mission);

        /// <summary>Picks the level (ASSET_MAP_POINTS' mission) and lists its game-mode points: spawn controls, checkpoints,
        /// extraction zones and demolition targets (team 1 SEALs, 2 mercenaries). A point's y is its own height.</summary>
        private static object Points(Session s, string? json)
        {
            if (AssetJson.Parse<PointArgs>(json)?.Mission is { Length: > 0 } pick)
            {
                string level = LevelOf(s, pick);
                // Edits on other levels stay (and save); each undo step knows its own level.
                if (!level.Equals(s.Level, StringComparison.OrdinalIgnoreCase)) { s.Level = level; s.Revision++; }
            }
            var points = s.Package.Points(s.Level).Select(p => (object)new
            {
                name = p.Name, team = p.Team, kind = p.Kind, x = p.Matrix[12], y = (float?)p.Matrix[13], z = p.Matrix[14],
                cellX = 0, cellZ = 0, island = (string?)null, editable = false, canMove = true,
            }).ToList();
            return new { mission = s.Level, points, teamSpawns = Array.Empty<object>() };
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
            // caNode: moved by its instance id (moveInstance); multiMove: several move in one batch.
            object Item(string model, string instance, string path, float[] m, bool movable = false) => new
            {
                kind = "reference", model, instance, path, position = new[] { m[12], m[13], m[14] }, scale = 1f, matrix = m,
                geoIndex = (int?)null, clutterIndex = (int?)null, source = $"{s.Level}:{model}", caNode = movable, multiMove = movable,
            };
            var models = pkg.Models(s.Level);
            int props = 0, world = 0, sky = 0, markers = 0, copies = 0, overlapping = 0;
            foreach (var i in pkg.Instances(s.Level))
            {
                if (i.Model == null) { markers++; continue; }
                var m = pkg.FindModel(s.Level, i.Model)!;
                if (m.Kind == "sky") { sky++; instances.Add(Item(m.Name, i.Id, $"/sky/{i.Id}", i.Matrix)); continue; }
                if (m.Kind == "world")
                {
                    // A world tile is drawn where its vertices are (below); an instance of it is drawn as well only when
                    // it repeats the tile elsewhere.
                    if (!Ftb3Package.IsRepeat(pkg.BuildMesh(m), i.Matrix)) { overlapping++; continue; }
                    copies++;
                    instances.Add(Item(m.Name, i.Id, $"/props/{i.Id}", i.Matrix, movable: true));
                    continue;
                }
                props++;
                instances.Add(Item(m.Name, i.Id, $"/props/{i.Id}", i.Matrix, movable: true));
            }
            var placed = pkg.Instances(s.Level).Where(i => i.Model != null).Select(i => i.Model!).ToHashSet(StringComparer.Ordinal);
            foreach (var m in models.Where(m => m.Kind == "world" || m.Kind == "sky" && !placed.Contains(m.Name)))
            {
                if (m.Kind == "sky") sky++; else world++;
                instances.Add(Item(m.Name, m.Name, m.Kind == "sky" ? $"/sky/{m.Name}" : $"/world/{m.Name}", Identity));
            }
            return new
            {
                sceneNamespace = s.Level,
                counts = new Dictionary<string, int> { ["prop"] = props, ["world"] = world, ["copy"] = copies, ["sky"] = sky, ["not drawn"] = markers + overlapping },
                returned = instances.Count, instances, game = Game,
            };
        }

        /// <summary>A texture of a level, or of a character (<c>characters/&lt;name&gt;</c>).</summary>
        private static Ftb3Package.Texture? FindTexture(Session s, string name, string level) =>
            level.StartsWith(Ftb3Package.CharacterArchive + "/", StringComparison.OrdinalIgnoreCase)
                ? s.Package.FindCharacter(level[(Ftb3Package.CharacterArchive.Length + 1)..]) is { } c ? s.Package.FindCharacterTexture(c, name) : null
                : s.Package.FindTexture(level, name);

        private static object TextureInfo(Session s, string name, string level)
        {
            var t = FindTexture(s, name, level);
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
            if (IsCharacters(archive)) return DescribeCharacter(s, FindCharacter(s, name));
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

        private static object DescribeCharacter(Session s, Ftb3Package.Character c)
        {
            var mesh = s.Package.BuildCharacter(c);
            string ns = CharacterNamespace(c.Name);
            var chunks = mesh.Chunks.Select(ch => new
            {
                index = ch.Index, offset = ch.GeometryOffset, visual = ch.Visual, texture = ch.Texture, binding = "ftb3",
                vertices = ch.VertexCount, triangles = ch.Triangles.Length / 3, scale = ch.Scale, origin = ch.Origin, vertexColors = ch.Colors != null,
            }).ToList();
            return new
            {
                archive = Ftb3Package.CharacterArchive, name = c.Name, kind = "character", @namespace = ns, size = 0, nodeIndex = 0,
                textureNamespace = ns, geoArchive = (string?)null, fromChains = true, game = Game, skinned = true,
                visuals = chunks.Select(ch => ch.visual).Distinct().ToList(), chunks,
                textures = mesh.Chunks.Select(ch => ch.Texture).Where(t => t != null).Distinct().Select(t => TextureInfo(s, t!, ns)).ToList(),
                geo = (object?)null,
            };
        }

        private static object Texture(Session s, string? name, string? archive)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("AssetName (the texture) is required");
            string level = archive?.StartsWith(Ftb3Package.CharacterArchive + "/", StringComparison.OrdinalIgnoreCase) == true ? archive : LevelOf(s, archive);
            var t = FindTexture(s, name, level) ?? throw new KeyNotFoundException($"Texture '{name}' not found");
            return new { info = TextureInfo(s, name, level), png = Convert.ToBase64String(PngEncoder.EncodeRgba(t.Rgba, t.Width, t.Height)) };
        }
    }
}
