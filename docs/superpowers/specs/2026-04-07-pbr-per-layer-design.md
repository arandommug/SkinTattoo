# SkinTatoo PBR 材质属性独立化设计（v1）

**日期**: 2026-04-07
**状态**: 设计已批准，待生成实施计划
**配套文档**:
- `docs/PBR材质属性独立化调研.md` — 物理事实与代码引证
- `docs/PBR材质属性独立化-设计决策记录.md` — Brainstorming Q&A 全程记录

## 背景与目标

当前 SkinTatoo 的图层只能影响 diffuse 像素和 emissive 颜色，且 emissive 在同一 TargetGroup 下被合并——改一个图层的发光会带动其他图层一起变。同时插件也无法调整 Glamourer 已经支持的 PBR 字段（Roughness / Metalness / Specular / Sheen 等）。

本期目标是引入 Glamourer 级别的 PBR 调整能力，并把所有图层的 PBR + emissive 做成**完全独立**——基于 character.shpk 系列 shader 的 ColorTable 行选择机制（normal.a 决定 row pair 号），为每个图层分配一个独立的 ColorTable row pair。

新增"全材质图层"概念：与现有贴花层并列，物理上是"覆盖整张材质 UV 的特殊贴花"，UI 上不暴露 UV 变换控件，但可以承载所有 PBR 字段。

## 范围

### v1（本设计）

**目标材质范围**：所有自带 ColorTable 的 shader package：
- character.shpk
- characterlegacy.shpk
- hair.shpk
- iris.shpk
- 其他装备/头发/眼/眉等

**v1 不支持**：vanilla 身体 skin.shpk
- 现有 `EmissiveCBufferHook` 兜底保留——身体材质继续可改 emissive 颜色
- 身体材质的 PBR 字段在 UI 上灰掉
- 身体材质的 emissive 也无法做"每图层独立"——CBuffer 的 g_EmissiveColor 是全局常量

### v2（后续 spec）

路线 C — skin.shpk → character.shpk 转换。详见调研报告"路线 C 待研究项"。v1 实施期间通过 IDA 并行调研。v1 的数据模型和合成管线设计为 v2 的必要前置——v2 在此基础上加一个"材质 shader 转换 pre-processor"即可。

## 用户决策摘要

| # | 决策点 | 选择 |
|---|---|---|
| Q3 | 数据模型形态 | 单一 `DecalLayer` 类型 + `LayerKind` 枚举（Decal / WholeMaterial） |
| Q4-A | Row pair 分配 | 全自动，用户感知不到行号概念 |
| Q4-B | Vanilla 行号占用 | 扫描并避开，贴花外保留 vanilla normal.a 原值 |
| Q5 | 边缘软过渡 | 每图层占完整 row pair（两行），行 0 = 图层 PBR，行 1 = vanilla 基底，normal.g 做权重插值 |
| Q5 | 多图层重叠 | z-order 后胜，物理上一个像素只能属于一个 row pair |
| Q6-A | PBR 字段范围 | 全套 Dawntrail 8 项（Diffuse / Specular / Emissive / Roughness / Metalness / Sheen Rate / Sheen Tint / Sheen Aperture），Legacy 字段 v1 不做 |
| Q6-B | Override 开关颗粒度 | 字段级（每个字段一个 `Affects*` bool） |
| Q6 | 重叠语义 | 后胜（语义 P）—— 后图层未启用的字段也落到 vanilla，不穿透到前图层 |
| Q7 | EmissiveMask 迁移 | 重命名为 `LayerFadeMask`，物理含义扩展为整个图层的"参与度形状"，对所有 PBR 字段一起 fade |
| Q8 | 范围切分 | v1 = 路线 A only，v2 = 路线 C |
| Q9 | 行号上限降级 | "按需分配 + 灰掉 PBR 字段"——任何 `Affects*` 启用时才占行号，耗尽时弹 toast 并保持 checkbox 关闭 |
| Q10 | Inplace vs Full Redraw | 除"首次接管材质"和"切换 TargetGroup"外全部 inplace |
| Q11 | 路线 C IDA 调研时机 | v1 实施期间并行做 |

## 架构

### 受影响模块

