// Asset Viewer: the archive browser — the items of the open layer, record folders, selection and navigation.

import { parseZedArchive } from './zed-archive-parser';
import type { ViewerPageLogic } from './zipper-workbench-page';
import type { BrowserItem, ZipperEntryInfo } from './zipper-workbench-types';

export const browserMethods = {
  currentLayerTitle(this: ViewerPageLogic): string {
    const stack = this.archiveStack();
    if (stack.length > 0) {
      // Use only the leaf name (strip RUN\COMMON\... path prefixes).
      const full = stack[stack.length - 1].name;
      return full.split(/[\\/]/).pop() ?? full;
    }
    return this.result()?.fileName ?? '';
  },

  currentLayerSummary(this: ViewerPageLogic): string {
    const stack = this.archiveStack();
    if (stack.length > 0) {
      const archive = stack[stack.length - 1].archive;
      return `Nested archive with ${archive.entries.length} entry(s)`;
    }
    return this.result()?.summary ?? '';
  },

  openFolder(this: ViewerPageLogic, index: number): void {
    this.recordFolders.update(f => [...f, index]);
    this.selectedIndex.set(null);
    this.resetPreview();
  },

  // Up: out of a record folder first, then out of the archive.
  navigateUp(this: ViewerPageLogic): void {
    if (this.recordFolders().length > 0) {
      this.recordFolders.update(f => f.slice(0, -1));
      this.selectedIndex.set(null);
      this.resetPreview();
      return;
    }
    this.navigateBack();
  },

  navigateToFolder(this: ViewerPageLogic, depth: number): void {
    this.recordFolders.update(f => f.slice(0, depth));
    this.selectedIndex.set(null);
    this.resetPreview();
  },

  folderNames(this: ViewerPageLogic): string[] {
    const byIndex = new Map(this.rawEntries().map(e => [e.index, e.name]));
    return this.recordFolders().map(i => byIndex.get(i) ?? '?');
  },

  computeBrowserItems(this: ViewerPageLogic): BrowserItem[] {
    const raw = this.rawEntries();
    const file = this.currentFileName();
    const parents = this.entryParents();
    const byIndex = new Map(raw.map(e => [e.index, e]));
    const childCount = (e: ZipperEntryInfo) => e.type ?? 0;
    const childrenOf = (index: number) => raw.filter(e => parents?.get(e.index) === index);
    const leaf = (p: string) => p.split(/[\\/]/).pop() ?? p;
    const folder = (p: string) => { const parts = p.split(/[\\/]/); parts.pop(); return parts.join('\\'); };
    const isArchiveName = (n: string) => /\.(zed|zar|zdb|cdb)$/i.test(n);

    // A package (ZDB/CDB root, or a flat listing): its files.
    if (!parents) {
      return raw.map((e): BrowserItem => ({
        entry: e,
        name: leaf(e.path || e.name),
        kind: isArchiveName(e.name) ? 'archive' : 'file',
        detail: folder(e.path || e.name),
        size: e.size,
      }));
    }
    const top = raw.filter(e => parents.get(e.index) === -1);

    if (/_MDL(?:\([^)]+\))?\.ZED$/i.test(file) || /^WORL_MDL\.ZED$/i.test(file)) {
      const ns = file.split('_')[0].toLowerCase();
      return top.filter(e => e.size > 0 || childCount(e) > 0).map((e): BrowserItem => {
        const visuals = childrenOf(e.index).filter(c => /^N\d+(?:_[A-Z]?\d+)+$/i.test(c.name)).length;
        const character = /^MESH_/i.test(e.name) || ns === 'clib' || ns === 'flib';
        return {
          entry: e,
          name: e.name,
          kind: character ? 'character' : ns === 'weap' ? 'weapon' : 'model',
          detail: character ? 'skinned mesh' : `${visuals} visual${visuals === 1 ? '' : 's'}`,
          size: e.size,
        };
      });
    }
    if (/_TXR(?:\([^)]+\))?\.ZED$/i.test(file)) {
      const thumbs = new Map(this.textureThumbnails().map(t => [t.entryIndex, t]));
      return raw.filter(e => /\.tif$/i.test(e.name)).map((e): BrowserItem => {
        const t = thumbs.get(e.index);
        const data = childrenOf(e.index).find(c => c.name === 'texdat');
        return { entry: e, name: e.name, kind: 'texture', detail: t ? `${t.width}×${t.height}${t.paletteResolved ? '' : ' · no palette'}` : '', size: data?.size ?? e.size };
      });
    }
    if (/_PAL(?:\([^)]+\))?\.ZED$/i.test(file)) {
      return raw.filter(e => /^texpal_\d+$/i.test(e.name)).map((e): BrowserItem => {
        const buf = childrenOf(e.index).find(c => c.name === 'buf');
        return { entry: e, name: e.name, kind: 'palette', detail: buf ? (buf.size >= 1024 ? '256 colours · 32-bit' : '256 colours · 16-bit') : '', size: buf?.size ?? 0 };
      });
    }
    if (/_GEO(?:\([^)]+\))?\.ZED$/i.test(file)) {
      const models = top.find(e => e.name === 'models');
      const defs = models ? childrenOf(models.index) : top;
      return defs.map((e): BrowserItem => ({ entry: e, name: e.name, kind: 'definition', detail: `${childCount(e)} part${childCount(e) === 1 ? '' : 's'}`, size: e.size }));
    }
    // CLUTTER.ZAR: the clutter models (what the Map Builder places), not the record tree.
    const folders = this.recordFolders();
    if (/CLUTTER\.ZAR$/i.test(file) && folders.length === 0) {
      const clutter = top.find(e => e.name === 'Clutter');
      if (clutter) {
        return childrenOf(clutter.index).map((e): BrowserItem => {
          const n = childrenOf(e.index).length;
          return { entry: e, name: e.name, kind: 'clutter', detail: `${n} placement${n === 1 ? '' : 's'}`, size: 0 };
        });
      }
    }
    // Any other archive (ZAR, loose ZED): the records of the open folder (the top level at first). A record that
    // holds records is a folder; nested archives are first-class.
    const level = folders.length ? childrenOf(folders[folders.length - 1]) : top;
    return level.map((e): BrowserItem => ({
      entry: e,
      name: e.name,
      kind: isArchiveName(e.name) ? 'archive' : childCount(e) > 0 && e.size === 0 ? 'folder' : /\.[a-z0-9]{2,4}$/i.test(e.name) ? 'file' : 'record',
      detail: childCount(e) ? `${childCount(e)} item${childCount(e) === 1 ? '' : 's'}` : '',
      size: e.size,
    })).filter(item => byIndex.has(item.entry.index));
  },

  openItem(this: ViewerPageLogic, item: BrowserItem): void {
    if (item.kind === 'folder' || item.kind === 'clutter') { this.openFolder(item.entry.index); return; }
    const pending = this.pendingArchiveEntry();
    if (pending && pending.entryIndex === item.entry.index) { this.enterPendingArchive(); return; }
    this.enterWhenReady = item.kind === 'archive' ? item.entry.index : null;
    this.selectEntry(item.entry);
  },

  selectEntry(this: ViewerPageLogic, entry: ZipperEntryInfo): void {
    this.selectedIndex.set(entry.index);
  },

  selectThumbnail(this: ViewerPageLogic, entryIndex: number): void {
    this.selectedIndex.set(entryIndex);
  },

  navigateBack(this: ViewerPageLogic): void {
    this.recordFolders.set([]);
    this.archiveStack.update(s => s.slice(0, -1));
    this.selectedIndex.set(null);
    this.resetPreview();
  },

  navigateRoot(this: ViewerPageLogic): void {
    this.recordFolders.set([]);
    this.archiveStack.set([]);
    this.selectedIndex.set(null);
    this.resetPreview();
  },

  navigateToDepth(this: ViewerPageLogic, depth: number): void {
    this.recordFolders.set([]);
    this.archiveStack.update(s => s.slice(0, depth));
    this.selectedIndex.set(null);
    this.resetPreview();
  },

  rootBreadcrumb(this: ViewerPageLogic): string {
    const p = this.result()?.path ?? '';
    const file = p.split(/[\\/]/).pop() ?? p;
    return file || 'Root';
  },

  onSearch(this: ViewerPageLogic, event: Event): void {
    this.search.set((event.target as HTMLInputElement).value);
  },

  enterPendingArchive(this: ViewerPageLogic): void {
    const pending = this.pendingArchiveEntry();
    if (!pending) { return; }
    const bytes = this.nodeCache.get(pending.entryIndex);
    if (!bytes) { return; }
    try {
      const archive = parseZedArchive(bytes);
      this.archiveStack.update(s => [...s, { name: pending.entryName, archive, index: pending.entryIndex }]);
      this.recordFolders.set([]);
      this.selectedIndex.set(null);
      this.pendingArchiveEntry.set(null);
      this.resetPreview();
      this.setStatus(`Entered ${pending.entryName} (${archive.entries.length} entries).`, false, false);
      this.loadNestedPalettesForCurrentLayer();
      // If the sub-archive is a *_MDL.ZED, pull TXR/PAL/GEO companions from
      // the parent (root ZDB or another sub-archive) so model chunks can find
      // their textures and palette.
      this.loadMdlCompanionsForCurrentLayer();
    } catch (err) {
      this.previewMessage.set('Nested archive parse failed: ' + (err instanceof Error ? err.message : String(err)));
    }
  },
};

export type BrowserMethods = typeof browserMethods;
