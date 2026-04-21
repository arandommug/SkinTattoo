using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using SkinTattoo.Core;
using SkinTattoo.Http;
using SkinTattoo.Interop;
using SkinTattoo.Services.Localization;

namespace SkinTattoo.Gui;

public partial class MainWindow
{
    // -- Left Panel: Card-based layer list ----------------------------------

    private void DrawLayerPanel()
    {
        // Group-level buttons
        if (ImGuiComponents.IconButton(10, FontAwesomeIcon.Plus))
        {
            resourceWindowOpen = true;
            RefreshResources();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Strings.T("tooltip.add_group"));

        ImGui.SameLine();
        var io = ImGui.GetIO();
        var canDeleteGroup = project.SelectedGroupIndex >= 0 && io.KeyCtrl && io.KeyShift;
        using (ImRaii.Disabled(!canDeleteGroup))
        {
            if (ImGuiComponents.IconButton(11, FontAwesomeIcon.Trash))
            {
                var gi2 = project.SelectedGroupIndex;
                if (gi2 >= 0 && gi2 < project.Groups.Count)
                {
                    penumbra.ClearRedirect();
                    previewService.ClearTextureCache();
                    previewService.ResetSwapState();
                    previewService.ClearMesh();
                    previewService.NotifyMeshChanged();
                    penumbra.RedrawPlayer();
                    project.RemoveGroup(gi2);
                }
                MarkPreviewDirty();
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(Strings.T("tooltip.delete_group"));

        ImGui.SameLine();
        var selectedGroup = project.SelectedGroupIndex >= 0 && project.SelectedGroupIndex < project.Groups.Count
            ? project.Groups[project.SelectedGroupIndex] : null;
        var canCopyGroup = selectedGroup != null && selectedGroup.Layers.Count > 0;
        using (ImRaii.Disabled(!canCopyGroup))
        {
            if (ImGuiComponents.IconButton(12, FontAwesomeIcon.Copy))
                CopyDecalGroup(selectedGroup!);
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(Strings.T("menu.copy_decal_group"));

        ImGui.SameLine();
        var canPasteGroup = copiedGroupLayers != null && project.SelectedGroupIndex >= 0
                            && project.SelectedGroupIndex < project.Groups.Count;
        using (ImRaii.Disabled(!canPasteGroup))
        {
            if (ImGuiComponents.IconButton(13, FontAwesomeIcon.Paste))
                PasteDecalGroup(project.SelectedGroupIndex);
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(Strings.T("menu.paste_decal_group"));

        ImGui.SameLine();
        ImGui.TextDisabled($"({project.Groups.Count})");

        ImGui.Separator();

        using var listChild = ImRaii.Child("##GroupList", new Vector2(-1, -1), false);
        if (!listChild.Success) return;

        for (var gi = 0; gi < project.Groups.Count; gi++)
        {
            ImGui.PushID(gi);
            DrawGroupCard(gi);
            ImGui.PopID();
            ImGui.Spacing();
        }

        if (project.Groups.Count == 0)
            ImGui.TextDisabled(Strings.T("hint.empty_group_list"));
    }

    private void DrawGroupCard(int gi)
    {
        var group = project.Groups[gi];
        var isGroupSelected = project.SelectedGroupIndex == gi;
        var drawList = ImGui.GetWindowDrawList();
        var availWidth = ImGui.GetContentRegionAvail().X;

        // -- Card header --
        var headerStart = ImGui.GetCursorScreenPos();
        var headerHeight = ImGui.GetFrameHeight() + 4;
        var headerEnd = headerStart + new Vector2(availWidth, headerHeight);

        var headerColor = isGroupSelected && group.SelectedLayerIndex < 0
            ? ImGui.GetColorU32(new Vector4(0.20f, 0.35f, 0.55f, 1f))
            : ImGui.GetColorU32(new Vector4(0.18f, 0.18f, 0.22f, 1f));

        if (group.IsExpanded)
            drawList.AddRectFilled(headerStart, headerEnd, headerColor, 4f, ImDrawFlags.RoundCornersTop);
        else
            drawList.AddRectFilled(headerStart, headerEnd, headerColor, 4f);

        // Collapse arrow
        ImGui.SetCursorScreenPos(headerStart + new Vector2(4, 2));
        var arrowIcon = group.IsExpanded ? FontAwesomeIcon.ChevronDown : FontAwesomeIcon.ChevronRight;
        ImGui.PushStyleColor(ImGuiCol.Button, 0);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ImGui.GetColorU32(new Vector4(1, 1, 1, 0.1f)));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, 0);
        if (ImGuiComponents.IconButton(50, arrowIcon))
            group.IsExpanded = !group.IsExpanded;
        ImGui.PopStyleColor(3);

        // Group name
        ImGui.SameLine();
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 2);
        var headerPad = 6f;
        var layerCountText = $"({group.Layers.Count})";
        var layerCountWidth = ImGui.CalcTextSize(layerCountText).X;
        var headerAddBtnSize = ImGui.GetFrameHeight();
        var headerClusterWidth = layerCountWidth + ImGui.GetStyle().ItemSpacing.X + headerAddBtnSize;
        var nameWidth = availWidth - (ImGui.GetCursorScreenPos().X - headerStart.X) - headerClusterWidth - (headerPad * 2f);
        if (nameWidth < 20) nameWidth = 20;
        if (ImGui.Selectable($"{group.Name}##grpHdr", false, ImGuiSelectableFlags.None,
            new Vector2(nameWidth, ImGui.GetTextLineHeight())))
        {
            project.SelectedGroupIndex = gi;
            group.SelectedLayerIndex = -1;
        }

        // Header-right: layer count + add layer button
        var headerRightX = headerStart.X + availWidth - headerClusterWidth - headerPad;
        ImGui.SetCursorScreenPos(new Vector2(headerRightX, headerStart.Y + 2f));
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(layerCountText);
        ImGui.SameLine();
        if (ImGuiComponents.IconButton(20, FontAwesomeIcon.Plus))
        {
            project.SelectedGroupIndex = gi;
            layerCounter++;
            var newLayer = group.AddLayer(Strings.T("layer.default.name", layerCounter));
            var (tw, th) = previewService.GetBaseTextureSize(group);
            float texAspect = (tw > 0 && th > 0) ? (float)tw / th : 1f;
            newLayer.UvScale = new Vector2(newLayer.UvScale.X, newLayer.UvScale.X * texAspect);
            SyncImagePathBuf();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Strings.T("tooltip.add_layer"));

        // Tooltip
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            if (!string.IsNullOrEmpty(group.DiffuseGamePath))
                ImGui.Text(Strings.T("tooltip_group.diffuse", group.DiffuseGamePath));
            if (!string.IsNullOrEmpty(group.NormGamePath))
                ImGui.Text(Strings.T("tooltip_group.normal", group.NormGamePath));
            if (!string.IsNullOrEmpty(group.MtrlGamePath))
                ImGui.Text(Strings.T("tooltip_group.material", group.MtrlGamePath));
            ImGui.TextDisabled(Strings.T("tooltip_group.copy_hint"));
            ImGui.EndTooltip();
        }

        // Right-click context menu
        if (ImGui.BeginPopupContextItem($"##grpCtx{gi}"))
        {
            var hasDiffuse = !string.IsNullOrEmpty(group.DiffuseGamePath);
            using (ImRaii.Disabled(!hasDiffuse))
                if (ImGui.MenuItem(Strings.T("menu.copy_diffuse_path")))
                    ImGui.SetClipboardText(group.DiffuseGamePath ?? "");

            var hasNorm = !string.IsNullOrEmpty(group.NormGamePath);
            using (ImRaii.Disabled(!hasNorm))
                if (ImGui.MenuItem(Strings.T("menu.copy_normal_path")))
                    ImGui.SetClipboardText(group.NormGamePath ?? "");

            var hasMtrl = !string.IsNullOrEmpty(group.MtrlGamePath);
            using (ImRaii.Disabled(!hasMtrl))
                if (ImGui.MenuItem(Strings.T("menu.copy_material_path")))
                    ImGui.SetClipboardText(group.MtrlGamePath ?? "");

            ImGui.Separator();

            if (ImGui.MenuItem(Strings.T("menu.copy_decal_group")))
                CopyDecalGroup(group);

            using (ImRaii.Disabled(copiedGroupLayers == null))
                if (ImGui.MenuItem(Strings.T("menu.paste_decal_group")))
                    PasteDecalGroup(gi);
            ImGui.EndPopup();
        }

        // Ensure cursor is past header
        ImGui.SetCursorScreenPos(new Vector2(headerStart.X, headerEnd.Y));

        if (!group.IsExpanded) return;

        // -- Card body --
        var bodyStart = ImGui.GetCursorScreenPos();

        drawList.ChannelsSplit(2);
        drawList.ChannelsSetCurrent(1);

        ImGui.Indent(6);

        // Layer rows
        var deleteLayerIndex = -1;
        var duplicateLayerIndex = -1;
        //for (var li = 0; li < group.Layers.Count; li++)
        //{
        //    var layer = group.Layers[li];
        //    ImGui.PushID(li + 1000);

        //    var isLayerSelected = isGroupSelected && group.SelectedLayerIndex == li;
        //    var canHighlight = !string.IsNullOrEmpty(layer.ImagePath) && !string.IsNullOrEmpty(group.MtrlGamePath);
        //    var isThisHighlighted = highlightActive && highlightGroupIndex == gi && highlightLayerIndex == li;

        //    if (isLayerSelected)
        //    {
        //        var rowPos = ImGui.GetCursorScreenPos();
        //        drawList.AddRectFilled(
        //            rowPos - new Vector2(2, 1),
        //            rowPos + new Vector2(ImGui.GetContentRegionAvail().X, ImGui.GetFrameHeight()),
        //            ImGui.GetColorU32(new Vector4(0.24f, 0.42f, 0.65f, 0.5f)), 3f);
        //    }

        //    // Visibility toggle
        //    var visIcon = layer.IsVisible ? FontAwesomeIcon.Eye : FontAwesomeIcon.EyeSlash;
        //    var visColor = layer.IsVisible ? new Vector4(1, 1, 1, 1) : new Vector4(0.5f, 0.5f, 0.5f, 1);
        //    ImGui.PushStyleColor(ImGuiCol.Text, visColor);
        //    if (ImGuiComponents.IconButton(100 + li, visIcon))
        //    {
        //        layer.IsVisible = !layer.IsVisible;
        //        // Force a Full Redraw so the toggle also propagates to materials that
        //        // live in a different TargetGroup but share this group's texture state.
        //        // Inplace GPU swap only covers the current group and misses those cases.
        //        if (layer.AffectsEmissive || layer.RequiresRowPair)
        //            previewService.InvalidateEmissiveForGroup(group);
        //        previewService.ForceFullRedrawNextCycle();
        //        MarkPreviewDirty(immediate: true);
        //    }
        //    ImGui.PopStyleColor();

        //    ImGui.SameLine();

        //    var rowStart = ImGui.GetCursorScreenPos();
        //    var rowAvailWidth = ImGui.GetContentRegionAvail().X;
        //    var buttonSize = ImGui.GetFrameHeight();
        //    var buttonGap = ImGui.GetStyle().ItemSpacing.X;

        //    int controlCount = 0;
        //    if (isLayerSelected)
        //    {
        //        if (li > 0) controlCount++;
        //        if (li < group.Layers.Count - 1) controlCount++;
        //        controlCount++; // ellipsis
        //    }

        //    var controlsWidth = controlCount > 0
        //        ? controlCount * buttonSize + controlCount * buttonGap
        //        : 0f;

        //    var totalAvail = ImGui.GetContentRegionAvail().X;
        //    var selectableWidth = totalAvail - controlsWidth - (controlCount > 0 ? 8f : 0f);

        //    if (ImGui.Selectable(layer.Name, isLayerSelected, ImGuiSelectableFlags.None, new Vector2(selectableWidth, 0f)))
        //    {
        //        project.SelectedGroupIndex = gi;
        //        group.SelectedLayerIndex = li;
        //        SyncImagePathBuf();
        //    }

        //    if (isLayerSelected)
        //    {
        //        var controlsStartX = rowStart.X + totalAvail - controlsWidth;
        //        float cursorX = controlsStartX;
        //        float controlsY = rowStart.Y;

        //        if (li > 0)
        //        {
        //            ImGui.SetCursorScreenPos(new Vector2(cursorX, controlsY));
        //            if (ImGuiComponents.IconButton(220 + li, FontAwesomeIcon.ArrowUp))
        //            {
        //                group.MoveLayerUp(li);
        //                SyncImagePathBuf();
        //                MarkPreviewDirty();
        //            }
        //            cursorX += buttonSize + buttonGap;
        //        }

        //        if (li < group.Layers.Count - 1)
        //        {
        //            ImGui.SetCursorScreenPos(new Vector2(cursorX, controlsY));
        //            if (ImGuiComponents.IconButton(240 + li, FontAwesomeIcon.ArrowDown))
        //            {
        //                group.MoveLayerDown(li);
        //                SyncImagePathBuf();
        //                MarkPreviewDirty();
        //            }
        //            cursorX += buttonSize + buttonGap;
        //        }

        //        ImGui.SetCursorScreenPos(new Vector2(cursorX, controlsY));
        //        if (ImGuiComponents.IconButton(260 + li, FontAwesomeIcon.EllipsisV))
        //            ImGui.OpenPopup("##LayerActions");

        //        if (ImGui.BeginPopup("##LayerActions"))
        //        {
        //            if (ImGui.MenuItem(Strings.T("menu.duplicate_layer")))
        //                duplicateLayerIndex = li;

        //            using (ImRaii.Disabled(!canHighlight))
        //            {
        //                if (ImGui.MenuItem(isThisHighlighted ? Strings.T("menu.highlight_off") : Strings.T("menu.highlight_on")))
        //                    ToggleLayerHighlight(group, gi, li, isThisHighlighted);
        //            }

        //            var io = ImGui.GetIO();
        //            var canDelete = io.KeyCtrl && io.KeyShift;
        //            using (ImRaii.Disabled(!canDelete))
        //            {
        //                if (ImGui.MenuItem(Strings.T("menu.delete_layer")))
        //                    deleteLayerIndex = li;
        //            }

        //            ImGui.EndPopup();
        //        }

        //        ImGui.SameLine();
        //        ImGui.Dummy(new Vector2(buttonGap, 0f));
        //    }

        //    ImGui.PopID();
        //}

        ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(6, 4)); // horizontal, vertical
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(4, 3)); // fixes icon centering

        if (ImGui.BeginTable("LayersTable", 4,
            ImGuiTableFlags.RowBg |
            ImGuiTableFlags.SizingStretchSame |
            ImGuiTableFlags.BordersInnerV))
        {
            float buttonSize = ImGui.GetFrameHeight();

            ImGui.TableSetupColumn("Vis", ImGuiTableColumnFlags.WidthFixed, buttonSize+6f);
            ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthFixed); // auto/min content
            ImGui.TableSetupColumn("Controls", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Menu", ImGuiTableColumnFlags.WidthFixed, buttonSize+6f);

            for (int li = 0; li < group.Layers.Count; li++)
            {
                var layer = group.Layers[li];
                ImGui.PushID(li + 1000);

                bool isLayerSelected = isGroupSelected && group.SelectedLayerIndex == li;
                bool canHighlight = !string.IsNullOrEmpty(layer.ImagePath) && !string.IsNullOrEmpty(group.MtrlGamePath);
                bool isThisHighlighted = highlightActive && highlightGroupIndex == gi && highlightLayerIndex == li;

                ImGui.TableNextRow();

                // --- COLUMN 1: VISIBILITY ---
                ImGui.TableSetColumnIndex(0);

                var visIcon = layer.IsVisible ? FontAwesomeIcon.Eye : FontAwesomeIcon.EyeSlash;
                var visColor = layer.IsVisible ? new Vector4(1, 1, 1, 1) : new Vector4(0.5f, 0.5f, 0.5f, 1);

                ImGui.PushStyleColor(ImGuiCol.Text, visColor);
                if (ImGuiComponents.IconButton(100 + li, visIcon))
                {
                    layer.IsVisible = !layer.IsVisible;

                    if (layer.AffectsEmissive || layer.RequiresRowPair)
                        previewService.InvalidateEmissiveForGroup(group);

                    previewService.ForceFullRedrawNextCycle();
                    MarkPreviewDirty(immediate: true);
                }
                ImGui.PopStyleColor();

                // --- COLUMN 2: NAME (selectable) ---
                ImGui.TableSetColumnIndex(1);

                if (ImGui.Selectable(layer.Name, isLayerSelected,
                    ImGuiSelectableFlags.SpanAllColumns))
                {
                    project.SelectedGroupIndex = gi;
                    group.SelectedLayerIndex = li;
                    SyncImagePathBuf();
                }
                ImGui.SetItemAllowOverlap();

                // --- COLUMN 3: MOVE BUTTONS (LEFT ALIGNED, STRETCH COLUMN) ---
                ImGui.TableSetColumnIndex(2);

                using (ImRaii.Disabled(li <= 0))
                {
                    if (ImGuiComponents.IconButton(220 + li, FontAwesomeIcon.ArrowUp))
                    {
                        group.MoveLayerUp(li);
                        SyncImagePathBuf();
                        MarkPreviewDirty();
                    }
                }

                ImGui.SameLine();

                using (ImRaii.Disabled(li >= group.Layers.Count - 1))
                {
                    if (ImGuiComponents.IconButton(240 + li, FontAwesomeIcon.ArrowDown))
                    {
                        group.MoveLayerDown(li);
                        SyncImagePathBuf();
                        MarkPreviewDirty();
                    }
                }

                // --- COLUMN 4: ELLIPSIS (RIGHT-ALIGNED FIXED) ---
                ImGui.TableSetColumnIndex(3);

                // right-align inside the column
                ImGui.SetCursorPosX(
                    ImGui.GetCursorPosX() +
                    ImGui.GetColumnWidth() -
                    ImGui.GetFrameHeight() -
                    ImGui.GetStyle().CellPadding.X
                );

                if (ImGuiComponents.IconButton(260 + li, FontAwesomeIcon.EllipsisV))
                {
                    project.SelectedGroupIndex = gi;
                    group.SelectedLayerIndex = li;
                    SyncImagePathBuf();
                    ImGui.OpenPopup("##LayerActions");
                }

                if (ImGui.BeginPopup("##LayerActions"))
                {
                    if (ImGui.MenuItem(Strings.T("menu.duplicate_layer")))
                        duplicateLayerIndex = li;

                    using (ImRaii.Disabled(!canHighlight))
                    {
                        if (ImGui.MenuItem(isThisHighlighted
                            ? Strings.T("menu.highlight_off")
                            : Strings.T("menu.highlight_on")))
                        {
                            ToggleLayerHighlight(group, gi, li, isThisHighlighted);
                        }
                    }

                    var io = ImGui.GetIO();
                    bool canDelete = io.KeyCtrl && io.KeyShift;

                    using (ImRaii.Disabled(!canDelete))
                    {
                        if (ImGui.MenuItem(Strings.T("menu.delete_layer")))
                            deleteLayerIndex = li;
                    }

                    ImGui.EndPopup();
                }

                ImGui.PopID();
            }

            ImGui.EndTable();
            ImGui.PopStyleVar(2);
        }

        if (duplicateLayerIndex >= 0 && duplicateLayerIndex < group.Layers.Count)
            DuplicateLayer(group, duplicateLayerIndex);

        if (deleteLayerIndex >= 0 && deleteLayerIndex < group.Layers.Count)
            DeleteLayer(group, deleteLayerIndex);

        ImGui.Unindent(6);
        ImGui.Spacing();

        // Draw body background on channel 0
        var bodyEnd = ImGui.GetCursorScreenPos();
        drawList.ChannelsSetCurrent(0);
        drawList.AddRectFilled(
            bodyStart,
            new Vector2(headerStart.X + availWidth, bodyEnd.Y),
            ImGui.GetColorU32(new Vector4(0.12f, 0.12f, 0.15f, 1f)), 4f, ImDrawFlags.RoundCornersBottom);
        drawList.AddRect(
            headerStart,
            new Vector2(headerStart.X + availWidth, bodyEnd.Y),
            ImGui.GetColorU32(new Vector4(0.28f, 0.28f, 0.32f, 1f)), 4f);
        drawList.ChannelsMerge();
    }

    private void CopyDecalGroup(TargetGroup group)
    {
        copiedGroupLayers = [];
        foreach (var layer in group.Layers)
            copiedGroupLayers.Add(layer.Clone());

        copiedGroupSelectedLayerIndex = group.SelectedLayerIndex;

        var (sw, sh) = previewService.GetBaseTextureSize(group);
        copiedGroupSrcAspect = (sw > 0 && sh > 0) ? (float)sw / sh : 0f;
    }

    private void DuplicateLayer(TargetGroup group, int index)
    {
        if (index < 0 || index >= group.Layers.Count)
            return;

        var clone = group.Layers[index].Clone();
        group.Layers.Insert(index + 1, clone);
        group.SelectedLayerIndex = index + 1;
        SyncImagePathBuf();
        MarkPreviewDirty();
    }

    private void DeleteLayer(TargetGroup group, int index)
    {
        if (index < 0 || index >= group.Layers.Count)
            return;

        var doomed = group.Layers[index];
        if (doomed.AffectsEmissive || doomed.RequiresRowPair)
            previewService.InvalidateEmissiveForGroup(group);
        previewService.ForceReleaseRowPair(group, doomed);

        group.RemoveLayer(index);
        MarkPreviewDirty(immediate: true);
    }

    private void ToggleLayerHighlight(TargetGroup group, int groupIndex, int layerIndex, bool isThisHighlighted)
    {
        if (isThisHighlighted)
        {
            highlightActive = false;
            highlightGroupIndex = -1;
            highlightLayerIndex = -1;
            highlightFrameCounter = 0;
            RestoreEmissiveAfterHighlight(group);
            return;
        }

        if (highlightActive && highlightGroupIndex >= 0 && highlightGroupIndex < project.Groups.Count)
            RestoreEmissiveAfterHighlight(project.Groups[highlightGroupIndex]);

        if (!previewService.HasEmissiveOffset(group.MtrlGamePath))
            previewService.EnsureEmissiveInitialized(group);

        highlightActive = true;
        highlightGroupIndex = groupIndex;
        highlightLayerIndex = layerIndex;
        highlightFrameCounter = 0;
    }

    private void PasteDecalGroup(int targetGroupIndex)
    {
        if (copiedGroupLayers == null || targetGroupIndex < 0 || targetGroupIndex >= project.Groups.Count)
            return;

        var targetGroup = project.Groups[targetGroupIndex];

        if (highlightActive && highlightGroupIndex == targetGroupIndex)
        {
            RestoreEmissiveAfterHighlight(targetGroup);
            highlightActive = false;
            highlightGroupIndex = -1;
            highlightLayerIndex = -1;
            highlightFrameCounter = 0;
        }

        previewService.InvalidateEmissiveForGroup(targetGroup);
        foreach (var layer in targetGroup.Layers)
            previewService.ForceReleaseRowPair(targetGroup, layer);

        var (dw, dh) = previewService.GetBaseTextureSize(targetGroup);
        float dstAspect = (dw > 0 && dh > 0) ? (float)dw / dh : 0f;
        // Remap UvScale.Y so decals keep their pixel-space aspect across
        // materials with different texture sizes (e.g. 1024x1024 -> 1024x2048).
        bool needRescale = copiedGroupSrcAspect > 0f && dstAspect > 0f
                           && MathF.Abs(copiedGroupSrcAspect - dstAspect) > 1e-4f;

        var clipboardGroup = new TargetGroup { SelectedLayerIndex = copiedGroupSelectedLayerIndex };
        foreach (var layer in copiedGroupLayers)
        {
            var cloned = layer.Clone();
            if (needRescale)
                cloned.UvScale = new Vector2(cloned.UvScale.X,
                    cloned.UvScale.Y * dstAspect / copiedGroupSrcAspect);
            clipboardGroup.Layers.Add(cloned);
        }
        targetGroup.ReplaceLayersFrom(clipboardGroup);

        project.SelectedGroupIndex = targetGroupIndex;
        SyncImagePathBuf();
        previewService.ForceFullRedrawNextCycle();
        MarkPreviewDirty(immediate: true);
    }
}
