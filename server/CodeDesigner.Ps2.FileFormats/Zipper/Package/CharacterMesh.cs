using CodeDesigner.Ps2.FileFormats.Assets;
using CodeDesigner.Ps2.FileFormats.Zipper.Geo;
using CodeDesigner.Ps2.FileFormats.Zipper.ZED;

namespace CodeDesigner.Ps2.FileFormats.Zipper.Package
{
    /// <summary>
    /// Skinned character meshes (<c>MESH_&lt;model&gt;</c> in <c>CLIB_MDL.ZED</c>), the format of
    /// <c>Mods/SOCOMCA/charmesh.py</c> / <c>s2mesh.py</c> (confirmed in game by the CA ports). The payload is blocks
    /// then a DMA chain at <c>&lt;model&gt;_START</c>:
    /// <list type="bullet">
    /// <item><c>end</c> tag, qwc = number of tags after it;</item>
    /// <item>per material section a <c>cnt</c> tag whose address is the .tif name field;</item>
    /// <item>per vertex batch a <c>ref</c> to the bone matrix (address = matrix_id, VIF UNPACK V4-32 @0) and a
    /// <c>ref</c> to the vertex block: STCYCL, UNPACK V4-16 header (flags, tris, bone, count) then per vertex
    /// (x, y, z, slot*2)(nx, ny, nz, weight Q15), bone-local, 1/4096 units;</item>
    /// <item>per group a <c>ref</c> to the draw block: UNPACK V4-8 triangles (slot*3), UNPACK V4-32 GIF tags,
    /// UNPACK V2-16 UVs (12-bit) per slot.</item>
    /// </list>
    /// A vertex is the weighted sum of its parts, each a bone-local position through that bone's bind world matrix.
    /// </summary>
    public static class CharacterMesh
    {
        public const float PositionScale = 1f / 4096f;

        /// <summary>True when the MDL record at <paramref name="nodeIndex"/> is a skinned mesh (has a <c>_START</c> child).</summary>
        public static bool IsSkinned(ZedContainer container, int nodeIndex) => StartOffset(container, nodeIndex) != null;

        public static int? StartOffset(ZedContainer container, int nodeIndex)
        {
            var nodes = container.Nodes;
            string name = nodes[nodeIndex].Name;
            int count = (int)nodes[nodeIndex].Type;
            for (int i = nodeIndex + 1; i <= nodeIndex + count && i < nodes.Count; i++)
                if (nodes[i].Name.Equals(name + "_START", StringComparison.OrdinalIgnoreCase) && nodes[i].Data.Length >= 4)
                    return BitConverter.ToInt32(nodes[i].Data, 0);
            return null;
        }

        /// <summary>The GEO skeleton name of a mesh record: <c>MESH_seal_A_woodland_LO</c> → <c>seal_A_woodland_LO</c>.</summary>
        public static string SkeletonName(string meshName) =>
            meshName.StartsWith("MESH_", StringComparison.OrdinalIgnoreCase) ? meshName[5..] : meshName;

        /// <summary>A SOCOM II skeleton from its GEO model (<c>CLIB_GEO.ZED</c> › <c>seal_A_woodland_LO</c>): every scene
        /// node under the model (the model node included as bone 0) with its nparams transform and the matrix_id its
        /// vertex batches name.</summary>
        public static CharacterSkeleton SkeletonFromGeo(GeoNode model)
        {
            var s = new CharacterSkeleton();
            void Visit(GeoNode node, int parent, float[] parentWorld)
            {
                float[] local = node.Transform;
                float[] world = Matrix4.Multiply(parentWorld, local);
                int matrixId = -1;
                foreach (GeoNode vis in node.Visuals)
                    if (vis.Child("matrix_id")?.Data is { Length: >= 4 } d) { matrixId = BitConverter.ToInt32(d, 0); break; }
                int index = s.Bones.Count;
                s.Add(new CharacterSkeleton.Bone(node.Name, matrixId, parent, node.Index, local, world));
                foreach (GeoNode c in node.SceneChildren)
                    if (c.IsSceneNode) Visit(c, index, world);
            }
            Visit(model, -1, Matrix4.Identity());
            return s;
        }

