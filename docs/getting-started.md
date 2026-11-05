# Getting started

This guide takes you from a fresh clone to an open map. It covers the requirements, the two ways to run Map Builder (browser or Windows desktop app), and what to open first.

## Requirements

| Requirement | Notes |
|---|---|
| [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) | Builds and runs the server |
| [Node.js](https://nodejs.org/) 20 or later | Builds the web client |
| Your own SOCOM II game files | A `.ZDB` / `.CDB` package, a loose `.ZED` / `.ZAR`, an unpacked package folder, or the disc itself. This repository ships no game files. |
| [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) | Only for the Windows desktop app |
| [ffmpeg](https://ffmpeg.org/) (optional) | Only to preview `.PSS` movies in the Asset Viewer (`winget install Gyan.FFmpeg`, or set `MAPBUILDER_FFMPEG`) |

## Build and run

Build the web client once:

```bash
cd web
npm ci
npm run build
```

Then start the server:

```bash
cd server
dotnet run
```

Open [http://127.0.0.1:8770](http://127.0.0.1:8770). If the web client has not been built, the server still starts and says so in its console.

In Visual Studio, open `MapBuilder.sln` and press F5 (after `npm run build` in `web/`); the launch profile uses the same address.

### Windows desktop app

After building the web client, build the WPF shell from the repository root:

```bash
dotnet build desktop/MapBuilder.Desktop.csproj -c Release
```

Run `desktop/bin/Release/net8.0-windows/MapBuilder.Desktop.exe` to open the editor in a resizable window, or choose **MapBuilder.Desktop** as the startup project in Visual Studio and press F5.

The desktop app starts the local server when it needs one. If Map Builder is already running at `http://127.0.0.1:8770`, the window connects to that session instead. Closing the window stops a server it started and leaves a separately started server running.

## Open a package

Choose **File › Open package…** (`Ctrl+O`). The file browser lists your drives, common folders and the folders you used last; you can also type or paste a path into its address bar and press Enter.

Good first packages:

| Open | To see |
|---|---|
| `RUN\M51.ZDB` | A single-player map in the Map Builder, with its props, clutter, spawn points, actions, mission tables and tactical map |
| `RUN\MP*.ZDB` | A multiplayer map |
| A `.CDB` memory-card pack | A custom map or character pack |
| The disc's drive or `RUN` folder | The whole disc in the Asset Viewer (**File › Open folder…** there) |

A map opens in the Map Builder; any other package opens in the Model Editor. All pages share one in-memory session per package, so you can switch pages freely and save everything together.

> **Keep your originals safe.** Nothing is written until you save or export, and saves write a copy by default. Work on copies and test modified packages in game before relying on them. See [Working with packages](working-with-packages.md).

## Network and security

The server binds to `127.0.0.1:8770`, so only your own machine can reach it. Pass `--urls http://127.0.0.1:9000` for another port.

The API can read and write any file your account can, so the server also only answers requests addressed to `localhost`, `127.0.0.1` or `[::1]`. This stops a web page from reaching it by pointing its own domain at your machine (DNS rebinding). To serve it under another name, pass both settings, for example:

```bash
dotnet run -- --urls http://0.0.0.0:8770 --AllowedHosts "mypc;localhost"
```

The console then warns that the API is reachable from the network.

## Settings

| Variable | Default | Effect |
|---|---|---|
| `MAPBUILDER_AUTOSAVE` | `%LOCALAPPDATA%\MapBuilder\autosave` | Folder for recovery copies of sessions with unsaved edits |
| `MAPBUILDER_AUTOSAVE_MINUTES` | `3` | Autosave interval; `0` turns it off |
| `MAPBUILDER_PREFABS` | `%LOCALAPPDATA%\MapBuilder\prefabs` | Where prefabs are kept |
| `MAPBUILDER_FFMPEG` | `ffmpeg` on `PATH` | ffmpeg used to convert `.PSS` movies for preview |

View preferences (shading, grid, snapping, render resolution, backdrop, camera bookmarks) are remembered in your browser.

## Next steps

- Tour the [Map Builder](map-builder.md), [Model Editor](model-editor.md), [Animation Editor](animation-editor.md) and [Asset Viewer](asset-viewer.md).
- Learn the [controls](controls.md).
- Connect an AI assistant through the [MCP server](mcp-server.md).
