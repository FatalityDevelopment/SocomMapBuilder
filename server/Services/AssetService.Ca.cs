using CodeDesigner.Ps2.FileFormats.Assets;
using System.Collections.Concurrent;
using System.Text.Json;
using CodeDesigner.Ps2.FileFormats.Imaging;
using CodeDesigner.Ps2.FileFormats.Zipper.Ca;
using CodeDesigner.Ps2.FileFormats.Zipper.Conversion;
using CodeDesigner.Ps2.FileFormats.Zipper.Package;

namespace MapBuilder.Server.Services
{
    /// <summary>
    /// SOCOM 3 / Combined Assault sessions: the same ASSET_* commands over a <see cref="CaPackage"/> (a location .ZAR or
    /// a ZDB v6). Read-only for now: models (ISLE world + prop prototypes, ASST props / vehicles / skies, skinned clib
    /// characters, GLOBAL.ZDB weapons), meshes, textures, the world as the map scene and the AUX_*ZAN.ZDB clips.
    /// Edits and saves answer that the session is read-only; CA firing effects are not decoded yet.
    /// </summary>
    internal sealed partial class ZipperAssetService
    {
        internal sealed class CaSession
        {
            public required string Id { get; init; }
            public required CaPackage Package { get; init; }
            public object Gate { get; } = new();
            /// <summary>The mission whose points ASSET_MAP_POINTS returns (a location archive holds several).</summary>
            public string? Mission { get; set; }
        }

        private static readonly ConcurrentDictionary<string, CaSession> CaSessions = new(StringComparer.OrdinalIgnoreCase);

        private const string CaGame = "SOCOM 3 / Combined Assault";

        /// <summary>
        /// Opens a CA archive, or a location / mission path on the disc that names one (<c>G:\RUN\WHARF</c>,
        /// <c>G:\RUN\WHARF\SP422.ZDB</c>: the archive is <see cref="CaPackage.ArchiveName"/>); a mission path selects
        /// that mission.
        /// </summary>
        private static string? TryOpenCa(string full)
        {
            string? mission = null;
            if (!File.Exists(full) && !Directory.Exists(full) && CaDisc.Resolve(full) is { } named)
                (full, mission) = (Path.GetFullPath(named.Archive), named.Mission);
            if (!CaPackage.IsCaPath(full)) return null;
            CaSession? existing = CaSessions.Values.FirstOrDefault(s => s.Package.SourcePath.Equals(full, StringComparison.OrdinalIgnoreCase));
            CaSession session = existing ?? new CaSession { Id = NewId(full), Package = CaPackage.Open(full) };
            if (mission != null)
                session.Mission = (session.Package.FindMission(mission) ?? throw new FileNotFoundException($"{session.Package.Location ?? Path.GetFileName(full)} has no mission {mission}")).Id;
            session.Mission ??= session.Package.Missions().FirstOrDefault()?.Id;
            CaSessions[session.Id] = session;
            return Ok("ASSET_OPEN", new { reused = existing != null, status = CaStatus(session) });
        }

        /// <summary>Answers <paramref name="command"/> when the message names a CA session; null otherwise.</summary>
        private static string? CaRoute(CommandMessage c, string command)
        {
            if (c.AssetSession == null || !CaSessions.TryGetValue(c.AssetSession, out var s)) return Assets.GtaAssetService.Route(c, command);
            try
            {
                object result;
                lock (s.Gate)
                {
                    result = command switch
                    {
                        "ASSET_STATUS" => CaStatus(s),
                        "ASSET_LIST_MODELS" => CaListModels(s, c.AssetJson),
                        "ASSET_DESCRIBE_MODEL" => CaDescribe(s, c.AssetArchive, c.AssetName),
                        "ASSET_MODEL_MESH" => CaMesh(s, c.AssetArchive, c.AssetName, c.AssetJson),
                        "ASSET_TEXTURE" => CaTexture(s, c.AssetName),
                        "ASSET_MAP_SCENE" => CaMapScene(s),
                        "ASSET_MAP_POINTS" => CaPoints(s, c.AssetJson),
                        "ASSET_MAP_TABLES" => CaTables(s),
                        "ASSET_LIST_ANIMATIONS" => CaListAnimations(s, c.AssetJson),
                        "ASSET_ANIMATION" => CaAnimation(s, c.AssetName),
                        "ASSET_WEAPONS" => CaWeapons(s),
                        "ASSET_WEAPON_FX" => new { weapon = c.AssetName, effect = (object?)null },
                        "ASSET_EXPORT" => CaExport(s, c.AssetArchive, c.AssetName, c.AssetJson),
                        "ASSET_EFFECTS" => Array.Empty<object>(),
                        _ => throw new NotSupportedException($"{CaGame} sessions are read-only for now ({command} is not available)"),
                    };
                }
                return Ok(command, result);
            }
            catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException or InvalidOperationException or InvalidDataException or NotSupportedException)
            {
                return Responses.Error($"{command}: {ex.Message}");
            }
        }

