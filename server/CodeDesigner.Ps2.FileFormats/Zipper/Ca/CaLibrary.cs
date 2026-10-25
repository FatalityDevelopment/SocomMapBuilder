using CodeDesigner.Ps2.FileFormats.Assets;
using CodeDesigner.Ps2.FileFormats.Zipper.Geo;
using CodeDesigner.Ps2.FileFormats.Zipper.Model;
using CodeDesigner.Ps2.FileFormats.Zipper.Package;

namespace CodeDesigner.Ps2.FileFormats.Zipper.Ca
{
    /// <summary>
    /// A SOCOM 3 / Combined Assault asset library: the serialized C++ object graph of an <c>ASST</c> resource (clib
    /// character libraries, vehicle / prop / sky <c>.rdr</c> libraries) or an <c>ISLE</c> resource (level data). The
    /// game loads it verbatim and turns stored offsets into pointers, so every pointer here is an offset into
    /// <see cref="Data"/>. Header vectors {begin, end, capacity, allocator} — models, objects, textures, palettes —
    /// start at +0x24 (version 1, ISLE), +0x28 (version 2, character libraries) or +0x2C (version 3; its word 3 points
    /// at a second library, e.g. every weapon in global_assets). Port of <c>Mods/SOCOMCA/ca_archive.py</c> /
    /// <c>castatic.py</c> / <c>rip_textures.py</c>.
    /// </summary>
    public sealed class CaLibrary
    {
        public const uint ModelRootType = 0x00839A2C, NodeType = 0x0083DC7C, StaticObjectType = 0x00841398;

        public byte[] Data { get; }
        private readonly int _base;

        public CaLibrary(byte[] data, int? vectorsAt = null)
        {
            Data = data;
            _base = vectorsAt ?? 0x28 + 4 * ((int)U32(0) - 2);
        }

        public uint Version => U32(0);

        public uint U32(int o) => o >= 0 && o + 4 <= Data.Length ? BitConverter.ToUInt32(Data, o) : 0;

        /// <summary>The offsets a {begin, end, ...} vector at <paramref name="o"/> holds.</summary>
        public List<int> Vector(int o)
        {
            var list = new List<int>();
            int begin = (int)U32(o), end = (int)U32(o + 4);
            if (begin <= 0 || end < begin || end > Data.Length || (end - begin) / 4 > 1_000_000) return list;
            for (int p = begin; p < end; p += 4) list.Add((int)U32(p));
            return list;
        }

        public List<int> Models => Vector(_base);
        public List<int> Objects => Vector(_base + 0x10);
        public List<int> Textures => Vector(_base + 0x20);
        public List<int> Palettes => Vector(_base + 0x30);

        /// <summary>The second library of a version-3 header (word 3), or null.</summary>
        public CaLibrary? Nested()
        {
            if (Version != 3) return null;
            int o = (int)U32(0x0C);
            return o > 0 && o < Data.Length - 0x58 ? new CaLibrary(Data, o + 0x18) : null;
        }

        // ------------------------------------------------------------------ node trees

        /// <summary>A model node: +0x04 name hash, +0x10 local 4x4 (row-major, translation in row 3 — the same float
        /// order as <see cref="Matrix4"/>'s column-major), +0x50 children, +0x98 mesh objects.</summary>
        public sealed record Node(int Offset, uint Hash, int Parent, float[] Local, float[] World, List<int> Objects);

        public uint ModelHash(int model) => U32(model + 4);

        /// <summary>A model's nodes in pre-order (the root first) with world transforms.</summary>
        public List<Node> Nodes(int model)
        {
            var list = new List<Node>();
            var seen = new HashSet<int>();
            void Walk(int n, int parent, float[] parentWorld)
            {
                if (!seen.Add(n) || n <= 0 || n + 0xA0 > Data.Length) return;
                var local = new float[16];
                for (int i = 0; i < 16; i++) local[i] = BitConverter.ToSingle(Data, n + 0x10 + 4 * i);
                if (!local.All(float.IsFinite)) local = Matrix4.Identity();
                float[] world = Matrix4.Multiply(parentWorld, local);
                int index = list.Count;
                list.Add(new Node(n, U32(n + 4), parent, local, world, Vector(n + 0x98)));
                foreach (int c in Vector(n + 0x50)) Walk(c, index, world);
            }
            Walk(model, -1, Matrix4.Identity());
            return list;
        }

        // ------------------------------------------------------------------ static meshes

