namespace CodeDesigner.Ps2.FileFormats.Zipper.ZED
{
    /// <summary>
    /// Tree edits on a ZED/ZAR container. Records are stored in pre-order and each record's <c>Type</c> is its
    /// child count, so inserting or removing a subtree must also adjust the parent's count.
    /// </summary>
    public static class ZedTree
    {
        /// <summary>A record to insert, with its children (pre-order).</summary>
        public sealed record Record(string Name, byte[] Data, IReadOnlyList<Record> Children)
        {
            public Record(string name, byte[] data) : this(name, data, Array.Empty<Record>()) { }
        }

        /// <summary>Parent record index per record; -1 for top-level records.</summary>
        public static int[] ParentIndices(IReadOnlyList<ZedNode> nodes)
        {
            var parent = new int[nodes.Count];
            int cursor = 0;
            void Walk(int p)
            {
                int self = cursor++;
                parent[self] = p;
                uint kids = nodes[self].Type;
                for (uint k = 0; k < kids && cursor < nodes.Count; k++) Walk(self);
            }
            while (cursor < nodes.Count) Walk(-1);
            return parent;
        }

        /// <summary>Index one past the last record of the subtree rooted at <paramref name="index"/>.</summary>
        public static int SubtreeEnd(IReadOnlyList<ZedNode> nodes, int index)
        {
            int next = index + 1;
            for (uint c = 0; c < nodes[index].Type && next < nodes.Count; c++) next = SubtreeEnd(nodes, next);
            return next;
        }

        /// <summary>Direct children of a record.</summary>
        public static List<int> Children(IReadOnlyList<ZedNode> nodes, int index)
        {
            var list = new List<int>();
            int cursor = index + 1;
            for (uint c = 0; c < nodes[index].Type && cursor < nodes.Count; c++)
            {
                list.Add(cursor);
                cursor = SubtreeEnd(nodes, cursor);
            }
            return list;
        }

        /// <summary>Copies the subtree rooted at <paramref name="index"/>.</summary>
        public static Record Copy(IReadOnlyList<ZedNode> nodes, int index) =>
            new(nodes[index].Name, (byte[])nodes[index].Data.Clone(), Children(nodes, index).Select(c => Copy(nodes, c)).ToList());

        /// <summary>Removes the subtree rooted at <paramref name="index"/> and decrements its parent's count.</summary>
        public static void Remove(ZedContainer container, int index)
        {
            int parent = ParentIndices(container.Nodes)[index];
            int end = SubtreeEnd(container.Nodes, index);
            for (int i = end - 1; i >= index; i--) container.RemoveNode(i);
            if (parent >= 0) container.Nodes[parent].Type--;
        }

        /// <summary>
        /// Appends <paramref name="record"/> as the last child of <paramref name="parent"/> and returns the index of
        /// the inserted subtree's root.
        /// </summary>
        public static int AppendChild(ZedContainer container, int parent, Record record)
        {
            int at = SubtreeEnd(container.Nodes, parent);
            int cursor = at;
            void Insert(Record r)
            {
                container.InsertNode(cursor++, r.Name, r.Data, (uint)r.Children.Count);
                foreach (Record c in r.Children) Insert(c);
            }
            Insert(record);
            container.Nodes[parent].Type++;
            return at;
        }
    }
}
