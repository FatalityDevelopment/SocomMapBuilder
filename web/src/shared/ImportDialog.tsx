import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { command } from '../api/client';
import { Icon } from './Icon';
import { activeImport, host, useSignals, type ImportRequest } from './index';

interface ModelRow { archive: string; name: string; kind: string; namespace: string; size: number }
interface TextureRow { name: string; namespace: string; archive: string; width: number; height: number; bpp: number; paletteId: number | null }

// One row of either list: a model (importModel) or a texture (importTexture).
interface Row { key: string; name: string; kind: string; archive: string; namespace: string }

const MODEL_KINDS = ['prop', 'library', 'character', 'weapon', 'sky'];

// Retail models name a texture in a 16-byte field: a longer name could not be put on their sections.
const shortName = (tif: string) => tif.length <= 15 ? '' : tif.replace(/\.tif$/i, '').slice(0, 11) + '.tif';

const errText = (err: unknown) => (err instanceof Error ? err.message : String(err)).replace(/^ASSET_[A-Z_]+:\s*/, '');

// Import from package: pick another package, one of its models (or textures), and where it goes in the open one. The
// server copies a model with its GEO definition, textures and palettes (importModel), a texture with its mip levels
// and palette (importTexture).
export function ImportDialog() {
  useSignals();
  const request = activeImport();
  return request ? <ImportForm key={request.targetSession + request.mode} request={request} /> : null;
}

