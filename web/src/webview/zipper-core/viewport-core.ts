import { DEFAULT_RENDER_SETTINGS, normalizeRenderSettings, renderPixelRatio, type RenderSettings } from '../../viewport/renderSettings';
// Shared 3D editor viewport for the Model and Map editors: renderer, perspective / orthographic cameras, orbit
// controls (middle drag or Alt + left orbits, right drag pans, wheel zooms), WASD fly (with the canvas focused),
// keyboard orbit (arrows held, numpad 4 / 6 / 8 / 2 / 9 steps, numpad + / − zoom, on the page in front),
// a transform gizmo, grid / axes, the corner orientation gizmo, view presets and framing, and the GS material rules
// (texel alpha x vertex alpha, blended when the section material blends and something is translucent).
// Subclasses own the content, selection and tools.

import * as THREE from 'three';
import { OrbitControls } from 'three/examples/jsm/controls/OrbitControls.js';
import { TransformControls } from 'three/examples/jsm/controls/TransformControls.js';
import type { ModelTexturePixels } from './texture-pixels';
import { effect, type EffectRef } from '../../shared/signals';
import { viewBackdrop } from '../../shared/viewSettings';
import { modalOpen } from '../../shared/host';
import { ORBIT_TAP, heldOrbitRate, matchCameraKey, orbitPosition, zoomPosition } from '../../viewport/cameraKeys';

export interface ViewportStats { fps: number; calls: number; triangles: number; geometries: number; textures: number }

export type Shading = 'textured' | 'solid' | 'wireframe' | 'xray';
// baked: the model's vertex colours as the game draws them (chunks without any are lit by the scene lights);
// studio: every chunk lit by the scene lights (ambient + a key light on the camera), vertex colours ignored;
// unlit: textures at full brightness.
export type Lighting = 'baked' | 'studio' | 'unlit';
export type Backdrop = 'dark' | 'blueprint';

// The lighting switch the Map Builder and Model Editor show.
export const LIGHTINGS: Array<{ id: Lighting; label: string; title: string }> = [
  { id: 'baked', label: 'Baked', title: 'Baked vertex lighting, as the game draws it (models without it are scene-lit)' },
  { id: 'studio', label: 'Studio', title: 'Scene lights (ambient + a key light from the camera) on everything; ignores baked colours' },
  { id: 'unlit', label: 'Unlit', title: 'Textures at full brightness' },
];
export type ViewPreset = 'front' | 'back' | 'left' | 'right' | 'top' | 'bottom' | 'iso';

export const SELECT_COLOR = 0xff9a1f;

// What a material depends on (a chunk of the editable mesh).
export interface MaterialSpec {
  texture: string | null;
  materialBlend: boolean | null;
  colors: Uint8Array | null;
  // A see-through map effect drawn after the terrain, e.g. a sheet of fog.
  translucentEffect?: boolean;
  // Its reverse side already has an authored face with its own lighting and UVs.
  frontOnly?: boolean;
  // Dynamically lit in game (characters: no baked colours, the VU lights them from their normals).
  lit?: boolean;
}

// The vertex data a chunk mesh is built from (display space, Y up).
export interface ChunkSource extends MaterialSpec {
  vertexCount: number;
  positions: Float32Array;
  uvs: Float32Array;
  normals: Float32Array;
  triangles: Uint32Array;
}

export abstract class ViewportCore {
  readonly renderer: THREE.WebGLRenderer;
  protected readonly scene = new THREE.Scene();
  protected readonly persp = new THREE.PerspectiveCamera(50, 1, 0.1, 100000);
  protected readonly ortho = new THREE.OrthographicCamera(-1, 1, 1, -1, -100000, 100000);
  protected camera: THREE.PerspectiveCamera | THREE.OrthographicCamera = this.persp;
  protected readonly controls: OrbitControls;
  protected readonly gizmo: TransformControls;
  protected readonly pivot = new THREE.Object3D();
  protected readonly helperGroup = new THREE.Group();
  protected readonly raycaster = new THREE.Raycaster();
  protected extent = 100;
  protected center = new THREE.Vector3();
  protected orbitWithAlt = false;
  private readonly light = new THREE.DirectionalLight(0xffffff, 1.6);
  private frameId = 0;
  private renderSettings: RenderSettings = { ...DEFAULT_RENDER_SETTINGS };
  onStats?: (stats: ViewportStats) => void;
  private statsStarted = performance.now();
  private statsFrames = 0;
  private lastFrame = performance.now();
  private readonly resizeObserver: ResizeObserver;
  private readonly axisScene = new THREE.Scene();
  private readonly axisCamera = new THREE.OrthographicCamera(-1.6, 1.6, 1.6, -1.6, 0.1, 10);
  protected readonly listenerAbort = new AbortController();
  private readonly flyKeys = new Set<string>();
  // Arrow keys held for keyboard orbit ('left', 'right', 'up', 'down'; 'shift' for faster).
  private readonly orbitKeys = new Set<string>();
  // Arrows pressed since the last frame that orbited.
  private readonly orbitTaps = new Set<string>();
  private readonly backdropEffect: EffectRef;

