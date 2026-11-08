// The Map Editor's 3D view: the composed map (worldmodel, placed props, clutter, sky) drawn as one instanced mesh per
// model chunk, AIMAPS points as labelled pillars on the ground, instance / point selection (click, Shift / Ctrl,
// box drag), a move / rotate / scale gizmo with live preview, place-on-surface, and WASD fly. Previews only: commits
// go to the page, which sends them to the bridge (moveInstance / moveClutter / movePoint / addClutter ...).

import * as THREE from 'three';
import { placementMatrix, samplePlacement, surfaceNormal, type PlacementOptions, type PlacementSample, type PlacementTransform } from './placement';
import { dabPoints, keepApart, seededRandom, slopeOk, withinRadius, type ScatterOptions } from './scatter';
import { applyDrag, type PivotMode } from './arrange';
import type { Measured } from './mapAnalysis';
import { SELECT_COLOR, ViewportCore, billboardYaw, chunkGeometry, isIdentity, labelSprite, type MaterialSpec } from '../webview/zipper-core/viewport-core';
import type { EditorMesh } from '../webview/model-editor/mesh-data';
import { propDrawChunks } from './propFaces';
import { groupInstancesByLayer, isSkyChunk, layerOf, movable as isMovable, pointColor } from '../mapModel';
import { actionIconCanvas, objectiveIconCanvas, type ActionKind } from '../shared/actionIcons';
import { markerAnchor, type ActionMarker } from './actionMarkers';
import { gearLocal, heldWeaponMatrix, poseBones, skinChunk, type PoseClip } from '../webview/zipper-core/skinning';
import { yawOf } from './actorFigures';
import { buildPathObject, buildRegionObject } from './mapShapes';
import { buildCollisionObject, disposeGroup, type CollisionFaceWire } from './collisionLayer';
import type { MapPathWire, MapRegionWire } from '../webview/model-editor/asset-client';

export type MapTool = 'select' | 'move' | 'rotate' | 'scale' | 'place' | 'paint' | 'measure';
/** A saved camera: where it stands and what it looks at. */
export interface CameraState { position: [number, number, number]; target: [number, number, number] }
/** What a move drag snaps to beyond the increment snap: nothing, the surface under each item, or the nearest vertex under the cursor. */
export type SnapTarget = 'increment' | 'surface' | 'vertex';
export interface BrushOptions extends ScatterOptions { erase: boolean }
export type MapLayer = 'world' | 'reference' | 'clutter' | 'sky' | 'points' | 'paths' | 'regions';

export interface MapInstance {
  caNode?: boolean;
  // Battlefront: moved by id like a CA node, but several can move in one batch.
  multiMove?: boolean;
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

export interface MapPointInfo { name: string; team: number; kind: string; x: number; y?: number | null; z: number; canMove?: boolean }

export interface MapModel { mesh: EditorMesh; textureNamespace: string | null }

export interface MapCallbacks {
  onSelectionChange?: () => void;
  onHover?: (text: string | null) => void;
  // New model-space world matrices for moved instances.
  onTransformCommit?: (items: Array<{ index: number; matrix: number[] }>, kind: 'move' | 'rotate' | 'scale') => void;
  onPointMove?: (name: string, position: [number, number, number]) => void;
  onPlace?: (transform: PlacementTransform) => void;
  onOpenModel?: (index: number) => void;
  onCameraChange?: () => void;
  // The clutter brush: placements to add (model-space matrices), or clutter instances to remove, at the end of a stroke.
  onScatter?: (matrices: number[][]) => void;
  onErase?: (indices: number[]) => void;
  // What a gizmo drag is doing right now (null when it ends), for the status bar.
  onDragInfo?: (text: string | null) => void;
  // The measure tool: the distance between its two points (null when cleared).
  onMeasure?: (units: number | null, points: number) => void;
  // Walk mode turned itself off (Escape).
  onWalkEnd?: () => void;
  // A marker above the map was clicked: an action icon (its prop is selected first) or a HUD objective.
  onMarkerPick?: (pick: MarkerPick) => void;
}

/** An actor drawn as its character at an AIMAPS start point, posed by a clip, holding a weapon in its right hand. */
export interface ActorFigure {
  point: string; character: MapModel; clip: PoseClip | null; weapon: MapModel | null; weaponName?: string | null;
  /** Worn gear (character.rdr): each model on its bone at its offset (units, degrees). */
  gear?: Array<{ model: MapModel; bone: string; position: number[]; rotation: number[] }>;
  actor?: string; clipName?: string | null;
  /** 0-7 eighths of a turn clockwise from north (-Z); the models face north unturned. */
  heading?: number | null;
  /** [x, z] from the point (a squad mate standing beside the player), else at the point itself. */
  offset?: [number, number] | null;
}

/** What a click on a 3D marker picked. */
export type MarkerPick = { kind: 'action'; geoIndex: number; instance: number } | { kind: 'objective'; row: number };
/** A HUD objective's map location (hudobjectives.rdr LOCATION is x, z). */
export interface ObjectiveMarker { row: number; x: number; z: number; label: string; secondary: boolean }

// Screen height of a marker icon in CSS pixels, whatever the distance.
const MARKER_PX = 26;

// Model space is Y up like the display, so placements are used as they are (column-major).
function toDisplay(m: ArrayLike<number>): THREE.Matrix4 {
  return new THREE.Matrix4().fromArray(Array.from(m));
}

// A hidden instance's matrix: nothing drawn, nothing picked.
const ZERO = new THREE.Matrix4().makeScale(0, 0, 0);

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
  private readonly placementGhost = new THREE.Group();
  private readonly placementMaterial = new THREE.MeshBasicMaterial({ color: 0x66ddbb, transparent: true, opacity: 0.45, depthWrite: false, side: THREE.DoubleSide });
  private placementLayer: MapLayer = 'clutter';
  private placementOptions: PlacementOptions | null = null;
  private placementSample: PlacementSample = { yaw: 0, scale: 1 };
  private placementSource: EditorMesh | null = null;
  private readonly placementCameraPosition = new THREE.Vector3(NaN, NaN, NaN);
  private readonly placementProjection = new THREE.Matrix4();
  private readonly placementCameraRotation = new THREE.Quaternion();
  private placementPointer: { x: number; y: number } | null = null;
  private readonly overlay = new THREE.Group();
  private readonly pointGroup = new THREE.Group();
  // Battlefront paths and regions (view only).
  private readonly pathGroup = new THREE.Group();
  private readonly regionGroup = new THREE.Group();
  private instances: MapInstance[] = [];
  private display: THREE.Matrix4[] = [];
  // Placements as they stand right now, including a gizmo drag in progress (what a commit sends).
  private live: THREE.Matrix4[] = [];
  private slots: Slot[][] = [];
  // Facade clutter: flat tree cards the game turns about Y to face the camera. Value = the card's flat axis.
  private readonly billboards = new Map<number, 'x' | 'z'>();
  private readonly overlayHolders = new Map<number, THREE.Object3D>();
  private readonly lastBillboardCamera = new THREE.Vector3(NaN, NaN, NaN);
  private readonly lastBillboardRotation = new THREE.Quaternion();
  private lastBillboardOrtho = false;
  private billboardsDirty = true;
  private targets: Array<{ mesh: THREE.Mesh; spec: MaterialSpec }> = [];
  private localBounds = new Map<string, THREE.Box3>();
  // The decoded meshes the current scene was built from (a reload with the same ones only moves instances).
  private builtModels = new Map<string, MapModel>();
  private readonly overlayMaterial = new THREE.LineBasicMaterial({ color: SELECT_COLOR, depthTest: false, transparent: true });
  // Meshes whose culling is off while a gizmo drag moves their instances (bounds are recomputed when it ends).
  private readonly dragMeshes = new Map<THREE.InstancedMesh, boolean>();
  // `figure`: the actor drawn at the point, kept in its own group so it shows with the Points layer hidden.
  private points: Array<MapPointInfo & { y: number; obj: THREE.Object3D; figure?: THREE.Object3D }> = [];
  private readonly figureGroup = new THREE.Group();
  // The Collision view (View menu): the map's solid faces, never picked.
  private readonly collisionGroup = new THREE.Group();
  // World models whose sky chunks moved to the Sky layer.
  private worldSky: Array<{ source: string; nodes: string[] }> = [];
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
  private gizmoDrag: { start: THREE.Matrix4; snapshot: Map<number, THREE.Matrix4>; point?: THREE.Vector3; centre: THREE.Vector3; bottoms: Map<number, number> } | null = null;
  private snapTargetValue: SnapTarget = 'increment';
  private pivotModeValue: PivotMode = 'center';
  private lastPointer: { x: number; y: number } | null = null;
  // The clutter brush: its options, the model it paints (its clutter is what it keeps apart from and erases), the ring
  // under the cursor, and the stroke in progress.
  private brush: BrushOptions = { radius: 20, spacing: 5, density: 8, maxSlope: 35, erase: false };
  private brushModel: string | null = null;
  private readonly brushRing: THREE.LineLoop;
  private readonly strokeMarks: THREE.Points;
  private stroke: { erasing: boolean; matrices: THREE.Matrix4[]; erase: Set<number>; last: THREE.Vector3 | null; screen: { x: number; y: number };
    random: () => number; } | null = null;
  private readonly eraseMaterial = new THREE.LineBasicMaterial({ color: 0xff5544, depthTest: false, transparent: true });
  // Instances hidden with H / isolate (drawn at zero scale, never picked).
  private readonly hiddenInstances = new Set<number>();
  // The measure tool's points and the line between them.
  private measurePoints: THREE.Vector3[] = [];
  private readonly measureLine = new THREE.Line(new THREE.BufferGeometry(), new THREE.LineBasicMaterial({ color: 0xffd040, depthTest: false, transparent: true }));
  private readonly measureDots = new THREE.Points(new THREE.BufferGeometry(), new THREE.PointsMaterial({ color: 0xffd040, size: 8, sizeAttenuation: false, depthTest: false }));
  // Walk mode: the eye stays this high over the ground under it; the fly speed it replaced comes back after.
  private walking: { eye: number; flySpeed: number } | null = null;
  private worldBox = new THREE.Box3();
  // Icons floating over props with map object actions, and over HUD objective locations (view only).
  private readonly markerGroup = new THREE.Group();
  private actionMarkerList: ActionMarker[] = [];
  private objectiveMarkerList: ObjectiveMarker[] = [];
  private showActionMarkersValue = true;
  private showObjectiveMarkersValue = true;
  private selectedObjective: number | null = null;
  private readonly markerTextures = new Map<string, THREE.CanvasTexture>();
  // Actors drawn as characters at their start points (by point name, lower case), and the meshes drawn for them.
  private figures = new Map<string, ActorFigure[]>();
  private figureTargets: Array<{ mesh: THREE.Mesh; spec: MaterialSpec }> = [];
  private lastPoints: MapPointInfo[] = [];

