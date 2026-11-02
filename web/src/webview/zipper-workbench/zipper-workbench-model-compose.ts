// Asset Viewer models built from a record hierarchy: characters (MESH_* parts placed by their GEO bones) and other
// multi-part nodes composed into one model.

import { applyMarkerBinding, applyWeaponDescriptorBinding, ChainOffset, convertPositionUnit, decodeCharacterFallbackPayload, decodeFallbackPayload, parseModel, PositionUnit, ZedModel } from './zed-model-parser';
import { InspectedZedArchive } from './zed-archive-parser';
import { findSceneNode, GeoNode, readGeoTransform, transformPoint } from './zed-geo-parser';
import type { ViewerPageLogic } from './zipper-workbench-page';

export const modelComposeMethods = {
  characterGeoNameCandidates(this: ViewerPageLogic, entryName: string): string[] {
    const leaf = (entryName.split(/[\\/]/).pop() ?? entryName).replace(/\.[^.]+$/i, '');
    const withoutPrefix = leaf.replace(/^mesh[_-]/i, '');
    const out = new Set<string>();
    const add = (value: string) => {
      const cleaned = value.replace(/_lo$/i, '');
      if (cleaned) { out.add(cleaned); }
      const noIndex = cleaned.replace(/_[0-9]+$/i, '');
      if (noIndex) { out.add(noIndex); }
    };
    add(withoutPrefix);
    add(leaf);
    return [...out];
  },

  characterBoneMatricesForEntry(this: ViewerPageLogic, entryName: string, sourceArchiveName: string | null | undefined): Array<number[] | undefined> | null {
    const scene = this.companionSceneForArchive(sourceArchiveName);
    if (!scene) { return null; }
    let root: GeoNode | null = null;
    for (const name of this.characterGeoNameCandidates(entryName)) {
      root = findSceneNode(scene, name);
      if (root) { break; }
    }
    if (!root) { return null; }

    const matrices: Array<number[] | undefined> = [];
    const childNamed = (node: GeoNode, name: string): GeoNode | null =>
      node.children.find(c => c.name.toLowerCase() === name.toLowerCase()) ?? null;
    const readU32 = (node: GeoNode | null): number | null => {
      const data = node?.rawData;
      if (!data || data.length < 4) { return null; }
      return new DataView(data.buffer, data.byteOffset, data.byteLength).getUint32(0, true);
    };

    const walk = (node: GeoNode, parentMatrix: number[]) => {
      const matrix = multiplyMatrices(parentMatrix, readGeoTransform(node));
      const visuals = childNamed(node, 'visuals');
      if (visuals) {
        for (const vis of visuals.children.filter(c => c.name.toLowerCase() === 'vis')) {
          const matrixId = readU32(childNamed(vis, 'matrix_id'));
          if (matrixId !== null && matrixId >= 0 && matrixId < 256) {
            matrices[matrixId] = matrix;
          }
        }
      }

      const children = childNamed(node, 'children');
      if (children) {
        for (const child of children.children) { walk(child, matrix); }
      }
    };
    walk(root, identityMatrix());
    return matrices.some(Boolean) ? matrices : null;
  },

  shouldAutoComposeCharacterNode(this: ViewerPageLogic, entry: { size: number; children: number[] }, archiveName: string | null | undefined): boolean {
    if (!this.isCharacterModelArchive(archiveName)) { return false; }
    if (entry.children.length === 0) { return false; }
    // CLIB/FLIB roots often carry skeleton/control bytes that look vaguely
    // renderable to the fallback scanner. Prefer the subtree mesh children
    // for roots and grouped body parts, but leave leaf payloads inspectable.
    return entry.size === 0 || entry.children.length > 1 || entry.size < 0x4000;
  },

  buildHierarchicalModel(
    this: ViewerPageLogic,
    archive: InspectedZedArchive,
    rootEntry: InspectedZedArchive['entries'][number],
    sourceArchiveName: string | null,
  ): { model: ZedModel; payloadsWithChunks: number; fallbackPayloads: number } | null {
    const composedVertices: ZedModel['vertices'] = [];
    const composedIndices: number[] = [];
    const composedChunks: ZedModel['chunks'] = [];
    const composedRefs = new Set<string>();
    const isWeaponSource = this.isWeaponModelArchive(sourceArchiveName);
    const isEffectSource = this.isEffectModelArchive(sourceArchiveName);
    const isCharacterSource = this.isCharacterModelArchive(sourceArchiveName);
    const rootCharacterBones = isCharacterSource
      ? this.characterBoneMatricesForEntry(rootEntry.name, sourceArchiveName)
      : null;
    const bindMode = isWeaponSource || isEffectSource ? 'tailScan' : this.mapMaterialBinding();
    let payloadsWithChunks = 0;
    let fallbackPayloads = 0;

    const descriptorOffsetsFor = (childIndices: number[]): ChainOffset[] =>
      chainOffsetsForChildren(archive, childIndices);

    const hasOnlyDescriptorChildren = (entry: InspectedZedArchive['entries'][number]): boolean =>
      entry.children.length > 0 &&
      entry.children.every(childIdx => (archive.entryData.get(childIdx)?.length ?? 0) === 4);

    const canUseFallbackForEntry = (entry: InspectedZedArchive['entries'][number]): boolean => {
      if (!isCharacterSource) { return true; }
      if (entry.children.length === 0 || hasOnlyDescriptorChildren(entry)) { return true; }
      const leaf = (entry.name.split(/[\\/]/).pop() ?? entry.name).toLowerCase();
      return /^mesh(?:[_-]|$)/i.test(leaf) || /[_-]mesh(?:[_-]|$)/i.test(leaf);
    };

    type DecodedPayloadModel = {
      model: ZedModel;
      bakedByCharacterBones: boolean;
    };

    const decodePayloadModel = (
      bytes: Uint8Array,
      entryForBytes: InspectedZedArchive['entries'][number],
      chainOffsets: ChainOffset[],
    ): DecodedPayloadModel | null => {
      let mdl: ZedModel | null = null;
      let bakedByCharacterBones = false;
      const descriptorOffsets = chainOffsets.map(c => c.offset);
      const parseOptions = this.parseOptionsFor(chainOffsets);

      try {
        mdl = isEffectSource
          ? (decodeFallbackPayload(bytes) ?? parseModel(bytes, parseOptions))
          : parseModel(bytes, parseOptions);
      } catch { mdl = null; }

      if ((!mdl || mdl.chunks.length === 0) && canUseFallbackForEntry(entryForBytes)) {
        const characterBones = isCharacterSource
          ? rootCharacterBones ?? this.characterBoneMatricesForEntry(entryForBytes.name, sourceArchiveName)
          : null;
        const fallback = isCharacterSource
          ? (decodeCharacterFallbackPayload(bytes, characterBones ? { boneMatrices: characterBones } : {}) ?? decodeFallbackPayload(bytes))
          : decodeFallbackPayload(bytes);
        if (fallback && fallback.chunks.length > 0) {
          mdl = fallback;
          bakedByCharacterBones = !!characterBones;
          fallbackPayloads++;
        }
      }

      if (!mdl || mdl.chunks.length === 0) { return null; }
      const chainBound = mdl.chunks.some(c => c.textureBinding === 'chain');
      if (isWeaponSource && !chainBound) {
        try { applyWeaponDescriptorBinding(mdl, bytes, descriptorOffsets); }
        catch { /* keep tail-scan names */ }
      }
      if (!isWeaponSource && !isEffectSource && !chainBound && bindMode !== 'tailScan' && bindMode !== 'chain') {
        try { applyMarkerBinding(mdl, bytes, descriptorOffsets, bindMode); }
        catch { /* keep tail-scan names */ }
      }
      return {
        model: mdl,
        bakedByCharacterBones,
      };
    };

    const mergePayload = (
      local: ZedModel,
      matrix: number[],
      sourceModelName: string,
    ): void => {
      payloadsWithChunks++;
      const vertexBase = composedVertices.length;
      const chunkBase = composedChunks.length;
      for (const v of local.vertices) {
        const [tx, ty, tz] = transformPoint(matrix, v.x, v.y, v.z);
        composedVertices.push({ ...v, x: tx, y: ty, z: tz });
      }
      for (const idx of local.indices) { composedIndices.push(vertexBase + idx); }
      for (const c of local.chunks) {
        composedChunks.push({
          ...c,
          // The renderer draws chunk.vertices: they must carry the transform.
          vertices: transformVertices(c.vertices, matrix),
          sourceModelName: c.sourceModelName ?? sourceModelName,
          sourceArchiveName: c.sourceArchiveName ?? sourceArchiveName ?? undefined,
          offset: c.offset + chunkBase * 0x80,
        });
      }
      for (const ref of local.textureRefs) { composedRefs.add(ref); }
    };

    const visit = (entryIndex: number, parentMatrix: number[]) => {
      const ent = archive.entries.find(e => e.index === entryIndex);
      if (!ent) { return; }
      const bytes = archive.entryData.get(ent.index);
      const localMatrix = bytes ? tryReadLocalMatrix(bytes) : identityMatrix();
      const matrix = multiplyMatrices(parentMatrix, localMatrix);

      if (bytes && bytes.length > 0) {
        const decoded = decodePayloadModel(bytes, ent, descriptorOffsetsFor(ent.children));
        if (decoded) {
          mergePayload(
            decoded.model,
            decoded.bakedByCharacterBones ? identityMatrix() : matrix,
            ent.name,
          );
        }
      }

      for (const childIdx of ent.children) { visit(childIdx, matrix); }
    };

    visit(rootEntry.index, identityMatrix());
    if (composedVertices.length === 0) { return null; }

    let radius = 0;
    for (const v of composedVertices) {
      const d = Math.hypot(v.x, v.y, v.z);
      if (d > radius) { radius = d; }
    }
    let composed: ZedModel = {
      chunks: composedChunks,
      vertices: composedVertices,
      indices: composedIndices,
      textureRefs: [...composedRefs],
      boundingRadius: radius || 1,
    };
    const unitChoice = fallbackPayloads > 0
      ? { unit: 'scale' as PositionUnit, reason: 'fallback decoder' }
      : this.choosePositionUnit(composed, rootEntry.name, sourceArchiveName);
    composed = convertPositionUnit(composed, unitChoice.unit);
    this.recordDecodeInfo(rootEntry.name, composed, unitChoice);
    return {
      model: composed,
      payloadsWithChunks,
      fallbackPayloads,
    };
  },

  // ------------------------------------------------------------------
  // Hierarchical model decode. Walks the currently-selected node's
  // subtree in the active archive, decoding chunks from EVERY child
  // node that contains them and applying any per-node transform matrix
  // pulled from the node's data. Produces one composed mesh — required
  // for CLIB / player rendering, where the actual geometry lives in
  // nested MESH_* children rather than the root payload.
  // (HTML map viewer's decodeHierarchicalModelNode parity.)
  // ------------------------------------------------------------------
  composeHierarchicalModel(this: ViewerPageLogic): void {
    const entry = this.selectedEntry();
    const stack = this.archiveStack();
    if (!entry || stack.length === 0) {
      this.log('[hierarchical] requires drilling into an archive first', true);
      return;
    }
    const archive = stack[stack.length - 1].archive;
    const sourceArchiveName = this.currentArchiveLeaf();
    const rootEntry = archive.entries.find(e => e.index === entry.index);
    if (!rootEntry) {
      this.log(`[hierarchical] entry ${entry.name} not found in current archive`, true);
      return;
    }

    const composed = this.buildHierarchicalModel(archive, rootEntry, sourceArchiveName);
    if (composed) {
      this.model.set(composed.model);
      this.previewMode.set('model');
      this.currentModelBytes = null;
      this.currentSliceOffsets = [];
      this.currentModelSourceArchive = sourceArchiveName;
      setTimeout(() => this.applyModelToRenderer(), 0);
      this.log(`[hierarchical] ${entry.name}: ${composed.payloadsWithChunks} payload(s) with chunks, ${composed.model.vertices.length} verts, ${composed.model.indices.length / 3 | 0} tris, fallback=${composed.fallbackPayloads}`, true);
      return;
    }
    if (this.isCharacterModelArchive(sourceArchiveName)) {
      this.log(`[hierarchical] ${entry.name}: no chunks found in subtree`, true);
      return;
    }

    const composedVertices: ZedModel['vertices'] = [];
    const composedIndices: number[] = [];
    const composedChunks: ZedModel['chunks'] = [];
    const composedRefs = new Set<string>();
    let payloadsWithChunks = 0;

    const descriptorOffsetsFor = (childIndices: number[]): ChainOffset[] =>
      chainOffsetsForChildren(archive, childIndices);

    const visit = (entryIndex: number, parentMatrix: number[]) => {
      const ent = archive.entries.find(e => e.index === entryIndex);
      if (!ent) { return; }
      const bytes = archive.entryData.get(ent.index);
      const localMatrix = bytes ? tryReadLocalMatrix(bytes) : identityMatrix();
      const matrix = multiplyMatrices(parentMatrix, localMatrix);

      if (bytes && bytes.length > 0) {
        let local: ZedModel | null = null;
        const chainOffsets = descriptorOffsetsFor(ent.children);
        const parseOptions = this.parseOptionsFor(chainOffsets);
        try {
          local = this.isCurrentEffectModelLayer()
            ? (decodeFallbackPayload(bytes) ?? parseModel(bytes, parseOptions))
            : parseModel(bytes, parseOptions);
        } catch { local = null; }
        // CLIB / similar payloads don't use the standard chunk format —
        // fall back to VIF / vertex-cloud decoding so the hierarchical
        // walk actually picks up character meshes etc.
        if (!local || local.chunks.length === 0) {
          local = decodeFallbackPayload(bytes);
        }
        if (local && local.chunks.length > 0) {
          // Apply the user's material-binding mode to THIS payload's
          // chunks while we still have the right bytes in hand. Composed
          // chunk offsets are mangled across payloads so we can't run
          // binding after merging — this is the only place it works.
          const sliceOffsets = chainOffsets.map(c => c.offset);
          const isWeaponSource = this.isCurrentWeaponModelLayer();
          const isEffectSource = this.isCurrentEffectModelLayer();
          const bindMode = isWeaponSource || isEffectSource ? 'tailScan' : this.mapMaterialBinding();
          const chainBound = local.chunks.some(c => c.textureBinding === 'chain');
          if (isWeaponSource && !chainBound) {
            try { applyWeaponDescriptorBinding(local, bytes, sliceOffsets); }
            catch { /* keep tail-scan names */ }
          }
          if (!isWeaponSource && !isEffectSource && !chainBound && bindMode !== 'tailScan' && bindMode !== 'chain') {
            try { applyMarkerBinding(local, bytes, sliceOffsets, bindMode); }
            catch { /* keep tail-scan names */ }
          }
          payloadsWithChunks++;
          const vertexBase = composedVertices.length;
          const chunkBase = composedChunks.length;
          for (const v of local.vertices) {
            const [tx, ty, tz] = transformPoint(matrix, v.x, v.y, v.z);
            composedVertices.push({ ...v, x: tx, y: ty, z: tz });
          }
          for (const idx of local.indices) { composedIndices.push(vertexBase + idx); }
          for (const c of local.chunks) {
            composedChunks.push({
              ...c,
              vertices: transformVertices(c.vertices, matrix),
              sourceModelName: c.sourceModelName ?? entry.name,
              sourceArchiveName: c.sourceArchiveName ?? sourceArchiveName ?? undefined,
              offset: c.offset + chunkBase * 0x80,
            });
          }
          for (const ref of local.textureRefs) { composedRefs.add(ref); }
        }
      }

      for (const childIdx of ent.children) { visit(childIdx, matrix); }
    };
    visit(rootEntry.index, identityMatrix());

    if (composedVertices.length === 0) {
      this.log(`[hierarchical] ${entry.name}: no chunks found in subtree`, true);
      return;
    }

    let radius = 0;
    for (const v of composedVertices) {
      const d = Math.hypot(v.x, v.y, v.z);
      if (d > radius) { radius = d; }
    }
    let composedModel: ZedModel = {
      chunks: composedChunks,
      vertices: composedVertices,
      indices: composedIndices,
      textureRefs: [...composedRefs],
      boundingRadius: radius || 1,
    };
    const unitChoice = this.choosePositionUnit(composedModel, rootEntry.name, sourceArchiveName);
    composedModel = convertPositionUnit(composedModel, unitChoice.unit);
    this.recordDecodeInfo(rootEntry.name, composedModel, unitChoice);
    this.selectedVisual.set('');
    this.model.set(composedModel);
    this.previewMode.set('model');
    this.currentModelBytes = null;
    this.currentSliceOffsets = [];
    this.currentModelSourceArchive = sourceArchiveName;
    setTimeout(() => this.applyModelToRenderer(), 0);
    this.log(`[hierarchical] ${entry.name}: ${payloadsWithChunks} payload(s) with chunks, ${composedVertices.length} verts, ${composedIndices.length / 3 | 0} tris`, true);
  },
};

