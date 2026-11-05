using CodeDesigner.Ps2.FileFormats.Gta;

namespace MapBuilder.Server.Services.Assets
{
    /// <summary>
    /// GTA: San Andreas in the Map Builder (view only). The disc's ~48 000 IPL placements are split into areas, one per
    /// IPL zone (LA/LAE2, SF/SFE, COUNTRY/COUNTRYN ...; interiors apart), offered as the page's "missions". A scene draws
    /// the chosen area's high-detail placements, plus the low-detail (lod*) blocks of the neighbouring zones so the area
    /// does not float in a void.
    /// </summary>
    internal static partial class GtaAssetService
    {
        // Grove Street, in game space: the area a disc opens on.
        private const float GroveX = 2495, GroveY = -1680;

        // Neighbouring zones' LOD blocks are drawn this far (metres) past the chosen area's edge.
        private const float LodMargin = 600;

        private static readonly Dictionary<string, string> Regions = new(StringComparer.OrdinalIgnoreCase)
        {
            ["LA"] = "Los Santos", ["SF"] = "San Fierro", ["VEGAS"] = "Las Venturas", ["COUNTRY"] = "Countryside",
            ["INTERIOR"] = "Interiors", ["LEVELDES"] = "Level design", ["GENERIC"] = "Generic",
        };

        private static bool HasMap(Session s) => s.Package.IsDisc && s.Package.MapAreas.Count > 0;

        private static string? CurrentArea(Session s)
        {
            if (!HasMap(s)) return null;
            return s.Area ??= (s.Package.AreaAt(GroveX, GroveY) ?? s.Package.MapAreas[0]).Id;
        }

        private static string AreaLabel(GtaPackage.MapArea a)
        {
            string label = a.Label;
            int slash = label.IndexOf('/');
            if (slash < 0) return label;
            string region = Regions.GetValueOrDefault(label[..slash], label[..slash]);
            return $"{region} · {label[(slash + 1)..]} ({a.Placements})";
        }

        private static object[] Missions(Session s) => !HasMap(s) ? Array.Empty<object>()
            : s.Package.MapAreas.Select(a => (object)new { id = a.Id, kind = "", mode = "", label = AreaLabel(a) }).ToArray();

        private sealed record PointArgs(string? Mission);

        /// <summary>ASSET_MAP_POINTS: picks the area (its "mission"); GTA has no AI points to list.</summary>
        private static object Points(Session s, string? json)
        {
            if (AssetJson.Parse<PointArgs>(json)?.Mission is { Length: > 0 } pick)
            {
                if (!HasMap(s)) throw new InvalidOperationException("Open the GTA: San Andreas disc (or its mounted folder) to view the map.");
                var area = s.Package.MapAreas.FirstOrDefault(a => a.Id.Equals(pick, StringComparison.OrdinalIgnoreCase))
                    ?? throw new KeyNotFoundException($"No map area {pick} (see ASSET_STATUS missions)");
                if (!area.Id.Equals(s.Area, StringComparison.OrdinalIgnoreCase)) { s.Area = area.Id; s.Revision++; }
            }
            return new { mission = CurrentArea(s), points = Array.Empty<object>(), teamSpawns = Array.Empty<object>() };
        }

        private sealed record SceneArgs(int? Limit);

        /// <summary>
        /// ASSET_MAP_SCENE: the area's placements (kind "reference", Props layer) that are not another placement's LOD,
        /// and the LOD placements of the other outdoor zones near it (kind "model", World layer). Each carries the
        /// Y-up placement matrix and its model as "archive:name" for ASSET_MODEL_MESH.
        /// </summary>
        private static object MapScene(Session s, string? json)
        {
            string id = CurrentArea(s) ?? throw new InvalidOperationException(
                "This GTA package has no map. Open the San Andreas disc image or mounted disc folder (not a single IMG or DFF).");
            var pkg = s.Package;
            var area = pkg.MapAreas.First(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            int limit = AssetJson.Parse<SceneArgs>(json)?.Limit ?? int.MaxValue;
            var instances = new List<object>();
            int detail = 0, lods = 0, hiddenLods = 0, unresolved = 0;
            object Item(string kind, GtaPackage.Placement p) => new
            {
                kind, model = p.Model!.Name, instance = $"{p.Zone}:{Path.GetFileNameWithoutExtension(p.File)}:{p.Row}",
                path = $"/{p.AreaId}/{p.Model.Name}/{Path.GetFileNameWithoutExtension(p.File)}#{p.Row}",
                position = new[] { p.Matrix[12], p.Matrix[13], p.Matrix[14] }, scale = 1f, matrix = p.Matrix,
                geoIndex = (int?)null, clutterIndex = (int?)null, source = $"{p.Model.Archive}:{p.Model.Name}", caNode = false, multiMove = false,
            };
            float minX = area.MinX - LodMargin, maxX = area.MaxX + LodMargin, minZ = area.MinZ - LodMargin, maxZ = area.MaxZ + LodMargin;
            foreach (var p in pkg.Placements)
            {
                if (instances.Count >= limit) break;
                bool mine = p.AreaId.Equals(id, StringComparison.OrdinalIgnoreCase);
                if (mine)
                {
                    if (p.Model == null) { unresolved++; continue; }
                    // The game swaps a LOD for its detailed placements up close; drawing both would z-fight.
                    if (p.IsLod) { hiddenLods++; continue; }
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
            return new
            {
                sceneNamespace = AreaLabel(area),
                counts = new Dictionary<string, int> { ["placement"] = detail, ["distant LOD"] = lods, ["LOD (hidden)"] = hiddenLods, ["unresolved"] = unresolved },
                returned = instances.Count, instances, game = Game,
                area = new { area.Id, label = AreaLabel(area), area.Interior, bounds = new[] { area.MinX, area.MinZ, area.MaxX, area.MaxZ } },
            };
        }
    }
}
