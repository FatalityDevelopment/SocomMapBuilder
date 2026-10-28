// Asset Viewer models: drawing the decoded model in the 3D preview, and the preview's view controls.

import { ZedModel } from './zed-model-parser';
import { type ModelBounds3, type ModelCameraView, type ModelTextureMap, ZedModelRenderer } from './zed-model-renderer';
import { collectVisualPartNames, findSceneNode } from './zed-geo-parser';
import type { ViewerPageLogic } from './zipper-workbench-page';
import { isPlaceholderTexture, namespaceFromArchiveName, parseModelTexture, textureBytesFor, textureOwnerNamespace, texturePixelsFor } from './zipper-workbench-textures';
import type { Bounds3 } from './zipper-workbench-types';
import { descriptorTextureForWeapon, guessTextureForWeapon, isHelperTextureRef, isWeaponPrimaryTexture, weaponPrimaryPaletteAlias } from './zipper-workbench-weapon-textures';

export const modelRenderMethods = {
  toggleWireframe(this: ViewerPageLogic): void {
    const next = !this.wireframe();
    this.wireframe.set(next);
    this.modelRenderer?.setWireframe(next);
  },

  toggleFlipY(this: ViewerPageLogic): void {
    const next = !this.flipY();
    this.flipY.set(next);
    this.modelRenderer?.setFlipY(next);
  },

  onChunkChange(this: ViewerPageLogic, event: Event): void {
    const value = Number((event.target as HTMLSelectElement).value);
    this.selectedChunk.set(Number.isFinite(value) ? value : -1);
    this.applyModelToRenderer();
  },

  onUvModeChange(this: ViewerPageLogic, event: Event): void {
    const value = (event.target as HTMLSelectElement).value as 'direct' | 'flipV' | 'swap' | 'swapFlipV';
    this.uvMode.set(value);
    this.applyModelToRenderer();
  },

  activeVertexCount(this: ViewerPageLogic): number {
    const mdl = this.model();
    if (!mdl) { return 0; }
    const chunk = this.selectedChunk();
    if (chunk >= 0 && chunk < mdl.chunks.length) { return mdl.chunks[chunk].vertexCount; }
    return mdl.vertices.length;
  },

  chunkLabel(this: ViewerPageLogic, idx: number, chunk: { vertexCount: number; decodedTriangleCount: number; textureName: string | null }): string {
    // User asked for ".tif should be the name" - lead the label with it.
    const tifName = chunk.textureName ?? '(no texture)';
    const visual = (chunk as { visualKey?: string }).visualKey;
    const partName = visual ?? this.partNameFor(idx);
    const stats = `${chunk.vertexCount}v/${chunk.decodedTriangleCount}t`;
    return partName
      ? `${tifName} - ${partName} (#${idx}, ${stats})`
      : `${tifName} (#${idx}, ${stats})`;
  },

  partNameFor(this: ViewerPageLogic, chunkIdx: number): string | null {
    const scene = this.companionScene();
    const entry = this.selectedEntry();
    if (!scene || !entry) { return null; }
    const sceneNode = findSceneNode(scene, entry.name);
    const parts = collectVisualPartNames(sceneNode);
    return parts[chunkIdx] ?? null;
  },

  activeTriangleCount(this: ViewerPageLogic): number {
    const mdl = this.model();
    if (!mdl) { return 0; }
    const chunk = this.selectedChunk();
    if (chunk >= 0 && chunk < mdl.chunks.length) { return mdl.chunks[chunk].decodedTriangleCount; }
    return mdl.indices.length / 3;
  },

  visualLabel(this: ViewerPageLogic, key: string): string {
    const mdl = this.model();
    if (!mdl) { return key; }
    const chunks = mdl.chunks.filter(c => c.visualKey === key);
    const tris = chunks.reduce((n, c) => n + c.decodedTriangleCount, 0);
    return `${key} (${chunks.length} chunk${chunks.length === 1 ? '' : 's'}, ${tris}t)`;
  },

  renderServerModel(this: ViewerPageLogic, textures: ModelTextureMap): void {
    const mdl = this.model();
    if (!this.modelRenderer || !mdl) { return; }
    const renderMode = this.mapRenderMode();
    const topDown = renderMode === 'topSolid' || renderMode === 'topWire';
    const cameraView = this.mapCameraView();
    const result = this.modelRenderer.setModel(mdl, {
      wireframe: renderMode === 'view3dWire' || renderMode === 'topWire',
      flipY: false,
      uvMode: 'direct',
      chunkIndex: this.selectedChunk(),
      visualFilter: this.selectedVisual() || null,
      textures,
      triStride: this.mapTriStride(),
      topDown,
      unlit: renderMode === 'view3dTextured' || renderMode === 'view3dSolid' || renderMode === 'topSolid',
      solidColor: renderMode === 'view3dSolid' || renderMode === 'topSolid',
      cameraView: cameraView === 'auto' && topDown ? 'top' : cameraView,
      vertexColors: this.mapLighting() !== 'off',
      vertexColorScale: this.mapLighting() === 'bright' ? 2 : 1,
    });
    this.modelDecodeInfo.update(info => info
      ? { ...info, renderedChunks: result.renderedChunks, droppedChunks: result.droppedChunks, missingTextures: result.missingTextures }
      : info);
  },

  ensureModelRenderer(this: ViewerPageLogic): void {
    const canvas = this.previewCanvas?.nativeElement;
    if (!canvas) { return; }
    // Angular destroys/recreates the canvas element each time the @if branch
    // re-mounts. If the renderer is still attached to a detached canvas, the
    // new visible one stays blank. Detect and rebind.
    if (this.modelRenderer && this.modelRenderer.attachedCanvas !== canvas) {
      this.modelRenderer.dispose();
      this.modelRenderer = null;
    }
    if (!this.modelRenderer) {
      this.modelRenderer = new ZedModelRenderer(canvas);
      this.modelRenderer.onFlyModeChange = flying => this.flying.set(flying);
    }
  },

  applyModelToRenderer(this: ViewerPageLogic): void {
    try {
      this.applyModelToRendererUnsafe();
    } catch (err) {
      // Surface renderer crashes instead of letting them tear down the
      // entire webview. Users were reporting "the whole panel locks up
      // and nothing displays" when opening UI_MDL.ZED entries; a thrown
      // exception inside setModel will at minimum show this message and
      // keep the entries list responsive.
      const msg = err instanceof Error ? err.message : String(err);
      this.previewMessage.set('Model render failed: ' + msg);
      this.log('[model] render error: ' + msg, true);
      this.modelRenderer?.setModel(null);
    }
  },

  applyModelToRendererUnsafe(this: ViewerPageLogic): void {
    this.ensureModelRenderer();
    const mdl = this.model();
    if (!this.modelRenderer || !mdl) { return; }
    if (this.serverModel?.model === mdl) {
      this.renderServerModel(this.serverModel.textures);
      return;
    }

    const entry = this.selectedEntry();
    const palettes = this.palettes();
    const sources = this.buildTifByteMap();
    const isGeoComposedModel =
      this.currentModelBytes === null &&
      this.currentModelSourceArchive === null &&
      mdl.chunks.some(c => !!c.sourceArchiveName);

    // PS2 weapon models use a single atlas .tif per weapon - the chunks
    // sample different UV regions of the same sheet. Pick a primary atlas
    // texture for the weapon, then load any per-chunk overrides on top.
    const textures: Record<string, ReturnType<typeof texturePixelsFor>> = {};
    const missingTextureSources = new Set<string>();
    const failedTextureDecodes = new Set<string>();
    const paletteOverride = this.paletteOverride();
    let defaultTif: string | null = null;
    let defaultTextureKey: string | null = null;

    const sourceNameForChunk = (chunk: ZedModel['chunks'][number]): string =>
      chunk.sourceModelName ?? entry?.name ?? '';

    const sourceArchiveForChunk = (chunk: ZedModel['chunks'][number]): string | null =>
      chunk.sourceArchiveName ?? this.currentModelSourceArchive;

    const sourceKeyForChunk = (chunk: ZedModel['chunks'][number]): string =>
      `${(sourceArchiveForChunk(chunk) ?? '').toLowerCase()}::${sourceNameForChunk(chunk).toLowerCase()}`;

    const sourceLooksWeaponForChunk = (chunk: ZedModel['chunks'][number]): boolean =>
      namespaceFromArchiveName(sourceArchiveForChunk(chunk)) === 'weap';

    const paletteAliasFor = (
      sourceName: string | null | undefined,
      tifName: string,
      sourceArchiveName: string | null | undefined = null,
    ): string | null => {
      const tex = tifName.toLowerCase();
      if (paletteOverride && defaultTif && tex === defaultTif.toLowerCase()) {
        return paletteOverride;
      }
      const source = sourceName ?? '';
      const explicit = source ? weaponPrimaryPaletteAlias(source, tex) : null;
      if (explicit) { return explicit; }
      const namespace = this.textureNamespaceForArchiveName(sourceArchiveName);
      if (namespace && palettes?.has(`${namespace}:${tex}`)) {
        return `${namespace}:${tex}`;
      }
      return null;
    };

    const textureRenderKeyFor = (
      sourceName: string | null | undefined,
      tifName: string,
      paletteAlias: string | null = paletteAliasFor(sourceName, tifName),
    ): string => {
      const tex = tifName.toLowerCase();
      if (!paletteAlias) { return tex; }
      const source = (sourceName || entry?.name || 'model')
        .toLowerCase()
        .replace(/[^a-z0-9_.-]+/g, '_');
      return `${source}::${tex}::${paletteAlias.toLowerCase()}`;
    };

    if (entry && !isGeoComposedModel) {
      defaultTif = guessTextureForWeapon(entry.name, sources, mdl);
      if (defaultTif) {
        const bytes = textureBytesFor(
          sources,
          defaultTif,
          this.currentModelSourceArchive,
          this.textureNamespaceForArchiveName(this.currentModelSourceArchive),
        );
        if (bytes) {
          try {
            // Honour the user's manual palette override if present, otherwise
            // use weapon-specific TXR/PAL aliases where the texture header id
            // is known to point at the wrong colourway.
            const sourcePaletteAlias = paletteAliasFor(entry.name, defaultTif, this.currentModelSourceArchive);
            const forcedPaletteAlias = paletteOverride ?? sourcePaletteAlias;
            const aliasForLookup = forcedPaletteAlias ?? defaultTif;
            defaultTextureKey = textureRenderKeyFor(entry.name, defaultTif, forcedPaletteAlias);
            const tex = parseModelTexture(bytes, palettes, aliasForLookup, defaultTif);
            textures[defaultTextureKey] = texturePixelsFor(tex);
          } catch {
            failedTextureDecodes.add(defaultTif);
            defaultTif = null;
            defaultTextureKey = null;
          }
        } else {
          missingTextureSources.add(defaultTif);
          defaultTif = null;
          defaultTextureKey = null;
        }
      }
    }

    const hasWeaponPrimaryAtlas = !!(entry && defaultTif && isWeaponPrimaryTexture(entry.name, defaultTif));
    const sourceDefaultTif = new Map<string, string | null>();
    const weaponDefaultForComposedChunk = (chunk: ZedModel['chunks'][number]): string | null => {
      if (!isGeoComposedModel || !sourceLooksWeaponForChunk(chunk)) { return null; }
      const key = sourceKeyForChunk(chunk);
      if (!sourceDefaultTif.has(key)) {
        const groupChunks = mdl.chunks.filter(c => sourceKeyForChunk(c) === key);
        const textureRefs = [...new Set(groupChunks
          .map(c => c.textureName)
          .filter((name): name is string => !!name))];
        sourceDefaultTif.set(key, guessTextureForWeapon(sourceNameForChunk(chunk), sources, {
          chunks: groupChunks,
          textureRefs,
        }));
      }
      return sourceDefaultTif.get(key) ?? null;
    };

    // Per-chunk overrides for chunks that explicitly reference a different tif.
    const effectiveChunkTexture = (chunk: ZedModel['chunks'][number]): string | null => {
      const name = chunk.textureName;
      // A chain-bound name is the texture the game draws this section
      // with; none of the weapon atlas guesses apply.
      if (chunk.textureBinding === 'chain') { return name; }
      const chunkDefaultTif = isGeoComposedModel ? weaponDefaultForComposedChunk(chunk) : defaultTif;
      if (!name || !chunkDefaultTif) { return name; }
      if (chunk.textureBinding === 'descriptor') {
        return descriptorTextureForWeapon(sourceNameForChunk(chunk), chunkDefaultTif, name);
      }
      // Weapon payloads often keep helper/marker .tif names near the real
      // atlas name in the same tail block (M4: mark03.tif + m4.tif). For
      // known weapon models, the selected weapon atlas is authoritative.
      const chunkHasWeaponPrimaryAtlas = isGeoComposedModel
        ? isWeaponPrimaryTexture(sourceNameForChunk(chunk), chunkDefaultTif)
        : hasWeaponPrimaryAtlas;
      if (chunkHasWeaponPrimaryAtlas && name.toLowerCase() !== chunkDefaultTif) {
        return chunkDefaultTif;
      }
      if ((!isGeoComposedModel || sourceLooksWeaponForChunk(chunk)) &&
        isHelperTextureRef(name) &&
        !isHelperTextureRef(chunkDefaultTif)) {
        return chunkDefaultTif;
      }
      return name;
    };

    type NeededTexture = { sourceName: string; sourceArchiveName: string | null; textureName: string; paletteAlias: string | null };
    const needed = new Map<string, NeededTexture>();
    for (const c of mdl.chunks) {
      const texName = effectiveChunkTexture(c);
      if (!texName || isPlaceholderTexture(texName)) { continue; }
      const sourceName = sourceNameForChunk(c);
      const sourceArchiveName = sourceArchiveForChunk(c);
      const paletteAlias = paletteAliasFor(sourceName, texName, sourceArchiveName);
      const key = textureRenderKeyFor(sourceName, texName, paletteAlias);
      if (!needed.has(key)) {
        needed.set(key, { sourceName, sourceArchiveName, textureName: texName.toLowerCase(), paletteAlias });
      }
    }
    for (const [key, req] of needed) {
      if (textures[key]) { continue; }
      const preferredNamespace = this.textureNamespaceForArchiveName(req.sourceArchiveName);
      const bytes = textureBytesFor(
        sources,
        req.textureName,
        req.sourceArchiveName,
        preferredNamespace,
      );
      if (!bytes) {
        missingTextureSources.add(req.textureName);
        continue;
      }
      try {
        const aliasForLookup = req.paletteAlias ?? req.textureName;
        // The palette pairs with the TXR the pixels came from, which is not
        // always the model's own (MP71's sky textures live in RM71_TXR).
        const ownerNamespace = textureOwnerNamespace(sources, req.textureName, bytes, preferredNamespace);
        const tex = parseModelTexture(bytes, palettes, aliasForLookup, req.textureName, ownerNamespace);
        textures[key] = texturePixelsFor(tex);
      } catch { failedTextureDecodes.add(req.textureName); }
    }

    if (missingTextureSources.size > 0) {
      this.requestRootTexturesForMissing([...missingTextureSources]);
    }

    const traceTextureHandoff = !!entry && (
      entry.name.toLowerCase().includes('fiveseven') ||
      entry.name.toLowerCase() === 'worldmodel' ||
      /^worl_mdl(?:\([^)]+\))?\.zed$/i.test(this.currentModelSourceArchive ?? '')
    );
    if (entry && (missingTextureSources.size > 0 || failedTextureDecodes.size > 0 || traceTextureHandoff)) {
      const missing = [...missingTextureSources].sort();
      const failed = [...failedTextureDecodes].sort();
      const loaded = Object.keys(textures).sort();
      const required = [...needed.values()]
        .map(req => req.paletteAlias ? `${req.textureName}@${req.paletteAlias}` : req.textureName)
        .sort();
      const digest = `${entry.name}|default=${defaultTif ?? ''}|needed=${required.join(',')}|loaded=${loaded.join(',')}|missing=${missing.join(',')}|failed=${failed.join(',')}`;
      if (digest !== this.lastTextureDiagnostic) {
        this.log(`[texture] ${entry.name}: default=${defaultTif ?? '(none)'} needed=${required.join(', ') || '(none)'} loaded=${loaded.join(', ') || '(none)'}`);
        if (missing.length) { this.log(`[texture] missing source(s): ${missing.join(', ')}`); }
        if (failed.length) { this.log(`[texture] decode failed: ${failed.join(', ')}`); }
        this.lastTextureDiagnostic = digest;
      }
    } else {
      this.lastTextureDiagnostic = '';
    }

    // Map-viewer dropdowns flow through as part of the render options. The
    // wireframe / topdown / lit toggles are derived from `mapRenderMode`;
    // `wireframe()` and `flipY()` from the button bank still win when set so
    // existing weapon previews aren't disturbed.
    const renderMode = this.mapRenderMode();
    const wireframeFromMode = renderMode === 'view3dWire' || renderMode === 'topWire';
    const topDown = renderMode === 'topSolid' || renderMode === 'topWire';
    const unlit = renderMode === 'view3dTextured' || renderMode === 'view3dSolid' || renderMode === 'topSolid';
    const solidColor = renderMode === 'view3dSolid' || renderMode === 'topSolid';
    const cameraView = this.mapCameraView();
    const rendererCameraView: ModelCameraView = cameraView === 'auto' && topDown ? 'top' : cameraView;
    const mapReference = this.activeMapCameraReference();
    const sourceNamespace = namespaceFromArchiveName(this.currentModelSourceArchive ?? '');
    const isWorldModel = (entry?.name ?? '').toLowerCase() === 'worldmodel' || sourceNamespace === 'worl';
    const shouldShowMapBounds = !!mapReference && (isWorldModel || cameraView === 'mapTop' || cameraView === 'mapIso');
    const referenceBounds = shouldShowMapBounds && mapReference
      ? { label: mapReference.label, bounds: toRendererBounds(mapReference.bounds) }
      : null;
    const hasEffectSource = this.isCurrentEffectModelLayer() ||
      mdl.chunks.some(c => namespaceFromArchiveName(c.sourceArchiveName ?? this.currentModelSourceArchive) === 'effe');

    // The old separate "UV mode" and "Flip Y" toggles are gone — derive
    // their values from the consolidated V-orientation dropdown so the
    // renderer keeps working without duplicate UI:
    //   mapTextureV === 'flipV' -> uvMode = 'flipV'
    //   otherwise               -> uvMode = 'direct'
    const effectiveUvMode = this.mapTextureV() === 'flipV' ? 'flipV' : 'direct';
    const renderModel = {
      ...mdl,
      chunks: mdl.chunks.filter(c => (this.showSky() || !isSkyChunk(c)) && !isPlaceholderTexture(c.textureName)).map(c => {
        const textureName = effectiveChunkTexture(c);
        const sourceName = sourceNameForChunk(c);
        const sourceArchiveName = sourceArchiveForChunk(c);
        return {
          ...c,
          textureName: textureName
            ? textureRenderKeyFor(sourceName, textureName, paletteAliasFor(sourceName, textureName, sourceArchiveName))
            : null,
        };
      }),
    };

    const renderResult = this.modelRenderer.setModel(renderModel, {
      wireframe: wireframeFromMode,
      flipY: false,
      uvMode: effectiveUvMode,
      chunkIndex: this.selectedChunk(),
      visualFilter: this.selectedVisual() || null,
      textures,
      defaultTexture: defaultTextureKey ?? defaultTif,
      textureRgba: hasWeaponPrimaryAtlas && defaultTextureKey ? textures[defaultTextureKey] ?? null : null,
      triStride: this.mapTriStride(),
      topDown,
      unlit: unlit || hasEffectSource,
      solidColor,
      transparentTextures: hasEffectSource,
      wireOverlay: hasEffectSource,
      wireOverlayColor: effectWireOverlayColor(entry?.name ?? ''),
      cameraView: rendererCameraView,
      referenceBounds,
      showBounds: shouldShowMapBounds,
      vertexColors: this.mapLighting() !== 'off',
      vertexColorScale: this.mapLighting() === 'bright' ? 2 : 1,
      markers: this.mapMarkersFor(mdl),
    });
    const missingForPane = [...new Set([...missingTextureSources, ...failedTextureDecodes, ...renderResult.missingTextures.map(k => k.split('::')[1] ?? k)])].sort();
    this.modelDecodeInfo.update(info => info
      ? { ...info, renderedChunks: renderResult.renderedChunks, droppedChunks: renderResult.droppedChunks, missingTextures: missingForPane }
      : info);
    if (renderResult.droppedChunks > 0) {
      this.previewMessage.set(`${renderResult.droppedChunks} chunk(s) not drawn: geometry exceeds the renderer cap (${renderResult.vertices} vertices drawn).`);
      this.log(`[render] ${renderResult.droppedChunks} chunk(s) dropped at the geometry cap`, true);
    }
  },

  // ------------------------------------------------------------------
  // Map-viewer dropdowns — set the corresponding signal and ping the
  // renderer config so any in-flight model preview re-draws with the new
  // setting. Defaults match the prior behaviour, so weapon previews don't
  // shift unless the user actively changes a setting.
  // ------------------------------------------------------------------
  onMapRenderModeChange(this: ViewerPageLogic, event: Event): void {
    this.mapRenderMode.set((event.target as HTMLSelectElement).value as ReturnType<typeof this.mapRenderMode>);
    this.applyModelToRenderer();
  },

  onMapCameraViewChange(this: ViewerPageLogic, event: Event): void {
    this.mapCameraView.set((event.target as HTMLSelectElement).value as ModelCameraView);
    this.applyModelToRenderer();
  },

  onMapTriStrideChange(this: ViewerPageLogic, event: Event): void {
    const n = Number((event.target as HTMLSelectElement).value);
    this.mapTriStride.set(Number.isFinite(n) && n > 0 ? n : 1);
    this.applyModelToRenderer();
  },

  onMapMaterialBindingChange(this: ViewerPageLogic, event: Event): void {
    this.mapMaterialBinding.set((event.target as HTMLSelectElement).value as ReturnType<typeof this.mapMaterialBinding>);
    // Re-run the binding pass against the cached model bytes so the dropdown
    // takes effect immediately. We re-parse from raw bytes to discard the
    // previous binding's overrides; otherwise switching back to tailScan
    // would leave stale smartWorld picks in place.
    if (this.canRedecodePreview()) {
      this.refreshPreview();
      return;
    }
    this.applyModelToRenderer();
  },

  onSelectedVisualChange(this: ViewerPageLogic, event: Event): void {
    this.selectedVisual.set((event.target as HTMLSelectElement).value);
    this.applyModelToRenderer();
  },

  onMapPositionUnitChange(this: ViewerPageLogic, event: Event): void {
    this.mapPositionUnit.set((event.target as HTMLSelectElement).value as ReturnType<typeof this.mapPositionUnit>);
    // Positions are baked at parse time, so decode the selected payload
    // again from its cached bytes.
    if (this.canRedecodePreview()) {
      this.refreshPreview();
      return;
    }
    this.applyModelToRenderer();
  },

  onMapTextureVChange(this: ViewerPageLogic, event: Event): void {
    this.mapTextureV.set((event.target as HTMLSelectElement).value as ReturnType<typeof this.mapTextureV>);
    this.applyModelToRenderer();
  },

  onMapUvInterpretationChange(this: ViewerPageLogic, event: Event): void {
    this.mapUvInterpretation.set((event.target as HTMLSelectElement).value as ReturnType<typeof this.mapUvInterpretation>);
    this.applyModelToRenderer();
  },
};

