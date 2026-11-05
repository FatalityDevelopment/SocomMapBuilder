using System.Collections.Concurrent;
using CodeDesigner.Ps2.FileFormats.Assets;
using CodeDesigner.Ps2.FileFormats.Gta;
using CodeDesigner.Ps2.FileFormats.Zipper.Conversion;
using CodeDesigner.Ps2.FileFormats.Imaging;

namespace MapBuilder.Server.Services.Assets
{
    /// <summary>
    /// GTA: San Andreas asset sessions: the ASSET_* commands over a <see cref="GtaPackage"/> (the disc image or a loose
    /// IMG, native DFF or mounted disc). Models, native edits, textures, animations and SOCOM II conversion.
    /// The asset command handlers hand a message here when its session is a GTA one.
    /// </summary>
    internal static partial class GtaAssetService
    {
        internal sealed class Session
        {
            public required string Id { get; init; }
            public required GtaPackage Package { get; init; }
            public object Gate { get; } = new();
            public int Revision { get; set; }
            public Dictionary<GtaPackage.Model, byte[]> Originals { get; } = new();
            public HashSet<GtaPackage.Model> Dirty { get; } = new();
            public Stack<Step> Undo { get; } = new();
            public Stack<Step> Redo { get; } = new();
        }

        private static readonly ConcurrentDictionary<string, Session> Sessions = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The disc image or file a session was opened from (null when the id is not one of these sessions).</summary>
        public static string? SourcePathOf(string id) => Sessions.TryGetValue(id, out var s) ? s.Package.SourcePath : null;

        public const string Game = "GTA: San Andreas";

        /// <summary>ASSET_OPEN for a GTA disc image / IMG archive; null when the path is not one.</summary>
        public static string? TryOpen(string full)
        {
            if (!GtaPackage.IsGtaPath(full)) return null;
            Session session;
            bool reused;
            lock (FileGate)
            {
                var existing = Sessions.Values.FirstOrDefault(s => s.Package.SourcePath.Equals(full, StringComparison.OrdinalIgnoreCase));
                reused = existing != null;
                session = existing ?? new Session { Id = AssetJson.SessionId(full), Package = GtaPackage.Open(full) };
                Sessions[session.Id] = session;
            }
            return AssetJson.Ok("ASSET_OPEN", new { reused, status = Status(session) });
        }

        public static int? Close(string id)
        {
            if (!Sessions.TryGetValue(id, out var s)) return null;
            lock (s.Gate) return Sessions.TryRemove(id, out _) ? s.Dirty.Count : null;
        }

        public static IEnumerable<object> Statuses() => Sessions.Values.Select(Status);

        /// <summary>Answers <paramref name="command"/> when the message names a GTA session; null otherwise.</summary>
        public static string? Route(CommandMessage c, string command)
        {
            if (c.AssetSession == null || !Sessions.TryGetValue(c.AssetSession, out var s)) return null;
            try
            {
                object result;
                lock (s.Gate)
                {
                    if (!Sessions.ContainsKey(s.Id)) throw new InvalidOperationException("The GTA session has closed.");
                    result = command switch
                    {
                        "ASSET_STATUS" => Status(s),
                        "ASSET_LIST_MODELS" => ListModels(s, c.AssetJson),
                        "ASSET_DESCRIBE_MODEL" => Describe(s, c.AssetName, c.AssetArchive),
                        "ASSET_MODEL_MESH" => Mesh(s, c.AssetName, c.AssetArchive, AssetJson.Parse<AssetJson.MeshArgs>(c.AssetJson)?.Arrays ?? true),
                        "ASSET_EDIT" => Edit(s, c.AssetJson),
                        "ASSET_UNDO" => UndoRedo(s, true),
                        "ASSET_REDO" => UndoRedo(s, false),
                        "ASSET_HISTORY" => History(s),
                        "ASSET_SAVE" => Save(s, c.AssetJson),
                        "ASSET_TEXTURE" => Texture(s, c.AssetName),
                        "ASSET_MAP_POINTS" => new { points = Array.Empty<object>() },
                        "ASSET_LIST_ANIMATIONS" => ListAnimations(s, c.AssetJson),
                        "ASSET_ANIMATION" => Animation(s, c.AssetName, c.AssetArchive),
                        "ASSET_WEAPONS" => s.Package.Models.Where(m => m.Kind == "weapon")
                            .Select(m => new { internalName = m.Name, model = m.Name, archive = m.Archive, fireAnimName = "", fireWait = 0f, tracerTextureName = (string?)null }).ToList(),
                        "ASSET_WEAPON_FX" => new { weapon = c.AssetName, effect = (object?)null },
                        "ASSET_EFFECTS" => Array.Empty<object>(),
                        "ASSET_EXPORT" => Export(s, c.AssetName, c.AssetArchive, c.AssetJson),
                        _ => throw new NotSupportedException($"{command} is unavailable for {Game} models"),
                    };
                }
                return AssetJson.Ok(command, result);
            }
            catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException or InvalidOperationException or InvalidDataException or NotSupportedException
                or IOException or UnauthorizedAccessException or System.Text.Json.JsonException or FormatException or OverflowException)
            {
                return Responses.Error($"{command}: {ex.Message}");
            }
        }

