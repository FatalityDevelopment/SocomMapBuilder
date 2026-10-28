// The Map Editor's 3D view: the composed map (worldmodel, placed props, clutter, sky) drawn as one instanced mesh per
// model chunk, AIMAPS points as labelled pillars on the ground, instance / point selection (click, Shift / Ctrl,
// box drag), a move / rotate / scale gizmo with live preview, place-on-surface, and WASD fly. Previews only: commits
// go to the page, which sends them to the bridge (moveInstance / moveClutter / movePoint / addClutter ...).

import * as THREE from 'three';
import { SELECT_COLOR, ViewportCore, chunkGeometry, isIdentity, labelSprite, type MaterialSpec } from '../webview/zipper-core/viewport-core';
import type { EditorMesh } from '../webview/model-editor/mesh-data';
import { layerOf, movable as isMovable, pointColor } from '../mapModel';

export type MapTool = 'select' | 'move' | 'rotate' | 'scale' | 'place';
export type MapLayer = 'world' | 'reference' | 'clutter' | 'sky' | 'points';

export interface MapInstance {
  kind: string;
  model: string;
  instance: string;
  path: string;
  position: number[];
  scale: number;
  // World transform, model space (column-major).
  matrix: number[];
  geoIndex: number | null;
  clutterIndex: number | null;
  // "<archive>:<model>" when the model resolves.
  source: string | null;
}

export interface MapPointInfo { name: string; team: number; kind: string; x: number; z: number }

export interface MapModel { mesh: EditorMesh; textureNamespace: string | null }

export interface MapCallbacks {
  onSelectionChange?: () => void;
  onHover?: (text: string | null) => void;
  // New model-space world matrices for moved instances.
  onTransformCommit?: (items: Array<{ index: number; matrix: number[] }>, kind: 'move' | 'rotate' | 'scale') => void;
  onPointMove?: (name: string, position: [number, number, number]) => void;
  onPlace?: (position: [number, number, number]) => void;
  onOpenModel?: (index: number) => void;
  onCameraChange?: () => void;
}

// Model space is Y up like the display, so placements are used as they are (column-major).
function toDisplay(m: ArrayLike<number>): THREE.Matrix4 {
  return new THREE.Matrix4().fromArray(Array.from(m));
}

export function toModelArray(d: THREE.Matrix4): number[] {
  return d.toArray();
}

// Identity of an instance across reloads (the matrix may change; what it is may not).
function instanceKey(inst: MapInstance): string {
  return `${inst.kind}|${inst.model}|${inst.path}|${inst.clutterIndex ?? ''}`;
}

interface Slot { mesh: THREE.InstancedMesh; slot: number }

// A vertical flat card (a facade tree): which horizontal axis it is flat along, or null for real 3D geometry.
function cardAxis(box: THREE.Box3 | undefined): 'x' | 'z' | null {
  if (!box || box.isEmpty()) { return null; }
  const s = box.getSize(new THREE.Vector3());
  if (s.y <= 0) { return null; }
  const flat = s.y * 0.02;
  if (s.z <= flat && s.x > flat) { return 'z'; }
  if (s.x <= flat && s.z > flat) { return 'x'; }
  return null;
}

export class MapViewport extends ViewportCore {
  private readonly content = new THREE.Group();
  private readonly overlay = new THREE.Group();
  private readonly pointGroup = new THREE.Group();
  private instances: MapInstance[] = [];
  private display: THREE.Matrix4[] = [];
  // Placements as they stand right now, including a gizmo drag in progress (what a commit sends).
  private live: THREE.Matrix4[] = [];
  private slots: Slot[][] = [];
  // Facade clutter: flat tree cards the game turns about Y to face the camera. Value = the card's flat axis.
  private readonly billboards = new Map<number, 'x' | 'z'>();
  private readonly overlayHolders = new Map<number, THREE.Object3D>();
  private readonly lastBillboardCamera = new THREE.Vector3(NaN, NaN, NaN);
  private billboardsDirty = true;
  private targets: Array<{ mesh: THREE.Mesh; spec: MaterialSpec }> = [];
  private localBounds = new Map<string, THREE.Box3>();
  // The decoded meshes the current scene was built from (a reload with the same ones only moves instances).
  private builtModels = new Map<string, MapModel>();
  private readonly overlayMaterial = new THREE.LineBasicMaterial({ color: SELECT_COLOR, depthTest: false, transparent: true });
  // Meshes whose culling is off while a gizmo drag moves their instances (bounds are recomputed when it ends).
  private readonly dragMeshes = new Map<THREE.InstancedMesh, boolean>();
  private points: Array<MapPointInfo & { y: number; obj: THREE.Object3D }> = [];
  // The sky dome encloses the map; hidden until asked for.
  private readonly hiddenLayers = new Set<MapLayer>(['sky']);
  private readonly hiddenModels = new Set<string>();
  private readonly lockedLayers = new Set<MapLayer>();
  // Move along a single prop's own axes (its yaw) rather than the world's.
  private localSpace = false;
  readonly selected = new Set<number>();
  selectedPoint: string | null = null;
  private toolValue: MapTool = 'select';
  private editableValue = true;
  private dragStart: { x: number; y: number } | null = null;
  private readonly boxEl: HTMLDivElement;
  private gizmoDrag: { start: THREE.Matrix4; snapshot: Map<number, THREE.Matrix4>; point?: THREE.Vector3 } | null = null;
  private worldBox = new THREE.Box3();

