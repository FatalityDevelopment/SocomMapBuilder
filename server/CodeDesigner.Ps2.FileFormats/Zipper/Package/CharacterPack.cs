using CodeDesigner.Ps2.FileFormats.Zipper.Conversion;
using CodeDesigner.Ps2.FileFormats.Zipper.Texture;
using CodeDesigner.Ps2.FileFormats.Zipper.ZED;

namespace CodeDesigner.Ps2.FileFormats.Zipper.Package
{
    /// <summary>
    /// A session's character as one memory-card pack, the layout of the CA ports confirmed in game
    /// (Mods/SOCOMCA/ca2s2.py): <c>RUN\MP\&lt;PACK&gt;\CLIB_TXR / PAL / GEO / MDL.ZED</c> holding one model under a
    /// donor's name. Named as a character the map does not load (con_pow01 is spawned by the mp_pow1 chartype, only on
    /// MP10 / MP11), the game's own chartype spawns it once the pack is loaded.
    /// <list type="bullet">
    /// <item>MDL: the mesh record (its payload as edited, so added gear comes along) as <c>MESH_&lt;donor&gt;</c>;</item>
    /// <item>GEO: its skeleton as <c>&lt;donor&gt;</c>;</item>
    /// <item>TXR: the textures its sections draw with, renumbered 1..N, mip levels off;</item>
    /// <item>PAL: their palettes, renumbered into the pack's own block of 32 ids (palette ids are global across all
    /// that is loaded, first registered wins), each texture's TEX0.CBP pointed at the new id.</item>
    /// </list>
    /// The archive headers and render phase are the package's own CLIB library's.
    /// </summary>
    public static class CharacterPack
    {
        private const int RegTex0 = 0x06, RegTex0_2 = 0x07, PalettesPerPack = 32;

        public sealed class Result
        {
            public List<(string Path, byte[] Data)> Files { get; } = new();
            public string Mesh { get; set; } = string.Empty;
            public string Skeleton { get; set; } = string.Empty;
            public List<string> Textures { get; } = new();
            /// <summary>Old palette id → the pack's.</summary>
            public Dictionary<int, int> Palettes { get; } = new();
            public List<string> Warnings { get; } = new();
        }

