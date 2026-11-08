// Animation Editor logic, moved from CodeDesigner's Angular component: every character clip of a package (its
// MOTION_S.ZAR, editable) and the shared MOTION_P.ZAR (read-only, copy in to change), played on a character from the
// package, plus a weapon's firing effects. Creating, renaming, deleting, retiming and keying clips are asset-session
// edits (addAnimation / renameAnimation / deleteAnimation / setAnimation / setAnimationKey), shared with the other
// pages. The React view (AnimationEditorPage.tsx) renders it; signals come from the shared shim.

import { activePage, computed, host, modalOpen, signal } from '../../shared';
import type { ModelTexturePixels } from '../zipper-core/texture-pixels';
import type { Shading, ViewPreset } from '../zipper-core/viewport-core';
import { AssetClient, type AnimationSummary, type CharacterGear, type FxInstruction, type FxNode, type GearItem, type ModelSummary, type SessionStatus, type WeaponSummary } from '../model-editor/asset-client';
import { TextureCache } from '../model-editor/model-loader';
import { decodeMesh, type EditorMesh, type MeshWire } from '../model-editor/mesh-data';
import { AnimViewport, type AnimTool, type AttachmentSpec, type ClipData, type ShotEffect } from './anim-viewport';

export interface MenuItem {
  label: string;
  shortcut?: string;
  action?: () => void;
  disabled?: () => boolean;
  checked?: () => boolean;
  separator?: boolean;
}

export interface Menu { id: string; label: string; items: MenuItem[] }

const PACKAGE_SOURCE = 'MOTION_S.ZAR';
// Models the user attached to clips, per clip name: kept in this viewer's browser (the game binds models through
// character.rdr's gear and motion.rdr's callbacks on the disc, which a package does not carry).
const ATTACH_KEY = 'mb.animation-editor.attachments';
type SavedAttachment = Pick<AttachmentSpec, 'id' | 'label' | 'model' | 'bone' | 'position' | 'rotation' | 'visible'>;

function loadSaved(): Record<string, SavedAttachment[]> {
  try { const v = JSON.parse(localStorage.getItem(ATTACH_KEY) ?? '{}') as Record<string, SavedAttachment[]>; return v && typeof v === 'object' ? v : {}; } catch { return {}; }
}
function storeSaved(all: Record<string, SavedAttachment[]>): void {
  try { localStorage.setItem(ATTACH_KEY, JSON.stringify(all)); } catch { /* per-viewer convenience only */ }
}

export class AnimationEditorPageLogic {
  private readonly client = new AssetClient();
  private readonly textures = new TextureCache(this.client);
  viewport!: AnimViewport;
  private pollTimer: ReturnType<typeof setInterval> | null = null;

  readonly shadings: Array<{ id: Shading; label: string }> = [
    { id: 'textured', label: 'Textured' }, { id: 'solid', label: 'Solid' }, { id: 'wireframe', label: 'Wire' }, { id: 'xray', label: 'X-ray' },
  ];
  readonly tools: Array<{ id: AnimTool; label: string; key: string; glyph: string }> = [
    { id: 'select', label: 'Select bone', key: 'Q', glyph: '⬚' },
    { id: 'rotate', label: 'Rotate bone (key)', key: 'R', glyph: '⟳' },
    { id: 'move', label: 'Move bone (key)', key: 'G', glyph: '✥' },
  ];

  readonly status = signal<SessionStatus | null>(null);
  readonly clips = signal<AnimationSummary[]>([]);
  readonly models = signal<ModelSummary[]>([]);
  readonly current = signal<AnimationSummary | null>(null);
  readonly clip = signal<ClipData | null>(null);
  readonly characterKey = signal('');
  readonly hasCharacter = signal(false);
  readonly openMenu = signal<string | null>(null);
  readonly query = signal('');
  readonly prefix = signal('');
  readonly source = signal('');
  readonly tool = signal<AnimTool>('select');
  readonly shading = signal<Shading>('textured');
  readonly showSkeleton = signal(true);
  readonly showMesh = signal(true);
  readonly playing = signal(false);
  readonly loop = signal(true);
  readonly speed = signal(1);
  readonly time = signal(0);
  readonly frame = signal(0);
  readonly selectedBone = signal<string | null>(null);
  readonly boneVersion = signal(0);
  readonly loading = signal('');
  readonly busy = signal(false);
  readonly message = signal('');
  readonly messageIsError = signal(false);
  readonly hover = signal<string | null>(null);
  readonly weapons = signal<WeaponSummary[]>([]);
  readonly weaponKey = signal('');
  readonly autoFire = signal(false);
  readonly fxTree = signal<FxNode[]>([]);
  readonly skipped = signal<string[]>([]);
  // Worn gear (character.rdr) and models attached to the clip.
  readonly gearTypes = signal<CharacterGear['types']>([]);
  readonly gearTable = signal<GearItem[]>([]);
  readonly gearSource = signal<string | null>(null);
  readonly charType = signal('');
  readonly showGear = signal(true);
  readonly showWeapon = signal(true);
  readonly attachments = signal<AttachmentSpec[]>([]);
  readonly selectedAttachment = signal<string | null>(null);
  readonly attachPick = signal('');
  readonly attachBone = signal('rhand');
  readonly allModels = signal<ModelSummary[]>([]);
  readonly gameShown = computed(() => { this.time(); this.attachments(); return this.viewport?.gameShown() ?? new Map<string, boolean>(); });
  readonly motion = computed(() => this.clip()?.motion ?? null);
  readonly weaponInfo = signal<{ name: string; model: string; fireAnim: string; spawns: string[]; flash: string | null; shell: string | null; fireWait: number } | null>(null);

