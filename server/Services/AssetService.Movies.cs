using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace MapBuilder.Server.Services
{
    /// <summary>
    /// Asset Viewer movies: browsers cannot play a .PSS (MPEG-2 video, and a sound track in the PS2's own "SShd"
    /// layout that ffmpeg does not read either), so VIEWER_MOVIE takes the sound out to a WAV (PssAudio) and has
    /// ffmpeg join it to the video as H.264 / AAC MP4, once, into a cache; GET /api/viewer/movie/{name} streams it
    /// (with ranges, so the player seeks). ffmpeg is found through MAPBUILDER_FFMPEG, PATH or winget's links folder.
    /// </summary>
    internal sealed partial class ZipperAssetService
    {
        private static readonly object MovieGate = new();

        internal static string MovieCacheDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MapBuilder", "movies");

        private static void RegisterMovies(CommandRegistry reg)
        {
            reg.Add(c => Responses.Run("VIEWER_MOVIE", () => ConvertMovie(c.InputPath)), "VIEWER_MOVIE");
        }

        /// <summary>The ffmpeg to convert with, or null when there is none.</summary>
        internal static string? FindFfmpeg()
        {
            string exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
            var candidates = new List<string?> { Environment.GetEnvironmentVariable("MAPBUILDER_FFMPEG") };
            candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Select(d => Path.Combine(d.Trim('"'), exe)));
            string winget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet");
            candidates.Add(Path.Combine(winget, "Links", exe));
            // winget's own package folder (its alias is not always linked, and PATH changes need a new login).
            string packages = Path.Combine(winget, "Packages");
            if (Directory.Exists(packages))
                candidates.AddRange(Directory.GetDirectories(packages, "*FFmpeg*").OrderDescending()
                    .SelectMany(d => Directory.GetFiles(d, exe, SearchOption.AllDirectories)));
            return candidates.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p));
        }

        // The cache name: the movie's path, size and date, so a changed file converts again.
        private static string MovieKey(string full)
        {
            var info = new FileInfo(full);
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{full.ToLowerInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}"));
            return $"{Path.GetFileNameWithoutExtension(full)}-{Convert.ToHexString(hash, 0, 6).ToLowerInvariant()}.mp4";
        }

        internal static string? CachedMovie(string full)
        {
            string path = Path.Combine(MovieCacheDir, MovieKey(full));
            return File.Exists(path) ? path : null;
        }

        private static object ConvertMovie(string? path)
        {
            string full = FullPathOf(path);
            if (!full.EndsWith(".pss", StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) throw new ArgumentException("VIEWER_MOVIE takes a .PSS file");
            string name = MovieKey(full);
            string target = Path.Combine(MovieCacheDir, name);
            lock (MovieGate)
            {
                if (!File.Exists(target))
                {
                    string ffmpeg = FindFfmpeg() ?? throw new InvalidOperationException(
                        "Converting movies needs ffmpeg. Install it (winget install Gyan.FFmpeg) or set MAPBUILDER_FFMPEG to ffmpeg.exe, then restart the server.");
                    Directory.CreateDirectory(MovieCacheDir);
                    string temp = target + ".part.mp4", wav = target + ".part.wav";
                    try
                    {
                        // The sound track, when the movie has one this reads; without it, the picture alone.
                        bool sound = CodeDesigner.Ps2.FileFormats.Ps2.PssAudio.ExtractWav(full, wav) != null;
                        string? error = RunFfmpeg(ffmpeg, full, sound ? wav : null, temp);
                        if (error != null && sound) { error = RunFfmpeg(ffmpeg, full, null, temp); sound = false; }
                        if (error != null) throw new InvalidDataException($"ffmpeg could not convert {Path.GetFileName(full)}: {error}");
                        File.Move(temp, target, overwrite: true);
                        if (!sound) File.WriteAllText(target + ".silent", "");
                    }
                    finally
                    {
                        foreach (string f in new[] { temp, wav }) try { File.Delete(f); } catch (IOException) { }
                    }
                }
            }
            return new { url = $"/api/viewer/movie/{Uri.EscapeDataString(name)}", size = new FileInfo(target).Length, silent = File.Exists(target + ".silent") };
        }

        private static string? RunFfmpeg(string ffmpeg, string input, string? wav, string output)
        {
            var psi = new ProcessStartInfo(ffmpeg) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (string a in new[] { "-y", "-hide_banner", "-loglevel", "error", "-i", input }) psi.ArgumentList.Add(a);
            // Not -shortest: some movies' sound runs on after the picture ends (M51_EXT: 34 s of video, 59 s of sound).
            if (wav != null) foreach (string a in new[] { "-i", wav, "-map", "0:v:0", "-map", "1:a:0", "-c:a", "aac", "-b:a", "192k" }) psi.ArgumentList.Add(a);
            else foreach (string a in new[] { "-map", "0:v:0", "-an" }) psi.ArgumentList.Add(a);
            foreach (string a in new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p", "-movflags", "+faststart", output }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start");
            var stderr = p.StandardError.ReadToEndAsync();
            p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(TimeSpan.FromMinutes(10))) { p.Kill(true); return "took longer than 10 minutes"; }
            string err = stderr.Result.Trim();
            return p.ExitCode == 0 && File.Exists(output) && new FileInfo(output).Length > 0 ? null : (err.Length > 0 ? err.Split('\n').Last() : $"exit code {p.ExitCode}");
        }
    }
}
