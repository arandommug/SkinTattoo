# SkinTatoo 文档索引

> 项目简介与构建说明请看仓库根目录的 [`CLAUDE.md`](../CLAUDE.md)。
> 这里只汇总技术文档与设计/实施历史。

## 常用参考

| 文档 | 用途 |
|---|---|
| [`development-notes.md`](development-notes.md) | 开发踩坑记录：Dalamud CN 环境、Lumina 版本、SqPack 限制、`.tex` 文件格式、Penumbra IPC 陷阱 |
| [`ConstantBuffer逆向分析.md`](ConstantBuffer逆向分析.md) | `ConstantBuffer` 0x70 字节内存布局、`Flags` 含义、`LoadSourcePointer` 调用约定，**实时 emissive 实现的依据** |

## 设计 spec（变更前的目标与决策）

| Spec | 描述 |
|---|---|
| [`superpowers/specs/2026-04-02-skintatoo-design.md`](superpowers/specs/2026-04-02-skintatoo-design.md) | 项目初版总设计：管线、模块拆分、CN/国际服差异 |
| [`superpowers/specs/2026-04-06-3d-decal-editor-design.md`](superpowers/specs/2026-04-06-3d-decal-editor-design.md) | 3D 贴花编辑器：DX11 离屏渲染、`OrbitCamera`、`RayPicker` 拾取 |
| [`superpowers/specs/2026-04-07-mod-export-design.md`](superpowers/specs/2026-04-07-mod-export-design.md) | Mod 导出：复用 `PreviewService` 合成路径 → `.pmp` zip → 本地保存 / `InstallMod` IPC |

## 实施计划（多步骤 task 拆分，含验收清单）

| Plan | 对应 spec |
|---|---|
| [`superpowers/plans/2026-04-02-skintatoo-implementation.md`](superpowers/plans/2026-04-02-skintatoo-implementation.md) | 初版总实施 |
| [`superpowers/plans/2026-04-06-3d-decal-editor.md`](superpowers/plans/2026-04-06-3d-decal-editor.md) | 3D 编辑器 |
| [`superpowers/plans/2026-04-06-realtime-fixes-and-glow-enhancements.md`](superpowers/plans/2026-04-06-realtime-fixes-and-glow-enhancements.md) | 实时管线修复 + 发光增强 |
| [`superpowers/plans/2026-04-07-mod-export.md`](superpowers/plans/2026-04-07-mod-export.md) | Mod 导出 |

> spec/plan 是**历史快照**——对应 commit 已合入主分支，文档保留作为决策记录，不再持续维护。当前代码以 `git log` 与 `CLAUDE.md` 为准。

## 运行时调试

插件启动后会监听 `http://localhost:14780/`，所有 REST 端点列在 [`CLAUDE.md`](../CLAUDE.md) 的 "HTTP 调试 API" 一节。
