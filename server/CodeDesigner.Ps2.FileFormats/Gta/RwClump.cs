using CodeDesigner.Ps2.FileFormats.Assets;
using System.Text;

namespace CodeDesigner.Ps2.FileFormats.Gta
{
    /// <summary>RenderWare binary stream chunks: {type, size, library version} headers, nested.</summary>
    public static class Rw
    {
        public const uint Struct = 0x01, String = 0x02, Extension = 0x03, Texture = 0x06, Material = 0x07, MaterialList = 0x08,
            FrameList = 0x0E, Geometry = 0x0F, Clump = 0x10, Atomic = 0x14, TextureNative = 0x15, TexDictionary = 0x16,
            GeometryList = 0x1A, HAnim = 0x11E, Skin = 0x116, BinMesh = 0x50E, NativeData = 0x510, NodeName = 0x0253F2FE;

        public static IEnumerable<(uint Type, int Data, int Size)> Chunks(byte[] d, int o, int end)
        {
            end = Math.Min(end, d.Length);
            while (o + 12 <= end)
            {
                uint type = BitConverter.ToUInt32(d, o);
                int size = BitConverter.ToInt32(d, o + 4);
                if (size < 0 || o + 12 + size > end) yield break;
                yield return (type, o + 12, size);
                o += 12 + size;
            }
        }

        public static (int Data, int Size)? Find(byte[] d, int o, int end, uint type)
        {
            foreach (var c in Chunks(d, o, end)) if (c.Type == type) return (c.Data, c.Size);
            return null;
        }

        public static string ReadString(byte[] d, int o, int size)
        {
            int end = Array.IndexOf(d, (byte)0, o, size);
            return Encoding.ASCII.GetString(d, o, (end < 0 ? o + size : end) - o);
        }
    }

    /// <summary>
    /// A RenderWare clump (.dff) as GTA: San Andreas ships it for the PS2: frames (with node names and HAnim bone ids),
    /// atomics, and geometries whose vertices are PS2 native data - per mesh (BinMesh split, one material) a DMA chain
    /// of VIF batches unpacking attributes into VU memory slots, ended by ITOP (vertex count) + MSCAL/MSCNT:
    /// <list type="bullet">
    /// <item>default pipeline (weapons, objects): slot 0 positions V3-32, 1 UVs V2-32, 2 prelit colours V4-8 unsigned,
    /// 3 normals V3-8; stored inline (DMA cnt / ret);</item>
    /// <item>skinned peds: slot 0 positions V4-16 (1/1024 units; w = 0x8000 marks a strip restart), 1 UVs V2-16
    /// (1/4096), 2 normals V4-8 signed, 3 weights V4-32 whose low mantissa bits hold the bone (((bits &amp; 0x3FF) &gt;&gt; 2)
    /// - 1); attributes fetched by DMA ref tags;</item>
    /// <item>vehicles: positions V4-16, UVs V2-16 or V4-16 (two sets), normals / colours V4-8.</item>
    /// </list>
    /// UV V is top-down. Skin: numBones, used bones, then the inverse bind matrices (their w corner stored as 0).
    /// </summary>
    public sealed class RwClump
    {
        public sealed record HAnimBone(int Id, int Index, int Flags);

        public sealed class Frame
        {
            public string Name { get; set; } = string.Empty;
            public int Parent { get; init; }
            /// <summary>Local matrix, column-major 4x4 (RW rows right, up, at, position = the same float order).</summary>
            public float[] Local { get; init; } = new float[16];
            public int? BoneId { get; set; }
            public List<HAnimBone>? Hierarchy { get; set; }
        }

        public sealed class Batch
        {
            public int Count { get; set; }
            public float[] Positions { get; set; } = Array.Empty<float>();
            public bool[] Restart { get; set; } = Array.Empty<bool>();
            public float[] Uvs { get; set; } = Array.Empty<float>();
            public float[]? Normals { get; set; }
            public byte[]? Colors { get; set; }
            public float[]? Weights { get; set; }
            public int[]? Bones { get; set; }
            /// <summary>Plain (non-native) geometry: the triangle list over the batch's vertices.</summary>
            public int[]? Indices { get; set; }
        }

        public sealed class Mesh
        {
            public int Material { get; init; }
            public List<Batch> Batches { get; } = new();
        }

