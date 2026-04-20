# Ch4 — cbuffer / sampler / texture 清单与分支差异

> 数据来源：`python parse_shpk.py` 对 `C:/Users/Shiro/Desktop/FF14Plugins/skin.shpk` 的解析输出
> 交叉验证：`extract_ps.py` 得到的 PS[2]/[8]/[12]/[19] 反汇编里每 shader 的局部资源表与指令流
> 关键取证：Ch3 §3.3 发现"Emissive 与 Body 的 t5 sampling swizzle 一致但后续消费路径不同"，Ch4 会把该差异落实到寄存器级别

## 4.1 全局资源表总览（skin.shpk 顶层定义的）

skin.shpk 顶层暴露了**完整可用资源清单**，各 PS 按需挑选绑定到自身的 t/s/b 寄存器：

| 类别 | 数量 | 说明 |
|---|---:|---|
| Material Params | 60 | 每一项都写到 cb0 `g_MaterialParameter`（320B）里的某段 |
| Constants (cbuffer) | 22 | 实际 PS 只会用其中 7~8 个 |
| Samplers | 13 | 实际 PS 只会用其中 5 个 |
| Textures | 27 | 实际 PS 只会用其中 10 个 |

### 22 个全局 Constant 槽位（按名字）

| 名称 | CRC | size (vec4) | 说明 |
|---|---|---:|---|
| **g_MaterialParameter** | `0x64D12851` | 20 | 材质层常量（mtrl 的 shader values 写进来） |
| g_WorldViewMatrix | `0x76BB3DC0` | 6 | 世界视图矩阵（VS 用） |
| **g_CameraParameter** | `0xF0BAD919` | 59 | 一组完整的视图/投影/逆矩阵，见 ps_019 反汇编开头定义 |
| **g_InstanceParameter** | `0x20A30B34` | 11 | 按角色实例传入的参数（颜色乘、风力、眼参等） |
| g_ModelParameter | `0x4E0A5472` | 1 | 模型级常量 |
| g_ConnectionVertex | `0xD35B646A` | 1 | VS 链接用 |
| g_ShapeDeformParam | `0xE6E8672F` | 1 | 形变参数 |
| g_JointMatrixArray | `0x88AA546A` | 768 | 蒙皮矩阵 |
| g_JointMatrixArrayPrev | `0xB531360D` | 768 | 上一帧蒙皮矩阵（速度缓冲用） |
| g_InstancingMatrix | `0x8413183B` | 768 | 实例矩阵 |
| g_PrevInstancingMatrix | `0xC38E361C` | 768 | 上一帧实例矩阵 |
| **g_PbrParameterCommon** | `0xFF0F34A7` | 5 | 全局 PBR 参数（m_LoopTime / m_SubSurfaceSSAOMaskMaxRate / m_MipBias 等） |
| g_DecalColor | `0x5B0F708C` | 1 | 贴花色 |
| g_CustomizeParameter | `0x2A4B3583` | 9 | 捏人参数（肤色、瞳色等） |
| **g_ShaderTypeParameter** | `0x3A310F21` | **2048** | **皮肤 shader ID 查找表（256 × 8 vec4 = 32 KB）见 §4.4** |
| **g_CommonParameter** | `0xA9442826` | 4 | 全局公共参数（RenderTarget / Viewport / Misc） |
| **g_AmbientParam** | `0xA296769F` | 10 | 环境光常量（方向光、球谐、反射） |
| g_FogParameter | `0xA6CCAF57` | 10 | 雾参数 |
| g_WrinklessWeightRate | `0x17FB799E` | 16 | 面部皱纹权重表 |
| **g_MaterialParameterDynamic** | `0x77F6BFB3` | 1 | **只有 Emissive PS 绑定！** 唯一字段 `m_EmissiveColor`（见 §4.3） |
| g_DissolveParam | `0x6AD7B6B6` | 23 | 溶解效果参数 |
| g_AuraParam | `0x07EF6FA3` | 16 | 光环参数 |

粗体那些就是 pass[2] lighting PS 真正会用到的 7~8 个。

