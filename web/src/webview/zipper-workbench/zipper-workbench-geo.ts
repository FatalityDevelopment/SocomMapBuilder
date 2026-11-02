// Asset Viewer GEO scenes (_GEO archives): placing models by their GEO nodes and composing a map's whole scene.

import { applyMarkerBinding, applyWeaponDescriptorBinding, ChainOffset, chainsAreLineOnly, convertPositionUnit, decodeFallbackPayload, parseModel, PositionUnit, ZedModel } from './zed-model-parser';
import { InspectedZedArchive } from './zed-archive-parser';
import { collectVisualPartNames, findSceneNode, GeoNode, GeoScene, instanceNameVariants, transformPoint } from './zed-geo-parser';
import { placeModelByGeo, type PositionUnitChoice } from '../zipper-core/model-placement';
import { chooseMapScene, collectSceneInstances, definitionNodeFor, mapSceneRoot } from './zed-scene-compose';
import { chainOffsetsForChildren, identityMatrix, multiplyMatrices, transformVertices, tryReadLocalMatrix } from './zipper-workbench-model-compose';
import type { ViewerPageLogic } from './zipper-workbench-page';
import { namespaceFromArchiveName } from './zipper-workbench-textures';

export const geoMethods = {
  // The GEO node that defines a model (same lookup as the unit choice).
  findGeoNodeForModel(this: ViewerPageLogic, entryName: string | null | undefined, sourceArchiveName: string | null | undefined): GeoNode | null {
    if (!entryName) { return null; }
    // The archive's own scene first, then the map scene (WORL_MDL has no
    // WORL_GEO: worldmodel lives in MP53_GEO), then every loaded scene.
    const scenes: GeoScene[] = [];
    const add = (scene: GeoScene | null) => { if (scene && !scenes.includes(scene)) { scenes.push(scene); } };
    const namespace = namespaceFromArchiveName(sourceArchiveName);
    if (namespace) { add(this.companionScenesByNamespace.get(namespace) ?? null); }
    add(this.mapScene());
    for (const scene of this.allGeoScenes()) { add(scene); }
    for (const scene of scenes) {
      for (const name of this.characterGeoNameCandidates(entryName)) {
        const node = findSceneNode(scene, name);
        if (node && node.children.some(c => c.name === 'visuals' || c.name === 'children')) { return node; }
      }
    }
    return null;
  },

  allGeoScenes(this: ViewerPageLogic): GeoScene[] {
    const scenes = [...this.companionScenesByNamespace.values()];
    const legacy = this.companionScene();
    if (legacy && !scenes.includes(legacy)) { scenes.push(legacy); }
    return scenes;
  },

  // The map's scene graph: the GEO archive that places the world
  // (worldmodel), preferring the map's own prefix (MP53_GEO). Package order
  // puts RUN\COMMON first, so "the first GEO" was EFFE_GEO.
  mapScene(this: ViewerPageLogic): GeoScene | null {
    const candidates = [...this.companionScenesByNamespace.entries()].map(([namespace, scene]) => ({ namespace, scene }));
    const chosen = chooseMapScene(candidates, this.primaryMapTextureNamespace());
    return chosen?.scene ?? this.companionScene();
  },

  // Put every chain-decoded chunk where its GEO node says: each visual's
  // positions are converted with that visual's own vparams unit and then
  // transformed by its node's accumulated matrix (relative to the model
  // root). Returns null when the chunks carry no visual keys or none of
  // them resolve, so callers fall back to the whole-model unit choice.
  placeModelByGeo(this: ViewerPageLogic, mdl: ZedModel, geoNode: GeoNode | null): { model: ZedModel; choice: PositionUnitChoice; matched: number } | null {
    const placed = placeModelByGeo(mdl, geoNode, this.mapPositionUnit());
    if (!placed) {
      if (geoNode && mdl.chunks.some(c => c.visualKey)) {
        this.log(`[placement] ${geoNode.name}: none of the visual keys matched a GEO node`);
      }
      return null;
    }
    this.log(`[placement] ${geoNode!.name}: ${placed.matched}/${placed.total} visual(s) placed by GEO node (${placed.mode}), units ${[...placed.unitCounts].map(([u, n]) => `${u}=${n}`).join(', ')}`);
    return placed;
  },

  companionSceneForArchive(this: ViewerPageLogic, archiveName: string | null | undefined): GeoScene | null {
    const namespace = namespaceFromArchiveName(archiveName);
    if (namespace) {
      const scene = this.companionScenesByNamespace.get(namespace);
      if (scene) { return scene; }
    }
    return this.companionScene();
  },

  dumpGeoReport(this: ViewerPageLogic): void {
    const scene = this.mapScene();
    if (!scene) { this.log('[DUMP geo] no GEO scene loaded', true); return; }
    this.log('--- GEO report ---', true);
    const entry = this.selectedEntry();
    if (entry) {
      const node = findSceneNode(scene, entry.name);
      const parts = collectVisualPartNames(node);
      this.log(`Selected entry ${entry.name}: ${parts.length} visual part(s)`);
      for (let i = 0; i < parts.length; i++) { this.log(`  part[${i}] = ${parts[i]}`); }
    } else {
      this.log('(no entry selected)');
    }
  },

  composeGeoScene(this: ViewerPageLogic, options: { fromSceneRoot?: boolean } = {}): boolean {
    const scene = this.mapScene();
    if (!scene) {
      this.log('[GEO] no GEO scene loaded for this package', true);
      return false;
    }
    const entry = this.selectedEntry();
    // Use the currently-selected node as the GEO root when it matches a
    // scene node (e.g. user clicks `worldmodel`); otherwise start at the
    // very top of the GEO tree.
    let root = entry && !options.fromSceneRoot ? findSceneNode(scene, entry.name) : null;
    const wholeMap = !root;
    if (!root) { root = mapSceneRoot(scene); }
    // Build a model lookup map across EVERY *_MDL.ZED in the package by
    // walking each archive's full entry tree (HTML reference behaviour —
    // CLIB / FLIB style packages have model nodes nested as MESH_*
    // descendants of skeleton roots, not at the top level). Each entry
    // contributes multiple lookup keys (lowercased, compacted, stripped
    // of LOD/index suffixes, MESH_-prefixed) so GEO placements with name
    // variants resolve cleanly.
    type ModelHit = { node: { index: number; name: string; size: number; children: number[] }; archive: InspectedZedArchive; sourceFile: string; bytes: Uint8Array };
    const mdlArchives = this.collectModelLookupArchives();
    const modelLookup = new Map<string, ModelHit[]>();
    for (const { leaf, archive } of mdlArchives) {
      for (const e of archive.entries) {
        const bytes = archive.entryData.get(e.index);
        if ((!bytes || bytes.length === 0) && e.children.length === 0) { continue; }
        const hit: ModelHit = {
          node: { index: e.index, name: e.name, size: e.size, children: e.children },
          archive,
          sourceFile: leaf,
          bytes: bytes ?? new Uint8Array(0),
        };
        for (const key of instanceNameVariants(e.name)) {
          const list = modelLookup.get(key);
          if (list) { list.push(hit); } else { modelLookup.set(key, [hit]); }
        }
      }
    }

    const preferOrder = this.modelSourcePreferOrder();
    const resolve = (name: string): ModelHit | null => {
      if (name.toLowerCase() === 'footbomb') {
        const satchelHits = modelLookup.get('satchel');
        const satchel = satchelHits?.find(x => x.sourceFile === 'flib_mdl.zed') ?? satchelHits?.[0];
        if (satchel) { return satchel; }
      }
      for (const key of instanceNameVariants(name)) {
        const hits = modelLookup.get(key);
        if (!hits || hits.length === 0) { continue; }
        for (const file of preferOrder) {
          const h = hits.find(x => x.sourceFile === file);
          if (h) { return h; }
        }
        return hits[0];
      }
      return null;
    };

    // Nodes that are models themselves (worldmodel, a selected weapon root)
    // are placed once with their whole subtree; model_name references are
    // always placed; other nodes with visuals are tried by name.
    const instances = collectSceneInstances(scene, root, name => !!resolve(name));
    // The whole map also gets its vegetation and rocks from CLUTTER.ZAR.
    if (wholeMap) { instances.push(...this.clutterInstances(scene, root, resolve)); }
    if (instances.length === 0) {
      this.log(`[GEO] no placeable instances found under ${root.name}`, true);
      return false;
    }

    // Compose. Each instance contributes a transformed copy of its model's
    // vertices and a re-based index list. Cache parsed models by source so
    // repeated placements don't re-parse.
    const meshCache = new Map<string, ZedModel>();
    const composedVertices: ZedModel['vertices'] = [];
    const composedIndices: number[] = [];
    const composedChunks: ZedModel['chunks'] = [];
    const composedTextureRefs = new Set<string>();
    const missing: string[] = [];
    let rendered = 0;
    let fallbackUsed = 0;

    const decodeHit = (hit: ModelHit): ZedModel | null => {
      const descriptorOffsetsFor = (archive: InspectedZedArchive, childIndices: number[]): ChainOffset[] =>
        chainOffsetsForChildren(archive, childIndices);
      const isWeaponSource = /(^|[\\/])weap_mdl(?:\([^)]+\))?\.zed$/i.test(hit.sourceFile);
      const isEffectSource = /(^|[\\/])effe_mdl(?:\([^)]+\))?\.zed$/i.test(hit.sourceFile);
      const bindMode = isWeaponSource || isEffectSource ? 'tailScan' : this.mapMaterialBinding();
      const mergePayload = (
        target: ZedModel,
        local: ZedModel,
        matrix: number[],
        sourceModelName: string,
      ) => {
        const vertexBase = target.vertices.length;
        const chunkBase = target.chunks.length;
        for (const v of local.vertices) {
          const [tx, ty, tz] = transformPoint(matrix, v.x, v.y, v.z);
          target.vertices.push({ ...v, x: tx, y: ty, z: tz });
        }
        for (const idx of local.indices) { target.indices.push(vertexBase + idx); }
        for (const c of local.chunks) {
          target.chunks.push({
            ...c,
            vertices: transformVertices(c.vertices, matrix),
            sourceModelName: c.sourceModelName ?? sourceModelName,
            sourceArchiveName: c.sourceArchiveName ?? hit.sourceFile,
            offset: c.offset + chunkBase * 0x80,
          });
        }
        for (const ref of local.textureRefs) {
          if (!target.textureRefs.includes(ref)) { target.textureRefs.push(ref); }
        }
      };

      const decodePayloadModel = (bytes: Uint8Array, chainOffsets: ChainOffset[] = []): ZedModel | null => {
        let mdl: ZedModel | null = null;
        const descriptorOffsets = chainOffsets.map(c => c.offset);
        const parseOptions = this.parseOptionsFor(chainOffsets);
        try {
          mdl = isEffectSource
            ? (decodeFallbackPayload(bytes) ?? parseModel(bytes, parseOptions))
            : parseModel(bytes, parseOptions);
        } catch { mdl = null; }
        // Line-strip models (wires) are not drawn; the fallback scan made ribbons of them.
        if ((!mdl || mdl.chunks.length === 0) && chainOffsets.length > 0 && chainsAreLineOnly(bytes, chainOffsets)) {
          return null;
        }
        if (!mdl || mdl.chunks.length === 0) {
          const fb = decodeFallbackPayload(bytes);
          if (fb && fb.chunks.length > 0) {
            mdl = fb;
            fallbackUsed++;
          }
        }
        const chainBound = !!mdl && mdl.chunks.some(c => c.textureBinding === 'chain');
        if (mdl && isWeaponSource && !chainBound && descriptorOffsets.length > 0 && mdl.chunks.length > 0) {
          try { applyWeaponDescriptorBinding(mdl, bytes, descriptorOffsets); }
          catch { /* keep tail-scan names */ }
        }
        if (mdl && !isWeaponSource && !isEffectSource && !chainBound && bindMode !== 'tailScan' && bindMode !== 'chain' && mdl.chunks.length > 0) {
          try { applyMarkerBinding(mdl, bytes, descriptorOffsets, bindMode); }
          catch { /* keep tail-scan names */ }
        }
        return mdl && mdl.chunks.length > 0 ? mdl : null;
      };

      if (hit.bytes.length > 0) {
        return decodePayloadModel(hit.bytes, descriptorOffsetsFor(hit.archive, hit.node.children));
      }

      const out: ZedModel = {
        chunks: [],
        vertices: [],
        indices: [],
        textureRefs: [],
        boundingRadius: 1,
      };
      const visit = (entryIndex: number, parentMatrix: number[]) => {
        const ent = hit.archive.entries.find(e => e.index === entryIndex);
        if (!ent) { return; }
        const bytes = hit.archive.entryData.get(ent.index) ?? new Uint8Array(0);
        const localMatrix = bytes.length > 0 ? tryReadLocalMatrix(bytes) : identityMatrix();
        const matrix = multiplyMatrices(parentMatrix, localMatrix);
        if (bytes.length > 0) {
          const local = decodePayloadModel(bytes, descriptorOffsetsFor(hit.archive, ent.children));
          if (local) { mergePayload(out, local, matrix, hit.node.name); }
        }
        for (const childIdx of ent.children) { visit(childIdx, matrix); }
      };
      for (const childIdx of hit.node.children) { visit(childIdx, identityMatrix()); }
      if (out.vertices.length === 0) { return null; }
      let radius = 0;
      for (const v of out.vertices) {
        const d = Math.hypot(v.x, v.y, v.z);
        if (d > radius) { radius = d; }
      }
      out.boundingRadius = radius || 1;
      return out;
    };

    const unitCounts = new Map<string, number>();
    const rawCache = new Map<string, ZedModel | null>();
    const placedCache = new Map<string, ZedModel>();
    const allScenes = this.allGeoScenes();
    const kindCounts = instances.reduce<Record<string, number>>((acc, i) => { acc[i.kind] = (acc[i.kind] ?? 0) + 1; return acc; }, {});
    for (const inst of instances) {
      const hit = resolve(inst.modelName);
      if (!hit) { missing.push(inst.modelName); continue; }
      const bindKey = /(^|[\\/])weap_mdl(?:\([^)]+\))?\.zed$/i.test(hit.sourceFile)
        ? 'tailScan'
        : this.mapMaterialBinding();
      // Decode once per source model (scale convention), then pick the
      // unit per placement from the placed node's GEO bounds / flags.
      const rawKey = `${hit.sourceFile}|${hit.node.name}|${bindKey}`;
      let raw = rawCache.get(rawKey);
      if (raw === undefined) {
        raw = decodeHit(hit);
        rawCache.set(rawKey, raw);
      }
      if (!raw) { continue; }
      // The GEO node that defines this model's visuals: the placed node
      // itself when its subtree owns visuals, else the model's node in its
      // own library scene (TLLM_GEO for a turret MP53_GEO places).
      const ownNamespace = namespaceFromArchiveName(hit.sourceFile);
      const defNode = definitionNodeFor(
        inst.node,
        hit.node.name,
        ownNamespace ? this.companionScenesByNamespace.get(ownNamespace) ?? null : null,
        allScenes,
      );
      let mdl: ZedModel | undefined;
      const placedKey = `${rawKey}|${inst.path}|${this.mapPositionUnit()}`;
      mdl = placedCache.get(placedKey);
      if (!mdl) {
        const placed = this.placeModelByGeo(raw, defNode);
        if (placed) {
          mdl = placed.model;
          placedCache.set(placedKey, mdl);
          unitCounts.set('GEO placement', (unitCounts.get('GEO placement') ?? 0) + 1);
        }
      } else {
        unitCounts.set('GEO placement', (unitCounts.get('GEO placement') ?? 0) + 1);
      }
      if (!mdl) {
        const choice = this.choosePositionUnit(raw, hit.node.name, hit.sourceFile, inst.node);
        const unitLabel = `${choice.unit} (${choice.reason})`;
        unitCounts.set(unitLabel, (unitCounts.get(unitLabel) ?? 0) + 1);
        const cacheKey = `${rawKey}|${choice.unit}`;
        mdl = meshCache.get(cacheKey);
        if (!mdl) {
          mdl = convertPositionUnit(raw, choice.unit);
          meshCache.set(cacheKey, mdl);
        }
      }

      const vertexBase = composedVertices.length;
      const chunkBase = composedChunks.length;
      for (const v of mdl.vertices) {
        const [tx, ty, tz] = transformPoint(inst.matrix, v.x, v.y, v.z);
        composedVertices.push({ ...v, x: tx, y: ty, z: tz });
      }
      for (const idx of mdl.indices) { composedIndices.push(vertexBase + idx); }
      for (const c of mdl.chunks) {
        composedChunks.push({
          ...c,
          // The renderer draws chunk.vertices: apply the placement there.
          vertices: transformVertices(c.vertices, inst.matrix),
          sourceModelName: c.sourceModelName ?? hit.node.name ?? inst.modelName,
          sourceArchiveName: c.sourceArchiveName ?? hit.sourceFile,
          offset: c.offset + chunkBase * 0x80,
        });
      }
      for (const ref of mdl.textureRefs) { composedTextureRefs.add(ref); }
      rendered++;
    }

    if (composedVertices.length === 0) {
      this.log(`[GEO] composition produced no vertices (instances=${instances.length}, missing=${missing.length})`, true);
      return false;
    }

    let radius = 0;
    for (const v of composedVertices) {
      const d = Math.hypot(v.x, v.y, v.z);
      if (d > radius) { radius = d; }
    }
    const composedModel: ZedModel = {
      chunks: composedChunks,
      vertices: composedVertices,
      indices: composedIndices,
      textureRefs: [...composedTextureRefs],
      boundingRadius: radius || 1,
    };

    // The pane shows the unit most placements used; the log has the full
    // per-reason breakdown.
    const dominant = [...unitCounts.entries()].sort((a, b) => b[1] - a[1])[0];
    const dominantUnit: PositionUnit = dominant && dominant[0].startsWith('sixteenth') ? 'sixteenth' : 'scale';
    this.recordDecodeInfo(`GEO ${root.name}`, composedModel, { unit: dominantUnit, reason: 'per placement' });
    this.selectedVisual.set('');
    this.model.set(composedModel);
    this.previewMode.set('model');
    this.currentModelBytes = null; // composed scene has no single payload
    this.currentSliceOffsets = [];
    this.currentModelSourceArchive = null;
    setTimeout(() => this.applyModelToRenderer(), 0);

    const uniqueMissing = [...new Set(missing.map(m => m.toLowerCase()))];
    this.log('--- GEO scene composed ---', true);
    this.log(`Root: ${root.name}, instances=${instances.length} (${Object.entries(kindCounts).map(([k, n]) => `${k}=${n}`).join(', ')}), rendered=${rendered}, missingModels=${uniqueMissing.length}, fallbackDecodes=${fallbackUsed}`);
    this.log(`Composed model: ${composedVertices.length} verts, ${composedIndices.length / 3 | 0} tris, ${composedChunks.length} chunks across ${meshCache.size} unique source model(s)`);
    const chainBound = composedChunks.filter(c => c.textureBinding === 'chain').length;
    const coloured = composedChunks.filter(c => c.hasVertexColors).length;
    const unitSummary = [...unitCounts].map(([unit, n]) => `${unit}=${n}`).join(', ');
    this.log(`[chain] ${chainBound}/${composedChunks.length} chunk(s) texture-bound by DMA chain, ${coloured} with vertex colours; position units: ${unitSummary || '(none)'}`);
    this.modelDecodeInfo.update(info => info ? { ...info, unitReason: `per placement: ${unitSummary || 'none'}` } : info);
    const instanceBounds = this.boundsFromGeoInstances(instances);
    const composedBounds = this.boundsFromVertices(composedVertices);
    if (instanceBounds) { this.log(`[bounds] GEO instance origins ${this.formatBounds(instanceBounds)}`); }
    if (composedBounds) { this.log(`[bounds] composed vertices ${this.formatBounds(composedBounds)}`); }
    this.logNearestPackageMapBounds(composedBounds ?? instanceBounds);
    if (uniqueMissing.length) {
      this.log(`Missing model refs: ${uniqueMissing.slice(0, 60).join(', ')}${uniqueMissing.length > 60 ? '…' : ''}`);
    }
    return true;
  },

  // Walks the loaded GEO scene like composeGeoScene does, but instead of
  // building a mesh, logs every instance with its resolved/missing status,
  // the variant keys we tried, and the resolved MDL source file. This is
  // the fastest way to figure out why so many GEO instances aren't matching
  // any of the package's MDL nodes.
  dumpGeoInstances(this: ViewerPageLogic): void {
    const scene = this.mapScene();
    if (!scene) {
      this.log('[GEO instances] no GEO scene loaded for this package', true);
      return;
    }
    const entry = this.selectedEntry();
    let root = entry ? findSceneNode(scene, entry.name) : null;
    if (!root) { root = scene.root; }
    // Build the same lookup map composeGeoScene uses so the report
    // reflects exactly what compose will do.
    type ModelHit = { sourceFile: string; nodeName: string };
    const modelLookup = new Map<string, ModelHit[]>();
    for (const { leaf, archive } of this.collectModelLookupArchives()) {
      for (const e of archive.entries) {
        if (!e.size && e.children.length === 0) { continue; }
        for (const key of instanceNameVariants(e.name)) {
          const list = modelLookup.get(key);
          if (list) { list.push({ sourceFile: leaf, nodeName: e.name }); }
          else { modelLookup.set(key, [{ sourceFile: leaf, nodeName: e.name }]); }
        }
      }
    }

    const preferOrder = this.modelSourcePreferOrder();
    const resolve = (name: string): ModelHit | null => {
      if (name.toLowerCase() === 'footbomb') {
        const satchelHits = modelLookup.get('satchel');
        const satchel = satchelHits?.find(x => x.sourceFile === 'flib_mdl.zed') ?? satchelHits?.[0];
        if (satchel) { return satchel; }
      }
      for (const key of instanceNameVariants(name)) {
        const hits = modelLookup.get(key);
        if (!hits || hits.length === 0) { continue; }
        for (const file of preferOrder) {
          const h = hits.find(x => x.sourceFile === file);
          if (h) { return h; }
        }
        return hits[0];
      }
      return null;
    };

    const instances = collectSceneInstances(scene, root, name => !!resolve(name));

    let resolved = 0, missing = 0;
    const missingNames = new Set<string>();
    const resolutionDetails: string[] = [];

    for (const inst of instances) {
      const variants = instanceNameVariants(inst.modelName);
      const hit = resolve(inst.modelName);
      if (hit) {
        resolved++;
        if (resolutionDetails.length < 60) {
          resolutionDetails.push(`  OK   ${inst.modelName.padEnd(40)} -> ${hit.sourceFile} / ${hit.nodeName}`);
        }
      } else {
        missing++;
        missingNames.add(inst.modelName.toLowerCase());
        if (resolutionDetails.length < 60) {
          resolutionDetails.push(`  MISS ${inst.modelName.padEnd(40)} (variants tried: ${variants.slice(0, 4).join(', ')}${variants.length > 4 ? '…' : ''})`);
        }
      }
    }

    this.log('--- GEO instance dump ---', true);
    this.log(`Root: ${root.name}  instances=${instances.length}  resolved=${resolved}  missing=${missing}`);
    this.log(`Package MDL archives: ${[...this.packageEntryBytes.keys()].filter(k => /_MDL(?:\([^)]+\))?\.zed$/i.test(k)).join(', ')}`);
    const instanceBounds = this.boundsFromGeoInstances(instances);
    if (instanceBounds) { this.log(`[bounds] GEO instance origins ${this.formatBounds(instanceBounds)}`); }
    this.logNearestPackageMapBounds(instanceBounds);
    this.log(`First ${Math.min(resolutionDetails.length, 60)} instance(s):`);
    for (const line of resolutionDetails) { this.log(line); }
    if (missingNames.size > 0) {
      const sorted = [...missingNames].sort();
      this.log(`Unique missing names (${missingNames.size}):`);
      this.log(`  ${sorted.slice(0, 80).join(', ')}${sorted.length > 80 ? '…' : ''}`);
    }
  },
};

export type GeoMethods = typeof geoMethods;
