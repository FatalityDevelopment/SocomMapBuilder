# SOCOM 3 / Combined Assault locations, missions and points

*SOCOM: Combined Assault* keeps each location in one archive on the disc, and each archive holds several missions. A mission's named points (squad starts, AI spawns and nav points, map markers) are in its `mission.rdr`, on AI grids stored per island. Code: `Ca/CaMissions.cs`, `CaPackage.Missions` / `MissionPoints` / `Location`, `Ca/CaDisc.cs`, `Services/AssetService.Ca.cs`. Tests: `CaMissionsTests` (the retail check needs `MAPBUILDER_CA_DISC`, the disc root).

These layouts were observed on the retail disc (SCUS-97545). Every one of its 196 mission files was checked, and nothing written this way has been tried in game.

## Location archives

`RUN\<hash>.ZAR` is a ZDB type-2 archive of the location's ZDB v6 files (`RUN\<LOCATION>\<FILE>.ZDB`). The hash is the CRC32 of `RUN\<LOCATION>`, written as 8 uppercase hex digits: `RUN\POLAND_RURAL` → `055146D2.ZAR`, `RUN\COMPOUND` → `9EA6A33E.ZAR`. `GLOBAL.ZDB › missionlist.rdr` (tag `MSNl`) names every location folder (`RUN\WHARF\` …) and mission file, so the names can be recovered without opening the archives. The retail disc has 25: 24 maps and the front end (`RUN\UI`).

| Location | Archive | Location | Archive |
|---|---|---|---|
| BANGLADESH_BREAKNECK | CEA9385F | MOUNTAIN | 88DABCF2 |
| BANGLADESH_PIRATEBASE | CD06FF55 | POLAND_FLOOD | 3986FA1A |
| BANGLADESH_RIVERRECON | 5299C50C | POLAND_RURAL | 055146D2 |
| BROKEN_ISLANDS | E8C3CD05 | RESERVOIR | E6B0B2BB |
| CAMP | 1D38D48C | RESISTANCEVILLAGE | 797B4826 |
| CITY | F1F79488 | RIVERGORGE | 5C7369E3 |
| COMPOUND | 9EA6A33E | STEPPES | CD5D52F7 |
| DESTROYED_COMPOUND | F0FCD405 | STEPPES_CONVOY | C909A6E6 |
| FOREST | 57AB5CB7 | UI | D6D6E01B |
| FOREST_TRAINING | 474AA7A5 | WHARF | 7AEA19D9 |
| MOROCCO_DUNE | D009AF68 | | |
| MOROCCO_FORT | E7763CEA | | |
| MOROCCO_MINES | 74068550 | | |
| MOROCCO_OASIS | 733981F0 | | |
| MOROCCO_TANKBATTLE | 838DC6B0 | | |

The file browser shows each archive's location next to its name. Opening `G:\RUN\WHARF` or `G:\RUN\WHARF\SP422.ZDB` (paths that are not on the disc) opens the location's archive, and the second form also selects the mission.

## Missions

Each mission is a ZDB in the location folder, named `<kind><number><mode>`: `SP` campaign, `MP` online, `IA` instant action, `TR` training; the mode is the letters after the number (`CTR`, `DEM`, `EXT`, `SUP`, `BRE`, `SMA`, `ESC`, `CVY`, `DES`, `SUS`, `EXS`, or a name such as `HDQ`). It holds `mission.rdr` (tag `AIMm`), `vehicles.rdr` (`MSNv`) and the mission's own tables (`MISN`, `MSNd`). Each island of the location is its own ZDB with `<island>.src` resources: the ISLE world, `AIMi` (its AI grids) and `MSNi` (its name).

The session status lists `location`, `missions` (id, kind, mode) and the `mission` whose points `ASSET_MAP_POINTS` returns (`{"mission": "MP414CTR"}` switches it). The Map Builder has a Mission picker for this.

## `mission.rdr` (`AIMm`)

A serialized C++ object. Each vector is stored as three u32 byte offsets into the resource (begin, end, capacity), followed by a u32 byte size.

| Offset | Size | Field |
|---|---|---|
| 0x00 | u32 | root offset |
| 0x18 | vector | not decoded (12-byte records) |
| 0x28 | vector | zone bounds, 32 bytes each: pointer (stale), 0, min xyz, max xyz |
| 0x38 | vector | islands, 24 bytes each: CRC32 of `<island>.src`, first zone, zone count, three zeros |

The root holds the zone count (u32), a vector, and then one u32 per zone giving the offset of that zone's object. The zone object's +0x20 is a vector of 28-byte points:

| Offset | Size | Field |
|---|---|---|
| +0x00 | u32 | packed: zone index in bits 0–5, cell x in bits 6–18, cell z in bits 19–31 (SOCOM II's packing, plus the zone) |
| +0x04 | f32 | height (world y) |
| +0x08 | char[16] | name |
| +0x18 | u32 | flags: byte 1 is 1 on the points the map screen names (`JULIET`, `EXTRACTION`, `INTEL$~1` …); the top half is uninitialised padding |

A point's X / Z is the centre of its cell on its zone's grid: `min + (cell + 0.5) × cell size`. The zone's grid is section number (zone − island's first zone) of that island's `AIMi`. Its min equals the zone's bounds min on every retail zone, and its cells are 10 units. Of the disc's 7,217 points, all but one land inside their zone's bounds (`INDIA` in STEPPES_CONVOY SP434 sits just outside). Checked against the world meshes, 71 of 73 points on COMPOUND SP432 and 152 of 153 on POLAND_RURAL MP341CTR are within 8 units of a surface below or above them, with a median gap of 0.2. With either axis mirrored, most of them miss.

Point kinds as the Map Builder shows them (from the flags and the names' prefixes): `marker` (flag set), `spectator`, `start` (`s_*` spawns, `*_Start`, `able1` / `bravo1`), `waypoint` (`n_*` AI nav points), `other` (doors, action and objective places).

## `<island>.src` (`AIMi`)

| Offset | Size | Field |
|---|---|---|
| 0x00 | u32 | section count (one per zone of the island) |
| 0x04 | u32 | CRC32 of `<island>.src` (the same hash as in `mission.rdr`) |
| 0x08 | u32 | location hash |
| 0x0C | u32 × count | section offsets |

A section starts with SOCOM II's AI map header (see [AI maps](aimaps.md), from its +0x04): min xyz, max xyz, width, height (cells), cell x, cell z, then 1 / cell x and 1 / cell z, a word, and the name (char[32]) at +0x34. The cells after it are not in SOCOM II's layout and are not decoded, so heights come from the points themselves.

## Not done yet

- What the 0x18 vector and the rest of each zone object hold (AI paths and links, probably), and the AIMi cells.
- Teams: CA points carry none, since the MP team starts are not among these names. They may be in the mission's `MISN` / `MSNd` tables (next wave: mission tables).
- Editing: CA sessions are read-only. Moving a point would mean rewriting its packed cell and height in `mission.rdr` and writing the mission ZDB back into the location archive.
