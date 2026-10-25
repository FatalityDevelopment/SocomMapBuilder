# Characters (skinned meshes)

Characters are `MESH_<model>` records in `CLIB_MDL.ZED` (and `FLIB_MDL.ZED`), skinned to a skeleton defined in `CLIB_GEO.ZED` as `<model>`. Code: `Package/CharacterMesh.cs`, `Package/ZipperModelMesh.cs`. The format is the one the SOCOM CA character ports write (`Mods/SOCOMCA/charmesh.py`, `s2mesh.py` in CodeDesigner), confirmed in game.

## The record

```
MESH_alban_informant              payload (blocks + DMA chain)
  MESH_alban_informant_START      u32: offset of the DMA chain
  ref_count                       u32
  mtx_count                       u32
```

A record is skinned when it has a `<name>_START` child.

## The DMA chain

At the `_START` offset: an `end` tag whose low 16 bits give the number of tags after it, then 16-byte tags (word 0 = qwc in bits 0–15 and the tag id in bits 28–30, word 1 = address, words 2–3 = VIF codes):

1. Per material section a `cnt` tag (id 1) whose address is the section's `.tif` name field (up to 32 bytes).
2. Per vertex batch, a `ref` whose VIF codes are `0x01000101` / `0x6C048000` (UNPACK V4-32 @0), with address = the bone's `matrix_id`. Then a `ref` to the **vertex block**: STCYCL, UNPACK V4-16 header (flags, triangle count, bone, vertex count). Then per vertex `(x, y, z, slot × 2)` and `(nx, ny, nz, weight Q15)`, in bone-local space at **1/4096 units**.
3. Per group a `ref` to the **draw block**: UNPACK V4-8 triangles (slot × 3), UNPACK V4-32 GIF tags, and UNPACK V2-16 UVs (12-bit) per slot.

A vertex is the weighted sum of its parts. Each part is a bone-local position transformed by that bone's bind world matrix. Slots shared between batches are the same vertex.

## The skeleton

`CLIB_GEO.ZED › models › <model>` is a scene tree. Every scene node under it is a bone, and the model node is bone 0:

- **Bind pose.** A bone's `nparams` transform is its local bind pose. World = parent world × local.
- **Matrices.** The `matrix_id` in a bone's `vis` entry is the number the vertex batches use to name it.

Animations drive bones by name (see [Animations](animations.md)).

## Notes

- `_1` variants (`MESH_alban_informant_1`) are separate, much smaller meshes (likely lower-detail copies) with their own skeleton definitions.
- A character only appears in a mission when the mission's character records name it. The mesh and skeleton alone are enough for the editors.
