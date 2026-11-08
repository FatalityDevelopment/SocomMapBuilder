# AI maps and points (`AIMAPS.MPS`)

A map package's `AIMAPS.MPS` holds the AI navigation grids and the named points: team starts, hostage starts, waypoints. Code: `MPS/MpsFile.cs` (grids), `MPS/MapPoints.cs` (points, a port of the page's `zed-spawn-points.ts`). Both layouts were worked out from retail files and match every retail map checked; moving a point is not yet confirmed in game.

## AI map records

| Offset | Size | Field |
|---|---|---|
| 0x04 | u32 | AI map count |
| 0x28 … | | records, each starting with a u32 marker `0x17` |

From the marker:

| Offset | Size | Field |
|---|---|---|
| +0x04 | 3 × f32 | min x, y, z |
| +0x10 | 3 × f32 | max x, y, z |
| +0x1C | u32 | grid width (cells) |
| +0x20 | u32 | grid height (cells) |
| +0x24 | f32 | cell size x |
| +0x28 | f32 | cell size z |
| +0x38 | char[32] | name, NUL-padded |

The reader scans the body in 4-byte steps and accepts a marker only when the bounds and sizes are sane.

## Points

A table of `count` (u32), one more u32, then 24-byte records:

| Offset | Size | Field |
|---|---|---|
| +0x00 | char[16] | name |
| +0x10 | u8 | team (`0xFF` on starts) |
| +0x11 | u8 | kind: 2 start, 1 waypoint, 0 spectator |
| +0x12 | 2 × u8 | unused |
| +0x14 | u32 | packed cell: x in bits 6–18, z in bits 19–31 |

A point's position is the centre of its cell in the first AI map's grid: `min + (cell + 0.5) × cell size` on x and z, and its height is that cell's floor in the grid (below). Moving a point rewrites the packed cell.

The table sits right after the first AI map's row table. The file holds no offsets: the header is the version and the map count, and the tables after the points (the 28-byte named records, then the other AI maps) are count-prefixed and read in order. So `addPoint` inserts a record at the end of the table and `removePoint` takes one out, moving the rest of the file by 24 bytes; both update the count. A new record copies the unused bytes and the packed word's low bits from a point of the same kind. Byte +0x12 is `0x99` on MP2's points. The word after the count, `0x01180B00` on MP2, decodes as a packed cell and is kept. Not yet tried in game.

## Grid cells

After each AI map's header (+0x58 from the marker) comes a sub-header, then the cells and a row table:

| Offset (from +0x58) | Size | Field |
|---|---|---|
| +0x1C | u32 | cell count (only cells that exist are stored) |
| +0x50 | count × 8 | cells, row by row |
| then | height × 12 | one entry per row |

A cell (8 bytes): byte 0 and byte 3 are flags, the u16 at +1 holds the floor height in its low 11 bits, and the u32 at +4 links to a stacked layer (a floor above, `0xFFFFFFFF` for none). Height: `min y + (u16 & 0x7FF) / 2047 × (max y − min y)`.

A row entry: u16 first x, u16 end x (exclusive; empty rows have first = end = width), u32 index of the row's first cell, and a pointer the game fills at load. Cell (x, z) is cell `index + x − first` of row z when `first ≤ x < end`. On every retail map the rows add up to the cell count, and the decoded heights match the floors under the points (terrain, and floors inside buildings where the terrain has holes). `MpsMap.HeightAt` reads it; the Map Builder stands point markers on it. What follows the row table (per-cell links for the AI) is not decoded.
