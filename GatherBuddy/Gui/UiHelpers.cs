using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using GatherBuddy.Classes;
using GatherBuddy.Config;
using GatherBuddy.Interfaces;
using GatherBuddy.RebornUI;
using GatherBuddy.RebornUI.Tables;
using GatherBuddy.Time;
using GatherBuddy.Utility;
using RebornMaterial;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using ImRaii = Dalamud.Interface.Utility.Raii.ImRaii;

namespace GatherBuddy.Gui;

public partial class Interface
{
    internal static bool DrawLocationInput(IGatherable item, ILocation? current, out ILocation? ret)
    {
        const string noPreferred = "No preferred location";
        var width = SetInputWidth * 0.85f;
        ret = current;
        if (item.Locations.Count == 1)
        {
            var only = 0;
            M3Widgets.Combo("##Location", ref only, 1, _ => item.Locations[0].Name, width, enabled: false,
                tooltip: LocationTooltip(item.Locations[0]));
            return false;
        }

        var index = current == null ? 0 : item.Locations.IndexOf(l => l.Id == current.Id) + 1;
        if (!M3Widgets.Combo("##Location", ref index, item.Locations.Count + 1, i => i == 0 ? noPreferred : item.Locations[i - 1].Name, width,
                noPreferred, item.Locations.Count > 8, tooltip: LocationTooltip(current),
                itemTooltip: i => i == 0 ? null : LocationTooltip(item.Locations[i - 1])))
        {
            return false;
        }

        ret = index <= 0 ? null : item.Locations[index - 1];
        return true;
    }

    private static readonly Dictionary<ILocation, string> LocationTooltips = new(ReferenceEqualityComparer.Instance);

    internal static string? LocationTooltip(ILocation? loc)
    {
        if (loc == null)
        {
            return null;
        }

        if (LocationTooltips.TryGetValue(loc, out var tooltip))
        {
            return tooltip;
        }

        var builder = new StringBuilder();
        if (loc is GatheringNode node)
        {
            builder.Append(loc.Territory.Name).Append('\n').Append(loc.GatheringType).Append('\n').Append(node.Times.PrintHours()).Append('\n');
        }

        var first = true;
        foreach (var gatherable in loc.Gatherables)
        {
            if (!first)
            {
                builder.Append('\n');
            }

            builder.Append(gatherable.Name[GatherBuddy.Language]);
            first = false;
        }

        tooltip = builder.ToString();
        LocationTooltips[loc] = tooltip;
        return tooltip;
    }

    internal static void DrawTimeInterval(TimeInterval uptime, bool uptimeDependency = false, bool rightAligned = true, IGatherable? item = null)
    {
        var active = uptime.ToTimeString(GatherBuddy.Time.ServerTime, false, out var timeString);
        var colorId = (active, uptimeDependency) switch
        {
            (true, true) => ColorId.DependentAvailableFish.Value(),
            (true, false) => ColorId.AvailableItem.Value(),
            (false, true) => ColorId.DependentUpcomingFish.Value(),
            (false, false) => ColorId.UpcomingItem.Value(),
        };

        using (ImRaii.PushColor(ImGuiCol.Text, colorId))
        {
            if (rightAligned)
            {
                Ui.RightAligned(timeString);
            }
            else
            {
                ImGui.TextUnformatted(timeString);
            }
        }

        DrawTimeIntervalTooltip(uptime, uptimeDependency, item, !char.IsLetter(timeString[0]));
    }

    private static readonly string[] UpcomingUptimeHeaders = ["Day", "Time", "Starts in", "Duration", "Downtime"];

