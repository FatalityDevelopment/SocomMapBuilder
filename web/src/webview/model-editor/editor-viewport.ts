// The Model Editor's 3D view: orbit / pan / zoom, grid and axes, shading
// modes, selection at object / visual / chunk / vertex / face level (click and
// box), a move / rotate / scale gizmo with live preview, a vertex-colour paint
// brush, hide / show and view presets. It only previews: every committed
// change is handed to the page (onTransformCommit / onPaintCommit), which sends
// it to the bridge as an asset-session edit and reloads the mesh.

import * as THREE from 'three';
import type { ModelTexturePixels } from '../zipper-workbench/zed-model-renderer';
import { SELECT_COLOR, ViewportCore, chunkGeometry, isIdentity, type MaterialSpec } from '../zipper-core/viewport-core';
import type { EditorChunk, EditorMesh } from './mesh-data';

export type { Shading, ViewPreset } from '../zipper-core/viewport-core';

export type SelectMode = 'object' | 'visual' | 'chunk' | 'vertex' | 'face';
export type Tool = 'select' | 'move' | 'rotate' | 'scale' | 'paint';

// Vertex indices per chunk index.
export type VertexSelection = Map<number, Set<number>>;

export interface HoverInfo {
  chunk: EditorChunk;
  vertex: number | null;
  triangle: number | null;
  point: [number, number, number];
}

export interface ViewportCallbacks {
  onSelectionChange?: () => void;
  onHover?: (info: HoverInfo | null) => void;
  // Display-space delta matrix applied to `vertices`.
  onTransformCommit?: (delta: number[], vertices: VertexSelection, kind: 'move' | 'rotate' | 'scale') => void;
  // New RGBA colours per chunk.
  onPaintCommit?: (colors: Map<number, Map<number, [number, number, number, number]>>) => void;
  onCameraChange?: () => void;
}

const VERTEX_COLOR = 0x101010;

interface ChunkView {
  chunk: EditorChunk;
  mesh: THREE.Mesh;
  geometry: THREE.BufferGeometry;
}

export class EditorViewport extends ViewportCore {
  private readonly modelGroup = new THREE.Group();
  private readonly overlayGroup = new THREE.Group();
  private readonly brush: THREE.Mesh;
  private views: ChunkView[] = [];

  // Settings the page drives.
  selectMode: SelectMode = 'chunk';
  private toolValue: Tool = 'select';
  showNormals = false;
  brushRadius = 10;
  brushColor: [number, number, number] = [255, 255, 255];
  brushIntensity = 1;
  brushStrength = 0.5;

  // State.
  private readonly hidden = new Set<number>();
  private visualFilter: string | null = null;
  readonly selectedChunks = new Set<number>();
  readonly selectedVertices: VertexSelection = new Map();
  readonly selectedFaces: Map<number, Set<number>> = new Map();
  private dragStart: { x: number; y: number } | null = null;
  private boxEl: HTMLDivElement;
  private gizmoDrag: { start: THREE.Matrix4; snapshot: Map<number, Float32Array> } | null = null;
  private painting: Map<number, Map<number, [number, number, number, number]>> | null = null;

  constructor(canvas: HTMLCanvasElement, private readonly callbacks: ViewportCallbacks = {}) {
    super(canvas, () => callbacks.onCameraChange?.());
    this.scene.add(this.modelGroup, this.overlayGroup);

    this.brush = new THREE.Mesh(
      new THREE.RingGeometry(0.92, 1, 48),
      new THREE.MeshBasicMaterial({ color: SELECT_COLOR, side: THREE.DoubleSide, depthTest: false, transparent: true, opacity: 0.9 }),
    );
    this.brush.visible = false;
    this.brush.renderOrder = 999;
    this.scene.add(this.brush);

    this.boxEl = document.createElement('div');
    this.boxEl.className = 'me-boxselect';
    canvas.parentElement?.appendChild(this.boxEl);

    canvas.addEventListener('pointerdown', this.onPointerDown);
    canvas.addEventListener('pointermove', this.onPointerMove);
    window.addEventListener('pointerup', this.onPointerUp);
    canvas.addEventListener('pointerleave', () => { this.brush.visible = false; this.callbacks.onHover?.(null); });
  }

