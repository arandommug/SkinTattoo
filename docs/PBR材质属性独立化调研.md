# PBR 材质属性独立化调研

> 2026-04-07 调研。承接 `材质替换路线研究.md`，进一步研究"每个贴花图层独立 PBR + emissive"的可行性。
> 源材料：Glamourer-CN、Penumbra-CN 的 MaterialExporter、SkinTatoo 现有 hook/swap 实现。

## 起因

用户需求两条：

1. 想要 Glamourer 那种 PBR 调参 UI（Diffuse / Specular / Emissive / Roughness / Metalness / Sheen Rate / Sheen Tint / Sheen Aperture），让贴花图层也具备这些属性。
2. 想要"将整张材质作为一个图层"的概念，不能移动旋转缩放，但可以调透明度、发光以及上面那些 PBR 属性。

并且观察到现状的痛点：当前 SkinTatoo 同一个 TargetGroup 下多个图层的 emissive 是被合并的，**改一个图层的发光会带动其他图层一起变**。

## 核心机制：ColorTable 行号是从 normal.a 采样出来的

这是整个独立化方案的物理基础。在调研之前我们一直默认"PBR 是材质级"——但 character.shpk 系列 shader 实际上把 PBR 参数表（ColorTable）按"行对"组织，**像素属于哪个行对完全由 normal map 的 alpha 通道决定**。

### Penumbra 导出代码里的明文证据

`Penumbra/Import/Models/Export/MaterialExporter.cs:136-149`

```csharp
var tablePair = (int) Math.Round(indexPixel.R / 17f);   // R 通道（从 normal.a 派生）→ row pair 号
var rowBlend  = 1.0f - indexPixel.G / 255f;             // G 通道 → 行对内的混合权重

var prevRow = table[tablePair * 2];
var nextRow = table[Math.Min(tablePair * 2 + 1, ColorTable.NumRows)];

var lerpedDiffuse       = Vector3.Lerp((Vector3)prevRow.DiffuseColor,  (Vector3)nextRow.DiffuseColor,  rowBlend);
var lerpedSpecularColor = Vector3.Lerp((Vector3)prevRow.SpecularColor, (Vector3)nextRow.SpecularColor, rowBlend);
var lerpedEmissive      = Vector3.Lerp((Vector3)prevRow.EmissiveColor, (Vector3)nextRow.EmissiveColor, rowBlend);
// roughness / metalness 同理
```

`Penumbra.GameData/Files/StainService/ColorTableSet.cs:155,159`：

```csharp
public const int NumRows = 32;
public static readonly (int Width, int Height) TextureSize = (8, 32);
```

也就是 32 行 = 16 个行对（Dawntrail 布局；legacy 是 16 行 = 8 行对）。

### 翻译

| 现象 | 物理解释 |
|---|---|
| 一张材质上不同部位有不同 PBR | 那些部位的 normal.a 不一样，对应不同的行对 |
| Glamourer 能"分行调 PBR" | 它的 UI 是按 row pair 索引展开的，每行调到的是 ColorTable 里对应行的字段 |
| 我们当前发光不能分图层 | 现状把所有 emissive 图层合并写到 normal.a 里同一个值，所以所有图层落到同一行，PBR 字段必然共享 |
| **要让每个贴花图层独立** | **给每个图层分配一个独立的 row pair，合成 normal.a 时贴花区域写对应行号即可** |

行对内的 G 通道插值给我们留了一个隐藏福利：**贴花边缘的软过渡可以用 row pair 内的两行做 lerp**，不必硬切。

## Glamourer 的 PBR UI 数据流（可直接复刻的范本）

### UI 入口

`Glamourer/UI/Materials/MaterialDrawer.cs:84-331`（单行）和 `:110-135`（多行）

支持的可调字段：
- 三个 RGB 拾色器：Diffuse / Specular / Emissive
- Legacy 模式额外字段：GlossStrength / SpecularStrength
- Dawntrail 模式额外字段：Roughness / Metalness / Sheen / SheenTint / SheenAperture
- 单套 UI + Mode 切换按钮（180-211 行 ModeToggle），不分两套面板

`Glamourer/UI/Materials/AdvancedDyePopup.cs:335-503` 是高级窗口，Sheen 三件套有独立 drag：
- DragSheen (592-603)
- DragSheenTint (606-618)
- DragSheenRoughness (621-633)

### 数据模型

`Glamourer/State/Material/ColorRow.cs:14-182`

| ColorRow 属性 | Half offset | 备注 |
|---|---|---|
| Diffuse | [0][1][2] | 两布局通用 |
| Specular | [4][5][6] | 两布局通用 |
| Emissive | [8][9][10] | 两布局通用 |
| GlossStrength | [3] | Legacy only |
| SpecularStrength | [7] | Legacy only |
| Sheen | [12] | Dawntrail only |
| SheenTint | [13] | Dawntrail only |
| SheenAperture | [14] | Dawntrail only |
| Roughness | [16] | Dawntrail only |
| Metalness | [18] | Dawntrail only |

