// Client for the map builder's asset sessions. Every open, query, edit and save goes to the backend
// (POST /api/command) as an ASSET_* command, so the page is only a client of the same operations an
// assistant could run.

import type { ModelTexturePixels } from '../types';

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
  hasTransparency?: boolean;
  alphaTest?: number | null;
  alphaBlend?: boolean | null;
  clamp?: [boolean, boolean] | null;
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

export interface EditResult {
  revision: number;
  description?: string;
  changed: string[];
}

interface CommandResponse {
  Success: boolean;
  Command?: string;
  Message?: string;
  Error?: string;
  Data?: string;
}

// Sends one command. The backend answers with CommandResponse; Data holds the result as JSON text.
export async function command<T>(name: string, fields: Record<string, string | undefined> = {}, session: string | null = null): Promise<T> {
  const res = await fetch('/api/command', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ Command: name, AssetSession: session ?? undefined, ...fields }),
  });
  if (!(res.headers.get('content-type') ?? '').includes('json')) {
    throw new Error(`${name}: backend answered HTTP ${res.status} (is MapBuilder.Server running?)`);
  }
  const reply = (await res.json()) as CommandResponse;
  if (!reply.Success) { throw new Error(reply.Error ?? `${name} failed`); }
  return (reply.Data ? JSON.parse(reply.Data) : reply.Message) as T;
}

function withJson<T>(name: string, json: unknown, session: string | null, fields: Record<string, string | undefined> = {}): Promise<T> {
  return command<T>(name, { ...fields, AssetJson: JSON.stringify(json) }, session);
}

export function base64ToBytes(value: string): Uint8Array {
  const bin = atob(value);
  const out = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) { out[i] = bin.charCodeAt(i); }
  return out;
}

// A PNG (base64) to top-down RGBA, unpremultiplied, as the backend decoded it.
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

// Session-scoped calls. The session id comes from open().
export class AssetClient {
  session: string | null = null;

  async open(path: string): Promise<SessionStatus> {
    const result = await command<{ status: SessionStatus }>('ASSET_OPEN', { InputPath: path });
    this.session = result.status.session;
    return result.status;
  }

  status(): Promise<SessionStatus> { return command('ASSET_STATUS', {}, this.session); }

  listModels(filter: { kind?: string; query?: string; archive?: string } = {}): Promise<ModelSummary[]> {
    return withJson('ASSET_LIST_MODELS', filter, this.session);
  }

  texture(name: string, namespace?: string | null): Promise<{ info: TextureInfo; png: string }> {
    return command('ASSET_TEXTURE', { AssetName: name, AssetArchive: namespace ?? undefined }, this.session);
  }

  // The editable mesh (model space, base64 arrays): see api/mesh.ts.
  mesh(archive: string, name: string): Promise<unknown> {
    return command('ASSET_MODEL_MESH', { AssetArchive: archive, AssetName: name, AssetJson: JSON.stringify({ arrays: true }) }, this.session);
  }

  // The composed map: worldmodel, GEO references, sky and clutter placements (model space).
  mapScene(filter: { kind?: string; query?: string; limit?: number; includeUnresolved?: boolean } = {}): Promise<MapSceneResult> {
    return withJson('ASSET_MAP_SCENE', { limit: 100000, ...filter }, this.session);
  }

  mapPoints(): Promise<{ points: MapPointWire[] }> { return command('ASSET_MAP_POINTS', {}, this.session); }

  edit(op: Record<string, unknown>): Promise<unknown> { return withJson('ASSET_EDIT', op, this.session); }

  undo(): Promise<EditResult> { return command('ASSET_UNDO', {}, this.session); }

  redo(): Promise<EditResult> { return command('ASSET_REDO', {}, this.session); }

  save(args: { mode: 'zdb' | 'pack'; outputPath?: string; packName?: string }): Promise<{ written: string[]; leaves: string[] }> {
    return withJson('ASSET_SAVE', args, this.session);
  }
}

// Textures decoded once per session revision; a placement edit keeps them, a model edit clears them.
export class TextureCache {
  private revision = -1;
  private readonly cache = new Map<string, ModelTexturePixels | null>();

  constructor(private readonly client: AssetClient) {}

  invalidate(revision: number): void {
    if (revision === this.revision) { return; }
    this.revision = revision;
    this.cache.clear();
  }

  async get(name: string, namespace: string | null): Promise<ModelTexturePixels | null> {
    const key = `${namespace ?? ''}:${name.toLowerCase()}`;
    if (this.cache.has(key)) { return this.cache.get(key)!; }
    let pixels: ModelTexturePixels | null = null;
    try {
      const { info, png } = await this.client.texture(name, namespace);
      const { width, height, rgba } = await pngToRgba(png);
      pixels = {
        width,
        height,
        rgba,
        wrapS: info.clamp?.[0] ? 'clamp' : 'repeat',
        wrapT: info.clamp?.[1] ? 'clamp' : 'repeat',
        alphaTest: info.alphaTest ?? null,
        hasTransparency: !!info.hasTransparency,
        alphaBlend: !!info.alphaBlend,
      };
    } catch { pixels = null; }
    this.cache.set(key, pixels);
    return pixels;
  }
}
