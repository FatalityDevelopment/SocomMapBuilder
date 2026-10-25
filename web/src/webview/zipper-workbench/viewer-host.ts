// The Asset Viewer's host: what CodeDesigner's VS Code extension did for this page (ZipperWorkbenchProvider), done in
// the browser against the Map Builder server. The page posts the same messages it always did ({ type: 'readNode' … });
// replies come back through deliver() in the same shapes (binary node payloads as ArrayBuffers).

import { command, commandRaw } from '../../api/client';
import { PACKAGE_FILTERS, host } from '../../shared';

interface WireNode { index: number; name: string; type: number; data: string }
interface BinaryNode { index: number; name: string; type: number; data: ArrayBuffer }

function base64ToArrayBuffer(base64: string): ArrayBuffer {
  const bin = atob(base64 ?? '');
  const out = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) { out[i] = bin.charCodeAt(i); }
  return out.buffer;
}

function arrayBufferToBase64(buf: ArrayBuffer): string {
  const bytes = new Uint8Array(buf);
  let bin = '';
  for (let i = 0; i < bytes.length; i += 0x8000) { bin += String.fromCharCode(...bytes.subarray(i, i + 0x8000)); }
  return btoa(bin);
}

const toBinaryNodes = (nodes: WireNode[]): BinaryNode[] => nodes.map(n => ({ index: n.index, name: n.name, type: n.type, data: base64ToArrayBuffer(n.data) }));
const toBase64Nodes = (nodes: BinaryNode[]): WireNode[] => nodes.map(n => ({ index: n.index, name: n.name, type: n.type, data: arrayBufferToBase64(n.data) }));

const dirOf = (p: string) => p.replace(/[\\/][^\\/]*$/, '');
const baseOf = (p: string) => p.split(/[\\/]/).pop() ?? p;
const extOf = (p: string) => (/\.[^.\\/]+$/.exec(p)?.[0] ?? '').toLowerCase();

export class ViewerHost {
  // The last packages loaded, so reopening one (or a child archive of it) needs no new fetch.
  private readonly packageCache = new Map<string, { nodes: BinaryNode[]; loadedAt: number; preloadToken?: string }>();
  private lastInspectPath = '';

  constructor(private readonly deliver: (msg: Record<string, unknown>) => void) {}

  postMessage(msg: Record<string, unknown>): void {
    void this.handle(msg);
  }

