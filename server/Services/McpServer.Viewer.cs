using System.Text.Json;
using System.Text.Json.Nodes;
using MapBuilder.Server;

namespace MapBuilder.Server.Services
{
    /// <summary>
    /// The Asset Viewer's tools: browse any folder, file or archive record (viewer_list), read a typed preview of one
    /// (viewer_preview), change it in place (viewer_edit) and copy a record out (viewer_extract). The page follows:
    /// browsing moves the viewer there (with Follow AI on), an edit opens the record and marks what changed, and any
    /// edit to the package it shows reloads it. Edits land in the package's asset session: assets_undo / assets_redo /
    /// assets_save take the session id viewer_edit answers.
    /// </summary>
    internal sealed partial class McpServer
    {
        private const string ViewerPlaceHelp =
            "path is a folder, a file (a disc file, a .ZDB package, a loose .ZED / .ZAR …) and inner the entry indices that lead into it " +
            "(viewer_list answers each item's inner): [] the file itself, [5] the package's sixth file, [5, 2] a record inside that.";

        private List<Tool> BuildViewerTools()
        {
            var tools = new List<Tool>();
            void Add(string name, string label, string description, bool readOnly, Dictionary<string, object> schema, Func<JsonElement, ToolResult> run)
                => tools.Add(new Tool(name, label, description, schema, readOnly, a => Task.FromResult(run(a))));
            var path = P("path", "string", "A folder, a file, or the file whose records inner leads into.");
            var inner = A("inner", "integer", "Entry indices into the file (default []).");

            Add("viewer_list", "Browse",
                "What a folder, file or archive holds, as the Asset Viewer lists it: each item's name, kind (package, archive, models, textures, " +
                "palettes, geo, clips, rdr, mps, sound, movie, image, icon, iconsys, text, program, binary …), size, detail and inner. Package " +
                "archives list what the editors see (models, textures, palettes, clips) from an asset session. " + ViewerPlaceHelp +
                " The Asset Viewer moves there while Follow AI is on. Risk: read-only.",
                true, Schema(new[] { "path" }, path, inner, P("query", "string", "Optional name substring."), P("kind", "string", "Optional item kind."),
                    N("offset", "integer", "Skip this many items.", 0), N("limit", "integer", "At most this many items (default 200).", 1, 2000)), a =>
                {
                    string full = FullPath(a);
                    int[] at = Inner(a);
                    var (ok, data, error) = Command(new CommandMessage { Command = "VIEWER_LIST", InputPath = full, AssetJson = JsonSerializer.Serialize(new { inner = at }, Json) });
                    if (!ok || data == null) return ToolResult.Fail(error ?? "VIEWER_LIST failed");
                    var l = JsonNode.Parse(data)!.AsObject();
                    var items = l["items"]!.AsArray().Select(i => i!.AsObject()).ToList();
                    string? q = Str(a, "query"), k = Str(a, "kind");
                    var hits = items.Where(i => (q == null || ((string?)i["name"] ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)) && (k == null || (string?)i["kind"] == k)).ToList();
                    int offset = IntOpt(a, "offset") ?? 0, limit = IntOpt(a, "limit") ?? 200;
                    var page = new JsonArray(hits.Skip(offset).Take(limit).Select(i =>
                    {
                        var c = (JsonObject)i.DeepClone();
                        c.Remove("path");
                        return (JsonNode)c;
                    }).ToArray());
                    var reply = new JsonObject
                    {
                        ["path"] = l["path"]?.DeepClone(), ["inner"] = l["inner"]?.DeepClone(), ["name"] = l["name"]?.DeepClone(), ["kind"] = l["kind"]?.DeepClone(),
                        ["session"] = l["session"]?.DeepClone(), ["editable"] = l["editable"]?.DeepClone(), ["editSession"] = l["editSession"]?.DeepClone(),
                        ["crumbs"] = new JsonArray(l["crumbs"]!.AsArray().Select(c => (JsonNode?)JsonValue.Create((string?)c!["label"])).ToArray()),
                        ["total"] = hits.Count, ["items"] = page,
                    };
                    if (offset + limit < hits.Count) reply["nextOffset"] = offset + limit;
                    ShowInViewer(full, at, open: true);
                    return ToolResult.Text(reply.ToJsonString(Json), $"{l["name"]}: {hits.Count} item(s)");
                });

            Add("viewer_preview", "Preview",
                "A typed preview of one file or record, as the Asset Viewer shows it, with what its editor can change: rdr (the atom tree " +
                "[atom, type, value, children]; atom and depth pick a subtree), mps (AI map headers and points), text, iconsys, geo (scene " +
                "nodes with nparams record, local transform column-major, bounds, kind, flags, model reference; node picks one, depth cuts " +
                "the tree), clutter (placements by model; use assets_edit moveClutter / addClutter / deleteClutter on the answered session), " +
                "zanim (effects; effect (index) or effectName decodes one: tracks, instructions with offset, fields and raw words), palette " +
                "(palette: its id, on a _PAL.ZED listing; change with assets_edit setPalette), image / icon / audio (sizes; pixels and sound " +
                "are not sent), hex (the first bytes and strings). " + ViewerPlaceHelp + " Models and textures of a package are read with the " +
                "assets_* tools on the listing's session. The viewer shows the item while Follow AI is on. Risk: read-only.",
                true, Schema(new[] { "path" }, path, inner,
                    N("atom", "integer", "rdr: the subtree to answer (default the root).", 0), N("depth", "integer", "rdr / geo: levels below it to include (default 4 / 2; 0: only it).", 0, 64),
                    N("node", "integer", "geo: a scene node's nparams record (default all definitions)."), P("query", "string", "geo: nodes whose name contains it (flat); zanim: effects whose name contains it."),
                    P("model", "string", "clutter: only this model's placements."), N("offset", "integer", "clutter: skip this many placements.", 0), N("limit", "integer", "clutter: at most this many (default 300).", 1, 5000),
                    N("effect", "integer", "zanim: decode this effect (an index from the list).", 0), P("effectName", "string", "zanim: decode the effect of this name."),
                    N("palette", "integer", "palette: the id (texpal_<id>) in this _PAL.ZED.", 0)), a =>
                {
                    string full = FullPath(a);
                    int[] at = Inner(a);
                    string request;
                    if (IntOpt(a, "palette") is int palette)
                    {
                        var (lok, ldata, lerr) = Command(new CommandMessage { Command = "VIEWER_LIST", InputPath = full, AssetJson = JsonSerializer.Serialize(new { inner = at }, Json) });
                        if (!lok || ldata == null) return ToolResult.Fail(lerr ?? "VIEWER_LIST failed");
                        var l = JsonNode.Parse(ldata)!;
                        if ((string?)l["kind"] != "palettes" || l["session"] == null) return ToolResult.Fail("palette needs path / inner of a _PAL.ZED (a palettes listing)");
                        request = JsonSerializer.Serialize(new { session = (string?)l["session"], archive = (string?)l["name"], palette }, Json);
                    }
                    else
                    {
                        int? effect = IntOpt(a, "effect");
                        if (effect == null && Str(a, "effectName") is { } effectName)
                        {
                            var (eok, edata, eerr) = Command(new CommandMessage { Command = "VIEWER_PREVIEW", InputPath = full, AssetJson = JsonSerializer.Serialize(new { inner = at }, Json) });
                            if (!eok || edata == null) return ToolResult.Fail(eerr ?? "VIEWER_PREVIEW failed");
                            var effects = JsonNode.Parse(edata)!["effects"]?.AsArray() ?? throw new ArgumentException("effectName is for a ZANIM archive");
                            int found = effects.Select((e, i) => ((string?)e!["name"], i)).FirstOrDefault(x => string.Equals(x.Item1, effectName, StringComparison.OrdinalIgnoreCase), (null, -1)).Item2;
                            if (found < 0) return ToolResult.Fail($"No effect named {effectName}");
                            effect = found;
                        }
                        request = JsonSerializer.Serialize(new { inner = at, effect }, Json);
                    }
                    var (ok, data, error) = Command(new CommandMessage { Command = "VIEWER_PREVIEW", InputPath = full, AssetJson = request });
                    if (!ok || data == null) return ToolResult.Fail(error ?? "VIEWER_PREVIEW failed");
                    var p = JsonNode.Parse(data)!.AsObject();
                    string type = (string?)p["type"] ?? "";
                    TrimPreview(p, a);
                    var focus = new JsonObject();
                    if (IntOpt(a, "atom") is int atomFocus) focus["atom"] = atomFocus;
                    if (IntOpt(a, "node") is int nodeFocus) focus["record"] = nodeFocus;
                    if (p["effect"] is JsonObject eff && eff["sequence"] is JsonNode seq) focus["sequence"] = seq.DeepClone();
                    if (IntOpt(a, "palette") is int pal) focus["palette"] = pal;
                    ShowInViewer(full, at, open: false, focus);
                    return ToolResult.Text(p.ToJsonString(Json), $"{p["name"]} ({type})");
                });

            Add("viewer_edit", "Edit in place",
                "Change a file or record in place, as the Asset Viewer's editors do, through the package's asset session (one undo step; the " +
                "answer has the session id for assets_undo / assets_redo / assets_save). " + ViewerPlaceHelp + " op is one of:\n" +
                "- rdrSet {atom, value} (an int, float or string atom, as text) · rdrDuplicate {atom} (a list item, copied to the list's end; " +
                "answers the copy's atom) · rdrRemove {atom}\n" +
                "- mpsMap {map, name?, min?, max?, scale?} (an AI map header; points: assets_edit setPoint)\n" +
                "- text {text} (ASCII; keep the file's line breaks, e.g. \\r\\n in .CNF files) · bytes {offset, hex} · iconSys {title1, title2, icons}\n" +
                "- image {pngBase64} (a raw screen or a memory-card icon's texture)\n" +
                "- nparams {node, matrix?, min?, max?, kind?, flags?} (a GEO scene node's nparams record, from viewer_preview) · modelName {node, name} " +
                "(its model_name record)\n" +
                "- zanimField {node, offset, field, fieldValue} (node: the effect's sequence record, offset: the instruction, from viewer_preview " +
                "with effect; fieldValue a number, [x, y, z] or true / false) · zanimWord {node, offset, word, wordValue} (a raw argument word)\n" +
                "Palettes and clutter change with assets_edit (setPalette, moveClutter, addClutter, deleteClutter). The viewer opens the record " +
                "and marks the change. Risk: changes the in-memory session only.",
                false, Schema(new[] { "path", "op" }, path, inner, P("op", "object", "The edit: {op: \"rdrSet\", atom: 12, value: \"3\"} and so on.")), a =>
                {
                    string full = FullPath(a);
                    int[] at = Inner(a);
                    if (!a.TryGetProperty("op", out var op) || op.ValueKind != JsonValueKind.Object || Str(op, "op") is not { } kind)
                        throw new ArgumentException("\"op\" is an object with an \"op\" name");
                    var body = JsonNode.Parse(op.GetRawText())!.AsObject();
                    body["inner"] = new JsonArray(at.Select(i => (JsonNode?)i).ToArray());
                    var (ok, data, error) = Command(new CommandMessage { Command = "VIEWER_EDIT", InputPath = full, AssetJson = body.ToJsonString() });
                    if (!ok || data == null) return ToolResult.Fail(error ?? "VIEWER_EDIT failed");
                    var r = JsonNode.Parse(data)!;
                    string session = (string?)r["session"] ?? "";
                    string? description = (string?)r["edit"]?["description"] ?? (string?)r["status"]?["canUndo"];
                    var focus = new JsonObject { ["path"] = full, ["inner"] = new JsonArray(at.Select(i => (JsonNode?)i).ToArray()) };
                    if (kind is "rdrSet" or "rdrRemove" && IntOpt(op, "atom") is int atom) focus["atom"] = atom;
                    if (kind == "rdrDuplicate" && r["result"] is JsonValue copy && copy.TryGetValue(out int copied)) focus["atom"] = copied;
                    if (kind is "nparams" or "modelName" && IntOpt(op, "node") is int record) focus["record"] = record;
                    if (kind is "zanimField" or "zanimWord" && IntOpt(op, "node") is int sequence) { focus["sequence"] = sequence; focus["offset"] = IntOpt(op, "offset"); }
                    if (kind == "mpsMap" && IntOpt(op, "map") is int map) focus["map"] = map;
                    Changed(session, "viewer", null, new { viewer = focus }, description);
                    return ToolResult.Text(data, $"{description ?? kind} (session {session})");
                });

            Add("viewer_extract", "Extract record",
                "Write a record's bytes, as stored, to a file (a model archive out of a package, an .rdr out of a .ZAR …). " + ViewerPlaceHelp +
                " Risk: writes disk (outputPath).",
                false, Schema(new[] { "path", "inner", "outputPath" }, path, inner, P("outputPath", "string", "The file to write.")),
                a => FromCommand(new CommandMessage
                {
                    Command = "VIEWER_EXTRACT", InputPath = FullPath(a), OutputPath = Str(a, "outputPath"),
                    AssetJson = JsonSerializer.Serialize(new { inner = Inner(a) }, Json),
                }, d => d.TryGetProperty("written", out var w) ? "wrote " + Path.GetFileName(w.GetString()) : null!));

            return tools;
        }

