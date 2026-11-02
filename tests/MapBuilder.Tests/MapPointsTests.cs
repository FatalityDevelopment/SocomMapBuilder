using System.Text;
using CodeDesigner.Ps2.FileFormats.Zipper.MPS;
using Xunit;

namespace MapBuilder.Tests
{
    public sealed class MapPointsTests
    {
        // An MPS with one 100 x 100 AI grid (10 units a cell from 0, 0) and a two-point table after it.
        private static byte[] Mps()
        {
            var b = new byte[0x200];
            BitConverter.GetBytes(1u).CopyTo(b, 0x04);
            const int grid = 0x28;
            BitConverter.GetBytes(0x17u).CopyTo(b, grid);
            BitConverter.GetBytes(1000f).CopyTo(b, grid + 0x10);
            BitConverter.GetBytes(1000f).CopyTo(b, grid + 0x18);
            BitConverter.GetBytes(100u).CopyTo(b, grid + 0x1C);
            BitConverter.GetBytes(100u).CopyTo(b, grid + 0x20);
            BitConverter.GetBytes(10f).CopyTo(b, grid + 0x24);
            BitConverter.GetBytes(10f).CopyTo(b, grid + 0x28);
            Encoding.ASCII.GetBytes("Main Map").CopyTo(b, grid + 0x38);
            const int table = 0x100;
            BitConverter.GetBytes(2u).CopyTo(b, table);
            void Point(int i, string name, byte team, byte kind, int cx, int cz)
            {
                int r = table + 8 + i * 24;
                Encoding.ASCII.GetBytes(name).CopyTo(b, r);
                b[r + 0x10] = team;
                b[r + 0x11] = kind;
                BitConverter.GetBytes(((uint)cx << 6) | ((uint)cz << 19)).CopyTo(b, r + 0x14);
            }
            Point(0, "Alpha1", 1, 2, 3, 4);
            Point(1, "Bravo1", 2, 1, 5, 6);
            return b;
        }

        [Fact]
        public void Update_renames_and_retypes_in_place()
        {
            byte[] before = Mps();
            byte[] after = MapPoints.Update(before, "alpha1", "Charlie9", 7, "waypoint");

            Assert.Equal(before.Length, after.Length);
            var points = MapPoints.Parse(after);
            var p = Assert.Single(points, x => x.Name == "Charlie9");
            Assert.Equal(7, p.Team);
            Assert.Equal("waypoint", p.Kind);
            Assert.Equal((3, 4), (p.CellX, p.CellZ));
            Assert.Contains(points, x => x.Name == "Bravo1" && x.Kind == "waypoint" && x.Team == 2);
            Assert.Equal("Alpha1", MapPoints.Parse(before)[0].Name);
        }

        [Fact]
        public void Update_then_move_keeps_the_new_name()
        {
            byte[] data = MapPoints.Move(MapPoints.Update(Mps(), "Bravo1", "Delta2", null, null), "Delta2", 95, 15);
            var p = Assert.Single(MapPoints.Parse(data), x => x.Name == "Delta2");
            Assert.Equal((9, 1), (p.CellX, p.CellZ));
        }

        [Theory]
        [InlineData("Bravo1", null, null)]
        [InlineData("a", null, null)]
        [InlineData("has space", null, null)]
        [InlineData(null, 300, null)]
        [InlineData(null, null, "boss")]
        public void Update_rejects_bad_changes(string? newName, int? team, string? kind)
        {
            Assert.ThrowsAny<ArgumentException>(() => MapPoints.Update(Mps(), "Alpha1", newName, team, kind));
        }

        [Fact]
        public void Update_needs_a_known_point()
        {
            Assert.Throws<KeyNotFoundException>(() => MapPoints.Update(Mps(), "Nobody", "Other", null, null));
        }
    }
}
