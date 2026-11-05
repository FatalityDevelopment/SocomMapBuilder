using CodeDesigner.Ps2.FileFormats.Assets;

namespace CodeDesigner.Ps2.FileFormats.Zipper.Ca
{
    /// <summary>
    /// SOCOM: Fireteam Bravo 2 (PSP) libraries. They share CA's library header, model / node trees and texture and object
    /// vectors (<see cref="CaLibrary"/>), but the records the PS2's VIF / GS read are replaced by the PSP's graphics
    /// engine (GE): static objects hold GE display lists over vertex buffers, and texture records hold swizzled GE
    /// texels with their CLUT. Layouts observed on the retail disc (UCUS-98645); see docs/formats/psp.md.
    /// </summary>
    public static class PspGe
    {
        /// <summary>A static mesh object (CA's type 0x00841398).</summary>
        public const uint StaticObjectType = 0x00945EA4;

        /// <summary>A skinned mesh object (characters, weapons, the world's animated pieces): the same batches, with
        /// vertex weights and each batch's bone palette. Read here in its bind pose.</summary>
        public const uint SkinnedObjectType = 0x0090C640;

        /// <summary>A mesh object of either kind.</summary>
        public static bool IsMeshObject(uint type) => type is StaticObjectType or SkinnedObjectType;

        // ------------------------------------------------------------------ textures

        /// <summary>
        /// A texture record: +0x08 name hash, +0x0C pixels (from the record, 0x30), +0x10 GE format (2 RGBA4444,
        /// 3 RGBA8888, 4 CLUT4, 5 CLUT8, 8 DXT1), +0x14 width (low half) and height, +0x18 buffer width in pixels (low
        /// half), +0x1C CLUT (from the record), +0x24 low byte the CLUT entry size (0x20: 32-bit RGBA8888, else 16-bit
        /// RGB565). Pixels are swizzled in 16-byte × 8-row blocks.
        /// </summary>
        public static CaLibrary.Texture? DecodeTexture(CaLibrary lib, int rec)
        {
            var d = lib.Data;
            if (rec <= 0 || rec + 0x30 > d.Length) return null;
            int fmt = (int)lib.U32(rec + 0x10);
            int w = (int)(lib.U32(rec + 0x14) & 0xFFFF), h = (int)(lib.U32(rec + 0x14) >> 16);
            int bufw = (int)(lib.U32(rec + 0x18) & 0xFFFF);
            int pix = rec + (int)lib.U32(rec + 0x0C), clut = rec + (int)lib.U32(rec + 0x1C);
            if (w is <= 0 or > 1024 || h is <= 0 or > 1024) return null;
            int bpp = fmt switch { 0 or 1 or 2 => 16, 3 => 32, 4 => 4, 5 => 8, _ => 0 };
            if (bpp == 0) return null;
            bufw = Math.Max(bufw, w);
            int stride = Math.Max(16, bufw * bpp / 8);
            int rows = (h + 7) & ~7;
            if (pix < 0 || pix + stride * rows > d.Length)
            {
                // The last block row may be short when the height is not a multiple of 8.
                if (pix < 0 || pix + stride * h > d.Length) return null;
            }
            var lin = Unswizzle(d, pix, stride, h);
            bool wide = (lib.U32(rec + 0x24) & 0xFF) == 0x20;
            int entries = fmt == 4 ? 16 : 256;
            uint[]? lut = null;
            if (fmt is 4 or 5)
            {
                lut = new uint[entries];
                for (int i = 0; i < entries; i++)
                {
                    int o = clut + i * (wide ? 4 : 2);
                    if (o < 0 || o + (wide ? 4 : 2) > d.Length) break;
                    lut[i] = wide ? lib.U32(o) : Rgb565(BitConverter.ToUInt16(d, o));
                }
            }
            var rgba = new byte[w * h * 4];
            bool alpha = false;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int row = y * stride;
                    uint c = fmt switch
                    {
                        5 => lut![lin[row + x]],
                        4 => lut![(lin[row + x / 2] >> ((x & 1) * 4)) & 15],
                        3 => BitConverter.ToUInt32(lin, row + x * 4),
                        2 => Rgba4444(BitConverter.ToUInt16(lin, row + x * 2)),
                        1 => Rgba5551(BitConverter.ToUInt16(lin, row + x * 2)),
                        _ => Rgb565(BitConverter.ToUInt16(lin, row + x * 2)),
                    };
                    int i = (y * w + x) * 4;
                    rgba[i] = (byte)c; rgba[i + 1] = (byte)(c >> 8); rgba[i + 2] = (byte)(c >> 16); rgba[i + 3] = (byte)(c >> 24);
                    if ((c >> 24) < 0xFF) alpha = true;
                }
            return new CaLibrary.Texture(lib.U32(rec + 0x08), w, h, fmt, rgba, alpha, 0);
        }

