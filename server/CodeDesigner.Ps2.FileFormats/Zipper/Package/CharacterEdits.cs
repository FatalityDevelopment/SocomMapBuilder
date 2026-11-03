using System.Text;
using CodeDesigner.Ps2.FileFormats.Assets;
using CodeDesigner.Ps2.FileFormats.Zipper.ZED;

namespace CodeDesigner.Ps2.FileFormats.Zipper.Package
{
    /// <summary>
    /// Edits to skinned character meshes (see <see cref="CharacterMesh"/> and docs/formats/characters.md). New geometry
    /// is rigid: every vertex follows one bone at full weight. It goes in as a new material section appended after the
    /// mesh's blocks, so every existing block keeps its offset; the DMA chain is rewritten after it with the new tags
    /// at its end, and <c>_START</c> / <c>ref_count</c> follow.
    /// </summary>
    public static class CharacterEdits
    {
        /// <summary>Per draw group, within what retail groups use (at most 51 vertices, 58 triangles; VU1 memory
        /// holds the vertices at 3 qwords each from 5, then the triangles).</summary>
        public const int MaxGroupVertices = 48, MaxGroupTriangles = 56;

        private const uint Stcycl101 = 0x01000101, Stcycl102 = 0x01000102, Stcycl103 = 0x01000103, UnpackMatrix = 0x6C048000, Mscnt = 0x17000000;

        public sealed class Result
        {
            public Dictionary<string, byte[]> Changed { get; } = new(StringComparer.OrdinalIgnoreCase);
            public string Bone { get; set; } = string.Empty;
            public int MatrixId { get; set; }
            public int Groups { get; set; }
            public int Vertices { get; set; }
            public int Triangles { get; set; }
        }

