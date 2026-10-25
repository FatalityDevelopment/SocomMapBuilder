using CodeDesigner.Ps2.FileFormats.Assets;
using CodeDesigner.Ps2.FileFormats.Zipper.Anim;
using CodeDesigner.Ps2.FileFormats.Zipper.Package;
using CodeDesigner.Ps2.FileFormats.Zipper.ZED;

namespace CodeDesigner.Ps2.FileFormats.Zipper.Conversion
{
    /// <summary>
    /// Retail SOCOM II pieces a converted model is built on, read from the game's <c>RUN/</c> folder (the same models
    /// Mods/SOCOMCA's converters used, whose packs were confirmed in game):
    /// <list type="bullet">
    /// <item>characters: MP52 <c>CLIB</c> › <c>alg_UN01</c> (skeleton, ZED headers, an 8-bit texdat sample) and M53's
    /// <c>alban_ter03_sleave.tif</c> (a 16-bit skin);</item>
    /// <item>weapons: M51 <c>WEAP</c> › <c>m4Acarbine_203</c>; vehicles: MP61 <c>BCAR</c> › <c>humvee</c>; props: MP61's own
    /// library › <c>sandbags</c> (material block, ZED headers, renderphase = library category);</item>
    /// <item>clips: <c>MOTION_P.ZAR</c> › <c>seal_stand</c> (bone list, track layout, static values).</item>
    /// </list>
    /// </summary>
    public sealed class Socom2Templates
    {
        public sealed record ZedHeader(uint StringBase, uint Word10, uint Word58, uint Word5C, uint Word60)
        {
            public static ZedHeader Of(ZedContainer c) => new(c.StringBasePointer, c.HeaderWord10, c.HeaderWord58, c.HeaderWord5C, c.HeaderWord60);
        }

        public sealed record Library(string Name, ZedHeader Txr, ZedHeader Pal, ZedHeader Geo, ZedHeader Mdl, ZedTree.Record RenderPhase);

        public sealed record Character(Library Library, ZedTree.Record Geo, CharacterSkeleton Skeleton, byte[] Sample8, byte[] Sample16);

        public sealed record Static(Library Library, byte[] Material, (uint, uint) MaterialVif, bool Weapon);

        public string RunDir { get; }
        private readonly Dictionary<string, ZipperPackage> _packages = new(StringComparer.OrdinalIgnoreCase);
        private Character? _character;
        private readonly Dictionary<string, Static> _statics = new();
        private MotionClip? _stand;

        public Socom2Templates(string runDir)
        {
            if (!File.Exists(Path.Combine(runDir, "MP52.ZDB")) || !File.Exists(Path.Combine(runDir, "MOTION_P.ZAR")))
                throw new FileNotFoundException($"{runDir} is not SOCOM II's RUN folder (no MP52.ZDB / MOTION_P.ZAR)");
            RunDir = runDir;
        }

        /// <summary>The first <c>X:/RUN</c> (or <paramref name="hint"/>) holding SOCOM II's MP52.ZDB and MOTION_P.ZAR.</summary>
        public static string? FindRunDir(string? hint = null)
        {
            var candidates = new List<string>();
            if (!string.IsNullOrEmpty(hint)) { candidates.Add(hint); candidates.Add(Path.Combine(hint, "RUN")); }
            foreach (var d in DriveInfo.GetDrives())
            {
                try { if (d.IsReady) candidates.Add(Path.Combine(d.RootDirectory.FullName, "RUN")); }
                catch (IOException) { }
            }
            return candidates.FirstOrDefault(c => File.Exists(Path.Combine(c, "MP52.ZDB")) && File.Exists(Path.Combine(c, "MOTION_P.ZAR")));
        }

        private ZipperPackage Package(string zdb)
        {
            if (!_packages.TryGetValue(zdb, out var p)) _packages[zdb] = p = ZipperPackage.Open(Path.Combine(RunDir, zdb + ".ZDB"));
            return p;
        }

        private static int Top(ZedContainer c, string name)
        {
            var parents = ZedTree.ParentIndices(c.Nodes);
            for (int i = 0; i < c.Nodes.Count; i++)
                if (parents[i] < 0 && c.Nodes[i].Name == name) return i;
            throw new InvalidDataException($"No top-level '{name}' record");
        }

        private static int Child(ZedContainer c, int parent, string name) =>
            ZedTree.Children(c.Nodes, parent).FirstOrDefault(i => c.Nodes[i].Name == name, -1) is int i and >= 0 ? i
                : throw new InvalidDataException($"No '{name}' under '{c.Nodes[parent].Name}'");

