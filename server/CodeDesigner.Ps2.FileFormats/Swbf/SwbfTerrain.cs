using CodeDesigner.Ps2.FileFormats.Assets;

namespace CodeDesigner.Ps2.FileFormats.Swbf
{
    /// <summary>
    /// A Battlefront PS2 terrain (a "tern" chunk) as a height grid and a <see cref="ModelMesh"/>. INFO is
    /// {f32 gridUnit, f32 heightScale, f32 floor, f32 ceiling, u16 gridSize, u16 patchEdge}. HGT8 holds run-length u8
    /// heights in 16x16-cell blocks (cell x, z at (x &amp; 15) + ((z &amp; 15) &lt;&lt; 4) + (block &lt;&lt; 8)); HEXP one 8-byte record
    /// per block {u8 shift, u8, i16 offset, ...}: height = ((h8 &lt;&lt; shift) + offset) * heightScale. Cell x, z sits at
    /// ((x - n/2) * gridUnit, (z - n/2 + 1) * gridUnit). LTEX names the layer textures and SCAL gives each layer's metres
    /// per repeat. PCHS holds one PTCH per 8x8-cell render patch, row by row: INFO {u16 layer mask, u16, i16 heightMin,
    /// i16 heightMax, u16 shift, u32 replaced}; a mask of 0 is outside the playable map and is not drawn, and a replaced
    /// patch carries its own POSI / STRP mesh (cut by a cave or a hangar) to draw instead of the grid. PCHS COLR is
    /// the baked lighting, one R5 G5 B5 A1 per vertex in 9x9 samples per patch. (Layout per openbf's research, MIT.)
    /// </summary>
    public sealed class SwbfTerrain
    {
        public string Name { get; private init; } = "";
        public int Size { get; private init; }
        public float GridUnit { get; private init; }
        public float HeightScale { get; private init; }
        public int PatchEdge { get; private init; } = 8;
        /// <summary>World heights, row-major [z][x].</summary>
        public float[] Heights { get; private init; } = Array.Empty<float>();
        /// <summary>Baked light per vertex as R G B (the PS2's 0x80 = 1.0), row-major [z][x]; null when absent.</summary>
        public byte[]? Light { get; private init; }
        public IReadOnlyList<string> Layers { get; private init; } = Array.Empty<string>();
        public IReadOnlyList<float> LayerScale { get; private init; } = Array.Empty<float>();
        /// <summary>Per render patch (row-major): the layer mask, 0 = not drawn.</summary>
        public IReadOnlyList<ushort> PatchMasks { get; private init; } = Array.Empty<ushort>();
        /// <summary>Per render patch: the shipped replacement mesh, or null where the grid is drawn.</summary>
        public IReadOnlyList<PatchMesh?> Replacements => _replacements;
        private PatchMesh?[] _replacements = Array.Empty<PatchMesh?>();

        public sealed record PatchMesh(float[] Positions, int[] Triangles, byte[]? Light);

        public static bool HasHeights(Ucfb tern) => tern.Child("INFO") is { Size: >= 20 } && tern.Child("HGT8") != null && tern.Child("HEXP") != null;

