// The Animation Editor's 3D view: a skinned character (ASSET_MODEL_MESH with skeleton + skin) posed by a motion
// clip (ASSET_ANIMATION) at a time, skinned on the CPU each frame, with the skeleton drawn as joints and lines.
// Bones are picked by clicking a joint; the rotate / move gizmo previews a new local key for the current frame and
// hands it to the page on release, which commits it through the bridge (setAnimationKey).

import * as THREE from 'three';
import { SELECT_COLOR, ViewportCore, chunkGeometry, type MaterialSpec } from '../zipper-core/viewport-core';
import type { ModelTexturePixels } from '../zipper-workbench/zed-model-renderer';
import type { BoneWire, EditorChunk, EditorMesh } from '../model-editor/mesh-data';
import type { FxNode } from '../model-editor/asset-client';
import { FxInstance } from './zanim-player';

export interface ClipTrackData { name: string; rawName: string; kind: string; positions: number[][]; rotations: number[][] }

export interface ClipData {
  name: string;
  source: string;
  editable: boolean;
  duration: number;
  frames: number;
  samples: number;
  fps: number;
  revision: number;
  bones: ClipTrackData[];
}

export type AnimTool = 'select' | 'rotate' | 'move';

export interface Locator { name: string; matrix: number[] }

// What one shot plays: the weapon's ZANIM effect tree (ASSET_WEAPON_FX), run by FxInstance, and the meshes of the
// models its effects use (flash, shell), by model name.
export interface ShotEffect {
  root: FxNode | null;
  models: Map<string, EditorMesh>;
}

export interface AnimCallbacks {
  onBoneSelect?: (bone: string | null) => void;
  onTime?: (time: number) => void;
  // A new local key for `bone` at sample `frame` (units / unit quaternion), from a gizmo drag.
  onKey?: (bone: string, frame: number, key: { rotation?: number[]; position?: number[] }) => void;
  onHover?: (text: string | null) => void;
}

// Clip-only bones (weapon mounts, helpers) and the skeleton bone they hang from.
const EXTRA_PARENT: Record<string, string> = {
  rifle: 'rhand', pistol: 'rhand', weapon: 'rhand', launcher: 'rhand', rifle_out: 'spinehi', back: 'spinehi', do_not_use: 'hips',
  righttoe: 'rightFoot', lefttoe: 'leftFoot',
};
// SOCOM II bone names -> SOCOM 3 / CA ones (camelCase limbs), for rigs that only have the latter.
const CA_ALIAS: Record<string, string> = { rhand: 'rightHand', lhand: 'leftHand', rfoot: 'rightFoot', lfoot: 'leftFoot' };

interface Bone {
  name: string;
  parent: number;
  bind: THREE.Matrix4;
  track: ClipTrackData | null;
  extra: boolean;
}

interface ChunkView { chunk: EditorChunk; mesh: THREE.Mesh; geometry: THREE.BufferGeometry }

export class AnimViewport extends ViewportCore {
  private readonly modelGroup = new THREE.Group();
  private readonly skeletonGroup = new THREE.Group();
  private readonly weaponGroup = new THREE.Group();
  private readonly fxGroup = new THREE.Group();
  private weaponViews: ChunkView[] = [];
  private fxViews: ChunkView[] = [];
  private locators = new Map<string, THREE.Matrix4>();
  private mountName = 'rifle';
  private characterPixels: Record<string, ModelTexturePixels> = {};
  private extraPixels: Record<string, ModelTexturePixels> = {};
  private shot: ShotEffect | null = null;
  private fxTemplates = new Map<string, THREE.Group>();
  private fxNodes = new Map<string, FxNode>();
  private instances: Array<{ fx: FxInstance; groups: Map<FxInstance, Map<string, THREE.Group>>; shells: Set<FxInstance> }> = [];
  /** Opcodes the preview met but does not model (particles, sounds ...), for the page to list. */
  readonly skippedOps = new Set<string>();
  private shells: Array<{ obj: THREE.Group; v: THREE.Vector3; spin: THREE.Vector3; life: number }> = [];
  private readonly muzzleLight = new THREE.PointLight(0xffc070, 0, 40, 1.5);
  autoFire = false;
  fireWait = 0.12;
  private fireTimer = 0;
  showWeapon = true;
  private views: ChunkView[] = [];
  private bones: Bone[] = [];
  private world: THREE.Matrix4[] = [];
  private joints: THREE.Mesh[] = [];
  private lines: THREE.LineSegments | null = null;
  private clip: ClipData | null = null;
  private meshData: EditorMesh | null = null;
  private override: { bone: number; rotation?: THREE.Quaternion; position?: THREE.Vector3 } | null = null;
  private drag: { bone: number; frame: number } | null = null;
  selectedBone: number = -1;
  time = 0;
  playing = false;
  speed = 1;
  loop = true;
  showSkeleton = true;
  showMesh = true;
  private toolValue: AnimTool = 'select';

