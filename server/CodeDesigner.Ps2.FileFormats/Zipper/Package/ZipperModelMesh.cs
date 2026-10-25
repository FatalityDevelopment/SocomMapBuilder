using CodeDesigner.Ps2.FileFormats.Assets;
using CodeDesigner.Ps2.FileFormats.Zipper.Geo;
using CodeDesigner.Ps2.FileFormats.Zipper.Model;
using CodeDesigner.Ps2.FileFormats.Zipper.ZED;

namespace CodeDesigner.Ps2.FileFormats.Zipper.Package
{
    /// <summary>
    /// SOCOM II / Zipper models as <see cref="ModelMesh"/>: every chunk the DMA chains draw, with GEO placement and the
    /// position unit applied (skinned MESH_* characters through their GEO skeleton). <see cref="SetVertices"/> /
    /// <see cref="TransformVertices"/> map model-space edits back to the chunks' int16 records.
    /// </summary>
    public static class ZipperModelMesh
    {
        public static ModelMesh Build(ZipperPackage pkg, ZipperPackage.ModelRef model)
        {
            ZedContainer container = pkg.Container(model.Archive);
            byte[] payload = container.Nodes[model.NodeIndex].Data;
            return Build(pkg, model, container, payload);
        }

        /// <summary>Build, or null when the model can't be read as a mesh.</summary>
        public static ModelMesh? TryBuild(ZipperPackage pkg, ZipperPackage.ModelRef model)
        {
            try { return Build(pkg, model); } catch (InvalidOperationException) { return null; } catch (InvalidDataException) { return null; }
        }

        private static ModelMesh Build(ZipperPackage pkg, ZipperPackage.ModelRef model, ZedContainer container, byte[] payload)
        {
            if (CharacterMesh.StartOffset(container, model.NodeIndex) is int start)
                return BuildCharacter(pkg, model, payload, start);
            var offsets = ZedModelFile.CollectChainOffsets(container.Nodes, model.NodeIndex);
            var chains = offsets.Count > 0 ? ZedModelFile.ParseVisualChains(payload, offsets) : null;
            if (chains == null) throw new InvalidOperationException($"{model.Name} has no DMA chains; only chain-drawn models can be edited as a mesh");

            GeoNode? geo = pkg.DefinitionNode(model);
            var keys = chains.Select(c => c.Key).Where(k => k != null).Cast<string>();
            var placements = geo != null ? VisualPlacement.Build(geo, keys) : new Dictionary<string, VisualPlacement.Placement>();
            ZedModelFile.PositionUnit fallbackUnit = geo?.VisualFlags is uint f ? ZedModelFile.PositionUnitForFlags(f)
                : model.Kind == "world" ? ZedModelFile.PositionUnit.Sixteenth : ZedModelFile.PositionUnit.Scale;

            var mesh = new ModelMesh { Archive = model.Archive, Name = model.Name };
            foreach (var chain in chains)
            {
                var seen = new HashSet<int>();
                foreach (var section in chain.Sections)
                {
                    bool? blend = MaterialBlend(payload, section);
                    foreach (var r in section.Chunks)
                    {
                        if (!seen.Add(r.GeometryOffset)) continue;
                        var place = chain.Key != null && placements.TryGetValue(chain.Key, out var p) ? p : null;
                        var chunk = DecodeChunk(payload, r, mesh.Chunks.Count, chain.Key, section.TextureName, blend,
                            place?.Unit ?? fallbackUnit, place?.Matrix ?? Matrix4.Identity(), place?.NodeName);
                        if (chunk != null) mesh.Chunks.Add(chunk);
                    }
                }
            }
            mesh.Recompute();
            return mesh;
        }

        /// <summary>
        /// A skinned character (<c>MESH_*</c>): each draw group as a chunk in bind pose (the GEO skeleton named
        /// like the mesh without <c>MESH_</c>), with the skin kept per vertex.
        /// </summary>
        private static ModelMesh BuildCharacter(ZipperPackage pkg, ZipperPackage.ModelRef model, byte[] payload, int start)
        {
            string skeletonName = CharacterMesh.SkeletonName(model.Name);
            GeoNode geo = pkg.FindSkeleton(model.Namespace, skeletonName)
                ?? throw new InvalidOperationException($"{model.Name}: no GEO skeleton '{skeletonName}'");
            var skeleton = CharacterMesh.SkeletonFromGeo(geo);
            return ModelMesh.FromGroups(model.Archive, model.Name, skeleton, CharacterMesh.Decode(payload, start, skeleton),
                nameof(ZedModelFile.PositionUnit.Scale), CharacterMesh.PositionScale);
        }

