"""Register-agnostic ColorTable emissive patch for skin.shpk Emissive pass[2] PSes.

The original dxbc_patch_colortable.py hardcodes register index 1 for the emissive
dest register. This works for 8/32 Emissive pass[2] PSes (those where SceneKey maps
to a variant with `mul r1.xyz, cb0[3]*cb0[3]`), but the remaining 24 PSes use r2
(or other registers) as emissive dest. Those silently skip the ColorTable patch at
runtime, falling back to vanilla `g_EmissiveColor^2 * normal.alpha * m_EmissiveColor_dyn`
behavior -- no per-layer emissive, no animation.

This module detects the emissive init pair generically and writes a replacement
with the actual dest register and normal source register parameterized.

The 2-instruction emissive init pair we locate:
    mul  rX.xyz, cb0[3].xyzx, cb0[3].xyzx     ; 9 tokens, cb3*cb3 tail invariant
    mul  rX.xyz, rS.<swiz>, rX.xyzx            ; 7 tokens, swiz = zzzz or wwww

Gets replaced with (dynamically constructed):
    mad    rX.y, rS.z, 0.9375, 0.015625      ; row UV
    mov    rX.x, 0.3125                       ; col UV (emissive col 2)
    sample rX.xyzw, rX.xyxx, t10, s5          ; ColorTable read

Notes:
  - For the 32 Emissive pass[2] PSes, normal swizzle is always `.zzzz` (normal.alpha
    lands at the .z component after `sample_b t5.zxwy -> r0.xz` or `r1.yz`).
  - If later evidence shows a PS with `.wwww` normal swizzle, the patcher would need
    to parameterize normal component too (currently fixed at .z).
"""
import struct
import os
import sys

from dxbc_patch_colortable import rebuild_dxbc, d3d_disassemble, d3d_validate


# -- Emissive init detection (shared with dxbc_patch_gloss_mask) --

# Invariant 6-token tail of the first emissive init: `cb0[3].xyzx, cb0[3].xyzx`.
_CB3_TAIL = struct.pack('<6I',
    0x00208246, 0x00000000, 0x00000003,
    0x00208246, 0x00000000, 0x00000003)


def _iter_shex_instructions(shex_data: bytes):
    pos = 8
    n = len(shex_data)
    while pos < n:
        tok = struct.unpack_from('<I', shex_data, pos)[0]
        length = (tok >> 24) & 0x7F
        if length == 0:
            length = 1
        blen = length * 4
        if pos + blen > n:
            break
        yield pos, tok, blen
        pos += blen


def find_emissive_init(shex_data: bytes):
    """Locate the 2-instruction emissive init pair.

    Returns (init1_pos, init2_pos, dest_reg, normal_reg, normal_comp) or None.
      - init1_pos / init2_pos: SHEX byte offsets of the 1st and 2nd MUL
      - dest_reg: the rX register that holds emissive^2
      - normal_reg / normal_comp: rS.<comp> -- register and component holding normal.alpha
    """
    for pos, tok, blen in _iter_shex_instructions(shex_data):
        opcode = tok & 0x7FF
        if opcode != 0x38 or blen != 36:
            continue
        if shex_data[pos + 12:pos + 36] != _CB3_TAIL:
            continue
        # Matched: `mul rX.xyz, cb0[3]*cb0[3]`. Extract dest register from token 2.
        dest_op = struct.unpack_from('<I', shex_data, pos + 4)[0]
        dest_reg = struct.unpack_from('<I', shex_data, pos + 8)[0]
        if dest_op != 0x00100072:  # TEMP, mask mode, mask .xyz
            continue

        # Parse the immediately following 7-token MUL: `mul rX.xyz, rS.<swiz>, rX.xyzx`
        next_pos = pos + blen
        if next_pos + 28 > len(shex_data):
            continue
        tok2 = struct.unpack_from('<I', shex_data, next_pos)[0]
        if (tok2 & 0x7FF) != 0x38 or ((tok2 >> 24) & 0x7F) * 4 != 28:
            continue
        # src0 at next_pos + 12
        src0_tok = struct.unpack_from('<I', shex_data, next_pos + 12)[0]
        src0_reg = struct.unpack_from('<I', shex_data, next_pos + 16)[0]
        num_comp = src0_tok & 0x3
        sel_mode = (src0_tok >> 2) & 0x3
        op_type = (src0_tok >> 12) & 0xFF
        if num_comp != 2 or sel_mode != 1 or op_type != 0:
            continue
        # swizzle mode: all 4 components should select the same one (zzzz or wwww)
        swiz = (src0_tok >> 4) & 0xFF
        c0 = swiz & 3
        c1 = (swiz >> 2) & 3
        c2 = (swiz >> 4) & 3
        c3 = (swiz >> 6) & 3
        if not (c0 == c1 == c2 == c3):
            continue

        return (pos, next_pos, int(dest_reg), int(src0_reg), c0)
    return None


# -- Replacement construction --

