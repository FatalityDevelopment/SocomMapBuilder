using CodeDesigner.Ps2.FileFormats.Assets;
using CodeDesigner.Ps2.FileFormats.Zipper.Ca;
using CodeDesigner.Ps2.FileFormats.Zipper.Conversion;

namespace MapBuilder.Server.Services
{
    /// <summary>
    /// ASSET_EXPORT for Fireteam Bravo 2: a character, weapon, prop or vehicle as a SOCOM II pack through the shared
    /// converter (<see cref="ModelToSocom2"/>). FTB2 models are already in SOCOM II's frame (characters face -Z, weapons
    /// point along +X), so nothing is turned. Character bones carry CA's names (<see cref="CaBones"/>), which map onto
    /// alg_UN01. One scale is used for every model: SOCOM II's character height over FTB2's (head to foot), so a
    /// weapon keeps its size next to a soldier. A weapon's firepoint, firepoint_shell, aimpoint and Gun_box nodes give
    /// its attach points. No clips: FTB2's animations are not read yet.
    /// </summary>
    internal sealed partial class ZipperAssetService
    {
        private static object FtbExport(CaSession s, CaPackage.Model m, ExportArgs a)
        {
            if (m.Kind is "world" or "ui") throw new NotSupportedException("A whole island does not convert; export its props one at a time.");
            if (a.Clips is { Length: > 0 }) throw new NotSupportedException($"{FtbGame} animations are not read yet, so no clips can be exported.");
            var (templates, run) = Socom2TemplatesFor(a.Socom2Run);
            var p = s.Package;
            var mesh = p.BuildMesh(m);
            string kind = m.Kind switch { "character" => "character", "weapon" => "weapon", _ => "prop" };
            if (kind == "character" && mesh.Skeleton == null) kind = "prop";
            var warnings = new List<string>();
            // A placed object (Nodes -1) can have its vertices in world space (the trucks): its base centre goes to
            // the origin so the prop places like any other.
            if (m.Nodes < 0 && mesh.Chunks.Count > 0)
            {
                float cx = (mesh.BoundsMin[0] + mesh.BoundsMax[0]) / 2, cz = (mesh.BoundsMin[2] + mesh.BoundsMax[2]) / 2, by = mesh.BoundsMin[1];
                float span = MathF.Max(mesh.BoundsMax[0] - mesh.BoundsMin[0], mesh.BoundsMax[2] - mesh.BoundsMin[2]);
                if (MathF.Sqrt(cx * cx + cz * cz) > span)
                {
                    mesh = Recentred(mesh, cx, by, cz);
                    warnings.Add($"the object's vertices are in world space; it was moved by ({-cx:0.#}, {-by:0.#}, {-cz:0.#}) so its base centre is at the origin");
                }
            }

            float scale = FtbScale(p, templates, kind == "character" ? mesh.Skeleton : null, warnings);
            Dictionary<string, string>? boneMap = null, anchors = null;
            if (kind == "character")
            {
                boneMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                anchors = new Dictionary<string, string>();
                foreach (var bone in mesh.Skeleton!.Bones)
                {
                    var known = CaBones.Known.Values.FirstOrDefault(k => k.Name.Equals(bone.Name, StringComparison.OrdinalIgnoreCase));
                    // The model's two roots above skel_root carry no weights; they follow the hips.
                    boneMap[bone.Name] = known.Socom2 ?? "hips";
                    if (known.Socom2 is { } s2 && known.Name is not ("skel_root" or "headTop" or "headPiece" or "headPiece2" or "body"))
                        anchors.TryAdd(s2, known.Name);
                }
            }
            Dictionary<string, float[]>? points = null;
            if (kind == "weapon")
            {
                var lib = p.Libraries[m.LibraryIndex].Lib;
                var names = new[] { "firepoint", "firepoint_shell", "aimpoint", "Gun_box" }.ToDictionary(CaNames.Crc32, n => n);
                points = lib.Nodes(m.Offset).Where(n => names.ContainsKey(n.Hash))
                    .GroupBy(n => names[n.Hash]).ToDictionary(g => g.Key, g => new[] { g.First().World[12], g.First().World[13], g.First().World[14] });
                if (points.Count == 0) points = null;
            }

