// Map-side helpers shared by the page and its panels: outliner layers, placement math and text formatting.

import type { MapInstanceWire, MapPointWire } from './webview/model-editor/asset-client';
import type { MapLayer } from './viewport/mapViewport';

export const LAYERS: Array<{ id: MapLayer; label: string }> = [
  { id: 'world', label: 'World' },
  { id: 'reference', label: 'Props' },
  { id: 'clutter', label: 'Clutter' },
  { id: 'sky', label: 'Sky' },
  { id: 'points', label: 'Points' },
];

export interface ModelGroup { source: string; model: string; indices: number[] }

export function layerOf(inst: MapInstanceWire): MapLayer {
  if (inst.kind === 'clutter') { return 'clutter'; }
  if (inst.kind === 'model') { return 'world'; }
  if (/^(sky|thesun|flare|mist)/i.test(inst.model)) { return 'sky'; }
  return 'reference';
}

// Props and clutter move; the world model and sky stay put.
export function movable(inst: MapInstanceWire | undefined): boolean {
  return !!inst && (inst.clutterIndex !== null || (inst.geoIndex !== null && layerOf(inst) === 'reference'));
}

// Bridge edit for one placement: clutter by index, GEO props by node index.
export function placementOp(inst: MapInstanceWire, fields: Record<string, unknown>): Record<string, unknown> | null {
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
  return kind === 'start' ? 0x46a758 : kind === 'waypoint' ? 0x3e8bff : 0xb4b4b4;
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