  constructor(canvas: HTMLCanvasElement, private readonly callbacks: AnimCallbacks = {}) {
    super(canvas);
    this.scene.add(this.modelGroup, this.skeletonGroup, this.weaponGroup, this.fxGroup, this.muzzleLight);
    this.weaponGroup.matrixAutoUpdate = false;
    this.skeletonGroup.renderOrder = 10;
    canvas.addEventListener('pointerdown', this.onPointerDown);
    canvas.addEventListener('pointermove', this.onPointerMove);
  }

  dispose(): void {
    for (const v of [...this.weaponViews, ...this.fxViews]) { v.geometry.dispose(); }
    this.clearCharacter();
    this.disposeCore();
  }

  protected materialTargets(): Array<{ mesh: THREE.Mesh; spec: MaterialSpec }> {
    return [...this.views, ...this.weaponViews, ...this.fxViews].map(v => ({ mesh: v.mesh, spec: v.chunk }));
  }

  // ------------------------------------------------------------------ content

  setCharacter(mesh: EditorMesh | null, textures: Record<string, ModelTexturePixels>, keepCamera = false): void {
    this.clearCharacter();
    this.meshData = mesh;
    this.characterPixels = textures;
    this.setTexturePixels({ ...textures, ...this.extraPixels });
    if (mesh) {
      for (const chunk of mesh.chunks) {
        const geometry = chunkGeometry(chunk);
        (geometry.getAttribute('position') as THREE.BufferAttribute).setUsage(THREE.DynamicDrawUsage);
        (geometry.getAttribute('normal') as THREE.BufferAttribute).setUsage(THREE.DynamicDrawUsage);
        const m = new THREE.Mesh(geometry, this.materialFor(chunk));
        m.frustumCulled = false;
        this.modelGroup.add(m);
        this.views.push({ chunk, mesh: m, geometry });
      }
    }
    this.buildBones();
    this.pose();
    const box = new THREE.Box3().setFromObject(this.modelGroup);
    if (!box.isEmpty()) {
      this.extent = Math.max(1, box.getSize(new THREE.Vector3()).length());
      box.getCenter(this.center);
    }
    this.clearHelpers();
    this.buildGridAxes(new THREE.Box3(new THREE.Vector3(-10, 0, -10), new THREE.Vector3(10, 20, 10)));
    if (!keepCamera) {
      this.facesNegZ = true;
      this.setView('iso');
      this.frameBox(box.isEmpty() ? new THREE.Box3(new THREE.Vector3(-8, 0, -8), new THREE.Vector3(8, 20, 8)) : box);
    }
  }

  private clearCharacter(): void {
    for (const v of this.views) { v.geometry.dispose(); }
    this.views = [];
    this.modelGroup.clear();
    this.clearMaterials();
  }

  setClip(clip: ClipData | null): void {
    const keepBone = this.bones[this.selectedBone]?.name ?? null;
    this.clip = clip;
    this.override = null;
    if (clip && this.time > clip.duration) { this.time = 0; }
    this.buildBones();
    this.selectedBone = keepBone ? this.bones.findIndex(b => b.name === keepBone) : -1;
    this.pose();
    this.updateGizmo();
  }