  dispose(): void {
    window.removeEventListener('pointerup', this.onPointerUp);
    this.clearModel();
    this.disposeCore();
  }

  // ------------------------------------------------------------------ model

  setMesh(mesh: EditorMesh | null, textures: Record<string, ModelTexturePixels>, keepCamera = false): void {
    this.clearModel();
    this.setTexturePixels(textures);
    if (!mesh) { this.gizmo.detach(); return; }
    for (const chunk of mesh.chunks) {
      const geometry = chunkGeometry(chunk);
      const meshObj = new THREE.Mesh(geometry, this.materialFor(chunk));
      meshObj.userData['chunk'] = chunk.index;
      meshObj.visible = this.isVisible(chunk);
      this.modelGroup.add(meshObj);
      this.views.push({ chunk, mesh: meshObj, geometry });
    }
    // Drop selection that no longer exists.
    for (const i of [...this.selectedChunks]) { if (!this.views[i]) { this.selectedChunks.delete(i); } }
    for (const map of [this.selectedVertices, this.selectedFaces]) {
      for (const i of [...map.keys()]) { if (!this.views[i]) { map.delete(i); } }
    }
    const box = this.boundsOf(this.views.filter(v => v.mesh.visible));
    if (!box.isEmpty()) {
      this.extent = Math.max(1, box.getSize(new THREE.Vector3()).length());
      box.getCenter(this.center);
    }
    this.rebuildHelpers();
    if (!keepCamera) {
      // Characters face -Z: start from their front.
      this.facesNegZ = !!mesh.skeleton;
      this.setView('front');
      this.frameAll();
    }
    this.refreshOverlays();
  }

  // Turns the view presets around (Front looks at the other side) and re-frames from the new front.
  toggleFacing(): void {
    this.facesNegZ = !this.facesNegZ;
    this.setView('front');
    this.frameAll();
  }

  private clearModel(): void {
    for (const v of this.views) { v.geometry.dispose(); }
    this.modelGroup.clear();
    this.views = [];
    this.clearMaterials();
    this.clearOverlays();
  }

  get chunkViews(): readonly EditorChunk[] { return this.views.map(v => v.chunk); }

  // ------------------------------------------------------------------ materials

  protected materialTargets(): Array<{ mesh: THREE.Mesh; spec: MaterialSpec }> {
    return this.views.map(v => ({ mesh: v.mesh, spec: v.chunk }));
  }

  protected override onShadingChanged(): void { this.refreshOverlays(); }

  // ------------------------------------------------------------------ visibility

  private isVisible(chunk: EditorChunk): boolean {
    return !this.hidden.has(chunk.index) && (!this.visualFilter || chunk.visual === this.visualFilter);
  }

  setVisualFilter(visual: string | null): void {
    this.visualFilter = visual || null;
    this.applyVisibility();
  }

  setChunkHidden(index: number, hidden: boolean): void {
    if (hidden) { this.hidden.add(index); } else { this.hidden.delete(index); }
    this.applyVisibility();
  }

  isChunkHidden(index: number): boolean { return this.hidden.has(index); }

  hideSelected(): void {
    for (const i of this.selectionChunkIndices()) { this.hidden.add(i); }
    this.clearSelection();
    this.applyVisibility();
  }

  unhideAll(): void {
    this.hidden.clear();
    this.applyVisibility();
  }

  isolateSelected(): void {
    const keep = new Set(this.selectionChunkIndices());
    if (keep.size === 0) { return; }
    for (const v of this.views) { if (!keep.has(v.chunk.index)) { this.hidden.add(v.chunk.index); } }
    this.applyVisibility();
  }

  private applyVisibility(): void {
    for (const v of this.views) { v.mesh.visible = this.isVisible(v.chunk); }
    this.refreshOverlays();
    this.callbacks.onSelectionChange?.();
  }

  // ------------------------------------------------------------------ tools / view

  set tool(value: Tool) {
    this.toolValue = value;
    this.brush.visible = false;
    this.updateGizmo();
  }
  get tool(): Tool { return this.toolValue; }

