import * as THREE from 'three';
import type { ZedModel, ZedModelChunk } from './zed-model-parser';

// Guard rails for the WebGL upload path. PS2 textures are always 2^N square
// and tiny by modern standards, so anything above 2048 is almost certainly a
// mis-parse — uploading it can blow VRAM budgets and crash the renderer
// process. The unique material cap is the same idea, sized for a composed
// map: M51 with its props and clutter uses ~110 textures, some of them in
// both a solid and a blended material. At 64, everything past the 64th
// texture drew as the flat grey fallback.
const MAX_TEXTURE_DIMENSION = 2048;
const MAX_UNIQUE_MATERIALS = 512;
// Hard cap on the BufferGeometry the renderer will allocate. World models
// stay well under 100k verts; anything above this is parsed garbage from a
// payload that isn't actually a 3D model (UI / script blobs that happen to
// match the chunk magic). Crashing the GPU process with a 200MB typed
// array is exactly the failure mode the user has been hitting.
const MAX_VERTICES_TOTAL = 2_000_000;
const MAX_INDICES_TOTAL = 6_000_000;

// Three.js viewer for ZED model payloads. Mirrors the structure of the PS2
// memory-card icon renderer (resize observer, raf loop, dispose) but uses an
// orbit-style mouse drag instead of an animation tween. The scene Y axis is
// inverted at the root so map/world models render right-side-up (the format
// stores them flipped vertically).

interface DragState {
  active: boolean;
  lastX: number;
  lastY: number;
}

export interface ModelTexturePixels {
  width: number;
  height: number;
  rgba: Uint8Array;
  // From the texture's GS CLAMP register: per-axis wrap mode.
  wrapS?: 'repeat' | 'clamp';
  wrapT?: 'repeat' | 'clamp';
  // From the GS TEST register: discard texels below this alpha (0..1).
  alphaTest?: number | null;
  // True when the decoded pixels contain any non-opaque texel.
  hasTransparency?: boolean;
  // Fraction of texels with alpha below 50% (0..1).
  transparentRatio?: number;
  // From the GS ALPHA register: the texture is alpha blended in game.
  alphaBlend?: boolean;
}

// A point of interest drawn as a marker pillar (spawn, waypoint ...), in the
// model's own coordinates.
export interface SceneMarker {
  position: [number, number, number];
  label: string;
  color: number;
  // Pillar height in model units.
  height?: number;
}

export interface ModelTextureMap {
  // Map from lowercased .tif name -> decoded texture pixels.
  [tifName: string]: ModelTexturePixels;
}

export type UvMode = 'direct' | 'flipV' | 'swap' | 'swapFlipV';
export type ModelCameraView = 'auto' | 'iso' | 'top' | 'front' | 'back' | 'left' | 'right' | 'mapTop' | 'mapIso';

export interface ModelBounds3 {
  minX: number; minY: number; minZ: number;
  maxX: number; maxY: number; maxZ: number;
}

export interface ModelRendererOptions {
  wireframe?: boolean;
  flipY?: boolean;
  chunkIndex?: number;
  // UV orientation applied to the vertex UVs before upload. PS2 conventions
  // vary per file family - the reference implementation exposes the same set.
  uvMode?: UvMode;
  // Per-chunk textures keyed by lowercased .tif filename.
  textures?: ModelTextureMap | null;
  // Default texture name (lowercased) for chunks that don't reference their
  // own .tif. PS2 weapon atlases share one sheet across all chunks.
  defaultTexture?: string | null;
  // Fallback single texture (used if chunk has no specific texture).
  textureRgba?: ModelTexturePixels | null;
  // Multiply the baked per-vertex colours from the model's DMA chains into
  // the material (the game's lighting for maps, props and vehicles). On by
  // default; chunks without colours get a neutral 1.0.
  vertexColors?: boolean;
  // Map-viewer style controls (HTML reference). Defaults preserve current
  // weapon-preview behaviour, so untouched options leave the renderer alone.
  triStride?: number;        // 1 = full, N = every Nth triangle.
  topDown?: boolean;         // orthographic top-down camera.
  unlit?: boolean;           // ignore directional light (ambient only).
  solidColor?: boolean;      // skip texture sampling, render flat material.
  transparentTextures?: boolean;
  wireOverlay?: boolean;
  wireOverlayColor?: number;
  cameraView?: ModelCameraView;
  referenceBounds?: { label?: string; bounds: ModelBounds3 } | null;
  showBounds?: boolean;
  // Only draw chunks whose visualKey matches (null / undefined = all).
  visualFilter?: string | null;
  // Multiplier on the baked vertex colours (1 = GS exact, 0x80 = 1.0).
  vertexColorScale?: number;
  // Marker pillars (spawn / waypoints).
  markers?: SceneMarker[] | null;
}

// What setModel actually drew, so the UI can say when chunks were left out.
export interface ModelRenderResult {
  totalChunks: number;
  renderedChunks: number;
  // Chunks skipped because the geometry caps were reached.
  droppedChunks: number;
  vertices: number;
  triangles: number;
  materials: number;
  // .tif keys that were requested but had no decoded pixels.
  missingTextures: string[];
  usedVertexColors: boolean;
}

