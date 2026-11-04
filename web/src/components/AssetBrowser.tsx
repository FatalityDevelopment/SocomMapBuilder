// The Map Builder's asset browser (left panel): the package's placeable models and the saved prefabs as thumbnail
// tiles. Click one to place it with the Place tool (P) or paint it with the brush (B); drag one onto the map to drop
// it there. Prefabs save from the selection and place as one edit.

import { useEffect, useRef, useState } from 'react';
import type { ModelSummary, PrefabSummary } from '../webview/model-editor/asset-client';

/** What the browser hands the viewport on a drag: a model by archive and name, or a prefab by name. */
export type AssetDrag = { kind: 'model'; archive: string; name: string } | { kind: 'prefab'; name: string };
export const ASSET_DRAG_TYPE = 'application/x-mapbuilder-asset';

type Shelf = 'models' | 'prefabs';

interface AssetBrowserProps {
  models: ModelSummary[];
  prefabs: PrefabSummary[];
  selectedModel: ModelSummary | null;
  selectedPrefab: string | null;
  onPickModel: (m: ModelSummary) => void;
  onPickPrefab: (name: string) => void;
  // A thumbnail data URL for a model ("<archive>:<name>") or a prefab ("prefab:<name>"); null when there is nothing to draw.
  thumbnail: (key: string) => Promise<string | null>;
  thumbnailNow: (key: string) => string | undefined;
  canSavePrefab: boolean;
  onSavePrefab: (name: string) => void;
  onDeletePrefab: (name: string) => void;
  onRefreshPrefabs: () => void;
  onImport: () => void;
  readOnly: boolean;
}

export function AssetBrowser(p: AssetBrowserProps) {
  const [shelf, setShelf] = useState<Shelf>('models');
  const [query, setQuery] = useState('');
  const [kinds, setKinds] = useState<Set<string>>(() => new Set(['prop', 'library']));
  const [naming, setNaming] = useState<string | null>(null);
  const q = query.toLowerCase();
  const models = p.models.filter(m => kinds.has(m.kind) && (!q || m.name.toLowerCase().includes(q))).slice(0, 600);
  const prefabs = p.prefabs.filter(f => !q || f.name.toLowerCase().includes(q) || (f.models ?? []).some(m => m.toLowerCase().includes(q)));
  const toggleKind = (k: string) => setKinds(prev => { const next = new Set(prev); if (next.has(k)) { next.delete(k); } else { next.add(k); } return next; });

  return (
    <div className="mb-assets">
      <div className="me-panel-title">
        <span>Assets</span>
        <button className="lm-tool-btn mb-assets-import" disabled={p.readOnly} onClick={p.onImport} title="Copy a model from another package into this one">Import…</button>
      </div>
      <div className="me-kinds">
        <button className={`me-chip${shelf === 'models' ? ' active' : ''}`} onClick={() => setShelf('models')}>Models</button>
        <button className={`me-chip${shelf === 'prefabs' ? ' active' : ''}`} onClick={() => { setShelf('prefabs'); p.onRefreshPrefabs(); }}>Prefabs ({p.prefabs.length})</button>
      </div>
      <input className="me-search" type="text" placeholder={shelf === 'models' ? 'Filter models…' : 'Filter prefabs…'} value={query} onChange={e => setQuery(e.target.value)} />
      {shelf === 'models' && (
        <div className="me-kinds">
          {['prop', 'library'].map(k => (
            <button key={k} className={`me-chip${kinds.has(k) ? ' active' : ''}`} onClick={() => toggleKind(k)}>{k === 'prop' ? 'Props' : 'Library'}</button>
          ))}
        </div>
      )}
      {shelf === 'prefabs' && (
        <div className="mb-prefab-save">
          {naming === null
            ? <button className="lm-tool-btn" disabled={!p.canSavePrefab || p.readOnly} onClick={() => setNaming('')}
              title="Save the selected props and clutter as a prefab, placed again as one edit">Save selection as prefab…</button>
            : <form onSubmit={e => { e.preventDefault(); if (naming.trim()) { p.onSavePrefab(naming.trim()); setNaming(null); } }}>
              <input autoFocus className="me-search" placeholder="Prefab name" value={naming} onChange={e => setNaming(e.target.value)}
                onKeyDown={e => { if (e.key === 'Escape') { setNaming(null); } }} />
              <button className="lm-tool-btn" type="submit" disabled={!naming.trim()}>Save</button>
              <button className="lm-tool-btn" type="button" onClick={() => setNaming(null)}>Cancel</button>
            </form>}
        </div>
      )}
      <div className="mb-tiles">
        {shelf === 'models' && models.map(m => (
          <Tile key={m.archive + ':' + m.name} thumbKey={`${m.archive}:${m.name}`} label={m.name} meta={m.archive}
            active={p.selectedModel?.archive === m.archive && p.selectedModel?.name === m.name}
            drag={{ kind: 'model', archive: m.archive, name: m.name }} onPick={() => p.onPickModel(m)} {...p} />
        ))}
        {shelf === 'prefabs' && prefabs.map(f => (
          <Tile key={f.name} thumbKey={`prefab:${f.name}`} label={f.name}
            meta={f.error ? f.error : `${f.items ?? 0} items: ${(f.models ?? []).join(', ')}`}
            active={p.selectedPrefab === f.name} drag={{ kind: 'prefab', name: f.name }} onPick={() => p.onPickPrefab(f.name)}
            onDelete={() => { if (window.confirm(`Delete the prefab ${f.name}? Maps it was placed in keep their copies.`)) { p.onDeletePrefab(f.name); } }} {...p} />
        ))}
        {shelf === 'models' && !models.length && <div className="lm-muted me-note">No models match.</div>}
        {shelf === 'prefabs' && !prefabs.length && <div className="lm-muted me-note">No prefabs yet: select props and clutter on the map, then Save selection as prefab.</div>}
      </div>
      <div className="lm-muted me-note mb-assets-hint">Click to place (P) or paint (B); drag onto the map to drop it there.</div>
    </div>
  );
}

