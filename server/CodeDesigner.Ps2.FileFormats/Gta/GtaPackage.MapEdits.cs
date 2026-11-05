using System.Globalization;


namespace CodeDesigner.Ps2.FileFormats.Gta;

/// <summary>
/// Moving San Andreas' map placements. A move replaces the placement's matrix in memory; saving writes each edited
/// IPL: a text IPL (DATA/MAPS/**.IPL) with only the edited INST lines rewritten, and a binary stream IPL with its 40-byte
/// records patched in place, inside a copy of its IMG (and of GTA3_1.IMG, the disc's second copy of GTA3.IMG).
/// </summary>
public sealed partial class GtaPackage
{
    // Stream IPL disc-style path (DATA/MAPS/LA/lae2_stream5.ipl) -> its IMG and entry.
    private readonly Dictionary<string, (string Archive, string Entry)> _streamFiles = new(StringComparer.OrdinalIgnoreCase);
    // Null originals are rows added in this session. File and row are stable across inserts and deletes.
    private readonly Dictionary<(string File, int Row), Placement?> _placementOriginals = new();
    private Dictionary<(string File, int Row), int>? _placementIndex;
    private Dictionary<string, MapArea>? _originalAreas;
    private Dictionary<(string File, int Row), int>? _sourceOrder;
    // New rows sort after the original records. Their physical ordinal is assigned on save.
    private const int FirstAddedRow = 1_000_000_000;
    private int _nextAddedRow = FirstAddedRow;

    private static (string, int) Key(string file, int row) => (file.ToUpperInvariant(), row);

    public Placement? FindPlacement(string file, int row)
    {
        var all = Placements;
        _placementIndex ??= all.Select((p, i) => (p, i)).ToDictionary(x => Key(x.p.File, x.p.Row), x => x.i);
        return _placementIndex.TryGetValue(Key(file, row), out int at) ? all[at] : null;
    }

    private static void ValidatePlacementMatrix(float[] matrix)
    {
        if (matrix.Length != 16 || matrix.Any(v => !float.IsFinite(v))) throw new ArgumentException("The matrix must be 16 finite numbers");
        var g = GameTransform(matrix);
        var rigid = Transform(g.X, g.Y, g.Z, g.Qx, g.Qy, g.Qz, g.Qw);
        if (rigid.Where((v, i) => Math.Abs(v - matrix[i]) > .001f).Any())
            throw new NotSupportedException("GTA placements cannot be scaled or sheared (IPLs store a position and a rotation).");
    }

    public Placement MovePlacement(string file, int row, float[] matrix)
    {
        ValidatePlacementMatrix(matrix);
        var p = FindPlacement(file, row) ?? throw new KeyNotFoundException($"No placement {file} row {row}");
        var g = GameTransform(matrix);
        var moved = p with { X = g.X, Y = g.Y, Z = g.Z, Matrix = Transform(g.X, g.Y, g.Z, g.Qx, g.Qy, g.Qz, g.Qw) };
        if (_placementOriginals.GetValueOrDefault(Key(file, row)) is { } original && original.Matrix.AsSpan().SequenceEqual(matrix))
            moved = original;
        SetPlacement(file, row, moved);
        return moved;
    }

    public static bool CanPlace(Model m) => m.Kind == "prop" && m.Id >= 0 &&
        (m.Archive.Equals("GTA3.IMG", StringComparison.OrdinalIgnoreCase) || m.Archive.Equals("GTA_INT.IMG", StringComparison.OrdinalIgnoreCase));

