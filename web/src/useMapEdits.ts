// The Map Builder's placement edits: transform commits, Item-tab fields, delete / duplicate / copy / paste, drop to
// ground and the Place tool. Each one is a single session edit (a batch for several instances).

import { useRef, type MutableRefObject } from 'react';
import type { ModelSummary } from './webview/model-editor/asset-client';
import type { MapTool, MapViewport } from './viewport/mapViewport';
import type { PropTab } from './components/PropsPanel';
import { movable, placementOp, scaleOf, yawOf } from './mapModel';
import { SHORTCUTS } from './mapShortcuts';
import type { useMapSession } from './useMapSession';

export interface PlaceSettings { model: ModelSummary | null; yaw: number; scale: number; randomYaw: boolean }

export function useMapEdits(session: ReturnType<typeof useMapSession>, viewportRef: MutableRefObject<MapViewport | null>, selection: number[],
  setTool: (tool: MapTool) => void, setPropTab: (tab: PropTab) => void, placeSettings: PlaceSettings) {
  const { instances, instancesRef, pointsRef, edit, say } = session;
  const { model: placeModel, yaw: placeYaw, scale: placeScale, randomYaw } = placeSettings;
  // Copied clutter placements (model + matrix); they paste into this package or another one that has the model.
  const clipboardRef = useRef<Array<{ model: string; matrix: number[] }>>([]);

  const commitTransforms = async (items: Array<{ index: number; matrix: number[] }>, kind: string) => {
    const list = instancesRef.current;
    const ops = items.map(it => placementOp(list[it.index], { matrix: it.matrix.map(v => Math.fround(v)) })).filter((o): o is Record<string, unknown> => !!o);
    if (!ops.length) { return; }
    const label = `${kind} ${ops.length === 1 ? list[items[0].index].model : ops.length + ' instances'}`;
    await edit(label, ops.length === 1 ? ops[0] : { op: 'batch', ops });
  };

  const setField = async (field: 'position' | 'yaw' | 'scale', value: string, axis = 0) => {
    const inst = selection.length === 1 ? instances[selection[0]] ?? null : null;
    const v = parseFloat(value);
    if (!inst || !Number.isFinite(v)) { return; }
    const m = inst.matrix;
    const position = [m[12], m[13], m[14]];
    if (field === 'position') { position[axis] = v; }
    const yaw = field === 'yaw' ? v : yawOf(m);
    const scale = field === 'scale' ? Math.max(0.01, v) : scaleOf(m);
    const op = field === 'position' ? placementOp(inst, { position }) : placementOp(inst, { position, yaw, scale });
    if (op) { await edit(`set ${field} of ${inst.model}`, op); }
  };

  const movePointField = async (name: string, axis: 'x' | 'z', value: string) => {
    const p = pointsRef.current.find(x => x.name === name);
    const v = parseFloat(value);
    if (!p || !Number.isFinite(v)) { return; }
    await edit(`move point ${name}`, { op: 'movePoint', name, position: [axis === 'x' ? v : p.x, 0, axis === 'z' ? v : p.z] });
  };

  const selectedInstances = () => selection.map(i => instancesRef.current[i]).filter(i => movable(i));

  const deleteSelection = async () => {
    const sel = selectedInstances();
    if (!sel.length) { return; }
    // Highest index first so earlier deletes do not shift later ones.
    const geo = sel.filter(i => i.geoIndex !== null && i.clutterIndex === null).sort((a, b) => b.geoIndex! - a.geoIndex!).map(i => ({ op: 'deleteInstance', geoIndex: i.geoIndex }));
    const clutter = sel.filter(i => i.clutterIndex !== null).sort((a, b) => b.clutterIndex! - a.clutterIndex!).map(i => ({ op: 'deleteClutter', clutterIndex: i.clutterIndex }));
    const ops = [...geo, ...clutter];
    viewportRef.current?.clearSelection();
    await edit(`delete ${ops.length === 1 ? sel[0].model : ops.length + ' instances'}`, ops.length === 1 ? ops[0] : { op: 'batch', ops });
  };

  const duplicateSelection = async () => {
    const sel = selectedInstances();
    if (!sel.length) { return; }
    const list = instancesRef.current;
    const beforeGeo = list.filter(i => i.clutterIndex === null).map(i => `${i.model}|${i.path}`);
    // GEO copies insert nodes: go from the highest index down so the earlier indices stay valid.
    const geo = sel.filter(i => i.clutterIndex === null).sort((a, b) => b.geoIndex! - a.geoIndex!).map(i => ({ op: 'duplicateInstance', geoIndex: i.geoIndex }));
    const clutter = sel.filter(i => i.clutterIndex !== null).map(i => ({ op: 'addClutter', model: i.model, matrix: i.matrix }));
    const ops = [...geo, ...clutter];
    const raw = await edit(`duplicate ${ops.length === 1 ? sel[0].model : ops.length + ' instances'}`, ops.length === 1 ? ops[0] : { op: 'batch', ops }) as
      { newClutterIndex?: number; results?: Array<{ newClutterIndex?: number }> } | null;
    if (!raw) { return; }
    // Copies come back selected with the Move tool, ready to place.
    const newClutter = new Set([raw.newClutterIndex, ...(raw.results ?? []).map(r => r?.newClutterIndex)].filter((v): v is number => typeof v === 'number' && v >= 0));
    // A GEO copy has its original's path and lands right after it: walk old and new in order, the extras are copies.
    const picked: number[] = [];
    let k = 0;
    instancesRef.current.forEach((inst, i) => {
      if (inst.clutterIndex !== null) { if (newClutter.has(inst.clutterIndex)) { picked.push(i); } return; }
      if (k < beforeGeo.length && beforeGeo[k] === `${inst.model}|${inst.path}`) { k++; } else { picked.push(i); }
    });
    viewportRef.current?.select(picked);
    setTool('move');
  };

  // Copy keeps clutter placements; GEO props are nodes of this scene, so they Duplicate instead.
  const copySelection = () => {
    const sel = selectedInstances();
    const clutter = sel.filter(i => i.clutterIndex !== null).map(i => ({ model: i.model, matrix: [...i.matrix] }));
    if (!clutter.length) { say(sel.length ? `Only clutter copies; use Duplicate (${SHORTCUTS.duplicate.label}) for props.` : 'Nothing selected to copy.', !!sel.length); return; }
    clipboardRef.current = clutter;
    const skipped = sel.length - clutter.length;
    say(`Copied ${clutter.length} clutter placement(s)${skipped ? ` (${skipped} prop(s) skipped: use Duplicate)` : ''}.`);
  };

  // Pastes where they were copied from, selected with the Move tool so they can be placed.
  const paste = async () => {
    const items = clipboardRef.current;
    if (!items.length) { say(`The clipboard is empty: select clutter and press ${SHORTCUTS.copy.label}.`); return; }
    const ops = items.map(it => ({ op: 'addClutter', model: it.model, matrix: it.matrix }));
    const raw = await edit(`paste ${ops.length === 1 ? items[0].model : ops.length + ' placements'}`, ops.length === 1 ? ops[0] : { op: 'batch', ops }) as
      { newClutterIndex?: number; results?: Array<{ newClutterIndex?: number }> } | null;
    if (!raw) { return; }
    const added = new Set([raw.newClutterIndex, ...(raw.results ?? []).map(r => r?.newClutterIndex)].filter((v): v is number => typeof v === 'number' && v >= 0));
    viewportRef.current?.select(instancesRef.current.map((inst, i) => inst.clutterIndex !== null && added.has(inst.clutterIndex) ? i : -1).filter(i => i >= 0));
    setTool('move');
  };

  // The Item tab's fields for a multi-selection: move by an offset, turn by an angle, or set one scale.
  const editSelection = async (field: 'offset' | 'turn' | 'scale', value: string, axis = 0) => {
    const v = parseFloat(value);
    const sel = selectedInstances();
    if (!sel.length || !Number.isFinite(v) || (field !== 'scale' && v === 0)) { return; }
    const ops = sel.map(inst => {
      const m = inst.matrix;
      const position = [m[12], m[13], m[14]];
      if (field === 'offset') { position[axis] += v; return placementOp(inst, { position }); }
      const yaw = yawOf(m) + (field === 'turn' ? v : 0);
      const scale = field === 'scale' ? Math.max(0.01, v) : scaleOf(m);
      return placementOp(inst, { position, yaw, scale });
    }).filter((o): o is Record<string, unknown> => !!o);
    const what = field === 'offset' ? `move ${'XYZ'[axis]} by ${v}` : field === 'turn' ? `turn by ${v}°` : `scale to ${v}`;
    await edit(`${what}: ${ops.length} instances`, ops.length === 1 ? ops[0] : { op: 'batch', ops });
  };

  // Moves the selection down onto the surface under it (world model and other props).
  const dropToGround = async () => {
    const vp = viewportRef.current;
    if (!vp) { return; }
    const ops: Record<string, unknown>[] = [];
    for (const i of vp.selected) {
      const inst = instancesRef.current[i];
      if (!movable(inst)) { continue; }
      const y = vp.groundBelow(i);
      if (y === null) { continue; }
      const op = placementOp(inst, { position: [inst.matrix[12], y, inst.matrix[14]] });
      if (op) { ops.push(op); }
    }
    if (ops.length) { await edit('drop to ground', ops.length === 1 ? ops[0] : { op: 'batch', ops }); }
    else { say('Nothing under the selection to drop onto.'); }
  };

  const place = async (position: [number, number, number]) => {
    if (!placeModel) { setPropTab('place'); say('Pick a model in the Place tab first.', true); return; }
    const yaw = randomYaw ? Math.round(Math.random() * 360) : placeYaw;
    const raw = await edit(`place ${placeModel.name}`, { op: 'addClutter', model: placeModel.name, position, yaw, scale: placeScale }) as { newClutterIndex?: number } | null;
    if (raw && typeof raw.newClutterIndex === 'number') {
      const i = instancesRef.current.findIndex(x => x.clutterIndex === raw.newClutterIndex);
      if (i >= 0) { viewportRef.current?.select([i]); }
    }
  };

  return { commitTransforms, setField, movePointField, deleteSelection, duplicateSelection, copySelection, paste, editSelection, dropToGround, place, clipboardRef };
}