        public sealed class Skin
        {
            public int BoneCount { get; init; }
            public byte[] Used { get; init; } = Array.Empty<byte>();
            /// <summary>Inverse bind matrix per bone (column-major 4x4, w corner fixed to 1).</summary>
            public List<float[]> InverseBind { get; } = new();
        }

        public sealed class Geometry
        {
            public uint Flags { get; init; }
            public int TriangleCount { get; init; }
            public int VertexCount { get; init; }
            public bool TriStrip { get; set; } = true;
            public List<string?> Materials { get; } = new();
            public List<byte[]> MaterialColors { get; } = new();
            public List<Mesh> Meshes { get; } = new();
            public Skin? Skin { get; set; }
        }

        public List<Frame> Frames { get; } = new();
        public List<Geometry> Geometries { get; } = new();
        public List<(int Frame, int Geometry)> Atomics { get; } = new();

        public static RwClump Parse(byte[] d)
        {
            var clump = Rw.Find(d, 0, d.Length, Rw.Clump) ?? throw new InvalidDataException("Not a RenderWare clump");
            var c = new RwClump();
            int end = clump.Data + clump.Size;
            if (Rw.Find(d, clump.Data, end, Rw.FrameList) is { } fl) c.ParseFrames(d, fl.Data, fl.Data + fl.Size);
            if (Rw.Find(d, clump.Data, end, Rw.GeometryList) is { } gl)
                foreach (var g in Rw.Chunks(d, gl.Data, gl.Data + gl.Size))
                    if (g.Type == Rw.Geometry) c.Geometries.Add(ParseGeometry(d, g.Data, g.Data + g.Size));
            foreach (var a in Rw.Chunks(d, clump.Data, end))
                if (a.Type == Rw.Atomic && Rw.Find(d, a.Data, a.Data + a.Size, Rw.Struct) is { } s)
                    c.Atomics.Add((BitConverter.ToInt32(d, s.Data), BitConverter.ToInt32(d, s.Data + 4)));
            return c;
        }

        private void ParseFrames(byte[] d, int o, int end)
        {
            var parts = Rw.Chunks(d, o, end).ToList();
            if (parts.Count == 0 || parts[0].Type != Rw.Struct) return;
            int n = BitConverter.ToInt32(d, parts[0].Data);
            for (int i = 0; i < n; i++)
            {
                int p = parts[0].Data + 4 + 56 * i;
                var m = new float[16];
                for (int k = 0; k < 3; k++)
                {
                    m[k] = BitConverter.ToSingle(d, p + 4 * k);            // right
                    m[4 + k] = BitConverter.ToSingle(d, p + 12 + 4 * k);   // up
                    m[8 + k] = BitConverter.ToSingle(d, p + 24 + 4 * k);   // at
                    m[12 + k] = BitConverter.ToSingle(d, p + 36 + 4 * k);  // position
                }
                m[15] = 1;
                Frames.Add(new Frame { Parent = BitConverter.ToInt32(d, p + 48), Local = m });
            }
            int fi = 0;
            foreach (var ext in parts.Skip(1))
            {
                if (ext.Type != Rw.Extension || fi >= Frames.Count) continue;
                var f = Frames[fi++];
                foreach (var e in Rw.Chunks(d, ext.Data, ext.Data + ext.Size))
                {
                    if (e.Type == Rw.NodeName) f.Name = Encoding.ASCII.GetString(d, e.Data, e.Size).TrimEnd('\0');
                    else if (e.Type == Rw.HAnim && e.Size >= 12)
                    {
                        f.BoneId = BitConverter.ToInt32(d, e.Data + 4);
                        int bones = BitConverter.ToInt32(d, e.Data + 8);
                        if (bones > 0 && e.Size >= 20 + 12 * bones)
                            f.Hierarchy = Enumerable.Range(0, bones).Select(k => new HAnimBone(
                                BitConverter.ToInt32(d, e.Data + 20 + 12 * k), BitConverter.ToInt32(d, e.Data + 24 + 12 * k), BitConverter.ToInt32(d, e.Data + 28 + 12 * k))).ToList();
                    }
                }
            }
        }

