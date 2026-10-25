using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using CodeDesigner.Ps2.FileFormats.Imaging;
using CodeDesigner.Ps2.FileFormats.Zipper.Model;
using CodeDesigner.Ps2.FileFormats.Zipper.Texture;
using CodeDesigner.Ps2.FileFormats.Zipper.ZED;

namespace MapBuilder.Server.Services
{
    /// <summary>
    /// Bridge commands for the AI-driven weapon generation workflow:
    /// enumerate weapon templates inside a ZED archive, fetch a template's
    /// structured DTO, render a template's texture as a PNG thumbnail, and
    /// write a generated weapon back as new sibling entries.
    /// </summary>
    internal sealed class ZipperWeaponsService : ICommandService
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        // Short explainer string handed to the AI alongside every template
        // payload so the model knows how to read what it's looking at.
        private const string FormatNotes =
            "Zipper PS2 model (.mdl) chunks: 0x50-byte header (vertexCount@+0x30, " +
            "triangleCount@+0x34, scale@+0x44), then vertex records (int16 X/Y/Z/W + " +
            "8 trailing bytes), an 8-byte triangle-command gap, triangle records " +
            "(4 bytes: a, b, c, flags — indices packed as VU slot offsets, real " +
            "index = packed / 3), then an ASCII .tif texture-name tail. Texture (.tif): " +
            "0x20 header (width, height @ +0x00/+0x02; paletteId @ +0x08; formatA @ +0x0c), " +
            "then either indexed8 bytes or RGBA32 (alpha 0..128), then 0x80 footer.";

        public void Register(CommandRegistry reg)
        {
            reg.Add(c => HandleListWeapons(c.InputPath), "ZIPPER_LIST_WEAPONS");
            reg.Add(c => HandleGetWeaponTemplate(c.InputPath, c.WeaponName), "ZIPPER_GET_WEAPON_TEMPLATE");
            reg.Add(c => HandleWeaponTexturePng(c.InputPath, c.WeaponName), "ZIPPER_WEAPON_TEXTURE_PNG");
            reg.Add(c => HandleEncodeWeaponTexture(
                c.InputPath, c.WeaponName, c.WeaponImagePath, c.WeaponImageData), "ZIPPER_ENCODE_WEAPON_TEXTURE");
            reg.Add(c => HandleEncodeWeaponModel(
                c.InputPath, c.WeaponName, c.WeaponMeshEditsJson), "ZIPPER_ENCODE_WEAPON_MODEL");
            reg.Add(c => HandleWriteWeapon(
                c.InputPath, c.OutputPath, c.WeaponNewName,
                c.WeaponModelData, c.WeaponTextureData, c.WeaponPaletteData,
                c.WeaponTextureEntryName, c.WeaponPaletteEntryName), "ZIPPER_WRITE_WEAPON");
        }

        // -------------------------------------------------------------------
        // ZIPPER_LIST_WEAPONS
        // -------------------------------------------------------------------

        private string HandleListWeapons(string? inputPath)
        {
            string? err = RequireFile(inputPath, out string path);
            if (err != null) return Responses.Error(err);

            try
            {
                var weapons = new List<object>();
                using var zed = ZedContainer.Open(path);

                // ZED archives can legitimately contain duplicate node names
                // (GEO child stubs like "N000_000" repeat per parent), so we
                // walk by index rather than building a name→node dictionary.
                for (int i = 0; i < zed.Nodes.Count; i++)
                {
                    var node = zed.Nodes[i];
                    if (!ZedModelFile.LooksLikeModel(node.Data)) continue;

                    var model = ParseForInspection(zed, i);
                    int totalVerts = model.Chunks.Sum(ch => ch.VertexCount);
                    int totalTris = model.Chunks.Sum(ch => ch.TriangleCount);

                    var textureNames = model.Chunks
                        .Select(ch => ch.TextureName)
                        .Where(n => n != null)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    weapons.Add(new
                    {
                        name = node.Name,
                        entryIndex = i,
                        chunks = model.Chunks.Count,
                        vertices = totalVerts,
                        triangles = totalTris,
                        textureEntries = textureNames,
                        sizeBytes = node.Data.Length,
                        chainBound = model.FromChains,
                        shapeHint = $"{model.Chunks.Count} chunks, {totalVerts} verts, " +
                                    $"{totalTris} tris" +
                                    (textureNames.Count > 0
                                        ? $", textures: {string.Join(", ", textureNames!)}"
                                        : ""),
                    });
                }

                return Responses.Json(new CommandResponse
                {
                    Success = true,
                    Command = "ZIPPER_LIST_WEAPONS",
                    Data = JsonSerializer.Serialize(new { weapons }, JsonOptions),
                });
            }
            catch (Exception ex)
            {
                return Responses.Error($"ZIPPER_LIST_WEAPONS error: {ex.Message}");
            }
        }