```
SkinTatoo/Core/
  DecalLayer.cs              # 改：加 LayerKind、PBR 字段、Affects 开关、字段重命名
  LayerKind.cs               # 新增：枚举
  LayerFadeMask.cs           # 新增：从 EmissiveMask 重命名 + 迁移
  RowPairAllocator.cs        # 新增：每 TargetGroup 一个，管理 vanilla 占用 + 按需分配
SkinTatoo/Services/
  PreviewService.cs          # 改：CpuUvComposite 新增 normal 重写、引入 RowPairAllocator
  MtrlFileWriter.cs          # 改：写完整 ColorTable（行 0/行 1 一对），不再只写 emissive
  ProjectMigration.cs        # 新增：旧 EmissiveMask 字段映射到 LayerFadeMask
SkinTatoo/Interop/
  TextureSwapService.cs      # 改：UpdateEmissiveViaColorTable → UpdateMaterialPbr (全字段，按 row pair)
  ColorTableSwap.cs          # 新增：抽出 GPU 读 / 写 ColorTable 的范本（参考 Glamourer DirectXService）
SkinTatoo/Gui/
  MainWindow.cs              # 改：图层面板按 Kind 隐藏 UV 控件，加 PBR 字段块
  PbrFieldsPanel.cs          # 新增：可复用的 PBR 字段块（checkbox + slider）
SkinTatoo/Http/
  DebugServer.cs             # 改：ApplyPartialUpdate 加新字段映射
SkinTatoo/Configuration.cs   # 改：DecalProject 反序列化挂上 ProjectMigration
```

### 数据模型

```csharp
namespace SkinTatoo.Core;

public enum LayerKind
{
    Decal,           // PNG 贴花，有 UV 变换
    WholeMaterial,   // 整张材质作为图层，无 UV 变换
}

public enum LayerFadeMask  // 从 EmissiveMask 重命名
{
    Uniform,
    RadialFadeOut,
    RadialFadeIn,
    EdgeGlow,
    DirectionalGradient,
    GaussianFeather,
    ShapeOutline,
}

public class DecalLayer
{
    public LayerKind Kind { get; set; } = LayerKind.Decal;

    // 通用基础
    public string Name = "";
    public bool IsVisible = true;
    public float Opacity = 1.0f;

    // 仅 Decal kind 使用（WholeMaterial 时序列化忽略）
    public string? ImagePath;
    public Vector2 UvCenter = new(0.5f, 0.5f);
    public Vector2 UvScale = new(0.2f, 0.2f);
    public float RotationDeg;
    public ClipMode Clip = ClipMode.None;
    public BlendMode BlendMode = BlendMode.Normal;

    // 影响开关（字段级 G1）
    public bool AffectsDiffuse;
    public bool AffectsSpecular;
    public bool AffectsEmissive;
    public bool AffectsRoughness;
    public bool AffectsMetalness;
    public bool AffectsSheen;        // Sheen 三件套合并一个开关

    // PBR 字段
    public Vector3 DiffuseColor = Vector3.One;
    public Vector3 SpecularColor = Vector3.One;
    public Vector3 EmissiveColor = Vector3.Zero;
    public float   EmissiveIntensity = 1.0f;
    public float   Roughness = 0.5f;
    public float   Metalness;
    public float   SheenRate = 0.1f;
    public float   SheenTint = 0.2f;        // single Half at offset [13], NOT RGB
    public float   SheenAperture = 5.0f;

    // 图层羽化（迁移自 EmissiveMask*）
    public LayerFadeMask FadeMask = LayerFadeMask.Uniform;
    public float FadeMaskFalloff = 0.0f;
    public float GradientAngleDeg;
    public float GradientScale = 1.0f;
    public float GradientOffset;

    // 运行时（不持久化）：当前分配到的 row pair（0-15），未分配为 -1
    [JsonIgnore]
    public int AllocatedRowPair = -1;

    public bool RequiresRowPair =>
        AffectsDiffuse || AffectsSpecular || AffectsEmissive
        || AffectsRoughness || AffectsMetalness || AffectsSheen;
}
```

**注意**：`AllocatedRowPair` 不进序列化，每次 session 启动重新分配。理由：vanilla 占用扫描结果可能因游戏更新而变化，固化到项目文件会带来兼容性陷阱。

### Row pair 分配器

每个 `TargetGroup` 在首次预览时构造一个 `RowPairAllocator`。

