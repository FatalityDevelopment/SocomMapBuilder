using System.Text.Json;
using CodeDesigner.Ps2.FileFormats.Imaging;
using CodeDesigner.Ps2.FileFormats.Zipper.Package;

namespace MapBuilder.Server.Services;

/// <summary>
/// The game's own HUD art for the Map Builder's minimap (ASSET_MAP_HUD): the radar disc, view cone and blips of
/// <c>RUN\COMMON\HUD2_TXR.ZED</c>, and a single-player map's tactical map (<c>RUN\UI\MM&lt;nn&gt;_TXR.ZED</c>,
/// <c>map_&lt;map&gt;_1_1</c> and <c>_1_2</c> side by side) with where it sits in the world when that is known
/// (<c>data/tacmaps.json</c>, measured by hand: nothing in the package says it). Layout: docs/formats/hud.md.
/// </summary>
internal sealed partial class ZipperAssetService
{
    /// <summary>The HUD2 textures the minimap draws, in the order the format doc lists them.</summary>
    internal static readonly string[] HudTextures = new[]
    {
        "compass_lo", "compass_bkrnd", "small_compass", "FOV", "ret_triangle", "ret_base",
        "ret_friendly", "ret_able", "ret_bravo", "ret_threat", "ret_hostage", "ret_bomb",
        "ret_objective", "ret_extraction", "teammate_health",
    }.Concat(Enumerable.Range(1, 24).Select(i => $"ret_nav_{i:00}")).ToArray();

    /// <summary>A tactical map's world rectangle: the world x / z at the image's left / right and top / bottom edges.</summary>
    internal sealed record TacmapPlacement(double MinX, double MaxX, double MinZ, double MaxZ);

    private static readonly Lazy<Dictionary<string, TacmapPlacement>> Tacmaps = new(() => ReadTacmaps(Path.Combine(AppContext.BaseDirectory, "data", "tacmaps.json")));

    internal static Dictionary<string, TacmapPlacement> ReadTacmaps(string path)
    {
        var map = new Dictionary<string, TacmapPlacement>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return map;
        using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.Object || !p.Value.TryGetProperty("world", out var w) || w.ValueKind != JsonValueKind.Array || w.GetArrayLength() != 4) continue;
            var v = w.EnumerateArray().Select(e => e.GetDouble()).ToArray();
            map[p.Name] = new TacmapPlacement(v[0], v[1], v[2], v[3]);
        }
        return map;
    }

    private static object? HudImage(ZipperPackage pkg, string tif, string? ns)
    {
        var decoded = pkg.DecodeTexture(tif, ns);
        if (decoded == null) return null;
        var h = decoded.Value.Texture.Header;
        return new { width = (int)h.Width, height = (int)h.Height, png = Convert.ToBase64String(PngEncoder.EncodeRgba(decoded.Value.Texture.Rgba, h.Width, h.Height)) };
    }

    private static object MapHud(AssetSession s)
    {
        var pkg = s.Package;
        var icons = new Dictionary<string, object>();
        var missing = new List<string>();
        foreach (string name in HudTextures)
        {
            if (HudImage(pkg, name + ".tif", "hud2") is { } img) icons[name] = img; else missing.Add(name);
        }

        // The tactical map: the first of the pause screen's three maps, two 512x512 tiles left and right.
        object? tacmap = null;
        string? map = pkg.MapNamespace;
        if (map != null)
        {
            string? uiNs = pkg.Entries.FirstOrDefault(e => e.Kind == "txr" && e.Namespace == "mm" + map.TrimStart('m', 'M'))?.Namespace;
            var left = pkg.FindTexture($"map_{map}_1_1.tif", uiNs);
            var right = pkg.FindTexture($"map_{map}_1_2.tif", uiNs);
            if (left != null && right != null)
            {
                var tiles = new[] { ($"map_{map}_1_1", 0), ($"map_{map}_1_2", 1) }
                    .Select(t => (t.Item2, img: HudImage(pkg, t.Item1 + ".tif", uiNs))).ToArray();
                Tacmaps.Value.TryGetValue(map, out var placed);
                tacmap = new
                {
                    name = $"map_{map}_1",
                    tiles = tiles.Select(t => t.img).ToArray(),
                    world = placed == null ? null : new[] { placed.MinX, placed.MaxX, placed.MinZ, placed.MaxZ },
                };
            }
        }
        return new { revision = s.Revision, map, icons, missing, tacmap };
    }
}
