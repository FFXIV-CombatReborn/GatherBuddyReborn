using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using GatherBuddy.AutoGather.Extensions;
using GatherBuddy.AutoGather.Helpers;
using GatherBuddy.AutoGather.Lists;
using GatherBuddy.Classes;
using GatherBuddy.Crafting;
using GatherBuddy.CustomInfo;
using GatherBuddy.Interfaces;
using GatherBuddy.Plugin;
using GatherBuddy.RebornUI;
using Lumina.Text.ReadOnly;
using RebornMaterial;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Text.RegularExpressions;
using ImRaii = Dalamud.Interface.Utility.Raii.ImRaii;

namespace GatherBuddy.Gui;

public partial class Interface
{
    private record class AutoGatherListsDragDropData(AutoGatherList List, IGatherable Item, int ItemIdx)
    {
        public static string Label => "AutoGatherListItem";
    }

    private class AutoGatherListsCache : IDisposable
    {
        public AutoGatherListsCache()
        {
            UpdateGatherables();
            WorldData.WorldLocationsChanged += UpdateGatherables;
            _plugin.AutoGatherListsManager.ListOrderChanged += OnListOrderChanged;
        }

        private void OnListOrderChanged()
        {
            AutoGatherListSelector.RefreshView();
        }

        public readonly AutoGatherListSelector Selector = new();

        public ReadOnlyCollection<IGatherable> AllGatherables { get; private set; }
        public ReadOnlyCollection<IGatherable> FilteredGatherables { get; private set; }
        private HashSet<IGatherable> ExcludedGatherables = [];

        public void SetExcludedGatherbales(IEnumerable<IGatherable> exclude)
        {
            var excludeSet = new HashSet<IGatherable>(exclude);
            if (!ExcludedGatherables.SetEquals(excludeSet))
            {
                var newGatherables = new List<IGatherable>(AllGatherables.Count);
                foreach (var gatherable in AllGatherables)
                {
                    if (!excludeSet.Contains(gatherable))
                    {
                        newGatherables.Add(gatherable);
                    }
                }

                UpdateGatherables(newGatherables.AsReadOnly(), excludeSet);
            }
        }

        private static ReadOnlyCollection<IGatherable> GenAllGatherables()
        {
            var all = new List<(IGatherable Item, string Name, int Index)>();
            var seenItemIds = new HashSet<uint>();
            foreach (var gatherable in GatherBuddy.GameData.Gatherables.Values)
            {
                if (HasWorldPositions(gatherable) && seenItemIds.Add(gatherable.ItemId))
                {
                    all.Add((gatherable, gatherable.Name[GatherBuddy.Language], all.Count));
                }
            }

            foreach (var fish in GatherBuddy.GameData.Fishes.Values)
            {
                if (seenItemIds.Add(fish.ItemId))
                {
                    all.Add((fish, fish.Name[GatherBuddy.Language], all.Count));
                }
            }

            all.Sort(static (lhs, rhs) =>
            {
                var order = string.Compare(lhs.Name, rhs.Name, StringComparison.CurrentCulture);
                return order != 0 ? order : lhs.Index.CompareTo(rhs.Index);
            });
            return all.ConvertAll(static entry => entry.Item).AsReadOnly();
        }

        private static bool HasWorldPositions(Gatherable gatherable)
        {
            foreach (var node in gatherable.NodeList)
            {
                foreach (var positions in node.WorldPositions.Values)
                {
                    if (positions.Count > 0)
                    {
                        return true;
                    }
                }
            }

            return false;
        }


        [MemberNotNull(nameof(FilteredGatherables)), MemberNotNull(nameof(AllGatherables))]
        private void UpdateGatherables()
            => UpdateGatherables(AllGatherables = GenAllGatherables(), []);

        [MemberNotNull(nameof(FilteredGatherables))]
        private void UpdateGatherables(ReadOnlyCollection<IGatherable> newGatherables, HashSet<IGatherable> newExcluded)
        {
            while (NewGatherableIdx > 0)
            {
                var item = FilteredGatherables![NewGatherableIdx];
                var idx = newGatherables.IndexOf(item);
                if (idx < 0)
                {
                    NewGatherableIdx--;
                }
                else
                {
                    NewGatherableIdx = idx;
                    break;
                }
            }

            FilteredGatherables = newGatherables;
            ExcludedGatherables = newExcluded;
        }