  // Materials and textures.
  private textures = new Map<string, THREE.DataTexture>();
  protected texturePixels: Record<string, ModelTexturePixels> = {};
  private materials = new Map<string, THREE.Material>();
  protected shadingValue: Shading = 'textured';
  lighting: Lighting = 'baked';
  private backdropValue: Backdrop = 'dark';
  private backdropTexture: THREE.CanvasTexture | null = null;
  private readonly darkBackground = new THREE.Color(cssColor('--vscode-editor-background', '#232323')).multiplyScalar(1.15);
  backfaces = true;
  showGrid = true;
  showAxes = true;
  // Units per second for WASD fly; scaled from the content size.
  flySpeed = 100;
  // The content faces -Z (SOCOM characters: toes point -Z), so Front / Back and Left / Right swap.
  facesNegZ = false;

  constructor(protected readonly canvas: HTMLCanvasElement, private readonly onCameraChange?: () => void) {
    this.renderer = new THREE.WebGLRenderer({ canvas, antialias: true });
    this.renderer.setPixelRatio(renderPixelRatio(window.devicePixelRatio || 1, 1));
    this.renderer.info.autoReset = false;
    this.renderer.outputColorSpace = THREE.LinearSRGBColorSpace;
    this.scene.background = this.darkBackground;
    ViewportCore.live.add(this);
    this.scene.add(this.helperGroup, this.pivot);
    this.scene.add(new THREE.HemisphereLight(0xffffff, 0x404040, 1.2));
    this.persp.add(this.light);
    this.light.position.set(0.3, 0.6, 1);
    this.scene.add(this.persp);
    this.ortho.add(this.light.clone());
    this.scene.add(this.ortho);

    // Alt + left drag orbits (laptops without a middle button); listen before OrbitControls does.
    canvas.addEventListener('pointerdown', e => {
      this.orbitWithAlt = e.button === 0 && e.altKey;
      this.controls.mouseButtons.LEFT = (this.orbitWithAlt ? THREE.MOUSE.ROTATE : -1) as THREE.MOUSE;
      canvas.focus();
    }, { capture: true, signal: this.listenerAbort.signal });

    this.controls = new OrbitControls(this.persp, canvas);
    this.controls.mouseButtons = { LEFT: -1 as THREE.MOUSE, MIDDLE: THREE.MOUSE.ROTATE, RIGHT: THREE.MOUSE.PAN };
    this.controls.zoomToCursor = true;
    this.controls.addEventListener('change', () => this.onCameraChange?.());

    this.gizmo = new TransformControls(this.persp, canvas);
    this.gizmo.setSpace('world');
    this.gizmo.size = 0.9;
    this.scene.add(this.gizmo.getHelper());
    this.gizmo.addEventListener('dragging-changed', e => {
      const dragging = !!(e as unknown as { value: boolean }).value;
      this.controls.enabled = !dragging;
      this.onGizmoDragging(dragging);
    });
    this.gizmo.addEventListener('objectChange', () => this.onGizmoChange());

    // WASD / QE fly while the canvas has focus (Shift faster).
    canvas.addEventListener('keydown', e => {
      if (e.ctrlKey || e.metaKey || e.altKey) { return; }
      const k = e.key.toLowerCase();
      // Shift+A / Shift+D are page shortcuts unless already flying (Shift is then "faster").
      const shortcut = e.shiftKey && (k === 'a' || k === 'd') && this.flyKeys.size === 0;
      if (['w', 'a', 's', 'd', 'q', 'e'].includes(k) && !shortcut && e.getModifierState('CapsLock') === false && this.flyEnabled()) {
        this.flyKeys.add(k);
        if (e.shiftKey) { this.flyKeys.add('shift'); }
        e.stopPropagation();
        e.preventDefault();
      }
    }, { signal: this.listenerAbort.signal });
    canvas.addEventListener('keyup', e => { this.flyKeys.delete(e.key.toLowerCase()); if (!e.shiftKey) { this.flyKeys.delete('shift'); } }, { signal: this.listenerAbort.signal });
    canvas.addEventListener('blur', () => this.flyKeys.clear(), { signal: this.listenerAbort.signal });
    canvas.addEventListener('contextmenu', e => e.preventDefault(), { signal: this.listenerAbort.signal });

    // Keyboard orbit / zoom on the window, for the view on screen only (every page stays mounted), and not while
    // typing in a field or with a prompt open.
    window.addEventListener('keydown', e => {
      if (!this.visible || modalOpen() || isTyping(e.target)) { return; }
      const action = matchCameraKey(e);
      if (!action) { return; }
      if (action.kind === 'hold') {
        if (!this.orbitKeys.has(action.key)) { this.orbitTaps.add(action.key); }
        this.orbitKeys.add(action.key);
        if (e.shiftKey) { this.orbitKeys.add('shift'); }
      } else if (action.kind === 'orbit') { this.orbitBy(action.yaw, action.pitch); }
      else { this.zoomBy(action.factor); }
      e.preventDefault();
    }, { signal: this.listenerAbort.signal });
    window.addEventListener('keyup', e => {
      const action = matchCameraKey({ key: e.key, code: e.code, ctrlKey: false, metaKey: false, altKey: false });
      if (action?.kind === 'hold') {
        // A tap released before any frame turned the view still turns it one step.
        if (this.orbitTaps.delete(action.key)) {
          const tap = heldOrbitRate(new Set([action.key]), false);
          this.orbitBy(Math.sign(tap.yaw) * ORBIT_TAP, Math.sign(tap.pitch) * ORBIT_TAP);
        }
        this.orbitKeys.delete(action.key);
      }
      if (e.key === 'Shift') { this.orbitKeys.delete('shift'); }
    }, { signal: this.listenerAbort.signal });
    window.addEventListener('blur', () => { this.orbitKeys.clear(); this.orbitTaps.clear(); }, { signal: this.listenerAbort.signal });

    this.buildAxisGizmo();
    this.resizeObserver = new ResizeObserver(() => this.resize());
    this.resizeObserver.observe(canvas.parentElement ?? canvas);
    this.resize();
    // The backdrop is one app-wide setting (the activity bar toggles it).
    this.backdropEffect = effect(() => this.setBackdrop(viewBackdrop()));
    const loop = () => {
      this.frameId = requestAnimationFrame(loop);
      // Every page stays mounted: draw only the one on screen (a hidden page's canvas has no layout box).
      if (document.hidden || this.canvas.offsetParent === null) { this.lastFrame = performance.now(); this.statsStarted = this.lastFrame; this.statsFrames = 0; return; }
      const now = performance.now();
      const dt = Math.min(0.1, (now - this.lastFrame) / 1000);
      this.stepFly(dt);
      this.stepOrbit(dt);
      this.onFrame(dt);
      this.lastFrame = now;
      this.render();
      if (this.renderSettings.showStats) {
        this.statsFrames++;
        const elapsed = now - this.statsStarted;
        if (elapsed >= 1000) {
          const info = this.renderer.info;
          this.onStats?.({ fps: Math.round(this.statsFrames * 1000 / elapsed), calls: info.render.calls,
            triangles: info.render.triangles, geometries: info.memory.geometries, textures: info.memory.textures });
          this.statsStarted = now;
          this.statsFrames = 0;
        }
      } else { this.statsStarted = now; this.statsFrames = 0; }
    };
    loop();
  }

