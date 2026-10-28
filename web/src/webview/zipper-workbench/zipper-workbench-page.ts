// Asset Viewer logic, moved from CodeDesigner's Angular component (the zipper workbench): browse .ZDB / .CDB / .ZAR /
// .ZED packages and preview their textures, models, GEO scenes, RDR screens, AI maps and animations. The page decodes
// archives itself; ViewerHost fetches them from the Map Builder server. The React view (ViewerPage.tsx) renders it.

import { computed, effect, type EffectRef, host, signal, untracked } from '../../shared';
import { ViewerHost } from './viewer-host';
import { aliasPalettesForTextureEntries, base64ToBytes, parsePaletteEntries, parsePaletteNodes, parseTexture, ZedPalette, ZedTexture } from './zed-texture-parser';
import { applyMarkerBinding, applyWeaponDescriptorBinding, ChainOffset, chainsAreLineOnly, convertPositionUnit, decodeCharacterFallbackPayload, decodeFallbackPayload, looksLikeZedContainer, parseModel, PositionUnit, ZedModel } from './zed-model-parser';
import { InspectedZedArchive, parseZedArchive } from './zed-archive-parser';
import { type ModelBounds3, type ModelCameraView, type ModelTextureMap, type SceneMarker, ZedModelRenderer } from './zed-model-renderer';
import { AssetClient } from '../model-editor/asset-client';
import { decodeMesh, type MeshWire } from '../model-editor/mesh-data';
import { TextureCache } from '../model-editor/model-loader';
import { animationFrameRate, cleanBoneName as cleanAnimationBoneName, hex as formatAnimationHex, parseRdrAnimation, RdrAnimationClip, RdrAnimationTrack, trackTypeKey } from './rdr-animation-parser';
import { RdrAnimationCanvasRenderer } from './rdr-animation-renderer';
import { findZanimAnimationForEntry, parseZanimArchive, ZanimAnimationSummary, ZanimArchiveSummary } from './zanim-parser';
import { collectVisualPartNames, findSceneNode, GeoEntry, GeoInstance, GeoNode, GeoScene, instanceNameVariants, parseGeoScene, positionUnitForNode, readGeoTransform, transformPoint } from './zed-geo-parser';
import { choosePositionUnit, placeModelByGeo, type PositionUnitChoice } from '../zipper-core/model-placement';
import { chooseMapScene, collectSceneInstances, definitionNodeFor, mapSceneRoot, parseClutterPlacements, type SceneInstance } from './zed-scene-compose';
import { groundHeight, MapPoint, parseMapPoints } from './zed-spawn-points';
import { isPlaceholderTexture, namespaceFromArchiveName, parseModelTexture, textureBytesFor, textureMethods, type TextureMethods, textureOwnerNamespace, texturePixelsFor, textureToDataUrl } from './zipper-workbench-textures';
import type { Bounds3, BrowserItem, ElementRef, MapCameraReference, ModelDecodeInfo, MpsMap, MpsResult, NodePayload, PreviewMode, RdrResult, RdrTreeNode, ZipperEntryInfo, ZipperInspectResult, ZipperOperationResult } from './zipper-workbench-types';
import { bytesToBase64, describeMagic, describePayload, escapeRegExp, mixin, nodeBytes, rootEntryLeaf } from './zipper-workbench-util';
export type { ZipperEntryInfo, ZipperSaveRecordInfo, ZipperInspectResult, ZipperOperationResult, PreviewMode, BrowserItemKind, BrowserItem, RdrResult, MpsMap, Bounds3, MpsResult, MapCameraReference, RdrTreeNode } from './zipper-workbench-types';

export class ViewerPageLogic {
  // Set by the view when the canvases mount (Angular's @ViewChild).
  previewCanvas?: ElementRef<HTMLCanvasElement>;
  animationCanvas?: ElementRef<HTMLCanvasElement>;

  // What the VS Code extension did for this page, against the Map Builder server; replies arrive at onMessage.
  readonly vscode = new ViewerHost(msg => this.onMessage({ data: msg } as MessageEvent));
  private modelRenderer: ZedModelRenderer | null = null;
  private animationRenderer: RdrAnimationCanvasRenderer | null = null;
  nodeCache = new Map<number, Uint8Array>();
  private inflightNode: number | null = null;
  // Maps a fetched node index back to the selected entry index that triggered
  // the fetch (used when .tif markers redirect to the next texdat entry).
  private nodeAliasForFetch = new Map<number, number>();
  private palettesRequested = false;
  private lastTextureDiagnostic = '';

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
  private serverPath: string | null = null;
  private serverModels: Promise<Map<string, { archive: string; name: string }>> | null = null;
  private serverTextures = new TextureCache(this.server);
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
  readonly rdrTreeRoots = computed<RdrTreeNode[]>(() => {
    const r = this.rdr();
    if (!r || r.fallback) { return []; }
    return this.buildRdrTreeRoots(r.root);
  });
  readonly rdrVisibleRows = computed<RdrTreeNode[]>(() => {
    const roots = this.rdrTreeRoots();
    const expanded = this.rdrExpanded();
    const out: RdrTreeNode[] = [];
    const walk = (nodes: RdrTreeNode[]) => {
      for (const n of nodes) {
        out.push(n);
        if (n.children && expanded.has(n.path)) { walk(n.children); }
      }
    };
    walk(roots);
    return out;
  });

  // AIMAPS.MPS preview state.
  readonly mps = signal<MpsResult | null>(null);
  private readonly packageMpsResults = signal<MpsResult[]>([]);

