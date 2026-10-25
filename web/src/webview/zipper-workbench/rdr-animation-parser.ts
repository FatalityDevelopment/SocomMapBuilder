export type Vec3 = [number, number, number];
export type Vec4 = [number, number, number, number];

export interface RdrAnimationBoneRecord {
  offset: number;
  a: number;
  b: number;
  c: number;
  d: number;
}

export interface RdrAnimationTrack {
  boneIndex: number;
  boneName: string;
  offset: number;
  type: number;
  typeName: string;
  size?: number;
  end?: number;
  baseVec4?: Vec4;
  posSamples?: Vec3[];
  staticQuat?: Vec4;
  quatSamples?: Vec4[];
  firstQuatNorm?: number;
  staticQuatNorm?: number;
}

export interface RdrAnimationClip {
  name: string;
  fileSize: number;
  version: number;
  duration: number;
  frameIntervals: number;
  sampleCount: number;
  boneCount: number;
  minNorm: number;
  maxNorm: number;
  nameOffset: number;
  animOffset: number;
  bones: string[];
  boneRecords: RdrAnimationBoneRecord[];
  tracks: RdrAnimationTrack[];
  parents: number[];
  counts: Record<string, number>;
  attachmentBones: string[];
  parseOk: boolean;
  parseEnd?: number;
  errors: string[];
  animationLike: boolean;
}

export interface RdrAnimationPose {
  worldPos: Vec3[];
  worldRot: Vec4[];
}

export interface RdrAnimationBounds {
  min?: Vec3;
  max?: Vec3;
  center: Vec3;
  size: Vec3;
  maxDim: number;
}

function u32(dv: DataView, offset: number): number {
  return offset + 4 <= dv.byteLength ? dv.getUint32(offset, true) : 0;
}

function i16(dv: DataView, offset: number): number {
  return offset + 2 <= dv.byteLength ? dv.getInt16(offset, true) : 0;
}

function f32(dv: DataView, offset: number): number {
  return offset + 4 <= dv.byteLength ? dv.getFloat32(offset, true) : 0;
}

function align4(value: number): number {
  return (value + 3) & ~3;
}

function vecI16(dv: DataView, offset: number, count: 3): Vec3;
function vecI16(dv: DataView, offset: number, count: 4): Vec4;
function vecI16(dv: DataView, offset: number, count: 3 | 4): Vec3 | Vec4 {
  const out: number[] = [];
  for (let i = 0; i < count; i++) {
    out.push(i16(dv, offset + i * 2));
  }
  return out as Vec3 | Vec4;
}

function quatNorm(q: Vec4): number {
  return Math.hypot(q[0], q[1], q[2], q[3]);
}

function ensure(dv: DataView, offset: number, size: number, label: string): void {
  if (offset < 0 || offset + size > dv.byteLength) {
    throw new Error(`${label} outside file at ${hex(offset)}`);
  }
}

export function hex(value: number | undefined, width = 0): string {
  if (value === undefined || !Number.isFinite(value)) { return '-'; }
  const body = Number(value >>> 0).toString(16).toUpperCase();
  return '0x' + (width > 0 ? body.padStart(width, '0') : body);
}

export function cleanBoneName(name: string): string {
  return (name || '').replace(/^-+/, '');
}

function boneDepth(name: string): number {
  return ((name || '').match(/^-*/) || [''])[0].length;
}

export function isAttachmentBone(name: string): boolean {
  return /weapon|rifle|gun|pistol|cig|cigar|smoke|prop|attach/i.test(name);
}

export function trackTypeKey(type: number): string {
  return `0x${type.toString(16).toUpperCase().padStart(2, '0')}`;
}

export function describeTrackType(type: number): string {
  switch (type) {
    case 0x0c: return 'anim base + anim quat';
    case 0x1c: return 'anim base + static quat';
    case 0x2c: return 'static base + anim quat';
    case 0x3c: return 'static base + static quat';
    default: return 'unknown';
  }
}

function readNames(dv: DataView, start: number, end: number, maxCount: number): string[] {
  const names: string[] = [];
  let p = start;
  while (p < end && names.length < maxCount) {
    const chars: string[] = [];
    while (p < end) {
      const c = dv.getUint8(p++);
      if (c === 0) { break; }
      if (c >= 0x20 && c <= 0x7e) {
        chars.push(String.fromCharCode(c));
      }
    }
    if (chars.length > 0) {
      names.push(chars.join(''));
    }
  }
  return names;
}