            var source = new ModelToSocom2.Source(mesh, kind, scale,
                tex => p.FindTexture(tex) is { } t ? (t.Rgba, t.Width, t.Height) : null, boneMap, anchors, points);
            // island_village_SP2_obj_ff2c0 -> ftb2_village_sp2_obj_ff2c0 (within the 31 characters a name may have).
            string stem = m.Name.StartsWith("clib_", StringComparison.OrdinalIgnoreCase) ? m.Name[5..]
                : m.Name.StartsWith("island_", StringComparison.OrdinalIgnoreCase) ? m.Name[7..] : m.Name;
            string modelName = a.ModelName ?? "ftb2_" + stem.ToLowerInvariant();
            if (modelName.Length > 31) modelName = modelName[..31];
            var r = ModelToSocom2.Export(source, Array.Empty<ModelToSocom2.SourceClip>(), templates,
                new CaToSocom2.Options(a.Pack ?? CaToSocom2.DefaultPackName(m), modelName, a.OutputDir ?? DefaultExportDir(), null, a.MaxTextureSize ?? 64));
            return new
            {
                target = "SOCOM II", path = r.CdbPath, r.Pack, model = r.Model, r.Kind, r.Library, r.Triangles, r.Textures, r.Palettes,
                clips = r.Clips, warnings = r.Warnings.Concat(warnings).ToList(), socom2Run = run, scale,
                open = new { archive = $"{r.Library}_MDL.ZED", name = r.Kind == "character" ? $"MESH_{r.Model}" : r.Model },
            };
        }

        private static ModelMesh Recentred(ModelMesh mesh, float x, float y, float z)
        {
            var moved = new ModelMesh { Archive = mesh.Archive, Name = mesh.Name };
            foreach (var c in mesh.Chunks)
            {
                var pos = (float[])c.Positions.Clone();
                for (int i = 0; i + 2 < pos.Length; i += 3) { pos[i] -= x; pos[i + 1] -= y; pos[i + 2] -= z; }
                moved.Chunks.Add(new ModelMesh.MeshChunk
                {
                    Index = c.Index, GeometryOffset = c.GeometryOffset, Visual = c.Visual, Node = c.Node, Texture = c.Texture, MaterialBlend = c.MaterialBlend,
                    Unit = c.Unit, Matrix = c.Matrix, Scale = c.Scale, Origin = c.Origin, VertexCount = c.VertexCount,
                    Positions = pos, Uvs = c.Uvs, Normals = c.Normals, Colors = c.Colors, Triangles = c.Triangles,
                });
            }
            moved.Recompute();
            return moved;
        }

        /// <summary>SOCOM II units per FTB2 unit: alg_UN01's head-to-foot height over an FTB2 character's (this one, else
        /// the package's first), or 1 when the package has no character.</summary>
        private static float FtbScale(CaPackage p, Socom2Templates templates, CharacterSkeleton? skeleton, List<string> warnings)
        {
            static float Y(CharacterSkeleton k, string bone) => k.Bones.FirstOrDefault(b => b.Name.Equals(bone, StringComparison.OrdinalIgnoreCase))?.World[13] ?? float.NaN;
            skeleton ??= p.Models().Where(x => x.Kind == "character").Select(x => p.BuildMesh(x).Skeleton).FirstOrDefault(k => k != null);
            if (skeleton == null) { warnings.Add("no FTB2 character to measure: converted at 1 SOCOM II unit per FTB2 unit"); return 1f; }
            var s2 = templates.CharacterTemplate().Skeleton;
            float ratio = (Y(s2, "head") - Y(s2, "lfoot")) / (Y(skeleton, "head") - Y(skeleton, "leftFoot"));
            return float.IsFinite(ratio) && ratio > 0 ? ratio : 1f;
        }
    }
}