export type ModelRenderMethods = typeof modelRenderMethods;

export function effectWireOverlayColor(modelName: string): number {
  const lower = modelName.toLowerCase();
  if (lower.includes('tracer')) { return 0xffcd5a; }
  if (lower.includes('shotgun')) { return 0xd24b37; }
  if (lower.includes('shell')) { return 0xd6b450; }
  if (lower.includes('muzzle') || lower.includes('flash')) { return 0xffd36a; }
  return 0x78beff;
}

export function toRendererBounds(bounds: Bounds3): ModelBounds3 {
  return {
    minX: bounds.minX,
    minY: bounds.minY,
    minZ: bounds.minZ,
    maxX: bounds.maxX,
    maxY: bounds.maxY,
    maxZ: bounds.maxZ,
  };
}

// Sky geometry: the sky archive (SP<map>_MDL: sky dome, mist), the map's
// sky / sun / flare nodes, and sky / sun textures.
export function isSkyChunk(c: { sourceArchiveName?: string | null; sourceModelName?: string | null; visualKey?: string | null; textureName?: string | null }): boolean {
  if (/^sp\d+$/.test(namespaceFromArchiveName(c.sourceArchiveName) ?? '')) { return true; }
  if (/^(?:sky|thesun|sun|flare|mist\d*)$/i.test(c.sourceModelName ?? '')) { return true; }
  return /^(?:sky|sunglow|sun_|flare|lensflare|cloud)/i.test(c.textureName ?? '');
}