export class ZedModelRenderer {
  private readonly renderer: THREE.WebGLRenderer;
  private readonly scene = new THREE.Scene();
  private readonly camera = new THREE.PerspectiveCamera(45, 4 / 3, 0.01, 5000);
  private readonly root = new THREE.Group();
  private readonly resizeObserver: ResizeObserver;

  private frameId = 0;
  private readonly maxAnisotropy: number;
  private mesh: THREE.Mesh | null = null;
  private wireOverlay: THREE.LineSegments | null = null;
  private boundsGuide: THREE.Group | null = null;
  private materials: THREE.Material[] = [];
  private textures: THREE.DataTexture[] = [];
  private boundingRadius = 1;

  private yaw = 0.6;
  private pitch = -0.3;
  private distance = 4;
  private cameraZoom = 1;
  // World -> scene offset (the geometry is re-centred on upload).
  private sceneOffset = new THREE.Vector3();
  private markerGroup: THREE.Group | null = null;
  // First-person fly camera. Position in scene space; yaw/pitch in radians.
  private flyMode = false;
  private readonly flyPos = new THREE.Vector3();
  private flyYaw = 0;
  private flyPitch = 0;
  private flySpeed = 60;
  private readonly keys = new Set<string>();
  private lastFrame = 0;
  // Called when the user leaves fly mode (Esc) so the UI can update.
  onFlyModeChange: ((flying: boolean) => void) | null = null;
  private wireframe = false;

  private readonly drag: DragState = { active: false, lastX: 0, lastY: 0 };

  public get attachedCanvas(): HTMLCanvasElement {
    return this.canvas;
  }

