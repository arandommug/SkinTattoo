# Ch6 -- PS[19] 逐段解剖（modding 参考手册）

> 目标：把 `skin.shpk` Emissive 变体 pass[2] 的 **485 条指令** 按功能拆成可读的若干块，对每一块给出物理含义、消费者、可改造点。
> 用法：任何人看着 `ShaderPatcher/extracted_ps/ps_019_disasm.txt` 某一行，都能在这里找到"这条指令属于哪个阶段、为什么存在、能不能改、改了会怎样"。
> 参照 shader 宿主：`FFXIV/shader/sm5/shpk/skin.shpk`，PS index = 19。
> 字段别名参见 Ch4 cbuffer 清单；Ch3 是 PS[2]/[8]/[12] 的 diff 视角对比，本章聚焦 PS[19] 本身。

## 6.1 宏观流水线（Block 总览）

PS[19] 从 `dcl_...` 到 `ret` 共 485 条指令，可归为 10 个功能 Block：

| Block | 行号 | 功能 | 产出 |
|---|---:|---|---|
| **A** | 295-310 | **输入采样 + emissive 静态初始化 + tile 缩放** | r1.xyz(emissive), r0.x(主表面强度初值), r2.xy(tile UV) |
| **B** | 311-325 | **屏幕 UV + 视向 + Light buffer & GBuffer 采样** | r2.xy(screen UV), r3(view vec), r4(diffuse light), r5(specular light), r6(GBuffer albedo), r0.yzw(GBuffer extra) |
| **C** | 326-342 | **Skin ID 查表 + F0 重建** | r1.w(skin ID * 8), r6.xyz(skin params via LUT), r8.xyz(F0/baseColor), r9.xyz(albedo^2) |
| **D** | 343-348 | **光向量 + NdotL** | r10(light dir), r11(normalized light), r2.w(NdotV or similar) |
| **E** | 349-592 | **skin-ID 分支（SSS/Subsurface vs GGX BRDF）** | r11.xyz(specular+SSS lobe 输出) |
| **F** | 594-633 | **次要高光 / rim / 混合** | r3.xyz, r5.xyz, r0.xy 更新后的值 |
| **G** | 634-703 | **环境光 (SH) + CubeMap 反射** | r9.xyz(反射) |
| **H** | 704-732 | **skin 色调调制 + 衣着透明** | r3.xyz(最终 diffuse-ish) |
| **I** | 733-761 | **汇总 + emissive 注入主累加器** | r0.yzw(最终线性颜色) |
| **J** | 762-775 | **alpha 输出 + instance tint + gamma + 曝光** | o0.xyzw |

下面逐块详述。

---

## 6.2 Block A（L295-310）---- 输入采样 + emissive 静态初始化 + tile UV

```hlsl
295  sample_b_indexable r0.xz, v2.xyxx, t5.zxwy, s1, r0.x
           // r0.x = normal.z, r0.z = normal.alpha  (从 t5 = g_SamplerNormal 读)
296  mul r1.xyz, cb0[3].xyzx, cb0[3].xyzx
           // r1.xyz = g_EmissiveColor^2  (sRGB->linear 的 gamma-2 近似)
297  mul r1.xyz, r0.zzzz, r1.xyzx
           // r1.xyz *= normal.alpha  (emissive mask)
298  mul r0.x, r0.x, cb0[9].x
           // r0.x *= g_TileAlpha(cb0[9]=offset 144)  (压暗主表面)
299  mul_sat r0.x, r0.x, v1.w
           // r0.x = sat(r0.x * vertex.alpha)  (主表面强度初值)
300  round_z r2.z, cb0[6].w
           // r2.z = trunc(g_TileIndex)  (cb0[6]=offset 96, .w=108=g_TileIndex)
301  mul r2.xy, v2.zwzz, cb0[7].xyxx
           // r2.xy = v2.zw * g_TileScale  (tile UV)
302-304  min/mul/log r0.z 与 cb0[7].xy
           // 计算 tile 采样的 mip bias
305  max r0.z, r0.z, l(0)
306  add r0.z, r0.z, cb0[17].y
           // + g_TextureMipBias  (cb0[17]=offset 272)
307  sample_b_indexable r0.z, r2.xyzx, t7.xywz, s2, r0.z
           // 从 t7 = g_SamplerTileOrb (texture2darray) 采 tile mask
           // r2.z 作为数组层索引（tile index）
308  add r0.z, r0.z, l(-1.0)
309  mad r0.x, r0.x, r0.z, l(1.0)
           // r0.x = r0.x * (tile-1) + 1  (tile 调制曲线)
310  mul r0.x, r0.x, r0.y
           // 注意 r0.y 此时还没显式写入，但 r0.yzw 在 L325 会被覆盖 -> [!] 这一行读的是未初始化值？
           // 实际原因：v0.xy 是 SV_Position 进来的，r0.y 可能已经隐式持有
           // 或者这是编译器优化后的 dead write（后续 L325 覆盖 r0.yzw 全部）-> 可能不影响
```