        // -------------------------------------------------------------------
        // ZIPPER_GET_WEAPON_TEMPLATE
        // -------------------------------------------------------------------

        private string HandleGetWeaponTemplate(string? inputPath, string? weaponName)
        {
            string? err = RequireFile(inputPath, out string path);
            if (err != null) return Responses.Error(err);
            if (string.IsNullOrWhiteSpace(weaponName))
                return Responses.Error("WeaponName is required.");

            try
            {
                using var zed = ZedContainer.Open(path);
                var node = zed.FindNode(weaponName);
                if (node == null) return Responses.Error($"Weapon '{weaponName}' not found.");

                if (!ZedModelFile.LooksLikeModel(node.Data))
                    return Responses.Error($"Entry '{weaponName}' does not look like a model.");

                var model = ParseForInspection(zed, zed.Nodes.ToList().IndexOf(node));

                // Try to resolve the first texture binding.
                object? textureMeta = null;
                string? firstTifName = model.Chunks
                    .Select(c => c.TextureName)
                    .FirstOrDefault(n => !string.IsNullOrEmpty(n));

                if (firstTifName != null)
                {
                    var tifNode = zed.FindNode(firstTifName);
                    if (tifNode != null && tifNode.Data.Length >= ZedTextureFile.HeaderSize)
                    {
                        var header = ZedTextureFile.ParseHeader(tifNode.Data);
                        textureMeta = new
                        {
                            entryName = firstTifName,
                            width = header.Width,
                            height = header.Height,
                            bpp = header.Bpp,
                            isIndexed = header.IsIndexed,
                            // The palette the game binds (TEX0.CBP -> texpal_<id>),
                            // falling back to the header slot for old records.
                            paletteId = header.ResolvePaletteId(),
                            textureIndex = header.TextureIndex,
                            psm = $"0x{header.Psm:X2}",
                            cbp = header.Cbp,
                            wrapS = header.Gs == null ? null : (header.Gs.ClampS ? "clamp" : "repeat"),
                            wrapT = header.Gs == null ? null : (header.Gs.ClampT ? "clamp" : "repeat"),
                            alphaTest = header.Gs?.AlphaTest,
                            alphaBlend = header.Gs?.AlphaBlend,
                            formatA = $"0x{header.FormatA:X4}",
                            sizeBytes = tifNode.Data.Length,
                        };
                    }
                }

                var dto = new
                {
                    name = node.Name,
                    sizeBytes = node.Data.Length,
                    model = new
                    {
                        chunks = model.Chunks.Select((c, idx) => new
                        {
                            index = idx,
                            offsetInPayload = c.Offset,
                            vertexCount = c.VertexCount,
                            triangleCount = c.TriangleCount,
                            scale = c.Scale,
                            textureName = c.TextureName,
                            textureBinding = c.TextureBinding,
                            visual = c.VisualKey,
                            hasVertexColors = c.HasVertexColors,
                            vertexBoundsXyz = ComputeBounds(c.Vertices),
                        }).ToList(),
                        allTextureRefs = model.TextureRefs,
                    },
                    texture = textureMeta,
                    formatNotes = FormatNotes,
                };

                return Responses.Json(new CommandResponse
                {
                    Success = true,
                    Command = "ZIPPER_GET_WEAPON_TEMPLATE",
                    Data = JsonSerializer.Serialize(dto, JsonOptions),
                });
            }
            catch (Exception ex)
            {
                return Responses.Error($"ZIPPER_GET_WEAPON_TEMPLATE error: {ex.Message}");
            }
        }

