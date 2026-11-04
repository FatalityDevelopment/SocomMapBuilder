// The Asset Viewer: browse a disc (or any folder) down into packages and archives, and preview what is there.
// Listings and previews come from the server (VIEWER_LIST / VIEWER_PREVIEW). Models, textures, palettes, maps and
// animation clips of a package are drawn the way the editors draw them: the same asset session commands, mesh
// decoding, texture cache and viewports (EditorViewport, MapViewport, AnimViewport).

import { activePage, computed, host, modalOpen, rememberFolder, rememberedFolder, signal } from '../../shared';
import { AssetClient, pngToRgba, type ModelSummary } from '../model-editor/asset-client';
import { TextureCache } from '../model-editor/model-loader';
import { decodeMesh, type EditorMesh, type MeshWire } from '../model-editor/mesh-data';
import { EditorViewport } from '../model-editor/editor-viewport';
import { AnimViewport, type ClipData } from '../animation-editor/anim-viewport';
import { MapViewport, type MapModel } from '../../viewport/mapViewport';
import { loadMapModels } from '../zipper-core/map-models';
import type { Lighting, Shading, ViewPreset, ViewportCore } from '../zipper-core/viewport-core';
import type { ModelTexturePixels } from '../zipper-core/texture-pixels';
import { samePath, type AiChange, type ViewerFocus } from '../../shared/aiBridge';
import * as api from './viewer-client';
import { RDR_ROOT, type EditStatus, type ItemKind, type Listing, type Place, type Preview, type ViewerItem } from './viewer-client';

export type Stage = 'empty' | 'loading' | 'model' | 'map' | 'clip' | 'image' | 'palette' | 'audio' | 'movie' | 'rdr' | 'mps' | 'text' | 'hex' | 'program' | 'iconsys' | 'geo' | 'clutter' | 'zanim' | 'summary' | 'error';
type ViewportKind = 'model' | 'map' | 'clip';

export interface MenuItem { label: string; action?: () => void; disabled?: () => boolean; shortcut?: string; separator?: boolean }
export interface Menu { id: string; label: string; items: MenuItem[] }

// GS pixel storage modes (TEX0.PSM).
const PSM: Record<string, string> = { '0x00': 'PSMCT32', '0x01': 'PSMCT24', '0x02': 'PSMCT16', '0x0A': 'PSMCT16S', '0x13': 'PSMT8', '0x14': 'PSMT4', '0x1B': 'PSMT8H', '0x24': 'PSMT4HL', '0x2C': 'PSMT4HH' };
const BROWSABLE = new Set<ItemKind>(['folder', 'package', 'models', 'textures', 'palettes', 'geo', 'clips', 'archive']);
const LAST_PLACE = 'mapbuilder.viewer.place';

export const KIND_LABEL: Record<ItemKind, string> = {
  folder: 'Folder', package: 'Package', models: 'Models', textures: 'Textures', palettes: 'Palettes', geo: 'GEO definitions',
  clips: 'Animations', archive: 'Archive', group: 'Group', model: 'Model', texture: 'Texture', palette: 'Palette', clip: 'Animation clip',
  rdr: 'RDR data', mps: 'AI maps', sound: 'Sound', bank: 'Sound bank', movie: 'Movie', image: 'Screen image', icon: 'Memory-card icon',
  iconsys: 'Icon list', program: 'Program', text: 'Text', binary: 'Data',
};

const errText = (err: unknown) => err instanceof Error ? err.message : String(err);
const fileName = (p: string) => p.split(/[\\/]/).filter(Boolean).pop() ?? p;
const isPackagePath = (p: string) => /\.(zdb|cdb)$/i.test(p);

function loadPlace(): Place | null {
  try { const v = JSON.parse(localStorage.getItem(LAST_PLACE) ?? 'null') as Place | null; return v && typeof v.path === 'string' ? v : null; } catch { return null; }
}
function savePlace(p: Place): void {
  try { localStorage.setItem(LAST_PLACE, JSON.stringify({ path: p.path, inner: p.inner })); } catch { /* per-viewer convenience only */ }
}

/** A menu whose items are worked out each time it is drawn. */
function liveMenu(id: string, label: string, items: () => MenuItem[]): Menu {
  return { id, label, get items() { return items(); } };
}

export class AssetViewerLogic {
  readonly listing = signal<Listing | null>(null);
  readonly listBusy = signal(false);
  readonly selected = signal<ViewerItem | null>(null);
  readonly query = signal('');
  readonly kindFilter = signal<ItemKind | ''>('');
  readonly stage = signal<Stage>('empty');
  readonly preview = signal<Preview | null>(null);
  readonly stageError = signal('');
  readonly stageNote = signal('');
  readonly message = signal('');
  readonly messageError = signal(false);
  readonly openMenu = signal<string | null>(null);
  readonly canBack = signal(false);
  readonly canForward = signal(false);

  // Texture / image previews.
  readonly imageUrl = signal('');
  readonly imageSize = signal<[number, number]>([0, 0]);
  readonly zoom = signal<'fit' | 1 | 2 | 4>('fit');
  // Sound previews.
  readonly audioUrl = signal('');
  readonly rate = signal(22050);
  // Movies: the converted MP4 the player streams, and a conversion in progress.
  readonly movieUrl = signal('');
  readonly movieBusy = signal(false);
  readonly movieNote = signal('');
  // Edits: the asset session holding this package's edits (undo, redo, save), its status, and an edit in flight.
  readonly editSession = signal<string | null>(null);
  readonly editStatus = signal<EditStatus | null>(null);
  readonly editBusy = signal(false);
  // Other packages holding unsaved edits (one navigated away from, or one an editor changed).
  readonly unsavedElsewhere = signal<EditStatus[]>([]);
  // 3D previews.
  readonly viewportKind = signal<ViewportKind | null>(null);
  readonly shading = signal<Shading>('textured');
  readonly lighting = signal<Lighting>('baked');
  readonly modelInfo = signal('');
  readonly mapProgress = signal('');
  readonly mapShown = signal(false);
  readonly characters = signal<ModelSummary[]>([]);
  readonly character = signal('');
  readonly clipPlaying = signal(false);
  readonly clipTime = signal(0);
  readonly clipDuration = signal(0);
  // What the AI last changed or asked to see in the preview (editors mark the matching row).
  readonly aiMark = signal<(ViewerFocus & { seq: number }) | null>(null);
  // ZANIM archives: the effect shown in full (an index into the archive's effects).
  readonly zanimEffect = signal<number | null>(null);
  // RDR trees: expanded rows, by path of list indices ("r/0/3").
  readonly rdrOpen = signal<ReadonlySet<string>>(new Set([RDR_ROOT]));

  readonly shadings: Array<{ id: Shading; label: string }> = [
    { id: 'textured', label: 'Textured' }, { id: 'solid', label: 'Solid' }, { id: 'wireframe', label: 'Wire' },
  ];

  readonly visibleItems = computed(() => {
    const l = this.listing();
    if (!l) { return []; }
    const q = this.query().trim().toLowerCase();
    const k = this.kindFilter();
    return l.items.filter(i => (!k || i.kind === k) && (!q || i.name.toLowerCase().includes(q) || (i.detail ?? '').toLowerCase().includes(q)));
  });

  readonly kinds = computed(() => {
    const counts = new Map<ItemKind, number>();
    for (const i of this.listing()?.items ?? []) { counts.set(i.kind, (counts.get(i.kind) ?? 0) + 1); }
    return [...counts.entries()].sort((a, b) => b[1] - a[1]).map(([kind, count]) => ({ kind, count }));
  });

