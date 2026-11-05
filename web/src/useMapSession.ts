// The Map Builder's backend session: open, reload, poll for outside edits, edit, undo / redo and save. busyRef and
// statusRef mirror the state for callbacks that outlive a render; savedRevisionRef marks the last open or save.

import { useCallback, useEffect, useRef, useState, type MutableRefObject } from 'react';
import { AssetClient, type MapInstanceWire, type MapPathWire, type MapPointWire, type MapRegionWire, type ModelSummary, type SessionStatus } from './webview/model-editor/asset-client';
import { TextureCache } from './webview/model-editor/model-loader';
import { loadMapModels } from './webview/zipper-core/map-models';
import type { ModelTexturePixels } from './webview/zipper-core/texture-pixels';
import type { MapModel, MapViewport } from './viewport/mapViewport';
import { errText, isCaFamily } from './mapModel';
import { host, openRequest, useSignals } from './shared';
import { aiChange, samePath, type AiFocus } from './shared/aiBridge';

// CA and Battlefront edits change placements and points only, so an undo keeps the decoded meshes.
const placementsOnly = (game?: string) => isCaFamily(game) || game === 'Star Wars: Battlefront' || game === 'GTA: San Andreas';
// A session the Map Builder cannot edit.
export const mapViewOnly = (s: SessionStatus | null | undefined) => !!s?.readOnly;

