using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using SkinTatoo.Core;
using SkinTatoo.Interop;
using SkinTatoo.Services;

namespace SkinTatoo.Gui;

public class MainWindow : Window, IDisposable
{
    private readonly DecalProject project;
    private readonly PreviewService previewService;
    private readonly PenumbraBridge penumbra;
    private readonly BodyModDetector bodyModDetector;
    private readonly Configuration config;
    private readonly ITextureProvider textureProvider;
    private readonly FileDialogManager fileDialog = new();

    private string imagePathBuf = string.Empty;
    private int lastEditedLayerIndex = -1;
    private int layerCounter;
    private string meshLoadStatus = "";

    private static readonly string[] BlendModeNames = ["正常", "正片叠底", "叠加", "柔光"];
    private static readonly BlendMode[] BlendModeValues = [BlendMode.Normal, BlendMode.Multiply, BlendMode.Overlay, BlendMode.SoftLight];

    private static readonly string[] TargetNames = ["身体", "面部"];
    private static readonly SkinTarget[] TargetValues = [SkinTarget.Body, SkinTarget.Face];

    // Body skin race codes to probe via Penumbra
    private static readonly string[] RaceCodes = ["0101", "0201", "0301", "0401", "0501", "0601", "0701", "0801", "1401", "1501", "1601", "1701", "1801"];
    private static readonly string[] TextureSuffixes = ["_d", "_n", "_s"];

    // Cached debug info
    private List<MaterialDebugEntry>? cachedMaterialInfo;
    private bool showDebugPanel;

    public MainWindow(
        DecalProject project,
        PreviewService previewService,
        PenumbraBridge penumbra,
        BodyModDetector bodyModDetector,
        Configuration config,
        ITextureProvider textureProvider)
        : base("SkinTatoo 纹身编辑器###SkinTatooMain",
               ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.project = project;
        this.previewService = previewService;
        this.penumbra = penumbra;
        this.bodyModDetector = bodyModDetector;
        this.config = config;
        this.textureProvider = textureProvider;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(700, 500),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void Draw()
    {
        // Top toolbar
        DrawToolbar();
        ImGui.Separator();

        var totalWidth = ImGui.GetContentRegionAvail().X;
        var totalHeight = ImGui.GetContentRegionAvail().Y;

        if (showDebugPanel)
        {
            var mainHeight = totalHeight * 0.6f;
            var debugHeight = totalHeight - mainHeight - ImGui.GetStyle().ItemSpacing.Y;

            DrawMainPanels(totalWidth, mainHeight);
            DrawDebugPanel(totalWidth, debugHeight);
        }
        else
        {
            DrawMainPanels(totalWidth, totalHeight);
        }

        fileDialog.Draw();
    }

    private void DrawToolbar()
    {
        var debugLabel = showDebugPanel ? "隐藏调试" : "显示调试";
        if (ImGui.Button(debugLabel))
        {
            showDebugPanel = !showDebugPanel;
            if (showDebugPanel && cachedMaterialInfo == null)
                RefreshMaterialInfo();
        }

        ImGui.SameLine();
        if (showDebugPanel && ImGui.Button("刷新材质信息"))
            RefreshMaterialInfo();
    }

    private void DrawMainPanels(float totalWidth, float height)
    {
        var leftWidth = totalWidth * 0.35f;
        var rightWidth = totalWidth - leftWidth - ImGui.GetStyle().ItemSpacing.X;

        using (var left = ImRaii.Child("##LeftPanel", new Vector2(leftWidth, height), true))
        {
            if (left.Success)
                DrawLayerPanel();
        }

        ImGui.SameLine();

        using (var right = ImRaii.Child("##RightPanel", new Vector2(rightWidth, height), true))
        {
            if (right.Success)
                DrawParameterPanel();
        }
    }

    private void DrawLayerPanel()
    {
        ImGui.TextDisabled("投影目标");
        ImGui.SameLine();
        var targetIdx = Array.IndexOf(TargetValues, project.Target);
        if (targetIdx < 0) targetIdx = 0;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.Combo("##Target", ref targetIdx, TargetNames, TargetNames.Length))
        {
            project.Target = TargetValues[targetIdx];
            config.LastTarget = project.Target;
            config.Save();
        }

        ImGui.Separator();

        var gpuColor = previewService.IsReady ? new Vector4(0, 1, 0.3f, 1) : new Vector4(1, 0.3f, 0.3f, 1);
        var penColor = penumbra.IsAvailable ? new Vector4(0, 1, 0.3f, 1) : new Vector4(1, 0.8f, 0, 1);
        ImGui.TextColored(gpuColor, previewService.IsReady ? "● GPU" : "● GPU ✗");
        ImGui.SameLine();
        ImGui.TextColored(penColor, penumbra.IsAvailable ? "● Penumbra" : "● Penumbra ✗");

        ImGui.Separator();

        ImGui.Text($"图层 ({project.Layers.Count})");
        ImGui.SameLine();
        if (ImGui.SmallButton("+##AddLayer"))
        {
            layerCounter++;
            project.AddLayer($"贴花 {layerCounter}");
        }
        ImGui.SameLine();
        using (ImRaii.Disabled(project.SelectedLayerIndex < 0 || project.Layers.Count == 0))
        {
            if (ImGui.SmallButton("-##RemoveLayer"))
                project.RemoveLayer(project.SelectedLayerIndex);
        }

        ImGui.Separator();

        using var listChild = ImRaii.Child("##LayerList", new Vector2(-1, -1), false);
        if (!listChild.Success) return;

        for (var i = 0; i < project.Layers.Count; i++)
        {
            var layer = project.Layers[i];

            var vis = layer.IsVisible;
            if (ImGui.Checkbox($"##vis{i}", ref vis))
                layer.IsVisible = vis;
            ImGui.SameLine();

            var isSelected = project.SelectedLayerIndex == i;
            if (ImGui.Selectable($"{layer.Name}##layer{i}", isSelected))
            {
                project.SelectedLayerIndex = i;
                SyncImagePathBuf(i);
            }
        }
    }

