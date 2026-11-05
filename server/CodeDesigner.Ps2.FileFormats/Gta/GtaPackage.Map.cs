using System.Numerics;

namespace CodeDesigner.Ps2.FileFormats.Gta;

/// <summary>
/// San Andreas' map: every IPL INST placement on the disc - the text IPLs under DATA/MAPS (id, model, interior,
/// position, rotation quaternion, LOD row) and the binary <c>&lt;zone&gt;_stream&lt;n&gt;.ipl</c> files in the IMGs
/// (40-byte records whose LOD indexes the rows of the zone's text IPL) - each resolved to its DFF by IDE id.
/// Placements are grouped into areas by their IPL zone (LA/LAe, SF/SFe, country/countryN ...); interior placements
/// form an area of their own per zone.
/// </summary>
public sealed partial class GtaPackage
{
    /// <summary>One INST placement. <see cref="Matrix"/> is the editors' Y-up column-major transform.</summary>
    public sealed record Placement(string Zone, string File, int Row, int Id, string Name, int Interior,
        float X, float Y, float Z, float[] Matrix, int LodRow, Model? Model)
    {
        /// <summary>Another placement of the zone names this one as its low-detail stand-in.</summary>
        public bool IsLod { get; internal set; }
        public string AreaId => IsInterior ? Zone + InteriorSuffix : Zone;
        /// <summary>Interior 0 is outdoors; 13 is SA's "visible everywhere" id (also outdoors).</summary>
        public bool IsInterior => (Interior & 0xFF) is not (0 or 13);
    }

    /// <summary>A map area to pick: an IPL zone's outdoor or interior placements. Bounds are Y-up (x, z) ground extents.</summary>
    public sealed record MapArea(string Id, string Label, bool Interior, int Placements, int Resolved,
        float MinX, float MinZ, float MaxX, float MaxZ)
    {
        public float CenterX => (MinX + MaxX) / 2;
        public float CenterZ => (MinZ + MaxZ) / 2;
    }

    public const string InteriorSuffix = "#interiors";

    private List<Placement>? _placements;
    private List<MapArea>? _areas;

    /// <summary>Every placement on the disc (text IPL rows first, then the zones' streamed rows), cached.</summary>
    public IReadOnlyList<Placement> Placements => _placements ??= ReadPlacements();

    public IReadOnlyList<MapArea> MapAreas => _areas ??= BuildAreas();

