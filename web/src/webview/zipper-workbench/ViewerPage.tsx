import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import { Icon } from '../../shared/Icon';
import { PageHeader } from '../../shared/PageHeader';
import { activePage, modalOpen, useSignals } from '../../shared';
import { ViewerPageLogic, type BrowserItem, type BrowserItemKind } from './zipper-workbench-page';

const KIND_LABEL: Record<BrowserItemKind, string> = {
  archive: 'Archive', folder: 'Folder', clutter: 'Clutter model', model: 'Model', character: 'Character', weapon: 'Weapon', texture: 'Texture',
  palette: 'Palette', definition: 'GEO definition', file: 'File', record: 'Record',
};
const KIND_ICON: Record<BrowserItemKind, string> = {
  archive: 'library', folder: 'library', clutter: 'globe', model: 'database', character: 'bot', weapon: 'zap', texture: 'image',
  palette: 'sparkles', definition: 'workflow', file: 'filecode', record: 'tag',
};

interface MenuAction { label: string; run?: () => void; disabled?: boolean; danger?: boolean; separator?: boolean }

const MODEL_KINDS: BrowserItemKind[] = ['model', 'character', 'weapon', 'definition'];

function menuActions(c: ViewerPageLogic, item: BrowserItem | null): MenuAction[] {
  const out: MenuAction[] = [];
  const sep = () => { if (out.length && !out[out.length - 1].separator) { out.push({ label: '', separator: true }); } };
  if (item) {
    if (item.kind === 'archive' || item.kind === 'folder') { out.push({ label: 'Open', run: () => c.openItem(item) }); }
    if (item.kind === 'clutter') {
      out.push({ label: 'Show placements', run: () => c.openItem(item) });
      if (c.hasMap()) { out.push({ label: 'Open map in Map Builder', run: () => c.openInMap() }); }
    }
    if (c.isModelArchive(item)) { out.push({ label: 'Open in Model Editor', run: () => c.openInModelEditor() }); }
    if (MODEL_KINDS.includes(item.kind)) { out.push({ label: 'Open in Model Editor', run: () => c.openInModelEditor(item) }); }
    if (item.kind === 'character' || c.isAnimationArchive(item)) { out.push({ label: 'Open in Animation Editor', run: () => c.openInAnimationEditor() }); }
    if (item.kind === 'texture') { out.push({ label: 'Export PNG…', run: () => void c.exportTexturePng(item) }); }
    out.push({ label: 'Extract…', run: () => c.extractEntry(item.entry) });
    if (c.canEditFiles()) {
      sep();
      out.push({ label: 'Replace…', run: () => void c.replaceItem(item) });
      out.push({ label: 'Remove', run: () => void c.removeItem(item), danger: true });
    }
  }
  sep();
  out.push({ label: c.archiveStack().length === 0 ? 'Import into package…' : 'Import into this archive…', run: () => void c.importFiles(), disabled: !c.canEditFiles() });
  if (c.hasMap()) { out.push({ label: 'Open map in Map Builder', run: () => c.openInMap() }); }
  out.push({ label: 'Open package in Model Editor', run: () => c.openInModelEditor() });
  sep();
  out.push({ label: 'Extract all…', run: () => c.extractAll() });
  out.push({ label: 'Reload from disk', run: () => c.reloadPackage() });
  return out;
}

// A context menu at the pointer, in the VS Code menu colours; any click or Escape closes it.
function ContextMenu({ x, y, actions, onClose }: { x: number; y: number; actions: MenuAction[]; onClose: () => void }) {
  const ref = useRef<HTMLDivElement>(null);
  const [pos, setPos] = useState({ x, y });
  useLayoutEffect(() => {
    const r = ref.current?.getBoundingClientRect();
    if (r) { setPos({ x: Math.min(x, window.innerWidth - r.width - 4), y: Math.min(y, window.innerHeight - r.height - 4) }); }
  }, [x, y]);
  useEffect(() => {
    const close = (e: Event) => { if (!(e.target instanceof Node && ref.current?.contains(e.target))) { onClose(); } };
    const key = (e: KeyboardEvent) => { if (e.key === 'Escape') { onClose(); } };
    window.addEventListener('pointerdown', close, true);
    window.addEventListener('keydown', key);
    window.addEventListener('blur', onClose);
    return () => { window.removeEventListener('pointerdown', close, true); window.removeEventListener('keydown', key); window.removeEventListener('blur', onClose); };
  }, [onClose]);
  return (
    <div ref={ref} className="zip-ctx" style={{ left: pos.x, top: pos.y }} onContextMenu={e => e.preventDefault()}>
      {actions.map((a, i) => a.separator ? <div key={i} className="zip-ctx-sep" /> : (
        <button key={i} type="button" className={`zip-ctx-item${a.danger ? ' danger' : ''}`} disabled={a.disabled}
          onClick={() => { onClose(); a.run?.(); }}>{a.label}</button>
      ))}
    </div>
  );
}

function ItemIcon({ item, thumb }: { item: BrowserItem; thumb?: string }) {
  if (thumb) { return <img className="zip-fm-thumb" src={thumb} alt="" />; }
  return <span className={item.kind === 'archive' || item.kind === 'folder' ? 'mb-fb-ico-dir' : 'mb-fb-ico-file'}><Icon name={KIND_ICON[item.kind]} size={14} /></span>;
}