        /// <summary>Texels in row order from the GE's swizzle: blocks of 16 bytes × 8 rows, left to right, then down.</summary>
        public static byte[] Unswizzle(byte[] d, int src, int stride, int height)
        {
            var lin = new byte[stride * height];
            int blocks = stride / 16;
            for (int by = 0; by < (height + 7) / 8; by++)
                for (int bx = 0; bx < blocks; bx++)
                    for (int r = 0; r < 8; r++, src += 16)
                    {
                        int y = by * 8 + r;
                        if (y < height && src + 16 <= d.Length) Buffer.BlockCopy(d, src, lin, y * stride + bx * 16, 16);
                    }
            return lin;
        }

        private static uint Rgb565(ushort c) =>
            (uint)((c & 31) * 255 / 31) | (uint)((((c >> 5) & 63) * 255 / 63) << 8) | (uint)((((c >> 11) & 31) * 255 / 31) << 16) | 0xFF000000u;

        private static uint Rgba5551(ushort c) =>
            (uint)((c & 31) * 255 / 31) | (uint)((((c >> 5) & 31) * 255 / 31) << 8) | (uint)((((c >> 10) & 31) * 255 / 31) << 16) | ((c >> 15) != 0 ? 0xFF000000u : 0);

        private static uint Rgba4444(ushort c) =>
            (uint)((c & 15) * 17) | (uint)(((c >> 4) & 15) * 17 << 8) | (uint)(((c >> 8) & 15) * 17 << 16) | (uint)(((c >> 12) & 15) * 17 << 24);

        // ------------------------------------------------------------------ static meshes

        /// <summary>
        /// A static object's batches. +0x04 its bounding box (8 corners), +0x70 the batch count; batch i is 0x140 bytes at +0x80 + 0x140·i: +0x00 the
        /// material's GE commands (texture state, patched by the game), +0x74 the texture's name hash, +0x80 the
        /// batch's bounding box (8 corners), +0xE0 its vertices and +0xE8 its display list (both from the batch start),
        /// +0xEC the primitive count, +0xF4 vertex count (high half) and stride (bits 8-15), +0xF8 vertex bytes.
        /// </summary>
        public sealed record Batch(int Offset, uint TextureHash, int Vertices, int DisplayList, int Count, int Stride);

        public const int BatchSize = 0x140, FirstBatch = 0x80;

        public static List<Batch> Batches(CaLibrary lib, int obj)
        {
            var list = new List<Batch>();
            if (!IsMeshObject(lib.U32(obj))) return list;
            // Two counts: the low half, then the high half (batches drawn after the others, e.g. blended ones).
            uint counts = lib.U32(obj + 0x70);
            int count = (int)(counts & 0xFFFF) + (int)(counts >> 16);
            if (count is <= 0 or > 4096) return list;
            for (int i = 0; i < count; i++)
            {
                int b = obj + FirstBatch + BatchSize * i;
                if (b + BatchSize > lib.Data.Length) break;
                uint shape = lib.U32(b + 0xF4);
                list.Add(new Batch(b, lib.U32(b + 0x74), b + (int)lib.U32(b + 0xE0), b + (int)lib.U32(b + 0xE8),
                    (int)(shape >> 16), (int)((shape >> 8) & 0xFF)));
            }
            return list;
        }

