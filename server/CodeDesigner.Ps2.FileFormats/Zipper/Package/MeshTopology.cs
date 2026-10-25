using CodeDesigner.Ps2.FileFormats.Assets;
using System.Globalization;
using System.Text;
using CodeDesigner.Ps2.FileFormats.Zipper.Geo;
using CodeDesigner.Ps2.FileFormats.Zipper.Model;
using CodeDesigner.Ps2.FileFormats.Zipper.ZED;

namespace CodeDesigner.Ps2.FileFormats.Zipper.Package
{
    /// <summary>
    /// Edits that change a model's geometry, not just its vertices: delete faces, duplicate faces, import a mesh
    /// (arrays, OBJ text or a primitive) into a visual and texture. Built on <see cref="ModelLayout"/>, which re-lays the
    /// payload (retail chunks re-encode byte for byte). New geometry is split into chunks of at most 72 vertices / 48
    /// triangles and quantised in the visual's own position unit. Each returns the archive's new bytes and the mesh
    /// indices (<see cref="ModelMesh"/> order) of the chunks it created.
    /// </summary>
    public static class MeshTopology
    {
        public sealed record Result(Dictionary<string, byte[]> Changed, List<int> NewChunks);

        /// <summary>A triangle mesh in model space (as ASSET_MODEL_MESH serves it).</summary>
        public sealed class InputMesh
        {
            public List<float[]> Positions { get; } = new();
            public List<float[]> Normals { get; } = new();
            public List<float[]> Uvs { get; } = new();
            public List<byte[]> Colors { get; } = new();
            public List<(int A, int B, int C)> Triangles { get; } = new();
        }

        private sealed class Context
        {
            public required ZedContainer Container { get; init; }
            public required ZipperPackage.ModelRef Model { get; init; }
            public required ModelLayout Layout { get; init; }
            public required ModelMesh Mesh { get; init; }
        }

        private static Context Open(ZipperPackage pkg, string archive, string modelName)
        {
            var model = pkg.FindModel(archive, modelName) ?? throw new KeyNotFoundException($"Model {archive}:{modelName} not found");
            var mesh = ZipperModelMesh.Build(pkg, model);
            var container = ZedContainer.FromBytes(pkg.Entry(archive)!.Data);
            var layout = ModelLayout.Parse(container, model.NodeIndex)
                ?? throw new InvalidOperationException($"{modelName} cannot be re-laid (line strips or unknown chain tags)");
            return new Context { Container = container, Model = model, Layout = layout, Mesh = mesh };
        }

        private static Result Finish(Context ctx, string archive, ISet<ModelLayout.Block> created)
        {
            ctx.Layout.Write(ctx.Container, ctx.Model.NodeIndex);
            // Mesh indices of the new chunks: ModelMesh walks visuals, sections, chunks, skipping geometry a chain repeats.
            var newChunks = new List<int>();
            int index = 0;
            foreach (var v in ctx.Layout.Visuals)
            {
                var seen = new HashSet<ModelLayout.Block>(ReferenceEqualityComparer.Instance);
                foreach (var s in v.Sections)
                    foreach (var c in s.Chunks)
                    {
                        if (!seen.Add(c.Geometry)) continue;
                        if (created.Contains(c.Geometry)) newChunks.Add(index);
                        index++;
                    }
            }
            return new Result(new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase) { [archive] = ctx.Container.ToBytes() }, newChunks);
        }

        // ------------------------------------------------------------------ delete