  constructor(private readonly canvas: HTMLCanvasElement) {
    this.renderer = new THREE.WebGLRenderer({ canvas, antialias: true, alpha: true });
    // Leave the framebuffer transparent so the canvas' own blueprint-style
    // background remains visible behind the model.
    this.renderer.setClearColor(0x111318, 0);
    this.renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2));
    // PS2 textures are stored in their final display form — no sRGB tag
    // needed. Tell Three.js to treat the framebuffer as linear so the
    // output sampler doesn't gamma-correct an already-correct image (this
    // was making the world model look washed out compared to the HTML
    // reference renderer).
    this.renderer.outputColorSpace = THREE.LinearSRGBColorSpace;
    this.maxAnisotropy = Math.min(8, this.renderer.capabilities.getMaxAnisotropy());

    this.scene.add(this.root);

    // Brighter ambient + dimmer key light to better approximate the HTML
    // map-viewer's flat-lit look (which uses fog + a single directional
    // term rather than a full PBR setup).
    const ambient = new THREE.AmbientLight(0xffffff, 1.05);
    const key = new THREE.DirectionalLight(0xffffff, 0.35);
    key.position.set(2, 2, 3);
    this.scene.add(ambient, key);

    this.resizeObserver = new ResizeObserver(() => this.resize());
    this.resizeObserver.observe(canvas);

    canvas.addEventListener('mousedown', this.onMouseDown);
    canvas.addEventListener('mousemove', this.onMouseMove);
    canvas.addEventListener('mouseup', this.onMouseUp);
    canvas.addEventListener('mouseleave', this.onMouseUp);
    canvas.addEventListener('wheel', this.onWheel, { passive: false });
    canvas.tabIndex = 0;
    canvas.addEventListener('keydown', this.onKeyDown);
    canvas.addEventListener('keyup', this.onKeyUp);
    canvas.addEventListener('blur', this.onBlur);

    this.resize();
    this.animate();
  }

  setModel(model: ZedModel | null, options: ModelRendererOptions = {}): ModelRenderResult {
    this.disposeMesh();
    const empty: ModelRenderResult = {
      totalChunks: model?.chunks.length ?? 0,
      renderedChunks: 0,
      droppedChunks: 0,
      vertices: 0,
      triangles: 0,
      materials: 0,
      missingTextures: [],
      usedVertexColors: false,
    };
    if (!model || model.chunks.length === 0) { return empty; }

    // Restrict to a single chunk / visual if requested.
    let visibleChunks =
      options.chunkIndex !== undefined &&
      options.chunkIndex >= 0 &&
      options.chunkIndex < model.chunks.length
        ? [model.chunks[options.chunkIndex]]
        : model.chunks;
    if (options.visualFilter) {
      const filtered = visibleChunks.filter(c => c.visualKey === options.visualFilter);
      if (filtered.length > 0) { visibleChunks = filtered; }
    }
    const missingTextures = new Set<string>();

    this.wireframe = !!options.wireframe;
    const textures = options.textures ?? {};
    // `solidColor` map-viewer mode skips texture sampling entirely so the
    // chunks render as flat material — useful for inspecting topology.
    const solidColorMode = !!options.solidColor;
    const transparentTextures = !!options.transparentTextures;
    // `unlit` removes the directional light contribution; everything is lit
    // by the ambient only. We approximate by using MeshBasicMaterial instead
    // of MeshStandardMaterial for textured chunks.
    const unlit = !!options.unlit;
    // Subsamples the triangle list: 1 = full, 2 = every other, etc. Mirrors
    // the HTML map viewer's triStep dropdown.
    const triStride = Math.max(1, options.triStride ?? 1);
    // Baked vertex colours are the game's lighting: when the model carries
    // them, render it pre-lit (no scene light) and multiply them in.
    const useVertexColors = (options.vertexColors ?? true) && !solidColorMode &&
      visibleChunks.some(c => c.hasVertexColors);
    const prelit = unlit || useVertexColors;
    const colorScale = options.vertexColorScale ?? 1;

    // Build the per-material texture cache. Each unique .tif becomes one
    // DataTexture + Mesh material. Chunks with no matching .tif fall
    // back to a plain grey material.
    const makeMaterial = (color: number, map?: THREE.DataTexture, pixels?: ModelTexturePixels, blended = false): THREE.Material => {
      // Alpha as the GS applies it: alpha = texel alpha * vertex alpha / 128.
      // Effects keep the soft-blend setup they had (transparentTextures).
      // A blended chunk (see blendsChunk) is drawn translucent in chain order
      // and, like the game, still writes depth; fully clear texels are
      // dropped so they don't hide what is behind them. Otherwise texels are
      // only discarded by the texture's own GS TEST alpha test (foliage
      // cutouts), and alpha is ignored.
      const gsAlphaTest = pixels?.alphaTest ?? null;
      let alphaTest: number;
      let translucent: boolean;
      let depthWrite = true;
      if (transparentTextures) {
        alphaTest = gsAlphaTest ?? 0.02;
        translucent = true;
        depthWrite = false;
      } else if (blended) {
        alphaTest = Math.max(gsAlphaTest ?? 0, 0.02);
        translucent = true;
      } else if (gsAlphaTest !== null && pixels?.hasTransparency) {
        alphaTest = gsAlphaTest;
        translucent = false;
      } else if ((pixels?.transparentRatio ?? 0) > 0.02) {
        // A cutout keeps depth sorting simple and matches the silhouette.
        alphaTest = 0.5;
        translucent = false;
        alphaTest = 0.5;
        translucent = false;
        alphaTest = 0.5;
        translucent = false;
        alphaTest = 0.5;
        translucent = false;
      } else {
        alphaTest = 0;
        translucent = false;
      }
      const base = {
        color,
        side: THREE.DoubleSide,
        wireframe: this.wireframe,
        transparent: translucent,
        depthWrite,
        alphaTest,
        vertexColors: useVertexColors,
      };
      const params = map ? { ...base, map } : base;
      if (prelit) {
        return new THREE.MeshBasicMaterial(params);
      }
      return new THREE.MeshStandardMaterial({
        ...params,
        metalness: 0,
        roughness: 0.85,
      });
    };
    const materialIndexByTif = new Map<string, number>();
    const fallbackMaterial = makeMaterial(0xb8b8b8);
    this.materials = [fallbackMaterial];
    const fallbackTexture = options.textureRgba ?? null;

    // A chunk is blended when its material's ALPHA_1 blends (the texture's
    // own ALPHA_1 when the chunk has no material block) and something is
    // actually translucent: the texture's alpha or the chunk's vertex alpha
    // (terrain layers fade into each other through it).
    const blendsChunk = (chunk: ZedModelChunk, tex: ModelTexturePixels): boolean => {
      if (transparentTextures || !(chunk.materialBlend ?? tex.alphaBlend ?? false)) { return false; }
      if (tex.hasTransparency) { return true; }
      return useVertexColors && !!chunk.hasVertexColors && chunk.vertices.some(v => (v.a ?? 128) < 128);
    };
    const textureDataFor = new Map<string, THREE.DataTexture>();

    const materialFor = (chunk: ZedModelChunk): number => {
      // solidColor mode short-circuits to the flat fallback material so
      // there's no per-chunk texture lookup at all.
      if (solidColorMode) { return 0; }
      // Fall back to the model-wide atlas texture if this chunk doesn't
      // carry its own .tif reference.
      const effective = chunk.textureName ?? options.defaultTexture ?? null;
      if (!effective) { return 0; }
      const tifKey = effective.toLowerCase();
      const tex = textures[tifKey] ?? fallbackTexture;
      if (!tex) { missingTextures.add(tifKey); return 0; }
      const blended = blendsChunk(chunk, tex);
      const key = `${tifKey}|${blended ? 'blend' : 'solid'}`;
      const existing = materialIndexByTif.get(key);
      if (existing !== undefined) { return existing; }
      // Cap the number of unique materials per model. Past this limit the
      // chunk just gets the fallback grey material — better than crashing
      // the renderer process by allocating dozens of WebGL textures.
      if (this.materials.length >= MAX_UNIQUE_MATERIALS) { return 0; }
      const shared = textureDataFor.get(tifKey);
      if (shared) {
        const mat = makeMaterial(0xffffff, shared, tex, blended);
        this.materials.push(mat);
        materialIndexByTif.set(key, this.materials.length - 1);
        return this.materials.length - 1;
      }
      // Sanity-check the decoded texture so a malformed header doesn't
      // bring down the GPU process. PS2 textures are always 2^N square in
      // practice; anything bigger than 2048 or smaller than 4 is junk.
      if (!Number.isFinite(tex.width) || !Number.isFinite(tex.height)) { return 0; }
      if (tex.width < 4 || tex.height < 4) { return 0; }
      if (tex.width > MAX_TEXTURE_DIMENSION || tex.height > MAX_TEXTURE_DIMENSION) { return 0; }
      const expectedLen = tex.width * tex.height * 4;
      if (tex.rgba.length < expectedLen) { return 0; }
      const dt = new THREE.DataTexture(
        tex.rgba,
        tex.width,
        tex.height,
        THREE.RGBAFormat,
        THREE.UnsignedByteType,
      );
      dt.flipY = false;
      // PS2 textures are stored in display-ready colour. Tagging them as
      // sRGB makes Three.js gamma-decode them on sample, which double-
      // corrects and washes out the colours vs. the HTML reference.
      dt.colorSpace = THREE.LinearSRGBColorSpace;
      // Closer to PS2 bilinear: linear mag, no mipmap (PS2 rarely uses
      // them in fixed-camera shots), repeat wrap for atlas tiling.
      // Mipmapped, trilinear + anisotropic. The game mipmaps its world
      // textures (TEX1 MMIN in the section materials); without mipmaps a
      // texture tiled across a map aliases into speckled noise at distance.
      dt.magFilter = THREE.LinearFilter;
      dt.minFilter = THREE.LinearMipmapLinearFilter;
      dt.generateMipmaps = true;
      dt.anisotropy = this.maxAnisotropy;
      // Wrap modes come from the texture's GS CLAMP register when known.
      dt.wrapS = tex.wrapS === 'clamp' ? THREE.ClampToEdgeWrapping : THREE.RepeatWrapping;
      dt.wrapT = tex.wrapT === 'clamp' ? THREE.ClampToEdgeWrapping : THREE.RepeatWrapping;
      dt.needsUpdate = true;
      this.textures.push(dt);
      textureDataFor.set(tifKey, dt);
      const mat = makeMaterial(0xffffff, dt, tex, blended);
      this.materials.push(mat);
      const idx = this.materials.length - 1;
      materialIndexByTif.set(key, idx);
      return idx;
    };

    // Pack each visible chunk's vertices + indices into one BufferGeometry,
    // adding a draw group per chunk so its material is applied.
    const allPositions: number[] = [];
    const allUvs: number[] = [];
    const allColors: number[] = [];
    const allIndices: number[] = [];
    const groups: Array<{ start: number; count: number; material: number }> = [];
    let vBase = 0;
    let iBase = 0;
    const uvMode = options.uvMode ?? 'direct';
    const transformUv = (u: number, v: number): [number, number] => {
      let outU = u;
      let outV = v;
      if (uvMode === 'swap' || uvMode === 'swapFlipV') {
        const t = outU; outU = outV; outV = t;
      }
      if (uvMode === 'flipV' || uvMode === 'swapFlipV') {
        outV = 1 - outV;
      }
      return [outU, outV];
    };
    let droppedChunks = 0;
    let renderedChunks = 0;
    for (const chunk of visibleChunks) {
      // Hard stop: a single malformed chunk shouldn't be able to allocate
      // millions of floats and tip the renderer process over the OOM line.
      // Chunks past the cap are counted and reported, never silently lost.
      if (vBase + chunk.vertices.length > MAX_VERTICES_TOTAL || iBase + chunk.indices.length > MAX_INDICES_TOTAL) {
        droppedChunks++;
        continue;
      }
      renderedChunks++;
      const chunkHasColors = !!chunk.hasVertexColors;
      for (const v of chunk.vertices) {
        allPositions.push(v.x, v.y, v.z);
        const [tu, tv] = transformUv(v.u, v.v);
        allUvs.push(tu, tv);
        if (useVertexColors) {
          // GS modulation: texel * colour / 128, so 0x80 is neutral and
          // 0xFF doubles the brightness. Chunks without a colour block
          // draw at 1.0.
          // Vertex alpha modulates texel alpha the same way (0x80 = 1.0).
          if (chunkHasColors) {
            allColors.push((v.r ?? 128) * colorScale / 128, (v.g ?? 128) * colorScale / 128, (v.b ?? 128) * colorScale / 128, Math.min(1, (v.a ?? 128) / 128));
          } else {
            allColors.push(1, 1, 1, 1);
          }
        }
      }
      // Apply the triangle stride here so the stride applies per-chunk.
      // Each chunk.indices is laid out as flat triples (a, b, c, a, b, c…).
      let kept = 0;
      for (let t = 0; t < chunk.indices.length; t += 3) {
        const triIndex = t / 3;
        if (triStride > 1 && triIndex % triStride !== 0) { continue; }
        allIndices.push(
          vBase + chunk.indices[t + 0],
          vBase + chunk.indices[t + 1],
          vBase + chunk.indices[t + 2],
        );
        kept += 3;
      }
      groups.push({ start: iBase, count: kept, material: materialFor(chunk) });
      vBase += chunk.vertices.length;
      iBase += kept;
    }

    if (droppedChunks > 0) {
      console.warn(
        `[ZedModelRenderer] ${droppedChunks} chunk(s) exceeded the vertex/index caps and were not drawn ` +
          `(vertices=${vBase} indices=${iBase})`,
      );
    }
    const result: ModelRenderResult = {
      totalChunks: model.chunks.length,
      renderedChunks,
      droppedChunks,
      vertices: vBase,
      triangles: (iBase / 3) | 0,
      materials: 0,
      missingTextures: [...missingTextures].sort(),
      usedVertexColors: useVertexColors,
    };
    if (allIndices.length === 0) { return result; }

    const geometry = new THREE.BufferGeometry();
    geometry.setAttribute('position', new THREE.BufferAttribute(new Float32Array(allPositions), 3));
    geometry.setAttribute('uv', new THREE.BufferAttribute(new Float32Array(allUvs), 2));
    if (useVertexColors) {
      geometry.setAttribute('color', new THREE.BufferAttribute(new Float32Array(allColors), 4));
    }
    geometry.setIndex(allIndices);
    geometry.computeVertexNormals();

    // Re-center the geometry so it sits in front of the camera regardless of
    // where the model authored its world position. Maps carry far-away
    // billboards (sun glow, sky cards) that would dwarf the playable area,
    // so frame on the bulk of the vertices (2nd..98th percentile per axis)
    // when that box is much smaller than the full one.
    geometry.computeBoundingBox();
    const box = geometry.boundingBox;
    const fitBox = trimmedBox(allPositions) ?? box;
    const originalCenter = new THREE.Vector3();
    if (fitBox) {
      const cx = (fitBox.min.x + fitBox.max.x) / 2;
      const cy = (fitBox.min.y + fitBox.max.y) / 2;
      const cz = (fitBox.min.z + fitBox.max.z) / 2;
      originalCenter.set(cx, cy, cz);
      geometry.translate(-cx, -cy, -cz);
      // Keep a fly camera at the same world position across re-renders.
      if (this.flyMode) { this.flyPos.add(this.sceneOffset).sub(originalCenter); }
      this.sceneOffset.set(cx, cy, cz);
    }
    geometry.computeBoundingSphere();
    geometry.computeBoundingBox();

    for (const g of groups) {
      geometry.addGroup(g.start, g.count, g.material);
    }

    const fitRadiusFromBox = fitBox ? radiusFromBox(fitBox) : 0;
    this.boundingRadius = fitRadiusFromBox || geometry.boundingSphere?.radius || 1;
    const referenceBox = options.referenceBounds
      ? boundsToBox3(options.referenceBounds.bounds).translate(originalCenter.clone().multiplyScalar(-1))
      : null;
    const modelBox = geometry.boundingBox?.clone() ?? null;
    const fitRadius = cameraFitRadius(this.boundingRadius, modelBox, referenceBox, options.cameraView);
    this.distance = fitRadius * 2.75 || 4;
    this.cameraZoom = 1;
    this.updateCameraProjection();

    this.mesh = new THREE.Mesh(geometry, this.materials);
    // These payloads often have unusual authoring bounds. A false-positive
    // frustum cull looks like random model parts disappearing while orbiting,
    // so keep debug/model payloads explicitly visible.
    this.mesh.frustumCulled = false;
    if (options.flipY) {
      this.mesh.rotation.x = Math.PI;
    }
    this.root.add(this.mesh);
    this.setMarkers(options.markers ?? null);

    if (options.wireOverlay && !this.wireframe) {
      const wireGeometry = new THREE.WireframeGeometry(geometry);
      const wireMaterial = new THREE.LineBasicMaterial({
        color: options.wireOverlayColor ?? 0xffd36a,
        transparent: true,
        opacity: 0.9,
        depthWrite: false,
      });
      this.wireOverlay = new THREE.LineSegments(wireGeometry, wireMaterial);
      this.wireOverlay.frustumCulled = false;
      this.root.add(this.wireOverlay);
    }

    if (options.showBounds || referenceBox) {
      this.boundsGuide = new THREE.Group();
      this.boundsGuide.renderOrder = 20;
      if (modelBox) {
        this.boundsGuide.add(createBoxGuide(modelBox, 0x8ea8b8, 0.45));
      }
      if (referenceBox) {
        this.boundsGuide.add(createBoxGuide(referenceBox, 0x4ec9b0, 0.95));
      }
      // Bounds boxes are an orbit-view aid; they cut across the view in first person.
      this.boundsGuide.visible = !this.flyMode;
      this.root.add(this.boundsGuide);
    }

    this.applyCameraView(options.cameraView ?? (options.topDown ? 'top' : 'auto'), !!referenceBox);
    result.materials = Math.max(0, this.materials.length - 1);
    return result;
  }

  setFlipY(value: boolean): void {
    if (this.mesh) {
      this.mesh.rotation.x = value ? Math.PI : 0;
    }
  }

  setWireframe(value: boolean): void {
    this.wireframe = value;
    for (const mat of this.materials) {
      if ('wireframe' in mat) {
        (mat as THREE.MeshStandardMaterial).wireframe = value;
      }
    }
  }

  dispose(): void {
    cancelAnimationFrame(this.frameId);
    this.resizeObserver.disconnect();
    this.canvas.removeEventListener('mousedown', this.onMouseDown);
    this.canvas.removeEventListener('mousemove', this.onMouseMove);
    this.canvas.removeEventListener('mouseup', this.onMouseUp);
    this.canvas.removeEventListener('mouseleave', this.onMouseUp);
    this.canvas.removeEventListener('wheel', this.onWheel);
    this.canvas.removeEventListener('keydown', this.onKeyDown);
    this.canvas.removeEventListener('keyup', this.onKeyUp);
    this.canvas.removeEventListener('blur', this.onBlur);
    this.removeWindowKeys();
    this.disposeMesh();
    this.renderer.dispose();
  }

  private animate = (): void => {
    this.frameId = requestAnimationFrame(this.animate);
    this.resize();
    const now = performance.now();
    const dt = this.lastFrame ? Math.min(0.1, (now - this.lastFrame) / 1000) : 0;
    this.lastFrame = now;
    // Debug boxes only clutter a first-person view.
    if (this.boundsGuide) { this.boundsGuide.visible = !this.flyMode; }
    if (this.flyMode) {
      this.stepFly(dt);
      this.renderer.render(this.scene, this.camera);
      return;
    }
    const r = this.distance;
    this.camera.zoom = this.cameraZoom;
    this.camera.position.set(
      r * Math.cos(this.pitch) * Math.sin(this.yaw),
      r * Math.sin(this.pitch),
      r * Math.cos(this.pitch) * Math.cos(this.yaw),
    );
    this.camera.lookAt(0, 0, 0);
    this.renderer.render(this.scene, this.camera);
  };

  private resize(): void {
    const width = Math.max(1, this.canvas.clientWidth);
    const height = Math.max(1, this.canvas.clientHeight);
    const drawing = this.renderer.getDrawingBufferSize(new THREE.Vector2());
    const pixelRatio = this.renderer.getPixelRatio();
    if (drawing.x !== Math.floor(width * pixelRatio) || drawing.y !== Math.floor(height * pixelRatio)) {
      this.renderer.setSize(width, height, false);
      this.camera.aspect = width / height;
      this.updateCameraProjection();
    }
  }

  private onMouseDown = (e: MouseEvent): void => {
    this.canvas.focus();
    this.drag.active = true;
    this.drag.lastX = e.clientX;
    this.drag.lastY = e.clientY;
  };

  private onMouseMove = (e: MouseEvent): void => {
    if (!this.drag.active) { return; }
    const dx = e.clientX - this.drag.lastX;
    const dy = e.clientY - this.drag.lastY;
    this.drag.lastX = e.clientX;
    this.drag.lastY = e.clientY;
    if (this.flyMode) {
      this.flyYaw -= dx * 0.005;
      this.flyPitch = Math.max(-Math.PI / 2 + 0.05, Math.min(Math.PI / 2 - 0.05, this.flyPitch - dy * 0.005));
      return;
    }
    this.yaw -= dx * 0.01;
    this.pitch = Math.max(-Math.PI / 2 + 0.05, Math.min(Math.PI / 2 - 0.05, this.pitch + dy * 0.01));
  };

  private onMouseUp = (): void => {
    this.drag.active = false;
  };

  private onWheel = (e: WheelEvent): void => {
    e.preventDefault();
    if (this.flyMode) {
      // In fly mode the wheel sets the walking speed.
      this.flySpeed = Math.max(5, Math.min(2000, this.flySpeed * (e.deltaY > 0 ? 1 / 1.2 : 1.2)));
      return;
    }
    const factor = e.deltaY > 0 ? 1 / 1.1 : 1.1;
    // Match the standalone HTML viewer's feel: zoom the view without moving
    // the camera through the object, which avoids near-plane clipping while
    // orbiting around detailed models.
    this.cameraZoom = Math.max(0.08, Math.min(40, this.cameraZoom * factor));
    this.updateCameraProjection();
  };

  // Enter first-person mode at a point in model coordinates, eye height
  // already included, facing `yaw` (radians, 0 = +Z).
  startFly(position: [number, number, number], yaw: number, speed = 60): void {
    this.flyPos.set(position[0], position[1], position[2]).sub(this.sceneOffset);
    this.flyYaw = yaw;
    this.flyPitch = -0.05;
    this.flySpeed = speed;
    this.cameraZoom = 1;
    this.flyMode = true;
    if (this.boundsGuide) { this.boundsGuide.visible = false; }
    this.camera.near = 0.5;
    this.camera.zoom = 1;
    this.camera.updateProjectionMatrix();
    this.keys.clear();
    this.canvas.focus();
    // Listen on the window while flying so the keys work wherever focus is
    // (the button that started flying keeps it in VS Code webviews).
    window.addEventListener('keydown', this.onKeyDown);
    window.addEventListener('keyup', this.onKeyUp);
    window.addEventListener('blur', this.onBlur);
    this.onFlyModeChange?.(true);
  }

  stopFly(): void {
    if (!this.flyMode) { return; }
    this.flyMode = false;
    if (this.boundsGuide) { this.boundsGuide.visible = true; }
    this.keys.clear();
    this.removeWindowKeys();
    this.updateCameraProjection();
    this.onFlyModeChange?.(false);
  }

  get isFlying(): boolean {
    return this.flyMode;
  }

  private stepFly(dt: number): void {
    const fast = this.keys.has('shift') ? 4 : 1;
    const step = this.flySpeed * fast * dt;
    const forward = new THREE.Vector3(Math.sin(this.flyYaw) * Math.cos(this.flyPitch), Math.sin(this.flyPitch), Math.cos(this.flyYaw) * Math.cos(this.flyPitch));
    const right = new THREE.Vector3(Math.cos(this.flyYaw), 0, -Math.sin(this.flyYaw));
    const move = new THREE.Vector3();
    if (this.keys.has('w') || this.keys.has('arrowup')) { move.add(forward); }
    if (this.keys.has('s') || this.keys.has('arrowdown')) { move.sub(forward); }
    if (this.keys.has('d') || this.keys.has('arrowright')) { move.sub(right); }
    if (this.keys.has('a') || this.keys.has('arrowleft')) { move.add(right); }
    if (this.keys.has('e') || this.keys.has(' ')) { move.y += 1; }
    if (this.keys.has('q') || this.keys.has('c')) { move.y -= 1; }
    if (move.lengthSq() > 0) { this.flyPos.addScaledVector(move.normalize(), step); }
    this.camera.position.copy(this.flyPos);
    this.camera.lookAt(this.flyPos.clone().add(forward));
  }

  private removeWindowKeys(): void {
    window.removeEventListener('keydown', this.onKeyDown);
    window.removeEventListener('keyup', this.onKeyUp);
    window.removeEventListener('blur', this.onBlur);
  }

  private onKeyDown = (e: KeyboardEvent): void => {
    const key = e.key.toLowerCase();
    if (!this.flyMode) { return; }
    // Leave typing and dropdown navigation alone.
    const target = e.target as HTMLElement | null;
    if (target && target !== this.canvas && /^(?:INPUT|SELECT|TEXTAREA)$/.test(target.tagName)) { return; }
    if (key === 'escape') { this.stopFly(); e.preventDefault(); return; }
    if (['w', 'a', 's', 'd', 'q', 'e', 'c', ' ', 'shift', 'arrowup', 'arrowdown', 'arrowleft', 'arrowright'].includes(key)) {
      this.keys.add(key);
      e.preventDefault();
      e.stopPropagation();
    }
  };

  private onKeyUp = (e: KeyboardEvent): void => {
    this.keys.delete(e.key.toLowerCase());
  };

  private onBlur = (): void => {
    this.keys.clear();
  };

  setMarkers(markers: SceneMarker[] | null): void {
    if (this.markerGroup) {
      this.root.remove(this.markerGroup);
      this.markerGroup.traverse(obj => {
        if (obj instanceof THREE.Mesh) {
          obj.geometry.dispose();
          (obj.material as THREE.Material).dispose();
        }
      });
      this.markerGroup = null;
    }
    if (!markers || markers.length === 0) { return; }
    const group = new THREE.Group();
    const radius = Math.max(1, this.boundingRadius / 400);
    for (const m of markers) {
      const height = m.height ?? radius * 12;
      const geometry = new THREE.CylinderGeometry(radius, radius * 0.4, height, 8);
      const material = new THREE.MeshBasicMaterial({ color: m.color, depthTest: true });
      const pillar = new THREE.Mesh(geometry, material);
      pillar.position.set(m.position[0], m.position[1] + height / 2, m.position[2]).sub(this.sceneOffset);
      pillar.name = m.label;
      pillar.frustumCulled = false;
      group.add(pillar);
    }
    this.markerGroup = group;
    this.root.add(group);
  }

  private updateCameraProjection(): void {
    const radius = Math.max(1e-3, this.boundingRadius);
    this.camera.near = this.flyMode ? 0.5 : Math.max(0.01, radius / 1000);
    this.camera.far = Math.max(100, this.distance + radius * 8);
    this.camera.zoom = this.cameraZoom;
    this.camera.updateProjectionMatrix();
  }

  private disposeMesh(): void {
    if (this.mesh) {
      this.root.remove(this.mesh);
      this.mesh.geometry.dispose();
      this.mesh = null;
    }
    if (this.wireOverlay) {
      this.root.remove(this.wireOverlay);
      this.wireOverlay.geometry.dispose();
      const material = this.wireOverlay.material;
      if (Array.isArray(material)) {
        for (const m of material) { m.dispose(); }
      } else {
        material.dispose();
      }
      this.wireOverlay = null;
    }
    for (const m of this.materials) { m.dispose(); }
    this.materials = [];
    for (const t of this.textures) { t.dispose(); }
    this.textures = [];
    if (this.boundsGuide) {
      this.root.remove(this.boundsGuide);
      this.boundsGuide.traverse(obj => {
        if (obj instanceof THREE.LineSegments) {
          obj.geometry.dispose();
          const material = obj.material;
          if (Array.isArray(material)) {
            for (const m of material) { m.dispose(); }
          } else {
            material.dispose();
          }
        }
      });
      this.boundsGuide = null;
    }
  }

  private applyCameraView(view: ModelCameraView, hasReferenceBounds: boolean): void {
    const effective = view === 'auto'
      ? 'iso'
      : view === 'mapTop' && !hasReferenceBounds
        ? 'top'
        : view === 'mapIso' && !hasReferenceBounds
          ? 'iso'
          : view;
    switch (effective) {
      case 'top':
      case 'mapTop':
        this.pitch = -Math.PI / 2 + 0.001;
        this.yaw = 0;
        break;
      case 'front':
        this.pitch = 0;
        this.yaw = 0;
        break;
      case 'back':
        this.pitch = 0;
        this.yaw = Math.PI;
        break;
      case 'left':
        this.pitch = 0;
        this.yaw = -Math.PI / 2;
        break;
      case 'right':
        this.pitch = 0;
        this.yaw = Math.PI / 2;
        break;
      case 'mapIso':
      case 'iso':
      default:
        this.pitch = -0.3;
        this.yaw = 0.6;
        break;
    }
  }
}

