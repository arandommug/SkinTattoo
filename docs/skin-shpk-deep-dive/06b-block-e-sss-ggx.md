# Ch6 v2 -- Block E 逐指令数学化解读（SSS / GGX BRDF）

> Ch6 v1（`06-ps19-anatomy.md`）把 PS[19] 粗切为 10 个 Block 并给每块打了标签。Block E（行 349-592，共 244 条指令）是整份 PS 最硬核的部分：skin-ID 分支下的两种皮肤 BRDF 实现。本章把它**逐段展开到数学公式**，并标注哪些是"教科书 PBR"模板、哪些是 SE 工程师自己的近似公式、哪些是 skin-specific 修正。
> 阅读前置：建议先过完 Ch6 v1 把握 r0..r11 各寄存器的含义；Block E 在进入前，PS[19] 的寄存器状态已准备完毕（r7=世界法线、r3=视向、r11=伪光向量、r8.xyz=F0、r9.xyz=albedo^2、r6.xyz=skin 参数、r0.y=粗糙度之类）。
> 确定性级别：**A** = 反汇编直接映射 / **B** = 强推断（常数值指纹匹配教科书公式）/ **C** = 工程直觉，待后续验证。

## 6b.1 Block E 的骨架

```
L349  ieq r12.xy, cb7[r1.w + 0].xxxx, l(1, 3, 0, 0)
         // r12.x = (skinID_LUT[0].x == 1)
         // r12.y = (skinID_LUT[0].x == 3)
L350  if_nz r12.x                        -+
         BLOCK E1 (Kelemen SSS + 各向异性 Beckmann)
         r11.xyz = 面部/脆皮 SSS 累积量
L484  else                                |
L485    if_nz r2.z                        +- r2.z 是法线 Z 正负判断
           BLOCK E2 (标准各向异性 GGX)
L549    else                              |
           BLOCK E3 (简化 GGX)
L592  endif                               -+
```

skinID_LUT[0].x == 1 或 3 -> 走 E1；否则看 r2.z 符号走 E2 或 E3。

Block E1 是 SE 自制的**Kelemen-Szirmay-Kalos 皮肤 SSS** 的 DT 版本，E2/E3 是**标准 UE4 式 GGX**。

---

## 6b.2 Block E1 详解（L350-483）---- 脸/精细皮肤 SSS

### Phase 1 (L351-364)：切线空间重建 + 方向点积

```
351  dp3 r4.w, v3.xyzx, v3.xyzx          ; |v3|^2      v3 = VS 传来的 tangent
352  rsq r4.w, r4.w                       ; 1/|v3|
353  mul r12.xzw, r4.wwww, v3.xxyz        ; r12.xzw = normalize(v3)  = 切线 T
354  dp3 r13.x, cb3[3].xyzx, r12.xzwx     ; T' = inverseView_row0 . T_local
355  dp3 r13.y, cb3[4].xyzx, r12.xzwx     ; 其中 cb3[3..5] = g_CameraParameter 的 inverseView 3*3
356  dp3 r13.z, cb3[5].xyzx, r12.xzwx     ; 这是把 T 从 local 变到 world/view
357  dp3 r4.w, r13.xyzx, r13.xyzx
358  rsq r4.w, r4.w
359  mul r12.xzw, r4.wwww, r13.xxyz       ; r12.xzw = normalize(T_world)
```
[A] **切线向量 T（世界空间）**在 `r12.xzw` 里待命。

```
360  add r4.w, -r6.y, l(1.000000)         ; r4.w = 1 - r6.y    (r6.y 是 g_SamplerMask 相关)
361  mul r5.w, r4.w, cb7[r1.w + 0].w      ; r5.w = r4.w * skinLUT[0].w   (SSS 强度？)
362  dp3_sat r6.y, r12.xzwx, r3.xyzx      ; r6.y = sat(T . V)  = "切线看向相机"
363  dp3 r6.w, r3.xyzx, r11.xyzx          ; r6.w = V . L       (视.光)
364  dp3 r8.w, r12.xzwx, r11.xyzx         ; r8.w = T . L       (切.光)
```
[A] **关键点积三件套**：`T.V`, `V.L`, `T.L`。Kelemen SSS 的特征就是用这些分量而非标准 NdotH/NdotL。

### Phase 2 (L365-379)：半角估算 + sin(halfAngle/2)