        private Library LibraryOf(ZipperPackage p, string lib)
        {
            var txr = p.Container($"{lib}_TXR.ZED");
            return new Library(lib, ZedHeader.Of(txr), ZedHeader.Of(p.Container($"{lib}_PAL.ZED")), ZedHeader.Of(p.Container($"{lib}_GEO.ZED")),
                ZedHeader.Of(p.Container($"{lib}_MDL.ZED")), ZedTree.Copy(txr.Nodes, Top(txr, "renderphase")));
        }

        public Character CharacterTemplate()
        {
            if (_character != null) return _character;
            var p = Package("MP52");
            var geo = p.Container("CLIB_GEO.ZED");
            var model = ZedTree.Copy(geo.Nodes, Child(geo, Top(geo, "models"), "alg_UN01"));
            var skeleton = CharacterMesh.SkeletonFromGeo(p.FindSkeleton("clib", "alg_UN01") ?? throw new InvalidDataException("MP52 has no alg_UN01 skeleton"));
            var txr = p.Container("CLIB_TXR.ZED");
            int textures = Top(txr, "textures");
            int first = ZedTree.Children(txr.Nodes, textures)[0];
            byte[] sample8 = txr.Nodes[ZedTree.Children(txr.Nodes, first)[0]].Data;
            var txr16 = Package("M53").Container("CLIB_TXR.ZED");
            int skin = Child(txr16, Top(txr16, "textures"), "alban_ter03_sleave.tif");
            byte[] sample16 = txr16.Nodes[ZedTree.Children(txr16.Nodes, skin)[0]].Data;
            return _character = new Character(LibraryOf(p, "CLIB"), model, skeleton, sample8, sample16);
        }

        /// <summary>kind: weapon (WEAP), vehicle (BCAR) or prop (the map library: drawn as a pre-lit map prop).</summary>
        public Static StaticTemplate(string kind)
        {
            if (_statics.TryGetValue(kind, out var hit)) return hit;
            var (zdb, lib, model) = kind switch
            {
                "weapon" => ("M51", "WEAP", "m4Acarbine_203"),
                "vehicle" => ("MP61", "BCAR", "humvee"),
                _ => ("MP61", "MP61", "sandbags"),
            };
            var p = Package(zdb);
            var mdl = p.Container($"{lib}_MDL.ZED");
            int top = Top(mdl, model);
            byte[] d = mdl.Nodes[top].Data;
            int chain = (int)BitConverter.ToUInt32(mdl.Nodes[ZedTree.Children(mdl.Nodes, top)[0]].Data, 0);
            int count = (int)(BitConverter.ToUInt32(d, chain) & 0xFFFF);
            for (int i = 1; i <= count; i++)
            {
                int o = chain + 16 * i;
                uint lo = BitConverter.ToUInt32(d, o);
                if (((lo >> 16) & 0x3FF) != 3) continue;
                int addr = (int)BitConverter.ToUInt32(d, o + 4), qwc = (int)(lo & 0xFFFF);
                var t = new Static(LibraryOf(p, lib), d[addr..(addr + 16 * qwc)], (BitConverter.ToUInt32(d, o + 8), BitConverter.ToUInt32(d, o + 12)), kind == "weapon");
                return _statics[kind] = t;
            }
            throw new InvalidDataException($"{zdb} {model}: no material block");
        }

        public (ZedHeader Header, MotionClip Stand) ClipTemplate()
        {
            var zar = ZedContainer.FromBytes(File.ReadAllBytes(Path.Combine(RunDir, "MOTION_P.ZAR")));
            _stand ??= MotionClip.Parse(zar.Nodes.First(n => n.Name == "seal_stand").Data);
            return (ZedHeader.Of(zar), _stand);
        }

        /// <summary>A ZED from top-level records with a template's header words (the layout build_charpack.zed_write
        /// produces; records pre-order, each record's type = its child count).</summary>
        public static byte[] WriteZed(ZedHeader header, IReadOnlyList<ZedTree.Record> tops)
        {
            var c = ZedContainer.CreateNew(header.StringBase, (uint)tops.Count, header.Word5C);
            c.HeaderWord10 = header.Word10; c.HeaderWord58 = header.Word58; c.HeaderWord60 = header.Word60;
            void Add(ZedTree.Record r)
            {
                c.AddNode(r.Name, r.Data, (uint)r.Children.Count);
                foreach (var k in r.Children) Add(k);
            }
            foreach (var t in tops) Add(t);
            return c.ToBytes();
        }
    }
}
