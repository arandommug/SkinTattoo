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
using SkinTattoo.Core;
using SkinTattoo.Services;
using SkinTattoo.Services.Localization;

namespace SkinTattoo.Gui;

public sealed class LibraryWindow : Window
{
    private enum LibraryViewMode
    {
        Large = 0,
        Medium = 1,
        Small = 2,
        Detail = 3,
    }

    private readonly LibraryService library;
    private readonly ITextureProvider textureProvider;
    private readonly Configuration config;
    private readonly FileDialogManager fileDialog = new();

    private string search = string.Empty;
    private string? pendingDeleteHash;
    private string? selectedEntryHash;
    private string selectedFolder = string.Empty;
    private string createFolderInput = string.Empty;
    private LibraryViewMode viewMode;

    public Action<LibraryEntry>? OnPicked { get; set; }

    public void SetSelectedEntry(string? hash)
    {
        selectedEntryHash = string.IsNullOrWhiteSpace(hash) ? null : hash;
    }

    public LibraryWindow(LibraryService library, ITextureProvider textureProvider, Configuration config)
        : base(Strings.T("window.library.title") + "###SkinTattooLibrary",
               ImGuiWindowFlags.NoScrollbar)
    {
        this.library = library;
        this.textureProvider = textureProvider;
        this.config = config;
        viewMode = NormalizeViewMode(config.LibraryViewMode);

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(480, 360),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void Draw()
    {
        fileDialog.Draw();

        if (ImGuiComponents.IconButton(FontAwesomeIcon.FileImport))
            OpenImportFilesDialog();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Strings.T("library.tooltip.import"));

        ImGui.SameLine();
        if (ImGuiComponents.IconButton(FontAwesomeIcon.FolderOpen))
            OpenImportFolderDialog();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Strings.T("library.tooltip.import_folder"));