        private static Geometry ParseGeometry(byte[] d, int o, int end)
        {
            var parts = Rw.Chunks(d, o, end).ToList();
            var st = parts.First(p => p.Type == Rw.Struct);
            var g = new Geometry
            {
                Flags = BitConverter.ToUInt32(d, st.Data), TriangleCount = BitConverter.ToInt32(d, st.Data + 4), VertexCount = BitConverter.ToInt32(d, st.Data + 8),
            };
            if (parts.FirstOrDefault(p => p.Type == Rw.MaterialList) is { Size: > 0 } ml)
                foreach (var m in Rw.Chunks(d, ml.Data, ml.Data + ml.Size).Where(c => c.Type == Rw.Material))
                {
                    var ms = Rw.Find(d, m.Data, m.Data + m.Size, Rw.Struct);
                    g.MaterialColors.Add(ms is { } s ? d[(s.Data + 4)..(s.Data + 8)] : new byte[] { 255, 255, 255, 255 });
                    string? tex = null;
                    if (Rw.Find(d, m.Data, m.Data + m.Size, Rw.Texture) is { } t)
                        foreach (var str in Rw.Chunks(d, t.Data, t.Data + t.Size))
                            if (str.Type == Rw.String) { tex = Rw.ReadString(d, str.Data, str.Size); break; }
                    g.Materials.Add(string.IsNullOrEmpty(tex) ? null : tex);
                }
            var plain = (g.Flags >> 24 & 1) == 0 && st.Size > 16 ? Plain(d, st.Data, st.Data + st.Size, g) : null;
            if (parts.FirstOrDefault(p => p.Type == Rw.Extension) is { Size: > 0 } ext)
            {
                var materials = new List<int>();
                foreach (var e in Rw.Chunks(d, ext.Data, ext.Data + ext.Size))
                {
                    if (e.Type == Rw.BinMesh)
                    {
                        g.TriStrip = (BitConverter.ToUInt32(d, e.Data) & 1) != 0;
                        int n = BitConverter.ToInt32(d, e.Data + 4);
                        for (int i = 0; i < n; i++) materials.Add(BitConverter.ToInt32(d, e.Data + 12 + 8 * i + 4));
                    }
                    else if (e.Type == Rw.NativeData && e.Size > 16 && BitConverter.ToUInt32(d, e.Data) == Rw.Struct)
                    {
                        // A Struct header (its size word is not reliable), platform (4 = PS2), then the meshes up to the
                        // end of the NativeData chunk.
                        int p = e.Data + 12 + 4, nend = e.Data + e.Size;
                        for (int i = 0; p + 8 <= nend; i++)
                        {
                            int size = BitConverter.ToInt32(d, p);
                            if (size <= 0 || p + 8 + size > nend) break;
                            var mesh = new Mesh { Material = i < materials.Count ? materials[i] : 0 };
                            DecodeNative(d, p + 8, size, mesh);
                            g.Meshes.Add(mesh);
                            p += 8 + size;
                        }
                    }
                    else if (e.Type == Rw.Skin && plain != null)
                        g.Skin = PlainSkin(d, e.Data, e.Data + e.Size, g.VertexCount, plain);
                    else if (e.Type == Rw.Skin && Rw.Find(d, e.Data, e.Data + e.Size, Rw.Struct) is { } ss && ss.Size >= 8)
                    {
                        int bones = d[ss.Data + 4], used = d[ss.Data + 5];
                        var skin = new Skin { BoneCount = bones, Used = d[(ss.Data + 8)..(ss.Data + 8 + used)] };
                        int q = ss.Data + 8 + used;
                        for (int b = 0; b < bones && q + 64 <= ss.Data + ss.Size; b++, q += 64)
                        {
                            var m = new float[16];
                            for (int k = 0; k < 16; k++) m[k] = BitConverter.ToSingle(d, q + 4 * k);
                            m[3] = m[7] = m[11] = 0; m[15] = 1;
                            skin.InverseBind.Add(m);
                        }
                        g.Skin = skin;
                    }
                }
            }
            if (plain != null) g.Meshes.AddRange(plain.Meshes(g.Materials.Count));
            return g;
        }

        // ------------------------------------------------------------------ plain (PC-style) geometry

        /// <summary>A plain geometry's vertex arrays (the clothes in PLAYER.IMG are stored like this, not as PS2
        /// native data): positions, normals, UVs, prelit colours, triangles (with material ids), skin.</summary>
        private sealed class PlainData
        {
            public int Count;
            public float[] Positions = Array.Empty<float>(), Uvs = Array.Empty<float>();
            public float[]? Normals;
            public byte[]? Colors;
            public List<(int A, int B, int C, int Material)> Triangles = new();
            public int[]? Bones;
            public float[]? Weights;

