// Client for asset sessions (ASSET_* commands). Every query, edit and save a page makes goes through here to the
// Map Builder server (POST /api/command), so every page works on the same session.
// (CodeDesigner's copy of this file posts the same commands to the VS Code extension instead.)

import { command as postCommand } from '../../api/client';

export interface SessionStatus {
  session: string;
  path: string;
  isZdb: boolean;
  mapNamespace: string | null;
  revision: number;
  dirty: string[];
  canUndo: string | null;
  canRedo: string | null;
  entries: number;
  models: Record<string, number>;
  // Set for packages from another game. CA supports a subset of Map Builder edits.
  game?: string;
  initialModel?: string | null;
  readOnly?: boolean;
  // The last autosave (a recovery copy of unsaved edits), if any.
  autosave?: { path: string; revision: number; at: string } | null;
  // SOCOM 3 / CA: the location the archive holds, its missions and the one whose points ASSET_MAP_POINTS returns.
  location?: string | null;
  mission?: string | null;
  packageKind?: string;
  missions?: MissionSummary[];
}

// kind: SP (campaign), MP (online), IA (instant action), TR (training) or other; mode: the letters after the number.
export interface MissionSummary { id: string; kind: string; mode: string }

export interface ModelSummary {
  archive: string;
  name: string;
  kind: string;
  namespace: string;
  size: number;
  visualCount: number;
}

export interface TextureInfo {
  name: string;
  found: boolean;
  namespace?: string;
  width?: number;
  height?: number;
  psm?: string;
  bpp?: number;
  palette?: number | null;
  paletteResolved?: boolean;
  hasTransparency?: boolean;
  alphaTest?: number | null;
  alphaBlend?: boolean | null;
  clamp?: [boolean, boolean] | null;
}

export interface ChunkInfo {
  index: number;
  offset: number;
  visual: string | null;
  texture: string | null;
  binding: string | null;
  vertices: number;
  triangles: number;
  scale: number;
  origin: [number, number, number];
  vertexColors: boolean;
}

export interface GeoInfo {
  name: string;
  index: number;
  transform: number[];
  boundsMin: number[] | null;
  boundsMax: number[] | null;
  vparamsFlags: string | null;
  positionUnit: string | null;
  visuals: number;
  reference: string | null;
  children: string[];
}

export interface ModelDescription {
  revision?: number;
  gta?: GtaModelInfo | null;
  archive: string;
  name: string;
  kind: string;
  namespace: string;
  size: number;
  nodeIndex: number;
  textureNamespace: string | null;
  geoArchive: string | null;
  fromChains: boolean;
  skinned?: boolean;
  skeleton?: { name: string }[];
  visuals: string[];
  chunks: ChunkInfo[];
  textures: TextureInfo[];
  geo: GeoInfo | null;
}

export interface GtaModelInfo {
  textureChoices: Array<{ name: string; dictionary: string }>;
  frames: Array<{ index: number; name: string; position: number[] }>;
  materials: Array<{ geometry: number; index: number; color: number[]; texture: string | null; textureCapacity: number }>;
  geometryCount: number;
}

export interface EditResult {
  revision: number;
  description?: string;
  changed: string[];
}

export interface MapInstanceWire {
  caNode?: boolean;
  multiMove?: boolean;
  kind: string;
  model: string;
  instance: string;
  path: string;
  position: number[];
  scale: number;
  matrix: number[];
  geoIndex: number | null;
  clutterIndex: number | null;
  source: string | null;
}

// ASSET_VALIDATE: a map lint. A problem names the item it is about (a placed node, a clutter placement, a point).
export interface MapProblem {
  severity: 'error' | 'warning' | 'info';
  code: string;
  message: string;
  geoIndex?: number | null;
  clutterIndex?: number | null;
  point?: string;
  texture?: string;
}

export interface MapValidation {
  ok: boolean;
  counts: Partial<Record<MapProblem['severity'], number>>;
  instances: number;
  textures: number;
  textureBytes: number;
  problems: MapProblem[];
}

export interface MapSceneResult {
  sceneNamespace: string;
  counts: Record<string, number>;
  returned: number;
  instances: MapInstanceWire[];
  // Star Wars: Battlefront: AI and spawn paths (world positions) and regions (a shape placed by a matrix).
  paths?: MapPathWire[];
  regions?: MapRegionWire[];
}

