// Decoder for Zipper PS2 model payloads stored inside ZED containers
// (WEAP_MDL.ZED, WORL_MDL.ZED, and friends).
//
// Geometry chunk (one VU1 batch; verified against the SOCOM II pack builders
// in Mods/SOCOMCA, whose output renders in game):
//   chunk + 0x00 = 0x01000101   STCYCL 0x101
//   chunk + 0x04 = 0x6C048000   UNPACK V4-32 @0, 4 qwords
//   chunk + 0x08 = GIF tag, GIF tag, (index addr, 0, verts, tris), (ox, oy, oz, scale)
//   chunk + 0x30 = vertexCount   (int32)
//   chunk + 0x34 = triangleCount (int32)
//   chunk + 0x38 = origin x/y/z  (float32 x3, world units; 0 on weapons)
//   chunk + 0x44 = scale         (float32)
//   chunk + 0x48 = 03 02 00 01   STCYCL 0x203 (write 2 qwords of every 3: the
//                                third slot is the colour, see below)
//   chunk + 0x4C = UNPACK V4-16 @4, 2 elements per vertex
//   chunk + 0x50 = vertex records, 0x10 bytes each:
//                  int16 x, y, z, nx | int16 u, v, ny, nz
//                  u/v are 12-bit fixed point (4096 = 1.0); normals are Q15.
//   vertexEnd + 0x08 = triangle table, 4 bytes each (a*3, b*3, c*3, 3)
//   then a V3-16 face-normal table and MSCNT.
//
// Positions: int16 * scale / 32768 for weapons (GEO vparams flag 0x20000 set)
// and int16 * scale / 16 for props, vehicles and map objects (flag clear),
// plus the chunk origin. int16 / 16 only reaches +-2048 units, so world
// chunks further out store their offset in the origin (MP53: 0 / 2000).
//
// A model's child stubs (N<node>_<visual>, 4-byte payload) point at DMA
// chains that bind the chunks: per material section a kind-6 tag (the .tif
// name), a kind-3 tag (GS material registers), then per chunk a kind-4 tag
// (per-vertex RGBA, 0x80 = 1.0, the baked lighting) and a kind-2 tag (the
// geometry chunk). parseVisualChains walks those; parseModel uses them when
// they validate and only falls back to scanning for .tif strings otherwise.

export interface ZedModelVertex {
  x: number;
  y: number;
  z: number;
  u: number;
  v: number;
  // Baked vertex colour from the chain's colour block, 0..255 per channel
  // with 0x80 = 1.0 (the GS modulates texture * colour * 2). Absent when
  // the chunk was decoded without its chain.
  r?: number;
  g?: number;
  b?: number;
  a?: number;
}

// How int16 vertex positions map to world units. See the header comment.
export type PositionUnit = 'scale' | 'sixteenth';

export interface ZedModelChunk {
  offset: number;
  vertexCount: number;
  triangleCount: number;
  decodedTriangleCount: number;
  scale: number;
  vertices: ZedModelVertex[];
  indices: number[];
  // Name of the .tif this chunk references (lowercased). From the DMA chain
  // when available, else found in the tail area after the triangle table.
  textureName: string | null;
  // Where textureName came from. 'chain' and 'descriptor' are explicit
  // material bindings and should not be second-guessed by render heuristics.
  textureBinding?: 'chain' | 'tail' | 'descriptor' | 'smartWorld' | 'sliceNearest' | 'globalNearest';
  // The visual (N<node>_<visual> stub) whose chain drew this chunk.
  visualKey?: string;
  // True when every vertex carries r/g/b/a from the chain's colour block.
  hasVertexColors?: boolean;
  // From the section material's ALPHA_1: true when the chunk is alpha
  // blended (0x44, (Cs - Cd) * As + Cd), false for a plain copy (0, used
  // with an alpha test by foliage cutouts). Absent without a material block.
  materialBlend?: boolean;
  // Which position unit was applied when decoding the vertices.
  positionUnit?: PositionUnit;
  // Chunk origin (header +0x38..+0x40, world units), already added to the
  // vertex positions. Unit conversions scale around it.
  origin?: [number, number, number];
  // Original MDL/GEO source for composed scenes. Palette selection can depend
  // on the model that referenced a shared .tif page.
  sourceModelName?: string;
  // Original archive/prefix for composed scenes. This lets the renderer prefer
  // FLIB/WEAP/M51-specific TXR/PAL aliases when multiple packages contain the
  // same .tif name.
  sourceArchiveName?: string;
}

export interface ZedModel {
  chunks: ZedModelChunk[];
  vertices: ZedModelVertex[];
  indices: number[];
  textureRefs: string[];
  boundingRadius: number;
}

export function looksLikeModel(bytes: Uint8Array): boolean {
  return findChunkOffsets(bytes).length > 0;
}

// Detect a nested ZED container payload. ZED files start with a 0x64-byte
// header where +0x10 = 0x10 (default HeaderWord10), +0x04 = slot count, and
// +0x08 = string table size. If the entry we're previewing is itself a ZED
// file, we should NOT parse it as a model.
export function looksLikeZedContainer(bytes: Uint8Array): boolean {
  if (bytes.length < 0x64) { return false; }
  const headerWord10 = bytes[0x10] | (bytes[0x11] << 8) | (bytes[0x12] << 16) | (bytes[0x13] << 24);
  if (headerWord10 !== 0x10) { return false; }
  const slotCount =
    bytes[0x04] | (bytes[0x05] << 8) | (bytes[0x06] << 16) | (bytes[0x07] << 24);
  if (slotCount === 0 || slotCount > 1_000_000) { return false; }
  const strTableSize =
    bytes[0x08] | (bytes[0x09] << 8) | (bytes[0x0a] << 16) | (bytes[0x0b] << 24);
  if (strTableSize > bytes.length) { return false; }
  return true;
}

function readU32LE(bytes: Uint8Array, offset: number): number {
  return (
    bytes[offset] |
    (bytes[offset + 1] << 8) |
    (bytes[offset + 2] << 16) |
    (bytes[offset + 3] << 24)
  ) >>> 0;
}

function findChunkOffsets(bytes: Uint8Array): number[] {
  const offsets: number[] = [];
  const limit = bytes.length - 0x50;
  for (let i = 0; i <= limit; i += 0x10) {
    if (readU32LE(bytes, i + 0x00) !== 0x01000101) { continue; }
    if (readU32LE(bytes, i + 0x04) !== 0x6c048000) { continue; }
    if (readU32LE(bytes, i + 0x08) !== 0x00008000) { continue; }
    if (
      bytes[i + 0x48] !== 0x03 ||
      bytes[i + 0x49] !== 0x02 ||
      bytes[i + 0x4a] !== 0x00 ||
      bytes[i + 0x4b] !== 0x01
    ) { continue; }

    const view = new DataView(bytes.buffer, bytes.byteOffset + i, 0x50);
    const vertexCount = view.getInt32(0x30, true);
    const triangleCount = view.getInt32(0x34, true);
    // Bounds bumped to 4096 per HTML map viewer reference. World-model
    // chunks can have several thousand verts; rejecting them at 1000 was
    // exactly what caused "holes" the user was seeing — large chunks
    // weren't decoded at all. The expectedEnd check below still rejects
    // garbage that happens to satisfy the magic bytes.
    if (vertexCount <= 0 || vertexCount > 4096) { continue; }
    if (triangleCount < 0 || triangleCount > 4096) { continue; }
    const expectedEnd = i + 0x50 + vertexCount * 0x10 + 0x08 + triangleCount * 0x04;
    if (expectedEnd > bytes.length) { continue; }

    offsets.push(i);
  }
  return offsets;
}

function extractTextureRefs(bytes: Uint8Array, start: number, end: number): string[] {
  const refs = new Set<string>();
  const clampedEnd = Math.min(end, bytes.length);
  let runStart = -1;
  for (let i = start; i < clampedEnd; i++) {
    const b = bytes[i];
    const printable = b >= 0x20 && b <= 0x7e;
    if (printable) {
      if (runStart < 0) { runStart = i; }
    } else {
      if (runStart >= 0 && i - runStart >= 4) {
        const text = new TextDecoder('ascii').decode(bytes.subarray(runStart, i));
        const match = text.match(/[A-Za-z0-9_\-.]+\.tif/i);
        if (match) { refs.add(match[0]); }
      }
      runStart = -1;
    }
  }
  return [...refs];
}

function findTifInRange(bytes: Uint8Array, start: number, end: number): string | null {
  const clampedEnd = Math.min(end, bytes.length);
  let runStart = -1;
  for (let i = start; i < clampedEnd; i++) {
    const b = bytes[i];
    const printable = b >= 0x20 && b <= 0x7e;
    if (printable) {
      if (runStart < 0) { runStart = i; }
    } else {
      if (runStart >= 0 && i - runStart >= 5) {
        const text = new TextDecoder('ascii').decode(bytes.subarray(runStart, i));
        const m = text.match(/[A-Za-z0-9_\-.]+\.tif/i);
        if (m) { return m[0].toLowerCase(); }
      }
      runStart = -1;
    }
  }
  return null;
}

