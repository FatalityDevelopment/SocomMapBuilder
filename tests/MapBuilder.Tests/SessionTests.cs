using System.Text.Json;
using CodeDesigner.Ps2.FileFormats.Zipper.ZDB;
using MapBuilder.Server;
using MapBuilder.Tests.Fixtures;
using Xunit;

namespace MapBuilder.Tests
{
    /// <summary>Batches, undo/redo history, session sharing and saving.</summary>
    public sealed class SessionTests : IDisposable
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

        private static object Move(int index, float x) => new { op = "moveClutter", clutterIndex = index, position = new[] { x, 0, 0 } };

        // ------------------------------------------------------------------ batch

        [Fact]
        public void Batch_is_one_undo_step()
        {
            string s = Open(_pkg.Folder);
            byte[] clutter = Backend.Bytes(s, "CLUTTER.ZAR"), mps = Backend.Bytes(s, "AIMAPS.MPS");
            var r = _b.Edit(s, new
            {
                op = "batch", label = "two things",
                ops = new object[] { Move(0, 11), Move(1, 22), new { op = "movePoint", name = "Alpha1", position = new[] { 55f, 0, 75 } } },
            });
            Assert.Equal(3, r.GetProperty("results").GetArrayLength());
            Assert.Equal("two things", _b.Status(s).GetProperty("canUndo").GetString());

            _b.Undo(s);
            Assert.Equal(clutter, Backend.Bytes(s, "CLUTTER.ZAR"));
            Assert.Equal(mps, Backend.Bytes(s, "AIMAPS.MPS"));
            Assert.True(_b.Status(s).GetProperty("canUndo").ValueKind == JsonValueKind.Null);
        }

        [Fact]
        public void A_failing_batch_rolls_back_the_ops_before_it()
        {
            string s = Open(_pkg.Folder);
            _b.Edit(s, Move(0, 5));
            byte[] before = Backend.Bytes(s, "CLUTTER.ZAR");
            _b.EditFails(s, new { op = "batch", ops = new object[] { Move(0, 11), Move(1, 22), new { op = "deleteClutter", clutterIndex = 99 } } });
            Assert.Equal(before, Backend.Bytes(s, "CLUTTER.ZAR"));
            // The edit before the batch is still the one to undo.
            Assert.StartsWith("move tree", _b.Status(s).GetProperty("canUndo").GetString());
        }

        [Fact]
        public void A_new_edit_clears_redo()
        {
            string s = Open(_pkg.Folder);
            _b.Edit(s, Move(0, 5));
            _b.Undo(s);
            Assert.NotEqual(JsonValueKind.Null, _b.Status(s).GetProperty("canRedo").ValueKind);
            _b.Edit(s, Move(1, 6));
            Assert.Equal(JsonValueKind.Null, _b.Status(s).GetProperty("canRedo").ValueKind);
            Assert.Contains("Nothing to redo", _b.Fails(new CommandMessage { Command = "ASSET_REDO", AssetSession = s }));
        }

        [Fact]
        public void Undo_with_empty_history_fails()
        {
            string s = Open(_pkg.Folder);
            Assert.Contains("Nothing to undo", _b.Fails(new CommandMessage { Command = "ASSET_UNDO", AssetSession = s }));
        }

        // ------------------------------------------------------------------ sharing

        [Fact]
        public void Opening_the_same_package_joins_its_session()
        {
            string a = Open(_pkg.Folder);
            var again = _b.Ok(new CommandMessage { Command = "ASSET_OPEN", InputPath = _pkg.Folder });
            Assert.True(again.GetProperty("reused").GetBoolean());
            Assert.Equal(a, again.GetProperty("status").GetProperty("session").GetString());
        }

        [Fact]
        public void Opening_a_missing_path_fails()
        {
            Assert.Contains("not found", _b.Fails(new CommandMessage { Command = "ASSET_OPEN", InputPath = Path.Combine(_pkg.Folder, "nope", "X.ZDB") }));
        }

        // ------------------------------------------------------------------ save

        [Fact]
        public void Saving_a_zdb_writes_the_edit_and_keeps_a_backup()
        {
            string zdb = _pkg.WriteZdb();
            string s = Open(zdb);
            _b.Edit(s, Move(0, 42));
            byte[] edited = Backend.Bytes(s, "CLUTTER.ZAR");
            _b.Ok(new CommandMessage { Command = "ASSET_SAVE", AssetSession = s, AssetJson = "{\"mode\":\"zdb\"}" });

            Assert.True(File.Exists(zdb + ".bak"));
            using var back = ZdbArchive.Open(zdb);
            Assert.Equal(edited, back.FindEntry(@"RUN\MP\TST\CLUTTER.ZAR")!.Data);
        }

        [Fact]
        public void Saving_a_copy_leaves_the_source_alone()
        {
            string zdb = _pkg.WriteZdb();
            byte[] original = File.ReadAllBytes(zdb);
            string s = Open(zdb);
            _b.Edit(s, Move(1, 42));
            string copy = Path.Combine(Path.GetDirectoryName(zdb)!, "COPY.ZDB");
            _b.Ok(new CommandMessage { Command = "ASSET_SAVE", AssetSession = s, AssetJson = JsonSerializer.Serialize(new { mode = "zdb", outputPath = copy }) });
            Assert.Equal(original, File.ReadAllBytes(zdb));

            string reopened = Open(copy);
            var placed = _b.Scene(reopened).GetProperty("instances").EnumerateArray().Single(i => i.GetProperty("clutterIndex").ValueKind == JsonValueKind.Number && i.GetProperty("clutterIndex").GetInt32() == 1);
            Assert.Equal(42f, placed.GetProperty("position")[0].GetSingle());
        }

        [Fact]
        public void Saving_with_no_changes_fails()
        {
            string s = Open(_pkg.Folder);
            Assert.Contains("No changes", _b.Fails(new CommandMessage { Command = "ASSET_SAVE", AssetSession = s }));
        }

        [Fact]
        public void A_pack_holds_the_changed_archives()
        {
            string s = Open(_pkg.WriteZdb());
            _b.Edit(s, Move(0, 1));
            string dir = Path.Combine(Path.GetDirectoryName(_pkg.Folder)!, "packs");
            var r = _b.Ok(new CommandMessage { Command = "ASSET_SAVE", AssetSession = s, AssetJson = JsonSerializer.Serialize(new { mode = "pack", packName = "mtest", outputPath = dir }) });
            string cdb = r.GetProperty("written")[0].GetString()!;
            Assert.Equal("MTEST.CDB", Path.GetFileName(cdb));
            using var pack = ZdbArchive.Open(cdb);
            Assert.Equal(1, pack.EntryCount);
            Assert.NotNull(pack.FindEntry(@"RUN\MP\TST\CLUTTER.ZAR"));
        }

        [Fact]
        public void A_pack_name_must_follow_the_card_rules()
        {
            string s = Open(_pkg.Folder);
            _b.Edit(s, Move(0, 1));
            Assert.Contains("must start with 'm'", _b.Fails(new CommandMessage { Command = "ASSET_SAVE", AssetSession = s, AssetJson = "{\"mode\":\"pack\",\"packName\":\"xhost\"}" }));
        }
    }
}
