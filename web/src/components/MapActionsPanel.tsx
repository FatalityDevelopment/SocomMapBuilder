// The Item panel's Actions tab: what a prop does when the player walks up to it (actions.rdr in READERM.ZAR). Rows
// bind by the name of a placed GEO node; the editor shows them in plain words, groups the map's actions by kind with
// the same icons the 3D view floats over props, starts a new action from sensible defaults and checks fields inline.

import { useEffect, useMemo, useRef, useState } from 'react';
import type { ActionFields, AssetClient, MapActions } from '../webview/model-editor/asset-client';
import { host } from '../shared';
import { turretHolders } from '../viewport/actionMarkers';
import { ACTION_CHOICES, ACTION_KINDS, ACTION_TYPES, actionIconUrl, actionKind, promptName, type ActionKind } from '../shared/actionIcons';
import { FIELD_HELP, checkAction, cleanFields, defaultsFor, groupRows, withKind } from './mapActionsModel';

interface Props {
  client: AssetClient;
  /** ASSET_MAP_ACTIONS for the current revision, or null while it loads. */
  data: MapActions | null;
  error?: string;
  selected: number | null;
  busy: boolean;
  path: string;
  edit: (label: string, op: Record<string, unknown>) => Promise<unknown>;
  model?: string;
  revision: number;
  /** Select (and frame) a placed node's prop in the 3D view. */
  onFocusNode?: (geoIndex: number) => void;
}

export function MapActionsPanel(p: Props) {
  const focus = useRef({ path: '', row: -1 });
  if (!p.data || p.data.revision !== p.revision) { return <div className="me-note">{p.error || 'Reading map actions…'}</div>; }
  return <ActionInspector key={`${p.data.revision}:${p.selected}`} {...p} data={p.data} focus={focus.current} />;
}

function Icon({ kind, size = 18 }: { kind: ActionKind; size?: number }) {
  const url = actionIconUrl(kind);
  return url ? <img className="mb-act-icon" src={url} width={size} height={size} alt="" /> : null;
}

const TURRET_PROMPTS = ['action_mount_turret.tif', 'action_dismount_turret.tif'];

/** What a placed turret offers: the game's own Mount and Dismount prompts, shown with their HUD pictures. */
function TurretCard({ client, data, name }: { client: AssetClient; data: MapActions; name: string }) {
  const [art, setArt] = useState<Record<string, string>>({});
  useEffect(() => {
    let live = true;
    for (const want of TURRET_PROMPTS) {
      const icon = data.icons.find(i => i.toLowerCase() === want);
      if (icon) { client.texture(icon, 'hud').then(t => { if (live) { setArt(a => ({ ...a, [want]: 'data:image/png;base64,' + t.png })); } }).catch(() => {}); }
    }
    return () => { live = false; };
  }, [client, data]);
  return <div className="me-card">
    <div className="me-card-title"><Icon kind="TURRET" size={16} /> Turret</div>
    <p className="me-note">The game gives every placed turret model these prompts by itself (its aim limits and gun are in READERC.ZAR › ai_turrets.rdr under a name like “{name}”). No action row is needed.</p>
    <div className="mb-act-chips">{TURRET_PROMPTS.map(p => <span key={p} className="mb-act-chip">
      {art[p] ? <img className="mb-act-icon" src={art[p]} alt="" width={22} height={22} /> : <Icon kind="TURRET" size={14} />} {promptName(p)}
    </span>)}</div>
  </div>;
}

function Hint({ text, tone }: { text?: string; tone?: 'error' | 'warn' }) {
  if (!text) { return null; }
  return <div className={`mb-act-hint${tone ? ' ' + tone : ''}`} role={tone === 'error' ? 'alert' : undefined}>{text}</div>;
}

