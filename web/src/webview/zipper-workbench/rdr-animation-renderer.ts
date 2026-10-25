import {
  RdrAnimationClip,
  Vec3,
  animationFrameRate,
  buildAnimationPose,
  cleanBoneName,
  computeAnimationBounds,
  isAttachmentBone,
} from './rdr-animation-parser';

interface AnimationView {
  yaw: number;
  pitch: number;
  zoom: number;
  scale: number;
  panX: number;
  panY: number;
  center: Vec3;
  dragging: boolean;
  lastX: number;
  lastY: number;
}

interface ProjectedPoint {
  x: number;
  y: number;
  z: number;
}

export class RdrAnimationCanvasRenderer {
  private readonly ctx: CanvasRenderingContext2D;
  private clip: RdrAnimationClip | null = null;
  private frame = 0;
  private playing = true;
  private raf = 0;
  private lastTick = 0;
  private frameRemainder = 0;
  private disposed = false;

  private readonly view: AnimationView = {
    yaw: -0.65,
    pitch: -0.35,
    zoom: 1,
    scale: 1,
    panX: 0,
    panY: 0,
    center: [0, 0, 0],
    dragging: false,
    lastX: 0,
    lastY: 0,
  };

  onFrameChange?: (frame: number) => void;

  constructor(public readonly attachedCanvas: HTMLCanvasElement) {
    const ctx = attachedCanvas.getContext('2d');
    if (!ctx) { throw new Error('2D canvas context unavailable'); }
    this.ctx = ctx;
    this.attachEvents();
    this.raf = requestAnimationFrame(this.tick);
  }

  setClip(clip: RdrAnimationClip | null): void {
    this.clip = clip;
    this.frame = 0;
    this.frameRemainder = 0;
    if (clip) { this.resetView(); }
    this.onFrameChange?.(this.frame);
    this.draw();
  }

  setPlaying(value: boolean): void {
    this.playing = value;
  }

  isPlaying(): boolean {
    return this.playing;
  }

  setFrame(frame: number): void {
    const maxFrame = Math.max(0, (this.clip?.sampleCount ?? 1) - 1);
    this.frame = Math.max(0, Math.min(maxFrame, Math.floor(frame)));
    this.frameRemainder = 0;
    this.onFrameChange?.(this.frame);
    this.draw();
  }

  currentFrame(): number {
    return this.frame;
  }

  resetView(): void {
    if (!this.clip) { return; }
    const bounds = computeAnimationBounds(this.clip);
    this.view.center = bounds.center;
    this.view.scale = 260 / bounds.maxDim;
    this.view.yaw = -0.65;
    this.view.pitch = -0.35;
    this.view.zoom = 1;
    this.view.panX = 0;
    this.view.panY = 0;
    this.draw();
  }

  dispose(): void {
    this.disposed = true;
    if (this.raf) { cancelAnimationFrame(this.raf); }
    this.detachEvents();
  }

  private readonly tick = (now: number): void => {
    if (this.disposed) { return; }
    const clip = this.clip;
    if (clip && this.playing && this.lastTick > 0) {
      const fps = Math.max(1, Math.min(60, animationFrameRate(clip)));
      this.frameRemainder += (now - this.lastTick) / 1000 * fps;
      if (this.frameRemainder >= 1) {
        const advance = Math.floor(this.frameRemainder);
        this.frameRemainder -= advance;
        this.frame = (this.frame + advance) % Math.max(1, clip.sampleCount || 1);
        this.onFrameChange?.(this.frame);
      }
    }
    this.lastTick = now;
    this.draw();
    this.raf = requestAnimationFrame(this.tick);
  };

  private readonly onMouseDown = (event: MouseEvent): void => {
    this.view.dragging = true;
    this.view.lastX = event.clientX;
    this.view.lastY = event.clientY;
  };

  private readonly onMouseUp = (): void => {
    this.view.dragging = false;
  };

  private readonly onMouseMove = (event: MouseEvent): void => {
    if (!this.view.dragging) { return; }
    const dx = event.clientX - this.view.lastX;
    const dy = event.clientY - this.view.lastY;
    this.view.lastX = event.clientX;
    this.view.lastY = event.clientY;
    this.view.yaw += dx * 0.01;
    this.view.pitch = Math.max(-1.45, Math.min(1.45, this.view.pitch + dy * 0.01));
    this.draw();
  };

  private readonly onWheel = (event: WheelEvent): void => {
    event.preventDefault();
    const factor = event.deltaY < 0 ? 1.1 : 0.9;
    this.view.zoom = Math.max(0.05, Math.min(20, this.view.zoom * factor));
    this.draw();
  };

  private attachEvents(): void {
    this.attachedCanvas.addEventListener('mousedown', this.onMouseDown);
    this.attachedCanvas.addEventListener('wheel', this.onWheel, { passive: false });
    window.addEventListener('mouseup', this.onMouseUp);
    window.addEventListener('mousemove', this.onMouseMove);
  }

