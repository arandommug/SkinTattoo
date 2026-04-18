using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using SkinTattoo.Services.Localization;

namespace SkinTattoo.Gui;

public partial class MainWindow
{
    private void DrawChangelogTab()
    {
        using var scroll = ImRaii.Child("##ChangelogScroll", new Vector2(-1, -1), false);
        if (!scroll.Success) return;

        var entries = changelogService.Entries;
        if (entries.Count == 0)
        {
            ImGui.TextDisabled(Strings.T("changelog.empty"));
            return;
        }

        var lang = Strings.Manager.CurrentLanguage;
        var versionColor = new Vector4(1f, 0.8f, 0.3f, 1f);
        var dateColor = new Vector4(0.6f, 0.6f, 0.6f, 1f);

        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var header = $"v{entry.Version}##chglog{i}";
            var flags = i == 0 ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None;

            ImGui.PushStyleColor(ImGuiCol.Text, versionColor);
            var open = ImGui.CollapsingHeader(header, flags);
            ImGui.PopStyleColor();

            if (open)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, dateColor);
                ImGui.TextUnformatted(entry.Date);
                ImGui.PopStyleColor();
                ImGui.Spacing();

                foreach (var bullet in entry.BulletsFor(lang))
                    ImGui.BulletText(bullet);

                ImGui.Spacing();
            }
        }
    }
}
