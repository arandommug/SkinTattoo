# Mod 导出功能实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 给 SkinTatoo 加上"导出 Mod"功能 — 把当前 DecalProject 中勾选的 group 烘焙成一个独立的 Penumbra `.pmp` 包，可保存到本地或直接调 IPC 安装到 Penumbra。

**Architecture:** 复用 `PreviewService` 的合成路径（新增 `internal CompositeForExport` 入口），把合成结果写到 staging 目录，再用 `PmpPackageWriter` 打成 zip，最后由 `ModExportService` 协调本地保存或 `InstallMod` IPC。导出过程跑在后台 Task，不污染 PreviewService 的运行时状态（GPU swap、cache、emissive hook）。完成后通过 Dalamud 的 `INotificationManager` 弹出系统通知（成功 / 失败）。

**Tech Stack:** C# 12, .NET 9 (Dalamud SDK 14), `System.IO.Compression.ZipArchive`, `System.Text.Json`, Penumbra.Api IPC（`InstallMod.V5`）, Dalamud `INotificationManager` (`Dalamud.Interface.ImGuiNotification`)。

**Spec 引用:** `docs/superpowers/specs/2026-04-07-mod-export-design.md`

---

## 文件结构总览

| 文件 | 操作 | 责任 |
|---|---|---|
| `SkinTatoo/Configuration.cs` | 修改 | 新增 `DefaultAuthor` / `DefaultVersion` / `LastExportDir` 字段 |
| `SkinTatoo/Core/ModExportOptions.cs` | 新建 | 导出参数纯数据类 + `ExportTarget` 枚举 |
| `SkinTatoo/Services/PreviewService.cs` | 修改 | 新增 `internal CompositeForExport(group, stagingDir)`（不污染状态） |
| `SkinTatoo/Services/PmpPackageWriter.cs` | 新建 | 静态工具：staging 目录 + meta JSON + default_mod JSON → `.pmp` zip |
| `SkinTatoo/Services/ModExportService.cs` | 新建 | 导出主流程协调器，完成后弹 Dalamud 通知 |
| `SkinTatoo/Interop/PenumbraBridge.cs` | 修改 | 新增 `InstallMod(pmpPath)` IPC 封装 |
| `SkinTatoo/Gui/ModExportWindow.cs` | 新建 | ImGui 弹窗，二态切换"本地"/"Penumbra" |
| `SkinTatoo/Gui/MainWindow.cs` | 修改 | 工具栏新增两个按钮触发 ModExportWindow |
| `SkinTatoo/Http/DebugServer.cs` | 修改 | 新增 `POST /api/export` 端点供集成测试 |
| `SkinTatoo/Plugin.cs` | 修改 | DI 注册 INotificationManager / ModExportService / ModExportWindow，并处理 Dispose |

---

## Task 1: Configuration 新增字段

**Files:**
- Modify: `SkinTatoo/SkinTatoo/Configuration.cs:57-78`

- [ ] **Step 1: 添加三个字段并升 Version**

把 `Configuration` 类改成：

```csharp
[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 3;
    public int HttpPort { get; set; } = 14780;
    public int TextureResolution { get; set; } = 1024;

    public List<SavedTargetGroup> TargetGroups { get; set; } = [];
    public int SelectedGroupIndex { get; set; } = -1;

    public bool MainWindowOpen { get; set; }
    public bool DebugWindowOpen { get; set; }
    public bool ModelEditorWindowOpen { get; set; }
    public string? LastImageDir { get; set; }
    public bool AutoPreview { get; set; }
    public bool UseGpuSwap { get; set; } = true;

    // Mod export defaults
    public string DefaultAuthor { get; set; } = "";
    public string DefaultVersion { get; set; } = "1.0";
    public string? LastExportDir { get; set; }

    [NonSerialized]
    private IDalamudPluginInterface? pluginInterface;

    public void Initialize(IDalamudPluginInterface pi) => pluginInterface = pi;
    public void Save() => pluginInterface?.SavePluginConfig(this);
}
```

- [ ] **Step 2: 编译验证**

Run: `cd /c/Users/Shiro/Desktop/FF14Plugins/SkinTatoo && dotnet build -c Release`
Expected: `Build succeeded`，0 错 0 警

- [ ] **Step 3: 提交**

```bash
cd /c/Users/Shiro/Desktop/FF14Plugins/SkinTatoo
git add SkinTatoo/SkinTatoo/Configuration.cs
git commit -m "feat(config): 添加 mod 导出默认值字段"
```

---

## Task 2: ModExportOptions 数据类

**Files:**
- Create: `SkinTatoo/SkinTatoo/Core/ModExportOptions.cs`

- [ ] **Step 1: 创建文件**

```csharp
using System.Collections.Generic;

namespace SkinTatoo.Core;

public enum ExportTarget
{
    LocalPmp,
    InstallToPenumbra,
}

public class ModExportOptions
{
    public string ModName { get; set; } = "";
    public string Author { get; set; } = "";
    public string Version { get; set; } = "1.0";
    public string Description { get; set; } = "";
    public List<TargetGroup> SelectedGroups { get; set; } = [];
    public ExportTarget Target { get; set; }
    public string? OutputPmpPath { get; set; }
}

public class ModExportResult
{
    public bool Success { get; set; }
    public string? PmpPath { get; set; }
    public string Message { get; set; } = "";
    public int SuccessGroups { get; set; }
    public int SkippedGroups { get; set; }
}
```

- [ ] **Step 2: 编译验证**

Run: `cd /c/Users/Shiro/Desktop/FF14Plugins/SkinTatoo && dotnet build -c Release`
Expected: 编译通过

- [ ] **Step 3: 提交**

```bash
git add SkinTatoo/SkinTatoo/Core/ModExportOptions.cs
git commit -m "feat(core): 添加 ModExportOptions 数据类"
```

---

## Task 3: PreviewService 新增 CompositeForExport 入口

**背景：** 现有 `ProcessGroup`（PreviewService.cs:606-657）的合成结果是写到 `outputDir/preview_*.tex`，并且会修改 `compositeResults` / `previewMtrlDiskPaths` / `emissiveOffsets` 等内部状态。导出需要一个**纯函数式**入口：相同的合成逻辑，输出到 staging 目录的 game-path 镜像，不动任何状态。

**Files:**
- Modify: `SkinTatoo/SkinTatoo/Services/PreviewService.cs`（在 `ResetSwapState` 后、`CheckCanSwapInPlace` 前插入新方法）