  // Skeleton bones (bind locals) plus clip-only bones; each takes its clip track by name.
  private buildBones(): void {
    const skeleton: BoneWire[] = this.meshData?.skeleton ?? [];
    const tracks = new Map((this.clip?.bones ?? []).map(t => [t.name.toLowerCase(), t]));
    this.bones = skeleton.map(b => ({
      name: b.name, parent: b.parent, bind: new THREE.Matrix4().fromArray(b.local), track: tracks.get(b.name.toLowerCase()) ?? null, extra: false,
    }));
    const exact = (n: string) => this.bones.findIndex(b => b.name.toLowerCase() === n.toLowerCase());
    const index = (n: string) => { const i = exact(n); return i >= 0 || !CA_ALIAS[n.toLowerCase()] ? i : exact(CA_ALIAS[n.toLowerCase()]); };
    for (const t of this.clip?.bones ?? []) {
      if (index(t.name) >= 0) { continue; }
      const parentName = EXTRA_PARENT[t.name.toLowerCase()] ?? 'hips';
      this.bones.push({ name: t.name, parent: index(parentName), bind: new THREE.Matrix4(), track: t, extra: true });
    }
    this.world = this.bones.map(() => new THREE.Matrix4());
    this.buildSkeleton();
  }

  boneNames(): Array<{ name: string; animated: boolean; extra: boolean; tracked: boolean }> {
    return this.bones.map(b => ({
      name: b.name, extra: b.extra, tracked: !!b.track,
      animated: !!b.track && (b.track.positions.length > 1 || b.track.rotations.length > 1),
    }));
  }

  // ------------------------------------------------------------------ playback

  get duration(): number { return this.clip?.duration ?? 0; }
  get samples(): number { return this.clip?.samples ?? 1; }
  get frame(): number { return this.clip ? Math.round(this.time / Math.max(1e-6, this.clip.duration) * (this.clip.samples - 1)) : 0; }

  setTime(t: number): void {
    const d = this.duration;
    this.time = d > 0 ? Math.max(0, Math.min(d, t)) : 0;
    this.override = null;
    this.pose();
    this.updateGizmo();
    this.callbacks.onTime?.(this.time);
  }

  setFrame(frame: number): void {
    const c = this.clip;
    if (!c) { return; }
    this.setTime(Math.max(0, Math.min(c.samples - 1, frame)) / Math.max(1, c.samples - 1) * c.duration);
  }

  protected override onFrame(dt: number): void {
    // The core renders its first frame from its own constructor, before these fields exist.
    if (!this.fxGroup) { return; }
    this.stepEffects(dt);
    if (!this.playing || !this.clip || this.drag) { return; }
    let t = this.time + dt * this.speed;
    if (t > this.clip.duration) {
      if (this.loop) { t = this.clip.duration > 0 ? t % this.clip.duration : 0; } else { t = this.clip.duration; this.playing = false; }
    }
    this.time = t;
    this.pose();
    this.updateGizmo();
    this.callbacks.onTime?.(this.time);
  }

  // ------------------------------------------------------------------ pose + skin

  private sampleTrack(keys: number[][], f: number, quaternion: boolean): number[] {
    if (keys.length === 1) { return keys[0]; }
    const i = Math.min(Math.floor(f), keys.length - 2);
    const u = Math.max(0, Math.min(1, f - i));
    const a = keys[i], b = keys[i + 1];
    if (!quaternion) { return [a[0] + (b[0] - a[0]) * u, a[1] + (b[1] - a[1]) * u, a[2] + (b[2] - a[2]) * u]; }
    const s = a[0] * b[0] + a[1] * b[1] + a[2] * b[2] + a[3] * b[3] < 0 ? -1 : 1;
    const q = [0, 1, 2, 3].map(k => a[k] + (s * b[k] - a[k]) * u);
    const n = Math.hypot(...q) || 1;
    return q.map(v => v / n);
  }

