// Decoder for Zipper PS2 ".tif" texture records (texdat) and PAL palettes
// packed inside ZED containers. Pixel data is delivered as RGBA8888, already
// flipped to top-down orientation for browser display.
//
// texdat layout (verified by the SOCOM II pack builders in Mods/SOCOMCA,
// whose output renders in game):
//   +0x00 u16 width          +0x02 u16 height
//   +0x04 u32 pixel bytes    +0x08 u32 texture index (TBP0 slot, NOT a palette id)
//   +0x0C u8  bits per pixel +0x0D 0x06  +0x0E 0
//   +0x0F u8  0x10 direct colour / 0x12 16-bit palette / 0x13 32-bit palette
//   +0x20     pixels (pixel bytes)
//   then      a GIF packet: GIF tag, then A+D register pairs (ALPHA, TEX1,
//             TEX0, TEST, CLAMP ...). TEX0 carries the pixel format (PSM),
//             the log2 size, and CBP = the palette id: `texpal_<CBP>` in the
//             PAL archive. The game binds palettes by that id, so it is the
//             exact palette for the texture; the header-id guesses below are
//             only fallbacks for records whose tail cannot be read.

export interface ZedGsRegisters {
  tex0Lo: number;
  tex0Hi: number;
  // GS pixel storage mode: 0x00 CT32, 0x01 CT24, 0x02 CT16, 0x0A CT16S,
  // 0x13 T8, 0x14 T4 (+ the H variants).
  psm: number;
  // Palette (CLUT) base pointer = the texpal id the texture binds to.
  cbp: number;
  // CLUT pixel format: 0 = 32-bit RGBA, 2 = 16-bit A1B5G5R5.
  cpsm: number;
  // CLUT storage: 0 = CSM1 (swizzled 8-bit layout), 1 = CSM2 (linear).
  csm: number;
  log2Width: number;
  log2Height: number;
  // Texture function: 0 modulate, 1 decal, 2 highlight, 3 highlight2.
  tfx: number;
  // From CLAMP_1: wrap mode per axis.
  wrapS: 'repeat' | 'clamp';
  wrapT: 'repeat' | 'clamp';
  // From TEST_1: alpha test threshold in PNG alpha space (0..1), or null
  // when the alpha test is off / not a "keep opaque texels" comparison.
  alphaTest: number | null;
  // From ALPHA_1: true when the blend equation is not a plain source copy.
  alphaBlend: boolean;
  // Which registers were found in the tail (for diagnostics).
  registers: number[];
}

export interface ZedTextureHeader {
  width: number;
  height: number;
  // Historical names kept for callers: these are the u16 halves of the u32
  // texture index at +0x08. They are NOT palette ids, but some archives
  // happen to number texpal entries the same way so they remain a fallback.
  paletteId: number;
  paletteAlt: number;
  formatA: number;
  formatB: number;
  bpp: number;
  isIndexed: boolean;
  // Declared pixel byte count (+0x04) when it is sane, else derived.
  pixelBytes: number;
  // GS PSM of the pixel data (see ZedGsRegisters.psm).
  psm: number;
  // Exact palette id from TEX0.CBP, or null when no TEX0 register was found.
  cbp: number | null;
  gs: ZedGsRegisters | null;
}

export interface ZedTexture {
  header: ZedTextureHeader;
  rgba: Uint8Array;
  paletteResolved: boolean;
  // True when any decoded pixel is not fully opaque.
  hasTransparency: boolean;
  // Fraction of texels with alpha below 50% (0..1).
  transparentRatio: number;
}

export interface ZedPalette {
  // Real PAL name id, for example texpal_563 -> 563.
  paletteId: number;
  // 256 RGBA8888 entries in logical (unswizzled) index order.
  rgba: Uint8Array;
  // True when the source CLUT was 16-bit A1B5G5R5 (512 bytes).
  is16Bit: boolean;
}

const TEXTURE_HEADER_SIZE = 0x20;
const TEXTURE_FOOTER_SIZE = 0x80;

// GS register addresses used in the texdat tail.
const GS_REG_TEX0_1 = 0x06;
const GS_REG_CLAMP_1 = 0x08;
const GS_REG_TEX1_1 = 0x14;
const GS_REG_ALPHA_1 = 0x42;
const GS_REG_TEST_1 = 0x47;