        ImGui.SameLine();
        if (ImGuiComponents.IconButton(FontAwesomeIcon.FolderPlus))
            ImGui.OpenPopup("##libCreateFolder");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Strings.T("library.tooltip.create_folder"));

        ImGui.SameLine();
        DrawViewModePicker();

        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##LibrarySearch", Strings.T("library.search_hint"), ref search, 128);

        ImGui.Separator();

        DrawCreateFolderPopup();

        var allEntries = library.Snapshot();
        var folders = library.SnapshotFolders();
        using (var contentPane = ImRaii.Child("##LibraryContent", new Vector2(-1, -1), false))
        {
            if (!contentPane.Success) return;

            var currentFolder = LibraryService.NormalizeFolderPath(selectedFolder);
            selectedFolder = currentFolder;

            DrawFolderBreadcrumbs(currentFolder);
            ImGui.Separator();

            var childFolders = folders
                .Where(f => IsDirectChildFolder(currentFolder, f))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var entries = new List<LibraryEntry>();
            foreach (var e in allEntries)
                if (string.Equals(LibraryService.NormalizeFolderPath(e.FolderPath), currentFolder, StringComparison.OrdinalIgnoreCase))
                    entries.Add(e);

            if (!string.IsNullOrEmpty(currentFolder))
            {
                entries = entries
                    .OrderBy(e => e.OriginalName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(e => e.AddedAt)
                    .ToList();
            }

            if (viewMode == LibraryViewMode.Detail)
                DrawDetailMode(allEntries, childFolders, entries);
            else
                DrawGridMode(childFolders, entries, GetCellSize(viewMode));

            DrawDeleteConfirm();
        }
    }

    private readonly record struct FolderAggregate(string SizeText, string AddedText);

    private void DrawViewModePicker()
    {
        var current = NormalizeViewMode(config.LibraryViewMode);
        if (current != viewMode)
            viewMode = current;

        ImGui.SetNextItemWidth(130f);
        if (!ImGui.BeginCombo("##LibraryViewMode", GetViewModeLabel(viewMode)))
            return;

        foreach (LibraryViewMode mode in Enum.GetValues<LibraryViewMode>())
        {
            var selected = mode == viewMode;
            if (ImGui.Selectable(GetViewModeLabel(mode), selected))
            {
                viewMode = mode;
                config.LibraryViewMode = (int)mode;
                config.Save();
            }

            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }

    private static LibraryViewMode NormalizeViewMode(int raw)
    {
        return Enum.IsDefined(typeof(LibraryViewMode), raw)
            ? (LibraryViewMode)raw
            : LibraryViewMode.Medium;
    }

    private static float GetCellSize(LibraryViewMode mode)
    {
        return mode switch
        {
            LibraryViewMode.Large => 128f,
            LibraryViewMode.Medium => 96f,
            LibraryViewMode.Small => 72f,
            _ => 96f,
        };
    }

    private static string GetViewModeLabel(LibraryViewMode mode)
    {
        return mode switch
        {
            LibraryViewMode.Large => Strings.T("library.view.large"),
            LibraryViewMode.Medium => Strings.T("library.view.medium"),
            LibraryViewMode.Small => Strings.T("library.view.small"),
            LibraryViewMode.Detail => Strings.T("library.view.detail"),
            _ => Strings.T("library.view.medium"),
        };
    }

    private void DrawGridMode(IReadOnlyList<string> childFolders, IReadOnlyList<LibraryEntry> entries, float cellSize)
    {
        const float cellPad = 12f;
        const int maxLabelLines = 3;

        var avail = ImGui.GetContentRegionAvail().X;
        var cols = Math.Max(1, (int)(avail / (cellSize + cellPad)));

        using var child = ImRaii.Child("##LibraryGrid", new Vector2(-1, -1), false);
        if (!child.Success) return;

        int drawn = 0;

        foreach (var folder in childFolders)
        {
            var folderName = GetFolderName(folder);
            if (!MatchesFolderSearch(folderName))
                continue;

            if (drawn % cols != 0) ImGui.SameLine();
            DrawFolderCell(folder, folderName, cellSize, maxLabelLines);
            drawn++;
        }

        foreach (var entry in entries)
        {
            if (!MatchesEntrySearch(entry))
                continue;

            if (drawn % cols != 0) ImGui.SameLine();
            DrawCell(entry, cellSize, maxLabelLines);
            drawn++;
        }

        if (drawn == 0)
            ImGui.TextDisabled(Strings.T("library.empty"));
    }

    private void DrawDetailMode(IReadOnlyList<LibraryEntry> allEntries, IReadOnlyList<string> childFolders, IReadOnlyList<LibraryEntry> entries)
    {
        using var child = ImRaii.Child("##LibraryDetail", new Vector2(-1, -1), false);
        if (!child.Success) return;

        bool drewAny = false;

        if (ImGui.BeginTable("##LibraryDetailTable", 4,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY))
        {
            ImGui.TableSetupColumn(Strings.T("library.detail.name"), ImGuiTableColumnFlags.WidthStretch, 0.45f);
            ImGui.TableSetupColumn(Strings.T("library.detail.type"), ImGuiTableColumnFlags.WidthStretch, 0.18f);
            ImGui.TableSetupColumn(Strings.T("library.detail.size"), ImGuiTableColumnFlags.WidthStretch, 0.18f);
            ImGui.TableSetupColumn(Strings.T("library.detail.added"), ImGuiTableColumnFlags.WidthStretch, 0.19f);
            ImGui.TableHeadersRow();

            foreach (var folder in childFolders)
            {
                var folderName = GetFolderName(folder);
                if (!MatchesFolderSearch(folderName))
                    continue;

                var aggregate = BuildFolderAggregate(folder, allEntries);

                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                DrawNameTypeIcon(FontAwesomeIcon.Folder);
                ImGui.SameLine();
                if (ImGui.Selectable(folderName + "##folder_row_" + folder, false))
                    selectedFolder = folder;

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(Strings.T("library.detail.folder_type"));

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(aggregate.SizeText);

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(aggregate.AddedText);
                drewAny = true;
            }

            foreach (var entry in entries)
            {
                var type = GetEntryTypeText(entry);
                if (!MatchesEntrySearch(entry, type))
                    continue;

                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                DrawNameTypeIcon(FontAwesomeIcon.FileImage);
                ImGui.SameLine();
                if (ImGui.Selectable(entry.OriginalName + "##file_row_" + entry.Hash, string.Equals(selectedEntryHash, entry.Hash, StringComparison.OrdinalIgnoreCase)))
                {
                    selectedEntryHash = entry.Hash;
                    OnPicked?.Invoke(entry);
                }

                if (ImGui.BeginPopupContextItem("##lib_ctx_" + entry.Hash))
                {
                    if (ImGui.MenuItem(Strings.T("library.menu.apply")))
                    {
                        selectedEntryHash = entry.Hash;
                        OnPicked?.Invoke(entry);
                    }

                    ImGui.Separator();
                    if (ImGui.MenuItem(Strings.T("library.menu.delete")))
                        pendingDeleteHash = entry.Hash;
                    ImGui.EndPopup();
                }

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(type);

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(GetEntrySizeText(entry));

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(GetAddedText(entry.AddedAt));
                drewAny = true;
            }

            ImGui.EndTable();
        }

        if (!drewAny)
            ImGui.TextDisabled(Strings.T("library.empty"));
    }

    private void DrawCreateFolderPopup()
    {
        using var popup = ImRaii.Popup("##libCreateFolder");
        if (!popup.Success) return;

        ImGui.SetNextItemWidth(280f);
        ImGui.InputTextWithHint("##libFolderName", Strings.T("library.folder.name_hint"), ref createFolderInput, 256);

        if (ImGui.Button(Strings.T("button.confirm"), new Vector2(120, 0)))
        {
            var normalizedInput = LibraryService.NormalizeFolderPath(createFolderInput);
            if (!string.IsNullOrEmpty(normalizedInput))
            {
                var targetFolder = normalizedInput.Contains('/', StringComparison.Ordinal)
                    ? normalizedInput
                    : LibraryService.NormalizeFolderPath(string.IsNullOrEmpty(selectedFolder)
                        ? normalizedInput
                        : selectedFolder + "/" + normalizedInput);

                if (library.CreateFolder(targetFolder))
                    selectedFolder = targetFolder;
            }
            createFolderInput = string.Empty;
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button(Strings.T("button.cancel"), new Vector2(120, 0)))
        {
            createFolderInput = string.Empty;
            ImGui.CloseCurrentPopup();
        }
    }

    private void DrawFolderCell(string folderPath, string folderName, float size, int maxLabelLines)
    {
        ImGui.BeginGroup();
        var cursor = ImGui.GetCursorScreenPos();

        var clicked = ImGui.InvisibleButton("##folder_" + folderPath, new Vector2(size, size));
        var hovered = ImGui.IsItemHovered();

        var drawList = ImGui.GetWindowDrawList();
        var bg = hovered
            ? ImGui.GetColorU32(ImGuiCol.FrameBgHovered)
            : ImGui.GetColorU32(ImGuiCol.FrameBg);
        drawList.AddRectFilled(cursor, cursor + new Vector2(size, size), bg, 4f);

        DrawLargeFolderIcon(drawList, cursor, size, hovered);

        if (hovered)
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(folderName);
            ImGui.TextDisabled(folderPath);
            ImGui.EndTooltip();
        }

        if (clicked)
            selectedFolder = folderPath;

        DrawWrappedText(folderName, size, maxLabelLines);

        ImGui.EndGroup();
    }

    private static string GetFolderName(string folderPath)
    {
        if (string.IsNullOrEmpty(folderPath)) return string.Empty;
        var idx = folderPath.LastIndexOf('/');
        return idx < 0 ? folderPath : folderPath[(idx + 1)..];
    }

    private static bool IsDirectChildFolder(string parentFolder, string candidateFolder)
    {
        var parent = LibraryService.NormalizeFolderPath(parentFolder);
        var child = LibraryService.NormalizeFolderPath(candidateFolder);
        if (string.IsNullOrEmpty(child)) return false;

        if (string.IsNullOrEmpty(parent))
            return !child.Contains('/');

        if (!child.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase))
            return false;

        var remainder = child[(parent.Length + 1)..];
        return !remainder.Contains('/');
    }

    private void DrawFolderBreadcrumbs(string currentFolder)
    {
        var allLabel = Strings.T("library.folder.all");
        if (ImGui.SmallButton(allLabel + "##crumb_root"))
            selectedFolder = string.Empty;

        if (string.IsNullOrEmpty(currentFolder))
            return;

        var parts = currentFolder.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var path = string.Empty;
        foreach (var part in parts)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(">");
            ImGui.SameLine();

            path = string.IsNullOrEmpty(path) ? part : path + "/" + part;
            if (ImGui.SmallButton(part + "##crumb_" + path))
                selectedFolder = path;
        }
    }

    private void DrawCell(LibraryEntry entry, float size, int maxLabelLines)
    {
        ImGui.BeginGroup();
        var cursor = ImGui.GetCursorScreenPos();
        var isSelected = string.Equals(selectedEntryHash, entry.Hash, StringComparison.OrdinalIgnoreCase);

        var clicked = ImGui.InvisibleButton("##cell_" + entry.Hash, new Vector2(size, size));
        var hovered = ImGui.IsItemHovered();

        var drawList = ImGui.GetWindowDrawList();
        var bg = isSelected
            ? ImGui.GetColorU32(new Vector4(0.20f, 0.36f, 0.60f, 0.95f))
            : hovered
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
        {
            selectedEntryHash = entry.Hash;
            OnPicked?.Invoke(entry);
        }

        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            ImGui.OpenPopup("##lib_ctx_" + entry.Hash);

        if (ImGui.BeginPopup("##lib_ctx_" + entry.Hash))
        {
            if (ImGui.MenuItem(Strings.T("library.menu.apply")))
            {
                selectedEntryHash = entry.Hash;
                OnPicked?.Invoke(entry);
            }
            ImGui.Separator();
            if (ImGui.MenuItem(Strings.T("library.menu.delete")))
                pendingDeleteHash = entry.Hash;
            ImGui.EndPopup();
        }

        if (isSelected)
        {
            var border = ImGui.GetColorU32(new Vector4(0.95f, 0.80f, 0.20f, 1f));
            drawList.AddRect(cursor + new Vector2(1, 1), cursor + new Vector2(size - 1, size - 1), border, 4f, ImDrawFlags.None, 2f);
        }

        DrawWrappedText(entry.OriginalName, size, maxLabelLines);

        ImGui.EndGroup();
    }

    private void DrawLargeFolderIcon(ImDrawListPtr drawList, Vector2 cursor, float size, bool hovered)
    {
        var iconWidth = size * 0.84f;
        var iconHeight = size * 0.68f;
        var x = cursor.X + (size - iconWidth) * 0.5f;
        var y = cursor.Y + (size - iconHeight) * 0.5f;

        var bodyMin = new Vector2(x, y + iconHeight * 0.22f);
        var bodyMax = new Vector2(x + iconWidth, y + iconHeight);
        var tabMin = new Vector2(x + iconWidth * 0.08f, y);
        var tabMax = new Vector2(x + iconWidth * 0.48f, y + iconHeight * 0.32f);

        var bodyColor = hovered
            ? ImGui.GetColorU32(ImGuiCol.FrameBgHovered)
            : ImGui.GetColorU32(ImGuiCol.FrameBg);
        var tabColor = hovered
            ? ImGui.GetColorU32(ImGuiCol.Header)
            : ImGui.GetColorU32(ImGuiCol.TitleBg);
        var borderColor = ImGui.GetColorU32(ImGuiCol.Border);

        drawList.AddRectFilled(tabMin, tabMax, tabColor, 3f);
        drawList.AddRectFilled(bodyMin, bodyMax, bodyColor, 4f);
        drawList.AddRect(tabMin, tabMax, borderColor, 3f, ImDrawFlags.None, 1.2f);
        drawList.AddRect(bodyMin, bodyMax, borderColor, 4f, ImDrawFlags.None, 1.2f);
    }

    private static void DrawNameTypeIcon(FontAwesomeIcon icon)
    {
        using var _ = ImRaii.PushFont(UiBuilder.IconFont);
        ImGui.TextUnformatted(icon.ToIconString());
    }

    private void DrawWrappedText(string text, float width, int maxLines)
    {
        var lines = BuildWrappedLines(text, width, maxLines);
        foreach (var line in lines)
            ImGui.TextUnformatted(line);

        var missingLines = maxLines - lines.Count;
        if (missingLines > 0)
            ImGui.Dummy(new Vector2(width, missingLines * ImGui.GetTextLineHeightWithSpacing()));
    }

    private static List<string> BuildWrappedLines(string text, float width, int maxLines)
    {
        var result = new List<string>();
        if (maxLines <= 0)
            return result;

        var source = string.IsNullOrWhiteSpace(text)
            ? Strings.T("library.na")
            : text.Replace('\n', ' ').Trim();

        var remaining = source.Trim();
        while (!string.IsNullOrEmpty(remaining) && result.Count < maxLines)
        {
            if (ImGui.CalcTextSize(remaining).X <= width)
            {
                result.Add(remaining);
                remaining = string.Empty;
                break;
            }

            var fitLen = GetMaxFittingLength(remaining, width);
            var breakLen = GetPreferredBreakLength(remaining, fitLen);
            var line = remaining[..breakLen].TrimEnd();
            if (string.IsNullOrEmpty(line))
            {
                line = remaining[..Math.Max(1, fitLen)];
                breakLen = line.Length;
            }

            result.Add(line);
            remaining = remaining[breakLen..].TrimStart();
        }

        if (!string.IsNullOrEmpty(remaining) && result.Count > 0)
        {
            var last = result[^1].TrimEnd();
            while (last.Length > 1 && ImGui.CalcTextSize(last + "...").X > width)
                last = last[..^1];

            result[^1] = (string.IsNullOrWhiteSpace(last) ? Strings.T("library.na") : last) + "...";
        }

        return result;
    }

    private static int GetMaxFittingLength(string value, float width)
    {
        if (string.IsNullOrEmpty(value))
            return 0;

        int lo = 1;
        int hi = value.Length;
        int best = 1;

        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            var sample = value[..mid];
            if (ImGui.CalcTextSize(sample).X <= width)
            {
                best = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return best;
    }

    private static int GetPreferredBreakLength(string value, int fitLength)
    {
        if (fitLength >= value.Length)
            return value.Length;

        for (var i = fitLength - 1; i > 0; i--)
        {
            var c = value[i];
            if (char.IsWhiteSpace(c) || c == '_' || c == '-' || c == '.')
                return i + 1;
        }

        return Math.Max(1, fitLength);
    }

    private bool MatchesFolderSearch(string folderName)
    {
        if (string.IsNullOrWhiteSpace(search)) return true;
        return ContainsIgnoreCase(folderName, search)
               || ContainsIgnoreCase(Strings.T("library.detail.folder_type"), search);
    }

    private bool MatchesEntrySearch(LibraryEntry entry, string? type = null)
    {
        if (string.IsNullOrWhiteSpace(search)) return true;
        var name = string.IsNullOrWhiteSpace(entry.OriginalName) ? Strings.T("library.na") : entry.OriginalName;
        var typeText = type ?? GetEntryTypeText(entry);
        return ContainsIgnoreCase(name, search)
               || ContainsIgnoreCase(typeText, search);
    }

    private static bool ContainsIgnoreCase(string? value, string term)
    {
        if (string.IsNullOrEmpty(value)) return false;
        return value.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private FolderAggregate BuildFolderAggregate(string folderPath, IReadOnlyList<LibraryEntry> allEntries)
    {
        long totalSize = 0;
        bool hasSize = false;
        DateTime latestAdded = default;
        bool hasDate = false;

        foreach (var entry in allEntries)
        {
            var entryFolder = LibraryService.NormalizeFolderPath(entry.FolderPath);
            if (!IsDescendantOrSameFolder(folderPath, entryFolder))
                continue;

            if (TryGetEntrySize(entry, out var entrySize))
            {
                totalSize += entrySize;
                hasSize = true;
            }

            if (entry.AddedAt != default && (!hasDate || entry.AddedAt > latestAdded))
            {
                latestAdded = entry.AddedAt;
                hasDate = true;
            }
        }

        var size = hasSize ? FormatFileSize(totalSize) : Strings.T("library.na");
        var added = hasDate ? GetAddedText(latestAdded) : Strings.T("library.na");
        return new FolderAggregate(size, added);
    }

    private static bool IsDescendantOrSameFolder(string rootFolder, string candidateFolder)
    {
        var root = LibraryService.NormalizeFolderPath(rootFolder);
        var candidate = LibraryService.NormalizeFolderPath(candidateFolder);
        if (string.IsNullOrEmpty(candidate))
            return false;

        return string.Equals(root, candidate, StringComparison.OrdinalIgnoreCase)
               || candidate.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);
    }

    private string GetEntryTypeText(LibraryEntry entry)
    {
        var ext = Path.GetExtension(entry.OriginalName);
        if (string.IsNullOrWhiteSpace(ext))
            ext = Path.GetExtension(entry.FileName);
        if (string.IsNullOrWhiteSpace(ext))
            return Strings.T("library.na");

        return ext.ToLowerInvariant();
    }

    private string GetEntrySizeText(LibraryEntry entry)
    {
        return TryGetEntrySize(entry, out var size)
            ? FormatFileSize(size)
            : Strings.T("library.na");
    }

    private bool TryGetEntrySize(LibraryEntry entry, out long size)
    {
        size = 0;
        try
        {
            var diskPath = library.ResolveDiskPath(entry.Hash);
            if (string.IsNullOrWhiteSpace(diskPath) || !File.Exists(diskPath))
                return false;

            var info = new FileInfo(diskPath);
            if (info.Length < 0)
                return false;

            size = info.Length;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 0)
            return Strings.T("library.na");

        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < suffixes.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        var result = unit == 0 ? $"{bytes} {suffixes[unit]}" : $"{size:0.##} {suffixes[unit]}";
        return string.IsNullOrWhiteSpace(result) ? Strings.T("library.na") : result;
    }

    private static string GetAddedText(DateTime value)
    {
        if (value == default || value.Year < 1900)
            return Strings.T("library.na");

        try
        {
            return value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        }
        catch
        {
            return Strings.T("library.na");
        }
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

    private void OpenImportFilesDialog()
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
                    if (entry != null)
                    {
                        library.SetEntryFolder(entry.Hash, selectedFolder);
                        config.LastImageDir = Path.GetDirectoryName(p);
                    }
                }
                config.Save();
            },
            10, config.LastImageDir, true);
    }

    private void OpenImportFolderDialog()
    {
        fileDialog.OpenFolderDialog(
            Strings.T("dialog.select_folder"),
            (ok, path) =>
            {
                if (!ok || string.IsNullOrWhiteSpace(path)) return;
                if (!Directory.Exists(path)) return;

                library.ImportFolderTree(path);
                config.LastImageDir = path;
                config.Save();
            },
            config.LastImageDir,
            true);
    }
}
