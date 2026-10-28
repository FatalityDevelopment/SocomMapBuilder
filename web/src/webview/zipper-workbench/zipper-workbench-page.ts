// Asset Viewer logic, moved from CodeDesigner's Angular component (the zipper workbench): browse .ZDB / .CDB / .ZAR /
// .ZED packages and preview their textures, models, GEO scenes, RDR screens, AI maps and animations. The page decodes
// archives itself; ViewerHost fetches them from the Map Builder server. The React view (ViewerPage.tsx) renders it.

import { computed, effect, type EffectRef, host, signal, untracked } from '../../shared';
import { ViewerHost } from './viewer-host';
import { aliasPalettesForTextureEntries, base64ToBytes, parsePaletteEntries, parsePaletteNodes, ZedPalette, ZedTexture } from './zed-texture-parser';
import { ChainOffset, PositionUnit, ZedModel } from './zed-model-parser';
import { InspectedZedArchive, parseZedArchive } from './zed-archive-parser';
import { type ModelCameraView, type ModelTextureMap, ZedModelRenderer } from './zed-model-renderer';
import { AssetClient } from '../model-editor/asset-client';
import { TextureCache } from '../model-editor/model-loader';
import { RdrAnimationClip, RdrAnimationTrack } from './rdr-animation-parser';
import { RdrAnimationCanvasRenderer } from './rdr-animation-renderer';
import { ZanimAnimationSummary, ZanimArchiveSummary } from './zanim-parser';
import { GeoEntry, GeoScene, parseGeoScene } from './zed-geo-parser';
import { MapPoint } from './zed-spawn-points';
import { aiMapMethods, type AiMapMethods } from './zipper-workbench-aimaps';
import { animationMethods, type AnimationMethods } from './zipper-workbench-animations';
import { clutterMethods, type ClutterMethods } from './zipper-workbench-clutter';
import { geoMethods, type GeoMethods } from './zipper-workbench-geo';
import { modelComposeMethods, type ModelComposeMethods } from './zipper-workbench-model-compose';
import { modelRenderMethods, type ModelRenderMethods } from './zipper-workbench-model-render';
import { modelMethods, type ModelMethods } from './zipper-workbench-models';
import { paletteMethods, type PaletteMethods } from './zipper-workbench-palettes';
import { rdrMethods, type RdrMethods } from './zipper-workbench-rdr';
import { namespaceFromArchiveName, textureMethods, type TextureMethods } from './zipper-workbench-textures';
import type { BrowserItem, ElementRef, MapCameraReference, ModelDecodeInfo, MpsResult, NodePayload, PreviewMode, RdrResult, RdrTreeNode, ZipperEntryInfo, ZipperInspectResult, ZipperOperationResult } from './zipper-workbench-types';
import { describePayload, escapeRegExp, mixin, nodeBytes, rootEntryLeaf } from './zipper-workbench-util';
export type { ZipperEntryInfo, ZipperSaveRecordInfo, ZipperInspectResult, ZipperOperationResult, PreviewMode, BrowserItemKind, BrowserItem, RdrResult, MpsMap, Bounds3, MpsResult, MapCameraReference, RdrTreeNode } from './zipper-workbench-types';

export class ViewerPageLogic {
  // Set by the view when the canvases mount (Angular's @ViewChild).
  previewCanvas?: ElementRef<HTMLCanvasElement>;
  animationCanvas?: ElementRef<HTMLCanvasElement>;

  // What the VS Code extension did for this page, against the Map Builder server; replies arrive at onMessage.
  readonly vscode = new ViewerHost(msg => this.onMessage({ data: msg } as MessageEvent));
  modelRenderer: ZedModelRenderer | null = null;
  animationRenderer: RdrAnimationCanvasRenderer | null = null;
  nodeCache = new Map<number, Uint8Array>();
  private inflightNode: number | null = null;
  // Maps a fetched node index back to the selected entry index that triggered
  // the fetch (used when .tif markers redirect to the next texdat entry).
  private nodeAliasForFetch = new Map<number, number>();
  palettesRequested = false;
  lastTextureDiagnostic = '';

  readonly result = signal<ZipperInspectResult | null>(null);
  readonly search = signal('');
  readonly selectedIndex = signal<number | null>(null);
  readonly busy = signal(false);
  readonly statusMsg = signal('');
  readonly statusIsError = signal(false);
  readonly lastOperation = signal<ZipperOperationResult | null>(null);

  readonly palettes = signal<Map<string, ZedPalette> | null>(null);
  readonly paletteMessage = signal('');

  // Stack of inline ZED archives we've drilled into. When non-empty, the
  // entry table at the top level shows the deepest sub-archive's entries and
  // node reads come from the in-memory cache, not the bridge.
  readonly archiveStack = signal<Array<{ name: string; archive: InspectedZedArchive; index?: number }>>([]);
  // Companion data: textures from sibling _TXR (name -> ZedTexture record bytes
  // + entry index pair) and the parsed scene from sibling _GEO.
  readonly companionTextures = signal<Map<string, Uint8Array> | null>(null);
  readonly companionScene = signal<GeoScene | null>(null);
  readonly previewMode = signal<PreviewMode>('none');
  readonly texture = signal<ZedTexture | null>(null);
  readonly textureDataUrl = signal('');
  readonly model = signal<ZedModel | null>(null);
  // SOCOM II models the server decodes (ASSET_MODEL_MESH): skinned characters, chain-drawn props and weapons with
  // their palettes resolved, the mesh the Model Editor and the map draw. The in-page decoders stay as the fallback
  // for everything the server does not list (nested archives, effects, other games).
  readonly server = new AssetClient();
  serverPath: string | null = null;
  serverModels: Promise<Map<string, { archive: string; name: string }>> | null = null;
  serverTextures = new TextureCache(this.server);
  serverModel: { model: ZedModel; textures: ModelTextureMap } | null = null;
  readonly previewMessage = signal('');
  readonly wireframe = signal(false);
  readonly flipY = signal(false);
  readonly selectedChunk = signal<number>(-1); // -1 = All chunks
  readonly uvMode = signal<'direct' | 'flipV' | 'swap' | 'swapFlipV'>('direct');
  readonly rdr = signal<RdrResult | null>(null);
  readonly rdrJson = computed(() => {
    const r = this.rdr();
    return r ? JSON.stringify(r.root, null, 2) : '';
  });
  // Tracks which RDR tree paths are currently expanded in the UI. Reset on
  // new RDR load. Use a signal so the template re-renders when toggled.
  readonly rdrExpanded = signal<Set<string>>(new Set());
  // Tree shape derived from the RDR result. Flattened to a depth-first list
  // of nodes that pass the expand filter, ready to render as a row list.
  readonly rdrTreeRoots = computed<RdrTreeNode[]>(() => this.computeRdrTreeRoots());
  readonly rdrVisibleRows = computed<RdrTreeNode[]>(() => this.computeRdrVisibleRows());

  // AIMAPS.MPS preview state.
  readonly mps = signal<MpsResult | null>(null);
  readonly packageMpsResults = signal<MpsResult[]>([]);

  // Texture thumbnails for the RDR fallback view. Resolves each .tif name
  // from the screen's string-scan ref list against the currently loaded
  // companion textures. Only populates when companion textures are available
  // (open the sibling _TXR.ZED first, or use "Load Textures" in the RDR pane).
  // Capped at 24 items so decoding never blocks the UI thread.
  readonly rdrTextureThumbnails = computed<Array<{ name: string; dataUrl: string; width: number; height: number }>>(() => this.computeRdrTextureThumbnails());
  readonly animationClip = signal<RdrAnimationClip | null>(null);
  readonly animationPlaying = signal(true);
  readonly animationFrame = signal(0);
  archiveNodesRequestedFor = '';
  readonly archiveNodesVersion = signal(0);
  readonly zanimSummary = computed<ZanimArchiveSummary | null>(() => this.computeZanimSummary());
  readonly selectedZanimAnimation = computed<ZanimAnimationSummary | null>(() => this.computeSelectedZanimAnimation());
  readonly visibleZanimAnimations = computed<ZanimAnimationSummary[]>(() => this.computeVisibleZanimAnimations());
  readonly animationBoneRows = computed<Array<{ index: number; name: string; clean: string; parent: number; trackType: string; attachment: boolean; depth: number }>>(() => this.computeAnimationBoneRows());
  readonly animationTrackRows = computed<RdrAnimationTrack[]>(() => this.animationClip()?.tracks ?? []);

  // Full ZDB-package state. After the user opens a .zdb, we fetch every
  // top-level entry's bytes in one socket round trip and decode all
  // textures / palettes / GEO across the whole package — mirrors the HTML
  // reference viewers which pre-load all of RUN.zip. The cached entry
  // bytes also let nested archive drill-ins skip the per-entry readNode.
  readonly packageLoading = signal(false);
  readonly packageMessage = signal('');
  // Persistent package state for the status line under the toolbar. The
  // loading banner alone hid failures: its message vanished with it.
  readonly packageState = signal<'idle' | 'loading' | 'loaded' | 'failed'>('idle');
  // Set once per opened file when a binary package arrived undecodable and
  // a base64 copy was requested instead.
  private packageBase64Retried = false;
  // Top-level map archives the bulk package payload did not deliver, fetched
  // one by one (entry index -> leaf) and folded back into the package.
  private packageFillFetches = new Map<number, string>();
  private packageFillBytes = new Map<string, Uint8Array>();
  private packageFillRequested = false;
  // Root TXR/PAL archives already requested on demand for missing textures.
  rootTextureFetchRequested = new Set<string>();
  // Namespaces / archives the texture lookup can currently see.
  readonly textureSourceSummary = computed<{ textures: number; archives: string[] }>(() => {
    const sources = this.companionTextures();
    if (!sources) { return { textures: 0, archives: [] }; }
    let textures = 0;
    const archives = new Set<string>();
    for (const key of sources.keys()) {
      const colon = key.indexOf(':');
      if (colon < 0) { textures++; } else { archives.add(key.slice(0, colon)); }
    }
    return { textures, archives: [...archives].sort() };
  });
  // Lowercased leaf name -> bytes, for the currently-loaded ZDB.
  packageEntryBytes = new Map<string, Uint8Array>();

