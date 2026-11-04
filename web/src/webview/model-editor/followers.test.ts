import { describe, expect, it } from 'vitest';
import { followers } from './editor-viewport';
import type { EditorChunk } from './mesh-data';

// One chunk of five vertices on the X axis: -4, -2, 0, 2, 4.
const chunk = { vertexCount: 5, positions: new Float32Array([-4, 0, 0, -2, 0, 0, 0, 0, 0, 2, 0, 0, 4, 0, 0]) } as unknown as EditorChunk;

describe('soft and mirrored moves', () => {
  it('takes the selection fully and its neighbours fading out to the radius', () => {
    const f = followers([chunk], new Map([[0, new Set([3])]]), 3, false, 10).get(0)!;
    expect(f.get(3)).toEqual({ w: 1, mirror: false });
    expect(f.get(4)!.w).toBeGreaterThan(0);
    expect(f.get(4)!.w).toBeLessThan(1);
    expect(f.get(2)!.w).toBeCloseTo(f.get(4)!.w);   // both 2 away
    expect(f.has(1)).toBe(false);                    // 5 away
  });

  it('adds the mirror image across X = 0, marked to move mirrored', () => {
    const f = followers([chunk], new Map([[0, new Set([4])]]), 0, true, 10).get(0)!;
    expect(f.get(0)).toEqual({ w: 1, mirror: true });
    expect([...f.keys()].sort()).toEqual([0, 4]);
    // A vertex on the mirror plane is its own image: it moves once.
    expect([...followers([chunk], new Map([[0, new Set([2])]]), 0, true, 10).get(0)!.keys()]).toEqual([2]);
  });
});