  private detachEvents(): void {
    this.attachedCanvas.removeEventListener('mousedown', this.onMouseDown);
    this.attachedCanvas.removeEventListener('wheel', this.onWheel);
    window.removeEventListener('mouseup', this.onMouseUp);
    window.removeEventListener('mousemove', this.onMouseMove);
  }

  private resize(): { width: number; height: number } {
    const rect = this.attachedCanvas.getBoundingClientRect();
    const dpr = Math.max(1, window.devicePixelRatio || 1);
    const width = Math.max(1, rect.width);
    const height = Math.max(1, rect.height);
    const targetW = Math.floor(width * dpr);
    const targetH = Math.floor(height * dpr);
    if (this.attachedCanvas.width !== targetW || this.attachedCanvas.height !== targetH) {
      this.attachedCanvas.width = targetW;
      this.attachedCanvas.height = targetH;
    }
    this.ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    return { width, height };
  }

  private transformPoint(p: Vec3, width: number, height: number): ProjectedPoint {
    let x = p[0] - this.view.center[0];
    let y = p[1] - this.view.center[1];
    let z = p[2] - this.view.center[2];

    const cy = Math.cos(this.view.yaw);
    const sy = Math.sin(this.view.yaw);
    const cp = Math.cos(this.view.pitch);
    const sp = Math.sin(this.view.pitch);

    const x1 = x * cy - z * sy;
    const z1 = x * sy + z * cy;
    const y1 = y;
    const y2 = y1 * cp - z1 * sp;
    const z2 = y1 * sp + z1 * cp;
    const scale = this.view.scale * this.view.zoom;

    return {
      x: width / 2 + this.view.panX + x1 * scale,
      y: height / 2 + this.view.panY - y2 * scale,
      z: z2,
    };
  }

  private drawGrid(width: number, height: number): void {
    const gridSize = 220;
    const step = 22;
    this.ctx.lineWidth = 1;
    this.ctx.strokeStyle = 'rgba(255,255,255,0.055)';
    for (let i = -gridSize; i <= gridSize; i += step) {
      const a = this.transformPoint([i / this.view.scale + this.view.center[0], 0, -gridSize / this.view.scale + this.view.center[2]], width, height);
      const b = this.transformPoint([i / this.view.scale + this.view.center[0], 0, gridSize / this.view.scale + this.view.center[2]], width, height);
      this.ctx.beginPath();
      this.ctx.moveTo(a.x, a.y);
      this.ctx.lineTo(b.x, b.y);
      this.ctx.stroke();

      const c = this.transformPoint([-gridSize / this.view.scale + this.view.center[0], 0, i / this.view.scale + this.view.center[2]], width, height);
      const d = this.transformPoint([gridSize / this.view.scale + this.view.center[0], 0, i / this.view.scale + this.view.center[2]], width, height);
      this.ctx.beginPath();
      this.ctx.moveTo(c.x, c.y);
      this.ctx.lineTo(d.x, d.y);
      this.ctx.stroke();
    }
  }

  private drawAxis(width: number, height: number): void {
    const origin = this.transformPoint(this.view.center, width, height);
    const len = 80 / Math.max(this.view.scale * this.view.zoom, 0.0001);
    const ax = this.transformPoint([this.view.center[0] + len, this.view.center[1], this.view.center[2]], width, height);
    const ay = this.transformPoint([this.view.center[0], this.view.center[1] + len, this.view.center[2]], width, height);
    const az = this.transformPoint([this.view.center[0], this.view.center[1], this.view.center[2] + len], width, height);

    this.ctx.lineWidth = 2;
    this.ctx.font = '12px Consolas, monospace';
    this.ctx.strokeStyle = '#f44747';
    this.ctx.beginPath();
    this.ctx.moveTo(origin.x, origin.y);
    this.ctx.lineTo(ax.x, ax.y);
    this.ctx.stroke();
    this.ctx.fillStyle = '#f44747';
    this.ctx.fillText('X', ax.x + 4, ax.y);

    this.ctx.strokeStyle = '#4ec9b0';
    this.ctx.beginPath();
    this.ctx.moveTo(origin.x, origin.y);
    this.ctx.lineTo(ay.x, ay.y);
    this.ctx.stroke();
    this.ctx.fillStyle = '#4ec9b0';
    this.ctx.fillText('Y', ay.x + 4, ay.y);

    this.ctx.strokeStyle = '#569cd6';
    this.ctx.beginPath();
    this.ctx.moveTo(origin.x, origin.y);
    this.ctx.lineTo(az.x, az.y);
    this.ctx.stroke();
    this.ctx.fillStyle = '#569cd6';
    this.ctx.fillText('Z', az.x + 4, az.y);
  }

