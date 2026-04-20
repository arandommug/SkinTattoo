# Ch5 — 接缝成因与改造路径（终章·综合）

> 综合 Ch2（SkinType 分支）、Ch3（DXBC 对比）、Ch4（cbuffer/sampler 清单）的全部结论。
> 用户于 2026-04-20 现场观察反馈："上面的皮肤是有光泽的，接缝下面的好像没有" —— 成为本章物证基准。
> 产出：对接缝成因给出闭环解释；对 A/B/C 三条修复路径给出 DXBC 级别的具体改动清单。
> 备注：本章不立即落地代码，只给出实施蓝图，实际 patch 留给后续迭代。

## 5.1 接缝物证链闭环

### 观察到的现象

- 身体材质挂 Emissive 变体（我们插件强切 `CategorySkinType=ValueEmissive`）
- 颈部以下（身体）**失去光泽/变哑光**
- 颈部以上（脸部）保持原有光泽

### 物证 1：vanilla 时 Body 与 Face 的 pass[2] PS 本就不同（Ch2 §2.4）

- Body：PS[8]（及其 SceneKey 兄弟 `[23, 38, 53, ...]`）
- Face：PS[2]（及其兄弟 `[21, 32, 51, ...]`）
- Emissive：PS[19]（及其兄弟 `[28, 49, 58, ...]`）

三者**是物理上不同的 PS**，不是同一 PS 加开关。

### 物证 2：Body 专属的"normal.alpha gloss mask"链（Ch4 §4.4）

Body PS[8] 在采样完法线后执行：

```
sample_b_indexable r0.xz, v2.xyxx, t5.zxwy, s1, r0.x   ; r0.x=normal.z, r0.z=normal.alpha
mul r0.z, r0.z, v1.w                                    ; r0.z = normal.alpha × vertex.alpha
...
mul_sat r0.x, r0.z, r0.x                                ; r0.x = sat(normal.alpha × vertex.alpha × normal.z)
```

这条链把 normal.alpha **作为 specular gloss mask** 乘进 r0.x，后续 r0.x 作为"主表面强度"参与高光计算。这是 body 光泽的来源。

### 物证 3：Face PS[2] 根本不读 normal.alpha

```
sample_b_indexable r0.x, v2.xyxx, t5.zxyw, s1, r0.x    ; 只取 .z 到 r0.x
```

Face 连 normal.alpha 都不采样。它的皮肤光泽来自别的路径（可能依赖 `g_SamplerMask` 的 mask 贴图，t6）—— 因为脸部贴图有专用的皱纹 mask，身体没有。

### 物证 4：Emissive PS[19] 采了 normal.alpha 但**把它挪去做 emissive mask**（Ch4 §4.6）

```
sample_b_indexable r0.xz, v2.xyxx, t5.zxwy, s1, r0.x   ; 同 Body：r0.x=normal.z, r0.z=normal.alpha
mul r1.xyz, cb0[3].xyzx, cb0[3].xyzx                   ; r1.xyz = g_EmissiveColor²
mul r1.xyz, r0.zzzz, r1.xyzx                           ; r1.xyz = g_EmissiveColor² × normal.alpha
mul r0.x, r0.x, cb0[9].x                               ; r0.x × g_TileAlpha
mul_sat r0.x, r0.x, v1.w                               ; r0.x = sat(normal.z × vertex.alpha)
                                                       ;   * 注意：不像 Body 那样乘 normal.alpha
```

**关键差异**：Emissive 把 `normal.alpha` 存到 `r1.xyz`（emissive accumulator），主表面 `r0.x` 不再乘它。这就是 body 切到 Emissive 后失去光泽的直接原因。

### 物证 5：Emissive 还额外做 `r3 = r1.xyz × max(luma, 1)`（L756）

这一乘法**放大了** emissive 分量。如果 `g_EmissiveColor = 0`（默认状态），r1 就是 0，无影响；但我们插件的 `MtrlFileWriter` 会写入 `g_EmissiveColor`，即使数值很小，只要 normal.alpha 不为 0（多数 body mod 的 normal.alpha 都 ≠ 0，因为它在 Body 分支是 gloss mask），就会产生可观察的亮度修饰。

### 综合解释

身体切到 Emissive 之后：

