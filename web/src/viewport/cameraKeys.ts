// Keyboard camera controls shared by every 3D view (Map Builder, Model Editor, Animation Editor): the arrow keys
// orbit while held, the numpad turns in 15° steps (Blender's 4 / 6 / 8 / 2, 9 for the opposite side) and zooms
// (+ / −). Pure: the viewport applies the result to its orbit target.

import * as THREE from 'three';

// Degrees per numpad press, degrees per second while an arrow is held (Shift: 3×), degrees for an arrow tap too
// short to last a frame, and the zoom factor per press.
export const ORBIT_STEP = 15;
export const ORBIT_RATE = 90;
export const ORBIT_TAP = 3;
export const ZOOM_STEP = 1.25;

export type CameraKey =
  | { kind: 'hold'; key: 'left' | 'right' | 'up' | 'down' }
  | { kind: 'orbit'; yaw: number; pitch: number }
  | { kind: 'zoom'; factor: number };

const ARROWS: Record<string, 'left' | 'right' | 'up' | 'down'> = { ArrowLeft: 'left', ArrowRight: 'right', ArrowUp: 'up', ArrowDown: 'down' };

// The camera action for a key, or null. Ctrl / Cmd / Alt combinations are left to the page.
export function matchCameraKey(e: Pick<KeyboardEvent, 'key' | 'code' | 'ctrlKey' | 'metaKey' | 'altKey'>): CameraKey | null {
  if (e.ctrlKey || e.metaKey || e.altKey) { return null; }
  if (ARROWS[e.key]) { return { kind: 'hold', key: ARROWS[e.key] }; }
  switch (e.code) {
    case 'Numpad4': return { kind: 'orbit', yaw: -ORBIT_STEP, pitch: 0 };
    case 'Numpad6': return { kind: 'orbit', yaw: ORBIT_STEP, pitch: 0 };
    case 'Numpad8': return { kind: 'orbit', yaw: 0, pitch: ORBIT_STEP };
    case 'Numpad2': return { kind: 'orbit', yaw: 0, pitch: -ORBIT_STEP };
    case 'Numpad9': return { kind: 'orbit', yaw: 180, pitch: 0 };
    case 'NumpadAdd': return { kind: 'zoom', factor: 1 / ZOOM_STEP };
    case 'NumpadSubtract': return { kind: 'zoom', factor: ZOOM_STEP };
  }
  return null;
}

// Yaw / pitch (degrees per second) from the held arrows: Left / Right turn the view, Up / Down tilt it.
export function heldOrbitRate(held: ReadonlySet<string>, fast: boolean): { yaw: number; pitch: number } {
  const rate = ORBIT_RATE * (fast ? 3 : 1);
  return {
    yaw: ((held.has('right') ? 1 : 0) - (held.has('left') ? 1 : 0)) * rate,
    pitch: ((held.has('up') ? 1 : 0) - (held.has('down') ? 1 : 0)) * rate,
  };
}

// The camera position after orbiting about target: yaw turns about world Y (positive = the view turns right, as if
// walking around the target counter-clockwise from above), pitch tilts up (positive looks more from above). The
// pitch stops short of straight up / down so the view never flips.
export function orbitPosition(position: THREE.Vector3, target: THREE.Vector3, yawDeg: number, pitchDeg: number): THREE.Vector3 {
  const offset = position.clone().sub(target);
  const s = new THREE.Spherical().setFromVector3(offset);
  s.theta += THREE.MathUtils.degToRad(yawDeg);
  s.phi = THREE.MathUtils.clamp(s.phi - THREE.MathUtils.degToRad(pitchDeg), 0.001, Math.PI - 0.001);
  return target.clone().add(new THREE.Vector3().setFromSpherical(s));
}

// The camera position after zooming toward (factor < 1) or away from (factor > 1) the target.
export function zoomPosition(position: THREE.Vector3, target: THREE.Vector3, factor: number, minDistance = 0.05): THREE.Vector3 {
  const offset = position.clone().sub(target);
  const length = Math.max(minDistance, offset.length() * factor);
  return target.clone().add(offset.setLength(length));
}