        public static ModelMesh.MeshChunk? DecodeChunk(byte[] b, ZedModelFile.ChainChunkRef r, int index, string? visual, string? texture,
            bool? blend, ZedModelFile.PositionUnit unit, float[] matrix, string? node = null)
        {
            int o = r.GeometryOffset;
            if (o + ZedModelFile.ChunkHeaderSize > b.Length) return null;
            int n = BitConverter.ToInt32(b, o + 0x30);
            int tris = BitConverter.ToInt32(b, o + 0x34);
            float scale = BitConverter.ToSingle(b, o + 0x44);
            if (!float.IsFinite(scale) || scale == 0) scale = 1;
            float Origin(int k) { float v = BitConverter.ToSingle(b, o + k); return float.IsFinite(v) && MathF.Abs(v) < 1e6f ? v : 0; }
            var origin = new[] { Origin(0x38), Origin(0x3C), Origin(0x40) };
            int vs = o + ZedModelFile.ChunkHeaderSize;
            if (n <= 0 || vs + n * 16 > b.Length) return null;
            float div = unit == ZedModelFile.PositionUnit.Sixteenth ? 16f : 32768f;

            var pos = new float[n * 3];
            var uv = new float[n * 2];
            var nrm = new float[n * 3];
            for (int i = 0; i < n; i++)
            {
                int v = vs + i * 16;
                var (x, y, z) = Matrix4.Apply(matrix,
                    BitConverter.ToInt16(b, v) * scale / div + origin[0],
                    BitConverter.ToInt16(b, v + 2) * scale / div + origin[1],
                    BitConverter.ToInt16(b, v + 4) * scale / div + origin[2]);
                pos[i * 3] = x; pos[i * 3 + 1] = y; pos[i * 3 + 2] = z;
                uv[i * 2] = BitConverter.ToInt16(b, v + 8) / 4096f;
                uv[i * 2 + 1] = BitConverter.ToInt16(b, v + 10) / 4096f;
                nrm[i * 3] = BitConverter.ToInt16(b, v + 6) / 32767f;
                nrm[i * 3 + 1] = BitConverter.ToInt16(b, v + 12) / 32767f;
                nrm[i * 3 + 2] = BitConverter.ToInt16(b, v + 14) / 32767f;
            }

            int td = vs + n * 16 + ZedModelFile.TriangleCommandGap;
            var triangles = new List<int>(tris * 3);
            for (int t = 0; t < tris && td + t * 4 + 4 <= b.Length; t++)
            {
                int a = b[td + t * 4] / 3, c1 = b[td + t * 4 + 1] / 3, c2 = b[td + t * 4 + 2] / 3;
                if (a < n && c1 < n && c2 < n) { triangles.Add(a); triangles.Add(c1); triangles.Add(c2); }
            }

            byte[]? colors = null;
            if (r.ColorOffset is int co && r.ColorLength >= n * 4 && co + n * 4 <= b.Length)
            {
                colors = new byte[n * 4];
                Buffer.BlockCopy(b, co, colors, 0, n * 4);
            }

            return new ModelMesh.MeshChunk
            {
                Index = index, GeometryOffset = o, ColorOffset = colors != null ? r.ColorOffset : null, Visual = visual, Node = node,
                Texture = texture, MaterialBlend = blend, Unit = unit.ToString(), Matrix = matrix, Scale = scale, Origin = origin,
                VertexCount = n, Positions = pos, Uvs = uv, Normals = nrm, Colors = colors, Triangles = triangles.ToArray(),
            };
        }

