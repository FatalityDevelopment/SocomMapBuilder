using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MapBuilder.Server.Services
{
    /// <summary>
    /// A Model Context Protocol server on POST /mcp (Streamable HTTP, JSON replies): the assets_* tools CodeDesigner's
    /// MCP has, run against the same asset sessions the pages edit, plus ui_* tools that drive and look at the pages.
    /// Every call is announced on the <see cref="UiBridge"/> (tool, reason, result), and edits tell the pages what
    /// changed so they follow along: the Map Builder reloads and frames the moved prop, the Model Editor opens the
    /// edited model.
    /// </summary>
    internal sealed partial class McpServer
    {
        private const string ServerVersion = "0.1.0";
        private static readonly string[] ProtocolVersions = { "2025-06-18", "2025-03-26", "2024-11-05" };
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            // Tool text is JSON inside JSON: keep quotes and paths readable (no "), it is never put in HTML.
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        // Replies past this are cut (a mesh with arrays, a large scene) so they do not flood the model's context.
        private const int MaxText = 120_000;

        private const string Instructions =
            "Map Builder edits SOCOM II packages (.ZDB / .CDB). Start with assets_open (or assets_sessions to join the session the " +
            "user has open), read with assets_map_scene / assets_list_models / assets_describe_model, change with assets_edit " +
            "(undoable, in memory; dryRun previews an edit) and write with assets_save only when the user asks. assets_validate lints a map " +
            "and assets_history lists what undo would take back. Prefabs (assets_save_prefab, assets_list_prefabs, assets_edit placePrefab) " +
            "reuse groups of props across maps. assets_edit placeInstance adds props; newProp copies a prop to reshape (addTexture materials, " +
            "addMesh shapes, hideVisual), fitBounds refits it, and placeInstance puts it in the map; addCharacterMesh puts gear on a " +
            "character, rigid on a bone (assets_describe_model lists its skeleton) or bending with the body without one, removeCharacterSection takes it off, scaleBones reshapes body parts " +
            "(thicker, longer, bigger, blending across joints), transformCharacter moves whole sections, assets_uv_layout shows where a skin lands " +
            "and paintTexture colours it by body part, characters_check (also in those edits' " +
            "replies) flags seams, folds and floating gear; characters_pack ships a " +
            "character as a memory-card pack under a donor name; " +
            "setPoint renames / retypes spawn points; fs_list finds packages; weapons_* builds weapons from a template archive; " +
            "package_* changes package files on disk. viewer_list / viewer_preview / viewer_edit browse and change any disc file or " +
            "archive record in place as the Asset Viewer does (RDR tables, AI maps, text, icons, GEO scene nodes, ZANIM effect scripts), " +
            "undone and saved with assets_undo / assets_save on the session viewer_edit answers. The user watches the app while you work: " +
            "every call's `reason` is shown in its AI activity panel, and edits make the page follow (Map Builder reloads and frames " +
            "the edited item, the Model Editor opens the edited model, the Asset Viewer opens the edited record and marks the change). " +
            "ui_screenshot shows you what the user sees; ui_state what each page has open; ui_show, " +
            "ui_focus and ui_view move the page.";

        private sealed record Tool(string Name, string Label, string Description, Dictionary<string, object> InputSchema, bool ReadOnly,
            Func<JsonElement, Task<ToolResult>> Run);

        private sealed class ToolResult
        {
            public List<object> Content { get; } = new();
            public bool IsError { get; init; }
            public string? Summary { get; init; }

            public static ToolResult Text(string text, string? summary = null)
            {
                var r = new ToolResult { Summary = summary };
                r.Content.Add(new { type = "text", text = text.Length > MaxText ? Truncated(text) : text });
                return r;
            }

            // A reply past MaxText: still valid JSON, with the start of the reply as text and how to get less.
            private static string Truncated(string text) => JsonSerializer.Serialize(new
            {
                truncated = true,
                totalCharacters = text.Length,
                hint = "The reply was too long. Narrow the query (kind, query, limit) or page with offset / nextOffset.",
                start = text[..(MaxText - 400)],
            }, Json);

            public static ToolResult Fail(string error)
            {
                var r = new ToolResult { IsError = true, Summary = error };
                r.Content.Add(new { type = "text", text = error });
                return r;
            }
        }

        private readonly CommandRegistry _registry;
        private readonly UiBridge _ui;
        private readonly List<Tool> _tools;

        public McpServer(CommandRegistry registry, UiBridge ui)
        {
            _registry = registry;
            _ui = ui;
            _tools = BuildTools();
        }

        public int ToolCount => _tools.Count;

        // ------------------------------------------------------------------ transport

        public async Task Handle(HttpContext ctx)
        {
            // A page on another site may POST here (simple requests skip CORS): refuse foreign origins and anything
            // that is not JSON, so only a local client gets through.
            string? origin = ctx.Request.Headers.Origin;
            if (!string.IsNullOrEmpty(origin) && !(Uri.TryCreate(origin, UriKind.Absolute, out var o) && (o.IsLoopback || o.Host == "localhost")))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            if (ctx.Request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) != true)
            {
                ctx.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
                return;
            }

            JsonDocument doc;
            try { doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted); }
            catch (JsonException ex)
            {
                await Write(ctx, Error(null, -32700, $"Parse error: {ex.Message}"));
                return;
            }
            using (doc)
            {
                object? reply;
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    var replies = new List<object>();
                    foreach (var m in doc.RootElement.EnumerateArray())
                        if (await Dispatch(m) is { } r) replies.Add(r);
                    reply = replies.Count > 0 ? replies : null;
                }
                else reply = await Dispatch(doc.RootElement);

                if (reply == null) { ctx.Response.StatusCode = StatusCodes.Status202Accepted; return; }
                await Write(ctx, reply);
            }
        }

        private static Task Write(HttpContext ctx, object reply)
        {
            ctx.Response.ContentType = "application/json";
            return ctx.Response.WriteAsync(JsonSerializer.Serialize(reply, Json));
        }

        internal async Task<object?> Dispatch(JsonElement msg)
        {
            if (msg.ValueKind != JsonValueKind.Object) return Error(null, -32600, "Invalid request");
            // Notifications (no id) and replies to requests we never send get no answer.
            if (!msg.TryGetProperty("id", out var idEl)) return null;
            object id = idEl.Clone();
            string? method = msg.TryGetProperty("method", out var m) ? m.GetString() : null;
            JsonElement p = msg.TryGetProperty("params", out var pe) ? pe : default;
            switch (method)
            {
                case "initialize":
                {
                    string asked = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("protocolVersion", out var v) ? v.GetString() ?? "" : "";
                    return Result(id, new
                    {
                        protocolVersion = ProtocolVersions.Contains(asked) ? asked : ProtocolVersions[0],
                        capabilities = new { tools = new { listChanged = false } },
                        serverInfo = new { name = "mapbuilder", title = "SOCOM Map Builder", version = ServerVersion },
                        instructions = Instructions,
                    });
                }
                case "ping":
                    return Result(id, new { });
                case "tools/list":
                    return Result(id, new
                    {
                        tools = _tools.Select(t => new
                        {
                            name = t.Name,
                            title = t.Label,
                            description = t.Description,
                            inputSchema = t.InputSchema,
                            annotations = new
                            {
                                title = t.Label,
                                readOnlyHint = t.ReadOnly,
                                destructiveHint = !t.ReadOnly && Destructive.Contains(t.Name),
                                idempotentHint = t.ReadOnly,
                                openWorldHint = false,
                            },
                        }),
                    });
                case "tools/call":
                {
                    string? name = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var tool = _tools.FirstOrDefault(t => t.Name == name);
                    if (tool == null) return Error(id, -32602, $"Unknown tool '{name}'");
                    JsonElement args = p.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Object ? a : EmptyObject;
                    var result = await Call(tool, args);
                    return Result(id, new { content = result.Content, isError = result.IsError });
                }
                default:
                    return Error(id, -32601, $"Method '{method}' not found");
            }
        }

        // Tools that write disk or throw work away (the rest change only the in-memory session or the view).
        private static readonly HashSet<string> Destructive = new(StringComparer.Ordinal)
        {
            "assets_save", "assets_close", "assets_entry", "assets_export", "package_edit", "weapons_create", "weapons_pack", "characters_pack", "mapbuilder_command",
            "assets_delete_prefab", "viewer_extract",
        };

        private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

        private static object Result(object id, object result) => new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

        private static object Error(object? id, int code, string message) =>
            new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new { code, message } };

        // A call, announced to the pages before and after (the AI activity panel).
        private async Task<ToolResult> Call(Tool tool, JsonElement args)
        {
            string callId = Guid.NewGuid().ToString("N")[..12];
            string? reason = Str(args, "reason");
            _ui.Publish(new { type = "activity", id = callId, phase = "start", tool = tool.Name, label = tool.Label, reason, readOnly = tool.ReadOnly });
            var sw = Stopwatch.StartNew();
            ToolResult result;
            try { result = CheckArguments(tool, args) is { } bad ? ToolResult.Fail(bad) : await tool.Run(args); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException or TimeoutException or JsonException
                or FormatException or IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or IndexOutOfRangeException)
            {
                result = ToolResult.Fail(ex.Message);
            }
            _ui.Publish(new
            {
                type = "activity", id = callId, phase = result.IsError ? "error" : "done", tool = tool.Name, label = tool.Label, reason,
                summary = result.Summary, ms = sw.ElapsedMilliseconds,
            });
            if (tool.ReadOnly && !result.IsError)
            {
                // Showing what was read is a courtesy: it never fails the read.
                try { ShowRead(tool.Name, args, result); }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException or IOException) { }
            }
            return result;
        }

        /// <summary>
        /// What a read looked at, brought forward in the page that shows it, so the user sees what the AI sees: the model it
        /// described in the Model Editor, the texture it decoded or the record it read in the Asset Viewer, the map it read in
        /// the Map Builder, the clip in the Animation Editor. Not forced: it moves the pages only while Follow AI is on.
        /// (viewer_list / viewer_preview move the Asset Viewer themselves; edits follow through Changed.)
        /// </summary>
        private void ShowRead(string tool, JsonElement a, ToolResult result)
        {
            JsonElement? Answer()
            {
                foreach (var c in result.Content)
                {
                    var e = JsonSerializer.SerializeToElement(c);
                    if (e.TryGetProperty("type", out var t) && t.GetString() == "text" && e.TryGetProperty("text", out var text))
                    {
                        try { return JsonDocument.Parse(text.GetString() ?? "").RootElement.Clone(); } catch (JsonException) { return null; }
                    }
                }
                return null;
            }
            string? SessionPath() => Str(a, "session") is { } s && Status(s) is { } st && st.TryGetProperty("path", out var p) ? p.GetString() : null;
            void Show(string page, string? path, object? model = null, object? focus = null) =>
                _ui.Publish(new { type = "show", page, path, model, focus });
            void Viewer(string path, int[] inner, bool open, JsonObject? extra = null)
            {
                var v = extra ?? new JsonObject();
                v["path"] = path;
                v["inner"] = new JsonArray(inner.Select(i => (JsonNode?)i).ToArray());
                v["open"] = open;
                _ui.Publish(new { type = "show", page = "viewer", path, focus = new { viewer = v } });
            }

            switch (tool)
            {
                case "assets_list_models":
                    Show("models", SessionPath());
                    break;
                case "assets_describe_model":
                case "assets_model_mesh":
                {
                    string? archive = Str(a, "archive") ?? (Answer() is { } d && d.TryGetProperty("archive", out var ar) ? ar.GetString() : null);
                    Show("models", SessionPath(), archive != null ? new { archive, name = Str(a, "model") } : null);
                    break;
                }
                case "assets_weapon_fx":
                {
                    // The weapon's model, when the answer names it.
                    var d = Answer();
                    string? model = d is { } x && x.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
                    string? archive = d is { } y && y.TryGetProperty("archive", out var ar) && ar.ValueKind == JsonValueKind.String ? ar.GetString() : null;
                    if (model != null && archive != null) Show("models", SessionPath(), new { archive, name = model });
                    break;
                }
                case "assets_texture":
                {
                    // The texture in its TXR listing: the package (a loose one by its folder), the archive, the item.
                    var d = Answer();
                    string? ns = d is { } x && x.TryGetProperty("namespace", out var n) ? n.GetString() : null;
                    if (SessionPath() is not { } pkg || ns == null) break;
                    Viewer(ViewerPackage(pkg), Array.Empty<int>(), false,
                        new JsonObject { ["archive"] = $"{ns.ToUpperInvariant()}_TXR.ZED", ["texture"] = Str(a, "texture") });
                    break;
                }
                case "assets_list_textures":
                case "assets_list_palettes":
                    if (SessionPath() is { } listed) Viewer(ViewerPackage(listed), Array.Empty<int>(), true);
                    break;
                case "assets_map_scene":
                case "assets_map_points":
                case "assets_map_collision":
                case "assets_validate":
                    Show("map", SessionPath());
                    break;
                case "assets_list_animations":
                    Show("animations", SessionPath());
                    break;
                case "assets_animation":
                    Show("animations", SessionPath(), null, new { clip = Str(a, "name") });
                    break;
                case "weapons_list":
                case "package_inspect":
                    if (Str(a, "path") is { } file) Viewer(Path.GetFullPath(file), Array.Empty<int>(), true);
                    break;
                case "fs_list":
                    if (Answer() is { } folder && folder.TryGetProperty("path", out var fp) && fp.GetString() is { } dir) Viewer(dir, Array.Empty<int>(), true);
                    break;
                case "weapons_template":
                    if (Str(a, "path") is { } zed) Show("models", Path.GetFullPath(zed), new { archive = Path.GetFileName(zed), name = Str(a, "weapon") });
                    break;
                case "weapons_texture":
                {
                    if (Str(a, "path") is not { } textureZed) break;
                    string full = Path.GetFullPath(textureZed);
                    var d = Answer();
                    string? entry = d is { } x && x.TryGetProperty("textureEntry", out var te) ? te.GetString() : null;
                    // Beside the model archive (its namespace's TXR) or inside it.
                    string archive = d is { } y && y.TryGetProperty("textureNamespace", out var tn) && tn.GetString() is { } tns
                        ? $"{tns.ToUpperInvariant()}_TXR.ZED" : Path.GetFileName(full);
                    if (entry != null) Viewer(Path.GetDirectoryName(full)!, Array.Empty<int>(), false, new JsonObject { ["archive"] = archive, ["texture"] = entry });
                    break;
                }
                case "weapons_definition":
                {
                    // The donor's record in the disc's weapon table.
                    if (Answer() is not { } d || !d.TryGetProperty("run", out var run) || run.GetString() is not { } runDir) break;
                    var focus = new JsonObject();
                    if (d.TryGetProperty("atom", out var atom) && atom.ValueKind == JsonValueKind.Number) focus["atom"] = atom.GetInt32();
                    Viewer(Path.Combine(runDir, "ZWEAPON.ZAR"), new[] { 0 }, false, focus);
                    break;
                }
            }
        }

        // The viewer opens a loose package by its folder: a loose session's path (<folder>\<folder name>) is not on disk.
        private static string ViewerPackage(string path) =>
            File.Exists(path) || Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path;

        /// <summary>
        /// The arguments against the tool's schema: no unknown names, required ones present (a non-empty reason
        /// included), each of its declared type, enum, item type and range. Null when they fit, else the error.
        /// </summary>
        internal static string? CheckArguments(string toolName, Dictionary<string, object> schema, JsonElement args)
        {
            var props = (Dictionary<string, object>)schema["properties"];
            var required = (string[])schema["required"];
            foreach (var arg in args.EnumerateObject())
                if (!props.ContainsKey(arg.Name))
                    return $"{toolName}: unknown argument \"{arg.Name}\" (takes {string.Join(", ", props.Keys.OrderBy(k => k))})";
            foreach (string name in required)
                if (!args.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null || (v.ValueKind == JsonValueKind.String && v.GetString()!.Trim().Length == 0))
                    return $"{toolName}: \"{name}\" is required";
            foreach (var arg in args.EnumerateObject())
            {
                if (arg.Value.ValueKind == JsonValueKind.Null) continue;
                var p = (Dictionary<string, object>)props[arg.Name];
                if (CheckValue(arg.Name, p, arg.Value) is { } error) return $"{toolName}: {error}";
            }
            return null;
        }

        private static string? CheckArguments(Tool tool, JsonElement args) => CheckArguments(tool.Name, tool.InputSchema, args);

        internal static string? CheckValue(string name, Dictionary<string, object> p, JsonElement v)
        {
            string type = p.TryGetValue("type", out var t) && t is string ts ? ts : "any";
            bool fits = type switch
            {
                "string" => v.ValueKind == JsonValueKind.String,
                "boolean" => v.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "integer" => v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out _),
                "number" => v.ValueKind == JsonValueKind.Number,
                "object" => v.ValueKind == JsonValueKind.Object,
                "array" => v.ValueKind == JsonValueKind.Array,
                _ => true,
            };
            if (!fits) return $"\"{name}\" must be {(type is "integer" or "array" or "object" ? "an" : "a")} {type}, not {v.ValueKind.ToString().ToLowerInvariant()}";
            if (p.TryGetValue("enum", out var e) && e is string[] values && !values.Contains(v.GetString()))
                return $"\"{name}\" is one of {string.Join(", ", values)}, not \"{v.GetString()}\"";
            if (v.ValueKind == JsonValueKind.Number)
            {
                double d = v.GetDouble();
                if (p.TryGetValue("minimum", out var min) && d < Convert.ToDouble(min)) return $"\"{name}\" must be at least {min}";
                if (p.TryGetValue("maximum", out var max) && d > Convert.ToDouble(max)) return $"\"{name}\" must be at most {max}";
                if (p.TryGetValue("exclusiveMinimum", out var xmin) && d <= Convert.ToDouble(xmin)) return $"\"{name}\" must be more than {xmin}";
            }
            if (v.ValueKind == JsonValueKind.Array)
            {
                int n = v.GetArrayLength();
                if (p.TryGetValue("minItems", out var lo) && n < Convert.ToInt32(lo)) return $"\"{name}\" needs at least {lo} items";
                if (p.TryGetValue("maxItems", out var hi) && n > Convert.ToInt32(hi)) return $"\"{name}\" takes at most {hi} items";
                if (p.TryGetValue("items", out var it) && it is Dictionary<string, object> item)
                    foreach (var x in v.EnumerateArray())
                        if (CheckValue(name + "[]", item, x) is { } error) return error;
            }
            return null;
        }

        // ------------------------------------------------------------------ commands

        private (bool Ok, string? Data, string? Error) Command(CommandMessage m)
        {
            if (!_registry.TryGet(m.Command, out var handler) || handler == null) return (false, null, $"Unknown command '{m.Command}'");
            string raw;
            try { raw = handler(m); }
            catch (Exception ex) { return (false, null, $"{m.Command}: {ex.Message}"); }
            var r = JsonSerializer.Deserialize<CommandResponse>(raw) ?? new CommandResponse { Error = "empty reply" };
            return (r.Success, r.Data ?? r.Message, r.Error);
        }

        private ToolResult FromCommand(CommandMessage m, Func<JsonElement, string>? summary = null)
        {
            var (ok, data, error) = Command(m);
            if (!ok) return ToolResult.Fail(error ?? $"{m.Command} failed");
            string? s = null;
            if (summary != null && data != null)
            {
                try { using var d = JsonDocument.Parse(data); s = summary(d.RootElement); } catch (JsonException) { }
            }
            return ToolResult.Text(data ?? "ok", s);
        }

        private JsonElement? Status(string session)
        {
            var (ok, data, _) = Command(new CommandMessage { Command = "ASSET_STATUS", AssetSession = session });
            return ok && data != null ? JsonDocument.Parse(data).RootElement.Clone() : null;
        }

        private static string Session(JsonElement a) => Str(a, "session") ?? throw new ArgumentException("\"session\" is required (assets_open or assets_sessions gives it)");

        // Tells the pages the session changed; page / model / focus say what to bring forward.
        private void Changed(string session, string? page, object? model, object? focus, string? description)
        {
            var st = Status(session);
            _ui.Publish(new
            {
                type = "changed",
                session,
                path = st is { } s && s.TryGetProperty("path", out var p) ? p.GetString() : null,
                revision = st is { } s2 && s2.TryGetProperty("revision", out var r) ? r.GetInt32() : (int?)null,
                page, model, focus, description,
            });
        }

        // Which page shows an edit, and what to select there.
        private static (string? Page, object? Model, object? Focus) Follow(JsonElement op, JsonElement result)
        {
            string kind = Str(op, "op") ?? "";
            if (kind == "batch" && op.TryGetProperty("ops", out var ops) && ops.ValueKind == JsonValueKind.Array && ops.GetArrayLength() > 0)
            {
                var first = ops[0];
                JsonElement firstResult = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("results", out var rs)
                    && rs.ValueKind == JsonValueKind.Array && rs.GetArrayLength() > 0 ? rs[0] : default;
                return Follow(first, firstResult);
            }
            int? R(string name) => result.ValueKind == JsonValueKind.Object && result.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
            switch (kind)
            {
                case "moveClutter": return ("map", null, new { clutterIndex = IntOpt(op, "clutterIndex") });
                case "addClutter": return ("map", null, new { clutterIndex = R("newClutterIndex") });
                case "moveInstance": return ("map", null, new { geoIndex = IntOpt(op, "geoIndex"), instance = Str(op, "instance") });
                case "duplicateInstance": return ("map", null, new { geoIndex = R("newGeoIndex") });
                case "deleteClutter":
                case "deleteInstance": return ("map", null, null);
                case "movePoint": return ("map", null, new { point = Str(op, "name") });
                case "setPoint": return ("map", null, new { point = Str(op, "newName") ?? Str(op, "name") });
                case "addPoint": return ("map", null, new { point = Str(op, "name") });
                case "removePoint":
                case "clearMap":
                case "clearActions":
                case "addLadder":
                case "pruneAssets":
                case "setCollision":
                case "clearCollision": return ("map", null, null);
                case "placeInstance": return ("map", null, new { geoIndex = R("newGeoIndex"),
                    instance = result.ValueKind == JsonValueKind.Object ? Str(result, "newInstance") : null });
                case "placePrefab":
                {
                    JsonElement first = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("results", out var pr) && pr.ValueKind == JsonValueKind.Array && pr.GetArrayLength() > 0 ? pr[0] : default;
                    int? geo = first.ValueKind == JsonValueKind.Object && first.TryGetProperty("newGeoIndex", out var g) && g.ValueKind == JsonValueKind.Number ? g.GetInt32() : null;
                    int? clutter = first.ValueKind == JsonValueKind.Object && first.TryGetProperty("newClutterIndex", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : null;
                    return ("map", null, geo != null ? new { geoIndex = geo } : clutter != null ? new { clutterIndex = clutter } : null);
                }
                case "setPalette": return ("viewer", null, new { viewer = new { archive = Str(op, "archive"), palette = IntOpt(op, "id") } });
                case "addAnimation":
                case "deleteAnimation":
                case "renameAnimation":
                case "setAnimation":
                case "setAnimationKey": return ("animations", null, null);
                case "importModel":
                case "newWeapon":
                case "newProp":
                {
                    string? archive = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("archive", out var ar) ? ar.GetString() : null;
                    string? name = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("name", out var nm) ? nm.GetString() : null;
                    return archive != null && name != null ? ("models", new { archive, name }, null) : ("models", null, null);
                }
            }
            if (Str(op, "archive") is { } a && Str(op, "model") is { } model) return ("models", new { archive = a, name = model }, null);
            return (null, null, null);
        }

        // ui_show for a model: the page opens packages on its own, so wait for its state to have the model, or for the
        // error it shows when the open failed (a missing path, a package it cannot read).
        private async Task<ToolResult> ModelShown(string? path, string archive, string name)
        {
            for (int tries = 0; tries < 16; tries++)
            {
                await Task.Delay(500);
                JsonElement state;
                try { state = await _ui.Request("state", null, TimeSpan.FromSeconds(3)); }
                catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or TaskCanceledException) { continue; }
                if (!state.TryGetProperty("pages", out var pages) || !pages.TryGetProperty("models", out var me) || me.ValueKind != JsonValueKind.Object) continue;
                if (me.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.Object
                    && string.Equals(m.GetProperty("name").GetString(), name, StringComparison.Ordinal)
                    && string.Equals(m.GetProperty("archive").GetString(), archive, StringComparison.OrdinalIgnoreCase)
                    && SamePackage(me.TryGetProperty("path", out var pp) ? pp.GetString() : null, path))
                    return ToolResult.Text($"Showing {name} in the Model Editor.", "models");
                if (me.TryGetProperty("messageIsError", out var err) && err.ValueKind == JsonValueKind.True
                    && me.TryGetProperty("message", out var msg) && msg.GetString() is { } text
                    && (path == null || text.Contains(path, StringComparison.OrdinalIgnoreCase)))
                    return ToolResult.Fail($"The Model Editor could not show {name}: {text}");
            }
            return ToolResult.Text($"Asked the Model Editor to show {name}; it has not opened it yet (ui_state shows what it has).", "models");
        }

        // The page's package is the one asked for: the same path, or for a loose package the same folder (its status path is
        // <folder>\<folder name>; a request may name the folder, a file in it or that status path).
        private static bool SamePackage(string? shown, string? requested)
        {
            if (requested == null) return true;
            if (shown == null) return false;
            if (string.Equals(shown, requested, StringComparison.OrdinalIgnoreCase)) return true;
            static string? Folder(string p) => Directory.Exists(p) ? p.TrimEnd('\\', '/') : Path.GetDirectoryName(p);
            return string.Equals(Folder(shown), Folder(requested), StringComparison.OrdinalIgnoreCase)
                && !shown.EndsWith(".ZDB", StringComparison.OrdinalIgnoreCase) && !shown.EndsWith(".CDB", StringComparison.OrdinalIgnoreCase);
        }

        // ------------------------------------------------------------------ tools

        private List<Tool> BuildTools()
        {
            var tools = new List<Tool>();
            void Add(string name, string label, string description, bool readOnly, Dictionary<string, object> schema, Func<JsonElement, Task<ToolResult>> run)
                => tools.Add(new Tool(name, label, description, schema, readOnly, run));
            void AddSync(string name, string label, string description, bool readOnly, Dictionary<string, object> schema, Func<JsonElement, ToolResult> run)
                => Add(name, label, description, readOnly, schema, a => Task.FromResult(run(a)));

            var session = P("session", "string", "Session id (assets_open / assets_sessions).");

            AddSync("mesh_refine_obj", "Round an OBJ mesh",
                "Subdivide an exported model, sharing geometry across chunk and UV seams. Writes a new OBJ, retaining material references. Place it beside its MTL and textures. Returns before/after triangle counts.",
                false, Schema(new[] { "inputPath", "outputPath" }, P("inputPath", "string", "Source OBJ path."),
                    P("outputPath", "string", "New OBJ path (must not exist)."), N("levels", "integer", "4Ã— triangles per level. Default 1.", 1, 2),
                    N("crease", "number", "Preserve edges sharper than this angle in degrees. Default 105.", 0, 180),
                    N("strength", "number", "Fraction of full smoothing per level. Default 0.75.", 0, 1),
                    A("brushes", "object", "Optional local shaping before subdivision, in order. Each {center:[x,y,z], radius:[rx,ry,rz], scale:[sx,sy,sz]} in OBJ space, with smooth falloff.", maxItems: 32)),
                a => FromCommand(new CommandMessage { Command = "MESH_REFINE_OBJ", InputPath = Str(a, "inputPath"),
                    OutputPath = Str(a, "outputPath"), AssetJson = a.GetRawText() }));

            // â”€â”€ sessions
            AddSync("assets_open", "Open package",
                "Open a SOCOM package (.ZDB / .CDB, loose archives or package folder), a Combined Assault location archive or mission path, " +
                "or a GTA: San Andreas mounted disc folder, ISO, IMG or DFF. The pages share a session for each path. Returns the " +
                "session id and a summary. The app opens it too: in the Map Builder when it is a map, else the Model Editor. Risk: changes " +
                "only what the app shows.",
                false, Schema(new[] { "path" }, P("path", "string", "Path to the package or its unpacked folder.")), a =>
                {
                    string path = Str(a, "path") ?? throw new ArgumentException("\"path\" is required");
                    var (ok, data, error) = Command(new CommandMessage { Command = "ASSET_OPEN", InputPath = path });
                    if (!ok || data == null) return ToolResult.Fail(error ?? "ASSET_OPEN failed");
                    using var d = JsonDocument.Parse(data);
                    var st = d.RootElement.GetProperty("status");
                    string id = st.GetProperty("session").GetString()!;
                    string full = st.TryGetProperty("path", out var fp) ? fp.GetString() ?? path : path;
                    bool isMap = Command(new CommandMessage { Command = "ASSET_MAP_SCENE", AssetSession = id, AssetJson = "{\"limit\":1}" }).Ok;
                    _ui.Publish(new { type = "show", page = isMap ? "map" : "models", path = full, session = id });
                    return ToolResult.Text(data, $"{Path.GetFileName(full)} â†’ session {id}");
                });

            AddSync("assets_sessions", "List sessions",
                "List the open asset sessions (including the one the user has open in the app): id, path, revision, changed archives, undo. Risk: read-only.",
                true, Schema(Array.Empty<string>()), _ => FromCommand(new CommandMessage { Command = "ASSET_SESSIONS" },
                    d => $"{d.GetArrayLength()} session(s)"));

            AddSync("assets_status", "Session status",
                "A session's status: path, revision, changed (unsaved) archives, the undo / redo descriptions, model counts by kind. Risk: read-only.",
                true, Schema(new[] { "session" }, session), a => FromCommand(new CommandMessage { Command = "ASSET_STATUS", AssetSession = Session(a) }));

            // â”€â”€ reading
            AddSync("assets_list_models", "List models",
                "The package's models: archive, name, kind (prop, library, character, weapon, vehicle, world â€¦), namespace, size. Filter by kind, " +
                "archive or a name substring. Risk: read-only.",
                true, Schema(new[] { "session" }, session, P("kind", "string", "Optional kind filter."), P("query", "string", "Optional name substring."),
                    P("archive", "string", "Optional archive leaf, e.g. M51_MDL.ZED.")),
                a => FromCommand(new CommandMessage
                {
                    Command = "ASSET_LIST_MODELS", AssetSession = Session(a),
                    AssetJson = JsonSerializer.Serialize(new { kind = Str(a, "kind"), query = Str(a, "query"), archive = Str(a, "archive") }, Json),
                }, d => $"{d.GetArrayLength()} model(s)"));

            AddSync("assets_describe_model", "Describe model",
                "A model's chunks (visual, texture, vertex / triangle counts, placement), its GEO definition and textures. Risk: read-only.",
                true, Schema(new[] { "session", "model" }, session, P("archive", "string", "Archive leaf, e.g. M51_MDL.ZED (optional)."), P("model", "string", "Model name.")),
                a => FromCommand(new CommandMessage { Command = "ASSET_DESCRIBE_MODEL", AssetSession = Session(a), AssetArchive = Str(a, "archive"), AssetName = Str(a, "model") }));

            AddSync("assets_model_mesh", "Read mesh",
                "The editable mesh of a model in model space, as the Model Editor shows it: per chunk its geometryOffset (the id setVertices / " +
                "transformVertices take), visual, texture, counts and placement. arrays=true adds base64 float32 positions / uvs / normals, RGBA " +
                "colours (128 = 1.0) and uint32 triangles (large). Skinned characters come in bind pose with their skeleton. Risk: read-only.",
                true, Schema(new[] { "session", "archive", "model" }, session, P("archive", "string", "Archive leaf, e.g. M51_MDL.ZED."), P("model", "string", "Model name."),
                    P("arrays", "boolean", "Include the vertex arrays (large).")),
                a => FromCommand(new CommandMessage
                {
                    Command = "ASSET_MODEL_MESH", AssetSession = Session(a), AssetArchive = Str(a, "archive"), AssetName = Str(a, "model"),
                    AssetJson = JsonSerializer.Serialize(new { arrays = Bool(a, "arrays") }, Json),
                }));

            AddSync("assets_list_textures", "List textures",
                "Every texture: name, namespace, archive, size, bpp, palette id. Risk: read-only.",
                true, Schema(new[] { "session" }, session), a => FromCommand(new CommandMessage { Command = "ASSET_LIST_TEXTURES", AssetSession = Session(a) },
                    d => $"{d.GetArrayLength()} texture(s)"));

            AddSync("assets_list_palettes", "List palettes",
                "Every palette: id, namespace, archive, 16 / 32-bit, and the textures that bind it. Risk: read-only.",
                true, Schema(new[] { "session" }, session), a => FromCommand(new CommandMessage { Command = "ASSET_LIST_PALETTES", AssetSession = Session(a) }));

            AddSync("assets_texture", "View texture",
                "Decode a texture (.tif) with the palette the game binds and return it as an image, with its info. Risk: read-only.",
                true, Schema(new[] { "session", "texture" }, session, P("texture", "string", "Texture name, e.g. oaktrunk.tif."),
                    P("archive", "string", "Optional TXR archive leaf or namespace to prefer (M51_TXR.ZED or m51).")), a =>
                {
                    var (ok, data, error) = Command(new CommandMessage { Command = "ASSET_TEXTURE", AssetSession = Session(a), AssetName = Str(a, "texture"), AssetArchive = Str(a, "archive") });
                    if (!ok || data == null) return ToolResult.Fail(error ?? "ASSET_TEXTURE failed");
                    using var d = JsonDocument.Parse(data);
                    var r = new ToolResult { Summary = Str(a, "texture") };
                    r.Content.Add(new { type = "image", data = d.RootElement.GetProperty("png").GetString(), mimeType = "image/png" });
                    r.Content.Add(new { type = "text", text = d.RootElement.GetProperty("info").GetRawText() });
                    return r;
                });

            AddSync("assets_map_scene", "Read map scene",
                "What the map places: kind (model = worldmodel, reference = a placed prop / turret / vehicle / sky, clutter = trees, bushes, grass, " +
                "rocks from CLUTTER.ZAR), model, position, scale, matrix, and the geoIndex / clutterIndex edit ops take. Filter by kind or a " +
                "name / path substring; limit defaults to 500. When more match, the answer's nextOffset pages on (pass it as offset). " +
                "Risk: read-only.",
                true, Schema(new[] { "session" }, session, E("kind", "Optional kind.", "model", "reference", "visuals", "clutter"),
                    P("query", "string", "Optional model name or scene path substring."), N("hour", "integer", "GTA scene hour (default noon).", minimum: 0, maximum: 23),
                    N("limit", "integer", "Maximum instances (default 500).", minimum: 1, maximum: 5000),
                    N("offset", "integer", "Skip this many matching instances (from a previous answer's nextOffset).", minimum: 0)),
                a => FromCommand(new CommandMessage
                {
                    Command = "ASSET_MAP_SCENE", AssetSession = Session(a),
                    AssetJson = JsonSerializer.Serialize(new { kind = Str(a, "kind"), query = Str(a, "query"), limit = IntOpt(a, "limit"), offset = IntOpt(a, "offset"), hour = IntOpt(a, "hour") }, Json),
                }, d => d.TryGetProperty("instances", out var i) ? $"{i.GetArrayLength()} instance(s)" + (d.TryGetProperty("nextOffset", out var n) && n.ValueKind == JsonValueKind.Number ? $", more from {n.GetInt32()}" : "") : null!));

            AddSync("assets_validate", "Check map",
                "Lint a map before saving or testing it: placed props and clutter whose model the package lacks, broken or zero-scale " +
                "transforms, duplicate AIMAPS point names, no start points, items outside the AI map, texture names retail models cannot hold " +
                "(over 15 characters), and the textures' total size against an optional VRAM budget. ok is false when there are errors. " +
                "Run it after a series of edits. Risk: read-only.",
                true, Schema(new[] { "session" }, session, N("vramBudget", "integer", "Optional texture budget in bytes to warn above.", minimum: 0)),
                a => FromCommand(new CommandMessage
                {
                    Command = "ASSET_VALIDATE", AssetSession = Session(a),
                    AssetJson = JsonSerializer.Serialize(new { vramBudget = a.TryGetProperty("vramBudget", out var vb) ? vb.GetInt64() : (long?)null }, Json),
                }, d => d.TryGetProperty("counts", out var c) ? (c.EnumerateObject().Any() ? string.Join(", ", c.EnumerateObject().Select(x => $"{x.Value.GetInt32()} {x.Name}")) : "no problems") : null!));

            AddSync("assets_list_prefabs", "List prefabs",
                "The saved prefabs (groups of props and clutter placed together, shared by every package): name, description, item count and " +
                "the models they need, or one prefab's items in full with name. Place one with assets_edit {op: placePrefab}; a map needs " +
                "the prefab's models (import them first). Risk: read-only.",
                true, Schema(Array.Empty<string>(), P("name", "string", "Optional prefab to read in full.")),
                a => FromCommand(new CommandMessage { Command = "ASSET_LIST_PREFABS", AssetName = Str(a, "name") },
                    d => d.TryGetProperty("prefabs", out var l) ? $"{l.GetArrayLength()} prefab(s)" : $"{d.GetProperty("items").GetArrayLength()} item(s)"));

            AddSync("assets_save_prefab", "Save prefab",
                "Save placed props (geoIndices) and clutter (clutterIndices) from a map as a reusable prefab, relative to origin (default: the " +
                "items' centre on the ground). Read the indices with assets_map_scene. replace overwrites a prefab of the same name. " +
                "Risk: writes a prefab file (not the package).",
                false, Schema(new[] { "session", "name" }, session, P("name", "string", "Prefab name: letters, digits, spaces, _ . -"),
                    A("geoIndices", "integer", "Placed references to include."), A("clutterIndices", "integer", "Clutter placements to include."),
                    A("origin", "number", "Optional [x, y, z] the items are saved around.", minItems: 3, maxItems: 3),
                    P("description", "string", "Optional description."), P("replace", "boolean", "Overwrite an existing prefab.")),
                a =>
                {
                    var fields = new Dictionary<string, object?>();
                    foreach (string k in new[] { "name", "geoIndices", "clutterIndices", "origin", "description", "replace" })
                        if (a.TryGetProperty(k, out var v)) fields[k] = v;
                    return FromCommand(new CommandMessage { Command = "ASSET_SAVE_PREFAB", AssetSession = Session(a), AssetJson = JsonSerializer.Serialize(fields, Json) },
                        d => $"saved {d.GetProperty("name").GetString()} ({d.GetProperty("items").GetInt32()} items)");
                });

            AddSync("assets_delete_prefab", "Delete prefab",
                "Delete a saved prefab file. Only when the user asks. Risk: deletes a prefab file.",
                false, Schema(new[] { "name" }, P("name", "string", "The prefab.")),
                a => FromCommand(new CommandMessage { Command = "ASSET_DELETE_PREFAB", AssetName = Str(a, "name") }, _ => "deleted"));

            AddSync("characters_presets", "List character presets",
                "Character presets: saved body shapes, skins and gear sets (any list of character edits), shipped with the app or saved " +
                "by the user or an AI: name, description, the ops they run, or one preset in full with name. Apply one to any skinned " +
                "character with assets_edit {op: applyCharacterPreset, preset, archive, model} (one undo step). Risk: read-only.",
                true, Schema(Array.Empty<string>(), P("name", "string", "Optional preset to read in full.")),
                a => FromCommand(new CommandMessage { Command = "ASSET_LIST_CHARACTER_PRESETS", AssetName = Str(a, "name") },
                    d => d.TryGetProperty("presets", out var l) ? $"{l.GetArrayLength()} preset(s)" : $"{d.GetProperty("ops").GetArrayLength()} op(s)"));

            AddSync("characters_save_preset", "Save character preset",
                "Save a list of character edits as a preset to apply to other characters later: ops as assets_edit takes them, without " +
                "archive and model (applying fills them in). It may hold addTexture, replaceTexture, paintTexture, retexture, scaleBones, " +
                "transformCharacter, addCharacterMesh, removeCharacterSection and setSkinWeights. Gear positions are in the bind pose " +
                "of the character it was made on; addCharacterMesh with conform fits it to another body. replace overwrites. " +
                "Risk: writes a preset file (not the package).",
                false, Schema(new[] { "name", "ops" }, P("name", "string", "Preset name: letters, digits, spaces, _ . -"),
                    A("ops", "object", "The character edits, in order."), P("description", "string", "What it does."), P("replace", "boolean", "Overwrite a saved preset.")),
                a =>
                {
                    var fields = new Dictionary<string, object?>();
                    foreach (string k in new[] { "name", "ops", "description", "replace" })
                        if (a.TryGetProperty(k, out var v)) fields[k] = v;
                    return FromCommand(new CommandMessage { Command = "ASSET_SAVE_CHARACTER_PRESET", AssetJson = JsonSerializer.Serialize(fields, Json) },
                        d => $"saved {d.GetProperty("name").GetString()} ({d.GetProperty("ops").GetInt32()} ops)");
                });

            AddSync("characters_delete_preset", "Delete character preset",
                "Delete a saved character preset (not a shipped one). Only when the user asks. Risk: deletes a preset file.",
                false, Schema(new[] { "name" }, P("name", "string", "The preset.")),
                a => FromCommand(new CommandMessage { Command = "ASSET_DELETE_CHARACTER_PRESET", AssetName = Str(a, "name") }, _ => "deleted"));

            AddSync("assets_history", "Edit history",
                "A session's undo and redo steps (most recent first, with the archives each touched), its revision and unsaved archives: " +
                "what assets_undo / assets_redo would take back. Risk: read-only.",
                true, Schema(new[] { "session" }, session), a => FromCommand(new CommandMessage { Command = "ASSET_HISTORY", AssetSession = Session(a) },
                    d => $"{d.GetProperty("undo").GetArrayLength()} undo, {d.GetProperty("redo").GetArrayLength()} redo"));

            AddSync("assets_map_points", "Read map points",
                "The AIMAPS spawn and waypoint points (name, team, kind, x, z, grid cell) and the AI grid's bounds. SOCOM 3 / CA: the " +
                "points of one mission of the location (mission.rdr; kinds start, waypoint, marker, spectator, other), with its zone and " +
                "island. Verified default demolition spawns appear separately in teamSpawns. The mission argument selects a mission by id.",
                true, Schema(new[] { "session" }, session, P("mission", "string", "SOCOM 3 / CA only: the mission whose points to read (kept for later calls).")),
                a => FromCommand(new CommandMessage { Command = "ASSET_MAP_POINTS", AssetSession = Session(a),
                    AssetJson = JsonSerializer.Serialize(new { mission = Str(a, "mission") }, Json) },
                    d => d.TryGetProperty("points", out var p) ? $"{p.GetArrayLength()} point(s)" : null!));

            AddSync("assets_map_collision", "Read map collision",
                "What is solid on a SOCOM II map (its GEO di faces): counts of world and prop faces, the placed props with no collision " +
                "(walked and shot through in game), and the faces in world space (points [x,y,z,...], up = a floor or up-slope, material, " +
                "the node holding it), in an area and up to maxFaces. With model: that model's own faces in model space. Fix with " +
                "assets_edit setCollision / clearCollision. Risk: read-only.",
                true, Schema(new[] { "session" }, session,
                    P("area", "array", "[minX, minZ, maxX, maxZ]: faces centred inside and props placed inside."),
                    P("model", "string", "A model: its own faces instead."),
                    P("maxFaces", "integer", "Faces to return (default 6000)."),
                    P("faces", "boolean", "false: counts and bare props only.")),
                a => FromCommand(new CommandMessage { Command = "ASSET_MAP_COLLISION", AssetSession = Session(a), AssetJson = a.GetRawText() },
                    d => d.TryGetProperty("faces", out var fc) ? $"{fc.GetArrayLength()} face(s)" : null!));

            AddSync("assets_map_tables", "Read mission tables",
                "The map's mission tables decoded: HUD objectives (text, valves, map LOCATION [x, z]), actors (vehicles.rdr: display name, " +
                "character, start points, teams, AI accuracy / courage), mission settings (AI parameters, satchel timer, valves such as " +
                "weapon Enable_*), world settings (lighting, fog, clip; read-only), map extents, mission cameras and valves, with the " +
                "in-game text of their message keys. Each editable row carries its path for assets_edit mapTable; mapText changes a message. Risk: read-only.",
                true, Schema(new[] { "session" }, session), a => FromCommand(new CommandMessage { Command = "ASSET_MAP_TABLES", AssetSession = Session(a) },
                    d => $"{d.GetProperty("objectives").GetArrayLength()} objective(s), {d.GetProperty("actors").GetArrayLength()} actor(s)"));

            AddSync("assets_list_animations", "List animations",
                "Character clips: the package's MOTION_S.ZAR (editable) and the shared MOTION_P.ZAR (read-only). Filter by a name substring or source. Risk: read-only.",
                true, Schema(new[] { "session" }, session, P("query", "string", "Optional clip name substring."), P("source", "string", "Optional source: package or shared.")),
                a => FromCommand(new CommandMessage
                {
                    Command = "ASSET_LIST_ANIMATIONS", AssetSession = Session(a),
                    AssetJson = JsonSerializer.Serialize(new { query = Str(a, "query"), source = Str(a, "source") }, Json),
                }));

            AddSync("assets_animation", "Read animation",
                "One clip's tracks: per bone its position / rotation keys (1 key = static). Risk: read-only.",
                true, Schema(new[] { "session", "name" }, session, P("name", "string", "Clip name."), P("source", "string", "Optional source archive.")),
                a => FromCommand(new CommandMessage { Command = "ASSET_ANIMATION", AssetSession = Session(a), AssetName = Str(a, "name"), AssetArchive = Str(a, "source") }));

            AddSync("assets_weapons", "List weapons",
                "The package's weapon definitions: internal name, model, fire animation, fire wait, tracer texture. Risk: read-only.",
                true, Schema(new[] { "session" }, session), a => FromCommand(new CommandMessage { Command = "ASSET_WEAPONS", AssetSession = Session(a) }));

            // â”€â”€ editing
            AddSync("assets_edit", "Edit",
                "Apply one edit to a session (undoable; nothing is written to disk until assets_save). `op` is an object with \"op\":\n" +
                "- moveClutter {clutterIndex, position?, yaw?, scale?, matrix?}\n" +
                "- addClutter {model, position, yaw?, scale?} (answers newClutterIndex)\n" +
                "- deleteClutter {clutterIndex}\n" +
                "- moveInstance {geoIndex, position?, yaw?, scale?, matrix?} (world space; Combined Assault uses the scene's string instance id instead of geoIndex)\n" +
                "- duplicateInstance {geoIndex, position?, yaw?, scale?, name?} (answers newGeoIndex)\n" +
                "- placeInstance {model, position, yaw?, scale?, matrix?, name?, templateGeoIndex?} (a new placed reference of any " +
                "package model, e.g. a crate, turret or vehicle; copied from the placed reference nearest the position unless " +
                "templateGeoIndex picks one; answers newGeoIndex)\n" +
                "- On a GTA map, placeInstance {model, archive?, matrix | position, templateInstance?} adds a prop and returns newInstance. " +
                "moveInstance {instance, matrix | position, followLod?} moves its dedicated LOD by default.\n" +
                "- deleteInstance {geoIndex}, or {instance} on a GTA map\n" +
                "- movePoint {name, position:[x,y,z]} (an AIMAPS point; snaps to its grid cell)\n" +
                "- setPoint {name, newName?, team?, kind?: start|waypoint|other, position?} (changes a point in place)\n" +
                "- addPoint {name, kind: start|waypoint|other, team?, position} / removePoint {name} (SOCOM II AIMAPS points; not yet tried in game)\n" +
                "- setCollision {model, shape?: box|mesh|definition|none, boxes?:[{min,max}], bottom?, material?, like?} (a model's GEO di faces, written " +
                "to its definition and every placed reference; a prop without them is walked and shot through. placeInstance copies the " +
                "placed model's faces, so set them before placing or after: both work)\n" +
                "- pruneAssets {models?, textures?, keep?} (removes unplaced props of the map's own archives and the textures / palettes " +
                "nothing draws with; answers the lists and bytes freed; dryRun: true lists them without removing)\n" +
                "- clearMap {props?, clutter?, world?, collision?, actions?, keepModels?, keepVisuals?} (a blank map to build on, one undo step: every " +
                "prop and clutter placement deleted, every world visual but sky / clouds / sea hidden, the world's collision and object actions removed)\n" +
                "- addLadder {position (foot), top, yaw? (the way the climbing face points), width?, name?} (a ladder node as MP2's: two faces, no visual; answers newGeoIndex)\n" +
                "- clearActions {dangling?} (remove the map's object actions, or only those whose node was deleted)\n" +
                "- clearCollision {geoIndices? | visuals? | area?:[minX,minZ,maxX,maxZ] | all?, references?} (the world's own floors and walls: " +
                "clear them under world visuals you hid; assets_map_collision shows what is solid)\n" +
                "- replaceTexture {texture, pngBase64 | imagePath, namespace?}\n" +
                "- retexture {archive, model, from, to, visual?}\n" +
                "- tintVertexColors {archive, model, multiply:[r,g,b], add?, visual?, texture?, alpha?}\n" +
                "- transformVertices {archive, model, translate?, rotate? (degrees), scale?, pivot?, chunks?, vertices?, matrix?}\n" +
                "- setVertices {archive, model, geometryOffset, vertices:[{i, position?, uv?, color?, normal?}]}\n" +
                "- deleteFaces / duplicateFaces {archive, model, faces:{geometryOffset:[triangleâ€¦]}, â€¦}\n" +
                "- addMesh {archive, model, visual:\"N000_000\", texture, replace?, shapes | obj | objPath | mesh | meshPath | primitive:{kind:cube|plane, size, center}}: " +
                "shapes [{kind: lathe {profile:[[along,radius]]} | cylinder {start,end,radius,radiusEnd?} | ring {at,inner,outer,thickness?} | sphere {radius,scale?} | " +
                "box {min,max,lean?,radius? (rounded),bevelSegments?,puff? (stuffed),divisions?,faceUv?} | strap {path:[[x,y,z]],width,thickness?,around?|outward?,steps?}, " +
                "material 0-7, uvRect? [u0,v0,u1,v1] (a painted atlas instead of the material strip), subdivide? 0-2 (smoothed, creases kept), noise? amount|{amount,scale,seed}, axis? x|y|z (default x), center?, segments?, squash?, caps?}] build a mesh shaded from a materials skin (addTexture materials)\n" +
                "- addMesh also takes uvTile (repeat the texture every N units: UVs projected per triangle, big faces split to stay in the " +
                "-8..8 repeats a vertex holds), uvOffset [u,v], or uvScale (a number or [u,v] times the mesh's own UVs)\n" +
                "- placeInstance / moveInstance scale is a number or [x, y, z] along the prop's axes (clutter: one number)\n" +
                "- hideVisual {archive, model, visual} (it draws nothing; addMesh can fill it again)\n" +
                "- newWeapon {template, name, archive?} (copies the weapon's visuals and GEO definition - LODs, firepoint, aimpoint - to reshape with addMesh / hideVisual)\n" +
                "- importModel {source (package path) | sourceSession, archive, name, targetArchive?, newName?}\n" +
                "- importTexture {source | sourceSession, name, sourceNamespace?, namespace?, newName?}\n" +
                "- addTexture {name, pngBase64 | imagePath | materials:[{color:[r,g,b]}] (up to 8: a 64x64 shaded-strip skin for shapes), namespace?, maxSize?} / addPalette {pngBase64 | imagePath, namespace?, is16?}\n" +
                "- setPalette {archive (a _PAL.ZED), id, rgba:[1024 numbers] | pngBase64 | imagePath} (256 colours in place, keeping the depth; " +
                "the Asset Viewer opens the palette)\n" +
                "- addAnimation / deleteAnimation / renameAnimation / setAnimation / setAnimationKey (package clips)\n" +
                "- placePrefab {prefab, position, yaw?} (every item of a saved prefab around position as one undo step; see assets_list_prefabs)\n" +
                "- batch {label?, ops:[â€¦]} (one undo step)\n" +
                "Positions are map units [x, y, z] (y is height; read nearby instances with assets_map_scene first); yaw is degrees about Y; " +
                "omitted parts keep their values. Indices shift after add / delete, so re-read before chaining. The app follows the edit " +
                "(the Map Builder frames the item, the Model Editor opens the model). dryRun: true checks the edit and answers what it " +
                "would do (archives changed, new indices) without changing anything. Risk: changes the in-memory session only.",
                false, Schema(new[] { "session" }, session,
                    ("op", EditOps.Schema()),
                    P("opJson", "string", "The same edit as JSON text (instead of op)."),
                    P("label", "string", "Optional undo label (defaults to a description of the op)."),
                    P("dryRun", "boolean", "Check the edit and report what it would do, changing nothing.")),
                a =>
                {
                    string id = Session(a);
                    JsonElement op;
                    using var parsed = a.TryGetProperty("op", out var o) && o.ValueKind == JsonValueKind.Object ? null
                        : JsonDocument.Parse(Str(a, "opJson") ?? throw new ArgumentException("\"op\" (an object) or \"opJson\" is required"));
                    op = parsed?.RootElement ?? o;
                    string json = op.GetRawText();
                    if (EditOps.Check(op) is { } invalid) throw new ArgumentException(invalid);
                    bool dryRun = Bool(a, "dryRun");
                    if ((Str(a, "label") is { } label && !op.TryGetProperty("label", out _)) || dryRun)
                    {
                        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
                        if (Str(a, "label") is { } l && !op.TryGetProperty("label", out _)) node["label"] = l;
                        if (dryRun) node["dryRun"] = true;
                        json = node.ToJsonString();
                    }
                    var (ok, data, error) = Command(new CommandMessage { Command = "ASSET_EDIT", AssetSession = id, AssetJson = json });
                    if (!ok || data == null) return ToolResult.Fail(error ?? "ASSET_EDIT failed");
                    if (dryRun) return ToolResult.Text(data, $"dry run: {Str(op, "op")}");
                    using var d = JsonDocument.Parse(data);
                    var root = d.RootElement;
                    var res = root.TryGetProperty("result", out var inner) && inner.ValueKind == JsonValueKind.Object ? inner : root;
                    string? description = res.TryGetProperty("description", out var de) ? de.GetString() : null;
                    var (page, model, focus) = Follow(op, root);
                    Changed(id, page, model, focus, description);
                    return ToolResult.Text(data, description ?? Str(op, "op"));
                });

            AddSync("assets_undo", "Undo",
                "Undo the last edit on a session (whoever made it: the user or you). Risk: changes the in-memory session only.",
                false, Schema(new[] { "session" }, session), a => UndoRedo(a, undo: true));
            AddSync("assets_redo", "Redo",
                "Redo the last undone edit. Risk: changes the in-memory session only.",
                false, Schema(new[] { "session" }, session), a => UndoRedo(a, undo: false));

            AddSync("assets_save", "Save",
                "Write a session's changed archives. mode=zdb writes the package to outputPath (default: over the source, keeping a one-time " +
                ".bak); mode=pack writes a memory-card pack <NAME>.CDB (packName starts with 'm', at most 7 characters) to outputPath and, with " +
                "folderCard (a PCSX2 folder memory card), installs it. For any game, an outputPath ending .iso writes a copy of the disc image " +
                "with the saved files patched in (sourceIso: the image the package came from, unless the session is an .ISO; a file that grows " +
                "moves to the end of the image). Prefer a copy (outputPath) over the retail file, and save only when the user asks. Risk: writes disk.",
                false, Schema(new[] { "session" }, session, E("mode", "zdb (default), pack, GTA model copy (dff), or GTA archive copy (img).", "zdb", "pack", "dff", "img"), P("outputPath", "string", "Output file, or folder for a pack or CA location. GTA requires a separate DFF or IMG file."),
                    P("archive", "string", "GTA source archive from assets_list_models."), P("model", "string", "GTA model to save in dff mode."),
                    P("packName", "string", "pack: the name, e.g. mfrst."), P("folderCard", "string", "pack: optional PCSX2 folder memory card to install into."),
                    P("archiveDir", "string", "pack from an unpacked folder: the files' folder in game, e.g. RUN\\MP\\M51 (default RUN\\MP\\<FOLDER>)."),
                    P("sourceIso", "string", "outputPath *.iso: the disc image the package came from (default: the session's own .ISO)."),
                    P("discPaths", "object", "outputPath *.iso: {fileName: disc path} for a file found in several places on the disc.")),
                a => FromCommand(new CommandMessage
                {
                    Command = "ASSET_SAVE", AssetSession = Session(a),
                    AssetJson = JsonSerializer.Serialize(new { mode = Str(a, "mode") ?? "zdb", outputPath = Str(a, "outputPath"), archive = Str(a, "archive"), model = Str(a, "model"), packName = Str(a, "packName"), folderCard = Str(a, "folderCard"), archiveDir = Str(a, "archiveDir"),
                        sourceIso = Str(a, "sourceIso"), discPaths = a.TryGetProperty("discPaths", out var dp) && dp.ValueKind == JsonValueKind.Object ? dp.Clone() : (JsonElement?)null }, Json),
                }, d => d.TryGetProperty("written", out var w) ? "wrote " + string.Join(", ", w.EnumerateArray().Select(x => Path.GetFileName(x.GetString()))) : null!));

            AddSync("mapbuilder_command", "Run command",
                "Run any backend command directly (the README's command reference: ZIPPER_INSPECT, ZIPPER_READ_NODE, PACKAGE_EDIT, FS_LIST, " +
                "ASSET_EXPORT â€¦). `fields` holds the command's fields (InputPath, OutputPath, AssetSession, AssetArchive, AssetName, AssetJson, â€¦). " +
                "Use the assets_* tools when one fits. Risk: depends on the command (PACKAGE_EDIT and the extract commands write disk).",
                false, Schema(new[] { "command" }, P("command", "string", "Command name, e.g. ZIPPER_INSPECT."), P("fields", "object", "The command's fields.")), a =>
                {
                    string command = Str(a, "command") ?? throw new ArgumentException("\"command\" is required");
                    var msg = a.TryGetProperty("fields", out var f) && f.ValueKind == JsonValueKind.Object
                        ? JsonSerializer.Deserialize<CommandMessage>(f.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new CommandMessage()
                        : new CommandMessage();
                    msg.Command = command;
                    var result = FromCommand(msg);
                    if (!result.IsError && msg.AssetSession != null && command.ToUpperInvariant() is "ASSET_EDIT" or "ASSET_UNDO" or "ASSET_REDO")
                        Changed(msg.AssetSession, null, null, null, command);
                    return result;
                });

            AddSync("assets_close", "Close session",
                "Close an asset session, discarding its unsaved edits (the answer says how many archives were changed). Close only " +
                "sessions you opened, and only when the user agrees to lose unsaved work. Risk: discards in-memory edits.",
                false, Schema(new[] { "session" }, session), a => FromCommand(new CommandMessage { Command = "ASSET_CLOSE", AssetSession = Session(a) },
                    d => d.TryGetProperty("discardedEdits", out var n) ? $"closed, {n.GetInt32()} unsaved archive(s) discarded" : "closed"));

            // â”€â”€ effects and weapons in a session
            AddSync("assets_effects", "Read effects",
                "The package's ZANIM effects (CZANIM.ZAR: muzzle flashes, shell ejection, explosions, smoke): without name, every effect " +
                "with its tracks, the sub-effects it spawns and the package models it uses (filter by query); with name, that effect fully " +
                "decoded (names, node refs, tracks and their instructions). Risk: read-only.",
                true, Schema(new[] { "session" }, session, P("name", "string", "Optional effect to decode fully."), P("query", "string", "Optional name substring (list).")),
                a => Str(a, "name") is { } name
                    ? FromCommand(new CommandMessage { Command = "ASSET_EFFECT", AssetSession = Session(a), AssetName = name }, _ => name)
                    : FromCommand(new CommandMessage { Command = "ASSET_EFFECTS", AssetSession = Session(a), AssetJson = JsonSerializer.Serialize(new { query = Str(a, "query") }, Json) },
                        d => $"{d.GetArrayLength()} effect(s)"));

            AddSync("assets_weapon_fx", "Read weapon effects",
                "What firing a weapon plays: its zweapon.rdr definition (fire animation, fire wait, tracer) and the ZANIM effect tree " +
                "(sub-effects, models such as muzzle flashes and shells, textures, tracks). Needs RUN/ZWEAPON.ZAR beside the package. Risk: read-only.",
                true, Schema(new[] { "session", "weapon" }, session, P("weapon", "string", "Weapon model or internal name (assets_weapons lists them).")),
                a => FromCommand(new CommandMessage { Command = "ASSET_WEAPON_FX", AssetSession = Session(a), AssetName = Str(a, "weapon") }, _ => Str(a, "weapon")!));

            AddSync("assets_export", "Convert a model",
                "Convert the selected GTA: San Andreas or Combined Assault model into a SOCOM II pack, or a SOCOM II model into a Combined Assault ZDB. Reads the current session including edits and returns the written path.",
                false, Schema(new[] { "session", "archive", "model" }, session, P("archive", "string", "Archive leaf, e.g. M51_MDL.ZED."), P("model", "string", "Model name."),
                    P("outputDir", "string", "Optional output folder (default: the app's export folder)."), P("modelName", "string", "Optional name in the new package."),
                    A("clips", "string", "Optional animation clip names to include (characters).")),
                a => FromCommand(new CommandMessage
                {
                    Command = "ASSET_EXPORT", AssetSession = Session(a), AssetArchive = Str(a, "archive"), AssetName = Str(a, "model"),
                    AssetJson = JsonSerializer.Serialize(new { outputDir = Str(a, "outputDir"), modelName = Str(a, "modelName"), clips = Strings(a, "clips") }, Json),
                }, d => d.TryGetProperty("path", out var p) ? "wrote " + Path.GetFileName(p.GetString()) : null!));

            AddSync("assets_entry", "Read package file",
                "A root file of the session's package (an archive such as M51_GEO.ZED, CLUTTER.ZAR or AIMAPS.MPS) with the session's " +
                "unsaved edits: its size, and either written to outputPath or returned as base64 (small files only). Risk: writes disk " +
                "only with outputPath.",
                false, Schema(new[] { "session", "archive" }, session, P("archive", "string", "Root file leaf, e.g. AIMAPS.MPS."),
                    P("outputPath", "string", "Optional file to write the bytes to.")), a =>
                {
                    var (ok, data, error) = Command(new CommandMessage { Command = "ASSET_ENTRY_BYTES", AssetSession = Session(a), AssetArchive = Str(a, "archive") });
                    if (!ok || data == null) return ToolResult.Fail(error ?? "ASSET_ENTRY_BYTES failed");
                    using var d = JsonDocument.Parse(data);
                    byte[] bytes = Convert.FromBase64String(d.RootElement.GetProperty("data").GetString() ?? "");
                    string leaf = d.RootElement.GetProperty("leaf").GetString()!;
                    if (Str(a, "outputPath") is { } output)
                    {
                        string full = Path.GetFullPath(output);
                        CodeDesigner.Ps2.FileFormats.SafeFile.Write(full, bytes);
                        return ToolResult.Text(JsonSerializer.Serialize(new { leaf, size = bytes.Length, written = full }, Json), $"wrote {Path.GetFileName(full)}");
                    }
                    if (bytes.Length * 4 / 3 > MaxText - 200)
                        return ToolResult.Fail($"{leaf} is {bytes.Length} bytes, too large to return; pass outputPath to write it to a file.");
                    return ToolResult.Text(JsonSerializer.Serialize(new { leaf, size = bytes.Length, data = Convert.ToBase64String(bytes) }, Json), $"{leaf}, {bytes.Length} bytes");
                });

            // â”€â”€ files on disk
            AddSync("fs_places", "List drives and folders",
                "The local drives and the user's common folders (Desktop, Documents, Downloads, Home): where to start looking for packages. Risk: read-only.",
                true, Schema(Array.Empty<string>()), _ => FromCommand(new CommandMessage { Command = "FS_PLACES" }));

            AddSync("fs_list", "List folder",
                "A folder's folders and files (name, path, size, modified), to find packages (.ZDB, .CDB, .ZAR, .ZED) to open. A file path " +
                "lists its folder; no path lists the Desktop. Risk: read-only.",
                true, Schema(Array.Empty<string>(), P("path", "string", "Folder (or file) path.")),
                a => FromCommand(new CommandMessage { Command = "FS_LIST", InputPath = Str(a, "path") },
                    d => d.TryGetProperty("entries", out var e) ? $"{e.GetArrayLength()} entr(ies)" : null!));

            AddSync("package_inspect", "Inspect package file",
                "List what a package or archive file on disk holds (a .ZDB / .CDB's root files, a .ZAR / .ZED's records) without opening a " +
                "session: the indices and names package_edit takes. Risk: read-only.",
                true, Schema(new[] { "path" }, P("path", "string", "A .ZDB, .CDB, .ZAR or .ZED.")),
                a => FromCommand(new CommandMessage { Command = "ZIPPER_INSPECT", InputPath = Str(a, "path") ?? throw new ArgumentException("\"path\" is required") }));

            AddSync("package_edit", "Edit package file",
                "Add, replace or remove files of a package on disk, or records of an archive, and write the result (outputPath; over the " +
                "source keeps a one-time .bak). ops: {op: add | replace, path, file} and {op: remove, path} for a .ZDB / .CDB's root files; " +
                "{op: merge, file (.ZDB / .CDB), replace?}; archive records take \"inside\": [root file path, then nested {name, index}] with " +
                "add {name?, file} or replace / remove {index, name}. Not for open sessions (use assets_edit). Prefer an outputPath copy. " +
                "Risk: writes disk.",
                false, Schema(new[] { "path", "ops" }, P("path", "string", "The .ZDB, .CDB, .ZAR or .ZED to change."),
                    P("outputPath", "string", "Where to write (same extension); default over the source."), A("ops", "object", "The operations.", minItems: 1),
                    P("dryRun", "boolean", "Check the operations and report what they would change, writing nothing.")), a =>
                {
                    if (!a.TryGetProperty("ops", out var ops) || ops.ValueKind != JsonValueKind.Array) throw new ArgumentException("\"ops\" (an array) is required");
                    return FromCommand(new CommandMessage { Command = "PACKAGE_EDIT", InputPath = Str(a, "path"), OutputPath = Str(a, "outputPath"), Data = ops.GetRawText(), DryRun = Bool(a, "dryRun") },
                        d => d.TryGetProperty("written", out var w) ? "wrote " + Path.GetFileName(w.GetString())
                            : d.TryGetProperty("wouldWrite", out var ww) ? "dry run: would write " + Path.GetFileName(ww.GetString()) : null!);
                });

            // â”€â”€ weapon archives on disk (a loose *_MDL.ZED / weapon .ZED)
            AddSync("weapons_list", "List weapon models",
                "The models in a weapon archive file (.ZED): name, chunk / vertex / triangle counts and textures, to choose a template. Risk: read-only.",
                true, Schema(new[] { "path" }, P("path", "string", "The weapon archive (.ZED).")),
                a => FromCommand(new CommandMessage { Command = "ZIPPER_LIST_WEAPONS", InputPath = Str(a, "path") },
                    d => d.TryGetProperty("weapons", out var w) ? $"{w.GetArrayLength()} weapon(s)" : null!));

            AddSync("weapons_template", "Read weapon template",
                "One weapon model of an archive in detail: its chunks (index, vertex / triangle counts, bounds, texture) and format notes, " +
                "the chunk indices weapons_create's meshEdits take. Risk: read-only.",
                true, Schema(new[] { "path", "weapon" }, P("path", "string", "The weapon archive (.ZED)."), P("weapon", "string", "Weapon model name.")),
                a => FromCommand(new CommandMessage { Command = "ZIPPER_GET_WEAPON_TEMPLATE", InputPath = Str(a, "path"), WeaponName = Str(a, "weapon") }, _ => Str(a, "weapon")!));

            AddSync("weapons_texture", "View weapon texture",
                "A weapon model's texture (or a texture entry) decoded as an image. Risk: read-only.",
                true, Schema(new[] { "path", "weapon" }, P("path", "string", "The weapon archive (.ZED)."), P("weapon", "string", "Weapon model or texture entry.")), a =>
                {
                    var (ok, data, error) = Command(new CommandMessage { Command = "ZIPPER_WEAPON_TEXTURE_PNG", InputPath = Str(a, "path"), WeaponName = Str(a, "weapon") });
                    if (!ok || data == null) return ToolResult.Fail(error ?? "ZIPPER_WEAPON_TEXTURE_PNG failed");
                    using var d = JsonDocument.Parse(data);
                    var r = new ToolResult { Summary = d.RootElement.TryGetProperty("textureEntry", out var te) ? te.GetString() : Str(a, "weapon") };
                    r.Content.Add(new { type = "image", data = d.RootElement.GetProperty("png").GetString(), mimeType = "image/png" });
                    var info = System.Text.Json.Nodes.JsonNode.Parse(d.RootElement.GetRawText())!.AsObject();
                    info.Remove("png");
                    r.Content.Add(new { type = "text", text = info.ToJsonString() });
                    return r;
                });

            AddSync("weapons_create", "Create weapon",
                "Make a new weapon from a template in a weapon archive: copies the template model's record (visual chains included) " +
                "with its vertices re-encoded (meshEdits: {perChunk: [{chunkIndex, transform?: {scaleX, scaleY, scaleZ, translateX, " +
                "translateY, translateZ}, newTextureName?}]}) as newName, and optionally a new skin from an image (imagePath or " +
                "imageBase64) at the size of the template texture it replaces (the one covering the most triangles), with a palette of " +
                "its own. On the disc layout (WEAP_MDL.ZED with WEAP_TXR.ZED / WEAP_PAL.ZED beside it) the skin goes into the TXR / PAL " +
                "pair and the model's texture fields point at it (textureEntryName, default <newName>.tif, no longer than the replaced " +
                "name); the model archive is written to outputPath (default over the archive) and the changed TXR / PAL beside it. " +
                "Originals are untouched; names in use are refused. The GEO definition (firepoint, aimpoint, LODs) is not made yet. " +
                "dryRun reports every file and entry it would write, writing nothing. Prefer an outputPath copy. Risk: writes disk.",
                false, Schema(new[] { "path", "template", "newName" }, P("path", "string", "The weapon archive (.ZED)."),
                    P("template", "string", "The template weapon model."), P("newName", "string", "The new model entry's name."),
                    P("outputPath", "string", "Where to write the archive (default over the source)."),
                    P("imagePath", "string", "Optional PNG / JPG for the new texture."), P("imageBase64", "string", "Optional PNG as base64 (instead of imagePath)."),
                    P("meshEdits", "object", "Optional {perChunk: [...]} model edits."),
                    P("textureEntryName", "string", "Optional name for the new .tif (default <newName>.tif)."),
                    P("paletteEntryName", "string", "Optional palette entry name; only for a texture inside the model archive (a PAL's palettes are texpal_<id>)."),
                    P("dryRun", "boolean", "Encode and check, writing nothing.")), a =>
                {
                    string? edits = a.TryGetProperty("meshEdits", out var me) && me.ValueKind == JsonValueKind.Object ? me.GetRawText() : null;
                    string path = Str(a, "path") ?? throw new ArgumentException("\"path\" is required");
                    var result = FromCommand(new CommandMessage
                    {
                        Command = "ZIPPER_CREATE_WEAPON", InputPath = path,
                        WeaponName = Str(a, "template") ?? throw new ArgumentException("\"template\" is required"),
                        OutputPath = Str(a, "outputPath"), WeaponNewName = Str(a, "newName"), WeaponMeshEditsJson = edits,
                        WeaponImagePath = Str(a, "imagePath"), WeaponImageData = Str(a, "imageBase64"),
                        WeaponTextureEntryName = Str(a, "textureEntryName"), WeaponPaletteEntryName = Str(a, "paletteEntryName"), DryRun = Bool(a, "dryRun"),
                    }, d => d.TryGetProperty("outputPath", out var o) ? $"{Str(a, "newName")} â†’ {Path.GetFileName(o.GetString())}"
                        : d.TryGetProperty("wouldWrite", out var w) ? $"dry run: {Str(a, "newName")} â†’ {Path.GetFileName(w.GetString())}" : null!);
                    // The new weapon is on disk, outside any session: the Model Editor opens the written archive on it.
                    if (!result.IsError && !Bool(a, "dryRun"))
                    {
                        string written = Path.GetFullPath(Str(a, "outputPath") ?? path);
                        _ui.Publish(new { type = "show", page = "models", path = written, model = new { archive = Path.GetFileName(written), name = Str(a, "newName") } });
                    }
                    return result;
                });

            AddSync("weapons_definition", "Read weapon definition",
                "A weapon's definition as the game reads it: its zweapon.rdr fields (FireWait, Effective_Range, Maximum_Range, " +
                "Ammo_Capacity, NumMags, Sound_Radius, Encumbrance, MaxFireMode, Muzzle_Velocity, ZoomMode0 ...), its ammo types' " +
                "fields (ImpactDamage, Stun, Piercing, Explosion_Damage, Explosion_Radius) and the armory language entries showing " +
                "it (short name, caption), from SOCOM II's RUN folder. Use it to pick a donor and values for weapons_pack. Risk: read-only.",
                true, Schema(new[] { "donor" }, P("donor", "string", "A weapon: InternalName, DisplayName or ModelName (e.g. F57, Model 18, glock18)."),
                    P("socom2Run", "string", "SOCOM II's RUN folder (default: the first drive that has it).")),
                a => FromCommand(new CommandMessage { Command = "ASSET_WEAPON_DEFINITION", AssetJson = a.GetRawText() }, _ => Str(a, "donor")!));

            AddSync("weapons_pack", "Pack custom weapon",
                "Package a session's weapon (e.g. one made with newWeapon + addMesh shapes) as one memory-card pack <PACK>.CDB that " +
                "takes over a donor weapon: RUN\\MP\\<PACK>\\ WEAP_* (the model, its GEO definition, textures, palettes), HUDX_* (a " +
                "128x32 HUD icon drawn from the model), ZWEAPON.ZAR (the disc's weapon table with the donor pointed at the model and " +
                "icon, its name / description and stats set; ammo values go to the donor's own copy of its ammo type) and, when a text " +
                "changes, UIMNLOC.ZAR (the armory's short name, caption and given language keys). Written to outputDir (default " +
                "Documents/CodeDesigner/Exports) with the archives loose beside it; folderCard installs it on a PCSX2 folder card. " +
                "Loading it in game needs the loader code (not part of the pack). dryRun builds everything and writes nothing. Risk: writes disk.",
                false, Schema(new[] { "session", "model", "donor", "pack" }, session,
                    P("model", "string", "The session's weapon model."), P("donor", "string", "The weapon it takes over (weapons_definition shows one)."),
                    P("pack", "string", "Pack name: m + up to 6 letters/digits (mrgun)."),
                    P("name", "string", "Display name (also the armory's short name)."), P("caption", "string", "Armory caption (default: <KIND>: <DESCRIPTION>)."),
                    P("description", "string", "Description (default: the name in capitals)."),
                    P("stats", "object", "Weapon fields to set: {FireWait, Effective_Range, Maximum_Range, Ammo_Capacity, NumMags, Sound_Radius, Encumbrance, MaxFireMode, Muzzle_Velocity, ZoomMode0, RecoilPct ...}."),
                    P("ammo", "object", "Damage on the weapon's own ammo: {ImpactDamage, Stun, Piercing, Explosion_Damage, Explosion_Radius}."),
                    P("texts", "object", "Other language keys to rewrite: {\"_1225_SealPistolHasMags_MSG\": \"...\"}."),
                    P("icon", "boolean", "Draw the HUD icon (default true)."), P("outputDir", "string", "Where the pack goes."),
                    P("folderCard", "string", "A PCSX2 folder memory card to install into."), P("socom2Run", "string", "SOCOM II's RUN folder."),
                    P("dryRun", "boolean", "Build and report, writing nothing.")), a =>
                {
                    var args = System.Text.Json.Nodes.JsonNode.Parse(a.GetRawText())!.AsObject();
                    args.Remove("session"); args.Remove("reason");
                    return FromCommand(new CommandMessage { Command = "ASSET_WEAPON_PACK", AssetSession = Session(a), AssetJson = args.ToJsonString() },
                        d => d.TryGetProperty("pack", out var p) ? $"{(Bool(a, "dryRun") ? "dry run: " : "")}{p.GetString()}.CDB" : null!);
                });

            AddSync("assets_uv_layout", "View UV layout",
                "Where a model draws a texture: the texture with the model's triangles outlined and tinted per body part (a character's " +
                "bones, else its visuals), returned as an image, with each part's colour, triangle count and UV bounds. Use it to see " +
                "which part of a skin is a sleeve, the chest or the face before painting (paintTexture) or drawing a new image " +
                "(replaceTexture). Risk: read-only.",
                true, Schema(new[] { "session", "model", "texture" }, session, P("model", "string", "The model (MESH_seal_A_woodland_LO)."),
                    P("texture", "string", "One of its textures (assets_describe_model lists them)."), P("archive", "string", "The model's *_MDL.ZED (optional).")), a =>
                {
                    var (ok, data, error) = Command(new CommandMessage { Command = "ASSET_UV_LAYOUT", AssetSession = Session(a),
                        AssetJson = JsonSerializer.Serialize(new { model = Str(a, "model"), texture = Str(a, "texture"), archive = Str(a, "archive") }) });
                    if (!ok || data == null) return ToolResult.Fail(error ?? "ASSET_UV_LAYOUT failed");
                    using var d = JsonDocument.Parse(data);
                    var r = new ToolResult { Summary = $"{Str(a, "texture")} on {Str(a, "model")}" };
                    r.Content.Add(new { type = "image", data = d.RootElement.GetProperty("png").GetString(), mimeType = "image/png" });
                    r.Content.Add(new { type = "text", text = d.RootElement.GetProperty("info").GetRawText() });
                    return r;
                });

            AddSync("characters_check", "Check character",
                "What the session's edits did to a skinned character, against the package on disk: where the surface stretched or folded " +
                "(edge length ratios outside 0.6-1.4, by bone and position: a seam, a drooping neck, a wavy limb), vertices whose skin " +
                "weights do not sum to 1, and how far added gear reaches from the body. ok is false when anything needs a look. The " +
                "character edits (addCharacterMesh, scaleBones, transformCharacter) answer the same check; look at it, and at ui_screenshot, " +
                "before calling a reshape done. Risk: read-only.",
                true, Schema(new[] { "session", "model" }, session, P("model", "string", "The character mesh (MESH_seal_A_woodland_LO).")), a =>
                    FromCommand(new CommandMessage { Command = "ASSET_CHARACTER_CHECK", AssetSession = Session(a), AssetJson = JsonSerializer.Serialize(new { model = Str(a, "model") }) },
                        d => d.TryGetProperty("ok", out var ok) ? (ok.GetBoolean() ? "ok" : $"{d.GetProperty("warnings").GetArrayLength()} warning(s)") : null!));

            AddSync("characters_pack", "Pack character",
                "Package a session's character (a skinned MESH_* as edited: addCharacterMesh gear, removeCharacterSection, retexture) as " +
                "one memory-card pack <PACK>.CDB that takes over a donor character name, the layout the CA character ports used in game: " +
                "RUN\\MP\\<PACK>\\CLIB_MDL (the mesh as MESH_<donor>), CLIB_GEO (its skeleton as <donor>), CLIB_TXR (the textures its " +
                "sections draw with) and CLIB_PAL (their palettes, renumbered into the pack's own ids). Name it as a character the map " +
                "does not load (con_pow01, spawned by the mp_pow1 chartype on MP10 / MP11) so that chartype spawns it once the pack is " +
                "loaded. Only the main mesh ships, not the lower-detail one. Written to outputDir (default Documents/CodeDesigner/Exports) " +
                "with the archives loose beside it; folderCard installs it on a PCSX2 folder card. dryRun builds and writes nothing. Risk: writes disk.",
                false, Schema(new[] { "session", "model", "donor", "pack" }, session,
                    P("model", "string", "The session's character mesh (MESH_seal_A_woodland_LO)."),
                    P("donor", "string", "The character name it takes (con_pow01)."),
                    P("pack", "string", "Pack name: m + up to 6 letters/digits (mseal1)."), P("outputDir", "string", "Where the pack goes."),
                    P("folderCard", "string", "A PCSX2 folder memory card to install into."), P("dryRun", "boolean", "Build and report, writing nothing.")), a =>
                {
                    var args = System.Text.Json.Nodes.JsonNode.Parse(a.GetRawText())!.AsObject();
                    args.Remove("session"); args.Remove("reason");
                    return FromCommand(new CommandMessage { Command = "ASSET_CHARACTER_PACK", AssetSession = Session(a), AssetJson = args.ToJsonString() },
                        d => d.TryGetProperty("pack", out var p) ? $"{(Bool(a, "dryRun") ? "dry run: " : "")}{p.GetString()}.CDB" : null!);
                });

            // â”€â”€ the app
            Add("ui_screenshot", "Screenshot",
                "A screenshot of the 3D view on the page the user has in front (Map Builder, Model Editor, Animation Editor or Asset Viewer), to " +
                "check your work as the user sees it. Needs the app open in a browser. Risk: read-only.",
                true, Schema(Array.Empty<string>(), N("maxSize", "integer", "Longest side in pixels (default 1280).", minimum: 64, maximum: 2048)), async a =>
                {
                    var data = await _ui.Request("screenshot", new { maxSize = Math.Clamp(IntOpt(a, "maxSize") ?? 1280, 64, 2048) }, TimeSpan.FromSeconds(15));
                    string png = data.TryGetProperty("png", out var p) ? p.GetString() ?? "" : "";
                    if (png.Length == 0) return ToolResult.Fail("The page sent no image (no 3D view on screen?).");
                    var r = new ToolResult { Summary = data.TryGetProperty("page", out var pg) ? pg.GetString() : null };
                    r.Content.Add(new { type = "image", data = png, mimeType = "image/png" });
                    data.TryGetProperty("width", out var w);
                    data.TryGetProperty("height", out var h);
                    r.Content.Add(new { type = "text", text = $"page {r.Summary}, {w}Ã—{h}" });
                    return r;
                });

            Add("ui_state", "Read app state",
                "What the app shows: the page in front, the package and session each page has open, the Map Builder's selection and the " +
                "Model Editor's model, view settings, and whether the page follows your edits. Risk: read-only.",
                true, Schema(Array.Empty<string>()), async _ =>
                {
                    var data = await _ui.Request("state", null, TimeSpan.FromSeconds(10));
                    return ToolResult.Text(JsonSerializer.Serialize(new { pages = _ui.ClientCount, state = data }, Json), "read");
                });

            Add("ui_show", "Show page",
                "Bring a page to the front, optionally opening a package (by session or path) and a model there: map (Map Builder), models " +
                "(Model Editor, with archive + model), animations (Animation Editor), viewer (Asset Viewer). For a model it waits until the " +
                "Model Editor has it open and fails with the page's own error when it could not open it. Risk: read-only.",
                true, Schema(new[] { "page" }, E("page", "map, models, animations or viewer.", "map", "models", "animations", "viewer"), P("session", "string", "Optional session to show."),
                    P("path", "string", "Optional package path (instead of session)."), P("archive", "string", "Model Editor: the model's archive."),
                    P("model", "string", "Model Editor: the model.")), async a =>
                {
                    string page = Str(a, "page") ?? throw new ArgumentException("\"page\" is required");
                    if (page is not ("map" or "models" or "animations" or "viewer")) throw new ArgumentException("page is map, models, animations or viewer");
                    string? path = Str(a, "path");
                    if (path == null && Str(a, "session") is { } sid) path = Status(sid) is { } st && st.TryGetProperty("path", out var p) ? p.GetString() : throw new ArgumentException($"No session '{sid}'");
                    string? archive = Str(a, "archive"), name = Str(a, "model");
                    object? model = archive != null && name != null ? new { archive, name } : null;
                    _ui.Publish(new { type = "show", page, path, model, force = true });
                    if (_ui.ClientCount == 0) return ToolResult.Text("No page is open; nothing shown.", page);
                    if (page != "models" || model == null) return ToolResult.Text($"Showing {page}.", page);
                    return await ModelShown(path, archive!, name!);
                });

            AddSync("ui_focus", "Focus item",
                "Select and frame an item in the Map Builder (bringing it forward): a placed instance by geoIndex, a clutter item by clutterIndex " +
                "(from assets_map_scene), a CA instance by its string id, or a map point by name. Risk: read-only.",
                true, Schema(new[] { "session" }, session, P("geoIndex", "integer", "A placed instance."), P("instance", "string", "A CA instance id."), P("clutterIndex", "integer", "A clutter item."), P("point", "string", "A map point name.")), a =>
                {
                    string id = Session(a);
                    string? path = Status(id) is { } st && st.TryGetProperty("path", out var p) ? p.GetString() : throw new ArgumentException($"No session '{id}'");
                    var focus = new { geoIndex = IntOpt(a, "geoIndex"), instance = Str(a, "instance"), clutterIndex = IntOpt(a, "clutterIndex"), point = Str(a, "point") };
                    if (focus.geoIndex == null && focus.instance == null && focus.clutterIndex == null && focus.point == null) throw new ArgumentException("geoIndex, instance, clutterIndex or point is required");
                    _ui.Publish(new { type = "show", page = "map", path, focus, force = true });
                    return ToolResult.Text("Focused.", focus.point ?? focus.instance ?? (focus.geoIndex != null ? $"geo #{focus.geoIndex}" : $"clutter #{focus.clutterIndex}"));
                });

            AddSync("ui_view", "Change view",
                "Change the view on the page in front (Map Builder or Model Editor): shading (textured, solid, wireframe, xray), lighting (baked, " +
                "studio, unlit), backdrop (dark, blueprint), a camera preset (front, back, left, right, top, bottom, iso), frame (selected, all), " +
                "then orbit [yaw, pitch] degrees about the view's centre (yaw turns right, pitch looks more from above) and zoom (a distance " +
                "factor: 0.5 twice as close, 2 twice as far). Risk: read-only.",
                true, Schema(Array.Empty<string>(), E("shading", "Shading.", "textured", "solid", "wireframe", "xray"), E("lighting", "Lighting.", "baked", "studio", "unlit"),
                    E("backdrop", "Backdrop.", "dark", "blueprint"), E("view", "Camera preset.", "front", "back", "left", "right", "top", "bottom", "iso"),
                    E("frame", "Frame the selection or the whole scene.", "selected", "all"),
                    A("orbit", "number", "[yaw, pitch] degrees.", minItems: 2, maxItems: 2), N("zoom", "number", "Distance factor (< 1 closer).", exclusiveMinimum: 0)), a =>
                {
                    float[]? orbit = a.TryGetProperty("orbit", out var o) && o.ValueKind == JsonValueKind.Array ? o.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetSingle()).ToArray() : null;
                    if (orbit is { Length: not 2 }) throw new ArgumentException("\"orbit\" is [yaw, pitch]");
                    float? zoom = a.TryGetProperty("zoom", out var z) && z.ValueKind == JsonValueKind.Number ? z.GetSingle() : null;
                    if (zoom is <= 0) throw new ArgumentException("\"zoom\" is a positive factor");
                    _ui.Publish(new { type = "view", shading = Str(a, "shading"), lighting = Str(a, "lighting"), backdrop = Str(a, "backdrop"), view = Str(a, "view"), frame = Str(a, "frame"), orbit, zoom });
                    var parts = new[] { "shading", "lighting", "backdrop", "view", "frame" }.Select(k => Str(a, k)).Where(v => v != null).ToList();
                    if (orbit != null) parts.Add($"orbit {orbit[0]}Â°, {orbit[1]}Â°");
                    if (zoom != null) parts.Add($"zoom Ã—{zoom}");
                    return ToolResult.Text("View changed.", string.Join(", ", parts));
                });

            tools.AddRange(BuildViewerTools());
            return tools;
        }

        private ToolResult UndoRedo(JsonElement a, bool undo)
        {
            string id = Session(a);
            var (ok, data, error) = Command(new CommandMessage { Command = undo ? "ASSET_UNDO" : "ASSET_REDO", AssetSession = id });
            if (!ok || data == null) return ToolResult.Fail(error ?? "failed");
            using var d = JsonDocument.Parse(data);
            string? description = d.RootElement.TryGetProperty("description", out var de) ? de.GetString() : null;
            Changed(id, null, null, null, $"{(undo ? "undo" : "redo")} {description}");
            return ToolResult.Text(data, $"{(undo ? "undid" : "redid")} {description}");
        }

        // ------------------------------------------------------------------ schema / argument helpers

        private static (string Name, Dictionary<string, object> Schema) P(string name, string type, string description) =>
            (name, new Dictionary<string, object> { ["type"] = type, ["description"] = description });

        /// <summary>A string that takes one of <paramref name="values"/>.</summary>
        private static (string Name, Dictionary<string, object> Schema) E(string name, string description, params string[] values) =>
            (name, new Dictionary<string, object> { ["type"] = "string", ["description"] = description, ["enum"] = values });

        /// <summary>A number or integer with optional bounds.</summary>
        private static (string Name, Dictionary<string, object> Schema) N(string name, string type, string description, double? minimum = null, double? maximum = null, double? exclusiveMinimum = null)
        {
            var d = new Dictionary<string, object> { ["type"] = type, ["description"] = description };
            if (minimum is { } lo) d["minimum"] = lo;
            if (maximum is { } hi) d["maximum"] = hi;
            if (exclusiveMinimum is { } x) d["exclusiveMinimum"] = x;
            return (name, d);
        }

        /// <summary>An array of <paramref name="itemType"/> items, optionally of a fixed size range.</summary>
        private static (string Name, Dictionary<string, object> Schema) A(string name, string itemType, string description, int? minItems = null, int? maxItems = null)
        {
            var d = new Dictionary<string, object> { ["type"] = "array", ["description"] = description, ["items"] = new Dictionary<string, object> { ["type"] = itemType } };
            if (minItems is { } lo) d["minItems"] = lo;
            if (maxItems is { } hi) d["maxItems"] = hi;
            return (name, d);
        }

        // Every tool takes `reason`: one line on why, shown in the app's AI activity panel.
        private static Dictionary<string, object> Schema(string[] required, params (string Name, Dictionary<string, object> Schema)[] props)
        {
            var properties = props.ToDictionary(p => p.Name, p => (object)p.Schema);
            properties["reason"] = new Dictionary<string, object>
            {
                ["type"] = "string",
                ["description"] = "One line: why you are making this call and what you expect to learn or change. Shown to the user in the app's AI activity panel.",
            };
            return new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = required.Append("reason").ToArray(),
                ["additionalProperties"] = false,
            };
        }

        private static string? Str(JsonElement a, string name) =>
            a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

        private static int? IntOpt(JsonElement a, string name) =>
            a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (int)v.GetDouble() : null;

        private static string[]? Strings(JsonElement a, string name) =>
            a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
                ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray() : null;

        private static bool Bool(JsonElement a, string name) =>
            a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
    }
}
