# 3D 贴花编辑器 & 贴花裁剪 — 设计文档

## 概述

为 SkinTatoo 插件新增两项功能：

1. **贴花半切预处理** — 对贴花图片做左/右/上/下半切裁剪，解决 FFXIV 镜像纹理问题
2. **3D 投影编辑器** — 在 3D 模型上直接点击/拖拽定位贴花，自动转换为 UV 坐标，走现有合成管线应用到游戏

## 背景与动机

- FFXIV 部分身体纹理只包含一半（如右半身），游戏内左右镜像显示。贴花跨越镜像边界时会产生错乱
- 当前贴花编辑完全在 2D UV 空间进行，用户需要理解 UV 布局才能准确放置贴花
- 游戏内已有实时预览（GPU Swap），但缺少直观的 3D 定位手段

## 功能设计

### Feature 1: 贴花半切预处理

**用户操作**：在 DecalLayer 属性面板中新增「裁剪」下拉选项：
- `None` — 不裁剪（默认）
- `ClipLeft` — 裁掉左半（保留右半）
- `ClipRight` — 裁掉右半（保留左半）
- `ClipTop` — 裁掉上半（保留下半）
- `ClipBottom` — 裁掉下半（保留上半）

**实现方式**：在 `DecalImageLoader` 加载图片后、进入合成管线前，对像素数据做裁剪：
- 裁掉的半边 alpha 设为 0（透明），保留原始尺寸不变
- 这样 UV 坐标、缩放比例等参数不受影响
- 合成时透明区域自然被跳过

**数据模型变更**：
```csharp
// DecalLayer.cs
public enum ClipMode { None, ClipLeft, ClipRight, ClipTop, ClipBottom }
public ClipMode Clip { get; set; } = ClipMode.None;
```

**管线集成点**：`PreviewService.CpuUvComposite()` 中采样贴花像素后，根据 ClipMode 判断该像素是否在裁剪区域内，若是则 alpha = 0 跳过。或在 `DecalImageLoader` 返回数据时预处理。

### Feature 2: 3D 投影编辑器

#### 架构概览

```
┌──────────────────────────────────────────────────────────────┐
│  ModelEditorWindow (ImGui 独立窗口)                           │
│  ┌─────────────────────────┐  ┌───────────────────────────┐  │
│  │  DxRenderer             │  │  OrbitCamera              │  │
│  │  - RenderTarget (tex2D) │  │  - Yaw / Pitch / Distance │  │
│  │  - DepthStencil         │  │  - Pan offset             │  │
│  │  - VS/PS Shader         │  │  - View / Proj matrices   │  │
│  │  - State save/restore   │  │                           │  │
│  └─────────┬───────────────┘  └───────────────────────────┘  │
│            │ SRV.NativePointer                                │
│            ↓                                                  │
│  ImGui.Image(srvPtr, size) + 鼠标输入处理                      │
│            │                                                  │
│  ┌─────────▼───────────────┐                                  │
│  │  RayPicker              │                                  │
│  │  - Unproject screen→ray │                                  │
│  │  - Ray-Triangle求交     │                                  │
│  │  - 重心坐标 → UV 插值   │                                  │
│  └─────────┬───────────────┘                                  │
│            │ UV coordinates                                   │
│            ↓                                                  │
│  DecalLayer.UvCenter / UvScale / RotationDeg                  │
│            │                                                  │
│            ↓                                                  │
│  现有管线: CpuUvComposite → TextureSwapService                │
└──────────────────────────────────────────────────────────────┘
```

#### 新增模块

##### 1. DirectX/ 目录

**DxRenderer.cs** — DX11 离屏渲染器
- 从 `PluginInterface.UiBuilder.DeviceHandle` 获取 ID3D11Device
- 创建 RenderTarget (Texture2D, B8G8R8A8_UNorm, BindFlags = RenderTarget | ShaderResource)
- 创建 DepthStencil (Texture2D, D32_Float)
- 渲染前保存 DX11 状态（RenderTargets, Viewport, Shaders 等），渲染后恢复
- 动态 resize：当 ImGui 可用区域变化时重建 RenderTarget
- 参考：VFXEditor `DirectX/Renderer.cs` 和 `DirectX/Renderers/ModelRenderer.cs`

**ShaderManager.cs** — Shader 编译与管理
- 编译 HLSL vertex/pixel shader（SharpDX.D3DCompiler）
- 管理 InputLayout, ConstantBuffer
- Shader 内容：简单 Blinn-Phong（MVP 变换 + 单方向光漫反射 + 高光）

**MeshBuffer.cs** — GPU 网格缓冲
- 从 MeshData 构建 VertexBuffer / IndexBuffer
- Vertex 格式：Position (float3) + Normal (float3) + UV (float2) = 32 bytes
- 支持纹理绑定（将合成后的纹理作为 diffuse 贴图）

##### 2. Mesh/RayPicker.cs — 射线拾取

**核心算法**：
1. 屏幕坐标 → NDC → 通过逆 ViewProjection 矩阵 unproject 得到射线 (origin, direction)
2. 遍历所有三角面，Möller–Trumbore 算法求交
3. 取最近命中点，用重心坐标 (w0, w1, w2) 插值三角面顶点 UV：
   `hitUV = w0 * uv0 + w1 * uv1 + w2 * uv2`
4. 返回 hitUV 作为 DecalLayer.UvCenter