// The Asset Viewer's view: CodeDesigner's Angular template (the zipper workbench) in JSX over the same logic class.
// Save crafting and APACHE00 patching are not part of Map Builder, so those panels are left out.
export function ViewerPage() {
  useSignals();
  const [c] = useState(() => new ViewerPageLogic());

  useEffect(() => {
    c.init();
    return () => c.dispose();
  }, [c]);

  const result = c.result();
  const entry = c.selectedEntry();
  const mode = c.previewMode();
  // Texture archives open as icons (thumbnails); everything else as a list.
  const [view, setView] = useState<'auto' | 'list' | 'icons'>('auto');
  const showIcons = c.isCurrentFileTextureArchive() && c.textureThumbnails().length > 0 && view !== 'list';
  const thumbs = new Map(c.textureThumbnails().map(t => [t.entryIndex, t.dataUrl]));

  // Right-click: what the item (or, on empty space, the package) can do; editors open on their own page.
  const [menu, setMenu] = useState<{ x: number; y: number; item: BrowserItem | null } | null>(null);
  const showMenu = (ev: React.MouseEvent, item: BrowserItem | null) => {
    ev.preventDefault();
    ev.stopPropagation();
    if (item) { c.selectEntry(item.entry); }
    setMenu({ x: ev.clientX, y: ev.clientY, item });
  };

  // Backspace goes up a level, like the file browser (not while typing).
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      const t = e.target as HTMLElement;
      if (e.key !== 'Backspace' || t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || modalOpen() || activePage() !== 'viewer') { return; }
      if (c.archiveStack().length > 0 || c.recordFolders().length > 0) { e.preventDefault(); c.navigateUp(); }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [c]);

  return (
    <div className="zip-shell">
      <PageHeader icon="database" title="Asset Viewer" subtitle="Archive and asset workbench" />
      <div className="zip-toolbar">
        <div className="zip-title">
          <Icon name="database" size={16} />
          <span>Asset Viewer</span>
        </div>
        <div className="zip-toolbar-spacer" />
        <button className="zip-btn zip-primary" disabled={c.busy()} onClick={() => c.openFile()}>
          <Icon name="harddrive" size={14} /> Open
        </button>
        <button className="zip-btn" disabled={!c.canEditFiles()} onClick={() => void c.importFiles()}
          title={c.archiveStack().length === 0 ? 'Add .ZED / .ZAR / .MPS files (or another package\'s files) to the package' : 'Add a file (.rdr …) to this archive'}>
          <Icon name="download" size={14} /> Import…
        </button>
      </div>

      {/* Path bar, as in the file browser: up a level, where we are, filter, list / icons. */}
      {result && (
        <div className="mb-fb-bar zip-fm-bar">
          <button className="mb-fb-icon-btn" title="Up one level (Backspace)" disabled={c.archiveStack().length === 0 && c.recordFolders().length === 0} onClick={() => c.navigateUp()}>
            <Icon name="chevronup" size={15} />
          </button>
          <div className="zip-fm-crumbs">
            <button className="zip-fm-crumb" onClick={() => c.navigateRoot()} title={result.path}>
              <Icon name="harddrive" size={13} /> {c.rootBreadcrumb()}
            </button>
            {c.archiveStack().map((layer, i) => (
              <span key={i} style={{ display: 'contents' }}>
                <Icon name="chevronright" size={12} />
                <button className="zip-fm-crumb" onClick={() => c.navigateToDepth(i + 1)} title={layer.name}>{layer.name.split(/[\\/]/).pop()}</button>
              </span>
            ))}
            {c.folderNames().map((name, i) => (
              <span key={'f' + i} style={{ display: 'contents' }}>
                <Icon name="chevronright" size={12} />
                <button className="zip-fm-crumb" onClick={() => c.navigateToFolder(i + 1)} title={name}>{name}</button>
              </span>
            ))}
          </div>
          <label className="zip-fm-search">
            <Icon name="search" size={13} />
            <input type="text" placeholder={`Filter ${c.browserItems().length} item(s)…`} value={c.search()} onChange={e => c.onSearch(e.nativeEvent)} />
          </label>
          <button className={`mb-fb-icon-btn${showIcons ? '' : ' on'}`} title="List" onClick={() => setView('list')}><Icon name="filter" size={14} /></button>
          <button className={`mb-fb-icon-btn${showIcons ? ' on' : ''}`} title="Icons (textures)" disabled={!c.isCurrentFileTextureArchive()} onClick={() => setView('icons')}><Icon name="image" size={14} /></button>
        </div>
      )}

      {c.statusMsg() && (
        <button
          type="button"
          className={`zip-status${c.statusIsError() ? ' zip-status-error' : ''}${c.busy() ? ' zip-status-busy' : ''}`}
          disabled={!c.lastRevealPath()}
          onClick={() => c.revealLast()}
          title={c.lastRevealPath() ? 'Copy the output path' : undefined}>
          {c.statusMsg()}
        </button>
      )}

      {/* Long-running package prefetch: does not gate the rest of the UI; entries stay browsable while bytes stream in. */}
      {c.packageLoading() ? (
        <div className="zip-scanning-bar">
          <div className="zip-scanning-bar-inner" />
          <span className="zip-scanning-text">{c.packageMessage() || 'Loading package…'}</span>
        </div>
      ) : c.packageState() === 'failed' ? (
        <div className={`zip-package-status${c.packageState() === 'failed' ? ' zip-package-failed' : ''}`}>
          <span>{c.packageMessage()}</span>
          {c.packageState() === 'failed' && <button className="zip-btn" onClick={() => c.reloadPackage()}>Retry</button>}
        </div>
      ) : null}

      <div className="zip-main">
        <aside className="zip-sidebar">
          <div className="zip-path" title={result?.path || ''}>
            {result?.path || 'No file selected'}
          </div>

          <div className="zip-meta">
            <div><span>Format</span><strong>{result?.format || '-'}</strong></div>
            <div><span>Role</span><strong>{c.roleLabel()}</strong></div>
            <div><span>Size</span><strong>{c.formatBytes(result?.size || 0)}</strong></div>
            <div><span>Items</span><strong>{c.browserItems().length || c.saveRecordCount()}</strong></div>
          </div>

          {/* Texture quick-grid from the loaded TXR companion; a click jumps to that .tif entry. */}
          {c.sidebarTextures().length > 0 && (
            <div className="zip-sidebar-texgrid-section">
              <div className="zip-sidebar-texgrid-title">{c.sidebarTexturesTitle()} ({c.sidebarTextures().length})</div>
              <input
                className="zip-search-input"
                type="text"
                placeholder="Filter textures…"
                value={c.mapTextureFilter()}
                onChange={e => c.onMapTextureFilterInput(e.nativeEvent)}
                style={{ width: '100%', marginBottom: 6, background: 'var(--vscode-input-background,#1e1e1e)', color: 'var(--vscode-input-foreground,#d4d4d4)', border: '1px solid var(--vscode-panel-border,#3e3e42)', borderRadius: 4, padding: '4px 6px', fontSize: 11 }} />
              <div className="zip-sidebar-texgrid">
                {c.sidebarTextures().map(tex => (
                  <button key={tex.name} type="button" className={`zip-sidebar-tex${c.activeTextureName() === tex.name.toLowerCase() ? ' active' : ''}`} title={tex.name} onClick={() => c.onSidebarTextureClick(tex.name)}>
                    <img src={tex.dataUrl} alt={tex.name} />
                    <div className="zip-tex-label">{tex.name}</div>
                  </button>
                ))}
              </div>
            </div>
          )}
        </aside>

        <section className="zip-content">
          {!result ? (
            <div className="zip-empty">
              <Icon name="database" size={34} />
              <span>Open a ZDB, CDB, ZAR or ZED file.</span>
            </div>
          ) : (
            <>
              <div className="zip-head">
                <div className="zip-head-main">
                  <div className="zip-file-name">{c.currentLayerTitle()}</div>
                  <div className="zip-summary">{c.currentLayerSummary()}</div>
                </div>
                {entry && (
                  <button className="zip-btn" disabled={c.busy()} onClick={() => c.extractEntry(entry)}>
                    <Icon name="save" size={14} /> Extract
                  </button>
                )}
              </div>

              {mode === 'zanim' && <ZanimPreview c={c} />}
              {mode === 'animation' && <AnimationPreview c={c} />}
              {mode === 'rdr' && <RdrPreview c={c} />}
              {mode === 'mps' && <MpsPreview c={c} />}

              {c.pendingArchiveEntry() && (() => {
                const pending = c.pendingArchiveEntry()!;
                return (
                  <div className="zip-preview">
                    <div className="zip-preview-stage" style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', flexDirection: 'column', gap: 12 }}>
                      <div style={{ fontSize: 14, color: 'var(--vscode-descriptionForeground)' }}>
                        {pending.entryName} is a nested ZED/ZAR archive ({pending.size} bytes).
                      </div>
                      <button className="zip-btn zip-primary" onClick={() => c.enterPendingArchive()}>
                        <Icon name="harddrive" size={14} /> Enter Archive
                      </button>
                    </div>
                    <div className="zip-preview-info">
                      <div><span>Entry</span><strong>{pending.entryName}</strong></div>
                      <div><span>Size</span><strong>{pending.size} bytes</strong></div>
                    </div>
                  </div>
                );
              })()}

              {entry && (mode === 'texture' || mode === 'model') && <TextureModelPreview c={c} />}

              {c.selectedPalette() && (() => {
                const pal = c.selectedPalette()!;
                const swatches = Array.from({ length: 256 }, (_, i) => pal.rgba.subarray(i * 4, i * 4 + 4));
                return (
                  <div className="zip-preview">
                    <div className="zip-preview-stage zip-pal-stage">
                      <div className="zip-pal-grid">
                        {swatches.map((c4, i) => (
                          <span key={i} className="zip-pal-cell" title={`#${i}: ${c4[0]}, ${c4[1]}, ${c4[2]}, a ${c4[3]}`}
                            style={{ background: `rgba(${c4[0]}, ${c4[1]}, ${c4[2]}, ${c4[3] / 255})` }} />
                        ))}
                      </div>
                    </div>
                    <div className="zip-preview-info">
                      <div><span>Palette</span><strong>texpal_{pal.paletteId}</strong></div>
                      <div><span>Entries</span><strong>256</strong></div>
                      <div><span>Format</span><strong>{pal.is16Bit ? '16-bit (A1B5G5R5)' : '32-bit RGBA'}</strong></div>
                    </div>
                  </div>
                );
              })()}

              {showIcons ? (
                <div className="zip-tex-grid-wrap">
                  <div className="zip-tex-grid">
                    {c.textureThumbnails().filter(t => !c.search().trim() || t.name.toLowerCase().includes(c.search().trim().toLowerCase())).map(tex => (
                      <button key={tex.entryIndex} type="button" className={`zip-tex-tile${c.selectedIndex() === tex.entryIndex ? ' active' : ''}`} onClick={() => c.selectThumbnail(tex.entryIndex)}
                        onContextMenu={ev => { const item = c.browserItems().find(i => i.entry.index === tex.entryIndex); if (item) { showMenu(ev, item); } }}>
                        <div className="zip-tex-img-wrap"><img src={tex.dataUrl} alt={tex.name} /></div>
                        <div className="zip-tex-tile-name" title={tex.name}>{tex.name}</div>
                        <div className="zip-tex-tile-meta">{tex.width}x{tex.height}{!tex.paletteResolved ? ' (no palette)' : ''}</div>
                      </button>
                    ))}
                  </div>
                </div>
              ) : (
                <div className="mb-fb-list zip-fm-list" onContextMenu={ev => showMenu(ev, null)}>
                  <div className="mb-fb-head zip-fm-row"><span>Name</span><span>Type</span><span>Details</span><span>Size</span><span /></div>
                  {c.filteredItems().length === 0 && <div className="mb-fb-empty">{c.search().trim() ? 'Nothing matches the filter.' : 'This archive is empty.'}</div>}
                  {c.filteredItems().map(item => (
                    <div key={item.entry.index} role="button" tabIndex={0}
                      className={`mb-fb-row zip-fm-row zip-entry-row${entry?.index === item.entry.index ? ' sel' : ''}`}
                      title={item.entry.path || item.name}
                      onClick={() => c.selectEntry(item.entry)}
                      onDoubleClick={() => c.openItem(item)}
                      onContextMenu={ev => showMenu(ev, item)}
                      onKeyDown={ev => { if (ev.key === 'Enter') { ev.preventDefault(); c.openItem(item); } }}>
                      <span className="mb-fb-name"><ItemIcon item={item} thumb={thumbs.get(item.entry.index)} />{item.name}</span>
                      <span className="mb-fb-meta zip-fm-text">{KIND_LABEL[item.kind]}</span>
                      <span className="mb-fb-meta zip-fm-text" title={item.detail}>{item.detail}</span>
                      <span className="mb-fb-meta">{item.size ? c.formatBytes(item.size) : ''}</span>
                      <span className="zip-row-actions">
                        <button className="zip-icon-btn" disabled={c.busy()} title="Extract" onClick={ev => { c.extractEntry(item.entry); ev.stopPropagation(); }}>
                          <Icon name="save" size={13} />
                        </button>
                      </span>
                    </div>
                  ))}
                </div>
              )}
            </>
          )}
        </section>
      </div>
      {menu && <ContextMenu x={menu.x} y={menu.y} actions={menuActions(c, menu.item)} onClose={() => setMenu(null)} />}
    </div>
  );
}