// Callbacks are read through refs, so the page can mirror selection without coupling session logic to its UI.
export function useMapSession(viewportRef: MutableRefObject<MapViewport | null>,
  onLoaded: (vp: MapViewport, keepCamera: boolean) => void, onOpened: () => void = () => {}) {
  const onLoadedRef = useRef(onLoaded);
  const onOpenedRef = useRef(onOpened);
  onLoadedRef.current = onLoaded;
  onOpenedRef.current = onOpened;
  const clientRef = useRef(new AssetClient());
  const texturesRef = useRef(new TextureCache(clientRef.current));
  const meshCacheRef = useRef(new Map<string, MapModel>());
  // Models the asset browser decoded that the scene does not draw (see loadModel).
  const browseCacheRef = useRef(new Map<string, MapModel>());
  const pixelCacheRef = useRef<Record<string, ModelTexturePixels>>({});
  const instancesRef = useRef<MapInstanceWire[]>([]);
  const pointsRef = useRef<MapPointWire[]>([]);
  const pointsMissionRef = useRef<string | null>(null);
  const statusRef = useRef<SessionStatus | null>(null);
  const busyRef = useRef(false);
  const operationRevisionRef = useRef(0);
  const reloadingRef = useRef(0);
  const pollingRef = useRef(false);
  const sceneReadyRef = useRef(false);

  const [status, setStatusState] = useState<SessionStatus | null>(null);
  const [sceneNs, setSceneNs] = useState('');
  const hourRef = useRef(12);
  const [sceneHour, setSceneHour] = useState(12);
  const [counts, setCounts] = useState<Record<string, number>>({});
  const [instances, setInstances] = useState<MapInstanceWire[]>([]);
  const [points, setPoints] = useState<MapPointWire[]>([]);
  // Battlefront paths and regions (view only).
  const [shapes, setShapes] = useState<{ paths: MapPathWire[]; regions: MapRegionWire[] }>({ paths: [], regions: [] });
  const [models, setModels] = useState<ModelSummary[]>([]);
  const [busy, setBusy] = useState(false);
  const [progress, setProgress] = useState('');
  const [loading, setLoading] = useState(false);
  const [sceneReady, setSceneReady] = useState(false);
  const [loadWarnings, setLoadWarnings] = useState<string[]>([]);
  const [connectionIssue, setConnectionIssue] = useState<string | null>(null);
  const [message, setMessage] = useState('');
  const [messageIsError, setMessageIsError] = useState(false);

  const setStatus = useCallback((s: SessionStatus | null) => { statusRef.current = s; setStatusState(s); }, []);
  const setBusyFlag = useCallback((on: boolean) => {
    if (on) { operationRevisionRef.current++; }
    busyRef.current = on;
    setBusy(on);
  }, []);

  // The revision last opened or saved: status.dirty compares with the source file, so it stays set after a Save copy.
  const savedRevisionRef = useRef<number | null>(null);
  const unsaved = () => {
    const s = statusRef.current;
    return !!s && s.dirty.length > 0 && s.revision !== savedRevisionRef.current;
  };

  useSignals();
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
      browseCacheRef.current.clear();
      pixelCacheRef.current = {};
      const s = statusRef.current;
      if (s) { texturesRef.current.invalidate(s.revision); }
    }
    setProgress('Reading map scene…');
    const warnings: string[] = [];
    const [scene, pts] = await Promise.all([
      clientRef.current.mapScene(statusRef.current?.game === 'GTA: San Andreas' ? { hour: hourRef.current } : {}),
      clientRef.current.mapPoints().catch(error => {
        warnings.push(`AIMAPS points could not be loaded: ${errText(error)}`);
        return { points: [] as MapPointWire[], mission: null, teamSpawns: [] };
      }),
    ]);
    setSceneNs(scene.sceneNamespace);
    setCounts(scene.counts);
    const sources = scene.instances.map(i => i.source).filter((s): s is string => !!s);
    const failed = await loadMapModels(clientRef.current, texturesRef.current, sources, meshCacheRef.current, pixelCacheRef.current,
      (done, total) => setProgress(`Loading models ${done}/${total}…`), scene.meshBatchSize);
    setProgress('Preparing viewport…');
    instancesRef.current = scene.instances;
    const shownPoints = pts.mission ? [
      ...pts.points.map(p => ({ ...p, canMove: !!p.island })),
      ...(pts.teamSpawns ?? []).map(p => ({ ...p, canMove: false, editable: false })),
    ] : pts.points;
    pointsRef.current = shownPoints;
    pointsMissionRef.current = pts.mission ?? null;
    setInstances(scene.instances);
    setPoints(shownPoints);
    vp.setScene(scene.instances, meshCacheRef.current, pixelCacheRef.current, shownPoints, keepCamera);
    const nextShapes = { paths: scene.paths ?? [], regions: scene.regions ?? [] };
    vp.setShapes(nextShapes.paths, nextShapes.regions);
    setShapes(nextShapes);
    onLoadedRef.current(vp, keepCamera);
    if (!keepCamera) {
      const kinds = Object.entries(scene.counts).map(([kind, count]) => `${count} ${kind}`).join(', ');
      say(`${scene.sceneNamespace}: ${scene.instances.length} instances (${kinds}), ${shownPoints.length} points.`);
    }
    warnings.push(...failed.map(failure => `Model not drawn: ${failure}`));
    setLoadWarnings(warnings);
    // Keep details visible until a successful retry, rather than only in the status bar.
    if (failed.length) {
      console.warn('Map Builder: models that did not load', failed);
      say(`${failed.length} model(s) did not load and are not drawn: ${failed.slice(0, 3).join('; ')}${failed.length > 3 ? '; …' : ''}`, true);
    }
  }, [say, viewportRef]);

  // Polls skip while a load runs: a map with many models takes longer than one poll interval.
  const reload = useCallback(async (keepCamera: boolean, modelsChanged: boolean) => {
    reloadingRef.current++;
    sceneReadyRef.current = false;
    setSceneReady(false);
    setLoading(true);
    setLoadWarnings([]);
    if (viewportRef.current) { viewportRef.current.editable = false; }
    try {
      await loadScene(keepCamera, modelsChanged);
      sceneReadyRef.current = true;
      setSceneReady(true);
    } catch (error) {
      setLoadWarnings([`Scene could not be loaded: ${errText(error)}`]);
      throw error;
    } finally {
      reloadingRef.current--;
      if (reloadingRef.current === 0) {
        setProgress('');
        setLoading(false);
        if (viewportRef.current) { viewportRef.current.editable = sceneReadyRef.current && !busyRef.current && !mapViewOnly(statusRef.current); }
      }
    }
  }, [loadScene]);

  const openPackage = useCallback(async (path: string) => {
    if (busyRef.current || reloadingRef.current > 0) { say('Wait for the current operation to finish before opening another package.', true); return; }
    const current = statusRef.current;
    if (current && current.path !== path && unsaved() && !window.confirm(`${current.path} has unsaved changes (${current.dirty.join(', ')}). They stay in the session until the server stops; open ${path} anyway?`)) { return; }
    setBusyFlag(true);
    say(`Opening ${path}…`);
    let opened = false;
    try {
      const s = await clientRef.current.open(path);
      opened = true;
      sceneReadyRef.current = false;
      setSceneReady(false);
      if (viewportRef.current) { viewportRef.current.editable = false; }
      setStatus(s);
      hourRef.current = 12;
      setSceneHour(12);
      setConnectionIssue(null);
      // Once the client switches sessions, old instance indices must never address the new package.
      instancesRef.current = [];
      pointsRef.current = [];
      setInstances([]);
      setPoints([]);
      setModels([]);
      setCounts({});
      setSceneNs('');
      viewportRef.current?.setPlacementMesh(null);
      viewportRef.current?.clearSelection();
      viewportRef.current?.setScene([], new Map(), {}, [], true);
      viewportRef.current?.setShapes([], []);
      setShapes({ paths: [], regions: [] });
      onOpenedRef.current();
      setLoadWarnings([]);
      savedRevisionRef.current = s.revision;
      texturesRef.current.invalidate(s.revision);
      meshCacheRef.current.clear();
      browseCacheRef.current.clear();
      pixelCacheRef.current = {};
      setModels(await clientRef.current.listModels());
      await reload(false, false);
    } catch (err) {
      if (opened) { setLoadWarnings([`Package opened, but its map could not be loaded: ${errText(err)}`]); }
      say(`Could not open ${path}: ${errText(err)}`, true);
    } finally {
      setBusyFlag(false);
    }
  }, [reload, say, setBusyFlag, setStatus]);

  // Another page asked for a package here (the Asset Viewer's "Open map in Map Builder").
  const request = openRequest();
  useEffect(() => {
    if (request?.page === 'map') { void openPackage(request.path); }
    // openPackage is stable for the page's life; only a new request should open.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [request]);

  // The AI (MCP) edited or asked to show something: open its package here if it brought this page forward, reload
  // when the session changed, then select and frame what it touched.
  const focus = useCallback((f: AiFocus | null | undefined) => {
    const vp = viewportRef.current;
    if (!vp || !f) { return; }
    if (f.point) { vp.selectPoint(f.point); vp.frameSelected(); return; }
    const i = instancesRef.current.findIndex(inst =>
      (f.instance != null && inst.instance === f.instance) ||
      (f.clutterIndex != null && inst.clutterIndex === f.clutterIndex) ||
      (f.geoIndex != null && inst.clutterIndex === null && inst.geoIndex === f.geoIndex));
    if (i >= 0) { vp.select([i]); vp.frameSelected(); }
  }, [viewportRef]);

  const handledChangeRef = useRef<number | null>(null);
  const change = aiChange();
  useEffect(() => {
    if (!change || change.seq === handledChangeRef.current || busyRef.current || reloadingRef.current > 0) { return; }
    handledChangeRef.current = change.seq;
    const here = samePath(statusRef.current?.path, change.path);
    const run = async () => {
      if (change.page === 'map' && change.path && !here) {
        await openPackage(change.path);
      } else if (here && change.edited) {
        setBusyFlag(true);
        try {
          setStatus(await clientRef.current.status());
          await reload(true, true);
        } finally { setBusyFlag(false); }
      } else if (!here) {
        return;
      }
      if (change.page === 'map' && samePath(statusRef.current?.path, change.path) && sceneReadyRef.current) { focus(change.focus); }
      if (change.edited && change.description) { say(`AI: ${change.description}`); }
    };
    void run().catch(err => say(`AI change: ${errText(err)}`, true));
    // A change received during an edit/load is applied once that operation finishes.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [change, busy, loading]);

  // Edits made by anyone on the session (an assistant on the same backend) show up here.
  useEffect(() => {
    const timer = setInterval(async () => {
      if (!clientRef.current.session || busyRef.current || reloadingRef.current > 0 || pollingRef.current) { return; }
      pollingRef.current = true;
      const session = clientRef.current.session;
      const operationRevision = operationRevisionRef.current;
      let receivedStatus = false;
      try {
        const next = await clientRef.current.status();
        receivedStatus = true;
        if (busyRef.current || reloadingRef.current > 0 || clientRef.current.session !== session || operationRevisionRef.current !== operationRevision) { return; }
        setConnectionIssue(null);
        const before = statusRef.current;
        setStatus(next);
        if (before && next.revision !== before.revision) {
          await reload(true, true);
          say(`Session changed (revision ${next.revision}${next.canUndo ? `: ${next.canUndo}` : ''}).`);
        } else if (before && next.mission !== before.mission) {
          viewportRef.current?.clearSelection();
          await reload(true, false);
        }
      } catch (error) {
        if (!receivedStatus && clientRef.current.session === session && operationRevisionRef.current === operationRevision) {
          setConnectionIssue(`Session refresh failed: ${errText(error)}. Retrying automatically.`);
        }
      } finally { pollingRef.current = false; }
    }, 2000);
    return () => clearInterval(timer);
  }, [reload, say, setStatus]);

  // The gizmo and Place tool are off while an edit runs or the session is read-only.
  useEffect(() => {
    if (viewportRef.current) { viewportRef.current.editable = sceneReady && !busy && !loading && !mapViewOnly(status); }
  }, [busy, loading, sceneReady, status]);

  // Closing the tab with edits made since the last save asks first.
  useEffect(() => {
    const onUnload = (e: BeforeUnloadEvent) => { if (unsaved()) { e.preventDefault(); e.returnValue = ''; } };
    window.addEventListener('beforeunload', onUnload);
    return () => window.removeEventListener('beforeunload', onUnload);
  }, []);

  const retryScene = async () => {
    if (!clientRef.current.session || busyRef.current || reloadingRef.current > 0) { return; }
    setBusyFlag(true);
    try {
      setModels(await clientRef.current.listModels());
      await reload(!!sceneNs, true);
      say('Map reload completed.');
    } catch (error) {
      setLoadWarnings([`Map reload failed: ${errText(error)}`]);
      say(`Could not reload map: ${errText(error)}`, true);
    } finally { setBusyFlag(false); }
  };

  const pickHour = async (hour: number) => {
    if (busyRef.current || reloadingRef.current || statusRef.current?.game !== 'GTA: San Andreas') { return; }
    hourRef.current = hour;
    setSceneHour(hour);
    setBusyFlag(true);
    try { await reload(true, false); }
    catch (error) { say(`Could not load the scene at ${hour}:00: ${errText(error)}`, true); }
    finally { setBusyFlag(false); }
  };

  // ------------------------------------------------------------------ edits

  const edit = useCallback(async (label: string, op: Record<string, unknown>): Promise<Record<string, unknown> | null> => {
    // One edit at a time, and none on a read-only session.
    if (!sceneReadyRef.current) { say('Reload the map before editing; its scene is not ready.', true); return null; }
    if (busyRef.current || reloadingRef.current > 0 || mapViewOnly(statusRef.current)) {
      say(mapViewOnly(statusRef.current) ? `${label}: this package is read-only.` : `${label}: wait for the previous change to finish.`, true);
      return null;
    }
    setBusyFlag(true);
    try {
      const missionOp = op.op === 'movePoint' || op.op === 'setPoint' || op.op === 'mapTable';
      const context = statusRef.current?.missions
        ? { mission: missionOp ? pointsMissionRef.current : undefined, revision: statusRef.current.revision } : {};
      const raw = await clientRef.current.edit({ label, ...context, ...op }) as unknown as Record<string, unknown>;
      const result = (raw['result'] ?? raw) as { revision: number; description?: string; warnings?: string[] };
      setStatus(await clientRef.current.status());
      await reload(true, false);
      const warnings = result.warnings ?? (Array.isArray(raw.warnings) ? raw.warnings : []);
      say([`${result.description ?? label} (revision ${result.revision})`, ...warnings].join(' '));
      return raw;
    } catch (err) {
      say(`${label}: ${errText(err)}`, true);
      await reload(true, false).catch(() => undefined);
      return null;
    } finally {
      setBusyFlag(false);
    }
  }, [reload, say, setBusyFlag, setStatus]);

  const history = async (undo: boolean) => {
    if (!clientRef.current.session || busyRef.current || reloadingRef.current > 0 || mapViewOnly(statusRef.current)) { return; }
    setBusyFlag(true);
    try {
      const r = undo ? await clientRef.current.undo() : await clientRef.current.redo();
      setStatus(await clientRef.current.status());
      say(`${undo ? 'Undid' : 'Redid'} ${r.description ?? ''} (revision ${r.revision})`);
      // CA edits change placements and mission records. SOCOM II history can also change meshes.
      await reload(true, !placementsOnly(statusRef.current?.game));
    } catch (err) {
      say(errText(err), true);
    } finally {
      setBusyFlag(false);
    }
  };

  // Back or forward several steps at once (the History list): that many undos or redos, then one reload.
  const jump = async (undoSteps: number, redoSteps: number) => {
    if (!clientRef.current.session || busyRef.current || reloadingRef.current > 0 || mapViewOnly(statusRef.current)) { return; }
    setBusyFlag(true);
    try {
      let last: { description?: string; revision: number } | null = null;
      for (let i = 0; i < undoSteps; i++) { last = await clientRef.current.undo(); }
      for (let i = 0; i < redoSteps; i++) { last = await clientRef.current.redo(); }
      setStatus(await clientRef.current.status());
      if (last) { say(`${undoSteps ? `Undid ${undoSteps}` : `Redid ${redoSteps}`} step(s), to revision ${last.revision}`); }
      await reload(true, !placementsOnly(statusRef.current?.game));
    } catch (err) {
      say(errText(err), true);
    } finally {
      setBusyFlag(false);
    }
  };

  // ------------------------------------------------------------------ save

  const save = async (args: { mode: 'zdb' | 'pack'; outputPath?: string; packName?: string; sourceIso?: string }) => {
    if (!clientRef.current.session || busyRef.current || reloadingRef.current > 0) { return; }
    setBusyFlag(true);
    try {
      const r = await clientRef.current.save(args);
      savedRevisionRef.current = r.revision ?? statusRef.current?.revision ?? null;
      const patched = r.patched ? `: ${r.patched.map(p => p.discPath + (p.moved ? ' (moved to the end)' : '')).join(', ')}` : '';
      say(`Wrote ${r.written.join(', ')} (${r.leaves.join(', ')})${patched}${r.warnings?.length ? '. ' + r.warnings.join(' ') : ''}`);
    } catch (err) {
      say(`Save failed: ${errText(err)}`, true);
    } finally {
      setBusyFlag(false);
    }
  };

  const saveCopy = async () => {
    const s = statusRef.current;
    if (!s || !s.dirty.length) { return; }
    const path = await host.pickPackageCopy(s);
    if (path) { await save({ mode: 'zdb', outputPath: path }); }
  };

  // The edited files written into a copy of the disc image (any game).
  const saveDisc = async () => {
    const s = statusRef.current;
    if (!s || !s.dirty.length) { return; }
    const picked = await host.pickDiscCopy(s);
    if (picked) { await save({ mode: 'zdb', ...picked }); }
  };

  const exportPack = async () => {
    const picked = await host.pickSavePath('pack', 'MMAP');
    if (picked.packName && picked.path) { void save({ mode: 'pack', packName: picked.packName, outputPath: picked.path }); }
  };

  const openDialog = async () => {
    const path = await host.pickPackage();
    if (path) { void openPackage(path); }
  };

  // A model for the asset browser's thumbnails and placement previews: the scene's own decoded mesh when the map
  // draws it, else decoded into browseCacheRef (kept out of the scene's cache, which decides whether a reload rebuilds).
  const loadModel = useCallback(async (source: string): Promise<MapModel | null> => {
    const scene = meshCacheRef.current.get(source);
    if (scene) { return scene; }
    await loadMapModels(clientRef.current, texturesRef.current, [source], browseCacheRef.current, pixelCacheRef.current, () => undefined);
    return browseCacheRef.current.get(source) ?? null;
  }, []);

  return {
    client: clientRef.current, instancesRef, pointsRef, statusRef,
    status, setStatus, sceneNs, counts, instances, points, shapes, models, setModels, busy, progress, loading, sceneReady,
    loadWarnings, connectionIssue, message, messageIsError, say, sceneHour, pickHour,
    reload, retryScene, edit, history, saveCopy, saveDisc, exportPack, openDialog,
    loadModel, texturePixels: pixelCacheRef.current, jump,
  };
}