        /// <summary>The batches of a static object as mesh chunks placed by <paramref name="world"/>. Textures are named
        /// like CA's (<see cref="CaLibrary.TextureName"/>); V is flipped to point up, as for CA.</summary>
        public static List<ModelMesh.MeshChunk> Chunks(CaLibrary lib, int obj, float[] world, string visual, int firstIndex)
        {
            var list = new List<ModelMesh.MeshChunk>();
            foreach (var batch in Batches(lib, obj))
            {
                var chunk = DecodeBatch(lib.Data, batch, firstIndex + list.Count, visual, world);
                if (chunk != null) list.Add(chunk);
            }
            return list;
        }

        // GE commands this reader follows.
        private const int CmdVaddr = 0x01, CmdIaddr = 0x02, CmdPrim = 0x04, CmdRet = 0x0B, CmdEnd = 0x0C, CmdBase = 0x10, CmdVtype = 0x12;

        /// <summary>
        /// One batch: its display list sets VTYPE, then draws PRIMs over consecutive vertices (VADDR is 0 in the file
        /// and offsets from the batch's vertices). Triangles, strips and fans are read; other primitives are skipped.
        /// </summary>
        public static ModelMesh.MeshChunk? DecodeBatch(byte[] d, Batch batch, int index, string? visual, float[] world)
        {
            if (batch.Vertices <= 0 || batch.DisplayList <= 0 || batch.DisplayList + 4 > d.Length) return null;
            uint vtype = 0;
            int vaddr = batch.Vertices, iaddr = -1;
            var pos = new List<float>(); var uv = new List<float>(); var nrm = new List<float>(); var col = new List<byte>();
            var tris = new List<int>();
            bool hasColour = false;
            int words = 0;
            for (int p = batch.DisplayList; p + 4 <= d.Length && words++ < 65536; p += 4)
            {
                uint w = BitConverter.ToUInt32(d, p);
                int cmd = (int)(w >> 24);
                uint arg = w & 0xFFFFFF;
                if (cmd is CmdRet or CmdEnd) break;
                switch (cmd)
                {
                    case CmdVtype: vtype = arg; break;
                    case CmdVaddr: vaddr = batch.Vertices + (int)arg; break;
                    case CmdIaddr: iaddr = batch.Vertices + (int)arg; break;
                    case CmdBase: break;
                    case CmdPrim:
                    {
                        var fmt = new VertexFormat(vtype);
                        if (fmt.Stride == 0) return null;
                        int type = (int)((arg >> 16) & 7), n = (int)(arg & 0xFFFF);
                        // Vertex indices of this primitive (indexed or consecutive), then the vertices they read.
                        var ids = new int[n];
                        int idxSize = fmt.Index;
                        for (int i = 0; i < n; i++)
                            ids[i] = idxSize == 0 || iaddr < 0 ? i
                                : idxSize == 1 ? d[iaddr + i] : BitConverter.ToUInt16(d, iaddr + 2 * i);
                        int first = pos.Count / 3;
                        var map = new Dictionary<int, int>();
                        int Vertex(int id)
                        {
                            if (map.TryGetValue(id, out int v)) return v;
                            int o = vaddr + id * fmt.Stride;
                            if (o < 0 || o + fmt.Stride > d.Length) return -1;
                            fmt.Read(d, o, world, pos, uv, nrm, col);
                            return map[id] = pos.Count / 3 - 1;
                        }
                        var vs = ids.Select(Vertex).ToArray();
                        if (vs.Any(v => v < 0)) return null;
                        hasColour |= fmt.Colour != 0;
                        void Tri(int a, int b, int c) { if (a != b && b != c && a != c) { tris.Add(a); tris.Add(b); tris.Add(c); } }
                        if (type == 3) for (int i = 0; i + 2 < n; i += 3) Tri(vs[i], vs[i + 1], vs[i + 2]);
                        else if (type == 4) for (int i = 0; i + 2 < n; i++) { if ((i & 1) == 0) Tri(vs[i], vs[i + 1], vs[i + 2]); else Tri(vs[i + 1], vs[i], vs[i + 2]); }
                        else if (type == 5) for (int i = 1; i + 1 < n; i++) Tri(vs[0], vs[i], vs[i + 1]);
                        // VADDR / IADDR advance past what the primitive read.
                        if (idxSize == 0 || iaddr < 0) vaddr += n * fmt.Stride; else iaddr += n * idxSize;
                        _ = first;
                        break;
                    }
                }
            }
            int count = pos.Count / 3;
            if (count == 0 || tris.Count == 0) return null;
            var uvs = uv.ToArray();
            for (int i = 1; i < uvs.Length; i += 2) uvs[i] = 1f - uvs[i];
            return new ModelMesh.MeshChunk
            {
                Index = index, GeometryOffset = batch.Offset, Visual = visual, Texture = CaLibrary.TextureName(batch.TextureHash),
                Unit = "Float", Matrix = world, Scale = 1f, VertexCount = count,
                Positions = pos.ToArray(), Uvs = uvs, Normals = nrm.ToArray(), Colors = hasColour ? col.ToArray() : null,
                Triangles = tris.ToArray(),
            };
        }

