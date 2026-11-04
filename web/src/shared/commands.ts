// The command search (F3): every menu item of the page in front as a searchable list. Matching is by words in
// order, each as a prefix or a run of letters in the label ("sv cp" finds "Save copy…"), ranked by how early and how
// tightly the letters fall.

export interface Command {
  label: string;
  // Where it lives, shown beside it ("Edit", "View").
  group: string;
  shortcut?: string;
  run: () => void;
  disabled?: boolean;
}

export interface MenuLike {
  label: string;
  // A label may be worked out when shown (the Model Editor's "Export to <game>").
  items: Array<{ label: string | (() => string); shortcut?: string; action?: () => void; disabled?: () => boolean; separator?: boolean }>;
}

/** The menus' runnable items as commands (separators and plain notes left out). */
export function commandsFromMenus(menus: MenuLike[]): Command[] {
  return menus.flatMap(m => m.items.filter(i => !i.separator && i.action).map(i => ({
    label: typeof i.label === 'function' ? i.label() : i.label, group: m.label, shortcut: i.shortcut, run: i.action!, disabled: i.disabled?.() ?? false,
  })));
}

/**
 * A match score for `query` against `text` (higher is better), or null when it does not match. Each query word must
 * appear in order as a subsequence; a word starting at a word start or matching contiguously scores more.
 */
export function matchScore(query: string, text: string): number | null {
  const words = query.toLowerCase().split(/\s+/).filter(Boolean);
  if (!words.length) { return 0; }
  const t = text.toLowerCase();
  let at = 0, score = 0;
  for (const w of words) {
    // Prefer a word start, then any contiguous run, then letters spread out.
    const starts = [...t.matchAll(/(^|[^a-z0-9])([a-z0-9])/g)].map(m => (m.index ?? 0) + m[1].length).filter(i => i >= at);
    const atStart = starts.find(i => t.startsWith(w, i));
    if (atStart !== undefined) { score += 30 - Math.min(20, atStart); at = atStart + w.length; continue; }
    const run = t.indexOf(w, at);
    if (run >= 0) { score += 15 - Math.min(10, run); at = run + w.length; continue; }
    let i = at, gaps = 0;
    for (const ch of w) {
      const j = t.indexOf(ch, i);
      if (j < 0) { return null; }
      gaps += j - i;
      i = j + 1;
    }
    score += Math.max(1, 10 - gaps);
    at = i;
  }
  return score;
}

/** The commands matching `query`, best first (enabled before disabled, then by score, then as listed). */
export function searchCommands(commands: Command[], query: string, limit = 40): Command[] {
  return commands
    .map((c, i) => ({ c, i, s: matchScore(query, `${c.label} ${c.group}`) }))
    .filter((x): x is { c: Command; i: number; s: number } => x.s !== null)
    .sort((a, b) => Number(a.c.disabled ?? false) - Number(b.c.disabled ?? false) || b.s - a.s || a.i - b.i)
    .slice(0, limit)
    .map(x => x.c);
}

/** A distance for the measure tool: game units, and about how many metres (a SEAL stands about 19 units). */
export function formatDistance(units: number): string {
  return `${units.toFixed(units < 10 ? 2 : 1)} units ≈ ${(units / 10.5).toFixed(units < 105 ? 2 : 1)} m`;
}
