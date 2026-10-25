<p align="center">
  <img src="docs/icon.svg" alt="SOCOM II Map Builder" width="112">
</p>

<h1 align="center">SOCOM II Map Builder</h1>

<p align="center">Map, model, texture and animation editors for <em>SOCOM U.S. Navy SEALs II</em> packages (.ZDB / .CDB), from the CodeDesigner team.</p>

---

Open a package and edit the map, the models on it (props, vehicles, characters, weapons), their textures and palettes, and the character animations. Every page works on the same in-memory session, so a prop moved on the map, a texture replaced in the Model Editor and a clip keyed in the Animation Editor all save together: as a copy of the .ZDB, or as a memory-card pack the game loads from `mc0:`.

## Contents

- [Pages](#pages)
- [Getting started](#getting-started)
- [Working with packages](#working-with-packages)
- [Controls](#controls)
- [How it fits together](#how-it-fits-together)
- [Command reference](#command-reference)
- [Format notes](#format-notes)
- [Development](#development)
- [Status and limits](#status-and-limits)

## Pages

The activity bar on the left switches pages; each keeps its state while you switch, and pages hand work to each other (double-click a prop on the map to open it in the Model Editor, right-click a model in the Asset Viewer to edit it).

### Map Builder

The composed map (world model, props, clutter, sky) with its textures and baked lighting.

- Move, rotate, scale, duplicate and delete props and clutter; drop them to the ground.
- Place clutter on any surface with the Place tool; pick the model, yaw (or random yaw) and scale.
- Move the AIMAPS spawn and waypoint points.
- **Import from package…** (File menu or the Place tab): bring a prop, vehicle, weapon or character from another package, then place it.
- Facade trees face the camera, as they do in game.

### Model Editor

Props, vehicles, weapons and skinned characters.

- Select by object, visual, chunk, vertex or face; move / rotate / scale; mirror, flip normals, snap to grid.
- Add planes, cubes or an OBJ; duplicate and delete faces; edit UVs.
- Paint baked vertex colours.
- **Textures**: retexture sections, replace a texture from any image (PNG, JPG, BMP, WebP), create a **new texture from an image** (with its own palette, or reusing one), **import a texture from another package**, create a **palette from an image**, export PNG.
- **Import from package…** (Assets panel or File menu): copy a model from another package.
- Export OBJ; convert models to SOCOM 3 / Combined Assault, and CA or GTA: San Andreas models to SOCOM II.

### Animation Editor

The package's character clips (MOTION_S.ZAR, editable) and the shared MOTION_P.ZAR (read-only), played on any skinned character.

- Create, duplicate, rename, delete and retime clips; key bones with the gizmo or exact fields.
- With the game's RUN folder available, weapons mount on the clip and fire their muzzle effects.

### Asset Viewer

A file manager for packages and archives.

- Browse .ZDB / .CDB / .ZAR / .ZED files and the archives inside them, in a list with icons and thumbnails, a path bar (Up / Backspace, breadcrumbs) and a filter.
- Lists match the editors: model archives list models (characters, weapons), TXR archives textures (as a list or icons), PAL archives palettes (with a swatch preview), GEO archives definitions, CLUTTER.ZAR its clutter models with their placement counts. Records that hold records open like folders.
- Previews: models (decoded by the server, textured), textures, palettes, GEO scenes with a spawn camera, RDR screens, AI maps, animations. The sidebar shows the textures of what is open.
- **Right-click** an item to open it in the editor it needs (Model Editor, Animation Editor, Map Builder), export a texture as PNG, extract it, replace or remove it.
- **Import…**: add .ZED / .ZAR / .MPS files to a package (or merge another package's files), or any file (an .RDR, say) to a .ZAR / .ZED archive. Replace and Remove work at both levels.

Edits in the editors are undoable. SOCOM 3 / Combined Assault archives and GTA: San Andreas (.ISO or .IMG) open read-only.

## Getting started

### Requirements

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Node.js](https://nodejs.org/) 20 or later
- Your own SOCOM II packages (Map Builder ships no game files)

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

Open http://127.0.0.1:8770 and choose **File › Open package…** (Ctrl+O). The file browser lists your drives and folders; pick a map package such as `RUN\MP\M51\M51.ZDB`, a .CDB pack, a loose .ZED, or an unpacked package folder.

In Visual Studio, open `MapBuilder.sln` and press F5 (after `npm run build` in `web/`); the launch profile uses the same address.

The server binds to `127.0.0.1:8770`, so only your own browser can reach it. Pass `--urls http://127.0.0.1:9000` for another port. If the page has not been built, the server still starts and says so in its console.

## Working with packages

- **Nothing is written until you save.** Editor edits live in the session; **Save copy** (Ctrl+S) writes a new .ZDB (or .CDB), and **Export memory-card pack** writes `<NAME>.CDB` for `mc0:/BASCUS-97275<NAME>/` (pack names start with `m`, at most 7 letters/digits).
- **Asset Viewer edits** (Import / Replace / Remove) ask where to save. Saving over the open file keeps a `<name>.bak` the first time.
- **Work on copies.** Keep your original packages untouched and edit copies.
- **Importing a model** copies its MDL record, its GEO definition (a character's skeleton), every texture its sections name with their mip levels, their palettes, and any models its definition is built from. Undo removes the whole import.
- **New textures** are 8-bit indexed, a power of two a side (8–512, default: the image rounded, at most 256), with a 256-colour palette made from the image (32-bit when the image has soft alpha, else 16-bit like most retail palettes).

## Controls

| Action | Keys |
|---|---|
| Fly | W A S D, Q / E down / up, Shift faster (viewport focused) |
| Orbit / pan / zoom | Middle drag or Alt + left drag / right drag / wheel |
| Tools | V select · G move · R rotate · T scale · P place |
| Duplicate / delete / drop | Shift+D / X or Delete / End |
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

Things the editors rely on, checked against retail packages:

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

## Status and limits

- Map, model and animation edits round-trip through save and reopen. Imported models and textures and textures made from images render in the editors but have not all been tested in game yet; test on copies.
- Runtime VRAM for map props is limited: large or many new textures may not all fit in game.
- An imported character appears in the editors; spawning it in a mission also needs its character records.
- Save-game crafting and APACHE00 / DNAS patch tools are not part of Map Builder.

*SOCOM U.S. Navy SEALs is a trademark of Sony Interactive Entertainment. This project is not affiliated with or endorsed by Sony; it ships no game files.*
