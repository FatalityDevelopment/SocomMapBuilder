using CodeDesigner.Ps2.FileFormats.Zipper.ZED;

namespace CodeDesigner.Ps2.FileFormats.Zipper.Model
{
    /// <summary>
    /// A chain-drawn model payload as editable structure, and the writer that lays it out again (port of
    /// Mods/SOCOMCA/s2static.py parse / encode, the layout confirmed in game). Each N&lt;node&gt;_&lt;visual&gt; stub
    /// points at a chain: per material section a name field (kind 6), a material block (kind 3), then per chunk a
    /// colour block (kind 4) and a geometry chunk (kind 2). Retail payloads are fully covered by their chains (all 728
    /// chain models of M51 / MP71 / MP6 / MP53 / M62), so re-laying one loses nothing. Blocks shared by several
    /// visuals stay shared (written once).
    /// </summary>
    public sealed class ModelLayout
    {
        public sealed class Block
        {
            public byte[] Data { get; set; }
            /// <summary>The tag's first word above qwc (kind, flags): kept so retail tag flags survive.</summary>
            public uint TagHigh { get; set; }
            public uint Upper0 { get; set; }
            public uint Upper1 { get; set; }
            /// <summary>Offset in the payload the block was parsed from (-1 for new blocks).</summary>
            public int SourceOffset { get; init; } = -1;
            public Block(byte[] data, uint tagHigh, uint upper0 = 0, uint upper1 = 0) { Data = data; TagHigh = tagHigh; Upper0 = upper0; Upper1 = upper1; }
        }

        public sealed class LayoutChunk
        {
            public Block? Colour { get; set; }
            public required Block Geometry { get; set; }
        }

        public sealed class Section
        {
            public required Block NameField { get; set; }
            public Block? Material { get; set; }
            public List<LayoutChunk> Chunks { get; } = new();

            public string TextureName
            {
                get
                {
                    int end = Array.IndexOf(NameField.Data, (byte)0);
                    return System.Text.Encoding.ASCII.GetString(NameField.Data, 0, end < 0 ? NameField.Data.Length : end);
                }
            }
        }

        public sealed class Visual
        {
            public required string Key { get; init; }
            /// <summary>Index of the N* stub record in the container.</summary>
            public int StubIndex { get; init; }
            /// <summary>Chain header words 1-3 (word 0 is the tag count).</summary>
            public uint[] HeaderExtra { get; init; } = new uint[3];
            public List<Section> Sections { get; } = new();
        }

        public List<Visual> Visuals { get; } = new();