        /// <summary>
        /// Deletes triangles (per chunk geometry offset). Unused vertices go too; an emptied chunk is removed with its
        /// colour block, an emptied section with its material. A visual must keep at least one triangle.
        /// </summary>
        public static Result DeleteFaces(ZipperPackage pkg, string archive, string modelName, IReadOnlyDictionary<int, int[]> faces)
        {
            var ctx = Open(pkg, archive, modelName);
            var byOffset = BlocksByOffset(ctx);
            var replaced = new Dictionary<ModelLayout.Block, ModelLayout.LayoutChunk?>(ReferenceEqualityComparer.Instance);
            foreach (var (offset, tris) in faces)
            {
                if (!byOffset.TryGetValue(offset, out var lc)) throw new ArgumentException($"No chunk at geometry offset {offset}");
                var drop = new HashSet<int>(tris);
                var src = ModelLayout.ReadChunk(lc.Geometry.Data);
                var keep = Enumerable.Range(0, src.Triangles.Count).Where(i => !drop.Contains(i)).ToList();
                replaced[lc.Geometry] = keep.Count == 0 ? null : Subset(src, lc, keep);
            }
            foreach (var v in ctx.Layout.Visuals)
            {
                var emptied = new List<ModelLayout.Section>();
                foreach (var s in v.Sections)
                {
                    bool had = s.Chunks.Count > 0;
                    for (int i = s.Chunks.Count - 1; i >= 0; i--)
                        if (replaced.TryGetValue(s.Chunks[i].Geometry, out var next))
                        {
                            if (next == null) s.Chunks.RemoveAt(i); else s.Chunks[i] = next;
                        }
                    if (had && s.Chunks.Count == 0) emptied.Add(s);
                }
                // Only sections this edit emptied go; retail's chunk-less sections stay.
                v.Sections.RemoveAll(emptied.Contains);
                if (!v.Sections.Any(s => s.Chunks.Count > 0)) throw new InvalidOperationException($"Deleting these faces would empty visual {v.Key}; hide it instead");
            }
            return Finish(ctx, archive, new HashSet<ModelLayout.Block>());
        }

        private static ModelLayout.LayoutChunk Subset(ModelLayout.ChunkData src, ModelLayout.LayoutChunk lc, List<int> keepTris)
        {
            var map = new Dictionary<int, int>();
            var dst = new ModelLayout.ChunkData { Head = src.Head, IndexWord = src.IndexWord, Origin = src.Origin, Scale = src.Scale };
            byte[]? colours = lc.Colour?.Data;
            var newColours = new List<byte>();
            int Map(int v)
            {
                if (map.TryGetValue(v, out int n)) return n;
                n = dst.Vertices.Count;
                map[v] = n;
                dst.Vertices.Add(src.Vertices[v]);
                if (colours != null) newColours.AddRange(v * 4 + 4 <= colours.Length ? colours[(v * 4)..(v * 4 + 4)] : new byte[] { 128, 128, 128, 128 });
                return n;
            }
            foreach (int t in keepTris)
            {
                var (a, b, c) = src.Triangles[t];
                dst.Triangles.Add((Map(a), Map(b), Map(c)));
                dst.TriangleFlags.Add(src.TriangleFlags[t]);
                dst.FaceNormals.Add(src.FaceNormals[t]);
            }
            return new ModelLayout.LayoutChunk
            {
                Geometry = new ModelLayout.Block(ModelLayout.WriteChunk(dst), lc.Geometry.TagHigh, lc.Geometry.Upper0, lc.Geometry.Upper1),
                Colour = lc.Colour != null ? ModelLayout.ColourBlock(newColours.ToArray(), lc.Colour) : null,
            };
        }

        // Mesh chunk geometry offsets are payload offsets: match them to the blocks parsed from there.
        private static Dictionary<int, ModelLayout.LayoutChunk> BlocksByOffset(Context ctx)
        {
            var result = new Dictionary<int, ModelLayout.LayoutChunk>();
            foreach (var c in ctx.Layout.Visuals.SelectMany(v => v.Sections).SelectMany(s => s.Chunks))
                if (c.Geometry.SourceOffset >= 0) result.TryAdd(c.Geometry.SourceOffset, c);
            return result;
        }

        // ------------------------------------------------------------------ add geometry

