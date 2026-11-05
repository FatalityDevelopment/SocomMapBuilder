using System.Globalization;
using System.Text;

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
    // The disc's own record of every placement moved in this session, by (file, row).
    private readonly Dictionary<(string File, int Row), Placement> _placementOriginals = new();
    private Dictionary<(string File, int Row), int>? _placementIndex;

    private static (string, int) Key(string file, int row) => (file.ToUpperInvariant(), row);

    /// <summary>A placement by its IPL file (as <see cref="Placement.File"/>) and row; null when there is none.</summary>
    public Placement? FindPlacement(string file, int row)
    {
        var all = Placements;
        _placementIndex ??= all.Select((p, i) => (p, i)).ToDictionary(x => Key(x.p.File, x.p.Row), x => x.i);
        return _placementIndex.TryGetValue(Key(file, row), out int at) ? all[at] : null;
    }

    /// <summary>Places a placement by a new Y-up matrix (rotation and translation only: IPLs hold no scale).</summary>
    public Placement MovePlacement(string file, int row, float[] matrix)
    {
        if (matrix.Length != 16 || matrix.Take(15).Any(v => !float.IsFinite(v))) throw new ArgumentException("The matrix must be 16 finite numbers");
        for (int c = 0; c < 3; c++)
        {
            float len = MathF.Sqrt(matrix[c * 4] * matrix[c * 4] + matrix[c * 4 + 1] * matrix[c * 4 + 1] + matrix[c * 4 + 2] * matrix[c * 4 + 2]);
            if (MathF.Abs(len - 1) > 0.01f) throw new NotSupportedException("GTA placements cannot be scaled (IPLs store a position and a rotation); move or turn them instead.");
        }
        var p = FindPlacement(file, row) ?? throw new KeyNotFoundException($"No placement {file} row {row}");
        var key = Key(file, row);
        _placementOriginals.TryAdd(key, p);
        var original = _placementOriginals[key];
        Placement moved;
        // Back to the disc's own matrix (an undo): the disc's record, exactly.
        if (original.Matrix.AsSpan().SequenceEqual(matrix)) { moved = original; _placementOriginals.Remove(key); }
        else
        {
            var g = GameTransform(matrix);
            // Keep the stored values (re-derived through Transform) so the scene shows exactly what a save writes.
            moved = p with { X = g.X, Y = g.Y, Z = g.Z, Matrix = Transform(g.X, g.Y, g.Z, g.Qx, g.Qy, g.Qz, g.Qw) };
        }
        _placements![_placementIndex![key]] = moved;
        _lodRows = null;
        return moved;
    }

    /// <summary>The IPL files (text and stream, as <see cref="Placement.File"/>) with placements moved from the disc.</summary>
    public IReadOnlyList<string> EditedPlacementFiles => _placementOriginals.Values
        .Select(p => p.File).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();

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

    /// <summary>An edited IPL's bytes: a text IPL with its moved INST lines rewritten (every other byte kept), or a
    /// binary stream IPL with its moved 40-byte records patched.</summary>
    public byte[] PlacementFileBytes(string file)
    {
        var moved = Placements.Where(p => p.File.Equals(file, StringComparison.OrdinalIgnoreCase) && _placementOriginals.ContainsKey(Key(p.File, p.Row)))
            .ToDictionary(p => p.Row);
        if (_streamFiles.TryGetValue(file, out var stream))
        {
            var data = _imgByArchive[stream.Archive].Read(stream.Entry) ?? throw new KeyNotFoundException($"{stream.Entry} is not in {stream.Archive}");
            int start = BitConverter.ToInt32(data, 28);
            foreach (var (row, p) in moved)
            {
                var g = GameTransform(p.Matrix);
                int at = start + 40 * row;
                foreach (var (offset, value) in new[] { (0, g.X), (4, g.Y), (8, g.Z), (12, g.Qx), (16, g.Qy), (20, g.Qz), (24, g.Qw) })
                    BitConverter.TryWriteBytes(data.AsSpan(at + offset, 4), value);
            }
            return data;
        }
        var source = _disc.GetValueOrDefault(file) ?? throw new KeyNotFoundException($"{file} is not on the disc");
        string text = Encoding.ASCII.GetString(source.Read());
        var lines = text.Split('\n');
        string? section = null;
        int inst = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            // The same reading as Ide(): comments stripped, blank lines skipped, "end" closes a section.
            string line = lines[i].Split('#')[0].Trim();
            if (line.Length == 0) continue;
            if (section == null) { section = line.ToLowerInvariant(); continue; }
            if (line.Equals("end", StringComparison.OrdinalIgnoreCase)) { section = null; continue; }
            if (section != "inst") continue;
            int row = inst++;
            if (!moved.TryGetValue(row, out var p)) continue;
            var c = line.Split(',').Select(x => x.Trim()).ToArray();
            var g = GameTransform(p.Matrix);
            var values = new[] { c[0], c[1], c[2], Num(g.X), Num(g.Y), Num(g.Z), Num(g.Qx), Num(g.Qy), Num(g.Qz), Num(g.Qw) }.Concat(c.Skip(10));
            lines[i] = string.Join(", ", values) + (lines[i].EndsWith('\r') ? "\r" : "");
        }
        return Encoding.ASCII.GetBytes(string.Join('\n', lines));
    }

    /// <summary>
    /// Writes every edited IPL under <paramref name="folder"/> at its disc path: text IPLs (DATA/MAPS/...), and for
    /// stream IPLs a copy of their IMG (MODELS/GTA3.IMG, with this session's edited DFFs too) - and of GTA3_1.IMG when
    /// the disc holds that second copy. Answers the files written.
    /// </summary>
    public IReadOnlyList<string> SaveMapEdits(string folder)
    {
        var files = EditedPlacementFiles;
        if (files.Count == 0) throw new InvalidOperationException("No placements were moved.");
        var written = new List<string>();
        foreach (string file in files.Where(f => !_streamFiles.ContainsKey(f)))
        {
            string path = Path.Combine(folder, file.Replace('/', Path.DirectorySeparatorChar));
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