  private drawOverlay(clip: RdrAnimationClip, width: number, root: Vec3): void {
    const lines = [
      `clip: ${clip.name}`,
      `frame: ${this.frame}/${Math.max(0, (clip.sampleCount || 1) - 1)}`,
      `root xyz: [${root.map(v => Number(v).toFixed(2)).join(', ')}]`,
      `bones: ${clip.boneCount}`,
      `scale: ${(this.view.scale * this.view.zoom).toFixed(4)}`,
      'drag: orbit   wheel: zoom',
    ];
    const x = 12;
    const y = 12;
    const lineH = 16;
    const boxW = Math.max(140, Math.min(width - 24, 330));
    const boxH = lines.length * lineH + 14;
    this.ctx.fillStyle = 'rgba(0,0,0,0.58)';
    this.ctx.strokeStyle = 'rgba(255,255,255,0.08)';
    this.ctx.lineWidth = 1;
    this.ctx.beginPath();
    this.ctx.roundRect(x, y, boxW, boxH, 6);
    this.ctx.fill();
    this.ctx.stroke();
    this.ctx.fillStyle = '#d4d4d4';
    this.ctx.font = '12px Consolas, monospace';
    for (let i = 0; i < lines.length; i++) {
      this.ctx.fillText(lines[i], x + 10, y + 18 + i * lineH);
    }
  }

  private draw(): void {
    const { width, height } = this.resize();
    this.ctx.clearRect(0, 0, width, height);
    this.ctx.fillStyle = '#1e1e1e';
    this.ctx.fillRect(0, 0, width, height);

    const clip = this.clip;
    if (!clip) {
      this.ctx.fillStyle = '#858585';
      this.ctx.font = '14px Consolas, monospace';
      this.ctx.fillText('No animation selected.', 20, 30);
      return;
    }
    if (!clip.tracks.length || !clip.bones.length) {
      this.ctx.fillStyle = '#858585';
      this.ctx.font = '14px Consolas, monospace';
      this.ctx.fillText('Selected entry has no parsed skeleton tracks.', 20, 30);
      return;
    }

    this.drawGrid(width, height);
    const frameIndex = this.frame % Math.max(1, clip.sampleCount || 1);
    const pose = buildAnimationPose(clip, frameIndex);
    const projected = pose.worldPos.map(p => this.transformPoint(p, width, height));

    if ((clip.sampleCount || 0) > 1) {
      this.ctx.strokeStyle = '#4ec9b0';
      this.ctx.lineWidth = 2;
      this.ctx.beginPath();
      for (let i = 0; i < clip.sampleCount; i++) {
        const trailPose = buildAnimationPose(clip, i);
        const p = this.transformPoint(trailPose.worldPos[0], width, height);
        if (i === 0) { this.ctx.moveTo(p.x, p.y); } else { this.ctx.lineTo(p.x, p.y); }
      }
      this.ctx.stroke();
    }

    const edges: Array<{ parent: number; child: number; z: number }> = [];
    for (let i = 0; i < (clip.boneCount || 0); i++) {
      const parent = clip.parents[i] ?? -1;
      if (parent >= 0 && projected[parent] && projected[i]) {
        edges.push({ parent, child: i, z: (projected[parent].z + projected[i].z) / 2 });
      }
    }
    edges.sort((a, b) => a.z - b.z);

    for (const edge of edges) {
      const childName = cleanBoneName(clip.bones[edge.child] || '');
      const attachment = isAttachmentBone(childName);
      const a = projected[edge.parent];
      const b = projected[edge.child];
      this.ctx.strokeStyle = attachment ? '#ce9178' : '#569cd6';
      this.ctx.lineWidth = attachment ? 5 : 3;
      this.ctx.lineCap = 'round';
      this.ctx.beginPath();
      this.ctx.moveTo(a.x, a.y);
      this.ctx.lineTo(b.x, b.y);
      this.ctx.stroke();
    }

    const joints = projected.map((p, i) => ({ p, i })).sort((a, b) => a.p.z - b.p.z);
    for (const item of joints) {
      const name = cleanBoneName(clip.bones[item.i] || '');
      const attachment = isAttachmentBone(name);
      const root = item.i === 0;
      this.ctx.fillStyle = attachment ? '#ce9178' : (root ? '#f44747' : '#dcdcaa');
      this.ctx.beginPath();
      this.ctx.arc(item.p.x, item.p.y, attachment ? 6 : (root ? 5 : 3.5), 0, Math.PI * 2);
      this.ctx.fill();
      if (attachment || root) {
        this.ctx.fillStyle = attachment ? '#ce9178' : '#f44747';
        this.ctx.font = '12px Consolas, monospace';
        this.ctx.fillText(name, item.p.x + 8, item.p.y - 6);
      }
    }

    this.drawAxis(width, height);
    this.drawOverlay(clip, width, pose.worldPos[0] || [0, 0, 0]);
  }
}
