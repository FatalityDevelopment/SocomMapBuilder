import { useCallback, useEffect, useMemo, useRef, useState, type KeyboardEvent as ReactKeyboardEvent } from 'react';
import { command } from '../api/client';
import { Icon } from './Icon';
import { activeBrowse, cleanPath, rememberedFolder, useSignals, type BrowseRequest } from './index';

interface Entry { name: string; path: string; dir: boolean; size: number | null; modified: string }
interface Listing { path: string; parent: string | null; entries: Entry[] }
interface Places { drives: Array<{ name: string; path: string; label: string; kind: string }>; places: Array<{ name: string; path: string }> }

const ALL_FILES = { label: 'All files', extensions: [] as string[] };

function formatSize(n: number | null): string {
  if (n === null) { return ''; }
  if (n < 1024) { return `${n} B`; }
  if (n < 1024 * 1024) { return `${(n / 1024).toFixed(1)} KB`; }
  if (n < 1024 * 1024 * 1024) { return `${(n / 1024 / 1024).toFixed(1)} MB`; }
  return `${(n / 1024 / 1024 / 1024).toFixed(2)} GB`;
}

const extOf = (name: string) => (/\.([^.]+)$/.exec(name)?.[1] ?? '').toLowerCase();
const joinPath = (dir: string, name: string) => dir.replace(/[\\/]+$/, '') + '\\' + name;

// The host's file browser: pick a file to open, a folder, or where to save. Lists the disk through the server.
export function FileBrowser() {
  useSignals();
  const request = activeBrowse();
  return request ? <BrowserDialog key={request.title + request.mode} request={request} /> : null;
}