/** kind: spawn (a command post's spawn path), boundary (the map's edge, a closed loop) or path. */
export interface MapPathWire { name: string; kind: string; closed: boolean; nodes: number[][]; commandPost?: string | null }
/** shape: box (size = half extents), sphere (size[0] = radius) or cylinder (radii size[0] / size[2], half height
 *  size[1]); role: capture, control, sound or region; matrix as the instances' (translation in 12..14). */
export interface MapRegionWire { name: string; shape: string; role: string; matrix: number[]; size: number[]; commandPost?: string | null }

// y: the floor of the point's cell in the AI grid (null where the grid has no cell).
export interface MapPointWire {
  name: string; team: number; kind: string; x: number; y?: number | null; z: number; cellX: number; cellZ: number;
  island?: string | null; faction?: string; editable?: boolean; canMove?: boolean;
}

export interface AnimationSummary { name: string; source: string; editable: boolean; duration: number; frames: number; samples: number; fps: number; bones: number }

export interface WeaponSummary { internalName: string; model: string; archive: string; fireAnimName: string; fireWait: number; tracerTextureName: string }

export interface FxInstruction {
  op: string;
  name: string;
  depth: number;
  condition: boolean;
  flags: number;
  fields: Record<string, unknown>;
  words: string;
}

export interface FxNode {
  name: string;
  found: boolean;
  set?: string;
  names?: string[];
  models?: Array<{ archive: string; name: string }>;
  textures?: string[];
  nodeRefs?: Array<{ name: string; flags: number; parent: number }>;
  tracks?: Array<{ target: string; headerFlags?: string; instructions: FxInstruction[] }>;
  spawns?: FxNode[] | null;
}

export interface WeaponFx {
  weapon: { internalName: string; modelName: string; fireAnimName: string; fireWait: number; tracerTextureName: string; fields: Record<string, unknown> };
  effect: FxNode | null;
}

export interface CharacterPresetSummary { name: string; description?: string | null; shipped: boolean; ops?: string[]; error?: string }
export interface PrefabSummary { name: string; description?: string | null; items?: number; models?: string[]; error?: string }
export interface PrefabDetail { name: string; description?: string | null; items: Array<{ kind: 'reference' | 'clutter'; model: string; matrix: number[] }> }

export interface AnimationList { packageArchive: string | null; sharedPath: string | null; clips: AnimationSummary[] }

export type ActionFields = Record<string, (string | number)[]>;
export interface MapActions {
  revision: number;
  hasReader: boolean;
  /** turret: the node places a turret model, which the game gives Mount / Dismount prompts without an action row. */
  targets: { index: number; name: string; path: string; model: string; faces: number[]; anchor?: number[] | null; turret?: boolean }[];
  rows: ActionFields[];
  tables: string[];
  valves: string[];
  animations: string[];
  icons: string[];
}

/** A path into a mission table (field names and item numbers), as ASSET_MAP_TABLES answers it, for mapTable edits. */
export type TablePath = Array<string | number>;
export interface MapObjective {
  row: number; path: TablePath; name: string | null; descriptions: string[];
  valve: string | null; displayValve: string | null; invalidValve: string | null;
  location: [number, number] | null; animation: string | null; navPoints: string[]; secondary: boolean; subObjective: boolean;
}
export interface MapActor {
  row: number; path: TablePath; name: string | null; display: string | null; character: string | null;
  /** "archive:name" of the character model its chartype draws as, when the package has it. */
  model: string | null;
  /** The chartype's default weapons (READERC.ZAR > character.rdr) with the package model each draws as; null when no
   * chartype table was found or the type is not in it. */
  weapons: Array<{ name: string; model: string | null }> | null;
  /** "archive:name" of the weapon the chartype holds (its first gun the package has), or null. */
  weapon: string | null;
  /** Its chartype's animation set (character.rdr) and that set's Stand clip (animset.rdr), when known. */
  animSet: string | null;
  stand: string | null;
  starts: Array<{ point: string; value: number | string | null }>; teams: number[];
  flags: Array<{ name: string; values: Array<number | string | null> }>;
  ai: Array<{ name: string; value: number | string | null }> | null;
}
export interface MapTables {
  missionId?: string | null;
  revision: number;
  hasReader: boolean;
  objectives: MapObjective[];
  actors: MapActor[];
  /** Where the chartype loadouts came from (a READERC.ZAR path, or the package's own table), or null. */
  loadoutSource: string | null;
  mission: {
    description: string | null; satchelTimer: number | null; elevation: { min: number | null; max: number | null } | null;
    ai: Array<{ name: string; value: number | string | null }>; valves: Array<{ index: number; name: string | null; value: number | string | null }>;
    // SOCOM 3 / CA: the briefing key, the map screen image and the weapons the mission allows (by name).
    briefing?: string | null; mapImage?: string | null; weapons?: string[];
    weaponSlots?: Array<{ index: number; hash: number; name: string }>;
    weaponChoices?: Array<{ hash: number; name: string }>;
  } | null;
  world: {
    record: string; metersPerUnit: number | null; defaultMaterial: string | null; nightMission: number | null; nightVision: number[] | null;
    shadow: number[] | null; ambient: number[] | null; lights: Array<{ name: string; rgb: number[] | null; dir: number[] | null; active: boolean }>;
    clip: number[] | null; fog: { enabled: boolean; range: number[] | null; rgb: number[] | null } | null;
  } | null;
  extents: { min: number[] | null; max: number[] | null; floors: number | null } | null;
  cams: Array<{ id: string | null; description: string | null; animation: string | null }>;
  valves: Array<{ name: string | null; id: number | null; value: number | null }>;
  /** In-game text of the message keys used above. */
  texts: Record<string, string>;
}

