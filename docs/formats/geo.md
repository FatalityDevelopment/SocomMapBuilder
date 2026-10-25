# GEO scenes (`*_GEO.ZED`)

A GEO archive holds the definitions of its namespace's models, and for a map the scene itself. Its single top-level record is `models`; each child is a definition named like the MDL record it places (a character's skeleton drops the `MESH_` prefix). Code: `Geo/GeoScene.cs`, `Geo/SceneInstances.cs`.

## Node records

A definition is a tree of scene nodes. A **scene node** is any record (not one of the container names below) with an `nparams` child.

| Child | Holds |
|---|---|
| `nparams` | 96 bytes: the node's transform and bounds (below) |
| `visuals` | `vis` entries, one per visual the node draws |
| `children` | child scene nodes |
| `model_name` | ASCII reference to another model (a prop built from other models). In `text {name}` the braced part wins: it cleans to `name` |

Container names that are never scene nodes: `models`, `children`, `visuals`, `vis`, `nparams`, `vparams`, `model_name`, `di`, `params`, `points`, `matrix_id`.

### nparams (96 bytes, 24 words)

| Words | Field |
|---|---|
| 0–15 | local transform, 4×4 f32 column-major (translation in 12–14). World = parent world × local, accumulated over the scene nodes above |
| 16–21 | bounds: min x, y, z, max x, y, z (f32, units) |
| 22 | kind (int): 7 on a model's root node, 1 on other nodes |
| 23 | flags (int): `0x80800051`, with `0x40000` added on nodes that own visuals; `-0x800000` on leaf nodes with visuals |

Words 22 and 23 are integers, not floats. Zeros there leave the model unbound in game, and it draws nothing. Confirmed with the CA prop ports.

### vis / vparams / matrix_id

Each `vis` entry under `visuals` has a `vparams` record. Its first u32 is the visual's flags: `0x20000` means positions are `int16 / 32768 × scale` (weapons), otherwise `int16 / 16 × scale`. Skinned characters' `vis` entries carry `matrix_id` (u32), the matrix their vertex batches name.

## The map scene

A map's scene is the GEO namespace that has a `worldmodel` node (normally the map's own, `m51`). `worldmodel` draws `WORL_MDL.ZED` and places the map's props: every scene node below it that resolves to a model is an instance at its world transform. The `models` list also holds prototypes (trees, crates, windows) defined at the origin, which are drawn only where something references them. Clutter is placed separately (see [Clutter](clutter.md)).

Scene names resolve to MDL records loosely (`GeoNames.Variants`): lowercase, an alphanumeric-only form, with `_LO` and a trailing `_<digits>` stripped, and with a `MESH_` prefix added, for the name as stored and its braced part. One alias is built in: M51 places `iw80a2` instances that resolve to the `m16a2` mesh.