**可改造点**：
- 改 296 行 `cb0[3]^2` 为 `cb0[3]`（不平方） -> emissive 从"sRGB^2 曲线"变为线性 -> 改变发光的非线性响应
- 删掉 297 行 -> emissive 不再被 normal.alpha 掩蔽 -> 整件材质均匀发光
- 改 301 行 `v2.zwzz` 为 `v2.xyxx` -> tile 采样对齐 Body 的 UV（与 Body/BodyJJM 统一）

**r0.z 暂时是 normal.alpha，但在 L325 会被 GBuffer1 的采样结果覆盖**，所以 296-299 行消费它必须在 325 行之前。

---

## 6.3 Block B（L311-325）---- 屏幕 UV + 视向 + Light/GBuffer 采样

```hlsl
311  mad r2.xy, v0.xyxx, cb1[0].xyxx, cb1[0].zwzz
           // r2.xy = SV_Position.xy * g_CommonParameter[0].xy + [0].zw
           // cb1[0] = m_RenderTarget (rt 尺寸/逆尺寸)  -> r2.xy = 屏幕归一化 UV
312  dp3 r1.w, -v4.xyzx, -v4.xyzx
313  rsq r1.w, r1.w
314  mul r3.xyz, r1.wwww, -v4.xyzx
           // r3.xyz = normalize(-v4.xyz) = 视向（从像素到相机）
           // v4 是 VS 传进来的 world-space view vector

315  sample_indexable r4.xyz, r2.xyxx, t0.xyzw, s0
           // r4.xyz = g_SamplerLightDiffuse.sample(screenUV)   <- 延迟渲染的漫反射累积
316  sample_indexable r5.xyz, r2.xyxx, t1.xyzw, s0
           // r5.xyz = g_SamplerLightSpecular.sample(screenUV)  <- 延迟渲染的高光累积
317  sample_indexable r6.xyzw, r2.xyxx, t4.xyzw, s0
           // r6.xyzw = g_SamplerGBuffer.sample(screenUV)        <- GBuffer 主通道
318  add r6.xyz, r6.xyzx, l(-0.5, -0.5, -0.5, 0.0)
           // r6.xyz -= 0.5  (GBuffer 法线是 [0,1] 存，解压成 [-0.5, 0.5])

319-321 dp3 r7.xyz, cb3[0..2].xyz, r6.xyz
           // r7 = inverseView * r6  (法线从 tangent space 变到 view/world)
           // cb3 = g_CameraParameter, [0..2] = 一个 3*3 matrix

322-324 dp3/rsq/mul 归一化 r7 -> r7.xyz = world-space normal

325  sample_indexable r0.yzw, r2.xyxx, t2.xyzw, s0
           // r0.yzw = g_SamplerGBuffer1.sample(screenUV)
           // t2 是 GBuffer 第 1 张：推测携带 (SSAO, roughness, skin ID/alpha mask, specular occlusion)
           // * 注意 r0.yzw 之前的值在这里被彻底覆盖
```

**可改造点**：
- 317 行 `t4` 是 `g_SamplerGBuffer`（合并版），315-317 都是延迟渲染的产物。我们无法在 PS[19] 里改"光照怎么来"，但可以改"如何消费它们"。

---