  setHelpers(grid: boolean, axes: boolean, normals: boolean): void {
    this.showGrid = grid;
    this.showAxes = axes;
    this.showNormals = normals;
    this.rebuildHelpers();
  }

  private rebuildHelpers(): void {
    this.clearHelpers();
    this.buildGridAxes(this.boundsOf(this.views));
    if (this.showNormals) {
      const pts: number[] = [];
      const len = this.extent * 0.02;
      for (const v of this.views) {
        if (!v.mesh.visible) { continue; }
        const p = v.geometry.getAttribute('position').array as Float32Array;
        const n = v.chunk.normals;
        for (let i = 0; i < v.chunk.vertexCount; i++) {
          pts.push(p[i * 3], p[i * 3 + 1], p[i * 3 + 2], p[i * 3] + n[i * 3] * len, p[i * 3 + 1] + n[i * 3 + 1] * len, p[i * 3 + 2] + n[i * 3 + 2] * len);
        }
      }
      const g = new THREE.BufferGeometry();
      g.setAttribute('position', new THREE.Float32BufferAttribute(pts, 3));
      this.helperGroup.add(new THREE.LineSegments(g, new THREE.LineBasicMaterial({ color: 0x4fc1ff })));
    }
  }

  frameAll(): void {
    this.frameBox(this.boundsOf(this.views.filter(v => v.mesh.visible)));
  }

  frameSelected(): void {
    const box = new THREE.Box3();
    const p = new THREE.Vector3();
    for (const [ci, verts] of this.selectionVertexMap()) {
      const pos = this.views[ci]?.geometry.getAttribute('position');
      if (!pos) { continue; }
      for (const i of verts) { box.expandByPoint(p.fromBufferAttribute(pos, i)); }
    }
    if (box.isEmpty()) { this.frameAll(); } else { this.frameBox(box); }
  }

  // ------------------------------------------------------------------ selection

  clearSelection(): void {
    this.selectedChunks.clear();
    this.selectedVertices.clear();
    this.selectedFaces.clear();
    this.selectionChanged();
  }

  selectAll(): void {
    this.clearSelectionSilently();
    for (const v of this.views) {
      if (!v.mesh.visible) { continue; }
      const c = v.chunk;
      if (this.selectMode === 'vertex') { this.selectedVertices.set(c.index, new Set(range(c.vertexCount))); }
      else if (this.selectMode === 'face') { this.selectedFaces.set(c.index, new Set(range(c.triangles.length / 3))); }
      else { this.selectedChunks.add(c.index); }
    }
    this.selectionChanged();
  }

  invertSelection(): void {
    for (const v of this.views) {
      if (!v.mesh.visible) { continue; }
      const c = v.chunk;
      if (this.selectMode === 'vertex' || this.selectMode === 'face') {
        const map = this.selectMode === 'vertex' ? this.selectedVertices : this.selectedFaces;
        const count = this.selectMode === 'vertex' ? c.vertexCount : c.triangles.length / 3;
        const cur = map.get(c.index) ?? new Set<number>();
        const next = new Set(range(count).filter(i => !cur.has(i)));
        if (next.size) { map.set(c.index, next); } else { map.delete(c.index); }
      } else if (this.selectedChunks.has(c.index)) {
        this.selectedChunks.delete(c.index);
      } else {
        this.selectedChunks.add(c.index);
      }
    }
    this.selectionChanged();
  }

  selectChunks(indices: number[], additive = false): void {
    if (!additive) { this.clearSelectionSilently(); }
    for (const i of indices) { this.selectedChunks.add(i); }
    this.selectionChanged();
  }

  private clearSelectionSilently(): void {
    this.selectedChunks.clear();
    this.selectedVertices.clear();
    this.selectedFaces.clear();
  }

  setSelectMode(mode: SelectMode): void {
    // Carry the selection over where it makes sense (chunks -> their vertices / faces).
    const chunks = this.selectionChunkIndices();
    this.selectMode = mode;
    this.clearSelectionSilently();
    if (mode === 'vertex') { for (const i of chunks) { this.selectedVertices.set(i, new Set(range(this.views[i].chunk.vertexCount))); } }
    else if (mode === 'face') { for (const i of chunks) { this.selectedFaces.set(i, new Set(range(this.views[i].chunk.triangles.length / 3))); } }
    else if (mode === 'object') { for (const v of this.views) { if (v.mesh.visible) { this.selectedChunks.add(v.chunk.index); } } }
    else { for (const i of chunks) { this.selectedChunks.add(i); } }
    this.selectionChanged();
  }

