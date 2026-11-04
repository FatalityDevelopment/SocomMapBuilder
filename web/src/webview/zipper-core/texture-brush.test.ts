import { describe, expect, it } from 'vitest';
import { stamp, texelsPerUnit, type Stamp } from './texture-brush';

const grey = (w: number, h: number) => new Uint8Array(w * h * 4).fill(128);
const px = (rgba: Uint8Array, w: number, x: number, y: number) => Array.from(rgba.slice((y * w + x) * 4, (y * w + x) * 4 + 4));
const base: Stamp = { x: 8, y: 8, radius: 4, mode: 'colour', colour: [200, 40, 40], strength: 1, seed: 1 };

describe('texture brush', () => {
  it('tints inside the radius, strongest at the centre, and leaves the rest and alpha alone', () => {
    const img = grey(16, 16);
    expect(stamp(img, 16, 16, base)).toBeGreaterThan(20);
    const centre = px(img, 16, 8, 8), edge = px(img, 16, 11, 8), out = px(img, 16, 14, 8);
    expect(centre[0]).toBeGreaterThan(edge[0]);
    expect(edge[0]).toBeGreaterThan(128);
    expect(out).toEqual([128, 128, 128, 128]);
    expect(centre[3]).toBe(128);
  });

  it('wraps across the texture edge', () => {
    const img = grey(16, 16);
    stamp(img, 16, 16, { ...base, x: 0.5, y: 8 });
    expect(px(img, 16, 15, 8)[0]).toBeGreaterThan(128);
  });

  it('dirt only darkens and scuff only lightens', () => {
    const dirt = grey(16, 16), scuff = grey(16, 16);
    stamp(dirt, 16, 16, { ...base, mode: 'dirt' });
    stamp(scuff, 16, 16, { ...base, mode: 'scuff' });
    expect(Array.from(dirt).every((v, i) => i % 4 === 3 || v <= 128)).toBe(true);
    expect(Array.from(dirt).some(v => v < 128)).toBe(true);
    expect(Array.from(scuff).every((v, i) => i % 4 === 3 || v >= 128)).toBe(true);
    expect(Array.from(scuff).some(v => v > 128)).toBe(true);
  });

  it('blur evens out a hard edge', () => {
    const img = grey(16, 16);
    for (let y = 0; y < 16; y++) { for (let x = 8; x < 16; x++) { img.fill(255, (y * 16 + x) * 4, (y * 16 + x) * 4 + 3); } }
    stamp(img, 16, 16, { ...base, mode: 'blur' });
    const left = px(img, 16, 7, 8)[0], right = px(img, 16, 8, 8)[0];
    expect(left).toBeGreaterThan(128);
    expect(right).toBeLessThan(255);
  });

  it('measures how many texels a world unit covers', () => {
    // A unit right triangle mapped onto a quarter of a 64×64 texture: 32 texels per unit.
    expect(texelsPerUnit([[0, 0, 0], [1, 0, 0], [0, 1, 0]], [[0, 0], [0.5, 0], [0, 0.5]], 64, 64)).toBeCloseTo(32);
  });
});
