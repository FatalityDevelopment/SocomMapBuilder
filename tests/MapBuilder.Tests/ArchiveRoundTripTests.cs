using CodeDesigner.Ps2.FileFormats.Zipper.Package;
using CodeDesigner.Ps2.FileFormats.Zipper.ZDB;
using CodeDesigner.Ps2.FileFormats.Zipper.ZED;
using Xunit;

namespace MapBuilder.Tests
{
    /// <summary>
    /// Containers built from scratch survive write → read unchanged (no game files needed): the property every save
    /// relies on, since a package is rewritten whole with only the edited entries replaced.
    /// </summary>
    public class ArchiveRoundTripTests
    {
        private static byte[] Bytes(int length, int seed)
        {
            var rng = new Random(seed);
            var b = new byte[length];
            rng.NextBytes(b);
            return b;
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        public void Zdb_entries_round_trip(int type)
        {
            using var zdb = ZdbArchive.CreateNew(type);
            byte[] a = Bytes(1000, 1), b = Bytes(37, 2);
            zdb.AddEntry(@"RUN\MP\M99\M99_GEO.ZED", a);
            zdb.AddEntry(@"RUN\MP\M99\CLUTTER.ZAR", b);

            using var back = ZdbArchive.FromBytes(zdb.ToBytes());
            Assert.Equal(2, back.EntryCount);
            Assert.Equal(a, back.FindEntry(@"RUN\MP\M99\M99_GEO.ZED")!.Data);
            Assert.Equal(b, back.FindEntry(@"RUN\MP\M99\CLUTTER.ZAR")!.Data);
        }

        [Fact]
        public void Zdb_replaced_entry_keeps_the_others()
        {
            using var zdb = ZdbArchive.CreateNew(1);
            byte[] keep = Bytes(500, 3), before = Bytes(64, 4), after = Bytes(4096, 5);
            zdb.AddEntry(@"RUN\MP\M99\KEEP.ZED", keep);
            zdb.AddEntry(@"RUN\MP\M99\EDIT.ZED", before);

            using var reopened = ZdbArchive.FromBytes(zdb.ToBytes());
            int index = reopened.Entries.ToList().FindIndex(e => e.FullPath.EndsWith("EDIT.ZED", StringComparison.OrdinalIgnoreCase));
            reopened.ReplaceEntryData(index, after);

            using var back = ZdbArchive.FromBytes(reopened.ToBytes());
            Assert.Equal(keep, back.FindEntry(@"RUN\MP\M99\KEEP.ZED")!.Data);
            Assert.Equal(after, back.FindEntry(@"RUN\MP\M99\EDIT.ZED")!.Data);
        }

        [Fact]
        public void Zed_nodes_round_trip_through_add_rename_remove()
        {
            using var zed = ZedContainer.CreateNew();
            byte[] one = Bytes(100, 6), two = Bytes(3, 7), three = Bytes(777, 8);
            zed.AddNode("ONE", one);
            zed.AddNode("TWO", two);
            zed.AddNode("THREE", three);

            using var mid = ZedContainer.FromBytes(zed.ToBytes());
            mid.RenameNode(0, "FIRST");
            mid.RemoveNode("TWO");

            using var back = ZedContainer.FromBytes(mid.ToBytes());
            Assert.Equal(one, back.FindNode("FIRST")!.Data);
            Assert.Null(back.FindNode("ONE"));
            Assert.Null(back.FindNode("TWO"));
            Assert.Equal(three, back.FindNode("THREE")!.Data);
        }

        [Fact]
        public void Cdb_pack_lays_files_out_on_card_sectors()
        {
            byte[] a = Bytes(10, 9), b = Bytes(PackageSave.CdbAlign + 1, 10);
            byte[] cdb = PackageSave.BuildCdb(new[] { (@"RUN\MP\M99\A.ZED", a), (@"RUN\MP\M99\B.ZAR", b) });

            Assert.Equal(PackageSave.CdbHeader, BitConverter.ToInt32(cdb, 0));
            Assert.Equal(2, BitConverter.ToInt32(cdb, 0x98));
            Assert.Equal(0, cdb.Length % PackageSave.CdbAlign);
            for (int i = 0; i < 2; i++)
            {
                int e = PackageSave.CdbFirst + PackageSave.CdbEntry * i;
                int offset = BitConverter.ToInt32(cdb, e + 0x44), length = BitConverter.ToInt32(cdb, e + 0x48);
                Assert.Equal(0, offset % PackageSave.CdbAlign);
                Assert.Equal(i == 0 ? a : b, cdb.AsSpan(offset, length).ToArray());
            }
        }

        [Theory]
        [InlineData("mhost", true)]
        [InlineData("MMAP", true)]
        [InlineData("m1234567", false)] // 8 characters: the card path overflows ZDB_Mount's buffer
        [InlineData("host", false)]     // must start with m
        [InlineData("m-x", false)]
        public void Pack_names_follow_the_loader_rules(string name, bool valid)
        {
            var ex = Record.Exception(() => PackageSave.CheckPackName(name));
            Assert.Equal(valid, ex == null);
        }
    }
}
