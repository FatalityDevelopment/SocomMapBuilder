using CodeDesigner.Ps2.FileFormats.Assets;
using System.Buffers.Binary;
using System.Text;
using CodeDesigner.Ps2.FileFormats.Zipper.Anim;
using CodeDesigner.Ps2.FileFormats.Zipper.Ca;
using CodeDesigner.Ps2.FileFormats.Zipper.Geo;
using CodeDesigner.Ps2.FileFormats.Zipper.Model;
using CodeDesigner.Ps2.FileFormats.Zipper.Package;
using CodeDesigner.Ps2.FileFormats.Zipper.ZDB;
using CodeDesigner.Ps2.FileFormats.Zipper.ZED;

namespace CodeDesigner.Ps2.FileFormats.Zipper.Conversion
{
    /// <summary>
    /// SOCOM II model -> SOCOM 3 / Combined Assault package (a ZDB v6 with an <c>ASST</c> library, a <c>BONE</c> clip
    /// bank and a <c>NAMS</c> name list), the reverse of <see cref="CaToSocom2"/>. The mesh formats are shared, so as
    /// in that direction the mesh and pixel bytes are copied as they are and only the containers change:
    /// <list type="bullet">
    /// <item>characters: the SOCOM II skeleton becomes the clib node tree (bones named by CRC32 of their SOCOM II
    /// names), each vertex block a CA vertex batch (two-element header, bone = pre-order node index) and the draw
    /// blocks are copied;</item>
    /// <item>weapons / vehicles / props: one node per GEO visual (placed by its node's transform) holding a static
    /// object whose chains are (TEX0 material, colours, geometry chunk) per section;</item>
    /// <item>textures and palettes: texdat pixels and PAL colours into CA records with the texture's TEX0;</item>
    /// <item>clips: SOCOM II clips (same units and quaternions) into CA clip records, one key or one per sample.</item>
    /// </list>
    /// The result opens in the editors; whether SOCOM 3 / CA itself loads it is not known yet (no pack loader).
    /// </summary>
    public static class Socom2ToCa
    {
        public sealed record Options(string ModelName, string OutputDir, IReadOnlyList<string>? Clips = null);

        public sealed record Result(string Path, string Model, string Kind, string Resource, int Triangles, int Textures, int Palettes,
            IReadOnlyList<string> Clips, IReadOnlyList<string> Warnings);

        /// <summary>The CodeDesigner name list resource tag: the names CA's CRC32 hashes stand for in this package.</summary>
        public const string NamesTag = "NAMS";

