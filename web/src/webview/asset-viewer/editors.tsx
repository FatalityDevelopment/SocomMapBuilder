// The Asset Viewer's in-place editors: an RDR tree with editable values and list items, AI map and point tables,
// a text editor, a byte patcher and the icon.sys form. Each change is one VIEWER_EDIT (or ASSET_EDIT for points),
// so it has the session's undo / redo and is written by Edit › Save changes.

import { createContext, useContext, useEffect, useMemo, useState, type KeyboardEvent } from 'react';
import type { AssetViewerLogic } from './asset-viewer-page';
import { RDR_ROOT, type MpsPoint, type Preview, type RdrTree } from './viewer-client';

type C = AssetViewerLogic;

/** A value shown as text that becomes an input on click; Enter or leaving the field applies a change, Escape drops it. */
function EditableValue({ value, editable, onCommit, className, title }: { value: string; editable: boolean; onCommit: (text: string) => void; className?: string; title?: string }) {
  const [draft, setDraft] = useState<string | null>(null);
  if (!editable || draft === null) {
    return <span className={`${className ?? ''}${editable ? ' av-editable' : ''}`} title={editable ? `${title ?? ''}Click to edit` : title} onClick={editable ? e => { e.stopPropagation(); setDraft(value); } : undefined}>{value}</span>;
  }
  const done = (apply: boolean) => { if (apply && draft !== value) { onCommit(draft); } setDraft(null); };
  return (
    <input className="me-input av-inline" autoFocus value={draft} onClick={e => e.stopPropagation()} onChange={e => setDraft(e.target.value)}
      onBlur={() => done(true)} onKeyDown={(e: KeyboardEvent) => { if (e.key === 'Enter') { done(true); } else if (e.key === 'Escape') { done(false); } e.stopPropagation(); }} />
  );
}

// ------------------------------------------------------------------ RDR

const isField = (n: RdrTree): n is [number, 'list', number, RdrTree[]] =>
  n[1] === 'list' && (n as [number, 'list', number, RdrTree[]])[3].length === 2 && (n as [number, 'list', number, RdrTree[]])[3][0][1] === 'string';

const shown = (n: RdrTree) => n[1] === 'string' ? String(n[2]) : n[1] === 'float' ? String(Number(n[2])) : String(n[2]);

const settable = (n: RdrTree) => n[1] === 'int' || n[1] === 'float' || n[1] === 'string';

// How many places use each atom: retail files point many lists at one shared block, so one change shows in each.
const Uses = createContext<ReadonlyMap<number, number>>(new Map());

function countUses(tree: RdrTree): Map<number, number> {
  const uses = new Map<number, number>();
  const stack: RdrTree[] = [tree];
  while (stack.length) {
    const n = stack.pop()!;
    uses.set(n[0], (uses.get(n[0]) ?? 0) + 1);
    if (n[1] === 'list') { stack.push(...(n as [number, 'list', number, RdrTree[]])[3]); }
  }
  return uses;
}

/** A value's editor, noting when other places share the atom. */
function RdrValue({ c, node, editable }: { c: C; node: RdrTree; editable: boolean }) {
  const n = useContext(Uses).get(node[0]) ?? 1;
  const shared = n > 1 ? `Shared: used in ${n} places, a change shows in each. ` : '';
  return (
    <>
      <EditableValue className={`av-rdr-val t-${node[1]}`} value={shown(node)} editable={editable && settable(node)} title={`${node[1]}. ${shared}`} onCommit={t => void c.setRdrValue(node[0], t)} />
      {n > 1 && <span className="lm-muted av-rdr-uses" title={shared}>×{n}</span>}
    </>
  );
}

// Lists past this many items show the rest on request.
const PAGE = 400;

function toggleOpen(c: C, key: string): void {
  const next = new Set(c.rdrOpen());
  if (next.has(key)) { next.delete(key); } else { next.add(key); }
  c.rdrOpen.set(next);
}

