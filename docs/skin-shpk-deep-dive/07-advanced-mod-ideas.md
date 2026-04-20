# Ch7 — 高级效果 idea 清单与可行性评估

> 基于 Ch2-Ch6 的理解，给出一份"站在我们目前知识之上、技术上已经可行"的 skin.shpk 改造方向清单。每条包含：目标、落点（DXBC 或 mtrl 或插件层）、工程量、风险、依赖条件、未来扩展空间。
> 目的：不必立即落地，但要让任何想做进一步 mod 的开发者能按图索骥。
> 分类：1⃣ 发光系 · 2⃣ PBR 扩展 · 3⃣ 接缝/兼容性工程 · 4⃣ 全新渲染特性 · 5⃣ 插件层"不改 shader"玩法。

## 7.1 评估坐标轴

每条 idea 都标注三个维度：

| 维度 | 级别 | 含义 |
|---|---|---|
| **工程量** | S / M / L / XL | S=单文件几行 / M=一个 shader patch+mtrl 改 / L=shpk 结构改+插件 UI / XL=重写 shader |
| **风险** | [!]低 / 中 / 高 | 低=改坏只影响发光 / 中=可能影响所有皮肤渲染 / 高=随机花屏或崩溃 |
| **依赖** |  | 前置章节 / 前置脚本 / 需先验证的事项 |

---

## 7.2 分类 1 —— 发光系列（Emissive）

### 7.2.1 修复 Body 切 Emissive 的光泽丢失（Ch5 路径 A 具体化）

- **目标**：启用发光时 body 不再失去 normal.alpha gloss mask，消除颈部接缝
- **落点**：PS[19] L297 之后插入 `mul r0.w, r0.z, v1.w`；L299 改 `v1.w → r0.w`
- **工程量**： **M**（dxbc_patcher 增加"在指定 offset 插入 2 条指令"能力 + 32 个 Emissive PS 逐个定位插入点）
- **风险**：[!] **低**（仅影响 specular mask 计算，不动光照模型）
- **依赖**： Ch6 v1 §6.2 已标出精确行号；需写 DXBC pattern matcher 处理 32 个兄弟 PS
- **扩展**：同一手法可用于 BodyJJM PS（如果未来也允许 BodyJJM → Emissive 切换）

### 7.2.2 线性 emissive 响应（去掉 sRGB 平方）

- **目标**：让发光强度和 UI 上的 sRGB 色板一致，不再被 `cb0[3]²` 的平方关系压暗
- **落点**：PS[19] L296 改为 `mov r1.xyz, cb0[3].xyzx`（不平方）
- **工程量**： **S**（1 条指令替换 × 32 PS）
- **风险**：[!] **低**（纯后处理曲线变化）
- **依赖**： 无
- **扩展**：引入 `g_EmissiveGamma` mtrl 常量，让用户选 `pow(emissive, gamma)`

### 7.2.3 关闭 emissive mask（取消 normal.alpha 乘法）

- **目标**：让发光均匀覆盖整张贴图，不受 normal alpha 限制（有些用户希望整件材质发光）
- **落点**：PS[19] L297 删除（或改为 `mov r1.xyz, r1.xyzx`）
- **工程量**： **S**
- **风险**：[!] **低**（发光范围扩大，光照模型不变）
- **依赖**： 无
- **扩展**：引入 shader key `EmissiveMaskMode`，运行时选 "normal.alpha mask" / "无 mask" / "vertex color mask" / "ColorTable mask"

### 7.2.4 双通道 normal.alpha 复用（gloss mask + emissive mask）

- **目标**：让一张法线贴图同时承载 gloss mask 和 emissive mask，不必二选一
- **落点**：PS[19] L295 采样后，把 `r0.z`（normal.alpha, 0-1 float）拆成两路：
  - 低 4 位当 gloss strength：`floor(r0.z * 16) / 16`
  - 高 4 位当 emissive strength：`frac(r0.z * 16)`
  - 需要 3-4 条 DXBC 指令实现位级拆分
- **工程量**： **M**（DXBC 新增 `ftou` + 位运算）
- **风险**：[!] **中**（用户贴图需要重制，社区贴图兼容性被打破）
- **依赖**： 需要额外的 shader key `NormalAlphaMode`，区分"传统单通道"和"双通道"
- **扩展**：升级到 16 位法线贴图后，可以分成 8+8 位（更精细的 mask）

