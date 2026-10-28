// Asset Viewer textures (_TXR archives and .tif records): thumbnails, the sidebar grid, texture lookup for models
// and the texture reports.

import { host } from '../../shared';
import { parseTexture, parseTextureHeader, ZedPalette, ZedTexture } from './zed-texture-parser';
import { ZedModel } from './zed-model-parser';
import type { ModelTextureMap } from './zed-model-renderer';
import type { ViewerPageLogic } from './zipper-workbench-page';
import type { BrowserItem } from './zipper-workbench-types';

export const textureMethods = {
  pixelsToDataUrl(this: ViewerPageLogic, p: ModelTextureMap[string]): string {
    const hit = this.pixelUrls.get(p);
    if (hit) { return hit; }
    const canvas = document.createElement('canvas');
    canvas.width = p.width;
    canvas.height = p.height;
    const ctx = canvas.getContext('2d');
    if (!ctx) { return ''; }
    const rgba = new Uint8ClampedArray(p.width * p.height * 4);
    rgba.set(p.rgba.subarray(0, rgba.length));
    ctx.putImageData(new ImageData(rgba, p.width, p.height), 0, 0);
    const url = canvas.toDataURL();
    this.pixelUrls.set(p, url);
    return url;
  },

  computeSidebarTextures(this: ViewerPageLogic): Array<{ name: string; dataUrl: string }> {
    const filter = this.mapTextureFilter().trim().toLowerCase();
    const mdl = this.model();
    const modelOpen = !!mdl && this.previewMode() === 'model';
    // A model decoded by the server: its textures exactly as drawn.
    if (modelOpen && this.serverModel?.model === mdl) {
      return Object.entries(this.serverModel.textures)
        .filter(([name]) => !filter || name.includes(filter))
        .map(([name, p]) => ({ name, dataUrl: this.pixelsToDataUrl(p) }))
        .sort((a, b) => a.name.localeCompare(b.name));
    }
    const sources = this.companionTextures();
    if (!sources || sources.size === 0) { return []; }
    const palettes = this.palettes();
    const wanted = modelOpen ? new Set(mdl!.chunks.map(c => c.textureName?.toLowerCase()).filter((t): t is string => !!t)) : null;
    const ns = this.layerNamespace();
    const nsHasTextures = !!ns && [...sources.keys()].some(k => k.startsWith(ns + ':'));
    const out: Array<{ name: string; dataUrl: string }> = [];
    for (const [name, bytes] of sources) {
      const leaf = name.includes(':') ? name.slice(name.indexOf(':') + 1) : name;
      if (wanted) {
        // Each texture once: the plain key.
        if (name.includes(':') || !wanted.has(leaf)) { continue; }
      } else if (nsHasTextures) {
        if (!name.startsWith(ns + ':')) { continue; }
      } else if (name.includes(':')) { continue; }
      if (filter && !name.toLowerCase().includes(filter)) { continue; }
      try {
        const tex = parseTexture(bytes, palettes, name);
        out.push({ name, dataUrl: textureToDataUrl(tex) });
      } catch { /* skip undecodable */ }
      if (out.length >= SIDEBAR_TEXTURE_LIMIT) { break; }
    }
    out.sort((a, b) => a.name.localeCompare(b.name));
    return out;
  },

  // A texture as PNG (decoded by the server, palette and alpha as the editors show them).
  async exportTexturePng(this: ViewerPageLogic, item: BrowserItem): Promise<void> {
    try {
      await this.serverModelIndex(this.rootPath());
      const ns = /^(.+?)_TXR/i.exec(this.currentFileName().split(/[\\/]/).pop() ?? '')?.[1]?.toLowerCase() ?? null;
      const { png } = await this.server.texture(item.name, ns);
      host.saveFile(item.name.replace(/\.tif$/i, '.png'), { base64: png }, 'image/png');
    } catch (err) {
      this.statusMsg.set(`Export ${item.name}: ${err instanceof Error ? err.message : String(err)}`);
      this.statusIsError.set(true);
    }
  },

  textureFormatLabel(this: ViewerPageLogic, tex: ZedTexture): string {
    const h = tex.header;
    const psm = `PSM 0x${h.psm.toString(16).toUpperCase().padStart(2, '0')}`;
    switch (h.bpp) {
      case 4: return `4-bit indexed (${psm})`;
      case 8: return `8-bit indexed (${psm})`;
      case 16: return `16-bit A1B5G5R5 (${psm})`;
      case 24: return `24-bit RGB (${psm})`;
      default: return `32-bit RGBA (${psm})`;
    }
  },

  textureGsLabel(this: ViewerPageLogic, tex: ZedTexture): string {
    const gs = tex.header.gs;
    if (!gs) { return 'no GS registers in record'; }
    const alpha = gs.alphaTest === null ? 'off' : `>= ${(gs.alphaTest * 100).toFixed(0)}%`;
    return `wrap ${gs.wrapS}/${gs.wrapT}, alpha test ${alpha}${gs.alphaBlend ? ', blended' : ''}${tex.hasTransparency ? ', has transparent texels' : ''}`;
  },

  // Missing textures after a render: fetch every root-level TXR/PAL pair
  // that is not loaded yet (once per archive per file) and re-render when
  // they land. Covers a package that failed or is incomplete, and models
  // whose textures live in another prefix's archive.
  requestRootTexturesForMissing(this: ViewerPageLogic, missing: string[]): void {
    if (this.packageLoading()) { return; }
    const filePath = this.result()?.path;
    if (!filePath || this.archiveStack().length === 0) { return; }
    const loadedNamespaces = new Set(this.textureSourceSummary().archives);
    const rootEntries = this.result()?.entries ?? [];
    const requested: string[] = [];
    for (const e of rootEntries) {
      const leaf = (e.name.split(/[\\/]/).pop() ?? e.name).toLowerCase();
      const m = /^(.+?)_(txr|pal)(?:\([^)]+\))?\.zed$/i.exec(leaf);
      if (!m) { continue; }
      const namespace = m[1].toLowerCase();
      const kind = m[2].toLowerCase() as 'txr' | 'pal';
      if (kind === 'txr' && loadedNamespaces.has(namespace)) { continue; }
      if (kind === 'pal' && this.rootCompanionPalArchives.has(namespace)) { continue; }
      if (this.packageEntryBytes.has(leaf)) { continue; }
      if (this.rootTextureFetchRequested.has(leaf)) { continue; }
      this.rootTextureFetchRequested.add(leaf);
      this.rootCompanionFetches.set(e.index, kind);
      this.vscode.postMessage({ type: 'readNode', path: filePath, entryIndex: e.index });
      requested.push(leaf);
    }
    if (requested.length > 0) {
      this.log(`[texture] ${missing.length} texture(s) missing; fetching ${requested.join(', ')} from the root archive`);
      this.setStatus(`Fetching ${requested.length} texture archive(s) for ${missing.length} missing texture(s)…`, false, true);
    }
  },

  // For an entry that names a texture (".tif"), find the next entry that holds
  // the pixel bytes. In real UIBF_TXR files the data entry is named "texdat".
  // IMPORTANT: this must read from the *current* layer's entries (sub-archive
  // when drilled in), not the root inspect result.
  findTextureDataIndex(this: ViewerPageLogic, tifIndex: number): number {
    // Always use the unfiltered list so adjacency to the texdat record is
    // preserved (the filtered table view drops some neighbours).
    const entries = this.rawEntries();
    const tif = entries.find(e => e.index === tifIndex);
    if (!tif) { return tifIndex; }
    // If the .tif entry itself has data, use it.
    if (tif.size > 0) { return tifIndex; }
    // Otherwise look at up to the next 3 entries.
    const ordered = entries.slice().sort((a, b) => a.index - b.index);
    const pos = ordered.findIndex(e => e.index === tifIndex);
    for (let k = pos + 1; k < Math.min(pos + 4, ordered.length); k++) {
      const next = ordered[k];
      if (/\.tif$/i.test(next.name)) { break; }
      if (next.size > 0) { return next.index; }
    }
    return -1;
  },

  textureNamespaceForArchiveName(this: ViewerPageLogic, archiveName: string | null | undefined): string | null {
    const namespace = namespaceFromArchiveName(archiveName);
    if (!namespace) { return null; }
    if (this.packageHasArchive(namespace, 'txr')) { return namespace; }

    // WORL_MDL is the map's baked world mesh, but SOCOM packages do not ship
    // a WORL_TXR/PAL pair. Its texture refs resolve through the active map
    // namespace instead: M51.ZDB -> M51_TXR/PAL, MP63.ZDB -> MP63_TXR/PAL, etc.
    if (namespace === 'worl') {
      return this.primaryMapTextureNamespace();
    }

    return namespace;
  },

  primaryMapTextureNamespace(this: ViewerPageLogic): string | null {
    const path = this.result()?.path;
    const leaf = path ? (path.split(/[\\/]/).pop() ?? path).replace(/\.[^.]+$/i, '').toLowerCase() : '';
    if (leaf && this.packageHasArchive(leaf, 'txr')) { return leaf; }

    const supportPrefixes = new Set(['weap', 'flib', 'clib', 'benz', 'objt', 'effe', 'trmg', 'tlrm', 'alph', 'font', 'hud', 'hud2', 'hudw', 'load', 'junk']);
    for (const ns of this.directCompanionTextureNamespaces) {
      if (supportPrefixes.has(ns)) { continue; }
      if (/^ld\d+$/i.test(ns) || /^z.+\d+$/i.test(ns)) { continue; }
      return ns;
    }
    for (const ns of this.rootCompanionTxrArchives.keys()) {
      if (supportPrefixes.has(ns)) { continue; }
      if (/^ld\d+$/i.test(ns) || /^z.+\d+$/i.test(ns)) { continue; }
      return ns;
    }
    for (const key of this.packageEntryBytes.keys()) {
      const ns = namespaceFromArchiveName(key);
      if (!ns || supportPrefixes.has(ns)) { continue; }
      if (this.packageHasArchive(ns, 'txr') && (this.packageHasArchive(ns, 'mdl') || this.packageHasArchive(ns, 'geo'))) {
        return ns;
      }
    }
    return null;
  },

  computeTextureThumbnails(this: ViewerPageLogic): Array<{
    name: string;
    width: number;
    height: number;
    dataUrl: string;
    entryIndex: number;
    paletteResolved: boolean;
  }> {
    if (!this.isCurrentFileTextureArchive()) { return []; }
    const entries = this.rawEntries();
    const palettes = this.palettes();
    const sources = this.buildTifByteMap();
    const out: Array<{ name: string; width: number; height: number; dataUrl: string; entryIndex: number; paletteResolved: boolean }> = [];
    for (let i = 0; i < entries.length; i++) {
      const entry = entries[i];
      if (!/\.tif$/i.test(entry.name)) { continue; }
      // Locate the bytes either via companion map (keyed by tif name) or by
      // looking at the very next entry that's a real payload.
      let bytes: Uint8Array | undefined = sources.get(entry.name.toLowerCase());
      if (!bytes) {
        for (let j = i + 1; j < Math.min(i + 4, entries.length); j++) {
          if (/\.tif$/i.test(entries[j].name)) { break; }
          if (entries[j].size > 0) {
            bytes = this.bytesFromActiveArchive(entries[j].index) ?? this.nodeCache.get(entries[j].index);
            break;
          }
        }
      }
      if (!bytes || bytes.length < 0x20) { continue; }
      try {
        const tex = parseTexture(bytes, palettes, entry.name);
        out.push({
          name: entry.name,
          width: tex.header.width,
          height: tex.header.height,
          dataUrl: textureToDataUrl(tex),
          entryIndex: entry.index,
          paletteResolved: tex.paletteResolved,
        });
      } catch { /* skip undecodable tile */ }
    }
    return out;
  },

  // Aggregate all known .tif name -> texdat bytes from every source: the root
  // companion _TXR file and any sub-archive currently on the navigation stack.
  buildTifByteMap(this: ViewerPageLogic): Map<string, Uint8Array> {
    const map = new Map<string, Uint8Array>();
    const root = this.companionTextures();
    if (root) {
      for (const [name, bytes] of root) { map.set(name, bytes); }
    }
    for (const layer of this.archiveStack()) {
      const archive = layer.archive;
      const namespace = namespaceFromArchiveName(layer.name);
      const entries = archive.entries;
      for (let i = 0; i < entries.length; i++) {
        if (!/\.tif$/i.test(entries[i].name)) { continue; }
        for (let j = i + 1; j < Math.min(i + 4, entries.length); j++) {
          if (/^texdat$/i.test(entries[j].name)) {
            const data = archive.entryData.get(entries[j].index);
            if (data && data.length > 0) {
              const key = entries[i].name.toLowerCase();
              map.set(key, data);
              if (namespace) { map.set(`${namespace}:${key}`, data); }
            }
            break;
          }
          if (/\.tif$/i.test(entries[j].name)) { break; }
        }
      }
    }
    return map;
  },

  findModelTexture(this: ViewerPageLogic, mdl: ZedModel): { width: number; height: number; rgba: Uint8Array } | null {
    if (!mdl.textureRefs.length) { return null; }
    const entries = this.currentEntries();
    for (const ref of mdl.textureRefs) {
      const entry = entries.find(e => e.name.toLowerCase() === ref.toLowerCase());
      if (!entry) { continue; }
      const cached = this.bytesFromActiveArchive(entry.index) ?? this.nodeCache.get(entry.index);
      if (!cached) {
        // Request asynchronously; will apply on next preview refresh.
        const filePath = this.result()?.path;
        if (filePath && this.archiveStack().length === 0) {
          this.vscode.postMessage({ type: 'readNode', path: filePath, entryIndex: entry.index });
        }
        continue;
      }
      try {
        const tex = parseTexture(cached, this.palettes(), ref.toLowerCase());
        return { width: tex.header.width, height: tex.header.height, rgba: tex.rgba };
      } catch { /* try next */ }
    }
    return null;
  },

  // ------------------------------------------------------------------
  // Sidebar texture grid — click a thumbnail to jump to the matching .tif
  // entry in the current layer's table (which then loads the preview pane).
  // ------------------------------------------------------------------
  onSidebarTextureClick(this: ViewerPageLogic, name: string): void {
    const lower = name.toLowerCase();
    const target = this.currentEntries().find(
      e => e.name.toLowerCase() === lower || e.path.toLowerCase().endsWith('/' + lower),
    );
    if (target) { this.selectedIndex.set(target.index); }
  },

  activeTextureName(this: ViewerPageLogic): string {
    const tex = this.texture();
    const entry = this.selectedEntry();
    return tex && entry && /\.tif$/i.test(entry.name) ? entry.name.toLowerCase() : '';
  },

  onMapTextureFilterInput(this: ViewerPageLogic, event: Event): void {
    this.mapTextureFilter.set((event.target as HTMLInputElement).value);
  },

  // ------------------------------------------------------------------
  // Dump-report buttons — replicate the HTML map viewer's "Dump *"
  // buttons. Output streams to the Code Designer ZDB output channel via
  // log(reveal=true).
  // ------------------------------------------------------------------
  dumpMissingTextures(this: ViewerPageLogic): void {
    const mdl = this.model();
    if (!mdl) { this.log('[DUMP missing] no model selected', true); return; }
    const textureSources = this.companionTextures();
    const have = textureSources ? new Set([...textureSources.keys()].map(k => k.toLowerCase())) : new Set<string>();
    const missing: string[] = [];
    for (const ref of mdl.textureRefs ?? []) {
      if (!have.has(ref.toLowerCase())) { missing.push(ref); }
    }
    this.log('--- Missing textures ---', true);
    this.log(`Total refs: ${mdl.textureRefs.length}, missing: ${missing.length}`);
    if (missing.length === 0) { this.log('(none)'); }
    for (const r of missing) { this.log(`  ${r}`); }
  },

  dumpMaterialReport(this: ViewerPageLogic): void {
    const mdl = this.model();
    if (!mdl) { this.log('[DUMP material] no model selected', true); return; }
    this.log('--- Material report ---', true);
    this.log(`Chunks: ${mdl.chunks.length}, vertices: ${mdl.vertices.length}, triangles: ${mdl.indices.length / 3}`);
    this.log(`Binding mode: ${this.mapMaterialBinding()}, UV interpretation: ${this.mapUvInterpretation()}, V mode: ${this.mapTextureV()}`);
    for (let i = 0; i < mdl.chunks.length; i++) {
      const c = mdl.chunks[i];
      const binding = c.textureBinding ? ` ${c.textureBinding}` : '';
      const visual = c.visualKey ? ` visual=${c.visualKey}` : '';
      const colours = c.hasVertexColors ? ' colours=yes' : '';
      const unit = c.positionUnit ? ` units=${c.positionUnit}` : '';
      this.log(`  chunk[${i}] tex=${c.textureName ?? '(none)'}${binding}${visual}${colours}${unit} v=${c.vertexCount} t=${c.decodedTriangleCount}/${c.triangleCount} scale=${c.scale.toFixed(4)}`);
    }
  },
};

