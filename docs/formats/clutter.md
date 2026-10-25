# Clutter (`CLUTTER.ZAR`)

The trees, bushes, grass, rocks and small props a map places outside its GEO scene. Code: `Geo/ClutterFile.cs` (a port of `parseClutterPlacements` in the page's `zed-scene-compose.ts`).

## Tree

```
Clutter_ZAR_Version    4 bytes
Clutter                children = clutter models
  oaktree_facade3      children = placements
    a_clutter          one per placement
      params           96 bytes
      scale_inverse    f32 = 1 / scale
```

Each model record's name is the model it places, resolved like a GEO scene name to an MDL record. M51 has 12 clutter models with 2,078 placements in all (`bush2_near` alone has 1,089).

## params (96 bytes, 24 floats)

Two layouts occur:

| Layout | How to tell | Transform |
|---|---|---|
| Matrix | floats 3, 7, 11 = 0 and float 15 = 1 | floats 0–15 are a 4×4 column-major matrix, translation in 12–14 (as nparams) |
| Quaternion | floats 4–7 = 0, 0, 0, 1 and float 11 > 0 | floats 0–3 = rotation quaternion (x, y, z, w; not always unit length), 8–10 = position, 11 = uniform scale |

Floats 12–23 of the quaternion layout are kept verbatim. Editing a placement rewrites `params` in its own layout and `scale_inverse` alongside (`ClutterFile.WritePlacement`). Adding one copies an existing `a_clutter` of the same model (of any model when it has none, adding the model's group under `Clutter`) so the floats past the transform keep plausible values, then writes the new transform. Removing the last placement of a model removes its group too.

## Facade trees

The `*_facade*` trees are single flat cards. Map Builder turns them toward the camera when drawing them, so they look like they do in game; nothing in the placement says so.