        public static Result Export(ZipperPackage pkg, ZipperPackage.ModelRef model, Options o)
        {
            var warnings = new List<string>();
            var names = new List<string> { o.ModelName };
            var lib = new CaLibraryWriter();
            var mesh = ZipperModelMesh.Build(pkg, model);
            string kind, resource;
            int triangles;
            var textureIndex = new Dictionary<string, (int Rec, ulong Tex0)>(StringComparer.OrdinalIgnoreCase);
            var palettes = new Dictionary<string, int>();
            string texNs = pkg.TextureNamespace(model.Namespace) ?? model.Namespace;

            (int Rec, ulong Tex0) Texture(string tif)
            {
                if (textureIndex.TryGetValue(tif, out var hit)) return hit;
                names.Add(tif);
                var found = pkg.FindTexture(tif, texNs);
                if (found == null) { warnings.Add($"texture {tif} not found - left out"); return textureIndex[tif] = (0, 0); }
                byte[] texdat = found.Value.Texdat;
                int pixLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(texdat.AsSpan(4));
                byte[] pixels = texdat[0x20..(0x20 + pixLen)];
                ulong tex0 = Tex0Of(texdat) ?? throw new InvalidDataException($"{tif}: no TEX0");
                int psm = (int)((tex0 >> 20) & 0x3F);
                int palette = 0;
                if (psm is 0x13 or 0x14)
                {
                    int id = (int)((tex0 >> 37) & 0x3FFF);
                    string key = $"{found.Value.Namespace}:{id}";
                    if (!palettes.TryGetValue(key, out palette))
                    {
                        var raw = RawPalette(pkg, found.Value.Namespace, id) ?? RawPaletteAnywhere(pkg, id);
                        if (raw == null) warnings.Add($"{tif}: palette {id} not found");
                        else palette = palettes[key] = lib.AddPalette(CaNames.Crc32($"texpal_{id}"), raw.Value.Format, raw.Value.Colours);
                    }
                }
                return textureIndex[tif] = (lib.AddTexture(CaNames.Crc32(tif), tex0, pixels, palette), tex0);
            }

            if (mesh.Skinned)
            {
                kind = "character";
                resource = "clib";
                triangles = Character(pkg, model, mesh, lib, names, Texture, o, warnings);
            }
            else
            {
                kind = model.Kind == "weapon" ? "weapon" : "prop";
                triangles = Static(pkg, model, lib, names, Texture, o, warnings, out bool scale);
                resource = (kind == "weapon" ? "weap_" : scale ? "scl_" : "prop_") + o.ModelName + ".rdr";
            }

            using var zdb = ZdbArchive.CreateNew(1);
            zdb.AddResource(resource, lib.ToBytes(CaNames.Crc32(resource)), "ASST", 29);

            var clips = new List<string>();
            if (o.Clips is { Count: > 0 })
            {
                var shared = PackageAnimations.SharedPath(pkg) is string sp ? ZedContainer.FromBytes(File.ReadAllBytes(sp)).Nodes : null;
                var bank = new List<(string Name, MotionClip Clip)>();
                foreach (string name in o.Clips)
                {
                    var c = PackageAnimations.Find(pkg, shared, name) ?? throw new KeyNotFoundException($"Clip '{name}' not found");
                    bank.Add((c.Name, MotionClip.Parse(c.Data)));
                    clips.Add(c.Name);
                    names.Add(c.Name);
                }
                zdb.AddResource("bone_loader", ClipBank(bank), "BONE", 4);
            }
            zdb.AddResource("names", Encoding.ASCII.GetBytes(string.Join("\n", names.Distinct()) + "\n"), NamesTag, 1);

            Directory.CreateDirectory(o.OutputDir);
            string path = Path.Combine(o.OutputDir, o.ModelName.ToUpperInvariant() + ".ZDB");
            File.WriteAllBytes(path, zdb.ToBytes());
            return new Result(path, o.ModelName, kind, resource, triangles, textureIndex.Values.Count(v => v.Rec != 0), palettes.Count, clips, warnings);
        }

        // ================================================================== characters

