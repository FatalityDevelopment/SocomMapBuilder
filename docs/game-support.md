# Game support

SOCOM U.S. Navy SEALs II is the primary target. Combined Assault also supports selected map edits. Other PlayStation 2 titles that share its engine or formats open for reading and model conversion.

| Game | Open | Edit | Convert |
|---|---|---|---|
| **SOCOM U.S. Navy SEALs II** | Packages (`.ZDB`, `.CDB`), loose `.ZED` / `.ZAR`, unpacked folders, the disc | Maps, models, textures, palettes, animations, mission tables and disc records | To SOCOM 3 / Combined Assault |
| **SOCOM: Combined Assault** | Location archives by name, missions, world, actors, loadouts and named objective areas | Points, actor starts and headings, valve start values, allowed weapon slots and world placements. Save to a copy. | Models to SOCOM II |
| **SOCOM 3** | Compatible asset archives | Map edits verified on the Combined Assault disc only | Compatible models to SOCOM II |
| **Grand Theft Auto: San Andreas (PS2)** | Mounted disc, ISO, IMG or DFF | Frame positions, geometry transforms and material properties. Save DFF or IMG copies. | Models and selected character clips to SOCOM II |
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

## Grand Theft Auto: San Andreas (PS2)

In the **Model Editor**, choose **File → Open package**, select the mounted GTA disc and click **Open folder**. You can also open its ISO, a single IMG archive or a loose DFF. Opening the disc gives the editor access to the game's model definitions, texture dictionaries, vehicle paint schemes, clothing and animation banks. Filter the model list by name or kind. The archive label distinguishes models with the same name, such as the Coach bus and CJ's coach jacket.

In the **Item** tab, select a frame to change its position relative to its parent, or select a geometry to scale and offset its vertices. The fields use GTA coordinates, with Z up. For a missing texture, use **Missing textures** to select its material. Pick an image from **Available textures** to preview it, or enter a native texture name of up to 31 printable ASCII characters. **Apply material** writes that name and the RGBA channels. Choose an individual clothing model to edit CJ's outfit pieces. The **UV Editor** can move, flip, rotate and snap primary UV coordinates on sections with stored UVs. Each edit supports undo and redo. For clothing files containing multiple body-shape clumps, the editor displays and edits the first clump.

**File → Save model as DFF** writes the selected model. **File → Save edited IMG archive** writes a full copy of the selected model's archive with all its edited entries. Fixed-size edits use the existing IMG entry when it has its own sector range. A longer texture name can grow a DFF. If its entry outgrows its slot or shares sectors with another entry, the writer appends the edited DFF and updates its directory record in the copy. A DFF stores references to textures. Keep the relevant TXDs alongside a loose model, or reopen the IMG in a copy of the original disc layout for its full dependency set.

**Export to SOCOM II** builds a memory-card pack from the current model including its session edits. Choose **File → Export with options** to include animation clips for a character. Automatic pack names include the source archive and model, so the Coach bus and the coach jacket export separately. If an automatic output name is occupied, the exporter selects another one. The resulting pack can be reopened in the editor. In-game verification of the new native edits and conversion changes is pending.

On the US PS2 disc (SLUS-20946), the editor produces geometry for all 15,208 source DFFs and the assembled CJ preview. All 32,555 texture records decode. The loader uses declared texture dictionaries, their parents, placement links to higher-detail models, and exact raster names. When it searches across archives it requires all candidates to agree on the pixels and keeps the player clothing separate. Of the original 219 unresolved material references, 47 now resolve. The remaining 172 appear in the editor for manual repair.

<img src="screenshots/gta-character.png" alt="CJ from PLAYER.IMG in the Model Editor" width="850">

## Not included

This repository does not include game assets, or the CodeDesigner runtime source used to inject map items and launch new maps.
