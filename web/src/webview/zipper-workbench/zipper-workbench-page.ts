// Asset Viewer logic, moved from CodeDesigner's Angular component (the zipper workbench): browse .ZDB / .CDB / .ZAR /
// .ZED packages and preview their textures, models, GEO scenes, RDR screens, AI maps and animations. The page decodes
// archives itself; ViewerHost fetches them from the Map Builder server. The React view (ViewerPage.tsx) renders it.

import { computed, effect, type EffectRef, signal, untracked } from '../../shared';
import { ViewerHost } from './viewer-host';
import { ZedPalette, ZedTexture } from './zed-texture-parser';
import { ChainOffset, PositionUnit, ZedModel } from './zed-model-parser';
import { InspectedZedArchive } from './zed-archive-parser';
import { type ModelCameraView, type ModelTextureMap, ZedModelRenderer } from './zed-model-renderer';
import { AssetClient } from '../model-editor/asset-client';
import { TextureCache } from '../model-editor/model-loader';
import { RdrAnimationClip, RdrAnimationTrack } from './rdr-animation-parser';
import { RdrAnimationCanvasRenderer } from './rdr-animation-renderer';
import { ZanimAnimationSummary, ZanimArchiveSummary } from './zanim-parser';
import { GeoScene } from './zed-geo-parser';
import { MapPoint } from './zed-spawn-points';
import { aiMapMethods, type AiMapMethods } from './zipper-workbench-aimaps';
import { animationMethods, type AnimationMethods } from './zipper-workbench-animations';
import { browserMethods, type BrowserMethods } from './zipper-workbench-browser';
import { clutterMethods, type ClutterMethods } from './zipper-workbench-clutter';
import { fileMethods, type FileMethods } from './zipper-workbench-files';
import { geoMethods, type GeoMethods } from './zipper-workbench-geo';
import { messageMethods, type MessageMethods } from './zipper-workbench-messages';
import { modelComposeMethods, type ModelComposeMethods } from './zipper-workbench-model-compose';
import { modelRenderMethods, type ModelRenderMethods } from './zipper-workbench-model-render';
import { modelMethods, type ModelMethods } from './zipper-workbench-models';
import { packageMethods, type PackageMethods } from './zipper-workbench-package';
import { paletteMethods, type PaletteMethods } from './zipper-workbench-palettes';
import { rdrMethods, type RdrMethods } from './zipper-workbench-rdr';
import { textureMethods, type TextureMethods } from './zipper-workbench-textures';
import type { BrowserItem, ElementRef, MapCameraReference, ModelDecodeInfo, MpsResult, NodePayload, PreviewMode, RdrResult, RdrTreeNode, ZipperEntryInfo, ZipperInspectResult, ZipperOperationResult } from './zipper-workbench-types';
import { mixin, nodeBytes } from './zipper-workbench-util';
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
  inflightNode: number | null = null;
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
  packageBase64Retried = false;
  // Top-level map archives the bulk package payload did not deliver, fetched
  // one by one (entry index -> leaf) and folded back into the package.
  packageFillFetches = new Map<number, string>();
  packageFillBytes = new Map<string, Uint8Array>();
  packageFillRequested = false;
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

  readonly browserItems = computed<BrowserItem[]>(() => this.computeBrowserItems());

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

  private readonly onMessage = (event: MessageEvent): void => this.handleMessage(event);

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

  layersBeforeEdit: string[] = [];

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

  resetPreview(): void {
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

  handleNodeData(fetchedIndex: number, payload: NodePayload): void {
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
export interface ViewerPageLogic extends TextureMethods, PaletteMethods, ModelMethods, ModelRenderMethods, ModelComposeMethods, GeoMethods, ClutterMethods, RdrMethods, AiMapMethods, AnimationMethods, PackageMethods, FileMethods, BrowserMethods, MessageMethods {}
mixin(ViewerPageLogic, textureMethods, paletteMethods, modelMethods, modelRenderMethods, modelComposeMethods, geoMethods, clutterMethods, rdrMethods, aiMapMethods, animationMethods, packageMethods, fileMethods, browserMethods, messageMethods);