        public static List<SkinnedMesh.Group> Decode(byte[] payload, int start, CharacterSkeleton skeleton)
        {
            var groups = new List<SkinnedMesh.Group>();
            if (start < 0 || start + 16 > payload.Length) throw new InvalidDataException($"Chain start 0x{start:X} is outside the mesh payload");
            int count = (int)(BitConverter.ToUInt32(payload, start) & 0xFFFF);
            string texture = string.Empty;
            int section = -1;
            var pending = new List<(int Bone, int Offset, int Length)>();
            int? pendingMatrix = null;
            for (int t = 1; t <= count; t++)
            {
                int o = start + t * 16;
                if (o + 16 > payload.Length) break;
                uint lo = BitConverter.ToUInt32(payload, o);
                int addr = BitConverter.ToInt32(payload, o + 4);
                uint v0 = BitConverter.ToUInt32(payload, o + 8), v1 = BitConverter.ToUInt32(payload, o + 12);
                int tid = (int)((lo >> 28) & 7), qwc = (int)(lo & 0xFFFF);
                if (tid == 1)
                {
                    section++;
                    texture = ReadName(payload, addr);
                    continue;
                }
                if (v0 == 0x01000101 && v1 == 0x6C048000) { pendingMatrix = addr; continue; }
                if (addr < 0 || addr + qwc * 16 > payload.Length) continue;
                if (pendingMatrix != null)
                {
                    pending.Add((pendingMatrix.Value, addr, qwc * 16));
                    pendingMatrix = null;
                    continue;
                }
                groups.Add(DecodeGroup(payload, texture, Math.Max(0, section), pending, addr, qwc * 16, skeleton));
                pending = new List<(int, int, int)>();
            }
            return groups;
        }

        private static string ReadName(byte[] b, int addr)
        {
            if (addr < 0 || addr >= b.Length) return string.Empty;
            int end = addr;
            while (end < b.Length && end - addr < 32 && b[end] != 0) end++;
            return System.Text.Encoding.Latin1.GetString(b, addr, end - addr);
        }

        private static SkinnedMesh.Group DecodeGroup(byte[] b, string texture, int section, List<(int Bone, int Offset, int Length)> vertexBlocks,
            int drawOffset, int drawLength, CharacterSkeleton skeleton)
        {
            var slots = new SortedDictionary<int, List<SkinnedMesh.Part>>();
            foreach (var (matrixId, offset, length) in vertexBlocks)
            {
                // STCYCL, UNPACK V4-16 @4: header (flags, tris, bone, count), then 16 bytes per vertex.
                int h = offset + 8;
                if (h + 8 > offset + length) continue;
                int bone = BitConverter.ToInt16(b, h + 4);
                int n = BitConverter.ToInt16(b, h + 6);
                // The header's bone is the matrix_id (the chain's matrix ref names the same one).
                int boneIndex = skeleton.IndexOfMatrix(bone);
                if (boneIndex < 0) boneIndex = Math.Max(0, skeleton.IndexOfMatrix(matrixId));
                for (int i = 0; i < n; i++)
                {
                    int v = h + 8 + i * 16;
                    if (v + 16 > offset + length) break;
                    int slot = BitConverter.ToInt16(b, v + 6) / 2;
                    var part = new SkinnedMesh.Part(boneIndex,
                        BitConverter.ToInt16(b, v) * PositionScale, BitConverter.ToInt16(b, v + 2) * PositionScale, BitConverter.ToInt16(b, v + 4) * PositionScale,
                        BitConverter.ToInt16(b, v + 8) / 32767f, BitConverter.ToInt16(b, v + 10) / 32767f, BitConverter.ToInt16(b, v + 12) / 32767f,
                        BitConverter.ToInt16(b, v + 14) / 32767f);
                    if (!slots.TryGetValue(slot, out var list)) slots[slot] = list = new List<SkinnedMesh.Part>();
                    list.Add(part);
                }
            }

            var triangles = new List<int>();
            var uvs = new List<float>();
            foreach (var (cmd, num, at) in Vif(b, drawOffset, drawOffset + drawLength))
            {
                if (cmd == 0x6E)        // UNPACK V4-8: (a, b, c, 3) as slot*3
                {
                    for (int i = 0; i < num; i++)
                    {
                        int p = at + i * 4;
                        triangles.Add(b[p] / 3); triangles.Add(b[p + 1] / 3); triangles.Add(b[p + 2] / 3);
                    }
                }
                else if (cmd == 0x65)   // UNPACK V2-16: 12-bit texture coordinates per slot
                {
                    for (int i = 0; i < num; i++)
                    {
                        uvs.Add(BitConverter.ToInt16(b, at + i * 4) / 4096f);
                        uvs.Add(BitConverter.ToInt16(b, at + i * 4 + 2) / 4096f);
                    }
                }
            }

            int vertexCount = Math.Max(slots.Count == 0 ? 0 : slots.Keys.Max() + 1, uvs.Count / 2);
            var vertices = new List<SkinnedMesh.Part>[vertexCount];
            for (int i = 0; i < vertexCount; i++) vertices[i] = slots.TryGetValue(i, out var l) ? l : new List<SkinnedMesh.Part>();
            while (uvs.Count < vertexCount * 2) uvs.Add(0);
            var tris = new List<int>(triangles.Count);
            for (int i = 0; i + 2 < triangles.Count; i += 3)
                if (triangles[i] < vertexCount && triangles[i + 1] < vertexCount && triangles[i + 2] < vertexCount)
                    tris.AddRange(new[] { triangles[i], triangles[i + 1], triangles[i + 2] });
            return new SkinnedMesh.Group { Texture = texture, Section = section, DrawOffset = drawOffset, Vertices = vertices, Uvs = uvs.Take(vertexCount * 2).ToArray(), Triangles = tris.ToArray() };
        }

