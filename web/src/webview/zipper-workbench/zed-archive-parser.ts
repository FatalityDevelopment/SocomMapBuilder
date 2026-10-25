// Tree-based parser for ZED containers. The key correction over the original
// flat-list reader: each record's `type` field is actually a **child count**.
// Records are stored pre-order, so we recursively consume `childCount` records
// after each parent to build the proper tree. This matches the working
// reference implementation, where for example WEAP_MDL.ZED entries look like:
//
//   m4Acarbine_203 (childCount=4)
//     N000_000 (childCount=0)
//     N000_001 (childCount=0)
//     N001_000 (childCount=0)
//     N001_001 (childCount=0)
//
// Without the tree, "Nxxx_xxx" entries appear as siblings of the weapon
// payload, which is misleading and produces wrong UV / chunk grouping.

const FIXED_HEADER_SIZE = 0x64;
const RECORD_SIZE = 0x10;

export interface InspectedZedEntry {
  index: number;
  name: string;
  size: number;
  type: number;        // childCount in disguise
  extension: string;
  depth: number;
  parent: number;      // entry index of parent, or -1 for roots
  children: number[];  // entry indices of immediate children
}

export interface InspectedZedArchive {
  entries: InspectedZedEntry[];
  entryData: Map<number, Uint8Array>;
  rootIndices: number[];
  rootValue: number;
  stringBasePointer: number;
  headerWord5C: number;
}

function u32(bytes: Uint8Array, off: number): number {
  return (
    bytes[off] |
    (bytes[off + 1] << 8) |
    (bytes[off + 2] << 16) |
    (bytes[off + 3] << 24)
  ) >>> 0;
}

function readNullTerminated(bytes: Uint8Array, start: number, end: number): string {
  let zero = start;
  while (zero < end && bytes[zero] !== 0) { zero++; }
  let out = '';
  for (let i = start; i < zero; i++) {
    const b = bytes[i];
    if (b >= 0x20 && b <= 0x7e) { out += String.fromCharCode(b); }
  }
  return out;
}

export function parseZedArchive(bytes: Uint8Array): InspectedZedArchive {
  if (bytes.length < FIXED_HEADER_SIZE) {
    throw new Error(`Too small for a ZED container (${bytes.length} bytes)`);
  }

  const slotCount = u32(bytes, 0x04);
  if (slotCount === 0) { throw new Error('Invalid slot count'); }

  const stringTableSize = u32(bytes, 0x08);
  const stringBase = u32(bytes, 0x0c);
  const headerWord5C = u32(bytes, 0x5c);
  const bodySize = u32(bytes, 0x54);

  const stringTableStart = FIXED_HEADER_SIZE;
  const stringTableEnd = stringTableStart + stringTableSize;
  if (stringTableEnd > bytes.length) {
    throw new Error('String table exceeds archive size');
  }

  const entryCount = slotCount - 1;
  const firstRecordOffset = FIXED_HEADER_SIZE + stringTableSize + 0x10;
  const recordEnd = firstRecordOffset + entryCount * RECORD_SIZE;
  const bodyStart = bytes.length - bodySize;
  if (bodyStart < recordEnd) {
    throw new Error('Record table overlaps body region');
  }

  const rootValue = u32(bytes, firstRecordOffset - 4);

  // Stage 1: flat decode of each record.
  const entries: InspectedZedEntry[] = [];
  const entryData = new Map<number, Uint8Array>();

  for (let i = 0; i < entryCount; i++) {
    const recordOffset = firstRecordOffset + i * RECORD_SIZE;
    const namePointer = u32(bytes, recordOffset + 0x00);
    const relativeOffset = u32(bytes, recordOffset + 0x04);
    const size = u32(bytes, recordOffset + 0x08);
    const childCount = u32(bytes, recordOffset + 0x0c);

    let name = '(unnamed)';
    if (namePointer >= stringBase) {
      const stringOffset = stringTableStart + (namePointer - stringBase);
      if (stringOffset >= stringTableStart && stringOffset < stringTableEnd) {
        name = readNullTerminated(bytes, stringOffset, stringTableEnd);
      }
    }

    let payload = new Uint8Array(0);
    if (size > 0) {
      const payloadStart = bodyStart + relativeOffset;
      const payloadEnd = payloadStart + size;
      if (payloadStart >= bodyStart && payloadEnd <= bytes.length) {
        payload = bytes.subarray(payloadStart, payloadEnd);
      }
    }

    const extension = (() => {
      const dot = name.lastIndexOf('.');
      return dot >= 0 ? name.substring(dot) : '';
    })();

    entries.push({
      index: i,
      name,
      size,
      type: childCount,
      extension,
      depth: 0,
      parent: -1,
      children: [],
    });
    entryData.set(i, payload);
  }

  // Stage 2: build the tree by walking pre-order and consuming `childCount`
  // immediate children after each parent.
  const rootIndices: number[] = [];
  let cursor = 0;
  const build = (parentIdx: number, depth: number): number => {
    const idx = cursor++;
    const node = entries[idx];
    node.parent = parentIdx;
    node.depth = depth;
    for (let k = 0; k < node.type && cursor < entries.length; k++) {
      const childIdx = build(idx, depth + 1);
      node.children.push(childIdx);
    }
    return idx;
  };
  while (cursor < entries.length) {
    rootIndices.push(build(-1, 0));
  }

  return {
    entries,
    entryData,
    rootIndices,
    rootValue,
    stringBasePointer: stringBase,
    headerWord5C,
  };
}

// Find a node by name anywhere in the tree (depth-first). Used for "models",
// "textures", "palettes" container lookups and named weapon entries.
export function findNodeByName(
  archive: InspectedZedArchive,
  name: string,
): InspectedZedEntry | null {
  const target = name.toLowerCase();
  for (const e of archive.entries) {
    if (e.name.toLowerCase() === target) { return e; }
  }
  return null;
}

// Convenience: return the immediate children entries of a parent.
export function childrenOf(
  archive: InspectedZedArchive,
  parent: InspectedZedEntry,
): InspectedZedEntry[] {
  return parent.children.map(i => archive.entries[i]);
}

// Find a named child of a parent entry.
export function findChild(
  archive: InspectedZedArchive,
  parent: InspectedZedEntry,
  childName: string,
): InspectedZedEntry | null {
  const target = childName.toLowerCase();
  for (const ci of parent.children) {
    const c = archive.entries[ci];
    if (c.name.toLowerCase() === target) { return c; }
  }
  return null;
}
