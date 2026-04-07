# PBR 材质属性独立化（v1）实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让 SkinTatoo 每个贴花图层持有完整 PBR 字段（Diffuse / Specular / Emissive / Roughness / Metalness / Sheen 三件套），并基于 character.shpk 系列的 ColorTable 行对机制做到**完全独立**——每图层一个 row pair，互不干扰；同时新增"整张材质作为图层"概念。

**Architecture:** 引入 `RowPairAllocator`（每 TargetGroup 一个）按需分配 0-15 行对；`CpuUvComposite` 除了写 diffuse 外同步重写 normal map `.a`（行对号）和 `.g`（行对内权重）；`ColorTableBuilder` 根据图层 PBR 覆盖对应行；`TextureSwapService` 抽出 `ReplaceColorTable` 做全表 GPU 原子交换；`SwapBatch` 扩成 `{diffuse, normal, colorTable}` 三元组；UI 按 `LayerKind` 隐藏 UV 控件，加 PBR 字段块 + 行号上限 UX。`EmissiveMask` 系列在 DecalLayer / UI / 方法名上全部重命名为 `LayerFadeMask`，但 `SavedLayer`（磁盘格式）保留旧字段名做兼容。

**Tech Stack:** C# 12 / .NET 9 / Dalamud SDK 14 / Lumina / SharpDX / FFXIVClientStructs / ImGui。不引入新依赖（`Penumbra.GameData` 不拉进来，用裸 `Half[]` + 固定 offset 常量）。

**Spec 引用:**
- `docs/superpowers/specs/2026-04-07-pbr-per-layer-design.md` — 正式 spec
- `docs/PBR材质属性独立化调研.md` — 物理事实
- `docs/PBR材质属性独立化-设计决策记录.md` — brainstorming 决策

**Scope check:** 本 plan 只覆盖 v1（路线 A，character.shpk 类材质）。skin.shpk 的路线 C 是独立 subsystem，已在 spec 中明确延后到 v2 并单独写 plan。本 plan 里不要做"顺手加 skin.shpk 支持"的扩展。

---

## 文件结构总览

| 文件 | 操作 | 责任 |
|---|---|---|
| `SkinTatoo/Core/LayerKind.cs` | 新建 | `LayerKind` 枚举（Decal / WholeMaterial） |
| `SkinTatoo/Core/DecalLayer.cs` | 修改 | 加 `Kind`、`Affects*` 字段级开关、PBR 字段、`AllocatedRowPair` 运行时态；`EmissiveMask` 枚举 → `LayerFadeMask`；`EmissiveMask` 字段 → `FadeMask` |
| `SkinTatoo/Core/RowPairAllocator.cs` | 新建 | 每 TargetGroup 一个；vanilla 扫描 + 按需分配 + 释放 |
| `SkinTatoo/Core/TargetGroup.cs` | 修改 | `AddLayer` 接受 `LayerKind` 参数；`HasEmissiveLayers` 迁移为 `HasPbrLayers` |
| `SkinTatoo/Services/ColorTableBuilder.cs` | 新建 | 纯函数：输入 vanilla `Half[]` + layers → 输出覆盖后的 `Half[]`；含 Dawntrail 布局 offset 常量 |
| `SkinTatoo/Services/PreviewService.cs` | 修改 | `RowPairAllocator` 存储 + 生命周期、normal 重写、`SwapBatch` 扩为三元组、分配触发点、rename emissive→fade mask 引用 |
| `SkinTatoo/Interop/TextureSwapService.cs` | 修改 | 抽出 `TryGetVanillaColorTable` / `ReplaceColorTableRaw`；`UpdateEmissiveViaColorTable` 内部复用 |
| `SkinTatoo/Http/DebugServer.cs` | 修改 | 序列化/ApplyPartialUpdate 增补 `kind`、`affects*`、PBR 字段、`fadeMask*`（同时接受旧 `emissiveMask*`） |
| `SkinTatoo/Gui/MainWindow.cs` | 修改 | 新增"材质层"创建按钮；按 `Kind` 隐藏 UV；新增 PBR 字段块 + 行号 UX + toast；rename `EmissiveMaskNames` → `LayerFadeMaskNames`；接 `layerFadeMigrationNotice` 弹窗 |
| `SkinTatoo/Core/DecalProject.cs` | 修改 | Save/Load 映射新字段；`SavedLayer.EmissiveMask` ↔ `DecalLayer.FadeMask`；DiffuseColor/PBR 新字段 |
| `SkinTatoo/Configuration.cs` | 修改 | Version 3 → 4；`SavedLayer` 加新字段；加 `ShowLayerFadeMaskMigrationNotice` flag |

**设计决策 — SavedLayer 不改旧字段名**: 磁盘 JSON 里继续用 `EmissiveMask` / `EmissiveMaskFalloff` 做字段名，新增 PBR 字段平铺上去即可。好处：旧项目文件原样加载，无需 JSON 级 migration。`DecalLayer` 类本身使用新字段名，`DecalProject.Save/Load` 在两者间手动映射——把"旧名对新名"的一次性转换做在映射层，一行代码的事。

**对 spec 的微调（已和 spec 作者脑内对齐）**:
- spec 列的 `Core/LayerFadeMask.cs` 单独文件 → 本 plan 把 `LayerFadeMask` enum 放进 `Core/DecalLayer.cs` 一起定义（enum 很小、和 DecalLayer 紧耦合、拆单文件反而增加心智负担）。
- spec 列的 `Core/ProjectMigration.cs` 单独文件 → 本 plan 不新建。采用 SavedLayer disk-compat 方案后，迁移逻辑只是映射层两行代码，没必要单独建文件。
- spec 列的 `Interop/ColorTableSwap.cs` 单独文件 → 本 plan 把 read/write 抽象放进 `TextureSwapService`（新增 `TryGetVanillaColorTable` / `ReplaceColorTableRaw`），把 table building 逻辑放进 `Services/ColorTableBuilder.cs`。这样分比单独 ColorTableSwap 文件更符合"I/O 和纯函数分层"。
- spec 列的 `Gui/PbrFieldsPanel.cs` 单独文件 → 本 plan 把 PBR 面板代码直接 inline 到 `MainWindow.cs`（通过新 `DrawPbrCheckbox` helper 做局部复用）。v1 只有一个地方画这个面板，抽类反而增加维护成本；等后续真有复用需求再重构。
- spec 列的 `Services/MtrlFileWriter.cs` 修改 → 本 plan 不动它。character.shpk 类材质的 ColorTable 通过 GPU swap 写入，不需要改 .mtrl 文件；skin.shpk 的 emissive 兜底路径仍走现有 MtrlFileWriter 逻辑，保持不变。

---

## Task 1: 数据模型 — LayerKind + DecalLayer 补 PBR 字段 + 全工程 rename EmissiveMask → LayerFadeMask

**Files:**
- Create: `SkinTatoo/SkinTatoo/Core/LayerKind.cs`
- Modify: `SkinTatoo/SkinTatoo/Core/DecalLayer.cs`
- Modify: `SkinTatoo/SkinTatoo/Core/TargetGroup.cs`
- Modify: `SkinTatoo/SkinTatoo/Core/DecalProject.cs` (update Load 引用新字段名，保持旧字段名做磁盘兼容)
- Modify: `SkinTatoo/SkinTatoo/Services/PreviewService.cs` (跟进 rename)
- Modify: `SkinTatoo/SkinTatoo/Gui/MainWindow.cs` (跟进 rename + 重命名 `EmissiveMaskNames`)
- Modify: `SkinTatoo/SkinTatoo/Http/DebugServer.cs` (跟进 rename)

重要：这一步涉及跨文件重命名，提交前必须一次性改完，否则编译不过。

- [ ] **Step 1: 新建 `LayerKind.cs`**

```csharp
namespace SkinTatoo.Core;

public enum LayerKind
{
    Decal,           // PNG 贴花，有 UV 变换
    WholeMaterial,   // 整张材质作为图层，无 UV 变换
}
```

- [ ] **Step 2: 改 `DecalLayer.cs` 全文**

把 `SkinTatoo/SkinTatoo/Core/DecalLayer.cs` 整个替换为：

```csharp
using System.Numerics;

namespace SkinTatoo.Core;

public enum BlendMode
{
    Normal,
    Multiply,
    Screen,
    Overlay,
    SoftLight,
    HardLight,
    Darken,
    Lighten,
    ColorDodge,
    ColorBurn,
    Difference,
    Exclusion,
}

// Renamed from EmissiveMask. Semantics widened: the shape now controls
// the ENTIRE layer's participation weight (all PBR fields fade together,
// not just emissive). Enum values and ordering preserved for compat.
public enum LayerFadeMask
{
    Uniform,
    RadialFadeOut,
    RadialFadeIn,
    EdgeGlow,
    DirectionalGradient,
    GaussianFeather,
    ShapeOutline,
}

public enum ClipMode
{
    None,
    ClipLeft,
    ClipRight,
    ClipTop,
    ClipBottom,
}

public class DecalLayer
{
    // New in v1 PBR: layer type
    public LayerKind Kind { get; set; } = LayerKind.Decal;

    public string Name { get; set; } = "New Decal";
    public string? ImagePath { get; set; }

    // UV-space placement — only meaningful when Kind == Decal
    public Vector2 UvCenter { get; set; } = new(0.5f, 0.5f);
    public Vector2 UvScale { get; set; } = new(0.2f, 0.2f);
    public float RotationDeg { get; set; } = 0f;

    public float Opacity { get; set; } = 1.0f;
    public BlendMode BlendMode { get; set; } = BlendMode.Normal;
    public ClipMode Clip { get; set; } = ClipMode.None;
    public bool IsVisible { get; set; } = true;

    // Field-level PBR affect switches (G1 granularity per spec Q6-B)
    public bool AffectsDiffuse { get; set; } = true;
    public bool AffectsSpecular { get; set; } = false;
    public bool AffectsEmissive { get; set; } = false;
    public bool AffectsRoughness { get; set; } = false;
    public bool AffectsMetalness { get; set; } = false;
    public bool AffectsSheen { get; set; } = false;   // Sheen Rate/Tint/Aperture combined

    // PBR field values
    public Vector3 DiffuseColor { get; set; } = new(1f, 1f, 1f);
    public Vector3 SpecularColor { get; set; } = new(1f, 1f, 1f);
    public Vector3 EmissiveColor { get; set; } = new(1f, 1f, 1f);
    public float EmissiveIntensity { get; set; } = 1.0f;
    public float Roughness { get; set; } = 0.5f;
    public float Metalness { get; set; } = 0f;
    public float SheenRate { get; set; } = 0.1f;
    public float SheenTint { get; set; } = 0.2f;        // single Half at offset [13], NOT RGB
    public float SheenAperture { get; set; } = 5.0f;

    // Layer fade mask (renamed from EmissiveMask*; applies to ALL PBR fields now)
    public LayerFadeMask FadeMask { get; set; } = LayerFadeMask.Uniform;
    public float FadeMaskFalloff { get; set; } = 0.5f;
    public float GradientAngleDeg { get; set; } = 0f;
    public float GradientScale { get; set; } = 1f;
    public float GradientOffset { get; set; } = 0f;

    // Runtime-only: which ColorTable row pair is assigned to this layer (0-15).
    // Not persisted — SavedLayer is the serialization middleman and doesn't carry this field,
    // so vanilla scan results re-compute every session (intentional per spec).
    public int AllocatedRowPair { get; set; } = -1;

    /// <summary>True if any PBR field is enabled — gate for row pair allocation.</summary>
    public bool RequiresRowPair =>
        AffectsDiffuse || AffectsSpecular || AffectsEmissive
        || AffectsRoughness || AffectsMetalness || AffectsSheen;
}
```

说明：`AffectsDiffuse` 默认 `true` 保持贴花用户的常规预期（贴图即上色）。其他 `Affects*` 默认 `false`，用户主动启用。`AllocatedRowPair` 不加任何序列化属性——`DecalLayer` 从来不直接进 Newtonsoft.Json，走的是 `SavedLayer` 中间类手工映射，而 `SavedLayer` 不带这个字段，所以天然不持久化。

