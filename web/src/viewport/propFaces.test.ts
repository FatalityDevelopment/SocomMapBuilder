import { describe, expect, it } from 'vitest';
import type { EditorChunk } from '../webview/model-editor/mesh-data';
import { propDrawChunks } from './propFaces';

function cloth(triangles: number[]): EditorChunk {
  return {
    index: 0, geometryOffset: 0, node: 'canopy', visual: 'N000_I000_V00', texture: 'cloth', materialBlend: false, unit: 'Sixteenth',
    vertexCount: 5, positions: new Float32Array([0,0,0, 1,0,0, 1,1,0, 0,1,0, 2,0,0]),
    uvs: new Float32Array(10), normals: new Float32Array(15), colors: null, triangles: new Uint32Array(triangles),
  };
}

describe('authored prop faces', () => {
  it('recognizes a reverse skin split along the other diagonal in another chunk, retaining a single-sided flap', () => {
    const outside = cloth([0,1,2, 0,2,3, 1,4,2]);
    const inside = cloth([1,0,3, 1,3,2]);
    const parts = propDrawChunks([outside, inside]);
    expect(parts.map(p => [p.frontOnly, [...p.chunk.triangles]])).toEqual([
      [true, [0,1,2, 0,2,3]], [false, [1,4,2]], [true, [1,0,3, 1,3,2]],
    ]);
    expect(outside.triangles.length).toBe(9);
  });

  it('leaves a single sheet and same-winding repeated draws double-sided', () => {
    const sheet = cloth([0,1,2, 0,2,3]);
    expect(propDrawChunks([sheet, sheet]).map(p => p.frontOnly)).toEqual([false, false]);
  });
});
