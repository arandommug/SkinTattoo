"""Verify CB0 dcl is extended in the patched skin_ct.shpk produced by the plugin.

Extracts each of the 32 lighting PSes, disassembles, and reports the CB0[N] dcl size.
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
    return ctypes.string_at(ptr, size).decode('utf-8', errors='replace')


def main():
    patched = os.path.expandvars(r"%APPDATA%\XIVLauncherCN\pluginConfigs\SkinTattoo\preview\skin_ct.shpk")
    if not os.path.exists(patched):
        print(f"ERROR: {patched} not found")
        sys.exit(1)

    print(f"Verifying: {patched}\n")
    print(f"{'PS':>4}  {'CB0[N]':>8}  {'t10':>5}  {'s5':>4}")
    print("-" * 40)

    cb0_ok = 0
    t10_s5_ok = 0
    for ps_idx in LIGHTING_PS_INDICES:
        try:
            blob, _ = extract_ps_from_shpk(patched, ps_idx)
            disasm = d3d_disassemble(blob)
            m = re.search(r'dcl_constantbuffer CB0\[(\d+)\]', disasm)
            cb0_size = int(m.group(1)) if m else -1
            has_t10 = 'dcl_resource_texture2d (float,float,float,float) t10' in disasm
            has_s5 = 'dcl_sampler s5' in disasm
            if cb0_size >= 20:
                cb0_ok += 1
            if has_t10 and has_s5:
                t10_s5_ok += 1
            print(f"{ps_idx:>4}  {cb0_size:>8}  {'Y' if has_t10 else 'n':>5}  {'Y' if has_s5 else 'n':>4}")
        except Exception as e:
            print(f"{ps_idx:>4}  ERROR: {e}")

    print("-" * 40)
    print(f"CB0 >= 20: {cb0_ok}/{len(LIGHTING_PS_INDICES)}")
    print(f"Emissive patched (t10+s5): {t10_s5_ok}/{len(LIGHTING_PS_INDICES)}")


if __name__ == "__main__":
    main()
