# SOCOM 3 / Combined Assault locations, missions, points and tables

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
| 0x18 | vector | cross-zone links, 12 bytes each. The first two u32s are packed endpoint cells. The last word is unknown. |
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

Eight MP321 and MP325 mission variants each carry an unnamed 28-byte point slot in an otherwise valid vector. The named-point list skips that slot and preserves its bytes on save.

Each of the 13,996 records in the +0x18 vector connects cells in two different valid zones. Their median horizontal separation in world space is 2.19 units.

### Squad spawn slots

The zone's +0x64 vector holds 12-byte records with a packed grid cell, flags and a third word of unknown purpose. The top byte of the flags is a slot ordinal. In campaign and instant action, 189 of 192 slots match the exact cell of a single named point. COMPOUND SP432's four entries coincide in order with `s_Able1`, `s_Able2`, `s_Bravo1` and `s_Bravo2`. A move updates both copies of the packed cell when a point uniquely matches a slot in a standard four-member squad (indices 0–3). Other slot layouts preserve their placement and the edit reports the ambiguity. The slot has no identified height, and its heading can differ from the heading in the actor table.

Online missions have larger sets in this vector, typically in indexed groups of 36. Only 19 of their 34,172 slots coincide with a named point. On 18 retail demolition maps, the default arrangement (flags byte 2 is zero) has four groups of 36. Every slot in groups 0 and 1 is closer to the uniquely named `seal_tent`; every slot in groups 2 and 3 is closer to `terrorist_tent`. Bits 4–5 give the group, and the high byte gives the ordinal, 0–35. POLAND_RURAL MP341DEM and FOREST_TRAINING MP421DEM both pass with all 144 slots. WHARF MP422DEM fails the distance check for part of the set.

`ASSET_MAP_POINTS` returns these positions in a separate read-only `teamSpawns` array when the layout, both tents and every faction assignment pass those checks. Each row carries the grid cell, X/Z in world space and a display faction (SEAL = team 1, Terrorist = team 2). Its height is unknown. Alternate spawn arrangements and other modes still need an independent check.

### Named objective regions

Each zone's +0x50 vector holds 32-byte rectangles on its grid. The first u32 packs the minimum corner cell using the point format above. At +0x04, the first u16 is the width in cells and the second is the depth. Two further words precede the 16-byte name at +0x10. Across 5,786 retail regions, every packed zone index matches its containing zone. Allowing one boundary cell, 5,785 rectangles fit their zone's extents.

Names beginning with `pObj_` or `sObj_` identify 44 primary and 17 secondary areas in 13 missions. TR401's CHARLIE, DELTA, ECHO and FOXTROT points lie within their matching objective rectangles. COMPOUND SP432 has five primary and six secondary areas, including `pObj_infltrate1` (retail spelling) centred at world X 3.42, Z 37.17. The Mission tab places each flag at its region's centre. The names identify areas rather than the full sequence of scripted objectives, conditions or HUD text.

## `<island>.src` (`AIMi`)

| Offset | Size | Field |
|---|---|---|
| 0x00 | u32 | section count (one per zone of the island) |
| 0x04 | u32 | CRC32 of `<island>.src` (the same hash as in `mission.rdr`) |
| 0x08 | u32 | location hash |
| 0x0C | u32 × count | section offsets |

A section starts with SOCOM II's AI map header (see [AI maps](aimaps.md), from its +0x04): min xyz, max xyz, width, height (cells), cell x, cell z, then 1 / cell x and 1 / cell z, a word, and the name (char[32]) at +0x34. The cells after it are not in SOCOM II's layout and are not decoded, so heights come from the points themselves.

## Mission tables