        private static string FullPath(JsonElement a) => Path.GetFullPath(Str(a, "path") ?? throw new ArgumentException("\"path\" is required"));

        private static int[] Inner(JsonElement a) =>
            a.TryGetProperty("inner", out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Select(x => x.GetInt32()).ToArray() : Array.Empty<int>();

        /// <summary>Moves the Asset Viewer to a place (open: list it) or to an item (select it), while Follow AI is on.</summary>
        private void ShowInViewer(string path, int[] inner, bool open, JsonObject? extra = null)
        {
            var viewer = extra ?? new JsonObject();
            viewer["path"] = path;
            viewer["inner"] = new JsonArray(inner.Select(i => (JsonNode?)i).ToArray());
            viewer["open"] = open;
            _ui.Publish(new { type = "show", page = "viewer", path, focus = new { viewer } });
        }

        // A preview cut to what a model can read: no pixels, sound or mesh; an rdr subtree, geo nodes to a depth, a page of clutter.
        private static void TrimPreview(JsonObject p, JsonElement a)
        {
            foreach (string key in new[] { "png", "wav", "mesh" })
                if (p[key] is JsonValue v && v.TryGetValue(out string? s) && s != null) { p.Remove(key); p[key + "Base64Length"] = s.Length; }
                else if (p[key] != null) { p.Remove(key); p[key + "Omitted"] = true; }
            if (p["rgba"] is JsonArray rgba && (string?)p["type"] == "palette")
                p["rgba"] = new JsonArray(Enumerable.Range(0, rgba.Count / 4).Select(i => (JsonNode?)$"#{(int)rgba[i * 4]!:x2}{(int)rgba[i * 4 + 1]!:x2}{(int)rgba[i * 4 + 2]!:x2}{(int)rgba[i * 4 + 3]!:x2}").ToArray());
            switch ((string?)p["type"])
            {
                case "rdr":
                {
                    var tree = p["tree"]!;
                    int? atom = IntOpt(a, "atom");
                    JsonNode? Find(JsonNode n) =>
                        (int)n[0]! == atom ? n : (string?)n[1] == "list" && n.AsArray().Count > 3 ? n[3]!.AsArray().Select(c => Find(c!)).FirstOrDefault(x => x != null) : null;
                    var root = atom == null ? tree : Find(tree) ?? throw new KeyNotFoundException($"No atom {atom} in the tree");
                    p["tree"] = Cut(root, IntOpt(a, "depth") ?? 4);
                    static JsonNode Cut(JsonNode n, int depth)
                    {
                        if ((string?)n[1] != "list" || n.AsArray().Count < 4) return n.DeepClone();
                        if (depth <= 0) return new JsonArray(n[0]!.DeepClone(), "list", n[2]!.DeepClone(), "…");
                        return new JsonArray(n[0]!.DeepClone(), "list", n[2]!.DeepClone(), new JsonArray(n[3]!.AsArray().Select(c => (JsonNode?)Cut(c!, depth - 1)).ToArray()));
                    }
                    p["treeNote"] = "[atom, type, value] or [atom, \"list\", count, children]; \"…\" marks lists below the depth (ask with atom).";
                    break;
                }
                case "geo":
                {
                    var models = p["models"]!.AsArray();
                    var all = new List<JsonObject>();
                    void Walk(JsonArray list) { foreach (var n in list) { all.Add(n!.AsObject()); Walk(n!["children"]!.AsArray()); } }
                    Walk(models);
                    int depth = IntOpt(a, "depth") ?? 2;
                    JsonNode Cut(JsonObject n, int d)
                    {
                        var c = new JsonObject();
                        foreach (var kv in n) if (kv.Key != "children") c[kv.Key] = kv.Value?.DeepClone();
                        var kids = n["children"]!.AsArray();
                        if (d <= 0) c["children"] = kids.Count;
                        else c["children"] = new JsonArray(kids.Select(k => (JsonNode?)Cut(k!.AsObject(), d - 1)).ToArray());
                        return c;
                    }
                    if (IntOpt(a, "node") is int node)
                        p["models"] = new JsonArray(all.Where(n => (int)n["nparams"]! == node || (int?)n["modelName"]?["node"] == node).Select(n => (JsonNode?)Cut(n, depth)).ToArray());
                    else if (Str(a, "query") is { } q)
                        p["models"] = new JsonArray(all.Where(n => ((string?)n["name"] ?? "").Contains(q, StringComparison.OrdinalIgnoreCase) || ((string?)n["modelName"]?["text"] ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)).Select(n => (JsonNode?)Cut(n, 0)).ToArray());
                    else
                        p["models"] = new JsonArray(models.Select(n => (JsonNode?)Cut(n!.AsObject(), depth)).ToArray());
                    p["nodeNote"] = "nparams is the record viewer_edit nparams takes; modelName.node the one modelName takes; a number for children counts nodes below the depth (ask with node).";
                    break;
                }
                case "clutter":
                {
                    var all = p["placements"]!.AsArray().Select((x, i) => { var o = (JsonObject)x!.DeepClone(); o["clutterIndex"] = i; return o; }).ToList();
                    if (Str(a, "model") is { } model) all = all.Where(x => string.Equals((string?)x["model"], model, StringComparison.OrdinalIgnoreCase)).ToList();
                    int offset = IntOpt(a, "offset") ?? 0, limit = IntOpt(a, "limit") ?? 300;
                    p["total"] = all.Count;
                    p["placements"] = new JsonArray(all.Skip(offset).Take(limit).Select(x => (JsonNode?)x).ToArray());
                    if (offset + limit < all.Count) p["nextOffset"] = offset + limit;
                    break;
                }
                case "zanim":
                    if (p["effects"] is JsonArray effects)
                    {
                        string? eq = Str(a, "query");
                        p["effects"] = new JsonArray(effects.Select((e, i) => { var o = (JsonObject)e!.DeepClone(); o["index"] = i; return o; })
                            .Where(e => eq == null || ((string?)e["name"] ?? "").Contains(eq, StringComparison.OrdinalIgnoreCase)).Select(e => (JsonNode?)e).ToArray());
                    }
                    break;
            }
        }
    }
}
