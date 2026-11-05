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
        public void Fixed_size_resource_edit_only_changes_its_payload_and_crc_in_the_whole_location()
        {
            using var mission = ZdbArchive.CreateNew(1);
            mission.AddResource("mission.rdr", new byte[] { 1, 2, 3, 4 }, "AIMm", 51);
            mission.AddResource("keep.rdr", Bytes(13, 8), "TEST", 1);
            // A retail dependency with size 2 points at the first two bytes of the first actual payload.
            mission.AddResource("other.ZDB", new byte[2], "DEP_", 1);
            byte[] v6 = mission.ToBytes();
            int body = BitConverter.ToInt32(v6, 0x0C);
            Assert.Equal(0, body % 16);
            v6[0x14] = 0x55;
            v6[0x1D] = 0xAA;
            v6[body + 7] = 0xBB; // padding after the edited resource
            // The table can be in a different order than the payloads.
            const int first = 0xC8, second = first + 0x58;
            byte[] header = v6.AsSpan(first, 0x58).ToArray();
            v6.AsSpan(second, 0x58).CopyTo(v6.AsSpan(first));
            header.CopyTo(v6, second);

            using var location = ZdbArchive.CreateNew(2);
            location.AddEntry(@"RUN\MAP\HEADER.RAW", Bytes(9, 7));
            location.AddEntry(@"RUN\MAP\SP.ZDB", v6);
            byte[] zar = location.ToBytes();
            int rawOffset = BitConverter.ToInt32(zar, 0xA0 + 0x44);
            int zdbOffset = BitConverter.ToInt32(zar, 0xA0 + 0x5C + 0x44);
            Assert.Equal(0x800, rawOffset);
            Assert.Equal(0x1000, zdbOffset);
            Assert.Equal(0, zar.Length % 0x800);
            zar[rawOffset + 9] = 0xCC; // nonzero sector padding between ZAR entries
            zar[^1] = 0xDD;

            using var outer = ZdbArchive.FromBytes(zar);
            using var inner = ZdbArchive.FromBytes(outer.GetEntry(1).Data);
            Assert.Equal("AIMm", inner.GetEntry(1).Tag);
            inner.ReplaceEntryData(1, new byte[] { 0xFA, 2, 3, 4 });
            outer.ReplaceEntryData(1, inner.ToBytes());
            byte[] expected = (byte[])zar.Clone();
            expected[zdbOffset + body] = 0xFA;
            BitConverter.GetBytes(0xE75B6A6Bu).CopyTo(expected, zdbOffset + second + 0x50);
            Assert.Equal(expected, outer.ToBytes());
        }

        [Fact]
        public void All_retail_CA_locations_and_embedded_v6_files_round_trip_byte_for_byte()
        {
            string? disc = Environment.GetEnvironmentVariable("MAPBUILDER_CA_DISC");
            if (string.IsNullOrEmpty(disc)) return;
            var archives = Directory.GetFiles(Path.Combine(disc, "RUN"), "*.ZAR");
            Assert.Equal(25, archives.Length);
            int files = 0;
            foreach (string path in archives)
            {
                byte[] source = File.ReadAllBytes(path);
                using var zar = ZdbArchive.FromBytes(source);
                for (int i = 0; i < zar.EntryCount; i++)
                {
                    var e = zar.GetEntry(i);
                    if (e.Data.Length < 8 || BitConverter.ToUInt32(e.Data) != 0xC8 || BitConverter.ToUInt32(e.Data, 4) != 0x58)
                        continue;
                    using var zdb = ZdbArchive.FromBytes(e.Data);
                    byte[] saved = zdb.ToBytes();
                    Assert.True(e.Data.AsSpan().SequenceEqual(saved), $"{path} / {e.FullPath}");
                    zar.ReplaceEntryData(i, saved);
                    files++;
                }
                Assert.True(source.AsSpan().SequenceEqual(zar.ToBytes()), path);
            }
            Assert.Equal(976, files);
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