  constructor(canvas: HTMLCanvasElement, private readonly callbacks: MapCallbacks = {}) {
    super(canvas, () => callbacks.onCameraChange?.());
    this.scene.add(this.content, this.overlay, this.pointGroup, this.pathGroup, this.regionGroup, this.figureGroup, this.placementGhost, this.collisionGroup);
    const ring = new THREE.BufferGeometry().setFromPoints(Array.from({ length: 48 }, (_, k) => new THREE.Vector3(Math.cos(k / 48 * Math.PI * 2), 0, Math.sin(k / 48 * Math.PI * 2))));
    this.brushRing = new THREE.LineLoop(ring, new THREE.LineBasicMaterial({ color: 0x66ddbb, depthTest: false, transparent: true }));
    this.brushRing.renderOrder = 30;
    this.brushRing.visible = false;
    this.strokeMarks = new THREE.Points(new THREE.BufferGeometry(), new THREE.PointsMaterial({ color: 0x66ddbb, size: 6, sizeAttenuation: false, depthTest: false }));
    this.strokeMarks.renderOrder = 31;
    this.scene.add(this.brushRing, this.strokeMarks);
    this.measureLine.renderOrder = 32;
    this.measureDots.renderOrder = 33;
    this.scene.add(this.measureLine, this.measureDots);
    this.markerGroup.renderOrder = 40;
    this.scene.add(this.markerGroup);
    canvas.addEventListener('keydown', e => { if (e.key === 'Escape' && this.walking) { this.setWalk(false); this.callbacks.onWalkEnd?.(); } }, { signal: this.listenerAbort.signal });
    this.placementGhost.visible = false;
    this.placementGhost.matrixAutoUpdate = false;
    this.boxEl = document.createElement('div');
    this.boxEl.className = 'me-boxselect';
    canvas.parentElement?.appendChild(this.boxEl);
    canvas.addEventListener('pointerdown', this.onPointerDown);
    canvas.addEventListener('pointermove', this.onPointerMove);
    canvas.addEventListener('dblclick', this.onDoubleClick);
    window.addEventListener('pointerup', this.onPointerUp);
    canvas.addEventListener('pointerleave', () => { this.placementPointer = null; this.placementGhost.visible = false; this.brushRing.visible = false; this.callbacks.onHover?.(null); }, { signal: this.listenerAbort.signal });
    canvas.tabIndex = 0;
  }

  dispose(): void {
    window.removeEventListener('pointerup', this.onPointerUp);
    this.canvas.removeEventListener('pointerdown', this.onPointerDown);
    this.canvas.removeEventListener('pointermove', this.onPointerMove);
    this.canvas.removeEventListener('dblclick', this.onDoubleClick);
    this.boxEl.remove();
    this.setPlacementMesh(null);
    this.placementMaterial.dispose();
    this.clearContent();
    this.overlay.traverse(o => { (o as THREE.Mesh).geometry?.dispose(); });
    this.overlayMaterial.dispose();
    this.eraseMaterial.dispose();
    this.brushRing.geometry.dispose();
    (this.brushRing.material as THREE.Material).dispose();
    this.strokeMarks.geometry.dispose();
    (this.strokeMarks.material as THREE.Material).dispose();
    for (const o of [this.measureLine, this.measureDots]) { o.geometry.dispose(); (o.material as THREE.Material).dispose(); }
    this.clearMarkers();
    this.markerTextures.forEach(t => t.dispose());
    this.disposeCore();
  }

  protected override flyEnabled(): boolean { return true; }
  protected materialTargets(): Array<{ mesh: THREE.Mesh; spec: MaterialSpec }> { return this.figureTargets.length ? [...this.targets, ...this.figureTargets] : this.targets; }
  protected override onShadingChanged(): void { this.refreshOverlay(); }
  // The base constructor renders a first frame before this class's fields exist, hence the guard.
  protected override onFrame(): void {
    if (this.billboards) { this.updateBillboards(); }
    if (this.markerGroup) { this.updateMarkers(); }
    if (this.walking) { this.keepEyeHeight(); }
    if (this.placementGhost && this.placementPointer && this.toolValue === 'place') {
      const cameraChanged = !this.placementCameraPosition.equals(this.camera.position)
        || !this.placementCameraRotation.equals(this.camera.quaternion)
        || !this.placementProjection.equals(this.camera.projectionMatrix);
      if (cameraChanged) { this.updatePlacementGhost(); }
    }
  }

  setPlacementMesh(mesh: EditorMesh | null): void {
    if (mesh === this.placementSource) { return; }
    this.setPlacementParts(mesh ? [{ mesh }] : null, mesh);
  }

  /** The placement ghost from several meshes, each at its own matrix relative to the placement (a prefab's items). */
  setPlacementParts(parts: Array<{ mesh: EditorMesh; matrix?: number[] }> | null, key: EditorMesh | null = null): void {
    this.placementGhost.traverse(object => { (object as THREE.Mesh).geometry?.dispose(); });
    this.placementGhost.clear();
    this.placementSource = parts?.length ? key ?? parts[0].mesh : null;
    for (const part of parts ?? []) {
      for (const chunk of part.mesh.chunks) {
        const m = new THREE.Mesh(chunkGeometry(chunk), this.placementMaterial);
        if (part.matrix) { m.matrixAutoUpdate = false; m.matrix.fromArray(part.matrix); }
        this.placementGhost.add(m);
      }
    }
    this.updatePlacementGhost();
  }

  /** Where a placement dropped at this screen point would go (drag and drop from the asset browser), or null off the map. */
  placementAt(clientX: number, clientY: number): PlacementTransform | null {
    if (!this.placementOptions) { return null; }
    const hit = this.placementHit(clientX, clientY);
    if (!hit) { return null; }
    const sample = samplePlacement(this.placementOptions);
    return { ...sample, matrix: placementMatrix(hit.point, surfaceNormal(hit), sample, this.placementOptions.alignSurface).toArray() };
  }

  setPlacementOptions(options: PlacementOptions, layer: MapLayer = 'clutter'): void {
    this.placementLayer = layer;
    this.placementOptions = options;
    this.placementSample = samplePlacement(options);
    this.updatePlacementGhost();
  }

  private canPlace(): boolean {
    return this.toolValue === 'place' && this.editableValue && !!this.placementSource && !!this.placementOptions
      && !this.lockedLayers.has(this.placementLayer) && !this.hiddenLayers.has(this.placementLayer);
  }

  private updatePlacementGhost(): void {
    this.placementCameraPosition.copy(this.camera.position);
    this.placementCameraRotation.copy(this.camera.quaternion);
    this.placementProjection.copy(this.camera.projectionMatrix);
    this.placementGhost.visible = false;
    if (!this.canPlace() || !this.placementPointer) { return; }
    const { x, y } = this.placementPointer;
    const hit = this.placementHit(x, y);
    if (!hit) { return; }
    this.placementGhost.matrix.copy(placementMatrix(hit.point, surfaceNormal(hit), this.placementSample, this.placementOptions!.alignSurface));
    this.placementGhost.visible = true;
  }

  private placementHit(x: number, y: number): THREE.Intersection | null {
    // Point labels and the ghost never obscure placement surfaces.
    const surfaces = this.content.children.filter(object => object.visible && object.userData['layer'] !== 'sky');
    return this.rayFrom(x, y).intersectObjects(surfaces, false)[0] ?? null;
  }

  // ------------------------------------------------------------------ billboards

  // The matrix a facade card is drawn with: its placement's position and scale, turned about Y to face the camera.
  private billboardMatrix(i: number, axis: 'x' | 'z'): THREE.Matrix4 {
    const pos = new THREE.Vector3(), rot = new THREE.Quaternion(), scale = new THREE.Vector3();
    this.live[i].decompose(pos, rot, scale);

    // Turn the card's normal (+Z for a card flat in Z, +X for one flat in X) onto the camera direction.
    const yaw = billboardYaw(axis, this.camera, pos);
    return new THREE.Matrix4().compose(pos, rot.setFromAxisAngle(new THREE.Vector3(0, 1, 0), yaw), scale);
  }

