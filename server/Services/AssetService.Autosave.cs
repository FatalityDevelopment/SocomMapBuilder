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
                    Directory.CreateDirectory(AutosaveFolder);
                    string path = AutosavePath(source, s.Package.IsZdb);
                    try { PackageSave.Write(s.Package, s.Dirty.ToList(), path); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException) { continue; }
                    LastAutosave[s.Id] = new Autosaved(path, s.Revision, DateTime.UtcNow);
                    written.Add(path);
                }
            }
            foreach (var s in CaSessions.Values)
            {
                lock (s.Gate)
                {
                    if (!CaSessions.ContainsKey(s.Id)) continue;
                    if (s.Dirty.Count == 0 || LastAutosave.TryGetValue(s.Id, out var last) && last.Revision == s.Revision) continue;
                    string path = AutosavePath(s.Package.SourcePath, true);
                    try { WriteCaCopy(s, path); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException) { continue; }
                    LastAutosave[s.Id] = new Autosaved(path, s.Revision, DateTime.UtcNow);
                    written.Add(path);
                }
            }
            return written;
        }

        /// <summary>
        /// Where a package's recovery copy goes: <c>&lt;package&gt;-&lt;8 hex of its full path&gt;.autosave.ZDB</c> (or .CDB),
        /// so two packages with the same name in different folders (every map's M51.ZDB copies) never replace each
        /// other's. A loose package folder has no archive to rewrite: its copy is a folder of its changed files,
        /// <c>&lt;package&gt;-&lt;hash&gt;.autosave</c>.
        /// </summary>
        internal static string AutosavePath(string source, bool isZdb)
        {
            string full = Path.GetFullPath(source.TrimEnd('\\', '/'));
            string name = Path.GetFileNameWithoutExtension(full);
            byte[] digest = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(full.ToUpperInvariant()));
            string tag = Convert.ToHexString(digest, 0, 4).ToLowerInvariant();
            string ext = isZdb ? Path.GetExtension(full).ToUpperInvariant() : "";
            if (isZdb && ext is not (".ZDB" or ".CDB" or ".ZAR")) ext = ".ZDB";
            return Path.Combine(AutosaveFolder, $"{name}-{tag}.autosave{ext}");
        }

        private static object? AutosaveOf(string id) =>
            LastAutosave.TryGetValue(id, out var a) ? new { path = a.Path, revision = a.Revision, at = a.At } : null;
    }
}