  private localOf(i: number, f: number): THREE.Matrix4 {
    const b = this.bones[i];
    const o = this.override && this.override.bone === i ? this.override : null;
    if (!b.track && !o) { return b.bind; }
    const bindPos = new THREE.Vector3(), bindRot = new THREE.Quaternion(), bindScale = new THREE.Vector3();
    b.bind.decompose(bindPos, bindRot, bindScale);
    // A track without positions (GTA: every bone but the root) keeps the bind translation.
    const p = o?.position ?? (b.track?.positions.length ? new THREE.Vector3(...this.sampleTrack(b.track.positions, f, false) as [number, number, number]) : bindPos);
    const q = o?.rotation ?? (b.track?.rotations.length ? new THREE.Quaternion(...this.sampleTrack(b.track.rotations, f, true) as [number, number, number, number]) : bindRot);
    return new THREE.Matrix4().compose(p, q, new THREE.Vector3(1, 1, 1));
  }

  private pose(): void {
    const c = this.clip;
    const f = c ? this.time / Math.max(1e-6, c.duration) * (c.samples - 1) : 0;
    for (let i = 0; i < this.bones.length; i++) {
      const parent = this.bones[i].parent;
      const local = this.localOf(i, f);
      if (parent >= 0 && parent < i) { this.world[i].multiplyMatrices(this.world[parent], local); }
      else if (parent >= 0) { this.world[i].multiplyMatrices(this.world[parent] ?? new THREE.Matrix4(), local); }
      else { this.world[i].copy(local); }
    }
    this.skin();
    this.updateSkeleton();
    this.placeWeapon();
  }

  private skin(): void {
    const inf = this.meshData?.skinInfluences ?? 0;
    for (const v of this.views) {
      const skin = v.chunk.skin;
      if (!skin || !inf) { continue; }
      const pos = v.geometry.getAttribute('position') as THREE.BufferAttribute;
      const nrm = v.geometry.getAttribute('normal') as THREE.BufferAttribute;
      const pa = pos.array as Float32Array, na = nrm.array as Float32Array;
      for (let i = 0; i < v.chunk.vertexCount; i++) {
        let x = 0, y = 0, z = 0, nx = 0, ny = 0, nz = 0;
        for (let k = 0; k < inf; k++) {
          const s = i * inf + k;
          const b = skin.bones[s];
          if (b < 0) { continue; }
          const w = skin.weights[s];
          const e = (this.world[b] ?? this.world[0]).elements;
          const lx = skin.positions[s * 3], ly = skin.positions[s * 3 + 1], lz = skin.positions[s * 3 + 2];
          x += (e[0] * lx + e[4] * ly + e[8] * lz + e[12]) * w;
          y += (e[1] * lx + e[5] * ly + e[9] * lz + e[13]) * w;
          z += (e[2] * lx + e[6] * ly + e[10] * lz + e[14]) * w;
          const ax = skin.normals[s * 3], ay = skin.normals[s * 3 + 1], az = skin.normals[s * 3 + 2];
          nx += (e[0] * ax + e[4] * ay + e[8] * az) * w;
          ny += (e[1] * ax + e[5] * ay + e[9] * az) * w;
          nz += (e[2] * ax + e[6] * ay + e[10] * az) * w;
        }
        pa[i * 3] = x; pa[i * 3 + 1] = y; pa[i * 3 + 2] = z;
        const l = Math.hypot(nx, ny, nz) || 1;
        na[i * 3] = nx / l; na[i * 3 + 1] = ny / l; na[i * 3 + 2] = nz / l;
      }
      pos.needsUpdate = true;
      nrm.needsUpdate = true;
    }
    this.modelGroup.visible = this.showMesh;
  }

  // ------------------------------------------------------------------ skeleton overlay

