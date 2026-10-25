// Textures for the Model Editor, decoded by the bridge (TEX0.CBP palette, real
// alpha) and cached per session revision so edits (replaceTexture) show up.

import type { ModelTexturePixels } from '../zipper-workbench/zed-model-renderer';
import { AssetClient, pngToRgba } from './asset-client';

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
