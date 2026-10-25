# SOCOM II asset formats

How Map Builder decodes (and writes) each kind of asset in a *SOCOM U.S. Navy SEALs II* package. Every page is taken from the code that reads the format (`server/CodeDesigner.Ps2.FileFormats/Zipper/…`), and each layout says where that is, so the two stay in step.

All values are little-endian. Offsets are in hex. "Confirmed in game" means a file written this way was loaded and drawn by SOCOM II. "Observed" means the layout matches every retail file checked, but nothing written that way has been tested in game.

| Page | Covers | Code |
|---|---|---|
| [Packages](packages.md) | .ZDB packages, .CDB memory-card packs, what a map package holds | `ZDB/ZdbArchive.cs`, `Package/PackageSave.cs`, `Package/ZipperPackage.cs` |
| [ZED / ZAR containers](zed-zar.md) | The record container every archive uses | `ZED/ZedContainer.cs`, `ZED/ZedTree.cs` |
| [Models](models.md) | `*_MDL.ZED` records, DMA chains, geometry chunks, vertex colours | `Model/ZedModelFile.cs`, `Geo/VisualPlacement.cs` |
| [GEO scenes](geo.md) | `*_GEO.ZED` model definitions, nparams, visuals, the map scene | `Geo/GeoScene.cs` |
| [Characters](characters.md) | Skinned `MESH_*` meshes and their skeletons | `Package/CharacterMesh.cs` |
| [Textures and palettes](textures.md) | `*_TXR.ZED` texdat, GS registers, `*_PAL.ZED` CLUTs | `Texture/ZedTextureFile.cs`, `Package/PackageTextures.cs` |
| [Clutter](clutter.md) | `CLUTTER.ZAR` placements | `Geo/ClutterFile.cs` |
| [AI maps and points](aimaps.md) | `AIMAPS.MPS` grids, spawns and waypoints | `MPS/MpsFile.cs`, `MPS/MapPoints.cs` |
| [RDR tables](rdr.md) | `.rdr` typed tables (READERM.ZAR, ZWEAPON …) | `RDR/RdrFile.cs` |
| [Animations](animations.md) | Motion clips (MOTION_S / MOTION_P) and ZANIM effect scripts | `Anim/MotionClip.cs`, `Anim/ZanimFile.cs` |

## Conventions used throughout

- **Namespaces.** An archive's prefix is its namespace: `M51_MDL.ZED` / `M51_GEO.ZED` / `M51_TXR.ZED` / `M51_PAL.ZED` all belong to `m51`. A model in `<ns>_MDL.ZED` is defined in `<ns>_GEO.ZED` and textured from `<ns>_TXR.ZED` with palettes in `<ns>_PAL.ZED`. Support namespaces such as `weap`, `clib`, `flib`, `effe` and `benz` work the same way. `WORL_MDL.ZED` holds the map's `worldmodel` and uses the map's own TXR/PAL pair.
- **Names are global in game.** Texture names and palette ids share one table across everything loaded (the first one registered wins), so new ones must not collide with any other loaded package.
- **Model space is Y up** as stored, in game units. Matrices are 4×4 floats, column-major, translation in elements 12–14.
