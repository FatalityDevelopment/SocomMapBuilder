import { describe, expect, it, vi } from 'vitest';
import { AssetClient } from '../model-editor/asset-client';
import { TextureCache } from '../model-editor/model-loader';
import type { MapModel } from '../../viewport/mapViewport';
import { loadMapModels } from './map-models';

describe('GTA map mesh batches', () => {
  it('draws successful models from a partial batch and retries only missing models on reload', async () => {
    const client = new AssetClient();
    const textures = new TextureCache(client);
    const meshes = new Map<string, MapModel>();
    const batch = vi.spyOn(client, 'meshBatch');
    const single = vi.spyOn(client, 'mesh');
    const progress = vi.fn();
    const sources = ['g:one', 'g:one', 'g:two', 'g:three', 'g:four', 'g:five'];
    const result = (source: string) => ({ source, mesh: { archive: 'g', name: source.slice(2),
      chunks: [], revision: 0, boundsMin: [], boundsMax: [] }, error: null });
    batch.mockResolvedValueOnce([{ source: 'g:two', mesh: null, error: 'missing model' }, result('g:one')])
      .mockRejectedValueOnce(new Error('connection lost')).mockResolvedValueOnce([result('g:five')]);
    const load = () => loadMapModels(client, textures, sources, meshes, {}, progress, 2);
    expect(await load()).toEqual(['g:two (missing model)', 'g:three (connection lost)', 'g:four (connection lost)']);
    expect([...meshes.keys()].sort()).toEqual(['g:five', 'g:one']);
    expect(progress).toHaveBeenLastCalledWith(5, 5);
    expect(batch.mock.calls.map(([group]) => group)).toEqual([['g:one', 'g:two'], ['g:three', 'g:four'], ['g:five']]);
    batch.mockImplementation(async group => group.map(result));
    expect(await load()).toEqual([]);
    expect(batch.mock.calls.slice(3).map(([group]) => group)).toEqual([['g:two', 'g:three'], ['g:four']]);
    expect(meshes.size).toBe(5);
    expect(single).not.toHaveBeenCalled();
  });
});