  // Subclass hooks.
  protected onGizmoDragging(_dragging: boolean): void { /* overridden */ }
  protected onGizmoChange(): void { /* overridden */ }
  protected onShadingChanged(): void { /* overridden */ }
  // Every animation frame before rendering (seconds since the last).
  protected onFrame(_dt: number): void { /* overridden */ }
  // Whether WASD flies the camera (the Model Editor uses those keys for tools, so it opts out).
  protected flyEnabled(): boolean { return false; }
  // Meshes whose material follows the shading / lighting settings.
  protected abstract materialTargets(): Array<{ mesh: THREE.Mesh; spec: MaterialSpec }>;

  disposeCore(): void {
    this.listenerAbort.abort();
    ViewportCore.live.delete(this);
    this.backdropEffect.destroy();
    this.backdropTexture?.dispose();

    cancelAnimationFrame(this.frameId);
    this.clearHelpers();
    disposeObjectResources(this.axisScene);
    this.resizeObserver.disconnect();
    this.gizmo.dispose();
    this.controls.dispose();
    this.clearMaterials();
    this.renderer.dispose();
  }

  // ------------------------------------------------------------------ materials

  protected setTexturePixels(pixels: Record<string, ModelTexturePixels>): void {
    this.texturePixels = pixels;
  }

  protected clearMaterials(): void {
    for (const m of this.materials.values()) { m.dispose(); }
    this.materials.clear();
    for (const t of this.textures.values()) { t.dispose(); }
    this.textures.clear();
  }