function Tile(t: { thumbKey: string; label: string; meta: string; active: boolean; drag: AssetDrag; onPick: () => void; onDelete?: () => void }
  & Pick<AssetBrowserProps, 'thumbnail' | 'thumbnailNow'>) {
  const ref = useRef<HTMLButtonElement | null>(null);
  const [url, setUrl] = useState<string | null | undefined>(() => t.thumbnailNow(t.thumbKey));
  // Drawn once the tile scrolls into view.
  useEffect(() => {
    if (url !== undefined || !ref.current) { return; }
    let live = true;
    const io = new IntersectionObserver(entries => {
      if (!entries.some(e => e.isIntersecting)) { return; }
      io.disconnect();
      void t.thumbnail(t.thumbKey).then(u => { if (live) { setUrl(u); } });
    });
    io.observe(ref.current);
    return () => { live = false; io.disconnect(); };
  }, [t.thumbKey, url]);
  return (
    <button ref={ref} type="button" className={`mb-tile${t.active ? ' active' : ''}`} title={`${t.label}\n${t.meta}`} draggable
      onClick={t.onPick}
      onDragStart={e => { e.dataTransfer.setData(ASSET_DRAG_TYPE, JSON.stringify(t.drag)); e.dataTransfer.effectAllowed = 'copy'; }}>
      <span className="mb-tile-thumb">{url ? <img src={url} alt="" draggable={false} /> : <span className="lm-muted">{url === null ? '—' : '…'}</span>}</span>
      <span className="mb-tile-name">{t.label}</span>
      {t.onDelete && <span className="mb-tile-del" role="button" title="Delete this prefab" onClick={e => { e.stopPropagation(); t.onDelete!(); }}>×</span>}
    </button>
  );
}
