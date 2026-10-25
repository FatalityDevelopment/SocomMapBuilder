using System.Text;
using CodeDesigner.Ps2.FileFormats.Zipper.Geo;
using CodeDesigner.Ps2.FileFormats.Zipper.Model;
using CodeDesigner.Ps2.FileFormats.Zipper.Texture;
using CodeDesigner.Ps2.FileFormats.Zipper.ZED;

namespace CodeDesigner.Ps2.FileFormats.Zipper.Package
{
    /// <summary>
    /// Edits on a <see cref="ZipperPackage"/>. Each works on a fresh copy of the root archives it touches and
    /// returns their new bytes by leaf; the caller (an asset session) applies them, which keeps undo simple and a
    /// failed edit from leaving a half-changed archive behind.
    /// </summary>
    public static class PackageEdits
    {
        public const string ClutterLeaf = "CLUTTER.ZAR";

        // ------------------------------------------------------------------ clutter

        public static Dictionary<string, byte[]> MoveClutter(ZipperPackage pkg, int clutterIndex, float[] matrix)
        {
            var (container, placements) = Clutter(pkg);
            ClutterFile.WritePlacement(container, At(placements, clutterIndex), CheckMatrix(matrix));
            return One(ClutterLeafOf(pkg), container);
        }

        public static Dictionary<string, byte[]> AddClutter(ZipperPackage pkg, string modelName, float[] matrix) =>
            AddClutter(pkg, modelName, matrix, out _);

        /// <summary>Adds a placement; <paramref name="newIndex"/> is its index in <see cref="ClutterFile.Parse(ZedContainer)"/> order.</summary>
        public static Dictionary<string, byte[]> AddClutter(ZipperPackage pkg, string modelName, float[] matrix, out int newIndex)
        {
            if (pkg.ResolveModel(modelName) == null) throw new ArgumentException($"No model '{modelName}' in the package");
            var (container, _) = Clutter(pkg);
            var added = ClutterFile.Add(container, modelName, CheckMatrix(matrix));
            newIndex = ClutterFile.Parse(container).FindIndex(p => p.ParamsIndex == added.ParamsIndex);
            return One(ClutterLeafOf(pkg), container);
        }

        public static Dictionary<string, byte[]> DeleteClutter(ZipperPackage pkg, int clutterIndex)
        {
            var (container, placements) = Clutter(pkg);
            ClutterFile.Delete(container, At(placements, clutterIndex));
            return One(ClutterLeafOf(pkg), container);
        }

        private static (ZedContainer, List<ClutterFile.Placement>) Clutter(ZipperPackage pkg)
        {
            var c = ZedContainer.FromBytes(pkg.Entry(ClutterLeafOf(pkg))!.Data);
            return (c, ClutterFile.Parse(c));
        }

        private static string ClutterLeafOf(ZipperPackage pkg) =>
            pkg.Entry(ClutterLeaf)?.Leaf ?? throw new InvalidOperationException("This package has no CLUTTER.ZAR");

        private static ClutterFile.Placement At(List<ClutterFile.Placement> list, int index) =>
            index >= 0 && index < list.Count ? list[index] : throw new ArgumentOutOfRangeException(nameof(index), $"Clutter index {index} is out of range (0..{list.Count - 1})");

        // ------------------------------------------------------------------ GEO scene instances

        /// <summary>Sets a scene node's local transform (the first 16 floats of its nparams).</summary>
        public static Dictionary<string, byte[]> SetInstanceTransform(ZipperPackage pkg, string sceneNs, int geoIndex, float[] localMatrix)
        {
            var (leaf, container, node) = SceneNode(pkg, sceneNs, geoIndex);
            GeoNode nparams = node.Child("nparams") ?? throw new InvalidOperationException($"{node.Name} has no nparams");
            byte[] data = (byte[])container.Nodes[nparams.Index].Data.Clone();
            float[] m = CheckMatrix(localMatrix);
            for (int k = 0; k < 16; k++) BitConverter.GetBytes(m[k]).CopyTo(data, k * 4);
            container.ReplaceNodeData(nparams.Index, data);
            return One(leaf, container);
        }

        /// <summary>
        /// Copies a scene node (a placed reference or prop) as a new sibling with <paramref name="localMatrix"/>
        /// (null keeps the original transform). <paramref name="newName"/> renames the copy.
        /// </summary>
        public static Dictionary<string, byte[]> DuplicateInstance(ZipperPackage pkg, string sceneNs, int geoIndex, float[]? localMatrix, string? newName) =>
            DuplicateInstance(pkg, sceneNs, geoIndex, localMatrix, newName, out _);

