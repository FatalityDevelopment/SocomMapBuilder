// Placing a decoded model in its own space: each visual's chunks are moved by
// their GEO node's matrix and decoded in that visual's position unit. Shared
// by the Zipper workbench and the Model Editor (pure functions, no state).
// See docs/zipper-model-format.md "Placing visuals (GEO)".

import type { GeoNode } from '../zipper-workbench/zed-geo-parser';
import { buildVisualPlacementMap, positionUnitForNode, readNodeBounds, transformPoint } from '../zipper-workbench/zed-geo-parser';
import type { PositionUnit, ZedModel } from '../zipper-workbench/zed-model-parser';
import { convertPositionUnit, modelExtent } from '../zipper-workbench/zed-model-parser';

export type PositionUnitOverride = 'auto' | PositionUnit;

export interface PositionUnitChoice {
  unit: PositionUnit;
  reason: string;
}

export interface GeoPlacement {
  model: ZedModel;
  choice: PositionUnitChoice;
  matched: number;
  total: number;
  mode: 'visualNodes' | 'allNodes';
  unitCounts: Map<PositionUnit, number>;
}

// Place every visual by its GEO node (accumulated matrix, own vparams unit).
// Null when the node is missing or none of the model's visual keys match it.
export function placeModelByGeo(mdl: ZedModel, geoNode: GeoNode | null, override: PositionUnitOverride = 'auto'): GeoPlacement | null {
  if (!geoNode) { return null; }
  const keys = mdl.chunks.map(c => c.visualKey).filter((k): k is string => !!k);
  if (keys.length === 0) { return null; }
  const map = buildVisualPlacementMap(geoNode, keys);
  if (map.matched === 0) { return null; }
  const rootUnit: PositionUnit = positionUnitForNode(geoNode) ?? 'scale';
  const counts = new Map<PositionUnit, number>();
  let radius = 0;
  const chunks = mdl.chunks.map(c => {
    const placement = c.visualKey ? map.placements.get(c.visualKey) : undefined;
    const unit: PositionUnit = override !== 'auto' ? override : (placement?.unit ?? rootUnit);
    counts.set(unit, (counts.get(unit) ?? 0) + 1);
    const from = c.positionUnit ?? 'scale';
    const f = from === unit ? 1 : unit === 'sixteenth' ? 32768 / 16 : 16 / 32768;
    const [ox, oy, oz] = c.origin ?? [0, 0, 0];
    const vertices = c.vertices.map(v => {
      let x = (v.x - ox) * f + ox, y = (v.y - oy) * f + oy, z = (v.z - oz) * f + oz;
      if (placement) { [x, y, z] = transformPoint(placement.matrix, x, y, z); }
      const d = Math.hypot(x, y, z);
      if (d > radius) { radius = d; }
      return { ...v, x, y, z };
    });
    return { ...c, vertices, positionUnit: unit };
  });
  const vertices: ZedModel['vertices'] = [];
  for (const c of chunks) { for (const v of c.vertices) { vertices.push(v); } }
  const dominant = [...counts.entries()].sort((a, b) => b[1] - a[1])[0]?.[0] ?? 'scale';
  const unitNote = override !== 'auto' ? 'dropdown' : 'per-visual vparams';
  return {
    model: { ...mdl, chunks, vertices, boundingRadius: radius || 1 },
    choice: { unit: dominant, reason: `GEO placement ${map.matched}/${map.total} visuals, ${unitNote}` },
    matched: map.matched,
    total: map.total,
    mode: map.mode,
    unitCounts: counts,
  };
}

// How a model's int16 positions map to units when it is not placed by GEO:
//   1. the override
//   2. the GEO node's nparams bounds: the convention whose extent matches
//      them (the two differ by exactly 2048x, so this is unambiguous)
//   3. the GEO node's vparams flags (weapons normalise by the chunk scale,
//      everything else is 1/16 units)
//   4. world models: 1/16; otherwise the scale convention
export function choosePositionUnit(
  model: ZedModel | null,
  node: GeoNode | null,
  override: PositionUnitOverride = 'auto',
  isWorldModel = false,
): PositionUnitChoice {
  if (override !== 'auto') { return { unit: override, reason: 'dropdown' }; }
  const flagsUnit = positionUnitForNode(node);
  const bounds = readNodeBounds(node);
  if (model && bounds && model.vertices.length > 0) {
    // Extent of the model in each convention versus the GEO bounds (the
    // chunk origins stay fixed, so convert rather than scale the extent).
    const extentScale = modelExtent(convertPositionUnit(model, 'scale'));
    const extentSixteenth = modelExtent(convertPositionUnit(model, 'sixteenth'));
    if (extentScale > 0 && extentSixteenth > 0) {
      const ratioScale = Math.abs(Math.log(extentScale / bounds.extent));
      const ratioSixteenth = Math.abs(Math.log(extentSixteenth / bounds.extent));
      // Only trust the bounds when one convention is clearly closer.
      if (Math.abs(ratioScale - ratioSixteenth) > 1) {
        return { unit: ratioSixteenth < ratioScale ? 'sixteenth' : 'scale', reason: 'GEO bounds' };
      }
    }
  }
  if (flagsUnit) { return { unit: flagsUnit, reason: 'GEO vparams flags' }; }
  // Map geometry is always in 1/16 units; without its GEO node the scale
  // convention would shrink the map 2048x.
  if (isWorldModel) { return { unit: 'sixteenth', reason: 'map world model' }; }
  return { unit: 'scale', reason: 'default' };
}