        private static bool? MaterialBlend(byte[] b, ZedModelFile.ChainSection section)
        {
            if (section.MaterialOffset is not int mo) return null;
            int end = Math.Min(b.Length, mo + section.MaterialLength);
            for (int o = mo; o + 16 <= end; o += 16)
            {
                if (BitConverter.ToUInt32(b, o + 8) != 0x42 || BitConverter.ToUInt32(b, o + 12) != 0) continue;
                uint lo = BitConverter.ToUInt32(b, o);
                uint a = lo & 3, bb = (lo >> 2) & 3, d = (lo >> 6) & 3;
                return bb == 1 || d == 1 || (a == 1 && d != 1);
            }
            return null;
        }

        // ------------------------------------------------------------------ edits

        /// <summary>One vertex change in model space; null parts are left as they are.</summary>
        public sealed record VertexEdit(int Index, float[]? Position = null, float[]? Uv = null, byte[]? Color = null, float[]? Normal = null);

        /// <summary>
        /// Applies vertex edits to the chunk at <paramref name="geometryOffset"/>. Positions are model space (as
        /// <see cref="Build(ZipperPackage, ZipperPackage.ModelRef)"/> returns them) and go back through the chunk's
        /// placement and unit to int16. When a position no longer fits int16 at the chunk's scale, the chunk is
        /// rescaled (its scale float and every vertex rewritten). Returns the archive's new bytes.
        /// </summary>
        public static Dictionary<string, byte[]> SetVertices(ZipperPackage pkg, string archive, string modelName, int geometryOffset, IReadOnlyList<VertexEdit> edits)
            => SetVertices(pkg, archive, modelName, new Dictionary<int, IReadOnlyList<VertexEdit>> { [geometryOffset] = edits });

        /// <summary>Vertex edits on several chunks (keyed by geometry offset) as one change.</summary>
        public static Dictionary<string, byte[]> SetVertices(ZipperPackage pkg, string archive, string modelName, IReadOnlyDictionary<int, IReadOnlyList<VertexEdit>> edits)
            => Edit(pkg, archive, modelName, (payload, mesh) =>
            {
                foreach (var (geometryOffset, list) in edits)
                {
                    var chunk = mesh.Chunks.FirstOrDefault(c => c.GeometryOffset == geometryOffset)
                        ?? throw new ArgumentException($"{modelName} has no chunk at geometry offset {geometryOffset}");
                    Apply(payload, chunk, list);
                }
            });

        /// <summary>
        /// Transforms vertices by a model-space matrix: every chunk, the chunks at <paramref name="geometryOffsets"/>,
        /// or (with <paramref name="vertices"/>, keyed by geometry offset) only those vertices.
        /// </summary>
        public static Dictionary<string, byte[]> TransformVertices(ZipperPackage pkg, string archive, string modelName, float[] matrix,
            IReadOnlyCollection<int>? geometryOffsets, IReadOnlyDictionary<int, int[]>? vertices)
            => Edit(pkg, archive, modelName, (payload, mesh) =>
            {
                if (matrix.Length != 16 || !matrix.All(float.IsFinite)) throw new ArgumentException("matrix needs 16 finite floats");
                var targets = mesh.Chunks.Where(c => geometryOffsets == null || geometryOffsets.Count == 0 || geometryOffsets.Contains(c.GeometryOffset)
                    || (vertices != null && vertices.ContainsKey(c.GeometryOffset))).ToList();
                if (targets.Count == 0) throw new ArgumentException("No chunk matched");
                // Shared geometry (one chunk drawn by several visuals) is transformed once.
                foreach (var chunk in targets.GroupBy(c => c.GeometryOffset).Select(g => g.First()))
                {
                    IEnumerable<int> which = vertices != null && vertices.TryGetValue(chunk.GeometryOffset, out var list) ? list : Enumerable.Range(0, chunk.VertexCount);
                    var edits = which.Select(i =>
                    {
                        if (i < 0 || i >= chunk.VertexCount) throw new ArgumentOutOfRangeException(nameof(vertices), $"vertex {i} out of range");
                        var (x, y, z) = Matrix4.Apply(matrix, chunk.Positions[i * 3], chunk.Positions[i * 3 + 1], chunk.Positions[i * 3 + 2]);
                        return new VertexEdit(i, new[] { x, y, z });
                    }).ToList();
                    Apply(payload, chunk, edits);
                }
            });

