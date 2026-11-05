using System.Text.Json;
using System.Text.RegularExpressions;
using MapBuilder.Server.Services;
using MapBuilder.Tests.Fixtures;
using Xunit;

namespace MapBuilder.Tests
{
    /// <summary>The ASSET_EDIT op table (EditOps) matches the ops AssetService runs, and checks ops before they run.</summary>
    public sealed class EditOpsSchemaTests : IDisposable
    {
        private readonly SyntheticPackage _pkg = new();
        private readonly Backend _b = new();
        private readonly McpServer _mcp;
        private readonly string _s;

        public EditOpsSchemaTests()
        {
            _mcp = new McpServer(_b.Registry, new UiBridge());
            _s = _b.Open(_pkg.Folder);
        }

        public void Dispose()
        {
            _b.Close(_s);
            _pkg.Dispose();
        }

        private static string RepoFile(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "MapBuilder.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray());
        }

        [Fact]
        public void The_table_lists_exactly_the_ops_the_server_runs()
        {
            string source = File.ReadAllText(RepoFile("server", "Services", "AssetService.cs"));
            int start = source.IndexOf("private static object Edit(AssetSession s, string? json)", StringComparison.Ordinal);
            int end = source.IndexOf("throw new ArgumentException($\"Unknown op '{op}'\")", start, StringComparison.Ordinal);
            var served = Regex.Matches(source[start..end], "case \"([A-Za-z]+)\":").Select(m => m.Groups[1].Value).ToHashSet();
            var gta = File.ReadAllText(RepoFile("server", "Services", "Assets", "GtaAssetService.Edits.cs"));
            served.UnionWith(Regex.Matches(gta, "case \"(gta[A-Za-z]+)\":").Select(m => m.Groups[1].Value));
            var table = EditOps.All.Select(s => s.Op).ToHashSet();
            Assert.Equal(served.OrderBy(x => x), table.OrderBy(x => x));
        }

        [Fact]
        public void Every_branch_is_closed_and_names_its_op()
        {
            var schema = EditOps.Schema();
            var branches = (List<Dictionary<string, object>>)schema["oneOf"];
            Assert.Equal(EditOps.All.Length, branches.Count);
            foreach (var b in branches)
            {
                Assert.False((bool)b["additionalProperties"]);
                var props = (Dictionary<string, Dictionary<string, object>>)b["properties"];
                Assert.Equal(b["title"], props["op"]["const"]);
                foreach (string r in (string[])b["required"]) Assert.True(props.ContainsKey(r), $"{b["title"]} requires undeclared {r}");
            }
        }

        private static JsonElement J(object o) => JsonDocument.Parse(JsonSerializer.Serialize(o)).RootElement;

        [Theory]
        [InlineData("{\"op\":\"moveClutter\",\"clutterIndex\":0,\"postion\":[1,2,3]}", "unknown field \"postion\"")]
        [InlineData("{\"op\":\"moveClutter\",\"position\":[1,2,3]}", "\"clutterIndex\" is required")]
        [InlineData("{\"op\":\"moveClutter\",\"clutterIndex\":\"0\"}", "\"clutterIndex\" must be an integer")]
        [InlineData("{\"op\":\"moveClutter\",\"clutterIndex\":0,\"position\":[1,2]}", "at least 3 items")]
        [InlineData("{\"op\":\"setPoint\",\"name\":\"Alpha1\",\"kind\":\"spawn\"}", "one of start, waypoint, other")]
        [InlineData("{\"op\":\"setPoint\",\"name\":\"Alpha1\",\"team\":300}", "at most 255")]
        [InlineData("{\"op\":\"transformVertices\",\"archive\":\"A\",\"model\":\"m\",\"scale\":\"big\"}", "number or [x, y, z]")]
        [InlineData("{\"op\":\"explode\"}", "unknown op \"explode\"")]
        [InlineData("{\"clutterIndex\":0}", "needs \"op\"")]
        [InlineData("{\"op\":\"batch\",\"ops\":[{\"op\":\"deleteClutter\",\"clutterIndex\":0},{\"op\":\"addClutter\",\"modle\":\"tree\"}]}", "op.ops[1] addClutter: unknown field \"modle\"")]
        public void Bad_ops_are_refused_before_they_run(string op, string error)
        {
            Assert.Contains(error, EditOps.Check(JsonDocument.Parse(op).RootElement));
        }

        [Fact]
        public void Good_ops_pass()
        {
            Assert.Null(EditOps.Check(J(new { op = "moveClutter", clutterIndex = 0, position = new[] { 1, 2, 3 }, yaw = 45, label = "x", dryRun = true })));
            Assert.Null(EditOps.Check(J(new { op = "transformVertices", archive = "A", model = "m", scale = new[] { 1, 2, 1 } })));
            Assert.Null(EditOps.Check(J(new { op = "transformVertices", archive = "A", model = "m", scale = 2 })));
            Assert.Null(EditOps.Check(J(new { op = "batch", ops = new object[] { new { op = "deleteClutter", clutterIndex = 1 }, new { op = "placePrefab", prefab = "p", position = new[] { 0, 0, 0 } } } })));
        }

        [Fact]
        public void The_tool_refuses_a_typo_and_changes_nothing()
        {
            byte[] before = Backend.Bytes(_s, "CLUTTER.ZAR");
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0", id = 1, method = "tools/call",
                @params = new { name = "assets_edit", arguments = new { session = _s, reason = "r", op = new { op = "moveClutter", clutterIndex = 0, postion = new[] { 1, 2, 3 } } } },
            }));
            var r = JsonDocument.Parse(JsonSerializer.Serialize(_mcp.Dispatch(doc.RootElement).GetAwaiter().GetResult())).RootElement.GetProperty("result");
            Assert.True(r.GetProperty("isError").GetBoolean());
            Assert.Contains("postion", r.GetProperty("content")[0].GetProperty("text").GetString());
            Assert.Equal(before, Backend.Bytes(_s, "CLUTTER.ZAR"));
        }
    }
}
