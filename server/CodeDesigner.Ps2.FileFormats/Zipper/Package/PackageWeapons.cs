using CodeDesigner.Ps2.FileFormats.Zipper.ZED;

namespace CodeDesigner.Ps2.FileFormats.Zipper.Package
{
    /// <summary>
    /// New weapons from a template weapon of the package (a .ZDB, or a loose folder such as RUN/COMMON with
    /// WEAP_MDL / WEAP_TXR / WEAP_PAL / WEAP_GEO). The copy is the template's whole MDL record (its N* visual stubs and
    /// chain records too, which carry the texture bindings and vertex colours) with a re-encoded payload. A new skin is
    /// a texture in the TXR / PAL pair beside the MDL built on the replaced texture (its size, wrap and filtering), with a palette of its own, and
    /// the copy's texture name fields point at it.
    /// </summary>
    public static class PackageWeapons
    {
        public sealed class Result
        {
            public Dictionary<string, byte[]> Changed { get; } = new(StringComparer.OrdinalIgnoreCase);
            public string Archive { get; set; } = string.Empty;
            public string Model { get; set; } = string.Empty;
            /// <summary>The template texture the new one replaces, or null without an image.</summary>
            public string? ReplacedTexture { get; set; }
            public string? Texture { get; set; }
            public string? TextureArchive { get; set; }
            public string? PaletteArchive { get; set; }
            public int? PaletteId { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
            public List<string> Warnings { get; } = new();
        }

        /// <summary>
        /// Adds <paramref name="newName"/> to <paramref name="mdlLeaf"/> as a copy of <paramref name="template"/>
        /// whose payload is <paramref name="payload"/> (the template's re-encoded, same layout). With
        /// <paramref name="rgba"/>, a new texture <paramref name="textureName"/> (default <c>&lt;newName&gt;.tif</c>)
        /// replaces <paramref name="replaceTexture"/> (default the template texture covering the most triangles).
        /// </summary>
        public static Result Create(ZipperPackage pkg, string mdlLeaf, string template, string newName, byte[] payload,
            byte[]? rgba = null, int srcWidth = 0, int srcHeight = 0, string? textureName = null, string? replaceTexture = null)
        {
            var mdlEntry = pkg.Entry(mdlLeaf) is { Kind: "mdl" } e ? e : throw new ArgumentException($"{mdlLeaf} is not a model archive of this package");
            var model = pkg.FindModel(mdlEntry.Leaf, template) ?? throw new KeyNotFoundException($"No model {template} in {mdlEntry.Leaf}");
            newName = newName.Trim();
            if (newName.Length == 0) throw new ArgumentException("The new weapon needs a name");

            var mdl = ZedContainer.FromBytes(mdlEntry.Data);
            if (PackageImport.TopLevel(mdl, newName) >= 0 || pkg.ResolveModel(newName) is { } clash && clash.Name.Equals(newName, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"This package already has a model named {newName}; pick another name");

            int at = PackageImport.TopLevel(mdl, template);
            if (at < 0) throw new KeyNotFoundException($"{template} is not a top-level record of {mdlEntry.Leaf}");
            ZedTree.Record copy = ZedTree.Copy(mdl.Nodes, at);
            if (payload.Length != copy.Data.Length)
                throw new ArgumentException($"The new payload is {payload.Length} bytes, the template's {copy.Data.Length}: its visual chains point into the payload by offset");
            copy = copy with
            {
                Name = newName,
                Data = payload,
                Children = copy.Children.Select(c => c.Name.Equals(template + "_START", StringComparison.Ordinal) ? c with { Name = newName + "_START" } : c).ToList(),
            };

            var result = new Result { Archive = mdlEntry.Leaf, Model = newName };
            if (rgba != null)
            {
                string ns = pkg.TextureNamespace(mdlEntry.Namespace!) ?? throw new InvalidOperationException($"{mdlEntry.Leaf} has no TXR / PAL archives beside it");
                var decoded = pkg.DecodeModel(model);
                string old = replaceTexture?.Trim() ?? decoded.Chunks.Where(c => !string.IsNullOrEmpty(c.TextureName))
                    .GroupBy(c => c.TextureName!, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(g => g.Sum(c => c.TriangleCount)).Select(g => g.Key).FirstOrDefault()
                    ?? throw new InvalidOperationException($"{template} names no texture");
                var found = pkg.DecodeTexture(old, ns) ?? throw new KeyNotFoundException($"Texture {old} is not in the package's TXR archives");

                string name = string.IsNullOrWhiteSpace(textureName) ? newName + ".tif" : textureName.Trim();
                if (!name.EndsWith(".tif", StringComparison.OrdinalIgnoreCase)) name += ".tif";
                // The copy's name fields are the old name's size.
                if (name.Length > old.Length)
                    throw new ArgumentException($"{name} is longer than {old} ({old.Length} characters), whose name fields it takes; pick a texture name of at most {old.Length} characters");

                // Built on the replaced texture, so the copy wraps and filters as the template did.
                var header = found.Texture.Header;
                var tex = PackageTextures.AddTexture(pkg, name, found.Namespace, rgba, srcWidth, srcHeight, header.Width, header.Height,
                    templateTexdat: pkg.FindTexture(old, found.Namespace)?.Texdat);
                foreach (var (leaf, bytes) in tex.Changed) result.Changed[leaf] = bytes;
                result.Warnings.AddRange(tex.Warnings);

                int fields = 0;
                copy = RenameTexture(copy, old, name, ref fields);
                if (fields == 0) result.Warnings.Add($"{template} has no name field for {old}; the copy still draws with it");
                result.ReplacedTexture = old;
                result.Texture = name;
                result.TextureArchive = tex.Archive;
                result.PaletteArchive = pkg.Archive(found.Namespace, "pal")?.Leaf;
                result.PaletteId = tex.PaletteId;
                result.Width = tex.Width;
                result.Height = tex.Height;
            }

            PackageImport.AppendTop(mdl, copy);
            result.Changed[mdlEntry.Leaf] = mdl.ToBytes();
            if (pkg.FindDefinition(model) == null) result.Warnings.Add($"{template} has no GEO definition; {newName} has none either");
            else result.Warnings.Add($"{newName} has no GEO definition yet (firepoint, aimpoint, LODs): the game places weapons by theirs");
            return result;
        }

        private static ZedTree.Record RenameTexture(ZedTree.Record r, string from, string to, ref int fields)
        {
            byte[] data = PackageImport.Rename(r.Data, from, to, out int n);
            fields += n;
            var children = new List<ZedTree.Record>(r.Children.Count);
            foreach (var c in r.Children) children.Add(RenameTexture(c, from, to, ref fields));
            return r with { Data = data, Children = children };
        }
    }
}