export function RdrEditor({ c, p }: { c: C; p: Extract<Preview, { type: 'rdr' }> }) {
  const editable = c.canEditInPlace() && !c.editBusy();
  const uses = useMemo(() => countUses(p.tree), [p.tree]);
  return (
    <div className="av-scroll av-mono av-rdr">
      {editable && <div className="lm-muted av-rdr-help">Click a value to change it (×N: shared by N places, which all change). Hover an item for ⧉ duplicate and ✕ remove. Edit › Undo takes a change back.</div>}
      <Uses.Provider value={uses}>
        <RdrRow c={c} node={p.tree} label={p.name} path={RDR_ROOT} depth={0} editable={editable} inList={false} />
      </Uses.Provider>
    </div>
  );
}

function RdrRow({ c, node, label, path, depth, editable, inList }: { c: C; node: RdrTree; label: string; path: string; depth: number; editable: boolean; inList: boolean }) {
  const [more, setMore] = useState(false);
  const atom = node[0];
  const markAtom = c.aiMark()?.atom;
  const marked = markAtom != null && (atom === markAtom || (isField(node) && (node[3][0][0] === markAtom || node[3][1][0] === markAtom)));
  const rowClass = marked ? ' av-ai-mark' : '';
  const open = c.rdrOpen().has(path);
  const toggle = () => toggleOpen(c, path);
  const actions = editable && inList && (
    <span className="av-rdr-actions">
      <button className="lm-row-btn" title="Duplicate (added after the list's last item)" onClick={e => { e.stopPropagation(); void c.duplicateRdr(atom); }}>⧉</button>
      <button className="lm-row-btn lm-danger-btn" title="Remove from the list" onClick={e => { e.stopPropagation(); void c.removeRdr(atom, path); }}>✕</button>
    </span>
  );

  // A named field: [name, value] shown as one row.
  if (isField(node)) {
    const [nameNode, valueNode] = node[3];
    const name = <EditableValue className="av-rdr-key" value={String(nameNode[2])} editable={editable} title="Field name. " onCommit={t => void c.setRdrValue(nameNode[0], t)} />;
    if (valueNode[1] !== 'list') {
      return (
        <div className={`av-rdr-row${rowClass}`} style={{ paddingLeft: depth * 14 + 16 }}>
          {name}<span className="av-rdr-sep">:</span>
          {valueNode[1] === 'shared' ? <SharedList node={valueNode} /> : <RdrValue c={c} node={valueNode} editable={editable} />}
          {actions}
        </div>
      );
    }
    return <RdrList c={c} node={valueNode as [number, 'list', number, RdrTree[]]} labelNode={name} path={path} depth={depth} editable={editable} actions={actions} marked={marked} />;
  }

  if (node[1] !== 'list') {
    return (
      <div className={`av-rdr-row${rowClass}`} style={{ paddingLeft: depth * 14 + 16 }}>
        <span className="av-rdr-key">{label}</span>
        {node[1] === 'shared' ? <SharedList node={node} /> : <RdrValue c={c} node={node} editable={editable} />}
        {actions}
      </div>
    );
  }
  const children = (node as unknown as [number, 'list', number, RdrTree[]])[3];
  const list = children.slice(0, more ? children.length : PAGE);
  return (
    <>
      <div className={`av-rdr-row av-rdr-branch${rowClass}`} style={{ paddingLeft: depth * 14 }} onClick={toggle}>
        <span className="av-rdr-caret">{open ? '▾' : '▸'}</span>
        <span className="av-rdr-key">{label}</span>
        <span className="lm-muted">[{children.length}]</span>
        {actions}
      </div>
      {open && list.map((ch, i) => <RdrRow key={ch[0]} c={c} node={ch} label={`[${i}]`} path={`${path}/${i}`} depth={depth + 1} editable={editable} inList />)}
      {open && !more && children.length > PAGE && <button className="lm-row-btn" style={{ marginLeft: (depth + 1) * 14 }} onClick={() => setMore(true)}>Show all {children.length}</button>}
    </>
  );
}

// A list shown elsewhere in the tree (lists may share a block of items).
function SharedList({ node }: { node: RdrTree }) {
  return <span className="lm-muted" title={`Atom #${node[0]}: the same list as one shown earlier; edit it there.`}>[{node[2]}] shared list</span>;
}

