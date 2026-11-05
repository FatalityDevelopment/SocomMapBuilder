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
  meshes: Map<string, MapModel>, pixels: Record<string, ModelTexturePixels>, progress: (done: number, total: number) => void,
  meshBatchSize = 0): Promise<string[]> {
  const todo = [...new Set(sources)].filter(s => !meshes.has(s));
  const failed: string[] = [];
  let done = 0;
  const loadOne = async (source: string, getWire: (source: string) => MeshWire | Promise<MeshWire>): Promise<void> => {
    try {
      const wire = await getWire(source);
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
  // Keep at most six models loading textures at once, including when a mesh batch arrives together.
  const loadGroup = async (group: string[], getWire: (source: string) => MeshWire | Promise<MeshWire>) => {
    let next = 0;
    await Promise.all(Array.from({ length: Math.min(6, group.length) }, async () => {
      while (next < group.length) { await loadOne(group[next++], getWire); }
    }));
  };
  if (meshBatchSize > 0) {
    for (let start = 0; start < todo.length; start += meshBatchSize) {
      const group = todo.slice(start, start + meshBatchSize);
      try {
        const results = new Map((await client.meshBatch(group)).map(item => [item.source, item]));
        await loadGroup(group, source => {
          const item = results.get(source);
          if (!item?.mesh) { throw new Error(item?.error ?? 'Mesh missing from batch response'); }
          return item.mesh;
        });
      } catch (err) {
        failed.push(...group.map(source => `${source} (${errText(err)})`));
        done += group.length;
        progress(done, todo.length);
      }
    }
  } else {
    await loadGroup(todo, source => {
      const cut = source.indexOf(':');
      return client.mesh(source.slice(0, cut), source.slice(cut + 1)) as Promise<MeshWire>;
    });
  }
  return failed;
}