        /// <param name="model">The session's skinned mesh (MESH_seal_A_woodland_LO).</param>
        /// <param name="donor">The character name it takes (con_pow01, with or without MESH_).</param>
        public static Result Build(ZipperPackage pkg, string model, string donor, string pack)
        {
            PackageSave.CheckPackName(pack);
            donor = CharacterMesh.SkeletonName(donor.Trim());
            if (donor.Length == 0 || !donor.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
                throw new ArgumentException("The donor is a character name of letters, digits and _ (e.g. con_pow01)");
            var m = pkg.Models().FirstOrDefault(x => x.Name.Equals(model, StringComparison.OrdinalIgnoreCase))
                ?? throw new KeyNotFoundException($"No model {model} in this package");
            var container = pkg.Container(m.Archive);
            if (CharacterMesh.StartOffset(container, m.NodeIndex) == null) throw new ArgumentException($"{m.Name} is not a skinned character mesh");

            var result = new Result { Mesh = "MESH_" + donor, Skeleton = donor };
            var mdl = ZedTree.Copy(container.Nodes, m.NodeIndex);
            mdl = mdl with
            {
                Name = result.Mesh,
                Children = mdl.Children.Select(c => c.Name.Equals(m.Name + "_START", StringComparison.OrdinalIgnoreCase) ? c with { Name = result.Mesh + "_START" } : c).ToList(),
            };

            string skeletonName = CharacterMesh.SkeletonName(m.Name);
            var (geoLeaf, skeleton) = Skeleton(pkg, m.Namespace, skeletonName)
                ?? throw new InvalidOperationException($"{m.Name} has no skeleton {skeletonName} in a GEO archive");
            var geo = ZedTree.Copy(pkg.Container(geoLeaf).Nodes, skeleton) with { Name = donor };

            // Textures the chain draws with (sections removed from the chain are not drawn, so not shipped).
            string? ns = pkg.TextureNamespace(m.Namespace);
            var textures = new List<ZedTree.Record>();
            var palettes = new List<ZedTree.Record>();
            int paletteBase = CaToSocom2.PaletteBase(pack);
            var names = ZipperModelMesh.Build(pkg, m).Chunks.Select(c => c.Texture).Where(t => !string.IsNullOrEmpty(t))
                .Select(t => t!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (string tif in names)
            {
                var found = pkg.FindTexture(tif, ns);
                if (found == null) { result.Warnings.Add($"Texture {tif} is not in the package; the pack leaves it out"); continue; }
                byte[] texdat = WeaponPack.Renumber(found.Value.Texdat, textures.Count + 1);
                var header = ZedTextureFile.ParseHeader(texdat);
                if (header.IsIndexed)
                {
                    int old = header.ResolvePaletteId();
                    if (!result.Palettes.TryGetValue(old, out int id))
                    {
                        if (result.Palettes.Count == PalettesPerPack) throw new InvalidOperationException($"The pack's textures use more than {PalettesPerPack} palettes");
                        var pal = PaletteRecord(pkg, old);
                        if (pal == null) { result.Warnings.Add($"Palette texpal_{old} of {tif} is not in the package; the texture keeps palette {old}"); }
                        else
                        {
                            id = paletteBase + result.Palettes.Count;
                            result.Palettes[old] = id;
                            palettes.Add(Renamed(pal, id));
                        }
                    }
                    if (result.Palettes.TryGetValue(old, out id)) texdat = WithPalette(texdat, header, id);
                }
                textures.Add(new ZedTree.Record(tif, Array.Empty<byte>(), new[] { new ZedTree.Record("texdat", texdat) }));
                result.Textures.Add(tif);
            }

            var lib = Library(pkg, m.Namespace);
            var files = new List<(string, byte[])>();
            CaToSocom2.AddLibrary(files, "CLIB", lib, textures, palettes, geo, mdl);
            string upper = pack.ToUpperInvariant();
            result.Files.AddRange(files.Select(f => ($"RUN\\MP\\{upper}\\{f.Item1}", f.Item2)));
            return result;
        }

        private static (string Leaf, int Index)? Skeleton(ZipperPackage pkg, string preferred, string name)
        {
            foreach (var e in pkg.Entries.Where(e => e.Kind == "geo" && e.Namespace != null)
                         .OrderBy(e => string.Equals(e.Namespace, preferred, StringComparison.OrdinalIgnoreCase) ? 0 : 1))
                if (pkg.Scene(e.Namespace!)?.FindModel(name) is { } node) return (e.Leaf, node.Index);
            return null;
        }

        // The package's own library for the namespace: archive headers and the TXR's render phase.
        private static Socom2Templates.Library Library(ZipperPackage pkg, string ns)
        {
            ZedContainer Of(string kind) => pkg.Container((pkg.Archive(ns, kind) ?? throw new InvalidOperationException($"The package has no {ns.ToUpperInvariant()}_{kind.ToUpperInvariant()}.ZED")).Leaf);
            var txr = Of("txr");
            var parents = ZedTree.ParentIndices(txr.Nodes);
            int phase = Enumerable.Range(0, txr.Nodes.Count).FirstOrDefault(i => parents[i] < 0 && txr.Nodes[i].Name == "renderphase", -1);
            if (phase < 0) throw new InvalidDataException($"{ns.ToUpperInvariant()}_TXR.ZED has no renderphase");
            return new Socom2Templates.Library(ns.ToUpperInvariant(), Socom2Templates.ZedHeader.Of(txr), Socom2Templates.ZedHeader.Of(Of("pal")),
                Socom2Templates.ZedHeader.Of(Of("geo")), Socom2Templates.ZedHeader.Of(Of("mdl")), ZedTree.Copy(txr.Nodes, phase));
        }

        private static ZedTree.Record? PaletteRecord(ZipperPackage pkg, int id)
        {
            foreach (var e in pkg.Entries.Where(e => e.Kind == "pal"))
            {
                var c = pkg.Container(e.Leaf);
                int at = PackageImport.TopRecordIndex(c, "texpal_" + id);
                if (at >= 0) return ZedTree.Copy(c.Nodes, at);
            }
            return null;
        }

        // texpal_<id> with its par record's first word (the id) set.
        private static ZedTree.Record Renamed(ZedTree.Record pal, int id) => pal with
        {
            Name = "texpal_" + id,
            Children = pal.Children.Select(c => c.Name == "par" && c.Data.Length >= 4
                ? c with { Data = BitConverter.GetBytes((uint)id).Concat(c.Data.Skip(4)).ToArray() } : c).ToList(),
        };

        // TEX0.CBP (bits 37-50) of the texdat's GIF tail set to the palette id.
        private static byte[] WithPalette(byte[] texdat, ZedTextureFile.Header header, int id)
        {
            byte[] data = (byte[])texdat.Clone();
            for (int o = ZedTextureFile.HeaderSize + header.PixelBytes; o + 16 <= data.Length; o += 16)
            {
                if (BitConverter.ToUInt32(data, o + 12) != 0 || BitConverter.ToUInt32(data, o + 8) is not (RegTex0 or RegTex0_2)) continue;
                ulong v = BitConverter.ToUInt64(data, o);
                v = (v & ~(0x3FFFUL << 37)) | ((ulong)id << 37);
                BitConverter.GetBytes(v).CopyTo(data, o);
            }
            return data;
        }
    }
}
