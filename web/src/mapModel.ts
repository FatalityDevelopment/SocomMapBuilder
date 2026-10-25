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