        private static object CaStatus(CaSession s)
        {
            var models = s.Package.Models();
            return new
            {
                session = s.Id,
                path = s.Package.SourcePath,
                game = CaGame,
                readOnly = true,
                isZdb = true,
                mapNamespace = models.FirstOrDefault(m => m.Kind == "world")?.Name,
                revision = 0,
                dirty = Array.Empty<string>(),
                canUndo = (string?)null,
                canRedo = (string?)null,
                entries = s.Package.Resources.Count,
                models = models.GroupBy(m => m.Kind).ToDictionary(g => g.Key, g => g.Count()),
                location = s.Package.Location,
                mission = s.Mission,
                missions = s.Package.Missions().Select(m => new { id = m.Id, kind = m.Kind, mode = m.Mode }).ToList(),
            };
        }

        private sealed record PointArgs(string? Mission);

        /// <summary>
        /// The session mission's named points (mission.rdr) in world space; <c>{"mission": "MP414CTR"}</c> selects
        /// another mission of the location first (and keeps it for later calls).
        /// </summary>
        /// <summary>
        /// The session mission's tables in ASSET_MAP_TABLES' shape (read-only): actors with their character type, model,
        /// weapons and starts (vehicles.rdr, character.rdr, GLOBAL's weapon table), the mission header (title, briefing,
        /// map image, allowed weapons) and its valves. CA has no HUD objective table, world settings or extents here, so
        /// those are empty; message keys get their own words as text (the game's text is not on the disc as strings).
        /// </summary>
        private static object CaTables(CaSession s)
        {
            var p = s.Package;
            if (s.Mission == null || p.MissionResource(s.Mission, "MSNv") is not { } vehiclesRes)
                return new { revision = 0, hasReader = false, objectives = Array.Empty<object>(), actors = Array.Empty<object>(), loadoutSource = (string?)null,
                    mission = (object?)null, world = (object?)null, extents = (object?)null, cams = Array.Empty<object>(), valves = Array.Empty<object>(), texts = new Dictionary<string, string>() };

            var actors = CaMissionTables.ParseActors(vehiclesRes.Data);
            var types = p.MissionResource(s.Mission, "CHAR") is { } charRes ? CaMissionTables.ParseCharacterTypes(charRes.Data) : new List<CaMissionTables.CharacterType>();
            var header = p.MissionResource(s.Mission, "MISN") is { } misn ? CaMissionTables.ParseHeader(misn.Data) : null;
            var valveValues = p.MissionResource(s.Mission, "VALV") is { } valv ? CaMissionTables.ParseValves(valv.Data) : new List<(uint Hash, int Value)>();
            var weaponTable = p.GlobalResource("WEAP")?.Data;
            var weapons = weaponTable == null ? new Dictionary<uint, CaMissionTables.Weapon>()
                : CaMissionTables.FindWeapons(weaponTable, types.SelectMany(t => t.Weapons).Concat(header?.Weapons ?? new List<uint>()));
            byte[]? vehicles = p.GlobalResource("GVEH")?.Data, turrets = p.GlobalResource("GTUR")?.Data;
            bool Holds(byte[]? table, uint hash) => table != null && table.AsSpan().IndexOf(BitConverter.GetBytes(hash)) >= 0;

            var texts = new Dictionary<string, string>();
            string? Text(string? key)
            {
                if (key != null && key.EndsWith("_MSG", StringComparison.Ordinal)) texts[key] = CaMissionTables.KeyText(key);
                return key;
            }
            string? ModelRef(uint hash, string kind) => p.ModelByHash(hash, kind) is { } m ? $"{CaArchive(m)}:{m.Name}" : null;
            // Grenades and gear have no display name: their model's name when it is known (not w<hash>), else the
            // start of their description key, marked as cut off.
            string WeaponName(uint h)
            {
                if (!weapons.TryGetValue(h, out var w)) return $"w{h:x8}";
                if (w.Display != null) return w.Display;
                if (w.Model is uint mh && p.ModelByHash(mh, "weapon") is { } m && !System.Text.RegularExpressions.Regex.IsMatch(m.Name, "^w[0-9a-f]{8}$")) return m.Name;
                return w.Key != null ? CaMissionTables.KeyText(w.Key) + "…" : $"w{h:x8}";
            }

