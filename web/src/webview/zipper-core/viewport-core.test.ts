import { describe, expect, it, vi } from 'vitest';
import * as THREE from 'three';
import { billboardYaw, disposeObjectResources } from './viewport-core';

describe('foliage orientation', () => {
  it('keeps orthographic cards parallel regardless of their positions', () => {
    const camera = new THREE.OrthographicCamera();
    camera.position.set(10, 5, 10);
    camera.lookAt(0, 0, 0);
    for (const axis of ['x', 'z'] as const) {
      expect(billboardYaw(axis, camera, new THREE.Vector3(100, 0, -40)))
        .toBeCloseTo(billboardYaw(axis, camera, new THREE.Vector3(-80, 0, 70)));
    }
    const before = billboardYaw('z', camera, new THREE.Vector3());
    camera.lookAt(20, 5, 10);
    expect(billboardYaw('z', camera, new THREE.Vector3())).not.toBeCloseTo(before);
  });

  it('turns perspective cards toward the camera from their own positions', () => {
    const camera = new THREE.PerspectiveCamera();
    camera.position.set(0, 0, 10);
    expect(billboardYaw('z', camera, new THREE.Vector3())).toBeCloseTo(0);
    expect(billboardYaw('z', camera, new THREE.Vector3(10, 0, 10))).toBeCloseTo(-Math.PI / 2);
    expect(billboardYaw('x', camera, new THREE.Vector3())).toBeCloseTo(-Math.PI / 2);
  });
});

describe('viewport resource cleanup', () => {
  it('releases shared helper geometry, materials and textures once', () => {
    const group = new THREE.Group();
    const geometry = new THREE.BoxGeometry();
    const texture = new THREE.Texture();
    const material = new THREE.MeshBasicMaterial({ map: texture });
    group.add(new THREE.Mesh(geometry, [material, material]), new THREE.Mesh(geometry, material));
    const spies = [geometry, material, texture].map(resource => vi.spyOn(resource, 'dispose'));
    disposeObjectResources(group);
    spies.forEach(spy => expect(spy).toHaveBeenCalledTimes(1));
  });
});