        private static int Character(ZipperPackage pkg, ZipperPackage.ModelRef model, ModelMesh mesh, CaLibraryWriter lib, List<string> names,
            Func<string, (int Rec, ulong Tex0)> texture, Options o, List<string> warnings)
        {
            var skeleton = mesh.Skeleton!;
            ZedContainer container = pkg.Container(model.Archive);
            byte[] payload = container.Nodes[model.NodeIndex].Data;
            int start = CharacterMesh.StartOffset(container, model.NodeIndex) ?? throw new InvalidDataException($"{model.Name}: no _START");

            // SOCOM II chain (s2mesh.parse): cnt = section (texture name), ref matrix + ref vertex block, ref draw block.
            using var data = new MemoryStream();
            int count = (int)(BitConverter.ToUInt32(payload, start) & 0xFFFF), triangles = 0;
            int? pendingMatrix = null;
            bool sectionOpen = false;
            for (int t = 1; t <= count; t++)
            {
                int o2 = start + 16 * t;
                uint lo = BitConverter.ToUInt32(payload, o2);
                int addr = BitConverter.ToInt32(payload, o2 + 4), qwc = (int)(lo & 0xFFFF);
                uint v0 = BitConverter.ToUInt32(payload, o2 + 8), v1 = BitConverter.ToUInt32(payload, o2 + 12);
                if (((lo >> 28) & 7) == 1)
                {
                    string tif = ReadName(payload, addr);
                    texture(tif);
                    var field = new byte[16];
                    BinaryPrimitives.WriteUInt32LittleEndian(field, CaNames.Crc32(tif));
                    Pad(data);
                    data.Write(field);
                    sectionOpen = true;
                    continue;
                }
                if (v0 == 0x01000101 && v1 == 0x6C048000) { pendingMatrix = addr; continue; }
                if (!sectionOpen || addr < 0 || addr + qwc * 16 > payload.Length) continue;
                byte[] block = payload[addr..(addr + qwc * 16)];
                if (pendingMatrix != null)
                {
                    // SOCOM II: STCYCL, UNPACK V4-16 @4 (1 + 2n), header (flags, tris, bone = matrix_id, n), verts.
                    // CA: STCYCL, UNPACK V4-16 @0 (2 + 2n), (flags, tris, bone = node index, n), (0, 0, 0, 0x8000), verts.
                    short flags = BitConverter.ToInt16(block, 8), tris = BitConverter.ToInt16(block, 10), mid = BitConverter.ToInt16(block, 12), n = BitConverter.ToInt16(block, 14);
                    int bone = Math.Max(0, skeleton.IndexOfMatrix(mid) - 1);
                    using var b = new MemoryStream();
                    var w = new BinaryWriter(b);
                    w.Write(0x01000101u); w.Write(0x6D008000u | (uint)((2 + 2 * n) & 0xFF) << 16);
                    w.Write(flags); w.Write(tris); w.Write((short)bone); w.Write(n);
                    w.Write((short)0); w.Write((short)0); w.Write((short)0); w.Write(unchecked((short)0x8000));
                    w.Write(block, 16, 16 * n);
                    w.Write(0x17000000u);
                    w.Flush();
                    Pad(b);
                    data.Write(b.ToArray());
                    pendingMatrix = null;
                }
                else
                {
                    data.Write(block);                                   // draw block: the same in both games
                    triangles += DrawTriangles(block);
                }
            }
            names.AddRange(skeleton.Bones.Select(b => b.Name));
            int obj = lib.AddSkinnedObject(data.ToArray());

            // Node tree: the skeleton (bone 0 = the model node = the clib model root).
            var specs = skeleton.Bones.Select(b => new CaLibraryWriter.NodeSpec { Hash = CaNames.Crc32(b.Name), Local = b.Local }).ToList();
            specs[0] = new CaLibraryWriter.NodeSpec { Hash = CaNames.Crc32(o.ModelName), Local = skeleton.Bones[0].Local };
            for (int i = 1; i < skeleton.Bones.Count; i++)
                specs[Math.Max(0, skeleton.Bones[i].Parent)].Children.Add(specs[i]);
            specs[0].Objects.Add(obj);
            lib.AddModel(specs[0]);
            return triangles;
        }

        private static void Pad(MemoryStream s) { while (s.Length % 16 != 0) s.WriteByte(0); }

        private static int DrawTriangles(byte[] draw)
        {
            for (int w = 0; w + 4 <= draw.Length; w += 4)
                if (((BitConverter.ToUInt32(draw, w) >> 24) & 0x7F) == 0x6E) return (int)((BitConverter.ToUInt32(draw, w) >> 16) & 0xFF);
            return 0;
        }

        private static string ReadName(byte[] b, int addr)
        {
            int end = addr;
            while (end < b.Length && end - addr < 32 && b[end] != 0) end++;
            return Encoding.Latin1.GetString(b, addr, end - addr);
        }

        // ================================================================== static models

