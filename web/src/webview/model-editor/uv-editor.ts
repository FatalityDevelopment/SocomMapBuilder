// UV editor: the texture of the selected chunks with their UV triangles drawn
// over it. Click / box selects UV vertices (shift adds, ctrl removes), dragging
// a selected vertex moves the selection, the wheel zooms, middle / right drag
// pans, F frames. A finished drag is handed to the page as UV edits, which go
// to the bridge as a setVertices edit.

import type { ModelTexturePixels } from '../zipper-workbench/zed-model-renderer';
import type { EditorChunk } from './mesh-data';

export type UvEdits = Map<number, Map<number, [number, number]>>;

export interface UvEditorCallbacks {
  onCommit?: (edits: UvEdits) => void;
  onSelectionChange?: (count: number) => void;
}

export class UvEditor {
  private readonly ctx: CanvasRenderingContext2D;
  private image: HTMLCanvasElement | null = null;
  private chunks: EditorChunk[] = [];
  // Working UVs (u, v as stored) per chunk, edited live while dragging.
  private uvs = new Map<number, Float32Array>();
  private readonly selected = new Map<number, Set<number>>();
  private zoom = 1;
  private panX = 0;
  private panY = 0;
  private drag: { mode: 'move' | 'box' | 'pan'; x: number; y: number; moved: boolean; start?: Map<number, Float32Array> } | null = null;
  private readonly resizeObserver: ResizeObserver;
  snapToPixels = false;
  private texSize: [number, number] = [256, 256];

  constructor(private readonly canvas: HTMLCanvasElement, private readonly callbacks: UvEditorCallbacks = {}) {
    this.ctx = canvas.getContext('2d')!;
    canvas.addEventListener('pointerdown', this.onDown);
    canvas.addEventListener('pointermove', this.onMove);
    canvas.addEventListener('pointerup', this.onUp);
    canvas.addEventListener('wheel', this.onWheel, { passive: false });
    canvas.addEventListener('contextmenu', e => e.preventDefault());
    this.resizeObserver = new ResizeObserver(() => this.draw());
    this.resizeObserver.observe(canvas.parentElement ?? canvas);
  }

  dispose(): void { this.resizeObserver.disconnect(); }

  setTexture(pixels: ModelTexturePixels | null): void {
    if (!pixels) { this.image = null; this.draw(); return; }
    const c = document.createElement('canvas');
    c.width = pixels.width;
    c.height = pixels.height;
    const data = new ImageData(new Uint8ClampedArray(pixels.rgba), pixels.width, pixels.height);
    c.getContext('2d')!.putImageData(data, 0, 0);
    this.image = c;
    this.texSize = [pixels.width, pixels.height];
    this.draw();
  }

  // Chunks to show and the vertices to start selected (from the 3D selection).
  setChunks(chunks: EditorChunk[], selected: Map<number, Set<number>>): void {
    this.chunks = chunks;
    this.uvs = new Map(chunks.map(c => [c.index, c.uvs.slice()]));
    this.selected.clear();
    for (const [ci, set] of selected) { if (this.uvs.has(ci)) { this.selected.set(ci, new Set(set)); } }
    this.callbacks.onSelectionChange?.(this.selectedCount());
    this.draw();
  }

  selectedCount(): number {
    let n = 0;
    for (const s of this.selected.values()) { n += s.size; }
    return n;
  }

  frame(): void {
    let minU = Infinity, minV = Infinity, maxU = -Infinity, maxV = -Infinity;
    for (const uv of this.uvs.values()) {
      for (let i = 0; i < uv.length; i += 2) {
        minU = Math.min(minU, uv[i]); maxU = Math.max(maxU, uv[i]);
        minV = Math.min(minV, uv[i + 1]); maxV = Math.max(maxV, uv[i + 1]);
      }
    }
    if (!Number.isFinite(minU)) { minU = 0; maxU = 1; minV = 0; maxV = 1; }
    minU = Math.min(minU, 0); minV = Math.min(minV, 0); maxU = Math.max(maxU, 1); maxV = Math.max(maxV, 1);
    const { w, h } = this.size();
    const span = Math.max(maxU - minU, maxV - minV) * 1.1;
    this.zoom = Math.min(w, h) / span;
    this.panX = w / 2 - ((minU + maxU) / 2) * this.zoom;
    this.panY = h / 2 - (1 - (minV + maxV) / 2) * this.zoom;
    this.draw();
  }

  // Applies a transform to the selected UVs (u, v space) and commits it.
  transformSelected(fn: (u: number, v: number) => [number, number]): void {
    const edits: UvEdits = new Map();
    for (const [ci, set] of this.selected) {
      const uv = this.uvs.get(ci)!;
      const map = new Map<number, [number, number]>();
      for (const i of set) {
        const [u, v] = fn(uv[i * 2], uv[i * 2 + 1]);
        uv[i * 2] = u; uv[i * 2 + 1] = v;
        map.set(i, [u, v]);
      }
      if (map.size) { edits.set(ci, map); }
    }
    this.draw();
    if (edits.size) { this.callbacks.onCommit?.(edits); }
  }

