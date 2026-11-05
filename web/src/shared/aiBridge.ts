// The page's side of the MCP server: the server's event stream (/api/events) says what the AI is doing, and the pages
// follow it. aiLog feeds the activity panel; aiChange asks a page to open, reload or frame what an edit touched (only
// while "Follow AI" is on, unless the AI asked for it with ui_show / ui_focus); aiView changes shading, lighting or the
// camera. The server's requests (a screenshot, the app state) are answered through /api/ui/reply.

import { signal } from './signals';
import { activePage, type PageId } from './host';
import { viewBackdrop } from './viewSettings';
import { ViewportCore, type Backdrop, type Lighting, type Shading, type ViewPreset } from '../webview/zipper-core/viewport-core';

export interface AiActivity {
  id: string;
  tool: string;
  label: string;
  reason?: string;
  phase: 'start' | 'done' | 'error';
  summary?: string;
  ms?: number;
  readOnly?: boolean;
  at: number;
}

/** Where the Asset Viewer goes: a place to list (open) or an item to select (path + inner), a palette or texture of the
 *  package (archive + palette / texture), and what to mark there (an RDR atom, a GEO record, a ZANIM instruction, an AI map). */
export interface ViewerFocus {
  path?: string; inner?: number[]; open?: boolean; archive?: string | null; palette?: number | null; texture?: string | null;
  atom?: number; record?: number; sequence?: number; offset?: number | null; map?: number;
}

export interface AiFocus { geoIndex?: number | null; instance?: string | null; clutterIndex?: number | null; point?: string | null; viewer?: ViewerFocus | null; clip?: string | null }

export interface AiChange {
  seq: number;
  // The page that should show it (null: reload wherever this package is open, switch nothing).
  page: PageId | null;
  path: string | null;
  model?: { archive: string; name: string } | null;
  focus?: AiFocus | null;
  description?: string | null;
  // The session changed (reload); otherwise the AI only asked to show something.
  edited: boolean;
}

// orbit: [yaw, pitch] degrees about the orbit target, applied after view / frame; zoom: distance factor (< 1 closer).
export interface AiView { seq: number; shading?: Shading; lighting?: Lighting; view?: ViewPreset; frame?: 'selected' | 'all'; orbit?: [number, number]; zoom?: number }

// A ui_view orbit / zoom on the 3D view on screen (each page calls it after its own preset and framing).
export function applyAiCamera(request: AiView): void {
  if (!request.orbit && !request.zoom) { return; }
  const vp = [...ViewportCore.live].find(v => v.visible);
  if (request.orbit) { vp?.orbitBy(request.orbit[0], request.orbit[1]); }
  if (request.zoom && request.zoom > 0) { vp?.zoomBy(request.zoom); }
}

export const aiConnected = signal(false);
export const aiLog = signal<AiActivity[]>([]);
export const aiChange = signal<AiChange | null>(null);
export const aiView = signal<AiView | null>(null);
export const aiPanelOpen = signal(false);

const FOLLOW = 'mapbuilder.aiFollow';
function storedFollow(): boolean {
  try { return localStorage.getItem(FOLLOW) !== 'off'; } catch { return true; }
}
export const aiFollow = signal(storedFollow());
export function setAiFollow(on: boolean): void {
  aiFollow.set(on);
  try { localStorage.setItem(FOLLOW, on ? 'on' : 'off'); } catch { /* storage unavailable */ }
}

// What each page reports for ui_state (registered by the page while mounted).
const stateProviders = new Map<PageId, () => unknown>();
export function provideAiState(page: PageId, fn: () => unknown): () => void {
  stateProviders.set(page, fn);
  return () => { if (stateProviders.get(page) === fn) { stateProviders.delete(page); } };
}

export const samePath = (a: string | null | undefined, b: string | null | undefined) => !!a && !!b && a.toLowerCase() === b.toLowerCase();

let seq = 0;
const PAGES: PageId[] = ['map', 'models', 'animations', 'viewer'];
const asPage = (p: unknown): PageId | null => PAGES.includes(p as PageId) ? p as PageId : null;

type ServerEvent = Record<string, unknown> & { type: string };

