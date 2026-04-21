using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace GatherBuddy.Gui;

internal static class VulcanUiStyle
{
    internal static readonly Vector4 PanelBackground = new(0.08f, 0.08f, 0.10f, 1f);
    internal static readonly Vector4 SurfaceBackground = new(0.15f, 0.15f, 0.18f, 1f);
    private static readonly Vector4 SurfaceHovered = new(0.18f, 0.18f, 0.22f, 1f);
    private static readonly Vector4 SurfaceActive = new(0.22f, 0.22f, 0.27f, 1f);
    private static readonly Vector4 PopupBackground = new(0.10f, 0.10f, 0.13f, 0.98f);
    private static readonly Vector4 HeaderBackground = new(0.17f, 0.17f, 0.21f, 1f);
    private static readonly Vector4 HeaderHovered = new(0.23f, 0.23f, 0.29f, 1f);
    private static readonly Vector4 HeaderActive = new(0.28f, 0.28f, 0.35f, 1f);
    private static readonly Vector4 BorderColor = new(0.32f, 0.32f, 0.39f, 0.85f);
    private static readonly Vector4 SeparatorColor = new(0.25f, 0.25f, 0.31f, 1f);
    private static readonly Vector4 TableHeaderBackground = new(0.12f, 0.12f, 0.15f, 1f);

    internal static IDisposable PushTheme()
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, SurfaceBackground);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, SurfaceBackground);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, SurfaceHovered);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, SurfaceActive);
        ImGui.PushStyleColor(ImGuiCol.PopupBg, PopupBackground);
        ImGui.PushStyleColor(ImGuiCol.Header, HeaderBackground);
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, HeaderHovered);
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, HeaderActive);
        ImGui.PushStyleColor(ImGuiCol.Border, BorderColor);
        ImGui.PushStyleColor(ImGuiCol.Separator, SeparatorColor);
        ImGui.PushStyleColor(ImGuiCol.SeparatorHovered, HeaderHovered);
        ImGui.PushStyleColor(ImGuiCol.SeparatorActive, HeaderActive);
        ImGui.PushStyleColor(ImGuiCol.TableHeaderBg, TableHeaderBackground);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupBorderSize, 1f);
        return new StyleScope(13, 3);
    }

    internal static IDisposable PushPanel()
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, PanelBackground);
        return new StyleScope(1, 0);
    }

    private sealed class StyleScope : IDisposable
    {
        private int _colorCount;
        private int _styleCount;

        public StyleScope(int colorCount, int styleCount)
        {
            _colorCount = colorCount;
            _styleCount = styleCount;
        }

        public void Dispose()
        {
            if (_styleCount > 0)
            {
                ImGui.PopStyleVar(_styleCount);
                _styleCount = 0;
            }
            if (_colorCount <= 0)
                return;

            ImGui.PopStyleColor(_colorCount);
            _colorCount = 0;
        }
    }
}