## 6.4 Block C（L326-342）---- Skin ID LUT 查表 + F0 重建

```hlsl
326  mad r1.w, r6.w, l(255.0), l(0.5)
           // r6.w = GBuffer alpha channel (0..1)
           // r1.w = r6.w * 255 + 0.5  (浮点到整数的 + 0.5 round)
327  ftou r1.w, r1.w                              // f32 -> u32 (0..255)
328  ishl r1.w, r1.w, l(3)                        // * 8  (每 skin entry 8 vec4)

329  lt r2.zw, l(0,0,0,0), cb7[r1.w + 0].zzzw    // entry[0].zw > 0 ?
330  and r6.z, r2.z, l(0x3f800000)               // bool -> 1.0 bit pattern (0x3f800000 = 1.0f)
331  lt r3.w, l(0), cb7[r1.w + 3].x              // entry[3].x > 0 ?
332  or r2.w, r2.w, r3.w                         // combine flags
333  lt r3.w, l(0), cb7[r1.w + 1].w              // entry[1].w > 0 ?
334  mov r6.x, l(0)
335  mov r6.y, r0.z                              // r0.z 此时已被 GBuffer1 覆盖
336  movc r8.xz, r3.wwww, r6.xxyx, r6.yyzy       // 条件选择：entry[1].w 决定取 0 还是 r0.z
337  mov r8.y, l(0)
338  movc r6.xyz, r2.wwww, r6.xyzx, r8.xyzx      // 最终 r6.xyz = 由 flags 选择的 skin 参数

339  sample_indexable r8.xyz, r2.xyxx, t3.xyzw, s0
           // t3 = g_SamplerGBuffer2  <- GBuffer 第 2 张：albedo/diffuseColor
340  mul r9.xyz, r8.xyzx, r8.xyzx
           // r9.xyz = albedo^2  (后续用来算 F0？)
341  mad r8.xyz, r8.xyzx, r8.xyzx, l(-0.04, -0.04, -0.04, 0)
           // r8.xyz = albedo^2 - 0.04  (metallic F0 基线减去绝缘体默认 0.04)
342  mad r8.xyz, r6.xxxx, r8.xyzx, l(0.04, 0.04, 0.04, 0)
           // r8.xyz = r6.x * (albedo^2 - 0.04) + 0.04
           //        = lerp(0.04, albedo^2, r6.x)  <- r6.x 就是 metallic 因子
           // * 这正是 Disney/UE4 里的 "F0 = lerp(0.04, albedo, metallic)" 经典公式
```

**关键发现**：PS[19] 用的是**标准 UE4 式 metallic-roughness BRDF**。`r8.xyz` 此后就是 F0（低角度菲涅尔反射）。

**skin ID LUT 的前 8 vec4 字段推断**（基于索引位置）：

| offset 内索引 | 推测字段 | 证据 |
|---|---|---|
| entry[0].x | skin-type code | L349 `ieq cb7[r1.w+0].xxxx, l(1,3,0,0)` ---- 判断 == 1 或 == 3 |
| entry[0].z | metallic flag? | L329 `lt 0, cb7[+0].z` |
| entry[0].w | 某个权重 | L361 `r5.w = r4.w * cb7[+0].w` |
| entry[1].w | flag for r0.z selection | L333 |
| entry[2].y | sincos 输入（各向异性方向？） | L397 `sincos r14.x, r15.x, cb7[+2].y` |
| entry[2].zw | 双向光照阈值（SSS thickness？） | L428 `add r6.yw, r6.yyyy, -cb7[+2].zzzw` |
| entry[3].x | SSS 相关 flag | L331 |
| entry[3].yz | anisotropic roughness scale | L395 `mad r13.xy, cb7[+3].yzyy, ...` |

**后 3 行就是"F0 = lerp(0.04, albedo^2, metallic)"**。后续 r8.xyz 会被用作 specular 色。

---

## 6.5 Block D（L343-348）---- 光方向与 NdotV

