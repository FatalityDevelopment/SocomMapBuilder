import { describe, expect, it } from 'vitest';
import * as THREE from 'three';
import { buildPathObject, buildRegionObject, pathDetail, regionGeometry } from './mapShapes';

const IDENTITY = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];

function sizeOf(g: THREE.BufferGeometry): THREE.Vector3 {
  g.computeBoundingBox();
  return g.boundingBox!.getSize(new THREE.Vector3());
}

describe('Battlefront map shapes', () => {
  it('draws a box region from its half extents', () => {
    const s = sizeOf(regionGeometry({ name: 'cp1_capture', shape: 'box', role: 'capture', matrix: IDENTITY, size: [6, 4, 12] }));
    expect([s.x, s.y, s.z]).toEqual([12, 8, 24]);
  });

  it('reads a sphere radius from the first size only', () => {
    const s = sizeOf(regionGeometry({ name: 'cp10_capture', shape: 'sphere', role: 'capture', matrix: IDENTITY, size: [5, 10, 10] }));
    expect(s.x).toBeCloseTo(10);
    expect(s.y).toBeCloseTo(10);
  });

  it('draws a cylinder with x / z radii and a half height', () => {
    const s = sizeOf(regionGeometry({ name: 'c', shape: 'cylinder', role: 'capture', matrix: IDENTITY, size: [10, 3, 20] }));
    expect(s.x).toBeCloseTo(20);
    expect(s.y).toBeCloseTo(6);
    expect(s.z).toBeCloseTo(40);
  });

  it('places a region by its matrix and closes a boundary path', () => {
    const moved = [...IDENTITY]; moved[12] = 100;
    const region = buildRegionObject({ name: 'r', shape: 'box', role: 'region', matrix: moved, size: [1, 1, 1] });
    expect(region.matrix.elements[12]).toBe(100);
    const edge = { name: 'boundary1', kind: 'boundary', closed: true, nodes: [[0, 0, 0], [10, 0, 0], [10, 0, 10]] };
    const line = buildPathObject(edge, 2).children[0] as THREE.Line;
    expect(line.geometry.getAttribute('position').count).toBe(4);
    expect(pathDetail({ ...edge, commandPost: null })).toBe('boundary · 3 nodes');
  });
});
