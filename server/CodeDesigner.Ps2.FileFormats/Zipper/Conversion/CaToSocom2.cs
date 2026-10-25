using CodeDesigner.Ps2.FileFormats.Assets;
using System.Buffers.Binary;
using CodeDesigner.Ps2.FileFormats.Zipper.Anim;
using CodeDesigner.Ps2.FileFormats.Zipper.Ca;
using CodeDesigner.Ps2.FileFormats.Zipper.Geo;
using CodeDesigner.Ps2.FileFormats.Zipper.Package;
using CodeDesigner.Ps2.FileFormats.Zipper.ZED;

namespace CodeDesigner.Ps2.FileFormats.Zipper.Conversion
{
    /// <summary>
    /// SOCOM 3 / Combined Assault model -> SOCOM II pack (<c>&lt;PACK&gt;.CDB</c> + its ZEDs), a C# port of the converters
    /// confirmed in game (Mods/SOCOMCA ca2s2.py, ca2s2static.py, s2mesh.py, s2static.py, ca_anim.py):
    /// <list type="bullet">
    /// <item>characters (<c>CLIB</c>): every vertex part re-expressed in the mapped bone (<see cref="CaBones"/>) of the
    /// retail alg_UN01 skeleton; CA's draw blocks copied as they are (same GS tags and vertex-buffer layout);</item>
    /// <item>weapons (<c>WEAP</c>), vehicles (<c>BCAR</c>), props (<c>PROP</c>): CA's geometry chunks copied as they are
    /// (the same format) under a GEO tree mirroring the CA nodes, minus LOD copies / hull;</item>
    /// <item>textures: CA records -> SOCOM II texdat (4-bit -> 8-bit, 32-bit -> 16-bit), palettes -> texpal_&lt;id&gt; in
    /// the pack's own block of ids;</item>
    /// <item>clips: CA clips retargeted onto seal_stand's layout (world rotations per bone; still root, motion on the
    /// hips) into the pack's <c>MOTION_S.ZAR</c>.</item>
    /// </list>
    /// </summary>
    public static class CaToSocom2
    {
        public sealed record Options(string PackName, string ModelName, string OutputDir, IReadOnlyList<string>? Clips = null, int MaxTextureSize = 64);

        public sealed record Result(string CdbPath, string Pack, string Model, string Kind, string Library, int Triangles,
            int Textures, int Palettes, IReadOnlyList<string> Clips, IReadOnlyList<string> Warnings);

        internal const float CharacterScale = 4096f;
        internal const int MtxCount = 26;

        /// <summary>The SOCOM II kind a CA model converts to: character, weapon, vehicle (veh_* libraries) or prop.</summary>
        public static string KindOf(CaPackage.Model model) => model.Kind switch
        {
            "character" => "character",
            "weapon" => "weapon",
            "prop" when model.Resource.StartsWith("veh_", StringComparison.OrdinalIgnoreCase) => "vehicle",
            "prop" => "prop",
            _ => throw new NotSupportedException($"{model.Name}: a {model.Kind} can't be exported to SOCOM II (characters, weapons, vehicles and props can)"),
        };

        /// <summary>A pack name for a model: 7 characters starting with m (the memory-card loader's limit).</summary>
        public static string DefaultPackName(CaPackage.Model model) => $"mc{CaNames.Crc32(model.Id) & 0xFFFFF:x5}";

        public static Result Export(CaPackage pkg, CaPackage.Model model, Socom2Templates templates, Options o)
        {
            PackageSave.CheckPackName(o.PackName);
            string kind = KindOf(model);
            var warnings = new List<string>();
            var files = new List<(string Leaf, byte[] Data)>();
            string library;
            int triangles, textures, palettes;
            if (kind == "character")
                (library, triangles, textures, palettes) = Character(pkg, model, templates, o, files, warnings);
            else
                (library, triangles, textures, palettes) = Static(pkg, model, kind, templates, o, files, warnings);

            var clips = new List<string>();
            if (o.Clips is { Count: > 0 })
            {
                var (header, stand) = templates.ClipTemplate();
                var records = new List<ZedTree.Record>();
                foreach (string name in o.Clips)
                {
                    var clip = pkg.Animations().FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                        ?? throw new KeyNotFoundException($"Clip '{name}' not found");
                    records.Add(new ZedTree.Record(clip.Name.Length > 63 ? clip.Name[..63] : clip.Name, Retarget(clip, stand).Encode()));
                    clips.Add(clip.Name);
                }
                files.Add((PackageAnimations.PackageLeaf, Socom2Templates.WriteZed(header, records)));
            }

            string pack = o.PackName.ToUpperInvariant();
            string dir = Path.Combine(o.OutputDir, o.PackName);
            Directory.CreateDirectory(dir);
            foreach (var (leaf, data) in files) File.WriteAllBytes(Path.Combine(dir, leaf), data);
            byte[] cdb = PackageSave.BuildCdb(files.Select(f => ($"RUN\\MP\\{pack}\\{f.Leaf}", f.Data)).ToList());
            string path = Path.Combine(dir, pack + ".CDB");
            File.WriteAllBytes(path, cdb);
            return new Result(path, o.PackName, o.ModelName, kind, library, triangles, textures, palettes, clips, warnings);
        }

        // ================================================================== characters

        private sealed record VertexBatch(short Flags, short Tris, short Bone, short[][] Verts);
        private sealed record RawGroup(List<VertexBatch> Vertex, byte[] Draw);
        private sealed record RawSection(uint Texture, List<RawGroup> Groups);