    private void DrawParameterPanel()
    {
        var layer = project.SelectedLayer;
        if (layer == null)
        {
            ImGui.TextDisabled("请在左侧选择一个图层");
            return;
        }

        var idx = project.SelectedLayerIndex;
        if (lastEditedLayerIndex != idx)
            SyncImagePathBuf(idx);

        // Name input with label on the left
        ImGui.Text("名称");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1);
        var name = layer.Name;
        if (ImGui.InputText("##LayerName", ref name, 128))
            layer.Name = name;

        ImGui.Spacing();

        // Image path with browse button
        ImGui.Text("贴花图片");
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 50);
        if (ImGui.InputText("##ImagePath", ref imagePathBuf, 512))
        {
            layer.ImagePath = imagePathBuf;
            lastEditedLayerIndex = idx;
        }
        ImGui.SameLine();
        if (ImGui.Button("...##Browse", new Vector2(-1, 0)))
        {
            var capturedIdx = idx;
            fileDialog.OpenFileDialog(
                "选择贴花图片",
                "图片文件{.png,.jpg,.jpeg,.tga,.bmp,.dds}",
                (ok, paths) =>
                {
                    if (ok && paths.Count > 0 && capturedIdx < project.Layers.Count)
                    {
                        var path = paths[0];
                        project.Layers[capturedIdx].ImagePath = path;
                        imagePathBuf = path;
                        lastEditedLayerIndex = capturedIdx;
                    }
                },
                1, null, false);
        }

        // Image preview
        if (!string.IsNullOrEmpty(layer.ImagePath) && File.Exists(layer.ImagePath))
        {
            try
            {
                var wrap = textureProvider.GetFromFile(layer.ImagePath).GetWrapOrDefault();
                if (wrap != null)
                {
                    var avail = ImGui.GetContentRegionAvail().X;
                    var previewSize = Math.Min(avail, 180f);
                    var aspect = (float)wrap.Width / wrap.Height;
                    var dispW = aspect >= 1f ? previewSize : previewSize * aspect;
                    var dispH = aspect >= 1f ? previewSize / aspect : previewSize;
                    ImGui.Image(wrap.Handle, new Vector2(dispW, dispH));
                    ImGui.SameLine();
                    ImGui.TextDisabled($"{wrap.Width}x{wrap.Height}");
                }
            }
            catch
            {
                ImGui.TextColored(new Vector4(1, 0.4f, 0.4f, 1), "图片加载失败");
            }
        }

        ImGui.Spacing();
        ImGui.Separator();

        var pos = layer.Position;
        if (ImGui.DragFloat3("位置", ref pos, 0.01f))
            layer.Position = pos;

        var rot = layer.Rotation;
        if (ImGui.DragFloat3("旋转 (°)", ref rot, 0.5f))
            layer.Rotation = rot;

        var scale = layer.Scale;
        if (ImGui.DragFloat2("缩放", ref scale, 0.005f, 0.001f, 10f))
            layer.Scale = scale;

        var depth = layer.Depth;
        if (ImGui.DragFloat("深度", ref depth, 0.005f, 0.001f, 5f))
            layer.Depth = depth;

        ImGui.Spacing();
        ImGui.Separator();

        var opacity = layer.Opacity;
        if (ImGui.SliderFloat("不透明度", ref opacity, 0f, 1f))
            layer.Opacity = opacity;

        var backface = layer.BackfaceCullingThreshold;
        if (ImGui.SliderFloat("背面剔除", ref backface, -1f, 1f))
            layer.BackfaceCullingThreshold = backface;

        var grazing = layer.GrazingAngleFade;
        if (ImGui.SliderFloat("掠射角渐隐", ref grazing, 0f, 1f))
            layer.GrazingAngleFade = grazing;

        ImGui.Spacing();
        ImGui.Separator();

        var blendIdx = Array.IndexOf(BlendModeValues, layer.BlendMode);
        if (blendIdx < 0) blendIdx = 0;
        ImGui.SetNextItemWidth(160);
        if (ImGui.Combo("混合模式", ref blendIdx, BlendModeNames, BlendModeNames.Length))
            layer.BlendMode = BlendModeValues[blendIdx];

        ImGui.Spacing();

        var affDiff = layer.AffectsDiffuse;
        if (ImGui.Checkbox("漫反射", ref affDiff))
            layer.AffectsDiffuse = affDiff;
        ImGui.SameLine();
        var affNorm = layer.AffectsNormal;
        if (ImGui.Checkbox("法线", ref affNorm))
            layer.AffectsNormal = affNorm;

        ImGui.Spacing();
        ImGui.Separator();

        if (previewService.CurrentMesh == null)
        {
            if (ImGui.Button("自动检测并加载身体网格", new Vector2(-1, 28)))
                AutoLoadBodyMesh();

            ImGui.SameLine();
            ImGui.TextDisabled("?");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("通过 Penumbra 获取当前玩家使用的身体模型路径");

            if (!string.IsNullOrEmpty(meshLoadStatus))
                ImGui.TextWrapped(meshLoadStatus);
        }
        else
        {
            var mesh = previewService.CurrentMesh;
            ImGui.TextDisabled($"网格: {mesh.Vertices.Length} 顶点, {mesh.TriangleCount} 三角形");

            if (ImGui.Button("更新预览", new Vector2(-1, 28)))
            {
                var texPath = FindPlayerBodyTexturePath();
                if (texPath != null)
                    previewService.UpdatePreview(project, texPath);
                else
                    meshLoadStatus = "无法找到皮肤贴图路径";
            }
        }
    }

    private void DrawDebugPanel(float width, float height)
    {
        using var child = ImRaii.Child("##DebugPanel", new Vector2(width, height), true);
        if (!child.Success) return;

        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1f, 1f), "材质/贴图调试信息");
        ImGui.SameLine();
        ImGui.TextDisabled("(通过 Penumbra 解析当前玩家路径)");
        ImGui.Separator();

        if (!penumbra.IsAvailable)
        {
            ImGui.TextColored(new Vector4(1, 0.4f, 0.4f, 1), "Penumbra 未连接，无法查询路径");
            return;
        }

        if (cachedMaterialInfo == null || cachedMaterialInfo.Count == 0)
        {
            ImGui.TextDisabled("点击「刷新材质信息」查询当前角色材质");
            return;
        }

        // Table
        if (ImGui.BeginTable("##MatTable", 4,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable |
            ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("类型", ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableSetupColumn("游戏路径", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("解析路径", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("状态", ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();

            foreach (var entry in cachedMaterialInfo)
            {
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(entry.Type);

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(entry.GamePath);

                ImGui.TableNextColumn();
                if (entry.IsRedirected)
                    ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), entry.ResolvedPath ?? "");
                else
                    ImGui.TextDisabled(entry.ResolvedPath ?? "(原版)");

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(entry.IsRedirected ? "Mod" : "原版");
            }

            ImGui.EndTable();
        }
    }

    private void RefreshMaterialInfo()
    {
        cachedMaterialInfo = [];
        if (!penumbra.IsAvailable) return;

        // Probe common body paths across race codes
        foreach (var rc in RaceCodes)
        {
            // Model
            var mdlPath = $"chara/human/c{rc}/obj/body/b0001/model/c{rc}b0001.mdl";
            var mdlResolved = penumbra.ResolvePlayer(mdlPath);
            if (mdlResolved != null)
            {
                cachedMaterialInfo.Add(new MaterialDebugEntry
                {
                    Type = "模型",
                    GamePath = mdlPath,
                    ResolvedPath = mdlResolved,
                    IsRedirected = mdlResolved != mdlPath,
                });
            }

            // Textures
            foreach (var suffix in TextureSuffixes)
            {
                var texPath = $"chara/human/c{rc}/obj/body/b0001/texture/--c{rc}b0001{suffix}.tex";
                var texResolved = penumbra.ResolvePlayer(texPath);
                if (texResolved != null)
                {
                    cachedMaterialInfo.Add(new MaterialDebugEntry
                    {
                        Type = suffix switch { "_d" => "漫反射", "_n" => "法线", "_s" => "遮罩", _ => suffix },
                        GamePath = texPath,
                        ResolvedPath = texResolved,
                        IsRedirected = texResolved != texPath,
                    });
                }
            }

            // Material
            var mtrlPath = $"chara/human/c{rc}/obj/body/b0001/material/v0001/mt_c{rc}b0001_a.mtrl";
            var mtrlResolved = penumbra.ResolvePlayer(mtrlPath);
            if (mtrlResolved != null)
            {
                cachedMaterialInfo.Add(new MaterialDebugEntry
                {
                    Type = "材质",
                    GamePath = mtrlPath,
                    ResolvedPath = mtrlResolved,
                    IsRedirected = mtrlResolved != mtrlPath,
                });
            }
        }

        // Face too
        foreach (var rc in RaceCodes)
        {
            foreach (var suffix in TextureSuffixes)
            {
                var texPath = $"chara/human/c{rc}/obj/face/f0001/texture/--c{rc}f0001{suffix}.tex";
                var texResolved = penumbra.ResolvePlayer(texPath);
                if (texResolved != null)
                {
                    cachedMaterialInfo.Add(new MaterialDebugEntry
                    {
                        Type = "面部" + suffix switch { "_d" => "漫反射", "_n" => "法线", "_s" => "遮罩", _ => suffix },
                        GamePath = texPath,
                        ResolvedPath = texResolved,
                        IsRedirected = texResolved != texPath,
                    });
                }
            }
        }
    }

    private void SyncImagePathBuf(int idx)
    {
        lastEditedLayerIndex = idx;
        imagePathBuf = (idx >= 0 && idx < project.Layers.Count)
            ? (project.Layers[idx].ImagePath ?? string.Empty)
            : string.Empty;
    }

    private void AutoLoadBodyMesh()
    {
        var resources = penumbra.GetPlayerResources();
        if (resources == null)
        {
            meshLoadStatus = "Penumbra 不可用，无法检测身体模型";
            return;
        }

        // Find body model: look for _top.mdl in equipment/e0000 (body slot) or body/b0001
        string? mdlPath = null;
        foreach (var (_, paths) in resources)
        {
            foreach (var (diskPath, gamePaths) in paths)
            {
                if (!diskPath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)) continue;

                foreach (var gp in gamePaths)
                {
                    if (gp.Contains("e0000") && gp.Contains("_top.mdl"))
                    {
                        mdlPath = diskPath;
                        break;
                    }
                    if (gp.Contains("obj/body/b0001/model/"))
                    {
                        mdlPath = diskPath;
                        break;
                    }
                }
                if (mdlPath != null) break;
            }
            if (mdlPath != null) break;
        }

        if (mdlPath == null)
        {
            meshLoadStatus = "未找到身体模型，尝试手动加载";
            return;
        }

        meshLoadStatus = $"加载中: {System.IO.Path.GetFileName(mdlPath)}...";
        var ok = previewService.LoadMesh(mdlPath);
        meshLoadStatus = ok
            ? $"加载成功: {previewService.CurrentMesh?.Vertices.Length} 顶点"
            : "加载失败，查看调试日志";
    }

    private string? FindPlayerBodyTexturePath()
    {
        var resources = penumbra.GetPlayerResources();
        if (resources == null) return null;

        // Find diffuse texture for body
        foreach (var (_, paths) in resources)
        {
            foreach (var (diskPath, gamePaths) in paths)
            {
                foreach (var gp in gamePaths)
                {
                    if (gp.Contains("obj/body/b0001/texture/") && gp.EndsWith("_d.tex"))
                        return gp;
                    if (gp.Contains("base.tex") || (gp.Contains("_d.tex") && gp.Contains("body")))
                        return diskPath;
                }
            }
        }

        return null;
    }

    public void Dispose() { }

    private class MaterialDebugEntry
    {
        public string Type { get; init; } = "";
        public string GamePath { get; init; } = "";
        public string? ResolvedPath { get; init; }
        public bool IsRedirected { get; init; }
    }
}
