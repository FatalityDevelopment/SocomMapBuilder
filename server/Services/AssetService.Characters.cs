using System.Text.Json;
using CodeDesigner.Ps2.FileFormats.Zipper.Package;

namespace MapBuilder.Server.Services
{
    // Characters as memory-card packs: a session's skinned mesh under a donor character's name (CharacterPack).
    internal sealed partial class ZipperAssetService
    {
        /// <summary>
        /// ASSET_CHARACTER_CHECK {model}: what the session's edits did to a character, against the package on disk
        /// (CharacterCheck): stretched / folded surface by bone, weights, added gear's gap from the body.
        /// </summary>
        private static object CharacterCheckOf(AssetSession s, string? json)
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            string model = Str(doc.RootElement, "model") ?? throw new ArgumentException("\"model\" (a skinned MESH_*) is required");
            return CheckReport(s, model) ?? throw new ArgumentException($"{model} is not a skinned character mesh of this session");
        }

        // The check of one character, or null when it is not a skinned mesh. Shared by ASSET_CHARACTER_CHECK and the
        // character edits, which answer it so a bad reshape is seen on the call that made it.
        internal static object? CheckReport(AssetSession s, string model)
        {
            ZipperPackage.ModelRef? now;
            CodeDesigner.Ps2.FileFormats.Assets.ModelMesh? after;
            lock (s.Gate)
            {
                now = s.Package.Models().FirstOrDefault(m => m.Name.Equals(model, StringComparison.OrdinalIgnoreCase));
                after = now == null ? null : ZipperModelMesh.TryBuild(s.Package, now);
            }
            if (now == null || after is not { Skinned: true }) return null;
            var was = DiskMesh(s, now.Archive, now.Name);
            var r = CharacterCheck.Compare(was ?? new CodeDesigner.Ps2.FileFormats.Assets.ModelMesh { Archive = now.Archive, Name = now.Name }, after);
            return new
            {
                model = now.Name, ok = r.Ok, warnings = r.Warnings,
                worstStretch = Math.Round(r.WorstStretch, 2), worstFold = Math.Round(r.WorstFold, 2), changedVertices = r.ChangedVertices,
                problems = r.Problems.Select(p => new { bone = p.Bone, ratio = Math.Round(p.Ratio, 2), at = p.At.Select(x => Math.Round(x, 2)) }),
                badWeights = r.BadWeights, addedTextures = r.AddedTextures, addedVertices = r.AddedVertices, addedGap = Math.Round(r.AddedGap, 2),
            };
        }

        // A model as the package on disk holds it, kept per file version: every character edit checks against it, and
        // reopening a map package each time costs a fraction of a second.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Path, DateTime Stamp, string Archive, string Name), CodeDesigner.Ps2.FileFormats.Assets.ModelMesh?> DiskMeshes = new();

        private static CodeDesigner.Ps2.FileFormats.Assets.ModelMesh? DiskMesh(AssetSession s, string archive, string name) =>
            DiskMeshes.GetOrAdd((s.OpenPath.ToUpperInvariant(), s.SourceStamp, archive.ToUpperInvariant(), name), key =>
            {
                foreach (var old in DiskMeshes.Keys.Where(k => k.Path == key.Path && k.Stamp != key.Stamp)) DiskMeshes.TryRemove(old, out _);
                var disk = ZipperPackage.Open(s.OpenPath);
                return disk.FindModel(archive, name) is { } m ? ZipperModelMesh.TryBuild(disk, m) : null;
            });

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
