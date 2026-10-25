import { useEffect, useRef, useState } from 'react';
import { Icon } from '../../shared/Icon';
import { openRequest, useSignals } from '../../shared';
import { PageHeader } from '../../shared/PageHeader';
import { AnimationEditorPageLogic } from './animation-editor-page';

// The Animation Editor's view: CodeDesigner's Angular template in JSX over the same logic class.
export function AnimationEditorPage() {
  useSignals();
  const [c] = useState(() => new AnimationEditorPageLogic());
  const canvasRef = useRef<HTMLCanvasElement>(null);

  useEffect(() => {
    c.mount(canvasRef.current!);
    return () => c.dispose();
  }, [c]);

  const request = openRequest();
  useEffect(() => {
    if (request?.page === 'animations') { void c.openPackage(request.path); }
  }, [request, c]);

  const s = c.status();
  const cur = c.current();
  const clip = c.clip();
  const w = c.weaponInfo();
  const bone = c.selectedBone();
  const local = c.boneLocal();

  return (
    <div className="me-app has-header" onPointerDown={e => c.closeMenusOutside(e.nativeEvent)}>
      <PageHeader icon="repeat" title="Animation Editor" subtitle="Character clips and weapon firing effects" />
      <div className="me-menubar">
        <span className="me-brand"><Icon name="repeat" size={14} /> Animation Editor</span>
        {c.menus.map(menu => (
          <div key={menu.id} className={`me-menu${c.openMenu() === menu.id ? ' open' : ''}`}>
            <button className="me-menu-btn" onClick={() => c.toggleMenu(menu.id)} onMouseEnter={() => c.hoverMenu(menu.id)}>{menu.label}</button>
            {c.openMenu() === menu.id && (
              <div className="me-menu-pop">
                {menu.items.map((item, i) => item.separator ? <div key={i} className="me-menu-sep" /> : (
                  <button key={i} className="me-menu-item" disabled={item.disabled?.()} onClick={() => c.runMenu(item)}>
                    <span className="me-menu-check">{item.checked?.() ? '✓' : ''}</span>
                    <span className="me-menu-label">{item.label}</span>
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
            {s.game
              ? <span className="me-badge" title="Edits, saves and animations are not available for this game yet">{s.game} · read-only</span>
              : <span className={`me-badge${c.dirtyCount() > 0 ? ' me-badge-dirty' : ''}`}>{c.dirtyCount() ? `${c.dirtyCount()} unsaved` : 'saved'} · rev {s.revision}</span>}
            <span className="me-session" title="Asset session id (shared with the other pages)">{s.session}</span>
          </>
        )}
      </div>

      <div className="me-header">
        <label className="me-toggle">Character
          <select className="me-input" value={c.characterKey()} onChange={e => void c.pickCharacter(e.target.value)}>
            {c.characters().map(ch => <option key={ch.archive + ':' + ch.name} value={ch.archive + ':' + ch.name}>{ch.name}</option>)}
          </select>
        </label>
        <div className="me-seg" title="Shading">
          {c.shadings.map(sh => (
            <button key={sh.id} className={c.shading() === sh.id ? 'on' : ''} onClick={() => c.setShading(sh.id)}>{sh.label}</button>
          ))}
        </div>
        <label className="me-toggle">Weapon
          <select className="me-input" value={c.weaponKey()} onChange={e => void c.pickWeapon(e.target.value)}>
            <option value="">None</option>
            {c.weapons().map(wp => <option key={wp.model} value={wp.model}>{wp.internalName || wp.model}</option>)}
          </select>
        </label>
        <button className="lm-tool-btn" disabled={!c.weaponKey()} onClick={() => c.fire()} title="Fire one shot (Enter): the weapon's FireAnimName effect">Fire</button>
        <label className="me-toggle" title="Fire at the weapon's FireWait rate"><input type="checkbox" checked={c.autoFire()} disabled={!c.weaponKey()} onChange={e => c.setAutoFire(e.target.checked)} /> Auto</label>
        <label className="me-toggle"><input type="checkbox" checked={c.showSkeleton()} onChange={e => c.setSkeleton(e.target.checked)} /> Skeleton</label>
        <label className="me-toggle"><input type="checkbox" checked={c.showMesh()} onChange={e => c.setMesh(e.target.checked)} /> Mesh</label>
        <span className="me-spacer" />
        <div className="me-seg" title="View">
          <button onClick={() => c.view('front')}>Front</button>
          <button onClick={() => c.view('back')}>Back</button>
          <button onClick={() => c.view('right')}>Side</button>
          <button onClick={() => c.view('iso')}>Iso</button>
          <button onClick={() => c.viewport.frameCharacter()} title="Frame (F)">Frame</button>
        </div>
      </div>

      <div className="me-body">
        <aside className="me-assets">
          <div className="me-panel-title">Animations <span className="lm-muted">{c.clips().length}</span></div>
          <input className="me-search" type="text" placeholder="Filter clips…" value={c.query()} onChange={e => c.query.set(e.target.value)} />
          {c.sources().length > 1 ? (
            // Other games' clips come in many source files (GTA: 160+ IFPs): pick one from a list.
            <select className="me-search" title="Source file" value={c.source()} onChange={e => c.source.set(e.target.value)}>
              <option value="">All sources ({c.sources().length})</option>
              {c.sources().map(src => <option key={src.name} value={src.name}>{src.name} ({src.count})</option>)}
            </select>
          ) : (
            <div className="me-kinds">
              {c.prefixes().map(p => (
                <button key={p} className={`me-chip${c.prefix() === p ? ' active' : ''}`} onClick={() => c.prefix.set(c.prefix() === p ? '' : p)}>{p}</button>
              ))}
            </div>
          )}
          <div className="me-list">
            {!s && <div className="me-empty">File › Open package… to load a .ZDB / .CDB, or a GTA: San Andreas .ISO / .IMG.</div>}
            {c.groups().map(g => (
              <div key={g.title}>
                <div className="me-group-title">{g.title} <span className="lm-muted">{g.clips.length}</span></div>
                {g.clips.map(cl => (
                  <button key={cl.source + ':' + cl.name} type="button" className={`me-row${c.isCurrent(cl) ? ' active' : ''}`}
                    onClick={() => void c.selectClip(cl)} onDoubleClick={() => void c.selectClip(cl, true)}
                    title={cl.source + (cl.editable ? '' : ' (read-only: copy into the package to edit)')}>
                    <span className="me-row-name">{cl.name}</span>
                    <span className="me-row-meta">{cl.duration.toFixed(2)}s</span>
                  </button>
                ))}
              </div>
            ))}
          </div>
        </aside>

        <nav className="me-tools">
          {c.tools.map(t => (
            <button key={t.id} className={`me-tool${c.tool() === t.id ? ' on' : ''}`} onClick={() => c.setTool(t.id)} title={`${t.label} (${t.key})`}>
              <span className="me-tool-glyph">{t.glyph}</span>
            </button>
          ))}
        </nav>

        <section className="me-center">
          <div className="me-viewport">
            <canvas ref={canvasRef} tabIndex={0} />
            <div className="me-vp-label">
              <div>{cur?.name ?? 'No clip'}{cur && !cur.editable ? ' · shared (read-only)' : ''}</div>
              {clip && <div className="lm-muted">frame {c.frame()} / {clip.samples - 1} · {c.time().toFixed(2)}s / {clip.duration.toFixed(2)}s · {clip.fps.toFixed(1)} fps</div>}
              {bone && <div className="lm-muted">bone {bone}</div>}
            </div>
            {c.loading()
              ? <div className="me-vp-center">{c.loading()}</div>
              : !s ? <div className="me-vp-center">File › Open package… (Ctrl+O)</div>
                : !c.hasCharacter() ? <div className="me-vp-center">This package has no skinned character to preview on.</div> : null}
            {c.busy() && <div className="me-vp-busy">Applying…</div>}
          </div>
          <div className="an-timeline">
            <button className="lm-tool-btn" onClick={() => c.stepFrame(-1)} title="Previous frame (,)">⏮</button>
            <button className="lm-tool-btn an-play" onClick={() => c.togglePlay()} title={c.playing() ? 'Pause (Space)' : 'Play (Space)'}>{c.playing() ? '⏸' : '▶'}</button>
            <button className="lm-tool-btn" onClick={() => c.stepFrame(1)} title="Next frame (.)">⏭</button>
            <input className="an-scrub" type="range" min="0" max={(clip?.samples ?? 1) - 1} step="1" value={c.frame()} onChange={e => c.scrub(e.target.value)} />
            <span className="lm-muted an-frame">{c.frame()}</span>
            <label className="me-toggle">Speed
              <select className="me-input" value={c.speed()} onChange={e => c.setSpeed(+e.target.value)}>
                {[0.1, 0.25, 0.5, 1, 2].map(sp => <option key={sp} value={sp}>{sp}×</option>)}
              </select>
            </label>
            <label className="me-toggle"><input type="checkbox" checked={c.loop()} onChange={e => c.setLoop(e.target.checked)} /> Loop</label>
          </div>
        </section>

        <aside className="me-side">
          <div className="me-outliner">
            <div className="me-panel-title">Bones <span className="lm-muted">{c.bones().length}</span></div>
            <div className="me-tree">
              {c.bones().map(b => (
                <div key={b.name} className={`me-tree-row${bone === b.name ? ' sel' : ''}`} onClick={() => c.pickBone(b.name)}>
                  <span className={[b.animated ? 'an-animated' : '', b.extra ? 'an-extra' : ''].filter(Boolean).join(' ')}>{b.name}</span>
                  <span className="lm-muted">{b.extra ? 'mount' : b.animated ? 'animated' : b.tracked ? 'static' : 'bind'}</span>
                </div>
              ))}
            </div>
          </div>
          <div className="me-props">
            <div className="me-tab-body">
              {cur && (
                <div className="me-card">
                  <div className="me-card-title">Clip</div>
                  <dl className="me-kv">
                    <dt>Name</dt><dd className="lm-mono">{cur.name}</dd>
                    <dt>Source</dt><dd>{cur.source}{cur.editable ? '' : ' (read-only)'}</dd>
                    <dt>Bones</dt><dd>{cur.bones}</dd>
                  </dl>
                  <div className="me-xform">
                    <span>Duration s</span>
                    <NumInput min="0.03" step="0.1" value={cur.duration.toFixed(3)} disabled={!cur.editable} onCommit={v => void c.setDuration(v)} />
                    <span>Frames</span>
                    <NumInput min="1" step="1" value={cur.frames} disabled={!cur.editable} onCommit={v => void c.setFrames(v)} title="Frame intervals (samples = frames + 1); changing it resamples" />
                  </div>
                  <div className="me-actions">
                    {cur.editable ? (
                      <>
                        <button className="lm-tool-btn" disabled={c.busy()} onClick={() => void c.renameClip()}>Rename…</button>
                        <button className="lm-tool-btn" disabled={c.busy()} onClick={() => void c.duplicateClip()}>Duplicate…</button>
                        <button className="lm-tool-btn lm-danger-btn" disabled={c.busy()} onClick={() => void c.deleteClip()}>Delete</button>
                      </>
                    ) : (
                      <button className="lm-tool-btn" disabled={c.busy() || !c.canWrite()} onClick={() => void c.duplicateClip()}>Copy into package…</button>
                    )}
                  </div>
                </div>
              )}
              {w && (
                <>
                  <div className="me-card">
                    <div className="me-card-title">Weapon {w.name}</div>
                    <dl className="me-kv">
                      <dt>Model</dt><dd className="lm-mono">{w.model}</dd>
                      <dt>Fire FX</dt><dd className="lm-mono">{w.fireAnim || '—'}</dd>
                      <dt>Spawns</dt><dd className="lm-mono">{w.spawns.join(', ') || '—'}</dd>
                      <dt>Flash</dt><dd className="lm-mono">{w.flash ?? '—'}</dd>
                      <dt>Shell</dt><dd className="lm-mono">{w.shell ?? '—'}</dd>
                      <dt>Rate</dt><dd>{w.fireWait} s between shots</dd>
                    </dl>
                    <div className="lm-muted me-note">Mounted on the clip's rifle bone. Fire runs the effect scripts as the game does (flash roll, scale curve, timing, light, sub-effects).{c.skipped().length ? ` Not modelled yet: ${c.skipped().join(', ')}.` : ''}</div>
                  </div>
                  {c.fxTree().map(fxn => (
                    <div key={fxn.name} className="me-card">
                      <div className="me-card-title">Effect {fxn.name}</div>
                      {fxn.nodeRefs?.length ? <div className="lm-muted me-note">objects: {c.refNames(fxn)}</div> : null}
                      {(fxn.tracks ?? []).map((t, ti) => (
                        <div key={ti}>
                          <div className="an-track">{t.target}</div>
                          {t.instructions.map((ins, ii) => (
                            <div key={ii} className={`an-ins${ins.condition ? ' an-cond' : ''}`} style={{ paddingLeft: 8 + ins.depth * 12 }}><b>{ins.name}</b> <span className="lm-muted">{c.fieldText(ins.fields)}</span></div>
                          ))}
                        </div>
                      ))}
                    </div>
                  ))}
                </>
              )}
              {bone ? (
                <div className="me-card">
                  <div className="me-card-title">Bone {bone} · frame {c.frame()}</div>
                  {local && (
                    <div className="me-xform">
                      <span>Position</span>
                      {[0, 1, 2].map(k => <NumInput key={k} step="0.1" value={c.fmt(local.position[k])} disabled={!c.editable()} onCommit={v => void c.setBoneValue('position', k, v)} />)}
                      <span>Rotate °</span>
                      {[0, 1, 2].map(k => <NumInput key={k} step="5" value={c.fmt(c.eulerOf(local.rotation)[k])} disabled={!c.editable()} onCommit={v => void c.setBoneValue('rotation', k, v)} />)}
                    </div>
                  )}
                  <div className="me-actions">
                    <button className="lm-tool-btn" disabled={!c.editable()} onClick={() => c.setTool('rotate')}>Rotate (R)</button>
                    <button className="lm-tool-btn" disabled={!c.editable()} onClick={() => c.setTool('move')}>Move (G)</button>
                    <button className="lm-tool-btn" disabled={!c.editable() || c.busy()} onClick={() => void c.holdPose()} title="Use this frame's pose for every frame of the bone">Hold for all frames</button>
                  </div>
                  <div className="lm-muted me-note">Gizmo drags and field edits key this bone at the current frame (a static bone becomes animated). Local to the parent bone; X along the limb.</div>
                </div>
              ) : (
                <div className="me-empty">Click a joint (or a bone in the list) to key it. Shared clips are read-only: copy one into the package first.</div>
              )}
              <div className="me-card">
                <div className="me-card-title">Session</div>
                <div className="me-note">The Map Builder and Model Editor share session <span className="lm-mono">{s?.session}</span>; their edits appear here.</div>
              </div>
            </div>
          </div>
        </aside>
      </div>

      <div className={`me-statusbar${c.messageIsError() ? ' err' : ''}`}>
        <span>{c.message()}</span>
        <span className="me-spacer" />
        {c.hover() && <span className="lm-muted">{c.hover()}</span>}
        <span>{c.characterKey()}</span>
      </div>
    </div>
  );
}

// A number input committed on blur / Enter, like Angular's (change) binding.
function NumInput({ value, onCommit, step, min, disabled, title }: { value: string | number; onCommit: (v: string) => void; step?: string; min?: string; disabled?: boolean; title?: string }) {
  const [text, setText] = useState(String(value));
  useEffect(() => setText(String(value)), [value]);
  return (
    <input className="me-num" type="number" step={step} min={min} disabled={disabled} title={title} value={text}
      onChange={e => setText(e.target.value)}
      onBlur={() => { if (text !== String(value)) { onCommit(text); } }}
      onKeyDown={e => { if (e.key === 'Enter') { (e.target as HTMLInputElement).blur(); } }} />
  );
}