        /// <summary>Parses the model record at <paramref name="nodeIndex"/>; null when it has no valid chains.</summary>
        public static ModelLayout? Parse(ZedContainer container, int nodeIndex)
        {
            byte[] d = container.Nodes[nodeIndex].Data;
            var stubs = ZedTree.Children(container.Nodes, nodeIndex).Where(i => container.Nodes[i].Data.Length == 4).ToList();
            if (stubs.Count == 0) return null;

            // Every block start, to size the name fields (they run to the next block).
            var starts = new SortedSet<int>();
            foreach (int s in stubs)
            {
                int o = (int)BitConverter.ToUInt32(container.Nodes[s].Data, 0);
                if (o < 0 || o + 16 > d.Length) return null;
                starts.Add(o);
                int count = (int)(BitConverter.ToUInt32(d, o) & 0xFFFF);
                if (o + 16 + count * 16 > d.Length) return null;
                for (int i = 0; i < count; i++) starts.Add((int)BitConverter.ToUInt32(d, o + 20 + i * 16));
            }
            int NextStart(int at) => starts.GetViewBetween(at + 1, int.MaxValue).DefaultIfEmpty(d.Length).First();

            var shared = new Dictionary<(int, int), Block>();
            Block Get(int addr, int length, uint high, uint u0, uint u1)
            {
                if (addr < 0 || addr + length > d.Length) throw new InvalidDataException($"block at {addr}+{length} is outside the payload");
                if (!shared.TryGetValue((addr, length), out var b))
                {
                    b = new Block(d[addr..(addr + length)], high, u0, u1) { SourceOffset = addr };
                    shared[(addr, length)] = b;
                }
                return b;
            }

            var layout = new ModelLayout();
            foreach (int s in stubs)
            {
                int o = (int)BitConverter.ToUInt32(container.Nodes[s].Data, 0);
                int count = (int)(BitConverter.ToUInt32(d, o) & 0xFFFF);
                var visual = new Visual
                {
                    Key = container.Nodes[s].Name,
                    StubIndex = s,
                    HeaderExtra = new[] { BitConverter.ToUInt32(d, o + 4), BitConverter.ToUInt32(d, o + 8), BitConverter.ToUInt32(d, o + 12) },
                };
                Block? colour = null;
                for (int i = 0; i < count; i++)
                {
                    int t = o + 16 + i * 16;
                    uint lo = BitConverter.ToUInt32(d, t);
                    int addr = (int)BitConverter.ToUInt32(d, t + 4);
                    uint u0 = BitConverter.ToUInt32(d, t + 8), u1 = BitConverter.ToUInt32(d, t + 12);
                    int kind = (int)((lo >> 16) & 0xFF);
                    int len = (int)(lo & 0xFFFF) * 16;
                    uint high = lo & 0xFFFF0000;
                    switch (kind)
                    {
                        case 6:
                            visual.Sections.Add(new Section { NameField = Get(addr, NextStart(addr) - addr, high, u0, u1) });
                            colour = null;
                            break;
                        case 3:
                            if (visual.Sections.Count == 0) return null;
                            visual.Sections[^1].Material = Get(addr, len, high, u0, u1);
                            break;
                        case 4:
                            colour = Get(addr, len, high, u0, u1);
                            break;
                        case 2:
                            if (visual.Sections.Count == 0) return null;
                            visual.Sections[^1].Chunks.Add(new LayoutChunk { Colour = colour, Geometry = Get(addr, len, high, u0, u1) });
                            colour = null;
                            break;
                        default:
                            return null; // line strips (kind 1) and unknown tags are not editable here
                    }
                }
                layout.Visuals.Add(visual);
            }
            return layout;
        }

        /// <summary>
        /// Lays the model out again (per visual: geometry, then material + name per section, then colours and the
        /// chain; shared blocks once) and writes the payload and the N* stub offsets into the container.
        /// </summary>
        public void Write(ZedContainer container, int nodeIndex)
        {
            var data = new List<byte>();
            var placed = new Dictionary<Block, int>(ReferenceEqualityComparer.Instance);
            int Place(Block b)
            {
                if (placed.TryGetValue(b, out int at)) return at;
                at = data.Count;
                data.AddRange(b.Data);
                while (data.Count % 16 != 0) data.Add(0);
                placed[b] = at;
                return at;
            }

            foreach (var v in Visuals)
            {
                foreach (var s in v.Sections) foreach (var c in s.Chunks) Place(c.Geometry);
                foreach (var s in v.Sections) { if (s.Material != null) Place(s.Material); Place(s.NameField); }
            }
            foreach (var v in Visuals)
            {
                var tags = new List<uint[]>();
                foreach (var s in v.Sections)
                {
                    tags.Add(new[] { s.NameField.TagHigh, (uint)Place(s.NameField), s.NameField.Upper0, s.NameField.Upper1 });
                    if (s.Material != null)
                        tags.Add(new[] { s.Material.TagHigh | (uint)(s.Material.Data.Length / 16), (uint)Place(s.Material), s.Material.Upper0, s.Material.Upper1 });
                    foreach (var c in s.Chunks)
                    {
                        if (c.Colour != null)
                            tags.Add(new[] { c.Colour.TagHigh | (uint)(c.Colour.Data.Length / 16), (uint)Place(c.Colour), c.Colour.Upper0, c.Colour.Upper1 });
                        tags.Add(new[] { c.Geometry.TagHigh | (uint)(c.Geometry.Data.Length / 16), (uint)Place(c.Geometry), c.Geometry.Upper0, c.Geometry.Upper1 });
                    }
                }
                int chainAt = data.Count;
                data.AddRange(BitConverter.GetBytes((uint)tags.Count));
                foreach (uint w in v.HeaderExtra) data.AddRange(BitConverter.GetBytes(w));
                foreach (var t in tags) foreach (uint w in t) data.AddRange(BitConverter.GetBytes(w));
                container.ReplaceNodeData(v.StubIndex, BitConverter.GetBytes((uint)chainAt));
            }
            container.ReplaceNodeData(nodeIndex, data.ToArray());
        }

