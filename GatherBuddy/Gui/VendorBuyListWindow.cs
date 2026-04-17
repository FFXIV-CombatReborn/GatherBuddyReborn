using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using ElliLib;
using ElliLib.Raii;
using GatherBuddy.Vulcan.Vendors;
using Lumina.Excel.Sheets;
using ImRaii = ElliLib.Raii.ImRaii;

namespace GatherBuddy.Gui;

public sealed class VendorBuyListWindow : Window
{
    private sealed record VendorListNpcOption(VendorNpc Npc, VendorNpcLocation? Location, string ZoneName);
    public const string WindowId = "Vendor Buy List###VendorBuyListWindow";
    private const string ListNamePopupId = "Vendor Buy List Name###VendorBuyListNamePopup";
    private static readonly Vector4 PanelBackgroundColor = new(0.08f, 0.08f, 0.10f, 1f);

    private readonly Dictionary<uint, string> _zoneNames = new();
    private bool   _wasFocusedLastFrame;
    private Guid?  _editingListId;
    private string _listNameInput = string.Empty;
    private bool   _focusListNameInput;
    private bool   _openListNamePopup;

    public VendorBuyListWindow()
        : base(WindowId)
    {
        Flags |= ImGuiWindowFlags.NoScrollbar;
        Size          = new Vector2(900, 520);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(640, 320),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        ShowCloseButton = true;
        RespectCloseHotkey = true;
        IsOpen = false;
    }

    public void Open()
        => IsOpen = true;

    public bool OpenCreateListPopup(uint itemId)
    {
        var manager = GatherBuddy.VendorBuyListManager;
        if (manager == null)
            return false;

        var list = manager.CreateList("Vendor List", false);
        if (!manager.TryIncrementTarget(list.Id, itemId, 1, selectList: true, openWindow: false, announce: false))
        {
            GatherBuddy.Log.Warning($"[VendorBuyListWindow] Failed to add item {itemId} to newly created vendor list '{list.Name}'.");
            return false;
        }

        Open();
        if (!manager.IsBusy)
            OpenListNamePopup(list.Id, list.Name);
        return true;
    }

    public override void Draw()
    {
        var manager = GatherBuddy.VendorBuyListManager;
        if (manager == null)
            return;

        var isFocused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
        if (isFocused)
        {
            GatherBuddy.ControllerSupport?.UpdateFocusedWindow(WindowId);
            _wasFocusedLastFrame = true;
        }
        else if (_wasFocusedLastFrame)
        {
            GatherBuddy.ControllerSupport?.UpdateFocusedWindow(null);
            _wasFocusedLastFrame = false;
        }

        var activeList = manager.ActiveList;
        if (activeList == null)
            return;

        DrawHeader();

        var avail = ImGui.GetContentRegionAvail();
        var leftWidth = Math.Clamp(avail.X * 0.22f, 190f, 220f);

        using (ImRaii.PushColor(ImGuiCol.ChildBg, PanelBackgroundColor))
        {
            ImGui.BeginChild("##vendorBuyListLeftPanel", new Vector2(leftWidth, avail.Y), true);
            DrawListSidebar(manager, activeList);
            ImGui.EndChild();
        }

        ImGui.SameLine();

        using (ImRaii.PushColor(ImGuiCol.ChildBg, PanelBackgroundColor))
        {
            ImGui.BeginChild("##vendorBuyListRightPanel", new Vector2(0, avail.Y), true);
            DrawActiveListPanel(manager, activeList);
            ImGui.EndChild();
        }

        DrawListNamePopup(manager);
    }

    private static void DrawHeader()
    {
        ImGui.TextColored(ImGuiColors.DalamudYellow, "Vendor Buy Lists");
        ImGui.TextColored(ImGuiColors.DalamudGrey3, "Manage named vendor lists and run the active list through Vulcan.");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
    }

