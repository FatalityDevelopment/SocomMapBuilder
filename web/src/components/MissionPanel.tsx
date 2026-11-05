// The Mission tab: the map's mission tables (READERM.ZAR) in plain words. HUD objectives (their flags stand on the
// map in 3D) with their text and location, the actors who start on the map and their AI, the mission's settings and
// weapon switches, and the world's lighting and fog (read-only). Values change in place with the mapTable edit,
// objective text with mapText; the Asset Viewer's RDR editor reaches everything else.

import { useEffect, useState } from 'react';
import type { MapTables, TablePath } from '../webview/model-editor/asset-client';
import { host } from '../shared';
import { objectiveIconUrl } from '../shared/actionIcons';
import { compassOf, weaponLabel, type FigurePlan } from '../viewport/actorFigures';

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
  /** Weapon models ("archive:name") and the weapon picked per character type for the 3D figures ("none", or missing: the loadout's). */
  weaponModels?: string[];
  actorWeapons?: Record<string, string>;
  /** How the 3D view draws each actor (where, facing, clip), by actor name. */
  figures?: FigurePlan[];
  /** A multiplayer map: its `player` actor is the template for human players. */
  multiplayer?: boolean;
  onChooseWeapon?: (chartype: string, source: string) => void;
  /** A CA mission can change its existing actor starts, weapon slots and valve values. */
  ca?: boolean;
  pointNames?: string[];
  readOnly?: boolean;
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
  const locked = p.busy || !!p.readOnly || t?.revision !== p.revision;
  // Keep the current CA mission's open sections in the DOM while the next revision loads.
  if (!t || (!p.ca && t.revision !== p.revision)) { return <div className="me-note">{p.error || 'Reading mission tables…'}</div>; }
  if (!t.hasReader) {
    return <div className="me-card mb-act-empty"><b>This package has no mission reader.</b> Objectives, actors and mission settings live in READERM.ZAR, which mission and multiplayer maps carry.</div>;
  }
  const text = (key: string | null | undefined) => (key && t.texts[key]) || key || '';
  const canEditText = (key: string) => !p.ca && !p.readOnly && t.texts[key] !== undefined;
  const set = (table: string, path: TablePath, values: Array<number | string>, label: string) =>
    void p.edit(label, { op: 'mapTable', revision: t.revision, mission: t.missionId, table, path, values, label });
  const setText = async (key: string) => {
    const r = await host.prompt('Change the text', [{ key: 'text', label: key, value: t.texts[key] ?? '' }], 'Save');
    const next = r?.text?.trim();
    if (next && next !== t.texts[key]) { void p.edit('Edit mission text', { op: 'mapText', revision: t.revision, key, text: next, label: 'Edit mission text' }); }
  };
  const weapons = t.mission?.valves.filter(v => v.name?.startsWith('Enable')) ?? [];
  const otherValves = t.mission?.valves.filter(v => !v.name?.startsWith('Enable')) ?? [];
  const selected = t.objectives.find(o => o.row === p.selectedObjective) ?? null;
  const setWeaponHash = async (index: number, hash: number) => {
    const result = await host.prompt('Replace weapon by hash', [{
      key: 'hash', label: 'Weapon hash (hex)', value: hash.toString(16).padStart(8, '0'), pattern: /^[a-f0-9]{8}$/i,
      hint: 'Enter eight hexadecimal digits.'
    }], 'Replace');
    if (result) { set('mission.rdr', ['Weapons', index], [parseInt(result.hash, 16)], 'Replace mission weapon'); }
  };

  return <div className="mb-mission">
    <div className="me-card">
      <div className="me-card-title">Mission</div>
      {t.mission?.description && t.texts[t.mission.description] && <p className="mb-mission-lead">{t.texts[t.mission.description]}</p>}
      {t.mission?.briefing && <p className="me-note" title={t.mission.briefing}>{text(t.mission.briefing)}</p>}
      {p.ca && <p className="me-note">Text comes from the message keys. Unidentified valves show their hashes.</p>}
      <p className="me-note">{t.objectives.length} objective(s), {t.actors.length} actor(s), {t.valves.length} valve(s). Flags on the map mark objective locations (View › Objective flags).</p>
    </div>

    <div className="me-card">
      <div className="me-card-title">Objectives ({t.objectives.length})</div>
      {t.objectives.length === 0 && <p className="me-note">{p.ca
        ? 'This mission has no named primary or secondary objective areas.'
        : 'This map has no HUD objectives (hudobjectives.rdr).'}</p>}
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
        {selected.name && <div className="mb-act-row"><span className="lm-muted">{selected.name}</span>{canEditText(selected.name) && <button type="button" onClick={() => void setText(selected.name!)}>Edit title</button>}</div>}
        {selected.descriptions.map(d => <div key={d} className="mb-mission-desc"><span>{text(d)}</span>{canEditText(d) && <button type="button" title={d} onClick={() => void setText(d)}>Edit</button>}</div>)}
        {selected.location ? <div className="mb-act-field">Location on the map
          <div className="mb-act-row">
            X <ValueField label="Objective X" disabled={locked || p.ca} value={selected.location[0]} onCommit={v => set('hudobjectives.rdr', [...selected.path, 'LOCATION'], [v, selected.location![1]], 'Move objective')} />
            Z <ValueField label="Objective Z" disabled={locked || p.ca} value={selected.location[1]} onCommit={v => set('hudobjectives.rdr', [...selected.path, 'LOCATION'], [selected.location![0], v], 'Move objective')} />
            <button type="button" onClick={() => p.onShowObjective(selected.row)}>Show</button>
          </div>
          <span className="mb-act-sub">{p.ca ? 'Centre of the named mission area.' : "World x and z, probably where the HUD map marks it (not checked in game). The trigger area is the mission script's and does not move with it."}</span>
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
      {t.actors.length > 0 && <p className="me-note">{p.ca && t.loadoutSource
        ? <>Weapons are each character type's own ({t.loadoutSource}).</>
        : t.loadoutSource
        ? <>Weapons are each character type's own (the game's chartype table, {t.loadoutSource}). That table is shared by every map, so the map cannot change them; a pick below only changes what the 3D character holds here.</>
        : <>No chartype table (READERC.ZAR beside the package or on the SOCOM II disc) was found, so the 3D characters hold stand-ins: SEALs the armory's weapons, others what you pick.</>}</p>}
      {t.actors.map(a => {
        const pick = a.character ? p.actorWeapons?.[a.character] : undefined;
        const held = pick ? (pick === 'none' ? null : pick) : a.weapon;
        const own = (a.weapons ?? []).filter(w => w.model);
        const others = (p.weaponModels ?? []).filter(m => !own.some(w => w.model === m));
        return <details key={a.row} className="mb-mission-actor">
        <summary><b>{text(a.display) || a.name}</b> <span className="lm-muted">{a.name}{a.character ? ` · ${a.character}` : ''}{a.model ? ` · ${a.model.slice(a.model.indexOf(':') + 1)}` : ''}</span>
          {a.model && <span className="lm-muted"> · holds {weaponLabel(held ?? null, a)}{pick ? ' (picked)' : ''}</span>}</summary>
        <dl className="mb-mission-facts">
          {a.weapons && <><dt>Weapons</dt><dd>{a.weapons.length ? a.weapons.map(w => w.name).join(', ') : 'none'}</dd></>}
          {a.model && a.character && p.onChooseWeapon && <><dt>Holds in 3D</dt><dd>
            <select aria-label={`Weapon ${a.name ?? a.character} holds in 3D`} value={pick ?? ''} onChange={e => p.onChooseWeapon!(a.character!, e.target.value)}>
              <option value="">{a.weapons ? `Its own: ${weaponLabel(a.weapon, a)}` : 'Stand-in'}</option>
              <option value="none">Nothing</option>
              {own.length > 0 && <optgroup label="Its loadout">{own.map(w => <option key={w.model!} value={w.model!}>{w.name}</option>)}</optgroup>}
              <optgroup label="Other weapons">{others.map(w => <option key={w} value={w}>{w.slice(w.indexOf(':') + 1)}</option>)}</optgroup>
            </select>{pick && <span className="lm-muted"> for every {a.character}</span>}</dd></>}
          {a.starts.length > 0 && <><dt>Starts at</dt><dd title="The number after each point is its heading: eighths of a turn clockwise from north">
            {p.ca ? a.starts.map((s, i) => <div key={i} className="mb-act-row">
              <select aria-label={`${a.name} start ${i + 1}`} value={s.point} disabled={locked}
                onChange={e => set('vehicles.rdr', [a.row, 'starts', i], [e.target.value, Number(s.value)], `Change ${a.name} start`)}>
                {!p.pointNames?.includes(s.point) && <option>{s.point}</option>}
                {p.pointNames?.map(point => <option key={point}>{point}</option>)}
              </select>
              <label>Heading <ValueField label={`${a.name} heading ${i + 1}`} value={s.value} disabled={locked}
                onCommit={v => set('vehicles.rdr', [a.row, 'starts', i], [s.point, v], `Change ${a.name} heading`)} /></label>
              <span>{compassOf(Number(s.value))}</span>
            </div>) : a.starts.map(s => `${s.point}${s.value != null ? `, faces ${compassOf(Number(s.value))} (${s.value})` : ''}`).join('; ')}
            {a.starts.length > 1 && <span className="lm-muted"> (alternatives; the 3D view uses the first)</span>}</dd></>}
          {(() => {
            const f = p.figures?.find(x => x.actor === (a.name ?? a.character));
            if (p.multiplayer && a.name?.toLowerCase() === 'player') {
              return <><dt>In 3D</dt><dd className="lm-muted">Not drawn: on a multiplayer map this is the template for human players, who spawn at the game's team spawns.</dd></>;
            }
            return <>
              {(a.stand || f?.clip) && <><dt>Stands</dt><dd>{f?.clip ?? a.stand}{a.animSet ? <span className="lm-muted"> ({a.animSet})</span> : null}</dd></>}
              {f?.withTeam && <><dt>In 3D</dt><dd className="lm-muted">Drawn beside the player: the SEAL team starts together, and its own start {f.withTeam} is far from the player's.</dd></>}
            </>;
          })()}
          {a.teams.length > 0 && <><dt>Teams</dt><dd>{a.teams.join(', ')}</dd></>}
          {a.flags.length > 0 && <><dt>Flags</dt><dd>{a.flags.map(f => f.values.length === 1 && f.values[0] === 1 ? f.name : `${f.name} ${f.values.join(' ')}`).join(', ')}</dd></>}
        </dl>
        {a.ai && a.ai.length > 0 && <fieldset className="mb-mission-grid" disabled={locked}><legend>AI</legend>
          {a.ai.map(f => <label key={f.name}>{pretty(f.name)}
            {typeof f.value === 'number' ? <ValueField label={`${a.name} ${f.name}`} value={f.value} width={70}
              onCommit={v => set('vehicles.rdr', [...a.path, 'ai_params', f.name], [v], `Set ${a.name} ${f.name}`)} /> : <span>{String(f.value)}</span>}
          </label>)}
        </fieldset>}
      </details>;
      })}
    </div>

    {t.mission && <div className="me-card">
      <div className="me-card-title">Mission settings</div>
      {t.mission.mapImage && <p className="me-note">Map screen: {t.mission.mapImage}</p>}
      {!!t.mission.weaponSlots?.length && <details><summary>Allowed weapon slots ({t.mission.weaponSlots.length})</summary>
        {t.mission.weaponSlots.map(slot => <div key={slot.index} className="mb-act-row">
          <label>Slot {slot.index + 1} <select aria-label={`Weapon slot ${slot.index + 1}`} disabled={locked} value={slot.hash}
            onChange={e => set('mission.rdr', ['Weapons', slot.index], [Number(e.target.value)], 'Replace mission weapon')}>
            {!t.mission?.weaponChoices?.some(w => w.hash === slot.hash) && <option value={slot.hash}>{slot.name}</option>}
            {t.mission?.weaponChoices?.map(w => <option key={w.hash} value={w.hash}>{w.name}</option>)}
          </select></label>
          <button type="button" disabled={locked} onClick={() => void setWeaponHash(slot.index, slot.hash)}>Enter hash…</button>
        </div>)}</details>}
      {!t.mission.weaponSlots && t.mission.weapons && t.mission.weapons.length > 0 && <details><summary>Weapons the mission allows ({t.mission.weapons.length})</summary>
        <p className="me-note">{t.mission.weapons.join(', ')}</p></details>}
      <fieldset className="mb-mission-grid" disabled={locked}>
        {t.mission.satchelTimer != null && <label>Satchel timer (s, -1 off)<ValueField label="Satchel timer" value={t.mission.satchelTimer} width={70} onCommit={v => set('mission.rdr', ['SATCHEL_TIMER'], [v], 'Set satchel timer')} /></label>}
        {t.mission.ai.map(f => <label key={f.name}>{pretty(f.name)}
          {typeof f.value === 'number' ? <ValueField label={f.name} value={f.value} width={70} onCommit={v => set('mission.rdr', ['ai_params', f.name], [v], `Set ${f.name}`)} /> : <span>{String(f.value)}</span>}
        </label>)}
      </fieldset>
      {weapons.length > 0 && <fieldset className="mb-mission-weapons" disabled={locked}><legend>Weapons available</legend>
        {weapons.map(w => <label key={w.index}><input type="checkbox" checked={w.value === 1}
          onChange={e => set('mission.rdr', ['Valves', w.index, 'VALUE'], [e.target.checked ? 1 : 0], `${e.target.checked ? 'Enable' : 'Disable'} ${w.name}`)} />
          {w.name!.replace(/^Enable_?/, '').replace(/^Equip/, '')}</label>)}
      </fieldset>}
      {otherValves.length > 0 && <details><summary>Other start values ({otherValves.length})</summary><fieldset className="mb-mission-grid" disabled={locked}>
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