- [ ] **Step 3: 改 `TargetGroup.cs`**

打开 `SkinTatoo/SkinTatoo/Core/TargetGroup.cs`，替换 `AddLayer` 和 `HasEmissiveLayers`：

```csharp
    public DecalLayer AddLayer(string name = "New Decal", LayerKind kind = LayerKind.Decal)
    {
        var layer = new DecalLayer { Name = name, Kind = kind };
        Layers.Add(layer);
        SelectedLayerIndex = Layers.Count - 1;
        return layer;
    }

    public bool HasEmissiveLayers()
    {
        // v1 PBR: "emissive" now means "any visible layer that affects emissive" —
        // WholeMaterial layers have no ImagePath but still count.
        foreach (var l in Layers)
        {
            if (!l.IsVisible || !l.AffectsEmissive) continue;
            if (l.Kind == LayerKind.Decal && string.IsNullOrEmpty(l.ImagePath)) continue;
            return true;
        }
        return false;
    }

    public bool HasPbrLayers()
    {
        foreach (var l in Layers)
        {
            if (!l.IsVisible || !l.RequiresRowPair) continue;
            if (l.Kind == LayerKind.Decal && string.IsNullOrEmpty(l.ImagePath)) continue;
            return true;
        }
        return false;
    }
```

- [ ] **Step 4: 跟进全工程 rename**

搜 `EmissiveMask`（作为类型/属性引用）+ `EmissiveMaskFalloff`，替换如下：

| 旧引用 | 新引用 |
|---|---|
| 类型 `EmissiveMask` | `LayerFadeMask` |
| `layer.EmissiveMask` | `layer.FadeMask` |
| `layer.EmissiveMaskFalloff` | `layer.FadeMaskFalloff` |
| `PreviewService.ComputeEmissiveMask` 方法名 | `ComputeFadeMaskWeight` |
| `MainWindow.EmissiveMaskNames` 字段名 | `LayerFadeMaskNames` |
| `MainWindow.DrawEmissiveMaskPreview` 方法名 | `DrawFadeMaskPreview` |

**注意**：
- `SavedLayer.EmissiveMask` / `SavedLayer.EmissiveMaskFalloff`（`Configuration.cs` 里）**不改**——保留旧字段名做磁盘兼容。`DecalProject.Save/Load` 做映射。
- `MtrlFileWriter` 里的 `CategorySkinType` / `ValueEmissive` / `ConstantEmissiveColor` 常量 **不改**——那是 FFXIV shader key 的固定 CRC，不是字段名。
- `EmissiveCBufferHook` 的类名和 `g_EmissiveColor` 引用 **不改**——那是 shader constant 名。
- `HasEmissiveLayers` 方法名 **不改**——仍用于"是否需要初始化 norm alpha + mtrl 发光"的判定（v1 语义保留）。

执行：

```bash
cd "C:/Users/Shiro/Desktop/FF14Plugins/SkinTatoo"
```

然后手动打开每个报错文件按上表改。特别检查：
- `Services/PreviewService.cs`（`LayerSnapshot` 有 `EmissiveMask` / `EmissiveMaskFalloff` 字段 + `CompositeEmissiveNorm` 有引用 + `ComputeEmissiveMask` 静态方法定义）
- `Gui/MainWindow.cs`（多处，包括 `ResetSelectedLayer` 里给 `layer.EmissiveMask = d.EmissiveMask;` 的行）
- `Http/DebugServer.cs`（`SerializeLayer` 中的 `emissiveMask = l.EmissiveMask.ToString()` 行）

**`DecalProject.cs` 的 Load 部分**（关键）：

```csharp
// 旧：EmissiveMask = (EmissiveMask)s.EmissiveMask,
FadeMask = (LayerFadeMask)s.EmissiveMask,
FadeMaskFalloff = s.EmissiveMaskFalloff,
```

**`DecalProject.cs` 的 Save 部分**：

```csharp
// 旧：EmissiveMask = (int)l.EmissiveMask,
EmissiveMask = (int)l.FadeMask,
EmissiveMaskFalloff = l.FadeMaskFalloff,
```

- [ ] **Step 5: 编译验证**

```bash
cd "C:/Users/Shiro/Desktop/FF14Plugins/SkinTatoo"
dotnet build -c Release
```

Expected: `Build succeeded. 0 Warning(s). 0 Error(s).`

如有 CS0117 / CS1061 类错误，按提示补漏 rename 点。

- [ ] **Step 6: Commit**

```bash
git add SkinTatoo/SkinTatoo/Core/LayerKind.cs SkinTatoo/SkinTatoo/Core/DecalLayer.cs SkinTatoo/SkinTatoo/Core/TargetGroup.cs SkinTatoo/SkinTatoo/Core/DecalProject.cs SkinTatoo/SkinTatoo/Services/PreviewService.cs SkinTatoo/SkinTatoo/Gui/MainWindow.cs SkinTatoo/SkinTatoo/Http/DebugServer.cs
git commit -m "feat(pbr): 数据模型加 LayerKind + PBR 字段，rename EmissiveMask → LayerFadeMask"
```

---

## Task 2: Configuration / SavedLayer 补字段 + Version 升 4 + 迁移通告 flag

**Files:**
- Modify: `SkinTatoo/SkinTatoo/Configuration.cs`
- Modify: `SkinTatoo/SkinTatoo/Core/DecalProject.cs` (Save/Load 补新 PBR 字段的双向映射)

- [ ] **Step 1: `Configuration.cs` SavedLayer 补字段 + Version 升 + 通告 flag**

在 `SavedLayer` 类里（`Configuration.cs:9-35`）**追加**以下字段（**不要**删除 `EmissiveMask` / `EmissiveMaskFalloff`）：

```csharp
    // v1 PBR: layer kind
    public int Kind { get; set; } = 0;   // 0 = Decal, 1 = WholeMaterial

    // Field-level affect switches
    public bool AffectsSpecular { get; set; }
    public bool AffectsRoughness { get; set; }
    public bool AffectsMetalness { get; set; }
    public bool AffectsSheen { get; set; }

    // PBR field values
    public float DiffuseColorR { get; set; } = 1f;
    public float DiffuseColorG { get; set; } = 1f;
    public float DiffuseColorB { get; set; } = 1f;

    public float SpecularColorR { get; set; } = 1f;
    public float SpecularColorG { get; set; } = 1f;
    public float SpecularColorB { get; set; } = 1f;

    public float Roughness { get; set; } = 0.5f;
    public float Metalness { get; set; } = 0f;
    public float SheenRate { get; set; } = 0.1f;
    public float SheenTint { get; set; } = 0.2f;
    public float SheenAperture { get; set; } = 5.0f;
```

在 `Configuration` 类里（`Configuration.cs:57-78`）：

- 把 `public int Version { get; set; } = 3;` 改成 `public int Version { get; set; } = 4;`
- 在 export 相关字段下面 **追加**：

```csharp
    // v1 PBR: if true, MainWindow shows a one-time dialog explaining the
    // EmissiveMask → LayerFadeMask semantics change (widens to all PBR fields).
    // Set to true on first load of any v3-saved project; cleared after user acks.
    public bool ShowLayerFadeMaskMigrationNotice { get; set; } = false;
```

- [ ] **Step 2: `Configuration.cs` 在 `Initialize` 里检测旧版本**

找到 `Configuration.cs` 底部的 `Initialize` 方法，改成：

```csharp
    public void Initialize(IDalamudPluginInterface pi)
    {
        pluginInterface = pi;

        // v3 → v4: EmissiveMask semantics widened (now fades all PBR fields, not just emissive).
        // Trigger a one-time notice dialog on next MainWindow draw.
        if (Version < 4 && TargetGroups.Count > 0)
        {
            ShowLayerFadeMaskMigrationNotice = true;
        }
        Version = 4;
    }
```

- [ ] **Step 3: `DecalProject.cs` Save/Load 映射新字段**

`DecalProject.cs` 的 `SaveToConfig`，在每个 layer 对应的 `new SavedLayer { ... }` 对象字面量中 **追加**：

```csharp
                    Kind = (int)l.Kind,
                    AffectsSpecular = l.AffectsSpecular,
                    AffectsRoughness = l.AffectsRoughness,
                    AffectsMetalness = l.AffectsMetalness,
                    AffectsSheen = l.AffectsSheen,
                    DiffuseColorR = l.DiffuseColor.X,
                    DiffuseColorG = l.DiffuseColor.Y,
                    DiffuseColorB = l.DiffuseColor.Z,
                    SpecularColorR = l.SpecularColor.X,
                    SpecularColorG = l.SpecularColor.Y,
                    SpecularColorB = l.SpecularColor.Z,
                    Roughness = l.Roughness,
                    Metalness = l.Metalness,
                    SheenRate = l.SheenRate,
                    SheenTint = l.SheenTint,
                    SheenAperture = l.SheenAperture,
```

`DecalProject.cs` 的 `LoadFromConfig`，在每个 `new DecalLayer { ... }` 对象字面量中 **追加**：

```csharp
                    Kind = (LayerKind)s.Kind,
                    AffectsSpecular = s.AffectsSpecular,
                    AffectsRoughness = s.AffectsRoughness,
                    AffectsMetalness = s.AffectsMetalness,
                    AffectsSheen = s.AffectsSheen,
                    DiffuseColor = new Vector3(s.DiffuseColorR, s.DiffuseColorG, s.DiffuseColorB),
                    SpecularColor = new Vector3(s.SpecularColorR, s.SpecularColorG, s.SpecularColorB),
                    Roughness = s.Roughness,
                    Metalness = s.Metalness,
                    SheenRate = s.SheenRate,
                    SheenTint = s.SheenTint,
                    SheenAperture = s.SheenAperture,
```

- [ ] **Step 4: 编译验证**

```bash
dotnet build -c Release
```

Expected: `Build succeeded. 0 Warning(s). 0 Error(s).`

- [ ] **Step 5: Commit**

```bash
git add SkinTatoo/SkinTatoo/Configuration.cs SkinTatoo/SkinTatoo/Core/DecalProject.cs
git commit -m "feat(pbr): Configuration/SavedLayer 加 PBR 字段 + version 升 4 + 迁移通告 flag"
```

---

## Task 3: RowPairAllocator

**Files:**
- Create: `SkinTatoo/SkinTatoo/Core/RowPairAllocator.cs`

- [ ] **Step 1: 新建 `RowPairAllocator.cs`**

