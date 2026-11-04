// Align and distribute for a multi-selection, and how a gizmo drag applies to each item under the pivot modes. Pure
// math on column-major matrices and world boxes, so it is tested without a viewport.

import * as THREE from 'three';

export type Axis = 0 | 1 | 2;
export type AlignTo = 'min' | 'center' | 'max';
/** Where rotate and scale turn about: the selection's centre, each item's own origin, or the first selected item's. */
export type PivotMode = 'center' | 'individual' | 'active';

export interface Boxed { min: number[]; max: number[] }

/**
 * Per item, how far to move it along `axis` so its box lines up with the others': all minimums on the lowest, all
 * centres on the middle of the selection's extent, all maximums on the highest.
 */
export function alignOffsets(boxes: Boxed[], axis: Axis, to: AlignTo): number[] {
  if (!boxes.length) { return []; }
  const lo = Math.min(...boxes.map(b => b.min[axis])), hi = Math.max(...boxes.map(b => b.max[axis]));
  return boxes.map(b => {
    if (to === 'min') { return lo - b.min[axis]; }
    if (to === 'max') { return hi - b.max[axis]; }
    return (lo + hi) / 2 - (b.min[axis] + b.max[axis]) / 2;
  });
}

/**
 * Per item, how far to move it along `axis` so the centres are evenly spaced between the outermost two (which stay).
 * Items keep their order along the axis.
 */
export function distributeOffsets(boxes: Boxed[], axis: Axis): number[] {
  const centre = (b: Boxed) => (b.min[axis] + b.max[axis]) / 2;
  const out = boxes.map(() => 0);
  if (boxes.length < 3) { return out; }
  const order = boxes.map((b, i) => ({ c: centre(b), i })).sort((a, b) => a.c - b.c || a.i - b.i);
  const first = order[0].c, step = (order[order.length - 1].c - first) / (order.length - 1);
  order.forEach((o, k) => { out[o.i] = first + step * k - o.c; });
  return out;
}

/**
 * Where each item ends up after a gizmo drag. `delta` is the pivot's change (world, about the selection's centre).
 * The centre mode applies it as it is; the others turn and scale each item about its own origin (or all about the
 * first item's), keeping the move part of the drag.
 */
export function applyDrag(delta: THREE.Matrix4, items: THREE.Matrix4[], mode: PivotMode, dragCentre: THREE.Vector3): THREE.Matrix4[] {
  if (mode === 'center' || !items.length) { return items.map(m => delta.clone().multiply(m)); }
  // The drag's own move: where the centre went.
  const moved = dragCentre.clone().applyMatrix4(delta).sub(dragCentre);
  // Its turn and scale alone, about the origin.
  const linear = delta.clone().setPosition(0, 0, 0);
  const about = (p: THREE.Vector3) => new THREE.Matrix4().makeTranslation(p.x, p.y, p.z).multiply(linear)
    .multiply(new THREE.Matrix4().makeTranslation(-p.x, -p.y, -p.z));
  const active = new THREE.Vector3().setFromMatrixPosition(items[0]);
  return items.map(m => {
    const p = mode === 'individual' ? new THREE.Vector3().setFromMatrixPosition(m) : active;
    return new THREE.Matrix4().makeTranslation(moved.x, moved.y, moved.z).multiply(about(p)).multiply(m);
  });
}
