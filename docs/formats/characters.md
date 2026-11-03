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

## The draw groups, field by field

Measured on M51's `MESH_seal_A_woodland_LO` (7 sections, 74 groups):

- **Vertex block header** `(flags, triangles, matrix_id, count)`. Flags: `2` on a group's first batch, `1` on the middle ones, `5` on its last, `6` on a group's only batch. Rigid vertices have weight 32767; a blended vertex appears in several batches with partial weights.
- **Draw block**: `STCYCL 0x102`, `UNPACK V4-8` (num = triangles, imm `0xC000 | (3N + 4)` where N is the group's vertex count; each triangle `(a*3, b*3, c*3, 3)`), `STCYCL 0x101`, `UNPACK V4-32` (4 GIF qwords, imm `0x8000`), `STCYCL 0x103`, `UNPACK V2-16` (num = N, imm `0x8005`; 12-bit UVs), `MSCNT`, zero padding to a qword.
- **GIF qwords**: the second one's NLOOP (low 15 bits) is N; the third holds `(3N + 4, 0, N, triangles)`. The first and fourth are the same in every group.
- **Sizes**: retail groups stay at or under 51 vertices and 58 triangles.

## Editing

`assets_edit addCharacterMesh` (`Package/CharacterEdits.cs`) adds rigid geometry (shapes, OBJ, arrays) that follows one bone. It goes in as a new material section appended after the mesh's blocks, so no existing offset moves. The chain is rewritten after the new blocks with the new tags at its end, and `_START` / `ref_count` are updated. Groups hold at most 48 vertices and 56 triangles, with flags `6` and weight 1.0. Positions are given in the character's bind pose; each must lie within 7.99 units of its bone (int16 at 1/4096). The section's texture must already be in the character's TXR namespace (`addTexture` with `namespace: clib`). Decoded by the editors and posed by the Animation Editor; **not yet tried in game**. The character's lower-detail mesh (`MESH_x_1`, or `MESH_x_1_LO` for `MESH_x_LO`) shares the skeleton's bone names and gets the same geometry unless `lowDetail` is false.

`scaleBones` reshapes body parts. It scales the vertex parts that follow each named bone in that bone's own frame, about its joint. `thickness` scales across the bone's length axis, `length` along it, and `scale` both. The length axis points to the bone's children (the average of their offsets); for an end bone it runs along the bone from its parent. A vertex shared by several bones moves by each bone's weight share, so the change fades across joints. Thickening SEAL A's right bicep by 1.3 stretches no triangle edge beyond 1.30×. On limbs and the spine use `thickness`: `scale` and `length` also stretch the part past the next joint (1.47× at the elbow). `scale` suits end bones (head, hands).

`transformCharacter` moves, rotates or scales a character's vertices in the bind pose (model space): all of them, a section, a texture, or the vertices whose heaviest bone is in a list. A vertex at p moves by d = M·p − p. Each of its parts gets d added in its own bone's frame, so the weighted blend (weights summing to 1) moves by exactly d, and the skinning is kept. Normals follow M's inverse transpose. Sizes don't change, so the payload is edited in place. A part may not end more than 7.99 units from its bone. The lower-detail mesh gets the same matrix when it has matching vertices. Its selection has a hard edge: a vertex moves fully or not at all, by its heaviest bone. So scaling part of the body steps at the border (1.47× edge stretch for the same bicep). That is what bent the neck and made arms wavy in the first trial; reshape body parts with `scaleBones` instead.

`removeCharacterSection` takes material sections out of the chain, by texture or by number. Their blocks stay in the payload, drawn by nothing, so no offset moves. It removes gear added earlier, or a part of the body. `retexture` points a character's sections at another texture, as it does for static models.

Character gear (`FLIB_MDL.ZED`: hats, goggles, pouches) are rigid models like weapons. `addMesh` and the texture ops change them in place. Nothing in a map package says which character wears which gear.

## Packs (getting a character into the game)

`characters_pack` (`Package/CharacterPack.cs`) writes a session's character as `<PACK>.CDB`, the layout of the CA ports confirmed in game (`Mods/SOCOMCA/ca2s2.py`, on MP61 in 2026-10). Everything is under `RUN\MP\<PACK>\`:

- `CLIB_MDL.ZED`: the mesh as `MESH_<donor>`, its payload as edited.
- `CLIB_GEO.ZED`: its skeleton as `<donor>`.
- `CLIB_TXR.ZED`: the textures its sections draw with, renumbered 1..N, mip levels off.
- `CLIB_PAL.ZED`: their palettes, renumbered into the pack's own block of 32 ids (`1024 + 32 * (crc32(pack) % 480)`), with each texture's TEX0.CBP set to match. Palette ids are global across everything loaded and the first registered wins, so packs must not share them.

Archive headers and the render phase come from the session's own CLIB library. Give the pack the name of a character the current map does not load. For example, `con_pow01` is spawned by the `mp_pow1` chartype and is loaded only on MP10 / MP11. The game's own chartype then spawns the packed character once the pack is loaded (loading is the bots tooling's job, not the pack's). Only the main mesh ships, not the lower-detail one, as in the CA packs.

## Notes

- `_1` variants (`MESH_alban_informant_1`) are separate, much smaller meshes (likely lower-detail copies) with their own skeleton definitions.
- A character only appears in a mission when the mission's character records name it. The mesh and skeleton alone are enough for the editors.
