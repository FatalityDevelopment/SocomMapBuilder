// The editable mesh as the bridge serves it (ASSET_MODEL_MESH), decoded into
// typed arrays. Model space is already Y up (the workbench draws it unchanged:
// sky domes above the map, cars on their wheels), so display space = model
// space; toModel* stay as the one place a conversion would go.

import { base64ToBytes } from './asset-client';

export interface MeshChunkWire {
  index: number;
  geometryOffset: number;
  visual: string | null;
  node?: string | null;
  texture: string | null;
  materialBlend: boolean | null;
  unit: string;
  matrix: number[];
  scale: number;
  origin: number[];
  vertexCount: number;
  triangleCount: number;
  hasColors: boolean;
  positions: string;
  uvs: string;
  normals: string;
  colors: string | null;
  triangles: string;
  // Skinned characters: per vertex `skinInfluences` (bone index, weight, bone-local position / normal).
  skinBones?: string | null;
  skinWeights?: string | null;
  skinPositions?: string | null;
  skinNormals?: string | null;
}

export interface BoneWire { name: string; matrixId: number; parent: number; geoIndex: number; local: number[]; world: number[] }

export interface MeshWire {
  archive: string;
  name: string;
  revision: number;
  textureNamespace?: string | null;
  boundsMin: number[];
  boundsMax: number[];
  chunks: MeshChunkWire[];
  skinInfluences?: number | null;
  skeleton?: BoneWire[] | null;
  locators?: Array<{ name: string; matrix: number[] }> | null;
}

export interface EditorChunk {
  index: number;
  geometryOffset: number;
  visual: string | null;
  // The GEO node placing the visual (m4_high, m4_low, scope ...).
  node?: string | null;
  texture: string | null;
  materialBlend: boolean | null;
  unit: string;
  vertexCount: number;
  // Display space (Y up).
  positions: Float32Array;
  uvs: Float32Array;
  normals: Float32Array;
  // RGBA bytes, 0x80 = 1.0; null when the chunk has no colour block.
  colors: Uint8Array | null;
  triangles: Uint32Array;
  skin?: ChunkSkin;
  lit?: boolean;
}

// Bone index (into EditorMesh.skeleton, -1 unused), weight and bone-local position / normal per influence slot.
export interface ChunkSkin { bones: Int32Array; weights: Float32Array; positions: Float32Array; normals: Float32Array }

export interface EditorMesh {
  archive: string;
  name: string;
  revision: number;
  chunks: EditorChunk[];
  // Skinned characters: the bind skeleton (world = bind pose, model space) and influences per vertex.
  skeleton: BoneWire[] | null;
  skinInfluences: number;
}

function floats(b64: string): Float32Array {
  const bytes = base64ToBytes(b64);
  return new Float32Array(bytes.buffer, bytes.byteOffset, bytes.byteLength / 4);
}

function ints(b64: string): Int32Array {
  const bytes = base64ToBytes(b64);
  return new Int32Array(bytes.buffer, bytes.byteOffset, bytes.byteLength / 4);
}

function uints(b64: string): Uint32Array {
  const bytes = base64ToBytes(b64);
  return new Uint32Array(bytes.buffer, bytes.byteOffset, bytes.byteLength / 4);
}

export function decodeMesh(wire: MeshWire): EditorMesh {
  return {
    archive: wire.archive,
    name: wire.name,
    revision: wire.revision,
    skeleton: wire.skeleton ?? null,
    skinInfluences: wire.skinInfluences ?? 0,
    chunks: wire.chunks.map(c => {
      const positions = floats(c.positions).slice();
      const normals = floats(c.normals).slice();
      return {
        index: c.index,
        geometryOffset: c.geometryOffset,
        visual: c.visual,
        node: c.node ?? null,
        texture: c.texture,
        materialBlend: c.materialBlend,
        unit: c.unit,
        vertexCount: c.vertexCount,
        positions,
        uvs: floats(c.uvs).slice(),
        normals,
        colors: c.colors ? base64ToBytes(c.colors) : null,
        triangles: uints(c.triangles).slice(),
        skin: c.skinBones && c.skinWeights && c.skinPositions && c.skinNormals ? {
          bones: ints(c.skinBones).slice(),
          weights: floats(c.skinWeights).slice(),
          positions: floats(c.skinPositions).slice(),
          normals: floats(c.skinNormals).slice(),
        } : undefined,
        lit: !!c.skinBones,
      };
    }),
  };
}

// Display <-> model space: the same axes (both Y up).
export function toModelPoint(p: ArrayLike<number>): [number, number, number] {
  return [p[0], p[1], p[2]];
}

export function toModelMatrix(m: ArrayLike<number>): number[] {
  return Array.from(m);
}