        /// <summary>ca2s2.ca_groups: a CA mesh object's sections (texture hash) of groups (vertex batches + the raw draw block).</summary>
        private static List<RawSection> RawGroups(byte[] d, int start, int end)
        {
            end = Math.Min(end, d.Length);
            bool BatchAt(int p) => p + 8 <= end && BitConverter.ToUInt32(d, p) == 0x01000101 && ((BitConverter.ToUInt32(d, p + 4) >> 24) & 0x7F) == 0x6D;
            var sections = new List<RawSection>();
            int o = start;
            while (o < end - 8)
            {
                if (!BatchAt(o)) { o += 16; continue; }
                var sec = new RawSection(o >= 16 ? BitConverter.ToUInt32(d, o - 16) : 0, new List<RawGroup>());
                int p = o;
                while (BatchAt(p))
                {
                    var vertex = new List<VertexBatch>();
                    int? drawAt = null, next = null;
                    byte[]? draw = null;
                    int w = p;
                    while (w + 4 <= end)
                    {
                        uint word = BitConverter.ToUInt32(d, w);
                        int cmd = (int)((word >> 24) & 0x7F), num = (int)((word >> 16) & 0xFF), imm = (int)(word & 0xFFFF);
                        if (cmd >= 0x60)
                        {
                            if (cmd is not (0x6E or 0x6C or 0x65 or 0x6D)) break;
                            int vn = (cmd >> 2) & 3, vl = cmd & 3;
                            int elem = vl == 3 ? 2 : (vl switch { 0 => 4, 1 => 2, _ => 1 }) * (vn + 1);
                            int n = num == 0 ? 256 : num, at = w + 4, len = (elem * n + 3) / 4 * 4;
                            if (at + len > end) break;
                            if (cmd == 0x6D)
                            {
                                short flags = BitConverter.ToInt16(d, at), tris = BitConverter.ToInt16(d, at + 2), bone = BitConverter.ToInt16(d, at + 4), count = BitConverter.ToInt16(d, at + 6);
                                var verts = new short[Math.Max(0, (int)count)][];
                                for (int i = 0; i < verts.Length; i++)
                                {
                                    verts[i] = new short[8];
                                    for (int k = 0; k < 8; k++) verts[i][k] = BitConverter.ToInt16(d, at + 16 + 16 * i + 2 * k);   // 2 header elements
                                }
                                vertex.Add(new VertexBatch(flags, tris, bone, verts));
                            }
                            w = at + len;
                        }
                        else if (cmd == 0x01) { if (imm == 0x102 && drawAt == null) drawAt = w; w += 4; }
                        else if (cmd == 0x17)
                        {
                            if (drawAt != null)
                            {
                                var block = d[drawAt.Value..(w + 4)];
                                draw = new byte[(block.Length + 15) & ~15];
                                Buffer.BlockCopy(block, 0, draw, 0, block.Length);
                                next = (w + 4 + 15) & ~15;
                                break;
                            }
                            w += 4;
                        }
                        else if (cmd == 0x00) w += 4;
                        else break;
                    }
                    if (next == null || draw == null) break;
                    sec.Groups.Add(new RawGroup(vertex, draw));
                    p = next.Value;
                }
                sections.Add(sec);
                o = Math.Max(o + 16, p + 16);
            }
            return sections;
        }

        // Bones that place the body: compared with the retail skeleton to tell an A-pose bind from a posed one.
        private static readonly uint[] PoseBones = { 0x7EE3A92F, 0xA7F3F69C, 0x45780C10, 0x9586FDE0, 0x3577BB50, 0x3A34EDF2, 0x0EE0BC99, 0x7A12CE1D, 0x19E5B48F, 0xA152AC8B, 0xC21990B4, 0xA1EEEA26 };

        private static float PoseDeviation(List<CaLibrary.Node> nodes, Socom2Templates.Character tpl)
        {
            float dev = 0;
            foreach (var n in nodes)
                if (PoseBones.Contains(n.Hash) && CaBones.Known.TryGetValue(n.Hash, out var b) && tpl.Skeleton.Bones.FirstOrDefault(x => x.Name == b.Socom2) is { } s)
                    dev = MathF.Max(dev, MathF.Sqrt(Sq(n.World[12] - s.World[12]) + Sq(n.World[13] - s.World[13]) + Sq(n.World[14] - s.World[14])));
            return dev;
        }

        /// <summary>Bind world matrix per node. A model whose stored skeleton is posed (menu / victory poses) takes the
        /// bind of the library's closest A-pose character (vertex positions are bone-relative: only the frame matters).</summary>
        private static List<double[]> BindPose(CaLibrary lib, CaPackage.Model model, List<CaLibrary.Node> nodes, Socom2Templates.Character tpl, CaPackage pkg, List<string> warnings)
        {
            if (PoseDeviation(nodes, tpl) <= 1.5f) return Worlds(nodes.Select(n => (n.Parent, n.Local)).ToList());
            var reference = pkg.Models().Where(m => m.Kind == "character" && m.LibraryIndex == model.LibraryIndex && m.Offset != model.Offset)
                .Select(m => lib.Nodes(m.Offset)).Select(ns => (Nodes: ns, Dev: PoseDeviation(ns, tpl))).Where(x => x.Dev <= 1.5f)
                .OrderBy(x => x.Dev).FirstOrDefault();
            if (reference.Nodes == null)
            {
                warnings.Add("the stored skeleton is posed and the library has no A-pose character to take the bind from: the mesh keeps the pose");
                return Worlds(nodes.Select(n => (n.Parent, n.Local)).ToList());
            }
            warnings.Add("the stored skeleton is posed: bind taken from an A-pose character of the same library");
            var refWorlds = Worlds(reference.Nodes.Select(n => (n.Parent, n.Local)).ToList());
            var byHash = new Dictionary<uint, double[]>();
            for (int i = 0; i < reference.Nodes.Count; i++) byHash.TryAdd(reference.Nodes[i].Hash, refWorlds[i]);
            var bind = new List<double[]>();
            foreach (var n in nodes)
                bind.Add(byHash.TryGetValue(n.Hash, out var w) ? w : Mul(n.Parent >= 0 ? bind[n.Parent] : Identity(), n.Local.Select(x => (double)x).ToArray()));
            return bind;
        }

        /// <summary>World matrices (column-major, double precision) of bones listed parents first.</summary>
        private static List<double[]> Worlds(List<(int Parent, float[] Local)> bones)
        {
            var list = new List<double[]>();
            foreach (var (parent, local) in bones)
                list.Add(Mul(parent >= 0 ? list[parent] : Identity(), local.Select(x => (double)x).ToArray()));
            return list;
        }

        private static double[] Identity() => new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };

        private static double[] Mul(double[] a, double[] b)
        {
            var r = new double[16];
            for (int c = 0; c < 4; c++)
                for (int row = 0; row < 4; row++)
                    r[c * 4 + row] = a[row] * b[c * 4] + a[4 + row] * b[c * 4 + 1] + a[8 + row] * b[c * 4 + 2] + a[12 + row] * b[c * 4 + 3];
            return r;
        }

