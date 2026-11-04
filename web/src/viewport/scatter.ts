// The clutter brush's sampling: where one dab of the brush drops placements, kept apart from each other and from what
// is already there, and how a placement's rotation and size vary. Pure (a seeded random), so tests can pin it down.

export interface ScatterOptions {
  // Brush radius and the gap kept between placements (game units).
  radius: number;
  spacing: number;
  // Placements tried per dab; spacing and slope then thin them.
  density: number;
  // Steepest ground accepted (degrees from flat).
  maxSlope: number;
}

/** A small seeded random (mulberry32), 0..1. */
export function seededRandom(seed: number): () => number {
  let a = seed >>> 0;
  return () => {
    a = (a + 0x6D2B79F5) >>> 0;
    let t = a;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

/** `count` points spread evenly over the disc (uniform by area), as [x, z]. */
export function dabPoints(cx: number, cz: number, radius: number, count: number, random: () => number): Array<[number, number]> {
  const out: Array<[number, number]> = [];
  for (let i = 0; i < count; i++) {
    const r = radius * Math.sqrt(random()), a = random() * Math.PI * 2;
    out.push([cx + r * Math.cos(a), cz + r * Math.sin(a)]);
  }
  return out;
}

/** The candidates (in order) that keep `spacing` from every kept point and from `existing`, on the XZ plane. */
export function keepApart(candidates: Array<[number, number]>, existing: Array<[number, number]>, spacing: number): Array<[number, number]> {
  const s2 = spacing * spacing;
  const kept: Array<[number, number]> = [];
  const far = (p: [number, number], q: [number, number]) => (p[0] - q[0]) ** 2 + (p[1] - q[1]) ** 2 >= s2;
  for (const c of candidates) {
    if (existing.every(e => far(c, e)) && kept.every(k => far(c, k))) { kept.push(c); }
  }
  return kept;
}

/** Whether ground with this (unit or not) normal is flat enough for the brush. */
export function slopeOk(normal: { x: number; y: number; z: number }, maxSlopeDeg: number): boolean {
  const len = Math.hypot(normal.x, normal.y, normal.z);
  if (len < 1e-9) { return false; }
  return Math.acos(Math.min(1, Math.max(-1, normal.y / len))) * 180 / Math.PI <= maxSlopeDeg + 1e-6;
}

/** Indices of `points` within `radius` of the centre on the XZ plane (what the eraser takes). */
export function withinRadius(points: Array<[number, number]>, cx: number, cz: number, radius: number): number[] {
  const r2 = radius * radius;
  return points.flatMap((p, i) => (p[0] - cx) ** 2 + (p[1] - cz) ** 2 <= r2 ? [i] : []);
}
