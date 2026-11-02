// Asset Viewer: whole .ZDB / .CDB packages — every top-level archive loaded at once, missing ones filled one by one,
// and the companion TXR / PAL / GEO archives a model archive draws with.

import { aliasPalettesForTextureEntries, parsePaletteEntries, ZedPalette } from './zed-texture-parser';
import { InspectedZedArchive, parseZedArchive } from './zed-archive-parser';
import { GeoEntry, parseGeoScene } from './zed-geo-parser';
import type { ViewerPageLogic } from './zipper-workbench-page';
import { namespaceFromArchiveName } from './zipper-workbench-textures';
import type { NodePayload } from './zipper-workbench-types';
import { describePayload, escapeRegExp, nodeBytes, rootEntryLeaf } from './zipper-workbench-util';

export const packageMethods = {
  loadMdlCompanionsForCurrentLayer(this: ViewerPageLogic): void {
    // Same guard as loadNestedPalettesForCurrentLayer — when the ZDB
    // package preload already wired up companionTextures, palettes, and
    // companionScene with merged cross-prefix data, this per-layer
    // single-prefix loader would CLOBBER that with one prefix's data
    // (e.g. drilling into M51_MDL.ZED inside M51.ZDB would replace the
    // M51+CLIB+FLIB+BENZ+WEAP merged palette map with M51-only). Skip.
    if (this.packageEntryBytes.size > 0) {
      this.log(`[companion] skipping per-layer load — package context already loaded`);
      return;
    }
    // The package is still streaming in: it re-decodes the selected model
    // when it lands, so loading one prefix here would only race it.
    if (this.packageLoading()) {
      this.log(`[companion] package still loading — textures will bind when it arrives`);
      return;
    }

    const stack = this.archiveStack();
    if (stack.length === 0) { return; }
    const current = stack[stack.length - 1];
    const currentLeafName = current.name.split(/[\\/]/).pop() ?? current.name;
    const mdlMatch = /^(.+?)_MDL(?:\([^)]+\))?\.ZED$/i.exec(currentLeafName);
    if (!mdlMatch) { return; }
    const prefix = mdlMatch[1];

    const companionPrefixes = [prefix];
    if (/^effe$/i.test(prefix)) {
      companionPrefixes.push('ALPH');
    }
    const previous = stack.length >= 2 ? stack[stack.length - 2].archive : null;
    const mapPrefix = /^worl$/i.test(prefix) ? this.mapCompanionPrefixForCurrentLayer(previous) : null;
    if (mapPrefix && !companionPrefixes.some(p => p.toLowerCase() === mapPrefix.toLowerCase())) {
      companionPrefixes.push(mapPrefix);
    }
    const targets: Array<{ kind: 'txr' | 'pal' | 'geo'; leafName: string }> = [];
    for (const p of companionPrefixes) {
      targets.push({ kind: 'txr', leafName: `${p}_TXR.ZED`.toLowerCase() });
      targets.push({ kind: 'pal', leafName: `${p}_PAL.ZED`.toLowerCase() });
    }
    targets.push({ kind: 'geo', leafName: `${prefix}_GEO.ZED`.toLowerCase() });
    if (mapPrefix) {
      targets.push({ kind: 'geo', leafName: `${mapPrefix}_GEO.ZED`.toLowerCase() });
    }

    this.rootCompanionFetches.clear();
    this.rootCompanionTxrArchives.clear();
    this.rootCompanionPalArchives.clear();
    this.companionTextures.set(null);
    this.palettes.set(null);

    // Search in-memory sub-archives on the stack first (excluding current).
    if (previous) {
      for (const t of targets) {
        const entry = previous.entries.find(
          e => (e.name.split(/[\\/]/).pop() ?? e.name).toLowerCase() === t.leafName,
        );
        if (!entry) { continue; }
        const bytes = previous.entryData.get(entry.index);
        if (!bytes || bytes.length === 0) { continue; }
        this.applyMdlCompanion(t.kind, bytes, entry.name);
      }
      return;
    }

    // Fall back to the root ZDB. We have to async-fetch each entry's bytes.
    const rootEntries = this.result()?.entries ?? [];
    const filePath = this.result()?.path;
    if (!filePath) { return; }
    const requested: string[] = [];
    const missing: string[] = [];
    for (const t of targets) {
      const entry = rootEntries.find(
        e => (e.name.split(/[\\/]/).pop() ?? e.name).toLowerCase() === t.leafName,
      );
      if (!entry) { missing.push(t.leafName); continue; }
      this.rootCompanionFetches.set(entry.index, t.kind);
      this.vscode.postMessage({ type: 'readNode', path: filePath, entryIndex: entry.index });
      requested.push(t.leafName);
    }
    if (requested.length > 0) {
      this.setStatus(
        `Fetching ${requested.length} companion(s): ${requested.join(', ')}${missing.length ? ` (missing: ${missing.join(', ')})` : ''}`,
        false,
        true,
      );
    } else if (missing.length > 0) {
      this.setStatus(`No companion files found in root for ${prefix} (looked for: ${missing.join(', ')}).`, true, false);
    }
  },

  applyMdlCompanion(this: ViewerPageLogic, kind: 'txr' | 'pal' | 'geo', bytes: Uint8Array, name: string): void {
    try {
      const archive = parseZedArchive(bytes);
      this.setStatus(`Loaded companion ${kind.toUpperCase()} (${name}, ${archive.entries.length} entries).`, false, false);
      const namespace = namespaceFromArchiveName(name) ?? name.toLowerCase();
      if (kind === 'txr') {
        this.rootCompanionTxrArchives.set(namespace, archive);
        // Build the .tif -> texdat map used by buildTifByteMap.
        const map = new Map<string, Uint8Array>(this.companionTextures() ?? []);
        const entries = archive.entries;
        for (let i = 0; i < entries.length; i++) {
          if (!/\.tif$/i.test(entries[i].name)) { continue; }
          for (let j = i + 1; j < Math.min(i + 4, entries.length); j++) {
            if (/^texdat$/i.test(entries[j].name)) {
              const data = archive.entryData.get(entries[j].index);
              if (data && data.length > 0) {
                const key = entries[i].name.toLowerCase();
                map.set(key, data);
                map.set(`${namespace}:${key}`, data);
              }
              break;
            }
            if (/\.tif$/i.test(entries[j].name)) { break; }
          }
        }
        this.companionTextures.set(map);
      } else if (kind === 'pal') {
        this.rootCompanionPalArchives.set(namespace, archive);
      } else if (kind === 'geo') {
        const entries: GeoEntry[] = archive.entries.map(e => ({
          index: e.index,
          name: e.name,
          type: e.type,
          data: archive.entryData.get(e.index) ?? new Uint8Array(0),
        }));
        const scene = parseGeoScene(entries);
        this.companionScenesByNamespace.set(namespace, scene);
        this.companionScene.set(scene);
      }
      // Re-derive palettes whenever TXR or PAL arrives, since aliasing needs both.
      this.rebuildAliasedPalettes(name);
      // Re-apply textures to the model in view.
      this.applyModelToRenderer();
    } catch { /* skip */ }
  },

  mapCompanionPrefixForCurrentLayer(this: ViewerPageLogic, previous: InspectedZedArchive | null): string | null {
    const path = this.result()?.path;
    const leaf = path ? (path.split(/[\\/]/).pop() ?? path).replace(/\.[^.]+$/i, '') : '';
    const supportPrefixes = new Set(['weap', 'flib', 'clib', 'benz', 'objt', 'effe', 'trmg', 'tlrm', 'alph', 'font', 'hud', 'hud2', 'hudw', 'load', 'junk']);
    const hasPair = (prefix: string): boolean => {
      if (this.packageHasArchive(prefix.toLowerCase(), 'txr') && this.packageHasArchive(prefix.toLowerCase(), 'pal')) {
        return true;
      }
      if (!previous) { return false; }
      const txr = `${prefix}_TXR.ZED`.toLowerCase();
      const pal = `${prefix}_PAL.ZED`.toLowerCase();
      return previous.entries.some(e => (e.name.split(/[\\/]/).pop() ?? e.name).toLowerCase() === txr) &&
        previous.entries.some(e => (e.name.split(/[\\/]/).pop() ?? e.name).toLowerCase() === pal);
    };

    if (leaf && hasPair(leaf)) { return leaf; }
    // WORL_MDL entered straight from the ZDB root: the map's own TXR/PAL
    // pair sits next to it in the root entries (M51.ZDB -> M51_TXR/PAL).
    if (!previous) {
      const rootLeaves = (this.result()?.entries ?? []).map(e => (e.name.split(/[\\/]/).pop() ?? e.name).toLowerCase());
      const rootPrefix = mapPrefixFromLeaves(rootLeaves, leaf, supportPrefixes);
      if (rootPrefix) { return rootPrefix; }
    }
    if (previous) {
      const prefixes = new Set<string>();
      for (const entry of previous.entries) {
        const entryLeaf = entry.name.split(/[\\/]/).pop() ?? entry.name;
        const m = /^(.+?)_TXR(?:\([^)]+\))?\.ZED$/i.exec(entryLeaf);
        if (m) { prefixes.add(m[1]); }
      }
      for (const prefix of prefixes) {
        const lower = prefix.toLowerCase();
        if (supportPrefixes.has(lower)) { continue; }
        if (/^ld\d+$/i.test(prefix) || /^z.+\d+$/i.test(prefix)) { continue; }
        if (hasPair(prefix)) { return prefix; }
      }
    }
    return this.primaryMapTextureNamespace();
  },

  packageHasArchive(this: ViewerPageLogic, namespace: string, kind: 'txr' | 'pal' | 'mdl' | 'geo'): boolean {
    const escaped = namespace.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    const rx = new RegExp(`^${escaped}_${kind}(?:\\([^)]+\\))?\\.zed$`, 'i');
    for (const key of this.packageEntryBytes.keys()) {
      if (rx.test(key)) { return true; }
    }
    return false;
  },

  // Kick off a full ZDB package fetch. Mirrors the HTML map viewer's
  // RUN.zip preload: we get every top-level entry's bytes in one round
  // trip so textures / palettes / GEO can be wired up before the user
  // even drills into a sub-archive.
  requestZdbPackageIfNeeded(this: ViewerPageLogic): void {
    const filePath = this.result()?.path;
    if (!filePath) { return; }
    const leaf = filePath.split(/[\\/]/).pop() ?? filePath;
    const isZdb = /\.(zdb|cdb)$/i.test(leaf);
    const isLooseMapWorld = /\.zed$/i.test(leaf) && (
      /^worl_mdl(?:\([^)]+\))?\.zed$/i.test(leaf) ||
      !/_(?:MDL|TXR|PAL|GEO)(?:\([^)]+\))?\.ZED$/i.test(leaf)
    );
    if (!isZdb && !isLooseMapWorld) { return; }
    // Skip apache/save ZDBs — they don't carry the sibling-ZED package layout.
    const role = this.result()?.role ?? '';
    if (role === 'apache-patch-archive' || role === 'game-save') { return; }
    this.packageLoading.set(true);
    this.packageState.set('loading');
    this.packageMessage.set(`Loading ${filePath.split(/[\\/]/).pop()} package…`);
    this.log(`[package] requesting full package contents for ${filePath.split(/[\\/]/).pop()}`);
    this.vscode.postMessage({ type: 'loadZdbPackage', path: filePath });
  },

  // Decode every top-level node of the just-loaded ZDB. _TXR / _PAL / _GEO
  // ZEDs are parsed inline so model previews resolve textures cross-archive
  // (the gap that caused 64-missing-textures on M51). Other entries are
  // cached by leaf name so drill-ins don't re-fetch them.
  applyZdbPackage(this: ViewerPageLogic, nodes: Array<{ index: number; name: string; data: NodePayload }>): void {
    this.packageEntryBytes.clear();
    this.packageMpsResults.set([]);
    this.companionScenesByNamespace.clear();
    this.companionScene.set(null);
    const txrArchives: Array<{ name: string; archive: InspectedZedArchive }> = [];
    const palArchives: Array<{ name: string; archive: InspectedZedArchive }> = [];
    const geoArchives: Array<{ name: string; archive: InspectedZedArchive }> = [];
    // The package lists every node, nested archives' nodes included. Match
    // its top-level files against the ZDB's own entry list by file name and
    // size: a ZDB can mix "RUN\\...\\X.ZED" paths with bare root names
    // (M51), so "has a separator" alone drops most archives, and nested
    // nodes can reuse a root's name (a 4-byte "WORL_MDL.ZED" reference).
    // Without a ZDB entry list (loose map folder) every node is a file.
    const rootKeys = this.packageRootKeys();
    const hasRootPaths = nodes.some(n => /[\\/]/.test(n.name));
    let decodedNodes = 0;
    let undecodableNodes = 0;
    let firstUndecodable = '';

    for (const node of nodes) {
      if (!node.data) { continue; }
      let bytes: Uint8Array;
      try { bytes = nodeBytes(node.data); }
      catch {
        undecodableNodes++;
        if (!firstUndecodable) { firstUndecodable = describePayload(node.data); }
        continue;
      }
      if (bytes.length === 0) { continue; }
      decodedNodes++;
      const leaf = (node.name.split(/[\\/]/).pop() ?? node.name).toLowerCase();
      const rootKey = `${leaf}|${bytes.length}`;
      const isRootPackageEntry = rootKeys
        ? rootKeys.delete(rootKey)
        : !hasRootPaths || /[\\/]/.test(node.name);
      if (!isRootPackageEntry) { continue; }
      this.packageEntryBytes.set(leaf, bytes);

      // Parse each sibling ZED inline. We tolerate parse errors (some
      // entries may not actually be ZED containers — e.g. raw data files).
      const tryParse = (): InspectedZedArchive | null => {
        try { return parseZedArchive(bytes); } catch { return null; }
      };
      if (/_TXR(?:\([^)]+\))?\.zed$/i.test(leaf)) {
        const ar = tryParse(); if (ar) { txrArchives.push({ name: leaf, archive: ar }); }
      } else if (/_PAL(?:\([^)]+\))?\.zed$/i.test(leaf)) {
        const ar = tryParse(); if (ar) { palArchives.push({ name: leaf, archive: ar }); }
      } else if (/_GEO(?:\([^)]+\))?\.zed$/i.test(leaf)) {
        const ar = tryParse(); if (ar) { geoArchives.push({ name: leaf, archive: ar }); }
      }
    }

    if (undecodableNodes > 0) {
      this.log(`[package] ${undecodableNodes} of ${nodes.length} payload(s) could not be decoded (first: ${firstUndecodable})`, true);
    }
    if (rootKeys && rootKeys.size > 0 && decodedNodes > 0) {
      this.requestMissingPackageEntries(rootKeys);
    }
    // A package whose payloads all failed to decode means the binary
    // transfer did not survive the trip to the webview. Ask once for a
    // base64 copy instead of rendering an untextured map.
    if (decodedNodes === 0 && undecodableNodes > 0 && !this.packageBase64Retried) {
      this.packageBase64Retried = true;
      const filePath = this.result()?.path;
      if (filePath) {
        this.packageMessage.set('Package payloads arrived undecodable; requesting a base64 copy…');
        this.log('[package] retrying the package as base64', true);
        this.vscode.postMessage({ type: 'reloadZdbPackage', path: filePath, encoding: 'base64' });
        return;
      }
    }

    const packageMpsResults = this.parsePackageMpsResults();
    this.packageMpsResults.set(packageMpsResults);
    this.loadMapPoints(packageMpsResults);

    // Build the .tif -> texdat map across every TXR archive in the package.
    const tifMap = new Map<string, Uint8Array>();
    for (const { name, archive } of txrArchives) {
      const namespace = namespaceFromArchiveName(name);
      const entries = archive.entries;
      for (let i = 0; i < entries.length; i++) {
        if (!/\.tif$/i.test(entries[i].name)) { continue; }
        // texdat sibling is the next non-tif data entry within a few slots.
        for (let j = i + 1; j < Math.min(i + 4, entries.length); j++) {
          if (/^texdat$/i.test(entries[j].name)) {
            const data = archive.entryData.get(entries[j].index);
            if (data && data.length > 0) {
              const key = entries[i].name.toLowerCase();
              tifMap.set(key, data);
              if (namespace) { tifMap.set(`${namespace}:${key}`, data); }
            }
            break;
          }
          if (/\.tif$/i.test(entries[j].name)) { break; }
        }
      }
    }
    if (tifMap.size > 0) { this.companionTextures.set(tifMap); }

    // Build palettes across every PAL/TXR pair we found. Match by leaf
    // prefix (e.g. M51_TXR.ZED <-> M51_PAL.ZED) so multi-prefix packages
    // alias correctly.
    let mergedPalettes = new Map<string, ZedPalette>();
    for (const pal of palArchives) {
      const prefix = pal.name.replace(/_pal(?:\([^)]+\))?\.zed$/i, '');
      const txrMatch = txrArchives.find(t => t.name.startsWith(prefix + '_txr'));
      let p = parsePaletteEntries(
        pal.archive.entries.map(e => ({ name: e.name })),
        i => pal.archive.entryData.get(pal.archive.entries[i].index) ?? new Uint8Array(0),
      );
      if (txrMatch) {
        p = aliasPalettesForTextureEntries(
          p,
          txrMatch.archive.entries.map(e => ({ name: e.name })),
          i => txrMatch.archive.entryData.get(txrMatch.archive.entries[i].index) ?? new Uint8Array(0),
        );
      }
      const namespace = prefix.toLowerCase();
      for (const [k, v] of p) {
        mergedPalettes.set(k, v);
        mergedPalettes.set(`${namespace}:${k}`, v);
      }
    }
    if (mergedPalettes.size > 0) { this.palettes.set(mergedPalettes); }

    // Parse every GEO archive into a namespace lookup. Keep the first one as
    // the legacy companionScene for map/debug tools, but character meshes need
    // their own CLIB/FLIB skeleton scenes even when a full package is loaded.
    for (const geo of geoArchives) {
      try {
        const geoEntries: GeoEntry[] = geo.archive.entries.map(e => ({
          index: e.index,
          name: e.name,
          type: e.type,
          data: geo.archive.entryData.get(e.index) ?? new Uint8Array(0),
        }));
        const scene = parseGeoScene(geoEntries);
        const namespace = namespaceFromArchiveName(geo.name);
        if (namespace) { this.companionScenesByNamespace.set(namespace, scene); }
        if (!this.companionScene()) { this.companionScene.set(scene); }
      } catch { /* try next */ }
    }

    this.packageLoading.set(false);
    this.packageState.set(decodedNodes > 0 ? 'loaded' : 'failed');
    this.packageMessage.set(decodedNodes > 0
      ? `Package: ${txrArchives.length} texture archive(s) (${txrArchives.map(t => t.name.replace(/\.zed$/i, '')).join(', ') || 'none'}), ` +
        `${palArchives.length} palette archive(s), ${geoArchives.length} GEO archive(s).`
      : `Package load failed: none of ${nodes.length} payload(s) could be decoded.`);
    const mdlLeaves = [...this.packageEntryBytes.keys()].filter(k => /_MDL(?:\([^)]+\))?\.zed$/i.test(k));
    this.log(`[package] ${this.packageEntryBytes.size} top-level entries; textures=${tifMap.size} palettes=${mergedPalettes.size} GEO=${geoArchives.length} MDL=${mdlLeaves.length}`);
    if (mdlLeaves.length) {
      this.log(`[package] MDL archives: ${mdlLeaves.join(', ')}`);
    }
    if (packageMpsResults.length) {
      const totalMaps = packageMpsResults.reduce((sum, r) => sum + r.foundCount, 0);
      this.log(`[bounds] package MPS bounds loaded: ${totalMaps} map(s) from ${packageMpsResults.map(r => r.sourcePath.split(/[\\/]/).pop()).join(', ')}`);
    }

    // If the user already clicked a preview before the package finished
    // loading, the initial render had no companion textures. Re-trigger
    // decodePayload from the cached bytes so the freshly-loaded textures
    // flow through the full pipeline (not just the renderer re-bind) —
    // this is what fixes the "have to open views after a fresh open"
    // symptom.
    const entry = this.selectedEntry();
    if (!entry && this.autoEnterLooseMapWorldModel()) {
      return;
    }
    if (entry) {
      const cached = this.nodeCache.get(entry.index)
        ?? this.bytesFromActiveArchive(entry.index);
      if (cached) {
        this.log(`[package] re-decoding ${entry.name} with fresh package context`);
        this.decodePayload(entry.index, cached);
      } else {
        // No cached bytes for the selected entry — at least re-bind the
        // renderer so any model already in `this.model()` picks up the
        // new texture map.
        this.applyModelToRenderer();
      }
    } else {
      this.applyModelToRenderer();
    }
  },

  // "path|size" of the opened ZDB's top-level entries, or null when the
  // open file is not a ZDB (a loose map folder sends plain file names).
  packageRootKeys(this: ViewerPageLogic): Set<string> | null {
    const result = this.result();
    if (!result || !/\.(zdb|cdb)$/i.test(result.path ?? '') || result.entries.length === 0) { return null; }
    return new Set(result.entries.map(e => `${rootEntryLeaf(e)}|${e.size}`));
  },

  // Fetch the root archives the package payload did not deliver (once per
  // file). When they have all arrived the package is applied again with
  // them, which re-decodes the model in view.
  requestMissingPackageEntries(this: ViewerPageLogic, unmatched: Set<string>): void {
    if (this.packageFillRequested) { return; }
    const filePath = this.result()?.path;
    if (!filePath) { return; }
    const wanted = (this.result()?.entries ?? []).filter(e => {
      const leaf = rootEntryLeaf(e);
      return e.size > 0 && unmatched.has(`${leaf}|${e.size}`) && isPackageArchiveLeaf(leaf);
    });
    if (wanted.length === 0) { return; }
    this.packageFillRequested = true;
    this.packageFillFetches.clear();
    this.packageFillBytes.clear();
    for (const e of wanted) {
      this.packageFillFetches.set(e.index, rootEntryLeaf(e));
      this.vscode.postMessage({ type: 'readNode', path: filePath, entryIndex: e.index });
    }
    this.log(`[package] ${wanted.length} root archive(s) missing from the package payload; fetching ${wanted.map(rootEntryLeaf).join(', ')}`, true);
  },

  applyPackageFill(this: ViewerPageLogic, entryIndex: number, payload: NodePayload): void {
    const leaf = this.packageFillFetches.get(entryIndex);
    if (!leaf) { return; }
    this.packageFillFetches.delete(entryIndex);
    try {
      const bytes = nodeBytes(payload);
      if (bytes.length > 0) { this.packageFillBytes.set(leaf, bytes); }
    } catch { /* skip undecodable */ }
    if (this.packageFillFetches.size > 0) { return; }
    const nodes: Array<{ index: number; name: string; data: NodePayload }> = [];
    for (const [name, bytes] of this.packageEntryBytes) {
      if (!this.packageFillBytes.has(name)) { nodes.push({ index: nodes.length, name, data: bytes }); }
    }
    for (const [name, bytes] of this.packageFillBytes) { nodes.push({ index: nodes.length, name, data: bytes }); }
    this.log(`[package] ${this.packageFillBytes.size} missing root archive(s) loaded; applying the package again`);
    this.packageFillBytes.clear();
    this.applyZdbPackage(nodes);
  },

  autoEnterLooseMapWorldModel(this: ViewerPageLogic): boolean {
    const filePath = this.result()?.path ?? '';
    const leaf = filePath.split(/[\\/]/).pop() ?? filePath;
    if (!/\.zed$/i.test(leaf)) { return false; }
    if (/_(?:MDL|TXR|PAL|GEO)(?:\([^)]+\))?\.ZED$/i.test(leaf)) { return false; }

    const bytes = this.packageEntryBytes.get('worl_mdl.zed');
    if (!bytes) { return false; }
    try {
      const archive = parseZedArchive(bytes);
      const world = archive.entries.find(e => e.name.toLowerCase() === 'worldmodel')
        ?? archive.entries.find(e => e.size > 0);
      if (!world) { return false; }
      this.archiveStack.set([{ name: 'WORL_MDL.ZED', archive }]);
      this.selectedIndex.set(world.index);
      this.pendingArchiveEntry.set(null);
      this.setStatus(`Loaded root map world (${archive.entries.length} entries).`, false, false);
      this.log(`[package] auto-entered WORL_MDL.ZED / ${world.name}`);
      return true;
    } catch (err) {
      this.log(`[package] failed to auto-enter WORL_MDL.ZED: ${err instanceof Error ? err.message : err}`);
      return false;
    }
  },
};

