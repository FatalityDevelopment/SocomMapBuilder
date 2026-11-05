import { readEditorPreferences, writeEditorPreferences } from './viewport/editorPreferences';
import { ShortcutReference } from './components/ShortcutReference';
import { readRenderSettings, writeRenderSettings } from './viewport/renderSettings';
import type { ViewportStats } from './webview/zipper-core/viewport-core';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { MapProblem, MapValidation, ModelSummary, PrefabDetail, PrefabSummary } from './webview/model-editor/asset-client';
import { MapViewport, type CameraState, type MapLayer, type MarkerPick, type MapTool, type SnapTarget } from './viewport/mapViewport';
import { CommandPalette } from './shared/CommandPalette';
import { HistoryList } from './shared/HistoryList';
import { commandsFromMenus, formatDistance } from './shared/commands';
import type { PivotMode } from './viewport/arrange';
import { analyzeScene } from './viewport/mapAnalysis';
import { ThumbnailRenderer, type ThumbPart } from './viewport/thumbnails';
import { AssetBrowser, ASSET_DRAG_TYPE, type AssetDrag } from './components/AssetBrowser';
import { LIGHTINGS, type Lighting, type Shading, type ViewPreset } from './webview/zipper-core/viewport-core';
import { pathDetail, regionDetail } from './viewport/mapShapes';
import { LAYERS, errText, isCaFamily, layerOf, missionLabel, movable, pointKindCounts, problemTarget, type ModelGroup } from './mapModel';