function parseChunk(
  bytes: Uint8Array,
  offset: number,
  nextChunkOffset: number,
  positionUnit: PositionUnit = 'scale',
): ZedModelChunk | null {
  const view = new DataView(bytes.buffer, bytes.byteOffset + offset, Math.min(0x50, bytes.length - offset));
  const vertexCount = view.getInt32(0x30, true);
  const triangleCount = view.getInt32(0x34, true);
  let scale = view.getFloat32(0x44, true);
  if (!Number.isFinite(scale) || scale === 0) { scale = 1; }

  const vertexStart = offset + 0x50;
  const vertexBlockSize = vertexCount * 0x10;
  if (vertexStart + vertexBlockSize > bytes.length) { return null; }

  // int16 -> units. Weapons normalise by the chunk scale (int16 / 32768 *
  // scale); props, vehicles and map objects store 1/16 units. The origin
  // is added on top, in world units.
  const divisor = positionUnit === 'sixteenth' ? 16 : 32768;
  const readOrigin = (o: number): number => {
    const value = view.getFloat32(o, true);
    return Number.isFinite(value) && Math.abs(value) < 1e6 ? value : 0;
  };
  const origin: [number, number, number] = [readOrigin(0x38), readOrigin(0x3c), readOrigin(0x40)];
  const vertices: ZedModelVertex[] = [];
  for (let i = 0; i < vertexCount; i++) {
    const o = vertexStart + i * 0x10;
    const v = new DataView(bytes.buffer, bytes.byteOffset + o, 0x10);
    const xi = v.getInt16(0, true);
    const yi = v.getInt16(2, true);
    const zi = v.getInt16(4, true);
    // Second element of the vertex: (u, v, ny, nz). u/v are 12-bit fixed
    // point, 4096 = 1.0.
    const ui = v.getInt16(0x08, true);
    const vi = v.getInt16(0x0a, true);
    vertices.push({
      x: (xi * scale) / divisor + origin[0],
      y: (yi * scale) / divisor + origin[1],
      z: (zi * scale) / divisor + origin[2],
      u: ui / 4096,
      v: vi / 4096,
    });
  }

  const triCommand = vertexStart + vertexBlockSize;
  const triData = triCommand + 0x08;

  const indices: number[] = [];
  let decoded = 0;
  for (let i = 0; i < triangleCount; i++) {
    const o = triData + i * 4;
    if (o + 4 > bytes.length) { break; }
    const aPacked = bytes[o + 0];
    const bPacked = bytes[o + 1];
    const cPacked = bytes[o + 2];
    // bytes[o + 3] is flags; we don't use it for preview rendering yet.
    const a = (aPacked / 3) | 0;
    const b = (bPacked / 3) | 0;
    const c = (cPacked / 3) | 0;
    if (a >= vertexCount || b >= vertexCount || c >= vertexCount) { continue; }
    indices.push(a, b, c);
    decoded++;
  }

  // Tail area between triangle data and the next chunk holds the .tif name.
  const triEnd = triData + triangleCount * 4;
  const textureName = findTifInRange(bytes, triEnd, nextChunkOffset);

  return {
    offset,
    vertexCount,
    triangleCount,
    decodedTriangleCount: decoded,
    scale,
    vertices,
    indices,
    textureName,
    textureBinding: textureName ? 'tail' : undefined,
    positionUnit,
    origin,
  };
}

// ---------------------------------------------------------------------------
// DMA chain binding. A model node's N<node>_<visual> children hold a 4-byte
// offset into the node payload. At that offset:
//   u32 count (low 16 bits), then `count` 16-byte DMA tags (lo, addr, v0, v1)
//   kind = (lo >> 16) & 0x3FF, qwc = lo & 0xFFFF
//     6  name field      addr -> NUL-terminated .tif name (16 or 32 bytes)
//     3  material block  qwc qwords of GS register data for the section
//     4  colour block    qwc qwords of RGBA per vertex; v0/v1 are the VIF
//                        codes (STCYCL 0x103, UNPACK V4-8 @6) that write it
//                        into the third slot of each vertex
//     2  geometry chunk  qwc qwords starting at a chunk header
// Sections start at a kind-6 tag; colour + geometry pairs follow.
// ---------------------------------------------------------------------------

export interface ChainChunkRef {
  geometryOffset: number;
  geometryLength: number;
  colorOffset: number | null;
  colorLength: number;
}

export interface ChainSection {
  textureName: string | null;
  materialOffset: number | null;
  materialLength: number;
  chunks: ChainChunkRef[];
}

export interface VisualChain {
  offset: number;
  key: string | null;
  sections: ChainSection[];
}

export interface ChainOffset {
  offset: number;
  key?: string | null;
}

const CHUNK_MAGIC_0 = 0x01000101;
const CHUNK_MAGIC_1 = 0x6c048000;

function readChainName(bytes: Uint8Array, offset: number): string | null {
  if (offset < 0 || offset >= bytes.length) { return null; }
  let text = '';
  for (let i = offset; i < Math.min(bytes.length, offset + 64); i++) {
    const b = bytes[i];
    if (b === 0) { break; }
    if (b < 0x20 || b > 0x7e) { return null; }
    text += String.fromCharCode(b);
  }
  const trimmed = text.trim();
  if (!trimmed) { return null; }
  const leaf = trimmed.split(/[\\/]/).pop() ?? trimmed;
  return leaf.toLowerCase();
}

// Parse one chain. Returns null when anything about it is implausible so the
// caller can fall back to the string-scan heuristics instead of trusting a
// 4-byte child that happened not to be a chain offset.
function parseVisualChain(bytes: Uint8Array, offset: number, key: string | null): VisualChain | null {
  if (offset < 0 || offset + 16 > bytes.length) { return null; }
  const count = readU32LE(bytes, offset) & 0xffff;
  if (count === 0 || count > 8192) { return null; }
  if (offset + 16 + count * 16 > bytes.length) { return null; }

  const sections: ChainSection[] = [];
  let pendingColor: { offset: number; length: number } | null = null;
  for (let i = 0; i < count; i++) {
    const tag = offset + 16 + i * 16;
    const lo = readU32LE(bytes, tag);
    const addr = readU32LE(bytes, tag + 4);
    // Kind in bits 16-23; bit 24 is a flag (seen on material tags, 0x31...)
    // and must not make the chain look invalid.
    const kind = (lo >>> 16) & 0xff;
    const qwc = lo & 0xffff;
    const length = qwc * 16;
    if (addr > bytes.length) { return null; }
    switch (kind) {
      case 6: {
        sections.push({ textureName: readChainName(bytes, addr), materialOffset: null, materialLength: 0, chunks: [] });
        pendingColor = null;
        break;
      }
      case 3: {
        if (sections.length === 0 || addr + length > bytes.length) { return null; }
        const section = sections[sections.length - 1];
        section.materialOffset = addr;
        section.materialLength = length;
        break;
      }
      case 4: {
        if (addr + length > bytes.length) { return null; }
        pendingColor = { offset: addr, length };
        break;
      }
      case 2: {
        if (addr + 0x50 > bytes.length || addr + length > bytes.length) { return null; }
        if (readU32LE(bytes, addr) !== CHUNK_MAGIC_0 || readU32LE(bytes, addr + 4) !== CHUNK_MAGIC_1) { return null; }
        if (!chunkCountsLookSane(bytes, addr)) { return null; }
        if (sections.length === 0) {
          sections.push({ textureName: null, materialOffset: null, materialLength: 0, chunks: [] });
        }
        sections[sections.length - 1].chunks.push({
          geometryOffset: addr,
          geometryLength: length,
          colorOffset: pendingColor ? pendingColor.offset : null,
          colorLength: pendingColor ? pendingColor.length : 0,
        });
        pendingColor = null;
        break;
      }
      default:
        return null;
    }
  }
  if (!sections.some(s => s.chunks.length > 0)) { return null; }
  return { offset, key, sections };
}

// The vertex / triangle counts of a chunk header, bounds-checked the same
// way findChunkOffsets does for the scan path.
function chunkCountsLookSane(bytes: Uint8Array, offset: number): boolean {
  const view = new DataView(bytes.buffer, bytes.byteOffset + offset, 0x50);
  const vertexCount = view.getInt32(0x30, true);
  const triangleCount = view.getInt32(0x34, true);
  if (vertexCount <= 0 || vertexCount > 4096) { return false; }
  if (triangleCount < 0 || triangleCount > 4096) { return false; }
  return offset + 0x50 + vertexCount * 0x10 + 0x08 + triangleCount * 0x04 <= bytes.length;
}

// True when the stubs point at chains that draw only kind-1 packets: GS line
// strips (PRIM type 2), e.g. MP6's power-line wires. They have no triangle
// chunks, and the string-scan fallback turned them into wide ribbons.
export function chainsAreLineOnly(bytes: Uint8Array, offsets: ChainOffset[]): boolean {
  let lines = 0;
  for (const { offset } of offsets) {
    if (offset < 0 || offset + 16 > bytes.length) { return false; }
    const count = readU32LE(bytes, offset) & 0xffff;
    if (count === 0 || offset + 16 + count * 16 > bytes.length) { return false; }
    for (let i = 0; i < count; i++) {
      const kind = (readU32LE(bytes, offset + 16 + i * 16) >>> 16) & 0xff;
      if (kind === 1) { lines++; } else if (kind !== 3 && kind !== 4 && kind !== 6) { return false; }
    }
  }
  return lines > 0;
}

