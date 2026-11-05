using System.Collections.Concurrent;
using CodeDesigner.Ps2.FileFormats.Zipper.Geo;
using CodeDesigner.Ps2.FileFormats.Zipper.RDR;
using CodeDesigner.Ps2.FileFormats.Zipper.Texture;
using CodeDesigner.Ps2.FileFormats.Zipper.ZED;

namespace MapBuilder.Server.Services;

/// <summary>
/// What ASSET_MAP_TABLES adds from outside READERM.ZAR (docs/formats/disc-survey.md): each actor's character type
/// from the disc's shared <c>RUN/READERC.ZAR › character.rdr</c> (model, weapons with their zweapon.rdr models, gear),
/// the map's scrolling textures (<c>TextureScroll_Object</c> in the map's own .ZED) and the map screen images the
/// package carries (<c>UI\MM&lt;n&gt;_TXR.ZED</c>, the pause-menu area map and intel pages; <c>UI\BF&lt;n&gt;_TXR.ZED</c>,
/// the briefing maps). The shared tables are found beside the package as ZWEAPON.ZAR is (the ZDB's folder, or its parent).
/// </summary>
internal sealed partial class ZipperAssetService
{
    private static readonly ConcurrentDictionary<string, (DateTime Stamp, CharacterTypes Types)> CharacterTypeCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A RUN-level file beside the package (its folder, or the folder above), or null.</summary>
    private static string? BesidePackage(AssetSession s, string file)
    {
        // A ZDB sits in RUN; a loose package's files are in RUN\SP\M51 (its SourcePath is <folder>\<name>).
        string? dir = Path.GetDirectoryName(s.Package.SourcePath);
        for (int up = 0; dir != null && up < 3; up++, dir = Path.GetDirectoryName(dir))
            if (Path.Combine(dir, file) is var p && File.Exists(p)) return p;
        return null;
    }

    /// <summary>RUN/READERC.ZAR › character.rdr beside the package (cached by file time), or null.</summary>
    internal static CharacterTypes? CharacterTypesBeside(AssetSession s)
    {
        string? path = BesidePackage(s, "READERC.ZAR");
        if (path == null) return null;
        DateTime stamp = File.GetLastWriteTimeUtc(path);
        if (CharacterTypeCache.TryGetValue(path, out var hit) && hit.Stamp == stamp) return hit.Types;
        var record = ZedContainer.FromBytes(File.ReadAllBytes(path)).Nodes.FirstOrDefault(n => n.Name.Equals("character.rdr", StringComparison.OrdinalIgnoreCase));
        if (record == null) return null;
        var types = CharacterTypes.Parse(record.Data);
        CharacterTypeCache[path] = (stamp, types);
        return types;
    }

    /// <summary>An actor's resolved character type for the page: model file, weapons (with the model each draws as), gear.</summary>
    private static object? ActorCharacter(CharacterTypes? types, WeaponDefs? weapons, string? chartype)
    {
        if (types?.Resolve(chartype) is not { } t) return null;
        return new
        {
            chain = t.Chain, model = t.Model, textureAsset = t.TextureAsset, animSet = t.AnimSet, gear = t.Gear,
            weapons = t.Weapons.Select(w =>
            {
                var def = weapons?.Weapons.FirstOrDefault(d => d.InternalName.Equals(w.Name, StringComparison.OrdinalIgnoreCase));
                return new { name = w.Name, ammo = w.Ammo, magazines = w.Magazines, model = def?.ModelName };
            }).ToArray(),
        };
    }

    /// <summary>The map's TextureScroll_Object rows (model, visual, u / v speed), empty when it has none.</summary>
    private static object[] MapTextureScroll(AssetSession s)
    {
        if (s.Package.MapScene() is not { } map || s.Package.Entry(map.Namespace.ToUpperInvariant() + ".ZED") is null) return Array.Empty<object>();
        var node = s.Package.Container(map.Namespace.ToUpperInvariant() + ".ZED").Nodes.FirstOrDefault(n => n.Name == "TextureScroll_Object");
        if (node == null || node.Data.Length == 0) return Array.Empty<object>();
        try { return TextureScroll.Parse(node.Data).Select(r => (object)new { model = r.Model, visual = r.Visual, u = r.U, v = r.V }).ToArray(); }
        catch (InvalidDataException) { return Array.Empty<object>(); }
    }

    /// <summary>
    /// The map screen images in the package: every texture of <c>MM&lt;n&gt;_TXR.ZED</c> (area map and intel pages,
    /// <c>map_m51_&lt;page&gt;_&lt;part&gt;.tif</c>) and <c>BF&lt;n&gt;_TXR.ZED</c> (briefing maps), with their sizes.
    /// </summary>
    private static object[] MapScreenImages(AssetSession s)
    {
        string? ns = s.Package.MapNamespace;
        if (ns == null) return Array.Empty<object>();
        string digits = new(ns.Where(char.IsDigit).ToArray());
        if (digits.Length == 0) return Array.Empty<object>();
        var images = new List<object>();
        foreach (var (prefix, kind) in new[] { ("mm", "map screen"), ("bf", "briefing") })
        {
            if (s.Package.Archive(prefix + digits, "txr") is not { } txr) continue;
            var nodes = s.Package.Container(txr.Leaf).Nodes;
            for (int i = 0; i + 1 < nodes.Count; i++)
            {
                if (!nodes[i].Name.EndsWith(".tif", StringComparison.OrdinalIgnoreCase) || nodes[i + 1].Name != "texdat") continue;
                try
                {
                    var h = ZedTextureFile.ParseHeader(nodes[i + 1].Data);
                    images.Add(new { archive = txr.Leaf, kind, name = nodes[i].Name, width = (int)h.Width, height = (int)h.Height });
                }
                catch (InvalidDataException) { }
            }
        }
        return images.ToArray();
    }
}
