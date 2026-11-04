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
- [Roadmap and work tracker](docs/ROADMAP.md)
- [Development](#development)
- [Status and limits](#status-and-limits)

## Pages

Use the activity bar to switch between tools. Editors load on their first visit, then remain mounted so their sessions, cameras and selections survive switching. The editors are connected: double-click a prop on the map to open it in the Model Editor, or double-click a model in the Asset Viewer to open it there.

### Map Builder

<img src="docs/screenshots/map.png" alt="SOCOM Map Builder map editor showing a composed level with world geometry, props, clutter, sky, textures, and baked lighting" width="850">

Inspect the assembled level with its world model, props, clutter, sky, textures, and baked lighting. Select and transform instances directly in the scene.

- Move, rotate, scale, duplicate and delete props and clutter; drop them to the ground.
- **Assets panel** (left): the package's props and library models, and your saved prefabs, as thumbnail tiles drawn with their textures. Click a tile to place it with the Place tool, or drag it onto the map to drop it where it lands.
- **Prefabs**: select props and clutter, then **Assets › Prefabs › Save selection as prefab…**. A prefab places again, in this map or any other that has its models, as one edit (one undo).
- Place on any surface with the Place tool (P), as clutter or as a GEO prop (**As prop**). Preview the model or prefab before clicking, choose yaw and scale (or random yaw and a random scale range), and optionally align it to the surface. Escape cancels placement. The Clutter layer must be visible and unlocked.
- **Clutter brush** (B): drag over the ground to scatter the picked model. Settings: radius, the spacing kept between placements (and from that model's clutter already there), density, and the steepest slope it paints on. Random yaw and scale make it look natural. Hold Ctrl, or tick Erase, to remove that model's clutter instead. One stroke is one undo.
- **Rotate** turns about any axis, not only yaw. **Scale** stretches GEO props along one axis; clutter keeps one scale, which is all its format holds. **Pivot**: the selection's centre, each item's own origin, or the first selected item. The status bar shows the move, turn or scale while you drag.
- **Snap to**: the increment (grid), the **surface** under each item (it slides over the ground and stays on it), or the nearest **vertex** under the cursor.
- **Align and distribute** a multi-selection on X, Y or Z: line up their minimums, centres or maximums, or spread the centres evenly (Item tab).
- **Map check** (Map tab) adds checks that need the drawn scene: clutter and props floating above the surface under them, a second copy stacked on the first, and the areas that draw far more triangles than the rest of the map. Click a problem to select what it is about.
- Set render resolution to 100%, 75%, or 50%, choose smooth or pixel texture filtering, and enable FPS, draw-call, triangle, and resource statistics. These rendering preferences are remembered in your browser. Render pixel density is capped at 2×; 50% resolution uses one quarter of the pixels of 100%.
- Move the AIMAPS spawn and waypoint points.
- Edit a multi-selection at once (move by, turn by, set scale); lock layers so clicks and box drags skip them; move a single prop along its own axes (Local).
- **Import from package…** (File menu or the Place tab): bring a prop, vehicle, weapon or character from another package, then place it.
- Facade trees face the camera, as they do in game.
- View style, grid, projection, snapping and placement preferences are remembered in your browser.
- Open **Help → Keyboard shortcuts…** (or press `?`) to search the controls.
- **Command search** (F3 or Ctrl+Shift+P): type a few letters of any menu command ("sv cp" finds *Save copy…*) and press Enter to run it. The Model Editor has it too.
- **History** (Map tab): every undo and redo step in order. Click a step to go back to before it, or forward to it.
- **Hide / isolate / show all** (H, Shift+H, Alt+H) changes only the view: hidden props and clutter are not drawn and cannot be picked. A badge in the status bar counts them; click it to show them again.
- **Measure** (M): click two points to see the distance in units and in metres (about 10.5 units to a metre).
- **Walk mode** (Shift+F, or View menu): walk the map at eye height with W A S D (Shift runs); the eye stays 16 units above the ground under it. Escape stops.
- **Camera bookmarks** (View › Bookmark this view…): save a view by name and jump back to it later; they are kept per package in your browser.
- **Autosave**: every 3 minutes, a recovery copy of each session with unsaved edits goes to `%LOCALAPPDATA%\MapBuilder\autosave\<package>.autosave.ZDB` (set `MAPBUILDER_AUTOSAVE` for another folder, `MAPBUILDER_AUTOSAVE_MINUTES` for another interval, 0 to turn it off). The session itself stays unsaved.
- Loading warnings stay visible with a **Retry map loading** action. A failed package switch clears the old map, and scene-loading failures block editing until a reload succeeds.

### Model Editor

<img src="docs/screenshots/character.png" alt="SOCOM Map Builder character model editing view" width="850">

<img src="docs/screenshots/weapon.png" alt="SOCOM Map Builder weapon model editing view" width="850">

Edit props, vehicles, weapons, and skinned characters using object-level and geometry-level tools.

- Select by object, visual, chunk, vertex or face; move / rotate / scale; mirror, flip normals, snap to grid.
- **Modeling** (Item tab, props / weapons / vehicles): **Extrude** the selected faces along their normal with walls round them, or **Inset** them (I) into a smaller copy with a ring of faces. **Soft radius** makes nearby vertices follow a move, fading out; **Mirror X** moves the selection's mirror image across X = 0 with it.
- Add planes, cubes or an OBJ; duplicate and delete faces; edit UVs.
- Paint baked vertex colours, and **bake ambient occlusion** into them (Paint tab): creases, undersides and insides darken so a prop looks seated in game.
- **Texture paint** (T): paint the texture under the cursor in Colour (keeps the texture's shading), Dirt, Scuff or Blur. One stroke is one undo.
- **Character presets** (Model tab): apply a body shape (Heavy build, Lean build) or a gear set (Plate carrier, Rucksack) to any skinned character as one edit. The AI saves new ones with `characters_save_preset`.
- **Characters** (Paint tab): a **skin weight view** colours the body by how much a bone moves each vertex (blue none, red fully), and the **weight brush** (B) adds, subtracts or sets that bone's weight (one stroke is one undo). **Pose** the character with any clip and scrub its time to see gear and joints bend while you paint; move, rotate and scale wait for the bind pose.
- **Textures**: retexture sections, replace a texture from any image (PNG, JPG, BMP, WebP), create a **new texture from an image** (with its own palette, or reusing one), **import a texture from another package**, create a **palette from an image**, export PNG.
- **Import from package…** (Assets panel or File menu): copy a model from another package.
- Export OBJ; convert models to SOCOM 3 / Combined Assault, and CA or GTA: San Andreas models to SOCOM II.
- **Command search** (F3 or Ctrl+Shift+P) and a **History** tab in the bottom panel, as in the Map Builder.

For a softer silhouette, choose **File → Export rounded model as OBJ** on an open model or **File → Round an OBJ file** for an existing export. One subdivision level multiplies the triangle count by four. Strength controls how far the surface moves, and the crease angle controls which edges stay sharp. Use the optional XYZ center, radius and scale to shape a local region. The OBJ keeps its material references and needs its MTL and images alongside it.

The MCP tool `mesh_refine_obj` takes `inputPath`, a new `outputPath`, and the same `levels`, `strength` and `crease` controls. Its optional `brushes` array applies local shaping in order. For example, `{"center":[0,15,0],"radius":[5,3,4],"scale":[1.2,1,1.1]}` broadens a chest centered at that position in OBJ coordinates.

For a skinned SOCOM II character, use **Item → Body proportions** to adjust a bone’s thickness and length in the package. **Add → Rounded box** places geometry at the selection center using the target texture. Both edits support Undo. Their MCP equivalents are `assets_edit` with `scaleBones` and `addCharacterMesh` with a `box` shape and positive `radius`.

### Animation Editor

<img src="docs/screenshots/anim.png" alt="SOCOM Map Builder animation editor for character clips" width="850">

Preview character clips on skinned models. Package-local `MOTION_S.ZAR` clips are editable; the shared `MOTION_P.ZAR` is read-only.

- Create, duplicate, rename, delete and retime clips; key bones with the gizmo or exact fields.
- With the game's RUN folder available, weapons mount on the clip and fire their muzzle effects.

### Asset Viewer

<img src="docs/screenshots/asset.png" alt="SOCOM Map Builder asset viewer for browsing package files and archives" width="850">

Browse a whole disc (or any folder) down into packages and archives, preview what is there, and send it to the editor that works on it. The page has the editors' layout: the browser on the left, the preview in the middle, what the selection is and what can be done with it on the right.

- Open a folder (File › Open folder…, Ctrl+O) — the SOCOM II disc's drive, its RUN folder, or a folder of packages — and browse folders, .ZDB / .CDB packages and the .ZAR / .ZED archives inside them, with breadcrumbs, Back / Forward / Up, a filter and kind chips. Lists of thousands of records (RUN/SOUNDS/VAGSTORE.ZAR holds 10 942) scroll without slowing down.
- Package archives list what the editors list, from the same asset session: models, textures, palettes, GEO definitions and animation clips (a loose RUN/MOTION_P.ZAR borrows a session from a package beside it).
- Previews use the editors' own code: models in the Model Editor's viewport, a package's composed map in the Map Builder's (Show map), clips playing on a character in the Animation Editor's, textures through ASSET_TEXTURE.
- Every file type on the retail disc previews: memory-card icons (.ICO, in 3D with their texture) and icon lists, sounds (.VAG and the VAGSTORE / BNKSTORE stores, .BNK / .BD banks sample by sample) played in the page, the RAW / RGB loading screens, RDR trees, AI maps, text (.CNF), programs (.ELF, .IRX: sections, IOP module name), movies (.PSS: picture size, frame rate, length, and **Convert for preview**, which takes out its sound track (the PS2's "SShd" PCM stream, which ffmpeg does not read) and has ffmpeg join it to the picture as an H.264 / AAC MP4, kept in `%LOCALAPPDATA%\MapBuilder\movies`, so it plays in the page; needs ffmpeg on the server: `winget install Gyan.FFmpeg` or `MAPBUILDER_FFMPEG`), and a hex dump with strings for anything else.
- Actions: open in the Map Builder, Model Editor or Animation Editor; export a texture, screen or icon texture as PNG and a sound as WAV; extract any record as stored.
- **Import… / Replace… / Remove**: change a package's files or an archive's records; the result is written to a file you choose.
- **Edit in place** (Edit menu: Undo Ctrl+Z, Redo Ctrl+Y, Save changes… Ctrl+S): RDR values, strings and list items (duplicate / remove), AI map headers and points, text files, icon.sys titles, a byte patcher for any record, and pictures (textures, RAW / RGB screens, icon textures) replaced from an image. Edits go through the package's asset session (a .ZDB's, or a loose file's folder's) and are spliced into their archives in place, so every other byte stays as it was.

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

### Windows desktop app

After building the web client above, run `dotnet build desktop/MapBuilder.Desktop.csproj -c Release` from the repository root. Double-click `desktop/bin/Release/net8.0-windows/MapBuilder.Desktop.exe` to open the editor in a resizable WPF window. In Visual Studio, you can also select **MapBuilder.Desktop** as the startup project and press F5. The desktop app requires the [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/).

Opening the app starts the local server when needed. If MapBuilder is already running at `http://127.0.0.1:8770`, the window connects to that session. Closing the window stops a server it started and leaves a separately started server running.

### Editing map actions

Props with actions show an icon above them in the 3D view (one icon per kind: door, switch, pick up, plant charge, bomb, defuse, breach), including doors and breach points whose geometry is part of the world model. Click an icon, or select a prop, and the **Actions** tab opens on it. The tab says in plain words what an action is, shows the prop's actions as chips, and edits one in groups: *what the player does* (pick the kind by its icon), *prompt* (icon with preview, reach distance), *what happens* (mission valve, map animations, works only once) and *advanced*. A new action starts from what the map's other actions of that kind use; fields are checked as you type (whole numbers, a valve or animation the map lacks, a name several placements share). All the map's actions are listed by kind below. View › **Action icons** turns the icons off (remembered). **Placement actions…** in the Model Editor opens the map inspector filtered by model. Use **File → Save copy…** to write the edited map.

### Mission tables

The **Mission** tab reads the map's READERM.ZAR tables ([format](docs/formats/map-tables.md)): HUD objectives with their in-game text (titles and briefing lines editable), their map location (editable, and drawn as flags in 3D: View › **Objective flags**), valves and nav points; the actors who start on the map (display name, character, start points, teams) with their AI accuracy, courage and surrender; mission settings (AI respawn and recycle values, satchel timer) and the **weapons available** in the mission (its `Enable_*` valves) as checkboxes; and, read-only, the world's lighting, fog, draw distance and the map extents. Every change is one undo step. The MCP reads the same with `assets_map_tables` and changes it with `assets_edit mapTable` / `mapText`.

The same tab offers experimental climb and ladder surfaces and links to the map's mission tables. The disc inventory and limits of the traversal presets are recorded in [Map interactions](docs/map-interactions.md).

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
| Keyboard orbit | Arrow keys while held (Left / Right turn, Up / Down tilt, Shift faster) · Numpad 4 / 6 / 8 / 2 in 15° steps · Numpad 9 opposite side · Numpad + / − zoom (every 3D view) |
| Tools | V select · G move · R rotate · T scale · P place · B paint clutter (Ctrl erases) |
| Duplicate / delete / drop | Shift+D / X or Delete / End |
| Copy / paste clutter | Ctrl+C / Ctrl+V (also into another package that has the model) |
| Frame selected / frame map | F / Home |
| Views | Numpad 1 front · 3 right · 7 top · 5 orthographic; with Ctrl: back · left · bottom |
| Shading | Z cycles textured, solid, wire, X-ray |
| Lighting / backdrop | Baked · Studio · Unlit in the header / image icon in the activity bar |
| Undo / redo / save copy | Ctrl+Z / Ctrl+Y / Ctrl+S |
| Command search | F3 or Ctrl+Shift+P (Map Builder and Model Editor) |
| Hide / isolate / show all | H / Shift+H / Alt+H |
| Measure | M (click two points) |
| Walk mode | Shift+F; W A S D walk, Shift runs, Esc stops |
| Asset Viewer | Double-click / Enter opens · Backspace up · Alt+Left / Right back / forward · arrows move · Space plays a clip |

## How it fits together

```
web/  React 18 + TypeScript + three.js ──POST /api/command──▶ server/  ASP.NET Core (.NET 8)
                                                                 └─ CodeDesigner.Ps2.FileFormats
                                                                    (Zipper ZDB/ZED/ZAR/GEO/MDL/TXR/PAL/RDR,
                                                                     CA and GTA codecs, conversion)
```

- The backend owns the package. Every edit is an `ASSET_*` command on an in-memory session (one per package path, shared by all pages); the pages preview and send commands.
- The Asset Viewer lists and previews with `VIEWER_LIST` / `VIEWER_PREVIEW`; package archives come from an asset session, so models, textures, maps and clips are drawn with the editors' commands and viewports. It edits RDR tables, AI maps, text, icons, palettes, clutter placements, GEO scene nodes and ZANIM effect scripts in place (`VIEWER_EDIT`, or the session's `ASSET_EDIT` ops), with the editors' undo, redo and save. Disc formats only it reads (SPU ADPCM, memory-card icons, raw screens, PSS / ELF headers) are in `CodeDesigner.Ps2.FileFormats/Ps2`.
- `web/src/webview/` mirrors CodeDesigner's webview folders: the viewports, decoders and players are shared files, and each page's logic runs on a small dependency-tracking signals shim (`web/src/shared/signals.ts`) under a React view.

```
server/
  Program.cs                      command registry, static web root, /mcp and /api/events
  Services/McpServer.cs           MCP tools (assets_*, ui_*) over the command registry
  Services/UiBridge.cs            event stream to the pages, screenshot / state requests
  Services/AssetService*.cs       ASSET_* sessions, edits, undo, save
  Services/PackageFilesService.cs PACKAGE_EDIT (package files, archive records)
  Services/FileSystemService.cs   FS_* listings for the file browser
  CodeDesigner.Ps2.FileFormats/   format codecs (Zipper/Package: edits, import, textures)
web/src/
  AppShell.tsx                    activity bar and pages
  MapPage.tsx, viewport/          Map Builder
  webview/model-editor/           Model Editor
  webview/animation-editor/       Animation Editor
  webview/asset-viewer/           Asset Viewer
  shared/                         host (dialogs, file browser, import), signals, icons
```

## View

- **Lighting** (Map Builder and Model Editor header): **Baked** (default) draws the model's baked vertex colours as the game does, and lights models that have none with the scene lights; **Studio** lights everything with an ambient fill and a key light from the camera, ignoring baked colours (useful on night maps); **Unlit** shows textures at full brightness.
- **Backdrop** (activity bar, image icon): dark or a blueprint grid, for every 3D view and the Asset Viewer's previews. Remembered in the browser.

## AI (MCP server)

The server is also a [Model Context Protocol](https://modelcontextprotocol.io) server at `http://127.0.0.1:8770/mcp` (Streamable HTTP). The repository's `.mcp.json` registers it for Claude Code; elsewhere:

```bash
claude mcp add --transport http mapbuilder http://127.0.0.1:8770/mcp
```

The AI works on the same asset sessions as the pages, so you watch it work: the **AI activity** panel (activity bar, robot icon) lists every call with the reason the AI gave, and with **Follow AI** on, edits bring their page forward: the Map Builder reloads and frames the moved prop or point, the Model Editor opens the edited model, the Asset Viewer opens the edited record (an RDR value, a GEO node, a ZANIM instruction, a palette) and marks it. Browsing with `viewer_list` / `viewer_preview` moves the Asset Viewer along, and any edit to the package it shows reloads it. Edits stay undoable (`Ctrl+Z` undoes the AI's too) and nothing is written until a save.

| Tools | Do |
|---|---|
| `assets_open`, `assets_sessions`, `assets_status` | Open or join a package session |
| `assets_list_models`, `assets_describe_model`, `assets_model_mesh`, `assets_list_textures`, `assets_list_palettes`, `assets_texture` (as an image), `assets_map_scene`, `assets_map_points`, `assets_map_tables`, `assets_list_animations`, `assets_animation`, `assets_weapons` | Read |
| `assets_effects`, `assets_weapon_fx` | Read ZANIM effects and what firing a weapon plays |
| `viewer_list`, `viewer_preview`, `viewer_edit`, `viewer_extract` | Browse any folder, file or archive record as the Asset Viewer does, read its typed preview (RDR subtrees, AI maps, GEO scene nodes, clutter pages, ZANIM effects decoded, palettes), change it in place (RDR values and list items, AI map headers, text, bytes, pictures, icon.sys, GEO nparams / model references, ZANIM fields and words) and copy a record out; edits are undone and saved with `assets_undo` / `assets_save` on the session `viewer_edit` answers. Palettes and clutter change with `assets_edit` (`setPalette`, `moveClutter` …) |
| `assets_edit` (every `ASSET_EDIT` op; `dryRun: true` previews one without changing anything), `assets_undo`, `assets_redo`, `assets_save`, `assets_close` | Change, save and close |
| `assets_save_prefab`, `assets_list_prefabs`, `assets_delete_prefab` | Save placed props and clutter as a reusable group; `assets_edit {op: placePrefab}` places one in any map (one undo step) |
| `assets_validate`, `assets_history` | Lint a map (missing models, broken transforms, duplicate points, long texture names, VRAM budget); list the undo / redo steps |
| `assets_export`, `assets_entry` | Export a model to SOCOM 3 / CA; read or write out one of the package's files (with unsaved edits) |
| `fs_places`, `fs_list` | Find packages on disk |
| `package_inspect`, `package_edit` | List and change the files of a package or archive on disk (no session) |
| `weapons_list`, `weapons_template`, `weapons_texture`, `weapons_create` | Build a new weapon from a template in a weapon archive (model edits, a texture from an image) |
| `ui_screenshot`, `ui_state`, `ui_show`, `ui_focus`, `ui_view` | See and drive the app (screenshot of the 3D view, page / selection state, open a page or model, frame an item, shading / lighting / backdrop / camera preset, orbit and zoom) |
| `mapbuilder_command` | Any backend command from the reference below |

Tool arguments are checked against each tool's schema before it runs: unknown names, wrong types, values outside an `enum` or range, and a missing `reason` are errors. `assets_map_scene` pages with `offset` / `nextOffset`, and a reply too long for the model's context comes back as valid JSON with `truncated: true`.

Claude Code finds the tools when a session starts, so start Map Builder (`dotnet run`) before opening the Claude session; a session started without it sees no `mapbuilder` tools until it is restarted. Claude Code also asks once to approve the project's `.mcp.json` server.

The pages listen on `GET /api/events` (Server-Sent Events) and answer screenshot / state requests on `POST /api/ui/reply`. `/mcp` refuses requests from other sites' pages (a foreign `Origin` or a non-JSON body).

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
| `ASSET_SAVE_PREFAB` / `ASSET_LIST_PREFABS` / `ASSET_DELETE_PREFAB` | `AssetSession`, `AssetJson: {name, geoIndices?, clutterIndices?, origin?, replace?}` / `AssetName?` | Prefab files in `%LOCALAPPDATA%\MapBuilder\prefabs` (or `MAPBUILDER_PREFABS`) |
| `ASSET_HISTORY` / `ASSET_VALIDATE` | `AssetSession` (+ `AssetJson: {vramBudget?}` for validate) | The undo / redo steps; a map lint |
| `PACKAGE_EDIT` | `InputPath`, `OutputPath`, `Data: [ops]`, `DryRun?` | Add / replace / remove package files and archive records (refused while an open session has unsaved edits on the file) |
| `FS_PLACES` / `FS_LIST` | `InputPath` | Drives, common folders and folder listings |
| `VIEWER_EDIT` | `InputPath`, `AssetJson: {inner, op: rdrSet / rdrDuplicate / rdrRemove / mpsMap / text / bytes / image / iconSys / nparams / modelName / zanimField / zanimWord, …}` | One in-place edit through the package's asset session (undo / redo / save with `ASSET_UNDO` / `ASSET_REDO` / `ASSET_SAVE`). `nparams {node, matrix?, min?, max?, kind?, flags?}` and `modelName {node, name}` change a GEO scene node; `zanimField {node, offset, field, fieldValue}` and `zanimWord {node, offset, word, wordValue}` an effect script instruction (node: its Seq_Data record) |
| `VIEWER_MOVIE` | `InputPath` (a .PSS) | Converts a movie to MP4 once (ffmpeg) and answers its `/api/viewer/movie/…` stream URL |
| `VIEWER_LIST` / `VIEWER_PREVIEW` / `VIEWER_EXTRACT` | `InputPath`, `AssetJson: {inner?: [entry indices], sample?, rate?, effect?}` (+ `OutputPath` for extract) | Asset Viewer: what a folder, file or archive holds; a typed preview of one file or record; a record's bytes as stored |

Edit ops include `moveClutter`, `addClutter`, `deleteClutter`, `moveInstance`, `duplicateInstance`, `placeInstance`, `deleteInstance`, `movePoint`, `setPoint`, `setVertices`, `transformVertices`, `addMesh`, `deleteFaces`, `duplicateFaces`, `retexture`, `replaceTexture`, `tintVertexColors`, the animation ops, and:

- `placeInstance` `{model, position, yaw?, scale?, matrix?, name?, templateGeoIndex?}`: a new placed reference of a package model, copied from the placed reference nearest the position (or `templateGeoIndex`) and added beside it; answers `newGeoIndex`
- `setPoint` `{name, newName?, team?, kind?: start | waypoint | other, position?}`: changes an AIMAPS point in place (points cannot be added or removed: their table sits mid-file between structures that are not decoded yet)
- `importModel` `{sourceSession | source, archive, name, targetArchive?, newName?}`
- `importTexture` `{sourceSession | source, name, sourceNamespace?, namespace?, newName?}`
- `addTexture` `{name, pngBase64 | imagePath, namespace?, width?, height?, maxSize?, paletteId?}`
- `addPalette` `{pngBase64 | imagePath, namespace?, is16?}`
- `setPalette` `{archive, id, rgba: [1024 bytes] | pngBase64 | imagePath}`: sets texpal_{id}'s 256 colours in place, keeping its depth (an image gives its first 256 pixels, row by row)
- `placePrefab` `{prefab, position, yaw?}`: every item of a saved prefab around the position, turned by yaw, as one batch (all or nothing)

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

Browser regression checks exercise rendering, placement, layer visibility and locks, saved preferences, shortcuts, and failed-load recovery with synthetic fixtures. With the backend and Vite dev server running, run:

```bash
cd web
npm run test:browser
```

The checks use system Chromium when available at `/usr/bin/chromium`. Otherwise install a Playwright browser with `npx playwright install chromium`, or set `CHROMIUM_PATH` to your Chromium executable. `MAP_BUILDER_URL` can select a different Vite development address; `MAP_BUILDER_MCP_URL` can select a different MCP endpoint (default `http://127.0.0.1:8770/mcp`). These checks require the Vite server because they also exercise viewport modules directly; they do not require game files or modify package files.

## Status and limits

This is an actively developed tool, and support differs by game and asset type.

- **SOCOM II is the primary tested target.** The project is still being developed; not every editing workflow has been verified in-game.
- Map, model, and animation edits can round-trip through save and reopen. Imported models, imported textures, and image-created textures render in the editors, but not every combination has been tested in-game. Test on copies.
- **Runtime VRAM is limited.** Large textures or many newly added textures may exceed what a map can use in-game.
- An imported character can appear correctly in the editors, but spawning it in a mission also requires the relevant character records.
- SOCOM 3 and Combined Assault workflows vary by format and feature. GTA: San Andreas model export is partial, and FTB has not been tested.
- This repository does not include game assets or the CodeDesigner runtime source used to inject map items and launch new maps.

*SOCOM U.S. Navy SEALs is a trademark of Sony Interactive Entertainment. This project is not affiliated with or endorsed by Sony; it ships no game files.*
