using System.Text.Json;
using CodeDesigner.Ps2.FileFormats.Iso;

namespace MapBuilder.Server.Services;

/// <summary>
/// ASSET_SAVE to a disc image, for every game: when <c>outputPath</c> ends in .ISO, the session's own save runs into a
/// scratch folder beside the output, each file it writes is matched to its place on the source disc image, and
/// <see cref="IsoPatcher"/> writes a copy of the image with those files replaced. The source image is the session's
/// own (a disc opened as an .ISO) or <c>sourceIso</c> (for a package opened from a drive or folder). A file name found
/// in more than one place on the disc is resolved from the session's own path, or by <c>discPaths</c> {file: path}.
/// </summary>
internal sealed partial class ZipperAssetService
{
    private sealed record DiscSaveArgs(string? OutputPath, string? SourceIso, string? Mode, string? Archive, string? Model, Dictionary<string, string>? DiscPaths);

    private static readonly JsonSerializerOptions LooseJson = new() { PropertyNameCaseInsensitive = true };

    /// <summary>The source path of any game's session (the package file or folder, or the disc image).</summary>
    private static string? SessionSourcePath(string id) =>
        Find(id) is { } z ? (z.OpenPath.Length > 0 ? z.OpenPath : z.Package.SourcePath)
        : CaSessions.TryGetValue(id, out var ca) ? ca.Package.SourcePath
        : Assets.GtaAssetService.SourcePathOf(id) ?? Assets.SwbfAssetService.SourcePathOf(id) ?? Assets.Ftb3AssetService.SourcePathOf(id);

    /// <summary>Answers ASSET_SAVE when it asks for a disc image; null otherwise (the game's own save runs).</summary>
    private static string? DiscSave(CommandMessage c, Func<CommandMessage, string> save)
    {
        DiscSaveArgs? args;
        try { args = string.IsNullOrWhiteSpace(c.AssetJson) ? null : JsonSerializer.Deserialize<DiscSaveArgs>(c.AssetJson, LooseJson); }
        catch (JsonException) { return null; }
        if (args?.OutputPath is not { } output || !output.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)) return null;
        return Responses.Run("ASSET_SAVE", () =>
        {
            if (string.Equals(args.Mode, "pack", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("A memory-card pack is not written into a disc image.");
            string id = c.AssetSession ?? throw new ArgumentException("A session is required");
            string sessionPath = SessionSourcePath(id) ?? throw new KeyNotFoundException($"No asset session '{id}'");
            string source = args.SourceIso is { Length: > 0 } given ? Path.GetFullPath(given)
                : sessionPath.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) ? sessionPath
                : throw new ArgumentException("Choose the disc image (.ISO) this package came from (sourceIso).");
            if (!File.Exists(source)) throw new FileNotFoundException($"{source} was not found");
            string target = Path.GetFullPath(output);

            // The game's own save, into a scratch folder on the output's drive (an IMG can be hundreds of MB).
            string scratch = Path.Combine(Path.GetDirectoryName(target)!, ".mapbuilder-disc-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(scratch);
            try
            {
                string inner = Find(id) is { Package.IsZdb: true } z ? Path.Combine(scratch, Path.GetFileName(z.Package.SourcePath))
                    : args.Mode?.Equals("img", StringComparison.OrdinalIgnoreCase) == true && args.Archive is { } a ? Path.Combine(scratch, Path.GetFileName(a.Replace('/', '\\')))
                    : args.Mode?.Equals("dff", StringComparison.OrdinalIgnoreCase) == true && args.Model is { } m ? Path.Combine(scratch, m + ".dff")
                    : scratch;
                using var doc = JsonDocument.Parse(c.AssetJson!);
                var forward = new Dictionary<string, object?>();
                foreach (var p in doc.RootElement.EnumerateObject())
                    if (p.Name is not ("outputPath" or "sourceIso" or "discPaths")) forward[p.Name] = p.Value.Clone();
                forward["outputPath"] = inner;
                var reply = JsonSerializer.Deserialize<CommandResponse>(save(new CommandMessage
                {
                    Command = "ASSET_SAVE", AssetSession = id, AssetJson = JsonSerializer.Serialize(forward),
                }), LooseJson)!;
                if (!reply.Success) throw new InvalidOperationException(reply.Error ?? "The save failed");
                using var data = JsonDocument.Parse(reply.Data ?? "{}");
                var written = data.RootElement.GetProperty("written").EnumerateArray().Select(w => w.GetString()!).ToList();
                object? leaves = data.RootElement.TryGetProperty("leaves", out var lv) ? lv.Clone() : null;
                object? revision = data.RootElement.TryGetProperty("revision", out var rv) ? rv.Clone() : null;

                var entries = IsoPatcher.Entries(source);
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string file in written)
                    map[DiscPathOf(file, scratch, sessionPath, entries, args.DiscPaths)] = file;
                var result = IsoPatcher.Patch(source, target, map);
                return new
                {
                    written = new[] { result.Path }, leaves, revision, sourceIso = source,
                    patched = result.Files.Select(f => new { discPath = f.DiscPath, moved = f.Moved }).ToList(),
                    warnings = result.Warnings,
                };
            }
            finally
            {
                try { Directory.Delete(scratch, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        });
    }

    /// <summary>Where a saved file goes on the disc: <paramref name="overrides"/> by file name, else the one entry
    /// whose path ends with the file's path under the scratch folder, else (several match) the one that is the
    /// session's own path on its drive.</summary>
    internal static string DiscPathOf(string file, string scratch, string sessionPath, IReadOnlyDictionary<string, IsoPatcher.Entry> entries,
        IReadOnlyDictionary<string, string>? overrides)
    {
        string name = Path.GetFileName(file);
        if (overrides != null && overrides.TryGetValue(name, out var forced)) return forced.Replace('\\', '/').Trim('/').ToUpperInvariant();
        string rel = Path.GetRelativePath(scratch, file).Replace('\\', '/').ToUpperInvariant();
        var hits = entries.Keys.Where(k => k.Equals(rel, StringComparison.OrdinalIgnoreCase) || k.EndsWith("/" + rel, StringComparison.OrdinalIgnoreCase)).ToList();
        if (hits.Count == 1) return hits[0];
        if (hits.Count == 0) throw new KeyNotFoundException($"{rel} is not on the disc image; pick the image it came from, or name its place with discPaths.");
        // A package opened from the disc's drive: its path under the drive root.
        string full = Path.GetFullPath(sessionPath);
        string root = Path.GetPathRoot(full) ?? "";
        string underRoot = full[root.Length..].Replace('\\', '/').ToUpperInvariant();
        string dir = Path.GetDirectoryName(underRoot.Replace('/', Path.DirectorySeparatorChar))?.Replace('\\', '/') ?? "";
        var mine = hits.Where(h => h.Equals(underRoot, StringComparison.OrdinalIgnoreCase)
            || (dir.Length > 0 && h.StartsWith(dir + "/", StringComparison.OrdinalIgnoreCase))).ToList();
        if (mine.Count == 1) return mine[0];
        throw new ArgumentException($"{rel} is in {hits.Count} places on the disc ({string.Join(", ", hits.Take(6))}); name the one to replace with discPaths {{\"{name}\": \"...\"}}.");
    }
}
