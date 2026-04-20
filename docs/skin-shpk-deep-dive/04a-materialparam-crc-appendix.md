# Ch4 附录 -- MaterialParam CRC 认领清单

> 目的：把 Ch4 Sec.4.2 里 skin.shpk 的 60 项 MaterialParam 逐一认名。初始状态下有 33 个 CRC 未识别，本附录把其中 **17 个** 通过交叉比对第三方工具（Meddle、ALum、xivmodding）认领成功，**16 个** 暂无可靠命名但给出默认值与用途线索。
> 方法：
> 1. 先本地用 FFXIV 专用 CRC（多项式 `0x04C11DB7`、输入 bit-reflect、输出 bit-reflect32）对候选名字批量算哈希（见 Sec.4a.4 Python 代码）
> 2. 再去 `Meddle/Meddle.Utils/Constants/Names.cs` 做已知 CRC->名字反查（社区已认领的在这里）
> 3. IDA 里搜 `ffxiv_dx11.exe` 的 `g_*` 字符串表，把所有出现在 binary 里的 shader 变量名喂进哈希
> 4. 剩余的记录默认值与形状提示，留给未来研究

## 4a.1 FFXIV Shader CRC 算法

**与 Lumina 文件路径 CRC 不同**（Lumina 用的是 `~zlib.crc32`）。Shader 常量名用的是 **"Square Enix" 风格的 CRC32**：

```
多项式：0x04C11DB7   （IEEE 802.3 / Ethernet CRC-32）
初始值：0x00000000
输入反射：字节级逐位反转（reflect8）
输出反射：完整 32 位反转（reflect32）
末尾不做 XOR
```

取证：
- 实现在 `Meddle/Meddle/Meddle.Utils/Export/ShaderPackage.cs:97-178`
- 验证命中：`g_EmissiveColor` -> `0x38A64362` [x]、`g_DiffuseColor` -> `0x2C2A34DD` [x]、`g_SamplerTable` -> `0x2005679F` [x]、`g_NormalScale` -> `0xB5545FBB` [x]
- 注意这个 CRC **不同于** `CategorySkinType`（0x380CAED0）这类 shader key----shader key 用的可能是另一种哈希 / 约定或直接手填（Penumbra 代码里的 key `0x380CAED0` 与字符串 `CategorySkinType` 的 SE-CRC 哈希 `0x7B7F12CD` 对不上，说明不是同一族）。

## 4a.2 本附录命中的 17 个 CRC

| CRC | 名字 | 默认值 | 说明（来自 Meddle Names.cs 注释 + PS 反汇编推断） |
|---|---|---|---|
| `0x29AC0223` | **g_AlphaThreshold** | `0.0` | Alpha 测试阈值（Dawntrail 新增，替代旧的 `g_AlphaAperture`？） |
| `0x11C90091` | **g_WhiteEyeColor** | `(1, 1, 1)` | 眼白色（RGB）。skin.shpk 共享这个常量是因为眼球同属皮肤系统 |
| `0xD925FF32` | **g_ShadowAlphaThreshold** | `0.5` | 阴影 alpha 测试阈值（shadow pass 用） |
| `0x50E36D56` | **g_IrisRingColor** | `(1, 1, 1)` | 虹膜外圈色（iris ring/limbal ring），RGB |
| `0x58DE06E2` | **g_IrisRingForceColor** | `(0, 0, 0)` | 虹膜外圈"强制色"，当非零时覆盖 g_IrisRingColor |
| `0x7DABA471` | **g_IrisRingEmissiveIntensity** | `0.25` | 虹膜外圈发光强度（我们已识别用于 iris 发光路径） |
| `0x285F72D2` | **g_IrisRingOddRate** | `~=0` (1e-45) | 虹膜外圈"异色"比率（奇数眼/异瞳用？） |
| `0x37DEA328` | **g_IrisUvRadius** | `0.2` | 虹膜 UV 半径 |
| `0xE18398AE` | **g_IrisRingUvRadius** | `(0.158, 0.174)` | 虹膜外圈 UV 半径（内/外环，两个 float） |
| `0x5B608CFE` | **g_IrisRingUvFadeWidth** | `(0.04, 0.02)` | 虹膜外圈边界羽化宽度 |
| `0x66C93D3E` | **g_IrisThickness** | `0.5` | 虹膜厚度（用于视差/折射？） |
| `0x29253809` | **g_IrisOptionColorRate** | `0` | 虹膜可选色混合率 |
| `0x8EA14846` | **g_IrisOptionColorEmissiveRate** | `0` | 虹膜可选色发光混合率 |
| `0x7918D232` | **g_IrisOptionColorEmissiveIntensity** | `1` | 虹膜可选色发光强度 |
| `0xD26FF0AE` | **g_VertexMovementMaxLength** | `1.0` | 顶点位移最大长度（vertex shape deform 上限？） |
| `0x641E0F22` | **g_VertexMovementScale** | `1.0` | 顶点位移缩放 |
| `0x00A680BC` | **g_ToonSpecIndex** | `~=0` (4e-45) | 卡通高光索引（对 `g_SamplerCharaToon` 查表？） |

