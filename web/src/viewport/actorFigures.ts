// Which actors (vehicles.rdr) the Map Builder draws as characters at their AIMAPS start points, posed with which clip
// and holding which stand-in weapon. Pure, so it is tested without WebGL; mapViewport draws the result.
//
// Weapons are stand-ins: no file this editor reads says which weapon an actor carries (AI weapons come from the game's
// shared chartype tables, READERC.ZAR). SEALs take the weapons the mission's armory enables (mission.rdr Enable_*),
// one each in turn; other actors take what the user picked for their character type, or nothing.

export interface ActorLike {
  name: string | null;
  character: string | null;
  /** "archive:name" of the character model, when the package has it. */
  model?: string | null;
  starts: ReadonlyArray<{ point: string }>;
}

export interface FigurePlan {
  /** The AIMAPS point the actor stands at (its first start). */
  point: string;
  actor: string;
  character: string;
  model: string;
  clip: string | null;
  /** "archive:name" of the weapon model, or null. */
  weapon: string | null;
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
export const GUARD_CLIPS = ['guard_stand02', 'guard_stand', 'guard_stand03', 'civ_stand'];

/**
 * One figure per actor with a model and a start point the map has (points matched case-insensitively). SEALs hold the
 * armory's enabled weapons in turn; others hold `chosen[character]` ("" or missing: nothing).
 */
export function planFigures(actors: ReadonlyArray<ActorLike>, points: ReadonlyArray<string>, opts: {
  clips: ReadonlyArray<string>; armory: ReadonlyArray<string>; chosen: Readonly<Record<string, string>>;
}): FigurePlan[] {
  const pointNames = new Map(points.map(p => [p.toLowerCase(), p]));
  const used = new Set<string>();
  const out: FigurePlan[] = [];
  let seal = 0;
  for (const a of actors) {
    if (!a.model || !a.character) { continue; }
    const point = a.starts.map(s => pointNames.get(s.point.toLowerCase())).find((p): p is string => !!p && !used.has(p));
    if (!point) { continue; }
    used.add(point);
    const sealActor = isSeal(a);
    const weapon = sealActor ? (opts.armory.length ? opts.armory[seal++ % opts.armory.length] : null) : (opts.chosen[a.character] || null);
    out.push({ point, actor: a.name ?? a.character, character: a.character, model: a.model,
      clip: pickClip(sealActor ? SEAL_CLIPS : GUARD_CLIPS, opts.clips), weapon });
  }
  return out;
}
