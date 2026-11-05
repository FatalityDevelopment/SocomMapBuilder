// What each kind of map object action (actions.rdr `type`) means, and the icon that stands for it. The Actions panel
// and the 3D markers above props both draw these, so one kind always has one icon.

// AIRSTRIKE is a SWITCH row with the helicopter prompt (MP61, MP62, MP73 and MP82 call air strikes from a radio), told
// apart so it has its own icon. TURRET is not an actions.rdr row at all: the game gives every placed turret model
// (TURR, TRMG, TLRM, TLMG, TLLM and T30M archives; MANUAL in READERC.ZAR › ai_turrets.rdr) its own mount and dismount
// prompts (action_mount_turret.tif / action_dismount_turret.tif).
export type ActionKind = 'DOOR' | 'SWITCH' | 'ITEM' | 'SATCHEL' | 'MPBOMB' | 'DEFUSE' | 'ACT_BREACH' | 'AIRSTRIKE' | 'TURRET' | 'ANIM' | 'OTHER';

export interface ActionKindInfo {
  kind: ActionKind;
  /** Short plain-language name. */
  label: string;
  /** One sentence on what the player does. */
  description: string;
  color: string;
}

export const ACTION_KINDS: Record<ActionKind, ActionKindInfo> = {
  DOOR: { kind: 'DOOR', label: 'Door', description: 'The player opens, closes or kicks a door.', color: '#3e8bff' },
  SWITCH: { kind: 'SWITCH', label: 'Switch', description: 'The player flips a switch or lever that sets a mission valve.', color: '#e5a50a' },
  ITEM: { kind: 'ITEM', label: 'Pick up', description: 'The player picks up or uses an item (intel, a key, a crate).', color: '#46a758' },
  SATCHEL: { kind: 'SATCHEL', label: 'Plant charge', description: 'The player plants a satchel charge on the object.', color: '#e5484d' },
  MPBOMB: { kind: 'MPBOMB', label: 'Bomb (multiplayer)', description: 'A multiplayer demolition bomb site: plant or carry the bomb.', color: '#d6409f' },
  DEFUSE: { kind: 'DEFUSE', label: 'Defuse', description: 'The player defuses a bomb or charge.', color: '#12a594' },
  ACT_BREACH: { kind: 'ACT_BREACH', label: 'Breach', description: 'The player breaches (blows open) a door or wall.', color: '#f76b15' },
  AIRSTRIKE: { kind: 'AIRSTRIKE', label: 'Air strike', description: 'The player radios in an air strike: a switch with the helicopter prompt that plays the strike animation.', color: '#6e56cf' },
  TURRET: { kind: 'TURRET', label: 'Turret', description: 'A mounted gun. The game offers Mount and Dismount on every placed turret model by itself; it needs no action row.', color: '#5b7a2e' },
  ANIM: { kind: 'ANIM', label: 'Animation', description: 'No prompt type: the row only plays map animations or sets a valve.', color: '#8e8c99' },
  OTHER: { kind: 'OTHER', label: 'Other', description: 'A type this editor does not know yet.', color: '#8e8c99' },
};

/** The named types seen on the retail disc, in the order the panel offers them. */
export const ACTION_TYPES: ActionKind[] = ['DOOR', 'SWITCH', 'ITEM', 'SATCHEL', 'MPBOMB', 'DEFUSE', 'ACT_BREACH'];

/** What the panel offers for a row: the named types plus presets written as one of them. */
export const ACTION_CHOICES: ActionKind[] = [...ACTION_TYPES, 'AIRSTRIKE'];

/** The actions.rdr `type` a preset is written as. */
export const ACTION_KIND_TYPE: Partial<Record<ActionKind, string>> = { AIRSTRIKE: 'SWITCH' };

/** The HUD prompt (HUD_TXR.ZED) each kind shows by default, as the retail rows use them. */
export const DEFAULT_PROMPT: Partial<Record<ActionKind, string>> = {
  DOOR: 'action_door_open.tif', SWITCH: 'action_pull_lever.tif', ITEM: 'action_pickup_item.tif', SATCHEL: 'action_MP_Bomb.tif',
  MPBOMB: 'action_MP_bomb.tif', DEFUSE: 'action_defuse.tif', AIRSTRIKE: 'action_helo.tif',
};

