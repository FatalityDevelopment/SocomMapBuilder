# Disc survey: where the map data lives

A read-only pass over the SOCOM II disc (SCUS-97275, mounted as `E:`) on 2026-11-04: every folder and package, which files belong to each map, what each holds, and how much of it Map Builder reads. The [format pages](README.md) give the layouts; this page is the map of the disc and the list of what is still unknown.

"Read" means Map Builder decodes the file (and says where). "Partial" means some of it is decoded. "Unknown" means only its name or outline is known.

## The disc

| Folder | What it holds | Map data? |
|---|---|---|
| `SYSTEM.CNF`, `SCUS_972.75`, `SCUSNGUI.ELF`, `*.CNF`, `ENDER`, `CNF/`, `NETGUI/`, `OVERLAY/` | Boot files, the game and network-setup executables, DNAS | No |
| `RUN/*.ZDB` (34 files) | **One package per map**: 12 single-player (`M51`–`M83`), 22 multiplayer (`MP1`–`MP83`) | Yes, nearly all of it |
| `RUN/COMMON/` | `FONT`, `SPLA`, `SPMP` textures (splash screens) loose; every other COMMON archive is copied into each map's ZDB | Shared art |
| `RUN/UI/` | Front-end archives (`UI_MDL/GEO/TXR`, `UI2D`, `UIMP`, `UIOP` …), loose copies of the per-map screens (`BF`, `EM`, `EQ`, `LP`, `MM`, `OM`), `READERC.ZAR` (110 menu screens incl. `dlg_Map51.rdr`…), `READERX.ZAR`, `DBUFFER.ZAR` | Map screens |
| `RUN/READERC.ZAR` | 55 shared `.rdr` tables: **`character.rdr`** (character types, weapons, gear), `hud.rdr` (incl. the **TacMap** colours), `missionlist.rdr`, `global_valves.rdr`, `ai_turrets.rdr`, `materials.rdr`, `decals.rdr`, `dynamics.rdr`, `animset.rdr`, `motion.rdr`, `orders.rdr`, every `*LOC.rdr` | Yes (shared) |
| `RUN/ZWEAPON.ZAR` | `zweapon.rdr`: every weapon and ammo type (125 records, 86 with a model) | Weapons |
| `RUN/MOTION_P.ZAR`, `*ZANIM.ZAR` (`EXTZANIM`, `MPZANIM`, `UIZANIM`) | Character motions, effect / UI animation scripts | Animation |
| `RUN/SOUNDRDR.ZAR`, `RUN/SOUNDS/` | `sounds.rdr` (1.9 MB), `BNKSTORE.ZAR` (67 MB), `VAGSTORE.ZAR` (577 MB) | Sound |
| `RUN/LOCALE/STATES/` | Shared language tables (`ALBLOC`, `HUDCLOC`, `UIMNLOC` …) | Text |
| `RUN/MOVIES/` (69 `.PSS`) | Cut-scenes; per-mission folders `M51/` … (`M51IG01.PSS`, `M51_EXT.PSS`, `M51_INS.PSS`) | No |
| `RUN/RAW/`, `RUN/ICONS/`, `RUN/IRX/`, `RUN/RECO/` | Loading images, memory-card icons, IOP modules, voice recognition | No |

Each map package is self-contained: the game loads one ZDB per map, and every COMMON / UI / LOCALE archive the map needs is copied into it.

## What one map package holds

Paths below are M51's (`RUN\SP\M51\…`); multiplayer maps use `RUN\MP\MP51\…`. `<n>` is the map number (51), `<map>` the namespace (`m51`, `mp51`).