        // ------------------------------------------------------------------ chunks

        /// <summary>Split size for new geometry (s2static encode_mesh; fits VU1 memory with room to spare).</summary>
        public const int MaxChunkVertices = 72, MaxChunkTriangles = 48;

        /// <summary>Decoded parts of a geometry chunk, as raw records (lossless).</summary>
        public sealed class ChunkData
        {
            /// <summary>The first 0x28 bytes: STCYCL, UNPACK and the two GIF tags.</summary>
            public required byte[] Head { get; init; }
            /// <summary>Header word after the index address (1 in retail chunks, 0 from s2static).</summary>
            public uint IndexWord { get; set; } = 1;
            public float[] Origin { get; set; } = new float[3];
            public float Scale { get; set; } = 1;
            /// <summary>16-byte vertex records: int16 x, y, z, nx, u, v, ny, nz.</summary>
            public List<byte[]> Vertices { get; } = new();
            public List<(int A, int B, int C)> Triangles { get; } = new();
            /// <summary>Per-triangle flag byte (3 in new geometry; retail also uses 1).</summary>
            public List<byte> TriangleFlags { get; } = new();
            /// <summary>6-byte face normals, one per triangle (int16 x, y, z).</summary>
            public List<byte[]> FaceNormals { get; } = new();
        }

        public static ChunkData ReadChunk(byte[] g)
        {
            int n = BitConverter.ToInt32(g, 0x30), t = BitConverter.ToInt32(g, 0x34);
            var c = new ChunkData
            {
                Head = g[..0x28],
                IndexWord = BitConverter.ToUInt32(g, 0x2C),
                Origin = new[] { BitConverter.ToSingle(g, 0x38), BitConverter.ToSingle(g, 0x3C), BitConverter.ToSingle(g, 0x40) },
                Scale = BitConverter.ToSingle(g, 0x44),
            };
            int vs = 0x50;
            for (int i = 0; i < n; i++) c.Vertices.Add(g[(vs + i * 16)..(vs + i * 16 + 16)]);
            int td = vs + n * 16 + 8;
            for (int i = 0; i < t; i++)
            {
                c.Triangles.Add((g[td + i * 4] / 3, g[td + i * 4 + 1] / 3, g[td + i * 4 + 2] / 3));
                c.TriangleFlags.Add(g[td + i * 4 + 3]);
            }
            int fn = td + t * 4 + 8; // after STCYCL + UNPACK V3-16
            for (int i = 0; i < t; i++)
                c.FaceNormals.Add(fn + i * 6 + 6 <= g.Length ? g[(fn + i * 6)..(fn + i * 6 + 6)] : new byte[6]);
            return c;
        }

