# SOCOM 1 maps (disc layout)

*SOCOM U.S. Navy SEALs* (SCUS-97134) has no `.ZDB` packages. Each map is a folder of loose files on the disc, and the archives inside are in SOCOM II's formats ([ZED / ZAR](zed-zar.md), [models](models.md), [GEO](geo.md), [textures](textures.md), [clutter](clutter.md)) under other names. Map Builder opens a map folder as a package and indexes each file under its SOCOM II name. Code: `Package/Socom1DiscMap.cs`, `ZipperPackage.OpenSocom1`. Tests: `Socom1DiscMapTests` (the retail check needs `MAPBUILDER_SOCOM1_DISC`, the disc root).

## A map folder

`RUN\MP\MP1\` (multiplayer `MP1`–`MP12`) or `RUN\SP\M5\` (single-player `M5`–`M21`):

| File | Holds |
|---|---|
| `<MAP>.ZED` | World settings (as SOCOM II's `<MAP>.ZED`) and **`assetlibs`**: one child per library the map loads, named as a path under `RUN\` |
| `AIMAPS.MPS` | AI maps and points. **Not read yet**: the header differs from SOCOM II's (it starts `02 00 00 00 03 00 00 00`), and the map shows 0 points |
| `CLUTTER.ZAR`, `READERM.ZAR`, `MZANIM.ZAR` | As in SOCOM II |
| `ASSETLIB\<MAP>\`, `CLIB\`, `FLIB\`, `JUNKYARD\` | The map's own library, characters, worn gear, the empty texture placeholder |

`assetlibs` on MP1: `//mp/mp1/flib`, `//mp/mp1/clib`, `//mp/mp1/junkyard`, `//common/assetlib/font`, `hud`, `weapons`, `hudweaps`, `alpha_fx`, `effects`, `//mp/mp1/assetlib/mp1`. Every map lists the same set with its own name. Paths are lowercase; the loader matches the disc's folders case-insensitively and keeps their real spelling.

## A library folder

| SOCOM 1 file | Holds | SOCOM II name it is read as |
|---|---|---|
| `<NS>_MDL.ZED` | `models` › definitions with `nparams`, `visuals`, `di`… | `<NS>_GEO.ZED` |
| `MODELS.ZAR` | One record per model: geometry payload and `N000_…` visual stubs | `<NS>_MDL.ZED` |
| `<NS>_TXR.ZED`, `<NS>_PAL.ZED` | Textures and palettes | (same) |

The map's own `MODELS.ZAR` also holds the `worldmodel` record, which SOCOM II keeps in a separate `WORL_MDL.ZED`. Older visual stubs are named `N000_000` as well as `N000_I000_V00` (see [models](models.md)).

## Opening and saving

- Open the map folder, its `<MAP>.ZED`, or any file directly in it. A folder qualifies when it holds `<FOLDER>.ZED` with an `assetlibs` list and an `ASSETLIB` subfolder. SOCOM II's loose folders have no `ASSETLIB`, so they open as before.
- Libraries are looked up under the `RUN` folder above the map. A library that is not on disk is listed in the session status as `missingLibraries`; the map opens without it.
- The session status says `edition: "SOCOM 1"`. It is not `game`: the pages read a set `game` as a SOCOM 3 / CA or GTA session.
- Saving writes each changed file back under its disc name and folder: over the original when no output is given, else under `<output>\RUN\…` in the same layout. Memory-card packs (`mode: pack`) are refused: they are SOCOM II's.

## Coverage on the disc (2026-11-05)

18 of the 22 maps open with their world, props and clutter (M5–M19 except M8, MP1–MP12 except MP8 and MP9). The other four failed because their files were unreadable on the copy tried, not because the format differs. M8, MP8 and MP9 have archives that are all zeros, and M21's `M21_MDL.ZED` has 32 zeroed sectors in the middle. Archives with a blank header are listed in the status as `blankFiles`, and a blank `<MAP>.ZED` is reported as a damaged copy.

## Not done yet

- `AIMAPS.MPS` (spawn and waypoint points) and the `READERM.ZAR` tables' differences from SOCOM II.
- Saved copies are partial: only the changed files are written under the output folder, so a copy opens on its own only when the libraries it lists are there too.
- Nothing written by Map Builder has been tried in SOCOM 1.
