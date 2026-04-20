# Ch2 -- skin.shpk 的 SkinType 分支全貌

> 数据来源：`C:/Users/Shiro/Desktop/FF14Plugins/skin.shpk`（DT 7.x 原版，10 527 598 字节，version `0x0D01`）
> 解析工具：`SkinTatoo/ShaderPatcher/dump_nodes.py`（本轮新写，详见 Sec.2.6）
> 交叉校验：运行时 `SkinShpkPatcher` NodeDump log（`skin-shpk-colortable-implementation.md` + 今日 log）

## 2.1 顶层统计

```
VS=72  PS=384  nodes=768  aliases=22272
SystemKeys=0   SceneKeys=8   MaterialKeys=3   SubViewKeys=2（固定）
```

- **SystemKey 空**：skin.shpk 不依赖任何 system-level 渲染开关。
- **SceneKey=8**：默认值从 `0xA1CDEFE9 ... 0x4518960B`，对应日夜/天气/阴影层级等外部状态。
- **MaterialKey=3**：依次是 `CategorySkinType / CategoryDecalMode / CategoryVertexColorMode`，默认值 `ValFace / ValDecalOff / ValVertexColorOff`。
- **NodeAlias=22272**：22 k 条 selector->node 别名，使得引擎对同一物理 PS 可以通过大量 key 组合命中。这是 Sec.1.4 `NodeSelectors` 字典把所有枚举展平的结果。
- **Nodes=768**：这是 shpk 真正"物理"的变体条目数，768 正好对应 `5 SkinType * 8 SceneKey * ??? = 768`（实际分布见 Sec.2.2）。

## 2.2 SkinType 分支节点分布

按 `MaterialKeys[SkinType_idx]` 聚类 nodes：

| SkinType 值 | 名称 | 节点数 |
|---|---|---:|
| `0x2BDB45F1` | **ValBody** | **256** |
| `0x57FF3B64` | ValBodyJJM | 128 |
| `0x72E697CD` | ValEmissive | 128 |
| `0xF5673524` | ValFace | 128 |
| **`0xF421D264`** | **未知（新发现）** | **128** |

**两个出人意料的发现：**

1. **ValBody 节点数是别人的两倍**（256 vs 128）。这解释了 Sec.1.2 里 `pass[0]` 在 Body 下出现 96 对 `(VS, PS)` 独立组合、而其他分支只有 32 对的差异 ---- Body 多了一整组并列变体（推测与 Hrothgar 身体毛发相关，虽然 SystemKey 为空，但可能走的是额外 SceneKey 维度）。
2. **存在第 5 个 SkinType：`0xF421D264`**。此前我们从未提及。它的 `pass[2] PS` 集合与 `ValFace` 完全相同 -> 观感与 Face 等价。可能是另一个 Face 子变体（Hrothgar-face？老版 `iri` 用法？），留给 Ch3/Ch4 具体证真。

## 2.3 Pass 槽位的功能推断

所有 5 个分支都恰好用到 `Passes[0..4]` 这 5 个槽（`PassIndices` 里只有 5 个非 `0xFF` 项）。每个分支 `PassIndices[16]` 普遍形如：

```
PassIndices[16] = [255, 4, 0, 255, 1, 255, 2, 255, 255, 255, 3, 255, ...]
                     ^       ^         ^         ^
                SubView=1   SubView=2  SubView=4  SubView=6  SubView=10
                ->Passes[4] ->Passes[0] ->Passes[1] ->Passes[2] ->Passes[3]
```

按 SubView 索引反推（推测，需要 Ch6 在引擎侧确认）：

| Pass 槽 | SubView | 推断功能 | 一个典型 PS（Face 分支） |
|---|---|---|---|
| Pass[4] | 1 | 深度/阴影 prepass | PS[4]（所有 SkinType 都共用这 8 个） |
| Pass[0] | 2 | G-Buffer 主 pass（可能的备选 / 前向透明？） | Face PS[0] |
| Pass[1] | 4 | Velocity（运动矢量）？ | Face PS[1] |
| **Pass[2]** | **6** | **主光照 PS（deferred/forward lighting）** | Face PS[2] / Body PS[8] / Emissive PS[19] |
| Pass[3] | 10 | 辅助光照（次要灯？反射？后处理？） | Face PS[3] / Body PS[9] / Emissive PS[20] |

**Pass[2] 才是决定皮肤观感的那个 PS** ---- 所有 SSS、specular、最终输出全在这里，也是 SkinType 分支差异的主要战场。我们原先针对 Emissive `PS[19]` 的 DXBC 改造，实际上是对 Pass[2] 的改造。

## 2.4 Pass[2] Lighting PS 按 SkinType 的完整集合

每个 SkinType 的 pass[2] 覆盖 32 个唯一 PS，对应 SceneKey=8 * 另一维=4 的组合。

| SkinType | Pass[2] PS 集合（32 项） |
|---|---|
| **ValBody** | 8, 23, 38, 53, 68, 83, 98, 113, 128, 143, 158, 173, 188, 203, 218, 233, 242, 251, 260, 269, 278, 287, 296, 305, 314, 323, 332, 341, 350, 359, 368, 377 |
| **ValBodyJJM** | 12, 25, 42, 55, 72, 85, 102, 115, 132, 145, 162, 175, 192, 205, 222, 235, 244, 253, 262, 271, 280, 289, 298, 307, 316, 325, 334, 343, 352, 361, 370, 379 |
| **ValEmissive** | 19, 28, 49, 58, 79, 88, 109, 118, 139, 148, 169, 178, 199, 208, 229, 238, 247, 256, 265, 274, 283, 292, 301, 310, 319, 328, 337, 346, 355, 364, 373, 382 |
| **ValFace** | 2, 21, 32, 51, 62, 81, 92, 111, 122, 141, 152, 171, 182, 201, 212, 231, 240, 249, 258, 267, 276, 285, 294, 303, 312, 321, 330, 339, 348, 357, 366, 375 |
| **0xF421D264** | 与 ValFace 完全相同 |

