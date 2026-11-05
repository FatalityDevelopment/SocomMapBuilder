// Skinning for the editors: a skeleton posed by a clip at a time, a skinned chunk's vertices through that pose, and
// the weight paint's math (a vertex's influences moved towards or away from one bone, renormalised over the bones
// its draw group lists for it) with the heat colours the weight view draws. Pure, so it is tested without WebGL.

import * as THREE from 'three';
import type { BoneWire, EditorChunk } from '../model-editor/mesh-data';

/** A clip as ASSET_ANIMATION serves it (the parts posing needs). */
export interface PoseClip {
  duration: number;
  samples: number;
  bones: Array<{ name: string; positions: number[][]; rotations: number[][] }>;
}

function sample(keys: number[][], f: number, quaternion: boolean): number[] {
  if (keys.length === 1) { return keys[0]; }
  const i = Math.min(Math.floor(f), keys.length - 2);
  const u = Math.max(0, Math.min(1, f - i));
  const a = keys[i], b = keys[i + 1];
  if (!quaternion) { return [0, 1, 2].map(k => a[k] + (b[k] - a[k]) * u); }
  const s = a[0] * b[0] + a[1] * b[1] + a[2] * b[2] + a[3] * b[3] < 0 ? -1 : 1;
  const q = [0, 1, 2, 3].map(k => a[k] + (s * b[k] - a[k]) * u);
  const n = Math.hypot(...q) || 1;
  return q.map(v => v / n);
}

/** Each skeleton bone's world matrix with `clip` at `time` (seconds); the bind pose without a clip. */
export function poseBones(skeleton: BoneWire[], clip: PoseClip | null, time: number): THREE.Matrix4[] {
  const tracks = new Map((clip?.bones ?? []).map(t => [t.name.toLowerCase(), t]));
  const f = clip ? time / Math.max(1e-6, clip.duration) * (clip.samples - 1) : 0;
  const world = skeleton.map(() => new THREE.Matrix4());
  skeleton.forEach((b, i) => {
    const bind = new THREE.Matrix4().fromArray(b.local);
    const track = tracks.get(b.name.toLowerCase());
    let local = bind;
    if (track && (track.positions.length || track.rotations.length)) {
      const p = new THREE.Vector3(), q = new THREE.Quaternion(), s = new THREE.Vector3();
      bind.decompose(p, q, s);
      if (track.positions.length) { p.fromArray(sample(track.positions, f, false)); }
      if (track.rotations.length) { q.fromArray(sample(track.rotations, f, true)); }
      local = new THREE.Matrix4().compose(p, q, new THREE.Vector3(1, 1, 1));
    }
    world[i] = b.parent >= 0 && b.parent < i ? world[b.parent].clone().multiply(local) : local.clone();
  });
  return world;
}

/** A held model's first-frame transform: the posed right hand followed by the clip's weapon mount. */
export function heldWeaponMatrix(skeleton: BoneWire[], world: THREE.Matrix4[], clip: PoseClip | null): THREE.Matrix4 | null {
  const hand = skeleton.findIndex(b => ['rhand', 'righthand'].includes(b.name.toLowerCase()));
  if (hand < 0) { return null; }
  const track = clip?.bones.find(b => ['rifle', 'weapon', 'pistol'].includes(b.name.toLowerCase()));
  const grip = world[hand].clone();
  if (!track) { return grip; }
  const p = track.positions[0] ? new THREE.Vector3().fromArray(track.positions[0]) : new THREE.Vector3();
  const q = track.rotations[0] ? new THREE.Quaternion().fromArray(track.rotations[0]) : new THREE.Quaternion();
  return grip.multiply(new THREE.Matrix4().compose(p, q, new THREE.Vector3(1, 1, 1)));
}

