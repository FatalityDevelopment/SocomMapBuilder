using CodeDesigner.Ps2.FileFormats.Zipper.Package;

namespace MapBuilder.Server.Services
{
    /// <summary>
    /// Autosave: every <see cref="AutosaveMinutes"/> minutes, each open session with unsaved edits since its last
    /// autosave is written whole, as a copy, to <see cref="AutosaveFolder"/> (<c>&lt;package&gt;.autosave.ZDB</c>,
    /// replaced each time; the previous one is kept once as .bak). It is the same write as Save copy, so the session
    /// stays unsaved and the package on disk is never touched. Covers every page and the AI alike, since they share the
    /// session. ASSET_STATUS reports the last one.
    /// </summary>
    internal sealed partial class ZipperAssetService
    {
        internal static string AutosaveFolder { get; set; } = Environment.GetEnvironmentVariable("MAPBUILDER_AUTOSAVE") is { Length: > 0 } env ? env
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MapBuilder", "autosave");

        /// <summary>Minutes between autosaves (MAPBUILDER_AUTOSAVE_MINUTES; 0 turns it off).</summary>
        internal static double AutosaveMinutes { get; } = double.TryParse(Environment.GetEnvironmentVariable("MAPBUILDER_AUTOSAVE_MINUTES"), out double m) ? m : 3;

        internal sealed record Autosaved(string Path, int Revision, DateTime At);

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Autosaved> LastAutosave = new(StringComparer.OrdinalIgnoreCase);
        private static Timer? _autosaveTimer;

        private static void StartAutosave()
        {
            if (AutosaveMinutes <= 0 || _autosaveTimer != null) return;
            var every = TimeSpan.FromMinutes(AutosaveMinutes);
            _autosaveTimer = new Timer(_ => { try { AutosaveNow(); } catch { /* a failed autosave waits for the next */ } }, null, every, every);
        }

        /// <summary>Writes the recovery copy of every session changed since its last one. Answers the paths written.</summary>
        internal static List<string> AutosaveNow()
        {
            var written = new List<string>();
            foreach (var s in Sessions.Values)
            {
                lock (s.Gate)
                {
                    if (s.Dirty.Count == 0) continue;
                    if (LastAutosave.TryGetValue(s.Id, out var last) && last.Revision == s.Revision) continue;
                    string source = s.OpenPath.Length > 0 ? s.OpenPath : s.Package.SourcePath;
                    string ext = Path.GetExtension(source);
                    // Loose package folders save in place only; their recovery copy is a ZDB.
                    if (!ext.Equals(".ZDB", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".CDB", StringComparison.OrdinalIgnoreCase)) ext = ".ZDB";
                    Directory.CreateDirectory(AutosaveFolder);
                    string path = Path.Combine(AutosaveFolder, Path.GetFileNameWithoutExtension(source.TrimEnd('\\', '/')) + ".autosave" + ext.ToUpperInvariant());
                    // A package that cannot be written (SOCOM 3 / CA, read-only) waits; the others still save.
                    try { PackageSave.Write(s.Package, s.Dirty.ToList(), path); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException) { continue; }
                    LastAutosave[s.Id] = new Autosaved(path, s.Revision, DateTime.UtcNow);
                    written.Add(path);
                }
            }
            return written;
        }

        private static object? AutosaveOf(AssetSession s) =>
            LastAutosave.TryGetValue(s.Id, out var a) ? new { path = a.Path, revision = a.Revision, at = a.At } : null;
    }
}