  constructor(canvas: HTMLCanvasElement, private readonly callbacks: MapCallbacks = {}) {
    super(canvas, () => callbacks.onCameraChange?.());
    this.scene.add(this.content, this.overlay, this.pointGroup);
    this.boxEl = document.createElement('div');
    this.boxEl.className = 'me-boxselect';
    canvas.parentElement?.appendChild(this.boxEl);
    canvas.addEventListener('pointerdown', this.onPointerDown);
    canvas.addEventListener('pointermove', this.onPointerMove);
    canvas.addEventListener('dblclick', this.onDoubleClick);
    window.addEventListener('pointerup', this.onPointerUp);
    canvas.addEventListener('pointerleave', () => this.callbacks.onHover?.(null));
    canvas.tabIndex = 0;
  }

  dispose(): void {
    window.removeEventListener('pointerup', this.onPointerUp);
    this.clearContent();
    this.overlay.traverse(o => { (o as THREE.Mesh).geometry?.dispose(); });
    this.overlayMaterial.dispose();
    this.disposeCore();
  }

  protected override flyEnabled(): boolean { return true; }
  protected materialTargets(): Array<{ mesh: THREE.Mesh; spec: MaterialSpec }> { return this.targets; }
  protected override onShadingChanged(): void { this.refreshOverlay(); }
  // The base constructor renders a first frame before this class's fields exist, hence the guard.
  protected override onFrame(): void { if (this.billboards) { this.updateBillboards(); } }

  // ------------------------------------------------------------------ billboards

  // The matrix a facade card is drawn with: its placement's position and scale, turned about Y to face the camera.
  private billboardMatrix(i: number, axis: 'x' | 'z'): THREE.Matrix4 {
    const pos = new THREE.Vector3(), rot = new THREE.Quaternion(), scale = new THREE.Vector3();
    this.live[i].decompose(pos, rot, scale);
    const dx = this.camera.position.x - pos.x, dz = this.camera.position.z - pos.z;
    // Turn the card's normal (+Z for a card flat in Z, +X for one flat in X) onto the camera direction.
    const yaw = axis === 'z' ? Math.atan2(dx, dz) : Math.atan2(-dz, dx);
    return new THREE.Matrix4().compose(pos, rot.setFromAxisAngle(new THREE.Vector3(0, 1, 0), yaw), scale);
  }

  private updateBillboards(): void {
    if (!this.billboards.size) { return; }
    if (!this.billboardsDirty && this.lastBillboardCamera.equals(this.camera.position)) { return; }
    this.lastBillboardCamera.copy(this.camera.position);
    this.billboardsDirty = false;
    const touched = new Set<THREE.InstancedMesh>();
    for (const [i, axis] of this.billboards) {
      const m = this.billboardMatrix(i, axis);
      for (const s of this.slots[i]) { s.mesh.setMatrixAt(s.slot, m); touched.add(s.mesh); }
      this.overlayHolders.get(i)?.matrix.copy(m);
    }
    touched.forEach(mesh => { mesh.instanceMatrix.needsUpdate = true; });
  }

  // ------------------------------------------------------------------ content