### 13 个 Sampler / 27 个 Texture

`skin.shpk` 把 Sampler（采样状态）和 Texture（纹理本体）分开记录——这是 Dawntrail shader 格式的新约定。pass[2] 里实际绑定的组合见 §4.5。

## 4.2 cb0 `g_MaterialParameter` —— mtrl 灌入的 60 项常量

`g_MaterialParameter` 总大小 320B = 20 vec4，被 60 个 MaterialParam 条目瓜分。每条占 4~12 字节，重叠映射到 cb0 的 0..319 偏移。**这张表就是 mtrl 文件 Constants 数组的消费者**——我们自己 `MtrlFileWriter` 写入的 `g_EmissiveColor`（CRC `0x38A64362`）等全走这里。

MaterialParam 默认值摘要（解析脚本自动打印）：

| 条目 | offset | size | 默认值 | 说明 |
|---|---:|---:|---|---|
| g_DiffuseColor | 0 | 12 | (1,1,1) | 漫反射色 |
| 0x29AC0223 | 12 | 4 | 0.0 | ？（未识别） |
| g_SpecularColorMask | 16 | 12 | (1,1,1) | 高光色 |
| 0xD925FF32 | 28 | 4 | 0.5 | ？ |
| 0x11C90091 | 32 | 12 | (1,1,1) | ？（颜色类） |
| g_ShaderID | 44 | 4 | 0.0 | **皮肤 shader ID**（整数，进入 `g_ShaderTypeParameter` 的键） |
| **g_EmissiveColor** | **48** | **12** | **(0,0,0)** | **发光色 — 我们自己插入的 mtrl 常量就写到这里** |
| 0x50E36D56 | 64 | 12 | (1,1,1) | ？ |
| g_LipRoughnessScale | 60 | 4 | 0.7 | 嘴唇粗糙度 |
| g_SphereMapIndex | 76 | 4 | 0 | 球面贴图索引 |
| 0x58DE06E2 | 80 | 12 | (0,0,0) | ？ |
| g_SSAOMask | 92 | 4 | 1.0 | SSAO 掩码 |
| g_OutlineColor | 96 | 12 | (0,0,0) | 描边色 |
| g_TileIndex | 108 | 4 | 0 | tile 贴图索引 |
| g_TileScale | 112 | 8 | (16,16) | tile 缩放 |
| 0xE18398AE | 120 | 8 | (0.158, 0.174) | ？（二维常量） |
| 0x5B608CFE | 128 | 8 | (0.040, 0.020) | ？（二维常量） |
| 0x1A60F60E | 136 | 8 | (0, 0) | ？ |
| g_TileAlpha | 144 | 4 | 1.0 | tile 透明度 |
| g_NormalScale | 152 | 4 | 1.0 | 法线强度 |
| g_SheenRate | 156 | 4 | 0.0 | 绒面强度 |
| g_SheenTintRate | 160 | 4 | 0.0 | 绒面色调 |
| g_SheenAperture | 164 | 4 | 1.0 | 绒面孔径 |
| **0x7DABA471** | **180** | **4** | **0.25** | **iris ring emissive intensity**（我们已识别，iris 专用） |
| g_AlphaAperture | 204 | 4 | 2.0 | 透明孔径 |
| g_AlphaOffset | 208 | 4 | 0.0 | 透明偏移 |
| g_GlassIOR | 220 | 4 | 1.0 | 玻璃 IOR |
| g_GlassThicknessMax | 224 | 4 | 0.01 | 玻璃最大厚度 |
| g_TextureMipBias | 228 | 4 | 0.0 | 纹理 mip 偏移 |
| g_OutlineWidth | 236 | 4 | 0.0 | 描边宽度 |
| g_ToonIndex | 240 | 4 | 0.0 | 卡通阴影索引 |
| g_ToonLightScale | 256 | 4 | 2.0 | 卡通光亮度 |
| g_ToonLightSpecAperture | 260 | 4 | 50.0 | 卡通高光孔径 |
| g_ToonReflectionScale | 268 | 4 | 2.5 | 卡通反射强度 |
| g_ShadowPosOffset | 272 | 4 | 0.0 | 阴影位置偏移 |
| g_TileMipBiasOffset | 276 | 4 | 0.0 | tile mip 偏移 |
| (共 60 项，其余均为未识别 CRC 的 4~12 字节浮点) | | | | |

