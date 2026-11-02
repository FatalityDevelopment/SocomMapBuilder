// The Map Builder's keys, defined once: the window key handler matches them, the menus, Help, header buttons and
// tool buttons show their labels.

import type { MapTool } from './viewport/mapViewport';

// ctrl: true needs Ctrl (or Cmd), false needs neither, undefined takes both (the numpad views).
export interface Shortcut { label: string; ctrl?: boolean; match: (e: KeyboardEvent, k: string) => boolean }

const key = (label: string, ctrl: boolean | undefined, match: Shortcut['match']): Shortcut => ({ label, ctrl, match });

// Order matters: the handler takes the first match, Ctrl combinations first (W A S D Q E fly, so tools use other keys).
export const SHORTCUTS = {
  // Ctrl+Shift+Z matches undo first, as it always has, so redo is Ctrl+Y in practice.
  undo: key('Ctrl+Z', true, (_, k) => k === 'z'),
  redo: key('Ctrl+Y', true, (e, k) => k === 'y' || (e.shiftKey && k === 'z')),
  save: key('Ctrl+S', true, (_, k) => k === 's'),
  open: key('Ctrl+O', true, (_, k) => k === 'o'),
  copy: key('Ctrl+C', true, (_, k) => k === 'c'),
  paste: key('Ctrl+V', true, (_, k) => k === 'v'),
  front: key('Numpad 1', undefined, e => e.code === 'Numpad1'),
  right: key('Numpad 3', undefined, e => e.code === 'Numpad3'),
  top: key('Numpad 7', undefined, e => e.code === 'Numpad7'),
  ortho: key('Numpad 5', undefined, e => e.code === 'Numpad5'),
  duplicate: key('Shift+D', false, (e, k) => k === 'd' && e.shiftKey),
  selectSame: key('Shift+A', false, (e, k) => k === 'a' && e.shiftKey),
  delete: key('X / Delete', false, (e, k) => k === 'x' || e.key === 'Delete'),
  drop: key('End', false, e => e.key === 'End'),
  frame: key('F', false, (_, k) => k === 'f'),
  frameAll: key('Home', false, e => e.key === 'Home'),
  toolSelect: key('V', false, (_, k) => k === 'v'),
  toolMove: key('G', false, (_, k) => k === 'g'),
  toolRotate: key('R', false, (_, k) => k === 'r'),
  toolScale: key('T', false, (_, k) => k === 't'),
  toolPlace: key('P', false, (_, k) => k === 'p'),
  shading: key('Z', false, (_, k) => k === 'z'),
  escape: key('Esc', false, e => e.key === 'Escape'),
} satisfies Record<string, Shortcut>;

export type ShortcutId = keyof typeof SHORTCUTS;

// The first shortcut the event matches, in table order.
export function matchShortcut(e: KeyboardEvent): ShortcutId | null {
  const ctrl = e.ctrlKey || e.metaKey;
  const k = e.key.toLowerCase();
  for (const [id, s] of Object.entries(SHORTCUTS) as Array<[ShortcutId, Shortcut]>) {
    if ((s.ctrl === undefined || s.ctrl === ctrl) && s.match(e, k)) { return id; }
  }
  return null;
}

export const TOOLS: Array<{ id: MapTool; label: string; key: string; glyph: string }> = [
  { id: 'select', label: 'Select (click / box drag)', key: SHORTCUTS.toolSelect.label, glyph: '⬚' },
  { id: 'move', label: 'Move', key: SHORTCUTS.toolMove.label, glyph: '✥' },
  { id: 'rotate', label: 'Rotate (yaw)', key: SHORTCUTS.toolRotate.label, glyph: '⟳' },
  { id: 'scale', label: 'Scale', key: SHORTCUTS.toolScale.label, glyph: '⤢' },
  { id: 'place', label: 'Place clutter', key: SHORTCUTS.toolPlace.label, glyph: '✚' },
];
