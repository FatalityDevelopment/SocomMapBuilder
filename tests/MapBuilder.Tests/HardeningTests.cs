using System.Text.Json;
using MapBuilder.Server;
using MapBuilder.Tests.Fixtures;
using Xunit;

namespace MapBuilder.Tests
{
    /// <summary>
    /// Bad input is refused with a clear error and leaves the session as it was; dirty tracking follows the bytes on
    /// disk; other tools writing a package do not clash with an open session.
    /// </summary>
    public sealed class HardeningTests : IDisposable
    {
        private readonly SyntheticPackage _pkg = new();
        private readonly Backend _b = new();
        private readonly List<string> _sessions = new();

        public void Dispose()
        {
            foreach (string s in _sessions) _b.Close(s);
            _pkg.Dispose();
        }

        private string Open(string path)
        {
            string s = _b.Open(path);
            _sessions.Add(s);
            return s;
        }

        private static string[] Dirty(JsonElement status) =>
            status.GetProperty("dirty").EnumerateArray().Select(x => x.GetString()!).ToArray();

        public static IEnumerable<object[]> BadOps() => new[]
        {
            new object[] { new { op = "movePoint", name = "Alpha1", position = new[] { 1f, 2 } }, "position" },
            new object[] { new { op = "setPoint", name = "Alpha1", position = new[] { 1f } }, "position" },
            new object[] { new { op = "moveClutter", clutterIndex = 0, position = new[] { 1f, 2, 3, 4 } }, "position" },
            new object[] { new { op = "moveClutter", clutterIndex = 0, matrix = new[] { 1f, 0, 0 } }, "matrix" },
            new object[] { new { op = "moveClutter", clutterIndex = 0, position = new object[] { 1, "x", 3 } }, "numbers" },
            new object[] { new { op = "moveClutter", clutterIndex = 0, position = new[] { 1e39, 0, 0 } }, "out of range" },
            new object[] { new { op = "moveClutter", clutterIndex = 0, position = new[] { 0f, 0, 0 }, scale = 0f }, "scale" },
            new object[] { new { op = "moveClutter", clutterIndex = 0, position = new[] { 0f, 0, 0 }, scale = -2f }, "scale" },
            new object[] { new { op = "addClutter", model = "tree" }, "position" },
            new object[] { new { op = "deleteFaces", archive = "TST_MDL.ZED", model = "crate", faces = new Dictionary<string, int[]> { ["abc"] = new[] { 0 } } }, "geometry offsets" },
            new object[] { new { op = "transformVertices", archive = "TST_MDL.ZED", model = "crate", matrix = new[] { 1f, 2 } }, "matrix" },
            new object[] { new { op = "transformVertices", archive = "TST_MDL.ZED", model = "crate", translate = new[] { 1f, 2 } }, "translate" },
            new object[] { new { op = "transformVertices", archive = "TST_MDL.ZED", model = "crate", scale = 0 }, "scale" },
            new object[] { new { op = "replaceTexture", texture = "x.tif", imagePath = @"Z:\no\such\image.png" }, "" },
        };

        [Theory]
        [MemberData(nameof(BadOps))]
        public void Bad_input_is_an_error_and_changes_nothing(object op, string mentions)
        {
            string s = Open(_pkg.Folder);
            int revision = _b.Status(s).GetProperty("revision").GetInt32();
            string error = _b.EditFails(s, op);
            Assert.Contains(mentions, error);
            Assert.Equal(revision, _b.Status(s).GetProperty("revision").GetInt32());
            Assert.Empty(Dirty(_b.Status(s)));
        }

        [Fact]
        public void Labels_are_honoured_by_every_op()
        {
            string s = Open(_pkg.Folder);
            _b.Edit(s, new { op = "setPoint", name = "Alpha1", team = 3, label = "my label" });
            Assert.Equal("my label", _b.Status(s).GetProperty("canUndo").GetString());
        }

        // ------------------------------------------------------------------ dirty tracking

        [Fact]
        public void Undoing_back_to_the_file_makes_the_session_clean()
        {
            string s = Open(_pkg.Folder);
            _b.Edit(s, new { op = "moveClutter", clutterIndex = 0, position = new[] { 9f, 0, 9 } });
            Assert.Equal(new[] { "CLUTTER.ZAR" }, Dirty(_b.Status(s)));
            _b.Undo(s);
            Assert.Empty(Dirty(_b.Status(s)));
            _b.Redo(s);
            Assert.Equal(new[] { "CLUTTER.ZAR" }, Dirty(_b.Status(s)));
        }

