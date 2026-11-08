# SOCOM 3 / Combined Assault worlds: scatter instances and surfaces

How the Map Builder draws a *SOCOM: Combined Assault* location: the island's node tree, the scatter instances placed beside it, and the GS state that decides how each surface draws. Code: `Ca/CaLibrary.cs` (`Instances`, `AdRegisters`, `IsBlend`, `AlphaTest`, `TextureState`), `Ca/CaPackage.cs` (`MapScene`, `FindTextureState`), `Services/AssetService.Ca.cs` (`CaTextureInfo`). Tests: `CaWorldTests` (the retail check needs `MAPBUILDER_CA_DISC`, the disc root).

These layouts were observed on the retail disc (SCUS-97545), on all 24 locations. Nothing is written back.

## What a world draws

An island's `ISLE` library holds its world model (the model with the most nodes) and its prop models. The world is drawn from two sources:

1. **The node tree.** Every node's static objects (type `0x00841398`). A node whose `+0x88` points at a prop model is an instance of it, but the node's own subtree already repeats that model's objects (checked on all 10,924 such nodes; the 23 with nothing below them point at models with no objects), so nothing more is added for it.
2. **Scatter instances.** Rocks, trees, bushes, crates and debris that are not in the node tree at all. The disc has 18,262 of them. Before they were read, Reservoir (the multiplayer map Waterworks) drew 258 of its 755 objects.

## Scatter instances (0x70 bytes)

The island keeps them in one vector. Its spatial grid's cells hold sub-ranges of it and pointers into it. `CaLibrary.Instances` finds every `{begin, end}` vector whose records all match the layout below, and takes each record once.

| Offset | Size | Field |
|---|---|---|
| 0x00 | f32 × 16 | Transform: a 4×4 matrix (rows are the axes, translation in row 3, as node matrices) |
| 0x00 | f32 × 4 | …or, with flag bit `0x02`, a quaternion (x, y, z, w) |
| 0x10 | f32 × 4 | (quaternion form) identity (0, 0, 0, 1) |
| 0x20 | f32 × 4 | (quaternion form) position xyz, uniform scale in w |
| 0x40 | f32 × 2 | Fade distances, squared (e.g. 500², 900²) |
| 0x4C | u32 | Flags: bit `0x02` quaternion form; the high half varies (`0x8000`, `0x10000`, `0x20000`) |
| 0x5C | u32 | A colour (RGBA) |
| 0x60 | u32 | The prop model (offset of a model in the library's model vector) |
| 0x64 | u32 | 0 |
| 0x68 | f32 × 2 | −1.0, −1.0 |

The quaternion turns the usual way, the same as three.js `Matrix4.compose`. Checked against the ground: of 1,380 tilted instances standing on slopes in five locations, 1,217 lean with the slope this way and 163 the other way. Each instance draws its model's nodes (without LOD copies, as props do) at `instance × node world`.

A few records are parked far off their island (12 at RiverRecon's origin, two of Reservoir's thousands of units out). Instances more than 1,000 units outside the world root's bounds (`+0xB0` min xyz, `+0xBC` max xyz) are not drawn; 56 of the 18,262 are skipped this way.

## Surfaces: blend, alpha test, clamping

Each static object's sections begin with a material (DMA tag kind 6), a GS A+D packet. Each texture record's own packet (`+0x14`) sets the same registers plus TEX0. Every texture uses MODULATE with texture alpha (TEX0 TFX 0, TCC 1), so a texel is texture × vertex colour, with 0x80 = 1.0, as in SOCOM II.

| ALPHA_1 | TEST_1 | Where | Drawn as |
|---|---|---|---|
| `0x44`: (Cs − Cd)·As + Cd | `0x53000` (alpha test off) | Almost every surface: terrain, its vertex-alpha blend layers, water, decals, distant tree and fog cards | Blended by texture alpha × vertex alpha |
| `0x4000000000`: Cs | `0x5040B` (GEQUAL 0x40) | Cut-out foliage | Opaque, texels under half alpha discarded |
| `0x48`: Cs·As + Cd | off | Effects | Additive (drawn as blended) |
| `0x42`: Cd − Cs·As | off | Effects | Subtractive (drawn as blended) |

### Which texture a material binds

A material's TEX0 is meant to say which of the object's textures (`+0x40`) it draws with, but every CA texture's TEX0 has base pointer 0 (the game places textures at run time). Two textures of the same size and format therefore look identical. On Farm (POLAND_RURAL `farm_i2`) the road and the grass are both 256×256 8-bit, and taking the first match painted the road layer with the dark ground texture, so the road vanished into dark, straight-edged patches. About a third of sections on the locations checked (1,780 of 5,231 on POLAND_RURAL) match more than one texture.

What does bind them is order: an object's texture list (`+0x40`) holds one texture per distinct material packet, in the order the packets first appear in its chains. A packet used again draws with the same texture, which is how a terrain object binds one texture from five materials. This holds for every one of the 43,726 sections on the disc whose TEX0 does name a single texture, in all 23,326 such objects. `CaLibrary.Sections` takes each material's texture from its packet's place in that order. A packet past the end of the list (not seen on the disc) falls back to its TEX0 match.

An earlier version learned packet → texture across the whole library from single TEX0 matches and guessed the rest by elimination. That fixed Farm's road, but the guesses left 1–340 sections per location on the wrong texture, mostly on the water and shore maps (Breakneck, Dune, Pirate Base). Before either version, the first TEX0 match won. That painted every layer of a Broken Islands shore object with one pale texture, which looked like a beach.

CLAMP_1 is 0 (repeat) or 5 (clamp S and T). The material's ALPHA_1 gives each chunk's `materialBlend` (the rule SOCOM II uses: B or D is Cd). The texture's registers give the texture info's `alphaBlend`, `alphaTest` and `clamp`.

The water on Reservoir, RiverRecon and other maps is one texture, `ca_34c168b2`: a 64×32 grey ripple at about 94% alpha with no fully opaque texel, tinted by vertex colour (brown-grey) and faded out at the shore by vertex alpha. It is drawn blended without writing depth. Before the GS state was read, CA textures with any alpha got a fixed 0.5 alpha test and no blend. The water drew as an opaque slab, and terrain blend layers (opaque textures faded by vertex alpha, 34% of RiverRecon's vertices) were cut into hard patches.

## Not done yet

- What the scatter flags' high half and the colour at `+0x5C` mean, and the `+0x30` row of the quaternion form (its x varies, y and z repeat the position).
- Fade distances are not used; everything draws at full detail.
- Additive and subtractive materials draw as normal blending, and the water's scrolling and any reflection pass are not reproduced.
- Scatter instances are read-only and show in the World layer.