```csharp
namespace SkinTatoo.Core;

public class RowPairAllocator
{
    private readonly bool[] occupied = new bool[16];   // index 0-15

    /// Scan vanilla normal.a histogram, mark high-frequency row pairs as occupied
    public void ScanVanillaOccupation(byte[] vanillaNormalRgba, int width, int height)
    {
        int[] histogram = new int[16];
        int totalPixels = width * height;
        for (int i = 3; i < vanillaNormalRgba.Length; i += 4)
        {
            int rowPair = (int)Math.Round(vanillaNormalRgba[i] / 17.0);
            if (rowPair >= 0 && rowPair < 16) histogram[rowPair]++;
        }
        // Threshold: any row pair covering ≥0.5% of pixels is "vanilla occupied"
        int threshold = totalPixels / 200;
        for (int i = 0; i < 16; i++)
            if (histogram[i] > threshold) occupied[i] = true;
    }

    public int? TryAllocate()
    {
        for (int i = 0; i < 16; i++)
            if (!occupied[i]) { occupied[i] = true; return i; }
        return null;
    }

    public void Release(int rowPair)
    {
        if (rowPair >= 0 && rowPair < 16) occupied[rowPair] = false;
        // Vanilla-occupied entries are never released — they were never allocated by us
    }

    public int AvailableSlots => occupied.Count(o => !o) + /* vanilla 占用部分需要扣除 */;
}
```

**按需分配触发点**：

| 触发 | 行为 |
|---|---|
| 加图层（任何 `Affects*` 都没启用） | 不分配 row pair，纯 diffuse 像素叠加路径 |
| 用户启用某图层第一个 `Affects*` 字段 | 调 `TryAllocate()`，成功则记到 `AllocatedRowPair`；失败则 toast 提示，保持 checkbox 关闭 |
| 用户关闭最后一个 `Affects*` | `Release(AllocatedRowPair)`，置 -1 |
| 删除图层 | 同上 |
| 切换 LayerKind | 不影响 row pair 分配，只影响 normal 重写时的覆盖范围 |

### 合成管线改造

#### Normal map 重写（CpuUvComposite 的扩展）

`PreviewService.CpuUvComposite` 当前只产 diffuse，新方案需要产 normal map（其实只重写 .a 和 .g 通道，.r/.b 保留 vanilla 原值）。

**Normal.a 写入逻辑**：

```csharp
// 起点：vanilla normal RGBA 的拷贝（保留 .r/.b）
byte[] normOut = (byte[])vanillaNormal.Clone();

// 按 z-order 从下往上叠加每个图层
foreach (var layer in visibleLayers)
{
    if (layer.AllocatedRowPair < 0) continue;  // 纯 diffuse 图层不影响 normal

    int rowPairValue = layer.AllocatedRowPair * 17;  // 0-15 → 0-255

    foreach (pixel in computeAffectedPixels(layer))
    {
        // PNG alpha × fade mask shape
        float effectiveWeight = pixel.pngAlpha * computeFadeMask(layer, pixel.uv);
        if (effectiveWeight <= 0) continue;

        // z-order 后胜：直接覆盖
        normOut[pixel.idx + 3] = (byte)rowPairValue;                          // .a = row pair
        normOut[pixel.idx + 1] = (byte)((1.0f - effectiveWeight) * 255);      // .g = 1 - weight (Penumbra 公式)
    }
}
```

**关键点**：

- `computeAffectedPixels(layer)`：
  - `LayerKind.Decal`：贴花覆盖的局部像素（按 UvCenter / UvScale / Rotation 计算）
  - `LayerKind.WholeMaterial`：所有像素
- `computeFadeMask(layer, uv)`：实现 7 种 mask 形状（沿用现有 `EmissiveMask` 实现，只是输入从 emissive intensity 改为参与度权重）
- 贴花外的像素 normal.a 保持 vanilla 原值（B3 决策）

#### ColorTable 写入

每次合成完成后，构造完整的 32 行 ColorTable，并通过 inplace swap 写回 GPU。

**ColorTable 构造**：