// Parse every chain the stubs point at. Chains that fail validation are
// dropped; returns null when none validate.
export function parseVisualChains(bytes: Uint8Array, offsets: ChainOffset[]): VisualChain[] | null {
  const seen = new Set<number>();
  const chains: VisualChain[] = [];
  for (const entry of offsets) {
    if (seen.has(entry.offset)) { continue; }
    seen.add(entry.offset);
    const chain = parseVisualChain(bytes, entry.offset, entry.key ?? null);
    if (chain) { chains.push(chain); }
  }
  if (chains.length === 0) { return null; }
  chains.sort((a, b) => (a.key ?? '').localeCompare(b.key ?? '') || a.offset - b.offset);
  return chains;
}

function attachVertexColors(chunk: ZedModelChunk, bytes: Uint8Array, ref: ChainChunkRef): void {
  if (ref.colorOffset === null) { return; }
  const needed = chunk.vertexCount * 4;
  if (ref.colorLength < needed || ref.colorOffset + needed > bytes.length) { return; }
  for (let i = 0; i < chunk.vertexCount; i++) {
    const o = ref.colorOffset + i * 4;
    const v = chunk.vertices[i];
    v.r = bytes[o];
    v.g = bytes[o + 1];
    v.b = bytes[o + 2];
    v.a = bytes[o + 3];
  }
  chunk.hasVertexColors = true;
}

export interface ParseModelOptions {
  // N<node>_<visual> stub offsets (and their names) for exact chain binding.
  chainOffsets?: ChainOffset[];
  // Position unit for int16 vertices; default 'scale' (weapon convention).
  positionUnit?: PositionUnit;
}

// Decode the chunks a set of chains reference, with their exact texture and
// vertex colours. Returns null when no chain validates.
export function parseModelFromChains(bytes: Uint8Array, chainOffsets: ChainOffset[], positionUnit: PositionUnit = 'scale'): ZedModel | null {
  const chains = parseVisualChains(bytes, chainOffsets);
  if (!chains) { return null; }
  const chunks: ZedModelChunk[] = [];
  // A visual can list the same geometry twice (alternative chains over one
  // mesh, e.g. wheel states): draw it once per visual. Different visuals
  // that share geometry are instances placed at different GEO nodes, so
  // each keeps its own copy.
  for (const chain of chains) {
    const seenGeometry = new Set<number>();
    for (const section of chain.sections) {
      for (const ref of section.chunks) {
        if (seenGeometry.has(ref.geometryOffset)) { continue; }
        seenGeometry.add(ref.geometryOffset);
        const chunk = parseChunk(bytes, ref.geometryOffset, ref.geometryOffset + ref.geometryLength, positionUnit);
        if (!chunk) { continue; }
        if (section.textureName) {
          chunk.textureName = section.textureName;
          chunk.textureBinding = 'chain';
        }
        chunk.visualKey = chain.key ?? `chain@0x${chain.offset.toString(16)}`;
        attachVertexColors(chunk, bytes, ref);
        const blend = readMaterialBlend(bytes, section);
        if (blend !== null) { chunk.materialBlend = blend; }
        chunks.push(chunk);
      }
    }
  }
  if (chunks.length === 0) { return null; }
  return assembleModel(bytes, chunks);
}

// ALPHA_1 (GS register 0x42) in a section's material block: blended when the
// equation reads the destination colour. Null when the block has no ALPHA_1.
function readMaterialBlend(bytes: Uint8Array, section: ChainSection): boolean | null {
  if (section.materialOffset === null) { return null; }
  const end = Math.min(bytes.length, section.materialOffset + section.materialLength);
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  for (let o = section.materialOffset; o + 16 <= end; o += 16) {
    if (view.getUint32(o + 8, true) !== 0x42 || view.getUint32(o + 12, true) !== 0) { continue; }
    const lo = view.getUint32(o, true);
    const a = lo & 0x3;
    const b = (lo >>> 2) & 0x3;
    const d = (lo >>> 6) & 0x3;
    return b === 1 || d === 1 || (a === 1 && d !== 1);
  }
  return null;
}

function assembleModel(bytes: Uint8Array, chunks: ZedModelChunk[]): ZedModel {
  const vertices: ZedModelVertex[] = [];
  const indices: number[] = [];
  let radius = 0;
  for (const chunk of chunks) {
    const base = vertices.length;
    for (const v of chunk.vertices) {
      vertices.push(v);
      const d = Math.hypot(v.x, v.y, v.z);
      if (d > radius) { radius = d; }
    }
    for (const idx of chunk.indices) {
      indices.push(base + idx);
    }
  }

  // Scan the full payload for embedded .tif texture references. These usually
  // live in a string block after the geometry chunks.
  const textureRefs = extractTextureRefs(bytes, 0, bytes.length);
  for (const chunk of chunks) {
    if (chunk.textureName && !textureRefs.some(r => r.toLowerCase() === chunk.textureName)) {
      textureRefs.push(chunk.textureName);
    }
  }

  return {
    chunks,
    vertices,
    indices,
    textureRefs,
    boundingRadius: radius || 1,
  };
}

// Re-express a model's positions in another unit. The two conventions
// differ by exactly 32768 / 16, so this is a pure scale and never needs a
// re-parse. Returns the same model when nothing changes.
export function convertPositionUnit(model: ZedModel, unit: PositionUnit): ZedModel {
  const factors = model.chunks.map(c => {
    const from = c.positionUnit ?? 'scale';
    if (from === unit) { return 1; }
    return unit === 'sixteenth' ? 32768 / 16 : 16 / 32768;
  });
  if (factors.every(f => f === 1)) { return model; }
  let radius = 0;
  const chunks = model.chunks.map((c, i) => {
    const f = factors[i];
    const [ox, oy, oz] = c.origin ?? [0, 0, 0];
    const vertices = f === 1 ? c.vertices : c.vertices.map(v => ({
      ...v,
      x: (v.x - ox) * f + ox,
      y: (v.y - oy) * f + oy,
      z: (v.z - oz) * f + oz,
    }));
    for (const v of vertices) {
      const d = Math.hypot(v.x, v.y, v.z);
      if (d > radius) { radius = d; }
    }
    return { ...c, vertices, positionUnit: unit };
  });
  const vertices: ZedModelVertex[] = [];
  for (const c of chunks) { for (const v of c.vertices) { vertices.push(v); } }
  return { ...model, chunks, vertices, boundingRadius: radius || 1 };
}

// Axis-aligned extent of a model's positions (max side of its bounding box).
export function modelExtent(model: ZedModel): number {
  let minX = Infinity, minY = Infinity, minZ = Infinity;
  let maxX = -Infinity, maxY = -Infinity, maxZ = -Infinity;
  for (const v of model.vertices) {
    if (v.x < minX) { minX = v.x; } if (v.x > maxX) { maxX = v.x; }
    if (v.y < minY) { minY = v.y; } if (v.y > maxY) { maxY = v.y; }
    if (v.z < minZ) { minZ = v.z; } if (v.z > maxZ) { maxZ = v.z; }
  }
  if (!Number.isFinite(minX)) { return 0; }
  return Math.max(maxX - minX, maxY - minY, maxZ - minZ);
}

export function parseModel(bytes: Uint8Array, options: ParseModelOptions = {}): ZedModel {
  const positionUnit = options.positionUnit ?? 'scale';
  if (options.chainOffsets && options.chainOffsets.length > 0) {
    const fromChains = parseModelFromChains(bytes, options.chainOffsets, positionUnit);
    if (fromChains) { return fromChains; }
  }

  const offsets = findChunkOffsets(bytes);
  const chunks: ZedModelChunk[] = [];
  for (let i = 0; i < offsets.length; i++) {
    const off = offsets[i];
    const nextOff = i + 1 < offsets.length ? offsets[i + 1] : bytes.length;
    const chunk = parseChunk(bytes, off, nextOff, positionUnit);
    if (chunk) { chunks.push(chunk); }
  }
  return assembleModel(bytes, chunks);
}

// ---------------------------------------------------------------------------
// HTML map-viewer style material binding helpers. Off by default — only
// applied when the caller (component) explicitly opts in via the map
// material-binding dropdown. The default chunk.textureName from the tail
// scan is preserved otherwise so castle / weapon rendering is unchanged.
// ---------------------------------------------------------------------------

export interface TifMarker {
  offset: number;
  name: string;       // lowercased canonical filename
  isDetail: boolean;
}

export function extractTextureMarkers(bytes: Uint8Array): TifMarker[] {
  const text = new TextDecoder('latin1').decode(bytes);
  const rx = /[A-Za-z0-9_./\\-]{3,}\.tif/gi;
  const markers: TifMarker[] = [];
  let m: RegExpExecArray | null;
  while ((m = rx.exec(text)) !== null) {
    const raw = m[0];
    const canonical = (raw.split(/[\\/]/).pop() ?? raw).toLowerCase();
    markers.push({
      offset: m.index,
      name: canonical,
      isDetail:
        canonical.includes('detail') ||
        canonical.endsWith('_d.tif') ||
        canonical.includes('_detail'),
    });
  }
  return markers;
}

