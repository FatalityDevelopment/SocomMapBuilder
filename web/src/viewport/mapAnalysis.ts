// The map check's checks that need the drawn scene (the backend's ASSET_VALIDATE cannot see geometry): props floating
// above the ground, copies stacked on each other, and areas that draw far more than the rest of the map. Pure: the
// viewport measures, this decides.

import type { MapProblem } from '../webview/model-editor/asset-client';

export interface Measured {
  model: string;
  instance: string;
  geoIndex: number | null;
  clutterIndex: number | null;
  position: number[];
  // The gap between the bottom of its mesh and the surface under it (null: nothing under it, or not measured).
  gap: number | null;
  triangles: number;
}

export interface AnalysisOptions {
  // Gaps over this float (units); the map's extent sets a floor so big maps are not flooded with info.
  floatGap: number;
  // Copies of one model closer than this are stacked.
  stackDistance: number;
  // Grid cell size for the triangle count; a cell is dense past both denseFactor × the average occupied cell and
  // denseTriangles, and only the `denseReport` densest are reported.
  cellSize: number;
  denseFactor: number;
  denseTriangles: number;
  denseReport: number;
}

export const DEFAULT_ANALYSIS: AnalysisOptions = { floatGap: 1, stackDistance: 0.05, cellSize: 256, denseFactor: 4, denseTriangles: 15000, denseReport: 5 };

function about(m: Measured): Partial<MapProblem> {
  return m.clutterIndex !== null ? { clutterIndex: m.clutterIndex } : m.geoIndex !== null ? { geoIndex: m.geoIndex } : {};
}

export function analyzeScene(items: Measured[], options: AnalysisOptions = DEFAULT_ANALYSIS): MapProblem[] {
  const problems: MapProblem[] = [];
  for (const m of items) {
    if (m.gap !== null && m.gap > options.floatGap) {
      problems.push({ severity: m.clutterIndex !== null ? 'warning' : 'info', code: 'floating', message: `${m.instance} (${m.model}) floats ${m.gap.toFixed(1)} units above the surface under it`, ...about(m) } as MapProblem);
    }
  }

  // Stacked copies: the same model at (almost) the same place, found through a grid of the stack distance.
  const s = Math.max(1e-6, options.stackDistance);
  const cells = new Map<string, Measured[]>();
  const key = (x: number, y: number, z: number) => `${Math.floor(x / s)},${Math.floor(y / s)},${Math.floor(z / s)}`;
  for (const m of items) {
    const [x, y, z] = m.position;
    let stacked: Measured | null = null;
    for (let dx = -1; dx <= 1 && !stacked; dx++) {
      for (let dy = -1; dy <= 1 && !stacked; dy++) {
        for (let dz = -1; dz <= 1 && !stacked; dz++) {
          const near = cells.get(key(x + dx * s, y + dy * s, z + dz * s)) ?? [];
          stacked = near.find(o => o.model === m.model && Math.hypot(o.position[0] - x, o.position[1] - y, o.position[2] - z) <= s) ?? null;
        }
      }
    }
    if (stacked) {
      problems.push({ severity: 'warning', code: 'stacked', message: `${m.instance} is a second ${m.model} on top of ${stacked.instance} (delete one)`, ...about(m) } as MapProblem);
    }
    const k = key(x, y, z);
    cells.set(k, [...(cells.get(k) ?? []), m]);
  }

  // Dense areas: cells of the XZ grid drawing far more triangles than the average occupied cell.
  const c = Math.max(1, options.cellSize);
  const load = new Map<string, { triangles: number; count: number; first: Measured }>();
  for (const m of items) {
    const k = `${Math.floor(m.position[0] / c)},${Math.floor(m.position[2] / c)}`;
    const cell = load.get(k) ?? { triangles: 0, count: 0, first: m };
    cell.triangles += m.triangles;
    cell.count++;
    load.set(k, cell);
  }
  if (load.size >= 4) {
    const average = [...load.values()].reduce((a, v) => a + v.triangles, 0) / load.size;
    const dense = [...load].filter(([, cell]) => cell.count > 1 && cell.triangles > Math.max(average * options.denseFactor, options.denseTriangles))
      .sort((a, b) => b[1].triangles - a[1].triangles).slice(0, options.denseReport);
    for (const [k, cell] of dense) {
      const [cx, cz] = k.split(',').map(Number);
      problems.push({ severity: 'info', code: 'dense-area', message: `The ${c}×${c} area at (${(cx * c).toFixed(0)}, ${(cz * c).toFixed(0)}) draws ${cell.triangles.toLocaleString()} triangles in ${cell.count} placements, ${(cell.triangles / average).toFixed(1)}× the map's average`, ...about(cell.first) } as MapProblem);
    }
  }
  return problems;
}