/** Plain names for the HUD prompt pictures (HUD_TXR.ZED `action_*`), by lower-case file name. */
export const PROMPT_NAMES: Record<string, string> = {
  'action_door_open.tif': 'Open door', 'action_door_close.tif': 'Close door', 'action_pull_lever.tif': 'Pull lever',
  'action_pickup_item.tif': 'Pick up item', 'action_pickup_item1.tif': 'Pick up item (2)', 'action_pickup_item2.tif': 'Pick up item (3)',
  'action_place_c4.tif': 'Place C4', 'action_mp_bomb.tif': 'Bomb', 'action_drop_mp_bomb.tif': 'Drop bomb', 'action_defuse.tif': 'Defuse',
  'action_helo.tif': 'Air strike (helicopter)', 'action_mount_turret.tif': 'Mount turret', 'action_dismount_turret.tif': 'Dismount turret',
  'action_climb.tif': 'Climb', 'action_slide.tif': 'Slide', 'action_restrain.tif': 'Restrain', 'action_conceal_body.tif': 'Conceal body',
  'action_close_combat.tif': 'Close combat', 'action_knife.tif': 'Knife', 'action_escortee.tif': 'Escort', 'action_talk.tif': 'Talk',
  'action_ok_talk.tif': 'Can talk', 'action_ok_talk2.tif': 'Can talk (2)', 'action_no_talk2.tif': 'Cannot talk', 'action_channel_busy.tif': 'Channel busy',
  'action_dead_zone.tif': 'Dead zone', 'action_tcm.tif': 'TCM', 'action_victory_dance.tif': 'Victory dance',
  'action_tune_team.tif': 'Radio: team', 'action_tune_offense.tif': 'Radio: offense', 'action_tune_defense.tif': 'Radio: defense',
  'action_tune_spectators.tif': 'Radio: spectators', 'action_button.tif': 'Button', 'action_x.tif': 'X button', 'action_circle.tif': 'Circle button',
  'action_square.tif': 'Square button', 'action_triangle.tif': 'Triangle button',
};

/** A prompt picture's plain name, else its file name tidied. */
export function promptName(bitmap: string): string {
  return PROMPT_NAMES[bitmap.toLowerCase()] ?? bitmap.replace(/^action_/i, '').replace(/\.tif$/i, '').replace(/_/g, ' ');
}

/** The kind of an actions.rdr row (its fields as the server sends them). */
export function actionKind(fields: Record<string, ReadonlyArray<string | number>> | undefined): ActionKind {
  const type = String(fields?.type?.[0] ?? '').trim().toUpperCase();
  if (!type) { return 'ANIM'; }
  if (type === 'SWITCH' && String(fields?.bitmap?.[0] ?? '').toLowerCase() === 'action_helo.tif') { return 'AIRSTRIKE'; }
  return (ACTION_TYPES as string[]).includes(type) ? type as ActionKind : 'OTHER';
}

type Ctx = CanvasRenderingContext2D;