  /** A texture's pixels changed in place (texture paint): upload them again. */
  protected markTextureDirty(name: string): void {
    const tex = this.textures.get(name.toLowerCase());
    if (tex) { tex.needsUpdate = true; }
  }

  private textureFor(name: string): { tex: THREE.DataTexture; pixels: ModelTexturePixels } | null {
    const key = name.toLowerCase();
    const pixels = this.texturePixels[key];
    if (!pixels) { return null; }
    let tex = this.textures.get(key);
    if (!tex) {
      tex = new THREE.DataTexture(pixels.rgba, pixels.width, pixels.height, THREE.RGBAFormat);
      tex.flipY = false;
      tex.colorSpace = THREE.LinearSRGBColorSpace;
      tex.magFilter = this.renderSettings.filtering === 'pixel' ? THREE.NearestFilter : THREE.LinearFilter;
      tex.minFilter = this.renderSettings.filtering === 'pixel' ? THREE.NearestFilter : THREE.LinearMipmapLinearFilter;
      tex.generateMipmaps = true;
      tex.anisotropy = this.renderSettings.filtering === 'pixel' ? 1 : this.renderer.capabilities.getMaxAnisotropy();
      tex.wrapS = pixels.wrapS === 'clamp' ? THREE.ClampToEdgeWrapping : THREE.RepeatWrapping;
      tex.wrapT = pixels.wrapT === 'clamp' ? THREE.ClampToEdgeWrapping : THREE.RepeatWrapping;
      tex.needsUpdate = true;
      this.textures.set(key, tex);
    }
    return { tex, pixels };
  }

  protected materialFor(spec: MaterialSpec): THREE.Material {
    const shading = this.shadingValue;
    const t = spec.texture ? this.textureFor(spec.texture) : null;
    const vertexAlpha = !!spec.colors && spec.colors.some((v, i) => i % 4 === 3 && v < 128);
    const blended = (spec.materialBlend ?? t?.pixels.alphaBlend ?? false) && (!!t?.pixels.hasTransparency || vertexAlpha);
    // Scene-lit: characters always (the game lights them), everything in studio, and baked chunks that carry no colours.
    const lit = shading === 'textured' && (!!spec.lit || this.lighting === 'studio' || (this.lighting === 'baked' && !spec.colors));
    const side = this.backfaces && !spec.frontOnly ? THREE.DoubleSide : THREE.FrontSide;
    const key = `${shading}|${this.lighting}|${side}|${spec.texture ?? ''}|${blended}|${lit}|${!!spec.translucentEffect}`;
    const hit = this.materials.get(key);
    if (hit) { return hit; }
    let mat: THREE.Material;
    if (shading === 'solid') {
      mat = new THREE.MeshStandardMaterial({ color: 0xb4b4b4, roughness: 0.9, metalness: 0, flatShading: true, side });
    } else if (shading === 'wireframe') {
      mat = new THREE.MeshBasicMaterial({ color: 0x9aa0a6, wireframe: true, side });
    } else if (lit) {
      // Lit like the game's character shading: texture × (ambient + key light), no baked colours.
      const gsTest = t && t.pixels.alphaTest !== null && t.pixels.alphaTest !== undefined && t.pixels.hasTransparency ? t.pixels.alphaTest : 0;
      mat = new THREE.MeshLambertMaterial({
        map: t?.tex ?? null,
        // Slightly over 1: the camera key light plus hemisphere ambient lands near the texture's own colours.
        color: t ? new THREE.Color(1.25, 1.25, 1.25) : 0x9a9a9a,
        side,
        transparent: blended,
        depthWrite: !spec.translucentEffect,
        alphaTest: blended ? Math.max(t?.pixels.alphaTest ?? 0, 0.02) : gsTest,
      });
    } else {
      const gsTest = t && t.pixels.alphaTest !== null && t.pixels.alphaTest !== undefined && t.pixels.hasTransparency ? t.pixels.alphaTest : 0;
      const alphaTest = blended ? Math.max(t?.pixels.alphaTest ?? 0, 0.02) : gsTest;
      const xray = shading === 'xray';
      mat = new THREE.MeshBasicMaterial({
        map: t?.tex ?? null,
        color: t ? 0xffffff : 0x9a9a9a,
        vertexColors: this.lighting === 'baked',
        side,
        transparent: blended || xray,
        opacity: xray ? 0.45 : 1,
        depthWrite: !xray && !spec.translucentEffect,
        alphaTest,
      });
    }
    this.materials.set(key, mat);
    return mat;
  }

  protected refreshMaterials(): void {
    for (const m of this.materials.values()) { m.dispose(); }
    this.materials.clear();
    for (const { mesh, spec } of this.materialTargets()) { mesh.material = this.materialFor(spec); }
  }

