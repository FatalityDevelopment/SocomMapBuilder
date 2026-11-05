using CodeDesigner.Ps2.FileFormats.Assets;

namespace CodeDesigner.Ps2.FileFormats.Swbf
{
    /// <summary>
    /// What a Battlefront model needs to go through the shared SOCOM II converter: the soldier skeleton (bone_*) against
    /// SOCOM II's alg_UN01, and the turn into SOCOM II's frame. Battlefront models face +Z (a soldier's left at +X) and
    /// weapons point their barrel along +Z from the grip; SOCOM II characters, vehicles and props face -Z and weapons
    /// point along +X. Both are Y up, and both soldier rigs run each limb bone's x axis along the limb.
    /// </summary>
    public static class SwbfToSocom2
    {
        public static readonly Dictionary<string, string> BoneMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["DummyRoot"] = "hips", ["bone_root"] = "hips", ["bone_pelvis"] = "hips",
            ["bone_a_spine"] = "spinelo", ["bone_b_spine"] = "spinelo", ["bone_ribcage"] = "spinehi",
            ["bone_neck"] = "neck", ["bone_head"] = "head",
            ["bone_r_clavicle"] = "rscap", ["bone_r_upperarm"] = "rbicep", ["bone_r_forearm"] = "rforearm", ["bone_r_hand"] = "rhand", ["hp_weapons"] = "rhand",
            ["bone_l_clavicle"] = "lscap", ["bone_l_upperarm"] = "lbicep", ["bone_l_forearm"] = "lforearm", ["bone_l_hand"] = "lhand",
            ["bone_r_thigh"] = "rthigh", ["bone_r_calf"] = "rcalf", ["bone_r_foot"] = "rfoot", ["bone_r_toe"] = "rtoe",
            ["bone_l_thigh"] = "lthigh", ["bone_l_calf"] = "lcalf", ["bone_l_foot"] = "lfoot", ["bone_l_toe"] = "ltoe",
        };

        public static readonly Dictionary<string, string> Anchors = new()
        {
            ["hips"] = "bone_pelvis", ["spinelo"] = "bone_a_spine", ["spinehi"] = "bone_ribcage", ["neck"] = "bone_neck", ["head"] = "bone_head",
            ["rscap"] = "bone_r_clavicle", ["rbicep"] = "bone_r_upperarm", ["rforearm"] = "bone_r_forearm", ["rhand"] = "bone_r_hand",
            ["lscap"] = "bone_l_clavicle", ["lbicep"] = "bone_l_upperarm", ["lforearm"] = "bone_l_forearm", ["lhand"] = "bone_l_hand",
            ["rthigh"] = "bone_r_thigh", ["rcalf"] = "bone_r_calf", ["rfoot"] = "bone_r_foot", ["rtoe"] = "bone_r_toe",
            ["lthigh"] = "bone_l_thigh", ["lcalf"] = "bone_l_calf", ["lfoot"] = "bone_l_foot", ["ltoe"] = "bone_l_toe",
        };

        /// <summary>The turn about Y into SOCOM II's frame, in degrees: weapons +Z -> +X (90), the rest +Z -> -Z (180).</summary>
        public static int TurnFor(string kind) => kind == "weapon" ? 90 : 180;

        /// <summary>A point turned about Y by a multiple of 90 degrees (x' = x cos + z sin, z' = -x sin + z cos).</summary>
        public static float[] Turn(float[] p, int degrees)
        {
            var (c, s) = CosSin(degrees);
            return new[] { p[0] * c + p[2] * s, p[1], -p[0] * s + p[2] * c };
        }

        /// <summary>The mesh turned about Y (positions, normals and the skeleton's bind matrices); everything else kept.</summary>
        public static ModelMesh Turn(ModelMesh mesh, int degrees)
        {
            var (c, s) = CosSin(degrees);
            void Rotate(float[] a, int at) { float x = a[at], z = a[at + 2]; a[at] = x * c + z * s; a[at + 2] = -x * s + z * c; }
            CharacterSkeleton? skeleton = null;
            if (mesh.Skeleton is { } k)
                skeleton = CharacterSkeleton.FromBones(k.Bones.Select(b =>
                {
                    var w = (float[])b.World.Clone();
                    // Row vectors: every row (the three axes, then the position) turns.
                    for (int r = 0; r < 4; r++) Rotate(w, r * 4);
                    var l = (float[])b.Local.Clone();
                    if (b.Parent < 0) for (int r = 0; r < 4; r++) Rotate(l, r * 4);
                    return new CharacterSkeleton.Bone(b.Name, b.MatrixId, b.Parent, b.GeoIndex, l, w);
                }));
            var turned = new ModelMesh { Archive = mesh.Archive, Name = mesh.Name, Skeleton = skeleton };
            foreach (var ch in mesh.Chunks)
            {
                var pos = (float[])ch.Positions.Clone();
                var nrm = (float[])ch.Normals.Clone();
                for (int i = 0; i + 2 < pos.Length; i += 3) Rotate(pos, i);
                for (int i = 0; i + 2 < nrm.Length; i += 3) Rotate(nrm, i);
                turned.Chunks.Add(new ModelMesh.MeshChunk
                {
                    Index = ch.Index, GeometryOffset = ch.GeometryOffset, ColorOffset = ch.ColorOffset, Visual = ch.Visual, Node = ch.Node, Texture = ch.Texture,
                    MaterialBlend = ch.MaterialBlend, Unit = ch.Unit, Matrix = ch.Matrix, Scale = ch.Scale, Origin = ch.Origin, VertexCount = ch.VertexCount,
                    Positions = pos, Uvs = ch.Uvs, Normals = nrm, Colors = ch.Colors, Triangles = ch.Triangles, SkinBones = ch.SkinBones, SkinWeights = ch.SkinWeights,
                });
            }
            turned.Recompute();
            return turned;
        }

        private static (float Cos, float Sin) CosSin(int degrees) => (((degrees % 360) + 360) % 360) switch
        {
            0 => (1, 0), 90 => (0, 1), 180 => (-1, 0), 270 => (0, -1),
            _ => ((float)Math.Cos(degrees * Math.PI / 180), (float)Math.Sin(degrees * Math.PI / 180)),
        };
    }
}