        public static SwbfTerrain Parse(Ucfb tern)
        {
            string name = tern.ChildText("NAME") ?? "";
            var info = tern.Child("INFO") is { Size: >= 20 } i ? i : throw new InvalidDataException($"Terrain {name} has no INFO");
            float unit = info.F32(0), scale = info.F32(4);
            int n = info.U16(16), edge = info.U16(18);
            if (n == 0 || n % 16 != 0 || n > 1024) throw new InvalidDataException($"Terrain {name}: grid size {n}");
            if (edge <= 0 || n % edge != 0) edge = 8;
            var h8 = SwbfTexture.Unpack(tern.Child("HGT8")!.Value.Bytes, 1, n * n);
            var hexp = tern.Child("HEXP")!.Value;
            int blocks = n / 16;
            if (hexp.Size < blocks * blocks * 8) throw new InvalidDataException($"Terrain {name}: HEXP is short");
            var heights = new float[n * n];
            for (int z = 0; z < n; z++)
                for (int x = 0; x < n; x++)
                {
                    int block = (x >> 4) + (z >> 4) * blocks;
                    int shift = hexp.Data[hexp.Body + block * 8] & 31;
                    int offset = hexp.S16(block * 8 + 2);
                    heights[z * n + x] = ((h8[(x & 15) + ((z & 15) << 4) + (block << 8)] << shift) + offset) * scale;
                }

            var layers = new List<string>();
            if (tern.Child("LTEX") is { } lt)
                foreach (var part in System.Text.Encoding.ASCII.GetString(lt.Bytes).Split('\0'))
                    if (part.Length > 0) layers.Add(part);
            var layerScale = new List<float>();
            if (tern.Child("SCAL") is { } sc)
                for (int k = 0; k < Math.Min(16, sc.Size / 4); k++) layerScale.Add(sc.F32(k * 4));

            int side = n / edge;
            var masks = new List<ushort>();
            var replaced = new List<(int Patch, Ucfb Ptch, short HeightMin, int Shift)>();
            byte[]? light = null;
            if (tern.Child("PCHS") is { } pchs)
            {
                foreach (var p in pchs.Children)
                {
                    if (p.Tag != "PTCH") continue;
                    var pi = p.Child("INFO");
                    ushort mask = pi is { Size: >= 8 } pin ? pin.U16(0) : (ushort)0;
                    if (pi is { Size: >= 14 } full && full.U32(10) != 0 && p.Child("POSI") != null && p.Child("STRP") != null)
                        replaced.Add((masks.Count, p, full.S16(4), full.U16(8)));
                    masks.Add(mask);
                }
                if (pchs.Child("COLR") is { } colr)
                    light = UnpackLight(SwbfTexture.Unpack(colr.Bytes, 2, side * side * 81 * 2), n, edge);
            }
            if (light == null && tern.Child("LUMI") is { } lumi && lumi.Size >= n * n)
            {
                // Grey, stored x-major.
                light = new byte[n * n * 3];
                for (int z = 0; z < n; z++)
                    for (int x = 0; x < n; x++)
                    {
                        byte v = (byte)(lumi.Data[lumi.Body + z + x * n] >> 1);
                        light[(z * n + x) * 3] = light[(z * n + x) * 3 + 1] = light[(z * n + x) * 3 + 2] = v;
                    }
            }

            var terrain = new SwbfTerrain
            {
                Name = name, Size = n, GridUnit = unit, HeightScale = scale, PatchEdge = edge, Heights = heights, Light = light,
                Layers = layers, LayerScale = layerScale, PatchMasks = masks,
            };
            terrain._replacements = new PatchMesh?[masks.Count];
            foreach (var (patch, ptch, heightMin, shift) in replaced)
                terrain._replacements[patch] = terrain.Replacement(patch, ptch, heightMin, shift);
            return terrain;
        }

        /// <summary>COLR (patches row by row, 9x9 samples row by row, PSMCT16) to R G B per grid vertex at the PS2's
        /// scale (5 bits shifted up 3: 0x80 = 1.0, up to 1.94).</summary>
        private static byte[]? UnpackLight(byte[] colr, int n, int edge)
        {
            int side = n / edge, samples = (edge + 1) * (edge + 1);
            if (colr.Length < side * side * samples * 2) return null;
            var light = new byte[n * n * 3];
            for (int p = 0; p < side * side; p++)
            {
                int pz = p / side, px = p % side;
                for (int s = 0; s < samples; s++)
                {
                    int x = px * edge + s % (edge + 1), z = pz * edge + s / (edge + 1);
                    if (x >= n || z >= n) continue;
                    int u = BitConverter.ToUInt16(colr, (p * samples + s) * 2);
                    int at = (z * n + x) * 3;
                    light[at] = (byte)((u & 31) << 3);
                    light[at + 1] = (byte)(((u >> 5) & 31) << 3);
                    light[at + 2] = (byte)(((u >> 10) & 31) << 3);
                }
            }
            return light;
        }

