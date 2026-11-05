import { describe, expect, it } from 'vitest';
import { compassOf, pickClip, planFigures, weaponForValve, weaponLabel, yawOf } from './actorFigures';

const at = (...names: string[]) => names.map((name, i) => ({ name, x: i * 1000, z: 0 }));

const weapons = ['WEAP_MDL.ZED:mp5sd', 'WEAP_MDL.ZED:m4Acarbine', 'WEAP_MDL.ZED:m4Acarbine_sd', 'WEAP_MDL.ZED:ak47', 'WEAP_MDL.ZED:remington870'];

describe('actor figures', () => {
  it('finds the weapon model an armory valve stands for', () => {
    expect(weaponForValve('Enable_mp5sd', weapons)).toBe('WEAP_MDL.ZED:mp5sd');
    expect(weaponForValve('Enable_M4A1_SD', weapons)).toBe('WEAP_MDL.ZED:m4Acarbine_sd');
    expect(weaponForValve('Enable_870', weapons)).toBe('WEAP_MDL.ZED:remington870');
    expect(weaponForValve('Enable_uzi', weapons)).toBeNull();
  });

  it('picks the first clip the package has', () => {
    expect(pickClip(['seal_stand', 'x'], ['X', 'guard'])).toBe('X');
    expect(pickClip(['a'], [])).toBeNull();
  });

  it('without loadouts: stands each actor at its first free start point, SEALs with the armory in turn', () => {
    const actors = [
      { name: 'player', character: 'jungle_A51', model: 'CLIB_MDL.ZED:MESH_seal_A_woodland_LO', starts: [{ point: 'sealstart' }] },
      { name: 'able2', character: 'jungle_B51', model: 'CLIB_MDL.ZED:MESH_seal_B_woodland_LO', starts: [{ point: 'Able2' }] },
      { name: 'bravo1', character: 'jungle_C51', model: 'CLIB_MDL.ZED:MESH_seal_A_woodland_LO', starts: [{ point: 'Bravo1' }] },
      { name: 'Road1', character: 'm51_terror01', model: 'CLIB_MDL.ZED:MESH_alban_ter01', starts: [{ point: 'road1' }, { point: 'Road1b' }] },
      { name: 'Road1again', character: 'm51_terror01', model: 'CLIB_MDL.ZED:MESH_alban_ter01', starts: [{ point: 'Road1' }, { point: 'Road1b' }] },
      { name: 'ghost', character: 'm51_terror02', model: null, starts: [{ point: 'Ghost' }] },
      { name: 'lost', character: 'm51_terror02', model: 'CLIB_MDL.ZED:MESH_alban_ter02', starts: [{ point: 'nowhere' }] },
    ];
    const plans = planFigures(actors, at('SealStart', 'Able2', 'Bravo1', 'Road1', 'Road1b', 'Ghost'), {
      clips: ['seal_stand', 'guard_stand02'], armory: ['WEAP_MDL.ZED:mp5sd', 'WEAP_MDL.ZED:m4Acarbine_sd'], chosen: { m51_terror01: 'WEAP_MDL.ZED:ak47' } });
    expect(plans.map(p => [p.actor, p.point, p.clip, p.weapon, p.weaponSource])).toEqual([
      ['player', 'SealStart', 'seal_stand', 'WEAP_MDL.ZED:mp5sd', 'stand-in'],
      ['able2', 'Able2', 'seal_stand', 'WEAP_MDL.ZED:m4Acarbine_sd', 'stand-in'],
      ['bravo1', 'Bravo1', 'seal_stand', 'WEAP_MDL.ZED:mp5sd', 'stand-in'],
      ['Road1', 'Road1', 'seal_stand', 'WEAP_MDL.ZED:ak47', 'picked'],
      ['Road1again', 'Road1b', 'seal_stand', 'WEAP_MDL.ZED:ak47', 'picked'],
    ]);
  });

  it('holds each chartype\'s own weapon unless the user picked another', () => {
    const uzi = 'WEAP_MDL.ZED:uzi', sd = 'WEAP_MDL.ZED:m4Acarbine_sd';
    const terror = [{ name: 'M67', model: 'WEAP_MDL.ZED:grenade' }, { name: '9mm Sub', model: uzi }, { name: 'Model 18', model: 'WEAP_MDL.ZED:glock18' }];
    const actors = [
      { name: 'player', character: 'jungle_A51', model: 'CLIB_MDL.ZED:seal', starts: [{ point: 'a' }], weapons: [{ name: 'M4A1 SD', model: sd }], weapon: sd },
      { name: 'Road1', character: 'm51_terror01', model: 'CLIB_MDL.ZED:ter', starts: [{ point: 'b' }], weapons: terror, weapon: uzi },
      { name: 'civ', character: 'civ', model: 'CLIB_MDL.ZED:civ', starts: [{ point: 'c' }], weapons: [], weapon: null },
      { name: 'Road2', character: 'm51_terror02', model: 'CLIB_MDL.ZED:ter', starts: [{ point: 'd' }], weapons: terror, weapon: uzi },
    ];
    const plans = planFigures(actors, at('a', 'b', 'c', 'd'), { clips: [], armory: ['WEAP_MDL.ZED:mp5sd'], chosen: { m51_terror02: 'none', civ: 'WEAP_MDL.ZED:ak47' } });
    expect(plans.map(p => [p.actor, p.weapon, p.weaponSource])).toEqual([
      ['player', sd, 'loadout'], ['Road1', uzi, 'loadout'], ['civ', 'WEAP_MDL.ZED:ak47', 'picked'], ['Road2', null, 'none'],
    ]);
    expect(weaponLabel(uzi, actors[1])).toBe('9mm Sub');
    expect(weaponLabel('WEAP_MDL.ZED:ak47', actors[2])).toBe('ak47');
    expect(weaponLabel(null)).toBe('nothing');
  });

  it('turns each figure to its start heading, stands it in its animation set clip and keeps the squad together', () => {
    const seal = (name: string, point: string, value: number) =>
      ({ name, character: `jungle_${name}`, model: 'CLIB_MDL.ZED:seal', teams: [1, 2, 31], starts: [{ point, value }], weapons: [], weapon: null, stand: 'seal_stand' });
    const actors = [
      seal('player', 'sealstart', 4), seal('able2', 'Able2', 5), seal('bravo1', 'Bravo1', 4),
      { name: 'Road1', character: 'm51_terror01', model: 'CLIB_MDL.ZED:ter', teams: [5, 30], starts: [{ point: 'Road1', value: 7 }], weapons: [], weapon: null, stand: 'seal_stand' },
      { name: 'Informant', character: 'albanian_informant', model: 'CLIB_MDL.ZED:inf', teams: [26], starts: [{ point: 'Informant', value: '2' }], weapons: [], weapon: null, stand: 'civ_stand' },
    ];
    const points = [{ name: 'sealstart', x: 948, z: 828 }, { name: 'Able2', x: 5388, z: 2898 }, { name: 'Bravo1', x: 938, z: 858 },
      { name: 'Road1', x: 1048, z: 1918 }, { name: 'Informant', x: 1238, z: 3098 }];
    const plans = planFigures(actors, points, { clips: ['seal_stand', 'civ_stand'], armory: [], chosen: {} });
    expect(plans.map(p => [p.actor, p.point, p.heading, p.clip, p.withTeam])).toEqual([
      ['player', 'sealstart', 4, 'seal_stand', null],
      // Able2 is ~4,900 units away: the squad mate stands beside the player, facing the player's way.
      ['able2', 'sealstart', 4, 'seal_stand', 'Able2'],
      ['bravo1', 'Bravo1', 4, 'seal_stand', null],
      ['Road1', 'Road1', 7, 'seal_stand', null],
      ['Informant', 'Informant', 2, 'civ_stand', null],
    ]);
    // Heading 4 faces south (+Z), so the first slot (to the right, behind) is at -x and -z of the player.
    const [dx, dz] = plans[1].offset!;
    expect(dx).toBeCloseTo(-7);
    expect(dz).toBeCloseTo(-4);
    expect(plans[0].offset).toBeNull();
    // A multiplayer map: the player record is the template for human players and is not drawn.
    expect(planFigures(actors, points, { clips: [], armory: [], chosen: {}, multiplayer: true }).map(p => p.actor))
      .toEqual(['able2', 'bravo1', 'Road1', 'Informant']);
  });

  it('reads headings as eighths of a turn clockwise from north (-Z)', () => {
    expect([0, 1, 2, 4, 6, 7].map(compassOf)).toEqual(['N', 'NE', 'E', 'S', 'W', 'NW']);
    expect(compassOf(null)).toBe('?');
    // The models face -Z unturned; a yaw of -h*45 degrees turns them clockwise seen from above.
    for (const [h, x, z] of [[0, 0, -1], [2, 1, 0], [4, 0, 1], [6, -1, 0]] as const) {
      const yaw = yawOf(h);
      expect(-Math.sin(yaw)).toBeCloseTo(x);
      expect(-Math.cos(yaw)).toBeCloseTo(z);
    }
  });
});
