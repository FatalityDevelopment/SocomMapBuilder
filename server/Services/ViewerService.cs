using System.Text.Json;

namespace MapBuilder.Server.Services
{
    /// <summary>
    /// File-system lookups the Asset Viewer needs around the file it opened (CodeDesigner's VS Code extension did
    /// these itself): the companion _TXR / _PAL / _GEO / _MDL archives, a sibling palette archive, and the files of a
    /// loose (unpacked) map package folder.
    /// </summary>
    internal sealed class ViewerService : ICommandService
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        // Prefixes of shared support archives that never name a map's own texture family.
        private static readonly HashSet<string> SupportPrefixes = new(StringComparer.OrdinalIgnoreCase)
        {
            "alph", "benz", "clib", "effe", "flib", "font", "hud", "hud2", "hudw", "junk", "ld", "load", "objt", "tlrm", "trmg", "weap",
        };

        public void Register(CommandRegistry reg)
        {
            reg.Add(c => Run("VIEWER_COMPANIONS", c.InputPath, p => FindCompanionFiles(p)), "VIEWER_COMPANIONS");
            reg.Add(c => Run("VIEWER_PALETTE", c.InputPath, p => new { path = FindSiblingPaletteFile(p) }), "VIEWER_PALETTE");
            reg.Add(c => Run("VIEWER_LOOSE_PACKAGE", c.InputPath, LoosePackage), "VIEWER_LOOSE_PACKAGE");
        }

        private static string Run(string command, string? inputPath, Func<string, object> body)
        {
            if (string.IsNullOrWhiteSpace(inputPath)) return Responses.Error("InputPath is required");
            try
            {
                string full = Path.GetFullPath(inputPath);
                return Responses.Json(new CommandResponse { Success = true, Command = command, Data = JsonSerializer.Serialize(body(full), JsonOptions) });
            }
            catch (Exception ex) { return Responses.Error($"{command}: {ex.Message}"); }
        }

        // ------------------------------------------------------------------ companions

