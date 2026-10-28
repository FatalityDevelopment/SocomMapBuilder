import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { AssetClient, type MapInstanceWire, type MapPointWire, type ModelSummary, type SessionStatus } from './webview/model-editor/asset-client';
import { TextureCache } from './webview/model-editor/model-loader';
import { decodeMesh, type MeshWire } from './webview/model-editor/mesh-data';
import type { ModelTexturePixels } from './webview/zipper-workbench/zed-model-renderer';
import { MapViewport, type MapLayer, type MapModel, type MapTool } from './viewport/mapViewport';
import type { Shading, ViewPreset } from './webview/zipper-core/viewport-core';
import { LAYERS, errText, layerOf, movable, placementOp, pointKindCounts, scaleOf, yawOf, type ModelGroup } from './mapModel';
import { MenuBar, type Menu } from './components/MenuBar';
import { PageHeader } from './shared/PageHeader';
import { Outliner } from './components/Outliner';
import { PropsPanel, type PropTab } from './components/PropsPanel';
import { ViewportView } from './components/ViewportView';
import { NumField } from './components/NumField';
import { activePage, host, modalOpen, openRequest, useSignals } from './shared';

const SHADINGS: Array<{ id: Shading; label: string }> = [
  { id: 'textured', label: 'Textured' },
  { id: 'solid', label: 'Solid' },
  { id: 'wireframe', label: 'Wire' },
  { id: 'xray', label: 'X-ray' },
];

// W A S D Q E fly, so tools use other keys.
export const TOOLS: Array<{ id: MapTool; label: string; key: string; glyph: string }> = [
  { id: 'select', label: 'Select (click / box drag)', key: 'V', glyph: '⬚' },
  { id: 'move', label: 'Move', key: 'G', glyph: '✥' },
  { id: 'rotate', label: 'Rotate (yaw)', key: 'R', glyph: '⟳' },
  { id: 'scale', label: 'Scale', key: 'T', glyph: '⤢' },
  { id: 'place', label: 'Place clutter', key: 'P', glyph: '✚' },
];

const SHADING_ORDER: Shading[] = ['textured', 'solid', 'wireframe', 'xray'];

