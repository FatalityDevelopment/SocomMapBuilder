# Handover (2026-11-03)

Read this first, then the [ROADMAP](ROADMAP.md) (its *Rule*: update it in the same commit as the work) and the format docs in [`docs/formats`](formats/README.md).

## The goal

The aim is to add to SOCOM II's Map Builder and Model Editor the tools Unity and Blender give game makers, so people and AI (over the MCP) can build characters and maps. The work runs in waves; each wave is one branch and one PR.

| Wave | What | Branch / PR | State |
|---|---|---|---|
| Character gear (MCP) | shapes (rounded / puffed boxes, straps, subdivide, noise), conform, grit, triangle budget | #11 | merged |
| 1: Map Builder | asset browser with thumbnails, prefabs UI, clutter brush and eraser, full rotate, per-axis scale for props, pivot modes, surface / vertex snap, align and distribute, scene checks (floating, stacked, dense) | #12 | merged |
| 2: Model Editor | skin weight view and brush (`setSkinWeights`), pose with a clip while editing, `extrudeFaces` (extrude / inset), soft selection, Mirror X | #13 | merged |
| Desktop app | WPF + WebView2 window (`desktop/`), OBJ rounding (`mesh_refine_obj`) | #14 | merged |
| 3: Textures and performance | `bakeOcclusion`, texture paint (T), simplified low-detail gear (`MeshDecimate`, `lowDetail`), character presets (`applyCharacterPreset`, `characters_*` MCP tools) | **#15** | open, mergeable |
| Ignore `models/` | `.gitignore` | **#16** | open |
| 4: Workflow polish | see below | `claude/wave4` | **pushed, no PR yet** |

## Wave 4: where it stands (`claude/wave4`, built on `claude/wave3`)

**Done (tests and build pass; not tried live in the browser yet):**

- **Command search** (F3 or Ctrl+Shift+P) in the Map Builder: every menu command, matched by words in order. Code: `web/src/shared/commands.ts` (tested) and `shared/CommandPalette.tsx`.
- **History list** in the Map Builder's Map tab. Click a step to undo back to before it (or redo forward to it). Code: `shared/HistoryList.tsx`; `useMapSession.jump` does N undos / redos and then one reload; `AssetClient.history()` calls ASSET_HISTORY.
- **Autosave** (server): every 3 minutes, a recovery copy of each session with unsaved edits goes to `%LOCALAPPDATA%\MapBuilder\autosave\<package>.autosave.ZDB`. It uses the same writer as Save copy, so the session stays unsaved. Env vars: `MAPBUILDER_AUTOSAVE` (folder) and `MAPBUILDER_AUTOSAVE_MINUTES` (0 turns it off). ASSET_STATUS reports `autosave`. Code: `server/Services/AssetService.Autosave.cs`; test: `AutosaveTests`.
- **Map Builder view tools** (in `viewport/mapViewport.ts`):
  - **Hide / isolate / show all** (H, Shift+H, Alt+H): view only; hidden instances draw at zero scale and can't be picked. A status-bar badge shows the count.
  - **Measure tool** (M): click two points; the distance shows in units and about metres (10.5 units to a metre).
  - **Walk mode** (Shift+F, View menu): eye 16 units above the ground under it, WASD walks (Shift runs), Esc stops.
  - **Camera bookmarks** (View › Bookmark this view…): stored in localStorage per package path.

**Still to do in Wave 4:**

1. Try it live. Build `web`, run the worktree's server on a spare port (see *Running* below), open M51, and check each feature. Walk mode's ground-follow and the history jump are the riskiest.
2. Add the command search and the History list to the **Model Editor** too. It uses `ModelEditorPageLogic` (signals) with the same menu shape: wire `CommandPalette` with `commandsFromMenus(c.menus)` and a key in `onKey`.
3. Update the README (Map Builder bullets, Controls table: H, Shift+H, Alt+H, M, Shift+F, F3) and the ROADMAP (a Wave 4 entry like the Wave 1–3 ones).
4. Open the PR. While #15 is open, base it on `claude/wave3`; after #15 merges, base it on `master`.

**Later ideas** (from the original Unity / Blender list): glTF import and export (skinned characters too), launching the map in PCSX2 from play mode, AI routes and sight lines in walk mode, user layers and selection sets, loop cut / bevel / knife, editable gear stacks, occlusion for characters (into the texture), texture paint across uv seams.

## Open threads outside the waves

- **In-game tests: none of the character work, nor Waves 1–3, has been tried in the game.** The `mwinter` pack in `C:\Users\tholu\Desktop\CodeDesigner\Mods\SOCOM2\packs\mwinter` still has the older script-built gear. Test it like this:
  - Repack with `characters_pack`.
  - The user hosts a round.
  - In `C:/Users/tholu/Desktop/CodeDesigner/Mods/SOCOM2/bots`, run `python socom_bots.py loadpack mwinter mem`, then `python socom_bots.py ai mp_pow1 here team=0x40020000`.
- **`duplicateFaces`** copies the faces into only one section when several sections draw the same chunk block. `extrudeFaces` handles this; fix `duplicateFaces` the same way, in `MeshTopology`.
- **`setSkinWeights`** cannot give a vertex a bone its draw group does not list; that needs rebuilding the group.
- **`models/`** in the main checkout holds game-derived packs. Never commit them (#16 ignores the folder).

## Working conventions (the user's)

- **Commits and PRs:** the user says "Commit and push" or "Commit and open PR" when a round is done. Commits end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`; PR bodies end with the Claude Code line. Branch names: `claude/<topic>`.
- **Work in worktrees** under `.claude/worktrees/<name>` (git ignores the folder). The main checkout (`C:\Users\tholu\Desktop\MapBuilder`) may hold the user's own uncommitted work: never switch its branch or touch its files.
- **Port 8770 belongs to the user's desktop app** (`MapBuilder.Desktop.exe` starts its own server). For a branch's server use port 8790, started with `cd <worktree>/server && dotnet run -c Release --no-launch-profile --urls http://127.0.0.1:8790` in the background. Open `http://127.0.0.1:8790` in the browser pane. The server caches the page at start, so restart it after `npm run build`.
- **Tests:**
  - .NET, in Debug (the desktop app locks Release): `MB_MAP_ZDB='C:\Users\tholu\Desktop\M51.ZDB' dotnet test tests/MapBuilder.Tests -c Debug`.
  - Tests that open M51 must be in `[Collection(MapPackageCollection.Name)]`: one package path is one session, so tests running in parallel would close each other's session.
  - Web, from `web/`: `npm ci`, `npx tsc --noEmit -p .`, `npx vitest run`, `npm run build`.
  - **Watch for tests that pass without checking anything.** Several real-map tests return early when M51 is missing. Make sure the thing you measure exists (assert it); the extrude test once passed with nothing checked.
- **MCP from scripts:** the helper in the scratchpad, `mcp9.sh <tool> <args.json>`, posts a `tools/call` to `http://127.0.0.1:8790/mcp`. Every tool needs a `reason`. `ui_show {page, session, archive?, model?}` points the open page at a session; `ui_view` moves the camera; `ui_screenshot` takes a picture.
- **New edit ops** go in `EditOps.All` (schema and checks; a field with no `type` accepts anything) and in the `Edit` switch in `AssetService.cs`. The test `EditOpsSchemaTests` keeps the two in step.