| File | Maps | Holds | Map Builder |
|---|---:|---|---|
| `<MAP>_GEO.ZED` | 34 | The scene: model definitions, placed references, visuals, `di` collision faces, ladders | Read ([geo.md](geo.md)) |
| `<MAP>_MDL.ZED`, `WORL_MDL.ZED` | 33 / 34 | Prop models; the world (terrain) model. MP53 has no `_MDL` | Read ([models.md](models.md)) |
| `<MAP>_TXR.ZED`, `<MAP>_PAL.ZED` | 34 | Map textures and palettes | Read ([textures.md](textures.md)) |
| `<MAP>.ZED` | 34 | Compiled world settings (below) | Partial |
| `CLUTTER.ZAR` | 34 | Placed clutter | Read ([clutter.md](clutter.md)) |
| `AIMAPS.MPS` | 34 | AI grids, points, and more (below) | Partial ([aimaps.md](aimaps.md)) |
| `READERM.ZAR` | 34 | The map's `.rdr` tables (below) | Partial ([map-tables.md](map-tables.md)) |
| `MZANIM.ZAR` | 34 | Map animation scripts (doors, objectives) | Read ([animations.md](animations.md)) |
| `MOTION_S.ZAR` | 34 | The map's extra character motions (guard idles, conversations) | Read ([animations.md](animations.md)) |
| `DBUFFER.ZAR` | 34 | Body size of each map archive (below) | Read (new: `BufferTable`) |
| `CLIB_*.ZED` | 34 | Characters (`MESH_*`) | Read ([characters.md](characters.md)) |
| `FLIB_*.ZED` | 34 | **Worn gear**: eyes, hats, goggles, holster, satchel, the `default_gear` names of `character.rdr` | Read as models |
| `JUNK_TXR.ZED` | 34 | Empty placeholder texture archive (528 bytes) | Read |
| `<MAP>LOC.ZAR` (`RUN\LOCALE\STATES\`) | 19 | The map's text; 15 MP maps have none | Read (`LanguageTable`) |
| `LD<n>_TXR/PAL` (SP), `UI\LP<n>` (MP), `UI\OM0<n>` (old MP) | 34 | Loading-screen picture (`ld_m51_prog.tif`) | Textures only |
| Extra namespaces | 1–5 each | Map-only prop / vehicle sets: `BENZ` (M51 car), `BCAR`, `CM<n>`, `VM<n>`, `TM<n>`, `SN<n>`, `VG<n>`, `BM<n>`, `VP<n>`, `SP<n>`, `ZM<n>` (props on SP maps, sky on MP72), `SK<n>` / `RM<n>` (skies), `AM<n>`, `DM<n>`, `C<n>` | Read like the map's own namespace |
| `RUN\COMMON\` copies | 34 | `WEAP_*` (weapon models), `EFFE_*` (effects), `HUD/HUD2/HUDW/ALPH/FONT/LOAD` art, turrets (`TURR`, `TRMG`, `TLRM`, `TLMG`, `TLLM`, `T30M`), vehicles (`AH6`, `KA50`, `PAVE`, `T72Y`), `OBJT` (tutorial arrows, SP only), `TCAM`, `DBUFFER.ZAR` | Read as packages |
| `RUN\UI\` copies (SP) | 12 | `MM<n>` (map screen), `BF<n>` (briefing), `EQ<n>` (team faces), `UIMM`, `UIBF`, `WSSP` (screen art) | **Listed** (new: `mapImages`) |
| `RUN\UI\` copies (MP) | 22 | `EM<n>` / `EM0<n>` (character portraits for team select), `WSMP`, `WSLC` | Textures only |
| `CZANIM.ZAR`, `LDZANIM.ZAR` | 34 | Shared effect scripts, loading-screen script | Read |

Duplicate leaf names: a map ZDB holds two `DBUFFER.ZAR` (`RUN\SP\M51\` and `RUN\COMMON\`). `ZipperPackage` indexes by leaf, so only the COMMON one is reachable as `Entry("DBUFFER.ZAR")`; saving is unaffected (it writes back by full path).

### `READERM.ZAR` tables per map

| Table | Maps | Map Builder |
|---|---:|---|
| `<map>.rdr`, `<ns>_lib.rdr` | 34 / 34, plus one per extra namespace | World settings: Mission tab › World. Libraries: Asset Viewer |
| `mission.rdr`, `valves.rdr`, `vehicles.rdr`, `aimaps.rdr`, `sndjukeboxm.rdr` | 34 | Mission tab / Asset Viewer ([map-tables.md](map-tables.md)) |
| `actions.rdr` | 31 (not MP5, MP8, MP10) | Actions tab |
| `layers.rdr`, `dyntex.rdr`, `map.rdr` | 33 / 33 / 31 | Asset Viewer; extents in the Mission tab |
| `clutter.rdr` (+ `clutter_template.rdr` on MP64) | 28 | Asset Viewer |
| `chartype.rdr` | 22 (every MP map) | **Team roster**: `navyseals` / `terrorists` → `{character, name}` rows. Read (new: `CharacterTypes.ParseRoster`), not shown yet |
| `hudobjectives.rdr`, `missioncams.rdr` | 12 / 13 (SP + MP61) | Mission tab |
| `lod.rdr` | 8 | `LOD_Definititions` (sic): named fade ranges (`veryclose` farFade 170–200 …) and `LOD_Connection` (object lists per range). Unknown to the editor |
| `net_000.rdr` | 5 MP | A named graph: `nodes {name, pos [x,y,z], radius, flags}`, `edges`, `routes` (MP51: `playerStart`, `backstart`; no edges). Unknown to the editor |
| `lensflare.rdr` | 3 MP | Sun flare: `light_source.node` (`sun`), glare colour, `lens_flare.node` (`sunflare`), opacity, scale. Unknown to the editor |

### The map's own `.ZED` (`M51.ZED`)

| Record | Size | Holds | Status |
|---|---|---|---|
| `DefaultMaterial`, `FoliageSound`, `MetersPerUnit`, `ShadowVector`, `GlobalLighting`, `NightMission`, `LensFX_NVG`, `LensFX_StarlightScope`, `WorldTextSpecs` | small | Compiled copies of `<map>.rdr`'s world settings | Observed, shown from the rdr ([map-tables.md](map-tables.md)) |
| `cameras › camera` | 144 | Compiled camera (fov, clip, fog) | Partial |
| `grid_params` | 20 | `8192, 16, 180.0f, 36, 25`: the rdr's `grid {cell_dim, atom_count, grid_dim, posts}` | Observed |
| `assetlibs` | children | One child per library the map loads (`//sp/m51/flib`, `//common/assetlib/hud` …) | Observed |
| `world_tree` | 0 | Empty on M51 | — |
| `LOD_Object` | 192 on M51 | 6 rows of 32 bytes; floats that are squared fade distances (60², 800², 300², 250², 200², 400²) and a pointer-like word | Partial |
| `Material_Palette › palEntry_<i> › dat (60), tex_name` | 8 entries | 15 words per entry (four colour-like floats, a count, factors), optional texture (`sky01.tif`) | Unknown |
| **`TextureScroll_Object`** | 520 × rows | **Scrolling textures**: model char[256], visual char[256], u, v (f32). M51: 12 river visuals at u = 0.2, sky `fogband1/2` at v = −0.075 / −0.12. 25 maps | **Read** (new: `TextureScroll`, shown in `ASSET_MAP_TABLES.textureScroll`) |

### `AIMAPS.MPS`

| Part | Status |
|---|---|
| AI map records (marker `0x17`, bounds, grid size, name: M51 has `Main Map` plus 8 indoor maps `Church1`, `House1`, `Loft`, `Bridge1`, `Tunnel` …) | Read |
| Grid cells and row table (floor heights) | Read |
| Points (24-byte rows: name, team, kind, packed cell) | Read |
| **Named 28-byte records** right after the points: `Rendezvous`, `Overwatch`, `Extract`, `Supply1`, `GasComment`, `SnipeWarn`, `warn`, `fail`, `router0` … (name at +8; two u16 at +0 that look like cell coordinates). Most likely the mission script's trigger areas | Unknown |
| **Event / pickup section**: token records holding `PICKUP`, `AMMO` / `WEAPON` and a name (`7.62 x 51mm`, `SR-25 SD`, `9x19P`, `12 Gauge`): the weapons and ammo lying on the map | Unknown |
| Per-indoor-map point tables (`Church1`, `Church1b` …) after each indoor grid | Unknown |
| Per-cell AI links after each row table | Unknown |

### `DBUFFER.ZAR`

`file buffersize table` with one child per archive holding a u32. The value is the archive's **body size** (ZED header +0x54): equal for 1430 of the 1488 archive entries found across the 34 packages; the other 58 differ by a few hundred bytes to a few KB either way, and some retail archives are *larger* than listed (M51's own `WORL_MDL.ZED`: body 1,893,008, listed 1,892,384), so the game does not stop at the listed size. Whether it sizes a buffer from it (and how much a body may grow) has not been tested. Map Builder's saves do not update it. Code: `ZAR/BufferTable.cs`; tests: `A_buffer_table_lists_each_archive_with_its_size`, `M51s_buffer_table_lists_each_map_archives_body_size`.

