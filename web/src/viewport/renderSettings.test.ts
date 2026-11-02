import { describe, expect, it } from 'vitest';
import { DEFAULT_RENDER_SETTINGS, normalizeRenderSettings, readRenderSettings, renderPixelRatio, writeRenderSettings } from './renderSettings';

describe('render preferences', () => {
  it('restores supported settings and rejects malformed values', () => {
    expect(normalizeRenderSettings({ resolution: 0.5, filtering: 'pixel', showStats: true }))
      .toEqual({ resolution: 0.5, filtering: 'pixel', showStats: true });
    for (const value of [null, 'bad', { resolution: -1, filtering: 'bad', showStats: 'true' }]) {
      expect(normalizeRenderSettings(value)).toEqual(DEFAULT_RENDER_SETTINGS);
    }
  });
  it('survives corrupt or blocked storage and round-trips preferences', () => {
    expect(readRenderSettings({ getItem: () => '{' })).toEqual(DEFAULT_RENDER_SETTINGS);
    expect(readRenderSettings({ getItem: () => { throw new Error('denied'); } })).toEqual(DEFAULT_RENDER_SETTINGS);
    let saved = '';
    const settings = { resolution: 0.75, filtering: 'pixel' as const, showStats: true };
    writeRenderSettings(settings, { setItem: (_, value) => { saved = value; } });
    expect(readRenderSettings({ getItem: () => saved })).toEqual(settings);
    expect(() => writeRenderSettings(settings, { setItem: () => { throw new Error('full'); } })).not.toThrow();
  });
  it('caps high-DPI cost and scales render area quadratically', () => {
    expect(renderPixelRatio(4, 1)).toBe(2);
    expect(renderPixelRatio(NaN, 1)).toBe(1);
    expect(renderPixelRatio(2, 0.5) ** 2 / renderPixelRatio(2, 1) ** 2).toBe(0.25);
  });
});
