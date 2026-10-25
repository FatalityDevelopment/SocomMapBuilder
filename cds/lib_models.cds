// SOCOM II (SCUS-97275, r0004) - load an asset library's model definitions (<lib>_mdl.zed)
//
// AssetLib_Acquire (0x0033E380) only reads a library's _txr/_pal/_geo parts. The map loader (0x001E91C0, inline
// at 0x001E9DB8) then reads each library's model file; a library loaded at runtime without this step has bodies
// with no render data (invisible, and the bone-matrix upload 0x00355A10 writes through a null palette buffer).
// This does the same steps for one library, while that library's map archive is mounted:
//
//   R = 0x000D5800, a PRIVATE reader built with the reader constructor 0x002596E0(R, 0, 0) and destroyed with
//   0x00259560(R, -1) at the end. (The first version borrowed the game's global model reader 0x00435480; in a
//   campaign mission the game was using it, our open failed and our close shut the game's reader -> hang.)
//   if (0x00258F60(R, path, -1, 1, 0x10, 0))            open run/<dir>/<lib>_mdl.zed
//       for each model m in lib (+0x0C count, +0x10 array) whose name (m+0x90) != "worldmodel":
//           0x003D4E00(R, m)
//       lib+0x34 = 0x002585D0(R)
//   0x00259280(R)                                       close
//
// Called through game_call.cds: a0 = ADDRESS of the lib pointer (the game_call result slot of the AssetLib_Acquire
// call that ran just before, so one mount swap does both), a1 = path string. Returns lib+0x34.

address $000D1000
lib_models:
    addiu $sp, $sp, -80
    sd    $ra, $0000($sp)
    sd    $s0, $0008($sp)
    sd    $s1, $0010($sp)
    sd    $s2, $0018($sp)
    sd    $s3, $0020($sp)
    sd    $s4, $0028($sp)
    lw    $s0, $0000($a0)             // lib (from the Acquire result slot)
    lw    $t0, $000C($s0)
    beq   $t0, $zero, :lm_none        // no models parsed (its parts were not found): reading the _mdl.zed would
    sw    $a1, $0038($sp)             // only park ~0.7 MB of heap in an empty library. path
    lui   $s4, $000D
    addiu $s4, $s4, $5800             // R = private reader
    daddu $a0, $s4, $zero
    daddu $a1, $zero, $zero
    jal   $002596E0                   // construct it
    daddu $a2, $zero, $zero
    lw    $a1, $0038($sp)
    daddu $a0, $s4, $zero
    addiu $a2, $zero, -1
    addiu $a3, $zero, 1
    addiu $t0, $zero, $0010
    jal   $00258F60
    daddu $t1, $zero, $zero
    andi  $v0, $v0, $00FF
    beq   $v0, $zero, :lm_close
    nop
    lw    $s2, $000C($s0)             // model count
    lw    $s3, $0010($s0)             // model array
    daddu $s1, $zero, $zero
lm_loop:
    sltu  $v0, $s1, $s2
    beq   $v0, $zero, :lm_read
    nop
    sll   $v0, $s1, 2
    addu  $v0, $s3, $v0
    lw    $a1, $0000($v0)             // model
    sw    $a1, $0030($sp)
    lw    $a0, $0090($a1)             // model name
    lui   $a1, $0041
    jal   $00198F18                   // strcmp(name, "worldmodel")
    addiu $a1, $a1, $D198
    beq   $v0, $zero, :lm_next
    nop
    daddu $a0, $s4, $zero
    jal   $003D4E00
    lw    $a1, $0030($sp)
lm_next:
    beq   $zero, $zero, :lm_loop
    addiu $s1, $s1, 1
lm_read:
    jal   $002585D0
    daddu $a0, $s4, $zero
    sw    $v0, $0034($s0)
lm_close:
    jal   $00259280                   // close
    daddu $a0, $s4, $zero
    daddu $a0, $s4, $zero
    jal   $00259560                   // destroy (flag -1: the object itself is not freed)
    addiu $a1, $zero, -1
lm_none:
    lw    $v0, $0034($s0)             // 0 for an empty library
    ld    $s4, $0028($sp)
    ld    $s3, $0020($sp)
    ld    $s2, $0018($sp)
    ld    $s1, $0010($sp)
    ld    $s0, $0008($sp)
    ld    $ra, $0000($sp)
    jr    $ra
    addiu $sp, $sp, 80