            var actorRows = actors.Select(a =>
            {
                var type = types.FirstOrDefault(t => t.Hash == a.Type);
                string kind = type != null && type.Model != 0 ? "character" : Holds(vehicles, a.Type) ? "vehicle" : Holds(turrets, a.Type) ? "turret" : "other";
                var loadout = type?.Weapons.Select(h => new { name = WeaponName(h), model = weapons.TryGetValue(h, out var w) && w.Model is uint mh ? ModelRef(mh, "weapon") : null }).ToList();
                // The figure holds the first gun the package has (grenades have no display name, items no model).
                string? held = type?.Weapons.Select(h => weapons.TryGetValue(h, out var w) && w.Kind == "weapon" && w.Display != null && w.Model is uint mh ? ModelRef(mh, "weapon") : null)
                    .FirstOrDefault(m => m != null);
                return new
                {
                    row = a.Row, path = new object[] { a.Row }, name = a.Name, display = Text(a.Display),
                    character = type != null ? $"{type.Display ?? kind} {a.Type:x8}" : $"{kind} {a.Type:x8}",
                    model = type != null && type.Model != 0 ? ModelRef(type.Model, "character") : null,
                    weapons = loadout, weapon = held, animSet = (string?)null, stand = (string?)null,
                    starts = a.Starts.Select(st => new { point = st.Point, value = (object)st.Heading }).ToList(),
                    teams = Array.Empty<int>(),
                    flags = new[] { new { name = "group", values = new object[] { a.Group } }, new { name = kind, values = Array.Empty<object>() } },
                    ai = (object?)null,
                };
            }).ToList();

            var valves = (header?.Valves ?? new List<uint>()).Select((h, i) => new
            {
                index = i, name = $"v{h:x8}",
                value = valveValues.FirstOrDefault(v => v.Hash == h) is { Hash: not 0 } v ? (object?)v.Value : null,
            }).ToList();
            var keys = header?.Strings.Where(x => x.EndsWith("_MSG", StringComparison.Ordinal)).ToList() ?? new List<string>();
            return new
            {
                revision = 0, hasReader = true, objectives = Array.Empty<object>(), actors = actorRows,
                loadoutSource = types.Count > 0 ? $"{s.Mission} character.rdr" : null,
                mission = header == null ? null : new
                {
                    description = Text(keys.FirstOrDefault()), briefing = Text(keys.Skip(1).FirstOrDefault()),
                    mapImage = header.Strings.FirstOrDefault(x => x.EndsWith(".raw", StringComparison.OrdinalIgnoreCase)),
                    satchelTimer = (float?)null, elevation = (object?)null, ai = Array.Empty<object>(),
                    valves = valves.Select(v => new { v.index, v.name, v.value }).ToList(),
                    weapons = header.Weapons.Where(h => weapons.TryGetValue(h, out var w) && w.Kind == "weapon").Select(WeaponName).Distinct().ToList(),
                },
                world = (object?)null, extents = (object?)null, cams = Array.Empty<object>(),
                valves = valves.Select(v => new { v.name, id = (int?)v.index, v.value }).ToList(),
                texts,
            };
        }

        private static object CaPoints(CaSession s, string? json)
        {
            if (Parse<PointArgs>(json)?.Mission is { Length: > 0 } pick)
                s.Mission = (s.Package.FindMission(pick) ?? throw new KeyNotFoundException($"No mission {pick} in this location")).Id;
            if (s.Mission == null) return new { mission = (string?)null, points = Array.Empty<object>() };
            var points = s.Package.MissionPoints(s.Mission);
            return new
            {
                mission = s.Mission,
                points = points.Select(p => new { p.Name, team = 0, p.Kind, p.X, y = (float?)p.Y, p.Z, p.CellX, p.CellZ, p.Zone, p.Island }).ToList(),
            };
        }

        // Models are addressed as (archive = "<file>|<resource>", name).
        private static string CaArchive(CaPackage.Model m) => $"{m.File}|{m.Resource}";