    private void DrawListSidebar(VendorBuyListManager manager, VendorBuyListDefinition activeList)
    {
        var canModifyLists = !manager.IsBusy;

        ImGui.TextColored(ImGuiColors.DalamudYellow, "Lists");
        ImGui.TextColored(ImGuiColors.DalamudGrey3, $"{manager.Lists.Count} total");
        ImGui.Spacing();

        using (ImRaii.Disabled(!canModifyLists))
        {
            if (ImGui.Button("New List", new Vector2(-1, 0)))
            {
                var list = manager.CreateList("Vendor List");
                OpenListNamePopup(list.Id, list.Name);
            }

            var buttonWidth = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) / 2f;
            if (ImGui.Button("Rename", new Vector2(buttonWidth, 0)))
                OpenListNamePopup(activeList.Id, activeList.Name);

            ImGui.SameLine();
            using (ImRaii.Disabled(manager.Lists.Count <= 1))
            {
                if (ImGui.Button("Delete", new Vector2(buttonWidth, 0)))
                    manager.DeleteList(activeList.Id);
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.BeginChild("##vendorBuyListSelectorScroll", new Vector2(-1, 0), false);
        foreach (var list in manager.Lists.OrderBy(list => list.CreatedAt))
            DrawListSidebarEntry(manager, list, activeList.Id == list.Id, canModifyLists);
        ImGui.EndChild();
    }

    private void DrawListSidebarEntry(VendorBuyListManager manager, VendorBuyListDefinition list, bool isSelected, bool canModifyLists)
    {
        var pendingCount = list.Entries.Count(entry => manager.GetRemainingQuantity(entry) > 0);
        var summary = $"{list.Entries.Count} entry(s)";
        if (pendingCount > 0)
            summary += $" · {pendingCount} pending";
        if (manager.IsRunning && manager.RunningListId == list.Id)
            summary += " · running";
        var selectableWidth = Math.Max(1f, ImGui.GetContentRegionAvail().X);

        if (isSelected)
            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.ParsedGold);

        using (ImRaii.Disabled(!canModifyLists))
        {
            if (ImGui.Selectable($"{list.Name}##vendorBuyList_{list.Id}", isSelected, ImGuiSelectableFlags.None, new Vector2(selectableWidth, 0)))
                manager.SetActiveList(list.Id);
        }

        if (isSelected)
            ImGui.PopStyleColor();

        var cursorX = ImGui.GetCursorPosX();
        ImGui.SetCursorPosX(cursorX + 8f);
        ImGui.TextColored(ImGuiColors.DalamudGrey3, summary);
        ImGui.Spacing();
    }

    private void DrawActiveListPanel(VendorBuyListManager manager, VendorBuyListDefinition activeList)
    {
        var entries = manager.Entries.ToList();
        var pending = manager.GetPendingEntryCount();

        ImGui.TextColored(ImGuiColors.ParsedGold, activeList.Name);
        ImGui.TextColored(ImGuiColors.DalamudGrey3, $"{entries.Count} entry(s) · {pending} pending");
        if (!string.IsNullOrWhiteSpace(manager.StatusText))
        {
            ImGui.Spacing();
            ImGui.TextColored(manager.IsRunning ? ImGuiColors.ParsedGold : ImGuiColors.DalamudGrey3, manager.StatusText);
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (!manager.IsRunning)
        {
            using (ImRaii.Disabled(entries.Count == 0 || pending == 0 || manager.IsBusy))
            {
                if (ImGui.Button("Start List", new Vector2(120f, 0)))
                    manager.Start();
            }
        }
        else if (ImGui.Button("Stop", new Vector2(120f, 0)))
        {
            manager.Stop();
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(entries.Count == 0 || manager.IsBusy))
        {
            if (ImGui.Button("Clear List", new Vector2(120f, 0)))
                manager.Clear();
        }

        if (entries.Count == 0)
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            ImGui.TextColored(ImGuiColors.DalamudGrey3, "This list is empty.");
            ImGui.TextColored(ImGuiColors.DalamudGrey3, "Add supported vendor items from the Vendors tab to populate it.");
            return;
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextColored(ImGuiColors.DalamudYellow, "Entries");
        ImGui.Spacing();

        DrawEntryTable(entries, manager);
    }

    private void DrawEntryTable(List<VendorBuyListEntry> entries, VendorBuyListManager manager)
    {
        const ImGuiTableFlags tableFlags = ImGuiTableFlags.BordersOuter | ImGuiTableFlags.BordersInnerV
                                         | ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg
                                         | ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable("##vendorBuyListTable", 8, tableFlags, new Vector2(-1, -1)))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Item",     ImGuiTableColumnFlags.WidthStretch, 1.25f);
        ImGui.TableSetupColumn("Cost",     ImGuiTableColumnFlags.WidthFixed, 58f);
        ImGui.TableSetupColumn("Want",     ImGuiTableColumnFlags.WidthFixed, 78f);
        ImGui.TableSetupColumn("Have",     ImGuiTableColumnFlags.WidthFixed, 54f);
        ImGui.TableSetupColumn("Need",     ImGuiTableColumnFlags.WidthFixed, 54f);
        ImGui.TableSetupColumn("Vendor",   ImGuiTableColumnFlags.WidthStretch, 0.80f);
        ImGui.TableSetupColumn("Location", ImGuiTableColumnFlags.WidthStretch, 1.00f);
        ImGui.TableSetupColumn("##remove", ImGuiTableColumnFlags.WidthFixed, 68f);
        ImGui.TableHeadersRow();

        const float iconSize = 20f;
        var iconVec = new Vector2(iconSize, iconSize);
        var clipper = ImGui.ImGuiListClipper();
        clipper.Begin(entries.Count);
        while (clipper.Step())
        {
            for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                DrawEntryRow(entries[i], manager, iconVec, iconSize);
        }
        clipper.End();
        clipper.Destroy();

        ImGui.EndTable();
    }