function ActionInspector({ data, selected, busy, path, edit, model, client, focus, onFocusNode }: Props & { data: MapActions; focus: { path: string; row: number } }) {
  const hit = data.targets.find(n => n.index === selected);
  // The rendered mesh is often a child of the door that owns the action.
  const bound = hit && data.targets.filter(t => t.index === hit.index || hit.path.startsWith(t.path + '/'))
    .filter(t => data.rows.some(r => r.node[0] === t.name)).sort((a, b) => b.path.length - a.path.length)[0];
  const initial = (selected === null ? data.targets.find(t => t.path === focus.path) : undefined) ?? bound ?? hit;
  const [index, setIndex] = useState(initial?.index ?? -1);
  const [filter, setFilter] = useState(initial ? '' : model ?? '');
  const initialRow = initial && data.rows[focus.row]?.node[0] === initial.name ? focus.row :
    initial ? data.rows.findIndex(r => r.node[0] === initial.name) : -1;
  const [row, setRow] = useState(initialRow);
  const [fields, setFields] = useState<ActionFields>(() => data.rows[initialRow] ?? (initial ? defaultsFor('DOOR', data, initial.name) : {}));
  const [touched, setTouched] = useState(false);
  const [profile, setProfile] = useState('climb');
  const [surface, setSurface] = useState('+Z');
  const [uniqueName, setUniqueName] = useState('');
  const [preview, setPreview] = useState('');
  const bitmap = String(fields.bitmap?.[0] ?? '');
  useEffect(() => {
    let live = true;
    setPreview('');
    const icon = data.icons.find(i => i.toLowerCase() === bitmap.toLowerCase());
    if (icon) {
      client.texture(icon, 'hud').then(texture => { if (live) { setPreview('data:image/png;base64,' + texture.png); } }).catch(() => {});
    }
    return () => { live = false; };
  }, [bitmap, client, data]);
  const target = data.targets.find(t => t.index === index);
  useEffect(() => { focus.path = target?.path ?? ''; focus.row = row; }, [target, row, focus]);
  const name = target?.name ?? String(data.rows[row]?.node[0] ?? '');
  const ambiguous = !!target && data.targets.some(t => t.index !== index && t.name.toLowerCase() === name.toLowerCase());
  const rename = row < 0 && ambiguous && !!target?.model;
  // A turret's prompts come from the game, not actions.rdr: the chosen node, a model under it or the node above it.
  const turret = target && data.targets.find(t => t.turret && (t.index === target.index || t.path.startsWith(target.path + '/') || target.path.startsWith(t.path + '/')));
  const turrets = useMemo(() => turretHolders(data.targets), [data.targets]);
  const kind = actionKind(fields);
  const check = useMemo(() => checkAction(fields, data), [fields, data]);
  const groups = useMemo(() => groupRows(data.rows), [data.rows]);
  const nodeRows = target ? data.rows.map((r, i) => ({ r, i })).filter(x => x.r.node[0] === target.name) : [];
  const valves = useMemo(() => [...new Set([...data.valves, ...data.rows.flatMap(r => (r.valve ?? []).map(String))])].sort(), [data]);
  const blocked = Object.keys(check.errors).length > 0 || !name || (rename && !uniqueName.trim()) || (row < 0 && ambiguous && !rename);

  function chooseRow(id: number, keepTarget = false) {
    setRow(id);
    setTouched(false);
    setFields(id >= 0 ? { ...data.rows[id] } : defaultsFor('DOOR', data, keepTarget ? target?.name ?? '' : ''));
    if (id >= 0 && !keepTarget) {
      const matches = data.targets.filter(t => t.name === data.rows[id].node[0]);
      setIndex(matches.length === 1 ? matches[0].index : -1);
      if (matches.length === 1) { onFocusNode?.(matches[0].index); }
    }
  }
  function chooseTarget(id: number) {
    setIndex(id); setSurface('+Z'); setUniqueName('');
    const t = data.targets.find(x => x.index === id);
    const r = data.rows.findIndex(x => x.node[0] === t?.name);
    setRow(r); setTouched(false);
    setFields(r >= 0 ? { ...data.rows[r] } : defaultsFor('DOOR', data, t?.name ?? ''));
  }
  function setField(key: string, values: (string | number)[] | undefined) {
    setTouched(true);
    setFields(old => { const next = { ...old }; if (values && values.length) { next[key] = values; } else if (values && (key === 'one_shot' || key === 'tcm')) { next[key] = []; } else { delete next[key]; } return next; });
  }
  function setKind(next: ActionKind) {
    setTouched(true);
    if (row < 0 && !touched) { setFields(defaultsFor(next, data, name)); return; }
    setFields(old => withKind(old, actionKind(old), next, data));
  }
  function submit() {
    if (blocked) { return; }
    const values = cleanFields(fields, rename ? uniqueName.trim() : name);
    if (row < 0) { focus.row = data.rows.length; }
    if (rename && target) { focus.path = target.path.slice(0, target.path.lastIndexOf('/') + 1) + uniqueName.trim(); }
    void edit(row < 0 ? 'Add map action' : 'Update map action', { op: 'mapAction', revision: data.revision, row, fields: values,
      ...(rename ? { renamePlacement: index } : {}) });
  }
  const available = data.targets.filter(t => t.index === index || (t.path + ' ' + t.model).toLowerCase().includes(filter.toLowerCase()));
  const text = (key: string) => String(fields[key]?.[0] ?? '');
  const number = (key: string, placeholder: string) => <label className="mb-act-field">{FIELD_HELP[key].label}
    <input type="number" step={1} value={text(key)} placeholder={placeholder} aria-invalid={!!check.errors[key]} onChange={e => setField(key, e.target.value ? [e.target.value] : undefined)} />
    <Hint text={check.errors[key]} tone="error" />
    <span className="mb-act-sub">{FIELD_HELP[key].hint}</span>
  </label>;

  return <div className="mb-action-editor">
    {!data.hasReader && <div className="me-card mb-act-empty"><b>This package has no mission reader.</b> Actions live in READERM.ZAR, which only mission and multiplayer maps carry.</div>}
    {data.hasReader && data.rows.length === 0 && !target && <div className="me-card mb-act-empty">
      <div className="me-card-title">What are actions?</div>
      <p>An action is something the player can do with a prop: open a door, flip a switch, pick up an item, plant a charge.
        The game shows a prompt when the player is close enough, then sets a <b>mission switch</b> (a valve) and plays <b>map animations</b>.</p>
      <p>This map has none yet. Select a prop in the 3D view, or choose one below, then add an action to it.</p>
    </div>}

    <div className="me-card">
      <div className="me-card-title">Prop</div>
      {target ? <div className="mb-act-target">
        <span className="mb-act-target-name" title={target.path}>{target.name}</span>
        <span className="lm-muted">{target.model || 'scene node'} · #{target.index}</span>
        {onFocusNode && <button type="button" onClick={() => onFocusNode(target.index)} title="Select and frame it in the 3D view">Show</button>}
      </div> : <p className="me-note">No prop chosen. Click a prop (or its icon) in the 3D view, or pick one here.</p>}
      {target && nodeRows.length > 0 && <div className="mb-act-chips">{nodeRows.map(({ r, i }) => {
        const k = actionKind(r);
        return <button type="button" key={i} className={`mb-act-chip${i === row ? ' on' : ''}`} onClick={() => chooseRow(i, true)}><Icon kind={k} size={14} /> {ACTION_KINDS[k].label}</button>;
      })}<button type="button" className={`mb-act-chip${row < 0 ? ' on' : ''}`} onClick={() => chooseRow(-1, true)}>+ New</button></div>}
      <details className="mb-act-picker" open={!target}>
        <summary>{target ? 'Choose another prop' : 'Choose a prop'}</summary>
        <input className="me-search" placeholder="Filter by name or model" value={filter} onChange={e => setFilter(e.target.value)} />
        <select aria-label="Placed GEO node" value={index} onChange={e => chooseTarget(+e.target.value)}>
          <option value={-1}>Choose a placement…</option>
          {available.map(n => <option key={n.index} value={n.index}>{n.path} · #{n.index}</option>)}
        </select>
      </details>
    </div>

    {turret && <TurretCard client={client} data={data} name={turrets.find(h => turret.path === h.path || turret.path.startsWith(h.path + '/'))?.name ?? turret.name} />}

    <fieldset disabled={busy || !data.hasReader || (!target && row < 0)} className="me-card mb-act-form">
      <legend>{row < 0 ? 'New action' : `Action ${row + 1} of ${data.rows.length}`}</legend>
      {row >= 0 && target?.name !== String(data.rows[row].node[0]) && <p className="me-note">Bound to “{String(data.rows[row].node[0])}”, which no placed node is named. Choose a prop above to rebind it.</p>}
      {row >= 0 && <label className="mb-act-field">Bound to<select value={index} onChange={e => { setIndex(+e.target.value); setSurface('+Z'); }}>
        <option value={-1}>{String(data.rows[row].node[0])}</option>
        {available.map(n => <option key={n.index} value={n.index}>{n.path}</option>)}
      </select><span className="mb-act-sub">Saving with another node moves the action to it.</span></label>}
      {row < 0 && data.rows.length > 0 && <label className="mb-act-field">Copy settings from<select value="" onChange={e => { if (e.target.value) { setTouched(true); setFields({ ...data.rows[Number(e.target.value)] }); } }}>
        <option value="">Defaults for the type below</option>{data.rows.map((r, i) => <option value={i} key={i}>{String(r.node[0])} · {ACTION_KINDS[actionKind(r)].label}</option>)}
      </select></label>}

      <div className="mb-act-group"><div className="mb-act-group-title">{FIELD_HELP.type.label}</div>
        <div className="mb-act-kinds" role="radiogroup" aria-label="Action type">
          {[...ACTION_CHOICES, 'ANIM' as ActionKind].map(k => <button type="button" role="radio" aria-checked={kind === k} key={k} className={`mb-act-kind${kind === k ? ' on' : ''}`}
            title={ACTION_KINDS[k].description} onClick={() => setKind(k)}><Icon kind={k} size={22} /><span>{k === 'ANIM' ? 'No prompt' : ACTION_KINDS[k].label}</span></button>)}
        </div>
        <div className="mb-act-sub">{ACTION_KINDS[kind].description}{kind === 'OTHER' && ` (${text('type')})`}</div>
        <Hint text={check.errors.type} tone="error" /><Hint text={check.warnings.type} tone="warn" />
      </div>

      {kind !== 'ANIM' && <div className="mb-act-group"><div className="mb-act-group-title">Prompt</div>
        <label className="mb-act-field">{FIELD_HELP.bitmap.label}
          <div className="mb-act-row">
            <select value={data.icons.find(i => i.toLowerCase() === bitmap.toLowerCase()) ?? (bitmap ? '__custom' : '')} onChange={e => setField('bitmap', e.target.value && e.target.value !== '__custom' ? [e.target.value] : undefined)}>
              <option value="">None</option>
              {data.icons.map(i => <option key={i} value={i}>{promptName(i)}</option>)}
              {bitmap && !data.icons.some(i => i.toLowerCase() === bitmap.toLowerCase()) && <option value="__custom">{bitmap}</option>}
            </select>
            {preview && <img className="mb-act-preview" src={preview} alt={bitmap} width={40} height={40} />}
          </div>
          <Hint text={check.warnings.bitmap} tone="warn" />
        </label>
        <label className="mb-act-field">{FIELD_HELP.descriptor.label}<input value={text('descriptor')} placeholder="(none)" aria-invalid={!!check.errors.descriptor} onChange={e => setField('descriptor', e.target.value ? [e.target.value] : undefined)} />
          <Hint text={check.errors.descriptor} tone="error" /></label>
        {number('range', '30')}
        {number('elevation', 'any height')}
      </div>}

      <div className="mb-act-group"><div className="mb-act-group-title">What happens</div>
        <label className="mb-act-field">{FIELD_HELP.valve.label}
          <input list="action-valves" value={text('valve')} placeholder="e.g. door01" aria-invalid={!!check.errors.valve} onChange={e => setField('valve', e.target.value ? [e.target.value] : undefined)} />
          <datalist id="action-valves">{valves.map(v => <option key={v} value={v} />)}</datalist>
          <Hint text={check.errors.valve} tone="error" /><Hint text={check.warnings.valve} tone="warn" />
          <span className="mb-act-sub">{FIELD_HELP.valve.hint}</span>
        </label>
        <div className="mb-act-field">{FIELD_HELP.anim.label}
          {(fields.anim ?? []).length === 0 && <div className="lm-muted">None.</div>}
          {(fields.anim ?? []).map((a, i) => <div className="mb-act-row" key={i}>
            <span className="mb-act-step">{i + 1}</span>
            <input aria-label={`Animation ${i + 1}`} value={String(a)} onChange={e => setField('anim', (fields.anim ?? []).map((x, k) => k === i ? e.target.value : x))} />
            <button type="button" title="Remove" onClick={() => setField('anim', (fields.anim ?? []).filter((_, k) => k !== i))}>×</button>
          </div>)}
          <select aria-label="Add a map animation" value="" onChange={e => { if (e.target.value) { setField('anim', [...(fields.anim ?? []), e.target.value]); } }}>
            <option value="">{data.animations.length ? '+ Add an animation from this map…' : '+ Add an animation…'}</option>
            {data.animations.map(v => <option key={v}>{v}</option>)}
          </select>
          {!data.animations.length && <button type="button" onClick={() => setField('anim', [...(fields.anim ?? []), ''])}>+ Add by name</button>}
          <Hint text={check.errors.anim} tone="error" /><Hint text={check.warnings.anim} tone="warn" />
          <span className="mb-act-sub">{FIELD_HELP.anim.hint}</span>
        </div>
        <label className="mb-act-check"><input type="checkbox" checked={!!fields.one_shot} onChange={e => setField('one_shot', e.target.checked ? [] : undefined)} /> {FIELD_HELP.one_shot.label}
          <span className="mb-act-sub"> · {FIELD_HELP.one_shot.hint}</span></label>
      </div>

      <details className="mb-act-group"><summary>Advanced</summary>
        {number('team', 'any team')}
        {number('points', 'none')}
        <label className="mb-act-check"><input type="checkbox" checked={!!fields.tcm} onChange={e => setField('tcm', e.target.checked ? [] : undefined)} /> {FIELD_HELP.tcm.label}
          <span className="mb-act-sub"> · {FIELD_HELP.tcm.hint}</span></label>
        <label className="mb-act-field">Raw type<input list="action-types" value={text('type')} onChange={e => setField('type', e.target.value ? [e.target.value] : undefined)} />
          <datalist id="action-types">{ACTION_TYPES.map(t => <option key={t} value={t} />)}</datalist></label>
      </details>

      {ambiguous && row < 0 && <Hint tone="warn" text={`Several placements are named “${name}”. The game binds by name, so a new action needs a name only this prop has.`} />}
      {rename && <label className="mb-act-field">New name for this placement<input value={uniqueName} placeholder={`${name}_action`} onChange={e => setUniqueName(e.target.value)} /></label>}
      <div className="mb-act-buttons">
        <button type="button" className="primary" disabled={blocked} onClick={submit}>{row < 0 ? 'Add action' : 'Save changes'}</button>
        {row >= 0 && touched && <button type="button" onClick={() => chooseRow(row, true)}>Revert</button>}
        {row >= 0 && <button type="button" onClick={() => void edit('Remove map action', { op: 'mapAction', revision: data.revision, row })}>Remove action</button>}
      </div>
      <p className="me-note">The valve and animations must exist for the action to work in game. Copying a binding also copies its valve.</p>
    </fieldset>

    <div className="me-card">
      <div className="me-card-title">Actions on this map ({data.rows.length})</div>
      {data.rows.length === 0 && <p className="me-note">None yet.</p>}
      {groups.map(g => <div key={g.kind} className="mb-act-list-group">
        <div className="mb-act-list-head"><Icon kind={g.kind} size={16} /> {ACTION_KINDS[g.kind].label} <span className="lm-muted">{g.rows.length}</span></div>
        <div className="mb-act-list">{g.rows.map(r => <button type="button" key={r.row} className={r.row === row ? 'on' : ''} onClick={() => chooseRow(r.row)}>{r.node}</button>)}</div>
      </div>)}
      {turrets.length > 0 && <div className="mb-act-list-group">
        <div className="mb-act-list-head"><Icon kind="TURRET" size={16} /> {ACTION_KINDS.TURRET.label} · mount / dismount <span className="lm-muted">{turrets.length}</span></div>
        <div className="mb-act-list">{turrets.map(t => <button type="button" key={t.index} className={t.index === index ? 'on' : ''}
          onClick={() => { chooseTarget(t.index); onFocusNode?.(t.index); }}>{t.name}</button>)}</div>
      </div>}
    </div>

    <details className="me-card"><summary>Climb and ladder surfaces · experimental</summary>
      <fieldset disabled={busy || !target} className="mb-act-plain">
        <p className="me-note">Adds or changes a four-point traversal face on the chosen prop, copying a profile sampled from a retail map.</p>
        <label className="mb-act-field">Profile<select value={profile} onChange={e => setProfile(e.target.value)}><option value="climb">Climb (MP51)</option><option value="ladder">Ladder (M51)</option></select></label>
        <label className="mb-act-field">Surface<select value={surface} onChange={e => setSurface(e.target.value)}>
          {['+X', '-X', '+Z', '-Z'].map(side => <option key={side} value={side}>New face on local {side} side</option>)}
          {target?.faces.map(face => <option key={face} value={face}>Change existing quad #{face}</option>)}
        </select></label>
        <button type="button" onClick={() => void edit('Set traversal surface', { op: 'traversalFace', revision: data.revision, geoIndex: index, profile,
          ...(/^[+-]/.test(surface) ? { side: surface } : { face: Number(surface) }) })}>Apply surface</button>
        <p className="me-note">New faces span the chosen side of the placement’s local bounds. Check the prompt, approach direction and dismount in game.</p>
      </fieldset>
    </details>
    <details className="me-card"><summary>Mission tables ({data.tables.length})</summary><p className="me-note">Open a table in the Asset Viewer to see or edit every value. The Mission tab shows the main ones.</p>
      {data.tables.map(t => <button type="button" key={t} onClick={() => host.openMissionTable(path, t)}>{t}</button>)}</details>
  </div>;
}
