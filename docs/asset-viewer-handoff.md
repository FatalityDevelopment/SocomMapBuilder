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

## Still to do

1. **Save and Replace picture from the page.** Both open a host dialog (save path / image file) that was not driven
   in this pass; both were verified through the server earlier. Click through once by hand.
2. **Not editable yet:** ZANIM effect scripts, GEO scene data, CLUTTER placements (edited in the Map Builder),
   palettes (no "replace palette" in the viewer), sounds and movies (out of scope). These all fall back to the byte
   patcher.
3. **INTRO_2.PSS** on the user's disc copy is 25% zero-filled sectors (likely a bad rip); converted, its sound runs
   ~30 s ahead after the first gap. Not a code problem.

## Running a test instance without touching the user's app

Build the server to a scratch folder and run it on another port, then point a temporary Vite config at it:
`dotnet build server/MapBuilder.Server.csproj -o <scratch>/srv`, `<scratch>/srv/MapBuilder.Server.exe --urls
http://127.0.0.1:8771`, and a Vite config with `server.proxy['/api'] = 'http://127.0.0.1:8771'` on port 5174 (Vite
listens on `localhost`, not 127.0.0.1). Playwright is not installed in `web/node_modules`; screenshots can be taken
with headless Edge over the DevTools protocol (`--remote-debugging-port`, then `Page.captureScreenshot`).
ffmpeg is installed via winget (`Gyan.FFmpeg`); the server finds it in `%LOCALAPPDATA%\Microsoft\WinGet\Packages`.
