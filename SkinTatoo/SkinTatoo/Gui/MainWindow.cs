using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Penumbra.Api.Enums;
using Penumbra.Api.Helpers;
using SkinTatoo.Core;
using SkinTatoo.Http;
using SkinTatoo.Interop;
using SkinTatoo.Services;

namespace SkinTatoo.Gui;

public class MainWindow : Window, IDisposable
{
    private readonly DecalProject project;
    private readonly PreviewService previewService;
    private readonly PenumbraBridge penumbra;
    private readonly Configuration config;
    private readonly ITextureProvider textureProvider;
    private readonly FileDialogManager fileDialog = new();

    private string imagePathBuf = string.Empty;
    private int lastEditedLayerIndex = -1;
    private int layerCounter;
    private bool scaleLocked = true;

    // Resource browser state
    private Dictionary<ushort, ResourceTreeDto>? cachedTrees;
    private bool resourceWindowOpen;
    private bool treeExpandRequest;
    private bool treeCollapseRequest;

    // Canvas state
    private float canvasZoom = 1.0f;
    private Vector2 canvasPan = Vector2.Zero;
    private bool canvasDraggingLayer;
    private bool canvasPanning;
    private bool canvasScalingLayer;
    private bool showHelpWindow;

    // Auto-preview debounce
    private bool previewDirty;
    private DateTime lastDirtyTime = DateTime.MinValue;
    private const double PreviewDebounceSec = 0.8;

    private static readonly string[] BlendModeNames = ["正常", "正片叠底", "叠加", "柔光"];
    private static readonly BlendMode[] BlendModeValues = [BlendMode.Normal, BlendMode.Multiply, BlendMode.Overlay, BlendMode.SoftLight];
    private static readonly string[] EmissiveMaskNames = ["均匀", "中心扩散", "边缘光环", "边缘描边"];

    public DebugWindow? DebugWindowRef { get; set; }
    public event Action? OnSaveRequested;