const PSM_CT32 = 0x00;
const PSM_CT24 = 0x01;
const PSM_CT16 = 0x02;
const PSM_CT16S = 0x0a;
const PSM_T8 = 0x13;
const PSM_T4 = 0x14;
const PSM_T8H = 0x1b;
const PSM_T4HL = 0x24;
const PSM_T4HH = 0x2c;

export function base64ToBytes(value: string): Uint8Array {
  const binary = atob(value);
  const out = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) {
    out[i] = binary.charCodeAt(i);
  }
  return out;
}

function bppForPsm(psm: number): number | null {
  switch (psm) {
    case PSM_CT32: return 32;
    case PSM_CT24: return 24;
    case PSM_CT16:
    case PSM_CT16S: return 16;
    case PSM_T8:
    case PSM_T8H: return 8;
    case PSM_T4:
    case PSM_T4HL:
    case PSM_T4HH: return 4;
    default: return null;
  }
}

function psmIsIndexed(psm: number): boolean {
  return psm === PSM_T8 || psm === PSM_T8H || psm === PSM_T4 || psm === PSM_T4HL || psm === PSM_T4HH;
}

// Scan the GIF A+D tail that follows the pixel data for the GS registers the
// game uploads with the texture. Returns null when no TEX0 is found.
export function parseGsRegisters(bytes: Uint8Array, tailStart: number): ZedGsRegisters | null {
  if (tailStart < 0 || tailStart + 16 > bytes.length) { return null; }
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  const regs = new Map<number, { lo: number; hi: number }>();
  // 0x80 bytes of tail = 8 qwords; allow a little slack for records that
  // carry extra registers.
  const limit = Math.min(bytes.length - 16, tailStart + 16 * 16);
  for (let o = tailStart; o <= limit; o += 16) {
    const regLo = view.getUint32(o + 8, true);
    const regHi = view.getUint32(o + 12, true);
    if (regHi !== 0 || regLo > 0x7f) { continue; }
    if (!regs.has(regLo)) {
      regs.set(regLo, { lo: view.getUint32(o, true), hi: view.getUint32(o + 4, true) });
    }
  }
  const tex0 = regs.get(GS_REG_TEX0_1);
  if (!tex0) { return null; }

  const psm = (tex0.lo >>> 20) & 0x3f;
  const log2Width = (tex0.lo >>> 26) & 0xf;
  const log2Height = ((tex0.lo >>> 30) & 0x3) | ((tex0.hi & 0x3) << 2);
  const tfx = (tex0.hi >>> 3) & 0x3;
  const cbp = (tex0.hi >>> 5) & 0x3fff;
  const cpsm = (tex0.hi >>> 19) & 0xf;
  const csm = (tex0.hi >>> 23) & 0x1;

  // Sanity: a real TEX0 has a known PSM and a power-of-two size <= 1024.
  if (bppForPsm(psm) === null || log2Width > 10 || log2Height > 10) { return null; }

  let wrapS: 'repeat' | 'clamp' = 'repeat';
  let wrapT: 'repeat' | 'clamp' = 'repeat';
  const clamp = regs.get(GS_REG_CLAMP_1);
  if (clamp) {
    // WMS bits 0-1, WMT bits 2-3: 0 REPEAT, 1 CLAMP, 2 REGION_CLAMP, 3 REGION_REPEAT.
    const wms = clamp.lo & 0x3;
    const wmt = (clamp.lo >>> 2) & 0x3;
    wrapS = wms === 1 || wms === 2 ? 'clamp' : 'repeat';
    wrapT = wmt === 1 || wmt === 2 ? 'clamp' : 'repeat';
  }

  let alphaTest: number | null = null;
  const test = regs.get(GS_REG_TEST_1);
  if (test && (test.lo & 0x1) !== 0) {
    // ATST bits 1-3: 0 NEVER, 1 ALWAYS, 2 LESS, 3 LEQUAL, 4 EQUAL, 5 GEQUAL,
    // 6 GREATER, 7 NOTEQUAL. AREF bits 4-11 in GS alpha (0x80 = 1.0).
    const atst = (test.lo >>> 1) & 0x7;
    const aref = (test.lo >>> 4) & 0xff;
    if (atst === 5 || atst === 6) {
      alphaTest = Math.min(1, ps2AlphaToPngAlpha(aref) / 255);
    } else if (atst === 7 && aref === 0) {
      alphaTest = 1 / 255;
    }
  }

  let alphaBlend = false;
  const alpha = regs.get(GS_REG_ALPHA_1);
  if (alpha) {
    // (A - B) * C + D: the opaque "copy source" setting is A=0 B=2 C=? D=2
    // or A=0 B=0 ... Treat anything that reads the destination colour as a
    // blend.
    const a = alpha.lo & 0x3;
    const b = (alpha.lo >>> 2) & 0x3;
    const d = (alpha.lo >>> 6) & 0x3;
    alphaBlend = b === 1 || d === 1 || (a === 1 && d !== 1);
  }

  return {
    tex0Lo: tex0.lo,
    tex0Hi: tex0.hi,
    psm,
    cbp,
    cpsm,
    csm,
    log2Width,
    log2Height,
    tfx,
    wrapS,
    wrapT,
    alphaTest,
    alphaBlend,
    registers: [...regs.keys()],
  };
}

