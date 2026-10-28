// Asset Viewer file commands: open, reload, extract, save, Apache patches, and importing / replacing / removing
// files in a package (the context menu).

import { host } from '../../shared';
import type { ViewerPageLogic } from './zipper-workbench-page';
import type { BrowserItem, ZipperEntryInfo } from './zipper-workbench-types';

export const fileMethods = {
  // ------------------------------------------------------------------ context menu & package files

  // The file the Viewer opened (a .ZDB / .CDB package, or a loose .ZED / .ZAR).
  rootPath(this: ViewerPageLogic): string { return this.result()?.path ?? ''; },

  rootIsPackage(this: ViewerPageLogic): boolean { return /\.(zdb|cdb)$/i.test(this.rootPath()); },

  // Where an edit of the current layer goes: [root entry path, …nested records] in a package; only the records in a
  // loose ZED / ZAR. Null at the package root (its files are package entries, not archive records).
  layerChain(this: ViewerPageLogic): unknown[] | null {
    const stack = this.archiveStack();
    if (stack.length === 0) { return this.rootIsPackage() ? null : []; }
    const chain: unknown[] = [];
    stack.forEach((layer, i) => {
      if (i === 0 && this.rootIsPackage()) {
        const entry = this.result()?.entries.find(e => e.index === layer.index);
        chain.push(entry?.path || layer.name);
      } else {
        chain.push({ name: layer.name.split(/[\\/]/).pop(), index: layer.index });
      }
    });
    return chain;
  },

  // Model / texture / palette / GEO archives are edited in the editors; files are added and removed elsewhere.
  layerIsAssetArchive(this: ViewerPageLogic): boolean {
    return /_(MDL|TXR|PAL|GEO)(?:\([^)]+\))?\.ZED$/i.test(this.currentFileName().split(/[\\/]/).pop() ?? '');
  },

  // Whether the current layer's files can be added to / replaced / removed.
  canEditFiles(this: ViewerPageLogic): boolean {
    if (!this.result() || this.busy()) { return false; }
    if (this.archiveStack().length === 0) { return true; }
    return !this.layerIsAssetArchive();
  },

  packageEdit(this: ViewerPageLogic, label: string, ops: unknown[]): void {
    this.layersBeforeEdit = this.archiveStack().map(l => l.name);
    this.vscode.postMessage({ type: 'packageEdit', path: this.rootPath(), ops, label });
  },

  // After an edit reopens the package: back into the archive the edit was made in (by name, layer by layer).
  async restoreLayers(this: ViewerPageLogic, names: string[], message: string): Promise<void> {
    for (const name of names) {
      const depth = this.archiveStack().length;
      const entry = this.rawEntries().find(e => e.name === name);
      if (!entry) { break; }
      this.enterWhenReady = entry.index;
      this.selectEntry(entry);
      for (let t = 0; t < 100 && this.archiveStack().length === depth; t++) { await new Promise(r => setTimeout(r, 50)); }
      if (this.archiveStack().length === depth) { break; }
    }
    if (message) { this.setStatus(message, false, false); }
  },

  // Import: package files (.ZED / .ZAR / .MPS, or every file of another .ZDB / .CDB) at the package root; any file
  // inside a ZAR / ZED.
  async importFiles(this: ViewerPageLogic): Promise<void> {
    if (!this.canEditFiles()) { return; }
    const chain = this.layerChain();
    const atRoot = chain === null;
    const file = await host.browse({
      mode: 'open',
      title: atRoot ? 'Import into the package' : `Import into ${this.currentLayerTitle()}`,
      filters: atRoot
        ? [{ label: 'Package files', extensions: ['zed', 'zar', 'mps'] }, { label: 'Another package (merge its files)', extensions: ['zdb', 'cdb'] }]
        : [{ label: 'RDR, ZED and ZAR files', extensions: ['rdr', 'zed', 'zar'] }],
      key: 'viewer-import',
      submitLabel: 'Import',
    });
    if (!file) { return; }
    const leaf = file.split(/[\\/]/).pop() ?? file;
    if (!atRoot) {
      const existing = this.browserItems().find(i => i.name.toLowerCase() === leaf.toLowerCase());
      if (existing) {
        if (!await this.confirm(`${this.currentLayerTitle()} already has ${leaf}`, 'Replace it')) { return; }
        this.packageEdit(`Replace ${leaf}`, [{ op: 'replace', inside: chain, index: existing.entry.index, name: existing.entry.name, file }]);
        return;
      }
      this.packageEdit(`Import ${leaf}`, [{ op: 'add', inside: chain, name: leaf, file }]);
      return;
    }
    if (/\.(zdb|cdb)$/i.test(leaf)) {
      if (!await this.confirm(`Merge every .ZED / .ZAR / .MPS of ${leaf} into this package? Files with the same path are replaced.`, 'Merge')) { return; }
      this.packageEdit(`Merge ${leaf}`, [{ op: 'merge', file }]);
      return;
    }
    const entries = this.result()?.entries ?? [];
    const same = entries.find(e => (e.path || e.name).split(/[\\/]/).pop()?.toLowerCase() === leaf.toLowerCase());
    if (same) {
      if (!await this.confirm(`The package already has ${same.path || same.name}`, 'Replace it')) { return; }
      this.packageEdit(`Replace ${leaf}`, [{ op: 'replace', path: same.path || same.name, file }]);
      return;
    }
    const v = await host.prompt(`Import ${leaf}`, [
      { key: 'path', label: 'Path in the package', value: this.suggestFolder(leaf) + leaf, pattern: /\.(zed|zar|mps)$/i, hint: 'A package path ending in the file name, e.g. RUN\\SP\\M51\\M51_EXTRA.ZED' },
    ], 'Import');
    if (!v) { return; }
    this.packageEdit(`Import ${leaf}`, [{ op: 'add', path: v['path'].trim(), file }]);
  },

  // The folder a new package file most likely belongs in: beside the files of its namespace (M51_… → RUN\SP\M51\),
  // else the folder most files are in.
  suggestFolder(this: ViewerPageLogic, leaf: string): string {
    const entries = (this.result()?.entries ?? []).map(e => e.path || e.name);
    const folderOf = (p: string) => p.replace(/[^\\/]+$/, '');
    const ns = /^([^_]+)_/.exec(leaf)?.[1]?.toLowerCase();
    const sibling = ns ? entries.find(p => (p.split(/[\\/]/).pop() ?? '').toLowerCase().startsWith(ns + '_')) : undefined;
    if (sibling) { return folderOf(sibling); }
    const counts = new Map<string, number>();
    for (const p of entries) { const f = folderOf(p); counts.set(f, (counts.get(f) ?? 0) + 1); }
    return [...counts].sort((a, b) => b[1] - a[1])[0]?.[0] ?? '';
  },

  async replaceItem(this: ViewerPageLogic, item: BrowserItem): Promise<void> {
    if (!this.canEditFiles()) { return; }
    const chain = this.layerChain();
    const ext = (/\.([^.\\/]+)$/.exec(item.name)?.[1] ?? '').toLowerCase();
    const file = await host.browse({ mode: 'open', title: `Replace ${item.name} with`, filters: ext ? [{ label: `.${ext.toUpperCase()} files`, extensions: [ext] }] : [], key: 'viewer-import', submitLabel: 'Replace' });
    if (!file) { return; }
    this.packageEdit(`Replace ${item.name}`, chain === null
      ? [{ op: 'replace', path: item.entry.path || item.entry.name, file }]
      : [{ op: 'replace', inside: chain, index: item.entry.index, name: item.entry.name, file }]);
  },

  async removeItem(this: ViewerPageLogic, item: BrowserItem): Promise<void> {
    if (!this.canEditFiles()) { return; }
    if (!await this.confirm(`Remove ${item.name} from ${this.currentLayerTitle()}?`, 'Remove')) { return; }
    const chain = this.layerChain();
    this.packageEdit(`Remove ${item.name}`, chain === null
      ? [{ op: 'remove', path: item.entry.path || item.entry.name }]
      : [{ op: 'remove', inside: chain, index: item.entry.index, name: item.entry.name }]);
  },

  async confirm(this: ViewerPageLogic, title: string, submit: string): Promise<boolean> {
    return (await host.prompt(title, [], submit)) !== null;
  },

  // Which editor an item opens in, from the package the Viewer has open.
  hasMap(this: ViewerPageLogic): boolean {
    return (this.result()?.entries ?? []).some(e => /(^|[\\/])(WORL_MDL\.ZED|CLUTTER\.ZAR)$/i.test(e.path || e.name));
  },

  openInMap(this: ViewerPageLogic): void { host.open('map', this.rootPath()); },

  openFile(this: ViewerPageLogic): void {
    this.vscode.postMessage({ type: 'openFile' });
  },

  // Force re-extract of the loaded package. Used after the source file
  // changes on disk or to recover from a state-loss bug.
  reloadPackage(this: ViewerPageLogic): void {
    const filePath = this.result()?.path;
    if (!filePath) { return; }
    this.packageEntryBytes.clear();
    this.packageMpsResults.set([]);
    this.companionTextures.set(null);
    this.companionScene.set(null);
    this.palettes.set(null);
    this.rootTextureFetchRequested.clear();
    this.packageFillRequested = false;
    this.packageFillFetches.clear();
    this.packageFillBytes.clear();
    this.packageLoading.set(true);
    this.packageState.set('loading');
    this.packageMessage.set(`Force-reloading ${filePath.split(/[\\/]/).pop()}…`);
    this.log(`[package] force-reloading ${filePath.split(/[\\/]/).pop()}`);
    this.vscode.postMessage({ type: 'reloadZdbPackage', path: filePath });
  },

  refresh(this: ViewerPageLogic): void {
    const file = this.result()?.path;
    if (file) { this.vscode.postMessage({ type: 'refresh', path: file }); }
  },

  extractAll(this: ViewerPageLogic): void {
    const file = this.result()?.path;
    if (file) { this.vscode.postMessage({ type: 'extractAll', path: file }); }
  },

  extractEntry(this: ViewerPageLogic, entry: ZipperEntryInfo): void {
    const file = this.result()?.path;
    if (!file) { return; }
    this.vscode.postMessage({
      type: 'extractEntry',
      path: file,
      entryIndex: entry.index,
      entryName: entry.name || entry.path,
    });
  },

  saveExportDecrypted(this: ViewerPageLogic): void {
    const file = this.result()?.path;
    if (file) { this.vscode.postMessage({ type: 'saveExportDecrypted', path: file }); }
  },

  saveCraft(this: ViewerPageLogic, mode: 'active' | 'overflow' | 'extreme' | 'all'): void {
    const file = this.result()?.path;
    if (file) { this.vscode.postMessage({ type: 'saveCraft', path: file, mode }); }
  },

  apacheDecrypt(this: ViewerPageLogic, section: 'ftscore' | 'zsealetc'): void {
    const file = this.result()?.path;
    if (file) { this.vscode.postMessage({ type: 'apacheDecryptSection', path: file, section }); }
  },

  apacheBuildPatch(this: ViewerPageLogic): void {
    const file = this.result()?.path;
    if (file) { this.vscode.postMessage({ type: 'apacheBuildPatch', path: file }); }
  },

  revealLast(this: ViewerPageLogic): void {
    const target = this.lastRevealPath();
    if (target) { this.vscode.postMessage({ type: 'revealPath', path: target }); }
  },
};

export type FileMethods = typeof fileMethods;