  setScene(
    instances: MapInstance[],
    models: Map<string, MapModel>,
    textures: Record<string, import('../webview/zipper-workbench/zed-model-renderer').ModelTexturePixels>,
    points: MapPointInfo[],
    keepCamera: boolean,
  ): void {
    // Keep a selected index only if it still holds the same instance: an undo or outside edit can shift the list.
    const before = this.instances;
    this.selected.forEach(i => { if (!before[i] || !instances[i] || instanceKey(before[i]) !== instanceKey(instances[i])) { this.selected.delete(i); } });
    // Only placements changed (a move, rotate, scale or drop): update the instance matrices in place.
    if (this.sameLayout(instances, models, textures)) {
      this.updatePlacements(instances, points);
      return;
    }
    this.clearContent();
    this.builtModels = new Map([...models]);
    this.setTexturePixels(textures);
    this.instances = instances;
    this.display = instances.map(i => toDisplay(i.matrix));
    this.slots = instances.map(() => []);

    const bySource = new Map<string, number[]>();
    instances.forEach((inst, i) => {
      if (!inst.source || !models.has(inst.source)) { return; }
      const list = bySource.get(inst.source) ?? [];
      list.push(i);
      bySource.set(inst.source, list);
    });

    for (const [source, idx] of bySource) {
      const model = models.get(source)!;
      const ns = model.textureNamespace;
      const box = new THREE.Box3();
      for (const chunk of model.mesh.chunks) {
        const geometry = chunkGeometry(chunk);
        geometry.computeBoundingBox();
        if (geometry.boundingBox) { box.union(geometry.boundingBox); }
        // Texture keys are namespaced: the same .tif name can differ between archives.
        const spec: MaterialSpec = { texture: chunk.texture ? `${ns ?? ''}:${chunk.texture.toLowerCase()}` : null, materialBlend: chunk.materialBlend, colors: chunk.colors };
        const mesh = new THREE.InstancedMesh(geometry, this.materialFor(spec), idx.length);
        idx.forEach((instIndex, slot) => {
          mesh.setMatrixAt(slot, this.display[instIndex]);
          this.slots[instIndex].push({ mesh, slot });
        });
        mesh.instanceMatrix.needsUpdate = true;
        mesh.computeBoundingSphere();
        mesh.userData['instances'] = idx;
        mesh.userData['layer'] = layerOf(instances[idx[0]]);
        mesh.userData['source'] = source;
        this.content.add(mesh);
        this.targets.push({ mesh, spec });
      }
      this.localBounds.set(source, box);
    }

    // Bounds of the playable map: the world and props (sky domes and fogbands would swamp it).
    this.live = this.display.map(m => m.clone());
    this.billboards.clear();
    instances.forEach((inst, i) => {
      const axis = layerOf(inst) === 'clutter' && inst.source ? cardAxis(this.localBounds.get(inst.source)) : null;
      if (!axis) { return; }
      this.billboards.set(i, axis);
      // Turning a card changes its instance's extent; skip culling rather than recompute bounds every frame.
      for (const s of this.slots[i]) { s.mesh.frustumCulled = false; }
    });
    this.billboardsDirty = true;

    this.worldBox = new THREE.Box3();
    instances.forEach((inst, i) => {
      const b = inst.source ? this.localBounds.get(inst.source) : null;
      if (!b || b.isEmpty() || layerOf(inst) === 'sky') { return; }
      this.worldBox.union(b.clone().applyMatrix4(this.display[i]));
    });
    if (!this.worldBox.isEmpty()) {
      this.extent = Math.max(10, this.worldBox.getSize(new THREE.Vector3()).length());
      this.worldBox.getCenter(this.center);
      this.flySpeed = this.extent / 12;
    }
    this.applyVisibility();
    this.buildPoints(points);
    this.clearHelpers();
    this.buildGridAxes(this.worldBox);
    if (!keepCamera) { this.frameAll(); }
    this.refreshOverlay();
    this.updateGizmo();
  }

  // The same instances (in order) drawn from the same decoded meshes and textures as the built scene.
  private sameLayout(instances: MapInstance[], models: Map<string, MapModel>, textures: Record<string, unknown>): boolean {
    if (!this.content.children.length || instances.length !== this.instances.length || textures !== this.texturePixels) { return false; }
    for (let i = 0; i < instances.length; i++) {
      const a = instances[i], b = this.instances[i];
      if (instanceKey(a) !== instanceKey(b) || a.source !== b.source) { return false; }
    }
    if (models.size !== this.builtModels.size) { return false; }
    for (const [source, model] of models) { if (this.builtModels.get(source) !== model) { return false; } }
    return true;
  }