  set shading(value: Shading) { this.shadingValue = value; this.refreshMaterials(); this.onShadingChanged(); }
  get shading(): Shading { return this.shadingValue; }

  setLighting(mode: Lighting): void { this.lighting = mode; this.refreshMaterials(); }

  // The colour behind the content: the editor's dark grey, or a blueprint grid (easier to read dark models against).
  get backdrop(): Backdrop { return this.backdropValue; }
  setBackdrop(mode: Backdrop): void {
    this.backdropValue = mode;
    this.updateBackdrop();
  }

  private updateBackdrop(): void {
    this.backdropTexture?.dispose();
    this.backdropTexture = null;
    if (this.backdropValue === 'dark') { this.scene.background = this.darkBackground; return; }
    // Drawn at the canvas size (redrawn on resize) so the cells stay square.
    const host = this.canvas.parentElement ?? this.canvas;
    this.backdropTexture = blueprintTexture(Math.max(1, host.clientWidth), Math.max(1, host.clientHeight));
    this.scene.background = this.backdropTexture;
  }

  // Every viewport alive, for the AI screenshot request (the one on screen answers).
  static readonly live = new Set<ViewportCore>();

  // The canvas as a PNG data URL: renders now so the drawing buffer still holds the frame when it is read.
  capture(): string | null {
    if (this.canvas.offsetParent === null) { return null; }
    this.render();
    return this.canvas.toDataURL('image/png');
  }
  setBackfaces(on: boolean): void { this.backfaces = on; this.refreshMaterials(); }

  setRenderSettings(settings: RenderSettings): void {
    const previous = this.renderSettings;
    this.renderSettings = normalizeRenderSettings(settings);
    this.statsStarted = performance.now();
    this.statsFrames = 0;
    if (previous.filtering !== this.renderSettings.filtering) {
      for (const texture of this.textures.values()) {
        const pixel = this.renderSettings.filtering === 'pixel';
        texture.magFilter = pixel ? THREE.NearestFilter : THREE.LinearFilter;
        texture.minFilter = pixel ? THREE.NearestFilter : THREE.LinearMipmapLinearFilter;
        texture.anisotropy = pixel ? 1 : this.renderer.capabilities.getMaxAnisotropy();
        texture.needsUpdate = true;
      }
    }
    this.resize();
  }

  setSnap(translate: number | null, rotateDeg: number | null, scale: number | null): void {
    this.gizmo.setTranslationSnap(translate);
    this.gizmo.setRotationSnap(rotateDeg === null ? null : THREE.MathUtils.degToRad(rotateDeg));
    this.gizmo.setScaleSnap(scale);
  }

  // ------------------------------------------------------------------ helpers

  // Grid on the floor of `box`, axes at the origin.
  protected buildGridAxes(box: THREE.Box3): void {
    const size = Math.max(10, niceStep(this.extent * 1.5));
    if (this.showGrid) {
      const grid = new THREE.GridHelper(size, 20, 0x5a5a5a, 0x3a3a3a);
      grid.position.set(this.center.x, box.isEmpty() ? 0 : box.min.y, this.center.z);
      this.helperGroup.add(grid);
    }
    if (this.showAxes) { this.helperGroup.add(new THREE.AxesHelper(size * 0.15)); }
  }

  protected clearHelpers(): void {
    disposeObjectResources(this.helperGroup);
    this.helperGroup.clear();
  }

  // ------------------------------------------------------------------ camera

  get isOrtho(): boolean { return this.camera === this.ortho; }
  // On screen: every page stays mounted, a hidden page's canvas has no layout box.
  get visible(): boolean { return this.canvas.offsetParent !== null; }
  get viewLabel(): string { return this.isOrtho ? 'Orthographic' : 'Perspective'; }

  toggleOrtho(): void {
    const from = this.camera;
    const to = this.isOrtho ? this.persp : this.ortho;
    to.position.copy(from.position);
    to.quaternion.copy(from.quaternion);
    this.camera = to;
    this.controls.object = to;
    this.gizmo.camera = to;
    this.resize();
    this.controls.update();
  }

  setView(preset: ViewPreset, target: THREE.Vector3 = this.center, distance = this.extent * 1.4): void {
    const dirs: Record<ViewPreset, [number, number, number]> = {
      front: [0, 0, 1], back: [0, 0, -1], left: [-1, 0, 0], right: [1, 0, 0], top: [0, 1, 0.0001], bottom: [0, -1, 0.0001], iso: [1, 0.8, 1],
    };
    const dir = new THREE.Vector3(...dirs[preset]).normalize();
    if (this.facesNegZ) { dir.x = -dir.x; dir.z = -dir.z; }
    this.controls.target.copy(target);
    this.camera.position.copy(target).addScaledVector(dir, distance);
    this.camera.lookAt(target);
    this.controls.update();
  }

