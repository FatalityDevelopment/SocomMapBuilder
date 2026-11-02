import * as THREE from 'three';

export interface PlacementOptions {
  yaw: number;
  scale: number;
  randomYaw: boolean;
  randomScale: boolean;
  minScale: number;
  maxScale: number;
  alignSurface: boolean;
}
export interface PlacementSample { yaw: number; scale: number }
export interface PlacementTransform extends PlacementSample { matrix: number[] }

export function samplePlacement(options: PlacementOptions, random: () => number = Math.random): PlacementSample {
  const scale = Number.isFinite(options.scale) ? Math.max(0.01, options.scale) : 1;
  const a = Number.isFinite(options.minScale) ? Math.max(0.01, options.minScale) : scale;
  const b = Number.isFinite(options.maxScale) ? Math.max(0.01, options.maxScale) : scale;
  return {
    yaw: options.randomYaw ? random() * 360 : Number.isFinite(options.yaw) ? options.yaw : 0,
    scale: options.randomScale ? Math.min(a, b) + random() * Math.abs(b - a) : scale,
  };
}

export function placementMatrix(position: THREE.Vector3, normal: THREE.Vector3, sample: PlacementSample, align: boolean): THREE.Matrix4 {
  const up = new THREE.Vector3(0, 1, 0);
  const rotation = new THREE.Quaternion();
  if (align && normal.lengthSq() > 1e-10) { rotation.setFromUnitVectors(up, normal.clone().normalize()); }
  rotation.multiply(new THREE.Quaternion().setFromAxisAngle(up, THREE.MathUtils.degToRad(sample.yaw)));
  return new THREE.Matrix4().compose(position, rotation, new THREE.Vector3().setScalar(sample.scale));
}

// Raycast face normals are local to geometry, including on instanced props.
export function surfaceNormal(hit: THREE.Intersection): THREE.Vector3 {
  if (!hit.face) { return new THREE.Vector3(0, 1, 0); }
  const world = hit.object.matrixWorld.clone();
  if (hit.object instanceof THREE.InstancedMesh && hit.instanceId !== undefined) {
    const instance = new THREE.Matrix4();
    hit.object.getMatrixAt(hit.instanceId, instance);
    world.multiply(instance);
  }
  return hit.face.normal.clone().applyNormalMatrix(new THREE.Matrix3().getNormalMatrix(world));
}

// Change heading or size without rebuilding an upright transform and losing surface tilt.
export function adjustPlacement(matrix: number[], change: { yaw?: number; scale?: number }): number[] {
  const result = [...matrix];
  const oldScale = Math.hypot(matrix[0], matrix[1], matrix[2]);
  const factor = change.scale !== undefined && oldScale > 1e-12 ? Math.max(0.01, change.scale) / oldScale : 1;
  const yaw = Math.atan2(-matrix[2], matrix[0]);
  const delta = change.yaw !== undefined ? THREE.MathUtils.degToRad(change.yaw) - yaw : 0;
  const c = Math.cos(delta), s = Math.sin(delta);
  for (const offset of [0, 4, 8]) {
    const x = matrix[offset], y = matrix[offset + 1], z = matrix[offset + 2];
    result[offset] = (c * x + s * z) * factor;
    result[offset + 1] = y * factor;
    result[offset + 2] = (-s * x + c * z) * factor;
  }
  return result;
}