  private updatePlacements(instances: MapInstance[], points: MapPointInfo[]): void {
    this.instances = instances;
    this.display = instances.map(i => toDisplay(i.matrix));
    this.live = this.display.map(m => m.clone());
    const touched = new Set<THREE.InstancedMesh>();
    instances.forEach((_, i) => {
      if (this.billboards.has(i)) { return; }
      for (const s of this.slots[i]) { s.mesh.setMatrixAt(s.slot, this.display[i]); touched.add(s.mesh); }
    });
    touched.forEach(m => { m.instanceMatrix.needsUpdate = true; m.computeBoundingSphere(); });
    this.billboardsDirty = true;
    this.buildPoints(points);
    this.refreshOverlay();
    this.updateGizmo();
  }

  private clearContent(): void {
    const geos = new Set<THREE.BufferGeometry>();
    this.content.traverse(o => { const g = (o as THREE.Mesh).geometry; if (g) { geos.add(g); } });
    geos.forEach(g => g.dispose());
    this.content.clear();
    this.targets = [];
    this.localBounds.clear();
    this.builtModels.clear();
    this.clearMaterials();
    this.clearPoints();
  }

  // Pillars and labels own their geometry, material and label texture.
  private clearPoints(): void {
    this.pointGroup.traverse(o => {
      const mesh = o as THREE.Mesh;
      mesh.geometry?.dispose();
      const mat = mesh.material as THREE.Material | undefined;
      if (mat) { (mat as THREE.SpriteMaterial).map?.dispose(); mat.dispose(); }
    });
    this.pointGroup.clear();
    this.points = [];
  }

  instanceList(): readonly MapInstance[] { return this.instances; }

  refreshHelpers(): void {
    this.clearHelpers();
    this.buildGridAxes(this.worldBox);
  }

  // Model-space Y of the first surface under instance i's origin (props, world), ignoring itself; null if none.
  groundBelow(i: number): number | null {
    const origin = new THREE.Vector3().setFromMatrixPosition(this.display[i]);
    // Start a little above the origin: a prop sunk into the ground still finds it, one inside a building finds the floor.
    const ray = new THREE.Raycaster(origin.clone().add(new THREE.Vector3(0, Math.max(1, this.extent * 0.002), 0)), new THREE.Vector3(0, -1, 0));
    const targets = this.content.children.filter(c => c.visible && c.userData['layer'] !== 'sky');
    for (const hit of ray.intersectObjects(targets, false)) {
      if (this.instanceOf(hit) !== i) { return hit.point.y; }
    }
    return null;
  }

  // ------------------------------------------------------------------ points

  private buildPoints(points: MapPointInfo[]): void {
    this.clearPoints();
    const height = Math.max(4, this.extent * 0.012);
    const ground = this.content.children.filter(c => c.userData['layer'] === 'world');
    const ray = new THREE.Raycaster();
    for (const p of points) {
      // On the highest world surface under the point (cells hold no height).
      ray.set(new THREE.Vector3(p.x, this.worldBox.isEmpty() ? 1000 : this.worldBox.max.y + 10, p.z), new THREE.Vector3(0, -1, 0));
      const hit = ray.intersectObjects(ground, false)[0];
      const y = hit ? hit.point.y : this.center.y;
      const color = pointColor(p.kind);
      const pillar = new THREE.Mesh(
        new THREE.CylinderGeometry(height * 0.08, height * 0.08, height, 10),
        new THREE.MeshBasicMaterial({ color, transparent: true, opacity: 0.85, depthTest: false }),
      );
      pillar.position.set(p.x, y + height / 2, p.z);
      pillar.renderOrder = 10;
      pillar.userData['point'] = p.name;
      const label = labelSprite(p.name, '#ffffff', 32);
      label.position.set(p.x, y + height * 1.25, p.z);
      label.scale.set(height * 0.35 * (label.userData['aspect'] as number), height * 0.35, 1);
      label.renderOrder = 11;
      const group = new THREE.Group();
      group.add(pillar, label);
      group.userData['point'] = p.name;
      this.pointGroup.add(group);
      this.points.push({ ...p, y, obj: group });
    }
    this.pointGroup.visible = !this.hiddenLayers.has('points');
  }