export function parseTextureHeader(bytes: Uint8Array): ZedTextureHeader {
  if (bytes.length < TEXTURE_HEADER_SIZE) {
    throw new Error(`texture too small (${bytes.length} bytes)`);
  }

  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  const width = view.getUint16(0x00, true);
  const height = view.getUint16(0x02, true);
  const declaredPixelBytes = view.getUint32(0x04, true);
  const paletteId = view.getUint16(0x08, true);
  const paletteAlt = view.getUint16(0x0a, true);
  const formatA = view.getUint16(0x0c, true);
  const formatB = view.getUint16(0x0e, true);
  const headerBpp = bytes[0x0c];

  const pixelCount = width * height;
  const available = bytes.length - TEXTURE_HEADER_SIZE;
  const declaredSane =
    declaredPixelBytes > 0 &&
    declaredPixelBytes <= available &&
    pixelCount > 0 &&
    [4, 8, 16, 24, 32].some(b => (pixelCount * b) / 8 === declaredPixelBytes);

  // Prefer the GS registers in the tail: they say exactly how the game
  // interprets the pixels.
  let gs: ZedGsRegisters | null = null;
  if (declaredSane) {
    gs = parseGsRegisters(bytes, TEXTURE_HEADER_SIZE + declaredPixelBytes);
  }
  if (!gs) {
    // Records without a sane length word: try the conventional 0x80 footer
    // at the end of the record, then each plausible pixel size.
    const candidates = new Set<number>();
    if (bytes.length >= TEXTURE_HEADER_SIZE + TEXTURE_FOOTER_SIZE) {
      candidates.add(bytes.length - TEXTURE_FOOTER_SIZE);
    }
    for (const b of [8, 32, 16, 4, 24]) {
      const n = (pixelCount * b) / 8;
      if (Number.isInteger(n) && n > 0 && n <= available) { candidates.add(TEXTURE_HEADER_SIZE + n); }
    }
    for (const start of candidates) {
      gs = parseGsRegisters(bytes, start);
      if (gs) { break; }
    }
  }

  let bpp: number;
  let isIndexed: boolean;
  let psm: number;
  if (gs) {
    psm = gs.psm;
    bpp = bppForPsm(psm) ?? 8;
    isIndexed = psmIsIndexed(psm);
    // CT24 pixels are stored either packed (3 bytes) or as 32-bit words;
    // the declared length tells which.
    if (psm === PSM_CT24 && declaredSane && declaredPixelBytes === pixelCount * 4) { bpp = 32; }
  } else {
    // Legacy heuristics: header byte +0x0C is the bpp, else compare sizes.
    const indexedSize = pixelCount + TEXTURE_HEADER_SIZE + TEXTURE_FOOTER_SIZE;
    const rgbaSize = pixelCount * 4 + TEXTURE_HEADER_SIZE + TEXTURE_FOOTER_SIZE;
    if (declaredSane && [4, 8, 16, 24, 32].includes(headerBpp) && (pixelCount * headerBpp) / 8 === declaredPixelBytes) {
      bpp = headerBpp;
    } else if (formatA === 0x0620 || bytes.length === rgbaSize) {
      bpp = 32;
    } else if (formatA === 0x0608 || bytes.length === indexedSize) {
      bpp = 8;
    } else if (formatA === 0x0610) {
      bpp = 16;
    } else if (formatA === 0x0604) {
      bpp = 4;
    } else if (Math.abs(bytes.length - rgbaSize) < Math.abs(bytes.length - indexedSize)) {
      bpp = 32;
    } else {
      bpp = 8;
    }
    isIndexed = bpp === 8 || bpp === 4;
    psm = bpp === 32 ? PSM_CT32 : bpp === 24 ? PSM_CT24 : bpp === 16 ? PSM_CT16 : bpp === 4 ? PSM_T4 : PSM_T8;
  }

  const pixelBytes = declaredSane ? declaredPixelBytes : Math.min(available, Math.ceil((pixelCount * bpp) / 8));

  return {
    width,
    height,
    paletteId,
    paletteAlt,
    formatA,
    formatB,
    bpp,
    isIndexed,
    pixelBytes,
    psm,
    cbp: gs ? gs.cbp : null,
    gs,
  };
}

