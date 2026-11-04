# RDR tables (`.rdr`)

Typed data tables: vehicles, missions, actions, HUD objectives, weapon definitions (`zweapon.rdr`), screens. A map's tables live in its `READERM.ZAR` (`aimaps.rdr`, `vehicles.rdr`, `m51_lib.rdr` …); shared ones in other ZARs. Code: `RDR/RdrFile.cs`, `RDR/WeaponDefs.cs`. Character animation clips are a different `.rdr` layout (version 5): see [Animations](animations.md).

## Layout

| Offset | Size | Field |
|---|---|---|
| 0x00 | u32 | version (1) |
| 0x04 | u32 | string pool size |
| 0x08 | u32 | object table offset |
| 0x0C | pool size | string pool: NUL-terminated strings |
| object table offset | 8 × n | atoms |

Each atom is `{ u32 tag, u32 value }`. The tag's low byte is the type:

| Type | Value |
|---|---|
| `0x01` | int |
| `0x02` | float |
| `0x03` | string: offset into the string pool |
| `0x04` | container: byte offset into the object table where its items start; the item count is `tag >> 16` |

The root atom starts at the object table offset and is the document's root container. Every named field is a container of exactly two items: a string (the field name) and its value, which may itself be a container (a list or a sub-object). Walking that gives a tree of named values.

Named fields come in two forms (a pair item `[key, [values]]`, or key and values side by side in the object's list), roots are usually a one-item wrapper list, and lists often share value blocks. [Map mission tables](map-tables.md) describes this and what each table of a map means.

## Editing

`RdrDocument` (`RDR/RdrDocument.cs`) edits in place: values set from text (`SetFromText`), list items duplicated and removed, strings added to the pool, and `SetValues(path, texts)` for a named field found by a path of field names and item numbers, unsharing every list on the way so a shared value changes only there. The Asset Viewer edits any table this way (`VIEWER_EDIT rdrSet / rdrDuplicate / rdrRemove`) and also adds, replaces and removes whole `.rdr` files inside their ZAR; the Map Builder's Mission tab uses `assets_edit mapTable`.