  pointList(): ReadonlyArray<MapPointInfo & { y: number }> { return this.points; }

  // ------------------------------------------------------------------ visibility

  setLayerHidden(layer: MapLayer, hidden: boolean): void {
    if (hidden) { this.hiddenLayers.add(layer); } else { this.hiddenLayers.delete(layer); }
    this.applyVisibility();
  }

  isLayerHidden(layer: MapLayer): boolean { return this.hiddenLayers.has(layer); }

  setModelHidden(source: string, hidden: boolean): void {
    if (hidden) { this.hiddenModels.add(source); } else { this.hiddenModels.delete(source); }
    this.applyVisibility();
  }

  isModelHidden(source: string): boolean { return this.hiddenModels.has(source); }

  private applyVisibility(): void {
    for (const c of this.content.children) {
      c.visible = !this.hiddenLayers.has(c.userData['layer']) && !this.hiddenModels.has(c.userData['source']);
    }
    this.pointGroup.visible = !this.hiddenLayers.has('points');
    this.refreshOverlay();
  }

  private instanceVisible(i: number): boolean {
    const inst = this.instances[i];
    return !this.hiddenLayers.has(layerOf(inst)) && !(inst.source && this.hiddenModels.has(inst.source));
  }

  // A locked layer stays visible (and something to drop onto or place on) but can't be picked.
  setLayerLocked(layer: MapLayer, locked: boolean): void {
    if (locked) { this.lockedLayers.add(layer); } else { this.lockedLayers.delete(layer); }
    let changed = false;
    for (const i of [...this.selected]) { if (!this.selectable(i)) { this.selected.delete(i); changed = true; } }
    if (layer === 'points' && locked && this.selectedPoint) { this.selectedPoint = null; changed = true; }
    if (changed) { this.selectionChanged(); }
  }

  isLayerLocked(layer: MapLayer): boolean { return this.lockedLayers.has(layer); }

  private selectable(i: number): boolean { return !this.lockedLayers.has(layerOf(this.instances[i])); }

  // Instance i's bounds on screen (canvas pixels), from the corners in front of the camera; null if none are.
  private screenRect(i: number, rect: DOMRect): { minX: number; maxX: number; minY: number; maxY: number } | null {
    const inst = this.instances[i];
    const local = inst.source ? this.localBounds.get(inst.source) : null;
    const corners: THREE.Vector3[] = [];
    if (local && !local.isEmpty()) {
      for (let k = 0; k < 8; k++) {
        corners.push(new THREE.Vector3(k & 1 ? local.max.x : local.min.x, k & 2 ? local.max.y : local.min.y, k & 4 ? local.max.z : local.min.z).applyMatrix4(this.display[i]));
      }
    } else {
      corners.push(new THREE.Vector3().setFromMatrixPosition(this.display[i]));
    }
    let out: { minX: number; maxX: number; minY: number; maxY: number } | null = null;
    for (const c of corners) {
      c.project(this.camera);
      if (c.z < -1 || c.z > 1) { continue; }
      const sx = (c.x + 1) / 2 * rect.width, sy = (1 - c.y) / 2 * rect.height;
      out = out ? { minX: Math.min(out.minX, sx), maxX: Math.max(out.maxX, sx), minY: Math.min(out.minY, sy), maxY: Math.max(out.maxY, sy) } : { minX: sx, maxX: sx, minY: sy, maxY: sy };
    }
    return out;
  }

  // ------------------------------------------------------------------ selection

  set tool(value: MapTool) { this.toolValue = value; this.updateGizmo(); }
  get tool(): MapTool { return this.toolValue; }

  set gizmoLocal(value: boolean) { this.localSpace = value; this.updateGizmo(); }
  get gizmoLocal(): boolean { return this.localSpace; }

  // Off while the page waits on an edit or the session is read-only: no gizmo, no placing.
  set editable(value: boolean) { if (this.editableValue !== value) { this.editableValue = value; this.updateGizmo(); } }
  get editable(): boolean { return this.editableValue; }

  select(indices: number[], additive = false): void {
    if (!additive) { this.selected.clear(); this.selectedPoint = null; }
    for (const i of indices) { this.selected.add(i); }
    this.selectionChanged();
  }

