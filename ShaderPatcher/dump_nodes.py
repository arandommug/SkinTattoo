"""Enumerate all Nodes in a .shpk and group pass[2] PS by SkinType.

Extends parse_shpk.py: reads Nodes[] and NodeAliases[] after the Keys section,
then for each MaterialKey value (SkinType) lists the unique pass[2] PSes that
end up being invoked for subView=6 (the main deferred lighting subview).

Usage:
    python dump_nodes.py [path_to_skin.shpk]
"""
import struct, sys, os, collections

CRC_NAMES = {
    0x380CAED0: "CategorySkinType", 0x72E697CD: "ValEmissive",
    0x2BDB45F1: "ValBody",           0xF5673524: "ValFace",
    0x57FF3B64: "ValBodyJJM",        0x6E5B8F10: "ValHrothgar",
    0xD2777173: "CategoryDecalMode", 0x4242B842: "ValDecalOff",
    0x584265DD: "ValDecalEmissive",  0xF52CCF05: "CategoryVertexColorMode",
    0xDFE74BAC: "ValVertexColorOff", 0xA7D2FF60: "ValVertexColorEmissive",
    0x2005679F: "g_SamplerTable",    0x38A64362: "g_EmissiveColor",
}

def cn(crc):
    return CRC_NAMES.get(crc, f"0x{crc:08X}")


def parse_shpk_nodes(path):
    with open(path, "rb") as f:
        data = f.read()
    pos = 0

    def u16():
        nonlocal pos; v = struct.unpack_from("<H", data, pos)[0]; pos += 2; return v
    def u32():
        nonlocal pos; v = struct.unpack_from("<I", data, pos)[0]; pos += 4; return v
    def skip(n):
        nonlocal pos; pos += n
    def read_bytes(n):
        nonlocal pos; v = data[pos:pos+n]; pos += n; return v

    # Header
    magic = u32()
    assert magic == 0x6B506853, f"not a ShPk file: magic=0x{magic:08X}"
    version = u32()
    dx      = u32()
    fsz     = u32()
    assert fsz == len(data), f"size mismatch {fsz} vs {len(data)}"
    blobs_off    = u32()
    strings_off  = u32()
    vs_count     = u32()
    ps_count     = u32()
    mat_par_size = u32()
    mat_par_cnt  = u16()
    has_defaults = u16() != 0
    const_cnt    = u32()
    samp_cnt     = u16()
    tex_cnt      = u16()
    uav_cnt      = u32()
    sys_key_cnt  = u32()
    scn_key_cnt  = u32()
    mat_key_cnt  = u32()
    node_cnt     = u32()
    alias_cnt    = u32()
    if version >= 0x0D01:
        skip(12)  # 3 reserved u32

    print(f"== {os.path.basename(path)} ==")
    print(f"  version=0x{version:04X} dx={dx:#x} filesize={fsz}")
    print(f"  VS={vs_count}  PS={ps_count}  nodes={node_cnt}  aliases={alias_cnt}")
    print(f"  sysKeys={sys_key_cnt}  sceneKeys={scn_key_cnt}  matKeys={mat_key_cnt}")

    # Skip shader entries (VS + PS): each has per-shader header + resources
    for _ in range(vs_count + ps_count):
        skip(4 + 4)      # blob off+sz
        c_cnt  = u16()
        s_cnt  = u16()
        u_cnt  = u16()
        t_cnt  = u16()
        if version >= 0x0D01:
            skip(4)
        # Resource = 16 bytes each
        skip((c_cnt + s_cnt + u_cnt + t_cnt) * 16)

    # MaterialParams
    skip(mat_par_cnt * 8)
    if has_defaults:
        skip(mat_par_size)

    # Resources: each is 16 bytes (constants, samplers, textures, uavs)
    skip((const_cnt + samp_cnt + tex_cnt + uav_cnt) * 16)

    # Keys
    system_keys = [(u32(), u32()) for _ in range(sys_key_cnt)]
    scene_keys  = [(u32(), u32()) for _ in range(scn_key_cnt)]
    mat_keys    = [(u32(), u32()) for _ in range(mat_key_cnt)]
    # SubViewKey defaults (always 2 SubViewKeys)
    sv1_def = u32(); sv2_def = u32()
    subview_keys = [(1, sv1_def), (2, sv2_def)]

    print(f"  SystemKeys:   {[f'{cn(k)}=def:{cn(v)}' for k,v in system_keys]}")
    print(f"  SceneKeys:    {[cn(k) for k,_ in scene_keys]} ({scn_key_cnt} keys)")
    print(f"  MaterialKeys: {[f'{cn(k)}=def:{cn(v)}' for k,v in mat_keys]}")
    print(f"  SubViewKeys:  defaults 0x{sv1_def:08X}, 0x{sv2_def:08X}")

    # Find the index of SkinType within MaterialKeys
    skin_type_idx = next((i for i,(k,_) in enumerate(mat_keys) if k == 0x380CAED0), -1)
    print(f"  SkinType at MaterialKey index {skin_type_idx}")

    # Read Nodes
    nodes = []
    for ni in range(node_cnt):
        selector = u32()
        pass_cnt = u32()
        pass_indices = list(read_bytes(16))
        if version >= 0x0D01:
            skip(8)   # unk131Keys (2 × u32)
        sys_vals  = [u32() for _ in range(sys_key_cnt)]
        scn_vals  = [u32() for _ in range(scn_key_cnt)]
        mat_vals  = [u32() for _ in range(mat_key_cnt)]
        sv_vals   = [u32() for _ in range(2)]   # SubViewKeys fixed=2
        passes = []
        for _ in range(pass_cnt):
            pid = u32(); vs = u32(); ps = u32()
            if version >= 0x0D01:
                skip(12)
            passes.append((pid, vs, ps))
        nodes.append(dict(
            selector=selector, pass_indices=pass_indices,
            sys_vals=sys_vals, scn_vals=scn_vals,
            mat_vals=mat_vals, sv_vals=sv_vals,
            passes=passes,
        ))

    # Node Aliases (selector -> node index)
    aliases = [(u32(), u32()) for _ in range(alias_cnt)]

    print(f"\n  Parsed {len(nodes)} nodes, {len(aliases)} aliases")
    return dict(version=version, nodes=nodes, aliases=aliases,
                sys_keys=system_keys, scene_keys=scene_keys,
                mat_keys=mat_keys, skin_type_idx=skin_type_idx)


