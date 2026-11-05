using CodeDesigner.Ps2.FileFormats.Assets;
using CodeDesigner.Ps2.FileFormats.Gta;

namespace MapBuilder.Server.Services.Assets
{
    /// <summary>
    /// GTA: San Andreas in the Map Builder (view only). The disc's ~48 000 IPL placements are split into areas, one per
    /// IPL zone (LA/LAE2, SF/SFE, COUNTRY/COUNTRYN ...; interiors apart), offered as the page's "missions". A scene draws
    /// the chosen area's high-detail placements, plus the low-detail (lod*) blocks of the neighbouring zones so the area
    /// does not float in a void, and the sea, rivers and pools of DATA/WATER.DAT over the same ground. Overview areas
    /// draw only the low-detail blocks of a whole city, the countryside or the whole map. The scene is the map at noon:
    /// timed objects that are off then (night-lit windows) are left out.
    /// </summary>
    internal static partial class GtaAssetService
    {
        // Grove Street, in game space: the area a disc opens on.
        private const float GroveX = 2495, GroveY = -1680;

        // Neighbouring zones' LOD blocks (and the water) are drawn this far (metres) past the chosen area's edge.
        private const float LodMargin = 600;

        // The hour the scene shows (timed objects).
        private const int SceneHour = 12;

        // The water mesh: "WATER.DAT:@water/<area id>" for ASSET_MODEL_MESH.
        private const string WaterArchive = "WATER.DAT", WaterPrefix = "@water/";

        private const string OverviewPrefix = "lod-";

        private static readonly Dictionary<string, string> Regions = new(StringComparer.OrdinalIgnoreCase)
        {
            ["LA"] = "Los Santos", ["SF"] = "San Fierro", ["VEGAS"] = "Las Venturas", ["COUNTRY"] = "Countryside",
            ["INTERIOR"] = "Interiors", ["LEVELDES"] = "Level design", ["GENERIC"] = "Generic",
        };

        private static bool HasMap(Session s) => s.Package.IsDisc && s.Package.MapAreas.Count > 0;