  selectPoint(name: string | null): void {
    this.selected.clear();
    this.selectedPoint = name;
    this.selectionChanged();
  }

  clearSelection(): void { this.selected.clear(); this.selectedPoint = null; this.selectionChanged(); }

  selectAllOfModel(source: string): void {
    this.select(this.instances.map((inst, i) => inst.source === source ? i : -1).filter(i => i >= 0));
  }

  private selectionChanged(): void {
    this.refreshOverlay();
    this.updateGizmo();
    this.callbacks.onSelectionChange?.();
  }

  // Selected instances that may move (the world model and sky stay put).
  private movable(): number[] {
    return [...this.selected].filter(i => isMovable(this.instances[i]));
  }

  selectionCenter(): THREE.Vector3 | null {
    if (this.selectedPoint) {
      const p = this.points.find(x => x.name === this.selectedPoint);
      return p ? new THREE.Vector3(p.x, p.y, p.z) : null;
    }
    const ids = [...this.selected];
    if (!ids.length) { return null; }
    const c = new THREE.Vector3();
    for (const i of ids) { c.add(new THREE.Vector3().setFromMatrixPosition(this.display[i])); }
    return c.divideScalar(ids.length);
  }

  frameAll(): void { this.frameBox(this.worldBox); }

  frameSelected(): void {
    const box = new THREE.Box3();
    for (const i of this.selected) {
      const b = this.instances[i].source ? this.localBounds.get(this.instances[i].source!) : null;
      if (b && !b.isEmpty()) { box.union(b.clone().applyMatrix4(this.display[i])); }
      else { box.expandByPoint(new THREE.Vector3().setFromMatrixPosition(this.display[i])); }
    }
    const c = this.selectionCenter();
    if (this.selectedPoint && c) { box.expandByPoint(c).expandByScalar(this.extent * 0.02); }
    if (box.isEmpty()) { this.frameAll(); } else { this.frameBox(box); }
  }

  // ------------------------------------------------------------------ overlay

  refreshOverlay(): void {
    this.overlay.traverse(o => { const g = (o as THREE.Mesh).geometry; if (g) { g.dispose(); } });
    this.overlay.clear();
    this.overlayHolders.clear();
    const mat = this.overlayMaterial;
    for (const i of this.selected) {
      const inst = this.instances[i];
      const b = inst.source ? this.localBounds.get(inst.source) : null;
      if (!b || b.isEmpty()) { continue; }
      const cube = new THREE.BoxGeometry(...b.getSize(new THREE.Vector3()).toArray());
      const box = new THREE.LineSegments(new THREE.EdgesGeometry(cube), mat);
      cube.dispose();
      box.position.copy(b.getCenter(new THREE.Vector3()));
      const holder = new THREE.Group();
      holder.matrixAutoUpdate = false;
      const axis = this.billboards.get(i);
      holder.matrix.copy(axis ? this.billboardMatrix(i, axis) : this.currentMatrix(i));
      holder.add(box);
      holder.renderOrder = 20;
      this.overlay.add(holder);
      this.overlayHolders.set(i, holder);
    }
    for (const p of this.points) {
      const pillar = p.obj.children[0] as THREE.Mesh;
      (pillar.material as THREE.MeshBasicMaterial).color.setHex(p.name === this.selectedPoint ? SELECT_COLOR : pointColor(p.kind));
    }
  }

  // The placement (never a billboard's camera-facing turn, so a move commits the real transform).
  private currentMatrix(i: number): THREE.Matrix4 {
    return this.live[i] ?? this.display[i];
  }

  // ------------------------------------------------------------------ gizmo

  private updateGizmo(): void {
    if (this.gizmoDrag) { return; }
    const t = this.toolValue;
    const center = this.selectionCenter();
    const canMove = this.selectedPoint ? t === 'move' : this.movable().length > 0;
    if (this.editableValue && (t === 'move' || t === 'rotate' || t === 'scale') && center && canMove) {
      this.pivot.position.copy(center);
      this.pivot.rotation.set(0, 0, 0);
      const ids = this.movable();
      const local = this.localSpace && !this.selectedPoint && ids.length === 1;
      if (local) {
        const q = new THREE.Quaternion();
        this.currentMatrix(ids[0]).decompose(new THREE.Vector3(), q, new THREE.Vector3());
        this.pivot.quaternion.copy(q);
      }
      this.gizmo.setSpace(local ? 'local' : 'world');
      this.pivot.scale.set(1, 1, 1);
      this.pivot.updateMatrixWorld(true);
      this.gizmo.setMode(t === 'move' ? 'translate' : t);
      // Props stand upright: rotate about Y only; points move on the ground plane.
      this.gizmo.showX = t !== 'rotate';
      this.gizmo.showZ = t !== 'rotate';
      this.gizmo.showY = !this.selectedPoint;
      this.gizmo.attach(this.pivot);
    } else {
      this.gizmo.detach();
    }
  }

