// Scene selection and instance collection for "Compose GEO scene".
//
// A map package carries several GEO archives (EFFE, WEAP, CLIB, FLIB, the
// map's own MP53_GEO ...). The map scene is the one that places the world:
// it has a `worldmodel` node, whose visuals are drawn by WORL_MDL's
// `worldmodel` payload. Instances are collected so that a node which is
// itself a model (it resolves to an MDL entry) is placed once and its
// implicit descendants are left to that model's own draw chains; explicit
// references (`model_name` children) are always placed.
//
// Pure functions so they can be checked outside the webview.

import type { InspectedZedArchive } from './zed-archive-parser';
import type { GeoInstance, GeoNode, GeoScene } from './zed-geo-parser';
import { findSceneNode, positionUnitForNode, readGeoTransform } from './zed-geo-parser';

export interface SceneCandidate {
  namespace: string;
  scene: GeoScene;
}

function countVisualNodes(node: GeoNode): number {
  let n = node.children.some(c => c.name === 'visuals') ? 1 : 0;
  for (const c of node.children) { n += countVisualNodes(c); }
  return n;
}

// The map's scene: preferred namespace first when it has a worldmodel, then
// any scene with a worldmodel, then the scene with the most placed visuals.
export function chooseMapScene(candidates: SceneCandidate[], preferredNamespace: string | null): SceneCandidate | null {
  if (candidates.length === 0) { return null; }
  const withWorld = candidates.filter(c => !!findSceneNode(c.scene, 'worldmodel'));
  const preferred = preferredNamespace?.toLowerCase();
  if (preferred) {
    const hit = withWorld.find(c => c.namespace === preferred) ?? candidates.find(c => c.namespace === preferred && countVisualNodes(c.scene.root) > 0);
    if (hit) { return hit; }
  }
  if (withWorld.length > 0) { return withWorld[0]; }
  return [...candidates].sort((a, b) => countVisualNodes(b.scene.root) - countVisualNodes(a.scene.root))[0];
}

export interface SceneInstance extends GeoInstance {
  // How the instance was found: the node resolves to a model, it references
  // one through model_name, it owns visuals no model covers, or CLUTTER.ZAR
  // places it.
  kind: 'model' | 'reference' | 'visuals' | 'clutter';
}

function identityMatrix(): number[] {
  return [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];
}

function multiplyMatrices(a: number[], b: number[]): number[] {
  const out = new Array<number>(16).fill(0);
  for (let row = 0; row < 4; row++) {
    for (let col = 0; col < 4; col++) {
      out[col * 4 + row] =
        a[0 * 4 + row] * b[col * 4 + 0] +
        a[1 * 4 + row] * b[col * 4 + 1] +
        a[2 * 4 + row] * b[col * 4 + 2] +
        a[3 * 4 + row] * b[col * 4 + 3];
    }
  }
  return out;
}

function cleanName(value: string): string {
  let s = (value ?? '').trim();
  const brace = s.indexOf('{');
  if (brace >= 0) {
    const end = s.indexOf('}', brace + 1);
    s = end > brace ? s.slice(brace + 1, end).trim() : s.slice(0, brace).trim();
  }
  return s;
}

function asciiOf(data: Uint8Array | null): string {
  if (!data) { return ''; }
  let s = '';
  for (const b of data) {
    if (b === 0) { break; }
    if (b >= 0x20 && b < 0x7f) { s += String.fromCharCode(b); }
  }
  return s.trim();
}

const CONTAINERS = new Set(['models', 'children', 'visuals', 'vis', 'nparams', 'vparams', 'model_name', 'di', 'params', 'points', 'matrix_id', '<root>']);

// Walk the scene from `root` in pre-order. `resolves(name)` says whether a
// name maps to a renderable MDL entry.
export function collectSceneInstances(scene: GeoScene, root: GeoNode, resolves: (name: string) => boolean): SceneInstance[] {
  const out: SceneInstance[] = [];
  const visit = (node: GeoNode, parent: number[], trail: string, covered: boolean) => {
    const isSceneNode = !CONTAINERS.has(node.name) && node.children.some(c => c.name === 'nparams');
    const matrix = isSceneNode ? multiplyMatrices(parent, readGeoTransform(node)) : parent;
    const path = isSceneNode ? `${trail}/${node.name}` : trail;
    let coveredBelow = covered;
    if (isSceneNode) {
      const name = cleanName(node.name);
      const ref = node.children.find(c => c.name === 'model_name');
      const refName = ref ? cleanName(asciiOf(ref.rawData)) : '';
      const hasVisuals = node.children.some(c => c.name === 'visuals');
      // The sky dome (`sky` -> SP53_MDL / M51_MDL) is placed like any other
      // reference; the preview's Show sky toggle hides it, and camera framing
      // and ground detection already leave sky geometry out.
      if (refName && resolves(refName)) {
        out.push({ modelName: refName, instanceName: name, path, matrix, node, positionUnit: positionUnitForNode(node), kind: 'reference' });
      } else if (!covered && resolves(name)) {
        out.push({ modelName: name, instanceName: name, path, matrix, node, positionUnit: positionUnitForNode(node), kind: 'model' });
        coveredBelow = true;
      } else if (!covered && hasVisuals) {
        out.push({ modelName: name, instanceName: name, path, matrix, node, positionUnit: positionUnitForNode(node), kind: 'visuals' });
      } else if (refName && !covered) {
        // Unresolvable reference: still reported so the log lists it.
        out.push({ modelName: refName, instanceName: name, path, matrix, node, positionUnit: positionUnitForNode(node), kind: 'reference' });
      }
    }
    for (const c of node.children) {
      if (c.name === 'nparams' || c.name === 'visuals' || c.name === 'model_name' || c.name === 'di') { continue; }
      visit(c, matrix, path, coveredBelow);
    }
  };
  visit(root, identityMatrix(), '', false);
  return out;
}