  private async handle(msg: Record<string, unknown>): Promise<void> {
    const path = typeof msg['path'] === 'string' ? msg['path'] : undefined;
    switch (msg['type'] as string | undefined) {
      case 'ready':
        this.deliver({ type: 'zipperReady' });
        if (this.lastInspectPath) { await this.inspectPath(this.lastInspectPath); }
        break;
      case 'openFile': {
        const picked = await host.browse({ mode: 'open', title: 'Open in the Asset Viewer', filters: [PACKAGE_FILTERS[0]], start: this.lastInspectPath || undefined, key: 'viewer', submitLabel: 'Open' });
        if (picked) { await this.inspectPath(picked); }
        break;
      }
      case 'refresh':
        await this.inspectPath(path);
        break;
      case 'packageEdit':
        await this.packageEdit(path, msg['ops'] as unknown[], String(msg['label'] ?? 'Edit'));
        break;
      case 'extractAll':
        await this.extractAll(path);
        break;
      case 'extractEntry':
        await this.extractEntry(path, msg['entryIndex'] as number | undefined, msg['entryName'] as string | undefined);
        break;
      case 'revealPath':
        if (path) {
          try { await navigator.clipboard.writeText(path); this.status(`Copied ${path}`, false); } catch { this.status(path, false); }
        }
        break;
      case 'readNode':
        await this.readNode(path, msg['entryIndex'] as number | undefined);
        break;
      case 'readPaletteFile':
        await this.readPaletteFile(path);
        break;
      case 'readCompanionFiles':
        await this.readCompanionFiles(path);
        break;
      case 'inspectRdr':
        await this.reply('rdrData', 'rdrError', { entryName: msg['entryName'] },
          () => command('ZIPPER_INSPECT_RDR', { InputPath: path, ZipperSection: msg['entryName'] as string | undefined }));
        break;
      case 'inspectRdrBytes':
        await this.reply('rdrData', 'rdrError', { entryName: msg['entryName'] },
          () => command('ZIPPER_INSPECT_RDR_BYTES', { Data: msg['base64'] as string, ZipperSection: msg['entryName'] as string | undefined }));
        break;
      case 'inspectMps':
        await this.reply('mpsData', 'mpsError', {}, () => command('ZIPPER_INSPECT_MPS', { InputPath: path }));
        break;
      case 'loadZdbPackage':
        await this.loadZdbPackage(path, false);
        break;
      case 'reloadZdbPackage':
        await this.loadZdbPackage(path, true, msg['encoding'] === 'base64' ? 'base64' : 'binary');
        break;
      case 'loadArchiveNodes':
        if (!path) { break; }
        try {
          const nodes = toBinaryNodes(await command<WireNode[]>('ZIPPER_READ_ALL_NODES', { InputPath: path }));
          this.deliver({ type: 'archiveNodes', path, nodes });
        } catch (err) {
          this.deliver({ type: 'archiveNodesError', path, message: errText(err) });
        }
        break;
      case 'zipperLog':
        if (typeof msg['text'] === 'string' && msg['text']) { host.log(msg['text']); }
        break;
      case 'openModelEditor':
        host.open('models', String(msg['path']), { archive: String(msg['archive']), name: String(msg['name']) });
        break;
      case 'saveExportDecrypted':
      case 'saveCraft':
      case 'apacheDecryptSection':
      case 'apacheBuildPatch':
        this.deliver({ type: 'zipperError', message: 'Save crafting and APACHE00 patching are not part of Map Builder.' });
        break;
    }
  }

  private status(message: string, busy: boolean): void { this.deliver({ type: 'zipperStatus', message, busy }); }

  private async reply(ok: string, fail: string, extra: Record<string, unknown>, run: () => Promise<unknown>): Promise<void> {
    try {
      this.deliver({ type: ok, ...extra, result: await run() });
    } catch (err) {
      this.deliver({ type: fail, ...extra, message: errText(err) });
    }
  }

  // Import / Replace / Remove on the package's files or an archive's records (PACKAGE_EDIT): saved where the user
  // says (the same file by default, which keeps a .bak), then reopened.
  private async packageEdit(filePath: string | undefined, ops: unknown[], label: string): Promise<void> {
    if (!filePath) { return; }
    const ext = (extOf(filePath) || '.zdb').slice(1);
    const target = await host.browse({
      mode: 'save',
      title: `${label}: save as`,
      filters: [{ label: `${ext.toUpperCase()} file`, extensions: [ext] }],
      start: dirOf(filePath),
      fileName: baseOf(filePath),
      key: 'save',
      submitLabel: 'Save',
    });
    if (!target) { return; }
    try {
      this.status(`${label}…`, true);
      const r = await command<{ written: string; added: string[]; replaced: string[]; removed: string[] }>('PACKAGE_EDIT', { InputPath: filePath, OutputPath: target, Data: JSON.stringify(ops) });
      this.packageCache.delete(filePath);
      this.packageCache.delete(r.written);
      await this.inspectPath(r.written);
      const parts = [
        r.added.length ? `added ${r.added.join(', ')}` : '',
        r.replaced.length ? `replaced ${r.replaced.join(', ')}` : '',
        r.removed.length ? `removed ${r.removed.join(', ')}` : '',
      ].filter(Boolean).join('; ');
      this.deliver({ type: 'packageEdited', message: `${label}: ${parts || 'no change'} → ${r.written}` });
    } catch (err) {
      this.deliver({ type: 'zipperError', message: errText(err) });
    }
  }

