// Parser for Zipper *_GEO.ZED scene-graph files.
//
// Format observation: every ZedNode's `type` field is the count of immediate
// children that follow it in the flat entry list. A pre-order walk that
// consumes `type` children after each parent reconstructs the tree exactly.
//
// Typical node names:
//   models           container at the top
//   <object_name>    a scene node (e.g. car_benz_black, m4Acarbine_203)
//   nparams          96-byte payload = 4x4 row-major matrix + extras
//   children         group holding sub-objects
//   visuals          group holding visual instances
//   vis              a single visual instance
//   vparams          24-byte payload = visual params (mesh ref + uv data)

import { base64ToBytes } from './zed-texture-parser';

export interface NodeParams {
  matrix: number[]; // 16 floats, row-major
  translation: [number, number, number];
  extra: Float32Array; // remaining floats after the matrix
}

export interface VisualParams {
  rawBytes: Uint8Array;
  // vparams layout (SOCOM II, from the pack builders in Mods/SOCOMCA):
  //   u32 flags, u32 lod, u16 triangles, u16 vertices, float centre x/y/z
  // flags 0x000BC0FA = weapon (positions normalised by the chunk scale),
  // 0x0009C0FA = prop / vehicle / map object (positions in 1/16 units).
  flags?: number;
  triangleCount?: number;
  vertexCount?: number;
  centre?: [number, number, number];
}

// vparams flag bit: vertex positions are int16 / 32768 * chunk scale (set on
// weapons) instead of int16 * scale / 16 (props, vehicles, map objects).
export const VPARAMS_FLAG_SCALE_NORMALISED = 0x20000;

export type GeoPositionUnit = 'scale' | 'sixteenth';

export interface GeoNode {
  name: string;
  type: number;
  index: number; // index in original entries
  size: number;
  rawData: Uint8Array | null;
  children: GeoNode[];
  // Parsed convenience fields when applicable.
  nparams?: NodeParams;
  vparams?: VisualParams;
}

export interface GeoEntry {
  index: number;
  name: string;
  type: number;
  // Payload as base64 (bridge responses) or raw bytes (archives already
  // parsed in the webview).
  data: string | Uint8Array;
}

export interface GeoScene {
  root: GeoNode;
  byName: Map<string, GeoNode[]>; // names can repeat (e.g. "nparams")
}

function parseNodeParams(bytes: Uint8Array): NodeParams | undefined {
  if (bytes.length < 64) { return undefined; }
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  const matrix: number[] = [];
  for (let i = 0; i < 16; i++) {
    matrix.push(view.getFloat32(i * 4, true));
  }
  // Row-major: translation is in the last row (m30, m31, m32) for SOCOM's
  // convention based on inspection (BENZ identity matrix has -20, 0, -40 at
  // +0x40 in a separate vec4, but +0x30..+0x3C is 0,0,0,1 = identity row).
  // We expose translation from row 3 if it differs from identity; otherwise
  // callers can read .extra.
  const translation: [number, number, number] = [matrix[12], matrix[13], matrix[14]];
  const extraFloats = Math.floor((bytes.length - 64) / 4);
  const extra = new Float32Array(extraFloats);
  for (let i = 0; i < extraFloats; i++) {
    extra[i] = view.getFloat32(64 + i * 4, true);
  }
  return { matrix, translation, extra };
}

function parseVisualParams(bytes: Uint8Array): VisualParams {
  const out: VisualParams = { rawBytes: bytes };
  if (bytes.length >= 12) {
    const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    out.flags = view.getUint32(0, true);
    out.triangleCount = view.getUint16(8, true);
    out.vertexCount = view.getUint16(10, true);
    if (bytes.length >= 24) {
      out.centre = [view.getFloat32(12, true), view.getFloat32(16, true), view.getFloat32(20, true)];
    }
  }
  return out;
}

// First vparams flags word found under `node` (its own visuals first, then
// its descendants). Null when the subtree has no visuals.
export function readVisualFlags(node: GeoNode | null): number | null {
  if (!node) { return null; }
  const visuals = node.children.find(c => c.name === 'visuals');
  if (visuals) {
    for (const vis of visuals.children) {
      const vparams = vis.children.find(c => c.name === 'vparams');
      if (vparams?.vparams?.flags !== undefined) { return vparams.vparams.flags; }
    }
  }
  for (const c of node.children) {
    const found = readVisualFlags(c);
    if (found !== null) { return found; }
  }
  return null;
}

