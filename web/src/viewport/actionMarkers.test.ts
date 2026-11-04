import { describe, expect, it } from 'vitest';
import { actionMarkers, markerAnchor } from './actionMarkers';
import { actionKind } from '../shared/actionIcons';
import { checkAction, cleanFields, defaultsFor, groupRows } from '../components/mapActionsModel';

// A door node with its frame and leaf drawn below it, a switch drawn by its own node, and a row naming nothing.
const targets = [
  { index: 10, name: 'door01', path: '/door01' },
  { index: 11, name: 'frame', path: '/door01/frame' },
  { index: 12, name: 'leaf', path: '/door01/leaf' },
  { index: 20, name: 'lever', path: '/lever' },
  { index: 30, name: 'crate', path: '/crate' },
];
const rows: Array<Record<string, Array<string | number>>> = [
  { node: ['door01'], type: ['DOOR'], valve: ['door01'], anim: ['singleDoor_left', 'kick_Door'], range: [30] },
  { node: ['LEVER'], type: ['SWITCH'] },
  { node: ['ghost'], type: ['ITEM'] },
  { node: ['door01'], anim: ['extra'] },
];
const instances = [{ geoIndex: 11 }, { geoIndex: 12 }, { geoIndex: 20 }, { geoIndex: 30 }, { geoIndex: null }];

describe('action markers', () => {
  it('puts one marker on each bound node that draws, covering the placements under it', () => {
    const markers = actionMarkers({ rows, targets }, instances);
    expect(markers).toHaveLength(2);
    const door = markers.find(m => m.node === 'door01')!;
    expect(door).toMatchObject({ geoIndex: 10, kind: 'DOOR', rows: [0, 3], instances: [0, 1] });
    // Names bind case-insensitively when nothing matches exactly.
    expect(markers.find(m => m.node === 'lever')).toMatchObject({ geoIndex: 20, kind: 'SWITCH', instances: [2] });
  });

  it('stands a bare node (no drawn prop) at its own anchor', () => {
    const bare = actionMarkers({ rows: [{ node: ['breach1'], type: ['ACT_BREACH'] }], targets: [{ index: 40, name: 'breach1', path: '/breach1', anchor: [1, 2, 3] }] }, instances);
    expect(bare).toEqual([{ geoIndex: 40, node: 'breach1', kind: 'ACT_BREACH', rows: [0], instances: [], anchor: [1, 2, 3] }]);
    expect(actionMarkers({ rows: [{ node: ['x'] }], targets: [{ index: 41, name: 'x', path: '/x', anchor: null }] }, instances)).toEqual([]);
  });

  it('stands over the top middle of the visible boxes, and nowhere when none are', () => {
    expect(markerAnchor([{ min: [0, 0, 0], max: [2, 4, 2] }, { min: [2, 1, 0], max: [4, 6, 2] }], 1)).toEqual([2, 7, 1]);
    expect(markerAnchor([], 1)).toBeNull();
  });

  it('names the kind from the type, with animation-only and unknown rows', () => {
    expect(actionKind({ type: ['door'] })).toBe('DOOR');
    expect(actionKind({ anim: ['x'] })).toBe('ANIM');
    expect(actionKind({ type: ['WHATEVER'] })).toBe('OTHER');
  });
});

describe('actions panel rules', () => {
  const data = { icons: ['action_door_open.tif', 'action_MP_Bomb.tif'], animations: ['singleDoor_left', 'kick_Door'], valves: ['door01', 'POBJ1'], rows };

  it('starts a new door like the map’s other doors', () => {
    expect(defaultsFor('DOOR', data, 'door09')).toEqual({ type: ['DOOR'], range: [30], bitmap: ['action_door_open.tif'], anim: ['singleDoor_left', 'kick_Door'], valve: ['door09'] });
    expect(defaultsFor('SATCHEL', data, 'charge1')).toEqual({ type: ['SATCHEL'], range: [30], bitmap: ['action_MP_Bomb.tif'], valve: ['charge1'] });
    expect(defaultsFor('ANIM', data, 'x')).toEqual({});
  });

  it('flags bad numbers and empty actions, and warns about names the map lacks', () => {
    const bad = checkAction({ type: ['DOOR'], range: ['2.5'], team: ['x'] }, data);
    expect(Object.keys(bad.errors).sort()).toEqual(['range', 'team']);
    expect(checkAction({ range: ['30'] }, data).errors.type).toBeTruthy();
    const warn = checkAction({ type: ['DOOR'], valve: ['newValve'], anim: ['singleDoor_left', 'nope'], bitmap: ['missing.tif'] }, data);
    expect(warn.errors).toEqual({});
    expect(warn.warnings.valve).toContain('newValve');
    expect(warn.warnings.anim).toContain('nope');
    expect(warn.warnings.bitmap).toContain('missing.tif');
    expect(checkAction({ type: ['DOOR'], valve: ['door01'], range: ['0'] }, data).errors.range).toBeTruthy();
  });

  it('cleans fields into the mapAction op shape', () => {
    expect(cleanFields({ node: ['old'], type: ['DOOR'], range: ['30'], elevation: [''], anim: [' a ', '', 'b'], one_shot: [], valve: ['  '] }, 'door02'))
      .toEqual({ node: ['door02'], type: ['DOOR'], range: [30], anim: ['a', 'b'], one_shot: [] });
  });

  it('groups the map’s rows by kind in a fixed order', () => {
    expect(groupRows(rows)).toEqual([
      { kind: 'DOOR', rows: [{ row: 0, node: 'door01' }] },
      { kind: 'SWITCH', rows: [{ row: 1, node: 'LEVER' }] },
      { kind: 'ITEM', rows: [{ row: 2, node: 'ghost' }] },
      { kind: 'ANIM', rows: [{ row: 3, node: 'door01' }] },
    ]);
  });
});