  // HTML map-viewer style controls. Drive the renderer config and the
  // material-binding signal used in the model parser path.
  readonly mapRenderMode = signal<'view3dTexturedLit' | 'view3dTextured' | 'view3dSolid' | 'topSolid' | 'topWire' | 'view3dWire'>('view3dTexturedLit');
  readonly mapTriStride = signal<number>(1);
  // Default to the DMA chain: the N<node>_<visual> stubs point at chains
  // that name the .tif for every material section and carry the baked
  // vertex colours, which is exactly how the game draws the model. The
  // marker heuristics stay as fallbacks for payloads without chains.
  // WEAP_MDL payloads are forced back to tail-scan in the marker modes:
  // weapon payloads often store helper + primary .tif names in one marker
  // block, and smart marker binding can pick the helper atlas.
  readonly mapMaterialBinding = signal<'chain' | 'tailScan' | 'smartWorld' | 'sliceNearest' | 'globalNearest'>('chain');
  // How int16 vertex positions map to units. 'auto' reads the GEO vparams
  // flags (weapons: int16 / 32768 * chunk scale; props, vehicles and map
  // objects: int16 * scale / 16) and falls back to the scale convention.
  readonly mapPositionUnit = signal<'auto' | PositionUnit>('auto');
  // Baked vertex lighting: GS exact (x1), brightened (x2), or off.
  readonly mapLighting = signal<'baked' | 'bright' | 'off'>('baked');
  // Player / hostage starts and AI waypoints from the package's AIMAPS.MPS.
  readonly mapPoints = signal<MapPoint[]>([]);
  readonly selectedMapPoint = signal<string>('');
  readonly showMapMarkers = signal(true);
  readonly flying = signal(false);
  // Draw the sky dome, sun and lens flare (sky archive SP<map>_MDL, the
  // map's sky / thesun / flare nodes).
  readonly showSky = signal(true);
  // Game units per metre (MP53.ZED MetersPerUnit 0.1 -> 10), for eye height.
  unitsPerMeter = 10;
  // The raw model bytes for the currently-displayed model, kept so we can
  // re-run the binding pass on dropdown change without re-decoding.
  currentModelBytes: Uint8Array | null = null;
  currentSliceOffsets: ChainOffset[] = [];
  currentPositionUnit: PositionUnit = 'scale';
  // How the model in view was decoded, shown in the preview pane so the
  // user does not have to open the log to see what happened.
  readonly modelDecodeInfo = signal<ModelDecodeInfo | null>(null);
  // Visual (N<node>_<visual> stub) to isolate in the renderer; '' = all.
  readonly selectedVisual = signal<string>('');
  readonly availableVisuals = computed<string[]>(() => {
    const mdl = this.model();
    if (!mdl) { return []; }
    const keys = new Set<string>();
    for (const c of mdl.chunks) { if (c.visualKey) { keys.add(c.visualKey); } }
    return [...keys].sort();
  });
  currentModelBindingMode: 'weaponDescriptor' | 'map' | 'tail' = 'tail';
  currentModelSourceArchive: string | null = null;
  readonly mapTextureV = signal<'flipV' | 'direct'>('flipV');
  readonly mapUvInterpretation = signal<'autoWorld' | 'norm4096' | 'pixel16' | 'pixel256'>('autoWorld');
  readonly mapTextureFilter = signal<string>('');
  readonly mapCameraView = signal<ModelCameraView>('auto');
  readonly activeMapCameraReference = computed<MapCameraReference | null>(() => this.computeActiveMapCameraReference());

  // Aggregated thumbnail list for the sidebar texture grid. Built from
  // companionTextures (TXR sibling) plus the current archive's own TXR
  // entries when applicable. Cached to avoid re-decoding on every render.
  //
  // The decode step is bounded to keep the UI responsive when the user
  // loads a large bundle (e.g. SOCOM UI bundle has 200+ textures × ~2
  // namespaced aliases = 400+ map entries). Skipping namespaced duplicates
  // alone halves the work; the SIDEBAR_TEXTURE_LIMIT then caps the
  // synchronous slice at 80 — the rest still go through the regular
  // texture-tab flow when needed.
  // The sidebar's textures follow what is open: the selected model's own textures (as the server decoded them, or
  // the ones its chunks name), else the current archive's namespace (CLIB_MDL → clib:…), else the whole package.
  readonly sidebarTexturesTitle = computed(() => {
    const mdl = this.model();
    const entry = this.selectedEntry();
    if (mdl && entry && this.previewMode() === 'model') { return `Textures of ${entry.name}`; }
    const ns = this.layerNamespace();
    return ns ? `${ns.toUpperCase()} textures` : 'Package textures';
  });

