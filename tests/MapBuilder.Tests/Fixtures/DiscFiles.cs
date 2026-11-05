using CodeDesigner.Ps2.FileFormats.Gta;

namespace MapBuilder.Tests.Fixtures;

/// <summary>
/// A file of a retail disc for tests that open one loose file: under a mounted disc or folder, or (for a disc image)
/// copied once at its disc path into a cache folder (MAPBUILDER_DISC_CACHE, else the temp folder), with the other disc
/// paths it needs beside it. A folder name ending "/" copies everything under it.
/// </summary>
public static class DiscFiles
{
    private static readonly object Gate = new();

    public static string Path(string disc, string discPath, params string[] alongside)
    {
        if (!disc.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
            return System.IO.Path.Combine(new[] { disc }.Concat(discPath.Split('/', '\\')).ToArray());
        var files = Iso9660.Files(disc);
        foreach (string other in alongside)
            foreach (var key in files.Keys.Where(k => other.EndsWith('/') ? k.StartsWith(other, StringComparison.OrdinalIgnoreCase) : k.Equals(other, StringComparison.OrdinalIgnoreCase)))
                Copy(disc, files, key);
        return Copy(disc, files, discPath);
    }

    /// <summary>GTA: an IMG opened loose reads the whole disc when it sits in a disc tree (MODELS/GTA3.IMG and DATA).</summary>
    public static string Gta(string disc, string discPath) => Path(disc, discPath, "MODELS/GTA3.IMG", "DATA/");

    private static string Copy(string disc, Dictionary<string, FileSpan> files, string discPath)
    {
        lock (Gate)
        {
            string cache = Environment.GetEnvironmentVariable("MAPBUILDER_DISC_CACHE") is { Length: > 0 } c ? c : System.IO.Path.GetTempPath();
            string target = System.IO.Path.Combine(cache, $"mb-disc-{System.IO.Path.GetFileNameWithoutExtension(disc)}-{new FileInfo(disc).Length:x}",
                discPath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            if (File.Exists(target)) return target;
            var span = files.TryGetValue(discPath.Replace('\\', '/'), out var s) ? s : throw new FileNotFoundException($"{discPath} is not on {disc}");
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            using (var from = File.OpenRead(span.Path))
            using (var to = File.Create(target + ".tmp"))
            {
                from.Position = span.Offset;
                var buffer = new byte[1 << 20];
                for (long left = span.Length; left > 0;)
                {
                    int n = from.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                    if (n <= 0) throw new EndOfStreamException(discPath);
                    to.Write(buffer, 0, n);
                    left -= n;
                }
            }
            File.Move(target + ".tmp", target, true);
            return target;
        }
    }
}