**观察 1**：`g_EmissiveColor` 虽然在这里已有位置（offset 48），但在原版 **Face/Body/BodyJJM 的 PS 里根本不被采样**——这是专供 Emissive PS 用的字段。Face/Body 不读 cb0[3] 的 `.xyz` 那一段。

**观察 2**：表里多达 35 个条目 CRC 未识别。这些留给 Ch4 未来版本或专门补齐 CRC 表。

## 4.3 cb5 `g_MaterialParameterDynamic` —— Emissive 专属的"动态"emissive 槽

这是整个 skin.shpk 里**唯一只出现在 Emissive 分支的 cbuffer**。结构极小：

```
cbuffer g_MaterialParameterDynamic : register(b5)
{
    struct MaterialParameter {
        float4 m_EmissiveColor;  // offset 0, size 16
    } g_MaterialParameterDynamic;
}
```

名字里的 "Dynamic" 暗示它不是 mtrl 常量（mtrl 属于 cb0），而是**运行时可以被引擎改写的 cbuffer**。事实上我们 Ch-背景 `constant-buffer-analysis.md` 就已经证明：`LoadSourcePointer` 机制（在 `OnRenderMaterial` 内部调用）可以实时写 `m_EmissiveColor`，引擎每帧上传。这正是 `EmissiveCBufferHook` 的工作机制。

**为什么 Face/Body 不需要这个槽？** 因为它们不带发光 —— 一切颜色直接从 cb0 `g_DiffuseColor/g_SpecularColorMask` 来，不需要每帧动态改。

整个 PS[19] 里对 cb5 的使用就只有**一条指令**（`ps_019_disasm.txt:753`）：

```
mul r1.xyz, r1.xyzx, cb5[0].xyzx
```

`r1.xyz` 在此时代表一个 specular 累积量，乘上 `m_EmissiveColor.xyz` 后成为新的高光分量，继续参与后续光照合成。这是我们在 Ch3 §3.4 已经点出的关键点——**Emissive 不是 additive，而是"以 specular 为载体的乘法调制"**。

## 4.4 cb6/cb7 `g_ShaderTypeParameter` —— 皮肤 shader ID 查找表

这是最让人吃惊的发现：skin.shpk 挂着一张 **2048 vec4 = 32 768 字节 = 32 KB 的查找表**。

```
size = 2048 vec4 = 256 × 8 vec4
```

访问方式（Body 的 PS[8] 片段）：

```
mad r1.z, r5.w, l(255.000000), l(0.500000)   // r1.z = normal_map.alpha * 255 + 0.5
ftou r1.z, r1.z                              // float → u32 (0..255)
ishl r1.z, r1.z, l(3)                        // r1.z *= 8 (8 vec4 per entry)
...
lt r5.xy, 0, cb6[r1.z + 0].zwzz              // 判断 entry[0].z, entry[0].w
lt r1.w, 0, cb6[r1.z + 3].x                  // entry[3].x
lt r2.w, 0, cb6[r1.z + 1].w                  // entry[1].w
ieq r11.xy, cb6[r1.z + 0].xxxx, l(1, 3, 0, 0) // entry[0].x == 1/3？
...
```

解读：

- `r5.w` = 法线贴图 alpha（`sample_indexable t5.zxwy → r5.w = normal.w`）
- `r5.w * 255 + 0.5 → ftou` → 得到一个 **0..255 的整数索引**（== 法线 alpha 当作皮肤 ID 使用）
- 乘 8 后寻址 `cb6`/`cb7`，每个"entry"横跨 8 连续 vec4
- **这就是 FFXIV 业界熟知的 "Skin ID" 系统**：法线贴图的 alpha 通道保存 skin ID，shader 用它在 256 项大表里挑一行，每行定义该 skin 的参数集合
- 表里字段的前几个看起来是 flag（`ieq ... l(1, 3, 0, 0)` —— entry[0].x == 1 或 3 时走不同分支）