  protected frameBox(box: THREE.Box3): void {
    if (box.isEmpty()) { return; }
    const center = box.getCenter(new THREE.Vector3());
    const size = Math.max(0.5, box.getSize(new THREE.Vector3()).length());
    const dir = this.camera.position.clone().sub(this.controls.target);
    if (dir.lengthSq() < 1e-6) { dir.set(1, 0.7, 1); }
    dir.normalize();
    this.controls.target.copy(center);
    this.camera.position.copy(center).addScaledVector(dir, size * 1.2);
    this.persp.near = Math.max(0.01, size / 1000);
    this.persp.far = Math.max(1000, this.extent * 20, size * 100);
    this.persp.updateProjectionMatrix();
    this.resize();
    this.controls.update();
  }

  // Ray from a client point into the scene.
  protected rayFrom(clientX: number, clientY: number): THREE.Raycaster {
    const rect = this.canvas.getBoundingClientRect();
    const ndc = new THREE.Vector2(((clientX - rect.left) / rect.width) * 2 - 1, -((clientY - rect.top) / rect.height) * 2 + 1);
    this.camera.updateMatrixWorld();
    this.raycaster.setFromCamera(ndc, this.camera);
    return this.raycaster;
  }

  // Orbits the camera about the orbit target: yaw turns about world Y, pitch tilts (degrees).
  orbitBy(yawDeg: number, pitchDeg: number): void {
    this.camera.position.copy(orbitPosition(this.camera.position, this.controls.target, yawDeg, pitchDeg));
    this.camera.lookAt(this.controls.target);
    this.controls.update();
  }

  // Moves the camera toward (factor < 1) or away from (> 1) the orbit target; the orthographic view follows it.
  zoomBy(factor: number): void {
    this.camera.position.copy(zoomPosition(this.camera.position, this.controls.target, factor, Math.max(0.05, this.extent / 2000)));
    this.controls.update();
  }

  private stepOrbit(dt: number): void {
    if (this.orbitKeys.size === 0 || dt <= 0) { return; }
    const { yaw, pitch } = heldOrbitRate(this.orbitKeys, this.orbitKeys.has('shift'));
    if (yaw !== 0 || pitch !== 0) { this.orbitBy(yaw * dt, pitch * dt); }
    this.orbitTaps.clear();
  }

  private stepFly(dt: number): void {
    if (this.flyKeys.size === 0 || dt <= 0) { return; }
    const speed = this.flySpeed * (this.flyKeys.has('shift') ? 4 : 1) * dt;
    const forward = new THREE.Vector3();
    this.camera.getWorldDirection(forward);
    const right = new THREE.Vector3().crossVectors(forward, this.camera.up).normalize();
    const move = new THREE.Vector3();
    if (this.flyKeys.has('w')) { move.add(forward); }
    if (this.flyKeys.has('s')) { move.sub(forward); }
    if (this.flyKeys.has('d')) { move.add(right); }
    if (this.flyKeys.has('a')) { move.sub(right); }
    if (this.flyKeys.has('e')) { move.y += 1; }
    if (this.flyKeys.has('q')) { move.y -= 1; }
    if (move.lengthSq() === 0) { return; }
    move.normalize().multiplyScalar(speed);
    this.camera.position.add(move);
    this.controls.target.add(move);
    this.controls.update();
  }

  protected resize(): void {
    const host = this.canvas.parentElement ?? this.canvas;
    const w = Math.max(1, host.clientWidth);
    const h = Math.max(1, host.clientHeight);
    const pixelRatio = renderPixelRatio(window.devicePixelRatio || 1, this.renderSettings.resolution);
    if (this.renderer.getPixelRatio() !== pixelRatio) { this.renderer.setPixelRatio(pixelRatio); }
    this.renderer.setSize(w, h, false);
    this.persp.aspect = w / h;
    this.persp.updateProjectionMatrix();
    const dist = this.camera.position.distanceTo(this.controls?.target ?? this.center);
    const half = Math.max(0.5, dist * Math.tan(THREE.MathUtils.degToRad(this.persp.fov / 2)));
    this.ortho.left = -half * w / h;
    this.ortho.right = half * w / h;
    this.ortho.top = half;
    this.ortho.bottom = -half;
    this.ortho.updateProjectionMatrix();
    if (this.backdropValue === 'blueprint') {
      const img = this.backdropTexture?.image as HTMLCanvasElement | undefined;
      if (!img || img.width !== w || img.height !== h) { this.updateBackdrop(); }
    }
  }

