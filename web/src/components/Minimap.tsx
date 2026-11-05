// The Map Builder's minimap: SOCOM II's own HUD radar (compass disc, view cone, blips from RUN\COMMON\HUD2_TXR.ZED)
// drawn over the map's tactical map (RUN\UI\MM<nn>_TXR.ZED) or a top-down render, turning with the view as the game's
// radar does. What to draw is decided in viewport/minimap.ts; this draws it on a canvas every frame the view moves.

import { useEffect, useMemo, useRef, useState } from 'react';
import type { MapHud } from '../webview/model-editor/asset-client';
import { headingOf, pinToRim, stepRange, toRadar, type Blip, type ImagePlacement } from '../viewport/minimap';

export type MinimapSource = 'game' | 'topdown';

export interface MinimapProps {
  hud: MapHud | null;
  blips: Blip[];
  /** The ground point the view is centred on, its facing and the camera's own position (MapViewport.viewHeading). */
  view: () => { x: number; z: number; forwardX: number; forwardZ: number; cameraX: number; cameraZ: number } | null;
  /** A top-down picture of the map and where it lies, made on demand (null while there is no scene). */
  topDown: { url: string; placement: ImagePlacement } | null;
  range: number;
  onRange: (range: number) => void;
  /** Click: move the view to this ground point. */
  onJump: (x: number, z: number) => void;
  onClose: () => void;
}

const SIZE = 220;
const RIM = 6;

function loadImage(src: string): Promise<HTMLImageElement> {
  return new Promise((resolve, reject) => {
    const img = new Image();
    img.onload = () => resolve(img);
    img.onerror = () => reject(new Error('image'));
    img.src = src;
  });
}

// White HUD art tinted to a colour (friendlies, spawns), cached per icon and colour.
function tinted(cache: Map<string, HTMLCanvasElement>, name: string, img: HTMLImageElement, color: string): HTMLCanvasElement {
  const key = `${name}|${color}`;
  let c = cache.get(key);
  if (!c) {
    c = document.createElement('canvas');
    c.width = img.width; c.height = img.height;
    const g = c.getContext('2d')!;
    g.drawImage(img, 0, 0);
    g.globalCompositeOperation = 'source-in';
    g.fillStyle = color;
    g.fillRect(0, 0, c.width, c.height);
    cache.set(key, c);
  }
  return c;
}

/** Vector stand-ins for HUD art a package does not have (a custom pack without HUD2_TXR.ZED). */
function drawStandIn(g: CanvasRenderingContext2D, icon: string, x: number, y: number, s: number, color: string): void {
  g.save();
  g.strokeStyle = color; g.fillStyle = color; g.lineWidth = 1.5;
  g.font = `bold ${Math.round(s * 0.7)}px sans-serif`; g.textAlign = 'center'; g.textBaseline = 'middle';
  const letter = icon === 'ret_able' ? 'A' : icon === 'ret_bravo' ? 'B' : icon === 'ret_friendly' ? 'F' : icon === 'ret_hostage' ? 'H'
    : icon.startsWith('ret_nav_') ? String.fromCharCode(66 + Number(icon.slice(8))) : '';
  if (icon.startsWith('ret_nav_')) { g.strokeRect(x - s / 2, y - s / 2, s, s); g.fillText(letter, x, y + 1); }
  else if (letter) { g.beginPath(); g.arc(x, y, s / 2, 0, Math.PI * 2); g.stroke(); g.fillText(letter, x, y + 1); }
  else if (icon === 'ret_objective') {
    g.beginPath();
    for (let i = 0; i < 10; i++) { const r = i % 2 ? s * 0.22 : s / 2, a = -Math.PI / 2 + i * Math.PI / 5; g.lineTo(x + r * Math.cos(a), y + r * Math.sin(a)); }
    g.closePath(); g.fill();
  } else if (icon === 'ret_extraction') {
    g.lineWidth = 3; g.beginPath(); g.moveTo(x - s / 3, y - s / 3); g.lineTo(x + s / 3, y + s / 3); g.moveTo(x + s / 3, y - s / 3); g.lineTo(x - s / 3, y + s / 3); g.stroke();
  } else if (icon === 'ret_threat') {
    g.fillStyle = '#d02020'; g.beginPath(); g.arc(x, y, s / 3, 0, Math.PI * 2); g.fill();
  } else {
    g.beginPath(); g.moveTo(x, y - s / 2); g.lineTo(x + s / 2, y + s / 2); g.lineTo(x - s / 2, y + s / 2); g.closePath(); g.fill();
  }
  g.restore();
}

