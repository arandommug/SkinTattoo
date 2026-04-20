"""Extract arbitrary PS index from a .shpk and optionally disassemble via D3DCompiler.

Usage:
    python extract_ps.py 8 19 2             # extract PS[8], PS[19], PS[2] from default skin.shpk
    python extract_ps.py /path/shpk 8 19    # custom shpk + indices
"""
import os, sys, struct
from shpk_patcher import parse_shpk_full
from dxbc_patch_colortable import d3d_disassemble


def extract_ps_from_parsed(shpk: dict, ps_index: int):
    """Return (blob bytes, shader_entry) for PS[ps_index] from a parsed shpk dict."""
    vs_count = shpk['vs_count']
    ps_count = shpk['ps_count']
    if ps_index >= ps_count:
        raise IndexError(f"PS[{ps_index}] out of range (max {ps_count-1})")
    entry = shpk['shaders'][vs_count + ps_index]
    blob_start = entry['blob_off']
    blob = bytes(shpk['blob_section'][blob_start:blob_start + entry['blob_sz']])
    return blob, entry


def dump_resources(shader_entry, strings: bytes) -> str:
    """Summarize resource bindings as readable text."""
    lines = []
    type_order = [
        ('C', 'c_cnt', 'Constants'),
        ('S', 's_cnt', 'Samplers'),
        ('U', 'uav_cnt', 'UAVs'),
        ('T', 't_cnt', 'Textures'),
    ]
    idx = 0
    for tag, cnt_key, label in type_order:
        cnt = shader_entry[cnt_key]
        if cnt == 0:
            continue
        lines.append(f"  -- {label} ({cnt}) --")
        for _ in range(cnt):
            r = shader_entry['resources'][idx]
            idx += 1
            name = ""
            if r['str_off'] < len(strings):
                end = strings.find(b'\0', r['str_off'])
                if end >= 0:
                    name = strings[r['str_off']:end].decode('ascii', errors='replace')
            lines.append(
                f"    {tag}  slot={r['slot']:3d}  size={r['size']:3d}  "
                f"id=0x{r['id']:08X}  name=\"{name}\""
            )
    return "\n".join(lines)


def main():
    args = sys.argv[1:]
    if not args:
        print(__doc__)
        sys.exit(1)
    if args[0].endswith('.shpk'):
        shpk_path = args[0]
        indices = [int(x) for x in args[1:]]
    else:
        shpk_path = os.path.join(
            os.path.dirname(os.path.abspath(__file__)), "..", "..", "skin.shpk")
        indices = [int(x) for x in args]

    with open(shpk_path, "rb") as f:
        data = f.read()
    shpk = parse_shpk_full(data)
    strings = bytes(shpk['string_section'])

    out_dir = os.path.join(os.path.dirname(os.path.abspath(__file__)), "extracted_ps")
    os.makedirs(out_dir, exist_ok=True)

    for idx in indices:
        blob, entry = extract_ps_from_parsed(shpk, idx)
        dxbc_path = os.path.join(out_dir, f"ps_{idx:03d}.dxbc")
        disasm_path = os.path.join(out_dir, f"ps_{idx:03d}_disasm.txt")
        with open(dxbc_path, "wb") as fo:
            fo.write(blob)
        try:
            text = d3d_disassemble(blob)
            res_summary = dump_resources(entry, strings)
            header = (
                f"// PS[{idx}]  blob_off=0x{entry['blob_off']:X}  "
                f"blob_sz={entry['blob_sz']}\n"
                f"{res_summary}\n"
                f"// ---- DXBC disassembly ----\n"
            )
            with open(disasm_path, "w", encoding="utf-8") as fo:
                fo.write(header + text)
            print(f"  PS[{idx}]: {entry['blob_sz']} bytes -> {dxbc_path}")
            print(f"           disasm -> {disasm_path}")
        except Exception as e:
            print(f"  PS[{idx}]: {entry['blob_sz']} bytes -> {dxbc_path} (disasm failed: {e})")


if __name__ == "__main__":
    main()