  layerNamespace(): string | null {
    const m = /^(.+?)_(?:MDL|TXR|PAL|GEO)(?:\([^)]+\))?\.ZED$/i.exec(this.currentFileName().split(/[\\/]/).pop() ?? '');
    return m ? m[1].toLowerCase() : null;
  }

  readonly pixelUrls = new Map<ModelTextureMap[string], string>();

  readonly sidebarTextures = computed<Array<{ name: string; dataUrl: string }>>(() => this.computeSidebarTextures());
  // Optional manual palette override key (e.g. "texpal_44"). When set, the
  // model viewer uses this palette instead of whatever the rules / header guess.
  readonly paletteOverride = signal<string | null>(null);
  // List of palette keys available in the current `palettes` signal, for the
  // override dropdown.
  readonly availablePalettes = computed<string[]>(() => {
    const p = this.palettes();
    if (!p) { return []; }
    return [...p.keys()].filter(k => /^texpal_\d+$/i.test(k)).sort((a, b) => {
      const na = Number(/(\d+)/.exec(a)?.[1] ?? 0);
      const nb = Number(/(\d+)/.exec(b)?.[1] ?? 0);
      return na - nb;
    });
  });

  readonly canPreviewTexture = computed(() => {
    const entry = this.selectedEntry();
    return !!entry && /\.tif$/i.test(entry.name);
  });

  readonly canPreviewModel = computed(() => this.model() !== null);

  // Full unfiltered entry list for the current layer. Used internally for
  // texdat / texture lookups (we need to see the Nxxx_xxx stubs to know what
  // belongs to what), and as the base for the user-facing table.
  readonly rawEntries = computed<ZipperEntryInfo[]>(() => {
    const stack = this.archiveStack();
    if (stack.length > 0) {
      const archive = stack[stack.length - 1].archive;
      return archive.entries.map(e => ({
        index: e.index,
        name: e.name,
        path: e.name,
        size: e.size,
        kind: e.size === 0 ? 'node' : 'payload',
        type: e.type,
        extension: e.extension,
      }));
    }
    return this.result()?.entries ?? [];
  });

  // What the user sees in the entry table. For _MDL.ZED files we hide the
  // Nxxx_xxx reference stubs because they're internal pointers into the
  // parent named item, not separate items themselves. The pattern covers both
  // simple form (N000_000) and the extended form (N000_I000_V00) used in
  // CLIB/FLIB/etc.
  readonly currentEntries = computed<ZipperEntryInfo[]>(() => {
    const raw = this.rawEntries();
    if (this.isCurrentFileModelArchive()) {
      return raw.filter(e => !/^N\d+(?:_[A-Z]?\d+)+$/i.test(e.name));
    }
    return raw;
  });

  // File-name detector for the current navigation layer (sub-archive name or
  // root file name). Used to switch UI between table, model preview, or grid.
  currentFileName(): string {
    const stack = this.archiveStack();
    if (stack.length > 0) { return stack[stack.length - 1].name; }
    const p = this.result()?.path ?? '';
    return p.split(/[\\/]/).pop() ?? '';
  }

  currentLayerTitle(): string {
    const stack = this.archiveStack();
    if (stack.length > 0) {
      // Use only the leaf name (strip RUN\COMMON\... path prefixes).
      const full = stack[stack.length - 1].name;
      return full.split(/[\\/]/).pop() ?? full;
    }
    return this.result()?.fileName ?? '';
  }

  currentLayerSummary(): string {
    const stack = this.archiveStack();
    if (stack.length > 0) {
      const archive = stack[stack.length - 1].archive;
      return `Nested archive with ${archive.entries.length} entry(s)`;
    }
    return this.result()?.summary ?? '';
  }

  isCurrentFileModelArchive(): boolean {
    return /_MDL(?:\([^)]+\))?\.ZED$/i.test(this.currentFileName());
  }

  isCurrentFileTextureArchive(): boolean {
    return /_TXR(?:\([^)]+\))?\.ZED$/i.test(this.currentFileName());
  }

  // Pre-order records with child counts → each record's parent (-1 at the top), or null when the layer has no tree.
  readonly entryParents = computed<Map<number, number> | null>(() => {
    const raw = this.rawEntries();
    if (raw.length === 0 || raw.some(e => e.type === undefined)) { return null; }
    const parents = new Map<number, number>();
    let cursor = 0;
    const walk = (parent: number): void => {
      const self = raw[cursor++];
      parents.set(self.index, parent);
      const kids = self.type ?? 0;
      for (let k = 0; k < kids && cursor < raw.length; k++) { walk(self.index); }
    };
    // A bogus child count (a flat package listing) would swallow everything; give up on the tree then.
    try { while (cursor < raw.length) { walk(-1); } } catch { return null; }
    return parents;
  });

  // Records opened as folders inside the current archive (record indices, outermost first): a record holding other
  // records (CLUTTER.ZAR › Clutter › oaktree_facade3 › a_clutter …) is browsed like a folder.
  readonly recordFolders = signal<number[]>([]);

  openFolder(index: number): void {
    this.recordFolders.update(f => [...f, index]);
    this.selectedIndex.set(null);
    this.resetPreview();
  }

  // Up: out of a record folder first, then out of the archive.
  navigateUp(): void {
    if (this.recordFolders().length > 0) {
      this.recordFolders.update(f => f.slice(0, -1));
      this.selectedIndex.set(null);
      this.resetPreview();
      return;
    }
    this.navigateBack();
  }

  navigateToFolder(depth: number): void {
    this.recordFolders.update(f => f.slice(0, depth));
    this.selectedIndex.set(null);
    this.resetPreview();
  }

  folderNames(): string[] {
    const byIndex = new Map(this.rawEntries().map(e => [e.index, e.name]));
    return this.recordFolders().map(i => byIndex.get(i) ?? '?');
  }

  readonly browserItems = computed<BrowserItem[]>(() => {
    const raw = this.rawEntries();
    const file = this.currentFileName();
    const parents = this.entryParents();
    const byIndex = new Map(raw.map(e => [e.index, e]));
    const childCount = (e: ZipperEntryInfo) => e.type ?? 0;
    const childrenOf = (index: number) => raw.filter(e => parents?.get(e.index) === index);
    const leaf = (p: string) => p.split(/[\\/]/).pop() ?? p;
    const folder = (p: string) => { const parts = p.split(/[\\/]/); parts.pop(); return parts.join('\\'); };
    const isArchiveName = (n: string) => /\.(zed|zar|zdb|cdb)$/i.test(n);

    // A package (ZDB/CDB root, or a flat listing): its files.
    if (!parents) {
      return raw.map((e): BrowserItem => ({
        entry: e,
        name: leaf(e.path || e.name),
        kind: isArchiveName(e.name) ? 'archive' : 'file',
        detail: folder(e.path || e.name),
        size: e.size,
      }));
    }
    const top = raw.filter(e => parents.get(e.index) === -1);

    if (/_MDL(?:\([^)]+\))?\.ZED$/i.test(file) || /^WORL_MDL\.ZED$/i.test(file)) {
      const ns = file.split('_')[0].toLowerCase();
      return top.filter(e => e.size > 0 || childCount(e) > 0).map((e): BrowserItem => {
        const visuals = childrenOf(e.index).filter(c => /^N\d+(?:_[A-Z]?\d+)+$/i.test(c.name)).length;
        const character = /^MESH_/i.test(e.name) || ns === 'clib' || ns === 'flib';
        return {
          entry: e,
          name: e.name,
          kind: character ? 'character' : ns === 'weap' ? 'weapon' : 'model',
          detail: character ? 'skinned mesh' : `${visuals} visual${visuals === 1 ? '' : 's'}`,
          size: e.size,
        };
      });
    }
    if (/_TXR(?:\([^)]+\))?\.ZED$/i.test(file)) {
      const thumbs = new Map(this.textureThumbnails().map(t => [t.entryIndex, t]));
      return raw.filter(e => /\.tif$/i.test(e.name)).map((e): BrowserItem => {
        const t = thumbs.get(e.index);
        const data = childrenOf(e.index).find(c => c.name === 'texdat');
        return { entry: e, name: e.name, kind: 'texture', detail: t ? `${t.width}×${t.height}${t.paletteResolved ? '' : ' · no palette'}` : '', size: data?.size ?? e.size };
      });
    }
    if (/_PAL(?:\([^)]+\))?\.ZED$/i.test(file)) {
      return raw.filter(e => /^texpal_\d+$/i.test(e.name)).map((e): BrowserItem => {
        const buf = childrenOf(e.index).find(c => c.name === 'buf');
        return { entry: e, name: e.name, kind: 'palette', detail: buf ? (buf.size >= 1024 ? '256 colours · 32-bit' : '256 colours · 16-bit') : '', size: buf?.size ?? 0 };
      });
    }
    if (/_GEO(?:\([^)]+\))?\.ZED$/i.test(file)) {
      const models = top.find(e => e.name === 'models');
      const defs = models ? childrenOf(models.index) : top;
      return defs.map((e): BrowserItem => ({ entry: e, name: e.name, kind: 'definition', detail: `${childCount(e)} part${childCount(e) === 1 ? '' : 's'}`, size: e.size }));
    }
    // CLUTTER.ZAR: the clutter models (what the Map Builder places), not the record tree.
    const folders = this.recordFolders();
    if (/CLUTTER\.ZAR$/i.test(file) && folders.length === 0) {
      const clutter = top.find(e => e.name === 'Clutter');
      if (clutter) {
        return childrenOf(clutter.index).map((e): BrowserItem => {
          const n = childrenOf(e.index).length;
          return { entry: e, name: e.name, kind: 'clutter', detail: `${n} placement${n === 1 ? '' : 's'}`, size: 0 };
        });
      }
    }
    // Any other archive (ZAR, loose ZED): the records of the open folder (the top level at first). A record that
    // holds records is a folder; nested archives are first-class.
    const level = folders.length ? childrenOf(folders[folders.length - 1]) : top;
    return level.map((e): BrowserItem => ({
      entry: e,
      name: e.name,
      kind: isArchiveName(e.name) ? 'archive' : childCount(e) > 0 && e.size === 0 ? 'folder' : /\.[a-z0-9]{2,4}$/i.test(e.name) ? 'file' : 'record',
      detail: childCount(e) ? `${childCount(e)} item${childCount(e) === 1 ? '' : 's'}` : '',
      size: e.size,
    })).filter(item => byIndex.has(item.entry.index));
  });

  readonly filteredItems = computed(() => {
    const q = this.search().trim().toLowerCase();
    const items = this.browserItems();
    return q ? items.filter(i => `${i.name} ${i.kind} ${i.detail}`.toLowerCase().includes(q)) : items;
  });

  // The swatches of the selected palette (palette preview).
  readonly selectedPalette = computed<ZedPalette | null>(() => {
    const entry = this.selectedEntry();
    if (!entry || !/^texpal_\d+$/i.test(entry.name)) { return null; }
    return this.palettes()?.get(entry.name.toLowerCase()) ?? null;
  });

  // Double-click: a nested archive is entered once its bytes are in; anything else is just selected.
  enterWhenReady: number | null = null;
  openItem(item: BrowserItem): void {
    if (item.kind === 'folder' || item.kind === 'clutter') { this.openFolder(item.entry.index); return; }
    const pending = this.pendingArchiveEntry();
    if (pending && pending.entryIndex === item.entry.index) { this.enterPendingArchive(); return; }
    this.enterWhenReady = item.kind === 'archive' ? item.entry.index : null;
    this.selectEntry(item.entry);
  }

  readonly filteredEntries = computed(() => {
    const current = this.currentEntries();
    const q = this.search().trim().toLowerCase();
    if (!q) { return current; }
    return current.filter(entry =>
      `${entry.name} ${entry.path} ${entry.kind} ${entry.extension ?? ''}`.toLowerCase().includes(q));
  });

  readonly selectedEntry = computed(() => {
    const idx = this.selectedIndex();
    if (idx === null) { return null; }
    const entries = this.currentEntries();
    return entries.find(e => e.index === idx) ?? null;
  });

  // Becomes true when the selected entry's bytes are themselves a ZED-format
  // archive (ZED or ZAR). We park here instead of auto-drilling so the user
  // can keep exploring the parent archive's other entries first.
  readonly pendingArchiveEntry = signal<{ entryIndex: number; entryName: string; size: number } | null>(null);

  readonly lastRevealPath = computed(() => {
    const op = this.lastOperation();
    return op?.outputDirectory ?? op?.outputPath ?? op?.writtenFiles?.[0] ?? '';
  });

  readonly roleLabel = computed(() => {
    switch (this.result()?.role) {
      case 'apache-patch-archive': return 'Apache Patch';
      case 'structured-container': return 'Structure';
      case 'game-save': return 'Game Save';
      case 'archive': return 'Archive';
      default: return this.result()?.role || '-';
    }
  });

  private readonly onMessage = (event: MessageEvent): void => {
    const data = event.data as { type: string; [key: string]: unknown };
    switch (data.type) {
      case 'zipperStatus':
        this.setStatus((data['message'] as string) ?? '', false, !!data['busy']);
        break;
      case 'packageEdited':
        void this.restoreLayers(this.layersBeforeEdit, (data['message'] as string) ?? '');
        break;
      case 'zipperError':
        this.setStatus((data['message'] as string) ?? 'Zipper operation failed', true, false);
        break;
      case 'zdbPackageLoading':
        this.packageLoading.set(true);
        this.packageMessage.set((data['message'] as string) ?? 'Loading package...');
        break;
      case 'zipperLoaded':
        this.result.set((data['result'] as ZipperInspectResult) ?? null);
        this.selectedIndex.set(null);
        this.lastOperation.set(null);
        this.nodeCache.clear();
        this.archiveNodesRequestedFor = '';
        this.archiveNodesVersion.update(v => v + 1);
        this.palettes.set(null);
        this.palettesRequested = false;
        this.archiveStack.set([]);
        this.packageEntryBytes.clear();
        this.packageMpsResults.set([]);
        this.directCompanionTextureNamespaces.clear();
        this.companionTextures.set(null);
        this.companionScene.set(null);
        this.packageState.set('idle');
        this.packageBase64Retried = false;
        this.packageFillRequested = false;
        this.packageFillFetches.clear();
        this.packageFillBytes.clear();
        this.mapPoints.set([]);
        this.selectedMapPoint.set('');
        this.rootTextureFetchRequested.clear();
        this.resetPreview();
        this.setStatus(this.result()?.summary ?? 'File loaded.', false, false);
        this.requestPalettesIfNeeded();
        // For ZDB packages, kick off the full-package preload so the model
        // viewer never has to chase per-entry round-trips later.
        this.requestZdbPackageIfNeeded();
        this.requestArchiveNodesIfNeeded();
        this.selectInitialZanimEntryIfNeeded();
        break;
      case 'zdbPackage': {
        const nodes = (data['nodes'] as Array<{ index: number; name: string; type: number; data: NodePayload }>) ?? [];
        const fromCache = !!data['fromCache'];
        if (fromCache) {
          this.log(`[package] restored from extension cache (${nodes.length} entries)`);
        }
        this.applyZdbPackage(nodes);
        break;
      }
      case 'zdbPackageError': {
        const message = (data['message'] as string) ?? 'unknown';
        this.packageLoading.set(false);
        this.packageState.set('failed');
        this.packageMessage.set('Package load failed: ' + message);
        this.log(`[package] ERROR: ${message}`, true);
        // Without the package, a model in view can still get its textures
        // from the root archives on demand.
        this.applyModelToRenderer();
        break;
      }
      case 'archiveNodes': {
        const nodes = (data['nodes'] as Array<{ index: number; name: string; type: number; data: NodePayload }>) ?? [];
        for (const node of nodes) {
          this.nodeCache.set(node.index, nodeBytes(node.data));
        }
        this.archiveNodesVersion.update(v => v + 1);
        this.setStatus(`Loaded ${nodes.length} archive node payload(s).`, false, false);
        if (this.previewMode() === 'zanim') {
          this.previewMessage.set('');
        }
        break;
      }
      case 'archiveNodesError': {
        const message = (data['message'] as string) ?? 'unknown';
        this.setStatus(`Archive node preload failed: ${message}`, true, false);
        if (this.previewMode() === 'zanim') {
          this.previewMessage.set('Archive node preload failed: ' + message);
        }
        break;
      }
      case 'zipperOperation':
        this.lastOperation.set((data['result'] as ZipperOperationResult) ?? null);
        this.setStatus((data['message'] as string) ?? 'Operation complete.', false, false);
        break;
      case 'nodeData': {
        const fetchedIndex = (data['entryIndex'] as number) ?? -1;
        const dataStr = (data['data'] as NodePayload) ?? '';
        // Intercept palette fetches that came from loadNestedPalettesForCurrentLayer.
        if (this.pendingPaletteFetch && this.pendingPaletteFetch.rootEntryIndex === fetchedIndex) {
          const pending = this.pendingPaletteFetch;
          this.pendingPaletteFetch = null;
          try {
            const palBytes = nodeBytes(dataStr);
            const stack = this.archiveStack();
            if (stack.length > 0) {
              this.applyPaletteBytesForCurrentLayer(palBytes, pending.palName, stack[stack.length - 1].archive);
            }
          } catch { /* ignore decode errors */ }
          break;
        }
        // Root archives the package payload did not deliver.
        if (this.packageFillFetches.has(fetchedIndex)) {
          this.applyPackageFill(fetchedIndex, dataStr);
          break;
        }
        // Intercept root-companion (TXR/PAL/GEO) fetches for a nested MDL.
        const compKind = this.rootCompanionFetches.get(fetchedIndex);
        if (compKind) {
          this.rootCompanionFetches.delete(fetchedIndex);
          try {
            const bytes = nodeBytes(dataStr);
            const entry = (this.result()?.entries ?? []).find(e => e.index === fetchedIndex);
            this.applyMdlCompanion(compKind, bytes, entry?.name ?? '(unknown)');
          } catch { /* ignore decode errors */ }
          break;
        }
        this.handleNodeData(fetchedIndex, dataStr);
        break;
      }
      case 'nodeError': {
        const failedIndex = (data['entryIndex'] as number) ?? -1;
        const msg = (data['message'] as string) ?? 'Failed to read entry.';
        // Was it a companion fetch?
        const compKind = this.rootCompanionFetches.get(failedIndex);
        if (compKind) {
          this.rootCompanionFetches.delete(failedIndex);
          this.setStatus(`Companion ${compKind.toUpperCase()} fetch failed: ${msg}`, true, false);
        } else if (this.pendingPaletteFetch?.rootEntryIndex === failedIndex) {
          this.pendingPaletteFetch = null;
          this.setStatus(`Palette fetch failed: ${msg}`, true, false);
        } else {
          this.previewMessage.set(msg);
        }
        this.inflightNode = null;
        break;
      }
      case 'paletteFile':
        if ((data['found'] as boolean) && data['nodes']) {
          const palettes = parsePaletteNodes(data['nodes'] as Array<{ index: number; name: string; data: string }>);
          this.palettes.set(palettes);
          this.paletteMessage.set(`Loaded ${palettes.size} palette(s).`);
          this.refreshPreview();
        } else {
          this.paletteMessage.set((data['message'] as string) ?? 'No sibling _PAL.ZED found.');
        }
        break;
      case 'rdrData': {
        const result = data['result'] as RdrResult | undefined;
        this.rdr.set(result ?? null);
        // Reset expand state so the new tree opens fresh.
        this.rdrExpanded.set(new Set());
        if (result) {
          if (result.fallback) {
            this.previewMessage.set(
              `RDR strict parse failed (${result.parseError ?? 'unknown'}); showing printable-string fallback.`,
            );
            this.log(`[RDR] strict parse failed — fallback string scan. ${result.parseError ?? ''}`);
          } else {
            this.previewMessage.set(
              `RDR v${result.version}: ${result.atomCount} atoms, ${result.stringPoolSize}B strings.`,
            );
            this.log(`[RDR] v${result.version}, atoms=${result.atomCount}, pool=${result.stringPoolSize}B`);
          }
        }
        break;
      }
      case 'rdrError':
        this.previewMessage.set('RDR parse failed: ' + ((data['message'] as string) ?? 'unknown'));
        this.log(`[RDR] ERROR: ${(data['message'] as string) ?? 'unknown'}`);
        break;
      case 'mpsData': {
        const result = data['result'] as MpsResult | undefined;
        this.mps.set(result ?? null);
        if (result) {
          this.previewMessage.set(
            `MPS: ${result.foundCount} / ${result.declaredCount} AI maps found.`,
          );
          this.log(`[MPS] declared=${result.declaredCount} found=${result.foundCount} (${result.fileSize}B)`);
        }
        break;
      }
      case 'mpsError':
        this.previewMessage.set('MPS parse failed: ' + ((data['message'] as string) ?? 'unknown'));
        this.log(`[MPS] ERROR: ${(data['message'] as string) ?? 'unknown'}`);
        break;
      case 'companionFiles': {
        const companions = (data['companions'] as Record<string, { path: string; nodes: Array<{ index: number; name: string; type: number; data: string }> }>) ?? {};
        this.directCompanionTextureNamespaces.clear();
        // PAL. If TXR is also available, add exact aliases from TXR texture ids
        // to the real texpal_NNN records, skipping RGBA32 textures that do not
        // consume a palette.
        if (companions['pal']) {
          let palettes = parsePaletteNodes(companions['pal'].nodes);
          if (companions['txr']) {
            const txrNodes = companions['txr'].nodes;
            palettes = aliasPalettesForTextureEntries(
              palettes,
              txrNodes.map(n => ({ name: n.name })),
              i => base64ToBytes(txrNodes[i].data),
            );
          }
          const namespace = namespaceFromArchiveName(companions['pal'].path) ?? namespaceFromArchiveName(companions['txr']?.path);
          if (namespace) {
            const namespaced = new Map<string, ZedPalette>(palettes);
            for (const [key, value] of palettes) {
              namespaced.set(`${namespace}:${key}`, value);
            }
            palettes = namespaced;
          }
          this.palettes.set(palettes);
        }
        // TXR -> .tif name to texdat bytes map
        if (companions['txr']) {
          const map = new Map<string, Uint8Array>();
          const nodes = companions['txr'].nodes;
          const namespace = namespaceFromArchiveName(companions['txr'].path);
          if (namespace) { this.directCompanionTextureNamespaces.add(namespace); }
          for (let i = 0; i < nodes.length; i++) {
            if (/\.tif$/i.test(nodes[i].name)) {
              for (let j = i + 1; j < Math.min(i + 4, nodes.length); j++) {
                if (/^texdat$/i.test(nodes[j].name)) {
                  const key = nodes[i].name.toLowerCase();
                  const bytes = base64ToBytes(nodes[j].data);
                  map.set(key, bytes);
                  if (namespace) { map.set(`${namespace}:${key}`, bytes); }
                  break;
                }
                if (/\.tif$/i.test(nodes[j].name)) { break; }
              }
            }
          }
          this.companionTextures.set(map);
        }
        // GEO -> scene tree
        if (companions['geo']) {
          const scene = parseGeoScene(companions['geo'].nodes as GeoEntry[]);
          this.companionScene.set(scene);
        }
        this.refreshPreview();
        break;
      }
    }
  };

  private readonly selectionEffect: EffectRef;
  private lastSelectedEntry: ZipperEntryInfo | null | undefined = undefined;

  constructor() {
    // Only a change of selectedEntry starts a preview (writes inside beginPreviewForEntry must not re-trigger it).
    this.selectionEffect = effect(() => {
      const entry = this.selectedEntry();
      if (entry === this.lastSelectedEntry) { return; }
      this.lastSelectedEntry = entry;
      untracked(() => {
        if (!entry) { this.resetPreview(); return; }
        this.beginPreviewForEntry(entry.index, entry.name);
      });
    });
  }

  // Called once the view is mounted (Angular's ngOnInit).
  init(): void {
    this.vscode.postMessage({ type: 'ready' });
  }

  dispose(): void {
    this.selectionEffect.destroy();
    this.modelRenderer?.dispose();
    this.modelRenderer = null;
    this.animationRenderer?.dispose();
    this.animationRenderer = null;
  }

  // ------------------------------------------------------------------ context menu & package files

  // The file the Viewer opened (a .ZDB / .CDB package, or a loose .ZED / .ZAR).
  rootPath(): string { return this.result()?.path ?? ''; }
  private rootIsPackage(): boolean { return /\.(zdb|cdb)$/i.test(this.rootPath()); }

  // Where an edit of the current layer goes: [root entry path, …nested records] in a package; only the records in a
  // loose ZED / ZAR. Null at the package root (its files are package entries, not archive records).
  private layerChain(): unknown[] | null {
    const stack = this.archiveStack();
    if (stack.length === 0) { return this.rootIsPackage() ? null : []; }
    const chain: unknown[] = [];
    stack.forEach((layer, i) => {
      if (i === 0 && this.rootIsPackage()) {
        const entry = this.result()?.entries.find(e => e.index === layer.index);
        chain.push(entry?.path || layer.name);
      } else {
        chain.push({ name: layer.name.split(/[\\/]/).pop(), index: layer.index });
      }
    });
    return chain;
  }

  // Model / texture / palette / GEO archives are edited in the editors; files are added and removed elsewhere.
  private layerIsAssetArchive(): boolean {
    return /_(MDL|TXR|PAL|GEO)(?:\([^)]+\))?\.ZED$/i.test(this.currentFileName().split(/[\\/]/).pop() ?? '');
  }

  // Whether the current layer's files can be added to / replaced / removed.
  canEditFiles(): boolean {
    if (!this.result() || this.busy()) { return false; }
    if (this.archiveStack().length === 0) { return true; }
    return !this.layerIsAssetArchive();
  }

  private layersBeforeEdit: string[] = [];

  private packageEdit(label: string, ops: unknown[]): void {
    this.layersBeforeEdit = this.archiveStack().map(l => l.name);
    this.vscode.postMessage({ type: 'packageEdit', path: this.rootPath(), ops, label });
  }

  // After an edit reopens the package: back into the archive the edit was made in (by name, layer by layer).
  private async restoreLayers(names: string[], message: string): Promise<void> {
    for (const name of names) {
      const depth = this.archiveStack().length;
      const entry = this.rawEntries().find(e => e.name === name);
      if (!entry) { break; }
      this.enterWhenReady = entry.index;
      this.selectEntry(entry);
      for (let t = 0; t < 100 && this.archiveStack().length === depth; t++) { await new Promise(r => setTimeout(r, 50)); }
      if (this.archiveStack().length === depth) { break; }
    }
    if (message) { this.setStatus(message, false, false); }
  }

  // Import: package files (.ZED / .ZAR / .MPS, or every file of another .ZDB / .CDB) at the package root; any file
  // inside a ZAR / ZED.
  async importFiles(): Promise<void> {
    if (!this.canEditFiles()) { return; }
    const chain = this.layerChain();
    const atRoot = chain === null;
    const file = await host.browse({
      mode: 'open',
      title: atRoot ? 'Import into the package' : `Import into ${this.currentLayerTitle()}`,
      filters: atRoot
        ? [{ label: 'Package files', extensions: ['zed', 'zar', 'mps'] }, { label: 'Another package (merge its files)', extensions: ['zdb', 'cdb'] }]
        : [{ label: 'RDR, ZED and ZAR files', extensions: ['rdr', 'zed', 'zar'] }],
      key: 'viewer-import',
      submitLabel: 'Import',
    });
    if (!file) { return; }
    const leaf = file.split(/[\\/]/).pop() ?? file;
    if (!atRoot) {
      const existing = this.browserItems().find(i => i.name.toLowerCase() === leaf.toLowerCase());
      if (existing) {
        if (!await this.confirm(`${this.currentLayerTitle()} already has ${leaf}`, 'Replace it')) { return; }
        this.packageEdit(`Replace ${leaf}`, [{ op: 'replace', inside: chain, index: existing.entry.index, name: existing.entry.name, file }]);
        return;
      }
      this.packageEdit(`Import ${leaf}`, [{ op: 'add', inside: chain, name: leaf, file }]);
      return;
    }
    if (/\.(zdb|cdb)$/i.test(leaf)) {
      if (!await this.confirm(`Merge every .ZED / .ZAR / .MPS of ${leaf} into this package? Files with the same path are replaced.`, 'Merge')) { return; }
      this.packageEdit(`Merge ${leaf}`, [{ op: 'merge', file }]);
      return;
    }
    const entries = this.result()?.entries ?? [];
    const same = entries.find(e => (e.path || e.name).split(/[\\/]/).pop()?.toLowerCase() === leaf.toLowerCase());
    if (same) {
      if (!await this.confirm(`The package already has ${same.path || same.name}`, 'Replace it')) { return; }
      this.packageEdit(`Replace ${leaf}`, [{ op: 'replace', path: same.path || same.name, file }]);
      return;
    }
    const v = await host.prompt(`Import ${leaf}`, [
      { key: 'path', label: 'Path in the package', value: this.suggestFolder(leaf) + leaf, pattern: /\.(zed|zar|mps)$/i, hint: 'A package path ending in the file name, e.g. RUN\\SP\\M51\\M51_EXTRA.ZED' },
    ], 'Import');
    if (!v) { return; }
    this.packageEdit(`Import ${leaf}`, [{ op: 'add', path: v['path'].trim(), file }]);
  }

  // The folder a new package file most likely belongs in: beside the files of its namespace (M51_… → RUN\SP\M51\),
  // else the folder most files are in.
  private suggestFolder(leaf: string): string {
    const entries = (this.result()?.entries ?? []).map(e => e.path || e.name);
    const folderOf = (p: string) => p.replace(/[^\\/]+$/, '');
    const ns = /^([^_]+)_/.exec(leaf)?.[1]?.toLowerCase();
    const sibling = ns ? entries.find(p => (p.split(/[\\/]/).pop() ?? '').toLowerCase().startsWith(ns + '_')) : undefined;
    if (sibling) { return folderOf(sibling); }
    const counts = new Map<string, number>();
    for (const p of entries) { const f = folderOf(p); counts.set(f, (counts.get(f) ?? 0) + 1); }
    return [...counts].sort((a, b) => b[1] - a[1])[0]?.[0] ?? '';
  }

  async replaceItem(item: BrowserItem): Promise<void> {
    if (!this.canEditFiles()) { return; }
    const chain = this.layerChain();
    const ext = (/\.([^.\\/]+)$/.exec(item.name)?.[1] ?? '').toLowerCase();
    const file = await host.browse({ mode: 'open', title: `Replace ${item.name} with`, filters: ext ? [{ label: `.${ext.toUpperCase()} files`, extensions: [ext] }] : [], key: 'viewer-import', submitLabel: 'Replace' });
    if (!file) { return; }
    this.packageEdit(`Replace ${item.name}`, chain === null
      ? [{ op: 'replace', path: item.entry.path || item.entry.name, file }]
      : [{ op: 'replace', inside: chain, index: item.entry.index, name: item.entry.name, file }]);
  }

  async removeItem(item: BrowserItem): Promise<void> {
    if (!this.canEditFiles()) { return; }
    if (!await this.confirm(`Remove ${item.name} from ${this.currentLayerTitle()}?`, 'Remove')) { return; }
    const chain = this.layerChain();
    this.packageEdit(`Remove ${item.name}`, chain === null
      ? [{ op: 'remove', path: item.entry.path || item.entry.name }]
      : [{ op: 'remove', inside: chain, index: item.entry.index, name: item.entry.name }]);
  }

  private async confirm(title: string, submit: string): Promise<boolean> {
    return (await host.prompt(title, [], submit)) !== null;
  }

  // Which editor an item opens in, from the package the Viewer has open.
  hasMap(): boolean {
    return (this.result()?.entries ?? []).some(e => /(^|[\\/])(WORL_MDL\.ZED|CLUTTER\.ZAR)$/i.test(e.path || e.name));
  }

  openInMap(): void { host.open('map', this.rootPath()); }

  openFile(): void {
    this.vscode.postMessage({ type: 'openFile' });
  }

  // Force re-extract of the loaded package. Used after the source file
  // changes on disk or to recover from a state-loss bug.
  reloadPackage(): void {
    const filePath = this.result()?.path;
    if (!filePath) { return; }
    this.packageEntryBytes.clear();
    this.packageMpsResults.set([]);
    this.companionTextures.set(null);
    this.companionScene.set(null);
    this.palettes.set(null);
    this.rootTextureFetchRequested.clear();
    this.packageFillRequested = false;
    this.packageFillFetches.clear();
    this.packageFillBytes.clear();
    this.packageLoading.set(true);
    this.packageState.set('loading');
    this.packageMessage.set(`Force-reloading ${filePath.split(/[\\/]/).pop()}…`);
    this.log(`[package] force-reloading ${filePath.split(/[\\/]/).pop()}`);
    this.vscode.postMessage({ type: 'reloadZdbPackage', path: filePath });
  }

  refresh(): void {
    const file = this.result()?.path;
    if (file) { this.vscode.postMessage({ type: 'refresh', path: file }); }
  }

  extractAll(): void {
    const file = this.result()?.path;
    if (file) { this.vscode.postMessage({ type: 'extractAll', path: file }); }
  }

  extractEntry(entry: ZipperEntryInfo): void {
    const file = this.result()?.path;
    if (!file) { return; }
    this.vscode.postMessage({
      type: 'extractEntry',
      path: file,
      entryIndex: entry.index,
      entryName: entry.name || entry.path,
    });
  }

  saveExportDecrypted(): void {
    const file = this.result()?.path;
    if (file) { this.vscode.postMessage({ type: 'saveExportDecrypted', path: file }); }
  }

  saveCraft(mode: 'active' | 'overflow' | 'extreme' | 'all'): void {
    const file = this.result()?.path;
    if (file) { this.vscode.postMessage({ type: 'saveCraft', path: file, mode }); }
  }

  apacheDecrypt(section: 'ftscore' | 'zsealetc'): void {
    const file = this.result()?.path;
    if (file) { this.vscode.postMessage({ type: 'apacheDecryptSection', path: file, section }); }
  }

  apacheBuildPatch(): void {
    const file = this.result()?.path;
    if (file) { this.vscode.postMessage({ type: 'apacheBuildPatch', path: file }); }
  }

  revealLast(): void {
    const target = this.lastRevealPath();
    if (target) { this.vscode.postMessage({ type: 'revealPath', path: target }); }
  }

  selectEntry(entry: ZipperEntryInfo): void {
    this.selectedIndex.set(entry.index);
  }

  selectThumbnail(entryIndex: number): void {
    this.selectedIndex.set(entryIndex);
  }

  navigateBack(): void {
    this.recordFolders.set([]);
    this.archiveStack.update(s => s.slice(0, -1));
    this.selectedIndex.set(null);
    this.resetPreview();
  }

  navigateRoot(): void {
    this.recordFolders.set([]);
    this.archiveStack.set([]);
    this.selectedIndex.set(null);
    this.resetPreview();
  }

  navigateToDepth(depth: number): void {
    this.recordFolders.set([]);
    this.archiveStack.update(s => s.slice(0, depth));
    this.selectedIndex.set(null);
    this.resetPreview();
  }

  rootBreadcrumb(): string {
    const p = this.result()?.path ?? '';
    const file = p.split(/[\\/]/).pop() ?? p;
    return file || 'Root';
  }

  onSearch(event: Event): void {
    this.search.set((event.target as HTMLInputElement).value);
  }

  isApache(): boolean {
    return this.result()?.role === 'apache-patch-archive';
  }

  isSave(): boolean {
    return this.result()?.role === 'game-save';
  }

  saveRecordCount(): number {
    return this.result()?.save?.records?.length ?? 0;
  }

  formatBytes(bytes: number): string {
    if (!Number.isFinite(bytes) || bytes <= 0) { return '0 B'; }
    if (bytes < 1024) { return `${bytes} B`; }
    if (bytes < 1024 * 1024) { return `${(bytes / 1024).toFixed(1)} KB`; }
    return `${(bytes / (1024 * 1024)).toFixed(2)} MB`;
  }

  hex(value: number | undefined, width = 8): string {
    if (value === undefined || value === null || !Number.isFinite(value)) { return '-'; }
    return '0x' + (value >>> 0).toString(16).toUpperCase().padStart(width, '0');
  }

  setStatus(message: string, isError: boolean, busy: boolean): void {
    this.statusMsg.set(message);
    this.statusIsError.set(isError);
    this.busy.set(busy);
  }

  setPreviewMode(mode: PreviewMode): void {
    if (mode === 'model' && !this.canPreviewModel()) { return; }
    if (mode === 'texture' && !this.canPreviewTexture()) { return; }
    if (mode === 'animation' && !this.animationClip()) { return; }
    this.previewMode.set(mode);
    if (mode === 'model') { this.ensureModelRenderer(); }
    if (mode === 'animation') { this.ensureAnimationRenderer(); }
  }

  private resetPreview(): void {
    this.previewMode.set('none');
    this.texture.set(null);
    this.textureDataUrl.set('');
    this.model.set(null);
    this.rdr.set(null);
    this.mps.set(null);
    this.animationClip.set(null);
    this.animationFrame.set(0);
    this.animationPlaying.set(true);
    this.previewMessage.set('');
    this.wireframe.set(false);
    this.selectedChunk.set(-1);
    this.selectedVisual.set('');
    this.modelDecodeInfo.set(null);
    this.paletteOverride.set(null);
    this.pendingArchiveEntry.set(null);
    this.modelRenderer?.setModel(null);
    this.animationRenderer?.setClip(null);
  }

  enterPendingArchive(): void {
    const pending = this.pendingArchiveEntry();
    if (!pending) { return; }
    const bytes = this.nodeCache.get(pending.entryIndex);
    if (!bytes) { return; }
    try {
      const archive = parseZedArchive(bytes);
      this.archiveStack.update(s => [...s, { name: pending.entryName, archive, index: pending.entryIndex }]);
      this.recordFolders.set([]);
      this.selectedIndex.set(null);
      this.pendingArchiveEntry.set(null);
      this.resetPreview();
      this.setStatus(`Entered ${pending.entryName} (${archive.entries.length} entries).`, false, false);
      this.loadNestedPalettesForCurrentLayer();
      // If the sub-archive is a *_MDL.ZED, pull TXR/PAL/GEO companions from
      // the parent (root ZDB or another sub-archive) so model chunks can find
      // their textures and palette.
      this.loadMdlCompanionsForCurrentLayer();
    } catch (err) {
      this.previewMessage.set('Nested archive parse failed: ' + (err instanceof Error ? err.message : String(err)));
    }
  }

  private beginPreviewForEntry(index: number, name: string): void {
    const filePath = this.result()?.path;
    if (!filePath) { return; }

    this.resetPreview();
    if (this.zanimSummary()) {
      this.previewMode.set('zanim');
      this.previewMessage.set(this.zanimSummary()?.nodesLoaded ? '' : 'Loading animation payloads...');
      this.requestArchiveNodesIfNeeded();
      return;
    }

    const looksTif = /\.tif$/i.test(name);
    this.previewMode.set(looksTif ? 'texture' : 'model');
    this.previewMessage.set('Loading preview...');

    // For .tif entries, pixel data lives in the next `texdat` entry; the .tif
    // itself is a 0-byte name marker. Pull the data entry instead.
    const targetIndex = looksTif ? this.findTextureDataIndex(index) : index;
    if (targetIndex < 0) {
      this.previewMessage.set('No texdat entry found after this .tif marker.');
      return;
    }

    // If we're inside a nested archive, the bytes are already in memory.
    const stackBytes = this.bytesFromActiveArchive(targetIndex);
    if (stackBytes) {
      this.decodePayload(index, stackBytes);
      return;
    }

    const cached = this.nodeCache.get(targetIndex);
    if (cached) {
      this.decodePayload(index, cached);
      return;
    }

    // When the loaded file is a ZDB, the full package was prefetched on
    // open. Look the target entry up by leaf name and use those bytes
    // instead of a per-entry readNode round trip — this is what makes the
    // model viewer have the same package context as the HTML viewers.
    if (this.archiveStack().length === 0) {
      const entries = this.result()?.entries ?? [];
      const target = entries.find(e => e.index === targetIndex);
      if (target) {
        const leaf = (target.name.split(/[\\/]/).pop() ?? target.name).toLowerCase();
        const pkgBytes = this.packageEntryBytes.get(leaf);
        if (pkgBytes) {
          this.nodeCache.set(targetIndex, pkgBytes);
          this.decodePayload(index, pkgBytes);
          return;
        }
      }
    }

    this.inflightNode = targetIndex;
    this.nodeAliasForFetch.set(targetIndex, index);
    this.vscode.postMessage({ type: 'readNode', path: filePath, entryIndex: targetIndex });
  }

  bytesFromActiveArchive(entryIndex: number): Uint8Array | null {
    const stack = this.archiveStack();
    if (stack.length === 0) { return null; }
    const archive = stack[stack.length - 1].archive;
    return archive.entryData.get(entryIndex) ?? null;
  }

  pendingPaletteFetch: { rootEntryIndex: number; palName: string } | null = null;

  // When user enters a *_MDL.ZED sub-archive, pull the matching TXR / PAL /
  // GEO siblings from the parent layer (in-memory sub-archive or async fetch
  // from the root ZDB). Once bytes arrive, populate companionTextures and
  // palettes so model chunks can resolve their textures by .tif name.
  rootCompanionFetches = new Map<number, 'txr' | 'pal' | 'geo'>();
  rootCompanionTxrArchives = new Map<string, InspectedZedArchive>();
  rootCompanionPalArchives = new Map<string, InspectedZedArchive>();
  companionScenesByNamespace = new Map<string, GeoScene>();
  directCompanionTextureNamespaces = new Set<string>();

  private loadMdlCompanionsForCurrentLayer(): void {
    // Same guard as loadNestedPalettesForCurrentLayer — when the ZDB
    // package preload already wired up companionTextures, palettes, and
    // companionScene with merged cross-prefix data, this per-layer
    // single-prefix loader would CLOBBER that with one prefix's data
    // (e.g. drilling into M51_MDL.ZED inside M51.ZDB would replace the
    // M51+CLIB+FLIB+BENZ+WEAP merged palette map with M51-only). Skip.
    if (this.packageEntryBytes.size > 0) {
      this.log(`[companion] skipping per-layer load — package context already loaded`);
      return;
    }
    // The package is still streaming in: it re-decodes the selected model
    // when it lands, so loading one prefix here would only race it.
    if (this.packageLoading()) {
      this.log(`[companion] package still loading — textures will bind when it arrives`);
      return;
    }

    const stack = this.archiveStack();
    if (stack.length === 0) { return; }
    const current = stack[stack.length - 1];
    const currentLeafName = current.name.split(/[\\/]/).pop() ?? current.name;
    const mdlMatch = /^(.+?)_MDL(?:\([^)]+\))?\.ZED$/i.exec(currentLeafName);
    if (!mdlMatch) { return; }
    const prefix = mdlMatch[1];

    const companionPrefixes = [prefix];
    if (/^effe$/i.test(prefix)) {
      companionPrefixes.push('ALPH');
    }
    const previous = stack.length >= 2 ? stack[stack.length - 2].archive : null;
    const mapPrefix = /^worl$/i.test(prefix) ? this.mapCompanionPrefixForCurrentLayer(previous) : null;
    if (mapPrefix && !companionPrefixes.some(p => p.toLowerCase() === mapPrefix.toLowerCase())) {
      companionPrefixes.push(mapPrefix);
    }
    const targets: Array<{ kind: 'txr' | 'pal' | 'geo'; leafName: string }> = [];
    for (const p of companionPrefixes) {
      targets.push({ kind: 'txr', leafName: `${p}_TXR.ZED`.toLowerCase() });
      targets.push({ kind: 'pal', leafName: `${p}_PAL.ZED`.toLowerCase() });
    }
    targets.push({ kind: 'geo', leafName: `${prefix}_GEO.ZED`.toLowerCase() });
    if (mapPrefix) {
      targets.push({ kind: 'geo', leafName: `${mapPrefix}_GEO.ZED`.toLowerCase() });
    }

    this.rootCompanionFetches.clear();
    this.rootCompanionTxrArchives.clear();
    this.rootCompanionPalArchives.clear();
    this.companionTextures.set(null);
    this.palettes.set(null);

    // Search in-memory sub-archives on the stack first (excluding current).
    if (previous) {
      for (const t of targets) {
        const entry = previous.entries.find(
          e => (e.name.split(/[\\/]/).pop() ?? e.name).toLowerCase() === t.leafName,
        );
        if (!entry) { continue; }
        const bytes = previous.entryData.get(entry.index);
        if (!bytes || bytes.length === 0) { continue; }
        this.applyMdlCompanion(t.kind, bytes, entry.name);
      }
      return;
    }

    // Fall back to the root ZDB. We have to async-fetch each entry's bytes.
    const rootEntries = this.result()?.entries ?? [];
    const filePath = this.result()?.path;
    if (!filePath) { return; }
    const requested: string[] = [];
    const missing: string[] = [];
    for (const t of targets) {
      const entry = rootEntries.find(
        e => (e.name.split(/[\\/]/).pop() ?? e.name).toLowerCase() === t.leafName,
      );
      if (!entry) { missing.push(t.leafName); continue; }
      this.rootCompanionFetches.set(entry.index, t.kind);
      this.vscode.postMessage({ type: 'readNode', path: filePath, entryIndex: entry.index });
      requested.push(t.leafName);
    }
    if (requested.length > 0) {
      this.setStatus(
        `Fetching ${requested.length} companion(s): ${requested.join(', ')}${missing.length ? ` (missing: ${missing.join(', ')})` : ''}`,
        false,
        true,
      );
    } else if (missing.length > 0) {
      this.setStatus(`No companion files found in root for ${prefix} (looked for: ${missing.join(', ')}).`, true, false);
    }
  }

  private applyMdlCompanion(kind: 'txr' | 'pal' | 'geo', bytes: Uint8Array, name: string): void {
    try {
      const archive = parseZedArchive(bytes);
      this.setStatus(`Loaded companion ${kind.toUpperCase()} (${name}, ${archive.entries.length} entries).`, false, false);
      const namespace = namespaceFromArchiveName(name) ?? name.toLowerCase();
      if (kind === 'txr') {
        this.rootCompanionTxrArchives.set(namespace, archive);
        // Build the .tif -> texdat map used by buildTifByteMap.
        const map = new Map<string, Uint8Array>(this.companionTextures() ?? []);
        const entries = archive.entries;
        for (let i = 0; i < entries.length; i++) {
          if (!/\.tif$/i.test(entries[i].name)) { continue; }
          for (let j = i + 1; j < Math.min(i + 4, entries.length); j++) {
            if (/^texdat$/i.test(entries[j].name)) {
              const data = archive.entryData.get(entries[j].index);
              if (data && data.length > 0) {
                const key = entries[i].name.toLowerCase();
                map.set(key, data);
                map.set(`${namespace}:${key}`, data);
              }
              break;
            }
            if (/\.tif$/i.test(entries[j].name)) { break; }
          }
        }
        this.companionTextures.set(map);
      } else if (kind === 'pal') {
        this.rootCompanionPalArchives.set(namespace, archive);
      } else if (kind === 'geo') {
        const entries: GeoEntry[] = archive.entries.map(e => ({
          index: e.index,
          name: e.name,
          type: e.type,
          data: archive.entryData.get(e.index) ?? new Uint8Array(0),
        }));
        const scene = parseGeoScene(entries);
        this.companionScenesByNamespace.set(namespace, scene);
        this.companionScene.set(scene);
      }
      // Re-derive palettes whenever TXR or PAL arrives, since aliasing needs both.
      this.rebuildAliasedPalettes(name);
      // Re-apply textures to the model in view.
      this.applyModelToRenderer();
    } catch { /* skip */ }
  }

  private handleNodeData(fetchedIndex: number, payload: NodePayload): void {
    if (this.inflightNode === fetchedIndex) { this.inflightNode = null; }
    try {
      const bytes = nodeBytes(payload);
      this.nodeCache.set(fetchedIndex, bytes);
      const aliasFor = this.nodeAliasForFetch.get(fetchedIndex) ?? fetchedIndex;
      this.nodeAliasForFetch.delete(fetchedIndex);
      if (this.selectedEntry()?.index === aliasFor) {
        this.decodePayload(aliasFor, bytes);
      }
    } catch (err) {
      this.previewMessage.set(err instanceof Error ? err.message : String(err));
    }
  }

  private mapCompanionPrefixForCurrentLayer(previous: InspectedZedArchive | null): string | null {
    const path = this.result()?.path;
    const leaf = path ? (path.split(/[\\/]/).pop() ?? path).replace(/\.[^.]+$/i, '') : '';
    const supportPrefixes = new Set(['weap', 'flib', 'clib', 'benz', 'objt', 'effe', 'trmg', 'tlrm', 'alph', 'font', 'hud', 'hud2', 'hudw', 'load', 'junk']);
    const hasPair = (prefix: string): boolean => {
      if (this.packageHasArchive(prefix.toLowerCase(), 'txr') && this.packageHasArchive(prefix.toLowerCase(), 'pal')) {
        return true;
      }
      if (!previous) { return false; }
      const txr = `${prefix}_TXR.ZED`.toLowerCase();
      const pal = `${prefix}_PAL.ZED`.toLowerCase();
      return previous.entries.some(e => (e.name.split(/[\\/]/).pop() ?? e.name).toLowerCase() === txr) &&
        previous.entries.some(e => (e.name.split(/[\\/]/).pop() ?? e.name).toLowerCase() === pal);
    };

    if (leaf && hasPair(leaf)) { return leaf; }
    // WORL_MDL entered straight from the ZDB root: the map's own TXR/PAL
    // pair sits next to it in the root entries (M51.ZDB -> M51_TXR/PAL).
    if (!previous) {
      const rootLeaves = (this.result()?.entries ?? []).map(e => (e.name.split(/[\\/]/).pop() ?? e.name).toLowerCase());
      const rootPrefix = mapPrefixFromLeaves(rootLeaves, leaf, supportPrefixes);
      if (rootPrefix) { return rootPrefix; }
    }
    if (previous) {
      const prefixes = new Set<string>();
      for (const entry of previous.entries) {
        const entryLeaf = entry.name.split(/[\\/]/).pop() ?? entry.name;
        const m = /^(.+?)_TXR(?:\([^)]+\))?\.ZED$/i.exec(entryLeaf);
        if (m) { prefixes.add(m[1]); }
      }
      for (const prefix of prefixes) {
        const lower = prefix.toLowerCase();
        if (supportPrefixes.has(lower)) { continue; }
        if (/^ld\d+$/i.test(prefix) || /^z.+\d+$/i.test(prefix)) { continue; }
        if (hasPair(prefix)) { return prefix; }
      }
    }
    return this.primaryMapTextureNamespace();
  }

  packageHasArchive(namespace: string, kind: 'txr' | 'pal' | 'mdl' | 'geo'): boolean {
    const escaped = namespace.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    const rx = new RegExp(`^${escaped}_${kind}(?:\\([^)]+\\))?\\.zed$`, 'i');
    for (const key of this.packageEntryBytes.keys()) {
      if (rx.test(key)) { return true; }
    }
    return false;
  }

  refreshPreview(): void {
    const entry = this.selectedEntry();
    if (!entry) { return; }
    const bytes = this.nodeCache.get(entry.index);
    if (bytes) { this.decodePayload(entry.index, bytes); }
  }

  // Decoded thumbnail grid for the current texture archive. Re-runs when
  // entries / palettes / loaded textures change.
  readonly textureThumbnails = computed<Array<{
    name: string;
    width: number;
    height: number;
    dataUrl: string;
    entryIndex: number;
    paletteResolved: boolean;
  }>>(() => this.computeTextureThumbnails());

  // Kick off a full ZDB package fetch. Mirrors the HTML map viewer's
  // RUN.zip preload: we get every top-level entry's bytes in one round
  // trip so textures / palettes / GEO can be wired up before the user
  // even drills into a sub-archive.
  private requestZdbPackageIfNeeded(): void {
    const filePath = this.result()?.path;
    if (!filePath) { return; }
    const leaf = filePath.split(/[\\/]/).pop() ?? filePath;
    const isZdb = /\.(zdb|cdb)$/i.test(leaf);
    const isLooseMapWorld = /\.zed$/i.test(leaf) && (
      /^worl_mdl(?:\([^)]+\))?\.zed$/i.test(leaf) ||
      !/_(?:MDL|TXR|PAL|GEO)(?:\([^)]+\))?\.ZED$/i.test(leaf)
    );
    if (!isZdb && !isLooseMapWorld) { return; }
    // Skip apache/save ZDBs — they don't carry the sibling-ZED package layout.
    const role = this.result()?.role ?? '';
    if (role === 'apache-patch-archive' || role === 'game-save') { return; }
    this.packageLoading.set(true);
    this.packageState.set('loading');
    this.packageMessage.set(`Loading ${filePath.split(/[\\/]/).pop()} package…`);
    this.log(`[package] requesting full package contents for ${filePath.split(/[\\/]/).pop()}`);
    this.vscode.postMessage({ type: 'loadZdbPackage', path: filePath });
  }

  // Decode every top-level node of the just-loaded ZDB. _TXR / _PAL / _GEO
  // ZEDs are parsed inline so model previews resolve textures cross-archive
  // (the gap that caused 64-missing-textures on M51). Other entries are
  // cached by leaf name so drill-ins don't re-fetch them.
  private applyZdbPackage(nodes: Array<{ index: number; name: string; data: NodePayload }>): void {
    this.packageEntryBytes.clear();
    this.packageMpsResults.set([]);
    this.companionScenesByNamespace.clear();
    this.companionScene.set(null);
    const txrArchives: Array<{ name: string; archive: InspectedZedArchive }> = [];
    const palArchives: Array<{ name: string; archive: InspectedZedArchive }> = [];
    const geoArchives: Array<{ name: string; archive: InspectedZedArchive }> = [];
    // The package lists every node, nested archives' nodes included. Match
    // its top-level files against the ZDB's own entry list by file name and
    // size: a ZDB can mix "RUN\\...\\X.ZED" paths with bare root names
    // (M51), so "has a separator" alone drops most archives, and nested
    // nodes can reuse a root's name (a 4-byte "WORL_MDL.ZED" reference).
    // Without a ZDB entry list (loose map folder) every node is a file.
    const rootKeys = this.packageRootKeys();
    const hasRootPaths = nodes.some(n => /[\\/]/.test(n.name));
    let decodedNodes = 0;
    let undecodableNodes = 0;
    let firstUndecodable = '';

    for (const node of nodes) {
      if (!node.data) { continue; }
      let bytes: Uint8Array;
      try { bytes = nodeBytes(node.data); }
      catch {
        undecodableNodes++;
        if (!firstUndecodable) { firstUndecodable = describePayload(node.data); }
        continue;
      }
      if (bytes.length === 0) { continue; }
      decodedNodes++;
      const leaf = (node.name.split(/[\\/]/).pop() ?? node.name).toLowerCase();
      const rootKey = `${leaf}|${bytes.length}`;
      const isRootPackageEntry = rootKeys
        ? rootKeys.delete(rootKey)
        : !hasRootPaths || /[\\/]/.test(node.name);
      if (!isRootPackageEntry) { continue; }
      this.packageEntryBytes.set(leaf, bytes);

      // Parse each sibling ZED inline. We tolerate parse errors (some
      // entries may not actually be ZED containers — e.g. raw data files).
      const tryParse = (): InspectedZedArchive | null => {
        try { return parseZedArchive(bytes); } catch { return null; }
      };
      if (/_TXR(?:\([^)]+\))?\.zed$/i.test(leaf)) {
        const ar = tryParse(); if (ar) { txrArchives.push({ name: leaf, archive: ar }); }
      } else if (/_PAL(?:\([^)]+\))?\.zed$/i.test(leaf)) {
        const ar = tryParse(); if (ar) { palArchives.push({ name: leaf, archive: ar }); }
      } else if (/_GEO(?:\([^)]+\))?\.zed$/i.test(leaf)) {
        const ar = tryParse(); if (ar) { geoArchives.push({ name: leaf, archive: ar }); }
      }
    }

    if (undecodableNodes > 0) {
      this.log(`[package] ${undecodableNodes} of ${nodes.length} payload(s) could not be decoded (first: ${firstUndecodable})`, true);
    }
    if (rootKeys && rootKeys.size > 0 && decodedNodes > 0) {
      this.requestMissingPackageEntries(rootKeys);
    }
    // A package whose payloads all failed to decode means the binary
    // transfer did not survive the trip to the webview. Ask once for a
    // base64 copy instead of rendering an untextured map.
    if (decodedNodes === 0 && undecodableNodes > 0 && !this.packageBase64Retried) {
      this.packageBase64Retried = true;
      const filePath = this.result()?.path;
      if (filePath) {
        this.packageMessage.set('Package payloads arrived undecodable; requesting a base64 copy…');
        this.log('[package] retrying the package as base64', true);
        this.vscode.postMessage({ type: 'reloadZdbPackage', path: filePath, encoding: 'base64' });
        return;
      }
    }

    const packageMpsResults = this.parsePackageMpsResults();
    this.packageMpsResults.set(packageMpsResults);
    this.loadMapPoints(packageMpsResults);

    // Build the .tif -> texdat map across every TXR archive in the package.
    const tifMap = new Map<string, Uint8Array>();
    for (const { name, archive } of txrArchives) {
      const namespace = namespaceFromArchiveName(name);
      const entries = archive.entries;
      for (let i = 0; i < entries.length; i++) {
        if (!/\.tif$/i.test(entries[i].name)) { continue; }
        // texdat sibling is the next non-tif data entry within a few slots.
        for (let j = i + 1; j < Math.min(i + 4, entries.length); j++) {
          if (/^texdat$/i.test(entries[j].name)) {
            const data = archive.entryData.get(entries[j].index);
            if (data && data.length > 0) {
              const key = entries[i].name.toLowerCase();
              tifMap.set(key, data);
              if (namespace) { tifMap.set(`${namespace}:${key}`, data); }
            }
            break;
          }
          if (/\.tif$/i.test(entries[j].name)) { break; }
        }
      }
    }
    if (tifMap.size > 0) { this.companionTextures.set(tifMap); }

    // Build palettes across every PAL/TXR pair we found. Match by leaf
    // prefix (e.g. M51_TXR.ZED <-> M51_PAL.ZED) so multi-prefix packages
    // alias correctly.
    let mergedPalettes = new Map<string, ZedPalette>();
    for (const pal of palArchives) {
      const prefix = pal.name.replace(/_pal(?:\([^)]+\))?\.zed$/i, '');
      const txrMatch = txrArchives.find(t => t.name.startsWith(prefix + '_txr'));
      let p = parsePaletteEntries(
        pal.archive.entries.map(e => ({ name: e.name })),
        i => pal.archive.entryData.get(pal.archive.entries[i].index) ?? new Uint8Array(0),
      );
      if (txrMatch) {
        p = aliasPalettesForTextureEntries(
          p,
          txrMatch.archive.entries.map(e => ({ name: e.name })),
          i => txrMatch.archive.entryData.get(txrMatch.archive.entries[i].index) ?? new Uint8Array(0),
        );
      }
      const namespace = prefix.toLowerCase();
      for (const [k, v] of p) {
        mergedPalettes.set(k, v);
        mergedPalettes.set(`${namespace}:${k}`, v);
      }
    }
    if (mergedPalettes.size > 0) { this.palettes.set(mergedPalettes); }

    // Parse every GEO archive into a namespace lookup. Keep the first one as
    // the legacy companionScene for map/debug tools, but character meshes need
    // their own CLIB/FLIB skeleton scenes even when a full package is loaded.
    for (const geo of geoArchives) {
      try {
        const geoEntries: GeoEntry[] = geo.archive.entries.map(e => ({
          index: e.index,
          name: e.name,
          type: e.type,
          data: geo.archive.entryData.get(e.index) ?? new Uint8Array(0),
        }));
        const scene = parseGeoScene(geoEntries);
        const namespace = namespaceFromArchiveName(geo.name);
        if (namespace) { this.companionScenesByNamespace.set(namespace, scene); }
        if (!this.companionScene()) { this.companionScene.set(scene); }
      } catch { /* try next */ }
    }

    this.packageLoading.set(false);
    this.packageState.set(decodedNodes > 0 ? 'loaded' : 'failed');
    this.packageMessage.set(decodedNodes > 0
      ? `Package: ${txrArchives.length} texture archive(s) (${txrArchives.map(t => t.name.replace(/\.zed$/i, '')).join(', ') || 'none'}), ` +
        `${palArchives.length} palette archive(s), ${geoArchives.length} GEO archive(s).`
      : `Package load failed: none of ${nodes.length} payload(s) could be decoded.`);
    const mdlLeaves = [...this.packageEntryBytes.keys()].filter(k => /_MDL(?:\([^)]+\))?\.zed$/i.test(k));
    this.log(`[package] ${this.packageEntryBytes.size} top-level entries; textures=${tifMap.size} palettes=${mergedPalettes.size} GEO=${geoArchives.length} MDL=${mdlLeaves.length}`);
    if (mdlLeaves.length) {
      this.log(`[package] MDL archives: ${mdlLeaves.join(', ')}`);
    }
    if (packageMpsResults.length) {
      const totalMaps = packageMpsResults.reduce((sum, r) => sum + r.foundCount, 0);
      this.log(`[bounds] package MPS bounds loaded: ${totalMaps} map(s) from ${packageMpsResults.map(r => r.sourcePath.split(/[\\/]/).pop()).join(', ')}`);
    }

    // If the user already clicked a preview before the package finished
    // loading, the initial render had no companion textures. Re-trigger
    // decodePayload from the cached bytes so the freshly-loaded textures
    // flow through the full pipeline (not just the renderer re-bind) —
    // this is what fixes the "have to open views after a fresh open"
    // symptom.
    const entry = this.selectedEntry();
    if (!entry && this.autoEnterLooseMapWorldModel()) {
      return;
    }
    if (entry) {
      const cached = this.nodeCache.get(entry.index)
        ?? this.bytesFromActiveArchive(entry.index);
      if (cached) {
        this.log(`[package] re-decoding ${entry.name} with fresh package context`);
        this.decodePayload(entry.index, cached);
      } else {
        // No cached bytes for the selected entry — at least re-bind the
        // renderer so any model already in `this.model()` picks up the
        // new texture map.
        this.applyModelToRenderer();
      }
    } else {
      this.applyModelToRenderer();
    }
  }

  // "path|size" of the opened ZDB's top-level entries, or null when the
  // open file is not a ZDB (a loose map folder sends plain file names).
  private packageRootKeys(): Set<string> | null {
    const result = this.result();
    if (!result || !/\.(zdb|cdb)$/i.test(result.path ?? '') || result.entries.length === 0) { return null; }
    return new Set(result.entries.map(e => `${rootEntryLeaf(e)}|${e.size}`));
  }

  // Root archives a map preview needs: model, scene, texture, palette, AI
  // map, map config and clutter files.
  private static isPackageArchiveLeaf(leaf: string): boolean {
    return /_(?:mdl|geo|txr|pal)(?:\([^)]+\))?\.zed$/i.test(leaf) || /\.mps$/i.test(leaf) || /^[a-z]+\d+\.zed$/i.test(leaf) || /^clutter\.zar$/i.test(leaf);
  }

  // Fetch the root archives the package payload did not deliver (once per
  // file). When they have all arrived the package is applied again with
  // them, which re-decodes the model in view.
  private requestMissingPackageEntries(unmatched: Set<string>): void {
    if (this.packageFillRequested) { return; }
    const filePath = this.result()?.path;
    if (!filePath) { return; }
    const wanted = (this.result()?.entries ?? []).filter(e => {
      const leaf = rootEntryLeaf(e);
      return e.size > 0 && unmatched.has(`${leaf}|${e.size}`) && ViewerPageLogic.isPackageArchiveLeaf(leaf);
    });
    if (wanted.length === 0) { return; }
    this.packageFillRequested = true;
    this.packageFillFetches.clear();
    this.packageFillBytes.clear();
    for (const e of wanted) {
      this.packageFillFetches.set(e.index, rootEntryLeaf(e));
      this.vscode.postMessage({ type: 'readNode', path: filePath, entryIndex: e.index });
    }
    this.log(`[package] ${wanted.length} root archive(s) missing from the package payload; fetching ${wanted.map(rootEntryLeaf).join(', ')}`, true);
  }

  private applyPackageFill(entryIndex: number, payload: NodePayload): void {
    const leaf = this.packageFillFetches.get(entryIndex);
    if (!leaf) { return; }
    this.packageFillFetches.delete(entryIndex);
    try {
      const bytes = nodeBytes(payload);
      if (bytes.length > 0) { this.packageFillBytes.set(leaf, bytes); }
    } catch { /* skip undecodable */ }
    if (this.packageFillFetches.size > 0) { return; }
    const nodes: Array<{ index: number; name: string; data: NodePayload }> = [];
    for (const [name, bytes] of this.packageEntryBytes) {
      if (!this.packageFillBytes.has(name)) { nodes.push({ index: nodes.length, name, data: bytes }); }
    }
    for (const [name, bytes] of this.packageFillBytes) { nodes.push({ index: nodes.length, name, data: bytes }); }
    this.log(`[package] ${this.packageFillBytes.size} missing root archive(s) loaded; applying the package again`);
    this.packageFillBytes.clear();
    this.applyZdbPackage(nodes);
  }

  private autoEnterLooseMapWorldModel(): boolean {
    const filePath = this.result()?.path ?? '';
    const leaf = filePath.split(/[\\/]/).pop() ?? filePath;
    if (!/\.zed$/i.test(leaf)) { return false; }
    if (/_(?:MDL|TXR|PAL|GEO)(?:\([^)]+\))?\.ZED$/i.test(leaf)) { return false; }

    const bytes = this.packageEntryBytes.get('worl_mdl.zed');
    if (!bytes) { return false; }
    try {
      const archive = parseZedArchive(bytes);
      const world = archive.entries.find(e => e.name.toLowerCase() === 'worldmodel')
        ?? archive.entries.find(e => e.size > 0);
      if (!world) { return false; }
      this.archiveStack.set([{ name: 'WORL_MDL.ZED', archive }]);
      this.selectedIndex.set(world.index);
      this.pendingArchiveEntry.set(null);
      this.setStatus(`Loaded root map world (${archive.entries.length} entries).`, false, false);
      this.log(`[package] auto-entered WORL_MDL.ZED / ${world.name}`);
      return true;
    } catch (err) {
      this.log(`[package] failed to auto-enter WORL_MDL.ZED: ${err instanceof Error ? err.message : err}`);
      return false;
    }
  }

  // ------------------------------------------------------------------
  // Logging — every diagnostic line the UI emits is mirrored to the
  // "Code Designer ZDB" output channel via the extension provider, so the
  // user has a persistent feed like the HTML viewers' on-page log pane.
  // ------------------------------------------------------------------
  log(text: string, reveal = false): void {
    if (!text) { return; }
    const fileTag = this.result()?.fileName ? `[${this.result()?.fileName}] ` : '';
    this.vscode.postMessage({ type: 'zipperLog', text: fileTag + text, reveal });
  }
}