// Resolves the model-payload slice each chunk belongs to. Slices come from
// the Nxxx_xxx child stubs of the model node — each stub's 4-byte payload
// is the byte offset into the parent. Pass [] when no stubs are available
// and the whole payload is treated as one slice.
export function makeSlicesFromOffsets(payloadLen: number, sliceOffsets: number[]): Array<{ start: number; end: number }> {
  const sorted = [...new Set(sliceOffsets.filter(o => o >= 0 && o < payloadLen))].sort((a, b) => a - b);
  if (sorted[0] !== 0) { sorted.unshift(0); }
  const slices: Array<{ start: number; end: number }> = [];
  for (let i = 0; i < sorted.length; i++) {
    const start = sorted[i];
    const end = i + 1 < sorted.length ? sorted[i + 1] : payloadLen;
    if (end > start) { slices.push({ start, end }); }
  }
  return slices;
}

export type MaterialBindingMode = 'smartWorld' | 'sliceNearest' | 'globalNearest';

// Post-processing pass that ASSIGNS chunk.textureName for chunks that the
// tail scan couldn't bind. Behaviour is supplementary: chunks that already
// have a textureName from findTifInRange keep it (those are exact matches
// pulled straight from the model payload's tail, almost always correct).
// Only previously-unbound chunks get a marker assignment per the chosen
// mode. This preserves castle / weapon accuracy while still filling in
// the ~80% of world chunks that tail scan misses.
//
// Mutates `model.chunks` in place.
export function applyMarkerBinding(
  model: ZedModel,
  bytes: Uint8Array,
  sliceOffsets: number[],
  mode: MaterialBindingMode,
): void {
  if (model.chunks.length === 0) { return; }
  const markers = extractTextureMarkers(bytes);
  if (markers.length === 0) { return; }
  const slices = makeSlicesFromOffsets(bytes.length, sliceOffsets);

  for (const chunk of model.chunks) {
    if (chunk.textureName) { continue; }  // tail-scan already nailed it.

    const slice = slices.find(s => chunk.offset >= s.start && chunk.offset < s.end)
      ?? { start: 0, end: Number.MAX_SAFE_INTEGER };
    const sliceMarkers = markers.filter(m => m.offset >= slice.start && m.offset < slice.end);
    const pool = sliceMarkers.length ? sliceMarkers : markers;

    let chosen: TifMarker | null = null;
    if (mode === 'smartWorld') {
      // Big chunks prefer non-detail textures (HTML's smartWorld logic). For
      // small chunks the nearest marker (any kind) tends to be correct.
      const big = chunk.triangleCount >= 24;
      const candidates = big ? pool.filter(m => !m.isDetail) : pool;
      chosen = chooseNearest(chunk.offset, candidates.length ? candidates : pool);
    } else if (mode === 'sliceNearest') {
      chosen = chooseNearest(chunk.offset, pool);
    } else if (mode === 'globalNearest') {
      chosen = chooseNearest(chunk.offset, markers);
    }
    if (chosen) {
      chunk.textureName = chosen.name;
      chunk.textureBinding = mode;
    }
  }
}

export function applyWeaponDescriptorBinding(
  model: ZedModel,
  bytes: Uint8Array,
  descriptorOffsets: number[],
): boolean {
  if (model.chunks.length === 0 || descriptorOffsets.length === 0) { return false; }

  const chunksByOffset = new Map<number, ZedModelChunk>();
  for (const chunk of model.chunks) {
    chunksByOffset.set(chunk.offset, chunk);
  }

  let applied = false;
  const uniqueRefs = new Set(model.textureRefs.map(r => r.toLowerCase()));
  const readU32 = (offset: number): number => {
    return (
      bytes[offset] |
      (bytes[offset + 1] << 8) |
      (bytes[offset + 2] << 16) |
      (bytes[offset + 3] << 24)
    ) >>> 0;
  };

  for (const descriptorOffset of [...new Set(descriptorOffsets)].sort((a, b) => a - b)) {
    if (descriptorOffset < 0 || descriptorOffset + 0x10 > bytes.length) { continue; }
    const recordCount = readU32(descriptorOffset);
    if (recordCount <= 0 || recordCount > 1024) { continue; }
    const recordsStart = descriptorOffset + 0x10;
    const recordsEnd = recordsStart + recordCount * 0x10;
    if (recordsEnd > bytes.length) { continue; }

    let currentTexture: string | null = null;
    for (let i = 0; i < recordCount; i++) {
      const record = recordsStart + i * 0x10;
      const command = readU32(record);
      const refOffset = readU32(record + 0x04);
      const opcode = (command >>> 16) & 0xffff;

      if (opcode === 0x1006) {
        currentTexture = readTifAt(bytes, refOffset);
        if (currentTexture && !uniqueRefs.has(currentTexture)) {
          model.textureRefs.push(currentTexture);
          uniqueRefs.add(currentTexture);
        }
      } else if (opcode === 0x3002 && currentTexture) {
        const chunk = chunksByOffset.get(refOffset);
        if (chunk) {
          chunk.textureName = currentTexture;
          chunk.textureBinding = 'descriptor';
          applied = true;
        }
      }
    }
  }

  return applied;
}

function readTifAt(bytes: Uint8Array, offset: number): string | null {
  if (offset < 0 || offset >= bytes.length) { return null; }
  let text = '';
  for (let i = offset; i < bytes.length; i++) {
    const b = bytes[i];
    if (b === 0) { break; }
    if (b < 0x20 || b > 0x7e) { break; }
    text += String.fromCharCode(b);
    if (text.length > 128) { break; }
  }
  const match = text.match(/[A-Za-z0-9_\-.]+\.tif/i);
  return match ? match[0].toLowerCase() : null;
}

function chooseNearest(offset: number, markers: TifMarker[]): TifMarker | null {
  if (markers.length === 0) { return null; }
  let best = markers[0];
  let bestDist = Math.abs(best.offset - offset);
  for (let i = 1; i < markers.length; i++) {
    const d = Math.abs(markers[i].offset - offset);
    if (d < bestDist) { best = markers[i]; bestDist = d; }
  }
  return best;
}

// ---------------------------------------------------------------------------
// VIF1 UNPACK stream + vertex cloud fallback decoders. Used when a payload
// doesn't contain the standard WORL_MDL chunk format — CLIB MESH_* (player
// characters) and similar PS2 DMA-style packets fall through here. Direct
// ports of the new HTML map viewer's findVifUnpackStreams / decodeVifPayload
// / findI16VertexClouds / decodeFloatCloudNode (see
// `zed_map_viewer_solution (1).html` lines 1276-1759).
// ---------------------------------------------------------------------------

interface VifStream {
  offset: number;
  num: number;
  imm: number;
  comps: number;
  bytesPerComponent: number;
  points: Array<{ x: number; y: number; z: number }>;
  minX: number; minY: number; minZ: number;
  maxX: number; maxY: number; maxZ: number;
  span: number;
}

function alignUp(value: number, alignment: number): number {
  const mask = alignment - 1;
  return (value + mask) & ~mask;
}

// Scan for PS2 VIF1 UNPACK commands. The decoded layout per command is:
//   byte 3   cmd     (0x60..0x7F = UNPACK with embedded format)
//   byte 2   num     (vertex count; 0 -> 256)
//   bytes0-1 imm     (destination VU address + flags)
// Position-shaped streams (V3/V4 with float / int16 / int8 lanes) are
// emitted; everything else is ignored.
export function findVifUnpackStreams(data: Uint8Array): VifStream[] {
  if (!data || data.length < 0x20 || data.length > 0x80000) { return []; }
  const view = new DataView(data.buffer, data.byteOffset, data.byteLength);
  const streams: VifStream[] = [];

  for (let o = 0; o <= data.length - 8; o += 4) {
    const imm = view.getUint16(o + 0, true);
    const numRaw = view.getUint8(o + 2);
    const cmd = view.getUint8(o + 3);

    const baseCmd = cmd & 0x7F;
    if (baseCmd < 0x60 || baseCmd > 0x7F) { continue; }

    const vn = (baseCmd >> 2) & 0x03;
    const vl = baseCmd & 0x03;
    const comps = [1, 2, 3, 4][vn];
    const bytesPerComponent = [4, 2, 1, 0][vl];
    if (!bytesPerComponent) { continue; }    // V4_5 packed format — skip.
    if (comps < 3) { continue; }              // need XYZ candidates.

    const num = numRaw === 0 ? 256 : numRaw;
    const dataStart = o + 4;
    const payloadBytes = num * comps * bytesPerComponent;
    const dataEnd = dataStart + payloadBytes;
    if (payloadBytes <= 0 || dataEnd > data.length) { continue; }
    if (num < 3 || num > 256) { continue; }

    const pts: Array<{ x: number; y: number; z: number }> = [];
    let minX = Infinity, minY = Infinity, minZ = Infinity;
    let maxX = -Infinity, maxY = -Infinity, maxZ = -Infinity;
    let bad = 0;
    for (let i = 0; i < num; i++) {
      const p = dataStart + i * comps * bytesPerComponent;
      let x: number, y: number, z: number;
      if (bytesPerComponent === 4) {
        x = view.getFloat32(p + 0, true);
        y = view.getFloat32(p + 4, true);
        z = view.getFloat32(p + 8, true);
      } else if (bytesPerComponent === 2) {
        x = view.getInt16(p + 0, true);
        y = view.getInt16(p + 2, true);
        z = view.getInt16(p + 4, true);
      } else {
        x = view.getInt8(p + 0);
        y = view.getInt8(p + 1);
        z = view.getInt8(p + 2);
      }
      if (!Number.isFinite(x) || !Number.isFinite(y) || !Number.isFinite(z)) { bad++; continue; }
      if (Math.abs(x) > 200000 || Math.abs(y) > 200000 || Math.abs(z) > 200000) { bad++; continue; }
      pts.push({ x, y, z });
      if (x < minX) { minX = x; } if (x > maxX) { maxX = x; }
      if (y < minY) { minY = y; } if (y > maxY) { maxY = y; }
      if (z < minZ) { minZ = z; } if (z > maxZ) { maxZ = z; }
    }
    if (pts.length < 3 || bad > num * 0.15) { continue; }
    const sx = maxX - minX, sy = maxY - minY, sz = maxZ - minZ;
    const span = Math.max(sx, sy, sz);
    const axes = [sx, sy, sz].filter(v => v > 0.001).length;
    if (span < 1 || span > 100000 || axes < 2) { continue; }

    streams.push({ offset: o, num, imm, comps, bytesPerComponent, points: pts, minX, minY, minZ, maxX, maxY, maxZ, span });
    o = alignUp(dataEnd, 4) - 4;
    if (streams.length >= 128) { break; }
  }

  return streams;
}

