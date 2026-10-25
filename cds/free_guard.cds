// SOCOM II (SCUS-97275, r0004) - free guard for runtime asset loading
//
// Libraries loaded mid-round (socom_bots.py loadlib) came back with their texture objects overwritten a few
// seconds later: the loader/reader tear-down that runs inside the same game_call frees memory those objects
// still use. While the flag at 0x000D06F8 is set, free (0x00180280) and delete (0x00180AD0) return without
// freeing and count the skipped calls at 0x000D06FC. The script sets the flag only around the load, so the
// cost is leaking that load's temporary buffers once. With the flag clear both behave exactly as before.
//
// Entry patches (two words each): 0x00180280 / 0x00180AD0 -> j <guard>; nop. The guard re-executes the
// overwritten prologue (addiu sp,-16 / beq a0,zero,<ret> with sd ra in the delay slot) and jumps back.

address $000D06F8
free_guard_flag:
    hexcode $00000000
free_guard_skipped:
    hexcode $00000000

address $000D1110                         // after lib_models.cds (0x000D1000..0x000D1103)
guard_free:                               // 0x00180280 free(a0)
    lui   $t0, $000D
    lw    $t1, $06F8($t0)
    beq   $t1, $zero, :gf_orig
    nop
    lw    $t1, $06FC($t0)
    addiu $t1, $t1, 1
    jr    $ra
    sw    $t1, $06FC($t0)
gf_orig:
    addiu $sp, $sp, -16
    sd    $ra, $0000($sp)
    beq   $a0, $zero, :gf_null
    nop
    j     $0018028C
    nop
gf_null:
    j     $00180588
    nop

guard_delete:                             // 0x00180AD0 delete(a0)
    lui   $t0, $000D
    lw    $t1, $06F8($t0)
    beq   $t1, $zero, :gd_orig
    nop
    lw    $t1, $06FC($t0)
    addiu $t1, $t1, 1
    jr    $ra
    sw    $t1, $06FC($t0)
gd_orig:
    addiu $sp, $sp, -16
    sd    $ra, $0000($sp)
    beq   $a0, $zero, :gd_null
    nop
    j     $00180ADC
    nop
gd_null:
    j     $00180DE8
    nop

address $00180280
    j     :guard_free
    nop

address $00180AD0
    j     :guard_delete
    nop
