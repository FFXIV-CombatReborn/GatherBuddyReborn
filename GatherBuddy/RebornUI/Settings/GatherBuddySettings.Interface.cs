using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using GatherBuddy.Config;
using GatherBuddy.RebornUI.Settings;
using RebornMaterial;
using System;
using System.Collections.Generic;
using System.Numerics;
using FishRecord = GatherBuddy.FishTimer.FishRecord;

namespace GatherBuddy.RebornUI;

internal sealed partial class GatherBuddySettings
{
    private M3SettingsPage BuildInterfacePage()
        => new(InterfacePageId, "Interface settings", FontAwesomeIcon.Palette, "Theme, windows, overlays and colors",
            new M3SettingsGroup("theme", "Theme", FontAwesomeIcon.Palette, "Applies to every GatherBuddy Reborn and Vulcan window.",
                Custom("Accent color color theme scheme", DrawAccentColor),
                Custom("Text size scale font", () => SettingRows.Slider("Text size", "Scales the text, on top of Dalamud's own font size.",
                    Config.UiTextScale, 0.75f, 2f, "%.2fx", v => Config.UiTextScale = MathF.Round(v, 2))),
                Custom("Control size element scale buttons switches", () => SettingRows.Slider("Control size",
                    "Scales buttons, switches, fields and the other controls.",
                    Config.UiElementScale, 0.75f, 2f, "%.2fx", v => Config.UiElementScale = MathF.Round(v, 2))),
                Custom("Spacing padding scale", () => SettingRows.Slider("Spacing", "Scales the space around and between things, without resizing them.",
                    Config.UiPaddingScale, 0.5f, 2f, "%.2fx", v => Config.UiPaddingScale = MathF.Round(v, 2))),
                Custom("Animation length motion", () => SettingRows.Slider("Animation length",
                    "How long animations take, such as folding a window or opening a card. 0% turns them off.",
                    Config.UiMotionScale * 100f, 0f, 200f, "%.0f%%", v => Config.UiMotionScale = MathF.Round(v) / 100f, 1f)),
                Toggle("Compact layout", "Tighter padding and shorter rows, so more fits on screen.",
                    () => Config.UiCompactLayout, v => Config.UiCompactLayout = v, "density dense tight"),
                Custom("Reset sizes", DrawResetSizes)),
            new M3SettingsGroup("main_window", "Main window", FontAwesomeIcon.WindowMaximize, null,
                Toggle("Open on start", "Show the GatherBuddy window when you start the game.",
                    () => Config.OpenOnStart, v => Config.OpenOnStart = v, "open config ui on start"),
                Toggle("Escape closes main window", "Pressing Escape while the main window is focused closes it.",
                    () => Config.CloseOnEscape, v => Config.CloseOnEscape = v),
                Toggle("Lock window position", "Stop the main window from being moved.",
                    () => Config.MainWindowLockPosition, v => Config.MainWindowLockPosition = v, "lock config ui movement"),
                Toggle("Lock window size", "Stop the main window from being resized.",
                    () => Config.MainWindowLockResize, v => Config.MainWindowLockResize = v, "lock config ui size"),
                Custom("Hotkey to open main interface", () => SettingRows.Hotkey("Hotkey to open the main window",
                    "Opens or closes the main GatherBuddy window.", Config.MainInterfaceHotkey, v => Config.MainInterfaceHotkey = v)),
                Toggle("Show names in weather page", "Write the weather names in the weather table, instead of only the icons with names on hover.",
                    () => Config.ShowWeatherNames, v => Config.ShowWeatherNames = v, "weather tab"),
                Toggle("Show status line", "Show a status line below the gatherables and fish tables.",
                    () => Config.ShowStatusLine, v => Config.ShowStatusLine = v),
                Toggle("Hide GatherClippy button", "Permanently hide the GatherClippy button on the Gatherables and Fish pages.",
                    () => Config.HideClippy, v => Config.HideClippy = v),
                Int("Upcoming windows uptime count", "How many gathering windows the upcoming uptimes tooltip lists.",
                    () => Config.UpcomingUptimesCount, v => Config.UpcomingUptimesCount = (ushort)v, 1, 100)),
            new M3SettingsGroup("fish_timer", "Fish timer", FontAwesomeIcon.Stopwatch, null,
                Toggle("Keep fish records", "Store fish records on your computer. The fish timer needs these for bite timings.",
                    () => Config.StoreFishRecords, v => Config.StoreFishRecords = v),
                Toggle("Use local time in records", "Show timestamps on the Fish Records page in local time instead of Unix time.",
                    () => Config.UseUnixTimeFishRecords, v => Config.UseUnixTimeFishRecords = v),
                Toggle("Show fish timer", "Show the fish timer window while fishing.",
                    () => Config.ShowFishTimer, v => Config.ShowFishTimer = v),
                Toggle("Edit fish timer", "Let the fish timer window be moved and resized.",
                    () => Config.FishTimerEdit, v => Config.FishTimerEdit = v),
                Toggle("Enable fish timer clickthrough", "Let clicks go through the fish timer, which also turns off its context menus.",
                    () => Config.FishTimerClickthrough, v => Config.FishTimerClickthrough = v),
                Toggle("Hide uncaught fish in fish timer",
                    "Hide fish that have never been recorded with your current combination of snagging and bait.",
                    () => Config.HideUncaughtFish, v => Config.HideUncaughtFish = v),
                Toggle("Hide unavailable fish in fish timer",
                    "Hide fish with known requirements you don't meet, like Fisher's Intuition or snagging.",
                    () => Config.HideUnavailableFish, v => Config.HideUnavailableFish = v),
                Toggle("Show uptimes in fish timer", "Show the uptimes of restricted fish in the fish timer window.",
                    () => Config.ShowFishTimerUptimes, v => Config.ShowFishTimerUptimes = v),
                Custom("Fish timer bite time scale seconds", DrawBiteTimeScale),
                Custom("Fish timer interval separators", () => SettingRows.Slider("Fish timer interval separators",
                    "How many interval lines, with their seconds, the timer shows. 0 turns them off.",
                    Config.ShowSecondIntervals, 0, 16, v => Config.ShowSecondIntervals = (byte)v)),
                Custom("Fish timer interval rounding digits", () => SettingRows.Slider("Fish timer interval rounding",
                    "Round the interval seconds to this many digits after the decimal point. 0 shows whole numbers.",
                    Config.SecondIntervalsRounding, 0, 3, v => Config.SecondIntervalsRounding = v)),
                Toggle("Hide catch popup", "Don't show the popup with your caught fish and its size, amount and quality.",
                    () => Config.HideFishSizePopup, v => Config.HideFishSizePopup = v),
                Toggle("Show collectable hints", "Show whether a fish is collectable in the fish timer window.",
                    () => Config.ShowCollectableHints, v => Config.ShowCollectableHints = v),
                Toggle("Show multi hook hints", "Show whether a fish can be double or triple hooked in Cosmic Exploration and Ocean Fishing.",
                    () => Config.ShowMultiHookHints, v => Config.ShowMultiHookHints = v)),
            new M3SettingsGroup("fish_stats", "Fish stats", FontAwesomeIcon.ChartBar, null,
                Toggle("Enable fish stats", "Adds a page that collects and reports fish stats from your local records. Currently in testing.",
                    () => Config.EnableFishStats, v => Config.EnableFishStats = v),
                Toggle("Copy time stats when reporting", "Add the minimum and maximum times when copying a report.",
                    () => Config.EnableReportTime, v => Config.EnableReportTime = v),
                Toggle("Copy size stats when reporting", "Add the minimum and maximum sizes when copying a report.",
                    () => Config.EnableReportSize, v => Config.EnableReportSize = v),
                Toggle("Copy multi hook stats when reporting", "Add stats about multi-hook yields when copying a report.",
                    () => Config.EnableReportMulti, v => Config.EnableReportMulti = v),
                Toggle("Enable graphs", "Visualise the report data when viewing a fishing spot. Very experimental.",
                    () => Config.EnableFishStatsGraphs, v => Config.EnableFishStatsGraphs = v)),
            new M3SettingsGroup("gather_window", "Gather window", FontAwesomeIcon.ListUl, "The small window with your pinned items and their uptimes.",
                Toggle("Show gather window", "Show a small window with pinned gatherables and their uptimes.",
                    () => Config.ShowGatherWindow, v => Config.ShowGatherWindow = v),
                Toggle("Anchor gather window to bottom left", "The window grows and shrinks from the top instead of the bottom.",
                    () => Config.GatherWindowBottomAnchor, v => Config.GatherWindowBottomAnchor = v),
                Toggle("Show gather window timers", "Show the uptimes of the items in the gather window.",
                    () => Config.ShowGatherWindowTimers, v => Config.ShowGatherWindowTimers = v),
                Toggle("Show active alarms in gather window",
                    "Also show active alarms as a last preset in the gather window, following the window's usual rules.",
                    () => Config.ShowGatherWindowAlarms, SetGatherWindowAlarms),
                Toggle("Sort gather window by uptime", "Sort the items in the gather window by their uptimes.",
                    () => Config.SortGatherWindowByUptime, v => Config.SortGatherWindowByUptime = v),
                Toggle("Show only available items", "Only show the items from your gather window setup that are available right now.",
                    () => Config.ShowGatherWindowOnlyAvailable, v => Config.ShowGatherWindowOnlyAvailable = v),
                Toggle("Hide completed items", "Hide items you already have the required amount of in your inventory.",
                    () => Config.HideGatherWindowCompletedItems, v => Config.HideGatherWindowCompletedItems = v),
                Toggle("Hide gather window in duty", "Hide the gather window while you are bound by any duty.",
                    () => Config.HideGatherWindowInDuty, v => Config.HideGatherWindowInDuty = v),
                Custom("Only show gather window if holding key hotkey to hold", () =>
                {
                    SettingRows.Switch("Only show gather window while holding a key", "The gather window only shows while you hold the key below.",
                        Config.OnlyShowGatherWindowHoldingKey, v => Config.OnlyShowGatherWindowHoldingKey = v);
                    if (!Config.OnlyShowGatherWindowHoldingKey)
                    {
                        return;
                    }

                    using var sub = M3SubGroup.Begin();
                    SettingRows.HoldKey("Key to hold", "Hold this key to keep the gather window visible. Click, then press it; Alt, Ctrl and Shift work on their own.",
                        Config.GatherWindowHoldKey, Configuration.ValidKeys, v => Config.GatherWindowHoldKey = v);
                }),
                Toggle("Lock gather window position", "Stop the gather window from being dragged around.",
                    () => Config.LockGatherWindow, v => Config.LockGatherWindow = v),
                Custom("Hotkey to open gather window", () => SettingRows.Hotkey("Hotkey to open the gather window",
                    "Opens or closes the gather window.", Config.GatherWindowHotkey, v => Config.GatherWindowHotkey = v)),
                Custom("Modifier to delete items on right-click", () => SettingRows.Modifier("Modifier to delete items on right-click",
                    "Hold this while right-clicking an item in the gather window to remove it.",
                    Config.GatherWindowDeleteModifier, v => Config.GatherWindowDeleteModifier = v))),
            new M3SettingsGroup("spearfishing", "Spearfishing", FontAwesomeIcon.Water, null,
                Toggle("Show spearfishing helper", "Show the spearfishing helper while spearfishing.",
                    () => Config.ShowSpearfishHelper, v => Config.ShowSpearfishHelper = v),
                Toggle("Show fish name overlay", "Show the names of identified fish in the spearfishing window.",
                    () => Config.ShowSpearfishNames, v => Config.ShowSpearfishNames = v),
                Toggle("Show speed of fish in overlay", "Show the speed of fish in the spearfishing window, next to their names.",
                    () => Config.ShowSpearfishSpeed, v => Config.ShowSpearfishSpeed = v),
                Toggle("Show list of available fish", "List the fish available at your current spearfishing spot beside the spearfishing window.",
                    () => Config.ShowAvailableSpearfish, v => Config.ShowAvailableSpearfish = v),
                Toggle("Show speed and size as text", "Show the speed and size of available fish as text instead of icons.",
                    () => Config.ShowSpearfishListIconsAsText, v => Config.ShowSpearfishListIconsAsText = v),
                Toggle("Show center line", "Show a straight line up from the center of the gig in the spearfishing window.",
                    () => Config.ShowSpearfishCenterLine, v => Config.ShowSpearfishCenterLine = v),
                Custom("Show fish names in fixed position fish name position percentage", () =>
                {
                    SettingRows.Switch("Show fish names in fixed position",
                        "Show the names of identified fish at a fixed height instead of on the moving fish.",
                        Config.FixNamesOnPosition, v => Config.FixNamesOnPosition = v);
                    if (!Config.FixNamesOnPosition)
                    {
                        return;
                    }

                    using var sub = M3SubGroup.Begin();
                    SettingRows.Slider("Fish name position", "How far up the spearfishing window the names sit.",
                        Config.FixNamesPercentage, 0, 100, v => Config.FixNamesPercentage = (byte)v);
                })),
            new M3SettingsGroup("colors", "Colors", FontAwesomeIcon.PaintBrush, "Colors used in tables, the gather window and chat messages.",
                Custom($"Colors colors {string.Join(' ', Array.ConvertAll(ColorIds, c => c.Data().Name))}", DrawColors),
                Custom("Names in chat commands in chat arguments in chat alarm message in chat palette", DrawChatColors)));

