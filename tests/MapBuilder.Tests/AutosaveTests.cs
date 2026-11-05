using MapBuilder.Server.Services;
using CodeDesigner.Ps2.FileFormats.Zipper.Ca;
using CodeDesigner.Ps2.FileFormats.Zipper.ZDB;
using MapBuilder.Tests.Fixtures;
using Xunit;

namespace MapBuilder.Tests
{
    /// <summary>Autosave writes a recovery copy of a changed session, once per revision, and leaves the session unsaved.</summary>
    [Collection(MapPackageCollection.Name)]
    public sealed class AutosaveTests : IDisposable
    {
        private readonly Backend _b = new();
        private readonly List<string> _sessions = new();
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "mb-autosave-" + Guid.NewGuid().ToString("N"));
        private readonly string _was = ZipperAssetService.AutosaveFolder;

        public AutosaveTests() => ZipperAssetService.AutosaveFolder = _folder;

        public void Dispose()
        {
            foreach (string s in _sessions) _b.Close(s);
            ZipperAssetService.AutosaveFolder = _was;
            if (Directory.Exists(_folder)) Directory.Delete(_folder, true);
        }

        [Fact]
        public void Same_named_packages_in_different_folders_get_their_own_recovery_copy()
        {
            string a = ZipperAssetService.AutosavePath(Path.Combine(_folder, "one", "M51.ZDB"), isZdb: true);
            string b = ZipperAssetService.AutosavePath(Path.Combine(_folder, "two", "M51.ZDB"), isZdb: true);
            Assert.NotEqual(a, b);
            Assert.Equal(a, ZipperAssetService.AutosavePath(Path.Combine(_folder, "one", "M51.ZDB"), isZdb: true));
            Assert.Matches(@"M51-[0-9a-f]{8}\.autosave\.ZDB$", a);
            Assert.EndsWith(".autosave.CDB", ZipperAssetService.AutosavePath(Path.Combine(_folder, "MEDIT.CDB"), isZdb: true));
            // A loose folder's copy is a folder of files, so it is not named like an archive.
            Assert.Matches(@"TST-[0-9a-f]{8}\.autosave$", ZipperAssetService.AutosavePath(Path.Combine(_folder, "TST"), isZdb: false));
        }

        [Fact]
        public void A_CA_recovery_archive_reopens_with_the_selected_missions_edits()
        {
            Directory.CreateDirectory(_folder);
            var mission = CaMissionsTests.EditableMission(_folder);
            using var outer = ZdbArchive.CreateNew(2);
            outer.AddEntry(@"RUN\COMPOUND\SP414.ZDB", File.ReadAllBytes(mission.SourcePath));
            string source = Path.Combine(_folder, "9EA6A33E.ZAR");
            outer.Save(source);
            string session = _b.Open(source);
            _sessions.Add(session);
            _b.Edit(session, new { op = "setPoint", name = "s_Able1", newName = "Recovery" });
            string copy = ZipperAssetService.AutosavePath(source, true);
            Assert.EndsWith(".ZAR", copy);
            Assert.Contains(copy, ZipperAssetService.AutosaveNow());
            Assert.Equal("Recovery", CaPackage.Open(copy).MissionPoints("SP414")[0].Name);
            Assert.DoesNotContain(copy, ZipperAssetService.AutosaveNow());
            Assert.Equal(copy, _b.Status(session).GetProperty("autosave").GetProperty("path").GetString());
            _b.Undo(session);
            Assert.Empty(_b.Status(session).GetProperty("dirty").EnumerateArray());
            Assert.Equal("s_Able1", CaPackage.Open(source).MissionPoints("SP414")[0].Name);
        }

        [Fact]
        public void A_changed_session_gets_one_recovery_copy_per_revision_and_stays_unsaved()
        {
            using var pkg = new SyntheticPackage();
            string zdb = pkg.WriteZdb();
            string s = _b.Open(zdb);
            _sessions.Add(s);
            // AutosaveNow covers every open session, and tests in other classes run alongside with their own
            // packages (also named TST), so look only for this package's copy.
            string mine = ZipperAssetService.AutosavePath(zdb, isZdb: true);
            Assert.DoesNotContain(mine, ZipperAssetService.AutosaveNow());   // nothing changed yet

            _b.Edit(s, new { op = "addClutter", model = "tree", position = new[] { 1f, 2f, 3f } });
            Assert.Contains(mine, ZipperAssetService.AutosaveNow());
            string copy = mine;
            Assert.StartsWith(_folder, copy);
            Assert.EndsWith(".autosave.ZDB", copy);
            Assert.True(File.Exists(copy), "the recovery copy is a file");
            Assert.True(new FileInfo(copy).Length > 0);
            Assert.DoesNotContain(mine, ZipperAssetService.AutosaveNow());   // same revision: not again

            var status = _b.Ok(new MapBuilder.Server.CommandMessage { Command = "ASSET_STATUS", AssetSession = s });
            Assert.NotEmpty(status.GetProperty("dirty").EnumerateArray());                            // still unsaved
            Assert.Equal(copy, status.GetProperty("autosave").GetProperty("path").GetString());
        }
    }
}
