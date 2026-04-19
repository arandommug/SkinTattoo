using System;
using System.IO;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using SkinTattoo.Core;
using SkinTattoo.Services;
using SkinTattoo.Services.Localization;

namespace SkinTattoo.Gui;

public sealed class LibraryWindow : Window
{
    private readonly LibraryService library;
    private readonly ITextureProvider textureProvider;
    private readonly Configuration config;
    private readonly FileDialogManager fileDialog = new();

    private string search = string.Empty;
    private string? pendingDeleteHash;

    public Action<LibraryEntry>? OnPicked { get; set; }

    public LibraryWindow(LibraryService library, ITextureProvider textureProvider, Configuration config)
        : base(Strings.T("window.library.title") + "###SkinTattooLibrary",
               ImGuiWindowFlags.NoScrollbar)
    {
        this.library = library;
        this.textureProvider = textureProvider;
        this.config = config;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(480, 360),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void Draw()
    {
        fileDialog.Draw();

        if (ImGuiComponents.IconButton(FontAwesomeIcon.FolderOpen))
            OpenImportDialog();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Strings.T("library.tooltip.import"));

        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##LibrarySearch", Strings.T("library.search_hint"), ref search, 128);

        ImGui.Separator();

        var entries = library.Snapshot();
        if (entries.Count == 0)
        {
            ImGui.TextDisabled(Strings.T("library.empty"));
            return;
        }

        const float cellSize = 96f;
        const float cellPad = 12f;
        var avail = ImGui.GetContentRegionAvail().X;
        var cols = Math.Max(1, (int)(avail / (cellSize + cellPad)));

        using var child = ImRaii.Child("##LibraryGrid", new Vector2(-1, -1), false);
        if (!child.Success) return;

        var hasSearch = !string.IsNullOrEmpty(search);
        int drawn = 0;
        foreach (var entry in entries)
        {
            if (hasSearch && entry.OriginalName.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            if (drawn % cols != 0) ImGui.SameLine();
            DrawCell(entry, cellSize);
            drawn++;
        }

        DrawDeleteConfirm();
    }

    private void DrawCell(LibraryEntry entry, float size)
    {
        ImGui.BeginGroup();
        var cursor = ImGui.GetCursorScreenPos();

        var clicked = ImGui.InvisibleButton("##cell_" + entry.Hash, new Vector2(size, size));
        var hovered = ImGui.IsItemHovered();

        var drawList = ImGui.GetWindowDrawList();
        var bg = hovered
            ? ImGui.GetColorU32(ImGuiCol.ButtonHovered)
            : ImGui.GetColorU32(ImGuiCol.FrameBg);
        drawList.AddRectFilled(cursor, cursor + new Vector2(size, size), bg, 4f);

        var thumbPath = library.ThumbPath(entry);
        if (!File.Exists(thumbPath))
            library.EnsureThumb(entry);
        if (File.Exists(thumbPath))
        {
            var wrap = textureProvider.GetFromFile(thumbPath).GetWrapOrDefault();
            if (wrap != null)
            {
                var iw = wrap.Width;
                var ih = wrap.Height;
                var maxDim = Math.Max(iw, ih);
                float scale = (size - 8f) / maxDim;
                var dw = iw * scale;
                var dh = ih * scale;
                var imgCursor = cursor + new Vector2((size - dw) * 0.5f, (size - dh) * 0.5f);
                drawList.AddImage(wrap.Handle, imgCursor, imgCursor + new Vector2(dw, dh));
            }
        }
        else
        {
            var loading = Strings.T("library.loading");
            var ts = ImGui.CalcTextSize(loading);
            drawList.AddText(cursor + new Vector2((size - ts.X) * 0.5f, (size - ts.Y) * 0.5f),
                ImGui.GetColorU32(ImGuiCol.TextDisabled), loading);
        }

        if (hovered)
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(entry.OriginalName);
            ImGui.TextDisabled($"{entry.Width} x {entry.Height}");
            ImGui.TextDisabled(Strings.T("library.tooltip.used_count", entry.UseCount));
            ImGui.EndTooltip();
        }

        if (clicked)
            OnPicked?.Invoke(entry);

        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            ImGui.OpenPopup("##lib_ctx_" + entry.Hash);

        if (ImGui.BeginPopup("##lib_ctx_" + entry.Hash))
        {
            if (ImGui.MenuItem(Strings.T("library.menu.apply")))
                OnPicked?.Invoke(entry);
            ImGui.Separator();
            if (ImGui.MenuItem(Strings.T("library.menu.delete")))
                pendingDeleteHash = entry.Hash;
            ImGui.EndPopup();
        }

        ImGui.EndGroup();
    }

    private void DrawDeleteConfirm()
    {
        if (pendingDeleteHash == null) return;
        var entry = library.Get(pendingDeleteHash);
        if (entry == null)
        {
            pendingDeleteHash = null;
            return;
        }

        ImGui.OpenPopup("##lib_delete_confirm");
        using var popup = ImRaii.PopupModal("##lib_delete_confirm",
            ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoTitleBar);
        if (!popup.Success) return;

        ImGui.TextUnformatted(Strings.T("library.delete_prompt", entry.OriginalName));
        ImGui.Spacing();

        if (ImGui.Button(Strings.T("button.confirm"), new Vector2(120, 0)))
        {
            library.Remove(entry.Hash);
            pendingDeleteHash = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button(Strings.T("button.cancel"), new Vector2(120, 0)))
        {
            pendingDeleteHash = null;
            ImGui.CloseCurrentPopup();
        }
    }

    private void OpenImportDialog()
    {
        fileDialog.OpenFileDialog(
            Strings.T("dialog.select_image"),
            "Image Files{.png,.jpg,.jpeg,.tga,.bmp,.dds}",
            (ok, paths) =>
            {
                if (!ok) return;
                foreach (var p in paths)
                {
                    var entry = library.ImportFromPath(p);
                    if (entry != null) config.LastImageDir = Path.GetDirectoryName(p);
                }
                config.Save();
            },
            10, config.LastImageDir, true);
    }
}