        /// <summary>
        /// A SOCOM 3 / Combined Assault skinned mesh object (the bytes from the object to +0x80 + its size word at
        /// +0x1C): material sections found by scanning - a 16-byte texture field (CRC32 name hash in its first word)
        /// then groups of vertex batches (STCYCL 0x101 + UNPACK V4-16 with a two-element header) closed by a draw
        /// block (STCYCL 0x102 ... MSCNT). A vertex batch's bone is the pre-order node index under the model root
        /// (excluded); <paramref name="boneOffset"/> maps it into the skeleton. Port of Mods/SOCOMCA/ca2s2.ca_groups.
        /// </summary>
        public static List<SkinnedMesh.Group> DecodeCa(byte[] b, int start, int end, int boneOffset, int boneCount, Func<uint, string> textureName)
        {
            var groups = new List<SkinnedMesh.Group>();
            end = Math.Min(end, b.Length);
            bool BatchAt(int p) => p + 8 <= end && BitConverter.ToUInt32(b, p) == 0x01000101 && ((BitConverter.ToUInt32(b, p + 4) >> 24) & 0x7F) == 0x6D;
            int section = -1, o = start;
            while (o < end - 8)
            {
                if (!BatchAt(o)) { o += 16; continue; }
                section++;
                string texture = o >= 16 ? textureName(BitConverter.ToUInt32(b, o - 16)) : string.Empty;
                int p = o;
                while (BatchAt(p))
                {
                    // One group: vertex batches, then the draw block up to its MSCNT; the next group or the next
                    // section's texture field follows (never walk across the texture field: it can parse as VIF).
                    var slots = new SortedDictionary<int, List<SkinnedMesh.Part>>();
                    int? drawAt = null, next = null;
                    var triangles = new List<int>();
                    var uvs = new List<float>();
                    int w = p;
                    while (w + 4 <= end)
                    {
                        uint word = BitConverter.ToUInt32(b, w);
                        int cmd = (int)((word >> 24) & 0x7F), num = (int)((word >> 16) & 0xFF), imm = (int)(word & 0xFFFF);
                        if (cmd >= 0x60)
                        {
                            int vn = (cmd >> 2) & 3, vl = cmd & 3;
                            int elem = vl == 3 ? 2 : (vl switch { 0 => 4, 1 => 2, _ => 1 }) * (vn + 1);
                            int n = num == 0 ? 256 : num, at = w + 4, len = (elem * n + 3) / 4 * 4;
                            if (at + len > end) break;
                            if (cmd == 0x6D)        // UNPACK V4-16: vertex batch (flags, tris, bone, count) + 1 more element
                            {
                                int bone = BitConverter.ToInt16(b, at + 4), count = BitConverter.ToInt16(b, at + 6);
                                int boneIndex = bone >= 0 && bone < boneCount ? bone + boneOffset : boneOffset;
                                for (int i = 0; i < count && 16 + 16 * i + 16 <= len; i++)
                                {
                                    int v = at + 16 + 16 * i;
                                    int slot = BitConverter.ToInt16(b, v + 6) / 2;
                                    var part = new SkinnedMesh.Part(boneIndex,
                                        BitConverter.ToInt16(b, v) * PositionScale, BitConverter.ToInt16(b, v + 2) * PositionScale, BitConverter.ToInt16(b, v + 4) * PositionScale,
                                        BitConverter.ToInt16(b, v + 8) / 32767f, BitConverter.ToInt16(b, v + 10) / 32767f, BitConverter.ToInt16(b, v + 12) / 32767f,
                                        BitConverter.ToInt16(b, v + 14) / 32767f);
                                    if (!slots.TryGetValue(slot, out var l)) slots[slot] = l = new List<SkinnedMesh.Part>();
                                    l.Add(part);
                                }
                            }
                            else if (cmd == 0x6E)   // UNPACK V4-8: triangles (a, b, c, 3) as slot*3
                                for (int i = 0; i < n; i++) { triangles.Add(b[at + i * 4] / 3); triangles.Add(b[at + i * 4 + 1] / 3); triangles.Add(b[at + i * 4 + 2] / 3); }
                            else if (cmd == 0x65)   // UNPACK V2-16: 12-bit UVs per slot. CA stores V negated (in [-1, 0]:
                                                    // the top of the head at -0.04 samples the hair rows), so flip it.
                                for (int i = 0; i < n; i++) { uvs.Add(BitConverter.ToInt16(b, at + i * 4) / 4096f); uvs.Add(-BitConverter.ToInt16(b, at + i * 4 + 2) / 4096f); }
                            else if (cmd != 0x6C) break;
                            w = at + len;
                        }
                        else if (cmd == 0x01) { if (imm == 0x102 && drawAt == null) drawAt = w; w += 4; }
                        else if (cmd == 0x17) { if (drawAt != null) { next = (w + 4 + 15) & ~15; break; } w += 4; }
                        else if (cmd is 0x00 or 0x14 or 0x15) w += 4;
                        else break;
                    }
                    if (next == null || drawAt == null) break;
                    int vertexCount = Math.Max(slots.Count == 0 ? 0 : slots.Keys.Max() + 1, uvs.Count / 2);
                    var vertices = new List<SkinnedMesh.Part>[vertexCount];
                    for (int i = 0; i < vertexCount; i++) vertices[i] = slots.TryGetValue(i, out var l) ? l : new List<SkinnedMesh.Part>();
                    while (uvs.Count < vertexCount * 2) uvs.Add(0);
                    var tris = new List<int>(triangles.Count);
                    for (int i = 0; i + 2 < triangles.Count; i += 3)
                        if (triangles[i] < vertexCount && triangles[i + 1] < vertexCount && triangles[i + 2] < vertexCount)
                            tris.AddRange(new[] { triangles[i], triangles[i + 1], triangles[i + 2] });
                    groups.Add(new SkinnedMesh.Group { Texture = texture, Section = section, DrawOffset = drawAt.Value, Vertices = vertices, Uvs = uvs.Take(vertexCount * 2).ToArray(), Triangles = tris.ToArray() });
                    p = next.Value;
                }
                o = Math.Max(o + 16, p + 16);
            }
            return groups;
        }