// The node the map scene is composed from: `worldmodel`, which places the
// terrain and buildings and references every prop. The scene's `models`
// list also holds the prototypes those references (and CLUTTER.ZAR) point
// at, defined at the origin; walking from the scene root drew each of them
// once at (0, 0, 0).
export function mapSceneRoot(scene: GeoScene): GeoNode {
  return findSceneNode(scene, 'worldmodel') ?? scene.root;
}

// One CLUTTER.ZAR placement: a model name and its column-major transform.
export interface ClutterPlacement {
  modelName: string;
  matrix: number[];
}

// CLUTTER.ZAR (same container as a ZED): Clutter > <model> > a_clutter >
// params (96 bytes) + scale_inverse. It places a map's trees, bushes, grass
// and rocks. `params` is either a 4x4 matrix laid out like nparams
// (translation in floats 12-14) or a quaternion (floats 0-3, x y z w,
// normalised here), floats 4-7 = 0 0 0 1, the position in floats 8-10 and
// the uniform scale in float 11 (= 1 / scale_inverse).
export function parseClutterPlacements(archive: InspectedZedArchive): ClutterPlacement[] {
  const out: ClutterPlacement[] = [];
  const byIndex = new Map(archive.entries.map(e => [e.index, e]));
  for (const entry of archive.entries) {
    if (entry.name !== 'params') { continue; }
    const owner = byIndex.get(byIndex.get(entry.parent)?.parent ?? -1);
    const data = archive.entryData.get(entry.index);
    if (!owner || !data || data.length < 64) { continue; }
    const view = new DataView(data.buffer, data.byteOffset, data.byteLength);
    const f = (i: number) => view.getFloat32(i * 4, true);
    const isMatrix = f(3) === 0 && f(7) === 0 && f(11) === 0 && f(15) === 1;
    const isQuat = f(4) === 0 && f(5) === 0 && f(6) === 0 && f(7) === 1 && f(11) > 0;
    let matrix: number[];
    if (isMatrix) {
      matrix = Array.from({ length: 16 }, (_, i) => f(i));
    } else if (isQuat) {
      const len = Math.hypot(f(0), f(1), f(2), f(3)) || 1;
      const [x, y, z, w] = [f(0) / len, f(1) / len, f(2) / len, f(3) / len];
      const s = f(11);
      matrix = [
        (1 - 2 * (y * y + z * z)) * s, 2 * (x * y + w * z) * s, 2 * (x * z - w * y) * s, 0,
        2 * (x * y - w * z) * s, (1 - 2 * (x * x + z * z)) * s, 2 * (y * z + w * x) * s, 0,
        2 * (x * z + w * y) * s, 2 * (y * z - w * x) * s, (1 - 2 * (x * x + y * y)) * s, 0,
        f(8), f(9), f(10), 1,
      ];
    } else {
      continue;
    }
    if (matrix.every(Number.isFinite)) { out.push({ modelName: owner.name, matrix }); }
  }
  return out;
}

function subtreeHasVisuals(node: GeoNode): boolean {
  if (node.children.some(c => c.name === 'visuals')) { return true; }
  return node.children.some(c => c.name === 'children' && c.children.some(subtreeHasVisuals));
}

// The GEO node that defines an instance's model: the placed node itself when
// its subtree owns visuals, else the model's node in its own library scene
// (TLLM_GEO for a turret placed by MP53_GEO), else any scene that has it.
export function definitionNodeFor(
  instanceNode: GeoNode,
  modelName: string,
  ownScene: GeoScene | null,
  allScenes: GeoScene[],
): GeoNode {
  if (subtreeHasVisuals(instanceNode)) { return instanceNode; }
  const tryScene = (scene: GeoScene | null): GeoNode | null => {
    if (!scene) { return null; }
    const node = findSceneNode(scene, modelName);
    return node && subtreeHasVisuals(node) ? node : null;
  };
  return tryScene(ownScene) ?? allScenes.map(tryScene).find(Boolean) ?? instanceNode;
}