        // -------------------------------------------------------------------
        // ZIPPER_WEAPON_TEXTURE_PNG — returns the template's bound .tif as a
        // PNG. Lets the AI visually compare templates against a user image
        // without us needing a 3D renderer in C#.
        // -------------------------------------------------------------------

        private string HandleWeaponTexturePng(string? inputPath, string? weaponName)
        {
            string? err = RequireFile(inputPath, out string path);
            if (err != null) return Responses.Error(err);
            if (string.IsNullOrWhiteSpace(weaponName))
                return Responses.Error("WeaponName is required.");

            try
            {
                using var zed = ZedContainer.Open(path);
                var node = zed.FindNode(weaponName);
                if (node == null) return Responses.Error($"Weapon '{weaponName}' not found.");

                ZedNode? tifNode = null;
                if (ZedModelFile.LooksLikeModel(node.Data))
                {
                    var model = ParseForInspection(zed, zed.Nodes.ToList().IndexOf(node));
                    string? firstTifName = model.Chunks
                        .Select(c => c.TextureName)
                        .FirstOrDefault(n => !string.IsNullOrEmpty(n))
                        ?? model.TextureRefs.FirstOrDefault();
                    if (firstTifName != null)
                        tifNode = zed.FindNode(firstTifName);
                }
                else if (node.Data.Length >= ZedTextureFile.HeaderSize)
                {
                    tifNode = node; // entry is itself a texture
                }

                if (tifNode == null)
                    return Responses.Error($"No texture entry resolved for '{weaponName}'.");

                // Look for a sibling palette. We try a few common conventions:
                // an entry named "texpal_N" matching paletteId, or the next
                // "buf" entry after the .tif (PAL.ZED triplet layout).
                var header = ZedTextureFile.ParseHeader(tifNode.Data);
                ZedTextureFile.Palette? palette = header.IsIndexed
                    ? TryResolvePalette(zed, header.ResolvePaletteId())
                    : null;

                var decoded = ZedTextureFile.Parse(tifNode.Data, palette);
                byte[] png = PngEncoder.EncodeRgba(decoded.Rgba, header.Width, header.Height);

                return Responses.Json(new CommandResponse
                {
                    Success = true,
                    Command = "ZIPPER_WEAPON_TEXTURE_PNG",
                    Data = JsonSerializer.Serialize(new
                    {
                        weapon = weaponName,
                        textureEntry = tifNode.Name,
                        width = header.Width,
                        height = header.Height,
                        paletteResolved = decoded.PaletteResolved,
                        png = Convert.ToBase64String(png),
                    }, JsonOptions),
                });
            }
            catch (Exception ex)
            {
                return Responses.Error($"ZIPPER_WEAPON_TEXTURE_PNG error: {ex.Message}");
            }
        }

        // Decode a model for inspection: through its N* child chains when the
        // node has them (exact texture per section, vertex colours), else the
        // legacy chunk scan. Editing/encoding paths keep the legacy parse.
        private static ZedModelFile.Model ParseForInspection(ZedContainer zed, int nodeIndex)
        {
            var node = zed.Nodes[nodeIndex];
            var chainOffsets = ZedModelFile.CollectChainOffsets(zed.Nodes, nodeIndex);
            return chainOffsets.Count > 0
                ? ZedModelFile.Parse(node.Data, chainOffsets)
                : ZedModelFile.Parse(node.Data);
        }