  protected override onGizmoDragging(dragging: boolean): void {
    if (dragging) {
      this.pivot.updateMatrixWorld(true);
      const snapshot = new Map<number, THREE.Matrix4>();
      for (const i of this.movable()) { snapshot.set(i, this.currentMatrix(i).clone()); }
      this.gizmoDrag = { start: this.pivot.matrixWorld.clone(), snapshot, point: this.selectionCenter() ?? undefined };
      return;
    }
    const drag = this.gizmoDrag;
    this.gizmoDrag = null;
    this.endDragCulling();
    if (!drag) { return; }
    this.pivot.updateMatrixWorld(true);
    const delta = this.pivot.matrixWorld.clone().multiply(drag.start.clone().invert());
    if (isIdentity(delta)) { return; }
    if (this.selectedPoint && drag.point) {
      const p = drag.point.clone().applyMatrix4(delta);
      this.callbacks.onPointMove?.(this.selectedPoint, [p.x, p.y, p.z]);
      return;
    }
    const kind = this.toolValue === 'rotate' ? 'rotate' : this.toolValue === 'scale' ? 'scale' : 'move';
    const items = [...drag.snapshot].map(([index, m]) => ({ index, matrix: toModelArray(delta.clone().multiply(m)) }));
    this.callbacks.onTransformCommit?.(items, kind);
  }

  protected override onGizmoChange(): void {
    const drag = this.gizmoDrag;
    if (!drag) { return; }
    if (this.toolValue === 'scale') {
      // Placements carry one scale (the props panel and the game's clutter): an axis handle scales all three.
      const s = this.pivot.scale;
      const u = [s.x, s.y, s.z].reduce((a, b) => Math.abs(b - 1) > Math.abs(a - 1) ? b : a, 1);
      s.setScalar(u);
    }
    this.pivot.updateMatrixWorld(true);
    const delta = this.pivot.matrixWorld.clone().multiply(drag.start.clone().invert());
    if (this.selectedPoint && drag.point) {
      const p = this.points.find(x => x.name === this.selectedPoint);
      if (p) { const at = drag.point.clone().applyMatrix4(delta); p.obj.position.set(at.x - p.x, at.y - p.y, at.z - p.z); }
      return;
    }
    const touched = new Set<THREE.InstancedMesh>();
    for (const [i, m] of drag.snapshot) {
      const next = delta.clone().multiply(m);
      this.live[i].copy(next);
      // Billboards are drawn from their live placement on the next frame.
      if (this.billboards.has(i)) { this.billboardsDirty = true; continue; }
      for (const s of this.slots[i]) { s.mesh.setMatrixAt(s.slot, next); touched.add(s.mesh); }
      this.overlayHolders.get(i)?.matrix.copy(next);
    }
    touched.forEach(m => {
      m.instanceMatrix.needsUpdate = true;
      // Culling would use stale bounds mid-drag; turn it off until the drag ends rather than recompute every frame.
      if (!this.dragMeshes.has(m)) { this.dragMeshes.set(m, m.frustumCulled); m.frustumCulled = false; }
    });
  }

  private endDragCulling(): void {
    for (const [mesh, culled] of this.dragMeshes) { mesh.computeBoundingSphere(); mesh.frustumCulled = culled; }
    this.dragMeshes.clear();
  }

  // ------------------------------------------------------------------ pointer

  private hit(clientX: number, clientY: number): THREE.Intersection | null {
    const targets: THREE.Object3D[] = this.content.children.filter(c => c.visible);
    if (this.pointGroup.visible) { targets.push(...this.pointGroup.children); }
    return this.rayFrom(clientX, clientY).intersectObjects(targets, true)[0] ?? null;
  }

  private instanceOf(hit: THREE.Intersection): number | null {
    const ids = hit.object.userData['instances'] as number[] | undefined;
    return ids && hit.instanceId !== undefined ? ids[hit.instanceId] ?? null : null;
  }

