using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using GatherBuddy.Alarms;
using GatherBuddy.Config;
using GatherBuddy.GatherHelper;
using GatherBuddy.Plugin;
using GatherBuddy.RebornUI;
using GatherBuddy.Time;
using RebornMaterial;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using ImRaii = Dalamud.Interface.Utility.Raii.ImRaii;

namespace GatherBuddy.Gui;

public partial class Interface
{
    private static string CheckUnnamed(string name)
        => name.Length > 0 ? name : "<Unnamed>";

    private static string CheckUndescribed(string desc)
        => desc.Length > 0 ? desc : "<No Description>";

    private class AlarmWindowDragDropData(AlarmGroup group, Alarm alarm, int idx)
    {
        public AlarmGroup Group = group;
        public Alarm Alarm = alarm;
        public int AlarmIdx = idx;
    }

    private class AlarmCache(AlarmManager manager)
    {
        public sealed class AlarmSelector(AlarmManager manager) : ItemSelector<AlarmGroup>(manager.Alarms, Flags.All)
        {
            private readonly AlarmManager _manager = manager;

            protected override bool Filtered(int idx)
                => Filter.Length != 0 && !Items[idx].Name.Contains(Filter, StringComparison.InvariantCultureIgnoreCase);

            protected override bool OnDraw(int idx)
                => ListRow(CheckUnnamed(Items[idx].Name), idx == CurrentIdx, !Items[idx].Enabled);

            protected override string NameOf(AlarmGroup item)
                => item.Name;

            protected override bool OnDelete(int idx)
            {
                _manager.DeleteGroup(idx);
                return true;
            }

            protected override bool OnAdd(string name)
            {
                _manager.AddGroup(name);
                return true;
            }

            protected override bool OnClipboardImport(string name, string data)
            {
                if (!AlarmGroup.Config.FromBase64(data, out var configGroup))
                {
                    return false;
                }

                var alarms = new List<Alarm>();
                var configAlarmCount = 0;
                foreach (var alarmConfig in configGroup.Alarms)
                {
                    configAlarmCount++;
                    if (Alarm.FromConfig(alarmConfig, out var alarm) && alarm != null)
                    {
                        alarms.Add(alarm);
                    }
                }

                var group = new AlarmGroup()
                {
                    Name = name,
                    Description = configGroup.Description,
                    Enabled = false,
                    Alarms = alarms,
                };

                if (group.Alarms.Count < configAlarmCount)
                {
                    GatherBuddy.Log.Warning("Invalid alarms skipped");
                }

                _manager.AddGroup(group);
                return true;
            }

            protected override bool OnDuplicate(string name, int idx)
            {
                var group = _manager.Alarms[idx].Clone();
                group.Name = name;
                _manager.AddGroup(group);
                return true;
            }

            protected override void OnDrop(object? data, int idx)
            {
                if (data is not AlarmWindowDragDropData obj)
                {
                    return;
                }

                var group = _plugin.AlarmManager.Alarms[idx];
                _plugin.AlarmManager.DeleteAlarm(obj.Group, obj.AlarmIdx);
                _plugin.AlarmManager.AddAlarm(group, obj.Alarm);
            }

            protected override bool OnMove(int idx1, int idx2)
            {
                _manager.MoveGroup(idx1, idx2);
                return idx1 != idx2;
            }
        }

        public static readonly Sounds[] SoundIds = Array.FindAll(Enum.GetValues<Sounds>(), s => s != Sounds.Unknown);

        public static readonly string[] SoundIdNames = Array.ConvertAll(SoundIds, s => s == Sounds.None ? "No Sound" : $"Sound {s.ToIdx()}");

        public readonly AlarmSelector Selector = new(manager);

        public string NewName = string.Empty;
        public int NewItemIdx;
        public bool NewEnabled;
        public bool NewPrintMessage;
        public int NewSoundIdx;
        public int NewSecondOffset;
        public uint ActiveOffsetInputId;

