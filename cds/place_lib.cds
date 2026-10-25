// SOCOM II (SCUS-97275, r0004) - give a runtime-loaded asset library its VRAM / palette placement
//
// The map loader (0x001E91C0) places every library's textures and palettes into the GS texture pools in a
// per-library loop (0x001E9430..0x001E9C04), switching on the library category byte (lib+0x2F, = the TXR's
// renderphase) through the table at 0x0040D1E0. A library added later (socom_bots.py loadlib / loadpack) skips that
// loop, so its texture entries keep random VRAM slots (entry+8) and no palette (texture+0x1C) -> red noise.
// Every case is the same routine over a category's own texture pools:
//
//   pools = *(0x004A1C48); idx_k = the pool index variables of the category:
//     0x10 characters  0x004A1D30 / 34 / 38   (case 0x001E9868)  clear pool+4 after placing textures
//     0x02 weapons     0x004A1C88 / 8C        (case 0x001E97C8)
//     0x11 vehicles    0x004A1D3C / 40 / 44   (case 0x001E99B8)  clear pool+4
//   for k: 0x00374110(pools[idx_k], lib, 1); [pools[idx_k]+4 (byte) = 0]    place textures
//   for k: 0x003748F0(pools[idx_k], lib)                                     place palettes
//
// a0 = lib, a1 = low half of the first pool index variable (0x1D30 = 0x004A1D30), a2 = how many, a3 = 1: clear
// pool+4. Called through game_call.cds.

address $000D1400
place_lib:
    addiu $sp, $sp, -64
    sd    $ra, $0000($sp)
    sd    $s0, $0008($sp)
    sd    $s1, $0010($sp)
    sd    $s2, $0018($sp)
    sd    $s3, $0020($sp)
    sd    $s4, $0028($sp)
    daddu $s0, $a0, $zero                 // lib
    daddu $s1, $a1, $zero                 // pool index variable offset (0x004A0000 + a1, +4, +8)
    daddu $s2, $a2, $zero                 // count
    daddu $s3, $a3, $zero                 // clear pool+4
    daddu $s4, $a2, $zero
pl_tex:
    lui   $v1, $004A
    addu  $v1, $v1, $s1
    lw    $v1, $0000($v1)                 // pool index
    lui   $v0, $004A
    lw    $v0, $1C48($v0)                 // pool table
    sll   $v1, $v1, 2
    addu  $v0, $v0, $v1
    lw    $a0, $0000($v0)                 // pool
    sw    $a0, $0030($sp)
    daddu $a1, $s0, $zero
    jal   $00374110
    addiu $a2, $zero, 1
    beq   $s3, $zero, :pl_next
    lw    $v0, $0030($sp)
    sb    $zero, $0004($v0)
pl_next:
    addiu $s1, $s1, 4
    addiu $s2, $s2, -1
    bne   $s2, $zero, :pl_tex
    nop
    sll   $v0, $s4, 2
    subu  $s1, $s1, $v0                   // back to the first variable
    daddu $s2, $s4, $zero
pl_pal:
    lui   $v1, $004A
    addu  $v1, $v1, $s1
    lw    $v1, $0000($v1)
    lui   $v0, $004A
    lw    $v0, $1C48($v0)
    sll   $v1, $v1, 2
    addu  $v0, $v0, $v1
    lw    $a0, $0000($v0)
    jal   $003748F0
    daddu $a1, $s0, $zero
    addiu $s1, $s1, 4
    addiu $s2, $s2, -1
    bne   $s2, $zero, :pl_pal
    nop
    ld    $s4, $0028($sp)
    ld    $s3, $0020($sp)
    ld    $s2, $0018($sp)
    ld    $s1, $0010($sp)
    ld    $s0, $0008($sp)
    ld    $ra, $0000($sp)
    jr    $ra
    addiu $sp, $sp, 64
