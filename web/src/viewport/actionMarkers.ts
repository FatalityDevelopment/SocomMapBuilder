// Which drawn props carry a map object action (actions.rdr), for the icons floating above them in the 3D view.
// The game binds a row by the name of a placed GEO node, which is often the parent of the visible mesh (a door node
// with its frame and leaf below it), so a marker covers the node and every placement under it.

import { actionKind, type ActionKind } from '../shared/actionIcons';

export interface ActionTargetLike { index: number; name: string; path: string; anchor?: ReadonlyArray<number> | null }
export interface ActionDataLike {
  rows: ReadonlyArray<Record<string, ReadonlyArray<string | number>>>;
  targets: ReadonlyArray<ActionTargetLike>;
}

export interface ActionMarker {
  /** The bound GEO node (the scene node the row names). */
  geoIndex: number;
  node: string;
  kind: ActionKind;
  /** actions.rdr rows bound to this node, first one decides the icon. */
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
  data.rows.forEach((row, r) => {
    const name = String(row.node?.[0] ?? '');
    if (!name) { return; }
    let bound = data.targets.filter(t => t.name === name);
    if (!bound.length) { bound = data.targets.filter(t => t.name.toLowerCase() === name.toLowerCase()); }
    for (const target of bound) {
      const existing = markers.get(target.index);
      if (existing) { existing.rows.push(r); continue; }
      const under = data.targets.filter(t => t.index === target.index || t.path.startsWith(target.path + '/'));
      const drawn = under.flatMap(t => byGeo.get(t.index) ?? []);
      const anchor = target.anchor && target.anchor.length === 3 && target.anchor.every(Number.isFinite) ? [target.anchor[0], target.anchor[1], target.anchor[2]] as [number, number, number] : undefined;
      if (!drawn.length && !anchor) { continue; }
      markers.set(target.index, { geoIndex: target.index, node: target.name, kind: actionKind(row), rows: [r], instances: [...new Set(drawn)].sort((a, b) => a - b),
        ...(drawn.length ? {} : { anchor }) });
    }
  });
  return [...markers.values()];
}

/** The world position of a marker: centred over the visible instances' boxes, a little above their top. */
export function markerAnchor(boxes: ReadonlyArray<{ min: number[]; max: number[] }>, lift: number): [number, number, number] | null {
  if (!boxes.length) { return null; }
  const min = [Infinity, Infinity, Infinity], max = [-Infinity, -Infinity, -Infinity];
  for (const b of boxes) { for (let k = 0; k < 3; k++) { min[k] = Math.min(min[k], b.min[k]); max[k] = Math.max(max[k], b.max[k]); } }
  if (!min.every(Number.isFinite) || !max.every(Number.isFinite)) { return null; }
  return [(min[0] + max[0]) / 2, max[1] + lift, (min[2] + max[2]) / 2];
}
