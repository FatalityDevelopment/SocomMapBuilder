using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeDesigner.Ps2.FileFormats.Zipper.ZAR;
using CodeDesigner.Ps2.FileFormats.Zipper.ZDB;
using CodeDesigner.Ps2.FileFormats.Zipper.MPS;
using CodeDesigner.Ps2.FileFormats.Zipper.RDR;
using CodeDesigner.Ps2.FileFormats.Zipper.ZED;

namespace MapBuilder.Server.Services
{
    /// <summary>
    /// Asset Viewer commands for ZDB / ZAR / ZED containers: inspect, read nodes, extract, RDR / MPS dumps and
    /// map-package preloading.
    /// </summary>
    internal sealed class ZipperFileFormatsService : ICommandService
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        private static readonly object PreloadGate = new();
        private static readonly Dictionary<string, PreloadedBundle> PreloadByToken = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> TokenByFingerprint = new(StringComparer.OrdinalIgnoreCase);
        private const long MaxBundleBytes = 1536L * 1024 * 1024;
        private const int MaxBundleCount = 3;
        private static readonly TimeSpan PreloadTtl = TimeSpan.FromMinutes(10);

        public void Register(CommandRegistry reg)
        {
            reg.Add(c => HandleInspect(c.InputPath), "ZIPPER_INSPECT", "FILEFORMATS_INSPECT");
            reg.Add(c => HandleExtractAll(c.InputPath, c.OutputDirectory), "ZIPPER_EXTRACT_ALL");
            reg.Add(c => HandleExtractEntry(c.InputPath, c.ZipperEntryIndex, c.OutputPath), "ZIPPER_EXTRACT_ENTRY");
            reg.Add(c => HandleReadNode(c.InputPath, c.ZipperEntryIndex), "ZIPPER_READ_NODE");
            reg.Add(c => HandleReadAllNodes(c.InputPath), "ZIPPER_READ_ALL_NODES");
            reg.Add(c => HandleInspectRdr(c.InputPath, c.ZipperSection), "ZIPPER_INSPECT_RDR");
            reg.Add(c => HandleInspectRdrBytes(c.Data, c.ZipperSection), "ZIPPER_INSPECT_RDR_BYTES");
            reg.Add(c => HandleInspectMps(c.InputPath), "ZIPPER_INSPECT_MPS");
            reg.Add(c => HandlePreloadMapPackage(c.InputPath), "ZIPPER_PRELOAD_MAP_PACKAGE");
            reg.Add(c => HandleGetPreloadedResource(c.ZipperPreloadToken ?? c.ZipperSection, c.Query), "ZIPPER_GET_PRELOADED_RESOURCE");
            reg.Add(c => HandleReleasePreload(c.ZipperPreloadToken ?? c.ZipperSection), "ZIPPER_RELEASE_PRELOAD");
        }

        private string HandleInspect(string? inputPath)
        {
            string? pathError = ValidateInputFile(inputPath, out string filePath);
            if (pathError != null) return Responses.Error(pathError);

            try
            {
                object result = Inspect(filePath);
                return Responses.Json(new CommandResponse
                {
                    Success = true,
                    Command = "ZIPPER_INSPECT",
                    Data = JsonSerializer.Serialize(result, JsonOptions),
                });
            }
            catch (Exception ex)
            {
                return Responses.Error($"ZIPPER_INSPECT error: {ex.Message}");
            }
        }

        private string HandleExtractAll(string? inputPath, string? outputDirectory)
        {
            string? pathError = ValidateInputFile(inputPath, out string filePath);
            if (pathError != null) return Responses.Error(pathError);
            if (string.IsNullOrWhiteSpace(outputDirectory)) return Responses.Error("OutputDirectory is required");

            try
            {
                Directory.CreateDirectory(outputDirectory);
                string ext = ArchiveExtension(filePath);
                List<string> written = ext switch
                {
                    ".zdb" => ExtractZdb(filePath, outputDirectory),
                    ".zar" => ExtractZar(filePath, outputDirectory),
                    ".zed" => ExtractZed(filePath, outputDirectory),
                    _ => throw new InvalidOperationException("This file type does not expose archive extraction."),
                };

                return OperationResult("ZIPPER_EXTRACT_ALL", $"{written.Count} item(s) extracted.", new
                {
                    outputDirectory = Path.GetFullPath(outputDirectory),
                    writtenFiles = written,
                    count = written.Count,
                });
            }
            catch (Exception ex)
            {
                return Responses.Error($"ZIPPER_EXTRACT_ALL error: {ex.Message}");
            }
        }

        private string HandleExtractEntry(string? inputPath, int? entryIndex, string? outputPath)
        {
            string? pathError = ValidateInputFile(inputPath, out string filePath);
            if (pathError != null) return Responses.Error(pathError);
            if (entryIndex is null || entryIndex < 0) return Responses.Error("ZipperEntryIndex is required");
            if (string.IsNullOrWhiteSpace(outputPath)) return Responses.Error("OutputPath is required");

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? ".");
                string ext = ArchiveExtension(filePath);
                int index = entryIndex.Value;

