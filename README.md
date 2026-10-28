<p align="center">
  <img src="docs/icon.svg" alt="SOCOM Map Builder screenshot" width="112">
</p>

<h1 align="center">SOCOM Map Builder</h1>

<p align="center">
  A work-in-progress toolkit for exploring and editing PlayStation 2 game assets.
</p>

<p align="center">
  Build maps and edit models, textures, palettes, and character animations in SOCOM U.S. Navy SEALs II packages, with additional support for SOCOM 3, Combined Assault, and experimental GTA: San Andreas model workflows.
</p>

<p align="center">
  <strong>Status:</strong> SOCOM II is the primary tested target. SOCOM 3 and Combined Assault support varies by workflow; GTA: San Andreas support is partial, and FTB has not been tested.
</p>

<p align="center">
  Developed with research and assistance from CodeDesigner AI. Runtime injection and map-launching source code from CodeDesigner is not included in this repository yet.
</p>

---

Open a game package and work across the map, its props and clutter, vehicles, characters, weapons, textures, palettes, and character animations. The editors share one in-memory package session, so changes made across different pages can be saved together as a copy of the `.ZDB` / `.CDB` package or exported as a memory-card pack for `mc0:`.

**Keep your originals safe:** edits stay in the session until you save or export. Work on copies, and test modified packages in-game before relying on them.

## Contents

