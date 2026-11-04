using MapBuilder.Server.Services;
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
        public void A_changed_session_gets_one_recovery_copy_per_revision_and_stays_unsaved()
        {
            using var pkg = new SyntheticPackage();
            string zdb = pkg.WriteZdb();
            string s = _b.Open(zdb);
            _sessions.Add(s);
            Assert.DoesNotContain(ZipperAssetService.AutosaveNow(), p => p.StartsWith(_folder));   // nothing changed yet

            _b.Edit(s, new { op = "addClutter", model = "tree", position = new[] { 1f, 2f, 3f } });
            var written = ZipperAssetService.AutosaveNow().Where(p => p.StartsWith(_folder)).ToList();
            string copy = Assert.Single(written);
            Assert.EndsWith(".autosave.ZDB", copy);
            Assert.True(new FileInfo(copy).Length > 0);
            Assert.DoesNotContain(ZipperAssetService.AutosaveNow(), p => p.StartsWith(_folder));   // same revision: not again

            var status = _b.Ok(new MapBuilder.Server.CommandMessage { Command = "ASSET_STATUS", AssetSession = s });
            Assert.NotEmpty(status.GetProperty("dirty").EnumerateArray());                            // still unsaved
            Assert.Equal(copy, status.GetProperty("autosave").GetProperty("path").GetString());
        }
    }
}