  // Chunks touched by the selection.
  selectionChunkIndices(): number[] {
    const set = new Set<number>(this.selectedChunks);
    for (const k of this.selectedVertices.keys()) { set.add(k); }
    for (const k of this.selectedFaces.keys()) { set.add(k); }
    return [...set].sort((a, b) => a - b);
  }

  // The selection as vertices per chunk (what transforms and paint act on).
  selectionVertexMap(): VertexSelection {
    const out: VertexSelection = new Map();
    for (const i of this.selectedChunks) { const c = this.views[i]?.chunk; if (c) { out.set(i, new Set(range(c.vertexCount))); } }
    for (const [i, verts] of this.selectedVertices) { out.set(i, new Set([...(out.get(i) ?? []), ...verts])); }
    for (const [i, faces] of this.selectedFaces) {
      const tris = this.views[i]?.chunk.triangles;
      if (!tris) { continue; }
      const set = out.get(i) ?? new Set<number>();
      for (const f of faces) { set.add(tris[f * 3]); set.add(tris[f * 3 + 1]); set.add(tris[f * 3 + 2]); }
      out.set(i, set);
    }
    return out;
  }

  selectionCounts(): { chunks: number; vertices: number; faces: number } {
    let vertices = 0;
    for (const s of this.selectionVertexMap().values()) { vertices += s.size; }
    let faces = 0;
    for (const s of this.selectedFaces.values()) { faces += s.size; }
    return { chunks: this.selectionChunkIndices().length, vertices, faces };
  }

  selectionCenter(): THREE.Vector3 | null {
    const box = new THREE.Box3();
    const p = new THREE.Vector3();
    for (const [ci, verts] of this.selectionVertexMap()) {
      const pos = this.views[ci]?.geometry.getAttribute('position');
      if (!pos) { continue; }
      for (const i of verts) { box.expandByPoint(p.fromBufferAttribute(pos, i)); }
    }
    return box.isEmpty() ? null : box.getCenter(new THREE.Vector3());
  }

  private selectionChanged(): void {
    this.refreshOverlays();
    this.updateGizmo();
    this.callbacks.onSelectionChange?.();
  }

  private pick(clientX: number, clientY: number): THREE.Intersection | null {
    const hits = this.rayFrom(clientX, clientY).intersectObjects(this.views.filter(v => v.mesh.visible).map(v => v.mesh), false);
    return hits[0] ?? null;
  }

  private nearestVertex(clientX: number, clientY: number, maxPx = 14): { chunk: number; vertex: number } | null {
    const rect = this.canvas.getBoundingClientRect();
    const x = clientX - rect.left;
    const y = clientY - rect.top;
    let best: { chunk: number; vertex: number; d: number; z: number } | null = null;
    const p = new THREE.Vector3();
    for (const v of this.views) {
      if (!v.mesh.visible) { continue; }
      const pos = v.geometry.getAttribute('position');
      for (let i = 0; i < pos.count; i++) {
        p.fromBufferAttribute(pos, i).project(this.camera);
        if (p.z < -1 || p.z > 1) { continue; }
        const sx = (p.x + 1) / 2 * rect.width;
        const sy = (1 - p.y) / 2 * rect.height;
        const d = Math.hypot(sx - x, sy - y);
        if (d > maxPx) { continue; }
        if (!best || d < best.d - 2 || (Math.abs(d - best.d) <= 2 && p.z < best.z)) { best = { chunk: v.chunk.index, vertex: i, d, z: p.z }; }
      }
    }
    return best ? { chunk: best.chunk, vertex: best.vertex } : null;
  }