// The methods live in one module per archive kind; each runs with `this` as the page, like the class's own.
export interface ViewerPageLogic extends TextureMethods, PaletteMethods, ModelMethods, ModelRenderMethods, ModelComposeMethods, GeoMethods, ClutterMethods, RdrMethods, AiMapMethods, AnimationMethods {}
mixin(ViewerPageLogic, textureMethods, paletteMethods, modelMethods, modelRenderMethods, modelComposeMethods, geoMethods, clutterMethods, rdrMethods, aiMapMethods, animationMethods);

// The map prefix among root-level archive names: a <prefix>_TXR.ZED with a
// matching _PAL.ZED that is not a shared support library. Prefers the opened
// file's own stem (M51.ZDB -> m51).
function mapPrefixFromLeaves(leaves: string[], preferred: string, supportPrefixes: Set<string>): string | null {
  const set = new Set(leaves);
  const hasPair = (p: string) =>
    [...set].some(l => new RegExp(`^${escapeRegExp(p)}_txr(?:\\([^)]+\\))?\\.zed$`, 'i').test(l)) &&
    [...set].some(l => new RegExp(`^${escapeRegExp(p)}_pal(?:\\([^)]+\\))?\\.zed$`, 'i').test(l));
  const want = preferred.toLowerCase();
  if (want && hasPair(want)) { return want; }
  for (const l of leaves) {
    const m = /^(.+?)_txr(?:\([^)]+\))?\.zed$/i.exec(l);
    if (!m) { continue; }
    const p = m[1].toLowerCase();
    if (supportPrefixes.has(p) || /^ld\d+$/i.test(p) || /^z.+\d+$/i.test(p)) { continue; }
    if (hasPair(p)) { return p; }
  }
  return null;
}
