# Roadmap and work tracker

The goal: people **and AI over MCP** can add, edit and delete every SOCOM asset and build maps. This file tracks the work toward that so any session (human or Claude) can pick it up where the last one stopped.

## How to resume

- Branch: `claude/lockdown-mcp-authoring` (from `master`).
- Build and test:
  ```bash
  dotnet build MapBuilder.sln -c Release
  dotnet test tests/MapBuilder.Tests
  ```
  ```bash
  cd web
  npm run typecheck
  npm test
  npm run build
  ```
  Browser checks (backend and `npm run dev` running): `npm run test:browser`.
- **Rule:** update this file in the same commit as the work. Tick the item, add a one-line note (what changed, which test covers it, anything left over) and rewrite *In progress / next up*.
- Tests use synthetic data only, never game files.

## In progress / next up

- **Next candidates:** a decodable synthetic mesh plus a TXR/PAL fixture (so the mesh, texture, import and animation ops get tests); Map Builder UI for prefabs; placing GEO props from the UI; the browser smoke edit flow; paging `assets_model_mesh` arrays.
- Note for fixtures: a ZDB of **type 1** (header `0xC8`/`0x58`) is detected as SOCOM 3 / CA and opens read-only. SOCOM II packages are type 2.
- Trying the app without game files: `MAPBUILDER_FIXTURE_OUT=<dir> dotnet test tests/MapBuilder.Tests -c Release --filter FixtureExport` writes the synthetic package (`<dir>/TST` and `<dir>/TST.ZDB`). Its models are opaque, so the page warns that 2 models are not drawn; everything else works. A second backend can run beside yours: `dotnet run -c Release --urls http://127.0.0.1:8790` in `server/` (build `web/` first).
- Note for running tests while the app is open: `dotnet run` locks `server/bin/Debug`, so run `dotnet test tests/MapBuilder.Tests -c Release`.

## Status checklist

### Phase 0: tracker
- [x] This file, linked from the README Contents.

### Phase 1: lock down what exists

**1a. Testability**
- [x] `InternalsVisibleTo` for `MapBuilder.Tests` in `server/MapBuilder.Server.csproj`; the test project references the server (plus `Microsoft.AspNetCore.App`).
- [x] `tests/MapBuilder.Tests/Fixtures/SyntheticPackage.cs`: a loose package folder `TST/` (GEO with worldmodel and two `crate` references, MDL records `crate` and `tree`, CLUTTER.ZAR with two `tree` placements, AIMAPS.MPS), `WriteZdb()` for a ZDB copy, and `Backend`, which runs commands like `/api/command`. *Left over:* the MDL records are opaque bytes, so mesh ops can't run on it yet, and there is no TXR/PAL pair. A decodable synthetic mesh is the next fixture to add.