  private clickSelect(e: PointerEvent): void {
    const additive = e.shiftKey;
    const subtract = e.ctrlKey || e.metaKey;
    if (!additive && !subtract) { this.clearSelectionSilently(); }
    const toggle = <T>(set: Set<T>, value: T) => { if (subtract) { set.delete(value); } else { set.add(value); } };
    if (this.selectMode === 'vertex') {
      const hit = this.nearestVertex(e.clientX, e.clientY);
      if (hit) {
        const set = this.selectedVertices.get(hit.chunk) ?? new Set<number>();
        toggle(set, hit.vertex);
        if (set.size) { this.selectedVertices.set(hit.chunk, set); } else { this.selectedVertices.delete(hit.chunk); }
      }
    } else {
      const hit = this.pick(e.clientX, e.clientY);
      const ci = hit ? hit.object.userData['chunk'] as number : null;
      if (ci !== null && ci !== undefined) {
        if (this.selectMode === 'face' && hit!.faceIndex !== undefined && hit!.faceIndex !== null) {
          const set = this.selectedFaces.get(ci) ?? new Set<number>();
          toggle(set, hit!.faceIndex);
          if (set.size) { this.selectedFaces.set(ci, set); } else { this.selectedFaces.delete(ci); }
        } else if (this.selectMode === 'visual') {
          const visual = this.views[ci].chunk.visual;
          for (const v of this.views) { if (v.chunk.visual === visual && v.mesh.visible) { toggle(this.selectedChunks, v.chunk.index); } }
        } else if (this.selectMode === 'object') {
          for (const v of this.views) { if (v.mesh.visible) { toggle(this.selectedChunks, v.chunk.index); } }
        } else {
          toggle(this.selectedChunks, ci);
        }
      }
    }
    this.selectionChanged();
  }

  private boxSelect(x0: number, y0: number, x1: number, y1: number, e: PointerEvent): void {
    const rect = this.canvas.getBoundingClientRect();
    const minX = Math.min(x0, x1) - rect.left, maxX = Math.max(x0, x1) - rect.left;
    const minY = Math.min(y0, y1) - rect.top, maxY = Math.max(y0, y1) - rect.top;
    const subtract = e.ctrlKey || e.metaKey;
    if (!e.shiftKey && !subtract) { this.clearSelectionSilently(); }
    const p = new THREE.Vector3();
    const inside = (pos: THREE.BufferAttribute | THREE.InterleavedBufferAttribute, i: number) => {
      p.fromBufferAttribute(pos, i).project(this.camera);
      if (p.z < -1 || p.z > 1) { return false; }
      const sx = (p.x + 1) / 2 * rect.width, sy = (1 - p.y) / 2 * rect.height;
      return sx >= minX && sx <= maxX && sy >= minY && sy <= maxY;
    };
    for (const v of this.views) {
      if (!v.mesh.visible) { continue; }
      const pos = v.geometry.getAttribute('position');
      const ci = v.chunk.index;
      if (this.selectMode === 'vertex') {
        const set = this.selectedVertices.get(ci) ?? new Set<number>();
        for (let i = 0; i < pos.count; i++) { if (inside(pos, i)) { if (subtract) { set.delete(i); } else { set.add(i); } } }
        if (set.size) { this.selectedVertices.set(ci, set); } else { this.selectedVertices.delete(ci); }
      } else if (this.selectMode === 'face') {
        const set = this.selectedFaces.get(ci) ?? new Set<number>();
        const t = v.chunk.triangles;
        for (let f = 0; f < t.length / 3; f++) {
          if (inside(pos, t[f * 3]) && inside(pos, t[f * 3 + 1]) && inside(pos, t[f * 3 + 2])) { if (subtract) { set.delete(f); } else { set.add(f); } }
        }
        if (set.size) { this.selectedFaces.set(ci, set); } else { this.selectedFaces.delete(ci); }
      } else {
        let any = false;
        for (let i = 0; i < pos.count && !any; i++) { any = inside(pos, i); }
        if (!any) { continue; }
        const targets = this.selectMode === 'visual'
          ? this.views.filter(w => w.chunk.visual === v.chunk.visual && w.mesh.visible).map(w => w.chunk.index)
          : this.selectMode === 'object' ? this.views.filter(w => w.mesh.visible).map(w => w.chunk.index) : [ci];
        for (const t of targets) { if (subtract) { this.selectedChunks.delete(t); } else { this.selectedChunks.add(t); } }
      }
    }
    this.selectionChanged();
  }

  // ------------------------------------------------------------------ overlays

