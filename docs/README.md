# SOCOM Map Builder: Documentation Index

Guides for using Map Builder, references for its file formats, and notes for working on it. New here? Start with [Getting started](getting-started.md), then tour the [Map Builder](map-builder.md).

---

## Using Map Builder

| Document | What it covers |
|---|---|
| [Getting started](getting-started.md) | Requirements, building, the desktop app, opening a package, network security and settings |
| [Map Builder](map-builder.md) | Composed maps: selecting and transforming, placement, the clutter brush, prefabs, characters, map actions, mission tables, points, map check, minimap and walk mode |
| [Model Editor](model-editor.md) | Props, weapons, vehicles and characters: geometry, UVs, textures and palettes, vertex / texture / weight painting, presets, import, OBJ export and conversion |
| [Animation Editor](animation-editor.md) | Playing and editing character clips, keying bones, and firing mounted weapons |
| [Asset Viewer](asset-viewer.md) | Browsing a disc down to archive records, previews for every retail file type, and in-place editing |
| [Working with packages](working-with-packages.md) | Sessions, saving and exporting, memory-card packs, backups, autosave, imports and in-game limits |
| [Controls](controls.md) | Every keyboard and mouse control, by page |
| [AI assistants (MCP)](mcp-server.md) | Connecting an assistant, watching it work, and every MCP tool |
| [Game support](game-support.md) | SOCOM II, SOCOM 3, Combined Assault and GTA: San Andreas support, with in-game examples |

## File formats

| Document | What it covers |
|---|---|
| [Format overview](formats/README.md) | Conventions shared by every format page |
| [Packages](formats/packages.md) | `.ZDB` packages, `.CDB` memory-card packs, what a map package holds |
| [ZED / ZAR containers](formats/zed-zar.md) | The record container every archive uses |
| [Models](formats/models.md) | `*_MDL.ZED` records, DMA chains, geometry chunks, vertex colours |
| [GEO scenes](formats/geo.md) | `*_GEO.ZED` model definitions, nparams, visuals, the map scene |
| [Characters](formats/characters.md) | Skinned `MESH_*` meshes and their skeletons |
| [Textures and palettes](formats/textures.md) | `*_TXR.ZED` texture data, GS registers, `*_PAL.ZED` CLUTs |
| [Clutter](formats/clutter.md) | `CLUTTER.ZAR` placements |
| [AI maps and points](formats/aimaps.md) | `AIMAPS.MPS` grids, spawns and waypoints |
| [Collision](formats/collision.md) | GEO `di` faces: what players and bullets hit |
| [RDR tables](formats/rdr.md) | `.rdr` typed tables (READERM.ZAR, ZWEAPON …) |
| [Map mission tables](formats/map-tables.md) | Objectives, actors, mission and world settings, valves, chartype loadouts |
| [Disc survey](formats/disc-survey.md) | Every map file on the disc, known vs unknown, and what to decode next |
| [HUD radar and tactical maps](formats/hud.md) | `HUD2_TXR.ZED` radar art and `MM<nn>_TXR.ZED` map pages |
| [Animations](formats/animations.md) | Motion clips (MOTION_S / MOTION_P) and ZANIM effect scripts |
| [Map interactions](map-interactions.md) | Object actions, climb and ladder geometry, and the mission tables across all 34 retail maps |

## Working on Map Builder

| Document | What it covers |
|---|---|
| [Development](development.md) | Live reload, tests, browser checks and contribution rules |
| [Architecture](architecture.md) | How the server, client, MCP server and desktop app fit together; repository layout; format conventions |
| [Command reference](command-reference.md) | Every `/api/command` command and `ASSET_EDIT` / `VIEWER_EDIT` operation |
| [Roadmap and work tracker](ROADMAP.md) | What is done, in progress and next; updated with every change |
| [Handover](HANDOVER.md) | Where each wave of work stands, and the project's working conventions |
| [Asset Viewer handoff](asset-viewer-handoff.md) | The Asset Viewer's code layout, editing model and verification notes |
