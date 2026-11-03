using System.Text;
using CodeDesigner.Ps2.FileFormats.Zipper.MPS;
using CodeDesigner.Ps2.FileFormats.Zipper.ZDB;
using Xunit;

namespace MapBuilder.Tests
{
    public sealed class MapPointsTests
    {
        // An MPS with one 100 x 100 AI grid (10 units a cell from 0, 0) and a two-point table after it.
        internal static byte[] Mps()
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
        [InlineData(" leading", null, null)]
        [InlineData("trailing ", null, null)]
        [InlineData(null, 300, null)]
        [InlineData(null, null, "boss")]
        public void Update_rejects_bad_changes(string? newName, int? team, string? kind)
        {
            Assert.ThrowsAny<ArgumentException>(() => MapPoints.Update(Mps(), "Alpha1", newName, team, kind));
        }

        [Fact]
        public void Names_with_spaces_parse_and_can_be_given()
        {
            // M51's main table names points "Demo Site 1" and "Sniper Rifle"; one such name used to hide the whole table.
            byte[] data = MapPoints.Update(Mps(), "Bravo1", "Demo Site 1", null, null);
            var points = MapPoints.Parse(data);
            Assert.Equal(2, points.Count);
            Assert.Contains(points, p => p.Name == "Demo Site 1" && p.Kind == "waypoint");
            Assert.Equal((5, 6), (MapPoints.Parse(MapPoints.Move(data, "Demo Site 1", 55, 65)).Single(p => p.Name == "Demo Site 1") is var q ? (q.CellX, q.CellZ) : default));
        }

        [Fact]
        public void A_grid_without_cells_gives_no_height()
        {
            Assert.All(MapPoints.Parse(Mps()), p => Assert.Null(p.Y));
        }

        /// <summary>On a disc (MAPBUILDER_DISC): every map's grid rows add up, point heights lie in the grid's range, and
        /// MP1's match the floors measured in the Map Builder (terrain, and floors where buildings stand in it).</summary>
        [Fact]
        public void Disc_points_take_their_floor_from_the_ai_grid()
        {
            string? disc = Environment.GetEnvironmentVariable("MAPBUILDER_DISC");
            if (string.IsNullOrEmpty(disc) || !Directory.Exists(Path.Combine(disc, "RUN"))) return;
            int maps = 0;
            foreach (string zdb in Directory.GetFiles(Path.Combine(disc, "RUN"), "*.ZDB").Where(f => !Path.GetFileName(f).StartsWith("APACHE")))
            {
                using var z = ZdbArchive.Open(zdb);
                byte[] mps = z.Entries.First(e => e.FullPath.EndsWith("AIMAPS.MPS", StringComparison.OrdinalIgnoreCase)).Data;
                var grid = MpsFile.Parse(mps).Maps[0];
                var points = MapPoints.Parse(mps, grid);
                Assert.True(points.Count(p => p.Y == null) <= 1, $"{zdb}: {points.Count(p => p.Y == null)} points without a floor");
                Assert.All(points.Where(p => p.Y != null), p => Assert.InRange(p.Y!.Value, grid.MinY - 0.01f, grid.MaxY + 0.01f));
                if (Path.GetFileName(zdb).Equals("MP1.ZDB", StringComparison.OrdinalIgnoreCase))
                {
                    var y = points.ToDictionary(p => p.Name, p => p.Y);
                    Assert.Equal(71.2f, y["StartBomb"]!.Value, 0.2f);  // terrain
                    Assert.Equal(169.2f, y["Juliet"]!.Value, 0.2f);    // terrain
                    Assert.Equal(122.2f, y["startPlayer"]!.Value, 0.2f); // the outbuilding's floor (no terrain under it)
                    Assert.Equal(111.8f, y["spectator"]!.Value, 0.2f);   // the dredge's deck
                }
                maps++;
            }
            Assert.True(maps >= 30, $"only {maps} maps");
        }

        [Fact]
        public void Update_needs_a_known_point()
        {
            Assert.Throws<KeyNotFoundException>(() => MapPoints.Update(Mps(), "Nobody", "Other", null, null));
        }
    }
}