  readonly dirtyCount = computed(() => this.status()?.dirty.length ?? 0);
  // SOCOM II's skinned meshes are MESH_*; in SOCOM 3 / CA packages every character model is skinned.
  readonly characters = computed(() => this.models().filter(m => m.kind === 'character' && (!!this.status()?.game || /^MESH_/i.test(m.name))));
  readonly prefixes = computed(() => {
    const counts = new Map<string, number>();
    for (const c of this.clips()) { const p = c.name.split('_')[0] + '_'; counts.set(p, (counts.get(p) ?? 0) + 1); }
    return [...counts].filter(([, n]) => n >= 3).map(([p]) => p).sort();
  });
  // Other games (status.game set): clips grouped by their source file.
  readonly sources = computed(() => {
    if (!this.status()?.game) { return []; }
    const counts = new Map<string, number>();
    for (const c of this.clips()) { counts.set(c.source, (counts.get(c.source) ?? 0) + 1); }
    return [...counts].map(([name, count]) => ({ name, count }));
  });
  readonly groups = computed(() => {
    const q = this.query().toLowerCase(), pre = this.prefix(), src = this.source();
    const list = this.clips().filter(c => (!q || c.name.toLowerCase().includes(q)) && (!pre || c.name.startsWith(pre)) && (!src || c.source === src));
    if (this.sources().length > 1) {
      const bySource = new Map<string, AnimationSummary[]>();
      for (const c of list) { bySource.set(c.source, [...(bySource.get(c.source) ?? []), c]); }
      return [...bySource].map(([title, clips]) => ({ title, clips }));
    }
    return [
      { title: 'Package (MOTION_S)', clips: list.filter(c => c.editable) },
      { title: 'Shared (MOTION_P, read-only)', clips: list.filter(c => !c.editable) },
    ].filter(g => g.clips.length);
  });
  readonly bones = computed(() => { this.boneVersion(); this.clip(); return this.viewport?.boneNames() ?? []; });
  readonly editable = computed(() => !!this.current()?.editable);
  readonly canWrite = computed(() => !!this.status() && !this.status()?.readOnly && !this.status()?.game);
  readonly boneLocal = computed(() => { this.frame(); this.boneVersion(); this.selectedBone(); return this.viewport?.selectedLocal() ?? null; });

  readonly menus: Menu[] = [
    {
      id: 'file', label: 'File', items: [
        { label: 'Open package…', shortcut: 'Ctrl+O', action: () => this.pickPackage() },
        { separator: true, label: '' },
        { label: 'Save copy…', shortcut: 'Ctrl+S', action: () => void this.saveZdbCopy(), disabled: () => !this.dirtyCount() },
        { label: 'Save to disc image…', action: () => void this.saveDisc(), disabled: () => !this.dirtyCount() },
        { label: 'Export memory-card pack…', action: () => void this.exportPack(), disabled: () => !!this.status()?.game || !this.dirtyCount() },
      ],
    },
    {
      id: 'edit', label: 'Edit', items: [
        { label: 'Undo', shortcut: 'Ctrl+Z', action: () => void this.history(true), disabled: () => !this.status()?.canUndo },
        { label: 'Redo', shortcut: 'Ctrl+Y', action: () => void this.history(false), disabled: () => !this.status()?.canRedo },
      ],
    },
    {
      id: 'anim', label: 'Animation', items: [
        { label: 'New from seal_stand…', shortcut: 'Ctrl+N', action: () => void this.newClip(), disabled: () => !this.canWrite() },
        { label: 'Duplicate / copy into package…', shortcut: 'Ctrl+D', action: () => void this.duplicateClip(), disabled: () => !this.current() || !this.canWrite() },
        { label: 'Rename…', shortcut: 'F2', action: () => void this.renameClip(), disabled: () => !this.editable() },
        { label: 'Delete', shortcut: 'Delete', action: () => void this.deleteClip(), disabled: () => !this.editable() },
        { separator: true, label: '' },
        { label: 'Play / pause', shortcut: 'Space', action: () => this.togglePlay(), disabled: () => !this.clip() },
        { label: 'Loop', action: () => this.setLoop(!this.loop()), checked: () => this.loop() },
      ],
    },
    {
      id: 'view', label: 'View', items: [
        { label: 'Front', action: () => this.view('front') },
        { label: 'Back', action: () => this.view('back') },
        { label: 'Side', action: () => this.view('right') },
        { label: 'Frame character', shortcut: 'F', action: () => this.viewport.frameCharacter() },
        { separator: true, label: '' },
        { label: 'Skeleton', action: () => this.setSkeleton(!this.showSkeleton()), checked: () => this.showSkeleton() },
        { label: 'Mesh', action: () => this.setMesh(!this.showMesh()), checked: () => this.showMesh() },
      ],
    },
    {
      id: 'help', label: 'Help', items: [
        { label: 'Space play / pause · , and . step frames · Home / End first / last frame' },
        { label: 'Click a joint to select a bone · R rotate · G move · Q select (gizmo drags key the current frame)' },
        { label: 'Orbit: middle drag or Alt+left drag · Pan: right drag · Zoom: wheel' },
        { label: 'Keyboard orbit: arrows (hold, Shift faster) · Numpad 4 / 6 / 8 / 2 steps · Numpad 9 opposite · Numpad + / − zoom' },
        { label: 'Shared MOTION_P clips are read-only: Copy into package, then edit the copy' },
      ],
    },
  ];

