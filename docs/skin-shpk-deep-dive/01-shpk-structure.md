# Ch1 — shpk 容器格式与 node/selector 选择链路

> 来源：`Penumbra-CN/Penumbra.GameData/Files/ShpkFile.cs`（权威实现）
> 交叉验证：`SkinTatoo/ShaderPatcher/parse_shpk.py`（我们自己写的解析器）
> 辅助：`_shpk_analysis/extract_shpk.py`（从 SqPack 解档 shpk 的脚本）

## 1.1 文件 magic 与版本

```
offset 0x00  magic   = "ShPk" (u32 LE = 0x6B506853)
offset 0x04  version (u32)
offset 0x08  dxVer   = "DX11" (0x31315844) 或 "DX9\0" (0x00395844)
offset 0x0C  fileSize (u32) ── 必须等于文件实际字节数
```

判断版本分水岭：
- `Version < 0x0D01` 且没有 MaterialParamsDefaults 且 `textureCount == 0` → **Legacy** 格式（pre-Dawntrail），sampler 与 texture 不分家。
- `Version >= 0x0D01` → **Dawntrail 之后**。在主头末尾追加 3 个 u32（`unk131` ×3），目前观察到恒为 0，推测是 Geometry/Domain/Hull shader 计数。

Penumbra 甚至提供 `FastIsObsolete()`（仅读前 8 字节）用于判断——对流式替换场景很有用。

## 1.2 主头字段（按顺序）

| 偏移 | 字段 | 类型 | 说明 |
|---:|---|---|---|
| 0x00 | magic | u32 | `ShPk` |
| 0x04 | version | u32 | ≥ 0x0D01 为 DT 格式 |
| 0x08 | dxVersion | u32 | DX9 / DX11 |
| 0x0C | fileSize | u32 | 全文件字节数 |
| 0x10 | blobsOffset | u32 | DXBC blob 区起点 |
| 0x14 | stringsOffset | u32 | 字符串区起点 |
| 0x18 | vertexShaderCount | u32 | VS 条目数 |
| 0x1C | pixelShaderCount | u32 | PS 条目数 |
| 0x20 | materialParamsSize | u32 | cb `g_MaterialParameter` 的总字节数（16 对齐） |
| 0x24 | materialParamCount | u16 | 材质参数条目数 |
| 0x26 | hasMatParamDefaults | u16 | 非零表示后续会跟一段默认值 blob |
| 0x28 | constantCount | u32 | cbuffer 资源条目数 |
| 0x2C | samplerCount | u16 | sampler 资源条目数 |
| 0x2E | textureCount | u16 | texture 资源条目数（Legacy = 0） |
| 0x30 | uavCount | u32 | UAV 资源条目数 |
| 0x34 | systemKeyCount | u32 | SystemKey 数 |
| 0x38 | sceneKeyCount | u32 | SceneKey 数 |
| 0x3C | materialKeyCount | u32 | MaterialKey 数 |
| 0x40 | nodeCount | u32 | Node 条目数 |
| 0x44 | nodeAliasCount | u32 | NodeAlias 条目数（额外的 selector → node 映射） |
| 0x48..0x54 | unk131[3] | u32×3 | 仅 ≥ 0x0D01 存在，恒 0（推测为 GS/DS/HS 计数） |

之后紧跟以下有序区块（与声明顺序一致）：

1. VS 条目数组（每条含 blob 偏移、blob 大小、资源 use 列表、资源 slot 列表）
2. PS 条目数组（同上）
3. MaterialParams[] —— `{u32 Id, u16 ByteOffset, u16 ByteSize}`
4. MaterialParamsDefaults（若 `hasMatParamDefaults`，大小 = `materialParamsSize`）
5. Constants[]（cbuffer）、Samplers[]、Textures[]、Uavs[] —— 每条 `Resource` 16 字节
6. SystemKeys[] / SceneKeys[] / MaterialKeys[] —— 每条 `{u32 Id, u32 DefaultValue}`
7. SubViewKeyDefault1 / SubViewKeyDefault2 —— 两个 u32，固定为 SubViewKey id=1、id=2 的默认值
8. Nodes[]（见 §1.4）
9. NodeAliases[] —— `{u32 Selector, u32 NodeIndex}`
10. AdditionalData（blobsOffset 前的剩余字节；通常为空）
11. blob 区：`data[blobsOffset..stringsOffset]`，所有 shader blob 的实际 DXBC 字节
12. 字符串区：`data[stringsOffset..]`，所有资源/Key 的名字