function flipRowsRgba(src: Uint8Array, width: number, height: number): Uint8Array {
  const stride = width * 4;
  const out = new Uint8Array(stride * height);
  for (let y = 0; y < height; y++) {
    const srcRow = (height - 1 - y) * stride;
    out.set(src.subarray(srcRow, srcRow + stride), y * stride);
  }
  return out;
}

function expand5To8(value: number): number {
  return ((value << 3) | (value >> 2)) & 0xff;
}

function ps2AlphaToPngAlpha(value: number): number {
  // PS2 GS alpha is commonly 0..128 rather than 0..255.
  return value <= 128 ? Math.min(255, value * 2) : value;
}

// Decode a 32-bit RGBA texture. PS2 GS alpha often uses 0..128; expand to PNG.
function decodeRgba32(bytes: Uint8Array, header: ZedTextureHeader): Uint8Array {
  const { width, height } = header;
  const pixelBytes = width * height * 4;
  const src = bytes.subarray(TEXTURE_HEADER_SIZE, TEXTURE_HEADER_SIZE + pixelBytes);
  const out = new Uint8Array(pixelBytes);
  for (let i = 0; i + 3 < src.length; i += 4) {
    out[i + 0] = src[i + 0];
    out[i + 1] = src[i + 1];
    out[i + 2] = src[i + 2];
    out[i + 3] = ps2AlphaToPngAlpha(src[i + 3]);
  }
  return out;
}

// 24-bit colour: packed 3 bytes per pixel, or 32-bit words whose top byte is
// unused (PSMCT24 in GS memory). Always opaque.
function decodeRgb24(bytes: Uint8Array, header: ZedTextureHeader): Uint8Array {
  const { width, height } = header;
  const pixels = width * height;
  const stride = header.bpp === 32 ? 4 : 3;
  const src = bytes.subarray(TEXTURE_HEADER_SIZE, TEXTURE_HEADER_SIZE + pixels * stride);
  const out = new Uint8Array(pixels * 4);
  for (let i = 0; i < pixels; i++) {
    const s = i * stride;
    if (s + 2 >= src.length) { break; }
    out[i * 4 + 0] = src[s + 0];
    out[i * 4 + 1] = src[s + 1];
    out[i * 4 + 2] = src[s + 2];
    out[i * 4 + 3] = 255;
  }
  return out;
}

