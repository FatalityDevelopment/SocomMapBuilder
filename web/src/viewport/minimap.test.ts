import { describe, expect, it } from 'vitest';
import { actionBlipsFrom, headingOf, minimapBlips, navIcon, pinToRim, squareAround, stepRange, toRadar, worldToImage } from './minimap';

describe('minimap radar', () => {
  it('measures the heading clockwise from north (-Z)', () => {
    expect(headingOf(0, -1)).toBeCloseTo(0);
    expect(headingOf(1, 0)).toBeCloseTo(Math.PI / 2);
    expect(Math.abs(headingOf(0, 1))).toBeCloseTo(Math.PI);
    expect(headingOf(-1, 0)).toBeCloseTo(-Math.PI / 2);
  });

  it('puts what the view faces at the top of the radar', () => {
    // Facing north: a point north is straight up, a point east is to the right.
    expect(toRadar(0, -100, 0, 0, 0, 100, 50)).toEqual([0, -50]);
    const east = toRadar(100, 0, 0, 0, 0, 100, 50);
    expect(east[0]).toBeCloseTo(50); expect(east[1]).toBeCloseTo(0);
    // Facing east: the point east is now up, the point north to the left.
    const ahead = toRadar(100, 0, 0, 0, Math.PI / 2, 100, 50);
    expect(ahead[0]).toBeCloseTo(0); expect(ahead[1]).toBeCloseTo(-50);
    const left = toRadar(0, -100, 0, 0, Math.PI / 2, 100, 50);
    expect(left[0]).toBeCloseTo(-50); expect(left[1]).toBeCloseTo(0);
  });

  it('pins far points to the rim and leaves near ones', () => {
    expect(pinToRim(10, 0, 50, 5)).toBeNull();
    expect(pinToRim(0, -200, 50, 5)).toEqual([0, -45]);
  });

  it('steps the range and keeps it inside the steps', () => {
    expect(stepRange(1200, true)).toBe(800);
    expect(stepRange(1200, false)).toBe(1800);
    expect(stepRange(300, true)).toBe(300);
    expect(stepRange(99999, false)).toBe(4000);
  });

  it('places world points on an image and squares a box', () => {
    // M51's tactical map: the SEALs' insertion point lands on the map's "Insertion" X (150, 68 of 1024 x 512).
    const [u, v] = worldToImage(948, 828, { minX: -78, maxX: 6926, minZ: 270, maxZ: 4467 }, 1024, 512);
    expect(Math.abs(u - 150)).toBeLessThan(6);
    expect(Math.abs(v - 68)).toBeLessThan(6);
    const sq = squareAround(0, 100, 0, 50, 0);
    expect(sq).toEqual({ minX: 0, maxX: 100, minZ: -25, maxZ: 75 });
  });
});

describe('minimap blips', () => {
  it('names nav blips by letter: A and B are the fireteams, C..Z are ret_nav_01..24', () => {
    expect(navIcon('Able')).toBe('ret_able');
    expect(navIcon('bravo2')).toBe('ret_bravo');
    expect(navIcon('Charlie')).toBe('ret_nav_01');
    expect(navIcon('Zulu')).toBe('ret_nav_24');
    expect(navIcon('7up')).toBeNull();
  });

  it('draws actors at their start points, spare starts, nav points, objectives and actions', () => {
    const points = [
      { name: 'sealstart', team: 8, kind: 'start', x: 948, z: 828 },
      { name: 'Able2', team: 8, kind: 'start', x: 5388, z: 2898 },
      { name: 'Bunker', team: 8, kind: 'start', x: 4048, z: 3008 },
      { name: 'Spare', team: 1, kind: 'start', x: 10, z: 20 },
      { name: 'Charlie', team: 8, kind: 'waypoint', x: 5448, z: 2478 },
      { name: 'Road5d', team: 8, kind: 'other', x: 1, z: 2 },
    ];
    const blips = minimapBlips({
      points,
      actors: [
        { name: 'Spectre', character: 'seal_a', starts: [{ point: 'SEALSTART' }] },
        { name: 'Jester', character: 'seal_b', starts: [{ point: 'Able2' }] },
        { name: null, character: 'alb_terr1', starts: [{ point: 'Bunker' }] },
        { name: 'Nowhere', character: 'alb_terr1', starts: [{ point: 'missing' }] },
      ],
      objectives: [
        { row: 0, name: '_1_Meet', location: [2028, 3328], secondary: false },
        { row: 1, name: '_2_Unseen', location: [2028, 3328], secondary: true },
        { row: 2, name: '_3_Extract', location: [5748, 1808], secondary: false },
        { row: 3, name: null, location: null, secondary: false },
      ],
      texts: { _1_Meet: 'MEET MALLARD', _3_Extract: 'MOVE TO EXTRACTION' },
      actions: [{ kind: 'DOOR', node: 'door01', x: 5, z: 6 }],
    });
    const by = (label: string) => blips.find(b => b.label === label);
    expect(by('Spectre')).toMatchObject({ kind: 'friendly', icon: 'ret_friendly', x: 948 });
    expect(by('Jester')).toMatchObject({ kind: 'friendly', icon: 'ret_able' });
    expect(by('alb_terr1')).toMatchObject({ kind: 'enemy', icon: 'ret_threat', x: 4048 });
    expect(by('Nowhere')).toBeUndefined();
    expect(by('Spare')).toMatchObject({ kind: 'spawn', icon: 'ret_base' });
    expect(by('Charlie')).toMatchObject({ kind: 'nav', icon: 'ret_nav_01', pinToRim: true });
    expect(by('Road5d')).toBeUndefined();
    // Two objectives at one place make one blip.
    expect(blips.filter(b => b.kind === 'objective')).toHaveLength(1);
    expect(by('MEET MALLARD')).toMatchObject({ icon: 'ret_objective' });
    expect(by('MOVE TO EXTRACTION')).toMatchObject({ kind: 'extraction', icon: 'ret_extraction' });
    expect(by('Door: door01')).toMatchObject({ kind: 'action', icon: null });
  });

  it('places action markers at their anchor or the middle of their props', () => {
    const out = actionBlipsFrom([
      { kind: 'DOOR', node: 'a', instances: [], anchor: [1, 2, 3] },
      { kind: 'SWITCH', node: 'b', instances: [0, 1] },
      { kind: 'ITEM', node: 'c', instances: [9] },
    ], [{ position: [0, 0, 0] }, { position: [10, 5, 20] }]);
    expect(out).toEqual([{ kind: 'DOOR', node: 'a', x: 1, z: 3 }, { kind: 'SWITCH', node: 'b', x: 5, z: 10 }]);
  });
});