```
365  mad r9.w,  r6.y, l(-0.156583), l(1.570796)     ; r9.w  = pi/2 - 0.156583 * r6.y
366  add r10.w, -r6.y, l(1.000000)                  ; r10.w = 1 - r6.y
367  sqrt r10.w, r10.w                              ; r10.w = sqrt(1 - r6.y)
368  mad r9.w, -r9.w, r10.w, l(1.570796)            ; r9.w  = pi/2 - r9.w * r10.w
                                                    ;       = arccos(r6.y) 的一种低阶近似
```
[B] `1.570796 = pi/2`，`-0.156583 ~= -pi/2 * 0.0997` ---- 这是 **Shanks / Abramowitz arccos 近似** 的一种变体：

```
arccos(x) ~= pi/2 - x * (1 + 0.156583 * sqrt(1-x))
```

更确切地：SE 把它写成 `arccos(x) ~= pi/2 - (pi/2 - 0.156583 * x) * sqrt(1-x)`，展开后等价。
所以 `r9.w ~= arccos(T.V)`。[A]

```
369  mad r10.w, |r8.w|, l(-0.156583), l(1.570796)   ; r10.w 同上但对 |T.L|
370  add r11.w, -|r8.w|, l(1.000000)
371  sqrt r11.w, r11.w
372  mul r13.x, r10.w, r11.w
373  ge r13.y, r8.w, l(0.000000)                    ; r13.y = (T.L >= 0)
374  mad r10.w, -r10.w, r11.w, l(3.141593)          ; pi - ...
375  movc r10.w, r13.y, r13.x, r10.w                ; 分支：T.L 正负选不同公式
376  add r10.w, -r10.w, l(1.570796)                 ; pi/2 - r10.w
```
[B] `3.141593 = pi`。这里处理了 `T.L` 正负号的对称性，等价于计算 `arcsin(T.L)` 而不是 arccos。

```
377  add r9.w, r9.w, -r10.w                         ; r9.w = arccos(T.V) - arcsin(T.L)
378  mul r9.w, |r9.w|, l(0.500000)                  ; r9.w = |...| / 2
379  sincos null, r9.w, r9.w                        ; r9.w = cos(r9.w)
                                                    ; sincos 两输出：sin->第一位(null), cos->第二位
```
[B] `r9.w = cos((arccos(T.V) - arcsin(T.L)) / 2)` ---- 这是 **Kelemen BRDF 里的半角因子**，典型形式。

### Phase 3 (L380-390)：切向投影 + SSS 多项式

```
380  mad r11.xyz, -r8.wwww, r12.xzwx, r11.xyzx      ; r11 = L - (T.L) * T   (L 在切平面内的分量)
381  mad r12.xzw, -r6.yyyy, r12.xxzw, r3.xxyz       ; r12 = V - (T.V) * T   (同理 V)
382  dp3 r10.w, r11.xyzx, r12.xzwx                  ; r10.w = (L perp ) . (V perp )
383  dp3 r11.x, r11.xyzx, r11.xyzx
384  dp3 r11.y, r12.xzwx, r12.xzwx
385  mad r11.x, r11.x, r11.y, l(0.000100)           ; |L perp |^2 * |V perp |^2 + epsilon
386  rsq r11.x, r11.x                               ; 1 / sqrt(...)
387  mul r10.w, r10.w, r11.x                        ; r10.w = cos(角度 between L perp  和 V perp )
388  mad r11.xyz, r10.wwww, l(0.500000, -3.650000, 17.000000, 0.000000),
                          l(0.500000, -3.980000, -16.780001, 0.000000)
```
[C] 这条 `mad r11.xyz = r10.w * a + b` 是**三组并行的多项式**：
  - r11.x = r10.w * 0.500 + 0.500
  - r11.y = r10.w * -3.650 + -3.980
  - r11.z = r10.w * 17.000 + -16.780

这些系数**不对应任何著名 BRDF 公式**。推测是 SE 自己拟合的"用户可配置 SSS 曲线"----r11.x 走 diffuse wrap，r11.y/r11.z 走两种能量条带。后续 Phase 5 `r14.xy * r12.x` 会消费这些。

```
389  mov_sat r11.x, r11.x                           ; clamp [0,1]
390  sqrt r11.x, r11.x                              ; r11.x = sqrtsat(...)
```

### Phase 4 (L391-405)：各向异性 Beckmann 准备

