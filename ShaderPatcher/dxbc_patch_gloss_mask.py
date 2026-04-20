"""Patch skin.shpk Emissive pass[2] PS (PS[19] and its 31 SceneKey siblings):
inject Body's gloss mask multiplication so switching a body mtrl into Emissive
does not lose the `normal.alpha * vertex.alpha` gloss contribution.

See docs/skin-shpk-deep-dive/05-seam-and-fix-paths.md §5.3 Path A.

Pattern (robust to register-allocator variations across 32 sibling PSes):

    # "Emissive init" — two consecutive MUL instructions:
    mul  rX.xyz, cb0[3].xyzx, cb0[3].xyzx        # emissive²  (always 9 tokens)
    mul  rX.xyz, rS.<swiz>, rX.xyzx              # rX *= rS.? (rS holds normal.alpha)
                                                 # swizzle is zzzz or wwww depending on PS

    # Later (may be several instructions away due to SceneKey diffs):
    mul  rC.?, ?, cb0[9].x                        # rC.? *= g_TileAlpha
    mul[_sat] rC.?, rC.?, v1.w                    # rC.? = [sat](rC.? × vertex.alpha)
                                                  # ← THIS is where we insert before

We insert:
    mul rC.?, rC.?, rS.?       # restore body's normal.alpha gloss contribution
"""
import struct
import os
import sys

from dxbc_patch_colortable import rebuild_dxbc, d3d_disassemble, d3d_validate


# ── DXBC operand token encoding helpers ──

def operand_temp_mask(comp_mask: int) -> int:
    """Encode dest-style operand: TEMP reg with write-mask.

    comp_mask: 4-bit mask (bit 0 = .x, bit 1 = .y, bit 2 = .z, bit 3 = .w).
    Returns 32-bit operand token (needs register index as next token).
    """
    # bits 0-1 = 2 (4-comp), 2-3 = 0 (mask mode), 4-7 = comp_mask,
    # 12-19 = 0 (TEMP), 20-21 = 1 (1D index)
    return 0x00100002 | (comp_mask << 4)


def operand_temp_select1(component: int) -> int:
    """Encode src-style operand: TEMP reg with single-component select.

    component: 0=x, 1=y, 2=z, 3=w.
    """
    # bits 0-1 = 2, 2-3 = 2 (select_1), 4-5 = component,
    # 12-19 = 0 (TEMP), 20-21 = 1
    return 0x0010000A | (component << 4)


def build_mul_single(dest_reg: int, dest_comp: int,
                     src0_reg: int, src0_comp: int,
                     src1_reg: int, src1_comp: int) -> bytes:
    """Build a 7-token `mul rD.c, rS0.c, rS1.c` instruction (no sat)."""
    dest_mask = 1 << dest_comp
    tokens = [
        0x07000038,                        # MUL opcode, length 7
        operand_temp_mask(dest_mask),      # dest operand
        dest_reg,
        operand_temp_select1(src0_comp),   # src0
        src0_reg,
        operand_temp_select1(src1_comp),   # src1
        src1_reg,
    ]
    return struct.pack('<7I', *tokens)


# ── SHEX scanning ──

def iter_shex_instructions(shex_data: bytes):
    """Yield (pos, opcode_token, length_bytes) for each instruction."""
    pos = 8  # skip version + token count header
    n = len(shex_data)
    while pos < n:
        tok = struct.unpack_from('<I', shex_data, pos)[0]
        length = (tok >> 24) & 0x7F
        if length == 0:
            length = 1
        byte_len = length * 4
        if pos + byte_len > n:
            break
        yield pos, tok, byte_len
        pos += byte_len


def decode_operand_src_swizzle(shex_data: bytes, pos: int):
    """Decode a src operand starting at `pos`. Returns (reg, component_list, operand_type, bytes_consumed).
    Handles simple operand forms only (TEMP / INPUT / CONSTBUFFER with 1D/2D index).
    Returns None if complex/unhandled.
    """
    tok = struct.unpack_from('<I', shex_data, pos)[0]
    num_comp = tok & 0x3
    sel_mode = (tok >> 2) & 0x3
    op_type = (tok >> 12) & 0xFF
    idx_dim = (tok >> 20) & 0x3
    if op_type not in (0, 1, 8):  # TEMP, INPUT, CONSTBUFFER
        return None
    # Extended operand present?
    if (tok >> 31) & 1:
        return None
    consumed = 4
    # index dims — for TEMP/INPUT typically 1D
    if idx_dim == 1:
        reg = struct.unpack_from('<I', shex_data, pos + consumed)[0]
        consumed += 4
        extra_idx = None
    elif idx_dim == 2:
        reg = struct.unpack_from('<I', shex_data, pos + consumed)[0]
        consumed += 4
        extra_idx = struct.unpack_from('<I', shex_data, pos + consumed)[0]
        consumed += 4
    else:
        return None

    # Component list
    if sel_mode == 0:  # mask mode (rare in src)
        mask = (tok >> 4) & 0xF
        comps = [i for i in range(4) if mask & (1 << i)]
    elif sel_mode == 1:  # swizzle mode
        swiz = (tok >> 4) & 0xFF
        comps = [(swiz >> (2 * i)) & 3 for i in range(4)]
    elif sel_mode == 2:  # select_1
        comp = (tok >> 4) & 0x3
        comps = [comp]
    else:
        return None
    return (op_type, reg, comps, extra_idx, consumed)


