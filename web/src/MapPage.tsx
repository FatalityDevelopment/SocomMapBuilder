import { readEditorPreferences, writeEditorPreferences } from './viewport/editorPreferences';
import { ShortcutReference } from './components/ShortcutReference';
import { readRenderSettings, writeRenderSettings } from './viewport/renderSettings';
import type { ViewportStats } from './webview/zipper-core/viewport-core';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { ModelSummary } from './webview/model-editor/asset-client';
import { decodeMesh, type MeshWire } from './webview/model-editor/mesh-data';
import { MapViewport, type MapLayer, type MapTool } from './viewport/mapViewport';
import { LIGHTINGS, type Lighting, type Shading, type ViewPreset } from './webview/zipper-core/viewport-core';
import { LAYERS, errText, layerOf, movable, pointKindCounts, type ModelGroup } from './mapModel';

import { MenuBar, type Menu } from './components/MenuBar';
import { PageHeader } from './shared/PageHeader';
import { Outliner } from './components/Outliner';
import { PropsPanel, type PropTab } from './components/PropsPanel';
import { ViewportView } from './components/ViewportView';
import { NumField } from './components/NumField';
import { activePage, host } from './shared';
import { aiView, applyAiCamera, provideAiState } from './shared/aiBridge';
import { SHORTCUTS, TOOLS } from './mapShortcuts';
import { useMapKeys } from './useMapKeys';
import { useMapSession } from './useMapSession';
import { useMapEdits } from './useMapEdits';

const SHADINGS: Array<{ id: Shading; label: string }> = [
  { id: 'textured', label: 'Textured' },
  { id: 'solid', label: 'Solid' },
  { id: 'wireframe', label: 'Wire' },
  { id: 'xray', label: 'X-ray' },
];

const SHADING_ORDER: Shading[] = ['textured', 'solid', 'wireframe', 'xray'];

