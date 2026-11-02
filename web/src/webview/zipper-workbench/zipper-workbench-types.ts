// Asset Viewer types: the server's inspect / operation results, browser items, RDR, AI map and decode records.

import { PositionUnit } from './zed-model-parser';

export interface ElementRef<T> { nativeElement: T }

export interface ZipperEntryInfo {
  index: number;
  name: string;
  path: string;
  size: number;
  kind: string;
  extension?: string;
  type?: number;
}

export interface ZipperSaveRecordInfo {
  fieldName: string;
  type: number;
  flags: number;
  count: number;
  valOffset: number;
  tableOffset: number;
  strOffset: number;
}

export interface ZipperInspectResult {
  format: string;
  role: string;
  path: string;
  fileName: string;
  size: number;
  summary: string;
  canExtract: boolean;
  entries: ZipperEntryInfo[];
  apache?: { hasFtScore: boolean; hasZSealEtc: boolean } | null;
  zed?: {
    rootValue: number;
    stringBasePointer: number;
    headerWord5C: number;
    dataAlignment: number;
  } | null;
  save?: {
    title: string;
    decryptedSize: number;
    header: {
      saveType: number;
      slotIndex: number;
      dataSize: number;
      moduleCount: number;
      field5C: number;
      field60: number;
    };
    modules: string[];
    records: ZipperSaveRecordInfo[];
  } | null;
}

export interface ZipperOperationResult {
  outputDirectory?: string;
  outputPath?: string;
  writtenFiles?: string[];
  count?: number;
  section?: string;
  buildVersion?: string;
  publicModulusHex?: string;
  generatedNewKey?: boolean;
}

export type PreviewMode = 'none' | 'texture' | 'model' | 'rdr' | 'mps' | 'animation' | 'zanim' | 'palette';

// One row of the file-manager list: what the editors call an asset (a model, a texture, a palette, a GEO
// definition, a file of the package), not every record of the archive.
export type BrowserItemKind = 'archive' | 'folder' | 'clutter' | 'model' | 'character' | 'weapon' | 'texture' | 'palette' | 'definition' | 'file' | 'record';

export interface BrowserItem {
  entry: ZipperEntryInfo;
  name: string;
  kind: BrowserItemKind;
  // Folder of a package file, visual count, texture size, palette depth …
  detail: string;
  size: number;
}

export interface RdrResult {
  sourcePath: string;
  entry?: string;
  version: number;
  stringPoolSize: number;
  atomCount: number;
  root: unknown;
  fallback?: boolean;
  parseError?: string;
}

export interface MpsMap {
  offset: number;
  name: string;
  width: number;
  height: number;
  minX: number; minY: number; minZ: number;
  maxX: number; maxY: number; maxZ: number;
  scaleX: number; scaleZ: number;
}

export interface Bounds3 {
  minX: number; minY: number; minZ: number;
  maxX: number; maxY: number; maxZ: number;
}

export interface MpsResult {
  sourcePath: string;
  declaredCount: number;
  foundCount: number;
  fileSize: number;
  maps: MpsMap[];
}

export interface MapCameraReference {
  source: string;
  map: MpsMap;
  bounds: Bounds3;
  label: string;
  distance: number;
}

// One node in the collapsible RDR tree. `path` is a unique key (uses dotted
// breadcrumb + index) so expand/collapse state can be tracked across re-renders.
export interface RdrTreeNode {
  path: string;
  name: string;
  kind: 'string' | 'int' | 'float' | 'array' | 'object' | 'leaf';
  value?: string | number;
  children?: RdrTreeNode[];
  depth: number;
}

// Node payloads arrive from the extension host either as base64 (older
// bridge responses, companion files) or as binary (ArrayBuffer / typed
// array, the package and node reads). Normalise at the boundary.
export type NodePayload = string | ArrayBuffer | Uint8Array | null | undefined;

export interface ModelDecodeInfo {
  binding: string;
  visuals: number;
  chunks: number;
  chainBound: number;
  coloured: number;
  unit: PositionUnit;
  unitReason: string;
  renderedChunks: number | null;
  droppedChunks: number;
  missingTextures: string[];
}
