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
  it('round-trips settings and survives broken or unavailable storage', () => {
    let saved = '';
    const preferences = { ...DEFAULT_EDITOR_PREFERENCES, baked: true, backfaces: false };
    writeEditorPreferences(preferences, { setItem: (_, value) => { saved = value; } });
    expect(readEditorPreferences({ getItem: () => saved })).toEqual(preferences);
    expect(readEditorPreferences({ getItem: () => '{' })).toEqual(DEFAULT_EDITOR_PREFERENCES);
    expect(readEditorPreferences({ getItem: () => { throw new Error('denied'); } })).toEqual(DEFAULT_EDITOR_PREFERENCES);
    expect(() => writeEditorPreferences(preferences, { setItem: () => { throw new Error('full'); } })).not.toThrow();
  });
});