        private static (string, int, int, int) Character(CaPackage pkg, CaPackage.Model model, Socom2Templates templates, Options o,
            List<(string, byte[])> files, List<string> warnings)
        {
            var tpl = templates.CharacterTemplate();
            var lib = pkg.Libraries[model.LibraryIndex].Lib;
            var nodes = lib.Nodes(model.Offset);
            var bind = BindPose(lib, model, nodes, tpl, pkg, warnings);
            var mids = tpl.Skeleton.Bones.Where(b => b.MatrixId >= 0).GroupBy(b => b.Name).ToDictionary(g => g.Key, g => g.First());
            var tplWorld = Worlds(tpl.Skeleton.Bones.Select(b => (b.Parent, b.Local)).ToList());
            int clamped = 0;
            short Clamp(double v)
            {
                long r = (long)Math.Round(v);
                if (r is < short.MinValue or > short.MaxValue) clamped++;
                return (short)Math.Clamp(r, short.MinValue, short.MaxValue);
            }

            // The SOCOM II bone of a CA node: its own mapping, else its nearest mapped ancestor's.
            CharacterSkeleton.Bone S2Bone(int node)
            {
                for (int n = node; n >= 0; n = nodes[n].Parent)
                    if (CaBones.Known.TryGetValue(nodes[n].Hash, out var b) && mids.TryGetValue(b.Socom2, out var s)) return s;
                return mids["body"];
            }

            var sections = new List<(string Texture, List<(List<(int Mid, byte[] Block)> Vertex, byte[] Draw)> Groups)>();
            var usedTextures = new List<uint>();
            int triangles = 0;
            foreach (var node in nodes)
                foreach (int obj in node.Objects)
                {
                    int size = (int)lib.U32(obj + 0x1C);
                    if (size <= 0) continue;
                    foreach (var sec in RawGroups(lib.Data, obj, obj + 0x80 + size))
                    {
                        if (!usedTextures.Contains(sec.Texture)) usedTextures.Add(sec.Texture);
                        var groups = new List<(List<(int, byte[])>, byte[])>();
                        foreach (var g in sec.Groups)
                        {
                            var blocks = new List<(int, byte[])>();
                            foreach (var vb in g.Vertex)
                            {
                                int caNode = Math.Clamp(vb.Bone + 1, 0, nodes.Count - 1);
                                var s2 = S2Bone(caNode);
                                double[] caWorld = bind[caNode], inv = RigidInverse(tplWorld[tpl.Skeleton.Bones.IndexOf(s2)]);
                                var verts = vb.Verts.Select(v =>
                                {
                                    // In double precision, like the Python converter (byte-identical output).
                                    var w = Apply(caWorld, v[0] / CharacterScale, v[1] / CharacterScale, v[2] / CharacterScale);
                                    var (lx, ly, lz) = Apply(inv, w.X, w.Y, w.Z);
                                    var rn = Rotate(caWorld, (v[4], v[5], v[6]));
                                    var (nx, ny, nz) = Rotate(inv, rn);
                                    return new[] { Clamp(lx * CharacterScale), Clamp(ly * CharacterScale), Clamp(lz * CharacterScale), v[3], Clamp(nx), Clamp(ny), Clamp(nz), v[7] };
                                }).ToArray();
                                blocks.Add((s2.MatrixId, VertexBlock(vb.Flags, vb.Tris, (short)s2.MatrixId, verts)));
                            }
                            triangles += DrawTriangles(g.Draw);
                            groups.Add((blocks, g.Draw));
                        }
                        sections.Add((CharacterTexture(sec.Texture), groups));
                    }
                }
            if (clamped > 0) warnings.Add($"{clamped} vertex values overflowed 16 bits and were clamped");
            var (data, start, refCount) = EncodeCharacterMesh(sections);

            var (texNodes, palNodes) = Textures(pkg, lib, usedTextures.Select(h => (h, CharacterTexture(h))).ToList(), o.PackName, tpl, null, warnings);
            var geo = new ZedTree.Record(o.ModelName, tpl.Geo.Data, tpl.Geo.Children);
            var mesh = new ZedTree.Record($"MESH_{o.ModelName}", data, new[]
            {
                new ZedTree.Record($"MESH_{o.ModelName}_START", BitConverter.GetBytes(start)),
                new ZedTree.Record("ref_count", BitConverter.GetBytes(refCount)),
                new ZedTree.Record("mtx_count", BitConverter.GetBytes(MtxCount)),
            });
            AddLibrary(files, "CLIB", tpl.Library, texNodes, palNodes, geo, mesh);
            return ("CLIB", triangles, texNodes.Count, palNodes.Count);
        }

        private static string CharacterTexture(uint hash) => $"ca_{hash:x8}.tif";

        private static int DrawTriangles(byte[] draw)
        {
            for (int w = 0; w + 4 <= draw.Length; w += 4)
                if ((BitConverter.ToUInt32(draw, w) >> 24 & 0x7F) == 0x6E) return (int)((BitConverter.ToUInt32(draw, w) >> 16) & 0xFF);
            return 0;
        }

        /// <summary>s2mesh.vertex_block: STCYCL 0x101, UNPACK V4-16 @4 (flg), header + verts, MSCNT, padded to 16.</summary>
        internal static byte[] VertexBlock(short flags, short tris, short bone, short[][] verts)
        {
            int n = 1 + 2 * verts.Length;
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(0x01000101u); w.Write(0x6D008004u | (uint)(n & 0xFF) << 16);
            w.Write(flags); w.Write(tris); w.Write(bone); w.Write((short)verts.Length);
            foreach (var v in verts) foreach (short s in v) w.Write(s);
            w.Write(0x17000000u);
            while (ms.Length % 16 != 0) w.Write((byte)0);
            return ms.ToArray();
        }

        /// <summary>s2mesh.encode: blocks then the DMA chain (end, per section cnt -> name, per batch matrix ref + vertex
        /// ref, per group draw ref). -> (data, chain start, ref count).</summary>
        internal static (byte[] Data, int Start, int RefCount) EncodeCharacterMesh(List<(string Texture, List<(List<(int Mid, byte[] Block)> Vertex, byte[] Draw)> Groups)> sections)
        {
            using var data = new MemoryStream();
            var chain = new List<byte[]>();
            byte[] Tag(uint a, uint b, uint c, uint d) { var t = new byte[16]; BinaryPrimitives.WriteUInt32LittleEndian(t, a); BinaryPrimitives.WriteUInt32LittleEndian(t.AsSpan(4), b); BinaryPrimitives.WriteUInt32LittleEndian(t.AsSpan(8), c); BinaryPrimitives.WriteUInt32LittleEndian(t.AsSpan(12), d); return t; }
            foreach (var (texture, groups) in sections)
            {
                chain.Add(Tag(0x10000000 | 6 << 16, (uint)data.Length, 0, 0));
                data.Write(NameField(texture));
                foreach (var (vertex, draw) in groups)
                {
                    foreach (var (mid, block) in vertex)
                    {
                        uint at = (uint)data.Length;
                        data.Write(block);
                        chain.Add(Tag(0x30090004, (uint)mid, 0x01000101, 0x6C048000));
                        chain.Add(Tag(0x300A0000 | (uint)(block.Length / 16), at, (uint)(ushort)BitConverter.ToInt16(block, 12), (uint)(ushort)BitConverter.ToInt16(block, 14)));
                    }
                    uint drawAt = (uint)data.Length;
                    data.Write(draw);
                    chain.Add(Tag(0x300B0000 | (uint)(draw.Length / 16), drawAt, (uint)(2 * vertex.Count), 0));
                }
            }
            int start = (int)data.Length;
            data.Write(Tag(0x70000000 | (uint)chain.Count, 0, 0, 0));
            foreach (var t in chain) data.Write(t);
            return (data.ToArray(), start, chain.Count);
        }

        internal static byte[] NameField(string name)
        {
            var raw = System.Text.Encoding.Latin1.GetBytes(name + "\0");
            var field = new byte[(raw.Length + 15) & ~15];
            Buffer.BlockCopy(raw, 0, field, 0, raw.Length);
            return field;
        }