    public MainWindow(
        DecalProject project,
        PreviewService previewService,
        PenumbraBridge penumbra,
        Configuration config,
        ITextureProvider textureProvider)
        : base("SkinTatoo 纹身编辑器###SkinTatooMain",
               ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.project = project;
        this.previewService = previewService;
        this.penumbra = penumbra;
        this.config = config;
        this.textureProvider = textureProvider;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(900, 550),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void Draw()
    {
        DrawToolbar();
        ImGui.Separator();

        var totalWidth = ImGui.GetContentRegionAvail().X;
        var totalHeight = ImGui.GetContentRegionAvail().Y;
        DrawThreePanelLayout(totalWidth, totalHeight);

        fileDialog.Draw();

        if (resourceWindowOpen)
            DrawResourceWindow();

        if (showHelpWindow)
            DrawHelpWindow();
    }

    private void DrawHelpWindow()
    {
        ImGui.SetNextWindowSize(new Vector2(340, 320), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("操作说明###SkinTatooHelp", ref showHelpWindow))
        {
            ImGui.End();
            return;
        }

        ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f), "画布操作");
        ImGui.Separator();
        ImGui.BulletText("滚轮: 缩放画布");
        ImGui.BulletText("中键拖动: 平移画布");
        ImGui.BulletText("左键拖动: 移动贴花位置");
        ImGui.BulletText("左键点空白: 选择贴花图层");
        ImGui.BulletText("右键拖动: 缩放贴花");

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f), "修饰键");
        ImGui.Separator();
        ImGui.BulletText("Shift + 左键拖动: 锁定 X 轴");
        ImGui.BulletText("Ctrl + 左键拖动: 锁定 Y 轴");
        ImGui.BulletText("Alt + 右键拖动: 旋转贴花");

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f), "图层");
        ImGui.Separator();
        ImGui.BulletText("Ctrl+Shift + 删除按钮: 删除图层/目标");

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f), "参数面板");
        ImGui.Separator();
        ImGui.BulletText("悬浮数值上滚轮: 微调参数");

        ImGui.End();
    }

    private void DrawToolbar()
    {
        if (ImGuiComponents.IconButton(FontAwesomeIcon.Save))
            OnSaveRequested?.Invoke();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("保存项目");

        ImGui.SameLine();
        if (ImGuiComponents.IconButton(1, FontAwesomeIcon.Bug))
        {
            if (DebugWindowRef != null)
                DebugWindowRef.IsOpen = !DebugWindowRef.IsOpen;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("调试窗口");

        ImGui.SameLine();
        using (ImRaii.Disabled(project.SelectedLayer == null))
        {
            if (ImGuiComponents.IconButton(2, FontAwesomeIcon.Undo))
                ResetSelectedLayer();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("重置当前图层参数");

        ImGui.SameLine();
        using (ImRaii.Disabled(project.Groups.Count == 0))
        {
            if (ImGuiComponents.IconButton(3, FontAwesomeIcon.Eraser))
            {
                penumbra.ClearRedirect();
                penumbra.RedrawPlayer();
                previewService.ClearTextureCache();
                DebugServer.AppendLog("[MainWindow] Restored original textures");
            }
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("还原贴图 — 清除 Penumbra 重定向");

        ImGui.SameLine();
        ImGui.Spacing();
        ImGui.SameLine();
        var penColor = penumbra.IsAvailable ? new Vector4(0, 1, 0.3f, 1) : new Vector4(1, 0.8f, 0, 1);
        ImGui.TextColored(penColor, penumbra.IsAvailable ? "● Penumbra" : "Penumbra [x]");
    }

    private void DrawThreePanelLayout(float totalWidth, float height)
    {
        var leftWidth = totalWidth * 0.20f;
        var rightWidth = 260f;
        var centerWidth = totalWidth - leftWidth - rightWidth - ImGui.GetStyle().ItemSpacing.X * 2;

        using (var left = ImRaii.Child("##LeftPanel", new Vector2(leftWidth, height), true))
        {
            if (left.Success) DrawLayerPanel();
        }

        ImGui.SameLine();

        using (var center = ImRaii.Child("##CenterPanel", new Vector2(centerWidth, height), true,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (center.Success) DrawCanvas();
        }

        ImGui.SameLine();

        using (var right = ImRaii.Child("##RightPanel", new Vector2(rightWidth, height), true))
        {
            if (right.Success) DrawParameterPanel();
        }
    }

    // ── Left Panel: Tree structure ───────────────────────────────────────────

    private void DrawLayerPanel()
    {
        // Group-level buttons
        if (ImGuiComponents.IconButton(10, FontAwesomeIcon.Plus))
        {
            resourceWindowOpen = true;
            RefreshResources();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("添加投影目标");

        ImGui.SameLine();
        var io = ImGui.GetIO();
        var canDeleteGroup = project.SelectedGroupIndex >= 0 && io.KeyCtrl && io.KeyShift;
        using (ImRaii.Disabled(!canDeleteGroup))
        {
            if (ImGuiComponents.IconButton(11, FontAwesomeIcon.Trash))
            {
                project.RemoveGroup(project.SelectedGroupIndex);
                MarkPreviewDirty();
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("按住 Ctrl+Shift 删除投影目标");

        ImGui.SameLine();
        ImGui.TextDisabled($"({project.Groups.Count})");

        ImGui.Separator();

        using var listChild = ImRaii.Child("##GroupTree", new Vector2(-1, -1), false);
        if (!listChild.Success) return;

        for (var gi = 0; gi < project.Groups.Count; gi++)
        {
            ImGui.PushID(gi);
            DrawGroupNode(gi);
            ImGui.PopID();
        }

        if (project.Groups.Count == 0)
            ImGui.TextDisabled("点击 + 添加投影目标");
    }

    private void DrawGroupNode(int gi)
    {
        var group = project.Groups[gi];
        var isGroupSelected = project.SelectedGroupIndex == gi;

        var flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.SpanAvailWidth;
        if (isGroupSelected && group.SelectedLayerIndex < 0) flags |= ImGuiTreeNodeFlags.Selected;
        if (group.IsExpanded) flags |= ImGuiTreeNodeFlags.DefaultOpen;

        var open = ImGui.TreeNodeEx($"{group.Name}##grp", flags);
        group.IsExpanded = open;

        if (ImGui.IsItemClicked(ImGuiMouseButton.Left) && !ImGui.IsItemToggledOpen())
        {
            project.SelectedGroupIndex = gi;
            group.SelectedLayerIndex = -1;
        }

        // Tooltip with target info
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            if (!string.IsNullOrEmpty(group.DiffuseGamePath))
                ImGui.Text($"贴图: {group.DiffuseGamePath}");
            if (!string.IsNullOrEmpty(group.NormGamePath))
                ImGui.Text($"法线: {group.NormGamePath}");
            if (!string.IsNullOrEmpty(group.MtrlGamePath))
                ImGui.Text($"材质: {group.MtrlGamePath}");
            ImGui.EndTooltip();
        }

        if (!open) return;

        // Layer buttons within group
        if (ImGuiComponents.IconButton(20, FontAwesomeIcon.Plus))
        {
            project.SelectedGroupIndex = gi;
            layerCounter++;
            group.AddLayer($"贴花 {layerCounter}");
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("添加图层");

        ImGui.SameLine();
        var io = ImGui.GetIO();
        var canDeleteLayer = isGroupSelected && group.SelectedLayerIndex >= 0
                             && io.KeyCtrl && io.KeyShift;
        using (ImRaii.Disabled(!canDeleteLayer))
        {
            if (ImGuiComponents.IconButton(21, FontAwesomeIcon.Trash))
                group.RemoveLayer(group.SelectedLayerIndex);
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("按住 Ctrl+Shift 删除图层");

        ImGui.SameLine();
        using (ImRaii.Disabled(!isGroupSelected || group.SelectedLayerIndex <= 0))
        {
            if (ImGuiComponents.IconButton(22, FontAwesomeIcon.ArrowUp))
            {
                group.MoveLayerUp(group.SelectedLayerIndex);
                SyncImagePathBuf();
            }
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(!isGroupSelected || group.SelectedLayerIndex < 0 || group.SelectedLayerIndex >= group.Layers.Count - 1))
        {
            if (ImGuiComponents.IconButton(23, FontAwesomeIcon.ArrowDown))
            {
                group.MoveLayerDown(group.SelectedLayerIndex);
                SyncImagePathBuf();
            }
        }

        ImGui.SameLine();
        ImGui.TextDisabled($"({group.Layers.Count})");

        // Layer list
        for (var li = 0; li < group.Layers.Count; li++)
        {
            var layer = group.Layers[li];
            ImGui.PushID(li + 1000);

            var visIcon = layer.IsVisible ? FontAwesomeIcon.Eye : FontAwesomeIcon.EyeSlash;
            var visColor = layer.IsVisible ? new Vector4(1, 1, 1, 1) : new Vector4(0.5f, 0.5f, 0.5f, 1);
            ImGui.PushStyleColor(ImGuiCol.Text, visColor);
            if (ImGuiComponents.IconButton(100 + li, visIcon))
                layer.IsVisible = !layer.IsVisible;
            ImGui.PopStyleColor();
            ImGui.SameLine();

            var isLayerSelected = isGroupSelected && group.SelectedLayerIndex == li;
            if (ImGui.Selectable(layer.Name, isLayerSelected))
            {
                project.SelectedGroupIndex = gi;
                group.SelectedLayerIndex = li;
                SyncImagePathBuf();
            }

            ImGui.PopID();
        }

        ImGui.TreePop();
    }

    // ── Center Panel: Interactive UV Canvas ──────────────────────────────────

    private void DrawCanvas()
    {
        var btnH = ImGui.GetFrameHeight();
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 80);
        ImGui.SliderFloat("##zoom", ref canvasZoom, 0.1f, 5.0f, $"{canvasZoom * 100:F0}%%");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("画布缩放 (滚轮)");
        ImGui.SameLine();
        if (ImGui.Button("适应", new Vector2(44, btnH)))
        {
            canvasZoom = 1.0f;
            canvasPan = Vector2.Zero;
        }
        ImGui.SameLine();
        if (ImGui.Button("?", new Vector2(btnH, btnH)))
            showHelpWindow = !showHelpWindow;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("操作说明");

        var avail = ImGui.GetContentRegionAvail();
        var canvasSize = new Vector2(avail.X, avail.Y);
        if (canvasSize.X < 10 || canvasSize.Y < 10) return;

        var canvasPos = ImGui.GetCursorScreenPos();
        var fitSize = MathF.Min(canvasSize.X, canvasSize.Y) * canvasZoom;
        var uvOrigin = canvasPos + (canvasSize - new Vector2(fitSize)) * 0.5f
                       - canvasPan * fitSize;

        ImGui.InvisibleButton("##Canvas", canvasSize);
        var isHovered = ImGui.IsItemHovered();
        var drawList = ImGui.GetWindowDrawList();

        drawList.PushClipRect(canvasPos, canvasPos + canvasSize, true);

        drawList.AddRectFilled(canvasPos, canvasPos + canvasSize,
            ImGui.GetColorU32(new Vector4(0.1f, 0.1f, 0.1f, 1f)));

        DrawCheckerboard(drawList, uvOrigin, fitSize);
        DrawBaseTexture(drawList, uvOrigin, fitSize);
        DrawLayerOverlays(drawList, uvOrigin, fitSize);

        drawList.AddRect(uvOrigin, uvOrigin + new Vector2(fitSize),
            ImGui.GetColorU32(new Vector4(0.4f, 0.4f, 0.4f, 1f)));

        DrawRulers(drawList, canvasPos, canvasSize, uvOrigin, fitSize);

        drawList.PopClipRect();

        if (isHovered)
            HandleCanvasInput(canvasPos, canvasSize, uvOrigin, fitSize);
    }

    private void DrawRulers(ImDrawListPtr drawList, Vector2 canvasPos, Vector2 canvasSize, Vector2 uvOrigin, float fitSize)
    {
        var tickColor = ImGui.GetColorU32(new Vector4(0.5f, 0.5f, 0.5f, 0.6f));
        var textColor = ImGui.GetColorU32(new Vector4(0.6f, 0.6f, 0.6f, 0.8f));
        var divisions = 10;

        for (var i = 0; i <= divisions; i++)
        {
            var t = i / (float)divisions;
            var label = $"{t:F1}";

            var hx = uvOrigin.X + t * fitSize;
            if (hx >= canvasPos.X && hx <= canvasPos.X + canvasSize.X)
            {
                var tickLen = (i % 5 == 0) ? 8f : 4f;
                drawList.AddLine(new Vector2(hx, uvOrigin.Y), new Vector2(hx, uvOrigin.Y - tickLen), tickColor);
                if (i % 5 == 0)
                    drawList.AddText(new Vector2(hx + 2, uvOrigin.Y - 16), textColor, label);
            }

            var vy = uvOrigin.Y + t * fitSize;
            if (vy >= canvasPos.Y && vy <= canvasPos.Y + canvasSize.Y)
            {
                var tickLen = (i % 5 == 0) ? 8f : 4f;
                drawList.AddLine(new Vector2(uvOrigin.X, vy), new Vector2(uvOrigin.X - tickLen, vy), tickColor);
                if (i % 5 == 0)
                    drawList.AddText(new Vector2(uvOrigin.X - 28, vy - 6), textColor, label);
            }
        }
    }

    private void DrawCheckerboard(ImDrawListPtr drawList, Vector2 origin, float size)
    {
        var checkerSize = 16f;
        var cols = (int)(size / checkerSize) + 1;
        var rows = cols;
        var darkColor = ImGui.GetColorU32(new Vector4(0.15f, 0.15f, 0.15f, 1f));
        var lightColor = ImGui.GetColorU32(new Vector4(0.2f, 0.2f, 0.2f, 1f));

        for (var y = 0; y < rows; y++)
        {
            for (var x = 0; x < cols; x++)
            {
                var pMin = origin + new Vector2(x * checkerSize, y * checkerSize);
                var pMax = pMin + new Vector2(checkerSize);
                pMin = Vector2.Max(pMin, origin);
                pMax = Vector2.Min(pMax, origin + new Vector2(size));
                if (pMin.X >= pMax.X || pMin.Y >= pMax.Y) continue;

                var color = ((x + y) % 2 == 0) ? darkColor : lightColor;
                drawList.AddRectFilled(pMin, pMax, color);
            }
        }
    }

    private void DrawBaseTexture(ImDrawListPtr drawList, Vector2 uvOrigin, float fitSize)
    {
        var group = project.SelectedGroup;
        if (group == null) return;

        var texDiskPath = group.DiffuseDiskPath;
        var texGamePath = group.DiffuseGamePath;
        if (string.IsNullOrEmpty(texDiskPath) && string.IsNullOrEmpty(texGamePath)) return;

        try
        {
            var shared = !string.IsNullOrEmpty(texDiskPath) && File.Exists(texDiskPath)
                ? textureProvider.GetFromFile(texDiskPath)
                : textureProvider.GetFromGame(texGamePath ?? "");
            var wrap = shared.GetWrapOrDefault();
            if (wrap != null)
            {
                drawList.AddImage(wrap.Handle,
                    uvOrigin, uvOrigin + new Vector2(fitSize),
                    Vector2.Zero, Vector2.One);
            }
        }
        catch { }
    }

    private void DrawLayerOverlays(ImDrawListPtr drawList, Vector2 uvOrigin, float fitSize)
    {
        var group = project.SelectedGroup;
        if (group == null) return;

        for (var i = 0; i < group.Layers.Count; i++)
        {
            var layer = group.Layers[i];
            if (!layer.IsVisible || string.IsNullOrEmpty(layer.ImagePath)) continue;

            var isSelected = group.SelectedLayerIndex == i;
            var center = layer.UvCenter;
            var scale = layer.UvScale;
            var pCenter = uvOrigin + center * fitSize;
            var pHalfSize = scale * fitSize * 0.5f;

            try
            {
                if (File.Exists(layer.ImagePath))
                {
                    var wrap = textureProvider.GetFromFile(layer.ImagePath).GetWrapOrDefault();
                    if (wrap != null)
                    {
                        var pMin = pCenter - pHalfSize;
                        var pMax = pCenter + pHalfSize;

                        if (MathF.Abs(layer.RotationDeg) < 0.1f)
                        {
                            var alpha = (uint)(layer.Opacity * 255) << 24 | 0x00FFFFFF;
                            drawList.AddImage(wrap.Handle, pMin, pMax,
                                Vector2.Zero, Vector2.One, alpha);
                        }
                        else
                        {
                            var rotRad = layer.RotationDeg * (MathF.PI / 180f);
                            var cos = MathF.Cos(rotRad);
                            var sin = MathF.Sin(rotRad);
                            Vector2 Rotate(Vector2 p) => new(
                                p.X * cos - p.Y * sin,
                                p.X * sin + p.Y * cos);

                            var tl = pCenter + Rotate(-pHalfSize);
                            var tr = pCenter + Rotate(new Vector2(pHalfSize.X, -pHalfSize.Y));
                            var br = pCenter + Rotate(pHalfSize);
                            var bl = pCenter + Rotate(new Vector2(-pHalfSize.X, pHalfSize.Y));

                            var alpha = (uint)(layer.Opacity * 255) << 24 | 0x00FFFFFF;
                            drawList.AddImageQuad(wrap.Handle,
                                tl, tr, br, bl,
                                new Vector2(0, 0), new Vector2(1, 0),
                                new Vector2(1, 1), new Vector2(0, 1),
                                alpha);
                        }
                    }
                }
            }
            catch { }

            // Bounding box
            var borderColor = isSelected
                ? ImGui.GetColorU32(new Vector4(1f, 0.8f, 0.2f, 1f))
                : ImGui.GetColorU32(new Vector4(0.6f, 0.6f, 0.6f, 0.5f));
            var thickness = isSelected ? 2f : 1f;

            if (MathF.Abs(layer.RotationDeg) < 0.1f)
            {
                drawList.AddRect(pCenter - pHalfSize, pCenter + pHalfSize, borderColor, 0, ImDrawFlags.None, thickness);
            }
            else
            {
                var rotRad = layer.RotationDeg * (MathF.PI / 180f);
                var cos = MathF.Cos(rotRad);
                var sin = MathF.Sin(rotRad);
                Vector2 Rotate(Vector2 p) => new(
                    p.X * cos - p.Y * sin,
                    p.X * sin + p.Y * cos);

                var tl = pCenter + Rotate(-pHalfSize);
                var tr = pCenter + Rotate(new Vector2(pHalfSize.X, -pHalfSize.Y));
                var br = pCenter + Rotate(pHalfSize);
                var bl = pCenter + Rotate(new Vector2(-pHalfSize.X, pHalfSize.Y));

                drawList.AddQuad(tl, tr, br, bl, borderColor, thickness);
            }

            if (isSelected)
            {
                var cross = 6f;
                drawList.AddLine(pCenter - new Vector2(cross, 0), pCenter + new Vector2(cross, 0), borderColor, 1f);
                drawList.AddLine(pCenter - new Vector2(0, cross), pCenter + new Vector2(0, cross), borderColor, 1f);
            }
        }
    }

    private void HandleCanvasInput(Vector2 canvasPos, Vector2 canvasSize, Vector2 uvOrigin, float fitSize)
    {
        var io = ImGui.GetIO();
        var mousePos = io.MousePos;
        var group = project.SelectedGroup;
        var selectedLayer = group?.SelectedLayer;
        var hasActiveLayer = selectedLayer != null && !string.IsNullOrEmpty(selectedLayer.ImagePath);

        if (MathF.Abs(io.MouseWheel) > 0.01f)
            canvasZoom = Math.Clamp(canvasZoom + io.MouseWheel * 0.15f * canvasZoom, 0.1f, 10f);

        if (ImGui.IsMouseDragging(ImGuiMouseButton.Middle))
        {
            canvasPanning = true;
            canvasPan -= io.MouseDelta / fitSize;
        }
        else
        {
            canvasPanning = false;
        }

        if (hasActiveLayer && group != null)
        {
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            {
                var pCenter = uvOrigin + selectedLayer!.UvCenter * fitSize;
                var pHalfSize = selectedLayer.UvScale * fitSize * 0.5f;
                canvasScalingLayer = mousePos.X >= pCenter.X - pHalfSize.X && mousePos.X <= pCenter.X + pHalfSize.X &&
                                    mousePos.Y >= pCenter.Y - pHalfSize.Y && mousePos.Y <= pCenter.Y + pHalfSize.Y;
            }

            if (canvasScalingLayer && ImGui.IsMouseDragging(ImGuiMouseButton.Right))
            {
                var delta = io.MouseDelta;

                if (io.KeyAlt)
                {
                    var rotDelta = -delta.Y * 0.5f;
                    selectedLayer!.RotationDeg = Math.Clamp(selectedLayer.RotationDeg + rotDelta, -180f, 180f);
                }
                else
                {
                    var scaleDelta = delta.X * 0.003f;
                    if (scaleLocked)
                    {
                        var s = Math.Clamp(selectedLayer!.UvScale.X + scaleDelta, 0.01f, 2f);
                        selectedLayer.UvScale = new Vector2(s, s);
                    }
                    else
                    {
                        selectedLayer!.UvScale = new Vector2(
                            Math.Clamp(selectedLayer.UvScale.X + scaleDelta, 0.01f, 2f),
                            Math.Clamp(selectedLayer.UvScale.Y + scaleDelta, 0.01f, 2f));
                    }
                }
                MarkPreviewDirty();
            }

            if (ImGui.IsMouseReleased(ImGuiMouseButton.Right))
                canvasScalingLayer = false;
        }

        if (!canvasPanning && !canvasScalingLayer && group != null)
        {
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                canvasDraggingLayer = false;
                if (hasActiveLayer)
                {
                    var pCenter = uvOrigin + selectedLayer!.UvCenter * fitSize;
                    var pHalfSize = selectedLayer.UvScale * fitSize * 0.5f;
                    canvasDraggingLayer = mousePos.X >= pCenter.X - pHalfSize.X && mousePos.X <= pCenter.X + pHalfSize.X &&
                                         mousePos.Y >= pCenter.Y - pHalfSize.Y && mousePos.Y <= pCenter.Y + pHalfSize.Y;
                }

                if (!canvasDraggingLayer)
                {
                    for (var i = group.Layers.Count - 1; i >= 0; i--)
                    {
                        var l = group.Layers[i];
                        if (!l.IsVisible || string.IsNullOrEmpty(l.ImagePath)) continue;
                        var lc = uvOrigin + l.UvCenter * fitSize;
                        var lh = l.UvScale * fitSize * 0.5f;
                        if (mousePos.X >= lc.X - lh.X && mousePos.X <= lc.X + lh.X &&
                            mousePos.Y >= lc.Y - lh.Y && mousePos.Y <= lc.Y + lh.Y)
                        {
                            group.SelectedLayerIndex = i;
                            SyncImagePathBuf();
                            canvasDraggingLayer = true;
                            break;
                        }
                    }
                }
            }

            if (canvasDraggingLayer && ImGui.IsMouseDragging(ImGuiMouseButton.Left) && group.SelectedLayer != null)
            {
                var layer = group.SelectedLayer;
                var delta = io.MouseDelta / fitSize;

                if (io.KeyShift) delta.X = 0;
                if (io.KeyCtrl) delta.Y = 0;

                layer.UvCenter = new Vector2(
                    Math.Clamp(layer.UvCenter.X + delta.X, 0f, 1f),
                    Math.Clamp(layer.UvCenter.Y + delta.Y, 0f, 1f));
                MarkPreviewDirty();
            }

            if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
                canvasDraggingLayer = false;
        }

        if (canvasDraggingLayer)
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll);
        else if (canvasScalingLayer)
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll);
        else if (canvasPanning)
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

        var mouseUv = (mousePos - uvOrigin) / fitSize;
        if (mouseUv.X >= 0 && mouseUv.X <= 1 && mouseUv.Y >= 0 && mouseUv.Y <= 1)
        {
            var statusText = $"UV: {mouseUv.X:F3}, {mouseUv.Y:F3}";
            if (io.KeyShift) statusText += "  [Shift: 锁X]";
            if (io.KeyCtrl) statusText += "  [Ctrl: 锁Y]";
            if (io.KeyAlt) statusText += "  [Alt: 旋转]";
            var drawList = ImGui.GetWindowDrawList();
            var textPos = canvasPos + new Vector2(4, canvasSize.Y - 18);
            drawList.AddRectFilled(textPos - new Vector2(2, 1), textPos + new Vector2(ImGui.CalcTextSize(statusText).X + 4, 16),
                ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.7f)));
            drawList.AddText(textPos, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.95f)), statusText);
        }
    }

    // ── Right Panel: Parameters ──────────────────────────────────────────────

    private void DrawParameterPanel()
    {
        var group = project.SelectedGroup;
        var layer = group?.SelectedLayer;
        if (layer == null)
        {
            if (group != null)
                ImGui.TextDisabled("选择或添加一个图层");
            else
                ImGui.TextDisabled("先添加一个投影目标");
            ImGui.Separator();
            DrawActionsSection();
            return;
        }

        var idx = group!.SelectedLayerIndex;
        if (lastEditedLayerIndex != idx)
            SyncImagePathBuf();

        ImGui.SetNextItemWidth(-1);
        var name = layer.Name;
        if (ImGui.InputText("##LayerName", ref name, 128))
            layer.Name = name;

        ImGui.Spacing();

        if (ImGui.CollapsingHeader("贴花图片", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 30);
            if (ImGui.InputText("##ImagePath", ref imagePathBuf, 512))
            {
                layer.ImagePath = imagePathBuf;
                lastEditedLayerIndex = idx;
            }
            ImGui.SameLine();
            if (ImGuiComponents.IconButton(20, FontAwesomeIcon.FolderOpen))
            {
                var capturedGi = project.SelectedGroupIndex;
                var capturedLi = idx;
                fileDialog.OpenFileDialog(
                    "选择贴花图片",
                    "图片文件{.png,.jpg,.jpeg,.tga,.bmp,.dds}",
                    (ok, paths) =>
                    {
                        if (ok && paths.Count > 0 && capturedGi < project.Groups.Count)
                        {
                            var g = project.Groups[capturedGi];
                            if (capturedLi < g.Layers.Count)
                            {
                                var path = paths[0];
                                g.Layers[capturedLi].ImagePath = path;
                                imagePathBuf = path;
                                lastEditedLayerIndex = capturedLi;
                                config.LastImageDir = Path.GetDirectoryName(path);
                                config.Save();
                            }
                        }
                    },
                    1, config.LastImageDir, false);
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("浏览...");
        }

        var hasImage = !string.IsNullOrEmpty(layer.ImagePath);

        using (ImRaii.Disabled(!hasImage))
        {
            if (ImGui.CollapsingHeader("UV 位置", ImGuiTreeNodeFlags.DefaultOpen))
            {
                ImGui.SetNextItemWidth(-1);
                var center = layer.UvCenter;
                if (ImGui.DragFloat2("##中心", ref center, 0.005f, 0f, 1f, "%.3f"))
                { layer.UvCenter = center; MarkPreviewDirty(); }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("中心点 ");
                {
                    var cx = layer.UvCenter.X; var cy = layer.UvCenter.Y;
                    if (ScrollAdjust(ref cx, 0.001f, 0f, 1f))
                    { layer.UvCenter = new Vector2(cx, cy); MarkPreviewDirty(); }
                }

                var lockIcon = scaleLocked ? FontAwesomeIcon.Link : FontAwesomeIcon.Unlink;
                if (ImGuiComponents.IconButton(30, lockIcon))
                    scaleLocked = !scaleLocked;
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(scaleLocked ? "比例锁定" : "比例解锁");
                ImGui.SameLine();
                ImGui.SetNextItemWidth(-1);
                var uvScale = layer.UvScale;
                if (scaleLocked)
                {
                    var s = uvScale.X;
                    if (ImGui.DragFloat("##scaleLocked", ref s, 0.005f, 0.01f, 2f, "%.3f"))
                    { layer.UvScale = new Vector2(s, s); MarkPreviewDirty(); }
                    if (ScrollAdjust(ref s, 0.005f, 0.01f, 2f))
                    { layer.UvScale = new Vector2(s, s); MarkPreviewDirty(); }
                }
                else
                {
                    if (ImGui.DragFloat2("##scaleUnlocked", ref uvScale, 0.005f, 0.01f, 2f, "%.3f"))
                    { layer.UvScale = uvScale; MarkPreviewDirty(); }
                }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("大小 ");

                ImGui.SetNextItemWidth(-1);
                var rot = layer.RotationDeg;
                if (ImGui.DragFloat("##rot", ref rot, 1f, -180f, 180f, "%.1f°"))
                { layer.RotationDeg = rot; MarkPreviewDirty(); }
                if (ScrollAdjust(ref rot, 1f, -180f, 180f))
                { layer.RotationDeg = rot; MarkPreviewDirty(); }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("旋转 ");
            }

            if (ImGui.CollapsingHeader("渲染", ImGuiTreeNodeFlags.DefaultOpen))
            {
                ImGui.SetNextItemWidth(-1);
                var opacity = layer.Opacity;
                if (ImGui.DragFloat("##opacity", ref opacity, 0.01f, 0f, 1f, "%.2f"))
                { layer.Opacity = opacity; MarkPreviewDirty(); }
                if (ScrollAdjust(ref opacity, 0.02f, 0f, 1f))
                { layer.Opacity = opacity; MarkPreviewDirty(); }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("不透明度 ");

                ImGui.SetNextItemWidth(-1);
                var blendIdx = Array.IndexOf(BlendModeValues, layer.BlendMode);
                if (blendIdx < 0) blendIdx = 0;
                if (ImGui.Combo("##blend", ref blendIdx, BlendModeNames, BlendModeNames.Length))
                { layer.BlendMode = BlendModeValues[blendIdx]; MarkPreviewDirty(); }

                var affDiff = layer.AffectsDiffuse;
                if (ImGui.Checkbox("贴图", ref affDiff))
                { layer.AffectsDiffuse = affDiff; MarkPreviewDirty(); }
                ImGui.SameLine();
                var affEmissive = layer.AffectsEmissive;
                if (ImGui.Checkbox("发光", ref affEmissive))
                { layer.AffectsEmissive = affEmissive; MarkPreviewDirty(); }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("修改材质启用发光效果");

                if (layer.AffectsEmissive)
                {
                    var emColor = layer.EmissiveColor;
                    if (ImGui.ColorEdit3("##emColor", ref emColor, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoLabel))
                    { layer.EmissiveColor = emColor; MarkPreviewDirty(); }
                    ImGui.SameLine();
                    ImGui.Text("发光颜色");

                    ImGui.SetNextItemWidth(-1);
                    var emIntensity = layer.EmissiveIntensity;
                    if (ImGui.DragFloat("##emIntensity", ref emIntensity, 0.05f, 0.1f, 10f, "%.2f"))
                    { layer.EmissiveIntensity = emIntensity; MarkPreviewDirty(); }
                    if (ScrollAdjust(ref emIntensity, 0.1f, 0.1f, 10f))
                    { layer.EmissiveIntensity = emIntensity; MarkPreviewDirty(); }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("发光强度");

                    ImGui.SetNextItemWidth(-1);
                    var maskIdx = (int)layer.EmissiveMask;
                    if (ImGui.Combo("##emMask", ref maskIdx, EmissiveMaskNames, EmissiveMaskNames.Length))
                    { layer.EmissiveMask = (EmissiveMask)maskIdx; MarkPreviewDirty(); }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("发光遮罩模式");

                    if (layer.EmissiveMask != EmissiveMask.Uniform)
                    {
                        ImGui.SetNextItemWidth(-1);
                        var falloff = layer.EmissiveMaskFalloff;
                        if (ImGui.DragFloat("##emFalloff", ref falloff, 0.01f, 0.01f, 1f, "%.2f"))
                        { layer.EmissiveMaskFalloff = falloff; MarkPreviewDirty(); }
                        if (ScrollAdjust(ref falloff, 0.05f, 0.01f, 1f))
                        { layer.EmissiveMaskFalloff = falloff; MarkPreviewDirty(); }
                        if (ImGui.IsItemHovered()) ImGui.SetTooltip("渐变范围");
                    }

                    // Emissive mask preview
                    DrawEmissiveMaskPreview(layer.EmissiveMask, layer.EmissiveMaskFalloff);

                    if (string.IsNullOrEmpty(group.MtrlGamePath))
                        ImGui.TextColored(new Vector4(1, 0.5f, 0.3f, 1), "需要选择材质(.mtrl)");
                }
            }
        }

        ImGui.Separator();
        DrawActionsSection();
    }

    private void DrawEmissiveMaskPreview(EmissiveMask mask, float falloff)
    {
        ImGui.TextDisabled("遮罩预览:");
        var previewSize = 100f;
        var pos = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();

        var cellCount = 32;
        var cellSize = previewSize / cellCount;

        for (int y = 0; y < cellCount; y++)
        {
            for (int x = 0; x < cellCount; x++)
            {
                float ru = (x + 0.5f) / cellCount - 0.5f;
                float rv = (y + 0.5f) / cellCount - 0.5f;
                float val = PreviewService.ComputeEmissiveMask(mask, falloff, ru, rv, 1f);

                var color = ImGui.GetColorU32(new Vector4(val, val, val, 1f));
                var p0 = pos + new Vector2(x * cellSize, y * cellSize);
                var p1 = p0 + new Vector2(cellSize + 0.5f, cellSize + 0.5f);
                drawList.AddRectFilled(p0, p1, color);
            }
        }

        drawList.AddRect(pos, pos + new Vector2(previewSize),
            ImGui.GetColorU32(new Vector4(0.4f, 0.4f, 0.4f, 1f)));
        ImGui.Dummy(new Vector2(previewSize, previewSize + 4));
    }

    private void DrawActionsSection()
    {
        var group = project.SelectedGroup;
        var hasTarget = group != null && !string.IsNullOrEmpty(group.DiffuseGamePath);

        if (group != null && !string.IsNullOrEmpty(group.MeshDiskPath) && previewService.CurrentMesh == null)
        {
            if (ImGui.Button("加载模型", new Vector2(-1, 24)))
                previewService.LoadMesh(group.MeshDiskPath);
        }

        var autoPreview = config.AutoPreview;
        if (ImGui.Checkbox("自动预览", ref autoPreview))
        {
            config.AutoPreview = autoPreview;
            config.Save();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("参数变化时自动更新游戏内预览");

        if (!config.AutoPreview)
        {
            using (ImRaii.Disabled(!hasTarget))
            {
                if (ImGui.Button("更新预览", new Vector2(-1, 26)))
                    TriggerPreview();
            }
        }

        if (config.AutoPreview && previewDirty && hasTarget)
        {
            var elapsed = (DateTime.UtcNow - lastDirtyTime).TotalSeconds;
            if (elapsed >= PreviewDebounceSec)
            {
                previewDirty = false;
                TriggerPreview();
            }
        }
    }

    private void TriggerPreview()
    {
        // Auto-detect mtrl for groups that need emissive
        foreach (var group in project.Groups)
        {
            if (group.HasEmissiveLayers() && string.IsNullOrEmpty(group.MtrlGamePath))
                AutoDetectMtrl(group);
        }

        previewService.UpdatePreview(project);
    }

    private void AutoDetectMtrl(TargetGroup group)
    {
        // Try finding mtrl from the resource tree by locating the equipment node
        // that contains our diffuse texture, then grabbing its mtrl descendant
        var trees = penumbra.GetPlayerTrees();
        if (trees == null || string.IsNullOrEmpty(group.DiffuseGamePath)) return;

        foreach (var (_, tree) in trees)
        {
            foreach (var topNode in tree.Nodes)
            {
                var diffuse = FindDescendant(topNode, n =>
                    n.Type == ResourceType.Tex && n.GamePath == group.DiffuseGamePath);
                if (diffuse == null) continue;

                var mtrl = FindDescendant(topNode, n =>
                    n.Type == ResourceType.Mtrl && !n.ActualPath.Contains("pluginConfigs"));
                if (mtrl != null)
                {
                    group.MtrlGamePath = mtrl.GamePath ?? "";
                    group.MtrlDiskPath = mtrl.ActualPath;
                    group.OrigMtrlDiskPath ??= mtrl.ActualPath;
                    DebugServer.AppendLog($"[MainWindow] Auto-detected mtrl for {group.Name}: {mtrl.GamePath}");
                    return;
                }
            }
        }

        // Fallback: match by disk path proximity
        var diffuseDisk = group.OrigDiffuseDiskPath ?? group.DiffuseDiskPath;
        if (string.IsNullOrEmpty(diffuseDisk)) return;
        var targetDir = Path.GetDirectoryName(diffuseDisk)?.Replace('\\', '/').ToLowerInvariant() ?? "";

        var resources = penumbra.GetPlayerResources();
        if (resources == null) return;

        foreach (var (_, paths) in resources)
        {
            foreach (var (diskPath, gamePaths) in paths)
            {
                if (diskPath.Contains("pluginConfigs")) continue;
                var ext = Path.GetExtension(diskPath).ToLowerInvariant();
                if (ext != ".mtrl") continue;

                var mtrlDir = Path.GetDirectoryName(diskPath)?.Replace('\\', '/').ToLowerInvariant() ?? "";
                if (mtrlDir == targetDir)
                {
                    var gp = gamePaths.FirstOrDefault() ?? "";
                    group.MtrlGamePath = gp;
                    group.MtrlDiskPath = diskPath;
                    group.OrigMtrlDiskPath ??= diskPath;
                    DebugServer.AppendLog($"[MainWindow] Auto-detected mtrl (disk proximity) for {group.Name}: {gp}");
                    return;
                }
            }
        }
    }

    private void MarkPreviewDirty()
    {
        previewDirty = true;
        lastDirtyTime = DateTime.UtcNow;
    }

    private static bool ScrollAdjust(ref float value, float step, float min, float max)
    {
        if (!ImGui.IsItemHovered()) return false;
        var wheel = ImGui.GetIO().MouseWheel;
        if (MathF.Abs(wheel) < 0.01f) return false;
        value = Math.Clamp(value + wheel * step, min, max);
        return true;
    }

    // ── Resource Browser Window ──────────────────────────────────────────────

    private void DrawResourceWindow()
    {
        ImGui.SetNextWindowSize(new Vector2(900, 600), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("添加投影目标###SkinTatooResources", ref resourceWindowOpen))
        {
            ImGui.End();
            return;
        }

        if (ImGui.Button("刷新资源"))
            RefreshResources();
        ImGui.SameLine();
        if (ImGui.Button("全部展开"))
            treeExpandRequest = true;
        ImGui.SameLine();
        if (ImGui.Button("全部折叠"))
            treeCollapseRequest = true;
        ImGui.SameLine();
        ImGui.TextDisabled(penumbra.IsAvailable ? "Penumbra 已连接" : "Penumbra 未连接");

        if (!penumbra.IsAvailable)
        {
            ImGui.TextColored(new Vector4(1, 0.4f, 0.4f, 1), "Penumbra 未连接");
            ImGui.End();
            return;
        }

        if (cachedTrees == null || cachedTrees.Count == 0)
        {
            ImGui.TextDisabled("点击「刷新资源」查询玩家资源");
            ImGui.End();
            return;
        }

        ImGui.Separator();

        using var scroll = ImRaii.Child("##TreeScroll", new Vector2(-1, -1), false);
        if (!scroll.Success) { ImGui.End(); return; }

        foreach (var (objIdx, tree) in cachedTrees)
        {
            ImGui.PushID(objIdx);

            if (treeExpandRequest) ImGui.SetNextItemOpen(true);
            if (treeCollapseRequest) ImGui.SetNextItemOpen(false);

            var headerLabel = $"{tree.Name}  (#{objIdx})";
            if (ImGui.CollapsingHeader(headerLabel, ImGuiTreeNodeFlags.DefaultOpen))
            {
                if (ImGui.BeginTable("##ResTree", 4,
                    ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersInnerH |
                    ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
                {
                    ImGui.TableSetupColumn("##action", ImGuiTableColumnFlags.WidthFixed, 46);
                    ImGui.TableSetupColumn("装备/外貌", ImGuiTableColumnFlags.WidthFixed, 280);
                    ImGui.TableSetupColumn("游戏路径", ImGuiTableColumnFlags.WidthStretch, 0.5f);
                    ImGui.TableSetupColumn("实际路径", ImGuiTableColumnFlags.WidthStretch, 0.5f);
                    ImGui.TableHeadersRow();

                    for (var i = 0; i < tree.Nodes.Count; i++)
                    {
                        if (!HasMtrlDescendant(tree.Nodes[i])) continue;
                        ImGui.PushID(i);
                        DrawResourceNode(tree.Nodes[i], null);
                        ImGui.PopID();
                    }

                    ImGui.EndTable();
                }
            }
            ImGui.PopID();
        }

        treeExpandRequest = false;
        treeCollapseRequest = false;

        ImGui.End();
    }

    /// <param name="parentMdl">The parent Mdl node, passed down so Mtrl rows can reference mesh info.</param>
    private void DrawResourceNode(ResourceNodeDto node, ResourceNodeDto? parentMdl)
    {
        var mdlForChildren = node.Type == ResourceType.Mdl ? node : parentMdl;

        ImGui.TableNextRow();

        // Column 1: action button
        ImGui.TableNextColumn();
        var isMtrl = node.Type == ResourceType.Mtrl;
        var mtrlHasDiffuse = isMtrl && HasDiffuseDescendant(node);
        if (mtrlHasDiffuse)
        {
            var addedGroupName = GetMtrlAddedGroupName(node);
            if (addedGroupName != null)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.3f, 1f, 0.3f, 1f));
                ImGui.PushFont(UiBuilder.IconFont);
                ImGui.Text(FontAwesomeIcon.Check.ToIconString());
                ImGui.PopFont();
                ImGui.PopStyleColor();
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip($"已添加到: {addedGroupName}");
            }
            else if (ImGui.SmallButton("添加"))
            {
                AddTargetGroupFromMtrl(node, parentMdl);
            }
        }

        // Column 2: tree structure + name
        ImGui.TableNextColumn();
        var nodeName = node.Name ?? Path.GetFileName(node.GamePath ?? node.ActualPath);
        var visibleChildren = node.Children.Where(c => !ShouldSkipNode(c)).ToList();
        var hasChildren = visibleChildren.Count > 0;
        var flags = ImGuiTreeNodeFlags.SpanAvailWidth;
        if (!hasChildren) flags |= ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen;

        if (treeExpandRequest) ImGui.SetNextItemOpen(true);
        if (treeCollapseRequest) ImGui.SetNextItemOpen(false);

        var added = mtrlHasDiffuse && GetMtrlAddedGroupName(node) != null;
        if (added) ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.3f, 1f, 0.3f, 1f));

        var typeTag = GetNodeTypeTag(node);
        var label = string.IsNullOrEmpty(typeTag) ? nodeName : $"{typeTag} {nodeName}";
        var open = ImGui.TreeNodeEx(label, flags);

        if (added) ImGui.PopStyleColor();

        // Column 3: game path
        ImGui.TableNextColumn();
        if (!string.IsNullOrEmpty(node.GamePath))
        {
            ImGui.TextUnformatted(node.GamePath);
            DrawPathHoverPreview(node, node.GamePath);
            DrawPathContextMenu(node.GamePath, "gp");
        }

        // Column 4: actual path
        ImGui.TableNextColumn();
        if (!string.IsNullOrEmpty(node.ActualPath))
        {
            var isModded = IsModdedPath(node);
            if (isModded) ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.5f, 0.8f, 1f, 1f));
            ImGui.TextUnformatted(node.ActualPath);
            if (isModded) ImGui.PopStyleColor();
            DrawPathHoverPreview(node, node.ActualPath);
            DrawPathContextMenu(node.ActualPath, "ap");
        }

        // Recurse visible children
        if (open && hasChildren)
        {
            for (var i = 0; i < visibleChildren.Count; i++)
            {
                ImGui.PushID(i);
                DrawResourceNode(visibleChildren[i], mdlForChildren);
                ImGui.PopID();
            }
            ImGui.TreePop();
        }
    }

    private void DrawPathHoverPreview(ResourceNodeDto node, string path)
    {
        if (!ImGui.IsItemHovered() || node.Type != ResourceType.Tex) return;
        try
        {
            var normalized = path.Replace('\\', '/');
            var shared = Path.IsPathRooted(path)
                ? textureProvider.GetFromFile(path)
                : textureProvider.GetFromGame(normalized);
            var wrap = shared.GetWrapOrDefault();
            if (wrap != null)
            {
                ImGui.BeginTooltip();
                ImGui.Image(wrap.Handle, new Vector2(384, 384));
                ImGui.Text($"{wrap.Width}x{wrap.Height}");
                ImGui.EndTooltip();
            }
        }
        catch { }
    }

    private static void DrawPathContextMenu(string path, string id)
    {
        if (!ImGui.BeginPopupContextItem(id)) return;
        if (ImGui.Selectable("复制路径"))
            ImGui.SetClipboardText(path);
        ImGui.EndPopup();
    }

    private static bool IsModdedPath(ResourceNodeDto node)
    {
        if (node.GamePath == null) return false;
        // actualPath with backslashes but same content as gamePath is NOT modded
        var normalized = node.ActualPath.Replace('\\', '/');
        return normalized != node.GamePath;
    }

    private static bool ShouldSkipNode(ResourceNodeDto node)
    {
        // Skip node types that are not useful for decal target selection
        return node.Type is ResourceType.Imc or ResourceType.Sklb or ResourceType.Skp
            or ResourceType.Phyb or ResourceType.Eid or ResourceType.Pbd
            or ResourceType.Kdb or ResourceType.Shpk;
    }

    private static bool HasMtrlDescendant(ResourceNodeDto node)
    {
        if (node.Type == ResourceType.Mtrl) return true;
        return node.Children.Any(HasMtrlDescendant);
    }


    private static string GetNodeTypeTag(ResourceNodeDto node)
    {
        // Equipment-level icons take priority
        var iconTag = node.Icon switch
        {
            ChangedItemIcon.Head => "[头部]",
            ChangedItemIcon.Body => "[身体]",
            ChangedItemIcon.Hands => "[手部]",
            ChangedItemIcon.Legs => "[腿部]",
            ChangedItemIcon.Feet => "[脚部]",
            ChangedItemIcon.Ears => "[耳饰]",
            ChangedItemIcon.Neck => "[项链]",
            ChangedItemIcon.Wrists => "[手镯]",
            ChangedItemIcon.Finger => "[戒指]",
            ChangedItemIcon.Mainhand => "[主手]",
            ChangedItemIcon.Offhand => "[副手]",
            ChangedItemIcon.Customization => "[外貌]",
            _ => (string?)null,
        };
        if (iconTag != null) return iconTag;

        return node.Type switch
        {
            ResourceType.Mdl => "[模型]",
            ResourceType.Mtrl => "[材质]",
            ResourceType.Tex => "[贴图]",
            _ => "",
        };
    }

    // ── Selection logic ──────────────────────────────────────────────────────

    private void AddTargetGroupFromMtrl(ResourceNodeDto mtrlNode, ResourceNodeDto? parentMdl)
    {
        previewService.ClearTextureCache();
        penumbra.ClearRedirect();
        penumbra.RedrawPlayer();

        // Capture diffuse game path before refresh for re-finding
        var diffuse = FindDescendant(mtrlNode, n =>
            n.Type == ResourceType.Tex && n.GamePath != null && IsDiffusePath(n.GamePath));
        var diffuseGp = diffuse?.GamePath;
        var mtrlGp = mtrlNode.GamePath;

        // Re-refresh for clean disk paths
        RefreshResources();

        // Re-find the Mtrl node in the refreshed tree
        ResourceNodeDto? freshMtrl = null;
        ResourceNodeDto? freshMdl = null;
        if (cachedTrees != null)
        {
            foreach (var (_, tree) in cachedTrees)
            {
                foreach (var topNode in tree.Nodes)
                {
                    var candidates = CollectDescendants(topNode, n => n.Type == ResourceType.Mtrl);
                    foreach (var candidate in candidates)
                    {
                        // Match by mtrl game path first, then by diffuse game path
                        var match = (mtrlGp != null && candidate.GamePath == mtrlGp) ||
                            (diffuseGp != null && FindDescendant(candidate, n =>
                                n.Type == ResourceType.Tex && n.GamePath == diffuseGp) != null);
                        if (!match) continue;

                        freshMtrl = candidate;
                        // Find the parent Mdl
                        freshMdl = FindAncestorMdl(topNode, candidate);
                        goto found;
                    }
                }
            }
        }
        found:
        freshMtrl ??= mtrlNode;
        freshMdl ??= parentMdl;

        var groupName = freshMtrl.Name ?? Path.GetFileName(freshMtrl.GamePath ?? freshMtrl.ActualPath);
        var tg = project.AddGroup(groupName);

        // Diffuse and Normal from Mtrl's Tex children
        var freshDiffuse = FindDescendant(freshMtrl, n =>
            n.Type == ResourceType.Tex && n.GamePath != null && IsDiffusePath(n.GamePath));
        var freshNormal = FindDescendant(freshMtrl, n =>
            n.Type == ResourceType.Tex && n.GamePath != null && IsNormalPath(n.GamePath));

        if (freshDiffuse != null)
        {
            tg.DiffuseGamePath = freshDiffuse.GamePath!;
            tg.DiffuseDiskPath = GetDiskPath(freshDiffuse);
            tg.OrigDiffuseDiskPath = tg.DiffuseDiskPath;
        }

        if (freshNormal != null)
        {
            tg.NormGamePath = freshNormal.GamePath!;
            tg.NormDiskPath = GetDiskPath(freshNormal);
            tg.OrigNormDiskPath = tg.NormDiskPath;
        }

        // Material is the Mtrl node itself
        tg.MtrlGamePath = freshMtrl.GamePath ?? "";
        tg.MtrlDiskPath = GetDiskPath(freshMtrl);
        tg.OrigMtrlDiskPath = tg.MtrlDiskPath;

        // Mesh from parent Mdl
        if (freshMdl != null)
        {
            var meshPath = GetDiskPath(freshMdl);
            tg.MeshDiskPath = meshPath;
            previewService.LoadMesh(meshPath);
        }

        config.Save();
        DebugServer.AppendLog($"[MainWindow] Added target group: {tg.Name}");
    }

    // ── Resource tree helpers ────────────────────────────────────────────────

    private void RefreshResources()
    {
        cachedTrees = penumbra.GetPlayerTrees();
        var count = cachedTrees?.Values.Sum(t => CountNodes(t.Nodes)) ?? 0;
        DebugServer.AppendLog($"[MainWindow] Refreshed: {cachedTrees?.Count ?? 0} objects, {count} nodes");
    }

    private static int CountNodes(List<ResourceNodeDto> nodes)
        => nodes.Sum(n => 1 + CountNodes(n.Children));

    private static ResourceNodeDto? FindDescendant(ResourceNodeDto node, Func<ResourceNodeDto, bool> predicate)
    {
        foreach (var child in node.Children)
        {
            if (predicate(child)) return child;
            var found = FindDescendant(child, predicate);
            if (found != null) return found;
        }
        return null;
    }

    private static bool HasDiffuseDescendant(ResourceNodeDto node)
    {
        if (node.Type == ResourceType.Tex && node.GamePath != null && IsDiffusePath(node.GamePath))
            return true;
        return node.Children.Any(HasDiffuseDescendant);
    }

    private static bool IsDiffusePath(string gamePath)
    {
        var gp = gamePath.ToLowerInvariant();
        return gp.EndsWith(".tex") && !gp.Contains("_n.tex") && !gp.Contains("_m.tex")
            && !gp.Contains("norm") && !gp.Contains("mask");
    }

    /// <summary>
    /// Get the effective disk path from a resource node.
    /// If actualPath is an absolute path (modded), use it; otherwise use gamePath.
    /// </summary>
    private static string GetDiskPath(ResourceNodeDto node)
        => Path.IsPathRooted(node.ActualPath) ? node.ActualPath : (node.GamePath ?? node.ActualPath);

    private static bool IsNormalPath(string gamePath)
    {
        var gp = gamePath.ToLowerInvariant();
        return gp.EndsWith(".tex") && (gp.Contains("_n.tex") || gp.Contains("norm"));
    }

    private string? GetMtrlAddedGroupName(ResourceNodeDto mtrlNode)
    {
        var diffuse = FindDescendant(mtrlNode, n =>
            n.Type == ResourceType.Tex && n.GamePath != null && IsDiffusePath(n.GamePath));
        if (diffuse?.GamePath == null) return null;
        return project.Groups.FirstOrDefault(g => g.DiffuseGamePath == diffuse.GamePath)?.Name;
    }

    private static List<ResourceNodeDto> CollectDescendants(ResourceNodeDto node, Func<ResourceNodeDto, bool> predicate)
    {
        var result = new List<ResourceNodeDto>();
        if (predicate(node)) result.Add(node);
        foreach (var child in node.Children)
            result.AddRange(CollectDescendants(child, predicate));
        return result;
    }

    private static ResourceNodeDto? FindAncestorMdl(ResourceNodeDto root, ResourceNodeDto target)
    {
        // Walk the tree to find the Mdl node that is an ancestor of target
        if (root == target) return null;
        foreach (var child in root.Children)
        {
            if (child == target)
                return root.Type == ResourceType.Mdl ? root : null;
            var found = FindAncestorMdl(child, target);
            if (found != null) return found;
            // If child contains target and child is Mdl, return child
            if (ContainsNode(child, target) && child.Type == ResourceType.Mdl)
                return child;
        }
        return null;
    }

    private static bool ContainsNode(ResourceNodeDto root, ResourceNodeDto target)
    {
        if (root == target) return true;
        return root.Children.Any(c => ContainsNode(c, target));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void ResetSelectedLayer()
    {
        var layer = project.SelectedLayer;
        if (layer == null) return;
        var d = new DecalLayer();
        layer.UvCenter = d.UvCenter;
        layer.UvScale = d.UvScale;
        layer.RotationDeg = d.RotationDeg;
        layer.Opacity = d.Opacity;
        layer.BlendMode = d.BlendMode;
        layer.IsVisible = d.IsVisible;
        layer.AffectsDiffuse = d.AffectsDiffuse;
    }

    private void SyncImagePathBuf()
    {
        var layer = project.SelectedLayer;
        var group = project.SelectedGroup;
        lastEditedLayerIndex = group?.SelectedLayerIndex ?? -1;
        imagePathBuf = layer?.ImagePath ?? string.Empty;
    }

    public void Dispose() { }
}