function readPosSamples(dv: DataView, offset: number, count: number): Vec3[] {
  ensure(dv, offset, count * 6, 'position samples');
  const out: Vec3[] = [];
  for (let i = 0; i < count; i++) {
    out.push(vecI16(dv, offset + i * 6, 3));
  }
  return out;
}

function readQuatSamples(dv: DataView, offset: number, count: number): Vec4[] {
  ensure(dv, offset, count * 8, 'quaternion samples');
  const out: Vec4[] = [];
  for (let i = 0; i < count; i++) {
    out.push(vecI16(dv, offset + i * 8, 4));
  }
  return out;
}

function countTrackTypes(tracks: RdrAnimationTrack[]): Record<string, number> {
  const counts: Record<string, number> = { '0x0C': 0, '0x1C': 0, '0x2C': 0, '0x3C': 0 };
  for (const track of tracks) {
    const key = trackTypeKey(track.type);
    counts[key] = (counts[key] ?? 0) + 1;
  }
  return counts;
}

function findAttachmentBones(bones: string[]): string[] {
  return bones.filter(isAttachmentBone);
}

function buildParentIndices(bones: string[]): number[] {
  const parents = new Array<number>(bones.length).fill(-1);
  const names = bones.map(cleanBoneName).map(n => n.toLowerCase());
  const find = (...parts: string[]): number => names.findIndex(n => parts.every(p => n.includes(p)));
  const set = (child: number, parent: number): void => {
    if (child >= 0) { parents[child] = parent; }
  };

  const root = find('skel_root');
  const hips = find('hips');
  const spinelo = find('spinelo');
  const spinehi = find('spinehi');
  const neck = find('neck');
  const head = find('head');
  const lbicep = find('lbicep');
  const lforearm = find('lforearm');
  const lhand = find('lhand');
  const rbicep = find('rbicep');
  const rforearm = find('rforearm');
  const rhand = find('rhand');
  const lthigh = find('lthigh');
  const lcalf = find('lcalf');
  const lfoot = find('lfoot');
  const ltoe = find('ltoe');
  const rthigh = find('rthigh');
  const rcalf = find('rcalf');
  const rfoot = find('rfoot');
  const rtoe = find('rtoe');
  const aim = find('aimnodes');

  set(hips, root);
  set(aim, hips);
  set(spinelo, hips);
  set(spinehi, spinelo >= 0 ? spinelo : hips);
  set(neck, spinehi >= 0 ? spinehi : hips);
  set(head, neck >= 0 ? neck : spinehi);
  set(lbicep, spinehi >= 0 ? spinehi : hips);
  set(lforearm, lbicep);
  set(lhand, lforearm);
  set(rbicep, spinehi >= 0 ? spinehi : hips);
  set(rforearm, rbicep);
  set(rhand, rforearm);
  set(lthigh, hips);
  set(lcalf, lthigh);
  set(lfoot, lcalf);
  set(ltoe, lfoot);
  set(rthigh, hips);
  set(rcalf, rthigh);
  set(rfoot, rcalf);
  set(rtoe, rfoot);

  for (let i = 0; i < names.length; i++) {
    if (parents[i] !== -1 || i === root) { continue; }
    if (/weapon|rifle|gun|pistol/.test(names[i])) {
      parents[i] = rhand >= 0 ? rhand : (lhand >= 0 ? lhand : hips);
    }
  }

  const depths = bones.map(boneDepth);
  for (let i = 0; i < bones.length; i++) {
    if (parents[i] !== -1 || i === root || i === 0) { continue; }
    const depth = depths[i];
    for (let j = i - 1; j >= 0; j--) {
      if (depths[j] < depth) {
        parents[i] = j;
        break;
      }
    }
    if (parents[i] === -1 && root >= 0 && i !== root && names[i] !== 'skel_root') {
      parents[i] = root;
    }
  }

  if (root >= 0) { parents[root] = -1; }
  return parents;
}

