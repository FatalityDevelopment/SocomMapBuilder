using System.Buffers.Binary;

namespace CodeDesigner.Ps2.FileFormats.Zipper.Ca
{
    /// <summary>
    /// Writes a SOCOM 3 / Combined Assault asset library (the serialized object graph <see cref="CaLibrary"/> reads):
    /// a version-2 header whose vectors {begin, end, capacity, allocator} at +0x28 list the models, objects,
    /// textures and palettes, with every pointer an offset into the library. Records follow the retail layouts:
    /// <list type="bullet">
    /// <item>nodes 0xD0 (type 0x0083DC7C, model root 0x00839A2C): +0x04 name hash, +0x10 local 4x4, +0x50
    /// children, +0x98 mesh objects;</item>
    /// <item>skinned mesh objects (0x00841A6C): a 0x80 header with the data size at +0x1C, then per material
    /// section a 16-byte texture field (name hash) and vertex batches / draw blocks;</item>
    /// <item>static mesh objects (0x00841398): +0x10 centre, +0x30 DMA chains (count, then tags of kind 6 material
    /// A+D packet, 3 colours, 2 geometry), +0x40 texture records;</item>
    /// <item>texture records: +0x14 a GIF A+D packet with TEX0, +0x4C name hash, +0x54 pixels, +0x58 palette;
    /// palette records: +0x04 format (0x00020200 16-bit / 0x400 32-bit), +0x0C hash, +0x14 colours.</item>
    /// </list>
    /// </summary>
    public sealed class CaLibraryWriter
    {
        private readonly MemoryStream _data = new();
        private readonly List<int> _models = new(), _objects = new(), _textures = new(), _palettes = new();
        private const int HeaderSize = 0x70;

        public CaLibraryWriter() => _data.Write(new byte[HeaderSize]);

        public int Length => (int)_data.Length;

        /// <summary>Appends <paramref name="bytes"/> (16-aligned) and answers where they went.</summary>
        public int Add(byte[] bytes, int align = 16)
        {
            while (_data.Length % align != 0) _data.WriteByte(0);
            int at = (int)_data.Length;
            _data.Write(bytes);
            return at;
        }

        public int Reserve(int size, int align = 16) => Add(new byte[size], align);

        public void Put32(int at, uint value)
        {
            long keep = _data.Position;
            _data.Position = at;
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(b, value);
            _data.Write(b);
            _data.Position = keep;
        }

        public void PutFloats(int at, IReadOnlyList<float> values)
        {
            for (int i = 0; i < values.Count; i++) Put32(at + 4 * i, BitConverter.SingleToUInt32Bits(values[i]));
        }

        /// <summary>A vector {begin, end, capacity, 0} at <paramref name="at"/> over a new array of offsets.</summary>
        public void Vector(int at, IReadOnlyList<int> items)
        {
            int begin = items.Count == 0 ? Length : Reserve(4 * items.Count, 4);
            for (int i = 0; i < items.Count; i++) Put32(begin + 4 * i, (uint)items[i]);
            Put32(at, (uint)begin);
            Put32(at + 4, (uint)(begin + 4 * items.Count));
            Put32(at + 8, (uint)(begin + 4 * items.Count));
        }

        // ------------------------------------------------------------------ records

        public sealed class NodeSpec
        {
            public uint Hash { get; init; }
            public float[] Local { get; init; } = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
            public List<NodeSpec> Children { get; } = new();
            public List<int> Objects { get; } = new();
        }

        /// <summary>Writes a node tree (pre-order) and lists its root as a model; answers the root's offset.</summary>
        public int AddModel(NodeSpec root)
        {
            int at = WriteNode(root, true);
            _models.Add(at);
            return at;
        }

        private int WriteNode(NodeSpec n, bool root)
        {
            int at = Reserve(0xD0);
            Put32(at, root ? CaLibrary.ModelRootType : CaLibrary.NodeType);
            Put32(at + 4, n.Hash);
            PutFloats(at + 0x10, n.Local);
            Put32(at + 0x7C, 0xFFFFFFFF);
            Put32(at + 0x84, 0xBEEF);
            Put32(at + 0xAC, 0x3F800000);
            var kids = n.Children.Select(c => WriteNode(c, false)).ToList();
            Vector(at + 0x50, kids);
            Vector(at + 0x98, n.Objects);
            return at;
        }