  private pointOf(hit: THREE.Intersection): string | null {
    let o: THREE.Object3D | null = hit.object;
    while (o) { if (o.userData['point']) { return o.userData['point'] as string; } o = o.parent; }
    return null;
  }

  private readonly onPointerDown = (e: PointerEvent): void => {
    if (e.button !== 0 || this.orbitWithAlt) { return; }
    if (this.gizmo.dragging || (this.gizmo as unknown as { axis: string | null }).axis) { return; }
    if (this.toolValue === 'place') {
      if (!this.editableValue) { return; }
      const h = this.hit(e.clientX, e.clientY);
      if (h && !this.pointOf(h)) { this.callbacks.onPlace?.([h.point.x, h.point.y, h.point.z]); }
      return;
    }
    this.canvas.setPointerCapture(e.pointerId);
    this.dragStart = { x: e.clientX, y: e.clientY };
  };

  private readonly onPointerMove = (e: PointerEvent): void => {
    if (this.dragStart && (e.buttons & 1)) {
      const dx = e.clientX - this.dragStart.x, dy = e.clientY - this.dragStart.y;
      if (Math.hypot(dx, dy) > 4) {
        const rect = this.canvas.getBoundingClientRect();
        Object.assign(this.boxEl.style, {
          display: 'block',
          left: `${Math.min(e.clientX, this.dragStart.x) - rect.left}px`,
          top: `${Math.min(e.clientY, this.dragStart.y) - rect.top}px`,
          width: `${Math.abs(dx)}px`,
          height: `${Math.abs(dy)}px`,
        });
      }
      return;
    }
    if (e.buttons !== 0) { return; }
    const h = this.hit(e.clientX, e.clientY);
    if (!h) { this.callbacks.onHover?.(null); return; }
    const point = this.pointOf(h);
    const i = point ? null : this.instanceOf(h);
    const p = `${h.point.x.toFixed(1)}, ${h.point.y.toFixed(1)}, ${h.point.z.toFixed(1)}`;
    this.callbacks.onHover?.(point ? `point ${point}` : i !== null ? `${this.instances[i].kind} ${this.instances[i].model} · ${p}` : p);
  };

  // Double-click a prop: open its model in the Model Editor.
  private readonly onDoubleClick = (e: MouseEvent): void => {
    const h = this.hit(e.clientX, e.clientY);
    const i = h && !this.pointOf(h) ? this.instanceOf(h) : null;
    if (i !== null) { this.callbacks.onOpenModel?.(i); }
  };

  private readonly onPointerUp = (e: PointerEvent): void => {
    const start = this.dragStart;
    this.dragStart = null;
    if (!start || e.button !== 0) { return; }
    const boxed = this.boxEl.style.display === 'block';
    this.boxEl.style.display = 'none';
    const subtract = e.ctrlKey || e.metaKey;
    if (!e.shiftKey && !subtract) { this.selected.clear(); this.selectedPoint = null; }
    if (boxed) {
      const rect = this.canvas.getBoundingClientRect();
      const minX = Math.min(start.x, e.clientX) - rect.left, maxX = Math.max(start.x, e.clientX) - rect.left;
      const minY = Math.min(start.y, e.clientY) - rect.top, maxY = Math.max(start.y, e.clientY) - rect.top;
      this.instances.forEach((inst, i) => {
        if (!this.instanceVisible(i) || !this.selectable(i) || layerOf(inst) === 'world' || layerOf(inst) === 'sky') { return; }
        const r = this.screenRect(i, rect);
        if (r && r.maxX >= minX && r.minX <= maxX && r.maxY >= minY && r.minY <= maxY) { if (subtract) { this.selected.delete(i); } else { this.selected.add(i); } }
      });
    } else {
      const h = this.hit(e.clientX, e.clientY);
      const point = h ? this.pointOf(h) : null;
      const i = h && !point ? this.instanceOf(h) : null;
      if (point) { if (!this.lockedLayers.has('points')) { this.selected.clear(); this.selectedPoint = point; } }
      else if (i !== null && layerOf(this.instances[i]) !== 'world' && this.selectable(i)) {
        if (subtract) { this.selected.delete(i); } else { this.selected.add(i); }
      }
    }
    this.selectionChanged();
  };

}
