// Map-side helpers shared by the page and its panels: outliner layers, placement math and text formatting.

import type { MapInstanceWire, MapPointWire, MapProblem } from './webview/model-editor/asset-client';
import type { MapLayer } from './viewport/mapViewport';

export const LAYERS: Array<{ id: MapLayer; label: string }> = [
  { id: 'world', label: 'World' },
  { id: 'reference', label: 'Props' },
  { id: 'clutter', label: 'Clutter' },
  { id: 'sky', label: 'Sky' },
  { id: 'points', label: 'Points' },
  // Star Wars: Battlefront only (listed when the map has them).
  { id: 'paths', label: 'Paths' },
  { id: 'regions', label: 'Regions' },
];

// Combined Assault and Fireteam Bravo 1 and 2 (PSP) share the CA session: ISLE worlds, placements and points only.
// Fireteam Bravo 3 is another engine with its own (view-only) session.
export const isCaFamily = (game?: string | null) => game === 'SOCOM 3 / Combined Assault' || /^SOCOM: Fireteam Bravo( 2)?$/.test(game ?? '');
export const isFtb3 = (game?: string | null) => game === 'SOCOM: Fireteam Bravo 3';

export interface ModelGroup { source: string; model: string; indices: number[] }

// Sky models go by many names (sky, sky_m72, mp62_sky, alaska4d_sky, sky_follow, sun); skylines and skylights are scenery.
const SKY_NAME = /^(thesun|flare|mist)|^sun$|sky(?!line|light|scraper|way)/i;
// Most maps draw their sky inside the world model: a skydome / sky / cloudlayer node, or a node with sky or cloud textures.
const SKY_PART = /sky(?!line|light|scraper)|cloud|whispy/i;
// Retail maps also keep celestial cards outside the dome node (MP7's moon / stars,
// MP53 and MP71's sunglow). Match complete names so signs and lamps stay scenery.
const CELESTIAL_PART = /^(?:moon|stars?|sun(?:glow)?)(?:\d+)?(?:_\d+)*(?:\.tif)?$/i;

export const isSkyModel = (name: string) => SKY_NAME.test(name);

/** Whether a world-model chunk is part of the sky, by its GEO node's name or its texture. */
export const isSkyChunk = (chunk: { node?: string | null; texture: string | null }) =>
  SKY_PART.test(chunk.node ?? '') || SKY_PART.test(chunk.texture ?? '')
  || CELESTIAL_PART.test(chunk.node ?? '') || CELESTIAL_PART.test(chunk.texture ?? '');

export function layerOf(inst: MapInstanceWire): MapLayer {
  if (inst.kind === 'clutter') { return 'clutter'; }
  if (isSkyModel(inst.model)) { return 'sky'; }
  if (inst.kind === 'model') { return 'world'; }
  return 'reference';
}

// CA draws individual ISLE nodes in the world layer.
export function movable(inst: MapInstanceWire | undefined): boolean {
  return !!inst && (!!inst.caNode || inst.clutterIndex !== null || (inst.geoIndex !== null && layerOf(inst) === 'reference'));
}

// Bridge edit for one placement: clutter by index, GEO props by node index.
export function placementOp(inst: MapInstanceWire, fields: Record<string, unknown>): Record<string, unknown> | null {
  if (inst.caNode) { return { op: 'moveInstance', instance: inst.instance, ...fields }; }
  if (inst.clutterIndex !== null) { return { op: 'moveClutter', clutterIndex: inst.clutterIndex, ...fields }; }
  if (inst.geoIndex !== null) { return { op: 'moveInstance', geoIndex: inst.geoIndex, ...fields }; }
  return null;
}

// Deletes for a selection, highest index first so earlier deletes do not shift later ones.
export function deleteOps(sel: MapInstanceWire[]): Record<string, unknown>[] {
  const geo = sel.filter(i => i.geoIndex !== null && i.clutterIndex === null).sort((a, b) => b.geoIndex! - a.geoIndex!).map(i => ({ op: 'deleteInstance', geoIndex: i.geoIndex }));
  const clutter = sel.filter(i => i.clutterIndex !== null).sort((a, b) => b.clutterIndex! - a.clutterIndex!).map(i => ({ op: 'deleteClutter', clutterIndex: i.clutterIndex }));
  return [...geo, ...clutter];
}

// Copies for a selection. GEO copies insert nodes: go from the highest index down so the earlier indices stay valid.
export function duplicateOps(sel: MapInstanceWire[]): Record<string, unknown>[] {
  const geo = sel.filter(i => i.clutterIndex === null).sort((a, b) => b.geoIndex! - a.geoIndex!).map(i => ({ op: 'duplicateInstance', geoIndex: i.geoIndex }));
  const clutter = sel.filter(i => i.clutterIndex !== null).map(i => ({ op: 'addClutter', model: i.model, matrix: i.matrix }));
  return [...geo, ...clutter];
}