  readonly menus: Menu[] = [
    {
      id: 'file', label: 'File', items: [
        { label: 'Open folder…', action: () => void this.openFolder(), shortcut: 'Ctrl+O' },
        { label: 'Open file…', action: () => void this.openFile() },
        { label: '', separator: true },
        { label: 'Extract selected…', action: () => void this.extractSelected(), disabled: () => !this.canExtract(this.selected()) },
        { label: 'Import into this archive…', action: () => void this.importHere(), disabled: () => !this.canImportHere() },
        { label: 'Replace selected…', action: () => void this.replaceSelected(), disabled: () => !this.canEdit(this.selected()) },
        { label: 'Remove selected', action: () => void this.removeSelected(), disabled: () => !this.canEdit(this.selected()) },
        { label: '', separator: true },
        { label: 'Copy path', action: () => void this.copyPath(), disabled: () => !this.listing() },
        { label: 'Reload', action: () => void this.reload(), shortcut: 'F5', disabled: () => !this.listing() },
      ],
    },
    liveMenu('edit', 'Edit', () => [
      { label: 'Undo', action: () => void this.undo(), shortcut: 'Ctrl+Z', disabled: () => !this.editStatus()?.canUndo },
      { label: 'Redo', action: () => void this.redo(), shortcut: 'Ctrl+Y', disabled: () => !this.editStatus()?.canRedo },
      { label: '', separator: true },
      { label: 'Save changes…', action: () => void this.saveEdits(), shortcut: 'Ctrl+S', disabled: () => !this.editStatus()?.dirty.length },
      { label: 'Replace picture from image…', action: () => void this.replaceImage(), disabled: () => !this.canReplaceImage() },
      { label: 'Palette colours from image…', action: () => void this.paletteFromImage(), disabled: () => !this.canEditPalette() },
      // Packages with unsaved edits elsewhere: going there makes their edits the ones Undo / Save act on.
      ...this.unsavedElsewhere().flatMap((st, i) => [
        ...(i === 0 ? [{ label: '', separator: true }] : []),
        { label: `Go to ${fileName(st.path)} (${st.dirty.length} unsaved)`, action: () => void this.navigate({ path: st.path, inner: [] }).catch(() => undefined) },
      ]),
    ]),
    {
      id: 'go', label: 'Go', items: [
        { label: 'Back', action: () => void this.back(), shortcut: 'Alt+Left', disabled: () => !this.canBack() },
        { label: 'Forward', action: () => void this.forward(), shortcut: 'Alt+Right', disabled: () => !this.canForward() },
        { label: 'Up one level', action: () => void this.up(), shortcut: 'Backspace', disabled: () => !this.parentPlace() },
        { label: '', separator: true },
        { label: 'Open selected', action: () => { const s = this.selected(); if (s) { void this.activate(s); } }, shortcut: 'Enter', disabled: () => !this.selected() },
      ],
    },
    {
      id: 'editors', label: 'Editors', items: [
        { label: 'Open in Map Builder', action: () => this.openInMap(), disabled: () => !this.mapPackage() },
        { label: 'Open in Model Editor', action: () => this.openInModelEditor(), disabled: () => !this.modelEditorTarget() },
        { label: 'Open in Animation Editor', action: () => this.openInAnimationEditor(), disabled: () => !this.animationTarget() },
      ],
    },
  ];

  private history: Place[] = [];
  private future: Place[] = [];
  private canvases: Partial<Record<ViewportKind, HTMLCanvasElement>> = {};
  private modelVp: EditorViewport | null = null;
  private mapVp: MapViewport | null = null;
  private animVp: AnimViewport | null = null;
  private clients = new Map<string, { client: AssetClient; textures: TextureCache }>();
  private previewSeq = 0;
  private listSeq = 0;
  private wav: string | null = null;
  private png: string | null = null;
  private clipTimer = 0;
  private readonly keyHandler = (e: KeyboardEvent) => this.onKey(e);

  mount(): void {
    window.addEventListener('keydown', this.keyHandler);
    this.clipTimer = window.setInterval(() => {
      if (this.animVp && this.viewportKind() === 'clip') { this.clipTime.set(this.animVp.time); this.clipPlaying.set(this.animVp.playing); }
    }, 100);
    const last = loadPlace();
    if (last) { void this.navigate(last, true).catch(() => this.navigate({ path: last.path.replace(/[\\/][^\\/]*$/, '') || last.path, inner: [] }, true).catch(() => undefined)); }
  }

  dispose(): void {
    window.removeEventListener('keydown', this.keyHandler);
    window.clearInterval(this.clipTimer);
    this.revokeUrls();
    this.modelVp?.dispose();
    this.mapVp?.dispose();
    this.animVp?.dispose();
  }

  attachCanvas(kind: ViewportKind, canvas: HTMLCanvasElement | null): void {
    if (canvas) { this.canvases[kind] = canvas; }
  }

  // ------------------------------------------------------------------ navigation

  async navigate(place: Place, replace = false, keepView = false): Promise<void> {
    const seq = ++this.listSeq;
    this.listBusy.set(true);
    try {
      const listing = await api.list(place);
      if (seq !== this.listSeq) { return; }
      const current = this.listing();
      if (current && !replace && !api.samePlace(current, listing)) { this.history.push({ path: current.path, inner: current.inner }); this.future = []; }
      this.listing.set(listing);
      void this.followEditSession(listing.editSession);
      if (keepView) { return; }
      this.query.set('');
      this.kindFilter.set('');
      this.selected.set(null);
      this.syncHistory();
      savePlace(listing);
      if (listing.kind === 'folder') { rememberFolder('viewer', listing.path); }
      this.say(`${listing.name}: ${listing.items.length} item${listing.items.length === 1 ? '' : 's'}`);
      await this.showPreview(null);
    } catch (err) {
      if (seq === this.listSeq) { this.say(`${fileName(place.path)}: ${errText(err)}`, true); }
      throw err;
    } finally {
      if (seq === this.listSeq) { this.listBusy.set(false); }
    }
  }

  private syncHistory(): void { this.canBack.set(this.history.length > 0); this.canForward.set(this.future.length > 0); }

  async back(): Promise<void> {
    const to = this.history.pop();
    const cur = this.listing();
    if (!to) { return; }
    if (cur) { this.future.push({ path: cur.path, inner: cur.inner }); }
    await this.navigate(to, true).catch(() => undefined);
    this.syncHistory();
  }

  async forward(): Promise<void> {
    const to = this.future.pop();
    const cur = this.listing();
    if (!to) { return; }
    if (cur) { this.history.push({ path: cur.path, inner: cur.inner }); }
    await this.navigate(to, true).catch(() => undefined);
    this.syncHistory();
  }

  parentPlace(): Place | null {
    const crumbs = this.listing()?.crumbs ?? [];
    return crumbs.length >= 2 ? crumbs[crumbs.length - 2] : null;
  }

  async up(): Promise<void> {
    const parent = this.parentPlace();
    const here = this.listing();
    if (!parent || !here) { return; }
    await this.navigate(parent).catch(() => undefined);
    // Land on the place we came from.
    const back = this.listing()?.items.find(i => api.samePlace(i, here) || (i.kind === 'folder' && i.path.toLowerCase() === here.path.toLowerCase()));
    if (back) { void this.select(back); }
  }

  async reload(): Promise<void> {
    const l = this.listing();
    const sel = this.selected();
    if (!l) { return; }
    await this.navigate(l, true).catch(() => undefined);
    const again = sel && this.listing()?.items.find(i => api.placeKey(i, i.ref) === api.placeKey(sel, sel.ref));
    if (again) { void this.select(again); }
  }

  async openFolder(): Promise<void> {
    const picked = await host.browse({ mode: 'folder', title: 'Browse a folder in the Asset Viewer', start: this.listing()?.path || rememberedFolder('viewer') || undefined, key: 'viewer', submitLabel: 'Browse' });
    if (picked) { await this.navigate({ path: picked, inner: [] }).catch(() => undefined); }
  }

  async openFile(): Promise<void> {
    const picked = await host.browse({ mode: 'open', title: 'Open in the Asset Viewer', start: this.listing()?.path || rememberedFolder('viewer') || undefined, key: 'viewer', submitLabel: 'Open', allowFolder: true });
    if (picked) { await this.openPath(picked); }
  }

  /** Opens a path from outside (the file browser, another page, the AI): archives open, other files are selected in their folder. */
  async openPath(path: string, missionTable?: string): Promise<void> {
    try {
      await this.navigate({ path, inner: [] });
      if (missionTable) {
        const reader = this.listing()?.items.find(i => i.name.toLowerCase() === 'readerm.zar');
        if (!reader) { this.say('Mission reader not found.', true); return; }
        await this.navigate(reader);
        const record = this.listing()?.items.find(i => i.name.toLowerCase() === missionTable.toLowerCase());
        if (record) await this.select(record);
        else this.say('Mission table not found.', true);
      }
    } catch {
      const dir = path.replace(/[\\/][^\\/]*$/, '');
      await this.navigate({ path: dir, inner: [] }).catch(() => undefined);
      const item = this.listing()?.items.find(i => i.path.toLowerCase() === path.toLowerCase());
      if (item) { await this.select(item); }
    }
  }