export type ModelComposeMethods = typeof modelComposeMethods;

// Matrix helpers used by composeHierarchicalModel + GEO scene composition.
// Direct ports of the HTML map viewer's tryReadLocalMatrix / multiplyMatrices
// / identityMatrix — same column-major convention so the two composition
// paths interoperate.
export function identityMatrix(): number[] {
  return [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];
}

export function multiplyMatrices(a: number[], b: number[]): number[] {
  const out = new Array<number>(16).fill(0);
  for (let row = 0; row < 4; row++) {
    for (let col = 0; col < 4; col++) {
      out[col * 4 + row] =
        a[0 * 4 + row] * b[col * 4 + 0] +
        a[1 * 4 + row] * b[col * 4 + 1] +
        a[2 * 4 + row] * b[col * 4 + 2] +
        a[3 * 4 + row] * b[col * 4 + 3];
    }
  }
  return out;
}

// The N<node>_<visual> children of a model node: 4-byte payloads holding the
// offset of that visual's DMA chain inside the parent payload. Keeps the
// child name so chunks can be labelled with the visual they belong to.
export function chainOffsetsForChildren(archive: InspectedZedArchive, childIndices: number[]): ChainOffset[] {
  const out: ChainOffset[] = [];
  for (const childIdx of childIndices) {
    const childBytes = archive.entryData.get(childIdx);
    if (!childBytes || childBytes.length !== 4) { continue; }
    const view = new DataView(childBytes.buffer, childBytes.byteOffset, 4);
    const child = archive.entries.find(e => e.index === childIdx);
    const leaf = child ? (child.name.split(/[\\/]/).pop() ?? child.name) : null;
    out.push({ offset: view.getUint32(0, true), key: leaf });
  }
  return out;
}

