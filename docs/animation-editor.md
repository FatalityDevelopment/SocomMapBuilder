# Animation Editor

The Animation Editor plays SOCOM II character clips on skinned models, edits the clips a package owns, and fires weapons mounted on the clip so you can see their muzzle effects.

<img src="screenshots/animation-editor.png" alt="The Animation Editor playing seal_walk_alert on a SEAL holding an M4A1" width="850">

*`seal_walk_alert` playing on a SEAL with the M4A1 mounted. The right panel lists the bones, the clip and the weapon's firing effects.*

## Layout

| Area | What it holds |
|---|---|
| **Header** | The character, shading, the mounted weapon, **Fire** and **Auto** (fire continuously), and Skeleton / Mesh toggles |
| **Animations** (left) | Every clip, filtered by name or by prefix chips (`civ_`, `conv_`, `death_`, `guard_`, `seal_` …). Package clips come first. |
| **Toolbar** | Select bone (Q), Rotate bone (R), Move bone (G); both transform tools set keys |
| **Viewport** | The character playing the clip, with its skeleton |
| **Timeline** | Previous / next frame (`,` / `.`), play / pause (Space), the scrubber, speed (0.1× to 2×) and loop |
| **Right panel** | Bones, the clip (name, source, bone count, duration) and the weapon (model, fire effect, spawned effects, flash, shell, rate) with its ZANIM effect scripts decoded |

## Clips

- Package-local clips in `MOTION_S.ZAR` are editable; the shared `MOTION_P.ZAR` is read-only. **Copy into package…** makes an editable copy of a shared clip.
- Create, duplicate, rename, delete and retime clips.
- Key bones with the gizmo or with exact values in the fields.
- All edits are undoable and are saved with the rest of the package.

## Weapons

With the game's `RUN` folder available (beside the package, one folder up, or on the disc), the chosen weapon mounts on the clip's `rifle` track under the right hand. **Fire** plays what the game plays when that weapon fires (muzzle flash, shell eject and the rest of its `ZANIM` effects); **Auto** keeps firing at the weapon's rate.

The same mount is used for the characters the [Map Builder](map-builder.md#characters-at-their-start-points) draws at their start points.

## Related

- [Animations format](formats/animations.md): motion clips and ZANIM effect scripts
- [Characters format](formats/characters.md)