/** A HUD texture as ASSET_MAP_HUD sends it. */
export interface HudImage { width: number; height: number; png: string }
/** The game's HUD art for the minimap (RUN\COMMON\HUD2_TXR.ZED) and the map's tactical map, when it has one. */
export interface MapHud {
  revision: number;
  map: string | null;
  icons: Record<string, HudImage>;
  /** HUD textures the package does not have (drawn with stand-ins). */
  missing: string[];
  /** map_<map>_1_1 and _1_2 side by side; world = [x left, x right, z top, z bottom], null when not measured yet. */
  tacmap: { name: string; tiles: Array<HudImage | null>; world: [number, number, number, number] | null } | null;
}

export class AssetClient {
  session: string | null = null;

  private call<T>(command: string, fields: Record<string, string | undefined> = {}): Promise<T> {
    return postCommand<T>(command, fields, this.session);
  }

  private withJson<T>(command: string, json: unknown, fields: Record<string, string | undefined> = {}): Promise<T> {
    return this.call<T>(command, { ...fields, AssetJson: JSON.stringify(json) });
  }

  async open(path: string): Promise<SessionStatus> {
    const result = await this.call<{ status: SessionStatus }>('ASSET_OPEN', { InputPath: path });
    this.session = result.status.session;
    return result.status;
  }

  status(): Promise<SessionStatus> { return this.call('ASSET_STATUS'); }

  // The undo and redo steps (most recent first), for the History list.
  history(): Promise<{ revision: number; undo: Array<{ description: string; archives: string[] }>; redo: Array<{ description: string; archives: string[] }>; dirty: string[] }> {
    return this.call('ASSET_HISTORY');
  }

  listModels(filter: { kind?: string; query?: string; archive?: string } = {}): Promise<ModelSummary[]> {
    return this.withJson('ASSET_LIST_MODELS', filter);
  }

  describe(archive: string, name: string): Promise<ModelDescription> {
    return this.call('ASSET_DESCRIBE_MODEL', { AssetArchive: archive, AssetName: name });
  }

  texture(name: string, namespace?: string | null): Promise<{ info: TextureInfo; png: string }> {
    return this.call('ASSET_TEXTURE', { AssetName: name, AssetArchive: namespace ?? undefined });
  }

  // The editable mesh (model space, base64 arrays): see mesh-data.ts.
  mesh(archive: string, name: string): Promise<unknown> {
    return this.call('ASSET_MODEL_MESH', { AssetArchive: archive, AssetName: name, AssetJson: JSON.stringify({ arrays: true }) });
  }

  // The composed map: worldmodel, GEO references, sky and clutter placements (model space).
  mapScene(filter: { kind?: string; query?: string; limit?: number; includeUnresolved?: boolean } = {}): Promise<MapSceneResult> {
    return this.withJson('ASSET_MAP_SCENE', { limit: 100000, ...filter });
  }

  // A SOCOM 3 / CA session reads one mission's points; naming one switches the session to it.
  mapPoints(mission?: string): Promise<{ points: MapPointWire[]; mission?: string | null; teamSpawns?: MapPointWire[] }> {
    return mission ? this.withJson('ASSET_MAP_POINTS', { mission }) : this.call('ASSET_MAP_POINTS');
  }
  mapActions(): Promise<MapActions> { return this.call('ASSET_MAP_ACTIONS'); }
  mapTables(): Promise<MapTables> { return this.call('ASSET_MAP_TABLES'); }
  mapHud(): Promise<MapHud> { return this.call('ASSET_MAP_HUD'); }

