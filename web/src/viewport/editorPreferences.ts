import type { Lighting, Shading } from '../webview/zipper-core/viewport-core';
import type { SnapTarget } from './mapViewport';
import type { PivotMode } from './arrange';

export interface EditorPreferences {
  shading: Shading;
  lighting: Lighting;
  grid: boolean;
  snap: boolean;
  snapMove: number;
  snapRotate: number;
  snapScale: number;
  gizmoLocal: boolean;
  ortho: boolean;
  backfaces: boolean;
  placeYaw: number;
  placeScale: number;
  randomYaw: boolean;
  randomScale: boolean;
  minPlaceScale: number;
  maxPlaceScale: number;
  alignSurface: boolean;
  snapTarget: SnapTarget;
  pivotMode: PivotMode;
  // Place clicks add clutter or a GEO prop (placeInstance).
  placeAs: 'clutter' | 'prop';
  brushRadius: number;
  brushSpacing: number;
  brushDensity: number;
  brushSlope: number;
  brushErase: boolean;
  // Icons over props with map object actions, and HUD objective flags (View menu).
  actionMarkers: boolean;
  objectiveMarkers: boolean;
  // Actors drawn as characters at their start points (View menu).
  actorFigures: boolean;
  // What the figures carry: their worn gear and the weapon in hand (View menu).
  figureGear: boolean;
  figureWeapons: boolean;
  // The HUD minimap in the viewport's corner (View menu, N) and its range: world units from centre to rim.
  minimap: boolean;
  minimapRange: number;
}
export const EDITOR_PREFERENCES_KEY = 'socom.map.editorPreferences.v1';
export const DEFAULT_EDITOR_PREFERENCES: EditorPreferences = {
  shading: 'textured', lighting: 'baked', grid: true, snap: false, snapMove: 10, snapRotate: 15,
  snapScale: 0.1, gizmoLocal: false, ortho: false, backfaces: true,
  placeYaw: 0, placeScale: 1, randomYaw: false, randomScale: false, minPlaceScale: 0.8, maxPlaceScale: 1.2, alignSurface: false,
  snapTarget: 'increment', pivotMode: 'center', placeAs: 'clutter',
  brushRadius: 20, brushSpacing: 5, brushDensity: 8, brushSlope: 35, brushErase: false,
  actionMarkers: true, objectiveMarkers: true, actorFigures: true, figureGear: true, figureWeapons: true, minimap: false, minimapRange: 1200,
};
export function normalizeEditorPreferences(value: unknown): EditorPreferences {
  const input = value && typeof value === 'object' ? value as Record<string, unknown> : {};
  const result = { ...DEFAULT_EDITOR_PREFERENCES };
  for (const key of Object.keys(result) as Array<keyof EditorPreferences>) {
    const candidate = input[key];
    const fallback = result[key];
    if (typeof fallback === 'boolean' && typeof candidate === 'boolean') {
      Object.assign(result, { [key]: candidate });
    } else if (typeof fallback === 'number' && typeof candidate === 'number' && Number.isFinite(candidate)) {
      const minimum = key === 'placeYaw' ? -360000 : key === 'snapMove' ? 0.001 : key === 'snapRotate' ? 1 : 0.01;
      if (candidate >= minimum && candidate <= 1000000) { Object.assign(result, { [key]: candidate }); }
    }
  }
  if (input.snapTarget === 'surface' || input.snapTarget === 'vertex' || input.snapTarget === 'increment') { result.snapTarget = input.snapTarget; }
  if (input.pivotMode === 'individual' || input.pivotMode === 'active' || input.pivotMode === 'center') { result.pivotMode = input.pivotMode; }
  if (input.placeAs === 'prop' || input.placeAs === 'clutter') { result.placeAs = input.placeAs; }
  if (typeof input.shading === 'string' && ['textured', 'solid', 'wireframe', 'xray'].includes(input.shading)) { result.shading = input.shading as Shading; }
  if (typeof input.lighting === 'string' && ['baked', 'studio', 'unlit'].includes(input.lighting)) {
    result.lighting = input.lighting as Lighting;
  } else if (typeof input.baked === 'boolean') {
    // Migrate the previous baked-lighting checkbox without changing the user's saved appearance.
    result.lighting = input.baked ? 'baked' : 'unlit';
  }
  return result;
}
export function readEditorPreferences(storage?: Pick<Storage, 'getItem'>): EditorPreferences {
  try { return normalizeEditorPreferences(JSON.parse((storage ?? window.localStorage).getItem(EDITOR_PREFERENCES_KEY) ?? 'null')); }
  catch { return { ...DEFAULT_EDITOR_PREFERENCES }; }
}
export function writeEditorPreferences(preferences: EditorPreferences, storage?: Pick<Storage, 'setItem'>): void {
  try { (storage ?? window.localStorage).setItem(EDITOR_PREFERENCES_KEY, JSON.stringify(normalizeEditorPreferences(preferences))); }
  catch { /* Editing remains usable when storage is unavailable. */ }
}