        public void Dispose()
        {
            WorldData.WorldLocationsChanged -= UpdateGatherables;
            _plugin.AutoGatherListsManager.ListOrderChanged -= OnListOrderChanged;
        }

        public int NewGatherableIdx;
        public string ItemFilter = string.Empty;
        public AutoGatherList? ItemFilterList;
    }

    private readonly AutoGatherListsCache _autoGatherListsCache;

    public AutoGatherList? CurrentAutoGatherList
        => _autoGatherListsCache.Selector.Selected;

    public static CraftingListDefinition? CurrentCraftingList
        => _plugin._vulcanWindow?.CurrentCraftingList;

    private static readonly string[] AutoGatherItemColumns = ["##drag", "##delete", "On", "Item", "Have", "Want", "Location"];

    private void DrawAutoGatherListsLine()
    {
        var current = _autoGatherListsCache.Selector.Selected;
        if (M3Widgets.IconButton("##copy", FontAwesomeIcon.Copy, "Copy this auto-gather list to your clipboard", enabled: current != null))
        {
            try
            {
                ImGui.SetClipboardText(new AutoGatherList.Config(current!).ToBase64());
                Communicator.PrintClipboardMessage("Auto-gather list ", current!.Name);
            }
            catch (Exception e)
            {
                Communicator.PrintClipboardMessage("Auto-gather list ", current!.Name, e);
            }
        }

        if (Reflection.ArtisanExporter.ArtisanAssemblyEnabled)
        {
            ImGui.SameLine(0, M3.Space1);
            if (M3Widgets.Button("##artisan", "Import from Artisan", M3ButtonStyle.Text, FontAwesomeIcon.FileImport,
                    tooltip: "Pick one of your Artisan lists to import into a new auto-gather list."))
            {
                M3Menu.Open("##artisanImport");
            }

            using var menu = M3Menu.Begin("##artisanImport");
            if (menu.IsOpen)
            {
                M3Menu.Heading("Import an Artisan list");
                foreach (var kvp in GatherBuddy.AutoGather.ArtisanExporter.GetArtisanListNames())
                {
                    if (M3Menu.Item($"##artisan{kvp.Key}", kvp.Value, FontAwesomeIcon.ClipboardList, tooltip: $"{kvp.Value} ({kvp.Key})"))
                    {
                        Communicator.Print($"Importing '{kvp.Value}' from Artisan...");
                        GatherBuddy.AutoGather.ArtisanExporter.StartArtisanImport(kvp);
                    }
                }
            }
        }

        ImGui.SameLine(0, M3.Space1);
        if (M3Widgets.Button("##teamcraft", "Import from TeamCraft", M3ButtonStyle.Text, FontAwesomeIcon.Paste, enabled: current != null,
                tooltip: "Add the items of a TeamCraft list in your clipboard to this list."))
        {
            ImportTeamCraftList(current!);
        }

        ImGui.SameLine(0, M3.Space2);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (M3Widgets.ButtonHeight - M3Widgets.HelpMarkerWidth()) * 0.5f);
        M3Widgets.HelpMarker(
            "If the config option to sort by location is not selected, items are gathered in the order of the enabled lists, then in the order of items in each list, "
          + "but timed nodes and fish are always prioritized.\n"
          + "Drag lists in the tree to move them, or onto a folder to put them in it.\n"
          + "Drag items by their handle to rearrange them, or onto another list in the tree to move them there.\n"
          + "In the Gather Window, hold Control and right-click an item to delete it from the list it belongs to.");
        ImGui.SameLine(0, M3.Space1);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() - (M3Widgets.ButtonHeight - M3Widgets.HelpMarkerWidth()) * 0.5f);
        if (M3Widgets.IconButton("##discord", FontAwesomeIcon.Comments, "Open the Auto-Gather support Discord"))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://discord.gg/p54TZMPnC9",
                UseShellExecute = true,
            });
        }
    }

    private static void ImportTeamCraftList(AutoGatherList list)
    {
        string clipboardText;
        try
        {
            clipboardText = ImGui.GetClipboardText() ?? string.Empty;
        }
        catch
        {
            clipboardText = string.Empty;
        }

        if (string.IsNullOrEmpty(clipboardText))
        {
            M3Snackbar.Show("Your clipboard is empty.", severity: M3Severity.Error);
            return;
        }

        try
        {
            var matches = Regex.Matches(clipboardText, @"\b(\d+)x\s(.+)\b");
            Dictionary<ReadOnlySeString, uint>? diademItems = null;
            Lumina.Excel.ExcelSheet<Lumina.Excel.Sheets.Item>? itemSheet = null;
            Dictionary<string, IGatherable> normalItems = [with(GatherBuddy.GameData.Gatherables.Count + GatherBuddy.GameData.Fishes.Count)];
            foreach (var gatherableItem in GatherBuddy.GameData.Gatherables.Values)
            {
                normalItems[gatherableItem.Name[GatherBuddy.Language]] = gatherableItem;
            }

            foreach (var fishItem in GatherBuddy.GameData.Fishes.Values)
            {
                normalItems[fishItem.Name[GatherBuddy.Language]] = fishItem;
            }

            var added = 0;
            foreach (Match match in matches)
            {
                var quantity = uint.Parse(match.Groups[1].Value);
                var itemName = match.Groups[2].Value;

                if (normalItems.TryGetValue(itemName, out var item))
                {
                    if (item.Locations.Count == 0)
                    {
                        continue;
                    }
                }
                else
                {
                    itemSheet ??= Dalamud.GameData.GetExcelSheet<Lumina.Excel.Sheets.Item>(GatherBuddy.Language);
                    if (diademItems == null)
                    {
                        diademItems = new Dictionary<ReadOnlySeString, uint>(Diadem.ApprovedToRawItemIds.Count);
                        foreach (var (approvedId, rawItemId) in Diadem.ApprovedToRawItemIds)
                        {
                            diademItems[itemSheet.GetRow(approvedId).Name] = rawItemId;
                        }
                    }

                    if (!diademItems.TryGetValue(itemName, out var rawId))
                    {
                        continue;
                    }

                    if (GatherBuddy.GameData.Gatherables.TryGetValue(rawId, out var gatherable))
                    {
                        item = gatherable;
                    }
                    else if (GatherBuddy.GameData.Fishes.TryGetValue(rawId, out var fish))
                    {
                        item = fish;
                    }
                    else
                    {
                        continue;
                    }
                }

                if (!list.Add(item, quantity))
                {
                    list.SetQuantity(item, quantity + list.Quantities[item]);
                }

                ++added;
            }

            _plugin.AutoGatherListsManager.Save();
            if (list.Enabled)
            {
                _plugin.AutoGatherListsManager.SetActiveItems();
            }

            M3Snackbar.Show(added == 0 ? "No gatherable items were found in your clipboard." : $"Added {added} items from TeamCraft.",
                severity: added == 0 ? M3Severity.Warning : M3Severity.Neutral);
        }
        catch (Exception e)
        {
            Communicator.PrintClipboardMessage("Error importing auto-gather list", e.ToString());
        }
    }

    private static void DrawAutoGatherListOption(string label, string tooltip, bool current, Action<bool> setter)
    {
        var value = current;
        if (M3Widgets.LabeledCheckbox(label, ref value, tooltip: tooltip) && value != current)
        {
            setter(value);
        }
    }

    private void DrawAutoGatherList(AutoGatherList list)
    {
        if (Ui.CommitField("##listName", "Name", list.Name, out var newName, SetInputWidth, 64))
        {
            _plugin.AutoGatherListsManager.ChangeName(list, newName);
        }

        ImGui.SameLine(0, M3.Space2);
        if (Ui.CommitField("##listDesc", "Description", list.Description, out var newDesc, 2 * SetInputWidth, 128))
        {
            _plugin.AutoGatherListsManager.ChangeDescription(list, newDesc);
        }

        var enabled = list.Enabled;
        if (M3Widgets.Switch("##listEnabled", ref enabled) && enabled != list.Enabled)
        {
            _plugin.AutoGatherListsManager.ToggleList(list);
        }

        M3Tooltip.Hovered("Gather the enabled items of this list.");
        ImGui.SameLine(0, M3.Space2);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (M3Widgets.SwitchSize().Y - ImGui.GetTextLineHeight()) * 0.5f);
        M3Text.Draw(enabled ? "Enabled" : "Disabled", M3TextStyle.Muted);
        ImGui.SameLine(0, M3.Space3 * 2);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() - (M3Widgets.SwitchSize().Y - ImGui.GetTextLineHeight()) * 0.5f);
        DrawAutoGatherListOption("Fallback##list",
            "Items from fallback lists won't be auto-gathered on their own.\n"
          + "But if a node doesn't contain any items from regular lists, or you gathered enough of them,\n"
          + "items from fallback lists are gathered instead if that node has them.",
            list.Fallback, v => _plugin.AutoGatherListsManager.SetFallback(list, v));
        ImGui.SameLine(0, M3.Space3);
        DrawAutoGatherListOption("Remove completed##list",
            "Remove enabled items from this list once your inventory reaches their quantity.",
            list.RemoveCompletedItems, v => _plugin.AutoGatherListsManager.SetRemoveCompletedItems(list, v));

        if (!ReferenceEquals(_autoGatherListsCache.ItemFilterList, list))
        {
            _autoGatherListsCache.ItemFilterList = list;
            _autoGatherListsCache.ItemFilter = string.Empty;
        }

        ImGui.Dummy(new Vector2(0, M3.Space1));
        var itemFilter = _autoGatherListsCache.ItemFilter;
        if (M3Widgets.SearchField("##autoGatherItemFilter", "Search items", ref itemFilter, 220f * M3.Scale))
        {
            _autoGatherListsCache.ItemFilter = itemFilter;
        }

        M3Tooltip.Hovered("Filter items by name. Reordering is off while a search is active.");

        var filterKeywords = _autoGatherListsCache.ItemFilter.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var filteringItems = filterKeywords.Length > 0;
        var visibleItemIndices = new List<int>(list.Items.Count);
        for (var i = 0; i < list.Items.Count; ++i)
        {
            var itemName = list.Items[i].Name[GatherBuddy.Language].ToString();
            if (Array.TrueForAll(filterKeywords, keyword => itemName.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            {
                visibleItemIndices.Add(i);
            }
        }

        var visibleItems = visibleItemIndices.ConvertAll(index => list.Items[index]);
        var rowTop = ImGui.GetItemRectMin().Y;
        var rowHeight = ImGui.GetItemRectSize().Y;

        void SameRow(float height)
        {
            ImGui.SameLine(0, M3.Space1);
            ImGui.SetCursorScreenPos(new Vector2(ImGui.GetCursorScreenPos().X, rowTop + (rowHeight - height) * 0.5f));
        }

        SameRow(M3Widgets.IconButtonSize);
        if (M3Widgets.IconButton("##enableVisible", FontAwesomeIcon.Check, "Enable the items shown", enabled: visibleItems.Count > 0))
        {
            _plugin.AutoGatherListsManager.ChangeEnabled(list, visibleItems, true);
        }

        SameRow(M3Widgets.IconButtonSize);
        if (M3Widgets.IconButton("##disableVisible", FontAwesomeIcon.Ban, "Disable the items shown", enabled: visibleItems.Count > 0))
        {
            _plugin.AutoGatherListsManager.ChangeEnabled(list, visibleItems, false);
        }

        SameRow(ImGui.GetTextLineHeight());
        M3Text.Draw(filteringItems ? $"{visibleItems.Count} of {list.Items.Count} items" : $"{list.Items.Count} items", M3TextStyle.Muted);

        var excludedGatherables = new List<IGatherable>(list.Items.Count);
        foreach (var listItem in list.Items)
        {
            if (listItem is Gatherable)
            {
                excludedGatherables.Add(listItem);
            }
        }

        _autoGatherListsCache.SetExcludedGatherbales(excludedGatherables);
        DrawAutoGatherItemTable(list, visibleItemIndices, filteringItems);
    }

    private bool DrawFilteredGatherableCombo(string id, ref int index, string emptyText, float width)
    {
        var gatherables = _autoGatherListsCache.FilteredGatherables;
        return M3Widgets.Combo(id, ref index, gatherables.Count, i => gatherables[i].Name[GatherBuddy.Language], width, emptyText, true);
    }

    private void DrawAutoGatherItemTable(AutoGatherList list, List<int> visibleItemIndices, bool filteringItems)
    {
        var gatherables = _autoGatherListsCache.FilteredGatherables;
        int changeIndex = -1, changeItemIndex = -1, deleteIndex = -1;
        var comboWidth = SetInputWidth * 1.25f;

        using (var style = M3Table.PushStyle())
        using (var table = ImRaii.Table("##autoGatherItems", AutoGatherItemColumns.Length,
                   ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg | ImGuiTableFlags.PadOuterX | ImGuiTableFlags.ScrollX))
        {
            if (table)
            {
                foreach (var column in AutoGatherItemColumns)
                {
                    ImGui.TableSetupColumn(column);
                }

                ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
                foreach (var column in AutoGatherItemColumns)
                {
                    ImGui.TableNextColumn();
                    if (column == "On" && list.Items.Count > 0)
                    {
                        var allEnabled = true;
                        var anyEnabled = false;
                        foreach (var listItem in list.Items)
                        {
                            if (list.EnabledItems[listItem])
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
                            _plugin.AutoGatherListsManager.ChangeEnabled(list, [.. list.Items], allEnabled);
                        }

                        M3Tooltip.Hovered((allEnabled ? "Disable" : "Enable") + " all items in the list");
                    }
                    else if (!column.StartsWith("##"))
                    {
                        M3Table.Header(column, center: false);
                    }
                }

                foreach (var i in visibleItemIndices)
                {
                    var item = list.Items[i];
                    using var id = ImRaii.PushId((int)item.ItemId);

                    ImGui.TableNextColumn();
                    DrawAutoGatherItemHandle(list, item, i, filteringItems);

                    ImGui.TableNextColumn();
                    if (M3Widgets.IconButton("##delete", FontAwesomeIcon.Trash, "Remove this item from the list", diameter: ImGui.GetFrameHeight()))
                    {
                        deleteIndex = i;
                    }

                    ImGui.TableNextColumn();
                    var enabled = list.EnabledItems[item];
                    if (M3Widgets.Checkbox("##enabled", ref enabled))
                    {
                        _plugin.AutoGatherListsManager.ChangeEnabled(list, item, enabled);
                    }

                    ImGui.TableNextColumn();
                    var newIdx = -1;
                    if (DrawFilteredGatherableCombo("##item", ref newIdx, item.Name[GatherBuddy.Language], comboWidth) && newIdx >= 0)
                    {
                        changeIndex = i;
                        changeItemIndex = newIdx;
                    }

                    ImGui.TableNextColumn();
                    var invTotal = item.GetTotalCount(list.UsesRetainerInventory);
                    var quantity = list.Quantities.TryGetValue(item, out var q) ? (int)q : 1;
                    Ui.RightAligned(invTotal.ToString("N0"));
                    M3Tooltip.Hovered(list.UsesRetainerInventory ? "In your inventory and retainers" : "In your inventory");

                    ImGui.TableNextColumn();
                    if (M3Widgets.NumberField("##quantity", ref quantity, 120f * M3.Scale, 1, 1, int.MaxValue))
                    {
                        _plugin.AutoGatherListsManager.ChangeQuantity(list, item, (uint)Math.Max(1, quantity));
                    }

                    ImGui.TableNextColumn();
                    if (DrawLocationInput(item, list.PreferredLocations.GetValueOrDefault(item), out var newLoc))
                    {
                        _plugin.AutoGatherListsManager.ChangePreferredLocation(list, item, newLoc);
                    }
                }

                using var addId = ImRaii.PushId(-1);
                ImGui.TableNextColumn();
                ImGui.TableNextColumn();
                if (M3Widgets.IconButton("##add", FontAwesomeIcon.Plus, "Add the chosen item at the end of the list", M3ButtonStyle.Tonal,
                        diameter: ImGui.GetFrameHeight(), enabled: _autoGatherListsCache.NewGatherableIdx < gatherables.Count))
                {
                    _plugin.AutoGatherListsManager.AddItem(list, gatherables[_autoGatherListsCache.NewGatherableIdx]);
                }

                ImGui.TableNextColumn();
                ImGui.TableNextColumn();
                var idx = _autoGatherListsCache.NewGatherableIdx;
                if (DrawFilteredGatherableCombo("##newItem", ref idx, "Add an item", comboWidth))
                {
                    _autoGatherListsCache.NewGatherableIdx = idx;
                    _plugin.AutoGatherListsManager.AddItem(list, gatherables[idx]);
                }
            }
        }

        if (visibleItemIndices.Count == 0 && list.Items.Count > 0)
        {
            M3Text.Draw("No items match the search.", M3TextStyle.Faint);
        }

        if (deleteIndex >= 0)
        {
            _plugin.AutoGatherListsManager.RemoveItem(list, deleteIndex);
        }

        if (changeIndex >= 0)
        {
            _plugin.AutoGatherListsManager.ChangeItem(list, gatherables[changeItemIndex], changeIndex);
        }
    }

    private void DrawAutoGatherItemHandle(AutoGatherList list, IGatherable item, int index, bool filteringItems)
    {
        var size = new Vector2(M3Draw.MeasureIcon(FontAwesomeIcon.GripVertical).X + M3.Space2, ImGui.GetFrameHeight());
        var min = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("##handle", size);
        var hovered = ImGui.IsItemHovered();
        M3Draw.IconCentered(ImGui.GetWindowDrawList(), FontAwesomeIcon.GripVertical, min, min + size,
            filteringItems ? M3Text.ColorOf(M3TextStyle.Faint) : hovered ? M3.Scheme.OnSurface : M3.Scheme.OnSurfaceVariant);
        if (filteringItems)
        {
            M3Tooltip.Hovered("Clear the search to reorder items.");
            return;
        }

        if (hovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeNs);
        }

        using (var source = ImRaii.DragDropSource())
        {
            if (source.Success)
            {
                _autoGatherListsCache.Selector.DragDropItem = new AutoGatherListsDragDropData(list, item, index);
                ImGui.SetDragDropPayload(AutoGatherListsDragDropData.Label, []);
                ImGui.TextUnformatted(item.Name[GatherBuddy.Language]);
            }
        }

        using var target = ImRaii.DragDropTarget();
        var dragDropData = _autoGatherListsCache.Selector.DragDropItem;
        if (target.Success && !ImGui.AcceptDragDropPayload(AutoGatherListsDragDropData.Label).IsNull && dragDropData != null)
        {
            _plugin.AutoGatherListsManager.MoveItem(dragDropData.List, dragDropData.ItemIdx, index);
            _autoGatherListsCache.Selector.DragDropItem = null;
        }
    }

    private static void DrawAutoGatherStatus()
    {
        var enabled = GatherBuddy.AutoGather.Enabled;
        if (M3Widgets.RowSwitch("Auto-Gather##auto_gather_enabled", ref enabled, $"Status: {GatherBuddy.AutoGather.AutoStatus}"))
        {
            GatherBuddy.AutoGather.Enabled = enabled;
        }
    }

    private static bool DrawSplitter(string id)
    {
        var scale = M3.Scale;
        var width = 8f * scale;
        ImGui.InvisibleButton(id, new Vector2(width, -1f));
        var hovered = ImGui.IsItemHovered();
        var active = ImGui.IsItemActive();
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var x = MathF.Round((min.X + max.X) * 0.5f);
        var s = M3.Scheme;
        ImGui.GetWindowDrawList().AddLine(new Vector2(x, min.Y + 4f * scale), new Vector2(x, max.Y - 4f * scale),
            M3.U32(active || hovered ? s.Primary : s.OutlineVariant, active ? 1f : hovered ? 0.8f : 0.55f), (active || hovered ? 2f : 1f) * scale);
        if (hovered || active)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEw);
        }

        return active;
    }

    private void DrawAutoGatherPage()
    {
        using var id = ImRaii.PushId("AutoGatherLists");
        DrawAutoGatherStatus();

        var selectorWidth = AutoGatherListSelector.SelectorWidth;
        using (var child = ImRaii.Child("AutoGatherListSelector", new Vector2(selectorWidth, -1), false))
        {
            if (child)
            {
                _autoGatherListsCache.Selector.Draw();
            }
        }

        ImGui.SameLine(0f, 0f);
        if (DrawSplitter("##splitter"))
        {
            var delta = ImGui.GetIO().MouseDelta.X;
            selectorWidth += delta;
            selectorWidth = Math.Clamp(selectorWidth, 150f * Scale, ImGui.GetWindowWidth() * 0.5f);
            AutoGatherListSelector.SelectorWidth = selectorWidth;
        }

        ImGui.SameLine(0f, 0f);

        ItemDetailsWindow.Draw("List Details", DrawAutoGatherListsLine, () =>
        {
            if (_autoGatherListsCache.Selector.Selected != null)
            {
                DrawAutoGatherList(_autoGatherListsCache.Selector.Selected);
            }
            else
            {
                M3Widgets.EmptyState("##noList", "No list selected", "Pick a list on the left, or create one.", FontAwesomeIcon.Robot);
            }
        });
    }
}