        private static CaPackage.Model CaFind(CaSession s, string? archive, string? name)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("AssetName (the model) is required");
            return s.Package.Models().FirstOrDefault(m => (archive == null || CaArchive(m).Equals(archive, StringComparison.OrdinalIgnoreCase)) && m.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? throw new KeyNotFoundException($"Model {archive}:{name} not found");
        }

        private static object CaListModels(CaSession s, string? json)
        {
            var f = Parse<ModelFilter>(json) ?? new ModelFilter(null, null, null);
            return s.Package.Models()
                .Where(m => (f.Kind == null || m.Kind.Equals(f.Kind, StringComparison.OrdinalIgnoreCase))
                    && (f.Query == null || m.Name.Contains(f.Query, StringComparison.OrdinalIgnoreCase)))
                .Select(m => new { archive = CaArchive(m), name = m.Name, kind = m.Kind, @namespace = m.File, size = 0, visualCount = m.Nodes })
                .ToList();
        }

        private static object CaTextureInfo(CaSession s, string name)
        {
            var t = s.Package.FindTexture(name);
            if (t == null) return new { name, found = false };
            return new
            {
                name, found = true, @namespace = (string?)null, width = t.Width, height = t.Height,
                psm = $"0x{t.Psm:X2}", bpp = t.Psm switch { 0x13 => 8, 0x14 => 4, 0x02 => 16, _ => 32 },
                palette = (int?)null, paletteResolved = true, hasTransparency = t.HasAlpha,
                // Cut-out (foliage, fences): alpha test at half when the texture has transparent texels.
                alphaTest = t.HasAlpha ? 0.5f : (float?)null, alphaBlend = (bool?)null, clamp = (bool[]?)null,
            };
        }

        private static object CaDescribe(CaSession s, string? archive, string? name)
        {
            var m = CaFind(s, archive, name);
            var mesh = s.Package.BuildMesh(m);
            var chunks = mesh.Chunks.Select(c => new
            {
                index = c.Index, offset = c.GeometryOffset, visual = c.Visual, texture = c.Texture, binding = "ca",
                vertices = c.VertexCount, triangles = c.Triangles.Length / 3, scale = c.Scale, origin = c.Origin, vertexColors = c.Colors != null,
            }).ToList();
            return new
            {
                archive = CaArchive(m), name = m.Name, kind = m.Kind, @namespace = m.File, size = 0, nodeIndex = 0,
                textureNamespace = (string?)null, geoArchive = (string?)null, fromChains = true, game = CaGame,
                visuals = chunks.Select(c => c.visual).Distinct().ToList(),
                chunks,
                textures = mesh.Chunks.Select(c => c.Texture).Where(t => t != null).Distinct().Select(t => CaTextureInfo(s, t!)).ToList(),
                geo = (object?)null,
            };
        }

        /// <summary>A model's mesh, or one placed object's (the map scene's sources: name obj_&lt;offset&gt;).</summary>
        private static (ModelMesh Mesh, string Archive, string Name) CaMeshOf(CaSession s, string? archive, string? name)
        {
            if (name != null && CaPackage.ParseObjectName(name) is int obj)
            {
                int li = Enumerable.Range(0, s.Package.Libraries.Count)
                    .FirstOrDefault(i => archive == null || s.Package.LibraryArchive(i).Equals(archive, StringComparison.OrdinalIgnoreCase));
                var om = s.Package.ObjectMesh(li, obj);
                return (om, om.Archive, om.Name);
            }
            var m = CaFind(s, archive, name);
            return (s.Package.BuildMesh(m), CaArchive(m), m.Name);
        }

