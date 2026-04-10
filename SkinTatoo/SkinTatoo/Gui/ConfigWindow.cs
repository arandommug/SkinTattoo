using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace SkinTatoo.Gui;

public class ConfigWindow : Window
{
    private readonly Configuration config;

    private static readonly int[] ResolutionOptions = [512, 1024, 2048, 4096];
    private static readonly string[] ResolutionNames = ["512", "1024", "2048", "4096"];

    private int pendingSwapInterval;
    private bool draggingSwapInterval;

    private const int SwapIntervalMin = 33;
    private const int SwapIntervalMax = 500;
    private const int SwapIntervalDefault = 150;

    public ConfigWindow(Configuration config)
        : base("SkinTatoo 设置###SkinTatooConfig")
    {
        this.config = config;

        Flags = ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoScrollbar;
        Size = new Vector2(380, 460);
    }

    public override void Draw()
    {
        const float labelW = 90f;

        // ── HTTP Server ──
        DrawSectionHeader("HTTP 调试服务器");
        ImGui.AlignTextToFramePadding(); ImGui.Text("端口号"); ImGui.SameLine(labelW);
        ImGui.SetNextItemWidth(120);
        var port = config.HttpPort;
        if (ImGui.InputInt("##port", ref port, 1, 100))
        {
            if (port is >= 1024 and <= 65535)
            {
                config.HttpPort = port;
                config.Save();
            }
        }

        ImGui.Spacing();

        // ── Texture ──
        DrawSectionHeader("贴图分辨率");
        ImGui.AlignTextToFramePadding(); ImGui.Text("分辨率"); ImGui.SameLine(labelW);
        ImGui.SetNextItemWidth(120);
        var resIdx = Array.IndexOf(ResolutionOptions, config.TextureResolution);
        if (resIdx < 0) resIdx = 1;
        if (ImGui.Combo("##res", ref resIdx, ResolutionNames, ResolutionNames.Length))
        {
            config.TextureResolution = ResolutionOptions[resIdx];
            config.Save();
        }

        ImGui.Spacing();

        // ── Swap Interval ──
        DrawSectionHeader("游戏贴图刷新");

        if (!draggingSwapInterval)
            pendingSwapInterval = Math.Clamp(config.GameSwapIntervalMs, SwapIntervalMin, SwapIntervalMax);

        ImGui.AlignTextToFramePadding(); ImGui.Text("刷新间隔"); ImGui.SameLine(labelW);
        ImGui.SetNextItemWidth(160);
        ImGui.SliderInt("##SwapInt", ref pendingSwapInterval, SwapIntervalMin, SwapIntervalMax, "%d ms");
        draggingSwapInterval = ImGui.IsItemActive();
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            config.GameSwapIntervalMs = pendingSwapInterval;
            config.Save();
            draggingSwapInterval = false;
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("默认"))
        {
            pendingSwapInterval = SwapIntervalDefault;
            config.GameSwapIntervalMs = SwapIntervalDefault;
            config.Save();
            draggingSwapInterval = false;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip($"恢复默认值 ({SwapIntervalDefault}ms)");

        ImGui.TextDisabled("拖动时游戏贴图最小刷新间隔。数值越小越实时。");
        ImGui.TextDisabled("松手后才应用。3D 编辑器预览不受此限制。");

        ImGui.Spacing();

        // ── UV Wireframe ──
        DrawSectionHeader("UV 网格线框");

        var uvAA = config.UvWireframeAntiAlias;
        if (ImGui.Checkbox("抗锯齿##uvAA", ref uvAA))
        {
            config.UvWireframeAntiAlias = uvAA;
            config.Save();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("关闭可显著提升线框渲染性能");

        var uvCull = config.UvWireframeCulling;
        if (ImGui.Checkbox("视口外剔除##uvCull", ref uvCull))
        {
            config.UvWireframeCulling = uvCull;
            config.Save();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("跳过完全在画布可见区域之外的三角形");

        var uvDedup = config.UvWireframeDedup;
        if (ImGui.Checkbox("共享边去重##uvDedup", ref uvDedup))
        {
            config.UvWireframeDedup = uvDedup;
            config.Save();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("相邻三角形的共享边只画一次，减少约 50%% 绘制量\n会增加少量 CPU 开销");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(1, 0.8f, 0.3f, 1),
            "修改端口或分辨率后需重启插件生效。");
    }

    private static void DrawSectionHeader(string text)
    {
        ImGui.TextColored(new Vector4(0.7f, 0.85f, 1f, 1f), text);
        ImGui.Separator();
    }
}