**性能考虑**：
- FFXIV 角色模型约 5000-15000 三角面，暴力遍历在现代 CPU 上 < 1ms
- 如果后续性能不足可加 BVH 加速结构

##### 3. Gui/ModelEditorWindow.cs — 3D 编辑器窗口

**布局**：
- 顶部工具栏：加载模型按钮、显示模式切换（实体/线框）、重置相机
- 中央：3D 视图（ImGui.Image）
- 底部状态栏：当前命中 UV 坐标、相机参数

**交互映射**：

| 输入 | 动作 |
|------|------|
| 左键点击模型 | 放置贴花（RayPicker → UvCenter） |
| 左键拖拽模型 | 沿表面移动贴花（连续 RayPick） |
| 滚轮（悬停模型上） | 缩放贴花 (UvScale) |
| 右键拖拽 | 旋转相机 (Yaw/Pitch) |
| 中键拖拽 | 平移相机 |
| Ctrl + 滚轮 | 相机缩放 (Distance) |
| R + 左键拖拽 | 旋转贴花 (RotationDeg) |

**相机**：
- 轨道相机（OrbitCamera）：围绕模型中心旋转
- 参数：Yaw, Pitch, Distance, PanOffset, Target
- ViewMatrix = LookAt(eye, target + pan, up)
- ProjMatrix = PerspectiveFov(π/4, aspect, 0.01, 100)

#### 纹理映射显示

3D 窗口中的模型使用合成后的纹理作为 diffuse 贴图：
1. `PreviewService` 合成完成后，将 BGRA 像素数据同时创建一个 DX11 Texture2D
2. 绑定到 Pixel Shader 的 diffuse 纹理槽
3. 用户在 3D 窗口中可以直接看到贴花在模型上的效果
4. 贴花参数变化 → 重新合成 → 更新纹理 → 3D 窗口自动刷新

#### 与现有系统集成

**数据流向**：
```
ModelEditorWindow (3D 交互)
  → 更新 DecalLayer.UvCenter / UvScale / RotationDeg
  → 触发 PreviewService.MarkDirty()
  → CpuUvComposite() 重新合成
  → TextureSwapService 游戏内实时更新
  → 同时更新 3D 窗口的 diffuse 纹理
```

**与 MainWindow 的关系**：
- 两个窗口编辑同一个 DecalLayer 数据
- MainWindow 的 2D 画布和 ModelEditorWindow 的 3D 视图实时同步
- 在任一窗口修改参数，另一个窗口立即反映

### 新增依赖

| 包 | 版本 | 用途 |
|---|---|---|
| SharpDX | 4.2.0 | DX11 核心封装 |
| SharpDX.Direct3D11 | 4.2.0 | Device, Texture2D, RenderTarget 等 |
| SharpDX.D3DCompiler | 4.2.0 | HLSL 编译 |
| SharpDX.DXGI | 4.2.0 | Format, SwapChain 类型 |
| SharpDX.Mathematics | 4.2.0 | Vector3, Matrix 等数学类型 |

注：VFXEditor-CN 已验证这些包与 Dalamud 环境兼容。

## 分阶段实施计划

### Phase 1: 贴花半切预处理
- DecalLayer 新增 ClipMode 枚举和属性
- 合成管线中添加裁剪逻辑
- MainWindow 属性面板添加裁剪下拉框
- 配置序列化/反序列化

### Phase 2: DX11 3D 渲染框架
- 引入 SharpDX 依赖
- 实现 DxRenderer（RenderTarget、DepthStencil、状态管理）
- 实现 ShaderManager（Blinn-Phong HLSL 编译）
- 实现 MeshBuffer（MeshData → GPU 缓冲）
- 实现 OrbitCamera
- 实现 ModelEditorWindow（显示模型 + 相机交互）
- 将合成纹理映射到模型 diffuse

### Phase 3: 射线拾取与 3D 交互
- 实现 RayPicker（Möller–Trumbore 求交 + UV 插值）
- ModelEditorWindow 添加点击放置、拖拽移动、滚轮缩放、R+拖拽旋转
- 双向同步：3D 编辑 ↔ 2D 画布 ↔ 游戏内预览
- 贴花高亮显示（3D 窗口中标记当前贴花位置）

## Future Work: 缩裹投影（方案 B）

当前的 UV 定位模式（方案 A）在模型弯曲处（肩膀、关节等）会因 UV 展开不均匀产生贴花畸变。未来可升级为真正的缩裹投影：

### 核心思路

不再将贴花作为 UV 空间的矩形处理，而是：

1. **定义投影体**：以命中点为中心，沿表面法线定义一个投影圆柱/锥体
2. **收集覆盖面片**：找到投影体内的所有三角面
3. **逐面片变形采样**：对每个三角面，将其 3D 空间坐标映射到贴花纹理坐标，采样贴花像素
4. **在 UV 空间写入**：将采样结果写回各三角面对应的 UV 纹理区域

### 技术要点

- **切线空间构建**：需要在命中点构建 TBN（Tangent/Bitangent/Normal）矩阵确定贴花朝向
- **UV 接缝处理**：覆盖 UV 岛边界的三角面需要特殊处理，避免采样错误
- **合成管线重写**：从「逐像素查询哪些贴花覆盖」变为「逐三角面投影采样」
- **性能**：逐面片处理比逐像素更重，可能需要 GPU 加速（Compute Shader）

### 预估工作量

约为方案 A 的 3-4 倍，建议在方案 A 稳定运行后再评估是否需要。
