// What the VS Code extension used to do for the editor pages, done in the browser: prompts for paths and names (the
// server reads and writes files by path), file pickers when only the contents are needed, downloads for exports, and
// switching between pages (Map ↔ Models ↔ Animations ↔ Viewer).

import { signal } from './signals';

export type PageId = 'map' | 'models' | 'animations' | 'viewer';

export interface DialogField {
  key: string;
  label: string;
  value: string;
  // Submit is refused until the value matches (hint explains why).
  pattern?: RegExp;
  hint?: string;
  // Left empty is allowed (the caller picks a default).
  optional?: boolean;
}

export interface DialogRequest {
  title: string;
  submitLabel: string;
  fields: DialogField[];
  resolve: (values: Record<string, string> | null) => void;
}

// The dialog on screen (rendered by HostDialog in the app shell).
export const activeDialog = signal<DialogRequest | null>(null);

export interface FileFilter { label: string; extensions: string[] }

export interface BrowseOptions {
  mode: 'open' | 'save' | 'folder';
  title: string;
  submitLabel?: string;
  // File types offered in the filter list (the first is selected); "All files" is always added.
  filters?: FileFilter[];
  // Folder (or file) to start in; otherwise the last folder used for the same key.
  start?: string;
  // Save mode: the suggested file name.
  fileName?: string;
  // Open mode: a folder can be chosen too (unpacked package folders).
  allowFolder?: boolean;
  // Groups remembered folders (packages, exports, extracts …).
  key?: string;
}

export interface BrowseRequest extends BrowseOptions { resolve: (path: string | null) => void }

// The file browser on screen (rendered by FileBrowser in the app shell).
export const activeBrowse = signal<BrowseRequest | null>(null);

// What an import copied (the server's importModel answer).
export interface ImportResult {
  archive: string;
  name: string;
  models: string[];
  textures: string[];
  reusedTextures: string[];
  renamedTextures: Record<string, string>;
  palettes: Record<string, number>;
  warnings: string[];
}

// What a texture import copied (the server's importTexture answer).
export interface TextureImportResult {
  archive: string;
  texture: string;
  textures: string[];
  reusedTextures: string[];
  palettes: Record<string, number>;
  warnings: string[];
}

export interface ImportRequest {
  // The asset session that receives the model or texture.
  targetSession: string;
  mode: 'model' | 'texture';
  // Pre-selects the model kind filter (the Map page asks for props).
  kind?: string;
  resolve: (result: unknown) => void;
}

// The import dialog on screen (rendered by ImportDialog in the app shell).
export const activeImport = signal<ImportRequest | null>(null);

// A prompt, the file browser or the import dialog is up: pages leave the keyboard alone.
export function modalOpen(): boolean {
  return !!activeDialog() || !!activeBrowse() || !!activeImport();
}

export const PACKAGE_FILTERS: FileFilter[] = [
  { label: 'SOCOM packages and archives', extensions: ['zdb', 'cdb', 'zar', 'zed'] },
  { label: 'GTA: San Andreas disc or IMG', extensions: ['iso', 'img'] },
];

const LAST_FOLDER = 'mapbuilder.lastFolder.';

export function rememberedFolder(key: string): string {
  try { return localStorage.getItem(LAST_FOLDER + key) ?? ''; } catch { return ''; }
}

export function rememberFolder(key: string, folder: string): void {
  try { localStorage.setItem(LAST_FOLDER + key, folder); } catch { /* storage unavailable */ }
}

const dirOf = (p: string) => p.replace(/[\\/][^\\/]*$/, '');
const baseOf = (p: string) => p.split(/[\\/]/).pop() ?? p;

// The page in front, and requests for another page to open something.
export const activePage = signal<PageId>('map');

export interface OpenRequest { page: PageId; path: string; model?: { archive: string; name: string }; seq: number }
export const openRequest = signal<OpenRequest | null>(null);
let openSeq = 0;

const LAST_PACKAGE = 'mapbuilder.lastPackage';

function remembered(): string {
  try { return localStorage.getItem(LAST_PACKAGE) ?? ''; } catch { return ''; }
}

function remember(path: string): void {
  try { localStorage.setItem(LAST_PACKAGE, path); } catch { /* storage unavailable */ }
}

// Trims a typed path and drops the quotes Explorer's "Copy as path" adds.
export function cleanPath(value: string | undefined | null): string {
  return (value ?? '').trim().replace(/^"(.*)"$/, '$1');
}