  // Texture thumbnails for the RDR fallback view. Resolves each .tif name
  // from the screen's string-scan ref list against the currently loaded
  // companion textures. Only populates when companion textures are available
  // (open the sibling _TXR.ZED first, or use "Load Textures" in the RDR pane).
  // Capped at 24 items so decoding never blocks the UI thread.
  readonly rdrTextureThumbnails = computed<Array<{ name: string; dataUrl: string; width: number; height: number }>>(() => {
    const lists = this.rdrFallbackLists();
    if (!lists || lists.textureRefs.length === 0) { return []; }
    const sources = this.companionTextures();
    if (!sources || sources.size === 0) { return []; }
    const palettes = this.palettes();
    const out: Array<{ name: string; dataUrl: string; width: number; height: number }> = [];
    for (const ref of lists.textureRefs.slice(0, 24)) {
      const key = ref.toLowerCase();
      const bytes = sources.get(key);
      if (!bytes) { continue; }
      try {
        const tex = parseTexture(bytes, palettes, key);
        out.push({ name: ref, dataUrl: textureToDataUrl(tex), width: tex.header.width, height: tex.header.height });
      } catch { /* skip undecodable */ }
    }
    return out;
  });
  readonly animationClip = signal<RdrAnimationClip | null>(null);
  readonly animationPlaying = signal(true);
  readonly animationFrame = signal(0);
  private archiveNodesRequestedFor = '';
  readonly archiveNodesVersion = signal(0);
  readonly zanimSummary = computed<ZanimArchiveSummary | null>(() => {
    this.archiveNodesVersion();
    const summary = parseZanimArchive(this.rawEntries(), index => this.nodeCache.get(index));
    return summary.isZanim ? summary : null;
  });
  readonly selectedZanimAnimation = computed<ZanimAnimationSummary | null>(() => {
    const entry = this.selectedEntry();
    return findZanimAnimationForEntry(this.zanimSummary(), entry?.index ?? null, entry?.name ?? null);
  });
  readonly visibleZanimAnimations = computed<ZanimAnimationSummary[]>(() => {
    const summary = this.zanimSummary();
    if (!summary) { return []; }
    const q = this.search().trim().toLowerCase();
    const animations = summary.sets.flatMap(set => set.animations);
    if (!q) { return animations; }
    return animations.filter(anim =>
      [anim.name, ...anim.nameRefs].join(' ').toLowerCase().includes(q));
  });
  readonly animationBoneRows = computed<Array<{ index: number; name: string; clean: string; parent: number; trackType: string; attachment: boolean; depth: number }>>(() => {
    const clip = this.animationClip();
    if (!clip) { return []; }
    return clip.bones.map((name, index) => {
      const clean = cleanAnimationBoneName(name);
      const track = clip.tracks[index];
      const depth = ((name || '').match(/^-*/) || [''])[0].length;
      return {
        index,
        name,
        clean,
        parent: clip.parents[index] ?? -1,
        trackType: track ? trackTypeKey(track.type) : '-',
        attachment: /weapon|rifle|gun|pistol|cig|cigar|smoke|prop|attach/i.test(clean),
        depth,
      };
    });
  });
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
  private unitsPerMeter = 10;
  // The raw model bytes for the currently-displayed model, kept so we can
  // re-run the binding pass on dropdown change without re-decoding.
  private currentModelBytes: Uint8Array | null = null;
  private currentSliceOffsets: ChainOffset[] = [];
  private currentPositionUnit: PositionUnit = 'scale';
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
  private currentModelBindingMode: 'weaponDescriptor' | 'map' | 'tail' = 'tail';
  private currentModelSourceArchive: string | null = null;
  readonly mapTextureV = signal<'flipV' | 'direct'>('flipV');
  readonly mapUvInterpretation = signal<'autoWorld' | 'norm4096' | 'pixel16' | 'pixel256'>('autoWorld');
  readonly mapTextureFilter = signal<string>('');
  readonly mapCameraView = signal<ModelCameraView>('auto');
  readonly activeMapCameraReference = computed<MapCameraReference | null>(() => {
    const mdl = this.model();
    if (!mdl) { return null; }
    const modelBounds = this.boundsFromVertices(mdl.vertices);
    return this.nearestPackageMapReference(modelBounds);
  });

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
  private readonly entryParents = computed<Map<number, number> | null>(() => {
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

  // CLUTTER.ZAR › Clutter › <model>: the model group of a clutter placement list.
  private clutterGroupName(entry: ZipperEntryInfo): string | null {
    if (!/CLUTTER\.ZAR$/i.test(this.currentFileName())) { return null; }
    const parents = this.entryParents();
    const parent = parents?.get(entry.index);
    if (parent === undefined || parent < 0) { return null; }
    const p = this.rawEntries().find(e => e.index === parent);
    return p?.name === 'Clutter' && parents?.get(parent) === -1 ? entry.name : null;
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
  private enterWhenReady: number | null = null;
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

  // An item's model; with none, the selected model of a model archive, else just the package.
  openInModelEditor(item?: BrowserItem): void {
    if (!item) {
      const selected = this.selectedEntry();
      item = selected && this.isCurrentFileModelArchive() ? this.browserItems().find(i => i.entry.index === selected.index) : undefined;
      if (!item) { host.open('models', this.rootPath()); return; }
    }
    const layer = this.currentFileName().split(/[\\/]/).pop() ?? '';
    const archive = item.kind === 'definition' ? layer.replace(/_GEO(\([^)]+\))?\.ZED$/i, '_MDL.ZED') : layer;
    host.open('models', this.rootPath(), { archive, name: item.name });
  }

  openInAnimationEditor(): void { host.open('animations', this.rootPath()); }

  isAnimationArchive(item: BrowserItem): boolean {
    return item.kind === 'archive' && /(MOTION[^\\/]*|ZANIM)\.ZAR$/i.test(item.name);
  }

  isModelArchive(item: BrowserItem): boolean {
    return item.kind === 'archive' && /_MDL(\([^)]+\))?\.ZED$/i.test(item.name);
  }

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

  toggleWireframe(): void {
    const next = !this.wireframe();
    this.wireframe.set(next);
    this.modelRenderer?.setWireframe(next);
  }

  toggleFlipY(): void {
    const next = !this.flipY();
    this.flipY.set(next);
    this.modelRenderer?.setFlipY(next);
  }

  onChunkChange(event: Event): void {
    const value = Number((event.target as HTMLSelectElement).value);
    this.selectedChunk.set(Number.isFinite(value) ? value : -1);
    this.applyModelToRenderer();
  }

  onUvModeChange(event: Event): void {
    const value = (event.target as HTMLSelectElement).value as 'direct' | 'flipV' | 'swap' | 'swapFlipV';
    this.uvMode.set(value);
    this.applyModelToRenderer();
  }

  onPaletteOverrideChange(event: Event): void {
    const value = (event.target as HTMLSelectElement).value;
    this.paletteOverride.set(value && value.length > 0 ? value : null);
    this.applyModelToRenderer();
  }

  selectZanimAnimation(entryIndex: number): void {
    this.selectedIndex.set(entryIndex);
  }

  zanimWordsLabel(words: number[] | undefined): string {
    if (!words || words.length === 0) { return '-'; }
    return words.map(w => '0x' + (w >>> 0).toString(16).toUpperCase()).join(' ');
  }

  zanimParamsLabel(params: { words: number[]; floats: number[] } | null): string {
    if (!params) { return 'loading...'; }
    const words = params.words.map(w => '0x' + (w >>> 0).toString(16).toUpperCase()).join(' ');
    const floats = params.floats.map(f => Number.isFinite(f) ? f.toFixed(3) : 'nan').join(', ');
    return `${words}  | f32 ${floats}`;
  }

  zanimNodeRefLabel(words: [number, number, number, number]): string {
    return `[${words.map(w => '0x' + w.toString(16).toUpperCase()).join(', ')}]`;
  }

  toggleAnimationPlayback(): void {
    const next = !this.animationPlaying();
    this.animationPlaying.set(next);
    this.animationRenderer?.setPlaying(next);
  }

  resetAnimationView(): void {
    this.animationRenderer?.resetView();
  }

  onAnimationFrameInput(event: Event): void {
    const value = Number((event.target as HTMLInputElement).value);
    if (!Number.isFinite(value)) { return; }
    this.animationPlaying.set(false);
    this.animationRenderer?.setPlaying(false);
    this.animationRenderer?.setFrame(value);
    this.animationFrame.set(Math.floor(value));
  }

  animationFrameMax(clip: RdrAnimationClip): number {
    return Math.max(0, clip.sampleCount - 1);
  }

  animationFpsLabel(clip: RdrAnimationClip): string {
    return `${animationFrameRate(clip).toFixed(2)} fps`;
  }

  cleanAnimationBoneLabel(name: string): string {
    return cleanAnimationBoneName(name);
  }

  isAnimationAttachment(name: string): boolean {
    return /weapon|rifle|gun|pistol|cig|cigar|smoke|prop|attach/i.test(cleanAnimationBoneName(name));
  }

  animationHex(value: number): string {
    return formatAnimationHex(value);
  }

  animationTrackBase(track: RdrAnimationTrack): string {
    if (track.baseVec4) { return `[${track.baseVec4.join(', ')}]`; }
    if (track.posSamples?.length) {
      const p = track.posSamples[0];
      return `[${p.map(v => Math.round(v)).join(', ')}] -> animated`;
    }
    return 'n/a';
  }

  animationTrackQuat(track: RdrAnimationTrack): string {
    if (track.staticQuat) { return `[${track.staticQuat.join(', ')}] static`; }
    if (track.quatSamples?.length) { return `[${track.quatSamples[0].join(', ')}] -> animated`; }
    return 'n/a';
  }

  activeVertexCount(): number {
    const mdl = this.model();
    if (!mdl) { return 0; }
    const chunk = this.selectedChunk();
    if (chunk >= 0 && chunk < mdl.chunks.length) { return mdl.chunks[chunk].vertexCount; }
    return mdl.vertices.length;
  }

  chunkLabel(idx: number, chunk: { vertexCount: number; decodedTriangleCount: number; textureName: string | null }): string {
    // User asked for ".tif should be the name" - lead the label with it.
    const tifName = chunk.textureName ?? '(no texture)';
    const visual = (chunk as { visualKey?: string }).visualKey;
    const partName = visual ?? this.partNameFor(idx);
    const stats = `${chunk.vertexCount}v/${chunk.decodedTriangleCount}t`;
    return partName
      ? `${tifName} - ${partName} (#${idx}, ${stats})`
      : `${tifName} (#${idx}, ${stats})`;
  }

  private partNameFor(chunkIdx: number): string | null {
    const scene = this.companionScene();
    const entry = this.selectedEntry();
    if (!scene || !entry) { return null; }
    const sceneNode = findSceneNode(scene, entry.name);
    const parts = collectVisualPartNames(sceneNode);
    return parts[chunkIdx] ?? null;
  }

  activeTriangleCount(): number {
    const mdl = this.model();
    if (!mdl) { return 0; }
    const chunk = this.selectedChunk();
    if (chunk >= 0 && chunk < mdl.chunks.length) { return mdl.chunks[chunk].decodedTriangleCount; }
    return mdl.indices.length / 3;
  }

  paletteLabel(tex: ZedTexture): string {
    if (!tex.header.isIndexed) { return 'n/a (direct colour)'; }
    const id = tex.header.cbp ?? tex.header.paletteId;
    const source = tex.header.cbp !== null ? 'TEX0' : 'header';
    return tex.paletteResolved
      ? `texpal_${id} (${source})`
      : `missing: texpal_${id} (${source})`;
  }

  visualLabel(key: string): string {
    const mdl = this.model();
    if (!mdl) { return key; }
    const chunks = mdl.chunks.filter(c => c.visualKey === key);
    const tris = chunks.reduce((n, c) => n + c.decodedTriangleCount, 0);
    return `${key} (${chunks.length} chunk${chunks.length === 1 ? '' : 's'}, ${tris}t)`;
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

  // When we enter a *_TXR.ZED sub-archive, the matching *_PAL.ZED usually
  // lives next to it in the parent layer (sub-archive or the root ZDB). Find
  // and parse it inline so indexed textures inside resolve to colour instead
  // of grayscale.
  private loadNestedPalettesForCurrentLayer(): void {
    // When the full ZDB package was preloaded (applyZdbPackage), the
    // cross-package merged palette map already contains every palette
    // from every _PAL.ZED in the archive. Re-fetching a single-prefix
    // PAL here would OVERWRITE the merged map with one prefix's
    // palettes and break textures from the other prefixes — that was
    // exactly the bug the user hit drilling into WORL_MDL.ZED inside
    // M51.ZDB. Skip when package context is intact.
    if (this.packageEntryBytes.size > 0) { return; }

    const stack = this.archiveStack();
    if (stack.length === 0) { return; }
    const current = stack[stack.length - 1];
    const currentLeafName = (current.name.split(/[\\/]/).pop() ?? current.name);
    const txrMatch = /^(.+?)_TXR(?:\([^)]+\))?\.ZED$/i.exec(currentLeafName);
    if (!txrMatch) { return; }
    const targetPalLeaf = `${txrMatch[1]}_PAL.ZED`.toLowerCase();

    const previousLayer = stack.length >= 2 ? stack[stack.length - 2].archive : null;

    // Case A: parent is another sub-archive. PAL bytes are in memory.
    if (previousLayer) {
      const palEntry = previousLayer.entries.find(
        e => (e.name.split(/[\\/]/).pop() ?? e.name).toLowerCase() === targetPalLeaf,
      );
      if (!palEntry) { return; }
      const palBytes = previousLayer.entryData.get(palEntry.index);
      if (!palBytes || palBytes.length === 0) { return; }
      this.applyPaletteBytesForCurrentLayer(palBytes, palEntry.name, current.archive);
      return;
    }

    // Case B: parent is the root file. We need to async-fetch the PAL entry
    // bytes from the bridge, then parse and apply.
    const filePath = this.result()?.path;
    if (!filePath) { return; }
    const rootEntries = this.result()?.entries ?? [];
    const palEntry = rootEntries.find(
      e => (e.name.split(/[\\/]/).pop() ?? e.name).toLowerCase() === targetPalLeaf,
    );
    if (!palEntry) { return; }

    // Mark this fetch as for "palette purposes" so handleNodeData routes it
    // to applyPaletteBytesForCurrentLayer instead of decodePayload.
    this.pendingPaletteFetch = { rootEntryIndex: palEntry.index, palName: palEntry.name };
    this.vscode.postMessage({ type: 'readNode', path: filePath, entryIndex: palEntry.index });
  }

  private pendingPaletteFetch: { rootEntryIndex: number; palName: string } | null = null;

  private applyPaletteBytesForCurrentLayer(
    palBytes: Uint8Array,
    palName: string,
    currentArchive: InspectedZedArchive,
  ): void {

    try {
      const palArchive = parseZedArchive(palBytes);
      let palettes = parsePaletteEntries(
        palArchive.entries.map(e => ({ name: e.name })),
        i => palArchive.entryData.get(palArchive.entries[i].index) ?? new Uint8Array(0),
      );
      palettes = aliasPalettesForTextureEntries(
        palettes,
        currentArchive.entries.map(e => ({ name: e.name })),
        i => currentArchive.entryData.get(currentArchive.entries[i].index) ?? new Uint8Array(0),
      );
      if (palettes.size > 0) {
        this.palettes.set(palettes);
        this.paletteMessage.set(`Loaded ${palettes.size} palette(s) from ${palName}.`);
      }
    } catch { /* leave existing palettes in place */ }
  }

  // When user enters a *_MDL.ZED sub-archive, pull the matching TXR / PAL /
  // GEO siblings from the parent layer (in-memory sub-archive or async fetch
  // from the root ZDB). Once bytes arrive, populate companionTextures and
  // palettes so model chunks can resolve their textures by .tif name.
  rootCompanionFetches = new Map<number, 'txr' | 'pal' | 'geo'>();
  rootCompanionTxrArchives = new Map<string, InspectedZedArchive>();
  rootCompanionPalArchives = new Map<string, InspectedZedArchive>();
  private companionScenesByNamespace = new Map<string, GeoScene>();
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

  private rebuildAliasedPalettes(sourceName: string): void {
    if (this.rootCompanionPalArchives.size === 0) { return; }
    // Merge into what is loaded: palettes from the package (or an earlier
    // companion) must survive an on-demand fetch of another archive.
    const merged = new Map<string, ZedPalette>(this.palettes() ?? []);
    for (const [namespace, pal] of this.rootCompanionPalArchives) {
      let palettes = parsePaletteEntries(
        pal.entries.map(e => ({ name: e.name })),
        i => pal.entryData.get(pal.entries[i].index) ?? new Uint8Array(0),
      );
      const txr = this.rootCompanionTxrArchives.get(namespace);
      if (txr) {
        palettes = aliasPalettesForTextureEntries(
          palettes,
          txr.entries.map(e => ({ name: e.name })),
          i => txr.entryData.get(txr.entries[i].index) ?? new Uint8Array(0),
        );
      }
      for (const [key, value] of palettes) {
        merged.set(key, value);
        merged.set(`${namespace}:${key}`, value);
      }
    }
    if (merged.size > 0) {
      this.palettes.set(merged);
      this.paletteMessage.set(`Loaded ${merged.size} palette alias(es) from ${sourceName}.`);
      this.setStatus(
        `Palettes ready (${merged.size}). TXR loaded: ${this.rootCompanionTxrArchives.size}.`,
        false,
        false,
      );
    } else {
      this.setStatus(`Palette PAL parsed but yielded 0 palettes (${sourceName}).`, true, false);
    }
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

  private decodePayload(index: number, bytes: Uint8Array, skipServer = false): void {
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
  }

  // The models the server lists for the open package, keyed by archive|name (lowercased).
  serverModelIndex(path: string): Promise<Map<string, { archive: string; name: string }>> {
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
  }

  // Decodes the selected model on the server (mesh + textures); anything it does not list, or a failure, goes back
  // through the in-page decoders.
  private async decodeWithServer(index: number, bytes: Uint8Array, archiveHint: string | null, name: string): Promise<void> {
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
  }

  private renderServerModel(textures: ModelTextureMap): void {
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

  private tryApplyAnimationPreview(bytes: Uint8Array, name: string): boolean {
    const leaf = name.split(/[\\/]/).pop() ?? name;
    const animation = parseRdrAnimation(bytes, leaf);
    if (!animation.animationLike) { return false; }

    this.previewMode.set('animation');
    this.animationClip.set(animation);
    this.animationPlaying.set(true);
    this.animationFrame.set(0);
    this.previewMessage.set(animation.parseOk
      ? ''
      : `Animation parsed partially (${animation.tracks.length}/${animation.boneCount} tracks).`);
    this.log(
      `[ANIM] ${leaf}: ${animation.boneCount} bones, ${animation.sampleCount} samples, ` +
      `${animation.tracks.length} tracks, ${animation.parseOk ? 'clean' : 'partial'}`,
    );
    setTimeout(() => this.applyAnimationToRenderer(), 0);
    return true;
  }

  private resolveAlternateModelPayload(entryName: string): { bytes: Uint8Array; sliceOffsets: ChainOffset[]; sourceName: string; sourceArchiveName: string } | null {
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
  }

  private currentArchiveLeaf(): string | null {
    const stack = this.archiveStack();
    if (stack.length === 0) {
      const path = this.result()?.path;
      return path ? (path.split(/[\\/]/).pop() ?? path).toLowerCase() : null;
    }
    const current = stack[stack.length - 1].name;
    return (current.split(/[\\/]/).pop() ?? current).toLowerCase();
  }

  private collectModelLookupArchives(): Array<{ leaf: string; archive: InspectedZedArchive }> {
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
  }

  private modelSourcePreferOrder(): string[] {
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
  }

  packageHasArchive(namespace: string, kind: 'txr' | 'pal' | 'mdl' | 'geo'): boolean {
    const escaped = namespace.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    const rx = new RegExp(`^${escaped}_${kind}(?:\\([^)]+\\))?\\.zed$`, 'i');
    for (const key of this.packageEntryBytes.keys()) {
      if (rx.test(key)) { return true; }
    }
    return false;
  }

  // Pulls slice offsets from the Nxxx_xxx child stubs of the selected entry.
  // These nodes hold 4-byte payloads = byte offsets into the parent model
  // payload, defining where each material slice begins. Returns [] when no
  // sub-archive context is available (e.g. opened a raw model directly).
  private collectSliceOffsetsForCurrentEntry(options: { siblingFallback?: boolean } = {}): ChainOffset[] {
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
  }

  // Runs the smartWorld / sliceNearest / globalNearest binding pass per the
  // current dropdown selection. `tailScan` (default) is a no-op so the
  // existing chunk.textureName from findTifInRange is preserved.
  private applyCurrentBinding(mdl: ZedModel): void {
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
  }

  // Parse options for the current binding dropdown: chain offsets are only
  // handed to the parser in chain mode so the marker modes still show the
  // heuristic result the user asked for. Positions are always decoded in
  // the scale convention first; choosePositionUnit converts afterwards.
  private parseOptionsFor(chainOffsets: ChainOffset[]): { chainOffsets?: ChainOffset[]; positionUnit: PositionUnit } {
    return this.mapMaterialBinding() === 'chain'
      ? { chainOffsets, positionUnit: 'scale' }
      : { positionUnit: 'scale' };
  }

  // Decide how a decoded model's int16 positions map to units.
  //   1. the dropdown override
  //   2. the GEO node's nparams bounds: the convention whose extent matches
  //      them (the two differ by exactly 2048x, so this is unambiguous)
  //   3. the GEO node's vparams flags (weapons normalise by the chunk
  //      scale, everything else is 1/16 units)
  //   4. the scale convention
  private choosePositionUnit(
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
  }

  // ------------------------------------------------------------------
  // Spawn camera. Points come from the package's AIMAPS.MPS (see
  // zed-spawn-points.ts); heights from the geometry in view.
  // ------------------------------------------------------------------
  private loadMapPoints(results: MpsResult[]): void {
    this.mapPoints.set([]);
    this.selectedMapPoint.set('');
    for (const result of results) {
      const grid = result.maps[0];
      const leaf = (result.sourcePath.split(/[\\/]/).pop() ?? '').trim().toLowerCase();
      const bytes = this.packageEntryBytes.get(leaf);
      if (!grid || !bytes) { continue; }
      const points = parseMapPoints(bytes, {
        minX: grid.minX, minZ: grid.minZ, minY: grid.minY, maxY: grid.maxY,
        scaleX: grid.scaleX, scaleZ: grid.scaleZ, width: grid.width, height: grid.height,
      });
      if (points.length > 0) {
        this.mapPoints.set(points);
        const first = points.find(p => p.name.toLowerCase() === 'startplayer') ?? points.find(p => p.kind === 'start') ?? points[0];
        this.selectedMapPoint.set(first.name);
        this.log(`[spawn] ${points.length} point(s) from ${leaf}: ${points.map(p => p.name).join(', ')}`);
        break;
      }
    }
    // MetersPerUnit lives in the map's config ZED (MP53.ZED).
    for (const [leaf, bytes] of this.packageEntryBytes) {
      if (!/^[a-z]+\d+\.zed$/i.test(leaf)) { continue; }
      try {
        const archive = parseZedArchive(bytes);
        const entry = archive.entries.find(e => e.name === 'MetersPerUnit');
        const data = entry ? archive.entryData.get(entry.index) : undefined;
        if (data && data.length >= 4) {
          const mpu = new DataView(data.buffer, data.byteOffset, 4).getFloat32(0, true);
          if (Number.isFinite(mpu) && mpu > 0 && mpu < 100) { this.unitsPerMeter = 1 / mpu; }
          break;
        }
      } catch { /* not a config archive */ }
    }
  }

  private mapGridMaxY(): number {
    const grid = this.packageMpsResults()[0]?.maps[0];
    return grid ? grid.maxY + 2 * this.unitsPerMeter : Number.POSITIVE_INFINITY;
  }

  private pointHeight(point: MapPoint, mdl: ZedModel | null): number | null {
    if (!mdl) { return null; }
    // Sky domes and sun cards are never ground.
    const visible = { chunks: mdl.chunks.filter(c => !isSkyChunk(c) && !/lightrays/i.test(c.textureName ?? '')) };
    return groundHeight(visible, point.x, point.z, this.mapGridMaxY());
  }

  // Markers only make sense on geometry that covers the AI grid (the world
  // or a composed scene), not on a single prop.
  private mapMarkersFor(mdl: ZedModel): SceneMarker[] | null {
    const points = this.mapPoints();
    if (!this.showMapMarkers() || points.length === 0) { return null; }
    const bounds = this.boundsFromVertices(mdl.chunks.flatMap(c => c.vertices));
    const grid = this.packageMpsResults()[0]?.maps[0];
    if (!bounds || !grid) { return null; }
    const coversGrid = (bounds.maxX - bounds.minX) > (grid.maxX - grid.minX) * 0.5 &&
      (bounds.maxZ - bounds.minZ) > (grid.maxZ - grid.minZ) * 0.5;
    if (!coversGrid) { return null; }
    return points.map(p => ({
      position: [p.x, this.pointHeight(p, mdl) ?? grid.minY, p.z] as [number, number, number],
      label: p.name,
      color: p.kind === 'start' ? (/hostage/i.test(p.name) ? 0xffa040 : 0x40e0a0) : 0x5aa0ff,
      height: 2.5 * this.unitsPerMeter,
    }));
  }

  onSelectedMapPointChange(event: Event): void {
    this.selectedMapPoint.set((event.target as HTMLSelectElement).value);
    // While flying, jump straight to the new point.
    if (this.flying()) { this.flyFromSelectedPoint(); }
  }

  onShowSkyChange(event: Event): void {
    this.showSky.set((event.target as HTMLInputElement).checked);
    this.applyModelToRenderer();
  }

  onShowMapMarkersChange(event: Event): void {
    this.showMapMarkers.set((event.target as HTMLInputElement).checked);
    this.applyModelToRenderer();
  }

  onMapLightingChange(event: Event): void {
    this.mapLighting.set((event.target as HTMLSelectElement).value as ReturnType<typeof this.mapLighting>);
    this.applyModelToRenderer();
  }

  // First-person camera at the selected point: on the ground found under
  // it, at eye height, facing the middle of the geometry.
  flyFromSelectedPoint(): void {
    const mdl = this.model();
    const point = this.mapPoints().find(p => p.name === this.selectedMapPoint());
    if (!mdl || !point || !this.modelRenderer) { return; }
    const ground = this.pointHeight(point, mdl);
    const grid = this.packageMpsResults()[0]?.maps[0];
    const y = (ground ?? grid?.minY ?? 0) + 1.7 * this.unitsPerMeter;
    const bounds = this.boundsFromVertices(mdl.chunks.flatMap(c => c.vertices));
    const cx = bounds ? (bounds.minX + bounds.maxX) / 2 : point.x;
    const cz = bounds ? (bounds.minZ + bounds.maxZ) / 2 : point.z + 1;
    const yaw = Math.atan2(cx - point.x, cz - point.z);
    this.modelRenderer.startFly([point.x, y, point.z], yaw, 6 * this.unitsPerMeter);
    this.log(`[spawn] flying from ${point.name} at (${point.x.toFixed(0)}, ${y.toFixed(0)}, ${point.z.toFixed(0)})${ground === null ? ' (no ground found under it)' : ''}`);
  }

  exitFly(): void {
    this.modelRenderer?.stopFly();
  }

  // The GEO node that defines a model (same lookup as the unit choice).
  private findGeoNodeForModel(entryName: string | null | undefined, sourceArchiveName: string | null | undefined): GeoNode | null {
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
  }

  private allGeoScenes(): GeoScene[] {
    const scenes = [...this.companionScenesByNamespace.values()];
    const legacy = this.companionScene();
    if (legacy && !scenes.includes(legacy)) { scenes.push(legacy); }
    return scenes;
  }

  // Decode-time settings (binding, units) need the payload decoded again:
  // a single model from its bytes, the composed map from the package.
  private canRedecodePreview(): boolean {
    const entry = this.selectedEntry();
    if (!entry) { return false; }
    return !!this.currentModelBytes || this.isMapWorldModelEntry(entry.name, this.currentArchiveLeaf());
  }

  // The map's world model (WORL_MDL > worldmodel) when the map scene that
  // places it has loaded.
  private isMapWorldModelEntry(entryName: string, sourceArchive: string | null | undefined): boolean {
    if (entryName.toLowerCase() !== 'worldmodel') { return false; }
    if (namespaceFromArchiveName(sourceArchive ?? '') !== 'worl') { return false; }
    const scene = this.mapScene();
    return !!scene && !!findSceneNode(scene, 'worldmodel');
  }

  // The map's scene graph: the GEO archive that places the world
  // (worldmodel), preferring the map's own prefix (MP53_GEO). Package order
  // puts RUN\COMMON first, so "the first GEO" was EFFE_GEO.
  mapScene(): GeoScene | null {
    const candidates = [...this.companionScenesByNamespace.entries()].map(([namespace, scene]) => ({ namespace, scene }));
    const chosen = chooseMapScene(candidates, this.primaryMapTextureNamespace());
    return chosen?.scene ?? this.companionScene();
  }

  // Put every chain-decoded chunk where its GEO node says: each visual's
  // positions are converted with that visual's own vparams unit and then
  // transformed by its node's accumulated matrix (relative to the model
  // root). Returns null when the chunks carry no visual keys or none of
  // them resolve, so callers fall back to the whole-model unit choice.
  private placeModelByGeo(mdl: ZedModel, geoNode: GeoNode | null): { model: ZedModel; choice: PositionUnitChoice; matched: number } | null {
    const placed = placeModelByGeo(mdl, geoNode, this.mapPositionUnit());
    if (!placed) {
      if (geoNode && mdl.chunks.some(c => c.visualKey)) {
        this.log(`[placement] ${geoNode.name}: none of the visual keys matched a GEO node`);
      }
      return null;
    }
    this.log(`[placement] ${geoNode!.name}: ${placed.matched}/${placed.total} visual(s) placed by GEO node (${placed.mode}), units ${[...placed.unitCounts].map(([u, n]) => `${u}=${n}`).join(', ')}`);
    return placed;
  }

  private recordDecodeInfo(label: string, mdl: ZedModel, choice: PositionUnitChoice): void {
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
  }

  private isCurrentWeaponModelLayer(): boolean {
    const stack = this.archiveStack();
    const current = stack.length > 0 ? stack[stack.length - 1].name : '';
    const leaf = current.split(/[\\/]/).pop() ?? current;
    return this.isWeaponModelArchive(leaf);
  }

  private isCurrentEffectModelLayer(): boolean {
    const stack = this.archiveStack();
    const current = stack.length > 0 ? stack[stack.length - 1].name : '';
    const leaf = current.split(/[\\/]/).pop() ?? current;
    return this.isEffectModelArchive(leaf);
  }

  private isWeaponModelArchive(archiveName: string | null | undefined): boolean {
    const leaf = (archiveName?.split(/[\\/]/).pop() ?? '').toLowerCase();
    return /^weap_mdl(?:\([^)]+\))?\.zed$/i.test(leaf);
  }

  private isEffectModelArchive(archiveName: string | null | undefined): boolean {
    const leaf = (archiveName?.split(/[\\/]/).pop() ?? '').toLowerCase();
    return /^effe_mdl(?:\([^)]+\))?\.zed$/i.test(leaf);
  }

  private isCharacterModelArchive(archiveName: string | null | undefined): boolean {
    const leaf = (archiveName?.split(/[\\/]/).pop() ?? '').toLowerCase();
    return /^(?:clib|flib)_mdl(?:\([^)]+\))?\.zed$/i.test(leaf);
  }