// The white glyph of each kind, drawn in a 0..1 box.
const GLYPHS: Record<ActionKind, (c: Ctx) => void> = {
  DOOR: c => {
    c.strokeRect(0.28, 0.16, 0.44, 0.7);
    c.beginPath(); c.moveTo(0.28, 0.16); c.lineTo(0.52, 0.24); c.lineTo(0.52, 0.94); c.lineTo(0.28, 0.86); c.closePath(); c.fill();
    c.beginPath(); c.arc(0.46, 0.55, 0.035, 0, Math.PI * 2); c.save(); c.fillStyle = '#000'; c.globalAlpha = 0.5; c.fill(); c.restore();
  },
  SWITCH: c => {
    c.strokeRect(0.26, 0.5, 0.48, 0.3);
    c.beginPath(); c.moveTo(0.5, 0.62); c.lineTo(0.68, 0.22); c.stroke();
    c.beginPath(); c.arc(0.68, 0.22, 0.08, 0, Math.PI * 2); c.fill();
  },
  ITEM: c => {
    c.beginPath(); c.moveTo(0.5, 0.18); c.lineTo(0.8, 0.33); c.lineTo(0.5, 0.48); c.lineTo(0.2, 0.33); c.closePath(); c.fill();
    c.beginPath(); c.moveTo(0.2, 0.33); c.lineTo(0.2, 0.68); c.lineTo(0.5, 0.84); c.lineTo(0.8, 0.68); c.lineTo(0.8, 0.33);
    c.moveTo(0.5, 0.48); c.lineTo(0.5, 0.84); c.stroke();
  },
  SATCHEL: c => {
    c.fillRect(0.24, 0.36, 0.52, 0.42);
    c.beginPath(); c.moveTo(0.36, 0.36); c.quadraticCurveTo(0.5, 0.1, 0.64, 0.36); c.stroke();
    c.save(); c.fillStyle = '#000'; c.globalAlpha = 0.45; c.fillRect(0.3, 0.48, 0.4, 0.06); c.restore();
  },
  MPBOMB: c => {
    c.beginPath(); c.arc(0.46, 0.58, 0.24, 0, Math.PI * 2); c.fill();
    c.beginPath(); c.moveTo(0.6, 0.38); c.quadraticCurveTo(0.68, 0.2, 0.8, 0.22); c.stroke();
    for (const [x, y] of [[0.84, 0.12], [0.9, 0.24], [0.76, 0.12]]) { c.beginPath(); c.arc(x, y, 0.025, 0, Math.PI * 2); c.fill(); }
  },
  DEFUSE: c => {
    c.beginPath(); c.arc(0.32, 0.7, 0.1, 0, Math.PI * 2); c.stroke();
    c.beginPath(); c.arc(0.68, 0.7, 0.1, 0, Math.PI * 2); c.stroke();
    c.beginPath(); c.moveTo(0.38, 0.62); c.lineTo(0.7, 0.18); c.moveTo(0.62, 0.62); c.lineTo(0.3, 0.18); c.stroke();
  },
  ACT_BREACH: c => {
    c.beginPath();
    for (let k = 0; k < 16; k++) {
      const a = (k / 16) * Math.PI * 2 - Math.PI / 2, r = k % 2 ? 0.16 : 0.34;
      const x = 0.5 + Math.cos(a) * r, y = 0.52 + Math.sin(a) * r;
      if (k) { c.lineTo(x, y); } else { c.moveTo(x, y); }
    }
    c.closePath(); c.fill();
  },
  AIRSTRIKE: c => {
    // A jet seen from above: fuselage, swept wings and tail.
    c.beginPath(); c.moveTo(0.5, 0.1); c.lineTo(0.56, 0.3); c.lineTo(0.9, 0.56); c.lineTo(0.9, 0.64); c.lineTo(0.56, 0.52); c.lineTo(0.55, 0.74);
    c.lineTo(0.68, 0.86); c.lineTo(0.68, 0.92); c.lineTo(0.5, 0.86); c.lineTo(0.32, 0.92); c.lineTo(0.32, 0.86); c.lineTo(0.45, 0.74);
    c.lineTo(0.44, 0.52); c.lineTo(0.1, 0.64); c.lineTo(0.1, 0.56); c.lineTo(0.44, 0.3); c.closePath(); c.fill();
  },
  TURRET: c => {
    // A machine gun on a tripod.
    c.fillRect(0.3, 0.36, 0.3, 0.14);
    c.beginPath(); c.moveTo(0.6, 0.43); c.lineTo(0.92, 0.43); c.moveTo(0.3, 0.42); c.lineTo(0.14, 0.5); c.stroke();
    c.beginPath(); c.moveTo(0.44, 0.5); c.lineTo(0.44, 0.62); c.lineTo(0.24, 0.9); c.moveTo(0.44, 0.62); c.lineTo(0.64, 0.9);
    c.moveTo(0.44, 0.62); c.lineTo(0.44, 0.92); c.stroke();
  },
  ANIM: c => {
    c.beginPath(); c.moveTo(0.36, 0.24); c.lineTo(0.76, 0.5); c.lineTo(0.36, 0.76); c.closePath(); c.fill();
  },
  OTHER: c => {
    // Fonts under a tiny scale round badly: draw the mark at 100× and scale back.
    c.save(); c.scale(0.01, 0.01);
    c.font = 'bold 70px sans-serif'; c.textAlign = 'center'; c.textBaseline = 'middle'; c.fillText('?', 50, 56);
    c.restore();
  },
};

