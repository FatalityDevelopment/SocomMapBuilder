// Asset Viewer clutter (CLUTTER.ZAR): the clutter model groups and their placements in a map scene.

import { parseZedArchive } from './zed-archive-parser';
import { findSceneNode, GeoNode, GeoScene, positionUnitForNode } from './zed-geo-parser';
import { parseClutterPlacements, type SceneInstance } from './zed-scene-compose';
import type { ViewerPageLogic } from './zipper-workbench-page';
import type { ZipperEntryInfo } from './zipper-workbench-types';

export const clutterMethods = {
  // CLUTTER.ZAR › Clutter › <model>: the model group of a clutter placement list.
  clutterGroupName(this: ViewerPageLogic, entry: ZipperEntryInfo): string | null {
    if (!/CLUTTER\.ZAR$/i.test(this.currentFileName())) { return null; }
    const parents = this.entryParents();
    const parent = parents?.get(entry.index);
    if (parent === undefined || parent < 0) { return null; }
    const p = this.rawEntries().find(e => e.index === parent);
    return p?.name === 'Clutter' && parents?.get(parent) === -1 ? entry.name : null;
  },

  // CLUTTER.ZAR placements as scene instances. Each is drawn with its
  // prototype's GEO definition (the node in the scene's `models` list).
  clutterInstances(this: ViewerPageLogic, scene: GeoScene, root: GeoNode, resolve: (name: string) => unknown): SceneInstance[] {
    const bytes = this.packageEntryBytes.get('clutter.zar');
    if (!bytes || bytes.length === 0) { return []; }
    let placements;
    try { placements = parseClutterPlacements(parseZedArchive(bytes)); }
    catch (err) {
      this.log(`[clutter] CLUTTER.ZAR could not be read: ${err instanceof Error ? err.message : String(err)}`);
      return [];
    }
    const out: SceneInstance[] = [];
    const counts = new Map<string, number>();
    const unresolved = new Set<string>();
    for (const p of placements) {
      if (!resolve(p.modelName)) { unresolved.add(p.modelName); continue; }
      const node = findSceneNode(scene, p.modelName) ?? root;
      out.push({
        modelName: p.modelName,
        instanceName: p.modelName,
        path: `/clutter/${p.modelName}`,
        matrix: p.matrix,
        node,
        positionUnit: positionUnitForNode(node),
        kind: 'clutter',
      });
      counts.set(p.modelName, (counts.get(p.modelName) ?? 0) + 1);
    }
    this.log(`[clutter] ${out.length}/${placements.length} placement(s): ${[...counts].map(([n, c]) => `${n}×${c}`).join(', ') || 'none'}` +
      (unresolved.size ? `; no model for ${[...unresolved].join(', ')}` : ''));
    return out;
  },
};

export type ClutterMethods = typeof clutterMethods;