// A field whose value is a list: the field's name on the branch row.
function RdrList({ c, node, labelNode, path, depth, editable, actions, marked }: { c: C; node: [number, 'list', number, RdrTree[]]; labelNode: JSX.Element; path: string; depth: number; editable: boolean; actions: false | JSX.Element; marked: boolean }) {
  const [more, setMore] = useState(false);
  const open = c.rdrOpen().has(path);
  const toggle = () => toggleOpen(c, path);
  const children = node[3];
  return (
    <>
      <div className={`av-rdr-row av-rdr-branch${marked ? ' av-ai-mark' : ''}`} style={{ paddingLeft: depth * 14 }} onClick={toggle}>
        <span className="av-rdr-caret">{open ? '▾' : '▸'}</span>
        {labelNode}
        <span className="lm-muted">[{children.length}]</span>
        {actions}
      </div>
      {open && children.slice(0, more ? children.length : PAGE).map((ch, i) => <RdrRow key={ch[0]} c={c} node={ch} label={`[${i}]`} path={`${path}/${i}`} depth={depth + 1} editable={editable} inList />)}
      {open && !more && children.length > PAGE && <button className="lm-row-btn" style={{ marginLeft: (depth + 1) * 14 }} onClick={() => setMore(true)}>Show all {children.length}</button>}
    </>
  );
}

// ------------------------------------------------------------------ AI maps

const num = (v: string) => Number(v.trim());
const allFinite = (v: number[]) => v.every(Number.isFinite);
// Six significant digits keep the fields short; a field left as shown keeps its exact value (see MapRow's apply).
const short = (v: number) => String(Number(v.toPrecision(6)));

export function MpsEditor({ c, p }: { c: C; p: Extract<Preview, { type: 'mps' }> }) {
  const editable = c.canEditInPlace() && !c.editBusy();
  return (
    <div className="av-scroll">
      <div className="me-card-title">AI maps</div>
      <table className="av-table av-edit-table">
        <thead><tr><th>Name</th><th>Grid</th><th>Bounds and cell scale (X Y Z)</th><th /></tr></thead>
        <tbody>{p.maps.map((m, i) => <MapRow key={m.offset} c={c} index={i} map={m} editable={editable} marked={c.aiMark()?.map === i} />)}</tbody>
      </table>
      <div className="lm-muted me-note">The grid size is fixed: the cells after each header depend on it.</div>
      {p.points && (
        <>
          <div className="me-card-title av-sub">Points <span className="lm-muted">{p.points.points.length}</span></div>
          <table className="av-table av-edit-table">
            <thead><tr><th>Name</th><th>Team</th><th>Kind</th><th>X</th><th>Z</th><th>Cell</th><th /></tr></thead>
            <tbody>{p.points.points.map(pt => <PointRow key={pt.name} c={c} point={pt} editable={!c.editBusy()} />)}</tbody>
          </table>
          <div className="lm-muted me-note">Points move to the nearest grid cell, as in the Map Builder.</div>
        </>
      )}
    </div>
  );
}

