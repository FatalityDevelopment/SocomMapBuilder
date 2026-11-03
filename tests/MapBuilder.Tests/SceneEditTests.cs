using CodeDesigner.Ps2.FileFormats.Zipper.Anim;
using CodeDesigner.Ps2.FileFormats.Zipper.Texture;
using CodeDesigner.Ps2.FileFormats.Zipper.ZDB;
using CodeDesigner.Ps2.FileFormats.Zipper.ZED;
using Xunit;

namespace MapBuilder.Tests
{
    /// <summary>
    /// The Asset Viewer's ZANIM and palette editors on a disc (MAPBUILDER_DISC, e.g. E:\): every decoded field writes
    /// back to the same bytes, a change touches only its own bytes, and every palette re-encodes as stored. Without
    /// the variable there is nothing to check.
    /// </summary>
    public sealed class SceneEditTests
    {
        private static string? Disc => Environment.GetEnvironmentVariable("MAPBUILDER_DISC") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

        // The disc's ZANIM archives: the loose ones in RUN and each package's CZANIM / MZANIM.
        private static IEnumerable<(string, byte[])> Zanims(string disc)
        {
            string run = Path.Combine(disc, "RUN");
            foreach (string f in Directory.GetFiles(run, "*ZANIM.ZAR")) yield return (f, File.ReadAllBytes(f));
            foreach (string zdb in Directory.GetFiles(run, "*.ZDB").Take(6))
            {
                using var z = ZdbArchive.Open(zdb);
                foreach (var e in z.Entries.Where(e => e.FullPath.EndsWith("ZANIM.ZAR", StringComparison.OrdinalIgnoreCase)))
                    yield return (zdb + "|" + e.FullPath, e.Data);
            }
        }

        [Fact]
        public void Every_zanim_field_writes_back_as_stored_and_a_change_stays_in_its_bytes()
        {
            if (Disc is not { } disc) return;
            int fields = 0;
            foreach (var (name, data) in Zanims(disc))
            {
                ZanimFile file;
                try { file = ZanimFile.Parse(data); } catch (InvalidDataException) { continue; }
                foreach (var a in file.Animations)
                    foreach (var i in a.Tracks.SelectMany(t => t.Instructions))
                    {
                        foreach (var f in ZanimFile.Layout(i.Op, a.Sequence, i.Offset, i.Words.Length * 4).Where(f => !f.ReadOnly))
                        {
                            object raw = ZanimFile.RawValue(a.Sequence, i.Offset, f);
                            object back = raw switch { float x => (double)x, int n => (double)n, float[] v => v.Select(x => (double)x).ToArray(), _ => raw };
                            // NaN floats cannot be written; nothing on the disc should hold one where a number is read.
                            if (back is double d && !double.IsFinite(d) || back is double[] ds && !ds.All(double.IsFinite)) continue;
                            Assert.True(a.Sequence.SequenceEqual(ZanimFile.WithField(a.Sequence, i.Offset, f.Name, back)), $"{name} {a.Name} {i.Name}.{f.Name} did not write back as stored");
                            fields++;
                        }
                        for (int w = 1; w < i.Words.Length; w++)
                            Assert.True(a.Sequence.SequenceEqual(ZanimFile.WithWord(a.Sequence, i.Offset, w, i.Words[w])));
                    }
                // A changed wait: only its four bytes differ.
                var wait = file.Animations.SelectMany(a => a.Tracks.SelectMany(t => t.Instructions).Where(i => i.Fields.ContainsKey("seconds")).Select(i => (a, i))).FirstOrDefault();
                if (wait.a != null)
                {
                    byte[] changed = ZanimFile.WithField(wait.a.Sequence, wait.i.Offset, "seconds", 1.25);
                    Assert.InRange(wait.a.Sequence.Zip(changed).Count(p => p.First != p.Second), 1, 4);
                    Assert.Equal(1.25f, ZanimFile.RawValue(changed, wait.i.Offset, ZanimFile.Layout(wait.i.Op, changed, wait.i.Offset, wait.i.Words.Length * 4).First(f => f.Name == "seconds")));
                }
            }
            Assert.True(fields > 1000, $"only {fields} fields checked");
        }

        [Fact]
        public void Every_palette_in_a_package_reencodes_as_stored()
        {
            if (Disc is not { } disc) return;
            int checkedPalettes = 0;
            using var z = ZdbArchive.Open(Path.Combine(disc, "RUN", "M51.ZDB"));
            foreach (var e in z.Entries.Where(e => e.FullPath.EndsWith("_PAL.ZED", StringComparison.OrdinalIgnoreCase)))
            {
                var nodes = ZedContainer.FromBytes(e.Data).Nodes;
                for (int i = 0; i < nodes.Count; i++)
                {
                    if (nodes[i].Name != "buf" || nodes[i].Data.Length < 512) continue;
                    byte[] buf = nodes[i].Data;
                    bool is16 = buf.Length < 1024;
                    var pal = ZedTextureFile.DecodeClutBuffer(buf, 0);
                    byte[] again = ZedTextureFile.EncodeClutBuffer(pal.Rgba, is16);
                    Assert.True(buf.Take(again.Length).SequenceEqual(again), $"{e.FullPath} palette at record {i} ({(is16 ? 16 : 32)}-bit) changed on re-encoding");
                    checkedPalettes++;
                }
            }
            Assert.True(checkedPalettes > 20, $"only {checkedPalettes} palettes checked");
        }
    }
}
