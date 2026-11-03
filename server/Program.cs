using MapBuilder.Server;
using MapBuilder.Server.Services;

// The map builder's backend: one HTTP endpoint that runs ASSET_* commands against an in-memory asset session,
// and the built web client (web/dist, copied to wwwroot next to the executable) at the root.
var registry = new CommandRegistry();
new ZipperAssetService().Register(registry);
new ZipperWeaponsService().Register(registry);
new ZipperFileFormatsService().Register(registry);
new FileSystemService().Register(registry);
new PackageFilesService().Register(registry);
// The pages' event stream, and the MCP endpoint an AI drives the same sessions through (the pages follow along).
var ui = new UiBridge();
var mcp = new McpServer(registry, ui);

// The page: wwwroot next to the executable (copied there by the build), else ../web/dist when running from source.
// Without either the API still runs; the console says how to build the page.
string? webRoot = new[]
{
    Path.Combine(AppContext.BaseDirectory, "wwwroot"),
    Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "web", "dist")),
}.FirstOrDefault(d => File.Exists(Path.Combine(d, "index.html")));

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    WebRootPath = webRoot,
});
string url = builder.Configuration["urls"] ?? "http://127.0.0.1:8770";
builder.WebHost.UseUrls(url);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
// The API reads and writes any path, so only answer requests addressed to this machine by name (a page on another
// site that rebinds its DNS name to 127.0.0.1 sends its own Host). Pass --AllowedHosts to serve other names.
if (string.IsNullOrEmpty(builder.Configuration["AllowedHosts"]))
{
    builder.Configuration["AllowedHosts"] = "localhost;127.0.0.1;[::1]";
}
bool loopbackOnly = url.Split(';').All(u =>
    Uri.TryCreate(u.Replace("://*", "://0.0.0.0").Replace("://+", "://0.0.0.0"), UriKind.Absolute, out var uri) &&
    (uri.IsLoopback || uri.Host == "localhost"));
var app = builder.Build();
var log = app.Logger;

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/health", () => Results.Ok(new { ok = true, commands = registry.Count, mcpTools = mcp.ToolCount, pages = ui.ClientCount }));

app.MapGet("/api/events", ctx => ui.Stream(ctx));
// Asset Viewer movies converted to MP4 (VIEWER_MOVIE), with ranges so the player can seek.
app.MapGet("/api/viewer/movie/{name}", (string name) =>
{
    string file = Path.GetFileName(name);
    string path = Path.Combine(ZipperAssetService.MovieCacheDir, file);
    return file.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) && File.Exists(path)
        ? Results.File(path, "video/mp4", enableRangeProcessing: true)
        : Results.NotFound();
});
app.MapPost("/api/ui/reply", (System.Text.Json.JsonElement body) => { ui.Reply(body); return Results.Ok(); });

// MCP (Streamable HTTP): JSON-RPC requests on POST; there is no server-initiated stream, so GET is refused.
app.MapPost("/mcp", ctx => mcp.Handle(ctx));
app.MapGet("/mcp", () => Results.StatusCode(StatusCodes.Status405MethodNotAllowed));
app.MapDelete("/mcp", () => Results.StatusCode(StatusCodes.Status405MethodNotAllowed));

// The body is read here rather than bound by the framework: a field of the wrong type (a number where a string is
// expected) or a body over Kestrel's size limit would otherwise get a plain-text 400 / 413 that is never logged, and
// the page could only say it cannot reach the server.
app.MapPost("/api/command", async (HttpContext ctx) =>
{
    CommandMessage? command;
    try
    {
        command = await ctx.Request.ReadFromJsonAsync<CommandMessage>();
    }
    catch (Exception ex) when (ex is System.Text.Json.JsonException or BadHttpRequestException)
    {
        string reason = ex.InnerException is System.Text.Json.JsonException inner ? inner.Message : ex.Message;
        log.LogWarning("Unreadable /api/command request: {Reason}", reason);
        return Results.Content(Responses.Error($"Unreadable request: {reason}"), "application/json");
    }
    if (command == null)
    {
        log.LogWarning("Empty /api/command request");
        return Results.Content(Responses.Error("Empty request"), "application/json");
    }
    if (!registry.TryGet(command.Command, out var handler) || handler == null)
    {
        return Results.Content(Responses.Error($"Unknown command '{command.Command}'"), "application/json");
    }
    try
    {
        return Results.Content(handler(command), "application/json");
    }
    catch (Exception ex)
    {
        // Handlers answer their own expected failures; anything else (bad JSON, IO) still gets a JSON reply.
        log.LogError(ex, "{Command} failed", command.Command);
        return Results.Content(Responses.Error($"{command.Command}: {ex.Message}"), "application/json");
    }
});

app.Lifetime.ApplicationStarted.Register(() =>
{
    Console.WriteLine($"Map Builder running at {url}");
    Console.WriteLine($"MCP server at {url.Split(';')[0].TrimEnd('/')}/mcp ({mcp.ToolCount} tools)");
    if (!loopbackOnly)
    {
        Console.WriteLine("WARNING: listening beyond this machine. Anyone who can reach it can read and write your files " +
            $"through the API; only hosts named in AllowedHosts ({app.Configuration["AllowedHosts"]}) are answered.");
    }
    if (webRoot == null)
    {
        Console.WriteLine("The page is not built yet: run `npm ci` and `npm run build` in web/, then restart (or use `npm run dev`).");
    }
});
app.Run();