        public Alarm CreateAlarm()
            => new(GatherBuddy.UptimeManager.TimedGatherables[NewItemIdx])
            {
                Enabled = NewEnabled,
                SecondOffset = NewSecondOffset,
                PrintMessage = NewPrintMessage,
                Name = NewName,
                SoundId = SoundIds[NewSoundIdx],
            };
    }

    private readonly AlarmCache _alarmCache;

    private static readonly string[] AlarmColumns = ["##delete", "Name", "On", "Item", "Seconds early", "Chat", "Sound", "Location", "Next"];

    private static float AlarmItemWidth
        => SetInputWidth * 1.15f;

    private static float AlarmSoundWidth
        => 120 * M3.Scale;

    private static bool DrawTimedItemCombo(string id, ref int index)
    {
        var items = GatherBuddy.UptimeManager.TimedGatherables;
        return M3Widgets.Combo(id, ref index, items.Length, i => items[i].Name[GatherBuddy.Language], AlarmItemWidth, "Choose an item", true);
    }

    private void DrawAlarmInfo(ref int alarmIdx, AlarmGroup group, float offsetWidth)
    {
        var alarm = group.Alarms[alarmIdx];
        using var id = ImRaii.PushId(alarmIdx);

        var rowHeight = M3Widgets.NumberFieldHeight;
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        Ui.CenterInRow(rowHeight, ImGui.GetFrameHeight());
        if (M3Widgets.IconButton("##delete", FontAwesomeIcon.Trash, "Delete this alarm", diameter: ImGui.GetFrameHeight()))
        {
            _plugin.AlarmManager.DeleteAlarm(group, alarmIdx--);
            return;
        }

        ImGui.TableNextColumn();
        var name = alarm.Name;
        if (M3TextField.Draw("##name", CheckUnnamed(string.Empty), ref name, SetInputWidth, 64))
        {
            _plugin.AlarmManager.ChangeAlarmName(group, alarmIdx, name);
        }

        M3Tooltip.Hovered("Names are optional and can be used in the alarm message that is printed to chat.");

        ImGui.TableNextColumn();
        Ui.CenterInRow(rowHeight, M3Widgets.CheckboxHitSize().Y);
        var enabled = alarm.Enabled;
        if (M3Widgets.Checkbox("##enabled", ref enabled) && enabled != alarm.Enabled)
        {
            _plugin.AlarmManager.ToggleAlarm(group, alarmIdx);
        }

        M3Tooltip.Hovered("Enable this alarm.");

        ImGui.TableNextColumn();
        Ui.CenterInRow(rowHeight, M3Widgets.ComboHeight);
        var itemIdx = alarm.Item.InternalLocationId - 1;
        if (DrawTimedItemCombo("##item", ref itemIdx))
        {
            _plugin.AlarmManager.ChangeAlarmItem(group, alarmIdx, GatherBuddy.UptimeManager.TimedGatherables[itemIdx]);
        }

        _alarmCache.Selector.CreateDropSource(new AlarmWindowDragDropData(group, alarm, alarmIdx), alarm.Item.Name[GatherBuddy.Language]);
        var localIdx = alarmIdx;
        _alarmCache.Selector.CreateDropTarget<AlarmWindowDragDropData>(d => _plugin.AlarmManager.MoveAlarm(group, d.AlarmIdx, localIdx));

        ImGui.TableNextColumn();
        var secondOffset = alarm.SecondOffset;
        var prevActiveId = ImGuiP.GetActiveID();
        if (M3Widgets.NumberField("##offset", ref secondOffset, offsetWidth, 10, 0, RealTime.SecondsPerDay, "s"))
        {
            _plugin.AlarmManager.ChangeAlarmOffset(group, alarmIdx, Math.Clamp(secondOffset, 0, RealTime.SecondsPerDay));
        }

        Ui.TrackActiveInput(ref _alarmCache.ActiveOffsetInputId, prevActiveId);

        M3Tooltip.Hovered("Trigger this alarm this many seconds before the item is next available.");

        ImGui.TableNextColumn();
        Ui.CenterInRow(rowHeight, M3Widgets.CheckboxHitSize().Y);
        var printMessage = alarm.PrintMessage;
        if (M3Widgets.Checkbox("##print", ref printMessage))
        {
            _plugin.AlarmManager.ChangeAlarmMessage(group, alarmIdx, printMessage);
        }

        M3Tooltip.Hovered("Print a chat message when this alarm is triggered.");

        ImGui.TableNextColumn();
        Ui.CenterInRow(rowHeight, M3Widgets.ComboHeight);
        var soundIdx = Array.IndexOf(AlarmCache.SoundIds, alarm.SoundId);
        if (M3Widgets.Combo("##sound", ref soundIdx, AlarmCache.SoundIdNames, AlarmSoundWidth, tooltip: "The sound played when this alarm is triggered."))
        {
            _plugin.AlarmManager.ChangeAlarmSound(group, alarmIdx, AlarmCache.SoundIds[soundIdx]);
            AlarmManager.PreviewAlarm(AlarmCache.SoundIds[soundIdx]);
        }

        ImGui.TableNextColumn();
        Ui.CenterInRow(rowHeight, M3Widgets.ComboHeight);
        if (DrawLocationInput(alarm.Item, alarm.PreferLocation, out var newLocation))
        {
            _plugin.AlarmManager.ChangeAlarmLocation(group, alarmIdx, newLocation);
        }

        ImGui.TableNextColumn();
        var (_, time) = AlarmManager.GetUptime(alarm);
        var now = GatherBuddy.Time.ServerTime.AddSeconds(alarm.SecondOffset);
        var size = new Vector2(150 * M3.Scale, ImGui.GetFrameHeight());
        Ui.CenterInRow(rowHeight, size.Y);
        if (time.Start > now)
        {
            Ui.Framed(TimeInterval.DurationString(time.Start, now, false), ColorId.WarningBg.Value(), size);
        }
        else
        {
            Ui.Framed("Triggered now", ColorId.ChangedLocationBg.Value(), size);
        }
    }