        private static int Static(ZipperPackage pkg, ZipperPackage.ModelRef model, CaLibraryWriter lib, List<string> names,
            Func<string, (int Rec, ulong Tex0)> texture, Options o, List<string> warnings, out bool scaleUnit)
        {
            ZedContainer container = pkg.Container(model.Archive);
            byte[] payload = container.Nodes[model.NodeIndex].Data;
            var offsets = ZedModelFile.CollectChainOffsets(container.Nodes, model.NodeIndex);
            var chains = offsets.Count > 0 ? ZedModelFile.ParseVisualChains(payload, offsets) : null;
            if (chains == null) throw new InvalidOperationException($"{model.Name} has no DMA chains");
            GeoNode? geo = pkg.DefinitionNode(model);
            var placements = geo != null ? VisualPlacement.Build(geo, chains.Select(c => c.Key).Where(k => k != null).Cast<string>())
                : new Dictionary<string, VisualPlacement.Placement>();
            scaleUnit = placements.Values.Any(p => p.Unit == ZedModelFile.PositionUnit.Scale)
                || (geo?.VisualFlags is uint f && ZedModelFile.PositionUnitForFlags(f) == ZedModelFile.PositionUnit.Scale);

            var root = new CaLibraryWriter.NodeSpec { Hash = CaNames.Crc32(o.ModelName) };
            int triangles = 0;
            foreach (var chain in chains)
            {
                var place = chain.Key != null && placements.TryGetValue(chain.Key, out var p) ? p : null;
                var caChains = new List<IReadOnlyList<CaLibraryWriter.ChainTag>>();
                var textures = new List<int>();
                var lo = new[] { float.MaxValue, float.MaxValue, float.MaxValue };
                var hi = new[] { float.MinValue, float.MinValue, float.MinValue };
                var seen = new HashSet<int>();
                foreach (var section in chain.Sections)
                {
                    var (rec, tex0) = section.TextureName != null ? texture(section.TextureName) : (0, 0UL);
                    if (rec == 0) continue;
                    if (!textures.Contains(rec)) textures.Add(rec);
                    var tags = new List<CaLibraryWriter.ChainTag> { new(6, CaLibraryWriter.Tex0Packet(tex0)) };
                    foreach (var r in section.Chunks)
                    {
                        if (!seen.Add(r.GeometryOffset)) continue;
                        byte[] geometry = payload[r.GeometryOffset..(r.GeometryOffset + r.GeometryLength)];
                        int verts = (int)BitConverter.ToUInt32(geometry, 8 + 40);
                        triangles += (int)BitConverter.ToUInt32(geometry, 8 + 44);
                        byte[] colour = r.ColorOffset is int co && r.ColorLength > 0 ? payload[co..(co + r.ColorLength)] : Grey(verts);
                        tags.Add(new CaLibraryWriter.ChainTag(3, colour, 0x01000103u, 0x6E00C006u | (uint)(verts & 0xFF) << 16));
                        tags.Add(new CaLibraryWriter.ChainTag(2, geometry));
                        var chunk = ZipperModelMesh.DecodeChunk(payload, r, 0, null, null, null, place?.Unit ?? (scaleUnit ? ZedModelFile.PositionUnit.Scale : ZedModelFile.PositionUnit.Sixteenth), Matrix4.Identity());
                        if (chunk != null)
                            for (int i = 0; i < chunk.Positions.Length; i++) { lo[i % 3] = MathF.Min(lo[i % 3], chunk.Positions[i]); hi[i % 3] = MathF.Max(hi[i % 3], chunk.Positions[i]); }
                    }
                    if (tags.Count > 1) caChains.Add(tags);
                }
                if (caChains.Count == 0) continue;
                var centre = lo[0] <= hi[0] ? new[] { (lo[0] + hi[0]) / 2, (lo[1] + hi[1]) / 2, (lo[2] + hi[2]) / 2 } : new float[3];
                int obj = lib.AddStaticObject(centre, caChains, textures);
                string nodeName = place?.NodeName ?? chain.Key ?? "visual";
                names.Add(nodeName);
                var node = new CaLibraryWriter.NodeSpec { Hash = CaNames.Crc32(nodeName), Local = place?.Matrix ?? Matrix4.Identity() };
                node.Objects.Add(obj);
                root.Children.Add(node);
            }
            lib.AddModel(root);
            return triangles;
        }

        private static byte[] Grey(int verts)
        {
            var b = new byte[(4 * verts + 15) & ~15];
            for (int i = 0; i < 4 * verts; i++) b[i] = 0x80;
            return b;
        }

        // ================================================================== textures

        private static ulong? Tex0Of(byte[] texdat)
        {
            int tail = 0x20 + (int)BinaryPrimitives.ReadUInt32LittleEndian(texdat.AsSpan(4));
            for (int o = tail; o + 16 <= texdat.Length; o += 16)
                if (BinaryPrimitives.ReadUInt64LittleEndian(texdat.AsSpan(o + 8)) == 0x06) return BinaryPrimitives.ReadUInt64LittleEndian(texdat.AsSpan(o));
            return null;
        }