// Reads the first 16 float32s of a node payload as a 4x4 matrix and rejects
// it if any sanity check fails (scale, translation magnitude, bottom row
// near identity). When the data doesn't look like a transform we fall back
// to identity so the chunks aren't accidentally warped into oblivion.
export function tryReadLocalMatrix(bytes: Uint8Array): number[] {
  if (!bytes || bytes.length < 0x40) { return identityMatrix(); }
  if (bytes.length > 0x90) { return identityMatrix(); }
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  const m: number[] = [];
  for (let i = 0; i < 16; i++) { m.push(view.getFloat32(i * 4, true)); }
  if (!m.every(Number.isFinite)) { return identityMatrix(); }
  const sx = Math.hypot(m[0], m[1], m[2]);
  const sy = Math.hypot(m[4], m[5], m[6]);
  const sz = Math.hypot(m[8], m[9], m[10]);
  const saneScale = sx > 0.001 && sy > 0.001 && sz > 0.001 && sx < 500 && sy < 500 && sz < 500;
  const saneBottom = Math.abs(m[3]) < 0.01 && Math.abs(m[7]) < 0.01 && Math.abs(m[11]) < 0.01 && Math.abs(Math.abs(m[15]) - 1) < 0.25;
  const saneTranslate = Math.abs(m[12]) < 100000 && Math.abs(m[13]) < 100000 && Math.abs(m[14]) < 100000;
  return saneScale && saneBottom && saneTranslate ? m : identityMatrix();
}

export function isIdentityMatrix(m: number[]): boolean {
  const id = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];
  return m.length === 16 && m.every((v, i) => Math.abs(v - id[i]) < 1e-9);
}

// Copies of chunk vertices moved by a placement matrix (column-major, as
// transformPoint). Identity returns the same array.
export function transformVertices(vertices: ZedModel['vertices'], matrix: number[]): ZedModel['vertices'] {
  if (isIdentityMatrix(matrix)) { return vertices; }
  return vertices.map(v => {
    const [x, y, z] = transformPoint(matrix, v.x, v.y, v.z);
    return { ...v, x, y, z };
  });
}