    private void OpenListNamePopup(Guid listId, string currentName)
    {
        _editingListId = listId;
        _listNameInput = currentName;
        _focusListNameInput = true;
        _openListNamePopup = true;
    }

    private void DrawListNamePopup(VendorBuyListManager manager)
    {
        if (_openListNamePopup)
        {
            ImGui.OpenPopup(ListNamePopupId);
            _openListNamePopup = false;
        }
        var isOpen = true;
        if (!ImGui.BeginPopupModal(ListNamePopupId, ref isOpen, ImGuiWindowFlags.AlwaysAutoResize))
            return;

        ImGui.Text("List Name");
        if (_focusListNameInput)
        {
            ImGui.SetKeyboardFocusHere();
            _focusListNameInput = false;
        }

        ImGui.SetNextItemWidth(320f);
        var submitted = ImGui.InputText("##vendorBuyListNameInput", ref _listNameInput, 128, ImGuiInputTextFlags.EnterReturnsTrue);

        if (submitted || ImGui.Button("Save"))
        {
            if (_editingListId.HasValue)
                manager.RenameList(_editingListId.Value, _listNameInput);
            _editingListId = null;
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
        {
            _editingListId = null;
            ImGui.CloseCurrentPopup();
        }

        if (!isOpen)
            _editingListId = null;

        ImGui.EndPopup();
    }

    private void DrawEntryRow(VendorBuyListEntry entry, VendorBuyListManager manager, Vector2 iconVec, float iconSize)
    {
        manager.TryResolveLiveEntry(entry, out var liveEntry, out var resolvedVendor, out _);
        var vendorOptions = BuildVendorOptions(liveEntry);
        var selectedVendor = GetSelectedVendorOption(vendorOptions, resolvedVendor);
        var isActive     = manager.ActiveEntryId == entry.Id;
        var currentCount = Math.Max(0, VendorBuyListManager.GetCurrentInventoryAndArmoryCount(entry.ItemId));
        var remaining    = manager.GetRemainingQuantity(entry);
        var targetCount  = entry.TargetQuantity > int.MaxValue ? int.MaxValue : (int)entry.TargetQuantity;

        ImGui.TableNextRow();
        if (isActive)
            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(new Vector4(1.00f, 0.84f, 0.00f, 0.16f)));

        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        var icon = Icons.DefaultStorage.TextureProvider.GetFromGameIcon(new GameIconLookup(entry.IconId));
        if (icon.TryGetWrap(out var wrap, out _))
        {
            ImGui.Image(wrap.Handle, iconVec);
            ImGui.SameLine(0, 4f);
        }
        else
        {
            ImGui.Dummy(iconVec);
            ImGui.SameLine(0, 4f);
        }

        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (iconSize - ImGui.GetTextLineHeight()) / 2f);
        if (isActive)
            ImGui.TextColored(ImGuiColors.ParsedGold, entry.ItemName);
        else
            ImGui.TextUnformatted(entry.ItemName);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(entry.ItemName);

        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted($"{entry.Cost:N0}");

