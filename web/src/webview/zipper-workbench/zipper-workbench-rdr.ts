// Asset Viewer RDR screens: the record tree, the fallback string scan and the textures a screen names.

import { parseTexture } from './zed-texture-parser';
import type { ViewerPageLogic } from './zipper-workbench-page';
import { textureToDataUrl } from './zipper-workbench-textures';
import type { RdrTreeNode } from './zipper-workbench-types';

export const rdrMethods = {
  computeRdrTreeRoots(this: ViewerPageLogic): RdrTreeNode[] {
    const r = this.rdr();
    if (!r || r.fallback) { return []; }
    return this.buildRdrTreeRoots(r.root);
  },

  computeRdrVisibleRows(this: ViewerPageLogic): RdrTreeNode[] {
    const roots = this.rdrTreeRoots();
    const expanded = this.rdrExpanded();
    const out: RdrTreeNode[] = [];
    const walk = (nodes: RdrTreeNode[]) => {
      for (const n of nodes) {
        out.push(n);
        if (n.children && expanded.has(n.path)) { walk(n.children); }
      }
    };
    walk(roots);
    return out;
  },

  computeRdrTextureThumbnails(this: ViewerPageLogic): Array<{ name: string; dataUrl: string; width: number; height: number }> {
    const lists = this.rdrFallbackLists();
    if (!lists || lists.textureRefs.length === 0) { return []; }
    const sources = this.companionTextures();
    if (!sources || sources.size === 0) { return []; }
    const palettes = this.palettes();
    const out: Array<{ name: string; dataUrl: string; width: number; height: number }> = [];
    for (const ref of lists.textureRefs.slice(0, 24)) {
      const key = ref.toLowerCase();
      const bytes = sources.get(key);
      if (!bytes) { continue; }
      try {
        const tex = parseTexture(bytes, palettes, key);
        out.push({ name: ref, dataUrl: textureToDataUrl(tex), width: tex.header.width, height: tex.header.height });
      } catch { /* skip undecodable */ }
    }
    return out;
  },

  // ------------------------------------------------------------------
  // RDR tree — turns the shaped JSON-like root into a flat list of nodes
  // with stable paths so we can collapse/expand by path. We render only
  // the visible-under-expand subset via rdrVisibleRows().
  // ------------------------------------------------------------------
  buildRdrTreeRoots(this: ViewerPageLogic, root: unknown): RdrTreeNode[] {
    return this.shapeToTree(root, '$', 0, null);
  },

  shapeToTree(this: ViewerPageLogic, value: unknown, path: string, depth: number, name: string | null): RdrTreeNode[] {
    const nameLabel = name ?? (depth === 0 ? 'root' : '');
    if (value === null || value === undefined) {
      return [{ path, name: nameLabel, kind: 'leaf', value: 'null', depth }];
    }
    if (typeof value === 'string') {
      return [{ path, name: nameLabel, kind: 'string', value, depth }];
    }
    if (typeof value === 'number') {
      const kind: RdrTreeNode['kind'] = Number.isInteger(value) ? 'int' : 'float';
      return [{ path, name: nameLabel, kind, value, depth }];
    }
    if (Array.isArray(value)) {
      const children: RdrTreeNode[] = [];
      for (let i = 0; i < value.length; i++) {
        children.push(...this.shapeToTree(value[i], `${path}.${i}`, depth + 1, `[${i}]`));
      }
      return [{ path, name: nameLabel, kind: 'array', value: `${value.length}`, children, depth }];
    }
    if (typeof value === 'object') {
      const obj = value as Record<string, unknown>;
      const children: RdrTreeNode[] = [];
      for (const [k, v] of Object.entries(obj)) {
        children.push(...this.shapeToTree(v, `${path}.${k}`, depth + 1, k));
      }
      return [{ path, name: nameLabel, kind: 'object', value: `${children.length}`, children, depth }];
    }
    return [{ path, name: nameLabel, kind: 'leaf', value: String(value), depth }];
  },

  toggleRdrNode(this: ViewerPageLogic, path: string): void {
    const next = new Set(this.rdrExpanded());
    if (next.has(path)) { next.delete(path); } else { next.add(path); }
    this.rdrExpanded.set(next);
  },

  isRdrExpanded(this: ViewerPageLogic, path: string): boolean { return this.rdrExpanded().has(path); },

  rdrIsExpandable(this: ViewerPageLogic, node: RdrTreeNode): boolean { return !!(node.children && node.children.length > 0); },

  // Lists rendered when RDR fell back to printable-string scan. The backend
  // shapes `root` as { strings, textureRefs, fileRefs, modelRefs }.
  rdrFallbackLists(this: ViewerPageLogic): { strings: string[]; textureRefs: string[]; fileRefs: string[]; modelRefs: string[] } | null {
    const r = this.rdr();
    if (!r || !r.fallback) { return null; }
    const root = r.root as Record<string, unknown> | null;
    if (!root) { return null; }
    const grab = (k: string): string[] => Array.isArray(root[k]) ? (root[k] as string[]) : [];
    return {
      strings: grab('strings'),
      textureRefs: grab('textureRefs'),
      fileRefs: grab('fileRefs'),
      modelRefs: grab('modelRefs'),
    };
  },

  // Loads companion textures for the currently open .rdr entry. When the user
  // opens READERC.ZAR or READERX.ZAR, this finds the sibling _TXR / _PAL files
  // and sends them to the provider, which responds via the existing
  // `companionFiles` message. The RDR texture thumbnail grid then populates.
  loadRdrCompanionTextures(this: ViewerPageLogic): void {
    const filePath = this.result()?.path;
    if (!filePath) { return; }
    this.vscode.postMessage({ type: 'readCompanionFiles', path: filePath });
  },

  dumpRdrReport(this: ViewerPageLogic): void {
    const r = this.rdr();
    if (!r) { this.log('[DUMP rdr] no RDR loaded', true); return; }
    this.log('--- RDR report ---', true);
    this.log(`Source: ${r.sourcePath}${r.entry ? ` / ${r.entry}` : ''}`);
    if (r.fallback) {
      this.log(`(fallback) ${r.parseError ?? ''}`);
      const lists = this.rdrFallbackLists();
      if (lists) {
        this.log(`Strings: ${lists.strings.length}, textureRefs: ${lists.textureRefs.length}, fileRefs: ${lists.fileRefs.length}, modelRefs: ${lists.modelRefs.length}`);
        if (lists.textureRefs.length) { this.log('Texture refs:'); for (const s of lists.textureRefs) { this.log('  ' + s); } }
        if (lists.fileRefs.length) { this.log('File refs:'); for (const s of lists.fileRefs) { this.log('  ' + s); } }
        if (lists.modelRefs.length) { this.log('Model refs:'); for (const s of lists.modelRefs.slice(0, 80)) { this.log('  ' + s); } }
      }
    } else {
      this.log(`Version ${r.version}, atoms ${r.atomCount}, pool ${r.stringPoolSize}B`);
      try { this.log(JSON.stringify(r.root, null, 2)); } catch { this.log('(unable to stringify root)'); }
    }
  },
};

export type RdrMethods = typeof rdrMethods;