1. **丢掉** normal.alpha 对 gloss mask 的贡献 → 主表面强度变弱
2. 同一个 normal.alpha 被**改用**为 emissive mask → 身体变亮，但亮的分布是 gloss 形状
3. 加上 cb5[0] × specular 的调制 → 最终观感偏扁/哑

脸部完全不受影响（它仍跑 vanilla PS[2]）。结果就是"脸上仍有光泽，颈下失去光泽"，和你看到的一模一样。

## 5.2 r1.xyz 完整通路（PS[19] 中 emissive 累加器的生命周期）

这是 Emissive PS 的核心骨架，也是后续改造的主要着力点。

| 行号 | 指令 | 作用 |
|---:|---|---|
| 296 | `mul r1.xyz, cb0[3].xyzx, cb0[3].xyzx` | `r1 = g_EmissiveColor²`（sRGB → 近似线性） |
| 297 | `mul r1.xyz, r0.zzzz, r1.xyzx` | `r1 *= normal.alpha`（emissive mask） |
| 298-752 | 中间 455 行 | **完全不动 r1.xyz**（仅使用 r1.w 作暂存） |
| 753 | `mul r1.xyz, r1.xyzx, cb5[0].xyzx` | `r1 *= g_MaterialParameterDynamic.m_EmissiveColor`（runtime 动态叠乘） |
| 756 | `mul r3.xyz, r1.xyzx, r2.wwww` | `r3 = r1 × max(luma_of_r2, 1)`（按周围亮度放大） |
| 761 | `mad r0.yzw, r1.xxyz, r2.wwww, r0.yyzw` | **`r0.yzw += r1 × max(luma, 1)` — emissive 汇入主色累加器** |
| 762+ | r1.xyz 被重用 | emissive 生命周期结束 |
| 772 | `mul r0.xyz, r0.yzwy, cb4[0].xyzx` | `r0.xyz = (r0.y, r0.z, r0.w) × m_MulColor.xyz`（实例级色调） |
| 773 | `max r0.xyz, r0.xyzx, 0` | clamp 到正域 |
| 774 | `sqrt r0.xyz, r0.xyzx` | 线性 → sRGB（gamma 2.0 近似） |
| 775 | `mul o0.xyz, r0.xyzx, cb1[3].xxxx` | × `g_CommonParameter[3].x`（全局曝光） |

**核心洞察**：emissive 分量最终的贡献 ≈ `g_EmissiveColor² × normal.alpha × m_EmissiveColor_dynamic × max(luma, 1) × m_MulColor`。亮度被多次放大，其中 `max(luma, 1)` 尤其关键 —— 它让 emissive **在本就亮的区域变得更亮**，这就是我们 UI 上感觉"发光强度不线性"的根本原因。

## 5.3 改造路径 A：在 PS[19] 里补 Body 的 gloss mask 链

**目标**：让 body 切到 Emissive 后 gloss mask 不丢失，视觉上和 Body PS[8] 一致（除了 emissive 覆盖层）。

**具体方案**：

1. 在 PS[19] 第 297 行（`mul r1.xyz, r0.zzzz, r1.xyzx`，消费掉 normal.alpha 作 emissive mask）**之后**、第 298 行（`mul r0.x, r0.x, cb0[9].x`）**之前**，**额外插入一条 Body 的链路**：

```
mul r0.w, r0.z, v1.w     ; r0.w = normal.alpha × vertex.alpha (借 r0.w 暂存)
```

2. 在第 299 行（`mul_sat r0.x, r0.x, v1.w`）处，把 `v1.w` 改为 `r0.w`：

```
mul_sat r0.x, r0.x, r0.w  ; 原本是 v1.w
```

这样 r0.x 就变成 `sat(normal.z × (normal.alpha × v1.w))`，恢复 Body 的 gloss mask 行为。

**注意**：r0.w 在这一段是空闲寄存器（前后 30 行都没使用），合法可用。如果担心 reg alloc 冲突，可以把 r0.w 改成 r2.w / r3.w（前提是检查确实空闲）。

**不动**：
- 不动 r1.xyz 的 emissive 初始化（296-297 行）—— 这样 emissive 效果仍然保留
- 不动 cb5[0] 的动态乘法（753 行）—— runtime hook 照旧工作

