using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using GatherBuddy.Alarms;
using GatherBuddy.GatherHelper;
using GatherBuddy.Interfaces;
using GatherBuddy.Plugin;
using GatherBuddy.RebornUI;
using RebornMaterial;
using System;
using System.Numerics;
using ImRaii = Dalamud.Interface.Utility.Raii.ImRaii;

namespace GatherBuddy.Gui;

public partial class Interface
{
    private class GatherWindowDragDropData(GatherWindowPreset preset, IGatherable item, int idx)
    {
        public GatherWindowPreset Preset = preset;
        public IGatherable Item = item;
        public int ItemIdx = idx;
    }

    private class GatherWindowCache
    {
        public class GatherWindowSelector : ItemSelector<GatherWindowPreset>
        {
            public GatherWindowSelector()
                : base(_plugin.GatherWindowManager.Presets, Flags.All)
            { }

            protected override bool Filtered(int idx)
                => Filter.Length != 0 && !Items[idx].Name.Contains(Filter, StringComparison.InvariantCultureIgnoreCase);

            protected override bool OnDraw(int idx)
                => ListRow(CheckUnnamed(Items[idx].Name), idx == CurrentIdx, !Items[idx].Enabled);

            protected override string NameOf(GatherWindowPreset item)
                => item.Name;

            protected override bool OnDelete(int idx)
            {
                _plugin.GatherWindowManager.DeletePreset(idx);
                return true;
            }

            protected override bool OnAdd(string name)
            {
                _plugin.GatherWindowManager.AddPreset(new GatherWindowPreset()
                {
                    Name = name,
                });
                return true;
            }

            protected override bool OnClipboardImport(string name, string data)
            {
                if (!GatherWindowPreset.Config.FromBase64(data, out var cfg))
                {
                    return false;
                }

                GatherWindowPreset.FromConfig(cfg, out var preset);
                preset.Name = name;
                _plugin.GatherWindowManager.AddPreset(preset);
                return true;
            }

            protected override bool OnDuplicate(string name, int idx)
            {
                if (Items.Count <= idx || idx < 0)
                {
                    return false;
                }

                var preset = _plugin.GatherWindowManager.Presets[idx].Clone();
                preset.Name = name;
                _plugin.GatherWindowManager.AddPreset(preset);
                return true;
            }

            protected override void OnDrop(object? data, int idx)
            {
                if (Items.Count <= idx || idx < 0 || data is not GatherWindowDragDropData obj)
                {
                    return;
                }

                var preset = _plugin.GatherWindowManager.Presets[idx];
                _plugin.GatherWindowManager.RemoveItem(obj.Preset, obj.ItemIdx);
                _plugin.GatherWindowManager.AddItem(preset, obj.Item);
            }

            protected override bool OnMove(int idx1, int idx2)
            {
                _plugin.GatherWindowManager.MovePreset(idx1, idx2);
                return true;
            }
        }

        public readonly GatherWindowSelector Selector = new();

        public int NewGatherableIdx;
    }

    private readonly GatherWindowCache _gatherWindowCache;