  constructor() {
    window.addEventListener('keydown', this.onKey);
  }

  // Called once the React view has its canvas (Angular's ngAfterViewInit).
  mount(canvas: HTMLCanvasElement): void {
    this.viewport = new AnimViewport(canvas, {
      onBoneSelect: b => { this.selectedBone.set(b); this.boneVersion.update(v => v + 1); },
      onTime: t => this.syncTime(t),
      onKey: (bone, frame, key) => void this.commitKey(bone, frame, key),
      onHover: h => this.hover.set(h),
    });
  }

  dispose(): void {
    window.removeEventListener('keydown', this.onKey);
    if (this.pollTimer) { clearInterval(this.pollTimer); }
    this.viewport?.dispose();
  }

  // ------------------------------------------------------------------ messaging

  private async askName(title: string, value: string): Promise<string | null> {
    const text = await host.askText(title, value, '^[A-Za-z0-9_]{1,63}$', '1-63 letters, digits or _');
    return text ? text : null;
  }

  private say(text: string, error = false): void {
    this.message.set(text);
    this.messageIsError.set(error);
    host.log(text);
  }

  toggleMenu(id: string): void { this.openMenu.set(this.openMenu() === id ? null : id); }
  hoverMenu(id: string): void { if (this.openMenu()) { this.openMenu.set(id); } }
  runMenu(item: MenuItem): void { this.openMenu.set(null); item.action?.(); }
  closeMenusOutside(e: Event): void { if (!(e.target as HTMLElement).closest('.me-menu')) { this.openMenu.set(null); } }

  private readonly onKey = (e: KeyboardEvent): void => {
    // Every page listens on the window; only the one in front (with no prompt open) takes keys.
    if (activePage() !== 'animations' || modalOpen()) { return; }
    const target = e.target as HTMLElement;
    if (target && ['INPUT', 'SELECT', 'TEXTAREA'].includes(target.tagName)) { return; }
    const ctrl = e.ctrlKey || e.metaKey;
    const k = e.key.toLowerCase();
    let handled = true;
    if (ctrl && k === 'z') { void this.history(true); }
    else if (ctrl && (k === 'y' || (e.shiftKey && k === 'z'))) { void this.history(false); }
    else if (ctrl && k === 's') { void this.saveZdbCopy(); }
    else if (ctrl && k === 'o') { this.pickPackage(); }
    else if (ctrl && k === 'n') { void this.newClip(); }
    else if (ctrl && k === 'd') { void this.duplicateClip(); }
    else if (ctrl) { handled = false; }
    else if (e.key === ' ') { this.togglePlay(); }
    else if (e.key === 'Enter') { this.fire(); }
    else if (e.key === ',') { this.stepFrame(-1); }
    else if (e.key === '.') { this.stepFrame(1); }
    else if (e.key === 'Home') { this.viewport.setFrame(0); }
    else if (e.key === 'End') { this.viewport.setFrame((this.clip()?.samples ?? 1) - 1); }
    else if (e.key === 'F2') { void this.renameClip(); }
    else if (e.key === 'Delete') { void this.deleteClip(); }
    else if (k === 'f') { this.viewport.frameCharacter(); }
    else if (k === 'q') { this.setTool('select'); }
    else if (k === 'r') { this.setTool('rotate'); }
    else if (k === 'g') { this.setTool('move'); }
    else if (e.key === 'Escape') { this.openMenu.set(null); this.viewport.selectBone(null); }
    else { handled = false; }
    if (handled) { e.preventDefault(); }
  };

  // ------------------------------------------------------------------ view / playback

  setTool(t: AnimTool): void {
    if (t !== 'select' && !this.editable()) { this.say('Shared clips are read-only: copy this one into the package to key it.', true); t = 'select'; }
    this.tool.set(t);
    this.viewport.tool = t;
  }
  setShading(s: Shading): void { this.shading.set(s); this.viewport.shading = s; }
  setSkeleton(on: boolean): void { this.showSkeleton.set(on); this.viewport.setSkeletonVisible(on); }
  setMesh(on: boolean): void { this.showMesh.set(on); this.viewport.setMeshVisible(on); }
  view(p: ViewPreset): void { this.viewport.setView(p); this.viewport.frameCharacter(); }
  togglePlay(): void { if (!this.clip()) { return; } this.viewport.playing = !this.viewport.playing; this.playing.set(this.viewport.playing); }
  setSpeed(v: number): void { this.speed.set(v); this.viewport.speed = v; }
  setLoop(v: boolean): void { this.loop.set(v); this.viewport.loop = v; }
  stepFrame(d: number): void { this.pause(); this.viewport.setFrame(this.viewport.frame + d); }
  scrub(v: string): void { this.pause(); this.viewport.setFrame(+v); }
  private pause(): void { this.viewport.playing = false; this.playing.set(false); }
  private syncTime(t: number): void {
    this.time.set(t);
    this.frame.set(this.viewport.frame);
    if (!this.viewport.playing && this.playing()) { this.playing.set(false); }
  }

  pickBone(name: string): void { this.viewport.selectBone(name); }

