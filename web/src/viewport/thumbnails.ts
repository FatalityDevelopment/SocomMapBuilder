// Thumbnails for the asset browser: a model (or a prefab's models, each at its matrix) drawn textured from a
// three-quarter view into a small offscreen canvas, kept as a data URL. One renderer for all of them, drawn one at a
// time from a queue, so a long list costs one WebGL context and never stalls a frame for long.

import * as THREE from 'three';
import { chunkGeometry } from '../webview/zipper-core/viewport-core';
import type { EditorMesh } from '../webview/model-editor/mesh-data';
import type { ModelTexturePixels } from '../webview/zipper-core/texture-pixels';

export interface ThumbPart { mesh: EditorMesh; textureNamespace: string | null; matrix?: number[] }

const SIZE = 112;

export class ThumbnailRenderer {
  private renderer: THREE.WebGLRenderer | null = null;
  private readonly scene = new THREE.Scene();
  private readonly camera = new THREE.PerspectiveCamera(32, 1, 0.01, 100000);
  private readonly cache = new Map<string, string>();
  private queue: Promise<unknown> = Promise.resolve();

  constructor() {
    this.scene.add(new THREE.AmbientLight(0xffffff, 1.1));
    const sun = new THREE.DirectionalLight(0xffffff, 1.6);
    sun.position.set(0.6, 1, 0.8);
    this.scene.add(sun);
  }

  cached(key: string): string | undefined { return this.cache.get(key); }

  /** The thumbnail for `key`, drawn from `load()`'s parts the first time (null: nothing to draw). */
  get(key: string, load: () => Promise<ThumbPart[] | null>, pixels: Record<string, ModelTexturePixels>): Promise<string | null> {
    const hit = this.cache.get(key);
    if (hit) { return Promise.resolve(hit); }
    const job = this.queue.then(async () => {
      if (this.cache.has(key)) { return this.cache.get(key)!; }
      const parts = await load().catch(() => null);
      if (!parts?.length) { return null; }
      const url = this.draw(parts, pixels);
      if (url) { this.cache.set(key, url); }
      return url;
    });
    this.queue = job.catch(() => null);
    return job;
  }

  clear(): void { this.cache.clear(); }

  dispose(): void {
    this.renderer?.dispose();
    this.renderer?.forceContextLoss();
    this.renderer = null;
  }

  private draw(parts: ThumbPart[], pixels: Record<string, ModelTexturePixels>): string | null {
    if (!this.renderer) {
      try {
        this.renderer = new THREE.WebGLRenderer({ antialias: true, alpha: true, preserveDrawingBuffer: true });
      } catch { return null; }
      this.renderer.setSize(SIZE, SIZE, false);
      this.renderer.setClearColor(0x000000, 0);
    }
    const group = new THREE.Group();
    const owned: Array<THREE.BufferGeometry | THREE.Material | THREE.Texture> = [];
    const textures = new Map<string, THREE.Texture | null>();
    const textureFor = (key: string): THREE.Texture | null => {
      if (textures.has(key)) { return textures.get(key)!; }
      const p = pixels[key];
      const t = p ? new THREE.DataTexture(p.rgba, p.width, p.height, THREE.RGBAFormat) : null;
      if (t) {
        t.wrapS = p!.wrapS === 'clamp' ? THREE.ClampToEdgeWrapping : THREE.RepeatWrapping;
        t.wrapT = p!.wrapT === 'clamp' ? THREE.ClampToEdgeWrapping : THREE.RepeatWrapping;
        t.colorSpace = THREE.SRGBColorSpace;
        t.needsUpdate = true;
        owned.push(t);
      }
      textures.set(key, t);
      return t;
    };
    for (const part of parts) {
      for (const chunk of part.mesh.chunks) {
        const geometry = chunkGeometry(chunk);
        if (!geometry.getAttribute('normal')) { geometry.computeVertexNormals(); }
        const key = chunk.texture ? `${part.textureNamespace ?? ''}:${chunk.texture}`.toLowerCase() : null;
        const map = key ? textureFor(key) : null;
        const material = new THREE.MeshLambertMaterial({ map, color: map ? 0xffffff : 0xb8b8b8, side: THREE.DoubleSide,
          alphaTest: map && pixels[key!]?.hasTransparency ? 0.4 : 0 });
        owned.push(geometry, material);
        const m = new THREE.Mesh(geometry, material);
        if (part.matrix) { m.matrixAutoUpdate = false; m.matrix.fromArray(part.matrix); }
        group.add(m);
      }
    }
    group.updateMatrixWorld(true);
    const box = new THREE.Box3().setFromObject(group);
    if (box.isEmpty()) { owned.forEach(o => o.dispose()); return null; }
    const sphere = box.getBoundingSphere(new THREE.Sphere());
    const r = Math.max(1e-3, sphere.radius);
    const dist = r / Math.sin(THREE.MathUtils.degToRad(this.camera.fov / 2)) * 1.05;
    this.camera.position.copy(sphere.center).add(new THREE.Vector3(0.9, 0.65, 1.1).normalize().multiplyScalar(dist));
    this.camera.near = dist / 100;
    this.camera.far = dist * 4;
    this.camera.lookAt(sphere.center);
    this.camera.updateProjectionMatrix();
    this.scene.add(group);
    this.renderer.render(this.scene, this.camera);
    const url = this.renderer.domElement.toDataURL('image/png');
    this.scene.remove(group);
    owned.forEach(o => o.dispose());
    return url;
  }
}