        /// <summary>A geometry chunk and its vertex colours; <paramref name="ColorVif"/> is the colour tag's VIF words
        /// (STCYCL + UNPACK V4-8), copied as they are by the SOCOM II converter.</summary>
        public sealed record Part(int? ColorOffset, int ColorLength, int GeometryOffset, int GeometryLength, (uint, uint) ColorVif = default);

        public sealed record Section(int? TextureRecord, int MaterialOffset, int MaterialLength, List<Part> Parts);

        /// <summary>
        /// A static mesh object (type 0x00841398): +0x30 its DMA chains, +0x40 its texture records. A chain is a count
        /// tag then tags of kind 6 (material: a GS A+D packet that starts a section), 3 (vertex colours, RGBA bytes) and
        /// 2 (a geometry chunk — SOCOM II's static chunk format). A material's texture is the record whose TEX0 it
        /// sets (terrain objects bind one texture from 5 materials); without a TEX0 match, the next texture in order
        /// (the Humvee body: 10 materials, 10 textures).
        /// </summary>
        public List<Section> Sections(int obj)
        {
            var sections = new List<Section>();
            if (U32(obj) != StaticObjectType) return sections;
            var textures = Vector(obj + 0x40);
            int next = 0;
            foreach (int chain in Vector(obj + 0x30))
            {
                int count = (int)(U32(chain) & 0xFFFF);
                (int Offset, int Length, uint V0, uint V1)? colour = null;
                for (int i = 1; i <= count; i++)
                {
                    int o = chain + 16 * i;
                    if (o + 16 > Data.Length) break;
                    uint lo = U32(o);
                    int addr = (int)U32(o + 4);
                    int kind = (int)((lo >> 16) & 0xFF), length = (int)(lo & 0xFFFF) * 16;
                    if (addr < 0 || addr + length > Data.Length) continue;
                    if (kind == 6)
                    {
                        int? texture = null;
                        if (MaterialTex0(addr, length) is ulong m)
                            foreach (int t in textures)
                                if (Tex0(t) is ulong tt && (tt & Tex0Identity) == (m & Tex0Identity)) { texture = t; break; }
                        texture ??= next < textures.Count ? textures[next] : sections.LastOrDefault(x => x.TextureRecord != null)?.TextureRecord;
                        sections.Add(new Section(texture, addr, length, new List<Part>()));
                        next++;
                    }
                    else if (kind == 3) colour = (addr, length, U32(o + 8), U32(o + 12));
                    else if (kind == 2)
                    {
                        if (sections.Count == 0) sections.Add(new Section(null, 0, 0, new List<Part>()));
                        sections[^1].Parts.Add(new Part(colour?.Offset, colour?.Length ?? 0, addr, length, colour is { } cv ? (cv.V0, cv.V1) : default));
                        colour = null;
                    }
                }
            }
            return sections;
        }

        /// <summary>
        /// The geometry chunks of a static mesh object as mesh chunks placed by <paramref name="world"/> (CA static
        /// positions are 1/16 units, like SOCOM II vehicles and maps). Textures are named <c>ca_&lt;hash&gt;</c>.
        /// </summary>
        /// <remarks>Positions are 1/16 units in level and prop libraries; weapons (global_assets' nested library) use
        /// the chunk's scale (<see cref="ZedModelFile.PositionUnit.Scale"/>: an AK is 8.6 units next to a 19-unit
        /// soldier). CA stores V top-down (a row of the texture), so it is flipped to the up-pointing V the viewers
        /// and SOCOM II use.</remarks>
        public List<ModelMesh.MeshChunk> Chunks(int obj, float[] world, string visual, int firstIndex,
            ZedModelFile.PositionUnit unit = ZedModelFile.PositionUnit.Sixteenth)
        {
            var list = new List<ModelMesh.MeshChunk>();
            foreach (var s in Sections(obj))
            {
                string? texture = s.TextureRecord is int t ? TextureName(U32(t + 0x4C)) : null;
                foreach (var part in s.Parts)
                {
                    // A geometry blob holds one chunk; find its header (STCYCL 0x101, UNPACK V4-32 @0 n4).
                    for (int o = part.GeometryOffset; o + ZedModelFile.ChunkHeaderSize <= part.GeometryOffset + part.GeometryLength; o += 16)
                    {
                        if (U32(o) != 0x01000101 || U32(o + 4) != 0x6C048000) continue;
                        var r = new ZedModelFile.ChainChunkRef
                        {
                            GeometryOffset = o, GeometryLength = part.GeometryOffset + part.GeometryLength - o,
                            ColorOffset = part.ColorOffset, ColorLength = part.ColorLength,
                        };
                        var chunk = ZipperModelMesh.DecodeChunk(Data, r, firstIndex + list.Count, visual, texture, null, unit, world);
                        if (chunk != null)
                        {
                            for (int i = 1; i < chunk.Uvs.Length; i += 2) chunk.Uvs[i] = 1f - chunk.Uvs[i];
                            list.Add(chunk);
                        }
                        break;
                    }
                }
            }
            return list;
        }