// nparams layout (SOCOM II): 16 floats of 4x4 matrix, then bounds min (3),
// bounds max (3), then two words (node kind, flags). The bounds are in
// units, which makes them a yardstick for the position-unit convention.
export interface GeoNodeBounds {
  min: [number, number, number];
  max: [number, number, number];
  extent: number;
}

export function readNodeBounds(node: GeoNode | null): GeoNodeBounds | null {
  if (!node) { return null; }
  const nparams = node.children.find(c => c.name === 'nparams');
  const data = nparams?.rawData;
  if (!data || data.length < 22 * 4) { return null; }
  const view = new DataView(data.buffer, data.byteOffset, data.byteLength);
  const f = (i: number) => view.getFloat32(i * 4, true);
  const min: [number, number, number] = [f(16), f(17), f(18)];
  const max: [number, number, number] = [f(19), f(20), f(21)];
  if (![...min, ...max].every(Number.isFinite)) { return null; }
  const extent = Math.max(max[0] - min[0], max[1] - min[1], max[2] - min[2]);
  if (!(extent > 0) || extent > 1e6) { return null; }
  return { min, max, extent };
}

// Position unit implied by a GEO node's visual flags, or null when unknown.
export function positionUnitForNode(node: GeoNode | null): GeoPositionUnit | null {
  const flags = readVisualFlags(node);
  if (flags === null) { return null; }
  return (flags & VPARAMS_FLAG_SCALE_NORMALISED) !== 0 ? 'scale' : 'sixteenth';
}

// Walk the flat entry list using `type` as the child count for the next N
// siblings. Returns a single synthetic root that owns all top-level entries.
export function parseGeoScene(entries: GeoEntry[]): GeoScene {
  const decoded: Array<{ entry: GeoEntry; data: Uint8Array | null }> = entries.map(e => ({
    entry: e,
    data: !e.data
      ? null
      : typeof e.data === 'string'
        ? base64ToBytes(e.data)
        : (e.data.length > 0 ? e.data : null),
  }));

  const byName = new Map<string, GeoNode[]>();

  const buildNode = (cursor: { i: number }): GeoNode | null => {
    if (cursor.i >= decoded.length) { return null; }
    const { entry, data } = decoded[cursor.i];
    const node: GeoNode = {
      name: entry.name,
      type: entry.type,
      index: entry.index,
      size: data ? data.length : 0,
      rawData: data,
      children: [],
    };
    cursor.i++;

    if (data && data.length >= 64 && entry.name === 'nparams') {
      node.nparams = parseNodeParams(data);
    }
    if (data && entry.name === 'vparams') {
      node.vparams = parseVisualParams(data);
    }

    const childCount = entry.type;
    for (let k = 0; k < childCount && cursor.i < decoded.length; k++) {
      const child = buildNode(cursor);
      if (child) { node.children.push(child); }
    }

    const list = byName.get(node.name);
    if (list) { list.push(node); } else { byName.set(node.name, [node]); }
    return node;
  };

  const root: GeoNode = {
    name: '<root>',
    type: 0,
    index: -1,
    size: 0,
    rawData: null,
    children: [],
  };
  const cursor = { i: 0 };
  while (cursor.i < decoded.length) {
    const node = buildNode(cursor);
    if (node) { root.children.push(node); }
  }
  return { root, byName };
}

// Find a named scene node that matches a given model entry name. Looks for the
// node within the 'models' container, falling back to any top-level match.
export function findSceneNode(scene: GeoScene, modelName: string): GeoNode | null {
  const targets = scene.byName.get(modelName);
  if (!targets || targets.length === 0) { return null; }
  // Prefer one that has a 'visuals' or 'children' descendant.
  for (const node of targets) {
    if (hasMeshData(node)) { return node; }
  }
  return targets[0];
}

function hasMeshData(node: GeoNode): boolean {
  for (const c of node.children) {
    if (c.name === 'visuals' || c.name === 'children') { return true; }
    if (hasMeshData(c)) { return true; }
  }
  return false;
}

// Produce a flat list of named "parts" (leaf visual nodes) for chunk labelling.
// For weapons this yields ['m4_high', 'm4_low', 'firepoint', ...] in encounter
// order, which is roughly the order chunks appear in the MDL payload.
export function collectVisualPartNames(node: GeoNode | null): string[] {
  if (!node) { return []; }
  const names: string[] = [];
  const walk = (n: GeoNode) => {
    // A "part" is a named scene node (type >= 1, has nparams) that ultimately
    // owns one or more `vis` entries.
    const isPart = n.children.some(c => c.name === 'nparams' || c.name === 'visuals');
    if (isPart && n.name !== node.name) {
      names.push(n.name);
    }
    for (const c of n.children) { walk(c); }
  };
  for (const c of node.children) { walk(c); }
  return names;
}

