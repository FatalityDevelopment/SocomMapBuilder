# Animations

Two kinds: character motion clips, and ZANIM effect scripts (muzzle flashes, shell ejection, smoke, sounds, camera shakes). Code: `Anim/MotionClip.cs`, `Anim/PackageAnimations.cs`, `Anim/ZanimFile.cs`.

## Motion clips (`MOTION_S.ZAR`, `RUN/MOTION_P.ZAR`)

Each record of a map package's `MOTION_S.ZAR` is a clip named by its record; `RUN/MOTION_P.ZAR` next to the packages holds the shared ones. Clips are the version-5 `.rdr` layout written by `Mods/SOCOMCA/ca_anim.py` in CodeDesigner. Clips written that way play in game (the ported CA clips).

| Offset | Size | Field |
|---|---|---|
| 0x00 | i32 | version, 5 |
| 0x04 | f32 | duration (seconds) |
| 0x08 | i32 | frame intervals (samples = intervals + 1, spread evenly over the duration) |
| 0x0C | i32 | bone count |
| 0x10 | f32 | norm min (−1) |
| 0x14 | f32 | norm max (1) |
| 0x18 | i32 | names offset |
| 0x1C | i32 | tracks offset |
| 0x20 | 16 × bones | bone records (runtime scratch, kept as stored) |
| names offset | | NUL-terminated bone names (leading dashes, as in `--hips`, are part of the name), padded to 4 |
| tracks offset | | one track per bone, in name order |

Each track starts with an i32 kind:

| Kind | Track |
|---|---|
| `0x3C` static | position (x, y, z, w) int16, rotation (x, y, z, w) int16 |
| `0x2C` animated rotation | position (x, y, z, w) int16, then a rotation per sample |
| `0x1C` animated position | a position (x, y, z) int16 per sample, pad to 4, one rotation |
| `0x0C` animated | a position (x, y, z) per sample, pad to 4, a rotation per sample |

- **Positions** are the bone's local translation in **1/256 units**. A seal's calf is 1108, which is 4.33 units and its GEO bind length.
- **Rotations** are local quaternions under the parent bone, int16 / 32767.
- **Bone matching.** Bones match the character skeleton by name.
- **The static w word.** The fourth word of a static position is 612 in retail clips, with no known effect.
- **Pad bytes.** The bytes between the positions and the rotations are not always zero. They are kept.

## ZANIM effect scripts (`CZANIM.ZAR`, `MZANIM.ZAR` …)

Container tree:

```
Anim_Sets
  <set>
    Name_Table            names the scripts refer to
    RdrPath_List
    Animation_List
      <anim>
        Anim_Params       24 bytes
        Name_Index_Table  u16 indices into the set's names: [0] NA, [1] the animation itself
        Node_Ref_List     8-byte object refs: byte 1 = name index, byte 2 = flags, byte 3 = parent ref
        Seq_Data          the script
```

**Seq_Data** is a list of tracks that run in parallel:

- **Track header.** Each track opens with a 7-word header: target name index, flags (`0x0102` / `0x0104`), 0, byte size including the header, 0, 0, 0.
- **Instructions.** Each instruction is a u16 opcode, then a u16 of `words << 4 | flags`, then its arguments at byte offsets from the instruction start.
- **Opcodes.** Opcode names come from the game's own table (EE RAM `0x84F2A0`, 20-byte records). Examples: `0x02` IF, `0x0F` WAIT, `0x1B` PARTICLE_SOURCE, `0x1E` SOUND, `0x2D` CALL_ANIMATION, `0x39` FIRE_WEAPON, `0x47` CAMERA_SHAKE. The full list is `ZanimFile.OpNames`.
- **Object operands.** 0 means none, `0xF9` the effect's own object, `0xFA` its owner (for example the weapon's firepoint). Anything else is an index into the node refs.

Weapons fire their muzzle effects through these scripts. The firepoint locators and `ZWEAPON` muzzle names select which script runs and where.
