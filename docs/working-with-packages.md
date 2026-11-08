# Working with packages

Map Builder edits SOCOM II packages in memory and writes nothing until you save or export. This page explains how sessions, saving, importing and new textures work, so you can keep your original files safe.

## Sessions

Opening a package creates an **asset session** for it: one per package path, shared by every page and by AI assistants connected over [MCP](mcp-server.md). Changes made in the Map Builder, Model Editor, Animation Editor and Asset Viewer all land in the same session and are undone, redone and saved together.

The status chip in each page's header shows the package, whether it has unsaved edits, and its revision.

## Saving and exporting

| Command | Writes |
|---|---|
| **File › Save copy…** (`Ctrl+S`) | A new `.ZDB` or `.CDB` with every edit in the session |
| **File › Export memory-card pack** | `<NAME>.CDB` for `mc0:/BASCUS-97275<NAME>/`. Pack names start with `m` and can contain up to 7 letters or digits. |
| Asset Viewer **Import / Replace / Remove** | A file you choose; saving over the open file keeps `<name>.bak` the first time |

- **Saves are all or nothing.** The new file is written to `<name>.tmp` and then replaces the target, so a crash mid-save leaves the old file intact.
- **Writing over an existing file keeps a backup.** `<name>.bak` (the original) is kept once; delete it yourself when you no longer need it.
- **Autosave** writes a recovery copy of each session with unsaved edits every 3 minutes to `%LOCALAPPDATA%\MapBuilder\autosave` (see [Settings](getting-started.md#settings)). The session itself stays unsaved.

> **Work on copies.** Keep your original packages untouched and test modified packages in game before relying on them.

## Importing models and textures

- **Importing a model** copies its MDL record, its GEO definition (a character's skeleton), every texture its sections name with their mip levels, their palettes, and any models its definition is built from. Undo removes the whole import.
- **Texture names and palette ids are global in game** (the first one registered wins), so imports keep a palette's id only when the target does not already use it; new palettes take ids from a block of their own.
- **New textures** are 8-bit indexed, a power of two on each side (8–512; by default the image's size rounded, at most 256), with a 256-colour palette made from the image: 32-bit when the image has soft alpha, otherwise 16-bit like most retail palettes.
- **Retail models name textures in 16-byte fields**, so a texture applied to them needs a name of at most 15 characters.

## Limits to keep in mind

- **Runtime VRAM is limited.** Large textures or many new textures may exceed what a map can use in game. The map lint (`assets_validate`) checks a VRAM budget.
- An imported character can appear correctly in the editors, but spawning it in a mission also requires the relevant character records.
- AIMAPS points can be moved, renamed and retyped, and added or removed through the MCP. Adding and removing have not been tried in game.
- Collision is per placed reference (see [Collision](formats/collision.md)). Hiding a world visual leaves its floor or wall solid until `clearCollision` removes it, and a new prop needs `setCollision`.
- The chartype table (`READERC.ZAR`) is shared by every map, so per-map loadouts cannot be changed from the Map Builder.

## Related

- [Packages format](formats/packages.md): `.ZDB` packages, `.CDB` memory-card packs and what a map package holds
- [Textures and palettes](formats/textures.md)
