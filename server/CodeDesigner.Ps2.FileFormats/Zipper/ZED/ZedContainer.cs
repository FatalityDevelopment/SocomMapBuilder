using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace CodeDesigner.Ps2.FileFormats.Zipper.ZED
{
    /// <summary>
    /// Represents one named record inside a ZED/ZAR container.
    /// </summary>
    public sealed class ZedNode
    {
        /// <summary>Name stored in the file's string table.</summary>
        public string Name { get; internal set; } = string.Empty;

        /// <summary>Raw payload bytes for this node.</summary>
        public byte[] Data { get; internal set; } = Array.Empty<byte>();

        /// <summary>Relative offset into the shared body region.</summary>
        public uint DataOffset { get; internal set; }

        /// <summary>Trailing per-node metadata word.</summary>
        public uint Type { get; set; }

        /// <summary>Payload size in bytes.</summary>
        public uint Size => (uint)Data.Length;
    }

    /// <summary>
    /// Shared reader/writer for the PS2 game's ZED/ZAR container family.
    ///
    /// Observations from the provided samples:
    /// - ZAR files can be true archives (for example READERM.ZAR)
    /// - ZED files use the same outer container, but usually store named scene/material blocks
    ///   instead of a simple list of child files
    ///
    /// The format stores:
    /// - a null-terminated string table
    /// - a 32-bit root/control word
    /// - a fixed-size record table
    /// - a shared body region with 0x10-aligned payload blocks
    /// </summary>
    public sealed class ZedContainer : IDisposable
    {
        private const int FixedHeaderSize = 0x64;
        private const int RecordSize = 0x10;
        private const uint DefaultHeaderWord10 = 0x10;
        private const uint DefaultHeaderWord60 = 0x00020002;
        public const uint DefaultDataAlignment = 0x10;

        private readonly List<ZedNode> _nodes = new List<ZedNode>();
        private readonly List<string> _stringOrder = new List<string>();
        private bool _isOpen;

        private ZedContainer() { }

        /// <summary>
        /// Root/control word stored immediately before the record table.
        /// </summary>
        public uint RootValue { get; set; }

        /// <summary>
        /// Base pointer used to turn string-table offsets into in-file name references.
        /// </summary>
        public uint StringBasePointer { get; set; }

        /// <summary>Header word at 0x10. Samples use 0x10.</summary>
        public uint HeaderWord10 { get; set; } = DefaultHeaderWord10;

        /// <summary>Header word at 0x58. Samples currently use 0.</summary>
        public uint HeaderWord58 { get; set; }

        /// <summary>Header word at 0x5C. This varies by file family.</summary>
        public uint HeaderWord5C { get; set; }

        /// <summary>Header word at 0x60. Samples use 0x00020002.</summary>
        public uint HeaderWord60 { get; set; } = DefaultHeaderWord60;

        /// <summary>Alignment used when repacking the shared body region.</summary>
        public uint DataAlignment { get; set; } = DefaultDataAlignment;

        /// <summary>All records stored in the container.</summary>
        public IReadOnlyList<ZedNode> Nodes => _nodes;

        /// <summary>Alias for archive-style callers.</summary>
        public IReadOnlyList<ZedNode> Entries => _nodes;

        /// <summary>Number of records currently loaded.</summary>
        public int NodeCount => _nodes.Count;

        /// <summary>Alias for archive-style callers.</summary>
        public int EntryCount => _nodes.Count;

        /// <summary>Opens a ZED/ZAR container from disk.</summary>
        public static ZedContainer Open(string filePath)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException("Container file not found.", filePath);

            var container = new ZedContainer();
            container.Load(File.ReadAllBytes(filePath));
            return container;
        }

        /// <summary>Opens a ZED/ZAR container from a byte array.</summary>
        public static ZedContainer FromBytes(byte[] data)
        {
            var container = new ZedContainer();
            container.Load(data);
            return container;
        }

        /// <summary>Creates a new, empty container.</summary>
        public static ZedContainer CreateNew(
            uint stringBasePointer = 0x10000000u,
            uint rootValue = 0,
            uint headerWord5C = 0x00010001u,
            uint dataAlignment = DefaultDataAlignment)
        {
            if (dataAlignment == 0)
                throw new ArgumentOutOfRangeException(nameof(dataAlignment), "Alignment must be greater than zero.");

            return new ZedContainer
            {
                _isOpen = true,
                StringBasePointer = stringBasePointer,
                RootValue = rootValue,
                HeaderWord10 = DefaultHeaderWord10,
                HeaderWord60 = DefaultHeaderWord60,
                HeaderWord5C = headerWord5C,
                DataAlignment = dataAlignment,
            };
        }

        /// <summary>Returns the node at <paramref name="index"/>.</summary>
        public ZedNode GetNode(int index) => _nodes[index];

        /// <summary>Alias for archive-style callers.</summary>
        public ZedNode GetEntry(int index) => GetNode(index);

        /// <summary>Finds the first node with the given name (case-insensitive).</summary>
        public ZedNode? FindNode(string name) =>
            _nodes.FirstOrDefault(n => string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase));

        /// <summary>Alias for archive-style callers.</summary>
        public ZedNode? FindEntry(string name) => FindNode(name);

        /// <summary>Finds every node with the given name (case-insensitive).</summary>
        public IReadOnlyList<ZedNode> FindNodes(string name)
        {
            EnsureOpen();
            return _nodes
                .Where(n => string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        /// <summary>Adds a new node to the container.</summary>
        public ZedNode AddNode(string name, byte[] data, uint type = 0)
        {
            EnsureOpen();
            string normalizedName = NormalizeName(name);
            RememberName(normalizedName);

            var node = new ZedNode
            {
                Name = normalizedName,
                Data = (byte[])data.Clone(),
                Type = type,
            };

            _nodes.Add(node);
            return node;
        }

        /// <summary>Alias for archive-style callers.</summary>
        public ZedNode AddEntry(string name, byte[] data, uint type = 0) => AddNode(name, data, type);

        /// <summary>
        /// Inserts a node at <paramref name="index"/> in the pre-order record list. The caller keeps the tree
        /// consistent (parent child counts); see <see cref="ZedTree"/>.
        /// </summary>
        public ZedNode InsertNode(int index, string name, byte[] data, uint type = 0)
        {
            EnsureOpen();
            string normalizedName = NormalizeName(name);
            RememberName(normalizedName);
            var node = new ZedNode { Name = normalizedName, Data = (byte[])data.Clone(), Type = type };
            _nodes.Insert(index, node);
            return node;
        }

        /// <summary>Renames an existing node.</summary>
        public void RenameNode(int index, string newName)
        {
            EnsureOpen();
            string normalizedName = NormalizeName(newName);
            _nodes[index].Name = normalizedName;
            RememberName(normalizedName);
        }

        /// <summary>Replaces the payload bytes of an existing node.</summary>
        public void ReplaceNodeData(int index, byte[] newData)
        {
            EnsureOpen();
            _nodes[index].Data = (byte[])newData.Clone();
        }

        /// <summary>Alias for archive-style callers.</summary>
        public void ReplaceEntryData(int index, byte[] newData) => ReplaceNodeData(index, newData);

        /// <summary>Removes a node by index.</summary>
        public void RemoveNode(int index)
        {
            EnsureOpen();
            _nodes.RemoveAt(index);
        }

        /// <summary>Alias for archive-style callers.</summary>
        public void RemoveEntry(int index) => RemoveNode(index);

        /// <summary>
        /// Removes every node whose name matches <paramref name="name"/> (case-insensitive).
        /// Returns the number of removed nodes.
        /// </summary>
        public int RemoveNode(string name)
        {
            EnsureOpen();
            return _nodes.RemoveAll(n => string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Alias for archive-style callers.</summary>
        public int RemoveEntry(string name) => RemoveNode(name);

        /// <summary>Writes one node's payload to disk.</summary>
        public void ExtractNode(int index, string outputPath)
        {
            EnsureOpen();
            File.WriteAllBytes(outputPath, _nodes[index].Data);
        }

        /// <summary>Alias for archive-style callers.</summary>
        public void ExtractEntry(int index, string outputPath) => ExtractNode(index, outputPath);

        /// <summary>Writes every node's payload to a directory using the node name as the file name.</summary>
        public void ExtractAll(string outputDirectory)
        {
            EnsureOpen();
            Directory.CreateDirectory(outputDirectory);

            for (int i = 0; i < _nodes.Count; i++)
            {
                ZedNode node = _nodes[i];
                string fileName = node.Name;
                string outputPath = Path.Combine(outputDirectory, fileName);

                if (File.Exists(outputPath))
                {
                    string stem = Path.GetFileNameWithoutExtension(fileName);
                    string ext = Path.GetExtension(fileName);
                    outputPath = Path.Combine(outputDirectory, $"{stem}_{i:X4}{ext}");
                }

                File.WriteAllBytes(outputPath, node.Data);
            }
        }

        /// <summary>Recalculates node offsets by packing payload blocks on the current alignment.</summary>
        public void RepackData()
        {
            EnsureOpen();

            uint nextOffset = 0;
            foreach (ZedNode node in _nodes)
            {
                if (node.Data.Length == 0)
                {
                    node.DataOffset = 0;
                    continue;
                }

                nextOffset = AlignUp(nextOffset, DataAlignment);
                node.DataOffset = nextOffset;
                nextOffset += (uint)node.Data.Length;
            }
        }

        /// <summary>Serialises the container to disk.</summary>
        public void Save(string filePath)
        {
            EnsureOpen();
            File.WriteAllBytes(filePath, ToBytes());
        }

        /// <summary>Serialises the container to a byte array.</summary>
        public byte[] ToBytes()
        {
            EnsureOpen();

            RepackData();

            byte[] stringTable = BuildStringTable(out Dictionary<string, uint> nameOffsets);
            int firstRecordOffset = checked(FixedHeaderSize + stringTable.Length + 0x10);
            int recordBytes = checked(_nodes.Count * RecordSize);
            int bodyStart = AlignUp(firstRecordOffset + recordBytes, (int)DataAlignment);

            uint maxEnd = 0;
            foreach (ZedNode node in _nodes)
            {
                if (node.Data.Length == 0) continue;
                uint end = node.DataOffset + (uint)node.Data.Length;
                if (end > maxEnd) maxEnd = end;
            }

            uint bodySize = AlignUp(maxEnd, DataAlignment);
            int totalSize = checked(bodyStart + (int)bodySize);
            var output = new byte[totalSize];

            for (int i = bodyStart; i < totalSize; i++)
                output[i] = 0xAF;

            Write32(output, 0x04, checked((uint)_nodes.Count + 1u));
            Write32(output, 0x08, (uint)stringTable.Length);
            Write32(output, 0x0C, StringBasePointer);
            Write32(output, 0x10, HeaderWord10 == 0 ? DefaultHeaderWord10 : HeaderWord10);
            Write32(output, 0x54, bodySize);
            Write32(output, 0x58, HeaderWord58);
            Write32(output, 0x5C, HeaderWord5C);
            Write32(output, 0x60, HeaderWord60 == 0 ? DefaultHeaderWord60 : HeaderWord60);

            Buffer.BlockCopy(stringTable, 0, output, FixedHeaderSize, stringTable.Length);
            Write32(output, firstRecordOffset - 4, RootValue);

            for (int i = 0; i < _nodes.Count; i++)
            {
                ZedNode node = _nodes[i];
                int recordOffset = firstRecordOffset + (i * RecordSize);

                Write32(output, recordOffset + 0x00, checked(StringBasePointer + nameOffsets[node.Name]));
                Write32(output, recordOffset + 0x04, node.Data.Length == 0 ? 0u : node.DataOffset);
                Write32(output, recordOffset + 0x08, (uint)node.Data.Length);
                Write32(output, recordOffset + 0x0C, node.Type);

                if (node.Data.Length > 0)
                {
                    Buffer.BlockCopy(
                        node.Data,
                        0,
                        output,
                        checked(bodyStart + (int)node.DataOffset),
                        node.Data.Length);
                }
            }

            for (int i = firstRecordOffset + recordBytes; i < bodyStart; i++)
                output[i] = 0xAF;

            return output;
        }

        public void Dispose()
        {
            _nodes.Clear();
            _stringOrder.Clear();
            _isOpen = false;
        }

        private void Load(byte[] data)
        {
            if (data.Length < FixedHeaderSize)
                throw new InvalidDataException("Data too short to be a ZED/ZAR container.");

            _nodes.Clear();
            _stringOrder.Clear();

            uint slotCount = Read32(data, 0x04);
            if (slotCount == 0)
                throw new InvalidDataException("Invalid slot count.");

            uint stringTableSize = Read32(data, 0x08);
            int stringTableStart = FixedHeaderSize;
            int stringTableEnd = checked(stringTableStart + (int)stringTableSize);
            if (stringTableEnd > data.Length)
                throw new InvalidDataException("String table exceeds file size.");

            StringBasePointer = Read32(data, 0x0C);
            HeaderWord10 = Read32(data, 0x10);
            HeaderWord58 = Read32(data, 0x58);
            HeaderWord5C = Read32(data, 0x5C);
            HeaderWord60 = Read32(data, 0x60);

            int entryCount = checked((int)slotCount - 1);
            int firstRecordOffset = checked(FixedHeaderSize + (int)stringTableSize + 0x10);
            int recordEnd = checked(firstRecordOffset + (entryCount * RecordSize));

            uint bodySize = Read32(data, 0x54);
            int bodyStart = checked(data.Length - (int)bodySize);
            if (bodyStart < recordEnd)
                throw new InvalidDataException("Record table overlaps the body region.");

            if (firstRecordOffset - 4 < 0 || firstRecordOffset > data.Length)
                throw new InvalidDataException("Record table offset is invalid.");

            RootValue = Read32(data, firstRecordOffset - 4);
            LoadStringOrder(data, stringTableStart, stringTableEnd);

            for (int i = 0; i < entryCount; i++)
            {
                int recordOffset = firstRecordOffset + (i * RecordSize);
                uint namePointer = Read32(data, recordOffset + 0x00);
                uint relativeOffset = Read32(data, recordOffset + 0x04);
                uint size = Read32(data, recordOffset + 0x08);
                uint type = Read32(data, recordOffset + 0x0C);

                string name = ResolveName(data, stringTableStart, stringTableEnd, namePointer);
                RememberName(name);

                byte[] nodeData = Array.Empty<byte>();
                if (size != 0)
                {
                    int payloadStart = checked(bodyStart + (int)relativeOffset);
                    int payloadEnd = checked(payloadStart + (int)size);
                    if (payloadStart < bodyStart || payloadEnd > data.Length)
                        throw new InvalidDataException($"Node '{name}' points outside the body region.");

                    nodeData = new byte[size];
                    Buffer.BlockCopy(data, payloadStart, nodeData, 0, (int)size);
                }

                _nodes.Add(new ZedNode
                {
                    Name = name,
                    Data = nodeData,
                    DataOffset = size == 0 ? 0u : relativeOffset,
                    Type = type,
                });
            }

            _isOpen = true;
        }

        private void LoadStringOrder(byte[] data, int start, int end)
        {
            int cursor = start;
            while (cursor < end)
            {
                int zero = Array.IndexOf(data, (byte)0, cursor, end - cursor);
                if (zero < 0) zero = end;

                if (zero > cursor)
                {
                    string value = Encoding.ASCII.GetString(data, cursor, zero - cursor);
                    RememberName(value);
                }

                cursor = zero + 1;
            }
        }

        private string ResolveName(byte[] data, int stringTableStart, int stringTableEnd, uint namePointer)
        {
            if (namePointer < StringBasePointer)
                throw new InvalidDataException("Node name pointer precedes the string base.");

            uint relative = namePointer - StringBasePointer;
            int stringOffset = checked(stringTableStart + (int)relative);
            if (stringOffset < stringTableStart || stringOffset >= stringTableEnd)
                throw new InvalidDataException("Node name pointer falls outside the string table.");

            int zero = Array.IndexOf(data, (byte)0, stringOffset, stringTableEnd - stringOffset);
            if (zero < 0) zero = stringTableEnd;
            return Encoding.ASCII.GetString(data, stringOffset, zero - stringOffset);
        }

        private byte[] BuildStringTable(out Dictionary<string, uint> nameOffsets)
        {
            var usedNames = new HashSet<string>(_nodes.Select(n => n.Name), StringComparer.OrdinalIgnoreCase);
            var orderedNames = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string name in _stringOrder)
            {
                if (!usedNames.Contains(name)) continue;
                if (seen.Add(name))
                    orderedNames.Add(name);
            }

            foreach (ZedNode node in _nodes)
            {
                if (seen.Add(node.Name))
                    orderedNames.Add(node.Name);
            }

            using var ms = new MemoryStream();
            nameOffsets = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);

            foreach (string name in orderedNames)
            {
                nameOffsets[name] = (uint)ms.Position;
                byte[] ascii = Encoding.ASCII.GetBytes(name);
                ms.Write(ascii, 0, ascii.Length);
                ms.WriteByte(0);
            }

            return ms.ToArray();
        }

        private void RememberName(string name)
        {
            if (_stringOrder.Any(existing => string.Equals(existing, name, StringComparison.OrdinalIgnoreCase)))
                return;

            _stringOrder.Add(name);
        }

        private static string NormalizeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Name must not be blank.", nameof(name));

            if (name.IndexOf('\0') >= 0)
                throw new ArgumentException("Name must not contain embedded null characters.", nameof(name));

            return name.Trim();
        }

        private void EnsureOpen()
        {
            if (!_isOpen)
                throw new InvalidOperationException("No container is currently loaded.");
        }

        private static uint AlignUp(uint value, uint alignment)
        {
            if (alignment == 0)
                throw new ArgumentOutOfRangeException(nameof(alignment), "Alignment must be greater than zero.");

            uint mask = alignment - 1;
            return (value + mask) & ~mask;
        }

        private static int AlignUp(int value, int alignment)
        {
            if (alignment <= 0)
                throw new ArgumentOutOfRangeException(nameof(alignment), "Alignment must be greater than zero.");

            int mask = alignment - 1;
            return (value + mask) & ~mask;
        }

        private static uint Read32(byte[] data, int offset) => BitConverter.ToUInt32(data, offset);

        private static void Write32(byte[] data, int offset, uint value)
        {
            data[offset + 0] = (byte)(value);
            data[offset + 1] = (byte)(value >> 8);
            data[offset + 2] = (byte)(value >> 16);
            data[offset + 3] = (byte)(value >> 24);
        }
    }

    /// <summary>
    /// Structured view for ZED files.
    /// Use this when the file is a named graph of records rather than a plain child-file archive.
    /// </summary>
    public sealed class ZedStructure : IDisposable
    {
        private readonly ZedContainer _container;

        private ZedStructure(ZedContainer container)
        {
            _container = container;
        }

        public uint RootValue
        {
            get => _container.RootValue;
            set => _container.RootValue = value;
        }

        public uint StringBasePointer
        {
            get => _container.StringBasePointer;
            set => _container.StringBasePointer = value;
        }

        public uint HeaderWord5C
        {
            get => _container.HeaderWord5C;
            set => _container.HeaderWord5C = value;
        }

        public uint DataAlignment
        {
            get => _container.DataAlignment;
            set => _container.DataAlignment = value;
        }

        public IReadOnlyList<ZedNode> Nodes => _container.Nodes;
        public int NodeCount => _container.NodeCount;
        public ZedContainer Container => _container;

        public static ZedStructure Open(string filePath) => new ZedStructure(ZedContainer.Open(filePath));
        public static ZedStructure FromBytes(byte[] data) => new ZedStructure(ZedContainer.FromBytes(data));

        public static ZedStructure CreateNew(
            uint stringBasePointer = 0x10000000u,
            uint rootValue = 0,
            uint headerWord5C = 0x00010001u,
            uint dataAlignment = ZedContainer.DefaultDataAlignment) =>
            new ZedStructure(ZedContainer.CreateNew(stringBasePointer, rootValue, headerWord5C, dataAlignment));

        public ZedNode GetNode(int index) => _container.GetNode(index);
        public ZedNode? FindNode(string name) => _container.FindNode(name);
        public IReadOnlyList<ZedNode> FindNodes(string name) => _container.FindNodes(name);
        public ZedNode AddNode(string name, byte[] data, uint type = 0) => _container.AddNode(name, data, type);
        public void RenameNode(int index, string newName) => _container.RenameNode(index, newName);
        public void ReplaceNodeData(int index, byte[] newData) => _container.ReplaceNodeData(index, newData);
        public void RemoveNode(int index) => _container.RemoveNode(index);
        public int RemoveNode(string name) => _container.RemoveNode(name);
        public void ExtractNode(int index, string outputPath) => _container.ExtractNode(index, outputPath);
        public void ExtractAll(string outputDirectory) => _container.ExtractAll(outputDirectory);
        public void RepackData() => _container.RepackData();
        public void Save(string filePath) => _container.Save(filePath);
        public byte[] ToBytes() => _container.ToBytes();
        public void Dispose() => _container.Dispose();
    }
}
