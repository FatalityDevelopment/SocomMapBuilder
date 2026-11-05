# SOCOM: Fireteam Bravo 2 (PSP)

*SOCOM: U.S. Navy SEALs Fireteam Bravo 2* (UCUS-98645) uses the SOCOM 3 / Combined Assault package and library formats, with the PS2's VIF and GS records replaced by the PSP graphics engine (GE). Map Builder opens it read-only through the Combined Assault code path. Code: `ZDB/ZdbArchive.cs` (version 7), `Ca/PspGe.cs`, `Ca/CaLibrary.cs` and `Ca/CaPackage.cs` (`IsPsp`, `OpenPsp`, `PspWorldObjects`). Tests: `FtbTests` (the retail checks need `MAPBUILDER_FTB2_DISC`, the UMD root, e.g. `K:\`).

The other two Fireteam Bravo games have not been checked yet. The layouts below were observed on this disc only.

## Disc layout

`PSP_GAME\USRDIR\RUN\` holds `GLOBAL.ZDB` (weapons: `global_assets.rdr`), shared ZDBs, and one folder per map (`FORT_MP`, `VILLAGE_SP2` …):

| File | Holds |
|---|---|
| `ISLAND_?.ZDB` | The island: an `ISLE` library (world, props, textures), `AIMi`, `ZANM` |
| `<MAP>.ZDB` | The world's `UNIV`, `ZANM`, `BANK`, `ZRDR`, and `DEP_` links to `customch.ZDB` and `keystone.ZDB` |
| `VCOMMON_<MODE>.ZDB` | The characters (`clib`), one copy per game mode |
| `DEM.ZDB`, `SUP.ZDB` … (`SP.ZDB`, `DYNAMIC.ZDB` in campaign maps) | A mode's or mission's `MISN`, `CHAR`, `AIMm`, `ACTN`, `MSNv` |

Opening a map folder, or any ZDB in one, loads every version-7 ZDB in the folder and `RUN\GLOBAL.ZDB`. Only the first `clib` of each size is kept, because the per-mode copies differ only in a header word.

## ZDB version 7

Type 1 (`0xC8`, `0x58`) as in [packages](packages.md), with one more word at `0x0C` (1 on every retail file). This shifts the header size, body size, ID, compression and error counts down by 4 bytes, to `0x10`–`0x23`. The header area is aligned to 0x40. `DEP_` descriptors have a size word of `0xFFFFFFFF`; they own no payload and the word is written back as read. All 393 retail ZDBs open and write back byte for byte.

## Libraries

The header and the model, object, texture and palette vectors are CA's. Nodes are CA's, but a node's mesh objects are at `+0x9C` (CA: `+0x98`) and the bounds at `+0xB4`/`+0xC0` (CA: `+0xB0`/`+0xBC`).

### Mesh objects

Type `0x00945EA4` (static) or `0x0090C640` (skinned: characters, weapons). `+0x04` is the bounding box (8 corners) and `+0x08` a hash. `+0x70` holds two batch counts, low half then high half; their sum is the number of batches. Batch *i* is 0x140 bytes at `+0x80 + 0x140·i`:

| Offset | Field |
|---|---|
| +0x00 | The material's GE commands (texture state; the game patches them) |
| +0x74 | The texture's name hash (named `ca_<hash>`, as CA's) |
| +0x80 | The batch's bounding box (8 corners) |
| +0xE0 | Vertices, from the batch start |
| +0xE8 | Display list, from the batch start |
| +0xEC | Primitive count |
| +0xF4 | Vertex count (high half), stride (bits 8–15) |
| +0xF8 | Vertex bytes |

A display list is `VTYPE`, `BASE`, `VADDR 0`, one or more `PRIM` (triangle strips on this disc), then `RET`. Primitives draw consecutive vertices from the batch's vertex block. Strips wind counter-clockwise (99.5% of Village's triangles agree with their stored normals). A material whose back-face culling is off (GE `0x1D 0`, in its first 0x74 bytes; trees and some cards) is drawn from both sides. The world and props use VTYPE `0x1BE`: u16 UV (/32768), RGBA8888 colour (0xFF = 1.0), s8 normal and float position, 24 bytes in all. Skinned batches add weights (stride 28) and a bone palette. These are read in bind pose; weights are not used yet. All 683 display lists on Fort belong to a batch.

### Textures

| Offset | Field |
|---|---|
| +0x08 | Name hash |
| +0x0C | Pixels, from the record (0x30) |
| +0x10 | GE format: 2 RGBA4444, 3 RGBA8888, 4 CLUT4, 5 CLUT8, 8 DXT1 (not decoded) |
| +0x14 | Width (low half), height |
| +0x18 | Buffer width in pixels (low half) |
| +0x1C | CLUT, from the record |
| +0x20 | How it draws: 0 opaque, 1 cut-out (alpha tested), 3 blended |
| +0x24 | Layout: low byte the CLUT's size in 32-byte units (so 16- or 32-bit entries), second byte bits per pixel / 4, top byte bit `0x04` RGBA5551 colours (1-bit alpha), else RGB565 |

Pixels are swizzled in blocks of 16 bytes × 8 rows. DXT1 is not swizzled; each 8-byte block stores its 2-bit indices before its two RGB565 colours (the PC order is the reverse). RGBA8888 textures with all-black colour are alpha masks. Foliage, fences and tree-line billboards are RGBA5551 CLUTs drawn as cut-outs; read as RGB565 they were solid green cards.

## Worlds

A map draws three sets of objects:

1. **Node objects.** Few: 15 on Fort.
2. **Instances.** 0x60-byte records: a 4×4 matrix (translation in row 3), `+0x40` the object, `+0x44` a colour, `+0x48` −1, `+0x4C` flags, `+0x54` the object's `+0x08` hash. These are trees, rocks and props.
3. **World-space objects.** Most of the terrain and buildings are objects that nothing above places. Their vertices are already in world space; the world's 784 terrain-tile nodes (type `0x0090A13C`, 100 × 100 units) find them by area. They are drawn as they are.

Sky domes (an object whose bounds enclose the others on x and z), cloud layers and sun cards (above 1,000 units) go in the **Sky** layer.

Coverage on the disc (2026-11-05): 27 maps draw, with 14,000–88,000 triangles each. At most one texture name per map is not found. `BORDER_TOWN_MP` and `UI_MOVIE` have no world model yet.

## Missions

Each game mode of an online map (`DEM.ZDB`, `SUP.ZDB` … and `DEFAULT.ZDB`) and each campaign file (`SP.ZDB`, `DYNAMIC.ZDB`) is a mission, named after its file. A folder ending `_SP` or `_SP<n>` holds campaign missions; the others are online. All 164 `AIMm` (`mission.rdr`) and `AIMi` resources on the disc read with CA's layouts ([Combined Assault missions](ca-missions.md)), so named points (1,971 in all) are placed on the island's AI grid as in CA.

`vehicles.rdr` (`MSNv`) actors are 0x1BC bytes, not CA's 0x1EC: name `char[32]` (no display key follows), another hash at `+0x20`, the character type at `+0x24` (a `character.rdr` record's hash), group `+0x30`, start count `+0x34`, starts from `+0x38` (32 bytes: point `char[28]`, heading). The player's start leaves its name empty and is followed by a position, which is not read.

`character.rdr` (`CHAR`): `+0x24`/`+0x28` a list of record offsets. A record is 0x4D4 bytes: its hash, the `clib` model's root hash at `+0x04`, and from `+0x2C0` CA-style vectors of 20 bytes (a stale pointer, begin, end, capacity, size). The one beginning at `+0x300` is the loadout: up to 8 weapon table hashes. No display name is stored, so the Mission tab names actors by their actor name.

`GLOBAL.ZDB › weapons/wep_weapon.rdr` (`WEAP`): a weapon's hash is the CRC32 of its upper-case name (`M4A1` → `D3E46CCE`). An entry starts with that hash and, at `+0x04`, the offset of its strings; the first, at `+0x0C`, is its UI message key (`_30660_ASSAULTRIFLE_UI_MSG`). The model's root hash is one of the entry's next words. Grenades and attachments have models too, so the key's category (ASSAULTRIFLE, SNIPERRIFLE, SMG, SHOTGUN, MACHINEGUN, PISTOL, LAUNCHER) decides what counts as a gun. Display names come from leftover source text in the table (`_LOCT("SNIPER RIFLE: SR-25" _30696_…)`) for the few keys it covers, else from the model's name when it is known (`M4A1`, `MP5`); otherwise the Mission tab shows the key's words. On Village SP2, 26 of 29 loadout hashes have an entry, each with a model.

The mission header (`MISN`) is not read: it holds the world ZDB's path, (online) a list of character types and the loading-screen tips, but no title, briefing or map image. Team spawns are not read.

## Not done yet

- Objectives and team spawns; names for the weapon models CA's word list does not crack.
- Skinning, animations (`BONE`, `ZANM`) and the terrain tiles' own records.
- Editing, saving and conversion to and from SOCOM II and Combined Assault.
- Fireteam Bravo and Fireteam Bravo 3.