```csharp
using System;
using System.Linq;

namespace SkinTatoo.Core;

/// <summary>
/// Per-TargetGroup allocator for ColorTable row pairs (0-15).
/// Scans vanilla normal.a to mark occupied slots, then hands out
/// unused slots on demand when a layer enables its first Affects* field.
/// </summary>
public class RowPairAllocator
{
    // occupied[i] tracks "vanilla uses row pair i"
    private readonly bool[] vanillaOccupied = new bool[16];
    // assigned[i] tracks "we've handed row pair i to a layer"
    private readonly bool[] assigned = new bool[16];

    private bool scanned;

    /// <summary>Whether vanilla scan has been performed (skip re-scan on reset).</summary>
    public bool Scanned => scanned;

    /// <summary>
    /// Scan vanilla normal.a histogram and mark high-frequency row pairs as occupied.
    /// Threshold: any row pair covering ≥0.5% of pixels is "vanilla occupied".
    /// Idempotent — safe to call multiple times on the same data.
    /// </summary>
    public void ScanVanillaOccupation(byte[] vanillaNormalRgba, int width, int height)
    {
        Array.Clear(vanillaOccupied, 0, 16);
        scanned = true;

        if (vanillaNormalRgba == null || vanillaNormalRgba.Length < 4) return;
        if (width <= 0 || height <= 0) return;

        var histogram = new int[16];
        int totalPixels = width * height;
        // Iterate alpha channel only
        for (int i = 3; i < vanillaNormalRgba.Length; i += 4)
        {
            int rowPair = (int)Math.Round(vanillaNormalRgba[i] / 17.0);
            if (rowPair >= 0 && rowPair < 16)
                histogram[rowPair]++;
        }

        // Threshold: row pair must cover ≥0.5% of pixels to count as "used"
        int threshold = Math.Max(1, totalPixels / 200);
        for (int i = 0; i < 16; i++)
        {
            if (histogram[i] > threshold)
                vanillaOccupied[i] = true;
        }
    }

    /// <summary>Allocate an unused row pair, or null if exhausted.</summary>
    public int? TryAllocate()
    {
        for (int i = 0; i < 16; i++)
        {
            if (!vanillaOccupied[i] && !assigned[i])
            {
                assigned[i] = true;
                return i;
            }
        }
        return null;
    }

    /// <summary>Release a previously-allocated row pair.</summary>
    public void Release(int rowPair)
    {
        if (rowPair >= 0 && rowPair < 16)
            assigned[rowPair] = false;
        // Vanilla-occupied entries are never released — we never allocated them.
    }

    /// <summary>Number of row pairs still available to allocate.</summary>
    public int AvailableSlots
    {
        get
        {
            int count = 0;
            for (int i = 0; i < 16; i++)
                if (!vanillaOccupied[i] && !assigned[i]) count++;
            return count;
        }
    }

    /// <summary>Total slots occupied by vanilla (informational).</summary>
    public int VanillaOccupiedCount => vanillaOccupied.Count(b => b);

    /// <summary>Reset all assignments but keep vanilla scan.</summary>
    public void ReleaseAll()
    {
        Array.Clear(assigned, 0, 16);
    }

    /// <summary>Fully reset including vanilla scan (e.g. on TargetGroup swap).</summary>
    public void Reset()
    {
        Array.Clear(vanillaOccupied, 0, 16);
        Array.Clear(assigned, 0, 16);
        scanned = false;
    }
}
```

- [ ] **Step 2: 编译验证**

```bash
dotnet build -c Release
```

Expected: `Build succeeded. 0 Warning(s). 0 Error(s).`

- [ ] **Step 3: Commit**

```bash
git add SkinTatoo/SkinTatoo/Core/RowPairAllocator.cs
git commit -m "feat(pbr): add RowPairAllocator — vanilla scan + on-demand 0-15 slot allocation"
```

---

## Task 4: TextureSwapService 抽出 ReadVanillaColorTable + ReplaceColorTableRaw

**Files:**
- Modify: `SkinTatoo/SkinTatoo/Interop/TextureSwapService.cs`

**目标**：把 `UpdateEmissiveViaColorTable` 内部的 read/write ColorTable 段抽成两个独立方法，方便 Task 8 的 full-table 写入复用。现有 emissive 单字段路径保持工作（fallback）。

- [ ] **Step 1: 加 `TryGetVanillaColorTable` 方法**

在 `TextureSwapService` 里 `UpdateEmissiveViaColorTable` 方法**上方**插入：

```csharp
    /// <summary>
    /// Copy the vanilla ColorTable bytes from a matched material's MaterialResourceHandle.
    /// Returns Half[] of length ctWidth*ctHeight*4, plus dimensions.
    /// Used by v1 PBR path to get a mutable baseline before writing back a full table.
    /// Returns false if no matching material or HasColorTable=false.
    /// </summary>
    public bool TryGetVanillaColorTable(CharacterBase* charBase, string mtrlGamePath,
        string? mtrlDiskPath, out Half[] data, out int width, out int height)
    {
        data = Array.Empty<Half>();
        width = 0;
        height = 0;

        if (!CanRead(charBase, 0x360)) return false;

        var slotCount = charBase->SlotCount;
        if (slotCount <= 0 || slotCount > 30) return false;

        var modelsPtr = charBase->Models;
        if (!CanRead(modelsPtr, slotCount * sizeof(nint))) return false;

        var normMtrl = mtrlGamePath.Replace('\\', '/').ToLowerInvariant();
        var normDisk = mtrlDiskPath?.Replace('\\', '/').ToLowerInvariant();

        for (int s = 0; s < slotCount; s++)
        {
            var model = modelsPtr[s];
            if (!CanRead(model, 0xA8)) continue;
            var matCount = model->MaterialCount;
            if (matCount <= 0 || matCount > 20) continue;
            var mats = model->Materials;
            if (!CanRead(mats, matCount * sizeof(nint))) continue;

            for (int m = 0; m < matCount; m++)
            {
                var mat = mats[m];
                if (!CanRead(mat, 0x58)) continue;
                var mtrlHandle = mat->MaterialResourceHandle;
                if (!CanRead(mtrlHandle, 0x100)) continue;

                string mtrlFileName;
                try { mtrlFileName = ((ResourceHandle*)mtrlHandle)->FileName.ToString(); }
                catch { continue; }
                if (string.IsNullOrEmpty(mtrlFileName)) continue;

                var normFileName = mtrlFileName.Replace('\\', '/').ToLowerInvariant();
                bool matched = normFileName.EndsWith(normMtrl) || normMtrl.EndsWith(normFileName);
                if (!matched && normDisk != null)
                    matched = normFileName.EndsWith(normDisk) || normDisk.EndsWith(normFileName)
                           || normFileName.Contains(Path.GetFileName(normDisk));
                if (!matched) continue;

                if (!mtrlHandle->HasColorTable) return false;
                var ctData = mtrlHandle->ColorTable;
                if (ctData == null) return false;

                var ctW = mtrlHandle->ColorTableWidth;
                var ctH = mtrlHandle->ColorTableHeight;
                if (ctW <= 0 || ctH <= 0) return false;

                int halfCount = ctW * ctH * 4;
                var copy = new Half[halfCount];
                fixed (Half* dst = copy)
                {
                    Buffer.MemoryCopy(ctData, dst, halfCount * sizeof(Half), halfCount * sizeof(Half));
                }
                data = copy;
                width = ctW;
                height = ctH;
                return true;
            }
        }
        return false;
    }
```

- [ ] **Step 2: 加 `ReplaceColorTableRaw` 方法**

在同一位置**继续插入**：

```csharp
    /// <summary>
    /// Replace a matched material's ColorTable GPU texture with a new full table.
    /// Input: Half[] of length width*height*4, where width = vec4 count per row (= texture width),
    /// height = row count. Performs an atomic slot swap via Interlocked.Exchange.
    /// Modeled after Glamourer's DirectXService.ReplaceColorTable.
    /// </summary>
    public bool ReplaceColorTableRaw(CharacterBase* charBase, string mtrlGamePath,
        string? mtrlDiskPath, Half[] data, int width, int height)
    {
        if (!CanRead(charBase, 0x360)) return false;
        if (data.Length != width * height * 4) return false;

        var slotCount = charBase->SlotCount;
        if (slotCount <= 0 || slotCount > 30) return false;
        var modelsPtr = charBase->Models;
        if (!CanRead(modelsPtr, slotCount * sizeof(nint))) return false;
        var ctTextures = charBase->ColorTableTextures;
        if (!CanRead(ctTextures, slotCount * CharacterBase.MaterialsPerSlot * sizeof(nint))) return false;

        var normMtrl = mtrlGamePath.Replace('\\', '/').ToLowerInvariant();
        var normDisk = mtrlDiskPath?.Replace('\\', '/').ToLowerInvariant();

        for (int s = 0; s < slotCount; s++)
        {
            var model = modelsPtr[s];
            if (!CanRead(model, 0xA8)) continue;
            var matCount = model->MaterialCount;
            if (matCount <= 0 || matCount > 20) continue;
            var mats = model->Materials;
            if (!CanRead(mats, matCount * sizeof(nint))) continue;

            for (int m = 0; m < matCount; m++)
            {
                var mat = mats[m];
                if (!CanRead(mat, 0x58)) continue;
                var mtrlHandle = mat->MaterialResourceHandle;
                if (!CanRead(mtrlHandle, 0x100)) continue;

                string mtrlFileName;
                try { mtrlFileName = ((ResourceHandle*)mtrlHandle)->FileName.ToString(); }
                catch { continue; }
                if (string.IsNullOrEmpty(mtrlFileName)) continue;

                var normFileName = mtrlFileName.Replace('\\', '/').ToLowerInvariant();
                bool matched = normFileName.EndsWith(normMtrl) || normMtrl.EndsWith(normFileName);
                if (!matched && normDisk != null)
                    matched = normFileName.EndsWith(normDisk) || normDisk.EndsWith(normFileName)
                           || normFileName.Contains(Path.GetFileName(normDisk));
                if (!matched) continue;

                if (!mtrlHandle->HasColorTable) continue;

                int flatIndex = s * CharacterBase.MaterialsPerSlot + m;
                var texSlot = &ctTextures[flatIndex];
                if (*texSlot == null)
                {
                    DebugServer.AppendLog($"[TextureSwap] ReplaceColorTableRaw: slot null Model[{s}]Mat[{m}]");
                    return false;
                }

                var newTex = GpuTexture.CreateTexture2D(
                    width, height, 1,
                    TexFormat.R16G16B16A16_FLOAT,
                    CreateFlags, 7);
                if (newTex == null)
                {
                    DebugServer.AppendLog("[TextureSwap] ReplaceColorTableRaw: CreateTexture2D failed");
                    return false;
                }

                fixed (Half* dataPtr = data)
                {
                    if (!newTex->InitializeContents(dataPtr))
                    {
                        newTex->DecRef();
                        DebugServer.AppendLog("[TextureSwap] ReplaceColorTableRaw: InitializeContents failed");
                        return false;
                    }
                }

                var oldPtr = Interlocked.Exchange(ref *(nint*)texSlot, (nint)newTex);
                if (oldPtr != 0)
                    ((GpuTexture*)oldPtr)->DecRef();

                DebugServer.AppendLog(
                    $"[TextureSwap] ColorTable full replace Model[{s}]Mat[{m}] " +
                    $"{width}x{height} old=0x{oldPtr:X} new=0x{(nint)newTex:X}");
                return true;
            }
        }

        return false;
    }
```

- [ ] **Step 3: 编译验证**

```bash
dotnet build -c Release
```

Expected: `Build succeeded. 0 Warning(s). 0 Error(s).`

现有的 `UpdateEmissiveViaColorTable` 不动——它是 v1 PBR 路径没打通前的兜底（skin.shpk 之外的 emissive-only 场景）。在 Task 8 集成完 ColorTableBuilder 后，我们评估是否让它走新 builder 路径。

- [ ] **Step 4: Commit**

```bash
git add SkinTatoo/SkinTatoo/Interop/TextureSwapService.cs
git commit -m "feat(pbr): TextureSwapService 抽出 TryGetVanillaColorTable + ReplaceColorTableRaw"
```

---

## Task 5: ColorTableBuilder

**Files:**
- Create: `SkinTatoo/SkinTatoo/Services/ColorTableBuilder.cs`

**目标**：纯函数式工具——输入 vanilla `Half[]` + layers（已分配 row pair）→ 输出覆盖后的 `Half[]`。不做 GPU I/O，可在后台线程跑。仅处理 Dawntrail 布局（ctWidth == 8）；legacy 布局直接返回 null，调用方要自己回退。

