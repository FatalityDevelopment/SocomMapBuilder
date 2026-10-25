using MapBuilder.Server;
using MapBuilder.Server.Services;

// The map builder's backend: one HTTP endpoint that runs ASSET_* commands against an in-memory asset session,
// and the built web client (web/dist, copied to wwwroot next to the executable) at the root.
var registry = new CommandRegistry();
new ZipperAssetService().Register(registry);
new ZipperWeaponsService().Register(registry);
new ZipperFileFormatsService().Register(registry);
new ViewerService().Register(registry);
new FileSystemService().Register(registry);
new PackageFilesService().Register(registry);

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
var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/health", () => Results.Ok(new { ok = true, commands = registry.Count }));

app.MapPost("/api/command", (CommandMessage command) =>
{
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
        return Results.Content(Responses.Error($"{command.Command}: {ex.Message}"), "application/json");
    }
});

app.Lifetime.ApplicationStarted.Register(() =>
{
    Console.WriteLine($"Map Builder running at {url}");
    if (webRoot == null)
    {
        Console.WriteLine("The page is not built yet: run `npm ci` and `npm run build` in web/, then restart (or use `npm run dev`).");
    }
});
app.Run();