```
391  div r12.xz, l(1.190000, 0.0, 0.800000, 0.0), r9.wwww  ; r12.x = 1.19/r9.w, r12.z = 0.80/r9.w
392  mad r11.w, r9.w, l(0.360000), r12.x                    ; r11.w = 0.36*r9.w + 1.19/r9.w
393  mul r12.x, r0.y, r0.y                                  ; r12.x = roughness^2
394  add r12.w, -r0.y, l(1.000000)                          ; r12.w = 1 - roughness
395  mad r13.xy, cb7[r1.w + 3].yzyy, r12.wwww, r0.yyyy
         ; r13.xy = skinLUT[3].yz * (1-roughness) + roughness
         ; 这是**各向异性粗糙度**：两个方向（y/z）各取一个 alpha_t, alpha_b
396  mul r13.xy, r13.xyxx, r13.xyxx                         ; r13.xy = alpha_t^2, alpha_b^2
397  sincos r14.x, r15.x, cb7[r1.w + 2].y
         ; r14.x = sin(LUT[2].y)      各向异性主轴角度
         ; r15.x = cos(LUT[2].y)
398  add r12.w, r14.x, r14.x                                ; r12.w = 2*sin  (常数整理)
399  mul r13.z, r11.x, r15.x                                ; r13.z = r11.x * cos
400  mad r13.w, -r6.y, r6.y, l(1.000000)                    ; r13.w = 1 - (T.V)^2
401  sqrt r13.w, r13.w                                      ; r13.w = sqrt(1-(T.V)^2) = sin(angle TV)
402  mul r14.x, r6.y, r14.x                                 ; r14.x = (T.V) * sin(axis)
403  mad r13.z, r13.z, r13.w, r14.x                         ; r13.z = r13.z * sin(TV) + r14.x
404  mul r12.x, r11.x, r12.x                                ; r12.x = r11.x * alpha^2
405  mul r14.xy, r12.xxxx, l(1.414214, 3.544908, 0.000000, 0.000000)
         ; r14.x = r12.x * sqrt2
         ; r14.y = r12.x * sqrt(4pi) ~= 3.54491
```
[B] `1.414214 = sqrt2`, `3.544908 = sqrt(4pi) = sqrt(12.566) ~= 3.545` ---- **Beckmann 分布归一化常数**。标准 Beckmann PDF 是
`D(m) = exp(-tan^2theta/alpha^2) / (pi alpha^2 cos^4theta)`，其归一化项 `1/(alpha pi)` 和 `1/(alpha^2 pi)` 对应这两个数。

[C] `cb7[r1.w + 3].yz` 是 **各向异性粗糙度 alpha_t, alpha_b**（tangent/bitangent 方向独立粗糙度）。`cb7[r1.w + 2].y` 是 **各向异性主轴旋转角**。这两条线索结合确认 Block E1 走的是**各向异性 BRDF**（普通皮肤走 E3，精细皮肤/脸部走 E1）。

### Phase 5 (L406-414)：Gaussian (Beckmann) 指数

```
406  add r6.y, r6.y, r8.w                                   ; r6.y = T.V + T.L
407  mad r8.w, -r12.w, r13.z, r6.y                          ; r8.w = r6.y - r12.w * r13.z
408  mul r8.w, r8.w, r8.w                                   ; r8.w^2
409  mul r8.w, r8.w, l(-0.500000)                           ; r8.w = -r8.w^2 / 2
410  mul r12.x, r14.x, r14.x                                ; r12.x = (sqrt2 * ...)^2
411  div r8.w, r8.w, r12.x                                  ; r8.w = -r8.w^2 / (2 * (sqrt2alpha)^2)
412  mul r8.w, r8.w, l(1.442695)                            ; * 1/ln(2)  -> 准备给 exp2
413  exp r8.w, r8.w                                         ; r8.w = 2^(...)  = exp(...)
414  div r8.w, r8.w, r14.y                                  ; r8.w /= sqrt(4pi)   Beckmann 归一化
```
[A] 这段就是 `exp(-((T.V+T.L)-2sin)^2 / (4alpha^2)) / sqrt(4pi alpha^2)` ---- **Beckmann 各向异性分布**。`1.442695 = 1/ln(2)` 是把 `exp(x)` 用 `exp2(x*1/ln2)` 替换（DXBC 优先 exp2 指令），最终数学值相同。

### Phase 6 (L415-425)：Schlick 菲涅尔

```
415  mul r8.w, r11.x, r8.w                                  ; r8.w *= r11.x
416  mad_sat r12.x, r6.w, l(0.500000), l(0.500000)          ; r12.x = sat(0.5 * V.L + 0.5)
                                                           ;        = sat((1 + V.L) / 2)
417  sqrt r12.x, r12.x                                      ; r12.x = sqrtsat(...)
418  add r12.x, -r12.x, l(1.000000)                         ; r12.x = 1 - sqrtsat(...)
419  mul r12.w, r12.x, r12.x                                ; r12.x^2
420  mul r12.w, r12.w, r12.w                                ; r12.x^4
421  mul r12.x, r12.x, r12.w                                ; r12.x * r12.x^4 = r12.x^5
422  mad r12.x, r12.x, l(0.953479), l(0.046521)             ; Fresnel: r12.x^5 * 0.95347 + 0.04652
423  mul r8.w, r8.w, r12.x                                  ; * Fresnel
424  mul r8.w, r8.w, l(0.250000)                            ; / 4  (BRDF denominator 4cosV cosL 中的 0.25)
```
[A] **Schlick 菲涅尔 `F(h.v) = F0 + (1-F0)(1-costheta)^5`**，其中 `F0 = 0.046521 ~= 0.04`（IOR 1.5 的绝缘体，正是标准皮肤 F0）。
`0.953479 = 1 - 0.046521` 是 `(1 - F0)` 展开。L422 这一行可以写成：

