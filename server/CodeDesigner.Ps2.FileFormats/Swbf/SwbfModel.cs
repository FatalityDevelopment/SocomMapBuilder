using CodeDesigner.Ps2.FileFormats.Assets;

namespace CodeDesigner.Ps2.FileFormats.Swbf
{
    /// <summary>
    /// A Battlefront PS2 model (a "modl" chunk) as a <see cref="ModelMesh"/>. The model's INFO holds three u32s (the
    /// first is 1 when positions are stored in bone bind space), then the vertex bounding box (min xyz, max xyz) that
    /// positions are quantised to, a second (visibility) box, and two u32s. Each "segm" is one draw: INFO
    /// {u32 vertices, u32 indices}, MTRL flags, RTYP render type, TNAM {u32 slot, texture name}, STRP u16 strip
    /// indices (bit 15 marks the first two of each strip), POSI u16 x3 across the box (0 = min, 0xFFFF = max),
    /// NORM s8 x3, TEX0 s16 x2 in 4.11 fixed point (V top down), optional COL0 (4 bytes per vertex, A B G R) and, for
    /// skinned segments, BMAP {u32 n, n skeleton bones} / BONE / WGHT / BIND {4 x slot * 4}. In a bone-space model a
    /// vertex is in its bone's bind space: the segment's BNAM bone, else BMAP[BIND[0] >> 2]; the model's "skel" gives
    /// the bind matrices. Positions are Y up with +Z forward and +X a character's left, as the editors draw them.
    /// (The unsigned quantisation and the bone space follow openbf's research, MIT licensed.)
    /// </summary>
    public static class SwbfModel
    {
        public sealed record Segment(string? Texture, string RenderType, int Vertices, int Indices, bool Colours, bool Skinned);

        public static string Name(Ucfb modl) => modl.ChildText("NAME") ?? "";

        public static IEnumerable<Segment> Segments(Ucfb modl) =>
            modl.Children.Where(c => c.Tag == "segm").Select(s =>
            {
                var info = s.Child("INFO");
                return new Segment(TextureOf(s), s.ChildText("RTYP") ?? "", info?.I32(0) ?? 0, info?.I32(4) ?? 0, s.Child("COL0") != null, s.Child("WGHT") != null);
            });

        private static string? TextureOf(Ucfb segm)
        {
            var t = segm.Child("TNAM");
            if (t is not { } tn || tn.Size <= 4) return null;
            string name = tn.Text(4);
            return name.Length > 0 ? name : null;
        }

        /// <summary>Bind bone-to-model matrices of a "skel" chunk, in its bone order (row-vector 4x4 as 16 floats,
        /// v' = v * M): INFO {name, u32 count}, NAME / PRNT packed names (parent "" for a root), XFRM 12 floats per bone
        /// (rotation rows, then translation, local to the parent).</summary>
        public static List<(string Name, string Parent, float[] Bind)> BindMatrices(Ucfb skel)
        {
            var result = new List<(string, string, float[])>();
            if (skel.Child("NAME") is not { } nc || skel.Child("XFRM") is not { } xf) return result;
            int n = xf.Size / 48;
            var names = Strings(nc, n);
            var parents = skel.Child("PRNT") is { } pc ? Strings(pc, n) : Enumerable.Repeat("", n).ToList();
            var local = new float[n][];
            for (int i = 0; i < n; i++)
            {
                var m = new float[16];
                for (int r = 0; r < 3; r++) for (int c = 0; c < 3; c++) m[r * 4 + c] = xf.F32(i * 48 + (r * 3 + c) * 4);
                for (int c = 0; c < 3; c++) m[12 + c] = xf.F32(i * 48 + 36 + c * 4);
                m[15] = 1;
                local[i] = m;
            }
            var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < n; i++) index.TryAdd(names[i], i);
            var world = new float[n][];
            float[] Resolve(int i, int depth)
            {
                if (world[i] != null) return world[i];
                if (depth > n || parents[i].Length == 0 || !index.TryGetValue(parents[i], out int p) || p == i) return world[i] = local[i];
                return world[i] = MulRow(local[i], Resolve(p, depth + 1));
            }
            for (int i = 0; i < n; i++) result.Add((names[i], parents[i], Resolve(i, 0)));
            return result;
        }

