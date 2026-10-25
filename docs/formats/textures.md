# Textures and palettes (`*_TXR.ZED`, `*_PAL.ZED`)

Textures and their palettes live in a namespace's TXR/PAL pair. Code: `Texture/ZedTextureFile.cs` (decode / encode), `Package/PackageTextures.cs` (new textures), `Package/PackageImport.cs` (copying between packages).

## TXR archive

```
renderphase            4 bytes
textures               children = texture count
  stone01.tif          children 1
    texdat             the texture (below)
  stone01_mip1.tif
    texdat
```

Mip levels are separate textures. A texture finds its mip levels through its MIPTBP registers, not through their names.

## texdat

| Offset | Size | Field |
|---|---|---|
| 0x00 | u16 | width |
| 0x02 | u16 | height |
| 0x04 | u32 | pixel byte count |
| 0x08 | u32 | **texture index**: 1..N within its TXR, equal to TEX0.TBP0 (true for every retail texture checked) |
| 0x0C | u8 | bits per pixel |
| 0x0D | u8 | varies (`07`, `06`); kept from the source |
| 0x0F | u8 | palette type: `0x12` = 16-bit palette, `0x13` = 32-bit palette (`0x10` on direct-colour textures) |
| 0x10–0x1F | | kept verbatim |
| 0x20 | pixel bytes | pixels, rows **bottom-up** |
| after the pixels | | GS register list (below) |

Pixel formats (GS PSM): `0x13` PSMT8 (8-bit indexed, the usual one), `0x14` PSMT4, `0x02` PSMCT16 (A1B5G5R5, no palette), `0x00` PSMCT32, `0x01` PSMCT24.

- **Alpha.** 32-bit pixels and palette entries store alpha as 0..0x80, where 0x80 is opaque. Decode doubles values up to 0x80.
- **16-bit pixels.** Bit 15 (STP) marks opaque texels. A texture that never sets it is fully opaque.

### GS register list

The tail is a GIF A+D list: 16-byte entries of a 64-bit value followed by a 64-bit register number. The registers seen in retail files are `0x0E` (the GIF tag), `0x42` ALPHA_1, `0x14` TEX1_1, `0x06` TEX0_1, `0x47` TEST_1, `0x08` CLAMP_1 and `0x34` MIPTBP1_1.

TEX0 (64-bit) fields:

| Bits | Field | Meaning here |
|---|---|---|
| 0–13 | TBP0 | the texture index (= header +0x08) |
| 14–19 | TBW | buffer width in 64-pixel units (at least 1) |
| 20–25 | PSM | pixel format |
| 26–29 / 30–33 | TW / TH | log2 width / height |
| 34 | TCC | texture alpha used |
| 35–36 | TFX | texture function |
| 37–50 | **CBP** | the **palette id** (`texpal_<CBP>`) |
| 51–54 | CPSM | palette format: 0 = 32-bit, 2 = 16-bit |
| 55 | CSM | CLUT storage mode (0 = CSM1) |

- **MIPTBP1 / MIPTBP2.** TBP1–3 sit at bits 0–13, 20–33 and 40–53 (TBW in between), and hold the texture indices of the mip levels. TEX1 bits 2–4 (MXL) give the number of mip levels.
- **CLAMP_1.** WMS / WMT are 0 for repeat and 1 for clamp.
- **TEST_1.** Holds the alpha test: ATST in bits 1–3, AREF in bits 4–11, with 0x80 = 1.0. Foliage uses it to cut out.

## PAL archive

```
palettes
  texpal_103           children 2
    par                8 bytes: u32 palette id, u32 type
    buf                512 bytes (16-bit) or 1024 bytes (32-bit)
```

`par` type: `0x00020200` for 16-bit palettes, `0x400` for 32-bit. The game registers the palette under the id in `par`. That id normally matches the `texpal_` name.

### CLUT buffer

256 entries in **CSM1 order**: the storage index of logical entry *i* is `(i & 0xE7) | ((i & 0x08) << 1) | ((i & 0x10) >> 1)`, which swaps bits 3 and 4. The same swap converts in both directions.

- **32-bit:** RGBA, alpha 0..0x80.
- **16-bit:** A1B5G5R5. There is no alpha channel, only STP (bit 15). In a palette that never sets STP, every entry is opaque except all-black ones. In one that does, an entry is clear only when it is black and lacks STP. So a transparent entry written to a 16-bit palette must be black without STP.

A 4-bit texture uses the first 16 CLUT entries in storage order, so logical indices 8–15 read entries 16–23.

## Binding a texture to its palette

The palette is `texpal_<TEX0.CBP>`, looked up first in the texture's own namespace and then in any PAL archive. The header's u16 at +0x08 is the texture index, not a palette id. Use CBP.

## Making and copying textures

- **New texture** (`PackageTextures.AddTexture`):
  - **Template.** Starts from an 8-bit texdat of the same TXR, preferring one without mip levels, and keeps its header and register list.
  - **Header and registers.** It then writes the new size, the pixel count, the next texture index (in the header and TBP0), TBW / TW / TH, PSM 0x13, the CBP and CPSM, and header +0x0F. TEX1's MXL is set to 0 and the MIPTBP registers are cleared.
  - **Size.** Powers of two from 8 to 512.
  - **Palette.** A median-cut 256-colour palette is made from the image. It is 32-bit when the image has partial alpha, otherwise it matches the template (16-bit in most retail TXRs).
- **Copying** (`PackageImport`):
  - The texture's mip levels come with it, and their indices are remapped in TBP0 and MIPTBP.
  - The palette keeps its id unless the target already uses it.
  - A name the target already uses keeps the target's texture when the pixels match. Otherwise the copy is renamed with a same-length name.
- **IDs.** New palette ids start from a block chosen per package (`1024 + 32 × hash(name) % 480`), above the disc's ids (≤ 594) and the character packs (600+).
- **Not yet in game.** New and copied textures decode correctly in the editors but have not all been tested in game. Runtime VRAM for map props is small, so large or numerous new textures may not all load.