`ColorRow.Apply(ref ColorTableRow row, Mode mode)` 内部 switch mode 决定写哪些 offset。

### 写入链路

```
UI 拖动滑块
  → designManager.ChangeMaterialValue(design, index, tmp)              MaterialDrawer.cs:329
  → DesignEditor.ChangeMaterialValue                                    DesignEditor.cs:273-311
  → StateApplier.ChangeMaterialValue                                    StateApplier.cs:335-357
      ↳ PrepareColorSet.TryGetColorTable(actor, index, out base, out mode)  // 从 GPU 读回当前表
      ↳ changedValue.Apply(ref baseTable[row], mode)                         // 在 CPU 副本上改一行
      ↳ directX.ReplaceColorTable(texture, baseTable)                        // 整张表写回 GPU
  → DirectXService.ReplaceColorTable                                    DirectXService.cs:21-46
      ↳ D3D11 UpdateSubresource，R16G16B16A16Float
```

我们当前的 `TextureSwapService.UpdateEmissiveViaColorTable` 走的是这条路的简化版（只写 emissive 三个 Half），扩成全字段是几乎机械的工作。

### 读回（初始化滑块）

`DirectXService.cs:49-154`

`AdvancedDyePopup.cs:110-111` 调 `directX.TryGetColorTable(*texture, out table)`，然后 `ColorRow.From(...)` 把 ColorTableRow 转成 UI state。原理：D3D11 创建 staging texture → CopyResource → Map → memcpy 到 `ColorTable.Table` → cache。

## SkinTatoo 当前状态摘要

| 模块 | 文件 | 现状 |
|---|---|---|
| 图层数据 | `Core/DecalLayer.cs:41-67` | 有 emissive 五件套（Color/Intensity/Mask/Falloff/Gradient*），**无任何 PBR 字段** |
| 材质绑定 | `Core/TargetGroup.cs:5-58` | 一个 group 对应一张 .mtrl，下挂多个 layer |
| 合成入口 | `Services/PreviewService.cs:200-218`（full）/ `:338-371`（async inplace） | 异步线程合成 → 主线程批量 swap |
| Emissive 合并 | `PreviewService.GetCombinedEmissiveColor` | **痛点**：所有图层 emissive RGB 加权合并成单值 |
| ColorTable 写入 | `Interop/TextureSwapService.cs:209-342` | 只写 emissive [8][9][10]，依赖 `mtrlHandle->HasColorTable` |
| skin.shpk 兜底 | `Interop/EmissiveCBufferHook.cs:139-178` | hook OnRenderMaterial 改 g_EmissiveColor CBuffer，仅 emissive |
| 分流决策 | `PreviewService.ApplyPendingSwaps:185-192` | 先尝试 ColorTable，失败再尝试 CBuffer hook |

## 路线对比（更新后）

| 路线 | 描述 | 复用 | 工作量 | 局限 |
|---|---|---|---|---|
| **A. character.shpk 类材质做完整 PBR + 每图层独立 row pair** | 贴花区域 normal.a 写独立行号；PBR 字段写到对应 ColorTable 行；扩 ColorTable swap 写全字段；单材质上限 16 个独立图层（扣除 vanilla 已用） | 现有合成器 + ColorTable swap + Glamourer 的 ColorRow 字段表 | **中** | 不覆盖 vanilla 身体（skin.shpk） |
| **B. skin.shpk 走 CBuffer 全字段 hook** | hook OnRenderMaterial 改 g_DiffuseColor / g_SpecularColor / g_Material* 等常量；身体材质能改 PBR 但不能分图层（CBuffer 是全局常量） | 现有 EmissiveCBufferHook 框架 | 中 | 仍然全图层共享一组值，**不解决独立化** |
| **C. skin.shpk 重写为 character/charactertattoo shpk** | 用 MtrlFile 把身体材质的 ShaderPackageName 改掉，构造目标 shader 期望的 sampler / ColorTable，让身体材质也享受路线 A | Penumbra 的 `MtrlFile` + `MtrlFile.AddRemove` + `ShpkFile`（已存在）；vanilla `charactertattoo.shpk` 作为潜在转换目标 | **中**（IDA 调研后下修） | .mtrl 重写正确性 + 可能需要补 placeholder 纹理 |

> **路线 C 工作量已大幅下修**（IDA 调研结果）：原本估计为"大、容易崩、需要 hook"，实际经 IDA 反编译确认 vanilla 引擎按 ShaderPackage 指针 fast-path 分流，**只要 .mtrl 文件里 ShaderPackageName 改对就能切 shader，不需要任何 hook**。详细发现见 `docs/路线C-IDA调研补充.md`。

## 决策

**用户选定路线 C**，目标是身体材质也支持每图层独立 PBR。

