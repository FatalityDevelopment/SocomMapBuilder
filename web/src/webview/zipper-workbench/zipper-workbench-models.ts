// Asset Viewer models (_MDL archives): decoding a payload — through the server for SOCOM II models it lists, else the
// in-page decoders — with its material binding, position unit and alternate payloads.

import { host } from '../../shared';
import { parseTexture } from './zed-texture-parser';
import { applyMarkerBinding, applyWeaponDescriptorBinding, ChainOffset, convertPositionUnit, decodeCharacterFallbackPayload, decodeFallbackPayload, looksLikeZedContainer, parseModel, PositionUnit, ZedModel } from './zed-model-parser';
import { InspectedZedArchive, parseZedArchive } from './zed-archive-parser';
import type { ModelTextureMap } from './zed-model-renderer';
import { decodeMesh, type MeshWire } from '../model-editor/mesh-data';
import { TextureCache } from '../model-editor/model-loader';
import { findSceneNode, GeoNode } from './zed-geo-parser';
import { choosePositionUnit, type PositionUnitChoice } from '../zipper-core/model-placement';
import { chainOffsetsForChildren } from './zipper-workbench-model-compose';
import type { ViewerPageLogic } from './zipper-workbench-page';
import { namespaceFromArchiveName, textureToDataUrl } from './zipper-workbench-textures';
import type { BrowserItem } from './zipper-workbench-types';
import { bytesToBase64, describeMagic } from './zipper-workbench-util';