// A1B5G5R5 (PSMCT16): R in the low 5 bits, then G, B, and the STP/alpha bit.
// Bit 15 maps through TEXA in the GS; SOCOM sets it on opaque texels when the
// texture has any transparency at all, and leaves it clear everywhere on
// textures that are fully opaque. So: if no texel has the bit set, the whole
// image is opaque; otherwise a clear bit means transparent.
function decodeRgb16(bytes: Uint8Array, header: ZedTextureHeader): Uint8Array {
  const { width, height } = header;
  const pixels = width * height;
  const view = new DataView(bytes.buffer, bytes.byteOffset + TEXTURE_HEADER_SIZE, Math.min(pixels * 2, bytes.length - TEXTURE_HEADER_SIZE));
  const count = Math.floor(view.byteLength / 2);
  let anyStp = false;
  for (let i = 0; i < count; i++) {
    if ((view.getUint16(i * 2, true) & 0x8000) !== 0) { anyStp = true; break; }
  }
  const out = new Uint8Array(pixels * 4);
  for (let i = 0; i < count; i++) {
    const v = view.getUint16(i * 2, true);
    out[i * 4 + 0] = expand5To8(v & 0x1f);
    out[i * 4 + 1] = expand5To8((v >> 5) & 0x1f);
    out[i * 4 + 2] = expand5To8((v >> 10) & 0x1f);
    out[i * 4 + 3] = !anyStp || (v & 0x8000) !== 0 ? 255 : 0;
  }
  return out;
}

function rgba5551ToRgba8888(clut: Uint8Array): Uint8Array {
  const out = new Uint8Array(256 * 4);
  const view = new DataView(clut.buffer, clut.byteOffset, clut.byteLength);
  let anyStp = false;
  for (let i = 0; i < 256; i++) {
    if ((view.getUint16(i * 2, true) & 0x8000) !== 0) { anyStp = true; break; }
  }
  for (let i = 0; i < 256; i++) {
    const v = view.getUint16(i * 2, true);
    const r = expand5To8(v & 0x1f);
    const g = expand5To8((v >> 5) & 0x1f);
    const b = expand5To8((v >> 10) & 0x1f);
    const hasStp = (v & 0x8000) !== 0;
    out[i * 4 + 0] = r;
    out[i * 4 + 1] = g;
    out[i * 4 + 2] = b;
    // Bit 15 goes through TEXA/AEM in the GS. Palettes with no STP bit at
    // all are opaque apart from the all-black (AEM) entry; palettes that do
    // use the bit keep coloured entries visible and only drop black
    // entries without it, which matches how the game's cutouts look.
    if (!anyStp) {
      out[i * 4 + 3] = (r !== 0 || g !== 0 || b !== 0) ? 255 : 0;
    } else {
      out[i * 4 + 3] = (r !== 0 || g !== 0 || b !== 0 || hasStp) ? 255 : 0;
    }
  }
  return out;
}

// 32-bit CLUT entries keep their real alpha (GS 0..0x80): foliage, grass and
// tree facades cut their silhouettes from it through the texture's alpha
// test or blend. Whether alpha is used at all is the renderer's call, from
// the texture's GS registers.
function rgba32PaletteToRgba8888(clut: Uint8Array): Uint8Array {
  const out = new Uint8Array(256 * 4);
  for (let i = 0; i < 256; i++) {
    const p = i * 4;
    out[p + 0] = clut[p + 0];
    out[p + 1] = clut[p + 1];
    out[p + 2] = clut[p + 2];
    out[p + 3] = ps2AlphaToPngAlpha(clut[p + 3]);
  }
  return out;
}

// GS CSM1 storage keeps 256-entry CLUTs with entries 8-15 and 16-23 swapped
// in every block of 32 (bits 3 and 4 of the index exchanged). Undo it at the
// decoded RGBA-entry level so it works for both 16-bit and 32-bit CLUTs.
function unswizzlePs2Clut8(rgba: Uint8Array): Uint8Array {
  const out = new Uint8Array(256 * 4);
  for (let i = 0; i < 256; i++) {
    const j = (i & 0xe7) | ((i & 0x08) << 1) | ((i & 0x10) >> 1);
    out.set(rgba.subarray(j * 4, j * 4 + 4), i * 4);
  }
  return out;
}

// Decode a raw CLUT buffer into RGBA8888 with the PS2 8-bit CLUT unswizzle.
// Buffers are either 512 bytes (256 x RGBA5551) or 1024 bytes (256 x RGBA8888).
function decodeClutBuffer(buf: Uint8Array): { rgba: Uint8Array; is16Bit: boolean } {
  if (buf.length >= 1024) {
    return { rgba: unswizzlePs2Clut8(rgba32PaletteToRgba8888(buf.subarray(0, 1024))), is16Bit: false };
  }
  if (buf.length >= 512) {
    return { rgba: unswizzlePs2Clut8(rgba5551ToRgba8888(buf.subarray(0, 512))), is16Bit: true };
  }
  throw new Error(`CLUT too small (${buf.length} bytes)`);
}

