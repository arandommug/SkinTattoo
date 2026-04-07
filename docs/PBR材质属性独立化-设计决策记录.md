# PBR 材质属性独立化 — 设计决策记录

> 2026-04-07 brainstorming 进行中。本文档记录用户与 Claude 在 brainstorming 阶段达成的所有设计共识。
> 配套文档：`PBR材质属性独立化调研.md`（技术研究 / 物理事实）。
> 后续动作：剩余澄清问题答完后，将共识整合为正式 spec 写入 `docs/superpowers/specs/`。

## 状态总览

| 项 | 状态 |
|---|---|
| 调研（Glamourer 数据流 + ColorTable 行选择机制） | ✅ 完成（写入 `PBR材质属性独立化调研.md`） |
| 设计澄清 Q&A | 🔄 进行中（已答 Q1-Q8，剩余约 1-2 题） |
| 提出 2-3 个备选方案 | ⏸️ 待 |
| 写正式 spec → `docs/superpowers/specs/` | ⏸️ 待 |
| 用户验收 spec | ⏸️ 待 |
| 转入 writing-plans 写实施计划 | ⏸️ 待 |

## 范围划分

### v1（本期）：路线 A — character.shpk 类材质完整支持

**目标材质范围**：所有自带 ColorTable 的材质
- character.shpk
- characterlegacy.shpk
- hair.shpk
- iris.shpk
- 其他装备/头发/眼/眉等

**v1 明确不支持**：vanilla 身体 skin.shpk
- 保留现有 `EmissiveCBufferHook` 兜底——身体材质继续可以改 emissive
- 但 v1 在身体材质上不支持 PBR 字段
- 也不支持"每图层独立 emissive"——因为 g_EmissiveColor 是 CBuffer 全局常量，多图层物理上必然合并

### v2（下一期）：路线 C — skin.shpk → character.shpk 转换

延后的原因：
- 路线 C 还有 6 个待研究项需要 IDA 配合验证（详见调研报告"路线 C 待研究项"）
- 路线 A 完成后我们才有"normal.a 写行号 + 修改 ColorTable"在游戏里实际跑通的第一手经验，再做 C 风险大幅下降
- 路线 A 的所有数据模型 / 合成器 / UI / HTTP 改动是 v2 的必要前置工作

并行任务：v1 实施期间用 IDA + Penumbra 数据双向调研路线 C 的未知点，到 v1 收尾时直接进入 v2 spec。

## 已达成的设计共识

### Q1 / Q3 — 数据模型形态：单一 DecalLayer + LayerKind 枚举

```
enum LayerKind {
    Decal,           // 现有的 PNG 贴花
    WholeMaterial,   // 新增：整张材质作为图层，无 UV 变换
}

class DecalLayer {
    LayerKind Kind;

    // 贴花专属（Kind == WholeMaterial 时 UI 隐藏 / 序列化忽略）
    string ImagePath;
    Vector2 UvCenter, UvScale;
    float RotationDeg;
    ClipMode Clip;

    // 通用：图层级
    float Opacity;
    bool IsVisible;

    // 通用：影响开关（字段级 G1）
    bool AffectsDiffuse;
    bool AffectsSpecular;
    bool AffectsEmissive;
    bool AffectsRoughness;
    bool AffectsMetalness;
    bool AffectsSheen;        // Sheen Rate / Tint / Aperture 三件套合并一个开关

    // 通用：PBR 字段（仅在对应 Affects* 为 true 时写入 ColorTable）
    Vector3 DiffuseColor;
    Vector3 SpecularColor;
    Vector3 EmissiveColor;
    float   EmissiveIntensity;     // 直接乘到 EmissiveColor 写入 Half ColorTable
    float   Roughness;
    float   Metalness;
    float   SheenRate;
    float   SheenTint;             // single Half at offset [13], 已用 Penumbra ColorTableRow.cs:117 验证
    float   SheenAperture;

    // 通用：图层羽化（Q7 中重命名自 EmissiveMask）
    LayerFadeMask FadeMask;        // 旧 EmissiveMask 枚举重命名
    float FadeMaskFalloff;
    float GradientAngleDeg;
    float GradientScale;
    float GradientOffset;
}
```

