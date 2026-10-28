import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { ModelSummary } from './webview/model-editor/asset-client';
import { MapViewport, type MapLayer, type MapTool } from './viewport/mapViewport';
import type { Shading, ViewPreset } from './webview/zipper-core/viewport-core';
import { LAYERS, layerOf, movable, placementOp, pointKindCounts, scaleOf, yawOf, type ModelGroup } from './mapModel';
import { MenuBar, type Menu } from './components/MenuBar';
import { PageHeader } from './shared/PageHeader';
import { Outliner } from './components/Outliner';
import { PropsPanel, type PropTab } from './components/PropsPanel';
import { ViewportView } from './components/ViewportView';
import { NumField } from './components/NumField';
import { activePage, host, modalOpen } from './shared';
import { SHORTCUTS, TOOLS, matchShortcut, type ShortcutId } from './mapShortcuts';
import { useMapSession } from './useMapSession';

const SHADINGS: Array<{ id: Shading; label: string }> = [
  { id: 'textured', label: 'Textured' },
  { id: 'solid', label: 'Solid' },
  { id: 'wireframe', label: 'Wire' },
  { id: 'xray', label: 'X-ray' },
];

const SHADING_ORDER: Shading[] = ['textured', 'solid', 'wireframe', 'xray'];

export function MapPage() {
  const viewportRef = useRef<MapViewport | null>(null);
  // After each load: a new map sets the fly speed from its size, and the selection follows the viewport's.
  const mapSession = useMapSession(viewportRef, (vp, keepCamera) => {
    if (!keepCamera) { setFlySpeedState(Math.round(vp.flySpeed)); }
    syncSelection();
  });
  const { client, instancesRef, pointsRef, statusRef, status, setStatus, sceneNs, counts, instances, points, models, setModels, busy, progress, message, messageIsError, say,
    reload, edit, history, saveCopy, exportPack, openDialog } = mapSession;
  const [openMenu, setOpenMenu] = useState<string | null>(null);
  const [query, setQuery] = useState('');
  const [libQuery, setLibQuery] = useState('');
  const [propTab, setPropTab] = useState<PropTab>('item');
  const [tool, setToolState] = useState<MapTool>('select');
  const [shading, setShadingState] = useState<Shading>('textured');
  const [baked, setBaked] = useState(false);
  const [grid, setGrid] = useState(true);
  const [snap, setSnap] = useState(false);
  const [snapMove, setSnapMove] = useState(10);
  const [snapRotate, setSnapRotate] = useState(15);
  const [snapScale, setSnapScale] = useState(0.1);
  const [gizmoLocal, setGizmoLocal] = useState(false);
  const [locked, setLocked] = useState<Set<MapLayer>>(() => new Set());
  // Copied clutter placements (model + matrix); they paste into this package or another one that has the model.
  const clipboardRef = useRef<Array<{ model: string; matrix: number[] }>>([]);
  const [ortho, setOrtho] = useState(false);
  const [backfaces, setBackfaces] = useState(true);
  const [flySpeed, setFlySpeedState] = useState(100);
  const [hover, setHover] = useState<string | null>(null);
  const [selection, setSelection] = useState<number[]>([]);
  const [selectedPoint, setSelectedPoint] = useState<string | null>(null);
  const [openNodes, setOpenNodes] = useState<Set<string>>(() => new Set(['L:reference', 'L:clutter']));
  const [hidden, setHidden] = useState<{ layers: Set<MapLayer>; models: Set<string> }>(() => ({ layers: new Set<MapLayer>(['sky']), models: new Set<string>() }));
  const [placeModel, setPlaceModel] = useState<ModelSummary | null>(null);
  const [placeYaw, setPlaceYaw] = useState(0);
  const [placeScale, setPlaceScale] = useState(1);
  const [randomYaw, setRandomYaw] = useState(false);

  const dirtyCount = status?.dirty.length ?? 0;
  const single = selection.length === 1 ? instances[selection[0]] ?? null : null;
  const movableCount = selection.filter(i => movable(instances[i])).length;

  const commitTransforms = async (items: Array<{ index: number; matrix: number[] }>, kind: string) => {
    const list = instancesRef.current;
    const ops = items.map(it => placementOp(list[it.index], { matrix: it.matrix.map(v => Math.fround(v)) })).filter((o): o is Record<string, unknown> => !!o);
    if (!ops.length) { return; }
    const label = `${kind} ${ops.length === 1 ? list[items[0].index].model : ops.length + ' instances'}`;
    await edit(label, ops.length === 1 ? ops[0] : { op: 'batch', ops });
  };

  const setField = async (field: 'position' | 'yaw' | 'scale', value: string, axis = 0) => {
    const inst = single;
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

  // A prop's model in the Model Editor: the same package (and session) with that model selected.
  const openModel = (index: number) => {
    const inst = instancesRef.current[index];
    const s = statusRef.current;
    if (!inst?.source || !s) { return; }
    const cut = inst.source.indexOf(':');
    host.open('models', s.path, { archive: inst.source.slice(0, cut), name: inst.source.slice(cut + 1) });
    say(`Opening ${inst.model} in the Model Editor…`);
  };

  // Copies a model from another package (importModel) and makes it the one the Place tool drops.
  const importFromPackage = async () => {
    const session = client.session;
    if (!session || status?.readOnly) { return; }
    const result = await host.importModel(session);
    if (!result) { return; }
    const list = await client.listModels();
    setModels(list);
    setStatus(await client.status());
    const added = list.find(m => m.archive === result.archive && m.name === result.name) ?? null;
    if (added) { setPlaceModel(added); setTool('place'); }
    const warn = result.warnings.length ? ' ' + result.warnings.join(' ') : '';
    say(`Imported ${result.name} into ${result.archive} (${result.textures.length} texture(s), ${Object.keys(result.palettes).length} palette(s)); click the ground to place it.${warn}`, !!warn);
  };

  // ------------------------------------------------------------------ view

  const setTool = (next: MapTool) => {
    setToolState(next);
    if (viewportRef.current) { viewportRef.current.tool = next; }
    if (next === 'place') { setPropTab('place'); }
  };

  const setShading = (s: Shading) => {
    setShadingState(s);
    if (viewportRef.current) { viewportRef.current.shading = s; }
  };

  const cycleShading = () => setShading(SHADING_ORDER[(SHADING_ORDER.indexOf(shading) + 1) % SHADING_ORDER.length]);

  const toggleBaked = (on: boolean) => {
    setBaked(on);
    viewportRef.current?.setLighting(on);
  };

  const toggleGrid = (on: boolean) => {
    setGrid(on);
    if (viewportRef.current) {
      viewportRef.current.showGrid = on;
      viewportRef.current.showAxes = on;
      viewportRef.current.refreshHelpers();
    }
  };

  const applySnap = (on: boolean, move: number, rotate: number, scale: number) => {
    viewportRef.current?.setSnap(on ? move : null, on ? rotate : null, on ? scale : null);
  };

  const toggleSnap = (on: boolean) => { setSnap(on); applySnap(on, snapMove, snapRotate, snapScale); };
  const changeSnapMove = (v: number) => { setSnapMove(v); applySnap(snap, v, snapRotate, snapScale); };
  const changeSnapRotate = (v: number) => { setSnapRotate(v); applySnap(snap, snapMove, v, snapScale); };
  const changeSnapScale = (v: number) => { setSnapScale(v); applySnap(snap, snapMove, snapRotate, v); };

  const toggleLocal = () => {
    const next = !gizmoLocal;
    setGizmoLocal(next);
    if (viewportRef.current) { viewportRef.current.gizmoLocal = next; }
  };

  const toggleLock = (layer: MapLayer) => {
    const vp = viewportRef.current;
    if (!vp) { return; }
    vp.setLayerLocked(layer, !vp.isLayerLocked(layer));
    setLocked(l => toggled(l, layer));
  };

  const setFlySpeed = (v: number) => {
    if (v > 0) {
      setFlySpeedState(v);
      if (viewportRef.current) { viewportRef.current.flySpeed = v; }
    }
  };

  const toggleBackfaces = () => {
    const next = !backfaces;
    setBackfaces(next);
    viewportRef.current?.setBackfaces(next);
  };

  const view = (preset: ViewPreset) => viewportRef.current?.setView(preset);
  const toggleOrtho = () => {
    viewportRef.current?.toggleOrtho();
    setOrtho(viewportRef.current?.isOrtho ?? false);
  };
  const frameSelected = () => viewportRef.current?.frameSelected();
  const frameAll = () => viewportRef.current?.frameAll();

  // Selection state mirrors the viewport's own set; the viewport is the source of truth.
  const syncSelection = () => {
    const vp = viewportRef.current;
    if (!vp) { return; }
    setSelection([...vp.selected].sort((a, b) => a - b));
    setSelectedPoint(vp.selectedPoint);
  };

  const toggleLayer = (layer: MapLayer) => {
    const vp = viewportRef.current;
    if (!vp) { return; }
    vp.setLayerHidden(layer, !vp.isLayerHidden(layer));
    setHidden(h => ({ ...h, layers: toggled(h.layers, layer) }));
  };
  const toggleModel = (source: string) => {
    const vp = viewportRef.current;
    if (!vp) { return; }
    vp.setModelHidden(source, !vp.isModelHidden(source));
    setHidden(h => ({ ...h, models: toggled(h.models, source) }));
  };

  const toggleOpen = (key: string) => {
    setOpenNodes(prev => toggled(prev, key));
  };

  const pickGroup = (g: ModelGroup, shift: boolean) => {
    viewportRef.current?.select(g.indices, shift);
    if (!shift && g.indices.length === 1) { viewportRef.current?.frameSelected(); }
  };
  const pickInstance = (i: number, shift: boolean) => {
    viewportRef.current?.select([i], shift);
    viewportRef.current?.frameSelected();
  };
  const pickPoint = (name: string) => {
    viewportRef.current?.selectPoint(name);
    viewportRef.current?.frameSelected();
  };
  const selectSameModel = () => {
    const list = instancesRef.current;
    const sources = new Set(selection.map(i => list[i]?.source).filter((s): s is string => !!s));
    const kinds = new Set(selection.map(i => list[i]?.kind));
    viewportRef.current?.select(list.map((inst, i) => inst.source && sources.has(inst.source) && kinds.has(inst.kind) ? i : -1).filter(i => i >= 0));
  };
  const clearSelection = () => viewportRef.current?.clearSelection();

  // ------------------------------------------------------------------ derived

  const groups = useMemo(() => {
    const q = query.toLowerCase();
    const out = new Map<MapLayer, Map<string, ModelGroup>>();
    instances.forEach((inst, i) => {
      if (q && !inst.model.toLowerCase().includes(q) && !inst.instance.toLowerCase().includes(q)) { return; }
      const layer = layerOf(inst);
      const key = inst.source ?? `?:${inst.model}`;
      const byModel = out.get(layer) ?? new Map<string, ModelGroup>();
      const g = byModel.get(key) ?? { source: key, model: inst.model, indices: [] };
      g.indices.push(i);
      byModel.set(key, g);
      out.set(layer, byModel);
    });
    return out;
  }, [instances, query]);

  const filteredPoints = useMemo(() => {
    const q = query.toLowerCase();
    return points.filter(p => !q || p.name.toLowerCase().includes(q));
  }, [points, query]);

  const library = useMemo(() => {
    const q = libQuery.toLowerCase();
    return models.filter(m => (m.kind === 'prop' || m.kind === 'library') && (!q || m.name.toLowerCase().includes(q))).slice(0, 400);
  }, [models, libQuery]);

  const layerCounts = useMemo(() => {
    const out = new Map<MapLayer, number>();
    for (const inst of instances) { const l = layerOf(inst); out.set(l, (out.get(l) ?? 0) + 1); }
    return out;
  }, [instances]);
  const layerCount = (layer: MapLayer) => layerCounts.get(layer) ?? 0;
  const selectionSummary = useMemo(() => {
    const by = new Map<string, number>();
    for (const i of selection) { const m = instances[i]?.model ?? '?'; by.set(m, (by.get(m) ?? 0) + 1); }
    return [...by].map(([m, n]) => `${n}× ${m}`).join(', ');
  }, [selection, instances]);

  const pointInfo = points.find(p => p.name === selectedPoint) ?? null;
  const countList = Object.entries(counts).map(([kind, count]) => ({ kind, count }));

  // ------------------------------------------------------------------ menus & keys

  const menus: Menu[] = [
    {
      id: 'file', label: 'File', items: [
        { label: 'Open package…', shortcut: SHORTCUTS.open.label, action: () => void openDialog() },
        { label: 'Reload', action: () => void reload(true, true), disabled: () => !status },
        { label: 'Import from package…', action: () => void importFromPackage(), disabled: () => !status || !!status.readOnly || busy },
        { separator: true, label: '' },
        { label: 'Save copy…', shortcut: SHORTCUTS.save.label, action: () => void saveCopy(), disabled: () => !dirtyCount },
        { label: 'Export memory-card pack…', action: () => void exportPack(), disabled: () => !dirtyCount },
      ],
    },
    {
      id: 'edit', label: 'Edit', items: [
        { label: status?.canUndo ? `Undo ${status.canUndo}` : 'Undo', shortcut: SHORTCUTS.undo.label, action: () => void history(true), disabled: () => !status?.canUndo },
        { label: status?.canRedo ? `Redo ${status.canRedo}` : 'Redo', shortcut: SHORTCUTS.redo.label, action: () => void history(false), disabled: () => !status?.canRedo },
        { separator: true, label: '' },
        { label: 'Copy clutter', shortcut: SHORTCUTS.copy.label, action: copySelection, disabled: () => !movableCount },
        { label: 'Paste clutter', shortcut: SHORTCUTS.paste.label, action: () => void paste(), disabled: () => !status || !!status.readOnly || busy },
        { separator: true, label: '' },
        { label: 'Duplicate', shortcut: SHORTCUTS.duplicate.label, action: () => void duplicateSelection(), disabled: () => !movableCount },
        { label: 'Delete', shortcut: SHORTCUTS.delete.label, action: () => void deleteSelection(), disabled: () => !movableCount },
        { label: 'Drop to ground', shortcut: SHORTCUTS.drop.label, action: () => void dropToGround(), disabled: () => !movableCount },
        { separator: true, label: '' },
        { label: 'Open model…', action: () => openModel(selection[0]), disabled: () => selection.length !== 1 || !single?.source },
      ],
    },
    {
      id: 'select', label: 'Select', items: [
        { label: 'None', shortcut: SHORTCUTS.escape.label, action: clearSelection },
        { label: 'All of this model', shortcut: SHORTCUTS.selectSame.label, action: selectSameModel, disabled: () => selection.length === 0 },
      ],
    },
    {
      id: 'view', label: 'View', items: [
        { label: 'Frame selected', shortcut: SHORTCUTS.frame.label, action: frameSelected },
        { label: 'Frame map', shortcut: SHORTCUTS.frameAll.label, action: frameAll },
        { separator: true, label: '' },
        { label: 'Top', shortcut: SHORTCUTS.top.label, action: () => view('top') },
        { label: 'Front', shortcut: SHORTCUTS.front.label, action: () => view('front') },
        { label: 'Right', shortcut: SHORTCUTS.right.label, action: () => view('right') },
        { label: 'Orthographic', shortcut: SHORTCUTS.ortho.label, action: toggleOrtho, checked: () => ortho },
        { separator: true, label: '' },
        ...LAYERS.map(l => ({ label: l.label, action: () => toggleLayer(l.id), checked: () => !hidden.layers.has(l.id) })),
        { separator: true, label: '' },
        { label: 'Back faces', action: toggleBackfaces, checked: () => backfaces },
      ],
    },
    {
      id: 'help', label: 'Help', items: [
        { label: 'Fly: W A S D, Q / E down / up, Shift faster (viewport focused)' },
        { label: 'Orbit: middle drag or Alt+left drag · Pan: right drag · Zoom: wheel' },
        { label: 'Select: click, Shift adds, Ctrl removes, drag for box' },
        { label: `Copy / paste clutter: ${SHORTCUTS.copy.label} / ${SHORTCUTS.paste.label} (also into another package with the model)` },
        { label: `Tools: ${TOOLS.map(t => `${t.key} ${t.id}`).join(' · ')}` },
        { label: 'Double-click a prop to open its model in the Model Editor' },
      ],
    },
  ];

  // What each shortcut does; false leaves the key to the browser. Read through a ref so the window listener is installed once.
  const keyActions: Record<ShortcutId, () => boolean | void> = {
    undo: () => void history(true),
    redo: () => void history(false),
    save: () => void saveCopy(),
    open: () => void openDialog(),
    // Without a selection / clipboard the browser keeps Ctrl+C / Ctrl+V (copying the status text, say).
    copy: () => { if (!selection.length) { return false; } copySelection(); },
    paste: () => { if (!clipboardRef.current.length) { return false; } void paste(); },
    front: () => void view('front'),
    right: () => void view('right'),
    top: () => void view('top'),
    ortho: toggleOrtho,
    duplicate: () => void duplicateSelection(),
    selectSame: selectSameModel,
    delete: () => void deleteSelection(),
    drop: () => void dropToGround(),
    frame: () => void frameSelected(),
    frameAll: () => void frameAll(),
    toolSelect: () => setTool('select'),
    toolMove: () => setTool('move'),
    toolRotate: () => setTool('rotate'),
    toolScale: () => setTool('scale'),
    toolPlace: () => setTool('place'),
    shading: cycleShading,
    escape: () => { setOpenMenu(null); if (tool !== 'select') { setTool('select'); } else { clearSelection(); } },
  };
  const keyHandlerRef = useRef<(e: KeyboardEvent) => void>(() => undefined);
  keyHandlerRef.current = (e: KeyboardEvent) => {
    // Only the page in front takes keys, and none while a prompt or the file browser is open.
    if (activePage() !== 'map' || modalOpen()) { return; }
    const target = e.target as HTMLElement;
    if (target && ['INPUT', 'SELECT', 'TEXTAREA'].includes(target.tagName)) { return; }
    const id = matchShortcut(e);
    if (id && keyActions[id]() !== false) { e.preventDefault(); }
  };

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => keyHandlerRef.current(e);
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, []);

  // ------------------------------------------------------------------ viewport wiring

  // The viewport calls these; they are read through a ref so they always see the current state.
  const handlersRef = useRef({ commitTransforms, place, syncSelection, edit, setHover, setPropTab, openDialog, openModel });
  handlersRef.current = { commitTransforms, place, syncSelection, edit, setHover, setPropTab, openDialog, openModel };

  const onViewportReady = useCallback((canvas: HTMLCanvasElement) => {
    if (viewportRef.current) { return; }
    const vp = new MapViewport(canvas, {
      onSelectionChange: () => {
        handlersRef.current.syncSelection();
        if (viewportRef.current && (viewportRef.current.selected.size || viewportRef.current.selectedPoint)) { handlersRef.current.setPropTab('item'); }
      },
      onHover: text => handlersRef.current.setHover(text),
      onTransformCommit: (items, kind) => void handlersRef.current.commitTransforms(items, kind),
      onPointMove: (name, position) => void handlersRef.current.edit(`move point ${name}`, { op: 'movePoint', name, position }),
      onPlace: position => void handlersRef.current.place(position),
      onOpenModel: index => handlersRef.current.openModel(index),
    });
    vp.setLighting(false);
    vp.setSnap(null, null, null);
    viewportRef.current = vp;
  }, []);

  useEffect(() => () => { viewportRef.current?.dispose(); viewportRef.current = null; }, []);

  // ------------------------------------------------------------------ render

  const viewLabel = ortho ? 'Orthographic' : 'Perspective';
  const toolLabel = TOOLS.find(t => t.id === tool)?.label.split(' (')[0] ?? '';
  const menuBarProps = {
    status,
    dirtyCount,
    menus,
    openMenu,
    setOpenMenu,
  };

  return (
    <div className="me-app has-header" onPointerDown={e => { if (!(e.target as HTMLElement).closest('.me-menu')) { setOpenMenu(null); } }}>
      <PageHeader icon="globe" title="Map Builder" subtitle="Props, clutter and spawn points of SOCOM II maps" />
      <MenuBar {...menuBarProps} />

      <div className="me-header">
        <div className="me-seg" title={`Shading (${SHORTCUTS.shading.label} cycles)`}>
          {SHADINGS.map(s => (
            <button key={s.id} className={shading === s.id ? 'on' : ''} onClick={() => setShading(s.id)}>{s.label}</button>
          ))}
        </div>
        <label className="me-toggle"><input type="checkbox" checked={baked} onChange={e => toggleBaked(e.target.checked)} /> Baked lighting</label>
        <label className="me-toggle"><input type="checkbox" checked={grid} onChange={e => toggleGrid(e.target.checked)} /> Grid</label>
        <label className="me-toggle"><input type="checkbox" checked={snap} onChange={e => toggleSnap(e.target.checked)} /> Snap</label>
        {snap && (
          <>
            <NumField small min="0.001" step={1} value={String(snapMove)} title="Move snap (units)" onCommit={v => changeSnapMove(+v || 1)} />
            <NumField small min={1} step={5} value={String(snapRotate)} title="Rotate snap (degrees)" onCommit={v => changeSnapRotate(+v || 15)} />
            <NumField small min="0.01" step={0.05} value={String(snapScale)} title="Scale snap (factor)" onCommit={v => changeSnapScale(+v || 0.1)} />
          </>
        )}
        <label className="me-toggle" title="Move a single prop along its own axes (its yaw) instead of the world's"><input type="checkbox" checked={gizmoLocal} onChange={toggleLocal} /> Local</label>
        <label className="me-toggle" title="WASD fly speed (Shift x4)">Fly <NumField small min={1} step={10} value={String(flySpeed)} onCommit={v => setFlySpeed(+v)} /></label>
        <span className="me-spacer" />
        <div className="me-seg" title="View">
          <button onClick={() => view('top')} title={`Top (${SHORTCUTS.top.label})`}>Top</button>
          <button onClick={() => view('front')} title={`Front (${SHORTCUTS.front.label})`}>Front</button>
          <button onClick={() => view('iso')} title="Iso">Iso</button>
          <button className={ortho ? 'on' : ''} onClick={toggleOrtho} title={`Orthographic (${SHORTCUTS.ortho.label})`}>Ortho</button>
          <button onClick={frameSelected} title={`Frame selected (${SHORTCUTS.frame.label})`}>Frame</button>
          <button onClick={frameAll} title={`Frame map (${SHORTCUTS.frameAll.label})`}>All</button>
        </div>
      </div>

      <div className="me-body no-assets">
        <aside className="me-assets" />
        <nav className="me-tools">
          {TOOLS.map(t => (
            <button key={t.id} className={`me-tool${tool === t.id ? ' on' : ''}`} onClick={() => setTool(t.id)} title={`${t.label} (${t.key})`}>
              <span className="me-tool-glyph">{t.glyph}</span>
            </button>
          ))}
        </nav>

        <section className="me-center">
          <ViewportView
            onReady={onViewportReady}
            viewLabel={`${viewLabel} · ${toolLabel}`}
            sceneNs={sceneNs}
            instanceCount={instances.length}
            pointCount={points.length}
            hasSession={!!status}
            progress={progress}
            busy={busy}
          />
        </section>

        <aside className="me-side">
          <Outliner
            query={query}
            setQuery={setQuery}
            layers={LAYERS}
            points={points}
            filteredPoints={filteredPoints}
            selectedPoint={selectedPoint}
            groups={groups}
            instances={instances}
            selection={selection}
            openNodes={openNodes}
            toggleOpen={toggleOpen}
            isLayerHidden={layer => hidden.layers.has(layer)}
            toggleLayer={toggleLayer}
            isLayerLocked={layer => locked.has(layer)}
            toggleLock={toggleLock}
            isModelHidden={source => hidden.models.has(source)}
            toggleModel={toggleModel}
            layerCount={layerCount}
            pickGroup={pickGroup}
            pickInstance={pickInstance}
            pickPoint={pickPoint}
          />
          <PropsPanel
            tab={propTab}
            setTab={setPropTab}
            status={status}
            sceneNs={sceneNs}
            countList={countList}
            pointKinds={pointKindCounts(points)}
            pointInfo={pointInfo}
            selectedPoint={selectedPoint}
            selection={selection}
            selectionSummary={selectionSummary}
            single={single}
            movableCount={movableCount}
            busy={busy}
            onMovePoint={movePointField}
            onSetField={setField}
            onEditSelection={editSelection}
            onDuplicate={() => void duplicateSelection()}
            onDelete={() => void deleteSelection()}
            onDrop={() => void dropToGround()}
            onOpenModel={() => openModel(selection[0])}
            library={library}
            libQuery={libQuery}
            setLibQuery={setLibQuery}
            placeModel={placeModel}
            setPlaceModel={model => { setPlaceModel(model); setTool('place'); }}
            placeYaw={placeYaw}
            setPlaceYaw={setPlaceYaw}
            placeScale={placeScale}
            setPlaceScale={setPlaceScale}
            randomYaw={randomYaw}
            setRandomYaw={setRandomYaw}
            onImport={() => void importFromPackage()}
          />
        </aside>
      </div>

      <div className={`me-statusbar${messageIsError ? ' err' : ''}`}>
        <span>{message}</span>
        <span className="me-spacer" />
        {hover && <span className="lm-muted">{hover}</span>}
        <span>{selection.length} selected</span>
      </div>

    </div>
  );
}

function toggled<T>(set: Set<T>, value: T): Set<T> {
  const next = new Set(set);
  if (next.has(value)) { next.delete(value); } else { next.add(value); }
  return next;
}