        private static object Status(Session s)
        {
            lock (s.Gate) return new
            {
                session = s.Id, path = s.Package.SourcePath, game = Game, readOnly = false,
                initialModel = s.Package.InitialModel, isZdb = true, mapNamespace = (string?)null,
                revision = s.Revision, dirty = s.Dirty.Select(Label).Order().ToArray(),
                canUndo = s.Undo.TryPeek(out var undo) ? undo.Description : null,
                canRedo = s.Redo.TryPeek(out var redo) ? redo.Description : null,
                entries = s.Package.Archives.Sum(a => a.Entries.Count),
                models = s.Package.Models.GroupBy(m => m.Kind).ToDictionary(g => g.Key, g => g.Count()),
            };
        }

        private static GtaPackage.Model Find(Session s, string? name, string? archive) =>
            string.IsNullOrEmpty(name) ? throw new ArgumentException("AssetName (the model) is required")
            : s.Package.FindModel(name, archive) ?? throw new KeyNotFoundException($"Model {archive}/{name} not found");

        private static object ListModels(Session s, string? json)
        {
            var f = AssetJson.Parse<AssetJson.ModelFilter>(json) ?? new AssetJson.ModelFilter(null, null, null);
            return s.Package.Models
                .Where(m => (f.Kind == null || m.Kind.Equals(f.Kind, StringComparison.OrdinalIgnoreCase))
                    && (f.Query == null || m.Name.Contains(f.Query, StringComparison.OrdinalIgnoreCase))
                    && (f.Archive == null || m.Archive.Equals(f.Archive, StringComparison.OrdinalIgnoreCase)))
                .Select(m => new { archive = m.Archive, name = m.Name, kind = m.Kind, @namespace = m.Txd, size = 0, visualCount = 1 })
                .ToList();
        }

        // A weapon's mesh is Y up for viewing; in the game it hangs off the hand bone in its own Z-up space. The "mount"
        // locator is that hand -> mesh transform, (x, y, z) -> (x, -z, y), applied when it is held.
        private static readonly float[] WeaponMount = { 1, 0, 0, 0, 0, 0, 1, 0, 0, -1, 0, 0, 0, 0, 0, 1 };

        private static object Mesh(Session s, string? name, string? archive, bool arrays)
        {
            var m = Find(s, name, archive);
            object? locators = m.Kind == "weapon" ? new[] { new { name = "mount", matrix = WeaponMount } } : null;
            return AssetJson.Mesh(s.Package.BuildMesh(m), m.Archive, m.Name, Game, arrays, locators, s.Revision);
        }

        private static object ListAnimations(Session s, string? json)
        {
            var f = AssetJson.Parse<AssetJson.AnimationFilter>(json) ?? new AssetJson.AnimationFilter(null, null);
            return new
            {
                packageArchive = (string?)null,
                sharedPath = (string?)null,
                clips = s.Package.Animations()
                    .Where(c => (f.Query == null || c.Name.Contains(f.Query, StringComparison.OrdinalIgnoreCase))
                        && (f.Source == null || c.Source.Equals(f.Source, StringComparison.OrdinalIgnoreCase)))
                    .Select(c => new { c.Name, source = c.Source, editable = false, c.Duration, frames = c.Samples - 1, samples = c.Samples, fps = GtaAnimations.SampleRate, bones = c.Tracks.Count })
                    .ToList(),
            };
        }