  isBrowsable(item: ViewerItem | null): boolean {
    // Records named like archives that hold none (UI/DBUFFER.ZAR) come back as data; models etc. are leaves.
    return !!item && !item.ref && BROWSABLE.has(item.kind);
  }

  async activate(item: ViewerItem): Promise<void> {
    if (this.isBrowsable(item)) { await this.navigate(item).catch(() => undefined); return; }
    if (item.kind === 'model') { this.openInModelEditor(item); return; }
    if (item.kind === 'clip') { this.toggleClip(); }
  }

  // ------------------------------------------------------------------ selection and previews

  async select(item: ViewerItem | null): Promise<void> {
    if (item && this.selected() && api.placeKey(item, item.ref) === api.placeKey(this.selected()!, this.selected()!.ref)) { return; }
    this.selected.set(item);
    this.zanimEffect.set(null);
    await this.showPreview(item);
  }

  moveSelection(delta: number): void {
    const items = this.visibleItems();
    if (!items.length) { return; }
    const sel = this.selected();
    const at = sel ? items.findIndex(i => api.placeKey(i, i.ref) === api.placeKey(sel, sel.ref)) : -1;
    const next = items[Math.max(0, Math.min(items.length - 1, at < 0 ? 0 : at + delta))];
    void this.select(next);
  }

  private async showPreview(item: ViewerItem | null): Promise<void> {
    const seq = ++this.previewSeq;
    const live = () => seq === this.previewSeq;
    this.stageError.set('');
    this.stageNote.set('');
    this.mapShown.set(false);
    this.mapProgress.set('');
    this.pauseClip();
    const l = this.listing();
    // Nothing selected: the place itself (a package's summary and map, an archive's summary).
    const target: ViewerItem | null = item ?? (l && l.kind !== 'folder' ? { path: l.path, inner: l.inner, name: l.name, kind: l.kind, size: l.size } : null);
    if (!target) { this.setStage('empty', null); return; }
    // After an edit the editor stays up (and keeps what it has open) while the new preview loads.
    if (!this.refreshing) { this.stage.set('loading'); }
    try {
      if (target.ref && l?.session) {
        if (target.kind === 'model') { await this.previewModel(l.session, target, live); return; }
        if (target.kind === 'texture') { await this.previewTexture(l.session, target, live); return; }
        if (target.kind === 'clip') { await this.previewClip(l.session, target, live); return; }
        if (target.kind === 'palette') {
          const p = await api.palette(l.session, target.ref.archive!, target.ref.id!);
          if (live()) { this.setStage('palette', p); }
          return;
        }
      }
      const effect = /ZANIM\.ZAR$/i.test(target.name) ? this.zanimEffect() ?? undefined : undefined;
      const p = await api.preview(target, { rate: this.rate(), effect });
      if (!live()) { return; }
      await this.showRawPreview(p, live);
    } catch (err) {
      if (!live()) { return; }
      this.setStage('error', null);
      this.stageError.set(errText(err));
    }
  }

  private async showRawPreview(p: Preview, live: () => boolean): Promise<void> {
    switch (p.type) {
      case 'image':
        this.setImage(p.png, p.width, p.height);
        this.setStage('image', p);
        return;
      case 'icon': {
        const mesh = decodeMesh(p.mesh as MeshWire);
        const pixels: Record<string, ModelTexturePixels> = {};
        if (p.png) {
          const { width, height, rgba } = await pngToRgba(p.png);
          // Black texels are see-through (CodeDesigner's icon renderer discards alpha below 0.05).
          pixels.icon = { width, height, rgba, wrapS: 'clamp', wrapT: 'clamp', alphaTest: 0.05, hasTransparency: rgba.some((v, i) => i % 4 === 3 && v === 0) };
          this.png = p.png;
        }
        if (!live()) { return; }
        this.setStage('model', p);
        this.showMesh(mesh, pixels, `${mesh.chunks[0]?.triangles.length / 3 | 0} triangles`);
        return;
      }
      case 'audio':
        this.setAudio(p.wav);
        this.setStage('audio', p);
        return;
      case 'package':
        this.setStage('summary', p);
        return;
      case 'movie':
        this.movieUrl.set(p.url ?? '');
        this.movieNote.set('');
        this.setStage('movie', p);
        return;
      case 'rdr': if (!this.refreshing) { this.rdrOpen.set(new Set([RDR_ROOT])); } this.setStage('rdr', p); return;
      case 'geo': this.setStage('geo', p); return;
      case 'clutter': this.setStage('clutter', p); return;
      case 'zanim': this.setStage('zanim', p); return;
      case 'iconsys': this.setStage('iconsys', p); return;
      case 'mps': this.setStage('mps', p); return;
      case 'text': this.setStage('text', p); return;
      case 'hex': this.setStage('hex', p); if (p.note) { this.stageNote.set(p.note); } return;
      case 'program': this.setStage('program', p); return;
      case 'palette': this.setStage('palette', p); return;
      default: this.setStage('summary', p);
    }
  }

  private setStage(stage: Stage, p: Preview | null): void {
    if (stage !== 'image') { this.png = stage === 'model' && p?.type === 'icon' ? this.png : null; }
    if (stage !== 'audio') { this.setAudio(null); }
    this.preview.set(p);
    this.stage.set(stage);
    this.viewportKind.set(stage === 'model' ? 'model' : stage === 'map' ? 'map' : stage === 'clip' ? 'clip' : null);
  }

  private clientFor(session: string): { client: AssetClient; textures: TextureCache } {
    let hit = this.clients.get(session);
    if (!hit) {
      const client = new AssetClient();
      client.session = session;
      hit = { client, textures: new TextureCache(client) };
      this.clients.set(session, hit);
    }
    return hit;
  }

  private async meshWithTextures(session: string, archive: string, name: string): Promise<{ mesh: EditorMesh; pixels: Record<string, ModelTexturePixels>; missing: string[] }> {
    const { client, textures } = this.clientFor(session);
    const wire = await client.mesh(archive, name) as MeshWire;
    const mesh = decodeMesh(wire);
    const ns = wire.textureNamespace ?? null;
    const pixels: Record<string, ModelTexturePixels> = {};
    const missing: string[] = [];
    await Promise.all([...new Set(mesh.chunks.map(c => c.texture).filter((t): t is string => !!t))].map(async n => {
      const p = await textures.get(n, ns);
      if (p) { pixels[n.toLowerCase()] = p; } else { missing.push(n); }
    }));
    return { mesh, pixels, missing };
  }

  private async previewModel(session: string, item: ViewerItem, live: () => boolean): Promise<void> {
    const { mesh, pixels, missing } = await this.meshWithTextures(session, item.ref!.archive!, item.ref!.name!);
    if (!live()) { return; }
    const tris = mesh.chunks.reduce((n, c) => n + c.triangles.length / 3, 0);
    const verts = mesh.chunks.reduce((n, c) => n + c.vertexCount, 0);
    const visuals = new Set(mesh.chunks.map(c => c.visual ?? '')).size;
    const facts: api.Fact[] = [
      ['Kind', item.ref?.modelKind ?? 'model'], ['Archive', item.ref!.archive!], ['Chunks', String(mesh.chunks.length)],
      ['Visuals', String(visuals)], ['Vertices', verts.toLocaleString()], ['Triangles', tris.toLocaleString()],
      ['Textures', `${Object.keys(pixels).length} bound${missing.length ? `, ${missing.length} missing` : ''}`],
    ];
    if (mesh.skeleton) { facts.push(['Skeleton', `${mesh.skeleton.length} bones`]); }
    this.setStage('model', { type: 'info', name: item.name, facts });
    if (missing.length) { this.stageNote.set(`Missing textures: ${missing.join(', ')}`); }
    this.showMesh(mesh, pixels, `${verts.toLocaleString()} vertices · ${tris.toLocaleString()} triangles`);
  }

