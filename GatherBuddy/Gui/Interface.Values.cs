using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace GatherBuddy.Gui;

public partial class Interface
{
    private static Vector2 IconSize;
    private static Vector2 WeatherIconSize;
    private static Vector2 SmallIconSize;
    private static Vector2 LineIconSize;
    private static Vector2 ItemSpacing;
    private static Vector2 FramePadding;
    private static Vector2 IconButtonSize;
    private static float SelectorWidth;
    private static float SetInputWidth;
    private static Vector2 HorizontalSpace = Vector2.Zero;
    private static float TextHeight;
    private static float TextHeightSpacing;
    private static float Scale;

    private static void SetupValues()
    {
        Scale = ImGuiHelpers.GlobalScale;
        ItemSpacing = ImGui.GetStyle().ItemSpacing;
        FramePadding = ImGui.GetStyle().FramePadding;
        SelectorWidth = Math.Max(ImGui.GetWindowSize().X * 0.15f, 225 * Scale);
        SetInputWidth = 200f * ImGuiHelpers.GlobalScale;
        TextHeight = ImGui.GetTextLineHeight();
        TextHeightSpacing = ImGui.GetTextLineHeightWithSpacing();
        IconSize = ImGuiHelpers.ScaledVector2(40, 40);
        WeatherIconSize = ImGuiHelpers.ScaledVector2(30, 30);
        SmallIconSize = ImGuiHelpers.ScaledVector2(20, 20);
        LineIconSize = new Vector2(TextHeight, TextHeight);
        IconButtonSize = new Vector2(ImGui.GetFrameHeight(), 0);
    }

    private static float TextWidth(string text)
        => ImGui.CalcTextSize(text).X + ItemSpacing.X;

    private static float MaxTextWidth<T>(IEnumerable<T> source, Func<T, string> getText)
    {
        var maxWidth = 0f;
        foreach (var value in source)
        {
            var width = TextWidth(getText(value));
            if (width > maxWidth)
            {
                maxWidth = width;
            }
        }

        return maxWidth;
    }
}