## Shared tables that map work needs

### Character types and weapons (`RUN/READERC.ZAR › character.rdr`)

468 character types. The root object holds `AngleUnits`, `ai_settings`, `body_items` (31 bone names and `gear_nodes`), `gear` (49 worn items: `{name, model, ofs [bone, [pos], [rot]]}`) and `characters`, a flat list: a type's name, optionally `":"` and its parent, then its fields:

| Field | Meaning |
|---|---|
| `model_name` | The character model as authored (`alban_ter01.xsi`); the package draws `MESH_<name without extension>` |
| `lods` | `[[file, distance]]` lower-detail models |
| `texture_asset` | Texture set (`xalbania`, `jnglseal`) |
| `anim_set` | `animset.rdr` entry (`Guard anim set`, `Seal anim set`) |
| **`default_weapons`** | The weapons the type carries: rows `{wep_name, ammo_count, mag_count}` or bare `["wep_name", [name]]`. Names are zweapon.rdr `InternalName`s (`9mm Sub` → model `uzi`) |
| **`default_gear`** | Worn gear by `gear` name (`seal_black_hat`, `seal_goggles`, `Satchel`): the models are in `FLIB_*.ZED` |
| `health`, `armor` | Per body part (`head`, `body`, `larm` …) |
| `ai_params` | `accuracy`, `courage`, `stealth`, `sensors {visual_range, visual_fov, visual_sens, aware_decay}` |
| `sounds`, `VoiceFont`, `SetPitchOffset` … | Voice cues (`CHRSND_*`) |
| `strength`, `recovery_factor`, `multiplayer`, `hostage` | Gameplay values and flags |

