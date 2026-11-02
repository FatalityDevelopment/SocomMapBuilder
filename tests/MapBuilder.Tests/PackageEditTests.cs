using System.Text.Json;
using CodeDesigner.Ps2.FileFormats.Zipper.ZDB;
using MapBuilder.Server;
using MapBuilder.Tests.Fixtures;
using Xunit;

namespace MapBuilder.Tests
{
    /// <summary>PACKAGE_EDIT: package files changed on disk, without a session.</summary>
    public sealed class PackageEditTests : IDisposable
    {
        private readonly SyntheticPackage _pkg = new();
        private readonly Backend _b = new();
        private readonly string _zdb;
        private readonly string _out;

        public PackageEditTests()
        {
            _zdb = _pkg.WriteZdb();
            _out = Path.Combine(Path.GetDirectoryName(_zdb)!, "OUT.ZDB");
        }

        public void Dispose() => _pkg.Dispose();

        private JsonElement Run(params object[] ops) =>
            _b.Ok(new CommandMessage { Command = "PACKAGE_EDIT", InputPath = _zdb, OutputPath = _out, Data = JsonSerializer.Serialize(ops) });

        private string RunFails(params object[] ops) =>
            _b.Fails(new CommandMessage { Command = "PACKAGE_EDIT", InputPath = _zdb, OutputPath = _out, Data = JsonSerializer.Serialize(ops) });

        private static string[] Paths(string zdb)
        {
            using var z = ZdbArchive.Open(zdb);
            return z.Entries.Select(e => e.FullPath).ToArray();
        }

        [Fact]
        public void Remove_and_add_a_root_file()
        {
            string mps = Path.Combine(_pkg.Folder, "AIMAPS.MPS");
            Run(new { op = "remove", path = @"RUN\MP\TST\AIMAPS.MPS" });
            Assert.DoesNotContain(@"RUN\MP\TST\AIMAPS.MPS", Paths(_out), StringComparer.OrdinalIgnoreCase);

            var r = _b.Ok(new CommandMessage
            {
                Command = "PACKAGE_EDIT", InputPath = _out, OutputPath = _out,
                Data = JsonSerializer.Serialize(new object[] { new { op = "add", path = @"RUN\MP\TST\AIMAPS.MPS", file = mps } }),
            });
            Assert.Single(r.GetProperty("added").EnumerateArray());
            using var z = ZdbArchive.Open(_out);
            Assert.Equal(File.ReadAllBytes(mps), z.FindEntry(@"RUN\MP\TST\AIMAPS.MPS")!.Data);
        }

        [Fact]
        public void Replace_swaps_the_bytes_and_leaves_the_source_alone()
        {
            byte[] source = File.ReadAllBytes(_zdb);
            string mps = Path.Combine(_pkg.Folder, "ALT.MPS");
            byte[] alt = MapPointsTests.Mps();
            alt[0x100 + 8] = (byte)'Z';
            File.WriteAllBytes(mps, alt);
            Run(new { op = "replace", path = @"RUN\MP\TST\AIMAPS.MPS", file = mps });
            Assert.Equal(source, File.ReadAllBytes(_zdb));
            using var z = ZdbArchive.Open(_out);
            Assert.Equal(alt, z.FindEntry(@"RUN\MP\TST\AIMAPS.MPS")!.Data);
        }

        [Fact]
        public void Add_refuses_a_file_already_there_and_files_a_package_cannot_hold()
        {
            string mps = Path.Combine(_pkg.Folder, "AIMAPS.MPS");
            Assert.Contains("already in the package", RunFails(new { op = "add", path = @"RUN\MP\TST\AIMAPS.MPS", file = mps }));
            Assert.Contains("a package holds", RunFails(new { op = "add", path = @"RUN\MP\TST\README.TXT", file = mps }));
            Assert.False(File.Exists(_out));
        }

        [Fact]
        public void Merge_brings_in_another_packages_files()
        {
            string other = Path.Combine(Path.GetDirectoryName(_zdb)!, "OTHER.ZDB");
            using (var z = ZdbArchive.CreateNew(2))
            {
                z.AddEntry(@"RUN\MP\TST\EXTRA.MPS", MapPointsTests.Mps());
                File.WriteAllBytes(other, z.ToBytes());
            }
            var r = Run(new { op = "merge", file = other });
            Assert.Contains(@"RUN\MP\TST\EXTRA.MPS", r.GetProperty("added").EnumerateArray().Select(x => x.GetString()));
            Assert.Contains(@"RUN\MP\TST\EXTRA.MPS", Paths(_out), StringComparer.OrdinalIgnoreCase);
        }

        [Fact]
        public void Unknown_ops_fail()
        {
            Assert.Contains("Unknown operation", RunFails(new { op = "explode" }));
        }
    }
}
