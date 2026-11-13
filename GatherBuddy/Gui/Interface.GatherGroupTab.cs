using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using GatherBuddy.Alarms;
using GatherBuddy.AutoGather.Lists;
using GatherBuddy.Classes;
using GatherBuddy.Config;
using GatherBuddy.GatherGroup;
using GatherBuddy.GatherHelper;
using GatherBuddy.Interfaces;
using GatherBuddy.Plugin;
using GatherBuddy.RebornUI;
using GatherBuddy.Time;
using RebornMaterial;
using System;
using System.Collections.Generic;
using System.Numerics;
using ImRaii = Dalamud.Interface.Utility.Raii.ImRaii;

namespace GatherBuddy.Gui;

public partial class Interface
{
    private class GatherGroupDragDropData(TimedGroup group, TimedGroupNode node, int idx)
    {
        public TimedGroup Group = group;
        public TimedGroupNode Node = node;
        public int NodeIdx = idx;
    }

    private class GatherGroupCache(GatherGroupManager gatherGroupManager)
    {
        public sealed class GatherGroupSelector(GatherGroupManager manager) : ItemSelector<TimedGroup>(manager.Groups.Values, Flags.All & ~Flags.Move)
        {
            private readonly GatherGroupManager _manager = manager;

            protected override bool Filtered(int idx)
                => Filter.Length != 0 && !Items[idx].Name.Contains(Filter, StringComparison.InvariantCultureIgnoreCase);

            protected override bool OnDraw(int idx)
                => ListRow(Items[idx].Name, idx == CurrentIdx);

            protected override string NameOf(TimedGroup item)
                => item.Name;

            protected override bool OnDelete(int idx)
            {
                if (Items.Count <= idx || idx < 0)
                {
                    return false;
                }

                _manager.Groups.RemoveAt(idx);
                _manager.Save();
                return true;
            }

            protected override bool OnAdd(string name)
                => _manager.AddGroup(name, new TimedGroup(name));

            protected override bool OnClipboardImport(string name, string data)
            {
                if (!TimedGroup.Config.FromBase64(data, out var cfgGroup))
                {
                    return false;
                }

                TimedGroup.FromConfig(cfgGroup, out var group);
                group.Name = name;
                return _manager.AddGroup(name, group);
            }

            protected override bool OnDuplicate(string name, int idx)
            {
                if (Items.Count <= idx || idx < 0)
                {
                    return false;
                }

                var group = _manager.Groups.Values[idx].Clone(name);
                return _manager.AddGroup(name, group);
            }

            protected override void OnDrop(object? data, int idx)
            {
                if (Items.Count <= idx || idx < 0 || data is not GatherGroupDragDropData d)
                {
                    return;
                }

                var group = Items[idx];

                if (!GatherGroupManager.ChangeGroupNode(@group, @group.Nodes.Count, d.Node.Item, d.Node.EorzeaStartMinute,
                        d.Node.EorzeaEndMinute, d.Node.Annotation, false))
                {
                    GatherBuddy.Log.Error($"Could not move node from group {d.Group.Name} to group {group.Name}.");
                    return;
                }

                GatherGroupManager.ChangeGroupNode(d.Group, d.NodeIdx, null, null, null, null, true);
                _plugin.GatherGroupManager.Save();
            }
        }

        public static readonly IGatherable[] AllGatherables = CreateAllGatherables();

        private static IGatherable[] CreateAllGatherables()
        {
            var gatherables = new List<(IGatherable Item, string Name, int Index)>();
            foreach (var gatherable in GatherBuddy.GameData.Gatherables.Values)
            {
                if (gatherable.Locations.Count != 0)
                {
                    gatherables.Add((gatherable, gatherable.Name[GatherBuddy.Language], gatherables.Count));
                }
            }

            foreach (var fish in GatherBuddy.GameData.Fishes.Values)
            {
                if (fish.Locations.Count != 0)
                {
                    gatherables.Add((fish, fish.Name[GatherBuddy.Language], gatherables.Count));
                }
            }

            gatherables.Sort(static (lhs, rhs) =>
            {
                var order = string.Compare(lhs.Name, rhs.Name, StringComparison.CurrentCulture);
                return order != 0 ? order : lhs.Index.CompareTo(rhs.Index);
            });
            var result = new IGatherable[gatherables.Count];
            for (var i = 0; i < gatherables.Count; i++)
            {
                result[i] = gatherables[i].Item;
            }

            return result;
        }

        public readonly GatherGroupSelector Selector = new(gatherGroupManager);

        public readonly string DefaultGroupTooltip =
                "Restore the gather groups provided by default if they have been deleted or changed in any way.\n"
              + "Hold Control to apply. Default Groups are:\n\t- "
              + $"{string.Join("\n\t- ", GroupData.DefaultGroups.ConvertAll(g => g.Name))}";
        public int NewItemIdx = 0;