    private static void DrawAccentColor()
    {
        var accent = Config.UiAccentColor;
        if (M3Widgets.RowColorChoices("Accent color", ref accent, MaterialUI.AccentPresets,
                "The whole color scheme is built from this one color. Pick a preset, or the last swatch for your own color."))
        {
            Config.UiAccentColor = accent with { W = 1f };
            Config.Save();
        }
    }

    private static void DrawResetSizes()
    {
        var isDefault = Config.UiTextScale == 1f && Config.UiElementScale == 1f && Config.UiPaddingScale == 1f && Config.UiMotionScale == 1f;
        if (!M3Widgets.Button("##theme_reset_sizes", "Reset sizes", M3ButtonStyle.Text, FontAwesomeIcon.Undo, enabled: !isDefault))
        {
            return;
        }

        Config.UiTextScale = 1f;
        Config.UiElementScale = 1f;
        Config.UiPaddingScale = 1f;
        Config.UiMotionScale = 1f;
        Config.Save();
    }

    private static void DrawBiteTimeScale()
    {
        var seconds = Config.FishTimerScale / 1000f;
        SettingRows.Number("Fish timer bite time scale",
            "The fish timer's bite times are scaled to this. Bites longer than this don't show their progress bar and bite windows. "
          + "Keep it as high as your longest bite window and otherwise as low as possible; about 40 seconds is usually enough.",
            seconds, FishRecord.MinBiteTime * 2 / 1000f, FishRecord.MaxBiteTime / 1000f,
            v => Config.FishTimerScale = (ushort)Math.Clamp((int)(v * 1000f + 0.9f), FishRecord.MinBiteTime * 2, FishRecord.MaxBiteTime),
            0.5f, "%.1f", "s");
    }