        /// <summary>
        /// A GE vertex type (VTYPE): bits 0-1 texture coordinates (u8, u16, float), 2-4 colour (4 RGB565, 5 RGBA5551,
        /// 6 RGBA4444, 7 RGBA8888), 5-6 normal and 7-8 position (s8, s16, float), 9-10 weights, 11-12 index size,
        /// 14-16 weight count − 1. A vertex is weights, UV, colour, normal, position, each aligned to its own size.
        /// </summary>
        public readonly struct VertexFormat
        {
            public readonly int Uv, Colour, Normal, Position, Weight, Weights, Index;
            public readonly int Stride, UvAt, ColourAt, NormalAt, PositionAt;

            public VertexFormat(uint vtype)
            {
                Uv = (int)(vtype & 3); Colour = (int)((vtype >> 2) & 7); Normal = (int)((vtype >> 5) & 3);
                Position = (int)((vtype >> 7) & 3); Weight = (int)((vtype >> 9) & 3); Index = (int)((vtype >> 11) & 3);
                Weights = Weight == 0 ? 0 : (int)((vtype >> 14) & 7) + 1;
                if (Index == 3) Index = 4; // 32-bit indices are not used by the GE; keep the size consistent.
                int o = 0, align = 1;
                int Place(int size, int count) { o = (o + size - 1) / size * size; int at = o; o += size * count; align = Math.Max(align, size); return at; }
                int Size(int kind) => kind switch { 1 => 1, 2 => 2, 3 => 4, _ => 0 };
                if (Weight != 0) Place(Size(Weight), Weights);
                UvAt = Uv != 0 ? Place(Size(Uv), 2) : -1;
                ColourAt = Colour switch { >= 4 and <= 6 => Place(2, 1), 7 => Place(4, 1), _ => -1 };
                NormalAt = Normal != 0 ? Place(Size(Normal), 3) : -1;
                PositionAt = Position != 0 ? Place(Size(Position), 3) : -1;
                Stride = Position == 0 ? 0 : (o + align - 1) / align * align;
            }

            private static float Read(byte[] d, int o, int kind, bool signed) => kind switch
            {
                1 => signed ? (sbyte)d[o] / 127f : d[o] / 128f,
                2 => signed ? BitConverter.ToInt16(d, o) / 32767f : BitConverter.ToUInt16(d, o) / 32768f,
                _ => BitConverter.ToSingle(d, o),
            };

            /// <summary>Appends one vertex. Colours are stored the PS2 way (0x80 = 1.0) that the viewers expect.</summary>
            public void Read(byte[] d, int v, float[] world, List<float> pos, List<float> uv, List<float> nrm, List<byte> col)
            {
                int ps = Position switch { 1 => 1, 2 => 2, _ => 4 };
                float x = Read(d, v + PositionAt, Position, true), y = Read(d, v + PositionAt + ps, Position, true), z = Read(d, v + PositionAt + 2 * ps, Position, true);
                var (wx, wy, wz) = Matrix4.Apply(world, x, y, z);
                pos.Add(wx); pos.Add(wy); pos.Add(wz);
                if (UvAt >= 0)
                {
                    int us = Uv switch { 1 => 1, 2 => 2, _ => 4 };
                    uv.Add(Read(d, v + UvAt, Uv, false)); uv.Add(Read(d, v + UvAt + us, Uv, false));
                }
                else { uv.Add(0); uv.Add(0); }
                if (NormalAt >= 0)
                {
                    int ns = Normal switch { 1 => 1, 2 => 2, _ => 4 };
                    float nx = Read(d, v + NormalAt, Normal, true), ny = Read(d, v + NormalAt + ns, Normal, true), nz = Read(d, v + NormalAt + 2 * ns, Normal, true);
                    // Directions only: the world matrix without its translation.
                    float tx = world[0] * nx + world[4] * ny + world[8] * nz, ty = world[1] * nx + world[5] * ny + world[9] * nz, tz = world[2] * nx + world[6] * ny + world[10] * nz;
                    float len = MathF.Sqrt(tx * tx + ty * ty + tz * tz);
                    if (len > 1e-6f) { tx /= len; ty /= len; tz /= len; }
                    nrm.Add(tx); nrm.Add(ty); nrm.Add(tz);
                }
                else { nrm.Add(0); nrm.Add(1); nrm.Add(0); }
                uint c = ColourAt < 0 ? 0xFFFFFFFFu : Colour switch
                {
                    7 => BitConverter.ToUInt32(d, v + ColourAt),
                    6 => Rgba4444(BitConverter.ToUInt16(d, v + ColourAt)),
                    5 => Rgba5551(BitConverter.ToUInt16(d, v + ColourAt)),
                    _ => Rgb565(BitConverter.ToUInt16(d, v + ColourAt)),
                };
                // PSP 0xFF = 1.0 → PS2 0x80 = 1.0.
                col.Add((byte)(((c & 0xFF) + 1) / 2)); col.Add((byte)((((c >> 8) & 0xFF) + 1) / 2));
                col.Add((byte)((((c >> 16) & 0xFF) + 1) / 2)); col.Add((byte)(((c >> 24) + 1) / 2));
            }
        }