**理由**：贴花层和材质层共享约 80% 字段（PBR、emissive、opacity、Affects 开关、目标 row pair）。统一类型让合成器只有一条管线、HTTP API 只多一个 `kind` 字段、持久化最简单。Whole material layer 在物理上就是"在所有像素都写自己 row pair 号"的特殊贴花。

### Q4 — Row pair 分配 = A1 全自动 + B3 贴花外保留 vanilla

**A1 全自动分配**：
- 加图层时插件自动分配未占用的 row pair（0-15）
- 用户完全感知不到行号概念，UI 上不暴露
- 不支持"两个图层共享同一行做联动"——如果用户要联动，复制 PBR 数值即可

**B3 贴花外保留 vanilla**：
- 合成 normal.a 时，先扫描原 normal.a 的直方图，标记哪些 row pair 已被 vanilla 占用
- 贴花覆盖区域内的像素 normal.a 写入新分配的 row pair 号
- 贴花覆盖区域外的像素 normal.a 保持 vanilla 原值
- 新分配的 row pair 必须避开 vanilla 占用范围
- ColorTable 写入时只覆盖我们分配到的行，vanilla 行原样保留

**结果**：vanilla 视觉完全不受影响，行号资源 ≈ 16 - vanilla 占用数（一般够 8-12 个图层用）。

### Q5 — 边缘软过渡 + 多图层重叠 = 方案 Y

**方案 Y：硬切 row pair + 行对内 G 通道做软边缘**

每个图层占用一个**完整 row pair**（两行）：

- **行 0** = 该图层调整后的 PBR（用户在 UI 里设的值）
- **行 1** = vanilla 基底 PBR（取被覆盖区域的原始 PBR 作为 fallback）

像素写入规则：

- `normal.a = 该 row pair 号 × 17`（精确到 0-15 行对索引）
- `normal.g = png_alpha × fade_mask_value`（贴花中心 = 1.0 完全用图层 PBR；边缘 = 0.x 跟 vanilla 混合）

多图层重叠区域：**z-order 后胜**。后面图层的 row pair 号完全覆盖前面图层。前面图层在重叠区域的 PBR 看不见。

**单图层占两行的代价**：16 row pair / 2 = 单材质最多 ~16 个图层（实际扣除 vanilla 占用后约 8-12 个），对单材质足够。

**为什么不用方案 Z（每图层只占一行）**：会导致"图层 PBR 跟 vanilla 之间发生 row pair 内插值"，那不是 feature 是 bug。

### Q6 — PBR 字段范围 + Override 语义 = F2 + G1 + 语义 P

**F2：全套 Dawntrail 字段（8 项）**：

| 字段 | Half offset | 类型 |
|---|---|---|
| Diffuse | [0][1][2] | Vector3 RGB |
| Specular | [4][5][6] | Vector3 RGB |
| Emissive | [8][9][10] | Vector3 RGB |
| Sheen Rate | [12] | float (single Half) |
| Sheen Tint | [13] | float (single Half, **不是 RGB**——Penumbra ColorTableRow.cs:117 + Glamourer MaterialValueManager.cs:14 已交叉验证) |
| Sheen Aperture | [14] | float (single Half) |
| Roughness | [16] | float |
| Metalness | [18] | float |

**v1 不做**：Legacy 模式的 GlossStrength [3] / SpecularStrength [7]。等真有用户需求再加，加的成本不大（按 Glamourer 的 ModeToggle 范式）。

**G1：字段级开关**：

每个 PBR 字段一个 `Affects*` bool（Sheen 三件套合并一个开关）。UI 上每个滑块前一个 checkbox，关掉就用 vanilla 值（即"行 1 = vanilla"，行 0 也写 vanilla 值）。

**语义 P：重叠 z-order 后胜**：

多图层在同一像素重叠时，后面的图层完全覆盖前面的。即使后面图层只启用了一部分 PBR 字段（其他字段 Affects=false），重叠区域的 row pair 号仍然是后面图层的，那些未启用字段也会落到 vanilla 值（行 1 内容），不会"穿透"到前面图层的 PBR。

**理由**：跟 row pair 物理模型一致——一个像素只能属于一个 row pair，那个 row pair 的所有字段都是它的。穿透合并需要在 CPU 端做字段级 merge，违背物理模型且实现复杂。

### Q7 — EmissiveMask 迁移 = M1 重命名 + 字段映射