// Pick the VIF streams that look like vertex positions. The HTML reference
// prefers the V4_16-at-VU-addr-0x4 pattern (the characteristic CLIB player
// mesh layout) and falls back to other position-shaped streams.
function selectRenderableVifStreams(streams: VifStream[]): VifStream[] {
  const isV4_16_addr4 = (s: VifStream) =>
    s.bytesPerComponent === 2 && s.comps === 4 && (s.imm & 0x03ff) === 0x4 && s.num >= 16;
  const fullSignedRange = (s: VifStream) =>
    s.minX < -32000 || s.minY < -32000 || s.minZ < -32000 ||
    s.maxX > 32000 || s.maxY > 32000 || s.maxZ > 32000;
  const likelyNormal = (s: VifStream) =>
    s.bytesPerComponent === 2 && fullSignedRange(s) && s.span > 60000;
  const plausible = (s: VifStream) => {
    const ax = [s.maxX - s.minX, s.maxY - s.minY, s.maxZ - s.minZ].filter(v => v > 16).length;
    return isV4_16_addr4(s) ||
      (s.bytesPerComponent === 2 && s.num >= 16 && ax >= 2 && s.span >= 100 && s.span < 58000 && !likelyNormal(s));
  };

  let selected = streams.filter(isV4_16_addr4);
  if (selected.length === 0) { selected = streams.filter(plausible); }
  if (selected.length === 0) {
    selected = streams
      .filter(s => s.bytesPerComponent === 2 && s.num >= 16 && !likelyNormal(s))
      .slice(0, 12);
  }
  return selected;
}

// Triangle strip builder with edge/area sanity checks so a noisy stream
// doesn't produce a spiky-ball artefact. Mirrors buildSafeVifStripTriangles.
function buildSafeStripTriangles(
  points: Array<{ x: number; y: number; z: number }>,
  base: number,
  chunkIndex: number,
): number[] {
  const out: number[] = [];
  if (points.length < 3) { return out; }
  let minX = Infinity, minY = Infinity, minZ = Infinity;
  let maxX = -Infinity, maxY = -Infinity, maxZ = -Infinity;
  for (const p of points) {
    if (p.x < minX) { minX = p.x; } if (p.x > maxX) { maxX = p.x; }
    if (p.y < minY) { minY = p.y; } if (p.y > maxY) { maxY = p.y; }
    if (p.z < minZ) { minZ = p.z; } if (p.z > maxZ) { maxZ = p.z; }
  }
  const diag = Math.max(1, Math.hypot(maxX - minX, maxY - minY, maxZ - minZ));
  const maxEdge = diag * 0.42;
  const minArea = Math.max(1, diag * diag * 0.0000008);
  const dist = (a: { x: number; y: number; z: number }, b: { x: number; y: number; z: number }) =>
    Math.hypot(a.x - b.x, a.y - b.y, a.z - b.z);
  const area = (a: { x: number; y: number; z: number }, b: { x: number; y: number; z: number }, c: { x: number; y: number; z: number }) => {
    const abx = b.x - a.x, aby = b.y - a.y, abz = b.z - a.z;
    const acx = c.x - a.x, acy = c.y - a.y, acz = c.z - a.z;
    const cx = aby * acz - abz * acy;
    const cy = abz * acx - abx * acz;
    const cz = abx * acy - aby * acx;
    return Math.hypot(cx, cy, cz);
  };
  void chunkIndex;
  for (let i = 2; i < points.length; i++) {
    const p0 = points[i - 2], p1 = points[i - 1], p2 = points[i];
    if (dist(p0, p1) > maxEdge || dist(p1, p2) > maxEdge || dist(p2, p0) > maxEdge) { continue; }
    if (area(p0, p1, p2) < minArea) { continue; }
    const flip = i & 1;
    out.push(base + i - 2, base + (flip ? i : i - 1), base + (flip ? i - 1 : i));
  }
  return out;
}

type CharacterTriPoint = {
  x: number;
  y: number;
  z: number;
  flags?: number;
  posW?: number;
  ctrlW?: number;
};

interface CharacterPoint extends CharacterTriPoint {
  nx: number;
  ny: number;
  nz: number;
  flags: number;
  posW: number;
  ctrlW: number;
}

type CharacterTopologyMode = 'plain' | 'adcPos8000' | 'adcCtrl8000' | 'adcEither8000';

function buildCharacterStripTriangles(
  points: CharacterTriPoint[],
  base: number,
): number[] {
  if (points.length < 3) { return []; }

  let minX = Infinity, minY = Infinity, minZ = Infinity;
  let maxX = -Infinity, maxY = -Infinity, maxZ = -Infinity;

  for (const p of points) {
    if (p.x < minX) { minX = p.x; }
    if (p.x > maxX) { maxX = p.x; }
    if (p.y < minY) { minY = p.y; }
    if (p.y > maxY) { maxY = p.y; }
    if (p.z < minZ) { minZ = p.z; }
    if (p.z > maxZ) { maxZ = p.z; }
  }

  const diag = Math.max(1, Math.hypot(maxX - minX, maxY - minY, maxZ - minZ));

  const dist = (a: CharacterTriPoint, b: CharacterTriPoint): number =>
    Math.hypot(a.x - b.x, a.y - b.y, a.z - b.z);

  const area = (a: CharacterTriPoint, b: CharacterTriPoint, c: CharacterTriPoint): number => {
    const abx = b.x - a.x, aby = b.y - a.y, abz = b.z - a.z;
    const acx = c.x - a.x, acy = c.y - a.y, acz = c.z - a.z;

    return Math.hypot(
      aby * acz - abz * acy,
      abz * acx - abx * acz,
      abx * acy - aby * acx,
    );
  };

  const consecutive: number[] = [];
  for (let i = 1; i < points.length; i++) {
    const d = dist(points[i - 1], points[i]);
    if (d > 0.00001) { consecutive.push(d); }
  }

  consecutive.sort((a, b) => a - b);

  const medianEdge = consecutive[Math.floor(consecutive.length / 2)] || diag;
  const upperEdge = consecutive[Math.floor(consecutive.length * 0.75)] || medianEdge;

  // Keep this close to v4 because v4 restored the human shape. We only add
  // ADC triangle suppression below; we do not aggressively split strips.
  const maxEdge = Math.min(
    diag * 0.50,
    Math.max(medianEdge * 6.0, upperEdge * 4.0, 1),
  );

  const minArea = Math.max(0.000001, diag * diag * 0.00000025);
  const degenerateDistance = Math.max(0.00001, medianEdge * 0.02);

  const isSamePoint = (a: CharacterTriPoint, b: CharacterTriPoint): boolean =>
    dist(a, b) <= degenerateDistance;

  const adcDisabled = (p: CharacterTriPoint, mode: CharacterTopologyMode): boolean => {
    const posW = p.posW ?? 0;
    const ctrlW = p.ctrlW ?? 0;

    if (mode === 'adcPos8000') { return (posW & 0x8000) !== 0; }
    if (mode === 'adcCtrl8000') { return (ctrlW & 0x8000) !== 0; }
    if (mode === 'adcEither8000') { return ((posW | ctrlW) & 0x8000) !== 0; }

    return false;
  };

  const buildCandidate = (
    mode: CharacterTopologyMode,
    splitOnObviousRestarts: boolean,
  ): number[] => {
    const out: number[] = [];

    const emitSegment = (start: number, end: number): void => {
      if (end - start < 3) { return; }

      for (let local = 2; start + local < end; local++) {
        const i = start + local;

        const p0 = points[i - 2];
        const p1 = points[i - 1];
        const p2 = points[i];

        if (adcDisabled(p2, mode)) {
          continue;
        }

        if (isSamePoint(p0, p1) || isSamePoint(p1, p2) || isSamePoint(p0, p2)) {
          continue;
        }

        // Skip bad triangles, but do not over-split the whole strip.
        if (dist(p0, p1) > maxEdge) { continue; }
        if (dist(p1, p2) > maxEdge) { continue; }
        if (dist(p2, p0) > maxEdge) { continue; }
        if (area(p0, p1, p2) < minArea) { continue; }

        const flip = local & 1;

        out.push(
          base + i - 2,
          base + (flip ? i : i - 1),
          base + (flip ? i - 1 : i),
        );
      }
    };

    let segmentStart = 0;

    for (let i = 1; i < points.length; i++) {
      if (!splitOnObviousRestarts) { continue; }

      const previous = points[i - 1];
      const current = points[i];

      const restartBreak = isSamePoint(previous, current);
      const hugeJumpBreak = dist(previous, current) > maxEdge * 1.75;

      if (restartBreak) {
        emitSegment(segmentStart, i);
        segmentStart = i + 1;
        continue;
      }

      if (hugeJumpBreak) {
        emitSegment(segmentStart, i);
        segmentStart = i;
      }
    }

    emitSegment(segmentStart, points.length);
    return out;
  };

  const plain = buildCandidate('plain', false);
  const restartSplit = buildCandidate('plain', true);

  let best = restartSplit.length >= plain.length * 0.85 ? restartSplit : plain;

  // Do not split on flags. Try ADC only as "skip this triangle ending at p2".
  // This is much closer to how GS ADC/restart-style controls behave.
  const adcCandidates = [
    buildCandidate('adcPos8000', false),
    buildCandidate('adcCtrl8000', false),
    buildCandidate('adcEither8000', false),
  ];

  for (const candidate of adcCandidates) {
    // Use ADC suppression only if it keeps most of the currently visible model.
    // This prevents another v3-style hole regression.
    if (candidate.length >= best.length * 0.90 && candidate.length < best.length) {
      best = candidate;
    }
  }

  return best;
}