                switch (ext)
                {
                    case ".zdb":
                        using (ZdbArchive archive = ZdbArchive.Open(filePath))
                        {
                            ZdbEntry entry = archive.GetEntry(index);
                            File.WriteAllBytes(outputPath, entry.Data);
                        }
                        break;
                    case ".zar":
                        using (ZarArchive archive = ZarArchive.Open(filePath))
                        {
                            ZedNode entry = archive.GetEntry(index);
                            File.WriteAllBytes(outputPath, entry.Data);
                        }
                        break;
                    case ".zed":
                        using (ZedStructure structure = ZedStructure.Open(filePath))
                        {
                            ZedNode node = structure.GetNode(index);
                            File.WriteAllBytes(outputPath, node.Data);
                        }
                        break;
                    default:
                        throw new InvalidOperationException("This file type does not expose entry extraction.");
                }

                return OperationResult("ZIPPER_EXTRACT_ENTRY", $"Extracted entry {index}.", new
                {
                    outputPath = Path.GetFullPath(outputPath),
                    writtenFiles = new[] { Path.GetFullPath(outputPath) },
                    count = 1,
                });
            }
            catch (Exception ex)
            {
                return Responses.Error($"ZIPPER_EXTRACT_ENTRY error: {ex.Message}");
            }
        }

        private string HandleReadNode(string? inputPath, int? entryIndex)
        {
            string? pathError = ValidateInputFile(inputPath, out string filePath);
            if (pathError != null) return Responses.Error(pathError);
            if (entryIndex is null || entryIndex < 0) return Responses.Error("ZipperEntryIndex is required");

            try
            {
                byte[] payload = ReadNodeBytes(filePath, entryIndex.Value);
                return Responses.Json(new CommandResponse
                {
                    Success = true,
                    Command = "ZIPPER_READ_NODE",
                    Data = Convert.ToBase64String(payload),
                });
            }
            catch (Exception ex)
            {
                return Responses.Error($"ZIPPER_READ_NODE error: {ex.Message}");
            }
        }

        private string HandleReadAllNodes(string? inputPath)
        {
            string? pathError = ValidateInputFile(inputPath, out string filePath);
            if (pathError != null) return Responses.Error(pathError);

            try
            {
                var nodes = ReadAllNodes(filePath);
                return Responses.Json(new CommandResponse
                {
                    Success = true,
                    Command = "ZIPPER_READ_ALL_NODES",
                    Data = JsonSerializer.Serialize(nodes, JsonOptions),
                });
            }
            catch (Exception ex)
            {
                return Responses.Error($"ZIPPER_READ_ALL_NODES error: {ex.Message}");
            }
        }

        private static byte[] ReadNodeBytes(string filePath, int index)
        {
            string ext = ArchiveExtension(filePath);
            switch (ext)
            {
                case ".zdb":
                    using (ZdbArchive archive = ZdbArchive.Open(filePath))
                        return (byte[])archive.GetEntry(index).Data.Clone();
                case ".zar":
                    using (ZarArchive archive = ZarArchive.Open(filePath))
                        return (byte[])archive.GetEntry(index).Data.Clone();
                case ".zed":
                    using (ZedStructure structure = ZedStructure.Open(filePath))
                        return (byte[])structure.GetNode(index).Data.Clone();
                default:
                    throw new InvalidOperationException("This file type does not expose node reads.");
            }
        }

        /// <summary>
        /// Parse a Zipper *.rdr typed-table file. Accepts either a standalone
        /// .rdr path, or a path to a ZAR / ZED / ZDB containing the .rdr as an
        /// entry. When the container has multiple .rdr entries, an explicit
        /// entry name can be supplied via ZipperSection.
        /// </summary>
        private string HandleInspectRdr(string? inputPath, string? entryName)
        {
            string? pathError = ValidateInputFile(inputPath, out string filePath);
            if (pathError != null) return Responses.Error(pathError);

            byte[] rdrBytes;
            try
            {
                rdrBytes = ResolveRdrBytes(filePath, entryName);
            }
            catch (Exception ex)
            {
                return Responses.Error($"ZIPPER_INSPECT_RDR error: {ex.Message}");
            }
            return BuildRdrResponse(rdrBytes, Path.GetFullPath(filePath), entryName);
        }

        /// <summary>
        /// Parse an .rdr from base64 bytes the webview already has in memory
        /// (e.g. an .rdr nested inside a ZAR we've already parsed client-side).
        /// Avoids the brittle ZDB drill-down path and works for any container
        /// arrangement.
        /// </summary>
        private string HandleInspectRdrBytes(string? base64, string? entryName)
        {
            if (string.IsNullOrEmpty(base64)) return Responses.Error("Data is required");
            byte[] rdrBytes;
            try { rdrBytes = Convert.FromBase64String(base64); }
            catch (Exception ex) { return Responses.Error($"ZIPPER_INSPECT_RDR_BYTES base64 error: {ex.Message}"); }
            return BuildRdrResponse(rdrBytes, "(in-memory)", entryName);
        }

        // Common path used by both the file-based and bytes-based RDR handlers.
        // Tries the strict typed-table parse first; on failure falls back to
        // the HTML reference's printable-string scan with categorised refs.
        private string BuildRdrResponse(byte[] rdrBytes, string sourcePath, string? entryName)
        {
            try
            {
                var rdr = RdrFile.Parse(rdrBytes);
                var tree = rdr.BuildTree();
                var shaped = RdrShape.Shape(tree);

                var result = new
                {
                    sourcePath,
                    entry = entryName,
                    version = rdr.Version,
                    stringPoolSize = rdr.StringPool.Length,
                    atomCount = rdr.Atoms.Length,
                    root = shaped,
                    fallback = false,
                };
                return Responses.Json(new CommandResponse
                {
                    Success = true,
                    Command = "ZIPPER_INSPECT_RDR",
                    Data = JsonSerializer.Serialize(result, JsonOptions),
                });
            }
            catch (Exception parseEx)
            {
                var strings = ExtractRdrStrings(rdrBytes);
                var textureRefs = new List<string>();
                var fileRefs = new List<string>();
                var modelRefs = new List<string>();
                foreach (var s in strings)
                {
                    string lower = s.ToLowerInvariant();
                    if (lower.EndsWith(".tif")) textureRefs.Add(s);
                    else if (lower.EndsWith(".zed") || lower.EndsWith(".zar") || lower.EndsWith(".mps") || lower.EndsWith(".rdr")) fileRefs.Add(s);
                    else if (!lower.Contains('.') && s.Length >= 3) modelRefs.Add(s);
                }

                var fallbackResult = new
                {
                    sourcePath,
                    entry = entryName,
                    version = 0u,
                    stringPoolSize = rdrBytes.Length,
                    atomCount = 0,
                    fallback = true,
                    parseError = parseEx.Message,
                    root = new
                    {
                        strings,
                        textureRefs,
                        fileRefs,
                        modelRefs,
                    },
                };
                return Responses.Json(new CommandResponse
                {
                    Success = true,
                    Command = "ZIPPER_INSPECT_RDR",
                    Data = JsonSerializer.Serialize(fallbackResult, JsonOptions),
                });
            }
        }

        // Mirrors zed_map_viewer_solution.html extractReaderStrings(): emit
        // every printable-ASCII run >= 3 chars that contains at least one
        // letter or underscore. Deduplicated + case-insensitive sorted.
        private static List<string> ExtractRdrStrings(byte[] data)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sb = new StringBuilder();
            void Flush()
            {
                if (sb.Length >= 3)
                {
                    string s = sb.ToString().Trim();
                    if (s.Length >= 3)
                    {
                        bool hasLetterOrUnderscore = false;
                        foreach (char c in s)
                        {
                            if (char.IsLetter(c) || c == '_') { hasLetterOrUnderscore = true; break; }
                        }
                        if (hasLetterOrUnderscore) seen.Add(s);
                    }
                }
                sb.Clear();
            }
            for (int i = 0; i < data.Length; i++)
            {
                byte b = data[i];
                if (b >= 0x20 && b < 0x7f) sb.Append((char)b);
                else Flush();
            }
            Flush();
            var list = seen.ToList();
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }

        /// <summary>
        /// Parse a Zipper '.mps' AI map file. Mirrors the HTML map viewer's
        /// parseAiMaps and returns a JSON-shaped manifest the webview renders
        /// as a table (name, dimensions, bounds, scale per map).
        /// </summary>
        private string HandleInspectMps(string? inputPath)
        {
            string? pathError = ValidateInputFile(inputPath, out string filePath);
            if (pathError != null) return Responses.Error(pathError);

            try
            {
                byte[] bytes = File.ReadAllBytes(filePath);
                MpsFile mps = MpsFile.Parse(bytes);

                var maps = new List<object>(mps.Maps.Count);
                foreach (var m in mps.Maps)
                {
                    maps.Add(new
                    {
                        offset = m.Offset,
                        name = m.Name,
                        width = m.Width,
                        height = m.Height,
                        minX = m.MinX,
                        minY = m.MinY,
                        minZ = m.MinZ,
                        maxX = m.MaxX,
                        maxY = m.MaxY,
                        maxZ = m.MaxZ,
                        scaleX = m.ScaleX,
                        scaleZ = m.ScaleZ,
                    });
                }

                var result = new
                {
                    sourcePath = Path.GetFullPath(filePath),
                    declaredCount = mps.MapCount,
                    foundCount = mps.Maps.Count,
                    fileSize = bytes.Length,
                    maps,
                };
                return Responses.Json(new CommandResponse
                {
                    Success = true,
                    Command = "ZIPPER_INSPECT_MPS",
                    Data = JsonSerializer.Serialize(result, JsonOptions),
                });
            }
            catch (Exception ex)
            {
                return Responses.Error($"ZIPPER_INSPECT_MPS error: {ex.Message}");
            }
        }

        private static byte[] ResolveRdrBytes(string filePath, string? entryName)
        {
            string ext = ArchiveExtension(filePath);
            switch (ext)
            {
                case ".rdr":
                    return File.ReadAllBytes(filePath);

                case ".zar":
                {
                    using var archive = ZarArchive.Open(filePath);
                    ZedNode? match = SelectRdrNode(archive.Entries, entryName);
                    if (match == null) throw new InvalidDataException("No .rdr entry found in ZAR.");
                    return match.Data;
                }

                case ".zed":
                {
                    using var structure = ZedStructure.Open(filePath);
                    ZedNode? match = SelectRdrNode(structure.Nodes, entryName);
                    if (match == null) throw new InvalidDataException("No .rdr entry found in ZED.");
                    return match.Data;
                }

                case ".zdb":
                {
                    using var archive = ZdbArchive.Open(filePath);
                    // Allow either an explicit entry name, or auto-pick the first
                    // entry that ends with .rdr. ZDB entries can also be ZAR
                    // archives containing .rdr - drill into those too.
                    var directMatch = SelectFirstZdbEntry(archive.Entries, e =>
                        e.Name.EndsWith(".rdr", StringComparison.OrdinalIgnoreCase) &&
                        (entryName == null || e.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase)
                            || EndsWithLeaf(e.Name, entryName)));
                    if (directMatch != null) return (byte[])directMatch.Data.Clone();

                    // Scan EVERY .zar entry in the ZDB for a matching .rdr. The
                    // previous version required the .zar's name to contain the
                    // .rdr name, which never matched real packages where a
                    // generic ZAR like READERM.ZAR holds the .rdr files.
                    foreach (var zarEntry in archive.Entries)
                    {
                        if (!zarEntry.Name.EndsWith(".zar", StringComparison.OrdinalIgnoreCase)) continue;
                        try
                        {
                            using var zar = ZarArchive.FromBytes(zarEntry.Data);
                            var innerMatch = SelectRdrNode(zar.Entries, entryName);
                            if (innerMatch != null) return innerMatch.Data;
                        }
                        catch { /* skip non-ZAR-shaped entries */ }
                    }
                    throw new InvalidDataException(
                        entryName == null
                            ? "No .rdr entry resolvable inside ZDB."
                            : $"No .rdr entry '{entryName}' found inside any ZAR within the ZDB.");
                }

                default:
                    throw new InvalidOperationException($"Unsupported container extension '{ext}' for RDR inspection.");
            }
        }

        private static ZedNode? SelectRdrNode(IReadOnlyList<ZedNode> nodes, string? entryName)
        {
            if (!string.IsNullOrWhiteSpace(entryName))
            {
                foreach (var n in nodes)
                {
                    if (n.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase)) return n;
                    if (EndsWithLeaf(n.Name, entryName)) return n;
                }
            }
            foreach (var n in nodes)
            {
                if (n.Name.EndsWith(".rdr", StringComparison.OrdinalIgnoreCase) && n.Data.Length > 0)
                    return n;
            }
            return null;
        }

        private static ZdbEntry? SelectFirstZdbEntry(IReadOnlyList<ZdbEntry> entries, Func<ZdbEntry, bool> match)
        {
            foreach (var e in entries) { if (match(e)) return e; }
            return null;
        }

        private static bool EndsWithLeaf(string path, string leaf)
        {
            string[] parts = path.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 0 && parts[^1].Equals(leaf, StringComparison.OrdinalIgnoreCase);
        }

        private static bool ContainsTokenIgnoreCase(string s, string token)
        {
            return s.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static IReadOnlyList<object> ReadAllNodes(string filePath)
        {
            string ext = ArchiveExtension(filePath);
            var flat = new List<(string Name, uint Type, byte[] Data)>();
            switch (ext)
            {
                case ".zdb":
                    using (ZdbArchive archive = ZdbArchive.Open(filePath))
                        CollectNodesFromZdbEntries(archive.Entries, 0, flat);
                    break;
                case ".zar":
                    using (ZarArchive archive = ZarArchive.Open(filePath))
                        CollectNodesFromZedNodes(archive.Entries, 0, flat);
                    break;
                case ".zed":
                    using (ZedStructure structure = ZedStructure.Open(filePath))
                        CollectNodesFromZedNodes(structure.Nodes, 0, flat);
                    break;
                default:
                    throw new InvalidOperationException("This file type does not expose bulk node reads.");
            }

            return flat.Select((n, i) => (object)new { index = i, name = n.Name, type = n.Type, data = Convert.ToBase64String(n.Data) }).ToList();
        }

        private static void CollectNodesFromZdbEntries(IReadOnlyList<ZdbEntry> entries, int depth, List<(string Name, uint Type, byte[] Data)> output)
        {
            if (depth > 6) return;
            foreach (ZdbEntry entry in entries)
            {
                output.Add((entry.FullPath, 0u, entry.Data));
                if (entry.Name.EndsWith(".zar", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using ZarArchive nested = ZarArchive.FromBytes(entry.Data);
                        CollectNodesFromZedNodes(nested.Entries, depth + 1, output);
                    }
                    catch (InvalidDataException) { /* keep raw entry, skip invalid nested archive */ }
                }
                else if (entry.Name.EndsWith(".zed", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using ZedStructure nested = ZedStructure.FromBytes(entry.Data);
                        CollectNodesFromZedNodes(nested.Nodes, depth + 1, output);
                    }
                    catch (InvalidDataException) { /* keep raw entry, skip invalid nested archive */ }
                }
            }
        }

        private static void CollectNodesFromZedNodes(IReadOnlyList<ZedNode> nodes, int depth, List<(string Name, uint Type, byte[] Data)> output)
        {
            if (depth > 6) return;
            foreach (ZedNode node in nodes)
            {
                output.Add((node.Name, node.Type, node.Data));
                if (node.Name.EndsWith(".zar", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using ZarArchive nested = ZarArchive.FromBytes(node.Data);
                        CollectNodesFromZedNodes(nested.Entries, depth + 1, output);
                    }
                    catch (InvalidDataException) { /* keep raw node, skip invalid nested archive */ }
                }
                else if (node.Name.EndsWith(".zed", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using ZedStructure nested = ZedStructure.FromBytes(node.Data);
                        CollectNodesFromZedNodes(nested.Nodes, depth + 1, output);
                    }
                    catch (InvalidDataException) { /* keep raw node, skip invalid nested archive */ }
                }
                else if (node.Name.EndsWith(".zdb", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using ZdbArchive nested = ZdbArchive.FromBytes(node.Data);
                        CollectNodesFromZdbEntries(nested.Entries, depth + 1, output);
                    }
                    catch (InvalidDataException) { /* keep raw node, skip invalid nested archive */ }
                }
            }
        }

        private string HandlePreloadMapPackage(string? inputPath)
        {
            string? pathError = ValidateInputFile(inputPath, out string filePath);
            if (pathError != null) return Responses.Error(pathError);
            string ext = ArchiveExtension(filePath);
            if (ext != ".zdb" && ext != ".zar" && ext != ".zed") return Responses.Error("InputPath must be a .zdb, .zar, or .zed map package.");

            try
            {
                string fingerprint = BuildFingerprint(filePath);
                lock (PreloadGate)
                {
                    PruneExpiredBundles();
                    if (TokenByFingerprint.TryGetValue(fingerprint, out string existingToken) && PreloadByToken.TryGetValue(existingToken, out PreloadedBundle existing))
                    {
                        existing.Touch();
                        return OperationResult("ZIPPER_PRELOAD_MAP_PACKAGE", "Using cached preloaded map package.", new { preloadToken = existing.Token, resourceCount = existing.Resources.Count, totalBytes = existing.TotalBytes, cacheHit = true });
                    }
                }

                var bundle = BuildBundle(filePath);
                lock (PreloadGate)
                {
                    PreloadByToken[bundle.Token] = bundle;
                    TokenByFingerprint[fingerprint] = bundle.Token;
                    EvictBundlesIfNeeded();
                }

                return OperationResult("ZIPPER_PRELOAD_MAP_PACKAGE", "Preloaded map package resources.", new { preloadToken = bundle.Token, resourceCount = bundle.Resources.Count, totalBytes = bundle.TotalBytes, cacheHit = false });
            }
            catch (Exception ex)
            {
                return Responses.Error($"ZIPPER_PRELOAD_MAP_PACKAGE error: {ex.Message}");
            }
        }

        private string HandleGetPreloadedResource(string? preloadToken, string? query)
        {
            if (string.IsNullOrWhiteSpace(preloadToken)) return Responses.Error("ZipperPreloadToken is required");
            if (string.IsNullOrWhiteSpace(query)) return Responses.Error("Query is required");
            lock (PreloadGate)
            {
                PruneExpiredBundles();
                if (!PreloadByToken.TryGetValue(preloadToken, out PreloadedBundle? bundle)) return Responses.Error("Preload token not found");
                bundle.Touch();
                if (bundle.TryGet(query, out BundledResource? resource))
                {
                    return Responses.Json(new CommandResponse { Success = true, Command = "ZIPPER_GET_PRELOADED_RESOURCE", Data = JsonSerializer.Serialize(new { key = resource.Key, source = resource.SourcePath, bytes = resource.Data.Length, data = Convert.ToBase64String(resource.Data) }, JsonOptions) });
                }
            }

            return Responses.Error($"Resource not found for query '{query}'");
        }

        private string HandleReleasePreload(string? preloadToken)
        {
            if (string.IsNullOrWhiteSpace(preloadToken)) return Responses.Error("ZipperPreloadToken is required");
            lock (PreloadGate)
            {
                PruneExpiredBundles();
                if (!PreloadByToken.Remove(preloadToken, out PreloadedBundle? removed)) return Responses.Error("Preload token not found");
                foreach (var pair in TokenByFingerprint.Where(p => p.Value.Equals(preloadToken, StringComparison.OrdinalIgnoreCase)).ToList()) TokenByFingerprint.Remove(pair.Key);
                return OperationResult("ZIPPER_RELEASE_PRELOAD", "Preloaded bundle released.", new { preloadToken = removed.Token, resourceCount = removed.Resources.Count, totalBytes = removed.TotalBytes });
            }
        }

        private static object Inspect(string filePath)
        {
            string ext = ArchiveExtension(filePath);
            return ext switch
            {
                ".zdb" => InspectZdb(filePath),
                ".zar" => InspectZar(filePath),
                ".zed" => InspectZed(filePath),
                _ => throw new InvalidDataException("Unsupported file. Supported files: .zdb, .cdb (memory-card packs), .zar and .zed."),
            };
        }

        private static object InspectZdb(string filePath)
        {
            using ZdbArchive archive = ZdbArchive.Open(filePath);
            bool hasFtScore = archive.Entries.Any(e => string.Equals(e.Name, "ftscore", StringComparison.OrdinalIgnoreCase));
            bool hasZSealEtc = archive.Entries.Any(e => string.Equals(e.Name, "zsealetc", StringComparison.OrdinalIgnoreCase));
            bool isApache = hasFtScore || hasZSealEtc || string.Equals(Path.GetFileName(filePath), "APACHE00.ZDB", StringComparison.OrdinalIgnoreCase);

            var entries = archive.Entries.Select((entry, index) => new
            {
                index,
                name = entry.Name,
                path = entry.FullPath,
                size = (long)entry.Size,
                kind = "file",
                extension = Path.GetExtension(entry.Name),
            }).ToList();

            return new
            {
                format = isApache ? "APACHE.ZDB" : filePath.EndsWith(".cdb", StringComparison.OrdinalIgnoreCase) ? "CDB pack" : "ZDB",
                role = isApache ? "apache-patch-archive" : "archive",
                path = Path.GetFullPath(filePath),
                fileName = Path.GetFileName(filePath),
                size = new FileInfo(filePath).Length,
                summary = isApache
                    ? $"APACHE00 ZDB with {archive.EntryCount} section(s)"
                    : $"ZDB type {archive.ArchiveType} archive with {archive.EntryCount} file(s)",
                canExtract = true,
                entries,
                apache = isApache ? new { hasFtScore, hasZSealEtc } : null,
            };
        }

        private static object InspectZar(string filePath)
        {
            using ZarArchive archive = ZarArchive.Open(filePath);
            var entries = archive.Entries.Select((entry, index) => new
            {
                index,
                name = entry.Name,
                path = entry.Name,
                size = (long)entry.Size,
                kind = "file",
                type = entry.Type,
                extension = Path.GetExtension(entry.Name),
            }).ToList();

            return new
            {
                format = "ZAR",
                role = "archive",
                path = Path.GetFullPath(filePath),
                fileName = Path.GetFileName(filePath),
                size = new FileInfo(filePath).Length,
                summary = $"ZAR archive with {archive.EntryCount} entr{(archive.EntryCount == 1 ? "y" : "ies")}",
                canExtract = true,
                entries,
                zed = new
                {
                    rootValue = archive.RootValue,
                    stringBasePointer = archive.StringBasePointer,
                    headerWord5C = archive.HeaderWord5C,
                    dataAlignment = archive.DataAlignment,
                },
            };
        }

        private static object InspectZed(string filePath)
        {
            using ZedStructure structure = ZedStructure.Open(filePath);
            var nodes = structure.Nodes.Select((node, index) => new
            {
                index,
                name = node.Name,
                path = node.Name,
                size = (long)node.Size,
                kind = node.Size == 0 ? "node" : "payload",
                type = node.Type,
                extension = Path.GetExtension(node.Name),
            }).ToList();

            return new
            {
                format = "ZED",
                role = "structured-container",
                path = Path.GetFullPath(filePath),
                fileName = Path.GetFileName(filePath),
                size = new FileInfo(filePath).Length,
                summary = $"ZED structure with {structure.NodeCount} node(s)",
                canExtract = true,
                entries = nodes,
                zed = new
                {
                    rootValue = structure.RootValue,
                    stringBasePointer = structure.StringBasePointer,
                    headerWord5C = structure.HeaderWord5C,
                    dataAlignment = structure.DataAlignment,
                },
            };
        }

        private static List<string> ExtractZdb(string filePath, string outputDirectory)
        {
            using ZdbArchive archive = ZdbArchive.Open(filePath);
            var written = new List<string>();
            for (int i = 0; i < archive.EntryCount; i++)
            {
                ZdbEntry entry = archive.GetEntry(i);
                string dest = SafeOutputPath(outputDirectory, entry.FullPath, $"{i:X4}_{entry.Name}");
                Directory.CreateDirectory(Path.GetDirectoryName(dest) ?? outputDirectory);
                File.WriteAllBytes(dest, entry.Data);
                written.Add(Path.GetFullPath(dest));
            }
            return written;
        }

        private static List<string> ExtractZar(string filePath, string outputDirectory)
        {
            using ZarArchive archive = ZarArchive.Open(filePath);
            return ExtractZedNodes(archive.Entries, outputDirectory);
        }

        private static List<string> ExtractZed(string filePath, string outputDirectory)
        {
            using ZedStructure structure = ZedStructure.Open(filePath);
            return ExtractZedNodes(structure.Nodes, outputDirectory);
        }

        private static List<string> ExtractZedNodes(IReadOnlyList<ZedNode> nodes, string outputDirectory)
        {
            var written = new List<string>();
            for (int i = 0; i < nodes.Count; i++)
            {
                ZedNode node = nodes[i];
                string dest = SafeOutputPath(outputDirectory, node.Name, $"{i:X4}_{node.Name}");
                if (File.Exists(dest))
                {
                    string dir = Path.GetDirectoryName(dest) ?? outputDirectory;
                    string stem = Path.GetFileNameWithoutExtension(dest);
                    string ext = Path.GetExtension(dest);
                    dest = Path.Combine(dir, $"{stem}_{i:X4}{ext}");
                }
                Directory.CreateDirectory(Path.GetDirectoryName(dest) ?? outputDirectory);
                File.WriteAllBytes(dest, node.Data);
                written.Add(Path.GetFullPath(dest));
            }
            return written;
        }

        private static string SafeOutputPath(string rootDirectory, string archivePath, string fallbackName)
        {
            string root = Path.GetFullPath(rootDirectory);
            string raw = string.IsNullOrWhiteSpace(archivePath) ? fallbackName : archivePath;
            string[] parts = raw.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);

            string current = root;
            foreach (string rawPart in parts.Length == 0 ? new[] { fallbackName } : parts)
            {
                string part = rawPart.Trim();
                if (part.Length == 0 || part == ".") continue;
                if (part == "..")
                    throw new InvalidDataException($"Unsafe archive path segment in '{archivePath}'.");
                if (part.EndsWith(":", StringComparison.Ordinal))
                    continue;

                foreach (char invalid in Path.GetInvalidFileNameChars())
                    part = part.Replace(invalid, '_');
                if (part.Length == 0) part = "_";
                current = Path.Combine(current, part);
            }

            string full = Path.GetFullPath(current);
            string rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
                ? root
                : root + Path.DirectorySeparatorChar;
            if (!full.Equals(root, StringComparison.OrdinalIgnoreCase) &&
                !full.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Archive entry escapes output directory: {archivePath}");

            return full;
        }

        private static string BuildFingerprint(string filePath)
        {
            FileInfo info = new(filePath);
            var materialParts = new List<string>
            {
                $"{Path.GetFullPath(filePath)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}"
            };
            foreach (string sidecarPath in ResolveExternalResourceArchives(filePath))
            {
                FileInfo sidecar = new(sidecarPath);
                materialParts.Add($"{Path.GetFullPath(sidecarPath)}|{sidecar.Length}|{sidecar.LastWriteTimeUtc.Ticks}");
            }

            string material = string.Join("||", materialParts);
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
            return Convert.ToHexString(hash);
        }

        private static PreloadedBundle BuildBundle(string filePath)
        {
            string token = Guid.NewGuid().ToString("N");
            var resources = new Dictionary<string, BundledResource>(StringComparer.OrdinalIgnoreCase);
            var basename = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            long totalBytes = 0;

            string ext = ArchiveExtension(filePath);
            if (ext == ".zdb")
            {
                using ZdbArchive zdb = ZdbArchive.Open(filePath);
                TraverseZdb(zdb.Entries, filePath, 0, resources, basename, ref totalBytes);
            }
            else if (ext == ".zar")
            {
                using ZarArchive zar = ZarArchive.Open(filePath);
                TraverseZedNodes(zar.Entries, filePath, 0, resources, basename, ref totalBytes);
            }
            else
            {
                using ZedStructure zed = ZedStructure.Open(filePath);
                TraverseZedNodes(zed.Nodes, filePath, 0, resources, basename, ref totalBytes);
            }

            foreach (string sidecarPath in ResolveExternalResourceArchives(filePath))
            {
                using ZarArchive sidecar = ZarArchive.Open(sidecarPath);
                TraverseZedNodes(sidecar.Entries, sidecarPath, 0, resources, basename, ref totalBytes);
            }

            return new PreloadedBundle(token, resources, basename, totalBytes);
        }

        private static IEnumerable<string> ResolveExternalResourceArchives(string mapPackagePath)
        {
            if (string.IsNullOrWhiteSpace(mapPackagePath)) yield break;
            string? packageDirectory = Path.GetDirectoryName(Path.GetFullPath(mapPackagePath));
            if (string.IsNullOrWhiteSpace(packageDirectory)) yield break;

            string[] candidates =
            {
                Path.Combine(packageDirectory, "ZWEAPON.zar"),
                Path.Combine(packageDirectory, "run", "ZWEAPON.zar"),
                Path.Combine(packageDirectory, "..", "run", "ZWEAPON.zar"),
            };

            var yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string candidate in candidates)
            {
                string fullPath = Path.GetFullPath(candidate);
                if (!yielded.Add(fullPath)) continue;
                if (!File.Exists(fullPath)) continue;
                yield return fullPath;
            }
        }

        private static void TraverseZdb(IReadOnlyList<ZdbEntry> entries, string source, int depth, Dictionary<string, BundledResource> resources, Dictionary<string, string> basename, ref long totalBytes)
        {
            if (depth > 6) return;
            foreach (var entry in entries)
            {
                AddResource(entry.FullPath, entry.Data, source, resources, basename, ref totalBytes);
                if (entry.Name.EndsWith(".zar", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using ZarArchive nested = ZarArchive.FromBytes(entry.Data);
                        TraverseZedNodes(nested.Entries, $"{source}:{entry.FullPath}", depth + 1, resources, basename, ref totalBytes);
                    }
                    catch (InvalidDataException) { /* keep raw resource, skip invalid nested archive */ }
                }
                else if (entry.Name.EndsWith(".zed", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using ZedStructure nested = ZedStructure.FromBytes(entry.Data);
                        TraverseZedNodes(nested.Nodes, $"{source}:{entry.FullPath}", depth + 1, resources, basename, ref totalBytes);
                    }
                    catch (InvalidDataException) { /* keep raw resource, skip invalid nested archive */ }
                }
            }
        }

        private static void TraverseZedNodes(IReadOnlyList<ZedNode> nodes, string source, int depth, Dictionary<string, BundledResource> resources, Dictionary<string, string> basename, ref long totalBytes)
        {
            if (depth > 6) return;
            foreach (var node in nodes)
            {
                AddResource(node.Name, node.Data, source, resources, basename, ref totalBytes);

                if (node.Name.EndsWith(".zar", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using ZarArchive nested = ZarArchive.FromBytes(node.Data);
                        TraverseZedNodes(nested.Entries, $"{source}:{node.Name}", depth + 1, resources, basename, ref totalBytes);
                    }
                    catch (InvalidDataException) { /* keep raw resource, skip invalid nested archive */ }
                }
                else if (node.Name.EndsWith(".zed", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using ZedStructure nested = ZedStructure.FromBytes(node.Data);
                        TraverseZedNodes(nested.Nodes, $"{source}:{node.Name}", depth + 1, resources, basename, ref totalBytes);
                    }
                    catch (InvalidDataException) { /* keep raw resource, skip invalid nested archive */ }
                }
                else if (node.Name.EndsWith(".zdb", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using ZdbArchive nested = ZdbArchive.FromBytes(node.Data);
                        TraverseZdb(nested.Entries, $"{source}:{node.Name}", depth + 1, resources, basename, ref totalBytes);
                    }
                    catch (InvalidDataException) { /* keep raw resource, skip invalid nested archive */ }
                }
            }
        }

        private static void AddResource(string path, byte[] data, string source, Dictionary<string, BundledResource> resources, Dictionary<string, string> basename, ref long totalBytes)
        {
            if (data.Length == 0) return;
            string key = path.Replace('\\','/').TrimStart('/');
            bool isCommon = key.Contains("/common/", StringComparison.OrdinalIgnoreCase) || key.StartsWith("common/", StringComparison.OrdinalIgnoreCase);
            bool shouldReplace = !resources.ContainsKey(key) || (resources[key].IsCommon && !isCommon);
            if (shouldReplace)
            {
                if (resources.TryGetValue(key, out var prev)) totalBytes -= prev.Data.Length;
                var r = new BundledResource(key, source, isCommon, (byte[])data.Clone());
                resources[key] = r;
                totalBytes += r.Data.Length;
                basename[Path.GetFileName(key)] = key;
                if (totalBytes > MaxBundleBytes) throw new InvalidDataException($"Preload exceeded max bundle bytes: {MaxBundleBytes}");
            }
        }

        private static void PruneExpiredBundles()
        {
            DateTime utcNow = DateTime.UtcNow;
            foreach (PreloadedBundle bundle in PreloadByToken.Values.Where(v => utcNow - v.LastAccessUtc > PreloadTtl).ToList())
            {
                PreloadByToken.Remove(bundle.Token);
                foreach (var pair in TokenByFingerprint.Where(p => p.Value.Equals(bundle.Token, StringComparison.OrdinalIgnoreCase)).ToList())
                    TokenByFingerprint.Remove(pair.Key);
            }
        }

        private static void EvictBundlesIfNeeded()
        {
            while (PreloadByToken.Count > MaxBundleCount)
            {
                var oldest = PreloadByToken.Values.OrderBy(v => v.LastAccessUtc).First();
                PreloadByToken.Remove(oldest.Token);
                foreach (var pair in TokenByFingerprint.Where(p => p.Value.Equals(oldest.Token, StringComparison.OrdinalIgnoreCase)).ToList()) TokenByFingerprint.Remove(pair.Key);
            }
        }

        private sealed class BundledResource
        {
            public BundledResource(string key, string sourcePath, bool isCommon, byte[] data) { Key = key; SourcePath = sourcePath; IsCommon = isCommon; Data = data; }
            public string Key { get; }
            public string SourcePath { get; }
            public bool IsCommon { get; }
            public byte[] Data { get; }
        }

        private sealed class PreloadedBundle
        {
            public PreloadedBundle(string token, Dictionary<string, BundledResource> resources, Dictionary<string, string> basenameIndex, long totalBytes)
            {
                Token = token; Resources = resources; BasenameIndex = basenameIndex; TotalBytes = totalBytes; LastAccessUtc = DateTime.UtcNow;
            }
            public string Token { get; }
            public Dictionary<string, BundledResource> Resources { get; }
            public Dictionary<string, string> BasenameIndex { get; }
            public long TotalBytes { get; }
            public DateTime LastAccessUtc { get; private set; }
            public void Touch() => LastAccessUtc = DateTime.UtcNow;
            public bool TryGet(string query, out BundledResource resource)
            {
                string key = query.Replace('\\','/').TrimStart('/');
                if (Resources.TryGetValue(key, out resource!)) return true;
                if (BasenameIndex.TryGetValue(Path.GetFileName(key), out string? resolved) && Resources.TryGetValue(resolved, out resource!)) return true;
                resource = null!;
                return false;
            }
        }

        private static string? ValidateInputFile(string? inputPath, out string filePath)
        {
            filePath = string.Empty;
            if (string.IsNullOrWhiteSpace(inputPath)) return "InputPath is required";
            filePath = Path.GetFullPath(inputPath);
            return File.Exists(filePath) ? null : $"File not found: {filePath}";
        }

        private static string OperationResult(string command, string message, object data)
            => Responses.Json(new CommandResponse
            {
                Success = true,
                Command = command,
                Message = message,
                Data = JsonSerializer.Serialize(data, JsonOptions),
            });

        /// <summary>
        /// The lower-case extension, with archives read by their layout: memory-card packs (.cdb) and .zar files that
        /// are ZDB archives rather than ZED containers (SOCOM: Combined Assault's RUN/&lt;hash&gt;.ZAR: type-2 header 0xFC
        /// holding nested ZDB v6 files) are read as .zdb.
        /// </summary>
        private static string ArchiveExtension(string filePath)
        {
            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            if (ext == ".cdb") return ".zdb";
            if (ext == ".zar" && File.Exists(filePath))
            {
                try
                {
                    using var f = File.OpenRead(filePath);
                    var head = new byte[4];
                    if (f.Read(head, 0, 4) == 4 && BitConverter.ToUInt32(head, 0) is 0xFC or 0xC8 or 0x110) return ".zdb";
                }
                catch (IOException) { /* fall back to the extension */ }
            }
            return ext;
        }
    }
}