        private PatchMesh? Replacement(int patch, Ucfb ptch, short heightMin, int shift)
        {
            var posi = ptch.Child("POSI")!.Value;
            int count = posi.Size / 6;
            if (count == 0) return null;
            int side = Size / PatchEdge;
            float half = Size / 2f * GridUnit;
            float x0 = patch % side * PatchEdge * GridUnit - half, z0 = patch / side * PatchEdge * GridUnit - (half - GridUnit);
            float span = PatchEdge * GridUnit * (65536f / 65535f);
            float step = shift >= 0 && shift < 32 ? (65536u >> shift) / 65536f : 0;
            var pos = new float[count * 3];
            for (int v = 0; v < count; v++)
            {
                pos[v * 3] = x0 + posi.U16(v * 6) / 65536f * span;
                pos[v * 3 + 1] = (heightMin + posi.U16(v * 6 + 2) * step) * HeightScale;
                pos[v * 3 + 2] = z0 + posi.U16(v * 6 + 4) / 65536f * span;
            }
            byte[]? light = null;
            if (ptch.Child("COLR") is { } c)
            {
                var raw = SwbfTexture.Unpack(c.Bytes, 2, count * 2);
                light = new byte[count * 3];
                for (int v = 0; v < count; v++)
                {
                    int u = BitConverter.ToUInt16(raw, v * 2);
                    light[v * 3] = (byte)((u & 31) << 3);
                    light[v * 3 + 1] = (byte)(((u >> 5) & 31) << 3);
                    light[v * 3 + 2] = (byte)(((u >> 10) & 31) << 3);
                }
            }
            var tri = SwbfModel.Triangles(ptch.Child("STRP")!.Value, count);
            return tri.Count == 0 ? null : new PatchMesh(pos, tri.ToArray(), light);
        }

        public float X(int x) => (x - Size / 2f) * GridUnit;
        public float Z(int z) => (z - (Size / 2f - 1)) * GridUnit;

        /// <summary>Texture repeats per world metre for layer <paramref name="k"/> (one repeat across the map when unset).</summary>
        public float RepeatsPerMetre(int k)
        {
            float s = k < LayerScale.Count ? LayerScale[k] : 0;
            return s > 0 && float.IsFinite(s) ? MathF.Floor(GridUnit / s * 2048f + 0.5f) / 2048f / GridUnit : 1f / (Size * GridUnit);
        }