type C = ViewerPageLogic;

function ZanimPreview({ c }: { c: C }) {
  const z = c.zanimSummary();
  const anim = c.selectedZanimAnimation();
  return (
    <div className="zip-preview zip-zanim-preview">
      <div className="zip-preview-stage zip-zanim-stage">
        {z ? (
          <>
            <div className="zip-zanim-layout">
              <div className="zip-zanim-list">
                <div className="zip-zanim-title">Animations ({c.visibleZanimAnimations().length} / {z.totalAnimations})</div>
                {c.visibleZanimAnimations().map(a => (
                  <button key={a.entryIndex} type="button" className={`zip-zanim-item${anim?.entryIndex === a.entryIndex ? ' active' : ''}`} onClick={() => c.selectZanimAnimation(a.entryIndex)}>
                    <span className="name">{a.name}</span>
                    <span className="meta">{a.nameRefs.slice(1).join(' · ') || 'no refs'}</span>
                    <span className="meta">{a.seqSize || a.sequence?.size || 0}B seq · {a.nodeRefs.length} node refs</span>
                  </button>
                ))}
              </div>
              <div className="zip-zanim-detail">
                {anim ? (
                  <>
                    <div className="zip-zanim-title">{anim.name}</div>
                    <div className="zip-zanim-kv">
                      <div><span>Entry</span><strong>#{anim.entryIndex}</strong></div>
                      <div><span>Names</span><strong>{anim.nameRefs.length}</strong></div>
                      <div><span>Node refs</span><strong>{anim.nodeRefs.length}</strong></div>
                      <div><span>Sequence</span><strong>{anim.seqSize || anim.sequence?.size || 0} bytes</strong></div>
                    </div>
                    <div className="zip-zanim-section">
                      <span>Name refs</span>
                      <div className="zip-zanim-tags">{anim.nameRefs.map((ref, i) => <span key={i}>{ref}</span>)}</div>
                    </div>
                    <div className="zip-zanim-section">
                      <span>Anim params</span>
                      <code>{c.zanimParamsLabel(anim.params)}</code>
                    </div>
                    {anim.nodeRefs.length > 0 && (
                      <div className="zip-zanim-section">
                        <span>Node refs</span>
                        <div className="zip-zanim-code-list">{anim.nodeRefs.slice(0, 16).map((ref, i) => <code key={i}>{c.zanimNodeRefLabel(ref.words)}</code>)}</div>
                      </div>
                    )}
                    {anim.sequence ? (
                      <>
                        <div className="zip-zanim-section"><span>Seq header u32</span><code>{c.zanimWordsLabel(anim.sequence.headerWords)}</code></div>
                        <div className="zip-zanim-section"><span>Seq preview u16</span><code>{c.zanimWordsLabel(anim.sequence.previewU16)}</code></div>
                        <div className="zip-zanim-section"><span>Seq bytes</span><code>{anim.sequence.previewHex}</code></div>
                      </>
                    ) : (
                      <div className="zip-zanim-section"><span>Seq bytes</span><code>{z.nodesLoaded ? 'empty' : 'loading payloads...'}</code></div>
                    )}
                  </>
                ) : (
                  <>
                    <div className="zip-zanim-title">Archive Summary</div>
                    <div className="zip-zanim-section">
                      <span>RDR search paths</span>
                      <div className="zip-zanim-code-list">
                        {z.sets.flatMap(set => set.rdrPaths.map(p => <code key={set.entryIndex + ':' + p}>{p}</code>))}
                      </div>
                    </div>
                  </>
                )}
              </div>
            </div>
            {c.previewMessage() && <div className="zip-preview-overlay">{c.previewMessage()}</div>}
          </>
        ) : (
          <div className="zip-preview-overlay">{c.previewMessage() || 'No ZANIM data selected.'}</div>
        )}
      </div>
      <div className="zip-preview-info">
        {z ? (
          <>
            <div><span>Format</span><strong>ZANIM script/effect archive</strong></div>
            <div><span>Sets</span><strong>{z.sets.length} / declared {z.declaredSetCount ?? '-'}</strong></div>
            <div><span>Names</span><strong>{z.totalNames}</strong></div>
            <div><span>RDR paths</span><strong>{z.totalRdrPaths}</strong></div>
            <div><span>Animations</span><strong>{z.totalAnimations}</strong></div>
            <div><span>Payloads</span><strong>{z.nodesLoaded ? 'loaded' : 'loading'}</strong></div>
            {z.mainParams && <div><span>Main params</span><strong>{c.zanimParamsLabel(z.mainParams)}</strong></div>}
          </>
        ) : (
          <div><span>Status</span><strong>{c.previewMessage() || 'Loading...'}</strong></div>
        )}
      </div>
    </div>
  );
}