  private buildAxisGizmo(): void {
    const axis = (dir: THREE.Vector3, color: number, label: string) => {
      const line = new THREE.Line(new THREE.BufferGeometry().setFromPoints([new THREE.Vector3(), dir]), new THREE.LineBasicMaterial({ color }));
      const dot = new THREE.Mesh(new THREE.CircleGeometry(0.22, 24), new THREE.MeshBasicMaterial({ color }));
      dot.position.copy(dir);
      this.axisScene.add(line, dot);
      const sprite = labelSprite(label);
      sprite.position.copy(dir);
      sprite.scale.setScalar(0.38);
      this.axisScene.add(sprite);
    };
    axis(new THREE.Vector3(1, 0, 0), 0xe5484d, 'X');
    axis(new THREE.Vector3(0, 1, 0), 0x46a758, 'Y');
    axis(new THREE.Vector3(0, 0, 1), 0x3e8bff, 'Z');
  }

  private render(): void {
    this.renderer.info.reset();
    if (this.isOrtho) {
      // Keep ortho zoom tied to the orbit distance.
      const host = this.canvas.parentElement ?? this.canvas;
      const w = Math.max(1, host.clientWidth), h = Math.max(1, host.clientHeight);
      const dist = this.camera.position.distanceTo(this.controls.target);
      const half = Math.max(0.05, dist * Math.tan(THREE.MathUtils.degToRad(this.persp.fov / 2)));
      if (Math.abs(this.ortho.top - half) > 1e-4) {
        this.ortho.left = -half * w / h; this.ortho.right = half * w / h; this.ortho.top = half; this.ortho.bottom = -half;
        this.ortho.updateProjectionMatrix();
      }
    }
    const size = this.renderer.getSize(new THREE.Vector2());
    this.renderer.setScissorTest(false);
    this.renderer.setViewport(0, 0, size.x, size.y);
    this.renderer.render(this.scene, this.camera);
    const g = 96;
    this.axisCamera.position.set(0, 0, 4).applyQuaternion(this.camera.quaternion);
    this.axisCamera.quaternion.copy(this.camera.quaternion);
    this.axisScene.traverse(o => { if ((o as THREE.Sprite).isSprite) { o.quaternion.copy(this.camera.quaternion); } });
    this.renderer.autoClear = false;
    this.renderer.clearDepth();
    this.renderer.setScissorTest(true);
    this.renderer.setScissor(size.x - g - 8, size.y - g - 8, g, g);
    this.renderer.setViewport(size.x - g - 8, size.y - g - 8, g, g);
    this.renderer.render(this.axisScene, this.axisCamera);
    this.renderer.setScissorTest(false);
    this.renderer.autoClear = true;
  }
}

// ------------------------------------------------------------------ shared helpers

// A chunk's geometry: positions, UVs (V flipped for the top-down texture upload), colours (0x80 = 1.0, alpha
// clamped to 1), normals and triangles.
export function chunkGeometry(chunk: ChunkSource): THREE.BufferGeometry {
  const geometry = new THREE.BufferGeometry();
  geometry.setAttribute('position', new THREE.BufferAttribute(chunk.positions.slice(), 3));
  const uv = new Float32Array(chunk.uvs.length);
  for (let i = 0; i < chunk.vertexCount; i++) { uv[i * 2] = chunk.uvs[i * 2]; uv[i * 2 + 1] = 1 - chunk.uvs[i * 2 + 1]; }
  geometry.setAttribute('uv', new THREE.BufferAttribute(uv, 2));
  geometry.setAttribute('color', new THREE.BufferAttribute(colorArray(chunk), 4));
  geometry.setAttribute('normal', new THREE.BufferAttribute(chunk.normals.slice(), 3));
  geometry.setIndex(new THREE.BufferAttribute(chunk.triangles, 1));
  // Scene lighting needs normals; chunks stored without them (all zero) get ones from their faces.
  if (!chunk.normals.some(v => v !== 0)) { geometry.computeVertexNormals(); }
  geometry.computeBoundingSphere();
  return geometry;
}

export function colorArray(chunk: { vertexCount: number; colors: Uint8Array | null }): Float32Array {
  const out = new Float32Array(chunk.vertexCount * 4);
  for (let i = 0; i < chunk.vertexCount; i++) {
    if (chunk.colors) {
      out[i * 4] = chunk.colors[i * 4] / 128;
      out[i * 4 + 1] = chunk.colors[i * 4 + 1] / 128;
      out[i * 4 + 2] = chunk.colors[i * 4 + 2] / 128;
      out[i * 4 + 3] = Math.min(1, chunk.colors[i * 4 + 3] / 128);
    } else {
      out[i * 4] = out[i * 4 + 1] = out[i * 4 + 2] = out[i * 4 + 3] = 1;
    }
  }
  return out;
}

