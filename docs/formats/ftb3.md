# SOCOM: Fireteam Bravo 3 (PSP)

*SOCOM: U.S. Navy SEALs Fireteam Bravo 3* (UCUS-98716) does not share the Zipper formats of the other SOCOM games. Its files are `.ssg` containers, and its vertices and textures are laid out for the PSP's graphics engine (GE). Map Builder opens its levels read-only. Code: `Ftb3/SsgArchive.cs`, `Ftb3/Ftb3Package.cs`, `Services/Assets/Ftb3AssetService.cs`. Tests: `Ftb3Tests` (the retail check needs `MAPBUILDER_FTB3_DISC`, the UMD root, e.g. `K:\`).

## Disc layout

`PSP_GAME\USRDIR` holds `levels\` (26 level folders), `common\` (shared files: `characters\` with one `.ssg` set per character, the front end, effects), `nis\` (cut scenes), `audio\` and `video\`. A level folder is named after its level and kind: `_mis` campaign, `_multi` online, `_cust` the campaign map with online modes, plus `SovietEstate_bonus` and `SovietBase_mis_old`.

| File | Holds |
|---|---|
| `<level>.ssg` | The level's models (types `0x00`, `0x0B`, `0x0D`), most of a campaign level's textures (`0x01`), and `0x04` / `0x05` (not read) |
| `<level>_vram.ssg` | The rest of its textures (`0x01`) |
| `<level>_inst.ssg` | Its placed instances (`0x07`) and bounds (`0x11`) |
| `<level>_full.ssg` | Animations (`0x1D`, 726), UI screens, the tactical-point graph, navmesh and collision grid |
| `<level>_coll.ssg`, `_topo.ssg`, `_tac*.ssg` | Collision, AI graphs, the tactical map |
| `<level>_vltl*.ssg`, `vltl*_campaign.ssg` | Lighting data and textures (not read) |
| `goliathdata.ssg`, `mistext.ssg`, `scbin.ssg` | Engine settings (weather, fog, shadow), mission text, scripts |

## .ssg files

All values are little-endian.

| Offset | Field |
|---|---|
| +0x00 | Version (6) |
| +0x04 | 1 when the data is zlib-compressed (`78 DA`) |
| +0x08 | The entry table's size |
| +0x0C | The string table's size |
| +0x10 | The data's size, unpacked |
| +0x18 | Its size, packed |

The entry table follows at 0x20, then the string table, then the data. An entry is 16 bytes: `+0x00` its name (an offset into the string table), `+0x04` size, `+0x08` offset (in the unpacked data), `+0x0C` type, `+0x0D` flags, `+0x0E` a 16-bit hash. An object's resources share one name, which is often two names run together: the object's, then its shape's (`Grass_TallGrass_Tall`). 1,351 of the disc's 1,353 `.ssg` files open; one cut-scene file does not.

## Models

A model is a type-`0x0B` entry (0x70 bytes: `+0x04` its piece count, `+0x10`/`+0x20` its bounds, `+0x4C` its position scale, 256), preceded by its mesh pieces: the type-`0x00` entries just before it that share its name. A mesh piece:

| Offset | Field |
|---|---|
| +0x00 | Its texture's hash (a texture record's `+0x00`; 0 for none) |
| +0x10, +0x20 | Bounds |
| +0x40 | GE commands, among them VTYPE (command `0x12`) |
| +0x50 | The strip count (low half; water ripples set the high half) |
| +0x58, +0x5C | The UV scale and offset (16, −16: a u16 UV of `0x8000` is 0) |
| +0x68 | The vertex count |
| +0x70 | The strips |

A strip is a 0x20-byte header (`+0x00` a bounding sphere, `+0x18` its vertex bytes, `+0x1C` the primitive: GE type in the high half, always 4, a strip, and the vertex count in the low half), then its vertices. Three VTYPEs occur: `0x132` (u16 UV, RGB565 colour, s8 normal, s16 position; 16 bytes), `0xC532` (the same with four u16 weights; 24 bytes; skinned, read in bind pose) and `0x13A` (RGBA4444 colour; water). Positions are s16 × scale / 32768, so 1/128 of a unit. Strips wind counter-clockwise from their first triangle (99% of Valley's triangles agree with their normals). On the disc, 40,611 of 40,634 pieces read exactly; the rest are empty.

Models named `…_LOD` are low-detail copies and are not drawn. Names with `Skydome` or `parallax` are the sky and the distant scenery (the **Sky** layer).

## Textures

A type-`0x01` entry:

| Offset | Field |
|---|---|
| +0x00 | Its hash (what mesh pieces name) |
| +0x04 | Width (low half), height |
| +0x08 | Flags: 7 has alpha, 1 opaque |
| +0x0C | The CLUT's entry count (bits 16–27) |
| +0x10 | Bits per pixel (top byte: 4, 8 or 32) |
| +0x14 | CLUT and pixel bytes |
| +0x18 | Pixel bytes |
| +0x20 | The CLUT (32-bit RGBA; none at 32 bits), then the pixels, swizzled in the GE's 16-byte × 8-row blocks |

Alpha runs 0–255 and fades softly (a conifer's averages 55), so alpha textures are drawn blended with a low cut-out (0.1) rather than cut at half. On the disc, every texture a level's models name is found and decoded but one (in `SovietBase_mis`).

## Levels

A level draws:

1. **Instances** (`_inst.ssg`, type `0x07`): `+0x00` a 4×4 matrix (row vectors, translation in row 3, w 1), `+0x40` 1, `+0x48` −1, `+0x50` the model's entry name. Entry names put a number before a bar (`161|Grass_Tall`) to keep copies apart. An instance whose name is no model marks a place; none of Valley's do.
2. **World models**: the models no instance places (terrain tiles `[n]Valley_terrainmeshRoot1`, water, buildings built in place). Their vertices are already where the level has them.
3. **Sky**: the dome and parallax scenery.

Coverage (2026-11-06): all 26 levels draw, with 30,000–134,000 triangles each (Valley: 107 models, 493 instances, 55,800 triangles).

## Not done yet

- Points (spawns, objectives, control points), the mission text and the tactical map.
- Characters (`common\characters`), skinning and animations.
- Lighting (`vltl`), types `0x04`, `0x05` and `0x0D`.
- Editing, saving and conversion to SOCOM II.
