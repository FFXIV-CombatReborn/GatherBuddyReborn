using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Textures;
using ElliLib;
using ElliLib.Raii;
using GatherBuddy.Vulcan.Vendors;
using Lumina.Excel.Sheets;
using ImRaii = ElliLib.Raii.ImRaii;

namespace GatherBuddy.Gui;

public partial class VulcanWindow
{
    private sealed record VendorDisplayNpcOption(VendorNpc Npc, VendorNpcLocation? Location, string ZoneName);
    private sealed record VendorDisplayRow(
        VendorShopEntry Entry,
        ISharedImmediateTexture Icon,
        IReadOnlyList<VendorDisplayNpcOption> NpcOptions,
        string FallbackVendorName,
        string IdSuffix,
        string CostText
    );
    private static readonly (VendorCurrencyGroup Group, string Label)[] SpecialCurrencyGroups =
    [
        (VendorCurrencyGroup.Tomestones,       "Tomestones"),
        (VendorCurrencyGroup.BicolorGemstones, "Bicolor Gemstones"),
        (VendorCurrencyGroup.HuntSeals,        "The Hunt"),
        (VendorCurrencyGroup.Scrips,           "Scrips"),
        (VendorCurrencyGroup.MGP,              "MGP"),
        (VendorCurrencyGroup.PvP,              "PvP"),
        (VendorCurrencyGroup.Other,            "Other"),
    ];

    private static readonly (VendorGilFilter Filter, string Label)[] GilFilters =
    [
        (VendorGilFilter.All,        "All"),
        (VendorGilFilter.Gatherable, "Gatherable"),
        (VendorGilFilter.Fish,       "Fish"),
        (VendorGilFilter.Craftable,  "Craftable"),
        (VendorGilFilter.Housing,    "Housing/Furnishing"),
        (VendorGilFilter.Dyes,       "Dyes"),
        (VendorGilFilter.Other,      "Other"),
    ];

    private VendorShopType                               _vendorCategory       = VendorShopType.GilShop;
    private VendorCurrencyGroup?                         _vendorSelectedGroup  = null;
    private VendorGilFilter                              _vendorGilFilter      = VendorGilFilter.All;
    private string                                       _vendorSearch         = string.Empty;
    private bool                                         _vendorFilterDirty    = true;
    private List<VendorDisplayRow>                       _vendorDisplay        = new();
    private bool                                         _vendorDisplayBuiltWithResolvedLocations;
    private Dictionary<VendorCurrencyGroup, int>?        _vendorGroupCounts;
    private Dictionary<VendorGilFilter, int>?            _vendorGilCounts;
    private readonly Dictionary<uint, string>            _vendorZoneNames = new();
    private readonly Dictionary<(VendorShopType ShopType, uint ItemId, uint CurrencyItemId, uint Cost), int> _vendorPurchaseQuantities = new();
    private (VendorShopType ShopType, uint ItemId, uint CurrencyItemId, uint Cost)? _vendorEditingQuantityKey;
    private string                                       _vendorEditingQuantityText = string.Empty;
    private bool                                         _vendorEditingQuantityFocus;
    private static readonly Vector4                      VendorMarkerButtonColor     = new(0.45f, 0.80f, 1.00f, 1f);
    private static readonly Vector4                      VendorBuyListButtonColor    = new(0.95f, 0.80f, 0.35f, 1f);
    private static readonly Vector4                      VendorAutomationButtonColor = new(0.60f, 0.95f, 0.60f, 1f);
    private static (VendorShopType ShopType, uint ItemId, uint CurrencyItemId, uint Cost) VendorQuantityKey(VendorShopEntry entry)
        => (entry.ShopType, entry.ItemId, entry.CurrencyItemId, entry.Cost);

    private int GetVendorPurchaseQuantity(VendorShopEntry entry)
    {
        var key = VendorQuantityKey(entry);
        if (_vendorPurchaseQuantities.TryGetValue(key, out var quantity) && quantity > 0)
            return quantity;

        _vendorPurchaseQuantities[key] = 1;
        return 1;
    }