export function fallbackBoneOffset(name: string, index: number): Vec4 {
  const clean = cleanBoneName(name).toLowerCase();
  if (clean === 'skel_root') { return [0, 0, 0, 0]; }
  if (clean === 'hips') { return [0, 70, 0, 0]; }
  if (clean.includes('spine')) { return [0, 28, 0, 0]; }
  if (clean.includes('neck')) { return [0, 22, 0, 0]; }
  if (clean.includes('head')) { return [0, 20, 0, 0]; }
  if (clean.includes('lbicep')) { return [-24, 12, 0, 0]; }
  if (clean.includes('lforearm')) { return [-28, -22, 0, 0]; }
  if (clean.includes('lhand')) { return [-18, -22, 0, 0]; }
  if (clean.includes('rbicep')) { return [24, 12, 0, 0]; }
  if (clean.includes('rforearm')) { return [28, -22, 0, 0]; }
  if (clean.includes('rhand')) { return [18, -22, 0, 0]; }
  if (clean.includes('lthigh')) { return [-14, -34, 0, 0]; }
  if (clean.includes('lcalf')) { return [0, -38, 0, 0]; }
  if (clean.includes('lfoot')) { return [0, -28, 14, 0]; }
  if (clean.includes('ltoe')) { return [0, 0, 16, 0]; }
  if (clean.includes('rthigh')) { return [14, -34, 0, 0]; }
  if (clean.includes('rcalf')) { return [0, -38, 0, 0]; }
  if (clean.includes('rfoot')) { return [0, -28, 14, 0]; }
  if (clean.includes('rtoe')) { return [0, 0, 16, 0]; }
  if (/weapon|rifle|gun|pistol/i.test(clean)) { return [36, -6, 18, 0]; }
  return [0, Math.max(8, 18 - index), 0, 0];
}

function emptyClip(bytes: Uint8Array, name: string, errors: string[]): RdrAnimationClip {
  return {
    name,
    fileSize: bytes.length,
    version: 0,
    duration: 0,
    frameIntervals: 0,
    sampleCount: 0,
    boneCount: 0,
    minNorm: 0,
    maxNorm: 0,
    nameOffset: 0,
    animOffset: 0,
    bones: [],
    boneRecords: [],
    tracks: [],
    parents: [],
    counts: { '0x0C': 0, '0x1C': 0, '0x2C': 0, '0x3C': 0 },
    attachmentBones: [],
    parseOk: false,
    errors,
    animationLike: false,
  };
}

