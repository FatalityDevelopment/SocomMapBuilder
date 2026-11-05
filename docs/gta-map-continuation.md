# GTA: San Andreas map: state and how to continue

This is a handover for the San Andreas map work in the Map Builder (November 2026). It covers what exists, where the code is, how to check it, and what is left.

## What works

| Feature | PR | Where |
|---|---|---|
| Every IPL placement read and resolved to its DFF (48,584, 100%). Areas are IPL zones, picked in the header's **Area** list. The disc opens on Grove Street (LAE2). | #40 | `GtaPackage.Map.cs`, `GtaAssetService.Map.cs` |
| Water from `DATA/WATER.DAT`. Timed objects that are off at noon are hidden. Low-detail overviews per city, the countryside and the whole map. | #41 | same files |
| Moving and turning placements (gizmo, fields, batches) with undo and redo. Saving writes the edited IPLs to a folder or a patched `.ISO`. | #42 | `GtaPackage.MapEdits.cs`, `GtaAssetService.MapEdits.cs` |

## Code map

- `server/CodeDesigner.Ps2.FileFormats/Gta/GtaPackage.Map.cs` reads the placements:
  - **Text IPLs** (`DATA/MAPS/**.IPL`, `inst` rows): id, name, interior, position, quaternion, LOD row.
  - **Binary stream IPLs** (`<zone>_stream<n>.ipl` inside the IMGs): `bnry` header, record count at +4, record offset at +28, then 40-byte records: position (3 floats), quaternion x y z w (4 floats), model id at +28, interior at +32, LOD at +36. The LOD indexes the rows of the zone's text IPL.
  - Also here: `Transform` (game to editor matrix), `MapAreas`, `AreaAt`, `LodOf`, water polygons, clipping, timed-object hours.
- `GtaPackage.MapEdits.cs`:
  - `MovePlacement`, `GameTransform` (the inverse of `Transform`).
  - `PlacementFileBytes`: rewrites only the moved INST lines, or patches the moved 40-byte records.
  - `SaveMapEdits`: writes text IPLs at their disc paths, and `MODELS/GTA3.IMG` plus its twin `GTA3_1.IMG` for stream IPLs.
- `server/Services/Assets/GtaAssetService.Map.cs` answers `ASSET_STATUS` (`missions`, the areas, with `label`), `ASSET_MAP_POINTS` (`mission` picks an area) and `ASSET_MAP_SCENE`, and builds the water mesh (`WATER.DAT:@water/<area>`).
- `GtaAssetService.MapEdits.cs` handles `moveInstance` and `batch`. The instance id is `zone:fileStem:row`.
- `GtaAssetService.Edits.cs` has `Step`, which holds either a DFF edit or placement `Moves`. It also handles undo/redo, history, and saving (mode `zdb`/`map` with a folder).
- Web:
  - `web/src/MapPage.tsx`: the `gta` flag. You can move but not place; the action panel is off.
  - `web/src/useMapSession.ts`: `placementsOnly` includes GTA.
  - `web/src/shared/host.ts`: GTA saves pick a folder.
  - `web/src/mapModel.ts`: `missionLabel` uses `label`.

## Facts learned the hard way

- **SA stores the conjugate (inverse) of each placement's rotation.** Roads and houses only line up with the quaternion conjugated. The editor matrix is `C · [R|t] · C⁻¹`, with `C = (x, y, z) → (x, z, −y)`, the same turn `BuildMesh` puts on every model.
- **`GTA3_1.IMG` is a byte-identical second copy of `GTA3.IMG`.** The PS2 game may stream from either. A save patches both. Note that the Model Editor's IMG save to `.ISO` still writes only `GTA3.IMG`.
- LOD models are not always named `lod*` (`laelodds03`, for example). Use `Placement.IsLod`: another row's LOD index points at it.
- Interior 0 and 13 are outdoors. Other values are interiors, which get their own area per zone.
- The text IPLs on the ISO are upper case (`DATA/MAPS/LA/LAE2.IPL`). Stream IPLs in the IMG are lower case.
- Retail tests that open the disc session must be in `[Collection(MapPackageCollection.Name)]`. Sessions are shared per path, and parallel tests close each other's sessions.
- C: is nearly full. Set `MAPBUILDER_DISC_CACHE=J:\swbf-test\cache` so the retail save test writes its ~1 GB IMG copies to J:.

## How to check

```bash
MAPBUILDER_GTA_DISC="F:/GTA SA.iso" MAPBUILDER_DISC_CACHE="J:/swbf-test/cache" dotnet test tests/MapBuilder.Tests --filter "FullyQualifiedName~GtaMapTests"
```

```bash
cd web && npx tsc --noEmit && npx vitest run
```

In the app:
1. Run the Debug server on port 8771 (`dotnet run --project server --no-launch-profile --urls http://127.0.0.1:8771`) and open it in a browser.
2. Use the MCP tools at `http://127.0.0.1:8771/mcp`: `ui_show {page:"map", path:"F:\\GTA SA.iso"}`, then `ui_focus {session, instance:"lae2:lae2_stream5:6"}` (CJ's house), `ui_view`, `ui_screenshot`.

An LA zone loads about 800 models in roughly a minute.

## What is left

1. **Test in the game.** A saved `.ISO` with moved placements has not been booted. Check that a moved stream IPL record shows up in the game, from both `GTA3.IMG` copies.
2. **LOD follow-up.** Moving a building leaves its LOD where it was (the edit warns about this). Option: move the LOD row by the same delta when it serves only that building.
3. **Adding and deleting placements.** Text IPLs can grow, and the ISO patcher moves larger files. Stream IPLs would need their record count and entry size changed (`GtaImg.WriteEntry` relocates entries that grow). The IDE id comes from the model.
4. **Collision.** SA's COL files (`*.col` in the IMGs) are not moved with a placement. The game uses the model's own collision at the placement, so this probably needs nothing; confirm in the game.
5. **Time of day.** The scene fixes noon (`SceneHour`). A picker could pass the hour through `ASSET_MAP_SCENE`.
6. **Water.** It is a flat colour. It could use the game's water texture and the per-vertex wave values.
7. **Load speed.** Models load one `ASSET_MODEL_MESH` call at a time from the web (6 concurrent requests, served in order). A batched mesh command would cut the whole-map overview's 4,300 calls.