    private void SetGatherWindowAlarms(bool enabled)
    {
        Config.ShowGatherWindowAlarms = enabled;
        _plugin.GatherWindowManager.SetShowGatherWindowAlarms(enabled);
    }

    private static readonly ColorId[] ColorIds = Enum.GetValues<ColorId>();

    private static readonly string[] ColorLabels = Array.ConvertAll(ColorIds, c => $"{c.Data().Name}##color_{c}");

    private static void DrawColors()
    {
        for (var i = 0; i < ColorIds.Length; ++i)
        {
            var color = ColorIds[i];
            var (defaultColor, _, description) = color.Data();
            var current = Config.Colors.TryGetValue(color, out var value) ? value : defaultColor;
            SettingRows.Color(ColorLabels[i], description, current, defaultColor, c => Config.Colors[color] = c);
        }
    }

    private static void DrawChatColors()
    {
        M3Widgets.SectionLabel("Chat colors");
        DrawPaletteColor("Names in chat", Config.SeColorNames, Configuration.DefaultSeColorNames, i => Config.SeColorNames = i);
        DrawPaletteColor("Commands in chat", Config.SeColorCommands, Configuration.DefaultSeColorCommands, i => Config.SeColorCommands = i);
        DrawPaletteColor("Arguments in chat", Config.SeColorArguments, Configuration.DefaultSeColorArguments, i => Config.SeColorArguments = i);
        DrawPaletteColor("Alarm message in chat", Config.SeColorAlarm, Configuration.DefaultSeColorAlarm, i => Config.SeColorAlarm = i);
    }