        /// <summary>A skinned mesh object over <paramref name="data"/> (texture fields + VIF batches).</summary>
        public int AddSkinnedObject(byte[] data)
        {
            int at = Reserve(0x80);
            Add(data);
            Put32(at, 0x00841A6C);
            Put32(at + 4, 0x0009C2FA);
            Put32(at + 8, 1);
            Put32(at + 0x1C, (uint)data.Length);
            _objects.Add(at);
            return at;
        }

        public sealed record ChainTag(int Kind, byte[] Data, uint Vif0 = 0, uint Vif1 = 0);

        /// <summary>A static mesh object: chains of (kind, block) tags and the texture records they draw with.</summary>
        public int AddStaticObject(float[] centre, IReadOnlyList<IReadOnlyList<ChainTag>> chains, IReadOnlyList<int> textures)
        {
            int at = Reserve(0x60);
            Put32(at, CaLibrary.StaticObjectType);
            Put32(at + 4, 0x0009C2FA);
            PutFloats(at + 0x10, centre);
            var chainAt = new List<int>();
            foreach (var chain in chains)
            {
                var blocks = chain.Select(t => Add(t.Data)).ToList();
                int c = Reserve(16 * (chain.Count + 1));
                Put32(c, (uint)chain.Count);
                for (int i = 0; i < chain.Count; i++)
                {
                    var t = chain[i];
                    int o = c + 16 * (i + 1);
                    Put32(o, 0x30000000u | (uint)t.Kind << 16 | (uint)(t.Data.Length / 16));
                    Put32(o + 4, (uint)blocks[i]);
                    Put32(o + 8, t.Vif0);
                    Put32(o + 12, t.Vif1);
                }
                chainAt.Add(c);
            }
            Vector(at + 0x30, chainAt);
            Vector(at + 0x40, textures);
            _objects.Add(at);
            return at;
        }

        /// <summary>A GS A+D packet setting TEX0 (a material tag's block, and a texture record's upload packet).</summary>
        public static byte[] Tex0Packet(ulong tex0)
        {
            var b = new byte[32];
            BinaryPrimitives.WriteUInt64LittleEndian(b, 1UL | 0x8000UL | (1UL << 60));   // NLOOP 1, EOP, NREG 1
            BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(8), 0xE);                   // A+D
            BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(16), tex0);
            BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(24), 0x06);                 // TEX0_1
            return b;
        }

        public int AddPalette(uint hash, uint format, byte[] colours)
        {
            int at = Reserve(0x20);
            int col = Add(colours);
            Put32(at, (uint)at);
            Put32(at + 4, format);
            Put32(at + 0x0C, hash);
            Put32(at + 0x10, 1);
            Put32(at + 0x14, (uint)col);
            _palettes.Add(at);
            return at;
        }

        public int AddTexture(uint hash, ulong tex0, byte[] pixels, int palette)
        {
            int at = Reserve(0x70);
            int packet = Add(Tex0Packet(tex0));
            int pix = Add(pixels);
            Put32(at + 0x14, (uint)packet);
            Put32(at + 0x4C, hash);
            Put32(at + 0x54, (uint)pix);
            Put32(at + 0x58, (uint)palette);
            Put32(at + 0x5C, (uint)((tex0 >> 20) & 0x3F));
            _textures.Add(at);
            return at;
        }

        /// <summary>The finished library (header version 2, total size, the four vectors).</summary>
        public byte[] ToBytes(uint nameHash)
        {
            Vector(0x28, _models);
            Vector(0x38, _objects);
            Vector(0x48, _textures);
            Vector(0x58, _palettes);
            while (_data.Length % 16 != 0) _data.WriteByte(0);
            Put32(0, 2);
            Put32(4, (uint)_data.Length);
            Put32(8, 0x10);
            Put32(0x10, nameHash);
            Put32(0x14, 2);
            Put32(0x18, 3);
            return _data.ToArray();
        }
    }
}
