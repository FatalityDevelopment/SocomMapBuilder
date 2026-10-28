// Asset Viewer AI maps (AIMAPS.MPS): the map grids and their bounds, spawn points and waypoints as markers, and
// flying from a point.

import { ZedModel } from './zed-model-parser';
import { parseZedArchive } from './zed-archive-parser';
import type { SceneMarker } from './zed-model-renderer';
import { GeoInstance, transformPoint } from './zed-geo-parser';
import { groundHeight, MapPoint, parseMapPoints } from './zed-spawn-points';
import { isSkyChunk } from './zipper-workbench-model-render';
import type { ViewerPageLogic } from './zipper-workbench-page';
import type { Bounds3, MapCameraReference, MpsMap, MpsResult } from './zipper-workbench-types';

export const aiMapMethods = {
  computeActiveMapCameraReference(this: ViewerPageLogic): MapCameraReference | null {
    const mdl = this.model();
    if (!mdl) { return null; }
    const modelBounds = this.boundsFromVertices(mdl.vertices);
    return this.nearestPackageMapReference(modelBounds);
  },

  // ------------------------------------------------------------------
  // Spawn camera. Points come from the package's AIMAPS.MPS (see
  // zed-spawn-points.ts); heights from the geometry in view.
  // ------------------------------------------------------------------
  loadMapPoints(this: ViewerPageLogic, results: MpsResult[]): void {
    this.mapPoints.set([]);
    this.selectedMapPoint.set('');
    for (const result of results) {
      const grid = result.maps[0];
      const leaf = (result.sourcePath.split(/[\\/]/).pop() ?? '').trim().toLowerCase();
      const bytes = this.packageEntryBytes.get(leaf);
      if (!grid || !bytes) { continue; }
      const points = parseMapPoints(bytes, {
        minX: grid.minX, minZ: grid.minZ, minY: grid.minY, maxY: grid.maxY,
        scaleX: grid.scaleX, scaleZ: grid.scaleZ, width: grid.width, height: grid.height,
      });
      if (points.length > 0) {
        this.mapPoints.set(points);
        const first = points.find(p => p.name.toLowerCase() === 'startplayer') ?? points.find(p => p.kind === 'start') ?? points[0];
        this.selectedMapPoint.set(first.name);
        this.log(`[spawn] ${points.length} point(s) from ${leaf}: ${points.map(p => p.name).join(', ')}`);
        break;
      }
    }
    // MetersPerUnit lives in the map's config ZED (MP53.ZED).
    for (const [leaf, bytes] of this.packageEntryBytes) {
      if (!/^[a-z]+\d+\.zed$/i.test(leaf)) { continue; }
      try {
        const archive = parseZedArchive(bytes);
        const entry = archive.entries.find(e => e.name === 'MetersPerUnit');
        const data = entry ? archive.entryData.get(entry.index) : undefined;
        if (data && data.length >= 4) {
          const mpu = new DataView(data.buffer, data.byteOffset, 4).getFloat32(0, true);
          if (Number.isFinite(mpu) && mpu > 0 && mpu < 100) { this.unitsPerMeter = 1 / mpu; }
          break;
        }
      } catch { /* not a config archive */ }
    }
  },

  mapGridMaxY(this: ViewerPageLogic): number {
    const grid = this.packageMpsResults()[0]?.maps[0];
    return grid ? grid.maxY + 2 * this.unitsPerMeter : Number.POSITIVE_INFINITY;
  },

  pointHeight(this: ViewerPageLogic, point: MapPoint, mdl: ZedModel | null): number | null {
    if (!mdl) { return null; }
    // Sky domes and sun cards are never ground.
    const visible = { chunks: mdl.chunks.filter(c => !isSkyChunk(c) && !/lightrays/i.test(c.textureName ?? '')) };
    return groundHeight(visible, point.x, point.z, this.mapGridMaxY());
  },

  // Markers only make sense on geometry that covers the AI grid (the world
  // or a composed scene), not on a single prop.
  mapMarkersFor(this: ViewerPageLogic, mdl: ZedModel): SceneMarker[] | null {
    const points = this.mapPoints();
    if (!this.showMapMarkers() || points.length === 0) { return null; }
    const bounds = this.boundsFromVertices(mdl.chunks.flatMap(c => c.vertices));
    const grid = this.packageMpsResults()[0]?.maps[0];
    if (!bounds || !grid) { return null; }
    const coversGrid = (bounds.maxX - bounds.minX) > (grid.maxX - grid.minX) * 0.5 &&
      (bounds.maxZ - bounds.minZ) > (grid.maxZ - grid.minZ) * 0.5;
    if (!coversGrid) { return null; }
    return points.map(p => ({
      position: [p.x, this.pointHeight(p, mdl) ?? grid.minY, p.z] as [number, number, number],
      label: p.name,
      color: p.kind === 'start' ? (/hostage/i.test(p.name) ? 0xffa040 : 0x40e0a0) : 0x5aa0ff,
      height: 2.5 * this.unitsPerMeter,
    }));
  },

  onSelectedMapPointChange(this: ViewerPageLogic, event: Event): void {
    this.selectedMapPoint.set((event.target as HTMLSelectElement).value);
    // While flying, jump straight to the new point.
    if (this.flying()) { this.flyFromSelectedPoint(); }
  },

  onShowSkyChange(this: ViewerPageLogic, event: Event): void {
    this.showSky.set((event.target as HTMLInputElement).checked);
    this.applyModelToRenderer();
  },

  onShowMapMarkersChange(this: ViewerPageLogic, event: Event): void {
    this.showMapMarkers.set((event.target as HTMLInputElement).checked);
    this.applyModelToRenderer();
  },

  onMapLightingChange(this: ViewerPageLogic, event: Event): void {
    this.mapLighting.set((event.target as HTMLSelectElement).value as ReturnType<typeof this.mapLighting>);
    this.applyModelToRenderer();
  },

  // First-person camera at the selected point: on the ground found under
  // it, at eye height, facing the middle of the geometry.
  flyFromSelectedPoint(this: ViewerPageLogic): void {
    const mdl = this.model();
    const point = this.mapPoints().find(p => p.name === this.selectedMapPoint());
    if (!mdl || !point || !this.modelRenderer) { return; }
    const ground = this.pointHeight(point, mdl);
    const grid = this.packageMpsResults()[0]?.maps[0];
    const y = (ground ?? grid?.minY ?? 0) + 1.7 * this.unitsPerMeter;
    const bounds = this.boundsFromVertices(mdl.chunks.flatMap(c => c.vertices));
    const cx = bounds ? (bounds.minX + bounds.maxX) / 2 : point.x;
    const cz = bounds ? (bounds.minZ + bounds.maxZ) / 2 : point.z + 1;
    const yaw = Math.atan2(cx - point.x, cz - point.z);
    this.modelRenderer.startFly([point.x, y, point.z], yaw, 6 * this.unitsPerMeter);
    this.log(`[spawn] flying from ${point.name} at (${point.x.toFixed(0)}, ${y.toFixed(0)}, ${point.z.toFixed(0)})${ground === null ? ' (no ground found under it)' : ''}`);
  },

  exitFly(this: ViewerPageLogic): void {
    this.modelRenderer?.stopFly();
  },

  parsePackageMpsResults(this: ViewerPageLogic): MpsResult[] {
    const results: MpsResult[] = [];
    const packagePath = this.result()?.path ?? 'package';
    for (const [leaf, bytes] of this.packageEntryBytes) {
      if (!/\.mps$/i.test(leaf)) { continue; }
      const parsed = this.parseMpsBytes(bytes, `${packagePath} / ${leaf}`);
      if (parsed && parsed.foundCount > 0) { results.push(parsed); }
    }
    return results;
  },

  parseMpsBytes(this: ViewerPageLogic, bytes: Uint8Array, sourcePath: string): MpsResult | null {
    if (!bytes || bytes.length < 0x28) { return null; }
    const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    const declaredCount = view.getUint32(0x04, true);
    const maps: MpsMap[] = [];
    for (let o = 0x28; o <= bytes.length - 0x80; o += 4) {
      if (view.getUint32(o, true) !== 0x17) { continue; }
      if (o + 0x58 > bytes.length) { continue; }
      const width = view.getUint32(o + 0x1C, true);
      const height = view.getUint32(o + 0x20, true);
      const scaleX = view.getFloat32(o + 0x24, true);
      const scaleZ = view.getFloat32(o + 0x28, true);
      if (width === 0 || width > 5000 || height === 0 || height > 5000) { continue; }
      if (!this.isFinitePositive(scaleX) || !this.isFinitePositive(scaleZ)) { continue; }
      const name = this.readFixedAscii(bytes, o + 0x38, 0x20);
      if (!name) { continue; }
      const map: MpsMap = {
        offset: o,
        name,
        width,
        height,
        minX: view.getFloat32(o + 0x04, true),
        minY: view.getFloat32(o + 0x08, true),
        minZ: view.getFloat32(o + 0x0C, true),
        maxX: view.getFloat32(o + 0x10, true),
        maxY: view.getFloat32(o + 0x14, true),
        maxZ: view.getFloat32(o + 0x18, true),
        scaleX,
        scaleZ,
      };
      if (!this.boundsAreFinite(map)) { continue; }
      maps.push(map);
      if (declaredCount > 0 && maps.length >= declaredCount) { break; }
    }
    return {
      sourcePath,
      declaredCount,
      foundCount: maps.length,
      fileSize: bytes.length,
      maps,
    };
  },

  readFixedAscii(this: ViewerPageLogic, bytes: Uint8Array, offset: number, maxLen: number): string {
    const end = Math.min(offset + maxLen, bytes.length);
    let s = '';
    for (let i = offset; i < end; i++) {
      const b = bytes[i];
      if (b === 0) { break; }
      if (b < 0x20 || b > 0x7e) { return ''; }
      s += String.fromCharCode(b);
    }
    return s.trim();
  },

  isFinitePositive(this: ViewerPageLogic, value: number): boolean {
    return Number.isFinite(value) && value > 0 && value < 1_000_000;
  },

  boundsAreFinite(this: ViewerPageLogic, map: MpsMap): boolean {
    return [map.minX, map.minY, map.minZ, map.maxX, map.maxY, map.maxZ].every(Number.isFinite);
  },

  boundsFromVertices(this: ViewerPageLogic, vertices: ZedModel['vertices']): Bounds3 | null {
    if (!vertices.length) { return null; }
    const bounds = this.emptyBounds();
    for (const v of vertices) { this.includeBoundsPoint(bounds, v.x, v.y, v.z); }
    return bounds;
  },

  boundsFromGeoInstances(this: ViewerPageLogic, instances: GeoInstance[]): Bounds3 | null {
    if (!instances.length) { return null; }
    const bounds = this.emptyBounds();
    for (const inst of instances) {
      const [x, y, z] = transformPoint(inst.matrix, 0, 0, 0);
      this.includeBoundsPoint(bounds, x, y, z);
    }
    return bounds;
  },

  boundsFromMpsMap(this: ViewerPageLogic, map: MpsMap): Bounds3 {
    return {
      minX: map.minX,
      minY: map.minY,
      minZ: map.minZ,
      maxX: map.maxX,
      maxY: map.maxY,
      maxZ: map.maxZ,
    };
  },

  emptyBounds(this: ViewerPageLogic): Bounds3 {
    return {
      minX: Number.POSITIVE_INFINITY,
      minY: Number.POSITIVE_INFINITY,
      minZ: Number.POSITIVE_INFINITY,
      maxX: Number.NEGATIVE_INFINITY,
      maxY: Number.NEGATIVE_INFINITY,
      maxZ: Number.NEGATIVE_INFINITY,
    };
  },

  includeBoundsPoint(this: ViewerPageLogic, bounds: Bounds3, x: number, y: number, z: number): void {
    if (!Number.isFinite(x) || !Number.isFinite(y) || !Number.isFinite(z)) { return; }
    bounds.minX = Math.min(bounds.minX, x);
    bounds.minY = Math.min(bounds.minY, y);
    bounds.minZ = Math.min(bounds.minZ, z);
    bounds.maxX = Math.max(bounds.maxX, x);
    bounds.maxY = Math.max(bounds.maxY, y);
    bounds.maxZ = Math.max(bounds.maxZ, z);
  },

  formatBounds(this: ViewerPageLogic, bounds: Bounds3): string {
    const f = (value: number) => Number.isFinite(value) ? value.toFixed(2) : String(value);
    return `min=(${f(bounds.minX)}, ${f(bounds.minY)}, ${f(bounds.minZ)}) max=(${f(bounds.maxX)}, ${f(bounds.maxY)}, ${f(bounds.maxZ)}) size=(${f(bounds.maxX - bounds.minX)}, ${f(bounds.maxY - bounds.minY)}, ${f(bounds.maxZ - bounds.minZ)})`;
  },

  logNearestPackageMapBounds(this: ViewerPageLogic, reference: Bounds3 | null): void {
    const refs = this.sortedPackageMapReferences(reference);
    if (!refs.length) {
      this.log('[bounds] no package MPS bounds loaded');
      return;
    }
    this.log(`[bounds] nearest MPS map bounds (${refs.length} available):`);
    for (const item of refs.slice(0, 3)) {
      const leaf = item.source.split(/[\\/]/).pop() ?? item.source;
      this.log(`  ${item.map.name} (${leaf}) grid=${item.map.width}x${item.map.height} scale=${item.map.scaleX.toFixed(3)}x${item.map.scaleZ.toFixed(3)} ${this.formatBounds(item.bounds)}`);
    }
  },

  nearestPackageMapReference(this: ViewerPageLogic, reference: Bounds3 | null): MapCameraReference | null {
    return this.sortedPackageMapReferences(reference)[0] ?? null;
  },

  sortedPackageMapReferences(this: ViewerPageLogic, reference: Bounds3 | null): MapCameraReference[] {
    const refs = this.packageMpsResults().flatMap(result =>
      result.maps.map(map => {
        const bounds = this.boundsFromMpsMap(map);
        const leaf = result.sourcePath.split(/[\\/]/).pop() ?? result.sourcePath;
        return {
          source: result.sourcePath,
          map,
          bounds,
          label: `${map.name} (${leaf})`,
          distance: reference ? this.boundsDistanceSquared(reference, bounds) : 0,
        };
      }),
    );
    return refs.sort((a, b) => a.distance - b.distance);
  },

  boundsDistanceSquared(this: ViewerPageLogic, a: Bounds3, b: Bounds3): number {
    const ac = this.boundsCenter(a);
    const bc = this.boundsCenter(b);
    const dx = ac[0] - bc[0];
    const dy = ac[1] - bc[1];
    const dz = ac[2] - bc[2];
    return dx * dx + dy * dy + dz * dz;
  },

  boundsCenter(this: ViewerPageLogic, bounds: Bounds3): [number, number, number] {
    return [
      (bounds.minX + bounds.maxX) * 0.5,
      (bounds.minY + bounds.maxY) * 0.5,
      (bounds.minZ + bounds.maxZ) * 0.5,
    ];
  },
};

export type AiMapMethods = typeof aiMapMethods;
