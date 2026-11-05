# SOCOM II map interactions and mission tables

Read from all 34 map ZDBs in the SCUS_972.75 disc's RUN directory. Counts below describe that disc. The source of the mission tables is each map's READERM.ZAR.

## Object actions

31 packages contain actions.rdr. MP5, MP8 and MP10 omit it. Its version 1 root holds a group with the key `actions` and a list of rows. Each row alternates a field name and a typed value list. The game binds an action by the name of the placed GEO node, which can be the parent of the visible model. MP51 binds `door_ft_1` to the valve of the same name, type DOOR, range 30, bitmap `action_door_open.tif` and map animation `singleDoor_left`. M51's door01 also supplies `kick_Door` as a second animation.

| Fields seen | Representation |
|---|---|
| node, valve, type, bitmap, descriptor | One string per field |
| anim | One or two animation names on this disc |
| range, elevation, points, team | One integer per field |
| one_shot, tcm | Presence of an empty list |

Named types on disc are DOOR, SWITCH, ITEM, SATCHEL, MPBOMB, DEFUSE and ACT_BREACH (449 rows: 200 DOOR, 133 ACT_BREACH, 40 SWITCH, 15 MPBOMB, 11 DEFUSE, 7 SATCHEL, 5 ITEM, 38 with no type). What the player sees comes from `bitmap` as much as `type`. The 7 **air strikes** (MP61, MP62, MP73, MP82) are SWITCH rows with `action_helo.tif`, a `descriptor` naming the target (`AIRSTRIKE AT DUNES`), the `tcm` flag (seen on no other row) and an `airstrike_*` animation; the editor shows them as their own kind. SWITCH rows also use `action_pickup_item.tif` (M53's `TAKE ITEM` documents) and `action_pull_lever.tif`.

**Turrets** have no actions.rdr rows. Every placed model from the shared turret archives (TURR, TRMG, TLRM, TLMG, TLLM, T30M: `RMG_turret`, `LMG_turret`, `30mm_MGL_turret` and their `_ledge` forms) gets the game's own `action_mount_turret.tif` / `action_dismount_turret.tif` prompts. Its settings (`MANUAL`, `MOUNTABLE_ANGLE`, aim limits, weapon) are in READERC.ZAR › ai_turrets.rdr under a name like the bare node holding the model (`mp73turret` for `mp73turret_1`). The link from a placed node to its ai_turrets entry is not decoded. The Actions tab marks these holders with a turret icon and shows the two prompts.

HUD_TXR.ZED carries 37 `action_*` prompts. Besides the ones above, it includes climb, slide, restrain, conceal body, knife, escort, talk and radio-channel prompts. These are not used by any actions.rdr row and are probably driven by the game code. Some rows use only node, valve and anim. READERM.ZAR/valves.rdr supplies mission state names. MZANIM.ZAR contains map animation scripts, HUD_TXR.ZED contains prompt art, and the shared MOTION_P.ZAR contains character motions. A copied binding can operate an existing mission valve. It does not create an independent script or door mechanism.

## Traversal geometry

The climb and ladder candidates contain a GEO `di / di` polygon with `params` and `points` children. A quad stores four local XYZ points, each followed by a fourth float. On M51, `ladderupdown` has two quads with the 12 parameter bytes `000400000300000012002044`. MP51's `di_climb_uppy13_1` has a quad with `0000000003000000121C4044`. Other named climb and ladder markers have variants. These are sampled profiles, and individual flag bits have not been decoded.

The editor can replace the profile of an existing quad or add a quad across a side of a placed node's local bounds. It preserves existing points when changing a profile. These edits reproduce file structures. Test approach direction, usable height, mount, and dismount in the game. The single-face tool does not reconstruct M51's multi-face ladder layout automatically.

## Other map tables

| Record | Maps | Evidence and editor use |
|---|---:|---|
| mission.rdr | 34 | Mission description, goals, elevation, AI parameters, and mission-specific state. M51 includes an AI visibility list. |
| valves.rdr | 34 | Named state entries with ID, initial value and runtime field. Useful alongside actions and objectives. IDs can repeat. |
| vehicles.rdr | 34 | Actor and vehicle setups. M51 includes SPECTER, JESTER and their named start references and teams. These refer to map points. |
| aimaps.rdr | 34 | Map layer filenames such as Church1.map. AIMAPS.MPS also carries the compiled AI grids and points. |
| map.rdr | 31 | Min/max extents, floor count and floor list. |
| hudobjectives.rdr | 12 | Objective text keys, map location pairs, valve, optional invalidation valve, nav points and on-call animation. Useful for a spatial objective layer. |
| missioncams.rdr | 13 | Camera cue identifiers, description keys and on-call animations. The example rows reference scripts. |
| layers.rdr | 33 | Authoring layer names, hidden/export flags and node lists, sometimes empty. |
| clutter.rdr / clutter_template.rdr | 28 / 1 | Generation settings and conversion templates. Individual placed clutter is in CLUTTER.ZAR. |
| dyntex.rdr / lod.rdr / lensflare.rdr | 33 / 8 / 3 | Dynamic texture boundaries, LOD definitions and light-source or flare settings. |
| chartype.rdr / net_000.rdr / sndjukeboxm.rdr | 22 / 5 / 34 | Character definitions, named graphs and mission music configuration. |
| Map-named RDR / *_lib.rdr | 34 / 80 library records | World/export settings and asset library metadata. M51 includes meters per unit, lighting, shadow direction and night vision colors. Check compiled GEO and ZED counterparts before relying on edits to export settings. |

The [map mission tables](formats/map-tables.md) page decodes these tables field by field; the Map Builder's Mission tab shows objectives, actors, mission settings and world settings, and draws objective flags in 3D. The Actions tab lists the mission tables actually present and opens them in the Asset Viewer's RDR editor. Shared RUN/READERC.ZAR separately contains animset, motion, dynamics, materials, global valves, mission list, AI and HUD definitions. Changes to those shared files have a different scope from a map copy.
