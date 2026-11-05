// The Actions panel's rules, kept apart from the UI so they can be tested: what each actions.rdr field means, the
// defaults a new action starts with, inline checks, and how the map's actions group by kind.

import type { ActionFields, MapActions } from '../webview/model-editor/asset-client';
import { ACTION_CHOICES, ACTION_KIND_TYPE, DEFAULT_PROMPT, actionKind, type ActionKind } from '../shared/actionIcons';

/** Fields holding one whole number. */
export const NUMBER_FIELDS = ['range', 'elevation', 'team', 'points'] as const;

/** Plain-language names and hints for every field the panel edits. */
export const FIELD_HELP: Record<string, { label: string; hint: string }> = {
  type: { label: 'What the player does', hint: 'The prompt the game shows near the object.' },
  bitmap: { label: 'Prompt icon', hint: 'The HUD picture shown with the prompt (HUD_TXR.ZED).' },
  descriptor: { label: 'Prompt text', hint: 'Optional words shown with the prompt, e.g. TAKE ITEM or AIRSTRIKE ON WHISKEY.' },
  range: { label: 'Reach distance', hint: 'How close the player must be, in map units (about 10.5 to a metre). Retail doors use 30.' },
  elevation: { label: 'Height allowance', hint: 'Optional: how far above or below the object the player may stand.' },
  valve: { label: 'Mission switch (valve)', hint: 'The named mission state this action sets. Objectives and scripts watch it.' },
  anim: { label: 'Map animations', hint: 'Scripts in MZANIM.ZAR played when the action runs (e.g. the door swing, then the kick).' },
  team: { label: 'Team', hint: 'Optional: only this team can use it (multiplayer).' },
  points: { label: 'Points', hint: 'Optional: score for using it (multiplayer).' },
  one_shot: { label: 'Works only once', hint: 'After it is used once the prompt goes away.' },
  tcm: { label: 'TCM flag', hint: 'Set on every retail air strike (MP61, MP62, MP73, MP82) and nothing else; its exact meaning is not known yet.' },
};

/** Sensible starting values for a new action of this kind on the node `name`, from what the map has. */
export function defaultsFor(kind: ActionKind, data: Pick<MapActions, 'icons' | 'animations' | 'valves' | 'rows'>, name: string): ActionFields {
  const fields: ActionFields = {};
  if (kind === 'TURRET') { return fields; }
  if (kind !== 'ANIM' && kind !== 'OTHER') { fields.type = [ACTION_KIND_TYPE[kind] ?? kind]; }
  if (kind !== 'ANIM') { fields.range = [30]; }
  // Copy what another row of the same kind on this map uses (its icon and animations), as the retail rows agree.
  const like = data.rows.find(r => actionKind(r) === kind);
  const icon = knownIcon(data, like?.bitmap?.[0]) ?? knownIcon(data, DEFAULT_PROMPT[kind]);
  if (icon) { fields.bitmap = [icon]; }
  const anims = like?.anim?.map(String).filter(a => data.animations.includes(a));
  if (anims?.length) { fields.anim = anims; }
  else if (kind === 'DOOR' && data.animations.includes('singleDoor_left')) { fields.anim = ['singleDoor_left']; }
  else if (kind === 'AIRSTRIKE') {
    const strike = data.animations.find(a => /airstrike/i.test(a));
    if (strike) { fields.anim = [strike]; }
  }
  // Retail air strikes (MP61, MP62, MP73, MP82) show their target as prompt text and carry the tcm flag.
  if (kind === 'AIRSTRIKE') { fields.descriptor = [String(like?.descriptor?.[0] ?? 'AIRSTRIKE')]; fields.tcm = []; }
  // Retail doors use a valve named like the door (door01 sets door01); a valve can be renamed after.
  if (name && kind !== 'ANIM') { fields.valve = [name]; }
  return fields;
}

/** The map's own spelling of a HUD prompt picture, when the map has it. */
function knownIcon(data: Pick<MapActions, 'icons'>, icon: string | number | undefined): string | undefined {
  return icon === undefined ? undefined : data.icons.find(i => i.toLowerCase() === String(icon).toLowerCase());
}

/** The fields after choosing another kind for a row: the type it is written as, and the prompt picture when it was
 *  the old kind's own (a door's open-door picture follows it to a lever; a picture chosen by hand stays). */
