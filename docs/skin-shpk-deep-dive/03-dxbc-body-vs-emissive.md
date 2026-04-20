# Ch3 -- DXBC 级别对比：Face / Body / BodyJJM / Emissive 的 pass[2] lighting PS

> 数据来源：`SkinTatoo/ShaderPatcher/extracted_ps/ps_{002,008,012,019}_disasm.txt`
> 提取工具：`SkinTatoo/ShaderPatcher/extract_ps.py`（本轮新写，详见 Sec.3.6）
> 对照：`reference/ps_019_EMISSIVE_disasm.txt` 作为先前版本的交叉验证
> 结论级别：**这是本研究目前最重要的一章**。"接缝是 Emissive 光照模型不同"这一直观假设被数据否定，真相另有所在。

## 3.1 四份样本的 blob 大小与指令数

```
PS[2]  Face       blob=20236 B   disasm=752 行   instr ~= 475
PS[8]  Body       blob=20264 B   disasm=753 行   instr ~= 476
PS[12] BodyJJM    blob=20300 B   disasm=754 行   instr ~= 477
PS[19] Emissive   blob=20796 B   disasm=777 行   instr ~= 485
```

四份样本的 blob 大小极其接近：Emissive 比 Body 只多 532 字节、9 条指令。这从量级上就排除了"Emissive 是另一套完全不同的光照模型"的假设。

## 3.2 资源绑定（cbuffer）对比

### Face / Body / BodyJJM ---- 完全相同

```
C  slot=0  size=  20  g_MaterialParameter        (0x64D12851)
C  slot=1  size=   4  g_CommonParameter          (0xA9442826)
C  slot=2  size=   5  g_PbrParameterCommon       (0xFF0F34A7)
C  slot=3  size=  59  g_CameraParameter          (0xF0BAD919)
C  slot=4  size=  11  g_InstanceParameter        (0x20A30B34)
C  slot=5  size=  10  g_AmbientParam             (0xA296769F)
C  slot=6  size=2048  g_ShaderTypeParameter      (0x3A310F21)
```

三种 SkinType 的 cbuffer 条目、slot、大小全部一致。这与 Sec.2.3 里"pass[2] 所有 SkinType 共用 VS[2, 11, 20, ...]"呼应 ---- **同一 VS 输出喂给不同的 PS，但 PS 的输入接口完全相同**。

### Emissive ---- 在 slot 5 插入一个新 cbuffer，其余后移

```
C  slot=0  size=  20  g_MaterialParameter         同上
C  slot=1  size=   4  g_CommonParameter           同上
C  slot=2  size=   5  g_PbrParameterCommon        同上
C  slot=3  size=  59  g_CameraParameter           同上
C  slot=4  size=  11  g_InstanceParameter         同上
C  slot=5  size=   1  g_MaterialParameterDynamic  (0x77F6BFB3)  * NEW
C  slot=6  size=  10  g_AmbientParam              (shifted from slot 5)
C  slot=7  size=2048  g_ShaderTypeParameter       (shifted from slot 6)
```

新增的 `g_MaterialParameterDynamic` 只有 1 vec4（16 字节）大小，结构是：

```
struct MaterialParameter {
    float4 m_EmissiveColor;   // Offset: 0
} g_MaterialParameterDynamic; // Size: 16
```

整份 shader 里对 cb5 的使用只有**一条指令**（见 Sec.3.4）。

### Sampler / Texture 对比

四份样本的 Samplers（5 项）与 Textures 在资源 id/slot 上完全一致（t0..t9, s0..s4），没有任何分支特有的纹理或采样器。**意味着"换 SkinType 不会新增纹理依赖"**。

## 3.3 输入签名（TEXCOORD）的细微但关键差异

```
dcl_input_ps linear v2.xy         <- Face -> * 其实是 Body 这样写
dcl_input_ps linear v2.xy         <- Body
dcl_input_ps linear v2.xy         <- BodyJJM
dcl_input_ps linear v2.xyzw       <- Face   *
dcl_input_ps linear v2.xyzw       <- Emissive
```

修正为：

| PS | v2 读取范围 |
|---|---|
| PS[2] Face | `v2.xyzw`（4 分量）|
| PS[8] Body | `v2.xy`（2 分量）|
| PS[12] BodyJJM | `v2.xy`（2 分量）|
| PS[19] Emissive | `v2.xyzw`（4 分量）|

**Face 和 Emissive 都读 4 个分量，Body/BodyJJM 只读 2 个。** 进一步看代码：

- Body: `mul r1.xy, v2.xyxx, cb0[7].xyxx` ---- 用 **v2.xy** 作为纹理采样 UV
- Face: `mul r1.xy, v2.zwzz, cb0[7].xyxx` ---- 用 **v2.zw** 作为纹理采样 UV（第二套 UV！）
- Emissive: `mul r2.xy, v2.zwzz, cb0[7].xyxx` ---- 同 Face，用 **v2.zw**