        [Fact]
        public void Saving_over_the_source_makes_the_session_clean_and_keeps_it()
        {
            string zdb = _pkg.WriteZdb();
            string s = Open(zdb);
            _b.Edit(s, new { op = "moveClutter", clutterIndex = 0, position = new[] { 9f, 0, 9 } });
            _b.Ok(new CommandMessage { Command = "ASSET_SAVE", AssetSession = s });
            Assert.Empty(Dirty(_b.Status(s)));

            // Reopening the saved file joins the same session; undo then makes it dirty against the saved file.
            var again = _b.Ok(new CommandMessage { Command = "ASSET_OPEN", InputPath = zdb });
            Assert.Equal(s, again.GetProperty("status").GetProperty("session").GetString());
            Assert.True(again.GetProperty("reused").GetBoolean());
            _b.Undo(s);
            Assert.Equal(new[] { "CLUTTER.ZAR" }, Dirty(_b.Status(s)));
        }

        [Fact]
        public void Saving_a_copy_keeps_the_session_dirty()
        {
            string zdb = _pkg.WriteZdb();
            string s = Open(zdb);
            _b.Edit(s, new { op = "moveClutter", clutterIndex = 0, position = new[] { 9f, 0, 9 } });
            string copy = Path.Combine(Path.GetDirectoryName(zdb)!, "COPY.ZDB");
            _b.Ok(new CommandMessage { Command = "ASSET_SAVE", AssetSession = s, AssetJson = JsonSerializer.Serialize(new { outputPath = copy }) });
            Assert.Equal(new[] { "CLUTTER.ZAR" }, Dirty(_b.Status(s)));
        }

        // ------------------------------------------------------------------ sessions and other writers

        [Fact]
        public void A_clean_session_reloads_in_place_when_its_file_changes()
        {
            string zdb = _pkg.WriteZdb();
            string s = Open(zdb);
            int revision = _b.Status(s).GetProperty("revision").GetInt32();
            // Another tool rewrites the package: one clutter placement moved.
            string other = _b.Open(_pkg.Folder);
            _sessions.Add(other);
            _b.Edit(other, new { op = "moveClutter", clutterIndex = 0, position = new[] { 77f, 0, 0 } });
            byte[] moved = Backend.Bytes(other, "CLUTTER.ZAR");
            _b.Ok(new CommandMessage
            {
                Command = "PACKAGE_EDIT", InputPath = zdb,
                Data = JsonSerializer.Serialize(new object[] { new { op = "replace", path = @"RUN\MP\TST\CLUTTER.ZAR", file = WriteTemp(moved) } }),
            });
            File.SetLastWriteTimeUtc(zdb, DateTime.UtcNow.AddMinutes(1));

            var again = _b.Ok(new CommandMessage { Command = "ASSET_OPEN", InputPath = zdb });
            Assert.Equal(s, again.GetProperty("status").GetProperty("session").GetString());
            Assert.True(again.GetProperty("status").GetProperty("revision").GetInt32() > revision);
            Assert.Equal(moved, Backend.Bytes(s, "CLUTTER.ZAR"));
        }

        [Fact]
        public void Package_edit_refuses_a_file_with_unsaved_edits_in_a_session()
        {
            string zdb = _pkg.WriteZdb();
            string s = Open(zdb);
            _b.Edit(s, new { op = "moveClutter", clutterIndex = 0, position = new[] { 9f, 0, 9 } });
            string error = _b.Fails(new CommandMessage
            {
                Command = "PACKAGE_EDIT", InputPath = zdb,
                Data = JsonSerializer.Serialize(new object[] { new { op = "remove", path = @"RUN\MP\TST\AIMAPS.MPS" } }),
            });
            Assert.Contains("unsaved edits", error);
            Assert.Contains(s, error);
        }

        [Fact]
        public void Closing_twice_is_an_error_not_a_crash()
        {
            string s = _b.Open(_pkg.Folder);
            _b.Ok(new CommandMessage { Command = "ASSET_CLOSE", AssetSession = s });
            Assert.Contains("No asset session", _b.Fails(new CommandMessage { Command = "ASSET_CLOSE", AssetSession = s }));
        }

        [Fact]
        public void Concurrent_opens_share_one_session()
        {
            var ids = new System.Collections.Concurrent.ConcurrentBag<string>();
            Parallel.For(0, 8, _ => ids.Add(new Backend().Open(_pkg.Folder)));
            Assert.Single(ids.Distinct());
            _sessions.Add(ids.First());
        }

        private string WriteTemp(byte[] data)
        {
            string path = Path.Combine(Path.GetDirectoryName(_pkg.Folder)!, Guid.NewGuid().ToString("N") + ".ZAR");
            File.WriteAllBytes(path, data);
            return path;
        }
    }
}