**成本**：+2 条指令、一个寄存器借用。`dxbc_patcher.py` 需要新增 "在指定 offset 插入指令" 的能力（目前只支持"替换指令"）。

**风险**：
- 中等：需要精确定位 PS[19] 中"第 297 行之后"的 DXBC 字节偏移 —— 反汇编易读，字节码定位需要匹配指令 opcode
- 其他 31 个 Emissive 兄弟 PS 的 reg alloc 未必一致 —— 需要对每个 PS 单独定位插入点（可以写成 pattern matching）

### 变体 A'：简化版（丢弃 emissive 专属 mask）

如果 A 工程复杂，可以退而求其次：**把 297 行的 `mul r1.xyz, r0.zzzz, r1.xyzx` 改成 `mul r1.xyz, l(1,1,1), r1.xyzx`**（即不再用 normal.alpha 当 emissive mask）。这样 normal.alpha 就回到 Body 的用途（通过现有的 Body gloss mask 链路——但这需要 297 行之后再加一条 `mul r0.z, r0.z, v1.w; mul_sat r0.x, r0.z, r0.x`）。

本质和 A 一样，只是插入位置变了。A' 的好处是不需要借 r0.w。

## 5.4 改造路径 B：给 Body/Face 分支原地注入 ColorTable 发光（不强切 SkinType）

**目标**：保持 `CategorySkinType=ValueBody/ValueFace` 不变，但让 Body PS[8]/Face PS[2] 的 pass[2] 输出**附加** ColorTable 发光贡献。

### 现在的难点（Ch3 §3.7 提出的重评）

Body PS 本来没有 cb5（g_MaterialParameterDynamic），也没有 emissive 累加器 r1.xyz。要"注入"emissive，需要：

1. **RDEF 层**：给 Body PS 的资源列表添加 `g_MaterialParameterDynamic` cbuffer 与 `g_SamplerTable` sampler+texture
2. **SHEX 层**：添加 `dcl_constantbuffer CB?, immediateIndexed`、`dcl_sampler s?`、`dcl_resource_texture2d t?`
3. **指令层**：在 Body PS 尾段（第 760 行附近，对应 PS[19] 的 761 行）插入：
   ```
   sample_indexable r_emissive.xyz, <UV>, t_ColorTable.xyzw, s_ColorTable  ; 采 ColorTable 拿 emissive
   mul r_emissive.xyz, r_emissive.xyz, r0.zzzz                             ; × normal.alpha 作 mask
   mad r0.yzw, r_emissive.xxyz, r2.wwww, r0.yyzw                           ; 并入主累加器
   ```

### 可行性评估

**好消息**：
- Ch4 §4.1 已经确认 Body PS 的 cbuffer slot 0..6 被占用，slot 7+ 还空着 —— 可以把 `g_MaterialParameterDynamic` 绑到 b7
- sampler 用的是 s0-s4，t0-t9；s5 / t10 未占用 —— 和我们 PS[19] patch 用的相同 slot 方案
- Body PS 的结构和 Emissive PS 非常接近（Ch3 §3.2 七个 cbuffer 完全一致），reg alloc 几乎对齐

**坏消息**：
- Body PS 没有"emissive 累加器"寄存器 —— 需要选一个尾段空闲的 r* 作为新 accumulator。需要做寄存器使用分析脚本
- 整个 shpk 的 NodeAliases 共 22272 条，Body 分支占 1/5 ≈ 4500 条，patching 后要验证全部 selector 依然能命中正确的新 PS
- dxbc_checksum 必须重算（我们的 patcher 已支持）

**32 个 Body pass[2] PS 都要 patch**：`[8, 23, 38, 53, 68, 83, 98, 113, 128, 143, 158, 173, 188, 203, 218, 233, 242, 251, 260, 269, 278, 287, 296, 305, 314, 323, 332, 341, 350, 359, 368, 377]`，加上 32 个 pass[3] 兄弟（辅助光照）`[9, 24, 39, 54, ...]`。每个独立匹配插入位置，成本高但可机械化。

### 简化版 B'：只 patch Body 的 pass[2]（32 个 PS）不 patch pass[3]

如果验证发现 pass[3] 不影响皮肤主观感，可以省一半工作量。

## 5.5 改造路径 C：脸部 mtrl 也走 Emissive 分支（对齐 SkinType）