export type TextureMethods = typeof textureMethods;

export function namespaceFromArchiveName(archiveName: string | null | undefined): string | null {
  if (!archiveName) { return null; }
  const leaf = (archiveName.split(/[\\/]/).pop() ?? archiveName).toLowerCase();
  const m = /^(.+?)_(?:mdl|txr|pal|geo)(?:\([^)]+\))?\.zed$/i.exec(leaf);
  return m ? m[1].toLowerCase() : null;
}

export function textureBytesFor(
  sources: Map<string, Uint8Array>,
  tifName: string,
  sourceArchiveName: string | null | undefined,
  textureNamespace: string | null = null,
): Uint8Array | undefined {
  const tex = tifName.toLowerCase();
  const namespace = textureNamespace ?? namespaceFromArchiveName(sourceArchiveName);
  if (namespace) {
    const namespaced = sources.get(`${namespace}:${tex}`);
    if (namespaced) { return namespaced; }
  }
  return sources.get(tex);
}

// The TXR namespace that holds these exact texture bytes (`ns:name.tif` in
// the source map), preferring `preferred`; null when no namespaced copy is.
export function textureOwnerNamespace(
  sources: Map<string, Uint8Array>,
  tifName: string,
  bytes: Uint8Array,
  preferred: string | null,
): string | null {
  const tex = tifName.toLowerCase();
  if (preferred && sources.get(`${preferred}:${tex}`) === bytes) { return preferred; }
  const suffix = `:${tex}`;
  for (const [key, value] of sources) {
    if (value === bytes && key.endsWith(suffix)) { return key.slice(0, -suffix.length); }
  }
  return null;
}

