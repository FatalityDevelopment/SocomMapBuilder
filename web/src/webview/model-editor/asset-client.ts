// Client for asset sessions (ASSET_* commands). Every query, edit and save a page makes goes through here to the
// Map Builder server (POST /api/command), so every page works on the same session.
// (CodeDesigner's copy of this file posts the same commands to the VS Code extension instead.)

import { command as postCommand } from '../../api/client';

export interface SessionStatus {
  session: string;
  path: string;
  isZdb: boolean;
  mapNamespace: string | null;
  revision: number;
  dirty: string[];
  canUndo: string | null;
  canRedo: string | null;
  entries: number;
  models: Record<string, number>;
  // Set for sessions of another game (SOCOM 3 / Combined Assault), which are read-only for now.
  game?: string;
  readOnly?: boolean;
}

export interface ModelSummary {
  archive: string;
  name: string;
  kind: string;
  namespace: string;
  size: number;
  visualCount: number;
}

export interface TextureInfo {
  name: string;
  found: boolean;
  namespace?: string;
  width?: number;
  height?: number;
  psm?: string;
  bpp?: number;
  palette?: number | null;
  paletteResolved?: boolean;
  hasTransparency?: boolean;
  alphaTest?: number | null;
  alphaBlend?: boolean | null;
  clamp?: [boolean, boolean] | null;
}

export interface ChunkInfo {
  index: number;
  offset: number;
  visual: string | null;
  texture: string | null;
  binding: string | null;
  vertices: number;
  triangles: number;
  scale: number;
  origin: [number, number, number];
  vertexColors: boolean;
}

export interface GeoInfo {
  name: string;
  index: number;
  transform: number[];
  boundsMin: number[] | null;
  boundsMax: number[] | null;
  vparamsFlags: string | null;
  positionUnit: string | null;
  visuals: number;
  reference: string | null;
  children: string[];
}

export interface ModelDescription {
  archive: string;
  name: string;
  kind: string;
  namespace: string;
  size: number;
  nodeIndex: number;
  textureNamespace: string | null;
  geoArchive: string | null;
  fromChains: boolean;
  visuals: string[];
  chunks: ChunkInfo[];
  textures: TextureInfo[];
  geo: GeoInfo | null;
}

export interface EditResult {
  revision: number;
  description?: string;
  changed: string[];
}

export interface MapInstanceWire {
  kind: string;
  model: string;
  instance: string;
  path: string;
  position: number[];
  scale: number;
  matrix: number[];
  geoIndex: number | null;
  clutterIndex: number | null;
  source: string | null;
}

export interface MapSceneResult {
  sceneNamespace: string;
  counts: Record<string, number>;
  returned: number;
  instances: MapInstanceWire[];
}

export interface MapPointWire { name: string; team: number; kind: string; x: number; z: number; cellX: number; cellZ: number }

export interface AnimationSummary { name: string; source: string; editable: boolean; duration: number; frames: number; samples: number; fps: number; bones: number }

export interface WeaponSummary { internalName: string; model: string; archive: string; fireAnimName: string; fireWait: number; tracerTextureName: string }

export interface FxInstruction {
  op: string;
  name: string;
  depth: number;
  condition: boolean;
  flags: number;
  fields: Record<string, unknown>;
  words: string;
}

export interface FxNode {
  name: string;
  found: boolean;
  set?: string;
  names?: string[];
  models?: Array<{ archive: string; name: string }>;
  textures?: string[];
  nodeRefs?: Array<{ name: string; flags: number; parent: number }>;
  tracks?: Array<{ target: string; headerFlags?: string; instructions: FxInstruction[] }>;
  spawns?: FxNode[] | null;
}

export interface WeaponFx {
  weapon: { internalName: string; modelName: string; fireAnimName: string; fireWait: number; tracerTextureName: string; fields: Record<string, unknown> };
  effect: FxNode | null;
}

export interface AnimationList { packageArchive: string | null; sharedPath: string | null; clips: AnimationSummary[] }

export class AssetClient {
  session: string | null = null;

  private call<T>(command: string, fields: Record<string, string | undefined> = {}): Promise<T> {
    return postCommand<T>(command, fields, this.session);
  }

  private withJson<T>(command: string, json: unknown, fields: Record<string, string | undefined> = {}): Promise<T> {
    return this.call<T>(command, { ...fields, AssetJson: JSON.stringify(json) });
  }

  async open(path: string): Promise<SessionStatus> {
    const result = await this.call<{ status: SessionStatus }>('ASSET_OPEN', { InputPath: path });
    this.session = result.status.session;
    return result.status;
  }

