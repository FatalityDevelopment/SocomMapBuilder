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

A point's position is the centre of its cell in the first AI map's grid: `min + (cell + 0.5) × cell size` on x and z. Moving a point rewrites the packed cell. The height comes from the map surface.