### 7.2.5 视角相关发光（Anisotropic Emissive）

- **目标**：让发光只在特定视角角度出现（类似 rim light 的"只在侧面亮"效果）
- **落点**：PS[19] L753 前，插入一条 `dp3_sat r2.w, r7.xyzx, r3.xyzx`（NdotV），然后 `mul r1.xyz, r1.xyzx, (1 - r2.w)⁴`（边缘加强）
- **工程量**： **S**（4-5 条新指令）
- **风险**：[!] **低**
- **依赖**： 要扩容 cb5 cbuffer（加 `m_EmissiveViewPower`），或复用 cb0 里一个未占用 param（比如 Ch4a 里未识别的 0x15B70E35）
- **扩展**：把系数做成 ColorTable 里的 per-row 参数，per-layer 可选"正面发光/侧面发光"

### 7.2.6 Bloom-ready emissive（显式超 1.0 发光值）

- **目标**：让发光色能写入 HDR 超 1.0 区间，触发 Bloom 后处理的辉光
- **落点**：PS[19] L773 的 `max r0.xyz, 0` 前移 + 取消 L774 的 `sqrt` 对 emissive 分量的限制
- **工程量**： **M**（需要保留 emissive 通道不做 gamma 压缩）
- **风险**：[!] **中**（可能让 emissive 过曝，影响整个 tone mapping）
- **依赖**： 需要先确认游戏的 Bloom 后处理是否读 HDR buffer（看上下文 `g_SamplerLightDiffuse` 是否 HDR 格式）
- **扩展**：配合 7.2.5，实现"特定角度才过曝辉光"

---

## 7.3 分类 2 —— PBR 扩展

### 7.3.1 湿润度 Wetness

- **目标**：mtrl 侧提供 0-1 wetness 滑条，皮肤变光滑 + Fresnel 增强
- **落点**：
  - 给 cb5 扩容到 2 vec4（第 2 个 vec4 存 `m_Wetness`）
  - 在 Block E2/E3 的 GGX 里：L499 后插入 `mul r4.w, r4.w, (1 - cb5[1].x × 0.8)`（降低 α）
  - 在 L497 后插入 `mad r13.xyz, r13.xyzx, (1 + cb5[1].x × 5), 0`（Fresnel 系数加强）
- **工程量**： **L**（扩 cbuffer + 改 mtrl 写入 + DXBC 修改 × 32+32 PS）
- **风险**：[!] **中**（GGX 参数改动会影响所有皮肤，需要仔细调参）
- **依赖**： Ch6 v2 §6.3 已标出 L499（α²）和 L497（Fresnel）的位置；需要 runtime hook 往 cb5 写 wetness 值
- **扩展**：引入"湿润贴图"（per-pixel wetness），在 L295 采样一张新纹理作 wetness mask

### 7.3.2 金属皮肤（Metallic Skin）

- **目标**：局部区域变金属质感（例如赛博朋克/神秘图腾）
- **落点**：
  - 在 PS[19] L342 之后（F0 计算完），插入 `mad r8.xyz, metallic_factor, (1 - r8.xyz), r8.xyz`，把 F0 向 1.0 方向拉
  - `metallic_factor` 从 ColorTable per-row 读，或从 cb0 里未命名常量借用
- **工程量**： **M**（单行 DXBC 插入 + ColorTable 语义扩展）
- **风险**：[!] **中**（F0 改大会让皮肤偏高光硬朗）
- **依赖**： 我们已有 ColorTable 注入框架（`MtrlFileWriter.BuildSkinColorTablePerLayer`），直接扩字段即可
- **扩展**：配合 metallic 改 albedo（参考 UE4 把 albedo 当 specular color 用）

### 7.3.3 各向异性强化（Anisotropic Roughness）

- **目标**：把"丝质""毛发""金属拉丝"等各向异性效果引入 skin 材质
- **落点**：PS[19] Block E1 L395 的 `cb7[r1.w + 3].yz` 是 skin LUT 的 α_t/α_b。改用 ColorTable per-row 值替换（L395 前插入 sample ColorTable → 覆写 r13.xy）
- **工程量**： **L**（涉及 skin LUT 逻辑分支，需确保 skinID == 1 或 3 才走 E1，其他 skinID 需要额外引导到 E1）
- **风险**：[!] **高**（Block E1 是 244 条指令的复杂 SSS 管线，动一条牵连全身）
- **依赖**： Ch6 v2 §6.2 Phase 4 详细标注了 L395 的作用
- **扩展**：per-layer 各向异性主轴旋转（cb7[r1.w + 2].y）让"拉丝方向"可画

