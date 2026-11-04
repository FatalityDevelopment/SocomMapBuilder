import { useEffect, useRef, useState } from 'react';
import type { ActionFields, AssetClient, MapActions } from '../webview/model-editor/asset-client';
import { host } from '../shared';

interface Props {
  client: AssetClient;
  selected: number | null;
  busy: boolean;
  path: string;
  edit: (label: string, op: Record<string, unknown>) => Promise<unknown>;
  model?: string;
  revision: number;
}

const numbers = ['range', 'elevation', 'team', 'points'];
const types = ['DOOR', 'SWITCH', 'ITEM', 'SATCHEL', 'MPBOMB', 'DEFUSE', 'ACT_BREACH'];
const labels: Record<string, string> = {
  type: 'Action type', valve: 'Mission valve', anim: 'Map animations (one per line)', bitmap: 'Prompt icon',
  descriptor: 'Prompt text', range: 'Range', elevation: 'Elevation', team: 'Team', points: 'Points'
};

export function MapActionsPanel(p: Props) {
  const [data, setData] = useState<MapActions | null>(null);
  const [error, setError] = useState('');
  const focus = useRef({ path: '', row: -1 });
  useEffect(() => {
    let live = true;
    p.client.mapActions().then(r => { if (live) setData(r); }).catch(e => { if (live) setError(String(e)); });
    return () => { live = false; };
  }, [p.client, p.revision]);
  return data && data.revision === p.revision ? <ActionInspector key={data.revision} {...p} data={data} focus={focus.current} /> :
    <div className="me-note">{error || 'Reading map actions…'}</div>;
}

