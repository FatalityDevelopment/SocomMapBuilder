using System.Text;
using CodeDesigner.Ps2.FileFormats.Zipper.Geo;
using CodeDesigner.Ps2.FileFormats.Zipper.Model;
using CodeDesigner.Ps2.FileFormats.Zipper.Texture;
using CodeDesigner.Ps2.FileFormats.Zipper.ZED;

namespace CodeDesigner.Ps2.FileFormats.Zipper.Package
{
    /// <summary>
    /// Copies a model from another SOCOM II package into this one: its MDL record (visual stubs included), its GEO
    /// definition (a character's skeleton), the textures it names with their mip levels and palettes, and the models
    /// its GEO definition references that this package lacks.
    /// <para>
    /// Texture names and palette ids are global in game (first registered wins), so imported palettes get ids no
    /// palette of this package uses, and a texture whose name this package already uses for other pixels is renamed
    /// (same length, so it fits the model's name fields). Imported textures take the next texture indices of the
    /// target TXR: header +0x08 = TEX0.TBP0, 1..N per archive as on the disc; MIPTBP1/2 point at the mip levels'
    /// indices and are remapped with them.
    /// </para>
    /// </summary>
    public static class PackageImport
    {
        private const int RegTex0 = 0x06, RegTex0_2 = 0x07, RegMipTbp1 = 0x34, RegMipTbp2 = 0x35;

        public sealed class Result
        {
            public Dictionary<string, byte[]> Changed { get; } = new(StringComparer.OrdinalIgnoreCase);
            public string Archive { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            /// <summary>Every model copied: the requested one first, then the ones its GEO definition references.</summary>
            public List<string> Models { get; } = new();
            public List<string> Textures { get; } = new();
            /// <summary>Textures this package already had with the same pixels; the model uses those.</summary>
            public List<string> ReusedTextures { get; } = new();
            public Dictionary<string, string> RenamedTextures { get; } = new(StringComparer.OrdinalIgnoreCase);
            /// <summary>Source palette id → new id.</summary>
            public Dictionary<int, int> Palettes { get; } = new();
            public List<string> Warnings { get; } = new();
        }

        /// <summary>
        /// Imports <paramref name="sourceArchive"/>:<paramref name="name"/> from <paramref name="source"/>.
        /// <paramref name="targetArchive"/> picks the *_MDL.ZED that receives it (default: the same namespace when
        /// this package has it, else the map's own); <paramref name="newName"/> renames the copy.
        /// </summary>
        public static Result ImportModel(ZipperPackage target, ZipperPackage source, string sourceArchive, string name, string? targetArchive = null, string? newName = null)
        {
            var model = source.FindModel(sourceArchive, name) ?? throw new KeyNotFoundException($"No model {sourceArchive}:{name} in {source.SourcePath}");
            if (model.Kind == "world") throw new ArgumentException("A map's worldmodel cannot be imported");
            var mdl = ChooseArchive(target, model, targetArchive);
            var job = new Job(target, source, mdl, target.TextureNamespace(mdl.Namespace!) ?? throw new InvalidOperationException($"{mdl.Leaf} has no TXR archive beside it"));
            string finalName = string.IsNullOrWhiteSpace(newName) ? model.Name : newName.Trim();
            job.Import(model, finalName, requested: true);
            job.Finish();
            job.Result.Name = finalName;
            return job.Result;
        }

        /// <summary>
        /// Copies one texture (its mip levels and palette too) from <paramref name="source"/> into the TXR/PAL pair
        /// of <paramref name="targetNs"/> (default: the source's namespace when this package has it, else the map's).
        /// <paramref name="newName"/> names the copy; otherwise a name this package already uses is renamed.
        /// </summary>
        public static Result ImportTexture(ZipperPackage target, ZipperPackage source, string tifName, string? sourceNs = null, string? targetNs = null, string? newName = null)
        {
            var found = source.FindTexture(tifName, sourceNs) ?? throw new KeyNotFoundException($"No texture {tifName} in {source.SourcePath}");
            string ns = !string.IsNullOrWhiteSpace(targetNs) ? targetNs.Trim().ToLowerInvariant()
                : target.HasArchive(found.Namespace, "txr") ? found.Namespace
                : target.MapNamespace ?? throw new ArgumentException("Pick the texture archive (namespace) to import into");
            if (!target.HasArchive(ns, "txr") || !target.HasArchive(ns, "pal")) throw new ArgumentException($"This package has no {ns.ToUpperInvariant()}_TXR.ZED / _PAL.ZED pair");
            var job = new Job(target, source, null, ns);
            string final = job.ImportTexture(tifName, found.Namespace, string.IsNullOrWhiteSpace(newName) ? null : newName.Trim());
            job.Finish();
            job.Result.Archive = target.Archive(ns, "txr")!.Leaf;
            job.Result.Name = final;
            return job.Result;
        }