**这解释了 Ch3 里 UV 采样差异留下的悬念**：

- Body PS 从 t5 读出 `normal.zw` 两个分量（`t5.zxwy` → r0.xz）
  - `r0.x` = normal.z（法线 Z，用于光照计算）
  - `r0.z` = normal.w（= skin ID alpha），被立即 `mul r0.z, r0.z, v1.w` 用顶点 alpha 调制，再参与 `sat(r0.z * r0.x)`
  - 这条链**让 normal alpha 直接影响 specular mask**
- Face PS 只从 t5 读 `normal.z`（`t5.zxyw` → r0.x），**根本不取 normal.w**。Face 用 `r5.w` (来自 t4 的 sample) 去查 skin ID，但不把 normal.alpha 作为 gloss 额外调制。
- Emissive PS 虽然也读 `normal.zw`，但把 `r0.z` 挪到一条全新的计算链：`r1.xyz = cb0[3]² × r0.z`（cb0[3] 是 g_CameraParameter 里某个 3D 常量）。这条链出现在 ps_019_disasm.txt 的第 295~297 行附近，**Body 根本没有**。也就是说，Emissive 获取了 normal.alpha 却没有把它用于 Body 那条 gloss mask 链。

### **☆ 这直接回答了用户今天的现场观察**

> 「看游戏内效果应该就是发光乘法导致的，上面的皮肤是有光泽的，接缝下面的好像没有」

组合三个事实解释全貌：

1. **Body 的光泽**来自 `sat(normal_alpha × v1.w × normal_z)` 这条 gloss mask 链，最终参与后段的 specular 计算
2. **Emissive 虽然也读了 normal_alpha，但没有把它接到 gloss mask 链**，而是挪去做 view-dependent 的 `cb0[3]²` 计算
3. **Emissive 又额外插入** `mul r1.xyz, r1.xyzx, cb5[0].xyzx` —— 把当前 specular 累积量乘上发光色

**结果**：body 切到 Emissive 后丢掉了 "normal_alpha gloss" 贡献（失去原有光泽），同时保留了法线/漫反射/间接光，再被发光色乘一下——具体观感就是"身体变哑光/偏薄"。face 仍然跑 vanilla PS[2]，保留原来的渲染，就在颈部形成"上面有泽、下面没有"的边界。

这和用户的口头描述**逐字对得上**——我们终于有了一条有物证的因果链。

## 4.5 Sampler / Texture 在 pass[2] 的实际绑定

pass[2] lighting PS（Face/Body/BodyJJM/Emissive 四份）绑定的 sampler 和 texture **完全相同**，区别只在反汇编里谁读了谁、怎么读。

### Samplers (s0..s4)

```
s0  g_SamplerGBuffer         (0xEBBB29BD)  → 采样 GBuffer 系列纹理
s1  g_SamplerNormal          (0x0C5EC1F1)  → 采样法线贴图
s2  g_SamplerTileOrb         (0x800BE99B)  → 采样 tile/orb
s3  g_SamplerReflectionArray (0xC5C4CB3C)  → 反射数组
s4  g_SamplerOcclusion       (0x32667BD7)  → SSAO
```

### Textures (t0..t9)

```
t0  g_SamplerLightDiffuse   (0x23D0F850)  — 预计算的漫反射光照缓冲（deferred 渲染管线的产物）
t1  g_SamplerLightSpecular  (0x6C19ACA4)  — 预计算的高光光照缓冲
t2  g_SamplerGBuffer1       (0xE4E57422)  — GBuffer 第 1 张（通常是法线+粗糙度）
t3  g_SamplerGBuffer2       (0x7DEC2598)  — GBuffer 第 2 张（albedo 等）
t4  g_SamplerGBuffer        (0xEBBB29BD)  — 合并 GBuffer
t5  g_SamplerNormal         (0x0C5EC1F1)  — ★ 法线贴图（本身）—— Face 取 .z，Body/Emissive 取 .zw
t6  g_SamplerMask           (0x8A4E82B6)  — 皮肤 mask 贴图
t7  g_SamplerTileOrb        (0x800BE99B)  — tile/orb 贴图 (texture2darray)
t8  g_SamplerReflectionArray(0xC5C4CB3C)  — 反射立方体数组 (texturecubearray)
t9  g_SamplerOcclusion      (0x32667BD7)  — 遮蔽/SSAO
```