  private async previewTexture(session: string, item: ViewerItem, live: () => boolean): Promise<void> {
    const { client } = this.clientFor(session);
    const { info, png } = await client.texture(item.ref!.name!, item.ref!.archive ?? item.ref!.namespace);
    if (!live()) { return; }
    const facts: api.Fact[] = [
      ['Size', `${info.width}×${info.height}`], ['Format', `${PSM[info.psm ?? ''] ?? info.psm ?? ''} (${info.bpp ?? '?'}-bit)`],
      ['Palette', info.palette === null || info.palette === undefined ? 'none (direct colour)' : `texpal_${info.palette}${info.paletteResolved ? '' : ' (not found)'}`],
      ['Transparency', info.hasTransparency ? (info.alphaBlend ? 'blended' : 'alpha tested') : 'opaque'],
      ['Wrap', info.clamp ? `${info.clamp[0] ? 'clamp' : 'repeat'} / ${info.clamp[1] ? 'clamp' : 'repeat'}` : 'repeat'],
      ['Archive', item.ref!.archive ?? ''],
    ];
    this.setImage(png, info.width ?? 0, info.height ?? 0);
    this.setStage('image', { type: 'info', name: item.name, facts });
  }

  private async previewClip(session: string, item: ViewerItem, live: () => boolean): Promise<void> {
    const { client } = this.clientFor(session);
    if (!this.characters().length || this.charactersSession !== session) {
      this.charactersSession = session;
      const chars = (await client.listModels({ kind: 'character' })).sort((a, b) => a.name.localeCompare(b.name));
      this.characters.set(chars);
      const pick = chars.find(m => /seal|ally|sniper|player/i.test(m.name)) ?? chars[0];
      this.character.set(pick ? `${pick.archive}:${pick.name}` : '');
      this.loadedCharacter = '';
    }
    const clip = await client.animation(item.ref!.name!, item.ref!.source) as ClipData;
    if (!live()) { return; }
    const facts: api.Fact[] = [
      ['Source', clip.source], ['Length', `${clip.duration.toFixed(2)} s`], ['Frames', `${clip.frames} (${clip.samples} samples)`],
      ['Rate', `${clip.fps} fps`], ['Bones', String(clip.bones.length)], ['Editable', clip.editable ? 'yes (the package\'s own)' : 'no (shared MOTION_P.ZAR)'],
    ];
    this.setStage('clip', { type: 'info', name: item.name, facts });
    const vp = this.ensureViewport('clip') as AnimViewport;
    await this.loadCharacter(session, live);
    if (!live()) { return; }
    vp.setClip(clip);
    vp.setTime(0);
    vp.playing = true;
    this.clipDuration.set(clip.duration);
    this.clipPlaying.set(true);
    this.modelInfo.set(`${item.name} on ${this.character().split(':')[1] ?? 'no character'}`);
  }

  private charactersSession = '';
  private loadedCharacter = '';

  private async loadCharacter(session: string, live: () => boolean): Promise<void> {
    const key = this.character();
    const vp = this.ensureViewport('clip') as AnimViewport;
    if (!key) { this.stageNote.set('This package has no character to play the clip on; the skeleton is drawn alone.'); return; }
    if (key === this.loadedCharacter) { return; }
    const cut = key.indexOf(':');
    const { mesh, pixels } = await this.meshWithTextures(session, key.slice(0, cut), key.slice(cut + 1));
    if (!live()) { return; }
    vp.setCharacter(mesh, pixels, !!this.loadedCharacter);
    if (!this.loadedCharacter) { vp.frameCharacter(); }
    this.loadedCharacter = key;
  }

  async pickCharacter(key: string): Promise<void> {
    this.character.set(key);
    const l = this.listing();
    const sel = this.selected();
    if (!l?.session || sel?.kind !== 'clip') { return; }
    const seq = this.previewSeq;
    await this.loadCharacter(l.session, () => seq === this.previewSeq);
  }

  toggleClip(): void {
    if (!this.animVp || this.viewportKind() !== 'clip') { return; }
    this.animVp.playing = !this.animVp.playing;
    this.clipPlaying.set(this.animVp.playing);
  }

  scrubClip(t: number): void {
    if (!this.animVp) { return; }
    this.animVp.playing = false;
    this.animVp.setTime(t);
    this.clipPlaying.set(false);
    this.clipTime.set(t);
  }

  private pauseClip(): void { if (this.animVp) { this.animVp.playing = false; } }

  /** Draws the package's composed map (the Map Builder's scene) in the preview. */
  async showMap(): Promise<void> {
    const p = this.preview();
    if (p?.type !== 'package' || !p.hasMap) { return; }
    const seq = this.previewSeq;
    const live = () => seq === this.previewSeq;
    const { client, textures } = this.clientFor(p.session);
    try {
      this.mapProgress.set('Reading map scene…');
      const [scene, pts] = await Promise.all([client.mapScene(), client.mapPoints().catch(() => ({ points: [] }))]);
      const meshes = new Map<string, MapModel>();
      const pixels: Record<string, ModelTexturePixels> = {};
      const failed = await loadMapModels(client, textures, scene.instances.map(i => i.source).filter((s): s is string => !!s), meshes, pixels,
        (done, total) => { if (live()) { this.mapProgress.set(`Loading models ${done}/${total}…`); } });
      if (!live()) { return; }
      this.viewportKind.set('map');
      this.stage.set('map');
      const vp = this.ensureViewport('map') as MapViewport;
      vp.editable = false;
      vp.setScene(scene.instances, meshes, pixels, pts.points, false);
      vp.frameAll();
      this.mapShown.set(true);
      this.modelInfo.set(`${scene.sceneNamespace.toUpperCase()} — ${scene.instances.length.toLocaleString()} instances · ${pts.points.length} points`);
      if (failed.length) { this.stageNote.set(`${failed.length} model(s) not drawn: ${failed.slice(0, 3).join('; ')}${failed.length > 3 ? '; …' : ''}`); }
    } catch (err) {
      if (live()) { this.stageNote.set(`Map could not be loaded: ${errText(err)}`); }
    } finally {
      if (live()) { this.mapProgress.set(''); }
    }
  }

  hideMap(): void {
    if (this.stage() !== 'map') { return; }
    this.mapShown.set(false);
    this.stage.set('summary');
    this.viewportKind.set(null);
  }

  async pickSample(index: number): Promise<void> {
    const target = this.selected() ?? this.locationItem();
    if (!target) { return; }
    const seq = ++this.previewSeq;
    try {
      const p = await api.preview(target, { sample: index, rate: this.rate() });
      if (seq !== this.previewSeq || p.type !== 'audio') { return; }
      this.setAudio(p.wav, true);
      this.preview.set(p);
    } catch (err) { this.say(errText(err), true); }
  }

  async setRate(rate: number): Promise<void> {
    this.rate.set(rate);
    const p = this.preview();
    if (p?.type === 'audio' && p.samples) { await this.pickSample(p.sample ?? 0); }
  }

  /** Converts the selected .PSS to MP4 with the server's ffmpeg (a minute or so for a long movie), then plays it. */
  async convertMovie(): Promise<void> {
    const item = this.selected();
    if (!item || item.kind !== 'movie' || this.movieBusy()) { return; }
    const seq = this.previewSeq;
    this.movieBusy.set(true);
    this.say(`Converting ${item.name}…`);
    try {
      const r = await api.convertMovie(item.path);
      if (seq !== this.previewSeq) { return; }
      this.movieUrl.set(r.url);
      this.movieNote.set(r.silent ? 'This movie has no sound track the viewer reads; the picture plays without sound.' : '');
      this.say(`${item.name} converted (${formatBytes(r.size)} MP4, kept for next time).`);
    } catch (err) {
      if (seq === this.previewSeq) { this.movieNote.set(errText(err)); this.say(`${item.name}: ${errText(err)}`, true); }
    } finally {
      this.movieBusy.set(false);
    }
  }

  private locationItem(): ViewerItem | null {
    const l = this.listing();
    return l ? { path: l.path, inner: l.inner, name: l.name, kind: l.kind, size: l.size } : null;
  }

  // ------------------------------------------------------------------ viewports