function BrowserDialog({ request }: { request: BrowseRequest }) {
  const filters = useMemo(() => [...(request.filters ?? []), ALL_FILES], [request.filters]);
  const [filterIndex, setFilterIndex] = useState(0);
  const [places, setPlaces] = useState<Places | null>(null);
  const [listing, setListing] = useState<Listing | null>(null);
  const [pathText, setPathText] = useState('');
  const [selected, setSelected] = useState<Entry | null>(null);
  const [fileName, setFileName] = useState(request.fileName ?? '');
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const listRef = useRef<HTMLDivElement>(null);
  const rootRef = useRef<HTMLDivElement>(null);

  // Keys (Escape, arrows, Enter) reach the dialog from the start, unless a field took focus.
  useEffect(() => {
    if (!rootRef.current?.contains(document.activeElement)) { rootRef.current?.focus(); }
  }, []);

  const navigate = useCallback(async (path: string, select?: string) => {
    setLoading(true);
    try {
      const l = await command<Listing>('FS_LIST', { InputPath: path });
      setListing(l);
      setPathText(l.path);
      setError(null);
      const pick = select ? l.entries.find(e => e.name.toLowerCase() === select.toLowerCase()) ?? null : null;
      setSelected(pick);
      listRef.current?.scrollTo({ top: 0 });
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setLoading(false);
    }
  }, []);

  // Start where the caller asked, else the last folder used for this kind of request, else the desktop.
  useEffect(() => {
    void command<Places>('FS_PLACES').then(setPlaces).catch(() => setPlaces({ drives: [], places: [] }));
    const start = cleanPath(request.start) || rememberedFolder(request.key ?? request.mode);
    const isFile = !!start && /\.[^.\\/]+$/.test(start) && request.mode !== 'folder';
    void navigate(start, isFile ? start.split(/[\\/]/).pop() : undefined);
  }, [request, navigate]);

  const filter = filters[filterIndex];
  const visible = useMemo(() => (listing?.entries ?? []).filter(e =>
    e.dir || (request.mode !== 'folder' && (filter.extensions.length === 0 || filter.extensions.includes(extOf(e.name))))), [listing, filter, request.mode]);

  const cancel = () => request.resolve(null);

  const choose = (entry: Entry | null = selected) => {
    if (!listing) { return; }
    if (request.mode === 'folder') {
      request.resolve(entry?.dir ? entry.path : listing.path);
      return;
    }
    if (request.mode === 'save') {
      const name = fileName.trim();
      if (!name) { setError('Enter a file name.'); return; }
      // A typed full path wins; otherwise the name goes in the current folder, with the filter's extension added.
      let target = /^[a-z]:[\\/]|^[\\/]{2}/i.test(name) ? name : joinPath(listing.path, name);
      const ext = request.filters?.[0]?.extensions[0];
      if (ext && !/\.[^.\\/]+$/.test(target)) { target += '.' + ext; }
      request.resolve(target);
      return;
    }
    if (!entry) { setError('Select a file.'); return; }
    if (entry.dir && !request.allowFolder) { void navigate(entry.path); return; }
    request.resolve(entry.path);
  };

  const activate = (entry: Entry) => {
    if (entry.dir) { void navigate(entry.path); return; }
    if (request.mode === 'save') { setFileName(entry.name); choose(entry); return; }
    choose(entry);
  };

  const onKey = (e: ReactKeyboardEvent) => {
    if (e.key === 'Escape') { e.preventDefault(); cancel(); return; }
    const inInput = (e.target as HTMLElement).tagName === 'INPUT' || (e.target as HTMLElement).tagName === 'SELECT';
    if (inInput) { return; }
    if (e.key === 'Backspace' && listing?.parent) { e.preventDefault(); void navigate(listing.parent); }
    else if (e.key === 'Enter' && selected) { e.preventDefault(); activate(selected); }
    else if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      e.preventDefault();
      const i = selected ? visible.findIndex(v => v.path === selected.path) : -1;
      const next = visible[Math.max(0, Math.min(visible.length - 1, i + (e.key === 'ArrowDown' ? 1 : -1)))];
      if (next) {
        setSelected(next);
        if (request.mode === 'save' && !next.dir) { setFileName(next.name); }
        listRef.current?.querySelector(`[data-path="${CSS.escape(next.path)}"]`)?.scrollIntoView({ block: 'nearest' });
      }
    }
  };

  const submitLabel = request.submitLabel ?? (request.mode === 'save' ? 'Save' : request.mode === 'folder' ? 'Select folder' : 'Open');
  const openFolderLabel = request.mode === 'open' && request.allowFolder && selected?.dir;

  return (
    <div className="mb-backdrop" onPointerDown={e => { if (e.target === e.currentTarget) { cancel(); } }}>
      <div className="mb-fb" ref={rootRef} onKeyDown={onKey} tabIndex={-1}>
        <div className="mb-fb-title">{request.title}</div>
        <div className="mb-fb-bar">
          <button className="mb-fb-icon-btn" title="Up one folder (Backspace)" disabled={!listing?.parent} onClick={() => listing?.parent && void navigate(listing.parent)}>
            <Icon name="chevronup" size={15} />
          </button>
          <button className="mb-fb-icon-btn" title="Refresh" disabled={!listing} onClick={() => listing && void navigate(listing.path)}>
            <Icon name="refreshcw" size={14} />
          </button>
          <input
            className="mb-fb-path"
            type="text"
            value={pathText}
            spellCheck={false}
            onChange={e => setPathText(e.target.value)}
            onKeyDown={e => {
              if (e.key !== 'Enter') { return; }
              e.preventDefault();
              // A typed file path lists its folder with the file selected.
              const typed = cleanPath(pathText);
              void navigate(typed, /\.[^.\\/]+$/.test(typed) ? typed.split(/[\\/]/).pop() : undefined);
            }}
            title="Type or paste a folder (or file) path and press Enter"
          />
        </div>

        <div className="mb-fb-body">
          <nav className="mb-fb-places">
            {places && places.places.length > 0 && <div className="mb-fb-group">Places</div>}
            {places?.places.map(p => (
              <button key={p.path} className={`mb-fb-place${listing?.path === p.path ? ' on' : ''}`} title={p.path} onClick={() => void navigate(p.path)}>
                <Icon name={p.name === 'Desktop' ? 'monitor' : p.name === 'Downloads' ? 'download' : p.name === 'Home' ? 'harddrive' : 'bookopen'} size={14} /> {p.name}
              </button>
            ))}
            {places && places.drives.length > 0 && <div className="mb-fb-group">Drives</div>}
            {places?.drives.map(d => (
              <button key={d.path} className={`mb-fb-place${listing?.path === d.path ? ' on' : ''}`} title={d.path} onClick={() => void navigate(d.path)}>
                <Icon name={d.kind === 'CDRom' ? 'disc3' : 'harddrive'} size={14} /> {d.name}{d.label ? ` ${d.label}` : ''}
              </button>
            ))}
          </nav>

          <div className="mb-fb-list" ref={listRef}>
            <div className="mb-fb-head"><span>Name</span><span>Size</span><span>Modified</span></div>
            {loading && !listing && <div className="mb-fb-empty">Loading…</div>}
            {listing && visible.length === 0 && <div className="mb-fb-empty">{request.mode === 'folder' ? 'No sub-folders.' : 'Nothing here matches the filter.'}</div>}
            {visible.map(e => (
              <div
                key={e.path}
                data-path={e.path}
                className={`mb-fb-row${selected?.path === e.path ? ' sel' : ''}`}
                onClick={() => { setSelected(e); if (request.mode === 'save' && !e.dir) { setFileName(e.name); } }}
                onDoubleClick={() => activate(e)}
              >
                <span className="mb-fb-name">
                  <span className={e.dir ? 'mb-fb-ico-dir' : 'mb-fb-ico-file'}><Icon name={e.dir ? 'library' : 'filecode'} size={14} /></span>
                  {e.name}
                </span>
                <span className="mb-fb-meta">{formatSize(e.size)}</span>
                <span className="mb-fb-meta">{e.modified}</span>
              </div>
            ))}
          </div>
        </div>

        <div className="mb-fb-foot">
          {request.mode === 'save' ? (
            <label className="mb-fb-field">File name
              <input className="me-search" type="text" autoFocus value={fileName} spellCheck={false}
                onChange={e => setFileName(e.target.value)} onKeyDown={e => { if (e.key === 'Enter') { e.preventDefault(); choose(); } }} />
            </label>
          ) : (
            <div className="mb-fb-chosen" title={selected?.path ?? listing?.path}>
              {request.mode === 'folder' ? (selected?.dir ? selected.path : listing?.path) : selected ? selected.path : 'No file selected'}
            </div>
          )}
          {request.mode !== 'folder' && (
            <select className="mb-fb-filter" value={filterIndex} onChange={e => setFilterIndex(+e.target.value)}>
              {filters.map((f, i) => <option key={f.label} value={i}>{f.label}{f.extensions.length ? ` (${f.extensions.map(x => '*.' + x).join(', ')})` : ''}</option>)}
            </select>
          )}
        </div>
        {error && <div className="mb-dialog-error mb-fb-error">{error}</div>}
        <div className="me-actions mb-fb-actions">
          <button type="button" className="lm-tool-btn" onClick={cancel}>Cancel</button>
          <button type="button" className="lm-tool-btn mb-fb-primary"
            disabled={request.mode === 'open' && !selected}
            onClick={() => choose()}>{openFolderLabel ? 'Open folder' : submitLabel}</button>
        </div>
      </div>
    </div>
  );
}