    private void SetVendorPurchaseQuantity(VendorShopEntry entry, int quantity)
        => _vendorPurchaseQuantities[VendorQuantityKey(entry)] = Math.Max(1, quantity);

    private static float GetVendorQuantityInputWidth()
        => Math.Max(80f, ImGui.CalcTextSize("99999").X + ImGui.GetStyle().FramePadding.X * 2f + 12f);

    private bool IsEditingVendorQuantity(VendorShopEntry entry)
        => _vendorEditingQuantityKey is { } key && key == VendorQuantityKey(entry);

    private void StartEditingVendorQuantity(VendorShopEntry entry)
    {
        _vendorEditingQuantityKey   = VendorQuantityKey(entry);
        _vendorEditingQuantityText  = GetVendorPurchaseQuantity(entry).ToString();
        _vendorEditingQuantityFocus = true;
    }

    private void StopEditingVendorQuantity()
    {
        _vendorEditingQuantityKey   = null;
        _vendorEditingQuantityText  = string.Empty;
        _vendorEditingQuantityFocus = false;
    }

    private void CommitVendorQuantityEdit(VendorShopEntry entry)
    {
        if (!int.TryParse(_vendorEditingQuantityText, out var quantity))
            quantity = GetVendorPurchaseQuantity(entry);

        SetVendorPurchaseQuantity(entry, quantity);
        StopEditingVendorQuantity();
    }

    private static void SetVendorNpcPref(VendorShopEntry entry, VendorNpc vendor)
        => VendorPreferenceHelper.SetPreferredNpc(entry, vendor);

    private static string GetVendorDisplayRowId(VendorShopEntry entry)
        => $"{(int)entry.ShopType}_{(int)entry.Group}_{entry.ItemId}_{entry.CurrencyItemId}_{entry.Cost}";

    private static int GetCurrentGrandCompanyEntryCount()
        => VendorShopResolver.GcShopEntries.Count(VendorShopResolver.MatchesCurrentGrandCompany);

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

    private void DrawVendorsTab()
    {
        using var tab = ImRaii.TabItem("Vendors##vendorsTab");
        if (!tab.Success) return;

        if (!VendorShopResolver.IsInitialized && !VendorShopResolver.IsInitializing)
            VendorShopResolver.InitializeAsync();

        if (VendorShopResolver.IsInitializing)
        {
            ImGui.Spacing();
            ImGui.TextColored(ImGuiColors.DalamudGrey, "Loading vendor data...");
            return;
        }

        DrawVendorsTabContent();
    }

    private void DrawVendorsTabContent()
    {
        var avail = ImGui.GetContentRegionAvail();
        const float leftW = 220f;

        using (ImRaii.PushColor(ImGuiCol.ChildBg, new Vector4(0.08f, 0.08f, 0.10f, 1f)))
        {
            ImGui.BeginChild("##vendorLeft", new Vector2(leftW, avail.Y), true);
            DrawVendorSidebar();
            ImGui.EndChild();
        }

        ImGui.SameLine();

        using (ImRaii.PushColor(ImGuiCol.ChildBg, new Vector4(0.08f, 0.08f, 0.10f, 1f)))
        {
            ImGui.BeginChild("##vendorRight", new Vector2(0, avail.Y), true);
            DrawVendorItemTable();
            ImGui.EndChild();
        }
    }

    private VendorDisplayRow BuildVendorDisplayRow(VendorShopEntry entry, bool locationCacheReady)
    {
        var selectableNpcs = VendorDevExclusions.GetSelectableNpcs(entry.Npcs, "building the Vendors tab", entry.ItemName);
        var npcOptions = new List<VendorDisplayNpcOption>(selectableNpcs.Count);
        foreach (var npc in selectableNpcs)
        {
            var location = VendorNpcLocationCache.TryGetFirstLocation(npc.NpcId);
            if (locationCacheReady && location == null)
                continue;

            npcOptions.Add(new VendorDisplayNpcOption(
                npc,
                location,
                location != null ? GetVendorZoneName(location) : string.Empty));
        }

        return new VendorDisplayRow(
            entry,
            Icons.DefaultStorage.TextureProvider.GetFromGameIcon(new GameIconLookup(entry.IconId)),
            npcOptions,
            selectableNpcs.Count > 0
                ? selectableNpcs[0].Name
                : entry.Npcs.Count > 0
                    ? entry.Npcs[0].Name
                    : "Unknown",
            GetVendorDisplayRowId(entry),
            $"{entry.Cost:N0}");
    }

