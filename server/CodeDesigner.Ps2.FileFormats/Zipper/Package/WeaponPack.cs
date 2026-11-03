using CodeDesigner.Ps2.FileFormats.Zipper.Conversion;
using CodeDesigner.Ps2.FileFormats.Zipper.RDR;
using CodeDesigner.Ps2.FileFormats.Zipper.Texture;
using CodeDesigner.Ps2.FileFormats.Zipper.ZED;

namespace CodeDesigner.Ps2.FileFormats.Zipper.Package
{
    /// <summary>
    /// A custom weapon as one memory-card pack: everything under <c>RUN\MP\&lt;PACK&gt;\</c>
    /// <list type="bullet">
    /// <item>WEAP_TXR / PAL / GEO / MDL: a weapon-only library (category of M51's WEAP) with the model, its GEO
    /// definition (LODs, firepoint, aimpoint), its textures (renumbered 1..N, no mip levels) and their palettes;</item>
    /// <item>ZWEAPON.ZAR: the disc's weapon table with the donor weapon pointed at the model and icon, renamed and its
    /// fields (fire wait, ranges, magazine ...) set; ammo fields go to the donor's own copy of its ammo type;</item>
    /// <item>UIMNLOC.ZAR (when a text changes): the armory language table with the donor's name / caption / given
    /// keys rewritten;</item>
    /// <item>HUDX_TXR / PAL / GEO / MDL: the HUD icon (<see cref="WeaponIcon"/>) in a category 0x0C library.</item>
    /// </list>
    /// Loading it in game (mounting the pack, reading this weapon table and language table instead of the disc's) is
    /// the loader code's job; the pack is complete without it.
    /// </summary>
    public static class WeaponPack
    {
        private const int RegTex0 = 0x06, RegTex0_2 = 0x07, RegTex1 = 0x14, RegMipTbp1 = 0x34, RegMipTbp2 = 0x35;
        public const string Library = "WEAP", IconLibrary = "HUDX";

        public sealed class Options
        {
            public required string Pack { get; init; }
            public required string Model { get; init; }
            /// <summary>The weapon table record the custom weapon takes over (InternalName, DisplayName or ModelName).</summary>
            public required string Donor { get; init; }
            public string? Name { get; init; }
            public string? Caption { get; init; }
            public string? Description { get; init; }
            /// <summary>Donor weapon fields (FireWait, Effective_Range, Ammo_Capacity ...), as zweapon.rdr names them.</summary>
            public Dictionary<string, object> Stats { get; init; } = new();
            /// <summary>Fields of the donor's own ammo copy (ImpactDamage, Stun, Piercing, Explosion_Damage, Explosion_Radius).</summary>
            public Dictionary<string, object> Ammo { get; init; } = new();
            /// <summary>Language keys to rewrite (_1225_SealPistolHasMags_MSG → text).</summary>
            public Dictionary<string, string> Texts { get; init; } = new();
            public bool Icon { get; init; } = true;
        }

        public sealed class Result
        {
            public List<(string Path, byte[] Data)> Files { get; } = new();
            public string DonorInternalName { get; set; } = string.Empty;
            public string? OldName { get; set; }
            public string? OldDescription { get; set; }
            public Dictionary<string, object> Fields { get; set; } = new();
            public string? AmmoType { get; set; }
            public Dictionary<string, string> TextsChanged { get; } = new();
            public List<string> Textures { get; } = new();
            public List<int> Palettes { get; } = new();
            public string? IconTexture { get; set; }
            public List<string> Warnings { get; } = new();
        }