export function parseModelTexture(
  bytes: Uint8Array,
  palettes: Map<string, ZedPalette> | null,
  primaryAlias: string | null,
  fallbackAlias: string | null = null,
  textureNamespace: string | null = null,
): ZedTexture {
  const aliases: Array<string | null> = [];
  const addAlias = (alias: string | null) => {
    const key = alias?.toLowerCase() ?? null;
    if (aliases.some(a => (a?.toLowerCase() ?? null) === key)) { return; }
    aliases.push(alias);
  };
  const namespace = namespaceFromPaletteAlias(primaryAlias) ?? namespaceFromPaletteAlias(fallbackAlias);
  const strictAliasFirst = namespace === 'weap' || namespace === 'flib';
  let header: ReturnType<typeof parseTextureHeader> | null = null;
  try {
    header = parseTextureHeader(bytes);
  } catch { /* parseTexture will report the real error below */ }

  // TEX0.CBP is the palette the game binds: texpal_<CBP> in the texture's
  // own TXR/PAL pair. When that palette is loaded it wins over the guesses
  // below (header ids are TBP slots, not palette ids, and the "most visible"
  // pick preferred bright wrong palettes: pale opaque tree cards, off
  // colours across M51 / MP71). An explicit palette alias (a user override
  // or a weapon fix) still comes first.
  const explicitPalette = !!primaryAlias && /(^|:)texpal_\d+$/i.test(primaryAlias);
  const cbpNamespace = textureNamespace ?? namespace;
  if (header && header.cbp !== null && cbpNamespace && !explicitPalette && !strictAliasFirst) {
    const exactKey = `${cbpNamespace}:texpal_${header.cbp}`;
    if (palettes?.has(exactKey)) { return parseTexture(bytes, palettes, exactKey); }
  }

  if (strictAliasFirst) {
    addAlias(primaryAlias);
    addAlias(fallbackAlias);
    if (header) {
      addAlias(`weap:texpal_${header.paletteId}`);
      addAlias(`texpal_${header.paletteId}`);
    }
    return parseFirstResolvedTexture(bytes, palettes, aliases, primaryAlias);
  }

  // M51/map-object textures match the standalone HTML viewer more closely when
  // the texture header's palette ids are tried before the order-based .tif
  // aliases. Those aliases are useful for some archives, but when they are
  // wrong they produce the same black/noisy surfaces we saw on the ammo box.
  if (header) {
    for (const id of texturePaletteCandidates(header.paletteId, header.paletteAlt)) {
      if (namespace) {
        addAlias(`${namespace}:texpal_${id}`);
        addAlias(`${namespace}:${id}`);
      }
      addAlias(`texpal_${id}`);
      addAlias(String(id));
    }
  }
  addAlias(primaryAlias);
  addAlias(fallbackAlias);
  return parseBestNonBlankTexture(bytes, palettes, aliases, primaryAlias);
}