export type PackageMethods = typeof packageMethods;

// The map prefix among root-level archive names: a <prefix>_TXR.ZED with a
// matching _PAL.ZED that is not a shared support library. Prefers the opened
// file's own stem (M51.ZDB -> m51).
export function mapPrefixFromLeaves(leaves: string[], preferred: string, supportPrefixes: Set<string>): string | null {
  const set = new Set(leaves);
  const hasPair = (p: string) =>
    [...set].some(l => new RegExp(`^${escapeRegExp(p)}_txr(?:\\([^)]+\\))?\\.zed$`, 'i').test(l)) &&
    [...set].some(l => new RegExp(`^${escapeRegExp(p)}_pal(?:\\([^)]+\\))?\\.zed$`, 'i').test(l));
  const want = preferred.toLowerCase();
  if (want && hasPair(want)) { return want; }
  for (const l of leaves) {
    const m = /^(.+?)_txr(?:\([^)]+\))?\.zed$/i.exec(l);
    if (!m) { continue; }
    const p = m[1].toLowerCase();
    if (supportPrefixes.has(p) || /^ld\d+$/i.test(p) || /^z.+\d+$/i.test(p)) { continue; }
    if (hasPair(p)) { return p; }
  }
  return null;
}

// Root archives a map preview needs: model, scene, texture, palette, AI
// map, map config and clutter files.
export function isPackageArchiveLeaf(leaf: string): boolean {
  return /_(?:mdl|geo|txr|pal)(?:\([^)]+\))?\.zed$/i.test(leaf) || /\.mps$/i.test(leaf) || /^[a-z]+\d+\.zed$/i.test(leaf) || /^clutter\.zar$/i.test(leaf);
}