- [ ] **Step 1: 在 PreviewService 末尾的"Private: shared helpers"段之前插入 CompositeForExport**

定位插入点：`PreviewService.cs:507` 之后（`ResetSwapState` 结束后）。新增以下方法：

```csharp
    /// <summary>
    /// Composite a group's textures into the given staging directory using
    /// game-path mirrored layout, only including visible layers. Does NOT mutate
    /// any PreviewService runtime state (GPU swap, caches, hook targets).
    /// Returns gamePath → relative disk path (forward slashes) for default_mod.json.
    /// </summary>
    internal Dictionary<string, string> CompositeForExport(TargetGroup group, string stagingDir)
    {
        var redirects = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrEmpty(group.DiffuseGamePath))
            return redirects;

        // Build a temporary list of visible layers (skip hidden / null-image)
        var visibleLayers = new List<DecalLayer>();
        foreach (var l in group.Layers)
        {
            if (!l.IsVisible || string.IsNullOrEmpty(l.ImagePath)) continue;
            visibleLayers.Add(l);
        }
        if (visibleLayers.Count == 0)
            return redirects;

        var baseTex = LoadBaseTexture(group);
        int w = baseTex.Width, h = baseTex.Height;

        // Diffuse composite — write to staging/<gamePath>
        var diffResult = CpuUvComposite(visibleLayers, baseTex.Data, w, h);
        if (diffResult != null)
        {
            var diffOut = WriteStagingTex(stagingDir, group.DiffuseGamePath!, diffResult, w, h);
            redirects[group.DiffuseGamePath!] = diffOut;
            DebugServer.AppendLog($"[ModExport] Diffuse → {group.DiffuseGamePath}");
        }

        // Emissive: only if there are visible emissive layers + mtrl path is known
        bool hasEmissive = false;
        foreach (var l in visibleLayers)
            if (l.AffectsEmissive) { hasEmissive = true; break; }

        if (hasEmissive && !string.IsNullOrEmpty(group.MtrlGamePath))
        {
            var emissiveColor = GetCombinedEmissiveColor(visibleLayers);
            var mtrlSource = group.OrigMtrlDiskPath ?? group.MtrlDiskPath ?? group.MtrlGamePath!;
            var mtrlOut = StagingPathFor(stagingDir, group.MtrlGamePath!);
            Directory.CreateDirectory(Path.GetDirectoryName(mtrlOut)!);
            if (TryBuildEmissiveMtrl(mtrlSource, mtrlOut, emissiveColor, out _))
            {
                redirects[group.MtrlGamePath!] = ToForwardSlash(group.MtrlGamePath!);
                DebugServer.AppendLog($"[ModExport] Mtrl → {group.MtrlGamePath}");
            }

            // Emissive normal map (alpha mask)
            if (!string.IsNullOrEmpty(group.NormGamePath) && !string.IsNullOrEmpty(group.NormDiskPath))
            {
                var normSource = group.OrigNormDiskPath ?? group.NormDiskPath!;
                var normResult = CompositeEmissiveNorm(visibleLayers, normSource, w, h);
                if (normResult != null)
                {
                    var normOut = WriteStagingTex(stagingDir, group.NormGamePath!, normResult, w, h);
                    redirects[group.NormGamePath!] = normOut;
                    DebugServer.AppendLog($"[ModExport] Norm (emissive alpha) → {group.NormGamePath}");
                }
            }
        }

        return redirects;
    }

    /// <summary>Map a game path to a staging-rooted disk path (mirrors game tree).</summary>
    private static string StagingPathFor(string stagingDir, string gamePath)
        => Path.Combine(stagingDir, gamePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Convert any path to forward-slash form for default_mod.json.</summary>
    private static string ToForwardSlash(string p) => p.Replace('\\', '/');

    /// <summary>Write a composited RGBA buffer as .tex into staging at the game-path mirror.</summary>
    private string WriteStagingTex(string stagingDir, string gamePath, byte[] rgba, int w, int h)
    {
        var diskPath = StagingPathFor(stagingDir, gamePath);
        Directory.CreateDirectory(Path.GetDirectoryName(diskPath)!);
        WriteBgraTexFile(diskPath, rgba, w, h);
        return ToForwardSlash(gamePath);
    }
```

**关键约束（自检表）：**
- ❌ 不写 `compositeResults`、`previewMtrlDiskPaths`、`emissiveOffsets`、`previewDiskPaths`、`initializedRedirects`、`lastEmissiveColors`
- ❌ 不调 `Interlocked.Increment(ref compositeVersion)`
- ❌ 不调 `penumbra.SetTextureRedirects` 或 `penumbra.RedrawPlayer`
- ❌ 不调 `emissiveHook.SetTargetByPath` / `ClearTargets`
- ✅ 复用现有 `LoadBaseTexture` / `CpuUvComposite` / `CompositeEmissiveNorm` / `TryBuildEmissiveMtrl` / `WriteBgraTexFile` / `GetCombinedEmissiveColor`
- ✅ 只对 `IsVisible == true` 的图层合成

注意：`LoadBaseTexture` 会写 `baseTextureCache`，但这个字典只是个性能优化（缓存的是原始游戏纹理，导出和预览读到的内容一致），写入它不影响导出与预览的隔离性，可以接受。

- [ ] **Step 2: 编译验证**

Run: `cd /c/Users/Shiro/Desktop/FF14Plugins/SkinTatoo && dotnet build -c Release`
Expected: 编译通过。如果报"`GetCombinedEmissiveColor` 是 `private static`"无法访问 — 它本身在同一个类里所以应该没问题。

- [ ] **Step 3: 提交**

```bash
git add SkinTatoo/SkinTatoo/Services/PreviewService.cs
git commit -m "feat(preview): 新增 CompositeForExport 入口供 mod 导出复用"
```

---

## Task 4: PmpPackageWriter 静态工具

**背景：** Penumbra 的 `InstallMod(pmpPath)` IPC 走 `HandleRegularArchive`，要求 zip 内有一个 `meta.json`（带 `Name` 字段），其余文件按 zip entry 路径解压到 mod 目录。我们额外把 `default_mod.json` 也放在 zip 根，Penumbra 加载时会自动读取。

**Files:**
- Create: `SkinTatoo/SkinTatoo/Services/PmpPackageWriter.cs`

- [ ] **Step 1: 创建文件**

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using SkinTatoo.Core;
using SkinTatoo.Http;

namespace SkinTatoo.Services;