        private static Dictionary<string, string> FindCompanionFiles(string currentPath)
        {
            string dir = Path.GetDirectoryName(currentPath)!;
            string name = Path.GetFileName(currentPath);
            var result = new Dictionary<string, string>();

            // Reader archives (READERC.ZAR / READERX.ZAR): the UI textures and palettes, so RDR screens show thumbnails.
            if (System.Text.RegularExpressions.Regex.IsMatch(name, @"^reader[cx]\.zar$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                if (FirstExisting(dir, "UI_TXR.ZED", "UI2D_TXR.ZED") is { } txr) result["txr"] = txr;
                if (FirstExisting(dir, "UI_PAL.ZED", "UI2D_PAL.ZED") is { } pal) result["pal"] = pal;
                return result;
            }

            var family = System.Text.RegularExpressions.Regex.Match(name, @"^(.+?)_(MDL|TXR|PAL|GEO)(\([^)]+\))?\.ZED$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!family.Success) return result;
            string prefix = family.Groups[1].Value;
            string companion = prefix.Equals("worl", StringComparison.OrdinalIgnoreCase) ? FindMapCompanionPrefix(dir) ?? prefix : prefix;

            var candidates = new Dictionary<string, string[]>
            {
                ["txr"] = new[] { $"{companion}_TXR.ZED", $"{companion}_TXR(1).ZED" },
                ["pal"] = new[] { $"{companion}_PAL.ZED", $"{companion}_PAL(1).ZED" },
                ["geo"] = new[] { $"{companion}_GEO.ZED", $"{prefix}_GEO.ZED" },
                ["mdl"] = new[] { $"{prefix}_MDL.ZED" },
            };
            foreach (var (kind, names) in candidates)
                if (FirstExisting(dir, names) is { } path) result[kind] = path;
            return result;
        }

        private static string? FindMapCompanionPrefix(string dir)
        {
            string dirPrefix = Path.GetFileName(dir);
            bool HasTxrPal(string p) => File.Exists(Path.Combine(dir, $"{p}_TXR.ZED")) && File.Exists(Path.Combine(dir, $"{p}_PAL.ZED"));
            if (dirPrefix.Length > 0 && HasTxrPal(dirPrefix)) return dirPrefix;
            var prefixes = Directory.EnumerateFiles(dir)
                .Select(f => System.Text.RegularExpressions.Regex.Match(Path.GetFileName(f), @"^(.+?)_TXR(?:\([^)]+\))?\.ZED$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                .Where(m => m.Success).Select(m => m.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase);
            return prefixes.FirstOrDefault(p =>
                !SupportPrefixes.Contains(p)
                && !System.Text.RegularExpressions.Regex.IsMatch(p, @"^ld\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                && !System.Text.RegularExpressions.Regex.IsMatch(p, @"^z.+\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                && HasTxrPal(p));
        }

        private static string? FindSiblingPaletteFile(string currentPath)
        {
            string dir = Path.GetDirectoryName(currentPath)!;
            string name = Path.GetFileName(currentPath);
            var candidates = new List<string>();
            var txr = System.Text.RegularExpressions.Regex.Match(name, @"^(.*?)_TXR(?:\([^)]+\))?\.ZED$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (txr.Success) { candidates.Add($"{txr.Groups[1].Value}_PAL.ZED"); candidates.Add($"{txr.Groups[1].Value}_PAL(1).ZED"); }
            candidates.Add(System.Text.RegularExpressions.Regex.Replace(name, "_TXR", "_PAL", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
            if (FirstExisting(dir, candidates.ToArray()) is { } hit) return hit;
            return Directory.EnumerateFiles(dir).FirstOrDefault(f =>
                System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(f), @"_PAL(?:\([^)]+\))?\.ZED$", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        }

        private static string? FirstExisting(string dir, params string[] names) =>
            names.Select(n => Path.Combine(dir, n)).FirstOrDefault(File.Exists);

        // ------------------------------------------------------------------ loose map package

        /// <summary>
        /// A loose map .ZED (next to WORL_MDL.ZED and its _TXR / _PAL family): every archive of the folder, the AI map
        /// (AIMAPS.MPS), the map config (MP53.ZED) and CLUTTER.ZAR, as the package nodes the viewer decodes.
        /// </summary>
        private static object LoosePackage(string filePath)
        {
            if (!IsLooseMapPackageFile(filePath)) return new { loose = false };
            string dir = Path.GetDirectoryName(filePath)!;
            static int Rank(string n) =>
                n.StartsWith("worl_mdl", StringComparison.OrdinalIgnoreCase) ? 0
                : n.Contains("_geo", StringComparison.OrdinalIgnoreCase) ? 1
                : n.Contains("_mdl", StringComparison.OrdinalIgnoreCase) ? 2
                : n.Contains("_txr", StringComparison.OrdinalIgnoreCase) ? 3
                : n.Contains("_pal", StringComparison.OrdinalIgnoreCase) ? 4 : 5;
            var names = Directory.EnumerateFiles(dir).Select(Path.GetFileName).OfType<string>()
                .Where(n =>
                    System.Text.RegularExpressions.Regex.IsMatch(n, @"(?:_(?:MDL|TXR|PAL|GEO)(?:\([^)]+\))?|^WORL_MDL)\.ZED$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                    || n.EndsWith(".MPS", StringComparison.OrdinalIgnoreCase)
                    || n.Equals("CLUTTER.ZAR", StringComparison.OrdinalIgnoreCase)
                    || System.Text.RegularExpressions.Regex.IsMatch(n, @"^[A-Z]+\d+\.ZED$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                .OrderBy(Rank).ThenBy(n => n, StringComparer.Ordinal)
                .ToList();
            var nodes = names.Select((n, i) => new { index = i, name = n, type = 0, data = Convert.ToBase64String(File.ReadAllBytes(Path.Combine(dir, n))) }).ToList();
            return new { loose = true, nodes };
        }

        private static bool IsLooseMapPackageFile(string filePath)
        {
            if (!Path.GetExtension(filePath).Equals(".zed", StringComparison.OrdinalIgnoreCase)) return false;
            string dir = Path.GetDirectoryName(filePath)!;
            string stem = Path.GetFileNameWithoutExtension(filePath);
            string dirPrefix = Path.GetFileName(dir);
            bool HasFamily(string p) => File.Exists(Path.Combine(dir, $"{p}_TXR.ZED")) && File.Exists(Path.Combine(dir, $"{p}_PAL.ZED"));
            return File.Exists(Path.Combine(dir, "WORL_MDL.ZED")) && (HasFamily(stem) || HasFamily(dirPrefix) || FindMapCompanionPrefix(dir) != null);
        }
    }
}