    private static void DrawPaletteColor(string label, int current, int defaultIndex, Action<int> setter)
    {
        var colors = Configuration.ForegroundColors;
        var scale = M3.Scale;
        var swatch = M3Widgets.ColorSwatchSize;
        var resetSize = 32f * scale;
        var canReset = current != defaultIndex && colors.ContainsKey(defaultIndex);
        var controlSize = new Vector2(swatch + (canReset ? M3.Space1 + resetSize : 0f), MathF.Max(swatch, resetSize));

        var row = M3SettingRow.Begin(label, "Click the swatch to pick a colour, right-click it to clear.", controlSize);
        var origin = row.ControlPosition;
        var popupId = $"##palette_{label}";

        ImGui.SetCursorScreenPos(origin + new Vector2(0f, (controlSize.Y - swatch) * 0.5f));
        var hasColor = colors.TryGetValue(current, out var packed);
        if (ImGui.InvisibleButton($"##swatch_{label}", new Vector2(swatch)))
        {
            ImGui.OpenPopup(popupId);
        }

        var hovered = ImGui.IsItemHovered();
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var drawList = ImGui.GetWindowDrawList();
        var s = M3.Scheme;
        if (hasColor)
        {
            drawList.AddRectFilled(min, max, packed, M3.ShapeSmall);
        }
        else
        {
            drawList.AddRectFilled(min, max, M3.U32(s.SurfaceContainerHighest), M3.ShapeSmall);
            drawList.AddLine(new Vector2(min.X + 4f * scale, max.Y - 4f * scale), new Vector2(max.X - 4f * scale, min.Y + 4f * scale),
                M3.U32(s.Error, 0.8f), 1.5f * scale);
        }

        drawList.AddRect(min, max, M3.U32(hovered ? s.OnSurface : s.Outline, hovered ? 0.9f : 0.6f), M3.ShapeSmall, ImDrawFlags.None, 1f * scale);
        if (hovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            M3Tooltip.Show(hasColor ? $"Color {current}" : "No color");
        }

        if (ImGui.IsItemClicked(ImGuiMouseButton.Right) && current != -1)
        {
            setter(-1);
            Config.Save();
        }

        if (canReset)
        {
            ImGui.SetCursorScreenPos(origin + new Vector2(swatch + M3.Space1, (controlSize.Y - resetSize) * 0.5f));
            if (M3Widgets.IconButton($"##palette_reset_{label}", FontAwesomeIcon.Undo, "Reset to default", diameter: resetSize))
            {
                setter(defaultIndex);
                Config.Save();
            }
        }

        M3SettingRow.End(row);
        DrawPalettePopup(popupId, colors, current, setter);
    }