function ImportForm({ request }: { request: ImportRequest }) {
  const textures = request.mode === 'texture';
  const [sourcePath, setSourcePath] = useState<string | null>(null);
  const [sourceSession, setSourceSession] = useState<string | null>(null);
  const [rows, setRows] = useState<Row[]>([]);
  const [targets, setTargets] = useState<string[]>([]);
  const [query, setQuery] = useState('');
  const [kind, setKind] = useState(request.kind ?? '');
  const [selected, setSelected] = useState<Row | null>(null);
  const [target, setTarget] = useState('');
  const [newName, setNewName] = useState('');
  const [preview, setPreview] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const cancel = useCallback(() => request.resolve(null), [request]);
  const started = useRef(false);

  const choosePackage = useCallback(async () => {
    const path = await host.pickPackage(textures ? 'Import texture from package' : 'Import from package');
    if (!path) { return false; }
    setBusy(true);
    setError(null);
    try {
      const open = await command<{ status: { session: string } }>('ASSET_OPEN', { InputPath: path });
      const session = open.status.session;
      if (session === request.targetSession) { throw new Error('That is the package being edited; pick another one.'); }
      if (textures) {
        const list = await command<TextureRow[]>('ASSET_LIST_TEXTURES', {}, session);
        setRows(list.map(t => ({
          key: t.namespace + ':' + t.name, name: t.name, archive: t.archive, namespace: t.namespace,
          kind: `${t.width}×${t.height} · ${t.bpp === 8 ? '8-bit' : t.bpp + '-bit'}`,
        })));
      } else {
        const list = await command<ModelRow[]>('ASSET_LIST_MODELS', { AssetJson: '{}' }, session);
        setRows(list.filter(m => m.kind !== 'world').map(m => ({ key: m.archive + ':' + m.name, name: m.name, kind: m.kind, archive: m.archive, namespace: m.namespace })));
      }
      setSourcePath(path);
      setSourceSession(session);
      setSelected(null);
      setPreview(null);
      setNewName('');
    } catch (err) {
      setError(errText(err));
    } finally {
      setBusy(false);
    }
    return true;
  }, [request.targetSession, textures]);

  // The target's archives (the "Into" list), then the source package right away.
  useEffect(() => {
    if (started.current) { return; }
    started.current = true;
    const into = textures
      ? command<TextureRow[]>('ASSET_LIST_TEXTURES', {}, request.targetSession).then(list => [...new Set(list.map(t => t.namespace))])
      : command<ModelRow[]>('ASSET_LIST_MODELS', { AssetJson: '{}' }, request.targetSession).then(list => [...new Set(list.filter(m => m.kind !== 'world').map(m => m.archive))]);
    void into.then(list => setTargets(list.sort())).catch(() => setTargets([]));
    void choosePackage().then(picked => { if (!picked) { cancel(); } });
  }, [request.targetSession, textures, choosePackage, cancel]);

  // A texture's picture, from the source package.
  useEffect(() => {
    if (!textures || !selected || !sourceSession) { setPreview(null); return; }
    let live = true;
    void command<{ png: string }>('ASSET_TEXTURE', { AssetName: selected.name, AssetArchive: selected.namespace }, sourceSession)
      .then(t => { if (live) { setPreview('data:image/png;base64,' + t.png); } })
      .catch(() => { if (live) { setPreview(null); } });
    return () => { live = false; };
  }, [textures, selected, sourceSession]);

  const visible = useMemo(() => {
    const q = query.trim().toLowerCase();
    return rows.filter(r => (textures || !kind || r.kind === kind) && (!q || r.name.toLowerCase().includes(q) || r.archive.toLowerCase().includes(q)));
  }, [rows, query, kind, textures]);

  const submit = async (row: Row | null = selected) => {
    if (!row || !sourceSession) { setError(textures ? 'Select a texture.' : 'Select a model.'); return; }
    setBusy(true);
    setError(null);
    try {
      const op: Record<string, string> = textures
        ? { op: 'importTexture', sourceSession, name: row.name, sourceNamespace: row.namespace }
        : { op: 'importModel', sourceSession, archive: row.archive, name: row.name };
      if (target) { op[textures ? 'namespace' : 'targetArchive'] = target; }
      if (newName.trim()) { op.newName = newName.trim(); }
      request.resolve(await command('ASSET_EDIT', { AssetJson: JSON.stringify(op) }, request.targetSession));
    } catch (err) {
      setError(errText(err));
      setBusy(false);
    }
  };

  // Hidden while the file browser is up (it opens from here).
  if (!sourcePath && !error) { return null; }

  return (
    <div className="mb-backdrop" onPointerDown={e => { if (e.target === e.currentTarget) { cancel(); } }}>
      <div className="mb-fb mb-import" tabIndex={-1} onKeyDown={e => {
        if (e.key === 'Escape') { e.preventDefault(); cancel(); }
        else if (e.key === 'Enter' && (e.target as HTMLElement).tagName !== 'SELECT') { e.preventDefault(); void submit(); }
      }}>
        <div className="mb-fb-title">{textures ? 'Import texture from package' : 'Import from package'}</div>
        <div className="mb-fb-bar">
          <div className="mb-fb-chosen" title={sourcePath ?? ''}>{sourcePath ?? 'No package'}</div>
          <button type="button" className="lm-tool-btn" disabled={busy} onClick={() => void choosePackage()}>Change package…</button>
        </div>
        <div className="mb-fb-bar">
          <input className="me-search mb-import-search" type="text" placeholder={textures ? 'Filter textures…' : 'Filter models…'} autoFocus value={query} onChange={e => setQuery(e.target.value)} />
          {!textures && (
            <select className="mb-fb-filter" value={kind} onChange={e => setKind(e.target.value)}>
              <option value="">All kinds</option>
              {MODEL_KINDS.map(k => <option key={k} value={k}>{k}</option>)}
            </select>
          )}
        </div>
        <div className={textures ? 'mb-import-split' : 'mb-import-single'}>
          <div className="mb-fb-list">
            <div className="mb-fb-head mb-import-row"><span>{textures ? 'Texture' : 'Model'}</span><span>{textures ? 'Size' : 'Kind'}</span><span>Archive</span></div>
            {busy && rows.length === 0 && <div className="mb-fb-empty">Loading…</div>}
            {!busy && sourcePath && visible.length === 0 && <div className="mb-fb-empty">Nothing matches.</div>}
            {visible.map(r => (
              <div key={r.key}
                className={`mb-fb-row mb-import-row${selected === r ? ' sel' : ''}`}
                onClick={() => { setSelected(r); setError(null); setNewName(textures ? shortName(r.name) : ''); }}
                onDoubleClick={() => { setSelected(r); void submit(r); }}>
                <span className="mb-fb-name"><span className="mb-fb-ico-file"><Icon name={textures ? 'image' : 'library'} size={14} /></span>{r.name}</span>
                <span className="mb-fb-meta">{r.kind}</span>
                <span className="mb-fb-meta">{r.archive}</span>
              </div>
            ))}
          </div>
          {textures && (
            <div className="mb-import-preview">
              {preview ? <img src={preview} alt={selected?.name ?? ''} /> : <span className="lm-muted">{selected ? 'Loading…' : 'Select a texture'}</span>}
            </div>
          )}
        </div>
        <div className="mb-fb-foot">
          <label className="mb-fb-field">Into
            <select className="mb-fb-filter" value={target} onChange={e => setTarget(e.target.value)}>
              <option value="">Automatic (same namespace, else the map's)</option>
              {targets.map(a => <option key={a} value={a}>{textures ? `${a.toUpperCase()}_TXR.ZED` : a}</option>)}
            </select>
          </label>
          <label className="mb-fb-field">Name
            <input className="me-search" type="text" spellCheck={false} placeholder={selected?.name ?? 'keep the source name'} value={newName} onChange={e => setNewName(e.target.value)} />
          </label>
        </div>
        <div className="lm-muted mb-import-note">
          {textures
            ? 'Copies the texture with its mip levels and palette. The palette keeps its id unless this package uses it; a name this package already uses is reused when the pixels match, else renamed. Names over 15 characters are shortened so models can use them.'
            : 'Copies the model with its GEO definition (a character\'s skeleton), its textures and their palettes, and any models it is built from. Palettes keep their id unless this package uses it; a texture name this package already uses for other pixels is renamed.'}
          {' '}Undo removes the import.
        </div>
        {error && <div className="mb-dialog-error mb-fb-error">{error}</div>}
        <div className="me-actions mb-fb-actions">
          <button type="button" className="lm-tool-btn" onClick={cancel}>Cancel</button>
          <button type="button" className="lm-tool-btn mb-fb-primary" disabled={busy || !selected} onClick={() => void submit()}>Import</button>
        </div>
      </div>
    </div>
  );
}
