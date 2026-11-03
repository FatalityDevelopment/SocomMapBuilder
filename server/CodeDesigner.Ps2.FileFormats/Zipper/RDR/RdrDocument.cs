using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace CodeDesigner.Ps2.FileFormats.Zipper.RDR
{
    /// <summary>
    /// An .rdr file to edit (see <see cref="RdrFile"/> for the layout): its atoms as a list, values set in place,
    /// strings added to the end of the pool, list items duplicated or removed. Saving an unedited document gives the
    /// same bytes back: the header, the pool, the bytes between the pool and the atoms and anything after the last
    /// whole atom are kept as they were.
    /// </summary>
    public sealed class RdrDocument
    {
        public const byte Int = 0x01, Float = 0x02, String = 0x03, List = 0x04;

        public uint Version { get; private set; }
        private byte[] _pool = Array.Empty<byte>();
        private byte[] _gap = Array.Empty<byte>();
        private byte[] _tail = Array.Empty<byte>();
        private int _alignment = 4;
        private readonly List<(uint Tag, uint Value)> _atoms = new();

        public int AtomCount => _atoms.Count;

        public static RdrDocument Parse(byte[] data)
        {
            if (data.Length < 0x0C) throw new InvalidDataException($"RDR file too small ({data.Length} bytes).");
            uint poolSize = BitConverter.ToUInt32(data, 4), table = BitConverter.ToUInt32(data, 8);
            int poolEnd = 0x0C + (int)poolSize;
            if (poolEnd > data.Length || table > data.Length || table < poolEnd) throw new InvalidDataException("RDR tables run past the end of the file.");
            var doc = new RdrDocument
            {
                Version = BitConverter.ToUInt32(data, 0),
                _pool = data[0x0C..poolEnd],
                _gap = data[poolEnd..(int)table],
            };
            // The pool's alignment, for when it grows: the largest power of two (to 64) the atoms' offset honours.
            for (int a = 64; a >= 1; a /= 2)
                if (table == AlignUp(poolEnd, a)) { doc._alignment = a; break; }
            int count = (data.Length - (int)table) / 8;
            for (int i = 0; i < count; i++)
                doc._atoms.Add((BitConverter.ToUInt32(data, (int)table + i * 8), BitConverter.ToUInt32(data, (int)table + i * 8 + 4)));
            doc._tail = data[((int)table + count * 8)..];
            return doc;
        }

        public byte[] ToBytes()
        {
            int poolEnd = 0x0C + _pool.Length;
            int table = poolEnd + _gap.Length;
            var out_ = new byte[table + _atoms.Count * 8 + _tail.Length];
            BitConverter.GetBytes(Version).CopyTo(out_, 0);
            BitConverter.GetBytes((uint)_pool.Length).CopyTo(out_, 4);
            BitConverter.GetBytes((uint)table).CopyTo(out_, 8);
            _pool.CopyTo(out_, 0x0C);
            _gap.CopyTo(out_, poolEnd);
            for (int i = 0; i < _atoms.Count; i++)
            {
                BitConverter.GetBytes(_atoms[i].Tag).CopyTo(out_, table + i * 8);
                BitConverter.GetBytes(_atoms[i].Value).CopyTo(out_, table + i * 8 + 4);
            }
            _tail.CopyTo(out_, table + _atoms.Count * 8);
            return out_;
        }

        // ------------------------------------------------------------------ reading

        public byte TypeOf(int atom) => (byte)(Atom(atom).Tag & 0xFF);
        public int CountOf(int atom) => (int)(Atom(atom).Tag >> 16);
        public int FirstChild(int atom) => (int)(Atom(atom).Value / 8);

        private (uint Tag, uint Value) Atom(int i) =>
            i >= 0 && i < _atoms.Count ? _atoms[i] : throw new ArgumentOutOfRangeException(nameof(i), $"No atom #{i}");

        public string StringAt(uint offset)
        {
            if (offset >= _pool.Length) return string.Empty;
            int end = Array.IndexOf(_pool, (byte)0, (int)offset);
            if (end < 0) end = _pool.Length;
            return Encoding.ASCII.GetString(_pool, (int)offset, end - (int)offset);
        }

        /// <summary>A node's value: int, float or string; null for lists and unknown types.</summary>
        public object? ValueOf(int atom) => TypeOf(atom) switch
        {
            Int => (int)Atom(atom).Value,
            Float => BitConverter.Int32BitsToSingle((int)Atom(atom).Value),
            String => StringAt(Atom(atom).Value),
            _ => null,
        };

        public IEnumerable<int> Children(int atom)
        {
            if (TypeOf(atom) != List) yield break;
            int first = FirstChild(atom), count = CountOf(atom);
            for (int i = 0; i < count && first + i < _atoms.Count; i++) yield return first + i;
        }

        /// <summary>The list holding <paramref name="atom"/>, or -1 (the root, or an atom nothing reaches).</summary>
        public int ParentOf(int atom)
        {
            for (int i = 0; i < _atoms.Count; i++)
                if (TypeOf(i) == List && atom >= FirstChild(i) && atom < FirstChild(i) + CountOf(i) && i != atom) return i;
            return -1;
        }

        /// <summary>The tree from the root (atom 0) for the page: [atom, type, value, children?]. Lists may share items
        /// (retail files point many lists at one block): a shared value shows wherever it is used, a list already shown
        /// comes back as [atom, "shared", count] so the tree stays finite.</summary>
        public object Tree()
        {
            var seen = new HashSet<int>();
            object Node(int a)
            {
                byte t = TypeOf(a);
                if (t != List)
                    return new object[] { a, t switch { Int => "int", Float => "float", String => "string", _ => $"0x{t:X2}" }, ValueOf(a) ?? Atom(a).Value };
                if (!seen.Add(a)) return new object[] { a, "shared", CountOf(a) };
                return new object[] { a, "list", CountOf(a), Children(a).Select(Node).ToList() };
            }
            return _atoms.Count == 0 ? new object[] { 0, "list", 0, new List<object>() } : Node(0);
        }

        // ------------------------------------------------------------------ editing

        public void SetInt(int atom, int value) { Require(atom, Int); Set(atom, (uint)value); }

        public void SetFloat(int atom, float value)
        {
            Require(atom, Float);
            if (!float.IsFinite(value)) throw new ArgumentException("The number must be finite.");
            Set(atom, (uint)BitConverter.SingleToInt32Bits(value));
        }

        /// <summary>Points the atom at the string, adding it to the end of the pool unless the pool already has it.</summary>
        public void SetString(int atom, string value)
        {
            Require(atom, String);
            if (value.Any(c => c < 0x20 || c > 0x7E)) throw new ArgumentException("Strings are printable ASCII.");
            Set(atom, PoolOffset(value));
        }

        /// <summary>Sets from text: an int, a float or a string, as the atom already is.</summary>
        public void SetFromText(int atom, string text)
        {
            switch (TypeOf(atom))
            {
                case Int:
                    SetInt(atom, int.TryParse(text.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int i) ? i
                        : throw new ArgumentException($"\"{text}\" is not a whole number."));
                    break;
                case Float:
                    SetFloat(atom, float.TryParse(text.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float f) ? f
                        : throw new ArgumentException($"\"{text}\" is not a number."));
                    break;
                case String: SetString(atom, text); break;
                default: throw new InvalidOperationException("Only numbers and strings have a value to set.");
            }
        }

        /// <summary>Copies a list item (and everything under it) in after the list's last item; answers the copy.</summary>
        public int Duplicate(int item)
        {
            int parent = ParentOf(item);
            if (parent < 0) throw new InvalidOperationException("Only an item of a list can be duplicated.");
            if (CountOf(parent) >= 0xFFFF) throw new InvalidOperationException("The list is full.");
            var copy = CopySubtree(item);
            int at = FirstChild(parent) + CountOf(parent);
            Insert(at, copy);
            SetCount(parent, CountOf(parent) + 1);
            return at;
        }

        /// <summary>
        /// Gives a list its own copy of its items (a new block at the end of the table) and answers the first: retail
        /// files point many lists at one value (every "1", every shared name), so a value is owned before it is set.
        /// </summary>
        public int Own(int list)
        {
            if (TypeOf(list) != List) throw new InvalidOperationException($"Atom #{list} is not a list.");
            var items = Children(list).ToList();
            int block = _atoms.Count;
            for (int i = 0; i < items.Count; i++) _atoms.Add((0, 0));
            for (int i = 0; i < items.Count; i++) _atoms[block + i] = CopySubtree(items[i]);
            _atoms[list] = (_atoms[list].Tag, (uint)(block * 8));
            return block;
        }

        /// <summary>Takes an item out of its list (what it held stays in the file, unreferenced).</summary>
        public void Remove(int item)
        {
            int parent = ParentOf(item);
            if (parent < 0) throw new InvalidOperationException("Only an item of a list can be removed.");
            _atoms.RemoveAt(item);
            for (int i = 0; i < _atoms.Count; i++)
                if (TypeOf(i) == List && FirstChild(i) > item) _atoms[i] = (_atoms[i].Tag, _atoms[i].Value - 8);
            int p = parent > item ? parent - 1 : parent;
            SetCount(p, CountOf(p) - 1);
        }

        // A copy of an atom; a list's items are copied to a new block at the end of the table.
        private (uint Tag, uint Value) CopySubtree(int atom)
        {
            var a = Atom(atom);
            if (TypeOf(atom) != List) return a;
            var items = Children(atom).ToList();
            int block = _atoms.Count;
            for (int i = 0; i < items.Count; i++) _atoms.Add((0, 0));
            for (int i = 0; i < items.Count; i++) _atoms[block + i] = CopySubtree(items[i]);
            return (a.Tag, (uint)(block * 8));
        }

        // Inserts an atom; lists whose items start at or after it move up one.
        private void Insert(int at, (uint Tag, uint Value) atom)
        {
            for (int i = 0; i < _atoms.Count; i++)
                if (TypeOf(i) == List && FirstChild(i) >= at && CountOf(i) > 0) _atoms[i] = (_atoms[i].Tag, _atoms[i].Value + 8);
            if (atom.Tag != 0 && (byte)atom.Tag == List && atom.Value / 8 >= at) atom.Value += 8;
            _atoms.Insert(at, atom);
        }

        private void SetCount(int list, int count) => _atoms[list] = ((_atoms[list].Tag & 0xFFFF) | ((uint)count << 16), _atoms[list].Value);

        private void Set(int atom, uint value) => _atoms[atom] = (_atoms[atom].Tag, value);

        private void Require(int atom, byte type)
        {
            if (TypeOf(atom) != type) throw new InvalidOperationException($"Atom #{atom} is not a {type switch { Int => "whole number", Float => "number", _ => "string" }}.");
        }

        private uint PoolOffset(string value)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(value + "\0");
            // An existing string (the start of a pool entry) is shared.
            for (int i = 0; i + bytes.Length <= _pool.Length; i++)
                if ((i == 0 || _pool[i - 1] == 0) && _pool.AsSpan(i, bytes.Length).SequenceEqual(bytes)) return (uint)i;
            uint offset = (uint)_pool.Length;
            _pool = _pool.Concat(bytes).ToArray();
            // Keep the atoms on the alignment the file used.
            int poolEnd = 0x0C + _pool.Length;
            _gap = new byte[AlignUp(poolEnd, _alignment) - poolEnd];
            return offset;
        }

        private static int AlignUp(int v, int a) => (v + a - 1) / a * a;
    }
}