  selectionCenter(): [number, number] | null {
    let su = 0, sv = 0, n = 0;
    for (const [ci, set] of this.selected) {
      const uv = this.uvs.get(ci)!;
      for (const i of set) { su += uv[i * 2]; sv += uv[i * 2 + 1]; n++; }
    }
    return n ? [su / n, sv / n] : null;
  }

  private size(): { w: number; h: number } {
    const host = this.canvas.parentElement ?? this.canvas;
    return { w: Math.max(1, host.clientWidth), h: Math.max(1, host.clientHeight) };
  }

  // u, v (stored) <-> canvas pixels. The texture's first row is v = 1.
  private toScreen(u: number, v: number): [number, number] {
    return [this.panX + u * this.zoom, this.panY + (1 - v) * this.zoom];
  }

  private toUv(x: number, y: number): [number, number] {
    return [(x - this.panX) / this.zoom, 1 - (y - this.panY) / this.zoom];
  }

  draw(): void {
    const { w, h } = this.size();
    const dpr = window.devicePixelRatio || 1;
    if (this.canvas.width !== Math.round(w * dpr) || this.canvas.height !== Math.round(h * dpr)) {
      this.canvas.width = Math.round(w * dpr);
      this.canvas.height = Math.round(h * dpr);
      this.canvas.style.width = `${w}px`;
      this.canvas.style.height = `${h}px`;
      if (this.zoom === 1) { this.frame(); return; }
    }
    const ctx = this.ctx;
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.fillStyle = '#1b1b1b';
    ctx.fillRect(0, 0, w, h);
    // Tiled texture (UVs outside 0..1 repeat), the 0..1 tile brighter.
    if (this.image) {
      ctx.imageSmoothingEnabled = false;
      for (let ty = -1; ty <= 1; ty++) {
        for (let tx = -1; tx <= 1; tx++) {
          ctx.globalAlpha = tx === 0 && ty === 0 ? 1 : 0.25;
          const [x, y] = this.toScreen(tx, 1 - ty);
          ctx.drawImage(this.image, x, y, this.zoom, this.zoom);
        }
      }
      ctx.globalAlpha = 1;
    }
    const [x0, y0] = this.toScreen(0, 1);
    ctx.strokeStyle = 'rgba(255,255,255,0.35)';
    ctx.lineWidth = 1;
    ctx.strokeRect(x0 + 0.5, y0 + 0.5, this.zoom, this.zoom);

    ctx.strokeStyle = 'rgba(120, 200, 255, 0.85)';
    ctx.beginPath();
    for (const c of this.chunks) {
      const uv = this.uvs.get(c.index)!;
      for (let f = 0; f < c.triangles.length; f += 3) {
        const pts = [c.triangles[f], c.triangles[f + 1], c.triangles[f + 2]].map(i => this.toScreen(uv[i * 2], uv[i * 2 + 1]));
        ctx.moveTo(pts[0][0], pts[0][1]);
        ctx.lineTo(pts[1][0], pts[1][1]);
        ctx.lineTo(pts[2][0], pts[2][1]);
        ctx.closePath();
      }
    }
    ctx.stroke();
    for (const c of this.chunks) {
      const uv = this.uvs.get(c.index)!;
      const sel = this.selected.get(c.index);
      for (let i = 0; i < c.vertexCount; i++) {
        const [x, y] = this.toScreen(uv[i * 2], uv[i * 2 + 1]);
        const on = sel?.has(i);
        ctx.fillStyle = on ? '#ff9a1f' : '#0b0b0b';
        ctx.fillRect(x - (on ? 3 : 2), y - (on ? 3 : 2), on ? 6 : 4, on ? 6 : 4);
      }
    }
    if (this.drag?.mode === 'box' && this.drag.moved) {
      const r = this.boxRect!;
      ctx.strokeStyle = '#ff9a1f';
      ctx.setLineDash([4, 3]);
      ctx.strokeRect(r.x, r.y, r.w, r.h);
      ctx.setLineDash([]);
    }
    ctx.fillStyle = 'rgba(255,255,255,0.45)';
    ctx.font = '11px sans-serif';
    ctx.fillText(`${this.texSize[0]}×${this.texSize[1]}  ·  zoom ${(this.zoom / Math.min(w, h) * 100).toFixed(0)}%`, 8, h - 8);
  }

  private boxRect: { x: number; y: number; w: number; h: number } | null = null;

  private local(e: PointerEvent): [number, number] {
    const r = this.canvas.getBoundingClientRect();
    return [e.clientX - r.left, e.clientY - r.top];
  }

