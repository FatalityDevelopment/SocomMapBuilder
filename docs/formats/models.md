# Models (`*_MDL.ZED`)

A model is a top-level record of a `*_MDL.ZED`. Its payload holds the geometry and the DMA chains that draw it; its children point into the payload. It is drawn where its [GEO definition](geo.md) places its visuals. Code: `Model/ZedModelFile.cs`, `Geo/VisualPlacement.cs`, `Package/ZipperModelMesh.cs`. The chunk layout is confirmed in game (the SOCOM CA prop and character ports render).

Skinned characters (`MESH_*`) use a different payload: see [Characters](characters.md).

## The record

```
room_table01            payload 2992 bytes, children 6
  N000_I000_V00         4 bytes: offset of a DMA chain in the payload
  N000_I000_V01
  N000_I001_V00
  …
```

Each child with a 4-byte payload is a **visual stub**: a u32 offset of a DMA chain inside the parent's payload. Its name is `N<node>_…_V<visual>` (`N000_000` in older files): `<node>` numbers the nodes of the model's GEO subtree that own a `visuals` group, in pre-order (the root first, then `children` recursively), and `<visual>` is the `vis` entry inside that group. A visual's chunks are in that GEO node's space and take the position unit of its `vparams` (below). `VisualPlacement` tries the count over visual-owning nodes and over all nodes and keeps the one that places more stubs.

## DMA chains

A chain at offset `o`:

- `o + 0x00` u32: tag count in the low 16 bits.
- `o + 0x10 + 0x10 × i`: tag *i*. Word 0: bits 0–15 = length in quadwords, bits 16–23 = **kind**, bit 24 a flag (seen on material tags). Word 1: address (an offset in the payload).

| Kind | Points at |
|---|---|
| 6 | A material section's texture name: a NUL-terminated `.tif` name in a 16- or 32-byte field. Starts a section |
| 3 | The section's GS material registers (ALPHA_1 decides alpha blending: `0x44` blends, 0 copies and relies on the alpha test) |
| 4 | Per-vertex RGBA for the next chunk, 4 bytes per vertex, `0x80` = 1.0: the baked lighting |
| 2 | A geometry chunk (below) |

A chain is rejected when it has an unknown kind, an address past the payload, or a kind-2 tag that does not point at a chunk header. A model with no valid chain is not chain-drawn (the tracer effects, far bush LODs); the editors cannot edit those as meshes. The same geometry may appear twice in one visual (alternative chains): decode it once per visual.

## Geometry chunk (one VU1 batch)

| Offset | Size | Field |
|---|---|---|
| +0x00 | u32 | `0x01000101` (STCYCL 0x101) |
| +0x04 | u32 | `0x6C048000` (UNPACK V4-32 @0, 4 qwords) |
| +0x08 | | GIF tags, then (index address, 0, vertex count, triangle count), (0, 0, 0, scale) |
| +0x30 | i32 | vertex count (at most 4096) |
| +0x34 | i32 | triangle count |
| +0x38 | 3 × f32 | chunk origin (world chunks; 0 on weapons) |
| +0x44 | f32 | scale |
| +0x48 | u32 | `03 02 00 01` (STCYCL 0x203: two of every three qwords; the third is the colour) |
| +0x4C | u32 | UNPACK V4-16 @4 |
| +0x50 | 0x10 × verts | vertex records |
| after the vertices + 0x08 | 4 × tris | triangle table |

Vertex record (16 bytes): `int16 x, y, z, nx` then `int16 u, v, ny, nz`. UVs are 12-bit fixed point (4096 = 1.0); normals are Q15.

Triangle record (4 bytes): `a × 3, b × 3, c × 3, 3`, packed VU slot offsets, so the vertex index is the byte / 3.

**Positions** are `int16 × scale / divisor + origin`. The divisor comes from the visual's GEO `vparams` flags: **32768** when flag `0x20000` is set (weapons), **16** otherwise (props, vehicles, map objects). `int16 / 16` only reaches ±2048 units, so world chunks far from the origin carry their offset in the chunk origin.

The bytes after a chunk's triangle table (up to the next chunk) hold material data, including `.tif` names; files without valid chains are bound to textures from there.

## Renaming a texture

Pointing sections at another texture rewrites the kind-6 name fields (`PackageEdits.RetextureModel`). The new name must fit the field: 16 bytes in retail models, so at most 15 characters plus the NUL.

## Locators

Locators (a weapon's firepoint and muzzle points, attachment points) are scene nodes of the model's GEO definition that draw nothing: no `visuals`, no `children` and no `di` record. Their transform is accumulated from the definition's root. They are not in the MDL payload.
