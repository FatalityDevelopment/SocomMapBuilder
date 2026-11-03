import { useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import { Icon } from '../../shared/Icon';
import { PageHeader } from '../../shared/PageHeader';
import { openRequest, useSignals } from '../../shared';
import { aiChange, provideAiState } from '../../shared/aiBridge';
import { LIGHTINGS } from '../zipper-core/viewport-core';
import { AssetViewerLogic, KIND_LABEL, formatBytes } from './asset-viewer-page';
import { placeKey, type ItemKind, type Preview, type ViewerItem } from './viewer-client';
import { BytePatch, IconSysEditor, MpsEditor, RdrEditor, TextEditor } from './editors';
import { ClutterEditor, GeoEditor, PaletteEditor, ZanimEditor } from './scene-editors';

const KIND_ICON: Record<ItemKind, string> = {
  folder: 'library', package: 'harddrive', models: 'database', textures: 'image', palettes: 'sparkles', geo: 'workflow',
  clips: 'repeat', archive: 'memorystick', group: 'tag', model: 'globe', texture: 'image', palette: 'sparkles', clip: 'play',
  rdr: 'filecode', mps: 'network', sound: 'radiotower', bank: 'radiotower', movie: 'monitor', image: 'camera', icon: 'star',
  iconsys: 'tag', program: 'terminal', text: 'bookopen', binary: 'code',
};
const MODEL_ICON: Record<string, string> = { character: 'bot', weapon: 'zap', world: 'globe', sky: 'sparkles', prop: 'database', library: 'library' };

function kindIcon(item: ViewerItem): string {
  return item.kind === 'model' ? MODEL_ICON[item.ref?.modelKind ?? ''] ?? 'database' : KIND_ICON[item.kind];
}

// The Asset Viewer: a disc / folder browser on the left, the preview in the middle, what the selection is and what
// can be done with it on the right. Previews use the editors' viewports and decoding (see asset-viewer-page.ts).
export function AssetViewerPage() {
  useSignals();
  const [c] = useState(() => new AssetViewerLogic());

  useEffect(() => {
    c.mount();
    return () => c.dispose();
  }, [c]);

  const request = openRequest();
  useEffect(() => {
    if (request?.page === 'viewer' && request.path) { void c.openPath(request.path); }
  }, [request, c]);

  // ui_show page "viewer" with a path: open it here.
  const change = aiChange();
  useEffect(() => { if (change) { void c.followAi(change); } }, [change, c]);
  useEffect(() => provideAiState('viewer', () => c.aiState()), [c]);
  // What the AI changed or asked to see: bring its row into view once the editor has drawn it.
  const mark = c.aiMark();
  useEffect(() => {
    if (!mark) { return; }
    const t = window.setTimeout(() => document.querySelector('.av-app .av-ai-mark')?.scrollIntoView({ block: 'center', behavior: 'smooth' }), 200);
    return () => window.clearTimeout(t);
  }, [mark]);

  const l = c.listing();
  const sel = c.selected();
  const p = c.preview();
  const stage = c.stage();

  return (
    <div className="me-app has-header av-app" onPointerDown={e => c.closeMenusOutside(e.nativeEvent)}>
      <PageHeader icon="database" title="Asset Viewer" subtitle="Browse a disc or package and preview what it holds" />
      <div className="me-menubar">
        <span className="me-brand"><Icon name="database" size={14} /> Asset Viewer</span>
        {c.menus.map(menu => (
          <div key={menu.id} className={`me-menu${c.openMenu() === menu.id ? ' open' : ''}`}>
            <button className="me-menu-btn" onClick={() => c.toggleMenu(menu.id)} onMouseEnter={() => c.hoverMenu(menu.id)}>{menu.label}</button>
            {c.openMenu() === menu.id && (
              <div className="me-menu-pop">
                {menu.items.map((item, i) => item.separator ? <div key={i} className="me-menu-sep" /> : (
                  <button key={i} className="me-menu-item" disabled={item.disabled?.()} onClick={() => c.runMenu(item)}>
                    <span className="me-menu-check" />
                    <span className="me-menu-label">{item.label}</span>
                    <span className="me-menu-key">{item.shortcut ?? ''}</span>
                  </button>
                ))}
              </div>
            )}
          </div>
        ))}
        <span className="me-spacer" />
        {l && <span className="me-pkg" title={l.path}>{l.path}</span>}
        {c.editStatus() && (
          <span className={`me-badge${c.editStatus()!.dirty.length ? ' me-badge-dirty' : ''}`} title={c.editStatus()!.dirty.join(', ') || 'No unsaved changes'}>
            {c.editStatus()!.dirty.length ? `${c.editStatus()!.dirty.length} unsaved` : 'saved'} · rev {c.editStatus()!.revision}
          </span>
        )}
        {c.unsavedElsewhere().length > 0 && (
          <button className="me-badge me-badge-dirty av-badge-btn" title={`Unsaved edits in ${c.unsavedElsewhere().map(st => `${st.path} (${st.dirty.join(', ')})`).join('; ')}. Edit › Go to… opens one to save it.`} onClick={() => c.toggleMenu('edit')}>
            +{c.unsavedElsewhere().length} elsewhere
          </button>
        )}
        {l?.session && <span className="me-session" title="Asset session the previews read (shared with the editors)">{l.session}</span>}
      </div>

      <div className="me-header av-header">
        <div className="me-seg">
          <button disabled={!c.canBack()} onClick={() => void c.back()} title="Back (Alt+Left)"><Icon name="arrowleft" size={13} /></button>
          <button disabled={!c.canForward()} onClick={() => void c.forward()} title="Forward (Alt+Right)"><Icon name="arrowright" size={13} /></button>
          <button disabled={!c.parentPlace()} onClick={() => void c.up()} title="Up one level (Backspace)"><Icon name="chevronup" size={13} /></button>
        </div>
        <nav className="av-crumbs" aria-label="Location">
          {l ? l.crumbs.map((cr, i) => (
            <span key={placeKey(cr)} className="av-crumb-wrap">
              {i > 0 && <Icon name="chevronright" size={11} />}
              <button className={`av-crumb${i === l.crumbs.length - 1 ? ' here' : ''}`} onClick={() => void c.navigate(cr)} title={cr.entryPath ?? cr.path}>
                {cr.inner.length > 0 || /\.[a-z0-9]+$/i.test(cr.label) ? null : <Icon name={i === 0 ? 'harddrive' : 'library'} size={12} />}
                {cr.label}
              </button>
            </span>
          )) : <span className="lm-muted">No folder open</span>}
        </nav>
        <button className="lm-tool-btn" onClick={() => void c.openFolder()} title="Browse a folder (Ctrl+O)"><Icon name="harddrive" size={13} /> Open…</button>
      </div>

      <div className="me-body av-body">
        <aside className="me-assets av-list-panel">
          <div className="me-panel-title">
            <span className="av-title-text" title={l?.name}>{l ? `${l.name}` : 'Browser'}</span>
            <span className="lm-muted">{l ? `${c.visibleItems().length}${c.visibleItems().length !== l.items.length ? ` / ${l.items.length}` : ''}` : ''}</span>
          </div>
          <input className="me-search" type="text" placeholder="Filter…" value={c.query()} onChange={e => c.query.set(e.target.value)} disabled={!l} />
          {c.kinds().length > 1 && (
            <div className="me-kinds">
              <button className={`me-chip${!c.kindFilter() ? ' active' : ''}`} onClick={() => c.kindFilter.set('')}>All</button>
              {c.kinds().map(k => (
                <button key={k.kind} className={`me-chip${c.kindFilter() === k.kind ? ' active' : ''}`} onClick={() => c.kindFilter.set(c.kindFilter() === k.kind ? '' : k.kind)}>
                  {KIND_LABEL[k.kind]} <span className="lm-muted">{k.count}</span>
                </button>
              ))}
            </div>
          )}
          {l ? <ItemList c={c} /> : (
            <div className="me-empty av-start">
              <p>Open a folder to browse it: a SOCOM II disc (its drive), a RUN folder, or any folder of packages.</p>
              <button className="lm-tool-btn" onClick={() => void c.openFolder()}>Open folder…</button>
              <button className="lm-tool-btn" onClick={() => void c.openFile()}>Open file…</button>
            </div>
          )}
          {c.listBusy() && <div className="av-list-busy" role="status">Reading…</div>}
        </aside>

        <section className="av-stage" aria-label="Preview">
          <div className={`av-3d${c.viewportKind() ? ' on' : ''}`}>
            <div className="av-3d-bar">
              <div className="me-seg" title="Shading">
                {c.shadings.map(s => <button key={s.id} className={c.shading() === s.id ? 'on' : ''} onClick={() => c.setShading(s.id)}>{s.label}</button>)}
              </div>
              <div className="me-seg" title="Lighting">
                {LIGHTINGS.map(li => <button key={li.id} className={c.lighting() === li.id ? 'on' : ''} onClick={() => c.setLighting(li.id)} title={li.title}>{li.label}</button>)}
              </div>
              <span className="me-spacer" />
              <div className="me-seg" title="View">
                <button onClick={() => c.view('front')}>Front</button>
                <button onClick={() => c.view('right')}>Side</button>
                <button onClick={() => c.view('top')}>Top</button>
                <button onClick={() => c.view('iso')}>Iso</button>
                <button onClick={() => c.frame()} title="Frame (F)">Frame</button>
              </div>
            </div>
            {(['model', 'map', 'clip'] as const).map(kind => (
              <div key={kind} className="me-viewport av-vp" hidden={c.viewportKind() !== kind}>
                <canvas ref={el => c.attachCanvas(kind, el)} tabIndex={0} />
                <div className="me-vp-label">
                  <div>{p?.name}</div>
                  <div className="lm-muted">{c.modelInfo()}</div>
                  <div className="lm-muted">Middle-drag orbit · right-drag pan · wheel zoom{kind === 'map' ? ' · WASD fly' : ''}</div>
                </div>
              </div>
            ))}
            {c.viewportKind() === 'clip' && (
              <div className="av-clip-bar">
                <button className="lm-tool-btn" onClick={() => c.toggleClip()} title="Play / pause (Space)"><Icon name={c.clipPlaying() ? 'pause' : 'play'} size={13} /></button>
                <input type="range" min={0} max={c.clipDuration() || 0} step={0.001} value={c.clipTime()} onChange={e => c.scrubClip(+e.target.value)} aria-label="Time" />
                <span className="lm-muted">{c.clipTime().toFixed(2)} / {c.clipDuration().toFixed(2)} s</span>
              </div>
            )}
          </div>
          {!c.viewportKind() && <StageBody c={c} />}
          {c.stageNote() && <div className="av-note" role="status">{c.stageNote()}</div>}
          {c.mapProgress() && <div className="me-vp-busy" role="status">{c.mapProgress()}</div>}
        </section>

        <aside className="me-side av-inspector">
          <div className="me-tab-body">
            {(sel || l) ? (
              <div className="me-card">
                <div className="av-insp-head">
                  <span className="av-insp-icon"><Icon name={sel ? kindIcon(sel) : KIND_ICON[l!.kind]} size={18} /></span>
                  <div className="av-insp-copy">
                    <div className="av-insp-name" title={sel?.name ?? l?.name}>{sel?.name ?? l?.name}</div>
                    <div className="lm-muted">{sel ? (sel.kind === 'model' ? `Model · ${sel.ref?.modelKind ?? ''}` : KIND_LABEL[sel.kind]) : `${KIND_LABEL[l!.kind]} (this location)`}</div>
                  </div>
                </div>
                {sel?.detail && <div className="lm-muted av-detail">{sel.detail}</div>}
                {p && p.facts.length > 0 && (
                  <dl className="me-kv av-kv">
                    {p.facts.map(([k, v], i) => <FactRow key={i} k={k} v={v} />)}
                  </dl>
                )}
              </div>
            ) : <div className="me-empty">Nothing selected.</div>}

            <Actions c={c} />

            {stage === 'clip' && c.characters().length > 0 && (
              <div className="me-card">
                <div className="me-card-title">Play on</div>
                <select className="me-input" value={c.character()} onChange={e => void c.pickCharacter(e.target.value)}>
                  {c.characters().map(ch => <option key={ch.archive + ':' + ch.name} value={ch.archive + ':' + ch.name}>{ch.name}</option>)}
                </select>
              </div>
            )}

            {p?.type === 'audio' && p.samples && <SampleList c={c} p={p} />}
          </div>
        </aside>
      </div>

      <div className={`me-statusbar${c.messageError() ? ' err' : ''}`}>
        <span title={c.message()}>{c.message() || 'Ready'}</span>
        <span className="me-spacer" />
        {sel && <span className="lm-muted">{sel.size ? formatBytes(sel.size) : ''}</span>}
      </div>
    </div>
  );
}

type C = AssetViewerLogic;

function FactRow({ k, v }: { k: string; v: string }) {
  return <><dt>{k}</dt><dd title={v}>{v}</dd></>;
}

// Rows are drawn only where they show: archives hold up to 11 000 records (RUN/SOUNDS/VAGSTORE.ZAR).
const ROW = 24;

function ItemList({ c }: { c: C }) {
  const items = c.visibleItems();
  const sel = c.selected();
  const ref = useRef<HTMLDivElement>(null);
  const [scroll, setScroll] = useState(0);
  const [height, setHeight] = useState(600);
  useLayoutEffect(() => {
    const el = ref.current;
    if (!el) { return; }
    const ro = new ResizeObserver(() => setHeight(el.clientHeight));
    ro.observe(el);
    setHeight(el.clientHeight);
    return () => ro.disconnect();
  }, []);
  // Keep the selection in view when the keyboard moves it.
  const selIndex = useMemo(() => sel ? items.findIndex(i => placeKey(i, i.ref) === placeKey(sel, sel.ref)) : -1, [items, sel]);
  useEffect(() => {
    const el = ref.current;
    if (!el || selIndex < 0) { return; }
    const top = selIndex * ROW;
    if (top < el.scrollTop) { el.scrollTop = top; } else if (top + ROW > el.scrollTop + el.clientHeight) { el.scrollTop = top + ROW - el.clientHeight; }
  }, [selIndex]);
  // A new listing starts at the top.
  const listing = c.listing();
  useEffect(() => { if (ref.current) { ref.current.scrollTop = 0; } setScroll(0); }, [listing]);

  const first = Math.max(0, Math.floor(scroll / ROW) - 10);
  const last = Math.min(items.length, Math.ceil((scroll + height) / ROW) + 10);
  return (
    <div className="av-list" ref={ref} onScroll={e => setScroll(e.currentTarget.scrollTop)} role="listbox" aria-label="Items">
      {items.length === 0 && <div className="me-empty">{c.query() || c.kindFilter() ? 'Nothing matches the filter.' : 'Empty.'}</div>}
      <div style={{ height: items.length * ROW, position: 'relative' }}>
        {items.slice(first, last).map((item, k) => {
          const i = first + k;
          const on = i === selIndex;
          return (
            <div key={placeKey(item, item.ref)} role="option" aria-selected={on} className={`av-row${on ? ' active' : ''}`} style={{ top: i * ROW }}
              title={item.detail ? `${item.name}\n${item.detail}` : item.name}
              onClick={() => void c.select(item)} onDoubleClick={() => void c.activate(item)}>
              <span className={`av-row-icon k-${item.kind}`}><Icon name={kindIcon(item)} size={13} /></span>
              <span className="av-row-name">{item.name}</span>
              <span className="av-row-meta">{item.detail || (item.kind !== 'folder' ? KIND_LABEL[item.kind] : '')}</span>
              <span className="av-row-size">{item.size ? formatBytes(item.size) : ''}</span>
              {c.isBrowsable(item) && <span className="av-row-go"><Icon name="chevronright" size={12} /></span>}
            </div>
          );
        })}
      </div>
    </div>
  );
}

function Actions({ c }: { c: C }) {
  const sel = c.selected();
  const l = c.listing();
  const p = c.preview();
  const buttons: Array<{ label: string; run: () => void; primary?: boolean; title?: string }> = [];
  if (sel && c.isBrowsable(sel)) { buttons.push({ label: 'Open', run: () => void c.activate(sel), primary: true, title: 'Browse into it (Enter or double-click)' }); }
  if (p?.type === 'package' && p.hasMap) {
    buttons.push(c.mapShown() ? { label: 'Hide map', run: () => c.hideMap() } : { label: 'Show map', run: () => void c.showMap(), primary: !sel || !c.isBrowsable(sel), title: 'Draw the composed map here' });
    buttons.push({ label: 'Open in Map Builder', run: () => c.openInMap() });
  }
  if (c.modelEditorTarget()) { buttons.push({ label: 'Open in Model Editor', run: () => c.openInModelEditor(), primary: sel?.kind === 'model' }); }
  if (c.animationTarget()) { buttons.push({ label: 'Open in Animation Editor', run: () => c.openInAnimationEditor() }); }
  if (c.canReplaceImage()) { buttons.push({ label: 'Replace picture…', run: () => void c.replaceImage(), title: 'Replace it from an image file (PNG, JPG …); Edit › Undo takes it back' }); }
  if (c.canExportImage()) { buttons.push({ label: 'Export PNG…', run: () => c.exportImage() }); }
  if (c.canExportAudio()) { buttons.push({ label: 'Save WAV…', run: () => c.exportAudio() }); }
  if (c.canExtract(sel)) { buttons.push({ label: 'Extract…', run: () => void c.extractSelected(), title: 'Save the record’s bytes as they are stored' }); }
  const edits = c.canEdit(sel) || c.canImportHere();
  if (!buttons.length && !edits) { return null; }
  return (
    <div className="me-card">
      <div className="me-card-title">Actions</div>
      <div className="av-actions">
        {buttons.map(b => <button key={b.label} className={`lm-tool-btn${b.primary ? ' av-primary' : ''}`} onClick={b.run} title={b.title}>{b.label}</button>)}
      </div>
      {edits && l && (
        <>
          <div className="me-card-title av-sub">Change {l.name}</div>
          <div className="av-actions">
            <button className="lm-tool-btn" disabled={!c.canImportHere()} onClick={() => void c.importHere()} title="Add a file as a new record">Import…</button>
            <button className="lm-tool-btn" disabled={!c.canEdit(sel)} onClick={() => void c.replaceSelected()}>Replace…</button>
            <button className="lm-tool-btn av-danger" disabled={!c.canEdit(sel)} onClick={() => void c.removeSelected()}>Remove</button>
          </div>
          <div className="lm-muted me-note">Written to a file you choose (saving over the original keeps a .bak).</div>
        </>
      )}
    </div>
  );
}

function SampleList({ c, p }: { c: C; p: Extract<Preview, { type: 'audio' }> }) {
  return (
    <div className="me-card av-samples">
      <div className="me-card-title">Samples <span className="lm-muted">{p.samples!.length}</span></div>
      <label className="me-toggle">Rate
        <select className="me-input" value={c.rate()} onChange={e => void c.setRate(+e.target.value)} title="Banks do not record their samples' rate">
          {[11025, 16000, 22050, 32000, 44100, 48000].map(r => <option key={r} value={r}>{r} Hz</option>)}
        </select>
      </label>
      <div className="av-sample-list">
        {p.samples!.map((s, i) => (
          <button key={i} className={`me-row${i === p.sample ? ' active' : ''}`} onClick={() => void c.pickSample(i)}>
            <span className="me-row-name">#{i + 1}</span>
            <span className="me-row-meta">{s.seconds.toFixed(2)} s · 0x{s.offset.toString(16).toUpperCase()}</span>
          </button>
        ))}
      </div>
    </div>
  );
}

function StageBody({ c }: { c: C }) {
  const stage = c.stage();
  const p = c.preview();
  switch (stage) {
    case 'empty':
      return <div className="av-center"><Icon name="database" size={34} /><span>{c.listing() ? 'Select something to preview it.' : 'Open a folder or file to start.'}</span></div>;
    case 'loading':
      return <div className="av-center"><span>Loading…</span></div>;
    case 'error':
      return <div className="av-center av-error"><span>{c.stageError()}</span></div>;
    case 'image':
      return <ImageStage c={c} />;
    case 'palette':
      return p?.type === 'palette' ? <PaletteEditor c={c} p={p} /> : null;
    case 'audio':
      return p?.type === 'audio' ? (
        <div className="av-center">
          <Icon name="radiotower" size={34} />
          {c.audioUrl() ? <audio key={c.audioUrl()} className="av-audio" controls src={c.audioUrl()} /> : <span>No audible samples.</span>}
          <span className="lm-muted">{p.seconds.toFixed(2)} s at {p.rate} Hz{p.samples ? ` · sample ${(p.sample ?? 0) + 1} of ${p.samples.length}` : ''}</span>
        </div>
      ) : null;
    case 'movie':
      return p?.type === 'movie' ? (
        <div className="av-center">
          {c.movieUrl()
            ? <video key={c.movieUrl()} className="av-video" controls src={c.movieUrl()} />
            : (
              <>
                <span className="av-big-icon"><Icon name="monitor" size={40} /></span>
                <div className="av-summary-name">{p.name}</div>
                {p.canConvert
                  ? <button className="lm-tool-btn av-primary" disabled={c.movieBusy()} onClick={() => void c.convertMovie()}>{c.movieBusy() ? 'Converting…' : 'Convert for preview'}</button>
                  : <div className="lm-muted av-hint">PS2 movies (MPEG-2 with SPU audio) do not play in a browser. With ffmpeg on the server (winget install Gyan.FFmpeg, then restart it) they convert to MP4 here.</div>}
                <div className="lm-muted av-hint">Converted once to H.264 / AAC and kept, so it plays straight away next time.</div>
              </>
            )}
          {c.movieNote() && <div className="lm-muted av-hint">{c.movieNote()}</div>}
        </div>
      ) : null;
    case 'rdr':
      return p?.type === 'rdr' ? <RdrEditor c={c} p={p} /> : null;
    case 'mps':
      return p?.type === 'mps' ? <MpsEditor c={c} p={p} /> : null;
    case 'text':
      return p?.type === 'text' ? <TextEditor c={c} p={p} /> : null;
    case 'iconsys':
      return p?.type === 'iconsys' ? <IconSysEditor c={c} p={p} /> : null;
    case 'geo':
      return p?.type === 'geo' ? <GeoEditor c={c} p={p} /> : null;
    case 'clutter':
      return p?.type === 'clutter' ? <ClutterEditor c={c} p={p} /> : null;
    case 'zanim':
      return p?.type === 'zanim' ? <ZanimEditor c={c} p={p} /> : null;
    case 'hex':
      return p?.type === 'hex' ? <HexStage c={c} p={p} /> : null;
    case 'program':
      return p?.type === 'program' ? (
        <div className="av-scroll">
          <table className="av-table">
            <thead><tr><th>Section</th><th>Type</th><th>Address</th><th>Size</th></tr></thead>
            <tbody>{p.sections.map((s, i) => <tr key={i}><td>{s.name || '(unnamed)'}</td><td>{s.type}</td><td className="av-mono">{s.address}</td><td>{formatBytes(s.size)}</td></tr>)}</tbody>
          </table>
        </div>
      ) : null;
    default:
      return <SummaryStage c={c} />;
  }
}

function SummaryStage({ c }: { c: C }) {
  const p = c.preview();
  const sel = c.selected();
  const l = c.listing();
  const kind = sel?.kind ?? l?.kind ?? 'binary';
  return (
    <div className="av-center">
      <span className="av-big-icon"><Icon name={sel ? kindIcon(sel) : KIND_ICON[kind]} size={40} /></span>
      <div className="av-summary-name">{p?.name}</div>
      <div className="lm-muted">{KIND_LABEL[kind]}</div>
      {p?.type === 'package' && p.hasMap && <button className="lm-tool-btn av-primary" onClick={() => void c.showMap()}>Show map {p.mapNamespace?.toUpperCase()}</button>}
      {sel && c.isBrowsable(sel) && <button className="lm-tool-btn" onClick={() => void c.activate(sel)}>Open</button>}
    </div>
  );
}

function ImageStage({ c }: { c: C }) {
  const [w, h] = c.imageSize();
  const zoom = c.zoom();
  const style = zoom === 'fit' ? undefined : { width: w * zoom, height: h * zoom, maxWidth: 'none', maxHeight: 'none' };
  return (
    <div className="av-image-wrap">
      <div className="av-image-bar">
        <div className="me-seg" title="Zoom">
          {(['fit', 1, 2, 4] as const).map(z => <button key={z} className={zoom === z ? 'on' : ''} onClick={() => c.zoom.set(z)}>{z === 'fit' ? 'Fit' : `${z}×`}</button>)}
        </div>
        <span className="lm-muted">{w}×{h}</span>
      </div>
      <div className={`av-image-scroll${zoom === 'fit' ? ' fit' : ''}`}>
        {c.imageUrl() && <img className="av-image" src={c.imageUrl()} alt={c.preview()?.name ?? ''} style={style} />}
      </div>
    </div>
  );
}

function HexStage({ c, p }: { c: C; p: Extract<Preview, { type: 'hex' }> }) {
  const lines = useMemo(() => {
    const bin = atob(p.bytes);
    const out: string[] = [];
    for (let o = 0; o < bin.length; o += 16) {
      const row = Array.from(bin.slice(o, o + 16), ch => ch.charCodeAt(0));
      const hex = row.map(b => b.toString(16).padStart(2, '0')).join(' ').padEnd(47, ' ');
      const ascii = row.map(b => b >= 0x20 && b < 0x7f ? String.fromCharCode(b) : '.').join('');
      out.push(`${o.toString(16).padStart(8, '0')}  ${hex}  ${ascii}`);
    }
    return out;
  }, [p.bytes]);
  return (
    <div className="av-hex">
      <div className="av-hex-main">
        {c.canEditInPlace() && <BytePatch c={c} />}
        <pre className="av-scroll av-mono">{lines.join('\n')}{lines.length >= 256 ? '\n… (first 4 KB)' : ''}</pre>
      </div>
      {p.strings.length > 0 && (
        <div className="av-strings">
          <div className="me-card-title">Strings <span className="lm-muted">{p.strings.length}</span></div>
          <div className="av-scroll av-mono">{p.strings.map((s, i) => <div key={i}>{s}</div>)}</div>
        </div>
      )}
    </div>
  );
}