**目标**：用户在脸上放贴花并开发光时，同步把 `*_fac_a.mtrl` 的 `CategorySkinType` 切到 `ValueEmissive`，让颈部两侧都走 PS[19] → 消除接缝。

### 新认识（Ch3 §3.3、Ch4 §4.4 之后）

- PS[19] 使用 `v2.zw` 作 UV（同 Face），不是 `v2.xy`（Body 用的）
- PS[19] 消费 normal.alpha 作 emissive mask —— 对 Face 而言，face 的法线贴图 alpha 通道在**现有的 vanilla 资源里是什么含义？** 很可能未定义（Face 用的是 t6 `g_SamplerMask`，不依赖 normal.alpha），因此 alpha 值是什么就决定 emissive 从哪里冒出

这意味着：

- 如果 Face 的 normal.alpha = 1（没特别设计）→ emissive 会覆盖整张脸
- 如果 Face 的 normal.alpha = 0 → emissive 完全看不见
- 如果 Face 的 normal.alpha 有纹理 → emissive 呈现该纹理的形状

### 工程上

- MtrlFileWriter 需要识别 Face mtrl（路径含 `/face/`），对它也执行 Emissive 强切
- PreviewService 要把 Face mtrl 也放进 redirect 清单
- 用户层 UI 增加一个开关"脸部同步发光变体"，默认关

### 风险

- 脸部贴图原本依赖 t6 `g_SamplerMask` 做皱纹/疤痕等效果 —— 切到 Emissive 可能改变脸部 mask 消费路径（需实测）
- 脸部的 `g_EmissiveColor`（cb0）原本为 0（vanilla 默认）→ 哪怕切了 SkinType，r1.xyz 仍为 0，emissive 不出现。我们需要显式写入 `g_EmissiveColor`

C 的工程量其实很小（MtrlFileWriter 增加一个分支），但风险评估要单独做一轮。

## 5.6 推荐实施顺序

1. **路径 A（或 A'）优先尝试**：改动最小、风险最低，能直接消除接缝。先手写一个 patch 工具，对 PS[19] 注入 gloss mask 链。
2. **路径 C 作为低成本补充**：MtrlFileWriter 加开关，UI 作为"高级选项"暴露给用户。用户在 A 不完全满意时可以叠加 C。
3. **路径 B 作为终极目标**：等 Ch6（PS[19] 完整解剖）和 Ch7（高级效果）走一轮，再评估工程投入。

## 5.7 同时开启更大研究方向

用户在本章定稿时提出："把所有相关指令分支都解读，为后续可以自己修改 shader 实现更高级效果做储备"。

这超出了"修 bug"范畴，指向**把 skin.shpk 作为可编程的皮肤着色器平台**。为此后续章节如下：

- **Ch6 —— PS[19] 逐段解剖**（计划）：把 485 条指令按功能分区解读。目标是让后来者可以指着任一行说"这是 SSS 半径"、"这是 cubemap 反射"、"这是 rim light"。
- **Ch7 —— 高级效果创意与可行性**（计划）：基于 Ch6 的理解，列出一些尚未被 modding 社区实现的特效方向，每条给可行性评估（如 per-layer SSS、动态 wetness、normal.alpha 双通道复用等）。
- **Ch4 附录**：把 35 个未识别 CRC 的 MaterialParam 继续认名字 —— 可以通过 character.shpk、characterlegacy.shpk、charactertattoo.shpk 交叉比对，或直接在 IDA 里找字符串。

## 5.8 本章交付要点回顾

- [x] 接缝的物证链已从"假设"升级为"DXBC 级确定事实"
- [x] r1.xyz 的 emissive 通路全程可追踪（Ch5 §5.2）
- [x] 三条修复路径都有 DXBC 级别的操作清单
- [x] 未来 modding 方向（Ch6/Ch7）已开出

## 状态

- [x] Ch0 总览
- [x] Ch1 shpk 结构
- [x] Ch2 SkinType 分支全貌
- [x] Ch3 DXBC 对比
- [x] Ch4 cbuffer/sampler/texture 清单
- [x] Ch5 接缝与改造路径（本文 —— 原主线研究收束）
- [ ] Ch6 PS[19] 逐段解剖（新扩展，用户要求）
- [ ] Ch7 高级效果 idea 清单（新扩展）