```
F = F0 + (1 - F0) * pow(1 - costheta_half, 5)
  = 0.046521 + 0.953479 * r12.x^5
```

### Phase 7 (L425-436)：SSS wrap + 第二个 Gaussian

```
425  mov_sat r6.w, -r6.w                                    ; r6.w = sat(-V.L)    (背光方向)
426  mad r4.w, r4.w, cb7[r1.w + 0].w, l(-1.000000)
427  mad r4.w, r6.w, r4.w, l(1.000000)                      ; SSS wrap 光照衰减
428  add r6.yw, r6.yyyy, -cb7[r1.w + 2].zzzw                ; (T.V+T.L) - LUT[2].zw
429  mul r6.yw, r6.yyyw, r6.yyyw                            ; 平方
430  mul r6.yw, r6.yyyw, l(0, -0.5, 0, -0.5)                ; -x^2/2
431  mul r12.xw, r13.xxxy, r13.xxxy                         ; alpha_t^4, alpha_b^4
432  div r6.yw, r6.yyyw, r12.xxxw                           ; / alpha^4
433  mul r6.yw, r6.yyyw, l(0, 1.442695, 0, 1.442695)        ; / ln(2)
434  exp r6.yw, r6.yyyw                                     ; exp
435  mul r12.xw, r13.xxxy, l(2.506628, 0, 0, 2.506628)      ; alpha^2 * sqrt(2pi) ~= 2.5066
436  div r6.yw, r6.yyyw, r12.xxxw                           ; Gaussian 归一化
```
[B] `2.506628 = sqrt(2pi)` ---- **Gaussian 的归一化常数** `1/sqrt(2pisigma^2)`。这里做了**第二个 Gaussian**，中心点由 `cb7[r1.w + 2].zw` 给（LUT 里可配置的 SSS 峰值位置），方差由 alpha^2 决定。

**这就是 SE 版 Kelemen SSS 的特色**：把 skin BRDF 分成 "GGX-like 直射 + 两个 Gaussian"，通过 skinID LUT 配置每种皮肤的 Gaussian 中心/宽度。论文见 *"A Practical Model for Realistic Subsurface Scattering in Video Games"*（Jorge Jimenez, 2012）。

### Phase 8 (L437-467)：第三个 Fresnel + 能量守恒

```
437  div r1.w, l(1,1,1,1), r11.w                            ; r1.w = 1/r11.w  (各向异性 Beckmann width)
438  mad r10.w, -r10.w, l(0.800000), l(0.600000)            ; r10.w = 0.6 - 0.8*r10.w
439  mad r10.w, r1.w, r10.w, l(1.000000)                    ; + 1
440  mul r10.w, r10.w, r11.x
441  mad r11.x, -r10.w, r10.w, l(1.000000)                  ; 1 - r10.w^2
442  max r11.x, r11.x, l(0.000000)
443  sqrt r11.x, r11.x                                      ; sqrtsat(1 - r10.w^2)
444  mad r11.x, -r9.w, r11.x, l(1.000000)                   ; 1 - r9.w * sqrt(...)
445  mul r11.w, r11.x, r11.x
446  mul r11.w, r11.w, r11.w                                ; r11.x^4
447  mul r11.x, r11.x, r11.w                                ; r11.x^5
448  min r11.x, r11.x, l(1.000000)
449  mad r11.x, r11.x, l(0.953479), l(0.046521)             ; * 又一个 Schlick Fresnel
450  add r11.x, -r11.x, l(1.000000)                         ; 1 - F
451  mul r11.x, r11.x, r11.x                                ; (1-F)^2

452  mul r1.w, r1.w, r10.w
453  mad r1.w, -r1.w, r1.w, l(1.000000)                     ; 1 - (...)^2
454  sqrt r1.w, r1.w                                        ; sqrt
455  mul r1.w, r1.w, l(0.500000)                            ; * 0.5
456  div r1.w, r1.w, r9.w                                   ; / r9.w
457  log r13.xyz, r9.xyzx                                   ; log(albedo^2) = 2*log(albedo)
458  mul r14.xyz, r1.wwww, r13.xyzx                         ; exponent
459  exp r14.xyz, r14.xyzx                                  ; exp(...)  = albedo^(2r1.w)
460  min r14.xyz, r14.xyzx, l(1,1,1,0)                      ; clamp
```
[B] L457-459 是 **`pow(albedo^2, exp)`** 的经典 DXBC 写法（log + mul + exp），等价于
`r14 = albedo^(2 * r1.w)`。这是皮肤次表面颜色的**能量守恒项**----吸收深度由 r1.w 决定。

