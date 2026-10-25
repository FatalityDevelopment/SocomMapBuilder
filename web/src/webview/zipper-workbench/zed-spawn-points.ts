// Named points from a map's AIMAPS.MPS (player / hostage starts, AI
// waypoints) and ground height lookup, for the editor's spawn camera.
//
// Layout (SOCOM II, checked on MP53): a table of `count` (u32) records, an
// unknown u32, then 24-byte records:
//   +0x00 name, 16 bytes, NUL padded
//   +0x10 u8 team (0xFF on starts, 0 on waypoints)
//   +0x11 u8 kind (2 = start, 1 = waypoint, 0 = spectator)
//   +0x12 2 bytes unused (left-over name bytes)
//   +0x14 u32 cell: x = bits 6..18, z = bits 19..31, cells of the first
//         AI map's grid (min x/z + cell * scale; MP53: 10 units)
// MP53 check: Delta / Echo / Foxtrot decode within 3..8 units of bunker3 /
// bunker2 / bunker1, Charlie onto the broken bridge.

export interface MpsGrid {
  minX: number;
  minZ: number;
  maxY: number;
  minY: number;
  scaleX: number;
  scaleZ: number;
  width: number;
  height: number;
}

export interface MapPoint {
  name: string;
  team: number;
  kind: 'start' | 'waypoint' | 'spectator' | 'other';
  cellX: number;
  cellZ: number;
  // World position; y is filled in later from the decoded geometry.
  x: number;
  y: number | null;
  z: number;
}

function readName(bytes: Uint8Array, offset: number): string | null {
  let s = '';
  let ended = false;
  for (let i = 0; i < 16; i++) {
    const b = bytes[offset + i];
    if (ended) {
      if (b !== 0) { return null; }
      continue;
    }
    if (b === 0) { ended = true; continue; }
    if (b < 0x21 || b > 0x7e) { return null; }
    s += String.fromCharCode(b);
  }
  return s.length >= 2 ? s : null;
}

export function parseMapPoints(bytes: Uint8Array, grid: MpsGrid | null): MapPoint[] {
  if (!grid || bytes.length < 64) { return []; }
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  for (let o = 0; o + 8 + 24 <= bytes.length; o += 4) {
    const count = view.getUint32(o, true);
    if (count < 1 || count > 256) { continue; }
    const first = o + 8;
    if (first + count * 24 > bytes.length) { continue; }
    const points: MapPoint[] = [];
    let ok = true;
    for (let i = 0; i < count; i++) {
      const r = first + i * 24;
      const name = readName(bytes, r);
      const kindByte = bytes[r + 0x11];
      if (!name || kindByte > 2) { ok = false; break; }
      const packed = view.getUint32(r + 0x14, true);
      const cellX = (packed >>> 6) & 0x1fff;
      const cellZ = (packed >>> 19) & 0x1fff;
      if (packed !== 0 && (cellX >= grid.width || cellZ >= grid.height)) { ok = false; break; }
      points.push({
        name,
        team: bytes[r + 0x10],
        kind: kindByte === 2 ? 'start' : kindByte === 1 ? 'waypoint' : packed === 0 ? 'spectator' : 'other',
        cellX,
        cellZ,
        x: grid.minX + (cellX + 0.5) * grid.scaleX,
        y: null,
        z: grid.minZ + (cellZ + 0.5) * grid.scaleZ,
      });
    }
    if (ok && points.some(p => p.kind === 'start' || p.kind === 'waypoint')) {
      return points.filter(p => p.kind !== 'spectator');
    }
  }
  return [];
}

interface TriangleSource {
  chunks: Array<{ vertices: Array<{ x: number; y: number; z: number }>; indices: number[] }>;
}

// Height of the walkable surface under (x, z): the highest triangle there
// that is not above the AI map's ceiling (keeps roofs and sky cards out).
export function groundHeight(model: TriangleSource, x: number, z: number, maxY: number): number | null {
  let best: number | null = null;
  for (const c of model.chunks) {
    const v = c.vertices;
    for (let t = 0; t + 2 < c.indices.length; t += 3) {
      const a = v[c.indices[t]], b = v[c.indices[t + 1]], d = v[c.indices[t + 2]];
      if (!a || !b || !d) { continue; }
      const minX = Math.min(a.x, b.x, d.x), maxX = Math.max(a.x, b.x, d.x);
      const minZ = Math.min(a.z, b.z, d.z), maxZ = Math.max(a.z, b.z, d.z);
      if (x < minX || x > maxX || z < minZ || z > maxZ) { continue; }
      const den = (b.z - d.z) * (a.x - d.x) + (d.x - b.x) * (a.z - d.z);
      if (Math.abs(den) < 1e-9) { continue; }
      const w0 = ((b.z - d.z) * (x - d.x) + (d.x - b.x) * (z - d.z)) / den;
      const w1 = ((d.z - a.z) * (x - d.x) + (a.x - d.x) * (z - d.z)) / den;
      const w2 = 1 - w0 - w1;
      if (w0 < -1e-6 || w1 < -1e-6 || w2 < -1e-6) { continue; }
      const y = w0 * a.y + w1 * b.y + w2 * d.y;
      if (y > maxY) { continue; }
      if (best === null || y > best) { best = y; }
    }
  }
  return best;
}
