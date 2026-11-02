import { describe, expect, it } from 'vitest';
import { filterShortcuts, MAP_SHORTCUTS } from './ShortcutReference';

describe('shortcut search', () => {
  it('returns all shortcuts for an empty search', () => {
    expect(filterShortcuts('  ')).toEqual(MAP_SHORTCUTS);
  });
  it('matches category, action and keys, ignoring case and whitespace', () => {
    expect(filterShortcuts(' EDITING   redo ').map(row => row[2])).toEqual(['Redo']);
    expect(filterShortcuts('numpad top')).toHaveLength(1);
    expect(filterShortcuts('unknown action')).toEqual([]);
  });
});
