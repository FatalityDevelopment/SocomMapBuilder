using System.Diagnostics;
using System.Text.Json;

namespace MapBuilder.Server.Services
{
    /// <summary>
    /// A Model Context Protocol server on POST /mcp (Streamable HTTP, JSON replies): the assets_* tools CodeDesigner's
    /// MCP has, run against the same asset sessions the pages edit, plus ui_* tools that drive and look at the pages.
    /// Every call is announced on the <see cref="UiBridge"/> (tool, reason, result), and edits tell the pages what
    /// changed so they follow along: the Map Builder reloads and frames the moved prop, the Model Editor opens the
    /// edited model.
    /// </summary>
    internal sealed class McpServer
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
            "(undoable, in memory) and write with assets_save only when the user asks. The user watches the app while you work: " +
            "every call's `reason` is shown in its AI activity panel, and edits make the page follow (Map Builder reloads and frames " +
            "the edited item, the Model Editor opens the edited model). ui_screenshot shows you what the user sees; ui_show, " +
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
                r.Content.Add(new { type = "text", text = text.Length > MaxText ? text[..MaxText] + $"\n… (cut at {MaxText} of {text.Length} characters; narrow the query)" : text });
                return r;
            }

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

        private async Task<object?> Dispatch(JsonElement msg)
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
                            annotations = new { title = t.Label, readOnlyHint = t.ReadOnly },
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
            try { result = await tool.Run(args); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException or TimeoutException or JsonException or FormatException)
            {
                result = ToolResult.Fail(ex.Message);
            }
            _ui.Publish(new
            {
                type = "activity", id = callId, phase = result.IsError ? "error" : "done", tool = tool.Name, label = tool.Label, reason,
                summary = result.Summary, ms = sw.ElapsedMilliseconds,
            });
            return result;
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
                case "moveInstance": return ("map", null, new { geoIndex = IntOpt(op, "geoIndex") });
                case "duplicateInstance": return ("map", null, new { geoIndex = R("newGeoIndex") });
                case "deleteClutter":
                case "deleteInstance": return ("map", null, null);
                case "movePoint": return ("map", null, new { point = Str(op, "name") });
                case "addAnimation":
                case "deleteAnimation":
                case "renameAnimation":
                case "setAnimation":
                case "setAnimationKey": return ("animations", null, null);
                case "importModel":
                {
                    string? archive = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("archive", out var ar) ? ar.GetString() : null;
                    string? name = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("name", out var nm) ? nm.GetString() : null;
                    return archive != null && name != null ? ("models", new { archive, name }, null) : ("models", null, null);
                }
            }
            if (Str(op, "archive") is { } a && Str(op, "model") is { } model) return ("models", new { archive = a, name = model }, null);
            return (null, null, null);
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

            // ── sessions
            AddSync("assets_open", "Open package",
                "Open a SOCOM II package (a .ZDB / .CDB, a loose .ZED / .ZAR, or an unpacked package folder; SOCOM 3 / Combined Assault and " +
                "GTA: San Andreas open read-only) as an asset session, or join the one already open on it (the pages share it). Returns the " +
                "session id and a summary. The app opens it too: in the Map Builder when it is a map, else the Model Editor. Risk: read-only.",
                true, Schema(new[] { "path" }, P("path", "string", "Path to the package or its unpacked folder.")), a =>
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
                    return ToolResult.Text(data, $"{Path.GetFileName(full)} → session {id}");
                });

            AddSync("assets_sessions", "List sessions",
                "List the open asset sessions (including the one the user has open in the app): id, path, revision, changed archives, undo. Risk: read-only.",
                true, Schema(Array.Empty<string>()), _ => FromCommand(new CommandMessage { Command = "ASSET_SESSIONS" },
                    d => $"{d.GetArrayLength()} session(s)"));

            AddSync("assets_status", "Session status",
                "A session's status: path, revision, changed (unsaved) archives, the undo / redo descriptions, model counts by kind. Risk: read-only.",
                true, Schema(new[] { "session" }, session), a => FromCommand(new CommandMessage { Command = "ASSET_STATUS", AssetSession = Session(a) }));

            // ── reading
            AddSync("assets_list_models", "List models",
                "The package's models: archive, name, kind (prop, library, character, weapon, vehicle, world …), namespace, size. Filter by kind, " +
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
                "name / path substring; limit defaults to 500. Risk: read-only.",
                true, Schema(new[] { "session" }, session, P("kind", "string", "Optional kind: model, reference, visuals, clutter."),
                    P("query", "string", "Optional model name or scene path substring."), P("limit", "integer", "Maximum instances (default 500).")),
                a => FromCommand(new CommandMessage
                {
                    Command = "ASSET_MAP_SCENE", AssetSession = Session(a),
                    AssetJson = JsonSerializer.Serialize(new { kind = Str(a, "kind"), query = Str(a, "query"), limit = IntOpt(a, "limit") }, Json),
                }, d => d.TryGetProperty("instances", out var i) ? $"{i.GetArrayLength()} instance(s)" : null!));

            AddSync("assets_map_points", "Read map points",
                "The AIMAPS spawn and waypoint points (name, team, kind, x, z, grid cell) and the AI grid's bounds. Risk: read-only.",
                true, Schema(new[] { "session" }, session), a => FromCommand(new CommandMessage { Command = "ASSET_MAP_POINTS", AssetSession = Session(a) },
                    d => d.TryGetProperty("points", out var p) ? $"{p.GetArrayLength()} point(s)" : null!));

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

            // ── editing
            AddSync("assets_edit", "Edit",
                "Apply one edit to a session (undoable; nothing is written to disk until assets_save). `op` is an object with \"op\":\n" +
                "- moveClutter {clutterIndex, position?, yaw?, scale?, matrix?}\n" +
                "- addClutter {model, position, yaw?, scale?} (answers newClutterIndex)\n" +
                "- deleteClutter {clutterIndex}\n" +
                "- moveInstance {geoIndex, position?, yaw?, scale?, matrix?} (world space)\n" +
                "- duplicateInstance {geoIndex, position?, yaw?, scale?, name?} (answers newGeoIndex)\n" +
                "- deleteInstance {geoIndex}\n" +
                "- movePoint {name, position:[x,y,z]} (an AIMAPS point; snaps to its grid cell)\n" +
                "- replaceTexture {texture, pngBase64 | imagePath, namespace?}\n" +
                "- retexture {archive, model, from, to, visual?}\n" +
                "- tintVertexColors {archive, model, multiply:[r,g,b], add?, visual?, texture?, alpha?}\n" +
                "- transformVertices {archive, model, translate?, rotate? (degrees), scale?, pivot?, chunks?, vertices?, matrix?}\n" +
                "- setVertices {archive, model, geometryOffset, vertices:[{i, position?, uv?, color?, normal?}]}\n" +
                "- deleteFaces / duplicateFaces {archive, model, faces:{geometryOffset:[triangle…]}, …}\n" +
                "- addMesh {archive, model, visual:\"N000_000\", texture, replace?, obj | primitive:{kind:cube|plane, size, center} | mesh}\n" +
                "- importModel {source (package path) | sourceSession, archive, name, targetArchive?, newName?}\n" +
                "- importTexture {source | sourceSession, name, sourceNamespace?, namespace?, newName?}\n" +
                "- addTexture {name, pngBase64 | imagePath, namespace?, maxSize?} / addPalette {pngBase64 | imagePath, namespace?, is16?}\n" +
                "- addAnimation / deleteAnimation / renameAnimation / setAnimation / setAnimationKey (package clips)\n" +
                "- batch {label?, ops:[…]} (one undo step)\n" +
                "Positions are map units [x, y, z] (y is height; read nearby instances with assets_map_scene first); yaw is degrees about Y; " +
                "omitted parts keep their values. Indices shift after add / delete, so re-read before chaining. The app follows the edit " +
                "(the Map Builder frames the item, the Model Editor opens the model). Risk: changes the in-memory session only.",
                false, Schema(new[] { "session" }, session,
                    P("op", "object", "The edit: an object with \"op\" (see the description)."),
                    P("opJson", "string", "The same edit as JSON text (instead of op)."),
                    P("label", "string", "Optional undo label (defaults to a description of the op).")),
                a =>
                {
                    string id = Session(a);
                    JsonElement op;
                    using var parsed = a.TryGetProperty("op", out var o) && o.ValueKind == JsonValueKind.Object ? null
                        : JsonDocument.Parse(Str(a, "opJson") ?? throw new ArgumentException("\"op\" (an object) or \"opJson\" is required"));
                    op = parsed?.RootElement ?? o;
                    string json = op.GetRawText();
                    if (Str(a, "label") is { } label && !op.TryGetProperty("label", out _))
                    {
                        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
                        node["label"] = label;
                        json = node.ToJsonString();
                    }
                    var (ok, data, error) = Command(new CommandMessage { Command = "ASSET_EDIT", AssetSession = id, AssetJson = json });
                    if (!ok || data == null) return ToolResult.Fail(error ?? "ASSET_EDIT failed");
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
                "folderCard (a PCSX2 folder memory card), installs it. Prefer a copy (outputPath) over the retail file, and save only when the " +
                "user asks. Risk: writes disk.",
                false, Schema(new[] { "session" }, session, P("mode", "string", "zdb (default) or pack."), P("outputPath", "string", "zdb: output .ZDB path. pack: output folder."),
                    P("packName", "string", "pack: the name, e.g. mfrst."), P("folderCard", "string", "pack: optional PCSX2 folder memory card to install into.")),
                a => FromCommand(new CommandMessage
                {
                    Command = "ASSET_SAVE", AssetSession = Session(a),
                    AssetJson = JsonSerializer.Serialize(new { mode = Str(a, "mode") ?? "zdb", outputPath = Str(a, "outputPath"), packName = Str(a, "packName"), folderCard = Str(a, "folderCard") }, Json),
                }, d => d.TryGetProperty("written", out var w) ? "wrote " + string.Join(", ", w.EnumerateArray().Select(x => Path.GetFileName(x.GetString()))) : null!));

            AddSync("mapbuilder_command", "Run command",
                "Run any backend command directly (the README's command reference: ZIPPER_INSPECT, ZIPPER_READ_NODE, PACKAGE_EDIT, FS_LIST, " +
                "ASSET_EXPORT …). `fields` holds the command's fields (InputPath, OutputPath, AssetSession, AssetArchive, AssetName, AssetJson, …). " +
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

            // ── the app
            Add("ui_screenshot", "Screenshot",
                "A screenshot of the 3D view on the page the user has in front (Map Builder, Model Editor, Animation Editor or Asset Viewer), to " +
                "check your work as the user sees it. Needs the app open in a browser. Risk: read-only.",
                true, Schema(Array.Empty<string>(), P("maxSize", "integer", "Longest side in pixels (default 1280).")), async a =>
                {
                    var data = await _ui.Request("screenshot", new { maxSize = IntOpt(a, "maxSize") ?? 1280 }, TimeSpan.FromSeconds(15));
                    string png = data.TryGetProperty("png", out var p) ? p.GetString() ?? "" : "";
                    if (png.Length == 0) return ToolResult.Fail("The page sent no image (no 3D view on screen?).");
                    var r = new ToolResult { Summary = data.TryGetProperty("page", out var pg) ? pg.GetString() : null };
                    r.Content.Add(new { type = "image", data = png, mimeType = "image/png" });
                    data.TryGetProperty("width", out var w);
                    data.TryGetProperty("height", out var h);
                    r.Content.Add(new { type = "text", text = $"page {r.Summary}, {w}×{h}" });
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

            AddSync("ui_show", "Show page",
                "Bring a page to the front, optionally opening a package (by session or path) and a model there: map (Map Builder), models " +
                "(Model Editor, with archive + model), animations (Animation Editor), viewer (Asset Viewer). Risk: read-only.",
                true, Schema(new[] { "page" }, P("page", "string", "map, models, animations or viewer."), P("session", "string", "Optional session to show."),
                    P("path", "string", "Optional package path (instead of session)."), P("archive", "string", "Model Editor: the model's archive."),
                    P("model", "string", "Model Editor: the model.")), a =>
                {
                    string page = Str(a, "page") ?? throw new ArgumentException("\"page\" is required");
                    if (page is not ("map" or "models" or "animations" or "viewer")) throw new ArgumentException("page is map, models, animations or viewer");
                    string? path = Str(a, "path");
                    if (path == null && Str(a, "session") is { } sid) path = Status(sid) is { } st && st.TryGetProperty("path", out var p) ? p.GetString() : throw new ArgumentException($"No session '{sid}'");
                    object? model = Str(a, "archive") is { } ar && Str(a, "model") is { } m ? new { archive = ar, name = m } : null;
                    _ui.Publish(new { type = "show", page, path, model, force = true });
                    return ToolResult.Text(_ui.ClientCount > 0 ? $"Showing {page}." : "No page is open; nothing shown.", page);
                });

            AddSync("ui_focus", "Focus item",
                "Select and frame an item in the Map Builder (bringing it forward): a placed instance by geoIndex, a clutter item by clutterIndex " +
                "(from assets_map_scene) or an AIMAPS point by name. Risk: read-only.",
                true, Schema(new[] { "session" }, session, P("geoIndex", "integer", "A placed instance."), P("clutterIndex", "integer", "A clutter item."), P("point", "string", "An AIMAPS point name.")), a =>
                {
                    string id = Session(a);
                    string? path = Status(id) is { } st && st.TryGetProperty("path", out var p) ? p.GetString() : throw new ArgumentException($"No session '{id}'");
                    var focus = new { geoIndex = IntOpt(a, "geoIndex"), clutterIndex = IntOpt(a, "clutterIndex"), point = Str(a, "point") };
                    if (focus.geoIndex == null && focus.clutterIndex == null && focus.point == null) throw new ArgumentException("geoIndex, clutterIndex or point is required");
                    _ui.Publish(new { type = "show", page = "map", path, focus, force = true });
                    return ToolResult.Text("Focused.", focus.point ?? (focus.geoIndex != null ? $"geo #{focus.geoIndex}" : $"clutter #{focus.clutterIndex}"));
                });

            AddSync("ui_view", "Change view",
                "Change the view on the page in front (Map Builder or Model Editor): shading (textured, solid, wireframe, xray), lighting (baked, " +
                "studio, unlit), backdrop (dark, blueprint), a camera preset (front, back, left, right, top, bottom, iso), or frame (selected, all). Risk: read-only.",
                true, Schema(Array.Empty<string>(), P("shading", "string", "textured, solid, wireframe or xray."), P("lighting", "string", "baked, studio or unlit."),
                    P("backdrop", "string", "dark or blueprint."), P("view", "string", "front, back, left, right, top, bottom or iso."), P("frame", "string", "selected or all.")), a =>
                {
                    _ui.Publish(new { type = "view", shading = Str(a, "shading"), lighting = Str(a, "lighting"), backdrop = Str(a, "backdrop"), view = Str(a, "view"), frame = Str(a, "frame") });
                    return ToolResult.Text("View changed.", string.Join(", ", new[] { "shading", "lighting", "backdrop", "view", "frame" }.Select(k => Str(a, k)).Where(v => v != null)));
                });

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
            };
        }

        private static string? Str(JsonElement a, string name) =>
            a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

        private static int? IntOpt(JsonElement a, string name) =>
            a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (int)v.GetDouble() : null;

        private static bool Bool(JsonElement a, string name) =>
            a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
    }
}