export function withKind(fields: ActionFields, from: ActionKind, to: ActionKind, data: Pick<MapActions, 'icons'>): ActionFields {
  const next = { ...fields };
  if (to === 'ANIM') { delete next.type; } else if (to !== 'OTHER' && to !== 'TURRET') { next.type = [ACTION_KIND_TYPE[to] ?? to]; }
  const bitmap = String(next.bitmap?.[0] ?? '').toLowerCase();
  if (!bitmap || bitmap === String(DEFAULT_PROMPT[from] ?? '').toLowerCase()) {
    const icon = knownIcon(data, DEFAULT_PROMPT[to]);
    if (icon) { next.bitmap = [icon]; } else if (to === 'ANIM' || bitmap) { delete next.bitmap; }
  }
  if (from === 'AIRSTRIKE' && to !== 'AIRSTRIKE') { delete next.tcm; }
  if (to === 'AIRSTRIKE') { next.tcm = []; if (!next.descriptor?.length) { next.descriptor = ['AIRSTRIKE']; } }
  return next;
}

export interface ActionCheck { errors: Record<string, string>; warnings: Record<string, string> }

/** Inline checks for the editor: errors block saving, warnings explain what may not work in game. */
export function checkAction(fields: ActionFields, data: Pick<MapActions, 'icons' | 'animations' | 'valves' | 'rows'>): ActionCheck {
  const errors: Record<string, string> = {}, warnings: Record<string, string> = {};
  for (const key of NUMBER_FIELDS) {
    const v = fields[key]?.[0];
    if (v === undefined || v === '') { continue; }
    const n = Number(v);
    if (!Number.isInteger(n) || n < -2147483648 || n > 2147483647) { errors[key] = 'Use a whole number.'; }
    else if (key === 'range' && n <= 0) { errors[key] = 'The reach must be more than 0.'; }
  }
  for (const key of ['valve', 'bitmap', 'descriptor', 'type'] as const) {
    const v = String(fields[key]?.[0] ?? '');
    if (v && !/^[ -~]+$/.test(v)) { errors[key] = 'Use plain letters, digits and punctuation (ASCII).'; }
  }
  if (fields.anim?.some(a => !/^[ -~]+$/.test(String(a)))) { errors.anim = 'Use plain letters, digits and punctuation (ASCII).'; }
  const hasContent = ['type', 'valve', 'anim', 'bitmap', 'descriptor'].some(k => (fields[k]?.length ?? 0) > 0 && String(fields[k][0]).trim() !== '');
  if (!hasContent) { errors.type = 'Choose what the player does, or give it a valve or an animation.'; }
  const valve = String(fields.valve?.[0] ?? '');
  const knownValves = new Set([...data.valves, ...data.rows.flatMap(r => (r.valve ?? []).map(String))]);
  if (valve && !knownValves.has(valve)) { warnings.valve = `“${valve}” is not in this map's valves.rdr yet: add it there for the game to track it.`; }
  const unknown = (fields.anim ?? []).map(String).filter(a => a.trim() && data.animations.length && !data.animations.includes(a.trim()));
  if (unknown.length) { warnings.anim = `Not in this map's MZANIM.ZAR: ${unknown.join(', ')}.`; }
  const bitmap = String(fields.bitmap?.[0] ?? '');
  if (bitmap && data.icons.length && !data.icons.some(i => i.toLowerCase() === bitmap.toLowerCase())) { warnings.bitmap = `No HUD picture named ${bitmap} in this map.`; }
  if (actionKind(fields) === 'OTHER') { warnings.type = `${String(fields.type?.[0])} is not a type seen on the retail disc.`; }
  return { errors, warnings };
}

/** The map's action rows grouped by kind (in the panel's order), each with its row number. */
export function groupRows(rows: ReadonlyArray<ActionFields>): Array<{ kind: ActionKind; rows: Array<{ row: number; node: string }> }> {
  const order: ActionKind[] = [...ACTION_CHOICES, 'ANIM', 'OTHER'];
  const groups = new Map<ActionKind, Array<{ row: number; node: string }>>();
  rows.forEach((r, row) => {
    const kind = actionKind(r);
    const list = groups.get(kind) ?? [];
    list.push({ row, node: String(r.node?.[0] ?? '?') });
    groups.set(kind, list);
  });
  return order.filter(k => groups.has(k)).map(kind => ({ kind, rows: groups.get(kind)! }));
}

/** The fields as the mapAction op takes them: numbers as numbers, blank values dropped, animations trimmed. */
export function cleanFields(fields: ActionFields, node: string): ActionFields {
  const out: ActionFields = { node: [node] };
  for (const [key, values] of Object.entries(fields)) {
    if (key === 'node') { continue; }
    if (key === 'one_shot' || key === 'tcm') { out[key] = []; continue; }
    if ((NUMBER_FIELDS as readonly string[]).includes(key)) {
      const v = values[0];
      if (v !== undefined && String(v).trim() !== '') { out[key] = [Number(v)]; }
      continue;
    }
    if (key === 'anim') {
      const list = values.map(v => String(v).trim()).filter(Boolean);
      if (list.length) { out.anim = list; }
      continue;
    }
    const v = String(values[0] ?? '').trim();
    if (v) { out[key] = [v]; }
  }
  return out;
}
