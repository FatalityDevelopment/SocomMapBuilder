using System.Text.Json;
using MapBuilder.Server;
using MapBuilder.Tests.Fixtures;
using Xunit;

namespace MapBuilder.Tests
{
    /// <summary>
    /// The map edit ops on a synthetic package: each changes what it says, and undo puts back the exact bytes
    /// (redo the edited ones).
    /// </summary>
    public sealed class EditOpsTests : IDisposable
    {
        private readonly SyntheticPackage _pkg = new();
        private readonly Backend _b = new();
        private readonly string _s;

        public EditOpsTests() => _s = _b.Open(_pkg.Folder);

        public void Dispose()
        {
            _b.Close(_s);
            _pkg.Dispose();
        }

        private List<JsonElement> Instances(string kind) =>
            _b.Scene(_s).GetProperty("instances").EnumerateArray().Where(i => i.GetProperty("kind").GetString() == kind).ToList();

        private static float[] Position(JsonElement instance) =>
            instance.GetProperty("position").EnumerateArray().Select(x => x.GetSingle()).ToArray();

        private int GeoIndex(string instance) =>
            Instances("reference").Single(i => i.GetProperty("instance").GetString() == instance).GetProperty("geoIndex").GetInt32();

        /// <summary>Runs an edit, checks undo restores <paramref name="leaf"/> byte for byte and redo reapplies it.</summary>
        private JsonElement RoundTrip(string leaf, object op)
        {
            byte[] before = Backend.Bytes(_s, leaf);
            var result = _b.Edit(_s, op);
            byte[] after = Backend.Bytes(_s, leaf);
            Assert.NotEqual(before, after);
            _b.Undo(_s);
            Assert.Equal(before, Backend.Bytes(_s, leaf));
            _b.Redo(_s);
            Assert.Equal(after, Backend.Bytes(_s, leaf));
            return result;
        }

        [Fact]
        public void Fixture_opens_as_a_map()
        {
            var scene = _b.Scene(_s);
            Assert.Equal("tst", scene.GetProperty("sceneNamespace").GetString());
            Assert.Equal(2, Instances("reference").Count);
            Assert.Equal(2, Instances("clutter").Count);
            Assert.Equal(2, _b.Points(_s).GetProperty("points").GetArrayLength());
        }

        [Fact]
        public void MoveClutter_moves_one_placement()
        {
            RoundTrip("CLUTTER.ZAR", new { op = "moveClutter", clutterIndex = 1, position = new[] { 50f, 1, 60 } });
            var moved = Instances("clutter").Single(i => i.GetProperty("clutterIndex").GetInt32() == 1);
            Assert.Equal(new[] { 50f, 1, 60 }, Position(moved));
            Assert.Equal(new[] { 1f, 0, 2 }, Position(Instances("clutter").Single(i => i.GetProperty("clutterIndex").GetInt32() == 0)));
        }

        [Fact]
        public void MoveClutter_rejects_an_index_out_of_range()
        {
            Assert.Contains("out of range", _b.EditFails(_s, new { op = "moveClutter", clutterIndex = 9, position = new[] { 0f, 0, 0 } }));
        }

        [Fact]
        public void AddClutter_adds_a_placement_and_answers_its_index()
        {
            var r = RoundTrip("CLUTTER.ZAR", new { op = "addClutter", model = "tree", position = new[] { 7f, 0, 8 } });
            int index = r.GetProperty("newClutterIndex").GetInt32();
            var all = Instances("clutter");
            Assert.Equal(3, all.Count);
            Assert.Equal(new[] { 7f, 0, 8 }, Position(all.Single(i => i.GetProperty("clutterIndex").GetInt32() == index)));
        }

        [Fact]
        public void AddClutter_rejects_an_unknown_model()
        {
            Assert.Contains("No model", _b.EditFails(_s, new { op = "addClutter", model = "nosuch", position = new[] { 0f, 0, 0 } }));
        }

        [Fact]
        public void DeleteClutter_removes_a_placement()
        {
            RoundTrip("CLUTTER.ZAR", new { op = "deleteClutter", clutterIndex = 0 });
            var left = Instances("clutter");
            Assert.Single(left);
            Assert.Equal(new[] { 3f, 0, 4 }, Position(left[0]));
        }

        [Fact]
        public void MoveInstance_sets_the_world_position()
        {
            RoundTrip("TST_GEO.ZED", new { op = "moveInstance", geoIndex = GeoIndex("crate_1"), position = new[] { 100f, 2, 200 } });
            Assert.Equal(new[] { 100f, 2, 200 }, Position(Instances("reference").Single(i => i.GetProperty("instance").GetString() == "crate_1")));
        }

        [Fact]
        public void MoveInstance_with_yaw_and_scale()
        {
            _b.Edit(_s, new { op = "moveInstance", geoIndex = GeoIndex("crate_2"), position = new[] { 1f, 2, 3 }, yaw = 90f, scale = 2f });
            var moved = Instances("reference").Single(i => i.GetProperty("instance").GetString() == "crate_2");
            Assert.Equal(2f, moved.GetProperty("scale").GetSingle(), 3);
        }