// ---------------------------------------------------------------------------
// GEO scene composition (HTML map viewer parity). Walks the scene tree and
// emits one placement record per node that names a model (via `model_name` or
// implied by a `visuals` child). Each placement carries the resolved 4x4
// matrix from its `nparams` child so the caller can transform the model's
// vertices into world space.
// ---------------------------------------------------------------------------

export interface GeoInstance {
  // The cleaned name of the placed-into-the-scene model (e.g. "car_benz_black").
  modelName: string;
  // The name of the scene node itself (sometimes "{var} car_benz_black").
  instanceName: string;
  // Slash-joined breadcrumb for diagnostics (e.g. "models/worldmodel/children/objects/inst_42").
  path: string;
  // The 4x4 transform pulled from nparams. Column-major (matches HTML reference).
  matrix: number[];
  // Source GEO node for further inspection.
  node: GeoNode;
  // Position unit from the node's vparams flags (null when it has none).
  positionUnit: GeoPositionUnit | null;
}

export interface CollectGeoInstancesOptions {
  accumulateTransforms?: boolean;
}

function safeFindChild(node: GeoNode, name: string): GeoNode | null {
  return node.children.find(c => c.name === name) ?? null;
}

function cleanInstanceName(value: string): string {
  let s = (value ?? '').trim();
  const brace = s.indexOf('{');
  if (brace >= 0) {
    const end = s.indexOf('}', brace + 1);
    if (end > brace) { s = s.slice(brace + 1, end).trim(); }
    else { s = s.slice(0, brace).trim(); }
  }
  return s;
}