这是我们在 DXBC diff 里**唯一一处真正的代码语义差异**（除了 Emissive 的那一条 cb5 乘法）。

**引申结论**：Face 与 Body 在 vanilla 引擎里本来就用两套不同的 UV 贴图采样方式。脸部的皮肤贴图沿 v2.zw，身体的沿 v2.xy。这就是颈部接缝在 vanilla 游戏里本来也**存在**（Face 和 Body 的 UV 映射不同，只是美术与贴图通常匹配得让差异不明显）。

我们原本以为"开启发光 -> 出现接缝"，但真实情况可能是：**开启发光 -> body 切换到 Emissive（仍走 v2.zw，与 Face 一致） -> body 的 UV 采样路径和 face 对上了 -> 观感变化暴露出美术原本靠不匹配 UV 掩盖的区域**。

这和我们之前的假设方向完全相反。

## 3.4 Emissive 特有的一条指令

在 PS[19] 的代码里，对 `cb5`（= g_MaterialParameterDynamic.m_EmissiveColor）的使用**总共只有一条指令**，位于第 753 行：

```
 752  mad r0.yzw, r3.xxyz, r0.yyyy, r4.xxyz   // 将某个光照项累加到 r0.yzw
 753  mul r1.xyz, r1.xyzx, cb5[0].xyzx        // * r1.xyz *= g_EmissiveColor
 754  dp3 r2.w, r2.xyzx, l(0.29891, 0.58661, 0.11448, 0)  // Rec.709 亮度
 755  max r2.w, r2.w, l(1.000000)
 756  mul r3.xyz, r1.xyzx, r2.wwww            // r3.xyz = 调制后的 r1 * 亮度
```

在 PS[8] Body 里对应位置则是：

```
(相同位置，无 cb5[0] 乘法)
mad r0.yzw, r3.xxyz, r0.yyyy, r4.xxyz
dp3 r2.w, r2.xyzx, l(0.29891, 0.58661, 0.11448, 0)
max r2.w, r2.w, l(1.000000)
mul r3.xyz, r1.xyzx, r2.wwww            // 用原始 r1 * 亮度
```

换言之 Emissive 相较 Body 的语义改动是：

> 把 `r1.xyz`（此时代表的是 specular/高光累积量）**乘上一个 material 级的发光色**，再拿去参与后续的高亮分量计算。

这是一种"以 specular 强度为载体"的发光混合方式 ---- 并不是常见的 additive emissive（`o0.rgb += emissive`）。所以 Emissive 的 `g_EmissiveColor` 越亮，并不是单纯加亮，而是**把高光部分放大 N 倍**，遇到暗区反而没有效果。这也解释了我们 UI 里 "发光在黑暗环境下看不见" 的观察。

## 3.5 尾段输出完全一致

所有 4 份 PS 的最后三行指令**逐字相同**：

```
max r0.xyz, r0.xyzx, l(0.000000, 0.000000, 0.000000, 0.000000)
sqrt r0.xyz, r0.xyzx
mul o0.xyz, r0.xyzx, cb1[3].xxxx
ret
```

- `max + sqrt` 等价于做了一个 `sqrt(max(x, 0))` 的 HDR -> 线性开方（Gamma 2.0 的反变换）
- 最后 `mul o0.xyz, r0, cb1[3].xxxx` 把结果乘以 `g_CommonParameter.m_Misc.x`（可能是全局曝光或增益）
- `o0.w` 在更早的行里被单独写入了

**这一观察极其关键：最终写入 RT 的颜色在所有分支都是 `sqrt(max(r0, 0)) * cb1[3].x`。不同分支只是把各自的光照结果写进 `r0` 的方式不同。** 换言之"接缝"绝不是最终输出阶段造成的，必然出自 `r0` 计算过程中某处的分支差异。

## 3.6 复现方式

```bash
cd C:/Users/Shiro/Desktop/FF14Plugins/SkinTatoo/ShaderPatcher
# 把指定 PS 从默认 ../../skin.shpk 提取并反汇编
python extract_ps.py 2 8 12 19
# 或传入具体 shpk 路径
python extract_ps.py /path/to/skin.shpk 19

# 输出落在 ShaderPatcher/extracted_ps/ 下：
#   ps_002.dxbc  ps_002_disasm.txt  <- Face pass[2]
#   ps_008.dxbc  ps_008_disasm.txt  <- Body pass[2]
#   ps_012.dxbc  ps_012_disasm.txt  <- BodyJJM pass[2]
#   ps_019.dxbc  ps_019_disasm.txt  <- Emissive pass[2]

# Diff：
diff -a ps_002_disasm.txt ps_008_disasm.txt   # Face vs Body：29 行差异
diff -a ps_008_disasm.txt ps_019_disasm.txt   # Body vs Emissive：903 行差异（但绝大多数是 reg alloc 噪声）
```