function ActionInspector({ data, selected, busy, path, edit, model, client, focus }: Props & { data: MapActions; focus: {path: string; row: number} }) {
  const hit = data.targets.find(n => n.index === selected);
  // The rendered mesh is often a child of the door that owns the action.
  const bound = hit && data.targets.filter(t => t.index === hit.index || hit.path.startsWith(t.path + '/'))
    .filter(t => data.rows.some(r => r.node[0] === t.name)).sort((a,b) => b.path.length - a.path.length)[0];
  const initial = data.targets.find(t => t.path === focus.path) ?? bound ?? hit;
  const [index, setIndex] = useState(initial?.index ?? -1);
  const [filter, setFilter] = useState(initial ? '' : model ?? '');
  const initialRow = initial && data.rows[focus.row]?.node[0] === initial.name ? focus.row :
    initial ? data.rows.findIndex(r => r.node[0] === initial.name) : -1;
  const [row, setRow] = useState(initialRow);
  const [fields, setFields] = useState<ActionFields>(data.rows[initialRow] ?? {});
  const [error, setError] = useState('');
  const [profile, setProfile] = useState('climb');
  const [surface, setSurface] = useState('+Z');
  const [uniqueName, setUniqueName] = useState('');
  const [preview, setPreview] = useState('');
  const bitmap = String(fields.bitmap?.[0] ?? '');
  useEffect(() => {
    let live = true;
    setPreview('');
    if (data.icons.includes(bitmap)) client.texture(bitmap, 'hud').then(texture => {
      if (live) setPreview('data:image/png;base64,' + texture.png);
    }).catch(() => {});
    return () => { live = false; };
  }, [bitmap, client, data]);
  const target = data.targets.find(t => t.index === index);
  useEffect(() => { focus.path = target?.path ?? ''; focus.row = row; }, [target, row, focus]);
  const name = target?.name ?? String(data.rows[row]?.node[0] ?? '');
  const ambiguous = !!target && data.targets.some(t => t.index !== index && t.name.toLowerCase() === name.toLowerCase());
  const rename = row < 0 && ambiguous && !!target?.model;
  function chooseRow(id: number) {
    setRow(id); setFields(data.rows[id] ?? {}); setError('');
  }
  function chooseTarget(id: number) {
    setIndex(id); setSurface('+Z'); setUniqueName('');
    chooseRow(data.rows.findIndex(r => r.node[0] === data.targets.find(t => t.index === id)?.name));
  }
  function setField(key: string, values: (string | number)[] | undefined) {
    setFields(old => { const next = { ...old }; if (values) next[key] = values; else delete next[key]; return next; });
  }
  function submit() {
    const values: ActionFields = { ...fields, node: [rename ? uniqueName.trim() : name] };
    for (const key of numbers) {
      if (!fields[key]) continue;
      const n = Number(fields[key][0]);
      if (!Number.isInteger(n) || n < -2147483648 || n > 2147483647) { setError(labels[key] + ' must be a whole number.'); return; }
      Object.assign(values, { [key]: [n] });
    }
    if (fields.anim) values.anim = fields.anim.map(v => String(v).trim()).filter(Boolean);
    if (Object.keys(values).length < 2) { setError('Set an action type, valve, or animation first.'); return; }
    if (row < 0) focus.row = data.rows.length;
    if (rename && target) focus.path = target.path.slice(0,target.path.lastIndexOf('/') + 1) + uniqueName.trim();
    void edit(row < 0 ? 'Add map action' : 'Update map action', { op: 'mapAction', revision: data.revision, row, fields: values,
      ...(rename ? { renamePlacement:index } : {}) });
  }
  const available = data.targets.filter(t => t.index === index || (t.path + ' ' + t.model).toLowerCase().includes(filter.toLowerCase()));
  return <div className="mb-action-editor">
    <div className="me-card"><div className="me-card-title">Action target</div>
      <input className="me-search" placeholder="Filter placed GEO nodes" value={filter} onChange={e => setFilter(e.target.value)} />
      <select aria-label="Placed GEO node" value={index} onChange={e => chooseTarget(+e.target.value)}>
        <option value={-1}>Choose a placement…</option>
        {available.map(n => <option key={n.index} value={n.index}>{n.path} · #{n.index}</option>)}
      </select>
      <label>Map actions<select value={row} onChange={e => {
        const id = +e.target.value; chooseRow(id); if (id >= 0) {
          const matches = data.targets.filter(t => t.name === data.rows[id].node[0]);
          setIndex(matches.length === 1 ? matches[0].index : -1); setSurface('+Z');
        }
      }}><option value={-1}>New binding</option>{data.rows.map((r,i) => <option value={i} key={i}>{String(r.node[0])} · {r.type ?? r.anim ?? r.valve}</option>)}</select></label>
    </div>
    <fieldset disabled={busy || !data.hasReader} className="me-card"><legend>Object action</legend>
      {!data.hasReader && <p>This package has no mission reader.</p>}
      {row >= 0 && <label>Bound node<select value={index} onChange={e => { setIndex(+e.target.value); setSurface('+Z'); }}>
        <option value={-1}>{String(data.rows[row].node[0])}</option>
        {available.map(n => <option key={n.index} value={n.index}>{n.path}</option>)}
      </select></label>}
      {row < 0 && <label>Start from<select defaultValue="" onChange={e => setFields(e.target.value ? { ...data.rows[Number(e.target.value)] } : {})}>
        <option value="">Blank action</option>{data.rows.map((r,i) => <option value={i} key={i}>{String(r.node[0])} · {r.type ?? r.anim ?? r.valve}</option>)}
      </select></label>}
      {Object.entries(labels).map(([key,label]) => <label key={key}>{label}
        {key === 'anim' ? <textarea rows={2} value={fields.anim?.join('\n') ?? ''} onChange={e => setField('anim',e.target.value ? e.target.value.split('\n') : undefined)} /> :
          <input list={'action-options-' + key} type={numbers.includes(key) ? 'number' : 'text'} value={fields[key]?.[0] ?? ''}
            onChange={e => setField(key,e.target.value ? [e.target.value] : undefined)} />}
      </label>)}
      {preview && <img src={preview} alt={bitmap} width={48} height={48} style={{objectFit:'contain'}} />}
      {data.animations.length > 0 && <label>Append map animation<select value="" onChange={e => {
        if (e.target.value) setField('anim', [...(fields.anim ?? []), e.target.value]);
      }}><option value="">Choose from this map…</option>{data.animations.map(v => <option key={v}>{v}</option>)}</select></label>}
      {['type','bitmap','valve'].map(key => <datalist key={key} id={'action-options-' + key}>
        {(key === 'type' ? types : key === 'bitmap' ? data.icons : [...new Set([...data.valves, ...data.rows.flatMap(r => r.valve ?? [])])]).map(v => <option key={v} value={v} />)}
      </datalist>)}
      {['one_shot','tcm'].map(k => <label key={k}><input type="checkbox" checked={!!fields[k]} onChange={e => setField(k,e.target.checked ? [] : undefined)} /> {k === 'one_shot' ? 'One shot' : 'TCM'}</label>)}
      {ambiguous && <p>Several placements share this name. A new action needs a unique GEO name.</p>}
      {rename && <label>Rename this placement for its action<input value={uniqueName} onChange={e => setUniqueName(e.target.value)} /></label>}
      {error && <p role="alert">{error}</p>}
      <button disabled={!name || (row < 0 && ambiguous && !(rename && uniqueName.trim()))} onClick={submit}>{row < 0 ? 'Add action' : 'Apply changes'}</button>
      {row >= 0 && <button onClick={() => void edit('Remove map action',{ op:'mapAction', revision:data.revision, row })}>Remove action</button>}
      <p className="me-note">The valve and map animations must exist for the intended behavior. Copying a binding also copies its valve.</p>
    </fieldset>
    <fieldset className="me-card" disabled={busy || !target}><legend>Climb and ladder surfaces · experimental</legend>
      <label>Traversal<select value={profile} onChange={e => setProfile(e.target.value)}><option value="climb">Climb (MP51)</option><option value="ladder">Ladder (M51)</option></select></label>
      <label>Surface<select value={surface} onChange={e => setSurface(e.target.value)}>
        {['+X','-X','+Z','-Z'].map(side => <option key={side} value={side}>New face on local {side} side</option>)}
        {target?.faces.map(face => <option key={face} value={face}>Change existing quad #{face}</option>)}
      </select></label>
      <button onClick={() => void edit('Set traversal surface',{ op:'traversalFace', revision:data.revision, geoIndex:index, profile,
        ...(/^[+-]/.test(surface) ? { side:surface } : { face:Number(surface) }) })}>Apply surface</button>
      <p className="me-note">New faces span the chosen side of the placement’s local bounds. Verify prompt, approach direction, and dismount in game.</p>
    </fieldset>
    <details className="me-card"><summary>Mission tables ({data.tables.length})</summary><p className="me-note">Open a table in the Asset Viewer to inspect or edit its values.</p>
      {data.tables.map(t => <button key={t} onClick={() => host.openMissionTable(path,t)}>{t}</button>)}</details>
  </div>;
}