  private clearOverlays(): void {
    this.overlayGroup.traverse(o => {
      const g = (o as THREE.Mesh).geometry;
      if (g) { g.dispose(); }
    });
    this.overlayGroup.clear();
  }

  refreshOverlays(): void {
    this.clearOverlays();
    const editMode = this.selectMode === 'vertex' || this.selectMode === 'face';
    const lineMat = new THREE.LineBasicMaterial({ color: SELECT_COLOR, depthTest: true, transparent: true, opacity: 0.95 });
    const wireMat = new THREE.LineBasicMaterial({ color: 0x000000, transparent: true, opacity: 0.35 });
    for (const v of this.views) {
      if (!v.mesh.visible) { continue; }
      if (editMode) {
        const wire = new THREE.LineSegments(new THREE.WireframeGeometry(v.geometry), wireMat);
        wire.renderOrder = 2;
        this.overlayGroup.add(wire);
      }
      if (this.selectedChunks.has(v.chunk.index)) {
        const edges = new THREE.LineSegments(new THREE.WireframeGeometry(v.geometry), lineMat);
        edges.renderOrder = 3;
        this.overlayGroup.add(edges);
      }
    }
    if (this.selectMode === 'vertex') {
      const all: number[] = [];
      const sel: number[] = [];
      for (const v of this.views) {
        if (!v.mesh.visible) { continue; }
        const pos = v.geometry.getAttribute('position').array as Float32Array;
        const s = this.selectedVertices.get(v.chunk.index);
        for (let i = 0; i < v.chunk.vertexCount; i++) {
          (s?.has(i) ? sel : all).push(pos[i * 3], pos[i * 3 + 1], pos[i * 3 + 2]);
        }
      }
      this.overlayGroup.add(points(all, VERTEX_COLOR, 4), points(sel, SELECT_COLOR, 7));
    }
    if (this.selectMode === 'face') {
      const tri: number[] = [];
      for (const [ci, faces] of this.selectedFaces) {
        const v = this.views[ci];
        if (!v?.mesh.visible) { continue; }
        const pos = v.geometry.getAttribute('position').array as Float32Array;
        for (const f of faces) {
          for (let k = 0; k < 3; k++) { const i = v.chunk.triangles[f * 3 + k]; tri.push(pos[i * 3], pos[i * 3 + 1], pos[i * 3 + 2]); }
        }
      }
      const g = new THREE.BufferGeometry();
      g.setAttribute('position', new THREE.Float32BufferAttribute(tri, 3));
      const m = new THREE.Mesh(g, new THREE.MeshBasicMaterial({
        color: SELECT_COLOR, transparent: true, opacity: 0.35, side: THREE.DoubleSide, depthWrite: false,
        polygonOffset: true, polygonOffsetFactor: -2, polygonOffsetUnits: -2,
      }));
      m.renderOrder = 4;
      this.overlayGroup.add(m);
    }
    if (this.showNormals) { this.rebuildHelpers(); }
  }

  // ------------------------------------------------------------------ gizmo

  private updateGizmo(): void {
    const t = this.toolValue;
    const center = this.selectionCenter();
    if ((t === 'move' || t === 'rotate' || t === 'scale') && center && !this.gizmoDrag) {
      this.pivot.position.copy(center);
      this.pivot.rotation.set(0, 0, 0);
      this.pivot.scale.set(1, 1, 1);
      this.pivot.updateMatrixWorld(true);
      this.gizmo.setMode(t === 'move' ? 'translate' : t);
      this.gizmo.attach(this.pivot);
    } else if (!this.gizmoDrag) {
      this.gizmo.detach();
    }
  }

  protected override onGizmoDragging(dragging: boolean): void {
    if (dragging) {
      const snapshot = new Map<number, Float32Array>();
      for (const ci of this.selectionVertexMap().keys()) {
        const pos = this.views[ci]?.geometry.getAttribute('position');
        if (pos) { snapshot.set(ci, (pos.array as Float32Array).slice()); }
      }
      this.pivot.updateMatrixWorld(true);
      this.gizmoDrag = { start: this.pivot.matrixWorld.clone(), snapshot };
      return;
    }
    const drag = this.gizmoDrag;
    this.gizmoDrag = null;
    if (!drag) { return; }
    this.pivot.updateMatrixWorld(true);
    const delta = this.pivot.matrixWorld.clone().multiply(drag.start.clone().invert());
    if (!isIdentity(delta)) {
      const kind = this.toolValue === 'rotate' ? 'rotate' : this.toolValue === 'scale' ? 'scale' : 'move';
      this.callbacks.onTransformCommit?.(delta.toArray(), this.selectionVertexMap(), kind);
    }
  }