重要验证：这五个集合两两不相交（除了 0xF421D264 与 Face 共用）-> **引擎在切换 `CategorySkinType` 时是在换一套独立的 PS，不是仅换一组 uniform 常量**。这是"开启发光就出现接缝"的物理根源。

### Pass[2] PS 索引的规律

- 每个 SkinType 的 Pass[2] PS 列表形如 `[base, base+offset, base+30, base+30+offset, base+60, base+60+offset, ...]`。
- Body: `base=8, offset=15`（8, 23；然后每 30 重复）
- BodyJJM: `base=12, offset=13`（12, 25；然后每 30 重复）
- Emissive: `base=19, offset=9`（19, 28；然后每 30 重复）
- Face / 0xF421D264: `base=2, offset=19`（2, 21；然后每 30 重复）

"每 30 重复"反映出 shpk 里 PS 按 SceneKey 串接；内部的 `offset` 则是另一组 key（推测 DecalMode * VertexColorMode * SubViewKey 的某种组合）。后续章节若需精细控制可进一步拆分。

### Pass[0]/[1]/[3]/[4] 的数据也已落盘

完整 pass 分布见 `dump_nodes.py` 输出（Sec.2.6 有复现方式）。摘要：

- **Pass[4] 是全 SkinType 共享**：`[4, 34, 64, 94, 124, 154, 184, 214]` ---- 8 个 PS，按 SceneKey 八倍展开。深度/阴影 prepass 在所有分支一致。
- **Pass[0]/[1] 只跟随 SkinType**：Body 有双倍 PS（推测 Hrothgar 身体毛发），其它分支各 8 个 PS。
- **Pass[3] 跟 Pass[2] 一样 SkinType-specific**，每分支 32 个 PS。

## 2.5 值得追查的未解之谜（留给后续章节）

1. **`0xF421D264` 到底是什么？** Pass[2] PS 与 Face 一致，但作为独立 SkinType 值存在 -> 可能是"Hrothgar 面部"、"Viera 面部"或其它需要在 mtrl 里显式指定的特殊面部变体。排查方向：在游戏 `sqpack` 索引里搜哪种 mtrl 在 AdditionalData / ShaderKey 里写了这个值；或者查 `Penumbra` 的 `DevKit` schema。
2. **Body 多出来的 128 个节点走哪条 key？** 四个 key 类别 (3 Mat + 2 SubView + 0 Sys + 8 Scene) 应该只能给 Body 分出 128 种组合，却实际有 256。说明 Penumbra 在 Ch1 Sec.1.4 末尾提到的"SubViewKey 值由运行时 fill"之外，Body 还多了一条隐式 key。
3. **Pass[0] 和 Pass[2] 在 Body 的双倍 PS 到底有什么区别？** 需要把 Body 的两条分支 PS（如 `PS[5]` vs `PS[6]`）反汇编 diff，看是否是 Hrothgar / 非 Hrothgar 的区别。

## 2.6 复现方式

```
cd C:/Users/Shiro/Desktop/FF14Plugins/SkinTatoo/ShaderPatcher
python dump_nodes.py              # 默认解析 ../../skin.shpk
python dump_nodes.py /path/to/any_skin.shpk
```

脚本输出：
1. 文件头概要（VS/PS/node/alias 数量、各类 Key 数）
2. 每个 SkinType 的 pass[0..4] VS/PS 分布（unique pairs）
3. 所有 pass[2] lighting PS 按 SkinType 的汇总（便于后续 Ch3 DXBC diff 时用到）

脚本覆盖范围：只解析到 Node 层面（不含 DXBC blob），故性能足够轻，单次运行 < 1 秒。

## 2.7 对改造路径的直接影响

有了 Ch2 的数据，前面三条改造路径现在可以量化：

| 路径 | 需要 patch 的 PS 数 | 依赖的前置研究 |
|---|---|---|
| **A** 把 PS[19] 光照调成 Body 风格 | 1 | Ch3 diff PS[19] vs PS[8] |
| **B** 给 Body/Face 分支原地注入 ColorTable 发光 | 32 + 32 = 64 | Ch3 + Ch4：确认 Body/Face PS 资源布局，判断能否机械复用 patch |
| **C** 让脸部 mtrl 也走 Emissive 分支 | 0（mtrl 侧一行代码）| 仅需 Ch3 快速确认 Emissive PS 在无贴花时不会恶化脸部观感 |

**下一步（Ch3）**：把 Face PS[2] / Body PS[8] / Emissive PS[19] 三段 DXBC 反汇编出来，按"cbuffer 绑定 -> 寄存器分配 -> 主 code block"顺序三向 diff。

## 状态

- [x] Ch0 总览
- [x] Ch1 shpk 结构
- [x] Ch2 SkinType 分支全貌（本文）
- [ ] Ch3 DXBC 对比
- [ ] Ch4 cbuffer 清单
- [ ] Ch5 接缝与改造路径
