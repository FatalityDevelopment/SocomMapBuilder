using CodeDesigner.Ps2.FileFormats.Gta;
using CodeDesigner.Ps2.FileFormats.Swbf;

namespace MapBuilder.Server.Services
{
    /// <summary>
    /// The Asset Viewer over disc images and Star Wars: Battlefront levels. A .ISO lists its files (by their path on the
    /// disc) as records read straight from the image. A .LVL, loose or on a Battlefront disc, lists the models and
    /// textures the level holds from a Battlefront asset session, so they preview with ASSET_MODEL_MESH /
    /// ASSET_TEXTURE and open in the Model Editor like a package's.
    /// </summary>
    internal sealed partial class ZipperAssetService
    {
        private static bool IsDiscImage(string path) => path.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) && File.Exists(path);

        private static List<ViewerNode> DiscNodes(string path) => Cached(path, () =>
            Iso9660.Files(path).OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase).Select(f =>
            {
                var span = f.Value;
                return new ViewerNode(f.Key, span.Length, () => span.Read(), null, f.Key, n => span.Read(0, Math.Min(n, span.Length)));
            }).ToList());

        /// <summary>The Battlefront session and level name for a .LVL place: a loose level, or a level record of a
        /// Battlefront disc image.</summary>
        private static (Assets.SwbfAssetService.Session Session, string Archive)? SwbfLevelOf(Place place)
        {
            if (place.Inner.Length == 0)
                return SwbfPackage.IsSwbfPath(place.Path) ? (Assets.SwbfAssetService.SessionFor(place.Path, out _), Path.GetFileName(place.Path).ToUpperInvariant()) : null;
            if (place.Inner.Length != 1 || !IsDiscImage(place.Path) || !SwbfPackage.IsIndexedLevel(place.Name)) return null;
            if (!DiscNodes(place.Path).Any(n => n.Name.Equals("SWBFRONT.ELF", StringComparison.OrdinalIgnoreCase))) return null;
            var s = Assets.SwbfAssetService.SessionFor(place.Path, out _);
            string archive = place.Name[SwbfPackage.LevelRoot.Length..];
            return s.Package.LevelIndex(archive) >= 0 ? (s, archive) : null;
        }

        private static object SwbfLevelListing(string full, Place place, List<object> crumbs, Assets.SwbfAssetService.Session s, string archive)
        {
            var pkg = s.Package;
            var items = new List<object>();
            foreach (var m in pkg.Models.Where(m => m.Archive.Equals(archive, StringComparison.OrdinalIgnoreCase)).OrderBy(m => m.Kind).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
                items.Add(new
                {
                    name = m.Name, kind = "model", size = (long)m.Size,
                    detail = $"{m.Kind}, {m.Segments} segment{(m.Segments == 1 ? "" : "s")}, {m.Vertices:N0} vertices{(m.Skinned ? ", skinned" : "")}",
                    path = full, inner = place.Inner,
                    @ref = new { archive = m.Archive, name = m.Name, modelKind = m.Kind },
                });
            foreach (var t in pkg.Textures.Where(t => t.Archive.Equals(archive, StringComparison.OrdinalIgnoreCase)).OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
                items.Add(new
                {
                    name = t.Name, kind = "texture", size = (long)t.Size, detail = $"{t.Width}×{t.Height}, {t.Depth}-bit",
                    path = full, inner = place.Inner, @ref = new { name = t.Name, archive = t.Archive, @namespace = t.Archive },
                });
            return new { path = full, inner = place.Inner, name = place.Name, kind = "level", size = place.Size, session = s.Id, package = pkg.SourcePath, crumbs, items, editable = false, editSession = (string?)null, editPackage = (string?)null };
        }

        private static object DiscPreview(Place place, List<(string, string)> facts)
        {
            if (place.Inner.Length != 0) return HexPreview(place, "disc", facts, null);
            var files = Iso9660.Files(place.Path);
            facts.Add(("Files", files.Count.ToString()));
            string game = SwbfPackage.IsSwbfDisc(files) ? "Star Wars: Battlefront"
                : files.ContainsKey("MODELS/GTA3.IMG") ? "GTA: San Andreas"
                : files.Keys.FirstOrDefault(k => k.StartsWith("SLUS_", StringComparison.OrdinalIgnoreCase) || k.StartsWith("SCUS_", StringComparison.OrdinalIgnoreCase) || k.StartsWith("SLES_", StringComparison.OrdinalIgnoreCase) || k.StartsWith("SCES_", StringComparison.OrdinalIgnoreCase)) ?? "Unknown";
            facts.Add(("Game", game));
            if (game == "Star Wars: Battlefront") facts.Add(("Levels", files.Keys.Count(SwbfPackage.IsIndexedLevel).ToString()));
            return new { type = "archive", name = place.Name, kind = "disc", facts = Facts(facts) };
        }

        private static object LevelPreview(Place place, List<(string, string)> facts)
        {
            if (SwbfLevelOf(place) is not { } level) return HexPreview(place, "level", facts, null);
            var pkg = level.Session.Package;
            facts.Add(("Format", "ZeroEngine level (ucfb)"));
            facts.Add(("Level", level.Archive));
            var models = pkg.Models.Where(m => m.Archive.Equals(level.Archive, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var g in models.GroupBy(m => m.Kind).OrderBy(g => g.Key)) facts.Add(($"Models: {g.Key}", g.Count().ToString()));
            facts.Add(("Textures", pkg.Textures.Count(t => t.Archive.Equals(level.Archive, StringComparison.OrdinalIgnoreCase)).ToString()));
            int li = pkg.LevelIndex(level.Archive);
            if (li >= 0)
            {
                var root = Ucfb.Root(pkg.LevelBytes(li));
                var tags = root.Children.GroupBy(c => c.Tag).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} ×{g.Count()}");
                facts.Add(("Chunks", string.Join(", ", tags)));
                if (root.Child("wrld") is { } w) facts.Add(("World", $"{w.ChildText("NAME")}, {w.Children.Count(c => c.Tag == "inst")} placed objects"));
            }
            return new { type = "archive", name = place.Name, kind = "level", facts = Facts(facts) };
        }
    }
}