  private buildSkeleton(): void {
    for (const j of this.joints) { j.geometry.dispose(); (j.material as THREE.Material).dispose(); }
    this.joints = [];
    this.lines?.geometry.dispose();
    this.skeletonGroup.clear();
    // ~1.5% of the rig's height: 0.28 on a SOCOM character (~19 units tall), a few cm on a GTA ped (metres).
    const ys = (this.meshData?.skeleton ?? []).map(b => b.world[13]);
    const height = ys.length ? Math.max(...ys) - Math.min(...ys) : 0;
    const r = height > 0 ? Math.min(0.28, height * 0.016) : 0.28;
    const geo = new THREE.SphereGeometry(r, 10, 8);
    this.bones.forEach((b, i) => {
      const m = new THREE.Mesh(geo, new THREE.MeshBasicMaterial({ color: this.jointColor(i), depthTest: false, transparent: true, opacity: 0.9 }));
      m.renderOrder = 12;
      m.userData['bone'] = i;
      this.skeletonGroup.add(m);
      this.joints.push(m);
    });
    const lineGeo = new THREE.BufferGeometry();
    lineGeo.setAttribute('position', new THREE.BufferAttribute(new Float32Array(this.bones.length * 6), 3));
    this.lines = new THREE.LineSegments(lineGeo, new THREE.LineBasicMaterial({ color: 0x7fd3ff, depthTest: false, transparent: true, opacity: 0.8 }));
    this.lines.renderOrder = 11;
    this.lines.frustumCulled = false;
    this.skeletonGroup.add(this.lines);
  }

  private jointColor(i: number): number {
    if (i === this.selectedBone) { return SELECT_COLOR; }
    const b = this.bones[i];
    if (b.extra) { return 0xb48cff; }
    return b.track && (b.track.positions.length > 1 || b.track.rotations.length > 1) ? 0x46d07a : 0xd8d8d8;
  }

  private updateSkeleton(): void {
    this.skeletonGroup.visible = this.showSkeleton;
    if (!this.lines) { return; }
    const arr = (this.lines.geometry.getAttribute('position') as THREE.BufferAttribute).array as Float32Array;
    const p = new THREE.Vector3(), q = new THREE.Vector3();
    this.bones.forEach((b, i) => {
      p.setFromMatrixPosition(this.world[i]);
      this.joints[i]?.position.copy(p);
      // No line to the model node (it sits at the origin, below skel_root).
      const parent = b.parent > 0 ? b.parent : i;
      q.setFromMatrixPosition(this.world[parent]);
      arr.set([p.x, p.y, p.z, q.x, q.y, q.z], i * 6);
    });
    this.lines.geometry.getAttribute('position').needsUpdate = true;
    // The model node and skel_root sit together at the origin / hips: hide the root joint.
    if (this.joints[0]) { this.joints[0].visible = this.bones.length > 1 ? false : true; }
  }

  // ------------------------------------------------------------------ weapon + firing

  /** Mounts a weapon (high LOD visuals) on the clip's `rifle` bone (else the right hand). */
  setWeapon(mesh: EditorMesh | null, textures: Record<string, ModelTexturePixels>, locators: Locator[]): void {
    for (const v of this.weaponViews) { v.geometry.dispose(); }
    this.weaponViews = [];
    this.weaponGroup.clear();
    this.locators = new Map(locators.map(l => [l.name.toLowerCase(), new THREE.Matrix4().fromArray(l.matrix)]));
    this.extraPixels = { ...this.extraPixels, ...textures };
    this.setTexturePixels({ ...this.characterPixels, ...this.extraPixels });
    if (mesh) {
      for (const chunk of mesh.chunks) {
        // The game draws one LOD and toggles the scopes; show the high model (and a plain scope when there is one).
        const node = (chunk.node ?? '').toLowerCase();
        if (node.endsWith('_low') || node === 'thermal_scope') { continue; }
        const geometry = chunkGeometry(chunk);
        const m = new THREE.Mesh(geometry, this.materialFor(chunk));
        this.weaponGroup.add(m);
        this.weaponViews.push({ chunk, mesh: m, geometry });
      }
    }
    this.placeWeapon();
  }

  setEffect(shot: ShotEffect | null, textures: Record<string, ModelTexturePixels>): void {
    for (const v of this.fxViews) { v.geometry.dispose(); }
    this.fxViews = [];
    this.fxGroup.clear();
    this.shells = [];
    this.instances = [];
    this.fxTemplates.clear();
    this.fxNodes.clear();
    this.shot = shot;
    this.extraPixels = { ...this.extraPixels, ...textures };
    this.setTexturePixels({ ...this.characterPixels, ...this.extraPixels });
    for (const [name, mesh] of shot?.models ?? []) { this.fxTemplates.set(name.toLowerCase(), this.buildFx(mesh)); }
    const walk = (n: FxNode | null | undefined) => { if (!n) { return; } this.fxNodes.set(n.name.toLowerCase(), n); (n.spawns ?? []).forEach(walk); };
    walk(shot?.root);
  }