    private VendorDisplayNpcOption? GetSelectedVendorOption(VendorDisplayRow row)
    {
        if (row.NpcOptions.Count == 0)
            return null;

        var preferredNpc = VendorPreferenceHelper.ResolvePreferredNpc(row.Entry, row.NpcOptions.Select(option => option.Npc).ToList());
        if (preferredNpc != null)
        {
            var selectedOption = row.NpcOptions.FirstOrDefault(option => VendorPreferenceHelper.MatchesVendor(option.Npc, preferredNpc));
            if (selectedOption != null)
                return selectedOption;
        }

        return row.NpcOptions[0];
    }

    private static string GetVendorNpcDisplayLabel(VendorDisplayRow row, VendorDisplayNpcOption option)
    {
        var duplicateRoute = row.NpcOptions.Count(other =>
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
        var duplicateName = row.NpcOptions.Count(other => other.Npc.Name.Equals(option.Npc.Name, StringComparison.OrdinalIgnoreCase)) > 1;
        if (!duplicateName)
            return option.Npc.Name;

        if (!string.IsNullOrWhiteSpace(option.ZoneName))
            return $"{option.Npc.Name} ({option.ZoneName})";

        return $"{option.Npc.Name} [{option.Npc.NpcId}]";
    }

    private static string GetVendorNpcSelectableLabel(VendorDisplayRow row, VendorDisplayNpcOption option)
        => $"{GetVendorNpcDisplayLabel(row, option)}##vendorNpc_{row.IdSuffix}_{VendorPreferenceHelper.GetRouteKey(option.Npc)}";

    private void DrawVendorZoneCell(VendorDisplayNpcOption? selectedNpc)
    {
        var location = selectedNpc?.Location;
        if (location == null)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey3, "Unknown");
            return;
        }

