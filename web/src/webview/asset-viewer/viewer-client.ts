// The Asset Viewer's server calls: VIEWER_LIST (what a folder, file or archive holds), VIEWER_PREVIEW (a typed
// preview of one file or record), VIEWER_EXTRACT, and PACKAGE_EDIT for Import / Replace / Remove. Models, textures,
// maps and clips are previewed through the asset session a listing names, with the editors' AssetClient.

import { command } from '../../api/client';

export type ItemKind =
  | 'folder' | 'disc' | 'level' | 'package' | 'models' | 'textures' | 'palettes' | 'geo' | 'clips' | 'archive' | 'group'
  | 'model' | 'texture' | 'palette' | 'clip'
  | 'rdr' | 'mps' | 'sound' | 'bank' | 'movie' | 'image' | 'icon' | 'iconsys' | 'program' | 'text' | 'binary';

/** A place: a path on disk and the entry indices that lead into it. */
export interface Place { path: string; inner: number[] }

export interface ItemRef {
  // Models: archive + name; textures: name + namespace; palettes: archive + id; clips: name + source.
  archive?: string; name?: string; modelKind?: string; namespace?: string | null; id?: number; source?: string;
}

export interface ViewerItem extends Place {
  name: string;
  kind: ItemKind;
  size: number;
  detail?: string;
  entryPath?: string | null;
  ref?: ItemRef;
  // Folder listings say per file whether it can be edited; records follow their listing's `editable`.
  editable?: boolean;
}

export interface Crumb extends Place { label: string; entryPath?: string | null }

export interface Listing extends Place {
  name: string;
  kind: ItemKind;
  size: number;
  session: string | null;
  package: string | null;
  crumbs: Crumb[];
  items: ViewerItem[];
  // Whether this listing's records can be edited, the session holding edits to its package (if any), and that package.
  editable: boolean;
  editSession: string | null;
  editPackage: string;
}

export type Fact = [string, string];

/** An RDR atom: [atom, 'list', count, children] or [atom, 'int' | 'float' | 'string' | other, value]. */
export type RdrTree = [number, 'list', number, RdrTree[]] | [number, string, string | number];

/** The RDR tree's root row. Rows are keyed by their path of list indices ("r/0/3"): a duplicate (added at the list's
 *  end) and value edits leave those alone; a remove shifts the later siblings (AssetViewerLogic.removeRdr). */
export const RDR_ROOT = 'r';

export interface MpsPoint { name: string; team: number; kind: string; x: number; z: number; cellX: number; cellZ: number }

/** A GEO scene node: its nparams record (transform column-major, bounds, kind, flags) and model reference. */
export interface GeoNode {
  name: string; nparams: number; matrix: number[]; min: number[]; max: number[]; kind: number; flags: number;
  modelName: { node: number; text: string } | null; visuals: number; children: GeoNode[];
}

export interface ClutterPlacement { model: string; position: number[]; yaw: number; scale: number; tilted: boolean; layout: string }

export interface ZanimField { name: string; kind: 'f32' | 'i32' | 'u16' | 'object' | 'name' | 'vec3' | 'bit' | 'bool16'; value: number | number[] | boolean; shown: string; readOnly: boolean }
export interface ZanimInstruction { offset: number; op: number; name: string; depth: number; condition: boolean; words: string[]; fields: ZanimField[] }
export interface ZanimEffect {
  name: string; set: string; sequence: number; names: string[]; objects: Array<{ value: number; label: string }>;
  tracks: Array<{ target: string; instructions: ZanimInstruction[] }>;
}