export function MapPage() {
  const clientRef = useRef(new AssetClient());
  const texturesRef = useRef(new TextureCache(clientRef.current));
  const meshCacheRef = useRef(new Map<string, MapModel>());
  const pixelCacheRef = useRef<Record<string, ModelTexturePixels>>({});
  const viewportRef = useRef<MapViewport | null>(null);
  const instancesRef = useRef<MapInstanceWire[]>([]);
  const pointsRef = useRef<MapPointWire[]>([]);
  const statusRef = useRef<SessionStatus | null>(null);
  const busyRef = useRef(false);
  const reloadingRef = useRef(0);

  const [status, setStatusState] = useState<SessionStatus | null>(null);
  const [sceneNs, setSceneNs] = useState('');
  const [counts, setCounts] = useState<Record<string, number>>({});
  const [instances, setInstances] = useState<MapInstanceWire[]>([]);
  const [points, setPoints] = useState<MapPointWire[]>([]);
  const [models, setModels] = useState<ModelSummary[]>([]);
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
  const [ortho, setOrtho] = useState(false);
  const [backfaces, setBackfaces] = useState(true);
  const [flySpeed, setFlySpeedState] = useState(100);
  const [busy, setBusy] = useState(false);
  const [progress, setProgress] = useState('');
  const [message, setMessage] = useState('');
  const [messageIsError, setMessageIsError] = useState(false);
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

  const setStatus = useCallback((s: SessionStatus | null) => { statusRef.current = s; setStatusState(s); }, []);
  const setBusyFlag = useCallback((on: boolean) => { busyRef.current = on; setBusy(on); }, []);

  // The revision last opened or saved: status.dirty compares with the source file, so it stays set after a Save copy.
  const savedRevisionRef = useRef<number | null>(null);
  const unsaved = () => {
    const s = statusRef.current;
    return !!s && s.dirty.length > 0 && s.revision !== savedRevisionRef.current;
  };

  const say = useCallback((text: string, error = false) => {
    setMessage(text);
    setMessageIsError(error);
  }, []);

  // ------------------------------------------------------------------ loading

  // Scene + points from the backend, then meshes and textures per unique model (cached: placement edits keep them).
  const loadScene = useCallback(async (keepCamera: boolean, modelsChanged: boolean) => {
    const vp = viewportRef.current;
    if (!vp) { return; }
    if (modelsChanged) {
      meshCacheRef.current.clear();
      pixelCacheRef.current = {};
      const s = statusRef.current;
      if (s) { texturesRef.current.invalidate(s.revision); }
    }
    const [scene, pts] = await Promise.all([
      clientRef.current.mapScene(),
      clientRef.current.mapPoints().catch(() => ({ points: [] as MapPointWire[] })),
    ]);
    setSceneNs(scene.sceneNamespace);
    setCounts(scene.counts);
    const sources = [...new Set(scene.instances.map(i => i.source).filter((s): s is string => !!s))].filter(s => !meshCacheRef.current.has(s));
    let done = 0;
    const loadOne = async (source: string): Promise<void> => {
      const cut = source.indexOf(':');
      try {
        const wire = await clientRef.current.mesh(source.slice(0, cut), source.slice(cut + 1)) as MeshWire;
        const mesh = decodeMesh(wire);
        const ns = wire.textureNamespace ?? null;
        const names = [...new Set(mesh.chunks.map(c => c.texture).filter((t): t is string => !!t))];
        await Promise.all(names.map(async n => {
          const key = `${ns ?? ''}:${n}`.toLowerCase();
          if (key in pixelCacheRef.current) { return; }
          const p = await texturesRef.current.get(n, ns);
          if (p) { pixelCacheRef.current[key] = p; }
        }));
        meshCacheRef.current.set(source, { mesh, textureNamespace: ns });
      } catch (err) {
        say(`${source}: ${errText(err)}`, true);
      }
      done++;
      setProgress(`Loading models ${done}/${sources.length}…`);
    };
    // A few at a time: the backend answers in order anyway, and the progress stays readable.
    const queue = [...sources];
    await Promise.all(Array.from({ length: Math.min(6, queue.length) }, async () => {
      while (queue.length) { await loadOne(queue.shift()!); }
    }));
    setProgress('');
    instancesRef.current = scene.instances;
    pointsRef.current = pts.points;
    setInstances(scene.instances);
    setPoints(pts.points);
    vp.setScene(scene.instances, meshCacheRef.current, pixelCacheRef.current, pts.points, keepCamera);
    if (!keepCamera) { setFlySpeedState(Math.round(vp.flySpeed)); }
    syncSelection();
    if (!keepCamera) {
      const kinds = Object.entries(scene.counts).map(([kind, count]) => `${count} ${kind}`).join(', ');
      say(`${scene.sceneNamespace}: ${scene.instances.length} instances (${kinds}), ${pts.points.length} points.`);
    }
    // syncSelection is stable: it reads the viewport only.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [say]);

  // Polls skip while a load runs: a map with many models takes longer than one poll interval.
  const reload = useCallback(async (keepCamera: boolean, modelsChanged: boolean) => {
    reloadingRef.current++;
    try { await loadScene(keepCamera, modelsChanged); } finally { reloadingRef.current--; }
  }, [loadScene]);

  const openPackage = useCallback(async (path: string) => {
    const current = statusRef.current;
    if (current && current.path !== path && unsaved() && !window.confirm(`${current.path} has unsaved changes (${current.dirty.join(', ')}). They stay in the session until the server stops; open ${path} anyway?`)) { return; }
    setBusyFlag(true);
    say(`Opening ${path}…`);
    try {
      const s = await clientRef.current.open(path);
      setStatus(s);
      savedRevisionRef.current = s.revision;
      texturesRef.current.invalidate(s.revision);
      meshCacheRef.current.clear();
      pixelCacheRef.current = {};
      setModels(await clientRef.current.listModels());
      viewportRef.current?.clearSelection();
      await reload(false, false);
    } catch (err) {
      say(`Could not open ${path}: ${errText(err)}`, true);
    } finally {
      setBusyFlag(false);
    }
  }, [reload, say, setBusyFlag, setStatus]);

  // Another page asked for a package here (the Asset Viewer's "Open map in Map Builder").
  useSignals();
  const request = openRequest();
  useEffect(() => {
    if (request?.page === 'map') { void openPackage(request.path); }
    // openPackage is stable for the page's life; only a new request should open.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [request]);

  // Edits made by anyone on the session (an assistant on the same backend) show up here.
  useEffect(() => {
    const timer = setInterval(async () => {
      if (!clientRef.current.session || busyRef.current || reloadingRef.current > 0) { return; }
      try {
        const next = await clientRef.current.status();
        const before = statusRef.current?.revision;
        setStatus(next);
        if (before !== undefined && next.revision !== before) {
          await reload(true, true);
          say(`Session changed (revision ${next.revision}${next.canUndo ? `: ${next.canUndo}` : ''}).`);
        }
      } catch { /* retried next poll */ }
    }, 2000);
    return () => clearInterval(timer);
  }, [reload, say, setStatus]);

  // The gizmo and Place tool are off while an edit runs or the session is read-only.
  useEffect(() => {
    if (viewportRef.current) { viewportRef.current.editable = !busy && !status?.readOnly; }
  }, [busy, status]);

  // Closing the tab with edits made since the last save asks first.
  useEffect(() => {
    const onUnload = (e: BeforeUnloadEvent) => { if (unsaved()) { e.preventDefault(); e.returnValue = ''; } };
    window.addEventListener('beforeunload', onUnload);
    return () => window.removeEventListener('beforeunload', onUnload);
  }, []);

  // ------------------------------------------------------------------ edits

  const edit = useCallback(async (label: string, op: Record<string, unknown>): Promise<Record<string, unknown> | null> => {
    // One edit at a time, and none on a read-only session.
    if (busyRef.current || statusRef.current?.readOnly) {
      say(statusRef.current?.readOnly ? `${label}: this package is read-only.` : `${label}: wait for the previous change to finish.`, true);
      return null;
    }
    setBusyFlag(true);
    try {
      const raw = await clientRef.current.edit({ label, ...op }) as unknown as Record<string, unknown>;
      const result = (raw['result'] ?? raw) as { revision: number; description?: string };
      setStatus(await clientRef.current.status());
      say(`${result.description ?? label} (revision ${result.revision})`);
      await reload(true, false);
      return raw;
    } catch (err) {
      say(`${label}: ${errText(err)}`, true);
      await reload(true, false).catch(() => undefined);
      return null;
    } finally {
      setBusyFlag(false);
    }
  }, [reload, say, setBusyFlag, setStatus]);

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

  const history = async (undo: boolean) => {
    if (!clientRef.current.session) { return; }
    setBusyFlag(true);
    try {
      const r = undo ? await clientRef.current.undo() : await clientRef.current.redo();
      setStatus(await clientRef.current.status());
      say(`${undo ? 'Undid' : 'Redid'} ${r.description ?? ''} (revision ${r.revision})`);
      // Undo can touch models as well as placements.
      await reload(true, true);
    } catch (err) {
      say(errText(err), true);
    } finally {
      setBusyFlag(false);
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

  // ------------------------------------------------------------------ save

  const save = async (args: { mode: 'zdb' | 'pack'; outputPath?: string; packName?: string }) => {
    setBusyFlag(true);
    try {
      const r = await clientRef.current.save(args);
      savedRevisionRef.current = r.revision ?? statusRef.current?.revision ?? null;
      say(`Wrote ${r.written.join(', ')} (${r.leaves.join(', ')})`);
    } catch (err) {
      say(`Save failed: ${errText(err)}`, true);
    } finally {
      setBusyFlag(false);
    }
  };

  const saveCopy = async () => {
    const s = statusRef.current;
    if (!s || !s.dirty.length) { return; }
    // A .CDB pack keeps its name (the game mounts mc0:/BASCUS-97275<NAME>/<NAME>.CDB); a .ZDB gets an _edit copy.
    const picked = await host.pickSavePath('zdb', /.cdb$/i.test(s.path) ? s.path : s.path.replace(/(.zdb)$/i, '_edit$1'));
    if (picked.path) { void save({ mode: 'zdb', outputPath: picked.path }); }
  };

  const exportPack = async () => {
    const picked = await host.pickSavePath('pack', 'MMAP');
    if (picked.packName && picked.path) { void save({ mode: 'pack', packName: picked.packName, outputPath: picked.path }); }
  };

  // Copies a model from another package (importModel) and makes it the one the Place tool drops.
  const importFromPackage = async () => {
    const session = clientRef.current.session;
    if (!session || status?.readOnly) { return; }
    const result = await host.importModel(session);
    if (!result) { return; }
    const list = await clientRef.current.listModels();
    setModels(list);
    setStatus(await clientRef.current.status());
    const added = list.find(m => m.archive === result.archive && m.name === result.name) ?? null;
    if (added) { setPlaceModel(added); setTool('place'); }
    const warn = result.warnings.length ? ' ' + result.warnings.join(' ') : '';
    say(`Imported ${result.name} into ${result.archive} (${result.textures.length} texture(s), ${Object.keys(result.palettes).length} palette(s)); click the ground to place it.${warn}`, !!warn);
  };

  const openDialog = async () => {
    const path = await host.pickPackage();
    if (path) { void openPackage(path); }
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

  const applySnap = (on: boolean, move: number, rotate: number) => {
    viewportRef.current?.setSnap(on ? move : null, on ? rotate : null, on ? 0.1 : null);
  };

  const toggleSnap = (on: boolean) => { setSnap(on); applySnap(on, snapMove, snapRotate); };
  const changeSnapMove = (v: number) => { setSnapMove(v); applySnap(snap, v, snapRotate); };
  const changeSnapRotate = (v: number) => { setSnapRotate(v); applySnap(snap, snapMove, v); };

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

  const layerCount = (layer: MapLayer) => instances.filter(i => layerOf(i) === layer).length;
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
        { label: 'Open package…', shortcut: 'Ctrl+O', action: () => void openDialog() },
        { label: 'Reload', action: () => void reload(true, true), disabled: () => !status },
        { label: 'Import from package…', action: () => void importFromPackage(), disabled: () => !status || !!status.readOnly || busy },
        { separator: true, label: '' },
        { label: 'Save copy…', shortcut: 'Ctrl+S', action: () => void saveCopy(), disabled: () => !dirtyCount },
        { label: 'Export memory-card pack…', action: () => void exportPack(), disabled: () => !dirtyCount },
      ],
    },
    {
      id: 'edit', label: 'Edit', items: [
        { label: 'Undo', shortcut: 'Ctrl+Z', action: () => void history(true), disabled: () => !status?.canUndo },
        { label: 'Redo', shortcut: 'Ctrl+Y', action: () => void history(false), disabled: () => !status?.canRedo },
        { separator: true, label: '' },
        { label: 'Duplicate', shortcut: 'Shift+D', action: () => void duplicateSelection(), disabled: () => !movableCount },
        { label: 'Delete', shortcut: 'X / Delete', action: () => void deleteSelection(), disabled: () => !movableCount },
        { label: 'Drop to ground', shortcut: 'End', action: () => void dropToGround(), disabled: () => !movableCount },
        { separator: true, label: '' },
        { label: 'Open model…', action: () => openModel(selection[0]), disabled: () => selection.length !== 1 || !single?.source },
      ],
    },
    {
      id: 'select', label: 'Select', items: [
        { label: 'None', shortcut: 'Esc', action: clearSelection },
        { label: 'All of this model', shortcut: 'Shift+A', action: selectSameModel, disabled: () => selection.length === 0 },
      ],
    },
    {
      id: 'view', label: 'View', items: [
        { label: 'Frame selected', shortcut: 'F', action: frameSelected },
        { label: 'Frame map', shortcut: 'Home', action: frameAll },
        { separator: true, label: '' },
        { label: 'Top', shortcut: 'Numpad 7', action: () => view('top') },
        { label: 'Front', shortcut: 'Numpad 1', action: () => view('front') },
        { label: 'Right', shortcut: 'Numpad 3', action: () => view('right') },
        { label: 'Orthographic', shortcut: 'Numpad 5', action: toggleOrtho, checked: () => ortho },
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
        { label: 'Tools: V select · G move · R rotate · T scale · P place' },
        { label: 'Double-click a prop to open its model in the Model Editor' },
      ],
    },
  ];

  // The keyboard handler is read through a ref so the window listener is installed once.
  const keyHandlerRef = useRef<(e: KeyboardEvent) => void>(() => undefined);
  keyHandlerRef.current = (e: KeyboardEvent) => {
    // Only the page in front takes keys, and none while a prompt or the file browser is open.
    if (activePage() !== 'map' || modalOpen()) { return; }
    const target = e.target as HTMLElement;
    if (target && ['INPUT', 'SELECT', 'TEXTAREA'].includes(target.tagName)) { return; }
    const ctrl = e.ctrlKey || e.metaKey;
    const k = e.key.toLowerCase();
    let handled = true;
    if (ctrl && k === 'z') { void history(true); }
    else if (ctrl && (k === 'y' || (e.shiftKey && k === 'z'))) { void history(false); }
    else if (ctrl && k === 's') { void saveCopy(); }
    else if (ctrl && k === 'o') { void openDialog(); }
    else if (e.code === 'Numpad1') { view('front'); }
    else if (e.code === 'Numpad3') { view('right'); }
    else if (e.code === 'Numpad7') { view('top'); }
    else if (e.code === 'Numpad5') { toggleOrtho(); }
    else if (ctrl) { handled = false; }
    else if (k === 'd' && e.shiftKey) { void duplicateSelection(); }
    else if (k === 'a' && e.shiftKey) { selectSameModel(); }
    else if (k === 'x' || e.key === 'Delete') { void deleteSelection(); }
    else if (e.key === 'End') { void dropToGround(); }
    else if (k === 'f') { frameSelected(); }
    else if (e.key === 'Home') { frameAll(); }
    else if (k === 'v') { setTool('select'); }
    else if (k === 'g') { setTool('move'); }
    else if (k === 'r') { setTool('rotate'); }
    else if (k === 't') { setTool('scale'); }
    else if (k === 'p') { setTool('place'); }
    else if (k === 'z') { cycleShading(); }
    else if (e.key === 'Escape') { setOpenMenu(null); if (tool !== 'select') { setTool('select'); } else { clearSelection(); } }
    else { handled = false; }
    if (handled) { e.preventDefault(); }
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
        <div className="me-seg" title="Shading (Z cycles)">
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
          </>
        )}
        <label className="me-toggle" title="WASD fly speed (Shift x4)">Fly <NumField small min={1} step={10} value={String(flySpeed)} onCommit={v => setFlySpeed(+v)} /></label>
        <span className="me-spacer" />
        <div className="me-seg" title="View">
          <button onClick={() => view('top')} title="Top (Numpad 7)">Top</button>
          <button onClick={() => view('front')} title="Front (Numpad 1)">Front</button>
          <button onClick={() => view('iso')} title="Iso">Iso</button>
          <button className={ortho ? 'on' : ''} onClick={toggleOrtho} title="Orthographic (Numpad 5)">Ortho</button>
          <button onClick={frameSelected} title="Frame selected (F)">Frame</button>
          <button onClick={frameAll} title="Frame map (Home)">All</button>
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
