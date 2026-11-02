using System;

namespace CodeDesigner.Ps2.FileFormats.Ps2
{
    /// <summary>
    /// Headerless frame-buffer images (RUN/RAW/*.RAW loading screens, NETGUI/*.RGB backdrops): RGBX or RGB pixels,
    /// top row first, at a screen width and pixel size the file does not record. The layout is the one in which the
    /// picture is smoothest: least change between neighbouring pixels, down and across (a wrong width tears rows
    /// apart; a wrong pixel size, at the same row stride, mixes channels along them).
    /// </summary>
    public static class RawScreen
    {
        private static readonly int[] Widths = { 640, 512, 480, 448, 320, 256, 128 };

        public sealed record Guess(int Width, int Height, int BytesPerPixel);

        public static Guess? GuessLayout(ReadOnlySpan<byte> data)
        {
            Guess? best = null;
            double bestScore = double.MaxValue;
            foreach (int bpp in new[] { 4, 3 })
            {
                if (data.Length % bpp != 0) continue;
                int pixels = data.Length / bpp;
                foreach (int w in Widths)
                {
                    if (pixels % w != 0 || pixels / w < 32) continue;
                    double score = RowScore(data, w, pixels / w, bpp);
                    // The PS2's own 640-wide screens win near-ties (flat backdrops line up at any width).
                    if (w == 640) score *= 0.8;
                    if (score < bestScore) { bestScore = score; best = new Guess(w, pixels / w, bpp); }
                }
            }
            return best;
        }

        private static double RowScore(ReadOnlySpan<byte> d, int w, int h, int bpp)
        {
            long change = 0, samples = 0;
            int stride = w * bpp;
            for (int y = 0; y < h - 1; y += Math.Max(1, h / 128))
                for (int x = 0; x < w - 1; x += Math.Max(1, w / 128))
                {
                    int i = y * stride + x * bpp;
                    for (int k = 0; k < 3; k++) change += Math.Abs(d[i + k] - d[i + stride + k]) + Math.Abs(d[i + k] - d[i + bpp + k]);
                    samples++;
                }
            return change / (double)Math.Max(1, samples);
        }

        /// <summary>The pixels as opaque RGBA (the PS2's alpha byte is 0 or 0x80 in these files, not real alpha).</summary>
        public static byte[] ToRgba(ReadOnlySpan<byte> data, Guess g)
        {
            var rgba = new byte[g.Width * g.Height * 4];
            for (int i = 0; i < g.Width * g.Height; i++)
            {
                rgba[i * 4] = data[i * g.BytesPerPixel];
                rgba[i * 4 + 1] = data[i * g.BytesPerPixel + 1];
                rgba[i * 4 + 2] = data[i * g.BytesPerPixel + 2];
                rgba[i * 4 + 3] = 255;
            }
            return rgba;
        }
    }
}