        public static Result Build(ZipperPackage pkg, Options o, Socom2Templates tpl)
        {
            PackageSave.CheckPackName(o.Pack);
            string pack = o.Pack.ToUpperInvariant();
            string Path_(string leaf) => $"RUN\\MP\\{pack}\\{leaf}";
            var result = new Result();
            var model = pkg.Models().FirstOrDefault(m => m.Name == o.Model)
                ?? throw new KeyNotFoundException($"No model {o.Model} in this package");

            // ---- the weapon library
            var mdl = ZedTree.Copy(pkg.Container(model.Archive).Nodes, model.NodeIndex);
            var def = pkg.FindDefinition(model) ?? throw new InvalidOperationException($"{o.Model} has no GEO definition (firepoint, LODs): make it with newWeapon or weapons_create");
            var geo = ZedTree.Copy(pkg.Container(pkg.Archive(def.Namespace, "geo")!.Leaf).Nodes, def.Node.Index);
            var textures = new List<ZedTree.Record>();
            var palettes = new List<ZedTree.Record>();
            string? ns = pkg.TextureNamespace(model.Namespace);
            // Textures the model draws with. A hidden visual's zero-area speck still names its old texture: its sections
            // are pointed at a texture the pack ships (name fields keep their size), else that texture ships too.
            var meshChunks = ZipperModelMesh.Build(pkg, model).Chunks;
            List<string> Named(IEnumerable<Assets.ModelMesh.MeshChunk> chunks) => chunks.Select(c => c.Texture).Where(t => !string.IsNullOrEmpty(t))
                .Select(t => t!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var names = Named(meshChunks.Where(Draws));
            foreach (string unused in Named(meshChunks).Except(names, StringComparer.OrdinalIgnoreCase).ToList())
            {
                string? stand = names.FirstOrDefault(n => n.Length <= unused.Length);
                if (stand == null) { names.Add(unused); continue; }
                mdl = RenameTexture(mdl, unused, stand);
            }
            foreach (string tif in names)
            {
                var found = pkg.FindTexture(tif, ns);
                if (found == null) { result.Warnings.Add($"Texture {tif} is not in the package; the pack leaves it out"); continue; }
                byte[] texdat = Renumber(found.Value.Texdat, textures.Count + 1);
                textures.Add(new ZedTree.Record(tif, Array.Empty<byte>(), new[] { new ZedTree.Record("texdat", texdat) }));
                result.Textures.Add(tif);
                var header = ZedTextureFile.ParseHeader(texdat);
                if (!header.IsIndexed) continue;
                int id = header.ResolvePaletteId();
                if (result.Palettes.Contains(id)) continue;
                var pal = PaletteRecord(pkg, id);
                if (pal == null) { result.Warnings.Add($"Palette texpal_{id} of {tif} is not in the package"); continue; }
                palettes.Add(pal);
                result.Palettes.Add(id);
            }
            var weap = tpl.StaticTemplate("weapon").Library;
            var lib = new List<(string, byte[])>();
            CaToSocom2.AddLibrary(lib, Library, weap, textures, palettes, geo, mdl);
            result.Files.AddRange(lib.Select(f => (Path_(f.Item1), f.Item2)));

            // ---- the HUD icon (its own category 0x0C library)
            if (o.Icon)
            {
                result.IconTexture = $"{o.Pack.ToLowerInvariant()}_icon.tif";
                int iconPal = CaToSocom2.PaletteBase(o.Pack);
                while (result.Palettes.Contains(iconPal)) iconPal++;
                var (pixels, palette) = WeaponIcon.Render(pkg, model);
                var (sample, txrHeader, palHeader) = tpl.HudIconTemplate();
                var iconTex = new ZedTree.Record(result.IconTexture, Array.Empty<byte>(), new[] { new ZedTree.Record("texdat", IconTexdat(sample, pixels, iconPal)) });
                var iconPalette = new ZedTree.Record("texpal_" + iconPal, Array.Empty<byte>(), new[]
                {
                    new ZedTree.Record("par", BitConverter.GetBytes((uint)iconPal).Concat(BitConverter.GetBytes(0x400u)).ToArray()),
                    new ZedTree.Record("buf", ZedTextureFile.EncodeClutBuffer(palette, false)),
                });
                result.Files.Add((Path_($"{IconLibrary}_TXR.ZED"), Socom2Templates.WriteZed(txrHeader, new[]
                    { new ZedTree.Record("renderphase", BitConverter.GetBytes(0x0Cu)), new ZedTree.Record("textures", Array.Empty<byte>(), new[] { iconTex }) })));
                result.Files.Add((Path_($"{IconLibrary}_PAL.ZED"), Socom2Templates.WriteZed(palHeader, new[] { new ZedTree.Record("palettes", Array.Empty<byte>(), new[] { iconPalette }) })));
                result.Files.Add((Path_($"{IconLibrary}_GEO.ZED"), Socom2Templates.WriteZed(weap.Geo, new[] { new ZedTree.Record("models", Array.Empty<byte>()) })));
                result.Files.Add((Path_($"{IconLibrary}_MDL.ZED"), Socom2Templates.WriteZed(weap.Mdl, Array.Empty<ZedTree.Record>())));
            }

            // ---- the weapon table
            var (zweapon, rdrIndex) = OpenZar(Path.Combine(tpl.RunDir, "ZWEAPON.ZAR"), "zweapon.rdr");
            var table = new WeaponTable(zweapon.Nodes[rdrIndex].Data);
            int w = table.FindWeapon(o.Donor);
            result.DonorInternalName = table.Get(w, "InternalName") as string ?? o.Donor;
            result.OldName = table.Get(w, "DisplayName") as string;
            result.OldDescription = table.Get(w, "Description") as string;
            table.Set(w, "ModelName", o.Model);
            if (result.IconTexture != null) table.Set(w, "IconTextureName", result.IconTexture);
            if (o.Name != null) table.Set(w, "DisplayName", Ascii(o.Name, "name"));
            string? description = o.Description ?? o.Name?.ToUpperInvariant();
            if (description != null) table.Set(w, "Description", Ascii(description, "description"));
            foreach (var (key, value) in o.Stats)
            {
                if (key is "ModelName" or "IconTextureName" or "InternalName" or "ID")
                    throw new ArgumentException($"{key} is set by the pack (model, icon) or keys the weapon (InternalName, ID); it cannot be a stat");
                table.Set(w, key, value);
            }
            if (o.Ammo.Count > 0)
            {
                string ammo = $"{o.Pack.ToLowerInvariant()}_ammo";
                int a = table.OwnAmmo(result.DonorInternalName, ammo);
                foreach (var (key, value) in o.Ammo)
                {
                    if (key is "InternalName" or "DisplayName" or "Description" or "ID") throw new ArgumentException($"{key} of the ammo copy is set by the pack");
                    table.Set(a, key, value);
                }
                result.AmmoType = ammo;
            }
            result.Fields = table.Fields(table.FindWeapon(result.DonorInternalName));
            zweapon.ReplaceNodeData(rdrIndex, table.ToBytes());
            result.Files.Add((Path_("ZWEAPON.ZAR"), zweapon.ToBytes()));

            // ---- the armory's language table
            string locPath = Path.Combine(tpl.RunDir, "LOCALE", "STATES", "UIMNLOC.ZAR");
            if (o.Name != null || o.Caption != null || o.Texts.Count > 0)
            {
                var (loc, locIndex) = OpenZar(locPath, ".rdr");
                var lang = new LanguageTable(loc.Nodes[locIndex].Data);
                if (o.Name != null && result.OldName != null)
                    foreach (string key in lang.KeysWithText(result.OldName))
                        Change(lang, result, key, o.Name);
                if (result.OldDescription != null && (o.Caption != null || description != null))
                    foreach (var (key, kind) in lang.Captions(result.OldDescription))
                        Change(lang, result, key, o.Caption ?? $"{kind}: {description}");
                foreach (var (key, text) in o.Texts) Change(lang, result, key, Ascii(text, key));
                if (result.TextsChanged.Count == 0) result.Warnings.Add($"No language entry shows {result.OldName}; the armory keeps its texts");
                loc.ReplaceNodeData(locIndex, lang.ToBytes());
                result.Files.Add((Path_("UIMNLOC.ZAR"), loc.ToBytes()));
            }
            return result;
        }

        private static ZedTree.Record RenameTexture(ZedTree.Record r, string from, string to) =>
            r with { Data = PackageImport.Rename(r.Data, from, to, out _), Children = r.Children.Select(c => RenameTexture(c, from, to)).ToList() };

        /// <summary>A chunk with at least one triangle that has area.</summary>
        private static bool Draws(Assets.ModelMesh.MeshChunk c)
        {
            float[] p = c.Positions;
            for (int t = 0; t + 2 < c.Triangles.Length; t += 3)
            {
                int a = c.Triangles[t] * 3, b = c.Triangles[t + 1] * 3, d = c.Triangles[t + 2] * 3;
                float ex = p[b] - p[a], ey = p[b + 1] - p[a + 1], ez = p[b + 2] - p[a + 2];
                float fx = p[d] - p[a], fy = p[d + 1] - p[a + 1], fz = p[d + 2] - p[a + 2];
                float cx = ey * fz - ez * fy, cy = ez * fx - ex * fz, cz = ex * fy - ey * fx;
                if (cx * cx + cy * cy + cz * cz > 1e-12f) return true;
            }
            return false;
        }

        private static void Change(LanguageTable lang, Result r, string key, string text)
        {
            lang.SetText(key, text);
            r.TextsChanged[key] = text;
        }

        private static string Ascii(string s, string what) =>
            s.Length > 0 && s.All(c => c >= 0x20 && c <= 0x7E) ? s : throw new ArgumentException($"The {what} must be printable ASCII text");

        private static (ZedContainer Zar, int Index) OpenZar(string path, string record)
        {
            if (!File.Exists(path)) throw new FileNotFoundException($"{path} is needed (the game's RUN folder)", path);
            var zar = ZedContainer.FromBytes(File.ReadAllBytes(path));
            int i = Enumerable.Range(0, zar.Nodes.Count).FirstOrDefault(n => record.StartsWith('.')
                ? zar.Nodes[n].Name.EndsWith(record, StringComparison.OrdinalIgnoreCase)
                : zar.Nodes[n].Name.Equals(record, StringComparison.OrdinalIgnoreCase), -1);
            if (i < 0) throw new InvalidDataException($"{Path.GetFileName(path)} has no {record}");
            return (zar, i);
        }

        /// <summary>texpal_&lt;id&gt; from whichever PAL archive of the package holds it.</summary>
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

        /// <summary>A texdat as texture <paramref name="index"/> of a new TXR (header +0x08 = TEX0.TBP0), mip levels off.</summary>
        internal static byte[] Renumber(byte[] texdat, int index)
        {
            byte[] data = (byte[])texdat.Clone();
            var header = ZedTextureFile.ParseHeader(data);
            BitConverter.GetBytes((uint)index).CopyTo(data, 8);
            for (int o = ZedTextureFile.HeaderSize + header.PixelBytes; o + 16 <= data.Length; o += 16)
            {
                uint reg = BitConverter.ToUInt32(data, o + 8);
                if (BitConverter.ToUInt32(data, o + 12) != 0 || reg > 0x7F) continue;
                ulong v = BitConverter.ToUInt64(data, o);
                if (reg is RegTex0 or RegTex0_2) v = (v & ~0x3FFFUL) | (uint)index;
                else if (reg == RegTex1) v &= ~(7UL << 2);
                else if (reg is RegMipTbp1 or RegMipTbp2) v = 0;
                else continue;
                BitConverter.GetBytes(v).CopyTo(data, o);
            }
            return data;
        }

        /// <summary>The icon texdat: the retail icon's header and GIF tail around our pixels, texture 1, our palette.</summary>
        private static byte[] IconTexdat(byte[] sample, byte[] pixels, int paletteId)
        {
            int size = (int)BitConverter.ToUInt32(sample, 4);
            if (size != pixels.Length) throw new InvalidDataException($"The retail icon holds {size} pixel bytes, not {pixels.Length}");
            byte[] data = sample[..ZedTextureFile.HeaderSize].Concat(pixels).Concat(sample[(ZedTextureFile.HeaderSize + size)..]).ToArray();
            data = Renumber(data, 1);
            for (int o = ZedTextureFile.HeaderSize + size; o + 16 <= data.Length; o += 16)
            {
                if (BitConverter.ToUInt32(data, o + 12) != 0 || BitConverter.ToUInt32(data, o + 8) is not (RegTex0 or RegTex0_2)) continue;
                ulong v = BitConverter.ToUInt64(data, o);
                v = (v & ~(0x3FFFUL << 37)) | ((ulong)paletteId << 37);
                BitConverter.GetBytes(v).CopyTo(data, o);
            }
            return data;
        }

        /// <summary>
        /// Writes the pack as <c>&lt;outputDir&gt;/&lt;PACK&gt;.CDB</c> plus its archives loose in
        /// <c>&lt;outputDir&gt;/&lt;PACK&gt;/</c> (to inspect), and installs it on a PCSX2 folder card when given.
        /// </summary>
        public static List<string> Write(Result r, string pack, string outputDir, string? folderCard)
        {
            var written = PackageSave.WriteCdb(r.Files, pack, outputDir, folderCard);
            string loose = Path.Combine(outputDir, pack.ToUpperInvariant());
            Directory.CreateDirectory(loose);
            foreach (var (path, data) in r.Files)
            {
                string file = Path.Combine(loose, path.Split('\\').Last());
                File.WriteAllBytes(file, data);
                written.Add(file);
            }
            return written;
        }
    }
}