export type Preview = { name: string; facts: Fact[] } & (
  | { type: 'folder' | 'info' }
  | { type: 'movie'; canConvert: boolean; url: string | null }
  | { type: 'archive'; kind: ItemKind }
  | { type: 'package'; session: string; package: string; hasMap: boolean; mapNamespace: string | null; models: number; characters: number }
  | { type: 'rdr'; tree: RdrTree }
  | { type: 'mps'; maps: Array<{ name: string; width: number; height: number; min: number[]; max: number[]; scale: number[]; offset: number }>; points: { points: MpsPoint[] } | null; session: string | null }
  | { type: 'iconsys'; line1: string; line2: string; icons: string[] }
  | { type: 'audio'; rate: number; seconds: number; wav: string | null; sample?: number; samples?: Array<{ offset: number; length: number; seconds: number }> }
  | { type: 'image'; width: number; height: number; png: string }
  | { type: 'icon'; mesh: unknown; png: string | null }
  | { type: 'program'; sections: Array<{ name: string; type: string; address: string; size: number }> }
  | { type: 'text'; text: string; truncated: boolean }
  | { type: 'hex'; kind: ItemKind; note: string | null; bytes: string; strings: string[] }
  | { type: 'palette'; rgba: number[]; is16: boolean }
  | { type: 'geo'; models: GeoNode[] }
  | { type: 'clutter'; session: string | null; placements: ClutterPlacement[]; models: string[] }
  | { type: 'zanim'; effects: Array<{ name: string; set: string; sequence: number; tracks: number; instructions: number; spawns: string[] }>; effect: ZanimEffect | null }
);

const json = (value: unknown) => JSON.stringify(value);

export function list(place: Place): Promise<Listing> {
  return command('VIEWER_LIST', { InputPath: place.path, AssetJson: json({ inner: place.inner }) });
}

export function preview(place: Place, options: { sample?: number; rate?: number; effect?: number } = {}): Promise<Preview> {
  return command('VIEWER_PREVIEW', { InputPath: place.path, AssetJson: json({ inner: place.inner, ...options }) });
}

export function palette(session: string, archive: string, id: number): Promise<Preview> {
  return command('VIEWER_PREVIEW', { AssetJson: json({ session, archive, palette: id }) });
}

/** Converts a .PSS to MP4 (once; it is cached) and answers where the page can stream it. */
export function convertMovie(path: string): Promise<{ url: string; size: number; silent: boolean }> {
  return command('VIEWER_MOVIE', { InputPath: path });
}

export interface EditStatus { session: string; path: string; isZdb: boolean; revision: number; dirty: string[]; canUndo: string | null; canRedo: string | null }

/** Every open asset session's status (the viewer's and the editors'), to find unsaved edits in other packages. */
export function sessions(): Promise<Array<Partial<EditStatus>>> {
  return command('ASSET_SESSIONS');
}

/** One VIEWER_EDIT on a file or record (rdrSet, rdrDuplicate, rdrRemove, mpsMap, text, bytes, image, iconSys, nparams,
 *  modelName, zanimField, zanimWord). */
export function edit(place: Place, op: Record<string, unknown>): Promise<{ session: string; package: string; result: unknown; status: EditStatus }> {
  return command('VIEWER_EDIT', { InputPath: place.path, AssetJson: json({ ...op, inner: place.inner }) });
}

export function extract(place: Place, outputPath: string): Promise<{ written: string; size: number }> {
  return command('VIEWER_EXTRACT', { InputPath: place.path, OutputPath: outputPath, AssetJson: json({ inner: place.inner }) });
}

export interface EditReport { written: string; added: string[]; replaced: string[]; removed: string[] }

export function packageEdit(path: string, output: string, ops: unknown[]): Promise<EditReport> {
  return command('PACKAGE_EDIT', { InputPath: path, OutputPath: output, Data: json(ops) });
}

export const samePlace = (a: Place | null | undefined, b: Place | null | undefined) =>
  !!a && !!b && a.path.toLowerCase() === b.path.toLowerCase() && a.inner.length === b.inner.length && a.inner.every((v, i) => v === b.inner[i]);

export const placeKey = (p: Place, ref?: ItemRef) => `${p.path}|${p.inner.join('/')}|${ref ? `${ref.archive ?? ''}:${ref.name ?? ref.id ?? ''}:${ref.source ?? ''}` : ''}`;