export function parseRdrAnimation(bytes: Uint8Array, name = 'file'): RdrAnimationClip {
  if (bytes.length < 0x20) {
    return emptyClip(bytes, name, [`too small for animation header (${bytes.length} bytes)`]);
  }

  const dv = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  const clip: RdrAnimationClip = {
    name,
    fileSize: bytes.length,
    version: u32(dv, 0x00),
    duration: f32(dv, 0x04),
    frameIntervals: u32(dv, 0x08),
    sampleCount: 0,
    boneCount: u32(dv, 0x0c),
    minNorm: f32(dv, 0x10),
    maxNorm: f32(dv, 0x14),
    nameOffset: u32(dv, 0x18),
    animOffset: u32(dv, 0x1c),
    bones: [],
    boneRecords: [],
    tracks: [],
    parents: [],
    counts: { '0x0C': 0, '0x1C': 0, '0x2C': 0, '0x3C': 0 },
    attachmentBones: [],
    parseOk: false,
    errors: [],
    animationLike: false,
  };
  clip.sampleCount = clip.frameIntervals + 1;

  if (clip.version !== 5) { clip.errors.push(`unexpected version/type ${clip.version}`); }
  if (!Number.isFinite(clip.duration) || clip.duration < 0 || clip.duration > 600) {
    clip.errors.push(`implausible duration ${clip.duration}`);
  }
  if (clip.frameIntervals > 10000) { clip.errors.push(`implausible frame interval count ${clip.frameIntervals}`); }
  if (clip.boneCount === 0 || clip.boneCount > 256) { clip.errors.push(`implausible bone count ${clip.boneCount}`); }
  if (clip.nameOffset < 0x20 || clip.nameOffset > bytes.length) { clip.errors.push('nameOffset outside file'); }
  if (clip.animOffset < clip.nameOffset || clip.animOffset > bytes.length) { clip.errors.push('animOffset outside file'); }

  const plausibleHeader =
    clip.version === 5 &&
    clip.boneCount > 0 &&
    clip.boneCount <= 256 &&
    clip.frameIntervals <= 10000 &&
    clip.nameOffset >= 0x20 &&
    clip.nameOffset <= bytes.length &&
    clip.animOffset >= clip.nameOffset &&
    clip.animOffset <= bytes.length;

  if (!plausibleHeader) {
    return clip;
  }

  for (let i = 0; i < clip.boneCount; i++) {
    const offset = 0x20 + i * 16;
    if (offset + 16 > bytes.length) {
      clip.errors.push(`bone record ${i} outside file`);
      break;
    }
    clip.boneRecords.push({
      offset,
      a: u32(dv, offset),
      b: u32(dv, offset + 4),
      c: u32(dv, offset + 8),
      d: u32(dv, offset + 12),
    });
  }

  clip.bones = readNames(dv, clip.nameOffset, clip.animOffset, clip.boneCount);

  let offset = clip.animOffset;
  for (let i = 0; i < clip.boneCount && offset + 4 <= bytes.length; i++) {
    const start = offset;
    const type = u32(dv, offset);
    const boneName = clip.bones[i] || `bone_${i}`;
    const track: RdrAnimationTrack = {
      boneIndex: i,
      boneName,
      offset: start,
      type,
      typeName: describeTrackType(type),
    };

    try {
      if (type === 0x3c) {
        ensure(dv, offset, 20, 'static transform track');
        track.baseVec4 = vecI16(dv, offset + 4, 4);
        track.staticQuat = vecI16(dv, offset + 12, 4);
        offset += 20;
      } else if (type === 0x2c) {
        ensure(dv, offset, 12, 'static base track');
        track.baseVec4 = vecI16(dv, offset + 4, 4);
        track.quatSamples = readQuatSamples(dv, offset + 12, clip.sampleCount);
        offset += 12 + clip.sampleCount * 8;
      } else if (type === 0x1c) {
        ensure(dv, offset, 4, 'animated base track');
        track.posSamples = readPosSamples(dv, offset + 4, clip.sampleCount);
        const qOffset = align4(offset + 4 + clip.sampleCount * 6);
        ensure(dv, qOffset, 8, 'static quaternion');
        track.staticQuat = vecI16(dv, qOffset, 4);
        offset = qOffset + 8;
      } else if (type === 0x0c) {
        ensure(dv, offset, 4, 'animated transform track');
        track.posSamples = readPosSamples(dv, offset + 4, clip.sampleCount);
        const qOffset = align4(offset + 4 + clip.sampleCount * 6);
        track.quatSamples = readQuatSamples(dv, qOffset, clip.sampleCount);
        offset = qOffset + clip.sampleCount * 8;
      } else {
        clip.errors.push(`unknown track marker ${hex(type)} at ${hex(start)} for bone ${i} ${boneName}`);
        break;
      }
    } catch (err) {
      clip.errors.push(`failed reading track at ${hex(start)}: ${err instanceof Error ? err.message : String(err)}`);
      break;
    }

    track.size = offset - start;
    track.end = offset;
    if (track.quatSamples?.length) { track.firstQuatNorm = quatNorm(track.quatSamples[0]); }
    if (track.staticQuat) { track.staticQuatNorm = quatNorm(track.staticQuat); }
    clip.tracks.push(track);
  }

  clip.parseEnd = offset;
  clip.parents = buildParentIndices(clip.bones);
  clip.counts = countTrackTypes(clip.tracks);
  clip.attachmentBones = findAttachmentBones(clip.bones);
  clip.parseOk = offset === bytes.length && clip.tracks.length === clip.boneCount && clip.errors.length === 0;
  clip.animationLike = clip.tracks.length > 0 && clip.tracks.length >= Math.min(3, clip.boneCount);
  return clip;
}

function vAdd(a: Vec3, b: Vec3): Vec3 {
  return [a[0] + b[0], a[1] + b[1], a[2] + b[2]];
}

function qNormalize(q: Vec4): Vec4 {
  const n = Math.hypot(q[0], q[1], q[2], q[3]) || 1;
  return [q[0] / n, q[1] / n, q[2] / n, q[3] / n];
}

function qMul(a: Vec4, b: Vec4): Vec4 {
  const ax = a[0], ay = a[1], az = a[2], aw = a[3];
  const bx = b[0], by = b[1], bz = b[2], bw = b[3];
  return [
    aw * bx + ax * bw + ay * bz - az * by,
    aw * by - ax * bz + ay * bw + az * bx,
    aw * bz + ax * by - ay * bx + az * bw,
    aw * bw - ax * bx - ay * by - az * bz,
  ];
}