        /// <summary>
        /// Encodes a geometry chunk (the s2static encode_chunk layout, game-confirmed), keeping the source chunk's
        /// GIF tags with the vertex count updated.
        /// </summary>
        public static byte[] WriteChunk(ChunkData c)
        {
            int nv = c.Vertices.Count, nt = c.Triangles.Count;
            if (nv == 0 || nt == 0) throw new ArgumentException("A chunk needs vertices and triangles");
            // Hard limits: triangle indices are stored as index * 3 in a byte, counts in UNPACK num fields.
            if (nv > 85 || nt > 255) throw new ArgumentException($"A chunk holds at most 85 vertices and 255 triangles (new geometry is split at {MaxChunkVertices} / {MaxChunkTriangles})");
            int idx = 4 + 3 * nv;
            var o = new List<byte>(c.Head);
            // Second GIF tag: NLOOP (low 15 bits) = vertex count.
            uint gif2 = BitConverter.ToUInt32(c.Head, 0x18);
            byte[] gif = BitConverter.GetBytes((gif2 & 0xFFFF8000) | (uint)nv);
            for (int k = 0; k < 4; k++) o[0x18 + k] = gif[k];
            void U32(uint v) => o.AddRange(BitConverter.GetBytes(v));
            void F32(float v) => o.AddRange(BitConverter.GetBytes(v));
            U32((uint)idx); U32(c.IndexWord); U32((uint)nv); U32((uint)nt);
            F32(c.Origin[0]); F32(c.Origin[1]); F32(c.Origin[2]); F32(c.Scale);
            U32(0x01000203); U32(0x6D000000u | (uint)(2 * nv) << 16 | 0x8004);
            foreach (var v in c.Vertices) o.AddRange(v);
            U32(0x01000102); U32(0x6E000000u | (uint)nt << 16 | 0xC000 | (uint)idx);
            for (int i = 0; i < nt; i++)
            {
                var (a, b, cc) = c.Triangles[i];
                o.Add((byte)(a * 3)); o.Add((byte)(b * 3)); o.Add((byte)(cc * 3));
                o.Add(i < c.TriangleFlags.Count ? c.TriangleFlags[i] : (byte)3);
            }
            U32(0x01000102); U32(0x69000000u | (uint)nt << 16 | 0x8000 | (uint)(idx + 1));
            foreach (var fnm in c.FaceNormals) o.AddRange(fnm);
            while (o.Count % 4 != 0) o.Add(0);
            U32(0x17000000); // MSCNT
            while (o.Count % 16 != 0) o.Add(0);
            return o.ToArray();
        }

        /// <summary>A colour block for <paramref name="rgba"/> (4 bytes per vertex), with the template's VIF codes.</summary>
        public static Block ColourBlock(byte[] rgba, Block? template)
        {
            int nv = rgba.Length / 4;
            var data = new byte[(rgba.Length + 15) / 16 * 16];
            rgba.CopyTo(data, 0);
            uint u0 = template?.Upper0 ?? 0x01000103;
            uint u1 = template != null ? (template.Upper1 & 0xFF00FFFF) | (uint)nv << 16 : 0x6E00C006u | (uint)nv << 16;
            return new Block(data, template?.TagHigh ?? 0x30040000, u0, u1);
        }

        /// <summary>Recomputes a face normal (int16, unit length * 32766) from three vertex records.</summary>
        public static byte[] FaceNormal(byte[] a, byte[] b, byte[] c)
        {
            float[] P(byte[] r) => new float[] { BitConverter.ToInt16(r, 0), BitConverter.ToInt16(r, 2), BitConverter.ToInt16(r, 4) };
            float[] p0 = P(a), p1 = P(b), p2 = P(c);
            float[] e1 = { p1[0] - p0[0], p1[1] - p0[1], p1[2] - p0[2] }, e2 = { p2[0] - p0[0], p2[1] - p0[1], p2[2] - p0[2] };
            float[] n = { e1[1] * e2[2] - e1[2] * e2[1], e1[2] * e2[0] - e1[0] * e2[2], e1[0] * e2[1] - e1[1] * e2[0] };
            float len = MathF.Sqrt(n[0] * n[0] + n[1] * n[1] + n[2] * n[2]);
            if (len == 0) len = 1;
            var o = new byte[6];
            for (int k = 0; k < 3; k++) BitConverter.GetBytes((short)Math.Clamp((int)MathF.Round(n[k] / len * 32766), -32767, 32767)).CopyTo(o, k * 2);
            return o;
        }
    }
}