        /// <summary>
        /// Adds triangles to <paramref name="visualKey"/> textured with <paramref name="texture"/> (an existing section
        /// with that texture, or a new section whose material is copied from the visual's first section).
        /// <paramref name="replace"/> removes the visual's existing geometry first.
        /// </summary>
        public static Result AddMesh(ZipperPackage pkg, string archive, string modelName, string visualKey, string texture, InputMesh input, bool replace)
        {
            if (input.Triangles.Count == 0) throw new ArgumentException("The mesh has no triangles");
            var ctx = Open(pkg, archive, modelName);
            var visual = ctx.Layout.Visuals.FirstOrDefault(v => v.Key.Equals(visualKey, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"{modelName} has no visual {visualKey} (visuals: {string.Join(", ", ctx.Layout.Visuals.Select(v => v.Key))})");
            var placement = ctx.Mesh.Chunks.FirstOrDefault(c => string.Equals(c.Visual, visual.Key, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"{visual.Key} has no geometry to take its placement from");
            // Retail chains can open with a section that has no chunks (material set-up); template from one that draws.
            var templateSection = visual.Sections.FirstOrDefault(s => s.Chunks.Count > 0)
                ?? ctx.Layout.Visuals.SelectMany(v => v.Sections).First(s => s.Chunks.Count > 0);
            var templateChunk = templateSection.Chunks[0];
            var head = ModelLayout.ReadChunk(templateChunk.Geometry.Data);

            if (replace) visual.Sections.Clear();
            var section = visual.Sections.FirstOrDefault(s => s.TextureName.Equals(texture, StringComparison.OrdinalIgnoreCase));
            if (section == null)
            {
                var template = ctx.Layout.Visuals.SelectMany(v => v.Sections).FirstOrDefault(s => s.TextureName.Equals(texture, StringComparison.OrdinalIgnoreCase)) ?? templateSection;
                section = new ModelLayout.Section
                {
                    NameField = new ModelLayout.Block(NameField(texture), templateSection.NameField.TagHigh, templateSection.NameField.Upper0, templateSection.NameField.Upper1),
                    Material = template.Material == null ? null : new ModelLayout.Block((byte[])template.Material.Data.Clone(), template.Material.TagHigh, template.Material.Upper0, template.Material.Upper1),
                };
                visual.Sections.Add(section);
            }

            var created = new HashSet<ModelLayout.Block>(ReferenceEqualityComparer.Instance);
            foreach (var chunk in Encode(input, placement, head, templateChunk))
            {
                section.Chunks.Add(chunk);
                created.Add(chunk.Geometry);
            }
            return Finish(ctx, archive, created);
        }

        /// <summary>Copies triangles (per chunk geometry offset), moved by a model-space matrix, into new chunks of the same sections.</summary>
        public static Result DuplicateFaces(ZipperPackage pkg, string archive, string modelName, IReadOnlyDictionary<int, int[]> faces, float[] matrix)
        {
            var ctx = Open(pkg, archive, modelName);
            var mesh = ctx.Mesh;
            var byOffset = BlocksByOffset(ctx);
            var created = new HashSet<ModelLayout.Block>(ReferenceEqualityComparer.Instance);
            foreach (var (offset, tris) in faces)
            {
                var mc = mesh.Chunks.First(c => c.GeometryOffset == offset);
                var lc = byOffset[offset];
                var section = ctx.Layout.Visuals.SelectMany(v => v.Sections).First(s => s.Chunks.Contains(lc));
                var input = new InputMesh();
                var map = new Dictionary<int, int>();
                int V(int i)
                {
                    if (map.TryGetValue(i, out int n)) return n;
                    n = input.Positions.Count;
                    map[i] = n;
                    var (x, y, z) = Matrix4.Apply(matrix, mc.Positions[i * 3], mc.Positions[i * 3 + 1], mc.Positions[i * 3 + 2]);
                    input.Positions.Add(new[] { x, y, z });
                    // Chunk normals are local: through the placement to model space, then the transform.
                    var (mx, my, mz) = ApplyLinear(mc.Matrix, mc.Normals[i * 3], mc.Normals[i * 3 + 1], mc.Normals[i * 3 + 2]);
                    var (nx, ny, nz) = ApplyLinear(matrix, mx, my, mz);
                    input.Normals.Add(new[] { nx, ny, nz });
                    input.Uvs.Add(new[] { mc.Uvs[i * 2], mc.Uvs[i * 2 + 1] });
                    input.Colors.Add(mc.Colors != null ? mc.Colors[(i * 4)..(i * 4 + 4)] : new byte[] { 128, 128, 128, 128 });
                    return n;
                }
                foreach (int t in tris)
                    input.Triangles.Add((V((int)mc.Triangles[t * 3]), V((int)mc.Triangles[t * 3 + 1]), V((int)mc.Triangles[t * 3 + 2])));
                if (input.Triangles.Count == 0) continue;
                var head = ModelLayout.ReadChunk(lc.Geometry.Data);
                foreach (var chunk in Encode(input, mc, head, lc))
                {
                    section.Chunks.Add(chunk);
                    created.Add(chunk.Geometry);
                }
            }
            if (created.Count == 0) throw new ArgumentException("No faces to duplicate");
            return Finish(ctx, archive, created);
        }

        private static (float, float, float) ApplyLinear(float[] m, float x, float y, float z) =>
            (x * m[0] + y * m[4] + z * m[8], x * m[1] + y * m[5] + z * m[9], x * m[2] + y * m[6] + z * m[10]);

        // Model-space mesh -> chunks in the visual's local space and unit (split like s2static encode_mesh).
        private static IEnumerable<ModelLayout.LayoutChunk> Encode(InputMesh input, ModelMesh.MeshChunk placement, ModelLayout.ChunkData head, ModelLayout.LayoutChunk template)
        {
            float[] inverse = Matrix4.InverseAffine(placement.Matrix);
            bool sixteenth = placement.Unit == nameof(ZedModelFile.PositionUnit.Sixteenth);
            float div = sixteenth ? 16f : 32768f;
            var local = input.Positions.Select(p => { var (x, y, z) = Matrix4.Apply(inverse, p[0], p[1], p[2]); return new[] { x, y, z }; }).ToList();
            var localNormals = input.Normals.Count == input.Positions.Count
                ? input.Normals.Select(n => { var (x, y, z) = ApplyLinear(inverse, n[0], n[1], n[2]); float l = MathF.Sqrt(x * x + y * y + z * z); return l > 0 ? new[] { x / l, y / l, z / l } : new[] { 0f, 1f, 0f }; }).ToList()
                : null;

            int i = 0;
            while (i < input.Triangles.Count)
            {
                var remap = new Dictionary<int, int>();
                var verts = new List<int>();
                var tris = new List<(int, int, int)>();
                while (i < input.Triangles.Count && tris.Count < ModelLayout.MaxChunkTriangles)
                {
                    var (a, b, c) = input.Triangles[i];
                    int need = new[] { a, b, c }.Distinct().Count(k => !remap.ContainsKey(k));
                    if (verts.Count + need > ModelLayout.MaxChunkVertices) break;
                    int R(int k) { if (!remap.TryGetValue(k, out int n)) { n = verts.Count; remap[k] = n; verts.Add(k); } return n; }
                    tris.Add((R(a), R(b), R(c)));
                    i++;
                }

                // Origin and scale so every vertex fits int16 (retail map chunks: scale 1, 1/16 units around an origin).
                var min = new float[] { float.MaxValue, float.MaxValue, float.MaxValue };
                var max = new float[] { float.MinValue, float.MinValue, float.MinValue };
                foreach (int k in verts) for (int d = 0; d < 3; d++) { min[d] = MathF.Min(min[d], local[k][d]); max[d] = MathF.Max(max[d], local[k][d]); }
                var origin = sixteenth ? new[] { MathF.Round((min[0] + max[0]) / 2), MathF.Round((min[1] + max[1]) / 2), MathF.Round((min[2] + max[2]) / 2) } : new float[3];
                float maxAbs = verts.SelectMany(k => Enumerable.Range(0, 3).Select(d => MathF.Abs(local[k][d] - origin[d]))).DefaultIfEmpty(1).Max();
                float scale = sixteenth ? MathF.Max(1f, maxAbs * div / 32000f) : MathF.Max(1e-4f, maxAbs * 1.0001f);

                var chunk = new ModelLayout.ChunkData { Head = head.Head, IndexWord = head.IndexWord, Origin = origin, Scale = scale };
                var colours = new List<byte>();
                foreach (int k in verts)
                {
                    var r = new byte[16];
                    for (int d = 0; d < 3; d++) PutI16(r, d * 2, (local[k][d] - origin[d]) * div / scale);
                    float[] n = localNormals?[k] ?? new[] { 0f, 1f, 0f };
                    PutI16(r, 6, n[0] * 32767);
                    float[] uv = k < input.Uvs.Count ? input.Uvs[k] : new[] { 0f, 0f };
                    PutI16(r, 8, uv[0] * 4096);
                    PutI16(r, 10, uv[1] * 4096);
                    PutI16(r, 12, n[1] * 32767);
                    PutI16(r, 14, n[2] * 32767);
                    chunk.Vertices.Add(r);
                    colours.AddRange(k < input.Colors.Count ? input.Colors[k] : new byte[] { 128, 128, 128, 128 });
                }
                foreach (var (a, b, c) in tris)
                {
                    chunk.Triangles.Add((a, b, c));
                    chunk.TriangleFlags.Add(3);
                    chunk.FaceNormals.Add(ModelLayout.FaceNormal(chunk.Vertices[a], chunk.Vertices[b], chunk.Vertices[c]));
                }
                yield return new ModelLayout.LayoutChunk
                {
                    Geometry = new ModelLayout.Block(ModelLayout.WriteChunk(chunk), template.Geometry.TagHigh, template.Geometry.Upper0, template.Geometry.Upper1),
                    Colour = template.Colour != null ? ModelLayout.ColourBlock(colours.ToArray(), template.Colour) : null,
                };
            }
        }

        private static void PutI16(byte[] b, int at, float v) =>
            BitConverter.GetBytes((short)Math.Clamp((int)MathF.Round(v), -32767, 32767)).CopyTo(b, at);

        private static byte[] NameField(string texture)
        {
            var raw = Encoding.ASCII.GetBytes(texture).Append((byte)0).ToList();
            while (raw.Count % 16 != 0) raw.Add(0);
            return raw.ToArray();
        }

        // ------------------------------------------------------------------ sources

        /// <summary>
        /// OBJ text (v / vt / vn / f; polygons fanned into triangles) as a model-space mesh. <paramref name="yUp"/>:
        /// the file is Y up (Blender, the editors' OBJ export), which is model space as it is; false flips a Y-down file.
        /// </summary>
        public static InputMesh ParseObj(string text, bool yUp = true, float scale = 1, float[]? offset = null)
        {
            var v = new List<float[]>(); var vt = new List<float[]>(); var vn = new List<float[]>();
            var mesh = new InputMesh();
            var keys = new Dictionary<string, int>();
            float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);
            float sy = yUp ? 1 : -1;
            int Vertex(string token)
            {
                if (keys.TryGetValue(token, out int k)) return k;
                string[] p = token.Split('/');
                int Idx(string s, int count) { int i = int.Parse(s, CultureInfo.InvariantCulture); return i < 0 ? count + i : i - 1; }
                var pos = v[Idx(p[0], v.Count)];
                k = mesh.Positions.Count;
                mesh.Positions.Add(new[] { pos[0] * scale + (offset?[0] ?? 0), pos[1] * sy * scale + (offset?[1] ?? 0), pos[2] * scale + (offset?[2] ?? 0) });
                mesh.Uvs.Add(p.Length > 1 && p[1].Length > 0 ? vt[Idx(p[1], vt.Count)] : new[] { 0f, 0f });
                var n = p.Length > 2 && p[2].Length > 0 ? vn[Idx(p[2], vn.Count)] : null;
                mesh.Normals.Add(n != null ? new[] { n[0], n[1] * sy, n[2] } : new[] { 0f, 0f, 0f });
                mesh.Colors.Add(new byte[] { 128, 128, 128, 128 });
                keys[token] = k;
                return k;
            }
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                string[] t = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                switch (t[0])
                {
                    case "v": v.Add(new[] { F(t[1]), F(t[2]), F(t[3]) }); break;
                    case "vt": vt.Add(new[] { F(t[1]), t.Length > 2 ? F(t[2]) : 0 }); break;
                    case "vn": vn.Add(new[] { F(t[1]), F(t[2]), F(t[3]) }); break;
                    case "f":
                        var idx = t.Skip(1).Select(Vertex).ToList();
                        // Y flip mirrors the mesh, so reverse the winding to keep faces facing out.
                        for (int i = 1; i + 1 < idx.Count; i++) mesh.Triangles.Add(yUp ? (idx[0], idx[i], idx[i + 1]) : (idx[0], idx[i + 1], idx[i]));
                        break;
                }
            }
            // Fill missing normals from the faces.
            var acc = mesh.Positions.Select(_ => new float[3]).ToList();
            foreach (var (a, b, c) in mesh.Triangles)
            {
                var n = Cross(mesh.Positions[a], mesh.Positions[b], mesh.Positions[c]);
                foreach (int k in new[] { a, b, c }) for (int d = 0; d < 3; d++) acc[k][d] += n[d];
            }
            for (int k = 0; k < mesh.Normals.Count; k++)
                if (mesh.Normals[k].All(x => x == 0)) mesh.Normals[k] = Normalise(acc[k]);
            return mesh;
        }