  validate(vramBudget?: number): Promise<MapValidation> { return this.withJson('ASSET_VALIDATE', { vramBudget }); }

    // Character animation clips: the package's MOTION_S.ZAR (editable) and the shared RUN/MOTION_P.ZAR.
  listAnimations(filter: { query?: string; source?: string } = {}): Promise<AnimationList> {
    return this.withJson('ASSET_LIST_ANIMATIONS', filter);
  }

  animation(name: string, source?: string): Promise<unknown> {
    return this.call('ASSET_ANIMATION', { AssetName: name, AssetArchive: source });
  }

    // Weapons in the package (zweapon.rdr) and what firing one plays (its ZANIM effect tree).
  weapons(): Promise<WeaponSummary[]> { return this.call('ASSET_WEAPONS'); }

  weaponFx(weapon: string): Promise<WeaponFx> { return this.call('ASSET_WEAPON_FX', { AssetName: weapon }); }

  entryBytes(leaf: string): Promise<{ leaf: string; revision: number; data: string }> {
    return this.call('ASSET_ENTRY_BYTES', { AssetArchive: leaf });
  }

  // Character presets: saved body shapes and gear sets, applied with the applyCharacterPreset op.
  listCharacterPresets(): Promise<{ folder: string; presets: CharacterPresetSummary[] }> { return this.call('ASSET_LIST_CHARACTER_PRESETS'); }
  // Prefabs: groups of placed props and clutter saved relative to an origin, placed again with the placePrefab op.
  savePrefab(args: { name: string; geoIndices?: number[]; clutterIndices?: number[]; description?: string; replace?: boolean }): Promise<{ name: string; items: number; models: string[] }> {
    return this.withJson('ASSET_SAVE_PREFAB', args);
  }

  listPrefabs(): Promise<{ folder: string; prefabs: PrefabSummary[] }> { return this.call('ASSET_LIST_PREFABS'); }

  prefab(name: string): Promise<PrefabDetail> { return this.call('ASSET_LIST_PREFABS', { AssetName: name }); }

  deletePrefab(name: string): Promise<{ deleted: string }> { return this.call('ASSET_DELETE_PREFAB', { AssetName: name }); }

  edit(op: Record<string, unknown>): Promise<EditResult> { return this.withJson('ASSET_EDIT', op); }

  undo(): Promise<EditResult> { return this.call('ASSET_UNDO'); }

  redo(): Promise<EditResult> { return this.call('ASSET_REDO'); }

  save(args: { mode: 'zdb' | 'pack' | 'dff' | 'img'; outputPath?: string; packName?: string; folderCard?: string; archive?: string; model?: string }): Promise<{ written: string[]; leaves: string[]; revision?: number }> {
    return this.withJson('ASSET_SAVE', args);
  }

  /** Converts a model to the other game (SOCOM 3 / CA -> SOCOM II pack, or the reverse); answers where it went. */
  exportModel(archive: string, name: string, args: { pack?: string; modelName?: string; outputDir?: string; clips?: string[] }): Promise<ExportResult> {
    return this.withJson('ASSET_EXPORT', args, { AssetArchive: archive, AssetName: name });
  }
}

export interface ExportResult {
  target: string;
  path: string;
  pack: string;
  model: string;
  kind: string;
  library: string;
  triangles: number;
  textures: number;
  palettes: number;
  clips: string[];
  warnings: string[];
  open: { archive: string; name: string } | null;
}

export function base64ToBytes(value: string): Uint8Array {
  const bin = atob(value);
  const out = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) { out[i] = bin.charCodeAt(i); }
  return out;
}

// A PNG (base64) to top-down RGBA, unpremultiplied, as the bridge decoded it.
export async function pngToRgba(base64: string): Promise<{ width: number; height: number; rgba: Uint8Array }> {
  const blob = new Blob([base64ToBytes(base64)], { type: 'image/png' });
  const bitmap = await createImageBitmap(blob, { premultiplyAlpha: 'none', colorSpaceConversion: 'none' });
  const canvas = document.createElement('canvas');
  canvas.width = bitmap.width;
  canvas.height = bitmap.height;
  const ctx = canvas.getContext('2d', { willReadFrequently: true })!;
  ctx.drawImage(bitmap, 0, 0);
  const data = ctx.getImageData(0, 0, bitmap.width, bitmap.height).data;
  bitmap.close();
  return { width: canvas.width, height: canvas.height, rgba: new Uint8Array(data.buffer.slice(0)) };
}