        // Column-major 4x4 (translation in 12..14), double precision.
        private static (double X, double Y, double Z) Apply(double[] m, double x, double y, double z) =>
            (m[0] * x + m[4] * y + m[8] * z + m[12], m[1] * x + m[5] * y + m[9] * z + m[13], m[2] * x + m[6] * y + m[10] * z + m[14]);

        private static (double X, double Y, double Z) Rotate(double[] m, (double X, double Y, double Z) v) =>
            (m[0] * v.X + m[4] * v.Y + m[8] * v.Z, m[1] * v.X + m[5] * v.Y + m[9] * v.Z, m[2] * v.X + m[6] * v.Y + m[10] * v.Z);

        /// <summary>Inverse of a rotation + translation (ca2s2.rigid_inverse): transposed 3x3, translation -t R^T.</summary>
        private static double[] RigidInverse(double[] m)
        {
            var r = new double[16];
            for (int c = 0; c < 3; c++) for (int k = 0; k < 3; k++) r[c * 4 + k] = m[k * 4 + c];
            for (int k = 0; k < 3; k++) r[12 + k] = -(m[12] * m[k * 4] + m[13] * m[k * 4 + 1] + m[14] * m[k * 4 + 2]);
            r[15] = 1;
            return r;
        }

        private static float Sq(float v) => v * v;

        // ================================================================== static models

        private static readonly string[] NodeNames = { "firepoint", "firepoint_shell", "aimpoint", "Gun_box", "body", "turret", "gun", "wheel", "door",
            "seat", "driver", "gunner", "passenger", "hatch", "barrel", "rotor", "tail_rotor", "prop" };
        internal static readonly string[] HeldOrder = { "firepoint", "firepoint_shell", "aimpoint", "Gun_box" };

        internal sealed record StaticChunk(byte[] Colour, (uint, uint) ColourVif, byte[] Geometry);
        internal sealed record StaticSection(byte[] NameField, byte[] Material, (uint, uint) MaterialVif, List<StaticChunk> Chunks);

        private static (int Verts, int Tris) ChunkStats(byte[] g)
        {
            for (int o = 0; o + 8 + 64 <= g.Length; o += 16)
                if (BitConverter.ToUInt32(g, o) == 0x01000101 && BitConverter.ToUInt32(g, o + 4) == 0x6C048000)
                    return ((int)BitConverter.ToUInt32(g, o + 8 + 40), (int)BitConverter.ToUInt32(g, o + 8 + 44));
            return (0, 0);
        }

        private static (string, int, int, int) Static(CaPackage pkg, CaPackage.Model model, string kind, Socom2Templates templates, Options o,
            List<(string, byte[])> files, List<string> warnings)
        {
            var tpl = templates.StaticTemplate(kind);
            var chr = templates.CharacterTemplate();                       // 8 / 16-bit texdat samples
            var lib = pkg.Libraries[model.LibraryIndex].Lib;
            var nodes = lib.Nodes(model.Offset);
            var skip = pkg.SkippedNodes(model);
            bool weapon = tpl.Weapon;

            // visuals: one per mesh object, N<index among nodes with objects>_<object index>
            var visuals = new List<(string Key, List<StaticSection> Sections)>();
            var nodeVis = new Dictionary<int, List<(string Key, int Verts, int Tris, float[] Centre)>>();
            var usedTextures = new List<(uint Hash, string Name)>();
            int visIndex = 0, triangles = 0;
            foreach (var node in nodes)
            {
                var objs = skip.Contains(node.Offset) ? new List<int>() : node.Objects.Where(x => lib.U32(x) == CaLibrary.StaticObjectType).ToList();
                if (objs.Count == 0) continue;
                nodeVis[node.Offset] = new();
                for (int oi = 0; oi < objs.Count; oi++)
                {
                    int obj = objs[oi];
                    var secs = new List<StaticSection>();
                    int verts = 0, tris = 0;
                    var drawn = new HashSet<string>();
                    foreach (var s in lib.Sections(obj))
                    {
                        if (s.TextureRecord is not int rec) continue;
                        var geometry = s.Parts.Select(p => lib.Data[p.GeometryOffset..(p.GeometryOffset + p.GeometryLength)]).ToList();
                        string key = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(geometry.SelectMany(g => g).ToArray()));
                        if (!drawn.Add(key)) continue;                    // alternative chains over the same geometry (wheels)
                        var raw = lib.Raw(rec);
                        if (raw?.Psm == 0x00 && geometry.Sum(g => ChunkStats(g).Tris) <= 4) continue;   // rotor blur cards (alpha)
                        uint th = lib.U32(rec + 0x4C);
                        string texName = $"{o.PackName}_{th:x8}.tif";
                        if (!usedTextures.Any(t => t.Hash == th)) usedTextures.Add((th, texName));
                        var chunks = new List<StaticChunk>();
                        for (int i = 0; i < s.Parts.Count; i++)
                        {
                            var part = s.Parts[i];
                            var (v, t) = ChunkStats(geometry[i]);
                            verts += v; tris += t;
                            byte[]? colour = part.ColorOffset is int co ? lib.Data[co..(co + part.ColorLength)] : null;
                            var cup = part.ColorVif;
                            if (colour != null && !colour.Where((b, j) => j % 4 != 3).Any(b => b != 0)) colour = null;   // all black: CA lights at run time
                            if (colour == null)
                            {
                                colour = new byte[(4 * v + 15) & ~15];
                                for (int j = 0; j < 4 * v; j++) colour[j] = 0x80;
                                cup = (0x01000103u, 0x6E00C006u | (uint)(v & 0xFF) << 16);
                            }
                            chunks.Add(new StaticChunk(colour, cup, geometry[i]));
                        }
                        secs.Add(new StaticSection(NameField(texName), tpl.Material, tpl.MaterialVif, chunks));
                    }
                    string vkey = $"N{visIndex:000}_{oi:000}";
                    visuals.Add((vkey, secs));
                    var centre = new[] { BitConverter.ToSingle(lib.Data, obj + 0x10), BitConverter.ToSingle(lib.Data, obj + 0x14), BitConverter.ToSingle(lib.Data, obj + 0x18) };
                    nodeVis[node.Offset].Add((vkey, verts, tris, centre));
                    triangles += tris;
                }
                visIndex++;
            }

            // bounds in game units, written to every node (culling; spawn_prop puts the lowest point on the ground)
            var mesh = pkg.BuildMesh(model);
            float[] lo = mesh.Chunks.Count > 0 ? mesh.BoundsMin : new[] { -4f, -4f, -4f }, hi = mesh.Chunks.Count > 0 ? mesh.BoundsMax : new[] { 4f, 4f, 4f };