        /// <summary>
        /// Adds <paramref name="input"/> (character model space, the bind pose) to the skinned mesh
        /// <paramref name="modelName"/>, drawn with <paramref name="texture"/> and moving rigidly with
        /// <paramref name="boneName"/>. <paramref name="archiveBytes"/>: the archive as an earlier step of the same edit
        /// left it (record indices are the package's), else the package's own bytes.
        /// </summary>
        public static Result AddRigidMesh(ZipperPackage pkg, string archive, string modelName, string boneName, string texture, MeshTopology.InputMesh input,
            byte[]? archiveBytes = null)
        {
            var entry = pkg.Entry(archive) is { Kind: "mdl" } e ? e : throw new ArgumentException($"{archive} is not a model archive of this package");
            var model = pkg.FindModel(entry.Leaf, modelName) ?? throw new KeyNotFoundException($"No model {modelName} in {entry.Leaf}");
            byte[] source = archiveBytes ?? entry.Data;
            var container = ZedContainer.FromBytes(source);
            int start = CharacterMesh.StartOffset(container, model.NodeIndex)
                ?? throw new ArgumentException($"{modelName} is not a skinned character mesh (no {modelName}_START); use addMesh");
            var geo = pkg.FindSkeleton(model.Namespace, CharacterMesh.SkeletonName(model.Name))
                ?? throw new InvalidOperationException($"{modelName} has no skeleton {CharacterMesh.SkeletonName(model.Name)} in a GEO archive");
            var skeleton = CharacterMesh.SkeletonFromGeo(geo);
            var bone = skeleton.Bones.FirstOrDefault(b => b.MatrixId >= 0 && b.Name.Equals(boneName, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"{modelName} has no skinning bone {boneName} (bones: {string.Join(", ", skeleton.Bones.Where(b => b.MatrixId >= 0).Select(b => b.Name))})");

            texture = texture.Trim();
            if (Encoding.Latin1.GetByteCount(texture) > 15) throw new ArgumentException($"{texture} is longer than 15 characters: a section's name field is 16 bytes");
            if (pkg.FindTexture(texture, pkg.TextureNamespace(model.Namespace)) == null)
                throw new KeyNotFoundException($"Texture {texture} is not in the package's TXR archives; add it first (addTexture, namespace {model.Namespace})");
            if (input.Positions.Count == 0 || input.Triangles.Count == 0) throw new ArgumentException("The mesh has no triangles");

            var nodes = container.Nodes;
            byte[] payload = nodes[model.NodeIndex].Data;
            if (start % 16 != 0 || start + 16 > payload.Length) throw new InvalidDataException($"{modelName}: chain start 0x{start:X} is not a tag in the payload");
            uint endWord = BitConverter.ToUInt32(payload, start);
            int count = (int)(endWord & 0xFFFF);
            if (start + 16 + count * 16 > payload.Length) throw new InvalidDataException($"{modelName}: its chain runs past the payload");
            byte[] gif = TemplateGif(payload, start, count) ?? throw new InvalidDataException($"{modelName} has no draw block to take GIF tags from");

            // Bone-local, as the vertex batches store them (1/4096 units, int16).
            float[] inverse = Matrix4.InverseAffine(bone.World);
            var normals = input.Normals.Count == input.Positions.Count ? input.Normals : FaceNormals(input);
            var local = new List<short[]>(input.Positions.Count);
            for (int i = 0; i < input.Positions.Count; i++)
            {
                var p = input.Positions[i];
                var (x, y, z) = Matrix4.Apply(inverse, p[0], p[1], p[2]);
                var n = normals[i];
                float nx = inverse[0] * n[0] + inverse[4] * n[1] + inverse[8] * n[2];
                float ny = inverse[1] * n[0] + inverse[5] * n[1] + inverse[9] * n[2];
                float nz = inverse[2] * n[0] + inverse[6] * n[1] + inverse[10] * n[2];
                float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                if (len > 1e-6f) { nx /= len; ny /= len; nz /= len; } else { nx = 0; ny = 1; nz = 0; }
                float reach = MathF.Max(MathF.Abs(x), MathF.Max(MathF.Abs(y), MathF.Abs(z)));
                if (reach * 4096 > 32767)
                    throw new ArgumentException($"Vertex {i} is {reach:0.##} units from {bone.Name} along an axis; skinned vertices reach at most 7.99. Use a nearer bone or split the mesh.");
                local.Add(new[] { Q(x * 4096), Q(y * 4096), Q(z * 4096), Q(nx * 32767), Q(ny * 32767), Q(nz * 32767) });
            }

            var data = new MemoryStream();
            data.Write(payload, 0, start);
            var tags = new List<uint[]>();
            int nameAt = (int)data.Length;
            var field = new byte[16];
            Encoding.Latin1.GetBytes(texture).CopyTo(field, 0);
            data.Write(field);
            tags.Add(new uint[] { 0x10060000, (uint)nameAt, 0, 0 });

            var result = new Result { Bone = bone.Name, MatrixId = bone.MatrixId, Vertices = input.Positions.Count, Triangles = input.Triangles.Count };
            foreach (var (verts, tris) in Groups(input.Triangles))
            {
                int n = verts.Count, t = tris.Count;
                byte[] vertexBlock = VertexBlock(bone.MatrixId, t, verts.Select(v => local[v]).ToList());
                int vertexAt = (int)data.Length;
                data.Write(vertexBlock);
                byte[] draw = DrawBlock(gif, n, tris, verts.Select(v => input.Uvs.Count > v ? input.Uvs[v] : new float[2]).ToList());
                int drawAt = (int)data.Length;
                data.Write(draw);
                tags.Add(new uint[] { 0x30090004, (uint)bone.MatrixId, Stcycl101, UnpackMatrix });
                tags.Add(new uint[] { 0x300A0000u | (uint)(vertexBlock.Length / 16), (uint)vertexAt, (uint)bone.MatrixId, (uint)n });
                tags.Add(new uint[] { 0x300B0000u | (uint)(draw.Length / 16), (uint)drawAt, 2, 0 });
                result.Groups++;
            }

            int newStart = (int)data.Length;
            int newCount = count + tags.Count;
            if (newCount > 0xFFFF) throw new InvalidOperationException($"{modelName}'s chain would hold {newCount} tags, over 65535");
            data.Write(BitConverter.GetBytes((endWord & 0xFFFF0000u) | (uint)newCount));
            data.Write(payload, start + 4, 12 + count * 16);
            foreach (var tag in tags) foreach (uint w in tag) data.Write(BitConverter.GetBytes(w));

            byte[] bytes = ZedSplice.Replace(source, model.NodeIndex, data.ToArray());
            for (int i = model.NodeIndex + 1; i <= model.NodeIndex + (int)nodes[model.NodeIndex].Type && i < nodes.Count; i++)
            {
                if (nodes[i].Name.Equals(model.Name + "_START", StringComparison.OrdinalIgnoreCase))
                    bytes = ZedSplice.Replace(bytes, i, WithInt(nodes[i].Data, newStart));
                else if (nodes[i].Name == "ref_count")
                    bytes = ZedSplice.Replace(bytes, i, WithInt(nodes[i].Data, newCount));
            }
            result.Changed[entry.Leaf] = bytes;
            return result;
        }

        /// <summary>Which vertices of a skinned mesh an edit takes: all, or those of a section, a texture and / or whose
        /// heaviest bone is one of <see cref="Bones"/> (every given filter must match).</summary>
        public sealed record Selection(IReadOnlySet<string>? Bones = null, string? Texture = null, int? Section = null);

        // One vertex part as stored: the entry's offset in the payload and the bone it follows.
        private readonly record struct Part(int Offset, CharacterSkeleton.Bone Bone, float Weight);

        /// <summary>
        /// The selected vertices' bind-pose positions (model space), per vertex its parts. A vertex is a draw group's
        /// slot; its parts sit in the group's vertex batches.
        /// </summary>
        private static List<(float[] Position, float[] Normal, List<Part> Parts)> Vertices(byte[] payload, int start, CharacterSkeleton skeleton, Selection sel)
        {
            var list = new List<(float[], float[], List<Part>)>();
            int count = (int)(BitConverter.ToUInt32(payload, start) & 0xFFFF);
            int section = -1;
            string texture = string.Empty;
            bool matrixPending = false;
            var slots = new SortedDictionary<int, List<Part>>();
            for (int t = 1; t <= count; t++)
            {
                int o = start + t * 16;
                uint lo = BitConverter.ToUInt32(payload, o);
                int addr = BitConverter.ToInt32(payload, o + 4), qwc = (int)(lo & 0xFFFF);
                if ((lo >> 28 & 7) == 1) { section++; texture = ReadName(payload, addr); continue; }
                if (BitConverter.ToUInt32(payload, o + 8) == Stcycl101 && BitConverter.ToUInt32(payload, o + 12) == UnpackMatrix) { matrixPending = true; continue; }
                if (addr < 0 || addr + qwc * 16 > payload.Length) continue;
                if (matrixPending)
                {
                    matrixPending = false;
                    int h = addr + 8;
                    var bone = skeleton.ByMatrixId(BitConverter.ToInt16(payload, h + 4));
                    int n = BitConverter.ToInt16(payload, h + 6);
                    if (bone == null) continue;
                    for (int i = 0; i < n && h + 8 + i * 16 + 16 <= addr + qwc * 16; i++)
                    {
                        int v = h + 8 + i * 16;
                        int slot = BitConverter.ToInt16(payload, v + 6) / 2;
                        if (!slots.TryGetValue(slot, out var parts)) slots[slot] = parts = new List<Part>();
                        parts.Add(new Part(v, bone, BitConverter.ToInt16(payload, v + 14) / 32767f));
                    }
                    continue;
                }
                // A draw block closes the group.
                bool inSection = (sel.Section == null || sel.Section == section) && (sel.Texture == null || sel.Texture.Equals(texture, StringComparison.OrdinalIgnoreCase));
                foreach (var parts in slots.Values)
                {
                    if (!inSection || parts.Sum(p => p.Weight) <= 0) continue;
                    if (sel.Bones != null && !sel.Bones.Contains(parts.MaxBy(p => p.Weight).Bone.Name)) continue;
                    var pos = new float[3];
                    var nrm = new float[3];
                    foreach (var p in parts)
                    {
                        var w = p.Bone.World;
                        float x = BitConverter.ToInt16(payload, p.Offset) / 4096f, y = BitConverter.ToInt16(payload, p.Offset + 2) / 4096f, z = BitConverter.ToInt16(payload, p.Offset + 4) / 4096f;
                        float nx = BitConverter.ToInt16(payload, p.Offset + 8) / 32767f, ny = BitConverter.ToInt16(payload, p.Offset + 10) / 32767f, nz = BitConverter.ToInt16(payload, p.Offset + 12) / 32767f;
                        var (wx, wy, wz) = Matrix4.Apply(w, x, y, z);
                        pos[0] += p.Weight * wx; pos[1] += p.Weight * wy; pos[2] += p.Weight * wz;
                        nrm[0] += p.Weight * (w[0] * nx + w[4] * ny + w[8] * nz);
                        nrm[1] += p.Weight * (w[1] * nx + w[5] * ny + w[9] * nz);
                        nrm[2] += p.Weight * (w[2] * nx + w[6] * ny + w[10] * nz);
                    }
                    list.Add((pos, nrm, parts));
                }
                slots = new SortedDictionary<int, List<Part>>();
            }
            return list;
        }

        /// <summary>The centre of the selected vertices' bounds (bind pose, model space), or null when none is selected.</summary>
        public static float[]? SelectionCentre(ZipperPackage pkg, string archive, string modelName, Selection sel)
        {
            var (_, _, payload, start, skeleton) = Open(pkg, archive, modelName, null);
            var verts = Vertices(payload, start, skeleton, sel);
            if (verts.Count == 0) return null;
            var min = new float[3]; var max = new float[3];
            for (int k = 0; k < 3; k++) { min[k] = verts.Min(v => v.Position[k]); max[k] = verts.Max(v => v.Position[k]); }
            return new[] { (min[0] + max[0]) / 2, (min[1] + max[1]) / 2, (min[2] + max[2]) / 2 };
        }

        /// <summary>
        /// Moves the selected vertices of a skinned mesh by <paramref name="matrix"/> (model space, bind pose). Each
        /// vertex moves by d = M·p − p: every part gets d in its bone's frame added, so the blend (weights summing to 1)
        /// moves by d; normals follow M's inverse transpose. Sizes stay, so the payload changes in place.
        /// </summary>
        public static Result TransformVertices(ZipperPackage pkg, string archive, string modelName, float[] matrix, Selection sel, byte[]? archiveBytes = null)
        {
            var (entry, model, payload, start, skeleton) = Open(pkg, archive, modelName, archiveBytes);
            payload = (byte[])payload.Clone();
            var verts = Vertices(payload, start, skeleton, sel);
            if (verts.Count == 0) throw new ArgumentException($"No vertex of {modelName} matches the selection");
            float[] normalMatrix = NormalMatrix(matrix);
            var inverses = new Dictionary<string, float[]>();
            foreach (var (p, n, parts) in verts)
            {
                var (mx, my, mz) = Matrix4.Apply(matrix, p[0], p[1], p[2]);
                float[] d = { mx - p[0], my - p[1], mz - p[2] };
                float[] nw = Normalize(new[]
                {
                    normalMatrix[0] * n[0] + normalMatrix[4] * n[1] + normalMatrix[8] * n[2],
                    normalMatrix[1] * n[0] + normalMatrix[5] * n[1] + normalMatrix[9] * n[2],
                    normalMatrix[2] * n[0] + normalMatrix[6] * n[1] + normalMatrix[10] * n[2],
                });
                foreach (var part in parts)
                {
                    if (!inverses.TryGetValue(part.Bone.Name, out var inv)) inverses[part.Bone.Name] = inv = Matrix4.InverseAffine(part.Bone.World);
                    float[] ld = { inv[0] * d[0] + inv[4] * d[1] + inv[8] * d[2], inv[1] * d[0] + inv[5] * d[1] + inv[9] * d[2], inv[2] * d[0] + inv[6] * d[1] + inv[10] * d[2] };
                    float[] ln = Normalize(new[] { inv[0] * nw[0] + inv[4] * nw[1] + inv[8] * nw[2], inv[1] * nw[0] + inv[5] * nw[1] + inv[9] * nw[2], inv[2] * nw[0] + inv[6] * nw[1] + inv[10] * nw[2] });
                    for (int k = 0; k < 3; k++)
                    {
                        float v = BitConverter.ToInt16(payload, part.Offset + k * 2) / 4096f + ld[k];
                        if (MathF.Abs(v) * 4096 > 32767)
                            throw new ArgumentException($"A vertex would end {MathF.Abs(v):0.##} units from {part.Bone.Name}; skinned vertices reach at most 7.99. Use a smaller change.");
                        BitConverter.GetBytes(Q(v * 4096)).CopyTo(payload, part.Offset + k * 2);
                        BitConverter.GetBytes(Q(ln[k] * 32767)).CopyTo(payload, part.Offset + 8 + k * 2);
                    }
                }
            }
            var result = new Result { Vertices = verts.Count };
            result.Changed[entry.Leaf] = ZedSplice.Replace(archiveBytes ?? entry.Data, model.NodeIndex, payload);
            return result;
        }

        /// <summary>How <see cref="ScaleBones"/> scales one bone's parts, in its own frame about its joint: along its
        /// length axis (<see cref="Length"/>), across it (<see cref="Thickness"/>), and all over (<see cref="Scale"/>).</summary>
        public sealed record BoneScale(float Scale = 1, float Thickness = 1, float Length = 1);

        /// <summary>
        /// Scales the parts that follow each named bone, in that bone's frame about its joint. A vertex shared by several
        /// bones moves by its weight's share, so the change fades across joints with no seam (thicker biceps blend into
        /// the elbow and shoulder). The length axis points from the joint to the bone's children (their average), or for
        /// an end bone (hand, head) along the bone from its parent. Normals follow the inverse scale.
        /// </summary>
        public static Result ScaleBones(ZipperPackage pkg, string archive, string modelName, IReadOnlyDictionary<string, BoneScale> scales, byte[]? archiveBytes = null)
        {
            var (entry, model, payload, start, skeleton) = Open(pkg, archive, modelName, archiveBytes);
            payload = (byte[])payload.Clone();
            var matrices = new Dictionary<int, (float[] M, float[] N, string Name)>();
            foreach (var (name, s) in scales)
            {
                int index = skeleton.Bones.FindIndex(b => b.MatrixId >= 0 && b.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (index < 0) throw new ArgumentException($"{modelName} has no skinning bone {name} (bones: {string.Join(", ", skeleton.Bones.Where(b => b.MatrixId >= 0).Select(b => b.Name))})");
                if (s.Scale <= 0 || s.Thickness <= 0 || s.Length <= 0) throw new ArgumentException($"{name}: scale, thickness and length are above 0");
                float[] axis = LengthAxis(skeleton, index);
                // S = scale * (thickness * (I - a aT) + length * a aT); symmetric, so its inverse transpose is its inverse.
                var m = new float[9];
                var n = new float[9];
                for (int r = 0; r < 3; r++)
                    for (int c = 0; c < 3; c++)
                    {
                        float aa = axis[r] * axis[c], id = r == c ? 1 : 0;
                        m[r * 3 + c] = s.Scale * (s.Thickness * (id - aa) + s.Length * aa);
                        n[r * 3 + c] = (1 / s.Scale) * ((1 / s.Thickness) * (id - aa) + (1 / s.Length) * aa);
                    }
                matrices[skeleton.Bones[index].MatrixId] = (m, n, skeleton.Bones[index].Name);
            }

            int count = (int)(BitConverter.ToUInt32(payload, start) & 0xFFFF), parts = 0;
            bool matrixPending = false;
            for (int t = 1; t <= count; t++)
            {
                int o = start + t * 16;
                uint lo = BitConverter.ToUInt32(payload, o);
                int addr = BitConverter.ToInt32(payload, o + 4), qwc = (int)(lo & 0xFFFF);
                if ((lo >> 28 & 7) == 1) continue;
                if (BitConverter.ToUInt32(payload, o + 8) == Stcycl101 && BitConverter.ToUInt32(payload, o + 12) == UnpackMatrix) { matrixPending = true; continue; }
                if (!matrixPending) continue;
                matrixPending = false;
                if (addr < 0 || addr + qwc * 16 > payload.Length) continue;
                int h = addr + 8;
                if (!matrices.TryGetValue(BitConverter.ToInt16(payload, h + 4), out var s)) continue;
                int vertices = BitConverter.ToInt16(payload, h + 6);
                for (int i = 0; i < vertices && h + 8 + i * 16 + 16 <= addr + qwc * 16; i++)
                {
                    int v = h + 8 + i * 16;
                    float[] p = { BitConverter.ToInt16(payload, v) / 4096f, BitConverter.ToInt16(payload, v + 2) / 4096f, BitConverter.ToInt16(payload, v + 4) / 4096f };
                    float[] nl = { BitConverter.ToInt16(payload, v + 8) / 32767f, BitConverter.ToInt16(payload, v + 10) / 32767f, BitConverter.ToInt16(payload, v + 12) / 32767f };
                    float[] nn = Normalize(Mul3(s.N, nl));
                    float[] q = Mul3(s.M, p);
                    for (int k = 0; k < 3; k++)
                    {
                        if (MathF.Abs(q[k]) * 4096 > 32767)
                            throw new ArgumentException($"A vertex would end {MathF.Abs(q[k]):0.##} units from {s.Name}; skinned vertices reach at most 7.99. Use a smaller scale.");
                        BitConverter.GetBytes(Q(q[k] * 4096)).CopyTo(payload, v + k * 2);
                        BitConverter.GetBytes(Q(nn[k] * 32767)).CopyTo(payload, v + 8 + k * 2);
                    }
                    parts++;
                }
            }
            if (parts == 0) throw new ArgumentException($"No vertex of {modelName} follows {string.Join(", ", scales.Keys)}");
            var result = new Result { Vertices = parts };
            result.Changed[entry.Leaf] = ZedSplice.Replace(archiveBytes ?? entry.Data, model.NodeIndex, payload);
            return result;
        }

        private static float[] Mul3(float[] m, float[] v) =>
            new[] { m[0] * v[0] + m[1] * v[1] + m[2] * v[2], m[3] * v[0] + m[4] * v[1] + m[5] * v[2], m[6] * v[0] + m[7] * v[1] + m[8] * v[2] };

        // A bone's length axis in its own frame: towards its children (the average of their offsets), else for an end
        // bone the direction it lies along from its parent.
        private static float[] LengthAxis(CharacterSkeleton skeleton, int index)
        {
            var sum = new float[3];
            foreach (var c in skeleton.Bones.Where(b => b.Parent == index))
                for (int k = 0; k < 3; k++) sum[k] += c.Local[12 + k];
            if (sum[0] * sum[0] + sum[1] * sum[1] + sum[2] * sum[2] > 1e-6f) return Normalize(sum);
            var own = skeleton.Bones[index].Local;
            // The offset from the parent is in the parent's frame; the bone's local rotation (transposed) brings it into its own.
            float[] t = { own[12], own[13], own[14] };
            float[] inOwn = { own[0] * t[0] + own[1] * t[1] + own[2] * t[2], own[4] * t[0] + own[5] * t[1] + own[6] * t[2], own[8] * t[0] + own[9] * t[1] + own[10] * t[2] };
            return inOwn[0] * inOwn[0] + inOwn[1] * inOwn[1] + inOwn[2] * inOwn[2] > 1e-6f ? Normalize(inOwn) : new[] { 0f, 1f, 0f };
        }

        private static (ZipperPackage.RootEntry Entry, ZipperPackage.ModelRef Model, byte[] Payload, int Start, CharacterSkeleton Skeleton) Open(
            ZipperPackage pkg, string archive, string modelName, byte[]? archiveBytes)
        {
            var entry = pkg.Entry(archive) is { Kind: "mdl" } e ? e : throw new ArgumentException($"{archive} is not a model archive of this package");
            var model = pkg.FindModel(entry.Leaf, modelName) ?? throw new KeyNotFoundException($"No model {modelName} in {entry.Leaf}");
            var container = ZedContainer.FromBytes(archiveBytes ?? entry.Data);
            int start = CharacterMesh.StartOffset(container, model.NodeIndex)
                ?? throw new ArgumentException($"{modelName} is not a skinned character mesh (no {modelName}_START); use transformVertices");
            var geo = pkg.FindSkeleton(model.Namespace, CharacterMesh.SkeletonName(model.Name))
                ?? throw new InvalidOperationException($"{modelName} has no skeleton {CharacterMesh.SkeletonName(model.Name)} in a GEO archive");
            byte[] payload = container.Nodes[model.NodeIndex].Data;
            if (start % 16 != 0 || start + 16 > payload.Length) throw new InvalidDataException($"{modelName}: chain start 0x{start:X} is not a tag in the payload");
            return (entry, model, payload, start, CharacterMesh.SkeletonFromGeo(geo));
        }

        // The inverse transpose of a matrix's 3x3 (column-major in a 4x4).
        private static float[] NormalMatrix(float[] m)
        {
            float a = m[0], b = m[4], c = m[8], d = m[1], e = m[5], f = m[9], g = m[2], h = m[6], i = m[10];
            float det = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
            if (MathF.Abs(det) < 1e-12f) throw new ArgumentException("The transform flattens the mesh (its determinant is 0)");
            // M^-1 row-major (adjugate / det); (M^-1)^T stored column-major is that same order.
            float[] inv = { (e * i - f * h) / det, (c * h - b * i) / det, (b * f - c * e) / det, (f * g - d * i) / det, (a * i - c * g) / det, (c * d - a * f) / det, (d * h - e * g) / det, (b * g - a * h) / det, (a * e - b * d) / det };
            return new[] { inv[0], inv[1], inv[2], 0, inv[3], inv[4], inv[5], 0, inv[6], inv[7], inv[8], 0, 0, 0, 0, 1f };
        }

        private static float[] Normalize(float[] v)
        {
            float l = MathF.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
            return l > 1e-6f ? new[] { v[0] / l, v[1] / l, v[2] / l } : new[] { 0f, 1f, 0f };
        }

        /// <summary>A character's lower-detail mesh name: <c>MESH_seal_A_woodland_LO</c> → <c>MESH_seal_A_woodland_1_LO</c>,
        /// <c>MESH_alban_ter01</c> → <c>MESH_alban_ter01_1</c>.</summary>
        public static string LowDetailName(string meshName) =>
            meshName.EndsWith("_LO", StringComparison.OrdinalIgnoreCase) ? meshName[..^3] + "_1" + meshName[^3..] : meshName + "_1";

        /// <summary>
        /// Takes material sections out of a skinned mesh's DMA chain: those drawn with <paramref name="texture"/>, or the
        /// one numbered <paramref name="section"/> (in chain order, as the editors number them). Their blocks stay in
        /// the payload, drawn by nothing, so every other offset holds. <paramref name="removed"/>: the sections taken out.
        /// </summary>
        public static Dictionary<string, byte[]> RemoveSections(ZipperPackage pkg, string archive, string modelName, string? texture, int? section, out List<int> removed)
        {
            if ((texture == null) == (section == null)) throw new ArgumentException("Give a texture or a section, not both");
            var entry = pkg.Entry(archive) is { Kind: "mdl" } e ? e : throw new ArgumentException($"{archive} is not a model archive of this package");
            var model = pkg.FindModel(entry.Leaf, modelName) ?? throw new KeyNotFoundException($"No model {modelName} in {entry.Leaf}");
            var container = ZedContainer.FromBytes(entry.Data);
            int start = CharacterMesh.StartOffset(container, model.NodeIndex)
                ?? throw new ArgumentException($"{modelName} is not a skinned character mesh (no {modelName}_START)");
            var nodes = container.Nodes;
            byte[] payload = nodes[model.NodeIndex].Data;
            uint endWord = BitConverter.ToUInt32(payload, start);
            int count = (int)(endWord & 0xFFFF);
            if (start + 16 + count * 16 > payload.Length) throw new InvalidDataException($"{modelName}: its chain runs past the payload");

            // Tags by section: a cnt tag (id 1) opens one.
            var sections = new List<(string Texture, List<int> Tags)>();
            for (int t = 1; t <= count; t++)
            {
                int o = start + t * 16;
                if ((BitConverter.ToUInt32(payload, o) >> 28 & 7) == 1)
                    sections.Add((ReadName(payload, BitConverter.ToInt32(payload, o + 4)), new List<int>()));
                if (sections.Count > 0) sections[^1].Tags.Add(t);
            }
            var picked = Enumerable.Range(0, sections.Count)
                .Where(i => section != null ? i == section : sections[i].Texture.Equals(texture!.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            if (picked.Count == 0)
                throw new ArgumentException(section != null
                    ? $"{modelName} has sections 0-{sections.Count - 1}"
                    : $"{modelName} has no section drawn with {texture} (textures: {string.Join(", ", sections.Select(x => x.Texture).Distinct())})");
            if (picked.Count == sections.Count) throw new InvalidOperationException($"That would remove every section of {modelName}");
            removed = picked;

            var drop = picked.SelectMany(i => sections[i].Tags).ToHashSet();
            int newCount = count - drop.Count;
            var data = new MemoryStream();
            data.Write(payload, 0, start);
            data.Write(BitConverter.GetBytes((endWord & 0xFFFF0000u) | (uint)newCount));
            data.Write(payload, start + 4, 12);
            for (int t = 1; t <= count; t++)
                if (!drop.Contains(t)) data.Write(payload, start + t * 16, 16);

            byte[] bytes = ZedSplice.Replace(entry.Data, model.NodeIndex, data.ToArray());
            for (int i = model.NodeIndex + 1; i <= model.NodeIndex + (int)nodes[model.NodeIndex].Type && i < nodes.Count; i++)
                if (nodes[i].Name == "ref_count") bytes = ZedSplice.Replace(bytes, i, WithInt(nodes[i].Data, newCount));
            return new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase) { [entry.Leaf] = bytes };
        }

        private static string ReadName(byte[] b, int addr)
        {
            if (addr < 0 || addr >= b.Length) return string.Empty;
            int end = addr;
            while (end < b.Length && end - addr < 32 && b[end] != 0) end++;
            return Encoding.Latin1.GetString(b, addr, end - addr);
        }

        private static short Q(float v) => (short)Math.Clamp((int)MathF.Round(v), short.MinValue, short.MaxValue);

        private static byte[] WithInt(byte[] original, int value)
        {
            var d = original.Length >= 4 ? (byte[])original.Clone() : new byte[4];
            BitConverter.GetBytes(value).CopyTo(d, 0);
            return d;
        }

        // Triangles split greedily into draw groups that fit VU memory; each group re-indexes its own vertices.
        private static IEnumerable<(List<int> Vertices, List<(int, int, int)> Triangles)> Groups(List<(int A, int B, int C)> triangles)
        {
            var verts = new List<int>();
            var slot = new Dictionary<int, int>();
            var tris = new List<(int, int, int)>();
            foreach (var (a, b, c) in triangles)
            {
                int fresh = new[] { a, b, c }.Distinct().Count(v => !slot.ContainsKey(v));
                if (tris.Count > 0 && (verts.Count + fresh > MaxGroupVertices || tris.Count + 1 > MaxGroupTriangles))
                {
                    yield return (verts, tris);
                    verts = new List<int>();
                    slot = new Dictionary<int, int>();
                    tris = new List<(int, int, int)>();
                }
                int S(int v)
                {
                    if (!slot.TryGetValue(v, out int s)) { s = verts.Count; slot[v] = s; verts.Add(v); }
                    return s;
                }
                tris.Add((S(a), S(b), S(c)));
            }
            if (tris.Count > 0) yield return (verts, tris);
        }

        // STCYCL 0x101, UNPACK V4-16 @4: header (flags 6 = a group's only batch, triangles, matrix, count), then per
        // vertex (x, y, z, slot*2)(nx, ny, nz, weight 1.0 in Q15); MSCNT; padded to a qword.
        private static byte[] VertexBlock(int matrixId, int triangles, List<short[]> verts)
        {
            var m = new MemoryStream();
            int qwords = 1 + 2 * verts.Count;
            m.Write(BitConverter.GetBytes(Stcycl101));
            m.Write(BitConverter.GetBytes(0x6D008004u | (uint)(qwords & 0xFF) << 16));
            foreach (short h in new short[] { 6, (short)triangles, (short)matrixId, (short)verts.Count }) m.Write(BitConverter.GetBytes(h));
            for (int i = 0; i < verts.Count; i++)
            {
                var v = verts[i];
                foreach (short h in new[] { v[0], v[1], v[2], (short)(i * 2), v[3], v[4], v[5], (short)32767 }) m.Write(BitConverter.GetBytes(h));
            }
            m.Write(BitConverter.GetBytes(Mscnt));
            while (m.Length % 16 != 0) m.WriteByte(0);
            return m.ToArray();
        }

        // STCYCL 0x102, UNPACK V4-8 (slot*3 triples) at 3N+4 (+TOPS), STCYCL 0x101, UNPACK V4-32 GIF tags (the
        // template's, NLOOP and the draw counts set), STCYCL 0x103, UNPACK V2-16 UVs (12-bit) at 5, MSCNT, padding.
        private static byte[] DrawBlock(byte[] templateGif, int n, List<(int A, int B, int C)> tris, List<float[]> uvs)
        {
            var m = new MemoryStream();
            int triAt = 3 * n + 4;
            m.Write(BitConverter.GetBytes(Stcycl102));
            m.Write(BitConverter.GetBytes(0x6E000000u | (uint)tris.Count << 16 | 0xC000u | (uint)triAt));
            foreach (var (a, b, c) in tris) m.Write(new[] { (byte)(a * 3), (byte)(b * 3), (byte)(c * 3), (byte)3 });
            m.Write(BitConverter.GetBytes(Stcycl101));
            m.Write(BitConverter.GetBytes(0x6C048000u));
            byte[] gif = (byte[])templateGif.Clone();
            uint nloop = BitConverter.ToUInt32(gif, 16);
            BitConverter.GetBytes((nloop & ~0x7FFFu) | (uint)n).CopyTo(gif, 16);
            BitConverter.GetBytes((uint)triAt).CopyTo(gif, 32);
            BitConverter.GetBytes(0u).CopyTo(gif, 36);
            BitConverter.GetBytes((uint)n).CopyTo(gif, 40);
            BitConverter.GetBytes((uint)tris.Count).CopyTo(gif, 44);
            m.Write(gif);
            m.Write(BitConverter.GetBytes(Stcycl103));
            m.Write(BitConverter.GetBytes(0x65000000u | (uint)n << 16 | 0x8005u));
            foreach (var uv in uvs)
            {
                m.Write(BitConverter.GetBytes(Q(uv[0] * 4096)));
                m.Write(BitConverter.GetBytes(Q(uv[1] * 4096)));
            }
            m.Write(BitConverter.GetBytes(Mscnt));
            while (m.Length % 16 != 0) m.WriteByte(0);
            return m.ToArray();
        }

        // The 4 GIF qwords of the mesh's first draw block (a ref tag whose bits 16-25 are 0x0B).
        private static byte[]? TemplateGif(byte[] payload, int start, int count)
        {
            for (int t = 1; t <= count; t++)
            {
                int o = start + t * 16;
                uint lo = BitConverter.ToUInt32(payload, o);
                if ((lo >> 28 & 7) != 3 || (lo >> 16 & 0x3FF) != 0x0B) continue;
                int addr = BitConverter.ToInt32(payload, o + 4), qwc = (int)(lo & 0xFFFF);
                if (addr < 0 || addr + qwc * 16 > payload.Length) continue;
                for (int p = addr; p + 4 <= addr + qwc * 16;)
                {
                    uint w = BitConverter.ToUInt32(payload, p);
                    int cmd = (int)(w >> 24 & 0x7F), num = (int)(w >> 16 & 0xFF);
                    if (cmd == 0x6C && num == 4 && p + 4 + 64 <= payload.Length) return payload.AsSpan(p + 4, 64).ToArray();
                    if (cmd >= 0x60)
                    {
                        int vn = cmd >> 2 & 3, vl = cmd & 3;
                        int size = vl == 3 ? 2 : (vn + 1) * (4 >> vl);
                        p += 4 + ((num == 0 ? 256 : num) * size + 3) / 4 * 4;
                    }
                    else p += 4;
                }
            }
            return null;
        }

        private static List<float[]> FaceNormals(MeshTopology.InputMesh input)
        {
            var acc = input.Positions.Select(_ => new float[3]).ToList();
            foreach (var (a, b, c) in input.Triangles)
            {
                float[] pa = input.Positions[a], pb = input.Positions[b], pc = input.Positions[c];
                float ux = pb[0] - pa[0], uy = pb[1] - pa[1], uz = pb[2] - pa[2];
                float vx = pc[0] - pa[0], vy = pc[1] - pa[1], vz = pc[2] - pa[2];
                float[] f = { uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx };
                foreach (int v in new[] { a, b, c }) for (int k = 0; k < 3; k++) acc[v][k] += f[k];
            }
            return acc;
        }
    }
}
