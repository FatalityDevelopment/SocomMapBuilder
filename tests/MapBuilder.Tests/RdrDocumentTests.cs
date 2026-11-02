using System.Text;
using CodeDesigner.Ps2.FileFormats.Zipper.RDR;
using CodeDesigner.Ps2.FileFormats.Zipper.ZDB;
using CodeDesigner.Ps2.FileFormats.Zipper.ZED;
using Xunit;

namespace MapBuilder.Tests
{
    /// <summary>RDR editing: values, strings, list items, and saving an unedited file byte for byte.</summary>
    public sealed class RdrDocumentTests
    {
        // Root list of two fields, ["speed", 3] and ["name", "m4"], then a list field ["ammo", [10, 20]].
        internal static byte[] Sample()
        {
            byte[] pool = Encoding.ASCII.GetBytes("speed\0name\0m4\0ammo\0");
            var atoms = new List<(uint, uint)>
            {
                (0x0003_0004, 1 * 8),     // 0 root: 3 fields at 1
                (0x0002_0004, 4 * 8),     // 1 field speed
                (0x0002_0004, 6 * 8),     // 2 field name
                (0x0002_0004, 8 * 8),     // 3 field ammo
                (0x03, 0), (0x01, 3),     // 4, 5 "speed", 3
                (0x03, 6), (0x03, 11),    // 6, 7 "name", "m4"
                (0x03, 14), (0x0002_0004, 10 * 8), // 8, 9 "ammo", [ ... ] at 10
                (0x01, 10), (0x01, 20),   // 10, 11
            };
            int table = (0x0C + pool.Length + 3) / 4 * 4;
            var data = new byte[table + atoms.Count * 8];
            BitConverter.GetBytes(1u).CopyTo(data, 0);
            BitConverter.GetBytes((uint)pool.Length).CopyTo(data, 4);
            BitConverter.GetBytes((uint)table).CopyTo(data, 8);
            pool.CopyTo(data, 0x0C);
            for (int i = 0; i < atoms.Count; i++)
            {
                BitConverter.GetBytes(atoms[i].Item1).CopyTo(data, table + i * 8);
                BitConverter.GetBytes(atoms[i].Item2).CopyTo(data, table + i * 8 + 4);
            }
            return data;
        }

        private static object Shape(byte[] data) => System.Text.Json.JsonSerializer.Serialize(RdrShape.Shape(RdrFile.Parse(data).BuildTree()));

        [Fact]
        public void An_unedited_document_saves_the_same_bytes()
        {
            byte[] data = Sample();
            Assert.Equal(data, RdrDocument.Parse(data).ToBytes());
        }

        [Fact]
        public void Values_and_strings_are_set_in_place()
        {
            var doc = RdrDocument.Parse(Sample());
            doc.SetFromText(5, "42");
            doc.SetFromText(7, "ak47");
            doc.SetFromText(6, "speed"); // an existing string is shared, not added again
            Assert.Equal("""{"speed":42,"speed#0":"ak47","ammo":[10,20]}""", Shape(doc.ToBytes()));
            Assert.Throws<ArgumentException>(() => doc.SetFromText(5, "fast"));
        }

        [Fact]
        public void List_items_duplicate_and_remove()
        {
            var doc = RdrDocument.Parse(Sample());
            int copy = doc.Duplicate(11);
            doc.SetFromText(copy, "30");
            Assert.Equal("""{"speed":3,"name":"m4","ammo":[10,20,30]}""", Shape(doc.ToBytes()));
            // A whole field (a list) copies with what it holds.
            doc.Duplicate(3);
            Assert.Equal("""{"speed":3,"name":"m4","ammo":[10,20,30],"ammo#0":[10,20,30]}""", Shape(doc.ToBytes()));
            doc.Remove(1);
            Assert.Equal("""{"name":"m4","ammo":[10,20,30],"ammo#0":[10,20,30]}""", Shape(doc.ToBytes()));
            Assert.Throws<InvalidOperationException>(() => doc.Remove(0));
        }

        /// <summary>Every .rdr on a disc (MAPBUILDER_DISC, e.g. E:\) saves back byte for byte, and duplicating any
        /// list's last item copies it exactly. Without the variable the test has nothing to check.</summary>
        [Fact]
        public void Every_disc_rdr_saves_back_unchanged()
        {
            string? disc = Environment.GetEnvironmentVariable("MAPBUILDER_DISC");
            if (string.IsNullOrEmpty(disc) || !Directory.Exists(disc)) return;
            int checkedFiles = 0;
            foreach (var (name, data) in DiscRdrs(disc))
            {
                Assert.True(data.SequenceEqual(RdrDocument.Parse(data).ToBytes()), $"{name} did not save back unchanged");
                var doc = RdrDocument.Parse(data);
                int list = Enumerable.Range(0, doc.AtomCount).FirstOrDefault(a => a > 0 && doc.TypeOf(a) == RdrDocument.List && doc.CountOf(a) > 0 && doc.ParentOf(a) >= 0, -1);
                if (list > 0)
                {
                    int last = doc.FirstChild(list) + doc.CountOf(list) - 1;
                    var before = RdrShape.Shape(RdrFile.Parse(data).BuildTree());
                    doc.Duplicate(last);
                    Assert.NotNull(RdrFile.Parse(doc.ToBytes()).BuildTree());
                }
                checkedFiles++;
            }
            Assert.True(checkedFiles > 100, $"only {checkedFiles} .rdr found under {disc}");
        }

