// Gear and models carried on bones in the Animation Editor, and which of them the game shows as a clip plays: motion.rdr
// runs ZANIM callbacks at times into a clip (show_cigarette at 0.05 s turns the cig / cigarette objects on), and what
// one clip leaves shown or hidden carries into the next. Pure, so it is tested without WebGL.

/** A clip's motion.rdr row: flags, values and the ZANIM callbacks the game runs at times into it. */
export interface ClipMotionData {
  flags: string[];
  values: Record<string, number>;
  callbacks: Array<{ effect: string; time: number; found: boolean; shows: string[]; hides: string[]; spawns: string[] }>;
}

/**
 * A model carried on a bone: worn gear (character.rdr's gear table: hats, eyes, holster, the cigarette) or one the
 * user attached to the clip. Placed at bone world x translate(position) x rotate(rotation, degrees). `objects` are
 * the ZANIM object names the game shows / hides it by (motion.rdr callbacks); `hiddenAtStart` keeps it out of sight
 * until a callback shows it.
 */
export interface AttachmentSpec {
  id: string;
  label: string;
  kind: 'gear' | 'user';
  model: string;
  bone: string;
  position: number[];
  rotation: number[];
  visible: boolean;
  objects?: string[];
  hiddenAtStart?: boolean;
}

/** Whether a ZANIM object name stands for an attachment (its name or model, or the start of its name: cig / cigarette). */
export function objectMatches(obj: string, a: Pick<AttachmentSpec, 'label' | 'objects'>): boolean {
  const o = obj.toLowerCase();
  return [a.label, ...(a.objects ?? [])].some(n => { const k = n.toLowerCase(); return k === o || (o.length >= 3 && k.startsWith(o)); });
}

/**
 * Which attachments the game shows at `time` into a clip: each starts as `carry` says (what the previous clip left),
 * except one the clip itself toggles, which starts the other way round from its first toggle; then every callback up
 * to `time` applies.
 */
export function gameVisibility(
  attachments: ReadonlyArray<Pick<AttachmentSpec, 'id' | 'label' | 'objects'>>,
  callbacks: ClipMotionData['callbacks'], time: number, carry: ReadonlyMap<string, boolean>,
): Map<string, boolean> {
  const out = new Map<string, boolean>();
  const sorted = [...callbacks].sort((a, b) => a.time - b.time);
  for (const a of attachments) {
    let state = carry.get(a.id) ?? true;
    const first = sorted.find(c => c.shows.some(o => objectMatches(o, a)) || c.hides.some(o => objectMatches(o, a)));
    if (first) { state = !first.shows.some(o => objectMatches(o, a)); }
    for (const c of sorted) {
      if (c.time > time + 1e-6) { break; }
      if (c.shows.some(o => objectMatches(o, a))) { state = true; }
      if (c.hides.some(o => objectMatches(o, a))) { state = false; }
    }
    out.set(a.id, state);
  }
  return out;
}
