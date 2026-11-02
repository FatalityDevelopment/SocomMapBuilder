// Asset Viewer palettes (_PAL archives, texpal_N records): loading them for the open layer and aliasing them to textures.

import { aliasPalettesForTextureEntries, parsePaletteEntries, ZedPalette, ZedTexture } from './zed-texture-parser';
import { InspectedZedArchive, parseZedArchive } from './zed-archive-parser';
import type { ViewerPageLogic } from './zipper-workbench-page';

export const paletteMethods = {
  onPaletteOverrideChange(this: ViewerPageLogic, event: Event): void {
    const value = (event.target as HTMLSelectElement).value;
    this.paletteOverride.set(value && value.length > 0 ? value : null);
    this.applyModelToRenderer();
  },

  paletteLabel(this: ViewerPageLogic, tex: ZedTexture): string {
    if (!tex.header.isIndexed) { return 'n/a (direct colour)'; }
    const id = tex.header.cbp ?? tex.header.paletteId;
    const source = tex.header.cbp !== null ? 'TEX0' : 'header';
    return tex.paletteResolved
      ? `texpal_${id} (${source})`
      : `missing: texpal_${id} (${source})`;
  },

  // When we enter a *_TXR.ZED sub-archive, the matching *_PAL.ZED usually
  // lives next to it in the parent layer (sub-archive or the root ZDB). Find
  // and parse it inline so indexed textures inside resolve to colour instead
  // of grayscale.
  loadNestedPalettesForCurrentLayer(this: ViewerPageLogic): void {
    // When the full ZDB package was preloaded (applyZdbPackage), the
    // cross-package merged palette map already contains every palette
    // from every _PAL.ZED in the archive. Re-fetching a single-prefix
    // PAL here would OVERWRITE the merged map with one prefix's
    // palettes and break textures from the other prefixes — that was
    // exactly the bug the user hit drilling into WORL_MDL.ZED inside
    // M51.ZDB. Skip when package context is intact.
    if (this.packageEntryBytes.size > 0) { return; }

    const stack = this.archiveStack();
    if (stack.length === 0) { return; }
    const current = stack[stack.length - 1];
    const currentLeafName = (current.name.split(/[\\/]/).pop() ?? current.name);
    const txrMatch = /^(.+?)_TXR(?:\([^)]+\))?\.ZED$/i.exec(currentLeafName);
    if (!txrMatch) { return; }
    const targetPalLeaf = `${txrMatch[1]}_PAL.ZED`.toLowerCase();

    const previousLayer = stack.length >= 2 ? stack[stack.length - 2].archive : null;

    // Case A: parent is another sub-archive. PAL bytes are in memory.
    if (previousLayer) {
      const palEntry = previousLayer.entries.find(
        e => (e.name.split(/[\\/]/).pop() ?? e.name).toLowerCase() === targetPalLeaf,
      );
      if (!palEntry) { return; }
      const palBytes = previousLayer.entryData.get(palEntry.index);
      if (!palBytes || palBytes.length === 0) { return; }
      this.applyPaletteBytesForCurrentLayer(palBytes, palEntry.name, current.archive);
      return;
    }

    // Case B: parent is the root file. We need to async-fetch the PAL entry
    // bytes from the bridge, then parse and apply.
    const filePath = this.result()?.path;
    if (!filePath) { return; }
    const rootEntries = this.result()?.entries ?? [];
    const palEntry = rootEntries.find(
      e => (e.name.split(/[\\/]/).pop() ?? e.name).toLowerCase() === targetPalLeaf,
    );
    if (!palEntry) { return; }

    // Mark this fetch as for "palette purposes" so handleNodeData routes it
    // to applyPaletteBytesForCurrentLayer instead of decodePayload.
    this.pendingPaletteFetch = { rootEntryIndex: palEntry.index, palName: palEntry.name };
    this.vscode.postMessage({ type: 'readNode', path: filePath, entryIndex: palEntry.index });
  },

  applyPaletteBytesForCurrentLayer(
    this: ViewerPageLogic,
    palBytes: Uint8Array,
    palName: string,
    currentArchive: InspectedZedArchive,
  ): void {

    try {
      const palArchive = parseZedArchive(palBytes);
      let palettes = parsePaletteEntries(
        palArchive.entries.map(e => ({ name: e.name })),
        i => palArchive.entryData.get(palArchive.entries[i].index) ?? new Uint8Array(0),
      );
      palettes = aliasPalettesForTextureEntries(
        palettes,
        currentArchive.entries.map(e => ({ name: e.name })),
        i => currentArchive.entryData.get(currentArchive.entries[i].index) ?? new Uint8Array(0),
      );
      if (palettes.size > 0) {
        this.palettes.set(palettes);
        this.paletteMessage.set(`Loaded ${palettes.size} palette(s) from ${palName}.`);
      }
    } catch { /* leave existing palettes in place */ }
  },

  rebuildAliasedPalettes(this: ViewerPageLogic, sourceName: string): void {
    if (this.rootCompanionPalArchives.size === 0) { return; }
    // Merge into what is loaded: palettes from the package (or an earlier
    // companion) must survive an on-demand fetch of another archive.
    const merged = new Map<string, ZedPalette>(this.palettes() ?? []);
    for (const [namespace, pal] of this.rootCompanionPalArchives) {
      let palettes = parsePaletteEntries(
        pal.entries.map(e => ({ name: e.name })),
        i => pal.entryData.get(pal.entries[i].index) ?? new Uint8Array(0),
      );
      const txr = this.rootCompanionTxrArchives.get(namespace);
      if (txr) {
        palettes = aliasPalettesForTextureEntries(
          palettes,
          txr.entries.map(e => ({ name: e.name })),
          i => txr.entryData.get(txr.entries[i].index) ?? new Uint8Array(0),
        );
      }
      for (const [key, value] of palettes) {
        merged.set(key, value);
        merged.set(`${namespace}:${key}`, value);
      }
    }
    if (merged.size > 0) {
      this.palettes.set(merged);
      this.paletteMessage.set(`Loaded ${merged.size} palette alias(es) from ${sourceName}.`);
      this.setStatus(
        `Palettes ready (${merged.size}). TXR loaded: ${this.rootCompanionTxrArchives.size}.`,
        false,
        false,
      );
    } else {
      this.setStatus(`Palette PAL parsed but yielded 0 palettes (${sourceName}).`, true, false);
    }
  },

  requestPalettesIfNeeded(this: ViewerPageLogic): void {
    const filePath = this.result()?.path;
    if (!filePath || this.palettesRequested) { return; }
    const name = filePath.split(/[\\/]/).pop() ?? '';
    // Fire for both .ZED siblings and for reader .ZAR archives (READERC/READERX).
    // The provider's _findCompanionFiles handles both cases and returns the
    // appropriate UI_TXR / UI_PAL companions so RDR screens show thumbnails.
    if (!/\.(ZED|ZAR)$/i.test(name)) { return; }
    this.palettesRequested = true;
    this.vscode.postMessage({ type: 'readCompanionFiles', path: filePath });
  },
};

export type PaletteMethods = typeof paletteMethods;
