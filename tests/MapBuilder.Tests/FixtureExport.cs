using MapBuilder.Tests.Fixtures;
using Xunit;

namespace MapBuilder.Tests
{
    /// <summary>
    /// Writes the synthetic package to MAPBUILDER_FIXTURE_OUT (a folder) so the app can open it without game files:
    /// <c>MAPBUILDER_FIXTURE_OUT=C:\temp\fixture dotnet test tests/MapBuilder.Tests --filter FixtureExport</c>, then open
    /// <c>C:\temp\fixture\TST</c>. Does nothing when the variable is not set.
    /// </summary>
    public sealed class FixtureExport
    {
        [Fact]
        public void Write_the_synthetic_package_when_asked()
        {
            string? outDir = Environment.GetEnvironmentVariable("MAPBUILDER_FIXTURE_OUT");
            if (string.IsNullOrWhiteSpace(outDir)) return;
            using var pkg = new SyntheticPackage();
            string target = Path.Combine(outDir, "TST");
            Directory.CreateDirectory(target);
            foreach (string file in Directory.GetFiles(pkg.Folder)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
            File.Copy(pkg.WriteZdb(), Path.Combine(outDir, "TST.ZDB"), overwrite: true);
        }
    }
}
