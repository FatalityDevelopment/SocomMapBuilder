// SOCOM II (SCUS-97275, r0004) - inject an extra asset library (e.g. another map's character library) at MAP LOAD
//
// A library loaded mid-round never gets the map's post-load passes (palette binding at texture+0x1C, VRAM
// placement at entry+8, scene setup), so its models render as red noise. Loading it during the map load lets the
// game do all of that itself. Two call-site patches:
//
//   0x002B0A84  jal ZDB_Mount(ctx, name) -> jal capture_mount: remember the map archive name, then mount as before
//   0x001EA0A0  jal 0x0033AD00(level)    -> jal inject_postload: once armed, AFTER the map loader has read every
//               file (all libraries + their _mdl.zed) and BEFORE the post-load passes: reset the mount, mount the
//               other map's archive, AssetLib_Acquire(lib path), load its <lib>_mdl.zed (lib_models.cds), reset,
//               remount the map archive (captured name, else the map id at 0x004417C0 - an online join mounts through
//               another path and the capture stays empty), disarm; then run the original pass. (Injecting inside the
//               library loop at 0x00336874 instead crashed the console - the loader still had the archive in use.)
//
// Data (filled by socom_bots.py inject):
//   0x000D0980 armed            0x000D0984 lib_models result     0x000D0988 injected lib
//   0x000D098C other-mount rc   0x000D0990 other archive name    0x000D09A0 captured map archive name
//   0x000D09C0 library path     0x000D09E0 <lib>_mdl.zed path    0x000D09FC remount rc
// Needs lib_models.cds at 0x000D1000 and place_lib.cds at 0x000D1400: a weapon pack (category 0x02) comes after
// the loader's texture placement loop (0x001E9430) and loaded invisible, so it is placed here itself.

address $000D1200
capture_mount:                            // a0 = mount ctx, a1 = archive name
    lui   $t0, $000D
    addiu $t1, $t0, $09A0
    daddu $t2, $a1, $zero
    addiu $t3, $zero, 31
cm_loop:
    lbu   $t4, $0000($t2)
    sb    $t4, $0000($t1)
    beq   $t4, $zero, :cm_done
    addiu $t2, $t2, 1
    addiu $t1, $t1, 1
    addiu $t3, $t3, -1
    bne   $t3, $zero, :cm_loop
    nop
    sb    $zero, $0000($t1)
cm_done:
    j     $003C8C80
    nop

inject_postload:                          // a0 = level (*(0x00435538))
    addiu $sp, $sp, -48
    sd    $ra, $0000($sp)
    sd    $s0, $0008($sp)
    daddu $s0, $a0, $zero
    lui   $t0, $000D
    lw    $t1, $0980($t0)
    beq   $t1, $zero, :ip_pass
    nop
    sw    $zero, $0980($t0)               // disarm (one injection per arming)
    lui   $a0, $004A
    jal   $003C8B80                       // ZDB_Reset
    addiu $a0, $a0, $4DD0
    lui   $a0, $004A
    addiu $a0, $a0, $4DD0
    lui   $a1, $000D
    jal   $003C8C80                       // ZDB_Mount(other)
    addiu $a1, $a1, $0990
    lui   $t0, $000D
    sw    $v0, $098C($t0)
    beq   $v0, $zero, :ip_remount
    nop
    lui   $a0, $000D
    jal   $0033E380                       // AssetLib_Acquire(extra library)
    addiu $a0, $a0, $09C0
    lui   $t0, $000D
    sw    $v0, $0988($t0)
    beq   $v0, $zero, :ip_remount
    nop
    lui   $a0, $000D
    addiu $a0, $a0, $0988
    lui   $a1, $000D
    jal   $000D1000                       // lib_models(&lib, "run/../<lib>_mdl.zed")
    addiu $a1, $a1, $09E0
    lui   $t0, $000D
    sw    $v0, $0984($t0)
    lw    $a0, $0988($t0)                 // weapon packs (category 0x02, lib+0x2F): the loader's texture placement
    lbu   $t1, $002F($a0)                 //   loop (0x001E9430) already ran, so place them ourselves -
    addiu $t2, $zero, 2                   //   place_lib(lib, 0x1C88, 2, 0) (place_lib.cds, TexCommonCS B1/B2)
    bne   $t1, $t2, :ip_remount
    nop
    addiu $a1, $zero, $1C88
    addiu $a2, $zero, 2
    jal   $000D1400
    daddu $a3, $zero, $zero
ip_remount:
    lui   $a0, $004A
    jal   $003C8B80                       // ZDB_Reset
    addiu $a0, $a0, $4DD0
    lui   $a1, $000D
    addiu $a1, $a1, $09A0                 // captured map archive name...
    lbu   $t1, $0000($a1)
    bne   $t1, $zero, :ip_mount_map
    nop
    lui   $a1, $0044                      // ...or, if the mount went through another path, the map id (0x004417C0)
    addiu $a1, $a1, $17C0
ip_mount_map:
    lui   $a0, $004A
    jal   $003C8C80                       // ZDB_Mount(map archive)
    addiu $a0, $a0, $4DD0
    lui   $t0, $000D
    sw    $v0, $09FC($t0)
ip_pass:
    jal   $0033AD00                       // the original first post-load pass, now including our library
    daddu $a0, $s0, $zero
    ld    $s0, $0008($sp)
    ld    $ra, $0000($sp)
    jr    $ra
    addiu $sp, $sp, 48

address $000D0980
inject_armed:
    hexcode $00000000