- [ ] **Step 1: 新建 `ColorTableBuilder.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Numerics;
using SkinTatoo.Core;

namespace SkinTatoo.Services;

/// <summary>
/// Pure-function builder: vanilla Half[] ColorTable + allocated layers → modified copy.
/// Handles Dawntrail 8-vec4-per-row layout only. Caller must check width == 8.
///
/// Row layout within a row pair (rowPairIdx 0-15):
///   row_lower = rowPairIdx * 2      → layer's overridden PBR
///   row_upper = rowPairIdx * 2 + 1  → vanilla baseline (for edge blend via normal.g)
///
/// Half offsets within each 32-Half row (Dawntrail, matches Penumbra ColorTableRow):
///   Diffuse:    [0][1][2]
///   Specular:   [4][5][6]
///   Emissive:   [8][9][10]
///   SheenRate:  [12]
///   SheenTint:  [13]   (single Half, NOT RGB)
///   SheenAper:  [14]
///   Roughness:  [16]
///   Metalness:  [18]
/// </summary>
public static class ColorTableBuilder
{
    public const int DawntrailRowPairs = 16;
    public const int DawntrailRowsPerTable = 32;
    public const int DawntrailVec4PerRow = 8;
    public const int DawntrailHalvesPerRow = DawntrailVec4PerRow * 4;  // 32

    // Half offsets within a row
    public const int OffDiffuseR = 0;
    public const int OffDiffuseG = 1;
    public const int OffDiffuseB = 2;
    public const int OffSpecularR = 4;
    public const int OffSpecularG = 5;
    public const int OffSpecularB = 6;
    public const int OffEmissiveR = 8;
    public const int OffEmissiveG = 9;
    public const int OffEmissiveB = 10;
    public const int OffSheenRate = 12;
    public const int OffSheenTint = 13;
    public const int OffSheenAperture = 14;
    public const int OffRoughness = 16;
    public const int OffMetalness = 18;

    /// <summary>
    /// Returns true if the given vanilla ColorTable dimensions are Dawntrail PBR-capable.
    /// Legacy (ctWidth==4, ctHeight==16) or other shapes are not supported by v1.
    /// </summary>
    public static bool IsDawntrailLayout(int ctWidth, int ctHeight)
        => ctWidth == DawntrailVec4PerRow && ctHeight == DawntrailRowsPerTable;

    /// <summary>
    /// Clone the vanilla table and overwrite allocated row pairs with each layer's PBR.
    /// - Row pair lower row: vanilla baseline (we use vanilla row 0 as fallback).
    /// - Row pair upper row: layer PBR override (layer value where Affects*=true, vanilla-baseline otherwise).
    /// NOTE: Row lower/upper swap vs spec wording — Penumbra MaterialExporter:136 shows
    /// rowBlend = 1 - normal.g/255, and we write normal.g = weight. So weight=1 (full layer)
    /// corresponds to rowBlend=0 → reads table[rowPair*2]. Layer override MUST live at row lower
    /// (rowPair*2), vanilla baseline at row upper (rowPair*2+1). This builder follows that.
    /// </summary>
    public static Half[] Build(Half[] vanillaTable, int ctWidth, int ctHeight,
        IReadOnlyList<DecalLayer> allocatedLayers)
    {
        if (!IsDawntrailLayout(ctWidth, ctHeight))
            throw new ArgumentException($"ColorTableBuilder only supports Dawntrail 8x32 layout, got {ctWidth}x{ctHeight}");
        if (vanillaTable.Length < ctWidth * ctHeight * 4)
            throw new ArgumentException("vanillaTable buffer too small");

        var table = (Half[])vanillaTable.Clone();

        foreach (var layer in allocatedLayers)
        {
            int pair = layer.AllocatedRowPair;
            if (pair < 0 || pair >= DawntrailRowPairs) continue;
            if (!layer.RequiresRowPair) continue;

            int rowLower = pair * 2;         // layer override
            int rowUpper = pair * 2 + 1;     // vanilla baseline (= vanilla row 0)

            int lowerBase = rowLower * DawntrailHalvesPerRow;
            int upperBase = rowUpper * DawntrailHalvesPerRow;
            int vanillaRow0Base = 0;  // simplified fallback per spec

            // Upper row = pure vanilla baseline
            for (int i = 0; i < DawntrailHalvesPerRow; i++)
                table[upperBase + i] = vanillaTable[vanillaRow0Base + i];

            // Lower row = vanilla baseline + per-field override
            for (int i = 0; i < DawntrailHalvesPerRow; i++)
                table[lowerBase + i] = vanillaTable[vanillaRow0Base + i];

            if (layer.AffectsDiffuse)
            {
                table[lowerBase + OffDiffuseR] = (Half)layer.DiffuseColor.X;
                table[lowerBase + OffDiffuseG] = (Half)layer.DiffuseColor.Y;
                table[lowerBase + OffDiffuseB] = (Half)layer.DiffuseColor.Z;
            }
            if (layer.AffectsSpecular)
            {
                table[lowerBase + OffSpecularR] = (Half)layer.SpecularColor.X;
                table[lowerBase + OffSpecularG] = (Half)layer.SpecularColor.Y;
                table[lowerBase + OffSpecularB] = (Half)layer.SpecularColor.Z;
            }
            if (layer.AffectsEmissive)
            {
                var em = layer.EmissiveColor * layer.EmissiveIntensity;
                table[lowerBase + OffEmissiveR] = (Half)em.X;
                table[lowerBase + OffEmissiveG] = (Half)em.Y;
                table[lowerBase + OffEmissiveB] = (Half)em.Z;
            }
            if (layer.AffectsRoughness)
                table[lowerBase + OffRoughness] = (Half)layer.Roughness;
            if (layer.AffectsMetalness)
                table[lowerBase + OffMetalness] = (Half)layer.Metalness;
            if (layer.AffectsSheen)
            {
                table[lowerBase + OffSheenRate] = (Half)layer.SheenRate;
                table[lowerBase + OffSheenTint] = (Half)layer.SheenTint;
                table[lowerBase + OffSheenAperture] = (Half)layer.SheenAperture;
            }
        }

        return table;
    }
}
```

- [ ] **Step 2: 编译验证**

```bash
dotnet build -c Release
```

Expected: `Build succeeded. 0 Warning(s). 0 Error(s).`

- [ ] **Step 3: Commit**

```bash
git add SkinTatoo/SkinTatoo/Services/ColorTableBuilder.cs
git commit -m "feat(pbr): add ColorTableBuilder — vanilla + layers → overridden Half[] table"
```

---

## Task 6: PreviewService 持 RowPairAllocator + 生命周期 API

**Files:**
- Modify: `SkinTatoo/SkinTatoo/Services/PreviewService.cs`

**目标**：PreviewService 管 `TargetGroup → RowPairAllocator` 映射，提供从 UI 调的 `AllocateRowPairIfNeeded` / `ReleaseRowPairIfUnused` 钩子，并在 `ResetSwapState` / `Dispose` 时清理。

- [ ] **Step 1: 加 allocator 字典 + 导入命名空间**

在 `PreviewService.cs` 顶部 `using` 列表加（如果还没有）：

```csharp
using System.Linq;
```

在 `PreviewService` 类的字段区（`previewMtrlDiskPaths` 附近），追加：

```csharp
    // v1 PBR: per-TargetGroup row pair allocators (keyed by MtrlGamePath)
    private readonly ConcurrentDictionary<string, RowPairAllocator> rowPairAllocators =
        new(StringComparer.OrdinalIgnoreCase);

    // Cached vanilla ColorTable bytes per material (keyed by MtrlGamePath).
    // Populated on first ColorTable write from the main thread; background composite
    // reads from here to avoid cross-thread GPU access.
    private readonly ConcurrentDictionary<string, (Half[] Data, int Width, int Height)> vanillaColorTables =
        new(StringComparer.OrdinalIgnoreCase);
```

- [ ] **Step 2: 加公共 allocator API**

在 `PreviewService.ClearEmissiveHookTargets()` 方法**上方**插入：

```csharp
    /// <summary>Get (or lazily create) the row pair allocator for a target group.</summary>
    public RowPairAllocator GetOrCreateAllocator(TargetGroup group)
    {
        var key = group.MtrlGamePath ?? group.Name;
        return rowPairAllocators.GetOrAdd(key, _ => new RowPairAllocator());
    }

    /// <summary>
    /// Called from UI when a layer needs a row pair (first Affects* toggled on).
    /// Returns true on success; false on exhaustion (caller must toast and revert the toggle).
    /// </summary>
    public bool TryAllocateRowPairForLayer(TargetGroup group, DecalLayer layer)
    {
        if (layer.AllocatedRowPair >= 0) return true;   // already has one
        var alloc = GetOrCreateAllocator(group);
        var slot = alloc.TryAllocate();
        if (slot == null)
        {
            DebugServer.AppendLog(
                $"[PreviewService] Row pair exhausted for {group.Name} " +
                $"(available={alloc.AvailableSlots}, vanilla={alloc.VanillaOccupiedCount})");
            return false;
        }
        layer.AllocatedRowPair = slot.Value;
        DebugServer.AppendLog($"[PreviewService] Allocated row pair {slot.Value} to layer '{layer.Name}'");
        return true;
    }

    /// <summary>
    /// Release this layer's row pair if it no longer requires one.
    /// Call after toggling off Affects* fields or before deleting a layer.
    /// </summary>
    public void ReleaseRowPairIfUnused(TargetGroup group, DecalLayer layer)
    {
        if (layer.AllocatedRowPair < 0) return;
        if (layer.RequiresRowPair) return;  // still needed

        var alloc = GetOrCreateAllocator(group);
        alloc.Release(layer.AllocatedRowPair);
        DebugServer.AppendLog($"[PreviewService] Released row pair {layer.AllocatedRowPair} from layer '{layer.Name}'");
        layer.AllocatedRowPair = -1;
    }

    /// <summary>Force-release this layer's row pair (e.g. on layer deletion).</summary>
    public void ForceReleaseRowPair(TargetGroup group, DecalLayer layer)
    {
        if (layer.AllocatedRowPair < 0) return;
        var alloc = GetOrCreateAllocator(group);
        alloc.Release(layer.AllocatedRowPair);
        layer.AllocatedRowPair = -1;
    }
```

- [ ] **Step 3: 清理在 `ResetSwapState` / `Dispose`**

找到 `ResetSwapState` 方法，在 `emissiveHook?.ClearTargets();` 行**之前**追加：

```csharp
        rowPairAllocators.Clear();
        vanillaColorTables.Clear();
```

在 `Dispose` 方法内，同样在 `emissiveHook?.ClearTargets();` **之前**追加：

```csharp
        rowPairAllocators.Clear();
        vanillaColorTables.Clear();
```

- [ ] **Step 4: 编译验证**

```bash
dotnet build -c Release
```

Expected: `Build succeeded. 0 Warning(s). 0 Error(s).`

- [ ] **Step 5: Commit**

```bash
git add SkinTatoo/SkinTatoo/Services/PreviewService.cs
git commit -m "feat(pbr): PreviewService 持 RowPairAllocator 字典 + 分配/释放 API"
```

---

## Task 7: PreviewService — Normal map 重写（row pair + weight）

**Files:**
- Modify: `SkinTatoo/SkinTatoo/Services/PreviewService.cs`

**目标**：把 `CompositeEmissiveNorm` 改造为 `CompositeNormalPbr`——保留 vanilla R/B 通道，A 通道写 row pair * 17，G 通道写 `255 * (1 - png_alpha * fade_mask_weight)`（符合 `rowBlend = 1 - normal.g/255` 约定：weight=1 → rowBlend=0 → 读 lower row）。

- [ ] **Step 1: 新加 `CompositeNormalPbr` 方法**

在 `PreviewService.cs` 的 `CompositeEmissiveNorm` **下方**（或旁边）新加方法——**不要**删除 `CompositeEmissiveNorm`，v1 PBR 成功后才能废弃它：

