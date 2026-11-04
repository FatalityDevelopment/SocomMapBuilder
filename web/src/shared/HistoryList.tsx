// The edit history as a list: undo steps (most recent first) above the current state, redo steps below it. Clicking a
// step goes back (or forward) to just after it, through that many undos (or redos).

import { useEffect, useState } from 'react';

export interface HistoryStep { description: string; archives: string[] }
export interface History { revision: number; undo: HistoryStep[]; redo: HistoryStep[]; dirty: string[] }

interface HistoryListProps {
  // The session's history (ASSET_HISTORY), reread whenever `revision` changes.
  load: () => Promise<History>;
  revision: number | null | undefined;
  busy: boolean;
  // Undo `undo` steps or redo `redo` steps, one at a time.
  onJump: (undo: number, redo: number) => void;
}

export function HistoryList({ load, revision, busy, onJump }: HistoryListProps) {
  const [history, setHistory] = useState<History | null>(null);
  useEffect(() => {
    let live = true;
    if (revision == null) { setHistory(null); return; }
    void load().then(h => { if (live) { setHistory(h); } }).catch(() => { if (live) { setHistory(null); } });
    return () => { live = false; };
  }, [revision]);
  if (!history) { return <div className="lm-muted me-note">No session.</div>; }
  const { undo, redo } = history;
  return (
    <ol className="hist-list">
      {/* Oldest at the bottom of the undo stack: list redo (future) on top, newest last, as a timeline reads down. */}
      {[...redo].reverse().map((step, i) => (
        <li key={`r${i}`} className="hist-step hist-redo" title={`Redo to here (${redo.length - i} step${redo.length - i === 1 ? '' : 's'}): ${step.archives.join(', ')}`}>
          <button type="button" disabled={busy} onClick={() => onJump(0, redo.length - i)}>{step.description}</button>
        </li>
      ))}
      <li className="hist-now">● now (revision {history.revision})</li>
      {undo.map((step, i) => (
        <li key={`u${i}`} className="hist-step" title={`Undo back to before this (${i + 1} step${i ? 's' : ''}): ${step.archives.join(', ')}`}>
          <button type="button" disabled={busy} onClick={() => onJump(i + 1, 0)}>{step.description}</button>
        </li>
      ))}
      {!undo.length && !redo.length && <li className="lm-muted me-note">No edits yet.</li>}
    </ol>
  );
}