/** Draws a kind's icon (a coloured disc with a white glyph) filling a size × size square at (x, y). */
export function drawActionIcon(c: Ctx, kind: ActionKind, size: number, x = 0, y = 0, selected = false): void {
  const info = ACTION_KINDS[kind];
  c.save();
  c.translate(x, y);
  c.scale(size, size);
  c.beginPath(); c.arc(0.5, 0.5, 0.46, 0, Math.PI * 2);
  c.fillStyle = info.color; c.fill();
  c.lineWidth = selected ? 0.08 : 0.05; c.strokeStyle = selected ? '#ffd040' : 'rgba(0,0,0,0.65)'; c.stroke();
  c.fillStyle = '#fff'; c.strokeStyle = '#fff'; c.lineWidth = 0.07; c.lineCap = 'round'; c.lineJoin = 'round';
  // The glyph sits in the middle 70% of the disc.
  c.translate(0.15, 0.15); c.scale(0.7, 0.7);
  GLYPHS[kind](c);
  c.restore();
}

/** A canvas holding the kind's icon (for a three.js texture). */
export function actionIconCanvas(kind: ActionKind, size = 64, selected = false): HTMLCanvasElement {
  const canvas = document.createElement('canvas');
  canvas.width = canvas.height = size;
  const c = canvas.getContext('2d');
  if (c) { drawActionIcon(c, kind, size, 0, 0, selected); }
  return canvas;
}

const urls = new Map<string, string>();
/** The kind's icon as a data URL (for an <img>), made once per kind. */
export function actionIconUrl(kind: ActionKind): string {
  let url = urls.get(kind);
  if (!url) {
    try { url = actionIconCanvas(kind, 48).toDataURL('image/png'); } catch { url = ''; }
    urls.set(kind, url);
  }
  return url;
}

// HUD objectives (hudobjectives.rdr) have a flag: gold for primary, silver-blue for secondary.
const OBJECTIVE_COLORS = { primary: '#f5c400', secondary: '#9fb6cc' };

/** Draws a HUD objective's flag filling a size × size square at (x, y). */
export function drawObjectiveIcon(c: Ctx, secondary: boolean, size: number, x = 0, y = 0, selected = false): void {
  c.save();
  c.translate(x, y);
  c.scale(size, size);
  c.lineCap = 'round'; c.lineJoin = 'round';
  // Pole down to the ground point (the bottom middle).
  c.strokeStyle = 'rgba(0,0,0,0.7)'; c.lineWidth = 0.12;
  c.beginPath(); c.moveTo(0.5, 0.98); c.lineTo(0.5, 0.06); c.stroke();
  c.strokeStyle = '#fff'; c.lineWidth = 0.06;
  c.beginPath(); c.moveTo(0.5, 0.98); c.lineTo(0.5, 0.06); c.stroke();
  c.beginPath(); c.moveTo(0.5, 0.08); c.lineTo(0.94, 0.24); c.lineTo(0.5, 0.42); c.closePath();
  c.fillStyle = secondary ? OBJECTIVE_COLORS.secondary : OBJECTIVE_COLORS.primary; c.fill();
  c.lineWidth = selected ? 0.07 : 0.04; c.strokeStyle = selected ? '#ffd040' : 'rgba(0,0,0,0.7)'; c.stroke();
  if (selected) { c.beginPath(); c.ellipse(0.5, 0.95, 0.22, 0.05, 0, 0, Math.PI * 2); c.strokeStyle = '#ffd040'; c.lineWidth = 0.05; c.stroke(); }
  c.restore();
}

export function objectiveIconCanvas(secondary: boolean, selected = false, size = 64): HTMLCanvasElement {
  const canvas = document.createElement('canvas');
  canvas.width = canvas.height = size;
  const c = canvas.getContext('2d');
  if (c) { drawObjectiveIcon(c, secondary, size, 0, 0, selected); }
  return canvas;
}

export function objectiveIconUrl(secondary: boolean): string {
  const key = secondary ? 'objective:2' : 'objective:1';
  let url = urls.get(key);
  if (!url) {
    try { url = objectiveIconCanvas(secondary, false, 48).toDataURL('image/png'); } catch { url = ''; }
    urls.set(key, url);
  }
  return url;
}