    private static void DrawGroupData(AlarmGroup group, int idx)
    {
        if (Ui.CommitField("##groupName", "Name", group.Name, out var newName, SetInputWidth, 64))
        {
            _plugin.AlarmManager.ChangeGroupName(idx, newName);
        }

        ImGui.SameLine(0, M3.Space2);
        if (Ui.CommitField("##groupDesc", "Description", group.Description, out var newDesc, 2 * SetInputWidth, 128))
        {
            _plugin.AlarmManager.ChangeGroupDescription(idx, newDesc);
        }

        ImGui.SameLine(0, M3.Space3);
        var enabled = group.Enabled;
        Ui.CenterInRow(M3TextField.Height, M3Widgets.SwitchSize().Y);
        if (M3Widgets.Switch("##groupEnabled", ref enabled) && enabled != group.Enabled)
        {
            _plugin.AlarmManager.ToggleGroup(idx);
        }

        M3Tooltip.Hovered("Enable this alarm group. Only the alarms in it that are enabled themselves count as active.");
        ImGui.SameLine(0, M3.Space2);
        Ui.RowText(enabled ? "Enabled" : "Disabled", M3TextField.Height, M3TextStyle.Muted);
    }

    private static void DrawAlarmTableHeader(AlarmGroup group)
    {
        var headerHeight = group.Alarms.Count > 0 ? M3Widgets.CheckboxHitSize().Y : 0f;
        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        for (var i = 0; i < AlarmColumns.Length; ++i)
        {
            ImGui.TableNextColumn();
            if (i == 2 && group.Alarms.Count > 0)
            {
                var allEnabled = true;
                var anyEnabled = false;
                foreach (var alarm in group.Alarms)
                {
                    if (alarm.Enabled)
                    {
                        anyEnabled = true;
                    }
                    else
                    {
                        allEnabled = false;
                    }
                }

                if (M3Widgets.Checkbox("##allEnabled", ref allEnabled, mixed: !allEnabled && anyEnabled))
                {
                    for (var j = 0; j < group.Alarms.Count; ++j)
                    {
                        if (group.Alarms[j].Enabled != allEnabled)
                        {
                            _plugin.AlarmManager.ToggleAlarm(group, j);
                        }
                    }
                }

                M3Tooltip.Hovered("Enable all alarms, or disable all alarms.");
                continue;
            }

            if (!AlarmColumns[i].StartsWith("##"))
            {
                Ui.TableHeader(AlarmColumns[i], headerHeight);
            }
        }
    }