        ImGui.TableNextColumn();
        using (ImRaii.Disabled(isActive || manager.IsBusy))
        {
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputInt($"##vendorBuyListTarget_{entry.Id}", ref targetCount))
                manager.UpdateTargetQuantity(entry.Id, (uint)Math.Max(1, targetCount));
        }

        ImGui.TableNextColumn();
        ImGui.TextUnformatted($"{currentCount:N0}");

        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        if (remaining > 0)
            ImGui.TextColored(ImGuiColors.HealerGreen, $"{remaining:N0}");
        else
            ImGui.TextColored(ImGuiColors.DalamudGrey3, "0");

        ImGui.TableNextColumn();
        selectedVendor = DrawEntryVendorCell(entry, manager, isActive, vendorOptions, selectedVendor);

        ImGui.TableNextColumn();
        if (selectedVendor?.Location == null)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey3, "Unknown");
        }
        else
        {
            if (Dalamud.ClientState.TerritoryType == selectedVendor.Location.TerritoryId)
                ImGui.TextColored(ImGuiColors.HealerGreen, selectedVendor.ZoneName);
            else
                ImGui.TextUnformatted(selectedVendor.ZoneName);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(selectedVendor.ZoneName);
        }

        ImGui.TableNextColumn();
        using (ImRaii.Disabled(isActive || manager.IsBusy))
        {
            if (ImGui.SmallButton($"Remove##vendorBuyListRemove_{entry.Id}"))
                manager.RemoveEntry(entry.Id);
        }
    }

    private List<VendorListNpcOption> BuildVendorOptions(VendorShopEntry? liveEntry)
    {
        var options = new List<VendorListNpcOption>();
        if (liveEntry == null)
            return options;

        var locationCacheReady = VendorNpcLocationCache.IsInitialized;
        var selectableNpcs = VendorDevExclusions.GetSelectableNpcs(liveEntry.Npcs, "building the vendor buy list", liveEntry.ItemName);
        foreach (var npc in selectableNpcs)
        {
            var location = VendorNpcLocationCache.TryGetFirstLocation(npc.NpcId);
            if (locationCacheReady && location == null)
                continue;

            options.Add(new VendorListNpcOption(
                npc,
                location,
                location != null ? GetZoneName(location) : string.Empty));
        }

        return options;
    }

    private static VendorListNpcOption? GetSelectedVendorOption(IReadOnlyList<VendorListNpcOption> vendorOptions, VendorNpc? resolvedVendor)
    {
        if (vendorOptions.Count == 0)
            return null;

        if (resolvedVendor != null)
        {
            var selectedVendor = vendorOptions.FirstOrDefault(option => VendorPreferenceHelper.MatchesVendor(option.Npc, resolvedVendor))
                ?? vendorOptions.FirstOrDefault(option => option.Npc.NpcId == resolvedVendor.NpcId && option.Npc.MenuShopType == resolvedVendor.MenuShopType && option.Npc.ShopId == resolvedVendor.ShopId)
                ?? vendorOptions.FirstOrDefault(option => option.Npc.NpcId == resolvedVendor.NpcId && option.Npc.MenuShopType == resolvedVendor.MenuShopType)
                ?? vendorOptions.FirstOrDefault(option => option.Npc.NpcId == resolvedVendor.NpcId);
            if (selectedVendor != null)
                return selectedVendor;
        }

        return vendorOptions[0];
    }

    private VendorListNpcOption? DrawEntryVendorCell(VendorBuyListEntry entry, VendorBuyListManager manager, bool isActive,
        IReadOnlyList<VendorListNpcOption> vendorOptions, VendorListNpcOption? selectedVendor)
    {
        if (vendorOptions.Count == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey3, entry.VendorNpcName);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(entry.VendorNpcName);
            return null;
        }

        selectedVendor ??= vendorOptions[0];
        if (vendorOptions.Count == 1)
        {
            var label = GetVendorNpcDisplayLabel(vendorOptions, selectedVendor);
            ImGui.TextColored(ImGuiColors.HealerGreen, label);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(label);
            return selectedVendor;
        }

        var selectedLabel = GetVendorNpcDisplayLabel(vendorOptions, selectedVendor);
        using (ImRaii.Disabled(isActive || manager.IsBusy))
        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.HealerGreen))
        {
            ImGui.SetNextItemWidth(-1);
            if (ImGui.BeginCombo($"##vendorBuyListVendor_{entry.Id}", selectedLabel))
            {
                foreach (var option in vendorOptions)
                {
                    var isSelected = VendorPreferenceHelper.MatchesVendor(option.Npc, selectedVendor.Npc);
                    if (ImGui.Selectable(GetVendorNpcSelectableLabel(entry, vendorOptions, option), isSelected)
                     && manager.UpdateEntryVendor(entry.Id, option.Npc))
                        selectedVendor = option;
                    if (isSelected)
                        ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(selectedLabel);

        return selectedVendor;
    }

    private static string GetVendorNpcDisplayLabel(IReadOnlyList<VendorListNpcOption> vendorOptions, VendorListNpcOption option)
    {
        var duplicateRoute = vendorOptions.Count(other =>
            other.Npc.Name.Equals(option.Npc.Name, StringComparison.OrdinalIgnoreCase)
         && string.Equals(other.ZoneName, option.ZoneName, StringComparison.OrdinalIgnoreCase)) > 1;
        if (duplicateRoute)
        {
            var routeLabel = GetVendorRouteLabel(option.Npc);
            if (!string.IsNullOrWhiteSpace(option.ZoneName) && !string.IsNullOrWhiteSpace(routeLabel))
                return $"{option.Npc.Name} ({option.ZoneName} · {routeLabel})";
            if (!string.IsNullOrWhiteSpace(option.ZoneName))
                return $"{option.Npc.Name} ({option.ZoneName})";
            if (!string.IsNullOrWhiteSpace(routeLabel))
                return $"{option.Npc.Name} [{routeLabel}]";
            return $"{option.Npc.Name} [{option.Npc.NpcId}]";
        }
        var duplicateName = vendorOptions.Count(other => other.Npc.Name.Equals(option.Npc.Name, StringComparison.OrdinalIgnoreCase)) > 1;
        if (!duplicateName)
            return option.Npc.Name;

        if (!string.IsNullOrWhiteSpace(option.ZoneName))
            return $"{option.Npc.Name} ({option.ZoneName})";

        return $"{option.Npc.Name} [{option.Npc.NpcId}]";
    }

    private static string GetVendorNpcSelectableLabel(VendorBuyListEntry entry, IReadOnlyList<VendorListNpcOption> vendorOptions, VendorListNpcOption option)
        => $"{GetVendorNpcDisplayLabel(vendorOptions, option)}##vendorBuyListVendor_{entry.Id}_{VendorPreferenceHelper.GetRouteKey(option.Npc)}";

    private static string GetVendorRouteLabel(VendorNpc npc)
    {
        var routeParts = new List<string>();
        if (npc.GcRankIndex >= 0)
            routeParts.Add($"Rank {npc.GcRankIndex + 1}");
        if (npc.GcCategoryIndex >= 0)
            routeParts.Add($"Category {npc.GcCategoryIndex + 1}");
        if (npc.InclusionPageIndex >= 0)
            routeParts.Add($"Page {npc.InclusionPageIndex + 1}");
        if (npc.InclusionSubPageIndex > 0)
            routeParts.Add($"Tab {npc.InclusionSubPageIndex}");
        if (routeParts.Count == 0 && npc.SourceShopId != 0)
            routeParts.Add($"Route {npc.SourceShopId}");
        return string.Join(" / ", routeParts);
    }

    private string GetZoneName(VendorNpcLocation location)
    {
        if (_zoneNames.TryGetValue(location.TerritoryId, out var zoneName))
            return zoneName;

        var territorySheet = Dalamud.GameData.GetExcelSheet<TerritoryType>();
        if (territorySheet == null || !territorySheet.TryGetRow(location.TerritoryId, out var territory))
            return _zoneNames[location.TerritoryId] = $"Territory {location.TerritoryId}";

        zoneName = territory.PlaceName.RowId != 0
            ? territory.PlaceName.Value.Name.ToString()
            : $"Territory {location.TerritoryId}";
        _zoneNames[location.TerritoryId] = zoneName;
        return zoneName;
    }
}
