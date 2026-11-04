using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace MapBuilder.Desktop;

public partial class MainWindow : Window
{
    private static readonly Uri Address = new("http://127.0.0.1:8770/");
    private readonly CancellationTokenSource lifetime = new();
    private readonly HttpClient client = new() { BaseAddress = Address, Timeout = TimeSpan.FromSeconds(2) };
    private Process? server;

    public MainWindow() => InitializeComponent();

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        try
        {
            await EnsureServerAsync(lifetime.Token);
            using var page = await client.GetAsync("/", HttpCompletionOption.ResponseHeadersRead, lifetime.Token);
            if (!page.IsSuccessStatusCode)
                throw new InvalidOperationException("The server cannot serve the editor. Run npm ci and npm run build in web, rebuild the desktop app, and restart the server.");

            string profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MapBuilder", "WebView2");
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profile);
            lifetime.Token.ThrowIfCancellationRequested();
            await Browser.EnsureCoreWebView2Async(environment).WaitAsync(TimeSpan.FromSeconds(30), lifetime.Token);
            lifetime.Token.ThrowIfCancellationRequested();
            Browser.NavigationCompleted += (_, navigation) =>
            {
                LoadingMessage.Visibility = Visibility.Collapsed;
                if (!navigation.IsSuccess && !lifetime.IsCancellationRequested)
                    MessageBox.Show(this, $"The editor could not load ({navigation.WebErrorStatus}).",
                        "Map Builder", MessageBoxButton.OK, MessageBoxImage.Error);
            };
            Browser.Source = Address;
        }
        catch (Exception ex)
        {
            if (lifetime.IsCancellationRequested) return;
            string message = ex is WebView2RuntimeNotFoundException
                ? "Install the Microsoft Edge WebView2 Runtime, then reopen Map Builder."
                : ex.Message;
            MessageBox.Show(this, message, "Map Builder could not open", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
        }
    }

    private async Task EnsureServerAsync(CancellationToken token)
    {
        if (await IsMapBuilderReadyAsync(token)) return;
        token.ThrowIfCancellationRequested();
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html")))
            throw new InvalidOperationException("Build the editor with npm ci and npm run build in web, then rebuild the desktop app.");

        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "MapBuilder.Server.dll", "--urls", Address.GetLeftPart(UriPartial.Authority) },
        };
        server = Process.Start(start) ?? throw new InvalidOperationException("Could not start MapBuilder.Server.");
        var elapsed = Stopwatch.StartNew();
        while (!await IsMapBuilderReadyAsync(token))
        {
            token.ThrowIfCancellationRequested();
            if (server.HasExited)
                throw new InvalidOperationException($"MapBuilder.Server exited with code {server.ExitCode}. Check whether another application is using port {Address.Port}.");
            if (elapsed.Elapsed > TimeSpan.FromSeconds(30))
                throw new TimeoutException($"MapBuilder.Server did not respond at {Address} within 30 seconds.");
            await Task.Delay(200, token);
        }
    }

    private async Task<bool> IsMapBuilderReadyAsync(CancellationToken token)
    {
        try
        {
            using var response = await client.GetAsync("api/health", token);
            if (!response.IsSuccessStatusCode) return false;
            using var health = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            var root = health.RootElement;
            return root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True &&
                root.TryGetProperty("mcpTools", out var count) && count.ValueKind == JsonValueKind.Number;
        }
        catch (HttpRequestException) { return false; }
        catch (JsonException) { return false; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return false; }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        lifetime.Cancel();
        Browser.Dispose();
        client.Dispose();
        if (server is null) return;
        try
        {
            if (!server.HasExited) server.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) when (server.HasExited) { }
        finally { server.Dispose(); }
    }
}