    private void DrawAlarmTable(AlarmGroup group)
    {
        using var style = M3Table.PushStyle();
        var offsetWidth = MathF.Max(
            MathF.Ceiling(M3Text.Measure("Seconds early", M3TextStyle.Label).X),
            M3Widgets.NumberFieldWidth(_alarmCache.NewSecondOffset.ToString(CultureInfo.InvariantCulture), true, "s"));
        for (var i = 0; i < group.Alarms.Count; ++i)
        {
            offsetWidth = MathF.Max(offsetWidth, M3Widgets.NumberFieldWidth(group.Alarms[i].SecondOffset.ToString(CultureInfo.InvariantCulture), true, "s"));
        }

        offsetWidth = MathF.Ceiling(Ui.ActiveNumberFieldWidth(ref _alarmCache.ActiveOffsetInputId, offsetWidth, true, "s"));
        var onHeaderWidth = MathF.Ceiling(M3Text.Measure("On", M3TextStyle.Label).X);
        var chatHeaderWidth = MathF.Ceiling(M3Text.Measure("Chat", M3TextStyle.Label).X);
        var checkboxWidth = M3Widgets.CheckboxHitSize().X;

        using var table = ImRaii.Table("##alarms", AlarmColumns.Length, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoKeepColumnsVisible
          | ImGuiTableFlags.RowBg | ImGuiTableFlags.PadOuterX);
        if (!table)
        {
            return;
        }

        foreach (var column in AlarmColumns)
        {
            ImGui.TableSetupColumn(column);
        }

        DrawAlarmTableHeader(group);
        for (var i = 0; i < group.Alarms.Count; ++i)
        {
            DrawAlarmInfo(ref i, group, offsetWidth);
        }

        using var id = ImRaii.PushId(-1);
        var rowHeight = M3Widgets.NumberFieldHeight;
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        Ui.CenterInRow(rowHeight, ImGui.GetFrameHeight());
        if (M3Widgets.IconButton("##add", FontAwesomeIcon.Plus, "Add this alarm", M3ButtonStyle.Tonal, diameter: ImGui.GetFrameHeight()))
        {
            _plugin.AlarmManager.AddAlarm(group, _alarmCache.CreateAlarm());
        }

        ImGui.TableNextColumn();
        M3TextField.Draw("##name", "Name of the new alarm", ref _alarmCache.NewName, SetInputWidth, 64);
        ImGui.TableNextColumn();
        Ui.CenterInRow(rowHeight, M3Widgets.CheckboxHitSize().Y);
        M3Widgets.Checkbox("##enabled", ref _alarmCache.NewEnabled);
        if (onHeaderWidth > checkboxWidth)
        {
            ImGui.SameLine(0f, 0f);
            ImGui.Dummy(new Vector2(onHeaderWidth - checkboxWidth, 0f));
        }

        ImGui.TableNextColumn();
        Ui.CenterInRow(rowHeight, M3Widgets.ComboHeight);
        DrawTimedItemCombo("##item", ref _alarmCache.NewItemIdx);
        ImGui.TableNextColumn();
        var prevNewOffsetActiveId = ImGuiP.GetActiveID();
        M3Widgets.NumberField("##offset", ref _alarmCache.NewSecondOffset, offsetWidth, 10, 0, RealTime.SecondsPerDay, "s");
        Ui.TrackActiveInput(ref _alarmCache.ActiveOffsetInputId, prevNewOffsetActiveId);
        ImGui.TableNextColumn();
        Ui.CenterInRow(rowHeight, M3Widgets.CheckboxHitSize().Y);
        M3Widgets.Checkbox("##print", ref _alarmCache.NewPrintMessage);
        if (chatHeaderWidth > checkboxWidth)
        {
            ImGui.SameLine(0f, 0f);
            ImGui.Dummy(new Vector2(chatHeaderWidth - checkboxWidth, 0f));
        }

        ImGui.TableNextColumn();
        Ui.CenterInRow(rowHeight, M3Widgets.ComboHeight);
        M3Widgets.Combo("##sound", ref _alarmCache.NewSoundIdx, AlarmCache.SoundIdNames, AlarmSoundWidth);
        ImGui.TableNextColumn();
        ImGui.Dummy(new Vector2(MathF.Ceiling(M3Text.Measure("Location", M3TextStyle.Label).X), 0f));
        ImGui.TableNextColumn();
        ImGui.Dummy(new Vector2(MathF.Ceiling(M3Text.Measure("Next", M3TextStyle.Label).X), 0f));
    }