        private static ZedTextureFile.Palette? TryResolvePalette(ZedContainer zed, int paletteId)
        {
            // Try by texpal_NNN naming first.
            var direct = zed.FindNode($"texpal_{paletteId}");
            if (direct != null && direct.Data.Length >= 512)
            {
                // texpal_NNN is the marker entry (often empty); the actual
                // CLUT bytes live in the next "buf" sibling.
                int idx = zed.Nodes.ToList().IndexOf(direct);
                for (int j = idx + 1; j < Math.Min(idx + 4, zed.Nodes.Count); j++)
                {
                    if (string.Equals(zed.Nodes[j].Name, "buf", StringComparison.OrdinalIgnoreCase)
                        && zed.Nodes[j].Data.Length >= 512)
                    {
                        try { return ZedTextureFile.DecodeClutBuffer(zed.Nodes[j].Data, paletteId); }
                        catch { return null; }
                    }
                }
                if (direct.Data.Length >= 512)
                {
                    try { return ZedTextureFile.DecodeClutBuffer(direct.Data, paletteId); }
                    catch { return null; }
                }
            }
            return null;
        }

        // -------------------------------------------------------------------
        // ZIPPER_ENCODE_WEAPON_TEXTURE — produces a TIM-format texture from a
        // user image (PNG path or inline base64), resampled into the template
        // weapon's TIM dimensions and bpp.
        // -------------------------------------------------------------------

        private string HandleEncodeWeaponTexture(
            string? archivePath, string? weaponName,
            string? imagePath, string? imageDataB64)
        {
            string? err = RequireFile(archivePath, out string path);
            if (err != null) return Responses.Error(err);
            if (string.IsNullOrWhiteSpace(weaponName))
                return Responses.Error("WeaponName is required.");
            if (string.IsNullOrWhiteSpace(imagePath) && string.IsNullOrWhiteSpace(imageDataB64))
                return Responses.Error("One of WeaponImagePath or WeaponImageData is required.");

            try
            {
                byte[] pngBytes;
                if (!string.IsNullOrWhiteSpace(imagePath))
                {
                    if (!File.Exists(imagePath))
                        return Responses.Error($"Image file does not exist: {imagePath}");
                    pngBytes = File.ReadAllBytes(imagePath);
                }
                else
                {
                    pngBytes = Convert.FromBase64String(imageDataB64!);
                }

                byte[] rgba = PngDecoder.DecodeRgba(pngBytes, out int srcW, out int srcH);

                using var zed = ZedContainer.Open(path);
                var modelNode = zed.FindNode(weaponName);
                if (modelNode == null) return Responses.Error($"Weapon '{weaponName}' not found.");
                if (!ZedModelFile.LooksLikeModel(modelNode.Data))
                    return Responses.Error($"Entry '{weaponName}' does not look like a model.");

                var model = ZedModelFile.Parse(modelNode.Data);
                string? tifName = model.Chunks
                    .Select(c => c.TextureName)
                    .FirstOrDefault(n => !string.IsNullOrEmpty(n));
                if (tifName == null) return Responses.Error($"Weapon '{weaponName}' has no texture binding.");

                var tifNode = zed.FindNode(tifName);
                if (tifNode == null) return Responses.Error($"Template texture '{tifName}' not present in archive.");

                var header = ZedTextureFile.ParseHeader(tifNode.Data);
                byte[] encoded = ZedTextureFile.ResampleToTemplate(
                    rgba, srcW, srcH, header, paletteRgba: null, out byte[]? generatedPalette);

                return Responses.Json(new CommandResponse
                {
                    Success = true,
                    Command = "ZIPPER_ENCODE_WEAPON_TEXTURE",
                    Data = JsonSerializer.Serialize(new
                    {
                        templateTextureEntry = tifName,
                        width = header.Width,
                        height = header.Height,
                        bpp = header.Bpp,
                        isIndexed = header.IsIndexed,
                        tif = Convert.ToBase64String(encoded),
                        // Only present when the template was indexed and we
                        // generated a fresh palette via median cut.
                        palette = generatedPalette != null
                            ? Convert.ToBase64String(generatedPalette)
                            : null,
                    }, JsonOptions),
                });
            }
            catch (Exception ex)
            {
                return Responses.Error($"ZIPPER_ENCODE_WEAPON_TEXTURE error: {ex.Message}");
            }
        }