    internal static void DrawTimeIntervalTooltip(TimeInterval uptime, bool uptimeDependency = false, IGatherable? item = null, bool displayNextWindow = true)
    {
        if (!uptimeDependency && !displayNextWindow || !ImGui.IsItemHovered())
        {
            return;
        }

        using var tt = ImRaii.Tooltip();

        if (uptimeDependency)
        {
            M3Badge.Draw("Depends on another fish's uptime", M3.Scheme.Warning);
        }

        if (!displayNextWindow)
        {
            return;
        }

        if (item is null || GatherBuddy.Config.UpcomingUptimesCount <= 1)
        {
            ImGui.TextUnformatted($"{uptime.Start}\n{uptime.End}\n{uptime.DurationString()}");
            return;
        }

        var uptimes = GatherBuddy.UptimeManager.GetUpcomingUptimes(item, GatherBuddy.Config.UpcomingUptimesCount);
        if (uptimes.Count <= 0)
        {
            ImGui.TextUnformatted($"{uptime.Start}\n{uptime.End}\n{uptime.DurationString()}\n\nLoading the next {GatherBuddy.Config.UpcomingUptimesCount} windows...");
            return;
        }

        using var tableStyle = M3Table.PushStyle();
        using var style = ImRaii.PushStyle(ImGuiStyleVar.CellPadding, new Vector2(10, 5) * ImGuiHelpers.GlobalScale);
        using var table = ImRaii.Table("##UpcomingUptimesTable", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.PadOuterX);
        if (!table)
        {
            return;
        }

        foreach (var header in UpcomingUptimeHeaders)
        {
            ImGui.TableSetupColumn(header);
        }

        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        foreach (var header in UpcomingUptimeHeaders)
        {
            ImGui.TableNextColumn();
            M3Table.Header(header, center: false);
        }

        DateTime? previousStartDate = null;
        var now = GatherBuddy.Time.ServerTime;

        for (var i = 0; i < uptimes.Count && i < GatherBuddy.Config.UpcomingUptimesCount; i++)
        {
            var time = uptimes[i];
            var startDate = time.Start.LocalTime;
            ImGui.TableNextColumn();
            if (previousStartDate is null || previousStartDate.Value.Date != startDate.Date)
            {
                ImGui.TextUnformatted(startDate.ToString("yyyy-MM-dd (ddd)", CultureInfo.InvariantCulture));
                previousStartDate = startDate;
            }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(startDate.ToString("HH:mm", CultureInfo.InvariantCulture));

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(now < time.Start ? new TimeInterval(now, time.Start).DurationString(true) : "Active");

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(time.DurationString(true));

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(i < uptimes.Count - 1 ? new TimeInterval(uptimes[i].End, uptimes[i + 1].Start).DurationString(true) : "-");
        }
    }

    internal static void HoverTooltip(string text)
    {
        if (!text.StartsWith('\0'))
        {
            M3Tooltip.Hovered(text);
        }
    }

    public static void AlignTextToSize(string text, Vector2 size)
    {
        var cursor = ImGui.GetCursorPos();
        ImGui.SetCursorPos(cursor + new Vector2(ImGui.GetStyle().ItemSpacing.X / 2, (size.Y - ImGui.GetTextLineHeight()) / 2));
        ImGui.TextUnformatted(text);
        ImGui.SameLine();
        ImGui.SetCursorPosY(cursor.Y);
        ImGui.NewLine();
    }

    private static float TableFooterHeight
        => (M3Widgets.ButtonHeight + ImGui.GetStyle().ItemSpacing.Y + M3.Space1) / ImGuiHelpers.GlobalScale;

    private void DrawTableFooter<T>(Table<T> table, Func<T, IGatherable> selector, string label, string statusName)
    {
        ImGui.Dummy(new Vector2(0, M3.Space1));
        var rowTop = ImGui.GetCursorScreenPos().Y;
        var drewButton = DrawAddAllFilteredToAutoGather(table, selector, label);
        DrawStatusLine(table, statusName, drewButton, rowTop, M3Widgets.ButtonHeight);
    }

    private static void DrawStatusLine<T>(Table<T> table, string name, bool sameLine, float rowTop, float rowHeight)
    {
        if (!GatherBuddy.Config.ShowStatusLine)
        {
            return;
        }

        void Next(float itemHeight)
        {
            if (sameLine)
            {
                ImGui.SameLine(0, M3.Space3);
            }

            sameLine = true;
            ImGui.SetCursorScreenPos(new Vector2(ImGui.GetCursorScreenPos().X, rowTop + (rowHeight - itemHeight) * 0.5f));
        }

        Next(ImGui.GetTextLineHeight());
        M3Text.Draw($"{table.CurrentItems} of {table.TotalItems} {name.ToLowerInvariant()} shown", M3TextStyle.Muted);
        if (table.TotalColumns != table.VisibleColumns)
        {
            Next(ImGui.GetTextLineHeight());
            var hidden = table.TotalColumns - table.VisibleColumns;
            M3Text.Draw(hidden == 1 ? "1 column hidden" : $"{hidden} columns hidden", M3TextStyle.Muted);
        }

        if (typeof(T) != typeof(ExtendedFish))
        {
            return;
        }

        if (File.Exists(GatherBuddy.GameData.OverrideFile))
        {
            Next(M3Widgets.ChipHeight);
            if (M3Widgets.Chip("##reimport", "Reimport fish overrides", false, FontAwesomeIcon.Redo,
                    "Read the fish data override file again.\nRight-click to move it to a backup instead.")
             && GatherBuddy.GameData.ReimportOverrides())
            {
                GatherBuddy.UptimeManager.ResetModifiedUptimes();
                LocationTooltips.Clear();
                foreach (var fish in ExtendedFishList)
                {
                    if (fish.Data.HasOverridenData)
                    {
                        fish.Update();
                    }
                }
            }

            using var menu = M3Menu.BeginContext("##overrideMenu");
            if (menu.IsOpen && M3Menu.Item("##backup", "Move to backup", FontAwesomeIcon.Archive))
            {
                try
                {
                    File.Move(GatherBuddy.GameData.OverrideFile, Path.ChangeExtension(GatherBuddy.GameData.OverrideFile, ".json.bak"), true);
                }
                catch (Exception ex)
                {
                    GatherBuddy.Log.Error($"Could not move fish data override file to backup:\n{ex}");
                }
            }
        }

        if (GatherBuddy.GameData.OverriddenFish > 0)
        {
            Next(ImGui.GetTextLineHeight());
            M3Text.Draw($"{GatherBuddy.GameData.OverriddenFish} fish overridden", M3TextStyle.Muted);
        }
    }

