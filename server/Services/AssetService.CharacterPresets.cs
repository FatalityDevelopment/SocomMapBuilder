using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MapBuilder.Server.Services
{
    /// <summary>
    /// Character presets: a body shape, a skin, a set of gear (any list of character edits) saved once and applied to
    /// any skinned character as one edit. A preset is <c>{name, description?, ops: [...]}</c>; its ops leave out
    /// <c>archive</c> and <c>model</c>, which applying fills in for the character it goes on (an op that gives its own
    /// keeps them, as addTexture's namespace does). Presets ship with the app (<c>presets/characters</c> beside the
    /// server) and are saved by the user or an AI into <see cref="CharacterPresetFolder"/>; a saved one hides a shipped
    /// one of the same name.
    /// </summary>
    internal sealed partial class ZipperAssetService
    {
        internal static string CharacterPresetFolder { get; set; } = Environment.GetEnvironmentVariable("MAPBUILDER_CHARACTER_PRESETS") is { Length: > 0 } env ? env
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MapBuilder", "character-presets");

        internal static string ShippedCharacterPresets { get; set; } = Path.Combine(AppContext.BaseDirectory, "presets", "characters");

        private static readonly Regex PresetName = new("^[A-Za-z0-9][A-Za-z0-9 _.-]{0,62}$");

        // The ops a preset may hold: character edits and what they need (textures). Map, prop and animation ops stay out.
        private static readonly HashSet<string> PresetOps = new(StringComparer.Ordinal)
        {
            "addTexture", "replaceTexture", "paintTexture", "retexture", "scaleBones", "transformCharacter", "addCharacterMesh", "removeCharacterSection", "setSkinWeights",
        };

        // Ops that act on the character: applying fills in its archive and model.
        private static readonly HashSet<string> OnCharacter = new(StringComparer.Ordinal)
        {
            "paintTexture", "retexture", "scaleBones", "transformCharacter", "addCharacterMesh", "removeCharacterSection", "setSkinWeights",
        };

        private sealed record SaveCharacterPresetArgs(string? Name, string? Description, JsonElement Ops, bool? Replace);

        private void RegisterCharacterPresets(CommandRegistry reg)
        {
            reg.Add(c => Responses.Run("ASSET_SAVE_CHARACTER_PRESET", () => SaveCharacterPreset(c.AssetJson)), "ASSET_SAVE_CHARACTER_PRESET");
            reg.Add(c => Responses.Run("ASSET_LIST_CHARACTER_PRESETS", () => ListCharacterPresets(c.AssetName)), "ASSET_LIST_CHARACTER_PRESETS");
            reg.Add(c => Responses.Run("ASSET_DELETE_CHARACTER_PRESET", () => DeleteCharacterPreset(c.AssetName)), "ASSET_DELETE_CHARACTER_PRESET");
        }

        private static string CheckPresetName(string? name) =>
            name != null && PresetName.IsMatch(name) ? name : throw new ArgumentException($"Preset name '{name}' must be 1-63 letters, digits, spaces, '_', '.' or '-'");

        private static string? FindCharacterPreset(string name)
        {
            foreach (string folder in new[] { CharacterPresetFolder, ShippedCharacterPresets })
            {
                string path = Path.Combine(folder, name + ".json");
                if (File.Exists(path)) return path;
            }
            return null;
        }

        private static JsonObject LoadCharacterPreset(string name)
        {
            string path = FindCharacterPreset(CheckPresetName(name)) ?? throw new KeyNotFoundException($"No character preset '{name}' (ASSET_LIST_CHARACTER_PRESETS lists them)");
            var preset = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new InvalidDataException($"{path} is not a JSON object");
            CheckPresetOps(preset["ops"], name);
            return preset;
        }

        private static void CheckPresetOps(JsonNode? ops, string name)
        {
            if (ops is not JsonArray list || list.Count == 0) throw new ArgumentException($"{name}: \"ops\" is a non-empty array of character edits");
            int i = 0;
            foreach (var op in list)
            {
                string? kind = op?["op"]?.GetValue<string>();
                if (kind == null || !PresetOps.Contains(kind))
                    throw new ArgumentException($"{name}: ops[{i}] is \"{kind}\"; a preset holds {string.Join(", ", PresetOps.OrderBy(x => x))}");
                i++;
            }
        }

        /// <summary>{name, description?, ops, replace?}: saves a preset (ops without archive / model).</summary>
        private static object SaveCharacterPreset(string? json)
        {
            var a = Parse<SaveCharacterPresetArgs>(json) ?? throw new ArgumentException("AssetJson {name, ops} is required");
            string name = CheckPresetName(a.Name);
            var ops = JsonNode.Parse(a.Ops.GetRawText());
            CheckPresetOps(ops, name);
            foreach (var op in (JsonArray)ops!) { (op as JsonObject)?.Remove("archive"); (op as JsonObject)?.Remove("model"); }
            string path = Path.Combine(CharacterPresetFolder, name + ".json");
            if (File.Exists(path) && a.Replace != true) throw new InvalidOperationException($"Character preset '{name}' exists; pass replace: true to overwrite it");
            var preset = new JsonObject { ["name"] = name, ["description"] = a.Description, ["ops"] = ops };
            Directory.CreateDirectory(CharacterPresetFolder);
            CodeDesigner.Ps2.FileFormats.SafeFile.Write(path, System.Text.Encoding.UTF8.GetBytes(preset.ToJsonString(new JsonSerializerOptions { WriteIndented = true })), backup: false);
            return new { name, path, ops = ((JsonArray)ops).Count };
        }

        /// <summary>Every preset (name, description, its ops' kinds, whether it ships with the app), or one in full.</summary>
        private static object ListCharacterPresets(string? name)
        {
            if (!string.IsNullOrWhiteSpace(name)) return LoadCharacterPreset(name);
            var seen = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var (folder, shipped) in new[] { (CharacterPresetFolder, false), (ShippedCharacterPresets, true) })
            {
                if (!Directory.Exists(folder)) continue;
                foreach (string file in Directory.GetFiles(folder, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    string n = Path.GetFileNameWithoutExtension(file);
                    if (seen.ContainsKey(n)) continue;
                    try
                    {
                        var p = LoadCharacterPreset(n);
                        var kinds = ((JsonArray)p["ops"]!).Select(o => o!["op"]!.GetValue<string>()).ToList();
                        seen[n] = new { name = n, description = p["description"]?.GetValue<string>(), shipped, ops = kinds };
                    }
                    catch (Exception ex) when (ex is JsonException or InvalidDataException or ArgumentException or InvalidOperationException) { seen[n] = new { name = n, shipped, error = ex.Message }; }
                }
            }
            return new { folder = CharacterPresetFolder, presets = seen.Values.ToList() };
        }

        private static object DeleteCharacterPreset(string? name)
        {
            string path = Path.Combine(CharacterPresetFolder, CheckPresetName(name) + ".json");
            if (!File.Exists(path))
                throw new KeyNotFoundException(FindCharacterPreset(name!) != null ? $"'{name}' ships with the app and cannot be deleted (save one of the same name to replace it)" : $"No character preset '{name}'");
            File.Delete(path);
            return new { deleted = name };
        }

        /// <summary>
        /// {preset, archive, model, label?}: applies a preset's ops to the character as one batch (one undo step; all or
        /// nothing). Character ops get its archive and model; the rest run as written, except an addTexture of a texture
        /// the package already has, which is skipped.
        /// </summary>
        private static object ApplyCharacterPreset(AssetSession s, JsonElement a)
        {
            string name = Str(a, "preset") ?? throw new ArgumentException("\"preset\" (a name from ASSET_LIST_CHARACTER_PRESETS) is required");
            string archive = Str(a, "archive") ?? throw new ArgumentException("\"archive\" is required");
            string model = Str(a, "model") ?? throw new ArgumentException("\"model\" is required");
            var preset = LoadCharacterPreset(name);
            var ops = new JsonArray();
            foreach (var node in (JsonArray)preset["ops"]!)
            {
                var op = (JsonObject)node!.DeepClone();
                // A texture the package already has (another preset's gear brought it) is used as it is.
                if (op["op"]!.GetValue<string>() == "addTexture" && op["name"]?.GetValue<string>() is { } tex
                    && s.Package.FindTexture(tex, op["namespace"]?.GetValue<string>()) is { } found
                    && (op["namespace"]?.GetValue<string>() is not { } ns || found.Namespace.Equals(ns, StringComparison.OrdinalIgnoreCase))) continue;
                if (OnCharacter.Contains(op["op"]!.GetValue<string>()))
                {
                    op["archive"] ??= archive;
                    op["model"] ??= model;
                }
                ops.Add(op);
            }
            var batch = new JsonObject { ["op"] = "batch", ["label"] = Str(a, "label") ?? $"apply preset {name} to {model}", ["ops"] = ops };
            using var doc = JsonDocument.Parse(batch.ToJsonString());
            return Batch(s, doc.RootElement);
        }
    }
}
