using System.Text.Json;
using MapBuilder.Server.Services;
using MapBuilder.Tests.Fixtures;
using Xunit;

namespace MapBuilder.Tests
{
    /// <summary>The MCP endpoint's JSON-RPC: the tool list, errors, and an edit made through tools/call.</summary>
    public sealed class McpServerTests : IDisposable
    {
        private readonly SyntheticPackage _pkg = new();
        private readonly Backend _b = new();
        private readonly McpServer _mcp;
        private int _id;

        public McpServerTests() => _mcp = new McpServer(_b.Registry, new UiBridge());

        public void Dispose() => _pkg.Dispose();

        /// <summary>One JSON-RPC request; the reply as JSON.</summary>
        private JsonElement Rpc(string method, object? @params = null)
        {
            string text = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = ++_id, method, @params });
            using var doc = JsonDocument.Parse(text);
            object? reply = _mcp.Dispatch(doc.RootElement).GetAwaiter().GetResult();
            return JsonDocument.Parse(JsonSerializer.Serialize(reply)).RootElement.Clone();
        }

        /// <summary>tools/call; the result object ({content, isError}).</summary>
        private JsonElement Call(string tool, object args) =>
            Rpc("tools/call", new { name = tool, arguments = args }).GetProperty("result");

        private static string Text(JsonElement result) => result.GetProperty("content")[0].GetProperty("text").GetString()!;

        [Fact]
        public void Initialize_answers_server_info()
        {
            var r = Rpc("initialize", new { protocolVersion = "2025-06-18" }).GetProperty("result");
            Assert.Equal("mapbuilder", r.GetProperty("serverInfo").GetProperty("name").GetString());
            Assert.True(r.GetProperty("capabilities").TryGetProperty("tools", out _));
        }

        [Fact]
        public void Every_tool_has_an_object_schema_that_requires_a_reason()
        {
            var tools = Rpc("tools/list").GetProperty("result").GetProperty("tools").EnumerateArray().ToList();
            Assert.True(tools.Count >= 30, $"only {tools.Count} tools");
            Assert.Equal(tools.Count, tools.Select(t => t.GetProperty("name").GetString()).Distinct().Count());
            foreach (var t in tools)
            {
                string name = t.GetProperty("name").GetString()!;
                Assert.False(string.IsNullOrWhiteSpace(t.GetProperty("description").GetString()), name);
                var schema = t.GetProperty("inputSchema");
                Assert.Equal("object", schema.GetProperty("type").GetString());
                var props = schema.GetProperty("properties");
                var required = schema.GetProperty("required").EnumerateArray().Select(x => x.GetString()).ToList();
                Assert.Contains("reason", required);
                foreach (string? r in required) Assert.True(props.TryGetProperty(r!, out _), $"{name} requires undeclared {r}");
            }
        }

        [Fact]
        public void An_unknown_tool_or_method_is_a_json_rpc_error()
        {
            Assert.Equal(-32602, Rpc("tools/call", new { name = "nope", arguments = new { } }).GetProperty("error").GetProperty("code").GetInt32());
            Assert.Equal(-32601, Rpc("nope/nope").GetProperty("error").GetProperty("code").GetInt32());
        }

        [Fact]
        public void A_failing_command_is_a_tool_error_not_a_crash()
        {
            var r = Call("assets_status", new { session = "missing", reason = "test" });
            Assert.True(r.GetProperty("isError").GetBoolean());
            Assert.Contains("No asset session", Text(r));
        }

        [Fact]
        public void Open_edit_undo_through_tools()
        {
            var open = Call("assets_open", new { path = _pkg.Folder, reason = "test" });
            Assert.False(open.GetProperty("isError").GetBoolean(), Text(open));
            string session = JsonDocument.Parse(Text(open)).RootElement.GetProperty("status").GetProperty("session").GetString()!;
            try
            {
                byte[] before = Backend.Bytes(session, "CLUTTER.ZAR");
                var edit = Call("assets_edit", new
                {
                    session, reason = "test", label = "via mcp",
                    op = new { op = "moveClutter", clutterIndex = 0, position = new[] { 8f, 0, 8 } },
                });
                Assert.False(edit.GetProperty("isError").GetBoolean(), Text(edit));
                Assert.Equal("via mcp", _b.Status(session).GetProperty("canUndo").GetString());

                // The page and the AI share the session: what the tool changed, the backend sees.
                Assert.NotEqual(before, Backend.Bytes(session, "CLUTTER.ZAR"));
                var undo = Call("assets_undo", new { session, reason = "test" });
                Assert.False(undo.GetProperty("isError").GetBoolean(), Text(undo));
                Assert.Equal(before, Backend.Bytes(session, "CLUTTER.ZAR"));
            }
            finally { _b.Close(session); }
        }

        [Fact]
        public void Edit_errors_come_back_as_tool_errors()
        {
            string session = _b.Open(_pkg.Folder);
            try
            {
                var r = Call("assets_edit", new { session, reason = "test", op = new { op = "deleteClutter", clutterIndex = 42 } });
                Assert.True(r.GetProperty("isError").GetBoolean());
                Assert.Contains("out of range", Text(r));
            }
            finally { _b.Close(session); }
        }
    }
}
