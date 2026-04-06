using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
public class ModExportService : IDisposable
{
    private readonly PreviewService previewService;
    private readonly PenumbraBridge penumbra;
    private readonly INotificationManager notifications;
    private readonly IPluginLog log;

    /// <summary>
    /// Persistent path for the .pmp built when installing into Penumbra. Penumbra's
    /// InstallMod IPC queues extraction on a background thread, so the file must
    /// outlive the IPC call. Cleaned on Dispose (plugin unload / game shutdown).
    /// </summary>
    private readonly string installPmpPath;

    public ModExportService(
        PreviewService previewService,
        PenumbraBridge penumbra,
        INotificationManager notifications,
        IPluginLog log,
        string installTempDir)
    {
        this.previewService = previewService;
        this.penumbra = penumbra;
        this.notifications = notifications;
        this.log = log;

        Directory.CreateDirectory(installTempDir);
        installPmpPath = Path.Combine(installTempDir, "install_pending.pmp");

        // Clean up any leftover from a previous session that crashed mid-install
        TryDeleteInstallPmp();
    }

    public void Dispose()
    {
        // Penumbra has long finished extracting by plugin unload time.
        TryDeleteInstallPmp();
    }

    private void TryDeleteInstallPmp()
    {
        try
        {
            if (File.Exists(installPmpPath))
            {
                File.Delete(installPmpPath);
                DebugServer.AppendLog($"[ModExport] Cleaned install pmp: {installPmpPath}");
            }
        }
        catch (Exception ex)
        {
            DebugServer.AppendLog($"[ModExport] Cleanup failed: {ex.Message}");
        }
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

        bool anyVisible = options.SelectedGroups
            .Any(g => g.Layers.Any(l => l.IsVisible && !string.IsNullOrEmpty(l.ImagePath)));
        if (!anyVisible)
            return "选中的图层组没有可见图层";

        if (options.Target == ExportTarget.InstallToPenumbra && !penumbra.IsAvailable)
            return "Penumbra 未运行";

        if (options.Target == ExportTarget.LocalPmp && string.IsNullOrWhiteSpace(options.OutputPmpPath))
            return "请选择导出文件路径";

        return null;
    }

    /// <summary>
    /// Build the mod package and (optionally) hand it to Penumbra. Synchronous —
    /// callers should invoke from a background thread (see ModExportWindow).
    /// </summary>
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

            // installPmpPath is persistent (cleaned on Dispose) so it outlives
            // Penumbra's async InstallMod extraction.
            var pmpPath = options.Target == ExportTarget.LocalPmp
                ? options.OutputPmpPath!
                : installPmpPath;

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
            // Staging dir always cleaned. installPmpPath is intentionally NOT deleted —
            // see installPmpPath field comment.
            try { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true); }
            catch (Exception ex) { DebugServer.AppendLog($"[ModExport] Staging cleanup failed: {ex.Message}"); }
        }
    }
}
