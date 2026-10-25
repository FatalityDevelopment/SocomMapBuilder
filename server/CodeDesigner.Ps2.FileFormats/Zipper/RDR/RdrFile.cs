using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json.Serialization;

namespace CodeDesigner.Ps2.FileFormats.Zipper.RDR
{
    /// <summary>
    /// Reader for Zipper '.rdr' typed-table files. These hold runtime data
    /// tables like vehicles, missions, weapons (zweapon.rdr), AI maps, etc.
    ///
    /// Layout:
    ///   0x00 u32 version (= 1)
    ///   0x04 u32 stringTableSize
    ///   0x08 u32 objectTableOffset
    ///   0x0C     string pool (null-terminated strings)
    ///   aligned  object table (sequence of 8-byte atoms)
    ///
    /// Each atom is { uint Tag; uint Value }:
    ///   Tag low byte = type
    ///     0x01 = int
    ///     0x02 = float
    ///     0x03 = string offset (Value points into string pool)
    ///     0x04 = container/list (Value = byte offset into object table where
    ///            the contained items start; (Tag >> 16) is the item count)
    ///
    /// Each named field in the resulting tree is a container with exactly two
    /// children: a string (the field name) and a value (which can itself be
    /// a container - list of items or sub-object). The root atom always begins
    /// at object-table-offset and is the document root container.
    /// </summary>
    public sealed class RdrFile
    {
        public uint Version { get; init; }
        public byte[] StringPool { get; init; } = Array.Empty<byte>();
        public RdrAtom[] Atoms { get; init; } = Array.Empty<RdrAtom>();
        public uint ObjectTableOffset { get; init; }

        public static RdrFile Parse(byte[] data)
        {
            if (data.Length < 0x0C)
                throw new InvalidDataException($"RDR file too small ({data.Length} bytes).");

            uint version = BitConverter.ToUInt32(data, 0x00);
            uint stringTableSize = BitConverter.ToUInt32(data, 0x04);
            uint objectTableOffset = BitConverter.ToUInt32(data, 0x08);

            int poolStart = 0x0C;
            int poolEnd = poolStart + (int)stringTableSize;
            if (poolEnd > data.Length)
                throw new InvalidDataException("RDR string table exceeds file size.");

            if (objectTableOffset > data.Length)
                throw new InvalidDataException("RDR object table offset is past EOF.");

            byte[] pool = new byte[stringTableSize];
            Buffer.BlockCopy(data, poolStart, pool, 0, (int)stringTableSize);

            int objectBytes = data.Length - (int)objectTableOffset;
            int atomCount = objectBytes / 8;
            var atoms = new RdrAtom[atomCount];
            for (int i = 0; i < atomCount; i++)
            {
                int o = (int)objectTableOffset + i * 8;
                atoms[i] = new RdrAtom
                {
                    Tag = BitConverter.ToUInt32(data, o),
                    Value = BitConverter.ToUInt32(data, o + 4),
                };
            }

            return new RdrFile
            {
                Version = version,
                StringPool = pool,
                Atoms = atoms,
                ObjectTableOffset = objectTableOffset,
            };
        }

        /// <summary>Resolve a null-terminated ASCII string from the string pool.</summary>
        public string GetString(uint offset)
        {
            if (offset >= StringPool.Length) return string.Empty;
            int end = (int)offset;
            while (end < StringPool.Length && StringPool[end] != 0) end++;
            return Encoding.ASCII.GetString(StringPool, (int)offset, end - (int)offset);
        }

        /// <summary>
        /// Resolve the atom at a byte offset inside the object table.
        /// Returns null if the offset is out of range.
        /// </summary>
        public RdrAtom? AtomAtByteOffset(uint byteOffset)
        {
            int idx = (int)(byteOffset / 8);
            return idx >= 0 && idx < Atoms.Length ? Atoms[idx] : null;
        }

        /// <summary>
        /// Build a recursive tree starting at the first atom (the root atom is
        /// always atom 0 per the format). Container atoms are followed via
        /// their byte-offset Value field.
        /// </summary>
        public RdrNode BuildTree()
        {
            if (Atoms.Length == 0)
            {
                return new RdrNode { Atom = new RdrAtom() };
            }
            var seen = new HashSet<int>();
            return BuildNode(0, seen);
        }