        /// <summary>A placement's region folder under DATA/MAPS (LA, SF, VEGAS, COUNTRY ...).</summary>
        private static string RegionOf(GtaPackage.Placement p)
        {
            const string root = "DATA/MAPS/";
            if (!p.File.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return "";
            int slash = p.File.IndexOf('/', root.Length);
            return slash < 0 ? "" : p.File[root.Length..slash].ToUpperInvariant();
        }

        private static bool InOverview(string id, GtaPackage.Placement p) =>
            p.IsLod && !p.IsInterior && p.Model != null
            && (id.Equals(OverviewPrefix + "all", StringComparison.OrdinalIgnoreCase) || (OverviewPrefix + RegionOf(p)).Equals(id, StringComparison.OrdinalIgnoreCase));

        /// <summary>The overview areas: each city's and the countryside's LOD blocks, then the whole map's. Cached on the session.</summary>
        private static IReadOnlyList<GtaPackage.MapArea> Overviews(Session s) => s.Overviews ??= new[] { "LA", "SF", "VEGAS", "COUNTRY", "all" }
            .Select(region =>
            {
                string id = OverviewPrefix + region.ToLowerInvariant();
                var rows = s.Package.Placements.Where(p => InOverview(id, p)).ToList();
                return rows.Count == 0 ? null : new GtaPackage.MapArea(id, region, false, rows.Count, rows.Count,
                    rows.Min(p => p.Matrix[12]), rows.Min(p => p.Matrix[14]), rows.Max(p => p.Matrix[12]), rows.Max(p => p.Matrix[14]));
            })
            .OfType<GtaPackage.MapArea>().ToList();

        private static IEnumerable<GtaPackage.MapArea> AllAreas(Session s) => s.Package.MapAreas.Concat(Overviews(s));

        private static bool IsOverview(string id) => id.StartsWith(OverviewPrefix, StringComparison.OrdinalIgnoreCase);

        private static GtaPackage.MapArea AreaById(Session s, string id) =>
            AllAreas(s).FirstOrDefault(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"No map area {id} (see ASSET_STATUS missions)");

        private static string? CurrentArea(Session s)
        {
            if (!HasMap(s)) return null;
            return s.Area ??= (s.Package.AreaAt(GroveX, GroveY) ?? s.Package.MapAreas[0]).Id;
        }

        private static string AreaLabel(GtaPackage.MapArea a)
        {
            if (IsOverview(a.Id))
            {
                string models = a.Placements >= 2000 ? $"{a.Placements} blocks, slow to load" : $"{a.Placements} blocks";
                return a.Id.Equals(OverviewPrefix + "all", StringComparison.OrdinalIgnoreCase)
                    ? $"All of San Andreas · low detail ({models})"
                    : $"{Regions.GetValueOrDefault(a.Label, a.Label)} · low detail ({models})";
            }
            string label = a.Label;
            int slash = label.IndexOf('/');
            if (slash < 0) return label;
            string region = Regions.GetValueOrDefault(label[..slash], label[..slash]);
            return $"{region} · {label[(slash + 1)..]} ({a.Placements})";
        }

        // Zones first (outdoors, then interiors), then the overviews.
        private static object[] Missions(Session s) => !HasMap(s) ? Array.Empty<object>()
            : AllAreas(s).Select(a => (object)new { id = a.Id, kind = "", mode = "", label = AreaLabel(a) }).ToArray();

        private sealed record PointArgs(string? Mission);

        /// <summary>ASSET_MAP_POINTS: picks the area (its "mission"); GTA has no AI points to list.</summary>
        private static object Points(Session s, string? json)
        {
            if (AssetJson.Parse<PointArgs>(json)?.Mission is { Length: > 0 } pick)
            {
                if (!HasMap(s)) throw new InvalidOperationException("Open the GTA: San Andreas disc (or its mounted folder) to view the map.");
                var area = AreaById(s, pick);
                if (!area.Id.Equals(s.Area, StringComparison.OrdinalIgnoreCase)) { s.Area = area.Id; s.Revision++; }
            }
            return new { mission = CurrentArea(s), points = Array.Empty<object>(), teamSpawns = Array.Empty<object>() };
        }

        private sealed record SceneArgs(int? Limit);

        /// <summary>
        /// ASSET_MAP_SCENE. A zone: its placements (kind "reference", Props layer) that are not another placement's LOD,
        /// the LOD placements of the other outdoor zones near it (kind "model", World layer) and its water. An overview:
        /// the region's LOD placements and water. Each carries the Y-up placement matrix and its model as
        /// "archive:name" for ASSET_MODEL_MESH.
        /// </summary>
        private static object MapScene(Session s, string? json)
        {
            string id = CurrentArea(s) ?? throw new InvalidOperationException(
                "This GTA package has no map. Open the San Andreas disc image or mounted disc folder (not a single IMG or DFF).");
            var pkg = s.Package;
            var area = AreaById(s, id);
            bool overview = IsOverview(area.Id);
            int limit = AssetJson.Parse<SceneArgs>(json)?.Limit ?? int.MaxValue;
            var instances = new List<object>();
            int detail = 0, lods = 0, hiddenLods = 0, unresolved = 0, offAtNoon = 0, water = 0;
            object Item(string kind, GtaPackage.Placement p) => new
            {
                kind, model = p.Model!.Name, instance = $"{p.Zone}:{Path.GetFileNameWithoutExtension(p.File)}:{p.Row}",
                path = $"/{p.AreaId}/{p.Model.Name}/{Path.GetFileNameWithoutExtension(p.File)}#{p.Row}",
                position = new[] { p.Matrix[12], p.Matrix[13], p.Matrix[14] }, scale = 1f, matrix = p.Matrix,
                // The area's own placements move by id (moveInstance), several in one batch; distant LOD blocks stay put.
                geoIndex = (int?)null, clutterIndex = (int?)null, source = $"{p.Model.Archive}:{p.Model.Name}", caNode = kind == "reference", multiMove = kind == "reference",
            };
            float margin = overview ? 0 : LodMargin;
            float minX = area.MinX - margin, maxX = area.MaxX + margin, minZ = area.MinZ - margin, maxZ = area.MaxZ + margin;
            foreach (var p in pkg.Placements)
            {
                if (instances.Count >= limit) break;
                if (overview)
                {
                    if (!InOverview(area.Id, p)) continue;
                    lods++;
                    instances.Add(Item("model", p));
                    continue;
                }
                bool mine = p.AreaId.Equals(id, StringComparison.OrdinalIgnoreCase);
                if (mine)
                {
                    if (p.Model == null) { unresolved++; continue; }
                    // The game swaps a LOD for its detailed placements up close; drawing both would z-fight.
                    if (p.IsLod) { hiddenLods++; continue; }
                    if (!pkg.ShowsAt(p.Model, SceneHour)) { offAtNoon++; continue; }
                    detail++;
                    instances.Add(Item("reference", p));
                }
                else if (!area.Interior && p.IsLod && !p.IsInterior && p.Model != null
                    && p.Matrix[12] >= minX && p.Matrix[12] <= maxX && p.Matrix[14] >= minZ && p.Matrix[14] <= maxZ)
                {
                    lods++;
                    instances.Add(Item("model", p));
                }
            }
            if (!area.Interior && instances.Count < limit && WaterMesh(s, area.Id) is { Chunks.Count: > 0 } sea)
            {
                water = sea.Chunks.Sum(c => c.Triangles.Length / 3);
                instances.Add(new
                {
                    kind = "model", model = "water", instance = "water", path = "/water", position = new[] { 0f, 0f, 0f }, scale = 1f,
                    matrix = Matrix4.Identity(), geoIndex = (int?)null, clutterIndex = (int?)null, source = $"{WaterArchive}:{WaterPrefix}{area.Id}",
                    caNode = false, multiMove = false,
                });
            }
            var counts = overview
                ? new Dictionary<string, int> { ["LOD block"] = lods, ["water triangle"] = water }
                : new Dictionary<string, int> { ["placement"] = detail, ["distant LOD"] = lods, ["LOD (hidden)"] = hiddenLods, ["off at noon"] = offAtNoon, ["water triangle"] = water, ["unresolved"] = unresolved };
            return new
            {
                sceneNamespace = AreaLabel(area), counts, returned = instances.Count, instances, game = Game, hour = SceneHour,
                area = new { area.Id, label = AreaLabel(area), area.Interior, bounds = new[] { area.MinX, area.MinZ, area.MaxX, area.MaxZ } },
            };
        }

        // Sea green-blue, translucent; vertex colours use 128 as 1.0.
        private static readonly byte[] WaterColor = { 52, 84, 92, 96 };

        /// <summary>
        /// The water of DATA/WATER.DAT clipped to an area's ground (plus the LOD margin), in the editors' Y-up frame:
        /// one untextured, translucent chunk facing up. Null when the area has no water (or the package no WATER.DAT).
        /// </summary>
        private static ModelMesh? WaterMesh(Session s, string areaId)
        {
            var area = AreaById(s, areaId);
            if (area.Interior) return null;
            float margin = IsOverview(area.Id) ? 0 : LodMargin;
            // Y-up (x, z) bounds back to game (x, y): game y = -z.
            float minX = area.MinX - margin, maxX = area.MaxX + margin, minY = -(area.MaxZ + margin), maxY = -(area.MinZ - margin);
            var pos = new List<float>();
            var tri = new List<int>();
            foreach (var poly in s.Package.WaterPolygons())
            {
                var clipped = GtaPackage.ClipToRectangle(poly, minX, minY, maxX, maxY);
                int n = clipped.Length / 3;
                if (n < 3) continue;
                int first = pos.Count / 3;
                for (int i = 0; i < n; i++) { pos.Add(clipped[3 * i]); pos.Add(clipped[3 * i + 2]); pos.Add(-clipped[3 * i + 1]); }
                // A fan; wound so its normal points up (+Y) whichever way the rim runs.
                float area2 = 0;
                for (int i = 0; i < n; i++)
                {
                    int j = (i + 1) % n;
                    area2 += clipped[3 * i] * clipped[3 * j + 1] - clipped[3 * j] * clipped[3 * i + 1];
                }
                // Counter-clockwise in game (x, y) seen from above (+Z) stays counter-clockwise seen from +Y.
                for (int i = 1; i + 1 < n; i++)
                {
                    if (area2 >= 0) { tri.Add(first); tri.Add(first + i); tri.Add(first + i + 1); }
                    else { tri.Add(first); tri.Add(first + i + 1); tri.Add(first + i); }
                }
            }
            var mesh = new ModelMesh { Archive = WaterArchive, Name = WaterPrefix + area.Id };
            if (tri.Count == 0) return mesh;
            int count = pos.Count / 3;
            var normals = new float[count * 3];
            var colors = new byte[count * 4];
            for (int i = 0; i < count; i++)
            {
                normals[3 * i + 1] = 1;
                WaterColor.CopyTo(colors, 4 * i);
            }
            mesh.Chunks.Add(new ModelMesh.MeshChunk
            {
                Index = 0, Visual = "water", Texture = null, Unit = "Float", Scale = 1, VertexCount = count, Positions = pos.ToArray(),
                Uvs = new float[count * 2], Normals = normals, Colors = colors, MaterialBlend = true, Triangles = tri.ToArray(),
            });
            mesh.Recompute();
            return mesh;
        }
    }
}