  private buildFx(mesh: EditorMesh): THREE.Group {
    const g = new THREE.Group();
    for (const chunk of mesh.chunks) {
      const geometry = chunkGeometry(chunk);
      const m = new THREE.Mesh(geometry, this.materialFor(chunk));
      m.renderOrder = 5;
      g.add(m);
      this.fxViews.push({ chunk, mesh: m, geometry });
    }
    return g;
  }

  private mountIndex(): number {
    const i = this.bones.findIndex(b => b.name.toLowerCase() === this.mountName);
    // SOCOM 'rhand', other rigs 'RightHand' / GTA 'R Hand'.
    return i >= 0 ? i : this.bones.findIndex(b => ['rhand', 'righthand'].includes(b.name.toLowerCase().replace(/\s+/g, '')));
  }

  private weaponMatrix(): THREE.Matrix4 | null {
    const i = this.mountIndex();
    return i >= 0 ? this.world[i] : null;
  }

  private placeWeapon(): void {
    const m = this.weaponMatrix();
    this.weaponGroup.visible = this.showWeapon && !!m && this.weaponViews.length > 0;
    if (m) {
      // A "mount" locator (GTA) is the hand -> weapon mesh transform; SOCOM weapons sit on their bone as is.
      const mount = this.locators.get('mount');
      this.weaponGroup.matrix.copy(m);
      if (mount) { this.weaponGroup.matrix.multiply(mount); }
      this.weaponGroup.matrixWorldNeedsUpdate = true;
    }
  }

  setWeaponVisible(on: boolean): void { this.showWeapon = on; this.placeWeapon(); }

  private locatorWorld(name: string): THREE.Matrix4 | null {
    const w = this.weaponMatrix(), l = this.locators.get(name);
    return w && l ? w.clone().multiply(l) : null;
  }

  /** One shot: runs the weapon's fire effect script (FireAnimName) from the firepoint. */
  fire(): void {
    const root = this.shot?.root;
    if (!root || !this.locatorWorld('firepoint')) { return; }
    const fx = new FxInstance(root, name => this.fxNodes.get(name.toLowerCase()) ?? null);
    this.instances.push({ fx, groups: new Map(), shells: new Set() });
  }

  // The flash scripts copy the owner's transform (fire_fix: OBJECT_ROTATE / TRANSLATE_STATE from 0xFA); for a muzzle
  // effect the owner is the weapon's firepoint, for shell effects its ejection port.
  private ownerOf(inst: FxInstance): THREE.Matrix4 | null {
    return this.locatorWorld(/shell/i.test(inst.node.name) ? 'firepoint_shell' : 'firepoint');
  }

  private throwShell(template: THREE.Group, port: THREE.Matrix4): void {
    const obj = template.clone();
    obj.position.setFromMatrixPosition(port);
    obj.quaternion.setFromRotationMatrix(port);
    // Out of the right side of the gun, a little up (the shell scripts' physics opcodes are not decoded yet).
    const side = new THREE.Vector3(0, 0, 1).applyMatrix4(new THREE.Matrix4().extractRotation(port));
    const v = side.multiplyScalar(14 + Math.random() * 4).add(new THREE.Vector3(0, 10 + Math.random() * 4, 0));
    this.fxGroup.add(obj);
    this.shells.push({ obj, v, spin: new THREE.Vector3(Math.random() * 20, Math.random() * 20, Math.random() * 20), life: 0.8 });
  }

