using System.Text;

namespace MapBuilder.Tests.Fixtures;

/// <summary>
/// Builds synthetic .rdr bytes (layout: docs/formats/rdr.md) from a tree: <see cref="L"/> is a list, ints, floats and
/// strings are values, <see cref="F"/> is a named field in the pair form <c>[key, [values]]</c> and <see cref="Obj"/>
/// an object in the flat form <c>key, [values], key, [values]</c>. The same <see cref="Node"/> instance used twice is
/// written once and pointed at twice, the way retail files share value blocks.
/// </summary>
internal static class RdrBuilder
{
    internal sealed class Node { public readonly List<object> Items; public Node(IEnumerable<object> items) => Items = items.ToList(); }

    public static Node L(params object[] items) => new(items);
    public static Node F(string key, params object[] values) => L(key, values.Length == 1 && values[0] is Node n ? n : L(values));
    public static Node Obj(params (string Key, object Value)[] fields) =>
        new(fields.SelectMany(f => new[] { (object)f.Key, f.Value is Node n ? n : L(f.Value) }));

    public static byte[] Build(Node root)
    {
        var pool = new List<byte>();
        var strings = new Dictionary<string, uint>();
        uint Str(string s)
        {
            if (strings.TryGetValue(s, out uint at)) return at;
            at = (uint)pool.Count;
            pool.AddRange(Encoding.ASCII.GetBytes(s));
            pool.Add(0);
            strings[s] = at;
            return at;
        }
        var atoms = new List<(uint Tag, uint Value)> { (0, 0) };
        var blocks = new Dictionary<Node, int>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<(int Atom, Node Node)>();
        queue.Enqueue((0, root));
        while (queue.Count > 0)
        {
            var (atom, node) = queue.Dequeue();
            if (blocks.TryGetValue(node, out int shared)) { atoms[atom] = (0x04u | ((uint)node.Items.Count << 16), (uint)shared * 8); continue; }
            int block = atoms.Count;
            blocks[node] = block;
            atoms[atom] = (0x04u | ((uint)node.Items.Count << 16), (uint)block * 8);
            foreach (var item in node.Items) atoms.Add((0, 0));
            for (int i = 0; i < node.Items.Count; i++)
            {
                switch (node.Items[i])
                {
                    case int n: atoms[block + i] = (0x01, unchecked((uint)n)); break;
                    case float f: atoms[block + i] = (0x02, (uint)BitConverter.SingleToInt32Bits(f)); break;
                    case string s: atoms[block + i] = (0x03, Str(s)); break;
                    case Node child: queue.Enqueue((block + i, child)); break;
                    default: throw new ArgumentException($"Unsupported RDR value {node.Items[i]}");
                }
            }
        }
        int table = (0x0C + pool.Count + 3) / 4 * 4;
        var bytes = new byte[table + atoms.Count * 8];
        BitConverter.GetBytes(1u).CopyTo(bytes, 0);
        BitConverter.GetBytes((uint)pool.Count).CopyTo(bytes, 4);
        BitConverter.GetBytes((uint)table).CopyTo(bytes, 8);
        pool.ToArray().CopyTo(bytes, 0x0C);
        for (int i = 0; i < atoms.Count; i++)
        {
            BitConverter.GetBytes(atoms[i].Tag).CopyTo(bytes, table + i * 8);
            BitConverter.GetBytes(atoms[i].Value).CopyTo(bytes, table + i * 8 + 4);
        }
        return bytes;
    }
}