    private static void DrawClippy()
    {
        const string popupName = "GatherClippy###ClippyPopup";
        const string text = "Can't find something?";
        if (GatherBuddy.Config.HideClippy)
        {
            return;
        }

        const FontAwesomeIcon icon = FontAwesomeIcon.QuestionCircle;
        var buttonSize = M3Widgets.PillSize(text, icon);
        var padding = new Vector2(M3.Space3);

        ImGui.SetCursorPos(ImGui.GetWindowSize() - buttonSize - padding);
        using var child = ImRaii.Child("##clippyChild", buttonSize, false, ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoDecoration);
        if (!child)
        {
            return;
        }

        if (M3Widgets.Pill("##clippy", text, M3.Scheme.Tertiary, icon,
                "Click for some help navigating this table.\nControl + Shift + Right-Click to permanently hide this button.", true))
        {
            ImGui.OpenPopup(popupName);
        }

        if (ImGui.IsItemClicked(ImGuiMouseButton.Right) && ImGui.GetIO().KeyCtrl && ImGui.GetIO().KeyShift)
        {
            GatherBuddy.Config.HideClippy = true;
            GatherBuddy.Config.Save();
        }

        var width = 760f * M3.Scale;
        ImGui.SetNextWindowPos(ImGui.GetIO().DisplaySize / 2, ImGuiCond.Always, new Vector2(0.5f));
        using var popup = ImRaii.Popup(popupName,
            ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.Modal);
        if (!popup)
        {
            return;
        }

        using (ImRaii.PushFont(M3.TitleMedium))
        {
            ImGui.TextUnformatted("Getting around the tables");
        }

        ImGui.Dummy(new Vector2(width, M3.Space1));
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width);
        M3Widgets.SectionLabel("Filters");
        M3Text.Bullet("Text filters like \"Filter items\" only show entries that contain the given text. They are case-insensitive and are not kept for your next session.");
        M3Text.Bullet("Text filters also support regular expressions, e.g. \"(blue|green)\" matches all entries that contain either blue or green.");
        M3Text.Bullet("Button filters like \"Next Uptime\", \"Node Type\" or \"Fish Type\" open a menu of what to show.");
        M3Text.Bullet("Those filters are kept across sessions. A column with an active filter shows a tinted button with a filter icon; right-click it to clear the filter.");
        M3Widgets.SectionLabel("Columns");
        M3Text.Bullet("Click the blank space of a header to sort the table by that column, ascending or descending. A small triangle shows the direction.");
        M3Text.Bullet("Right-click the blank space of a header to open the table's context menu, where you can hide columns you are not interested in.");
        M3Text.Bullet("Resize text columns by dragging the small separators between them. Sizes are kept across sessions.");
        M3Text.Bullet("Reorder most columns by dragging the blank space of their header. The order is kept across sessions.");
        M3Widgets.SectionLabel("Items");
        M3Text.Bullet("Right-click item names and a few other columns (like bait and fishing spot) for menus with more options.");
        ImGui.PopTextWrapPos();

        ImGui.Dummy(new Vector2(0f, M3.Space2));
        if (M3Widgets.Button("##clippy_understood", "Understood", M3ButtonStyle.Filled, FontAwesomeIcon.Check, width))
        {
            ImGui.CloseCurrentPopup();
        }
    }
}
