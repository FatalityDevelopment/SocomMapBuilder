// The Asset Viewer's editors for structured package data: palettes (256 colours), clutter placements, GEO scene
// nodes and ZANIM effect scripts. Palettes and clutter change through the package session (the editors' ops);
// GEO and ZANIM records through VIEWER_EDIT. Every change is one undoable edit, saved with Edit › Save changes.

import { useEffect, useMemo, useState, type KeyboardEvent, type ReactNode } from 'react';
import type { AssetViewerLogic } from './asset-viewer-page';
import type { ClutterPlacement, GeoNode, Preview, ZanimEffect, ZanimField, ZanimInstruction } from './viewer-client';

type C = AssetViewerLogic;

// Six significant digits keep fields short; a field left as shown keeps its exact value when applied.
const short = (v: number) => String(Number(v.toPrecision(6)));
const num = (v: string) => Number(v.trim());

/** Exact values where the field still shows the original, parsed text elsewhere; null when one is not a number. */
function merged(drafts: string[], shown: string[], exact: number[]): number[] | null {
  const out = drafts.map((d, i) => d === shown[i] ? exact[i] : num(d));
  return out.every(Number.isFinite) ? out : null;
}

/** A text field that applies on Enter or when left (if changed); Escape puts the value back. */
function Commit({ value, onCommit, disabled, className, title, placeholder }: { value: string; onCommit: (text: string) => void; disabled?: boolean; className?: string; title?: string; placeholder?: string }) {
  const [draft, setDraft] = useState(value);
  useEffect(() => setDraft(value), [value]);
  const done = () => { if (draft !== value) { onCommit(draft); } };
  return (
    <input className={`me-input ${className ?? ''}`} value={draft} disabled={disabled} title={title} placeholder={placeholder}
      onChange={e => setDraft(e.target.value)} onBlur={done}
      onKeyDown={(e: KeyboardEvent<HTMLInputElement>) => { if (e.key === 'Enter') { done(); } else if (e.key === 'Escape') { setDraft(value); } e.stopPropagation(); }} />
  );
}

function ApplyRevert({ show, onApply, onRevert }: { show: boolean; onApply: () => void; onRevert: () => void }) {
  return show ? <><button className="lm-tool-btn av-primary" onClick={onApply}>Apply</button> <button className="lm-row-btn" onClick={onRevert}>Revert</button></> : null;
}

// ------------------------------------------------------------------ palettes

const hex2 = (v: number) => v.toString(16).padStart(2, '0');

