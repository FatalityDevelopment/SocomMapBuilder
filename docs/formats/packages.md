# Packages: .ZDB and .CDB

A package is a flat list of files, each with a path (`RUN\SP\M51\M51_MDL.ZED`) and its bytes. Code: `ZDB/ZdbArchive.cs` (read/write), `Package/PackageSave.cs` (CDB writer), `Package/ZipperPackage.cs` (indexing by namespace).

The first u32 tells the variant:

| Magic | Variant | Used by |
|---|---|---|
| `0xC8` | Type 1 (ZDB v6) | SOCOM 3 / Combined Assault |
| `0xFC` | Type 2 | SOCOM II packages (M51.ZDB …) |
| `0x110` | Memory-card pack (.CDB) | Packs the game mounts from `mc0:` |

## Type 2 (SOCOM II .ZDB)

Main header (0xFC bytes):

| Offset | Size | Field |
|---|---|---|
| 0x00 | u32 | header size, `0xFC` |
| 0x04 | u32 | unknown (kept) |
| 0x08 | u32 | id word (`0x3F8898BE` in M51.ZDB; kept verbatim) |
| 0x0C | u32 | unknown (kept) |
| 0x10 | u32 | unknown (kept) |
| 0x14 | char[] | version string, NUL-terminated (to 0x97) |
| 0x98 | u32 | file count |
| 0x9C | u32 | file header size, `0x5C` |

File headers start at `0xA0`, one per file, each `size` bytes long (read from the header itself):

| Offset | Size | Field |
|---|---|---|
| +0x00 | u32 | this header's size |
| +0x04 | char[0x40] | path, NUL-terminated. The bytes after the NUL are not zero in retail files and are written back as read |
| +0x44 | u32 | data offset (from the start of the file) |
| +0x48 | u32 | data size |
| +0x4C | 3 × u32 | zero |
| +0x58 | u32 | trailing word, not zero in retail files (kept) |

Files are stored uncompressed.

## Type 1 (SOCOM 3 / CA .ZDB)

Main header 0xC8 bytes: header size `0xC8` (+0x00), file header size `0x58` (+0x04), version (+0x08), archive header size (+0x0C, data offsets are relative to it), body size (+0x10), sentinel `0x4532ABCD` (+0x14), compression (+0x18), error / warning counts (+0x1C / +0x1E), file count at +0xC4. File headers follow at 0xC8, each `0x58` bytes: path (+0x00), resource tag (+0x40, four ASCII characters such as `ASST`, `CHAR`), resource version (+0x44), data offset (+0x48), size (+0x4C), checksum (+0x50, recomputed on write, `ZdbChecksum.cs`). Map Builder reads these packages and opens them read-only.

## Memory-card pack (.CDB)

The game mounts `mc0:/BASCUS-97275<NAME>/<NAME>.CDB` natively. Layout as `PackageSave.BuildCdb` writes it, confirmed in game with `loadpack`:

| Offset | Size | Field |
|---|---|---|
| 0x00 | u32 | `0x110` |
| 0x04 | u32 | 1 |
| 0x0C | u32 | 2 |
| 0x98 | u32 | file count |
| 0x9C | u32 | entry size, `0x70` |
| 0xA0 | 0x70 × count | entries: +0x00 `0x70`, +0x04 path (NUL-terminated, under 0x40 bytes), +0x44 data offset, +0x48 size |

The header and table are padded to 0x800 bytes. Each file starts on a 0x800 boundary and is padded to the next one. Pack names start with `m` and have at most 7 letters/digits: the loader keeps the directory only for `mp/m` and `sp/m` paths and builds the card path in a 32-byte buffer.

## What a map package holds

Retail map packages hold only `.ZED`, `.ZAR` and `.MPS` files at the root. For M51.ZDB:

- `RUN\SP\M51\` — the map's namespaces: `M51_MDL/GEO/TXR/PAL.ZED` (props), `WORL_MDL.ZED` (the world model), `CLIB_*.ZED` (characters), `FLIB_*`, `BENZ_*` (vehicles), `ZM51_TXR/PAL`, `CLUTTER.ZAR`, `MOTION_S.ZAR`, `READERM.ZAR` (.rdr tables), `MZANIM.ZAR`, `DBUFFER.ZAR`, `AIMAPS.MPS`.
- `RUN\COMMON\` — shared archives such as `WEAP_*.ZED` and `EFFE_*.ZED`.
- `RUN\UI\`, `RUN\LOCALE\…` — interface textures and localised strings.

Other files (`.rdr` screens and tables) live inside ZARs, not at the package root.