- [Pages and editors](#pages)
- [Examples](#examples)
- [Getting started](#getting-started)
- [Working with packages](#working-with-packages)
- [Controls](#controls)
- [Architecture](#how-it-fits-together)
- [Command reference](#command-reference)
- [Format notes](#format-notes)
- [Format documentation](docs/formats/README.md)
- [Development](#development)
- [Status and limits](#status-and-limits)

## Pages

Use the activity bar to switch between tools. Each page retains its state, and the editors are connected: double-click a prop on the map to open it in the Model Editor, or right-click a model in the Asset Viewer to send it to the appropriate editor.

### Map Builder

<img src="docs/screenshots/map.png" alt="SOCOM Map Builder map editor showing a composed level with world geometry, props, clutter, sky, textures, and baked lighting" width="850">

Inspect the assembled level with its world model, props, clutter, sky, textures, and baked lighting. Select and transform instances directly in the scene.

- Move, rotate, scale, duplicate and delete props and clutter; drop them to the ground.
- Place clutter on any surface with the Place tool; pick the model, yaw (or random yaw) and scale.
- Move the AIMAPS spawn and waypoint points.
- Edit a multi-selection at once (move by, turn by, set scale); lock layers so clicks and box drags skip them; move a single prop along its own axes (Local).
- **Import from package…** (File menu or the Place tab): bring a prop, vehicle, weapon or character from another package, then place it.
- Facade trees face the camera, as they do in game.

### Model Editor

<img src="docs/screenshots/character.png" alt="SOCOM Map Builder character model editing view" width="850">

<img src="docs/screenshots/weapon.png" alt="SOCOM Map Builder weapon model editing view" width="850">

Edit props, vehicles, weapons, and skinned characters using object-level and geometry-level tools.

- Select by object, visual, chunk, vertex or face; move / rotate / scale; mirror, flip normals, snap to grid.
- Add planes, cubes or an OBJ; duplicate and delete faces; edit UVs.
- Paint baked vertex colours.
- **Textures**: retexture sections, replace a texture from any image (PNG, JPG, BMP, WebP), create a **new texture from an image** (with its own palette, or reusing one), **import a texture from another package**, create a **palette from an image**, export PNG.
- **Import from package…** (Assets panel or File menu): copy a model from another package.
- Export OBJ; convert models to SOCOM 3 / Combined Assault, and CA or GTA: San Andreas models to SOCOM II.

### Animation Editor

<img src="docs/screenshots/anim.png" alt="SOCOM Map Builder animation editor for character clips" width="850">

Preview character clips on skinned models. Package-local `MOTION_S.ZAR` clips are editable; the shared `MOTION_P.ZAR` is read-only.

- Create, duplicate, rename, delete and retime clips; key bones with the gizmo or exact fields.
- With the game's RUN folder available, weapons mount on the clip and fire their muzzle effects.

### Asset Viewer

<img src="docs/screenshots/asset.png" alt="SOCOM Map Builder asset viewer for browsing package files and archives" width="850">

Browse package contents and archives, preview assets, and send items directly to the editor that supports them.

- Browse .ZDB / .CDB / .ZAR / .ZED files and the archives inside them, in a list with icons and thumbnails, a path bar (Up / Backspace, breadcrumbs) and a filter.
- Lists match the editors: model archives list models (characters, weapons), TXR archives textures (as a list or icons), PAL archives palettes (with a swatch preview), GEO archives definitions, CLUTTER.ZAR its clutter models with their placement counts. Records that hold records open like folders.
- Previews: models (decoded by the server, textured), textures, palettes, GEO scenes with a spawn camera, RDR screens, AI maps, animations. The sidebar shows the textures of what is open.
- **Right-click** an item to open it in the editor it needs (Model Editor, Animation Editor, Map Builder), export a texture as PNG, extract it, replace or remove it.
- **Import…**: add .ZED / .ZAR / .MPS files to a package (or merge another package's files), or any file (an .RDR, say) to a .ZAR / .ZED archive. Replace and Remove work at both levels.

Edits in the editors are undoable. SOCOM 3 / Combined Assault archives and GTA: San Andreas (.ISO or .IMG) open read-only.

## Examples

<img src="docs/screenshots/raygun.png" alt="Custom ray gun asset shown in the SOCOM Map Builder workflow" width="850">

*Example: a custom weapon asset.*

<img src="docs/screenshots/hummer.png" alt="Hummer vehicle model preview in SOCOM Map Builder" width="850">

*Example: a vehicle model.*

<img src="docs/screenshots/tank.png" alt="Tank model preview in SOCOM Map Builder" width="850">

*Example: a tank model.*

<img src="docs/screenshots/ca_char.png" alt="Combined Assault character asset preview" width="850">

*Example: a character asset from Combined Assault.*

### GTA: San Andreas compatibility (partial)

Character and prop workflows are working, along with some vehicles. This support is still incomplete, so treat exports as experimental and verify the results in your target workflow.

<img src="docs/screenshots/gta_cj.png" alt="Grand Theft Auto San Andreas character model preview" width="850">

*GTA: San Andreas example: character model support.*

<img src="docs/screenshots/gta_dildo.png" alt="Grand Theft Auto San Andreas prop model preview" width="850">

*GTA: San Andreas example: prop model support.*

<img src="docs/screenshots/gta_flame_thrower.png" alt="Grand Theft Auto San Andreas weapon model preview" width="850">

*GTA: San Andreas example: weapon model export workflow.*

## Getting started

### Requirements

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Node.js](https://nodejs.org/) 20 or later
- Your own SOCOM II packages (this repository does not include game files)
- A supported SOCOM II package to edit, such as a `.ZDB` / `.CDB` package or an unpacked package folder

### Build and run

```bash
cd web
npm ci
npm run build
```

```bash
cd server
dotnet run
```

Open [http://127.0.0.1:8770](http://127.0.0.1:8770) and choose **File → Open package…** (`Ctrl+O`). The file browser lists your drives and folders. Select a map package such as `RUN\MP\M51\M51.ZDB`, a `.CDB` pack, a loose `.ZED`, or an unpacked package folder.

In Visual Studio, open `MapBuilder.sln` and press F5 (after `npm run build` in `web/`); the launch profile uses the same address.

The server binds to `127.0.0.1:8770`, so only your own browser can reach it. Pass `--urls http://127.0.0.1:9000` for another port. If the page has not been built, the server still starts and says so in its console.

The API can read and write any file your account can, so the server also only answers requests addressed to `localhost`, `127.0.0.1` or `[::1]`; this stops a web page from reaching it by pointing its own domain at your machine (DNS rebinding). To serve it under another name, pass both, e.g. `--urls http://0.0.0.0:8770 --AllowedHosts "mypc;localhost"`; the console then warns that the API is reachable from the network.

## Working with packages

- **Changes are session-based until you save or export.** **Save copy** (`Ctrl+S`) writes a new `.ZDB` or `.CDB`; **Export memory-card pack** writes `<NAME>.CDB` for `mc0:/BASCUS-97275<NAME>/`. Pack names start with `m` and can contain up to 7 letters or digits.
- **Asset Viewer edits** (Import / Replace / Remove) ask where to save. Saving over the open file keeps a `<name>.bak` the first time.
- **Saves are written whole or not at all**: the new file goes to `<name>.tmp` and then replaces the target, so a crash mid-save leaves the old file. Writing over an existing file keeps `<name>.bak` once (the original); delete it yourself when you no longer need it.
- **Work on copies.** Keep your original packages untouched and test modified packages in-game before using them.
- **Importing a model** copies its MDL record, its GEO definition (a character's skeleton), every texture its sections name with their mip levels, their palettes, and any models its definition is built from. Undo removes the whole import.
- **New textures** are 8-bit indexed, a power of two a side (8–512, default: the image rounded, at most 256), with a 256-colour palette made from the image (32-bit when the image has soft alpha, else 16-bit like most retail palettes).

## Controls

| Action | Keys |
|---|---|
| Fly | W A S D, Q / E down / up, Shift faster (viewport focused) |
| Orbit / pan / zoom | Middle drag or Alt + left drag / right drag / wheel |
| Tools | V select · G move · R rotate · T scale · P place |
| Duplicate / delete / drop | Shift+D / X or Delete / End |
| Copy / paste clutter | Ctrl+C / Ctrl+V (also into another package that has the model) |
| Frame selected / frame map | F / Home |
| Views | Numpad 1 front · 3 right · 7 top · 5 orthographic |
| Shading | Z cycles textured, solid, wire, X-ray |
| Undo / redo / save copy | Ctrl+Z / Ctrl+Y / Ctrl+S |
| Asset Viewer | Double-click / Enter opens · Backspace up · right-click for actions |

## How it fits together

```
web/  React 18 + TypeScript + three.js ──POST /api/command──▶ server/  ASP.NET Core (.NET 8)
                                                                 └─ CodeDesigner.Ps2.FileFormats
                                                                    (Zipper ZDB/ZED/ZAR/GEO/MDL/TXR/PAL/RDR,
                                                                     CA and GTA codecs, conversion)
```

- The backend owns the package. Every edit is an `ASSET_*` command on an in-memory session (one per package path, shared by all pages); the pages preview and send commands.
- The Asset Viewer reads archives with `ZIPPER_*` commands and decodes most of them in the page; SOCOM II models are decoded by the server, the same mesh the editors draw.
- `web/src/webview/` mirrors CodeDesigner's webview folders: the viewports, decoders and players are shared files, and each page's logic runs on a small dependency-tracking signals shim (`web/src/shared/signals.ts`) under a React view.

```
server/
  Program.cs                      command registry, static web root
  Services/AssetService*.cs       ASSET_* sessions, edits, undo, save
  Services/PackageFilesService.cs PACKAGE_EDIT (package files, archive records)
  Services/FileSystemService.cs   FS_* listings for the file browser
  CodeDesigner.Ps2.FileFormats/   format codecs (Zipper/Package: edits, import, textures)
web/src/
  AppShell.tsx                    activity bar and pages
  MapPage.tsx, viewport/          Map Builder
  webview/model-editor/           Model Editor
  webview/animation-editor/       Animation Editor
  webview/zipper-workbench/       Asset Viewer
  shared/                         host (dialogs, file browser, import), signals, icons
```

## Command reference

All commands are `POST /api/command` with a JSON body `{ "Command": ..., ...fields }`; the reply is `{ Success, Data }` with `Data` as JSON text.

| Command | Fields | Does |
|---|---|---|
| `ASSET_OPEN` | `InputPath` | Opens (or reuses) a session for a package, folder or archive |
| `ASSET_LIST_MODELS` / `ASSET_LIST_TEXTURES` / `ASSET_LIST_PALETTES` | `AssetSession` | The package's assets |
| `ASSET_MODEL_MESH` / `ASSET_DESCRIBE_MODEL` / `ASSET_TEXTURE` | `AssetSession`, `AssetArchive`, `AssetName` | A model's mesh and description, a texture as PNG |
| `ASSET_MAP_SCENE` / `ASSET_MAP_POINTS` | `AssetSession` | Map instances and AIMAPS points |
| `ASSET_EDIT` | `AssetSession`, `AssetJson: {op, …}` | One edit (below); `batch` runs several as one undo step |
| `ASSET_UNDO` / `ASSET_REDO` / `ASSET_SAVE` | `AssetSession` (+ `AssetJson` for save) | History and saving (`zdb` or `pack`) |
| `PACKAGE_EDIT` | `InputPath`, `OutputPath`, `Data: [ops]` | Add / replace / remove package files and archive records |
| `FS_PLACES` / `FS_LIST` | `InputPath` | Drives, common folders and folder listings |

Edit ops include `moveClutter`, `addClutter`, `deleteClutter`, `moveInstance`, `duplicateInstance`, `deleteInstance`, `movePoint`, `setVertices`, `transformVertices`, `addMesh`, `deleteFaces`, `duplicateFaces`, `retexture`, `replaceTexture`, `tintVertexColors`, the animation ops, and:

- `importModel` `{sourceSession | source, archive, name, targetArchive?, newName?}`
- `importTexture` `{sourceSession | source, name, sourceNamespace?, namespace?, newName?}`
- `addTexture` `{name, pngBase64 | imagePath, namespace?, width?, height?, maxSize?, paletteId?}`
- `addPalette` `{pngBase64 | imagePath, namespace?, is16?}`

## Format notes

The full decoding documentation for every asset type (packages, ZED/ZAR containers, models, GEO scenes, characters, textures and palettes, clutter, AI maps, RDR tables, animations) is in **[docs/formats](docs/formats/README.md)**. In short, the things the editors rely on, checked against retail packages:

- Model space is Y up as stored; GEO and clutter matrices are column-major with the translation in 12–14.
- A model is a top-level record of `<ns>_MDL.ZED`; its definition is a node of the same name under `models` in `<ns>_GEO.ZED` (a character's skeleton is named without `MESH_`).
- A ZED's root value is its top-level record count.
- Texture names and palette ids are global in game (the first registered wins), so imports keep a palette's id only when the target does not use it, and new palettes take ids from a block of their own (1024 + 32 × hash).
- A texture's header `+0x08` is its texture index (1..N per TXR) and equals TEX0.TBP0; MIPTBP1/2 hold its mip levels' indices; TEX0.CBP binds the palette. Header `+0x0F` is `0x12` for a 16-bit palette, `0x13` for 32-bit.
- 16-bit palettes have no alpha, only the STP bit: a black entry without it is transparent.
- Retail models name textures in 16-byte fields, so a texture applied to them needs a name of at most 15 characters.
- A package's root holds .ZED, .ZAR and .MPS files; .RDR screens live inside ZARs (READERM.ZAR).

## Development

Run the backend and the Vite dev server side by side; the dev server proxies `/api` to the backend and reloads on save.

```bash
cd server
dotnet watch
```

```bash
cd web
npm run dev
```

Open http://localhost:5173. `npm run typecheck` checks the page without building it.

Tests use synthetic data, never game files. CI (`.github/workflows/ci.yml`) runs both on every push and pull request.

```bash
dotnet test tests/MapBuilder.Tests
```

```bash
cd web
npm test
```

## Status and limits

This is an actively developed tool, and support differs by game and asset type.

- **SOCOM II is the primary tested target.** The project is still being developed; not every editing workflow has been verified in-game.
- Map, model, and animation edits can round-trip through save and reopen. Imported models, imported textures, and image-created textures render in the editors, but not every combination has been tested in-game. Test on copies.
- **Runtime VRAM is limited.** Large textures or many newly added textures may exceed what a map can use in-game.
- An imported character can appear correctly in the editors, but spawning it in a mission also requires the relevant character records.
- SOCOM 3 and Combined Assault workflows vary by format and feature. GTA: San Andreas model export is partial, and FTB has not been tested.
- This repository does not include game assets or the CodeDesigner runtime source used to inject map items and launch new maps.

*SOCOM U.S. Navy SEALs is a trademark of Sony Interactive Entertainment. This project is not affiliated with or endorsed by Sony; it ships no game files.*
