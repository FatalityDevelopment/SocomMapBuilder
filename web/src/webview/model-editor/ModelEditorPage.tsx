import { useEffect, useRef, useState } from 'react';
import { Icon } from '../../shared/Icon';
import { openRequest, useSignals } from '../../shared';
import { PageHeader } from '../../shared/PageHeader';
import { ModelEditorPageLogic } from './model-editor-page';

// The Model Editor's view: CodeDesigner's Angular template in JSX over the same logic class.
export function ModelEditorPage() {
  useSignals();
  const [c] = useState(() => new ModelEditorPageLogic());
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const uvCanvasRef = useRef<HTMLCanvasElement>(null);

  useEffect(() => {
    c.mount(canvasRef.current!, uvCanvasRef.current!);
    return () => c.dispose();
  }, [c]);

  // "Open model…" from another page: the same package (and session) with that model selected.
  const request = openRequest();
  useEffect(() => {
    if (request?.page === 'models') { void c.openPackage(request.path, request.model); }
  }, [request, c]);

  const s = c.status();
  const target = c.exportTarget();
  const m = c.mesh();
  const d = c.description();
  const counts = c.counts();
  const h = c.hover();

  return (
    <div className="me-app has-header" onPointerDown={e => c.closeMenusOutside(e.nativeEvent)}>
      <PageHeader icon="library" title="Model Editor" subtitle="Meshes, textures and vertex colours of SOCOM II models" />
      {/* Menu bar */}
      <div className="me-menubar">
        <span className="me-brand"><Icon name="library" size={14} /> Model Editor</span>
        {c.menus.map(menu => (
          <div key={menu.id} className={`me-menu${c.openMenu() === menu.id ? ' open' : ''}`}>
            <button className="me-menu-btn" onClick={() => c.toggleMenu(menu.id)} onMouseEnter={() => c.hoverMenu(menu.id)}>{menu.label}</button>
            {c.openMenu() === menu.id && (
              <div className="me-menu-pop">
                {menu.items.map((item, i) => item.separator ? <div key={i} className="me-menu-sep" /> : (
                  <button key={i} className="me-menu-item" disabled={item.disabled?.()} onClick={() => c.runMenu(item)}>
                    <span className="me-menu-check">{item.checked?.() ? '✓' : ''}</span>
                    <span className="me-menu-label">{c.labelOf(item)}</span>
                    <span className="me-menu-key">{item.shortcut ?? ''}</span>
                  </button>
                ))}
              </div>
            )}
          </div>
        ))}
        <span className="me-spacer" />
        {s && (
          <>
            <span className="me-pkg" title={s.path}>{c.fileName(s.path)}</span>
            {s.game && <span className="me-badge" title="Edits and saves are not available for this game yet">{s.game} · read-only</span>}
            {target
              ? <button className="lm-tool-btn me-export-btn" disabled={c.busy()} onClick={() => void c.quickExport()}
                  title={`Convert ${c.current()?.name ?? ''} into a ${target} package and open it (Ctrl+E). File › Export with options… picks animations / names.`}>Export to {target}…</button>
              : <span className={`me-badge${c.dirtyCount() > 0 ? ' me-badge-dirty' : ''}`}>{c.dirtyCount() ? `${c.dirtyCount()} unsaved` : 'saved'} · rev {s.revision}</span>}
            <span className="me-session" title="Asset session id (shared with the other pages)">{s.session}</span>
          </>
        )}
      </div>

      {c.exportOpen() && (
        <div className="me-dialog-backdrop" onClick={() => c.exportOpen.set(false)}>
          <div className="me-dialog" onClick={e => e.stopPropagation()}>
            <div className="me-dialog-title">Export {c.current()?.name} to {target}</div>
            <p className="lm-muted me-dialog-note">
              {c.exportToCa()
                ? 'Builds a SOCOM 3 / CA package (.ZDB) from this model, then opens it here. CA loading it in game is not confirmed yet.'
                : 'Builds a SOCOM II memory-card pack from this model with the converter confirmed in game, then opens it here.'}
            </p>
            {!c.exportToCa() && (
              <label className="me-field me-field-wide"><span>Pack name</span>
                <input className="me-input" type="text" maxLength={7} placeholder="automatic" value={c.exportPackName()} onChange={e => c.exportPackName.set(e.target.value)} /></label>
            )}
            <label className="me-field me-field-wide"><span>Model name</span>
              <input className="me-input" type="text" maxLength={31} placeholder={c.exportModelPlaceholder()} value={c.exportModelName()} onChange={e => c.exportModelName.set(e.target.value)} /></label>
            <label className="me-field me-field-wide"><span>Folder</span>
              <input className="me-input" type="text" placeholder="Documents/CodeDesigner/Exports" value={c.exportDir()} onChange={e => c.exportDir.set(e.target.value)} /></label>
            {c.current()?.kind === 'character' && (
              <>
                <div className="me-dialog-sub">Animations <span className="lm-muted">({c.exportClips().size} selected; {c.exportToCa() ? 'written as a CA clip bank' : 'retargeted onto the SOCOM II skeleton'})</span></div>
                <div className="me-dialog-row">
                  <input className="me-search" type="text" placeholder="Filter clips…" value={c.exportClipQuery()} onChange={e => c.exportClipQuery.set(e.target.value)} />
                  <button className="lm-tool-btn" onClick={() => c.selectShownClips(true)}>All shown</button>
                  <button className="lm-tool-btn" onClick={() => c.selectShownClips(false)}>None</button>
                </div>
                <div className="me-dialog-list">
                  {c.shownExportClips().map(clip => (
                    <label key={clip} className="me-toggle"><input type="checkbox" checked={c.exportClips().has(clip)} onChange={() => c.toggleExportClip(clip)} /> {clip}</label>
                  ))}
                  {!c.shownExportClips().length && <div className="lm-muted">No clips match.</div>}
                </div>
              </>
            )}
            <div className="me-dialog-actions">
              <button className="lm-tool-btn" disabled={c.busy()} onClick={() => void c.runExport()}>{c.busy() ? 'Exporting…' : 'Export and open'}</button>
              <button className="lm-tool-btn" onClick={() => c.exportOpen.set(false)}>Cancel</button>
            </div>
          </div>
        </div>
      )}

      {/* Header toolbar */}
      <div className="me-header">
        <div className="me-seg" title="Selection mode (1-5)">
          {c.selectModes.map(mode => (
            <button key={mode.id} className={c.selectMode() === mode.id ? 'on' : ''} onClick={() => c.setSelectMode(mode.id)} title={`${mode.label} (${mode.key})`}>{mode.label}</button>
          ))}
        </div>
        <div className="me-seg" title="Shading (Z cycles)">
          {c.shadings.map(sh => (
            <button key={sh.id} className={c.shading() === sh.id ? 'on' : ''} onClick={() => c.setShading(sh.id)}>{sh.label}</button>
          ))}
        </div>
        <label className="me-toggle"><input type="checkbox" checked={c.baked()} onChange={e => c.setBaked(e.target.checked)} /> Baked lighting</label>
        <label className="me-toggle"><input type="checkbox" checked={c.grid()} onChange={e => { c.grid.set(e.target.checked); c.applyHelpers(); }} /> Grid</label>
        <label className="me-toggle"><input type="checkbox" checked={c.normals()} onChange={e => { c.normals.set(e.target.checked); c.applyHelpers(); }} /> Normals</label>
        <label className="me-toggle"><input type="checkbox" checked={c.snap()} onChange={e => { c.snap.set(e.target.checked); c.applySnap(); }} /> Snap</label>
        {c.snap() && (
          <>
            <NumInput small min="0.001" step="0.5" value={c.snapMove()} title="Move snap (units)" onCommit={v => { c.snapMove.set(+v || 1); c.applySnap(); }} />
            <NumInput small min="1" step="5" value={c.snapRotate()} title="Rotate snap (degrees)" onCommit={v => { c.snapRotate.set(+v || 15); c.applySnap(); }} />
          </>
        )}
        <span className="me-spacer" />
        <div className="me-seg" title="View">
          <button onClick={() => c.view('front')} title="Front (Numpad 1)">Front</button>
          <button onClick={() => c.view('back')} title="Back (Ctrl+Numpad 1)">Back</button>
          <button onClick={() => c.view('right')} title="Right (Numpad 3)">Right</button>
          <button onClick={() => c.view('top')} title="Top (Numpad 7)">Top</button>
          <button onClick={() => c.view('iso')} title="Iso">Iso</button>
          <button className={c.ortho() ? 'on' : ''} onClick={() => c.toggleOrtho()} title="Orthographic (Numpad 5)">Ortho</button>
          <button onClick={() => c.frameSelected()} title="Frame selected (F)">Frame</button>
        </div>
      </div>

      <div className={`me-body${c.showAssets() ? '' : ' no-assets'}`}>
        {/* Asset browser */}
        {c.showAssets() && (
          <aside className="me-assets">
            <div className="me-panel-title">Assets <span className="me-spacer" /><button className="lm-row-btn" disabled={!c.canImport()} onClick={() => void c.importFromPackage()} title="Import a model from another package">Import…</button><button className="lm-row-btn" onClick={() => c.showAssets.set(false)} title="Hide">×</button></div>
            <input className="me-search" type="text" placeholder="Filter models…" value={c.query()} onChange={e => c.query.set(e.target.value)} />
            <div className="me-kinds">
              <button className={`me-chip${!c.kindFilter() ? ' active' : ''}`} onClick={() => c.kindFilter.set('')}>All</button>
              {c.kinds().map(k => (
                <button key={k} className={`me-chip${c.kindFilter() === k ? ' active' : ''}`} onClick={() => c.kindFilter.set(k)}>{c.kindLabel(k)}</button>
              ))}
            </div>
            <div className="me-list">
              {!s && <div className="me-empty">File › Open package… to load a .ZDB, a .CDB pack, a loose .ZED, or a GTA: San Andreas .ISO / .IMG.</div>}
              {c.groups().map(group => (
                <div key={group.kind}>
                  <div className="me-group-title">{c.kindLabel(group.kind)} <span className="lm-muted">{group.models.length}</span></div>
                  {group.models.map(model => (
                    <button key={model.archive + ':' + model.name} type="button" className={`me-row${c.isCurrent(model) ? ' active' : ''}`} onClick={() => void c.select(model)} title={model.archive}>
                      <span className="me-row-name">{model.name}</span>
                      <span className="me-row-meta">{model.visualCount} vis</span>
                    </button>
                  ))}
                </div>
              ))}
            </div>
          </aside>
        )}

        {/* Tool shelf */}
        <nav className="me-tools">
          {!c.showAssets() && <button className="me-tool" onClick={() => c.showAssets.set(true)} title="Show assets"><Icon name="library" size={16} /></button>}
          {c.tools.map(t => (
            <button key={t.id} className={`me-tool${c.tool() === t.id ? ' on' : ''}`} onClick={() => c.setTool(t.id)} title={`${t.label} (${t.key})`}>
              <span className="me-tool-glyph">{t.glyph}</span>
            </button>
          ))}
        </nav>

        {/* Viewport + bottom panel */}
        <section className="me-center">
          <div className="me-viewport">
            <canvas ref={canvasRef} tabIndex={0} />
            <div className="me-vp-label">
              <div>{c.viewLabel()} · {c.selectModeLabel()} mode · {c.toolLabel()}</div>
              {m && <div className="lm-muted">{m.name} — {c.meshStats()}</div>}
            </div>
            {c.loading()
              ? <div className="me-vp-center">Loading {c.current()?.name}…</div>
              : !m && s ? <div className="me-vp-center">Pick a model in Assets.</div>
                : !s ? <div className="me-vp-center">File › Open package… (Ctrl+O)</div> : null}
            {c.busy() && <div className="me-vp-busy">Applying…</div>}
          </div>
          <div className={`me-bottom${c.showBottom() ? '' : ' collapsed'}`}>
            <div className="me-bottom-tabs">
              <button className={c.bottomTab() === 'uv' ? 'on' : ''} onClick={() => { c.bottomTab.set('uv'); c.showBottom.set(true); c.refreshUv(); }}>UV Editor</button>
              <button className={c.bottomTab() === 'log' ? 'on' : ''} onClick={() => { c.bottomTab.set('log'); c.showBottom.set(true); }}>Info</button>
              <span className="me-spacer" />
              {c.bottomTab() === 'uv' && c.showBottom() && (
                <>
                  <span className="lm-muted">{c.uvCount()} UV selected</span>
                  <button className="lm-row-btn" onClick={() => c.uvOp('flipU')} title="Mirror selected UVs horizontally">Flip U</button>
                  <button className="lm-row-btn" onClick={() => c.uvOp('flipV')} title="Mirror selected UVs vertically">Flip V</button>
                  <button className="lm-row-btn" onClick={() => c.uvOp('rot90')} title="Rotate selected UVs 90°">Rotate 90°</button>
                  <label className="me-toggle"><input type="checkbox" onChange={e => c.uvSnap(e.target.checked)} /> Pixel snap</label>
                  <button className="lm-row-btn" onClick={() => c.uvFrame()}>Frame</button>
                </>
              )}
              <button className="lm-row-btn" onClick={() => c.showBottom.set(!c.showBottom())}>{c.showBottom() ? '▾' : '▴'}</button>
            </div>
            <div className="me-bottom-body" hidden={!c.showBottom()}>
              <div className="me-uv" hidden={c.bottomTab() !== 'uv'}>
                <canvas ref={uvCanvasRef} />
                {!c.uvChunks().length && <div className="me-vp-center">Select chunks, faces or vertices to edit their UVs.</div>}
              </div>
              <div className="me-log" hidden={c.bottomTab() !== 'log'}>
                {c.logLines().map((line, i) => <div key={i} className={line.error ? 'err' : ''}>{line.text}</div>)}
              </div>
            </div>
          </div>
        </section>

        {/* Outliner + properties */}
        <aside className="me-side">
          <div className="me-outliner">
            <div className="me-panel-title">Outliner
              {target && <button className="lm-row-btn" disabled={c.busy()} onClick={() => void c.quickExport()} title={`Export ${c.current()?.name ?? ''} to ${target}`}>Export to {target}…</button>}
            </div>
            {m ? (
              <div className="me-tree">
                <div className="me-tree-row me-tree-root" onClick={() => c.selectAllChunks()}><Icon name="database" size={12} /> {m.name}</div>
                {c.visuals().map(v => (
                  <div key={v.name}>
                    <div className={`me-tree-row me-tree-visual${c.visualSelected(v.name) ? ' sel' : ''}`}>
                      <button className="me-eye" onClick={() => c.toggleVisualHidden(v.name)} title={v.hidden ? 'Show' : 'Hide'}>{v.hidden ? '◌' : '●'}</button>
                      <span onClick={e => c.selectVisual(v.name, e.nativeEvent)}>{v.name}</span>
                      <span className="lm-muted">{v.chunks.length}</span>
                    </div>
                    {v.chunks.map(ch => (
                      <div key={ch.index} className={`me-tree-row me-tree-chunk${c.chunkSelected(ch.index) ? ' sel' : ''}`}>
                        <button className="me-eye" onClick={() => c.toggleChunkHidden(ch.index)} title={c.isHidden(ch.index) ? 'Show' : 'Hide'}>{c.isHidden(ch.index) ? '◌' : '●'}</button>
                        <span onClick={e => c.selectChunk(ch.index, e.nativeEvent)}>#{ch.index} {ch.texture ?? '(no texture)'}</span>
                        <span className="lm-muted">{ch.vertexCount}v</span>
                      </div>
                    ))}
                  </div>
                ))}
              </div>
            ) : <div className="me-empty">No model.</div>}
          </div>

          <div className="me-props">
            <div className="me-tabs">
              {c.propTabs.map(t => (
                <button key={t.id} className={`me-tab${c.propTab() === t.id ? ' active' : ''}`} onClick={() => c.propTab.set(t.id)} title={t.label}>{t.label}</button>
              ))}
            </div>
            <div className="me-tab-body">
              {c.propTab() === 'item' && (
                <>
                  <div className="me-card">
                    <div className="me-card-title">Selection</div>
                    <div className="me-kv2"><span>Chunks</span><b>{counts.chunks}</b><span>Vertices</span><b>{counts.vertices}</b><span>Faces</span><b>{counts.faces}</b></div>
                  </div>
                  <div className="me-card">
                    <div className="me-card-title">Transform</div>
                    <div className="me-xform">
                      <span>Location</span>
                      {[0, 1, 2].map(k => (
                        <NumInput key={k} step="1" value={c.fmt(c.location()[k])} disabled={!counts.vertices} onCommit={v => c.setLocation(k, v)} />
                      ))}
                      <span>Rotate °</span>
                      {[0, 1, 2].map(k => (
                        <input key={k} className="me-num" type="number" step="5" value={c.pendingRot()[k]} disabled={!counts.vertices} onChange={e => c.setPending('rot', k, e.target.value)} />
                      ))}
                      <span>Scale</span>
                      {[0, 1, 2].map(k => (
                        <input key={k} className="me-num" type="number" step="0.1" value={c.pendingScale()[k]} disabled={!counts.vertices} onChange={e => c.setPending('scale', k, e.target.value)} />
                      ))}
                    </div>
                    <div className="me-actions">
                      <button className="lm-tool-btn" disabled={!counts.vertices || !c.pendingActive()} onClick={() => void c.applyPending()}>Apply</button>
                      <button className="lm-tool-btn" disabled={!c.pendingActive()} onClick={() => c.resetPending()}>Reset</button>
                    </div>
                    <div className="lm-muted me-note">X right, Y up, Z toward the front view; pivot = selection centre.</div>
                  </div>
                  <div className="me-card">
                    <div className="me-card-title">Geometry</div>
                    <label className="me-field me-field-wide">Visual
                      <select value={c.targetVisual()} onChange={e => c.targetVisual.set(e.target.value)}>
                        {c.visuals().map(v => <option key={v.name} value={v.name}>{v.name}</option>)}
                      </select>
                    </label>
                    <label className="me-field me-field-wide">Texture
                      <input className="me-input" type="text" list="me-tex-list2" value={c.targetTexture()} onChange={e => c.targetTexture.set(e.target.value)} />
                    </label>
                    <datalist id="me-tex-list2">
                      {(d?.textures ?? []).map(t => <option key={t.name} value={t.name} />)}
                    </datalist>
                    <label className="me-field me-field-wide">Size
                      <NumInput min="0.1" step="1" value={c.primSize()} onCommit={v => c.primSize.set(+v || 10)} />
                    </label>
                    <div className="me-actions">
                      <button className="lm-tool-btn" disabled={c.busy() || !c.canAdd()} onClick={() => void c.addPrimitive('plane')}>Add plane</button>
                      <button className="lm-tool-btn" disabled={c.busy() || !c.canAdd()} onClick={() => void c.addPrimitive('cube')}>Add cube</button>
                      <button className="lm-tool-btn" disabled={c.busy() || !c.canAdd()} onClick={() => void c.importObj()}>Import OBJ…</button>
                    </div>
                    <div className="me-actions">
                      <button className="lm-tool-btn" disabled={c.busy() || !counts.vertices} onClick={() => void c.duplicateSelection()}>Duplicate (Shift+D)</button>
                      <button className="lm-tool-btn lm-danger-btn" disabled={c.busy() || !counts.vertices} onClick={() => void c.deleteSelection()}>Delete faces (X)</button>
                    </div>
                    <div className="lm-muted me-note">New geometry goes into the visual above with the texture above, at the selection centre (or the origin). Delete removes the selected faces (vertex mode: faces whose three vertices are selected; chunk mode: whole chunks).</div>
                  </div>
                </>
              )}
              {c.propTab() === 'material' && (
                c.selectedChunkInfos().length ? (
                  <>
                    {c.selectedChunkInfos().slice(0, 6).map(ch => (
                      <div key={ch.index} className="me-card">
                        <div className="me-card-title">Chunk #{ch.index}</div>
                        <dl className="me-kv">
                          <dt>Visual</dt><dd className="lm-mono">{ch.visual}</dd>
                          <dt>Texture</dt><dd className="lm-mono">{ch.texture ?? '—'}</dd>
                          <dt>Blend</dt><dd>{ch.materialBlend === null ? '—' : ch.materialBlend ? 'alpha blend (0x44)' : 'copy + alpha test'}</dd>
                          <dt>Vertices</dt><dd>{ch.vertexCount} · {ch.triangles.length / 3} tris</dd>
                          <dt>Colours</dt><dd>{ch.colors ? 'baked vertex colours' : 'none'}</dd>
                          <dt>Units</dt><dd>{ch.unit}</dd>
                        </dl>
                      </div>
                    ))}
                    <div className="me-card">
                      <div className="me-card-title">Retexture selected sections</div>
                      <input className="me-input" type="text" list="me-tex-list" placeholder="texture.tif" value={c.retexTo()} onChange={e => c.retexTo.set(e.target.value)} />
                      <datalist id="me-tex-list">
                        {(d?.textures ?? []).map(t => <option key={t.name} value={t.name} />)}
                      </datalist>
                      <button className="lm-tool-btn" disabled={c.busy() || !c.retexTo()} onClick={() => void c.retexture()}>Apply</button>
                      <div className="lm-muted me-note">Points every section using the selected chunks' texture at the new .tif (it must exist in the package).</div>
                    </div>
                  </>
                ) : <div className="me-empty">Select chunks to see their material.</div>
              )}
              {c.propTab() === 'texture' && (
                <div className="me-card">
                  <div className="me-card-title">Add textures</div>
                  <div className="me-actions">
                    <button className="lm-tool-btn" disabled={c.busy() || !c.canImport() || !c.current()} onClick={() => void c.newTextureFromImage()} title="An 8-bit texture with its own palette, from a PNG / JPG / BMP / WebP">New from image…</button>
                    <button className="lm-tool-btn" disabled={c.busy() || !c.canImport() || !c.current()} onClick={() => void c.importTextureFromPackage()} title="Copy a texture (with its palette) from another package">Import from package…</button>
                    <button className="lm-tool-btn" disabled={c.busy() || !c.canImport() || !c.current()} onClick={() => void c.newPaletteFromImage()} title="A 256-colour palette made from an image's colours">New palette from image…</button>
                  </div>
                  <div className="lm-muted me-note">New and imported textures go on the selected sections. A new texture gets its own palette, made from the image, under an id no palette here uses.</div>
                </div>
              )}
              {c.propTab() === 'texture' && (
                c.selectedTextures().length ? c.selectedTextures().map(t => (
                  <div key={t.name} className="me-card">
                    <div className="me-tex-head">
                      {c.thumbs()[t.name] && <img className="me-thumb" src={c.thumbs()[t.name]} alt={t.name} />}
                      <div>
                        <div className="lm-mono">{t.name}</div>
                        <div className="lm-muted">{c.texSummary(t)}</div>
                      </div>
                    </div>
                    <div className="me-actions">
                      <button className="lm-tool-btn" disabled={c.busy() || !t.found} onClick={() => void c.replaceTexture(t)}>Replace from image…</button>
                      <button className="lm-tool-btn" disabled={!t.found} onClick={() => void c.exportTexture(t)}>Export PNG…</button>
                    </div>
                  </div>
                )) : <div className="me-empty">Select chunks to see their textures.</div>
              )}
              {c.propTab() === 'paint' && (
                <div className="me-card">
                  <div className="me-card-title">Vertex paint (baked lighting)</div>
                  <label className="me-field">Colour <input type="color" value={c.brushHex()} onChange={e => c.setBrushColor(e.target.value)} /></label>
                  <label className="me-field">Intensity <input type="range" min="0" max="2" step="0.05" value={c.brushIntensity()} onChange={e => c.setBrush('intensity', e.target.value)} /> <span>{c.brushIntensity()}</span></label>
                  <label className="me-field">Radius <input type="range" min="0.5" max={c.maxRadius()} step="0.5" value={c.brushRadius()} onChange={e => c.setBrush('radius', e.target.value)} /> <span>{c.brushRadius()}</span></label>
                  <label className="me-field">Strength <input type="range" min="0.05" max="1" step="0.05" value={c.brushStrength()} onChange={e => c.setBrush('strength', e.target.value)} /> <span>{c.brushStrength()}</span></label>
                  <div className="me-actions">
                    <button className="lm-tool-btn" onClick={() => c.setTool('paint')}>Paint tool (P)</button>
                    <button className="lm-tool-btn" disabled={!counts.vertices || c.busy()} onClick={() => void c.fillSelection()}>Fill selection</button>
                    <button className="lm-tool-btn" disabled={!counts.vertices || c.busy()} onClick={() => void c.resetColors()}>Reset to neutral</button>
                  </div>
                  <div className="lm-muted me-note">White at intensity 1 = the texture as-is (GS 0x80). Painting limits itself to the selected chunks when any are selected.</div>
                </div>
              )}
              {c.propTab() === 'model' && (
                <>
                  {d && (
                    <>
                      <div className="me-card">
                        <div className="me-card-title">Model</div>
                        <dl className="me-kv">
                          <dt>Name</dt><dd className="lm-mono">{d.name}</dd>
                          <dt>Kind</dt><dd>{d.kind}</dd>
                          <dt>Archive</dt><dd className="lm-mono">{d.archive}</dd>
                          <dt>Visuals</dt><dd>{d.visuals.length}</dd>
                          <dt>Textures</dt><dd>{d.textures.length} ({d.textureNamespace ?? '?'})</dd>
                          {m?.skeleton && <><dt>Skeleton</dt><dd>{m.skeleton.length} bones, skinned (geometry is view-only for now)</dd></>}
                          {d.geo && <><dt>GEO</dt><dd className="lm-mono">{d.geoArchive} › {d.geo.name}</dd><dt>Units</dt><dd>{d.geo.positionUnit} {d.geo.vparamsFlags}</dd></>}
                        </dl>
                      </div>
                      {c.missingTextures().length > 0 && <div className="me-card me-warn">Missing textures: {c.missingTextures().join(', ')}</div>}
                    </>
                  )}
                  <div className="me-card">
                    <div className="me-card-title">Session</div>
                    <div className="me-note">The Map Builder and Animation Editor share session <span className="lm-mono">{s?.session}</span>; their edits appear here.</div>
                  </div>
                </>
              )}
            </div>
          </div>
        </aside>
      </div>

      {/* Status bar */}
      <div className={`me-statusbar${c.messageIsError() ? ' err' : ''}`}>
        <span>{c.message() || c.hint()}</span>
        <span className="me-spacer" />
        {h && <span className="lm-muted">#{h.chunk.index} {h.chunk.texture}{h.vertex !== null ? ' · v' + h.vertex : ''} · {c.fmtVec(h.point)}</span>}
        <span>{counts.chunks} chunk(s) · {counts.vertices} vert(s) · {counts.faces} face(s)</span>
      </div>
    </div>
  );
}

// A number input committed on blur / Enter, like Angular's (change) binding.
function NumInput({ value, onCommit, step, min, disabled, title, small }: { value: string | number; onCommit: (v: string) => void; step?: string; min?: string; disabled?: boolean; title?: string; small?: boolean }) {
  const [text, setText] = useState(String(value));
  useEffect(() => setText(String(value)), [value]);
  return (
    <input className={`me-num${small ? ' me-num-sm' : ''}`} type="number" step={step} min={min} disabled={disabled} title={title} value={text}
      onChange={e => setText(e.target.value)}
      onBlur={() => { if (text !== String(value)) { onCommit(text); } }}
      onKeyDown={e => { if (e.key === 'Enter') { (e.target as HTMLInputElement).blur(); } }} />
  );
}