            var children = nodes.Select((n, i) => (n, i)).ToLookup(x => x.n.Parent, x => x.i);
            ZedTree.Record GeoNode(int i)
            {
                var n = nodes[i];
                string name = i == 0 ? o.ModelName : NodeNames.FirstOrDefault(x => CaNames.Crc32(x) == n.Hash) ?? $"n_{n.Hash:x8}";
                var kids = children[i].ToList();
                uint flags = weapon ? 0x80800055u : 0x80800051u;
                bool hasVis = nodeVis.ContainsKey(n.Offset);
                if (hasVis) { flags |= 0x40000; if (i > 0 && kids.Count == 0) flags &= ~0x800000u; }
                var recs = new List<ZedTree.Record> { new("nparams", NParams(n.Local, lo, hi, i == 0 ? 7u : 1u, flags)) };
                if (hasVis)
                    recs.Add(new ZedTree.Record("visuals", new byte[0], nodeVis[n.Offset].Select(v => new ZedTree.Record("vis", new byte[0],
                        new[] { new ZedTree.Record("vparams", VParams(weapon ? 0x000BC0FAu : 0x0009C0FAu, 0, v.Tris, v.Verts, v.Centre)) })).ToList()));
                if (kids.Count > 0) recs.Add(new ZedTree.Record("children", new byte[0], kids.Select(GeoNode).ToList()));
                return new ZedTree.Record(name, new byte[0], recs);
            }
            var geo = GeoNode(0);
            if (weapon) (geo, visuals) = WeaponShape(geo, visuals, o.ModelName, warnings);

