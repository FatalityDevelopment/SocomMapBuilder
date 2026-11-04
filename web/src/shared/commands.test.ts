import { describe, expect, it } from 'vitest';
import { commandsFromMenus, formatDistance, matchScore, searchCommands } from './commands';

const noop = () => undefined;
const menus = [
  { label: 'File', items: [{ label: 'Save copy…', shortcut: 'Ctrl+S', action: noop }, { label: '', separator: true }, { label: 'Export memory-card pack…', action: noop, disabled: () => true }] },
  { label: 'Edit', items: [{ label: 'Duplicate', action: noop }, { label: 'Drop to ground', action: noop }] },
  { label: 'Help', items: [{ label: 'Fly: W A S D' }] },
];

describe('command search', () => {
  it('lists the runnable menu items with their menu and keys', () => {
    const cmds = commandsFromMenus(menus);
    expect(cmds.map(c => c.label)).toEqual(['Save copy…', 'Export memory-card pack…', 'Duplicate', 'Drop to ground']);
    expect(cmds[0]).toMatchObject({ group: 'File', shortcut: 'Ctrl+S', disabled: false });
    expect(cmds[1].disabled).toBe(true);
  });

  it('works out labels given as functions (the Model Editor menus)', () => {
    const cmds = commandsFromMenus([{ label: 'File', items: [{ label: () => 'Export to SOCOM 3', action: noop }] }]);
    expect(cmds[0].label).toBe('Export to SOCOM 3');
    expect(matchScore('exp soc', cmds[0].label)).not.toBeNull();
  });

  it('matches words in order, as prefixes or spread letters', () => {
    expect(matchScore('sv cp', 'Save copy…')).not.toBeNull();
    expect(matchScore('copy save', 'Save copy…')).toBeNull();
    expect(matchScore('dup', 'Duplicate')! > matchScore('dup', 'Drop to ground uplift')!).toBe(true);
    expect(matchScore('xyz', 'Duplicate')).toBeNull();
  });

  it('ranks word starts first and puts disabled commands last', () => {
    const cmds = commandsFromMenus(menus);
    expect(searchCommands(cmds, 'd').map(c => c.label)[0]).toBe('Duplicate');
    expect(searchCommands(cmds, 'e').map(c => c.label).at(-1)).toBe('Export memory-card pack…');
    expect(searchCommands(cmds, '')).toHaveLength(4);
  });

  it('formats distances in units and metres', () => {
    expect(formatDistance(21)).toBe('21.0 units ≈ 2.00 m');
    expect(formatDistance(1050)).toBe('1050.0 units ≈ 100.0 m');
  });
});
