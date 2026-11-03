using CodeDesigner.Ps2.FileFormats.Assets;
using CodeDesigner.Ps2.FileFormats.Zipper.Texture;

namespace CodeDesigner.Ps2.FileFormats.Zipper.Package
{
    /// <summary>
    /// A weapon's HUD / armory icon drawn from its model: a 128×32 side view (camera on +z, +x to the right) of the
    /// high-detail visual, coloured from the model's own textures and shaded from its normals, 4×4 supersampled.
    /// Pixels the model covers less than half of are clear (index 0); colours go to a 6×7×6 cube (palette entries
    /// 1..252), as Mods/SOCOMCA/custom_weapon.py's icons did. HUD icons are 8-bit with a 32-bit palette.
    /// </summary>
    public static class WeaponIcon
    {
        public const int Width = 128, Height = 32;
        private const int Ss = 4;
        private static readonly float[] Light = { 0.35f, 0.85f, 0.45f };

        /// <summary>8-bit pixels (128×32) with rows bottom-up, as the HUD's textures are stored, and the logical
        /// 256-entry RGBA palette (index 0 clear).</summary>
        public static (byte[] Pixels, byte[] Palette) Render(ZipperPackage pkg, ZipperPackage.ModelRef model)
        {
            var mesh = ZipperModelMesh.Build(pkg, model);
            // The high-detail visual(s): N000_*, or the chunks placed by a *_high node.
            var chunks = mesh.Chunks.Where(c => c.Visual?.StartsWith("N000", StringComparison.Ordinal) == true
                || c.Node?.EndsWith("_high", StringComparison.OrdinalIgnoreCase) == true).ToList();
            if (chunks.Count == 0) chunks = mesh.Chunks.ToList();
            string? ns = pkg.TextureNamespace(model.Namespace);
            var textures = new Dictionary<string, (byte[] Rgba, int W, int H)?>(StringComparer.OrdinalIgnoreCase);
            (byte[] Rgba, int W, int H)? Tex(string? name)
            {
                if (string.IsNullOrEmpty(name)) return null;
                if (!textures.TryGetValue(name, out var t))
                {
                    try { t = pkg.DecodeTexture(name, ns) is { } d ? (d.Texture.Rgba, d.Texture.Header.Width, d.Texture.Header.Height) : null; }
                    catch (InvalidDataException) { t = null; }
                    textures[name] = t;
                }
                return t;
            }

            float x0 = float.MaxValue, x1 = float.MinValue, y0 = float.MaxValue, y1 = float.MinValue;
            var tris = new List<(float[] P, float[] N, float[] Uv, (byte[] Rgba, int W, int H)? Tex)>();
            foreach (var c in chunks)
            {
                var tex = Tex(c.Texture);
                for (int t = 0; t + 2 < c.Triangles.Length; t += 3)
                {
                    int a = c.Triangles[t], b = c.Triangles[t + 1], d = c.Triangles[t + 2];
                    float[] P = new float[9], N = new float[9], Uv = new float[6];
                    int k = 0;
                    foreach (int v in new[] { a, b, d })
                    {
                        for (int j = 0; j < 3; j++) P[k * 3 + j] = c.Positions[v * 3 + j];
                        float nx = c.Normals.Length > v * 3 + 2 ? c.Normals[v * 3] : 0, ny = c.Normals.Length > v * 3 + 2 ? c.Normals[v * 3 + 1] : 1, nz = c.Normals.Length > v * 3 + 2 ? c.Normals[v * 3 + 2] : 0;
                        float[] m = c.Matrix;
                        N[k * 3] = nx * m[0] + ny * m[4] + nz * m[8];
                        N[k * 3 + 1] = nx * m[1] + ny * m[5] + nz * m[9];
                        N[k * 3 + 2] = nx * m[2] + ny * m[6] + nz * m[10];
                        if (c.Uvs.Length > v * 2 + 1) { Uv[k * 2] = c.Uvs[v * 2]; Uv[k * 2 + 1] = c.Uvs[v * 2 + 1]; }
                        k++;
                    }
                    // Zero-area triangles (a hidden visual's speck) neither draw nor frame the view.
                    float ex = P[3] - P[0], ey = P[4] - P[1], fx = P[6] - P[0], fy = P[7] - P[1];
                    float ez = P[5] - P[2], fz = P[8] - P[2];
                    float cx = ey * fz - ez * fy, cy = ez * fx - ex * fz, cz = ex * fy - ey * fx;
                    if (cx * cx + cy * cy + cz * cz < 1e-12f) continue;
                    tris.Add((P, N, Uv, tex));
                    for (int j = 0; j < 3; j++)
                    {
                        x0 = MathF.Min(x0, P[j * 3]); x1 = MathF.Max(x1, P[j * 3]);
                        y0 = MathF.Min(y0, P[j * 3 + 1]); y1 = MathF.Max(y1, P[j * 3 + 1]);
                    }
                }
            }
            if (tris.Count == 0) throw new InvalidOperationException($"{model.Name} has no geometry to draw an icon from");

            int W = Width * Ss, H = Height * Ss;
            float sc = MathF.Min((W - 2 * Ss) / MathF.Max(x1 - x0, 1e-6f), (H - 2 * Ss) / MathF.Max(y1 - y0, 1e-6f));
            float ox = (W - (x1 - x0) * sc) / 2, oy = (H - (y1 - y0) * sc) / 2;
            var col = new int[W * H];                       // 0 = empty, else 0xFFRRGGBB
            var zb = new float[W * H];
            Array.Fill(zb, float.MinValue);
            foreach (var (P, N, Uv, tex) in tris)
            {
                float[] sx = new float[3], sy = new float[3];
                for (int j = 0; j < 3; j++) { sx[j] = ox + (P[j * 3] - x0) * sc; sy[j] = H - (oy + (P[j * 3 + 1] - y0) * sc); }
                float den = (sy[1] - sy[2]) * (sx[0] - sx[2]) + (sx[2] - sx[1]) * (sy[0] - sy[2]);
                if (MathF.Abs(den) < 1e-9f) continue;
                int lx = Math.Max(0, (int)sx.Min()), hx = Math.Min(W - 1, (int)sx.Max() + 1);
                int ly = Math.Max(0, (int)sy.Min()), hy = Math.Min(H - 1, (int)sy.Max() + 1);
                for (int py = ly; py <= hy; py++)
                    for (int px = lx; px <= hx; px++)
                    {
                        float l0 = ((sy[1] - sy[2]) * (px + 0.5f - sx[2]) + (sx[2] - sx[1]) * (py + 0.5f - sy[2])) / den;
                        float l1 = ((sy[2] - sy[0]) * (px + 0.5f - sx[2]) + (sx[0] - sx[2]) * (py + 0.5f - sy[2])) / den;
                        float l2 = 1 - l0 - l1;
                        if (l0 < 0 || l1 < 0 || l2 < 0) continue;
                        float z = l0 * P[2] + l1 * P[5] + l2 * P[8];
                        int at = py * W + px;
                        if (z <= zb[at]) continue;
                        zb[at] = z;
                        float nx = l0 * N[0] + l1 * N[3] + l2 * N[6], ny = l0 * N[1] + l1 * N[4] + l2 * N[7], nz = l0 * N[2] + l1 * N[5] + l2 * N[8];
                        float ln = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                        if (ln < 1e-6f) ln = 1;
                        float shade = MathF.Max(0, (nx * Light[0] + ny * Light[1] + nz * Light[2]) / ln) * 0.75f + 0.25f;
                        int r = 160, g = 160, b = 160;
                        if (tex is { } t)
                        {
                            float u = l0 * Uv[0] + l1 * Uv[2] + l2 * Uv[4], v = l0 * Uv[1] + l1 * Uv[3] + l2 * Uv[5];
                            int tx = Math.Clamp((int)((u - MathF.Floor(u)) * t.W), 0, t.W - 1), ty = Math.Clamp((int)((v - MathF.Floor(v)) * t.H), 0, t.H - 1);
                            int o = (ty * t.W + tx) * 4;
                            r = t.Rgba[o]; g = t.Rgba[o + 1]; b = t.Rgba[o + 2];
                        }
                        col[at] = unchecked((int)0xFF000000) | (Clamp(r * shade) << 16) | (Clamp(g * shade) << 8) | Clamp(b * shade);
                    }
            }

            // Palette: 0 clear, 1.. a 6×7×6 colour cube; downsample by coverage and mean colour.
            var palette = new byte[1024];
            int n = 1;
            for (int ri = 0; ri < 6; ri++)
                for (int gi = 0; gi < 7; gi++)
                    for (int bi = 0; bi < 6; bi++, n++)
                    {
                        palette[n * 4] = (byte)(ri * 51); palette[n * 4 + 1] = (byte)MathF.Round(gi * 42.5f); palette[n * 4 + 2] = (byte)(bi * 51); palette[n * 4 + 3] = 255;
                    }
            var pixels = new byte[Width * Height];
            for (int y = 0; y < Height; y++)
                for (int x = 0; x < Width; x++)
                {
                    int hits = 0; float sr = 0, sg = 0, sb = 0;
                    for (int j = 0; j < Ss; j++)
                        for (int i = 0; i < Ss; i++)
                        {
                            int c = col[(y * Ss + j) * W + x * Ss + i];
                            if (c == 0) continue;
                            hits++; sr += (c >> 16) & 0xFF; sg += (c >> 8) & 0xFF; sb += c & 0xFF;
                        }
                    if (hits * 2 < Ss * Ss) continue;
                    int qr = (int)MathF.Round(sr / hits / 51f), qg = (int)MathF.Round(sg / hits / 42.5f), qb = (int)MathF.Round(sb / hits / 51f);
                    pixels[(Height - 1 - y) * Width + x] = (byte)(1 + qr * 42 + qg * 6 + qb);
                }
            return (pixels, palette);
        }

        private static int Clamp(float v) => Math.Clamp((int)MathF.Round(v), 0, 255);
    }
}