  private hitVertex(x: number, y: number): { chunk: number; vertex: number } | null {
    let best: { chunk: number; vertex: number; d: number } | null = null;
    for (const c of this.chunks) {
      const uv = this.uvs.get(c.index)!;
      for (let i = 0; i < c.vertexCount; i++) {
        const [sx, sy] = this.toScreen(uv[i * 2], uv[i * 2 + 1]);
        const d = Math.hypot(sx - x, sy - y);
        if (d <= 7 && (!best || d < best.d)) { best = { chunk: c.index, vertex: i, d }; }
      }
    }
    return best;
  }

  private readonly onDown = (e: PointerEvent): void => {
    const [x, y] = this.local(e);
    this.canvas.setPointerCapture(e.pointerId);
    if (e.button === 1 || e.button === 2) { this.drag = { mode: 'pan', x, y, moved: false }; return; }
    if (e.button !== 0) { return; }
    const hit = this.hitVertex(x, y);
    if (hit && this.selected.get(hit.chunk)?.has(hit.vertex) && !e.shiftKey && !e.ctrlKey) {
      this.drag = { mode: 'move', x, y, moved: false, start: new Map([...this.uvs].map(([k, v]) => [k, v.slice()])) };
      return;
    }
    this.drag = { mode: 'box', x, y, moved: false };
  };

  private readonly onMove = (e: PointerEvent): void => {
    const d = this.drag;
    if (!d) { return; }
    const [x, y] = this.local(e);
    if (Math.hypot(x - d.x, y - d.y) > 3) { d.moved = true; }
    if (d.mode === 'pan') {
      this.panX += x - d.x; this.panY += y - d.y; d.x = x; d.y = y;
    } else if (d.mode === 'move' && d.start) {
      let du = (x - d.x) / this.zoom, dv = -(y - d.y) / this.zoom;
      if (this.snapToPixels) {
        du = Math.round(du * this.texSize[0]) / this.texSize[0];
        dv = Math.round(dv * this.texSize[1]) / this.texSize[1];
      }
      for (const [ci, set] of this.selected) {
        const from = d.start.get(ci)!, uv = this.uvs.get(ci)!;
        for (const i of set) { uv[i * 2] = from[i * 2] + du; uv[i * 2 + 1] = from[i * 2 + 1] + dv; }
      }
    } else if (d.mode === 'box') {
      this.boxRect = { x: Math.min(x, d.x), y: Math.min(y, d.y), w: Math.abs(x - d.x), h: Math.abs(y - d.y) };
    }
    this.draw();
  };

  private readonly onUp = (e: PointerEvent): void => {
    const d = this.drag;
    this.drag = null;
    if (!d) { return; }
    const [x, y] = this.local(e);
    if (d.mode === 'move' && d.moved) {
      const edits: UvEdits = new Map();
      for (const [ci, set] of this.selected) {
        const uv = this.uvs.get(ci)!;
        edits.set(ci, new Map([...set].map(i => [i, [uv[i * 2], uv[i * 2 + 1]] as [number, number]])));
      }
      this.callbacks.onCommit?.(edits);
    } else if (d.mode === 'box') {
      const subtract = e.ctrlKey || e.metaKey;
      if (!e.shiftKey && !subtract) { this.selected.clear(); }
      if (d.moved && this.boxRect) {
        const r = this.boxRect;
        for (const c of this.chunks) {
          const uv = this.uvs.get(c.index)!;
          const set = this.selected.get(c.index) ?? new Set<number>();
          for (let i = 0; i < c.vertexCount; i++) {
            const [sx, sy] = this.toScreen(uv[i * 2], uv[i * 2 + 1]);
            if (sx >= r.x && sx <= r.x + r.w && sy >= r.y && sy <= r.y + r.h) { if (subtract) { set.delete(i); } else { set.add(i); } }
          }
          if (set.size) { this.selected.set(c.index, set); } else { this.selected.delete(c.index); }
        }
      } else {
        const hit = this.hitVertex(x, y);
        if (hit) {
          const set = this.selected.get(hit.chunk) ?? new Set<number>();
          if (subtract) { set.delete(hit.vertex); } else { set.add(hit.vertex); }
          if (set.size) { this.selected.set(hit.chunk, set); } else { this.selected.delete(hit.chunk); }
        }
      }
      this.boxRect = null;
      this.callbacks.onSelectionChange?.(this.selectedCount());
    }
    this.draw();
  };

  private readonly onWheel = (e: WheelEvent): void => {
    e.preventDefault();
    const [x, y] = [e.offsetX, e.offsetY];
    const factor = Math.exp(-e.deltaY * 0.0015);
    this.panX = x - (x - this.panX) * factor;
    this.panY = y - (y - this.panY) * factor;
    this.zoom *= factor;
    this.draw();
  };
}