```csharp
    /// <summary>
    /// v1 PBR normal rewrite: keep vanilla R/B, overwrite A with row pair index*17
    /// and G with (1 - png_alpha * fade_mask_weight) * 255 for Penumbra row blend.
    /// Only layers with AllocatedRowPair >= 0 participate (others are still painted via diffuse).
    /// </summary>
    private byte[]? CompositeNormalPbr(List<DecalLayer> allocatedLayers, string normDiskPath, int w, int h)
    {
        byte[] baseNorm;
        if (baseTextureCache.TryGetValue(normDiskPath, out var cachedNorm))
        {
            var (data, iw, ih) = cachedNorm;
            baseNorm = (iw != w || ih != h) ? ResizeBilinear(data, iw, ih, w, h) : (byte[])data.Clone();
        }
        else
        {
            var normImg = File.Exists(normDiskPath) ? imageLoader.LoadImage(normDiskPath) : LoadGameTexture(normDiskPath);
            if (normImg == null) return null;
            baseTextureCache[normDiskPath] = normImg.Value;
            var (data, iw, ih) = normImg.Value;
            baseNorm = (iw != w || ih != h) ? ResizeBilinear(data, iw, ih, w, h) : (byte[])data.Clone();
        }

        var output = (byte[])baseNorm.Clone();
        bool anyWritten = false;

        // z-order: iterate layers front-to-back so later layers overwrite earlier ones
        foreach (var layer in allocatedLayers)
        {
            if (!layer.IsVisible) continue;
            if (layer.AllocatedRowPair < 0) continue;

            byte rowPairByte = (byte)Math.Clamp(layer.AllocatedRowPair * 17, 0, 255);

            if (layer.Kind == LayerKind.WholeMaterial)
            {
                // Uniform coverage: every pixel is fully in this layer
                for (int py = 0; py < h; py++)
                {
                    for (int px = 0; px < w; px++)
                    {
                        int oIdx = (py * w + px) * 4;
                        // weight = 1.0 → G = 0 (rowBlend = 1 - 0 = 1... WRONG, see below)
                        // Penumbra: rowBlend = 1 - g/255, and lower row index = rowPair*2 = "our override row"
                        // Per MaterialExporter: prevRow = table[rowPair*2], nextRow = table[rowPair*2+1]
                        // lerpedX = lerp(prevRow.X, nextRow.X, rowBlend)
                        // We want weight=1 → use OUR override (row lower = prevRow) → rowBlend = 0 → g = 255
                        // weight=0 → use vanilla baseline (row upper = nextRow) → rowBlend = 1 → g = 0
                        output[oIdx + 3] = rowPairByte;
                        output[oIdx + 1] = 255;  // rowBlend = 0 → fully use layer override
                    }
                }
                anyWritten = true;
                continue;
            }

            // Decal kind
            if (string.IsNullOrEmpty(layer.ImagePath)) continue;
            var decalImage = imageLoader.LoadImage(layer.ImagePath);
            if (decalImage == null) continue;
            var (decalData, decalW, decalH) = decalImage.Value;

            var center = layer.UvCenter;
            var scale = layer.UvScale;
            var opacity = layer.Opacity;
            var rotRad = layer.RotationDeg * (MathF.PI / 180f);
            var cosR = MathF.Cos(rotRad);
            var sinR = MathF.Sin(rotRad);

            int pxMin = Math.Max(0, (int)((center.X - scale.X / 2f) * w));
            int pxMax = Math.Min(w - 1, (int)((center.X + scale.X / 2f) * w));
            int pyMin = Math.Max(0, (int)((center.Y - scale.Y / 2f) * h));
            int pyMax = Math.Min(h - 1, (int)((center.Y + scale.Y / 2f) * h));

            for (int py = pyMin; py <= pyMax; py++)
            {
                for (int px = pxMin; px <= pxMax; px++)
                {
                    float u = (px + 0.5f) / w;
                    float v = (py + 0.5f) / h;
                    float lu = (u - center.X) / scale.X;
                    float lv = (v - center.Y) / scale.Y;
                    float ru = lu * cosR + lv * sinR;
                    float rv = -lu * sinR + lv * cosR;
                    if (ru < -0.5f || ru > 0.5f || rv < -0.5f || rv > 0.5f) continue;
                    switch (layer.Clip)
                    {
                        case ClipMode.ClipLeft when ru < 0f: continue;
                        case ClipMode.ClipRight when ru >= 0f: continue;
                        case ClipMode.ClipTop when rv < 0f: continue;
                        case ClipMode.ClipBottom when rv >= 0f: continue;
                    }

                    float du = (ru + 0.5f) * decalW - 0.5f;
                    float dv = (rv + 0.5f) * decalH - 0.5f;
                    SampleBilinear(decalData, decalW, decalH, du, dv, out _, out _, out _, out float da);
                    da *= opacity;
                    if (da < 0.001f) continue;

                    float weight;
                    if (layer.FadeMask == LayerFadeMask.DirectionalGradient)
                        weight = ComputeDirectionalGradient(ru, rv, da,
                            layer.GradientAngleDeg, layer.GradientScale, layer.FadeMaskFalloff, layer.GradientOffset);
                    else if (layer.FadeMask == LayerFadeMask.ShapeOutline)
                    {
                        float sum = 0; int cnt = 0;
                        for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0) continue;
                            float ndu = du + dx; float ndv = dv + dy;
                            SampleBilinear(decalData, decalW, decalH, ndu, ndv, out _, out _, out _, out float na);
                            sum += na * opacity; cnt++;
                        }
                        weight = ComputeShapeOutline(da, layer.FadeMaskFalloff, sum / cnt);
                    }
                    else
                        weight = ComputeFadeMaskWeight(layer.FadeMask, layer.FadeMaskFalloff, ru, rv, da);

                    weight = Math.Clamp(weight, 0f, 1f);
                    if (weight <= 0.001f) continue;

                    int oIdx = (py * w + px) * 4;
                    output[oIdx + 3] = rowPairByte;                                  // .a = row pair * 17
                    output[oIdx + 1] = (byte)Math.Clamp((int)(weight * 255), 0, 255); // .g = weight (rowBlend = 1 - g/255)
                    anyWritten = true;
                }
            }
        }

        return anyWritten ? output : null;
    }
```

**为什么 WholeMaterial 分支的注释写那么长**：normal.g 的语义容易搞反。Penumbra `MaterialExporter.cs:137` 的公式是 `rowBlend = 1 - indexPixel.G / 255`；而 `prevRow = table[tablePair*2]`, `nextRow = table[tablePair*2+1]`, `lerped = lerp(prev, next, rowBlend)`。我们把 layer override 放在 `rowLower = pair*2`（即 prevRow），vanilla baseline 放在 `rowUpper = pair*2+1`（即 nextRow）。weight=1 表示"完全用 layer override" → rowBlend 应 = 0 → g = 255。weight=0 表示"完全 vanilla" → rowBlend = 1 → g = 0。所以写入公式是 `g = weight * 255`，和直觉相反（你可能以为 weight=1 会让"更多权重往 layer 上堆"意味着 g 大，但 g 实际上是"layer 那一侧的权重 100% 时等于 255"）。ColorTableBuilder 的注释必须和这里保持一致。

- [ ] **Step 2: 编译验证**

```bash
dotnet build -c Release
```

Expected: `Build succeeded. 0 Warning(s). 0 Error(s).`

- [ ] **Step 3: Commit**

```bash
git add SkinTatoo/SkinTatoo/Services/PreviewService.cs
git commit -m "feat(pbr): PreviewService.CompositeNormalPbr 写 row pair + rowBlend weight"
```

---

## Task 8: PreviewService — SwapBatch 扩三元组 + ColorTable 集成 + ApplyPendingSwaps 更新

**Files:**
- Modify: `SkinTatoo/SkinTatoo/Services/PreviewService.cs`

**目标**：后台线程同时产出 `diffuse` / `normal` / `colorTable` 三件；主线程 `ApplyPendingSwaps` 按顺序原子提交；首次 ColorTable 读取在主线程缓存到 `vanillaColorTables` 字典。

- [ ] **Step 1: 扩展 SwapBatch 记录**

找到 `PreviewService.cs` 里的 `SwapBatchEntry` / `EmissiveEntry` / `SwapBatch` 三个 `record` 定义（`PreviewService.cs:53-55` 附近），**追加**新记录（保留旧的）：

```csharp
    private record ColorTableEntry(string MtrlGamePath, string? MtrlDiskPath, Half[] Data, int Width, int Height);
```

把 `SwapBatch` 改成：

```csharp
    private record SwapBatch(List<SwapBatchEntry> Textures, List<EmissiveEntry> Emissives, List<ColorTableEntry> ColorTables);
```

所有 `new SwapBatch(texEntries, emEntries)` 的构造点（`StartAsyncInPlace` 内部）都改成 `new SwapBatch(texEntries, emEntries, ctEntries)`，并在上方 `var ctEntries = new List<ColorTableEntry>();`。

- [ ] **Step 2: 在 StartAsyncInPlace 产出 ColorTable entry**

在 `StartAsyncInPlace` 的 Task.Run 主循环里，`foreach (var job in jobs)` 内部，在 `CompositeEmissiveNorm` 调用**之后**（或替代它）、emissive entry 追加**之前**插入新块：

```csharp
                    // v1 PBR: compute allocated layers, rewrite normal, build ColorTable
                    var allocatedLayers = layers.Where(l => l.AllocatedRowPair >= 0 && l.IsVisible).ToList();
                    bool hasPbrLayers = allocatedLayers.Any();

                    if (hasPbrLayers && !string.IsNullOrEmpty(job.NormDiskPath))
                    {
                        var normRgba = CompositeNormalPbr(allocatedLayers, job.NormDiskPath!, baseTex.Width, baseTex.Height);
                        if (normRgba != null)
                        {
                            previewDiskPaths.TryGetValue(job.NormGamePath ?? "", out var normDiskOut);
                            if (normDiskOut != null)
                                WriteBgraTexFile(normDiskOut, normRgba, baseTex.Width, baseTex.Height);
                            var normBgra = TextureSwapService.RgbaToBgra(normRgba);
                            texEntries.Add(new SwapBatchEntry(
                                job.NormGamePath!, normDiskOut, normBgra, baseTex.Width, baseTex.Height));
                        }
                    }

                    if (hasPbrLayers && !string.IsNullOrEmpty(job.MtrlGamePath)
                        && vanillaColorTables.TryGetValue(job.MtrlGamePath!, out var vanilla)
                        && ColorTableBuilder.IsDawntrailLayout(vanilla.Width, vanilla.Height))
                    {
                        var modified = ColorTableBuilder.Build(vanilla.Data, vanilla.Width, vanilla.Height, allocatedLayers);
                        var mtrlDisk = previewMtrlDiskPaths.GetValueOrDefault(job.MtrlGamePath!);
                        ctEntries.Add(new ColorTableEntry(
                            job.MtrlGamePath!, mtrlDisk, modified, vanilla.Width, vanilla.Height));
                    }
```

**关键**：保留旧 emissive entry 构造（紧邻下方），它是 skin.shpk 兜底路径，PBR 独立化完成后仍需要它处理"没有 ColorTable 的材质只有 emissive"的场景。但加一个 guard：**如果本材质有 ColorTable 条目，跳过 emissive entry 构造**（避免双写冲突）：

在紧邻下方旧的 `if (HasEmissiveLayers(job.Layers) && !string.IsNullOrEmpty(job.NormDiskPath)) { ... }` 块前面加守卫：

```csharp
                    // Skin.shpk fallback path: only if no ColorTable entry was queued for this mtrl
                    bool ctQueued = hasPbrLayers && !string.IsNullOrEmpty(job.MtrlGamePath)
                        && vanillaColorTables.ContainsKey(job.MtrlGamePath!);
                    if (!ctQueued && HasEmissiveLayers(job.Layers) && !string.IsNullOrEmpty(job.NormDiskPath))
                    {
                        // ... existing CompositeEmissiveNorm + emEntries.Add ...
                    }
```

- [ ] **Step 3: 主线程缓存 vanilla ColorTable**

在 `ApplyPendingSwaps` 方法里，在 `foreach (var em in batch.Emissives)` **之前**插入 ColorTable 应用段 + 新增 vanilla 缓存尝试。完整改造版（替换整个方法体）：

