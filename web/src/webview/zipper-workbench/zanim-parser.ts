export interface ZanimEntryLike {
  index: number;
  name: string;
  size: number;
  type?: number;
}

export interface ZanimTreeEntry extends ZanimEntryLike {
  type: number;
  parent: number;
  depth: number;
  children: number[];
}

export interface ZanimParams {
  words: number[];
  floats: number[];
}

export interface ZanimNodeRef {
  words: [number, number, number, number];
}

export interface ZanimSequenceSummary {
  size: number;
  headerWords: number[];
  previewU16: number[];
  previewHex: string;
}

export interface ZanimAnimationSummary {
  entryIndex: number;
  name: string;
  childIndices: number[];
  params: ZanimParams | null;
  nameIndices: number[];
  nameRefs: string[];
  nodeRefs: ZanimNodeRef[];
  seqSize: number;
  sequence: ZanimSequenceSummary | null;
}

export interface ZanimSetSummary {
  entryIndex: number;
  name: string;
  nameTable: string[];
  rdrPaths: string[];
  animations: ZanimAnimationSummary[];
  declaredNameCount: number | null;
  declaredAnimationCount: number | null;
}

export interface ZanimArchiveSummary {
  isZanim: boolean;
  entries: ZanimTreeEntry[];
  mainParams: ZanimParams | null;
  declaredSetCount: number | null;
  sets: ZanimSetSummary[];
  totalNames: number;
  totalRdrPaths: number;
  totalAnimations: number;
  nodesLoaded: boolean;
}

function u16(bytes: Uint8Array, offset: number): number {
  return offset + 2 <= bytes.length
    ? bytes[offset] | (bytes[offset + 1] << 8)
    : 0;
}

function u32(bytes: Uint8Array, offset: number): number {
  return offset + 4 <= bytes.length
    ? (bytes[offset] |
      (bytes[offset + 1] << 8) |
      (bytes[offset + 2] << 16) |
      (bytes[offset + 3] << 24)) >>> 0
    : 0;
}

function f32(bytes: Uint8Array, offset: number): number {
  if (offset + 4 > bytes.length) { return 0; }
  const view = new DataView(bytes.buffer, bytes.byteOffset + offset, 4);
  return view.getFloat32(0, true);
}

function readCount(bytes: Uint8Array | undefined): number | null {
  if (!bytes || bytes.length < 4) { return null; }
  return u32(bytes, 0);
}

function readParams(bytes: Uint8Array | undefined): ZanimParams | null {
  if (!bytes || bytes.length < 4) { return null; }
  const words: number[] = [];
  const floats: number[] = [];
  for (let offset = 0; offset + 4 <= bytes.length; offset += 4) {
    words.push(u32(bytes, offset));
    floats.push(f32(bytes, offset));
  }
  return { words, floats };
}

function hexByte(value: number): string {
  return value.toString(16).padStart(2, '0').toUpperCase();
}

function hexBytes(bytes: Uint8Array, max = 48): string {
  return [...bytes.slice(0, max)].map(hexByte).join(' ');
}

function buildTree(entries: ZanimEntryLike[]): ZanimTreeEntry[] {
  const sorted = entries.slice().sort((a, b) => a.index - b.index);
  const out = sorted.map(e => ({
    index: e.index,
    name: e.name,
    size: e.size,
    type: e.type ?? 0,
    parent: -1,
    depth: 0,
    children: [] as number[],
  }));
  const byPosition = new Map<number, number>();
  for (let i = 0; i < out.length; i++) { byPosition.set(out[i].index, i); }

  let cursor = 0;
  const build = (parentIndex: number, depth: number): number => {
    const pos = cursor++;
    const node = out[pos];
    node.parent = parentIndex;
    node.depth = depth;
    for (let i = 0; i < node.type && cursor < out.length; i++) {
      const childPos = build(node.index, depth + 1);
      node.children.push(out[childPos].index);
    }
    return pos;
  };

  while (cursor < out.length) {
    build(-1, 0);
  }

  // If indexes are not dense for any future archive, parent/child references
  // still use real entry indexes. The map above is kept for validation.
  void byPosition;
  return out;
}

function childByName(entriesByIndex: Map<number, ZanimTreeEntry>, parent: ZanimTreeEntry, name: string): ZanimTreeEntry | null {
  const target = name.toLowerCase();
  for (const childIndex of parent.children) {
    const child = entriesByIndex.get(childIndex);
    if (child?.name.toLowerCase() === target) { return child; }
  }
  return null;
}

function childrenOf(entriesByIndex: Map<number, ZanimTreeEntry>, parent: ZanimTreeEntry | null): ZanimTreeEntry[] {
  if (!parent) { return []; }
  return parent.children.map(i => entriesByIndex.get(i)).filter((e): e is ZanimTreeEntry => !!e);
}

function readNameIndices(bytes: Uint8Array | undefined): number[] {
  if (!bytes) { return []; }
  const out: number[] = [];
  for (let offset = 0; offset + 2 <= bytes.length; offset += 2) {
    out.push(u16(bytes, offset));
  }
  return out;
}

function readNodeRefs(bytes: Uint8Array | undefined): ZanimNodeRef[] {
  if (!bytes) { return []; }
  const out: ZanimNodeRef[] = [];
  for (let offset = 0; offset + 8 <= bytes.length; offset += 8) {
    out.push({
      words: [
        u16(bytes, offset),
        u16(bytes, offset + 2),
        u16(bytes, offset + 4),
        u16(bytes, offset + 6),
      ],
    });
  }
  return out;
}

