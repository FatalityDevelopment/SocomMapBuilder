// The Map Builder's window key handler: matches SHORTCUTS and runs the page's action for it.

import { useEffect, useRef } from 'react';
import { activePage, modalOpen } from './shared';
import { matchShortcut, type ShortcutId } from './mapShortcuts';

// An action that returns false leaves the key to the browser. Read through a ref so the listener is installed once.
export type MapKeyActions = Record<ShortcutId, () => boolean | void>;

export function useMapKeys(actions: MapKeyActions) {
  const actionsRef = useRef(actions);
  actionsRef.current = actions;
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      // Only the page in front takes keys, and none while a prompt or the file browser is open.
      if (activePage() !== 'map' || modalOpen()) { return; }
      const target = e.target as HTMLElement;
      if (target && ['INPUT', 'SELECT', 'TEXTAREA'].includes(target.tagName)) { return; }
      const id = matchShortcut(e);
      if (id && actionsRef.current[id]() !== false) { e.preventDefault(); }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, []);
}