export function PaletteEditor({ c, p }: { c: C; p: Extract<Preview, { type: 'palette' }> }) {
  const editable = c.canEditPalette() && !c.editBusy();
  const [sel, setSel] = useState(0);
  const at = p.rgba.slice(sel * 4, sel * 4 + 4);
  const colour = `#${at.slice(0, 3).map(hex2).join('')}`;
  const [d, setD] = useState({ colour, alpha: String(at[3]) });
  useEffect(() => setD({ colour, alpha: String(at[3]) }), [colour, at[3], sel]);
  const changed = d.colour.toLowerCase() !== colour || d.alpha !== String(at[3]);
  const apply = () => {
    const m = /^#?([0-9a-f]{6})$/i.exec(d.colour.trim());
    const a = Math.round(num(d.alpha));
    if (!m || !Number.isFinite(a) || a < 0 || a > 255) { c.say('A colour is #rrggbb and alpha 0-255', true); return; }
    const next = [...p.rgba];
    next.splice(sel * 4, 4, parseInt(m[1].slice(0, 2), 16), parseInt(m[1].slice(2, 4), 16), parseInt(m[1].slice(4, 6), 16), a);
    void c.setPalette(next, `Colour #${sel}`);
  };
  return (
    <div className="av-pal-edit">
      <div className="av-palette av-palette-edit">
        {Array.from({ length: 256 }, (_, i) => {
          const [r, g, b, a] = p.rgba.slice(i * 4, i * 4 + 4);
          return <span key={i} className={i === sel ? 'on' : ''} title={`#${i}: ${r}, ${g}, ${b}, alpha ${a}`} style={{ background: `rgba(${r}, ${g}, ${b}, ${a / 255})` }} onClick={() => setSel(i)} />;
        })}
      </div>
      <div className="av-form av-pal-form">
        <div className="me-card-title">Colour #{sel}</div>
        <label className="me-field me-field-wide"><span>Colour</span>
          <span className="av-pal-pick">
            <input type="color" value={/^#[0-9a-f]{6}$/i.test(d.colour) ? d.colour : colour} disabled={!editable} onChange={e => setD({ ...d, colour: e.target.value })} />
            <input className="me-input" value={d.colour} disabled={!editable} onChange={e => setD({ ...d, colour: e.target.value })} />
          </span>
        </label>
        <label className="me-field me-field-wide"><span>Alpha</span><input className="me-input" value={d.alpha} disabled={!editable} onChange={e => setD({ ...d, alpha: e.target.value })} /></label>
        {editable && <div className="av-actions"><ApplyRevert show={changed} onApply={apply} onRevert={() => setD({ colour, alpha: String(at[3]) })} /></div>}
        <div className="lm-muted me-note">
          {p.is16 ? '16-bit palette: each channel keeps 5 bits and alpha is on (128 and up) or off.' : '32-bit palette: alpha 255 is opaque (stored as the PS2\'s 0x80).'}
        </div>
        {editable && <button className="lm-tool-btn" onClick={() => void c.paletteFromImage()}>Load colours from image…</button>}
        <div className="lm-muted me-note">An image gives its first 256 pixels row by row: a 16 × 16 image maps one pixel to each colour.</div>
      </div>
    </div>
  );
}

// ------------------------------------------------------------------ clutter

const PAGE = 300;

export function ClutterEditor({ c, p }: { c: C; p: Extract<Preview, { type: 'clutter' }> }) {
  const editable = !!p.session && !c.editBusy();
  const [model, setModel] = useState('');
  const [more, setMore] = useState(false);
  const counts = useMemo(() => {
    const m = new Map<string, number>();
    for (const pl of p.placements) { m.set(pl.model, (m.get(pl.model) ?? 0) + 1); }
    return m;
  }, [p.placements]);
  const rows = p.placements.map((pl, index) => ({ pl, index })).filter(r => !model || r.pl.model === model);
  const shown = more ? rows : rows.slice(0, PAGE);
  return (
    <div className="av-scroll">
      <div className="av-image-bar av-clutter-bar">
        <select className="me-input" value={model} onChange={e => { setModel(e.target.value); setMore(false); }}>
          <option value="">All models ({p.placements.length})</option>
          {[...counts.entries()].sort((a, b) => a[0].localeCompare(b[0])).map(([m, n]) => <option key={m} value={m}>{m} ({n})</option>)}
        </select>
        {editable && <AddClutter c={c} models={p.models} model={model} near={rows[0]?.pl.position} />}
      </div>
      {!p.session && <div className="lm-muted me-note">Only a package's own CLUTTER.ZAR is edited here.</div>}
      <table className="av-table av-edit-table">
        <thead><tr><th>#</th><th>Model</th><th>Position X Y Z</th><th>Yaw°</th><th>Scale</th><th /></tr></thead>
        <tbody>{shown.map(r => <ClutterRow key={r.index} c={c} index={r.index} pl={r.pl} editable={editable} />)}</tbody>
      </table>
      {!more && rows.length > PAGE && <button className="lm-row-btn av-more" onClick={() => setMore(true)}>Show all {rows.length}</button>}
      <div className="lm-muted me-note">Yaw and scale rebuild the placement upright: one tilted (⚠) loses its tilt when they change. Position alone keeps it.</div>
    </div>
  );
}

function ClutterRow({ c, index, pl, editable }: { c: C; index: number; pl: ClutterPlacement; editable: boolean }) {
  const initial = useMemo(() => ({ pos: pl.position.map(short), yaw: short(pl.yaw), scale: short(pl.scale) }), [pl]);
  const [d, setD] = useState(initial);
  useEffect(() => setD(initial), [initial]);
  const changed = JSON.stringify(d) !== JSON.stringify(initial);
  const apply = () => {
    const pos = merged(d.pos, initial.pos, pl.position);
    if (!pos) { c.say('Position is three numbers', true); return; }
    if (d.yaw === initial.yaw && d.scale === initial.scale) { void c.moveClutter(index, pos); return; }
    const yaw = d.yaw === initial.yaw ? pl.yaw : num(d.yaw);
    const scale = d.scale === initial.scale ? pl.scale : num(d.scale);
    if (!Number.isFinite(yaw) || !(scale > 0)) { c.say('Yaw is a number and scale above 0', true); return; }
    void c.moveClutter(index, pos, { yaw, scale });
  };
  return (
    <tr>
      <td className="lm-muted">{index}</td>
      <td title={`${pl.layout} layout`}>{pl.model}{pl.tilted && <span className="av-warn" title="Tilted: changing yaw or scale stands it upright"> ⚠</span>}</td>
      <td><div className="av-vec">{d.pos.map((v, j) => <input key={j} className="me-num av-num" value={v} title={String(pl.position[j])} disabled={!editable} onChange={e => setD({ ...d, pos: d.pos.map((x, n) => n === j ? e.target.value : x) })} />)}</div></td>
      <td><input className="me-num av-num" value={d.yaw} disabled={!editable} onChange={e => setD({ ...d, yaw: e.target.value })} /></td>
      <td><input className="me-num av-num" value={d.scale} disabled={!editable} onChange={e => setD({ ...d, scale: e.target.value })} /></td>
      <td className="av-row-actions">
        <ApplyRevert show={editable && changed} onApply={apply} onRevert={() => setD(initial)} />
        {editable && !changed && <button className="lm-row-btn lm-danger-btn" title="Remove this placement" onClick={() => void c.deleteClutter(index)}>✕</button>}
      </td>
    </tr>
  );
}

function AddClutter({ c, models, model, near }: { c: C; models: string[]; model: string; near?: number[] }) {
  const [m, setM] = useState(model || models[0] || '');
  const [pos, setPos] = useState(['0', '0', '0']);
  useEffect(() => { if (model) { setM(model); } }, [model]);
  useEffect(() => { if (near) { setPos(near.map(short)); } }, [near?.join()]);
  const add = () => {
    const v = pos.map(num);
    if (!m || !v.every(Number.isFinite)) { c.say('Pick a model and give a position', true); return; }
    void c.addClutter(m, v);
  };
  return (
    <span className="av-vec av-add">
      <span className="lm-muted">Add</span>
      <select className="me-input" value={m} onChange={e => setM(e.target.value)}>{models.map(x => <option key={x} value={x}>{x}</option>)}</select>
      <span className="lm-muted">at</span>
      {pos.map((v, j) => <input key={j} className="me-num av-num" value={v} onChange={e => setPos(pos.map((x, n) => n === j ? e.target.value : x))} />)}
      <button className="lm-tool-btn" onClick={add}>Add</button>
    </span>
  );
}

// ------------------------------------------------------------------ GEO scene nodes

function walk(nodes: GeoNode[], visit: (n: GeoNode, depth: number) => void, depth = 0): void {
  for (const n of nodes) { visit(n, depth); walk(n.children, visit, depth + 1); }
}

export function GeoEditor({ c, p }: { c: C; p: Extract<Preview, { type: 'geo' }> }) {
  const editable = c.canEditInPlace() && !c.editBusy();
  const [sel, setSel] = useState<number | null>(p.models[0]?.nparams ?? null);
  const [open, setOpen] = useState<ReadonlySet<number>>(new Set());
  const [q, setQ] = useState('');
  const byId = useMemo(() => {
    const m = new Map<number, GeoNode>();
    walk(p.models, n => m.set(n.nparams, n));
    return m;
  }, [p.models]);
  const rows: ReactNode[] = [];
  const query = q.trim().toLowerCase();
  const row = (n: GeoNode, depth: number) => (
    <div key={n.nparams} className={`av-geo-row${n.nparams === sel ? ' active' : ''}`} style={{ paddingLeft: 6 + depth * 14 }} onClick={() => setSel(n.nparams)}>
      <span className="av-rdr-caret" onClick={e => { e.stopPropagation(); const next = new Set(open); if (next.has(n.nparams)) { next.delete(n.nparams); } else { next.add(n.nparams); } setOpen(next); }}>
        {n.children.length ? (open.has(n.nparams) ? '▾' : '▸') : ''}
      </span>
      <span>{n.name}</span>
      {n.modelName && <span className="lm-muted"> → {n.modelName.text}</span>}
      {n.visuals > 0 && <span className="lm-muted"> · {n.visuals} vis</span>}
    </div>
  );
  if (query) {
    walk(p.models, (n, depth) => { if (n.name.toLowerCase().includes(query) || n.modelName?.text.toLowerCase().includes(query)) { rows.push(row(n, Math.min(depth, 1))); } });
  } else {
    const add = (nodes: GeoNode[], depth: number) => { for (const n of nodes) { rows.push(row(n, depth)); if (open.has(n.nparams)) { add(n.children, depth + 1); } } };
    add(p.models, 0);
  }
  const node = sel !== null ? byId.get(sel) : undefined;
  return (
    <div className="av-split">
      <div className="av-split-list">
        <input className="me-input av-split-filter" placeholder="Filter nodes…" value={q} onChange={e => setQ(e.target.value)} />
        <div className="av-scroll av-mono">{rows}</div>
      </div>
      <div className="av-scroll av-form">
        {node ? <GeoNodeForm key={node.nparams} c={c} node={node} editable={editable} /> : <div className="lm-muted">Pick a scene node.</div>}
      </div>
    </div>
  );
}

const AXES: Array<[string, number]> = [['X axis', 0], ['Y axis', 4], ['Z axis', 8], ['Position', 12]];

function GeoNodeForm({ c, node, editable }: { c: C; node: GeoNode; editable: boolean }) {
  const initial = useMemo(() => ({
    matrix: node.matrix.map(short), min: node.min.map(short), max: node.max.map(short),
    kind: String(node.kind), flags: `0x${(node.flags >>> 0).toString(16).toUpperCase().padStart(8, '0')}`, model: node.modelName?.text ?? '',
  }), [node]);
  const [d, setD] = useState(initial);
  useEffect(() => setD(initial), [initial]);
  const changed = JSON.stringify(d) !== JSON.stringify(initial);
  const vec = (k: 'min' | 'max', label: string) => (
    <div className="av-vec"><span className="lm-muted av-vec-label">{label}</span>
      {d[k].map((v, j) => <input key={j} className="me-num av-num" value={v} title={String(node[k][j])} disabled={!editable} onChange={e => setD({ ...d, [k]: d[k].map((x, n) => n === j ? e.target.value : x) })} />)}
    </div>
  );
  const apply = async () => {
    const fields: { matrix?: number[]; min?: number[]; max?: number[]; kind?: number; flags?: number } = {};
    for (const k of ['matrix', 'min', 'max'] as const) {
      if (d[k].join() === initial[k].join()) { continue; }
      const v = merged(d[k], initial[k], node[k]);
      if (!v) { c.say(`${k}: numbers only`, true); return; }
      fields[k] = v;
    }
    if (d.kind !== initial.kind) { fields.kind = Math.round(num(d.kind)); }
    if (d.flags !== initial.flags) {
      const f = Number(d.flags.trim());
      if (!Number.isInteger(f) || f < 0 || f > 0xFFFFFFFF) { c.say('Flags are a number (0x… for hex)', true); return; }
      fields.flags = f;
    }
    if (Object.keys(fields).length && !await c.setGeoNode(node.nparams, fields)) { return; }
    if (node.modelName && d.model !== initial.model) { await c.setModelName(node.modelName.node, d.model.trim()); }
  };
  return (
    <>
      <div className="me-card-title">{node.name}</div>
      <div className="lm-muted me-note">nparams record #{node.nparams}{node.visuals ? ` · ${node.visuals} visual${node.visuals === 1 ? '' : 's'}` : ''} · {node.children.length} child node{node.children.length === 1 ? '' : 's'}</div>
      {node.modelName && (
        <label className="me-field me-field-wide"><span>Places model</span><input className="me-input" value={d.model} disabled={!editable} onChange={e => setD({ ...d, model: e.target.value })} /></label>
      )}
      <div className="me-card-title av-sub">Local transform</div>
      {AXES.map(([label, at]) => (
        <div key={at} className="av-vec"><span className="lm-muted av-vec-label av-vec-label-wide">{label}</span>
          {[0, 1, 2].map(j => <input key={j} className="me-num av-num" value={d.matrix[at + j]} title={String(node.matrix[at + j])} disabled={!editable} onChange={e => setD({ ...d, matrix: d.matrix.map((x, n) => n === at + j ? e.target.value : x) })} />)}
        </div>
      ))}
      <div className="me-card-title av-sub">Bounds</div>
      {vec('min', 'Min')}
      {vec('max', 'Max')}
      <div className="me-card-title av-sub">Binding</div>
      <label className="me-field me-field-wide"><span>Kind</span><input className="me-input" value={d.kind} disabled={!editable} onChange={e => setD({ ...d, kind: e.target.value })} /></label>
      <label className="me-field me-field-wide"><span>Flags</span><input className="me-input" value={d.flags} disabled={!editable} onChange={e => setD({ ...d, flags: e.target.value })} /></label>
      <div className="lm-muted me-note">Kind is 7 on a model's root node and 1 below it; flags are 0x80800051 (+0x40000 with visuals). Zeros leave the model unbound in game.</div>
      {editable && <div className="av-actions"><ApplyRevert show={changed} onApply={() => void apply()} onRevert={() => setD(initial)} /></div>}
    </>
  );
}

// ------------------------------------------------------------------ ZANIM effect scripts

export function ZanimEditor({ c, p }: { c: C; p: Extract<Preview, { type: 'zanim' }> }) {
  const [q, setQ] = useState('');
  const current = c.zanimEffect();
  const query = q.trim().toLowerCase();
  const list = p.effects.map((e, i) => ({ e, i })).filter(x => !query || x.e.name.toLowerCase().includes(query));
  return (
    <div className="av-split">
      <div className="av-split-list">
        <input className="me-input av-split-filter" placeholder={`Filter ${p.effects.length} effects…`} value={q} onChange={e => setQ(e.target.value)} />
        <div className="av-scroll">
          {list.map(({ e, i }) => (
            <button key={i} className={`me-row${i === current ? ' active' : ''}`} onClick={() => void c.showEffect(i)}>
              <span className="me-row-name">{e.name}</span>
              <span className="me-row-meta">{e.instructions}</span>
            </button>
          ))}
        </div>
      </div>
      <div className="av-scroll">
        {p.effect && current !== null
          ? <EffectView c={c} e={p.effect} spawns={p.effects[current]?.spawns ?? []} />
          : <div className="av-center"><span className="lm-muted">Pick an effect to see and change its script.</span></div>}
      </div>
    </div>
  );
}

function EffectView({ c, e, spawns }: { c: C; e: ZanimEffect; spawns: string[] }) {
  const editable = c.canEditInPlace() && !c.editBusy() && e.sequence >= 0;
  return (
    <div className="av-zanim">
      <div className="me-card-title">{e.name} <span className="lm-muted">{e.set}</span></div>
      <div className="lm-muted me-note">
        {e.tracks.length} track{e.tracks.length === 1 ? '' : 's'}{spawns.length ? ` · starts ${spawns.join(', ')}` : ''}.
        {' '}Values apply on Enter. Words are the instruction as stored (hex); the first holds the opcode and size and stays.
      </div>
      {e.tracks.map((t, ti) => (
        <div key={ti} className="av-track">
          <div className="me-card-title av-sub">Track {ti + 1} <span className="lm-muted">on {t.target}</span></div>
          {t.instructions.map(i => <InstructionRow key={i.offset} c={c} e={e} i={i} editable={editable} />)}
        </div>
      ))}
    </div>
  );
}

function InstructionRow({ c, e, i, editable }: { c: C; e: ZanimEffect; i: ZanimInstruction; editable: boolean }) {
  const [words, setWords] = useState(false);
  return (
    <div className={`av-instr${i.condition ? ' cond' : ''}`} style={{ paddingLeft: 6 + i.depth * 16 }}>
      <span className={`av-instr-op${i.condition ? ' lm-muted' : ''}`} title={`0x${i.op.toString(16).toUpperCase()} at +${i.offset}`}>{i.name}</span>
      {i.fields.map(f => <FieldEdit key={f.name} c={c} e={e} i={i} f={f} editable={editable} />)}
      {i.words.length > 1 && <button className="lm-row-btn av-words-btn" title="The instruction's words" onClick={() => setWords(!words)}>{words ? '−' : '+'} words</button>}
      {words && (
        <span className="av-words">
          <span className="av-mono lm-muted">{i.words[0]}</span>
          {i.words.slice(1).map((w, k) => (
            <Commit key={k} className="av-mono av-word" value={w} disabled={!editable}
              onCommit={text => { const v = parseInt(text.trim().replace(/^0x/i, ''), 16); if (!/^(0x)?[0-9a-f]{1,8}$/i.test(text.trim()) || !Number.isFinite(v)) { c.say('A word is up to 8 hex digits', true); return; } void c.setZanimWord(e.sequence, i.offset, k + 1, v >>> 0); }} />
          ))}
        </span>
      )}
    </div>
  );
}

function FieldEdit({ c, e, i, f, editable }: { c: C; e: ZanimEffect; i: ZanimInstruction; f: ZanimField; editable: boolean }) {
  const set = (value: number | number[] | boolean) => void c.setZanimField(e.sequence, i.offset, f.name, value);
  const label = <span className="lm-muted av-field-label">{f.name}</span>;
  if (f.readOnly || !editable) { return <span className="av-field">{label}<span>{f.shown}</span></span>; }
  switch (f.kind) {
    case 'bit':
    case 'bool16':
      return <label className="av-field">{label}<input type="checkbox" checked={f.value as boolean} onChange={ev => set(ev.target.checked)} /></label>;
    case 'object':
    case 'name': {
      const options = f.kind === 'object' ? e.objects : e.names.map((n, k) => ({ value: k, label: n }));
      const v = f.value as number;
      return (
        <span className="av-field">{label}
          <select className="me-input av-field-select" value={v} onChange={ev => set(Number(ev.target.value))}>
            {options.some(o => o.value === v) ? null : <option value={v}>#{v}</option>}
            {options.map(o => <option key={o.value} value={o.value}>{o.label}</option>)}
          </select>
        </span>
      );
    }
    case 'vec3': {
      const v = f.value as number[];
      return (
        <span className="av-field">{label}
          {v.map((x, k) => <Commit key={k} className="me-num av-num" value={short(x)} title={String(x)} onCommit={t => { const n = num(t); if (!Number.isFinite(n)) { c.say(`${f.name}: numbers only`, true); return; } set(v.map((y, m) => m === k ? n : y)); }} />)}
        </span>
      );
    }
    default: {
      const v = f.value as number;
      const whole = f.kind !== 'f32';
      return (
        <span className="av-field">{label}
          <Commit className="me-num av-num" value={whole ? String(v) : short(v)} title={String(v)}
            onCommit={t => { const n = num(t); if (!Number.isFinite(n) || (whole && !Number.isInteger(n))) { c.say(`${f.name}: ${whole ? 'a whole number' : 'a number'}`, true); return; } set(n); }} />
        </span>
      );
    }
  }
}
