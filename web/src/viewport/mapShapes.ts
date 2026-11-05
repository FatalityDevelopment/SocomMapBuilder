// Paths and regions drawn over a map (Star Wars: Battlefront): paths as lines through their nodes with a dot at each,
// regions as wireframe boxes, spheres and cylinders. View only; each object carries its name in userData.shape.

import * as THREE from 'three';
import type { MapPathWire, MapRegionWire } from '../webview/model-editor/asset-client';

export const PATH_COLORS: Record<string, number> = { spawn: 0x46d158, boundary: 0xff5040, path: 0x3e8bff };
export const REGION_COLORS: Record<string, number> = { capture: 0xffd23e, control: 0xff8a2a, sound: 0x9a7cff, region: 0x4fd6e0 };

/** A short description for the outliner. */
export function pathDetail(p: MapPathWire): string {
  return `${p.kind} · ${p.nodes.length} nodes${p.commandPost ? ` · ${p.commandPost}` : ''}`;
}
export function regionDetail(r: MapRegionWire): string {
  return `${r.role} · ${r.shape}${r.commandPost ? ` · ${r.commandPost}` : ''}`;
}

/** The unit outline of a region shape, scaled by its size (box half extents, sphere radius, cylinder radii / half height). */
export function regionGeometry(r: MapRegionWire): THREE.BufferGeometry {
  const [sx = 1, sy = 1, sz = 1] = r.size;
  if (r.shape === 'sphere') {
    // Three great circles read as a sphere without a mesh of lines.
    const rad = Math.max(sx, 0.1);
    const pts: number[] = [];
    const seg = 48;
    for (const axis of [0, 1, 2]) {
      for (let i = 0; i < seg; i++) {
        for (const k of [i, i + 1]) {
          const a = (k / seg) * Math.PI * 2, c = Math.cos(a) * rad, s = Math.sin(a) * rad;
          pts.push(...(axis === 0 ? [c, s, 0] : axis === 1 ? [c, 0, s] : [0, c, s]));
        }
      }
    }
    return new THREE.BufferGeometry().setAttribute('position', new THREE.Float32BufferAttribute(pts, 3));
  }
  if (r.shape === 'cylinder') {
    const cyl = new THREE.CylinderGeometry(1, 1, 2, 24, 1, true);
    const edges = new THREE.EdgesGeometry(cyl, 1);
    cyl.dispose();
    // Four upright edges, so the sides show.
    const pos = Array.from(edges.getAttribute('position').array as ArrayLike<number>);
    for (const a of [0, Math.PI / 2, Math.PI, Math.PI * 1.5]) { pos.push(Math.cos(a), -1, Math.sin(a), Math.cos(a), 1, Math.sin(a)); }
    edges.dispose();
    const g = new THREE.BufferGeometry().setAttribute('position', new THREE.Float32BufferAttribute(pos, 3));
    return g.scale(Math.max(sx, 0.1), Math.max(sy, 0.1), Math.max(sz || sx, 0.1));
  }
  const box = new THREE.BoxGeometry(2 * Math.max(sx, 0.05), 2 * Math.max(sy, 0.05), 2 * Math.max(sz, 0.05));
  const edges = new THREE.EdgesGeometry(box);
  box.dispose();
  return edges;
}

export function buildPathObject(p: MapPathWire, dot: number): THREE.Group {
  const color = PATH_COLORS[p.kind] ?? PATH_COLORS.path;
  const group = new THREE.Group();
  const pts = p.nodes.map(n => new THREE.Vector3(n[0], n[1] + 0.3, n[2]));
  if (p.closed && pts.length > 2) { pts.push(pts[0].clone()); }
  const lineMat = new THREE.LineBasicMaterial({ color, transparent: true, opacity: 0.95, depthTest: true });
  const line = new THREE.Line(new THREE.BufferGeometry().setFromPoints(pts), lineMat);
  line.renderOrder = 9;
  group.add(line);
  const dots = new THREE.Points(new THREE.BufferGeometry().setFromPoints(pts),
    new THREE.PointsMaterial({ color, size: dot, sizeAttenuation: true, depthTest: true }));
  dots.renderOrder = 9;
  group.add(dots);
  group.userData['shape'] = p.name;
  return group;
}

export function buildRegionObject(r: MapRegionWire): THREE.LineSegments {
  const color = REGION_COLORS[r.role] ?? REGION_COLORS.region;
  const seg = new THREE.LineSegments(regionGeometry(r), new THREE.LineBasicMaterial({ color, transparent: true, opacity: 0.9 }));
  seg.matrixAutoUpdate = false;
  seg.matrix.fromArray(r.matrix);
  seg.matrixWorldNeedsUpdate = true;
  seg.renderOrder = 9;
  seg.userData['shape'] = r.name;
  return seg;
}
