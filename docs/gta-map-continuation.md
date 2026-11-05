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
  - `GtaPackage.PlacementFiles.cs` writes edited INST lines and binary records. Deleting a text row also updates stream and text rows whose LOD indices shift.
  - `SaveMapEdits`: writes text IPLs at their disc paths, and `MODELS/GTA3.IMG` plus its twin `GTA3_1.IMG` for stream IPLs.
- `server/Services/Assets/GtaAssetService.Map.cs` answers `ASSET_STATUS` (`missions`, the areas, with `label`), `ASSET_MAP_POINTS` (`mission` picks an area) and `ASSET_MAP_SCENE`, and builds the water mesh (`WATER.DAT:@water/<area>`).
- `GtaAssetService.MapEdits.cs` handles `moveInstance`, `placeInstance`, `deleteInstance` and `batch`. The instance id is `zone:fileStem:row`.
- `GtaAssetService.Edits.cs` has `Step`, which holds either a DFF edit or placement `Moves`. It also handles undo/redo, history, and saving (mode `zdb`/`map` with a folder).
- Web:
  - `web/src/MapPage.tsx`: the `gta` flag. Select, place, duplicate or delete props in a zone.
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
MAPBUILDER_GTA_DISC="F:/GTA SA.iso" MAPBUILDER_DISC_CACHE="J:/swbf-test/cache" dotnet test tests/MapBuilder.Tests --filter "FullyQualifiedName~GtaMapTests|FullyQualifiedName~GtaPlacementEditsTests"
```

```bash
cd web && npx tsc --noEmit && npx vitest run
```

In the app:
1. Run the Debug server on port 8771 (`dotnet run --project server --no-launch-profile --urls http://127.0.0.1:8771`) and open it in a browser.
2. Use the MCP tools at `http://127.0.0.1:8771/mcp`: `ui_show {page:"map", path:"F:\\GTA SA.iso"}`, then `ui_focus {session, instance:"lae2:lae2_stream5:6"}` (CJ's house), `ui_view`, `ui_screenshot`.

An LA zone can contain about 800 distinct models. The time to load it depends on texture decoding, the disc cache and the renderer.

## Follow-up

- Additions and deletions use stable session IDs. On save the writer rebuilds the text or binary INST rows, updates LOD indices and adjusts offsets to later binary sections. Undo restores the original rows.
- A move or turn carries its dedicated LOD through the same transform. The editor warns about shared LODs. Pass `followLod: false` on a move to edit just the detailed placement.
- The **Time** picker filters timed placements by hour. The water preview uses `waterclear256` from the disc when available and animates ripples from the flow and wave values in `WATER.DAT`.
- The web clients request up to 16 GTA meshes per batch. On the US PS2 disc the whole-map overview requests 4,299 distinct meshes, including water, in 269 batches on a cold load. A model failure identifies its source, and successful meshes stay cached.

## In-game check on the US PS2 disc

In a copy of SLUS-20946, raise `lae:lae_stream3:145` (`compmedhos4_lae`, model 3557) by 10 m next to the opening BMX alley. Reopen the saved ISO and inspect `lae_stream3.ipl` in both `GTA3.IMG` and `GTA3_1.IMG`. Each copy contained the new Z of 36.4609375 instead of 26.4609375. Every other row in that stream matched retail.

PCSX2 booted the patched ISO. The game streamed model 3557 at the edited height. A call to the game's downward collision query at world XY (2249.21094, −1277.94531) hit that same house at Z 38.6501. The original roof geometry predicts Z 28.6587 at that point. Moving this placement carried its collision with it, with no edit to its COL file. The archive comparison covers both copies. The gameplay observation covers one boot.