        /// <summary>
        /// The drawn terrain as a mesh in world space: the grid of every patch that paints a layer, in 32x32-cell
        /// chunks, then each replaced patch's own mesh. Quads split the PS2's way (the diagonal alternates by row).
        /// One texture, the base layer (LTEX[0]), tiled by its SCAL; the baked light is the vertex colour.
        /// </summary>
        public ModelMesh Build(string archive, string name)
        {
            var mesh = new ModelMesh { Archive = archive, Name = name };
            string? texture = Layers.Count > 0 ? Layers[0] : null;
            float rep = RepeatsPerMetre(0);
            int n = Size, side = n / PatchEdge;
            bool Drawn(int cx, int cz)
            {
                int p = cz / PatchEdge * side + cx / PatchEdge;
                if (p >= PatchMasks.Count) return PatchMasks.Count == 0;
                return PatchMasks[p] != 0 && (p >= Replacements.Count || Replacements[p] == null);
            }
            float H(int x, int z) => Heights[Math.Clamp(z, 0, n - 1) * n + Math.Clamp(x, 0, n - 1)];
            const int Block = 32;
            int index = 0;
            for (int bz = 0; bz < n - 1; bz += Block)
                for (int bx = 0; bx < n - 1; bx += Block)
                {
                    int w = Math.Min(Block, n - 1 - bx), d = Math.Min(Block, n - 1 - bz);
                    var tri = new List<int>();
                    int stride = w + 1;
                    for (int z = 0; z < d; z++)
                        for (int x = 0; x < w; x++)
                        {
                            if (!Drawn(bx + x, bz + z)) continue;
                            int a = z * stride + x, b = a + 1, c = a + stride, e = c + 1;
                            if (((bz + z) & 1) == 0) tri.AddRange(new[] { a, c, e, a, e, b });
                            else tri.AddRange(new[] { a, c, b, b, c, e });
                        }
                    if (tri.Count == 0) continue;
                    int count = (w + 1) * (d + 1);
                    var pos = new float[count * 3];
                    var nrm = new float[count * 3];
                    var uv = new float[count * 2];
                    var col = Light != null ? new byte[count * 4] : null;
                    for (int z = 0; z <= d; z++)
                        for (int x = 0; x <= w; x++)
                        {
                            int gx = bx + x, gz = bz + z, v = z * stride + x;
                            float wx = X(gx), wz = Z(gz);
                            pos[v * 3] = wx; pos[v * 3 + 1] = H(gx, gz); pos[v * 3 + 2] = wz;
                            float nx = -(H(gx + 1, gz) - H(gx - 1, gz)) / (2 * GridUnit), nz = -(H(gx, gz + 1) - H(gx, gz - 1)) / (2 * GridUnit);
                            float len = MathF.Sqrt(nx * nx + 1 + nz * nz);
                            nrm[v * 3] = nx / len; nrm[v * 3 + 1] = 1 / len; nrm[v * 3 + 2] = nz / len;
                            uv[v * 2] = (gx * GridUnit) * rep; uv[v * 2 + 1] = (gz * GridUnit) * rep;
                            if (col != null) Lit(col, v, Light!, gz * n + gx);
                        }
                    mesh.Chunks.Add(new ModelMesh.MeshChunk
                    {
                        Index = index++, GeometryOffset = -1, Visual = $"terrain{bx / Block}_{bz / Block}", Texture = texture, Unit = "World", Scale = 1,
                        VertexCount = count, Positions = pos, Normals = nrm, Uvs = uv, Colors = col, Triangles = tri.ToArray(),
                    });
                }
            for (int p = 0; p < Replacements.Count; p++)
            {
                if (Replacements[p] is not { } r || (p < PatchMasks.Count && PatchMasks[p] == 0)) continue;
                int count = r.Positions.Length / 3;
                var uv = new float[count * 2];
                var nrm = new float[count * 3];
                var col = r.Light != null ? new byte[count * 4] : null;
                float half = n / 2f * GridUnit;
                for (int v = 0; v < count; v++)
                {
                    uv[v * 2] = (r.Positions[v * 3] + half) * rep;
                    uv[v * 2 + 1] = (r.Positions[v * 3 + 2] + half - GridUnit) * rep;
                    if (col != null) Lit(col, v, r.Light!, v);
                }
                FaceNormals(r.Positions, r.Triangles, nrm);
                mesh.Chunks.Add(new ModelMesh.MeshChunk
                {
                    Index = index++, GeometryOffset = -1, Visual = $"patch{p}", Texture = texture, Unit = "World", Scale = 1,
                    VertexCount = count, Positions = (float[])r.Positions.Clone(), Normals = nrm, Uvs = uv, Colors = col, Triangles = r.Triangles,
                });
            }
            mesh.Recompute();
            return mesh;
        }

        private static void Lit(byte[] col, int v, byte[] light, int at)
        {
            col[v * 4] = light[at * 3]; col[v * 4 + 1] = light[at * 3 + 1]; col[v * 4 + 2] = light[at * 3 + 2]; col[v * 4 + 3] = 0x80;
        }

        private static void FaceNormals(float[] pos, int[] tri, float[] nrm)
        {
            for (int t = 0; t + 2 < tri.Length; t += 3)
            {
                int a = tri[t] * 3, b = tri[t + 1] * 3, c = tri[t + 2] * 3;
                float ux = pos[b] - pos[a], uy = pos[b + 1] - pos[a + 1], uz = pos[b + 2] - pos[a + 2];
                float vx = pos[c] - pos[a], vy = pos[c + 1] - pos[a + 1], vz = pos[c + 2] - pos[a + 2];
                float nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
                foreach (int i in new[] { a, b, c }) { nrm[i] += nx; nrm[i + 1] += ny; nrm[i + 2] += nz; }
            }
            for (int i = 0; i < nrm.Length; i += 3)
            {
                float len = MathF.Sqrt(nrm[i] * nrm[i] + nrm[i + 1] * nrm[i + 1] + nrm[i + 2] * nrm[i + 2]);
                if (len < 1e-6f) { nrm[i + 1] = 1; continue; }
                nrm[i] /= len; nrm[i + 1] /= len; nrm[i + 2] /= len;
            }
        }
    }
}