## 1.3 Shader 条目与 Resource 表

每个 Shader 条目（VS 或 PS）紧凑记录 blob 位置与其所引用的资源：

```
u32  blobOffset     // 相对 blobsOffset
u32  blobSize
u16  constantCount
u16  samplerCount
u16  uavCount
u16  textureCount   // Legacy 恒为 0
[u32 unk131]        // 仅 ≥ 0x0D01
Resource constants[constantCount]
Resource samplers [samplerCount]
Resource uavs     [uavCount]
Resource textures [textureCount]
```

每条 `Resource` 16 字节：

```
u32  id            // 资源名的 CRC32（例如 0x38A64362 = g_EmissiveColor）
u32  strOffset     // 名字在 strings 区里的偏移
u16  strSize       // 名字长度（不含 0 终止）
u16  isTexture     // 0=cbuf/sampler, 1=texture（Penumbra 注释：常量/sampler 恒 0，texture 恒 1）
u16  slot          // HLSL 里的寄存器号（b0/b1/.../s5/t10/u0）
u16  size          // cbuf 按 16B 向量计；sampler/texture 含义不同
```

由于 `id = CRC32(name)`，`parse_shpk.py:5-29` 维护一张反查表把常见 CRC 翻译回人名。Ch0 术语表里的 CRC 都出自这里。

## 1.4 Node、Pass、Selector

整套变体选择机制建立在 **4 组 Key + 1 个 Selector 字典 + Node[]**。

### Key

```csharp
struct Key {
    uint Id;            // CRC32(name)
    uint DefaultValue;  // 未被 node 显式指定时的缺省值
    HashSet<uint> Values;  // 该 key 在所有 node 里出现过的值集合（运行时重建）
}
```

四种 Key：

- **SystemKeys** —— 跟渲染系统状态绑定（高配低配、某些渲染特性开关）
- **SceneKeys** —— 跟场景状态绑定（日间/夜间、天气、光照层级）
- **MaterialKeys** —— 跟材质状态绑定（SkinType / DecalMode / VertexColorMode）
- **SubViewKeys** —— 固定 2 个（id=1、id=2），表示当前 SubView 的两个维度

SubViewKeys 的值集合并不在文件里显式列出，只在头部存它们的默认值（`subViewKey1Default`, `subViewKey2Default`）。运行时具体 SubView 值由引擎填。

### Pass

```csharp
struct Pass {
    uint Id;            // 该 pass 在 shpk 全局 passes 池里的 id
    uint VertexShader;  // VS 索引（进 VertexShaders[]）
    uint PixelShader;   // PS 索引（进 PixelShaders[]）
    uint Unk131A, Unk131B, Unk131C;  // 仅 ≥ 0x0D01，推测是 GS/DS/HS 索引
}
```

### Node

```csharp
struct Node {
    uint Selector;                       // 预计算好的哈希，用于 NodeSelectors 反查
    byte PassIndices[16];                // SubView ID 0..15 → 该 node.Passes[] 的下标
    uint Unk131Keys[2];                  // 仅 ≥ 0x0D01
    uint SystemKeys [systemKeyCount];    // 此 node 要求的 SystemKey 各值
    uint SceneKeys  [sceneKeyCount];     // ...
    uint MaterialKeys[materialKeyCount]; // ...
    uint SubViewKeys[subViewKeyCount];   // ...
    Pass Passes[];                       // 本 node 的 pass 池，按 PassIndices 查
}
```

**PassIndices 是一张 16 槽的查表**。例：`PassIndices[6] = 2` 表示 SubView=6 时走 `node.Passes[2]` 这一 pass。`255` 表示该 SubView 不渲染。我们运行时 log 里见到的典型值是：

```
PassIndices[16] = [255, 4, 0, 255, 1, 255, 2, 255, 255, 255, 3, 255, 255, 255, 255, 255]
```

也就是 SubView = 1/2/4/6/10 各对应一个 pass，其他不画。SubView=6 对应 `Passes[2]`——这就是我们反复提到的"pass[2] 主光照"。

### Selector 计算