function AnimationPreview({ c }: { c: C }) {
  const clip = c.animationClip();
  return (
    <div className="zip-preview zip-animation-preview">
      <div className="zip-preview-stage zip-animation-stage">
        <canvas ref={el => { c.animationCanvas = el ? { nativeElement: el } : undefined; }} className="zip-preview-canvas zip-animation-canvas" />
        {!clip && c.previewMessage() && <div className="zip-preview-overlay">{c.previewMessage()}</div>}
      </div>
      <div className="zip-preview-info">
        {clip ? (
          <>
            <div className="zip-pane-section">
              <div className="zip-pane-section-title">Animation</div>
              <div><span>Clip</span><strong>{clip.name}</strong></div>
              <div><span>Duration</span><strong>{clip.duration.toFixed(6)} sec</strong></div>
              <div><span>Frames</span><strong>{clip.sampleCount} samples / {c.animationFpsLabel(clip)}</strong></div>
              <div><span>Bones</span><strong>{clip.boneCount} total / {clip.attachmentBones.length} sockets</strong></div>
              <div><span>Status</span><strong>{clip.parseOk ? 'clean parse' : 'partial parse'}</strong></div>
            </div>

            <div className="zip-pane-section">
              <div className="zip-pane-section-title">Playback</div>
              <div className="zip-animation-controls">
                <button className="zip-btn" type="button" onClick={() => c.toggleAnimationPlayback()}>
                  <Icon name={c.animationPlaying() ? 'pause' : 'play'} size={13} />
                  {c.animationPlaying() ? 'Pause' : 'Play'}
                </button>
                <button className="zip-btn" type="button" onClick={() => c.resetAnimationView()}>
                  <Icon name="refreshcw" size={13} />
                  Reset View
                </button>
              </div>
              <label className="zip-animation-scrub">
                <span>Frame {c.animationFrame()} / {c.animationFrameMax(clip)}</span>
                <input type="range" min="0" max={c.animationFrameMax(clip)} value={c.animationFrame()} onChange={e => c.onAnimationFrameInput(e.nativeEvent)} />
              </label>
            </div>

            <div className="zip-pane-section">
              <div className="zip-pane-section-title">Tracks</div>
              <div className="zip-animation-counts">
                <span>0x0C {clip.counts['0x0C'] || 0}</span>
                <span>0x1C {clip.counts['0x1C'] || 0}</span>
                <span>0x2C {clip.counts['0x2C'] || 0}</span>
                <span>0x3C {clip.counts['0x3C'] || 0}</span>
              </div>
              {clip.attachmentBones.length > 0 && (
                <div className="zip-animation-tags">{clip.attachmentBones.map(b => <span key={b} className="weapon">{c.cleanAnimationBoneLabel(b)}</span>)}</div>
              )}
              {clip.errors.length > 0 && (
                <div className="zip-animation-errors">{clip.errors.slice(0, 4).map(err => <div key={err}>{err}</div>)}</div>
              )}
            </div>

            <div className="zip-pane-section">
              <div className="zip-pane-section-title">Bones</div>
              <div className="zip-animation-list">
                {c.animationBoneRows().map(bone => (
                  <div key={bone.index} className={`zip-animation-row${bone.attachment ? ' weapon' : ''}`}>
                    <span className="idx">{bone.index.toString().padStart(2, '0')}</span>
                    <span className="bone" style={{ paddingLeft: bone.depth * 10 }}>{bone.clean}</span>
                    <span className="tag">{bone.trackType}</span>
                    <span className="tag">p={bone.parent}</span>
                  </div>
                ))}
              </div>
            </div>

            <div className="zip-pane-section">
              <div className="zip-pane-section-title">Track Detail</div>
              <div className="zip-animation-list">
                {c.animationTrackRows().map(track => (
                  <div key={track.offset} className={`zip-animation-track${c.isAnimationAttachment(track.boneName) ? ' weapon' : ''}`}>
                    <div>
                      <span className="idx">{track.boneIndex.toString().padStart(2, '0')}</span>
                      <strong>{c.cleanAnimationBoneLabel(track.boneName)}</strong>
                      <span className="tag">{c.animationHex(track.type)}</span>
                      <span className="muted">@ {c.animationHex(track.offset)}</span>
                    </div>
                    <div className="muted">{track.typeName}</div>
                    <div>base={c.animationTrackBase(track)}</div>
                    <div>quat={c.animationTrackQuat(track)}</div>
                  </div>
                ))}
              </div>
            </div>
          </>
        ) : (
          <div><span>Status</span><strong>{c.previewMessage() || 'Loading animation...'}</strong></div>
        )}
      </div>
    </div>
  );
}