export function parseFirstResolvedTexture(
  bytes: Uint8Array,
  palettes: Map<string, ZedPalette> | null,
  aliases: Array<string | null>,
  fallbackAlias: string | null,
): ZedTexture {
  let last: ZedTexture | null = null;
  for (const alias of aliases) {
    const tex = parseTexture(bytes, palettes, alias);
    if (!tex.header.isIndexed || tex.paletteResolved) { return tex; }
    last = tex;
  }
  return last ?? parseTexture(bytes, palettes, fallbackAlias);
}

export function parseBestNonBlankTexture(
  bytes: Uint8Array,
  palettes: Map<string, ZedPalette> | null,
  aliases: Array<string | null>,
  fallbackAlias: string | null,
): ZedTexture {
  let lastUnresolved: ZedTexture | null = null;
  let firstResolved: ZedTexture | null = null;
  let bestResolved: ZedTexture | null = null;
  let bestScore = -1;

  for (const alias of aliases) {
    const tex = parseTexture(bytes, palettes, alias);
    if (!tex.header.isIndexed) { return tex; }
    if (!tex.paletteResolved) {
      lastUnresolved = tex;
      continue;
    }

    firstResolved ??= tex;
    const score = textureVisibilityScore(tex);
    if (score > bestScore) {
      bestScore = score;
      bestResolved = tex;
    }
    if (score >= 18) { return tex; }
  }

  return bestResolved ?? firstResolved ?? lastUnresolved ?? parseTexture(bytes, palettes, fallbackAlias);
}

