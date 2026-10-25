# ZED / ZAR containers

`.ZED` and `.ZAR` files share one container: a string table, a record table and a body. A ZAR usually holds files (`READERM.ZAR` holds `.rdr` tables), while a ZED holds a tree of named blocks (models, scene nodes, textures). Code: `ZED/ZedContainer.cs`, `ZED/ZedTree.cs`. Round-trips byte for byte on retail files.

## Layout

| Offset | Size | Field |
|---|---|---|
| 0x04 | u32 | slot count = record count + 1 |
| 0x08 | u32 | string table size |
| 0x0C | u32 | string base pointer (added to name offsets) |
| 0x10 | u32 | header word, `0x10` |
| 0x54 | u32 | body size |
| 0x58, 0x5C | u32 | header words (kept) |
| 0x60 | u32 | header word, `0x00020002` by default |
| 0x64 | — | string table: NUL-terminated names |
| table end … +0x10 | 0x10 | last u32 = **root value** (the number of top-level records) |
| next | 0x10 × records | record table |
| file end − body size | body | payloads |

Each record (0x10 bytes):

| Offset | Size | Field |
|---|---|---|
| +0x00 | u32 | name pointer: string base pointer + offset of the name in the string table |
| +0x04 | u32 | payload offset, relative to the body start (0 when empty) |
| +0x08 | u32 | payload size |
| +0x0C | u32 | **child count** |

Payloads are aligned to 0x10 bytes; the gaps and the padding before the body are filled with `0xAF`.

## The record tree

Records are stored in pre-order and each record's child count says how many of the records after it are its direct children (each followed by its own subtree). So:

```
[0] textures        children 2
[1]   stone01.tif   children 1
[2]     texdat      (payload)
[3]   sky01.tif     children 1
[4]     texdat      (payload)
```

- A record's subtree ends where its last child's subtree ends (`ZedTree.SubtreeEnd`).
- The root value is the number of top-level records. It must be kept in step when adding or removing a top-level record (checked on every retail M51 archive: `M51_MDL.ZED` has 46, `M51_TXR.ZED` 2, the GEO/PAL archives 1).
- Inserting a child: insert the subtree after the parent's last descendant and add one to the parent's child count (`ZedTree.AppendChild`). Removing one: delete the subtree and subtract one from the parent's count, or from the root value at the top level (`ZedTree.Remove`).

## Top-level shapes by archive kind

| Archive | Top level |
|---|---|
| `*_MDL.ZED` | one record per model (see [Models](models.md)) |
| `*_GEO.ZED` | `models` (one child per definition) |
| `*_TXR.ZED` | `renderphase` (4 bytes), `textures` (one `<name>.tif` per texture) |
| `*_PAL.ZED` | `palettes` (one `texpal_<id>` per palette) |
| `CLUTTER.ZAR` | `Clutter_ZAR_Version`, `Clutter` |
| `READERM.ZAR` | one record per `.rdr` file |
| `MOTION_S.ZAR` | one record per clip |
