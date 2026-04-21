using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace SkinTattoo.Gui;

// Mirrors CustomizePlus's CtrlHelper pattern: a real InfoCircle icon for help,
// rendered before the visible text. The first arg is the ImGui id (with ##),
// the second arg is the visible label.
internal static class UiHelpers
{
    private const float HelpWrapMul = 35f;

    public static bool CheckboxWithTextAndHelp(string id, string text, string helpText, ref bool value)
    {
        var changed = ImGui.Checkbox(id, ref value);
        ImGui.SameLine();
        DrawInfoIcon();
        AddHoverText(helpText);
        ImGui.SameLine();
        ImGui.TextUnformatted(StripImGuiId(text));
        AddHoverText(helpText);
        return changed;
    }

    private static string StripImGuiId(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var idx = text.IndexOf("##", System.StringComparison.Ordinal);
        return idx < 0 ? text : text[..idx];
    }

    public static void LabelWithHelp(string text, string helpText)
    {
        DrawInfoIcon();
        AddHoverText(helpText);
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(text);
        AddHoverText(helpText);
    }

    public static void DrawInfoIcon()
    {
        // Align with adjacent frame widgets (Checkbox / Button) so the icon sits on the
        // text baseline instead of hugging the top of the line.
        ImGui.AlignTextToFramePadding();
        ImGui.PushFont(UiBuilder.IconFont);
        ImGui.TextDisabled(FontAwesomeIcon.InfoCircle.ToIconString());
        ImGui.PopFont();
    }

    public static void AddHoverText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (!ImGui.IsItemHovered()) return;
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * HelpWrapMul);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    // Square icon button with explicit size, so layouts can predict exact width.
    // Default size = current frame height, matching surrounding form controls.
    // Icon is drawn manually via drawList so it lands on the geometric centre of the
    // button regardless of glyph asymmetry (ImGui.Button + ButtonTextAlign relies on
    // the glyph's bbox which is uneven for many FontAwesome icons).
    public static bool SquareIconButton(int id, FontAwesomeIcon icon, float size)
    {
        var startPos = ImGui.GetCursorScreenPos();
        var btnSize = new System.Numerics.Vector2(size, size);
        var clicked = ImGui.Button($"##sq{id}", btnSize);

        ImGui.PushFont(UiBuilder.IconFont);
        var iconStr = icon.ToIconString();
        var iconSize = ImGui.CalcTextSize(iconStr);
        var iconPos = new System.Numerics.Vector2(
            startPos.X + (size - iconSize.X) * 0.5f,
            startPos.Y + (size - iconSize.Y) * 0.5f);
        ImGui.GetWindowDrawList().AddText(iconPos, ImGui.GetColorU32(ImGuiCol.Text), iconStr);
        ImGui.PopFont();
        return clicked;
    }

    public static bool SquareIconButton(int id, FontAwesomeIcon icon)
        => SquareIconButton(id, icon, ImGui.GetFrameHeight());

    public static bool SquareIconButton(string id, FontAwesomeIcon icon)
        => SquareIconButton(id.GetHashCode(), icon, ImGui.GetFrameHeight());
}