        public static string TextureName(uint hash) => $"ca_{hash:x8}";

        // TEX0 fields that identify a texture: TBP0 (0-13), TBW (14-19), PSM (20-25), TW / TH (26-33), CBP (37-50), CPSM (51-54).
        private const ulong Tex0Identity = 0x3FFFUL | (0x3FUL << 14) | (0x3FUL << 20) | (0xFFUL << 26) | (0x3FFFUL << 37) | (0xFUL << 51);

        /// <summary>TEX0 of the first A+D GIF packet in [o, o + length), or null.</summary>
        private ulong? MaterialTex0(int o, int length)
        {
            for (int p = o; p + 16 <= o + length; p += 16)
            {
                ulong lo = BitConverter.ToUInt64(Data, p), hi = BitConverter.ToUInt64(Data, p + 8);
                int nloop = (int)(lo & 0x7FFF);
                if (hi != 0xE || ((lo >> 60) & 0xF) != 1 || nloop is 0 or > 32) continue;
                for (int i = 0; i < nloop && p + 32 + 16 * i <= Data.Length; i++)
                    if (BitConverter.ToUInt64(Data, p + 24 + 16 * i) == 0x06) return BitConverter.ToUInt64(Data, p + 16 + 16 * i);
                return null;
            }
            return null;
        }

        /// <summary>TEX0 of a texture record (from its GIF A+D packet at +0x14), or null.</summary>
        public ulong? Tex0(int rec)
        {
            int packet = (int)U32(rec + 0x14);
            if (packet <= 0 || packet + 16 > Data.Length) return null;
            int nloop = (int)(U32(packet) & 0x7FFF);
            for (int i = 0; i < nloop && packet + 0x20 + 0x10 * i <= Data.Length; i++)
                if (BitConverter.ToUInt64(Data, packet + 0x18 + 0x10 * i) == 0x06) return BitConverter.ToUInt64(Data, packet + 0x10 + 0x10 * i);
            return null;
        }

        // ------------------------------------------------------------------ textures

        public sealed record Texture(uint Hash, int Width, int Height, int Psm, byte[] Rgba, bool HasAlpha, uint PaletteHash);

        /// <summary>
        /// A texture record: +0x14 its GIF A+D packet (TEX0 gives size and PSM), +0x4C name hash, +0x54 pixels (linear),
        /// +0x58 palette record (+0x04 0x00020200 = 256 16-bit colours, 0x00000400 = 256 32-bit; +0x0C hash, +0x14
        /// colours, CSM1 order). The palette's format is TEX0.CPSM. Returns RGBA (alpha 0-255; PS2 0x80 = opaque).
        /// </summary>
        /// <summary>A texture record as stored: TEX0, size, pixel bytes (null when they live in another library) and
        /// its palette record (0 for none).</summary>
        public sealed record RawTexture(uint Hash, ulong Tex0, int Width, int Height, int Psm, byte[]? Pixels, int PaletteRecord);

        public RawTexture? Raw(int rec)
        {
            if (Tex0(rec) is not ulong t0) return null;
            int psm = (int)((t0 >> 20) & 0x3F), w = 1 << (int)((t0 >> 26) & 0xF), h = 1 << (int)((t0 >> 30) & 0xF);
            int bpp = psm switch { 0x13 => 8, 0x14 => 4, 0x02 => 16, 0x00 => 32, 0x01 => 24, _ => 8 };
            int pix = (int)U32(rec + 0x54), size = w * h * bpp / 8;
            byte[]? pixels = pix > 0 && pix + size <= Data.Length ? Data[pix..(pix + size)] : null;
            return new RawTexture(U32(rec + 0x4C), t0, w, h, psm, pixels, (int)U32(rec + 0x58));
        }

        /// <summary>A palette record's colours as stored (CSM1 order) and its format word (0x00020200 16-bit, 0x400
        /// 32-bit; high flag bits removed).</summary>
        public (byte[] Colours, uint Format)? RawPalette(int rec)
        {
            if (rec <= 0 || rec + 0x20 > Data.Length) return null;
            uint format = U32(rec + 4) & 0x000FFFFF;
            int size = (format & 0x400) != 0 ? 1024 : 512, col = (int)U32(rec + 0x14);
            return col > 0 && col + size <= Data.Length ? (Data[col..(col + size)], format) : null;
        }

