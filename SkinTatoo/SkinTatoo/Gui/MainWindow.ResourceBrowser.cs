using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Penumbra.Api.Enums;
using Penumbra.Api.Helpers;
using SkinTatoo.Core;
using SkinTatoo.Http;
using SkinTatoo.Services;

namespace SkinTatoo.Gui;

public partial class MainWindow
{
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

        // Recurse
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

    // ── Selection logic ──────────────────────────────────────────────────────

    private void AddTargetGroupFromMtrl(ResourceNodeDto mtrlNode, ResourceNodeDto? parentMdl)
    {
        previewService.ClearTextureCache();
        previewService.ResetSwapState();
        penumbra.ClearRedirect();
        penumbra.RedrawPlayer();

        var diffuse = FindDescendant(mtrlNode, n =>
            n.Type == ResourceType.Tex && n.GamePath != null && IsDiffusePath(n.GamePath));
        var diffuseGp = diffuse?.GamePath;
        var mtrlGp = mtrlNode.GamePath;

        RefreshResources();

        // Re-find the Mtrl node in the refreshed tree.
        // Only collect extra models from the SAME resource tree (same character object)
        // to avoid mixing models from different races that share the same diffuse texture.
        ResourceNodeDto? freshMtrl = null;
        ResourceNodeDto? freshMdl = null;
        ushort primaryTreeId = 0;
        var extraMdls = new List<ResourceNodeDto>();
        if (cachedTrees != null)
        {
            foreach (var (treeId, tree) in cachedTrees)
            {
                foreach (var topNode in tree.Nodes)
                {
                    var candidates = CollectDescendants(topNode, n => n.Type == ResourceType.Mtrl);
                    foreach (var candidate in candidates)
                    {
                        var match = (mtrlGp != null && candidate.GamePath == mtrlGp) ||
                            (diffuseGp != null && FindDescendant(candidate, n =>
                                n.Type == ResourceType.Tex && n.GamePath == diffuseGp) != null);
                        if (!match) continue;

                        var mdl = FindAncestorMdl(topNode, candidate);
                        if (freshMtrl == null)
                        {
                            freshMtrl = candidate;
                            freshMdl = mdl;
                            primaryTreeId = treeId;
                        }
                        else if (mdl != null && mdl != freshMdl && treeId == primaryTreeId)
                        {
                            extraMdls.Add(mdl);
                        }
                    }
                }
            }
        }
        freshMtrl ??= mtrlNode;
        freshMdl ??= parentMdl;

        var groupName = freshMtrl.Name ?? Path.GetFileName(freshMtrl.GamePath ?? freshMtrl.ActualPath);
        var tg = project.AddGroup(groupName);

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

        tg.MtrlGamePath = freshMtrl.GamePath ?? "";
        tg.MtrlDiskPath = GetDiskPath(freshMtrl);
        tg.OrigMtrlDiskPath = tg.MtrlDiskPath;

        if (freshMdl != null)
        {
            tg.MeshDiskPath = GetDiskPath(freshMdl);
            foreach (var extra in extraMdls)
            {
                var extraPath = GetDiskPath(extra);
                if (!string.IsNullOrEmpty(extraPath) && extraPath != tg.MeshDiskPath)
                    tg.MeshDiskPaths.Add(extraPath);
            }
            previewService.LoadMeshes(tg.AllMeshPaths);
            ModelEditorWindowRef?.OnMeshChanged();
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
        if (root == target) return null;
        foreach (var child in root.Children)
        {
            if (child == target)
                return root.Type == ResourceType.Mdl ? root : null;
            var found = FindAncestorMdl(child, target);
            if (found != null) return found;
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

    private static bool IsModdedPath(ResourceNodeDto node)
    {
        if (node.GamePath == null) return false;
        var normalized = node.ActualPath.Replace('\\', '/');
        return normalized != node.GamePath;
    }

    private static bool ShouldSkipNode(ResourceNodeDto node)
    {
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
}
