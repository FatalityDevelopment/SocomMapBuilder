# SOCOM: Fireteam Bravo 3 (PSP)

*SOCOM: U.S. Navy SEALs Fireteam Bravo 3* (UCUS-98716) does not share the Zipper formats of the other SOCOM games. Its files are `.ssg` containers, and its vertices and textures are laid out for the PSP's graphics engine (GE). Map Builder draws its levels and characters and moves a level's props and game-mode points. Code: `Ftb3/SsgArchive.cs`, `Ftb3/Ftb3Package.cs`, `Ftb3/Ftb3Characters.cs`, `Services/Assets/Ftb3AssetService.cs` (and `.Edits.cs`). Tests: `Ftb3Tests` (the retail check needs `MAPBUILDER_FTB3_DISC`, the UMD root, e.g. `K:\`).

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

Written back, an unedited file is byte for byte the disc's, packed ones too: .NET's smallest zlib level gives the game's stream. An edited packed file is compressed again, so its size changes by a few bytes.

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

## Points

`_coll.ssg` holds the level's dynamic objects as type-`0x14` entries: `+0x00` the name (`char[16]`), `+0x40` a 4×4 matrix (row vectors, translation in row 3), then bounds and the collision shape. `_full.ssg` holds a copy of each. All 800 on the disc keep their matrix there. Most are doors, turrets and other breakable or moving objects; the game-mode places are listed as points:

| Name | Kind | Team |
|---|---|---|
| `AlphaSealControl` … `EchoMercControl`, `AlphaNeutral` … | spawn | Seal 1, Merc 2, Neutral 0 |
| `SealCheckPoint_1`, `MercCheckPoint_2` | checkpoint | by name |
| `SealExtractZone_1`, `MercExtractionZone_…` | extraction | by name |
| `DemoSealTarget`, `DemoMercTarget` (not their `_Destroyed` copies) | target | by name |

Valley has 25.

## Editing and saving

Edits rewrite 64 bytes in place:

- **Props** (instances): the instance's matrix in `_inst.ssg` (unpacked on every level). They move, turn and scale; the terrain and buildings built in place cannot move.
- **Points**: the object's matrix in `_coll.ssg` and its copy in `_full.ssg`.

**Save copy** writes each edited file into the chosen folder at its place under `levels` (`<folder>\Valley_multi\Valley_multi_inst.ssg`); a `.ISO` gets them patched in by those paths. An instance move changes 3–4 bytes of `_inst.ssg`. Nothing has been tried in game.

## Characters

`common\characters` holds one `.ssg` per part: `<name>.ssg` the body, `_H` the head, `_G` gloves, `_P` pouches, `_T` torso gear (`_U` is another uniform and is not drawn). The 50 characters with a body and a head are listed (`Sandman_woodland`, `KRA_Regulars1` …), under the archive `characters`; their textures are in their own part files.

All share one skeleton, `skeleton\skelly.skel`, which is text: `(skeleton skelly 20)`, each bone's parent (`-1 ; parent of 0 (hips) is -1`), then each bone's local rotation (a quaternion) and translation and its inverse bind matrix (4×4, row vectors), named in a comment (`; 0 (hips)`). The 20 names are CA's (hips, spinelo, spinehi, neck, head, the arms, legs and `weapon`).

Character meshes are skinned (VTYPE `0xC532`): each vertex starts with four u16 weights (`0x8000` = 1.0), and a strip header's `+0x10` holds the four bones they mean, one byte each (`0xFF` none). Positions are in the bind pose, in model space, at **8 times the model's scale** (`+0x4C` is 8; the vertices need 64). This holds on all 847 character models (their stored bounds need exactly that), and level models, which are not weighted, use their scale as it is. Characters stand in a T-pose (arms along Z), 1.95 m tall with their headgear; 96.9% of the vertices weighted fully to one bone lie at that bone or a neighbour.

## Not done yet

- The mission text and the tactical map; renaming points, changing their team.
- Animations (`_full.ssg` type `0x1D`, `common\characters\motions`) and the other uniform (`_U`).
- Lighting (`vltl`), types `0x04`, `0x05` and `0x0D`.
- Conversion to SOCOM II.