        private bool _itemPerMinuteDirty = true;
        private readonly List<short> _itemPerMinute = [with(24)];

        public void SetDirty()
            => _itemPerMinuteDirty = true;

        public List<short> UpdateItemPerMinute(TimedGroup group)
        {
            if (!_itemPerMinuteDirty && group.Nodes.Count + 1 == _itemPerMinute.Count)
            {
                return _itemPerMinute;
            }

            _itemPerMinute.Clear();
            for (var i = 0; i <= group.Nodes.Count; ++i)
            {
                _itemPerMinute.Add(0);
            }

            for (var i = 0; i < RealTime.MinutesPerDay; ++i)
            {
                var node = group.CurrentNode((uint)i);
                if (node == null)
                {
                    _itemPerMinute[0]++;
                }
                else
                {
                    var idx = group.Nodes.IndexOf(node);
                    _itemPerMinute[idx + 1]++;
                }
            }

            _itemPerMinuteDirty = false;
            return _itemPerMinute;
        }
    }

    private readonly GatherGroupCache _gatherGroupCache;

    private static bool DrawGatherableCombo(string id, ref int index, float width)
        => M3Widgets.Combo(id, ref index, GatherGroupCache.AllGatherables.Length, i => GatherGroupCache.AllGatherables[i].Name[GatherBuddy.Language],
            width, "Choose an item", true);

    private static readonly string[] GatherGroupColumns = ["##delete", "Item", "Eorzea time", "Location", "Length", "Annotation"];

    private static void DrawTimeInput(string label, float width, int value, Action<int> setter)
    {
        var hour = value / RealTime.MinutesPerHour;
        var minute = value % RealTime.MinutesPerHour;
        using var group = ImRaii.Group();
        using var id = ImRaii.PushId(label);
        ImGui.SetNextItemWidth(width);
        using var style = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, Vector2.One * 2 * ImGuiHelpers.GlobalScale);
        var change = ImGui.DragInt("##hour", ref hour, 0.05f, 0, RealTime.HoursPerDay - 1, "%02d", ImGuiSliderFlags.AlwaysClamp);
        ImGui.SameLine();
        ImGui.TextUnformatted(":");
        ImGui.SameLine();
        style.Pop();
        ImGui.SetNextItemWidth(width);
        change |= ImGui.DragInt("##minute", ref minute, 0.2f, 0, RealTime.MinutesPerHour - 1, "%02d", ImGuiSliderFlags.AlwaysClamp);