import { MenuBar, type Menu } from './components/MenuBar';
import { PageHeader } from './shared/PageHeader';
import { Outliner } from './components/Outliner';
import { PropsPanel, type PropTab } from './components/PropsPanel';
import { MapActionsPanel } from './components/MapActionsPanel';
import { MissionPanel } from './components/MissionPanel';
import { actionMarkers } from './viewport/actionMarkers';
import { planFigures, weaponForValve, weaponLabel, type FigurePlan } from './viewport/actorFigures';
import { Minimap } from './components/Minimap';
import { actionBlipsFrom, minimapBlips, squareAround, type ImagePlacement } from './viewport/minimap';
import type { PoseClip } from './webview/zipper-core/skinning';
import type { MapActions, MapHud, MapTables } from './webview/model-editor/asset-client';
import { ViewportView } from './components/ViewportView';
import { NumField } from './components/NumField';
import { activePage, host, openRequest } from './shared';
import { aiView, applyAiCamera, provideAiState } from './shared/aiBridge';
import { SHORTCUTS, TOOLS } from './mapShortcuts';
import { useMapKeys } from './useMapKeys';
import { mapViewOnly, useMapSession } from './useMapSession';
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
  }, () => { setPlaceModel(null); setPlacePrefab(null); setSelection([]); setSelectedPoint(null); thumbsRef.current?.clear(); });
  const { client, instancesRef, statusRef, status, setStatus, sceneNs, counts, instances, points, shapes, models, setModels,
    busy, progress, loading, loadWarnings, connectionIssue, message, messageIsError, say,
    retryScene, edit, history, saveCopy, saveDisc, exportPack, openDialog, loadModel, texturePixels, jump, sceneReady, reload } = mapSession;
  const ca = isCaFamily(status?.game);
  const ftb = /^SOCOM: Fireteam Bravo/.test(status?.game ?? '');
  const battlefront = status?.game === 'Star Wars: Battlefront';
  const gta = status?.game === 'GTA: San Andreas';
  const viewOnly = mapViewOnly(status);
  const socomTools = !!status && !viewOnly && !ca && !battlefront && !gta;
  const canPlace = socomTools || (gta && !viewOnly && !status?.mission?.startsWith('lod-'));
  const [openMenu, setOpenMenu] = useState<string | null>(null);
  // The Map tab's check: rerun while the tab is open whenever the session changes.
  const [check, setCheck] = useState<MapValidation | null>(null);
  const [checking, setChecking] = useState(false);
  const runCheck = async () => {
    if (!client.session) { return; }
    setChecking(true);
    try {
      const server = await client.validate();
      // The checks that need the drawn scene: floating, stacked and dense placements.
      const scene = viewportRef.current ? analyzeScene(viewportRef.current.measureScene()) : [];
      const problems = [...server.problems, ...scene];
      const counts: Record<string, number> = {};
      for (const pr of problems) { counts[pr.severity] = (counts[pr.severity] ?? 0) + 1; }
      setCheck({ ...server, problems, counts, ok: server.ok });
    }
    catch (err) { say(`Map check failed: ${errText(err)}`, true); }
    finally { setChecking(false); }
  };
  const [query, setQuery] = useState('');
  const [propTab, setPropTab] = useState<PropTab>('item');
  const request = openRequest();
  useEffect(() => { if (request?.actionsFor) setPropTab('actions'); }, [request]);
  // Each edit (and each package) gets a fresh check while the Map tab is open.
  useEffect(() => {
    if (propTab === 'map' && status && !ca) { void runCheck(); }
  }, [propTab, status?.session, status?.revision]);
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
  const [snapTarget, setSnapTargetState] = useState<SnapTarget>(initialPreferences.snapTarget);
  const [pivotMode, setPivotModeState] = useState<PivotMode>(initialPreferences.pivotMode);
  const [placeAs, setPlaceAs] = useState(initialPreferences.placeAs);
  const [brushRadius, setBrushRadius] = useState(initialPreferences.brushRadius);
  const [brushSpacing, setBrushSpacing] = useState(initialPreferences.brushSpacing);
  const [brushDensity, setBrushDensity] = useState(initialPreferences.brushDensity);
  const [brushSlope, setBrushSlope] = useState(initialPreferences.brushSlope);
  const [brushErase, setBrushErase] = useState(initialPreferences.brushErase);
  const [prefabs, setPrefabs] = useState<PrefabSummary[]>([]);
  const [placePrefab, setPlacePrefab] = useState<PrefabDetail | null>(null);
  const [dragInfo, setDragInfo] = useState<string | null>(null);
  const [paletteOpen, setPaletteOpen] = useState(false);
  const [measure, setMeasure] = useState<string | null>(null);
  const [walking, setWalking] = useState(false);
  const [hiddenCount, setHiddenCount] = useState(0);
  // Map object actions and mission tables for this revision, and the 3D markers drawn from them.
  const [actionData, setActionData] = useState<MapActions | null>(null);
  const [actionError, setActionError] = useState('');
  const [tables, setTables] = useState<MapTables | null>(null);
  const [tablesError, setTablesError] = useState('');
  const [selectedObjective, setSelectedObjective] = useState<number | null>(null);
  // An action node picked by its icon that draws nothing to select (a door in the world model).
  const [actionNode, setActionNode] = useState<number | null>(null);
  const [actionMarkersOn, setActionMarkersOn] = useState(initialPreferences.actionMarkers);
  const [objectiveMarkersOn, setObjectiveMarkersOn] = useState(initialPreferences.objectiveMarkers);
  const [figuresOn, setFiguresOn] = useState(initialPreferences.actorFigures);
  // How each actor is drawn (by actor name), for the Mission tab.
  const [figurePlans, setFigurePlans] = useState<FigurePlan[]>([]);
  // The HUD minimap: the game's radar art and tactical map (ASSET_MAP_HUD), and a top-down render for maps without one.
  const [minimapOn, setMinimapOn] = useState(initialPreferences.minimap);
  const [minimapRange, setMinimapRange] = useState(initialPreferences.minimapRange);
  const [hud, setHud] = useState<MapHud | null>(null);
  const [topDown, setTopDown] = useState<{ url: string; placement: ImagePlacement } | null>(null);
  // Weapons the user picked for character types instead of their loadout's (view only; this viewer's browser):
  // chartype -> "archive:name", or "none". The key is new: picks stored under the old key were stand-ins for missing loadouts.
  const [actorWeapons, setActorWeapons] = useState<Record<string, string>>(() => {
    try { return JSON.parse(localStorage.getItem('mapbuilder.actorWeaponPicks') ?? '{}') as Record<string, string>; } catch { return {}; }
  });
  const chooseActorWeapon = (chartype: string, source: string) => {
    const next = { ...actorWeapons, [chartype]: source };
    if (!source) { delete next[chartype]; }
    setActorWeapons(next);
    try { localStorage.setItem('mapbuilder.actorWeaponPicks', JSON.stringify(next)); } catch { /* storage unavailable */ }
  };
  const clipListRef = useRef<{ session: string; clips: Map<string, string> } | null>(null);
  const poseCacheRef = useRef(new Map<string, PoseClip | null>());
  // Camera bookmarks, per package (browser storage: a convenience, kept per viewer).
  const [bookmarks, setBookmarks] = useState<Array<{ name: string; camera: CameraState }>>([]);
  const thumbsRef = useRef<ThumbnailRenderer | null>(null);

  const dirtyCount = status?.dirty.length ?? 0;
  const single = selection.length === 1 ? instances[selection[0]] ?? null : null;
  const movableCount = ca && selection.length > 1 ? 0 : selection.filter(i => movable(instances[i])).length;

  const { commitTransforms, setField, movePointField, deleteSelection, duplicateSelection, copySelection, paste,
    editSelection, dropToGround, place, scatter, erase, arrange, clipboardRef } = useMapEdits(mapSession, viewportRef, selection,
      next => setTool(next), setPropTab, { model: placeModel, prefab: placePrefab?.name ?? null, placeAs });

  // ------------------------------------------------------------------ assets: models, prefabs, thumbnails

  const sourceOf = (model: string): string | null => {
    const m = models.find(x => x.name.toLowerCase() === model.toLowerCase());
    return m ? `${m.archive}:${m.name}` : null;
  };
  // A prefab's items as ghost / thumbnail parts (items whose model this package lacks are left out).
  const prefabParts = async (detail: PrefabDetail): Promise<ThumbPart[]> => {
    const parts = await Promise.all(detail.items.map(async (it): Promise<ThumbPart | null> => {
      const source = sourceOf(it.model);
      const model = source ? await loadModel(source) : null;
      return model ? { mesh: model.mesh, textureNamespace: model.textureNamespace, matrix: it.matrix } : null;
    }));
    return parts.filter((x): x is ThumbPart => !!x);
  };
  const thumbnail = (key: string) => {
    thumbsRef.current ??= new ThumbnailRenderer();
    return thumbsRef.current.get(key, async () => {
      if (key.startsWith('prefab:')) { return prefabParts(await client.prefab(key.slice(7))); }
      const model = await loadModel(key);
      return model ? [{ mesh: model.mesh, textureNamespace: model.textureNamespace }] : null;
    }, texturePixels);
  };
  useEffect(() => () => { thumbsRef.current?.dispose(); thumbsRef.current = null; }, []);

  const refreshPrefabs = async () => {
    try { setPrefabs((await client.listPrefabs()).prefabs); }
    catch (err) { say(`Could not list prefabs: ${errText(err)}`, true); }
  };
  useEffect(() => { if (socomTools) { void refreshPrefabs(); } }, [status?.session, socomTools]);

  const pickModel = (m: ModelSummary) => {
    setPlacePrefab(null);
    setPlaceModel(m);
    if (tool !== 'paint') { setTool('place'); }
  };
  const pickPrefab = async (name: string) => {
    try {
      const detail = await client.prefab(name);
      setPlaceModel(null);
      setPlacePrefab(detail);
      setTool('place');
    } catch (err) { say(`Could not open prefab ${name}: ${errText(err)}`, true); }
  };
  const savePrefab = async (name: string) => {
    const sel = selection.map(i => instancesRef.current[i]).filter(i => i && movable(i));
    const geoIndices = sel.filter(i => i.clutterIndex === null && i.geoIndex !== null).map(i => i.geoIndex!);
    const clutterIndices = sel.filter(i => i.clutterIndex !== null).map(i => i.clutterIndex!);
    if (!geoIndices.length && !clutterIndices.length) { say('Select props or clutter to save as a prefab.', true); return; }
    try {
      const exists = prefabs.some(f => f.name.toLowerCase() === name.toLowerCase());
      if (exists && !window.confirm(`Replace the prefab ${name}?`)) { return; }
      const r = await client.savePrefab({ name, geoIndices, clutterIndices, replace: exists });
      thumbsRef.current?.clear();
      await refreshPrefabs();
      say(`Saved prefab ${r.name}: ${r.items} item(s) (${r.models.join(', ')}). Drag it from Assets › Prefabs to place it.`);
    } catch (err) { say(`Could not save the prefab: ${errText(err)}`, true); }
  };
  const deletePrefab = async (name: string) => {
    try {
      await client.deletePrefab(name);
      if (placePrefab?.name === name) { setPlacePrefab(null); }
      thumbsRef.current?.clear();
      await refreshPrefabs();
      say(`Deleted prefab ${name}.`);
    } catch (err) { say(`Could not delete the prefab: ${errText(err)}`, true); }
  };

  // A tile dropped on the map: placed where it lands, with the Place tab's rotation and scale.
  const dropAsset = async (e: React.DragEvent) => {
    const raw = e.dataTransfer.getData(ASSET_DRAG_TYPE);
    const vp = viewportRef.current;
    if (!raw || !vp || !canPlace || (gta && (locked.has('reference') || hidden.layers.has('reference')))) { return; }
    e.preventDefault();
    const drag = JSON.parse(raw) as AssetDrag;
    const at = vp.placementAt(e.clientX, e.clientY);
    if (!at) { say('Drop onto the map: nothing is under that point.', true); return; }
    if (drag.kind === 'prefab') { if (!socomTools) { return; } await place(at, { prefab: drag.name }); return; }
    const model = models.find(m => m.archive === drag.archive && m.name === drag.name);
    if (model && (!gta || model.canPlace)) { setPlacePrefab(null); setPlaceModel(model); await place(at, { model }); }
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

  // Copies a model from another package and selects it for placement.
  const importFromPackage = async () => {
    const session = client.session;
    if (!session || !socomTools) { return; }
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

  // ------------------------------------------------------------------ bookmarks, hide / isolate, walk

  const bookmarkKey = (path: string) => `mapbuilder.bookmarks.${path.toLowerCase()}`;
  useEffect(() => {
    if (!status?.path) { setBookmarks([]); return; }
    try { setBookmarks(JSON.parse(localStorage.getItem(bookmarkKey(status.path)) ?? '[]')); } catch { setBookmarks([]); }
  }, [status?.path]);
  const storeBookmarks = (next: Array<{ name: string; camera: CameraState }>) => {
    setBookmarks(next);
    try { if (status?.path) { localStorage.setItem(bookmarkKey(status.path), JSON.stringify(next)); } } catch { /* storage unavailable */ }
  };
  // The app's own dialog, not window.prompt (which embedded browsers may block).
  // SOCOM 3 / CA: switch the session to another mission of the location, then redraw its points (models stay cached).
  const pickMission = async (mission: string) => {
    if (!status || mission === status.mission) { return; }
    try {
      await client.mapPoints(mission);
      setStatus(await client.status());
      setSelectedPoint(null);
      // A Battlefront "mission" (a GTA area) is a whole map elsewhere: frame it. CA missions share the location's models.
      await reload(!battlefront && !gta, false);
      say(`${mission}: ${mapSession.pointsRef.current.length} points.`);
    } catch (error) {
      say(`Mission ${mission} could not be read: ${errText(error)}`, true);
    }
  };

  const addBookmark = async () => {
    const vp = viewportRef.current;
    if (!vp || !status) { return; }
    const camera = vp.cameraState();
    const name = (await host.prompt('Bookmark this view', [{ key: 'name', label: 'Name', value: `View ${bookmarks.length + 1}` }], 'Save'))?.name?.trim();
    if (!name) { return; }
    storeBookmarks([...bookmarks.filter(b => b.name !== name), { name, camera }]);
    say(`Saved the view as “${name}” (View › Bookmarks).`);
  };
  const deleteBookmarkAsk = async () => {
    const names = bookmarks.map(b => b.name).join(', ');
    const n = (await host.prompt('Delete a bookmark', [{ key: 'name', label: `Which one? (${names})`, value: bookmarks.at(-1)?.name ?? '' }], 'Delete'))?.name?.trim();
    if (n) { deleteBookmark(n); }
  };
  const goToBookmark = (camera: CameraState) => { viewportRef.current?.setCameraState(camera); setWalking(false); };
  const deleteBookmark = (name: string) => storeBookmarks(bookmarks.filter(b => b.name !== name));

  // ------------------------------------------------------------------ actions, mission tables, markers

  useEffect(() => {
    let live = true;
    if (!status || ((ca || viewOnly) && !status.missions?.length)) { setActionData(null); setTables(null); return; }
    setActionError(''); setTablesError('');
    if (ca || viewOnly || gta) { setActionData(null); } else {
      client.mapActions().then(r => { if (live) { setActionData(r); } }).catch(e => { if (live) { setActionData(null); setActionError(errText(e)); } });
    }
    client.mapTables().then(r => { if (live) { setTables(r); } }).catch(e => { if (live) { setTables(null); setTablesError(errText(e)); } });
    return () => { live = false; };
  }, [status?.session, status?.revision, viewOnly, status?.mission]);
  useEffect(() => { setSelectedObjective(null); setActionNode(null); }, [status?.session]);
  useEffect(() => { if (selection.length) { setActionNode(null); } }, [selection]);
  useEffect(() => {
    viewportRef.current?.setActionMarkers(actionData ? actionMarkers(actionData, instances) : []);
  }, [actionData, instances]);
  useEffect(() => {
    viewportRef.current?.setObjectiveMarkers((tables?.objectives ?? []).filter(o => o.location).map(o => ({
      row: o.row, x: o.location![0], z: o.location![1], secondary: o.secondary,
      label: (o.name && tables?.texts[o.name]) || o.name || `Objective ${o.row + 1}`,
    })));
  }, [tables, instances]);
  useEffect(() => { viewportRef.current?.setSelectedObjective(selectedObjective); }, [selectedObjective]);
  const weaponModels = useMemo(() => models.filter(m => m.kind === 'weapon').map(m => `${m.archive}:${m.name}`), [models]);
  // Actors as characters at their start points, posed by a standing clip, holding their chartype's weapon.
  useEffect(() => {
    let live = true;
    const vp = viewportRef.current;
    const session = status?.session;
    if (!vp || !session || !tables || !figuresOn) { vp?.setActorFigures([]); setFigurePlans([]); return; }
    void (async () => {
      try {
        if (clipListRef.current?.session !== session) {
          const list = await client.listAnimations();
          clipListRef.current = { session, clips: new Map(list.clips.map(c => [c.name, c.source])) };
          poseCacheRef.current.clear();
        }
        const clips = clipListRef.current.clips;
        const armory = (tables.mission?.valves ?? []).filter(v => v.name?.startsWith('Enable') && v.value === 1)
          .map(v => weaponForValve(v.name!, weaponModels)).filter((m): m is string => !!m);
        const plans = planFigures(tables.actors, points, { ca, clips: [...clips.keys()], armory, chosen: actorWeapons,
          multiplayer: /^mp\d/i.test(status?.mission ?? status?.mapNamespace ?? '') });
        if (live) { setFigurePlans(plans); }
        const figures = await Promise.all(plans.map(async plan => {
          const [character, weapon] = await Promise.all([loadModel(plan.model), plan.weapon ? loadModel(plan.weapon) : Promise.resolve(null)]);
          let clip: PoseClip | null = null;
          if (plan.clip) {
            if (!poseCacheRef.current.has(plan.clip)) {
              poseCacheRef.current.set(plan.clip, await client.animation(plan.clip, clips.get(plan.clip)).then(c => c as PoseClip).catch(() => null));
            }
            clip = poseCacheRef.current.get(plan.clip) ?? null;
          }
          const actor = tables.actors.find(a => (a.name ?? a.character) === plan.actor);
          return character ? { point: plan.point, actor: plan.actor, character, clip, weapon, weaponName: weapon ? weaponLabel(plan.weapon, actor) : null,
            heading: plan.heading, offset: plan.offset, clipName: plan.clip } : null;
        }));
        if (live && client.session === session) { vp.setActorFigures(figures.filter((f): f is NonNullable<typeof f> => !!f)); }
      } catch (err) { if (live) { say(`Could not draw the actors: ${errText(err)}`, true); } }
    })();
    return () => { live = false; };
  }, [tables, points, figuresOn, actorWeapons, weaponModels, status?.session, sceneReady]);
  const toggleFigures = () => setFiguresOn(on => !on);
  const toggleActionMarkers = () => {
    const next = !actionMarkersOn;
    setActionMarkersOn(next);
    if (viewportRef.current) { viewportRef.current.showActionMarkers = next; }
  };
  const toggleObjectiveMarkers = () => {
    const next = !objectiveMarkersOn;
    setObjectiveMarkersOn(next);
    if (viewportRef.current) { viewportRef.current.showObjectiveMarkers = next; }
  };
  const toggleMinimap = () => setMinimapOn(on => !on);
  useEffect(() => {
    let live = true;
    setHud(null);
    if (!minimapOn || !status?.session) { return; }
    client.mapHud().then(h => { if (live) { setHud(h); } }).catch(() => { if (live) { setHud(null); } });
    return () => { live = false; };
  }, [minimapOn, status?.session, status?.mission]);
  // The top-down picture: redrawn a moment after the scene settles (edits, layers), while the minimap is open.
  useEffect(() => {
    setTopDown(null);
    if (!minimapOn || !sceneReady) { return; }
    const timer = window.setTimeout(() => {
      const vp = viewportRef.current;
      const box = vp?.sceneExtent();
      if (!vp || !box) { return; }
      const placement = squareAround(box.minX, box.maxX, box.minZ, box.maxZ);
      const url = vp.renderTopDown(placement, 1024);
      if (url) { setTopDown({ url, placement }); }
    }, 1200);
    return () => window.clearTimeout(timer);
  }, [minimapOn, sceneReady, instances, hidden, shading, lighting]);
  const minimapView = useCallback(() => viewportRef.current?.viewHeading() ?? null, []);
  const blips = useMemo(() => !minimapOn ? [] : minimapBlips({
    points, actors: tables?.actors, objectives: tables?.objectives, texts: tables?.texts,
    actions: actionData ? actionBlipsFrom(actionMarkers(actionData, instances), instances) : [],
  }), [minimapOn, points, tables, actionData, instances]);

  // Select a placed node's prop (the node itself or the first placement drawn under it) and frame it.
  const focusNode = (geoIndex: number) => {
    const list = instancesRef.current;
    let i = list.findIndex(inst => inst.geoIndex === geoIndex);
    if (i < 0 && actionData) {
      const node = actionData.targets.find(t => t.index === geoIndex);
      const under = new Set(actionData.targets.filter(t => node && t.path.startsWith(node.path + '/')).map(t => t.index));
      i = list.findIndex(inst => inst.geoIndex !== null && under.has(inst.geoIndex));
    }
    if (i < 0) {
      viewportRef.current?.clearSelection();
      setActionNode(geoIndex);
      setPropTab('actions');
      return;
    }
    setActionNode(null);
    viewportRef.current?.select([i]);
    viewportRef.current?.frameSelected();
    setPropTab('actions');
  };
  const showObjective = (row: number) => { setSelectedObjective(row); viewportRef.current?.frameObjective(row); };
  const pickMarker = (pick: MarkerPick) => {
    if (pick.kind === 'action') { setActionNode(pick.instance < 0 ? pick.geoIndex : null); setPropTab('actions'); return; }
    setSelectedObjective(pick.row);
    setPropTab('mission');
  };

  const hideSelection = () => { const vp = viewportRef.current; if (vp && selection.length) { vp.hideInstances(selection); setHiddenCount(vp.hiddenCount); } };
  const isolateSelection = () => { const vp = viewportRef.current; if (vp && selection.length) { vp.isolateInstances(selection); setHiddenCount(vp.hiddenCount); } };
  const unhideAll = () => { const vp = viewportRef.current; if (vp) { vp.unhideInstances(); setHiddenCount(0); } };

  const toggleWalk = () => {
    const vp = viewportRef.current;
    if (!vp) { return; }
    const next = !vp.isWalking;
    vp.setWalk(next);
    setWalking(next);
    if (next) { say('Walk mode: W A S D walk (Shift runs), drag to look round, Esc to stop.'); }
  };

  // ------------------------------------------------------------------ view

  const setTool = (next: MapTool) => {
    if ((!canPlace && next === 'place') || (!socomTools && next === 'paint') || (gta && next === 'scale')) { return; }
    setToolState(next);
    if (viewportRef.current) { viewportRef.current.tool = next; }
    if (next === 'place' || next === 'paint') { setPropTab('place'); }
  };

  useEffect(() => {
    if ((tool === 'place' && !canPlace) || (tool === 'paint' && !socomTools) || (tool === 'scale' && gta)) { setTool('select'); }
    if ((ca && (propTab === 'place' || propTab === 'actions')) || (gta && (propTab === 'actions' || propTab === 'mission'))) { setPropTab('item'); }
  }, [ca, gta, canPlace, socomTools, propTab, tool]);

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

  const setSnapTarget = (t: SnapTarget) => { setSnapTargetState(t); if (viewportRef.current) { viewportRef.current.snapTarget = t; } };
  const setPivotMode = (m: PivotMode) => { setPivotModeState(m); if (viewportRef.current) { viewportRef.current.pivotMode = m; } };

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
  const pickProblem = (problem: MapProblem) => {
    const target = problemTarget(problem, instancesRef.current);
    if (!target) { say('That item is not in the loaded scene (reload the map).', true); return; }
    if ('point' in target) { pickPoint(target.point); } else { pickInstance(target.instance, false); }
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

  const layerCounts = useMemo(() => {
    const out = new Map<MapLayer, number>();
    for (const inst of instances) { const l = layerOf(inst); out.set(l, (out.get(l) ?? 0) + 1); }
    return out;
  }, [instances]);
  const layerCount = (layer: MapLayer) => layer === 'paths' ? shapes.paths.length : layer === 'regions' ? shapes.regions.length : layerCounts.get(layer) ?? 0;
  // Paths and regions are listed only on maps that have them (Battlefront).
  const layers = useMemo(() => LAYERS.filter(l => (l.id !== 'paths' || shapes.paths.length > 0) && (l.id !== 'regions' || shapes.regions.length > 0)), [shapes]);
  const shapeRows = useMemo(() => ({
    paths: shapes.paths.map(p => ({ name: p.name, detail: pathDetail(p) })),
    regions: shapes.regions.map(r => ({ name: r.name, detail: regionDetail(r) })),
  }), [shapes]);
  const pickShape = (layer: 'paths' | 'regions', name: string) => {
    const vp = viewportRef.current;
    if (!vp) { return; }
    if (vp.isLayerHidden(layer)) { toggleLayer(layer); }
    vp.frameShape(layer, name);
    say(`${name}: ${layer === 'paths' ? pathDetail(shapes.paths.find(p => p.name === name)!) : regionDetail(shapes.regions.find(r => r.name === name)!)}`);
  };
  // Read after the viewport builds the scene (it runs before the instances state lands).
  const skyInWorld = useMemo(() => viewportRef.current?.skyInWorld() ?? [], [instances]);
  const selectionSummary = useMemo(() => {
    const by = new Map<string, number>();
    for (const i of selection) { const m = instances[i]?.model ?? '?'; by.set(m, (by.get(m) ?? 0) + 1); }
    return [...by].map(([m, n]) => `${n}× ${m}`).join(', ');
  }, [selection, instances]);

  const pointInfo = points.find(p => p.name === selectedPoint) ?? null;
  const renamePoint = async (name: string) => {
    const mission = status?.mission;
    const revision = status?.revision;
    const session = status?.session;
    const answer = await host.prompt('Rename point', [{
      key: 'name', label: 'Point name', value: name, pattern: /^[!-~][ -~]{0,13}[!-~]$/, hint: 'Use 2–15 printable ASCII characters with spaces only between words.'
    }], 'Rename');
    if (!answer?.name || answer.name === name || session !== client.session) { return; }
    if (await edit(`Rename ${name}`, { op: 'setPoint', name, newName: answer.name, mission, revision })) {
      viewportRef.current?.selectPoint(answer.name);
    }
  };
  const countList = Object.entries(counts).map(([kind, count]) => ({ kind, count }));

  // ------------------------------------------------------------------ menus & keys

  const menus: Menu[] = [
    {
      id: 'file', label: 'File', items: [
        { label: 'Open package…', shortcut: SHORTCUTS.open.label, action: () => void openDialog() },
        { label: 'Reload', action: () => void retryScene(), disabled: () => !status || busy || loading },
        { label: 'Import from package…', action: () => void importFromPackage(), disabled: () => !socomTools || busy },
        { separator: true, label: '' },
        { label: 'Save copy…', shortcut: SHORTCUTS.save.label, action: () => void saveCopy(), disabled: () => busy || loading || !dirtyCount },
        { label: 'Save to disc image…', action: () => void saveDisc(), disabled: () => busy || loading || !dirtyCount },
        { label: 'Export memory-card pack…', action: () => void exportPack(), disabled: () => !socomTools || !dirtyCount },
      ],
    },
    {
      id: 'edit', label: 'Edit', items: [
        { label: status?.canUndo ? `Undo ${status.canUndo}` : 'Undo', shortcut: SHORTCUTS.undo.label, action: () => void history(true), disabled: () => busy || loading || !status?.canUndo },
        { label: status?.canRedo ? `Redo ${status.canRedo}` : 'Redo', shortcut: SHORTCUTS.redo.label, action: () => void history(false), disabled: () => busy || loading || !status?.canRedo },
        { separator: true, label: '' },
        { label: 'Copy clutter', shortcut: SHORTCUTS.copy.label, action: copySelection, disabled: () => !socomTools || !movableCount },
        { label: 'Paste clutter', shortcut: SHORTCUTS.paste.label, action: () => void paste(), disabled: () => !socomTools || busy },
        { separator: true, label: '' },
        { label: 'Duplicate', shortcut: SHORTCUTS.duplicate.label, action: () => void duplicateSelection(), disabled: () => !canPlace || !movableCount },
        { label: 'Delete', shortcut: SHORTCUTS.delete.label, action: () => void deleteSelection(), disabled: () => !canPlace || !movableCount },
        { label: 'Drop to ground', shortcut: SHORTCUTS.drop.label, action: () => void dropToGround(), disabled: () => !movableCount },
        { separator: true, label: '' },
        { label: 'Open model…', action: () => openModel(selection[0]), disabled: () => selection.length !== 1 || !single?.source },
        { separator: true, label: '' },
        { label: 'History… (Map tab)', action: () => setPropTab('map'), disabled: () => !status },
      ],
    },
    {
      id: 'select', label: 'Select', items: [
        { label: 'None', shortcut: SHORTCUTS.escape.label, action: clearSelection },
        { label: 'All of this model', shortcut: SHORTCUTS.selectSame.label, action: selectSameModel, disabled: () => selection.length === 0 },
        { separator: true, label: '' },
        { label: 'Hide selected', shortcut: SHORTCUTS.hide.label, action: hideSelection, disabled: () => selection.length === 0 },
        { label: 'Isolate selected', shortcut: SHORTCUTS.isolate.label, action: isolateSelection, disabled: () => selection.length === 0 },
        { label: 'Show all hidden', shortcut: SHORTCUTS.unhide.label, action: unhideAll, disabled: () => hiddenCount === 0 },
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
        ...layers.map(l => ({ label: l.label, action: () => toggleLayer(l.id), checked: () => !hidden.layers.has(l.id) })),
        { separator: true, label: '' },
        { label: 'Back faces', action: toggleBackfaces, checked: () => backfaces },
        { label: 'Action icons', action: toggleActionMarkers, checked: () => actionMarkersOn },
        { label: 'Objective flags', action: toggleObjectiveMarkers, checked: () => objectiveMarkersOn },
        { label: 'Characters at start points', action: toggleFigures, checked: () => figuresOn },
        { label: 'Minimap', shortcut: SHORTCUTS.minimap.label, action: toggleMinimap, checked: () => minimapOn },
        { separator: true, label: '' },
        { label: 'Walk mode', shortcut: SHORTCUTS.walk.label, action: toggleWalk, checked: () => walking, disabled: () => !status },
        { label: 'Measure', shortcut: SHORTCUTS.toolMeasure.label, action: () => setTool('measure'), disabled: () => !status },
        { separator: true, label: '' },
        { label: 'Bookmark this view…', action: () => void addBookmark(), disabled: () => !status },
        ...bookmarks.map(b => ({ label: `Go to “${b.name}”`, action: () => goToBookmark(b.camera) })),
        ...(bookmarks.length ? [{ label: 'Delete a bookmark…', action: () => void deleteBookmarkAsk() }] : []),
      ],
    },
    {
      id: 'help', label: 'Help', items: [
        { label: 'Search commands…', shortcut: SHORTCUTS.commands.label, action: () => setPaletteOpen(true) },
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
    copy: () => { if (!socomTools || !selection.length) { return false; } copySelection(); },
    paste: () => { if (!socomTools || !clipboardRef.current.length) { return false; } void paste(); },
    front: () => void view('front'),
    right: () => void view('right'),
    top: () => void view('top'),
    back: () => void view('back'),
    left: () => void view('left'),
    bottom: () => void view('bottom'),
    ortho: toggleOrtho,
    duplicate: () => { if (canPlace) { void duplicateSelection(); } },
    selectSame: selectSameModel,
    delete: () => { if (canPlace) { void deleteSelection(); } },
    drop: () => { if (movableCount) { void dropToGround(); } },
    frame: () => void frameSelected(),
    frameAll: () => void frameAll(),
    toolSelect: () => setTool('select'),
    toolMove: () => setTool('move'),
    toolRotate: () => setTool('rotate'),
    toolScale: () => setTool('scale'),
    toolPlace: () => setTool('place'),
    toolPaint: () => setTool('paint'),
    toolMeasure: () => setTool('measure'),
    commands: () => setPaletteOpen(true),
    hide: () => { if (!selection.length) { return false; } hideSelection(); },
    isolate: () => { if (!selection.length) { return false; } isolateSelection(); },
    unhide: unhideAll,
    walk: toggleWalk,
    minimap: toggleMinimap,
    shading: cycleShading,
    escape: () => {
      setOpenMenu(null);
      if (walking) { toggleWalk(); return; }
      if (tool === 'measure') { viewportRef.current?.clearMeasure(); }
      if (tool !== 'select') { setTool('select'); } else { clearSelection(); }
    },
  }, !shortcutsOpen && !paletteOpen);

  // ------------------------------------------------------------------ viewport wiring

  // The viewport calls these; they are read through a ref so they always see the current state.
  const handlersRef = useRef({ commitTransforms, place, syncSelection, edit, setHover, setPropTab, openDialog, openModel, scatter, erase, setDragInfo, setMeasure, setWalking, pickMarker });
  handlersRef.current = { commitTransforms, place, syncSelection, edit, setHover, setPropTab, openDialog, openModel, scatter, erase, setDragInfo, setMeasure, setWalking, pickMarker };

  const onViewportReady = useCallback((canvas: HTMLCanvasElement) => {
    if (viewportRef.current) { return; }
    const vp = new MapViewport(canvas, {
      onSelectionChange: () => {
        handlersRef.current.syncSelection();
        if (viewportRef.current && (viewportRef.current.selected.size || viewportRef.current.selectedPoint)) {
          handlersRef.current.setPropTab(tab => tab === 'actions' && !viewportRef.current?.selectedPoint ? tab : 'item');
        }
      },
      onHover: text => handlersRef.current.setHover(text),
      onTransformCommit: (items, kind) => void handlersRef.current.commitTransforms(items, kind),
      onPointMove: (name, position) => void handlersRef.current.edit(`move point ${name}`, { op: 'movePoint', name, position }),
      onPlace: transform => void handlersRef.current.place(transform),
      onOpenModel: index => handlersRef.current.openModel(index),
      onScatter: matrices => void handlersRef.current.scatter(matrices),
      onErase: indices => void handlersRef.current.erase(indices),
      onDragInfo: text => handlersRef.current.setDragInfo(text),
      onMeasure: (units, points) => handlersRef.current.setMeasure(units !== null ? `Distance ${formatDistance(units)}` : points === 1 ? 'Measure: click the second point' : null),
      onWalkEnd: () => handlersRef.current.setWalking(false),
      onMarkerPick: pick => handlersRef.current.pickMarker(pick),
    });
    vp.editable = false;
    vp.onStats = setViewportStats;
    vp.shading = initialPreferences.shading;
    vp.setLighting(initialPreferences.lighting);
    vp.setBackfaces(initialPreferences.backfaces);
    vp.showGrid = initialPreferences.grid;
    vp.showAxes = initialPreferences.grid;
    vp.showActionMarkers = initialPreferences.actionMarkers;
    vp.showObjectiveMarkers = initialPreferences.objectiveMarkers;
    vp.gizmoLocal = initialPreferences.gizmoLocal;
    vp.snapTarget = initialPreferences.snapTarget;
    vp.pivotMode = initialPreferences.pivotMode;
    if (initialPreferences.ortho) { vp.toggleOrtho(); }
    vp.setSnap(initialPreferences.snap ? initialPreferences.snapMove : null,
      initialPreferences.snap ? initialPreferences.snapRotate : null, initialPreferences.snap ? initialPreferences.snapScale : null);

    viewportRef.current = vp;
  }, []);

  useEffect(() => () => { viewportRef.current?.dispose(); viewportRef.current = null; }, []);

  useEffect(() => {
    viewportRef.current?.setPlacementOptions({ yaw: placeYaw, scale: gta ? 1 : placeScale, randomYaw, randomScale: !gta && randomScale,
      minScale: minPlaceScale, maxScale: maxPlaceScale, alignSurface }, gta ? 'reference' : 'clutter');
  }, [gta, placeYaw, placeScale, randomYaw, randomScale, minPlaceScale, maxPlaceScale, alignSurface]);

  useEffect(() => {
    let cancelled = false;
    const vp = viewportRef.current;
    vp?.setPlacementMesh(null);
    if (!vp || (!placeModel && !placePrefab) || !status) { return; }
    const session = client.session;
    const parts = placePrefab ? prefabParts(placePrefab)
      : loadModel(`${placeModel!.archive}:${placeModel!.name}`).then(m => m ? [{ mesh: m.mesh }] : []);
    void parts.then(list => {
      if (!cancelled && session === client.session) { vp.setPlacementParts(list.length ? list : null); }
    }).catch(error => {
      if (!cancelled) { say(`Could not preview ${placePrefab?.name ?? placeModel?.name}: ${errText(error)}`, true); }
    });
    return () => { cancelled = true; vp.setPlacementMesh(null); };
  }, [placeModel, placePrefab, status?.path, status?.revision, say]);

  useEffect(() => {
    viewportRef.current?.setBrush({ radius: brushRadius, spacing: brushSpacing, density: brushDensity, maxSlope: brushSlope, erase: brushErase },
      placeModel?.name ?? null);
  }, [brushRadius, brushSpacing, brushDensity, brushSlope, brushErase, placeModel]);

  useEffect(() => {
    writeRenderSettings(renderSettings);
    viewportRef.current?.setRenderSettings(renderSettings);
    setViewportStats(null);
  }, [renderSettings]);

  useEffect(() => {
    writeEditorPreferences({ shading, lighting, grid, snap, snapMove, snapRotate, snapScale, gizmoLocal, ortho, backfaces,
      placeYaw, placeScale, randomYaw, randomScale, minPlaceScale, maxPlaceScale, alignSurface,
      snapTarget, pivotMode, placeAs, brushRadius, brushSpacing, brushDensity, brushSlope, brushErase,
      actionMarkers: actionMarkersOn, objectiveMarkers: objectiveMarkersOn, actorFigures: figuresOn,
      minimap: minimapOn, minimapRange });
  }, [shading, lighting, grid, snap, snapMove, snapRotate, snapScale, gizmoLocal, ortho, backfaces,
    placeYaw, placeScale, randomYaw, randomScale, minPlaceScale, maxPlaceScale, alignSurface,
    snapTarget, pivotMode, placeAs, brushRadius, brushSpacing, brushDensity, brushSlope, brushErase, actionMarkersOn, objectiveMarkersOn, figuresOn, minimapOn, minimapRange]);
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
    markers: viewportRef.current?.markerCount() ?? null,
    characters: viewportRef.current?.figureBounds() ?? null,
    selectedObjective,
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
      <PageHeader icon="globe" title="Map Builder" subtitle={ftb ? `${status?.game?.replace('SOCOM: ', '')} maps: move props, vehicles and points` : ca ? 'Combined Assault maps and missions' : battlefront ? 'Star Wars: Battlefront maps: move props, command posts and vehicle spawns' : gta ? 'GTA: San Andreas map areas: move and turn buildings and props' : 'Props, clutter and spawn points of SOCOM II maps'} />
      <MenuBar {...menuBarProps} />
      {shortcutsOpen && <ShortcutReference onClose={() => setShortcutsOpen(false)} />}
      {paletteOpen && <CommandPalette commands={commandsFromMenus(menus)} onClose={() => setPaletteOpen(false)} />}

      <div className="me-header">
        {!!status?.missions?.length && (
          <label className="me-toggle" title={gta ? `The area drawn (an IPL zone, with the nearby zones' low-detail blocks): the disc holds ${status.missions.length}` : battlefront ? `The map drawn: the disc holds ${status.missions.length}` : `The mission whose points are shown: ${status.location ?? 'this location'} holds ${status.missions.length}`}>{gta ? 'Area' : battlefront ? 'Map' : 'Mission'}
            <select aria-label="Mission" value={status.mission ?? ''} disabled={busy || loading} onChange={e => void pickMission(e.target.value)}>
              {status.missions.map(m => <option key={m.id} value={m.id}>{missionLabel(m)}</option>)}
            </select>
          </label>
        )}
        {gta && <label className="me-toggle" title="Show placements active at this hour">
          Time <select aria-label="Scene hour" value={mapSession.sceneHour} disabled={busy || loading}
            onChange={e => void mapSession.pickHour(Number(e.target.value))}>
            {Array.from({ length: 24 }, (_, h) => <option key={h} value={h}>{String(h).padStart(2, '0')}:00</option>)}
          </select>
        </label>}
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
        <label className="me-toggle" title="What a move drag snaps to: the increment above, the surface under each item (it slides over the ground), or the nearest vertex under the cursor">Snap to
          <select aria-label="Snap target" value={snapTarget} onChange={e => setSnapTarget(e.target.value as SnapTarget)}>
            <option value="increment">Increment</option><option value="surface">Surface</option><option value="vertex">Vertex</option>
          </select>
        </label>
        <label className="me-toggle" title="Where rotate and scale turn about: the selection's centre, each item's own origin, or the first selected item">Pivot
          <select aria-label="Pivot" value={pivotMode} onChange={e => setPivotMode(e.target.value as PivotMode)}>
            <option value="center">Centre</option><option value="individual">Each origin</option><option value="active">First selected</option>
          </select>
        </label>
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

      <div className="me-body">
        <aside className="me-assets">
          <AssetBrowser
            key={gta ? 'gta' : 'package'}
            models={gta ? models.filter(m => m.canPlace) : models}
            nativePlacements={gta}
            prefabs={prefabs}
            selectedModel={placePrefab ? null : placeModel}
            selectedPrefab={placePrefab?.name ?? null}
            onPickModel={pickModel}
            onPickPrefab={name => void pickPrefab(name)}
            thumbnail={thumbnail}
            thumbnailNow={key => thumbsRef.current?.cached(key)}
            canSavePrefab={movableCount > 0}
            onSavePrefab={name => void savePrefab(name)}
            onDeletePrefab={name => void deletePrefab(name)}
            onRefreshPrefabs={() => void refreshPrefabs()}
            onImport={() => void importFromPackage()}
            readOnly={!canPlace}
          />
        </aside>
        <nav className="me-tools">
          {TOOLS.filter(t => (!gta || t.id !== 'scale') && (t.id !== 'paint' || socomTools) && (t.id !== 'place' || canPlace)).map(t => (
            <button key={t.id} className={`me-tool${tool === t.id ? ' on' : ''}`} onClick={() => setTool(t.id)} title={`${t.label} (${t.key})`}>
              <span className="me-tool-glyph">{t.glyph}</span>
            </button>
          ))}
        </nav>

        <section className="me-center"
          onDragOver={e => { if (e.dataTransfer.types.includes(ASSET_DRAG_TYPE)) { e.preventDefault(); e.dataTransfer.dropEffect = 'copy'; } }}
          onDrop={e => void dropAsset(e)}>
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
          {minimapOn && status && <Minimap hud={hud} blips={blips} view={minimapView} topDown={topDown} range={minimapRange} onRange={setMinimapRange}
            onJump={(x, z) => viewportRef.current?.jumpTo(x, z)} onClose={() => setMinimapOn(false)} />}
        </section>

        <aside className="me-side">
          <Outliner
            query={query}
            setQuery={setQuery}
            layers={layers}
            shapeRows={shapeRows}
            pickShape={pickShape}
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
            skyInWorld={skyInWorld}
            pickGroup={pickGroup}
            pickInstance={pickInstance}
            pickPoint={pickPoint}
          />
          <PropsPanel
            actions={status && socomTools ? <MapActionsPanel key={status.session} data={actionData} error={actionError} onFocusNode={focusNode}
              revision={status.revision} client={client} selected={single?.geoIndex ?? actionNode} busy={busy || loading} edit={edit} path={status.path} model={request?.path === status.path ? request.actionsFor : undefined} /> :
              <div className="me-note">Open an editable SOCOM II map to manage its actions.</div>}
            mission={status && (!viewOnly || !!status.missions?.length) ? <MissionPanel tables={!ca || tables?.missionId === status.mission ? tables : null}
              ca={ca} pointNames={ca ? [...new Set(points.filter(p => p.editable !== false).map(p => p.name))] : undefined} readOnly={viewOnly} error={tablesError} revision={status.revision} busy={busy || loading} edit={edit}
              path={status.path} selectedObjective={selectedObjective} onSelectObjective={row => { setSelectedObjective(row); if (row !== null) { viewportRef.current?.frameObjective(row); } }}
              onShowObjective={showObjective} weaponModels={weaponModels} actorWeapons={actorWeapons} onChooseWeapon={chooseActorWeapon} figures={figurePlans}
              multiplayer={/^mp\d/i.test(status.mission ?? status.mapNamespace ?? '')} /> :
              <div className="me-note">Open an editable SOCOM II map to see its mission tables.</div>}
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
            onRenamePoint={name => void renamePoint(name)}
            onSetField={setField}
            onEditSelection={editSelection}
            onDuplicate={() => void duplicateSelection()}
            onDelete={() => void deleteSelection()}
            onDrop={() => void dropToGround()}
            onOpenModel={() => openModel(selection[0])}
            onArrange={(axis, how) => void arrange(axis, how)}
            placeModel={placeModel}
            placePrefab={placePrefab?.name ?? null}
            placeAs={placeAs}
            setPlaceAs={setPlaceAs}
            tool={tool}
            brush={{ radius: brushRadius, spacing: brushSpacing, density: brushDensity, slope: brushSlope, erase: brushErase }}
            setBrush={b => { setBrushRadius(b.radius); setBrushSpacing(b.spacing); setBrushDensity(b.density); setBrushSlope(b.slope); setBrushErase(b.erase); }}
            onPaintTool={() => setTool('paint')}
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
            check={check}
            checking={checking}
            onCheck={() => void runCheck()}
            onPickProblem={pickProblem}
            history={<HistoryList load={() => client.history()} revision={status?.revision} busy={busy} onJump={(u, r) => void jump(u, r)} />}
          />
        </aside>
      </div>

      <div className={`me-statusbar${messageIsError ? ' err' : ''}`}>
        <span>{message}</span>
        <span className="me-spacer" />
        {walking && <span className="mb-walk-badge">Walking · Esc to stop</span>}
        {hiddenCount > 0 && <button type="button" className="mb-hidden-badge" onClick={unhideAll} title="Show all hidden (Alt+H)">{hiddenCount} hidden</button>}
        {measure && <span className="mb-measure">{measure}</span>}
        {dragInfo ? <span>{dragInfo}</span> : hover && <span className="lm-muted">{hover}</span>}
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