export function texturePaletteCandidates(paletteId: number, paletteAlt: number): number[] {
  const out: number[] = [];
  const add = (value: number) => {
    if (!Number.isFinite(value) || value < 0 || out.includes(value)) { return; }
    out.push(value);
  };
  add(paletteId);
  add(paletteId + 102);
  add(paletteAlt);
  add(paletteAlt + 102);
  return out;
}

export function namespaceFromPaletteAlias(alias: string | null | undefined): string | null {
  if (!alias || !alias.includes(':')) { return null; }
  return alias.split(':', 1)[0].toLowerCase();
}

export function textureVisibilityScore(tex: ZedTexture): number {
  const rgba = tex.rgba;
  const pixels = Math.max(1, rgba.length / 4);
  const step = Math.max(1, Math.floor(pixels / 768));
  let visible = 0;
  let lit = 0;
  let colorEnergy = 0;
  let sampled = 0;
  for (let i = 0; i < pixels; i += step) {
    sampled++;
    const o = i * 4;
    if (rgba[o + 3] <= 8) { continue; }
    visible++;
    const brightness = (rgba[o] + rgba[o + 1] + rgba[o + 2]) / 3;
    colorEnergy += brightness;
    if (brightness > 18) { lit++; }
  }
  if (visible === 0) { return 0; }
  return (visible / sampled) * 10 + (lit / visible) * 20 + (colorEnergy / visible) / 16;
}