function qRotate(q: Vec4, v: Vec3): Vec3 {
  const x = v[0], y = v[1], z = v[2];
  const qx = q[0], qy = q[1], qz = q[2], qw = q[3];
  const tx = 2 * (qy * z - qz * y);
  const ty = 2 * (qz * x - qx * z);
  const tz = 2 * (qx * y - qy * x);
  return [
    x + qw * tx + (qy * tz - qz * ty),
    y + qw * ty + (qz * tx - qx * tz),
    z + qw * tz + (qx * ty - qy * tx),
  ];
}

function quatFromS16(q: Vec4): Vec4 {
  return qNormalize([q[0] / 32767, q[1] / 32767, q[2] / 32767, q[3] / 32767]);
}

export function animationFrameRate(clip: RdrAnimationClip): number {
  if (clip.duration > 0 && clip.frameIntervals > 0) {
    const fps = clip.frameIntervals / clip.duration;
    if (Number.isFinite(fps) && fps > 0) { return fps; }
  }
  return 30;
}

export function trackLocalPos(track: RdrAnimationTrack | undefined, frameIndex: number): Vec3 {
  if (!track) { return [0, 0, 0]; }
  if (track.posSamples?.length) {
    const pos = Math.max(0, Math.min(frameIndex, track.posSamples.length - 1));
    return track.posSamples[pos];
  }
  if (track.baseVec4?.length) {
    const raw: Vec3 = [track.baseVec4[0], track.baseVec4[1], track.baseVec4[2]];
    if (Math.hypot(raw[0], raw[1], raw[2]) < 0.0001) {
      const fallback = fallbackBoneOffset(track.boneName, track.boneIndex);
      return [fallback[0], fallback[1], fallback[2]];
    }
    return raw;
  }
  const fallback = fallbackBoneOffset(track.boneName, track.boneIndex);
  return [fallback[0], fallback[1], fallback[2]];
}

export function trackLocalQuat(track: RdrAnimationTrack | undefined, frameIndex: number): Vec4 {
  if (!track) { return [0, 0, 0, 1]; }
  if (track.quatSamples?.length) {
    const pos = Math.max(0, Math.min(frameIndex, track.quatSamples.length - 1));
    return quatFromS16(track.quatSamples[pos]);
  }
  if (track.staticQuat?.length === 4) {
    return quatFromS16(track.staticQuat);
  }
  return [0, 0, 0, 1];
}

export function buildAnimationPose(clip: RdrAnimationClip, frameIndex: number): RdrAnimationPose {
  const count = clip.boneCount || 0;
  const worldPos = new Array<Vec3>(count);
  const worldRot = new Array<Vec4>(count);
  for (let i = 0; i < count; i++) {
    const track = clip.tracks[i];
    const parent = clip.parents[i] ?? -1;
    const localPos = trackLocalPos(track, frameIndex);
    const localQuat = trackLocalQuat(track, frameIndex);
    if (parent < 0 || !worldPos[parent]) {
      worldPos[i] = localPos;
      worldRot[i] = localQuat;
    } else {
      worldPos[i] = vAdd(worldPos[parent], qRotate(worldRot[parent], localPos));
      worldRot[i] = qNormalize(qMul(worldRot[parent], localQuat));
    }
  }
  return { worldPos, worldRot };
}

export function computeAnimationBounds(clip: RdrAnimationClip): RdrAnimationBounds {
  const min: Vec3 = [Infinity, Infinity, Infinity];
  const max: Vec3 = [-Infinity, -Infinity, -Infinity];
  const samples = Math.max(1, Math.min(clip.sampleCount || 1, 60));
  for (let i = 0; i < samples; i++) {
    const frameIndex = Math.floor(i / Math.max(1, samples - 1) * Math.max(0, (clip.sampleCount || 1) - 1));
    const pose = buildAnimationPose(clip, frameIndex);
    for (const p of pose.worldPos) {
      if (!p) { continue; }
      for (let axis = 0; axis < 3; axis++) {
        if (p[axis] < min[axis]) { min[axis] = p[axis]; }
        if (p[axis] > max[axis]) { max[axis] = p[axis]; }
      }
    }
  }
  if (!Number.isFinite(min[0])) {
    return { center: [0, 0, 0], size: [1, 1, 1], maxDim: 1 };
  }
  const center: Vec3 = [
    (min[0] + max[0]) / 2,
    (min[1] + max[1]) / 2,
    (min[2] + max[2]) / 2,
  ];
  const size: Vec3 = [max[0] - min[0], max[1] - min[1], max[2] - min[2]];
  return { min, max, center, size, maxDim: Math.max(size[0], size[1], size[2], 1) };
}