  // ------------------------------------------------------------------ weapon

  private async meshAndPixels(archive: string, name: string): Promise<{ mesh: EditorMesh; pixels: Record<string, ModelTexturePixels>; locators: Array<{ name: string; matrix: number[] }> }> {
    const wire = await this.client.mesh(archive, name) as MeshWire;
    const mesh = decodeMesh(wire);
    const pixels: Record<string, ModelTexturePixels> = {};
    await Promise.all([...new Set(mesh.chunks.map(c => c.texture).filter((t): t is string => !!t))].map(async n => {
      const p = await this.textures.get(n, wire.textureNamespace ?? null);
      if (p) { pixels[n.toLowerCase()] = p; }
    }));
    return { mesh, pixels, locators: wire.locators ?? [] };
  }

  async pickWeapon(model: string): Promise<void> {
    this.weaponKey.set(model);
    if (!model) {
      this.fxTree.set([]);
      this.viewport.setWeapon(null, {}, []);
      this.viewport.setEffect(null, {});
      this.weaponInfo.set(null);
      this.setAutoFire(false);
      return;
    }
    const w = this.weapons().find(x => x.model === model);
    if (!w) { return; }
    try {
      const gun = await this.meshAndPixels(w.archive, w.model);
      this.viewport.setWeapon(gun.mesh, gun.pixels, gun.locators);
      const fx = await this.client.weaponFx(w.model);
      const all: FxNode[] = [];
      const walk = (n: FxNode | null | undefined) => { if (!n) { return; } all.push(n); (n.spawns ?? []).forEach(walk); };
      walk(fx.effect);
      const models = all.flatMap(n => n.models ?? []);
      this.fxTree.set(all);
      const flashRef = models.find(m => /^muzzle_flash/i.test(m.name)) ?? null;
      const shellRef = models.find(m => /^bullet_shell/i.test(m.name)) ?? null;
      // Meshes for every model the effect tree uses (flash, shell); the script itself drives them (zanim-player).
      const pixels: Record<string, ModelTexturePixels> = {};
      const meshes = new Map<string, EditorMesh>();
      const seen = new Set<string>();
      for (const ref of models) {
        if (seen.has(ref.name)) { continue; }
        seen.add(ref.name);
        try {
          const got = await this.meshAndPixels(ref.archive, ref.name);
          meshes.set(ref.name, got.mesh);
          Object.assign(pixels, got.pixels);
        } catch { /* a model that does not decode is left out */ }
      }
      this.viewport.setEffect({ root: fx.effect, models: meshes }, pixels);
      this.viewport.fireWait = w.fireWait > 0 ? w.fireWait : 0.12;
      this.weaponInfo.set({
        name: w.internalName || w.model, model: w.model, fireAnim: w.fireAnimName, fireWait: w.fireWait,
        spawns: (fx.effect?.spawns ?? []).map(n => n.name), flash: flashRef?.name ?? null, shell: shellRef?.name ?? null,
      });
    } catch (err) {
      this.say(`${model}: ${errText(err)}`, true);
    }
  }

  setShowWeapon(on: boolean): void {
    this.showWeapon.set(on);
    this.viewport.setWeaponVisible(on);
    if (!on) { this.setAutoFire(false); }
  }

  fire(): void {
    if (!this.weaponKey() || !this.showWeapon()) { return; }
    this.viewport.fire();
    setTimeout(() => this.skipped.set([...this.viewport.skippedOps].sort()), 300);
  }

  refNames(n: FxNode): string { return (n.nodeRefs ?? []).slice(1).map((r, k) => `${k + 1}=${r.name}`).join(', '); }

  fieldText(f: Record<string, unknown>): string {
    return Object.entries(f ?? {}).filter(([k]) => k !== 'flags').map(([k, v]) =>
      `${k}=${Array.isArray(v) ? '(' + v.map(x => +Number(x).toFixed(3)).join(', ') + ')' : typeof v === 'number' ? +v.toFixed(4) : v}`).join(' ');
  }
  setAutoFire(on: boolean): void { this.autoFire.set(on); this.viewport.autoFire = on; }

  // ------------------------------------------------------------------ gear and attachments

  /** The character types drawn with this mesh and the gear table (READERC.ZAR › character.rdr). */
  private async loadGear(mesh: string): Promise<void> {
    if (this.status()?.game) { this.gearTypes.set([]); this.gearTable.set([]); return; }
    try {
      const g = await this.client.characterGear(mesh);
      this.gearTypes.set(g.types);
      this.gearTable.set(g.gear);
      this.gearSource.set(g.source);
      const keep = g.types.find(t => t.name === this.charType());
      this.charType.set(keep?.name ?? g.types[0]?.name ?? '');
      if (g.note) { this.say(g.note, true); }
      await this.applyGear();
    } catch (err) {
      this.gearTypes.set([]);
      this.gearTable.set([]);
      this.say(`Gear: ${errText(err)}`, true);
    }
  }

  async pickCharType(name: string): Promise<void> { this.charType.set(name); await this.applyGear(); }

  async setShowGear(on: boolean): Promise<void> { this.showGear.set(on); await this.applyGear(); }