        /// <summary>As above; <paramref name="newIndex"/> is the copy's record index in the GEO container.</summary>
        public static Dictionary<string, byte[]> DuplicateInstance(ZipperPackage pkg, string sceneNs, int geoIndex, float[]? localMatrix, string? newName, out int newIndex)
        {
            var (leaf, container, node) = SceneNode(pkg, sceneNs, geoIndex);
            int parent = ZedTree.ParentIndices(container.Nodes)[geoIndex];
            if (parent < 0) throw new InvalidOperationException("Top-level scene records cannot be duplicated");
            ZedTree.Record copy = ZedTree.Copy(container.Nodes, geoIndex);
            if (!string.IsNullOrWhiteSpace(newName)) copy = copy with { Name = newName };
            int at = ZedTree.AppendChild(container, parent, copy);
            newIndex = at;
            if (localMatrix != null)
            {
                int nparams = ZedTree.Children(container.Nodes, at).FirstOrDefault(i => container.Nodes[i].Name == "nparams", -1);
                if (nparams < 0) throw new InvalidOperationException($"{node.Name} has no nparams");
                byte[] data = (byte[])container.Nodes[nparams].Data.Clone();
                float[] m = CheckMatrix(localMatrix);
                for (int k = 0; k < 16; k++) BitConverter.GetBytes(m[k]).CopyTo(data, k * 4);
                container.ReplaceNodeData(nparams, data);
            }
            return One(leaf, container);
        }

        public static Dictionary<string, byte[]> DeleteInstance(ZipperPackage pkg, string sceneNs, int geoIndex)
        {
            var (leaf, container, node) = SceneNode(pkg, sceneNs, geoIndex);
            if (node.Name == "worldmodel") throw new InvalidOperationException("worldmodel cannot be deleted");
            ZedTree.Remove(container, geoIndex);
            return One(leaf, container);
        }

        private static (string Leaf, ZedContainer Container, GeoNode Node) SceneNode(ZipperPackage pkg, string sceneNs, int geoIndex)
        {
            var entry = pkg.Archive(sceneNs, "geo") ?? throw new KeyNotFoundException($"No {sceneNs}_GEO.ZED");
            var container = ZedContainer.FromBytes(entry.Data);
            GeoScene scene = GeoScene.Parse(container.Nodes);
            GeoNode? node = scene.FindAll(geoIndex >= 0 && geoIndex < container.Nodes.Count ? container.Nodes[geoIndex].Name : string.Empty)
                .FirstOrDefault(n => n.Index == geoIndex);
            if (node == null || !node.IsSceneNode) throw new ArgumentException($"Record {geoIndex} of {entry.Leaf} is not a scene node");
            return (entry.Leaf, container, node);
        }

        // ------------------------------------------------------------------ textures

        /// <summary>
        /// Replaces a texture's pixels from an RGBA image (resampled to the texture's size). An indexed texture
        /// whose palette no other texture uses gets a new palette; a shared palette is kept and the image is
        /// matched to it. The texture keeps its record, GS registers and palette id.
        /// </summary>
        public static Dictionary<string, byte[]> ReplaceTexture(ZipperPackage pkg, string tifName, string? preferredNs, byte[] rgba, int width, int height)
        {
            var found = pkg.FindTexture(tifName, preferredNs) ?? throw new KeyNotFoundException($"Texture '{tifName}' not found");
            string ns = found.Namespace;
            var txrEntry = pkg.Archive(ns, "txr")!;
            var txr = ZedContainer.FromBytes(txrEntry.Data);
            int texdat = TexdatIndex(txr, tifName);
            var header = ZedTextureFile.ParseHeader(txr.Nodes[texdat].Data);
            var changed = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

            if (!header.IsIndexed)
            {
                txr.ReplaceNodeData(texdat, ZedTextureFile.ResampleToTemplate(rgba, width, height, header, null, out _));
                changed[txrEntry.Leaf] = txr.ToBytes();
                return changed;
            }

            int paletteId = header.ResolvePaletteId();
            var palEntry = pkg.Archive(ns, "pal") ?? throw new InvalidOperationException($"No {ns}_PAL.ZED for palette {paletteId}");
            var pal = ZedContainer.FromBytes(palEntry.Data);
            int buf = PaletteBufIndex(pal, paletteId);
            bool is16 = pal.Nodes[buf].Data.Length < 1024;
            bool shared = SharesPalette(txr, paletteId, texdat);
            if (shared)
            {
                var existing = ZedTextureFile.DecodeClutBuffer(pal.Nodes[buf].Data, paletteId);
                txr.ReplaceNodeData(texdat, ZedTextureFile.ResampleToTemplate(rgba, width, height, header, existing.Rgba, out _));
            }
            else
            {
                byte[] encoded = ZedTextureFile.ResampleToTemplate(rgba, width, height, header, null, out byte[]? generated);
                txr.ReplaceNodeData(texdat, encoded);
                if (generated != null)
                {
                    pal.ReplaceNodeData(buf, ZedTextureFile.EncodeClutBuffer(generated, is16));
                    changed[palEntry.Leaf] = pal.ToBytes();
                }
            }
            changed[txrEntry.Leaf] = txr.ToBytes();
            return changed;
        }

