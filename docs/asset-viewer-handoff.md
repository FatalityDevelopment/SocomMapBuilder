# Asset Viewer: state and what is left

Branch `claude/lockdown-mcp-authoring`, commits `1815d72` (viewer rebuild, previews, movies) and `574d8bf`
(in-place editing). Not pushed. `server/.../MPS/MapPoints.cs` and `tests/.../MapPointsTests.cs` hold the user's own
uncommitted work: leave them alone.

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

## Still to do

1. **Check the editing UI in the running app.** The edits were tested through the server and by unit tests; the
   page's editors were only partly clicked through (the RDR tree renders and shows the hints). Walk through each:
   RDR value click-to-edit, ⧉ / ✕ on list items, AI map Apply / Revert and point rows, text Apply, byte patch Write,
   icon.sys Apply, Replace picture (texture, RAW, icon), Edit menu Undo / Redo / Save (Ctrl+Z / Y / S), the
   "N unsaved · rev" badge. Fix layout issues (the edit tables were not seen at full size).
2. **RDR tree on very large files.** `sounds.rdr` (1.9 MB, ~230k atoms) sends the whole tree as JSON; check the
   time and memory, and page or lazy-load subtrees (`RdrDocument.Tree` → children on demand) if it is slow.
3. **RDR open branches after duplicate / remove.** Expanded rows are keyed by atom index (`rdrOpen`); inserting or
   removing atoms shifts later indices, so a few branches may collapse or open after such an edit. Key them by path
   instead if it bothers.
4. **Unsaved edits in another package.** The badge follows the current listing's package; edits in a package you
   navigated away from are kept in its session but not shown. Consider listing sessions with unsaved edits (the
   Edit menu, or a warning when leaving).
5. **Not editable yet:** ZANIM effect scripts, GEO scene data, CLUTTER placements (edited in the Map Builder),
   palettes (no "replace palette" in the viewer), sounds and movies (out of scope). These all fall back to the byte
   patcher.
6. **INTRO_2.PSS** on the user's disc copy is 25% zero-filled sectors (likely a bad rip); converted, its sound runs
   ~30 s ahead after the first gap. Not a code problem.
7. **Old server commands** `ZIPPER_*` / `VIEWER_COMPANIONS` / `VIEWER_PALETTE` / `VIEWER_LOOSE_PACKAGE` are no
   longer called by the page; check whether the MCP tools use them before removing.
8. **README screenshot** `docs/screenshots/asset.png` still shows the old viewer.
9. Push the branch / open a PR when the above is checked.

## Running a test instance without touching the user's app

Build the server to a scratch folder and run it on another port, then point a temporary Vite config at it:
`dotnet build server/MapBuilder.Server.csproj -o <scratch>/srv`, `<scratch>/srv/MapBuilder.Server.exe --urls
http://127.0.0.1:8771`, and a Vite config with `server.proxy['/api'] = 'http://127.0.0.1:8771'` on port 5174.
ffmpeg is installed via winget (`Gyan.FFmpeg`); the server finds it in `%LOCALAPPDATA%\Microsoft\WinGet\Packages`.