### 7.3.4 可配置 SSS 曲线

- **目标**：让 UI 暴露一个"SSS 曲线预设"下拉（浅色/自然/深色皮肤）
- **落点**：PS[19] Block E1 L388 那 6 个魔法常数 `(0.5, -3.65, 17.0, 0.5, -3.98, -16.78)`，改成从 cb0 读取
- **工程量**： **L**（cb0 扩字段 + DXBC 改 + mtrl 侧写入 + UI 下拉）
- **风险**：[!] **高**（这 6 个数是 SE 调过的 SSS 曲线，乱改会导致皮肤发蓝/发绿）
- **依赖**： 需要先逆向确认这 6 个常数的物理含义（Ch6 v2 §6.2 Phase 3 标了 "C 级推断"）；建议先把 Jimenez SSS 论文读一遍
- **扩展**：通过 ColorTable per-row 配置，实现"躯干/手腕/颈部不同 SSS"

### 7.3.5 基于色彩的 Fresnel（Iridescence / 珠光）

- **目标**：Fresnel 系数变成随角度变色（蝴蝶翅膀、CD 光碟效果）
- **落点**：PS[19] 所有 Fresnel 位置（L422, 449, 473, 497, 561）的 `(0.953479, 0.046521)` 改成从 cb0 读 `(F₁ - F₀, F₀)`，其中 F₀/F₁ 是两种色
- **工程量**： **XL**（要在 R/G/B 三通道各自独立做 Fresnel，涉及 15+ 处 DXBC 修改）
- **风险**：[!] **高**
- **依赖**： Ch6 v2 §6.2/6.3 定位全部 Fresnel；需要定义新 mtrl 字段 `g_FresnelColorLow`、`g_FresnelColorHigh`
- **扩展**：把角度函数换成 `cos(nθ)` 多周期震荡 → 真正的 iridescence

### 7.3.6 ClearCoat（清漆层）

- **目标**：皮肤上再叠一层镜面反射（模拟化妆、涂油）
- **落点**：Block I（L736-761）之前，重复一遍 Block E2 的 GGX 计算但用不同 roughness（从 cb0 读 `g_ClearCoatRoughness`），然后把结果加到 `r5`（specular 累加）上
- **工程量**： **XL**（整套 BRDF 再来一次）
- **风险**：[!] **中高**（容易让皮肤"发油"过度）
- **依赖**： Ch6 v2 §6.3 的 E2 模板；mtrl 扩字段 `g_ClearCoatRoughness`、`g_ClearCoatMask`（贴图）
- **扩展**：结合 wetness（7.3.1），实现 "化妆 + 出汗 + 油光" 多层 mask

---

## 7.4 分类 3 —— 接缝与兼容性工程

### 7.4.1 脸部同步 Emissive（Ch5 路径 C 落地）

- **目标**：UI 一键开关"脸部跟随发光变体"，让颈部两侧都走 PS[19]
- **落点**：`MtrlFileWriter.cs` 增加一个 Face mtrl 分支，同样写入 SkinType=Emissive
- **工程量**： **S**（C# 侧一个 if）
- **风险**：[!] **低中**（脸部观感会有细微变化，但不会破坏渲染）
- **依赖**： Ch3 §3.3 已确认 Face PS[2] 与 Emissive PS[19] 的 UV 采样源一致；需要验证 face mtrl 的 normal.alpha 是否默认为 0（否则脸会莫名发光）
- **扩展**：UI 里让用户单独选"脸部贴花"，把贴花只画在脸上而非身体

### 7.4.2 完整的 Body 分支 emissive 注入（Ch5 路径 B）

- **目标**：不强切 SkinType，保留 Body 分支，给它"附加"ColorTable 发光能力
- **落点**：
  - Body PS[8] 资源表加 cb5（`g_MaterialParameterDynamic`）、s5/t10（`g_SamplerTable`）
  - Body PS[8] 尾段（L760 等效位置）插入 3-4 条 emissive mul + 加到 r0.yzw