    private static void DrawPalettePopup(string popupId, IDictionary<int, uint> colors, int current, Action<int> setter)
    {
        if (!ImGui.BeginPopup(popupId))
        {
            return;
        }

        var scale = M3.Scale;
        var box = 22f * scale;
        var gap = 4f * scale;
        const int perLine = 10;
        var counter = 0;
        foreach (var (index, value) in colors)
        {
            if (counter++ % perLine != 0)
            {
                ImGui.SameLine(0f, gap);
            }

            if (ImGui.InvisibleButton($"##palette_entry_{index}", new Vector2(box)))
            {
                setter(index);
                Config.Save();
                ImGui.CloseCurrentPopup();
            }

            var min = ImGui.GetItemRectMin();
            var max = ImGui.GetItemRectMax();
            var drawList = ImGui.GetWindowDrawList();
            drawList.AddRectFilled(min, max, value, M3.ShapeExtraSmall);
            if (index == current)
            {
                drawList.AddRect(min - new Vector2(2f * scale), max + new Vector2(2f * scale), M3.U32(M3.Scheme.Primary), M3.ShapeExtraSmall,
                    ImDrawFlags.None, 2f * scale);
            }

            if (ImGui.IsItemHovered())
            {
                M3Tooltip.Show($"Color {index}");
            }
        }

        ImGui.EndPopup();
    }
}