    public Placement AddPlacement(string area, Model model, float[] matrix, Placement? template = null)
    {
        ValidatePlacementMatrix(matrix);
        if (!CanPlace(model)) throw new ArgumentException("Choose a world prop with a defined IDE id.");
        bool copyFlags = template != null;
        template ??= Placements.Where(p => p.AreaId.Equals(area, StringComparison.OrdinalIgnoreCase) && !p.IsLod)
            .MinBy(p => Math.Pow(p.Matrix[12] - matrix[12], 2) + Math.Pow(p.Matrix[13] - matrix[13], 2) + Math.Pow(p.Matrix[14] - matrix[14], 2));
        template ??= Placements.FirstOrDefault(p => p.AreaId.Equals(area, StringComparison.OrdinalIgnoreCase))
            ?? _placementOriginals.Values.FirstOrDefault(p => p?.AreaId.Equals(area, StringComparison.OrdinalIgnoreCase) == true);
        if (template == null || !template.AreaId.Equals(area, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Pick a map area and a placement within it.");
        // A streamed INST stores only the IDE id. Check the interior-dependent archive resolution.
        var matches = Models.Where(m => m.Id == model.Id && m.Kind is "prop" or "vehicle" or "character" or "weapon").ToList();
        string preferred = template.IsInterior ? "GTA_INT.IMG" : "GTA3.IMG";
        var resolved = matches.FirstOrDefault(m => m.Archive.Equals(preferred, StringComparison.OrdinalIgnoreCase)) ?? matches.First();
        if (resolved != model) throw new ArgumentException($"IDE id {model.Id} resolves to {resolved.Name} in this area.");
        var g = GameTransform(matrix);
        int interior = copyFlags ? template.Interior : template.Interior & 255;
        var added = new Placement(template.Zone, template.File, checked(_nextAddedRow++), model.Id, model.Name, interior,
            g.X, g.Y, g.Z, Transform(g.X, g.Y, g.Z, g.Qx, g.Qy, g.Qz, g.Qw), -1, model);
        SetPlacement(added.File, added.Row, added);
        return added;
    }

    // Replays a placement change, also used by batch rollback and undo/redo.
    public void SetPlacement(string file, int row, Placement? value)
    {
        var key = Key(file, row);
        var previous = FindPlacement(file, row);
        if (previous == null && value == null) return;
        _originalAreas ??= MapAreas.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
        // Preserve draw and paging order when undoing a delete or rolling back a rejected batch.
        if (previous == null || value == null) _sourceOrder ??= new(_placementIndex!);
        _placementOriginals.TryAdd(key, previous);
        var original = _placementOriginals[key];
        if (value == null && original == null || original != null && value != null && original.Matrix.AsSpan().SequenceEqual(value.Matrix))
        {
            value = original;
            _placementOriginals.Remove(key);
        }
        if (previous == null)
        {
            var sourceOrder = _sourceOrder!;
            int order = sourceOrder.GetValueOrDefault(key, row);
            int next = _placements!.FindIndex(p => sourceOrder.GetValueOrDefault(Key(p.File, p.Row), p.Row) > order);
            if (next < 0) _placements.Add(value!);
            else _placements.Insert(next, value!);
        }
        else if (value == null) _placements!.RemoveAt(_placementIndex![key]);
        else _placements![_placementIndex![key]] = value;
        _lodRows = null;
        _areas = null;
        if (previous == null || value == null)
        {
            _placementIndex = null;
            MarkLods(_placements!, (previous ?? value!).Zone);
        }
    }

    // A LOD index counts physical INST rows in the text IPL. Project the live session IDs at write time.
    private Dictionary<string, int[]> DeletedTextRows() => _placementOriginals.Values
        .OfType<Placement>().Where(p => !_streamFiles.ContainsKey(p.File) && FindPlacement(p.File, p.Row) == null)
        .GroupBy(p => p.Zone, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.Select(p => p.Row).Order().ToArray(), StringComparer.OrdinalIgnoreCase);

    private static int SavedLod(Placement p, Dictionary<string, int[]> deleted)
    {
        if (p.LodRow < 0 || !deleted.TryGetValue(p.Zone, out var rows)) return p.LodRow;
        int index = Array.BinarySearch(rows, p.LodRow);
        return index >= 0 ? -1 : p.LodRow - ~index;
    }

    public IReadOnlyList<string> EditedPlacementFiles
    {
        get
        {
            var deleted = DeletedTextRows();
            var files = _placementOriginals.Select(kv => (kv.Value ?? FindPlacement(kv.Key.File, kv.Key.Row)!).File);
            if (deleted.Count > 0) files = files.Concat(Placements.Where(p => SavedLod(p, deleted) != p.LodRow).Select(p => p.File));
            return files.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    /// <summary>Where a placement file lives: "GTA3.IMG/lae2_stream5.ipl" for a stream IPL, else its disc path.</summary>
    public string PlacementFileLabel(string file) => _streamFiles.TryGetValue(file, out var s) ? $"{s.Archive}/{s.Entry}" : file;

    /// <summary>
    /// The game-space values an IPL stores for a Y-up matrix: position and the conjugated rotation quaternion
    /// (the inverse of <see cref="Transform"/>).
    /// </summary>
    public static (float X, float Y, float Z, float Qx, float Qy, float Qz, float Qw) GameTransform(float[] matrix)
    {
        var g = Assets.Matrix4.Multiply(Assets.Matrix4.Multiply(YUpToZUp, matrix), ZUpToYUp);
        // Column-major: element (row r, column c) is g[c * 4 + r].
        float m00 = g[0], m10 = g[1], m20 = g[2], m01 = g[4], m11 = g[5], m21 = g[6], m02 = g[8], m12 = g[9], m22 = g[10];
        float qw, qx, qy, qz, trace = m00 + m11 + m22;
        if (trace > 0)
        {
            float s = MathF.Sqrt(trace + 1) * 2;
            qw = s / 4; qx = (m21 - m12) / s; qy = (m02 - m20) / s; qz = (m10 - m01) / s;
        }
        else if (m00 > m11 && m00 > m22)
        {
            float s = MathF.Sqrt(1 + m00 - m11 - m22) * 2;
            qw = (m21 - m12) / s; qx = s / 4; qy = (m01 + m10) / s; qz = (m02 + m20) / s;
        }
        else if (m11 > m22)
        {
            float s = MathF.Sqrt(1 + m11 - m00 - m22) * 2;
            qw = (m02 - m20) / s; qx = (m01 + m10) / s; qy = s / 4; qz = (m12 + m21) / s;
        }
        else
        {
            float s = MathF.Sqrt(1 + m22 - m00 - m11) * 2;
            qw = (m10 - m01) / s; qx = (m02 + m20) / s; qy = (m12 + m21) / s; qz = s / 4;
        }
        float n = MathF.Sqrt(qx * qx + qy * qy + qz * qz + qw * qw);
        // SA stores the conjugate of the placement's rotation.
        return (g[12], g[13], g[14], -qx / n, -qy / n, -qz / n, qw / n);
    }

    // Shortest text that reads back as the same float (no exponent: tiny values are written as 0).
    private static string Num(float v) => (MathF.Abs(v) < 1e-6f ? 0f : v).ToString("R", CultureInfo.InvariantCulture);

    // IPL encoders live in GtaPackage.PlacementFiles.cs.

    /// <summary>
    /// Writes every edited IPL under <paramref name="folder"/> at its disc path: text IPLs (DATA/MAPS/...), and for
    /// stream IPLs a copy of their IMG (MODELS/GTA3.IMG, with this session's edited DFFs too) - and of GTA3_1.IMG when
    /// the disc holds that second copy. Answers the files written.
    /// </summary>
    public IReadOnlyList<string> SaveMapEdits(string folder)
    {
        var files = EditedPlacementFiles;
        if (files.Count == 0) throw new InvalidOperationException("No placements were edited.");
        var written = new List<string>();
        foreach (string file in files.Where(f => !_streamFiles.ContainsKey(f)))
        {
            string path = Path.Combine(folder, file.Replace('/', Path.DirectorySeparatorChar));
            if (IsSourceFile(path)) throw new InvalidOperationException("Choose a separate folder for the edited map files.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, PlacementFileBytes(file));
            written.Add(path);
        }
        foreach (var group in files.Where(_streamFiles.ContainsKey).GroupBy(f => _streamFiles[f].Archive, StringComparer.OrdinalIgnoreCase))
        {
            string archive = group.Key;
            string discPath = _disc.Keys.FirstOrDefault(k => Path.GetFileName(k).Equals(archive, StringComparison.OrdinalIgnoreCase)) ?? "MODELS/" + archive;
            string path = Path.Combine(folder, discPath.Replace('/', Path.DirectorySeparatorChar));
            SaveImgCopy(archive, path, group.ToDictionary(f => _streamFiles[f].Entry, PlacementFileBytes, StringComparer.OrdinalIgnoreCase));
            written.Add(path);
            // The PS2 disc carries GTA3.IMG twice (GTA3_1.IMG, byte for byte); the game may stream from either.
            string twin = Path.Combine(Path.GetDirectoryName(discPath) ?? "", Path.GetFileNameWithoutExtension(archive) + "_1" + Path.GetExtension(archive)).Replace('\\', '/');
            if (_disc.TryGetValue(twin, out var copy) && _disc.TryGetValue(discPath, out var original) && copy.Length == original.Length)
            {
                string twinPath = Path.Combine(folder, twin.Replace('/', Path.DirectorySeparatorChar));
                File.Copy(path, twinPath, overwrite: true);
                written.Add(twinPath);
            }
        }
        return written;
    }
}
