import * as THREE from 'three';
import { describe, expect, it } from 'vitest';
import { dabPoints, keepApart, seededRandom, slopeOk, withinRadius } from './scatter';
import { alignOffsets, applyDrag, distributeOffsets } from './arrange';
import { analyzeScene, type Measured } from './mapAnalysis';

describe('scatter brush', () => {
  it('drops points inside the brush, the same ones for the same seed', () => {
    const a = dabPoints(10, -5, 3, 200, seededRandom(4));
    expect(a).toHaveLength(200);
    for (const [x, z] of a) { expect(Math.hypot(x - 10, z + 5)).toBeLessThanOrEqual(3 + 1e-9); }
    expect(dabPoints(10, -5, 3, 200, seededRandom(4))).toEqual(a);
    // Uniform by area: about a quarter land inside half the radius.
    const inner = a.filter(([x, z]) => Math.hypot(x - 10, z + 5) < 1.5).length;
    expect(inner).toBeGreaterThan(30);
    expect(inner).toBeLessThan(75);
  });

  it('keeps placements apart from each other and from what is there', () => {
    const kept = keepApart([[0, 0], [0.5, 0], [2, 0], [5, 5]], [[5, 5.5]], 1);
    expect(kept).toEqual([[0, 0], [2, 0]]);
  });

  it('refuses steep ground and erases within the radius', () => {
    expect(slopeOk({ x: 0, y: 1, z: 0 }, 30)).toBe(true);
    expect(slopeOk({ x: 1, y: 1, z: 0 }, 30)).toBe(false);    // 45°
    expect(slopeOk({ x: 1, y: 1, z: 0 }, 45)).toBe(true);
    expect(withinRadius([[0, 0], [3, 0], [1, 1]], 0, 0, 1.5)).toEqual([0, 2]);
  });
});

describe('align and distribute', () => {
  const boxes = [{ min: [0, 0, 0], max: [2, 1, 1] }, { min: [5, 3, 0], max: [6, 4, 1] }, { min: [9, 1, 0], max: [13, 2, 1] }];

  it('lines boxes up on their minimum, centre or maximum', () => {
    expect(alignOffsets(boxes, 1, 'min')).toEqual([0, -3, -1]);
    expect(alignOffsets(boxes, 1, 'max')).toEqual([3, 0, 2]);
    expect(alignOffsets(boxes, 0, 'center')).toEqual([5.5, 1, -4.5]);
  });

  it('spaces centres evenly between the outer two, which stay', () => {
    // Centres 1, 5.5, 11: the middle goes to 6.
    expect(distributeOffsets(boxes, 0)).toEqual([0, 0.5, 0]);
    expect(distributeOffsets(boxes.slice(0, 2), 0)).toEqual([0, 0]);
  });
});

describe('pivot modes', () => {
  const at = (x: number, z: number) => new THREE.Matrix4().makeTranslation(x, 0, z);
  const pos = (m: THREE.Matrix4) => new THREE.Vector3().setFromMatrixPosition(m).toArray().map(v => +v.toFixed(5));
  const centre = new THREE.Vector3(5, 0, 0);
  // A quarter turn about Y through the centre.
  const turn = new THREE.Matrix4().makeTranslation(5, 0, 0).multiply(new THREE.Matrix4().makeRotationY(Math.PI / 2)).multiply(new THREE.Matrix4().makeTranslation(-5, 0, 0));

  it('turns everything about the centre, or each item where it stands', () => {
    const items = [at(0, 0), at(10, 0)];
    expect(applyDrag(turn, items, 'center', centre).map(pos)).toEqual([[5, 0, 5], [5, 0, -5]]);
    const own = applyDrag(turn, items, 'individual', centre);
    expect(own.map(pos)).toEqual([[0, 0, 0], [10, 0, 0]]);
    // ...but each is turned.
    expect(new THREE.Vector3(1, 0, 0).transformDirection(own[1]).toArray().map(v => +v.toFixed(5))).toEqual([0, 0, -1]);
    expect(applyDrag(turn, items, 'active', centre).map(pos)).toEqual([[0, 0, 0], [0, 0, -10]]);
  });

  it('keeps the move part of a drag in every mode', () => {
    const move = new THREE.Matrix4().makeTranslation(1, 2, 3);
    for (const mode of ['center', 'individual', 'active'] as const) {
      expect(applyDrag(move, [at(0, 0)], mode, centre).map(pos)).toEqual([[1, 2, 3]]);
    }
  });
});

describe('map analysis', () => {
  const item = (over: Partial<Measured>): Measured => ({ model: 'crate', instance: 'c', geoIndex: 1, clutterIndex: null, position: [0, 0, 0], gap: 0, triangles: 10, ...over });

  it('flags floating props and stacked copies, pointing at them', () => {
    const problems = analyzeScene([
      item({ instance: 'a', gap: 3, geoIndex: 4 }),
      item({ instance: 'b', position: [100, 0, 0], clutterIndex: 7, geoIndex: null }),
      item({ instance: 'c', position: [100.01, 0, 0], clutterIndex: 8, geoIndex: null }),
      item({ instance: 'd', model: 'tree', position: [100.01, 0, 0] }),
    ]);
    expect(problems.map(p => p.code)).toEqual(['floating', 'stacked']);
    expect(problems[0].geoIndex).toBe(4);
    expect(problems[1].clutterIndex).toBe(8);
  });

  it('flags an area drawing far more than the rest', () => {
    const spread = [0, 1, 2, 3, 4].map(k => item({ position: [k * 1000, 0, 0], triangles: 100 }));
    const heavy = [0, 1, 2].map(k => item({ position: [5000 + k * 5, 0, 0], triangles: 6000 }));
    expect(analyzeScene([...spread, ...heavy]).filter(p => p.code === 'dense-area')).toHaveLength(1);
    // Many times the average but light in itself: not reported.
    const light = [0, 1, 2].map(k => item({ position: [5000 + k * 5, 0, 0], triangles: 1000 }));
    expect(analyzeScene([...spread, ...light]).filter(p => p.code === 'dense-area')).toHaveLength(0);
  });
});