        public Texture? DecodeTexture(int rec)
        {
            int packet = (int)U32(rec + 0x14);
            if (packet <= 0 || packet + 16 > Data.Length) return null;
            int nloop = (int)(U32(packet) & 0x7FFF);
            ulong? tex0 = null;
            for (int i = 0; i < nloop && packet + 0x20 + 0x10 * i <= Data.Length; i++)
            {
                ulong value = BitConverter.ToUInt64(Data, packet + 0x10 + 0x10 * i);
                ulong reg = BitConverter.ToUInt64(Data, packet + 0x18 + 0x10 * i);
                if (reg == 0x06) tex0 = value;
            }
            if (tex0 is not ulong t0) return null;
            int psm = (int)((t0 >> 20) & 0x3F);
            int w = 1 << (int)((t0 >> 26) & 0xF), h = 1 << (int)((t0 >> 30) & 0xF);
            int pix = (int)U32(rec + 0x54);
            // A null pixel pointer: the record only names the texture; its pixels live in another library (an island's
            // shared textures are in its dependency, e.g. FARM_I1 -> POLAND_RURALarm_c1.rdr).
            if (pix <= 0) return null;
            int n = w * h;
            var rgba = new byte[n * 4];
            uint palHash = 0;
            bool alpha = false;

            uint[]? Palette(int count)
            {
                int pr = (int)U32(rec + 0x58);
                if (pr <= 0 || pr + 0x20 > Data.Length) return null;
                palHash = U32(pr + 0x0C);
                // The palette format is TEX0.CPSM (0 = PSMCT32, 2 = PSMCT16). The record's +0x04 word says the same
                // in its low bits (0x400 / 0x20200) but carries flags in the high byte (0x10000400, 0x18020200 ...).
                bool wide = ((t0 >> 51) & 0xF) == 0;
                int col = (int)U32(pr + 0x14);
                var colours = new uint[count];
                for (int i = 0; i < count; i++)
                {
                    // Undo CSM1: entries 8-15 and 16-23 of every 32 are swapped, so a 16-colour palette sits at 0-7, 16-23.
                    int src = count == 256 ? (i & ~0x18) | ((i & 0x08) << 1) | ((i & 0x10) >> 1) : i < 8 ? i : i + 8;
                    colours[i] = wide ? Rgba32(U32(col + 4 * src)) : Rgba16((ushort)(U32(col + 2 * src) & 0xFFFF));
                }
                return colours;
            }

            void Put(int i, uint c)
            {
                rgba[i * 4] = (byte)c; rgba[i * 4 + 1] = (byte)(c >> 8); rgba[i * 4 + 2] = (byte)(c >> 16); rgba[i * 4 + 3] = (byte)(c >> 24);
                if ((c >> 24) < 0xFF) alpha = true;
            }

            switch (psm)
            {
                case 0x13: // PSMT8
                {
                    var lut = Palette(256);
                    if (lut == null || pix + n > Data.Length) return null;
                    for (int i = 0; i < n; i++) Put(i, lut[Data[pix + i]]);
                    break;
                }
                case 0x14: // PSMT4
                {
                    var lut = Palette(16);
                    if (lut == null || pix + n / 2 > Data.Length) return null;
                    for (int i = 0; i < n; i++) { int b = Data[pix + i / 2]; Put(i, lut[(i & 1) == 0 ? b & 15 : b >> 4]); }
                    break;
                }
                case 0x02: // PSMCT16
                    if (pix + n * 2 > Data.Length) return null;
                    for (int i = 0; i < n; i++) Put(i, Rgba16(BitConverter.ToUInt16(Data, pix + 2 * i)));
                    break;
                case 0x00: // PSMCT32
                    if (pix + n * 4 > Data.Length) return null;
                    for (int i = 0; i < n; i++) Put(i, Rgba32(U32(pix + 4 * i)));
                    break;
                default:
                    return null;
            }
            return new Texture(U32(rec + 0x4C), w, h, psm, rgba, alpha, palHash);
        }

        // PSMCT16 A1 B5 G5 R5; a clear A bit is opaque unless the texture uses it (TEXA), so treat both as opaque here.
        private static uint Rgba16(ushort c) =>
            (uint)((c & 31) * 255 / 31) | (uint)((((c >> 5) & 31) * 255 / 31) << 8) | (uint)((((c >> 10) & 31) * 255 / 31) << 16) | 0xFF000000u;

        // PSMCT32 with PS2 alpha (0x80 = 1.0).
        private static uint Rgba32(uint c) => (c & 0x00FFFFFF) | ((uint)Math.Min(255, (int)(c >> 24) * 2) << 24);
    }
}