    private void DrawAlarmInfo(AlarmGroup group, int idx)
    {
        using var id = ImRaii.PushId(RuntimeHelpers.GetHashCode(group));
        DrawGroupData(group, idx);
        ImGui.Dummy(new Vector2(0, M3.Space2));
        DrawAlarmTable(group);
    }

    private void DrawAlarmGroupHeaderLine()
    {
        var current = _alarmCache.Selector.Current;
        var row = new Ui.Row(M3Widgets.ButtonHeight);
        row.Place(M3Widgets.IconButtonSize);
        if (M3Widgets.IconButton("##copy", FontAwesomeIcon.Copy, "Copy this alarm group to your clipboard", enabled: current != null))
        {
            try
            {
                var s = new AlarmGroup.Config(current!).ToBase64();
                ImGui.SetClipboardText(s);
                Communicator.PrintClipboardMessage("Alarm Group ", current!.Name);
            }
            catch (Exception e)
            {
                GatherBuddy.Log.Error($"Could not write Alarm Group {current!.Name} to Clipboard:\n{e}");
                Communicator.PrintClipboardMessage("Alarm Group ", current!.Name, e);
            }
        }

        row.Next(M3Widgets.ButtonHeight, M3.Space1);
        if (M3Widgets.Button("##preset", "Make gather window preset", M3ButtonStyle.Text, FontAwesomeIcon.ListUl, enabled: current != null,
                tooltip: "Create a new gather window preset from this alarm group."))
        {
            _plugin.GatherWindowManager.AddPreset(new GatherWindowPreset(current!));
            M3Snackbar.Show("Created a gather window preset.");
        }

        row.Next(ImGui.GetFrameHeight(), M3.Space2);
        M3Widgets.HelpMarker("Use /gather alarm to gather the last item alarm triggered.\n"
          + "Use /gatherfish alarm to gather the last fish alarm triggered.");
    }

    private void DrawAlarmPage()
    {
        using var id = ImRaii.PushId("Alarms");
        _alarmCache.Selector.Draw(SelectorWidth);
        ImGui.SameLine(0, M3.Space2);

        ItemDetailsWindow.Draw("Alarm Group Details", DrawAlarmGroupHeaderLine, () =>
        {
            if (_alarmCache.Selector.Current != null)
            {
                DrawAlarmInfo(_alarmCache.Selector.Current, _alarmCache.Selector.CurrentIdx);
            }
            else
            {
                M3Widgets.EmptyState("##noAlarms", "No alarm group selected", "Add a group with the plus button below the list.", FontAwesomeIcon.Bell);
            }
        });
    }
}