  protected override onGizmoChange(): void {
    const drag = this.gizmoDrag;
    if (!drag) { return; }
    this.pivot.updateMatrixWorld(true);
    const delta = this.pivot.matrixWorld.clone().multiply(drag.start.clone().invert());
    const p = new THREE.Vector3();
    for (const [ci, verts] of this.selectionVertexMap()) {
      const view = this.views[ci];
      const start = drag.snapshot.get(ci);
      if (!view || !start) { continue; }
      const attr = view.geometry.getAttribute('position') as THREE.BufferAttribute;
      const arr = attr.array as Float32Array;
      for (const i of verts) {
        p.set(start[i * 3], start[i * 3 + 1], start[i * 3 + 2]).applyMatrix4(delta);
        arr[i * 3] = p.x; arr[i * 3 + 1] = p.y; arr[i * 3 + 2] = p.z;
      }
      attr.needsUpdate = true;
      view.geometry.computeBoundingSphere();
    }
    this.refreshOverlays();
  }

  // Preview a numeric transform from the properties panel (same path as the gizmo).
  previewDelta(delta: THREE.Matrix4 | null): void {
    for (const v of this.views) {
      const attr = v.geometry.getAttribute('position') as THREE.BufferAttribute;
      (attr.array as Float32Array).set(v.chunk.positions);
      attr.needsUpdate = true;
    }
    if (delta) {
      const p = new THREE.Vector3();
      for (const [ci, verts] of this.selectionVertexMap()) {
        const v = this.views[ci];
        const attr = v.geometry.getAttribute('position') as THREE.BufferAttribute;
        const arr = attr.array as Float32Array;
        for (const i of verts) {
          p.set(v.chunk.positions[i * 3], v.chunk.positions[i * 3 + 1], v.chunk.positions[i * 3 + 2]).applyMatrix4(delta);
          arr[i * 3] = p.x; arr[i * 3 + 1] = p.y; arr[i * 3 + 2] = p.z;
        }
      }
    }
    this.refreshOverlays();
  }

  // ------------------------------------------------------------------ pointer

  private readonly onPointerDown = (e: PointerEvent): void => {
    if (e.button !== 0 || this.orbitWithAlt) { return; }
    if (this.gizmo.dragging || (this.gizmo as unknown as { axis: string | null }).axis) { return; }
    this.canvas.setPointerCapture(e.pointerId);
    if (this.toolValue === 'paint') {
      this.painting = new Map();
      this.controls.enabled = false;
      this.paintAt(e);
      return;
    }
    this.dragStart = { x: e.clientX, y: e.clientY };
  };

  private readonly onPointerMove = (e: PointerEvent): void => {
    if (this.painting) { this.paintAt(e); return; }
    if (this.dragStart && (e.buttons & 1)) {
      const dx = e.clientX - this.dragStart.x, dy = e.clientY - this.dragStart.y;
      if (Math.hypot(dx, dy) > 4) {
        const rect = this.canvas.getBoundingClientRect();
        const s = this.boxEl.style;
        s.display = 'block';
        s.left = `${Math.min(e.clientX, this.dragStart.x) - rect.left}px`;
        s.top = `${Math.min(e.clientY, this.dragStart.y) - rect.top}px`;
        s.width = `${Math.abs(dx)}px`;
        s.height = `${Math.abs(dy)}px`;
      }
      return;
    }
    if (e.buttons !== 0) { return; }
    this.hover(e);
  };