**有 11 个 iris/眼球相关 CRC 藏在 skin.shpk 的 MaterialParam 里** ---- 这验证了 skin.shpk 和 iris.shpk 共享材质常量布局，`chara/human/.../_iri_a.mtrl` 材质虽然声明挂 `iris.shpk`，但常量布局与 skin.shpk 完全一样（见 Ch2 log 里 `iris.shpk shpk=0x178D77B2500 ... matKeys=3`）。

## 4a.3 剩余 16 个未识别 CRC（按默认值线索分类）

这些 16 个 CRC 在 Meddle Names.cs 里也只留下 `// MaterialParam Unknown: ...` 的注释，社区尚未认出。以下按默认值给出用途推测：

### 角度对（-45/45）可能是同一对特性的 min/max

| CRC | 默认值 | 推测用途 |
|---|---|---|
| `0x2B5EB116` | `-45` | 角度下界（视角？高光？Hrothgar 胡须？） |
| `0x5C598180` | `45` | 对应的角度上界 |

**线索**：同一数值绝对值、符号相反 -> 99% 是一对。并且位于 MaterialParam offset 248/252（紧邻 `g_ToonIndex=240`），猜测是 toon 相关角度阈值 -- 比如 `g_ToonAngleMin` / `g_ToonAngleMax` 或 `g_ToonShadowStart` / `g_ToonShadowEnd`。

### 0/1 开关类（32 bit float 值 0 或 1.0）

| CRC | 默认值 | offset | 推测用途 |
|---|---|---|---|
| `0x15B70E35` | `0` | 148 | 某个开关/强度，紧邻 `g_TileAlpha` (144) |
| `0x4172EDCC` | `1` | 284 | 开关（toon 相关区域） |
| `0x43345395` | `1` | 280 | 开关 |
| `0x6C159E95` | `0.85` | 264 | **非整数 0.85 -- 罕见，可能是"反射衰减基线"** |
| `0x71CC9A45` | `0` | 292 | 开关（toon 相关区域） |
| `0x738A241C` | `0` | 288 | 开关（toon 相关区域） |
| `0xD87BBC76` | `1` | 300 | 开关 |
| `0xDA3D022F` | `1` | 296 | 开关 |

### 全零初始值（整体不激活）

| CRC | 默认值 | offset | 推测用途 |
|---|---|---|---|
| `0xAD94E254` | `0` | 212 | 紧邻 `g_AlphaAperture` (204) / `g_AlphaOffset` (208) |
| `0xAE4F649C` | `0` | 216 | 同上邻居 |
| `0xB61D7498` | `0` | 232 | 紧邻 `g_TextureMipBias` (228) |
| `0xEA8375A6` | `0` | 304 | toon 区域尾端 |
| `0xE8C5CBFF` | `0` | 308 | toon 区域尾端 |

### 8 字节 (2f) 组合

| CRC | 默认值 | offset | 推测 |
|---|---|---|---|
| `0x1A60F60E` | `(0, 0)` | 136 | 紧邻 `g_TileScale` (112) 与 `g_TileAlpha` (144)；可能是 tile UV 偏移 / 旋转 |

**紧邻 offset 关系**帮助我们按"功能区块"理解这些未命名常量：
- **100-148 bytes**：Tile 系统相关（已知 `g_TileIndex` (108), `g_TileScale` (112)；未知 `0x1A60F60E` (136), `0x15B70E35` (148)）
- **200-232 bytes**：Alpha/glass/mip 区域（已知 `g_AlphaAperture` (204), `g_AlphaOffset` (208), `g_GlassIOR` (220), `g_GlassThicknessMax` (224), `g_TextureMipBias` (228)；未知 `0xAD94E254` (212), `0xAE4F649C` (216), `0xB61D7498` (232)）
- **240-308 bytes**：Toon 系统扩展（已知 `g_ToonIndex` (240), `g_ToonLightScale` (256), `g_ToonLightSpecAperture` (260), `g_ToonReflectionScale` (268), `g_ShadowPosOffset` (272), `g_TileMipBiasOffset` (276)；未知 `0x2B5EB116` (248, -45), `0x5C598180` (252, +45), `0x6C159E95` (264, 0.85), `0x43345395` (280), `0x4172EDCC` (284), `0x738A241C` (288), `0x71CC9A45` (292), `0xDA3D022F` (296), `0xD87BBC76` (300), `0xEA8375A6` (304), `0xE8C5CBFF` (308)）