        private static Dictionary<string, byte[]> Edit(ZipperPackage pkg, string archive, string modelName, Action<byte[], ModelMesh> change)
        {
            var model = pkg.FindModel(archive, modelName) ?? throw new KeyNotFoundException($"Model {archive}:{modelName} not found");
            var container = ZedContainer.FromBytes(pkg.Entry(archive)!.Data);
            byte[] payload = (byte[])container.Nodes[model.NodeIndex].Data.Clone();
            var mesh = Build(pkg, model, container, payload);
            if (mesh.Skinned) throw new NotSupportedException($"{modelName} is a skinned character mesh; its vertices can't be edited yet");
            change(payload, mesh);
            container.ReplaceNodeData(model.NodeIndex, payload);
            return new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase) { [archive] = container.ToBytes() };
        }

        private static void Apply(byte[] b, ModelMesh.MeshChunk chunk, IReadOnlyList<VertexEdit> edits)
        {
            int vs = chunk.GeometryOffset + ZedModelFile.ChunkHeaderSize;
            float div = chunk.Unit == nameof(ZedModelFile.PositionUnit.Sixteenth) ? 16f : 32768f;
            float[] inverse = Matrix4.InverseAffine(chunk.Matrix);

            // Local positions (relative to the chunk origin) after the edits.
            var local = new float[chunk.VertexCount * 3];
            for (int i = 0; i < chunk.VertexCount; i++)
                for (int k = 0; k < 3; k++)
                    local[i * 3 + k] = BitConverter.ToInt16(b, vs + i * 16 + k * 2) * chunk.Scale / div;
            bool movedAny = false;
            foreach (var e in edits)
            {
                if (e.Index < 0 || e.Index >= chunk.VertexCount) throw new ArgumentOutOfRangeException(nameof(edits), $"vertex {e.Index} out of range (0..{chunk.VertexCount - 1})");
                if (e.Position is { Length: 3 } p)
                {
                    var (x, y, z) = Matrix4.Apply(inverse, p[0], p[1], p[2]);
                    local[e.Index * 3] = x - chunk.Origin[0];
                    local[e.Index * 3 + 1] = y - chunk.Origin[1];
                    local[e.Index * 3 + 2] = z - chunk.Origin[2];
                    movedAny = true;
                }
            }

            if (movedAny)
            {
                float scale = chunk.Scale;
                float maxAbs = local.Select(MathF.Abs).DefaultIfEmpty(0).Max();
                if (maxAbs * div / scale > 32767f)
                {
                    scale = maxAbs * div / 32000f;
                    BitConverter.GetBytes(scale).CopyTo(b, chunk.GeometryOffset + 0x44);
                }
                for (int i = 0; i < chunk.VertexCount; i++)
                    for (int k = 0; k < 3; k++)
                        WriteI16(b, vs + i * 16 + k * 2, local[i * 3 + k] * div / scale);
            }

            foreach (var e in edits)
            {
                int v = vs + e.Index * 16;
                if (e.Uv is { Length: 2 } uv)
                {
                    WriteI16(b, v + 8, uv[0] * 4096f);
                    WriteI16(b, v + 10, uv[1] * 4096f);
                }
                if (e.Normal is { Length: 3 } nm)
                {
                    float len = MathF.Sqrt(nm[0] * nm[0] + nm[1] * nm[1] + nm[2] * nm[2]);
                    if (len > 0)
                    {
                        WriteI16(b, v + 6, nm[0] / len * 32767f);
                        WriteI16(b, v + 12, nm[1] / len * 32767f);
                        WriteI16(b, v + 14, nm[2] / len * 32767f);
                    }
                }
                if (e.Color is { Length: >= 3 } col)
                {
                    if (chunk.ColorOffset is not int co) throw new InvalidOperationException($"Chunk at {chunk.GeometryOffset} has no colour block");
                    for (int k = 0; k < Math.Min(4, col.Length); k++) b[co + e.Index * 4 + k] = col[k];
                }
            }
        }

        private static void WriteI16(byte[] b, int at, float value) =>
            BitConverter.GetBytes((short)Math.Clamp((int)MathF.Round(value), short.MinValue, short.MaxValue)).CopyTo(b, at);
    }
}
