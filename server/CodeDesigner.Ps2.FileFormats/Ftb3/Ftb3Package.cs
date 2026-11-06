using CodeDesigner.Ps2.FileFormats.Assets;
using CodeDesigner.Ps2.FileFormats.Zipper.Ca;

namespace CodeDesigner.Ps2.FileFormats.Ftb3
{
    /// <summary>
    /// SOCOM: U.S. Navy SEALs Fireteam Bravo 3 (PSP, UCUS-98716), read-only: the disc's levels
    /// (<c>PSP_GAME\USRDIR\levels\&lt;level&gt;</c>), each a set of .ssg files. A level's own <c>&lt;level&gt;.ssg</c> holds
    /// its models (type 0x0B, each with the mesh pieces before it, type 0x00) and some textures (0x01), <c>_vram.ssg</c>
    /// the rest of its textures and <c>_inst.ssg</c> its placed instances (0x07). See docs/formats/ftb3.md.
    /// </summary>
    public sealed class Ftb3Package
    {
        public const string Game = "SOCOM: Fireteam Bravo 3";

        /// <summary>A model of a level: its name (the entry name, object then shape), kind (<c>world</c>: drawn where its
        /// vertices are; <c>prop</c>: placed by instances; <c>lod</c>, <c>sky</c>), the level and its model entry (an index into the level archive's entries).</summary>
        public sealed record Model(string Name, string Kind, string Level, int Entry, int Pieces, int Vertices);

        /// <summary>A placed instance: its entry name, the model it draws (null for a marker without one) and its matrix
        /// (row vectors, translation in [12..14]).</summary>
        public sealed record Instance(string Id, string Name, string? Model, float[] Matrix);

        public sealed record Texture(string Name, uint Hash, int Width, int Height, int Depth, byte[] Rgba, bool HasAlpha);

        /// <summary>The disc folder (<c>PSP_GAME\USRDIR</c>) or level folder the package was opened from.</summary>
        public string SourcePath { get; }

        /// <summary>The level folders, by name (<c>Valley_multi</c> → its full path).</summary>
        public IReadOnlyDictionary<string, string> Levels { get; }

        private readonly Dictionary<string, LevelData> _loaded = new(StringComparer.OrdinalIgnoreCase);

        private Ftb3Package(string source, Dictionary<string, string> levels)
        {
            SourcePath = source;
            Levels = levels;
        }

        /// <summary>A level folder (it holds <c>&lt;name&gt;.ssg</c>), a .ssg file in one, the disc root or its
        /// <c>PSP_GAME\USRDIR</c> or <c>levels</c> folder.</summary>
        public static bool IsFtb3Path(string path) => LevelsOf(path).Count > 0;

        public static Ftb3Package Open(string path)
        {
            var levels = LevelsOf(path);
            if (levels.Count == 0) throw new InvalidDataException($"{path} is not a Fireteam Bravo 3 level or disc");
            return new Ftb3Package(Path.GetFullPath(path), levels);
        }

        private static Dictionary<string, string> LevelsOf(string path)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(path))
            {
                if (!path.EndsWith(".ssg", StringComparison.OrdinalIgnoreCase) || !SsgArchive.IsSsg(path)) return map;
                path = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";
            }
            if (!Directory.Exists(path)) return map;
            if (IsLevel(path)) { map[Path.GetFileName(Path.TrimEndingDirectorySeparator(path))] = Path.GetFullPath(path); return map; }
            foreach (string root in new[] { path, Path.Combine(path, "levels"), Path.Combine(path, "USRDIR", "levels"), Path.Combine(path, "PSP_GAME", "USRDIR", "levels") })
            {
                if (!Directory.Exists(root)) continue;
                foreach (string d in Directory.GetDirectories(root).Where(IsLevel).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                    map[Path.GetFileName(d)] = Path.GetFullPath(d);
                if (map.Count > 0) break;
            }
            return map;
        }