        if (change)
        {
            var newValue = Math.Clamp(hour * RealTime.MinutesPerHour + minute, 0, RealTime.MinutesPerDay - 1);
            if (newValue != value)
            {
                setter(newValue);
            }
        }
    }

    private void DrawTimeInput(int fromValue, int toValue, Action<int, int> setter)
    {
        var width = 24 * ImGuiHelpers.GlobalScale;
        using var group = ImRaii.Group();
        ImGui.AlignTextToFramePadding();
        using (M3Text.Push(M3TextStyle.Muted))
        {
            ImGui.TextUnformatted("from");
        }

        ImGui.SameLine();
        DrawTimeInput("##from", width, fromValue, v => setter(v, toValue));
        ImGui.SameLine();
        using (M3Text.Push(M3TextStyle.Muted))
        {
            ImGui.TextUnformatted("to");
        }

        ImGui.SameLine();
        DrawTimeInput("##to", width, toValue, v => setter(fromValue, v));
    }

    private static void DrawLocationTooltip(ILocation? loc)
    {
        if (loc != null && ImGui.IsItemHovered())
        {
            M3Tooltip.Show(LocationTooltip(loc));
        }
    }

    private static void DrawLocationInput(TimedGroup group, int nodeIdx, TimedGroupNode node)
    {
        if (DrawLocationInput(node.Item, node.PreferLocation, out var newLoc)
         && GatherGroupManager.ChangeGroupNodeLocation(group, nodeIdx, newLoc))
        {
            _plugin.GatherGroupManager.Save();
        }
    }

    private void DrawGatherGroupNode(TimedGroup group, ref int idx, int minutes)
    {
        var node = group.Nodes[idx];
        using var id = ImRaii.PushId(idx);
        var i = idx;
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        if (M3Widgets.IconButton("##delete", FontAwesomeIcon.Trash, "Remove this item", diameter: ImGui.GetFrameHeight()))
        {
            if (GatherGroupManager.ChangeGroupNode(group, i, null, null, null, null, true))
            {
                --idx;
                _plugin.GatherGroupManager.Save();
                _gatherGroupCache.SetDirty();
            }

            return;
        }

        ImGui.TableNextColumn();
        var itemIdx = Array.IndexOf(GatherGroupCache.AllGatherables, node.Item);
        if (DrawGatherableCombo("##item", ref itemIdx, SetInputWidth * 1.25f)
         && GatherGroupManager.ChangeGroupNode(group, i, GatherGroupCache.AllGatherables[itemIdx], null, null, null, false))
        {
            _plugin.GatherGroupManager.Save();
        }

        _gatherGroupCache.Selector.CreateDropSource(new GatherGroupDragDropData(group, node, i), node.Item.Name[GatherBuddy.Language]);
        _gatherGroupCache.Selector.CreateDropTarget<GatherGroupDragDropData>(d => _plugin.GatherGroupManager.MoveNode(group, d.NodeIdx, i));

        ImGui.TableNextColumn();
        DrawTimeInput(node.EorzeaStartMinute, node.EorzeaEndMinute, (from, to) =>
        {
            if (GatherGroupManager.ChangeGroupNode(group, i, null, from, to, null, false))
            {
                _plugin.GatherGroupManager.Save();
                _gatherGroupCache.SetDirty();
            }
        });

        ImGui.TableNextColumn();
        DrawLocationInput(group, i, node);

        ImGui.TableNextColumn();
        var length = node.Length();
        if (minutes < length)
        {
            Ui.Framed($"{length} minutes", ColorId.WarningBg.Value());
            HoverTooltip($"{length - minutes} minutes are overwritten by overlap with earlier items.");
        }
        else
        {
            Ui.Framed($"{length} minutes", M3.Scheme.SurfaceContainerHighest);
        }

        ImGui.TableNextColumn();
        if (Ui.CommitField("##annotation", "Annotation", node.Annotation, out var annotation, 250 * M3.Scale)
         && GatherGroupManager.ChangeGroupNode(group, i, null, null, null, annotation, false))
        {
            _plugin.GatherGroupManager.Save();
        }
    }

    private void DrawGatherGroupNodeTable(TimedGroup group)
    {
        var times = _gatherGroupCache.UpdateItemPerMinute(group);
        if (times[0] > 0)
        {
            M3Widgets.Banner("##missingTimes", "Not all minutes have an item.", M3Severity.Warning, FontAwesomeIcon.ExclamationTriangle);
        }

        using var style = M3Table.PushStyle();
        using var table = ImRaii.Table("##nodes", GatherGroupColumns.Length, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.ScrollX
          | ImGuiTableFlags.RowBg | ImGuiTableFlags.PadOuterX);
        if (!table)
        {
            return;
        }

        foreach (var column in GatherGroupColumns)
        {
            ImGui.TableSetupColumn(column);
        }

        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        foreach (var column in GatherGroupColumns)
        {
            ImGui.TableNextColumn();
            if (!column.StartsWith("##"))
            {
                M3Table.Header(column, center: false);
            }
        }

        for (var i = 0; i < group.Nodes.Count; ++i)
        {
            DrawGatherGroupNode(group, ref i, times[i + 1]);
        }

        var idx = _gatherGroupCache.NewItemIdx;
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        if (M3Widgets.IconButton("##add", FontAwesomeIcon.Plus, "Add this item", M3ButtonStyle.Tonal, diameter: ImGui.GetFrameHeight()))
        {
            var gatherable = GatherGroupCache.AllGatherables[idx];
            if (gatherable.InternalLocationId > 0)
            {
                var locations = gatherable.Locations;
                if (locations.Count is 1 && locations[0] is GatheringNode node)
                {
                    var changes = false;
                    foreach (var (start, end) in node.Times.AllUptimes())
                    {
                        changes |= GatherGroupManager.ChangeGroupNode(group, group.Nodes.Count, gatherable, (int)start * RealTime.MinutesPerHour,
                            (int)end * RealTime.MinutesPerHour, null, false);
                    }

                    if (changes)
                    {
                        _gatherGroupCache.SetDirty();
                        _plugin.GatherGroupManager.Save();
                    }
                }
            }
            else if (GatherGroupManager.ChangeGroupNode(group, group.Nodes.Count, gatherable, null, null, null, false))
            {
                _gatherGroupCache.SetDirty();
                _plugin.GatherGroupManager.Save();
            }
        }

        ImGui.TableNextColumn();
        if (DrawGatherableCombo("##newItem", ref idx, SetInputWidth * 1.25f))
        {
            _gatherGroupCache.NewItemIdx = idx;
        }
    }

    private static void DrawNameField(TimedGroup group)
    {
        if (!Ui.CommitField("##groupName", "Name", group.Name, out var newName, SetInputWidth, 64))
        {
            return;
        }

        newName = newName.Trim();
        if (newName.Length == 0)
        {
            M3Snackbar.Show("A gather group needs a name.", severity: M3Severity.Error);
        }
        else if (newName != group.Name && _plugin.GatherGroupManager.Groups.ContainsKey(newName.ToLowerInvariant()))
        {
            M3Snackbar.Show($"There already is a gather group named {newName}.", severity: M3Severity.Error);
        }
        else if (_plugin.GatherGroupManager.RenameGroup(group, newName))
        {
            _plugin.GatherGroupManager.Save();
        }
    }

    private static void DrawDescField(TimedGroup group)
    {
        if (Ui.CommitField("##groupDesc", "Description", group.Description, out var newDesc, 2 * SetInputWidth, 128)
         && GatherGroupManager.ChangeDescription(group, newDesc))
        {
            _plugin.GatherGroupManager.Save();
        }
    }

    private void DrawGatherGroup(TimedGroup group)
    {
        using var id = ImRaii.PushId(group.Name);

        DrawNameField(group);
        ImGui.SameLine(0, M3.Space2);
        DrawDescField(group);
        ImGui.Dummy(new Vector2(0, M3.Space2));
        DrawGatherGroupNodeTable(group);
    }

    private void DrawGatherGroupHeaderLine()
    {
        var current = _gatherGroupCache.Selector.Current;
        if (M3Widgets.IconButton("##copy", FontAwesomeIcon.Copy, "Copy this gather group to your clipboard", enabled: current != null))
        {
            try
            {
                ImGui.SetClipboardText(current!.ToConfig().ToBase64());
                Communicator.PrintClipboardMessage("Gather Group ", current.Name);
            }
            catch (Exception e)
            {
                Communicator.PrintClipboardMessage("Gather Group ", current!.Name, e);
            }
        }

        ImGui.SameLine(0, M3.Space1);
        if (M3Widgets.Button("##list", "Make auto-gather list", M3ButtonStyle.Text, FontAwesomeIcon.Robot, enabled: current != null,
                tooltip: "Create a new auto-gather list from this gather group."))
        {
            _plugin.AutoGatherListsManager.AddList(new AutoGatherList(current!));
            M3Snackbar.Show("Created an auto-gather list.");
        }

        ImGui.SameLine(0, M3.Space1);
        if (M3Widgets.Button("##preset", "Make window preset", M3ButtonStyle.Text, FontAwesomeIcon.ListUl, enabled: current != null,
                tooltip: "Create a new gather window preset from this gather group."))
        {
            _plugin.GatherWindowManager.AddPreset(new GatherWindowPreset(current!));
            M3Snackbar.Show("Created a gather window preset.");
        }

        ImGui.SameLine(0, M3.Space1);
        if (M3Widgets.Button("##alarms", "Make alarms", M3ButtonStyle.Text, FontAwesomeIcon.Bell, enabled: current != null,
                tooltip: "Create a new alarm group from this gather group."))
        {
            _plugin.AlarmManager.AddGroup(new AlarmGroup(current!));
            M3Snackbar.Show("Created an alarm group.");
        }

        ImGui.SameLine(0, M3.Space1);
        if (M3Widgets.HoldButton("##restore", "Restore default groups", 1f, M3ButtonStyle.Tonal, FontAwesomeIcon.Undo,
                tooltip: $"{_gatherGroupCache.DefaultGroupTooltip.Replace("Hold Control to apply.", "Press and hold to apply.")}")
         && _plugin.GatherGroupManager.SetDefaults(true))
        {
            _gatherGroupCache.Selector.TryRestoreCurrent();
            _plugin.GatherGroupManager.Save();
            M3Snackbar.Show("Restored the default gather groups.");
        }

        ImGui.SameLine(0, M3.Space2);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (M3Widgets.ButtonHeight - M3Widgets.HelpMarkerWidth()) * 0.5f);
        M3Widgets.HelpMarker("Use /gathergroup [name] [optional:minute offset] to call a group.\n"
          + "This will /gather the item that is up currently (or [minute offset] eorzea minutes in the future).\n"
          + "If times for multiple items overlap, the first item from top to bottom will be gathered.");
    }

    private void DrawGatherGroupPage()
    {
        using var id = ImRaii.PushId("Gather Groups");
        _gatherGroupCache.Selector.Draw(SelectorWidth);
        ImGui.SameLine(0, M3.Space2);

        ItemDetailsWindow.Draw("Group Details", DrawGatherGroupHeaderLine, () =>
        {
            if (_gatherGroupCache.Selector.Current != null)
            {
                DrawGatherGroup(_gatherGroupCache.Selector.Current);
            }
            else
            {
                M3Widgets.EmptyState("##noGroup", "No gather group selected", "Add a group with the plus button below the list.", FontAwesomeIcon.LayerGroup);
            }
        });
    }
}