  private stepEffects(dt: number): void {
    if (this.autoFire) {
      this.fireTimer -= dt;
      if (this.fireTimer <= 0) { this.fire(); this.fireTimer += Math.max(0.03, this.fireWait); }
    }
    let light = 0;
    const lightColor = new THREE.Color(0, 0, 0);
    for (const run of this.instances) {
      run.fx.step(dt);
      for (const inst of run.fx.all()) {
        inst.skipped.forEach(op => this.skippedOps.add(op));
        const owner = this.ownerOf(inst);
        for (const m of inst.node.models ?? []) {
          const key = m.name.toLowerCase();
          const template = this.fxTemplates.get(key);
          if (!template || !owner) { continue; }
          if (/^bullet_shell/i.test(m.name)) {
            if (!run.shells.has(inst)) { run.shells.add(inst); this.throwShell(template, owner); }
            continue;
          }
          let byModel = run.groups.get(inst);
          if (!byModel) { byModel = new Map(); run.groups.set(inst, byModel); }
          let g = byModel.get(key);
          if (!g) { g = template.clone(); g.matrixAutoUpdate = false; this.fxGroup.add(g); byModel.set(key, g); }
          // The model is the effect's own object; its rotate / scale child nodes carry the script's roll and size.
          const self = inst.objects.get('self') ?? inst.objects.get(m.name);
          const rot = inst.objects.get('rotate'), scl = inst.objects.get('scale');
          g.visible = !!self?.visible;
          const e = rot?.euler ?? [0, 0, 0], k = scl?.scale ?? [1, 1, 1];
          g.matrix.copy(owner)
            .multiply(new THREE.Matrix4().makeScale(k[0], k[1], k[2]))
            .multiply(new THREE.Matrix4().makeRotationFromEuler(new THREE.Euler(e[0], e[1], e[2], 'XYZ')));
          g.matrixWorldNeedsUpdate = true;
        }
        for (const l of inst.lights) {
          const fade = 1 - l.age / l.life;
          light += fade;
          lightColor.add(new THREE.Color(l.color[0] * fade, l.color[1] * fade, l.color[2] * fade));
          if (owner) { this.muzzleLight.position.setFromMatrixPosition(owner); }
          this.muzzleLight.distance = l.range * 2;
        }
      }
    }
    for (const run of this.instances.filter(r => r.fx.done)) {
      for (const byModel of run.groups.values()) { for (const g of byModel.values()) { this.fxGroup.remove(g); } }
    }
    this.instances = this.instances.filter(r => !r.fx.done && r.fx.age < 5);
    this.muzzleLight.intensity = light > 0 ? 60 * Math.min(1, light) : 0;
    if (light > 0) { this.muzzleLight.color.copy(lightColor.multiplyScalar(1 / light)); }
    for (const s of this.shells) {
      s.life -= dt;
      s.v.y -= 60 * dt;
      s.obj.position.addScaledVector(s.v, dt);
      s.obj.rotation.x += s.spin.x * dt; s.obj.rotation.y += s.spin.y * dt; s.obj.rotation.z += s.spin.z * dt;
      if (s.life <= 0) { this.fxGroup.remove(s.obj); }
    }
    this.shells = this.shells.filter(s => s.life > 0);
  }

  setSkeletonVisible(on: boolean): void { this.showSkeleton = on; this.skeletonGroup.visible = on; }
  setMeshVisible(on: boolean): void { this.showMesh = on; this.modelGroup.visible = on; }

  // ------------------------------------------------------------------ selection + gizmo

  set tool(t: AnimTool) { this.toolValue = t; this.updateGizmo(); }
  get tool(): AnimTool { return this.toolValue; }

  selectBone(name: string | null): void {
    this.selectedBone = name ? this.bones.findIndex(b => b.name === name) : -1;
    this.joints.forEach((j, i) => (j.material as THREE.MeshBasicMaterial).color.setHex(this.jointColor(i)));
    this.updateGizmo();
    this.callbacks.onBoneSelect?.(this.selectedBone >= 0 ? this.bones[this.selectedBone].name : null);
  }

  /** The selected bone's current local key (units / quaternion) at the current time. */
  selectedLocal(): { position: number[]; rotation: number[] } | null {
    const i = this.selectedBone;
    if (i < 0 || !this.bones[i]) { return null; }
    const c = this.clip;
    const f = c ? this.time / Math.max(1e-6, c.duration) * (c.samples - 1) : 0;
    const p = new THREE.Vector3(), q = new THREE.Quaternion(), s = new THREE.Vector3();
    this.localOf(i, f).decompose(p, q, s);
    return { position: p.toArray(), rotation: [q.x, q.y, q.z, q.w] };
  }

