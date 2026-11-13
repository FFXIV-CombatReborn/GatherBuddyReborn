using Dalamud.Bindings.ImGui;
using RebornMaterial;
using System;
using System.Numerics;

namespace GatherBuddy.Gui;

internal static class VulcanUiStyle
{
    internal static Vector4 PanelBackground
        => M3.Alpha(M3.Scheme.SurfaceContainerLowest, 0.92f);

    internal static Vector4 SurfaceBackground
        => M3.Alpha(M3.Scheme.SurfaceContainerLow, 0.88f);

    internal static IDisposable PushTheme()
    {
        var s = M3.Scheme;
        ImGui.PushStyleColor(ImGuiCol.ChildBg, SurfaceBackground);
        ImGui.PushStyleColor(ImGuiCol.Border, M3.Alpha(s.OutlineVariant, 0.6f));
        ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, 1f);
        return new StyleScope(2, 1);
    }

    internal static Action Panels(Action draw)
        => () =>
        {
            using var theme = PushTheme();
            draw();
        };

    private sealed class StyleScope(int colorCount, int styleCount) : IDisposable
    {
        private int _colorCount = colorCount;
        private int _styleCount = styleCount;

        public void Dispose()
        {
            if (_styleCount > 0)
            {
                ImGui.PopStyleVar(_styleCount);
                _styleCount = 0;
            }

            if (_colorCount <= 0)
            {
                return;
            }

            ImGui.PopStyleColor(_colorCount);
            _colorCount = 0;
        }
    }
}
