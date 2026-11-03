using System.Text;
using CodeDesigner.Ps2.FileFormats.Imaging;
using System.Text.Json;
using CodeDesigner.Ps2.FileFormats.Ps2;
using CodeDesigner.Ps2.FileFormats.Zipper.Anim;
using CodeDesigner.Ps2.FileFormats.Zipper.Geo;
using CodeDesigner.Ps2.FileFormats.Zipper.MPS;
using CodeDesigner.Ps2.FileFormats.Zipper.RDR;
using CodeDesigner.Ps2.FileFormats.Zipper.ZDB;
using CodeDesigner.Ps2.FileFormats.Zipper.ZED;

namespace MapBuilder.Server.Services
{
    /// <summary>
    /// Asset Viewer edits (VIEWER_EDIT): RDR values and list items, AI map headers, text, bytes, screens, icon
    /// textures and icon.sys titles, on any file or record. An edit goes through the package's asset session (a
    /// .ZDB's, or a loose file's folder's), so it has the editors' undo / redo, unsaved-changes count and saving
    /// (ASSET_UNDO / ASSET_REDO / ASSET_SAVE); a record inside archives is changed and its archives rebuilt around it.
    /// </summary>
    internal sealed partial class ZipperAssetService
    {
        private sealed record ViewerEditRequest(
            int[]? Inner, string? Op, int? Atom, string? Value, int? Map, string? Name, float[]? Min, float[]? Max, float[]? Scale,
            string? Text, int? Offset, string? Hex, string? PngBase64, string? Title1, string? Title2, string[]? Icons,
            int? Node, float[]? Matrix, int? Kind, uint? Flags, string? Field, JsonElement? FieldValue, int? Word, uint? WordValue);

        private static void RegisterViewerEdits(CommandRegistry reg)
        {
            reg.Add(c => Responses.Run("VIEWER_EDIT", () => ViewerEdit(c.InputPath, c.AssetJson)), "VIEWER_EDIT");
        }

        // The package a file belongs to for editing: a .ZDB itself, a loose file's folder.
        private static string EditPackageOf(string full) => ZdbArchive.IsArchivePath(full) ? full : Path.GetDirectoryName(full)!;

        /// <summary>The session holding edits to <paramref name="full"/>'s package, without opening one; null when
        /// there is none or it has no edits (the disk is then as current).</summary>
        private static AssetSession? EditedSessionOf(string full)
        {
            string key = EditPackageOf(full);
            var s = Sessions.Values.FirstOrDefault(x => x.OpenPath.Equals(key, StringComparison.OrdinalIgnoreCase));
            return s != null && (s.Dirty.Count > 0 || s.Undo.Count > 0 || s.Redo.Count > 0) ? s : null;
        }

        /// <summary>Whether a loose file can be edited: its folder's session would hold it.</summary>
        private static bool LooseEditable(string full)
        {
            var info = new FileInfo(full);
            return info.Exists && info.Length < 512L * 1024 * 1024 && !ZdbArchive.IsArchivePath(full) && !full.EndsWith(".pss", StringComparison.OrdinalIgnoreCase);
        }

        private static object ViewerEdit(string? path, string? json)
        {
            var req = Parse<ViewerEditRequest>(json) ?? throw new ArgumentException("AssetJson with an \"op\" is required");
            string full = FullPathOf(path);
            if (Directory.Exists(full)) throw new ArgumentException("Pick a file to edit, not a folder");
            bool zdb = ZdbArchive.IsArchivePath(full);
            int[] inner = req.Inner ?? Array.Empty<int>();
            if (zdb && inner.Length == 0) throw new ArgumentException("Pick a file inside the package to edit");
            if (!zdb && !LooseEditable(full)) throw new InvalidOperationException($"{Path.GetFileName(full)} cannot be edited here (movies and files over 512 MB are not)");

            var place = Resolve(full, inner);
            string leaf = zdb ? ZdbNodes(full)[inner[0]].Name : Path.GetFileName(full);
            int[] rest = zdb ? inner[1..] : inner;
            var s = ViewerSession(EditPackageOf(full));
            lock (s.Gate)
            {
                var entry = s.Package.Entry(leaf) ?? throw new KeyNotFoundException($"{leaf} is not in the session's package");
                object? result = null;
                string description = $"{Describe(req)} {place.Name}";
                var edit = ApplyEdit(s, description, _ => new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
                {
                    [leaf] = Rebuild(entry.Data, rest, data => ApplyViewerOp(req, data, out result)),
                });
                return new { session = s.Id, package = s.OpenPath, edit, result, status = StatusObject(s) };
            }
        }

        private static string Describe(ViewerEditRequest r) => r.Op switch
        {
            "rdrSet" => "Set a value in",
            "rdrDuplicate" => "Duplicate an item in",
            "rdrRemove" => "Remove an item from",
            "mpsMap" => "Change an AI map in",
            "text" => "Edit",
            "bytes" => "Patch bytes in",
            "image" => "Replace the picture of",
            "iconSys" => "Change the title of",
            "nparams" => "Change a scene node in",
            "modelName" => "Change a model reference in",
            "zanimField" or "zanimWord" => "Change an effect in",
            _ => "Edit",
        };