```csharp
    public unsafe void ApplyPendingSwaps()
    {
        var batch = Interlocked.Exchange(ref pendingBatch, null);
        if (batch == null) return;

        var charBase = textureSwap?.GetLocalPlayerCharacterBase();
        if (charBase == null) return;

        foreach (var entry in batch.Textures)
        {
            var slot = textureSwap!.FindTextureSlot(charBase, entry.GamePath, entry.DiskPath);
            if (slot != null)
                textureSwap.SwapTexture(slot, entry.BgraData, entry.Width, entry.Height);
        }

        foreach (var ct in batch.ColorTables)
        {
            textureSwap!.ReplaceColorTableRaw(charBase, ct.MtrlGamePath, ct.MtrlDiskPath, ct.Data, ct.Width, ct.Height);
        }

        foreach (var em in batch.Emissives)
        {
            // Legacy single-emissive path: only used when ColorTable path did not fire
            textureSwap!.UpdateEmissiveViaColorTable(charBase, em.MtrlGamePath, em.MtrlDiskPath, em.Color);
            if (emissiveHook != null && em.CBufferOffset > 0)
                emissiveHook.SetTargetByPath(charBase, em.MtrlGamePath, em.MtrlDiskPath, em.Color);
        }

        LastUpdateMode = "inplace";
    }
```

- [ ] **Step 4: 主线程首次缓存 vanilla ColorTable + 扫描 vanilla normal**

在 `UpdatePreviewFull` 方法内，`foreach (var (gamePath, diskPath) in redirects)` 循环**之后**（即已完成 `RedrawPlayer`），插入：

```csharp
            // v1 PBR: cache vanilla ColorTable + scan vanilla normal.a for each managed mtrl.
            // Must run after RedrawPlayer so the MaterialResourceHandle exists.
            foreach (var group in project.Groups)
            {
                if (string.IsNullOrEmpty(group.MtrlGamePath)) continue;
                if (vanillaColorTables.ContainsKey(group.MtrlGamePath!)) continue;

                var charBase = textureSwap?.GetLocalPlayerCharacterBase();
                if (charBase == null) continue;

                if (textureSwap!.TryGetVanillaColorTable(
                        charBase, group.MtrlGamePath!, group.OrigMtrlDiskPath ?? group.MtrlDiskPath,
                        out var ctData, out var ctW, out var ctH))
                {
                    vanillaColorTables[group.MtrlGamePath!] = (ctData, ctW, ctH);
                    DebugServer.AppendLog($"[PreviewService] Cached vanilla ColorTable {ctW}x{ctH} for {group.MtrlGamePath}");
                }

                // Vanilla normal.a histogram scan for allocator
                var normDisk = group.OrigNormDiskPath ?? group.NormDiskPath;
                if (!string.IsNullOrEmpty(normDisk))
                {
                    var alloc = GetOrCreateAllocator(group);
                    if (!alloc.Scanned)
                    {
                        var (baseData, baseW, baseH) = LoadBaseTexture(group);
                        var normImg = File.Exists(normDisk) ? imageLoader.LoadImage(normDisk) : LoadGameTexture(normDisk);
                        if (normImg != null)
                        {
                            var (normData, normW, normH) = normImg.Value;
                            alloc.ScanVanillaOccupation(normData, normW, normH);
                            DebugServer.AppendLog(
                                $"[PreviewService] Vanilla row scan: {group.Name} " +
                                $"vanillaOccupied={alloc.VanillaOccupiedCount}, available={alloc.AvailableSlots}");
                        }
                    }
                }
            }
```

- [ ] **Step 5: 编译验证**

```bash
dotnet build -c Release
```

Expected: `Build succeeded. 0 Warning(s). 0 Error(s).`

- [ ] **Step 6: Commit**

```bash
git add SkinTatoo/SkinTatoo/Services/PreviewService.cs
git commit -m "feat(pbr): SwapBatch 扩三元组 + ColorTable 集成 + vanilla scan/cache 主线程路径"
```

---

## Task 9: MainWindow — LayerKind 创建按钮 + 隐藏 UV 控件

**Files:**
- Modify: `SkinTatoo/SkinTatoo/Gui/MainWindow.cs`

- [ ] **Step 1: 找到现有的"新建图层"按钮**

搜 `AddLayer(` 调用点。典型在图层列表工具栏（grep `新建贴花` 或 `"+"` 按钮）。在那一段**紧邻**旧按钮下方加"新建材质层"按钮：

```csharp
            ImGui.SameLine();
            if (ImGui.Button("+ 材质层##addWholeMat"))
            {
                var name = $"材质层 {group.Layers.Count + 1}";
                var layer = group.AddLayer(name, LayerKind.WholeMaterial);
                layer.AffectsDiffuse = false;  // WholeMaterial 默认不影响贴图，用户主动开 PBR
                MarkPreviewDirty();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("添加整张材质作为图层（无 UV 变换，可调 PBR）");
```

- [ ] **Step 2: 在贴花变换 UI 外面包一层 `Kind == Decal` 检测**

找到"贴花变换"/`CollapsingHeader("变换")` 或"UV 中心"那一段（MainWindow 约在 1130-1200 行，根据 Task 1 rename 之后行号略移）。整个 UV 控件 block 用以下包裹：

```csharp
            if (layer.Kind == LayerKind.Decal)
            {
                if (ImGui.CollapsingHeader("变换", ImGuiTreeNodeFlags.DefaultOpen))
                {
                    // ... existing UV center / scale / rotation / image path / blend / clip controls ...
                }
            }
```

（只把"变换"折叠头及其内容包起来；不包"渲染"折叠头。）

- [ ] **Step 3: 隐藏 ImagePath / BlendMode / Clip 对 WholeMaterial 不适用**

上一 Step 已经把整个"变换"folder 包住，这里确认 `BlendMode` / `ClipMode` / `ImagePath` 三个控件不出现在 WholeMaterial 图层。如果它们在另一个 folder 里，额外包一层 `if (layer.Kind == LayerKind.Decal) { ... }`。

- [ ] **Step 4: `ResetSelectedLayer` 保留 Kind**

找到 `ResetSelectedLayer` 方法（`MainWindow.cs:2019-2040` 附近）。在创建 `new DecalLayer();` **之前**保存 `var kind = layer.Kind;`，在整个 reset 完成**之后**回写 `layer.Kind = kind;`，并同步重置新加的 PBR 字段：

```csharp
    private void ResetSelectedLayer()
    {
        var layer = project.SelectedLayer;
        if (layer == null) return;
        var savedKind = layer.Kind;
        var d = new DecalLayer();
        layer.Kind = savedKind;   // preserve layer type
        layer.UvCenter = d.UvCenter;
        layer.UvScale = d.UvScale;
        layer.RotationDeg = d.RotationDeg;
        layer.Opacity = d.Opacity;
        layer.BlendMode = d.BlendMode;
        layer.Clip = d.Clip;
        layer.IsVisible = d.IsVisible;
        layer.AffectsDiffuse = d.AffectsDiffuse;
        layer.AffectsSpecular = d.AffectsSpecular;
        layer.AffectsEmissive = d.AffectsEmissive;
        layer.AffectsRoughness = d.AffectsRoughness;
        layer.AffectsMetalness = d.AffectsMetalness;
        layer.AffectsSheen = d.AffectsSheen;
        layer.DiffuseColor = d.DiffuseColor;
        layer.SpecularColor = d.SpecularColor;
        layer.EmissiveColor = d.EmissiveColor;
        layer.EmissiveIntensity = d.EmissiveIntensity;
        layer.Roughness = d.Roughness;
        layer.Metalness = d.Metalness;
        layer.SheenRate = d.SheenRate;
        layer.SheenTint = d.SheenTint;
        layer.SheenAperture = d.SheenAperture;
        layer.FadeMask = d.FadeMask;
        layer.FadeMaskFalloff = d.FadeMaskFalloff;
        layer.GradientAngleDeg = d.GradientAngleDeg;
        layer.GradientScale = d.GradientScale;
        layer.GradientOffset = d.GradientOffset;
    }
```

- [ ] **Step 5: 编译验证**

```bash
dotnet build -c Release
```

Expected: `Build succeeded. 0 Warning(s). 0 Error(s).`

- [ ] **Step 6: Commit**

```bash
git add SkinTatoo/SkinTatoo/Gui/MainWindow.cs
git commit -m "feat(pbr): MainWindow 加材质层按钮 + 按 Kind 隐藏 UV 控件 + ResetSelectedLayer 覆盖新字段"
```

---

## Task 10: MainWindow — PBR 字段面板 + 行号分配 UX

**Files:**
- Modify: `SkinTatoo/SkinTatoo/Gui/MainWindow.cs`

- [ ] **Step 1: 加 toast / banner state**

在 `MainWindow` 类字段区添加（`previewDirty` 附近）：

```csharp
    // v1 PBR: row pair exhaustion toast
    private string? rowPairToast;
    private DateTime rowPairToastUntil;
```

- [ ] **Step 2: 在"渲染"折叠头**（或独立新增"PBR 属性"CollapsingHeader）**末尾**追加 PBR 面板

定位到"发光"复选框逻辑段（`MainWindow.cs:1242-1322` 附近，Task 1 rename 后）。**替换**该段以拓展为完整 PBR 块：

```csharp
                // ── PBR 属性 (v1) ────────────────────────────────
                if (ImGui.CollapsingHeader("PBR 属性", ImGuiTreeNodeFlags.DefaultOpen))
                {
                    var alloc = previewService.GetOrCreateAllocator(group);
                    bool exhausted = alloc.AvailableSlots == 0 && layer.AllocatedRowPair < 0;

                    if (exhausted)
                    {
                        ImGui.TextColored(new Vector4(1f, 0.6f, 0.2f, 1f),
                            $"⚠ 该材质 PBR 行号已满（vanilla 占 {alloc.VanillaOccupiedCount}，已分 {16 - alloc.AvailableSlots - alloc.VanillaOccupiedCount}）");
                        ImGui.TextDisabled("请关闭其他图层的 PBR 字段后再试");
                    }

                    DrawPbrCheckbox(group, layer, "漫反射",
                        () => layer.AffectsDiffuse, v => layer.AffectsDiffuse = v, exhausted);
                    if (layer.AffectsDiffuse)
                    {
                        var d = layer.DiffuseColor;
                        if (ImGui.ColorEdit3("##diffColor", ref d, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoLabel))
                        { layer.DiffuseColor = d; MarkPreviewDirty(); }
                    }

                    DrawPbrCheckbox(group, layer, "镜面反射",
                        () => layer.AffectsSpecular, v => layer.AffectsSpecular = v, exhausted);
                    if (layer.AffectsSpecular)
                    {
                        var sc = layer.SpecularColor;
                        if (ImGui.ColorEdit3("##specColor", ref sc, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoLabel))
                        { layer.SpecularColor = sc; MarkPreviewDirty(); }
                    }

                    DrawPbrCheckbox(group, layer, "发光",
                        () => layer.AffectsEmissive, v => layer.AffectsEmissive = v, exhausted);
                    if (layer.AffectsEmissive)
                    {
                        var emColor = layer.EmissiveColor;
                        if (ImGui.ColorEdit3("##emColor", ref emColor, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoLabel))
                        { layer.EmissiveColor = emColor; MarkPreviewDirty(); TryDirectEmissiveUpdate(group, layer); }
                        ImGui.SameLine(); ImGui.Text("强度");
                        ImGui.SetNextItemWidth(80);
                        var emI = layer.EmissiveIntensity;
                        if (ImGui.DragFloat("##emI", ref emI, 0.05f, 0.1f, 10f, "%.2f"))
                        { layer.EmissiveIntensity = emI; MarkPreviewDirty(); TryDirectEmissiveUpdate(group, layer); }
                    }

                    DrawPbrCheckbox(group, layer, "粗糙度",
                        () => layer.AffectsRoughness, v => layer.AffectsRoughness = v, exhausted);
                    if (layer.AffectsRoughness)
                    {
                        var r = layer.Roughness;
                        if (ImGui.SliderFloat("##rough", ref r, 0f, 1f, "%.2f"))
                        { layer.Roughness = r; MarkPreviewDirty(); }
                    }

                    DrawPbrCheckbox(group, layer, "金属度",
                        () => layer.AffectsMetalness, v => layer.AffectsMetalness = v, exhausted);
                    if (layer.AffectsMetalness)
                    {
                        var mt = layer.Metalness;
                        if (ImGui.SliderFloat("##metal", ref mt, 0f, 1f, "%.2f"))
                        { layer.Metalness = mt; MarkPreviewDirty(); }
                    }

                    DrawPbrCheckbox(group, layer, "光泽",
                        () => layer.AffectsSheen, v => layer.AffectsSheen = v, exhausted);
                    if (layer.AffectsSheen)
                    {
                        var sr = layer.SheenRate;
                        if (ImGui.SliderFloat("Rate##sheenRate", ref sr, 0f, 1f, "%.2f"))
                        { layer.SheenRate = sr; MarkPreviewDirty(); }
                        var st = layer.SheenTint;
                        if (ImGui.SliderFloat("Tint##sheenTint", ref st, 0f, 1f, "%.2f"))
                        { layer.SheenTint = st; MarkPreviewDirty(); }
                        var sa = layer.SheenAperture;
                        if (ImGui.SliderFloat("Apt##sheenAp", ref sa, 0f, 20f, "%.2f"))
                        { layer.SheenAperture = sa; MarkPreviewDirty(); }
                    }

                    // Layer fade mask (all PBR fields fade together now per v1 spec)
                    ImGui.Separator();
                    ImGui.TextDisabled("图层羽化（影响所有 PBR 字段）");
                    var maskIdx = (int)layer.FadeMask;
                    if (ImGui.Combo("##fadeMask", ref maskIdx, LayerFadeMaskNames, LayerFadeMaskNames.Length))
                    { layer.FadeMask = (LayerFadeMask)maskIdx; MarkPreviewDirty(); }
                    if (layer.FadeMask != LayerFadeMask.Uniform)
                    {
                        var f = layer.FadeMaskFalloff;
                        if (ImGui.SliderFloat("羽化##fadeFalloff", ref f, 0.01f, 1f, "%.2f"))
                        { layer.FadeMaskFalloff = f; MarkPreviewDirty(); }
                    }
                    if (layer.FadeMask == LayerFadeMask.DirectionalGradient)
                    {
                        var a = layer.GradientAngleDeg;
                        if (ImGui.SliderFloat("角度##gAng", ref a, -180f, 180f, "%.1f°"))
                        { layer.GradientAngleDeg = a; MarkPreviewDirty(); }
                        var gs = layer.GradientScale;
                        if (ImGui.SliderFloat("范围##gScl", ref gs, 0.1f, 2f, "%.2f"))
                        { layer.GradientScale = gs; MarkPreviewDirty(); }
                        var go = layer.GradientOffset;
                        if (ImGui.SliderFloat("偏移##gOff", ref go, -1f, 1f, "%.2f"))
                        { layer.GradientOffset = go; MarkPreviewDirty(); }
                    }
                }
```

