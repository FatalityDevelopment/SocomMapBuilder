// Asset Viewer logic, moved from CodeDesigner's Angular component (the zipper workbench): browse .ZDB / .CDB / .ZAR /
// .ZED packages and preview their textures, models, GEO scenes, RDR screens, AI maps and animations. The page decodes
// archives itself; ViewerHost fetches them from the Map Builder server. The React view (ViewerPage.tsx) renders it.

import { computed, effect, type EffectRef, signal, untracked } from '../../shared';
import { ViewerHost } from './viewer-host';
import { aliasPalettesForTextureEntries, base64ToBytes, parsePaletteNodes, ZedPalette, ZedTexture } from './zed-texture-parser';
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
import { fileMethods, type FileMethods } from './zipper-workbench-files';
import { geoMethods, type GeoMethods } from './zipper-workbench-geo';
import { modelComposeMethods, type ModelComposeMethods } from './zipper-workbench-model-compose';
import { modelRenderMethods, type ModelRenderMethods } from './zipper-workbench-model-render';
import { modelMethods, type ModelMethods } from './zipper-workbench-models';
import { packageMethods, type PackageMethods } from './zipper-workbench-package';
import { paletteMethods, type PaletteMethods } from './zipper-workbench-palettes';
import { rdrMethods, type RdrMethods } from './zipper-workbench-rdr';
import { namespaceFromArchiveName, textureMethods, type TextureMethods } from './zipper-workbench-textures';
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

  layersBeforeEdit: string[] = [];

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
export interface ViewerPageLogic extends TextureMethods, PaletteMethods, ModelMethods, ModelRenderMethods, ModelComposeMethods, GeoMethods, ClutterMethods, RdrMethods, AiMapMethods, AnimationMethods, PackageMethods, FileMethods {}
mixin(ViewerPageLogic, textureMethods, paletteMethods, modelMethods, modelRenderMethods, modelComposeMethods, geoMethods, clutterMethods, rdrMethods, aiMapMethods, animationMethods, packageMethods, fileMethods);