  private companionSceneForArchive(archiveName: string | null | undefined): GeoScene | null {
    const namespace = namespaceFromArchiveName(archiveName);
    if (namespace) {
      const scene = this.companionScenesByNamespace.get(namespace);
      if (scene) { return scene; }
    }
    return this.companionScene();
  }

  private characterGeoNameCandidates(entryName: string): string[] {
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
  }

  private characterBoneMatricesForEntry(entryName: string, sourceArchiveName: string | null | undefined): Array<number[] | undefined> | null {
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
  }

  private shouldAutoComposeCharacterNode(entry: { size: number; children: number[] }, archiveName: string | null | undefined): boolean {
    if (!this.isCharacterModelArchive(archiveName)) { return false; }
    if (entry.children.length === 0) { return false; }
    // CLIB/FLIB roots often carry skeleton/control bytes that look vaguely
    // renderable to the fallback scanner. Prefer the subtree mesh children
    // for roots and grouped body parts, but leave leaf payloads inspectable.
    return entry.size === 0 || entry.children.length > 1 || entry.size < 0x4000;
  }

  private buildHierarchicalModel(
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
  }

  private refreshPreview(): void {
    const entry = this.selectedEntry();
    if (!entry) { return; }
    const bytes = this.nodeCache.get(entry.index);
    if (bytes) { this.decodePayload(entry.index, bytes); }
  }

