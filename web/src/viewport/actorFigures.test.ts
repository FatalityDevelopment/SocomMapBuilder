import { describe, expect, it } from 'vitest';
import { pickClip, planFigures, weaponForValve } from './actorFigures';

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

  it('stands each actor with a model at its first free start point, SEALs with the armory in turn', () => {
    const actors = [
      { name: 'player', character: 'jungle_A51', model: 'CLIB_MDL.ZED:MESH_seal_A_woodland_LO', starts: [{ point: 'sealstart' }] },
      { name: 'able2', character: 'jungle_B51', model: 'CLIB_MDL.ZED:MESH_seal_B_woodland_LO', starts: [{ point: 'Able2' }] },
      { name: 'bravo1', character: 'jungle_C51', model: 'CLIB_MDL.ZED:MESH_seal_A_woodland_LO', starts: [{ point: 'Bravo1' }] },
      { name: 'Road1', character: 'm51_terror01', model: 'CLIB_MDL.ZED:MESH_alban_ter01', starts: [{ point: 'road1' }, { point: 'Road1b' }] },
      { name: 'Road1again', character: 'm51_terror01', model: 'CLIB_MDL.ZED:MESH_alban_ter01', starts: [{ point: 'Road1' }, { point: 'Road1b' }] },
      { name: 'ghost', character: 'm51_terror02', model: null, starts: [{ point: 'Ghost' }] },
      { name: 'lost', character: 'm51_terror02', model: 'CLIB_MDL.ZED:MESH_alban_ter02', starts: [{ point: 'nowhere' }] },
    ];
    const plans = planFigures(actors, ['SealStart', 'Able2', 'Bravo1', 'Road1', 'Road1b', 'Ghost'], {
      clips: ['seal_stand', 'guard_stand02'], armory: ['WEAP_MDL.ZED:mp5sd', 'WEAP_MDL.ZED:m4Acarbine_sd'], chosen: { m51_terror01: 'WEAP_MDL.ZED:ak47' } });
    expect(plans.map(p => [p.actor, p.point, p.clip, p.weapon])).toEqual([
      ['player', 'SealStart', 'seal_stand', 'WEAP_MDL.ZED:mp5sd'],
      ['able2', 'Able2', 'seal_stand', 'WEAP_MDL.ZED:m4Acarbine_sd'],
      ['bravo1', 'Bravo1', 'seal_stand', 'WEAP_MDL.ZED:mp5sd'],
      ['Road1', 'Road1', 'guard_stand02', 'WEAP_MDL.ZED:ak47'],
      ['Road1again', 'Road1b', 'guard_stand02', 'WEAP_MDL.ZED:ak47'],
    ]);
  });
});