**M1：重命名 EmissiveMask → LayerFadeMask（中文 UI："图层羽化"）**

旧的 7 种枚举值原样保留，含义从"emissive 强度形状"变成"图层整体参与度形状"：

| 旧字段名 | 新字段名 |
|---|---|
| `EmissiveMask` | `FadeMask` |
| `EmissiveMaskFalloff` | `FadeMaskFalloff` |
| `GradientAngleDeg` | `GradientAngleDeg`（不变） |
| `GradientScale` | `GradientScale`（不变） |
| `GradientOffset` | `GradientOffset`（不变） |
| `AffectsEmissive` | `AffectsEmissive`（不变，语义仍是"是否覆盖 emissive"） |

**项目文件 migration**：DecalProject 反序列化时，识别到旧字段名读取后映射到新字段名。一次性写入新格式后旧字段不再出现。

**物理副作用（用户必须意识到）**：

- 旧语义：mask 形状只影响 emissive 强度
- 新语义：mask 形状影响该图层的**所有 PBR 字段**（diffuse / specular / emissive / roughness / metalness / sheen）一起按 mask 形状渐变
- 这是 row pair 内插值的物理本质：一个 G 权重对所有字段同时生效，没法只让某个字段单独 fade
- 视觉上更自然——贴花本来就应该作为"一个整体"在边缘融入 vanilla

**Normal.g 写入公式**：

```
normal.g = clamp(png_alpha × fade_mask_value, 0, 1)
```

PNG 自带 alpha 边缘 + 用户选的 fade mask 形状叠加生效。

**EmissiveIntensity 处理**：

直接乘到 EmissiveColor 三个 Half 上写入 ColorTable 行 0：

```
row[0].EmissiveR = (Half)(EmissiveColor.X × EmissiveIntensity)
row[0].EmissiveG = (Half)(EmissiveColor.Y × EmissiveIntensity)
row[0].EmissiveB = (Half)(EmissiveColor.Z × EmissiveIntensity)
```

Half 类型支持 >1 的值，作 HDR 用没问题。

### Q8 — 范围切分 = 切法 1（v1 = 路线 A only）

详见上方"范围划分"。

## 待澄清的剩余问题

按重要性排序：

1. **行号上限的降级行为** — 用户加了超过可用 row pair 数的图层时怎么办？报错 / 复用最旧 / 拒绝创建？
2. **PBR 字段调整的 inplace swap 边界** — 用户拖滑块改 Roughness 时走 Glamourer 范式的 ColorTable 全字段更新（应该可以无闪烁），但 mask 形状改变需不需要重做合成？字段开关切换走哪条路？
3. **路线 C 的 IDA 调研触发时机** — v1 实施期间并行做？还是 v1 收尾后再开始？

剩余问题答完后进入"提出 2-3 个备选方案 + 用户拍板 + 写正式 spec"阶段。

## 关键技术约束（已固化）

源自调研报告，列在这里方便后续 spec 直接引用：

1. **ColorTable 行号 = round(normal.a / 17)**（Penumbra `MaterialExporter.cs:136`）
2. **行对内插值权重 = 1 - normal.g / 255**（同上 :137）
3. **Dawntrail 布局 = 32 行 × 64 字节 = 2048 字节**；Legacy = 16 行 × 16 字节 = 256 字节
4. **shader 模式判别**：`ShpkName == "characterlegacy.shpk"` → Legacy，否则 Dawntrail（`Glamourer/Interop/Material/PrepareColorSet.cs:144`）
5. **skin.shpk 没有 ColorTable**，`HasColorTable=false`（v1 跳过此类材质的 PBR 处理）
6. **Glamourer 的 ColorTable 全字段写入范本** = `DirectXService.cs:21-46` 的 `ReplaceColorTable`，D3D11 UpdateSubresource，R16G16B16A16Float
7. **Glamourer 的 ColorTable 读回范本** = `DirectXService.cs:49-154` 的 staging texture + memcpy
8. **Latent bug**：`TextureSwapService.UpdateEmissiveViaColorTable` 的 `int rowStride = ctWidth * 4` 对 emissive 两布局都正确，但写 Roughness/Metalness/Sheen 必须按 Dawntrail 8-vec4/行布局，实施时按 `ctWidth >= 8` 区分