```csharp
// 起点：从 GPU 读回 vanilla ColorTable（参考 Glamourer DirectXService.TryGetColorTable）
ColorTable.Table table = ReadVanillaColorTable(material);

// 对每个分配到 row pair 的图层，覆盖对应的两行
foreach (var layer in visibleLayers.Where(l => l.AllocatedRowPair >= 0))
{
    int rowPairIdx = layer.AllocatedRowPair;
    int row0 = rowPairIdx * 2;
    int row1 = rowPairIdx * 2 + 1;

    // 行 1 = vanilla 基底（取 vanilla normal.a 在贴花中心位置对应的 vanilla row pair）
    // 简化：直接用 row pair 0 作为 fallback（vanilla 第一行通常是默认 PBR）
    // TODO: 更精确的做法是采样贴花中心位置的 vanilla normal.a，找到对应行
    table.Rows[row1] = vanillaTable.Rows[0];

    // 行 0 = 该图层的 PBR override（启用的字段写新值，未启用的字段写 vanilla 行 1 的值）
    var newRow = vanillaTable.Rows[0];  // 起点 = 行 1
    if (layer.AffectsDiffuse)   newRow.DiffuseColor   = (HalfColor)layer.DiffuseColor;
    if (layer.AffectsSpecular)  newRow.SpecularColor  = (HalfColor)layer.SpecularColor;
    if (layer.AffectsEmissive)  newRow.EmissiveColor  = (HalfColor)(layer.EmissiveColor * layer.EmissiveIntensity);
    if (layer.AffectsRoughness) newRow.Roughness      = (Half)layer.Roughness;
    if (layer.AffectsMetalness) newRow.Metalness      = (Half)layer.Metalness;
    if (layer.AffectsSheen)
    {
        newRow.SheenRate     = (Half)layer.SheenRate;
        newRow.SheenTintRate = (Half)layer.SheenTint;   // single Half, [13]
        newRow.SheenAperture = (Half)layer.SheenAperture;
    }
    table.Rows[row0] = newRow;
}

// 通过 inplace swap 写回 GPU
colorTableSwap.ReplaceColorTable(materialTexture, table);
```

**注意未确认项**：行 1 的 vanilla 基底取值策略目前是简化版（直接用 vanilla row 0）。更精确的做法是按贴花中心 UV 采样 vanilla normal.a，反查对应 row pair 的 vanilla 行。**实施时先用简化版跑通，遇到视觉问题再换精确版**。

#### Inplace swap 边界

| 操作 | 路径 |
|---|---|
| 拖动 PBR 滑块 | inplace ColorTable 写回 |
| 切换 `Affects*` 开关（不涉及行号分配） | inplace ColorTable 写回 |
| 启用第一个 PBR 字段（首次行号分配） | inplace（同时重写 normal + ColorTable） |
| 关闭最后一个 PBR 字段（行号释放） | inplace |
| 改 EmissiveIntensity | inplace |
| 改 LayerFadeMask 形状 / Falloff / Gradient | inplace（normal 重合成） |
| 改 UV 中心 / 缩放 / 旋转 | inplace（normal + diffuse 重合成） |
| 切换 LayerKind | inplace（normal 重合成） |
| 加图层 / 删图层 | inplace |
| 改 PNG ImagePath | inplace |
| **首次接管材质** | full redraw |
| **切换 TargetGroup** | full redraw |

**实现要点**：异步合成线程产出一个 `SwapBatch { diffuseRgba, normalRgba, colorTable }` 三件套，主线程在一帧内原子提交。

### UI 设计

#### 图层面板布局（按 Kind 切换）

```
[Layer 列表]
  + Decal layer "刺青 1"
  + Whole material layer "皮肤金属化"
  + ...
  [+] 新建贴花 [+] 新建材质层

[选中图层属性]
  名称: ___
  可见 [✓]   不透明度: [====     ]

  ┌─ 贴花变换（仅 Decal kind 显示） ──────┐
  │ UV 中心: [0.5] [0.5]                  │
  │ UV 缩放: [0.2] [0.2]                  │
  │ 旋转角度: [0°]                        │
  │ 镜像裁剪: [无 ▼]                      │
  │ PNG: [选择文件...]                    │
  └───────────────────────────────────────┘

  ┌─ 图层羽化 ──────────────────────────┐
  │ 形状: [Uniform ▼]                    │
  │ 衰减: [====      ]                   │
  │ ... (按形状显示对应参数)             │
  └──────────────────────────────────────┘

  ┌─ PBR 属性 ──────────────────────────┐
  │ [✓] 漫反射     [color picker]        │
  │ [ ] 镜面反射   [color picker]        │
  │ [✓] 发光       [color picker] 强度[1]│
  │ [ ] 粗糙度     [====      ]          │
  │ [ ] 金属度     [====      ]          │
  │ [ ] 光泽       Rate[ ] Tint[col] Apt[]│
  │                                       │
  │ ⚠ Row pair 已耗尽（如适用）          │
  └───────────────────────────────────────┘
```

