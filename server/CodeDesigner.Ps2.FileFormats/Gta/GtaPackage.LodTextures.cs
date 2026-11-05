namespace CodeDesigner.Ps2.FileFormats.Gta;

public sealed partial class GtaPackage
{
    private Dictionary<int, HashSet<Model>>? _lodChildren;
    private readonly Dictionary<Model, HashSet<string>> _authoredMaterials = new();
    private readonly Dictionary<(Model Model, string Texture), string?> _lodTextureNames = new();

    /// <summary>When the low-detail model's own dictionary chain lacks a raster, look for the exact
    /// material on its high-detail models, as linked by the IPL instances. Different rasters on those
    /// models leave the choice unresolved. Read original high-detail DFFs so a preview's fallback
    /// never changes in response to editing another model.</summary>
    private string? LodTextureName(Model model, string texture)
    {
        if (!IsDisc || model.Kind != "prop" || model.Id < 0) return null;
        _lodChildren ??= IndexLodChildren();
        if (!_lodChildren.TryGetValue(model.Id, out var children)) return null;
        var key = (model, texture.ToLowerInvariant());
        if (_lodTextureNames.TryGetValue(key, out var cached)) return cached;
        return _lodTextureNames[key] = Resolve();

        string? Declared(Model m)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (string? name = m.Txd; !string.IsNullOrEmpty(name) && seen.Add(name); name = _txdParent.GetValueOrDefault(name))
                if (Txd(name, m.Archive)?.Names.Contains(texture, StringComparer.OrdinalIgnoreCase) == true) return name;
            return null;
        }

        string? Resolve()
        {
            if (Declared(model) != null || Txd("vehicle", "MODELS/GENERIC")?.Names.Contains(texture, StringComparer.OrdinalIgnoreCase) == true) return null;
            string? selected = null;
            RwTexDictionary.Texture? first = null;
            foreach (var child in children)
            {
                var dictionary = Declared(child);
                if (dictionary == null) continue;
                if (!_authoredMaterials.TryGetValue(child, out var materials))
                {
                    try
                    {
                        var raw = _modelFiles[child].Read();
                        materials = RwClump.Parse(raw).Geometries.SelectMany(g => g.Materials).OfType<string>()
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    }
                    catch (Exception e) when (e is InvalidDataException or IOException or ArgumentException or InvalidOperationException or IndexOutOfRangeException)
                    {
                        // A damaged detail model should not prevent its independent LOD mesh from opening.
                        return null;
                    }
                    _authoredMaterials.Add(child, materials);
                }
                if (!materials.Contains(texture)) continue;
                string source = $"{child.Archive}|{dictionary}/{texture}";
                if (string.Equals(selected, source, StringComparison.OrdinalIgnoreCase)) continue;
                var raster = Txd(dictionary, child.Archive)?.Get(texture);
                if (raster == null || first != null && (first.Width != raster.Width || first.Height != raster.Height ||
                    !first.Rgba.AsSpan().SequenceEqual(raster.Rgba))) return null;
                first = raster;
                selected = source;
            }
            return selected;
        }
    }

    private Dictionary<int, HashSet<Model>> IndexLodChildren()
    {
        var result = new Dictionary<int, HashSet<Model>>();
        var byId = _models.Where(m => m.Id >= 0).GroupBy(m => m.Id).Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single());
        void Link(IReadOnlyList<int> modelIds, int row, int childId)
        {
            if ((uint)row >= modelIds.Count || modelIds[row] < 0 || modelIds[row] == childId || !byId.TryGetValue(childId, out var child)) return;
            if (!result.TryGetValue(modelIds[row], out var children)) result.Add(modelIds[row], children = new());
            children.Add(child);
        }

        // The LOD field indexes the INST rows of the main text IPL. Keep all row ordinals, including
        // placements that have no model in this package. Each binary <map>_stream<n>.ipl uses the
        // same main IPL's row indices; its own 40-byte INST records carry model and LOD at +28/+36.
        var mainFiles = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (file, span) in _disc.Where(kv => kv.Key.EndsWith(".ipl", StringComparison.OrdinalIgnoreCase)))
        {
            var rows = Ide(span.Read()).Where(r => r.Section == "inst").Select(r => r.Columns).ToList();
            var modelIds = rows.Select(c => c.Length >= 11 && int.TryParse(c[0], out int id)
                && byId.TryGetValue(id, out var m) && m.Name.Equals(c[1], StringComparison.OrdinalIgnoreCase) ? id : -1).ToList();
            mainFiles[Path.GetFileNameWithoutExtension(file)] = modelIds;
            for (int i = 0; i < rows.Count; i++)
                if (rows[i].Length >= 11 && int.TryParse(rows[i][10], out int row)) Link(modelIds, row, modelIds[i]);
        }
        foreach (var img in _imgs)
            foreach (var e in img.Entries.Where(e => e.Name.EndsWith(".ipl", StringComparison.OrdinalIgnoreCase)))
            {
                var stem = Path.GetFileNameWithoutExtension(e.Name);
                int suffix = stem.LastIndexOf("_stream", StringComparison.OrdinalIgnoreCase);
                if (suffix < 0 || !int.TryParse(stem.AsSpan(suffix + 7), out _) || !mainFiles.TryGetValue(stem[..suffix], out var modelIds)) continue;
                var data = img.Read(e);
                if (data.Length < 76 || !data.AsSpan(0, 4).SequenceEqual("bnry"u8)) continue;
                int count = BitConverter.ToInt32(data, 4), start = BitConverter.ToInt32(data, 28);
                if (count < 0 || start < 76 || start + 40L * count > data.Length) continue;
                for (int i = 0; i < count; i++)
                {
                    int at = start + 40 * i;
                    Link(modelIds, BitConverter.ToInt32(data, at + 36), BitConverter.ToInt32(data, at + 28));
                }
            }
        return result;
    }
}
