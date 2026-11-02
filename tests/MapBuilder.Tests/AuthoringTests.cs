using System.Text.Json;
using MapBuilder.Server;
using MapBuilder.Server.Services;
using MapBuilder.Tests.Fixtures;
using Xunit;

namespace MapBuilder.Tests
{
    /// <summary>
    /// What an AI building maps leans on: strict tool arguments, dry runs, the edit history, map validation and
    /// paging through a scene.
    /// </summary>
    public sealed class AuthoringTests : IDisposable
    {
        private readonly SyntheticPackage _pkg = new();
        private readonly Backend _b = new();
        private readonly McpServer _mcp;
        private readonly List<string> _sessions = new();
        private int _id;

        public AuthoringTests() => _mcp = new McpServer(_b.Registry, new UiBridge());

        public void Dispose()
        {
            foreach (string s in _sessions) _b.Close(s);
            _pkg.Dispose();
        }

        private string Open(string path)
        {
            string s = _b.Open(path);
            _sessions.Add(s);
            return s;
        }

        private JsonElement Rpc(string method, object? @params)
        {
            string text = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = ++_id, method, @params });
            using var doc = JsonDocument.Parse(text);
            object? reply = _mcp.Dispatch(doc.RootElement).GetAwaiter().GetResult();
            return JsonDocument.Parse(JsonSerializer.Serialize(reply)).RootElement.GetProperty("result").Clone();
        }

        private JsonElement Call(string tool, object args) => Rpc("tools/call", new { name = tool, arguments = args });

        private static bool IsError(JsonElement r) => r.GetProperty("isError").GetBoolean();

        private static string Text(JsonElement r) => r.GetProperty("content")[0].GetProperty("text").GetString()!;

        // ------------------------------------------------------------------ strict arguments

        [Theory]
        [InlineData("{\"session\":\"x\"}", "\"reason\" is required")]
        [InlineData("{\"session\":\"x\",\"reason\":\"  \"}", "\"reason\" is required")]
        [InlineData("{\"session\":\"x\",\"reason\":\"r\",\"sesion\":\"typo\"}", "unknown argument \"sesion\"")]
        [InlineData("{\"session\":\"x\",\"reason\":\"r\",\"limit\":\"10\"}", "\"limit\" must be an integer")]
        [InlineData("{\"session\":\"x\",\"reason\":\"r\",\"limit\":2.5}", "\"limit\" must be an integer")]
        [InlineData("{\"session\":\"x\",\"reason\":\"r\",\"limit\":0}", "at least 1")]
        [InlineData("{\"session\":\"x\",\"reason\":\"r\",\"kind\":\"props\"}", "one of model, reference, visuals, clutter")]
        public void Map_scene_arguments_are_checked(string args, string error)
        {
            var r = Call("assets_map_scene", JsonDocument.Parse(args).RootElement);
            Assert.True(IsError(r));
            Assert.Contains(error, Text(r));
        }

        [Fact]
        public void View_arguments_are_checked()
        {
            Assert.Contains("one of textured", Text(Call("ui_view", new { reason = "r", shading = "glossy" })));
            Assert.Contains("at least 2 items", Text(Call("ui_view", new { reason = "r", orbit = new[] { 10 } })));
            Assert.Contains("must be a number", Text(Call("ui_view", new { reason = "r", orbit = new object[] { 10, "up" } })));
            Assert.Contains("more than 0", Text(Call("ui_view", new { reason = "r", zoom = 0 })));
            Assert.False(IsError(Call("ui_view", new { reason = "r", shading = "solid", orbit = new[] { 10, 5 }, zoom = 0.5 })));
        }

        [Fact]
        public void Tools_declare_closed_schemas_and_hints()
        {
            var list = Rpc("tools/list", null).GetProperty("tools").EnumerateArray().ToDictionary(t => t.GetProperty("name").GetString()!);
            foreach (var t in list.Values) Assert.False(t.GetProperty("inputSchema").GetProperty("additionalProperties").GetBoolean());
            Assert.True(list["assets_save"].GetProperty("annotations").GetProperty("destructiveHint").GetBoolean());
            Assert.False(list["assets_edit"].GetProperty("annotations").GetProperty("destructiveHint").GetBoolean());
            Assert.False(list["assets_open"].GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
            Assert.True(list["assets_validate"].GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
            Assert.True(list.ContainsKey("assets_history"));
        }

        // ------------------------------------------------------------------ dry run

        [Fact]
        public void A_dry_run_answers_what_it_would_do_and_changes_nothing()
        {
            string s = Open(_pkg.Folder);
            _b.Edit(s, new { op = "moveClutter", clutterIndex = 0, position = new[] { 5f, 0, 5 } });
            _b.Undo(s);
            var before = _b.Status(s);
            byte[] clutter = Backend.Bytes(s, "CLUTTER.ZAR");

            var r = Call("assets_edit", new { session = s, reason = "r", dryRun = true, op = new { op = "addClutter", model = "tree", position = new[] { 1f, 0, 1 } } });
            Assert.False(IsError(r), Text(r));
            var answer = JsonDocument.Parse(Text(r)).RootElement;
            Assert.True(answer.GetProperty("dryRun").GetBoolean());
            Assert.Equal(2, answer.GetProperty("wouldDo").GetProperty("newClutterIndex").GetInt32());

            var after = _b.Status(s);
            Assert.Equal(before.GetProperty("revision").GetInt32(), after.GetProperty("revision").GetInt32());
            Assert.Equal(clutter, Backend.Bytes(s, "CLUTTER.ZAR"));
            Assert.Empty(after.GetProperty("dirty").EnumerateArray());
            // The redo the user had is still there.
            Assert.Equal(before.GetProperty("canRedo").GetString(), after.GetProperty("canRedo").GetString());
        }

        [Fact]
        public void A_dry_run_reports_the_error_a_real_edit_would_hit()
        {
            string s = Open(_pkg.Folder);
            string error = _b.EditFails(s, new { op = "batch", dryRun = true, ops = new object[] { new { op = "deleteClutter", clutterIndex = 0 }, new { op = "deleteClutter", clutterIndex = 7 } } });
            Assert.Contains("out of range", error);
            Assert.Equal(2, _b.Scene(s).GetProperty("instances").EnumerateArray().Count(i => i.GetProperty("kind").GetString() == "clutter"));
        }

        [Fact]
        public void Package_edit_dry_run_writes_nothing()
        {
            string zdb = _pkg.WriteZdb();
            byte[] original = File.ReadAllBytes(zdb);
            var r = _b.Ok(new CommandMessage
            {
                Command = "PACKAGE_EDIT", InputPath = zdb, DryRun = true,
                Data = JsonSerializer.Serialize(new object[] { new { op = "remove", path = @"RUN\MP\TST\AIMAPS.MPS" } }),
            });
            Assert.True(r.GetProperty("dryRun").GetBoolean());
            Assert.Single(r.GetProperty("removed").EnumerateArray());
            Assert.Equal(original, File.ReadAllBytes(zdb));
            Assert.False(File.Exists(zdb + ".bak"));
        }

        // ------------------------------------------------------------------ history

        [Fact]
        public void History_lists_undo_and_redo_most_recent_first()
        {
            string s = Open(_pkg.Folder);
            _b.Edit(s, new { op = "moveClutter", clutterIndex = 0, position = new[] { 5f, 0, 5 }, label = "first" });
            _b.Edit(s, new { op = "movePoint", name = "Alpha1", position = new[] { 55f, 0, 75 }, label = "second" });
            _b.Edit(s, new { op = "moveClutter", clutterIndex = 1, position = new[] { 6f, 0, 6 }, label = "third" });
            _b.Undo(s);
            var h = _b.Ok(new CommandMessage { Command = "ASSET_HISTORY", AssetSession = s });
            Assert.Equal(new[] { "second", "first" }, h.GetProperty("undo").EnumerateArray().Select(u => u.GetProperty("description").GetString()));
            Assert.Equal("third", h.GetProperty("redo")[0].GetProperty("description").GetString());
            Assert.Equal("AIMAPS.MPS", h.GetProperty("undo")[0].GetProperty("archives")[0].GetString());
        }

        // ------------------------------------------------------------------ validation

        private JsonElement Validate(string s, object? args = null) =>
            _b.Ok(new CommandMessage { Command = "ASSET_VALIDATE", AssetSession = s, AssetJson = args == null ? null : JsonSerializer.Serialize(args) });

        private static List<string> Codes(JsonElement v) => v.GetProperty("problems").EnumerateArray().Select(p => p.GetProperty("code").GetString()!).ToList();

        [Fact]
        public void A_sound_map_validates()
        {
            var v = Validate(Open(_pkg.Folder));
            Assert.True(v.GetProperty("ok").GetBoolean(), v.ToString());
            // crate_2 at x = -30 sits outside the 0..1000 AI map: informational.
            var outside = v.GetProperty("problems").EnumerateArray().Single(p => p.GetProperty("code").GetString() == "outside-ai-map");
            Assert.Equal("info", outside.GetProperty("severity").GetString());
            Assert.True(outside.TryGetProperty("geoIndex", out _));
        }

        [Fact]
        public void Missing_models_are_errors()
        {
            _pkg.RemoveModel("tree");
            var v = Validate(Open(_pkg.Folder));
            Assert.False(v.GetProperty("ok").GetBoolean());
            Assert.Equal(2, Codes(v).Count(c => c == "missing-model"));
        }

        [Fact]
        public void The_validate_tool_answers_the_report()
        {
            string s = Open(_pkg.Folder);
            var r = Call("assets_validate", new { session = s, reason = "r" });
            Assert.False(IsError(r), Text(r));
            Assert.True(JsonDocument.Parse(Text(r)).RootElement.GetProperty("ok").GetBoolean());
        }

        // ------------------------------------------------------------------ paging

        [Fact]
        public void The_scene_pages_with_offset_and_next_offset()
        {
            string s = Open(_pkg.Folder);
            JsonElement Page(int? offset) => _b.Ok(new CommandMessage
            {
                Command = "ASSET_MAP_SCENE", AssetSession = s, AssetJson = JsonSerializer.Serialize(new { kind = "clutter", limit = 1, offset }),
            });
            var first = Page(null);
            Assert.Equal(2, first.GetProperty("matched").GetInt32());
            Assert.Equal(1, first.GetProperty("nextOffset").GetInt32());
            var second = Page(1);
            Assert.Equal(JsonValueKind.Null, second.GetProperty("nextOffset").ValueKind);
            Assert.NotEqual(first.GetProperty("instances")[0].GetProperty("clutterIndex").GetInt32(), second.GetProperty("instances")[0].GetProperty("clutterIndex").GetInt32());
        }
    }
}