```hlsl
343  add r10.xyz, -v4.xyzx, l(0, 0.2, 0, 0)
           // r10.xyz = -v4.xyz + (0, 0.2, 0)  view vector 加个偏移
           // 0.2 的 Y 偏移看起来是"补正光源方向"（如头顶偏上的主光）
344-346  dp3/rsq/mul r11.xyz = normalize(r10.xyz)
           // r11 = 归一化的偏移后视向  = 伪"光方向"

347  dp3 r2.w, r7.xyzx, r11.xyzx
           // r2.w = dot(worldNormal, lightDir-ish)
348  mov_sat r3.w, r2.w   // r3.w = sat(NdotL)
```

**r10/r11 被当作"光方向"**，但它是从 view vector 偏移得到的，这在**延迟渲染**里是合理的 ---- 因为真实灯光贡献已经烘焙进 `g_SamplerLightDiffuse`（r4）和 `g_SamplerLightSpecular`（r5），PS[19] 不重新做"每灯循环"。这里的"伪光方向"更多是给 SSS / 反射 lobe 做 NdotL 估算。

---

## 6.6 Block E（L349-592）---- skin-type 分支：SSS 或 GGX

这是 PS[19] 最长的一块，内含三层嵌套 if。整体结构：

```
if (skinID_type == 1 || skinID_type == 3) {         // Block E1 (L350-483)
    // 走 SSS + 各向异性 GGX 路径
} else {
    if (entry[0].z > 0) {                           // Block E2 (L485-548)
        // 走标准各向异性 GGX（带 F0 菲涅尔近似）
    } else {                                        // Block E3 (L550-591)
        // 走简化 GGX（无各向异性）
    }
}
```

### Block E1（L350-483）---- SSS + 各向异性（skin ID 1 或 3 时启用）

高度专业化的 SSS 计算，涉及：

- L351-359：切空间重建（dp3 v3 * cb3[3..5]）---- 从顶点 tangent 算出世界切线
- L365-379：半球到 hemispherical-cos 的映射（-0.156583 与 1.570796 = pi/2，典型 arccos 逼近）
- L388：`l(0.5, -3.65, 17.0)` 常数组 ---- 看起来是 SSS 深度散射的多项式曲线
- L391-396：Beckmann-like 高斯的准备（散射带宽）
- L397-402：`sincos cb7[+2].y` ---- **各向异性主方向旋转**
- L405：`l(1.414214, 3.544908)` = (sqrt2, sqrt(4pi)) ---- 高斯归一化常数
- L412 * `l(1.442695)` = 1/ln(2) ---- **用 `exp`+这个系数做底数为 2 的指数**（常见于 Beckmann 分布）
- L422: `l(0.953479, 0.046521)` = Schlick Fresnel 的 `(1 - 0.04, 0.04)` 映射

**物理含义**：这一大段是**各向异性 Kelemen-Szirmay-Kalos 皮肤 SSS** 近似，专门针对脸部/手部这种高细节皮肤。skin ID == 1 可能是"标准皮肤"，skin ID == 3 可能是"脸部（毛孔）"。

### Block E2（L485-548）---- 标准 GGX（带 F0 菲涅尔）

- L491-498：NdotH 计算（半程向量法线点乘）
- L499-504：roughness^2 处理
- L506-516：**GGX 分布函数**（`D = alpha^2 / (pi * (NdotH^2(alpha^2 - 1) + 1)^2)`）
- L517-527：**Smith Geometry 项**
- L528-538：F0 菲涅尔近似

这是游戏里"普通皮肤"的 BRDF。对照 Unreal 引擎的经典 UE4 皮肤 shader，能一一对应。

### Block E3（L550-591）---- 简化 GGX（无各向异性）

Block E2 的精简版本，少一条各向异性旋转。用于"不重要的"皮肤（tail、远处 NPC？）。

### 共同输出

三个分支最终都写到 **`r11.xyz`**（或 r10.xyz 被 movc 选中到 r11.xyz）---- 这是**直射高光累积**。

---

## 6.7 Block F（L594-633）---- 次要高光 / rim / SSS 混合