        // A record inside archives: change it, then splice it into each archive around it (every other byte kept).
        private static byte[] Rebuild(byte[] data, int[] path, Func<byte[], byte[]> change)
        {
            if (path.Length == 0) return change(data);
            var nodes = ZedContainer.FromBytes(data).Nodes;
            if (path[0] < 0 || path[0] >= nodes.Count) throw new KeyNotFoundException($"No record #{path[0]}");
            return ZedSplice.Replace(data, path[0], Rebuild(nodes[path[0]].Data, path[1..], change));
        }

        private static byte[] ApplyViewerOp(ViewerEditRequest r, byte[] data, out object? result)
        {
            result = null;
            switch (r.Op)
            {
                case "rdrSet":
                {
                    var doc = RdrDocument.Parse(data);
                    doc.SetFromText(r.Atom ?? throw new ArgumentException("\"atom\" is required"), r.Value ?? throw new ArgumentException("\"value\" is required"));
                    return doc.ToBytes();
                }
                case "rdrDuplicate":
                {
                    var doc = RdrDocument.Parse(data);
                    result = doc.Duplicate(r.Atom ?? throw new ArgumentException("\"atom\" is required"));
                    return doc.ToBytes();
                }
                case "rdrRemove":
                {
                    var doc = RdrDocument.Parse(data);
                    doc.Remove(r.Atom ?? throw new ArgumentException("\"atom\" is required"));
                    return doc.ToBytes();
                }
                case "mpsMap":
                    return MpsFile.WithMap(data, r.Map ?? throw new ArgumentException("\"map\" is required"), r.Name, r.Min, r.Max, r.Scale);
                case "text":
                {
                    string text = r.Text ?? throw new ArgumentException("\"text\" is required");
                    if (text.Any(c => c > 0x7E || (c < 0x20 && c is not '\r' and not '\n' and not '\t'))) throw new ArgumentException("Text files here are plain ASCII.");
                    return Encoding.ASCII.GetBytes(text);
                }
                case "bytes":
                {
                    int offset = r.Offset ?? throw new ArgumentException("\"offset\" is required");
                    byte[] patch = Convert.FromHexString(new string((r.Hex ?? "").Where(Uri.IsHexDigit).ToArray()));
                    if (patch.Length == 0) throw new ArgumentException("\"hex\" needs at least one byte");
                    if (offset < 0 || offset + patch.Length > data.Length) throw new ArgumentException($"The patch runs past the end ({data.Length} bytes)");
                    var output = (byte[])data.Clone();
                    patch.CopyTo(output, offset);
                    return output;
                }
                case "image":
                {
                    byte[] rgba = PngDecoder.DecodeRgba(Convert.FromBase64String(r.PngBase64 ?? throw new ArgumentException("\"pngBase64\" is required")), out int w, out int h);
                    return Ps2Icon.IsIcon(data) ? Ps2Icon.WithTexture(data, rgba, w, h) : RawScreen.WithRgba(data, rgba, w, h);
                }
                case "iconSys":
                    return Ps2Icon.WithIconSys(data, r.Title1 ?? "", r.Title2 ?? "", r.Icons ?? Array.Empty<string>());
                case "nparams":
                    return GeoEdits.WithNodeParams(data, r.Node ?? throw new ArgumentException("\"node\" (the nparams record) is required"), r.Matrix, r.Min, r.Max, r.Kind, r.Flags);
                case "modelName":
                    return GeoEdits.WithModelName(data, r.Node ?? throw new ArgumentException("\"node\" (the model_name record) is required"), r.Name ?? throw new ArgumentException("\"name\" is required"));
                case "zanimField":
                case "zanimWord":
                {
                    // {node: the animation's Seq_Data record, offset: the instruction, field + fieldValue | word + wordValue}
                    int node = r.Node ?? throw new ArgumentException("\"node\" (the Seq_Data record) is required");
                    int offset = r.Offset ?? throw new ArgumentException("\"offset\" (the instruction) is required");
                    var nodes = ZedContainer.FromBytes(data).Nodes;
                    if (node < 0 || node >= nodes.Count || nodes[node].Name != "Seq_Data") throw new ArgumentException($"Record #{node} is not a Seq_Data");
                    byte[] seq = r.Op == "zanimWord"
                        ? ZanimFile.WithWord(nodes[node].Data, offset, r.Word ?? throw new ArgumentException("\"word\" is required"), r.WordValue ?? throw new ArgumentException("\"wordValue\" is required"))
                        : ZanimFile.WithField(nodes[node].Data, offset, r.Field ?? throw new ArgumentException("\"field\" is required"), FieldValueOf(r.FieldValue));
                    return ZedSplice.Replace(data, node, seq);
                }
                default:
                    throw new ArgumentException($"Unknown op '{r.Op}' (rdrSet, rdrDuplicate, rdrRemove, mpsMap, text, bytes, image, iconSys, nparams, modelName, zanimField, zanimWord)");
            }
        }

        // A ZANIM field value from JSON: a number, three numbers, or true / false.
        private static object FieldValueOf(JsonElement? v) => v?.ValueKind switch
        {
            JsonValueKind.Number => v.Value.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Array => v.Value.EnumerateArray().Select(x => x.GetDouble()).ToArray(),
            _ => throw new ArgumentException("\"fieldValue\" is a number, three numbers or true / false"),
        };
    }
}