路线 A 是路线 C 的子集——即便先做 A 也是必要前置工作，因为 A 完成后我们才知道"一张正常 character.shpk 材质 + 我们写入的 normal.a 行号 + 修改后的 ColorTable"在游戏里能不能跑起来。所以执行顺序自然是 **先 A 后 C**：

1. 在装备/眼/发上把"每图层独立 row pair + 全字段 PBR swap"链路打通
2. 验证渲染正确、边缘可控、行号上限够用
3. 然后回头研究 skin.shpk → character.shpk 的转换

## 路线 C 待研究项（IDA 调研后状态）

| # | 项 | 状态 | 结论 |
|---|---|---|---|
| 1 | 两个 shader 的 sampler/CBuffer 列表差异 | 🔄 推迟 v2 | **不需要 IDA**——用 Penumbra `ShpkFile.cs` 解析 vanilla `.shpk` 文件本身就能拿到 sampler 名 / CBuffer 字段名 / ShaderKey 列表。新增第三个候选：`charactertattoo.shpk` |
| 2 | ShaderPackageName 改写后的加载链路 | ✅ 已确认 | shader package 走 ResourceManager 通用 loader (`sub_140304A50`)，跟 .tex/.mtrl 同一条路；Penumbra 重定向后 .mtrl 会被重新解析 |
| 3 | ColorTable 物理大小变化兼容性 | ⏸️ 留 v2 实施期验证 | 需要游戏内实测；MtrlFile.Write 已能扩展 DataSet |
| 4 | normal.a 通道含义切换 | ✅ 已确认 | 取决于 ShaderPackage——vanilla skin.shpk 内 BuildSkin 把 normal.a 重置为 255；切到 character.shpk 后含义自动变成 row pair index |
| 5 | 运行时切 shader package 的 hook 点 | ✅ 已确认 | **不需要 hook**——OnRenderMaterial 按 ShaderPackage 指针自动 fast-path 分流，只要 .mtrl 文件里 ShaderPackageName 改对，引擎自动走对应分支。这是路线 C 工作量大幅下修的根据 |
| 6 | Vanilla 行号占用扫描 | ✅ 不需要 IDA | 在合成器里扫 vanilla normal.a 直方图避开高频值即可，已写入 v1 spec |

详细 IDA 反编译结果（包括 ffxiv_dx11.exe 内的具体地址、shader package 字符串数组、OnRenderMaterial 函数签名、MaterialResourceHandle 字段 offset）见 `docs/路线C-IDA调研补充.md`。

## 已确认的关键事实

### 事实 7（新增）：character.shpk 行选择 = round(normal.a / 17)

`Penumbra/Import/Models/Export/MaterialExporter.cs:136`：`Math.Round(indexPixel.R / 17f)` 把 0-255 映射到 0-15。意味着我们写入贴花行号时要用 `(rowPair * 17).Clamp(0, 255)` 这样的精确值。

### 事实 8（新增）：行对内插值由 G 通道承担

`MaterialExporter.cs:137`：`1.0f - indexPixel.G / 255f`。意味着如果未来我们想做"贴花边缘 PBR 软过渡"，可以用同一个 row pair 的两行 + G 通道渐变实现，不必跨 row pair。

### 事实 9（新增）：skin.shpk 内部把 normal.a 重置为 255

`MaterialExporter.cs:359-393` 的 `BuildSkin` 注释明确说"移除皮肤颜色影响和湿润度遮罩"，确认 vanilla 身体 normal.a 在 skin.shpk 下不是 row index。这正是路线 C 必须重写 normal.a 含义的根本原因。

### 事实 10（新增）：Glamourer 用单套 UI 处理两种布局

`MaterialDrawer.cs:180-211` 的 ModeToggle + `ColorRow.Apply` 的 mode switch。我们做 PBR UI 时直接抄这个范式，不要分两套窗口。

## Latent bug 提醒（沿用 + 新增）

- `TextureSwapService.UpdateEmissiveViaColorTable` 的 `int rowStride = ctWidth * 4`：emissive 在 vec4 #2，两布局都正确；但写 Roughness [16] / Metalness [18] / Sheen [12-14] 时只对 Dawntrail 8-vec4 布局有效。实现 `UpdateMaterialPbr` 必须先用 `ctWidth >= 8` 区分布局。
- 路线 A 实施时，"每图层独立 row pair" 意味着 swap 入口要从"写 1 行" 改成"写 N 行（每个图层一行）"。建议直接复制 Glamourer 的 `ColorRow + ReplaceColorTable` 全表写回模式，不要保留单行 patch。
- 路线 C 实施时，重写 .mtrl 后 `MaterialResourceHandle.FileName` 走 Penumbra 重定向格式（`|prefix_hash|disk_path.mtrl`），现有的"游戏路径 + 磁盘路径双匹配"逻辑要继承到新材质。