        private static object CaMesh(CaSession s, string? archive, string? name, string? json)
        {
            bool arrays = Parse<MeshArgs>(json)?.Arrays ?? true;
            var (mesh, meshArchive, meshName) = CaMeshOf(s, archive, name);
            static string F(float[] a) { var bytes = new byte[a.Length * 4]; Buffer.BlockCopy(a, 0, bytes, 0, bytes.Length); return Convert.ToBase64String(bytes); }
            static string I(int[] a) { var bytes = new byte[a.Length * 4]; Buffer.BlockCopy(a, 0, bytes, 0, bytes.Length); return Convert.ToBase64String(bytes); }
            return new
            {
                archive = meshArchive, name = meshName, revision = 0, textureNamespace = (string?)null, game = CaGame,
                boundsMin = mesh.BoundsMin, boundsMax = mesh.BoundsMax,
                chunks = mesh.Chunks.Select(c => new
                {
                    c.Index, c.GeometryOffset, c.Visual, node = c.Node, c.Texture, c.MaterialBlend, unit = c.Unit.ToString(), c.Matrix, c.Scale, c.Origin,
                    c.VertexCount, triangleCount = c.Triangles.Length / 3, hasColors = c.Colors != null,
                    positions = arrays ? F(c.Positions) : null,
                    uvs = arrays ? F(c.Uvs) : null,
                    normals = arrays ? F(c.Normals) : null,
                    colors = arrays && c.Colors != null ? Convert.ToBase64String(c.Colors) : null,
                    triangles = arrays ? I(c.Triangles) : null,
                    skinBones = arrays && c.SkinBones != null ? I(c.SkinBones) : null,
                    skinWeights = arrays && c.SkinWeights != null ? F(c.SkinWeights) : null,
                    skinPositions = arrays && c.SkinPositions != null ? F(c.SkinPositions) : null,
                    skinNormals = arrays && c.SkinNormals != null ? F(c.SkinNormals) : null,
                }).ToList(),
                skinInfluences = mesh.Skinned ? ModelMesh.SkinInfluences : (int?)null,
                locators = (object?)null,
                skeleton = mesh.Skeleton?.Bones.Select(b => new { b.Name, b.MatrixId, b.Parent, b.GeoIndex, b.Local, b.World }).ToList(),
            };
        }

