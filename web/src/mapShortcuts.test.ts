import { describe, expect, it } from 'vitest';
import { matchShortcut } from './mapShortcuts';

function event(key: string, fields: Partial<KeyboardEvent> = {}): KeyboardEvent {
  return { key, code: '', ctrlKey: false, metaKey: false, shiftKey: false, altKey: false, ...fields } as KeyboardEvent;
}

describe('map shortcuts after session and key extraction', () => {
  it('distinguishes undo and redo with both Ctrl and Cmd', () => {
    for (const modifier of [{ ctrlKey: true }, { metaKey: true }]) {
      expect(matchShortcut(event('z', modifier))).toBe('undo');
      expect(matchShortcut(event('Z', { ...modifier, shiftKey: true }))).toBe('redo');
      expect(matchShortcut(event('y', modifier))).toBe('redo');
    }
  });
  it('keeps the shortcut reference and placement cancellation available', () => {
    expect(matchShortcut(event('?', { shiftKey: true }))).toBe('help');
    expect(matchShortcut(event('Escape'))).toBe('escape');
    expect(matchShortcut(event('p'))).toBe('toolPlace');
    expect(matchShortcut(event('p', { ctrlKey: true }))).toBeNull();
  });
});