        // A level folder holds <name>.ssg next to <name>_inst.ssg.
        private static bool IsLevel(string dir)
        {
            string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(dir));
            return name.Length > 0 && File.Exists(Path.Combine(dir, name + ".ssg")) && File.Exists(Path.Combine(dir, name + "_inst.ssg"));
        }

        /// <summary>A level's kind from its folder name: <c>_mis</c> campaign (SP), <c>_multi</c> online (MP),
        /// <c>_cust</c> custom (the campaign map with online modes), others other.</summary>
        public static string LevelKind(string level) =>
            level.EndsWith("_mis", StringComparison.OrdinalIgnoreCase) || level.EndsWith("_mis_old", StringComparison.OrdinalIgnoreCase) ? "SP"
            : level.EndsWith("_multi", StringComparison.OrdinalIgnoreCase) ? "MP"
            : level.EndsWith("_cust", StringComparison.OrdinalIgnoreCase) ? "custom" : "other";

        // ------------------------------------------------------------------ levels

        private sealed class LevelData
        {
            public required SsgArchive Meshes { get; init; }
            public SsgArchive? Vram { get; init; }
            public SsgArchive? Placements { get; init; }
            public List<Model> Models { get; } = new();
            public Dictionary<uint, (SsgArchive Archive, SsgArchive.Entry Entry)> TexturesByHash { get; } = new();
            public Dictionary<string, (SsgArchive Archive, SsgArchive.Entry Entry)> TexturesByName { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, ModelMesh> Meshes2 { get; } = new(StringComparer.OrdinalIgnoreCase);
            public List<Instance>? Instances { get; set; }
        }

        private LevelData Load(string level)
        {
            if (_loaded.TryGetValue(level, out var hit)) return hit;
            if (!Levels.TryGetValue(level, out var dir)) throw new KeyNotFoundException($"No level {level}");
            string name = Path.GetFileName(dir);
            SsgArchive? Optional(string suffix) => File.Exists(Path.Combine(dir, name + suffix)) ? SsgArchive.Open(Path.Combine(dir, name + suffix)) : null;
            var data = new LevelData { Meshes = SsgArchive.Open(Path.Combine(dir, name + ".ssg")), Vram = Optional("_vram.ssg"), Placements = Optional("_inst.ssg") };
            // Textures: the level's own (most of a campaign level's), then _vram.ssg's.
            foreach (var a in new[] { data.Meshes, data.Vram })
                if (a != null)
                    foreach (var e in a.Entries.Where(e => e.Type == SsgArchive.Texture && e.Size >= 0x20))
                    {
                        data.TexturesByHash.TryAdd(a.U32(e.Offset), (a, e));
                        data.TexturesByName.TryAdd(e.Name, (a, e));
                    }
            var instanced = Instances(data, level).Where(i => i.Model != null).Select(i => i.Model!).ToHashSet(StringComparer.Ordinal);
            for (int i = 0; i < data.Meshes.Entries.Count; i++)
            {
                var e = data.Meshes.Entries[i];
                if (e.Type != SsgArchive.Model) continue;
                var list = PiecesOf(data.Meshes, i);
                int vertices = list.Sum(p => (int)data.Meshes.U32(p.Offset + 0x68));
                string kind = e.Name.EndsWith("_LOD", StringComparison.OrdinalIgnoreCase) ? "lod"
                    : IsSky(e.Name) ? "sky"
                    : instanced.Contains(e.Name) ? "prop" : "world";
                data.Models.Add(new Model(e.Name, kind, level, i, list.Count, vertices));
            }
            return _loaded[level] = data;
        }

        private static bool IsSky(string name) =>
            name.Contains("Skydome", StringComparison.OrdinalIgnoreCase) || name.Contains("parallax", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// A model's mesh pieces: the type-0x00 entries before its model entry (0x70 bytes: +0x04 the piece count, +0x10
        /// / +0x20 its bounds, +0x4C its position scale, 256) that share its name.
        /// </summary>
        private static List<SsgArchive.Entry> PiecesOf(SsgArchive a, int model)
        {
            var list = new List<SsgArchive.Entry>();
            string name = a.Entries[model].Name;
            for (int i = model - 1; i >= 0 && a.Entries[i].Type == SsgArchive.MeshPiece && a.Entries[i].Name == name; i--)
                list.Add(a.Entries[i]);
            list.Reverse();
            return list;
        }

        public List<Model> Models(string level) => Load(level).Models;

        public Model? FindModel(string level, string name) => Load(level).Models.FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// The level's placed instances (<c>_inst.ssg</c>, type 0x07): +0x00 a 4×4 matrix (row vectors, translation in
        /// row 3), +0x40 1, +0x48 -1, +0x50 the model's entry name (<c>Grass_TallGrass_Tall</c>). An instance whose
        /// name is no model (<c>AlphaSealControl</c>, <c>EchoMercControl</c>) marks a place; its model is null.
        /// Entry names put a number before a bar (<c>161|Grass_Tall</c>) to keep copies apart.
        /// </summary>
        public List<Instance> Instances(string level) => Instances(Load(level), level);

        private static List<Instance> Instances(LevelData data, string level)
        {
            if (data.Instances != null) return data.Instances;
            var list = new List<Instance>();
            var models = data.Meshes.Entries.Where(e => e.Type == SsgArchive.Model).Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
            var a = data.Placements;
            if (a != null)
            {
                var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in a.Entries.Where(e => e.Type == SsgArchive.Instance && e.Size >= 0x51))
                {
                    var m = new float[16];
                    for (int i = 0; i < 16; i++) m[i] = a.F32(e.Offset + 4 * i);
                    if (!m.All(float.IsFinite)) continue;
                    string model = a.Str(e.Offset + 0x50, e.Size - 0x50);
                    int n = seen.GetValueOrDefault(e.Name);
                    seen[e.Name] = n + 1;
                    list.Add(new Instance(n == 0 ? e.Name : $"{e.Name}~{n}", e.Name, models.Contains(model) ? model : null, m));
                }
            }
            return data.Instances = list;
        }

        // ------------------------------------------------------------------ meshes

        /// <summary>
        /// A model in its own space (positions times its scale, 256). Each mesh piece is one chunk: +0x00 its texture's
        /// hash (a <c>_vram.ssg</c> record's +0x00), +0x10 / +0x20 bounds, +0x40 GE commands (VTYPE, command 0x12), +0x50
        /// the strip count (low half), +0x58 / +0x5C the UV scale and offset (16, -16: a u16 UV of 0x8000 is 0), +0x68 the
        /// vertex count; strips from +0x70, each a 0x20-byte header (+0x00 a
        /// bounding sphere, +0x18 its vertex bytes, +0x1C the primitive: GE type in the high half, 4 = strip, and the
        /// vertex count in the low half) and its vertices.
        /// </summary>
        public ModelMesh BuildMesh(Model model)
        {
            var data = Load(model.Level);
            if (data.Meshes2.TryGetValue(model.Name, out var hit)) return hit;
            var a = data.Meshes;
            var entry = a.Entries[model.Entry];
            float scale = a.F32(entry.Offset + 0x4C) is > 0 and < 65536 and var k ? k : 256f;
            var world = new[] { scale, 0, 0, 0, 0, scale, 0, 0, 0, 0, scale, 0, 0, 0, 0, 1f };
            var mesh = new ModelMesh { Archive = model.Level, Name = model.Name };
            foreach (var piece in PiecesOf(a, model.Entry))
                if (Piece(a, piece, world, mesh.Chunks.Count, TextureName(data, a.U32(piece.Offset))) is { } chunk) mesh.Chunks.Add(chunk);
            mesh.Recompute();
            return data.Meshes2[model.Name] = mesh;
        }

        private static string TextureName(LevelData data, uint hash) =>
            data.TexturesByHash.TryGetValue(hash, out var t) ? t.Entry.Name : $"ftb3_{hash:x8}";

        /// <summary>One mesh piece as a chunk (null when it draws nothing). Strips wind counter-clockwise from their first
        /// triangle.</summary>
        public static ModelMesh.MeshChunk? Piece(SsgArchive a, SsgArchive.Entry piece, float[] world, int index, string? texture)
        {
            var d = a.Data;
            int o = piece.Offset, end = piece.Offset + piece.Size;
            uint vtype = 0;
            for (int k = 0x40; k < 0x50; k += 4) if (a.U32(o + k) >> 24 == 0x12) vtype = a.U32(o + k) & 0xFFFFFF;
            var fmt = new PspGe.VertexFormat(vtype);
            if (fmt.Stride == 0) return null;
            int strips = (int)(a.U32(o + 0x50) & 0xFFFF);
            var pos = new List<float>(); var uv = new List<float>(); var nrm = new List<float>(); var col = new List<byte>();
            var tris = new List<int>();
            int p = o + 0x70;
            for (int s = 0; s < strips && p + 0x20 <= end; s++)
            {
                int bytes = (int)a.U32(p + 0x18);
                uint prim = a.U32(p + 0x1C);
                int n = (int)(prim & 0xFFFF), type = (int)((prim >> 16) & 7);
                int v = p + 0x20;
                if (bytes < 0 || v + bytes > end || n * fmt.Stride > bytes) break;
                int first = pos.Count / 3;
                for (int i = 0; i < n; i++) fmt.Read(d, v + i * fmt.Stride, world, pos, uv, nrm, col);
                void Tri(int x, int y, int z) { if (x != y && y != z && x != z) { tris.Add(first + x); tris.Add(first + y); tris.Add(first + z); } }
                if (type == 4) for (int i = 0; i + 2 < n; i++) { if ((i & 1) == 0) Tri(i, i + 1, i + 2); else Tri(i + 1, i, i + 2); }
                else if (type == 3) for (int i = 0; i + 2 < n; i += 3) Tri(i, i + 1, i + 2);
                else if (type == 5) for (int i = 1; i + 1 < n; i++) Tri(0, i, i + 1);
                p = v + bytes;
            }
            if (tris.Count == 0) return null;
            float uvScale = a.F32(o + 0x58), uvOffset = a.F32(o + 0x5C);
            if (!float.IsFinite(uvScale) || uvScale == 0 || !float.IsFinite(uvOffset)) (uvScale, uvOffset) = (1, 0);
            var uvs = uv.ToArray();
            for (int i = 0; i < uvs.Length; i++) uvs[i] = uvs[i] * uvScale + uvOffset;
            for (int i = 1; i < uvs.Length; i += 2) uvs[i] = 1f - uvs[i];
            return new ModelMesh.MeshChunk
            {
                Index = index, GeometryOffset = piece.Offset, Visual = $"piece{index}", Texture = texture,
                Unit = "Float", Matrix = Matrix4.Identity(), Scale = 1f, VertexCount = pos.Count / 3,
                Positions = pos.ToArray(), Uvs = uvs, Normals = nrm.ToArray(), Colors = fmt.Colour != 0 ? col.ToArray() : null,
                Triangles = tris.ToArray(),
            };
        }

        // ------------------------------------------------------------------ textures

        /// <summary>A level's texture by its entry name or <c>ftb3_&lt;hash&gt;</c>.</summary>
        public Texture? FindTexture(string level, string name)
        {
            var data = Load(level);
            if (data.TexturesByName.TryGetValue(name, out var byName)) return DecodeTexture(byName.Archive, byName.Entry);
            return name.StartsWith("ftb3_", StringComparison.OrdinalIgnoreCase) && uint.TryParse(name[5..], System.Globalization.NumberStyles.HexNumber, null, out uint h)
                && data.TexturesByHash.TryGetValue(h, out var byHash) ? DecodeTexture(byHash.Archive, byHash.Entry) : null;
        }

        public IEnumerable<string> TextureNames(string level) => Load(level).TexturesByName.Keys;

        /// <summary>
        /// A texture record (type 0x01): +0x00 its hash (what mesh pieces name), +0x04 width (low half) and height,
        /// +0x08 flags (bit 1, 2: drawn with alpha), +0x0C its CLUT's entry count (bits 16-27), +0x10 bits per pixel (top
        /// byte: 4, 8 or 32), +0x14 CLUT and pixel bytes, +0x18 pixel bytes; the CLUT from +0x20 (32-bit RGBA; none at
        /// 32 bits), then the pixels (swizzled in the GE's 16-byte × 8-row blocks).
        /// </summary>
        public static Texture? DecodeTexture(SsgArchive a, SsgArchive.Entry e)
        {
            int o = e.Offset;
            int w = (int)(a.U32(o + 4) & 0xFFFF), h = (int)(a.U32(o + 4) >> 16);
            int bpp = (int)(a.U32(o + 0x10) >> 24), pixels = (int)a.U32(o + 0x18), total = (int)a.U32(o + 0x14);
            if (w is <= 0 or > 1024 || h is <= 0 or > 1024 || bpp is not (4 or 8 or 32) || 0x20 + total > e.Size || pixels > total) return null;
            // 32 bits per pixel: RGBA8888 texels and no CLUT (a few metal trims: Train_Tanker_01f).
            int entries = bpp == 4 ? 16 : bpp == 8 ? 256 : 0;
            int stride = Math.Max(16, w * bpp / 8);
            if (stride * h > pixels) return null;
            int clut = o + 0x20;
            var lin = PspGe.Unswizzle(a.Data, clut + (total - pixels), stride, h);
            var lut = new uint[entries];
            for (int i = 0; i < entries && clut + 4 * i + 4 <= o + e.Size; i++) lut[i] = a.U32(clut + 4 * i);
            var rgba = new byte[w * h * 4];
            bool alpha = false;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    uint c = bpp == 32 ? BitConverter.ToUInt32(lin, y * stride + 4 * x)
                        : lut[bpp == 8 ? lin[y * stride + x] : (lin[y * stride + x / 2] >> ((x & 1) * 4)) & 15];
                    int i = (y * w + x) * 4;
                    rgba[i] = (byte)c; rgba[i + 1] = (byte)(c >> 8); rgba[i + 2] = (byte)(c >> 16); rgba[i + 3] = (byte)(c >> 24);
                    if ((c >> 24) < 0xFF) alpha = true;
                }
            return new Texture(e.Name, a.U32(o), w, h, bpp, rgba, alpha);
        }
    }
}