            /// <summary>One mesh per material: a batch holding every vertex, indexed by that material's triangles.</summary>
            public IEnumerable<Mesh> Meshes(int materials)
            {
                foreach (var group in Triangles.GroupBy(t => t.Material).OrderBy(x => x.Key))
                {
                    var mesh = new Mesh { Material = Math.Clamp(group.Key, 0, Math.Max(0, materials - 1)) };
                    mesh.Batches.Add(new Batch
                    {
                        Count = Count, Positions = Positions, Uvs = Uvs, Normals = Normals, Colors = Colors, Bones = Bones, Weights = Weights,
                        Indices = group.SelectMany(t => new[] { t.A, t.B, t.C }).ToArray(),
                    });
                    yield return mesh;
                }
            }
        }

        /// <summary>Geometry struct {flags, triangles, vertices, morph targets}; not native: prelit colours (flag 0x08),
        /// UV sets (flags 0x04 / 0x80, count in bits 16-23), triangles {v2, v1, material, v3} (u16), then the first
        /// morph target {sphere, has vertices, has normals, positions, normals}.</summary>
        private static PlainData? Plain(byte[] d, int o, int end, Geometry g)
        {
            uint flags = g.Flags;
            int nt = g.TriangleCount, nv = g.VertexCount;
            if (nv <= 0 || nt <= 0) return null;
            int sets = (int)(flags >> 16 & 0xFF);
            if (sets == 0) sets = (flags & 0x80) != 0 ? 2 : (flags & 0x04) != 0 ? 1 : 0;
            int p = o + 16;
            var pd = new PlainData { Count = nv };
            if ((flags & 0x08) != 0) { if (p + 4 * nv > end) return null; pd.Colors = d[p..(p + 4 * nv)]; p += 4 * nv; }
            if (sets > 0)
            {
                if (p + 8 * nv * sets > end) return null;
                pd.Uvs = new float[2 * nv];
                for (int i = 0; i < 2 * nv; i++) pd.Uvs[i] = BitConverter.ToSingle(d, p + 4 * i);
                p += 8 * nv * sets;
            }
            if (p + 8 * nt > end) return null;
            for (int i = 0; i < nt; i++, p += 8)
            {
                int v2 = BitConverter.ToUInt16(d, p), v1 = BitConverter.ToUInt16(d, p + 2), mat = BitConverter.ToUInt16(d, p + 4), v3 = BitConverter.ToUInt16(d, p + 6);
                if (v1 < nv && v2 < nv && v3 < nv) pd.Triangles.Add((v1, v2, v3, mat));
            }
            p += 16;                                               // bounding sphere
            if (p + 8 > end) return null;
            bool hasPos = BitConverter.ToUInt32(d, p) != 0, hasNrm = BitConverter.ToUInt32(d, p + 4) != 0;
            p += 8;
            if (!hasPos || p + 12 * nv > end) return null;
            pd.Positions = new float[3 * nv];
            for (int i = 0; i < 3 * nv; i++) pd.Positions[i] = BitConverter.ToSingle(d, p + 4 * i);
            p += 12 * nv;
            if (hasNrm && p + 12 * nv <= end)
            {
                pd.Normals = new float[3 * nv];
                for (int i = 0; i < 3 * nv; i++) pd.Normals[i] = BitConverter.ToSingle(d, p + 4 * i);
            }
            if (pd.Uvs.Length == 0) pd.Uvs = new float[2 * nv];
            return pd;
        }