        /// <summary>A GTA clip resampled at 30 fps: per bone (named by HAnim id from the ped skeleton) local unit
        /// quaternions, and translations for the root (other bones keep their bind translation: positions empty).</summary>
        private static object Animation(Session s, string? name, string? source)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("AssetName (the clip) is required");
            var c = s.Package.FindAnimation(name, string.IsNullOrEmpty(source) ? null : source)
                ?? throw new KeyNotFoundException($"Clip '{name}' not found (see ASSET_LIST_ANIMATIONS)");
            int samples = c.Samples;
            return new
            {
                c.Name, source = c.Source, editable = false, c.Duration, frames = samples - 1, samples, fps = GtaAnimations.SampleRate,
                revision = 0,
                bones = c.Tracks.Select(t => new
                {
                    name = s.Package.BoneName(t.BoneId, t.Name), rawName = t.Name, kind = "gta",
                    positions = t.BoneId == 0 ? InPlace(GtaAnimations.SampleTranslations(t, c, samples)) : GtaAnimations.SampleTranslations(t, c, samples),
                    rotations = GtaAnimations.SampleRotations(t, c, samples),
                }).ToList(),
            };
        }

        /// <summary>The root's travel (game space x, y; Z is up) held at its first key, as the game moves the ped by it:
        /// clips play in place like the other games' clips, keeping the vertical bob.</summary>
        private static List<float[]> InPlace(List<float[]> root)
        {
            if (root.Count == 0) return root;
            float x = root[0][0], y = root[0][1];
            return root.Select(p => new[] { x, y, p[2] }).ToList();
        }

        // ------------------------------------------------------------------ export to SOCOM II

        private sealed record ExportArgs(string? Target, string? Pack, string? ModelName, string? OutputDir, string[]? Clips, string? Socom2Run, int? MaxTextureSize);

        // SOCOM II units per GTA metre for weapons / props (a ped's own head-to-toe ratio for characters, ~10.4).
        private const float UnitsPerMetre = 10.4f;

        /// <summary>ASSET_EXPORT (target SOCOM II): a ped (with clips), weapon, vehicle or object as a SOCOM II pack.</summary>
        private static object Export(Session s, string? name, string? archive, string? json)
        {
            var m = Find(s, name, archive);
            var a = AssetJson.Parse<ExportArgs>(json) ?? new ExportArgs(null, null, null, null, null, null, null);
            if (a.Target != null && !a.Target.Contains("SOCOM II", StringComparison.OrdinalIgnoreCase) && !a.Target.Equals("socom2", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException($"{Game} models export to SOCOM II only");
            var (templates, run) = ZipperAssetService.Socom2TemplatesFor(a.Socom2Run);
            var mesh = s.Package.BuildMesh(m);
            string kind = m.Kind switch { "character" => "character", "weapon" => "weapon", "vehicle" => "vehicle", _ => "prop" };
            if (kind == "character" && !mesh.Skinned) kind = "prop";
            float scale = UnitsPerMetre;
            var clips = new List<ModelToSocom2.SourceClip>();
            if (kind == "character")
            {
                var s2 = templates.CharacterTemplate().Skeleton;
                static float Y(CharacterSkeleton k, string bone) => k.Bones.FirstOrDefault(b => b.Name.Equals(bone, StringComparison.OrdinalIgnoreCase))?.World[13] ?? float.NaN;
                float ratio = (Y(s2, "head") - Y(s2, "ltoe")) / (Y(mesh.Skeleton!, "Head") - Y(mesh.Skeleton!, "L Toe0"));
                if (float.IsFinite(ratio) && ratio > 0) scale = ratio;
                foreach (string clipName in a.Clips ?? Array.Empty<string>())
                {
                    int slash = clipName.IndexOf('/');
                    var clip = s.Package.FindAnimation(slash > 0 ? clipName[(slash + 1)..] : clipName, slash > 0 ? clipName[..slash] : null)
                        ?? throw new KeyNotFoundException($"Clip '{clipName}' not found");
                    int frames = Math.Max(1, (int)Math.Round(clip.Duration * 30));
                    var posed = GtaAnimations.Pose(mesh.Skeleton!, clip, frames, s.Package.BoneName);
                    clips.Add(new ModelToSocom2.SourceClip(clip.Name, clip.Duration, frames, k => posed[Math.Min(k, posed.Count - 1)]));
                }
            }
            else if (a.Clips is { Length: > 0 }) throw new NotSupportedException("Clips export with a character");

            Dictionary<string, float[]>? points = null;
            if (kind == "weapon" && s.Package.FramePoints(m).TryGetValue("gunflash", out var muzzle))
                points = new Dictionary<string, float[]> { ["firepoint"] = muzzle };
            var source = new ModelToSocom2.Source(mesh, kind, scale,
                tex => s.Package.FindTexture(tex) is { } t ? (t.Rgba, t.Width, t.Height) : null,
                kind == "character" ? GtaPedRig.BoneMap : null, kind == "character" ? GtaPedRig.Anchors : null, points);
            string modelName = a.ModelName ?? ("gta_" + m.Name.ToLowerInvariant());
            if (modelName.Length > 31) modelName = modelName[..31];
            string output = a.OutputDir ?? ZipperAssetService.DefaultExportDir();
            CaToSocom2.Result Convert(string pack, string directory) => ModelToSocom2.Export(source, clips, templates,
                new CaToSocom2.Options(pack, modelName, directory, null, a.MaxTextureSize ?? 64));
            var r = a.Pack != null ? Convert(a.Pack, output) : ExportWithDefaultName(m, output, Convert);
            return new
            {
                target = "SOCOM II", path = r.CdbPath, r.Pack, model = r.Model, r.Kind, r.Library, r.Triangles, r.Textures, r.Palettes,
                clips = r.Clips, r.Warnings, socom2Run = run, scale,
                open = new { archive = $"{r.Library}_MDL.ZED", name = r.Kind == "character" ? $"MESH_{r.Model}" : r.Model },
            };
        }

        // Six base-36 characters fit the SOCOM II memory-card naming limit. Include the archive so two
        // identically named models (e.g. the coach bus and jacket) get independent exports.
        internal static string DefaultPackName(GtaPackage.Model model, int attempt = 0)
        {
            string key = string.Concat(model.Archive.ToUpperInvariant(), "\0", model.Name.ToUpperInvariant(), "\0", attempt.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var digest = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key));
            ulong hash = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(digest);
            const string digits = "0123456789abcdefghijklmnopqrstuvwxyz";
            Span<char> name = stackalloc char[7];
            name[0] = 'm';
            for (int i = name.Length - 1; i > 0; i--) { name[i] = digits[(int)(hash % 36)]; hash /= 36; }
            return new string(name);
        }

        // Never replace an existing automatic export. Publish the completed folder with one move. This
        // handles collisions between separate processes or archives opened without the rest of the disc.
        private static CaToSocom2.Result ExportWithDefaultName(GtaPackage.Model model, string output,
            Func<string, string, CaToSocom2.Result> convert)
        {
            string staging = Path.Combine(output, ".gta-export-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            try
            {
                for (int attempt = 0; ; attempt++)
                {
                    string pack = DefaultPackName(model, attempt), target = Path.Combine(output, pack);
                    if (Path.Exists(target)) continue;
                    var result = convert(pack, staging);
                    try { Directory.Move(Path.Combine(staging, pack), target); }
                    catch (IOException) when (Path.Exists(target)) { continue; }
                    return result with { CdbPath = Path.Combine(target, Path.GetFileName(result.CdbPath)) };
                }
            }
            finally { Directory.Delete(staging, recursive: true); }
        }

        private static object TextureInfo(Session s, string name)
        {
            var t = s.Package.FindTexture(name);
            if (t == null) return new { name, found = false };
            return new
            {
                name, found = true, @namespace = (string?)null, width = t.Width, height = t.Height,
                psm = t.Depth switch { 8 => "0x13", 4 => "0x14", 16 => "0x02", _ => "0x00" }, bpp = t.Depth,
                palette = (int?)null, paletteResolved = true, hasTransparency = t.HasAlpha,
                alphaTest = t.HasAlpha && !t.HasTranslucency ? 0.5f : (float?)null, alphaBlend = t.HasTranslucency ? true : (bool?)null, clamp = (bool[]?)null,
            };
        }

        private static object Describe(Session s, string? name, string? archive)
        {
            var m = Find(s, name, archive);
            var mesh = s.Package.BuildMesh(m);
            var chunks = mesh.Chunks.Select(c => new
            {
                index = c.Index, offset = c.GeometryOffset, visual = c.Visual, texture = c.Texture, binding = "gta",
                vertices = c.VertexCount, triangles = c.Triangles.Length / 3, scale = c.Scale, origin = c.Origin, vertexColors = c.Colors != null,
            }).ToList();
            var native = s.Package.IsEditable(m) ? GtaDffEdits.Describe(s.Package.ReadModelData(m)) : null;
            return new
            {
                archive = m.Archive, name = m.Name, kind = m.Kind, @namespace = m.Txd, size = 0, nodeIndex = 0,
                gta = native == null ? null : new { native.Frames, native.Materials, native.GeometryCount, textureChoices = s.Package.TextureChoices(m) },
                revision = s.Revision,
                skinned = mesh.Skinned,
                textureNamespace = (string?)null, geoArchive = (string?)null, fromChains = true, game = Game,
                visuals = chunks.Select(c => c.visual).Distinct().ToList(), chunks,
                textures = mesh.Chunks.Select(c => c.Texture).Where(t => t != null).Distinct().Select(t => TextureInfo(s, t!)).ToList(),
                geo = (object?)null,
            };
        }

        private static object Texture(Session s, string? name)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("AssetName (the texture) is required");
            var t = s.Package.FindTexture(name) ?? throw new KeyNotFoundException($"Texture '{name}' not found");
            return new { info = TextureInfo(s, name), png = Convert.ToBase64String(PngEncoder.EncodeRgba(t.Rgba, t.Width, t.Height)) };
        }
    }
}
