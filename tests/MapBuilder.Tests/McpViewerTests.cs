using System.Text.Json;
using System.Threading.Channels;
using MapBuilder.Server.Services;
using MapBuilder.Tests.Fixtures;
using Xunit;

namespace MapBuilder.Tests
{
    /// <summary>The Asset Viewer's MCP tools, and the events that make the page follow them.</summary>
    public sealed class McpViewerTests : IDisposable
    {
        private readonly SyntheticPackage _pkg = new();
        private readonly Backend _b = new();
        private readonly UiBridge _ui = new();
        private readonly ChannelReader<string> _events;
        private readonly McpServer _mcp;
        private int _id;

        public McpViewerTests()
        {
            _mcp = new McpServer(_b.Registry, _ui);
            _events = _ui.Listen();
        }

        public void Dispose() => _pkg.Dispose();

        private JsonElement Call(string tool, object args)
        {
            string text = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = ++_id, method = "tools/call", @params = new { name = tool, arguments = args } });
            using var doc = JsonDocument.Parse(text);
            object? reply = _mcp.Dispatch(doc.RootElement).GetAwaiter().GetResult();
            var r = JsonDocument.Parse(JsonSerializer.Serialize(reply)).RootElement.GetProperty("result").Clone();
            Assert.False(r.GetProperty("isError").GetBoolean(), r.GetProperty("content")[0].GetProperty("text").GetString());
            return JsonDocument.Parse(r.GetProperty("content")[0].GetProperty("text").GetString()!).RootElement.Clone();
        }

        // The events published since the last call, activity reports left out.
        private List<JsonElement> Events()
        {
            var list = new List<JsonElement>();
            while (_events.TryRead(out var json))
            {
                var e = JsonDocument.Parse(json).RootElement.Clone();
                if (e.GetProperty("type").GetString() != "activity") list.Add(e);
            }
            return list;
        }

        private static JsonElement Item(JsonElement listing, string name) =>
            listing.GetProperty("items").EnumerateArray().First(i => i.GetProperty("name").GetString() == name);

        [Fact]
        public void Browsing_lists_and_previews_and_moves_the_viewer_there()
        {
            string zdb = _pkg.WriteZdb();
            var l = Call("viewer_list", new { path = zdb, reason = "test" });
            Assert.Equal("package", l.GetProperty("kind").GetString());
            int geo = Item(l, "TST_GEO.ZED").GetProperty("inner")[0].GetInt32();
            var show = Assert.Single(Events());
            Assert.Equal("show", show.GetProperty("type").GetString());
            Assert.Equal("viewer", show.GetProperty("page").GetString());
            Assert.True(show.GetProperty("focus").GetProperty("viewer").GetProperty("open").GetBoolean());

            var p = Call("viewer_preview", new { path = zdb, inner = new[] { geo }, depth = 0, reason = "test" });
            Assert.Equal("geo", p.GetProperty("type").GetString());
            // Below the depth, children are counted rather than listed.
            Assert.Equal(2, p.GetProperty("models")[0].GetProperty("children").GetInt32());
            var focus = Assert.Single(Events()).GetProperty("focus").GetProperty("viewer");
            Assert.False(focus.GetProperty("open").GetBoolean());
            Assert.Equal(geo, focus.GetProperty("inner")[0].GetInt32());
        }

        [Fact]
        public void An_edit_changes_the_session_and_tells_the_viewer_what_changed()
        {
            string zdb = _pkg.WriteZdb();
            int geo = Item(Call("viewer_list", new { path = zdb, reason = "test" }), "TST_GEO.ZED").GetProperty("inner")[0].GetInt32();
            var crate = Call("viewer_preview", new { path = zdb, inner = new[] { geo }, query = "crate_2", reason = "test" }).GetProperty("models")[0];
            int nparams = crate.GetProperty("nparams").GetInt32();
            Events();

            var r = Call("viewer_edit", new { path = zdb, inner = new[] { geo }, op = new { op = "nparams", node = nparams, kind = 7 }, reason = "test" });
            string session = r.GetProperty("session").GetString()!;
            var changed = Assert.Single(Events());
            Assert.Equal("changed", changed.GetProperty("type").GetString());
            Assert.Equal("viewer", changed.GetProperty("page").GetString());
            Assert.Equal(nparams, changed.GetProperty("focus").GetProperty("viewer").GetProperty("record").GetInt32());
            Assert.Equal(7, Call("viewer_preview", new { path = zdb, inner = new[] { geo }, node = nparams, reason = "test" }).GetProperty("models")[0].GetProperty("kind").GetInt32());

            // Undo through the session tools, which the page also follows.
            Call("assets_undo", new { session, reason = "test" });
            Assert.Contains(Events(), e => e.GetProperty("type").GetString() == "changed");
            Assert.NotEqual(7, Call("viewer_preview", new { path = zdb, inner = new[] { geo }, node = nparams, reason = "test" }).GetProperty("models")[0].GetProperty("kind").GetInt32());
            _b.Close(session);
        }

        [Fact]
        public void Clutter_pages_and_palettes_follow_to_the_viewer()
        {
            string zdb = _pkg.WriteZdb();
            int clutter = Item(Call("viewer_list", new { path = zdb, reason = "test" }), "CLUTTER.ZAR").GetProperty("inner")[0].GetInt32();
            var p = Call("viewer_preview", new { path = zdb, inner = new[] { clutter }, limit = 1, reason = "test" });
            Assert.Equal(1, p.GetProperty("placements").GetArrayLength());
            Assert.Equal(0, p.GetProperty("placements")[0].GetProperty("clutterIndex").GetInt32());
            Assert.True(p.GetProperty("total").GetInt32() >= 1);
        }

        [Fact]
        public void Bad_edits_are_tool_errors()
        {
            string text = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = "viewer_edit", arguments = new { path = _pkg.Folder, op = new { op = "nothing" }, reason = "test" } } });
            using var doc = JsonDocument.Parse(text);
            var r = JsonDocument.Parse(JsonSerializer.Serialize(_mcp.Dispatch(doc.RootElement).GetAwaiter().GetResult())).RootElement.GetProperty("result");
            Assert.True(r.GetProperty("isError").GetBoolean());
        }
    }
}