        /// <summary>Plain skin: {bones, used, max weights, pad}, used bone ids, per vertex 4 bone indices (u8) and 4
        /// weights (f32), then an inverse bind matrix per bone.</summary>
        private static Skin? PlainSkin(byte[] d, int o, int end, int nv, PlainData pd)
        {
            if (o + 4 > end) return null;
            int bones = d[o], used = d[o + 1];
            int p = o + 4 + used;
            if (p + 20 * nv + 64 * bones > end) return null;
            pd.Bones = new int[4 * nv];
            for (int i = 0; i < 4 * nv; i++) pd.Bones[i] = d[p + i];
            p += 4 * nv;
            pd.Weights = new float[4 * nv];
            for (int i = 0; i < 4 * nv; i++) pd.Weights[i] = BitConverter.ToSingle(d, p + 4 * i);
            p += 16 * nv;
            var skin = new Skin { BoneCount = bones, Used = d[(o + 4)..(o + 4 + used)] };
            for (int b = 0; b < bones; b++, p += 64)
            {
                var m = new float[16];
                for (int k = 0; k < 16; k++) m[k] = BitConverter.ToSingle(d, p + 4 * k);
                m[3] = m[7] = m[11] = 0; m[15] = 1;
                skin.InverseBind.Add(m);
            }
            return skin;
        }

        // ------------------------------------------------------------------ PS2 native data

        private static void DecodeNative(byte[] d, int start, int size, Mesh mesh)
        {
            int end = start + size;
            var cur = new Batch();
            void Finish()
            {
                if (cur.Positions.Length > 0)
                {
                    int n = cur.Count > 0 ? cur.Count : cur.Positions.Length / 3;
                    n = Math.Min(n, cur.Positions.Length / 3);
                    cur.Count = n;
                    mesh.Batches.Add(cur);
                }
                cur = new Batch();
            }
            void Vif(uint w, int dataAt, int dataEnd, ref int consumed)
            {
                int cmd = (int)((w >> 24) & 0x7F), num = (int)((w >> 16) & 0xFF), imm = (int)(w & 0xFFFF);
                if (cmd >= 0x60)
                {
                    int vn = (cmd >> 2) & 3, vl = cmd & 3;
                    int n = num == 0 ? 256 : num;
                    int bits = vl switch { 0 => 32, 1 => 16, 2 => 8, _ => 16 };
                    int bytes = vl == 3 ? 2 * n : ((vn + 1) * bits * n + 31) / 32 * 4;
                    if (dataAt + bytes <= dataEnd) Unpack(d, dataAt, cmd, n, imm, cur);
                    consumed = bytes;
                }
                else if (cmd == 0x04) cur.Count = imm & 0x3FF;                         // ITOP: vertex count
                else if (cmd is 0x14 or 0x15 or 0x17) Finish();                       // MSCAL / MSCALF / MSCNT
                else if (cmd is 0x20) consumed = 4;
                else if (cmd is 0x30 or 0x31) consumed = 16;
                else if (cmd is 0x50 or 0x51) consumed = (imm == 0 ? 65536 : imm) * 16;
            }
            void Stream(int o, int e)
            {
                while (o + 4 <= e)
                {
                    uint w = BitConverter.ToUInt32(d, o);
                    int consumed = 0;
                    Vif(w, o + 4, e, ref consumed);
                    o += 4 + consumed;
                }
            }

            int t = start;
            while (t + 16 <= end)
            {
                ulong lo = BitConverter.ToUInt64(d, t);
                int id = (int)((lo >> 28) & 7), qwc = (int)(lo & 0xFFFF), addr = (int)((lo >> 32) & 0x7FFFFFFF);
                uint v0 = BitConverter.ToUInt32(d, t + 8), v1 = BitConverter.ToUInt32(d, t + 12);
                if (id == 3)                                                            // ref: the tag's VIF codes unpack the block
                {
                    int block = start + addr * 16, blockEnd = Math.Min(end, block + qwc * 16);
                    int c0 = 0, c1 = 0;
                    Vif(v0, block, blockEnd, ref c0);
                    Vif(v1, block, blockEnd, ref c1);
                    t += 16;
                }
                else                                                                    // cnt / ret / end: VIF codes then inline data
                {
                    int body = t + 16, bodyEnd = Math.Min(end, body + qwc * 16), at = body;
                    int c0 = 0, c1 = 0;
                    Vif(v0, at, bodyEnd, ref c0);
                    at += c0;
                    Vif(v1, at, bodyEnd, ref c1);
                    at += c1;
                    Stream(at, bodyEnd);
                    t = bodyEnd;
                    if (id is 0 or 6 or 7) break;
                }
            }
            Finish();
        }