```
461  mul r11.yz, r11.yyzy, l(0, 1.442695, 1.442695, 0)      ; r11.y*=1/ln2, r11.z*=1/ln2
462  exp r11.yz, r11.yyzy                                   ; exp(原本的 SSS 多项式两项)
463  mul r6.yw, r6.yyyw, r11.yyyz                           ; 与前面 Gaussian 相乘
464  mul r1.w, r11.x, r6.y
465  mul r11.xyz, r14.xyzx, r1.wwww                         ; 皮肤吸收色 * 权重
466  mul r11.xyz, r5.wwww, r11.xyzx                         ; * SSS 强度 (r5.w)
467  mad r11.xyz, r8.wwww, r4.wwww, r11.xyzx                ; + 直射 Beckmann * wrap
```
[A] `r11.xyz` 此时累积了：**wrap lighting * Beckmann 直射分布 + 能量守恒 SSS 色**。

### Phase 9 (L468-483)：第四个 Fresnel + 最终输出

```
468  mad r1.w, -r9.w, l(0.500000), l(1.000000)              ; 1 - 0.5*r9.w
469  mul r4.w, r1.w, r1.w
470  mul r4.w, r4.w, r4.w                                    ; r1.w^4
471  mul r1.w, r1.w, r4.w                                    ; r1.w^5
472  min r1.w, r1.w, l(1.000000)
473  mad r1.w, r1.w, l(0.953479), l(0.046521)                ; * Schlick Fresnel #3
474  add r4.w, -r1.w, l(1.000000)
475  mul r4.w, r4.w, r4.w                                    ; (1-F)^2
476  mul r1.w, r1.w, r4.w                                    ; F * (1-F)^2  -- 典型的"透射"权重
477  mul r12.xzw, r12.zzzz, r13.xxyz                         ; r12.xzw = power_exp * log(albedo^2)
478  exp r12.xzw, r12.xxzw                                   ; albedo^(2*r12.z)  第二次能量守恒
479  min r12.xzw, r12.xxzw, l(1, 0, 1, 1)
480  mul r1.w, r1.w, r6.w                                    ; * 背光 mask
481  mad r11.xyz, r1.wwww, r12.xzwx, r11.xyzx                ; 累加到 r11
482  min r11.xyz, -r11.xyzx, l(0,0,0,0)                      ; r11 = min(-r11, 0) = -max(r11, 0)
483  mov r11.xyz, -r11.xyzx                                  ; r11 = max(r11, 0)
                                                            ; L482-483 合起来 = clamp r11 到 [0, inf)
```
[A] Block E1 结束，`r11.xyz` 是**非负的 SSS + 各向异性 specular lobe 输出**。这个值会和 Block E2/E3 的输出一起汇入 r11，再被 Block F-I 消费。

### Block E1 数学总结

```
E1 输出 r11.xyz ~= skinBRDF_E1(V, L, T, roughness, LUT)
             = Fresnel1 * Beckmann_各向异性(T.V+T.L, 2alpha_t^2 alpha_b^2)
               * wrap(V.L)
             + (1 - Fresnel2)^2 * pow(albedo^2, 2r1.w)     <- SSS 吸收色 #1
             + Fresnel3 * (1 - Fresnel3)^2 * pow(albedo^2, 2r12.z) * 背光mask    <- SSS 吸收色 #2
```

- Fresnel 系数 `F0 = 0.04652` (= 0.046521)、`1-F0 = 0.95348` 出现至少 4 次
- Beckmann 各向异性：`cb7[LUT+3].yz` 是 `alpha_t, alpha_b`，`cb7[LUT+2].y` 是主轴角度
- 能量守恒：`pow(albedo^2, exp)` 出现 2 次，exp 来自 roughness 派生量
- Gaussian 中心：`cb7[LUT+2].zw`

这本质上是 **Kelemen/Szirmay-Kalos skin + GGX 各向异性 + Jimenez-style multi-lobe** 的三合一。

---

## 6b.3 Block E2 详解（L485-548）---- 标准 GGX

Block E2 是 `skinID LUT[0].x != 1` 且 `r2.z > 0`（法线 z 正）时的分支----**身体主流皮肤**走这里。

