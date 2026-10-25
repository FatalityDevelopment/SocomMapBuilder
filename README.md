<p align="center">
  <img src="docs/icon.svg" alt="Map Builder" width="112">
</p>

<h1 align="center">Map Builder</h1>

<p align="center">Map, model and animation editors for SOCOM U.S. Navy SEALs II packages (.ZDB), from the CodeDesigner team.</p>

---

Open a package and edit the map, its models (props, characters, weapons) and its animations. Every page works on the same session, so a prop opened from the map, a clip keyed in the Animation Editor and a retextured model all save together as a copy of the .ZDB or as a memory-card pack.

## Pages

The activity bar on the left switches pages; each keeps its state while you switch.

- **Map Builder**: the composed map (world model, props, clutter, sky) with textures and baked lighting. Move, rotate, scale, duplicate and delete props and clutter, place new clutter on any surface, drop to ground, and move the AIMAPS spawn and waypoint points. Facade trees face the camera as they do in game. Double-click a prop to open its model in the Model Editor.
- **Model Editor**: models, textures and characters. Select by object, visual, chunk, vertex or face; move / rotate / scale vertices; add planes, cubes or an OBJ; duplicate and delete faces; mirror, flip normals, snap to grid; retexture, replace a texture from a PNG, paint baked vertex colours; edit UVs; export OBJ or PNG. Converts models to SOCOM 3 / Combined Assault (and CA or GTA: San Andreas models to SOCOM II).
- **Animation Editor**: the package's character clips (MOTION_S.ZAR, editable) and the shared MOTION_P.ZAR (read-only), played on any skinned character. Create, duplicate, rename, delete and retime clips; key bones with the gizmo or exact fields. With the game's RUN folder available, weapons mount on the clip and fire their muzzle effects.
- **Asset Viewer**: browse .ZDB / .CDB / .ZAR / .ZED archives and nested archives; preview textures, models, GEO scenes (with a first-person spawn camera), RDR screens, AI maps and animations; extract entries.

Everything is undoable. Read-only sessions open SOCOM 3 / Combined Assault archives and GTA: San Andreas (.ISO or .IMG).

## Requirements

- .NET 8 SDK
- Node.js 20 or later

## Run

```bash
cd web
npm ci
npm run build
cd ../server
dotnet run
```

Then open http://127.0.0.1:8770 and choose **File › Open package…** (Ctrl+O). Paste the full path of a map package, such as `G:\RUN\MP\M51\M51.ZDB`, or an unpacked package folder.

In Visual Studio, open `MapBuilder.sln` and press F5 (after `npm run build` in `web/`); the launch profile uses the same address.

The server binds to `127.0.0.1:8770`. To use another address, pass `--urls http://127.0.0.1:9000`. If the page has not been built yet, the server still starts and says so in its console.

## Develop

Run the backend and the Vite dev server side by side. The dev server proxies `/api` to the backend and reloads on save.

```bash
cd server
dotnet watch
```

```bash
cd web
npm run dev
```

Then open http://localhost:5173.

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

## How it fits together

```
web/ (React + TypeScript + three.js) ──POST /api/command──▶ server/ (ASP.NET Core, .NET 8)
                                                               └─ CodeDesigner.Ps2.FileFormats (Zipper, CA, GTA codecs)
```

Every edit is an `ASSET_*` command (`ASSET_OPEN`, `ASSET_MAP_SCENE`, `ASSET_EDIT`, `ASSET_SAVE` …) on an in-memory session shared by all pages. The pages only preview; the backend owns the package. Nothing is written to disk until you save, and **Save copy** never overwrites the original. The Asset Viewer reads archives with `ZIPPER_*` commands and decodes them in the page.

`web/src/webview/` mirrors CodeDesigner's webview folders: the viewports, decoders and players are the same files, and each page's logic is CodeDesigner's, rendered by a React view over a small signals shim (`web/src/shared/signals.ts`).

## Notes

- Map Builder ships no game files. You need your own SOCOM II packages; weapon firing effects and model conversion also read the game's RUN folder.
- Save-game crafting and APACHE00 patch building are not part of Map Builder.
