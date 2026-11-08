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

        /// <summary>The template files, from SOCOM II's RUN folder.</summary>
        public static readonly string[] TemplateFiles = { "MP52.ZDB", "M51.ZDB", "M53.ZDB", "MP61.ZDB", "MOTION_P.ZAR" };

        /// <summary>
        /// The first <c>X:/RUN</c> (or <paramref name="hint"/>) holding SOCOM II's MP52.ZDB and MOTION_P.ZAR. A SOCOM II
        /// disc image works too (the hint, else one <see cref="DiscImage"/> finds): its template files are copied once
        /// into a cache folder, which is returned.
        /// </summary>
        public static string? FindRunDir(string? hint = null)
        {
            // A disc image named as the hint wins over mounted drives.
            if (!string.IsNullOrEmpty(hint) && hint.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) && RunFromIso(hint) is { } named) return named;
            var candidates = new List<string>();
            if (!string.IsNullOrEmpty(hint) && !hint.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)) { candidates.Add(hint); candidates.Add(Path.Combine(hint, "RUN")); }
            candidates.AddRange(MountedRunDirs());
            string? folder = candidates.FirstOrDefault(IsRunDir);
            if (folder != null) return folder;
            return DiscImage() is { } iso ? RunFromIso(iso) : null;
        }

        private static bool IsRunDir(string dir) => File.Exists(Path.Combine(dir, "MP52.ZDB")) && File.Exists(Path.Combine(dir, "MOTION_P.ZAR"));

        private static IEnumerable<string> MountedRunDirs()
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                string? run = null;
                try { if (d.IsReady) run = Path.Combine(d.RootDirectory.FullName, "RUN"); }
                catch (IOException) { }
                if (run != null) yield return run;
            }
        }

        private static readonly object DiscGate = new();
        private static string? _discImage;
        private static DateTime _discMissAt = DateTime.MinValue;

        /// <summary>
        /// A SOCOM II disc image on this machine: <c>MAPBUILDER_SOCOM2_DISC</c> (an .ISO, or a folder holding one), else
        /// the first SOCOM II .ISO in a drive's root, one folder below a root (<c>F:\Desktop\…iso</c>), or the user's
        /// Desktop, Downloads or Documents. The answer is kept; a miss is retried after a minute.
        /// </summary>
        public static string? DiscImage()
        {
            lock (DiscGate)
            {
                if (_discImage != null && File.Exists(_discImage)) return _discImage;
                if (DateTime.UtcNow - _discMissAt < TimeSpan.FromMinutes(1)) return null;
                _discImage = CandidateImages().FirstOrDefault(IsSocom2Image);
                if (_discImage == null) _discMissAt = DateTime.UtcNow;
                return _discImage;
            }
        }

        private static IEnumerable<string> CandidateImages()
        {
            var folders = new List<string>();
            string? env = Environment.GetEnvironmentVariable("MAPBUILDER_SOCOM2_DISC");
            if (!string.IsNullOrWhiteSpace(env))
            {
                if (File.Exists(env)) yield return env;
                else folders.Add(env);
            }
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (!d.IsReady) continue;
                    folders.Add(d.RootDirectory.FullName);
                    folders.AddRange(Directory.EnumerateDirectories(d.RootDirectory.FullName));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            folders.AddRange(new[] { Path.Combine(home, "Desktop"), Path.Combine(home, "Downloads"), Path.Combine(home, "Documents") });
            foreach (string folder in folders.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                List<string> isos;
                try { isos = Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*.iso").ToList() : new List<string>(); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
                foreach (string iso in isos) yield return iso;
            }
        }

        private static bool IsSocom2Image(string iso)
        {
            try
            {
                var files = Gta.Iso9660.Files(iso);
                return files.ContainsKey("RUN/MP52.ZDB") && files.ContainsKey("RUN/MOTION_P.ZAR");
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or EndOfStreamException or ArgumentException) { return false; }
        }

        /// <summary>A SOCOM II disc image's template files in a cache folder (copied on first use); null when the image
        /// is not SOCOM II.</summary>
        public static string? RunFromIso(string iso) => IsSocom2Image(iso) ? CopyFromIso(iso, TemplateFiles) : null;

        /// <summary>
        /// One file of SOCOM II's RUN folder (<c>ZWEAPON.ZAR</c>, <c>READERC.ZAR</c>, <c>EXTZANIM.ZAR</c> …): from a
        /// mounted disc's RUN folder, else copied out of the disc image <see cref="DiscImage"/> finds into the cache
        /// folder. Null when there is no SOCOM II disc or it has no such file.
        /// </summary>
        public static string? RunFile(string leaf)
        {
            foreach (string run in MountedRunDirs())
                if (IsRunDir(run) && File.Exists(Path.Combine(run, leaf))) return Path.Combine(run, leaf);
            if (DiscImage() is not { } iso || CopyFromIso(iso, new[] { leaf }) is not { } cache) return null;
            string path = Path.Combine(cache, leaf);
            return File.Exists(path) ? path : null;
        }

        private static string? CopyFromIso(string iso, IEnumerable<string> names)
        {
            try
            {
                var files = Gta.Iso9660.Files(iso);
                var info = new FileInfo(iso);
                string cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MapBuilder", "socom2-run",
                    $"{Path.GetFileNameWithoutExtension(iso)}-{info.Length:x}");
                Directory.CreateDirectory(cache);
                foreach (string name in names)
                {
                    string target = Path.Combine(cache, name);
                    if (File.Exists(target) || !files.TryGetValue("RUN/" + name.Replace('\\', '/'), out var span)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.WriteAllBytes(target + ".tmp", span.Read());
                    File.Move(target + ".tmp", target, true);
                }
                return cache;
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or EndOfStreamException) { return null; }
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

        /// <summary>
        /// A retail HUD icon to build weapon icons on: MP61's RUN/COMMON/HUDW › sigcommando_icon.tif (128×32, 8-bit,
        /// 32-bit palette; header, GIF tail) and the HUDW TXR / PAL header words. HUD icons live in a category 0x0C
        /// library (the HUD's own VRAM pool).
        /// </summary>
        public (byte[] Texdat, ZedHeader Txr, ZedHeader Pal) HudIconTemplate()
        {
            var p = Package("MP61");
            var txr = p.Container("HUDW_TXR.ZED");
            int icon = Enumerable.Range(0, txr.Nodes.Count).FirstOrDefault(i => txr.Nodes[i].Name == "sigcommando_icon.tif", -1);
            if (icon < 0) throw new InvalidDataException("MP61's HUDW_TXR has no sigcommando_icon.tif");
            int texdat = ZedTree.Children(txr.Nodes, icon).FirstOrDefault(i => txr.Nodes[i].Name == "texdat", -1);
            if (texdat < 0) throw new InvalidDataException("sigcommando_icon.tif has no texdat");
            return (txr.Nodes[texdat].Data, ZedHeader.Of(txr), ZedHeader.Of(p.Container("HUDW_PAL.ZED")));
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