**1b. Characterisation tests** (pin today's behaviour)
- [~] `EditOpsTests.cs`: the map ops (clutter move/add/delete, instance move/duplicate/place/delete, movePoint, setPoint), each followed by undo back to the original bytes and redo; plus revision/label/dirty and unknown op/session. *Left over:* setVertices, transformVertices, deleteFaces/duplicateFaces, addMesh, the texture ops (addTexture/addPalette/replaceTexture/retexture/tint) and the animation ops all need the mesh/TXR fixture.
- [x] Batches are in `SessionTests.cs`: one undo step, a failing op rolls back the ops before it, a new edit clears redo, empty history.
- [x] `SessionTests.cs`: join an existing session, missing path, ZDB save keeps `.bak`, save as a copy, no-changes save, pack contents and pack-name rules.
- [ ] `ImportTests.cs`: importModel and importTexture between sessions, with undo (needs the mesh/TXR fixture).
- [x] `PackageEditTests.cs`: PACKAGE_EDIT add, remove, replace (the source is left alone), merge, refusals, unknown op.
- [x] `McpServerTests.cs`: initialize; every tool has an object schema whose required fields (including `reason`) are declared; unknown tool or method gives a JSON-RPC error; a failing command is a tool error; open → edit → undo through the tools; edit errors are tool errors. `McpServer.Dispatch` is `internal` for this.

**1c. Hardening** (each fix lands with a test)
- [x] Opening a loose package folder twice made two sessions, because `ZipperPackage.SourcePath` for a folder is `<dir>\<name>`, not the folder. The page and the AI therefore never shared edits on an unpacked package. Sessions now remember the path they were opened with (`AssetSession.OpenPath`). Test: `SessionTests.Opening_the_same_package_joins_its_session`.
- [x] Pack export from a **loose folder** used the file's disk path as the archive name and failed with "Archive name too long". `PackageSave.ArchivePath` now takes the path from a `RUN\` folder on the disk path when there is one, else uses `RUN\MP\<FOLDER>\<leaf>`; `archiveDir` (ASSET_SAVE / `assets_save`) overrides it. Test: `SessionTests.A_pack_from_a_loose_folder_uses_in_game_paths`.
- [x] Input checks: a `Vec(name, lengths)` / `Number` helper in AssetService. Arrays must have the right length (position 3, uv 2, colour 3/4, matrix 16, translate/rotate/scale/pivot/multiply/add 3), hold only numbers, and fit a float. Placement scale must be > 0 and vertex scale non-zero. Used by every op that reads vectors (movePoint, setPoint, placements, setVertices, transformVertices, duplicateFaces, tint). Test: `HardeningTests.Bad_input_is_an_error_and_changes_nothing` (14 cases).
- [x] `faces` / `vertices` keys are parsed with `OffsetKey` (TryParse) and give a clear error.
- [x] Error mapping: `WithSession` also catches IndexOutOfRange, Format, Json, IO and UnauthorizedAccess; `McpServer.Call` also catches IO, UnauthorizedAccess, InvalidData, NotSupported and IndexOutOfRange. Test: the missing-image case above.
- [x] Dirty tracking: `AssetSession.Clean` holds each leaf's on-disk bytes. A leaf stays dirty only while it differs from them, so undoing back to the file makes it clean. A ZDB save over the source, or a folder save in place, records the new clean bytes and the file stamp; a save to a copy or a pack leaves the session dirty. Tests: `HardeningTests.Undoing_back…`, `Saving_over_the_source…`, `Saving_a_copy…`.
- [x] A clean session whose file changed on disk **reloads in place**: it keeps its id and bumps its revision. Before, it was replaced under a new id, which broke the page holding the old one. Test: `A_clean_session_reloads_in_place_when_its_file_changes`. *Note:* the reload drops its undo history (there were no unsaved edits).
- [x] replaceTexture, retexture and tintVertexColors honour `label`.
- [x] Session races: `HandleOpen` runs under `OpenGate`; `HandleClose` waits for the session's `Gate`; imports read a source session under its `Gate` (`WithSource`, giving up after 5 s so two sessions importing from each other can't deadlock). Test: `Concurrent_opens_share_one_session`. *Not unit-tested:* the import lock (needs the mesh fixture).
- [x] PACKAGE_EDIT refuses a target that an open session has unsaved edits on, naming the session (`ZipperAssetService.UnsavedSessionOn`). Test: `Package_edit_refuses…`.
- [x] addClutter requires `position` (or `matrix`).
- [x] `MapPoints.Move` refuses cells above 8191 (the 13-bit fields). *Not unit-tested:* it needs an MPS grid wider than 8191 cells.
- [x] The Save doc comment now sits on `Save`.
- Before/after check: with the old server code, 15 of the 22 HardeningTests fail; with the fixes, all pass.

**1d. Web**
- [x] Vitest for the selection-edit bookkeeping: `deleteOps`, `duplicateOps`, `addedClutter` and `pickDuplicates` moved from `useMapEdits.ts` into `mapModel.ts`, tested in `mapModel.test.ts` (delete/duplicate order, batch result parsing, picking the copies).
- [ ] Browser smoke: edit → undo → save-request flow. *Not done:* it needs the backend and the Vite dev server running; add it to `web/scripts/browser-smoke.cjs` next to `checkWorkflow`.

