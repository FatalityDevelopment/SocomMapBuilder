import { describe, expect, it } from 'vitest';
import { buildCollisionObject, outline, triangulate, type CollisionFaceWire } from './collisionLayer';

const face = (points: number[], up: boolean): CollisionFaceWire => ({ geoIndex: 1, node: 'crate_1', kind: 'prop', material: 7, up, points });
// A floor quad at y 2 and a triangular wall.
const quad = face([0, 2, 0, 1, 2, 0, 1, 2, 1, 0, 2, 1], true);
const tri = face([0, 0, 0, 0, 2, 0, 1, 0, 0], false);

describe('collision layer', () => {
  it('fans each polygon into triangles', () => {
    const t = triangulate([quad, tri]);
    expect(t.length).toBe((2 + 1) * 9);
    expect(Array.from(t.slice(0, 9))).toEqual([0, 2, 0, 1, 2, 0, 1, 2, 1]);
    expect(Array.from(t.slice(9, 18))).toEqual([0, 2, 0, 1, 2, 1, 0, 2, 1]);
  });

  it('outlines every edge, closing each polygon', () => {
    const o = outline([quad]);
    expect(o.length).toBe(4 * 6);
    expect(Array.from(o.slice(18, 24))).toEqual([0, 2, 1, 0, 2, 0]);
  });

  it('draws floors and walls apart, and nothing for no faces', () => {
    expect(buildCollisionObject([]).children).toHaveLength(0);
    expect(buildCollisionObject([quad, tri]).children).toHaveLength(3);
    expect(buildCollisionObject([quad]).children).toHaveLength(2);
  });
});
