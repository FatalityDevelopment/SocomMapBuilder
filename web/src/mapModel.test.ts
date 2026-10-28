import { describe, expect, it } from 'vitest';
import type { MapInstanceWire } from './webview/model-editor/asset-client';
import { layerOf, movable, placementOp, pointKindCounts, scaleOf, yawOf } from './mapModel';

// Column-major, translation in 12..14 (as GEO and clutter store it): yaw about Y, uniform scale.
function matrix(yawDeg: number, scale: number, x = 0, y = 0, z = 0): number[] {
  const r = yawDeg * Math.PI / 180, c = Math.cos(r) * scale, s = Math.sin(r) * scale;
  return [c, 0, -s, 0, 0, scale, 0, 0, s, 0, c, 0, x, y, z, 1];
}

function inst(fields: Partial<MapInstanceWire>): MapInstanceWire {
  return { kind: 'reference', model: 'CRATE', instance: 'crate01', path: '/scene/crate01', position: [0, 0, 0], scale: 1, matrix: matrix(0, 1), geoIndex: 4, clutterIndex: null, source: 'A_MDL.ZED:CRATE', ...fields };
}

describe('layerOf', () => {
  it('sorts instances into outliner layers', () => {
    expect(layerOf(inst({ kind: 'clutter', clutterIndex: 0, geoIndex: null }))).toBe('clutter');
    expect(layerOf(inst({ kind: 'model' }))).toBe('world');
    expect(layerOf(inst({ model: 'SKYDOME' }))).toBe('sky');
    expect(layerOf(inst({ model: 'theSun01' }))).toBe('sky');
    expect(layerOf(inst({}))).toBe('reference');
  });
});

describe('movable', () => {
  it('lets props and clutter move but not the world, sky or unplaced instances', () => {
    expect(movable(inst({}))).toBe(true);
    expect(movable(inst({ kind: 'clutter', clutterIndex: 3, geoIndex: null }))).toBe(true);
    expect(movable(inst({ kind: 'model' }))).toBe(false);
    expect(movable(inst({ model: 'SKY' }))).toBe(false);
    expect(movable(inst({ geoIndex: null }))).toBe(false);
    expect(movable(undefined)).toBe(false);
  });
});

describe('placementOp', () => {
  it('addresses clutter by clutter index and props by GEO node', () => {
    expect(placementOp(inst({ kind: 'clutter', clutterIndex: 7, geoIndex: null }), { yaw: 5 })).toEqual({ op: 'moveClutter', clutterIndex: 7, yaw: 5 });
    expect(placementOp(inst({ geoIndex: 12 }), { position: [1, 2, 3] })).toEqual({ op: 'moveInstance', geoIndex: 12, position: [1, 2, 3] });
    expect(placementOp(inst({ geoIndex: null }), {})).toBeNull();
  });
});

describe('yawOf / scaleOf', () => {
  it('reads back the yaw and scale a placement was built with', () => {
    for (const yaw of [0, 30, 90, -45, 179]) {
      for (const scale of [0.5, 1, 2.25]) {
        const m = matrix(yaw, scale, 10, 20, 30);
        expect(yawOf(m)).toBeCloseTo(yaw, 4);
        expect(scaleOf(m)).toBeCloseTo(scale, 6);
      }
    }
  });
});

describe('pointKindCounts', () => {
  it('counts points per kind', () => {
    const p = (kind: string) => ({ name: kind, team: 0, kind, x: 0, z: 0, cellX: 0, cellZ: 0 });
    expect(pointKindCounts([p('start'), p('waypoint'), p('start')])).toEqual([{ kind: 'start', count: 2 }, { kind: 'waypoint', count: 1 }]);
  });
});
