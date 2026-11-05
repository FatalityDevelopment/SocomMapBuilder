import { describe, expect, it } from 'vitest';
import { DEFAULT_EDITOR_PREFERENCES, normalizeEditorPreferences, readEditorPreferences, writeEditorPreferences } from './editorPreferences';

describe('editor preferences', () => {
  it('restores view, snapping and placement settings without mutating defaults', () => {
    const input = { shading: 'wireframe', grid: false, ortho: true, snap: true, snapMove: 20,
      randomScale: true, minPlaceScale: 0.7, maxPlaceScale: 1.3, alignSurface: true, placeYaw: -45 };
    expect(normalizeEditorPreferences(input)).toMatchObject(input);
    expect(DEFAULT_EDITOR_PREFERENCES.grid).toBe(true);
  });
  it('rejects corrupt values, invalid numeric limits and unexpected types', () => {
    expect(normalizeEditorPreferences({ grid: 'false', shading: 'broken', snapMove: 0, snapRotate: -5,
      placeScale: Infinity, minPlaceScale: NaN, snapScale: -1, placeYaw: '90', maxPlaceScale: 1e30 }))
      .toEqual(DEFAULT_EDITOR_PREFERENCES);
    expect(normalizeEditorPreferences(null)).toEqual(DEFAULT_EDITOR_PREFERENCES);
  });
  it('migrates the old checkbox and lets an explicit lighting mode take precedence', () => {
    expect(normalizeEditorPreferences({ baked: false }).lighting).toBe('unlit');
    expect(normalizeEditorPreferences({ baked: true }).lighting).toBe('baked');
    expect(normalizeEditorPreferences({ baked: false, lighting: 'studio' }).lighting).toBe('studio');
    expect(normalizeEditorPreferences({ lighting: 'broken' }).lighting).toBe('baked');
  });
  it('round-trips settings and survives broken or unavailable storage', () => {
    let saved = '';
    const preferences = { ...DEFAULT_EDITOR_PREFERENCES, lighting: 'studio' as const, backfaces: false };
    writeEditorPreferences(preferences, { setItem: (_, value) => { saved = value; } });
    expect(readEditorPreferences({ getItem: () => saved })).toEqual(preferences);
    expect(readEditorPreferences({ getItem: () => '{' })).toEqual(DEFAULT_EDITOR_PREFERENCES);
    expect(readEditorPreferences({ getItem: () => { throw new Error('denied'); } })).toEqual(DEFAULT_EDITOR_PREFERENCES);
    expect(() => writeEditorPreferences(preferences, { setItem: () => { throw new Error('full'); } })).not.toThrow();
  });
  it('keeps the minimap switch and its range', () => {
    expect(DEFAULT_EDITOR_PREFERENCES.minimap).toBe(false);
    const p = normalizeEditorPreferences({ minimap: true, minimapRange: 2700 });
    expect(p.minimap).toBe(true);
    expect(p.minimapRange).toBe(2700);
    expect(normalizeEditorPreferences({ minimap: 'yes', minimapRange: -5 })).toMatchObject({ minimap: false, minimapRange: 1200 });
  });
});
