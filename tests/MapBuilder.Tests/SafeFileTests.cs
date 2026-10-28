using CodeDesigner.Ps2.FileFormats;
using Xunit;

namespace MapBuilder.Tests
{
    public sealed class SafeFileTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "mapbuilder-tests-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }

        [Fact]
        public void New_file_is_written_without_backup_or_temp()
        {
            string path = Path.Combine(_dir, "sub", "NEW.ZDB");
            SafeFile.Write(path, new byte[] { 1, 2, 3 });

            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
            Assert.False(File.Exists(path + ".bak"));
            Assert.False(File.Exists(path + ".tmp"));
        }

        [Fact]
        public void Backup_keeps_the_original_across_saves()
        {
            Directory.CreateDirectory(_dir);
            string path = Path.Combine(_dir, "M99.ZDB");
            File.WriteAllBytes(path, new byte[] { 9 });

            SafeFile.Write(path, new byte[] { 1 });
            SafeFile.Write(path, new byte[] { 2 });

            Assert.Equal(new byte[] { 2 }, File.ReadAllBytes(path));
            Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(path + ".bak"));
            Assert.False(File.Exists(path + ".tmp"));
        }

        [Fact]
        public void No_backup_when_asked()
        {
            Directory.CreateDirectory(_dir);
            string path = Path.Combine(_dir, "MPACK.CDB");
            File.WriteAllBytes(path, new byte[] { 9 });

            SafeFile.Write(path, new byte[] { 1 }, backup: false);

            Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(path));
            Assert.False(File.Exists(path + ".bak"));
        }

        [Fact]
        public void A_failed_write_leaves_the_target_alone()
        {
            Directory.CreateDirectory(_dir);
            string path = Path.Combine(_dir, "LOCKED.ZDB");
            File.WriteAllBytes(path, new byte[] { 7 });
            // Hold the temp name open so the write fails before the target is touched.
            using (new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
            {
                Assert.ThrowsAny<IOException>(() => SafeFile.Write(path, new byte[] { 1 }, backup: false));
            }
            Assert.Equal(new byte[] { 7 }, File.ReadAllBytes(path));
        }
    }
}