- **工程量**： **XL**（32 个 Body PS × DXBC 结构重写 + RDEF 扩展 + dxbc_patcher 重构）
- **风险**：[!] **高**（RDEF 改动失败会让整个 Body 渲染崩溃）
- **依赖**： Ch3 §3.2 已知 Body 的 cbuffer 布局差异；Ch6 v1 §6.10 给出尾段插入位置；需要写全新的 `patch_body_ps.py`
- **扩展**：同理 Face/BodyJJM 分支都能注入，最终实现"任何 SkinType 都能发光"

### 7.4.3 ALum 兼容层（可选）

- **目标**：让 SkinTattoo 和 ALum mod 能共存（当前 ALum 抢占了 skin.shpk 的 shader key `0x9D4A3204`）
- **落点**：MtrlFileWriter 检测 mtrl 里已有 ALum key，主动跳过冲突或降级到 UI 警告
- **工程量**： **S**
- **风险**：[!] **低**
- **依赖**： 从 MEMORY.md 已知"ALum 已废弃，不需兼容"。这条现阶段**不用做**，留作未来 ALum 复活时的前瞻

---

## 7.5 分类 4 —— 全新渲染特性

### 7.5.1 深度感知发光（Depth-aware Emissive）

- **目标**：发光强度随角色距相机远近变化（近处强、远处弱 → 表现"发热"）
- **落点**：PS[19] L753 前，计算 `r2.w = distance(worldPos, camera) / maxDistance`，从 cb3 `g_CameraParameter` 和 `cb0[27..]` 里的 world position 获取，然后 `mul r1.xyz, r1.xyzx, (1 - r2.w²)`
- **工程量**： **M**（需要理清 world position 在 PS 里是否直接可得，看 v0..v4 哪个传了 worldPos）
- **风险**：[!] **中**（可能找不到合适的 varying，需要 VS 侧也改）
- **依赖**： 需要先看 VS 的 dcl_output_siv 列表，确认 worldPos 是否在 v0/v1/v3/v4 之一
- **扩展**：结合时间（cb1 或 cb6 里的 `m_LoopTime`）实现"脉动式发热"

### 7.5.2 屏幕空间 Cavity 强化

- **目标**：利用 GBuffer 的 depth gradient 检测皮肤凹陷（皱纹、毛孔），加强阴影
- **落点**：Block B 已经采了 GBuffer（t2-t4），在 L325 采样之后计算 Laplacian：
  - 多采几次 t2 with offset，计算 `∇²depth`
  - 把结果乘到 albedo（r8 或 r9）上
- **工程量**： **L**（新增 5-8 条 sample 指令 + 卷积计算）
- **风险**：[!] **中**（增加 shader 开销，特别是高分辨率下）
- **依赖**： 无
- **扩展**：把强化系数做成 ColorTable 可调 → per-layer "多毛孔/少毛孔"

### 7.5.3 次级法线叠加（Detail Normal）

- **目标**：在基础法线之上叠一层高频细节法线（毛孔、纹理）
- **落点**：Block B L318 之前，多采一次法线贴图（新的 t11 = detail normal），做 UDN/RNM 法线混合
- **工程量**： **L**（新增 sampler+texture slot，扩 RDEF）
- **风险**：[!] **中**（新 slot 未被其他 PS 占用，但要注意 mtrl 写入时的 tex 索引）
- **依赖**： 需要先确认 Body/Face mtrl 的 tex 最大索引，看 t11 是否安全
- **扩展**：per-layer 细节法线强度（ColorTable 配）

### 7.5.4 时间动态 emissive（不依赖 ColorTable）

- **目标**：把时间从 cb6 `g_PbrParameterCommon.m_LoopTime` 读出来，PS 里直接做脉动
- **落点**：PS[19] L296 前，`mul r1.xyz, cb0[3].xyzx, (sin(cb6[0].x * freq) × amp + base)`
- **工程量**： **M**（几条 DXBC + 需要 cb6 的字段定位）
- **风险**：[!] **低**
- **依赖**： Ch6 v1 §6.4 提到 cb6 是 g_AmbientParam（在 Emissive 里）；但 `m_LoopTime` 在 `g_PbrParameterCommon` (cb2)，需要改索引
- **扩展**：我们现在的 ColorTable 里已经有 anim 字段（AnimSpeed/Amp/Mode），这一条其实是 ColorTable 方案的"廉价版"

### 7.5.5 超采样 SSS（Gaussian Kernel）