        private static int TexdatIndex(ZedContainer txr, string tifName)
        {
            var nodes = txr.Nodes;
            for (int i = 0; i < nodes.Count; i++)
            {
                if (!nodes[i].Name.Equals(tifName, StringComparison.OrdinalIgnoreCase)) continue;
                for (int j = i + 1; j < Math.Min(i + 4, nodes.Count); j++)
                    if (nodes[j].Name == "texdat") return j;
            }
            throw new KeyNotFoundException($"No texdat for {tifName}");
        }

        private static int PaletteBufIndex(ZedContainer pal, int id)
        {
            var nodes = pal.Nodes;
            for (int i = 0; i < nodes.Count; i++)
            {
                if (nodes[i].Name != "texpal_" + id) continue;
                for (int j = i + 1; j < Math.Min(i + 4, nodes.Count); j++)
                    if (nodes[j].Name == "buf") return j;
            }
            throw new KeyNotFoundException($"No texpal_{id} buf");
        }

        private static bool SharesPalette(ZedContainer txr, int paletteId, int exceptTexdat)
        {
            for (int i = 0; i < txr.Nodes.Count; i++)
            {
                if (i == exceptTexdat || txr.Nodes[i].Name != "texdat" || txr.Nodes[i].Data.Length < ZedTextureFile.HeaderSize) continue;
                try
                {
                    var h = ZedTextureFile.ParseHeader(txr.Nodes[i].Data);
                    if (h.IsIndexed && h.ResolvePaletteId() == paletteId) return true;
                }
                catch (InvalidDataException) { }
            }
            return false;
        }

        // ------------------------------------------------------------------ models

        /// <summary>
        /// Points a model's material sections at another texture: rewrites the chain name fields (kind 6) that
        /// name <paramref name="from"/>, in every visual or only <paramref name="visualKey"/>. The new name must fit
        /// the field (16 or 32 bytes with its NUL).
        /// </summary>
        public static Dictionary<string, byte[]> RetextureModel(ZipperPackage pkg, string archive, string modelName, string from, string to, string? visualKey)
        {
            if (pkg.FindTexture(to, pkg.TextureNamespace(pkg.Entry(archive)?.Namespace ?? string.Empty)) == null)
                throw new ArgumentException($"Texture '{to}' is not in the package");
            var (container, index, payload) = ModelPayload(pkg, archive, modelName);
            int count = 0;
            foreach (var tag in ChainTags(payload, container, index, visualKey).Where(t => t.Kind == 6))
            {
                int field = FieldLength(payload, tag.Address);
                string name = ReadName(payload, tag.Address);
                if (!name.Equals(from, StringComparison.OrdinalIgnoreCase)) continue;
                byte[] bytes = Encoding.ASCII.GetBytes(to);
                if (bytes.Length >= field) throw new ArgumentException($"'{to}' does not fit the {field}-byte name field of '{from}'");
                Array.Clear(payload, tag.Address, field);
                bytes.CopyTo(payload, tag.Address);
                count++;
            }
            if (count == 0) throw new ArgumentException($"{modelName} has no section textured '{from}'{(visualKey != null ? " in " + visualKey : "")}");
            container.ReplaceNodeData(index, payload);
            return One(archive, container);
        }

