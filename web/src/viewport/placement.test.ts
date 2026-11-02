import { describe, expect, it } from 'vitest';
import * as THREE from 'three';
import { adjustPlacement, placementMatrix, samplePlacement, surfaceNormal, type PlacementOptions } from './placement';

const options: PlacementOptions = { yaw: 90, scale: 2, randomYaw: false, randomScale: false, minScale: 0.8, maxScale: 1.2, alignSurface: false };

describe('placement transforms', () => {
  it('samples repeatable random yaw and scale, including reversed ranges', () => {
    expect(samplePlacement(options)).toEqual({ yaw: 90, scale: 2 });
    expect(samplePlacement({ ...options, randomYaw: true, randomScale: true, minScale: 2, maxScale: 1 }, () => 0.25))
      .toEqual({ yaw: 90, scale: 1.25 });
    expect(samplePlacement({ ...options, yaw: NaN, scale: Infinity })).toEqual({ yaw: 0, scale: 1 });
  });
  it('preserves position and scale while aligning local up to the surface', () => {
    const position = new THREE.Vector3(10, 20, 30);
    const normal = new THREE.Vector3(0, 1, 1).normalize();
    const matrix = placementMatrix(position, normal, { yaw: 35, scale: 2 }, true);
    expect(new THREE.Vector3().setFromMatrixPosition(matrix).toArray()).toEqual(position.toArray());
    expect(new THREE.Vector3(0, 1, 0).transformDirection(matrix).distanceTo(normal)).toBeLessThan(1e-6);
    new THREE.Vector3().setFromMatrixScale(matrix).toArray().forEach(value => expect(value).toBeCloseTo(2));
    expect(new THREE.Vector3(0, 1, 0).transformDirection(placementMatrix(position, normal, { yaw: 35, scale: 2 }, false)).y).toBeCloseTo(1);
  });
  it('transforms instanced surface normals into world space without mutating the hit', () => {
    const mesh = new THREE.InstancedMesh(new THREE.PlaneGeometry(), new THREE.MeshBasicMaterial(), 1);
    mesh.setMatrixAt(0, new THREE.Matrix4().makeRotationX(Math.PI / 2));
    mesh.rotation.z = Math.PI / 2;
    mesh.updateMatrixWorld();
    const normal = new THREE.Vector3(0, 0, 1);
    const hit: THREE.Intersection = { object: mesh, instanceId: 0, distance: 1, point: new THREE.Vector3(), face: { normal, a: 0, b: 1, c: 2, materialIndex: 0 } };
    expect(surfaceNormal(hit).distanceTo(new THREE.Vector3(1, 0, 0))).toBeLessThan(1e-6);
    expect(normal.toArray()).toEqual([0, 0, 1]);
    mesh.dispose(); mesh.geometry.dispose(); (mesh.material as THREE.Material).dispose();
  });
});


describe('editing aligned placements', () => {
  it('changes size without removing tilt, heading or translation', () => {
    const normal = new THREE.Vector3(0, 1, 1).normalize();
    const original = placementMatrix(new THREE.Vector3(4, 5, 6), normal, { yaw: 35, scale: 2 }, true).toArray();
    const changed = new THREE.Matrix4().fromArray(adjustPlacement(original, { scale: 3 }));
    expect(new THREE.Vector3(0, 1, 0).transformDirection(changed).distanceTo(normal)).toBeLessThan(1e-6);
    new THREE.Vector3().setFromMatrixScale(changed).toArray().forEach(value => expect(value).toBeCloseTo(3));
    expect(new THREE.Vector3().setFromMatrixPosition(changed).toArray()).toEqual([4, 5, 6]);
    expect(original).toEqual(placementMatrix(new THREE.Vector3(4, 5, 6), normal, { yaw: 35, scale: 2 }, true).toArray());
  });
  it('changes world heading while preserving the amount of tilt and scale', () => {
    const normal = new THREE.Vector3(0, 1, 1).normalize();
    const original = placementMatrix(new THREE.Vector3(4, 5, 6), normal, { yaw: 35, scale: 2 }, true).toArray();
    const changed = adjustPlacement(original, { yaw: 90 });
    expect(Math.atan2(-changed[2], changed[0]) * 180 / Math.PI).toBeCloseTo(90);
    expect(new THREE.Vector3(0, 1, 0).transformDirection(new THREE.Matrix4().fromArray(changed)).y).toBeCloseTo(normal.y);
    expect(Math.hypot(changed[0], changed[1], changed[2])).toBeCloseTo(2);
    expect(changed.slice(12, 15)).toEqual([4, 5, 6]);
  });
});
