import * as THREE from 'three';
import { describe, expect, it } from 'vitest';
import { gameVisibility, objectMatches, type ClipMotionData } from './gear-state';
import { gearLocal } from '../zipper-core/skinning';

// guard_light_cigarette, guard_toss_cigarette and guard_smoke_drag01 as motion.rdr has them (CZANIM's show_cigarette
// turns on cig and cigarette, toss_cigarette turns off cig).
const light: ClipMotionData['callbacks'] = [
  { effect: 'show_cigarette', time: 0.05, found: true, shows: ['cig', 'cigarette'], hides: ['ember'], spawns: [] },
  { effect: 'cigarette_flame', time: 0.656, found: true, shows: [], hides: [], spawns: [] },
];
const toss: ClipMotionData['callbacks'] = [{ effect: 'toss_cigarette', time: 0.14, found: true, shows: [], hides: ['cig'], spawns: ['drop_cigarette'] }];
const drag: ClipMotionData['callbacks'] = [{ effect: 'cigarette_smoke', time: 0.32, found: true, shows: [], hides: [], spawns: [] }];

const cigarette = { id: 'gear:cigarette', label: 'cigarette', objects: ['cigarette'] };
const holster = { id: 'gear:seal_holster', label: 'seal_holster', objects: ['gear_holster'] };

describe('gear the game shows', () => {
  it('matches ZANIM objects by name, model or the start of the name', () => {
    expect(objectMatches('cigarette', cigarette)).toBe(true);
    expect(objectMatches('cig', cigarette)).toBe(true);
    expect(objectMatches('ember', cigarette)).toBe(false);
    expect(objectMatches('gear_holster', holster)).toBe(true);
    // Two letters are too few to stand for anything.
    expect(objectMatches('ci', cigarette)).toBe(false);
  });

  it('hides the cigarette until show_cigarette, then shows it', () => {
    const carry = new Map([['gear:cigarette', false]]);
    expect(gameVisibility([cigarette, holster], light, 0, carry).get('gear:cigarette')).toBe(false);
    expect(gameVisibility([cigarette, holster], light, 1, carry).get('gear:cigarette')).toBe(true);
    // Gear no callback touches keeps showing.
    expect(gameVisibility([cigarette, holster], light, 0, carry).get('gear:seal_holster')).toBe(true);
  });

  it('starts a clip the other way round from its first toggle, whatever was carried', () => {
    // Even when the last clip left it hidden, a clip that tosses the cigarette starts with it in hand.
    const hidden = new Map([['gear:cigarette', false]]);
    expect(gameVisibility([cigarette], toss, 0, hidden).get('gear:cigarette')).toBe(true);
    expect(gameVisibility([cigarette], toss, 0.5, hidden).get('gear:cigarette')).toBe(false);
  });

  it('keeps what the last clip left when this one does not toggle it', () => {
    expect(gameVisibility([cigarette], drag, 0.5, new Map([['gear:cigarette', true]])).get('gear:cigarette')).toBe(true);
    expect(gameVisibility([cigarette], drag, 0.5, new Map([['gear:cigarette', false]])).get('gear:cigarette')).toBe(false);
  });
});

describe('gear offsets', () => {
  it("turn the gear model's Y up along a bone's X, as SOCOM II's limbs run", () => {
    // character.rdr: seal_black_hat {ofs head [0 0 0] [-179 89.7 90.91]} and black_battle_helmet [44.8 90 -44.8]
    // both stand the hat up along the head bone (+X).
    for (const rot of [[-179, 89.7, 90.91], [44.8, 90, -44.8]]) {
      const up = new THREE.Vector3(0, 1, 0).applyMatrix4(new THREE.Matrix4().extractRotation(gearLocal([0, 0, 0], rot)));
      expect(up.x).toBeGreaterThan(0.99);
    }
  });

  it('translates in the bone space before rotating', () => {
    const p = new THREE.Vector3().setFromMatrixPosition(gearLocal([1.75, -0.12, -0.33], [2.93, -132.2, 159.5]));
    expect(p.toArray().map(v => +v.toFixed(2))).toEqual([1.75, -0.12, -0.33]);
  });
});