def group_by_skintype(parsed):
    """Cluster nodes by SkinType value and summarize pass slot distribution."""
    sk_idx = parsed['skin_type_idx']
    assert sk_idx >= 0, "SkinType not found in MaterialKeys"
    buckets = collections.defaultdict(list)
    for n in parsed['nodes']:
        sk = n['mat_vals'][sk_idx]
        buckets[sk].append(n)

    print(f"\n== Per-SkinType summary ==")
    for sk, nds in sorted(buckets.items()):
        print(f"\n[SkinType = {cn(sk)} / 0x{sk:08X}]  nodes={len(nds)}")
        # For each pass slot (0..15 in PassIndices -> which Passes[] entry),
        # collect the distinct (VS, PS) pairs
        slot_to_passes = collections.defaultdict(set)
        ps_in_node = collections.defaultdict(int)
        for n in nds:
            for sub_view, pass_slot in enumerate(n['pass_indices']):
                if pass_slot == 0xFF:
                    continue
                if pass_slot < len(n['passes']):
                    _, vs, ps = n['passes'][pass_slot]
                    slot_to_passes[pass_slot].add((vs, ps))
            for _, vs, ps in n['passes']:
                ps_in_node[ps] += 1
        for slot in sorted(slot_to_passes):
            pairs = sorted(slot_to_passes[slot])
            ps_list = sorted({ps for _, ps in pairs})
            vs_list = sorted({vs for vs, _ in pairs})
            print(f"   pass[{slot}]: VS={vs_list}  PS={ps_list}  (unique pairs={len(pairs)})")
        all_ps = sorted(ps_in_node)
        print(f"   all PS reachable: {all_ps}")


def find_pass2_lighting_ps(parsed):
    """pass[2] is typically the main deferred lighting PS (SubView=6 → PassIndices[6]=2).
    Dump the PS list per SkinType for pass index 2 specifically."""
    sk_idx = parsed['skin_type_idx']
    print("\n== pass[2] LIGHTING PS by SkinType ==")
    sk_buckets = collections.defaultdict(set)
    for n in parsed['nodes']:
        sk = n['mat_vals'][sk_idx]
        if 2 < len(n['passes']):
            _, vs, ps = n['passes'][2]
            sk_buckets[sk].add(ps)
    for sk, ps_set in sorted(sk_buckets.items()):
        print(f"  {cn(sk):20s}  PS = {sorted(ps_set)}")


if __name__ == "__main__":
    default_path = os.path.join(
        os.path.dirname(os.path.abspath(__file__)),
        "..", "..", "skin.shpk",
    )
    path = sys.argv[1] if len(sys.argv) > 1 else default_path
    parsed = parse_shpk_nodes(path)
    group_by_skintype(parsed)
    find_pass2_lighting_ps(parsed)
