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
        ushort CurrencyIconId,
        IReadOnlyList<VendorDisplayNpcOption> NpcOptions,
        string FallbackVendorName,
        string IdSuffix,
        string CostText
    );
    private enum VendorSortColumn { Name, Cost, Currency, Vendor, Location }
    private enum VendorSortDirection { Ascending, Descending }
    private static readonly (VendorCurrencyGroup Group, string Label, uint CurrencyItemId)[] SpecialCurrencyGroups =
    [
        (VendorCurrencyGroup.Tomestones,       "Tomestones",       28),
        (VendorCurrencyGroup.BicolorGemstones, "Bicolor Gemstones", VendorShopResolver.BicolorCurrencyItemId),
        (VendorCurrencyGroup.HuntSeals,        "The Hunt",         VendorShopResolver.AlliedSealCurrencyItemId),
        (VendorCurrencyGroup.Scrips,           "Scrips",           33913),
        (VendorCurrencyGroup.MGP,              "MGP",              VendorShopResolver.MgpCurrencyItemId),
        (VendorCurrencyGroup.PvP,              "PvP",              VendorShopResolver.WolfMarkCurrencyItemId),
        (VendorCurrencyGroup.Other,            "Other",            0),
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
    private const uint MinerClassJobId          = 16;
    private const uint FisherClassJobId         = 18;
    private const uint CraftingLogMainCommandId = 9;
    private const uint HousingMainCommandId     = 44;
    private const uint DyeGeneralActionId       = 15;

    private VendorShopType                               _vendorCategory       = VendorShopType.GilShop;
    private VendorCurrencyGroup?                         _vendorSelectedGroup  = null;
    private uint?                                        _vendorSelectedCurrencyItemId;
    private VendorGilFilter                              _vendorGilFilter      = VendorGilFilter.All;
    private VendorSortColumn                             _vendorSortColumn     = VendorSortColumn.Name;
    private VendorSortDirection                          _vendorSortDirection  = VendorSortDirection.Ascending;
    private string                                       _vendorSearch         = string.Empty;
    private bool                                         _vendorFilterDirty    = true;
    private List<VendorDisplayRow>                       _vendorDisplay        = new();
    private bool                                         _vendorDisplayBuiltWithResolvedLocations;
    private Dictionary<VendorCurrencyGroup, int>?        _vendorGroupCounts;
    private Dictionary<VendorGilFilter, int>?            _vendorGilCounts;
    private readonly Dictionary<VendorGilFilter, ushort> _vendorGilFilterIconIds = new();
    private readonly Dictionary<uint, ushort>            _vendorCurrencyIconIds = new();
    private readonly Dictionary<uint, string>            _vendorCurrencyNames   = new();
    private readonly Dictionary<uint, string>            _vendorZoneNames = new();
    private readonly Dictionary<(VendorShopType ShopType, uint ItemId, uint CurrencyItemId, uint Cost), int> _vendorPurchaseQuantities = new();
    private (VendorShopType ShopType, uint ItemId, uint CurrencyItemId, uint Cost)? _vendorEditingQuantityKey;
    private string                                       _vendorEditingQuantityText = string.Empty;
    private bool                                         _vendorEditingQuantityFocus;
    private static readonly Vector4                      VendorMarkerButtonColor     = new(0.45f, 0.80f, 1.00f, 1f);
    private static readonly Vector4                      VendorBuyListButtonColor    = new(0.95f, 0.80f, 0.35f, 1f);
    private static readonly Vector4                      VendorAutomationButtonColor = new(0.60f, 0.95f, 0.60f, 1f);
    private static readonly Vector4                      VendorSelectedFilterColor   = new(0.25f, 0.50f, 0.85f, 1.00f);
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

    private static uint GetCurrentGrandCompanyCurrencyItemId()
        => VendorShopResolver.GetCurrentGrandCompanySealCurrencyItemId() is var currencyItemId && currencyItemId != 0
            ? currencyItemId
            : VendorShopResolver.GetGrandCompanySealCurrencyItemId(1);

    private ushort GetVendorCurrencyIconId(uint currencyItemId)
    {
        if (_vendorCurrencyIconIds.TryGetValue(currencyItemId, out var iconId))
            return iconId;

        var itemSheet = Dalamud.GameData.GetExcelSheet<Item>();
        iconId = itemSheet != null && itemSheet.TryGetRow(currencyItemId, out var item)
            ? (ushort)item.Icon
            : (ushort)0;
        _vendorCurrencyIconIds[currencyItemId] = iconId;
        return iconId;
    }

    private string GetVendorCurrencyName(uint currencyItemId, string fallbackName = "")
    {
        if (_vendorCurrencyNames.TryGetValue(currencyItemId, out var currencyName))
            return currencyName;

        var itemSheet = Dalamud.GameData.GetExcelSheet<Item>();
        currencyName = itemSheet != null && itemSheet.TryGetRow(currencyItemId, out var item)
            ? item.Name.ExtractText()
            : string.IsNullOrWhiteSpace(fallbackName)
                ? $"Currency {currencyItemId}"
                : fallbackName;
        _vendorCurrencyNames[currencyItemId] = currencyName;
        return currencyName;
    }

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
            GetVendorCurrencyIconId(entry.CurrencyItemId),
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
            return !string.IsNullOrWhiteSpace(option.ZoneName)
                ? $"{option.Npc.Name} ({option.ZoneName})"
                : option.Npc.Name;

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
        var size         = Vector2.One * ImGui.GetFrameHeight();

        bool DrawCenteredButton()
        {
            using var font = ImRaii.PushFont(UiBuilder.IconFont);
            var iconText = icon.ToIconString();
            var cursor   = ImGui.GetCursorScreenPos();
            var iconSize = ImGui.CalcTextSize(iconText);

            bool clicked;
            using (ImRaii.PushId(id))
                clicked = ImGui.Button(string.Empty, size);

            var iconPos = cursor + ((size - iconSize) / 2f);
            ImGui.GetWindowDrawList().AddText(iconPos, ImGui.GetColorU32(color), iconText);
            return clicked;
        }

        if (disabled)
        {
            bool disabledHovered;
            using (ImRaii.Disabled())
            {
                DrawCenteredButton();
                disabledHovered = ImGui.IsItemHovered(hoveredFlags);
            }
            if (disabledHovered)
                ImGui.SetTooltip(tooltip);
            return false;
        }
        bool clicked;
        bool hovered;
        clicked = DrawCenteredButton();
        hovered = ImGui.IsItemHovered();

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
    private int GetVendorTopLevelFilterCount(VendorShopType shopType, VendorCurrencyGroup? group = null)
        => shopType switch
        {
            VendorShopType.GilShop           => VendorShopResolver.GilShopEntries.Count,
            VendorShopType.GrandCompanySeals => GetCurrentGrandCompanyEntryCount(),
            VendorShopType.SpecialCurrency when group.HasValue => GetGroupCounts().GetValueOrDefault(group.Value, 0),
            VendorShopType.SpecialCurrency   => VendorShopResolver.SpecialShopEntries.Count,
            _                                => 0,
        };

    private bool IsVendorTopLevelFilterSelected(VendorShopType shopType, VendorCurrencyGroup? group = null)
        => _vendorCategory == shopType
        && (shopType != VendorShopType.SpecialCurrency || _vendorSelectedGroup == group);

    private void SelectVendorTopLevelFilter(VendorShopType shopType, VendorCurrencyGroup? group = null)
    {
        if (IsVendorTopLevelFilterSelected(shopType, group))
            return;

        _vendorCategory               = shopType;
        _vendorSelectedGroup          = shopType == VendorShopType.SpecialCurrency ? group : null;
        _vendorSelectedCurrencyItemId = null;
        _vendorFilterDirty            = true;
    }

    private static ushort GetVendorClassJobIconId(uint classJobId)
        => classJobId == 0 ? (ushort)0 : (ushort)(62100 + classJobId);

    private ushort GetVendorMainCommandIconId(uint mainCommandId)
    {
        var sheet = Dalamud.GameData.GetExcelSheet<MainCommand>();
        return sheet != null && sheet.TryGetRow(mainCommandId, out var mainCommand)
            ? (ushort)mainCommand.Icon
            : (ushort)0;
    }

    private ushort GetVendorGeneralActionIconId(uint generalActionId)
    {
        var sheet = Dalamud.GameData.GetExcelSheet<GeneralAction>();
        return sheet != null && sheet.TryGetRow(generalActionId, out var generalAction)
            ? (ushort)generalAction.Icon
            : (ushort)0;
    }

    private ushort GetVendorGilFilterIconId(VendorGilFilter filter)
    {
        if (_vendorGilFilterIconIds.TryGetValue(filter, out var iconId))
            return iconId;

        iconId = filter switch
        {
            VendorGilFilter.All        => GetVendorCurrencyIconId(VendorShopResolver.GilCurrencyItemId),
            VendorGilFilter.Gatherable => GetVendorClassJobIconId(MinerClassJobId),
            VendorGilFilter.Fish       => GetVendorClassJobIconId(FisherClassJobId),
            VendorGilFilter.Craftable  => GetVendorMainCommandIconId(CraftingLogMainCommandId),
            VendorGilFilter.Housing    => GetVendorMainCommandIconId(HousingMainCommandId),
            VendorGilFilter.Dyes       => GetVendorGeneralActionIconId(DyeGeneralActionId),
            _                          => 0,
        };

        if (iconId == 0 && filter != VendorGilFilter.Other)
            GatherBuddy.Log.Debug($"[VulcanWindow] Failed to resolve an icon for vendor gil filter {filter}.");

        _vendorGilFilterIconIds[filter] = iconId;
        return iconId;
    }

    private static (float ButtonSize, float ButtonPad, Vector2 IconSize) GetVendorIconButtonMetrics(float availableWidth)
    {
        const int columns = 4;
        const float buttonPad = 4f;
        var framePad = ImGui.GetStyle().FramePadding;
        var buttonSize = Math.Max(32f, (availableWidth - (columns - 1) * buttonPad) / columns);
        var iconWidth = Math.Max(18f, buttonSize - framePad.X * 2f);
        var iconHeight = Math.Max(18f, buttonSize - framePad.Y * 2f);
        return (buttonSize, buttonPad, new Vector2(iconWidth, iconHeight));
    }

    private bool DrawVendorFilterButton(string id, string label, ushort iconId, bool selected, string tooltip, float buttonSize, Vector2 iconSize)
    {
        if (selected)
            ImGui.PushStyleColor(ImGuiCol.Button, VendorSelectedFilterColor);

        bool clicked;
        ImGui.PushID(id);
        if (iconId != 0)
        {
            var wrap = Icons.DefaultStorage.TextureProvider
                .GetFromGameIcon(new GameIconLookup(iconId))
                .GetWrapOrDefault();
            clicked = wrap != null
                ? ImGui.ImageButton(wrap.Handle, iconSize)
                : ImGui.Button(label, new Vector2(buttonSize, buttonSize));
        }
        else
        {
            clicked = ImGui.Button(label, new Vector2(buttonSize, buttonSize));
        }
        ImGui.PopID();

        if (selected)
            ImGui.PopStyleColor();

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);
        return clicked;
    }

    private void DrawVendorTopLevelCurrencyFilters()
    {
        var currentGcCurrencyItemId = GetCurrentGrandCompanyCurrencyItemId();
        var topLevelFilters = new List<(VendorShopType ShopType, VendorCurrencyGroup? Group, string Label, uint CurrencyItemId)>
        {
            (VendorShopType.GilShop,           null,                         "Gil",      VendorShopResolver.GilCurrencyItemId),
            (VendorShopType.GrandCompanySeals, null,                         "GC Seals", currentGcCurrencyItemId),
        };
        topLevelFilters.AddRange(SpecialCurrencyGroups.Select(group => (VendorShopType.SpecialCurrency, (VendorCurrencyGroup?)group.Group, group.Label, group.CurrencyItemId)));

        var visibleFilters = topLevelFilters
            .Where(filter => GetVendorTopLevelFilterCount(filter.ShopType, filter.Group) > 0)
            .ToList();
        if (visibleFilters.Count == 0)
            return;

        var (buttonSize, buttonPad, iconSize) = GetVendorIconButtonMetrics(ImGui.GetContentRegionAvail().X);
        for (var i = 0; i < visibleFilters.Count; i++)
        {
            var (shopType, group, label, currencyItemId) = visibleFilters[i];
            var count = GetVendorTopLevelFilterCount(shopType, group);

            if (DrawVendorFilterButton(
                    $"vendorTopLevel_{shopType}_{group}",
                    label,
                    currencyItemId != 0 ? GetVendorCurrencyIconId(currencyItemId) : (ushort)0,
                    IsVendorTopLevelFilterSelected(shopType, group),
                    $"{label}\n{count:N0} item(s)",
                    buttonSize,
                    iconSize))
                SelectVendorTopLevelFilter(shopType, group);

            if ((i + 1) % 4 != 0 && i < visibleFilters.Count - 1)
                ImGui.SameLine(0, buttonPad);
        }
    }

    private void DrawVendorGilFilterButtons()
    {
        var gilCounts = GetGilCounts();
        var (buttonSize, buttonPad, iconSize) = GetVendorIconButtonMetrics(ImGui.GetContentRegionAvail().X);

        for (var i = 0; i < GilFilters.Length; i++)
        {
            var (filter, label) = GilFilters[i];
            gilCounts.TryGetValue(filter, out var count);
            var isSelected = _vendorGilFilter == filter;

            if (DrawVendorFilterButton(
                    $"vendorGil_{filter}",
                    label,
                    GetVendorGilFilterIconId(filter),
                    isSelected,
                    $"{label}\n{count:N0} item(s)",
                    buttonSize,
                    iconSize)
             && !isSelected)
            {
                _vendorGilFilter            = filter;
                _vendorSelectedCurrencyItemId = null;
                _vendorFilterDirty          = true;
            }

            if ((i + 1) % 4 != 0 && i < GilFilters.Length - 1)
                ImGui.SameLine(0, buttonPad);
        }
    }

    private List<(uint CurrencyItemId, string Label, int Count)> GetSpecificCurrencyFilters()
        => GetVendorBaseEntries()
            .GroupBy(entry => entry.CurrencyItemId)
            .Select(group => (group.Key, GetVendorCurrencyName(group.Key, group.First().CurrencyName), group.Count()))
            .OrderBy(group => group.Item2, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private void DrawVendorSpecificCurrencyButtons()
    {
        var currencyFilters = GetSpecificCurrencyFilters();
        if (currencyFilters.Count <= 1)
            return;

        var (buttonSize, buttonPad, iconSize) = GetVendorIconButtonMetrics(ImGui.GetContentRegionAvail().X);
        var renderedButtonCount = currencyFilters.Count + 1;
        var renderedButtons     = 0;
        var isAllSelected = _vendorSelectedCurrencyItemId == null;
        if (DrawVendorFilterButton("vendorCurrency_all", "All", 0, isAllSelected, "All currencies", buttonSize, iconSize))
        {
            if (!isAllSelected)
            {
                _vendorSelectedCurrencyItemId = null;
                _vendorFilterDirty            = true;
            }
        }

        renderedButtons++;
        if (renderedButtons % 4 != 0 && renderedButtons < renderedButtonCount)
            ImGui.SameLine(0, buttonPad);

        for (var i = 0; i < currencyFilters.Count; i++)
        {
            var (currencyItemId, label, count) = currencyFilters[i];
            if (DrawVendorFilterButton(
                    $"vendorCurrency_{currencyItemId}",
                    label,
                    GetVendorCurrencyIconId(currencyItemId),
                    _vendorSelectedCurrencyItemId == currencyItemId,
                    $"{label}\n{count:N0} item(s)",
                    buttonSize,
                    iconSize))
            {
                if (_vendorSelectedCurrencyItemId != currencyItemId)
                {
                    _vendorSelectedCurrencyItemId = currencyItemId;
                    _vendorFilterDirty            = true;
                }
            }

            renderedButtons++;
            if (renderedButtons % 4 != 0 && renderedButtons < renderedButtonCount)
                ImGui.SameLine(0, buttonPad);
        }
    }

    private void DrawVendorSidebar()
    {
        ImGui.Spacing();
        ImGui.TextColored(ImGuiColors.DalamudGrey3, "Currency");
        ImGui.Spacing();

        ImGui.BeginChild("##vendorSidebarScroll", new Vector2(-1, ImGui.GetContentRegionAvail().Y), false);
        DrawVendorTopLevelCurrencyFilters();

        if (_vendorCategory == VendorShopType.GilShop)
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            ImGui.TextColored(ImGuiColors.DalamudGrey3, "Source");
            ImGui.Spacing();
            DrawVendorGilFilterButtons();
        }
        else
        {
            var currencyFilters = GetSpecificCurrencyFilters();
            if (currencyFilters.Count > 1)
            {
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();
                ImGui.TextColored(ImGuiColors.DalamudGrey3, "Specific Currency");
                ImGui.Spacing();
                DrawVendorSpecificCurrencyButtons();
            }
        }
        ImGui.EndChild();
    }

    private IEnumerable<VendorShopEntry> GetVendorBaseEntries()
        => _vendorCategory switch
        {
            VendorShopType.GilShop => _vendorGilFilter switch
            {
                VendorGilFilter.Gatherable => VendorShopResolver.GilShopEntries.Where(entry => VendorShopResolver.GatherableIds.Contains(entry.ItemId)),
                VendorGilFilter.Fish       => VendorShopResolver.GilShopEntries.Where(entry => VendorShopResolver.FishIds.Contains(entry.ItemId)),
                VendorGilFilter.Craftable  => VendorShopResolver.GilShopEntries.Where(entry => VendorShopResolver.CraftableIds.Contains(entry.ItemId)),
                VendorGilFilter.Housing    => VendorShopResolver.GilShopEntries.Where(entry => VendorShopResolver.HousingItemIds.Contains(entry.ItemId)),
                VendorGilFilter.Dyes       => VendorShopResolver.GilShopEntries.Where(entry => VendorShopResolver.DyeItemIds.Contains(entry.ItemId)),
                VendorGilFilter.Other      => VendorShopResolver.GilShopEntries.Where(IsGilOtherEntry),
                _                          => VendorShopResolver.GilShopEntries,
            },
            VendorShopType.GrandCompanySeals => VendorShopResolver.GcShopEntries
                .Where(VendorShopResolver.MatchesCurrentGrandCompany),
            VendorShopType.SpecialCurrency => VendorShopResolver.SpecialShopEntries
                .Where(entry => _vendorSelectedGroup == null || entry.Group == _vendorSelectedGroup),
            _ => Enumerable.Empty<VendorShopEntry>(),
        };

    private string GetVendorSortLabel()
        => _vendorSortColumn switch
        {
            VendorSortColumn.Name     => "Name",
            VendorSortColumn.Cost     => "Cost",
            VendorSortColumn.Currency => "Currency",
            VendorSortColumn.Vendor   => "Vendor",
            VendorSortColumn.Location => "Location",
            _                         => "Sort",
        };

    private string GetVendorSortVendorName(VendorDisplayRow row)
        => GetSelectedVendorOption(row)?.Npc.Name
        ?? row.FallbackVendorName;

    private string GetVendorSortZoneName(VendorDisplayRow row)
        => GetSelectedVendorOption(row)?.ZoneName ?? "Unknown";

    private void SortVendorDisplayRows(List<VendorDisplayRow> rows)
    {
        if (rows.Count <= 1)
            return;

        IOrderedEnumerable<VendorDisplayRow> ordered = _vendorSortColumn switch
        {
            VendorSortColumn.Cost => _vendorSortDirection == VendorSortDirection.Ascending
                ? rows.OrderBy(row => row.Entry.Cost)
                    .ThenBy(row => row.Entry.ItemName, StringComparer.OrdinalIgnoreCase)
                : rows.OrderByDescending(row => row.Entry.Cost)
                    .ThenBy(row => row.Entry.ItemName, StringComparer.OrdinalIgnoreCase),
            VendorSortColumn.Currency => _vendorSortDirection == VendorSortDirection.Ascending
                ? rows.OrderBy(row => GetVendorCurrencyName(row.Entry.CurrencyItemId, row.Entry.CurrencyName), StringComparer.OrdinalIgnoreCase)
                    .ThenBy(row => row.Entry.Cost)
                    .ThenBy(row => row.Entry.ItemName, StringComparer.OrdinalIgnoreCase)
                : rows.OrderByDescending(row => GetVendorCurrencyName(row.Entry.CurrencyItemId, row.Entry.CurrencyName), StringComparer.OrdinalIgnoreCase)
                    .ThenBy(row => row.Entry.Cost)
                    .ThenBy(row => row.Entry.ItemName, StringComparer.OrdinalIgnoreCase),
            VendorSortColumn.Vendor => _vendorSortDirection == VendorSortDirection.Ascending
                ? rows.OrderBy(GetVendorSortVendorName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(row => row.Entry.ItemName, StringComparer.OrdinalIgnoreCase)
                : rows.OrderByDescending(GetVendorSortVendorName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(row => row.Entry.ItemName, StringComparer.OrdinalIgnoreCase),
            VendorSortColumn.Location => _vendorSortDirection == VendorSortDirection.Ascending
                ? rows.OrderBy(row => GetSelectedVendorOption(row)?.Location == null ? 1 : 0)
                    .ThenBy(row => GetSelectedVendorOption(row)?.Location?.TerritoryId == Dalamud.ClientState.TerritoryType ? 0 : 1)
                    .ThenBy(GetVendorSortZoneName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(row => row.Entry.ItemName, StringComparer.OrdinalIgnoreCase)
                : rows.OrderBy(row => GetSelectedVendorOption(row)?.Location == null ? 1 : 0)
                    .ThenByDescending(row => GetSelectedVendorOption(row)?.Location?.TerritoryId == Dalamud.ClientState.TerritoryType ? 0 : 1)
                    .ThenByDescending(GetVendorSortZoneName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(row => row.Entry.ItemName, StringComparer.OrdinalIgnoreCase),
            _ => _vendorSortDirection == VendorSortDirection.Ascending
                ? rows.OrderBy(row => row.Entry.ItemName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(row => row.Entry.Cost)
                : rows.OrderByDescending(row => row.Entry.ItemName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(row => row.Entry.Cost),
        };

        var originalCount = rows.Count;
        var sortedRows    = ordered.ToList();
        if (sortedRows.Count != originalCount)
            GatherBuddy.Log.Debug($@"[VulcanWindow] Vendor sort changed row count unexpectedly ({originalCount} -> {sortedRows.Count}) for category={_vendorCategory}, group={_vendorSelectedGroup?.ToString() ?? "none"}, currency={_vendorSelectedCurrencyItemId?.ToString() ?? "all"}, gil={_vendorGilFilter}, search=""{_vendorSearch}""");

        rows.Clear();
        rows.AddRange(sortedRows);
    }

    private void DrawVendorSortControl()
    {
        var sortIcon = _vendorSortDirection == VendorSortDirection.Ascending
            ? FontAwesomeIcon.ArrowUp
            : FontAwesomeIcon.ArrowDown;

        ImGui.TextColored(ImGuiColors.DalamudGrey3, "Sort:");
        ImGui.SameLine();
        if (ImGui.Button($"{GetVendorSortLabel()}##vendorSortBtn", new Vector2(90f, 0f)))
            ImGui.OpenPopup("##vendorSortMenu");

        ImGui.SameLine(0f, 4f);
        using (ImRaii.PushFont(UiBuilder.IconFont))
            ImGui.Text(sortIcon.ToIconString());

        if (!ImGui.BeginPopup("##vendorSortMenu"))
            return;

        foreach (var sortColumn in Enum.GetValues<VendorSortColumn>())
        {
            var isSelected = _vendorSortColumn == sortColumn;
            if (!ImGui.MenuItem(sortColumn.ToString(), string.Empty, isSelected))
                continue;

            if (isSelected)
                _vendorSortDirection = _vendorSortDirection == VendorSortDirection.Ascending
                    ? VendorSortDirection.Descending
                    : VendorSortDirection.Ascending;
            else
                _vendorSortColumn = sortColumn;
            _vendorFilterDirty = true;
        }

        ImGui.EndPopup();
    }

    private void DrawVendorCostCell(VendorDisplayRow row, Vector2 iconVec, float iconSize)
    {
        if (row.CurrencyIconId != 0)
        {
            var currencyIcon = Icons.DefaultStorage.TextureProvider.GetFromGameIcon(new GameIconLookup(row.CurrencyIconId));
            if (currencyIcon.TryGetWrap(out var wrap, out _))
            {
                ImGui.Image(wrap.Handle, iconVec);
                ImGui.SameLine(0, 4f);
            }
        }

        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (iconSize - ImGui.GetTextLineHeight()) / 2f);
        ImGui.TextUnformatted(row.CostText);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(GetVendorCurrencyName(row.Entry.CurrencyItemId, row.Entry.CurrencyName));
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
        if (ImGui.InputTextWithHint("##vendorSearch", "Search items...", ref _vendorSearch, 256))
            _vendorFilterDirty = true;

        ImGui.Spacing();
        var buyListManager  = GatherBuddy.VendorBuyListManager;

        var purchaseManager = GatherBuddy.VendorPurchaseManager;
        if (ImGui.Button("Open Vendor Buy List"))
            buyListManager.OpenWindow();
        ImGui.SameLine();
        ImGui.TextColored(ImGuiColors.DalamudYellow, $"{buyListManager.ActiveListName}: {buyListManager.Entries.Count} item(s)");
        ImGui.Spacing();

        if (!string.IsNullOrWhiteSpace(buyListManager.StatusText))
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow,
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
        ImGui.SameLine(Math.Max(ImGui.GetCursorPosX(), ImGui.GetWindowContentRegionMax().X - 140f));
        DrawVendorSortControl();
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
        ImGui.TableSetupColumn("Cost",     ImGuiTableColumnFlags.WidthFixed, 110f);
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
        DrawVendorCostCell(row, iconVec, iconSize);
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

    private void DrawVendorAddToListPopup(VendorShopEntry entry, VendorNpc vendor, uint targetQuantity)
    {
        var buyListManager = GatherBuddy.VendorBuyListManager;
        if (buyListManager == null)
            return;

        if (ImGui.Selectable("Create New List..."))
        {
            var vendorBuyListWindow = GatherBuddy.VendorBuyListWindow;
            if (vendorBuyListWindow == null)
                GatherBuddy.Log.Warning($"[VulcanWindow] Unable to open Create Vendor List popup for {entry.ItemName}: vendor buy list window unavailable.");
            else if (!vendorBuyListWindow.OpenCreateListPopup(entry, vendor, targetQuantity))
                GatherBuddy.Log.Debug($"[VulcanWindow] Unable to create a new vendor buy list for {entry.ItemName} with target {targetQuantity:N0}.");
        }

        if (!ImGui.BeginMenu("Add to Existing List", buyListManager.Lists.Count > 0))
            return;

        foreach (var list in buyListManager.Lists.OrderByDescending(list => list.CreatedAt))
        {
            if (ImGui.Selectable(list.Name)
             && !buyListManager.TryAddTarget(list.Id, entry, vendor, targetQuantity, selectList: true, openWindow: true, announce: true))
                GatherBuddy.Log.Debug($"[VulcanWindow] Unable to add {entry.ItemName} to vendor list '{list.Name}' with target {targetQuantity:N0}.");
        }

        ImGui.EndMenu();
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
        var buyListManager = GatherBuddy.VendorBuyListManager;
        if (buyListManager == null)
        {
            DrawVendorIconButton($"vendor_add_disabled_{row.IdSuffix}", FontAwesomeIcon.Plus,
                VendorBuyListButtonColor, "Vendor buy list manager unavailable", true);
            return;
        }

        var targetQuantity = (uint)Math.Max(1, GetVendorPurchaseQuantity(row.Entry));
        var contextMenuId = $"##vendorAddToListPopup_{row.IdSuffix}";
        if (DrawVendorIconButton($"vendor_add_{row.IdSuffix}", FontAwesomeIcon.Plus,
                VendorBuyListButtonColor,
                "Add to active list - right-click for more options")
         && !buyListManager.TryAddTarget(row.Entry, selectedNpc.Npc, targetQuantity, openWindow: false, announce: false))
            GatherBuddy.Log.Debug($@"[VulcanWindow] Unable to add {row.Entry.ItemName} to active vendor list '{buyListManager.ActiveListName}' with target {targetQuantity:N0}.");

        if (ImGui.BeginPopupContextItem(contextMenuId))
        {
            DrawVendorAddToListPopup(row.Entry, selectedNpc.Npc, targetQuantity);
            ImGui.EndPopup();
        }
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
        IEnumerable<VendorShopEntry> source = GetVendorBaseEntries();

        if (_vendorSelectedCurrencyItemId.HasValue)
            source = source.Where(entry => entry.CurrencyItemId == _vendorSelectedCurrencyItemId.Value);

        if (!string.IsNullOrWhiteSpace(_vendorSearch))
        {
            var search = _vendorSearch;
            source = source.Where(entry => entry.ItemName.Contains(search, StringComparison.OrdinalIgnoreCase));
        }
        _vendorDisplay = source
            .Select(entry => BuildVendorDisplayRow(entry, locationCacheReady))
            .ToList();
        SortVendorDisplayRows(_vendorDisplay);

        if (_vendorEditingQuantityKey.HasValue
         && !_vendorDisplay.Any(row => VendorQuantityKey(row.Entry) == _vendorEditingQuantityKey.Value))
            StopEditingVendorQuantity();
        _vendorDisplayBuiltWithResolvedLocations = locationCacheReady;
    }
}