  private readonly onPointerUp = (e: PointerEvent): void => {
    if (this.painting) {
      const painted = this.painting;
      this.painting = null;
      this.controls.enabled = true;
      if (painted.size) { this.callbacks.onPaintCommit?.(painted); }
      return;
    }
    const start = this.dragStart;
    this.dragStart = null;
    if (!start || e.button !== 0) { return; }
    const boxed = this.boxEl.style.display === 'block';
    this.boxEl.style.display = 'none';
    if (boxed) { this.boxSelect(start.x, start.y, e.clientX, e.clientY, e); }
    else { this.clickSelect(e); }
  };

  private hover(e: PointerEvent): void {
    const hit = this.pick(e.clientX, e.clientY);
    if (this.toolValue === 'paint' && hit) {
      const n = hit.face?.normal ?? new THREE.Vector3(0, 1, 0);
      this.brush.position.copy(hit.point).addScaledVector(n, this.extent * 0.002);
      this.brush.lookAt(hit.point.clone().add(n));
      this.brush.scale.setScalar(this.brushRadius);
      this.brush.visible = true;
    } else {
      this.brush.visible = false;
    }
    if (!hit) { this.callbacks.onHover?.(null); return; }
    const chunk = this.views[hit.object.userData['chunk'] as number].chunk;
    const near = this.selectMode === 'vertex' ? this.nearestVertex(e.clientX, e.clientY) : null;
    this.callbacks.onHover?.({
      chunk,
      vertex: near && near.chunk === chunk.index ? near.vertex : null,
      triangle: hit.faceIndex ?? null,
      point: [hit.point.x, hit.point.y, hit.point.z],
    });
  }

  private paintAt(e: PointerEvent): void {
    this.hover(e);
    const hit = this.pick(e.clientX, e.clientY);
    if (!hit || !this.painting) { return; }
    const target = this.brushColor.map(c => c / 255 * 128 * this.brushIntensity) as [number, number, number];
    const r2 = this.brushRadius * this.brushRadius;
    const p = new THREE.Vector3();
    const restrict = this.selectionChunkIndices();
    for (const v of this.views) {
      if (!v.mesh.visible || !v.chunk.colors) { continue; }
      if (restrict.length && !restrict.includes(v.chunk.index)) { continue; }
      const pos = v.geometry.getAttribute('position');
      const col = v.geometry.getAttribute('color') as THREE.BufferAttribute;
      const carr = col.array as Float32Array;
      let touched = false;
      for (let i = 0; i < pos.count; i++) {
        const d2 = p.fromBufferAttribute(pos, i).distanceToSquared(hit.point);
        if (d2 > r2) { continue; }
        const falloff = 1 - Math.sqrt(d2) / this.brushRadius;
        const k = this.brushStrength * falloff;
        const map = this.painting.get(v.chunk.index) ?? new Map<number, [number, number, number, number]>();
        const cur = map.get(i) ?? [v.chunk.colors[i * 4], v.chunk.colors[i * 4 + 1], v.chunk.colors[i * 4 + 2], v.chunk.colors[i * 4 + 3]];
        const next: [number, number, number, number] = [
          cur[0] + (target[0] - cur[0]) * k, cur[1] + (target[1] - cur[1]) * k, cur[2] + (target[2] - cur[2]) * k, cur[3],
        ];
        map.set(i, next);
        this.painting.set(v.chunk.index, map);
        carr[i * 4] = next[0] / 128; carr[i * 4 + 1] = next[1] / 128; carr[i * 4 + 2] = next[2] / 128;
        touched = true;
      }
      if (touched) { col.needsUpdate = true; }
    }
  }

  // ------------------------------------------------------------------ frame

  private boundsOf(views: ChunkView[]): THREE.Box3 {
    const box = new THREE.Box3();
    for (const v of views) {
      v.geometry.computeBoundingBox();
      if (v.geometry.boundingBox) { box.union(v.geometry.boundingBox); }
    }
    return box;
  }

}

function points(xyz: number[], color: number, size: number): THREE.Points {
  const g = new THREE.BufferGeometry();
  g.setAttribute('position', new THREE.Float32BufferAttribute(xyz, 3));
  const p = new THREE.Points(g, new THREE.PointsMaterial({ color, size, sizeAttenuation: false, depthTest: true }));
  p.renderOrder = 5;
  return p;
}

function range(n: number): number[] {
  return Array.from({ length: n }, (_, i) => i);
}