  private ensureModelRenderer(): void {
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
  }

  private ensureAnimationRenderer(): void {
    const canvas = this.animationCanvas?.nativeElement;
    if (!canvas) { return; }
    if (this.animationRenderer && this.animationRenderer.attachedCanvas !== canvas) {
      this.animationRenderer.dispose();
      this.animationRenderer = null;
    }
    if (!this.animationRenderer) {
      this.animationRenderer = new RdrAnimationCanvasRenderer(canvas);
      this.animationRenderer.onFrameChange = frame => this.animationFrame.set(frame);
    }
  }

  private applyAnimationToRenderer(): void {
    this.ensureAnimationRenderer();
    if (!this.animationRenderer) { return; }
    this.animationRenderer.setClip(this.animationClip());
    this.animationRenderer.setPlaying(this.animationPlaying());
  }

  private applyModelToRenderer(): void {
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
  }

  private applyModelToRendererUnsafe(): void {
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

  private requestPalettesIfNeeded(): void {
    const filePath = this.result()?.path;
    if (!filePath || this.palettesRequested) { return; }
    const name = filePath.split(/[\\/]/).pop() ?? '';
    // Fire for both .ZED siblings and for reader .ZAR archives (READERC/READERX).
    // The provider's _findCompanionFiles handles both cases and returns the
    // appropriate UI_TXR / UI_PAL companions so RDR screens show thumbnails.
    if (!/\.(ZED|ZAR)$/i.test(name)) { return; }
    this.palettesRequested = true;
    this.vscode.postMessage({ type: 'readCompanionFiles', path: filePath });
  }

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

  private requestArchiveNodesIfNeeded(): void {
    const filePath = this.result()?.path;
    if (!filePath || this.archiveNodesRequestedFor === filePath) { return; }
    if (!this.zanimSummary()) { return; }
    this.archiveNodesRequestedFor = filePath;
    this.setStatus(`Loading ${filePath.split(/[\\/]/).pop()} animation payloads...`, false, true);
    this.vscode.postMessage({ type: 'loadArchiveNodes', path: filePath });
  }

  private selectInitialZanimEntryIfNeeded(): void {
    if (this.selectedIndex() !== null) { return; }
    const summary = this.zanimSummary();
    if (!summary) { return; }
    const firstAnimation = summary.sets[0]?.animations[0];
    const animSets = summary.entries.find(e => e.parent < 0 && e.name.toLowerCase() === 'anim_sets');
    this.selectedIndex.set(firstAnimation?.entryIndex ?? animSets?.index ?? null);
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

  private parsePackageMpsResults(): MpsResult[] {
    const results: MpsResult[] = [];
    const packagePath = this.result()?.path ?? 'package';
    for (const [leaf, bytes] of this.packageEntryBytes) {
      if (!/\.mps$/i.test(leaf)) { continue; }
      const parsed = this.parseMpsBytes(bytes, `${packagePath} / ${leaf}`);
      if (parsed && parsed.foundCount > 0) { results.push(parsed); }
    }
    return results;
  }

  private parseMpsBytes(bytes: Uint8Array, sourcePath: string): MpsResult | null {
    if (!bytes || bytes.length < 0x28) { return null; }
    const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    const declaredCount = view.getUint32(0x04, true);
    const maps: MpsMap[] = [];
    for (let o = 0x28; o <= bytes.length - 0x80; o += 4) {
      if (view.getUint32(o, true) !== 0x17) { continue; }
      if (o + 0x58 > bytes.length) { continue; }
      const width = view.getUint32(o + 0x1C, true);
      const height = view.getUint32(o + 0x20, true);
      const scaleX = view.getFloat32(o + 0x24, true);
      const scaleZ = view.getFloat32(o + 0x28, true);
      if (width === 0 || width > 5000 || height === 0 || height > 5000) { continue; }
      if (!this.isFinitePositive(scaleX) || !this.isFinitePositive(scaleZ)) { continue; }
      const name = this.readFixedAscii(bytes, o + 0x38, 0x20);
      if (!name) { continue; }
      const map: MpsMap = {
        offset: o,
        name,
        width,
        height,
        minX: view.getFloat32(o + 0x04, true),
        minY: view.getFloat32(o + 0x08, true),
        minZ: view.getFloat32(o + 0x0C, true),
        maxX: view.getFloat32(o + 0x10, true),
        maxY: view.getFloat32(o + 0x14, true),
        maxZ: view.getFloat32(o + 0x18, true),
        scaleX,
        scaleZ,
      };
      if (!this.boundsAreFinite(map)) { continue; }
      maps.push(map);
      if (declaredCount > 0 && maps.length >= declaredCount) { break; }
    }
    return {
      sourcePath,
      declaredCount,
      foundCount: maps.length,
      fileSize: bytes.length,
      maps,
    };
  }

  private readFixedAscii(bytes: Uint8Array, offset: number, maxLen: number): string {
    const end = Math.min(offset + maxLen, bytes.length);
    let s = '';
    for (let i = offset; i < end; i++) {
      const b = bytes[i];
      if (b === 0) { break; }
      if (b < 0x20 || b > 0x7e) { return ''; }
      s += String.fromCharCode(b);
    }
    return s.trim();
  }

  private isFinitePositive(value: number): boolean {
    return Number.isFinite(value) && value > 0 && value < 1_000_000;
  }

  private boundsAreFinite(map: MpsMap): boolean {
    return [map.minX, map.minY, map.minZ, map.maxX, map.maxY, map.maxZ].every(Number.isFinite);
  }

  private boundsFromVertices(vertices: ZedModel['vertices']): Bounds3 | null {
    if (!vertices.length) { return null; }
    const bounds = this.emptyBounds();
    for (const v of vertices) { this.includeBoundsPoint(bounds, v.x, v.y, v.z); }
    return bounds;
  }

  private boundsFromGeoInstances(instances: GeoInstance[]): Bounds3 | null {
    if (!instances.length) { return null; }
    const bounds = this.emptyBounds();
    for (const inst of instances) {
      const [x, y, z] = transformPoint(inst.matrix, 0, 0, 0);
      this.includeBoundsPoint(bounds, x, y, z);
    }
    return bounds;
  }

  private boundsFromMpsMap(map: MpsMap): Bounds3 {
    return {
      minX: map.minX,
      minY: map.minY,
      minZ: map.minZ,
      maxX: map.maxX,
      maxY: map.maxY,
      maxZ: map.maxZ,
    };
  }

  private emptyBounds(): Bounds3 {
    return {
      minX: Number.POSITIVE_INFINITY,
      minY: Number.POSITIVE_INFINITY,
      minZ: Number.POSITIVE_INFINITY,
      maxX: Number.NEGATIVE_INFINITY,
      maxY: Number.NEGATIVE_INFINITY,
      maxZ: Number.NEGATIVE_INFINITY,
    };
  }

  private includeBoundsPoint(bounds: Bounds3, x: number, y: number, z: number): void {
    if (!Number.isFinite(x) || !Number.isFinite(y) || !Number.isFinite(z)) { return; }
    bounds.minX = Math.min(bounds.minX, x);
    bounds.minY = Math.min(bounds.minY, y);
    bounds.minZ = Math.min(bounds.minZ, z);
    bounds.maxX = Math.max(bounds.maxX, x);
    bounds.maxY = Math.max(bounds.maxY, y);
    bounds.maxZ = Math.max(bounds.maxZ, z);
  }

  private formatBounds(bounds: Bounds3): string {
    const f = (value: number) => Number.isFinite(value) ? value.toFixed(2) : String(value);
    return `min=(${f(bounds.minX)}, ${f(bounds.minY)}, ${f(bounds.minZ)}) max=(${f(bounds.maxX)}, ${f(bounds.maxY)}, ${f(bounds.maxZ)}) size=(${f(bounds.maxX - bounds.minX)}, ${f(bounds.maxY - bounds.minY)}, ${f(bounds.maxZ - bounds.minZ)})`;
  }

  private logNearestPackageMapBounds(reference: Bounds3 | null): void {
    const refs = this.sortedPackageMapReferences(reference);
    if (!refs.length) {
      this.log('[bounds] no package MPS bounds loaded');
      return;
    }
    this.log(`[bounds] nearest MPS map bounds (${refs.length} available):`);
    for (const item of refs.slice(0, 3)) {
      const leaf = item.source.split(/[\\/]/).pop() ?? item.source;
      this.log(`  ${item.map.name} (${leaf}) grid=${item.map.width}x${item.map.height} scale=${item.map.scaleX.toFixed(3)}x${item.map.scaleZ.toFixed(3)} ${this.formatBounds(item.bounds)}`);
    }
  }

  private nearestPackageMapReference(reference: Bounds3 | null): MapCameraReference | null {
    return this.sortedPackageMapReferences(reference)[0] ?? null;
  }

  private sortedPackageMapReferences(reference: Bounds3 | null): MapCameraReference[] {
    const refs = this.packageMpsResults().flatMap(result =>
      result.maps.map(map => {
        const bounds = this.boundsFromMpsMap(map);
        const leaf = result.sourcePath.split(/[\\/]/).pop() ?? result.sourcePath;
        return {
          source: result.sourcePath,
          map,
          bounds,
          label: `${map.name} (${leaf})`,
          distance: reference ? this.boundsDistanceSquared(reference, bounds) : 0,
        };
      }),
    );
    return refs.sort((a, b) => a.distance - b.distance);
  }

  private boundsDistanceSquared(a: Bounds3, b: Bounds3): number {
    const ac = this.boundsCenter(a);
    const bc = this.boundsCenter(b);
    const dx = ac[0] - bc[0];
    const dy = ac[1] - bc[1];
    const dz = ac[2] - bc[2];
    return dx * dx + dy * dy + dz * dz;
  }

  private boundsCenter(bounds: Bounds3): [number, number, number] {
    return [
      (bounds.minX + bounds.maxX) * 0.5,
      (bounds.minY + bounds.maxY) * 0.5,
      (bounds.minZ + bounds.maxZ) * 0.5,
    ];
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

  // ------------------------------------------------------------------
  // Map-viewer dropdowns — set the corresponding signal and ping the
  // renderer config so any in-flight model preview re-draws with the new
  // setting. Defaults match the prior behaviour, so weapon previews don't
  // shift unless the user actively changes a setting.
  // ------------------------------------------------------------------
  onMapRenderModeChange(event: Event): void {
    this.mapRenderMode.set((event.target as HTMLSelectElement).value as ReturnType<typeof this.mapRenderMode>);
    this.applyModelToRenderer();
  }
  onMapCameraViewChange(event: Event): void {
    this.mapCameraView.set((event.target as HTMLSelectElement).value as ModelCameraView);
    this.applyModelToRenderer();
  }
  onMapTriStrideChange(event: Event): void {
    const n = Number((event.target as HTMLSelectElement).value);
    this.mapTriStride.set(Number.isFinite(n) && n > 0 ? n : 1);
    this.applyModelToRenderer();
  }
  onMapMaterialBindingChange(event: Event): void {
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
  }
  onSelectedVisualChange(event: Event): void {
    this.selectedVisual.set((event.target as HTMLSelectElement).value);
    this.applyModelToRenderer();
  }
  onMapPositionUnitChange(event: Event): void {
    this.mapPositionUnit.set((event.target as HTMLSelectElement).value as ReturnType<typeof this.mapPositionUnit>);
    // Positions are baked at parse time, so decode the selected payload
    // again from its cached bytes.
    if (this.canRedecodePreview()) {
      this.refreshPreview();
      return;
    }
    this.applyModelToRenderer();
  }
  onMapTextureVChange(event: Event): void {
    this.mapTextureV.set((event.target as HTMLSelectElement).value as ReturnType<typeof this.mapTextureV>);
    this.applyModelToRenderer();
  }
  onMapUvInterpretationChange(event: Event): void {
    this.mapUvInterpretation.set((event.target as HTMLSelectElement).value as ReturnType<typeof this.mapUvInterpretation>);
    this.applyModelToRenderer();
  }

  // ------------------------------------------------------------------
  // RDR tree — turns the shaped JSON-like root into a flat list of nodes
  // with stable paths so we can collapse/expand by path. We render only
  // the visible-under-expand subset via rdrVisibleRows().
  // ------------------------------------------------------------------
  private buildRdrTreeRoots(root: unknown): RdrTreeNode[] {
    return this.shapeToTree(root, '$', 0, null);
  }
  private shapeToTree(value: unknown, path: string, depth: number, name: string | null): RdrTreeNode[] {
    const nameLabel = name ?? (depth === 0 ? 'root' : '');
    if (value === null || value === undefined) {
      return [{ path, name: nameLabel, kind: 'leaf', value: 'null', depth }];
    }
    if (typeof value === 'string') {
      return [{ path, name: nameLabel, kind: 'string', value, depth }];
    }
    if (typeof value === 'number') {
      const kind: RdrTreeNode['kind'] = Number.isInteger(value) ? 'int' : 'float';
      return [{ path, name: nameLabel, kind, value, depth }];
    }
    if (Array.isArray(value)) {
      const children: RdrTreeNode[] = [];
      for (let i = 0; i < value.length; i++) {
        children.push(...this.shapeToTree(value[i], `${path}.${i}`, depth + 1, `[${i}]`));
      }
      return [{ path, name: nameLabel, kind: 'array', value: `${value.length}`, children, depth }];
    }
    if (typeof value === 'object') {
      const obj = value as Record<string, unknown>;
      const children: RdrTreeNode[] = [];
      for (const [k, v] of Object.entries(obj)) {
        children.push(...this.shapeToTree(v, `${path}.${k}`, depth + 1, k));
      }
      return [{ path, name: nameLabel, kind: 'object', value: `${children.length}`, children, depth }];
    }
    return [{ path, name: nameLabel, kind: 'leaf', value: String(value), depth }];
  }
  toggleRdrNode(path: string): void {
    const next = new Set(this.rdrExpanded());
    if (next.has(path)) { next.delete(path); } else { next.add(path); }
    this.rdrExpanded.set(next);
  }
  isRdrExpanded(path: string): boolean { return this.rdrExpanded().has(path); }
  rdrIsExpandable(node: RdrTreeNode): boolean { return !!(node.children && node.children.length > 0); }

  // Lists rendered when RDR fell back to printable-string scan. The backend
  // shapes `root` as { strings, textureRefs, fileRefs, modelRefs }.
  rdrFallbackLists(): { strings: string[]; textureRefs: string[]; fileRefs: string[]; modelRefs: string[] } | null {
    const r = this.rdr();
    if (!r || !r.fallback) { return null; }
    const root = r.root as Record<string, unknown> | null;
    if (!root) { return null; }
    const grab = (k: string): string[] => Array.isArray(root[k]) ? (root[k] as string[]) : [];
    return {
      strings: grab('strings'),
      textureRefs: grab('textureRefs'),
      fileRefs: grab('fileRefs'),
      modelRefs: grab('modelRefs'),
    };
  }

  // Loads companion textures for the currently open .rdr entry. When the user
  // opens READERC.ZAR or READERX.ZAR, this finds the sibling _TXR / _PAL files
  // and sends them to the provider, which responds via the existing
  // `companionFiles` message. The RDR texture thumbnail grid then populates.
  loadRdrCompanionTextures(): void {
    const filePath = this.result()?.path;
    if (!filePath) { return; }
    this.vscode.postMessage({ type: 'readCompanionFiles', path: filePath });
  }
  dumpGeoReport(): void {
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
  }
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
  canOpenInModelEditor(): boolean {
    const stack = this.archiveStack();
    return !!this.selectedEntry() && stack.length === 1 && /\.(zdb|cdb)$/i.test(this.result()?.path ?? '') &&
      /_MDL(?:\([^)]+\))?\.zed$/i.test(this.currentArchiveLeaf() ?? '');
  }

  composeGeoScene(options: { fromSceneRoot?: boolean } = {}): boolean {
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
  }

  // CLUTTER.ZAR placements as scene instances. Each is drawn with its
  // prototype's GEO definition (the node in the scene's `models` list).
  private clutterInstances(scene: GeoScene, root: GeoNode, resolve: (name: string) => unknown): SceneInstance[] {
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
  }

  // ------------------------------------------------------------------
  // Hierarchical model decode. Walks the currently-selected node's
  // subtree in the active archive, decoding chunks from EVERY child
  // node that contains them and applying any per-node transform matrix
  // pulled from the node's data. Produces one composed mesh — required
  // for CLIB / player rendering, where the actual geometry lives in
  // nested MESH_* children rather than the root payload.
  // (HTML map viewer's decodeHierarchicalModelNode parity.)
  // ------------------------------------------------------------------
  composeHierarchicalModel(): void {
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
  }

  // Walks the loaded GEO scene like composeGeoScene does, but instead of
  // building a mesh, logs every instance with its resolved/missing status,
  // the variant keys we tried, and the resolved MDL source file. This is
  // the fastest way to figure out why so many GEO instances aren't matching
  // any of the package's MDL nodes.
  dumpGeoInstances(): void {
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
  }

  dumpRdrReport(): void {
    const r = this.rdr();
    if (!r) { this.log('[DUMP rdr] no RDR loaded', true); return; }
    this.log('--- RDR report ---', true);
    this.log(`Source: ${r.sourcePath}${r.entry ? ` / ${r.entry}` : ''}`);
    if (r.fallback) {
      this.log(`(fallback) ${r.parseError ?? ''}`);
      const lists = this.rdrFallbackLists();
      if (lists) {
        this.log(`Strings: ${lists.strings.length}, textureRefs: ${lists.textureRefs.length}, fileRefs: ${lists.fileRefs.length}, modelRefs: ${lists.modelRefs.length}`);
        if (lists.textureRefs.length) { this.log('Texture refs:'); for (const s of lists.textureRefs) { this.log('  ' + s); } }
        if (lists.fileRefs.length) { this.log('File refs:'); for (const s of lists.fileRefs) { this.log('  ' + s); } }
        if (lists.modelRefs.length) { this.log('Model refs:'); for (const s of lists.modelRefs.slice(0, 80)) { this.log('  ' + s); } }
      }
    } else {
      this.log(`Version ${r.version}, atoms ${r.atomCount}, pool ${r.stringPoolSize}B`);
      try { this.log(JSON.stringify(r.root, null, 2)); } catch { this.log('(unable to stringify root)'); }
    }
  }
}

// The methods live in one module per archive kind; each runs with `this` as the page, like the class's own.
export interface ViewerPageLogic extends TextureMethods {}
mixin(ViewerPageLogic, textureMethods);

// Match a weapon entry name to the right atlas texture filename.
// Strategy (in order):
//   1. Rules table - picks the PRIMARY atlas for known weapons. Important for
//      multi-texture weapons like m4Acarbine (uses both m4.tif for the body
//      and mark03.tif for the scope) where chunk-frequency would pick the
//      wrong one as default.
//   2. Most-frequent chunk-embedded .tif - fallback when rules don't match
//      (less-known weapons / vehicles / etc.).
//   3. Whole-payload ref scan - last resort.
//   Per-chunk overrides are still applied separately by applyModelToRenderer
//   so individual chunks can use their own texture (e.g. the scope chunk
//   uses mark03 even when the default atlas is m4).
function guessTextureForWeapon(
  weaponName: string,
  sources: Map<string, Uint8Array>,
  model: { chunks: Array<{ textureName: string | null }>; textureRefs: string[] },
): string | null {
  const lower = weaponName.toLowerCase();
  const embedded = chooseEmbeddedWeaponTexture(weaponName, sources, model);
  if (embedded) { return embedded; }

  // Specific matches FIRST so longer / more-specific weapon names win over
  // generic substrings (e.g. "mp5sd" must be checked before "mp5").
  const rules: Array<[string, string]> = [
    // Assault / battle rifles
    ['m4acarbine_203', 'm4.tif'],
    ['m4acarbine_sd', 'm4.tif'],
    ['m4acarbine', 'm4.tif'],
    ['m16_m203', 'm16.tif'],
    ['m16a2', 'm16.tif'],
    ['ak105', 'aks74_s.tif'],
    ['ak47', 'ak47.tif'],
    ['aks74', 'aks74_s.tif'],
    ['m14_gun', 'm14.tif'],
    ['m14', 'm14.tif'],
    ['steyr_aug9', 'steyr.tif'],
    ['steyr', 'steyr.tif'],
    ['iw80a2', 'RA14_groza.tif'],
    ['ra14_groza', 'RA14_groza.tif'],
    ['bmmg_f2k', 'BMMG_f2k.tif'],
    // SMGs
    ['mp5sd', 'mp5.tif'],
    ['mp5', 'mp5.tif'],
    ['fnp90', 'fnp90.tif'],
    ['uzi', 'uzi.tif'],
    ['hk5k', 'mp5.tif'],
    ['sp10_gyuraz', 'SP10_gyuraz.tif'],
    // Sniper rifles
    ['barretm82a1', 'barretm82.tif'],
    ['barret', 'barretm82.tif'],
    ['mcmillanm87', 'mcmillan_m87.tif'],
    ['mcmillan', 'mcmillan_m87.tif'],
    ['stoner_sr25', 'stoner_sr25.tif'],
    ['remington700', 'remington700.tif'],
    ['dragunov', 'drag.tif'],
    // LMGs
    ['stoner_m63a', 'stoner_m63a.tif'],
    ['m60e', 'm60.tif'],
    ['sig_commando', 'sig_commando.tif'],
    // Shotguns
    ['remington870', 'remington870.tif'],
    ['spas12', 'spas12.tif'],
    ['jackhammer', 'jackhammer.tif'],
    // Pistols
    ['m11_p228', 'M11_p228.tif'],
    ['a_mark23sd', 'mark23.tif'],
    ['a_mark23', 'mark23.tif'],
    ['mark23', 'mark23.tif'],
    ['fiveseven', 'fiveseven.tif'],
    ['glock18', 'glock18.tif'],
    ['baretta_m9', 'baretta_m9.tif'],
    ['baretta', 'baretta_m9.tif'],
    ['desert_eagle', 'deserteagle.tif'],
    ['hkp9s', 'p9s.tif'],
    ['sig226', 'sig_p226.tif'],
    // Launchers
    ['m4acarbine_203', 'mark03.tif'],
    ['m203', 'mark03.tif'],
    ['m79', 'm79.tif'],
    ['mglmk1', 'mglmk1.tif'],
    ['mglmk2', 'mglmk2.tif'],
    ['at4_heat', 'at4_text.tif'],
    ['at4', 'at4_text.tif'],
    ['rpg7', 'mp5.tif'],
    ['rpgrenade', 'HEgrenade.tif'],
    // Explosives / mines / utility
    ['pmn_mine', 'pmn_mine.tif'],
    ['pmn', 'pmn_mine.tif'],
    ['c4', 'c401.tif'],
    ['claymore', 'claymore.tif'],
    ['flashbang', 'flashbang.tif'],
    ['hegrenade', 'HEgrenade.tif'],
    ['grenade', 'm79.tif'],
    ['a_smoke_grenade', 'smoke_grenade.tif'],
    ['smoke', 'smoke_grenade.tif'],
    ['detonator', 'a_buttonpanel01.tif'],
    ['fiftycal_box', '50cal_box_top.tif'],
    ['artillery_shell', 'G11b.tif'],
    ['footbomb', 'satchel_tex.tif'],
    ['bleu_chem', 'a_buttonpanel01.tif'],
    // Misc
    ['laser_designator', 'laser_designator.tif'],
  ];

  for (const [needle, tif] of rules) {
    if (lower.includes(needle) && sources.has(tif.toLowerCase())) {
      return tif.toLowerCase();
    }
  }

  // Fallback: most-used chunk-embedded ref.
  const counts = new Map<string, number>();
  for (const c of model.chunks) {
    if (!c.textureName) { continue; }
    const key = c.textureName.toLowerCase();
    if (!sources.has(key)) { continue; }
    counts.set(key, (counts.get(key) ?? 0) + 1);
  }
  let chunkBest: string | null = null;
  let chunkCount = 0;
  for (const [name, count] of counts) {
    if (count > chunkCount) { chunkCount = count; chunkBest = name; }
  }
  if (chunkBest) { return chunkBest; }

  // Last resort: whole-payload ref scan.
  for (const ref of model.textureRefs ?? []) {
    if (sources.has(ref.toLowerCase())) { return ref.toLowerCase(); }
  }

  return null;
}

function chooseEmbeddedWeaponTexture(
  weaponName: string,
  sources: Map<string, Uint8Array>,
  model: { chunks: Array<{ textureName: string | null }>; textureRefs: string[] },
): string | null {
  const candidates: string[] = [];
  const add = (name: string | null) => {
    if (!name) { return; }
    const key = name.toLowerCase();
    if (!sources.has(key) || candidates.includes(key)) { return; }
    candidates.push(key);
  };

  for (const ref of model.textureRefs ?? []) { add(ref); }
  for (const chunk of model.chunks) { add(chunk.textureName); }
  if (candidates.length === 0) { return null; }
  if (candidates.length === 1) { return candidates[0]; }

  const weaponKey = compactTextureName(weaponName);
  const direct = candidates.find(c => {
    const texKey = compactTextureName(c.replace(/\.tif$/i, ''));
    return weaponKey.includes(texKey) || texKey.includes(weaponKey);
  });
  if (direct) { return direct; }

  const nonHelper = candidates.filter(c => !isHelperTextureRef(c));
  if (nonHelper.length === 1) { return nonHelper[0]; }
  if (nonHelper.length > 1) { return null; }

  return candidates[candidates.length - 1];
}

function compactTextureName(value: string): string {
  return value.toLowerCase().replace(/\.tif$/i, '').replace(/[^a-z0-9]+/g, '');
}

function isHelperTextureRef(value: string): boolean {
  const key = compactTextureName(value);
  return key === 'mark03' ||
    key === 'mark03b' ||
    key === 'handle01' ||
    key === 'laserdesignator' ||
    key === 'smokegrenade02' ||
    key === 'abuttonpanel01';
}

function isWeaponPrimaryTexture(weaponName: string, tifName: string): boolean {
  const lower = weaponName.toLowerCase();
  const tif = tifName.toLowerCase();
  const primaries: Array<[string, string]> = [
    ['m4acarbine_203', 'm4.tif'],
    ['m4acarbine_sd', 'm4.tif'],
    ['m4acarbine', 'm4.tif'],
    ['m16_m203', 'm16.tif'],
    ['m16a2', 'm16.tif'],
    ['ak105', 'aks74_s.tif'],
    ['ak47', 'ak47.tif'],
    ['aks74', 'aks74_s.tif'],
    ['m14_gun', 'm14.tif'],
    ['m14', 'm14.tif'],
    ['steyr_aug9', 'steyr.tif'],
    ['steyr', 'steyr.tif'],
    ['iw80a2', 'RA14_groza.tif'],
    ['ra14_groza', 'RA14_groza.tif'],
    ['bmmg_f2k', 'BMMG_f2k.tif'],
    ['sig_commando', 'sig_commando.tif'],
    ['mp5sd', 'mp5.tif'],
    ['mp5', 'mp5.tif'],
    ['fnp90', 'fnp90.tif'],
    ['uzi', 'uzi.tif'],
    ['hk5k', 'mp5.tif'],
    ['sp10_gyuraz', 'SP10_gyuraz.tif'],
    ['barretm82a1', 'barretm82.tif'],
    ['barret', 'barretm82.tif'],
    ['mcmillanm87', 'mcmillan_m87.tif'],
    ['mcmillan', 'mcmillan_m87.tif'],
    ['stoner_sr25', 'stoner_sr25.tif'],
    ['remington700', 'remington700.tif'],
    ['dragunov', 'drag.tif'],
    ['stoner_m63a', 'stoner_m63a.tif'],
    ['m60e', 'm60.tif'],
    ['remington870', 'remington870.tif'],
    ['spas12', 'spas12.tif'],
    ['jackhammer', 'jackhammer.tif'],
    ['m11_p228', 'M11_p228.tif'],
    ['a_mark23sd', 'mark23.tif'],
    ['a_mark23', 'mark23.tif'],
    ['mark23', 'mark23.tif'],
    ['fiveseven', 'fiveseven.tif'],
    ['glock18', 'glock18.tif'],
    ['baretta_m9', 'baretta_m9.tif'],
    ['baretta', 'baretta_m9.tif'],
    ['desert_eagle', 'deserteagle.tif'],
    ['hkp9s', 'p9s.tif'],
    ['sig226', 'sig_p226.tif'],
  ];
  return primaries.some(([needle, primary]) => lower.includes(needle) && tif === primary.toLowerCase());
}

function effectWireOverlayColor(modelName: string): number {
  const lower = modelName.toLowerCase();
  if (lower.includes('tracer')) { return 0xffcd5a; }
  if (lower.includes('shotgun')) { return 0xd24b37; }
  if (lower.includes('shell')) { return 0xd6b450; }
  if (lower.includes('muzzle') || lower.includes('flash')) { return 0xffd36a; }
  return 0x78beff;
}

function toRendererBounds(bounds: Bounds3): ModelBounds3 {
  return {
    minX: bounds.minX,
    minY: bounds.minY,
    minZ: bounds.minZ,
    maxX: bounds.maxX,
    maxY: bounds.maxY,
    maxZ: bounds.maxZ,
  };
}

function descriptorTextureForWeapon(weaponName: string, defaultTif: string, descriptorTif: string): string {
  const lower = weaponName.toLowerCase();
  const fallback = defaultTif.toLowerCase();
  const tex = descriptorTif.toLowerCase();

  if (lower.includes('dragunov') && fallback === 'drag.tif') {
    // Dragunov's WEAP_MDL descriptor table names unrelated weapon atlases
    // (barretm82/mp5/jackhammer/etc.) for earlier chunks. The actual model
    // uses drag.tif plus the small DragBar detail page.
    return tex === 'drag.tif' || tex === 'dragbar.tif' ? tex : fallback;
  }

  return tex;
}

function weaponPrimaryPaletteAlias(weaponName: string, tifName: string): string | null {
  const lower = weaponName.toLowerCase();
  const tif = tifName.toLowerCase();
  for (const rule of WEAP_PALETTE_OVERRIDES) {
    if (!lower.includes(rule.model)) { continue; }
    if (!rule.textures.includes(tif)) { continue; }
    return rule.palette;
  }
  return null;
}

// WEAP_TXR texture header ids are not sufficient for every weapon. Several
// pages are colour variants sharing the same index art; keep confirmed fixes
// here so model binding and palette binding stay separate.
const WEAP_PALETTE_OVERRIDES: Array<{ model: string; textures: string[]; palette: string }> = [
  { model: 'm4acarbine', textures: ['m4.tif'], palette: 'weap:texpal_4' },
  { model: 'm16', textures: ['m16.tif'], palette: 'weap:texpal_13' },
  { model: 'mp5', textures: ['mp5.tif'], palette: 'weap:texpal_6' },
  { model: 'hk5k', textures: ['mp5.tif'], palette: 'weap:texpal_6' },
  { model: 'ak105', textures: ['aks74_s.tif'], palette: 'weap:texpal_14' },
  { model: 'ak105', textures: ['uzi.tif'], palette: 'weap:texpal_25' },
  { model: 'ak105', textures: ['mcmillan_m87.tif'], palette: 'weap:texpal_32' },
  { model: 'ak105', textures: ['drag.tif'], palette: 'weap:texpal_47' },
  { model: 'ak47', textures: ['ak47.tif'], palette: 'weap:texpal_8' },
  { model: 'aks74', textures: ['aks74_s.tif'], palette: 'weap:texpal_14' },
  { model: 'm14', textures: ['m14.tif'], palette: 'weap:texpal_39' },
  { model: 'steyr', textures: ['steyr.tif'], palette: 'weap:texpal_46' },
  { model: 'bmmg_f2k', textures: ['bmmg_f2k.tif'], palette: 'weap:texpal_31' },
  { model: 'ra14_groza', textures: ['ra14_groza.tif'], palette: 'weap:texpal_51' },
  { model: 'iw80a2', textures: ['ra14_groza.tif'], palette: 'weap:texpal_51' },
  { model: 'iw80a2', textures: ['spas12.tif'], palette: 'weap:texpal_42' },
  { model: 'sig_commando', textures: ['sig_commando.tif'], palette: 'weap:texpal_23' },
  { model: 'sig_commando', textures: ['m4.tif'], palette: 'weap:texpal_4' },
  { model: 'fnp90', textures: ['fnp90.tif'], palette: 'weap:texpal_24' },
  { model: 'uzi', textures: ['uzi.tif'], palette: 'weap:texpal_25' },
  { model: 'sp10_gyuraz', textures: ['sp10_gyuraz.tif'], palette: 'weap:texpal_50' },
  { model: 'barret', textures: ['barretm82.tif'], palette: 'weap:texpal_30' },
  { model: 'barret', textures: ['bmmg_f2k.tif'], palette: 'weap:texpal_31' },
  { model: 'barret', textures: ['mark03.tif'], palette: 'weap:texpal_5' },
  { model: 'mcmillan', textures: ['mcmillan_m87.tif'], palette: 'weap:texpal_32' },
  { model: 'mcmillan', textures: ['bmmg_f2k.tif'], palette: 'weap:texpal_31' },
  { model: 'mcmillan', textures: ['mark03.tif'], palette: 'weap:texpal_5' },
  { model: 'mcmillan', textures: ['mark03b.tif'], palette: 'weap:texpal_20' },
  { model: 'stoner_sr25', textures: ['stoner_sr25.tif'], palette: 'weap:texpal_33' },
  { model: 'stoner_sr25', textures: ['bmmg_f2k.tif'], palette: 'weap:texpal_31' },
  { model: 'stoner_sr25', textures: ['mark03.tif'], palette: 'weap:texpal_5' },
  { model: 'remington700', textures: ['remington700.tif'], palette: 'weap:texpal_44' },
  { model: 'remington700', textures: ['bmmg_f2k.tif'], palette: 'weap:texpal_31' },
  { model: 'remington700', textures: ['mark03.tif'], palette: 'weap:texpal_5' },
  { model: 'remington700', textures: ['mark03b.tif'], palette: 'weap:texpal_20' },
  { model: 'hegrenade', textures: ['hegrenade.tif'], palette: 'weap:texpal_2' },
  { model: 'rpgrenade', textures: ['hegrenade.tif'], palette: 'weap:texpal_2' },
  { model: 'rpgrenade', textures: ['mglmk2.tif'], palette: 'weap:texpal_37' },
  { model: 'stoner_m63a', textures: ['stoner_m63a.tif'], palette: 'weap:texpal_27' },
  { model: 'm60e', textures: ['m60.tif'], palette: 'weap:texpal_26' },
  { model: 'jackhammer', textures: ['jackhammer.tif'], palette: 'weap:texpal_22' },
  { model: 'm11_p228', textures: ['m11_p228.tif'], palette: 'weap:texpal_49' },
  { model: 'mark23', textures: ['mark23.tif'], palette: 'weap:texpal_10' },
  { model: 'baretta', textures: ['baretta_m9.tif'], palette: 'weap:texpal_11' },
  { model: 'desert_eagle', textures: ['deserteagle.tif'], palette: 'weap:texpal_19' },
  { model: 'glock18', textures: ['glock18.tif'], palette: 'weap:texpal_20' },
  { model: 'hkp9s', textures: ['p9s.tif'], palette: 'weap:texpal_21' },
  { model: 'sig226', textures: ['sig_p226.tif'], palette: 'weap:texpal_18' },
  { model: 'fiveseven', textures: ['fiveseven.tif'], palette: 'weap:texpal_34' },
  { model: 'dragunov', textures: ['drag.tif'], palette: 'weap:texpal_47' },
  { model: 'artillery_shell', textures: ['g11b.tif'], palette: 'weap:texpal_0' },
  { model: 'grenade', textures: ['g11b.tif'], palette: 'weap:texpal_0' },
  { model: 'grenade', textures: ['m79.tif'], palette: 'weap:texpal_1' },
  { model: 'm203', textures: ['m79.tif'], palette: 'weap:texpal_1' },
  { model: 'm79', textures: ['m79.tif'], palette: 'weap:texpal_1' },
  { model: 'mglmk1', textures: ['mglmk1.tif'], palette: 'weap:texpal_36' },
  { model: 'mglmk2', textures: ['mglmk2.tif'], palette: 'weap:texpal_37' },
  { model: 'at4', textures: ['at4_text.tif'], palette: 'weap:texpal_49' },
  { model: 'at4', textures: ['laser_designator.tif'], palette: 'weap:texpal_40' },
  { model: 'at4', textures: ['mark03.tif'], palette: 'weap:texpal_5' },
  { model: 'rpg7', textures: ['barretm82.tif'], palette: 'weap:texpal_30' },
  { model: 'rpg7', textures: ['mp5.tif'], palette: 'weap:texpal_6' },
  { model: 'rpg7', textures: ['jackhammer.tif'], palette: 'weap:texpal_22' },
  { model: 'rpg7', textures: ['stoner_sr25.tif'], palette: 'weap:texpal_33' },
  { model: 'rpg7', textures: ['laser_designator.tif'], palette: 'weap:texpal_40' },
  { model: 'rpg7', textures: ['m79.tif'], palette: 'weap:texpal_1' },
  { model: 'rpg7', textures: ['mcmillan_m87.tif'], palette: 'weap:texpal_32' },
  { model: 'rpg7', textures: ['mglmk1.tif'], palette: 'weap:texpal_36' },
  { model: 'rpg7', textures: ['mark03.tif'], palette: 'weap:texpal_5' },
  { model: 'claymore', textures: ['claymore.tif'], palette: 'weap:texpal_5' },
  { model: 'detonator', textures: ['claymore.tif'], palette: 'weap:texpal_5' },
  { model: 'flashbang', textures: ['flashbang.tif'], palette: 'weap:texpal_49' },
  { model: 'flashbang', textures: ['smoke_grenade02.tif'], palette: 'weap:texpal_18' },
  { model: 'smoke', textures: ['smoke_grenade.tif'], palette: 'weap:texpal_12' },
  { model: 'footbomb', textures: ['satchel_handle.tif', 'satchel_tex.tif'], palette: 'flib:texpal_34' },
  { model: 'bleu_chem', textures: ['sp10_gyuraz.tif', 'a_buttonpanel01.tif'], palette: 'weap:texpal_33' },
  { model: 'fiftycal_box', textures: ['50cal_box_top.tif', '50cal_box_side.tif'], palette: 'weap:texpal_29' },
  { model: 'laser_designator', textures: ['laser_designator.tif'], palette: 'weap:texpal_40' },
];

// Sky geometry: the sky archive (SP<map>_MDL: sky dome, mist), the map's
// sky / sun / flare nodes, and sky / sun textures.
function isSkyChunk(c: { sourceArchiveName?: string | null; sourceModelName?: string | null; visualKey?: string | null; textureName?: string | null }): boolean {
  if (/^sp\d+$/.test(namespaceFromArchiveName(c.sourceArchiveName) ?? '')) { return true; }
  if (/^(?:sky|thesun|sun|flare|mist\d*)$/i.test(c.sourceModelName ?? '')) { return true; }
  return /^(?:sky|sunglow|sun_|flare|lensflare|cloud)/i.test(c.textureName ?? '');
}

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

// Matrix helpers used by composeHierarchicalModel + GEO scene composition.
// Direct ports of the HTML map viewer's tryReadLocalMatrix / multiplyMatrices
// / identityMatrix — same column-major convention so the two composition
// paths interoperate.
function identityMatrix(): number[] {
  return [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];
}
function multiplyMatrices(a: number[], b: number[]): number[] {
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
function chainOffsetsForChildren(archive: InspectedZedArchive, childIndices: number[]): ChainOffset[] {
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
function tryReadLocalMatrix(bytes: Uint8Array): number[] {
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

function isIdentityMatrix(m: number[]): boolean {
  const id = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];
  return m.length === 16 && m.every((v, i) => Math.abs(v - id[i]) < 1e-9);
}

// Copies of chunk vertices moved by a placement matrix (column-major, as
// transformPoint). Identity returns the same array.
function transformVertices(vertices: ZedModel['vertices'], matrix: number[]): ZedModel['vertices'] {
  if (isIdentityMatrix(matrix)) { return vertices; }
  return vertices.map(v => {
    const [x, y, z] = transformPoint(matrix, v.x, v.y, v.z);
    return { ...v, x, y, z };
  });
}
