import { describe, expect, it } from 'vitest';
import type { MapInstanceWire } from './webview/model-editor/asset-client';
import { addedClutter, deleteOps, duplicateOps, groupInstancesByLayer, isSkyChunk, layerOf, missionLabel, movable, pickDuplicates, placementOp, pointColor, pointKindCounts, problemTarget, scaleOf, sortProblems, yawOf } from './mapModel';

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
    expect(layerOf(inst({ kind: 'model', model: 'SKYDOME' }))).toBe('sky');
    expect(layerOf(inst({}))).toBe('reference');
  });

  it('knows the retail sky models by their names, but not skylines or skylights', () => {
    for (const model of ['sky', 'sky_m72', 'mp62_sky', 'mp72_sky', 'alaska4d_sky', 'sky_follow', 'sun']) {
      expect(layerOf(inst({ model }))).toBe('sky');
    }
    for (const model of ['obj_skylight', 'far_skyline', 'sunroof', 'crate']) {
      expect(layerOf(inst({ model }))).toBe('reference');
    }
  });
});

describe('isSkyChunk', () => {
  it('finds the sky drawn inside world models by node or texture', () => {
    expect(isSkyChunk({ node: 'skydome', texture: 'sky01c.tif' })).toBe(true);
    expect(isSkyChunk({ node: 'whispy_scroller', texture: 'cloud_whispy.tif' })).toBe(true);
    expect(isSkyChunk({ node: 'g3', texture: 'gen_cloud_n_sun_256x256.tif' })).toBe(true);
    expect(isSkyChunk({ node: 'ocean_3', texture: 'cuba1a_sky01.tif' })).toBe(true);
    expect(isSkyChunk({ node: 'g21801_3', texture: 'far_skyline.tif' })).toBe(false);
    expect(isSkyChunk({ node: 'm52', texture: 'concrete01.tif' })).toBe(false);
    expect(isSkyChunk({ node: null, texture: null })).toBe(false);
  });

  it('includes standalone celestial cards without hiding light fixtures or signs', () => {
    for (const node of ['moon', 'star1', 'star1_4', 'stars']) {
      expect(isSkyChunk({ node, texture: null })).toBe(true);
    }
    expect(isSkyChunk({ node: 'flare', texture: 'sunglow.tif' })).toBe(true);
    for (const texture of ['gaslight_flare.tif', 'sign_northstar.tif', 'tex_redstar.tif', 'moonreflect.tif']) {
      expect(isSkyChunk({ node: 'scenery', texture })).toBe(false);
    }
    expect(isSkyChunk({ node: 'streetlight', texture: 'lanternglass.tif' })).toBe(false);
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
  it('moves a CA world node by its unique instance ID', () => {
    const node = inst({ kind: 'model', caNode: true, geoIndex: null, instance: 'COMPOUND/abcdef01~1' });
    expect(movable(node)).toBe(true);
    expect(placementOp(node, { position: [1, 2, 3] })).toEqual({ op: 'moveInstance', instance: node.instance, position: [1, 2, 3] });
  });
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


describe('render batches', () => {
  it('keeps prop and clutter instances separate even when they share a model', () => {
    const instances = [inst({}), inst({ kind: 'clutter', clutterIndex: 0, geoIndex: null }), inst({ geoIndex: 5 })];
    expect(groupInstancesByLayer(instances)).toEqual([
      { source: 'A_MDL.ZED:CRATE', layer: 'reference', indices: [0, 2] },
      { source: 'A_MDL.ZED:CRATE', layer: 'clutter', indices: [1] },
    ]);
  });
  it('keeps archive identities separate and skips unresolved models', () => {
    expect(groupInstancesByLayer([inst({}), inst({ source: 'B_MDL.ZED:CRATE' }), inst({ source: null })]))
      .toEqual([
        { source: 'A_MDL.ZED:CRATE', layer: 'reference', indices: [0] },
        { source: 'B_MDL.ZED:CRATE', layer: 'reference', indices: [1] },
      ]);
  });
});

describe('selection edits', () => {
  const geo = (geoIndex: number, path: string) => inst({ geoIndex, path });
  const clutter = (clutterIndex: number) => inst({ kind: 'clutter', model: 'TREE', clutterIndex, geoIndex: null, path: '' });

  it('deletes from the highest index down, GEO before clutter', () => {
    expect(deleteOps([geo(4, '/a'), clutter(1), geo(9, '/b'), clutter(5)])).toEqual([
      { op: 'deleteInstance', geoIndex: 9 }, { op: 'deleteInstance', geoIndex: 4 },
      { op: 'deleteClutter', clutterIndex: 5 }, { op: 'deleteClutter', clutterIndex: 1 },
    ]);
  });

  it('duplicates GEO from the highest index down and copies clutter by model and matrix', () => {
    const c = clutter(2);
    expect(duplicateOps([geo(3, '/a'), c, geo(7, '/b')])).toEqual([
      { op: 'duplicateInstance', geoIndex: 7 }, { op: 'duplicateInstance', geoIndex: 3 },
      { op: 'addClutter', model: 'TREE', matrix: c.matrix },
    ]);
  });

  it('reads the new clutter indices from one edit or a batch', () => {
    expect([...addedClutter({ newClutterIndex: 4 })]).toEqual([4]);
    expect([...addedClutter({ results: [{ newClutterIndex: 1 }, {}, null, { newClutterIndex: -1 }, { newClutterIndex: 6 }] })]).toEqual([1, 6]);
  });

  it('picks the copies after a duplicate', () => {
    const before = [geo(1, '/a'), geo(2, '/b'), clutter(0)];
    // The copy of /a lands right after it with the same path; clutter #1 is new.
    const after = [geo(1, '/a'), geo(2, '/a'), geo(3, '/b'), clutter(0), clutter(1)];
    expect(pickDuplicates(before, after, new Set([1]))).toEqual([1, 4]);
  });
});

describe('map problems', () => {
  const list = [inst({ geoIndex: 7 }), inst({ kind: 'clutter', clutterIndex: 7, geoIndex: null }), inst({ geoIndex: 9 })];

  it('finds what a problem is about', () => {
    expect(problemTarget({ severity: 'error', code: 'x', message: '', geoIndex: 9 }, list)).toEqual({ instance: 2 });
    // A clutter index never matches a GEO node with the same number.
    expect(problemTarget({ severity: 'error', code: 'x', message: '', clutterIndex: 7 }, list)).toEqual({ instance: 1 });
    expect(problemTarget({ severity: 'error', code: 'x', message: '', point: 'Alpha1' }, list)).toEqual({ point: 'Alpha1' });
    expect(problemTarget({ severity: 'warning', code: 'x', message: '', geoIndex: 42 }, list)).toBeNull();
    expect(problemTarget({ severity: 'warning', code: 'x', message: '' }, list)).toBeNull();
  });

  it('lists errors first and keeps the order within a severity', () => {
    const p = (severity: 'error' | 'warning' | 'info', message: string) => ({ severity, code: 'x', message });
    expect(sortProblems([p('info', 'a'), p('error', 'b'), p('warning', 'c'), p('error', 'd')]).map(x => x.message)).toEqual(['b', 'd', 'c', 'a']);
  });
});

describe('SOCOM 3 / CA missions', () => {
  it('labels a mission by kind and mode', () => {
    expect(missionLabel({ id: 'MP414CTR', kind: 'MP', mode: 'CTR' })).toBe('MP414CTR · online CTR');
    expect(missionLabel({ id: 'SP414', kind: 'SP', mode: '' })).toBe('SP414 · campaign');
    expect(missionLabel({ id: 'DEFAULT', kind: 'other', mode: '' })).toBe('DEFAULT');
  });

  it('gives map markers their own colour', () => {
    expect(pointColor('marker')).not.toBe(pointColor('other'));
  });
});
