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
        /// <paramref name="boneName"/>.
        /// </summary>
        public static Result AddRigidMesh(ZipperPackage pkg, string archive, string modelName, string boneName, string texture, MeshTopology.InputMesh input)
        {
            var entry = pkg.Entry(archive) is { Kind: "mdl" } e ? e : throw new ArgumentException($"{archive} is not a model archive of this package");
            var model = pkg.FindModel(entry.Leaf, modelName) ?? throw new KeyNotFoundException($"No model {modelName} in {entry.Leaf}");
            var container = ZedContainer.FromBytes(entry.Data);
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

            byte[] bytes = entry.Data;
            bytes = ZedSplice.Replace(bytes, model.NodeIndex, data.ToArray());
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