```hlsl
594-596  r10.xyz = (r3.w * pi)(if entry[0].y==0 else 0) * r11.xyz
         // r10.xyz = r11.xyz * pi * NdotL  or zero
597-606  complex：mad r2.xzw with cb4[2].zwzz
         // cb4[2] = g_InstanceParameter[2] = ?  (可能是 wetness 参数)
607      mad r4.xyz, r10.xyz, r2.yyyy, r5.xyzx
         // r4.xyz = r10 * r2.y + r5  (直射高光 + 预烘焙高光)

608-631  rim light 计算
         // 基于 r0.w（一个 1-NdotV 类量）做边缘光
632-633  movc r5.xyz, r0.zzzz, r5.xyzx, r9.xyzx
         // 根据某 flag 在两个颜色之间切换
```

---

## 6.8 Block G（L634-703）---- 环境光 + CubeMap 反射

```hlsl
634-640  环境光强度系数 r0.z
         // 基于 cb6[4] = g_AmbientParam[4]  (方向、阈值相关)

641-645  dp4_sat with cb6[0..3]
         // r9.xyz = 法线方向的球谐 (SH2) 环境色 * cb6[3].w (强度)
646      mul r9.xyz, r0.zzzz, r6.xywx    // 缩放

647-658  亮度归一化、反射视向计算

659-667  dp4_sat with cb6[0..3] 和 cb6[6..8]
         // 两组 SH 系数（低/高频？primary/secondary？）
         // cb6[6..8] = 推测是第二组 SH  （天空光 vs 环境光？）
668-674  两组 SH 权重 blend

675-681  反射向量 -> world space（cb3[3..5] = inverseView）

682      sample_l_indexable(texturecubearray) r12.xyzw, r9.xyzw, t8.xyzw, s3, r0.z
         // * 采样 CubeMap 反射：t8 = g_SamplerReflectionArray
         // LOD 由 r0.z（roughness derived）控制

683-694  单独一个 if 分支做二级 cubemap 采样与 blend
         // cb6[9].z > 0 时混合（可能是"室内外"或"多反射源"开关）

696-703  最终反射色 r9.xyz，含 2.356194 = 3pi/4（能量守恒归一化常数）
```

**r9.xyz 最终是环境反射贡献**，后续和直射反射 r3.xyz 混合。

---

## 6.9 Block H（L704-732）---- skin 色调调制

```hlsl
704-712  r3.xyz 最终合成（diffuse + spec + rim 某种 blend）
713-717  `l(1/3, 1/3, 1/3)` 平均 -> luminance；对 r3.xyz 做 tone curve
718-724  cb0[5] = g_EmissiveColor[z,w] = offset 80 和 84 之间某常量；
         // `cb0[5].w` * 0.85，配合 cb2[0].z（g_PbrParameterCommon.m_SubSurfaceSSAOMaskMaxRate）
         // 这一段做"皮肤次表面削弱"的曲线
725-726  r0.y = sat(r0.y * (r1.w^2 - 1) + 1)
727-732  r7.xyz = tone mapped 版本
733      ieq r0.w, cb4[9].z, l(3)
         // cb4[9] = g_InstanceParameter[9] = ?  检查某模式是否 == 3
734      movc r0.w, r0.w, l(0.85), l(0.75)
735      mul r7.xyz *= r0.w
         // 模式 3 -> 0.85，其它 -> 0.75  (可能是"布料下的皮肤"衰减系数)
```

这一段是 **skin 色调衰减**，专门处理半透明衣物下的次表面衰减、肤色调整等。

---

## 6.10 Block I（L733-761）---- 汇总 + emissive 注入

```hlsl
736-752  各种 blend/mad，把 r3（直射）、r5（间接）、r7（tone）揉进 r0.yzw

753  mul r1.xyz, r1.xyzx, cb5[0].xyzx
           // * r1 * runtime 动态 emissive  (cb5 = g_MaterialParameterDynamic.m_EmissiveColor)
754  dp3 r2.w, r2.xyzx, l(0.29891, 0.58661, 0.11448, 0)
           // r2.w = luminance(r2.xyz)  (Rec.709 系数)
755  max r2.w, r2.w, l(1.0)
           // r2.w = max(luma, 1)  (防止在暗处 emissive 被压小)

756  mul r3.xyz, r1.xyzx, r2.wwww
           // r3.xyz = emissive * max(luma, 1)

757  mad r1.w, r1.w, l(0.8), l(0.7)      // r1.w 缩到 [0.7, 1.5]
758  mul r0.x, r0.x, r1.w                 // 主表面强度微调
759  mul r0.yzw, r0.xxxx, r0.yyzw        // r0.yzw *= r0.x
760  mad r0.yzw, r2.xxyz, r5.xxyz, r0.yyzw
           // r0.yzw += r2.xyz * r5.xyz  (某间接光贡献)

761  mad r0.yzw, r1.xxyz, r2.wwww, r0.yyzw
           // *** r0.yzw += r1.xyz * r2.w  <- emissive 正式汇入最终颜色累加器
```

