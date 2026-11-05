import * as THREE from 'three';
import { describe, expect, it } from 'vitest';
import { heldWeaponMatrix, paintWeight, poseBones, skinChunk, weightColor, weightOf } from './skinning';
import type { BoneWire, EditorChunk } from '../model-editor/mesh-data';

const bone = (name: string, parent: number, at: number[]): BoneWire => {
  const local = new THREE.Matrix4().makeTranslation(at[0], at[1], at[2]).toArray();
  return { name, parent, matrixId: 0, geoIndex: 0, local, world: local };
};

describe('weight paint', () => {
  // Slots: bone 3 at 0.6, bone 5 at 0.4, two empty.
  const bones = [3, 5, -1, -1], weights = [0.6, 0.4, 0, 0];

  it('adds towards 1 and the others keep their proportions', () => {
    const w = paintWeight(bones, weights, 5, 'add', 0.5)!;
    expect(w[1]).toBeCloseTo(0.7);
    expect(w[0]).toBeCloseTo(0.3);
    expect(w[0] + w[1]).toBeCloseTo(1);
  });

  it('subtracts towards 0, handing the share to the other bones', () => {
    const w = paintWeight(bones, weights, 3, 'subtract', 0.5)!;
    expect(w[0]).toBeCloseTo(0.3);
    expect(w[1]).toBeCloseTo(0.7);
  });

  it('cannot add a bone the vertex has no slot for, or empty a vertex that hangs on one bone', () => {
    expect(paintWeight(bones, weights, 9, 'add', 1)).toBeNull();
    expect(paintWeight([3, -1, -1, -1], [1, 0, 0, 0], 3, 'subtract', 1)).toBeNull();
    // A weight-0 slot (the group's first batch lists every vertex) can take weight.
    const w = paintWeight([3, 5, -1, -1], [1, 0, 0, 0], 5, 'add', 0.25)!;
    expect(w[1]).toBeCloseTo(0.25);
    expect(w[0]).toBeCloseTo(0.75);
    expect(weightOf([3, 5, -1, -1], w, 4, 0, 5)).toBeCloseTo(0.25);
  });

  it('sets towards a given weight', () => {
    const w = paintWeight(bones, weights, 3, 'set', 1, 0.25)!;
    expect(w[0]).toBeCloseTo(0.25);
    expect(w[1]).toBeCloseTo(0.75);
  });

  it('draws weights from blue to red', () => {
    expect(weightColor(1)[0]).toBeGreaterThan(0.9);
    expect(weightColor(0.001)[2]).toBeGreaterThan(0.7);
  });
});

describe('posing', () => {
  it.each(['rhand', 'rightHand'])('mounts the rifle in the posed %s frame', name => {
    const skeleton = [bone('root', -1, [10, 0, 0]), bone(name, 0, [0, 3, 0])];
    const clip = { duration: 1, samples: 1, bones: [
      { name, positions: [], rotations: [[0, 0, Math.SQRT1_2, Math.SQRT1_2]] },
      { name: 'rifle', positions: [[2, 0, 0]], rotations: [] },
    ] };
    const grip = heldWeaponMatrix(skeleton, poseBones(skeleton, clip, 0), clip)!;
    expect(new THREE.Vector3().setFromMatrixPosition(grip).toArray().map(Math.round)).toEqual([10, 5, 0]);
  });

  it('bends a vertex with the bones it follows', () => {
    // A root and an elbow 10 up; a clip turns the elbow a quarter about Z.
    const skeleton = [bone('root', -1, [0, 0, 0]), bone('elbow', 0, [0, 10, 0])];
    const clip = { duration: 1, samples: 2, bones: [{ name: 'elbow', positions: [], rotations: [[0, 0, Math.SQRT1_2, Math.SQRT1_2]] }] };
    const world = poseBones(skeleton, clip, 0);
    // A vertex 4 above the elbow, half on each bone.
    const chunk = {
      vertexCount: 1,
      skin: { bones: new Int32Array([0, 1]), weights: new Float32Array([0.5, 0.5]), positions: new Float32Array([0, 14, 0, 0, 4, 0]), normals: new Float32Array([1, 0, 0, 1, 0, 0]) },
    } as unknown as EditorChunk;
    const pos = new Float32Array(3), nrm = new Float32Array(3);
    skinChunk(chunk, 2, world, pos, nrm);
    // Root carries it to (0, 14); the turned elbow to (-4, 10): the blend is halfway.
    expect([pos[0], pos[1]].map(v => +v.toFixed(4))).toEqual([-2, 12]);
    // The bind pose puts it where it was.
    skinChunk(chunk, 2, poseBones(skeleton, null, 0), pos, nrm);
    expect([pos[0], pos[1]].map(v => +v.toFixed(4))).toEqual([0, 14]);
  });
});
