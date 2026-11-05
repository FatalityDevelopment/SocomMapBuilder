# Game support

SOCOM U.S. Navy SEALs II is the primary target. Combined Assault also supports selected map edits. Other PlayStation 2 titles that share its engine or formats open for reading and model conversion.

| Game | Open | Edit | Convert |
|---|---|---|---|
| **SOCOM U.S. Navy SEALs II** | Packages (`.ZDB`, `.CDB`), loose `.ZED` / `.ZAR`, unpacked folders, the disc | Maps, models, textures, palettes, animations, mission tables and disc records | To SOCOM 3 / Combined Assault |
| **SOCOM: Combined Assault** | Location archives by name, missions, world, actors, loadouts and named objective areas | Points, actor starts and headings, valve start values, allowed weapon slots and world placements. Save to a copy. | Models to SOCOM II |
| **SOCOM 3** | Compatible asset archives | Map edits verified on the Combined Assault disc only | Compatible models to SOCOM II |
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

## SOCOM: Combined Assault

Each location is a `RUN\<hash>.ZAR` archive. Open a location or mission by name, such as `G:\RUN\COMPOUND\SP432.ZDB`, then use **Mission** in the toolbar to switch missions. The map shows named points, squad starts and flags at named objective regions. Verified demolition layouts also show SEAL and Terrorist team starts. The **Mission** tab lists actors, their equipment and starts, allowed weapons and valves.

Move a point within its current AI zone or rename it. The editor updates actor starts that use the old name. Check compiled actions and scripts in game after a rename. In the Mission tab, edit an existing actor start and heading, valve value or allowed weapon slot. Move a world object to reposition its node, attached meshes and children. The original AI navigation and baked visibility still apply.

Undo and redo work across missions in the same archive. **Save copy** writes the complete archive into the selected folder. To see shared weapon models and labels when reopening, place the copy beside the game's `GLOBAL.ZDB`. In-game verification is pending. [File layouts and in-game check](formats/ca-missions.md#editing-and-saving)

## Grand Theft Auto: San Andreas (partial)

Character and prop models open and convert, along with some vehicles. Support is incomplete, so treat exports as experimental and check the results in your target workflow.

<img src="screenshots/gta-character.png" alt="CJ from GTA: San Andreas open read-only in the Model Editor" width="850">

*CJ from `PLAYER.IMG`, open read-only in the Model Editor with **Export to SOCOM II** in the header.*

## Not included

This repository does not include game assets, or the CodeDesigner runtime source used to inject map items and launch new maps.