Penumbra 的权威实现（`ShpkFile.cs:630-664`）：

```csharp
public const uint SelectorMultiplier = 31;

public static uint BuildSelector(uint sysSel, uint sceneSel, uint matSel, uint subViewSel)
    => BuildSelector(new[] { sysSel, sceneSel, matSel, subViewSel });

public static uint BuildSelector(ReadOnlySpan<uint> keys) {
    uint result = 0, multiplier = 1;
    for (int i = 0; i < keys.Length; ++i) {
        result   += keys[i] * multiplier;
        multiplier *= SelectorMultiplier;
    }
    return result;
}
```

展开等价于：

```
selector = sysKey + sceneKey * 31 + matKey * 961 + subViewKey * 29791
```

**但这里的 `sysKey`/`sceneKey`/... 不是原始 CRC 值**，而是它们各自先经过一次 polynomial-31 哈希（把该组内的几条 key 值摊平）。`AllSelectors(keys, values)` 递归地把每组 key 的所有可能值都摊出来，再把四组乘 31 的幂。

### NodeSelectors 字典

```
Dictionary<uint, uint> NodeSelectors  // selector → nodeIndex
```

- 先把每个 node 自身的 `Selector` 塞进去；
- 再读取 NodeAliases[]（每条 `{selector, nodeIndex}`），把额外的 selector 都指到同一个 node。所以 **多个 key 组合会指向同一 node**（典型场景：多个 SceneKey 值都走同一物理 PS）。

### 引擎查 PS 的全流程

```
当前 (sysKey,sceneKey,matKey,subViewKey)
    ↓ BuildSelector()
selector
    ↓ NodeSelectors[selector]
nodeIndex → node
    ↓ node.PassIndices[subViewID]
passIdx → pass = node.Passes[passIdx]
    ↓
PS = PixelShaders[pass.PixelShader]
```

## 1.5 我们自己的 `parse_shpk.py` 与权威实现的差异

| 项 | parse_shpk.py | Penumbra ShpkFile.cs | 备注 |
|---|---|---|---|
| 版本字段 | `version >= 0x0D01` 时跳 12 字节 | 同，而且校验 unk131 ×3 必须为 0 | 我们只跳不校验，稍显宽松 |
| 每个 Shader 条目的 unk131 | 版本 ≥0x0D01 时跳 4 字节 | 同 | 一致 |
| MaterialParamsDefaults | 未解析（只打印） | 完整读出 | 我们用不到默认值 |
| SubViewKeys | 脚本目前没读（TODO 注释也没写） | 显式造出 id=1、id=2 两条 | 我们后续要解析 node 时必须补上 |
| Nodes / Selectors | **没实现** | 完整实现 | ★ Ch2 要写的脚本就补这一块 |

结论：**当前 `parse_shpk.py` 足以打印资源、常量、keys，但还不能枚举 node**。Ch2 需要写一个增量脚本 `dump_nodes.py`，填上 Node/Pass 的解析，然后按 MaterialKey=SkinType 反向聚类。

## 1.6 参考链路小结（便于后面章节引用）

当用户材质把 `CategorySkinType` 的值写成 `ValueEmissive(0x72E697CD)` 后：

1. 引擎当前场景给出一组 (sysKey, sceneKey, subViewKey) 具体值；
2. matKey 数组里的 CategorySkinType 位置被读为 `0x72E697CD`；
3. `BuildSelector(sys, scene, [..., 0x72E697CD, ...], subView)` 计算出 selector；
4. `NodeSelectors[selector]` 返回 nodeIndex，该 node 的 MaterialKeys 必须与输入匹配；
5. `node.PassIndices[subViewID=6]` 返回 2（主光照 pass）；
6. `node.Passes[2].PixelShader` 指向 PS[19]（或其同分支的兄弟）。

这一套逻辑是**纯数据驱动**的：引擎没有对任何 PS 做特判，我们只要写出正确的 mtrl（或者改 shpk 的 Key 值映射），就能让引擎选中任意 PS。**这是后续所有改造路径的底层自由度保证。**

## 状态

- [x] Ch0 总览
- [x] Ch1 shpk 结构（本文）
- [ ] Ch2 SkinType 分支全貌
- [ ] Ch3 DXBC 对比
- [ ] Ch4 cbuffer 清单
- [ ] Ch5 接缝与改造路径