        /// <summary>A skel's bones as a <see cref="CharacterSkeleton"/>, in the skel's order (vertex bone indices point
        /// into it; the skel does not list parents first): World is the bind matrix, Local relative to the parent.</summary>
        private static CharacterSkeleton Skeleton(List<(string Name, string Parent, float[] Bind)> bones)
        {
            var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < bones.Count; i++) index.TryAdd(bones[i].Name, i);
            return CharacterSkeleton.FromBones(bones.Select((b, i) =>
            {
                int parent = b.Parent.Length > 0 && index.TryGetValue(b.Parent, out int p) && p != i ? p : -1;
                float[] local = parent >= 0 ? MulRow(b.Bind, Inverse(bones[parent].Bind)) : (float[])b.Bind.Clone();
                return new CharacterSkeleton.Bone(b.Name, i, parent, -1, local, (float[])b.Bind.Clone());
            }));
        }

        /// <summary>The inverse of a rigid row-vector matrix (rotation rows, translation in 12..14).</summary>
        private static float[] Inverse(float[] m)
        {
            var r = new float[16];
            for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) r[i * 4 + j] = m[j * 4 + i];
            for (int j = 0; j < 3; j++) r[12 + j] = -(m[12] * r[j] + m[13] * r[4 + j] + m[14] * r[8 + j]);
            r[15] = 1;
            return r;
        }

        private static List<string> Strings(Ucfb c, int n)
        {
            var list = new List<string>(n);
            var b = c.Bytes;
            int at = 0;
            for (int i = 0; i < n; i++)
            {
                int z = at < b.Length ? b[at..].IndexOf((byte)0) : -1;
                int end = z < 0 ? b.Length : at + z;
                list.Add(at < b.Length ? System.Text.Encoding.ASCII.GetString(b[at..end]) : "");
                at = Math.Min(end + 1, b.Length);
            }
            return list;
        }

        private static float[] MulRow(float[] a, float[] b)
        {
            var m = new float[16];
            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 4; j++)
                {
                    float sum = 0;
                    for (int k = 0; k < 4; k++) sum += a[i * 4 + k] * b[k * 4 + j];
                    m[i * 4 + j] = sum;
                }
            return m;
        }

        /// <summary>The model as a mesh in its bind pose. With <paramref name="skin"/> (for export), a bone-space model
        /// also carries its skeleton (bind matrices as <see cref="CharacterSkeleton.Bone.World"/>) and each vertex its one
        /// bone at weight 1 (the retail WGHT is 255/0/0/0 throughout).</summary>
        public static ModelMesh Build(Ucfb modl, string archive, Ucfb? skel = null, bool skin = false)
        {
            string name = Name(modl);
            var info = modl.Child("INFO") ?? throw new InvalidDataException($"Model {name} has no INFO");
            float[] min = { info.F32(12), info.F32(16), info.F32(20) };
            float[] max = { info.F32(24), info.F32(28), info.F32(32) };
            float[] step = new float[3];
            for (int k = 0; k < 3; k++) step[k] = (max[k] - min[k]) / 65535f;
            bool boneSpace = info.U32(0) == 1;
            var bones = boneSpace && skel is { } sk ? BindMatrices(sk) : new List<(string Name, string Parent, float[] Bind)>();
            var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < bones.Count; i++) byName.TryAdd(bones[i].Name, i);
            bool skinned = skin && boneSpace && bones.Count > 0;
            var mesh = new ModelMesh { Archive = archive, Name = name, Skeleton = skinned ? Skeleton(bones) : null };

            int index = 0;
            foreach (var segm in modl.Children.Where(c => c.Tag == "segm"))
            {
                int number = index++;
                var sinfo = segm.Child("INFO");
                if (sinfo is not { } si) continue;
                int n = si.I32(0);
                var posi = segm.Child("POSI");
                var strp = segm.Child("STRP");
                if (n <= 0 || posi is not { } p || strp is not { } st || p.Size < n * 6) continue;

                // Each vertex's bone in a bone-space model: the segment's bone, else its first influence's.
                int hard = boneSpace && segm.ChildText("BNAM") is { Length: > 0 } bn && byName.TryGetValue(bn, out var hb) ? hb : -1;
                byte[]? bmap = segm.Child("BMAP") is { } bm && bm.Size >= 4 ? bm.Bytes.Slice(4, Math.Clamp(bm.I32(0), 0, bm.Size - 4)).ToArray() : null;
                var bindChunk = segm.Child("BIND");
                int BoneOf(int v)
                {
                    if (!boneSpace) return -1;
                    if (hard >= 0) return hard;
                    if (bmap == null || bindChunk is not { } bc || bc.Size < (v + 1) * 4) return -1;
                    int slot = bc.Data[bc.Body + v * 4] >> 2;
                    int bone = slot < bmap.Length ? bmap[slot] : -1;
                    return bone >= 0 && bone < bones.Count ? bone : -1;
                }
                float[]? MatrixOf(int v) => BoneOf(v) is >= 0 and var b ? bones[b].Bind : null;

                var pos = new float[n * 3];
                var mats = new float[]?[n];
                for (int i = 0; i < n; i++)
                {
                    float x = min[0] + p.U16(i * 6) * step[0], y = min[1] + p.U16(i * 6 + 2) * step[1], z = min[2] + p.U16(i * 6 + 4) * step[2];
                    if ((mats[i] = MatrixOf(i)) is { } m)
                        (x, y, z) = (x * m[0] + y * m[4] + z * m[8] + m[12], x * m[1] + y * m[5] + z * m[9] + m[13], x * m[2] + y * m[6] + z * m[10] + m[14]);
                    pos[i * 3] = x; pos[i * 3 + 1] = y; pos[i * 3 + 2] = z;
                }
                var nrm = new float[n * 3];
                if (segm.Child("NORM") is { } nm && nm.Size >= n * 3)
                    for (int i = 0; i < n; i++)
                    {
                        float x = (sbyte)nm.Data[nm.Body + i * 3], y = (sbyte)nm.Data[nm.Body + i * 3 + 1], z = (sbyte)nm.Data[nm.Body + i * 3 + 2];
                        if (mats[i] is { } m) (x, y, z) = (x * m[0] + y * m[4] + z * m[8], x * m[1] + y * m[5] + z * m[9], x * m[2] + y * m[6] + z * m[10]);
                        float len = MathF.Sqrt(x * x + y * y + z * z);
                        if (len < 1e-3f) { nrm[i * 3 + 1] = 1; continue; }
                        nrm[i * 3] = x / len; nrm[i * 3 + 1] = y / len; nrm[i * 3 + 2] = z / len;
                    }
                else for (int i = 0; i < n; i++) nrm[i * 3 + 1] = 1;
                var uv = new float[n * 2];
                if (segm.Child("TEX0") is { } tx && tx.Size >= n * 4)
                    for (int i = 0; i < n; i++)
                    {
                        uv[i * 2] = tx.S16(i * 4) / 2048f;
                        uv[i * 2 + 1] = 1f - tx.S16(i * 4 + 2) / 2048f;
                    }
                byte[]? col = null;
                if (segm.Child("COL0") is { } c0 && c0.Size >= n * 4)
                {
                    col = new byte[n * 4];
                    for (int i = 0; i < n; i++)
                    {
                        int at = c0.Body + i * 4;
                        // A B G R, 0xFF = 1.0 -> R G B A, 0x80 = 1.0
                        col[i * 4] = (byte)(c0.Data[at + 3] >> 1);
                        col[i * 4 + 1] = (byte)(c0.Data[at + 2] >> 1);
                        col[i * 4 + 2] = (byte)(c0.Data[at + 1] >> 1);
                        col[i * 4 + 3] = (byte)(c0.Data[at] >> 1);
                    }
                }
                var tri = Triangles(st, n);
                if (tri.Count == 0) continue;
                int[]? skinBones = null;
                float[]? skinWeights = null;
                if (skinned)
                {
                    skinBones = new int[n * ModelMesh.SkinInfluences];
                    skinWeights = new float[n * ModelMesh.SkinInfluences];
                    Array.Fill(skinBones, -1);
                    for (int i = 0; i < n; i++)
                    {
                        int b = BoneOf(i);
                        skinBones[i * ModelMesh.SkinInfluences] = b >= 0 ? b : 0;
                        skinWeights[i * ModelMesh.SkinInfluences] = 1;
                    }
                }
                mesh.Chunks.Add(new ModelMesh.MeshChunk
                {
                    Index = number, GeometryOffset = segm.Offset, Visual = $"segment{number}", Texture = TextureOf(segm),
                    Unit = "Quantised", Scale = 1, VertexCount = n, Positions = pos, Uvs = uv, Normals = nrm, Colors = col,
                    Triangles = tri.ToArray(), SkinBones = skinBones, SkinWeights = skinWeights,
                });
            }
            mesh.Recompute();
            return mesh;
        }

        /// <summary>Triangles from a STRP run: bit 15 on two indices in a row starts a strip; winding alternates and is
        /// counter-clockwise from the front; degenerate triangles are dropped.</summary>
        public static List<int> Triangles(Ucfb strp, int vertices)
        {
            int count = strp.Size / 2;
            var tri = new List<int>(count * 3);
            int a = -1, b = -1, k = 0;
            for (int i = 0; i < count; i++)
            {
                int raw = strp.U16(i * 2);
                int v = raw & 0x7FFF;
                bool start = (raw & 0x8000) != 0 && i + 1 < count && (strp.U16(i * 2 + 2) & 0x8000) != 0;
                if (start)
                {
                    a = v;
                    b = strp.U16(i * 2 + 2) & 0x7FFF;
                    i++;
                    k = 0;
                    continue;
                }
                if (a >= 0 && b >= 0 && a != b && b != v && a != v && a < vertices && b < vertices && v < vertices)
                {
                    if ((k & 1) == 0) { tri.Add(a); tri.Add(b); tri.Add(v); }
                    else { tri.Add(b); tri.Add(a); tri.Add(v); }
                }
                a = b;
                b = v;
                k++;
            }
            return tri;
        }
    }
}