        // -------------------------------------------------------------------
        // ZIPPER_ENCODE_WEAPON_MODEL — takes a template weapon and a JSON
        // edits description (vertex transform + per-chunk texture rebind),
        // returns the re-encoded .mdl bytes.
        // -------------------------------------------------------------------

        private string HandleEncodeWeaponModel(
            string? archivePath, string? weaponName, string? editsJson)
        {
            string? err = RequireFile(archivePath, out string path);
            if (err != null) return Responses.Error(err);
            if (string.IsNullOrWhiteSpace(weaponName))
                return Responses.Error("WeaponName is required.");

            try
            {
                using var zed = ZedContainer.Open(path);
                var modelNode = zed.FindNode(weaponName);
                if (modelNode == null) return Responses.Error($"Weapon '{weaponName}' not found.");

                var model = ZedModelFile.Parse(modelNode.Data);
                List<ZedModelFile.ChunkEdit>? edits = null;
                if (!string.IsNullOrWhiteSpace(editsJson))
                    edits = ParseEdits(editsJson!);

                byte[] encoded = ZedModelFile.Encode(model, edits);
                return Responses.Json(new CommandResponse
                {
                    Success = true,
                    Command = "ZIPPER_ENCODE_WEAPON_MODEL",
                    Data = JsonSerializer.Serialize(new
                    {
                        templateName = weaponName,
                        editsApplied = edits?.Count ?? 0,
                        bytes = encoded.Length,
                        mdl = Convert.ToBase64String(encoded),
                    }, JsonOptions),
                });
            }
            catch (Exception ex)
            {
                return Responses.Error($"ZIPPER_ENCODE_WEAPON_MODEL error: {ex.Message}");
            }
        }

        private static List<ZedModelFile.ChunkEdit> ParseEdits(string json)
        {
            var edits = new List<ZedModelFile.ChunkEdit>();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("perChunk", out var perChunk)) return edits;
            if (perChunk.ValueKind != JsonValueKind.Array) return edits;