CA's counterpart of SOCOM II's `READERM.ZAR` (see [map tables](map-tables.md)) is spread over the mission ZDB and `GLOBAL.ZDB`. These tables are compiled objects, not RDR text trees, and their names are CRC32 hashes, except for actor and point names, display names and message keys. The Mission tab reads these through `ASSET_MAP_TABLES`. Editable fields and their paths are listed under [Editing and saving](#editing-and-saving).

| Resource | Tag | Holds |
|---|---|---|
| `vehicles.rdr` | `MSNv` | Actors |
| `character.rdr` | `CHAR` | Character types: model, display name, weapons |
| `<mission>` | `MISN` | Mission header |
| `mission.rdr` | `VALV` | Valve start values |
| `mission.rdr` | `ACTN` | Actions (not decoded) |
| `story_def.rdr` | `MSNd` | Compiled mission script with AI population groups (`pop_*`); campaign, instant action and training missions only. Not decoded |
| `GLOBAL.ZDB › weapons/wep_weapon.rdr` | `WEAP` | Every weapon, grenade, attachment and gear item |
| `GLOBAL.ZDB › ai_vehicles.rdr`, `ai_turrets.rdr` | `GVEH`, `GTUR` | Vehicle and turret types (only checked for an actor's type hash) |

### Actors (`vehicles.rdr`)

+0x00 and +0x04 give the begin and end of the actor records, 0x1EC bytes each:

| Offset | Size | Field |
|---|---|---|
| +0x00 | char[32] | name (`player`, `able2`, `tech_suv1`) |
| +0x20 | char[32] | display message key (`_14867_JESTER_HUD_MSG`) |
| +0x40 | u32 | type: a character type in the mission's `character.rdr`, or a vehicle (`ai_vehicles.rdr`) or turret (`ai_turrets.rdr`) |
| +0x54 | u32 | Object kind. 1 identifies characters, 2 vehicles and 3 turrets. In COMPOUND SP432 the boat has 2 and the SEALs have 1. |
| +0x58 | u32 | start count (1 on 943 of the disc's 948 actors, else 2) |
| +0x5C | 32 × n | starts: point char[28] (a `mission.rdr` point), heading u32 (0–7, eighths of a turn clockwise from north, as SOCOM II's `setup`) |
| +0x16C | f32 × 12 | Probably AI values (−1, 0.6, 0.5, 1, 500, 180, 0.5 …); not shown |

Online missions leave the start's point name empty. A vehicle's or turret's slot then holds its own spawn place instead: x, y, z (f32, world units) at the slot's +0x10 and the heading at +0x1C as usual. On POLAND_RURAL MP341CTR (Farm) `tech_suv1` spawns at (−3335, 425, 85) facing south on the farm road, `soc_r1` at (−3870, 350, 3135). The Map Builder shows each as a read-only *vehicle spawn* point named after the actor, with the vehicle drawn on it. The player's empty slot also holds a place, but players spawn at the game's team spawns, so it is not shown.

A vehicle or turret type is CRC32 of its model library's name without `.rdr` (`veh_humvee_50cal`, `veh_tech_suv`, `veh_soc_r`, `veh_multi_turret_humvee`), so the type names the `RUN\VEH_*.ZDB` library it comes from. The body is that library's largest model; the others are its wheels, guns and low-detail copies.

On the disc all 420 actor records with a character-model match use kind 1, all 285 identified GLOBAL vehicles use 2, and all 104 identified GLOBAL turrets use 3. Both teams' vehicles use kind 2. The 139 records without a model match include patrols in kind 1 and `boat_tech1`, `boat_tech2` and `little_bird` in kind 2.

### Character types (`character.rdr`)

+0x08 and +0x0C give the begin and end of a list of record offsets. Records vary in size (most are 0x440):

| Offset | Size | Field |
|---|---|---|
| +0x000 | u32 | hash (what actors name) |
| +0x004 | u32 | model: root hash of a `clib` character model |
| +0x1F4 | u32 | lower-detail model; a float at +0x1F8 (170) is probably its distance |
| +0x1FC | 7 × 20 | vectors, each a stale pointer, then begin, end, capacity, size. The third (+0x224) is the **weapons** (weapon table hashes); the others are not decoded |
| +0x404 | char[32] | display name: the faction (`DEFENSE FORCE`, `MERCENARY`) or the squad mate (`BULLFROG`, `SNAKEBITE`); empty for the player's SEALs |

Types without a model are vehicles and turrets.

### Weapon table (`wep_weapon.rdr`)

Entries vary in length and are found by their hash. A gun or grenade has its model hash (a GLOBAL weapon model's root) at +0x0C, its display name at +0x44 (`ASSAULT RIFLE: M4A1`) and its description key at +0x64. Grenades have no display name. An attachment or gear item has its display name at +0x3C (`ATTACHMENT: 4X SCOPE`) and its key at +0x5C. Some have no display name and their key at +0x3C. All 88 guns and grenades match the 88 GLOBAL weapon models. In the Mission tab, a weapon without a display name is shown by its model's name when that name is known, else by the start of its description key followed by "…".

### Mission header (`MISN`)

Pairs of (count, offset): +0x10 the character types (u32 each), +0x20 the strings (hash, string offset), +0x30 the weapons and items the mission allows, +0x4C the valves. Campaign strings are the map screen image (`RUN\RAW\MAPS\m_432.RAW`), the title and briefing keys and the loading-screen textures. Online missions instead list their squads' display names and skins.

### Valves (`VALV mission.rdr`)

Each 8-byte record holds the valve hash and a word whose low 16 bits are the start value. The high half is 0x0301 on every retail record. The Mission tab resolves 37 of the 234 distinct retail valve hashes against known names and uses `v<hash>` for the rest.

### Text

The game's text is not on the disc as plain strings. "JESTER" appears only inside its key, `_14867_JESTER_HUD_MSG`. The Mission tab shows each key's own words as its text: `JESTER`, or `1Infiltrateh` for a cut-off sentence.

### In the Map Builder

COMPOUND SP432's tab lists SPECTER (`player`, holds the M4A1), JESTER and SIMPLE (HK5), KILLJOY (M4A1) and the SOCR boat. Its title, briefing, map image, the 21 weapons it allows and its 9 valves show too. The four SEALs are drawn in 3D at their starts with their CA models. When the shared animation bank is present, armed figures use the first frame of `AUX_GZAN.ZDB`'s `guard_stand`. The viewport places each weapon at the clip's `rifle` transform under the character's posed `rightHand`.

## Editing and saving

The editor writes fixed-size fields at their original offsets. Each operation creates one undo step for all the resources it changes. `ASSET_HISTORY` reports the affected files and tags. `ASSET_SAVE` with an explicit `outputPath` writes the whole archive to a separate file or folder. Automatic recovery copies use the source archive extension.

| Operation | Stored change |
|---|---|
| `movePoint` | Snaps X and Z to a cell on the point's own AIMi grid and writes its height. Moves a matching squad placement when the original cell identifies it uniquely in a verified four-slot squad. Moving between zones would require resizing two vectors. |
| `setPoint` | Writes a unique 2–15 character ASCII name and updates matching actor start names in this mission. Compiled action and script references may still use the old hash. |
| `mapTable` | `vehicles.rdr [row,"starts",index]` takes a point name and heading (0–7). In `mission.rdr`, `["Valves",index,"VALUE"]` takes a 16-bit start value and `["Weapons",slot]` takes a known unsigned weapon or item hash. Use the table's revision and mission id. |
| `moveInstance` | Uses the scene's string instance id, including its suffix for repeated hashes. Stores the requested world transform in parent-local space at node +0x10. |

On COMPOUND, 62 placements share one mesh with local centre (0, 8.5, 0), including two nodes at (322, −28, −1589) and (322, −28, −1314). A placement move changes the node matrix. The game still uses the island's baked spatial data, so check navigation and visibility around a moved building.

**In-game check.** Open COMPOUND SP432 and move `s_Able1` four cells along +X, from −371.58374 to −331.58374. Keep Y at −89.99609 and Z at −1977.8335, then use **Save copy**. This edit changes six of the archive's 12,507,136 bytes across the named point, squad placement and resource checksum. With your disc-image tools, put the output `9EA6A33E.ZAR` at `RUN\9EA6A33E.ZAR` on a copy of the disc. Start SP432 and compare the player's spawn with the original. Running the mission in game is pending.

## Not done yet

- The final cross-zone link word, other vectors in the zone object and the AIMi cells.
- Spawn groups and team ownership outside the verified default demolition sets.
- The full scripted objective list and its conditions. The compiled `MSNd` contains 41 of the 61 hashes of named objective regions. The script's structure remains under investigation.
- Resizing mission tables, transferring points between zones, and inserting or deleting world nodes.