- **目标**：用屏幕空间 Gaussian 多次采样模糊光照（更真实的皮肤散射）
- **落点**：Block E1 的两个 Gaussian 已经是"解析 Beckmann"，可以替换成 Block B 的 `g_SamplerLightDiffuse` (t0) 额外多次 sample with jitter
- **工程量**： **XL**（完全重写 Block E1，244 条指令）
- **风险**：[!] **极高**
- **依赖**： 需要深入理解 Jimenez 2015 Separable SSS 论文
- **扩展**：省略 - 此方案成熟度极高，实现 1 次但维护永久

---

## 7.6 分类 5 —— 插件层玩法（不改 shader）

这一类是改进 SkinTattoo 插件本身的功能，完全不碰 skin.shpk。

### 7.6.1 贴花 → 粗糙度 mask

- **目标**：用户画的贴花不再只影响 emissive/diffuse，还可以当粗糙度 mask（变哑光/变亮）
- **落点**：`DecalLayer` 加一个 `TargetMap = Roughness` 选项；`MtrlFileWriter` 写入 `g_NormalScale` 或 ColorTable roughness 字段
- **工程量**： **M**（C# 侧：DecalLayer 枚举扩展 + 新 target + ColorTable 通道）
- **风险**：[!] **低**（纯插件层扩展）
- **依赖**： 我们的 ColorTable 扩展框架已支持 per-layer 字段（Ch4 §4.3 BuildSkinColorTablePerLayer）；已有 `AffectsRoughness` 字段的开关
- **扩展**：同样做 metallic / sheen / iridescence 目标

### 7.6.2 贴花 → 金属度 mask

- **目标**：贴花指定区域变金属
- **落点**：同 7.6.1，新增 `TargetMap = Metallic`
- **工程量**： **M**
- **风险**：[!] **低**
- **依赖**： 需要先做 7.3.2（shader 侧要支持金属化），否则单纯写 ColorTable 没效果
- **扩展**：配合 7.3.5 Iridescence，做"铬/钛/金/银"预设色卡

### 7.6.3 基于 UV 分区的多 ColorTable

- **目标**：同一角色不同身体区域（脸/胸/手/腿）用不同 ColorTable
- **落点**：现在我们一张 ColorTable 覆盖全身；扩展为根据 skin ID（normal.alpha）路由到不同 ColorTable 行块
- **工程量**： **M**（纯 ColorTable 数据结构扩展，shader 不动）
- **风险**：[!] **低**
- **依赖**： Ch4 §4.4 里的 skinID LUT 索引机制 —— 我们能用 normal.alpha 给的 0-255 skin ID 选择不同 ColorTable 段
- **扩展**：用户可以画"只在手腕的贴花"通过精确 skin ID 匹配

### 7.6.4 实时预览的 3D 光照改进

- **目标**：ModelEditorWindow 里加一个可旋转的主光源 + ambient 选项，更接近游戏内观感
- **落点**：`DxRenderer` 里添加方向光参数 + HLSL shader
- **工程量**： **L**（渲染器扩展）
- **风险**：[!] **低**（纯 UI/渲染层）
- **依赖**： 无
- **扩展**：加入 FFXIV 游戏内 cubemap 采样（用贴图模拟 g_SamplerReflectionArray）

### 7.6.5 贴花资源库 → 社区共享

- **目标**：通过 HTTP API 让用户从社区仓库一键拉取贴花 preset
- **落点**：新增 `LibraryService`，GET/POST 一个 remote index，本地缓存
- **工程量**： **L**（需要后端）
- **风险**：[!] **低**
- **依赖**： 现有 `ResourceBrowser` 已有本地库；扩展为远程即可
- **扩展**：版本管理、评分、标签

---

## 7.7 实施优先级矩阵

| 优先级 | idea | 理由 |
|---|---|---|
|  P0 | 7.4.1 脸部同步 Emissive | 极小工程量就能消接缝，即刻可用 |
|  P0 | 7.2.1 Block A gloss mask 再注入 | Ch5 路径 A 的根治方案，2 行 DXBC |
| ⭐ P1 | 7.6.1 贴花→粗糙度 | 插件能力从"只能发光"升级到"全 PBR 贴花" |
| ⭐ P1 | 7.2.2 线性 emissive | 单条指令改动，但 UI 响应立刻变直观 |
| ⭐ P1 | 7.2.3 关闭 emissive mask | 解决"我就是想整件发光"这类用户诉求 |
|  P2 | 7.3.1 Wetness | 技术储备完备，可以做一个 demo 验证可行性 |
|  P2 | 7.3.2 Metallic Skin | 同上，一旦 7.6.2 做完就能看见效果 |
|  P3 | 7.5.1 深度感知发光 | 效果炫酷但工程量中等，需要额外 VS 侧工作 |
|  P4 | 7.4.2 Body 分支原生 emissive | 终极目标，能做到就消除了所有"切 SkinType"的副作用 |
|  P5 | 7.3.4/7.3.5/7.5.5 | XL 级工程量，留给未来有专门时间的大改造 |

