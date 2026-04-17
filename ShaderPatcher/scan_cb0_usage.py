"""Scan cb0 index usage across all 32 lighting PSes in vanilla skin.shpk.

Goal: verify cb0[18]/cb0[19] are safe to use for animation parameters
(i.e., no existing PS already reads these indices).
"""
import ctypes
import os
import re
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from dxbc_patcher import extract_ps_from_shpk

LIGHTING_PS_INDICES = [
    19, 28, 49, 58, 79, 88, 109, 118,
    139, 148, 169, 178, 199, 208, 229, 238,
    247, 256, 265, 274, 283, 292, 301, 310,
    319, 328, 337, 346, 355, 364, 373, 382,
]


def d3d_disassemble(dxbc_bytes):
    d3d = ctypes.CDLL('D3DCompiler_47.dll')
    blob_ptr = ctypes.c_void_p()
    hr = d3d.D3DDisassemble(dxbc_bytes, len(dxbc_bytes), 0, None, ctypes.byref(blob_ptr))
    if hr != 0 or not blob_ptr.value:
        raise RuntimeError(f"D3DDisassemble failed hr=0x{hr:X}")
    blob = ctypes.c_void_p(blob_ptr.value)
    get_buf_ptr = d3d.D3DGetBufferPointer if hasattr(d3d, 'D3DGetBufferPointer') else None
    if get_buf_ptr is None:
        class IUnknownVtbl(ctypes.Structure):
            _fields_ = [('QueryInterface', ctypes.c_void_p),
                        ('AddRef', ctypes.c_void_p),
                        ('Release', ctypes.c_void_p),
                        ('GetBufferPointer', ctypes.c_void_p),
                        ('GetBufferSize', ctypes.c_void_p)]
        vtbl_addr = struct.unpack('<Q', ctypes.string_at(blob.value, 8))[0]
        vtbl = ctypes.cast(vtbl_addr, ctypes.POINTER(IUnknownVtbl))[0]
        GetBufferPointerProto = ctypes.WINFUNCTYPE(ctypes.c_void_p, ctypes.c_void_p)
        GetBufferSizeProto = ctypes.WINFUNCTYPE(ctypes.c_size_t, ctypes.c_void_p)
        get_ptr = GetBufferPointerProto(vtbl.GetBufferPointer)
        get_size = GetBufferSizeProto(vtbl.GetBufferSize)
        ptr = get_ptr(blob)
        size = get_size(blob)
        text = ctypes.string_at(ptr, size).decode('utf-8', errors='replace')
    return text


def scan_cb0_indices(disasm_text):
    """Return set of cb0 element indices referenced in the shader code (not declarations)."""
    indices = set()
    # Skip the comment header block by starting at 'ps_5_0'
    code_start = disasm_text.find('\nps_')
    if code_start < 0:
        code_start = 0
    code = disasm_text[code_start:]
    for match in re.finditer(r'cb0\[(\d+)\]', code):
        indices.add(int(match.group(1)))
    return indices


def scan_cb_dcl(disasm_text):
    """Extract dcl_constantbuffer CB0[N] size."""
    m = re.search(r'dcl_constantbuffer CB0\[(\d+)\]', disasm_text)
    return int(m.group(1)) if m else -1


def main():
    script_dir = os.path.dirname(os.path.abspath(__file__))
    shpk_path = os.path.join(script_dir, "..", "..", "skin.shpk")
    if not os.path.exists(shpk_path):
        print(f"ERROR: {shpk_path} not found")
        sys.exit(1)

    print(f"Scanning cb0 usage in {len(LIGHTING_PS_INDICES)} lighting PSes\n")
    print(f"{'PS':>4} {'dcl':>5} {'maxRd':>6} {'uses 18/19?':>12}  indices")
    print("-" * 80)

    global_max = -1
    any_uses_new = False

    for ps_idx in LIGHTING_PS_INDICES:
        try:
            blob, _ = extract_ps_from_shpk(shpk_path, ps_idx)
            disasm = d3d_disassemble(blob)
            dcl_size = scan_cb_dcl(disasm)
            used = scan_cb0_indices(disasm)
            max_idx = max(used) if used else -1
            uses_new = (18 in used) or (19 in used)
            if max_idx > global_max:
                global_max = max_idx
            if uses_new:
                any_uses_new = True
            flag = "YES!" if uses_new else "no"
            sorted_idx = sorted(used)
            print(f"{ps_idx:>4} {dcl_size:>5} {max_idx:>6} {flag:>12}  {sorted_idx}")
        except Exception as e:
            print(f"{ps_idx:>4}  ERROR: {e}")

    print("-" * 80)
    print(f"Global max cb0 index used: {global_max}")
    print(f"Any PS uses cb0[18] or cb0[19]: {any_uses_new}")
    if not any_uses_new:
        print("\nOK: cb0[18] and cb0[19] are free for animation parameters.")
    else:
        print("\nWARN: must pick different cb0 indices.")


if __name__ == "__main__":
    main()