            foreach (var item in perChunk.EnumerateArray())
            {
                var edit = new ZedModelFile.ChunkEdit
                {
                    ChunkIndex = item.GetProperty("chunkIndex").GetInt32(),
                };
                if (item.TryGetProperty("transform", out var t))
                {
                    edit.Transform = new ZedModelFile.VertexTransform
                    {
                        ScaleX = t.TryGetProperty("scaleX", out var sx) ? sx.GetSingle() : 1f,
                        ScaleY = t.TryGetProperty("scaleY", out var sy) ? sy.GetSingle() : 1f,
                        ScaleZ = t.TryGetProperty("scaleZ", out var sz) ? sz.GetSingle() : 1f,
                        TranslateX = t.TryGetProperty("translateX", out var tx) ? (short)tx.GetInt32() : (short)0,
                        TranslateY = t.TryGetProperty("translateY", out var ty) ? (short)ty.GetInt32() : (short)0,
                        TranslateZ = t.TryGetProperty("translateZ", out var tz) ? (short)tz.GetInt32() : (short)0,
                    };
                }
                if (item.TryGetProperty("newTextureName", out var ntn))
                    edit.NewTextureName = ntn.GetString();
                edits.Add(edit);
            }
            return edits;
        }

        // -------------------------------------------------------------------
        // ZIPPER_WRITE_WEAPON — adds new sibling entries to the archive.
        // Original entries are untouched.
        // -------------------------------------------------------------------

        private string HandleWriteWeapon(
            string? inputPath, string? outputPath, string? newName,
            string? modelB64, string? textureB64, string? paletteB64,
            string? textureEntryName, string? paletteEntryName)
        {
            string? err = RequireFile(inputPath, out string path);
            if (err != null) return Responses.Error(err);
            if (string.IsNullOrWhiteSpace(newName))
                return Responses.Error("WeaponNewName is required.");
            if (string.IsNullOrWhiteSpace(modelB64) && string.IsNullOrWhiteSpace(textureB64))
                return Responses.Error("At least one of WeaponModelData or WeaponTextureData is required.");

            try
            {
                using var zed = ZedContainer.Open(path);
                var added = new List<object>();

                if (!string.IsNullOrWhiteSpace(modelB64))
                {
                    byte[] modelBytes = Convert.FromBase64String(modelB64);
                    var node = zed.AddNode(newName!, modelBytes);
                    added.Add(new { name = node.Name, kind = "model", bytes = modelBytes.Length });
                }

                if (!string.IsNullOrWhiteSpace(textureB64))
                {
                    string tifName = textureEntryName ?? DefaultTextureEntryName(newName!);
                    byte[] tifBytes = Convert.FromBase64String(textureB64);
                    var node = zed.AddNode(tifName, tifBytes);
                    added.Add(new { name = node.Name, kind = "texture", bytes = tifBytes.Length });
                }

                if (!string.IsNullOrWhiteSpace(paletteB64))
                {
                    string palName = paletteEntryName ?? DefaultPaletteEntryName(newName!);
                    byte[] palBytes = Convert.FromBase64String(paletteB64);
                    var node = zed.AddNode(palName, palBytes);
                    added.Add(new { name = node.Name, kind = "palette", bytes = palBytes.Length });
                }

                string saveTo = string.IsNullOrWhiteSpace(outputPath) ? path : outputPath!;
                zed.Save(saveTo);

                return Responses.Json(new CommandResponse
                {
                    Success = true,
                    Command = "ZIPPER_WRITE_WEAPON",
                    Data = JsonSerializer.Serialize(new
                    {
                        outputPath = Path.GetFullPath(saveTo),
                        added,
                    }, JsonOptions),
                });
            }
            catch (Exception ex)
            {
                return Responses.Error($"ZIPPER_WRITE_WEAPON error: {ex.Message}");
            }
        }

        private static string DefaultTextureEntryName(string weaponName)
        {
            string stem = StripExtension(weaponName);
            return $"{stem}.tif";
        }

        private static string DefaultPaletteEntryName(string weaponName)
        {
            string stem = StripExtension(weaponName);
            return $"{stem}_pal";
        }

        private static string StripExtension(string name)
        {
            int dot = name.LastIndexOf('.');
            return dot > 0 ? name.Substring(0, dot) : name;
        }

        // -------------------------------------------------------------------
        // Shared helpers
        // -------------------------------------------------------------------

        private static string? RequireFile(string? maybePath, out string fullPath)
        {
            if (string.IsNullOrWhiteSpace(maybePath))
            {
                fullPath = string.Empty;
                return "InputPath is required.";
            }
            fullPath = Path.GetFullPath(maybePath);
            if (!File.Exists(fullPath)) return $"Input file does not exist: {fullPath}";
            return null;
        }

        private static object ComputeBounds(IReadOnlyList<ZedModelFile.Vertex> verts)
        {
            if (verts.Count == 0)
                return new { minX = 0, minY = 0, minZ = 0, maxX = 0, maxY = 0, maxZ = 0 };
            short mnx = short.MaxValue, mny = short.MaxValue, mnz = short.MaxValue;
            short mxx = short.MinValue, mxy = short.MinValue, mxz = short.MinValue;
            foreach (var v in verts)
            {
                if (v.X < mnx) mnx = v.X; if (v.X > mxx) mxx = v.X;
                if (v.Y < mny) mny = v.Y; if (v.Y > mxy) mxy = v.Y;
                if (v.Z < mnz) mnz = v.Z; if (v.Z > mxz) mxz = v.Z;
            }
            return new { minX = mnx, minY = mny, minZ = mnz, maxX = mxx, maxY = mxy, maxZ = mxz };
        }
    }
}