export function Minimap({ hud, blips, view, topDown, range, onRange, onJump, onClose }: MinimapProps) {
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const [icons, setIcons] = useState<Record<string, HTMLImageElement>>({});
  const [tacmap, setTacmap] = useState<{ image: HTMLCanvasElement; placement: ImagePlacement } | null>(null);
  const [topImage, setTopImage] = useState<HTMLImageElement | null>(null);
  const [source, setSource] = useState<MinimapSource>('game');
  const [tip, setTip] = useState('');

  // The HUD icons and the tactical map (its two tiles side by side) as images.
  useEffect(() => {
    let live = true;
    setIcons({}); setTacmap(null);
    if (!hud) { return; }
    void Promise.all(Object.entries(hud.icons).map(async ([name, t]) => [name, await loadImage(`data:image/png;base64,${t.png}`)] as const))
      .then(list => { if (live) { setIcons(Object.fromEntries(list)); } }).catch(() => undefined);
    const t = hud.tacmap;
    if (t && t.world && t.tiles.every(Boolean)) {
      void Promise.all(t.tiles.map(tile => loadImage(`data:image/png;base64,${tile!.png}`))).then(tiles => {
        if (!live) { return; }
        const c = document.createElement('canvas');
        c.width = tiles.reduce((w, i) => w + i.width, 0); c.height = Math.max(...tiles.map(i => i.height));
        const g = c.getContext('2d')!;
        let x = 0;
        for (const i of tiles) { g.drawImage(i, x, 0); x += i.width; }
        const [minX, maxX, minZ, maxZ] = t.world!;
        setTacmap({ image: c, placement: { minX, maxX, minZ, maxZ } });
      }).catch(() => undefined);
    }
    return () => { live = false; };
  }, [hud]);

  useEffect(() => {
    let live = true;
    setTopImage(null);
    if (topDown) { void loadImage(topDown.url).then(i => { if (live) { setTopImage(i); } }).catch(() => undefined); }
    return () => { live = false; };
  }, [topDown?.url]);

  const background = useMemo(() => {
    if (source === 'game' && tacmap) { return { image: tacmap.image as CanvasImageSource, placement: tacmap.placement, game: true }; }
    if (topImage && topDown) { return { image: topImage as CanvasImageSource, placement: topDown.placement, game: false }; }
    if (tacmap) { return { image: tacmap.image as CanvasImageSource, placement: tacmap.placement, game: true }; }
    return null;
  }, [source, tacmap, topImage, topDown]);

  // Redraw whenever the view moves (or anything drawn changes).
  const state = useRef({ blips, background, icons, range });
  state.current = { blips, background, icons, range };
  const lastKey = useRef('');
  const tintCache = useRef(new Map<string, HTMLCanvasElement>());
  const placed = useRef<Array<{ b: Blip; x: number; y: number }>>([]);
  const lastView = useRef<{ x: number; z: number; heading: number } | null>(null);
  useEffect(() => { lastKey.current = ''; }, [blips, background, icons, range]);
  useEffect(() => {
    let frame = 0;
    const draw = () => {
      frame = requestAnimationFrame(draw);
      const canvas = canvasRef.current;
      const v = view();
      if (!canvas || !v) { return; }
      const heading = headingOf(v.forwardX, v.forwardZ);
      const key = `${v.x.toFixed(1)},${v.z.toFixed(1)},${v.cameraX.toFixed(1)},${v.cameraZ.toFixed(1)},${heading.toFixed(4)}`;
      if (key === lastKey.current) { return; }
      lastKey.current = key;
      lastView.current = { x: v.x, z: v.z, heading };
      const { blips, background, icons, range } = state.current;
      const dpr = window.devicePixelRatio || 1;
      if (canvas.width !== Math.round(SIZE * dpr)) { canvas.width = canvas.height = Math.round(SIZE * dpr); }
      const g = canvas.getContext('2d');
      if (!g) { return; }
      g.setTransform(dpr, 0, 0, dpr, 0, 0);
      g.clearRect(0, 0, SIZE, SIZE);
      const c = SIZE / 2, R = SIZE / 2 - RIM;

      // The map, turned so the view faces up.
      g.save();
      g.beginPath(); g.arc(c, c, R, 0, Math.PI * 2); g.clip();
      g.fillStyle = '#0b1016'; g.fillRect(0, 0, SIZE, SIZE);
      if (background) {
        const p = background.placement, s = R / range;
        g.save();
        g.translate(c, c); g.rotate(-heading); g.scale(s, s); g.translate(-v.x, -v.z);
        g.imageSmoothingEnabled = true;
        // A render from straight above comes out dark under the map's baked light; lift it to read like the tactical maps.
        if (!background.game) { g.filter = 'brightness(1.8) saturate(0.85)'; }
        g.drawImage(background.image, p.minX, p.minZ, p.maxX - p.minX, p.maxZ - p.minZ);
        g.restore();
      }
      // The game's radar disc (dark glass with N and ticks) laid over the map, its N kept pointing north.
      const disc = icons['compass_lo'];
      if (disc) {
        g.save(); g.translate(c, c); g.rotate(-heading); g.globalAlpha = 0.55;
        g.drawImage(disc, -R - RIM / 2, -R - RIM / 2, 2 * R + RIM, 2 * R + RIM);
        g.restore();
      } else {
        g.fillStyle = 'rgba(10, 30, 45, 0.35)'; g.fillRect(0, 0, SIZE, SIZE);
      }
      // The view cone (FOV.tif: apex at the bottom middle).
      const fov = icons['FOV'];
      if (fov) { g.globalAlpha = 0.9; g.drawImage(fov, c - R * 0.45, c - R * 0.9, R * 0.9, R * 0.9); g.globalAlpha = 1; }
      g.restore();

      // Rim, and N on it when the disc art is missing.
      g.strokeStyle = 'rgba(160, 200, 220, 0.75)'; g.lineWidth = 2;
      g.beginPath(); g.arc(c, c, R, 0, Math.PI * 2); g.stroke();
      if (!disc) {
        const [nx, ny] = toRadar(v.x, v.z - range, v.x, v.z, heading, range, R - 9);
        g.fillStyle = '#cfe6f0'; g.font = 'bold 11px sans-serif'; g.textAlign = 'center'; g.textBaseline = 'middle';
        g.fillText('N', c + nx, c + ny);
      }

      // Blips stay upright; out-of-range objectives and nav points sit on the rim.
      const order: Record<string, number> = { action: 0, spawn: 1, enemy: 2, hostage: 3, friendly: 4, nav: 5, objective: 6, extraction: 7 };
      const shown: Array<{ b: Blip; x: number; y: number }> = [];
      for (const b of [...blips].sort((a, z) => order[a.kind] - order[z.kind])) {
        let [x, y] = toRadar(b.x, b.z, v.x, v.z, heading, range, R);
        const edge = pinToRim(x, y, R, 8);
        if (edge) { if (!b.pinToRim) { continue; } [x, y] = edge; }
        x += c; y += c;
        g.globalAlpha = b.dim ? 0.55 : 1;
        if (!b.icon) {
          g.fillStyle = b.tint ?? '#ccc'; g.strokeStyle = '#000'; g.lineWidth = 1;
          g.fillRect(x - 3, y - 3, 6, 6); g.strokeRect(x - 3, y - 3, 6, 6);
        } else {
          const s = b.kind === 'spawn' ? 9 : b.kind === 'enemy' ? 11 : b.kind === 'nav' || b.kind === 'objective' || b.kind === 'extraction' ? 15 : 13;
          const img = icons[b.icon];
          if (img) { g.drawImage(b.tint ? tinted(tintCache.current, b.icon, img, b.tint) : img, x - s / 2, y - s / 2, s, s); }
          else { drawStandIn(g, b.icon, x, y, s, b.tint ?? '#fff'); }
        }
        g.globalAlpha = 1;
        shown.push({ b, x, y });
      }
      placed.current = shown;

      // The camera itself, when it stands away from what it looks at (orbiting): the HUD's small compass mark, on the
      // rim when it is out of range.
      if (Math.hypot(v.cameraX - v.x, v.cameraZ - v.z) > range * 0.04) {
        let [x, y] = toRadar(v.cameraX, v.cameraZ, v.x, v.z, heading, range, R);
        const edge = pinToRim(x, y, R, 7);
        if (edge) { [x, y] = edge; }
        const mark = icons['small_compass'];
        if (mark) { g.drawImage(tinted(tintCache.current, 'small_compass', mark, '#9fd8ff'), c + x - 6, c + y - 6, 12, 12); }
        else { g.strokeStyle = '#9fd8ff'; g.beginPath(); g.arc(c + x, c + y, 5, 0, Math.PI * 2); g.stroke(); }
      }

      // The view's centre (the player in walk mode): a white arrow pointing the way the view faces.
      const arrow = icons['ret_triangle'];
      if (arrow) { g.drawImage(arrow, c - 6, c - 7, 12, 12); }
      else { drawStandIn(g, 'ret_triangle', c, c, 11, '#fff'); }
    };
    frame = requestAnimationFrame(draw);
    return () => cancelAnimationFrame(frame);
  }, [view]);

  const hit = (e: React.MouseEvent<HTMLCanvasElement>) => {
    const r = e.currentTarget.getBoundingClientRect();
    const x = (e.clientX - r.left) * SIZE / r.width, y = (e.clientY - r.top) * SIZE / r.height;
    return { x, y, near: placed.current.reduce<{ b: Blip; d: number } | null>((best, p) => {
      const d = Math.hypot(p.x - x, p.y - y);
      return d < 9 && (!best || d < best.d) ? { b: p.b, d } : best;
    }, null) };
  };

  const hasGame = !!tacmap;
  const label = background ? (background.game ? 'Tactical map' : 'Top-down') : 'No map picture';
  return (
    <div className="mp-minimap" aria-label="Minimap">
      <canvas ref={canvasRef} style={{ width: SIZE, height: SIZE }} title={tip}
        onWheel={e => { e.preventDefault(); onRange(stepRange(range, e.deltaY < 0)); }}
        onMouseMove={e => { const h = hit(e); setTip(h.near ? h.near.b.label : 'Click to move the view here · wheel zooms'); }}
        onClick={e => {
          const v = lastView.current;
          if (!v) { return; }
          const h = hit(e);
          const dx = h.x - SIZE / 2, dy = h.y - SIZE / 2, s = range / (SIZE / 2 - RIM);
          // Back from radar to world: undo the turn.
          const cs = Math.cos(v.heading), sn = Math.sin(v.heading);
          onJump(v.x + (dx * cs - dy * sn) * s, v.z + (dx * sn + dy * cs) * s);
        }} />
      <div className="mp-minimap-bar">
        <button type="button" disabled={!hasGame} title={hasGame ? 'Switch between the game\'s tactical map and a top-down render' : 'This map has no placed tactical map: top-down render'}
          onClick={() => setSource(s => s === 'game' ? 'topdown' : 'game')}>{label}</button>
        <button type="button" title="Zoom in" onClick={() => onRange(stepRange(range, true))}>+</button>
        <button type="button" title="Zoom out" onClick={() => onRange(stepRange(range, false))}>−</button>
        <span className="mp-minimap-range" title="Centre to rim">{Math.round(range / 10.5)} m</span>
        <button type="button" title="Hide the minimap (N)" onClick={onClose}>×</button>
      </div>
    </div>
  );
}
