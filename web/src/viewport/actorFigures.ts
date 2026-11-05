// Which actors (vehicles.rdr) the Map Builder draws as characters at their AIMAPS start points, facing which way,
// posed with which clip and holding which weapon. Pure, so it is tested without WebGL; mapViewport draws the result.
//
// Weapons: each actor holds its chartype's own weapon (the server reads the default weapons from the game's chartype
// table, READERC.ZAR > character.rdr, and answers the first gun the package has as `weapon`), unless the user picked
// another for that character type (view only). Without a chartype table (no READERC.ZAR beside the package or on the
// disc) the stand-ins of before: SEALs take the mission armory's weapons (mission.rdr Enable_*) in turn, others nothing.

export interface ActorLike {
  name: string | null;
  character: string | null;
  /** "archive:name" of the character model, when the package has it. */
  model?: string | null;
  starts: ReadonlyArray<{ point: string; value?: number | string | null }>;
  teams?: ReadonlyArray<number>;
  /** The Stand clip of its chartype's animation set (animset.rdr), when known. */
  stand?: string | null;
  /** The chartype's default weapons (null: no chartype table, or the type is not in it). */
  weapons?: ReadonlyArray<{ name: string; model: string | null }> | null;
  /** "archive:name" of the weapon the chartype holds, or null. */
  weapon?: string | null;
}

/** Where a figure's weapon came from: its chartype's loadout, the user's pick, the armory stand-in, or nothing. */
export type WeaponSource = 'loadout' | 'picked' | 'stand-in' | 'none';

export interface FigurePlan {
  /** The AIMAPS point the actor stands at (its first start). */
  point: string;
  actor: string;
  character: string;
  model: string;
  clip: string | null;
  /** "archive:name" of the weapon model, or null. */
  weapon: string | null;
  weaponSource: WeaponSource;
  /** The start's heading, 0-7 eighths of a turn clockwise from north (-Z); null when the setup gives none. */
  heading: number | null;
  /** [x, z] from the point when the figure stands with the squad instead of at its own start, else null. */
  offset: [number, number] | null;
  /** The actor's own start point when it is drawn with the squad (it is far from the player's), else null. */
  withTeam: string | null;
}

/** The loadout name of a weapon model ("9mm Sub" for WEAP_MDL.ZED:uzi), else the model's own name. */
export function weaponLabel(model: string | null, actor?: Pick<ActorLike, 'weapons'>): string {
  if (!model) { return 'nothing'; }
  return actor?.weapons?.find(w => w.model === model)?.name ?? model.slice(model.indexOf(':') + 1);
}

/** A SEAL is drawn with SEAL clips and the armory's weapons. */
export const isSeal = (a: Pick<ActorLike, 'model' | 'character'>) => /seal/i.test(a.model ?? '') || /seal|jungle_[a-d]/i.test(a.character ?? '');

const norm = (s: string) => s.toLowerCase().replace(/[^a-z0-9]/g, '');
// Valve names (Enable_<x>) that do not spell the weapon model's name.
const ALIASES: Record<string, string> = {
  m4a1sd: 'm4acarbinesd', m4a1: 'm4acarbine', m16203: 'm16m203', m4acarbine203: 'm4acarbine203', mcmillanm87: 'mcmillanm87',
  barretlightm82a1: 'barretm82a1', 870: 'remington870', spas: 'spas12', fnp: 'fnp90', steyr: 'steyraug9', jack: 'jackhammer',
  m60e: 'm60e', m63a: 'stonerm63a', m14gun: 'm14gun', sigcommando: 'sigcommando', stonersr25sd: 'stonersr25', ak105: 'ak105',
};

/** The weapon model ("archive:name") an armory valve such as Enable_mp5sd or Enable_M4A1_SD stands for, or null. */
export function weaponForValve(valve: string, weaponModels: ReadonlyArray<string>): string | null {
  let key = norm(valve).replace(/^enable/, '').replace(/^equip/, '');
  key = ALIASES[key] ?? key;
  if (!key) { return null; }
  const byName = weaponModels.map(m => ({ m, n: norm(m.slice(m.indexOf(':') + 1)) }));
  return byName.find(w => w.n === key)?.m ?? byName.find(w => key.length >= 3 && w.n.startsWith(key))?.m ?? null;
}

/** The first clip of `wanted` the package has. */
export function pickClip(wanted: ReadonlyArray<string>, available: ReadonlyArray<string>): string | null {
  const have = new Map(available.map(c => [c.toLowerCase(), c]));
  for (const w of wanted) { const hit = have.get(w.toLowerCase()); if (hit) { return hit; } }
  return null;
}

export const SEAL_CLIPS = ['seal_stand', 'seal_p_stand', 'seal_stand_alert01'];
// Without the game's animation sets: armed actors idle like the Guard set does (seal_stand), unarmed ones in civ_stand.
export const GUARD_CLIPS = ['seal_stand', 'guard_stand02', 'guard_stand', 'civ_stand'];
export const UNARMED_CLIPS = ['civ_stand', 'guard_stand02', 'seal_stand'];
// AUX_GZAN's upright armed idle and its alternate both have a rifle track under rightHand.
const CA_ARMED_CLIPS = ['guard_stand', 'seal_stand'];