  private ensureViewport(kind: ViewportKind): ViewportCore {
    const canvas = this.canvases[kind];
    if (!canvas) { throw new Error('The preview is not ready yet'); }
    if (kind === 'model') { return this.modelVp ??= this.styled(new EditorViewport(canvas)); }
    if (kind === 'map') { return this.mapVp ??= this.styled(new MapViewport(canvas)); }
    return this.animVp ??= this.styled(new AnimViewport(canvas));
  }

  private styled<T extends ViewportCore>(vp: T): T {
    vp.shading = this.shading();
    vp.setLighting(this.lighting());
    return vp;
  }

  private showMesh(mesh: EditorMesh, pixels: Record<string, ModelTexturePixels>, info: string): void {
    const vp = this.ensureViewport('model') as EditorViewport;
    vp.setBackfaces(this.selected()?.kind === 'icon');
    vp.setMesh(mesh, pixels, false);
    vp.setView('iso');
    vp.frameAll();
    this.modelInfo.set(info);
  }

  private activeViewport(): ViewportCore | null {
    const k = this.viewportKind();
    return k === 'model' ? this.modelVp : k === 'map' ? this.mapVp : k === 'clip' ? this.animVp : null;
  }

  setShading(s: Shading): void {
    this.shading.set(s);
    for (const vp of [this.modelVp, this.mapVp, this.animVp]) { if (vp) { vp.shading = s; } }
  }

  setLighting(l: Lighting): void {
    this.lighting.set(l);
    for (const vp of [this.modelVp, this.mapVp, this.animVp]) { vp?.setLighting(l); }
  }

  view(preset: ViewPreset): void {
    const vp = this.activeViewport();
    if (!vp) { return; }
    vp.setView(preset);
    this.frame();
  }

  frame(): void {
    const k = this.viewportKind();
    if (k === 'model') { this.modelVp?.frameAll(); } else if (k === 'map') { this.mapVp?.frameAll(); } else if (k === 'clip') { this.animVp?.frameCharacter(); }
  }

  // ------------------------------------------------------------------ media

  private revokeUrls(): void {
    if (this.audioUrl()) { URL.revokeObjectURL(this.audioUrl()); }
    if (this.imageUrl()) { URL.revokeObjectURL(this.imageUrl()); }
  }

  private setImage(png: string, w: number, h: number): void {
    if (this.imageUrl()) { URL.revokeObjectURL(this.imageUrl()); }
    this.png = png;
    this.imageUrl.set(URL.createObjectURL(new Blob([base64Bytes(png)], { type: 'image/png' })));
    this.imageSize.set([w, h]);
  }

  private setAudio(wav: string | null, keepIfSame = false): void {
    if (keepIfSame && wav === this.wav) { return; }
    if (this.audioUrl()) { URL.revokeObjectURL(this.audioUrl()); }
    this.wav = wav;
    this.audioUrl.set(wav ? URL.createObjectURL(new Blob([base64Bytes(wav)], { type: 'audio/wav' })) : '');
  }

  // ------------------------------------------------------------------ editing (VIEWER_EDIT, through the package's session)

  private refreshing = false;

  /** Whether the selection (or, with nothing selected, this place) can be edited in place. */
  canEditInPlace(item: ViewerItem | null = this.selected()): boolean {
    const l = this.listing();
    if (!item || !l || item.ref) { return false; }
    if (['folder', 'package', 'group', 'sound', 'bank', 'movie'].includes(item.kind)) { return false; }
    return item.inner.length === 0 ? !!item.editable || (l.editable && api.samePlace(item, l)) : l.editable;
  }

  canReplaceImage(): boolean {
    const s = this.selected();
    return !!s && (s.kind === 'texture' ? !!s.ref && !!this.listing()?.session : (s.kind === 'image' || s.kind === 'icon') && this.canEditInPlace(s));
  }

  private editTarget(): ViewerItem | null {
    const s = this.selected();
    if (s) { return s; }
    const l = this.listing();
    return l && l.kind !== 'folder' ? { path: l.path, inner: l.inner, name: l.name, kind: l.kind, size: l.size, editable: l.editable } : null;
  }

  /** Runs one edit on the selection, then shows the result (the listing and preview read the edited bytes). */
  async applyEdit(op: Record<string, unknown>): Promise<boolean> {
    const target = this.editTarget();
    if (!target || this.editBusy()) { return false; }
    this.editBusy.set(true);
    try {
      const r = await api.edit(target, op);
      this.editSession.set(r.session);
      this.editStatus.set(r.status);
      this.say(`${r.status.canUndo ?? 'Edited'} (unsaved: ${r.status.dirty.join(', ') || 'none'})`);
      await this.refreshAfterEdit();
      return true;
    } catch (err) {
      this.say(errText(err), true);
      return false;
    } finally {
      this.editBusy.set(false);
    }
  }

  // The listing again (sizes, kinds) with the same selection, filters and open RDR branches.
  private async refreshAfterEdit(): Promise<void> {
    const l = this.listing();
    const sel = this.selected();
    if (!l) { return; }
    await this.navigate(l, true, true).catch(() => undefined);
    const again = sel ? this.listing()?.items.find(i => api.placeKey(i, i.ref) === api.placeKey(sel, sel.ref)) ?? null : null;
    this.selected.set(again);
    this.refreshing = true;
    try { await this.showPreview(again); } finally { this.refreshing = false; }
    void this.refreshUnsaved();
  }

  private async followEditSession(session: string | null): Promise<void> {
    if (session === this.editSession() && session) { return; }
    this.editSession.set(session);
    this.editStatus.set(null);
    void this.refreshUnsaved();
    if (!session) { return; }
    try { this.editStatus.set(await this.clientFor(session).client.status() as unknown as EditStatus); } catch { this.editSession.set(null); }
  }

  /** The other sessions with unsaved edits; called after a listing and after each edit, undo, redo and save. */
  async refreshUnsaved(): Promise<void> {
    try {
      const all = await api.sessions();
      const here = this.editSession();
      this.unsavedElsewhere.set(all.filter((st): st is EditStatus => !!st.session && st.session !== here && !!st.path && Array.isArray(st.dirty) && st.dirty.length > 0));
    } catch { /* the badge is a convenience; the edits stay in their sessions */ }
  }

  async undo(): Promise<void> { await this.stepHistory(true); }
  async redo(): Promise<void> { await this.stepHistory(false); }

  private async stepHistory(undo: boolean): Promise<void> {
    const session = this.editSession();
    const st = this.editStatus();
    const what = undo ? st?.canUndo : st?.canRedo;
    if (!session || !what) { return; }
    try {
      const { client } = this.clientFor(session);
      await (undo ? client.undo() : client.redo());
      this.editStatus.set(await client.status() as unknown as EditStatus);
      this.say(`${undo ? 'Undid' : 'Redid'}: ${what}`);
      await this.refreshAfterEdit();
    } catch (err) { this.say(errText(err), true); }
  }

  /** Writes the changed files: a package to a .ZDB you choose (over itself keeps a .bak), loose files into a folder. */
  async saveEdits(): Promise<void> {
    const session = this.editSession();
    const st = this.editStatus();
    if (!session || !st?.dirty.length) { return; }
    const folder = st.path.replace(/[\\/][^\\/]*$/, '');
    const target = st.isZdb
      ? await host.browse({ mode: 'save', title: `Save ${fileName(st.path)}`, filters: [{ label: 'Package', extensions: ['zdb', 'cdb'] }], start: folder, fileName: fileName(st.path), key: 'save', submitLabel: 'Save' })
      : await host.browse({ mode: 'folder', title: `Save ${st.dirty.length} changed file(s) into a folder`, key: 'save', submitLabel: 'Save here' });
    if (!target) { return; }
    try {
      const { client } = this.clientFor(session);
      const r = await client.save({ mode: 'zdb', outputPath: target });
      this.editStatus.set(await client.status() as unknown as EditStatus);
      this.say(`Saved ${r.leaves.join(', ')} → ${r.written.join(', ')}`);
      void this.refreshUnsaved();
    } catch (err) { this.say(`Save: ${errText(err)}`, true); }
  }