```
485  if_nz r2.z
486    dp3 r1.w, r7.xyzx, r3.xyzx                            ; r1.w = N . V       (NdotV)
487    mad r12.xzw, r10.xxyz, r0.zzzz, r3.xxyz               ; r12 = L + r0.z * r10 = 半向量拟合
488    dp3 r2.z, r12.xzwx, r12.xzwx
489    rsq r2.z, r2.z
490    mul r12.xzw, r2.zzzz, r12.xxzw                        ; r12 = normalize(L + r10)  ~= H (half-vector)
491    dp3_sat r2.z, r3.xyzx, r12.xzwx                       ; r2.z = sat(V . H)     (VdotH)
492    add r13.xyz, -r8.xyzx, l(1,1,1,0)                     ; r13 = 1 - F0     (F0 in r8)
493    add r2.z, -r2.z, l(1.000000)                          ; r2.z = 1 - VdotH
494    mul r4.w, r2.z, r2.z
495    mul r4.w, r4.w, r4.w
496    mul r2.z, r2.z, r4.w                                  ; (1-VdotH)^5
497    mad r13.xyz, r13.xyzx, r2.zzzz, r8.xyzx
                                                            ; r13 = F0 + (1-F0)(1-VdotH)^5
                                                            ;      = Schlick Fresnel（按 R/G/B 分别算）
498    dp3_sat r2.z, r7.xyzx, r12.xzwx                       ; r2.z = sat(N . H)    (NdotH)
```
[A] L492-497：**经典的 color-valued Schlick Fresnel**，`F(v.h) = F0 + (1-F0)(1-v.h)^5`。`r13.xyz` 是**彩色** Fresnel（因为 F0 来自金属反射系数 albedo^2 在 L341 已拟合成向量）。

```
499    mul r4.w, r0.y, r0.y                                  ; alpha = roughness^2
500    max r4.w, r4.w, l(0.001000)                           ; alpha >= 0.001
501    mul r5.w, r4.w, r4.w                                  ; alpha^2
502    max r6.y, r5.w, l(0.001000)                           ; >= 0.001
503    mul r2.z, r2.z, r2.z                                  ; NdotH^2
504    mad r6.w, r4.w, r4.w, l(-1.000000)                    ; alpha^2 - 1
505    mad r6.w, r2.z, r6.w, l(1.000000)                     ; NdotH^2(alpha^2-1) + 1
506    mul r6.w, r6.w, r6.w                                  ; 平方
507    mul r6.w, r6.w, l(3.141593)                           ; * pi
508    rcp r6.w, r6.w                                        ; 1 / denominator
509    mul r5.w, r5.w, r6.w                                  ; D(h) = alpha^2 / (pi * (NdotH^2(alpha^2-1) + 1)^2)
```
[A] **这就是教科书 GGX/Trowbridge-Reitz 分布**：

```
D(h) = alpha^2 / (pi * (NdotH^2(alpha^2 - 1) + 1)^2)
```

`r5.w` 现在是 D(h)。

```
510    mul r6.w, r6.y, r6.y                                  ; alpha^4   (smooth term)
511    mad r6.y, r6.y, r6.y, l(-1.000000)                    ; alpha^4 - 1
512    mad r2.z, r2.z, r6.y, l(1.000000)                     ; NdotH^2(alpha^4-1)+1
513    mul r2.z, r2.z, r2.z
514    mul r2.z, r2.z, l(3.141593)
515    rcp r2.z, r2.z
516    mul r2.z, r2.z, r6.w                                  ; 另一个 GGX D? 用 alpha^4 的"高粗糙"版本
517    add r6.y, r0.y, l(1.000000)                           ; roughness + 1
518    mul r6.y, r6.y, r6.y
519    mul r6.w, r6.y, l(0.125000)                           ; (r+1)^2 / 8   <- Smith G k1
520    mad r6.y, -r6.y, l(0.125000), l(1.000000)             ; 1 - (r+1)^2/8
521    mad r8.w, r3.w, r6.y, r6.w                            ; NdotL * (1-k) + k  <- Smith G_L
522    rcp r8.w, r8.w
523    mul r8.w, r3.w, r8.w                                  ; NdotL / (NdotL(1-k) + k)
524    mad r6.y, |r1.w|, r6.y, r6.w                          ; NdotV * (1-k) + k  <- Smith G_V
525    rcp r6.y, r6.y
526    mul r6.y, |r1.w|, r6.y                                ; NdotV / (NdotV(1-k) + k)
527    mul r6.y, r6.y, r8.w                                  ; G = G_V * G_L
```
[A] **Smith geometry-shadowing term**（UE4 Schlick-GGX 版）：