**r1.xyz 的 emissive 通路就到 761 行闭环**。后续 r1 被重用为别的 temp。

---

## 6.11 Block J（L762-775）---- alpha、tint、gamma、输出

```hlsl
762  mul r1.xyz, r0.xxxx, r4.xyzx
           // r1 重新被用作 r0.x * lightDiffuse (r4)
763  mul r2.xyz, r3.xyzx, cb4[1].wwww
           // r2 = r3 * g_InstanceParameter[1].w  (某 rim 系数？)
764  mad r1.xyz, cb1[2].xxxx, r1.zxyz, r2.zxyz
           // cb1[2] = g_CommonParameter[2].x  (推测曝光或 UI 调整)
765-770  r1/r2 swizzle + max chain  -> r1.xy
771  div o0.w, r1.x, r1.y
           // * alpha 输出：o0.w = 某比值  (屏幕空间的 alpha，用于溶解等效果？)

772  mul r0.xyz, r0.yzwy, cb4[0].xyzx
           // * r0.xyz = (r0.y, r0.z, r0.w) * m_MulColor.xyz
           //   cb4[0] = g_InstanceParameter[0] = m_MulColor（实例颜色乘）

773  max r0.xyz, r0.xyzx, l(0, 0, 0, 0)
           // clamp 到正数
774  sqrt r0.xyz, r0.xyzx
           // * gamma 2.0 近似（线性 -> sRGB）
775  mul o0.xyz, r0.xyzx, cb1[3].xxxx
           // * * g_CommonParameter[3].x（全局曝光）
           // cb1 = g_CommonParameter
776  ret
```

---

## 6.12 资源消费汇总表（哪一块在用哪些资源）

| Block | cb0 | cb1 | cb2 | cb3 | cb4 | cb5 | cb6 | cb7 | t0 | t1 | t2 | t3 | t4 | t5 | t6 | t7 | t8 | t9 |
|---|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| A emissive init | [3][9] | | | | | | | | | | | | | (ok) | | | | |
| A tile UV | [6][7][17] | | | | | | | | | | | | | | | (ok) | | |
| B screen+vec | | [0] | | | | | | | | | | | | | | | | |
| B lighting sample | | | | | | | | | (ok) | (ok) | (ok) | | (ok) | | | | | |
| B normal transform | | | | [0-2] | | | | | | | | | | | | | | |
| C skinID LUT | | | | | | | | (ok) | | | | (ok) | | | | | | |
| D light dir | | | | | | | | | | | | | | | | | | |
| E SSS | | | | [3-5] | | | | (ok) | | | | | | | | | | |
| G cubemap | | | | [3-5] | | | (ok) | | | | | | | | | | (ok) | |
| I emissive mix | | | | | [2] | (ok) | | | | | | | | | | | | (ok) |
| J tint/output | | [2][3] | | | [0][1] | | | | | | | | | | | | | |

cb6/cb7 的索引差异体现 Ch4 Sec.4.1 的 cbuffer slot shift（Emissive 在 slot 5 插入 g_MaterialParameterDynamic，把 g_AmbientParam 推到 cb6、g_ShaderTypeParameter 推到 cb7）。

## 6.13 可改造热点总结

按"改动风险 * 可见效果"排序：