脚本 `extract_ps.py` 依赖：
- `shpk_patcher.parse_shpk_full()` ---- 解析 shpk 二进制
- `dxbc_patch_colortable.d3d_disassemble()` ---- 调 `D3DCompiler_47.dll.D3DDisassemble` 还原文本

## 3.7 Ch3 核心结论

1. **Face / Body / BodyJJM 三者的 cbuffer 布局完全一致**。Emissive 在 slot 5 插入 `g_MaterialParameterDynamic`（16 字节，只存 m_EmissiveColor），导致其后 cbuffer 全部 +1。这是 DXBC patch 设计时唯一需要考虑的"结构性"差异。

2. **Emissive 相较 Body 在语义上只多一条 `mul r1.xyz, r1.xyzx, cb5[0].xyzx`**。其他 9 条额外指令是由此引起的寄存器分配连锁（dcl_temps 16 vs 15、少量 r* 编号重分配），**不构成光照模型差异**。

3. **真正的 Face vs Body 语义差异是：UV 采样源不同**。Face 用 `v2.zw`，Body 用 `v2.xy`。vanilla 中颈部接缝本就存在微小差异，只是艺术资源刻意让它不可见。

4. **因此"启用发光后出现接缝"的根本原因很可能并非"切了 SkinType -> 光照模型改变"，而是**：
   - 切 SkinType 顺带改变了 UV 采样源（v2.xy -> v2.zw，body 现在沿 face 相同的 UV）；或
   - 发光乘法（cb5[0] * specular）放大了 body 贴图纹理本就存在的 specular 差异、使接缝变得可见；或
   - 我们自己的 MtrlFileWriter 在强切 SkinType 时附带改动了别的 shader key，牵连到其他非 lighting pass。

5. **改造路径评估全面刷新**（Ch5 会定稿）：
   - **路径 A**（调整 PS[19] 使之匹配 Body）现在变得几乎无意义 ---- PS[19] 本身就是 Body PS + 一条乘法，两者已经非常接近。
   - **真正要做的是 Ch4 + 定位接缝成因**：搞清楚是 UV 路径、specular 放大、还是 mtrl 副作用。
   - **路径 B**（给 Body/Face 注入 ColorTable 发光）在这里看来又变得可行了 ---- 目标是在 Body PS 里复制 Emissive 的那一条 cb5 乘法 + 资源绑定，工程量虽然不小但"可定位"。
   - **路径 C**（同步把脸也推到 Emissive）：此时的含义也要修正 ---- Emissive 本身保留了 Face 的 v2.zw UV 采样规律，脸走 Emissive 不会破坏本来的 UV 关系。C 可能比想象中更无感。

## 3.8 留给后续章节的问题

- **Q1**：PS[19] 里早期出现的 `mul r1.xyz, cb0[3].xyzx, cb0[3].xyzx` + `mul r1.xyz, r0.zzzz, r1.xyzx` 这两条，Body/Face 都没有。r1 在随后的 sample 里被覆盖，这两条的产物是否对后续某个分支有实际贡献？还是编译器的 dead code？-> 留给 Ch4 再查。
- **Q2**：`g_ShaderTypeParameter`（2048 vec4，即 **32 KB**）是一张巨大的查找表，被所有 pass[2] PS 共享，`cb6[r1.z + N]`（Face/Body）或 `cb7[r1.w + N]`（Emissive）间接寻址。这张表是什么？皮肤 shader ID 的一个调色板？-> Ch4 重点。
- **Q3**：Face 里对 `t5` 的 sample swizzle 是 `t5.zxyw`，Body 的是 `t5.zxwy`（两者仅交换了最后两位）。实际是否影响输出？还是读到相同的分量只是顺序不同？-> 写一个小脚本单步追溯后能回答。
- **Q4**：第 5 个 SkinType 值 `0xF421D264` 在 pass[2] 层面与 Face 的 PS 集合完全相同 ---- 它们的 DXBC blob 是否逐字节相同？还是只是"同一套 PS 被两个 SkinType 都指向"？-> 一个简单的 blob hash diff 即可证实。

## 状态

- [x] Ch0 总览
- [x] Ch1 shpk 结构
- [x] Ch2 SkinType 分支全貌
- [x] Ch3 DXBC 对比（本文）
- [ ] Ch4 cbuffer 清单（特别是 g_ShaderTypeParameter）
- [ ] Ch5 接缝与改造路径