function onEvent(e: ServerEvent): void {
  switch (e.type) {
    case 'activity': {
      const entry: AiActivity = {
        id: String(e['id']), tool: String(e['tool']), label: String(e['label'] ?? e['tool']), reason: e['reason'] as string | undefined,
        phase: e['phase'] as AiActivity['phase'], summary: e['summary'] as string | undefined, ms: e['ms'] as number | undefined,
        readOnly: e['readOnly'] as boolean | undefined, at: Date.now(),
      };
      aiLog.update(log => {
        const i = log.findIndex(x => x.id === entry.id);
        if (i < 0) { return [...log.slice(-49), entry]; }
        const next = [...log];
        next[i] = { ...log[i], ...entry, readOnly: log[i].readOnly };
        return next;
      });
      if (entry.phase === 'start') { aiPanelOpen.set(true); }
      return;
    }
    case 'show':
    case 'changed': {
      const page = asPage(e['page']);
      const follow = aiFollow() || e['force'] === true;
      if (follow && page) { activePage.set(page); }
      aiChange.set({
        seq: ++seq,
        page: follow ? page : null,
        path: (e['path'] as string | null) ?? null,
        model: e['model'] as AiChange['model'],
        focus: e['focus'] as AiFocus | null,
        description: e['description'] as string | null,
        edited: e.type === 'changed',
      });
      return;
    }
    case 'view': {
      if (e['backdrop'] === 'dark' || e['backdrop'] === 'blueprint') { viewBackdrop.set(e['backdrop'] as Backdrop); }
      aiView.set({
        seq: ++seq,
        shading: e['shading'] as Shading | undefined, lighting: e['lighting'] as Lighting | undefined,
        view: e['view'] as ViewPreset | undefined, frame: e['frame'] as AiView['frame'],
        orbit: Array.isArray(e['orbit']) && e['orbit'].length === 2 ? e['orbit'] as [number, number] : undefined,
        zoom: typeof e['zoom'] === 'number' ? e['zoom'] : undefined,
      });
      return;
    }
    case 'request':
      void answer(String(e['id']), String(e['kind']), (e['args'] ?? {}) as Record<string, unknown>);
      return;
  }
}

async function answer(id: string, kind: string, args: Record<string, unknown>): Promise<void> {
  let body: Record<string, unknown>;
  try {
    if (kind === 'screenshot') { body = { id, data: await screenshot(Number(args['maxSize']) || 1280) }; }
    else if (kind === 'state') { body = { id, data: appState() }; }
    else { body = { id, error: `Unknown request '${kind}'` }; }
  } catch (err) {
    body = { id, error: err instanceof Error ? err.message : String(err) };
  }
  // Every open tab answers; the server takes the first, the visible one first (hidden tabs wait a little).
  if (document.hidden) { await new Promise(r => setTimeout(r, 400)); }
  await fetch('/api/ui/reply', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) }).catch(() => undefined);
}

function appState(): unknown {
  const pages: Record<string, unknown> = {};
  for (const [page, fn] of stateProviders) {
    try { pages[page] = fn(); } catch (err) { pages[page] = { error: String(err) }; }
  }
  return { activePage: activePage(), follow: aiFollow(), backdrop: viewBackdrop(), tabHidden: document.hidden, pages };
}

// The 3D view on screen as a PNG (scaled so its longest side is at most maxSize).
async function screenshot(maxSize: number): Promise<{ png: string; page: PageId; width: number; height: number }> {
  let url: string | null = null;
  for (const vp of ViewportCore.live) { url = vp.capture(); if (url) { break; } }
  if (!url) {
    // Pages without a shared viewport (the Asset Viewer): its largest visible canvas.
    const canvases = [...document.querySelectorAll<HTMLCanvasElement>('.mb-page.on canvas')].filter(c => c.offsetParent !== null && c.width > 0);
    canvases.sort((a, b) => b.width * b.height - a.width * a.height);
    url = canvases[0]?.toDataURL('image/png') ?? null;
  }
  if (!url) { throw new Error('No 3D view is on screen.'); }
  const img = new Image();
  await new Promise<void>((resolve, reject) => { img.onload = () => resolve(); img.onerror = () => reject(new Error('Could not read the view')); img.src = url!; });
  const k = Math.min(1, maxSize / Math.max(img.width, img.height));
  const c = document.createElement('canvas');
  c.width = Math.max(1, Math.round(img.width * k));
  c.height = Math.max(1, Math.round(img.height * k));
  const g = c.getContext('2d')!;
  g.drawImage(img, 0, 0, c.width, c.height);
  // Canvas overlays drawn over the 3D view (the Map Builder's minimap), where they sit on screen.
  const view = [...document.querySelectorAll<HTMLCanvasElement>('.me-viewport canvas')].find(v => v.offsetParent !== null);
  if (view) {
    const at = view.getBoundingClientRect();
    const sx = c.width / Math.max(1, at.width), sy = c.height / Math.max(1, at.height);
    for (const overlay of document.querySelectorAll<HTMLCanvasElement>('.mp-minimap canvas')) {
      const r = overlay.getBoundingClientRect();
      if (overlay.offsetParent !== null && r.width > 0) { g.drawImage(overlay, (r.left - at.left) * sx, (r.top - at.top) * sy, r.width * sx, r.height * sy); }
    }
  }
  return { png: c.toDataURL('image/png').replace(/^data:image\/png;base64,/, ''), page: activePage(), width: c.width, height: c.height };
}

let source: EventSource | null = null;

// Subscribes once; EventSource reconnects by itself when the server restarts.
export function connectAi(): void {
  if (source || typeof EventSource === 'undefined') { return; }
  source = new EventSource('/api/events');
  source.onopen = () => aiConnected.set(true);
  source.onerror = () => aiConnected.set(false);
  source.onmessage = msg => {
    try { onEvent(JSON.parse(msg.data) as ServerEvent); } catch { /* not ours */ }
  };
}