```
k = (roughness + 1)^2 / 8
G1(n, v) = NdotV / (NdotV(1-k) + k)
G = G1(n, v) * G1(n, l)
```

`r6.y` 现在是 G，`r5.w` 是 D，`r13.xyz` 是彩色 F。三者即将相乘。

```
528-538：类似 519-527 的 Smith G 第二套计算（用 alpha^4 的版本，做能量补偿）
539    mad r6.w, r6.z, l(-0.150000), l(1.000000)             ; 额外减弱
540    max r6.w, r6.w, l(0.850000)
541    mul r2.z, r2.z, r4.w                                  ; D * 某因子
542    mad r4.w, r5.w, r6.y, -r2.z                           ; D*G - D'*G'
543    mad r2.z, r6.w, r4.w, r2.z                            ; 混合
544    mul r12.xzw, r2.zzzz, r13.xxyz                        ; * Fresnel -> r12 = D*G*F
545    mul r1.w, r3.w, |r1.w|                                ; NdotL * |NdotV|
546    mad r1.w, r1.w, l(4.000000), l(0.000000)              ; * 4
547    rcp r1.w, r1.w
548    mul r11.xyz, r1.wwww, r12.xzwx                        ; r11 = (D*G*F) / (4 NdotV NdotL)
```
[A] **标准 Cook-Torrance 除以分母**：

```
BRDF_specular = D * G * F / (4 * NdotV * NdotL)
```

`r11.xyz` 此时是 **GGX 镜面反射项**。E2 结束。

### Block E2 数学总结

```
E2 输出 r11.xyz = GGX_spec(N, V, L, roughness, F0)
             = D_GGX(NdotH, alpha^2) * G_Smith(NdotV, NdotL, alpha) * F_Schlick(VdotH, F0)
               / (4 * NdotV * NdotL)
```

完全是 **UE4 经典 BRDF**，没有 skin-specific 修正。这也是"body 切 Emissive 接缝"的一个隐藏因子：Body PS[8] 走 E2，Emissive PS[19] 也走 E2（当 skinID != 1/3 时）----两者此处一致，所以接缝主要来自 Block A 早期（cb0[3]^2 初始化）和 Block I 尾段（cb5[0] 乘法）。

---

## 6b.4 Block E3 详解（L550-591）---- 简化 GGX

E3 是 `r2.z <= 0`（法线朝背面？）时的 fallback 分支，用于次要皮肤。

```
550  dp3 r1.w, r7.xyzx, r3.xyzx                              ; NdotV（同 E2）
551  mad r10.xyz, r10.xyzx, r0.zzzz, r3.xyzx                 ; 半向量拟合
552-554  normalize
555  dp3_sat r0.z, r3.xyzx, r10.xyzx                         ; VdotH
556  add r12.xzw, -r8.xxyz, l(1, 0, 1, 1)                    ; 1 - F0
557-560  Schlick (1-VdotH)^5
561  mad r12.xzw, r12.xxzw, r0.zzzz, r8.xxyz                 ; F = F0 + (1-F0)(1-VdotH)^5
562  mul r0.z, |r1.w|, r3.w                                  ; |NdotV| * NdotL
563  dp3_sat r2.z, r7.xyzx, r10.xyzx                         ; NdotH
564-573  D_GGX
574-584  Smith G (只算一次，不走能量补偿)
585-589  final = D*G*F / (4 NdotV NdotL)
590  ge r0.z, l(0.000000), r0.z                              ; denominator <= 0 ?
591  movc r11.xyz, r0.zzzz, l(0,0,0,0), r10.xyzx              ; 若 denominator <= 0 -> 输出 0
```

E3 和 E2 差别只在**没有第二个 GGX/Smith 的能量补偿项**（E2 的 L510-543）。这是为了性能优化 ---- 次要皮肤（身体非显眼区域）不值得双 lobe BRDF。

### Block E3 数学总结

```
E3 = D_GGX * G_Smith * F_Schlick / (4 NdotV NdotL)
```

**公式上和 E2 一致**，只省略了能量补偿那几步。细微之处是 L556 用 `l(1, 0, 1, 1)` 而不是 E2 的 `l(1, 1, 1, 0)`---- 一个 channel 的符号差异，但运行结果几乎相同。

---

## 6b.5 三个分支的共通出口（L593-596）

```
593  endif                                                   ; 结束 Block E
594  mul r0.z, r3.w, l(3.141593)                             ; r0.z = pi * NdotL
595  movc r0.z, r12.y, l(0), r0.z                            ; 若 LUT[0].x == 3 -> r0.z = 0
                                                            ; 否则 r0.z = pi * NdotL
596  mul r10.xyz, r0.zzzz, r11.xyzx                          ; r10 = r11 * r0.z
```
[A] E1/E2/E3 都把 specular 累积量留在 `r11.xyz`。L594-596 在出口处：

