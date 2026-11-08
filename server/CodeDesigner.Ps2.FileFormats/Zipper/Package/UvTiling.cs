namespace CodeDesigner.Ps2.FileFormats.Zipper.Package
{
    /// <summary>
    /// Repeating textures on new geometry (addMesh <c>uvTile</c> / <c>uvScale</c>). SOCOM II stores a vertex's UV as
    /// int16 / 4096, so it holds −8 … 8 repeats; anything further is clamped and the texture smears. Each triangle
    /// gets its own corners here (no shared vertices), triangles spanning more than <see cref="MaxSpan"/> repeats
    /// are split, and each triangle's UVs move by whole repeats to start near 0, which a repeating texture draws the
    /// same. Repeat (wrap) addressing in game is assumed: retail floors and sea tile this way.
    /// </summary>
    public static class UvTiling
    {
        /// <summary>The most repeats one triangle may span (with its start in 0 … 1 it then stays under 8).</summary>
        public const float MaxSpan = 6.5f;

        /// <summary>
        /// UVs projected from the geometry: each triangle onto the plane its normal faces most (x/z for floors, x/y or
        /// z/y for walls), one repeat every <paramref name="unitsPerRepeat"/> units, plus <paramref name="offset"/>.
        /// </summary>
        public static MeshTopology.InputMesh Project(MeshTopology.InputMesh mesh, float unitsPerRepeat, float[]? offset = null)
        {
            if (!(unitsPerRepeat > 0) || !float.IsFinite(unitsPerRepeat)) throw new ArgumentException("uvTile is the units one texture repeat covers (> 0)");
            float ou = offset is { Length: >= 2 } ? offset[0] : 0, ov = offset is { Length: >= 2 } ? offset[1] : 0;
            var flat = Unshare(mesh);
            for (int t = 0; t < flat.Triangles.Count; t++)
            {
                var (a, b, c) = flat.Triangles[t];
                float[] pa = flat.Positions[a], pb = flat.Positions[b], pc = flat.Positions[c];
                float ux = pb[0] - pa[0], uy = pb[1] - pa[1], uz = pb[2] - pa[2], vx = pc[0] - pa[0], vy = pc[1] - pa[1], vz = pc[2] - pa[2];
                float nx = MathF.Abs(uy * vz - uz * vy), ny = MathF.Abs(uz * vx - ux * vz), nz = MathF.Abs(ux * vy - uy * vx);
                foreach (int i in new[] { a, b, c })
                {
                    var p = flat.Positions[i];
                    (float u, float v) = ny >= nx && ny >= nz ? (p[0], -p[2]) : nx >= nz ? (p[2], p[1]) : (p[0], p[1]);
                    flat.Uvs[i] = new[] { u / unitsPerRepeat + ou, v / unitsPerRepeat + ov };
                }
            }
            return Fit(flat);
        }

        /// <summary>The mesh's own UVs multiplied by (<paramref name="u"/>, <paramref name="v"/>), kept in range.</summary>
        public static MeshTopology.InputMesh Scale(MeshTopology.InputMesh mesh, float u, float v)
        {
            if (!(u > 0) || !(v > 0) || !float.IsFinite(u) || !float.IsFinite(v)) throw new ArgumentException("uvScale is [u, v] repeats (> 0)");
            var flat = Unshare(mesh);
            for (int i = 0; i < flat.Uvs.Count; i++) flat.Uvs[i] = new[] { flat.Uvs[i][0] * u, flat.Uvs[i][1] * v };
            return Fit(flat);
        }

        /// <summary>Splits triangles spanning more than <see cref="MaxSpan"/> repeats and moves each into range.</summary>
        public static MeshTopology.InputMesh Fit(MeshTopology.InputMesh flat)
        {
            var output = new MeshTopology.InputMesh();
            bool normals = flat.Normals.Count == flat.Positions.Count, colors = flat.Colors.Count == flat.Positions.Count;
            var work = new Stack<Corner[]>();
            foreach (var (a, b, c) in flat.Triangles) work.Push(new[] { CornerOf(flat, a, normals, colors), CornerOf(flat, b, normals, colors), CornerOf(flat, c, normals, colors) });
            int guard = 0;
            while (work.Count > 0)
            {
                if (++guard > 2_000_000) throw new InvalidOperationException("uv tiling splits too many triangles: use a larger uvTile");
                var tri = work.Pop();
                float spanU = Span(tri, 0), spanV = Span(tri, 1);
                if (spanU > MaxSpan || spanV > MaxSpan)
                {
                    // Split the edge longest in UV at its middle.
                    int e = 0; float best = -1;
                    for (int k = 0; k < 3; k++)
                    {
                        var p = tri[k].Uv; var q = tri[(k + 1) % 3].Uv;
                        float d = MathF.Max(MathF.Abs(p[0] - q[0]), MathF.Abs(p[1] - q[1]));
                        if (d > best) { best = d; e = k; }
                    }
                    var m = Corner.Mid(tri[e], tri[(e + 1) % 3]);
                    work.Push(new[] { tri[e], m, tri[(e + 2) % 3] });
                    work.Push(new[] { m, tri[(e + 1) % 3], tri[(e + 2) % 3] });
                    continue;
                }
                float du = MathF.Floor(tri.Min(c => c.Uv[0])), dv = MathF.Floor(tri.Min(c => c.Uv[1]));
                int at = output.Positions.Count;
                foreach (var c in tri)
                {
                    output.Positions.Add(c.Position);
                    output.Uvs.Add(new[] { c.Uv[0] - du, c.Uv[1] - dv });
                    if (c.Normal != null) output.Normals.Add(c.Normal);
                    if (c.Color != null) output.Colors.Add(c.Color);
                }
                output.Triangles.Add((at, at + 1, at + 2));
            }
            return output;
        }

        private static float Span(Corner[] t, int axis) => t.Max(c => c.Uv[axis]) - t.Min(c => c.Uv[axis]);

        private sealed record Corner(float[] Position, float[] Uv, float[]? Normal, byte[]? Color)
        {
            public static Corner Mid(Corner a, Corner b) => new(Lerp(a.Position, b.Position), Lerp(a.Uv, b.Uv),
                a.Normal != null && b.Normal != null ? Normalize(Lerp(a.Normal, b.Normal)) : null,
                a.Color != null && b.Color != null ? a.Color.Zip(b.Color, (x, y) => (byte)((x + y + 1) / 2)).ToArray() : null);

            private static float[] Lerp(float[] a, float[] b) => a.Zip(b, (x, y) => (x + y) / 2).ToArray();

            private static float[] Normalize(float[] n)
            {
                float l = MathF.Sqrt(n.Sum(x => x * x));
                return l > 0 ? n.Select(x => x / l).ToArray() : n;
            }
        }

        private static Corner CornerOf(MeshTopology.InputMesh m, int i, bool normals, bool colors) =>
            new(m.Positions[i], i < m.Uvs.Count ? m.Uvs[i] : new[] { 0f, 0f }, normals ? m.Normals[i] : null, colors ? m.Colors[i] : null);

        // Every triangle with its own three corners.
        private static MeshTopology.InputMesh Unshare(MeshTopology.InputMesh mesh)
        {
            var flat = new MeshTopology.InputMesh();
            bool normals = mesh.Normals.Count == mesh.Positions.Count, colors = mesh.Colors.Count == mesh.Positions.Count;
            foreach (var (a, b, c) in mesh.Triangles)
            {
                int at = flat.Positions.Count;
                foreach (int i in new[] { a, b, c })
                {
                    flat.Positions.Add(mesh.Positions[i]);
                    flat.Uvs.Add(i < mesh.Uvs.Count ? mesh.Uvs[i] : new[] { 0f, 0f });
                    if (normals) flat.Normals.Add(mesh.Normals[i]);
                    if (colors) flat.Colors.Add(mesh.Colors[i]);
                }
                flat.Triangles.Add((at, at + 1, at + 2));
            }
            return flat;
        }
    }
}