  status(): Promise<SessionStatus> { return this.call('ASSET_STATUS'); }

  listModels(filter: { kind?: string; query?: string; archive?: string } = {}): Promise<ModelSummary[]> {
    return this.withJson('ASSET_LIST_MODELS', filter);
  }

  describe(archive: string, name: string): Promise<ModelDescription> {
    return this.call('ASSET_DESCRIBE_MODEL', { AssetArchive: archive, AssetName: name });
  }

  texture(name: string, namespace?: string | null): Promise<{ info: TextureInfo; png: string }> {
    return this.call('ASSET_TEXTURE', { AssetName: name, AssetArchive: namespace ?? undefined });
  }

  // The editable mesh (model space, base64 arrays): see mesh-data.ts.
  mesh(archive: string, name: string): Promise<unknown> {
    return this.call('ASSET_MODEL_MESH', { AssetArchive: archive, AssetName: name, AssetJson: JSON.stringify({ arrays: true }) });
  }

  // The composed map: worldmodel, GEO references, sky and clutter placements (model space).
  mapScene(filter: { kind?: string; query?: string; limit?: number; includeUnresolved?: boolean } = {}): Promise<MapSceneResult> {
    return this.withJson('ASSET_MAP_SCENE', { limit: 100000, ...filter });
  }

  mapPoints(): Promise<{ points: MapPointWire[] }> { return this.call('ASSET_MAP_POINTS'); }

    // Character animation clips: the package's MOTION_S.ZAR (editable) and the shared RUN/MOTION_P.ZAR.
  listAnimations(filter: { query?: string; source?: string } = {}): Promise<AnimationList> {
    return this.withJson('ASSET_LIST_ANIMATIONS', filter);
  }

  animation(name: string, source?: string): Promise<unknown> {
    return this.call('ASSET_ANIMATION', { AssetName: name, AssetArchive: source });
  }

    // Weapons in the package (zweapon.rdr) and what firing one plays (its ZANIM effect tree).
  weapons(): Promise<WeaponSummary[]> { return this.call('ASSET_WEAPONS'); }

  weaponFx(weapon: string): Promise<WeaponFx> { return this.call('ASSET_WEAPON_FX', { AssetName: weapon }); }

  entryBytes(leaf: string): Promise<{ leaf: string; revision: number; data: string }> {
    return this.call('ASSET_ENTRY_BYTES', { AssetArchive: leaf });
  }

  edit(op: Record<string, unknown>): Promise<EditResult> { return this.withJson('ASSET_EDIT', op); }

  undo(): Promise<EditResult> { return this.call('ASSET_UNDO'); }

  redo(): Promise<EditResult> { return this.call('ASSET_REDO'); }

  save(args: { mode: 'zdb' | 'pack'; outputPath?: string; packName?: string; folderCard?: string }): Promise<{ written: string[]; leaves: string[] }> {
    return this.withJson('ASSET_SAVE', args);
  }

  /** Converts a model to the other game (SOCOM 3 / CA -> SOCOM II pack, or the reverse); answers where it went. */
  exportModel(archive: string, name: string, args: { pack?: string; modelName?: string; outputDir?: string; clips?: string[] }): Promise<ExportResult> {
    return this.withJson('ASSET_EXPORT', args, { AssetArchive: archive, AssetName: name });
  }
}

export interface ExportResult {
  target: string;
  path: string;
  pack: string;
  model: string;
  kind: string;
  library: string;
  triangles: number;
  textures: number;
  palettes: number;
  clips: string[];
  warnings: string[];
  open: { archive: string; name: string } | null;
}

export function base64ToBytes(value: string): Uint8Array {
  const bin = atob(value);
  const out = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) { out[i] = bin.charCodeAt(i); }
  return out;
}

// A PNG (base64) to top-down RGBA, unpremultiplied, as the bridge decoded it.
export async function pngToRgba(base64: string): Promise<{ width: number; height: number; rgba: Uint8Array }> {
  const blob = new Blob([base64ToBytes(base64)], { type: 'image/png' });
  const bitmap = await createImageBitmap(blob, { premultiplyAlpha: 'none', colorSpaceConversion: 'none' });
  const canvas = document.createElement('canvas');
  canvas.width = bitmap.width;
  canvas.height = bitmap.height;
  const ctx = canvas.getContext('2d', { willReadFrequently: true })!;
  ctx.drawImage(bitmap, 0, 0);
  const data = ctx.getImageData(0, 0, bitmap.width, bitmap.height).data;
  bitmap.close();
  return { width: canvas.width, height: canvas.height, rgba: new Uint8Array(data.buffer.slice(0)) };
}
