import { describe, expect, it } from 'vitest';
import * as THREE from 'three';
import { heldOrbitRate, matchCameraKey, orbitPosition, zoomPosition } from './cameraKeys';

function event(key: string, code = '', fields: Partial<KeyboardEvent> = {}) {
  return { key, code, ctrlKey: false, metaKey: false, altKey: false, ...fields };
}

describe('camera keys', () => {
  it('maps arrows to held orbit and the numpad to steps and zoom', () => {
    expect(matchCameraKey(event('ArrowLeft'))).toEqual({ kind: 'hold', key: 'left' });
    expect(matchCameraKey(event('4', 'Numpad4'))).toEqual({ kind: 'orbit', yaw: -15, pitch: 0 });
    expect(matchCameraKey(event('8', 'Numpad8'))).toEqual({ kind: 'orbit', yaw: 0, pitch: 15 });
    expect(matchCameraKey(event('9', 'Numpad9'))).toEqual({ kind: 'orbit', yaw: 180, pitch: 0 });
    expect(matchCameraKey(event('+', 'NumpadAdd'))?.kind).toBe('zoom');
    expect(matchCameraKey(event('1', 'Numpad1'))).toBeNull();
    expect(matchCameraKey(event('ArrowUp', '', { ctrlKey: true }))).toBeNull();
  });

  it('combines held arrows into a rate', () => {
    expect(heldOrbitRate(new Set(['left', 'up']), false)).toEqual({ yaw: -90, pitch: 90 });
    expect(heldOrbitRate(new Set(['left', 'right']), true)).toEqual({ yaw: 0, pitch: 0 });
  });

  it('orbits about the target keeping the distance', () => {
    const target = new THREE.Vector3(10, 0, 0);
    const front = new THREE.Vector3(10, 0, 5);
    const turned = orbitPosition(front, target, 90, 0);
    expect(turned.distanceTo(target)).toBeCloseTo(5);
    expect(turned.x).toBeCloseTo(15);
    expect(turned.z).toBeCloseTo(0);
    const above = orbitPosition(front, target, 0, 90);
    expect(above.y).toBeCloseTo(5, 2);
    // Past the pole it stops instead of flipping.
    expect(orbitPosition(above, target, 0, 45).y).toBeGreaterThan(4.99);
  });

  it('zooms toward and away from the target', () => {
    const target = new THREE.Vector3();
    expect(zoomPosition(new THREE.Vector3(0, 0, 10), target, 0.5).z).toBeCloseTo(5);
    expect(zoomPosition(new THREE.Vector3(0, 0, 10), target, 2).z).toBeCloseTo(20);
  });
});