        private RdrNode BuildNode(int atomIndex, HashSet<int> seen)
        {
            if (!seen.Add(atomIndex))
            {
                // Cycle guard - return an empty node to keep traversal finite.
                return new RdrNode { Atom = Atoms[atomIndex] };
            }
            var atom = Atoms[atomIndex];
            var node = new RdrNode { Atom = atom };

            switch (atom.TypeCode)
            {
                case 0x01:
                    node.IntValue = (int)atom.Value;
                    break;
                case 0x02:
                    node.FloatValue = BitConverter.Int32BitsToSingle((int)atom.Value);
                    break;
                case 0x03:
                    node.StringValue = GetString(atom.Value);
                    break;
                case 0x04:
                    int firstChildIdx = (int)(atom.Value / 8);
                    for (int i = 0; i < atom.ItemCount; i++)
                    {
                        int childIdx = firstChildIdx + i;
                        if (childIdx < 0 || childIdx >= Atoms.Length) break;
                        node.Children.Add(BuildNode(childIdx, seen));
                    }
                    break;
            }
            return node;
        }
    }

    /// <summary>One raw 8-byte typed atom from an RDR file.</summary>
    public sealed class RdrAtom
    {
        public uint Tag { get; init; }
        public uint Value { get; init; }

        public byte TypeCode => (byte)(Tag & 0xFF);
        public ushort ItemCount => (ushort)((Tag >> 16) & 0xFFFF);
        public byte Flags => (byte)((Tag >> 8) & 0xFF);
    }

    /// <summary>A node in the recursive RDR tree.</summary>
    public sealed class RdrNode
    {
        public RdrAtom Atom { get; init; } = new RdrAtom();
        public List<RdrNode> Children { get; } = new();
        public int? IntValue { get; set; }
        public float? FloatValue { get; set; }
        public string? StringValue { get; set; }

        public byte TypeCode => Atom.TypeCode;
        public bool IsContainer => TypeCode == 0x04;
        public bool IsString => TypeCode == 0x03;
        public bool IsInt => TypeCode == 0x01;
        public bool IsFloat => TypeCode == 0x02;

        /// <summary>
        /// Treats this container as an object - children that themselves are
        /// 2-element containers [string name, value] become named fields.
        /// Other children become indexed entries with name = $"#{index}".
        /// </summary>
        public Dictionary<string, RdrNode> AsObject()
        {
            var result = new Dictionary<string, RdrNode>(StringComparer.OrdinalIgnoreCase);
            int unnamedIndex = 0;
            foreach (var c in Children)
            {
                if (c.IsContainer && c.Children.Count == 2 && c.Children[0].IsString)
                {
                    string key = c.Children[0].StringValue ?? $"#{unnamedIndex++}";
                    if (!result.ContainsKey(key))
                    {
                        result[key] = c.Children[1];
                    }
                    else
                    {
                        // duplicate field name - append index to keep both
                        result[$"{key}#{unnamedIndex++}"] = c.Children[1];
                    }
                }
                else
                {
                    result[$"#{unnamedIndex++}"] = c;
                }
            }
            return result;
        }

        /// <summary>Unwrap a single-item container chain to its leaf value.</summary>
        public RdrNode Unwrap()
        {
            var n = this;
            while (n.IsContainer && n.Children.Count == 1)
            {
                n = n.Children[0];
            }
            return n;
        }
    }

    /// <summary>
    /// Optional second-stage shaper that turns an RDR tree into a JSON-friendly
    /// nested dictionary. Wraps single-element containers and exposes typed
    /// leaf values.
    /// </summary>
    public static class RdrShape
    {
        public static object Shape(RdrNode node)
        {
            switch (node.TypeCode)
            {
                case 0x01: return node.IntValue ?? 0;
                case 0x02: return node.FloatValue ?? 0f;
                case 0x03: return node.StringValue ?? string.Empty;
                case 0x04:
                {
                    // If every child looks like a named field, present as an object.
                    bool allNamed = node.Children.Count > 0 && node.Children.TrueForAll(
                        c => c.IsContainer && c.Children.Count == 2 && c.Children[0].IsString);
                    if (allNamed)
                    {
                        var obj = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                        int dup = 0;
                        foreach (var c in node.Children)
                        {
                            string key = c.Children[0].StringValue ?? $"#{dup}";
                            if (obj.ContainsKey(key)) { key = $"{key}#{dup++}"; }
                            obj[key] = Shape(c.Children[1].Unwrap());
                        }
                        return obj;
                    }
                    // Otherwise, present as an array.
                    var arr = new List<object>(node.Children.Count);
                    foreach (var c in node.Children) arr.Add(Shape(c.Unwrap()));
                    return arr;
                }
                default:
                    return new { unknownType = node.TypeCode, raw = node.Atom.Value };
            }
        }
    }
}