        [Fact]
        public void MoveInstance_rejects_a_record_that_is_not_a_scene_node()
        {
            // Record 0 is the "models" container.
            Assert.Contains("not a scene node", _b.EditFails(_s, new { op = "moveInstance", geoIndex = 0, position = new[] { 0f, 0, 0 } }));
        }

        [Fact]
        public void DuplicateInstance_adds_a_sibling_copy()
        {
            var r = RoundTrip("TST_GEO.ZED", new { op = "duplicateInstance", geoIndex = GeoIndex("crate_1"), position = new[] { 5f, 0, 5 }, name = "crate_3" });
            Assert.True(r.GetProperty("newGeoIndex").GetInt32() > 0);
            var refs = Instances("reference");
            Assert.Equal(3, refs.Count);
            Assert.Equal(new[] { 5f, 0, 5 }, Position(refs.Single(i => i.GetProperty("instance").GetString() == "crate_3")));
            Assert.Equal(new[] { 10f, 0, 20 }, Position(refs.Single(i => i.GetProperty("instance").GetString() == "crate_1")));
        }

        [Fact]
        public void PlaceInstance_copies_the_nearest_reference()
        {
            var r = RoundTrip("TST_GEO.ZED", new { op = "placeInstance", model = "tree", position = new[] { -29f, 0, 6 }, name = "tree_1" });
            Assert.Equal("crate_2", r.GetProperty("template").GetProperty("name").GetString());
            var placed = Instances("reference").Single(i => i.GetProperty("instance").GetString() == "tree_1");
            Assert.Equal("tree", placed.GetProperty("model").GetString());
            Assert.Equal(new[] { -29f, 0, 6 }, Position(placed));
        }

        [Fact]
        public void PlaceInstance_requires_a_position()
        {
            Assert.Contains("position", _b.EditFails(_s, new { op = "placeInstance", model = "tree" }));
        }

        [Fact]
        public void DeleteInstance_removes_a_reference_but_never_the_worldmodel()
        {
            RoundTrip("TST_GEO.ZED", new { op = "deleteInstance", geoIndex = GeoIndex("crate_1") });
            Assert.Single(Instances("reference"));
            Assert.Contains("worldmodel", _b.EditFails(_s, new { op = "deleteInstance", geoIndex = 1 }));
        }

        [Fact]
        public void MovePoint_moves_to_the_cell_under_the_position()
        {
            RoundTrip("AIMAPS.MPS", new { op = "movePoint", name = "Alpha1", position = new[] { 55f, 0, 75 } });
            var p = _b.Points(_s).GetProperty("points").EnumerateArray().Single(x => x.GetProperty("name").GetString() == "Alpha1");
            Assert.Equal(5, p.GetProperty("cellX").GetInt32());
            Assert.Equal(7, p.GetProperty("cellZ").GetInt32());
        }

        [Fact]
        public void SetPoint_renames_retypes_and_moves()
        {
            RoundTrip("AIMAPS.MPS", new { op = "setPoint", name = "Bravo1", newName = "Bravo9", team = 1, position = new[] { 15f, 0, 25 } });
            var p = _b.Points(_s).GetProperty("points").EnumerateArray().Single(x => x.GetProperty("name").GetString() == "Bravo9");
            Assert.Equal(1, p.GetProperty("team").GetInt32());
            Assert.Equal(1, p.GetProperty("cellX").GetInt32());
            Assert.Equal(2, p.GetProperty("cellZ").GetInt32());
        }

        [Fact]
        public void SetPoint_with_nothing_to_change_fails()
        {
            Assert.Contains("Nothing to change", _b.EditFails(_s, new { op = "setPoint", name = "Alpha1" }));
        }

        [Fact]
        public void Edits_bump_the_revision_and_name_the_undo_step()
        {
            int r0 = _b.Status(_s).GetProperty("revision").GetInt32();
            _b.Edit(_s, new { op = "moveClutter", clutterIndex = 0, position = new[] { 9f, 9, 9 }, label = "nudge tree" });
            var st = _b.Status(_s);
            Assert.Equal(r0 + 1, st.GetProperty("revision").GetInt32());
            Assert.Equal("nudge tree", st.GetProperty("canUndo").GetString());
            Assert.Contains("CLUTTER.ZAR", st.GetProperty("dirty").EnumerateArray().Select(x => x.GetString()));
        }

        [Fact]
        public void Unknown_op_and_missing_session_fail_cleanly()
        {
            Assert.Contains("Unknown", _b.EditFails(_s, new { op = "noSuchOp" }), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("No asset session", _b.Fails(new CommandMessage { Command = "ASSET_EDIT", AssetSession = "nope", AssetJson = "{\"op\":\"batch\",\"ops\":[]}" }));
        }
    }
}