function RdrPreview({ c }: { c: C }) {
  const r = c.rdr();
  const f = c.rdrFallbackLists();
  return (
    <div className="zip-preview">
      <div className="zip-preview-stage" style={{ display: 'block', overflow: 'auto', padding: 8, background: '#111318' }}>
        {r ? (
          r.fallback ? (
            f && (
              <>
                {/* Decoded .tif thumbnails for every textureRef in this screen's string pool (needs the textures loaded). */}
                {c.rdrTextureThumbnails().length > 0 && (
                  <div className="zip-rdr-fallback-section">
                    <h4>Matched textures ({c.rdrTextureThumbnails().length})</h4>
                    <div className="zip-rdr-tex-grid">
                      {c.rdrTextureThumbnails().map(t => (
                        <div key={t.name} className="zip-rdr-tex-card">
                          <div className="zip-rdr-tex-img"><img src={t.dataUrl} alt={t.name} loading="lazy" /></div>
                          <div className="zip-rdr-tex-name">{t.name}</div>
                          <div className="zip-rdr-tex-meta">{t.width}×{t.height}</div>
                        </div>
                      ))}
                    </div>
                  </div>
                )}
                <div className="zip-rdr-fallback-section"><h4>Texture refs ({f.textureRefs.length})</h4><ul>{f.textureRefs.map(s => <li key={s}>{s}</li>)}</ul></div>
                <div className="zip-rdr-fallback-section"><h4>File refs ({f.fileRefs.length})</h4><ul>{f.fileRefs.map(s => <li key={s}>{s}</li>)}</ul></div>
                <div className="zip-rdr-fallback-section"><h4>Model refs ({f.modelRefs.length})</h4><ul>{f.modelRefs.map(s => <li key={s}>{s}</li>)}</ul></div>
              </>
            )
          ) : (
            <div className="zip-rdr-tree">
              {c.rdrVisibleRows().map(row => (
                <div key={row.path} className={`zip-rdr-tree-row${c.rdrIsExpandable(row) ? ' expandable' : ''}`} style={{ paddingLeft: 6 + row.depth * 14 }}
                  onClick={() => { if (c.rdrIsExpandable(row)) { c.toggleRdrNode(row.path); } }}>
                  <span className="zip-rdr-caret">{c.rdrIsExpandable(row) ? (c.isRdrExpanded(row.path) ? '▾' : '▸') : ''}</span>
                  <span className="zip-rdr-name">{row.name}</span>
                  <span className="zip-rdr-type">:{row.kind}</span>
                  {row.kind !== 'object' && row.kind !== 'array'
                    ? <span className="zip-rdr-value">{row.value}</span>
                    : <span className="zip-rdr-type">({row.value})</span>}
                </div>
              ))}
            </div>
          )
        ) : (
          <div style={{ padding: 8, color: 'var(--vscode-descriptionForeground)' }}>{c.previewMessage() || 'Loading…'}</div>
        )}
      </div>
      <div className="zip-preview-info">
        {r ? (
          <>
            {r.fallback ? (
              <>
                <div><span>Mode</span><strong>fallback (string scan)</strong></div>
                {r.parseError && <div><span>Parse error</span><strong>{r.parseError}</strong></div>}
              </>
            ) : (
              <>
                <div><span>RDR version</span><strong>{r.version}</strong></div>
                <div><span>Atoms</span><strong>{r.atomCount}</strong></div>
                <div><span>String pool</span><strong>{r.stringPoolSize} bytes</strong></div>
              </>
            )}
            {r.entry && <div><span>Entry</span><strong>{r.entry}</strong></div>}
            <div className="zip-dump-buttons">
              <button className="zip-btn" onClick={() => c.dumpRdrReport()}>Dump RDR report</button>
              {r.fallback && c.rdrTextureThumbnails().length === 0 && c.companionTextures() === null && (
                <button className="zip-btn" onClick={() => c.loadRdrCompanionTextures()} title="Load the sibling _TXR.ZED / _PAL.ZED so texture thumbnails appear above the ref list.">Load Textures</button>
              )}
            </div>
          </>
        ) : (
          <div><span>Status</span><strong>{c.previewMessage() || 'Loading...'}</strong></div>
        )}
      </div>
    </div>
  );
}

