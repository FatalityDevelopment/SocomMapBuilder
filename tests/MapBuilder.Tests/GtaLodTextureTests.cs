using CodeDesigner.Ps2.FileFormats.Gta;
using Xunit;

namespace MapBuilder.Tests;

public sealed class GtaLodTextureTests
{
    [Fact]
    public void Retail_text_and_streamed_placements_choose_their_own_high_detail_material()
    {
        string? root = Environment.GetEnvironmentVariable("MAPBUILDER_GTA_DISC");
        if (string.IsNullOrEmpty(root)) return;
        var pkg = GtaPackage.Open(root);
        string Binding(string model, string raster)
        {
            var mesh = pkg.BuildMesh(pkg.FindModel(model, "GTA3.IMG")!);
            return Assert.Single(mesh.Chunks.Select(c => c.Texture).OfType<string>()
                .Where(t => t.EndsWith("/" + raster, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase));
        }

        // This raster name has conflicting versions elsewhere on the disc. One relationship comes
        // from a text INST row, the other from a binary stream row referencing its main IPL.
        string stadium = Binding("lodastadium", "ws_rooftarmac1"), ammo = Binding("lodunation_sfs", "ws_rooftarmac1");
        Assert.Equal("GTA3.IMG|vgnbball/ws_rooftarmac1", stadium, ignoreCase: true);
        Assert.Equal("GTA3.IMG|queensammo_sfs/ws_rooftarmac1", ammo, ignoreCase: true);
        var stadiumTexture = pkg.FindTexture(stadium)!;
        var ammoTexture = pkg.FindTexture(ammo)!;
        Assert.Equal((64, 64), (stadiumTexture.Width, stadiumTexture.Height));
        Assert.True(stadiumTexture.Rgba.AsSpan().SequenceEqual(ammoTexture.Rgba));
        Assert.Equal("GTA3.IMG|lodvegaswest3/vgnstadwal1_lod", Binding("lodastadium", "vgnstadwal1_lod"), ignoreCase: true);
    }
}