# ── Pattern recognition ──

def find_emissive_init_pair(shex_data: bytes):
    """Locate the `mul rX.xyz, cb0[3], cb0[3]` + `mul rX.xyz, rS.<swiz>, rX.xyzx` pair.

    Returns (pos_of_second_mul, rS_register, rS_component) or None.
    """
    # Signature of `mul rX.xyz, cb0[3].xyzx, cb0[3].xyzx` (9 tokens):
    #   token0: 0x09000038 (MUL len 9, no SAT)
    #   token1-2: dest rX.xyz  (operand, reg)
    #   token3-5: src0 cb0[3].xyzx  (operand, cb reg, cb idx)
    #   token6-8: src1 cb0[3].xyzx
    # We match the tail (tokens 3-8) which are invariant across PSes.
    cb3_tail = struct.pack('<6I',
        0x00208246, 0x00000000, 0x00000003,
        0x00208246, 0x00000000, 0x00000003)

    for pos, tok, blen in iter_shex_instructions(shex_data):
        opcode = tok & 0x7FF
        if opcode != 0x38 or blen != 9 * 4:
            continue
        if shex_data[pos + 12:pos + 12 + 24] != cb3_tail:
            continue
        # Match — this is `mul rX.xyz, cb0[3], cb0[3]`
        # Now parse the IMMEDIATELY following instruction
        next_pos = pos + blen
        if next_pos >= len(shex_data):
            continue
        tok2 = struct.unpack_from('<I', shex_data, next_pos)[0]
        if (tok2 & 0x7FF) != 0x38:
            continue
        len2 = ((tok2 >> 24) & 0x7F) * 4
        # Expect 7 tokens: `mul rX.xyz, rS.<swiz>, rX.xyzx`
        if len2 != 7 * 4:
            continue
        # src0 starts at next_pos + 12 (after opcode + dest operand + dest reg)
        src0 = decode_operand_src_swizzle(shex_data, next_pos + 12)
        if src0 is None:
            continue
        op_type, reg, comps, _, _ = src0
        if op_type != 0:  # must be TEMP
            continue
        # The swizzle is typically .zzzz or .wwww — component is comps[0]
        return (next_pos, reg, comps[0])
    return None


def find_vertex_alpha_mul(shex_data: bytes, start_pos: int):
    """From start_pos onwards, find the `mul[_sat] rC.?, rC.?, v1.w` instruction.

    Returns (pos, rC_register, rC_component) or None.
    v1.w signature: operand_token has op_type=1 (INPUT), select_1 .w (bits 4-5 = 3), reg index 1.
    """
    for pos, tok, blen in iter_shex_instructions(shex_data[start_pos:]):
        real_pos = pos + start_pos
        opcode = tok & 0x7FF
        if opcode != 0x38:
            continue
        if blen != 7 * 4:
            continue
        # src1 at offset 20 (after opcode + dest 2 + src0 2)
        src1_op_tok = struct.unpack_from('<I', shex_data, real_pos + 20)[0]
        src1_reg = struct.unpack_from('<I', shex_data, real_pos + 24)[0]
        op_type = (src1_op_tok >> 12) & 0xFF
        sel_mode = (src1_op_tok >> 2) & 0x3
        comp = (src1_op_tok >> 4) & 0x3
        # v1.w check: INPUT, select_1, component .w (=3), reg 1
        if op_type != 1 or sel_mode != 2 or comp != 3 or src1_reg != 1:
            continue
        # Dest = src0 check (both TEMP, same reg, same component)
        dest_op = struct.unpack_from('<I', shex_data, real_pos + 4)[0]
        dest_reg = struct.unpack_from('<I', shex_data, real_pos + 8)[0]
        src0_op = struct.unpack_from('<I', shex_data, real_pos + 12)[0]
        src0_reg = struct.unpack_from('<I', shex_data, real_pos + 16)[0]
        # Dest fields
        dest_num_comp = dest_op & 0x3
        dest_sel_mode = (dest_op >> 2) & 0x3
        dest_op_type = (dest_op >> 12) & 0xFF
        if dest_num_comp != 2 or dest_sel_mode != 0 or dest_op_type != 0:
            continue  # dest must be a TEMP mask-mode operand
        dest_mask = (dest_op >> 4) & 0xF
        if dest_mask not in (1, 2, 4, 8):
            continue
        dest_comp = {1: 0, 2: 1, 4: 2, 8: 3}[dest_mask]
        # src0 should be same register with select_1 on same component
        src0_num_comp = src0_op & 0x3
        src0_sel_mode = (src0_op >> 2) & 0x3
        src0_op_type = (src0_op >> 12) & 0xFF
        src0_comp = (src0_op >> 4) & 0x3
        if (src0_num_comp != 2 or src0_sel_mode != 2 or src0_op_type != 0
                or src0_comp != dest_comp or src0_reg != dest_reg):
            continue
        return (real_pos, dest_reg, dest_comp)
    return None