        /// <summary>A primitive (plane, cube) of <paramref name="size"/> centred at <paramref name="center"/>, model space.</summary>
        public static InputMesh Primitive(string kind, float size, float[] center)
        {
            var m = new InputMesh();
            float h = size / 2;
            void Quad(float[][] p, float[] n)
            {
                int b = m.Positions.Count;
                float[][] uv = { new[] { 0f, 0f }, new[] { 1f, 0f }, new[] { 1f, 1f }, new[] { 0f, 1f } };
                for (int i = 0; i < 4; i++)
                {
                    m.Positions.Add(new[] { center[0] + p[i][0], center[1] + p[i][1], center[2] + p[i][2] });
                    m.Normals.Add(n); m.Uvs.Add(uv[i]); m.Colors.Add(new byte[] { 128, 128, 128, 128 });
                }
                m.Triangles.Add((b, b + 1, b + 2));
                m.Triangles.Add((b, b + 2, b + 3));
            }
            switch (kind.ToLowerInvariant())
            {
                case "plane":
                    // Model space is Y up: a floor faces +Y (counter-clockwise seen from above).
                    Quad(new[] { new[] { -h, 0f, h }, new[] { h, 0f, h }, new[] { h, 0f, -h }, new[] { -h, 0f, -h } }, new[] { 0f, 1f, 0f });
                    break;
                case "cube":
                    Quad(new[] { new[] { -h, -h, h }, new[] { h, -h, h }, new[] { h, h, h }, new[] { -h, h, h } }, new[] { 0f, 0f, 1f });
                    Quad(new[] { new[] { h, -h, -h }, new[] { -h, -h, -h }, new[] { -h, h, -h }, new[] { h, h, -h } }, new[] { 0f, 0f, -1f });
                    Quad(new[] { new[] { h, -h, h }, new[] { h, -h, -h }, new[] { h, h, -h }, new[] { h, h, h } }, new[] { 1f, 0f, 0f });
                    Quad(new[] { new[] { -h, -h, -h }, new[] { -h, -h, h }, new[] { -h, h, h }, new[] { -h, h, -h } }, new[] { -1f, 0f, 0f });
                    Quad(new[] { new[] { -h, -h, -h }, new[] { h, -h, -h }, new[] { h, -h, h }, new[] { -h, -h, h } }, new[] { 0f, -1f, 0f });
                    Quad(new[] { new[] { -h, h, h }, new[] { h, h, h }, new[] { h, h, -h }, new[] { -h, h, -h } }, new[] { 0f, 1f, 0f });
                    break;
                default:
                    throw new ArgumentException($"Unknown primitive '{kind}' (plane, cube)");
            }
            return m;
        }

        private static float[] Cross(float[] p0, float[] p1, float[] p2)
        {
            float[] e1 = { p1[0] - p0[0], p1[1] - p0[1], p1[2] - p0[2] }, e2 = { p2[0] - p0[0], p2[1] - p0[1], p2[2] - p0[2] };
            return new[] { e1[1] * e2[2] - e1[2] * e2[1], e1[2] * e2[0] - e1[0] * e2[2], e1[0] * e2[1] - e1[1] * e2[0] };
        }

        private static float[] Normalise(float[] n)
        {
            float l = MathF.Sqrt(n[0] * n[0] + n[1] * n[1] + n[2] * n[2]);
            return l > 0 ? new[] { n[0] / l, n[1] / l, n[2] / l } : new[] { 0f, -1f, 0f };
        }
    }
}
