using System.Text.Json;
using System.Text.RegularExpressions;
using CodeDesigner.Ps2.FileFormats.Assets;
using CodeDesigner.Ps2.FileFormats.Zipper.Geo;
using CodeDesigner.Ps2.FileFormats.Zipper.Package;

namespace MapBuilder.Server.Services
{
    /// <summary>
    /// Prefabs: a group of placed props and clutter saved from a map, relative to an origin, and placed again in any
    /// map that has the models (by the user or an AI) as one edit. A prefab is a JSON file in <see cref="PrefabFolder"/>:
    /// <c>{name, description?, items: [{kind: reference | clutter, model, matrix}]}</c>, each matrix column-major and
    /// relative to the prefab's origin.
    /// </summary>
    internal sealed partial class ZipperAssetService
    {
        /// <summary>Where prefabs live: MAPBUILDER_PREFABS, else %LOCALAPPDATA%\MapBuilder\prefabs. Tests set it.</summary>
        internal static string PrefabFolder { get; set; } = Environment.GetEnvironmentVariable("MAPBUILDER_PREFABS") is { Length: > 0 } env ? env
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MapBuilder", "prefabs");

        private static readonly Regex PrefabName = new("^[A-Za-z0-9][A-Za-z0-9 _.-]{0,62}$");

        internal sealed record PrefabItem(string Kind, string Model, float[] Matrix);

        internal sealed record Prefab(string Name, string? Description, List<PrefabItem> Items);

        private sealed record SavePrefabArgs(string? Name, string? Description, int[]? GeoIndices, int[]? ClutterIndices, float[]? Origin, bool? Replace);

        private void RegisterPrefabs(CommandRegistry reg)
        {
            reg.Add(c => WithSession(c.AssetSession, "ASSET_SAVE_PREFAB", s => SavePrefab(s, c.AssetJson)), "ASSET_SAVE_PREFAB");
            reg.Add(c => Responses.Run("ASSET_LIST_PREFABS", () => ListPrefabs(c.AssetName)), "ASSET_LIST_PREFABS");
            reg.Add(c => Responses.Run("ASSET_DELETE_PREFAB", () => DeletePrefab(c.AssetName)), "ASSET_DELETE_PREFAB");
        }

        private static string PrefabPath(string name)
        {
            if (!PrefabName.IsMatch(name)) throw new ArgumentException($"Prefab name '{name}' must be 1-63 letters, digits, spaces, '_', '.' or '-'");
            return Path.Combine(PrefabFolder, name + ".prefab.json");
        }

        private static Prefab LoadPrefab(string name)
        {
            string path = PrefabPath(name);
            if (!File.Exists(path)) throw new KeyNotFoundException($"No prefab '{name}' (ASSET_LIST_PREFABS lists them)");
            var p = JsonSerializer.Deserialize<Prefab>(File.ReadAllText(path), JsonOptions) ?? throw new InvalidDataException($"{path} is empty");
            foreach (var it in p.Items)
            {
                if (it.Kind is not ("reference" or "clutter")) throw new InvalidDataException($"{name}: item kind '{it.Kind}' is not reference or clutter");
                if (it.Matrix is not { Length: 16 } || !it.Matrix.All(float.IsFinite)) throw new InvalidDataException($"{name}: {it.Model} needs 16 finite matrix values");
            }
            return p;
        }

        /// <summary>
        /// {name, geoIndices?, clutterIndices?, origin?, description?, replace?}: saves placed props (by geoIndex) and
        /// clutter (by clutterIndex) as a prefab. The origin defaults to the items' centre on the ground (their lowest Y).
        /// </summary>
        private static object SavePrefab(AssetSession s, string? json)
        {
            var a = Parse<SavePrefabArgs>(json) ?? throw new ArgumentException("AssetJson {name, geoIndices?, clutterIndices?} is required");
            string name = a.Name ?? throw new ArgumentException("\"name\" is required");
            string path = PrefabPath(name);
            if (File.Exists(path) && a.Replace != true) throw new InvalidOperationException($"Prefab '{name}' exists; pass replace: true to overwrite it");

            var map = s.Package.MapScene() ?? throw new InvalidOperationException("The package has no map scene");
            Func<string, bool> resolves = n => s.Package.ResolveModel(n) != null;
            var scene = SceneInstances.Collect(map.Scene.MapRoot, resolves);
            var clutter = s.Package.Entry(PackageEdits.ClutterLeaf) is { } c
                ? SceneInstances.FromClutter(ClutterFile.Parse(s.Package.Container(c.Leaf)), resolves).ToList()
                : new List<SceneInstances.Instance>();

