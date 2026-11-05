using CodeDesigner.Ps2.FileFormats.Assets;
using CodeDesigner.Ps2.FileFormats.Swbf;
using CodeDesigner.Ps2.FileFormats.Zipper.Conversion;

namespace MapBuilder.Server.Services.Assets
{
    /// <summary>
    /// ASSET_EXPORT for Battlefront: a soldier, weapon, vehicle or prop as a SOCOM II pack through the shared converter
    /// (<see cref="ModelToSocom2"/>). The model is turned into SOCOM II's frame first (<see cref="SwbfToSocom2"/>); a
    /// soldier is retargeted onto alg_UN01 through its bone_* skeleton and scaled head to toe, everything else at
    /// SOCOM II's units per metre. A weapon's muzzle is its hp_fire hardpoint. No clips (Battlefront's animations are
    /// not read yet).
    /// </summary>
    internal static partial class SwbfAssetService
    {
        private sealed record ExportArgs(string? Target, string? Pack, string? ModelName, string? OutputDir, string[]? Clips, string? Socom2Run, int? MaxTextureSize);

        // SOCOM II units per metre (as for GTA's metres).
        private const float UnitsPerMetre = 10.4f;

        private static object Export(Session s, string? name, string? archive, string? json)
        {
            var m = Find(s, name, archive);
            var a = AssetJson.Parse<ExportArgs>(json) ?? new ExportArgs(null, null, null, null, null, null, null);
            if (a.Target != null && !a.Target.Contains("SOCOM II", StringComparison.OrdinalIgnoreCase) && !a.Target.Equals("socom2", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException($"{Game} models export to SOCOM II only");
            if (a.Clips is { Length: > 0 }) throw new NotSupportedException("Battlefront animations are not read yet, so no clips can be exported.");
            var (templates, run) = ZipperAssetService.Socom2TemplatesFor(a.Socom2Run);

            var raw = s.Package.BuildExportMesh(m);
            string kind = m.Kind switch { "character" => "character", "weapon" => "weapon", "vehicle" => "vehicle", _ => "prop" };
            if (kind == "character" && (raw.Skeleton == null || !raw.Skeleton.Bones.Any(b => b.Name.Equals("bone_pelvis", StringComparison.OrdinalIgnoreCase)))) kind = "prop";
            int turn = SwbfToSocom2.TurnFor(kind);
            var mesh = SwbfToSocom2.Turn(raw, turn);
            var warnings = new List<string>();

            float scale = UnitsPerMetre;
            if (kind == "character")
            {
                var s2 = templates.CharacterTemplate().Skeleton;
                static float Y(CharacterSkeleton k, string bone) => k.Bones.FirstOrDefault(b => b.Name.Equals(bone, StringComparison.OrdinalIgnoreCase))?.World[13] ?? float.NaN;
                float ratio = (Y(s2, "head") - Y(s2, "ltoe")) / (Y(mesh.Skeleton!, "bone_head") - Y(mesh.Skeleton!, "bone_l_toe"));
                if (float.IsFinite(ratio) && ratio > 0) scale = ratio;
            }
            Dictionary<string, float[]>? points = null;
            if (kind == "weapon" && s.Package.Hardpoints(m).TryGetValue("hp_fire", out var muzzle))
                points = new Dictionary<string, float[]> { ["firepoint"] = SwbfToSocom2.Turn(muzzle, turn) };

            var source = new ModelToSocom2.Source(mesh, kind, scale,
                tex => s.Package.FindTexture(tex, m.Archive) is { } t ? (t.Rgba, t.Width, t.Height) : null,
                kind == "character" ? SwbfToSocom2.BoneMap : null, kind == "character" ? SwbfToSocom2.Anchors : null, points);
            string modelName = a.ModelName ?? ("swbf_" + m.Name.ToLowerInvariant());
            if (modelName.Length > 31) modelName = modelName[..31];
            string output = a.OutputDir ?? ZipperAssetService.DefaultExportDir();
            string pack = a.Pack ?? FreePackName(m, output);
            var r = ModelToSocom2.Export(source, Array.Empty<ModelToSocom2.SourceClip>(), templates,
                new CaToSocom2.Options(pack, modelName, output, null, a.MaxTextureSize ?? 64));
            return new
            {
                target = "SOCOM II", path = r.CdbPath, r.Pack, model = r.Model, r.Kind, r.Library, r.Triangles, r.Textures, r.Palettes,
                clips = r.Clips, warnings = r.Warnings.Concat(warnings).ToList(), socom2Run = run, scale,
                open = new { archive = $"{r.Library}_MDL.ZED", name = r.Kind == "character" ? $"MESH_{r.Model}" : r.Model },
            };
        }

        /// <summary>A memory-card pack name for a model ('m' and six base-36 characters from its level and name) whose
        /// folder is not taken yet in <paramref name="output"/>.</summary>
        internal static string FreePackName(SwbfPackage.Model model, string output)
        {
            for (int attempt = 0; ; attempt++)
            {
                string key = $"SWBF\0{model.Archive.ToUpperInvariant()}\0{model.Name.ToUpperInvariant()}\0{attempt}";
                var digest = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key));
                ulong hash = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(digest);
                const string digits = "0123456789abcdefghijklmnopqrstuvwxyz";
                var name = new char[7];
                name[0] = 'm';
                for (int i = 6; i > 0; i--) { name[i] = digits[(int)(hash % 36)]; hash /= 36; }
                string pack = new(name);
                if (!Directory.Exists(Path.Combine(output, pack))) return pack;
            }
        }
    }
}
