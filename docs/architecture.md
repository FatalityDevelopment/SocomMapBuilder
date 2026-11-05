# Architecture

Map Builder is a local web app: an ASP.NET Core server owns the game packages and a React + three.js client previews them and sends edit commands. The same server exposes those commands to AI assistants over MCP, and a small WPF shell can host the client as a desktop app.

```
web/  React 18 + TypeScript + three.js ──POST /api/command──▶ server/  ASP.NET Core (.NET 8)
        ▲                                                        ├─ /mcp         MCP tools (Streamable HTTP)
        └──────────── GET /api/events (SSE) ◀────────────────────┤
                                                                 └─ CodeDesigner.Ps2.FileFormats
                                                                    (Zipper ZDB/ZED/ZAR/GEO/MDL/TXR/PAL/RDR,
                                                                     CA and GTA codecs, conversion)
desktop/  WPF + WebView2 window that starts or joins the server
```

## Principles

- **The backend owns the package.** Every edit is an `ASSET_*` command on an in-memory session (one per package path, shared by all pages and by AI assistants); the pages preview and send commands. See the [command reference](command-reference.md).
- **Edits are undoable and nothing is written until a save.** Saves write a temporary file and then replace the target, keeping a one-time `.bak`.
- **The Asset Viewer reuses the editors.** It lists and previews with `VIEWER_LIST` / `VIEWER_PREVIEW`; package archives come from an asset session, so models, textures, maps and clips are drawn with the editors' commands and viewports. In-place edits (RDR tables, AI maps, text, icons, palettes, clutter, GEO scene nodes, ZANIM effect scripts) go through `VIEWER_EDIT` or the session's `ASSET_EDIT` ops, with the editors' undo, redo and save. Disc formats only it reads (SPU ADPCM, memory-card icons, raw screens, PSS / ELF headers) live in `CodeDesigner.Ps2.FileFormats/Ps2`.
- **The client mirrors CodeDesigner's webview folders.** `web/src/webview/` holds the viewports, decoders and players as shared files, and each page's logic runs on a small dependency-tracking signals shim (`web/src/shared/signals.ts`) under a React view.
- **AI assistants see what the user sees.** The server pushes AI activity and view requests to the pages over Server-Sent Events, and the pages answer screenshot and state requests. See [MCP server](mcp-server.md).

## Repository layout

```
server/
  Program.cs                      command registry, static web root, /mcp and /api/events
  Services/McpServer.cs           MCP tools (assets_*, ui_*) over the command registry
  Services/UiBridge.cs            event stream to the pages, screenshot / state requests
  Services/AssetService*.cs       ASSET_* sessions, edits, undo, save, map tables, viewer
  Services/PackageFilesService.cs PACKAGE_EDIT (package files, archive records)
  Services/FileSystemService.cs   FS_* listings for the file browser
  CodeDesigner.Ps2.FileFormats/   format codecs (Zipper/Package: edits, import, textures)
  data/                           hand-made tables (chartypes.tsv, tacmaps.json)
web/src/
  AppShell.tsx                    activity bar and pages
  MapPage.tsx, viewport/          Map Builder
  webview/model-editor/           Model Editor
  webview/animation-editor/       Animation Editor
  webview/asset-viewer/           Asset Viewer
  shared/                         host (dialogs, file browser, import), signals, icons, AI bridge
desktop/                          Windows desktop app (WPF + WebView2)
tests/MapBuilder.Tests/           server tests (synthetic data only)
docs/                             guides, format documentation, roadmap
```

## Formats

How every asset type is decoded and written is documented in [docs/formats](formats/README.md). A few conventions the editors rely on, checked against retail packages:

- Model space is Y up as stored; GEO and clutter matrices are column-major with the translation in elements 12–14.
- A model is a top-level record of `<ns>_MDL.ZED`; its definition is a node of the same name under `models` in `<ns>_GEO.ZED` (a character's skeleton is named without `MESH_`).
- A ZED's root value is its top-level record count.
- Texture names and palette ids are global in game (the first registered wins), so imports keep a palette's id only when the target does not use it, and new palettes take ids from a block of their own (1024 + 32 × hash).
- A texture header's `+0x08` is its texture index (1..N per TXR) and equals `TEX0.TBP0`; `MIPTBP1/2` hold its mip levels' indices; `TEX0.CBP` binds the palette. Header `+0x0F` is `0x12` for a 16-bit palette and `0x13` for 32-bit.
- 16-bit palettes have no alpha, only the STP bit: a black entry without it is transparent.
- Retail models name textures in 16-byte fields, so a texture applied to them needs a name of at most 15 characters.
- A package's root holds `.ZED`, `.ZAR` and `.MPS` files; `.RDR` tables live inside ZARs (`READERM.ZAR`).