        private static ZipperPackage.RootEntry ChooseArchive(ZipperPackage target, ZipperPackage.ModelRef model, string? requested)
        {
            if (!string.IsNullOrWhiteSpace(requested))
                return target.Entry(requested) is { Kind: "mdl" } e && e.Namespace != "worl" ? e
                    : throw new ArgumentException($"{requested} is not a model archive of this package");
            if (target.Archive(model.Namespace, "mdl") is { } same) return same;
            if (model.Kind != "character" && target.MapNamespace != null && target.Archive(target.MapNamespace, "mdl") is { } map) return map;
            throw new ArgumentException($"This package has no {model.Namespace.ToUpperInvariant()}_MDL.ZED; pick the archive to import into");
        }

        private sealed class Job
        {
            private readonly ZipperPackage _target, _source;
            // MDL / GEO are null for a texture-only import.
            private readonly ZipperPackage.RootEntry? _mdlEntry, _geoEntry;
            private readonly ZipperPackage.RootEntry _txrEntry, _palEntry;
            private readonly ZedContainer? _mdl, _geo;
            private readonly ZedContainer _txr, _pal;
            private readonly HashSet<string> _usedTextureNames = new(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<int> _usedPalettes = new();
            private readonly Dictionary<string, string> _textureMap = new(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<string> _imported = new(StringComparer.OrdinalIgnoreCase);
            private int _nextTextureIndex;
            private int _nextPalette;
            public Result Result { get; } = new();

            public Job(ZipperPackage target, ZipperPackage source, ZipperPackage.RootEntry? mdl, string texNs)
            {
                _target = target;
                _source = source;
                if (mdl != null)
                {
                    string ns = mdl.Namespace!;
                    _mdlEntry = mdl;
                    _geoEntry = target.Archive(ns, "geo") ?? throw new InvalidOperationException($"No {ns.ToUpperInvariant()}_GEO.ZED for {mdl.Leaf}");
                    _mdl = ZedContainer.FromBytes(_mdlEntry.Data);
                    _geo = ZedContainer.FromBytes(_geoEntry.Data);
                    Result.Archive = mdl.Leaf;
                }
                _txrEntry = target.Archive(texNs, "txr") ?? throw new InvalidOperationException($"No {texNs.ToUpperInvariant()}_TXR.ZED");
                _palEntry = target.Archive(texNs, "pal") ?? throw new InvalidOperationException($"No {texNs.ToUpperInvariant()}_PAL.ZED");
                _txr = ZedContainer.FromBytes(_txrEntry.Data);
                _pal = ZedContainer.FromBytes(_palEntry.Data);

                _usedTextureNames.UnionWith(UsedTextureNames(target));
                _usedPalettes.UnionWith(UsedPaletteIds(target));
                _nextTextureIndex = NextTextureIndex(_txr);
                _nextPalette = PaletteBlock(target);
            }

            public void Import(ZipperPackage.ModelRef model, string newName, bool requested)
            {
                if (_mdl == null || _geo == null) throw new InvalidOperationException("Texture-only import");
                if (!_imported.Add(model.Name)) return;
                bool character = model.Name.StartsWith("MESH_", StringComparison.OrdinalIgnoreCase);
                if (character && !newName.StartsWith("MESH_", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException($"A character's mesh name starts with MESH_ ({newName})");
                if (TopLevel(_mdl, newName) >= 0 || _target.ResolveModel(newName) is { } clash && clash.Name.Equals(newName, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException($"This package already has a model named {newName}; import it under another name");

                var srcNodes = _source.Container(model.Archive).Nodes;
                ZedTree.Record mdl = ZedTree.Copy(srcNodes, model.NodeIndex);

                // GEO: a character's skeleton, else the model's definition (props, weapons, vehicles).
                (GeoNode Node, string Namespace)? def = character
                    ? _source.FindSkeleton(model.Namespace, CharacterMesh.SkeletonName(model.Name)) is { } sk ? (sk, NamespaceOf(_source, sk, model.Namespace)) : null
                    : _source.FindDefinition(model);
                ZedTree.Record? geo = null;
                if (def is { } d)
                {
                    var geoNodes = _source.Container(_source.Archive(d.Namespace, "geo")!.Leaf).Nodes;
                    geo = ZedTree.Copy(geoNodes, d.Node.Index);
                }
                else Result.Warnings.Add($"{model.Name} has no GEO definition in the source; only its MDL record was copied");

                // Textures: copy (or reuse) each, then point the payload at the names they ended up with.
                byte[] payload = mdl.Data;
                foreach (string tif in TextureNames(model))
                {
                    string final = ImportTexture(tif, _source.TextureNamespace(model.Namespace));
                    if (final.Equals(tif, StringComparison.OrdinalIgnoreCase)) continue;
                    payload = Rename(payload, tif, final, out int fields);
                    if (fields == 0) Result.Warnings.Add($"{model.Name}: no name field for {tif} to point at {final}");
                }
                mdl = mdl with { Data = payload };

                if (!newName.Equals(model.Name, StringComparison.Ordinal))
                {
                    mdl = mdl with
                    {
                        Name = newName,
                        Children = mdl.Children.Select(c => c.Name.Equals(model.Name + "_START", StringComparison.Ordinal) ? c with { Name = newName + "_START" } : c).ToList(),
                    };
                    if (geo != null) geo = geo with { Name = character ? CharacterMesh.SkeletonName(newName) : newName };
                }

                AppendTop(_mdl, mdl);
                if (geo != null)
                {
                    int models = TopLevel(_geo, "models");
                    if (models < 0) throw new InvalidOperationException($"{_geoEntry!.Leaf} has no models list");
                    ZedTree.AppendChild(_geo, models, geo);
                }
                Result.Models.Add(newName);

                // Props built from other models (a GEO definition's model_name references) bring those along.
                if (geo != null)
                    foreach (string reference in References(geo).Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        if (_target.ResolveModel(reference) != null || _imported.Contains(reference)) continue;
                        if (_source.ResolveModel(reference) is { } sub && sub.Kind != "world") Import(sub, sub.Name, requested: false);
                        else Result.Warnings.Add($"{newName} references {reference}, which neither package has");
                    }
            }

            public void Finish()
            {
                if (_mdl != null) Result.Changed[_mdlEntry!.Leaf] = _mdl.ToBytes();
                if (_geo != null) Result.Changed[_geoEntry!.Leaf] = _geo.ToBytes();
                if (Result.Textures.Count > 0) Result.Changed[_txrEntry.Leaf] = _txr.ToBytes();
                if (Result.Palettes.Count > 0) Result.Changed[_palEntry.Leaf] = _pal.ToBytes();
            }

            private IEnumerable<string> TextureNames(ZipperPackage.ModelRef model)
            {
                var decoded = _source.DecodeModel(model);
                var names = decoded.Chunks.Select(c => c.TextureName).Where(t => !string.IsNullOrEmpty(t)).Select(t => t!).ToList();
                if (names.Count == 0 && ZipperModelMesh.TryBuild(_source, model) is { } mesh)
                    names = mesh.Chunks.Select(c => c.Texture).Where(t => !string.IsNullOrEmpty(t)).Select(t => t!).ToList();
                return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }

            /// <summary>Copies a source texture (and its mip levels) into the target TXR; answers the name it has there.</summary>
            public string ImportTexture(string tif, string? preferredNs, string? forcedName = null)
            {
                if (forcedName != null)
                {
                    if (!forcedName.EndsWith(".tif", StringComparison.OrdinalIgnoreCase)) forcedName += ".tif";
                    if (forcedName.Length > 31) throw new ArgumentException($"{forcedName} is longer than a texture name field (31 characters)");
                    if (_usedTextureNames.Contains(forcedName)) throw new ArgumentException($"This package already has a texture named {forcedName}");
                }
                else if (_textureMap.TryGetValue(tif, out var done)) return done;
                var found = _source.FindTexture(tif, preferredNs);
                if (found == null)
                {
                    Result.Warnings.Add($"Texture {tif} is not in the source package");
                    _textureMap[tif] = tif;
                    return tif;
                }
                string ns = found.Value.Namespace;

                // Same name, same pixels: the target's copy serves (names are global in game anyway).
                if (forcedName == null && _usedTextureNames.Contains(tif))
                {
                    if (SamePixels(tif, ns))
                    {
                        _textureMap[tif] = tif;
                        Result.ReusedTextures.Add(tif);
                        return tif;
                    }
                }

                var srcTxr = _source.Container(_source.Archive(ns, "txr")!.Leaf);
                int record = TopRecordIndex(srcTxr, tif);
                var group = MipGroup(srcTxr, record);
                // Index remap for the whole group so MIPTBP can follow it.
                var indexMap = new Dictionary<int, int>();
                foreach (int r in group) indexMap[TexdatIndexOf(srcTxr, r) is int t ? (int)BitConverter.ToUInt32(srcTxr.Nodes[t].Data, 8) : -1] = _nextTextureIndex++;

                string final = tif;
                foreach (int r in group)
                {
                    string srcName = srcTxr.Nodes[r].Name;
                    string dstName = srcName;
                    // A renamed texture's mip levels follow its new name (<name>_mipN.tif).
                    if (forcedName != null) dstName = r == record ? forcedName : Path.GetFileNameWithoutExtension(forcedName) + "_mip" + group.IndexOf(r) + ".tif";
                    if (_usedTextureNames.Contains(dstName)) dstName = FreeName(dstName);
                    _usedTextureNames.Add(dstName);
                    if (r == record) final = dstName;
                    if (!dstName.Equals(srcName, StringComparison.OrdinalIgnoreCase)) Result.RenamedTextures[srcName] = dstName;
                    _textureMap[srcName] = dstName;

                    ZedTree.Record copy = ZedTree.Copy(srcTxr.Nodes, r) with { Name = dstName };
                    copy = copy with
                    {
                        Children = copy.Children.Select(c => c.Name == "texdat" ? c with { Data = PatchTexdat(c.Data, indexMap, ns) } : c).ToList(),
                    };
                    int textures = TopLevel(_txr, "textures");
                    if (textures < 0) throw new InvalidOperationException($"{_txrEntry.Leaf} has no textures list");
                    ZedTree.AppendChild(_txr, textures, copy);
                    Result.Textures.Add(dstName);
                }
                return final;
            }

            private bool SamePixels(string tif, string sourceNs)
            {
                try
                {
                    var a = _source.DecodeTexture(tif, sourceNs);
                    var b = _target.DecodeTexture(tif, null);
                    return a != null && b != null && a.Value.Texture.Header.Width == b.Value.Texture.Header.Width
                        && a.Value.Texture.Header.Height == b.Value.Texture.Header.Height && a.Value.Texture.Rgba.AsSpan().SequenceEqual(b.Value.Texture.Rgba);
                }
                catch (InvalidDataException) { return false; }
            }

            /// <summary>A texture and the mip levels its MIPTBP registers point at (by texture index, same TXR).</summary>
            private static List<int> MipGroup(ZedContainer txr, int record)
            {
                var group = new List<int> { record };
                int? texdat = TexdatIndexOf(txr, record);
                if (texdat == null) return group;
                var byIndex = new Dictionary<int, int>();
                foreach (int r in TopTextureRecords(txr))
                    if (TexdatIndexOf(txr, r) is int t && txr.Nodes[t].Data.Length >= 12) byIndex[(int)BitConverter.ToUInt32(txr.Nodes[t].Data, 8)] = r;
                int self = (int)BitConverter.ToUInt32(txr.Nodes[texdat.Value].Data, 8);
                foreach (var (reg, lo, hi) in Registers(txr.Nodes[texdat.Value].Data))
                {
                    if (reg != RegMipTbp1 && reg != RegMipTbp2) continue;
                    ulong v = ((ulong)hi << 32) | lo;
                    for (int level = 0; level < 3; level++)
                    {
                        int tbp = (int)((v >> (level * 20)) & 0x3FFF);
                        if (tbp != 0 && tbp != self && byIndex.TryGetValue(tbp, out int r) && !group.Contains(r)) group.Add(r);
                    }
                }
                return group;
            }

            /// <summary>New texture index (header +0x08, TBP0, MIPTBP) and palette id (TEX0.CBP) for a copied texdat.</summary>
            private byte[] PatchTexdat(byte[] texdat, Dictionary<int, int> indexMap, string sourceNs)
            {
                byte[] data = (byte[])texdat.Clone();
                var header = ZedTextureFile.ParseHeader(data);
                int oldIndex = (int)BitConverter.ToUInt32(data, 8);
                int newIndex = indexMap.TryGetValue(oldIndex, out int ni) ? ni : _nextTextureIndex++;
                BitConverter.GetBytes((uint)newIndex).CopyTo(data, 8);
                int? newPalette = header.IsIndexed ? ImportPalette(header.ResolvePaletteId(), sourceNs) : null;

                int tail = ZedTextureFile.HeaderSize + header.PixelBytes;
                for (int o = tail; o + 16 <= data.Length; o += 16)
                {
                    uint reg = BitConverter.ToUInt32(data, o + 8), regHi = BitConverter.ToUInt32(data, o + 12);
                    if (regHi != 0 || reg > 0x7F) continue;
                    ulong v = BitConverter.ToUInt64(data, o);
                    if (reg is RegTex0 or RegTex0_2)
                    {
                        v = (v & ~0x3FFFUL) | (uint)newIndex;
                        if (newPalette is int p) v = (v & ~(0x3FFFUL << 37)) | ((ulong)p << 37);
                    }
                    else if (reg is RegMipTbp1 or RegMipTbp2)
                    {
                        for (int level = 0; level < 3; level++)
                        {
                            int shift = level * 20;
                            int tbp = (int)((v >> shift) & 0x3FFF);
                            if (tbp != 0 && indexMap.TryGetValue(tbp, out int mapped)) v = (v & ~(0x3FFFUL << shift)) | ((ulong)mapped << shift);
                        }
                    }
                    else continue;
                    BitConverter.GetBytes(v).CopyTo(data, o);
                }
                return data;
            }

            /// <summary>Copies source palette <paramref name="id"/> under a new id (once per id); answers the new id.</summary>
            private int ImportPalette(int id, string sourceNs)
            {
                if (Result.Palettes.TryGetValue(id, out int mapped)) return mapped;
                var pal = _source.Archive(sourceNs, "pal") is { } own && TopRecordIndex(_source.Container(own.Leaf), "texpal_" + id) >= 0
                    ? _source.Container(own.Leaf)
                    : _source.Entries.Where(e => e.Kind == "pal").Select(e => _source.Container(e.Leaf)).FirstOrDefault(c => TopRecordIndex(c, "texpal_" + id) >= 0)
                    ?? throw new KeyNotFoundException($"Palette texpal_{id} is not in the source package");
                int record = TopRecordIndex(pal, "texpal_" + id);
                // The source's id serves when this package does not use it (it is already apart from the disc's);
                // otherwise the next free id of this package's block.
                int newId = id;
                if (_usedPalettes.Contains(newId) || newId <= 0) newId = _nextPalette = FreePaletteId(_usedPalettes, _nextPalette);
                _usedPalettes.Add(newId);
                ZedTree.Record copy = ZedTree.Copy(pal.Nodes, record) with { Name = "texpal_" + newId };
                copy = copy with
                {
                    Children = copy.Children.Select(c =>
                    {
                        if (c.Name != "par" || c.Data.Length < 4) return c;
                        byte[] par = (byte[])c.Data.Clone();
                        BitConverter.GetBytes((uint)newId).CopyTo(par, 0);
                        return c with { Data = par };
                    }).ToList(),
                };
                int palettes = TopLevel(_pal, "palettes");
                if (palettes < 0) throw new InvalidOperationException($"{_palEntry.Leaf} has no palettes list");
                ZedTree.AppendChild(_pal, palettes, copy);
                Result.Palettes[id] = newId;
                return newId;
            }

            /// <summary>A free texture name of the same length (so it fits every name field the old one did).</summary>
            private string FreeName(string tif)
            {
                string ext = Path.GetExtension(tif);
                string stem = tif[..^ext.Length];
                const string digits = "0123456789abcdefghijklmnopqrstuvwxyz";
                for (int k = 1; k < 36 * 36; k++)
                {
                    string suffix = $"{digits[k / 36]}{digits[k % 36]}";
                    string candidate = (stem.Length > 2 ? stem[..^2] : stem) + suffix + ext;
                    if (!_usedTextureNames.Contains(candidate)) return candidate;
                }
                throw new InvalidOperationException($"No free name for {tif}");
            }

            private static string NamespaceOf(ZipperPackage pkg, GeoNode node, string preferred)
            {
                if (pkg.Scene(preferred)?.FindAll(node.Name).Contains(node) == true) return preferred;
                foreach (var e in pkg.Entries.Where(e => e.Kind == "geo" && e.Namespace != null))
                    if (pkg.Scene(e.Namespace!)?.FindAll(node.Name).Contains(node) == true) return e.Namespace!;
                return preferred;
            }
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>Every texture name of the package (all TXR archives): names are global in game.</summary>
        internal static HashSet<string> UsedTextureNames(ZipperPackage pkg)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in pkg.Entries.Where(e => e.Kind == "txr"))
                foreach (var n in SafeNodes(pkg, e.Leaf).Where(n => n.Name.EndsWith(".tif", StringComparison.OrdinalIgnoreCase)))
                    names.Add(n.Name);
            return names;
        }

        /// <summary>Every palette id of the package (all PAL archives): ids are global in game.</summary>
        internal static HashSet<int> UsedPaletteIds(ZipperPackage pkg)
        {
            var ids = new HashSet<int>();
            foreach (var e in pkg.Entries.Where(e => e.Kind == "pal"))
                foreach (var n in SafeNodes(pkg, e.Leaf))
                    if (n.Name.StartsWith("texpal_", StringComparison.Ordinal) && int.TryParse(n.Name[7..], out int id)) ids.Add(id);
            return ids;
        }

        /// <summary>The next texture index of a TXR (header +0x08 = TEX0.TBP0, 1..N per archive).</summary>
        internal static int NextTextureIndex(ZedContainer txr) =>
            1 + TextureNodes(txr).Select(i => (int)BitConverter.ToUInt32(txr.Nodes[i].Data, 8)).DefaultIfEmpty(0).Max();

        /// <summary>
        /// Where new palette ids start for a package: a block of its own, picked the way the pack converters pick
        /// theirs (1024 + 32 * hash % 480), above the disc (&lt;= 594) and the character packs (600+), apart from
        /// other packs. Ids are 14 bits (TEX0.CBP).
        /// </summary>
        internal static int PaletteBlock(ZipperPackage pkg)
        {
            uint hash = 2166136261;
            foreach (char ch in Path.GetFileNameWithoutExtension(pkg.SourcePath).ToLowerInvariant()) hash = (hash ^ ch) * 16777619;
            return 1024 + 32 * (int)(hash % 480);
        }

        /// <summary>The first id from <paramref name="start"/> that <paramref name="used"/> lacks (wrapping to 1024).</summary>
        internal static int FreePaletteId(HashSet<int> used, int start)
        {
            int id = start;
            for (int tries = 0; used.Contains(id); tries++)
            {
                if (tries > 0x4000) throw new InvalidOperationException("No free palette id (TEX0.CBP is 14 bits)");
                id = id >= 0x3FFF ? 1024 : id + 1;
            }
            return id;
        }

        internal static void AppendToList(ZedContainer c, string list, ZedTree.Record record, string leaf)
        {
            int at = TopLevel(c, list);
            if (at < 0) throw new InvalidOperationException($"{leaf} has no {list} list");
            ZedTree.AppendChild(c, at, record);
        }

        /// <summary>Rewrites every NUL-terminated occurrence of a texture name in a model payload (chain kind-6 fields,
        /// character section names). The new name has the old one's length, so it fits its field.</summary>
        private static byte[] Rename(byte[] payload, string from, string to, out int fields)
        {
            fields = 0;
            byte[] a = Encoding.ASCII.GetBytes(from + "\0");
            byte[] b = Encoding.ASCII.GetBytes(to + "\0");
            if (b.Length > a.Length) throw new InvalidOperationException($"{to} is longer than {from}");
            byte[] data = (byte[])payload.Clone();
            for (int i = data.AsSpan().IndexOf(a); i >= 0;)
            {
                // Only whole fields: the name starts the field or follows a NUL.
                if (i == 0 || data[i - 1] == 0)
                {
                    Array.Clear(data, i, a.Length);
                    b.CopyTo(data, i);
                    fields++;
                }
                int next = data.AsSpan(i + 1).IndexOf(a);
                i = next < 0 ? -1 : i + 1 + next;
            }
            return data;
        }

        private static IEnumerable<string> References(ZedTree.Record r)
        {
            if (r.Name == "model_name") yield return GeoNames.Clean(GeoNames.Ascii(r.Data));
            foreach (var c in r.Children)
                foreach (string s in References(c)) yield return s;
        }

        private static void AppendTop(ZedContainer c, ZedTree.Record record)
        {
            int cursor = c.Nodes.Count;
            void Insert(ZedTree.Record r)
            {
                c.InsertNode(cursor++, r.Name, r.Data, (uint)r.Children.Count);
                foreach (var child in r.Children) Insert(child);
            }
            Insert(record);
            c.RootValue++;
        }

        /// <summary>Index of the top-level record <paramref name="name"/>, or -1.</summary>
        internal static int TopLevel(ZedContainer c, string name)
        {
            for (int i = 0; i < c.Nodes.Count; i = ZedTree.SubtreeEnd(c.Nodes, i))
                if (c.Nodes[i].Name.Equals(name, StringComparison.Ordinal)) return i;
            return -1;
        }

        /// <summary>A record by name anywhere in the container (texture and palette records sit under one list).</summary>
        internal static int TopRecordIndex(ZedContainer c, string name)
        {
            for (int i = 0; i < c.Nodes.Count; i++)
                if (c.Nodes[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        private static IEnumerable<int> TopTextureRecords(ZedContainer txr)
        {
            for (int i = 0; i < txr.Nodes.Count; i++)
                if (txr.Nodes[i].Name.EndsWith(".tif", StringComparison.OrdinalIgnoreCase)) yield return i;
        }

        internal static IEnumerable<int> TextureNodes(ZedContainer txr)
        {
            for (int i = 0; i < txr.Nodes.Count; i++)
                if (txr.Nodes[i].Name == "texdat" && txr.Nodes[i].Data.Length >= ZedTextureFile.HeaderSize) yield return i;
        }

        internal static int? TexdatIndexOf(ZedContainer txr, int record)
        {
            foreach (int c in ZedTree.Children(txr.Nodes, record))
                if (txr.Nodes[c].Name == "texdat" && txr.Nodes[c].Data.Length >= ZedTextureFile.HeaderSize) return c;
            return null;
        }

        internal static IEnumerable<(uint Reg, uint Lo, uint Hi)> Registers(byte[] texdat)
        {
            var header = ZedTextureFile.ParseHeader(texdat);
            for (int o = ZedTextureFile.HeaderSize + header.PixelBytes; o + 16 <= texdat.Length; o += 16)
            {
                uint reg = BitConverter.ToUInt32(texdat, o + 8);
                if (BitConverter.ToUInt32(texdat, o + 12) != 0 || reg > 0x7F) continue;
                yield return (reg, BitConverter.ToUInt32(texdat, o), BitConverter.ToUInt32(texdat, o + 4));
            }
        }

        private static IReadOnlyList<ZedNode> SafeNodes(ZipperPackage pkg, string leaf)
        {
            try { return pkg.Container(leaf).Nodes; } catch (InvalidDataException) { return Array.Empty<ZedNode>(); }
        }
    }
}
