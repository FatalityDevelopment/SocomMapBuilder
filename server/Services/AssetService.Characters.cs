using System.Text.Json;
using CodeDesigner.Ps2.FileFormats.Zipper.Package;

namespace MapBuilder.Server.Services
{
    // Characters as memory-card packs: a session's skinned mesh under a donor character's name (CharacterPack).
    internal sealed partial class ZipperAssetService
    {
        /// <summary>
        /// ASSET_CHARACTER_PACK {model, donor, pack, outputDir?, folderCard?, dryRun?}: the session's character as a
        /// memory-card pack (see CharacterPack).
        /// </summary>
        private static object CharacterPackOf(AssetSession s, string? json)
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            var a = doc.RootElement;
            string pack = Str(a, "pack") ?? throw new ArgumentException("\"pack\" (m + up to 6 letters/digits, e.g. mseal1) is required");
            string model = Str(a, "model") ?? throw new ArgumentException("\"model\" (the session's character mesh) is required");
            string donor = Str(a, "donor") ?? throw new ArgumentException("\"donor\" (the character name it takes, e.g. con_pow01) is required");
            CharacterPack.Result r;
            lock (s.Gate) r = CharacterPack.Build(s.Package, model, donor, pack);
            string outputDir = Str(a, "outputDir") ?? DefaultExportDir();
            bool dryRun = a.TryGetProperty("dryRun", out var dr) && dr.ValueKind == JsonValueKind.True;
            var written = dryRun ? new List<string>() : WeaponPack.Write(r.Files, pack, outputDir, Str(a, "folderCard"));
            return new
            {
                dryRun, pack = pack.ToUpperInvariant(), written,
                files = r.Files.Select(f => new { path = f.Path, size = f.Data.Length }),
                mesh = r.Mesh, skeleton = r.Skeleton, textures = r.Textures,
                palettes = r.Palettes.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value), warnings = r.Warnings,
            };
        }
    }
}