        private static void Unpack(byte[] d, int at, int cmd, int n, int imm, Batch b)
        {
            int slot = imm & 0x3FF;
            bool unsigned = (imm & 0x4000) != 0;
            int vn = (cmd >> 2) & 3, vl = cmd & 3, comps = vn + 1;
            float Comp(int i, int k) => vl switch
            {
                0 => BitConverter.ToSingle(d, at + 4 * (comps * i + k)),
                1 => unsigned ? BitConverter.ToUInt16(d, at + 2 * (comps * i + k)) : BitConverter.ToInt16(d, at + 2 * (comps * i + k)),
                _ => unsigned ? d[at + comps * i + k] : (sbyte)d[at + comps * i + k],
            };
            switch (slot)
            {
                case 0:                                                                 // positions
                {
                    float s = vl == 1 ? 1f / 1024f : 1f;
                    b.Positions = new float[n * 3];
                    b.Restart = new bool[n];
                    for (int i = 0; i < n; i++)
                    {
                        for (int k = 0; k < 3 && k < comps; k++) b.Positions[3 * i + k] = Comp(i, k) * s;
                        if (comps == 4 && vl == 1) b.Restart[i] = BitConverter.ToInt16(d, at + 2 * (4 * i + 3)) == -32768;
                    }
                    break;
                }
                case 1:                                                                 // UVs (first set)
                {
                    float s = vl == 1 ? 1f / 4096f : 1f;
                    b.Uvs = new float[n * 2];
                    for (int i = 0; i < n; i++) { b.Uvs[2 * i] = Comp(i, 0) * s; b.Uvs[2 * i + 1] = Comp(i, 1) * s; }
                    break;
                }
                default:
                    if (cmd == 0x6C && slot == 3)                                       // skin weights + bone indices
                    {
                        b.Weights = new float[n * 4];
                        b.Bones = new int[n * 4];
                        for (int i = 0; i < n; i++)
                            for (int k = 0; k < 4; k++)
                            {
                                uint bits = BitConverter.ToUInt32(d, at + 16 * i + 4 * k);
                                b.Weights[4 * i + k] = BitConverter.ToSingle(d, at + 16 * i + 4 * k);
                                b.Bones[4 * i + k] = bits == 0 ? -1 : (int)((bits & 0x3FF) >> 2) - 1;
                            }
                    }
                    else if (vl == 2 && unsigned)                                       // prelit colours (RGBA bytes)
                    {
                        b.Colors = new byte[n * 4];
                        for (int i = 0; i < n; i++)
                            for (int k = 0; k < 4; k++) b.Colors[4 * i + k] = k < comps ? d[at + comps * i + k] : (byte)255;
                    }
                    else if (vl == 2)                                                   // normals (signed bytes)
                    {
                        b.Normals = new float[n * 3];
                        for (int i = 0; i < n; i++)
                            for (int k = 0; k < 3; k++) b.Normals[3 * i + k] = (sbyte)d[at + comps * i + k] / 127f;
                    }
                    break;
            }
        }

        /// <summary>Triangles of a batch: strips (alternating winding; restart flags and degenerate triangles skipped)
        /// or lists.</summary>
        public static List<int> Triangles(Batch b, bool strip)
        {
            if (b.Indices != null) return b.Indices.ToList();
            var tris = new List<int>();
            int n = b.Count;
            bool Same(int a, int c) => b.Positions[3 * a] == b.Positions[3 * c] && b.Positions[3 * a + 1] == b.Positions[3 * c + 1] && b.Positions[3 * a + 2] == b.Positions[3 * c + 2];
            if (!strip)
            {
                for (int i = 0; i + 2 < n; i += 3) tris.AddRange(new[] { i, i + 1, i + 2 });
                return tris;
            }
            for (int i = 2; i < n; i++)
            {
                if (b.Restart.Length > i && b.Restart[i]) continue;
                int a = i - 2, c = i - 1;
                if (Same(a, c) || Same(a, i) || Same(c, i)) continue;
                if ((i & 1) == 0) tris.AddRange(new[] { a, c, i }); else tris.AddRange(new[] { c, a, i });
            }
            return tris;
        }

        /// <summary>World matrix per frame (column-major), parents first.</summary>
        public List<float[]> FrameWorlds()
        {
            var list = new List<float[]>();
            foreach (var f in Frames)
                list.Add(f.Parent >= 0 && f.Parent < list.Count ? Assets.Matrix4.Multiply(list[f.Parent], f.Local) : f.Local);
            return list;
        }
    }
}