  // RDR
  setRdrValue(atom: number, text: string): Promise<boolean> { return this.applyEdit({ op: 'rdrSet', atom, value: text }); }
  duplicateRdr(atom: number): Promise<boolean> { return this.applyEdit({ op: 'rdrDuplicate', atom }); }
  async removeRdr(atom: number, path: string): Promise<boolean> {
    // The removed row's later siblings move up one: their open branches move with them.
    const cut = path.lastIndexOf('/');
    const parent = path.slice(0, cut);
    const at = Number(path.slice(cut + 1));
    const before = this.rdrOpen();
    const after = new Set<string>();
    for (const key of before) {
      if (!key.startsWith(`${parent}/`)) { after.add(key); continue; }
      const rest = key.slice(parent.length + 1);
      const slash = rest.indexOf('/');
      const i = Number(slash < 0 ? rest : rest.slice(0, slash));
      if (i < at) { after.add(key); } else if (i > at) { after.add(`${parent}/${i - 1}${slash < 0 ? '' : rest.slice(slash)}`); }
    }
    this.rdrOpen.set(after);
    const ok = await this.applyEdit({ op: 'rdrRemove', atom });
    if (!ok) { this.rdrOpen.set(before); }
    return ok;
  }

  // AI maps: the header through VIEWER_EDIT; points through the package session's setPoint (as the Map Builder does).
  setMap(map: number, fields: { name?: string; min?: number[]; max?: number[]; scale?: number[] }): Promise<boolean> {
    return this.applyEdit({ op: 'mpsMap', map, ...fields });
  }

  async setPoint(name: string, fields: { newName?: string; team?: number; kind?: string; position?: number[] }): Promise<void> {
    const p = this.preview();
    if (p?.type === 'mps' && p.session) { await this.sessionEdit(p.session, { op: 'setPoint', name, ...fields }, `Changed point ${name}`); }
  }

  /** One ASSET_EDIT through a package session (the editors' ops), then the listing and preview again. */
  private async sessionEdit(session: string, op: Record<string, unknown>, done: string): Promise<boolean> {
    if (this.editBusy()) { return false; }
    this.editBusy.set(true);
    try {
      const { client, textures } = this.clientFor(session);
      await client.edit(op);
      const status = await client.status();
      textures.invalidate(status.revision);
      this.editSession.set(session);
      this.editStatus.set(status as unknown as EditStatus);
      this.say(done);
      await this.refreshAfterEdit();
      return true;
    } catch (err) {
      this.say(errText(err), true);
      return false;
    } finally {
      this.editBusy.set(false);
    }
  }

  // Palettes: 256 colours set through the package session (textures using them redraw).
  canEditPalette(): boolean {
    const s = this.selected();
    const l = this.listing();
    return s?.kind === 'palette' && !!s.ref && !!l?.session && !l.session.startsWith('ca');
  }

  async setPalette(rgba: number[], what: string): Promise<void> {
    const s = this.selected();
    if (!s?.ref || !this.canEditPalette()) { return; }
    await this.sessionEdit(this.listing()!.session!, { op: 'setPalette', archive: s.ref.archive, id: s.ref.id, rgba, label: `${what} in texpal_${s.ref.id}` }, `${what} in texpal_${s.ref.id}`);
  }

  async paletteFromImage(): Promise<void> {
    const s = this.selected();
    if (!s?.ref || !this.canEditPalette()) { return; }
    const picked = await host.pickImage();
    if (!picked) { return; }
    await this.sessionEdit(this.listing()!.session!, { op: 'setPalette', archive: s.ref.archive, id: s.ref.id, pngBase64: picked.base64, label: `texpal_${s.ref.id} from ${picked.name}` }, `texpal_${s.ref.id} from ${picked.name}`);
  }

  // Clutter: the Map Builder's ops on the package session. Yaw and scale only when changed (they drop any tilt).
  async moveClutter(index: number, position: number[], turn?: { yaw: number; scale: number }): Promise<void> {
    const p = this.preview();
    if (p?.type === 'clutter' && p.session) { await this.sessionEdit(p.session, { op: 'moveClutter', clutterIndex: index, position, ...turn }, `Moved clutter #${index}`); }
  }

  async addClutter(model: string, position: number[]): Promise<void> {
    const p = this.preview();
    if (p?.type === 'clutter' && p.session) { await this.sessionEdit(p.session, { op: 'addClutter', model, position }, `Added ${model}`); }
  }

  async deleteClutter(index: number): Promise<void> {
    const p = this.preview();
    if (p?.type === 'clutter' && p.session) { await this.sessionEdit(p.session, { op: 'deleteClutter', clutterIndex: index }, `Removed clutter #${index}`); }
  }

  // GEO scene nodes: the nparams and model_name records of the selected GEO archive.
  setGeoNode(nparams: number, fields: { matrix?: number[]; min?: number[]; max?: number[]; kind?: number; flags?: number }): Promise<boolean> {
    return this.applyEdit({ op: 'nparams', node: nparams, ...fields });
  }

  setModelName(node: number, name: string): Promise<boolean> { return this.applyEdit({ op: 'modelName', node, name }); }

  // ZANIM effects: one shown in full, its fields and argument words written in place.
  async showEffect(index: number): Promise<void> {
    this.zanimEffect.set(index);
    this.refreshing = true;
    try { await this.showPreview(this.selected()); } finally { this.refreshing = false; }
  }

  setZanimField(sequence: number, offset: number, field: string, value: number | number[] | boolean): Promise<boolean> {
    return this.applyEdit({ op: 'zanimField', node: sequence, offset, field, fieldValue: value });
  }

  setZanimWord(sequence: number, offset: number, word: number, value: number): Promise<boolean> {
    return this.applyEdit({ op: 'zanimWord', node: sequence, offset, word, wordValue: value });
  }

  setText(text: string): Promise<boolean> { return this.applyEdit({ op: 'text', text }); }
  patchBytes(offset: number, hex: string): Promise<boolean> { return this.applyEdit({ op: 'bytes', offset, hex }); }
  setIconSys(title1: string, title2: string, icons: string[]): Promise<boolean> { return this.applyEdit({ op: 'iconSys', title1, title2, icons }); }

  /** A texture of the package (its session's replaceTexture), a screen or an icon's texture, from an image file. */
  async replaceImage(): Promise<void> {
    const s = this.selected();
    if (!s || !this.canReplaceImage()) { return; }
    const picked = await host.pickImage();
    if (!picked) { return; }
    if (s.kind === 'texture' && s.ref) {
      await this.sessionEdit(this.listing()!.session!, { op: 'replaceTexture', texture: s.ref.name, namespace: s.ref.namespace ?? undefined, pngBase64: picked.base64 }, `Replaced ${s.name} with ${picked.name}`);
      return;
    }
    await this.applyEdit({ op: 'image', pngBase64: picked.base64 });
  }

  canExportImage(): boolean { return !!this.png && (this.stage() === 'image' || this.stage() === 'model'); }
  canExportAudio(): boolean { return !!this.wav && this.stage() === 'audio'; }

  exportImage(): void {
    if (!this.png) { return; }
    const name = (this.selected()?.name ?? this.listing()?.name ?? 'image').replace(/\.[^.]+$/, '');
    const where = host.saveFile(`${name}.png`, { base64: this.png }, 'image/png');
    this.say(`Saved ${where}`);
  }

  exportAudio(): void {
    if (!this.wav) { return; }
    const p = this.preview();
    const base = (this.selected()?.name ?? this.listing()?.name ?? 'sound').replace(/\.[^.]+$/, '');
    const name = p?.type === 'audio' && p.samples ? `${base}_${(p.sample ?? 0) + 1}` : base;
    const where = host.saveFile(`${name}.wav`, { base64: this.wav }, 'audio/wav');
    this.say(`Saved ${where}`);
  }

  // ------------------------------------------------------------------ editors

  /** The package to open in the Map Builder: a .ZDB / .CDB listed or selected, when it has a map. */
  mapPackage(): string | null {
    const p = this.preview();
    if (p?.type === 'package' && p.hasMap) { return p.package; }
    return null;
  }

  modelEditorTarget(item: ViewerItem | null = this.selected()): { path: string; model?: { archive: string; name: string } } | null {
    const l = this.listing();
    if (!l) { return null; }
    if (item?.kind === 'model' && item.ref && l.package) { return { path: l.package, model: { archive: item.ref.archive!, name: item.ref.name! } }; }
    if ((l.kind === 'models' || l.kind === 'geo' || l.kind === 'textures') && l.package) { return { path: l.package }; }
    const p = this.preview();
    if (p?.type === 'package' && p.models > 0) { return { path: p.package }; }
    return null;
  }