            var (data, offsets) = EncodeStatic(visuals);
            var mdl = new ZedTree.Record(o.ModelName, data, visuals.Select(v => new ZedTree.Record(v.Key, BitConverter.GetBytes(offsets[v.Key]))).ToList());
            var (texNodes, palNodes) = Textures(pkg, lib, usedTextures, o.PackName, chr, o.MaxTextureSize, warnings);
            string libName = kind == "prop" ? "PROP" : tpl.Library.Name;
            AddLibrary(files, libName, tpl.Library, texNodes, palNodes, geo, mdl);
            return (libName, triangles, texNodes.Count, palNodes.Count);
        }

        internal static byte[] NParams(float[] l, float[] lo, float[] hi, uint kind, uint flags)
        {
            var b = new byte[96];
            float[] f = { l[0], l[1], l[2], 0, l[4], l[5], l[6], 0, l[8], l[9], l[10], 0, l[12], l[13], l[14], 1, lo[0], lo[1], lo[2], hi[0], hi[1], hi[2] };
            for (int i = 0; i < f.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(4 * i), f[i]);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(88), kind);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(92), flags);
            return b;
        }

        internal static byte[] VParams(uint flags, uint lod, int tris, int verts, float[] centre)
        {
            var b = new byte[24];
            BinaryPrimitives.WriteUInt32LittleEndian(b, flags);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), lod);
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(8), (ushort)Math.Min(tris, 0xFFFF));
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(10), (ushort)Math.Min(verts, 0xFFFF));
            for (int k = 0; k < 3; k++) BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(12 + 4 * k), centre[k]);
            return b;
        }

        /// <summary>ca2s2static.weapon_shape: the game reads a held weapon's children by position - &lt;name&gt;_high and
        /// &lt;name&gt;_low (the mesh, one visual with every section), then firepoint, firepoint_shell, aimpoint, Gun_box.</summary>
        private static (ZedTree.Record, List<(string, List<StaticSection>)>) WeaponShape(ZedTree.Record geo, List<(string Key, List<StaticSection> Sections)> visuals, string name, List<string> warnings)
        {
            var rowsRec = geo.Children.FirstOrDefault(c => c.Name == "children");
            var rows = rowsRec?.Children ?? Array.Empty<ZedTree.Record>();
            var meshes = rows.Where(r => r.Children.Any(c => c.Name == "visuals")).ToList();
            if (meshes.Count != 1)
            {
                warnings.Add($"weapon layout: {meshes.Count} mesh nodes under the root - left as converted (the game may not hold it right)");
                return (geo, visuals);
            }
            var mesh = meshes[0];
            var secs = visuals.OrderBy(v => v.Key, StringComparer.Ordinal).SelectMany(v => v.Sections).ToList();
            var vps = mesh.Children.First(c => c.Name == "visuals").Children.Select(v => v.Children[0].Data).ToList();
            uint flags = BitConverter.ToUInt32(vps[0], 0), lod = BitConverter.ToUInt32(vps[0], 4);
            int tris = vps.Sum(v => BitConverter.ToUInt16(v, 8)), verts = vps.Sum(v => BitConverter.ToUInt16(v, 10));
            var vp = VParams(flags, lod, tris, verts, new[] { BitConverter.ToSingle(vps[0], 12), BitConverter.ToSingle(vps[0], 16), BitConverter.ToSingle(vps[0], 20) });
            var np = mesh.Children.First(c => c.Name == "nparams").Data;
            string baseName = name.StartsWith("ca_") ? name[3..] : name;
            var lods = new[] { "high", "low" }.Select(tag => new ZedTree.Record($"{baseName}_{tag}", new byte[0], new[]
            {
                new ZedTree.Record("nparams", np),
                new ZedTree.Record("visuals", new byte[0], new[] { new ZedTree.Record("vis", new byte[0], new[] { new ZedTree.Record("vparams", vp) }) }),
            })).ToList();
            var named = rows.GroupBy(r => r.Name).ToDictionary(g => g.Key, g => g.First());
            var order = HeldOrder.Where(named.ContainsKey).Select(n => named[n]).ToList();
            if (!named.ContainsKey("firepoint")) warnings.Add("no firepoint node: the game fires from the weapon's origin");
            var kids = geo.Children.Where(c => c.Name != "children").Append(new ZedTree.Record("children", new byte[0], lods.Concat(order).ToList())).ToList();
            return (new ZedTree.Record(geo.Name, geo.Data, kids), new() { ("N000_000", secs), ("N001_000", secs) });
        }

        /// <summary>s2static.encode: per GEO node, each visual's geometry chunks then (material, name) per section; then
        /// each visual's colour blocks and its chain (refe, then cnt name / ref material / (ref colour, ref geometry)).</summary>
        internal static (byte[] Data, Dictionary<string, int> Offsets) EncodeStatic(List<(string Key, List<StaticSection> Sections)> visuals)
        {
            using var data = new MemoryStream();
            var offsets = new Dictionary<string, int>();
            byte[] Tag(uint a, uint b, uint c, uint d) { var t = new byte[16]; BinaryPrimitives.WriteUInt32LittleEndian(t, a); BinaryPrimitives.WriteUInt32LittleEndian(t.AsSpan(4), b); BinaryPrimitives.WriteUInt32LittleEndian(t.AsSpan(8), c); BinaryPrimitives.WriteUInt32LittleEndian(t.AsSpan(12), d); return t; }
            foreach (var group in visuals.GroupBy(v => v.Key.Split('_')[0]))
            {
                var placed = new Dictionary<string, (List<int> Geo, List<(int M, int N)> Mats)>();
                foreach (var (key, secs) in group)
                {
                    var geoAt = new List<int>();
                    foreach (var s in secs) foreach (var c in s.Chunks) { geoAt.Add((int)data.Length); data.Write(c.Geometry); }
                    var mats = new List<(int, int)>();
                    foreach (var s in secs)
                    {
                        int m = (int)data.Length; data.Write(s.Material);
                        int n = (int)data.Length; data.Write(s.NameField);
                        mats.Add((m, n));
                    }
                    placed[key] = (geoAt, mats);
                }
                foreach (var (key, secs) in group)
                {
                    var (geoAt, mats) = placed[key];
                    var tags = new List<byte[]>();
                    int gi = 0;
                    for (int si = 0; si < secs.Count; si++)
                    {
                        var s = secs[si];
                        tags.Add(Tag(0x10060000, (uint)mats[si].N, 0, 0));
                        tags.Add(Tag(0x30030000 | (uint)(s.Material.Length / 16), (uint)mats[si].M, s.MaterialVif.Item1, s.MaterialVif.Item2));
                        foreach (var c in s.Chunks)
                        {
                            int at = (int)data.Length; data.Write(c.Colour);
                            tags.Add(Tag(0x30040000 | (uint)(c.Colour.Length / 16), (uint)at, c.ColourVif.Item1, c.ColourVif.Item2));
                            tags.Add(Tag(0x30020000 | (uint)(c.Geometry.Length / 16), (uint)geoAt[gi++], 0, 0));
                        }
                    }
                    offsets[key] = (int)data.Length;
                    data.Write(Tag((uint)tags.Count, 0, 0, 0));
                    foreach (var t in tags) data.Write(t);
                }
            }
            return (data.ToArray(), offsets);
        }

        // ================================================================== textures, palettes, library files

        /// <summary>Palette ids are global across everything loaded (first registered wins): each pack gets its own
        /// block of 32 - 1024 + 32 * (crc32(pack) % 480).</summary>
        public static int PaletteBase(string pack) => 1024 + 32 * (int)(CaNames.Crc32(pack.ToLowerInvariant()) % 480);

        private static (List<ZedTree.Record> Textures, List<ZedTree.Record> Palettes) Textures(CaPackage pkg, CaLibrary lib,
            List<(uint Hash, string Name)> used, string pack, Socom2Templates.Character samples, int? maxSize, List<string> warnings)
        {
            var texNodes = new List<ZedTree.Record>();
            var palNodes = new List<ZedTree.Record>();
            var palIds = new Dictionary<(CaLibrary, int), int>();
            int index = 0;
            foreach (var (hash, name) in used)
            {
                var found = FindRaw(pkg, lib, hash);
                if (found == null) { warnings.Add($"texture {hash:x8}: no pixels found - left out"); continue; }
                var (owner, raw) = found.Value;
                index++;
                int palId = 0;
                if (raw.Psm is 0x13 or 0x14)
                {
                    if (!palIds.TryGetValue((owner, raw.PaletteRecord), out palId))
                    {
                        var pal = owner.RawPalette(raw.PaletteRecord);
                        if (pal == null) { warnings.Add($"texture {hash:x8}: palette missing"); }
                        else
                        {
                            palId = PaletteBase(pack) + palIds.Count;
                            palIds[(owner, raw.PaletteRecord)] = palId;
                            var par = new byte[8];
                            BinaryPrimitives.WriteUInt32LittleEndian(par, (uint)palId);
                            BinaryPrimitives.WriteUInt32LittleEndian(par.AsSpan(4), pal.Value.Format);
                            palNodes.Add(new ZedTree.Record($"texpal_{palId}", new byte[0], new[] { new ZedTree.Record("par", par), new ZedTree.Record("buf", pal.Value.Colours) }));
                        }
                    }
                }
                texNodes.Add(new ZedTree.Record(name, new byte[0], new[] { new ZedTree.Record("texdat", TexDat(raw, index, palId, samples, maxSize)) }));
            }
            return (texNodes, palNodes);
        }

        private static (CaLibrary, CaLibrary.RawTexture)? FindRaw(CaPackage pkg, CaLibrary lib, uint hash)
        {
            foreach (var l in new[] { lib }.Concat(pkg.Libraries.Select(x => x.Lib).Where(x => x != lib)))
                foreach (int rec in l.Textures)
                    if (l.U32(rec + 0x4C) == hash && l.Raw(rec) is { Pixels: not null } raw) return (l, raw);
            return null;
        }

        /// <summary>ca2s2.texdat: 0x20 header, pixels, GIF A+D tail (from a SOCOM II sample) with TEX0 = the texture's
        /// (TBP0 = index, CBP = palette id). 32-bit -> A1B5G5R5, 4-bit -> 8-bit (CA's 16 colours already sit in CSM1
        /// slots 0-7 / 16-23), larger than <paramref name="maxSize"/> halved (VRAM for runtime-loaded props is scarce).</summary>
        private static byte[] TexDat(CaLibrary.RawTexture t, int index, int palId, Socom2Templates.Character samples, int? maxSize)
        {
            int w = t.Width, h = t.Height, psm = t.Psm;
            byte[] pix = t.Pixels!;
            ulong tex0 = t.Tex0;
            if (psm == 0x00)
            {
                var o = new byte[w * h * 2];
                for (int i = 0; i < w * h; i++)
                {
                    uint c = BitConverter.ToUInt32(pix, 4 * i);
                    uint v = ((c >> 3) & 31) | (((c >> 11) & 31) << 5) | (((c >> 19) & 31) << 10) | ((c >> 31) != 0 || ((c >> 24) & 0xFF) >= 0x40 ? 0x8000u : 0);
                    BinaryPrimitives.WriteUInt16LittleEndian(o.AsSpan(2 * i), (ushort)v);
                }
                pix = o; psm = 0x02; tex0 = (tex0 & ~(0x3FUL << 20)) | (0x02UL << 20);
            }
            if (psm == 0x14)
            {
                var o = new byte[w * h];
                for (int i = 0; i < w * h / 2; i++) { o[2 * i] = (byte)(pix[i] & 0xF); o[2 * i + 1] = (byte)(pix[i] >> 4); }
                pix = o; psm = 0x13; tex0 = (tex0 & ~(0x3FUL << 20)) | (0x13UL << 20);
            }
            while (maxSize is int max && Math.Max(w, h) > max && w > 1 && h > 1)
            {
                int bpp = psm == 0x02 ? 2 : 1;
                var o = new byte[(w / 2) * (h / 2) * bpp];
                for (int y = 0; y < h / 2; y++)
                    for (int x = 0; x < w / 2; x++)
                        Buffer.BlockCopy(pix, ((2 * y) * w + 2 * x) * bpp, o, (y * (w / 2) + x) * bpp, bpp);
                pix = o; w /= 2; h /= 2;
                ulong tw = (tex0 >> 26) & 0xF, th = (tex0 >> 30) & 0xF;
                tex0 = (tex0 & ~(0xFFUL << 26)) | ((tw - 1) << 26) | ((th - 1) << 30);
                tex0 = (tex0 & ~(0x3FUL << 14)) | ((ulong)Math.Max(1, w / 64) << 14);
            }
            byte[] sample = psm == 0x02 ? samples.Sample16 : samples.Sample8;
            var hdr = sample[..0x20];
            BinaryPrimitives.WriteUInt16LittleEndian(hdr, (ushort)w);
            BinaryPrimitives.WriteUInt16LittleEndian(hdr.AsSpan(2), (ushort)h);
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(4), (uint)pix.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(8), (uint)index);
            if (psm == 0x13 && ((tex0 >> 51) & 0xF) == 0) hdr[0x0F] = 0x13;       // 32-bit palette: else read as 16-bit
            var tail = sample[(0x20 + (int)BitConverter.ToUInt32(sample, 4))..];
            int at = -1;
            for (int i = 0; i + 16 <= tail.Length; i += 16) if (BitConverter.ToUInt64(tail, i + 8) == 0x06) { at = i; break; }
            if (at < 0) throw new InvalidDataException("The SOCOM II texdat sample has no TEX0");
            tex0 = (tex0 & ~0x3FFFUL) | (uint)index;
            tex0 = psm == 0x13 ? (tex0 & ~(0x3FFFUL << 37)) | ((ulong)palId << 37) : tex0 & ((1UL << 37) - 1);
            BinaryPrimitives.WriteUInt64LittleEndian(tail.AsSpan(at), tex0);
            return hdr.Concat(pix).Concat(tail).ToArray();
        }

        internal static void AddLibrary(List<(string, byte[])> files, string lib, Socom2Templates.Library t,
            List<ZedTree.Record> textures, List<ZedTree.Record> palettes, ZedTree.Record geo, ZedTree.Record mdl)
        {
            files.Add(($"{lib}_TXR.ZED", Socom2Templates.WriteZed(t.Txr, new[] { t.RenderPhase, new ZedTree.Record("textures", new byte[0], textures) })));
            files.Add(($"{lib}_PAL.ZED", Socom2Templates.WriteZed(t.Pal, new[] { new ZedTree.Record("palettes", new byte[0], palettes) })));
            files.Add(($"{lib}_GEO.ZED", Socom2Templates.WriteZed(t.Geo, new[] { new ZedTree.Record("models", new byte[0], new[] { geo }) })));
            files.Add(($"{lib}_MDL.ZED", Socom2Templates.WriteZed(t.Mdl, new[] { mdl })));
        }

        // ================================================================== clips

        // CA parents (Mods/SOCOMCA/ca_bone_parents.json + the clip-only weapon / toe bones), parents first.
        private static readonly (uint Bone, uint? Parent)[] CaParent =
        {
            (0x89ED856D, null), (0x7EE3A92F, 0x89ED856D), (0x5E4CDCAC, 0x7EE3A92F), (0xD343BC9D, 0x5E4CDCAC), (0xA36E7E68, 0xD343BC9D),
            (0x45780C10, 0xA36E7E68), (0x9586FDE0, 0x45780C10), (0x4C4BFAA8, 0x9586FDE0), (0xF708CF93, 0x4C4BFAA8), (0x2011D653, 0xD343BC9D),
            (0xA7F3F69C, 0x2011D653), (0xFC45C9E2, 0xA7F3F69C), (0x5078BD30, 0xD343BC9D), (0x3577BB50, 0x5078BD30), (0x3A34EDF2, 0x3577BB50),
            (0xE3F9EABA, 0x3A34EDF2), (0x4F03913A, 0xE3F9EABA), (0x0EE0BC99, 0x7EE3A92F), (0x7A12CE1D, 0x0EE0BC99), (0x19E5B48F, 0x7A12CE1D),
            (0xA152AC8B, 0x7EE3A92F), (0xC21990B4, 0xA152AC8B), (0xA1EEEA26, 0xC21990B4), (0xDBA80BB2, null),
            (0xBBD4FAD5, 0xF708CF93), (0x799F10C4, 0x19E5B48F), (0xCF64B6F3, 0xA1EEEA26),
        };

        internal static readonly Dictionary<string, string> S2Parent = new()
        {
            ["hips"] = "skel_root", ["rthigh"] = "hips", ["rcalf"] = "rthigh", ["rfoot"] = "rcalf", ["rtoe"] = "rfoot", ["lthigh"] = "hips",
            ["lcalf"] = "lthigh", ["lfoot"] = "lcalf", ["ltoe"] = "lfoot", ["aimnodes"] = "hips", ["spinelo"] = "aimnodes", ["spinehi"] = "spinelo",
            ["rshoulder_wgt"] = "spinehi", ["rscap"] = "spinehi", ["rbicep"] = "spinehi", ["rforearm"] = "rbicep", ["rhand"] = "rforearm",
            ["rifle"] = "rhand", ["neck"] = "spinehi", ["head"] = "neck", ["lshoulder_wgt"] = "spinehi", ["lscap"] = "spinehi",
            ["lbicep"] = "spinehi", ["lforearm"] = "lbicep", ["lhand"] = "lforearm", ["do_not_use"] = "hips",
        };

        // SOCOM II bone <- the CA bone whose WORLD rotation it takes (both rigs run each bone's x axis along the limb).
        private static readonly Dictionary<string, uint> Source = new()
        {
            ["hips"] = 0x7EE3A92F, ["spinelo"] = 0x5E4CDCAC, ["spinehi"] = 0xD343BC9D, ["neck"] = 0x2011D653, ["head"] = 0xA7F3F69C,
            ["rscap"] = 0xA36E7E68, ["rbicep"] = 0x45780C10, ["rforearm"] = 0x9586FDE0, ["rhand"] = 0xF708CF93, ["lscap"] = 0x5078BD30,
            ["lbicep"] = 0x3577BB50, ["lforearm"] = 0x3A34EDF2, ["lhand"] = 0x4F03913A, ["rthigh"] = 0x0EE0BC99, ["rcalf"] = 0x7A12CE1D,
            ["rfoot"] = 0x19E5B48F, ["lthigh"] = 0xA152AC8B, ["lcalf"] = 0xC21990B4, ["lfoot"] = 0xA1EEEA26,
        };

        /// <summary>ca_anim.retarget: a CA clip on SOCOM II's seal_stand layout (still root, motion on the hips) at 30 fps.
        /// Per frame the CA world rotations are built from its bone-local keys, then each SOCOM II bone takes its source
        /// bone's world rotation relative to its SOCOM II parent; aimnodes cancels the hips; the rest keep seal_stand's
        /// values. The hips position is CA's (root + hips, ground-based) minus SOCOM II's root.</summary>
        public static MotionClip Retarget(CaAnimations.Clip ca, MotionClip stand, double fps = 30)
        {
            double dur = ca.Duration;
            int frames = Math.Max(1, (int)Math.Round(dur * fps));
            var tracks = ca.Tracks.GroupBy(t => t.Bone).ToDictionary(g => g.Key, g => g.First());
            var names = stand.Tracks.Select(t => t.Name).ToList();
            var refRot = stand.Tracks.ToDictionary(t => t.Name, t => Q(t.Rotations[0]));
            var refPos = stand.Tracks.ToDictionary(t => t.Name, t => t.Positions[0]);
            var rootPos = refPos.TryGetValue("skel_root", out var rp) ? rp : new short[] { 0, 0, 0 };
            var outRot = names.ToDictionary(n => n, _ => new List<double[]>());
            var outPos = names.ToDictionary(n => n, _ => new List<double[]>());
            string ParentOf(string n) => S2Parent.TryGetValue(n, out var p) && names.Contains(p) ? p : "skel_root";

            for (int k = 0; k <= frames; k++)
            {
                double t = dur * k / frames;
                var wr = new Dictionary<uint, double[]>();
                var wp = new Dictionary<uint, double[]>();
                foreach (var (bone, parent) in CaParent)
                {
                    if (!tracks.TryGetValue(bone, out var tr) || tr.Positions.Length == 0 || tr.Rotations.Length == 0) continue;
                    var lp = SampleP(tr.Positions, t, dur);
                    var lr = SampleQ(tr.Rotations, t, dur);
                    if (parent is uint p && wr.TryGetValue(p, out var pr))
                    {
                        wr[bone] = Norm(QMul(pr, lr));
                        var d = RotateQ(pr, lp);
                        wp[bone] = new[] { wp[p][0] + d[0], wp[p][1] + d[1], wp[p][2] + d[2] };
                    }
                    else { wr[bone] = lr; wp[bone] = lp; }
                }
                var sw = new Dictionary<string, double[]> { ["skel_root"] = refRot.GetValueOrDefault("skel_root", new double[] { 0, 0, 0, 1 }) };
                var done = new HashSet<string> { "skel_root" };
                for (int pass = 0; pass < names.Count && done.Count < names.Count; pass++)
                    foreach (string n in names)
                    {
                        if (done.Contains(n) || !done.Contains(ParentOf(n))) continue;
                        var par = sw[ParentOf(n)];
                        double[] loc = Source.TryGetValue(n, out uint src) && wr.TryGetValue(src, out var w) ? Norm(QMul(Inv(par), w))
                            : n == "aimnodes" && outRot["hips"].Count > 0 ? Inv(outRot["hips"][^1])
                            : refRot[n];
                        sw[n] = Norm(QMul(par, loc));
                        outRot[n].Add(loc);
                        if (n == "hips" && wp.TryGetValue(0x7EE3A92F, out var hp))
                            outPos[n].Add(new[] { hp[0] - rootPos[0], hp[1] - rootPos[1], hp[2] - rootPos[2] });
                        done.Add(n);
                    }
            }

            var clip = new MotionClip { Duration = (float)ca.Duration, Intervals = frames, NormMin = stand.NormMin, NormMax = stand.NormMax, Pad = stand.Pad };
            short C(double v) => (short)Math.Clamp((int)Math.Round(v), -32767, 32767);
            foreach (var st in stand.Tracks)
            {
                string n = st.Name;
                var rots = (n == "skel_root" || outRot[n].Count == 0 ? Enumerable.Repeat(refRot[n], frames + 1) : outRot[n])
                    .Select(q => new[] { C(q[0] * 32767), C(q[1] * 32767), C(q[2] * 32767), C(q[3] * 32767) }).ToList();
                var track = new MotionClip.Track { RawName = st.RawName, Record = st.Record };
                short[] still = st.Positions[0].Length >= 4 ? st.Positions[0] : new[] { st.Positions[0][0], st.Positions[0][1], st.Positions[0][2], MotionClip.StaticPositionW };
                if (n == "hips" && outPos[n].Count == frames + 1)
                {
                    track.Positions = outPos[n].Select(p => new[] { C(p[0]), C(p[1]), C(p[2]) }).ToList();
                    track.Rotations = rots;
                }
                else if (n == "skel_root" || rots.All(r => r.SequenceEqual(rots[0])))
                {
                    track.Positions = new() { still };
                    track.Rotations = new() { rots[0] };
                }
                else
                {
                    track.Positions = new() { still };
                    track.Rotations = rots;
                }
                clip.Tracks.Add(track);
            }
            return clip;
        }

        internal static double[] Q(short[] q) => Norm(new[] { q[0] / 32767.0, q[1] / 32767.0, q[2] / 32767.0, q[3] / 32767.0 });

        private static double[] SampleP(short[][] keys, double t, double dur)
        {
            if (keys.Length == 1) return new double[] { keys[0][0], keys[0][1], keys[0][2] };
            double f = Math.Clamp(t / dur * (keys.Length - 1), 0, keys.Length - 1);
            int i = Math.Min((int)f, keys.Length - 2);
            double u = f - i;
            return new[] { keys[i][0] + (keys[i + 1][0] - keys[i][0]) * u, keys[i][1] + (keys[i + 1][1] - keys[i][1]) * u, keys[i][2] + (keys[i + 1][2] - keys[i][2]) * u };
        }

        private static double[] SampleQ(short[][] keys, double t, double dur)
        {
            if (keys.Length == 1) return Q(keys[0]);
            double f = Math.Clamp(t / dur * (keys.Length - 1), 0, keys.Length - 1);
            int i = Math.Min((int)f, keys.Length - 2);
            double u = f - i;
            double[] a = Q(keys[i]), b = Q(keys[i + 1]);
            double s = a[0] * b[0] + a[1] * b[1] + a[2] * b[2] + a[3] * b[3] < 0 ? -1 : 1;
            return Norm(new[] { a[0] + (s * b[0] - a[0]) * u, a[1] + (s * b[1] - a[1]) * u, a[2] + (s * b[2] - a[2]) * u, a[3] + (s * b[3] - a[3]) * u });
        }

        internal static double[] QMul(double[] a, double[] b) => new[]
        {
            a[3] * b[0] + a[0] * b[3] + a[1] * b[2] - a[2] * b[1],
            a[3] * b[1] - a[0] * b[2] + a[1] * b[3] + a[2] * b[0],
            a[3] * b[2] + a[0] * b[1] - a[1] * b[0] + a[2] * b[3],
            a[3] * b[3] - a[0] * b[0] - a[1] * b[1] - a[2] * b[2],
        };

        internal static double[] Inv(double[] q) => new[] { -q[0], -q[1], -q[2], q[3] };

        internal static double[] Norm(double[] q)
        {
            double n = Math.Sqrt(q[0] * q[0] + q[1] * q[1] + q[2] * q[2] + q[3] * q[3]);
            return n > 1e-9 ? new[] { q[0] / n, q[1] / n, q[2] / n, q[3] / n } : new double[] { 0, 0, 0, 1 };
        }

        internal static double[] RotateQ(double[] q, double[] v)
        {
            var p = QMul(QMul(q, new[] { v[0], v[1], v[2], 0.0 }), Inv(q));
            return new[] { p[0], p[1], p[2] };
        }
    }
}