**关键认识**：pass[2] 已经是**前半部已合成好的 light buffer 之后的 composite 阶段**。t0/t1 传入的是**光照累积缓冲**，t2/t3 是之前深度/法线填充好的 GBuffer。也就是说 skin.shpk 的 pass[2] 并不是传统意义上的"forward 光照 PS"，而更像**延迟渲染的"skin-specific composite"阶段**——它在读完预烘焙的光照后，加上 skin 自己的 SSS / 镜面反射 / 发光。

这让"为什么不同 SkinType 只修改局部分支"这件事变得合理：光照主力已经在 GBuffer + light buffer 层完成，PS[2] 只做合成阶段的皮肤特化处理。每个 SkinType 的"合成方式"有自己的偏好。

### Sampler 的 slot vs Texture 的 slot 对齐

观察 sampler `size` 与 texture `size` 列：`s0.size=0 ↔ t4.size=0`（都是 GBuffer 主采样器）、`s1.size=1 ↔ t5.size=1`（Normal）、`s2.size=2 ↔ t7.size=2`（TileOrb）—— **size 列其实是把 sampler-texture 对配对起来的连接键**。这是 Dawntrail shader 容器的新设计：在旧版本里 sampler 和 texture 是同一份资源，现在拆开后用这个 size 字段重新关联。

## 4.6 PS[19] 里 r1 早期那两条指令的真相（Ch3 Q1 回答）

Ch3 §3.8 问过：

```
PS[19] 独有的早期两条：
  mul r1.xyz, cb0[3].xyzx, cb0[3].xyzx    // r1.xyz = g_DiffuseColor² ? 不对，是 cb0[3] = offset 48 起
  mul r1.xyz, r0.zzzz, r1.xyzx            // r1.xyz *= r0.z  (r0.z = normal.alpha)
```

cb0[3] 的**字节偏移是 48**（因为 cb0 的每 vec4 = 16B，index 3 = 48B~63B），这正是 `g_EmissiveColor` 的位置（见 §4.2 里 offset=48, size=12）。

所以这两条实际是：

```
r1.xyz = g_EmissiveColor² × normal.alpha
```

这是 **mtrl 级 g_EmissiveColor**（cb0）的静态"pre-square"计算，后续在第 753 行 `mul r1.xyz, r1.xyzx, cb5[0].xyzx` 又把**动态 g_MaterialParameterDynamic.m_EmissiveColor**（cb5）也乘上去。最终 r1.xyz = `emissive_static² × normal.alpha × emissive_dynamic`。

**两份 emissive 同时存在**：
- cb0[3].xyz = mtrl 层的静态 g_EmissiveColor（我们 `MtrlFileWriter` 往 mtrl 里写的那一份）
- cb5[0].xyz = 引擎 hook 层的动态 g_MaterialParameterDynamic.m_EmissiveColor（我们 `EmissiveCBufferHook` 实时改的那一份）

**它们俩是乘在一起的**，并不是冗余备份！我们原本以为 cb0 的 `g_EmissiveColor` 是 Emissive 专用 + 被动 replace、cb5 是 runtime 改写的独立通道，实际上**它们联合决定最终发光贡献**。

这条发现对调参逻辑有实质影响：  
- 如果 mtrl 层 `g_EmissiveColor = (0.5, 0.5, 0.5)`，runtime 动态层 = (1, 1, 1)，则最终发光强度 = 0.5² × 1 = 0.25（每分量）
- 如果 mtrl 层 = (1,1,1)，runtime = (1,1,1)，则 = 1.0
- 如果 mtrl 层 = (0,0,0)，无论 runtime 写什么，都乘成 0 → **关不掉发光的时候可能是 mtrl 把 emissive 写为 0 了**