| 改造点 | 位置 | 风险 | 可见效果 |
|---|---|---|---|
| 补 Body gloss mask 链 | L297-299 之间 | 低 | 身体切 Emissive 后保留原光泽 -> **消接缝** |
| 关闭 emissive mask（normal.alpha） | 删 L297 | 低 | emissive 全面覆盖，不受法线 alpha 限制 |
| 替换 emissive sRGB 曲线 | L296 | 低 | 线性 emissive 响应（更亮） |
| 改 emissive 放大机制 | L755 | 中 | 发光在暗处不被 `max(luma,1)` 压制 |
| 引入 ColorTable 采样 | 在 753 附近注入 | 中 | per-layer 独立发光色（我们现有方案的核心） |
| 改变 skin ID LUT | 改 cb7 数据（mtrl 侧） | 高 | 重新定义"皮肤类型"参数含义 |
| 删除 SSS E1 分支 | 把 349 行 `ieq` 的结果强制为 0 | 高 | 所有 skin 走 GGX（无 SSS）-> 皮肤观感变成塑料 |
| 替换 cubemap 采样器 | L682 换 t8 | 高 | 角色反射改成自定义环境 |
| **切换整块光照模型** | Block E 全替换 | 极高 | 彻底自定义皮肤 shader |

---

## 6.14 给后续 Ch7 的"可以做什么"种子

基于本章理解，以下是**技术上已经可行**但尚未启动的方向（Ch7 会细化评估）：

1. **per-layer roughness ramp**：ColorTable 里写 roughness 值，PS 中在 Block E2/E3 的 GGX `alpha^2` 处理那一行读取 ColorTable，让同一皮肤不同区域有不同粗糙度。
2. **normal.alpha 双通道复用**：现在 normal.alpha 在 Body 当 gloss mask、在 Emissive 当 emissive mask。能否通过 shader key 或 LUT 扩展让一张贴图同时支撑两种用途（高位 bit = emissive mask，低位 bit = gloss mask）。
3. **动态 wetness**：向 cb5（或扩容 cb5）里注入 `m_Wetness` 浮点，在 Block F/G 前插入 roughness /= (1 + wetness) 和 F0 *= (1 + wetness * k)。
4. **自定义 rim light 颜色**：Block F 的 rim 计算用一个新的 material param（比如把 `0x285F72D2` 之类未命名常量认领）当 rim color。
5. **视角相关发光（anisotropic emissive）**：把 `m_EmissiveColor` 从 float3 扩为 float4，第 4 分量作为 view-dependent gate（NdotV ramp）。
6. **SSS lobe 替换**：Block E1 的 Kelemen-Szirmay-Kalos 近似替换成更新的 Separable Subsurface / Screen-Space SSS。

这些每一条都对应 PS[19] 里具体的行号/寄存器，不再是"概念性"想法。

---

## 6.15 复现与追查

- PS 反汇编：`ShaderPatcher/extract_ps.py <shpk> <psIdx>` -> `extracted_ps/ps_XXX_disasm.txt`
- Block 边界复现：搜 `if_nz` / `else` / `endif` 找分支骨架；搜 `sample_*` 找资源消费位置
- 指令计数：反汇编最后一行 `// Approximately N instruction slots used`

## 6.16 后续修订

本章是 **基础版 v1**，覆盖了 PS[19] 全部 485 条指令的功能归属。以下方面留作 v2：

- Block E 的三个分支逐指令细解（每行物理含义，目前只到系数级别）
- Block G 里两组 SH 系数（cb6[0..3] 和 cb6[6..8]）的区分验证
- 32 个 Emissive 兄弟 PS（PS[19] 的 SceneKey 变体）里有哪些指令因 SceneKey 而浮动
- Face PS[2] / Body PS[8] / BodyJJM PS[12] 的 Block E 差异（现已知 Face 只走 E3，Body 可能三个都有）

## 状态

- [x] Ch0 总览
- [x] Ch1 shpk 结构
- [x] Ch2 SkinType 分支全貌
- [x] Ch3 DXBC 对比
- [x] Ch4 cbuffer/sampler/texture 清单
- [x] Ch5 接缝与改造路径
- [x] Ch6 PS[19] 逐段解剖（本文 v1）
- [ ] Ch7 高级效果 idea 清单
- [ ] Ch4 附录：认领未知 CRC