function boundsToBox3(bounds: ModelBounds3): THREE.Box3 {
  return new THREE.Box3(
    new THREE.Vector3(bounds.minX, bounds.minY, bounds.minZ),
    new THREE.Vector3(bounds.maxX, bounds.maxY, bounds.maxZ),
  );
}

// Box over the 2nd..98th percentile of the vertex positions on each axis,
// or null when it is not much smaller than the full box (nothing to trim).
function trimmedBox(positions: number[]): THREE.Box3 | null {
  const count = positions.length / 3;
  if (count < 64) { return null; }
  const step = Math.max(1, Math.floor(count / 20000));
  const axes: number[][] = [[], [], []];
  for (let i = 0; i < count; i += step) {
    axes[0].push(positions[i * 3]);
    axes[1].push(positions[i * 3 + 1]);
    axes[2].push(positions[i * 3 + 2]);
  }
  const lo: number[] = [], hi: number[] = [], fullLo: number[] = [], fullHi: number[] = [];
  for (const a of axes) {
    a.sort((x, y) => x - y);
    lo.push(a[Math.floor(a.length * 0.02)]);
    hi.push(a[Math.min(a.length - 1, Math.ceil(a.length * 0.98))]);
    fullLo.push(a[0]);
    fullHi.push(a[a.length - 1]);
  }
  const trimmed = new THREE.Box3(new THREE.Vector3(lo[0], lo[1], lo[2]), new THREE.Vector3(hi[0], hi[1], hi[2]));
  const full = new THREE.Box3(new THREE.Vector3(fullLo[0], fullLo[1], fullLo[2]), new THREE.Vector3(fullHi[0], fullHi[1], fullHi[2]));
  return radiusFromBox(trimmed) < radiusFromBox(full) * 0.6 ? trimmed : null;
}