interface CharacterVifStream {
  offset: number;
  matrixId: number;
  points: CharacterPoint[];
}

export interface CharacterFallbackOptions {
  boneMatrices?: Array<number[] | undefined>;
  positionScale?: number;
}

function findCharacterVifStreams(data: Uint8Array): CharacterVifStream[] {
  if (!data || data.length < 0x80 || data.length > 0x80000) { return []; }

  const view = new DataView(data.buffer, data.byteOffset, data.byteLength);
  const streams: CharacterVifStream[] = [];

  for (let o = 0; o <= data.length - 12; o += 4) {
    const imm = view.getUint16(o + 0, true);
    const numRaw = view.getUint8(o + 2);
    const cmd = view.getUint8(o + 3);
    const baseCmd = cmd & 0x7f;

    // CLIB/FLIB character mesh packets use V4_16 at VU address 0x4.
    // Row 0 is a control/header row.
    // Rows 1,3,5... are vertex positions.
    // Rows 2,4,6... are paired normal/control/flag rows.
    if (baseCmd !== 0x6d || (imm & 0x03ff) !== 0x4) { continue; }

    const num = numRaw === 0 ? 256 : numRaw;
    const dataStart = o + 4;
    const dataEnd = dataStart + num * 8;

    if (num < 7 || num > 256 || dataEnd > data.length) { continue; }

    const headerX = view.getInt16(dataStart + 0, true);
    const headerY = view.getInt16(dataStart + 2, true);
    const headerZ = view.getInt16(dataStart + 4, true);
    const positionCount = view.getInt16(dataStart + 6, true);

    const isPositionStream = headerX === 2 || headerX === 6;
    const expectedRows = positionCount * 2 + 1;

    if (
      !isPositionStream ||
      Math.abs(headerX) > 256 ||
      Math.abs(headerY) > 256 ||
      Math.abs(headerZ) > 256 ||
      positionCount < 3 ||
      positionCount > 128 ||
      Math.abs(num - expectedRows) > 1
    ) {
      continue;
    }

    const points: CharacterPoint[] = [];

    let minX = Infinity, minY = Infinity, minZ = Infinity;
    let maxX = -Infinity, maxY = -Infinity, maxZ = -Infinity;
    let bad = 0;

    for (let i = 0; i < positionCount; i++) {
      const pos = dataStart + (1 + i * 2) * 8;
      const ctrl = dataStart + (2 + i * 2) * 8;

      if (pos + 8 > dataEnd) {
        bad++;
        continue;
      }

      const x = view.getInt16(pos + 0, true);
      const y = view.getInt16(pos + 2, true);
      const z = view.getInt16(pos + 4, true);
      const posW = view.getUint16(pos + 6, true);

      if (Math.abs(x) > 30000 || Math.abs(y) > 30000 || Math.abs(z) > 30000) {
        bad++;
        continue;
      }

      let nx = 0;
      let ny = 0;
      let nz = 0;
      let ctrlW = 0;

      if (ctrl + 8 <= dataEnd) {
        nx = view.getInt16(ctrl + 0, true);
        ny = view.getInt16(ctrl + 2, true);
        nz = view.getInt16(ctrl + 4, true);
        ctrlW = view.getUint16(ctrl + 6, true);
      }

      points.push({
        x,
        y,
        z,
        nx,
        ny,
        nz,
        posW,
        ctrlW,
        flags: posW | ctrlW,
      });

      if (x < minX) { minX = x; }
      if (x > maxX) { maxX = x; }
      if (y < minY) { minY = y; }
      if (y > maxY) { maxY = y; }
      if (z < minZ) { minZ = z; }
      if (z > maxZ) { maxZ = z; }
    }

    if (points.length < 3 || bad > Math.max(1, positionCount * 0.1)) {
      continue;
    }

    const sx = maxX - minX;
    const sy = maxY - minY;
    const sz = maxZ - minZ;
    const span = Math.max(sx, sy, sz);
    const axes = [sx, sy, sz].filter(v => v > 16).length;

    if (span < 20 || span > 60000 || axes < 2) {
      continue;
    }

    streams.push({
      offset: o,
      matrixId: headerZ,
      points,
    });

    o = alignUp(dataEnd, 4) - 4;

    if (streams.length >= 256) { break; }
  }

  return streams;
}

function transformCharacterPoint(
  m: number[],
  x: number,
  y: number,
  z: number,
): { x: number; y: number; z: number } {
  return {
    x: x * m[0] + y * m[4] + z * m[8] + m[12],
    y: x * m[1] + y * m[5] + z * m[9] + m[13],
    z: x * m[2] + y * m[6] + z * m[10] + m[14],
  };
}

export function decodeCharacterFallbackPayload(
  bytes: Uint8Array,
  options: CharacterFallbackOptions = {},
): ZedModel | null {
  const streams = findCharacterVifStreams(bytes);
  if (streams.length === 0) { return null; }

  const vertices: ZedModelVertex[] = [];
  const indices: number[] = [];
  const chunks: ZedModelChunk[] = [];

  const positionScale = options.positionScale ?? (options.boneMatrices ? 1 / 4096 : 1);

  for (const stream of streams) {
    const base = vertices.length;
    const matrix = options.boneMatrices?.[stream.matrixId];

    const points: CharacterTriPoint[] = stream.points.map(p => {
      const x = p.x * positionScale;
      const y = p.y * positionScale;
      const z = p.z * positionScale;

      const transformed = matrix
        ? transformCharacterPoint(matrix, x, y, z)
        : { x, y, z };

      return {
        x: transformed.x,
        y: transformed.y,
        z: transformed.z,
        flags: p.flags,
        posW: p.posW,
        ctrlW: p.ctrlW,
      };
    });

    for (const p of points) {
      vertices.push({
        x: p.x,
        y: p.y,
        z: p.z,
        u: 0,
        v: 0,
      });
    }

    const strip = buildCharacterStripTriangles(points, base);

    for (const i of strip) {
      indices.push(i);
    }

    chunks.push({
      offset: stream.offset,
      vertexCount: stream.points.length,
      triangleCount: (strip.length / 3) | 0,
      decodedTriangleCount: (strip.length / 3) | 0,
      scale: 1,
      vertices: vertices.slice(base),
      indices: strip.map(i => i - base),
      textureName: null,
    });
  }

  if (vertices.length === 0 || indices.length === 0) {
    return null;
  }

  let r = 0;
  for (const v of vertices) {
    const d = Math.hypot(v.x, v.y, v.z);
    if (d > r) { r = d; }
  }

  return {
    chunks,
    vertices,
    indices,
    textureRefs: extractTextureRefs(bytes, 0, bytes.length),
    boundingRadius: r || 1,
  };
}

interface I16Cloud {
  offset: number;
  count: number;
  stride: number;
  span: number;
  score: number;
}