  /** Puts the chosen type's default_gear on the character, each on its bone at its gear-table offset. */
  private async applyGear(): Promise<void> {
    this.viewport.clearAttachments('gear');
    const type = this.gearTypes().find(t => t.name === this.charType());
    const specs: AttachmentSpec[] = [];
    if (this.showGear() && type) {
      for (const name of type.gear) {
        const item = this.gearTable().find(g => g.name.toLowerCase() === name.toLowerCase());
        if (!item?.model) { continue; }
        // A weapon worn on the body (the satchel charge's bag) only shows on a type that carries the weapon; it stays
        // in the list, unticked, so it can be shown.
        const carried = !item.weapon || (type.weapons ?? []).some(w => w.toLowerCase() === item.weapon!.toLowerCase());
        const spec: AttachmentSpec = {
          id: 'gear:' + item.name, label: item.name, kind: 'gear', model: item.model, bone: item.bone,
          position: [...item.position], rotation: [...item.rotation], visible: carried, objects: [item.model.slice(item.model.indexOf(':') + 1)],
          hiddenAtStart: item.hiddenAtStart,
        };
        if (await this.mountAttachment(spec)) { specs.push(spec); }
      }
    }
    this.attachments.set([...specs, ...this.attachments().filter(a => a.kind === 'user')]);
  }

  private async mountAttachment(spec: AttachmentSpec): Promise<boolean> {
    const cut = spec.model.indexOf(':');
    try {
      const got = await this.meshAndPixels(spec.model.slice(0, cut), spec.model.slice(cut + 1));
      this.viewport.setAttachment(spec, got.mesh, got.pixels);
      return true;
    } catch (err) {
      this.say(`${spec.label}: ${errText(err)}`, true);
      return false;
    }
  }

  /** The current clip's attached models (saved per clip in this viewer). */
  private async applyUserAttachments(): Promise<void> {
    if (!this.viewport) { return; }
    this.viewport.clearAttachments('user');
    const clip = this.current()?.name;
    const saved = clip ? loadSaved()[clip] ?? [] : [];
    const specs: AttachmentSpec[] = [];
    for (const a of saved) {
      const spec: AttachmentSpec = { ...a, kind: 'user', objects: [a.model.slice(a.model.indexOf(':') + 1)] };
      if (await this.mountAttachment(spec)) { specs.push(spec); }
    }
    this.attachments.set([...this.attachments().filter(a => a.kind === 'gear'), ...specs]);
    if (!this.attachments().some(a => a.id === this.selectedAttachment())) { this.selectedAttachment.set(null); }
  }

  private storeUserAttachments(): void {
    const clip = this.current()?.name;
    if (!clip) { return; }
    const all = loadSaved();
    const mine = this.attachments().filter(a => a.kind === 'user')
      .map(({ id, label, model, bone, position, rotation, visible }) => ({ id, label, model, bone, position, rotation, visible }));
    if (mine.length) { all[clip] = mine; } else { delete all[clip]; }
    storeSaved(all);
  }

  /** What the attach picker offers: gear-table items (with their own bone and offset) and the package's models. */
  readonly attachChoices = computed(() => {
    const gear = this.gearTable().filter(g => g.model).map(g => ({ value: 'gear:' + g.name, label: `${g.name} (${g.bone})`, group: 'Gear table' }));
    const models = this.allModels().filter(m => ['character', 'prop', 'weapon', 'library'].includes(m.kind) && !/^MESH_/i.test(m.name))
      .map(m => ({ value: m.archive + ':' + m.name, label: m.name, group: m.archive }));
    return [...gear, ...models];
  });

  /** Attaches the picked model to the current clip: a gear-table item on its own bone and offset, else on the chosen bone. */
  async attach(): Promise<void> {
    const pick = this.attachPick(), clip = this.current();
    if (!pick || !clip) { this.say('Pick a clip and a model to attach.', true); return; }
    const item = pick.startsWith('gear:') ? this.gearTable().find(g => 'gear:' + g.name === pick) : undefined;
    const model = item?.model ?? pick;
    const label = item?.name ?? model.slice(model.indexOf(':') + 1);
    let id = 'user:' + label, n = 2;
    while (this.attachments().some(a => a.id === id)) { id = `user:${label}${n++}`; }
    const spec: AttachmentSpec = {
      id, label, kind: 'user', model, bone: item?.bone ?? this.attachBone(),
      position: item ? [...item.position] : [0, 0, 0], rotation: item ? [...item.rotation] : [0, 0, 0], visible: true,
      objects: [model.slice(model.indexOf(':') + 1)],
    };
    if (!(await this.mountAttachment(spec))) { return; }
    this.attachments.set([...this.attachments(), spec]);
    this.selectedAttachment.set(id);
    this.storeUserAttachments();
    this.say(`Attached ${label} to ${spec.bone} on ${clip.name}.`);
  }

  private changeAttachment(id: string, change: (a: AttachmentSpec) => AttachmentSpec): void {
    let changed: AttachmentSpec | null = null;
    this.attachments.set(this.attachments().map(a => a.id === id ? (changed = change(a)) : a));
    if (!changed) { return; }
    this.viewport.updateAttachment(changed);
    if ((changed as AttachmentSpec).kind === 'user') { this.storeUserAttachments(); }
  }

  setAttachmentVisible(id: string, on: boolean): void { this.changeAttachment(id, a => ({ ...a, visible: on })); }
  setAttachmentBone(id: string, bone: string): void { this.changeAttachment(id, a => ({ ...a, bone })); }
  setAttachmentValue(id: string, kind: 'position' | 'rotation', axis: number, value: string): void {
    const v = parseFloat(value);
    if (!Number.isFinite(v)) { return; }
    this.changeAttachment(id, a => { const next = [...a[kind]]; next[axis] = v; return { ...a, [kind]: next }; });
  }