- [ ] **Step 3: 加 `DrawPbrCheckbox` helper**

在 `MainWindow` 类里的 `ResetSelectedLayer` 方法**上方**加：

```csharp
    /// <summary>
    /// Draw a PBR-field checkbox. Toggling it on triggers row pair allocation;
    /// on exhaustion, shows a toast and reverts the toggle to false.
    /// Toggling off triggers release-if-unused.
    /// </summary>
    private void DrawPbrCheckbox(TargetGroup group, DecalLayer layer, string label,
        Func<bool> get, Action<bool> set, bool exhausted)
    {
        var was = get();
        var v = was;
        var disabled = exhausted && !was;
        if (disabled) ImGui.BeginDisabled();
        if (ImGui.Checkbox(label, ref v))
        {
            if (v && !was)
            {
                // Attempt allocation only if this is the first Affects* on this layer
                bool needsAlloc = layer.AllocatedRowPair < 0;
                set(true);
                if (needsAlloc && !previewService.TryAllocateRowPairForLayer(group, layer))
                {
                    // Revert
                    set(false);
                    rowPairToast = $"无法启用 {label}：该材质 PBR 行号已满。请关闭其他图层的 PBR 字段后再试。";
                    rowPairToastUntil = DateTime.UtcNow.AddSeconds(4);
                }
                else
                {
                    MarkPreviewDirty();
                }
            }
            else if (!v && was)
            {
                set(false);
                previewService.ReleaseRowPairIfUnused(group, layer);
                MarkPreviewDirty();
            }
        }
        if (disabled) ImGui.EndDisabled();
    }
```

- [ ] **Step 4: 在 Draw() 的末尾画 toast**

在 `MainWindow.Draw` 方法**尾部**（即 `ApplyPendingSwaps` 之后，或 `ImGui.End()` 调用之前的合适位置）加：

```csharp
        if (rowPairToast != null && DateTime.UtcNow < rowPairToastUntil)
        {
            var vp = ImGui.GetMainViewport();
            ImGui.SetNextWindowPos(vp.WorkPos + new Vector2(vp.WorkSize.X * 0.5f, 80), ImGuiCond.Always, new Vector2(0.5f, 0f));
            if (ImGui.Begin("##rowPairToast", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize
                | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoSavedSettings))
            {
                ImGui.TextColored(new Vector4(1f, 0.7f, 0.2f, 1f), rowPairToast);
            }
            ImGui.End();
        }
        else if (rowPairToast != null)
        {
            rowPairToast = null;
        }
```

- [ ] **Step 5: 删除（或注释掉）旧 Emissive 专用 UI 段**

Task 1 rename 之后，旧的"发光"UI 段（`layer.AffectsEmissive` 那块含 `ColorEdit3("##emColor")` / `Combo("##emMask")` / `DrawFadeMaskPreview` 调用）在新的 PBR 面板里已被替换。**删除**旧的那一整段（CollapsingHeader "渲染" 内 `if (layer.AffectsEmissive) { ... }` 包含的发光 color/intensity/mask/gradient 子树）。确保 `DrawFadeMaskPreview`（原 `DrawEmissiveMaskPreview`）也挪进新的 PBR 块，或删除掉（v1 不要求预览）。如需保留预览，在 Step 2 的 `FadeMask != Uniform` 块里调用 `DrawFadeMaskPreview(layer.FadeMask, layer.FadeMaskFalloff, layer);`。

- [ ] **Step 6: 图层删除时释放 row pair**

找 `RemoveLayer` 调用点（`group.Layers.RemoveAt` / `RemoveLayer` 方法引用）。在调用 `group.RemoveLayer(idx)` **之前**先 `previewService.ForceReleaseRowPair(group, group.Layers[idx]);`。典型在图层列表行的"×"删除按钮 handler 内。

- [ ] **Step 7: 编译验证**

```bash
dotnet build -c Release
```

Expected: `Build succeeded. 0 Warning(s). 0 Error(s).`

- [ ] **Step 8: Commit**

```bash
git add SkinTatoo/SkinTatoo/Gui/MainWindow.cs
git commit -m "feat(pbr): MainWindow 加 PBR 字段面板 + 行号分配 UX + toast"
```

---

## Task 11: MainWindow — LayerFadeMask 迁移公告

**Files:**
- Modify: `SkinTatoo/SkinTatoo/Gui/MainWindow.cs`

- [ ] **Step 1: 加迁移公告 modal**

在 `MainWindow.Draw()` 方法**开头**（ImGui 开始绘制之前，或当 window visible 时）加：

```csharp
        if (config.ShowLayerFadeMaskMigrationNotice)
        {
            ImGui.OpenPopup("##layerFadeMigrate");
        }

        bool modalOpen = true;
        if (ImGui.BeginPopupModal("##layerFadeMigrate", ref modalOpen,
            ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoTitleBar))
        {
            ImGui.TextWrapped("项目已从旧版升级。");
            ImGui.Separator();
            ImGui.TextWrapped(
                "注意：图层羽化（原\"发光遮罩\"）的行为有所变化——现在它会让该图层的所有 PBR 效果" +
                "（包括漫反射、镜面反射、粗糙度、金属度、光泽等）一起按形状渐变，而不再仅影响发光。");
            ImGui.TextWrapped("如果旧效果与预期不符，请检查图层羽化设置。");
            ImGui.Separator();
            if (ImGui.Button("我知道了", new Vector2(200, 0)))
            {
                config.ShowLayerFadeMaskMigrationNotice = false;
                config.Save();
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }
```

- [ ] **Step 2: 编译验证**

```bash
dotnet build -c Release
```

Expected: `Build succeeded. 0 Warning(s). 0 Error(s).`

- [ ] **Step 3: Commit**

```bash
git add SkinTatoo/SkinTatoo/Gui/MainWindow.cs
git commit -m "feat(pbr): MainWindow 一次性迁移公告（EmissiveMask → LayerFadeMask 语义变化）"
```

---

## Task 12: HTTP API 新字段

**Files:**
- Modify: `SkinTatoo/SkinTatoo/Http/DebugServer.cs`

- [ ] **Step 1: 扩展 `SerializeLayer`**

找到 `DebugServer.SerializeLayer`（`DebugServer.cs:492-508`）。替换为：

```csharp
    private static object SerializeLayer(DecalLayer l) => new
    {
        kind                    = l.Kind.ToString(),
        name                    = l.Name,
        imagePath               = l.ImagePath,
        uvCenter                = new { x = l.UvCenter.X, y = l.UvCenter.Y },
        uvScale                 = new { x = l.UvScale.X, y = l.UvScale.Y },
        rotationDeg             = l.RotationDeg,
        opacity                 = l.Opacity,
        blendMode               = l.BlendMode.ToString(),
        clip                    = l.Clip.ToString(),
        isVisible               = l.IsVisible,
        allocatedRowPair        = l.AllocatedRowPair,

        affectsDiffuse          = l.AffectsDiffuse,
        affectsSpecular         = l.AffectsSpecular,
        affectsEmissive         = l.AffectsEmissive,
        affectsRoughness        = l.AffectsRoughness,
        affectsMetalness        = l.AffectsMetalness,
        affectsSheen            = l.AffectsSheen,

        diffuseColor            = new { r = l.DiffuseColor.X, g = l.DiffuseColor.Y, b = l.DiffuseColor.Z },
        specularColor           = new { r = l.SpecularColor.X, g = l.SpecularColor.Y, b = l.SpecularColor.Z },
        emissiveColor           = new { r = l.EmissiveColor.X, g = l.EmissiveColor.Y, b = l.EmissiveColor.Z },
        emissiveIntensity       = l.EmissiveIntensity,
        roughness               = l.Roughness,
        metalness               = l.Metalness,
        sheenRate               = l.SheenRate,
        sheenTint               = l.SheenTint,
        sheenAperture           = l.SheenAperture,

        fadeMask                = l.FadeMask.ToString(),
        fadeMaskFalloff         = l.FadeMaskFalloff,
        gradientAngleDeg        = l.GradientAngleDeg,
        gradientScale           = l.GradientScale,
        gradientOffset          = l.GradientOffset,
    };
```

- [ ] **Step 2: 扩展 `ApplyPartialUpdate`**

找到 `DebugServer.ApplyPartialUpdate`（`DebugServer.cs:510-564`）。在现有方法内部 **追加**以下块（放在方法末尾、`}` 之前）：