// `null_xmas.bmp` and similar: a placeholder no texture archive holds, on
// small flat helper polygons (turret bases, world markers). The game shows
// nothing there; drawn, they were grey tiles listed as missing textures.
export function isPlaceholderTexture(name: string | null | undefined): boolean {
  return /^null[_.]/i.test(name ?? '');
}

export function texturePixelsFor(tex: ZedTexture): { width: number; height: number; rgba: Uint8Array; wrapS?: 'repeat' | 'clamp'; wrapT?: 'repeat' | 'clamp'; alphaTest?: number | null; hasTransparency?: boolean; transparentRatio?: number; alphaBlend?: boolean } {
  return {
    width: tex.header.width,
    height: tex.header.height,
    rgba: tex.rgba,
    wrapS: tex.header.gs?.wrapS,
    wrapT: tex.header.gs?.wrapT,
    alphaTest: tex.header.gs?.alphaTest ?? null,
    hasTransparency: tex.hasTransparency,
    transparentRatio: tex.transparentRatio,
    alphaBlend: tex.header.gs?.alphaBlend ?? false,
  };
}

// Reused canvas so we don't allocate a new <canvas> + 2D context for every
// texture decode. Hundreds of decodes happen during a single sidebar /
// gallery refresh; per-call element allocation was a measurable chunk of
// the lockup the user reported when opening UI bundles.
export let __sharedTexCanvas: HTMLCanvasElement | null = null;

export let __sharedTexCtx: CanvasRenderingContext2D | null = null;

export function textureToDataUrl(tex: ZedTexture): string {
  if (!__sharedTexCanvas) {
    __sharedTexCanvas = document.createElement('canvas');
    __sharedTexCtx = __sharedTexCanvas.getContext('2d', { willReadFrequently: false });
  }
  const canvas = __sharedTexCanvas;
  const ctx = __sharedTexCtx;
  if (!ctx) { return ''; }
  canvas.width = tex.header.width;
  canvas.height = tex.header.height;
  const image = ctx.createImageData(tex.header.width, tex.header.height);
  image.data.set(tex.rgba);
  ctx.putImageData(image, 0, 0);
  return canvas.toDataURL('image/png');
}

// Cap on the sidebar texture quick-grid. The existing weapon/map TXR
// companions have a few dozen textures; beyond 64 we skip the grid so
// large UI archives don't decode hundreds of PNGs synchronously.
export const SIDEBAR_TEXTURE_LIMIT = 64;