function MapRow({ c, index, map, editable, marked }: { c: C; index: number; map: Extract<Preview, { type: 'mps' }>['maps'][number]; editable: boolean; marked: boolean }) {
  const initial = useMemo(() => ({ name: map.name, min: map.min.map(short), max: map.max.map(short), scale: map.scale.map(short) }), [map]);
  const [d, setD] = useState(initial);
  useEffect(() => setD(initial), [initial]);
  const changed = JSON.stringify(d) !== JSON.stringify(initial);
  const vec = (k: 'min' | 'max' | 'scale', label: string) => (
    <div className="av-vec">
      <span className="lm-muted av-vec-label">{label}</span>
      {d[k].map((v, j) => (
        <input key={j} className="me-num av-num" value={v} title={String(map[k][j])} disabled={!editable} onChange={e => setD({ ...d, [k]: d[k].map((x, n) => n === j ? e.target.value : x) })} />
      ))}
    </div>
  );
  const apply = () => {
    const fields: { name?: string; min?: number[]; max?: number[]; scale?: number[] } = {};
    if (d.name !== initial.name) { fields.name = d.name.trim(); }
    for (const k of ['min', 'max', 'scale'] as const) {
      if (d[k].join() === initial[k].join()) { continue; }
      const v = d[k].map((x, j) => x === initial[k][j] ? map[k][j] : num(x));
      if (!allFinite(v)) { c.say(`${k}: numbers only`, true); return; }
      fields[k] = v;
    }
    void c.setMap(index, fields);
  };
  return (
    <tr className={marked ? 'av-ai-mark' : undefined}>
      <td><input className="me-input" value={d.name} maxLength={31} disabled={!editable} onChange={e => setD({ ...d, name: e.target.value })} /></td>
      <td>{map.width} × {map.height}</td>
      <td>{vec('min', 'Min')}{vec('max', 'Max')}{vec('scale', 'Cell')}</td>
      <td className="av-row-actions">{editable && changed && <><button className="lm-tool-btn av-primary" onClick={apply}>Apply</button> <button className="lm-row-btn" onClick={() => setD(initial)}>Revert</button></>}</td>
    </tr>
  );
}

function PointRow({ c, point, editable }: { c: C; point: MpsPoint; editable: boolean }) {
  const initial = useMemo(() => ({ name: point.name, team: String(point.team), kind: point.kind, x: point.x.toFixed(2), z: point.z.toFixed(2) }), [point]);
  const [d, setD] = useState(initial);
  useEffect(() => setD(initial), [initial]);
  const changed = JSON.stringify(d) !== JSON.stringify(initial);
  const apply = () => {
    const fields: { newName?: string; team?: number; kind?: string; position?: number[] } = {};
    if (d.name !== initial.name) { fields.newName = d.name.trim(); }
    if (d.team !== initial.team) { fields.team = Math.round(num(d.team)); }
    if (d.kind !== initial.kind) { fields.kind = d.kind; }
    if (d.x !== initial.x || d.z !== initial.z) {
      const pos = [num(d.x), 0, num(d.z)];
      if (!allFinite(pos)) { c.say('X and Z are numbers', true); return; }
      fields.position = pos;
    }
    void c.setPoint(point.name, fields);
  };
  return (
    <tr>
      <td><input className="me-input" value={d.name} maxLength={15} disabled={!editable} onChange={e => setD({ ...d, name: e.target.value })} /></td>
      <td><input className="me-num av-num" value={d.team} disabled={!editable} onChange={e => setD({ ...d, team: e.target.value })} /></td>
      <td>
        <select className="me-input" value={d.kind} disabled={!editable} onChange={e => setD({ ...d, kind: e.target.value })}>
          {['start', 'waypoint', 'other'].concat(['start', 'waypoint', 'other'].includes(point.kind) ? [] : [point.kind]).map(k => <option key={k} value={k}>{k}</option>)}
        </select>
      </td>
      <td><input className="me-num av-num" value={d.x} disabled={!editable} onChange={e => setD({ ...d, x: e.target.value })} /></td>
      <td><input className="me-num av-num" value={d.z} disabled={!editable} onChange={e => setD({ ...d, z: e.target.value })} /></td>
      <td className="lm-muted">{point.cellX}, {point.cellZ}</td>
      <td className="av-row-actions">{editable && changed && <><button className="lm-tool-btn av-primary" onClick={apply}>Apply</button> <button className="lm-row-btn" onClick={() => setD(initial)}>Revert</button></>}</td>
    </tr>
  );
}

// ------------------------------------------------------------------ text, bytes, icon.sys

