export type TextureFiltering = 'smooth' | 'pixel';
export interface RenderSettings {
  resolution: number;
  filtering: TextureFiltering;
  showStats: boolean;
}
export const DEFAULT_RENDER_SETTINGS: RenderSettings = { resolution: 1, filtering: 'smooth', showStats: false };
export const RENDER_SETTINGS_KEY = 'socom.map.renderSettings.v1';

export function normalizeRenderSettings(value: unknown): RenderSettings {
  const input = value && typeof value === 'object' ? value as Partial<RenderSettings> : {};
  return {
    resolution: [0.5, 0.75, 1].includes(input.resolution ?? 0) ? input.resolution! : 1,
    filtering: input.filtering === 'pixel' ? 'pixel' : 'smooth',
    showStats: input.showStats === true,
  };
}

// Storage may be unavailable in private browsing or embedded contexts.
export function readRenderSettings(storage?: Pick<Storage, 'getItem'>): RenderSettings {
  try { return normalizeRenderSettings(JSON.parse((storage ?? window.localStorage).getItem(RENDER_SETTINGS_KEY) ?? 'null')); }
  catch { return { ...DEFAULT_RENDER_SETTINGS }; }
}
export function writeRenderSettings(settings: RenderSettings, storage?: Pick<Storage, 'setItem'>): void {
  try { (storage ?? window.localStorage).setItem(RENDER_SETTINGS_KEY, JSON.stringify(normalizeRenderSettings(settings))); }
  catch { /* A preference must not interrupt editing. */ }
}

// 100% draws at the screen's own pixel density (a 4K screen at 250% too: a cap there stretches the frame and blurs
// textures); 75% / 50% trade sharpness for speed.
export function renderPixelRatio(deviceRatio: number, resolution: number): number {
  return Math.max(1, Number.isFinite(deviceRatio) ? deviceRatio : 1) * resolution;
}