  private updateGizmo(): void {
    if (this.drag) { return; }
    const i = this.selectedBone;
    if (i < 0 || this.toolValue === 'select' || !this.bones[i]) { this.gizmo.detach(); return; }
    const p = new THREE.Vector3(), q = new THREE.Quaternion(), s = new THREE.Vector3();
    this.world[i].decompose(p, q, s);
    this.pivot.position.copy(p);
    this.pivot.quaternion.copy(q);
    this.pivot.scale.set(1, 1, 1);
    this.pivot.updateMatrixWorld(true);
    this.gizmo.setMode(this.toolValue === 'rotate' ? 'rotate' : 'translate');
    this.gizmo.setSpace(this.toolValue === 'rotate' ? 'local' : 'world');
    this.gizmo.size = 0.7;
    this.gizmo.attach(this.pivot);
  }

  protected override onGizmoDragging(dragging: boolean): void {
    const i = this.selectedBone;
    if (dragging) {
      this.playing = false;
      // Keys land on samples: snap the time to the current one first.
      const frame = this.frame;
      this.setFrame(frame);
      this.drag = { bone: i, frame };
      return;
    }
    const drag = this.drag;
    this.drag = null;
    const o = this.override;
    if (!drag || !o || o.bone !== drag.bone) { this.updateGizmo(); return; }
    const key: { rotation?: number[]; position?: number[] } = {};
    if (o.rotation) { key.rotation = [o.rotation.x, o.rotation.y, o.rotation.z, o.rotation.w]; }
    if (o.position) { key.position = o.position.toArray(); }
    this.callbacks.onKey?.(this.bones[drag.bone].name, drag.frame, key);
  }

  protected override onGizmoChange(): void {
    const drag = this.drag;
    if (!drag) { return; }
    const b = this.bones[drag.bone];
    this.pivot.updateMatrixWorld(true);
    const parentWorld = b.parent >= 0 ? this.world[b.parent] : new THREE.Matrix4();
    const inv = parentWorld.clone().invert();
    const local = inv.multiply(this.pivot.matrixWorld.clone());
    const p = new THREE.Vector3(), q = new THREE.Quaternion(), s = new THREE.Vector3();
    local.decompose(p, q, s);
    const current = this.selectedLocal();
    this.override = this.toolValue === 'rotate'
      ? { bone: drag.bone, rotation: q.normalize() }
      : { bone: drag.bone, position: p, rotation: current ? new THREE.Quaternion(...current.rotation as [number, number, number, number]) : undefined };
    this.pose();
  }

  /** Drops a previewed key (e.g. when the bridge refused it). */
  clearOverride(): void { this.override = null; this.pose(); this.updateGizmo(); }

  private readonly onPointerDown = (e: PointerEvent): void => {
    if (e.button !== 0 || this.orbitWithAlt) { return; }
    if (this.gizmo.dragging || (this.gizmo as unknown as { axis: string | null }).axis) { return; }
    if (!this.showSkeleton) { return; }
    const hit = this.rayFrom(e.clientX, e.clientY).intersectObjects(this.joints.filter(j => j.visible), false)[0];
    this.selectBone(hit ? this.bones[hit.object.userData['bone'] as number].name : null);
  };

  private readonly onPointerMove = (e: PointerEvent): void => {
    if (e.buttons !== 0 || !this.showSkeleton) { return; }
    const hit = this.rayFrom(e.clientX, e.clientY).intersectObjects(this.joints.filter(j => j.visible), false)[0];
    this.callbacks.onHover?.(hit ? this.bones[hit.object.userData['bone'] as number].name : null);
  };

  frameCharacter(): void {
    const box = new THREE.Box3();
    for (const w of this.world) { box.expandByPoint(new THREE.Vector3().setFromMatrixPosition(w)); }
    box.expandByScalar(2);
    this.frameBox(box);
  }

  // Facing toggle (characters face -Z).
  toggleFacing(): void { this.facesNegZ = !this.facesNegZ; this.setView('front'); this.frameCharacter(); }
}
