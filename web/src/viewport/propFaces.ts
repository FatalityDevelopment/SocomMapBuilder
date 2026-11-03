import { Triangle, Vector3 } from 'three';
import type { EditorChunk } from '../webview/model-editor/mesh-data';

interface Face { triangle: Triangle; normal: Vector3 }
interface Corner { position: Vector3; faces: Face[] }

/** Some props have separately shaded inner and outer skins at the same positions.
 * Drawing the backs of those triangles over the authored fronts causes z-fighting.
 * A panel's two sides can use different diagonals or fall in different DMA chunks. */
export function propDrawChunks(chunks: EditorChunk[]): Array<{ chunk: EditorChunk; frontOnly: boolean }> {
  const batches = new Map<string, Map<string, Corner>>();
  const parts = chunks.map(chunk => {
    const batch = JSON.stringify([chunk.node, chunk.visual, chunk.texture]);
    let positions = batches.get(batch);
    if (!positions) { positions = new Map(); batches.set(batch, positions); }
    const corners = Array.from({ length: chunk.vertexCount }, (_, vertex) => {
      const i = vertex * 3, p = chunk.positions;
      const key = `${p[i]},${p[i + 1]},${p[i + 2]}`;
      let corner = positions.get(key);
      if (!corner) {
        corner = { position: new Vector3().fromArray(p, i), faces: [] };
        positions.set(key, corner);
      }
      return corner;
    });
    const faces: Face[] = [];
    const indices = chunk.triangles;
    for (let i = 0; i < indices.length; i += 3) {
      const a = corners[indices[i]], b = corners[indices[i + 1]], c = corners[indices[i + 2]];
      const triangle = new Triangle(a.position, b.position, c.position);
      const face = { triangle, normal: triangle.getNormal(new Vector3()) };
      faces.push(face);
      a.faces.push(face); b.faces.push(face); c.faces.push(face);
    }
    return { chunk, corners, faces };
  });

  const center = new Vector3();
  return parts.flatMap(({ chunk, corners, faces }) => {
    const paired: number[] = [], unpaired: number[] = [];
    const indices = chunk.triangles;
    faces.forEach((face, index) => {
      const i = index * 3;
      const vertices = [corners[indices[i]], corners[indices[i + 1]], corners[indices[i + 2]]];
      const opposite = (other: Face) => face.normal.dot(other.normal) < -0.995;
      // Contact along a seam alone doesn't make the other panel a reverse skin.
      face.triangle.getMidpoint(center);
      const covered = vertices.every(vertex => vertex.faces.some(opposite))
        && vertices.some(vertex => vertex.faces.some(other => opposite(other) && other.triangle.containsPoint(center)));
      const target = covered ? paired : unpaired;
      target.push(indices[i], indices[i + 1], indices[i + 2]);
    });
    if (!paired.length || !unpaired.length) { return [{ chunk, frontOnly: paired.length > 0 }]; }
    return [
      { chunk: { ...chunk, triangles: new Uint32Array(paired) }, frontOnly: true },
      { chunk: { ...chunk, triangles: new Uint32Array(unpaired) }, frontOnly: false },
    ];
  });
}