export function TextEditor({ c, p }: { c: C; p: Extract<Preview, { type: 'text' }> }) {
  const editable = c.canEditInPlace() && !p.truncated;
  // A textarea hands back \n line breaks; the file keeps its own (SYSTEM.CNF and the .CNFs use \r\n).
  const crlf = p.text.includes('\r\n');
  const base = useMemo(() => p.text.replace(/\r\n/g, '\n'), [p.text]);
  const [draft, setDraft] = useState(base);
  useEffect(() => setDraft(base), [base]);
  const changed = draft !== base;
  return (
    <div className="av-text-edit">
      {editable && (
        <div className="av-image-bar">
          <button className="lm-tool-btn av-primary" disabled={!changed || c.editBusy()} onClick={() => void c.setText(crlf ? draft.replace(/\r?\n/g, '\r\n') : draft)}>Apply</button>
          <button className="lm-row-btn" disabled={!changed} onClick={() => setDraft(base)}>Revert</button>
          <span className="lm-muted">{changed ? 'Changed (not applied)' : `Plain ASCII text${crlf ? ' · CRLF' : ''}`}</span>
        </div>
      )}
      <textarea className="av-scroll av-mono av-textarea" value={draft} readOnly={!editable} spellCheck={false} onChange={e => setDraft(e.target.value)} />
      {p.truncated && <div className="lm-muted me-note">Only the first 512 KB is shown, so this file is not edited here.</div>}
    </div>
  );
}

export function BytePatch({ c }: { c: C }) {
  const [offset, setOffset] = useState('0');
  const [hex, setHex] = useState('');
  const apply = () => {
    const o = parseInt(offset.trim().replace(/^0x/i, ''), 16);
    const bytes = hex.replace(/[^0-9a-f]/gi, '');
    if (!Number.isFinite(o) || o < 0) { c.say('Offset is a hex number', true); return; }
    if (!bytes.length || bytes.length % 2) { c.say('Bytes are pairs of hex digits', true); return; }
    void c.patchBytes(o, bytes);
  };
  return (
    <div className="av-patch">
      <label className="me-toggle">Offset (hex) <input className="me-input av-patch-off" value={offset} onChange={e => setOffset(e.target.value)} /></label>
      <label className="me-toggle av-patch-bytes">Bytes (hex) <input className="me-input" placeholder="e.g. 01 00 80 3F" value={hex} onChange={e => setHex(e.target.value)} /></label>
      <button className="lm-tool-btn av-primary" disabled={c.editBusy()} onClick={apply}>Write</button>
    </div>
  );
}

export function IconSysEditor({ c, p }: { c: C; p: Extract<Preview, { type: 'iconsys' }> }) {
  const initial = useMemo(() => ({ line1: p.line1, line2: p.line2, icons: [...p.icons] }), [p]);
  const [d, setD] = useState(initial);
  useEffect(() => setD(initial), [initial]);
  const editable = c.canEditInPlace() && !c.editBusy();
  const changed = JSON.stringify(d) !== JSON.stringify(initial);
  const roles = ['Icon', 'Copy icon', 'Delete icon'];
  return (
    <div className="av-scroll av-form">
      <div className="me-card-title">Save title</div>
      <label className="me-field me-field-wide"><span>Line 1</span><input className="me-input" value={d.line1} disabled={!editable} onChange={e => setD({ ...d, line1: e.target.value })} /></label>
      <label className="me-field me-field-wide"><span>Line 2</span><input className="me-input" value={d.line2} disabled={!editable} onChange={e => setD({ ...d, line2: e.target.value })} /></label>
      <div className="me-card-title av-sub">Icon files</div>
      {roles.map((r, i) => (
        <label key={r} className="me-field me-field-wide"><span>{r}</span>
          <input className="me-input" value={d.icons[i] ?? ''} disabled={!editable} onChange={e => setD({ ...d, icons: d.icons.map((x, n) => n === i ? e.target.value : x) })} />
        </label>
      ))}
      <div className="lm-muted me-note">Titles are letters, digits, spaces and : . , - ( ) &amp; / ! ? (33 characters across both lines).</div>
      {editable && changed && (
        <div className="av-actions">
          <button className="lm-tool-btn av-primary" onClick={() => void c.setIconSys(d.line1, d.line2, d.icons)}>Apply</button>
          <button className="lm-row-btn" onClick={() => setD(initial)}>Revert</button>
        </div>
      )}
    </div>
  );
}