  detach(id: string): void {
    const a = this.attachments().find(x => x.id === id);
    if (!a) { return; }
    this.viewport.removeAttachment(id);
    this.attachments.set(this.attachments().filter(x => x.id !== id));
    if (this.selectedAttachment() === id) { this.selectedAttachment.set(null); }
    if (a.kind === 'user') { this.storeUserAttachments(); }
  }

  /** Every bone an attachment can go on: the skeleton's and the clip's mounts. */
  readonly attachBones = computed(() => this.bones().map(b => b.name));

  jumpTo(time: number): void { this.pause(); this.viewport.setTime(time); }

  // ------------------------------------------------------------------ fire effect edits

  /** Whether an effect field can be edited here: numbers, vectors and flags (objects and names stay as they are). */
  fieldEditable(fx: FxNode, ins: FxInstruction, key: string): boolean {
    const v = ins.fields[key];
    // IF / ELSEIF terms shape the script (ZanimFile.WithField refuses them).
    return this.canWrite() && fx.found && (fx.sequence ?? -1) >= 0 && ins.offset !== undefined && key !== 'flags' && key !== 'terms'
      && (typeof v === 'number' || typeof v === 'boolean' || (Array.isArray(v) && v.length === 3));
  }

  /** One value of a weapon effect's script (setEffectField), then the effect reloaded and fired to show it. */
  async setEffectField(fx: FxNode, ins: FxInstruction, key: string, value: number | boolean | number[]): Promise<void> {
    if (ins.offset === undefined) { return; }
    const ok = await this.edit(`set ${key} of ${fx.name} (${ins.name})`, { op: 'setEffectField', effect: fx.name, offset: ins.offset, field: key, value });
    if (ok && this.weaponKey()) { await this.pickWeapon(this.weaponKey()); this.fire(); }
  }

  // ------------------------------------------------------------------ loading

  pickPackage(): void { void host.pickPackage().then(path => { if (path) { void this.openPackage(path); } }); }

  async openPackage(path: string): Promise<void> {
    this.loading.set(`Opening ${this.fileName(path)}…`);
    try {
      const status = await this.client.open(path);
      this.status.set(status);
      this.textures.invalidate(status.revision);
      this.models.set(await this.client.listModels({ kind: 'character' }));
      await this.refreshClips();
      const chars = this.characters();
      // SOCOM's SEAL; GTA's swat ped (its first ped, male01, is a placeholder blob).
      const seal = chars.find(c => /seal/i.test(c.name) && !/_\d+(_LO)?$/i.test(c.name)) ?? chars.find(c => c.name.toLowerCase() === 'swat') ?? chars[0];
      if (seal) { await this.loadCharacter(`${seal.archive}:${seal.name}`); } else { this.hasCharacter.set(false); }
      let weaponNote = '';
      try { this.weapons.set(await this.client.weapons()); } catch (err) { this.weapons.set([]); weaponNote = ` No weapons: ${errText(err)}`; }
      try { this.allModels.set(await this.client.listModels()); } catch { this.allModels.set([]); }
      const m4 = this.weapons().find(w => w.model === 'm4Acarbine' || w.model === 'm4a1' || w.model === 'm4') ?? this.weapons()[0];
      if (m4) { await this.pickWeapon(m4.model); }
      const first = this.clips().find(c => c.name === 'seal_stand') ?? this.clips().find(c => c.name.toLowerCase() === 'idle_stance') ?? this.clips()[0];
      if (first) { await this.selectClip(first, true); }
      this.say(`${status.path}: ${this.clips().length} animations (${this.clips().filter(c => c.editable).length} in the package), ${chars.length} characters.${weaponNote}`, !!weaponNote);
      if (!this.pollTimer) { this.pollTimer = setInterval(() => void this.poll(), 2000); }
    } catch (err) {
      this.say(`Could not open ${path}: ${errText(err)}`, true);
    } finally {
      this.loading.set('');
    }
  }

  private async refreshClips(): Promise<void> {
    const before = new Set(this.clips().map(c => `${c.source}:${c.name}`));
    const list = await this.client.listAnimations();
    this.clips.set(list.clips);
    const cur = this.current();
    if (cur) {
      // Gone (an undone rename / add, or an agent's edit): follow the clip that appeared, else drop the selection.
      this.current.set(list.clips.find(c => c.name === cur.name && c.source === cur.source)
        ?? list.clips.find(c => c.name === cur.name)
        ?? list.clips.find(c => c.editable && !before.has(`${c.source}:${c.name}`))
        ?? null);
    }
  }

  async pickCharacter(key: string): Promise<void> { await this.loadCharacter(key); }

