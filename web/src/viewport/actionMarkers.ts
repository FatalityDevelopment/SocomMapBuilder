// Which drawn props carry a map object action (actions.rdr), for the icons floating above them in the 3D view.
// The game binds a row by the name of a placed GEO node, which is often the parent of the visible mesh (a door node
// with its frame and leaf below it), so a marker covers the node and every placement under it.

import { actionKind, type ActionKind } from '../shared/actionIcons';

export interface ActionTargetLike { index: number; name: string; path: string; anchor?: ReadonlyArray<number> | null; model?: string; turret?: boolean }
export interface ActionDataLike {
  rows: ReadonlyArray<Record<string, ReadonlyArray<string | number>>>;
  targets: ReadonlyArray<ActionTargetLike>;
}

export interface ActionMarker {
  /** The bound GEO node (the scene node the row names). */
  geoIndex: number;
  node: string;
  kind: ActionKind;
  /** actions.rdr rows bound to this node, first one decides the icon (none for a turret, which the game prompts by itself). */
  rows: number[];
  /** Map instances (indices into the scene's instance list) drawn under the node. */
  instances: number[];
  /** For a node that draws nothing of its own (a door whose leaf is in the world model): where its icon stands. */
  anchor?: [number, number, number];
}

/** One marker per bound node: over what it draws, else at the node's own place. */
export function actionMarkers(data: ActionDataLike, instances: ReadonlyArray<{ geoIndex: number | null }>): ActionMarker[] {
  const byGeo = new Map<number, number[]>();
  instances.forEach((inst, i) => {
    if (inst.geoIndex == null) { return; }
    const list = byGeo.get(inst.geoIndex);
    if (list) { list.push(i); } else { byGeo.set(inst.geoIndex, [i]); }
  });
  const markers = new Map<number, ActionMarker>();
  const place = (target: ActionTargetLike, kind: ActionKind, rows: number[]) => {
    const under = data.targets.filter(t => t.index === target.index || t.path.startsWith(target.path + '/'));
    const drawn = under.flatMap(t => byGeo.get(t.index) ?? []);
    const anchor = target.anchor && target.anchor.length === 3 && target.anchor.every(Number.isFinite) ? [target.anchor[0], target.anchor[1], target.anchor[2]] as [number, number, number] : undefined;
    if (!drawn.length && !anchor) { return; }
    markers.set(target.index, { geoIndex: target.index, node: target.name, kind, rows, instances: [...new Set(drawn)].sort((a, b) => a - b),
      ...(drawn.length ? {} : { anchor }) });
  };
  data.rows.forEach((row, r) => {
    const name = String(row.node?.[0] ?? '');
    if (!name) { return; }
    let bound = data.targets.filter(t => t.name === name);
    if (!bound.length) { bound = data.targets.filter(t => t.name.toLowerCase() === name.toLowerCase()); }
    for (const target of bound) {
      const existing = markers.get(target.index);
      if (existing) { existing.rows.push(r); continue; }
      place(target, actionKind(row), [r]);
    }
  });
  // Turrets: one marker on the node that holds the turret model (mp73turret_1 above RMG_turret_ledge), unless an
  // action row already marks it or the model itself.
  for (const holder of turretHolders(data.targets)) {
    const marked = [...markers.values()].some(m => m.geoIndex === holder.index || data.targets.some(t => t.index === m.geoIndex && holder.path.startsWith(t.path + '/')));
    if (!marked) { place(holder, 'TURRET', []); }
  }
  return [...markers.values()];
}

/** The nodes that stand for a placed turret: the bare parent holding a turret model (the name ai_turrets.rdr uses),
 *  or the model's own node when it sits at the top of the map. */
export function turretHolders(targets: ReadonlyArray<ActionTargetLike>): ActionTargetLike[] {
  const out = new Map<number, ActionTargetLike>();
  for (const t of targets) {
    if (!t.turret) { continue; }
    const cut = t.path.lastIndexOf('/');
    const parent = cut > 0 ? targets.find(p => p.path === t.path.slice(0, cut)) : undefined;
    const holder = parent && !parent.model && !parent.turret ? parent : t;
    out.set(holder.index, holder);
  }
  return [...out.values()];
}

/** The world position of a marker: centred over the visible instances' boxes, a little above their top. */
export function markerAnchor(boxes: ReadonlyArray<{ min: number[]; max: number[] }>, lift: number): [number, number, number] | null {
  if (!boxes.length) { return null; }
  const min = [Infinity, Infinity, Infinity], max = [-Infinity, -Infinity, -Infinity];
  for (const b of boxes) { for (let k = 0; k < 3; k++) { min[k] = Math.min(min[k], b.min[k]); max[k] = Math.max(max[k], b.max[k]); } }
  if (!min.every(Number.isFinite) || !max.every(Number.isFinite)) { return null; }
  return [(min[0] + max[0]) / 2, max[1] + lift, (min[2] + max[2]) / 2];
}