- **隐藏 UV 控件**：`Kind == WholeMaterial` 时整个"贴花变换"框不渲染
- **PBR 字段**：每行 = checkbox + 控件。checkbox 触发行号分配/释放，可能弹 toast
- **行号上限提示**：当 `RowPairAllocator.AvailableSlots == 0` 且当前图层未持有 row pair 时，整个 PBR 块标灰，顶部加一个 warning banner

#### 行号上限的 UX

当用户点击一个 `Affects*` checkbox 但行号耗尽时：

1. checkbox 不被勾选（保持 false）
2. 显示 toast：`"已达 PBR 行号上限（当前 N 个图层使用 PBR），无法启用此字段。请关闭其他图层的 PBR 字段或删除图层。"`
3. 在 PBR 块顶部显示常驻 warning banner，提醒用户当前材质 PBR 容量已满

### HTTP API 改动

`PUT /api/layer/{id}` 的 `ApplyPartialUpdate` (`Http/DebugServer.cs:510-564`) 需要增加以下字段映射：

```
kind                  → DecalLayer.Kind
affectsSpecular       → bool
affectsRoughness      → bool
affectsMetalness      → bool
affectsSheen          → bool
diffuseColor          → {r,g,b}
specularColor         → {r,g,b}
roughness             → float
metalness             → float
sheenRate             → float
sheenTint             → float          // single value, not RGB
sheenAperture         → float
fadeMask              → string (LayerFadeMask 枚举名)
fadeMaskFalloff       → float
```

**注意**：现有 `emissiveMask` / `emissiveMaskFalloff` 字段是已知的 API 完整性缺口（调研报告事实 7 + DebugServer.cs:560+）。本次顺手补齐，同时支持新旧字段名读取（migration 期间）。

### EmissiveMask → LayerFadeMask 迁移

#### 项目文件迁移

`Configuration` 反序列化时识别 v1 之前的字段名：

```csharp
// Pseudo-code
public class ProjectMigration
{
    public static void Migrate(JsonObject layerNode)
    {
        // 字段重命名
        if (layerNode.TryGetPropertyValue("emissiveMask", out var em))
        {
            layerNode["fadeMask"] = em;
            layerNode.Remove("emissiveMask");
        }
        if (layerNode.TryGetPropertyValue("emissiveMaskFalloff", out var emf))
        {
            layerNode["fadeMaskFalloff"] = emf;
            layerNode.Remove("emissiveMaskFalloff");
        }
        // gradientAngleDeg / gradientScale / gradientOffset 字段名不变
    }
}
```

迁移在 `Configuration.Load()` 内一次性完成，下次 Save 时旧字段名不再出现。

#### 行为变化告知

迁移后用户的旧项目里所有 fade mask 形状会从"只对 emissive fade"变成"对图层所有效果一起 fade"。这是 row pair 物理模型的必然结果。

**首次加载迁移过的项目时弹一次性公告**：

```
检测到旧版项目文件已自动升级。

注意：图层羽化（原"发光遮罩"）的行为有所变化——现在它会让该图层的所有 PBR 效果（包括漫反射、镜面反射等）一起按形状渐变，而不再仅影响发光。如果旧效果与预期不符，请检查图层羽化设置。
```

公告 ack 后写入 Configuration 一个 flag 不再弹出。

## 测试与验证

### 单元测试范围

| 测试 | 验证 |
|---|---|
| RowPairAllocator vanilla 扫描 | 用合成的 normal RGBA fixture 验证直方图阈值正确 |
| RowPairAllocator 按需分配 | 启用 → 分配；关闭最后一个 → 释放；耗尽 → 返回 null |
| LayerFadeMask 7 种形状 | 已有 EmissiveMask 测试沿用，输入输出语义不变 |
| ColorTable 行布局 | Dawntrail vs Legacy（如未来加 Legacy 支持时）的 Half offset 正确 |
| ProjectMigration | 旧字段名能正确映射到新字段名 |

### 游戏内手动验证清单