export function labelSprite(text: string, color = '#ffffff', px = 40): THREE.Sprite {
  const c = document.createElement('canvas');
  c.width = Math.max(64, text.length * px * 0.62);
  c.height = 64;
  const ctx = c.getContext('2d')!;
  ctx.fillStyle = color;
  ctx.font = `bold ${px}px sans-serif`;
  ctx.textAlign = 'center';
  ctx.textBaseline = 'middle';
  ctx.fillText(text, c.width / 2, 34);
  const tex = new THREE.CanvasTexture(c);
  const sprite = new THREE.Sprite(new THREE.SpriteMaterial({ map: tex, depthTest: false }));
  sprite.userData['aspect'] = c.width / c.height;
  return sprite;
}

// A blueprint sheet: blue paper, a fine grid every 16 px, a heavier line every 5 cells, lighter towards the middle.
export const BLUEPRINT = { paper: '#1f4a7a', edge: '#173a62', minor: 'rgba(190, 220, 255, 0.10)', major: 'rgba(205, 230, 255, 0.24)', cell: 16 };

export function blueprintTexture(w: number, h: number): THREE.CanvasTexture {
  const c = document.createElement('canvas');
  c.width = w;
  c.height = h;
  const ctx = c.getContext('2d')!;
  const glow = ctx.createRadialGradient(w / 2, h / 2, 0, w / 2, h / 2, Math.hypot(w, h) / 2);
  glow.addColorStop(0, BLUEPRINT.paper);
  glow.addColorStop(1, BLUEPRINT.edge);
  ctx.fillStyle = glow;
  ctx.fillRect(0, 0, w, h);
  const cell = BLUEPRINT.cell;
  for (const major of [false, true]) {
    ctx.strokeStyle = major ? BLUEPRINT.major : BLUEPRINT.minor;
    ctx.lineWidth = 1;
    ctx.beginPath();
    const step = major ? cell * 5 : cell;
    for (let x = 0.5; x < w; x += step) { ctx.moveTo(x, 0); ctx.lineTo(x, h); }
    for (let y = 0.5; y < h; y += step) { ctx.moveTo(0, y); ctx.lineTo(w, y); }
    ctx.stroke();
  }
  const tex = new THREE.CanvasTexture(c);
  tex.colorSpace = THREE.LinearSRGBColorSpace;
  return tex;
}

export function niceStep(v: number): number {
  const p = Math.pow(10, Math.floor(Math.log10(v)));
  const m = v / p;
  return (m <= 1 ? 1 : m <= 2 ? 2 : m <= 5 ? 5 : 10) * p;
}

export function isIdentity(m: THREE.Matrix4): boolean {
  const id = new THREE.Matrix4().elements;
  return m.elements.every((v, i) => Math.abs(v - id[i]) < 1e-6);
}

export function cssColor(name: string, fallback: string): string {
  const v = getComputedStyle(document.documentElement).getPropertyValue(name).trim();
  return v || fallback;
}

// Only use for groups that own their resources (not shared content materials).
export function disposeObjectResources(root: THREE.Object3D): void {
  const geometries = new Set<THREE.BufferGeometry>();
  const materials = new Set<THREE.Material>();
  const textures = new Set<THREE.Texture>();
  root.traverse(object => {
    const mesh = object as THREE.Mesh;
    if (mesh.geometry) { geometries.add(mesh.geometry); }
    const list = Array.isArray(mesh.material) ? mesh.material : mesh.material ? [mesh.material] : [];
    for (const material of list) {
      materials.add(material);
      const map = (material as THREE.MeshBasicMaterial).map;
      if (map) { textures.add(map); }
    }
  });
  geometries.forEach(g => g.dispose());
  materials.forEach(m => m.dispose());
  textures.forEach(t => t.dispose());
}

// Whether a key event goes to a text field (or a select), where the arrows belong to the field.
function isTyping(target: EventTarget | null): boolean {
  const el = target as HTMLElement | null;
  return !!el && (['INPUT', 'SELECT', 'TEXTAREA'].includes(el.tagName) || el.isContentEditable);
}

export function billboardYaw(axis: 'x' | 'z', camera: THREE.Camera, position: THREE.Vector3): number {
  const direction = camera instanceof THREE.OrthographicCamera
    ? camera.getWorldDirection(new THREE.Vector3()).negate()
    : camera.position.clone().sub(position);
  return axis === 'z' ? Math.atan2(direction.x, direction.z) : Math.atan2(-direction.z, direction.x);
}
