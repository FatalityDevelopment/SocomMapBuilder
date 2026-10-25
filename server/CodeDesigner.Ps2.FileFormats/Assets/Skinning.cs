namespace CodeDesigner.Ps2.FileFormats.Assets
{
    /// <summary>
    /// A character's bind skeleton (any game): bones parents first, each with its local and bind-pose world transform
    /// (column-major) and the id its skinned vertex data names it by (SOCOM II matrix_id; elsewhere the bone index).
    /// </summary>
    public sealed class CharacterSkeleton
    {
        public sealed record Bone(string Name, int MatrixId, int Parent, int GeoIndex, float[] Local, float[] World);

        public List<Bone> Bones { get; } = new();
        private readonly Dictionary<int, int> _byMatrixId = new();

        /// <summary>The bone a vertex batch names by matrix_id, or null.</summary>
        public Bone? ByMatrixId(int matrixId) => _byMatrixId.TryGetValue(matrixId, out int i) ? Bones[i] : null;

        /// <summary>Index into <see cref="Bones"/> of the bone with <paramref name="matrixId"/>, or -1.</summary>
        public int IndexOfMatrix(int matrixId) => _byMatrixId.TryGetValue(matrixId, out int i) ? i : -1;

        /// <summary>A skeleton from bones already in order (parents first); a bone's matrix_id is its index.</summary>
        public static CharacterSkeleton FromBones(IEnumerable<Bone> bones)
        {
            var s = new CharacterSkeleton();
            foreach (var b in bones)
            {
                s._byMatrixId.TryAdd(b.MatrixId, s.Bones.Count);
                s.Bones.Add(b);
            }
            return s;
        }

        /// <summary>Appends a bone (parents first); answers its index.</summary>
        public int Add(Bone bone)
        {
            if (bone.MatrixId >= 0) _byMatrixId.TryAdd(bone.MatrixId, Bones.Count);
            Bones.Add(bone);
            return Bones.Count - 1;
        }
    }

    /// <summary>Skinned vertices of any game: per vertex its influences (bone, bone-local position and normal, weight).</summary>
    public static class SkinnedMesh
    {
        /// <summary>One influence on a vertex: bone (index into the skeleton), bone-local position and normal, weight.</summary>
        public readonly record struct Part(int Bone, float X, float Y, float Z, float Nx, float Ny, float Nz, float Weight);

        public sealed class Group
        {
            public string Texture { get; init; } = string.Empty;
            public int Section { get; init; }
            /// <summary>Offset of the draw block in the payload (the group's identity).</summary>
            public int DrawOffset { get; init; }
            /// <summary>Per vertex (slot), its influences.</summary>
            public List<Part>[] Vertices { get; init; } = Array.Empty<List<Part>>();
            public float[] Uvs { get; init; } = Array.Empty<float>();
            public int[] Triangles { get; init; } = Array.Empty<int>();
        }

        /// <summary>Bind-pose position and normal of one vertex (model space).</summary>
        public static (float[] Position, float[] Normal) Pose(IReadOnlyList<Part> parts, CharacterSkeleton skeleton, IReadOnlyList<float[]>? boneWorld = null)
        {
            var p = new float[3];
            var n = new float[3];
            foreach (var part in parts)
            {
                float[] m = boneWorld?[part.Bone] ?? skeleton.Bones[part.Bone].World;
                var (x, y, z) = Matrix4.Apply(m, part.X, part.Y, part.Z);
                p[0] += x * part.Weight; p[1] += y * part.Weight; p[2] += z * part.Weight;
                n[0] += (part.Nx * m[0] + part.Ny * m[4] + part.Nz * m[8]) * part.Weight;
                n[1] += (part.Nx * m[1] + part.Ny * m[5] + part.Nz * m[9]) * part.Weight;
                n[2] += (part.Nx * m[2] + part.Ny * m[6] + part.Nz * m[10]) * part.Weight;
            }
            float len = MathF.Sqrt(n[0] * n[0] + n[1] * n[1] + n[2] * n[2]);
            if (len > 1e-6f) { n[0] /= len; n[1] /= len; n[2] /= len; }
            return (p, n);
        }
    }
}
