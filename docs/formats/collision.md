# Collision (`di` faces in `*_GEO.ZED`)

On SOCOM II, what players and bullets hit is a list of convex faces on the GEO scene nodes. No other file holds collision: `WORL_MDL.ZED` and the MDL archives only draw. Code: `Geo/GeoCollision.cs` (faces), `Package/CollisionEdits.cs` (edits), `Services/AssetService.Collision.cs` (`ASSET_MAP_COLLISION`). Tests: `CollisionTests`.

## Where faces live

A scene node's collision is its `di` child (the region), whose children are `di` faces:

```
barrel_single            (a placed reference under worldmodel › children)
  nparams                (its transform)
  model_name             barrel_single
  di                     the region: 7 faces
    di                   a face
      params             12 bytes
      points             16 bytes a corner
    di …
```

- **Props.** A model's definition in the `models` list has the region, and **every placed reference carries its own copy** in the same local space. MP2's 21 barrels and its containers repeat their definition's faces byte for byte. The game reads the copy on the reference, so a reference without a region is not solid, whatever its definition has.
- **The world.** Floors, walls and invisible blockers are faces on the world's own scene nodes under `worldmodel`, both those that draw a world-model visual (`deckv_5` draws a deck tile) and collision-only nodes (`out_di1_*`, `di_invis`, `camcol`). Hiding a world visual leaves its node's faces in place.
- Counts: MP2's scene has 2,756 faces and M51's 13,362. Across the 34 map scenes there are 303,598.

## Faces

`points` is 16 bytes a corner: x, y, z (f32, in the node's space) and a zero word. A face is a convex polygon of 3 to 6 corners on retail maps. MP2's barrel has a hexagon top and six quads. Corners run counter-clockwise seen from outside, so `(p1 − p0) × (p2 − p0)` points outward. Retail boxes (containers, the barrel's prism) have no bottom face.

`params` (12 bytes):

| Offset | Size | Field |
|---|---|---|
| +0 | u32 | Usually 0. `0x22` on some of MP2's deck faces, `0x400` on some of M51's. Meaning unknown |
| +4 | u32 | Surface material, probably an index into `materials.rdr › SOILS`. On MP2: 3 the deck, 4 metal walkways, 7 the containers, 61 the barrels |
| +8 | u8 | Corner count × 4 + 2, plus 1 on faces that look up (below) |
| +9, +10 | u8 | Flags, meaning unknown. `0x64 0x00` on MP2's deck, `0x64 0x40` on its containers, `0x98 0x00` on the barrels |
| +11 | u8 | `0x44` on MP2, `0x04` on some of M51's faces |

Byte 8's low bit is authored, not computed. On MP2's scene it is set on exactly the faces whose normal points up (floors and up-slopes). On M51 it is set on most of them but not all, and on the weapon library's faces it is rarely set. It probably marks walkable faces, though that has not been checked in game. Faces written here set it on faces whose normal has a positive y. The disc test re-encodes every map-scene face. Every byte matches retail except this bit, which differs on a minority of faces.

## Editing

| Op | What it does |
|---|---|
| `setCollision {model, shape: box \| mesh \| definition \| none, boxes?, bottom?, material?, like?}` | Writes the faces to the model's definition and to every placed reference of it. `box` is the drawn geometry's bounds, without a bottom. `boxes` gives several boxes in model space. `mesh` makes one triangle face per drawn triangle. `definition` puts the definition's own faces back on every reference. `like` copies another model's surface profile (every params byte but the count) |
| `clearCollision {geoIndices? \| visuals? \| area? \| all, references?}` | Removes the regions of the world's own nodes, picked by record, by the world-model visual they draw (`N004`), by an area (a face centred inside) or all of them. `references` also clears placed props in the area |
| `placeInstance` | The new reference gets a copy of the placed model's definition region. It used to copy the template reference's, so a crate placed from a barrel was a solid barrel |
| `newProp` | Copies the template's definition with its region. After reshaping, set the new prop's collision |

`clearMap {props?, clutter?, world?, collision?, keepModels?, keepVisuals?}` starts a blank map in one undo step. It deletes every placed prop and clutter placement, hides every world-model visual except the sky, clouds and sea, and runs `clearCollision all`. The map keeps its settings, tables, AI grid and points.

The Map Builder's View › **Collision** draws the faces over the map: floors green, walls orange. `ASSET_MAP_COLLISION` / `assets_map_collision` lists world and prop face counts, the placed props without a region, and the faces in world space. `assets_validate` reports each bare prop as `no-collision` (info).

## Not known yet

- What the +0 word, the flag bytes and byte 11 do. Copy them from a retail model with `like`.
- Whether the game needs faces to stay within the node's `nparams` bounds, and how many faces a node may hold. A `mesh` collider of a detailed model is many faces. Boxes are what retail uses for props.
- None of this has been tried in game yet.