// Real PAL.ZED files store palettes as triplets of entries:
//   texpal_NNN (size 0, name marker)
//   par        (size 8: palette id, then 0x00020200 = 16-bit / 0x400 = 32-bit)
//   buf        (size 512 or 1024, the CLUT data)
// Walk the entry list and group them.
export function parsePaletteNodes(
  nodes: Array<{ index: number; name: string; data: string }>,
): Map<string, ZedPalette> {
  return parsePaletteEntries(
    nodes.map(n => ({ name: n.name })),
    i => base64ToBytes(nodes[i].data),
  );
}

// Variant that consumes entries already extracted in memory. Used when we've
// drilled into a nested PAL archive via the client-side parser.
export function parsePaletteEntries(
  entries: Array<{ name: string }>,
  getBytes: (i: number) => Uint8Array,
): Map<string, ZedPalette> {
  const result = new Map<string, ZedPalette>();
  let sequential = 1;

  for (let i = 0; i < entries.length; i++) {
    const node = entries[i];
    const m = /^texpal_(\d+)$/i.exec(node.name);
    if (!m) { continue; }

    const paletteId = Number(m[1]);
    let bufIndex = -1;
    let parIndex = -1;
    for (let j = i + 1; j < Math.min(i + 4, entries.length); j++) {
      if (/^buf$/i.test(entries[j].name)) { bufIndex = j; break; }
      if (/^par$/i.test(entries[j].name)) { parIndex = j; continue; }
      if (/^texpal_/i.test(entries[j].name)) { break; }
    }
    if (bufIndex < 0) { continue; }

    try {
      const buf = getBytes(bufIndex);
      if (buf.length < 512) { continue; }
      const decoded = decodeClutBuffer(buf);
      const pal: ZedPalette = { paletteId, rgba: decoded.rgba, is16Bit: decoded.is16Bit };
      result.set(node.name.toLowerCase(), pal);
      result.set(String(paletteId), pal);

      // The game registers the palette under the id in `par` word 0. It
      // normally equals the texpal_NNN name, but trust the record when they
      // differ: TEX0.CBP lookups go through this id.
      if (parIndex >= 0) {
        const par = getBytes(parIndex);
        if (par.length >= 4) {
          const parId = (par[0] | (par[1] << 8) | (par[2] << 16) | (par[3] << 24)) >>> 0;
          if (parId !== paletteId && parId < 0x4000 && !result.has(`texpal_${parId}`)) {
            result.set(`texpal_${parId}`, pal);
            result.set(String(parId), pal);
          }
        }
      }

      // Useful fallback for simple files where the texture header's palette slot
      // is just 1,2,3... instead of the real texpal_NNN name.
      if (!result.has(`texpal_${sequential}`)) {
        result.set(`texpal_${sequential}`, pal);
      }
      sequential++;
    } catch { /* skip invalid palette */ }
  }

  return result;
}