function readSequence(bytes: Uint8Array | undefined): ZanimSequenceSummary | null {
  if (!bytes) { return null; }
  const headerWords: number[] = [];
  for (let offset = 0; offset + 4 <= Math.min(bytes.length, 16); offset += 4) {
    headerWords.push(u32(bytes, offset));
  }
  const previewU16: number[] = [];
  const previewStart = bytes.length >= 16 ? 16 : 0;
  for (let offset = previewStart; offset + 2 <= Math.min(bytes.length, previewStart + 64); offset += 2) {
    previewU16.push(u16(bytes, offset));
  }
  return {
    size: bytes.length,
    headerWords,
    previewU16,
    previewHex: hexBytes(bytes, 64),
  };
}

export function parseZanimArchive(
  entries: ZanimEntryLike[],
  dataForEntry: (index: number) => Uint8Array | undefined,
): ZanimArchiveSummary {
  const tree = buildTree(entries);
  const byIndex = new Map(tree.map(e => [e.index, e]));
  const rootNames = new Set(tree.filter(e => e.parent < 0).map(e => e.name.toLowerCase()));
  const hasZanimShape = rootNames.has('anim_main_params') && rootNames.has('anim_sets');

  const empty: ZanimArchiveSummary = {
    isZanim: hasZanimShape,
    entries: tree,
    mainParams: null,
    declaredSetCount: null,
    sets: [],
    totalNames: 0,
    totalRdrPaths: 0,
    totalAnimations: 0,
    nodesLoaded: false,
  };
  if (!hasZanimShape) { return empty; }

  const rootByName = (name: string): ZanimTreeEntry | null =>
    tree.find(e => e.parent < 0 && e.name.toLowerCase() === name.toLowerCase()) ?? null;

  const animSets = rootByName('Anim_Sets');
  const sets = childrenOf(byIndex, animSets).map(setEntry => {
    const nameTableNode = childByName(byIndex, setEntry, 'Name_Table');
    const rdrPathNode = childByName(byIndex, setEntry, 'RdrPath_List');
    const animationListNode = childByName(byIndex, setEntry, 'Animation_List');
    const nameTable = childrenOf(byIndex, nameTableNode).map(e => e.name);
    const rdrPaths = childrenOf(byIndex, rdrPathNode).map(e => e.name);

    const animations = childrenOf(byIndex, animationListNode).map(animEntry => {
      const animParams = childByName(byIndex, animEntry, 'Anim_Params');
      const nameIndexTable = childByName(byIndex, animEntry, 'Name_Index_Table');
      const nodeRefList = childByName(byIndex, animEntry, 'Node_Ref_List');
      const seqDataSize = childByName(byIndex, animEntry, 'Seq_Data_Size');
      const seqData = childByName(byIndex, animEntry, 'Seq_Data');
      const nameIndices = readNameIndices(nameIndexTable ? dataForEntry(nameIndexTable.index) : undefined);
      return {
        entryIndex: animEntry.index,
        name: animEntry.name,
        childIndices: animEntry.children.slice(),
        params: readParams(animParams ? dataForEntry(animParams.index) : undefined),
        nameIndices,
        nameRefs: nameIndices.map(i => nameTable[i] ?? `#${i}`),
        nodeRefs: readNodeRefs(nodeRefList ? dataForEntry(nodeRefList.index) : undefined),
        seqSize: readCount(seqDataSize ? dataForEntry(seqDataSize.index) : undefined) ?? 0,
        sequence: readSequence(seqData ? dataForEntry(seqData.index) : undefined),
      };
    });

    return {
      entryIndex: setEntry.index,
      name: setEntry.name,
      nameTable,
      rdrPaths,
      animations,
      declaredNameCount: readCount(childByName(byIndex, setEntry, 'Name_Table_Count') ? dataForEntry(childByName(byIndex, setEntry, 'Name_Table_Count')!.index) : undefined),
      declaredAnimationCount: readCount(childByName(byIndex, setEntry, 'Animation_List_Count') ? dataForEntry(childByName(byIndex, setEntry, 'Animation_List_Count')!.index) : undefined),
    };
  });

  const totalNames = sets.reduce((sum, set) => sum + set.nameTable.length, 0);
  const totalRdrPaths = sets.reduce((sum, set) => sum + set.rdrPaths.length, 0);
  const totalAnimations = sets.reduce((sum, set) => sum + set.animations.length, 0);
  const nonEmptyPayloads = tree.filter(e => e.size > 0).length;
  const loadedPayloads = tree.filter(e => e.size > 0 && !!dataForEntry(e.index)).length;

  return {
    isZanim: true,
    entries: tree,
    mainParams: readParams(rootByName('Anim_Main_Params') ? dataForEntry(rootByName('Anim_Main_Params')!.index) : undefined),
    declaredSetCount: readCount(rootByName('Anim_Set_Count') ? dataForEntry(rootByName('Anim_Set_Count')!.index) : undefined),
    sets,
    totalNames,
    totalRdrPaths,
    totalAnimations,
    nodesLoaded: nonEmptyPayloads === 0 || loadedPayloads >= nonEmptyPayloads,
  };
}

export function findZanimAnimationForEntry(
  summary: ZanimArchiveSummary | null,
  entryIndex: number | null,
  entryName?: string | null,
): ZanimAnimationSummary | null {
  if (!summary || !summary.isZanim || entryIndex === null) { return null; }
  const lowerName = entryName?.toLowerCase();
  for (const set of summary.sets) {
    for (const anim of set.animations) {
      if (anim.entryIndex === entryIndex || anim.childIndices.includes(entryIndex)) {
        return anim;
      }
    }
  }
  if (lowerName) {
    for (const set of summary.sets) {
      const match = set.animations.find(a => a.name.toLowerCase() === lowerName);
      if (match) { return match; }
    }
  }
  return null;
}
