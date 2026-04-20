"""Compile a tiny HLSL fragment containing our pulse modulation math
and extract the DXBC tokens to use as injection payload.
"""
import ctypes
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))  # parent ShaderPatcher/ for dxbc_patcher
from dxbc_patcher import parse_dxbc, parse_shex

HLSL = r"""
cbuffer g_PbrParameterCommon : register(b2) {
    float LoopTime;
    float3 _pad0;
    float4 _reserve[4];
};

Texture2D ColorTable : register(t10);
SamplerState samTable : register(s5);

struct VIn { float4 pos : SV_Position; float3 col : COLOR; float2 uv : TEXCOORD0; };

float4 main(VIn v) : SV_Target {
    // First sample: columns 8..11 (emissive + unused)
    float2 uvEm = float2(0.3125, v.uv.y * 0.9375 + 0.015625);
    float4 em = ColorTable.Sample(samTable, uvEm);
    // Second sample: columns 12..15 (speed, amp, reserved, reserved)
    float2 uvAn = float2(0.4375, v.uv.y * 0.9375 + 0.015625);
    float4 an = ColorTable.Sample(samTable, uvAn);
    float speed = an.x;
    float amp   = an.y;
    float k = 1.0 + amp * sin(speed * LoopTime * 6.283185);
    float3 emissive = em.xyz * k;
    return float4(emissive, 1.0);
}
"""


def compile_hlsl(src, entry="main", profile="ps_5_0"):
    d3d = ctypes.CDLL('D3DCompiler_47.dll')

    src_bytes = src.encode('ascii')
    code_ptr = ctypes.c_void_p()
    err_ptr = ctypes.c_void_p()

    hr = d3d.D3DCompile(
        src_bytes, len(src_bytes),
        None,  # source name
        None,  # defines
        None,  # includes
        entry.encode(), profile.encode(),
        0, 0,
        ctypes.byref(code_ptr),
        ctypes.byref(err_ptr),
    )

    if hr != 0:
        msg = ""
        if err_ptr.value:
            msg = _blob_to_str(d3d, err_ptr.value)
        raise RuntimeError(f"D3DCompile hr=0x{hr:X}: {msg}")

    return _blob_bytes(d3d, code_ptr.value)


def _blob_bytes(d3d, blob):
    class Vtbl(ctypes.Structure):
        _fields_ = [('QueryInterface', ctypes.c_void_p),
                    ('AddRef', ctypes.c_void_p),
                    ('Release', ctypes.c_void_p),
                    ('GetBufferPointer', ctypes.c_void_p),
                    ('GetBufferSize', ctypes.c_void_p)]
    vtbl_addr = struct.unpack('<Q', ctypes.string_at(blob, 8))[0]
    vtbl = ctypes.cast(vtbl_addr, ctypes.POINTER(Vtbl))[0]
    GetPtr = ctypes.WINFUNCTYPE(ctypes.c_void_p, ctypes.c_void_p)
    GetSize = ctypes.WINFUNCTYPE(ctypes.c_size_t, ctypes.c_void_p)
    ptr = GetPtr(vtbl.GetBufferPointer)(blob)
    size = GetSize(vtbl.GetBufferSize)(blob)
    return ctypes.string_at(ptr, size)


def _blob_to_str(d3d, blob):
    return _blob_bytes(d3d, blob).decode('utf-8', errors='replace')


def d3d_disassemble(dxbc_bytes):
    d3d = ctypes.CDLL('D3DCompiler_47.dll')
    blob_ptr = ctypes.c_void_p()
    hr = d3d.D3DDisassemble(dxbc_bytes, len(dxbc_bytes), 0, None, ctypes.byref(blob_ptr))
    if hr != 0 or not blob_ptr.value:
        raise RuntimeError(f"D3DDisassemble failed hr=0x{hr:X}")
    return _blob_to_str(d3d, blob_ptr.value)


dxbc = compile_hlsl(HLSL)
print(f"Compiled: {len(dxbc)} bytes\n")

dis = d3d_disassemble(dxbc)
print(dis[:3500])
print()
print("=" * 60)

container = parse_dxbc(dxbc)
shex = container.get_chunk(b'SHEX')
_, _, _, instructions = parse_shex(shex)
print(f"\nSHEX instructions ({len(instructions)} total):")
for i, inst in enumerate(instructions):
    hex_tokens = ' '.join(f'{t:08X}' for t in inst.tokens)
    print(f"  [{i}] op=0x{inst.opcode:02X} len={inst.length:2d} {inst.name:20s} | {hex_tokens}")
