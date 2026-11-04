// The Mission tab: the map's mission tables (READERM.ZAR) in plain words. HUD objectives (their flags stand on the
// map in 3D) with their text and location, the actors who start on the map and their AI, the mission's settings and
// weapon switches, and the world's lighting and fog (read-only). Values change in place with the mapTable edit,
// objective text with mapText; the Asset Viewer's RDR editor reaches everything else.

import { useEffect, useState } from 'react';
import type { MapTables, TablePath } from '../webview/model-editor/asset-client';
import { host } from '../shared';
import { objectiveIconUrl } from '../shared/actionIcons';
import { isSeal } from '../viewport/actorFigures';

interface Props {
  tables: MapTables | null;
  error?: string;
  revision: number;
  busy: boolean;
  path: string;
  edit: (label: string, op: Record<string, unknown>) => Promise<unknown>;
  selectedObjective: number | null;
  onSelectObjective: (row: number | null) => void;
  /** Frame an objective's location in the 3D view. */
  onShowObjective: (row: number) => void;
  /** Weapon models ("archive:name") and the stand-in weapon picked per character type for the 3D figures. */
  weaponModels?: string[];
  actorWeapons?: Record<string, string>;
  onChooseWeapon?: (chartype: string, source: string) => void;
}

/** A value that commits on Enter or leaving the field, with an inline check. */
function ValueField({ value, label, onCommit, disabled, width }: { value: number | string | null | undefined; label: string; onCommit: (v: number) => void; disabled?: boolean; width?: number }) {
  const [text, setText] = useState(value == null ? '' : String(value));
  useEffect(() => { setText(value == null ? '' : String(value)); }, [value]);
  const n = Number(text);
  const bad = text.trim() === '' || !Number.isFinite(n);
  const commit = () => { if (!bad && String(n) !== String(value)) { onCommit(n); } else if (bad) { setText(value == null ? '' : String(value)); } };
  return <input className={`mb-mission-num${bad ? ' bad' : ''}`} aria-label={label} title={bad ? 'Enter a number' : label} disabled={disabled} value={text} style={width ? { width } : undefined}
    onChange={e => setText(e.target.value)} onBlur={commit} onKeyDown={e => { if (e.key === 'Enter') { (e.target as HTMLInputElement).blur(); } if (e.key === 'Escape') { setText(value == null ? '' : String(value)); } }} />;
}

const rgb = (c: number[] | null | undefined) => c && c.length >= 3 ? `rgb(${c.slice(0, 3).map(v => Math.max(0, Math.min(255, Math.round(v)))).join(',')})` : 'transparent';
const pretty = (key: string) => key.replace(/_/g, ' ').replace(/([a-z])([A-Z])/g, '$1 $2').replace(/^\w/, c => c.toUpperCase());

