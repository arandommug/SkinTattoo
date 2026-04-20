# skin.shpk 深度研究总览

> 启动日期：2026-04-20
> 目的：在不局限于 SkinTattoo 需要的前提下，系统地逆向并记录 FFXIV `shader/sm5/shpk/skin.shpk` 的全部可观察行为，方便后续任何皮肤 shader 相关工作（发光、PBR、接缝修复、着色器替换）都能复用同一份参考。
> 约束：本阶段只做研究与文档，不改动运行代码；所有结论必须给出可复现的取证路径（IDA 地址、DXBC 反汇编行号、log 片段等）。

## 为什么要做这件事

当前插件在启用发光时，会把身体材质的 `CategorySkinType` 从 `Body/BodyJJM` 强切到 `Emissive`，导致身体走上了另一套 PS 光照路径，与脸部仍保留的 `Face` 分支在颈/腕边界产生可见接缝。初步 fix 思路分三条（A：把 Emissive PS 的光照调得和 Body 一致；B：给 Body/Face 分支也注入 ColorTable 发光；C：把脸部 mtrl 也推到 Emissive 分支），任何一条都建立在"对 skin.shpk 各分支的底层行为有完整理解"之上。因此先把研究补齐，fix 自然水到渠成。

## 章节规划

| 章节 | 主题 | 产出 | 依赖 |
|---|---|---|---|
| Ch0（本文） | 研究总览、方法论、术语表 | 00-overview.md | 无 |
| Ch1 | shpk 二进制容器与 node/selector 选择链路 | 01-shpk-structure.md | Penumbra `ShpkFile.cs`、`parse_shpk.py`、XIV Docs 格式页 |
| Ch2 | skin.shpk 四个 SkinType 分支（Face/Body/BodyJJM/Emissive）的 PS 集合、pass 功能、节点分裂规律 | 02-skintype-branches.md | Ch1；我们运行时 log 的 NodeDump 输出；vanilla shpk 解析 |
| Ch3 | DXBC 级别对比 Body PS 代表样本 vs Emissive PS[19]：资源绑定、寄存器布局、主要代码段 | 03-dxbc-body-vs-emissive.md | Ch2；ShaderPatcher/reference 现有反汇编 + 新提取的 Body PS |
| Ch4 | 全部 cbuffer 清单与字段含义（g_MaterialParameter, g_CommonParameter, g_CameraParameter, g_InstanceParameter, g_MaterialParameterDynamic, g_AmbientParam, …）以及各分支的差异 | 04-cbuffers.md | Ch3；PS 反汇编头部 cbuffer 声明 |
| Ch5 | 接缝成因物理解释 + A/B/C 三条改造路径的工程评估（工作量、误伤面、验证门槛） | 05-seam-and-fix-paths.md | Ch2-Ch4 |
| Ch6（候选） | 引擎侧的 shader 选择 fast-path（IDA 反编译对照） | 06-engine-fastpath.md | route-c-ida-research.md；IDA 现场 |
| Ch7（候选） | 与其它 shpk 的关系（iris/character/charactertattoo）、共享 cbuffer/sampler | 07-cross-shpk.md | Ch4 |

## 方法论

- **取证优先**：每条结论都附来源（文件行号、IDA 地址、log 片段）。避免"据说"。
- **可复现脚本**：凡是从 shpk 里提取信息的，都先尝试用现有 `parse_shpk.py` / `extract_shpk.py` 完成；不足时写最小增量脚本并落盘到 `ShaderPatcher/` 下。
- **不假设对称**：Body/BodyJJM/Face/Emissive 四个分支各做独立验证，不默认它们结构相同。
- **小步提交**：每写完一章先停，让用户过目再推进，避免早期假设错误传递到后续章节。

## 术语表

| 术语 | 含义 |
|---|---|
| shpk | ShaderPackage 文件，FFXIV 把多个 VS/PS 变体及其选择逻辑打包的容器 |
| Node | shpk 里的一项变体条目，包含一组 pass（Passes[]）和一组 key 值 |
| Pass | Node 的一个渲染阶段，对应一对 `{VS, PS}` 索引；同一 node 的 pass 用 `PassIndices[16]` 按 SubView 查表 |
| SubView | 当前渲染子视图（主摄像机 / 阴影 / 速度缓冲 / 反射 / ...），索引 0..15 |
| SystemKey / SceneKey / MaterialKey / SubViewKey | 四类渲染状态 key，每个 key 有若干可选值。四类值通过多项式哈希生成 selector |
| Selector | `sysKey + sceneKey*31 + matKey*961 + subViewKey*29791`，用于在 `NodeSelectors` 字典里反查 node |
| SkinType | skin.shpk 里 MaterialKey 之一（CRC `0x380CAED0`），当前可选值：Face / Body / BodyJJM / Emissive / Hrothgar（部分版本）/ ... |
| Pass[2] LIGHTING PS | skin.shpk node 的 Pass[2] 位置固定是"主光照 PS"（deferred 主场景 SubView 使用），决定实际皮肤光照观感 |
| ColorTable | mtrl 末尾挂的 32×8 vec4 浮点表，character.shpk/skin_ct.shpk 等会用它做 per-row PBR；skin.shpk 原版不读 |
| DXBC | DirectX Byte Code，SM5 shader 的二进制格式，含 SHEX（指令流）/RDEF（资源定义）/ISGN/OSGN/STAT 等 chunk |

## 术语以外的关键符号

| CRC | 名称 | 说明 |
|---|---|---|
| `0x380CAED0` | CategorySkinType | skin.shpk 的 MaterialKey 类别 |
| `0x2BDB45F1` | ValBody | Body 分支值 |
| `0xF5673524` | ValFace | Face 分支值（也是 MatKey[0] 默认值） |
| `0x57FF3B64` | ValBodyJJM | BodyJJM 分支值（bust/JJM 专用） |
| `0x72E697CD` | ValEmissive | Emissive 分支值（我们当前强切到这里） |
| `0xD2777173` | CategoryDecalMode | 贴花模式 MaterialKey |
| `0xF52CCF05` | CategoryVertexColorMode | 顶点色模式 MaterialKey |
| `0x2005679F` | g_SamplerTable | ColorTable 纹理采样器 |
| `0x38A64362` | g_EmissiveColor | 发光颜色常量 |

## 状态

- [x] Ch0 总览（本文）
- [ ] Ch1 shpk 结构
- [ ] Ch2 SkinType 分支全貌
- [ ] Ch3 DXBC 对比
- [ ] Ch4 cbuffer 清单
- [ ] Ch5 接缝与改造路径
- [ ] Ch6 引擎 fast-path（候选）
- [ ] Ch7 跨 shpk 关系（候选）