function MpsPreview({ c }: { c: C }) {
  const m = c.mps();
  return (
    <div className="zip-preview">
      <div className="zip-preview-stage zip-mps-table" style={{ display: 'block' }}>
        {m ? (
          m.maps.length === 0 ? (
            <div style={{ padding: 8, color: 'var(--vscode-descriptionForeground)' }}>No AI maps found in this MPS file.</div>
          ) : (
            <table>
              <thead>
                <tr><th>Name</th><th>W × H</th><th>Min XYZ</th><th>Max XYZ</th><th>Scale X / Z</th><th>Offset</th></tr>
              </thead>
              <tbody>
                {m.maps.map(map => (
                  <tr key={map.offset}>
                    <td>{map.name}</td>
                    <td>{map.width} × {map.height}</td>
                    <td>{map.minX.toFixed(1)}, {map.minY.toFixed(1)}, {map.minZ.toFixed(1)}</td>
                    <td>{map.maxX.toFixed(1)}, {map.maxY.toFixed(1)}, {map.maxZ.toFixed(1)}</td>
                    <td>{map.scaleX.toFixed(3)} / {map.scaleZ.toFixed(3)}</td>
                    <td>{c.hex(map.offset)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )
        ) : (
          <div style={{ padding: 8, color: 'var(--vscode-descriptionForeground)' }}>{c.previewMessage() || 'Loading…'}</div>
        )}
      </div>
      <div className="zip-preview-info">
        {m ? (
          <>
            <div><span>Declared count</span><strong>{m.declaredCount}</strong></div>
            <div><span>Found count</span><strong>{m.foundCount}</strong></div>
            <div><span>File size</span><strong>{c.formatBytes(m.fileSize)}</strong></div>
          </>
        ) : (
          <div><span>Status</span><strong>{c.previewMessage() || 'Loading…'}</strong></div>
        )}
      </div>
    </div>
  );
}

function TextureModelPreview({ c }: { c: C }) {
  const mode = c.previewMode();
  const tex = c.texture();
  const mdl = c.model();
  const info = c.modelDecodeInfo();
  const src = c.textureSourceSummary();
  const ref = c.activeMapCameraReference();
  return (
    <div className="zip-preview">
      <div className="zip-preview-stage">
        {mode === 'texture'
          ? <img className="zip-preview-pic" src={c.textureDataUrl()} alt="Texture preview" />
          : <canvas ref={el => { c.previewCanvas = el ? { nativeElement: el } : undefined; }} className="zip-preview-canvas" />}
        {c.previewMessage() && <div className="zip-preview-overlay">{c.previewMessage()}</div>}
      </div>
      <div className="zip-preview-info">
        <div className="zip-preview-tabs">
          <button type="button" className={`zip-preview-tab${mode === 'texture' ? ' active' : ''}`} disabled={!c.canPreviewTexture()} onClick={() => c.setPreviewMode('texture')}>Texture</button>
          <button type="button" className={`zip-preview-tab${mode === 'model' ? ' active' : ''}`} disabled={!c.canPreviewModel()} onClick={() => c.setPreviewMode('model')}>Model</button>
        </div>
        {mode === 'texture' && tex && (
          <>
            <div><span>Size</span><strong>{tex.header.width} x {tex.header.height}</strong></div>
            <div><span>Format</span><strong>{c.textureFormatLabel(tex)}</strong></div>
            <div><span>Palette</span><strong className={tex.header.isIndexed && !tex.paletteResolved ? 'zip-warn' : ''}>{c.paletteLabel(tex)}</strong></div>
            <div><span>GS registers</span><strong title="From the record's TEX0 / CLAMP / TEST / ALPHA registers">{c.textureGsLabel(tex)}</strong></div>
          </>
        )}
        {mode === 'model' && mdl && (
          <>
            <div className="zip-pane-section">
              <div className="zip-pane-section-title">Mesh</div>
              <div><span>Chunks</span><strong>{mdl.chunks.length}</strong></div>
              <div><span>Vertices / Triangles</span><strong>{c.activeVertexCount()} / {c.activeTriangleCount()}</strong></div>
              {info && (
                <>
                  <div>
                    <span>Decode</span>
                    <strong title="How textures were bound to chunks. DMA chain = exact, from the model's own draw chains.">{info.binding}: {info.chainBound}/{info.chunks} chunks bound{info.visuals ? `, ${info.visuals} visual${info.visuals === 1 ? '' : 's'}` : ''}</strong>
                  </div>
                  <div>
                    <span>Lighting</span>
                    <strong>{info.coloured > 0 ? `baked vertex colours (${info.coloured} chunks)` : 'none in model (scene light)'}</strong>
                  </div>
                  <div>
                    <span>Position units</span>
                    <strong title="How int16 positions were converted. Change it under Render if a model is 2048x too big or too small.">{info.unit === 'sixteenth' ? '1/16 units' : 'chunk scale / 32768'} — {info.unitReason}</strong>
                  </div>
                  {info.droppedChunks > 0 && <div><span>Not drawn</span><strong className="zip-warn">{info.droppedChunks} chunk(s) over the geometry cap</strong></div>}
                  {info.missingTextures.length > 0 && (
                    <div>
                      <span>Missing textures ({info.missingTextures.length})</span>
                      <div className="zip-texrefs">{info.missingTextures.map(name => <span key={name} className="zip-warn">{name}</span>)}</div>
                      <div className="zip-hint">
                        {src && <>Searched {src.textures} texture(s){src.archives.length ? ' from ' + src.archives.join(', ') : ' (no texture archive loaded)'}. </>}
                        {c.packageState() === 'loading' && 'The package is still loading; textures bind when it arrives. '}
                        {c.packageState() === 'failed' && 'The package failed to load.'}
                      </div>
                      <button className="zip-btn" disabled={c.packageLoading()} onClick={() => c.reloadPackage()} title="Fetch the whole package again (bypasses the cache) and re-bind textures.">Reload package</button>
                    </div>
                  )}
                </>
              )}
              {c.availableVisuals().length > 1 && (
                <div>
                  <span>Visual</span>
                  <select className="zip-chunk-select" value={c.selectedVisual()} onChange={e => c.onSelectedVisualChange(e.nativeEvent)} title="Isolate one visual (N<node>_<visual> draw chain). Detail levels and state alternatives show up as separate visuals.">
                    <option value="">All visuals ({c.availableVisuals().length})</option>
                    {c.availableVisuals().map(key => <option key={key} value={key}>{c.visualLabel(key)}</option>)}
                  </select>
                </div>
              )}
              {mdl.chunks.length > 1 && (
                <div>
                  <span>Chunk</span>
                  <select className="zip-chunk-select" value={c.selectedChunk()} onChange={e => c.onChunkChange(e.nativeEvent)}>
                    <option value={-1}>All chunks ({mdl.chunks.length})</option>
                    {mdl.chunks.map((chunk, idx) => <option key={chunk.offset} value={idx}>{c.chunkLabel(idx, chunk)}</option>)}
                  </select>
                </div>
              )}
              {mdl.textureRefs.length > 0 && (
                <div>
                  <span>Texture refs</span>
                  <div className="zip-texrefs">{mdl.textureRefs.map(r => <span key={r}>{r}</span>)}</div>
                </div>
              )}
            </div>

            {/* Render: all draw and shading toggles in one place. */}
            <div className="zip-pane-section">
              <div className="zip-pane-section-title">Render</div>
              <div className="zip-map-controls">
                <div className="zip-map-ctrl">
                  <span>Mode</span>
                  <select value={c.mapRenderMode()} onChange={e => c.onMapRenderModeChange(e.nativeEvent)}>
                    <option value="view3dTexturedLit">3D textured + fog/lighting</option>
                    <option value="view3dTextured">3D textured solid</option>
                    <option value="view3dSolid">3D material solid</option>
                    <option value="topSolid">top-down material solid</option>
                    <option value="topWire">top-down wire</option>
                    <option value="view3dWire">3D wire</option>
                  </select>
                </div>
                <div className="zip-map-ctrl">
                  <span>Camera</span>
                  <select value={c.mapCameraView()} onChange={e => c.onMapCameraViewChange(e.nativeEvent)}>
                    <option value="auto">auto</option>
                    <option value="iso">3D iso</option>
                    <option value="top">top</option>
                    <option value="front">front</option>
                    <option value="back">back</option>
                    <option value="left">left</option>
                    <option value="right">right</option>
                    <option value="mapTop" disabled={!ref}>nearest map top</option>
                    <option value="mapIso" disabled={!ref}>nearest map iso</option>
                  </select>
                </div>
                <div className="zip-map-ctrl">
                  <span>Triangle stride</span>
                  <select value={c.mapTriStride()} onChange={e => c.onMapTriStrideChange(e.nativeEvent)}>
                    <option value={1}>1 — full solid</option>
                    <option value={2}>2 — skip half</option>
                    <option value={4}>4 — debug overview</option>
                    <option value={8}>8 — wire overview</option>
                  </select>
                </div>
                <div className="zip-map-ctrl">
                  <span>V orientation</span>
                  <select value={c.mapTextureV()} onChange={e => c.onMapTextureVChange(e.nativeEvent)}>
                    <option value="flipV">flip V</option>
                    <option value="direct">direct</option>
                  </select>
                </div>
                <div className="zip-map-ctrl">
                  <span>UV interpretation</span>
                  <select value={c.mapUvInterpretation()} onChange={e => c.onMapUvInterpretationChange(e.nativeEvent)}>
                    <option value="autoWorld">auto world UV</option>
                    <option value="norm4096">raw / 4096 normalized</option>
                    <option value="pixel16">raw / 16 pixel tiled</option>
                    <option value="pixel256">raw / 256 pixel tiled</option>
                  </select>
                </div>
                <label className="zip-map-ctrl zip-check">
                  <input type="checkbox" checked={c.showSky()} onChange={e => c.onShowSkyChange(e.nativeEvent)} />
                  <span>Show sky (dome, sun, flare)</span>
                </label>
                <div className="zip-map-ctrl">
                  <span>Lighting</span>
                  <select value={c.mapLighting()} onChange={e => c.onMapLightingChange(e.nativeEvent)} title="Baked vertex lighting from the model. The game multiplies texture by colour / 128; some areas (church interiors) are baked very dark.">
                    <option value="baked">baked (as in game)</option>
                    <option value="bright">baked x2 (brighter)</option>
                    <option value="off">off (texture only)</option>
                  </select>
                </div>
                <div className="zip-map-ctrl">
                  <span>Position units</span>
                  <select value={c.mapPositionUnit()} onChange={e => c.onMapPositionUnitChange(e.nativeEvent)} title="How int16 vertex positions map to units. auto reads the GEO vparams flags: weapons normalise by the chunk scale, props / vehicles / map objects store 1/16 units.">
                    <option value="auto">auto (GEO vparams flags)</option>
                    <option value="scale">chunk scale / 32768 (weapons)</option>
                    <option value="sixteenth">1/16 units (props, vehicles, map)</option>
                  </select>
                </div>
              </div>
              {ref && <div><span>Nearest map bounds</span><strong>{ref.label}</strong></div>}
            </div>

            {/* Spawn camera: first-person view from the map's start points and AI waypoints (AIMAPS.MPS). */}
            {c.mapPoints().length > 0 && (
              <div className="zip-pane-section">
                <div className="zip-pane-section-title">Spawn camera</div>
                <div className="zip-map-controls">
                  <div className="zip-map-ctrl">
                    <span>Point</span>
                    <select value={c.selectedMapPoint()} onChange={e => c.onSelectedMapPointChange(e.nativeEvent)}>
                      {c.mapPoints().map(p => <option key={p.name} value={p.name}>{p.name} ({p.kind})</option>)}
                    </select>
                  </div>
                  <label className="zip-map-ctrl zip-check">
                    <input type="checkbox" checked={c.showMapMarkers()} onChange={e => c.onShowMapMarkersChange(e.nativeEvent)} />
                    <span>Show markers (green start, orange hostage, blue waypoint)</span>
                  </label>
                </div>
                <div className="zip-dump-buttons">
                  {!c.flying()
                    ? <button className="zip-btn zip-primary" onClick={() => c.flyFromSelectedPoint()} title="Put a first-person camera on the selected point, at eye height.">Fly from point</button>
                    : <button className="zip-btn" onClick={() => c.exitFly()}>Back to orbit (Esc)</button>}
                </div>
                {c.flying() && <div className="zip-hint">W/A/S/D or arrows move, drag the view to look, E/Space up, Q/C down, Shift faster, wheel sets speed, Esc back to orbit. Pick another point to jump there.</div>}
              </div>
            )}

            <div className="zip-pane-section">
              <div className="zip-pane-section-title">Materials</div>
              <div className="zip-map-controls">
                <div className="zip-map-ctrl">
                  <span>Binding</span>
                  <select value={c.mapMaterialBinding()} onChange={e => c.onMapMaterialBindingChange(e.nativeEvent)} title="chain = exact binding from the model's DMA chains (texture per material section + baked vertex colours), as the game draws it. The marker modes are string-scan heuristics for payloads without chains.">
                    <option value="chain">DMA chain (exact, default)</option>
                    <option value="tailScan">tail scan</option>
                    <option value="smartWorld">smart world marker</option>
                    <option value="sliceNearest">slice nearest marker</option>
                    <option value="globalNearest">global nearest marker</option>
                  </select>
                </div>
                {c.availablePalettes().length > 0 && (
                  <div className="zip-map-ctrl">
                    <span>Palette override</span>
                    <select value={c.paletteOverride() ?? ''} onChange={e => c.onPaletteOverrideChange(e.nativeEvent)}>
                      <option value="">(auto)</option>
                      {c.availablePalettes().map(key => <option key={key} value={key}>{key}</option>)}
                    </select>
                  </div>
                )}
              </div>
            </div>

            {/* Compose / dump utilities (dumps go to the browser console). */}
            <div className="zip-pane-section">
              <div className="zip-pane-section-title">Tools</div>
              <div className="zip-dump-buttons">
                <button className="zip-btn zip-primary" disabled={!c.canOpenInModelEditor()} onClick={() => c.openInModelEditor()} title="Open this model in the Model Editor (inspect, retexture, tint, replace textures, save a copy or a pack).">Open in Model Editor</button>
                <button className="zip-btn zip-primary" disabled={!c.companionScene()} onClick={() => c.composeGeoScene()} title="Walk the GEO scene tree, resolve each placed instance to a model from the loaded package, and render the composed mesh.">Compose GEO scene</button>
                <button className="zip-btn" onClick={() => c.composeHierarchicalModel()} title="Decode chunks from the selected entry AND every child node in its subtree, applying per-node transforms. Required for CLIB players and other hierarchical models.">Compose hierarchical</button>
                <button className="zip-btn" onClick={() => c.dumpMissingTextures()}>Dump missing textures</button>
                <button className="zip-btn" onClick={() => c.dumpMaterialReport()}>Dump material report</button>
                <button className="zip-btn" onClick={() => c.dumpGeoReport()}>Dump GEO report</button>
                <button className="zip-btn" disabled={!c.companionScene()} onClick={() => c.dumpGeoInstances()}>Dump GEO instances</button>
              </div>
            </div>
          </>
        )}
      </div>
    </div>
  );
}