function bytesToAscii(data: Uint8Array): string {
  let s = '';
  for (let i = 0; i < data.length; i++) {
    const b = data[i];
    if (b === 0) break;
    if (b >= 0x20 && b < 0x7f) { s += String.fromCharCode(b); }
  }
  return s.trim();
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

function looksLikeTransform(m: number[]): boolean {
  if (!m.every(Number.isFinite)) { return false; }
  const sx = Math.hypot(m[0], m[1], m[2]);
  const sy = Math.hypot(m[4], m[5], m[6]);
  const sz = Math.hypot(m[8], m[9], m[10]);
  const saneScale = sx > 0.001 && sy > 0.001 && sz > 0.001 && sx < 500 && sy < 500 && sz < 500;
  const saneBottom = Math.abs(m[3]) < 0.01 && Math.abs(m[7]) < 0.01 && Math.abs(m[11]) < 0.01 && Math.abs(Math.abs(m[15]) - 1) < 0.25;
  const saneTranslate = Math.abs(m[12]) < 100000 && Math.abs(m[13]) < 100000 && Math.abs(m[14]) < 100000;
  return saneScale && saneBottom && saneTranslate;
}

export function readGeoTransform(node: GeoNode): number[] {
  const nparams = safeFindChild(node, 'nparams');
  if (!nparams || !nparams.rawData || nparams.rawData.length < 0x40) { return identityMatrix(); }
  const data = nparams.rawData;
  const view = new DataView(data.buffer, data.byteOffset, data.byteLength);
  const f: number[] = [];
  const max = Math.min(16, Math.floor(data.length / 4));
  for (let i = 0; i < max; i++) { f.push(view.getFloat32(i * 4, true)); }
  while (f.length < 16) { f.push(0); }
  // A valid rotated PS2 placement matrix can have m00 close to zero, so use
  // full transform sanity checks instead of treating that as junk.
  if (!looksLikeTransform(f)) { return identityMatrix(); }
  return f;
}

// Column-major matrix-vector multiply: row[k] = sum(m[k + col*4] * v[col]).
export function transformPoint(m: number[], x: number, y: number, z: number): [number, number, number] {
  return [
    x * m[0] + y * m[4] + z * m[8] + m[12],
    x * m[1] + y * m[5] + z * m[9] + m[13],
    x * m[2] + y * m[6] + z * m[10] + m[14],
  ];
}

function formatGeoPath(node: GeoNode, root: GeoNode): string {
  // Walk from `node` up to `root` via parent links? The flat tree doesn't
  // carry parent refs; do a DFS-with-trail instead.
  const trail: string[] = [];
  const walk = (n: GeoNode, acc: string[]): boolean => {
    acc.push(n.name);
    if (n === node) { trail.push(...acc); return true; }
    for (const c of n.children) {
      if (walk(c, acc)) { return true; }
    }
    acc.pop();
    return false;
  };
  walk(root, []);
  return trail.join('/');
}

// Walks the scene tree for nodes that look like placed models — they either
// carry a `model_name` child (explicit) or a `visuals` child (implicit, name
// comes from the node itself). Skips the sky shell because it usually
// dominates bounds and hides the actual playable area.
export function collectGeoInstances(scene: GeoScene, root: GeoNode, options: CollectGeoInstancesOptions = {}): GeoInstance[] {
  const out: GeoInstance[] = [];
  const walk = (node: GeoNode, parentMatrix: number[]) => {
    const localMatrix = readGeoTransform(node);
    const worldMatrix = options.accumulateTransforms
      ? multiplyMatrices(parentMatrix, localMatrix)
      : localMatrix;
    const explicitChild = safeFindChild(node, 'model_name');
    const explicitName = explicitChild?.rawData ? bytesToAscii(explicitChild.rawData) : '';
    const hasVisuals = safeFindChild(node, 'visuals') !== null;
    const cleanNode = cleanInstanceName(node.name);
    const candidate = explicitName || (hasVisuals ? cleanNode : '');

    if (candidate && cleanNode.toLowerCase() !== 'sky' && candidate.toLowerCase() !== 'sky') {
      out.push({
        modelName: candidate,
        instanceName: cleanNode,
        path: formatGeoPath(node, scene.root),
        matrix: worldMatrix,
        node,
        positionUnit: positionUnitForNode(node),
      });
    }

    for (const c of node.children) { walk(c, worldMatrix); }
  };
  walk(root, identityMatrix());

  // Dedup by path+modelName so a model placed in multiple equivalent ways
  // doesn't get rendered twice.
  const seen = new Set<string>();
  return out.filter(inst => {
    const key = `${inst.path}|${inst.modelName.toLowerCase()}`;
    if (seen.has(key)) { return false; }
    seen.add(key);
    return true;
  });
}

// Loose name matching for resolving an instance's `modelName` to an actual
// model entry across every loaded *_MDL.ZED. Mirrors the HTML's findModelFor
// Instance + preferModelHit cascade.
export function compactName(value: string): string {
  let out = '';
  const s = String(value).toLowerCase();
  for (let i = 0; i < s.length; i++) {
    const c = s.charCodeAt(i);
    if ((c >= 48 && c <= 57) || (c >= 97 && c <= 122)) { out += s[i]; }
  }
  return out;
}

// Strip LOD / index suffixes from model names so a placement that asks for
// "car_benz_01" can resolve to a node called "car_benz". Matches the new
// HTML map viewer's stripModelSuffix.
export function stripModelSuffix(name: string): string {
  let s = String(name ?? '').trim();
  s = s.replace(/_LO$/i, '');
  s = s.replace(/_[0-9]+$/i, '');
  return s;
}


// Known cross-convention aliases seen in weapon/map packages where the GEO
// instance name (iw*/ra*/etc) differs from the MDL node naming family.
const MODEL_NAME_ALIASES: Record<string, string[]> = {
  // M51 packages often place iw80a2 instances that resolve to m16a2 meshes.
  iw80a2: ['m16a2', 'm16', 'mesh_m16a2', 'mesh_m16'],
  mesh_iw80a2: ['m16a2', 'm16', 'mesh_m16a2', 'mesh_m16'],
};

// Generate every name variant we should try when matching a GEO instance
// name to an MDL entry. Includes:
//   1. lowercased original
//   2. lowercased cleaned (braces stripped)
//   3. compact (alphanumerics only)
//   4. stripped suffix variants of the above
//   5. MESH_-prefixed variants (CLIB convention)
export function instanceNameVariants(rawName: string): string[] {
  const clean = cleanInstanceName(rawName);
  const variants = new Set<string>();
  const addAll = (base: string) => {
    const lower = base.toLowerCase();
    if (!lower) { return; }
    variants.add(lower);
    variants.add(compactName(lower));
    const stripped = stripModelSuffix(lower);
    variants.add(stripped);
    variants.add(compactName(stripped));
    variants.add('mesh_' + lower);
    variants.add('mesh_' + stripped);
    variants.add(compactName('mesh' + lower));
    variants.add(compactName('mesh' + stripped));
  };
  addAll(rawName);
  addAll(clean);

  const aliasKeys = [String(rawName ?? "").toLowerCase(), String(clean ?? "").toLowerCase()];
  for (const k of aliasKeys) {
    const aliases = MODEL_NAME_ALIASES[k];
    if (!aliases) { continue; }
    for (const a of aliases) { addAll(a); }
  }

  variants.delete('');
  return [...variants];
}

// ---------------------------------------------------------------------------
// Per-visual placement. A model's N<node>_<visual> stubs (and the chunks the
// DMA chain parser tags with that key) belong to one GEO node: <node> counts
// the nodes of the model's GEO subtree that own a `visuals` group, in
// pre-order (root first, then `children` recursively); <visual> is the `vis`
// entry inside it. Chunk positions are in that node's local space, so each
// visual needs its node's accumulated matrix, and its own vparams unit.
// (Numbering confirmed by the SOCOM II pack builders in Mods/SOCOMCA.)
// ---------------------------------------------------------------------------

export interface VisualPlacement {
  // Node matrix accumulated from the model root (root's own local excluded),
  // column-major like readGeoTransform.
  matrix: number[];
  unit: GeoPositionUnit | null;
  nodeName: string;
}

export interface VisualPlacementMap {
  placements: Map<string, VisualPlacement>;
  // How <node> was counted: nodes owning visuals, or every node.
  mode: 'visualNodes' | 'allNodes';
  matched: number;
  total: number;
}

// N000_000 -> {0, 0}; N003_I001_V02 -> {3, 2} (first and last numbers).
export function parseVisualKey(key: string): { node: number; visual: number } | null {
  const m = /^N(\d+)(?:_[A-Z]*\d+)*_[A-Z]*(\d+)$/i.exec(key.trim());
  if (!m) { return null; }
  return { node: Number(m[1]), visual: Number(m[2]) };
}

interface PlacedNode {
  node: GeoNode;
  matrix: number[];
  vis: GeoNode[];
}

function walkModelNodes(root: GeoNode): PlacedNode[] {
  const out: PlacedNode[] = [];
  const visit = (node: GeoNode, matrix: number[]) => {
    const visuals = safeFindChild(node, 'visuals');
    out.push({ node, matrix, vis: visuals ? visuals.children.filter(c => c.name === 'vis') : [] });
    const children = safeFindChild(node, 'children');
    if (!children) { return; }
    for (const child of children.children) {
      visit(child, multiplyMatrices(matrix, readGeoTransform(child)));
    }
  };
  visit(root, identityMatrix());
  return out;
}

function unitForVis(vis: GeoNode): GeoPositionUnit | null {
  const vparams = vis.children.find(c => c.name === 'vparams');
  const flags = vparams?.vparams?.flags;
  if (flags === undefined) { return null; }
  return (flags & VPARAMS_FLAG_SCALE_NORMALISED) !== 0 ? 'scale' : 'sixteenth';
}

// Resolve every key to its node. Both numberings are tried and the one that
// places more keys wins (ties go to the visual-node numbering).
export function buildVisualPlacementMap(root: GeoNode, keys: string[]): VisualPlacementMap {
  const nodes = walkModelNodes(root);
  const visualNodes = nodes.filter(n => n.vis.length > 0);
  const unique = [...new Set(keys)];
  const attempt = (list: PlacedNode[]): Map<string, VisualPlacement> => {
    const map = new Map<string, VisualPlacement>();
    for (const key of unique) {
      const parsed = parseVisualKey(key);
      if (!parsed) { continue; }
      const target = list[parsed.node];
      if (!target || parsed.visual >= target.vis.length) { continue; }
      map.set(key, { matrix: target.matrix, unit: unitForVis(target.vis[parsed.visual]), nodeName: target.node.name });
    }
    return map;
  };
  const byVisual = attempt(visualNodes);
  const byAll = attempt(nodes);
  const useAll = byAll.size > byVisual.size;
  const placements = useAll ? byAll : byVisual;
  return { placements, mode: useAll ? 'allNodes' : 'visualNodes', matched: placements.size, total: unique.length };
}

// True when `node` sits inside the subtree of `ancestor` (or is it).
export function isInSubtree(ancestor: GeoNode, node: GeoNode): boolean {
  if (ancestor === node) { return true; }
  for (const c of ancestor.children) {
    if (isInSubtree(c, node)) { return true; }
  }
  return false;
}
