// The Map Builder's minimap: SOCOM II's HUD radar (the compass disc, view cone and blips of RUN\COMMON\HUD2_TXR.ZED)
// over the map's own tactical map, or a top-down render where there is none. Pure: what to draw and where, so it is
// tested without a canvas; components/Minimap.tsx draws it. Format notes: docs/formats/hud.md.
//
// World to radar: north (-Z) is up and +X right when the radar is not turned (the tactical maps are drawn that way).
// Like the game's radar, it turns with the view so that where the camera looks is always up.

import { ACTION_KINDS, type ActionKind } from '../shared/actionIcons';

/** HUD2 texture names (without .tif) a blip is drawn with. */
export type HudIcon = 'ret_friendly' | 'ret_able' | 'ret_bravo' | 'ret_threat' | 'ret_hostage' | 'ret_bomb' | 'ret_objective'
  | 'ret_extraction' | 'ret_base' | 'ret_triangle' | `ret_nav_${string}`;

export type BlipKind = 'friendly' | 'enemy' | 'hostage' | 'objective' | 'extraction' | 'nav' | 'spawn' | 'action';

export interface Blip {
  kind: BlipKind;
  x: number;
  z: number;
  label: string;
  /** The HUD texture it is drawn with; null for actions (no HUD art: a small square in the action's colour). */
  icon: HudIcon | null;
  /** Tint for white HUD art (friendlies, spawns) or the colour of an action square. */
  tint?: string;
  /** Objectives and nav points stay on the rim when they are out of range, as the game's nav marker does. */
  pinToRim?: boolean;
  /** Secondary objectives draw dimmer. */
  dim?: boolean;
}

export interface MinimapInputs {
  points: ReadonlyArray<{ name: string; team: number; kind: string; x: number; z: number }>;
  actors?: ReadonlyArray<{ name: string | null; character: string | null; model?: string | null; starts: ReadonlyArray<{ point: string }> }>;
  objectives?: ReadonlyArray<{ row: number; name: string | null; location: readonly [number, number] | number[] | null; secondary: boolean }>;
  texts?: Readonly<Record<string, string>>;
  actions?: ReadonlyArray<{ kind: ActionKind; node: string; x: number; z: number }>;
}

export const FRIENDLY_TINT = '#7fe0a0';
export const SPAWN_TINT = '#c8c8c8';
const TEAM_TINTS: Record<number, string> = { 1: '#7fb8ff', 2: '#ff8a7a' };

/** The nav blip for a point name: ret_nav_01 is C … ret_nav_24 is Z (A and B are the fireteams, ret_able / ret_bravo). */
export function navIcon(name: string): HudIcon | null {
  const letter = name.trim().charAt(0).toUpperCase();
  if (letter === 'A') { return 'ret_able'; }
  if (letter === 'B') { return 'ret_bravo'; }
  const n = letter.charCodeAt(0) - 'C'.charCodeAt(0) + 1;
  return n >= 1 && n <= 24 ? `ret_nav_${String(n).padStart(2, '0')}` : null;
}

const isSealActor = (a: { model?: string | null; character: string | null }) =>
  /seal/i.test(a.model ?? '') || /seal|jungle_[a-d]/i.test(a.character ?? '');

/** Every blip the minimap draws for a map: actors at their start points, spare start points, nav points, objectives, actions. */
export function minimapBlips(input: MinimapInputs): Blip[] {
  const byName = new Map(input.points.map(p => [p.name.toLowerCase(), p]));
  const blips: Blip[] = [];
  const claimed = new Set<string>();
  for (const a of input.actors ?? []) {
    const start = a.starts.map(s => byName.get(s.point.toLowerCase())).find(p => p && !claimed.has(p.name.toLowerCase()));
    if (!start) { continue; }
    claimed.add(start.name.toLowerCase());
    const label = a.name ?? a.character ?? start.name;
    if (isSealActor(a)) {
      const team = /^able/i.test(start.name) ? 'ret_able' : /^bravo/i.test(start.name) ? 'ret_bravo' : 'ret_friendly';
      blips.push({ kind: 'friendly', x: start.x, z: start.z, label, icon: team, tint: FRIENDLY_TINT });
    } else if (/hostage|informant|civ|vip|escort/i.test(`${a.character ?? ''} ${a.name ?? ''}`)) {
      blips.push({ kind: 'hostage', x: start.x, z: start.z, label, icon: 'ret_hostage', tint: '#ffe08a' });
    } else {
      blips.push({ kind: 'enemy', x: start.x, z: start.z, label, icon: 'ret_threat' });
    }
  }
  for (const p of input.points) {
    if (claimed.has(p.name.toLowerCase())) { continue; }
    if (p.kind === 'waypoint') {
      blips.push({ kind: 'nav', x: p.x, z: p.z, label: p.name, icon: navIcon(p.name) ?? 'ret_triangle', pinToRim: true });
    } else if (p.kind === 'start' || p.kind === 'spawn') {
      blips.push({ kind: 'spawn', x: p.x, z: p.z, label: p.name, icon: 'ret_base', tint: TEAM_TINTS[p.team] ?? SPAWN_TINT });
    }
  }
  for (const o of input.objectives ?? []) {
    if (!o.location || o.location.length < 2) { continue; }
    const label = (o.name && input.texts?.[o.name]) || o.name || `Objective ${o.row + 1}`;
    const extraction = /extract/i.test(label);
    blips.push({ kind: extraction ? 'extraction' : 'objective', x: o.location[0], z: o.location[1], label,
      icon: extraction ? 'ret_extraction' : 'ret_objective', pinToRim: !o.secondary, dim: o.secondary });
  }
  // Objectives often share a location (Meet Mallard and Remain Unseen): one blip each place, the first one's label.
  const seen = new Set<string>();
  const kept = blips.filter(b => {
    if (b.kind !== 'objective' && b.kind !== 'extraction') { return true; }
    const key = `${Math.round(b.x)},${Math.round(b.z)}`;
    if (seen.has(key)) { return false; }
    seen.add(key);
    return true;
  });
  for (const a of input.actions ?? []) {
    kept.push({ kind: 'action', x: a.x, z: a.z, label: `${ACTION_KINDS[a.kind].label}: ${a.node}`, icon: null, tint: ACTION_KINDS[a.kind].color });
  }
  return kept;
}

