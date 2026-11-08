// The Map Builder's Collision view: what the game treats as solid (SOCOM II GEO di faces, docs/formats/collision.md),
// drawn over the map as see-through floors (green: faces that look up) and walls (orange), with their outlines.

import * as THREE from 'three';

export interface CollisionFaceWire {
  geoIndex: number;
  node: string;
  kind: 'world' | 'prop';
  material: number;
  up: boolean;
  /** World-space corners: x, y, z for each (a convex polygon). */
  points: number[];
}

export const FLOOR_COLOR = 0x4fd18b;
export const WALL_COLOR = 0xf08a3c;

/** Fan-triangulates convex polygons into one position buffer (three floats a vertex). */
export function triangulate(faces: CollisionFaceWire[]): Float32Array {
  let triangles = 0;
  for (const f of faces) { triangles += Math.max(0, f.points.length / 3 - 2); }
  const out = new Float32Array(triangles * 9);
  let o = 0;
  for (const f of faces) {
    const p = f.points;
    const n = p.length / 3;
    for (let i = 1; i + 1 < n; i++) {
      for (const v of [0, i, i + 1]) { out[o++] = p[v * 3]; out[o++] = p[v * 3 + 1]; out[o++] = p[v * 3 + 2]; }
    }
  }
  return out;
}

/** Each polygon's edges as line segments (three floats a vertex, two vertices a segment). */
export function outline(faces: CollisionFaceWire[]): Float32Array {
  let segments = 0;
  for (const f of faces) { segments += f.points.length / 3; }
  const out = new Float32Array(segments * 6);
  let o = 0;
  for (const f of faces) {
    const p = f.points;
    const n = p.length / 3;
    for (let i = 0; i < n; i++) {
      const j = (i + 1) % n;
      out[o++] = p[i * 3]; out[o++] = p[i * 3 + 1]; out[o++] = p[i * 3 + 2];
      out[o++] = p[j * 3]; out[o++] = p[j * 3 + 1]; out[o++] = p[j * 3 + 2];
    }
  }
  return out;
}

function fill(faces: CollisionFaceWire[], color: number, opacity: number): THREE.Mesh {
  const g = new THREE.BufferGeometry();
  g.setAttribute('position', new THREE.BufferAttribute(triangulate(faces), 3));
  const mesh = new THREE.Mesh(g, new THREE.MeshBasicMaterial({
    color, transparent: true, opacity, side: THREE.DoubleSide, depthWrite: false,
    // Drawn just in front of the surfaces they lie on, so floors show on the floor they match.
    polygonOffset: true, polygonOffsetFactor: -2, polygonOffsetUnits: -2,
  }));
  mesh.renderOrder = 20;
  return mesh;
}

/** The whole layer as one group: floors and walls filled, every face outlined. */
export function buildCollisionObject(faces: CollisionFaceWire[]): THREE.Group {
  const group = new THREE.Group();
  group.name = 'collision';
  if (!faces.length) { return group; }
  const floors = faces.filter(f => f.up);
  const walls = faces.filter(f => !f.up);
  if (floors.length) { group.add(fill(floors, FLOOR_COLOR, 0.35)); }
  if (walls.length) { group.add(fill(walls, WALL_COLOR, 0.28)); }
  const lines = new THREE.BufferGeometry();
  lines.setAttribute('position', new THREE.BufferAttribute(outline(faces), 3));
  const edges = new THREE.LineSegments(lines, new THREE.LineBasicMaterial({ color: 0xffffff, transparent: true, opacity: 0.45, depthWrite: false }));
  edges.renderOrder = 21;
  group.add(edges);
  return group;
}

export function disposeGroup(group: THREE.Group): void {
  group.traverse(o => {
    const m = o as THREE.Mesh;
    m.geometry?.dispose();
    (m.material as THREE.Material | undefined)?.dispose?.();
  });
  group.clear();
}
