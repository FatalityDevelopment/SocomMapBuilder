# Command reference

Every page and every MCP tool talks to the server through one endpoint. Each command is `POST /api/command` with a JSON body `{ "Command": ..., ...fields }`; the reply is `{ Success, Data }` with `Data` as JSON text. AI assistants can send any of these through the `mapbuilder_command` [MCP tool](mcp-server.md).

## Commands

| Command | Fields | Does |
|---|---|---|
| `ASSET_OPEN` | `InputPath` | Opens (or reuses) a session for a package, folder or archive |
| `ASSET_LIST_MODELS` / `ASSET_LIST_TEXTURES` / `ASSET_LIST_PALETTES` | `AssetSession` | The package's assets |
| `ASSET_MODEL_MESH` / `ASSET_DESCRIBE_MODEL` / `ASSET_TEXTURE` | `AssetSession`, `AssetArchive`, `AssetName` | A model's mesh and description; a texture as PNG |
| `ASSET_MAP_SCENE` / `ASSET_MAP_POINTS` | `AssetSession` | Map instances and AIMAPS points |
| `ASSET_MAP_COLLISION` | `AssetSession`, JSON `{area?, model?, maxFaces?, faces?}` | Collision faces in world space, counts, and placed props without collision |
| `ASSET_EDIT` | `AssetSession`, `AssetJson: {op, …}` | One edit (below); `batch` runs several as one undo step |
| `ASSET_UNDO` / `ASSET_REDO` / `ASSET_SAVE` | `AssetSession` (+ `AssetJson` for save) | History and saving (`zdb` or `pack`) |
| `ASSET_SAVE_PREFAB` / `ASSET_LIST_PREFABS` / `ASSET_DELETE_PREFAB` | `AssetSession`, `AssetJson: {name, geoIndices?, clutterIndices?, origin?, replace?}` / `AssetName?` | Prefab files in `%LOCALAPPDATA%\MapBuilder\prefabs` (or `MAPBUILDER_PREFABS`) |
| `ASSET_HISTORY` / `ASSET_VALIDATE` | `AssetSession` (+ `AssetJson: {vramBudget?}` for validate) | The undo / redo steps; a map lint |
| `PACKAGE_EDIT` | `InputPath`, `OutputPath`, `Data: [ops]`, `DryRun?` | Add, replace or remove package files and archive records (refused while an open session has unsaved edits on the file) |
| `FS_PLACES` / `FS_LIST` | `InputPath` | Drives, common folders and folder listings |
| `VIEWER_LIST` / `VIEWER_PREVIEW` / `VIEWER_EXTRACT` | `InputPath`, `AssetJson: {inner?: [entry indices], sample?, rate?, effect?}` (+ `OutputPath` for extract) | Asset Viewer: what a folder, file or archive holds; a typed preview of one file or record; a record's bytes as stored |
| `VIEWER_EDIT` | `InputPath`, `AssetJson: {inner, op, …}` | One in-place edit through the package's asset session (see below) |
| `VIEWER_MOVIE` | `InputPath` (a `.PSS`) | Converts a movie to MP4 once (ffmpeg) and returns its `/api/viewer/movie/…` stream URL |

## Edit operations

`ASSET_EDIT` ops include `moveClutter`, `addClutter`, `deleteClutter`, `moveInstance`, `duplicateInstance`, `placeInstance`, `deleteInstance`, `movePoint`, `setPoint`, `setVertices`, `transformVertices`, `addMesh`, `deleteFaces`, `duplicateFaces`, `extrudeFaces`, `retexture`, `replaceTexture`, `tintVertexColors`, `bakeOcclusion`, `setSkinWeights`, `scaleBones`, `addCharacterMesh`, `removeCharacterSection`, `transformCharacter`, `paintTexture`, `applyCharacterPreset`, `newProp`, `fitBounds`, `mapTable`, `mapText`, the animation ops, and:

| Op | Fields | Does |
|---|---|---|
| `placeInstance` | `{model, position, yaw?, scale?, matrix?, name?, templateGeoIndex?}` | A new placed reference of a package model, copied from the placed reference nearest the position (or `templateGeoIndex`) and added beside it; returns `newGeoIndex`. `scale` (here and on `moveInstance`) is a number or `[x, y, z]` along the prop's own axes; clutter takes one number |
| `addMesh` UV options | `uvTile?, uvOffset?, uvScale?` | `uvTile`: repeat the texture every that many units, with UVs projected from each triangle's facing (x/z on floors). `uvScale`: multiply the mesh's own UVs. A vertex holds −8 … 8 repeats (int16 / 4096), so triangles are split and moved by whole repeats to stay inside that range |
| `setPoint` | `{name, newName?, team?, kind?: start \| waypoint \| other, position?}` | Changes an AIMAPS point in place |
| `addPoint` / `removePoint` | `{name, kind, team?, position}` / `{name}` | Adds an AIMAPS point at the cell holding the position, or removes one (never the last start or waypoint). See [AI maps](formats/aimaps.md) |
| `setCollision` | `{model, shape?: box \| mesh \| definition \| none, boxes?, bottom?, material?, like?}` | A model's collision faces, on its definition and every placed reference. See [Collision](formats/collision.md) |
| `clearCollision` | `{geoIndices? \| visuals? \| area? \| all, references?}` | Removes the world's own collision faces (after hiding world visuals) |
| `pruneAssets` | `{models?, textures?, keep?}` | Removes the map's own props that nothing places (with their GEO definitions), then the textures (with their mip levels) and palettes nothing draws with. A name appearing anywhere else in the package keeps its asset, except in the authoring tables `<map>_lib.rdr` and `lod.rdr`. The removed names are taken out of those tables too (`delisted`). Use `dryRun` to see the list first |
| `bakeLighting` | `{archive, model, scale?, chunks?, tint?}` | Bakes the map's lighting into a static model's vertex colours the way retail props are prelit. The lighting is `READERM.ZAR › <map>.rdr › world_params.GlobalLighting`: an ambient colour and up to three directional lights, each with `RGB` and `dir` (the way the light travels). A colour is ½ × (ambient + Σ RGB × max(0, −normal · dir)) × tint, using each chunk's own normals. MP2's crane re-bakes to its retail red channel within 1. Run `bakeOcclusion` after it |
| `clearActions` | `{dangling?}` | Removes the map's object actions (`actions.rdr` rows: doors, switches, bomb sites), all of them or only those whose node was deleted. `assets_validate` reports the latter as `dangling-action` |
| `addLadder` | `{position, top, yaw?, width?, name?}` | A ladder built like MP2's: a bare `ladderdown_N` node under worldmodel with two faces and nothing drawn. One face runs from the foot to the top, facing the climber (yaw is the way it points). The other runs from the top to 10 units above it, facing away |
| `clearMap` | `{props?, clutter?, world?, collision?, keepModels?, keepVisuals?}` | A blank map in one undo step: props and clutter deleted, world visuals hidden except the sky, clouds and sea, and world collision removed |
| `importModel` | `{sourceSession \| source, archive, name, targetArchive?, newName?}` | Copies a model and everything it needs from another package |
| `importTexture` | `{sourceSession \| source, name, sourceNamespace?, namespace?, newName?}` | Copies a texture with its palette |
| `addTexture` | `{name, pngBase64 \| imagePath, namespace?, width?, height?, maxSize?, paletteId?}` | A new texture from an image |
| `addPalette` | `{pngBase64 \| imagePath, namespace?, is16?}` | A new palette from an image |
| `setPalette` | `{archive, id, rgba: [1024 bytes] \| pngBase64 \| imagePath}` | Sets `texpal_{id}`'s 256 colours in place, keeping its depth (an image gives its first 256 pixels, row by row) |
| `placePrefab` | `{prefab, position, yaw?}` | Every item of a saved prefab around the position, turned by yaw, as one batch (all or nothing) |

Every op accepts `dryRun: true` through the MCP tool to preview the change without applying it.

## In-place viewer edits

`VIEWER_EDIT` ops are `rdrSet`, `rdrDuplicate`, `rdrRemove`, `mpsMap`, `text`, `bytes`, `image`, `iconSys`, `nparams`, `modelName`, `zanimField` and `zanimWord`. Undo, redo and save them with `ASSET_UNDO` / `ASSET_REDO` / `ASSET_SAVE` on the session the edit returns.

- `nparams {node, matrix?, min?, max?, kind?, flags?}` and `modelName {node, name}` change a GEO scene node.
- `zanimField {node, offset, field, fieldValue}` and `zanimWord {node, offset, word, wordValue}` change an effect script instruction (`node` is its `Seq_Data` record).

Palettes and clutter placements change through `ASSET_EDIT` (`setPalette`, `moveClutter` …).