export const host = {
  prompt(title: string, fields: DialogField[], submitLabel = 'OK'): Promise<Record<string, string> | null> {
    return new Promise(resolve => {
      activeDialog.set({
        title,
        submitLabel,
        fields,
        resolve: values => { activeDialog.set(null); resolve(values); },
      });
    });
  },

  // The file browser: a file to open, a folder, or where to save. Resolves to the chosen path (null on cancel).
  browse(options: BrowseOptions): Promise<string | null> {
    return new Promise(resolve => {
      activeBrowse.set({
        ...options,
        resolve: path => {
          activeBrowse.set(null);
          if (path) { rememberFolder(options.key ?? options.mode, options.mode === 'folder' ? path : dirOf(path)); }
          resolve(path);
        },
      });
    });
  },

  // A package for ASSET_OPEN: a SOCOM .ZDB / .CDB / .ZAR / .ZED or an unpacked package folder; GTA: San Andreas .ISO / .IMG.
  async pickPackage(title = 'Open package'): Promise<string | null> {
    const path = await host.browse({ mode: 'open', title, filters: PACKAGE_FILTERS, allowFolder: true, key: 'package', start: remembered() || undefined, submitLabel: 'Open' });
    if (path) { remember(path); }
    return path;
  },

  // Where to save: a .ZDB / .CDB copy, a folder, or a memory-card pack (name, then its folder).
  async pickSavePath(kind: 'zdb' | 'folder' | 'pack', suggested?: string): Promise<{ path: string | null; packName: string | null }> {
    if (kind === 'pack') {
      const v = await host.prompt('Export memory-card pack', [
        { key: 'packName', label: 'Pack name', value: suggested ?? 'MEDIT', pattern: /^m[a-z0-9_]{1,6}$/i, hint: 'Start with M, at most 7 letters/digits (the game mounts mc0:/BASCUS-97275<NAME>/<NAME>.CDB)' },
      ], 'Next');
      if (!v) { return { path: null, packName: null }; }
      const packName = v['packName'].trim();
      const folder = await host.browse({ mode: 'folder', title: `Folder for the ${packName} pack`, key: 'pack', submitLabel: 'Export here' });
      return folder ? { path: folder, packName } : { path: null, packName: null };
    }
    if (kind === 'folder') {
      return { path: await host.browse({ mode: 'folder', title: 'Choose a folder', start: suggested, key: 'folder' }), packName: null };
    }
    // A .CDB pack saves as a .CDB; anything else as a .ZDB.
    const cdb = /\.cdb$/i.test(suggested ?? '');
    const path = await host.browse({
      mode: 'save',
      title: 'Save the edited package as',
      filters: [cdb ? { label: 'Memory-card pack', extensions: ['cdb'] } : { label: 'SOCOM II package', extensions: ['zdb'] }],
      start: suggested ? dirOf(suggested) : undefined,
      fileName: suggested ? baseOf(suggested) : undefined,
      key: 'save',
      submitLabel: 'Save',
    });
    return { path, packName: null };
  },

  // Copies a model (MDL, GEO, textures, palettes) from another package into the target session (importModel).
  importModel(targetSession: string, kind?: string): Promise<ImportResult | null> {
    return new Promise(resolve => {
      activeImport.set({ targetSession, mode: 'model', kind, resolve: result => { activeImport.set(null); resolve(result as ImportResult | null); } });
    });
  },

  // Copies a texture (mip levels, palette) from another package into the target session (importTexture).
  importTexture(targetSession: string): Promise<TextureImportResult | null> {
    return new Promise(resolve => {
      activeImport.set({ targetSession, mode: 'texture', resolve: result => { activeImport.set(null); resolve(result as TextureImportResult | null); } });
    });
  },

  // An image of any type the browser reads (PNG, JPG, BMP, GIF, WebP), as PNG: the server takes PNG only.
  pickImage(): Promise<{ name: string; base64: string; width: number; height: number } | null> {
    return new Promise(resolve => {
      const input = document.createElement('input');
      input.type = 'file';
      input.accept = 'image/*';
      input.onchange = async () => {
        const file = input.files?.[0];
        if (!file) { resolve(null); return; }
        try {
          const bitmap = await createImageBitmap(file);
          const canvas = document.createElement('canvas');
          canvas.width = bitmap.width;
          canvas.height = bitmap.height;
          canvas.getContext('2d')!.drawImage(bitmap, 0, 0);
          const base64 = canvas.toDataURL('image/png').replace(/^data:image\/png;base64,/, '');
          resolve({ name: file.name, base64, width: bitmap.width, height: bitmap.height });
        } catch {
          resolve(null);
        }
      };
      input.addEventListener('cancel', () => resolve(null));
      input.click();
    });
  },

  async askText(title: string, value: string, pattern?: string, hint?: string): Promise<string | null> {
    const v = await host.prompt(title, [{ key: 'text', label: 'Name', value, pattern: pattern ? new RegExp(pattern) : undefined, hint }]);
    return v ? v['text'] : null;
  },

  // A local file's contents (images, OBJ text): the browser reads it, no path needed.
  pickFile(accept: string, as: 'text' | 'base64'): Promise<{ name: string; text?: string; base64?: string } | null> {
    return new Promise(resolve => {
      const input = document.createElement('input');
      input.type = 'file';
      input.accept = accept;
      input.onchange = async () => {
        const file = input.files?.[0];
        if (!file) { resolve(null); return; }
        if (as === 'text') { resolve({ name: file.name, text: await file.text() }); return; }
        const bytes = new Uint8Array(await file.arrayBuffer());
        let bin = '';
        for (let i = 0; i < bytes.length; i += 0x8000) { bin += String.fromCharCode(...bytes.subarray(i, i + 0x8000)); }
        resolve({ name: file.name, base64: btoa(bin) });
      };
      input.addEventListener('cancel', () => resolve(null));
      input.click();
    });
  },

  // Exports (OBJ, PNG, JSON): a browser download.
  saveFile(suggested: string, content: { text?: string; base64?: string }, mime = 'application/octet-stream'): string {
    const name = suggested.split(/[\\/]/).pop() || 'export';
    const blob = content.text !== undefined
      ? new Blob([content.text], { type: mime })
      : new Blob([Uint8Array.from(atob(content.base64 ?? ''), c => c.charCodeAt(0))], { type: mime });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = name;
    a.click();
    setTimeout(() => URL.revokeObjectURL(url), 10_000);
    return name;
  },

  // Brings another page to the front and asks it to open a package (and select a model).
  open(page: PageId, path: string, model?: { archive: string; name: string }): void {
    activePage.set(page);
    openRequest.set({ page, path, model, seq: ++openSeq });
  },

  log(text: string): void {
    console.debug(`[Map Builder] ${text}`);
  },
};
