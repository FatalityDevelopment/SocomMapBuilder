// Texture painting on the model: one brush stamp into a texture's pixels (top-down RGBA) round a point of the
// texture, with a soft falloff and wrapping. Modes: colour (tints toward the brush colour, keeping the texture's
// shading), dirt (darkens in uneven blotches), scuff (lightens with speckle, as worn edges do), and blur. Pure, so
// it is tested without WebGL; the viewport finds the point from a hit's uv.

export type TextureBrushMode = 'colour' | 'dirt' | 'scuff' | 'blur';

export interface Stamp {
  // The centre in pixels (x right, y down: the geometry's uv.y × height) and the radius in pixels.
  x: number;
  y: number;
  radius: number;
  mode: TextureBrushMode;
  colour: [number, number, number];
  strength: number;
  seed: number;
}

// Value noise in 0..1 on a lattice of `size` pixels (dirt blotches, scuff speckle).
function noise(x: number, y: number, size: number, seed: number): number {
  const fx = x / size, fy = y / size, x0 = Math.floor(fx), y0 = Math.floor(fy);
  let tx = fx - x0, ty = fy - y0;
  tx = tx * tx * (3 - 2 * tx); ty = ty * ty * (3 - 2 * ty);
  const h = (i: number, j: number) => {
    let n = Math.imul((x0 + i) | 0, 73856093) ^ Math.imul((y0 + j) | 0, 19349663) ^ Math.imul(seed | 0, 83492791);
    n = Math.imul(n ^ (n >>> 13), 0x5bd1e995);
    return ((n ^ (n >>> 15)) >>> 0) / 4294967295;
  };
  const a = h(0, 0) + (h(1, 0) - h(0, 0)) * tx, b = h(0, 1) + (h(1, 1) - h(0, 1)) * tx;
  return a + (b - a) * ty;
}

/** Applies one stamp to `rgba` (width × height, top-down) in place, wrapping at the edges. Answers the pixels changed. */
export function stamp(rgba: Uint8Array, width: number, height: number, s: Stamp): number {
  const r = Math.max(0.5, s.radius);
  const k = Math.max(0, Math.min(1, s.strength));
  const x0 = Math.floor(s.x - r), x1 = Math.ceil(s.x + r), y0 = Math.floor(s.y - r), y1 = Math.ceil(s.y + r);
  const wrap = (v: number, n: number) => ((v % n) + n) % n;
  const source = s.mode === 'blur' ? rgba.slice() : null;
  let changed = 0;
  // The tint keeps each pixel's brightness against the brush colour's.
  const colourLum = Math.max(1, 0.299 * s.colour[0] + 0.587 * s.colour[1] + 0.114 * s.colour[2]);
  for (let py = y0; py <= y1; py++) {
    for (let px = x0; px <= x1; px++) {
      const d = Math.hypot(px + 0.5 - s.x, py + 0.5 - s.y);
      if (d > r) { continue; }
      const t = 1 - d / r;
      const fall = t * t * (3 - 2 * t) * k;
      if (fall <= 0) { continue; }
      const x = wrap(px, width), y = wrap(py, height), i = (y * width + x) * 4;
      const old = [rgba[i], rgba[i + 1], rgba[i + 2]];
      let next: number[];
      if (s.mode === 'colour') {
        const lum = 0.299 * old[0] + 0.587 * old[1] + 0.114 * old[2];
        const shade = Math.min(2, lum / colourLum * (colourLum / 128));
        next = s.colour.map(c => c * Math.max(0.25, shade));
      } else if (s.mode === 'dirt') {
        const n = 0.6 * noise(x, y, 6, s.seed) + 0.4 * noise(x, y, 2, s.seed + 7);
        next = old.map(c => c * (1 - 0.55 * n));
      } else if (s.mode === 'scuff') {
        const n = noise(x, y, 1.5, s.seed + 13);
        next = old.map(c => c + (235 - c) * (n > 0.45 ? 0.6 : 0.15));
      } else {
        // A 3×3 box blur of the stroke's starting pixels.
        const acc = [0, 0, 0];
        for (let dy = -1; dy <= 1; dy++) {
          for (let dx = -1; dx <= 1; dx++) {
            const j = (wrap(y + dy, height) * width + wrap(x + dx, width)) * 4;
            for (let c = 0; c < 3; c++) { acc[c] += source![j + c]; }
          }
        }
        next = acc.map(v => v / 9);
      }
      let moved = false;
      for (let c = 0; c < 3; c++) {
        const v = Math.round(old[c] + (Math.max(0, Math.min(255, next[c])) - old[c]) * fall);
        if (v !== rgba[i + c]) { rgba[i + c] = v; moved = true; }
      }
      if (moved) { changed++; }
    }
  }
  return changed;
}

/** Texels per world unit on a triangle: how big a world-space brush is in its texture. */
export function texelsPerUnit(p: ArrayLike<number>[], uv: ArrayLike<number>[], width: number, height: number): number {
  const world = Math.hypot(...cross(sub(p[1], p[0]), sub(p[2], p[0]))) / 2;
  const tex = Math.abs((uv[1][0] - uv[0][0]) * width * (uv[2][1] - uv[0][1]) * height - (uv[2][0] - uv[0][0]) * width * (uv[1][1] - uv[0][1]) * height) / 2;
  return world > 1e-12 && tex > 1e-12 ? Math.sqrt(tex / world) : 0;
}

function sub(a: ArrayLike<number>, b: ArrayLike<number>): number[] { return [a[0] - b[0], a[1] - b[1], a[2] - b[2]]; }
function cross(a: number[], b: number[]): number[] { return [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]]; }