  private updateBillboards(): void {
    if (!this.billboards.size) { return; }
    if (!this.billboardsDirty && this.lastBillboardCamera.equals(this.camera.position) && this.lastBillboardRotation.equals(this.camera.quaternion) && this.lastBillboardOrtho === this.isOrtho) { return; }
    this.lastBillboardCamera.copy(this.camera.position);
    this.lastBillboardRotation.copy(this.camera.quaternion);
    this.lastBillboardOrtho = this.isOrtho;
    this.billboardsDirty = false;
    const touched = new Set<THREE.InstancedMesh>();
    for (const [i, axis] of this.billboards) {
      const m = this.hiddenInstances.has(i) ? ZERO : this.billboardMatrix(i, axis);
      for (const s of this.slots[i]) { s.mesh.setMatrixAt(s.slot, m); touched.add(s.mesh); }
      this.overlayHolders.get(i)?.matrix.copy(m);
    }
    touched.forEach(mesh => { mesh.instanceMatrix.needsUpdate = true; });
  }

  // ------------------------------------------------------------------ content

  setScene(
    instances: MapInstance[],
    models: Map<string, MapModel>,
    textures: Record<string, import('../webview/zipper-core/texture-pixels').ModelTexturePixels>,
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
    this.hiddenInstances.clear();
    this.worldSky = [];
    this.builtModels = new Map([...models]);
    this.setTexturePixels(textures);
    this.instances = instances;
    this.display = instances.map(i => toDisplay(i.matrix));
    this.slots = instances.map(() => []);
    // MP82 draws fog across snow/rock layers that themselves use vertex-alpha blending.
    // Draw wholly translucent textures after those surfaces have established their depth.
    const translucentTextures = new Set(Object.keys(textures).filter(key => {
      const pixels = textures[key];
      return pixels.hasTransparency && !pixels.rgba.some((value, i) => i % 4 === 3 && value === 255);
    }));

    for (const { source, layer, indices: idx } of groupInstancesByLayer(instances)) {
      const model = models.get(source);
      if (!model) { continue; }
      const ns = model.textureNamespace;
      const box = new THREE.Box3();
      const skyNodes = new Set<string>();
      const drawChunks = layer === 'reference' ? propDrawChunks(model.mesh.chunks)
        : model.mesh.chunks.map(chunk => ({ chunk, frontOnly: false }));
      for (const { chunk, frontOnly } of drawChunks) {
        const geometry = chunkGeometry(chunk);
        geometry.computeBoundingBox();
        // A world model's sky dome draws with the Sky layer, and stays out of the map's bounds and the point drop.
        const chunkLayer: MapLayer = layer === 'world' && isSkyChunk(chunk) ? 'sky' : layer;
        if (chunkLayer === 'sky' && layer === 'world') { skyNodes.add(chunk.node || chunk.texture || 'sky'); }
        else if (geometry.boundingBox) { box.union(geometry.boundingBox); }
        // Texture keys are namespaced: the same .tif name can differ between archives.
        const texture = chunk.texture ? `${ns ?? ''}:${chunk.texture}`.toLowerCase() : null;
        const translucentEffect = !!texture && translucentTextures.has(texture)
          && (chunk.materialBlend ?? textures[texture].alphaBlend ?? false);
        const spec: MaterialSpec = { texture, materialBlend: chunk.materialBlend, colors: chunk.colors,
          waterMotion: chunk.waterMotion, translucentEffect: translucentEffect || !!chunk.waterMotion, frontOnly };
        const mesh = new THREE.InstancedMesh(geometry, this.materialFor(spec), idx.length);
        if (spec.translucentEffect) { mesh.renderOrder = 1; }
        idx.forEach((instIndex, slot) => {
          mesh.setMatrixAt(slot, this.display[instIndex]);
          this.slots[instIndex].push({ mesh, slot });
        });
        mesh.instanceMatrix.needsUpdate = true;
        mesh.computeBoundingSphere();
        mesh.userData['instances'] = idx;
        mesh.userData['layer'] = chunkLayer;
        mesh.userData['source'] = source;
        this.content.add(mesh);
        this.targets.push({ mesh, spec });
      }
      this.localBounds.set(source, box);
      if (skyNodes.size) { this.worldSky.push({ source, nodes: [...skyNodes].sort() }); }
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

    this.updateWorldBounds(points);
    this.flySpeed = this.worldBox.isEmpty() ? 100 : this.extent / 12;
    this.applyVisibility();
    this.buildPoints(points);
    this.buildMarkers();
    this.clearHelpers();
    this.buildGridAxes(this.worldBox);
    if (!keepCamera) { this.frameAll(); }
    this.refreshOverlay();
    this.updateGizmo();
    this.updatePlacementGhost();
  }

  private updateWorldBounds(points: MapPointInfo[]): void {
    this.worldBox.makeEmpty();
    this.instances.forEach((inst, i) => {
      const bounds = inst.source ? this.localBounds.get(inst.source) : null;
      if (!bounds || bounds.isEmpty() || layerOf(inst) === 'sky') { return; }
      this.worldBox.union(bounds.clone().applyMatrix4(this.display[i]));
    });
    if (!this.instances.length) {
      for (const p of points) { this.worldBox.expandByPoint(new THREE.Vector3(p.x, p.y ?? 0, p.z)); }
    }
    this.extent = this.worldBox.isEmpty() ? 100 : Math.max(10, this.worldBox.getSize(new THREE.Vector3()).length());
    if (this.worldBox.isEmpty()) { this.center.set(0, 0, 0); }
    else { this.worldBox.getCenter(this.center); }
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
      for (const s of this.slots[i]) { s.mesh.setMatrixAt(s.slot, this.hiddenInstances.has(i) ? ZERO : this.display[i]); touched.add(s.mesh); }
    });
    touched.forEach(m => { m.instanceMatrix.needsUpdate = true; m.computeBoundingSphere(); });
    this.billboardsDirty = true;
    this.updateWorldBounds(points);
    this.clearHelpers();
    this.buildGridAxes(this.worldBox);
    this.buildPoints(points);
    this.refreshOverlay();
    this.updateGizmo();
    this.updatePlacementGhost();
  }

  private clearContent(): void {
    const geos = new Set<THREE.BufferGeometry>();
    this.content.traverse(o => {
      const mesh = o as THREE.Mesh;
      if (mesh.geometry) { geos.add(mesh.geometry); }
      if (o instanceof THREE.InstancedMesh) { o.dispose(); }
    });
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
      // Figures draw with the shared material cache: those materials are not theirs to dispose.
      if (o.userData['figure']) { return; }
      const mat = mesh.material as THREE.Material | undefined;
      if (mat) { (mat as THREE.SpriteMaterial).map?.dispose(); mat.dispose(); }
    });
    this.pointGroup.clear();
    // Figures: their geometry is theirs, their materials are the shared cache's.
    this.figureGroup.traverse(o => { (o as THREE.Mesh).geometry?.dispose(); });
    this.figureGroup.clear();
    this.points = [];
    this.figureTargets = [];
  }

  /** Actors to draw as characters at their start points (their points' pillars give way to them). */
  setActorFigures(figures: ActorFigure[]): void {
    this.figures = new Map();
    for (const f of figures) { const key = f.point.toLowerCase(); this.figures.set(key, [...(this.figures.get(key) ?? []), f]); }
    this.buildPoints(this.lastPoints);
    this.refreshOverlay();
  }

  get figureCount(): number { return this.points.reduce((n, p) => n + (this.figures.get(p.name.toLowerCase())?.length ?? 0), 0); }

  /** Where each figure is drawn: its point and world bounds (for ui_state). */
  figureBounds(): Array<{ point: string; actor: string | null; heading: number | null; clip: string | null; weapon: string | null; min: number[]; max: number[] }> {
    const out: Array<{ point: string; actor: string | null; heading: number | null; clip: string | null; weapon: string | null; min: number[]; max: number[] }> = [];
    const r = (v: number) => Math.round(v * 10) / 10;
    for (const p of this.points) {
      if (!p.figure) { continue; }
      p.figure.updateMatrixWorld(true);
      for (const body of p.figure.children) {
        const f = body.userData['actorFigure'] as ActorFigure | undefined;
        const box = new THREE.Box3().setFromObject(body);
        out.push({ point: p.name, actor: f?.actor ?? null, heading: f?.heading ?? null, clip: f?.clipName ?? null, weapon: f?.weaponName ?? null,
          min: box.min.toArray().map(r), max: box.max.toArray().map(r) });
      }
    }
    return out;
  }

  // A figure: the character's chunks skinned by its clip's first frame, and its weapon on the right hand.
  private buildFigure(f: ActorFigure): THREE.Group {
    const group = new THREE.Group();
    const mesh = f.character.mesh;
    const world = mesh.skeleton ? poseBones(mesh.skeleton, f.clip, 0) : [];
    const add = (chunk: (typeof mesh.chunks)[number], ns: string | null, matrix?: THREE.Matrix4, skinned = false) => {
      const geometry = chunkGeometry(chunk);
      if (skinned && chunk.skin && mesh.skinInfluences) {
        const positions = new Float32Array(chunk.vertexCount * 3), normals = new Float32Array(chunk.vertexCount * 3);
        skinChunk(chunk, mesh.skinInfluences, world, positions, normals);
        geometry.setAttribute('position', new THREE.BufferAttribute(positions, 3));
        geometry.setAttribute('normal', new THREE.BufferAttribute(normals, 3));
        geometry.computeBoundingSphere();
      }
      const spec: MaterialSpec = { texture: chunk.texture ? `${ns ?? ''}:${chunk.texture}`.toLowerCase() : null, materialBlend: chunk.materialBlend,
        colors: chunk.colors, lit: true };
      const m = new THREE.Mesh(geometry, this.materialFor(spec));
      m.userData['figure'] = true;
      if (matrix) { m.matrixAutoUpdate = false; m.matrix.copy(matrix); }
      group.add(m);
      this.figureTargets.push({ mesh: m, spec });
    };
    for (const chunk of mesh.chunks) { add(chunk, f.character.textureNamespace, undefined, !!mesh.skeleton); }
    const grip = f.weapon && mesh.skeleton && heldWeaponMatrix(mesh.skeleton, world, f.clip);
    if (f.weapon && grip) {
      for (const chunk of f.weapon.mesh.chunks) { add(chunk, f.weapon.textureNamespace, grip); }
    }
    for (const g of mesh.skeleton ? f.gear ?? [] : []) {
      const bone = mesh.skeleton!.findIndex(b => b.name.toLowerCase() === g.bone.toLowerCase());
      if (bone < 0 || !world[bone]) { continue; }
      const at = world[bone].clone().multiply(gearLocal(g.position, g.rotation));
      // Eyes carry a shut copy for blinking: draw them open.
      for (const chunk of g.model.mesh.chunks) { if (!/_shut/i.test(chunk.texture ?? '')) { add(chunk, g.model.textureNamespace, at); } }
    }
    return group;
  }


  instanceList(): readonly MapInstance[] { return this.instances; }

  /** Sky drawn inside world models (their GEO node names), shown under the Sky layer. */
  skyInWorld(): ReadonlyArray<{ source: string; nodes: string[] }> { return this.worldSky; }

  refreshHelpers(): void {
    this.clearHelpers();
    this.buildGridAxes(this.worldBox);
  }

  // Target Y for the placement's pivot when its mesh rests on the surface beneath it.
  // A turret's pivot is at the gun, for example, several units above the base.
  groundedOriginY(i: number): number | null {
    const origin = new THREE.Vector3().setFromMatrixPosition(this.display[i]);
    let bottom = Infinity;
    const matrix = new THREE.Matrix4();
    for (const { mesh, slot } of this.slots[i]) {
      mesh.getMatrixAt(slot, matrix);
      const e = matrix.elements, pos = mesh.geometry.getAttribute('position');
      for (let v = 0; v < pos.count; v++) {
        bottom = Math.min(bottom, e[1] * pos.getX(v) + e[5] * pos.getY(v) + e[9] * pos.getZ(v) + e[13]);
      }
    }
    if (!Number.isFinite(bottom)) { return null; }
    // Start a little above the origin: a prop sunk into the ground still finds it, one inside a building finds the floor.
    const startY = Math.max(origin.y, bottom) + Math.max(1, this.extent * 0.002);
    const ray = new THREE.Raycaster(new THREE.Vector3(origin.x, startY, origin.z), new THREE.Vector3(0, -1, 0));
    const targets = this.content.children.filter(c => c.visible && c.userData['layer'] !== 'sky');
    for (const hit of ray.intersectObjects(targets, false)) {
      if (this.instanceOf(hit) !== i) { return hit.point.y + (origin.y - bottom); }
    }
    return null;
  }

  // ------------------------------------------------------------------ points

  private buildPoints(points: MapPointInfo[]): void {
    this.lastPoints = points;
    this.clearPoints();
    const height = Math.max(4, this.extent * 0.012);
    // Without a height from the AI grid: the highest terrain or prop surface under the point (buildings stand in holes
    // in the terrain), else the bottom of the map.
    const ground = this.content.children.filter(c => c.userData['layer'] === 'world' || c.userData['layer'] === 'reference');
    const ray = new THREE.Raycaster();
    for (const p of points) {
      let y = p.y;
      if (y == null || !Number.isFinite(y)) {
        ray.set(new THREE.Vector3(p.x, this.worldBox.isEmpty() ? 1000 : this.worldBox.max.y + 10, p.z), new THREE.Vector3(0, -1, 0));
        const hit = ray.intersectObjects(ground, false)[0];
        y = hit ? hit.point.y : this.worldBox.isEmpty() ? 0 : this.worldBox.min.y;
      }
      const color = pointColor(p.kind);
      const pillar = new THREE.Mesh(
        new THREE.CylinderGeometry(height * 0.08, height * 0.08, height, 10),
        new THREE.MeshBasicMaterial({ color, transparent: true, opacity: 0.85, depthWrite: false }),
      );
      pillar.position.set(p.x, y + height / 2, p.z);
      pillar.renderOrder = 10;
      pillar.userData['point'] = p.name;
      const figures = this.figures.get(p.name.toLowerCase()) ?? [];
      const label = labelSprite(p.name, '#ffffff', 32);
      // These labels live on the map, unlike selection/gizmo overlays. A sky dome
      // or building in front of a point must occlude both its pillar and its label.
      label.material.depthTest = true;
      label.material.depthWrite = false;
      label.position.set(p.x, y + height * 1.25, p.z);
      label.scale.set(height * 0.35 * (label.userData['aspect'] as number), height * 0.35, 1);
      label.renderOrder = 11;
      const group = new THREE.Group();
      group.add(pillar, label);
      let holder: THREE.Group | undefined;
      if (figures.length) {
        // The characters stand on the point (squad mates beside it), turned to their start's heading; a thin ring keeps
        // the point's colour (and the selection) readable. They live in their own group (a holder that follows the
        // point's drags), so hiding the Points layer leaves them.
        holder = new THREE.Group();
        for (const figure of figures) {
          const body = this.buildFigure(figure);
          body.position.set(p.x + (figure.offset?.[0] ?? 0), y, p.z + (figure.offset?.[1] ?? 0));
          body.rotation.y = yawOf(figure.heading);
          body.userData['actorFigure'] = figure;
          holder.add(body);
        }
        holder.userData['point'] = p.name;
        this.figureGroup.add(holder);
        pillar.geometry.dispose();
        (pillar as THREE.Mesh).geometry = new THREE.RingGeometry(3.2, 4.2, 24).rotateX(-Math.PI / 2);
        pillar.position.set(p.x, y + 0.3, p.z);
        label.position.set(p.x, y + 24, p.z);
        label.scale.set(5 * (label.userData['aspect'] as number), 5, 1);
      }
      group.userData['point'] = p.name;
      this.pointGroup.add(group);
      this.points.push({ ...p, y, obj: group, figure: holder });
    }
    this.pointGroup.visible = !this.hiddenLayers.has('points');
  }

  pointList(): ReadonlyArray<MapPointInfo & { y: number }> { return this.points; }

  // ------------------------------------------------------------------ markers (action icons, HUD objectives)

  /** Props with map object actions: one icon above each bound node's drawn placements. */
  setActionMarkers(markers: ActionMarker[]): void { this.actionMarkerList = markers; this.buildMarkers(); }
  /** HUD objective locations, drawn as flags standing on the ground. */
  setObjectiveMarkers(markers: ObjectiveMarker[]): void { this.objectiveMarkerList = markers; this.buildMarkers(); }
  get showActionMarkers(): boolean { return this.showActionMarkersValue; }
  set showActionMarkers(on: boolean) { this.showActionMarkersValue = on; this.buildMarkers(); }
  get showObjectiveMarkers(): boolean { return this.showObjectiveMarkersValue; }
  set showObjectiveMarkers(on: boolean) { this.showObjectiveMarkersValue = on; this.buildMarkers(); }
  /** The HUD objective shown as selected (its flag is outlined). */
  setSelectedObjective(row: number | null): void { this.selectedObjective = row; this.buildMarkers(); }
  /** Frames the camera on a HUD objective's flag. */
  frameObjective(row: number): void {
    const flag = this.markerGroup.children.find(c => (c.userData['marker'] as MarkerPick).kind === 'objective' && (c.userData['marker'] as { row: number }).row === row);
    const o = this.objectiveMarkerList.find(m => m.row === row);
    if (!o) { return; }
    const at = flag ? flag.position.clone() : new THREE.Vector3(o.x, this.groundBelow(o.x, o.z) ?? 0, o.z);
    this.frameBox(new THREE.Box3().setFromCenterAndSize(at, new THREE.Vector3().setScalar(Math.max(40, this.extent * 0.03))));
  }
  /** Markers drawn now (for checks and the AI). */
  markerCount(): { actions: number; objectives: number } {
    let actions = 0, objectives = 0;
    for (const c of this.markerGroup.children) {
      if (!c.visible) { continue; }
      if ((c.userData['marker'] as MarkerPick).kind === 'action') { actions++; } else { objectives++; }
    }
    return { actions, objectives };
  }

  private markerTexture(key: string, make: () => HTMLCanvasElement): THREE.CanvasTexture {
    let t = this.markerTextures.get(key);
    if (!t) { t = new THREE.CanvasTexture(make()); t.colorSpace = THREE.SRGBColorSpace; this.markerTextures.set(key, t); }
    return t;
  }

  private markerSprite(map: THREE.Texture, pick: MarkerPick): THREE.Sprite {
    const sprite = new THREE.Sprite(new THREE.SpriteMaterial({ map, depthTest: false, depthWrite: false, transparent: true }));
    // Anchored at the bottom middle: the icon stands on the point it marks.
    sprite.center.set(0.5, 0);
    sprite.renderOrder = 40;
    sprite.userData['marker'] = pick;
    return sprite;
  }

  private clearMarkers(): void {
    for (const c of this.markerGroup.children) { ((c as THREE.Sprite).material as THREE.Material).dispose(); }
    this.markerGroup.clear();
  }

  private buildMarkers(): void {
    if (!this.markerGroup) { return; }
    this.clearMarkers();
    if (this.showActionMarkersValue) {
      for (const m of this.actionMarkerList) {
        const live = m.instances.filter(i => i < this.instances.length);
        if (!live.length && !m.anchor) { continue; }
        const selected = live.some(i => this.selected.has(i));
        const kind: ActionKind = m.kind;
        const sprite = this.markerSprite(this.markerTexture(kind + (selected ? ':sel' : ''), () => actionIconCanvas(kind, 64, selected)),
          { kind: 'action', geoIndex: m.geoIndex, instance: live[0] ?? -1 });
        sprite.userData['instances'] = live;
        if (!live.length && m.anchor) { sprite.userData['anchor'] = m.anchor; sprite.position.set(...m.anchor); }
        sprite.name = m.node;
        this.markerGroup.add(sprite);
      }
    }
    if (this.showObjectiveMarkersValue) {
      for (const o of this.objectiveMarkerList) {
        const selected = o.row === this.selectedObjective;
        const sprite = this.markerSprite(this.markerTexture(`objective:${o.secondary ? 2 : 1}:${selected ? 1 : 0}`, () => objectiveIconCanvas(o.secondary, selected)),
          { kind: 'objective', row: o.row });
        const ground = this.instances.length ? this.groundBelow(o.x, o.z) : null;
        sprite.position.set(o.x, ground ?? (this.worldBox.isEmpty() ? 0 : this.worldBox.min.y), o.z);
        sprite.name = o.label;
        this.markerGroup.add(sprite);
      }
    }
    this.updateMarkers();
  }

  // Each frame: action icons follow their props (drags included) and hide with them; every icon keeps its screen size.
  private updateMarkers(): void {
    if (!this.markerGroup.children.length) { return; }
    const host = this.canvas.parentElement ?? this.canvas;
    const h = Math.max(1, host.clientHeight);
    const view = new THREE.Vector3();
    this.camera.getWorldDirection(view);
    const toMarker = new THREE.Vector3();
    for (const c of this.markerGroup.children) {
      const sprite = c as THREE.Sprite;
      const pick = sprite.userData['marker'] as MarkerPick;
      if (pick.kind === 'action' && sprite.userData['anchor']) {
        // A bare node: it stands where the node is and shows with the world.
        const a = sprite.userData['anchor'] as [number, number, number];
        sprite.position.set(a[0], a[1], a[2]);
        sprite.visible = !this.hiddenLayers.has('world');
        if (!sprite.visible) { continue; }
      } else if (pick.kind === 'action') {
        const boxes: Array<{ min: number[]; max: number[] }> = [];
        for (const i of sprite.userData['instances'] as number[]) {
          if (!this.instanceVisible(i)) { continue; }
          const inst = this.instances[i];
          const local = inst.source ? this.localBounds.get(inst.source) : null;
          const box = local && !local.isEmpty() ? local.clone().applyMatrix4(this.currentMatrix(i))
            : new THREE.Box3().setFromPoints([new THREE.Vector3().setFromMatrixPosition(this.currentMatrix(i))]);
          boxes.push({ min: box.min.toArray(), max: box.max.toArray() });
        }
        const at = markerAnchor(boxes, 0);
        sprite.visible = !!at;
        if (!at) { continue; }
        sprite.position.set(at[0], at[1], at[2]);
      }
      // World units per pixel at the marker's depth.
      let perPixel: number;
      if (this.isOrtho) {
        const o = this.camera as THREE.OrthographicCamera;
        perPixel = (o.top - o.bottom) / o.zoom / h;
      } else {
        const depth = Math.max(0.01, view.dot(toMarker.subVectors(sprite.position, this.camera.position)));
        perPixel = 2 * depth * Math.tan(THREE.MathUtils.degToRad(this.persp.fov / 2)) / h;
      }
      const size = MARKER_PX * perPixel;
      sprite.scale.set(size, size, 1);
      // A small gap between the prop's top and its icon.
      if (pick.kind === 'action') { sprite.position.y += 6 * perPixel; }
    }
  }

  private markerAt(clientX: number, clientY: number): THREE.Sprite | null {
    const visible = this.markerGroup.children.filter(c => c.visible);
    if (!visible.length) { return null; }
    const hit = this.rayFrom(clientX, clientY).intersectObjects(visible, false)[0];
    return hit ? hit.object as THREE.Sprite : null;
  }

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
    this.pathGroup.visible = !this.hiddenLayers.has('paths');
    this.regionGroup.visible = !this.hiddenLayers.has('regions');
    this.updatePlacementGhost();
    this.refreshOverlay();
  }

  private instanceVisible(i: number): boolean {
    const inst = this.instances[i];
    return !this.hiddenInstances.has(i) && !this.hiddenLayers.has(layerOf(inst)) && !(inst.source && this.hiddenModels.has(inst.source));
  }

  // ------------------------------------------------------------------ hide / isolate (view only)

  /** Hides instances from the view and from picking (H); the map is not changed. */
  hideInstances(indices: number[]): void {
    for (const i of indices) { if (this.instances[i]) { this.hiddenInstances.add(i); this.selected.delete(i); } }
    this.applyInstanceHiding();
  }

  /** Hides every prop and clutter instance but these (Shift+H); the world model and sky stay. */
  isolateInstances(indices: number[]): void {
    const keep = new Set(indices);
    this.instances.forEach((inst, i) => { if (!keep.has(i) && isMovable(inst)) { this.hiddenInstances.add(i); } });
    this.applyInstanceHiding();
  }

  unhideInstances(): void {
    this.hiddenInstances.clear();
    this.applyInstanceHiding();
  }

  get hiddenCount(): number { return this.hiddenInstances.size; }

  private applyInstanceHiding(): void {
    const touched = new Set<THREE.InstancedMesh>();
    this.instances.forEach((_, i) => {
      if (this.billboards.has(i)) { return; }
      for (const s of this.slots[i]) { s.mesh.setMatrixAt(s.slot, this.hiddenInstances.has(i) ? ZERO : this.currentMatrix(i)); touched.add(s.mesh); }
    });
    touched.forEach(m => { m.instanceMatrix.needsUpdate = true; m.computeBoundingSphere(); });
    this.billboardsDirty = true;
    this.selectionChanged();
  }

  // ------------------------------------------------------------------ camera bookmarks and walk mode

  cameraState(): CameraState {
    return { position: this.camera.position.toArray() as [number, number, number], target: this.controls.target.toArray() as [number, number, number] };
  }

  setCameraState(c: CameraState): void {
    if (this.walking) { this.setWalk(false); this.callbacks.onWalkEnd?.(); }
    this.camera.position.fromArray(c.position);
    this.controls.target.fromArray(c.target);
    this.controls.update();
  }

  get isWalking(): boolean { return !!this.walking; }

  // ------------------------------------------------------------------ minimap

  /**
   * For the minimap: the ground point the view is centred on (what the camera looks at; the walker in walk mode), which
   * way it faces, and where the camera itself is.
   */
  viewHeading(): { x: number; z: number; forwardX: number; forwardZ: number; cameraX: number; cameraZ: number } {
    const dir = this.camera.getWorldDirection(new THREE.Vector3());
    const p = this.walking ? this.camera.position : this.controls.target;
    // Straight down, the top of the screen says which way the view faces.
    if (Math.abs(dir.y) > 0.999) { dir.set(0, 1, 0).applyQuaternion(this.camera.quaternion); }
    return { x: p.x, z: p.z, forwardX: dir.x, forwardZ: dir.z, cameraX: this.camera.position.x, cameraZ: this.camera.position.z };
  }

  /** Moves the view (camera and what it looks at, or the walker) over the ground point x / z, keeping its height and facing. */
  jumpTo(x: number, z: number): void {
    const p = this.walking ? this.camera.position : this.controls.target;
    const dx = x - p.x, dz = z - p.z;
    this.camera.position.x += dx; this.camera.position.z += dz;
    this.controls.target.x += dx; this.controls.target.z += dz;
    this.controls.update();
  }

  /** The drawn map's ground extent (x / z), or null before a scene is built. */
  sceneExtent(): { minX: number; maxX: number; minZ: number; maxZ: number } | null {
    if (this.worldBox.isEmpty()) { return null; }
    return { minX: this.worldBox.min.x, maxX: this.worldBox.max.x, minZ: this.worldBox.min.z, maxZ: this.worldBox.max.z };
  }

  /**
   * The map seen straight down (north, -Z, up; +X right) over `area`, as a PNG data URL `size` pixels square: the
   * minimap's picture for maps without a tactical map. Only the map itself is drawn (no points, markers, helpers, sky).
   */
  renderTopDown(area: { minX: number; maxX: number; minZ: number; maxZ: number }, size = 1024): string | null {
    if (!this.content.children.length || this.worldBox.isEmpty()) { return null; }
    const hidden: THREE.Object3D[] = [];
    for (const child of this.scene.children) { if (child !== this.content && child.visible) { child.visible = false; hidden.push(child); } }
    for (const child of this.content.children) { if (child.visible && child.userData['layer'] === 'sky') { child.visible = false; hidden.push(child); } }
    const cam = new THREE.OrthographicCamera((area.minX - area.maxX) / 2, (area.maxX - area.minX) / 2, (area.maxZ - area.minZ) / 2, (area.minZ - area.maxZ) / 2, 1, 1e6);
    cam.up.set(0, 0, -1);
    cam.position.set((area.minX + area.maxX) / 2, this.worldBox.max.y + 100, (area.minZ + area.maxZ) / 2);
    cam.lookAt(cam.position.x, this.worldBox.min.y, cam.position.z);
    cam.updateProjectionMatrix();
    const target = new THREE.WebGLRenderTarget(size, size, { samples: 4 });
    const background = this.scene.background;
    this.scene.background = new THREE.Color(0x101418);
    const pixels = new Uint8Array(size * size * 4);
    try {
      this.renderer.setRenderTarget(target);
      this.renderer.render(this.scene, cam);
      this.renderer.readRenderTargetPixels(target, 0, 0, size, size, pixels);
    } finally {
      this.renderer.setRenderTarget(null);
      this.scene.background = background;
      for (const o of hidden) { o.visible = true; }
      target.dispose();
    }
    const canvas = document.createElement('canvas');
    canvas.width = canvas.height = size;
    const ctx = canvas.getContext('2d');
    if (!ctx) { return null; }
    const image = ctx.createImageData(size, size);
    // WebGL rows run bottom-up.
    for (let y = 0; y < size; y++) { image.data.set(pixels.subarray((size - 1 - y) * size * 4, (size - y) * size * 4), y * size * 4); }
    ctx.putImageData(image, 0, 0);
    return canvas.toDataURL('image/png');
  }

  /**
   * Walk mode: the camera stands at a player's eye height (16 units) on the ground under the view's centre and keeps
   * to the ground as WASD moves it (Shift runs); dragging looks round. Escape or setWalk(false) gives the free
   * camera back.
   */
  setWalk(on: boolean): void {
    if (!on) {
      if (this.walking) { this.flySpeed = this.walking.flySpeed; this.walking = null; }
      return;
    }
    if (this.walking) { return; }
    const eye = 16;
    const at = this.controls.target.clone();
    let ground = this.groundBelow(at.x, at.z);
    if (ground === null) {
      // Nothing under the orbit target (Frame all on an L-shaped map centres on empty space): stand on the surface
      // in the middle of the view instead.
      const rect = this.canvas.getBoundingClientRect();
      const targets = this.content.children.filter(c => c.visible && (c.userData['layer'] === 'world' || c.userData['layer'] === 'reference'));
      const hit = this.rayFrom(rect.left + rect.width / 2, rect.top + rect.height / 2).intersectObjects(targets, false)[0];
      if (hit) { at.set(hit.point.x, hit.point.y, hit.point.z); ground = hit.point.y; }
    }
    ground ??= at.y;
    const forward = at.clone().sub(this.camera.position).setY(0);
    if (forward.lengthSq() < 1e-6) { forward.set(0, 0, -1); }
    forward.normalize();
    this.walking = { eye, flySpeed: this.flySpeed };
    this.flySpeed = 45;
    this.camera.position.set(at.x, ground + eye, at.z);
    this.controls.target.copy(this.camera.position).addScaledVector(forward, 2);
    this.controls.update();
    this.canvas.focus();
  }

  // The eye at walking height over the ground under it (stepping up to half a body: kerbs, stairs).
  private keepEyeHeight(): void {
    const w = this.walking!;
    const p = this.camera.position;
    const top = this.worldBox.isEmpty() ? p.y + 1000 : this.worldBox.max.y + 10;
    const ray = new THREE.Raycaster(new THREE.Vector3(p.x, Math.min(top, p.y - w.eye + w.eye * 0.5), p.z), new THREE.Vector3(0, -1, 0));
    const targets = this.content.children.filter(c => c.visible && (c.userData['layer'] === 'world' || c.userData['layer'] === 'reference'));
    const hit = ray.intersectObjects(targets, false)[0];
    if (!hit) { return; }
    const dy = hit.point.y + w.eye - p.y;
    if (Math.abs(dy) < 1e-4) { return; }
    p.y += dy;
    this.controls.target.y += dy;
    this.controls.update();
  }

  // ------------------------------------------------------------------ measure

  clearMeasure(): void {
    this.measurePoints = [];
    this.drawMeasure();
  }

  private drawMeasure(): void {
    this.measureLine.geometry.dispose();
    this.measureLine.geometry = new THREE.BufferGeometry().setFromPoints(this.measurePoints);
    this.measureDots.geometry.dispose();
    this.measureDots.geometry = new THREE.BufferGeometry().setFromPoints(this.measurePoints);
    const [a, b] = this.measurePoints;
    this.callbacks.onMeasure?.(a && b ? a.distanceTo(b) : null, this.measurePoints.length);
  }

  // A locked layer stays visible (and something to drop onto or place on) but can't be picked.
  setLayerLocked(layer: MapLayer, locked: boolean): void {
    if (locked) { this.lockedLayers.add(layer); } else { this.lockedLayers.delete(layer); }
    this.updatePlacementGhost();
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

  set tool(value: MapTool) { this.toolValue = value; this.updateGizmo(); this.updatePlacementGhost(); this.updateBrushRing(); }
  get tool(): MapTool { return this.toolValue; }

  set gizmoLocal(value: boolean) { this.localSpace = value; this.updateGizmo(); }
  get gizmoLocal(): boolean { return this.localSpace; }

  set snapTarget(value: SnapTarget) { this.snapTargetValue = value; }
  get snapTarget(): SnapTarget { return this.snapTargetValue; }

  set pivotMode(value: PivotMode) { this.pivotModeValue = value; this.updateGizmo(); }
  get pivotMode(): PivotMode { return this.pivotModeValue; }

  setBrush(options: BrushOptions, model: string | null): void {
    this.brush = options;
    this.brushModel = model;
    this.updateBrushRing();
  }

  // Off while the page waits on an edit or the session is read-only: no gizmo, no placing.
  set editable(value: boolean) { if (this.editableValue !== value) { this.editableValue = value; this.updateGizmo(); this.updatePlacementGhost(); } }
  get editable(): boolean { return this.editableValue; }

  select(indices: number[], additive = false): void {
    if (!additive) { this.selected.clear(); this.selectedPoint = null; }
    for (const i of indices) { if (this.instances[i] && this.selectable(i)) { this.selected.add(i); } }
    this.selectionChanged();
  }

  selectPoint(name: string | null): void {
    this.selected.clear();
    this.selectedPoint = name && !this.lockedLayers.has('points') && this.points.some(point => point.name === name) ? name : null;
    this.selectionChanged();
  }

  clearSelection(): void { this.selected.clear(); this.selectedPoint = null; this.selectionChanged(); }

  selectAllOfModel(source: string): void {
    this.select(this.instances.map((inst, i) => inst.source === source ? i : -1).filter(i => i >= 0));
  }

  private selectionChanged(): void {
    this.buildMarkers();
    this.refreshOverlay();
    this.updateGizmo();
    this.callbacks.onSelectionChange?.();
  }

  // Selected instances that may move (the world model and sky stay put).
  private movable(): number[] {
    if (this.selected.size > 1 && [...this.selected].some(i => this.instances[i]?.caNode && !this.instances[i]?.multiMove)) { return []; }
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

  // ------------------------------------------------------------------ collision

  /** Draws the map's collision faces (world space); an empty list clears them. */
  setCollision(faces: CollisionFaceWire[]): void {
    disposeGroup(this.collisionGroup);
    this.collisionGroup.add(buildCollisionObject(faces));
  }

  // ------------------------------------------------------------------ paths and regions

  /** Draws a map's paths (lines through their nodes) and regions (wireframe shapes); empty lists clear them. */
  setShapes(paths: MapPathWire[], regions: MapRegionWire[]): void {
    for (const g of [this.pathGroup, this.regionGroup]) {
      g.traverse(o => {
        const m = o as THREE.Mesh;
        m.geometry?.dispose();
        const mat = m.material as THREE.Material | undefined;
        mat?.dispose?.();
      });
      g.clear();
    }
    const dot = Math.max(1.5, this.extent * 0.002);
    for (const p of paths) { if (p.nodes.length) { this.pathGroup.add(buildPathObject(p, dot)); } }
    for (const r of regions) { this.regionGroup.add(buildRegionObject(r)); }
    this.pathGroup.visible = !this.hiddenLayers.has('paths');
    this.regionGroup.visible = !this.hiddenLayers.has('regions');
  }

  /** Frames a path or region by name (shown first when its layer is hidden). */
  frameShape(layer: 'paths' | 'regions', name: string): boolean {
    const group = layer === 'paths' ? this.pathGroup : this.regionGroup;
    const obj = group.children.find(c => c.userData['shape'] === name);
    if (!obj) { return false; }
    obj.updateMatrixWorld(true);
    const box = new THREE.Box3().setFromObject(obj);
    if (box.isEmpty()) { return false; }
    this.frameBox(box.expandByScalar(Math.max(4, this.extent * 0.01)));
    return true;
  }

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
    for (const i of this.stroke?.erase ?? []) {
      const inst = this.instances[i];
      const b = inst.source ? this.localBounds.get(inst.source) : null;
      if (!b || b.isEmpty()) { continue; }
      const cube = new THREE.BoxGeometry(...b.getSize(new THREE.Vector3()).toArray());
      const box = new THREE.LineSegments(new THREE.EdgesGeometry(cube), this.eraseMaterial);
      cube.dispose();
      box.position.copy(b.getCenter(new THREE.Vector3()));
      const holder = new THREE.Group();
      holder.matrixAutoUpdate = false;
      holder.matrix.copy(this.currentMatrix(i));
      holder.add(box);
      holder.renderOrder = 20;
      this.overlay.add(holder);
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
    const canMove = this.selectedPoint ? t === 'move' && this.points.find(p => p.name === this.selectedPoint)?.canMove !== false : this.movable().length > 0;
    if (this.editableValue && (t === 'move' || t === 'rotate' || t === 'scale') && center && canMove) {
      const ids = this.movable();
      this.pivot.position.copy(this.pivotModeValue === 'active' && ids.length && !this.selectedPoint
        ? new THREE.Vector3().setFromMatrixPosition(this.currentMatrix(ids[0])) : center);
      this.pivot.rotation.set(0, 0, 0);
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
      // Points move on the ground plane; props and clutter turn about any axis.
      this.gizmo.showX = true;
      this.gizmo.showZ = true;
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
      const bottoms = new Map<number, number>();
      for (const i of this.movable()) {
        snapshot.set(i, this.currentMatrix(i).clone());
        // How far each origin sits above its mesh's lowest point, for the surface snap.
        const bottom = this.bottomOf(i);
        if (bottom !== null) { bottoms.set(i, new THREE.Vector3().setFromMatrixPosition(this.currentMatrix(i)).y - bottom); }
      }
      this.gizmoDrag = { start: this.pivot.matrixWorld.clone(), snapshot, point: this.selectionCenter() ?? undefined,
        centre: this.pivot.position.clone(), bottoms };
      return;
    }
    const drag = this.gizmoDrag;
    this.gizmoDrag = null;
    this.endDragCulling();
    this.callbacks.onDragInfo?.(null);
    if (!drag) { return; }
    this.pivot.updateMatrixWorld(true);
    const delta = this.pivot.matrixWorld.clone().multiply(drag.start.clone().invert());
    if (this.selectedPoint && drag.point) {
      if (isIdentity(delta)) { return; }
      const p = drag.point.clone().applyMatrix4(delta);
      this.callbacks.onPointMove?.(this.selectedPoint, [p.x, p.y, p.z]);
      return;
    }
    const kind = this.toolValue === 'rotate' ? 'rotate' : this.toolValue === 'scale' ? 'scale' : 'move';
    const ids = [...drag.snapshot.keys()];
    const next = this.dragResult(drag, delta);
    if (ids.every((i, k) => next[k].equals(drag.snapshot.get(i)!))) { return; }
    this.callbacks.onTransformCommit?.(ids.map((index, k) => ({ index, matrix: toModelArray(next[k]) })), kind);
  }

  // Each dragged item's placement for this pivot change: the pivot mode, then the surface or vertex snap of a move.
  private dragResult(drag: NonNullable<MapViewport['gizmoDrag']>, delta: THREE.Matrix4): THREE.Matrix4[] {
    const ids = [...drag.snapshot.keys()];
    if (this.toolValue === 'move' && this.snapTargetValue === 'vertex' && this.lastPointer) {
      const v = this.vertexUnder(this.lastPointer.x, this.lastPointer.y, new Set(ids));
      if (v) { delta = new THREE.Matrix4().makeTranslation(v.x - drag.centre.x, v.y - drag.centre.y, v.z - drag.centre.z); }
    }
    const next = applyDrag(delta, ids.map(i => drag.snapshot.get(i)!), this.pivotModeValue, drag.centre);
    if (this.toolValue === 'move' && this.snapTargetValue === 'surface') {
      const skip = new Set(ids);
      next.forEach((m, k) => {
        const off = drag.bottoms.get(ids[k]);
        const p = new THREE.Vector3().setFromMatrixPosition(m);
        const ground = off === undefined ? null : this.groundBelow(p.x, p.z, skip);
        if (ground !== null) { m.setPosition(p.x, ground + off!, p.z); }
      });
    }
    return next;
  }

  // The lowest point of instance i's mesh as placed now (its box's corners), or null without a mesh.
  private bottomOf(i: number): number | null {
    const inst = this.instances[i];
    const b = inst.source ? this.localBounds.get(inst.source) : null;
    if (!b || b.isEmpty()) { return null; }
    let low = Infinity;
    for (let k = 0; k < 8; k++) {
      low = Math.min(low, new THREE.Vector3(k & 1 ? b.max.x : b.min.x, k & 2 ? b.max.y : b.min.y, k & 4 ? b.max.z : b.min.z).applyMatrix4(this.currentMatrix(i)).y);
    }
    return low;
  }

  // The highest world or prop surface under (x, z), leaving out `skip`'s instances; null if there is none.
  private groundBelow(x: number, z: number, skip: Set<number> = new Set(), layers: MapLayer[] = ['world', 'reference']): number | null {
    const top = this.worldBox.isEmpty() ? 10000 : this.worldBox.max.y + 10;
    const ray = new THREE.Raycaster(new THREE.Vector3(x, top, z), new THREE.Vector3(0, -1, 0));
    const targets = this.content.children.filter(c => c.visible && layers.includes(c.userData['layer']));
    for (const hit of ray.intersectObjects(targets, false)) {
      const i = this.instanceOf(hit);
      if (i === null || !skip.has(i)) { return hit.point.y; }
    }
    return null;
  }

  // The corner of the face under the cursor nearest the hit, in world space (vertex snap), leaving out `skip`.
  private vertexUnder(clientX: number, clientY: number, skip: Set<number>): THREE.Vector3 | null {
    const targets = this.content.children.filter(c => c.visible && c.userData['layer'] !== 'sky');
    for (const hit of this.rayFrom(clientX, clientY).intersectObjects(targets, false)) {
      const i = this.instanceOf(hit);
      if ((i !== null && skip.has(i)) || !hit.face) { continue; }
      const mesh = hit.object as THREE.Mesh;
      const world = mesh.matrixWorld.clone();
      if (mesh instanceof THREE.InstancedMesh && hit.instanceId !== undefined) {
        const m = new THREE.Matrix4();
        mesh.getMatrixAt(hit.instanceId, m);
        world.multiply(m);
      }
      const pos = mesh.geometry.getAttribute('position');
      let best: THREE.Vector3 | null = null;
      for (const v of [hit.face.a, hit.face.b, hit.face.c]) {
        const p = new THREE.Vector3().fromBufferAttribute(pos, v).applyMatrix4(world);
        if (!best || p.distanceToSquared(hit.point) < best.distanceToSquared(hit.point)) { best = p; }
      }
      return best;
    }
    return null;
  }

  protected override onGizmoChange(): void {
    const drag = this.gizmoDrag;
    if (!drag) { return; }
    if (this.toolValue === 'scale' && [...drag.snapshot.keys()].some(i => this.instances[i].clutterIndex !== null)) {
      // Clutter carries one scale (its quaternion layout and scale_inverse): an axis handle scales all three. GEO props
      // are full matrices and stretch along one axis.
      const s = this.pivot.scale;
      const u = [s.x, s.y, s.z].reduce((a, b) => Math.abs(b - 1) > Math.abs(a - 1) ? b : a, 1);
      s.setScalar(u);
    }
    this.pivot.updateMatrixWorld(true);
    const delta = this.pivot.matrixWorld.clone().multiply(drag.start.clone().invert());
    if (this.selectedPoint && drag.point) {
      const p = this.points.find(x => x.name === this.selectedPoint);
      if (p) {
        const at = drag.point.clone().applyMatrix4(delta);
        p.obj.position.set(at.x - p.x, at.y - p.y, at.z - p.z);
        p.figure?.position.copy(p.obj.position);
      }
      return;
    }
    this.callbacks.onDragInfo?.(this.dragText(delta));
    const touched = new Set<THREE.InstancedMesh>();
    const results = this.dragResult(drag, delta);
    let k = 0;
    for (const [i] of drag.snapshot) {
      const next = results[k++];
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

  // The drag so far, as the status bar shows it: the move, the turn or the scale.
  private dragText(delta: THREE.Matrix4): string {
    const p = new THREE.Vector3(), q = new THREE.Quaternion(), sc = new THREE.Vector3();
    delta.decompose(p, q, sc);
    if (this.toolValue === 'rotate') {
      const e = new THREE.Euler().setFromQuaternion(q, 'YXZ');
      return `Turn  X ${THREE.MathUtils.radToDeg(e.x).toFixed(1)}°  Y ${THREE.MathUtils.radToDeg(e.y).toFixed(1)}°  Z ${THREE.MathUtils.radToDeg(e.z).toFixed(1)}°`;
    }
    if (this.toolValue === 'scale') { return `Scale  ${this.pivot.scale.x.toFixed(3)} × ${this.pivot.scale.y.toFixed(3)} × ${this.pivot.scale.z.toFixed(3)}`; }
    return `Move  ΔX ${p.x.toFixed(2)}  ΔY ${p.y.toFixed(2)}  ΔZ ${p.z.toFixed(2)}${this.snapTargetValue !== 'increment' ? ` · snap to ${this.snapTargetValue}` : ''}`;
  }

  private endDragCulling(): void {
    for (const [mesh, culled] of this.dragMeshes) { mesh.computeBoundingSphere(); mesh.frustumCulled = culled; }
    this.dragMeshes.clear();
  }

  // ------------------------------------------------------------------ clutter brush

  private canPaint(erasing = this.brush.erase): boolean {
    return this.toolValue === 'paint' && this.editableValue && !!this.placementOptions && (erasing || !!this.brushModel)
      && !this.lockedLayers.has('clutter') && !this.hiddenLayers.has('clutter');
  }

  // The world or prop surface under the cursor (the brush paints onto the ground, not onto other clutter).
  private groundHit(ray: THREE.Raycaster): THREE.Intersection | null {
    const targets = this.content.children.filter(c => c.visible && (c.userData['layer'] === 'world' || c.userData['layer'] === 'reference'));
    return ray.intersectObjects(targets, false)[0] ?? null;
  }

  private updateBrushRing(): void {
    const p = this.lastPointer;
    const hit = p && this.toolValue === 'paint' ? this.groundHit(this.rayFrom(p.x, p.y)) : null;
    this.brushRing.visible = !!hit;
    if (!hit) { return; }
    this.brushRing.position.copy(hit.point).add(new THREE.Vector3(0, Math.max(0.05, this.extent * 0.0005), 0));
    this.brushRing.scale.setScalar(Math.max(0.1, this.brush.radius));
    (this.brushRing.material as THREE.LineBasicMaterial).color.setHex(this.brush.erase ? 0xff5544 : this.canPaint() ? 0x66ddbb : 0x888888);
  }

  // Clutter placements of the brush's model (all clutter when it has none), as [index, x, z].
  private brushClutter(): Array<[number, number, number]> {
    const out: Array<[number, number, number]> = [];
    this.instances.forEach((inst, i) => {
      if (inst.clutterIndex === null || (this.brushModel && inst.model !== this.brushModel) || !this.instanceVisible(i)) { return; }
      const e = this.display[i].elements;
      out.push([i, e[12], e[14]]);
    });
    return out;
  }

  // One dab of the stroke at the cursor: placements scattered on the ground under the brush, or clutter marked for
  // erasing. Dabs come at most every 40% of the radius of cursor travel.
  private dab(clientX: number, clientY: number): void {
    const stroke = this.stroke;
    const hit = stroke ? this.groundHit(this.rayFrom(clientX, clientY)) : null;
    if (!stroke || !hit) { return; }
    if (stroke.last && stroke.last.distanceTo(hit.point) < this.brush.radius * 0.4) { return; }
    stroke.last = hit.point.clone();
    const { x: cx, z: cz } = hit.point;
    const clutter = this.brushClutter();
    if (stroke.erasing) {
      for (const k of withinRadius(clutter.map(c => [c[1], c[2]]), cx, cz, this.brush.radius)) { stroke.erase.add(clutter[k][0]); }
      this.refreshOverlay();
      return;
    }
    const near = clutter.map(c => [c[1], c[2]] as [number, number])
      .concat(stroke.matrices.map(m => [m.elements[12], m.elements[14]] as [number, number]))
      .filter(([x, z]) => Math.hypot(x - cx, z - cz) <= this.brush.radius + this.brush.spacing);
    const top = this.worldBox.isEmpty() ? hit.point.y + 1000 : this.worldBox.max.y + 10;
    for (const [x, z] of keepApart(dabPoints(cx, cz, this.brush.radius, Math.max(1, Math.round(this.brush.density)), stroke.random), near, this.brush.spacing)) {
      const ground = this.groundHit(new THREE.Raycaster(new THREE.Vector3(x, top, z), new THREE.Vector3(0, -1, 0)));
      if (!ground) { continue; }
      const normal = surfaceNormal(ground);
      if (!slopeOk(normal, this.brush.maxSlope)) { continue; }
      stroke.matrices.push(placementMatrix(ground.point, normal, samplePlacement(this.placementOptions!, stroke.random), this.placementOptions!.alignSurface));
    }
    this.strokeMarks.geometry.dispose();
    this.strokeMarks.geometry = new THREE.BufferGeometry().setFromPoints(stroke.matrices.map(m => new THREE.Vector3().setFromMatrixPosition(m)));
  }

  private endStroke(): void {
    const stroke = this.stroke;
    this.stroke = null;
    this.strokeMarks.geometry.dispose();
    this.strokeMarks.geometry = new THREE.BufferGeometry();
    if (!stroke) { return; }
    if (stroke.erasing) {
      this.refreshOverlay();
      if (stroke.erase.size) { this.callbacks.onErase?.([...stroke.erase]); }
    } else if (stroke.matrices.length) {
      this.callbacks.onScatter?.(stroke.matrices.map(toModelArray));
    }
  }

  // ------------------------------------------------------------------ measuring (the map check)

  /**
   * Every movable placement measured for the map check: the gap between its mesh's bottom and the surface under it
   * (at most `maxGaps` are measured: each is a ray through the scene) and the triangles it draws.
   */
  measureScene(maxGaps = 2000): Measured[] {
    const out: Measured[] = [];
    let rays = 0;
    this.instances.forEach((inst, i) => {
      if (!isMovable(inst)) { return; }
      const e = this.display[i].elements;
      let triangles = 0;
      for (const { mesh } of this.slots[i]) {
        const g = mesh.geometry;
        triangles += (g.index ? g.index.count : g.getAttribute('position').count) / 3;
      }
      let gap: number | null = null;
      const bottom = this.bottomOf(i);
      if (bottom !== null && rays++ < maxGaps) {
        const ground = this.groundBelow(e[12], e[14], new Set([i]));
        if (ground !== null) { gap = bottom - ground; }
      }
      out.push({ model: inst.model, instance: inst.instance, geoIndex: inst.geoIndex, clutterIndex: inst.clutterIndex,
        position: [e[12], e[13], e[14]], gap, triangles: Math.round(triangles) });
    });
    return out;
  }

  /** World boxes of instances (for align and distribute). */
  worldBounds(indices: number[]): Array<{ min: number[]; max: number[] }> {
    return indices.map(i => {
      const inst = this.instances[i];
      const b = inst?.source ? this.localBounds.get(inst.source) : null;
      const box = b && !b.isEmpty() ? b.clone().applyMatrix4(this.currentMatrix(i))
        : new THREE.Box3().setFromPoints([new THREE.Vector3().setFromMatrixPosition(this.currentMatrix(i))]);
      return { min: box.min.toArray(), max: box.max.toArray() };
    });
  }

  // ------------------------------------------------------------------ pointer

  private hit(clientX: number, clientY: number): THREE.Intersection | null {
    const targets: THREE.Object3D[] = this.content.children.filter(c => c.visible);
    if (this.pointGroup.visible) { targets.push(...this.pointGroup.children); }
    // Characters stay pickable (selecting their point) with the Points layer hidden.
    targets.push(...this.figureGroup.children);
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
    if (this.toolValue === 'measure') {
      const hit = this.hit(e.clientX, e.clientY);
      if (!hit) { return; }
      this.measurePoints = this.measurePoints.length >= 2 ? [hit.point.clone()] : [...this.measurePoints, hit.point.clone()];
      this.drawMeasure();
      return;
    }
    if (this.toolValue === 'paint') {
      // Ctrl turns the brush round: paint erases, erase paints.
      const erasing = this.brush.erase !== (e.ctrlKey || e.metaKey);
      if (!this.canPaint(erasing)) { return; }
      this.canvas.setPointerCapture(e.pointerId);
      this.stroke = { erasing, matrices: [], erase: new Set(), last: null, screen: { x: e.clientX, y: e.clientY }, random: seededRandom(Date.now() & 0x7fffffff) };
      this.dab(e.clientX, e.clientY);
      return;
    }
    if (this.toolValue === 'place') {
      this.placementPointer = { x: e.clientX, y: e.clientY };
      this.updatePlacementGhost();
      if (this.placementGhost.visible && this.canPlace()) {
        this.callbacks.onPlace?.({ ...this.placementSample, matrix: this.placementGhost.matrix.toArray() });
        this.placementSample = samplePlacement(this.placementOptions!);
        this.updatePlacementGhost();
      }
      return;
    }
    this.canvas.setPointerCapture(e.pointerId);
    this.dragStart = { x: e.clientX, y: e.clientY };
  };

  private readonly onPointerMove = (e: PointerEvent): void => {
    this.placementPointer = { x: e.clientX, y: e.clientY };
    this.lastPointer = { x: e.clientX, y: e.clientY };
    this.updatePlacementGhost();
    if (this.toolValue === 'paint') {
      this.updateBrushRing();
      if (this.stroke && (e.buttons & 1)) {
        // A fast flick moves far between events: dab along the way so the stroke has no gaps.
        const from = this.stroke.screen, steps = Math.max(1, Math.ceil(Math.hypot(e.clientX - from.x, e.clientY - from.y) / 10));
        for (let k = 1; k <= steps; k++) { this.dab(from.x + (e.clientX - from.x) * k / steps, from.y + (e.clientY - from.y) * k / steps); }
        this.stroke.screen = { x: e.clientX, y: e.clientY };
      }
      return;
    }
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

  // A click on a marker: an action icon selects its prop, an objective flag tells the page. True when one was hit.
  private markerPick(e: PointerEvent): boolean {
    const sprite = this.markerAt(e.clientX, e.clientY);
    if (!sprite) { return false; }
    const pick = sprite.userData['marker'] as MarkerPick;
    if (pick.kind === 'action') {
      const shown = (sprite.userData['instances'] as number[]).find(i => this.instanceVisible(i) && this.selectable(i)) ?? pick.instance;
      this.selected.clear(); this.selectedPoint = null;
      if (shown >= 0) { this.selected.add(shown); }
      this.selectionChanged();
      this.callbacks.onMarkerPick?.({ ...pick, instance: shown });
    } else {
      this.callbacks.onMarkerPick?.(pick);
    }
    return true;
  }

  // Double-click a prop: open its model in the Model Editor.
  private readonly onDoubleClick = (e: MouseEvent): void => {
    if (this.toolValue === 'place' || this.toolValue === 'paint' || this.toolValue === 'measure') { return; }
    const h = this.hit(e.clientX, e.clientY);
    const i = h && !this.pointOf(h) ? this.instanceOf(h) : null;
    if (i !== null) { this.callbacks.onOpenModel?.(i); }
  };

  private readonly onPointerUp = (e: PointerEvent): void => {
    if (this.stroke && e.button === 0) { this.endStroke(); return; }
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
    } else if (this.markerPick(e)) {
      return;
    } else {
      const h = this.hit(e.clientX, e.clientY);
      const point = h ? this.pointOf(h) : null;
      const i = h && !point ? this.instanceOf(h) : null;
      if (point) { if (!this.lockedLayers.has('points')) { this.selected.clear(); this.selectedPoint = point; } }
      else if (i !== null && (this.instances[i].caNode || layerOf(this.instances[i]) !== 'world') && this.selectable(i) && !this.hiddenInstances.has(i)) {
        if (subtract) { this.selected.delete(i); } else { this.selected.add(i); }
      }
    }
    this.selectionChanged();
  };

}