function radiusFromBox(box: THREE.Box3 | null): number {
  if (!box) { return 0; }
  const size = new THREE.Vector3();
  box.getSize(size);
  return Math.max(0.001, size.length() * 0.5);
}

function cameraFitRadius(modelRadius: number, modelBox: THREE.Box3 | null, referenceBox: THREE.Box3 | null, view?: ModelCameraView): number {
  if (!referenceBox || (view !== 'mapTop' && view !== 'mapIso')) {
    return Math.max(0.001, modelRadius);
  }
  const combined = new THREE.Box3();
  if (modelBox) { combined.union(modelBox); }
  combined.union(referenceBox);
  return Math.max(modelRadius, radiusFromBox(combined));
}

function createBoxGuide(box: THREE.Box3, color: number, opacity: number): THREE.LineSegments {
  const min = box.min;
  const max = box.max;
  const pts = [
    min.x, min.y, min.z, max.x, min.y, min.z,
    max.x, min.y, min.z, max.x, min.y, max.z,
    max.x, min.y, max.z, min.x, min.y, max.z,
    min.x, min.y, max.z, min.x, min.y, min.z,

    min.x, max.y, min.z, max.x, max.y, min.z,
    max.x, max.y, min.z, max.x, max.y, max.z,
    max.x, max.y, max.z, min.x, max.y, max.z,
    min.x, max.y, max.z, min.x, max.y, min.z,

    min.x, min.y, min.z, min.x, max.y, min.z,
    max.x, min.y, min.z, max.x, max.y, min.z,
    max.x, min.y, max.z, max.x, max.y, max.z,
    min.x, min.y, max.z, min.x, max.y, max.z,
  ];
  const geometry = new THREE.BufferGeometry();
  geometry.setAttribute('position', new THREE.Float32BufferAttribute(pts, 3));
  const material = new THREE.LineBasicMaterial({
    color,
    transparent: true,
    opacity,
    depthTest: false,
    depthWrite: false,
  });
  const lines = new THREE.LineSegments(geometry, material);
  lines.frustumCulled = false;
  lines.renderOrder = 20;
  return lines;
}