// The clutter indices an addClutter edit (or a batch of them) answered.
export function addedClutter(raw: { newClutterIndex?: number; results?: Array<{ newClutterIndex?: number } | null> }): Set<number> {
  return new Set([raw.newClutterIndex, ...(raw.results ?? []).map(r => r?.newClutterIndex)].filter((v): v is number => typeof v === 'number' && v >= 0));
}

// The copies after a duplicate: the added clutter, and the GEO instances that were not there before. A GEO copy has its
// original's path and lands right after it, so walking the old and new lists in order leaves the copies as extras.
export function pickDuplicates(before: MapInstanceWire[], after: MapInstanceWire[], newClutter: Set<number>): number[] {
  const beforeGeo = before.filter(i => i.clutterIndex === null).map(i => `${i.model}|${i.path}`);
  const picked: number[] = [];
  let k = 0;
  after.forEach((inst, i) => {
    if (inst.clutterIndex !== null) { if (newClutter.has(inst.clutterIndex)) { picked.push(i); } return; }
    if (k < beforeGeo.length && beforeGeo[k] === `${inst.model}|${inst.path}`) { k++; } else { picked.push(i); }
  });
  return picked;
}

export function yawOf(m: number[]): number { return Math.atan2(-m[2], m[0]) * 180 / Math.PI; }
export function scaleOf(m: number[]): number { return Math.hypot(m[0], m[1], m[2]); }
export function fmt(v: number): string { return (Math.abs(v) < 1e-4 ? 0 : v).toFixed(2); }
export function fileName(p: string): string { return p.split(/[\\/]/).pop() ?? p; }
export function errText(err: unknown): string { return err instanceof Error ? err.message : String(err); }

export function pointColor(kind: string): number {
  return kind === 'start' ? 0x46a758 : kind === 'waypoint' ? 0x3e8bff : kind === 'marker' ? 0xe0a030
    : kind === 'command post' ? 0xffd23e : kind === 'vehicle spawn' ? 0xd070ff : 0xb4b4b4;
}

const MISSION_KINDS: Record<string, string> = { SP: 'campaign', MP: 'online', IA: 'instant action', TR: 'training', custom: 'custom' };

/** A SOCOM 3 / CA mission for the picker: "MP414CTR · online CTR", "SP414 · campaign". */
export function missionLabel(m: { id: string; kind: string; mode: string; label?: string }): string {
  if (m.label) { return m.label; }
  const kind = MISSION_KINDS[m.kind];
  return kind ? `${m.id} · ${kind}${m.mode ? ` ${m.mode}` : ''}` : m.id;
}

export function pointKindCounts(points: MapPointWire[]): Array<{ kind: string; count: number }> {
  const by = new Map<string, number>();
  for (const p of points) { by.set(p.kind, (by.get(p.kind) ?? 0) + 1); }
  return [...by].map(([kind, count]) => ({ kind, count }));
}

// A model can occur as both a prop and clutter. Each draw batch needs one layer so visibility stays independent.
export function groupInstancesByLayer(instances: MapInstanceWire[]): Array<{ source: string; layer: MapLayer; indices: number[] }> {
  const sources = new Map<string, Map<MapLayer, number[]>>();
  instances.forEach((instance, index) => {
    if (!instance.source) { return; }
    const layers = sources.get(instance.source) ?? new Map<MapLayer, number[]>();
    const layer = layerOf(instance);
    const indices = layers.get(layer) ?? [];
    indices.push(index);
    layers.set(layer, indices);
    sources.set(instance.source, layers);
  });
  return [...sources].flatMap(([source, layers]) => [...layers].map(([layer, indices]) => ({ source, layer, indices })));
}

// What a problem points at in the loaded scene: an instance index, a point name, or nothing to select.
export function problemTarget(problem: MapProblem, instances: MapInstanceWire[]): { instance: number } | { point: string } | null {
  if (problem.point) { return { point: problem.point }; }
  const i = typeof problem.clutterIndex === 'number' ? instances.findIndex(x => x.clutterIndex === problem.clutterIndex)
    : typeof problem.geoIndex === 'number' ? instances.findIndex(x => x.clutterIndex === null && x.geoIndex === problem.geoIndex)
      : -1;
  return i >= 0 ? { instance: i } : null;
}

// Errors first, then warnings, then info; the order within a severity is kept.
export function sortProblems(problems: MapProblem[]): MapProblem[] {
  const rank = { error: 0, warning: 1, info: 2 } as const;
  return problems.map((p, i) => ({ p, i })).sort((a, b) => rank[a.p.severity] - rank[b.p.severity] || a.i - b.i).map(x => x.p);
}