        private static object CaTexture(CaSession s, string? name)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("AssetName (the texture) is required");
            var t = s.Package.FindTexture(name) ?? throw new KeyNotFoundException($"Texture '{name}' not found");
            return new { info = CaTextureInfo(s, name), png = Convert.ToBase64String(PngEncoder.EncodeRgba(t.Rgba, t.Width, t.Height)) };
        }

        /// <summary>
        /// The location's world pieces as the map scene: one instance per placed object (most detailed LOD), its
        /// source the object's own mesh (obj_&lt;offset&gt;), so the Map Editor instances repeated objects (trees).
        /// </summary>
        private static object CaMapScene(CaSession s)
        {
            var placements = s.Package.MapScene();
            if (placements.Count == 0) throw new InvalidOperationException("This package has no ISLE world");
            // Ids are "<world>/<node hash>"; a node can place several objects (terrain + overlay, trunk + canopy) and a
            // hash can repeat in a world, so repeats get "~<n>".
            var seen = new Dictionary<string, int>();
            var ids = placements.Select(p =>
            {
                string id = $"{p.World}/{p.NodeHash:x8}";
                int n = seen.TryGetValue(id, out int k) ? k + 1 : 0;
                seen[id] = n;
                return n == 0 ? id : $"{id}~{n}";
            }).ToList();
            var instances = placements.Select((p, i) => new
            {
                kind = "model", model = p.World, instance = ids[i], path = "/" + ids[i],
                position = new[] { p.Matrix[12], p.Matrix[13], p.Matrix[14] }, scale = 1f, matrix = p.Matrix,
                geoIndex = (int?)null, clutterIndex = (int?)null,
                source = $"{s.Package.LibraryArchive(p.LibraryIndex)}:{CaPackage.ObjectName(p.Object)}",
            }).ToList();
            return new
            {
                sceneNamespace = string.Join("+", placements.Select(p => p.World).Distinct()),
                counts = new Dictionary<string, int> { ["model"] = instances.Count },
                returned = instances.Count, instances, game = CaGame,
            };
        }

        // ------------------------------------------------------------------ animations + weapons

        /// <summary>CA clips (read-only): the package's BONE banks and the shared AUX_*ZAN.ZDB ones, source = bank.</summary>
        private static object CaListAnimations(CaSession s, string? json)
        {
            var f = Parse<AnimationFilter>(json) ?? new AnimationFilter(null, null);
            return new
            {
                packageArchive = (string?)null,
                sharedPath = (string?)null,
                clips = s.Package.Animations()
                    .Where(c => (f.Query == null || c.Name.Contains(f.Query, StringComparison.OrdinalIgnoreCase))
                        && (f.Source == null || c.Bank.Equals(f.Source, StringComparison.OrdinalIgnoreCase)))
                    .Select(c => new { c.Name, source = c.Bank, editable = false, c.Duration, frames = c.Intervals, samples = c.Intervals + 1, fps = CaAnimations.FramesPerSecond, bones = c.Tracks.Count })
                    .ToList(),
            };
        }

        /// <summary>A CA clip resampled at 30 fps: per bone (CA names, <see cref="CaBones"/>) local positions in units
        /// and unit quaternions, one entry when the track holds a single key.</summary>
        private static object CaAnimation(CaSession s, string? name)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("AssetName (the clip) is required");
            var c = s.Package.Animations().FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? throw new KeyNotFoundException($"Clip '{name}' not found");
            int samples = c.Intervals + 1;
            return new
            {
                c.Name, source = c.Bank, editable = false, c.Duration, frames = c.Intervals, samples, fps = CaAnimations.FramesPerSecond,
                revision = 0,
                bones = c.Tracks.Select(t => new
                {
                    name = s.Package.BoneName(t.Bone), rawName = $"{t.Bone:x8}", kind = "ca",
                    positions = CaAnimations.SamplePositions(t, samples),
                    rotations = CaAnimations.SampleRotations(t, samples),
                }).ToList(),
            };
        }

        /// <summary>CA weapons (GLOBAL.ZDB) for the animation preview's weapon picker; CA's firing effects are not
        /// decoded yet.</summary>
        private static object CaWeapons(CaSession s) => s.Package.Models().Where(m => m.Kind == "weapon")
            .Select(m => new { internalName = m.Name, model = m.Name, archive = CaArchive(m), fireAnimName = "", fireWait = 0f, tracerTextureName = (string?)null })
            .ToList();

        // ------------------------------------------------------------------ export

        private sealed record ExportArgs(string? Pack, string? ModelName, string? OutputDir, string[]? Clips, string? Socom2Run, int? MaxTextureSize);

        private static readonly ConcurrentDictionary<string, Socom2Templates> TemplateCache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>SOCOM II's templates from its RUN folder (found from <paramref name="hint"/>, cached).</summary>
        internal static (Socom2Templates Templates, string Run) Socom2TemplatesFor(string? hint)
        {
            string run = Socom2Templates.FindRunDir(hint)
                ?? throw new InvalidOperationException("SOCOM II's RUN folder was not found (pass socom2Run, e.g. G:/RUN)");
            return (TemplateCache.GetOrAdd(run, r => new Socom2Templates(r)), run);
        }

        /// <summary>Where exported packs go unless the caller names a folder: Documents/CodeDesigner/Exports.</summary>
        internal static string DefaultExportDir() =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "CodeDesigner", "Exports");

        /// <summary>
        /// A CA model (character, weapon, vehicle, prop) as a SOCOM II pack: &lt;outputDir&gt;/&lt;pack&gt;/&lt;PACK&gt;.CDB + its ZEDs,
        /// optionally with CA clips retargeted into its MOTION_S.ZAR. Templates come from SOCOM II's RUN folder
        /// (socom2Run, else the first drive with RUN/MP52.ZDB).
        /// </summary>
        private static object CaExport(CaSession s, string? archive, string? name, string? json)
        {
            var m = CaFind(s, archive, name);
            var a = Parse<ExportArgs>(json) ?? new ExportArgs(null, null, null, null, null, null);
            string run = Socom2Templates.FindRunDir(a.Socom2Run)
                ?? throw new InvalidOperationException("SOCOM II's RUN folder was not found (pass socom2Run, e.g. G:/RUN)");
            var templates = TemplateCache.GetOrAdd(run, r => new Socom2Templates(r));
            string modelName = a.ModelName ?? (m.Name.StartsWith("clib_", StringComparison.OrdinalIgnoreCase) ? "ca_" + m.Name[5..] : "ca_" + m.Name);
            if (modelName.Length > 31) modelName = modelName[..31];
            var r = CaToSocom2.Export(s.Package, m, templates, new CaToSocom2.Options(
                a.Pack ?? CaToSocom2.DefaultPackName(m), modelName, a.OutputDir ?? DefaultExportDir(), a.Clips, a.MaxTextureSize ?? 64));
            return new
            {
                target = "SOCOM II", path = r.CdbPath, r.Pack, model = r.Model, r.Kind, r.Library, r.Triangles, r.Textures, r.Palettes,
                clips = r.Clips, r.Warnings, socom2Run = run,
                // How to open the result: the pack's model (library MDL leaf + model name).
                open = new { archive = $"{r.Library}_MDL.ZED", name = r.Kind == "character" ? $"MESH_{r.Model}" : r.Model },
            };
        }

        private static readonly float[] Matrix4Identity = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };

        private static object CaSessionsList() => CaSessions.Values.Select(CaStatus).ToList();

        private static bool CloseCa(string id) => CaSessions.TryRemove(id, out _);
    }
}