  private async inspectPath(filePath: string | undefined): Promise<void> {
    const clean = filePath?.trim();
    if (!clean) { return; }
    try {
      this.status('Inspecting file...', true);
      const result = await command('ZIPPER_INSPECT', { InputPath: clean });
      this.lastInspectPath = clean;
      this.deliver({ type: 'zipperLoaded', result });
      const ext = extOf(clean);
      if (ext === '.zar' || ext === '.zed') {
        const parent = this.findParentPackage(clean);
        if (parent) {
          this.deliver({ type: 'zdbPackage', path: parent.path, nodes: parent.nodes, fromCache: true, fromParentPackage: true, preloadToken: parent.preloadToken });
        }
      }
    } catch (err) {
      this.deliver({ type: 'zipperError', message: errText(err) });
    }
  }

  private async readNode(filePath: string | undefined, entryIndex: number | undefined): Promise<void> {
    if (!filePath || entryIndex === undefined) { return; }
    try {
      const data = await commandRaw('ZIPPER_READ_NODE', { InputPath: filePath, ZipperEntryIndex: entryIndex });
      this.deliver({ type: 'nodeData', entryIndex, data: base64ToArrayBuffer(data) });
    } catch (err) {
      this.deliver({ type: 'nodeError', entryIndex, message: errText(err) });
    }
  }

  private async readPaletteFile(currentPath: string | undefined): Promise<void> {
    if (!currentPath) { return; }
    try {
      const { path: palPath } = await command<{ path: string | null }>('VIEWER_PALETTE', { InputPath: currentPath });
      if (!palPath) { this.deliver({ type: 'paletteFile', found: false }); return; }
      const nodes = await command<WireNode[]>('ZIPPER_READ_ALL_NODES', { InputPath: palPath });
      this.deliver({ type: 'paletteFile', found: true, path: palPath, nodes });
    } catch (err) {
      this.deliver({ type: 'paletteFile', found: false, message: errText(err) });
    }
  }

  private async readCompanionFiles(currentPath: string | undefined): Promise<void> {
    if (!currentPath) { return; }
    try {
      const companions = await command<Record<string, string>>('VIEWER_COMPANIONS', { InputPath: currentPath });
      const out: Record<string, { path: string; nodes: WireNode[] }> = {};
      for (const [kind, p] of Object.entries(companions)) {
        try {
          out[kind] = { path: p, nodes: await command<WireNode[]>('ZIPPER_READ_ALL_NODES', { InputPath: p }) };
        } catch (err) {
          host.log(`[viewer] companion ${kind} load failed: ${errText(err)}`);
        }
      }
      this.deliver({ type: 'companionFiles', companions: out });
    } catch (err) {
      this.deliver({ type: 'companionFiles', companions: {}, message: errText(err) });
    }
  }

  private async loadZdbPackage(filePath: string | undefined, force: boolean, encoding: 'binary' | 'base64' = 'binary'): Promise<void> {
    if (!filePath) { return; }
    const wire = (nodes: BinaryNode[]) => encoding === 'base64' ? toBase64Nodes(nodes) : nodes;
    if (!force) {
      const cached = this.packageCache.get(filePath);
      if (cached) {
        this.deliver({ type: 'zdbPackage', path: filePath, nodes: wire(cached.nodes), fromCache: true, preloadToken: cached.preloadToken });
        return;
      }
    }
    try {
      // A loose map folder (WORL_MDL.ZED + its _TXR / _PAL family): the server hands over every archive of it.
      const loose = await command<{ loose: boolean; nodes?: WireNode[] }>('VIEWER_LOOSE_PACKAGE', { InputPath: filePath });
      if (loose.loose && loose.nodes) {
        const nodes = toBinaryNodes(loose.nodes);
        this.packageCache.set(filePath, { nodes, loadedAt: Date.now() });
        this.deliver({
          type: 'zdbPackage', path: filePath, nodes: wire(nodes), fromCache: false, looseMapPackage: true,
          preloadStats: { preloadToken: '', resourceCount: nodes.length, totalBytes: nodes.reduce((sum, n) => sum + n.data.byteLength, 0), cacheHit: false },
        });
        return;
      }
      const preload = await command<{ preloadToken: string; resourceCount: number; totalBytes: number; cacheHit: boolean }>('ZIPPER_PRELOAD_MAP_PACKAGE', { InputPath: filePath });
      const nodes = toBinaryNodes(await command<WireNode[]>('ZIPPER_READ_ALL_NODES', { InputPath: filePath }));
      this.packageCache.set(filePath, { nodes, loadedAt: Date.now(), preloadToken: preload.preloadToken });
      // Keep the last four packages.
      if (this.packageCache.size > 4) {
        const oldest = [...this.packageCache.entries()].sort((a, b) => a[1].loadedAt - b[1].loadedAt)[0];
        if (oldest) {
          if (oldest[1].preloadToken) { void command('ZIPPER_RELEASE_PRELOAD', { ZipperPreloadToken: oldest[1].preloadToken }).catch(() => undefined); }
          this.packageCache.delete(oldest[0]);
        }
      }
      this.deliver({ type: 'zdbPackage', path: filePath, nodes: wire(nodes), fromCache: false, preloadToken: preload.preloadToken, preloadStats: preload });
    } catch (err) {
      this.deliver({ type: 'zdbPackageError', path: filePath, message: errText(err) });
    }
  }

