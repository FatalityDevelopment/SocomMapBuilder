// Asset Viewer: replies from the Map Builder server (ViewerHost) — inspect results, node bytes, packages, companions.

import { aliasPalettesForTextureEntries, base64ToBytes, parsePaletteNodes, ZedPalette } from './zed-texture-parser';
import { GeoEntry, parseGeoScene } from './zed-geo-parser';
import type { ViewerPageLogic } from './zipper-workbench-page';
import { namespaceFromArchiveName } from './zipper-workbench-textures';
import type { MpsResult, NodePayload, RdrResult, ZipperInspectResult, ZipperOperationResult } from './zipper-workbench-types';
import { nodeBytes } from './zipper-workbench-util';

export const messageMethods = {
  handleMessage(this: ViewerPageLogic, event: MessageEvent): void {
    const data = event.data as { type: string; [key: string]: unknown };
    switch (data.type) {
      case 'zipperStatus':
        this.setStatus((data['message'] as string) ?? '', false, !!data['busy']);
        break;
      case 'packageEdited':
        void this.restoreLayers(this.layersBeforeEdit, (data['message'] as string) ?? '');
        break;
      case 'zipperError':
        this.setStatus((data['message'] as string) ?? 'Zipper operation failed', true, false);
        break;
      case 'zdbPackageLoading':
        this.packageLoading.set(true);
        this.packageMessage.set((data['message'] as string) ?? 'Loading package...');
        break;
      case 'zipperLoaded':
        this.result.set((data['result'] as ZipperInspectResult) ?? null);
        this.selectedIndex.set(null);
        this.lastOperation.set(null);
        this.nodeCache.clear();
        this.archiveNodesRequestedFor = '';
        this.archiveNodesVersion.update(v => v + 1);
        this.palettes.set(null);
        this.palettesRequested = false;
        this.archiveStack.set([]);
        this.packageEntryBytes.clear();
        this.packageMpsResults.set([]);
        this.directCompanionTextureNamespaces.clear();
        this.companionTextures.set(null);
        this.companionScene.set(null);
        this.packageState.set('idle');
        this.packageBase64Retried = false;
        this.packageFillRequested = false;
        this.packageFillFetches.clear();
        this.packageFillBytes.clear();
        this.mapPoints.set([]);
        this.selectedMapPoint.set('');
        this.rootTextureFetchRequested.clear();
        this.resetPreview();
        this.setStatus(this.result()?.summary ?? 'File loaded.', false, false);
        this.requestPalettesIfNeeded();
        // For ZDB packages, kick off the full-package preload so the model
        // viewer never has to chase per-entry round-trips later.
        this.requestZdbPackageIfNeeded();
        this.requestArchiveNodesIfNeeded();
        this.selectInitialZanimEntryIfNeeded();
        break;
      case 'zdbPackage': {
        const nodes = (data['nodes'] as Array<{ index: number; name: string; type: number; data: NodePayload }>) ?? [];
        const fromCache = !!data['fromCache'];
        if (fromCache) {
          this.log(`[package] restored from extension cache (${nodes.length} entries)`);
        }
        this.applyZdbPackage(nodes);
        break;
      }
      case 'zdbPackageError': {
        const message = (data['message'] as string) ?? 'unknown';
        this.packageLoading.set(false);
        this.packageState.set('failed');
        this.packageMessage.set('Package load failed: ' + message);
        this.log(`[package] ERROR: ${message}`, true);
        // Without the package, a model in view can still get its textures
        // from the root archives on demand.
        this.applyModelToRenderer();
        break;
      }
      case 'archiveNodes': {
        const nodes = (data['nodes'] as Array<{ index: number; name: string; type: number; data: NodePayload }>) ?? [];
        for (const node of nodes) {
          this.nodeCache.set(node.index, nodeBytes(node.data));
        }
        this.archiveNodesVersion.update(v => v + 1);
        this.setStatus(`Loaded ${nodes.length} archive node payload(s).`, false, false);
        if (this.previewMode() === 'zanim') {
          this.previewMessage.set('');
        }
        break;
      }
      case 'archiveNodesError': {
        const message = (data['message'] as string) ?? 'unknown';
        this.setStatus(`Archive node preload failed: ${message}`, true, false);
        if (this.previewMode() === 'zanim') {
          this.previewMessage.set('Archive node preload failed: ' + message);
        }
        break;
      }
      case 'zipperOperation':
        this.lastOperation.set((data['result'] as ZipperOperationResult) ?? null);
        this.setStatus((data['message'] as string) ?? 'Operation complete.', false, false);
        break;
      case 'nodeData': {
        const fetchedIndex = (data['entryIndex'] as number) ?? -1;
        const dataStr = (data['data'] as NodePayload) ?? '';
        // Intercept palette fetches that came from loadNestedPalettesForCurrentLayer.
        if (this.pendingPaletteFetch && this.pendingPaletteFetch.rootEntryIndex === fetchedIndex) {
          const pending = this.pendingPaletteFetch;
          this.pendingPaletteFetch = null;
          try {
            const palBytes = nodeBytes(dataStr);
            const stack = this.archiveStack();
            if (stack.length > 0) {
              this.applyPaletteBytesForCurrentLayer(palBytes, pending.palName, stack[stack.length - 1].archive);
            }
          } catch { /* ignore decode errors */ }
          break;
        }
        // Root archives the package payload did not deliver.
        if (this.packageFillFetches.has(fetchedIndex)) {
          this.applyPackageFill(fetchedIndex, dataStr);
          break;
        }
        // Intercept root-companion (TXR/PAL/GEO) fetches for a nested MDL.
        const compKind = this.rootCompanionFetches.get(fetchedIndex);
        if (compKind) {
          this.rootCompanionFetches.delete(fetchedIndex);
          try {
            const bytes = nodeBytes(dataStr);
            const entry = (this.result()?.entries ?? []).find(e => e.index === fetchedIndex);
            this.applyMdlCompanion(compKind, bytes, entry?.name ?? '(unknown)');
          } catch { /* ignore decode errors */ }
          break;
        }
        this.handleNodeData(fetchedIndex, dataStr);
        break;
      }
      case 'nodeError': {
        const failedIndex = (data['entryIndex'] as number) ?? -1;
        const msg = (data['message'] as string) ?? 'Failed to read entry.';
        // Was it a companion fetch?
        const compKind = this.rootCompanionFetches.get(failedIndex);
        if (compKind) {
          this.rootCompanionFetches.delete(failedIndex);
          this.setStatus(`Companion ${compKind.toUpperCase()} fetch failed: ${msg}`, true, false);
        } else if (this.pendingPaletteFetch?.rootEntryIndex === failedIndex) {
          this.pendingPaletteFetch = null;
          this.setStatus(`Palette fetch failed: ${msg}`, true, false);
        } else {
          this.previewMessage.set(msg);
        }
        this.inflightNode = null;
        break;
      }
      case 'paletteFile':
        if ((data['found'] as boolean) && data['nodes']) {
          const palettes = parsePaletteNodes(data['nodes'] as Array<{ index: number; name: string; data: string }>);
          this.palettes.set(palettes);
          this.paletteMessage.set(`Loaded ${palettes.size} palette(s).`);
          this.refreshPreview();
        } else {
          this.paletteMessage.set((data['message'] as string) ?? 'No sibling _PAL.ZED found.');
        }
        break;
      case 'rdrData': {
        const result = data['result'] as RdrResult | undefined;
        this.rdr.set(result ?? null);
        // Reset expand state so the new tree opens fresh.
        this.rdrExpanded.set(new Set());
        if (result) {
          if (result.fallback) {
            this.previewMessage.set(
              `RDR strict parse failed (${result.parseError ?? 'unknown'}); showing printable-string fallback.`,
            );
            this.log(`[RDR] strict parse failed — fallback string scan. ${result.parseError ?? ''}`);
          } else {
            this.previewMessage.set(
              `RDR v${result.version}: ${result.atomCount} atoms, ${result.stringPoolSize}B strings.`,
            );
            this.log(`[RDR] v${result.version}, atoms=${result.atomCount}, pool=${result.stringPoolSize}B`);
          }
        }
        break;
      }
      case 'rdrError':
        this.previewMessage.set('RDR parse failed: ' + ((data['message'] as string) ?? 'unknown'));
        this.log(`[RDR] ERROR: ${(data['message'] as string) ?? 'unknown'}`);
        break;
      case 'mpsData': {
        const result = data['result'] as MpsResult | undefined;
        this.mps.set(result ?? null);
        if (result) {
          this.previewMessage.set(
            `MPS: ${result.foundCount} / ${result.declaredCount} AI maps found.`,
          );
          this.log(`[MPS] declared=${result.declaredCount} found=${result.foundCount} (${result.fileSize}B)`);
        }
        break;
      }
      case 'mpsError':
        this.previewMessage.set('MPS parse failed: ' + ((data['message'] as string) ?? 'unknown'));
        this.log(`[MPS] ERROR: ${(data['message'] as string) ?? 'unknown'}`);
        break;
      case 'companionFiles': {
        const companions = (data['companions'] as Record<string, { path: string; nodes: Array<{ index: number; name: string; type: number; data: string }> }>) ?? {};
        this.directCompanionTextureNamespaces.clear();
        // PAL. If TXR is also available, add exact aliases from TXR texture ids
        // to the real texpal_NNN records, skipping RGBA32 textures that do not
        // consume a palette.
        if (companions['pal']) {
          let palettes = parsePaletteNodes(companions['pal'].nodes);
          if (companions['txr']) {
            const txrNodes = companions['txr'].nodes;
            palettes = aliasPalettesForTextureEntries(
              palettes,
              txrNodes.map(n => ({ name: n.name })),
              i => base64ToBytes(txrNodes[i].data),
            );
          }
          const namespace = namespaceFromArchiveName(companions['pal'].path) ?? namespaceFromArchiveName(companions['txr']?.path);
          if (namespace) {
            const namespaced = new Map<string, ZedPalette>(palettes);
            for (const [key, value] of palettes) {
              namespaced.set(`${namespace}:${key}`, value);
            }
            palettes = namespaced;
          }
          this.palettes.set(palettes);
        }
        // TXR -> .tif name to texdat bytes map
        if (companions['txr']) {
          const map = new Map<string, Uint8Array>();
          const nodes = companions['txr'].nodes;
          const namespace = namespaceFromArchiveName(companions['txr'].path);
          if (namespace) { this.directCompanionTextureNamespaces.add(namespace); }
          for (let i = 0; i < nodes.length; i++) {
            if (/\.tif$/i.test(nodes[i].name)) {
              for (let j = i + 1; j < Math.min(i + 4, nodes.length); j++) {
                if (/^texdat$/i.test(nodes[j].name)) {
                  const key = nodes[i].name.toLowerCase();
                  const bytes = base64ToBytes(nodes[j].data);
                  map.set(key, bytes);
                  if (namespace) { map.set(`${namespace}:${key}`, bytes); }
                  break;
                }
                if (/\.tif$/i.test(nodes[j].name)) { break; }
              }
            }
          }
          this.companionTextures.set(map);
        }
        // GEO -> scene tree
        if (companions['geo']) {
          const scene = parseGeoScene(companions['geo'].nodes as GeoEntry[]);
          this.companionScene.set(scene);
        }
        this.refreshPreview();
        break;
      }
    }
  },
};

export type MessageMethods = typeof messageMethods;