// Heuristic detector for PS2 i16 vertex clouds. The HTML viewer uses this
// as an inspector to surface candidate vertex runs in CLIB MESH_* payloads
// where the proper display list isn't decoded yet.
export function findI16VertexClouds(data: Uint8Array): I16Cloud[] {
  if (!data || data.length < 0x60 || data.length > 0x40000) { return []; }
  const view = new DataView(data.buffer, data.byteOffset, data.byteLength);
  const strides = [0x08, 0x0C, 0x10, 0x14, 0x18, 0x20];
  const out: I16Cloud[] = [];
  for (const stride of strides) {
    for (let start = 0; start <= data.length - stride * 18; start += 2) {
      const cloud = measureI16Cloud(view, data.length, start, stride);
      if (!cloud) { continue; }
      if (!isUsefulI16Cloud(cloud)) { continue; }
      out.push(cloud);
      if (out.length >= 64) { break; }
      start += cloud.count * stride - 2;
    }
    if (out.length >= 64) { break; }
  }
  // Deduplicate near-overlapping detections from different strides.
  out.sort((a, b) => b.score - a.score);
  const deduped: I16Cloud[] = [];
  for (const c of out) {
    if (deduped.some(o => Math.abs(o.offset - c.offset) < Math.max(o.stride, c.stride) * 4)) { continue; }
    deduped.push(c);
    if (deduped.length >= 32) { break; }
  }
  return deduped.sort((a, b) => a.offset - b.offset);
}

function measureI16Cloud(view: DataView, length: number, start: number, stride: number): (I16Cloud & { minX: number; minY: number; minZ: number }) | null {
  let count = 0;
  let zeroish = 0;
  let minX = Infinity, minY = Infinity, minZ = Infinity;
  let maxX = -Infinity, maxY = -Infinity, maxZ = -Infinity;
  let sumSqX = 0, sumSqY = 0, sumSqZ = 0;
  let sumX = 0, sumY = 0, sumZ = 0;
  for (let o = start; o <= length - stride; o += stride) {
    const x = view.getInt16(o + 0, true);
    const y = view.getInt16(o + 2, true);
    const z = view.getInt16(o + 4, true);
    if (Math.abs(x) > 32760 || Math.abs(y) > 32760 || Math.abs(z) > 32760) { break; }
    if (Math.abs(x) + Math.abs(y) + Math.abs(z) < 2) { zeroish++; }
    if (x < minX) { minX = x; } if (x > maxX) { maxX = x; }
    if (y < minY) { minY = y; } if (y > maxY) { maxY = y; }
    if (z < minZ) { minZ = z; } if (z > maxZ) { maxZ = z; }
    sumX += x; sumY += y; sumZ += z;
    sumSqX += x * x; sumSqY += y * y; sumSqZ += z * z;
    count++;
    if (count >= 4096) { break; }
  }
  if (count < 18) { return null; }
  const sx = maxX - minX, sy = maxY - minY, sz = maxZ - minZ;
  const span = Math.max(sx, sy, sz);
  const mx = sumX / count, my = sumY / count, mz = sumZ / count;
  const vx = Math.max(0, sumSqX / count - mx * mx);
  const vy = Math.max(0, sumSqY / count - my * my);
  const vz = Math.max(0, sumSqZ / count - mz * mz);
  const axes = [vx, vy, vz].sort((a, b) => b - a);
  const lineRatio = axes[0] / Math.max(1e-5, axes[1]);
  const score = count * Math.log2(span + 2) / Math.max(1, lineRatio * 0.35);
  // Stash zeroish/lineRatio via score weighting; the useful-test below uses
  // them directly.
  return { offset: start, count, stride, span, score, minX, minY, minZ } as I16Cloud & { minX: number; minY: number; minZ: number };
}

function isUsefulI16Cloud(c: I16Cloud): boolean {
  if (c.count < 24) { return false; }
  if (c.span < 20 || c.span > 60000) { return false; }
  return true;
}

// Top-level fallback: decode CLIB-style payloads via VIF unpack streams,
// emitting one chunk per stream and triangle-strip topology where it looks
// safe. Returns a ZedModel that the existing renderer can consume.
export function decodeFallbackPayload(bytes: Uint8Array): ZedModel | null {
  const effectModel = decodeEffectMdlPayload(bytes);
  if (effectModel) { return effectModel; }

  const vifStreams = findVifUnpackStreams(bytes);
  const useful = selectRenderableVifStreams(vifStreams);
  if (useful.length === 0) {
    // Try i16 vertex clouds as a last resort (point cloud preview).
    const clouds = findI16VertexClouds(bytes);
    if (clouds.length === 0) { return null; }
    const vertices: ZedModelVertex[] = [];
    const indices: number[] = [];
    const chunks: ZedModelChunk[] = [];
    const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    for (const cloud of clouds.slice(0, 16)) {
      const chunkIndex = chunks.length;
      const base = vertices.length;
      for (let i = 0; i < cloud.count; i++) {
        const o = cloud.offset + i * cloud.stride;
        const x = view.getInt16(o + 0, true);
        const y = view.getInt16(o + 2, true);
        const z = view.getInt16(o + 4, true);
        vertices.push({ x, y, z, u: 0, v: 0 });
      }
      // Emit a tiny indexed degenerate per-vertex so the renderer has something
      // to show (point-cloud as repeated 1-vert triangles).
      for (let i = 0; i < cloud.count - 2; i++) {
        indices.push(base + i, base + i + 1, base + i + 2);
      }
      chunks.push({
        offset: cloud.offset,
        vertexCount: cloud.count,
        triangleCount: Math.max(0, cloud.count - 2),
        decodedTriangleCount: Math.max(0, cloud.count - 2),
        scale: 1,
        vertices: vertices.slice(base),
        indices: [],
        textureName: null,
      });
      void chunkIndex;
    }
    if (vertices.length === 0) { return null; }
    let r = 0;
    for (const v of vertices) {
      const d = Math.hypot(v.x, v.y, v.z);
      if (d > r) { r = d; }
    }
    return { chunks, vertices, indices, textureRefs: [], boundingRadius: r || 1 };
  }

  const vertices: ZedModelVertex[] = [];
  const indices: number[] = [];
  const chunks: ZedModelChunk[] = [];
  for (const stream of useful) {
    const chunkIndex = chunks.length;
    const base = vertices.length;
    for (const p of stream.points) {
      vertices.push({ x: p.x, y: p.y, z: p.z, u: 0, v: 0 });
    }
    const strip = buildSafeStripTriangles(stream.points, base, chunkIndex);
    for (const i of strip) { indices.push(i); }
    chunks.push({
      offset: stream.offset,
      vertexCount: stream.points.length,
      triangleCount: strip.length / 3 | 0,
      decodedTriangleCount: strip.length / 3 | 0,
      scale: 1,
      vertices: vertices.slice(base),
      indices: strip.map(i => i - base),
      textureName: null,
    });
  }
  let r = 0;
  for (const v of vertices) {
    const d = Math.hypot(v.x, v.y, v.z);
    if (d > r) { r = d; }
  }
  return { chunks, vertices, indices, textureRefs: [], boundingRadius: r || 1 };
}

// True when a payload is renderable by any of our decoders. Used by the
// component to decide whether to enumerate nested MESH_* entries as
// previewable rows.
export function hasRenderablePayload(bytes: Uint8Array): boolean {
  if (!bytes || bytes.length < 0x40) { return false; }
  if (findChunkOffsets(bytes).length > 0) { return true; }
  if (findEffectVifStarts(bytes).length > 0) { return true; }
  if (bytes.length < 0x400) { return false; }
  if (findVifUnpackStreams(bytes).length > 0) { return true; }
  if (findI16VertexClouds(bytes).length > 0) { return true; }
  return false;
}

// ---------------------------------------------------------------------------
// EFFE_MDL / gun-FX decoder. These payloads do not use the full WORL_MDL
// chunk header, but they do start with the familiar STCYCL word followed by a
// compact VIF command stream. The standalone SOCOM gun-FX preview proved the
// key layout:
//   0x01000101
//   V4_32/V4_16/V4_8 UNPACK commands
//   MSCAL/MSCNT terminator
//   ASCII .tif references near the segment tail
//
// V4_16 rows are position/normal pairs. V4_8 rows are triangle slot indices;
// divide the first three bytes by 3 to get vertex indices. This preserves the
// explicit FX topology instead of falling back to noisy generic strips.
// ---------------------------------------------------------------------------

type EffectVifValue = number[];

interface EffectVifArray {
  cmd: number;
  name: string;
  imm: number;
  num: number;
  offset: number;
  rows: EffectVifValue[];
}

interface EffectVifSegment {
  start: number;
  end: number;
  arrays: EffectVifArray[];
}