        // ------------------------------------------------------------------ instances

        /// <summary>
        /// An island's placed objects (CA's scatter instances): 0x60-byte records, +0x00 a 4×4 matrix (rows the axes,
        /// translation in row 3, w 1.0), +0x40 the static object, +0x44 a colour, +0x48 −1, +0x4C flags, +0x54 the
        /// object's +0x08 hash. Found by scanning for records that match.
        /// </summary>
        public const int InstanceSize = 0x60;

        public static List<CaLibrary.Instance> Instances(CaLibrary lib)
        {
            var objects = lib.Objects.Where(o => lib.U32(o) == StaticObjectType).ToHashSet();
            var list = new List<CaLibrary.Instance>();
            var d = lib.Data;
            for (int r = 0; r + InstanceSize <= d.Length; r += 16)
            {
                int obj = (int)lib.U32(r + 0x40);
                if (!objects.Contains(obj) || lib.U32(r + 0x3C) != 0x3F800000 || lib.U32(r + 0x48) != 0xFFFFFFFF
                    || lib.U32(r + 0x54) != lib.U32(obj + 0x08)
                    || lib.U32(r + 0x0C) != 0 || lib.U32(r + 0x1C) != 0 || lib.U32(r + 0x2C) != 0) continue;
                var m = new float[16];
                for (int i = 0; i < 16; i++) m[i] = BitConverter.ToSingle(d, r + 4 * i);
                if (!m.All(float.IsFinite)) continue;
                list.Add(new CaLibrary.Instance(r, 0, lib.U32(r + 0x4C), m, obj));
                r += InstanceSize - 16;
            }
            return list;
        }
    }
}