export const modelMethods = {
  // An item's model; with none, the selected model of a model archive, else just the package.
  openInModelEditor(this: ViewerPageLogic, item?: BrowserItem): void {
    if (!item) {
      const selected = this.selectedEntry();
      item = selected && this.isCurrentFileModelArchive() ? this.browserItems().find(i => i.entry.index === selected.index) : undefined;
      if (!item) { host.open('models', this.rootPath()); return; }
    }
    const layer = this.currentFileName().split(/[\\/]/).pop() ?? '';
    const archive = item.kind === 'definition' ? layer.replace(/_GEO(\([^)]+\))?\.ZED$/i, '_MDL.ZED') : layer;
    host.open('models', this.rootPath(), { archive, name: item.name });
  },

  isModelArchive(this: ViewerPageLogic, item: BrowserItem): boolean {
    return item.kind === 'archive' && /_MDL(\([^)]+\))?\.ZED$/i.test(item.name);
  },

  decodePayload(this: ViewerPageLogic, index: number, bytes: Uint8Array, skipServer = false): void {
    const entry = this.selectedEntry();
    if (!entry || entry.index !== index) { return; }

    // RDR typed-table data: ask the bridge to parse and display structured tree.
    if (/\.rdr$/i.test(entry.name)) {
      const leaf = entry.name.split(/[\\/]/).pop() ?? entry.name;
      if (this.tryApplyAnimationPreview(bytes, leaf)) { return; }

      this.previewMode.set('rdr');
      this.previewMessage.set('Parsing RDR table...');
      this.rdr.set(null);
      const filePath = this.result()?.path;
      // We already have the .rdr bytes here (passed in via `bytes` arg). Skip
      // the backend's container drill-down and parse the bytes directly —
      // this works for any nesting depth, including .rdr-inside-.zar-inside
      // .zdb where the legacy path was failing.
      const base64 = bytesToBase64(bytes);
      this.log(`[RDR] inspecting ${leaf} (${bytes.length}B, in-memory)`);
      this.vscode.postMessage({ type: 'inspectRdrBytes', base64, entryName: leaf });
      return;
      // Note: legacy `inspectRdr` (path + entryName) is still available on
      // the provider for path-only entrypoints, but `decodePayload` is
      // always called with bytes in hand so we prefer the bytes variant.
      void filePath; // unused, retained for future telemetry context
    }

    // AIMAPS.MPS — parsed server-side via the new MPS handler. The .mps file
    // is always at the package root (not nested), so we pass the full path
    // through unchanged. Mirrors the HTML map viewer's parseAiMaps flow.
    if (/\.mps$/i.test(entry.name)) {
      this.previewMode.set('mps');
      this.previewMessage.set('Parsing AI maps...');
      this.mps.set(null);
      const filePath = this.result()?.path;
      const leaf = entry.name.split(/[\\/]/).pop() ?? entry.name;
      const inMemory = this.parseMpsBytes(bytes, `${filePath ?? 'package'} / ${leaf}`);
      if (inMemory) {
        this.mps.set(inMemory);
        this.previewMessage.set(`MPS: ${inMemory.foundCount} / ${inMemory.declaredCount} AI maps found.`);
        this.log(`[MPS] ${leaf}: declared=${inMemory.declaredCount} found=${inMemory.foundCount} (${inMemory.fileSize}B, in-memory)`);
        return;
      }
      if (filePath) {
        this.log(`[MPS] inspecting ${leaf}…`);
        // .mps only lives as a sibling on disk; if the loaded archive itself
        // is the .mps, use its path. Otherwise the path is the parent ZDB
        // and we can't currently extract a single .mps from it — fall back
        // to displaying an error in the table.
        if (/\.mps$/i.test(filePath)) {
          this.vscode.postMessage({ type: 'inspectMps', path: filePath });
        } else {
          this.previewMessage.set(`MPS preview requires the .mps file as the loaded path (got ${filePath.split(/[\\/]/).pop()}).`);
          this.log(`[MPS] skipped — outer archive is not .mps; open ${leaf} directly`);
        }
      }
      return;
    }

    // A clutter group: the model it places (the server finds it by name in the package's MDL archives).
    const clutterModel = this.clutterGroupName(entry);
    if (clutterModel && !skipServer) {
      this.previewMode.set('model');
      this.previewMessage.set('Decoding…');
      void this.decodeWithServer(index, bytes, null, clutterModel);
      return;
    }
    // A record that only holds records: a folder, opened with a double-click.
    if (bytes.length === 0 && (entry.type ?? 0) > 0) {
      this.previewMode.set('none');
      this.model.set(null);
      this.previewMessage.set(`${entry.name}: ${entry.type} item(s). Double-click to open.`);
      return;
    }

    // A palette record: its swatches (selectedPalette), nothing to decode.
    if (/^texpal_\d+$/i.test(entry.name)) {
      this.previewMode.set('palette');
      this.previewMessage.set(this.selectedPalette() ? '' : 'Palette not loaded');
      return;
    }

    const looksTif = /\.tif$/i.test(entry.name);

    if (looksTif) {
      // Keep texture mode active even on failure so the user sees an error
      // instead of a collapsed pane.
      this.previewMode.set('texture');
      try {
        const tex = parseTexture(bytes, this.palettes(), entry.name.toLowerCase());
        this.texture.set(tex);
        this.textureDataUrl.set(textureToDataUrl(tex));
        this.previewMessage.set(
          tex.header.isIndexed && !tex.paletteResolved
            ? `Palette texpal_${tex.header.cbp ?? tex.header.paletteId} not loaded`
            : ''
        );
        const gs = tex.header.gs;
        this.log(
          `[texture] ${entry.name}: ${tex.header.width}x${tex.header.height} psm=0x${tex.header.psm.toString(16)} bpp=${tex.header.bpp}` +
          (gs ? ` cbp=${gs.cbp} cpsm=${gs.cpsm} wrap=${gs.wrapS}/${gs.wrapT} alphaTest=${gs.alphaTest === null ? 'off' : gs.alphaTest.toFixed(2)} blend=${gs.alphaBlend}` : ' (no GS tail)') +
          (tex.header.isIndexed ? ` palette=${tex.paletteResolved ? 'ok' : 'missing'}` : '') +
          (tex.hasTransparency ? ' transparent' : ''),
        );
      } catch (err) {
        this.texture.set(null);
        this.textureDataUrl.set('');
        this.previewMessage.set('Texture decode failed: ' + (err instanceof Error ? err.message : String(err)));
      }
      return;
    }

    // If this entry is itself a nested ZED archive (e.g. a row inside a ZDB
    // or ZAR container), drill into it inline. Pushing onto the archive stack
    // swaps the entries table to show the sub-archive's children and routes
    // node reads through the cached bytes.
    if (looksLikeZedContainer(bytes)) {
      // Don't drill automatically - the user might want to inspect siblings
      // in the parent archive first. Park the entry and show an Enter button.
      this.previewMode.set('none');
      this.model.set(null);
      this.pendingArchiveEntry.set({
        entryIndex: index,
        entryName: entry.name,
        size: bytes.length,
      });
      // Cache so Enter Archive doesn't need a re-fetch.
      this.nodeCache.set(index, bytes);
      if (this.enterWhenReady === index) {
        this.enterWhenReady = null;
        setTimeout(() => this.enterPendingArchive(), 0);
      }
      this.previewMessage.set('');
      return;
    }
    this.pendingArchiveEntry.set(null);

    // Motion archives store raw animation payloads under bare clip names
    // (civ_jog, guard_checkwatch, etc.) rather than ".rdr" filenames.
    if (this.tryApplyAnimationPreview(bytes, entry.name)) { return; }

    // Anything else: try to parse as a model. Keep the pane open even when no
    // chunks are found, so the user sees diagnostics rather than nothing.
    this.previewMode.set('model');
    const mdlArchive = this.currentArchiveLeaf();
    if (!skipServer && mdlArchive && /_mdl\.zed$/i.test(mdlArchive) && !this.isMapWorldModelEntry(entry.name, mdlArchive)) {
      this.previewMessage.set('Decoding…');
      void this.decodeWithServer(index, bytes, mdlArchive, entry.name);
      return;
    }
    try {
      const alternate = this.resolveAlternateModelPayload(entry.name);
      const modelBytes = alternate?.bytes ?? bytes;
      const bindingMode: 'weaponDescriptor' | 'map' | 'tail' = alternate
        ? 'tail'
        : this.isCurrentWeaponModelLayer()
          ? 'weaponDescriptor'
          : this.isCurrentEffectModelLayer()
            ? 'tail'
            : 'map';
      const sliceOffsets = alternate?.sliceOffsets ?? this.collectSliceOffsetsForCurrentEntry({
        siblingFallback: bindingMode === 'map',
      });
      const sourceArchive = alternate?.sourceArchiveName ?? this.currentArchiveLeaf();
      if (alternate) {
        this.log(`[model] ${entry.name}: using ${alternate.sourceName}`);
      }

      // The map's world model previews as the whole map: terrain and
      // buildings plus every prop the map's GEO scene places, once the
      // package (GEO, MDL, TXR/PAL, AIMAPS) has loaded.
      // Any failure falls back to drawing worldmodel on its own.
      if (!alternate && this.isMapWorldModelEntry(entry.name, sourceArchive)) {
        let composed = false;
        try {
          composed = this.composeGeoScene({ fromSceneRoot: true });
        } catch (err) {
          this.log(`[GEO] map composition failed, showing worldmodel alone: ${err instanceof Error ? err.stack ?? err.message : String(err)}`, true);
        }
        if (composed) {
          this.previewMessage.set('');
          return;
        }
        this.previewMode.set('model');
      }

      const activeArchive = this.archiveStack().at(-1)?.archive ?? null;
      const activeNode = activeArchive?.entries.find(e => e.index === entry.index) ?? null;
      if (!alternate && activeArchive && activeNode && this.shouldAutoComposeCharacterNode(activeNode, sourceArchive)) {
        const composed = this.buildHierarchicalModel(activeArchive, activeNode, sourceArchive);
        if (composed) {
          this.currentModelBytes = null;
          this.currentSliceOffsets = [];
          this.currentModelBindingMode = 'tail';
          this.currentModelSourceArchive = sourceArchive;
          this.selectedChunk.set(-1);
          this.model.set(composed.model);
          this.previewMessage.set(
            `Composed ${composed.payloadsWithChunks} character payload(s) from hierarchy.`,
          );
          this.log(`[character] ${entry.name}: auto-composed ${composed.payloadsWithChunks} payload(s), ${composed.model.vertices.length} verts, ${composed.model.indices.length / 3 | 0} tris`);
          setTimeout(() => this.applyModelToRenderer(), 0);
          return;
        }
        this.currentModelBytes = null;
        this.currentSliceOffsets = [];
        this.currentModelBindingMode = 'tail';
        this.currentModelSourceArchive = sourceArchive;
        this.model.set(null);
        this.previewMessage.set(`No character mesh chunks found in ${entry.name}'s hierarchy.`);
        this.log(`[character] ${entry.name}: hierarchy produced no renderable chunks`);
        return;
      }

      const parseOptions = this.parseOptionsFor(sliceOffsets);
      let mdl = this.isCurrentEffectModelLayer()
        ? (decodeFallbackPayload(modelBytes) ?? parseModel(modelBytes, parseOptions))
        : parseModel(modelBytes, parseOptions);
      let fallbackUsed = false;
      // If the standard chunk format yielded nothing (common for CLIB
      // MESH_* payloads which start with their name as ASCII rather than
      // the WORL_MDL chunk magic), try the VIF / vertex-cloud fallback
      // decoders. They give the user *something* to inspect instead of an
      // empty pane.
      if (mdl.chunks.length === 0) {
        const characterBones = this.isCharacterModelArchive(sourceArchive)
          ? this.characterBoneMatricesForEntry(entry.name, sourceArchive)
          : null;
        const fallback = this.isCharacterModelArchive(sourceArchive)
          ? (decodeCharacterFallbackPayload(modelBytes, characterBones ? { boneMatrices: characterBones } : {}) ?? decodeFallbackPayload(modelBytes))
          : decodeFallbackPayload(modelBytes);
        if (fallback && fallback.chunks.length > 0) {
          mdl = fallback;
          fallbackUsed = true;
        }
      }
      this.currentModelBytes = modelBytes;
      this.currentSliceOffsets = sliceOffsets;
      this.currentModelBindingMode = bindingMode;
      this.currentModelSourceArchive = sourceArchive;
      this.applyCurrentBinding(mdl);
      const placed = fallbackUsed ? null : this.placeModelByGeo(mdl, this.findGeoNodeForModel(entry.name, sourceArchive));
      let unitChoice: PositionUnitChoice;
      if (placed) {
        mdl = placed.model;
        unitChoice = placed.choice;
      } else {
        unitChoice = fallbackUsed
          ? { unit: 'scale' as PositionUnit, reason: 'fallback decoder' }
          : this.choosePositionUnit(mdl, entry.name, sourceArchive);
        mdl = convertPositionUnit(mdl, unitChoice.unit);
      }
      this.currentPositionUnit = unitChoice.unit;
      this.selectedVisual.set('');
      if (mdl.chunks.length > 0) { this.recordDecodeInfo(entry.name, mdl, unitChoice); }
      this.model.set(mdl);
      if (mdl.chunks.length === 0) {
        this.previewMessage.set(
          `No model chunks found (payload ${modelBytes.length} bytes, first 4 = ${describeMagic(modelBytes)})`
        );
      } else if (fallbackUsed) {
        this.previewMessage.set(
          `Decoded ${mdl.vertices.length} verts via VIF / vertex-cloud fallback (no standard chunks).`,
        );
        this.log(`[fallback] ${entry.name}: ${mdl.chunks.length} stream(s), ${mdl.vertices.length} verts`);
      } else {
        this.previewMessage.set('');
      }
      setTimeout(() => this.applyModelToRenderer(), 0);
    } catch (err) {
      this.model.set(null);
      this.currentModelBytes = null;
      this.currentSliceOffsets = [];
      this.currentModelBindingMode = 'tail';
      this.currentModelSourceArchive = null;
      this.previewMessage.set('Model decode failed: ' + (err instanceof Error ? err.message : String(err)));
    }
  },

  // The models the server lists for the open package, keyed by archive|name (lowercased).
  serverModelIndex(this: ViewerPageLogic, path: string): Promise<Map<string, { archive: string; name: string }>> {
    if (this.serverPath !== path || !this.serverModels) {
      this.serverPath = path;
      this.serverTextures = new TextureCache(this.server);
      this.serverModels = this.server.open(path)
        .then(() => this.server.listModels())
        .then(models => new Map(models.map(m => [`${m.archive.toLowerCase()}|${m.name.toLowerCase()}`, { archive: m.archive, name: m.name }])))
        .catch(err => {
          this.log(`[model] server decoder unavailable for ${path}: ${err instanceof Error ? err.message : String(err)}`);
          return new Map();
        });
    }
    return this.serverModels;
  },

  // Decodes the selected model on the server (mesh + textures); anything it does not list, or a failure, goes back
  // through the in-page decoders.
  async decodeWithServer(this: ViewerPageLogic, index: number, bytes: Uint8Array, archiveHint: string | null, name: string): Promise<void> {
    const stillSelected = () => this.selectedEntry()?.index === index;
    const fallBack = () => { if (stillSelected()) { this.decodePayload(index, bytes, true); } };
    const path = this.result()?.path;
    if (!path) { fallBack(); return; }
    try {
      const models = await this.serverModelIndex(path);
      // No archive: the model by name (a clutter group), the map's own archive first.
      const known = archiveHint
        ? models.get(`${archiveHint.toLowerCase()}|${name.toLowerCase()}`)
        : [...models.values()].filter(m => m.name.toLowerCase() === name.toLowerCase()).sort((a, b) => (/^(clib|flib|weap|effe)_/i.test(a.archive) ? 1 : 0) - (/^(clib|flib|weap|effe)_/i.test(b.archive) ? 1 : 0))[0];
      if (!known) {
        if (archiveHint) { fallBack(); } else if (stillSelected()) { this.model.set(null); this.previewMessage.set(`No model named ${name} in this package`); }
        return;
      }
      const archive = known.archive;
      const wire = await this.server.mesh(known.archive, known.name) as MeshWire;
      const mesh = decodeMesh(wire);
      if (mesh.chunks.length === 0) { fallBack(); return; }
      const chunks: ZedModel['chunks'] = mesh.chunks.map(c => {
        const vertices: ZedModel['vertices'] = [];
        for (let i = 0; i < c.vertexCount; i++) {
          const v: ZedModel['vertices'][number] = {
            x: c.positions[i * 3], y: c.positions[i * 3 + 1], z: c.positions[i * 3 + 2],
            // Server UVs are top-origin and the renderer's textures are not flipped (as in the Model Editor viewport).
            u: c.uvs[i * 2], v: 1 - c.uvs[i * 2 + 1],
          };
          if (c.colors) { v.r = c.colors[i * 4]; v.g = c.colors[i * 4 + 1]; v.b = c.colors[i * 4 + 2]; v.a = c.colors[i * 4 + 3]; }
          vertices.push(v);
        }
        return {
          offset: c.geometryOffset,
          vertexCount: c.vertexCount,
          triangleCount: c.triangles.length / 3,
          decodedTriangleCount: c.triangles.length / 3,
          scale: 1,
          vertices,
          indices: Array.from(c.triangles),
          textureName: c.texture?.toLowerCase() ?? null,
          textureBinding: 'chain' as const,
          visualKey: c.visual ?? undefined,
          hasVertexColors: !!c.colors,
          materialBlend: c.materialBlend ?? undefined,
        };
      });
      const textureNames = [...new Set(chunks.map(c => c.textureName).filter((t): t is string => !!t))];
      const textures: ModelTextureMap = {};
      await Promise.all(textureNames.map(async t => {
        const pixels = await this.serverTextures.get(t, wire.textureNamespace ?? null);
        if (pixels) { textures[t] = pixels; }
      }));
      if (!stillSelected()) { return; }
      let radius = 0;
      const indices: number[] = [];
      let base = 0;
      for (const c of chunks) {
        for (const v of c.vertices) { radius = Math.max(radius, Math.hypot(v.x, v.y, v.z)); }
        for (const i of c.indices) { indices.push(base + i); }
        base += c.vertices.length;
      }
      const model: ZedModel = { chunks, vertices: chunks.flatMap(c => c.vertices), indices, textureRefs: textureNames, boundingRadius: radius };
      this.currentModelBytes = null;
      this.currentSliceOffsets = [];
      this.currentModelBindingMode = 'tail';
      this.currentModelSourceArchive = archive;
      this.currentPositionUnit = 'scale';
      this.selectedChunk.set(-1);
      this.selectedVisual.set('');
      this.serverModel = { model, textures };
      this.recordDecodeInfo(name, model, { unit: 'scale', reason: 'server decoder' });
      this.model.set(model);
      const missing = textureNames.filter(t => !textures[t]);
      this.previewMessage.set(missing.length ? `Texture(s) not found: ${missing.join(', ')}` : '');
      setTimeout(() => this.applyModelToRenderer(), 0);
    } catch (err) {
      this.log(`[model] ${name}: server decode failed, using the in-page decoder: ${err instanceof Error ? err.message : String(err)}`);
      fallBack();
    }
  },

  resolveAlternateModelPayload(this: ViewerPageLogic, entryName: string): { bytes: Uint8Array; sliceOffsets: ChainOffset[]; sourceName: string; sourceArchiveName: string } | null {
    if (entryName.toLowerCase() !== 'footbomb') { return null; }
    const flibBytes = this.packageEntryBytes.get('flib_mdl.zed');
    if (!flibBytes) { return null; }
    try {
      const archive = parseZedArchive(flibBytes);
      const satchel = archive.entries.find(e => e.name.toLowerCase() === 'satchel');
      if (!satchel) { return null; }
      const bytes = archive.entryData.get(satchel.index);
      if (!bytes || bytes.length === 0) { return null; }
      const sliceOffsets = chainOffsetsForChildren(archive, satchel.children);
      return { bytes, sliceOffsets, sourceName: 'flib_mdl.zed / Satchel', sourceArchiveName: 'flib_mdl.zed' };
    } catch {
      return null;
    }
  },

  currentArchiveLeaf(this: ViewerPageLogic): string | null {
    const stack = this.archiveStack();
    if (stack.length === 0) {
      const path = this.result()?.path;
      return path ? (path.split(/[\\/]/).pop() ?? path).toLowerCase() : null;
    }
    const current = stack[stack.length - 1].name;
    return (current.split(/[\\/]/).pop() ?? current).toLowerCase();
  },

  collectModelLookupArchives(this: ViewerPageLogic): Array<{ leaf: string; archive: InspectedZedArchive }> {
    const out: Array<{ leaf: string; archive: InspectedZedArchive }> = [];
    const seen = new Set<string>();
    const addArchive = (leafName: string, archive: InspectedZedArchive): void => {
      const leaf = (leafName.split(/[\\/]/).pop() ?? leafName).toLowerCase();
      if (!/_MDL(?:\([^)]+\))?\.zed$/i.test(leaf) || seen.has(leaf)) { return; }
      seen.add(leaf);
      out.push({ leaf, archive });
    };
    const addBytes = (leafName: string, bytes: Uint8Array | undefined): void => {
      if (!bytes || bytes.length === 0) { return; }
      const leaf = (leafName.split(/[\\/]/).pop() ?? leafName).toLowerCase();
      if (!/_MDL(?:\([^)]+\))?\.zed$/i.test(leaf) || seen.has(leaf)) { return; }
      try { addArchive(leaf, parseZedArchive(bytes)); } catch { /* skip non-ZED payloads */ }
    };

    for (const [leaf, bytes] of this.packageEntryBytes) { addBytes(leaf, bytes); }

    for (const layer of this.archiveStack()) {
      addArchive(layer.name, layer.archive);
      for (const entry of layer.archive.entries) {
        addBytes(entry.name, layer.archive.entryData.get(entry.index));
      }
    }

    return out;
  },

  modelSourcePreferOrder(this: ViewerPageLogic): string[] {
    const order: string[] = [];
    const add = (value: string | null | undefined): void => {
      const leaf = (value?.split(/[\\/]/).pop() ?? '').toLowerCase();
      if (leaf && !order.includes(leaf)) { order.push(leaf); }
    };
    const path = this.result()?.path ?? '';
    const stem = path ? (path.split(/[\\/]/).pop() ?? path).replace(/\.[^.]+$/i, '').toLowerCase() : '';
    if (stem) { add(`${stem}_mdl.zed`); }
    add(this.currentArchiveLeaf());
    for (const leaf of ['worl_mdl.zed', 'weap_mdl.zed', 'clib_mdl.zed', 'flib_mdl.zed', 'benz_mdl.zed']) { add(leaf); }
    return order;
  },

  // Pulls slice offsets from the Nxxx_xxx child stubs of the selected entry.
  // These nodes hold 4-byte payloads = byte offsets into the parent model
  // payload, defining where each material slice begins. Returns [] when no
  // sub-archive context is available (e.g. opened a raw model directly).
  collectSliceOffsetsForCurrentEntry(this: ViewerPageLogic, options: { siblingFallback?: boolean } = {}): ChainOffset[] {
    const entry = this.selectedEntry();
    const stack = this.archiveStack();
    if (!entry || stack.length === 0) { return []; }
    const archive = stack[stack.length - 1].archive;
    const modelEntry = archive.entries.find(e => e.index === entry.index);
    if (!modelEntry) { return []; }
    const offsets = chainOffsetsForChildren(archive, modelEntry.children);
    if (offsets.length > 0 || !options.siblingFallback) { return offsets; }

    // The standalone M51 viewer learned these material slices by scanning
    // following Nxxx entries in archive order. Some nested archives expose
    // those stubs as siblings rather than children, so mirror that fallback
    // for object/map models without changing the weapon descriptor path.
    const start = archive.entries.findIndex(e => e.index === entry.index);
    if (start < 0) { return offsets; }
    for (let i = start + 1; i < archive.entries.length; i++) {
      const sibling = archive.entries[i];
      const leaf = sibling.name.split(/[\\/]/).pop() ?? sibling.name;
      const siblingBytes = archive.entryData.get(sibling.index);
      const size = siblingBytes?.length ?? sibling.size;
      if (size === 4 && /^N\d+/i.test(leaf) && siblingBytes) {
        const view = new DataView(siblingBytes.buffer, siblingBytes.byteOffset, 4);
        offsets.push({ offset: view.getUint32(0, true), key: leaf });
        continue;
      }
      if (size > 64 || (!/^N\d+/i.test(leaf) && size !== 4)) { break; }
    }
    if (offsets.length > 0) {
      this.log(`[model] ${entry.name}: using ${offsets.length} sibling N-slice offset(s)`);
    }
    return offsets;
  },

  // Runs the smartWorld / sliceNearest / globalNearest binding pass per the
  // current dropdown selection. `tailScan` (default) is a no-op so the
  // existing chunk.textureName from findTifInRange is preserved.
  applyCurrentBinding(this: ViewerPageLogic, mdl: ZedModel): void {
    if (!this.currentModelBytes) { return; }
    // Chain-bound chunks already carry the exact texture per section.
    if (mdl.chunks.some(c => c.textureBinding === 'chain')) { return; }
    const sliceOffsets = this.currentSliceOffsets.map(c => c.offset);
    if (this.currentModelBindingMode === 'weaponDescriptor') {
      applyWeaponDescriptorBinding(mdl, this.currentModelBytes, sliceOffsets);
      return;
    }
    if (this.currentModelBindingMode === 'tail') { return; }
    const mode = this.mapMaterialBinding();
    if (mode === 'tailScan' || mode === 'chain') { return; }
    applyMarkerBinding(mdl, this.currentModelBytes, sliceOffsets, mode);
  },

  // Parse options for the current binding dropdown: chain offsets are only
  // handed to the parser in chain mode so the marker modes still show the
  // heuristic result the user asked for. Positions are always decoded in
  // the scale convention first; choosePositionUnit converts afterwards.
  parseOptionsFor(this: ViewerPageLogic, chainOffsets: ChainOffset[]): { chainOffsets?: ChainOffset[]; positionUnit: PositionUnit } {
    return this.mapMaterialBinding() === 'chain'
      ? { chainOffsets, positionUnit: 'scale' }
      : { positionUnit: 'scale' };
  },

  // Decide how a decoded model's int16 positions map to units.
  //   1. the dropdown override
  //   2. the GEO node's nparams bounds: the convention whose extent matches
  //      them (the two differ by exactly 2048x, so this is unambiguous)
  //   3. the GEO node's vparams flags (weapons normalise by the chunk
  //      scale, everything else is 1/16 units)
  //   4. the scale convention
  choosePositionUnit(
    this: ViewerPageLogic,
    model: ZedModel | null,
    entryName: string | null | undefined,
    sourceArchiveName: string | null | undefined,
    geoNode: GeoNode | null = null,
  ): PositionUnitChoice {
    let node = geoNode;
    if (!node && entryName && this.mapPositionUnit() === 'auto') {
      const scene = this.companionSceneForArchive(sourceArchiveName);
      if (scene) {
        for (const name of this.characterGeoNameCandidates(entryName)) {
          node = findSceneNode(scene, name);
          if (node) { break; }
        }
      }
    }
    const isWorld = namespaceFromArchiveName(sourceArchiveName) === 'worl' || (entryName ?? '').toLowerCase() === 'worldmodel';
    return choosePositionUnit(model, node, this.mapPositionUnit(), isWorld);
  },

  // Decode-time settings (binding, units) need the payload decoded again:
  // a single model from its bytes, the composed map from the package.
  canRedecodePreview(this: ViewerPageLogic): boolean {
    const entry = this.selectedEntry();
    if (!entry) { return false; }
    return !!this.currentModelBytes || this.isMapWorldModelEntry(entry.name, this.currentArchiveLeaf());
  },

  // The map's world model (WORL_MDL > worldmodel) when the map scene that
  // places it has loaded.
  isMapWorldModelEntry(this: ViewerPageLogic, entryName: string, sourceArchive: string | null | undefined): boolean {
    if (entryName.toLowerCase() !== 'worldmodel') { return false; }
    if (namespaceFromArchiveName(sourceArchive ?? '') !== 'worl') { return false; }
    const scene = this.mapScene();
    return !!scene && !!findSceneNode(scene, 'worldmodel');
  },

  recordDecodeInfo(this: ViewerPageLogic, label: string, mdl: ZedModel, choice: PositionUnitChoice): void {
    const chainBound = mdl.chunks.filter(c => c.textureBinding === 'chain').length;
    const coloured = mdl.chunks.filter(c => c.hasVertexColors).length;
    const visuals = new Set(mdl.chunks.map(c => c.visualKey).filter(Boolean)).size;
    const binding = chainBound > 0
      ? 'DMA chain'
      : (mdl.chunks.find(c => c.textureBinding)?.textureBinding ?? 'none');
    this.modelDecodeInfo.set({
      binding,
      visuals,
      chunks: mdl.chunks.length,
      chainBound,
      coloured,
      unit: choice.unit,
      unitReason: choice.reason,
      renderedChunks: null,
      droppedChunks: 0,
      missingTextures: [],
    });
    this.log(`[decode] ${label}: binding=${binding} visuals=${visuals} chunks=${mdl.chunks.length} chain-bound=${chainBound} vertex-colours=${coloured} units=${choice.unit} (${choice.reason})`);
  },

  isCurrentWeaponModelLayer(this: ViewerPageLogic): boolean {
    const stack = this.archiveStack();
    const current = stack.length > 0 ? stack[stack.length - 1].name : '';
    const leaf = current.split(/[\\/]/).pop() ?? current;
    return this.isWeaponModelArchive(leaf);
  },

  isCurrentEffectModelLayer(this: ViewerPageLogic): boolean {
    const stack = this.archiveStack();
    const current = stack.length > 0 ? stack[stack.length - 1].name : '';
    const leaf = current.split(/[\\/]/).pop() ?? current;
    return this.isEffectModelArchive(leaf);
  },

  isWeaponModelArchive(this: ViewerPageLogic, archiveName: string | null | undefined): boolean {
    const leaf = (archiveName?.split(/[\\/]/).pop() ?? '').toLowerCase();
    return /^weap_mdl(?:\([^)]+\))?\.zed$/i.test(leaf);
  },

  isEffectModelArchive(this: ViewerPageLogic, archiveName: string | null | undefined): boolean {
    const leaf = (archiveName?.split(/[\\/]/).pop() ?? '').toLowerCase();
    return /^effe_mdl(?:\([^)]+\))?\.zed$/i.test(leaf);
  },

  isCharacterModelArchive(this: ViewerPageLogic, archiveName: string | null | undefined): boolean {
    const leaf = (archiveName?.split(/[\\/]/).pop() ?? '').toLowerCase();
    return /^(?:clib|flib)_mdl(?:\([^)]+\))?\.zed$/i.test(leaf);
  },

  // ------------------------------------------------------------------
  // GEO scene composition. Mirrors the HTML map viewer's
  // buildGeoSceneMesh: walks the loaded GEO scene for placed instances,
  // resolves each instance to a model by name across every loaded
  // *_MDL.ZED in the package, applies the instance's 4x4 transform, and
  // merges everything into one big ZedModel. Renders via the existing
  // model preview pipeline.
  // ------------------------------------------------------------------
  // fromSceneRoot composes the whole map (worldmodel plus every placed
  // prop) whatever is selected; the world model preview uses it.
  // The selected record of a root *_MDL.ZED of an opened .ZDB, editable in the Model Editor.
  canOpenInModelEditor(this: ViewerPageLogic): boolean {
    const stack = this.archiveStack();
    return !!this.selectedEntry() && stack.length === 1 && /\.(zdb|cdb)$/i.test(this.result()?.path ?? '') &&
      /_MDL(?:\([^)]+\))?\.zed$/i.test(this.currentArchiveLeaf() ?? '');
  },
};

export type ModelMethods = typeof modelMethods;
