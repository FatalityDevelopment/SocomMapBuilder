namespace CodeDesigner.Ps2.FileFormats.Assets
{
    /// <summary>
    /// A model as an editable mesh in model space, the same for every game: chunks with positions, UVs (V up),
    /// normals, vertex colours (0x80 = 1.0) and triangles, and for skinned characters a bind skeleton plus per-vertex
    /// skin (bone-local positions and weights) so a viewer can pose them. The editors draw exactly this and agents read
    /// exactly this (ASSET_MODEL_MESH). Each game builds it from its own formats (Zipper: ZipperModelMesh, CaPackage;
    /// GTA: GtaPackage).
    /// </summary>
    public sealed class ModelMesh
    {
        public sealed class MeshChunk
        {
            /// <summary>Chunk number within the mesh (stable for a given payload).</summary>
            public int Index { get; init; }
            /// <summary>Offset of the chunk header in the model payload: the chunk's identity for edits.</summary>
            public int GeometryOffset { get; init; }
            public int? ColorOffset { get; init; }
            public string? Visual { get; init; }
            /// <summary>The GEO node that places the visual (m4_high, m4_low, scope ...), when known.</summary>
            public string? Node { get; init; }
            public string? Texture { get; init; }
            public bool? MaterialBlend { get; init; }
            /// <summary>How the chunk stores positions (a game's own unit name, e.g. SOCOM II Sixteenth / Scale).</summary>
            public string Unit { get; init; } = "Float";
            /// <summary>Chunk local space → model space (column-major), the visual's GEO placement.</summary>
            public float[] Matrix { get; init; } = Matrix4.Identity();
            public float Scale { get; init; }
            public float[] Origin { get; init; } = new float[3];
            public int VertexCount { get; init; }
            public float[] Positions { get; init; } = Array.Empty<float>();
            public float[] Uvs { get; init; } = Array.Empty<float>();
            public float[] Normals { get; init; } = Array.Empty<float>();
            public byte[]? Colors { get; init; }
            public int[] Triangles { get; init; } = Array.Empty<int>();
            /// <summary>
            /// Skinned (character) chunks: per vertex <see cref="SkinInfluences"/> slots of bone index (into
            /// <see cref="Skeleton"/>, -1 = unused), weight and bone-local position, so a viewer can pose them.
            /// </summary>
            public int[]? SkinBones { get; init; }
            public float[]? SkinWeights { get; init; }
            public float[]? SkinPositions { get; init; }
            public float[]? SkinNormals { get; init; }
        }

        /// <summary>Influences per skinned vertex in the Skin* arrays.</summary>
        public const int SkinInfluences = 4;

        public required string Archive { get; init; }
        public required string Name { get; init; }
        public List<MeshChunk> Chunks { get; } = new();
        public float[] BoundsMin { get; private set; } = new float[3];
        public float[] BoundsMax { get; private set; } = new float[3];
        /// <summary>The bind skeleton of a skinned character mesh, else null.</summary>
        public CharacterSkeleton? Skeleton { get; init; }
        public bool Skinned => Skeleton != null;

        /// <summary>A skinned mesh from character groups (any game): each group a chunk in bind pose with the skin kept
        /// per vertex; <paramref name="unit"/> / <paramref name="scale"/> describe how the source stored positions.</summary>
        public static ModelMesh FromGroups(string archive, string name, CharacterSkeleton skeleton, IEnumerable<SkinnedMesh.Group> groups,
            string unit = "Float", float scale = 1f)
        {
            var mesh = new ModelMesh { Archive = archive, Name = name, Skeleton = skeleton };
            foreach (var g in groups)
            {
                int n = g.Vertices.Length;
                var pos = new float[n * 3];
                var nrm = new float[n * 3];
                var bones = new int[n * SkinInfluences];
                var weights = new float[n * SkinInfluences];
                var local = new float[n * SkinInfluences * 3];
                var localN = new float[n * SkinInfluences * 3];
                Array.Fill(bones, -1);
                for (int i = 0; i < n; i++)
                {
                    var (p, nn) = SkinnedMesh.Pose(g.Vertices[i], skeleton);
                    Array.Copy(p, 0, pos, i * 3, 3);
                    Array.Copy(nn, 0, nrm, i * 3, 3);
                    // The heaviest influences when a vertex has more than the slots hold (retail uses at most two).
                    var parts = g.Vertices[i].OrderByDescending(x => x.Weight).Take(SkinInfluences).ToList();
                    for (int k = 0; k < parts.Count; k++)
                    {
                        int at = i * SkinInfluences + k;
                        bones[at] = parts[k].Bone;
                        weights[at] = parts[k].Weight;
                        local[at * 3] = parts[k].X; local[at * 3 + 1] = parts[k].Y; local[at * 3 + 2] = parts[k].Z;
                        localN[at * 3] = parts[k].Nx; localN[at * 3 + 1] = parts[k].Ny; localN[at * 3 + 2] = parts[k].Nz;
                    }
                }
                mesh.Chunks.Add(new MeshChunk
                {
                    Index = mesh.Chunks.Count, GeometryOffset = g.DrawOffset, Visual = $"section{g.Section}", Texture = g.Texture,
                    MaterialBlend = null, Unit = unit, Scale = scale,
                    VertexCount = n, Positions = pos, Uvs = g.Uvs, Normals = nrm, Triangles = g.Triangles,
                    SkinBones = bones, SkinWeights = weights, SkinPositions = local, SkinNormals = localN,
                });
            }
            mesh.Recompute();
            return mesh;
        }

        /// <summary>Recomputes the bounds from the chunks.</summary>
        public void Recompute()
        {
            var min = new[] { float.MaxValue, float.MaxValue, float.MaxValue };
            var max = new[] { float.MinValue, float.MinValue, float.MinValue };
            foreach (var c in Chunks)
                for (int i = 0; i < c.Positions.Length; i++)
                {
                    min[i % 3] = MathF.Min(min[i % 3], c.Positions[i]);
                    max[i % 3] = MathF.Max(max[i % 3], c.Positions[i]);
                }
            BoundsMin = Chunks.Count > 0 ? min : new float[3];
            BoundsMax = Chunks.Count > 0 ? max : new float[3];
        }

    }
}