  animationTarget(): string | null {
    const l = this.listing();
    if (l?.kind === 'clips' && l.package) { return l.package; }
    const p = this.preview();
    return p?.type === 'package' && p.characters > 0 ? p.package : null;
  }

  openInMap(): void { const p = this.mapPackage(); if (p) { host.open('map', p); } }

  openInModelEditor(item: ViewerItem | null = this.selected()): void {
    const t = this.modelEditorTarget(item);
    if (t) { host.open('models', t.path, t.model); }
  }

  openInAnimationEditor(): void { const p = this.animationTarget(); if (p) { host.open('animations', p); } }

  // ------------------------------------------------------------------ extract / import / replace / remove

  canExtract(item: ViewerItem | null): boolean { return !!item && !item.ref && item.inner.length > 0; }

  async extractSelected(): Promise<void> {
    const item = this.selected();
    if (!item || !this.canExtract(item)) { return; }
    const safe = item.name.replace(/[<>:"/\\|?*\x00-\x1f]/g, '_') || 'entry.bin';
    const target = await host.browse({ mode: 'save', title: `Extract ${item.name}`, fileName: safe, key: 'extract', submitLabel: 'Extract' });
    if (!target) { return; }
    try {
      const r = await api.extract(item, target);
      this.say(`Extracted ${item.name} (${formatBytes(r.size)}) → ${r.written}`);
    } catch (err) { this.say(`Extract: ${errText(err)}`, true); }
  }

  // Records the viewer can change on disk with PACKAGE_EDIT: a package's files and the records of archives (raw
  // listings only; models, textures and clips are edited in the editors).
  canEdit(item: ViewerItem | null): boolean {
    const l = this.listing();
    return !!item && !!l && !item.ref && item.inner.length > 0 && !l.session?.startsWith('ca') && this.editChain(item.inner.slice(0, -1)) !== null;
  }

  canImportHere(): boolean {
    const l = this.listing();
    return !!l && l.kind !== 'folder' && !l.items.some(i => i.ref) && this.editChain(l.inner) !== null;
  }

  // The PACKAGE_EDIT "inside" chain for the archive at `inner` (null: not editable): in a package, the root entry's
  // path then {name, index} per nested record; in a loose .ZED / .ZAR, only the records.
  private editChain(inner: number[]): unknown[] | null {
    const l = this.listing();
    if (!l) { return null; }
    const pkg = isPackagePath(l.path);
    if (!pkg && !/\.(zed|zar)$/i.test(l.path)) { return null; }
    const crumbs = l.crumbs.filter(c => c.path.toLowerCase() === l.path.toLowerCase() && c.inner.length > 0);
    const chain: unknown[] = [];
    for (let k = 0; k < inner.length; k++) {
      const crumb = crumbs.find(c => c.inner.length === k + 1);
      if (!crumb) { return null; }
      if (pkg && k === 0) { if (!crumb.entryPath) { return null; } chain.push(crumb.entryPath); } else { chain.push({ name: crumb.label, index: inner[k] }); }
    }
    return chain;
  }

  private async runEdit(label: string, ops: unknown[]): Promise<void> {
    const l = this.listing();
    if (!l) { return; }
    const ext = (/\.[^.\\/]+$/.exec(l.path)?.[0] ?? '.zdb').slice(1);
    const target = await host.browse({ mode: 'save', title: `${label}: save as`, filters: [{ label: `${ext.toUpperCase()} file`, extensions: [ext] }], start: l.path.replace(/[\\/][^\\/]*$/, ''), fileName: fileName(l.path), key: 'save', submitLabel: 'Save' });
    if (!target) { return; }
    try {
      this.say(`${label}…`);
      const r = await api.packageEdit(l.path, target, ops);
      const parts = [r.added.length ? `added ${r.added.join(', ')}` : '', r.replaced.length ? `replaced ${r.replaced.join(', ')}` : '', r.removed.length ? `removed ${r.removed.join(', ')}` : ''].filter(Boolean).join('; ');
      await this.navigate({ path: r.written, inner: l.inner }, true).catch(() => this.navigate({ path: r.written, inner: [] }, true));
      this.say(`${label}: ${parts || 'no change'} → ${r.written}`);
    } catch (err) { this.say(`${label}: ${errText(err)}`, true); }
  }

  private recordOp(item: ViewerItem, op: 'replace' | 'remove', file?: string): unknown | null {
    const l = this.listing();
    if (!l) { return null; }
    if (isPackagePath(l.path) && item.inner.length === 1) {
      return item.entryPath ? { op, path: item.entryPath, ...(file ? { file } : {}) } : null;
    }
    const inside = this.editChain(item.inner.slice(0, -1));
    return inside ? { op, inside, index: item.inner[item.inner.length - 1], name: item.name, ...(file ? { file } : {}) } : null;
  }

  async replaceSelected(): Promise<void> {
    const item = this.selected();
    if (!item || !this.canEdit(item)) { return; }
    const file = await host.browse({ mode: 'open', title: `Replace ${item.name} with…`, key: 'import', submitLabel: 'Replace' });
    const op = file && this.recordOp(item, 'replace', file);
    if (op) { await this.runEdit(`Replace ${item.name}`, [op]); }
  }

  async removeSelected(): Promise<void> {
    const item = this.selected();
    if (!item || !this.canEdit(item)) { return; }
    const ok = window.confirm(`Remove ${item.name}? The archive is written to a file you choose next (the same file keeps a .bak).`);
    const op = ok && this.recordOp(item, 'remove');
    if (op) { await this.runEdit(`Remove ${item.name}`, [op]); }
  }

  async importHere(): Promise<void> {
    const l = this.listing();
    if (!l || !this.canImportHere()) { return; }
    const file = await host.browse({ mode: 'open', title: `Import into ${l.name}`, key: 'import', submitLabel: 'Import' });
    if (!file) { return; }
    if (isPackagePath(l.path) && l.inner.length === 0) {
      // Package files keep the folder their siblings have inside the package.
      const sibling = l.items.find(i => i.entryPath)?.entryPath ?? '';
      const dir = sibling.includes('\\') || sibling.includes('/') ? sibling.replace(/[\\/][^\\/]*$/, '') + '\\' : '';
      await this.runEdit(`Import ${fileName(file)}`, [{ op: 'add', path: dir + fileName(file).toUpperCase(), file }]);
      return;
    }
    const inside = this.editChain(l.inner);
    if (inside) { await this.runEdit(`Import ${fileName(file)}`, [{ op: 'add', inside, name: fileName(file), file }]); }
  }

  async copyPath(): Promise<void> {
    const l = this.listing();
    if (!l) { return; }
    const sel = this.selected();
    const text = sel && sel.inner.length === 0 ? sel.path : [l.path, ...l.crumbs.filter(c => c.inner.length > 0).map(c => c.label), ...(sel ? [sel.name] : [])].join(' › ');
    try { await navigator.clipboard.writeText(text); this.say(`Copied ${text}`); } catch { this.say(text); }
  }

  // ------------------------------------------------------------------ menus, keys, status

  toggleMenu(id: string): void { this.openMenu.set(this.openMenu() === id ? null : id); }
  hoverMenu(id: string): void { if (this.openMenu() && this.openMenu() !== id) { this.openMenu.set(id); } }
  runMenu(item: MenuItem): void { this.openMenu.set(null); if (!item.disabled?.()) { item.action?.(); } }
  closeMenusOutside(e: PointerEvent): void {
    if (this.openMenu() && !(e.target instanceof Element && e.target.closest('.me-menu'))) { this.openMenu.set(null); }
  }

  private onKey(e: KeyboardEvent): void {
    if (activePage() !== 'viewer' || modalOpen()) { return; }
    const t = e.target as HTMLElement | null;
    const typing = !!t && (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.tagName === 'SELECT' || t.isContentEditable);
    // The 3D preview's own keys (W/A/S/D fly, F frame) belong to it while it has focus.
    const inCanvas = t?.tagName === 'CANVAS';
    let handled = true;
    if (e.ctrlKey && e.key.toLowerCase() === 'o') { void this.openFolder(); }
    else if (e.key === 'F5') { void this.reload(); }
    else if (e.ctrlKey && !typing && e.key.toLowerCase() === 'z') { void this.undo(); }
    else if (e.ctrlKey && !typing && e.key.toLowerCase() === 'y') { void this.redo(); }
    else if (e.ctrlKey && e.key.toLowerCase() === 's') { void this.saveEdits(); }
    else if (e.altKey && e.key === 'ArrowLeft') { void this.back(); }
    else if (e.altKey && e.key === 'ArrowRight') { void this.forward(); }
    else if (typing || inCanvas) { handled = false; }
    else if (e.key === 'Backspace') { void this.up(); }
    else if (e.key === 'ArrowDown') { this.moveSelection(1); }
    else if (e.key === 'ArrowUp') { this.moveSelection(-1); }
    else if (e.key === 'PageDown') { this.moveSelection(15); }
    else if (e.key === 'PageUp') { this.moveSelection(-15); }
    else if (e.key === 'Enter') { const s = this.selected(); if (s) { void this.activate(s); } }
    else if (e.key === ' ' && this.viewportKind() === 'clip') { this.toggleClip(); }
    else if (e.key === 'Escape') { this.openMenu.set(null); }
    else { handled = false; }
    if (handled) { e.preventDefault(); }
  }

  say(text: string, error = false): void { this.message.set(text); this.messageError.set(error); }

  // ------------------------------------------------------------------ following the AI

  private markSeq = 0;

  /** An AI change or show request: go where it points (with Follow AI on), and reload whenever the package shown changed. */
  async followAi(change: AiChange): Promise<void> {
    const v = change.focus?.viewer;
    if (change.page === 'viewer' && v) { await this.reveal(v, change.path, change.edited); return; }
    if (change.page === 'viewer' && change.path && !samePath(change.path, this.listing()?.path)) { await this.openPath(change.path); return; }
    if (change.edited && this.showsPackage(change.path)) { await this.refreshAfterEdit(); await this.refreshEditStatus(); }
  }

  private showsPackage(path: string | null): boolean {
    const l = this.listing();
    const trim = (p: string | null | undefined) => p?.replace(/[\\/]+$/, '');
    return !!l && !!path && [l.editPackage, l.package, l.path].some(p => samePath(trim(p), trim(path)));
  }

  private async refreshEditStatus(): Promise<void> {
    const session = this.listing()?.editSession ?? this.editSession();
    if (!session) { return; }
    try {
      this.editSession.set(session);
      this.editStatus.set(await this.clientFor(session).client.status() as unknown as EditStatus);
    } catch { /* the session closed */ }
    void this.refreshUnsaved();
  }

  private async reveal(v: ViewerFocus, packagePath: string | null, edited: boolean): Promise<void> {
    const go = async (place: Place) => {
      const l = this.listing();
      if (l && api.samePlace(l, place)) { if (edited) { await this.navigate(place, true, true).catch(() => undefined); } return true; }
      return this.navigate(place).then(() => true, () => false);
    };
    let item: ViewerItem | undefined;
    if (v.archive && (v.palette != null || v.texture) && packagePath) {
      // A palette or texture of the package: its _PAL / _TXR.ZED listing, then the item.
      const l = this.listing();
      const kind = v.palette != null ? 'palettes' : 'textures';
      if (!(l?.kind === kind && l.name.toLowerCase() === v.archive.toLowerCase() && samePath(l.path, packagePath))) {
        if (!await go({ path: packagePath, inner: [] })) { return; }
        const archive = this.listing()?.items.find(i => i.name.toLowerCase() === v.archive!.toLowerCase());
        if (!archive || !await go(archive)) { return; }
      } else if (edited) { await go(l); }
      item = v.palette != null
        ? this.listing()?.items.find(i => i.ref?.id === v.palette)
        : this.listing()?.items.find(i => i.name.toLowerCase() === v.texture!.toLowerCase());
    } else if (v.path) {
      const place: Place = { path: v.path, inner: v.inner ?? [] };
      if (v.open) { if (await go(place) && edited) { await this.refreshAfterEdit(); } return; }
      if (v.palette != null) {
        // viewer_preview of a palette: the _PAL.ZED listing itself, then the palette.
        if (!await go(place)) { return; }
        item = this.listing()?.items.find(i => i.ref?.id === v.palette);
      } else {
        const parent: Place = place.inner.length ? { path: place.path, inner: place.inner.slice(0, -1) } : { path: place.path.replace(/[\\/][^\\/]*$/, ''), inner: [] };
        if (!await go(parent)) { return; }
        item = this.listing()?.items.find(i => !i.ref && (place.inner.length ? api.samePlace(i, place) : samePath(i.path, place.path)));
      }
    }
    if (!item) { return; }
    const sel = this.selected();
    if (sel && api.placeKey(sel, sel.ref) === api.placeKey(item, item.ref)) {
      if (edited) { await this.refreshAfterEdit(); }
    } else {
      await this.select(item);
    }
    const p = this.preview();
    if (v.sequence != null && p?.type === 'zanim') {
      const index = p.effects.findIndex(e => e.sequence === v.sequence);
      if (index >= 0 && (index !== this.zanimEffect() || edited)) { await this.showEffect(index); }
    }
    if (v.atom != null) { this.openRdrTo(v.atom); }
    if (edited) { await this.refreshEditStatus(); }
    this.aiMark.set({ ...v, seq: ++this.markSeq });
  }

  // Opens the RDR rows down to an atom (keys as RdrRow builds them: a field's list shares the field's path).
  private openRdrTo(atom: number): void {
    const p = this.preview();
    if (p?.type !== 'rdr') { return; }
    type ListNode = [number, 'list', number, api.RdrTree[]];
    const isList = (n: api.RdrTree): n is ListNode => n[1] === 'list';
    const isField = (n: api.RdrTree) => isList(n) && n[3].length === 2 && n[3][0][1] === 'string';
    const find = (n: api.RdrTree, path: string): string[] | null => {
      if (n[0] === atom) { return []; }
      if (!isList(n)) { return null; }
      let children = n[3];
      if (isField(n)) {
        if (children[0][0] === atom || children[1][0] === atom) { return []; }
        if (!isList(children[1])) { return null; }
        children = children[1][3];
      }
      for (let i = 0; i < children.length; i++) {
        const below = find(children[i], `${path}/${i}`);
        if (below) { return [path, ...below]; }
      }
      return null;
    };
    const keys = find(p.tree, RDR_ROOT);
    if (keys) { this.rdrOpen.set(new Set([...this.rdrOpen(), ...keys])); }
  }

  /** What ui_state reports for the Asset Viewer. */
  aiState(): unknown {
    const l = this.listing();
    const s = this.selected();
    const p = this.preview();
    const st = this.editStatus();
    return {
      listing: l ? { path: l.path, inner: l.inner, name: l.name, kind: l.kind, items: l.items.length, session: l.session, editable: l.editable } : null,
      selected: s ? { name: s.name, kind: s.kind, path: s.path, inner: s.inner, ref: s.ref ?? null } : null,
      stage: this.stage(),
      preview: p ? { type: p.type, name: p.name, effect: p.type === 'zanim' && p.effect ? p.effect.name : undefined } : null,
      edits: st ? { session: st.session, path: st.path, revision: st.revision, unsaved: st.dirty, canUndo: st.canUndo, canRedo: st.canRedo } : null,
      unsavedElsewhere: this.unsavedElsewhere().map(x => ({ session: x.session, path: x.path, unsaved: x.dirty })),
    };
  }
}

function base64Bytes(b64: string): Uint8Array {
  const bin = atob(b64);
  const out = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) { out[i] = bin.charCodeAt(i); }
  return out;
}

export function formatBytes(n: number): string {
  if (n < 1024) { return `${n} B`; }
  if (n < 1024 * 1024) { return `${(n / 1024).toFixed(n < 10240 ? 1 : 0)} KB`; }
  if (n < 1024 * 1024 * 1024) { return `${(n / 1024 / 1024).toFixed(1)} MB`; }
  return `${(n / 1024 / 1024 / 1024).toFixed(2)} GB`;
}