            var picked = new List<SceneInstances.Instance>();
            foreach (int g in a.GeoIndices ?? Array.Empty<int>())
            {
                var inst = scene.FirstOrDefault(i => i.Node?.Index == g && i.Kind == "reference")
                    ?? throw new ArgumentException($"geoIndex {g} is not a placed reference (assets_map_scene kind=reference)");
                picked.Add(inst);
            }
            foreach (int k in a.ClutterIndices ?? Array.Empty<int>())
                picked.Add(clutter.FirstOrDefault(i => i.ClutterIndex == k) ?? throw new ArgumentException($"clutterIndex {k} is out of range"));
            if (picked.Count == 0) throw new ArgumentException("Pick at least one item: geoIndices and / or clutterIndices");

            float[] origin = a.Origin is { Length: 3 } o ? o
                : new[] { picked.Average(i => i.Matrix[12]), picked.Min(i => i.Matrix[13]), picked.Average(i => i.Matrix[14]) };
            float[] toLocal = Matrix4.InverseAffine(Matrix4.Compose(origin[0], origin[1], origin[2]));
            var prefab = new Prefab(name, a.Description, picked.Select(i => new PrefabItem(
                i.Kind == "clutter" ? "clutter" : "reference",
                i.ModelName,
                Matrix4.Multiply(toLocal, i.Matrix).Select(v => MathF.Round(v, 5)).ToArray())).ToList());

            Directory.CreateDirectory(PrefabFolder);
            CodeDesigner.Ps2.FileFormats.SafeFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(prefab, new JsonSerializerOptions(JsonOptions) { WriteIndented = true }), backup: false);
            return new { name, path, items = prefab.Items.Count, origin, models = prefab.Items.Select(i => i.Model).Distinct().ToList() };
        }

        /// <summary>Every prefab (name, description, item count, models), or one prefab in full when <paramref name="name"/> is given.</summary>
        private static object ListPrefabs(string? name)
        {
            if (!string.IsNullOrWhiteSpace(name)) return LoadPrefab(name);
            if (!Directory.Exists(PrefabFolder)) return new { folder = PrefabFolder, prefabs = Array.Empty<object>() };
            var list = new List<object>();
            foreach (string file in Directory.GetFiles(PrefabFolder, "*.prefab.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                string n = Path.GetFileName(file)[..^".prefab.json".Length];
                try
                {
                    var p = LoadPrefab(n);
                    list.Add(new { name = n, p.Description, items = p.Items.Count, models = p.Items.Select(i => i.Model).Distinct().ToList() });
                }
                catch (Exception ex) when (ex is JsonException or InvalidDataException or ArgumentException) { list.Add(new { name = n, error = ex.Message }); }
            }
            return new { folder = PrefabFolder, prefabs = list };
        }

        private static object DeletePrefab(string? name)
        {
            string path = PrefabPath(name ?? throw new ArgumentException("AssetName (the prefab) is required"));
            if (!File.Exists(path)) throw new KeyNotFoundException($"No prefab '{name}'");
            File.Delete(path);
            return new { deleted = name };
        }

        /// <summary>
        /// {prefab, position, yaw?, label?}: places every item of a prefab around <c>position</c>, turned by
        /// <c>yaw</c> degrees, as one batch (one undo step; all or nothing). Props copy the placed reference nearest
        /// each one, as placeInstance does.
        /// </summary>
        private static object PlacePrefab(AssetSession s, JsonElement a)
        {
            string name = Str(a, "prefab") ?? throw new ArgumentException("\"prefab\" (a name from ASSET_LIST_PREFABS) is required");
            float[] pos = Vec(a, "position", 3) ?? throw new ArgumentException("\"position\" [x, y, z] is required");
            float yaw = a.TryGetProperty("yaw", out var y) ? Number(y, "yaw") : 0;
            var prefab = LoadPrefab(name);
            float[] place = Matrix4.Compose(pos[0], pos[1], pos[2], yaw);
            var ops = prefab.Items.Select(it =>
            {
                float[] world = Matrix4.Multiply(place, it.Matrix);
                return it.Kind == "clutter"
                    ? (object)new { op = "addClutter", model = it.Model, matrix = world }
                    : new { op = "placeInstance", model = it.Model, matrix = world };
            }).ToList();
            string json = JsonSerializer.Serialize(new { op = "batch", label = Str(a, "label") ?? $"place prefab {name}", ops });
            using var doc = JsonDocument.Parse(json);
            return Batch(s, doc.RootElement);
        }
    }
}