A type leaves fields out and inherits them: `m51_terror01 : albanian_terrorist01 : guard_alb` takes its model (`alban_ter01`) from the middle and sets its own weapons (`9mm Sub` 30×2, `Model 18`). M51's player `jungle_A51 : jungle_A_woodland_LO : able1 : basic_seal` resolves to model `seal_A_woodland_LO` with the M4A1 SD, Mark 23 SD, satchel and flashbang. 455 types have a parent, 303 a model, 301 their own weapons.

So the chain for an actor's weapon is: `vehicles.rdr › character` → `character.rdr` type (with parents) → `default_weapons[].wep_name` → `zweapon.rdr` `InternalName` → `ModelName` → the model in `WEAP_MDL.ZED` / `WEAP_GEO.ZED`. MP maps also list their selectable characters in `READERM.ZAR › chartype.rdr`.

Code: `RDR/CharacterTypes.cs` (`Parse`, `Resolve`, `ParseRoster`). `ASSET_MAP_TABLES` adds `chartype {chain, model, textureAsset, animSet, gear, weapons [{name, ammo, magazines, model}]}` to each actor when `READERC.ZAR` sits beside the package (the ZDB's folder or up to two above, as on the disc), and falls back to its model when `data/chartypes.tsv` has none. Tests: `Character_types_inherit_model_and_weapons_from_their_parents`, `A_multiplayer_roster_lists_each_teams_characters`, `Actors_carry_their_character_types_weapons_and_the_map_its_scrolling_textures`, `The_discs_character_types_give_m51s_smugglers_their_models_and_weapons` (needs `MAPBUILDER_DISC`).

Two fixes in the same pass, both found here:

- **`RdrFile.BuildTree` lost shared values.** Its cycle guard was global, so a value block that two lists share was read only the first time: `WeaponDefs` found 55 weapons where `zweapon.rdr` has 86 with a model (SR-25 shares its `ModelName` block with SR-25 SD; the SR-25, Dragunov, 552SD, SR-1 Gyurza, satchel and M203 rounds, among others, were dropped). The guard now covers only the path from the root. Now 84.
- **Two retail weapon records hold a stray note** between fields (M16A2 and SA-80 A2: bare `"adjusted", "from", 40`), which made them odd-length and `WeaponDefs` skipped them. It now steps over a value where a key should be. Now 86. Test: `Shared_value_blocks_are_read_wherever_they_are_used`.

### Weapon art

| Where | What |
|---|---|
| `RUN\COMMON\WEAP_MDL/GEO/TXR/PAL.ZED` (in every map ZDB) | In-hand weapon models (`ModelName`s), grenades, M203 |
| `RUN\UI\UI_MDL.ZED` / `UI_GEO.ZED` | Armory display models (`ak47UI`, `m4AcarbineUI` …), not in map packages |
| `RUN\COMMON\HUDW_TXR.ZED` | Weapon icons (`ak_47_icon.tif` … 65) |
| `RUN\COMMON\HUD2_TXR.ZED` | Reticles (`ret_rifle_01` …) |
| `mission.rdr › Valves › Enable_<weapon>` | Which weapons the SP armory offers |

### Map screens and the TacMap (for the minimap)

| Where | What | Use |
|---|---|---|
| **`RUN\UI\MM<n>_TXR.ZED`** (SP, in the map ZDB) | 9 textures `map_m51_<page>_<part>.tif`. Page 1 is the **area map**: two 512×512 annotated satellite halves (`_1_1` left, `_1_2` right) and a 128×128 overview (`_1_3`). Pages 2 and 3 are the general and specific intel boards | Area map backdrop. `UI/READERC.ZAR › dlg_Map51.rdr` places the halves on screen (`MAPCONTROL`, `UPPERLEFT [15, 66]`, `LOWERRIGHT [625, 405]`, element `XSIZE 512`); **nothing found ties them to world coordinates**, so they would need fitting by hand (objective `LOCATION`s and nav points are the obvious control points) |
| `RUN\UI\BF<n>_TXR.ZED` (SP) | Briefing art: `m51_overview.tif` and `m51_1..3.tif` (512×128 strips), `deploy_m51.tif` (512×256), AOP picture | Not plan views |
| **`RUN\READERC.ZAR › hud.rdr › TacMap`** | The in-game tactical map's style: `Polygons` and `Lines`, nine types each (`Type 0..8` with `Color [r,g,b]` and `Opacity`; e.g. type 4 blue 87,112,176 at 0.5). The shapes it draws are per map, and **where they are stored is not found yet**: not a named record in any map archive. `hudobjectives.rdr › LINE_IDS` (1–5) look like ids of its lines. Candidates: the undecoded `AIMAPS.MPS` sections, or flags on GEO `di` faces | The SOCOM-style radar's colours; the shapes are the top unknown |
| `RUN\COMMON\HUD2_TXR.ZED` | Radar / compass art: `compass_bkrnd` (32²), `compass_lo`, `small_compass` (16²), `FOV.tif` (64², the view cone), `ret_nav_01..24`, `ret_objective`, `ret_friendly`, `ret_hostage`, `ret_extraction`, `ret_base`, `ret_bomb`, `ret_able`, `ret_bravo`, `teammate_health` | Minimap icons |
| `RUN\COMMON\HUD_TXR.ZED` | Action prompts (`action_door_open.tif` …) | Already used by the Actions tab |
| MP maps | No map image on the disc (MP packages carry no `MM`/`BF` archive; `RM<n>` is a sky) | A minimap there must be drawn from geometry |

`ASSET_MAP_TABLES.mapImages` lists the `MM<n>` and `BF<n>` textures a package holds (archive, kind, name, width, height); the Asset Viewer already decodes them.

### Other shared tables (not decoded for the editor)

`missionlist.rdr` (`NAME, NUMBER, TYPE, MULTIPLAYER` per map), `global_valves.rdr`, `ai_turrets.rdr` (turret types per map with `visual_range`), `materials.rdr` (`SOILS`: opacity, penetration, ricochet, footstep sounds; likely what `di` face materials index), `decals.rdr`, `dynamics.rdr` (movement constants: `max_slope`, `step_height`, `gravity`), `orders.rdr`, `non_mission.rdr`, `animset.rdr`, `motion.rdr`, `sounds.rdr`.

## Known vs unknown, at a glance

| Kind | Known (read by Map Builder) | Unknown |
|---|---|---|
| Geometry | MDL, GEO scene, world model, clutter, textures, palettes | `di` face params (material byte, vertex count, flag bits), `Material_Palette`, `LOD_Object` rows |
| Props | GEO definitions, placements, actions | `lod.rdr` use, `layers.rdr` node lists |
| RDR tables | Objectives, actors, mission, world, extents, valves, cams, actions, **character types and weapons** | `setup` numbers, `LINE_IDS`, `team` values, `net_000.rdr`, `lensflare.rdr`, `lod.rdr` |
| Nav / AI | Grids, heights, points | Named 28-byte records, per-indoor point tables, cell links |
| Spawns | Points (kind 2), actors' `setup` | — |
| Objectives | `hudobjectives.rdr`, valves | Trigger areas (likely the named MPS records) |
| Lights | `<map>.rdr` lighting, fog | Which copy the game reads (rdr or `.ZED`) |
| Sound | `sndjukeboxm.rdr` (Asset Viewer) | `sounds.rdr`, `BNKSTORE` / `VAGSTORE` |
| Textures | All TXR/PAL; **scrolling** (`TextureScroll_Object`) | `dyntex.rdr` boundaries |
| Minimap / HUD | Map screen pages, HUD icons, TacMap colours | **TacMap shapes**, image-to-world fit |
| Pickups | — | `PICKUP AMMO/WEAPON` records in `AIMAPS.MPS` |
| Memory | `DBUFFER.ZAR` values | Whether the game needs it updated after an archive grows |

## What to decode next

1. **TacMap shapes.** Find where the per-map polygons and lines live (start with the `AIMAPS.MPS` sections after the points and the `di` face flags; `LINE_IDS` 1–5 should match). This is what a SOCOM-style minimap needs, for MP maps above all, which have no map image.
2. **`AIMAPS.MPS` named records and pickups.** The 28-byte records (likely script trigger areas, with what look like cell coordinates) and the `PICKUP` records (weapons and ammo on the ground). Both are map content a builder should show and move.
3. **Show character types in the Mission tab** and give the 3D figures their real weapons: the data is now in `ASSET_MAP_TABLES` (`actors[].chartype.weapons[].model`); the weapon session can load `WEAP_MDL.ZED` models by that name and attach them at the `rifle` / `pistol` body items (`character.rdr › body_items`).
4. **Map screen fit.** Register `map_<map>_1_1/_1_2` to world x/z with two or three control points per map and store the transform (for SP minimaps).
5. **`DBUFFER.ZAR` after edits.** Test in game whether an archive that grows past its listed size loads; if needed, update the map's table on save (the map's own `DBUFFER.ZAR` must then be reached by full path, not leaf).
6. **`di` face params**: material byte against `materials.rdr › SOILS`, vertex count, flag bits (ladders and climb faces are already sampled in [map-interactions.md](../map-interactions.md)).
7. Smaller tables: `lod.rdr` / `LOD_Object`, `net_000.rdr`, `lensflare.rdr`, `Material_Palette`, `missionlist.rdr`.

## How this was surveyed

A scratch console program on the repo's own readers (`ZdbArchive`, `ZedContainer`, `RdrFile`, `ZipperPackage.DecodeTexture`) listed every package's files and every ZED/ZAR record name (1.6 M records), dumped the tables named here and decoded the map screen textures to PNG. Nothing was written to the disc and no game files are in the repo.