/** Action markers (actionMarkers) reduced to minimap positions: their anchor, else the middle of the props they mark. */
export function actionBlipsFrom(markers: ReadonlyArray<{ kind: ActionKind; node: string; instances: number[]; anchor?: [number, number, number] }>,
  instances: ReadonlyArray<{ position: number[] }>): Array<{ kind: ActionKind; node: string; x: number; z: number }> {
  const out: Array<{ kind: ActionKind; node: string; x: number; z: number }> = [];
  for (const m of markers) {
    if (m.anchor) { out.push({ kind: m.kind, node: m.node, x: m.anchor[0], z: m.anchor[2] }); continue; }
    const at = m.instances.map(i => instances[i]?.position).filter((p): p is number[] => !!p && p.length >= 3);
    if (!at.length) { continue; }
    out.push({ kind: m.kind, node: m.node, x: at.reduce((s, p) => s + p[0], 0) / at.length, z: at.reduce((s, p) => s + p[2], 0) / at.length });
  }
  return out;
}

/** The camera's heading in radians, clockwise from north (-Z), from its forward direction. */
export function headingOf(forwardX: number, forwardZ: number): number {
  if (Math.abs(forwardX) < 1e-9 && Math.abs(forwardZ) < 1e-9) { return 0; }
  return Math.atan2(forwardX, -forwardZ);
}

/**
 * A world point on the radar, in pixels from its centre (y down), for a radar centred at (cx, cz) turned to `heading`
 * and showing `range` world units from the centre to the rim of a radar `radius` pixels across.
 */
export function toRadar(x: number, z: number, cx: number, cz: number, heading: number, range: number, radius: number): [number, number] {
  const s = radius / range;
  const dx = (x - cx) * s, dz = (z - cz) * s;
  const c = Math.cos(-heading), n = Math.sin(-heading);
  return [dx * c - dz * n, dx * n + dz * c];
}

/** Pulls a radar point onto the rim (inset by `inset` pixels) when it lies outside; null when it is inside already. */
export function pinToRim(px: number, py: number, radius: number, inset: number): [number, number] | null {
  const d = Math.hypot(px, py);
  const r = radius - inset;
  if (d <= r) { return null; }
  return [px / d * r, py / d * r];
}

/** World rectangle of an image: x at the left / right edges, z at the top / bottom edges. */
export interface ImagePlacement { minX: number; maxX: number; minZ: number; maxZ: number }

/** Where a world point falls on an image placed at `p` (pixels, y down). */
export function worldToImage(x: number, z: number, p: ImagePlacement, width: number, height: number): [number, number] {
  return [(x - p.minX) / (p.maxX - p.minX) * width, (z - p.minZ) / (p.maxZ - p.minZ) * height];
}

/** A square placement around a box with a margin, for the top-down render. */
export function squareAround(minX: number, maxX: number, minZ: number, maxZ: number, margin = 0.02): ImagePlacement {
  const cx = (minX + maxX) / 2, cz = (minZ + maxZ) / 2;
  const half = Math.max(maxX - minX, maxZ - minZ, 1) / 2 * (1 + margin);
  return { minX: cx - half, maxX: cx + half, minZ: cz - half, maxZ: cz + half };
}

/** Range steps (world units from centre to rim) the wheel moves through. ~10.5 units are a metre. */
export const MINIMAP_RANGES = [300, 500, 800, 1200, 1800, 2700, 4000];
export const DEFAULT_MINIMAP_RANGE = 1200;

export function stepRange(range: number, zoomIn: boolean): number {
  const i = MINIMAP_RANGES.findIndex(r => r >= range);
  const at = i < 0 ? MINIMAP_RANGES.length - 1 : i;
  return MINIMAP_RANGES[Math.max(0, Math.min(MINIMAP_RANGES.length - 1, at + (zoomIn ? -1 : 1)))];
}
