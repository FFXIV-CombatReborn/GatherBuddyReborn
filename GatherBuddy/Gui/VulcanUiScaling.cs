using Dalamud.Interface.Utility;
using System.Numerics;

namespace GatherBuddy.Gui;

internal static class VulcanUiScaling
{
    internal static float Scale
        => ImGuiHelpers.GlobalScale;

    internal static float Scaled(float value)
        => value > 0f ? value * Scale : value;

    internal static Vector2 Scaled(float x, float y)
        => new(Scaled(x), Scaled(y));

    internal static Vector2 Scaled(Vector2 value)
        => new(Scaled(value.X), Scaled(value.Y));

    internal static Vector2 Unscaled(Vector2 value)
        => Scale > 0f ? value / Scale : value;
}