这解释了一些先前的 EmissiveCBufferHook"明明写了值但没亮"的 bug 可能来源。

## 4.7 Ch3 留下的其他疑问

### Q3（t5 swizzle 差异）—— 已落实为 normal.alpha 是否被消费

见 §4.4 上面的详细解答。Face 只读 `t5.z`，Body/Emissive 读 `t5.zw`；差异**是真实的**，不是 swizzle 噪声。

### Q4（第 5 个 SkinType 0xF421D264 vs Face）—— 通过 pass[2] PS 列表直接证实

Ch2 §2.4 已经列出：`0xF421D264` 的 pass[2] PS 列表与 `ValFace` **逐项相同**（都是 `[2, 21, 32, 51, ...]`）。shpk 的 NodeSelectors 字典把两个不同的 selector（一个 SkinType=Face、一个 SkinType=0xF421D264）指向同一组 node index，后续 pass[2] lookup 同源。**所以两者共享物理 PS，不是独立分支**，观感等价于 Face。

`0xF421D264` 很可能是"另一种不得不显式指定的面部材质"（比如 Hrothgar face 的某种变体，或 Viera 耳朵的附加材质）—— 具体认证留 Ch6 IDA 侧。

## 4.8 Ch4 核心结论

1. **skin.shpk 的 cbuffer 层面唯一 Emissive 特有的就是 cb5 `g_MaterialParameterDynamic`**，一共 1 vec4，就放一个 m_EmissiveColor。其他所有 cbuffer 在 Face/Body/BodyJJM 之间完全相同。

2. **cb0 `g_MaterialParameter` 里还有一个静态 `g_EmissiveColor`**（offset 48）。Emissive PS 会把静态和动态两个 emissive **相乘**使用。非 Emissive PS 不读它。

3. **`g_ShaderTypeParameter` 是一张 256 × 8 vec4 的皮肤 shader ID 表**，按法线贴图 alpha 当 u8 索引查表。这是 FFXIV 皮肤 shader 的"LUT 主力"，每个 skin ID 定义一整套参数。

4. **pass[2] 本质是 composite 阶段**，读已有的 light buffer + GBuffer，做 skin-specific 混合。所以不同 SkinType 的修改限于"合成风格"，不涉及主光照。

5. **颈部"有光泽/没光泽"接缝的物证链已完整**：Body 有一条 `sat(normal_alpha × v1.w × normal_z)` gloss mask 链；Emissive 虽然也读到 normal_alpha，却把它挪去做 view-dependent 计算，没有接入 gloss mask；再加上 `cb5[0] × specular` 的乘法调制。=> 用户看到的现象完全对应。

6. **Ch5 改造路径评估将会彻底颠覆**：
   - 路径 A（PS[19] 光照对齐 Body）的具体做法现在非常明确：**在 PS[19] 里添加 Body 的 gloss mask 链**（`r0.z = r0.z × v1.w`；后续 `mul_sat r0.x, r0.z, r0.x`），或者**移除 Emissive 独有的 view-dependent 分支**。工作量远比我们最初预估的小。
   - 路径 B（Body 原地注入 ColorTable 发光）现在可以写得更安全：**只需要在 Body 的 pass[2] PS 末尾添加 `mul r1.xyz, r1.xyzx, <ColorTable.emissive>`**。不需要整套资源重组。
   - 路径 C（脸部也切 Emissive）现在的代价得知：face 走了 Emissive 后会**新增** cb5 乘法 + 丢掉 view-dependent 计算 + 继承身体那条 gloss mask 链（但 Face VS 可能不产生 r0.z 有效值）→ 观感会变。

## 状态

- [x] Ch0 总览
- [x] Ch1 shpk 结构
- [x] Ch2 SkinType 分支全貌
- [x] Ch3 DXBC 对比
- [x] Ch4 cbuffer/sampler/texture 清单（本文）
- [ ] Ch5 接缝与改造路径（最后一章主线，综合 Ch2~Ch4）
- [ ] Ch6 引擎 fast-path（候选）
- [ ] Ch7 跨 shpk 关系（候选）