  // A child archive (.zar / .zed) of a package already loaded: reuse that package so shared resources resolve.
  private findParentPackage(filePath: string): { path: string; nodes: BinaryNode[]; preloadToken?: string } | undefined {
    const direct = this.packageCache.get(filePath);
    if (direct) { return { path: filePath, nodes: direct.nodes, preloadToken: direct.preloadToken }; }
    const dir = dirOf(filePath);
    const zdbs = [...this.packageCache.entries()].filter(([p]) => p.toLowerCase().endsWith('.zdb')).sort((a, b) => b[1].loadedAt - a[1].loadedAt);
    const sameDir = zdbs.find(([p]) => dirOf(p) === dir);
    if (sameDir) { return { path: sameDir[0], nodes: sameDir[1].nodes, preloadToken: sameDir[1].preloadToken }; }
    const stem = baseOf(filePath).replace(/\.[^.]+$/, '').toLowerCase();
    const byStem = zdbs.find(([p]) => baseOf(p).replace(/\.zdb$/i, '').toLowerCase().includes(stem.slice(0, Math.min(stem.length, 6))));
    return byStem ? { path: byStem[0], nodes: byStem[1].nodes, preloadToken: byStem[1].preloadToken } : undefined;
  }

  private async extractAll(filePath: string | undefined): Promise<void> {
    if (!filePath) { return; }
    const picked = await host.pickSavePath('folder', dirOf(filePath));
    if (!picked.path) { return; }
    try {
      this.status('Extracting entries...', true);
      const result = await command('ZIPPER_EXTRACT_ALL', { InputPath: filePath, OutputDirectory: picked.path });
      this.deliver({ type: 'zipperOperation', message: 'Extracted entries.', result });
    } catch (err) {
      this.deliver({ type: 'zipperError', message: errText(err) });
    }
  }

  private async extractEntry(filePath: string | undefined, entryIndex: number | undefined, entryName: string | undefined): Promise<void> {
    if (!filePath || entryIndex === undefined) { return; }
    const safe = (entryName ?? `entry_${entryIndex}`).replace(/[<>:"/\\|?*\x00-\x1f]/g, '_') || 'entry.bin';
    const target = await host.browse({ mode: 'save', title: `Extract ${entryName ?? 'entry'}`, start: dirOf(filePath), fileName: safe, key: 'extract', submitLabel: 'Extract' });
    if (!target) { return; }
    try {
      this.status('Extracting entry...', true);
      const result = await command('ZIPPER_EXTRACT_ENTRY', { InputPath: filePath, ZipperEntryIndex: entryIndex, OutputPath: target });
      this.deliver({ type: 'zipperOperation', message: 'Extracted entry.', result });
    } catch (err) {
      this.deliver({ type: 'zipperError', message: errText(err) });
    }
  }
}

function errText(err: unknown): string {
  return err instanceof Error ? err.message : String(err);
}