        /// <summary>VIF commands in [start, end): (command, num, payload offset) for UNPACKs; walks past the others.</summary>
        private static IEnumerable<(int Cmd, int Num, int At)> Vif(byte[] b, int start, int end)
        {
            int o = start;
            while (o + 4 <= end)
            {
                uint w = BitConverter.ToUInt32(b, o);
                int cmd = (int)((w >> 24) & 0x7F), num = (int)((w >> 16) & 0xFF);
                if (cmd >= 0x60)
                {
                    int vn = (cmd >> 2) & 3, vl = cmd & 3;
                    int elem = vl switch { 0 => 4, 1 => 2, 2 => 1, _ => 0 } * (vn + 1);
                    if (vl == 3) elem = 2;
                    int n = num == 0 ? 256 : num;
                    int len = (elem * n + 3) / 4 * 4;
                    yield return (cmd, n, o + 4);
                    o += 4 + len;
                }
                else if (cmd is 0x30 or 0x31) o += 20;
                else if (cmd == 0x20) o += 8;
                else if (cmd is 0x50 or 0x51) { int imm = (int)(w & 0xFFFF); o += 4 + (imm == 0 ? 65536 : imm) * 16; }
                else if (cmd is 0 or 1 or 2 or 3 or 4 or 5 or 6 or 7 or 0x10 or 0x11 or 0x13 or 0x14 or 0x15 or 0x17) o += 4;
                else yield break;
            }
        }
    }
}