  private async loadCharacter(key: string): Promise<void> {
    const cut = key.indexOf(':');
    this.characterKey.set(key);
    this.loading.set(`Loading ${key.slice(cut + 1)}…`);
    try {
      const wire = await this.client.mesh(key.slice(0, cut), key.slice(cut + 1)) as MeshWire;
      const mesh: EditorMesh = decodeMesh(wire);
      const ns = wire.textureNamespace ?? null;
      const pixels: Record<string, ModelTexturePixels> = {};
      await Promise.all([...new Set(mesh.chunks.map(c => c.texture).filter((t): t is string => !!t))].map(async n => {
        const p = await this.textures.get(n, ns);
        if (p) { pixels[n.toLowerCase()] = p; }
      }));
      this.viewport.clearAttachments();
      this.viewport.setCharacter(mesh, pixels, this.hasCharacter());
      this.viewport.setClip(this.clip());
      this.hasCharacter.set(!!mesh.skeleton);
      this.boneVersion.update(v => v + 1);
      if (mesh.skeleton) { await this.loadGear(key.slice(cut + 1)); await this.applyUserAttachments(); }
    } catch (err) {
      this.say(`${key}: ${errText(err)}`, true);
    } finally {
      this.loading.set('');
    }
  }

  isCurrent(c: AnimationSummary): boolean { const cur = this.current(); return !!cur && cur.name === c.name && cur.source === c.source; }

  async selectClip(c: AnimationSummary, play = false): Promise<void> {
    this.current.set(c);
    await this.loadClip(play);
    await this.applyUserAttachments();
  }

  private async loadClip(play: boolean, keepTime = false): Promise<void> {
    const c = this.current();
    if (!c) { this.clip.set(null); this.viewport.setClip(null); return; }
    try {
      const data = await this.client.animation(c.name, c.source) as ClipData;
      if (this.current() !== c) { return; }
      const t = this.viewport.time;
      this.clip.set(data);
      this.viewport.setClip(data);
      if (keepTime) { this.viewport.setTime(Math.min(t, data.duration)); } else { this.viewport.setTime(0); }
      if (play) { this.viewport.playing = true; this.playing.set(true); }
      if (!c.editable && this.tool() !== 'select') { this.setTool('select'); }
      this.boneVersion.update(v => v + 1);
    } catch (err) {
      this.say(`${c.name}: ${errText(err)}`, true);
    }
  }

  // Edits made by anyone on the session (an agent, the other editors) show up here.
  private async poll(): Promise<void> {
    if (!this.client.session || this.busy() || this.loading()) { return; }
    try {
      const status = await this.client.status();
      const before = this.status()?.revision;
      this.status.set(status);
      if (before !== undefined && status.revision !== before) {
        await this.refreshClips();
        await this.loadClip(this.viewport.playing, true);
        this.say(`Session changed (revision ${status.revision}${status.canUndo ? `: ${status.canUndo}` : ''}).`);
      }
    } catch { /* retried next poll */ }
  }

  // ------------------------------------------------------------------ edits (all through the bridge)

  private async edit(label: string, op: Record<string, unknown>, select?: { name: string; source: string }): Promise<boolean> {
    this.busy.set(true);
    try {
      const raw = await this.client.edit({ label, ...op }) as unknown as { result?: { revision: number; description?: string }; revision?: number; description?: string };
      const result = raw.result ?? (raw as { revision: number; description?: string });
      this.status.set(await this.client.status());
      this.say(`${result.description ?? label} (revision ${result.revision})`);
      await this.refreshClips();
      if (select) {
        const c = this.clips().find(x => x.name === select.name && x.source === select.source);
        if (c) { this.current.set(c); }
      }
      await this.loadClip(false, true);
      return true;
    } catch (err) {
      this.say(`${label}: ${errText(err)}`, true);
      this.viewport.clearOverride();
      return false;
    } finally {
      this.busy.set(false);
    }
  }

  private uniqueName(base: string): string {
    const names = new Set(this.clips().map(c => c.name.toLowerCase()));
    if (!names.has(base.toLowerCase())) { return base; }
    for (let i = 2; ; i++) { const n = `${base}${i}`; if (!names.has(n.toLowerCase())) { return n; } }
  }

  async newClip(): Promise<void> {
    if (!this.canWrite()) { return; }
    const name = await this.askName('New animation (a copy of seal_stand to start from)', this.uniqueName('my_anim'));
    if (!name) { return; }
    await this.edit(`add animation ${name}`, { op: 'addAnimation', name, from: 'seal_stand' }, { name, source: PACKAGE_SOURCE });
  }

  async duplicateClip(): Promise<void> {
    const c = this.current();
    if (!c || !this.canWrite()) { return; }
    const name = await this.askName(c.editable ? `Duplicate ${c.name} as` : `Copy ${c.name} into the package as`, this.uniqueName(c.editable ? `${c.name}_copy` : c.name));
    if (!name) { return; }
    await this.edit(`${c.editable ? 'duplicate' : 'copy'} ${c.name} as ${name}`, { op: 'addAnimation', name, from: c.name, fromSource: c.source, after: c.editable ? c.name : undefined },
      { name, source: PACKAGE_SOURCE });
  }

  async renameClip(): Promise<void> {
    const c = this.current();
    if (!c?.editable) { return; }
    const name = await this.askName(`Rename ${c.name} to`, c.name);
    if (!name || name === c.name) { return; }
    await this.edit(`rename ${c.name} to ${name}`, { op: 'renameAnimation', name: c.name, newName: name }, { name, source: PACKAGE_SOURCE });
  }

  async deleteClip(): Promise<void> {
    const c = this.current();
    if (!c?.editable) { return; }
    const ok = await this.edit(`delete ${c.name}`, { op: 'deleteAnimation', name: c.name });
    if (ok) {
      const next = this.clips().find(x => x.name === c.name) ?? this.clips().find(x => x.editable) ?? this.clips()[0] ?? null;
      this.current.set(next);
      await this.loadClip(false);
    }
  }