### Phase 2: MCP authoring
- [x] Typed schemas: helpers `E` (enum), `N` (number/integer bounds) and `A` (typed array items, min/max items); `additionalProperties:false` on every tool. Enums cover the map scene kind, the save mode, the ui_show page and the ui_view shading/lighting/backdrop/view/frame. `assets_edit.op` is a `oneOf` with one closed branch per op, generated from the op table in `server/Services/EditOps.cs`. The same table checks each op (and each op in a batch) before it runs: unknown fields (typos), missing required fields, wrong types, enums, ranges. Tests: `EditOpsSchemaTests`, including one that keeps the table and the `Edit` switch in AssetService.cs in step. **When you add an op, add it to `EditOps.All`.**
- [x] Strict arguments: `McpServer.CheckArguments` runs before every tool and rejects unknown names, missing required values (a blank `reason` included), wrong types (an integer must be integral), enum misses, out-of-range numbers and bad array items. Tests: `AuthoringTests.Map_scene_arguments_are_checked` and `View_arguments_are_checked`. `web/scripts/browser-smoke.cjs` now passes `reason`.
- [x] Dry run: `ASSET_EDIT {dryRun:true}` (batches too) runs the edit, then takes it back with the session's revision, dirty set and redo stack restored; `assets_edit` takes `dryRun`. `PACKAGE_EDIT` / `package_edit` take `DryRun` / `dryRun` and write nothing. Tests: `AuthoringTests.A_dry_run_*` and `Package_edit_dry_run_writes_nothing`. `weapons_create` takes `dryRun` too. It was already all or nothing: the two encode steps write nothing, and ZIPPER_WRITE_WEAPON writes once through SafeFile. That write now also refuses entry names already in the archive (before, they were silently added as duplicates) and a target with unsaved session edits. Tests: `WeaponWriteTests`.
- [~] Safe responses: an over-long reply becomes valid JSON (`truncated`, `totalCharacters`, `hint`, `start`); `assets_map_scene` takes `offset` and answers `matched` / `offset` / `nextOffset` (test: `The_scene_pages_with_offset_and_next_offset`); `ui_screenshot maxSize` is limited to 64..2048. *Left over:* paging for `assets_model_mesh` arrays; no unit test for the truncation path.
- [x] Tool annotations: `destructiveHint` (save, close, entry, export, package_edit, weapons_create, mapbuilder_command), `idempotentHint` (= read-only), `openWorldHint:false`; `assets_open` is no longer read-only. Test: `Tools_declare_closed_schemas_and_hints`.
- [~] `ASSET_VALIDATE` / `assets_validate`: errors for missing models (props and clutter), non-finite transforms, duplicate point names and no map scene; warnings for zero scale, no AIMAPS / no start points, texture names over 15 characters and a `vramBudget` overrun; info for items outside the AI map. `ok` is false on any error. Tests: `A_sound_map_validates`, `Missing_models_are_errors`, `The_validate_tool_answers_the_report`. The Map tab has a **Problems** card: it checks when the tab opens and after each edit while open (and has a Check map button), lists errors first, and clicking a problem selects and frames its prop, clutter or point (`problemTarget` / `sortProblems` in `mapModel.ts`, tested in `mapModel.test.ts`). Tried in the app on the synthetic package: it showed `crate_2` outside the AI map, the click selected it, and after an AI move it showed "No problems found". *Left over:* a better VRAM model (it sums texdat sizes).
- [x] Prefabs (`server/Services/AssetService.Prefabs.cs`): `ASSET_SAVE_PREFAB` saves placed references (geoIndices) and clutter (clutterIndices) relative to an origin (default: the centre on the ground) as `<name>.prefab.json` in `%LOCALAPPDATA%\MapBuilder\prefabs` (`MAPBUILDER_PREFABS` overrides it). There are also `ASSET_LIST_PREFABS` and `ASSET_DELETE_PREFAB`. The `placePrefab {prefab, position, yaw?}` op is a batch of placeInstance/addClutter, so it is all or nothing, one undo step, and `dryRun` works on it. MCP: `assets_save_prefab`, `assets_list_prefabs`, `assets_delete_prefab`. Tests: `PrefabTests` (6). *Left over:* Map Builder UI ("Save selection as prefab" and a prefab list in the Place tab); prefabs do not carry AIMAPS points.
- [x] `ASSET_HISTORY` / `assets_history`: undo and redo steps (most recent first, with the archives each touched), revision, dirty. Test: `History_lists_undo_and_redo_most_recent_first`.