export function MissionPanel(p: Props) {
  const t = p.tables;
  if (!t || t.revision !== p.revision) { return <div className="me-note">{p.error || 'Reading mission tables…'}</div>; }
  if (!t.hasReader) {
    return <div className="me-card mb-act-empty"><b>This package has no mission reader.</b> Objectives, actors and mission settings live in READERM.ZAR, which mission and multiplayer maps carry.</div>;
  }
  const text = (key: string | null | undefined) => (key && t.texts[key]) || key || '';
  const set = (table: string, path: TablePath, values: Array<number | string>, label: string) =>
    void p.edit(label, { op: 'mapTable', revision: t.revision, table, path, values, label });
  const setText = async (key: string) => {
    const r = await host.prompt('Change the text', [{ key: 'text', label: key, value: t.texts[key] ?? '' }], 'Save');
    const next = r?.text?.trim();
    if (next && next !== t.texts[key]) { void p.edit('Edit mission text', { op: 'mapText', revision: t.revision, key, text: next, label: 'Edit mission text' }); }
  };
  const weapons = t.mission?.valves.filter(v => v.name?.startsWith('Enable')) ?? [];
  const otherValves = t.mission?.valves.filter(v => !v.name?.startsWith('Enable')) ?? [];
  const selected = t.objectives.find(o => o.row === p.selectedObjective) ?? null;

  return <div className="mb-mission">
    <div className="me-card">
      <div className="me-card-title">Mission</div>
      {t.mission?.description && t.texts[t.mission.description] && <p className="mb-mission-lead">{t.texts[t.mission.description]}</p>}
      <p className="me-note">{t.objectives.length} objective(s), {t.actors.length} actor(s), {t.valves.length} valve(s). Flags on the map mark objective locations (View › Objective flags).</p>
    </div>

    <div className="me-card">
      <div className="me-card-title">Objectives ({t.objectives.length})</div>
      {t.objectives.length === 0 && <p className="me-note">This map has no HUD objectives (hudobjectives.rdr).</p>}
      <ol className="mb-mission-list">
        {t.objectives.map(o => <li key={o.row} className={o.row === p.selectedObjective ? 'on' : ''}>
          <button type="button" className="mb-mission-item" onClick={() => p.onSelectObjective(o.row === p.selectedObjective ? null : o.row)}>
            <img src={objectiveIconUrl(o.secondary)} width={16} height={16} alt="" />
            <span className="mb-mission-name">{text(o.name) || `Objective ${o.row + 1}`}</span>
            {o.secondary && <span className="mb-mission-tag">secondary</span>}
            {o.subObjective && <span className="mb-mission-tag">sub</span>}
          </button>
        </li>)}
      </ol>
      {selected && <fieldset className="mb-mission-detail" disabled={p.busy}>
        <legend>{text(selected.name) || `Objective ${selected.row + 1}`}</legend>
        {selected.name && <div className="mb-act-row"><span className="lm-muted">{selected.name}</span>{t.texts[selected.name] !== undefined && <button type="button" onClick={() => void setText(selected.name!)}>Edit title</button>}</div>}
        {selected.descriptions.map(d => <div key={d} className="mb-mission-desc"><span>{text(d)}</span>{t.texts[d] !== undefined && <button type="button" title={d} onClick={() => void setText(d)}>Edit</button>}</div>)}
        {selected.location ? <div className="mb-act-field">Location on the map
          <div className="mb-act-row">
            X <ValueField label="Objective X" value={selected.location[0]} onCommit={v => set('hudobjectives.rdr', [...selected.path, 'LOCATION'], [v, selected.location![1]], 'Move objective')} />
            Z <ValueField label="Objective Z" value={selected.location[1]} onCommit={v => set('hudobjectives.rdr', [...selected.path, 'LOCATION'], [selected.location![0], v], 'Move objective')} />
            <button type="button" onClick={() => p.onShowObjective(selected.row)}>Show</button>
          </div>
          <span className="mb-act-sub">World x and z, probably where the HUD map marks it (not checked in game). The trigger area is the mission script's and does not move with it.</span>
        </div> : <p className="me-note">No map location.</p>}
        <dl className="mb-mission-facts">
          {selected.valve && <><dt>Done when valve</dt><dd>{selected.valve}</dd></>}
          {selected.displayValve && <><dt>Shown when</dt><dd>{selected.displayValve}</dd></>}
          {selected.invalidValve && <><dt>Failed when</dt><dd>{selected.invalidValve}</dd></>}
          {selected.navPoints.length > 0 && <><dt>Nav points</dt><dd>{selected.navPoints.join(', ')}</dd></>}
          {selected.animation && <><dt>Plays</dt><dd>{selected.animation}</dd></>}
        </dl>
      </fieldset>}
    </div>

    <div className="me-card">
      <div className="me-card-title">Actors ({t.actors.length})</div>
      {t.actors.length === 0 && <p className="me-note">No actors (vehicles.rdr).</p>}
      {p.onChooseWeapon && (() => {
        const types = [...new Set(t.actors.filter(a => a.model && a.character && !isSeal(a)).map(a => a.character!))].sort();
        return types.length > 0 && <details className="mb-mission-actor"><summary>Weapons the 3D characters hold</summary>
          <p className="me-note">View only. No map file says which weapon an AI carries (the game's shared chartype tables do), so pick a stand-in per character type. SEALs hold the weapons the mission's armory enables.</p>
          <div className="mb-mission-grid">{types.map(type => <label key={type}>{type}
            <select value={p.actorWeapons?.[type] ?? ''} onChange={e => p.onChooseWeapon!(type, e.target.value)}>
              <option value="">None</option>
              {(p.weaponModels ?? []).map(w => <option key={w} value={w}>{w.slice(w.indexOf(':') + 1)}</option>)}
            </select></label>)}</div>
        </details>;
      })()}
      {t.actors.map(a => <details key={a.row} className="mb-mission-actor">
        <summary><b>{a.display || a.name}</b> <span className="lm-muted">{a.name}{a.character ? ` · ${a.character}` : ''}{a.model ? ` · ${a.model.slice(a.model.indexOf(':') + 1)}` : ''}</span></summary>
        <dl className="mb-mission-facts">
          {a.starts.length > 0 && <><dt>Starts at</dt><dd>{a.starts.map(s => `${s.point}${s.value != null ? ` (${s.value})` : ''}`).join(', ')}</dd></>}
          {a.teams.length > 0 && <><dt>Teams</dt><dd>{a.teams.join(', ')}</dd></>}
          {a.flags.length > 0 && <><dt>Flags</dt><dd>{a.flags.map(f => f.values.length === 1 && f.values[0] === 1 ? f.name : `${f.name} ${f.values.join(' ')}`).join(', ')}</dd></>}
        </dl>
        {a.ai && a.ai.length > 0 && <fieldset className="mb-mission-grid" disabled={p.busy}><legend>AI</legend>
          {a.ai.map(f => <label key={f.name}>{pretty(f.name)}
            {typeof f.value === 'number' ? <ValueField label={`${a.name} ${f.name}`} value={f.value} width={70}
              onCommit={v => set('vehicles.rdr', [...a.path, 'ai_params', f.name], [v], `Set ${a.name} ${f.name}`)} /> : <span>{String(f.value)}</span>}
          </label>)}
        </fieldset>}
      </details>)}
    </div>

    {t.mission && <div className="me-card">
      <div className="me-card-title">Mission settings</div>
      <fieldset className="mb-mission-grid" disabled={p.busy}>
        {t.mission.satchelTimer != null && <label>Satchel timer (s, -1 off)<ValueField label="Satchel timer" value={t.mission.satchelTimer} width={70} onCommit={v => set('mission.rdr', ['SATCHEL_TIMER'], [v], 'Set satchel timer')} /></label>}
        {t.mission.ai.map(f => <label key={f.name}>{pretty(f.name)}
          {typeof f.value === 'number' ? <ValueField label={f.name} value={f.value} width={70} onCommit={v => set('mission.rdr', ['ai_params', f.name], [v], `Set ${f.name}`)} /> : <span>{String(f.value)}</span>}
        </label>)}
      </fieldset>
      {weapons.length > 0 && <fieldset className="mb-mission-weapons" disabled={p.busy}><legend>Weapons available</legend>
        {weapons.map(w => <label key={w.index}><input type="checkbox" checked={w.value === 1}
          onChange={e => set('mission.rdr', ['Valves', w.index, 'VALUE'], [e.target.checked ? 1 : 0], `${e.target.checked ? 'Enable' : 'Disable'} ${w.name}`)} />
          {w.name!.replace(/^Enable_?/, '').replace(/^Equip/, '')}</label>)}
      </fieldset>}
      {otherValves.length > 0 && <details><summary>Other start values ({otherValves.length})</summary><fieldset className="mb-mission-grid" disabled={p.busy}>
        {otherValves.map(v => <label key={v.index}>{v.name}{typeof v.value === 'number'
          ? <ValueField label={v.name ?? ''} value={v.value} width={70} onCommit={n => set('mission.rdr', ['Valves', v.index, 'VALUE'], [n], `Set ${v.name}`)} /> : <span>{String(v.value)}</span>}</label>)}
      </fieldset></details>}
    </div>}

    {(t.world || t.extents) && <div className="me-card">
      <div className="me-card-title">World</div>
      {t.world && <>
        <div className="mb-mission-swatches">
          <span><i style={{ background: rgb(t.world.ambient) }} /> Ambient</span>
          {t.world.lights.map(l => <span key={l.name} className={l.active ? '' : 'lm-muted'}><i style={{ background: rgb(l.rgb) }} /> {pretty(l.name)}</span>)}
          {t.world.fog && <span className={t.world.fog.enabled ? '' : 'lm-muted'}><i style={{ background: rgb(t.world.fog.rgb) }} /> Fog {t.world.fog.range?.join('–')}</span>}
        </div>
        <dl className="mb-mission-facts">
          {t.world.clip && <><dt>Draw distance</dt><dd>{t.world.clip.join(' – ')} units</dd></>}
          {t.world.metersPerUnit != null && <><dt>Metres per unit</dt><dd>{t.world.metersPerUnit}</dd></>}
          {t.world.defaultMaterial && <><dt>Ground material</dt><dd>{t.world.defaultMaterial}</dd></>}
          {t.world.nightMission != null && <><dt>Night mission</dt><dd>{t.world.nightMission ? 'yes' : 'no'}</dd></>}
        </dl>
      </>}
      {t.extents?.min && t.extents.max && <dl className="mb-mission-facts"><dt>Map extents</dt><dd>{t.extents.min.map(v => v.toFixed(0)).join(', ')} → {t.extents.max.map(v => v.toFixed(0)).join(', ')}</dd></dl>}
      <p className="me-note">Read-only: the map's compiled settings (in the map's own .ZED) may be what the game uses. Open {t.world?.record ?? 'the table'} in the Asset Viewer to change the source values.</p>
      {t.world && <button type="button" onClick={() => host.openMissionTable(p.path, t.world!.record)}>Open {t.world.record}</button>}
    </div>}

    {t.cams.length > 0 && <details className="me-card"><summary>Mission cameras ({t.cams.length})</summary>
      <ul className="mb-mission-cams">{t.cams.map((c, i) => <li key={i}><b>{c.id}</b> {c.description && <span>{text(c.description)}</span>} {c.animation && <span className="lm-muted">plays {c.animation}</span>}</li>)}</ul>
    </details>}
  </div>;
}