  async setDuration(v: string): Promise<void> {
    const c = this.current(), d = parseFloat(v);
    if (!c?.editable || !(d > 0)) { return; }
    await this.edit(`set ${c.name} duration to ${d}s`, { op: 'setAnimation', name: c.name, duration: d });
  }

  async setFrames(v: string): Promise<void> {
    const c = this.current(), n = parseInt(v, 10);
    if (!c?.editable || !(n >= 1) || n === c.frames) { return; }
    await this.edit(`resample ${c.name} to ${n} frames`, { op: 'setAnimation', name: c.name, frames: n });
  }

  private async commitKey(bone: string, frame: number, key: { rotation?: number[]; position?: number[] }): Promise<void> {
    const c = this.current();
    if (!c?.editable) { this.viewport.clearOverride(); return; }
    await this.edit(`key ${bone} at frame ${frame}`, { op: 'setAnimationKey', name: c.name, bone, frame, ...key });
  }

  async setBoneValue(kind: 'position' | 'rotation', axis: number, value: string): Promise<void> {
    const bone = this.selectedBone(), l = this.boneLocal(), v = parseFloat(value);
    if (!bone || !l || !Number.isFinite(v)) { return; }
    this.pause();
    const frame = this.viewport.frame;
    if (kind === 'position') {
      const p = [...l.position]; p[axis] = v;
      await this.commitKey(bone, frame, { position: p });
    } else {
      const e = this.eulerOf(l.rotation); e[axis] = v;
      await this.commitKey(bone, frame, { rotation: quatFromEuler(e) });
    }
  }

  async holdPose(): Promise<void> {
    const c = this.current(), bone = this.selectedBone(), l = this.boneLocal();
    if (!c?.editable || !bone || !l) { return; }
    await this.edit(`hold ${bone} for all frames`, { op: 'setAnimationKey', name: c.name, bone, allFrames: true, rotation: l.rotation });
  }

  private async history(undo: boolean): Promise<void> {
    if (!this.client.session) { return; }
    this.busy.set(true);
    try {
      const r = undo ? await this.client.undo() : await this.client.redo();
      this.status.set(await this.client.status());
      this.say(`${undo ? 'Undid' : 'Redid'} ${r.description ?? ''} (revision ${r.revision})`);
      await this.refreshClips();
      await this.loadClip(false, true);
      // An undone effect edit: fire with the script as it is now.
      if (this.weaponKey() && /effect|set \w+ of/i.test(r.description ?? '')) { await this.pickWeapon(this.weaponKey()); }
    } catch (err) {
      this.say(errText(err), true);
    } finally {
      this.busy.set(false);
    }
  }

  async saveZdbCopy(): Promise<void> {
    const s = this.status();
    if (!s || !this.dirtyCount()) { return; }
    const path = await host.pickPackageCopy(s);
    if (path) { await this.save({ mode: 'zdb', outputPath: path }); }
  }

  // The edited files written into a copy of the disc image.
  async saveDisc(): Promise<void> {
    const s = this.status();
    if (!s || !this.dirtyCount()) { return; }
    const picked = await host.pickDiscCopy(s);
    if (picked) { await this.save({ mode: 'zdb', ...picked }); }
  }

  async exportPack(): Promise<void> {
    const picked = await host.pickSavePath('pack', 'MANIM');
    if (!picked.packName) { return; }
    await this.save({ mode: 'pack', packName: picked.packName, outputPath: picked.path ?? undefined });
  }

  private async save(args: { mode: 'zdb' | 'pack'; outputPath?: string; packName?: string; sourceIso?: string }): Promise<void> {
    this.busy.set(true);
    try {
      const r = await this.client.save(args);
      this.say(`Wrote ${r.written.join(', ')} (${r.leaves.join(', ')})${r.warnings?.length ? '. ' + r.warnings.join(' ') : ''}`);
    } catch (err) {
      this.say(`Save failed: ${errText(err)}`, true);
    } finally {
      this.busy.set(false);
    }
  }

  // ------------------------------------------------------------------ helpers

  eulerOf(q: number[]): number[] {
    const [x, y, z, w] = q;
    const rx = Math.atan2(2 * (w * x + y * z), 1 - 2 * (x * x + y * y));
    const ry = Math.asin(Math.max(-1, Math.min(1, 2 * (w * y - z * x))));
    const rz = Math.atan2(2 * (w * z + x * y), 1 - 2 * (y * y + z * z));
    return [rx, ry, rz].map(r => r * 180 / Math.PI);
  }
  fileName(p: string): string { return p.split(/[\\/]/).pop() ?? p; }
  fmt(v: number): string { return (Math.abs(v) < 1e-4 ? 0 : v).toFixed(2); }
}

// XYZ (roll, pitch, yaw) degrees -> quaternion, the inverse of eulerOf.
function quatFromEuler(e: number[]): number[] {
  const [x, y, z] = e.map(d => d * Math.PI / 360);
  const cx = Math.cos(x), sx = Math.sin(x), cy = Math.cos(y), sy = Math.sin(y), cz = Math.cos(z), sz = Math.sin(z);
  return [sx * cy * cz - cx * sy * sz, cx * sy * cz + sx * cy * sz, cx * cy * sz - sx * sy * cz, cx * cy * cz + sx * sy * sz];
}

function errText(err: unknown): string {
  return err instanceof Error ? err.message : String(err);
}