/** The chunk's vertices (and normals) through `world`, written into `positions` / `normals` (3 per vertex). */
export function skinChunk(chunk: EditorChunk, influences: number, world: THREE.Matrix4[], positions: Float32Array, normals: Float32Array): void {
  const skin = chunk.skin;
  if (!skin || !influences) { return; }
  for (let i = 0; i < chunk.vertexCount; i++) {
    let x = 0, y = 0, z = 0, nx = 0, ny = 0, nz = 0;
    for (let k = 0; k < influences; k++) {
      const s = i * influences + k, b = skin.bones[s];
      if (b < 0) { continue; }
      const w = skin.weights[s], e = (world[b] ?? world[0]).elements;
      const lx = skin.positions[s * 3], ly = skin.positions[s * 3 + 1], lz = skin.positions[s * 3 + 2];
      x += (e[0] * lx + e[4] * ly + e[8] * lz + e[12]) * w;
      y += (e[1] * lx + e[5] * ly + e[9] * lz + e[13]) * w;
      z += (e[2] * lx + e[6] * ly + e[10] * lz + e[14]) * w;
      const ax = skin.normals[s * 3], ay = skin.normals[s * 3 + 1], az = skin.normals[s * 3 + 2];
      nx += (e[0] * ax + e[4] * ay + e[8] * az) * w;
      ny += (e[1] * ax + e[5] * ay + e[9] * az) * w;
      nz += (e[2] * ax + e[6] * ay + e[10] * az) * w;
    }
    positions[i * 3] = x; positions[i * 3 + 1] = y; positions[i * 3 + 2] = z;
    const l = Math.hypot(nx, ny, nz) || 1;
    normals[i * 3] = nx / l; normals[i * 3 + 1] = ny / l; normals[i * 3 + 2] = nz / l;
  }
}

/** The weight `bone` has on vertex `i` (0 when it has no part for it). */
export function weightOf(bones: ArrayLike<number>, weights: ArrayLike<number>, influences: number, i: number, bone: number): number {
  let w = 0;
  for (let k = 0; k < influences; k++) { if (bones[i * influences + k] === bone) { w += weights[i * influences + k]; } }
  return w;
}

export type WeightMode = 'add' | 'subtract' | 'set';

/**
 * One vertex's influence weights (`weights[k]` for slot k, `bones[k]` its bone, -1 empty) after a brush touch of
 * strength `amount` (0..1) for `bone`: add pulls its weight towards 1, subtract towards 0, set towards `setTo`.
 * The other bones share what is left in their old proportions. Null when the vertex has no slot for the bone
 * (its draw group does not list it) or nothing can change (subtracting from a bone it hangs on alone).
 */
export function paintWeight(bones: ArrayLike<number>, weights: ArrayLike<number>, bone: number, mode: WeightMode, amount: number, setTo = 1): number[] | null {
  const slots = Array.from(bones, (b, k) => (b === bone ? k : -1)).filter(k => k >= 0);
  if (!slots.length) { return null; }
  const out = Array.from(weights);
  const old = slots.reduce((a, k) => a + out[k], 0);
  const goal = mode === 'add' ? 1 : mode === 'subtract' ? 0 : setTo;
  const target = old + (goal - old) * amount;
  const others = out.reduce((a, w, k) => a + (bones[k] >= 0 && bones[k] !== bone ? w : 0), 0);
  const othersSlots = Array.from(bones, (b, k) => (b >= 0 && b !== bone ? k : -1)).filter(k => k >= 0);
  if (!othersSlots.length && target < 1) { return null; }
  const next = Math.max(0, Math.min(1, target));
  if (Math.abs(next - old) < 1e-6) { return null; }
  // The bone's own weight sits in its first slot; the rest share 1 - next as before (evenly when they had none).
  slots.forEach((k, j) => { out[k] = j === 0 ? next : 0; });
  const rest = 1 - next;
  othersSlots.forEach(k => { out[k] = others > 1e-6 ? out[k] / others * rest : rest / othersSlots.length; });
  return out;
}

/** The weight view's heat colour for a weight: dark blue at 0 through green and yellow to red at 1 (0..1 RGB). */
export function weightColor(w: number): [number, number, number] {
  const t = Math.max(0, Math.min(1, w));
  if (t <= 0) { return [0.05, 0.05, 0.35]; }
  const stops: Array<[number, [number, number, number]]> = [[0, [0.1, 0.15, 0.8]], [0.33, [0.1, 0.8, 0.3]], [0.66, [0.95, 0.9, 0.1]], [1, [0.95, 0.15, 0.1]]];
  for (let i = 1; i < stops.length; i++) {
    if (t <= stops[i][0]) {
      const [a, ca] = stops[i - 1], [b, cb] = stops[i], u = (t - a) / (b - a);
      return [0, 1, 2].map(k => ca[k] + (cb[k] - ca[k]) * u) as [number, number, number];
    }
  }
  return stops[stops.length - 1][1];
}