- **如果 skinID LUT[0].x == 3**（特殊皮肤），乘 0 -> specular 失效
- **否则**乘 `pi * NdotL` -> 和标准 Lambert 系数合拍

这解释了 `LUT[0].x == 3` 是"**完全没有 specular 的特殊皮肤**"---- 比如可能是幽灵角色、涂白漆的 NPC 之类。

`r10.xyz` 自此作为 specular 输出参与 Block F（Ch6 v1 Sec.6.7 已覆盖）。

---

## 6b.6 Block E 全章数学总结

| 概念 | 确定性 | 对应指令 |
|---|---|---|
| GGX 分布 D(h) = alpha^2/(pi(NdotH^2(alpha^2-1)+1)^2) | A | L499-509（E2）, L564-573（E3） |
| Smith G = NdotV/(NdotV(1-k)+k) * NdotL/(NdotL(1-k)+k), k=(r+1)^2/8 | A | L517-527（E2）, L574-584（E3） |
| Schlick F = F0 + (1-F0)(1-VdotH)^5 | A | L492-497（E2）, L556-561（E3）, L415-422/468-473（E1 四次） |
| Beckmann 各向异性 | A | L406-414（E1） |
| Gaussian SSS 衰减 | B | L428-436（E1） |
| arccos/arcsin 近似（Abramowitz） | B | L365-379（E1） |
| 能量守恒 pow(albedo^2, exp) | A | L457-460/477-479（E1） |
| skinID LUT[0].x == 3 -> 无 specular | A | L594-595 |
| 未拟合的 SSS 多项式常数 (0.5, -3.65, 17.0) 等 | C | L388（E1） |
| **F0 = 0.046521 (= 0.04)** 皮肤 IOR 1.5 典型值 | A | 所有 Fresnel 常数 |

## 6b.7 可改造热点（专属于 Block E 的）

| 目标 | 改什么 | 风险 |
|---|---|---|
| 让所有皮肤走简化 GGX（E3） | L350 `if_nz r12.x` 改成恒 false；L485 `if_nz r2.z` 改成恒 true | 低 -- 简单逻辑覆写 |
| 完全禁用 specular | L596 `r10.xyz = 0`（`mov r10.xyz, l(0,0,0,0)`） | 低 -- 效果=塑料皮 |
| 自定义各向异性方向 | 改 `cb7[r1.w+2].y`（运行时改 skin LUT） | 中 -- 要 mtrl 侧重写 |
| 改 F0 让皮肤更金属 | 把 L422 的 `0.046521` 改为更大值（如 0.5） | 中 -- 视觉会明显"金属化" |
| 移除 Gaussian #2（简化 SSS） | L428-436 全部改成 `mov r6.y, l(0); mov r6.w, l(0)` | 中 -- 脸部会失去 SSS |
| 注入新的 SSS 机制（比如 Jimenez screen-space） | 完全替换 Block E1 | 极高 -- 工程浩大但可行 |

## 6b.8 给 Ch7 的具体种子

Block E 的理解解锁的新 modding 方向：

1. **可配置金属皮肤**：给 skin.shpk 加一个 shader key `MetallicSkin`，在 L422 前插入 `cb0[xxx].xyz` 乘到 r12.x，让 mtrl 级 `g_MetallicSkinColor` 控制金属度。
2. **用户可编辑 SSS 曲线**：把 L388 的两组 6 个常数（`0.5, -3.65, 17.0, 0.5, -3.98, -16.78`）改成从 cb0 读取，mtrl 侧暴露 "SSS Curve Preset" 下拉。
3. **各向异性虫形皮肤**：L395 `cb7[r1.w + 3].yz` 现在是 skin LUT 配置。可以改成读 ColorTable 行，让不同 UV 区域有不同各向异性强度 -> 皮肤上可画"丝绸纹理"。
4. **Fresnel 调色**：L422 的 `(0.953479, 0.046521)` 改成从 cb0/cb5 读，runtime 可调 -> 皮肤"湿度感"滑条。

## 状态

- [x] Ch0 总览
- [x] Ch1 shpk 结构
- [x] Ch2 SkinType 分支全貌
- [x] Ch3 DXBC 对比
- [x] Ch4 cbuffer/sampler/texture 清单
- [x] Ch4 附录：MaterialParam CRC 认领
- [x] Ch5 接缝与改造路径
- [x] Ch6 PS[19] 逐段解剖 v1
- [x] **Ch6 v2：Block E SSS/GGX 细解（本文）**
- [ ] Ch7 高级效果 idea 清单
