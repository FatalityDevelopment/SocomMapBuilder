# Map mission tables (`READERM.ZAR`)

A SOCOM II map package carries its mission data as [RDR tables](rdr.md) in `READERM.ZAR`. This page says what each table holds and what the editor does with it. Everything here was read from **M51** (`RUN\SP\M51\READERM.ZAR`, 17 tables); the per-map counts in [map-interactions.md](../map-interactions.md) cover all 34 retail map packages.

Code: `server/Services/AssetService.MapTables.cs` (decode: `ASSET_MAP_TABLES` / MCP `assets_map_tables`; edits: `mapTable`, `mapText`), `AssetService.MapActions.cs` (actions.rdr), `RDR/RdrDocument.cs` (`Field`, `Resolve`, `SetValues`). Tests: `MapTablesTests`, `MapActionsTests`. UI: the Map Builder's **Mission** and **Actions** tabs, and the action icons and objective flags in its 3D view.

"Observed" means the layout matches M51's tables; nothing written with these edits has been tried in game yet.

## How the tables are shaped

Every table is a tree of lists. A **named field** comes in two forms, and retail files mix them, sometimes in one table:

- **Pair form:** a list of two items, `[ "key", [values…] ]`. Example: every field of a `vehicles.rdr` actor.
- **Flat form:** the key string and its value list sit side by side in the object's own list, `[ "key1", [v…], "key2", [v…] … ]`. Example: every row of `hudobjectives.rdr`.

Most roots are a **wrapper**: a list whose only item is the real object or row list. A field present with an empty value list (`ISSECONDARY: []`, `one_shot: []`) is a flag: being there means yes.

**Shared values.** Retail tables point several lists at one block of values (every actor with accuracy 0.4 shares one `[0.4]`). Changing such a value in place would change it everywhere. `RdrDocument.SetValues` therefore gives each list on the path from the root its own copy of its items first (one level at a time, `OwnShallow`), then sets the values: only the field edited changes. The old block stays in the file, unreferenced.

**Paths.** The editor addresses a field by a path from the root: field names (both forms are found, wrappers looked through) and item numbers. `["HUD_OBJECTIVES", 0, "LOCATION"]` is the first objective's location; `[0, 5, "ai_params", "accuracy"]` is the sixth actor's accuracy (`0` is the wrapper). `ASSET_MAP_TABLES` answers each row's path.

## The tables

