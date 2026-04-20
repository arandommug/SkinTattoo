# 贴花素材库（Resource Library）实现计划

> 计划日期：2026-04-19
> 灵感来源：[TextureOverlayer](https://github.com/Glorou/TextureOverlayer) 的 `CacheService`（作者 KarouKarou，Discord 沟通后确认其本意是"图层素材快速切换"，非性能优化）
> 目标：为 SkinTattoo 增加跨项目复用的"贴花素材库"，配合一个 flyout/侧栏面板让用户在添加/替换图层时一键挑选历史素材，同时解决项目文件因源 PNG 移动而失效的问题。

---

## 一、动机

当前痛点：

1. `DecalLayer.ImagePath` 直接存绝对路径，每加一层都要走文件对话框（重复劳动）。
2. 路径脆弱：用户挪图、改名、换电脑后项目"红叉"，无法在 reload 时重建图层（参考 `project_persistence_dto` 记忆）。
3. 同一张 PNG 反复导入会被当成不同素材处理，缺少去重。

TextureOverlayer 已经验证 hash + copy 的方案能一次性解决这三件事，但其实现把素材塞在 Penumbra 目录下、错误处理较弱、UI 仅停留在"想做但没做"的 flyout 阶段。本计划将其思路本土化到 SkinTattoo 的代码风格与持久化模式。

---

## 二、目标与非目标

**目标**

- 跨项目持久的"我用过的贴花"列表，UI 面板可一键应用到当前 layer。
- 同一张图（按内容 hash）只在磁盘存一份。
- 项目文件中 layer 通过 hash 引用素材，源文件移动/删除不影响项目可重建。
- 不破坏现有 `DecalImageLoader` 的内存级解码缓存（性能与功能解耦）。

**非目标**

- 不做素材分类/标签/搜索（首版），只按"最近使用"排序。
- 不做云同步、跨用户共享。
- 不替代 PMP 导出 -- 二者是导出/编辑两条独立链路。
- 不引入新的图像格式或预处理（仍按 `DecalImageLoader` 现有解码路径）。

---

## 三、方案设计

### 3.1 存储位置

```
<pluginConfigDir>/library/
  index.json                 # hash -> {fileName, originalName, addedAt, lastUsedAt, useCount}
  blobs/<hash>.<ext>         # 实际素材（按内容 hash 命名，扩展名保留）
  thumbs/<hash>.png          # 64x64 缩略图（lazy 生成，非必需）
```

放在 `<pluginConfigDir>` 而不是 Penumbra 目录的原因：

- 与插件配置一起备份/迁移，逻辑归属清晰。
- 不污染 Penumbra mod 列表。
- 生命周期跟随插件（卸载时一并清理）。

### 3.2 哈希算法

采用 **xxHash3 (64-bit)**（已是 .NET 内置 `System.IO.Hashing.XxHash3`，无需新依赖）。

- 速度：xxHash3 ~= 6 GB/s，远快于 Blake3（~= 1.5 GB/s 单线程），素材库场景不需要密码学强度。
- 64-bit 空间对"个人贴花库"规模（量级 10^2-10^4）碰撞概率可忽略。
- 拒绝引入 `Blake3` 包以保持依赖最小化。

文件名格式：`<hash:x16>.<ext>`，例如 `7f3a9e2b4c1d8e0f.png`。

### 3.3 数据模型变更

**新增** `Core/LibraryEntry.cs`：

```csharp
public sealed class LibraryEntry
{
    public string Hash { get; set; } = "";       // hex string
    public string FileName { get; set; } = "";   // <hash>.<ext>
    public string OriginalName { get; set; } = "";
    public DateTime AddedAt { get; set; }
    public DateTime LastUsedAt { get; set; }
    public int UseCount { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}
```

**修改** `DecalLayer`（兼容现有项目）：

- 新增 `ImageHash`（string，可空）字段。
- 保留 `ImagePath`（运行时解析后填充，便于 `DecalImageLoader` 复用现有 path-based cache）。
- 加载顺序：`ImageHash != null` -> 解析为 library 路径；否则按 `ImagePath` 走老路径。
- 首次保存含 hash 的项目时，老 `ImagePath` 仍写出以便降级回滚。

**对应 `SavedLayer` DTO**（`DecalProject.Save/Load`）：

- 新增 `imageHash` 字段，序列化双写。
- 加载时：先尝试 hash 解析，失败 fallback 到 path 重导（沿用 `LibraryService.ImportFromPath`）。
- **务必同步更新** `DecalProject` 双向映射，避免重启后字段丢失（这正是 `project_persistence_dto` 提醒的坑）。

### 3.4 服务层

**新增** `Services/LibraryService.cs`：

```csharp
public interface ILibraryService
{
    IReadOnlyList<LibraryEntry> Entries { get; }   // 排序：lastUsedAt desc
    LibraryEntry ImportFromPath(string diskPath);  // 哈希 -> 命中返回旧条目；未命中复制 + 入库
    string? ResolveDiskPath(string hash);          // null = 库中已不存在
    void Touch(string hash);                       // UseCount++ / LastUsedAt = now
    void Remove(string hash);                      // 删除 blob + index 记录
    void Rebuild();                                // 扫描 blobs/，修复 index.json 漂移
}
```

写入 `index.json` 走 `FilesystemUtil.WriteAllTextSafe`（原子写）模式，避免崩溃损坏索引。

**修改** `DecalImageLoader.LoadImage`：无需改造，库返回的是磁盘路径，现有 `(path, mtime, size)` 内存 cache 自动生效。

### 3.5 UI 设计

**位置**：`MainWindow.LayerPanel.cs` 添加按钮"素材库"，点击弹出右侧 flyout（或 popup）。

**布局**：网格视图，每格 64x64 缩略图 + 原始文件名 hover 提示。底部按钮 `导入新素材` / `从当前 layer 导入`。

**交互**：

- 单击：替换当前选中 layer 的 image，保留所有几何/样式参数。
- 拖入新建：拖到图层列表 -> 走 `ImportFromPath` -> 创建新 layer。
- 右键素材：删除（提示"X 个图层正在使用"，确认后可级联设为 path-only 引用回退）。

**缩略图**：lazy 生成 -- 首次出现在视口时由 `LibraryService` 后台线程生成 64x64 PNG 写入 `thumbs/`，UI 用 `IDalamudTextureWrap` 绑定。

---

## 四、迁移与兼容

| 场景 | 处理 |
|---|---|
| 旧项目（只有 `ImagePath`） | 加载正常，保存时若文件存在自动入库并补 `ImageHash` |
| 库丢失 / 用户清理 | layer 落回 `ImagePath`；缺失则与现状一致显示 "missing" |
| 同一项目跨设备 | 依赖项目文件随 hash 自带元信息（首版不实现项目内嵌素材；后续可扩展为"项目伴随 zip" 或 "合并到 PMP 导出"） |
| 多版本 SkinTattoo 共享 library | `<pluginConfigDir>/library/` 为单实例独占；版本升级保留 |

---

## 五、分阶段实施

**Phase 1 -- MVP（约 1 个工作量单元）**

- `LibraryService` + `LibraryEntry` + `index.json` 读写
- `DecalLayer.ImageHash` + `SavedLayer` 双写
- 简单的 ListBox 风格素材库面板（无缩略图，仅文件名 + 尺寸）
- `ImportFromPath` 替代当前的"打开文件对话框"路径

**Phase 2 -- UX 打磨**

- 缩略图 + 网格视图
- 拖拽支持、右键菜单、级联删除提示
- "最近使用" 排序与置顶常用

**Phase 3 -- 进阶（可选）**

- 项目导出时自动把引用素材打进 PMP/项目伴随 zip（解决跨设备问题）
- 标签 / 搜索（如果素材数量真的涨到需要）

---

## 六、风险与权衡

1. **磁盘占用** -- 用户可能积累大量素材；提供"清理未引用"工具。
2. **hash 冲突** -- xxHash3 64-bit 在万级规模可忽略，但仍应在写入时校验 `Length + 首块字节` 做二次确认。
3. **库目录损坏** -- `Rebuild()` 扫盘重建索引兜底；`index.json` 走原子写。
4. **与 PMP 导出的关系** -- 完全正交：导出仍走 `CompositeForExport`，库只影响"编辑期素材引用"。

---

## 七、待决策项

- [ ] 缩略图格式：PNG（透明）vs JPG（小但丢 alpha）---- 倾向 PNG。
- [ ] 删除素材时是否级联清理使用它的 layer 的 `ImageHash`，还是仅断开引用让其降级为 path-only。
- [ ] 是否在项目"另存为"时自动把引用素材的 hash 列入项目元信息（便于跨设备时提示用户缺失）。