        /// <summary>
        /// Scales (and offsets) the baked vertex colours of a model, all of it or one visual / texture. Colours are
        /// 0..255 with 0x80 = 1.0; alpha is left alone unless <paramref name="alpha"/> is given.
        /// </summary>
        public static Dictionary<string, byte[]> TintVertexColors(ZipperPackage pkg, string archive, string modelName, float[] multiply, float[]? add, string? visualKey, string? texture, float? alpha)
        {
            if (multiply.Length < 3) throw new ArgumentException("multiply needs r, g, b");
            var (container, index, payload) = ModelPayload(pkg, archive, modelName);
            var done = new HashSet<int>();
            string? section = null;
            foreach (var tag in ChainTags(payload, container, index, visualKey))
            {
                if (tag.Kind == 6) { section = ReadName(payload, tag.Address); continue; }
                if (tag.Kind != 4 || !done.Add(tag.Address)) continue;
                if (texture != null && !string.Equals(section, texture, StringComparison.OrdinalIgnoreCase)) continue;
                for (int o = tag.Address; o + 3 < tag.Address + tag.Length && o + 3 < payload.Length; o += 4)
                {
                    for (int ch = 0; ch < 3; ch++)
                        payload[o + ch] = ClampByte(payload[o + ch] * multiply[ch] + (add != null && add.Length > ch ? add[ch] : 0));
                    if (alpha is float a) payload[o + 3] = ClampByte(payload[o + 3] * a);
                }
            }
            if (done.Count == 0) throw new ArgumentException($"{modelName} has no vertex colour blocks to tint");
            container.ReplaceNodeData(index, payload);
            return One(archive, container);
        }

        private static byte ClampByte(float v) => (byte)Math.Clamp((int)MathF.Round(v), 0, 255);

        private static (ZedContainer Container, int Index, byte[] Payload) ModelPayload(ZipperPackage pkg, string archive, string modelName)
        {
            var model = pkg.FindModel(archive, modelName) ?? throw new KeyNotFoundException($"Model {archive}:{modelName} not found");
            var container = ZedContainer.FromBytes(pkg.Entry(archive)!.Data);
            return (container, model.NodeIndex, (byte[])container.Nodes[model.NodeIndex].Data.Clone());
        }

        private readonly record struct ChainTag(int Kind, int Address, int Length);

        private static IEnumerable<ChainTag> ChainTags(byte[] payload, ZedContainer container, int index, string? visualKey)
        {
            foreach (var chain in ZedModelFile.CollectChainOffsets(container.Nodes, index))
            {
                if (visualKey != null && !string.Equals(chain.Key, visualKey, StringComparison.OrdinalIgnoreCase)) continue;
                int o = chain.Offset;
                if (o < 0 || o + 16 > payload.Length) continue;
                int count = (int)(BitConverter.ToUInt32(payload, o) & 0xFFFF);
                for (int i = 0; i < count && o + 32 + i * 16 <= payload.Length; i++)
                {
                    uint lo = BitConverter.ToUInt32(payload, o + 16 + i * 16);
                    int addr = (int)BitConverter.ToUInt32(payload, o + 20 + i * 16);
                    if (addr < 0 || addr >= payload.Length) continue;
                    yield return new ChainTag((int)((lo >> 16) & 0xFF), addr, (int)(lo & 0xFFFF) * 16);
                }
            }
        }

        private static string ReadName(byte[] payload, int at)
        {
            int end = at;
            while (end < payload.Length && payload[end] != 0 && end - at < 64) end++;
            return Encoding.ASCII.GetString(payload, at, end - at);
        }

        /// <summary>A name field runs to the 16-byte boundary after its NUL (16 or 32 bytes in retail data).</summary>
        private static int FieldLength(byte[] payload, int at)
        {
            int nul = at;
            while (nul < payload.Length && payload[nul] != 0) nul++;
            int end = at + ((nul - at) / 16 + 1) * 16;
            return Math.Min(end, payload.Length) - at;
        }

        // ------------------------------------------------------------------ helpers

        private static float[] CheckMatrix(float[] m)
        {
            if (m.Length != 16 || !m.All(float.IsFinite)) throw new ArgumentException("A transform is 16 finite floats (column-major, translation in 12-14)");
            return m;
        }

        private static Dictionary<string, byte[]> One(string leaf, ZedContainer container) =>
            new(StringComparer.OrdinalIgnoreCase) { [leaf] = container.ToBytes() };
    }
}
