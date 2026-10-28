// The Map Builder's backend session: open, reload, poll for outside edits, edit, undo / redo and save. busyRef and
// statusRef mirror the state for callbacks that outlive a render; savedRevisionRef marks the last open or save.

import { useCallback, useEffect, useRef, useState, type MutableRefObject } from 'react';
import { AssetClient, type MapInstanceWire, type MapPointWire, type ModelSummary, type SessionStatus } from './webview/model-editor/asset-client';
import { TextureCache } from './webview/model-editor/model-loader';
import { decodeMesh, type MeshWire } from './webview/model-editor/mesh-data';
import type { ModelTexturePixels } from './webview/zipper-workbench/zed-model-renderer';
import type { MapModel, MapViewport } from './viewport/mapViewport';
import { errText } from './mapModel';
import { host, openRequest, useSignals } from './shared';

// onLoaded runs after each scene load (camera kept or not); it is read through a ref, so it may change every render.
export function useMapSession(viewportRef: MutableRefObject<MapViewport | null>, onLoaded: (vp: MapViewport, keepCamera: boolean) => void) {
  const clientRef = useRef(new AssetClient());
  const texturesRef = useRef(new TextureCache(clientRef.current));
  const meshCacheRef = useRef(new Map<string, MapModel>());
  const pixelCacheRef = useRef<Record<string, ModelTexturePixels>>({});
  const instancesRef = useRef<MapInstanceWire[]>([]);
  const pointsRef = useRef<MapPointWire[]>([]);
  const statusRef = useRef<SessionStatus | null>(null);
  const busyRef = useRef(false);
  const reloadingRef = useRef(0);
  const onLoadedRef = useRef(onLoaded);
  onLoadedRef.current = onLoaded;

  const [status, setStatusState] = useState<SessionStatus | null>(null);
  const [sceneNs, setSceneNs] = useState('');
  const [counts, setCounts] = useState<Record<string, number>>({});
  const [instances, setInstances] = useState<MapInstanceWire[]>([]);
  const [points, setPoints] = useState<MapPointWire[]>([]);
  const [models, setModels] = useState<ModelSummary[]>([]);
  const [busy, setBusy] = useState(false);
  const [progress, setProgress] = useState('');
  const [message, setMessage] = useState('');
  const [messageIsError, setMessageIsError] = useState(false);

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
    const failed: string[] = [];
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
        failed.push(`${source} (${errText(err)})`);
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
    onLoadedRef.current(vp, keepCamera);
    if (!keepCamera) {
      const kinds = Object.entries(scene.counts).map(([kind, count]) => `${count} ${kind}`).join(', ');
      say(`${scene.sceneNamespace}: ${scene.instances.length} instances (${kinds}), ${pts.points.length} points.`);
    }
    // All failures at once (they used to overwrite each other); the console has the full list.
    if (failed.length) {
      console.warn('Map Builder: models that did not load', failed);
      say(`${failed.length} model(s) did not load and are not drawn: ${failed.slice(0, 3).join('; ')}${failed.length > 3 ? '; …' : ''}`, true);
    }
  }, [say, viewportRef]);

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
    // unsaved reads refs only.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [reload, say, setBusyFlag, setStatus, viewportRef]);

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
  }, [busy, status, viewportRef]);

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

  const openDialog = async () => {
    const path = await host.pickPackage();
    if (path) { void openPackage(path); }
  };

  return {
    client: clientRef.current, instancesRef, pointsRef, statusRef,
    status, setStatus, sceneNs, counts, instances, points, models, setModels, busy, progress, message, messageIsError, say,
    reload, edit, history, saveCopy, exportPack, openDialog,
  };
}