const COMPASS = ['N', 'NE', 'E', 'SE', 'S', 'SW', 'W', 'NW'];
/** A start's heading (vehicles.rdr setup: 0-7, eighths of a turn clockwise from north, -Z) as a compass word. */
export const compassOf = (heading: number | null | undefined) =>
  heading == null || !Number.isFinite(heading) ? '?' : COMPASS[((Math.round(heading) % 8) + 8) % 8];
/** The figure's yaw about +Y (radians) for a heading: the models face north (-Z) unturned, and turn clockwise. */
export const yawOf = (heading: number | null | undefined) => heading == null || !Number.isFinite(heading) ? 0 : -heading * Math.PI / 4;

/** A squad mate whose own start is further than this (units, about 14 m) from the player's stands with the team. */
export const SQUAD_RADIUS = 150;
// Where squad mates stand around the player (right, forward), in units: beside and behind, as a team forms up.
const SLOTS: Array<[number, number]> = [[7, -4], [-7, -4], [0, -9], [7, -13], [-7, -13], [0, -18]];

/** A point the map has, with its position. */
export interface PointLike { name: string; x: number; z: number }

/**
 * One figure per actor with a model and a start point the map has (points matched case-insensitively), standing at its
 * first start, turned to that start's heading and posed by its animation set's Stand clip (`stand`, else a fallback).
 * The SEAL squad starts together: a squad mate whose own start is far from the player's stands beside the player.
 * On a multiplayer map (`multiplayer`) the `player` actor is only the template for human players, who spawn at the
 * game's team spawns, so it is not drawn. Each holds `chosen[character]` when the user picked one ("none": nothing),
 * else its loadout's weapon; actors without a loadout fall back to the stand-ins (SEALs: the armory's weapons in turn).
 */
export function planFigures(actors: ReadonlyArray<ActorLike>, points: ReadonlyArray<PointLike>, opts: {
  clips: ReadonlyArray<string>; armory: ReadonlyArray<string>; chosen: Readonly<Record<string, string>>; multiplayer?: boolean; ca?: boolean;
}): FigurePlan[] {
  const pointNames = new Map(points.map(p => [p.name.toLowerCase(), p]));
  const used = new Set<string>();
  const out: FigurePlan[] = [];
  let seal = 0;
  const startOf = (a: ActorLike) => a.starts.map(s => ({ s, p: pointNames.get(s.point.toLowerCase()) }))
    .find((x): x is { s: ActorLike['starts'][number]; p: PointLike } => !!x.p && !used.has(x.p.name.toLowerCase()));
  const player = opts.multiplayer ? null : actors.find(a => a.name?.toLowerCase() === 'player' && a.model);
  const playerStart = player ? startOf(player) : undefined;
  let slot = 0;
  for (const a of actors) {
    if (!a.model || !a.character) { continue; }
    if (opts.multiplayer && a.name?.toLowerCase() === 'player') { continue; }
    let start = a === player ? playerStart : startOf(a);
    let offset: [number, number] | null = null, withTeam: string | null = null;
    const squadMate = a !== player && !!playerStart && isSeal(a) && (a.teams ?? []).includes(1);
    if (squadMate && (!start || Math.hypot(start.p.x - playerStart!.p.x, start.p.z - playerStart!.p.z) > SQUAD_RADIUS) && slot < SLOTS.length) {
      withTeam = start?.p.name ?? a.starts[0]?.point ?? null;
      const heading = num(playerStart!.s.value);
      const [right, forward] = SLOTS[slot++];
      const t = (heading ?? 0) * Math.PI / 4;
      // Forward is (sin t, -cos t) on x, z (north is -Z); right is (cos t, sin t).
      offset = [right * Math.cos(t) + forward * Math.sin(t), right * Math.sin(t) - forward * Math.cos(t)];
      start = { s: { point: playerStart!.p.name, value: playerStart!.s.value }, p: playerStart!.p };
    }
    if (!start) { continue; }
    if (!offset) { used.add(start.p.name.toLowerCase()); }
    const sealActor = isSeal(a);
    const pick = opts.chosen[a.character];
    let weapon: string | null = null, weaponSource: WeaponSource = 'none';
    if (pick) { weapon = pick === 'none' ? null : pick; weaponSource = weapon ? 'picked' : 'none'; }
    else if (a.weapons) { weapon = a.weapon ?? null; weaponSource = weapon ? 'loadout' : 'none'; }
    else if (sealActor && opts.armory.length) { weapon = opts.armory[seal++ % opts.armory.length]; weaponSource = 'stand-in'; }
    const fallback = opts.ca && weapon ? CA_ARMED_CLIPS : sealActor ? SEAL_CLIPS : a.weapons && !a.weapon ? UNARMED_CLIPS : GUARD_CLIPS;
    out.push({ point: start.p.name, actor: a.name ?? a.character, character: a.character, model: a.model,
      clip: pickClip(a.stand ? [a.stand, ...fallback] : fallback, opts.clips), weapon, weaponSource,
      heading: num(start.s.value), offset, withTeam });
  }
  return out;
}

const num = (v: unknown): number | null => typeof v === 'number' && Number.isFinite(v) ? v : typeof v === 'string' && v.trim() !== '' && Number.isFinite(Number(v)) ? Number(v) : null;