    private List<MapArea> BuildAreas()
    {
        var areas = Placements.GroupBy(p => p.AreaId, StringComparer.OrdinalIgnoreCase)
        .Select(g =>
        {
            var first = g.First();
            string folder = first.File.Contains('/') ? first.File[..first.File.LastIndexOf('/')] : "";
            folder = folder.StartsWith("DATA/MAPS/", StringComparison.OrdinalIgnoreCase) ? folder[10..] : folder;
            string label = (folder.Length > 0 ? folder.ToUpperInvariant() + "/" : "") + first.Zone.ToUpperInvariant()
                + (first.IsInterior ? " interiors" : "");
            // Bounds from the drawn (non-LOD) placements, so a zone's far LOD blocks do not stretch the frame.
            var shown = g.Where(p => !p.IsLod).DefaultIfEmpty(first).ToList();
            return new MapArea(g.Key, label, first.IsInterior, g.Count(), g.Count(p => p.Model != null),
                shown.Min(p => p.Matrix[12]), shown.Min(p => p.Matrix[14]), shown.Max(p => p.Matrix[12]), shown.Max(p => p.Matrix[14]));
        })
        .ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
        // Keep an emptied area selectable so the user can place a replacement or undo.
        if (_originalAreas != null)
            foreach (var area in _originalAreas.Values) areas.TryAdd(area.Id, area with { Placements = 0, Resolved = 0 });
        return areas.Values.OrderBy(a => a.Interior).ThenBy(a => a.Label, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The outdoor area holding the most placements within <paramref name="radius"/> of a game-space point.</summary>
    public MapArea? AreaAt(float gameX, float gameY, float radius = 150)
    {
        var best = Placements.Where(p => !p.IsInterior && !p.IsLod && (p.X - gameX) * (p.X - gameX) + (p.Y - gameY) * (p.Y - gameY) < radius * radius)
            .GroupBy(p => p.AreaId, StringComparer.OrdinalIgnoreCase).MaxBy(g => g.Count());
        return best == null ? null : MapAreas.First(a => a.Id.Equals(best.Key, StringComparison.OrdinalIgnoreCase));
    }

    private List<Placement> ReadPlacements()
    {
        var result = new List<Placement>();
        if (!IsDisc) return result;
        var byId = new Dictionary<int, List<Model>>();
        foreach (var m in _models.Where(m => m.Id >= 0 && m.Kind is "prop" or "vehicle" or "character" or "weapon"))
        {
            if (!byId.TryGetValue(m.Id, out var list)) byId.Add(m.Id, list = new());
            list.Add(m);
        }
        // An id defined in both world archives: interiors prefer GTA_INT.IMG, everything else GTA3.IMG.
        Model? Resolve(int id, bool interior)
        {
            if (!byId.TryGetValue(id, out var list)) return null;
            if (list.Count == 1) return list[0];
            string prefer = interior ? "GTA_INT.IMG" : "GTA3.IMG";
            return list.FirstOrDefault(m => m.Archive.Equals(prefer, StringComparison.OrdinalIgnoreCase)) ?? list[0];
        }

        // Each zone's text rows, so binary rows can name their LOD (an index into those rows) and LODs can be flagged.
        var textRows = new Dictionary<string, List<Placement>>(StringComparer.OrdinalIgnoreCase);
        var zoneFile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (file, span) in _disc.Where(kv => kv.Key.EndsWith(".ipl", StringComparison.OrdinalIgnoreCase)).OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            string zone = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
            if (textRows.ContainsKey(zone)) continue;
            var rows = new List<Placement>();
            int row = 0;
            foreach (var (section, c) in Ide(span.Read()))
            {
                if (section != "inst") continue;
                int index = row++;
                if (c.Length < 11 || !int.TryParse(c[0], out int id) || !int.TryParse(c[2], out int interior) || !TryFloats(c, 3, 7, out var f)
                    || !int.TryParse(c[10], out int lod)) continue;
                var model = Resolve(id, (interior & 0xFF) is not (0 or 13));
                // A text row also names its model; trust the id only when the name agrees (or there is no IDE entry).
                if (model != null && !model.Name.Equals(c[1], StringComparison.OrdinalIgnoreCase))
                    model = FindModel(c[1], model.Archive) ?? FindModel(c[1]) ?? model;
                rows.Add(new Placement(zone, file, index, id, model?.Name ?? c[1], interior, f[0], f[1], f[2],
                    Transform(f[0], f[1], f[2], f[3], f[4], f[5], f[6]), lod, model));
            }
            textRows[zone] = rows;
            zoneFile[zone] = file;
            result.AddRange(rows);
        }

        var streamed = new List<Placement>();
        foreach (var img in _imgs)
            foreach (var e in img.Entries.Where(e => e.Name.EndsWith(".ipl", StringComparison.OrdinalIgnoreCase)).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            {
                var stem = Path.GetFileNameWithoutExtension(e.Name);
                int suffix = stem.LastIndexOf("_stream", StringComparison.OrdinalIgnoreCase);
                if (suffix < 0 || !int.TryParse(stem.AsSpan(suffix + 7), out _)) continue;
                string zone = stem[..suffix].ToLowerInvariant();
                if (!zoneFile.TryGetValue(zone, out var mainFile)) continue;
                string streamFile = mainFile.Replace(Path.GetFileName(mainFile), e.Name);
                _streamFiles[streamFile] = (_imgByArchive.First(kv => kv.Value == img).Key, e.Name);
                byte[] data;
                try { data = img.Read(e); } catch (IOException) { continue; }
                if (data.Length < 76 || !data.AsSpan(0, 4).SequenceEqual("bnry"u8)) continue;
                int count = BitConverter.ToInt32(data, 4), start = BitConverter.ToInt32(data, 28);
                if (count < 0 || start < 76 || start + 40L * count > data.Length) continue;
                for (int i = 0; i < count; i++)
                {
                    int at = start + 40 * i;
                    float F(int o) => BitConverter.ToSingle(data, at + o);
                    int id = BitConverter.ToInt32(data, at + 28), interior = BitConverter.ToInt32(data, at + 32), lod = BitConverter.ToInt32(data, at + 36);
                    var model = Resolve(id, (interior & 0xFF) is not (0 or 13));
                    streamed.Add(new Placement(zone, streamFile, i, id, model?.Name ?? $"#{id}", interior,
                        F(0), F(4), F(8), Transform(F(0), F(4), F(8), F(12), F(16), F(20), F(24)), lod, model));
                }
            }
        result.AddRange(streamed);

        MarkLods(result);
        return result;
    }

    private void MarkLods(List<Placement> placements, string? zone = null)
    {
        var rows = zone == null ? placements : placements.Where(p => p.Zone == zone).ToList();
        var linked = rows.Where(p => p.LodRow >= 0).Select(p => (p.Zone, p.LodRow)).ToHashSet();
        foreach (var p in rows) p.IsLod = !_streamFiles.ContainsKey(p.File) && linked.Contains((p.Zone, p.Row));
    }

    public Placement? LodOf(Placement p)
    {
        if (p.LodRow < 0) return null;
        _lodRows ??= Placements.Where(x => !_streamFiles.ContainsKey(x.File)).ToDictionary(x => (x.Zone, x.Row));
        return _lodRows.GetValueOrDefault((p.Zone, p.LodRow));
    }
    private Dictionary<(string Zone, int Row), Placement>? _lodRows;

    private static bool TryFloats(string[] c, int from, int count, out float[] values)
    {
        values = new float[count];
        for (int i = 0; i < count; i++)
            if (!float.TryParse(c[from + i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out values[i])) return false;
        return true;
    }

    /// <summary>
    /// An IPL placement as the editors' Y-up matrix. SA stores the inverse (conjugate) of the instance's rotation, so
    /// the quaternion is conjugated; the game-space transform is then conjugated by the Z-up -> Y-up turn that
    /// <see cref="BuildMesh"/> puts on every model: M = C * [R | t] * C^-1 with C: (x, y, z) -> (x, z, -y).
    /// </summary>
    public static float[] Transform(float x, float y, float z, float qx, float qy, float qz, float qw)
    {
        float n = MathF.Sqrt(qx * qx + qy * qy + qz * qz + qw * qw);
        if (n < 1e-6f || !float.IsFinite(n)) { qx = qy = qz = 0; qw = n = 1; }
        qx = -qx / n; qy = -qy / n; qz = -qz / n; qw /= n;
        // Game-space rotation, column-major.
        float[] g =
        {
            1 - 2 * (qy * qy + qz * qz), 2 * (qx * qy + qz * qw), 2 * (qx * qz - qy * qw), 0,
            2 * (qx * qy - qz * qw), 1 - 2 * (qx * qx + qz * qz), 2 * (qy * qz + qx * qw), 0,
            2 * (qx * qz + qy * qw), 2 * (qy * qz - qx * qw), 1 - 2 * (qx * qx + qy * qy), 0,
            x, y, z, 1,
        };
        return Assets.Matrix4.Multiply(Assets.Matrix4.Multiply(ZUpToYUp, g), YUpToZUp);
    }

    // The inverse of ZUpToYUp: (x, y, z) -> (x, -z, y).
    private static readonly float[] YUpToZUp = { 1, 0, 0, 0, 0, 0, 1, 0, 0, -1, 0, 0, 0, 0, 0, 1 };

    // ------------------------------------------------------------------ timed objects

    private readonly Dictionary<string, (int On, int Off)> _timedHours = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether a model shows at <paramref name="hour"/> (0-23): always, unless the IDE lists it as a timed
    /// object (tobj) whose hours, which may wrap past midnight, leave that hour out.</summary>
    public bool ShowsAt(Model model, int hour)
    {
        if (!_timedHours.TryGetValue(model.Name, out var t) || t.On == t.Off) return true;
        return t.On < t.Off ? hour >= t.On && hour < t.Off : hour >= t.On || hour < t.Off;
    }

    // ------------------------------------------------------------------ water

    public readonly record struct WaterVertex(Vector3 Position, Vector2 Flow, Vector2 Wave)
    {
        internal WaterVertex Lerp(WaterVertex to, float t) => new(Vector3.Lerp(Position, to.Position, t),
            Vector2.Lerp(Flow, to.Flow, t), Vector2.Lerp(Wave, to.Wave, t));
    }

    private List<WaterVertex[]>? _water;
    public IReadOnlyList<WaterVertex[]> WaterSurfaces => _water ??= ReadWater();

    /// <summary>
    /// DATA/WATER.DAT: after its "processed" line, one quad (4 vertices) or triangle (3) per line, each vertex
    /// "x y z flowX flowY bigWave smallWave" in game space, then a flag. A polygon comes back as its game-space
    /// corners (x, y, z per vertex) in order round its rim (a quad lists its corners as a 2x2 grid: re-ordered).
    /// </summary>
    public IReadOnlyList<float[]> WaterPolygons() => WaterSurfaces.Select(PositionsOf).ToList();

    private static float[] PositionsOf(WaterVertex[] poly) =>
        poly.SelectMany(v => new[] { v.Position.X, v.Position.Y, v.Position.Z }).ToArray();

    private List<WaterVertex[]> ReadWater()
    {
        var result = new List<WaterVertex[]>();
        var data = _disc.GetValueOrDefault("DATA/WATER.DAT")?.Read();
        if (data == null) return result;
        foreach (var raw in System.Text.Encoding.ASCII.GetString(data).Split('\n'))
        {
            var v = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            int corners = v.Length switch { 29 => 4, 22 => 3, _ => 0 };
            if (corners == 0 || !TryFloats(v, 0, corners * 7, out var f) || !f.All(float.IsFinite)) continue;
            var poly = new WaterVertex[corners];
            for (int i = 0; i < corners; i++)
            {
                int j = i * 7;
                poly[i] = new(new(f[j], f[j + 1], f[j + 2]), new(f[j + 3], f[j + 4]), new(f[j + 5], f[j + 6]));
            }
            if (corners == 4) (poly[2], poly[3]) = (poly[3], poly[2]);
            result.Add(poly);
        }
        return result;
    }

    /// <summary>A convex polygon (x, y, z per vertex) clipped to the game-space rectangle [minX, maxX] x [minY, maxY]
    /// (Sutherland-Hodgman; z follows linearly). Empty when nothing is left.</summary>
    public static float[] ClipToRectangle(float[] poly, float minX, float minY, float maxX, float maxY) =>
        PositionsOf(ClipWater(Enumerable.Range(0, poly.Length / 3)
            .Select(i => new WaterVertex(new(poly[3 * i], poly[3 * i + 1], poly[3 * i + 2]), default, default)).ToArray(),
            minX, minY, maxX, maxY));

    public static WaterVertex[] ClipWater(WaterVertex[] poly, float minX, float minY, float maxX, float maxY)
    {
        var pts = poly.ToList();
        foreach (var (axis, limit, keepAbove) in new[] { (0, minX, true), (0, maxX, false), (1, minY, true), (1, maxY, false) })
        {
            if (pts.Count == 0) break;
            var next = new List<WaterVertex>();
            for (int i = 0; i < pts.Count; i++)
            {
                var a = pts[i];
                var b = pts[(i + 1) % pts.Count];
                float da = a.Position[axis] - limit, db = b.Position[axis] - limit;
                bool ina = keepAbove ? da >= 0 : da <= 0, inb = keepAbove ? db >= 0 : db <= 0;
                if (ina) next.Add(a);
                if (ina == inb) continue;
                next.Add(a.Lerp(b, da / (da - db)));
            }
            pts = next;
        }
        return pts.Count < 3 ? Array.Empty<WaterVertex>() : pts.ToArray();
    }
}