def build_colortable_replacement(dest_reg: int, normal_reg: int, normal_comp: int) -> bytes:
    """Build register-agnostic ColorTable sample replacement.

    Replaces the 16-token (64-byte) emissive init pair with 25 tokens (100 bytes)
    that sample column 2 (emissive RGB) of the ColorTable at row selected by
    normal.alpha. Caller is responsible for updating SHEX token count (+9).
    """
    # mad rDest.y, rNormal.<normal_comp>, l(0.9375), l(0.015625)
    mad_src_norm = 0x0010000A | (normal_comp << 4)  # TEMP select_1 comp
    return struct.pack('<25I',
        # mad rDest.y, rNormal.<comp>, 0.9375, 0.015625
        0x09000032,               # mad, length=9
        0x00100022, dest_reg,     # dest rDest.y
        mad_src_norm, normal_reg, # src0 rNormal.<comp>
        0x00004001, 0x3F700000,   # src1 l(0.9375)
        0x00004001, 0x3C800000,   # src2 l(0.015625)
        # mov rDest.x, l(0.3125)
        0x05000036,               # mov, length=5
        0x00100012, dest_reg,     # dest rDest.x
        0x00004001, 0x3EA00000,   # src l(0.3125)
        # sample_indexable(texture2d)(float4) rDest.xyzw, rDest.xyxx, t10.xyzw, s5
        0x8B000045, 0x800000C2, 0x00155543,   # extended sample opcode + return type mask
        0x001000F2, dest_reg,     # dest rDest.xyzw
        0x00100046, dest_reg,     # src0 rDest.xyxx (swizzle xyxx)
        0x00107E46, 0x0000000A,   # src1 t10.xyzw
        0x00106000, 0x00000005,   # src2 s5
    )


# -- Patch API --

def patch_shex_replace_emissive_v2(shex_data: bytearray):
    """Return (patched_shex, info) where info = (dest_reg, normal_reg, normal_comp) or None.

    Unlike the v1 patch (which matched only r1 dest variants), this version handles any
    dest register and any single-component normal swizzle (z or w).
    """
    match = find_emissive_init(shex_data)
    if match is None:
        return None, None
    init1_pos, init2_pos, dest_reg, normal_reg, normal_comp = match

    replacement = build_colortable_replacement(dest_reg, normal_reg, normal_comp)
    # Replace 16 tokens (64 bytes) with 25 tokens (100 bytes).
    old_size = 16 * 4
    new_size = len(replacement)
    assert new_size == 25 * 4

    result = bytearray(shex_data[:init1_pos])
    result.extend(replacement)
    result.extend(shex_data[init1_pos + old_size:])

    # Update SHEX token count
    old_count = struct.unpack_from('<I', result, 4)[0]
    new_count = old_count + (new_size - old_size) // 4
    struct.pack_into('<I', result, 4, new_count)

    return result, (dest_reg, normal_reg, normal_comp)


def patch_dxbc_colortable_v2(dxbc_bytes: bytes):
    """Top-level: patch a DXBC blob and return (patched_dxbc, info) or (None, None)."""
    chunk_count = struct.unpack_from('<I', dxbc_bytes, 28)[0]
    shex_off = shex_size = None
    for i in range(chunk_count):
        off = struct.unpack_from('<I', dxbc_bytes, 32 + i * 4)[0]
        magic = dxbc_bytes[off:off + 4]
        size = struct.unpack_from('<I', dxbc_bytes, off + 4)[0]
        if magic in (b'SHEX', b'SHDR'):
            shex_off = off + 8
            shex_size = size
            break
    if shex_off is None:
        return None, None

    shex_data = bytearray(dxbc_bytes[shex_off:shex_off + shex_size])
    patched_shex, info = patch_shex_replace_emissive_v2(shex_data)
    if patched_shex is None:
        return None, None
    result = bytes(rebuild_dxbc(dxbc_bytes, bytes(patched_shex)))
    if not d3d_validate(result):
        return None, None
    return result, info


if __name__ == "__main__":
    # Simple CLI: patch a single DXBC file
    if len(sys.argv) < 2:
        print("Usage: dxbc_patch_colortable_v2.py <input.dxbc> [output.dxbc]")
        sys.exit(1)
    in_path = sys.argv[1]
    out_path = sys.argv[2] if len(sys.argv) > 2 else in_path.replace('.dxbc', '_CT_V2.dxbc')

    with open(in_path, 'rb') as f:
        original = f.read()
    patched, info = patch_dxbc_colortable_v2(original)
    if patched is None:
        print("Patch failed (no emissive init found or validation error)")
        sys.exit(1)
    dest_reg, normal_reg, normal_comp = info
    print(f"Patched: emissive=r{dest_reg}, normal=r{normal_reg}.{'xyzw'[normal_comp]}")
    print(f"Size: {len(original)} -> {len(patched)} (delta {len(patched) - len(original):+d})")
    with open(out_path, 'wb') as f:
        f.write(patched)
    print(f"Wrote: {out_path}")