/// <summary>
/// Packs a staging directory + meta into a Penumbra .pmp (zip) file.
/// .pmp layout: meta.json + default_mod.json at root, plus all staging files
/// at their game-path mirrored locations.
/// </summary>
public static class PmpPackageWriter
{
    /// <summary>
    /// Build a .pmp zip at outputPmpPath. Overwrites if exists.
    /// </summary>
    /// <param name="stagingDir">Directory containing files at game-path mirrored layout.</param>
    /// <param name="options">Mod metadata (name, author, etc.).</param>
    /// <param name="redirects">gamePath → relative disk path (forward slashes) inside the mod.</param>
    /// <param name="outputPmpPath">Destination .pmp file path.</param>
    public static void Pack(string stagingDir, ModExportOptions options,
        Dictionary<string, string> redirects, string outputPmpPath)
    {
        var metaJson = BuildMetaJson(options);
        var defaultModJson = BuildDefaultModJson(redirects);

        if (File.Exists(outputPmpPath))
            File.Delete(outputPmpPath);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPmpPath)!);

        using var fs = new FileStream(outputPmpPath, FileMode.CreateNew);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

        WriteEntry(zip, "meta.json", metaJson);
        WriteEntry(zip, "default_mod.json", defaultModJson);

        if (Directory.Exists(stagingDir))
        {
            var rootLen = stagingDir.Length + 1;
            foreach (var file in Directory.EnumerateFiles(stagingDir, "*", SearchOption.AllDirectories))
            {
                var rel = file.Substring(rootLen).Replace('\\', '/');
                var entry = zip.CreateEntry(rel, CompressionLevel.Fastest);
                using var es = entry.Open();
                using var rs = File.OpenRead(file);
                rs.CopyTo(es);
            }
        }

        DebugServer.AppendLog($"[PmpPackageWriter] Packed → {outputPmpPath}");
    }

    private static void WriteEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
        using var s = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        s.Write(bytes, 0, bytes.Length);
    }

    /// <summary>Build meta.json (FileVersion 3, matches Penumbra ModMeta.cs:11-65).</summary>
    private static string BuildMetaJson(ModExportOptions options)
    {
        var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("FileVersion", 3);
            w.WriteString("Name", options.ModName);
            w.WriteString("Author", options.Author);
            w.WriteString("Description", options.Description);
            w.WriteString("Image", "");
            w.WriteString("Version", options.Version);
            w.WriteString("Website", "");
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>
    /// Build default_mod.json (matches Penumbra ModSaveGroup.Save + SubMod.WriteModContainer:82-117).
    /// Both keys (game paths) and values (relative paths) use forward slashes.
    /// </summary>
    private static string BuildDefaultModJson(Dictionary<string, string> redirects)
    {
        var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("Version", 0);
            w.WritePropertyName("Files");
            w.WriteStartObject();
            foreach (var (gamePath, relPath) in redirects)
            {
                w.WriteString(gamePath.Replace('\\', '/'), relPath.Replace('\\', '/'));
            }
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
```

- [ ] **Step 2: 编译验证**

Run: `cd /c/Users/Shiro/Desktop/FF14Plugins/SkinTatoo && dotnet build -c Release`
Expected: 编译通过

- [ ] **Step 3: 提交**

```bash
git add SkinTatoo/SkinTatoo/Services/PmpPackageWriter.cs
git commit -m "feat(services): 添加 PmpPackageWriter 工具"
```

---

## Task 5: PenumbraBridge 新增 InstallMod IPC

**Files:**
- Modify: `SkinTatoo/SkinTatoo/Interop/PenumbraBridge.cs`

- [ ] **Step 1: 添加 IPC 字段和方法**

在 `PenumbraBridge` 中：

1. 在文件顶部 using 区，已经有 `Penumbra.Api.IpcSubscribers` 引用，无需改动。

2. 在私有字段区（约 `PenumbraBridge.cs:15-21`，现有 `getPlayerResourceTrees` 后）添加：

```csharp
    private readonly Penumbra.Api.IpcSubscribers.InstallMod installMod;
```

3. 在构造函数 `PenumbraBridge(...)` 中（约第 31-37 行的初始化区）添加：

```csharp
        installMod = new Penumbra.Api.IpcSubscribers.InstallMod(pluginInterface);
```

4. 在 `ClearRedirect` / `RedrawPlayer` 等方法附近添加新方法（建议放在 `RedrawPlayer` 后，约第 99 行后）：

```csharp
    /// <summary>
    /// Install a .pmp mod package into Penumbra. Returns the API error code.
    /// </summary>
    public PenumbraApiEc InstallMod(string pmpPath)
    {
        if (!IsAvailable) return PenumbraApiEc.SystemDisposed;
        try
        {
            var ec = installMod.Invoke(pmpPath);
            Http.DebugServer.AppendLog($"[PenumbraBridge] InstallMod({pmpPath}) → {ec}");
            return ec;
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to install mod");
            return PenumbraApiEc.UnknownError;
        }
    }
```

- [ ] **Step 2: 编译验证**

Run: `cd /c/Users/Shiro/Desktop/FF14Plugins/SkinTatoo && dotnet build -c Release`
Expected: 编译通过

- [ ] **Step 3: 提交**

```bash
git add SkinTatoo/SkinTatoo/Interop/PenumbraBridge.cs
git commit -m "feat(interop): 添加 InstallMod IPC 封装"
```

---

## Task 6: ModExportService 协调器

**Files:**
- Create: `SkinTatoo/SkinTatoo/Services/ModExportService.cs`

- [ ] **Step 1: 创建文件**

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Plugin.Services;
using Penumbra.Api.Enums;
using SkinTatoo.Core;
using SkinTatoo.Http;
using SkinTatoo.Interop;

namespace SkinTatoo.Services;

/// <summary>
/// Orchestrates exporting a DecalProject to a Penumbra .pmp mod package.
/// Runs on a background task; does not touch PreviewService runtime state.
/// </summary>
public class ModExportService
{
    private readonly PreviewService previewService;
    private readonly PenumbraBridge penumbra;
    private readonly INotificationManager notifications;
    private readonly IPluginLog log;

    public ModExportService(
        PreviewService previewService,
        PenumbraBridge penumbra,
        INotificationManager notifications,
        IPluginLog log)
    {
        this.previewService = previewService;
        this.penumbra = penumbra;
        this.notifications = notifications;
        this.log = log;
    }

    private void Notify(bool success, string title, string content)
    {
        try
        {
            notifications.AddNotification(new Notification
            {
                Title = title,
                Content = content,
                Type = success ? NotificationType.Success : NotificationType.Error,
            });
        }
        catch (Exception ex)
        {
            DebugServer.AppendLog($"[ModExport] Notification failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Validate options. Returns null on success, error message string on failure.
    /// </summary>
    public string? Validate(ModExportOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ModName))
            return "请输入 Mod 名称";
        if (options.SelectedGroups.Count == 0)
            return "请至少选择一个图层组";

        bool anyVisible = false;
        foreach (var g in options.SelectedGroups)
        {
            foreach (var l in g.Layers)
                if (l.IsVisible && !string.IsNullOrEmpty(l.ImagePath)) { anyVisible = true; break; }
            if (anyVisible) break;
        }
        if (!anyVisible)
            return "选中的图层组没有可见图层";

        if (options.Target == ExportTarget.InstallToPenumbra && !penumbra.IsAvailable)
            return "Penumbra 未运行";

        if (options.Target == ExportTarget.LocalPmp && string.IsNullOrWhiteSpace(options.OutputPmpPath))
            return "请选择导出文件路径";

        return null;
    }

    /// <summary>
    /// Run the export on a background thread. Returns a Task that completes with the result.
    /// </summary>
    public Task<ModExportResult> ExportAsync(ModExportOptions options)
        => Task.Run(() => Export(options));

    /// <summary>Synchronous export — call from a background thread.</summary>
    public ModExportResult Export(ModExportOptions options)
    {
        var err = Validate(options);
        if (err != null)
        {
            Notify(false, "导出失败", err);
            return new ModExportResult { Success = false, Message = err };
        }

        var stagingDir = Path.Combine(Path.GetTempPath(),
            $"SkinTatoo_Export_{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDir);
        DebugServer.AppendLog($"[ModExport] Staging: {stagingDir}");

        string? tempPmp = null;
        try
        {
            var allRedirects = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int success = 0, skipped = 0;

            foreach (var group in options.SelectedGroups)
            {
                try
                {
                    var groupRedirects = previewService.CompositeForExport(group, stagingDir);
                    if (groupRedirects.Count == 0)
                    {
                        skipped++;
                        DebugServer.AppendLog($"[ModExport] Skipped (no output): {group.Name}");
                        continue;
                    }
                    foreach (var (gp, rp) in groupRedirects)
                        allRedirects[gp] = rp;
                    success++;
                }
                catch (Exception ex)
                {
                    log.Error(ex, $"[ModExport] Group failed: {group.Name}");
                    DebugServer.AppendLog($"[ModExport] Group failed: {group.Name} — {ex.Message}");
                    skipped++;
                }
            }

            if (success == 0)
            {
                var msg = $"{skipped} 个 group 全部跳过";
                Notify(false, "导出失败", msg);
                return new ModExportResult
                {
                    Success = false,
                    Message = msg,
                    SkippedGroups = skipped,
                };
            }

            // Decide target pmp path
            string pmpPath;
            if (options.Target == ExportTarget.LocalPmp)
            {
                pmpPath = options.OutputPmpPath!;
            }
            else
            {
                tempPmp = Path.Combine(Path.GetTempPath(),
                    $"SkinTatoo_Install_{Guid.NewGuid():N}.pmp");
                pmpPath = tempPmp;
            }

            PmpPackageWriter.Pack(stagingDir, options, allRedirects, pmpPath);

            if (options.Target == ExportTarget.InstallToPenumbra)
            {
                var ec = penumbra.InstallMod(pmpPath);
                if (ec != PenumbraApiEc.Success)
                {
                    var failMsg = $"Penumbra 安装失败：{ec}";
                    Notify(false, "导出失败", failMsg);
                    return new ModExportResult
                    {
                        Success = false,
                        Message = failMsg,
                        SuccessGroups = success,
                        SkippedGroups = skipped,
                    };
                }
            }

            var summary = skipped > 0
                ? $"{success} 成功 / {skipped} 跳过"
                : $"{success} 个图层组";
            var notifyTitle = options.Target == ExportTarget.LocalPmp
                ? "导出成功"
                : "已安装到 Penumbra";
            var notifyContent = options.Target == ExportTarget.LocalPmp
                ? $"{options.ModName}：{summary}\n{pmpPath}"
                : $"{options.ModName}：{summary}";
            Notify(true, notifyTitle, notifyContent);

            return new ModExportResult
            {
                Success = true,
                PmpPath = options.Target == ExportTarget.LocalPmp ? pmpPath : null,
                Message = $"{notifyTitle}（{summary}）",
                SuccessGroups = success,
                SkippedGroups = skipped,
            };
        }
        catch (Exception ex)
        {
            log.Error(ex, "[ModExport] Export failed");
            var msg = $"导出异常：{ex.Message}";
            Notify(false, "导出失败", msg);
            return new ModExportResult { Success = false, Message = msg };
        }
        finally
        {
            // Always clean up staging
            try { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true); }
            catch (Exception ex) { DebugServer.AppendLog($"[ModExport] Staging cleanup failed: {ex.Message}"); }

            // Always delete temp pmp used for InstallToPenumbra
            if (tempPmp != null && File.Exists(tempPmp))
            {
                try { File.Delete(tempPmp); }
                catch (Exception ex) { DebugServer.AppendLog($"[ModExport] Temp pmp cleanup failed: {ex.Message}"); }
            }
        }
    }
}
```

- [ ] **Step 2: 编译验证**

Run: `cd /c/Users/Shiro/Desktop/FF14Plugins/SkinTatoo && dotnet build -c Release`
Expected: 编译通过

- [ ] **Step 3: 提交**

```bash
git add SkinTatoo/SkinTatoo/Services/ModExportService.cs
git commit -m "feat(services): 添加 ModExportService 协调器"
```

---

## Task 7: HTTP /api/export 端点

**Files:**
- Modify: `SkinTatoo/SkinTatoo/Http/DebugServer.cs`

- [ ] **Step 1: 在 ApiController 添加字段和构造参数**

定位到 `DebugServer.cs:91-119`（`ApiController` 类）。修改：

1. 在字段区添加：

```csharp
    private readonly Services.ModExportService _exportService;
```

2. 修改构造函数签名（增加 `ModExportService exportService` 参数）和体内赋值。**注意**：这同时需要修改 `DebugServer` 字段、构造函数，以及外部 `Plugin.cs` 创建 `DebugServer` 的地方。

修改 `DebugServer` 字段（约第 28 行后）：

```csharp
    private readonly Services.ModExportService _exportService;
```

修改 `DebugServer` 构造函数签名（约第 43-57 行）：

```csharp
    public DebugServer(
        Configuration config,
        DecalProject project,
        PenumbraBridge penumbra,
        PreviewService preview,
        IDataManager dataManager,
        Services.ModExportService exportService,
        TextureSwapService? textureSwap = null)
    {
        _config   = config;
        _project  = project;
        _penumbra = penumbra;
        _preview  = preview;
        _dataManager = dataManager;
        _exportService = exportService;
        _textureSwap = textureSwap;
    }
```

修改 `Start()` 中创建 `ApiController` 的 lambda（约第 67-68 行）：

```csharp
            .WithWebApi("/api", m => m.WithController(() =>
                new ApiController(_project, _penumbra, _preview, _config, _dataManager, _exportService, _textureSwap)));
```

修改 `ApiController` 构造函数（约第 105-119 行）和字段：

```csharp
    private readonly Services.ModExportService _exportService;

    public ApiController(
        DecalProject project,
        PenumbraBridge penumbra,
        PreviewService preview,
        Configuration config,
        IDataManager dataManager,
        Services.ModExportService exportService,
        TextureSwapService? textureSwap = null)
    {
        _project  = project;
        _penumbra = penumbra;
        _preview  = preview;
        _config   = config;
        _dataManager = dataManager;
        _exportService = exportService;
        _textureSwap = textureSwap;
    }
```

3. 在 `ApiController` 末尾添加新端点（在最后一个 `[Route]` 后）：

```csharp
    [Route(HttpVerbs.Post, "/export")]
    public async Task<object> PostExport()
    {
        var body = await HttpContext.GetRequestBodyAsStringAsync();
        if (string.IsNullOrWhiteSpace(body))
        {
            HttpContext.Response.StatusCode = 400;
            return new { error = "Empty body" };
        }

        // Body schema: { name, author?, version?, description?, target: "local"|"penumbra",
        //                outputPath?, groupIndices: [int...] }
        try
        {
            var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            var options = new Core.ModExportOptions
            {
                ModName = root.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "",
                Author = root.TryGetProperty("author", out var a) ? (a.GetString() ?? "") : "",
                Version = root.TryGetProperty("version", out var v) ? (v.GetString() ?? "1.0") : "1.0",
                Description = root.TryGetProperty("description", out var d) ? (d.GetString() ?? "") : "",
                Target = (root.TryGetProperty("target", out var t) && t.GetString() == "penumbra")
                    ? Core.ExportTarget.InstallToPenumbra
                    : Core.ExportTarget.LocalPmp,
                OutputPmpPath = root.TryGetProperty("outputPath", out var op) ? op.GetString() : null,
            };

            if (root.TryGetProperty("groupIndices", out var giArr) && giArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in giArr.EnumerateArray())
                {
                    var idx = el.GetInt32();
                    if (idx >= 0 && idx < _project.Groups.Count)
                        options.SelectedGroups.Add(_project.Groups[idx]);
                }
            }
            else
            {
                // Default: all groups with layers
                foreach (var g in _project.Groups)
                    if (g.Layers.Count > 0)
                        options.SelectedGroups.Add(g);
            }

            var result = _exportService.Export(options);
            HttpContext.Response.StatusCode = result.Success ? 200 : 500;
            return new
            {
                success = result.Success,
                message = result.Message,
                pmpPath = result.PmpPath,
                successGroups = result.SuccessGroups,
                skippedGroups = result.SkippedGroups,
            };
        }
        catch (Exception ex)
        {
            HttpContext.Response.StatusCode = 500;
            return new { error = ex.Message };
        }
    }
```

- [ ] **Step 2: 编译验证（暂时会失败 — 因为 Plugin.cs 还没传 exportService）**

Run: `cd /c/Users/Shiro/Desktop/FF14Plugins/SkinTatoo && dotnet build -c Release`
Expected: 编译失败 — `Plugin.cs` 调用 `new DebugServer(...)` 缺参数。这是预期的，下个 Task 修。

- [ ] **Step 3: 暂不提交**，等 Task 8 一起。

---

## Task 8: Plugin.cs 注册 ModExportService + 修复 DebugServer 构造

**Files:**
- Modify: `SkinTatoo/SkinTatoo/Plugin.cs`

- [ ] **Step 1: 注入 INotificationManager PluginService**

在 `Plugin.cs` 顶部 `[PluginService]` 字段区（约第 18-22 行）追加：

```csharp
    [PluginService] public static Dalamud.Interface.ImGuiNotification.INotificationManager NotificationManager { get; private set; } = null!;
```

- [ ] **Step 2: 添加 ModExportService 字段**

在 `Plugin` 类的字段区（约第 36 行 `previewService` 后）添加：

```csharp
    private readonly ModExportService modExportService;
```

- [ ] **Step 3: 在构造函数中初始化（顺序在 previewService 之后、debugServer 之前）**

在 `Plugin.cs` 约第 86 行（`previewService = new PreviewService(...)` 之后）添加：

```csharp
        modExportService = new ModExportService(previewService, penumbra, NotificationManager, log);
```

- [ ] **Step 4: 修改 DebugServer 创建**

把约第 92 行的：

```csharp
        debugServer = new DebugServer(config, project, penumbra, previewService, dataManager, textureSwap);
```

改为：

```csharp
        debugServer = new DebugServer(config, project, penumbra, previewService, dataManager, modExportService, textureSwap);
```

- [ ] **Step 5: 编译验证**

Run: `cd /c/Users/Shiro/Desktop/FF14Plugins/SkinTatoo && dotnet build -c Release`
Expected: 编译通过

- [ ] **Step 6: 提交（与 Task 7 合并）**

```bash
git add SkinTatoo/SkinTatoo/Http/DebugServer.cs SkinTatoo/SkinTatoo/Plugin.cs
git commit -m "feat(http): 添加 POST /api/export 端点 + 注册 ModExportService"
```

---

## Task 9: ModExportWindow ImGui 弹窗

**背景：** 单个窗口承担两种"打开"语义（`OpenAs(target)`），通过窗口标题和内部隐藏/显示路径输入框来切换。

**Files:**
- Create: `SkinTatoo/SkinTatoo/Gui/ModExportWindow.cs`

- [ ] **Step 1: 创建文件**

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using SkinTatoo.Core;
using SkinTatoo.Services;

namespace SkinTatoo.Gui;

public class ModExportWindow : Window
{
    private readonly DecalProject project;
    private readonly ModExportService exportService;
    private readonly Configuration config;
    private readonly FileDialogManager fileDialog = new();

    private ExportTarget currentTarget = ExportTarget.LocalPmp;
    private string modName = "";
    private string author = "";
    private string version = "1.0";
    private string description = "";
    private string outputPath = "";
    private readonly HashSet<int> selectedGroupIndices = new();

    private bool exporting;

    public ModExportWindow(DecalProject project, ModExportService exportService, Configuration config)
        : base("导出 Mod###SkinTatooExport",
               ImGuiWindowFlags.NoCollapse)
    {
        this.project = project;
        this.exportService = exportService;
        this.config = config;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(440, 400),
            MaximumSize = new Vector2(640, 800),
        };
        IsOpen = false;
    }

    /// <summary>Open the dialog with a target preset. Resets fields from config defaults.</summary>
    public void OpenAs(ExportTarget target)
    {
        currentTarget = target;
        WindowName = target == ExportTarget.LocalPmp
            ? "导出 Mod 到本地###SkinTatooExport"
            : "安装 Mod 到 Penumbra###SkinTatooExport";

        author = config.DefaultAuthor;
        version = string.IsNullOrEmpty(config.DefaultVersion) ? "1.0" : config.DefaultVersion;

        if (string.IsNullOrEmpty(modName))
            modName = "SkinTatoo Decal";

        // Default-select all groups with layers
        selectedGroupIndices.Clear();
        for (int i = 0; i < project.Groups.Count; i++)
            if (project.Groups[i].Layers.Count > 0)
                selectedGroupIndices.Add(i);

        if (string.IsNullOrEmpty(description))
            description = $"由 SkinTatoo 生成 — 包含 {selectedGroupIndices.Count} 个图层组";

        IsOpen = true;
    }

    public override void Draw()
    {
        fileDialog.Draw();

        ImGui.Text("Mod 名称");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##modName", ref modName, 128);

        ImGui.Text("作者");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("##author", ref author, 64))
        {
            config.DefaultAuthor = author;
            config.Save();
        }

        ImGui.Text("版本");
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputText("##version", ref version, 16))
        {
            config.DefaultVersion = version;
            config.Save();
        }

        ImGui.Text("描述");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextMultiline("##desc", ref description, 1024, new Vector2(-1, 60));

        if (currentTarget == ExportTarget.LocalPmp)
        {
            ImGui.Text("导出位置");
            ImGui.SetNextItemWidth(-90);
            ImGui.InputText("##outPath", ref outputPath, 512);
            ImGui.SameLine();
            if (ImGui.Button("浏览…"))
            {
                var startDir = !string.IsNullOrEmpty(config.LastExportDir) && Directory.Exists(config.LastExportDir)
                    ? config.LastExportDir
                    : Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                var defaultName = SafeFileName(modName) + ".pmp";
                fileDialog.SaveFileDialog("保存 Mod", ".pmp", defaultName, ".pmp", (ok, path) =>
                {
                    if (ok && !string.IsNullOrEmpty(path))
                    {
                        outputPath = path;
                        config.LastExportDir = Path.GetDirectoryName(path);
                        config.Save();
                    }
                }, startDir);
            }
        }

        ImGui.Separator();
        ImGui.Text("包含的图层组");
        if (project.Groups.Count == 0)
        {
            ImGui.TextColored(new Vector4(1, 0.6f, 0, 1), "（项目中没有图层组）");
        }
        else
        {
            using (ImRaii.Child("##groups", new Vector2(-1, 120), true))
            {
                for (int i = 0; i < project.Groups.Count; i++)
                {
                    var g = project.Groups[i];
                    bool sel = selectedGroupIndices.Contains(i);
                    var label = $"{g.Name} ({g.Layers.Count} 图层)##g{i}";
                    if (ImGui.Checkbox(label, ref sel))
                    {
                        if (sel) selectedGroupIndices.Add(i);
                        else selectedGroupIndices.Remove(i);
                    }
                }
            }
        }

        ImGui.Separator();

        var canExport = !exporting && !string.IsNullOrWhiteSpace(modName) && selectedGroupIndices.Count > 0
            && (currentTarget != ExportTarget.LocalPmp || !string.IsNullOrWhiteSpace(outputPath));

        using (ImRaii.Disabled(!canExport))
        {
            var btnLabel = currentTarget == ExportTarget.LocalPmp ? "导出到本地" : "安装到 Penumbra";
            if (exporting) btnLabel = "导出中…";
            if (ImGui.Button(btnLabel, new Vector2(160, 0)))
            {
                StartExport();
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("关闭"))
            IsOpen = false;
    }

    private void StartExport()
    {
        var options = new ModExportOptions
        {
            ModName = modName.Trim(),
            Author = author.Trim(),
            Version = version.Trim(),
            Description = description,
            Target = currentTarget,
            OutputPmpPath = currentTarget == ExportTarget.LocalPmp ? outputPath : null,
        };
        foreach (var i in selectedGroupIndices)
            if (i >= 0 && i < project.Groups.Count)
                options.SelectedGroups.Add(project.Groups[i]);

        exporting = true;
        // Result is delivered via Dalamud Notification (see ModExportService.Notify).
        // Window can be closed by user immediately; the notification persists.
        Task.Run(() =>
        {
            try { exportService.Export(options); }
            finally { exporting = false; }
        });
    }

    private static string SafeFileName(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = s.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (Array.IndexOf(invalid, chars[i]) >= 0) chars[i] = '_';
        var result = new string(chars).Trim();
        return string.IsNullOrEmpty(result) ? "SkinTatoo" : result;
    }
}
```

- [ ] **Step 2: 编译验证**

Run: `cd /c/Users/Shiro/Desktop/FF14Plugins/SkinTatoo && dotnet build -c Release`
Expected: 编译通过

- [ ] **Step 3: 提交**

```bash
git add SkinTatoo/SkinTatoo/Gui/ModExportWindow.cs
git commit -m "feat(gui): 添加 ModExportWindow 弹窗"
```

---

## Task 10: MainWindow 工具栏接入按钮

**Files:**
- Modify: `SkinTatoo/SkinTatoo/Gui/MainWindow.cs`

- [ ] **Step 1: 添加 ModExportWindow 引用字段**

在 `MainWindow.cs` 字段区（约第 75-78 行 `DebugWindowRef`/`ConfigWindowRef` 旁）添加：

```csharp
    public ModExportWindow? ModExportWindowRef { get; set; }
