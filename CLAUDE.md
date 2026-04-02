# SkinTatoo - FFXIV 实时皮肤贴花插件

## 项目概述

Dalamud 插件，将 PNG 贴花投影到 FFXIV 角色皮肤上，通过 Penumbra 临时 Mod 实时预览。

## 构建

```bash
cd /c/Users/Shiro/Desktop/FF14Plugins/SkinTatoo
dotnet build -c Release
```

Dalamud SDK 路径通过 `Directory.Build.props` 自动指向 `XIVLauncherCN`。如果构建失败检查 `%AppData%\XIVLauncherCN\addon\Hooks\dev\` 是否存在。

**每次修改代码后必须执行 `dotnet build -c Release` 验证编译通过。**

## 项目结构

```
SkinTatoo.slnx                       # 解决方案
Penumbra.Api/                         # git submodule (MeowZWR cn-temp)
SkinTatoo/SkinTatoo/                  # 主项目
  Plugin.cs                           # 入口
  Configuration.cs                    # 持久化配置
  Core/                               # 数据模型 (DecalLayer, DecalProject)
  Gpu/                                # DX11 计算着色器管线
    Shaders/*.hlsl                    # 3 个 HLSL 着色器 (投影/合成/膨胀)
  Mesh/                               # 网格提取与 UV 位置图生成
  Interop/                            # Penumbra IPC (PenumbraBridge, BodyModDetector)
  Services/                           # PreviewService, TexFileWriter, DecalImageLoader
  Http/                               # EmbedIO HTTP 调试服务器 (localhost:14780)
  Gui/                                # ImGui 窗口 (MainWindow, ConfigWindow)
```

也有部分文件在 `SkinTatoo/Mesh/` 和 `SkinTatoo/Services/`（项目上级），通过 csproj 的 `<Compile Include>` 引入。

## 关键约定

- 命名空间：`SkinTatoo.*`（如 `SkinTatoo.Gpu`, `SkinTatoo.Core`）
- UI 语言：中文
- 代码注释：仅在关键处写英文注释
- 提交信息：不写 Co-Authored-By
- HLSL 着色器以嵌入资源方式编译，资源名格式 `SkinTatoo.Gpu.Shaders.{name}`
- GPU 操作使用 TerraFX.Interop.Windows（unsafe 指针），不用 SharpDX

## 依赖

| 库 | 用途 |
|---|---|
| Penumbra.Api (submodule) | Penumbra IPC 类型安全封装 |
| Lumina | .mdl/.tex 文件解析 |
| StbImageSharp | 图片加载 |
| TerraFX.Interop.Windows | DX11 互操作 |
| EmbedIO | HTTP 调试服务器 |

## HTTP 调试 API

插件启动后在 `http://localhost:14780/` 提供 REST API：

- `GET /api/status` — 插件状态
- `GET /api/project` — 当前项目 JSON
- `POST /api/layer` — 添加图层
- `PUT /api/layer/{id}` — 修改图层参数
- `DELETE /api/layer/{id}` — 删除图层
- `POST /api/preview` — 触发预览更新
- `POST /api/mesh/load` — 加载网格
- `GET /api/mesh/info` — 网格信息
- `GET /api/log` — 最近日志

## 核心管线流程

```
PNG 图片 → GPU 投影(ProjectionPass) → 合成(CompositePass) → 膨胀(DilationPass)
    → GPU 回读 → 写临时 .tex → Penumbra 临时 Mod → 角色重绘
```

## 游戏内测试

1. `/xlsettings` → Dev Plugin Locations 添加输出目录
2. `/xlplugins` 启用 SkinTatoo
3. `/skintatoo` 打开编辑器
4. HTTP: `curl http://localhost:14780/api/status`