        private static (byte[] Colours, uint Format)? RawPalette(ZipperPackage pkg, string ns, int id)
        {
            var pal = pkg.Archive(ns, "pal");
            return pal == null ? null : FindPalette(pkg.Container(pal.Leaf).Nodes, id);
        }

        private static (byte[] Colours, uint Format)? RawPaletteAnywhere(ZipperPackage pkg, int id)
        {
            foreach (var e in pkg.Entries.Where(e => e.Kind == "pal"))
                if (FindPalette(pkg.Container(e.Leaf).Nodes, id) is { } found) return found;
            return null;
        }

        private static (byte[] Colours, uint Format)? FindPalette(IReadOnlyList<ZedNode> nodes, int id)
        {
            string name = "texpal_" + id;
            for (int i = 0; i < nodes.Count; i++)
            {
                if (nodes[i].Name != name) continue;
                byte[]? par = null, buf = null;
                for (int j = i + 1; j < Math.Min(i + 4, nodes.Count); j++)
                {
                    if (nodes[j].Name == "par") par = nodes[j].Data;
                    if (nodes[j].Name == "buf") buf = nodes[j].Data;
                }
                if (buf != null) return (buf, par is { Length: >= 8 } ? BitConverter.ToUInt32(par, 4) : (buf.Length >= 1024 ? 0x400u : 0x00020200u));
            }
            return null;
        }

        // ================================================================== clips

        /// <summary>CA clip records (0x60: tracks vector, flags 0x2017...., name CRC32, duration) over 16-byte tracks
        /// {bone hash, positions, rotations, 3 x positions | 4 x rotations &lt;&lt; 16}; keys int16 as in SOCOM II.</summary>
        public static byte[] ClipBank(IReadOnlyList<(string Name, MotionClip Clip)> clips)
        {
            using var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            w.Write(new byte[16]);                                         // nothing at offset 0 (a clip's tracks never start there)
            int records = (int)ms.Length;
            w.Write(new byte[0x60 * clips.Count]);
            byte[] buffer;
            var fix = new List<(int At, uint Value)>();
            for (int index = 0; index < clips.Count; index++)
            {
                var (name, clip) = clips[index];
                int rec = records + 0x60 * index;
                var tracks = clip.Tracks.Take(40).ToList();
                int trackAt = (int)ms.Length;
                w.Write(new byte[16 * tracks.Count]);
                for (int t = 0; t < tracks.Count; t++)
                {
                    var tr = tracks[t];
                    while (ms.Length % 4 != 0) w.Write((byte)0);
                    int posAt = (int)ms.Length;
                    foreach (var p in tr.Positions) { w.Write(p[0]); w.Write(p[1]); w.Write(p[2]); }
                    while (ms.Length % 4 != 0) w.Write((byte)0);
                    int rotAt = (int)ms.Length;
                    foreach (var q in tr.Rotations) { w.Write(q[0]); w.Write(q[1]); w.Write(q[2]); w.Write(q[3]); }
                    int o = trackAt + 16 * t;
                    fix.Add((o, CaNames.Crc32(tr.Name)));
                    fix.Add((o + 4, (uint)posAt));
                    fix.Add((o + 8, (uint)rotAt));
                    fix.Add((o + 12, (uint)(3 * tr.Positions.Count) | (uint)(4 * tr.Rotations.Count) << 16));
                }
                fix.Add((rec, (uint)trackAt));
                fix.Add((rec + 4, (uint)(trackAt + 16 * tracks.Count)));
                fix.Add((rec + 8, (uint)(trackAt + 16 * tracks.Count)));
                fix.Add((rec + 0x20, 0x20170000u));
                fix.Add((rec + 0x24, CaNames.Crc32(name)));
                fix.Add((rec + 0x2C, BitConverter.SingleToUInt32Bits(clip.Duration)));
            }
            while (ms.Length % 16 != 0) w.Write((byte)0);
            w.Flush();
            buffer = ms.ToArray();
            foreach (var (at, value) in fix) BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(at), value);
            return buffer;
        }
    }
}