        if (Dalamud.ClientState.TerritoryType == location.TerritoryId)
            ImGui.TextColored(ImGuiColors.HealerGreen, selectedNpc!.ZoneName);
        else
            ImGui.TextUnformatted(selectedNpc!.ZoneName);
    }

    private void DrawVendorMapMarkerButton(VendorDisplayRow row, VendorDisplayNpcOption? selectedNpc)
    {
        var location = selectedNpc?.Location;
        if (location == null)
        {
            DrawVendorIconButton($"vendor_flag_disabled_{row.IdSuffix}", FontAwesomeIcon.MapMarkerAlt,
                VendorMarkerButtonColor, "No location data available", true);
            return;
        }

        if (DrawVendorIconButton($"vendor_flag_{row.IdSuffix}", FontAwesomeIcon.MapMarkerAlt,
                VendorMarkerButtonColor, $"Place a map marker for {selectedNpc!.Npc.Name}"))
            GatherBuddy.VendorNavigator.PlaceMapMarker(location);
    }

    private string GetVendorZoneName(VendorNpcLocation? location)
    {
        if (location == null)
            return "Unknown";

        if (_vendorZoneNames.TryGetValue(location.TerritoryId, out var zoneName))
            return zoneName;

        var territorySheet = Dalamud.GameData.GetExcelSheet<TerritoryType>();
        if (territorySheet == null || !territorySheet.TryGetRow(location.TerritoryId, out var territory))
            return _vendorZoneNames[location.TerritoryId] = $"Territory {location.TerritoryId}";

        zoneName = territory.PlaceName.RowId != 0
            ? territory.PlaceName.Value.Name.ToString()
            : $"Territory {location.TerritoryId}";
        _vendorZoneNames[location.TerritoryId] = zoneName;
        return zoneName;
    }

    private static bool DrawVendorIconButton(string id, FontAwesomeIcon icon, Vector4 color, string tooltip, bool disabled = false)
    {
        var hoveredFlags = disabled ? ImGuiHoveredFlags.AllowWhenDisabled : ImGuiHoveredFlags.None;
        var size         = new Vector2(ImGui.GetFrameHeight(), 0f);

        if (disabled)
        {
            bool disabledHovered;
            using (ImRaii.Disabled())
            using (ImRaii.PushFont(UiBuilder.IconFont))
            using (ImRaii.PushColor(ImGuiCol.Text, color))
            {
                ImGui.Button($"{icon.ToIconString()}##{id}", size);
                disabledHovered = ImGui.IsItemHovered(hoveredFlags);
            }
            if (disabledHovered)
                ImGui.SetTooltip(tooltip);
            return false;
        }
        bool clicked;
        bool hovered;

        using (ImRaii.PushFont(UiBuilder.IconFont))
        using (ImRaii.PushColor(ImGuiCol.Text, color))
        {
            clicked = ImGui.Button($"{icon.ToIconString()}##{id}", size);
            hovered = ImGui.IsItemHovered();
        }

        if (hovered)
            ImGui.SetTooltip(tooltip);
        return clicked;
    }

    private Dictionary<VendorCurrencyGroup, int> GetGroupCounts()
    {
        if (_vendorGroupCounts != null) return _vendorGroupCounts;
        _vendorGroupCounts = VendorShopResolver.SpecialShopEntries
            .GroupBy(e => e.Group)
            .ToDictionary(g => g.Key, g => g.Count());
        return _vendorGroupCounts;
    }

    private Dictionary<VendorGilFilter, int> GetGilCounts()
    {
        if (_vendorGilCounts != null) return _vendorGilCounts;
        var entries = VendorShopResolver.GilShopEntries;
        _vendorGilCounts = new Dictionary<VendorGilFilter, int>
        {
            [VendorGilFilter.All]        = entries.Count,
            [VendorGilFilter.Gatherable] = entries.Count(e => VendorShopResolver.GatherableIds.Contains(e.ItemId)),
            [VendorGilFilter.Fish]       = entries.Count(e => VendorShopResolver.FishIds.Contains(e.ItemId)),
            [VendorGilFilter.Craftable]  = entries.Count(e => VendorShopResolver.CraftableIds.Contains(e.ItemId)),
            [VendorGilFilter.Housing]    = entries.Count(e => VendorShopResolver.HousingItemIds.Contains(e.ItemId)),
            [VendorGilFilter.Dyes]       = entries.Count(e => VendorShopResolver.DyeItemIds.Contains(e.ItemId)),
            [VendorGilFilter.Other]      = entries.Count(IsGilOtherEntry),
        };
        return _vendorGilCounts;
    }

    private static bool IsGilOtherEntry(VendorShopEntry entry)
        => !VendorShopResolver.GatherableIds.Contains(entry.ItemId)
        && !VendorShopResolver.FishIds.Contains(entry.ItemId)
        && !VendorShopResolver.CraftableIds.Contains(entry.ItemId)
        && !VendorShopResolver.HousingItemIds.Contains(entry.ItemId)
        && !VendorShopResolver.DyeItemIds.Contains(entry.ItemId);

    private void DrawVendorSidebar()
    {
        ImGui.Spacing();
        ImGui.TextColored(ImGuiColors.DalamudGrey3, "Shop Type");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawVendorTypeSelectable("Gil Shops", VendorShopType.GilShop,          VendorShopResolver.GilShopEntries.Count);
        DrawVendorTypeSelectable("GC Seals",  VendorShopType.GrandCompanySeals, GetCurrentGrandCompanyEntryCount());

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.BeginChild("##vendorSidebarScroll", new Vector2(-1, ImGui.GetContentRegionAvail().Y), false);

        if (_vendorCategory == VendorShopType.GilShop)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey3, "Source");
            ImGui.Spacing();
            var gilCounts = GetGilCounts();
            foreach (var (filter, label) in GilFilters)
            {
                gilCounts.TryGetValue(filter, out var count);
                var isSelected = _vendorGilFilter == filter;
                if (ImGui.Selectable($"{label}##vendorGil_{filter}", isSelected) && !isSelected)
                {
                    _vendorGilFilter   = filter;
                    _vendorFilterDirty = true;
                }
                ImGui.SameLine();
                ImGui.SetCursorPosX(ImGui.GetContentRegionMax().X - ImGui.CalcTextSize(count.ToString()).X - ImGui.GetStyle().ItemSpacing.X);
                ImGui.TextColored(ImGuiColors.DalamudGrey3, count.ToString());
            }
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
        }

        ImGui.TextColored(ImGuiColors.DalamudGrey3, "Currency");
        ImGui.Spacing();
        var counts = GetGroupCounts();
        foreach (var (group, label) in SpecialCurrencyGroups)
        {
            counts.TryGetValue(group, out var count);
            if (count == 0) continue;
            var isSelected = _vendorCategory == VendorShopType.SpecialCurrency && _vendorSelectedGroup == group;
            if (ImGui.Selectable($"{label}##vendorGroup_{group}", isSelected) && !isSelected)
            {
                _vendorCategory      = VendorShopType.SpecialCurrency;
                _vendorSelectedGroup = group;
                _vendorFilterDirty   = true;
            }
            ImGui.SameLine();
            ImGui.SetCursorPosX(ImGui.GetContentRegionMax().X - ImGui.CalcTextSize(count.ToString()).X - ImGui.GetStyle().ItemSpacing.X);
            ImGui.TextColored(ImGuiColors.DalamudGrey3, count.ToString());
        }
        ImGui.EndChild();
    }

    private void DrawVendorTypeSelectable(string label, VendorShopType type, int count)
    {
        var isSelected = _vendorCategory == type;
        if (ImGui.Selectable($"{label}##vendorType_{type}", isSelected) && !isSelected)
        {
            _vendorCategory      = type;
            _vendorSelectedGroup = null;
            _vendorGilFilter     = VendorGilFilter.All;
            _vendorFilterDirty   = true;
        }
        ImGui.SameLine();
        ImGui.SetCursorPosX(ImGui.GetContentRegionMax().X - ImGui.CalcTextSize(count.ToString()).X - ImGui.GetStyle().ItemSpacing.X);
        ImGui.TextColored(ImGuiColors.DalamudGrey3, count.ToString());
    }

    private void DrawVendorItemTable()
    {
        var locationCacheReady = VendorNpcLocationCache.IsInitialized;
        if (_vendorDisplayBuiltWithResolvedLocations != locationCacheReady)
            GatherBuddy.Log.Debug($"[VulcanWindow] Rebuilding vendor display after location cache state change ({_vendorDisplayBuiltWithResolvedLocations} -> {locationCacheReady})");

        if (_vendorFilterDirty || _vendorDisplayBuiltWithResolvedLocations != locationCacheReady)
            RebuildVendorDisplay();

        ImGui.Spacing();
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##vendorSearch", "Search items or vendors...", ref _vendorSearch, 256))
            _vendorFilterDirty = true;

        ImGui.Spacing();
        var buyListManager  = GatherBuddy.VendorBuyListManager;

        var purchaseManager = GatherBuddy.VendorPurchaseManager;
        if (ImGui.Button("Open Vendor Buy List"))
            buyListManager.OpenWindow();
        ImGui.SameLine();
        ImGui.TextColored(ImGuiColors.DalamudGrey3, $"{buyListManager.ActiveListName}: {buyListManager.Entries.Count} item(s)");
        ImGui.Spacing();

        if (!string.IsNullOrWhiteSpace(buyListManager.StatusText))
        {
            ImGui.TextColored(buyListManager.IsRunning ? ImGuiColors.ParsedGold : ImGuiColors.DalamudGrey3,
                $"Buy List: {buyListManager.StatusText}");
            ImGui.Spacing();
        }
        if (purchaseManager.IsRunning)
        {
            ImGui.TextColored(ImGuiColors.ParsedGold, purchaseManager.StatusText);
            ImGui.Spacing();
        }

        if (_vendorCategory == VendorShopType.GrandCompanySeals && GetCurrentGrandCompanyEntryCount() == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "Loading GC Seal data...");
            return;
        }

        var overflow = _vendorDisplay.Count > 500;
        ImGui.TextColored(ImGuiColors.DalamudGrey3, overflow
            ? $"Showing 500 of {_vendorDisplay.Count} \u2014 refine your search"
            : $"{_vendorDisplay.Count} result(s)");
        ImGui.Spacing();
        var showAutomationControls = _vendorCategory is VendorShopType.GilShop or VendorShopType.SpecialCurrency or VendorShopType.GrandCompanySeals;
        var quantityColumnWidth = GetVendorQuantityInputWidth() + ImGui.GetStyle().CellPadding.X * 2f;

        const ImGuiTableFlags tableFlags = ImGuiTableFlags.BordersOuter | ImGuiTableFlags.BordersInnerV
                                         | ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg
                                         | ImGuiTableFlags.SizingFixedFit;
        if (!ImGui.BeginTable("##vendorTable", showAutomationControls ? 8 : 6, tableFlags, new Vector2(-1, -1)))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Item",     ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Cost",     ImGuiTableColumnFlags.WidthFixed, 80f);
        if (showAutomationControls)
            ImGui.TableSetupColumn("Qty",      ImGuiTableColumnFlags.WidthFixed, quantityColumnWidth);
        ImGui.TableSetupColumn("Vendor",   ImGuiTableColumnFlags.WidthFixed, 170f);
        ImGui.TableSetupColumn("Location", ImGuiTableColumnFlags.WidthFixed, 180f);
        ImGui.TableSetupColumn("##flag",   ImGuiTableColumnFlags.WidthFixed, 32f);
        if (showAutomationControls)
            ImGui.TableSetupColumn("##list",   ImGuiTableColumnFlags.WidthFixed, 32f);
        ImGui.TableSetupColumn("##go",     ImGuiTableColumnFlags.WidthFixed, 32f);
        ImGui.TableHeadersRow();

        const float iconSize = 20f;
        var iconVec = new Vector2(iconSize, iconSize);
        var limit   = overflow ? 500 : _vendorDisplay.Count;
        var clipper = ImGui.ImGuiListClipper();
        clipper.Begin(limit);
        while (clipper.Step())
        {
            for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                DrawVendorTableRow(_vendorDisplay[i], showAutomationControls, iconVec, iconSize);
        }
        clipper.End();
        clipper.Destroy();

        ImGui.EndTable();
    }

    private void DrawVendorTableRow(VendorDisplayRow row, bool showGilControls, Vector2 iconVec, float iconSize)
    {
        var entry       = row.Entry;
        var selectedNpc = GetSelectedVendorOption(row);

        ImGui.TableNextRow();
        ImGui.TableNextColumn();

        var tex = row.Icon;
        if (tex.TryGetWrap(out var wrap, out _))
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
        ImGui.TextUnformatted(entry.ItemName);

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(row.CostText);
        if (showGilControls)
        {
            ImGui.TableNextColumn();
            DrawVendorQuantityCell(row);
        }

        ImGui.TableNextColumn();
        DrawVendorNpcCell(row, selectedNpc);

        ImGui.TableNextColumn();
        DrawVendorZoneCell(selectedNpc);

        ImGui.TableNextColumn();
        DrawVendorMapMarkerButton(row, selectedNpc);
        if (showGilControls)
        {
            ImGui.TableNextColumn();
            DrawVendorAddToListButton(row, selectedNpc);
        }

        ImGui.TableNextColumn();
        DrawVendorGoButton(row, selectedNpc);
    }

    private void DrawVendorQuantityCell(VendorDisplayRow row)
    {
        if (IsEditingVendorQuantity(row.Entry))
        {
            if (_vendorEditingQuantityFocus)
            {
                ImGui.SetKeyboardFocusHere();
                _vendorEditingQuantityFocus = false;
            }

            ImGui.SetNextItemWidth(GetVendorQuantityInputWidth());
            ImGui.InputText($"##vendorQty_{row.IdSuffix}", ref _vendorEditingQuantityText, 16,
                ImGuiInputTextFlags.CharsDecimal | ImGuiInputTextFlags.AutoSelectAll | ImGuiInputTextFlags.EnterReturnsTrue);
            if (ImGui.IsItemDeactivated())
                CommitVendorQuantityEdit(row.Entry);
            return;
        }

        var quantity = GetVendorPurchaseQuantity(row.Entry);
        if (ImGui.Button($"{quantity:N0}##vendorQtyButton_{row.IdSuffix}", new Vector2(GetVendorQuantityInputWidth(), 0f)))
            StartEditingVendorQuantity(row.Entry);
    }

    private void DrawVendorNpcCell(VendorDisplayRow row, VendorDisplayNpcOption? selectedNpc)
    {
        if (row.NpcOptions.Count == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey3, row.FallbackVendorName);
            return;
        }

        if (row.NpcOptions.Count == 1)
        {
            ImGui.TextColored(ImGuiColors.HealerGreen, row.NpcOptions[0].Npc.Name);
            return;
        }
        var selectedLabel = selectedNpc != null
            ? GetVendorNpcDisplayLabel(row, selectedNpc)
            : GetVendorNpcDisplayLabel(row, row.NpcOptions[0]);
        ImGui.SetNextItemWidth(-1);
        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.HealerGreen))
        {
            if (ImGui.BeginCombo($"##vnpc_{row.IdSuffix}", selectedLabel))
            {
                foreach (var npc in row.NpcOptions)
                {
                    var isSelected = selectedNpc != null && VendorPreferenceHelper.MatchesVendor(npc.Npc, selectedNpc.Npc);
                    if (ImGui.Selectable(GetVendorNpcSelectableLabel(row, npc), isSelected))
                        SetVendorNpcPref(row.Entry, npc.Npc);
                    if (isSelected)
                        ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }
        }
    }

    private void DrawVendorAddToListButton(VendorDisplayRow row, VendorDisplayNpcOption? selectedNpc)
    {
        if (selectedNpc == null)
        {
            DrawVendorIconButton($"vendor_add_disabled_{row.IdSuffix}", FontAwesomeIcon.Plus,
                VendorBuyListButtonColor, "No vendor available for the buy list", true);
            return;
        }
        if (!VendorPurchaseManager.IsPurchaseSupported(row.Entry, selectedNpc.Npc))
        {
            DrawVendorIconButton($"vendor_add_disabled_{row.IdSuffix}", FontAwesomeIcon.Plus,
                VendorBuyListButtonColor, "Automation is not available for the selected vendor route", true);
            return;
        }

        var targetQuantity = (uint)Math.Max(1, GetVendorPurchaseQuantity(row.Entry));
        if (DrawVendorIconButton($"vendor_add_{row.IdSuffix}", FontAwesomeIcon.Plus,
                VendorBuyListButtonColor, $"Add {row.Entry.ItemName} to the vendor buy list with target {targetQuantity:N0}"))
            GatherBuddy.VendorBuyListManager.TryAddTarget(row.Entry, selectedNpc.Npc, targetQuantity);
    }

    private void DrawVendorGoButton(VendorDisplayRow row, VendorDisplayNpcOption? selectedNpc)
    {
        var location = selectedNpc?.Location;
        if (selectedNpc == null || location == null)
        {
            DrawVendorIconButton($"vendor_go_disabled_{row.IdSuffix}", FontAwesomeIcon.ShoppingCart,
                VendorAutomationButtonColor, "No location data available", true);
            return;
        }

        var entry            = row.Entry;
        var canPurchaseHere  = VendorPurchaseManager.IsPurchaseSupported(entry, selectedNpc.Npc);
        var requestedQuantity = canPurchaseHere ? GetVendorPurchaseQuantity(entry) : 1;
        var navigator        = GatherBuddy.VendorNavigator;
        var purchaseManager  = GatherBuddy.VendorPurchaseManager;
        var isPurchaseActive = purchaseManager.IsRunningFor(entry, selectedNpc.Npc);
        var isActive         = isPurchaseActive || navigator.IsActive && navigator.CurrentTarget?.NpcId == selectedNpc.Npc.NpcId;

        if (isActive)
        {
            if (DrawVendorIconButton($"vendor_go_active_{row.IdSuffix}", FontAwesomeIcon.ShoppingCart,
                    ImGuiColors.ParsedGold, isPurchaseActive
                        ? $"{purchaseManager.StatusText} — click to cancel"
                        : $"Navigating to {selectedNpc.Npc.Name} — click to cancel"))
            {
                if (isPurchaseActive)
                    purchaseManager.Stop();
                else
                    navigator.Stop();
            }
        }
        else
        {
            if (DrawVendorIconButton($"vendor_go_{row.IdSuffix}", FontAwesomeIcon.ShoppingCart,
                    VendorAutomationButtonColor, canPurchaseHere
                        ? $"Navigate to {selectedNpc.Npc.Name} and buy {requestedQuantity:N0}x {entry.ItemName}"
                        : $"Navigate to {selectedNpc.Npc.Name}"))
            {
                if (canPurchaseHere)
                    purchaseManager.StartPurchase(entry, selectedNpc.Npc, location, (uint)requestedQuantity);
                else
                    navigator.StartNavigation(location);
            }
        }
    }

    private void RebuildVendorDisplay()
    {
        _vendorFilterDirty = false;
        var locationCacheReady = VendorNpcLocationCache.IsInitialized;

        IEnumerable<VendorShopEntry> source = _vendorCategory switch
        {
            VendorShopType.GilShop => _vendorGilFilter switch
            {
                VendorGilFilter.Gatherable => VendorShopResolver.GilShopEntries.Where(e => VendorShopResolver.GatherableIds.Contains(e.ItemId)),
                VendorGilFilter.Fish       => VendorShopResolver.GilShopEntries.Where(e => VendorShopResolver.FishIds.Contains(e.ItemId)),
                VendorGilFilter.Craftable  => VendorShopResolver.GilShopEntries.Where(e => VendorShopResolver.CraftableIds.Contains(e.ItemId)),
                VendorGilFilter.Housing    => VendorShopResolver.GilShopEntries.Where(e => VendorShopResolver.HousingItemIds.Contains(e.ItemId)),
                VendorGilFilter.Dyes       => VendorShopResolver.GilShopEntries.Where(e => VendorShopResolver.DyeItemIds.Contains(e.ItemId)),
                VendorGilFilter.Other      => VendorShopResolver.GilShopEntries.Where(IsGilOtherEntry),
                _                         => VendorShopResolver.GilShopEntries,
            },
            VendorShopType.GrandCompanySeals => VendorShopResolver.GcShopEntries
                .Where(VendorShopResolver.MatchesCurrentGrandCompany),
            VendorShopType.SpecialCurrency   => VendorShopResolver.SpecialShopEntries
                .Where(e => _vendorSelectedGroup == null || e.Group == _vendorSelectedGroup),
            _                                => Enumerable.Empty<VendorShopEntry>(),
        };

        if (!string.IsNullOrWhiteSpace(_vendorSearch))
        {
            var search = _vendorSearch;
            source = source.Where(e =>
                e.ItemName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                e.Npcs.Any(n => n.Name.Contains(search, StringComparison.OrdinalIgnoreCase)));
        }
        _vendorDisplay = source
            .Select(entry => BuildVendorDisplayRow(entry, locationCacheReady))
            .ToList();

        if (_vendorEditingQuantityKey.HasValue
         && !_vendorDisplay.Any(row => VendorQuantityKey(row.Entry) == _vendorEditingQuantityKey.Value))
            StopEditingVendorQuantity();
        _vendorDisplayBuiltWithResolvedLocations = locationCacheReady;
    }
}
