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
        }

        private static readonly ConcurrentDictionary<string, CaSession> CaSessions = new(StringComparer.OrdinalIgnoreCase);

        private const string CaGame = "SOCOM 3 / Combined Assault";

        private static string? TryOpenCa(string full)
        {
            if (!CaPackage.IsCaPath(full)) return null;
            CaSession? existing = CaSessions.Values.FirstOrDefault(s => s.Package.SourcePath.Equals(full, StringComparison.OrdinalIgnoreCase));
            CaSession session = existing ?? new CaSession { Id = NewId(full), Package = CaPackage.Open(full) };
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
                        "ASSET_MAP_POINTS" => new { points = Array.Empty<object>() },
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