## Known issues found (2026-11-02 review)

Server (`server/Services/AssetService.cs` unless noted):
- `movePoint` reads `pos[2]` without a length check: a short `position` throws IndexOutOfRange (L116).
- `FaceMap` uses `int.Parse` on face keys (L464): bad keys throw FormatException.
- `WithSession` (L924) and `McpServer.Call` (L186) don't catch IndexOutOfRange, FileNotFound or IOException, so they reach the generic handler as server errors.
- `Dirty` only grows: it isn't cleared on save or by undo, so a saved session never counts as clean and `HandleOpen` never refreshes it from disk.
- replaceTexture, retexture and tintVertexColors ignore `label` (L329/392/401).
- `HandleOpen` is check-then-add (two opens at once can create two sessions); `HandleClose` doesn't take `s.Gate`; importing from another session reads it without its lock.
- `PACKAGE_EDIT` writes package files on disk without knowing about open sessions; a later save can throw KeyNotFound (PackageSave.cs:48).
- No finite checks on vertex data or on a caller-supplied `transformVertices` matrix.
- `addClutter` with no position silently places at the origin.
- `MapPoints.Move` shifts cell x/z into 13-bit fields without masking (MapPoints.cs:76).
- The Save doc comment sits above `ExportToCa` (L796).

MCP (`server/Services/McpServer.cs`):
- Schemas are flat `{type, description}` with no enums, items or bounds; `op` is a free-form object.
- `Str` / `IntOpt` / `Bool` silently return null on the wrong type; `IntOpt` truncates floats; unknown properties are ignored; `reason` is required in the schema but never checked.
- `MaxText` cuts replies at 120,000 characters mid-JSON (L48).
- `ui_screenshot` and `assets_texture` images have no size cap; `assets_map_scene` has no offset.
- ~~`weapons_create` is not atomic~~: wrong, it writes once. The real issue was duplicate entry names, now fixed.
- No auth token on `/mcp`: any local process can call it (only Origin and Content-Type are checked).

## Backlog

**Editor parity** (Map Builder)
- Place GEO props from the UI (placeInstance in the Place tab; today only clutter is placed from the UI).
- Full rotation gizmo (pitch/roll) and non-uniform scale.
- Pivot modes; live drag deltas.
- Surface, vertex and object snap; align and distribute.
- Paste at the cursor, for props as well as clutter.
- History panel; camera bookmarks; isolate / hide selected; measure tool.
- Top-down minimap or quad view.
- Clutter scatter brush.
- Replace a model across instances; instance rename and tags.
- User layers and selection sets.
- Autosave; remappable keys.

**Asset CRUD coverage**
- Delete textures, palettes and whole models.
- Add and remove AIMAPS points (needs the MPS mid-file structures decoded).
- Edit existing weapons (today they can only be created from a template).
- RDR and MPS editing; ZANIM effects.
- Sounds, collision and lights: research items, each needs a format doc in `docs/formats`.

**Pipeline**
- Play-test (launch in PCSX2).
- Per-asset in-game verification checklist.
- glTF import and export.
- CA and GTA write paths.

**AI**
- A "map brief → layout" workflow built on prefabs, validate and dry-run.
- An MCP auth token for non-loopback use.

## Completed log

- 2026-11-02: tracker created. Phase 1 done except the mesh/texture/import/animation tests (they need a decodable mesh fixture), the loose-folder pack export and the browser edit flow. Phase 2: strict arguments, dry run, history, validate, paging and annotations done.
- 2026-11-02 (continued): loose-folder pack export fixed; prefabs; the `EditOps` op table (oneOf schema and op checks); Problems list in the Map tab (tried in the app); `weapons_create` dry run and duplicate-name guard. 126 server tests and 38 web tests pass.