const effectUnpackInfo: Record<number, { name: string; comps: number; bytesPerComponent: number }> = {
  0x60: { name: 'S32', comps: 1, bytesPerComponent: 4 },
  0x61: { name: 'S16', comps: 1, bytesPerComponent: 2 },
  0x62: { name: 'S8', comps: 1, bytesPerComponent: 1 },
  0x64: { name: 'V2_32', comps: 2, bytesPerComponent: 4 },
  0x65: { name: 'V2_16', comps: 2, bytesPerComponent: 2 },
  0x66: { name: 'V2_8', comps: 2, bytesPerComponent: 1 },
  0x68: { name: 'V3_32', comps: 3, bytesPerComponent: 4 },
  0x69: { name: 'V3_16', comps: 3, bytesPerComponent: 2 },
  0x6a: { name: 'V3_8', comps: 3, bytesPerComponent: 1 },
  0x6c: { name: 'V4_32', comps: 4, bytesPerComponent: 4 },
  0x6d: { name: 'V4_16', comps: 4, bytesPerComponent: 2 },
  0x6e: { name: 'V4_8', comps: 4, bytesPerComponent: 1 },
  0x6f: { name: 'V4_5', comps: 4, bytesPerComponent: 0 },
};

const effectKnownVifCommands = new Set([0, 1, 0x10, 0x11, 0x13, 0x14, 0x17, 0x20, 0x30, 0x31]);

function findEffectVifStarts(bytes: Uint8Array): number[] {
  const starts: number[] = [];
  for (let o = 0; o + 8 < bytes.length; o += 0x10) {
    if (readU32LE(bytes, o) !== 0x01000101) { continue; }
    const next = readU32LE(bytes, o + 4);
    if ((next >>> 24) === 0x6c) { starts.push(o); }
  }
  return starts;
}

function effectCmdName(cmd: number): string {
  switch (cmd) {
    case 0: return 'NOP';
    case 1: return 'STCYCL';
    case 0x10: return 'FLUSH';
    case 0x11: return 'FLUSHE';
    case 0x13: return 'FLUSHA';
    case 0x14: return 'MSCAL';
    case 0x17: return 'MSCNT';
    case 0x20: return 'STMASK';
    case 0x30: return 'STROW';
    case 0x31: return 'STCOL';
    default: return effectUnpackInfo[cmd]?.name ?? `CMD_${cmd.toString(16)}`;
  }
}

function effectUnpackByteLength(cmd: number, num: number): number {
  const info = effectUnpackInfo[cmd];
  if (!info) { return 0; }
  const bytes = info.bytesPerComponent === 0
    ? num * 4
    : num * info.comps * info.bytesPerComponent;
  return alignUp(bytes, 4);
}

function readEffectArray(bytes: Uint8Array, offset: number, cmd: number, num: number): EffectVifValue[] {
  const info = effectUnpackInfo[cmd];
  if (!info || info.bytesPerComponent === 0) { return []; }
  const out: EffectVifValue[] = [];
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  const rowSize = info.comps * info.bytesPerComponent;
  if (offset + num * rowSize > bytes.length) { return []; }

  for (let i = 0; i < num; i++) {
    const row: number[] = [];
    const rowOffset = offset + i * rowSize;
    for (let c = 0; c < info.comps; c++) {
      const valueOffset = rowOffset + c * info.bytesPerComponent;
      if (info.bytesPerComponent === 4) {
        row.push(cmd === 0x6c || cmd === 0x68 || cmd === 0x64
          ? view.getFloat32(valueOffset, true)
          : view.getInt32(valueOffset, true));
      } else if (info.bytesPerComponent === 2) {
        row.push(view.getInt16(valueOffset, true));
      } else {
        row.push(bytes[valueOffset]);
      }
    }
    out.push(row);
  }
  return out;
}

function parseEffectVifSegment(bytes: Uint8Array, start: number): EffectVifSegment | null {
  const arrays: EffectVifArray[] = [];
  let o = start;
  let guard = 0;
  while (o + 4 <= bytes.length && guard++ < 96) {
    const word = readU32LE(bytes, o);
    const imm = word & 0xffff;
    const num = (word >>> 16) & 0xff;
    const cmd = (word >>> 24) & 0xff;
    const dataLen = effectUnpackByteLength(cmd, num);
    o += 4;

    if (dataLen) {
      if (o + dataLen > bytes.length) { return null; }
      arrays.push({
        cmd,
        name: effectCmdName(cmd),
        imm,
        num,
        offset: o,
        rows: readEffectArray(bytes, o, cmd, num),
      });
      o += dataLen;
    }

    if (cmd === 0x17 || cmd === 0x14) {
      return { start, end: alignUp(o, 0x10), arrays };
    }
    if (!effectUnpackInfo[cmd] && !effectKnownVifCommands.has(cmd)) { return null; }
  }
  return null;
}

function chooseEffectTextureName(markers: TifMarker[], segment: EffectVifSegment, nextStart: number): string | null {
  const afterInSegment = markers.find(m => m.offset >= segment.end && m.offset < nextStart);
  if (afterInSegment) { return afterInSegment.name; }
  const after = markers.find(m => m.offset >= segment.end);
  return after?.name ?? markers[0]?.name ?? null;
}

function decodeEffectChunk(bytes: Uint8Array, segment: EffectVifSegment, textureName: string | null): ZedModelChunk | null {
  void bytes;
  const v16 = segment.arrays
    .filter(a => a.cmd === 0x6d)
    .sort((a, b) => b.num - a.num)[0];
  const v32 = segment.arrays
    .filter(a => a.cmd === 0x6c && a.rows.some(r => r.some(v => Number.isFinite(v) && Math.abs(v) > 0.001)))
    .slice(-1)[0];
  const i8 = segment.arrays
    .filter(a => a.cmd === 0x6e)
    .sort((a, b) => b.num - a.num)[0];

  const positions: Array<{ x: number; y: number; z: number }> = [];
  if (v16 && v16.rows.length >= 2) {
    for (let i = 0; i + 1 < v16.rows.length; i += 2) {
      const p = v16.rows[i];
      positions.push({ x: p[0] / 4096, y: p[1] / 4096, z: p[2] / 4096 });
    }
  } else if (v32 && v32.rows.length >= 3) {
    for (const p of v32.rows) {
      positions.push({ x: p[0], y: p[1], z: p[2] });
    }
  }
  if (positions.length === 0) { return null; }

  const minX = Math.min(...positions.map(p => p.x));
  const maxX = Math.max(...positions.map(p => p.x));
  const minY = Math.min(...positions.map(p => p.y));
  const maxY = Math.max(...positions.map(p => p.y));
  const sx = maxX - minX || 1;
  const sy = maxY - minY || 1;
  const vertices = positions.map(p => ({
    x: p.x,
    y: p.y,
    z: p.z,
    // The preview HTML did not surface dedicated UV rows. Use stable planar
    // UVs so textured mode reveals the referenced atlas instead of sampling
    // one corner pixel across the whole FX card.
    u: (p.x - minX) / sx,
    v: (p.y - minY) / sy,
  }));

  const indices: number[] = [];
  if (i8) {
    for (const r of i8.rows) {
      const a = Math.floor(r[0] / 3);
      const b = Math.floor(r[1] / 3);
      const c = Math.floor(r[2] / 3);
      if (a < 0 || b < 0 || c < 0) { continue; }
      if (a >= vertices.length || b >= vertices.length || c >= vertices.length) { continue; }
      if (a === b || b === c || a === c) { continue; }
      indices.push(a, b, c);
    }
  }
  if (indices.length === 0 && vertices.length === 4) {
    indices.push(0, 1, 2, 0, 2, 3);
  }
  if (indices.length === 0 && vertices.length >= 3) {
    for (let i = 0; i + 2 < vertices.length; i += 3) {
      indices.push(i, i + 1, i + 2);
    }
  }
  if (indices.length === 0) { return null; }

  return {
    offset: segment.start,
    vertexCount: vertices.length,
    triangleCount: indices.length / 3 | 0,
    decodedTriangleCount: indices.length / 3 | 0,
    scale: 1,
    vertices,
    indices,
    textureName,
    textureBinding: textureName ? 'tail' : undefined,
  };
}

function decodeEffectMdlPayload(bytes: Uint8Array): ZedModel | null {
  const starts = findEffectVifStarts(bytes);
  if (starts.length === 0) { return null; }
  const markers = extractTextureMarkers(bytes);
  const chunks: ZedModelChunk[] = [];
  for (let i = 0; i < starts.length; i++) {
    const start = starts[i];
    if (chunks.some(c => start >= c.offset && start < c.offset + Math.max(1, c.vertexCount * 0x10))) { continue; }
    const segment = parseEffectVifSegment(bytes, start);
    if (!segment) { continue; }
    const nextStart = starts.find(s => s > segment.end) ?? bytes.length;
    const textureName = chooseEffectTextureName(markers, segment, nextStart);
    const chunk = decodeEffectChunk(bytes, segment, textureName);
    if (chunk) { chunks.push(chunk); }
  }
  if (chunks.length === 0) { return null; }

  const vertices: ZedModelVertex[] = [];
  const indices: number[] = [];
  let radius = 0;
  for (const chunk of chunks) {
    const base = vertices.length;
    for (const v of chunk.vertices) {
      vertices.push(v);
      const d = Math.hypot(v.x, v.y, v.z);
      if (d > radius) { radius = d; }
    }
    for (const idx of chunk.indices) {
      indices.push(base + idx);
    }
  }

  const textureRefs = [...new Set(markers.map(m => m.name))];
  return {
    chunks,
    vertices,
    indices,
    textureRefs,
    boundingRadius: radius || 1,
  };
}
