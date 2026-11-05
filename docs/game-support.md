# Game support

SOCOM U.S. Navy SEALs II is the primary target and the only game Map Builder edits fully. Other PlayStation 2 titles that share its engine or formats open for reading and model conversion.

| Game | Open | Edit | Convert |
|---|---|---|---|
| **SOCOM U.S. Navy SEALs II** | Packages (`.ZDB`, `.CDB`), loose `.ZED` / `.ZAR`, unpacked folders, the disc | Maps, models, textures, palettes, animations, mission tables and disc records | To SOCOM 3 / Combined Assault |
| **SOCOM 3** and **SOCOM: Combined Assault** | Archives, read-only; Combined Assault maps by location name, with each mission's points | — | Combined Assault models to SOCOM II |
| **Grand Theft Auto: San Andreas** | `.ISO` or `.IMG`, read-only | — | Models to SOCOM II (partial) |
| **SOCOM: Fireteam Bravo** | Not tested | — | — |

## SOCOM II

Map, model and animation edits round-trip through save and reopen. Imported models, imported textures and textures made from images render in the editors, but not every combination has been tested in game, so test on copies. See [Working with packages](working-with-packages.md) for the limits that apply in game (VRAM, character records, AIMAPS points).

### In game

<img src="screenshots/raygun.png" alt="A custom ray gun in the SOCOM II armory" width="850">

*A custom weapon asset in the armory.*

<img src="screenshots/hummer.png" alt="A Hummer vehicle in game" width="850">

*A vehicle model in game.*

<img src="screenshots/tank.png" alt="A tank model in game" width="850">

*A tank model in game.*

<img src="screenshots/ca_char.png" alt="A Combined Assault character asset in game" width="850">

*A character asset from Combined Assault.*

## SOCOM: Combined Assault (read-only)

Each location is one archive on the disc, `RUN\<hash>.ZAR`. The file browser shows the location's name next to it (`055146D2.ZAR  POLAND_RURAL`). You can also open a location or one of its missions by name: `G:\RUN\WHARF` or `G:\RUN\WHARF\SP422.ZDB`. The Map Builder draws the location's world and the selected mission's points: squad starts and AI spawns, AI nav points, the markers named on the map screen, and doors and objective places. Pick another mission from **Mission** in the toolbar. Teams and mission tables are not read yet, and nothing can be edited or saved. Layouts: [Combined Assault missions](formats/ca-missions.md).

## Grand Theft Auto: San Andreas (partial)

Character and prop models open and convert, along with some vehicles. Support is incomplete, so treat exports as experimental and check the results in your target workflow.

<img src="screenshots/gta-character.png" alt="CJ from GTA: San Andreas open read-only in the Model Editor" width="850">

*CJ from `PLAYER.IMG`, open read-only in the Model Editor with **Export to SOCOM II** in the header.*

## Not included

This repository does not include game assets, or the CodeDesigner runtime source used to inject map items and launch new maps.