export function MapPage() {
  const [initialPreferences] = useState(readEditorPreferences);
  const [shortcutsOpen, setShortcutsOpen] = useState(false);
  const viewportRef = useRef<MapViewport | null>(null);
  const mapSession = useMapSession(viewportRef, (vp, keepCamera) => {
    if (!keepCamera) { setFlySpeedState(Math.round(vp.flySpeed)); }
    syncSelection();
  }, () => { setPlaceModel(null); setSelection([]); setSelectedPoint(null); });
  const { client, instancesRef, statusRef, status, setStatus, sceneNs, counts, instances, points, models, setModels,
    busy, progress, loading, loadWarnings, connectionIssue, message, messageIsError, say,
    retryScene, edit, history, saveCopy, exportPack, openDialog } = mapSession;
  const [openMenu, setOpenMenu] = useState<string | null>(null);
  const [query, setQuery] = useState('');
  const [libQuery, setLibQuery] = useState('');
  const [propTab, setPropTab] = useState<PropTab>('item');
  const [tool, setToolState] = useState<MapTool>('select');
  const [shading, setShadingState] = useState<Shading>(initialPreferences.shading);
  const [lighting, setLightingState] = useState<Lighting>(initialPreferences.lighting);
  const [grid, setGrid] = useState(initialPreferences.grid);
  const [snap, setSnap] = useState(initialPreferences.snap);
  const [snapMove, setSnapMove] = useState(initialPreferences.snapMove);
  const [snapRotate, setSnapRotate] = useState(initialPreferences.snapRotate);
  const [snapScale, setSnapScale] = useState(initialPreferences.snapScale);
  const [gizmoLocal, setGizmoLocal] = useState(initialPreferences.gizmoLocal);
  const [locked, setLocked] = useState<Set<MapLayer>>(() => new Set());
  const [ortho, setOrtho] = useState(initialPreferences.ortho);
  const [backfaces, setBackfaces] = useState(initialPreferences.backfaces);
  const [renderSettings, setRenderSettings] = useState(readRenderSettings);
  const [viewportStats, setViewportStats] = useState<ViewportStats | null>(null);
  const [flySpeed, setFlySpeedState] = useState(100);
  const [hover, setHover] = useState<string | null>(null);
  const [selection, setSelection] = useState<number[]>([]);
  const [selectedPoint, setSelectedPoint] = useState<string | null>(null);
  const [openNodes, setOpenNodes] = useState<Set<string>>(() => new Set(['L:reference', 'L:clutter']));
  const [hidden, setHidden] = useState<{ layers: Set<MapLayer>; models: Set<string> }>(() => ({ layers: new Set<MapLayer>(['sky']), models: new Set<string>() }));
  const [placeModel, setPlaceModel] = useState<ModelSummary | null>(null);
  const [placeYaw, setPlaceYaw] = useState(initialPreferences.placeYaw);
  const [placeScale, setPlaceScale] = useState(initialPreferences.placeScale);
  const [randomYaw, setRandomYaw] = useState(initialPreferences.randomYaw);
  const [randomScale, setRandomScale] = useState(initialPreferences.randomScale);
  const [minPlaceScale, setMinPlaceScale] = useState(initialPreferences.minPlaceScale);
  const [maxPlaceScale, setMaxPlaceScale] = useState(initialPreferences.maxPlaceScale);
  const [alignSurface, setAlignSurface] = useState(initialPreferences.alignSurface);

  const dirtyCount = status?.dirty.length ?? 0;
  const single = selection.length === 1 ? instances[selection[0]] ?? null : null;
  const movableCount = selection.filter(i => movable(instances[i])).length;

  const { commitTransforms, setField, movePointField, deleteSelection, duplicateSelection, copySelection, paste,
    editSelection, dropToGround, place, clipboardRef } = useMapEdits(mapSession, viewportRef, selection,
      next => setTool(next), setPropTab, { model: placeModel });

  // A prop's model in the Model Editor: the same package (and session) with that model selected.
  const openModel = (index: number) => {
    const inst = instancesRef.current[index];
    const s = statusRef.current;
    if (!inst?.source || !s) { return; }
    const cut = inst.source.indexOf(':');
    host.open('models', s.path, { archive: inst.source.slice(0, cut), name: inst.source.slice(cut + 1) });
    say(`Opening ${inst.model} in the Model Editor…`);
  };

  // Copies a model from another package and selects it for placement.
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

  const setLighting = (mode: Lighting) => {
    setLightingState(mode);
    viewportRef.current?.setLighting(mode);
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
        { label: 'Reload', action: () => void retryScene(), disabled: () => !status || busy || loading },
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
        { label: 'Keyboard shortcuts…', shortcut: SHORTCUTS.help.label, action: () => setShortcutsOpen(true) },
        { separator: true, label: '' },
        { label: 'Fly: W A S D, Q / E down / up, Shift faster (viewport focused)' },
        { label: 'Orbit: middle drag or Alt+left drag · Pan: right drag · Zoom: wheel' },
        { label: 'Select: click, Shift adds, Ctrl removes, drag for box' },
        { label: `Copy / paste clutter: ${SHORTCUTS.copy.label} / ${SHORTCUTS.paste.label} (also into another package with the model)` },
        { label: `Tools: ${TOOLS.map(t => `${t.key} ${t.id}`).join(' · ')}` },
        { label: 'Double-click a prop to open its model in the Model Editor' },
      ],
    },
  ];

  // What each shortcut does (SHORTCUTS has the keys).
  useMapKeys({
    help: () => setShortcutsOpen(true),
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
    back: () => void view('back'),
    left: () => void view('left'),
    bottom: () => void view('bottom'),
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
  }, !shortcutsOpen);

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
      onPlace: transform => void handlersRef.current.place(transform),
      onOpenModel: index => handlersRef.current.openModel(index),
    });
    vp.editable = false;
    vp.onStats = setViewportStats;
    vp.shading = initialPreferences.shading;
    vp.setLighting(initialPreferences.lighting);
    vp.setBackfaces(initialPreferences.backfaces);
    vp.showGrid = initialPreferences.grid;
    vp.showAxes = initialPreferences.grid;
    vp.gizmoLocal = initialPreferences.gizmoLocal;
    if (initialPreferences.ortho) { vp.toggleOrtho(); }
    vp.setSnap(initialPreferences.snap ? initialPreferences.snapMove : null,
      initialPreferences.snap ? initialPreferences.snapRotate : null, initialPreferences.snap ? initialPreferences.snapScale : null);

    viewportRef.current = vp;
  }, []);

  useEffect(() => () => { viewportRef.current?.dispose(); viewportRef.current = null; }, []);

  useEffect(() => {
    viewportRef.current?.setPlacementOptions({ yaw: placeYaw, scale: placeScale, randomYaw, randomScale,
      minScale: minPlaceScale, maxScale: maxPlaceScale, alignSurface });
  }, [placeYaw, placeScale, randomYaw, randomScale, minPlaceScale, maxPlaceScale, alignSurface]);

  useEffect(() => {
    let cancelled = false;
    const vp = viewportRef.current;
    vp?.setPlacementMesh(null);
    if (!vp || !placeModel || !status) { return; }
    const session = client.session;
    void client.mesh(placeModel.archive, placeModel.name).then(wire => {
      if (!cancelled && session === client.session) { vp.setPlacementMesh(decodeMesh(wire as MeshWire)); }
    }).catch(error => {
      if (!cancelled) { say(`Could not preview ${placeModel.name}: ${errText(error)}`, true); }
    });
    return () => { cancelled = true; vp.setPlacementMesh(null); };
  }, [placeModel, status?.path, status?.revision, say]);

  useEffect(() => {
    writeRenderSettings(renderSettings);
    viewportRef.current?.setRenderSettings(renderSettings);
    setViewportStats(null);
  }, [renderSettings]);

  useEffect(() => {
    writeEditorPreferences({ shading, lighting, grid, snap, snapMove, snapRotate, snapScale, gizmoLocal, ortho, backfaces,
      placeYaw, placeScale, randomYaw, randomScale, minPlaceScale, maxPlaceScale, alignSurface });
  }, [shading, lighting, grid, snap, snapMove, snapRotate, snapScale, gizmoLocal, ortho, backfaces,
    placeYaw, placeScale, randomYaw, randomScale, minPlaceScale, maxPlaceScale, alignSurface]);
  // ------------------------------------------------------------------ AI (MCP)

  // ui_view on this page: shading, lighting, camera preset, framing.
  const aiViewRequest = aiView();
  useEffect(() => {
    if (!aiViewRequest || activePage() !== 'map') { return; }
    if (aiViewRequest.shading) { setShading(aiViewRequest.shading); }
    if (aiViewRequest.lighting) { setLighting(aiViewRequest.lighting); }
    if (aiViewRequest.view) { view(aiViewRequest.view); }
    if (aiViewRequest.frame === 'selected') { frameSelected(); } else if (aiViewRequest.frame === 'all') { frameAll(); }
    applyAiCamera(aiViewRequest);
    // Only a new request should run.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [aiViewRequest]);

  // What ui_state reports for this page (read through a ref: always the latest render's values).
  const aiStateRef = useRef<() => unknown>(() => null);
  aiStateRef.current = () => ({
    path: status?.path ?? null,
    session: status?.session ?? null,
    revision: status?.revision ?? null,
    unsaved: status?.dirty ?? [],
    shading, lighting, tool,
    instances: instances.length,
    selection: selection.slice(0, 50).map(i => {
      const inst = instances[i];
      return inst && { model: inst.model, kind: inst.kind, geoIndex: inst.geoIndex, clutterIndex: inst.clutterIndex, position: inst.position };
    }),
    selectedPoint,
  });
  useEffect(() => provideAiState('map', () => aiStateRef.current()), []);


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
    <div className="me-app mp-app has-header" onPointerDown={e => { if (!(e.target as HTMLElement).closest('.me-menu')) { setOpenMenu(null); } }}>
      <PageHeader icon="globe" title="Map Builder" subtitle="Props, clutter and spawn points of SOCOM II maps" />
      <MenuBar {...menuBarProps} />
      {shortcutsOpen && <ShortcutReference onClose={() => setShortcutsOpen(false)} />}

      <div className="me-header">
        <div className="me-seg" title={`Shading (${SHORTCUTS.shading.label} cycles)`}>
          {SHADINGS.map(s => (
            <button key={s.id} className={shading === s.id ? 'on' : ''} onClick={() => setShading(s.id)}>{s.label}</button>
          ))}
        </div>
        <div className="me-seg" title="Lighting">
          {LIGHTINGS.map(l => (
            <button key={l.id} className={lighting === l.id ? 'on' : ''} onClick={() => setLighting(l.id)} title={l.title}>{l.label}</button>
          ))}
        </div>
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

      <div className="me-header mp-render-settings" aria-label="Rendering settings">
        <label className="me-toggle">Resolution
          <select aria-label="Render resolution" value={renderSettings.resolution} onChange={e => setRenderSettings(s => ({ ...s, resolution: Number(e.target.value) }))}>
            <option value={1}>100%</option><option value={0.75}>75%</option><option value={0.5}>50%</option>
          </select>
        </label>
        <label className="me-toggle">Textures
          <select aria-label="Texture filtering" value={renderSettings.filtering} onChange={e => setRenderSettings(s => ({ ...s, filtering: e.target.value === 'pixel' ? 'pixel' : 'smooth' }))}>
            <option value="smooth">Smooth</option><option value="pixel">Pixel</option>
          </select>
        </label>
        <label className="me-toggle"><input type="checkbox" checked={renderSettings.showStats} onChange={e => setRenderSettings(s => ({ ...s, showStats: e.target.checked }))} /> Statistics</label>
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
            loading={loading}
            warnings={loadWarnings}
            connectionIssue={connectionIssue}
            onRetry={() => void retryScene()}
            stats={renderSettings.showStats ? viewportStats : null}
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
            randomScale={randomScale}
            setRandomScale={setRandomScale}
            minPlaceScale={minPlaceScale}
            setMinPlaceScale={setMinPlaceScale}
            maxPlaceScale={maxPlaceScale}
            setMaxPlaceScale={setMaxPlaceScale}
            alignSurface={alignSurface}
            setAlignSurface={setAlignSurface}
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