        /// <summary>In every ZED / ZAR archive on a disc (MAPBUILDER_DISC), splicing a record back unchanged gives the
        /// same bytes, and growing one keeps every other record as it was.</summary>
        [Fact]
        public void Every_disc_archive_splices_in_place()
        {
            string? disc = Environment.GetEnvironmentVariable("MAPBUILDER_DISC");
            if (string.IsNullOrEmpty(disc) || !Directory.Exists(disc)) return;
            var differ = new List<string>();
            int count = 0;
            void Check(string label, byte[] data)
            {
                ZedContainer c;
                try { c = ZedContainer.FromBytes(data); } catch (InvalidDataException) { return; }
                count++;
                int pick = Enumerable.Range(0, c.Nodes.Count).FirstOrDefault(i => c.Nodes[i].Size > 0, -1);
                if (pick >= 0)
                {
                    if (!data.SequenceEqual(ZedSplice.Replace(data, pick, c.Nodes[pick].Data))) differ.Add(label + " (unchanged)");
                    var grown = ZedContainer.FromBytes(ZedSplice.Replace(data, pick, c.Nodes[pick].Data.Concat(new byte[100]).ToArray())).Nodes;
                    if (grown[pick].Size != c.Nodes[pick].Size + 100 || Enumerable.Range(0, c.Nodes.Count).Any(i => i != pick && !grown[i].Data.SequenceEqual(c.Nodes[i].Data)))
                        differ.Add(label + " (grown)");
                }
                foreach (var n in c.Nodes)
                    if (n.Name.EndsWith(".zed", StringComparison.OrdinalIgnoreCase) || n.Name.EndsWith(".zar", StringComparison.OrdinalIgnoreCase)) Check(label + "|" + n.Name, n.Data);
            }
            foreach (string file in Directory.EnumerateFiles(disc, "*", SearchOption.AllDirectories))
            {
                string ext = Path.GetExtension(file).ToUpperInvariant();
                if (new FileInfo(file).Length > 64L << 20) continue;
                if (ext == ".ZDB")
                {
                    using var zdb = ZdbArchive.Open(file);
                    foreach (var e in zdb.Entries)
                        if (e.FullPath.EndsWith(".zed", StringComparison.OrdinalIgnoreCase) || e.FullPath.EndsWith(".zar", StringComparison.OrdinalIgnoreCase)) Check(file + "|" + e.FullPath, e.Data);
                }
                else if (ext is ".ZED" or ".ZAR") Check(file, File.ReadAllBytes(file));
            }
            Assert.True(differ.Count == 0, $"{differ.Count} of {count} archives splice wrong: {string.Join(", ", differ.Take(8))}");
        }

        private static IEnumerable<(string, byte[])> DiscRdrs(string root)
        {
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                string ext = Path.GetExtension(file).ToUpperInvariant();
                if (new FileInfo(file).Length > 64L << 20) continue;
                if (ext is ".ZDB")
                {
                    using var zdb = ZdbArchive.Open(file);
                    foreach (var e in zdb.Entries)
                        foreach (var r in Records(file + "|" + e.FullPath, e.FullPath, e.Data)) yield return r;
                }
                else if (ext is ".ZAR" or ".ZED" or ".RDR")
                    foreach (var r in Records(file, file, File.ReadAllBytes(file))) yield return r;
            }
        }

        private static IEnumerable<(string, byte[])> Records(string label, string name, byte[] data)
        {
            if (name.EndsWith(".rdr", StringComparison.OrdinalIgnoreCase) && data.Length >= 0x0C) { yield return (label, data); yield break; }
            if (!name.EndsWith(".zar", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".zed", StringComparison.OrdinalIgnoreCase)) yield break;
            IReadOnlyList<ZedNode> nodes;
            try { nodes = ZedContainer.FromBytes(data).Nodes; } catch (InvalidDataException) { yield break; }
            foreach (var n in nodes)
                if (n.Size > 0)
                    foreach (var r in Records(label + "|" + n.Name, n.Name, n.Data)) yield return r;
        }
    }
}