## 7.8 给自己的建议（如果我是贡献者）

在正式开始任何 idea 之前：

1. **先做完 Ch5 路径 A（7.2.1）**。这是"修 bug + 获得 DXBC 改写经验"一石二鸟 —— 实现完这条，整个 patcher 工具链会成熟到能支撑后续所有 shader 改造。
2. **把 `dxbc_patch_colortable.py` 重构为"指令级 DSL"**：目前它只能"替换 PS[19] 指定段"，应当升级为能从 pattern-match 定位指令、按相对偏移插入 / 替换。这将是所有后续 shader mod 的基础。
3. **建立自动化回归测试**：写一个 python 脚本，对 patched shpk 做"加载 → 全 SkinType × SceneKey 枚举 → 每组 node 检查 selector 仍可命中"。防止 patch 过程中意外破坏 NodeSelectors。
4. **先做 7.6.1 / 7.6.2 等插件层功能**，再做 shader 改造。插件层改动可逆、风险低，用户也能立即感知进步。shader 改造需要"一杆子到底"，不适合半成品发布。
5. **任何 shader 改造都先在 `_shpk_analysis` 里做**，不要直接改 `ShaderPatcher/skin_patched.shpk`。等验证好了再复制。
6. **每次改动都更新 docs/skin-shpk-deep-dive**。这套文档就是维护 shader mod 的"地图"，脱节了后续人就找不到路。

## 7.9 整套研究收束

回看 Ch0 规划的 8 章目标：

| 章节 | 状态 | 交付文件 |
|---|---|---|
| Ch0 总览 | [x] | `00-overview.md` |
| Ch1 shpk 结构 | [x] | `01-shpk-structure.md` |
| Ch2 SkinType 分支 | [x] | `02-skintype-branches.md` |
| Ch3 DXBC 对比 | [x] | `03-dxbc-body-vs-emissive.md` |
| Ch4 cbuffer/sampler/texture | [x] | `04-cbuffers.md` |
| Ch4a MaterialParam CRC 附录 | [x] | `04a-materialparam-crc-appendix.md` |
| Ch5 接缝与改造路径 | [x] | `05-seam-and-fix-paths.md` |
| Ch6 v1 PS[19] 逐段解剖 | [x] | `06-ps19-anatomy.md` |
| Ch6 v2 Block E 细解 | [x] | `06b-block-e-sss-ggx.md` |
| Ch7 高级效果 idea 清单 | [x] | `07-advanced-mod-ideas.md`（本文）|

**对应产出工具**：
- `SkinTatoo/ShaderPatcher/dump_nodes.py`（Ch2 的成果）
- `SkinTatoo/ShaderPatcher/extract_ps.py`（Ch3 的成果）
- `SkinTatoo/ShaderPatcher/extracted_ps/ps_{002,008,012,019}*`（Ch3-Ch6 的取证数据）
- 完整的 parse_shpk.py CRC 认领补丁（Ch4a 的成果）

**对应结论**：
1. **接缝成因被彻底闭环**（Ch5）：不是"光照模型不同"，而是 Body 有一条 `normal.alpha` gloss mask 链、Emissive 没有；以及 Emissive 多一条 `cb5[0] × specular` 调制
2. **skin.shpk 光照本质不是 forward 而是 composite**（Ch6 v1 §6.3）：主光照已烘焙进 `g_SamplerLightDiffuse/Specular`，pass[2] 只做 skin-specific 合成
3. **Block E 是 Kelemen SSS + 各向异性 Beckmann + 标准 GGX 三合一**（Ch6 v2），给出了所有 Fresnel/Beckmann/GGX 常数的物理含义
4. **所有改造路径的 DXBC 行号都已精确定位**（Ch5、Ch6、Ch7），可以立即动手

**研究本身闭环**。

## 状态

- [x] 全部章节完成