    private void DrawGatherWindowPresetHeaderLine()
    {
        var current = _gatherWindowCache.Selector.Current;
        if (M3Widgets.IconButton("##copy", FontAwesomeIcon.Copy, "Copy this gather window preset to your clipboard", enabled: current != null))
        {
            try
            {
                var s = new GatherWindowPreset.Config(current!).ToBase64();
                ImGui.SetClipboardText(s);
                Communicator.PrintClipboardMessage("Gather window preset ", current!.Name);
            }
            catch (Exception e)
            {
                Communicator.PrintClipboardMessage("Gather window preset ", current!.Name, e);
            }
        }

        ImGui.SameLine(0, M3.Space1);
        if (M3Widgets.Button("##alarms", "Make alarms", M3ButtonStyle.Text, FontAwesomeIcon.Bell, enabled: current != null,
                tooltip: "Create a new alarm group from this gather window preset."))
        {
            _plugin.AlarmManager.AddGroup(new AlarmGroup(current!));
            M3Snackbar.Show("Created an alarm group.");
        }

        ImGui.SameLine(0, M3.Space2);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (M3Widgets.ButtonHeight - M3Widgets.HelpMarkerWidth()) * 0.5f);
        M3Widgets.HelpMarker(
            "If the gather window is not sorted by uptimes, items are added once each, in the order of the enabled presets and then their items.\n"
          + "Drag presets in the list to move them.\n"
          + "Drag items in a preset to move them.\n"
          + "Drag an item onto another preset in the list to move it there.\n"
          + "In the gather window itself, hold Control and right-click an item to delete it from its preset. Removing the last item of a preset removes the preset too.");
    }

    private void DrawGatherWindowPreset(GatherWindowPreset preset)
    {
        if (Ui.CommitField("##presetName", "Name", preset.Name, out var newName, SetInputWidth, 64))
        {
            _plugin.GatherWindowManager.ChangeName(preset, newName);
        }

        ImGui.SameLine(0, M3.Space2);
        if (Ui.CommitField("##presetDesc", "Description", preset.Description, out var newDesc, 2 * SetInputWidth, 128))
        {
            _plugin.GatherWindowManager.ChangeDescription(preset, newDesc);
        }

        ImGui.SameLine(0, M3.Space3);
        var enabled = preset.Enabled;
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (M3TextField.Height - M3Widgets.SwitchSize().Y) * 0.5f);
        if (M3Widgets.Switch("##presetEnabled", ref enabled) && enabled != preset.Enabled)
        {
            _plugin.GatherWindowManager.TogglePreset(preset);
        }

        M3Tooltip.Hovered("Show this preset's items in the gather window.");
        ImGui.SameLine(0, M3.Space2);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (M3Widgets.SwitchSize().Y - ImGui.GetTextLineHeight()) * 0.5f);
        M3Text.Draw(enabled ? "Enabled" : "Disabled", M3TextStyle.Muted);

        ImGui.Dummy(new Vector2(0, M3.Space2));
        M3Widgets.SectionLabel($"Items ({preset.Items.Count})");
        var comboWidth = SetInputWidth * 1.4f;
        for (var i = 0; i < preset.Items.Count; ++i)
        {
            using var id = ImRaii.PushId(i);
            var item = preset.Items[i];
            using (ImRaii.Group())
            {
                if (M3Widgets.IconButton("##delete", FontAwesomeIcon.Trash, "Remove this item from the preset", diameter: M3Widgets.ComboHeight))
                {
                    _plugin.GatherWindowManager.RemoveItem(preset, i--);
                    continue;
                }

                ImGui.SameLine(0, M3.Space1);
                var index = Array.IndexOf(GatherGroupCache.AllGatherables, item);
                if (DrawGatherableCombo("##item", ref index, comboWidth))
                {
                    _plugin.GatherWindowManager.ChangeItem(preset, GatherGroupCache.AllGatherables[index], i);
                }
            }

            _gatherWindowCache.Selector.CreateDropSource(new GatherWindowDragDropData(preset, item, i), item.Name[GatherBuddy.Language]);

            var localIdx = i;
            _gatherWindowCache.Selector.CreateDropTarget<GatherWindowDragDropData>(d
                => _plugin.GatherWindowManager.MoveItem(d.Preset, d.ItemIdx, localIdx));
        }

        var newItem = GatherGroupCache.AllGatherables[_gatherWindowCache.NewGatherableIdx];
        var contained = preset.Items.Contains(newItem);
        if (M3Widgets.IconButton("##add", FontAwesomeIcon.Plus,
                contained ? "This item is already in the preset." : "Add this item at the end of the preset", M3ButtonStyle.Tonal,
                diameter: M3Widgets.ComboHeight, enabled: !contained))
        {
            _plugin.GatherWindowManager.AddItem(preset, newItem);
        }

        ImGui.SameLine(0, M3.Space1);
        DrawGatherableCombo("##newItem", ref _gatherWindowCache.NewGatherableIdx, comboWidth);
    }

    private void DrawGatherWindowPage()
    {
        using var id = ImRaii.PushId("GatherWindow");
        _gatherWindowCache.Selector.Draw(SelectorWidth);
        ImGui.SameLine(0, M3.Space2);

        ItemDetailsWindow.Draw("Preset Details", DrawGatherWindowPresetHeaderLine, () =>
        {
            if (_gatherWindowCache.Selector.Current != null)
            {
                DrawGatherWindowPreset(_gatherWindowCache.Selector.Current);
            }
            else
            {
                M3Widgets.EmptyState("##noPreset", "No preset selected", "Add a preset with the plus button below the list.", FontAwesomeIcon.ListUl);
            }
        });
    }
}
