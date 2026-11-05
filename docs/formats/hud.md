# HUD radar art and tactical maps (`HUD2_TXR.ZED`, `MM<nn>_TXR.ZED`)

What the Map Builder's minimap draws, and where SOCOM II keeps it. Everything here is ordinary TXR/PAL texture pairs ([Textures and palettes](textures.md)); this page says which textures they are and what they show. Code: `Services/AssetService.MapHud.cs` (ASSET_MAP_HUD), `web/src/viewport/minimap.ts`, `web/src/components/Minimap.tsx`.

## Where the art is

| Archive (in each map package) | What | Notes |
|---|---|---|
| `RUN\COMMON\HUD2_TXR.ZED` / `HUD2_PAL.ZED` | The radar and reticles | In every retail map package, SP and MP (M51, M62, M83, MP1, MP5 checked) |
| `RUN\UI\MM<nn>_TXR.ZED` / `MM<nn>_PAL.ZED` | The pause screen's map pages (SP only) | `MM51` in M51.ZDB, `MM62` in M62.ZDB … |
| `RUN\COMMON\HUD_TXR.ZED` | Action prompts (`action_door_open` …) | Not used by the minimap |
| `RUN\COMMON\HUDW_TXR.ZED` | Weapon icons (`m4carbine_icon` …) | Not used by the minimap |
| `RUN\UI\BF<nn>_TXR.ZED` (on the disc's `RUN\UI`, and in the package) | Briefing art: `m51_overview`, `m51_1..3`, `deploy_m51` (512 × 256) | Painted briefing pages, not a world-aligned map |

MP packages carry no `MM` archive (`OM01_TXR` in MP1.ZDB is only the loading picture `ld_mp1_prog`), so an MP minimap is drawn from the map's own geometry (a top-down render).

## The radar (`HUD2_TXR.ZED`)

All 8-bit indexed, mostly white with alpha so the game can tint them. Names without `.tif`:

| Texture | Size | What it shows | Minimap use |
|---|---|---|---|
| `compass_lo` | 128² | The radar disc: dark blue glass, an **N** at the top and ticks at the four points | Laid over the map at 55 % and turned so N points north |
| `compass_bkrnd` | 32² | A dark round shadow | (served, not drawn) |
| `small_compass` | 16² | A small ring with a centre dot | The camera's own position while orbiting |
| `FOV` | 64² | The view cone, apex at the bottom middle | Drawn from the centre upwards |
| `ret_triangle` | 16² | A white triangle pointing up | The player / view centre |
| `ret_friendly` | 16² | **F** in a ring | SEALs (tinted green) |
| `ret_able`, `ret_bravo` | 16² | **A** / **B** in a ring | SEALs starting at an `Able*` / `Bravo*` point; nav points named A… / B… |
| `ret_threat` | 16² | A red arc (already red) | Other actors (enemies) |
| `ret_hostage` | 16² | **H** in a ring | Actors whose chartype or name says hostage / informant / civilian |
| `ret_bomb` | 16² | A bomb | (served, not drawn) |
| `ret_base` | 16² | A white triangle | Start points no actor uses (tinted grey, or blue / red by team 1 / 2) |
| `ret_objective` | 16² | A white star | HUD objectives (`hudobjectives.rdr` LOCATION); secondary ones dimmer |
| `ret_extraction` | 16² | A white **X** | An objective whose text says extraction |
| `ret_nav_01` … `ret_nav_24` | 16² | **C** … **Z** in a square box (01 = C, 24 = Z) | AIMAPS waypoints (Charlie, Delta …) by their first letter. A and B are the fireteams above, so the letters start at C |
| `teammate_health` | 16² | A small soft square | (served, not drawn) |

The radar also has the reticles (`ret_rifle_01` …), NVG overlays and sun flares; the minimap does not use them. Action props have no radar art in the game, so the minimap draws them as small squares in the Actions tab's colours.

The tactical map's line and polygon colours are in `RUN\READERC.ZAR › hud.rdr › TacMap` (nine types, `Color` and `Opacity`; found by the disc survey, `disc-survey.md`). The per-map shapes they colour have not been found yet, so the minimap does not draw them.

## Tactical maps (`MM<nn>_TXR.ZED`)

| Texture | Size | What |
|---|---|---|
| `map_<map>_1_1`, `map_<map>_1_2` | 512² each | Page 1: the annotated satellite map, left and right halves (1024 × 512 together; the game shows it squeezed to about square) |
| `map_<map>_1_3` | 128² | Page 1's thumbnail |
| `map_<map>_2_*`, `map_<map>_3_*` | same | Pages 2 and 3: intel photo collages, not maps |

M62's `MM62` holds page 1 only.

### Where page 1 sits in the world

Nothing in the package says where the picture lies in the world, so each map is measured by hand and kept in `server/data/tacmaps.json` as `world: [x at the left edge, x at the right edge, z at the top edge, z at the bottom edge]`. North (−Z) is up, +X right; the image is not mirrored.

**M51** (measured 2026-11-04): `[-78, 6926, 270, 4467]`, from landmarks drawn on the map against the map's points and objectives:

| Landmark on the map | Image (1024 × 512) | World (x, z) |
|---|---|---|
| "Insertion" X | 150, 68 | `sealstart` 948, 828 |
| "Office – Esnik works here" | 815, 270 | objective *Capture foreman* 5458, 2448 |
| "Demo site 1" | 828, 378 | point `Demo Site 1` 5498, 3368 |
| "Demo site 2" | 862, 315 | `Romeo` 5818, 2758 |

Fit: image x = 11.4 + 0.1462 · world x, image y = −33 + 0.122 · world z. Checked by blending a top-down render of that rectangle over the map: the river, the village's loop road and the tree clusters line up. The "Extraction" X sits about 20 px north-east of the *Move to extraction* objective location (where the truck stops, presumably).

**Not measured yet:** M52, M53, M61–M63, M71–M73, M81–M83. Until they are, their minimap uses the top-down render. To add one: find two or three landmarks on `map_<map>_1_1/_1_2`, fit x and z separately, and add the entry.

## Combined Assault

Combined Assault's location archives contain 30 images named `RUN\RAW\MAPS\*.RAW`. Each contains 256 × 256 pixels in row-major order, with four bytes per pixel (red, green, blue, zero). They show perspective mission scenery. For example, `M_432.RAW` shows the facade of the church in COMPOUND. The Map Builder renders its CA minimap from the location's world geometry and draws mission markers using the viewport's world coordinates.

## ASSET_MAP_HUD

`{ revision, map, icons: { <name>: { width, height, png } }, missing: [names], tacmap: { name, tiles: [left, right], world } | null }`. `icons` covers the names in `ZipperAssetService.HudTextures` (the table above); a package without HUD2 lists them under `missing` and the page draws vector stand-ins. `tacmap.world` is null when the map has page 1 but no measured placement. Tests: `MapHudTests`.