// Adds exact aliases from TXR texture-slot ids to PAL entries by walking the TXR
// entries and assigning palettes only to indexed texdat records. This fixes
// files like UIBF where texture ids are 1..24 but PAL names are 563..584, and
// where RGBA32 texture records consume a texture id but no palette.
export function aliasPalettesForTextureEntries(
  palettes: Map<string, ZedPalette>,
  entries: Array<{ name: string }>,
  getBytes: (i: number) => Uint8Array,
): Map<string, ZedPalette> {
  if (palettes.size === 0) { return palettes; }

  const out = new Map<string, ZedPalette>(palettes);
  const paletteList: ZedPalette[] = [];
  const seen = new Set<number>();

  for (const [key, value] of palettes) {
    if (!/^texpal_\d+$/i.test(key)) { continue; }
    if (seen.has(value.paletteId)) { continue; }
    seen.add(value.paletteId);
    paletteList.push(value);
  }

  let paletteCursor = 0;
  for (let i = 0; i < entries.length; i++) {
    if (!/\.tif$/i.test(entries[i].name)) { continue; }

    let dataIndex = -1;
    for (let j = i + 1; j < Math.min(i + 4, entries.length); j++) {
      if (/^texdat$/i.test(entries[j].name)) { dataIndex = j; break; }
      if (/\.tif$/i.test(entries[j].name)) { break; }
    }
    if (dataIndex < 0) { continue; }

    try {
      const header = parseTextureHeader(getBytes(dataIndex));
      if (!header.isIndexed) { continue; }
      // When TEX0 names the palette, alias the .tif straight to it. That is
      // the binding the game uses, so it beats the order-based pairing.
      if (header.cbp !== null) {
        const exact = palettes.get(`texpal_${header.cbp}`);
        if (exact) {
          out.set(entries[i].name.toLowerCase(), exact);
          paletteCursor++;
          continue;
        }
      }
      const pal = paletteList[paletteCursor++];
      if (!pal) { break; }
      // Keep only the tif-name alias. Overwriting texpal_${header.paletteId}
      // with the order-based palette breaks header-palId lookups for other
      // textures and was the cause of "wrong colours per weapon".
      out.set(entries[i].name.toLowerCase(), pal);
    } catch { /* skip bad texture record */ }
  }

  return out;
}

function hasTransparentPixels(rgba: Uint8Array): boolean {
  for (let i = 3; i < rgba.length; i += 4) {
    if (rgba[i] < 255) { return true; }
  }
  return false;
}

function transparentRatioOf(rgba: Uint8Array): number {
  const count = rgba.length / 4;
  if (count === 0) { return 0; }
  let clear = 0;
  for (let i = 3; i < rgba.length; i += 4) {
    if (rgba[i] < 128) { clear++; }
  }
  return clear / count;
}

// Resolve the palette for an indexed texture. Order:
//   1. an explicit palette alias (texpal_N / ns:texpal_N: a user override or
//      a hand-tuned rule) when the caller passed one
//   2. TEX0.CBP inside the texture's own namespace (exact, game binding)
//   3. a .tif alias (the TXR/PAL order pairing from the same archive pair)
//   4. TEX0.CBP globally
//   5. the legacy header-id guesses
export function resolvePaletteForTexture(
  header: ZedTextureHeader,
  palettes: Map<string, ZedPalette> | null,
  paletteAlias?: string | null,
): ZedPalette | undefined {
  if (!palettes) { return undefined; }
  const namespace = paletteAlias?.includes(':')
    ? paletteAlias.split(':', 1)[0].toLowerCase()
    : null;
  const aliasIsPaletteKey = !!paletteAlias && /(^|:)(texpal_)?\d+$/i.test(paletteAlias);
  if (aliasIsPaletteKey && paletteAlias) {
    const forced = palettes.get(paletteAlias.toLowerCase());
    if (forced) { return forced; }
  }
  const byPaletteId = (id: number, exact: boolean): ZedPalette | undefined => {
    if (!Number.isFinite(id) || id < 0) { return undefined; }
    const keys = namespace
      ? [`${namespace}:texpal_${id}`, `${namespace}:${id}`, `texpal_${id}`, String(id)]
      : [`texpal_${id}`, String(id)];
    for (const key of keys) {
      const pal = palettes.get(key.toLowerCase());
      if (pal) { return pal; }
    }
    if (exact) { return undefined; }
    for (const candidate of palettes.values()) {
      if (candidate.paletteId === id) { return candidate; }
    }
    return undefined;
  };

  const hasCbp = header.cbp !== null && header.cbp > 0;
  if (hasCbp && namespace) {
    const exact = palettes.get(`${namespace}:texpal_${header.cbp}`) ?? palettes.get(`${namespace}:${header.cbp}`);
    if (exact) { return exact; }
  }
  // Alias lookup. `aliasPalettesForTextureEntries` wires each .tif name
  // (e.g. "door_wood.tif") to the palette that came from the SAME TXR/PAL
  // pair the texture lives in, which matters once palettes from multiple
  // prefixes (CLIB / FLIB / M51 / WEAP) get merged into one map. Palette
  // ids are global in the game, so a CBP found in another namespace is
  // only trusted after that pairing.
  let palette: ZedPalette | undefined;
  if (paletteAlias) {
    palette = palettes.get(paletteAlias.toLowerCase());
  }
  if (palette) { return palette; }
  if (hasCbp) {
    const exact = byPaletteId(header.cbp as number, true);
    if (exact) { return exact; }
  }
  const candidates: number[] = [];
  const addCandidate = (value: number) => {
    if (!Number.isFinite(value) || candidates.includes(value)) { return; }
    candidates.push(value);
  };
  addCandidate(header.paletteId);
  addCandidate(header.paletteId + 102);
  addCandidate(header.paletteAlt);
  addCandidate(header.paletteAlt + 102);
  for (const id of candidates) {
    palette = byPaletteId(id, false);
    if (palette) { break; }
  }
  return palette;
}

