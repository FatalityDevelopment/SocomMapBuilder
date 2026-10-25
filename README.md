<p align="center">
  <img src="docs/icon.svg" alt="Map Builder" width="112">
</p>

<h1 align="center">Map Builder</h1>

<p align="center">A map editor for SOCOM U.S. Navy SEALs II map packages (.ZDB), from the CodeDesigner team.</p>

---

Open a map package and edit what it places: move, rotate, scale, duplicate and delete props and clutter, place new clutter, and move the AIMAPS spawn and waypoint points. Save the result as a copy of the .ZDB or as a memory-card pack.

## Features

- **3D view** of the composed map (world model, props, clutter, sky) with textures, baked lighting, wireframe and X-ray shading.
- **Outliner** grouped by layer and model, with show / hide per layer or per model.
- **Tools**: select (click, Shift adds, Ctrl removes, box drag), move / rotate / scale gizmo with snapping, and place clutter on any surface.
- **Edits**: duplicate, delete, drop to ground, and exact position / yaw / scale fields. Everything is undoable.
- **Points**: AIMAPS player starts and waypoints, shown as pillars and movable on the ground.
- **Save**: write a .ZDB copy, or export a memory-card pack (`<NAME>.CDB`) of the changed archives.

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

The server binds to `127.0.0.1:8770`. To use another address, pass `--urls http://127.0.0.1:9000`.

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
                                                               └─ CodeDesigner.Ps2.FileFormats (Zipper codec)
```

Every edit is an `ASSET_*` command (`ASSET_OPEN`, `ASSET_MAP_SCENE`, `ASSET_EDIT`, `ASSET_SAVE` …) on an in-memory session. The page only previews; the backend owns the package. Nothing is written to disk until you save, and **Save copy** never overwrites the original.

## Notes

- Map Builder ships no game files. You need your own SOCOM II map packages.
- Edits change placements, not geometry. Model and texture editing are not part of Map Builder.