1. **首次接管 character.shpk 类材质**：装备/眼/发，验证 normal 重写 + ColorTable 写入不崩游戏
2. **每图层独立 PBR**：加两个图层 A、B，A 设金属度 1.0，B 设粗糙度 0.0，预期两个图层的视觉互不干扰
3. **每图层独立 emissive**：加两个图层，分别设不同发光颜色，预期不再合并
4. **多图层重叠 z-order**：加 A、B 重叠，验证后图层完全覆盖前图层 PBR
5. **行号上限**：单材质加超过可用 row pair 数的图层，验证降级行为正确
6. **vanilla 视觉保留**：在装备上加单个图层后，对比修改前后非贴花区域的视觉是否完全一致
7. **图层羽化软过渡**：选 RadialFadeOut，验证贴花边缘是否平滑过渡到 vanilla PBR
8. **WholeMaterial 图层**：新建一个全材质层，调金属度，验证整张材质均匀变金属
9. **混合 Decal + WholeMaterial**：全材质层在底，贴花层在上，验证贴花区域显示贴花的 PBR
10. **inplace 无闪烁**：拖动任意 PBR 滑块，验证角色不闪烁
11. **vanilla skin.shpk 兜底**：身体材质加图层，验证 PBR 字段灰掉、emissive 仍可改（但合并）
12. **项目迁移**：用 v0 时代的项目文件加载，验证字段映射正确 + 一次性公告弹出

## v2 路线 C 接口预留

v1 的代码结构应为 v2 的 skin.shpk → character.shpk 转换留出最小侵入接入点：

| 接入点 | 设计 |
|---|---|
| 材质类型判别 | `MaterialUtil.IsColorTableSupported(materialHandle)` 单点函数。v1 = 检查 `HasColorTable`；v2 = 在转换后的材质上也返回 true |
| ColorTable 读回 | `ColorTableSwap.ReadVanillaColorTable(material)` 抽象。v1 = 直接读 GPU；v2 = 对转换后的材质，从 v2 转换器生成的"虚拟 vanilla 表"读 |
| Normal 通道含义 | `MaterialUtil.NormalAlphaIsRowPair(materialHandle)` 单点函数。v1 = ColorTable 类材质返回 true；v2 = 转换过的 skin 材质也返回 true |
| 合成器入口 | `PreviewService` 的合成入口接收 `ITargetMaterialAdapter` 接口，v1 实现 = `CharacterShpkAdapter`，v2 加 `ConvertedSkinShpkAdapter` |

**v1 不实现这些接口的"v2 分支"，但函数签名按"v2 友好"设计**。这样 v2 spec 可以在不重构 v1 代码的前提下扩展。

## 风险与已知限制

### 风险

1. **行 1 vanilla 基底取值简化**：当前设计直接用 vanilla row 0 做 fallback。如果 vanilla 材质在不同区域有显著不同的 PBR，贴花边缘 fade 的过渡终点会不对（用户预期是"过渡到本地 vanilla"，实际是"过渡到 vanilla row 0"）。简化版上线后用第一手反馈决定是否升级
2. **vanilla 行号扫描的阈值**：0.5% 像素阈值是经验值，可能在某些极端材质上误判（小面积的特殊行被当成"未占用"覆盖）。需要在测试清单第 6 项验证
3. **与现有 EmissiveCBufferHook 的并存**：身体材质走 hook 路径，character.shpk 类走新 ColorTable 路径，分流逻辑必须严格——一张材质不能同时被两条路径修改

> 已解决：原"SheenTint Half offset 布局"风险已通过 Glamourer `MaterialValueManager.cs:14` + Penumbra `ColorTableRow.cs:117` 双向交叉验证消解——SheenTint 是 single Half (`row.SheenTintRate = this[13]`)，不是 RGB。

### 已知限制

1. **不支持 Legacy 模式（characterlegacy.shpk）的 PBR 字段**：Legacy 的 GlossStrength / SpecularStrength v1 不开放，等真有用户报告再加
2. **单材质上限 16 个 PBR 图层**：扣除 vanilla 占用后实际约 8-12 个。纯装饰图层（不启用 PBR）不占行号，可以无限加
3. **多图层 PBR 不能在重叠区域混合**：物理决定的硬限制。重叠区域 100% 归 z-order 后胜的图层管
4. **图层羽化对所有 PBR 字段一起生效**：物理决定的硬限制。无法做"diffuse 是硬边但 emissive 是软边"
5. **v1 不覆盖身体材质**：路线 C 在 v2 才做
