# Asset Viewer: state and what is left

Branch `claude/lockdown-mcp-authoring` (PR #6), commits `1815d72` (viewer rebuild, previews, movies), `574d8bf`
(in-place editing) and the follow-up that checked the editors in the running app. `server/.../MPS/MapPoints.cs` and
`tests/.../MapPointsTests.cs` hold the user's own uncommitted work: leave them alone.

## Where things are

- Page: `web/src/webview/asset-viewer/` — `AssetViewerPage.tsx` (view), `asset-viewer-page.ts` (logic, signals),
  `editors.tsx` (RDR tree, AI map / point tables, text, byte patch, icon.sys form), `viewer-client.ts` (commands).
- Server: `server/Services/AssetService.Viewer.cs` (VIEWER_LIST / VIEWER_PREVIEW / VIEWER_EXTRACT),
  `AssetService.ViewerEdit.cs` (VIEWER_EDIT), `AssetService.Movies.cs` (VIEWER_MOVIE + `/api/viewer/movie/{name}`).
- Formats: `CodeDesigner.Ps2.FileFormats/Ps2/` (SPU ADPCM, PS2 icons, raw screens, PSS / ELF info, PSS audio),
  `Zipper/RDR/RdrDocument.cs` (RDR writer), `Zipper/ZED/ZedSplice.cs` (replace one record in place),
  `Zipper/ZED/ZedIndex.cs` (big archives read by record table).
- Tests: `tests/MapBuilder.Tests/ViewerTests.cs`, `RdrDocumentTests.cs`. Disc checks run with
  `MAPBUILDER_DISC=E:\ dotnet test --filter Every_disc` (every .rdr round-trips; every ZED/ZAR splices in place).

## How editing works

Every edit is `VIEWER_EDIT {InputPath, AssetJson: {inner, op, …}}`. It opens (or reuses) the asset session of the
package (`EditPackageOf`: the .ZDB, or a loose file's folder), rebuilds the root file with the record spliced in
(`Rebuild` → `ZedSplice.Replace`), and records it with `ApplyEdit` — so `ASSET_UNDO` / `ASSET_REDO` / `ASSET_SAVE`
work as in the editors. Listings and previews read a package's bytes from its session once it has edits
(`EditedSessionOf`). Saving: a ZDB to a .ZDB path; a loose folder's changed files into a chosen folder (the disc is
read-only). Texture replacement uses the existing `ASSET_EDIT replaceTexture`; AI points use `setPoint`.

Verified on the retail disc through the server: RDR int / string edits, weapon duplicate (86 → 87), undo / redo /
save; AIMAPS map rename and point move in M51.ZDB (record positions stable); SYSTEM.CNF text; ICON.SYS title and
names; a byte patch; LIST.ICO texture; PULOADGP.RAW screen. 149 server tests and 37 web tests pass; typecheck passes.

## Checked in the running app

Every editor was used on the retail disc through the page: text Apply / Revert, RDR click-to-edit, ⧉ / ✕, AI map
and point rows, icon.sys Apply, byte patch Write, Edit › Undo / Redo (menu and Ctrl+Z / Y), the unsaved badge.
Found and fixed on the way:

- Text edits turned CRLF into LF (the textarea normalises line breaks; SYSTEM.CNF went 57 → 55 bytes). The editor
  now writes the file's own line breaks back.
- Retail RDR files share atoms between lists (sounds.rdr: 39k references, e.g. every DYNAMIC_WEIGHTS points at one
  0.05). The tree showed those as an uneditable `null` ("cycle"); shared values now show with ×N and a warning that a
  change shows in each place; only a list met again comes back as `[atom, "shared", count]`.
- RDR int / float values had no colour (`.t-number` never matched).
- The AI map table overflowed the preview (Max, Cell scale and Apply off-screen). Bounds now stack as Min / Max /
  Cell lines in one cell at six significant digits; a component left as shown keeps its exact value on Apply.
- A folder listing showed a loose file's disc size after an edit; it shows the edited size.
- Open RDR branches are keyed by path ("r/0/3"), and a remove moves later siblings' keys up one, so branches stay
  open after duplicate / remove. (Undo of a remove can't know the shift: those branches close.)
- Edits in other packages: `+N elsewhere` badge and Edit › Go to … (from `ASSET_SESSIONS`), so they can be found and
  saved.
- `ViewerService` (`VIEWER_COMPANIONS` / `VIEWER_PALETTE` / `VIEWER_LOOSE_PACKAGE`) was dead and is removed. The
  `ZIPPER_*` commands stay: the MCP tools and the README's command reference use them.
- `docs/screenshots/asset.png` retaken (M51 › TRMG_MDL.ZED › RMG_turret).

`sounds.rdr` (248k atoms, 5.5 MB JSON) previews in ~0.5 s on the server and ~170 ms of page work; only open
branches render (400 items a page), so no lazy loading was needed. The disc test now checks that duplicating a nested
list leaves the rest of the tree as it was.

## Editors for structured package data

Selecting these in a package opens an editor in the preview (`scene-editors.tsx`, server `AssetService.ViewerScenes.cs`):

- **Palettes** (a texpal in a _PAL listing): click a swatch, set its colour and alpha, Apply; or Load colours from
  image (Edit › Palette colours from image…). ASSET_EDIT `setPalette` re-encodes the buf at its own depth and splices
  it; every M51 palette re-encodes byte for byte, so one swatch never disturbs the others. Textures redraw.
- **CLUTTER.ZAR**: placements by model (filter), position / yaw / scale with Apply, ✕ remove, Add a model at a
  position. Uses the session's moveClutter / addClutter / deleteClutter. Yaw or scale rebuild a placement upright
  (⚠ marks tilted ones); position alone keeps the matrix.
- **_GEO.ZED**: the scene node tree (filter), and per node its local transform (axes and position), bounds, kind,
  flags and model reference. VIEWER_EDIT `nparams` / `modelName` (`Geo/GeoEdits.cs`).
- ***ZANIM.ZAR**: the effects (filter); one shows its tracks and instructions, nested by IF / WHILE, with decoded
  arguments editable in place (numbers, vectors, object / animation pickers, flags) and each instruction's argument
  words in hex. VIEWER_EDIT `zanimField` / `zanimWord`; `ZanimFile.Layout` is the one table for reading and writing.
  Sizes never change: instructions are not added, removed or reordered, and IF term counts are read-only.

Checked in the running app on M51: each editor's edits, undo of all of them, Replace picture (a generated PNG fed to
the page's file input) and Save into a scratch folder (the saved copy reopens with the new texture). Saving a copy
elsewhere leaves the edits counted as unsaved, as in the editors: only saving over the package clears them.

## The AI (MCP) and the live page

`McpServer.Viewer.cs` adds `viewer_list`, `viewer_preview`, `viewer_edit` and `viewer_extract` over the VIEWER_* commands
(palettes and clutter stay on `assets_edit`: `setPalette`, `moveClutter` …). Previews are cut for a model's context: no
pixels / sound / mesh, an RDR subtree (`atom`, `depth`), GEO nodes to a depth (`node`, `query`), clutter pages, ZANIM effects
by index or `effectName`.

The page follows through the existing event stream: viewer tools publish `show` / `changed` with `focus.viewer` (the place
or item, and an RDR atom, GEO record, ZANIM sequence + offset, AI map or palette). `AssetViewerLogic.followAi` opens the
place, selects the item, opens the effect, expands the RDR tree to the atom, and marks the row (`aiMark`, `.av-ai-mark`,
scrolled into view). Any `changed` event on the package the viewer shows reloads it, so undo / redo and other pages' edits
show too. `ui_state` reports the viewer (`aiState`). Checked live: an MCP client browsing M51, editing a ZANIM field, a
deep sounds.rdr string, a GEO node and a palette, and undoing, with the page following each step.

## Still to do

1. **Structural ZANIM edits** (add / remove / reorder instructions, new effects) would need sizes and IF term counts
   rewritten; only values change today.
2. **Sounds and movies** are previewed, not edited (out of scope).
3. **INTRO_2.PSS** on the user's disc copy is 25% zero-filled sectors (likely a bad rip); converted, its sound runs
   ~30 s ahead after the first gap. Not a code problem.

## Running a test instance without touching the user's app

Build the server to a scratch folder and run it on another port, then point a temporary Vite config at it:
`dotnet build server/MapBuilder.Server.csproj -o <scratch>/srv`, `<scratch>/srv/MapBuilder.Server.exe --urls
http://127.0.0.1:8771`, and a Vite config with `server.proxy['/api'] = 'http://127.0.0.1:8771'` on port 5174 (Vite
listens on `localhost`, not 127.0.0.1). Playwright is not installed in `web/node_modules`; screenshots can be taken
with headless Edge over the DevTools protocol (`--remote-debugging-port`, then `Page.captureScreenshot`).
ffmpeg is installed via winget (`Gyan.FFmpeg`); the server finds it in `%LOCALAPPDATA%\Microsoft\WinGet\Packages`.