尤其 240-308 这一段几乎全是 toon 系统扩展。看起来是 Dawntrail 加入 toon 二代渲染时新增的参数。未来可以通过**扫描使用 `toon.shpk` 的材质默认值**来推断。

## 4a.4 参考实现（Python 版 SE Shader CRC）

```python
def make_crc_table():
    poly = 0x04C11DB7
    table = [0] * 256
    for i in range(256):
        cur = i << 24
        for _ in range(8):
            cur = ((cur << 1) ^ poly) & 0xFFFFFFFF if cur & 0x80000000 else (cur << 1) & 0xFFFFFFFF
        table[i] = cur
    return table

def reflect8(v):
    r = 0
    for i in range(8):
        if v & (1 << i): r |= (1 << (7 - i))
    return r

def reflect32(v):
    r = 0
    for i in range(32):
        if v & (1 << i): r |= (1 << (31 - i))
    return r

TABLE = make_crc_table()

def se_shader_crc(s: str) -> int:
    crc = 0
    for b in s.encode('utf-8'):
        crc ^= reflect8(b) << 24
        crc = ((crc << 8) & 0xFFFFFFFF) ^ TABLE[crc >> 24]
    return reflect32(crc)

# 验证
assert se_shader_crc('g_EmissiveColor') == 0x38A64362
assert se_shader_crc('g_DiffuseColor') == 0x2C2A34DD
assert se_shader_crc('g_SamplerTable') == 0x2005679F
```

## 4a.5 给 `parse_shpk.py` 的补丁建议

`parse_shpk.py:5-29` 的 `CRC_NAMES` 字典可以扩展如下（直接补进去即可）：

```python
# 新增已认领名字
0x29AC0223: "g_AlphaThreshold",
0x11C90091: "g_WhiteEyeColor",
0xD925FF32: "g_ShadowAlphaThreshold",
0x50E36D56: "g_IrisRingColor",
0x58DE06E2: "g_IrisRingForceColor",
0x285F72D2: "g_IrisRingOddRate",
0x37DEA328: "g_IrisUvRadius",
0xE18398AE: "g_IrisRingUvRadius",
0x5B608CFE: "g_IrisRingUvFadeWidth",
0x66C93D3E: "g_IrisThickness",
0x29253809: "g_IrisOptionColorRate",
0x8EA14846: "g_IrisOptionColorEmissiveRate",
0x7918D232: "g_IrisOptionColorEmissiveIntensity",
0xD26FF0AE: "g_VertexMovementMaxLength",
0x641E0F22: "g_VertexMovementScale",
0x00A680BC: "g_ToonSpecIndex",
# (g_IrisRingEmissiveIntensity 已经在表里)
```

补上后 skin.shpk 60 项里只剩 16 项以原始 CRC 显示。

## 4a.6 后续研究方向

若想把剩余 16 个也认出来：

1. **社区字典扩展**：把 `toon.shpk` / `watereffect.shpk` 之类次要 shader 的 MaterialParam 也解析一遍，看有没有与这 16 个相同的 CRC 出现在已命名上下文里。
2. **反向 hash 攻击**：针对 `0x2B5EB116` / `0x5C598180` 的对角关系，构造"XX_Min/XX_Max"族名字批量算 CRC。
3. **DXBC 使用位置取证**：既然这些常量在 skin.shpk 的 PS 里有特定 offset，可以看 PS 反汇编里哪些 `cb0[N].c` 读到了它们的 offset，结合上下文代码猜含义（比如 `cb0[15].w` = offset 252 = `0x5C598180` -> 看它被哪些指令消费）。
4. **新版本 FFXIV 更新**：SE 可能在未来补丁里给某个新 UI 暴露它们（比如"捏脸滑条：脸部高光角度"），那时反编译一看就有线索。

## 状态

- [x] Ch0 总览
- [x] Ch1 shpk 结构
- [x] Ch2 SkinType 分支全貌
- [x] Ch3 DXBC 对比
- [x] Ch4 cbuffer/sampler/texture 清单
- [x] **Ch4 附录：MaterialParam CRC 认领（本文）**
- [x] Ch5 接缝与改造路径
- [x] Ch6 PS[19] 逐段解剖 v1
- [ ] Ch6 v2：Block E 逐指令细解（SSS/GGX 数学解读）
- [ ] Ch7 高级效果 idea 清单