// Decode a full texture, resolving the palette from a parsed palette map if
// needed. Returns rgba pixels in top-down orientation.
export function parseTexture(
  bytes: Uint8Array,
  palettes: Map<string, ZedPalette> | null,
  paletteAlias?: string | null,
): ZedTexture {
  const header = parseTextureHeader(bytes);
  const { width, height, psm } = header;
  const pixelCount = width * height;

  if (!header.isIndexed) {
    let flat: Uint8Array;
    if (psm === PSM_CT16 || psm === PSM_CT16S || header.bpp === 16) {
      flat = decodeRgb16(bytes, header);
    } else if (psm === PSM_CT24 || header.bpp === 24) {
      flat = decodeRgb24(bytes, header);
    } else {
      flat = decodeRgba32(bytes, header);
    }
    return {
      header,
      rgba: flipRowsRgba(flat, width, height),
      paletteResolved: true,
      hasTransparency: hasTransparentPixels(flat),
      transparentRatio: transparentRatioOf(flat),
    };
  }

  const palette = resolvePaletteForTexture(header, palettes, paletteAlias);
  const flat = new Uint8Array(pixelCount * 4);
  const is4Bit = header.bpp === 4;
  const indexBytes = is4Bit ? Math.ceil(pixelCount / 2) : pixelCount;
  const indices = bytes.subarray(TEXTURE_HEADER_SIZE, TEXTURE_HEADER_SIZE + indexBytes);

  if (palette) {
    // 4-bit textures use the first 16 CLUT entries in storage order. The
    // decoded palette is in logical order, so map 8..15 back to 16..23.
    const entryFor = (idx: number): number => (is4Bit && idx >= 8 ? idx + 8 : idx) * 4;
    if (is4Bit) {
      for (let i = 0; i < pixelCount; i++) {
        const b = indices[i >> 1];
        const idx = (i & 1) === 0 ? (b & 0x0f) : (b >> 4);
        const p = entryFor(idx);
        flat[i * 4 + 0] = palette.rgba[p + 0];
        flat[i * 4 + 1] = palette.rgba[p + 1];
        flat[i * 4 + 2] = palette.rgba[p + 2];
        flat[i * 4 + 3] = palette.rgba[p + 3];
      }
    } else {
      for (let i = 0; i < indices.length; i++) {
        const idx = indices[i] * 4;
        flat[i * 4 + 0] = palette.rgba[idx + 0];
        flat[i * 4 + 1] = palette.rgba[idx + 1];
        flat[i * 4 + 2] = palette.rgba[idx + 2];
        flat[i * 4 + 3] = palette.rgba[idx + 3];
      }
    }
  } else {
    // No palette: render indices as grayscale so the user still sees structure.
    for (let i = 0; i < pixelCount; i++) {
      const v = is4Bit
        ? (((i & 1) === 0 ? (indices[i >> 1] & 0x0f) : (indices[i >> 1] >> 4)) * 17)
        : indices[i];
      flat[i * 4 + 0] = v;
      flat[i * 4 + 1] = v;
      flat[i * 4 + 2] = v;
      flat[i * 4 + 3] = 255;
    }
  }

  return {
    header,
    rgba: flipRowsRgba(flat, width, height),
    paletteResolved: !!palette,
    hasTransparency: !!palette && hasTransparentPixels(flat),
    transparentRatio: palette ? transparentRatioOf(flat) : 0,
  };
}