```

- [ ] **Step 2: 在 DrawToolbar 中追加两个按钮**

定位到 `DrawToolbar`（`MainWindow.cs:214`）。在"还原贴图"按钮（`IconButton(3, FontAwesomeIcon.Eraser)`，约第 247-256 行）之后插入：

```csharp
        ImGui.SameLine();
        using (ImRaii.Disabled(project.Groups.Count == 0))
        {
            if (ImGuiComponents.IconButton(7, FontAwesomeIcon.FileExport))
                ModExportWindowRef?.OpenAs(Core.ExportTarget.LocalPmp);
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("导出 Mod 到本地 (.pmp)");

        ImGui.SameLine();
        using (ImRaii.Disabled(project.Groups.Count == 0 || !penumbra.IsAvailable))
        {
            if (ImGuiComponents.IconButton(8, FontAwesomeIcon.Download))
                ModExportWindowRef?.OpenAs(Core.ExportTarget.InstallToPenumbra);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(penumbra.IsAvailable ? "安装 Mod 到 Penumbra" : "Penumbra 未运行");
```

- [ ] **Step 3: 编译验证**

Run: `cd /c/Users/Shiro/Desktop/FF14Plugins/SkinTatoo && dotnet build -c Release`
Expected: 编译通过

- [ ] **Step 4: 提交**

```bash
git add SkinTatoo/SkinTatoo/Gui/MainWindow.cs
git commit -m "feat(gui): MainWindow 工具栏添加导出/安装按钮"
```

---

## Task 11: Plugin.cs 注册 ModExportWindow

**Files:**
- Modify: `SkinTatoo/SkinTatoo/Plugin.cs`

- [ ] **Step 1: 添加字段**

在 `Plugin.cs` 字段区（约第 49 行 `modelEditorWindow` 后）添加：

```csharp
    private readonly ModExportWindow modExportWindow;
```

- [ ] **Step 2: 构造函数中创建并注册**

在约第 102 行 `mainWindow.ModelEditorWindowRef = modelEditorWindow;` 之后插入：

```csharp
        modExportWindow = new ModExportWindow(project, modExportService, config);
        mainWindow.ModExportWindowRef = modExportWindow;
```

并在约第 112 行 `windowSystem.AddWindow(modelEditorWindow);` 之后插入：

```csharp
        windowSystem.AddWindow(modExportWindow);
```

- [ ] **Step 3: 编译验证**

Run: `cd /c/Users/Shiro/Desktop/FF14Plugins/SkinTatoo && dotnet build -c Release`
Expected: 编译通过

- [ ] **Step 4: 提交**

```bash
git add SkinTatoo/SkinTatoo/Plugin.cs
git commit -m "feat(plugin): 注册 ModExportWindow"
```

---

## Task 12: HTTP 集成测试

游戏内启动插件后，用 curl 测试 `/api/export` 端点验证 .pmp 产物正确。

**Files:** 无修改。

- [ ] **Step 1: 启动游戏 + Penumbra + SkinTatoo**

打开 SkinTatoo 主窗口，确保至少有一个 group 含可见 diffuse 图层（用现有项目数据即可）。

- [ ] **Step 2: curl 调用导出**

```bash
curl -X POST http://localhost:14780/api/export \
  -H "Content-Type: application/json" \
  -d '{
    "name": "TestExport",
    "author": "test",
    "version": "0.1",
    "description": "integration test",
    "target": "local",
    "outputPath": "C:/Users/Shiro/Desktop/test_export.pmp"
  }'
```

Expected:
```json
{"success":true,"message":"导出成功（1 个 group）","pmpPath":"C:/Users/Shiro/Desktop/test_export.pmp","successGroups":1,"skippedGroups":0}
```

- [ ] **Step 3: 验证 .pmp 内容**

```bash
cd /c/Users/Shiro/Desktop && unzip -l test_export.pmp
```

Expected:
- `meta.json` 在根
- `default_mod.json` 在根
- 至少一个 `chara/.../*.tex` 在游戏路径镜像位置

```bash
unzip -p test_export.pmp meta.json
```

Expected: JSON 含 `"FileVersion": 3`, `"Name": "TestExport"`, `"Author": "test"`, `"Version": "0.1"`

```bash
unzip -p test_export.pmp default_mod.json
```

Expected: JSON 含 `"Version": 0`, `"Files"` 字典，键是游戏路径，值是相同的相对路径

- [ ] **Step 4: 验证 emissive 烘焙（如果当前 group 有 emissive 图层）**

```bash
unzip -l test_export.pmp | grep -i mtrl
```

如果存在 mtrl，说明 emissive 路径有效。具体烘焙颜色的正确性留给端到端测试通过游戏视觉对照。

- [ ] **Step 5: 清理**

```bash
rm /c/Users/Shiro/Desktop/test_export.pmp
```

- [ ] **Step 6: 提交**（这一步无代码变化，跳过 commit）

---

## Task 13: 游戏内端到端验证

- [ ] **Step 1: 流程 A — 导出到本地 → Penumbra 手动导入**

1. SkinTatoo 主窗口 → 工具栏点"导出 Mod 到本地"按钮（FileExport 图标）
2. 弹窗出现，标题为"导出 Mod 到本地"
3. 填名称、点"浏览…"选个保存位置
4. 勾选所有想要的 group
5. 点"导出到本地"按钮
6. 状态栏显示绿色"导出成功（X 个 group）"
7. 关闭对话框
8. 在 Penumbra 主窗口，把刚导出的 .pmp 拖入或用"导入"按钮导入
9. 启用导入的 mod，`/penumbra` 重绘角色
10. 视觉对照：角色身上的纹身/贴花和 SkinTatoo 实时预览看到的应该一致
11. 如果含 emissive 图层：发光颜色应被烘焙到 .mtrl，启用即亮（无需 SkinTatoo 运行）

- [ ] **Step 2: 流程 B — 安装到 Penumbra**

1. SkinTatoo 主窗口 → 工具栏点"安装 Mod 到 Penumbra"按钮（Download 图标）
2. 弹窗出现，标题为"安装 Mod 到 Penumbra"，无"导出位置"输入框
3. 填名称、勾选 group
4. 点"安装到 Penumbra"按钮
5. 状态栏显示绿色"导出成功"
6. 切到 Penumbra 主窗口，应能在 mod 列表中看到刚才那个 mod 名
7. 启用、重绘 → 视觉对照

- [ ] **Step 3: 错误用例 — 未勾选 group**

1. 打开导出对话框
2. 取消所有 group 勾选
3. "导出到本地"按钮变灰（因为 `canExport` 为 false）—— 验证 disabled 状态正确
4. 即使绕过 disabled，调 `/api/export` 时不传 `groupIndices` 默认全选；传 `[]` 应返回 `请至少选择一个图层组` 错误

```bash
curl -X POST http://localhost:14780/api/export -H "Content-Type: application/json" \
  -d '{"name":"x","target":"local","outputPath":"C:/temp/x.pmp","groupIndices":[]}'
```

Expected: `{"success":false,"message":"请至少选择一个图层组",...}`

- [ ] **Step 4: 错误用例 — Mod 名为空**

```bash
curl -X POST http://localhost:14780/api/export -H "Content-Type: application/json" \
  -d '{"name":"","target":"local","outputPath":"C:/temp/x.pmp"}'
```

Expected: `{"success":false,"message":"请输入 Mod 名称",...}`

- [ ] **Step 5: 错误用例 — Penumbra 未运行**

1. 在 Penumbra 主窗口禁用 Penumbra（或临时关掉）
2. 在 SkinTatoo 主窗口点"安装 Mod 到 Penumbra"按钮
3. 按钮应变灰（因为 `IconButton` disabled by `!penumbra.IsAvailable`），tooltip 显示"Penumbra 未运行"

或 curl：
```bash
curl -X POST http://localhost:14780/api/export -H "Content-Type: application/json" \
  -d '{"name":"x","target":"penumbra"}'
```
Expected: `{"success":false,"message":"Penumbra 未运行",...}`

- [ ] **Step 6: 隔离性验证 — 导出期间预览正常**

1. 触发一次较重的导出（多个 group）
2. 同时调整任意图层的 emissive 颜色滑块
3. 颜色变化应正常生效，与导出运行无冲突
4. 导出完成后再调一次实时预览，应仍然正常工作（验证 ModExportService 没污染 PreviewService 状态）

- [ ] **Step 7: 检查 staging 残留**

```bash
ls /c/Users/Shiro/AppData/Local/Temp/ | grep SkinTatoo
```

Expected: 无 `SkinTatoo_Export_*` 或 `SkinTatoo_Install_*` 残留目录/文件（应已被 finally 清理）

- [ ] **Step 8: 提交端到端验证完成标记（无代码变化）**

如果发现 bug，回到对应 Task 修复并新建 commit。

---

## Task 14: 文档更新

**Files:**
- Modify: `SkinTatoo/CLAUDE.md`（HTTP API 部分追加新端点）

- [ ] **Step 1: 在 HTTP API 列表追加 /api/export**

定位到 CLAUDE.md HTTP 调试 API 列表（约第 67-81 行），在 `GET /api/log` 后追加：

```markdown
- `POST /api/export` — 导出 Mod（body: `{name, author?, version?, description?, target: "local"|"penumbra", outputPath?, groupIndices?: [int]}`）
```

- [ ] **Step 2: 在 PreviewService 流程描述部分追加导出说明**

在 CLAUDE.md "核心管线流程"一节末尾追加：

```markdown
### Mod 导出
- `Services/ModExportService.cs` 协调导出
- 复用 `PreviewService.CompositeForExport` 入口（仅 visible 图层、不污染运行时状态）
- `Services/PmpPackageWriter.cs` 打包 staging 目录为 `.pmp` zip（meta.json + default_mod.json + 游戏路径镜像）
- 两条路径：本地保存 / `PenumbraBridge.InstallMod` IPC
- 详细设计：`docs/superpowers/specs/2026-04-07-mod-export-design.md`
```

- [ ] **Step 3: 提交**

```bash
git add SkinTatoo/CLAUDE.md
git commit -m "docs: 记录 mod 导出 API 和管线"
```

---

## 自检清单（Plan 作者执行）

### Spec 覆盖

| Spec 决策 | 实现 Task |
|---|---|
| 用户在对话框勾选 group | Task 9 (复选框列表) |
| Emissive 烘焙进 .mtrl | Task 3 (复用 `TryBuildEmissiveMtrl` + `GetCombinedEmissiveColor`) |
| 两个独立按钮 | Task 10 (两个 IconButton) |
| 本地 `.pmp` 单文件 | Task 4 (PmpPackageWriter) |
| `InstallMod` IPC | Task 5 + Task 6 |
| 默认值存 Configuration、对话框可覆盖 | Task 1 + Task 9 |
| 仅 visible 图层 | Task 3 (visibleLayers 过滤) |
| 后台 Task | Task 9 (`Task.Run` in `StartExport`) |
| 完成提示（持久、不依赖窗口） | Task 6 (`INotificationManager.AddNotification`) |
| Staging 临时目录 + finally 清理 | Task 6 (try/finally) |
| Penumbra 不可用预检 | Task 6 (Validate) + Task 10 (按钮 disable) |
| 隔离性（不污染 PreviewService 状态） | Task 3 (约束清单) |
| HTTP 端点 | Task 7 |
| 游戏内端到端 | Task 13 |

### 类型/方法名一致性
- ✅ `ModExportOptions.SelectedGroups` 在 Task 2/6/9 一致
- ✅ `ModExportResult.SuccessGroups` 在 Task 2/6/7/9 一致
- ✅ `ExportTarget.LocalPmp` / `InstallToPenumbra` 在 Task 2/6/9/10 一致
- ✅ `CompositeForExport(group, stagingDir)` 签名在 Task 3/6 一致
- ✅ `PmpPackageWriter.Pack(stagingDir, options, redirects, outputPmpPath)` 签名在 Task 4/6 一致
- ✅ `PenumbraBridge.InstallMod(pmpPath)` 在 Task 5/6 一致
- ✅ `ModExportService(previewService, penumbra, notifications, log)` 构造在 Task 6/8 一致
- ✅ `ModExportWindowRef.OpenAs(target)` 在 Task 9/10 一致
- ✅ `Configuration.DefaultAuthor/DefaultVersion/LastExportDir` 在 Task 1/9 一致
- ✅ `INotificationManager` 注入：Plugin.cs 提供 PluginService → ModExportService 构造参数

### 占位符扫描
无 TBD / TODO / "implement later" / 模糊代码描述。所有代码片段为完整可粘贴形式。

---