```csharp
        // v1 PBR field mappings
        if (root.TryGetProperty("kind", out v) && v.ValueKind == JsonValueKind.String)
        {
            if (Enum.TryParse<LayerKind>(v.GetString(), ignoreCase: true, out var k))
                layer.Kind = k;
        }

        if (root.TryGetProperty("clip", out v) && v.ValueKind == JsonValueKind.String)
        {
            if (Enum.TryParse<ClipMode>(v.GetString(), ignoreCase: true, out var cm))
                layer.Clip = cm;
        }

        if (root.TryGetProperty("affectsSpecular", out v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False)
            layer.AffectsSpecular = v.GetBoolean();
        if (root.TryGetProperty("affectsRoughness", out v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False)
            layer.AffectsRoughness = v.GetBoolean();
        if (root.TryGetProperty("affectsMetalness", out v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False)
            layer.AffectsMetalness = v.GetBoolean();
        if (root.TryGetProperty("affectsSheen", out v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False)
            layer.AffectsSheen = v.GetBoolean();

        if (root.TryGetProperty("diffuseColor", out v) && v.ValueKind == JsonValueKind.Object)
        {
            float r = v.TryGetProperty("r", out var vr) ? vr.GetSingle() : layer.DiffuseColor.X;
            float g = v.TryGetProperty("g", out var vg) ? vg.GetSingle() : layer.DiffuseColor.Y;
            float b = v.TryGetProperty("b", out var vb) ? vb.GetSingle() : layer.DiffuseColor.Z;
            layer.DiffuseColor = new Vector3(r, g, b);
        }
        if (root.TryGetProperty("specularColor", out v) && v.ValueKind == JsonValueKind.Object)
        {
            float r = v.TryGetProperty("r", out var vr) ? vr.GetSingle() : layer.SpecularColor.X;
            float g = v.TryGetProperty("g", out var vg) ? vg.GetSingle() : layer.SpecularColor.Y;
            float b = v.TryGetProperty("b", out var vb) ? vb.GetSingle() : layer.SpecularColor.Z;
            layer.SpecularColor = new Vector3(r, g, b);
        }

        if (root.TryGetProperty("roughness", out v) && v.ValueKind == JsonValueKind.Number)
            layer.Roughness = v.GetSingle();
        if (root.TryGetProperty("metalness", out v) && v.ValueKind == JsonValueKind.Number)
            layer.Metalness = v.GetSingle();
        if (root.TryGetProperty("sheenRate", out v) && v.ValueKind == JsonValueKind.Number)
            layer.SheenRate = v.GetSingle();
        if (root.TryGetProperty("sheenTint", out v) && v.ValueKind == JsonValueKind.Number)
            layer.SheenTint = v.GetSingle();
        if (root.TryGetProperty("sheenAperture", out v) && v.ValueKind == JsonValueKind.Number)
            layer.SheenAperture = v.GetSingle();

        // fadeMask (new) + legacy emissiveMask alias
        if (root.TryGetProperty("fadeMask", out v) && v.ValueKind == JsonValueKind.String)
        {
            if (Enum.TryParse<LayerFadeMask>(v.GetString(), ignoreCase: true, out var fm))
                layer.FadeMask = fm;
        }
        else if (root.TryGetProperty("emissiveMask", out v) && v.ValueKind == JsonValueKind.String)
        {
            if (Enum.TryParse<LayerFadeMask>(v.GetString(), ignoreCase: true, out var fm))
                layer.FadeMask = fm;
        }

        if (root.TryGetProperty("fadeMaskFalloff", out v) && v.ValueKind == JsonValueKind.Number)
            layer.FadeMaskFalloff = v.GetSingle();
        else if (root.TryGetProperty("emissiveMaskFalloff", out v) && v.ValueKind == JsonValueKind.Number)
            layer.FadeMaskFalloff = v.GetSingle();

        if (root.TryGetProperty("gradientAngleDeg", out v) && v.ValueKind == JsonValueKind.Number)
            layer.GradientAngleDeg = v.GetSingle();
        if (root.TryGetProperty("gradientScale", out v) && v.ValueKind == JsonValueKind.Number)
            layer.GradientScale = v.GetSingle();
        if (root.TryGetProperty("gradientOffset", out v) && v.ValueKind == JsonValueKind.Number)
            layer.GradientOffset = v.GetSingle();
```

**注意**：`LayerKind` 枚举引用需要在文件顶部 `using SkinTatoo.Core;` 已导入（现有代码已引入 `SkinTatoo.Core`）。

- [ ] **Step 3: 编译验证**

```bash
dotnet build -c Release
```

Expected: `Build succeeded. 0 Warning(s). 0 Error(s).`

- [ ] **Step 4: Commit**

```bash
git add SkinTatoo/SkinTatoo/Http/DebugServer.cs
git commit -m "feat(pbr): HTTP API 补 PBR/kind/fadeMask 字段 + 向后兼容 emissiveMask 别名"
```

---

## Task 13: 最终 Build + 手动验证清单

**Files:** 无（纯验证）

- [ ] **Step 1: 全量 Release 构建**

```bash
cd "C:/Users/Shiro/Desktop/FF14Plugins/SkinTatoo"
dotnet build -c Release
```

Expected: `Build succeeded. 0 Warning(s). 0 Error(s).`

- [ ] **Step 2: 启动游戏并重新加载插件**

用户操作：
1. `/xlplugins` → 禁用 → 重启 → 启用 SkinTatoo
2. `/skintatoo` 打开编辑器
3. 若有旧项目文件，确认"一次性迁移公告"弹出
4. 点"我知道了"确认公告消失 + 下次不再弹

- [ ] **Step 3: 初次接管 character.shpk 类材质**

手动步骤：
1. 选一件装备（如手套/鞋），加一个贴花图层
2. 触发预览，确认角色闪一次后贴花上色（说明 Full Redraw 路径 OK）
3. 检查插件日志：应看到 `Cached vanilla ColorTable 8x32 for ...` 和 `Vanilla row scan: ... vanillaOccupied=N, available=16-N`

如失败：检查 `DebugServer.AppendLog` 输出里 `TryGetVanillaColorTable` 是否匹配到材质；常见原因是 `HasColorTable=false`（该材质是 skin.shpk 类）或 `ColorTableWidth != 8`（legacy 材质）。

- [ ] **Step 4: 每图层独立 PBR**

手动步骤：
1. 同一装备加 A、B 两个贴花图层，不重叠
2. A 启用"金属度"并设 1.0；B 启用"粗糙度"并设 0.0
3. 切换到 inplace 路径（拖动滑块）
4. 视觉验证：A 区域金属感强，B 区域光滑反射；两区域互不干扰

- [ ] **Step 5: 每图层独立 Emissive**

手动步骤：
1. A 启用"发光"设红色；B 启用"发光"设蓝色
2. 确认两区域分别显示红/蓝发光（**关键回归点**——旧版本会合并成紫色）

- [ ] **Step 6: 多图层重叠 z-order**

手动步骤：
1. A 在底层启金属度 1.0
2. B 在顶层启粗糙度 0.0（重叠区覆盖 A）
3. 重叠区域应该只显示 B 的效果（A 的金属度"穿透不上来"）

- [ ] **Step 7: 行号上限降级**

手动步骤：
1. 快速加 15+ 图层，每个都启一个 Affects* 字段
2. 当行号耗尽时再点"启用粗糙度"等字段
3. 预期：checkbox 不被勾选，顶部弹 toast `"无法启用 粗糙度：该材质 PBR 行号已满"`，PBR 块顶部常驻 warning banner

- [ ] **Step 8: vanilla 视觉保留（关键回归）**

手动步骤：
1. 加单个小贴花在大面积装备的角落
2. 对比加前/加后：**非贴花区域的 diffuse / specular / roughness 应该看起来完全一致**（因为 vanilla normal.a 被保留）

如失败：检查 `CompositeNormalPbr` 是否只在有贴花覆盖的像素写了 A/G 通道，非覆盖像素是否保持了 `baseNorm.Clone()` 原值。

- [ ] **Step 9: 图层羽化软过渡**

手动步骤：
1. 选 RadialFadeOut，设 falloff 0.5
2. 启用"金属度"1.0
3. 贴花中心应完全金属，边缘应平滑过渡到 vanilla 粗糙度

- [ ] **Step 10: WholeMaterial 图层**

手动步骤：
1. 点"+ 材质层"按钮新建 WholeMaterial 图层
2. 确认右侧 UV 控件段**隐藏**
3. 启用"金属度"1.0
4. 整张材质变金属

- [ ] **Step 11: 混合 Decal + WholeMaterial**

手动步骤：
1. 底层加 WholeMaterial 图层设金属度 1.0
2. 顶层加 Decal 图层启"粗糙度"1.0
3. 贴花区域应是"金属 + 粗糙"的组合（实际上贴花 row pair 覆盖整行，所以贴花区域只走贴花 PBR；非贴花区域走 WholeMaterial 的金属）

**注意**：v1 的物理模型决定了重叠区域只会使用后胜图层的 row pair，不会"金属 + 粗糙度合并"——这是已知限制，不是 bug。spec 已说明。

- [ ] **Step 12: inplace 无闪烁**

手动步骤：
1. 拖动任意 PBR 滑块
2. 角色不应闪一下（如果闪了说明走了 Full Redraw 路径，`CanSwapInPlace` 判定有问题）
3. 切换"发光"checkbox 也应 inplace

如失败：检查 `PreviewService.CheckCanSwapInPlace` 是否因为新 PBR 字段变化被意外拒绝。

- [ ] **Step 13: vanilla skin.shpk 兜底（身体材质）**

手动步骤：
1. 在身体皮肤上加一个贴花图层
2. PBR 面板里应能改 diffuse 和 emissive（老路径兜底），但勾 Roughness/Metalness/Sheen 时要么 no-op，要么日志里 `IsDawntrailLayout` 返回 false
3. 确认之前的 EmissiveCBufferHook 路径仍然工作（身体贴花发光颜色能改）

**v1 接受的行为**：身体材质的 PBR 字段"无效但不崩溃"。v2 通过路线 C 转换后才真正支持。

- [ ] **Step 14: 项目持久化**

手动步骤：
1. 加几个 PBR 图层，设好各种字段
2. 关闭插件，重启游戏
3. 重新打开，确认所有 PBR 字段和 Kind、Affects* 状态都恢复
4. 确认 `AllocatedRowPair` 是重新分配的（不会持久化），所以 vanilla scan 是在 Full Redraw 时重新跑的

- [ ] **Step 15: HTTP API 往返测试**

```bash
curl http://localhost:14780/api/project
# 检查返回 JSON 的 layer 里应有 kind / affects* / diffuseColor / specularColor / roughness / metalness / sheen* / fadeMask / fadeMaskFalloff

curl -X PUT http://localhost:14780/api/layer/<layerId> \
  -H "Content-Type: application/json" \
  -d '{"affectsRoughness": true, "roughness": 0.1}'
# 预期：游戏内该 layer 的粗糙度变 0.1
```

- [ ] **Step 16: 最终总提交 commit（如前面每个 task 已提交则跳过）**

```bash
git status
# 确认 working tree clean
git log --oneline -20
# 确认 task 1-12 提交都在
```

---

## 已知限制 & 后续工作

| 项 | 状态 | 备注 |
|---|---|---|
| 行 1 vanilla 基底取 row 0 的简化 | ✅ v1 接受 | spec 风险 1，视觉若有问题再升级 |
| 身体材质 PBR 不支持 | ✅ v1 接受 | v2 路线 C 解决 |
| 重叠区域 PBR 不混合 | ✅ 物理限制 | 文档已说明 |
| 单材质 16 行号上限 | ✅ 物理限制 | vanilla 占用后约 8-12 个 |
| Legacy 模式字段（GlossStrength/SpecularStrength）| ✅ v1 不做 | 真有用户提再加 |
| 路线 C IDA 调研 | 🔄 v1 并行 | 已有 `docs/路线C-IDA调研补充.md` |

---

## 实施期 checklist（每个 task 结束时检查）

- [ ] `dotnet build -c Release` 通过，0 warning / 0 error
- [ ] 新加的字段都进了 `Save` 和 `Load`（不要有"写了没读"的漏网之鱼）
- [ ] `DebugServer.SerializeLayer` / `ApplyPartialUpdate` 对称（read/write 互为逆操作）
- [ ] 所有 `previewService.ReleaseRowPairIfUnused` 调用点都在状态变化**之后**而不是之前
- [ ] commit 信息只有一行正文 + 无 Co-Authored-By（匹配项目约定）

---

## 执行建议

本 plan 的 Task 1 跨文件 rename，必须一次性改完才能编译通过；Task 2-12 每个都是独立的 commit。建议：

1. **Subagent-Driven（推荐）**：每 task dispatch 一个独立 subagent，main 线程做 review。Task 1 因为跨文件，subagent 内部要自检 `dotnet build` 再提交。
2. **Inline**：在当前 session 里逐 task 执行，Task 8 / Task 10 这两个复杂点设置 checkpoint 做 review。