# ── Patch function ──

def patch_shex_insert_gloss_mask(shex_data: bytearray) -> bytearray:
    """Insert gloss-mask mul just before the `mul[_sat] rC.?, rC.?, v1.w` instruction."""
    emissive = find_emissive_init_pair(shex_data)
    if emissive is None:
        raise ValueError("gloss_mask: could not locate emissive init (mul rX.xyz, cb0[3]*cb0[3]).")
    emissive_pos, normal_reg, normal_comp = emissive

    # Start searching for vertex-alpha mul right after the emissive init pair
    # (i.e. after the 7-token second mul that reads rS)
    search_from = emissive_pos + 7 * 4

    target = find_vertex_alpha_mul(shex_data, search_from)
    if target is None:
        raise ValueError("gloss_mask: could not locate `mul[_sat] rC.?, rC.?, v1.w` pattern.")
    target_pos, dest_reg, dest_comp = target

    # Build new instruction: mul rC.<comp>, rC.<comp>, rS.<normal_comp>
    new_inst = build_mul_single(
        dest_reg, dest_comp,
        dest_reg, dest_comp,
        normal_reg, normal_comp,
    )

    print(f"  emissive_init_second@0x{emissive_pos:X}  normal=r{normal_reg}.{'xyzw'[normal_comp]}")
    print(f"  v_alpha_mul@0x{target_pos:X}  accumulator=r{dest_reg}.{'xyzw'[dest_comp]}")
    print(f"  inserting: mul r{dest_reg}.{'xyzw'[dest_comp]}, r{dest_reg}.{'xyzw'[dest_comp]}, r{normal_reg}.{'xyzw'[normal_comp]}")

    result = bytearray(shex_data[:target_pos])
    result.extend(new_inst)
    result.extend(shex_data[target_pos:])

    # Update token count (offset 4 of SHEX)
    old_count = struct.unpack_from('<I', result, 4)[0]
    new_count = old_count + 7
    struct.pack_into('<I', result, 4, new_count)
    return result


def patch_dxbc_gloss_mask(dxbc_bytes: bytes) -> bytes:
    """Patch a full DXBC blob and return validated output."""
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
        raise ValueError("No SHEX/SHDR chunk in DXBC")

    shex_data = bytearray(dxbc_bytes[shex_off:shex_off + shex_size])
    patched_shex = patch_shex_insert_gloss_mask(shex_data)
    result = bytes(rebuild_dxbc(dxbc_bytes, bytes(patched_shex)))

    if not d3d_validate(result):
        raise ValueError("gloss_mask: patched DXBC failed D3DCompiler validation")

    return result


# ── CLI ──

def main():
    script_dir = os.path.dirname(os.path.abspath(__file__))
    in_path = os.path.join(script_dir, "extracted_ps", "ps_019.dxbc")
    out_path = os.path.join(script_dir, "extracted_ps", "ps_019_GLOSSMASK.dxbc")
    disasm_path = os.path.join(script_dir, "extracted_ps", "ps_019_GLOSSMASK_disasm.txt")

    if len(sys.argv) > 1:
        in_path = sys.argv[1]
    if len(sys.argv) > 2:
        out_path = sys.argv[2]

    with open(in_path, 'rb') as f:
        original = f.read()
    print(f"Input DXBC: {len(original)} bytes  ({in_path})")

    patched = patch_dxbc_gloss_mask(original)
    print(f"Output DXBC: {len(patched)} bytes  (delta {len(patched) - len(original):+d})")

    with open(out_path, 'wb') as f:
        f.write(patched)
    print(f"Written: {out_path}")

    text = d3d_disassemble(patched)
    with open(disasm_path, 'w', encoding='utf-8') as f:
        f.write("// gloss-mask-patched\n")
        f.write(text)
    print(f"Disasm: {disasm_path}")


if __name__ == "__main__":
    main()
