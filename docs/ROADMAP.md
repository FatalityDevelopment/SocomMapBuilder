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

- **Now:** 1a, making the server testable.
- **Next:** 1b, characterisation tests for the edit ops.

## Status checklist

### Phase 0: tracker
- [x] This file, linked from the README Contents.

### Phase 1: lock down what exists

**1a. Testability**
- [ ] `InternalsVisibleTo` for `MapBuilder.Tests` in `server/MapBuilder.Server.csproj`; the test project references the server.
- [ ] `tests/MapBuilder.Tests/Fixtures/SyntheticPackage.cs`: an in-memory package with a GEO (worldmodel and a scene node), CLUTTER.ZAR, AIMAPS.MPS, an MDL mesh and a TXR/PAL pair.

**1b. Characterisation tests** (pin today's behaviour)
- [ ] `EditOpsTests.cs`: each `ASSET_EDIT` op, then undo back to the original bytes, then redo.
- [ ] `BatchTests.cs`: a batch is one undo step; a failing op rolls the batch back.
- [ ] `SessionTests.cs`: join an existing session, reopen after a disk change, save zdb and reopen, pack mode.
- [ ] `ImportTests.cs`: importModel and importTexture between sessions, with undo.
- [ ] `PackageEditTests.cs`: PACKAGE_EDIT add, replace, remove and merge.
- [ ] `McpServerTests.cs`: `tools/list` schemas, an unknown tool, an `assets_edit` round trip.

**1c. Hardening** (each fix lands with a test)
- [ ] Vector length and finite checks (movePoint `position`, setVertices, transformVertices matrix/pivot/translate/rotate/scale); reject scale ≤ 0.
- [ ] `FaceMap` face keys: `TryParse` with a clear error.
- [ ] Error mapping: IndexOutOfRange, FileNotFound and IO errors become command/tool errors, not HTTP 500s (`WithSession`, `McpServer.Call`).
- [ ] Dirty tracking: cleared on a full save; leaves back to their original bytes after undo are dropped.
- [ ] replaceTexture, retexture and tintVertexColors honour `label`.
- [ ] Session races: atomic open, close under the session lock, import reads the source session under its lock.
- [ ] PACKAGE_EDIT refuses a file whose open session has unsaved edits.
- [ ] addClutter requires `position`.
- [ ] `MapPoints.Move` range-checks its 13-bit cell fields.
- [ ] Move the misplaced Save doc comment in AssetService.cs.

**1d. Web**
- [ ] Vitest for the `useMapEdits` index bookkeeping and the clutter copy/paste payload.
- [ ] Browser smoke: edit → undo → save-request flow.

### Phase 2: MCP authoring
- [ ] Typed schemas: `enum`, `items`, min/max, `additionalProperties:false`; `assets_edit.op` as `oneOf` from one op-spec table that also drives validation.
- [ ] Strict argument parsing: wrong types and unknown properties are errors; `reason` is enforced.
- [ ] `dryRun` for `assets_edit`, `package_edit` and `weapons_create`; `weapons_create` writes atomically.
- [ ] Safe responses: valid JSON truncation with `nextOffset`; `offset` on `assets_map_scene`; capped image sizes.
- [ ] Tool annotations: `destructiveHint`, `idempotentHint`; `assets_open` is not read-only.
- [ ] `assets_validate` / `ASSET_VALIDATE` map lint (dangling refs, long texture names, out-of-bounds instances, duplicate point names, VRAM estimate), also shown in the Map tab.
- [ ] Prefabs: `placePrefab` op, `ASSET_SAVE_PREFAB`, `assets_list_prefabs`; one placement is one undo step.
- [ ] `assets_history`: undo/redo labels and revision.

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
- `weapons_create` runs three commands and is not atomic.
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

- 2026-11-02: tracker created.