| Table | What it holds | Editor |
|---|---|---|
| `hudobjectives.rdr` | **HUD objectives**, list `HUD_OBJECTIVES` of flat-form rows (below) | Mission tab: text, location (editable), valves; flags in 3D |
| `vehicles.rdr` | **Actors**: everyone who starts on the map, players included (below) | Mission tab: list; AI values editable |
| `mission.rdr` | **Mission settings** (below) | Mission tab: AI parameters, satchel timer, weapons, start values (editable) |
| `actions.rdr` | **Object actions**: what props do when used. See [map-interactions.md](../map-interactions.md) | Actions tab; icons in 3D |
| `valves.rdr` | **Valves**: list `valves` of `{name, id, value, runtime}`. Named mission state (switches) that objectives, actions and scripts read and set. Ids can repeat (M51's POBJ4 and POBJ5 are both 3) | Valve suggestions and checks in the Actions tab |
| `<map>.rdr` (`m51.rdr`) | **World settings**, the authoring source of the map's compiled `.ZED` (below) | Mission tab › World, read-only |
| `map.rdr` | `Map: {MinExtent [x,y,z], MaxExtent [x,y,z], NumFloors, Floors []}`: the playable extents | Mission tab › World |
| `missioncams.rdr` | List `MISSIONCAMS` of `{IDENTIFIER, NAME, DESCRIPTION?, DESCRIPTION2?, ON_CALL_ANIMATION}`: the mission briefing / tutorial camera cues. NAME is a blank `" "` on M51 | Mission tab, read-only |
| `sndjukeboxm.rdr` | `JUKEBOX: {FIGHT_MUSIC, MED_FIGHT_MUSIC, HEAVY_FIGHT_MUSIC, STEALTH_MUSIC …}`: music sequences, each `[SEQ_NAME, [name], SNDNAME, [sound], PAUSETIME, [s] …]` alternating sounds and pauses | Asset Viewer |
| `aimaps.rdr` | `map_list`: the AI map layer files (`aimap.map`, `Church1.map` …). The compiled grids are in `AIMAPS.MPS` ([aimaps.md](aimaps.md)) | Asset Viewer |
| `clutter.rdr` | Clutter generation settings: `clutter_converts` (model names), `template` → `template_model` entries with `base_name`, weighted `choices` `[[weight, model] …]`, `scale_range`, `rotation_range {min, max}` (degrees), `repel.max_angle`, `aimap_soil` and `aimap_bbox`. Placed clutter itself is `CLUTTER.ZAR` ([clutter.md](clutter.md)) | Asset Viewer |
| `layers.rdr` | Authoring layers `{name, hidden, export, nodes []}` (M51: one empty layer "New") | Asset Viewer |
| `dyntex.rdr` | `boundaries: [0.24, 0.52]` (dynamic texture blend boundaries, meaning not confirmed) | Asset Viewer |
| `*_lib.rdr` (`m51_lib.rdr`, `benz_lib.rdr`, `ld51_lib.rdr`, `zm51_lib.rdr`) | Asset library metadata: `libname`, `locked`, `gearlib`, `renderphase`, `textures` (per texture: `name`, `dim2 [w, h, bpp]`, `typeU`/`typeV` wrap, `blur`, `mips`, `pal`, `detail {name, uv, range, bmode}`), `palettes`, `models` | Asset Viewer |

### HUD objectives (`hudobjectives.rdr`)

| Field | Meaning |
|---|---|
| `NAME` | Message key of the title, e.g. `_10035_MeetMallard_MSG` → "MEET MALLARD" |
| `DESCRIPTION`, `DESCRIPTION2`, `DESCRIPTION3` | Message keys of the briefing lines |
| `VALVE` | The valve that completes the objective |
| `DISPLAY_VALVE` | Optional: the objective shows once this valve is set |
| `INVALID_VALVE` | Optional: the objective fails when this valve is set |
| `LOCATION` | Two floats, **world x and z** (no height). Probably where the HUD map marks the objective (not checked in game). Observed: the values fall inside `map.rdr`'s extents, but they are not the position of the objective's AIMAPS nav point (M51's first objective is at 2029, 3329; its nav point Charlie at 5448, 2478), and which site each marks has not been checked in game. The trigger area is the mission script's, not this |
| `ON_CALL_ANIMATION` | Map animation (MZANIM.ZAR) played when the objective is called up |
| `LINE_IDS` | Integer list (route line on the HUD map, observed 1–5) |
| `NAV_POINTS` | AIMAPS point names for navigation (`Charlie`, `Delta` …) |
| `ISSECONDARY`, `ISSUB_OBJECTIVE` | Flags (empty lists) |

The message keys resolve through the map's language table: `M51LOC.ZAR` → `m51LOC.rdr`, records `{ID, MSG_ID, LOCALIZED}` (`LanguageTable.cs`). `mapText {key, text}` changes a message in every `*LOC.ZAR` table of the package that has it. Keys that live in shared tables (the mission title `_60883_SeedingChaos_MSG` is not in M51LOC) are not in the package and are shown as keys.

### Actors (`vehicles.rdr`)

A wrapper list of actor rows, each in pair form:

| Field | Meaning |
|---|---|
| `name` | Actor id (`player`, `able2`, `Road1` …); `mission.rdr`'s `UiVehicles` names the player's team by it |
| `display` | Name shown in game (`SPECTER`, `Sesseri Smuggler`) |
| `character` | Character type (`jungle_A51`, `m51_terror01`). Which model it draws as is not in the map package; `server/data/chartypes.tsv` (copied from the bots tooling) maps 430 types, e.g. `m51_terror01` → `MESH_alban_ter01`, `jungle_A51` → `MESH_seal_A_woodland_LO`. `assets_map_tables` answers the resolved `model`. Which **weapons** an actor carries come from its chartype in the game's shared chartype table (below), not from the map; `assets_map_tables` answers them as `weapons` and the held one as `weapon` |
| `setup` | List of `[point, heading]` pairs: the AIMAPS point(s) the actor starts at and the way it faces there. The **heading** is 0–7, eighths of a turn clockwise from north (−Z; +X is east, as on the tactical maps): 0 N, 2 E, 4 S, 6 W. Read from the retail maps: every campaign team faces into its map (M51's SEALs 4, south, toward the road; M82's 7 and M83's 6 face their enemies), and the character models face −Z unturned. Not checked in game. Several pairs give alternative starts |
| `team` | Integer list: team / group memberships (1–2 for SEALs, 30–31 sides, others squads) |
| `nofade`, `noshoot`, `nosnooze` | Flags with value 1 |
| `ai_params` | `{accuracy (0–1), courage (observed 8.5), surrender (0 or 1)}`, often shared between actors |

### Mission settings (`mission.rdr`)

A wrapper around one flat-form object:

| Field | Meaning |
|---|---|
| `LoadingScreenAssets`, `ExtraStringTables` | Loading-screen library (`sp/m51/ld51`), extra string table (`Alb`) |
| `description` | Message key of the mission title |
| `objectives {win, fail}` | Empty on M51 (the HUD objectives and scripts do the work) |
| `goal_completion_time {hours, minutes, seconds}` | -1 for none |
| `elevation {max, min}` | Height limits (M51: max -322, min 50; meaning of the order not confirmed) |
| `ai_params` | `weather_factor`, `recycle_time`, `recycle_range`, `respawn_time`, `respawn_rate`, `shadow_scale` |
| `ai_map_visibility` | One `[0/1]` per AI map layer of `aimaps.rdr` |
| `Valves` | `{NAME, VALUE}` start values. Most are **`Enable_<weapon>`** (67 on M51): 1 makes the weapon available in the mission's armory (M51 enables the MP5SD and M4A1 SD). Also `CurrentMission` |
| `UiVars`, `UiVehicles` | Briefing screen variables; the player's team by actor name |
| `SATCHEL_TIMER` | Seconds; -1 for the default |
| `IndoorReverb`, `OutdoorReverb` | `Depth` / `Seconds` pairs |

### World settings (`<map>.rdr`)

`fileinfo.version`, then `world_params`: `name`, `Flatten`, `DefaultMaterial` (footstep surface, `DIRT`), `FoliageSound`, `NightMission`, `LensFX_NVG_RGB` / `LensFX_StarlightScope_RGB` (night-vision tints), `WorldTextSpecs`, `MetersPerUnit` (0.1), `ShadowVector`, `GlobalLighting {ambient.RGB, light0..2 {RGB, dir, active}}`; `camera`: `fov`, `clip [near, far]`, `mid_clip`, `fog_standard {enabled, range [start, end], mid, RGB}`, `fog_altitude`, `fog_directional`, `start.pos`; `grid {cell_dim, atom_count, grid_dim, posts}`; `assetlibs`; `world_tree` (the world model's bbox and matrix).

The map's own `.ZED` (`M51.ZED`) holds compiled copies of several of these as binary records (`GlobalLighting` 112 bytes, `ShadowVector` 12, `MetersPerUnit` 4, `LensFX_NVG` 16, `camera` 144, `grid_params` 20). Which copy the game reads has not been tested, so the editor shows these values and does not change them; the Asset Viewer can.

## What the editor changes, and the limits

- `mapTable {table, path, values, revision}` sets one field's values: the same number of values, each keeping its type (an int field refuses 1.5; a float field takes 3). It refuses `actions.rdr` (use `mapAction`) and a stale `revision`. One undo step; the other tables and records in READERM.ZAR are byte-for-byte unchanged.
- `mapText {key, text, revision}` changes a message (printable ASCII).
- Not done: adding or removing objectives or actors (the Asset Viewer duplicates and removes list items), editing world settings, a check of the `setup` headings in game, `LINE_IDS` and `team` values, and any check in game.

## Chartype loadouts (`RUN\READERC.ZAR › character.rdr`)

Shared by every map (not in a map package). The root object holds `AngleUnits`, `ai_settings`, `body_items` (bone names, then `gear_nodes`), `gear` (hats, teeth, holster mounts: `name`, `model`, `ofs` {bone = [x y z], [rx ry rz] degrees}) and **`characters`**: a flat list of `name, {fields}` or `name, ":", parent, {fields}` (430-odd types). A type's fields override its parent's: `anim_set`, `strength`, `health` / `armor` per body part, `ai_params` (accuracy, courage, stealth, `sensors`), `sounds` (`CHRSND_*` → sound ids) and **`default_weapons`**: a list of `{wep_name, ammo_count?, mag_count?}` in the game's order, primary first (an empty list means none). `wep_name` is a `zweapon.rdr` `InternalName` (`9mm Sub` → model `uzi`, `M4A1 SD` → `m4Acarbine_sd`); a few names have no zweapon record on the retail disc (`SR-25`, `SA-80 A2`, `SR-1 Gyurza`, `M16A2`, `Dragunov`, `Satchel`, `RED SMOKE GRENADE`) and the server maps them to models by an alias list.

M51: SEALs (`jungle_A51` … `jungle_D51`, parents `jungle_*_woodland_LO`) carry M4A1 SD, Mark 23SD, Satchel and a Mark141 or M67; `m51_terror01/02` (parents `albanian_terrorist01/02`) a 9mm Sub and a Model 18; `m51_foreman` an 870; `m51_sniper` an M82A1A; `albanian_informant` nothing.

Code: `RDR/CharacterLoadouts.cs`; the server (`LoadoutsOf` in `AssetService.MapTables.cs`) takes the map's own `character.rdr` (READERM.ZAR, or a READERC.ZAR in the package) first, else READERC.ZAR beside the package, the folder above it, or the disc's RUN folder; ZWEAPON.ZAR the same way. A figure holds the first weapon with a model in the package that is not a grenade, mine, explosive or other gear.

## Animation sets (`RUN\READERC.ZAR › animset.rdr`) and where characters stand

A list of sets `{anim_set_name, include?, anim_set_anims}`; each anim is `{type_name, anim_name}` or `{type_name, modes: [["default", clip], {mode = [clip weight] …} …]}`, and a set takes anim types it lacks from its `include`s (`GLOBAL`, `SOLDIER`). A chartype's `anim_set` (character.rdr, inherited) picks its set, and the set's **Stand** clip is how it idles: `seal_stand` for both the Seal and the **Guard** set (guards play `guard_susp_search02` only when suspicious), `civ_stand` for No_weapon (hostages, informants). Other sets (POW_sitting01/02, Restrained, Torturer, Guard_bored02/03 = `guard_stand02/03`, Guard_smoking, Guard_warmhands, computer, Surrender, turret, launcher) are named by no map table on the disc (no `anim_set` in any vehicles.rdr), so the game's mission code must assign them; the editor cannot tell which actor uses one. Code: `RDR/AnimSets.cs`; ASSET_MAP_TABLES answers `animSet` and `stand` per actor.

Other things the retail maps show about starts:

- **The SEAL team starts together.** On most campaign maps one or two squad mates have a `setup` point far from `sealstart` (M51's JESTER at `Able2`, 4,900 units away; M53, M72, M73, M81, M82 and M83 the same). The Map Builder draws a squad mate (team 1, a SEAL) whose start is over 150 units from the player's beside the player, facing the player's way, and says so in the Mission tab. Not checked in game.
- **Multiplayer maps** (`MP*`): the `player` actor is the template for human players (it starts at `startPlayer`, next to the hostages on MP71 Fish Hook); human players spawn at the game's team spawn points, which are not in the AIMAPS point table (the spawn manager at `0x00669B30` in RAM, see the bots tooling's `socom_bots.py`). The Map Builder does not draw it.
- `disabled` (61 actors on the disc) and `respawn_setup` are not decoded; disabled actors are probably spawned later by the mission.

