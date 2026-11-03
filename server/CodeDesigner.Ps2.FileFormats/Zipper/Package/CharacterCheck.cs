using CodeDesigner.Ps2.FileFormats.Assets;

namespace CodeDesigner.Ps2.FileFormats.Zipper.Package
{
    /// <summary>
    /// What an edit did to a skinned character, against the character as it was (the package on disk): where the
    /// surface stretched or folded (a seam, a drooping neck, a wavy limb shows as triangle edges much longer or
    /// shorter than before), skin weights that no longer sum to 1, and added gear that floats away from the body.
    /// Groups are matched by their draw block's offset, which edits keep (sections are appended, transforms are in
    /// place); groups present only now are added gear.
    /// </summary>
    public static class CharacterCheck
    {
        /// <summary>Edge length ratios outside this band are reported as problems (scaleBones' own thickness stays inside
        /// it for any reasonable value; a hard-edged selection does not).</summary>
        public const float MaxStretch = 1.4f, MinStretch = 0.6f;

        /// <summary>Added vertices further than this from every body vertex float off the body (a backpack stands out about 2).</summary>
        public const float MaxGap = 2.5f;

        public sealed record Spot(string Bone, float Ratio, float[] At);

        public sealed class Report
        {
            public int Groups { get; set; }
            public int ChangedVertices { get; set; }
            public float WorstStretch { get; set; } = 1;
            public float WorstFold { get; set; } = 1;
            /// <summary>The worst stretched or folded edges, one per bone, worst first.</summary>
            public List<Spot> Problems { get; } = new();
            public int BadWeights { get; set; }
            public List<string> AddedTextures { get; } = new();
            public int AddedVertices { get; set; }
            public float AddedGap { get; set; }
            public List<string> Warnings { get; } = new();
            public bool Ok => Warnings.Count == 0;
        }

        public static Report Compare(ModelMesh before, ModelMesh after)
        {
            var r = new Report { Groups = after.Chunks.Count };
            var old = before.Chunks.ToDictionary(c => c.GeometryOffset);
            var skeleton = after.Skeleton;
            string BoneOf(ModelMesh.MeshChunk c, int v)
            {
                if (skeleton == null || c.SkinBones == null || c.SkinWeights == null) return "?";
                int best = v * ModelMesh.SkinInfluences;
                for (int k = 1; k < ModelMesh.SkinInfluences; k++)
                    if (c.SkinWeights[v * ModelMesh.SkinInfluences + k] > c.SkinWeights[best]) best = v * ModelMesh.SkinInfluences + k;
                int bone = c.SkinBones[best];
                return bone >= 0 && bone < skeleton.Bones.Count ? skeleton.Bones[bone].Name : "?";
            }
            var worstPerBone = new Dictionary<string, Spot>();
            void Note(string bone, float ratio, float[] at)
            {
                float badness = ratio >= 1 ? ratio : 1 / ratio;
                if (!worstPerBone.TryGetValue(bone, out var s) || badness > (s.Ratio >= 1 ? s.Ratio : 1 / s.Ratio)) worstPerBone[bone] = new Spot(bone, ratio, at);
            }

            var body = new List<float[]>();
            var added = new List<ModelMesh.MeshChunk>();
            foreach (var c in after.Chunks)
            {
                if (c.SkinWeights != null)
                    for (int v = 0; v < c.VertexCount; v++)
                    {
                        float sum = 0;
                        for (int k = 0; k < ModelMesh.SkinInfluences; k++) sum += c.SkinWeights[v * ModelMesh.SkinInfluences + k];
                        if (MathF.Abs(sum - 1) > 0.02f) r.BadWeights++;
                    }
                if (!old.TryGetValue(c.GeometryOffset, out var o) || o.VertexCount != c.VertexCount) { added.Add(c); continue; }
                for (int v = 0; v < c.VertexCount; v++)
                {
                    body.Add(new[] { c.Positions[v * 3], c.Positions[v * 3 + 1], c.Positions[v * 3 + 2] });
                    if (Dist(o.Positions, v, c.Positions, v) > 1e-3f) r.ChangedVertices++;
                }
                for (int t = 0; t + 2 < c.Triangles.Length; t += 3)
                    for (int e = 0; e < 3; e++)
                    {
                        int a = c.Triangles[t + e], b = c.Triangles[t + (e + 1) % 3];
                        float l0 = Dist(o.Positions, a, o.Positions, b), l1 = Dist(c.Positions, a, c.Positions, b);
                        if (l0 < 0.05f) continue;
                        float ratio = l1 / l0;
                        r.WorstStretch = MathF.Max(r.WorstStretch, ratio);
                        r.WorstFold = MathF.Min(r.WorstFold, ratio);
                        if (ratio > MaxStretch || ratio < MinStretch)
                            Note(BoneOf(c, a), ratio, new[] { (c.Positions[a * 3] + c.Positions[b * 3]) / 2, (c.Positions[a * 3 + 1] + c.Positions[b * 3 + 1]) / 2, (c.Positions[a * 3 + 2] + c.Positions[b * 3 + 2]) / 2 });
                    }
            }
            r.Problems.AddRange(worstPerBone.Values.OrderByDescending(s => s.Ratio >= 1 ? s.Ratio : 1 / s.Ratio));

            foreach (var c in added)
            {
                if (!string.IsNullOrEmpty(c.Texture) && !r.AddedTextures.Contains(c.Texture, StringComparer.OrdinalIgnoreCase)) r.AddedTextures.Add(c.Texture);
                r.AddedVertices += c.VertexCount;
                if (body.Count == 0) continue;
                for (int v = 0; v < c.VertexCount; v++)
                {
                    float best = float.MaxValue;
                    foreach (var p in body)
                    {
                        float dx = p[0] - c.Positions[v * 3], dy = p[1] - c.Positions[v * 3 + 1], dz = p[2] - c.Positions[v * 3 + 2];
                        best = MathF.Min(best, dx * dx + dy * dy + dz * dz);
                    }
                    r.AddedGap = MathF.Max(r.AddedGap, MathF.Sqrt(best));
                }
            }

            foreach (var p in r.Problems)
                r.Warnings.Add($"{(p.Ratio > 1 ? "stretched" : "folded")} {p.Ratio:0.00}x near {p.Bone} at [{p.At[0]:0.0}, {p.At[1]:0.0}, {p.At[2]:0.0}]: " +
                    "a seam or kink; reshape body parts with scaleBones thickness rather than a transform with a hard edge");
            if (r.BadWeights > 0) r.Warnings.Add($"{r.BadWeights} vertices' skin weights do not sum to 1");
            if (r.AddedGap > MaxGap) r.Warnings.Add($"added geometry reaches {r.AddedGap:0.0} units from the body: it may float off it");
            return r;
        }

        private static float Dist(float[] p, int a, float[] q, int b)
        {
            float dx = p[a * 3] - q[b * 3], dy = p[a * 3 + 1] - q[b * 3 + 1], dz = p[a * 3 + 2] - q[b * 3 + 2];
            return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        }
    }
}
