// Meshes and textures for every model a map places, from an asset session: what the Map Builder and the Asset
// Viewer's map preview both draw with MapViewport.setScene. Pixels are keyed "<namespace>:<texture>" (lower case).

import type { AssetClient } from '../model-editor/asset-client';
import type { TextureCache } from '../model-editor/model-loader';
import { decodeMesh, type MeshWire } from '../model-editor/mesh-data';
import type { MapModel } from '../../viewport/mapViewport';
import type { ModelTexturePixels } from './texture-pixels';

const errText = (err: unknown) => err instanceof Error ? err.message : String(err);

/** Loads the sources not in `meshes` yet into `meshes` / `pixels`; answers the ones that failed, with why. */
export async function loadMapModels(client: AssetClient, textures: TextureCache, sources: string[],
  meshes: Map<string, MapModel>, pixels: Record<string, ModelTexturePixels>, progress: (done: number, total: number) => void): Promise<string[]> {
  const todo = [...new Set(sources)].filter(s => !meshes.has(s));
  const failed: string[] = [];
  let done = 0;
  const loadOne = async (source: string): Promise<void> => {
    const cut = source.indexOf(':');
    try {
      const wire = await client.mesh(source.slice(0, cut), source.slice(cut + 1)) as MeshWire;
      const mesh = decodeMesh(wire);
      const ns = wire.textureNamespace ?? null;
      const names = [...new Set(mesh.chunks.map(c => c.texture).filter((t): t is string => !!t))];
      await Promise.all(names.map(async n => {
        const key = `${ns ?? ''}:${n}`.toLowerCase();
        if (key in pixels) { return; }
        const p = await textures.get(n, ns);
        if (p) { pixels[key] = p; }
      }));
      meshes.set(source, { mesh, textureNamespace: ns });
    } catch (err) {
      failed.push(`${source} (${errText(err)})`);
    }
    done++;
    progress(done, todo.length);
  };
  // A few at a time: the backend answers in order anyway, and the progress stays readable.
  const queue = [...todo];
  await Promise.all(Array.from({ length: Math.min(6, queue.length) }, async () => {
    while (queue.length) { await loadOne(queue.shift()!); }
  }));
  return failed;
}
