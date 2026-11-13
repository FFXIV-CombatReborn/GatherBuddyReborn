using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using GatherBuddy.RebornUI;
using GatherBuddy.Utility;
using GatherBuddy.Vulcan.Vendors;
using Lumina.Excel.Sheets;
using RebornMaterial;
using System;
using System.Collections.Generic;
using System.Numerics;
using ImRaii = Dalamud.Interface.Utility.Raii.ImRaii;

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
    private const uint MinerClassJobId = 16;
    private const uint FisherClassJobId = 18;
    private const uint CraftingLogMainCommandId = 9;
    private const uint HousingMainCommandId = 44;
    private const uint DyeGeneralActionId = 15;

    private VendorShopType _vendorCategory = VendorShopType.GilShop;
    private VendorCurrencyGroup? _vendorSelectedGroup = null;
    private uint? _vendorSelectedCurrencyItemId;
    private VendorGilFilter _vendorGilFilter = VendorGilFilter.All;
    private VendorSortColumn _vendorSortColumn = VendorSortColumn.Name;
    private VendorSortDirection _vendorSortDirection = VendorSortDirection.Ascending;
    private string _vendorSearch = string.Empty;
    private bool _vendorFilterDirty = true;
    private List<VendorDisplayRow> _vendorDisplay = [];
    private bool _vendorDisplayBuiltWithResolvedLocations;
    private int _vendorDisplayShopRevision = -1;
    private int _vendorDisplayLocationRevision = -1;
    private Dictionary<VendorCurrencyGroup, int>? _vendorGroupCounts;
    private Dictionary<VendorGilFilter, int>? _vendorGilCounts;
    private readonly Dictionary<VendorGilFilter, ushort> _vendorGilFilterIconIds = [];
    private readonly Dictionary<uint, ushort> _vendorCurrencyIconIds = [];
    private readonly Dictionary<uint, string> _vendorCurrencyNames = [];
    private readonly Dictionary<uint, string> _vendorZoneNames = [];
    private readonly Dictionary<(VendorShopType ShopType, uint ItemId, uint CurrencyItemId, uint Cost), int> _vendorPurchaseQuantities = [];
    private static Vector4 VendorMarkerButtonColor
        => M3.Scheme.Info;

    private static Vector4 VendorBuyListButtonColor
        => M3.Scheme.Tertiary;

    private static Vector4 VendorAutomationButtonColor
        => M3.Scheme.Success;

    private static readonly M3PluginRequirement[] RequiredVendorAutomationPlugins =
    [
        new("InventoryTools", "Allagan Tools"),
        new(VendorAutomationRequirements.AllaganItemSearchInternalName, "Allagan Item Search"),
    ];
    private static (VendorShopType ShopType, uint ItemId, uint CurrencyItemId, uint Cost) VendorQuantityKey(VendorShopEntry entry)
        => (entry.ShopType, entry.ItemId, entry.CurrencyItemId, entry.Cost);

    private int GetVendorPurchaseQuantity(VendorShopEntry entry)
    {
        var key = VendorQuantityKey(entry);
        if (_vendorPurchaseQuantities.TryGetValue(key, out var quantity) && quantity > 0)
        {
            return quantity;
        }

        _vendorPurchaseQuantities[key] = 1;
        return 1;
    }

    private void SetVendorPurchaseQuantity(VendorShopEntry entry, int quantity)
        => _vendorPurchaseQuantities[VendorQuantityKey(entry)] = System.Math.Max(1, quantity);

    private static float GetVendorQuantityInputWidth()
        => System.Math.Max(VulcanUiScaling.Scaled(80f), ImGui.CalcTextSize("99999").X + ImGui.GetStyle().FramePadding.X * 2f + VulcanUiScaling.Scaled(12f));

    private static void SetVendorNpcPref(VendorShopEntry entry, VendorNpc vendor)
        => VendorPreferenceHelper.SetPreferredNpc(entry, vendor);

    private static string GetVendorDisplayRowId(VendorShopEntry entry)
        => $"{(int)entry.ShopType}_{(int)entry.Group}_{entry.ItemId}_{entry.CurrencyItemId}_{entry.Cost}";

    private static int GetCurrentGrandCompanyEntryCount()
    {
        var count = 0;
        foreach (var entry in VendorShopResolver.GcShopEntries)
        {
            if (VendorShopResolver.MatchesCurrentGrandCompany(entry))
            {
                count++;
            }
        }

        return count;
    }

    private static uint GetCurrentGrandCompanyCurrencyItemId()
        => VendorShopResolver.GetCurrentGrandCompanySealCurrencyItemId() is var currencyItemId && currencyItemId != 0
            ? currencyItemId
            : VendorShopResolver.GetGrandCompanySealCurrencyItemId(1);

    private ushort GetVendorCurrencyIconId(uint currencyItemId)
    {
        if (_vendorCurrencyIconIds.TryGetValue(currencyItemId, out var iconId))
        {
            return iconId;
        }

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
        {
            return currencyName;
        }

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
        {
            routeParts.Add($"Rank {npc.GcRankIndex + 1}");
        }

        if (npc.GcCategoryIndex >= 0)
        {
            routeParts.Add($"Category {npc.GcCategoryIndex + 1}");
        }

        if (npc.InclusionPageIndex >= 0)
        {
            routeParts.Add($"Page {npc.InclusionPageIndex + 1}");
        }

        if (npc.InclusionSubPageIndex > 0)
        {
            routeParts.Add($"Tab {npc.InclusionSubPageIndex}");
        }

        if (routeParts.Count == 0 && npc.SourceShopId != 0)
        {
            routeParts.Add($"Route {npc.SourceShopId}");
        }

        return string.Join(" / ", routeParts);
    }

    private void DrawVendorsPage()
    {
        if (!VendorShopResolver.IsInitialized && !VendorShopResolver.IsInitializing)
        {
            VendorShopResolver.InitializeAsync();
        }

        if (VendorShopResolver.IsInitializing)
        {
            M3Widgets.LinearProgressIndeterminate(new Vector2(ImGui.GetContentRegionAvail().X, 4f * M3.Scale));
            M3Text.Draw("Loading vendor data...", M3TextStyle.Muted);
            return;
        }

        DrawVendorsTabContent();
    }

    private void DrawVendorsTabContent()
    {
        InvalidateVendorDisplayForCacheRevisions();
        var avail = ImGui.GetContentRegionAvail();
        using (var left = M3Panel.Begin("##vendorLeft", new Vector2(VulcanUiScaling.Scaled(240f), avail.Y), true, VulcanUiStyle.PanelBackground))
        {
            if (left)
            {
                DrawVendorSidebar();
            }
        }

        ImGui.SameLine(0, M3.Space2);
        using var right = M3Panel.Begin("##vendorRight", new Vector2(0, avail.Y), false);
        if (right)
        {
            DrawVendorItemTable();
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
            {
                continue;
            }

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

    private static VendorDisplayNpcOption? GetSelectedVendorOption(VendorDisplayRow row)
    {
        if (row.NpcOptions.Count == 0)
        {
            return null;
        }

        var npcs = new List<VendorNpc>(row.NpcOptions.Count);
        foreach (var option in row.NpcOptions)
        {
            npcs.Add(option.Npc);
        }

        var preferredNpc = VendorPreferenceHelper.ResolvePreferredNpc(row.Entry, npcs);
        if (preferredNpc != null)
        {
            foreach (var option in row.NpcOptions)
            {
                if (VendorPreferenceHelper.MatchesVendor(option.Npc, preferredNpc))
                {
                    return option;
                }
            }
        }

        return row.NpcOptions[0];
    }

    private static bool HasMultipleNpcOptions(IReadOnlyList<VendorDisplayNpcOption> options, VendorDisplayNpcOption option, bool matchZone)
    {
        var matches = 0;
        for (var i = 0; i < options.Count; i++)
        {
            var other = options[i];
            if (other.Npc.Name.Equals(option.Npc.Name, StringComparison.OrdinalIgnoreCase)
             && (!matchZone || string.Equals(other.ZoneName, option.ZoneName, StringComparison.OrdinalIgnoreCase))
             && ++matches > 1)
            {
                return true;
            }
        }

        return false;
    }

    private static string GetVendorNpcDisplayLabel(VendorDisplayRow row, VendorDisplayNpcOption option)
    {
        var duplicateRoute = HasMultipleNpcOptions(row.NpcOptions, option, true);
        if (duplicateRoute)
        {
            var routeLabel = GetVendorRouteLabel(option.Npc);
            if (!string.IsNullOrWhiteSpace(option.ZoneName) && !string.IsNullOrWhiteSpace(routeLabel))
            {
                return $"{option.Npc.Name} ({option.ZoneName} · {routeLabel})";
            }

            if (!string.IsNullOrWhiteSpace(option.ZoneName))
            {
                return $"{option.Npc.Name} ({option.ZoneName})";
            }

            if (!string.IsNullOrWhiteSpace(routeLabel))
            {
                return $"{option.Npc.Name} [{routeLabel}]";
            }

            return $"{option.Npc.Name} [{option.Npc.NpcId}]";
        }
        var duplicateName = HasMultipleNpcOptions(row.NpcOptions, option, false);
        if (!duplicateName)
        {
            return !string.IsNullOrWhiteSpace(option.ZoneName)
                ? $"{option.Npc.Name} ({option.ZoneName})"
                : option.Npc.Name;
        }

        if (!string.IsNullOrWhiteSpace(option.ZoneName))
        {
            return $"{option.Npc.Name} ({option.ZoneName})";
        }

        return $"{option.Npc.Name} [{option.Npc.NpcId}]";
    }

    private static void DrawVendorZoneCell(VendorDisplayNpcOption? selectedNpc)
    {
        var location = selectedNpc?.Location;
        if (location == null)
        {
            M3Text.Draw("Unknown", M3TextStyle.Faint);
            return;
        }

        if (Dalamud.ClientState.TerritoryType == location.TerritoryId)
        {
            Ui.Text(selectedNpc!.ZoneName, M3.Scheme.Success);
            M3Tooltip.Hovered("You are in this zone.");
        }
        else
        {
            ImGui.TextUnformatted(selectedNpc!.ZoneName);
        }
    }

    private static void DrawVendorMapMarkerButton(VendorDisplayRow row, VendorDisplayNpcOption? selectedNpc)
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
        {
            VendorNavigator.PlaceMapMarker(location);
        }
    }

    private string GetVendorZoneName(VendorNpcLocation? location)
    {
        if (location == null)
        {
            return "Unknown";
        }

        if (_vendorZoneNames.TryGetValue(location.TerritoryId, out var zoneName))
        {
            return zoneName;
        }

        var territorySheet = Dalamud.GameData.GetExcelSheet<TerritoryType>();
        if (territorySheet == null || !territorySheet.TryGetRow(location.TerritoryId, out var territory))
        {
            return _vendorZoneNames[location.TerritoryId] = $"Territory {location.TerritoryId}";
        }

        zoneName = territory.PlaceName.RowId != 0
            ? territory.PlaceName.Value.Name.ToString()
            : $"Territory {location.TerritoryId}";
        _vendorZoneNames[location.TerritoryId] = zoneName;
        return zoneName;
    }

    private static bool DrawVendorIconButton(string id, FontAwesomeIcon icon, Vector4 color, string tooltip, bool disabled = false)
        => M3Widgets.IconButton($"##{id}", icon, tooltip, tint: disabled ? null : color, diameter: ImGui.GetFrameHeight(), enabled: !disabled);

    private Dictionary<VendorCurrencyGroup, int> GetGroupCounts()
    {
        if (_vendorGroupCounts != null)
        {
            return _vendorGroupCounts;
        }

        var groupCounts = new Dictionary<VendorCurrencyGroup, int>();
        foreach (var entry in VendorShopResolver.SpecialShopEntries)
        {
            groupCounts.TryGetValue(entry.Group, out var count);
            groupCounts[entry.Group] = count + 1;
        }

        _vendorGroupCounts = groupCounts;
        return _vendorGroupCounts;
    }

    private Dictionary<VendorGilFilter, int> GetGilCounts()
    {
        if (_vendorGilCounts != null)
        {
            return _vendorGilCounts;
        }

        var entries = VendorShopResolver.GilShopEntries;
        _vendorGilCounts = new Dictionary<VendorGilFilter, int>
        {
            [VendorGilFilter.All] = entries.Count,
            [VendorGilFilter.Gatherable] = CountGilEntries(entries, VendorGilFilter.Gatherable),
            [VendorGilFilter.Fish] = CountGilEntries(entries, VendorGilFilter.Fish),
            [VendorGilFilter.Craftable] = CountGilEntries(entries, VendorGilFilter.Craftable),
            [VendorGilFilter.Housing] = CountGilEntries(entries, VendorGilFilter.Housing),
            [VendorGilFilter.Dyes] = CountGilEntries(entries, VendorGilFilter.Dyes),
            [VendorGilFilter.Other] = CountGilEntries(entries, VendorGilFilter.Other),
        };
        return _vendorGilCounts;
    }

    private static int CountGilEntries(IReadOnlyList<VendorShopEntry> entries, VendorGilFilter filter)
    {
        var count = 0;
        for (var i = 0; i < entries.Count; i++)
        {
            if (MatchesGilFilter(entries[i], filter))
            {
                count++;
            }
        }

        return count;
    }

    private static bool MatchesGilFilter(VendorShopEntry entry, VendorGilFilter filter)
        => filter switch
        {
            VendorGilFilter.Gatherable => VendorShopResolver.GatherableIds.Contains(entry.ItemId),
            VendorGilFilter.Fish => VendorShopResolver.FishIds.Contains(entry.ItemId),
            VendorGilFilter.Craftable => VendorShopResolver.CraftableIds.Contains(entry.ItemId),
            VendorGilFilter.Housing => VendorShopResolver.HousingItemIds.Contains(entry.ItemId),
            VendorGilFilter.Dyes => VendorShopResolver.DyeItemIds.Contains(entry.ItemId),
            VendorGilFilter.Other => IsGilOtherEntry(entry),
            _ => true,
        };

    private static bool IsGilOtherEntry(VendorShopEntry entry)
        => !VendorShopResolver.GatherableIds.Contains(entry.ItemId)
        && !VendorShopResolver.FishIds.Contains(entry.ItemId)
        && !VendorShopResolver.CraftableIds.Contains(entry.ItemId)
        && !VendorShopResolver.HousingItemIds.Contains(entry.ItemId)
        && !VendorShopResolver.DyeItemIds.Contains(entry.ItemId);
    private int GetVendorTopLevelFilterCount(VendorShopType shopType, VendorCurrencyGroup? group = null)
        => shopType switch
        {
            VendorShopType.GilShop => VendorShopResolver.GilShopEntries.Count,
            VendorShopType.GrandCompanySeals => GetCurrentGrandCompanyEntryCount(),
            VendorShopType.SpecialCurrency when group.HasValue => GetGroupCounts().GetValueOrDefault(group.Value, 0),
            VendorShopType.SpecialCurrency => VendorShopResolver.SpecialShopEntries.Count,
            _ => 0,
        };

    private bool IsVendorTopLevelFilterSelected(VendorShopType shopType, VendorCurrencyGroup? group = null)
        => _vendorCategory == shopType
        && (shopType != VendorShopType.SpecialCurrency || _vendorSelectedGroup == group);

    private void SelectVendorTopLevelFilter(VendorShopType shopType, VendorCurrencyGroup? group = null)
    {
        if (IsVendorTopLevelFilterSelected(shopType, group))
        {
            return;
        }

        _vendorCategory = shopType;
        _vendorSelectedGroup = shopType == VendorShopType.SpecialCurrency ? group : null;
        _vendorSelectedCurrencyItemId = null;
        _vendorFilterDirty = true;
    }

    private static ushort GetVendorClassJobIconId(uint classJobId)
        => classJobId == 0 ? (ushort)0 : (ushort)(62100 + classJobId);

    private static ushort GetVendorMainCommandIconId(uint mainCommandId)
    {
        var sheet = Dalamud.GameData.GetExcelSheet<MainCommand>();
        return sheet != null && sheet.TryGetRow(mainCommandId, out var mainCommand)
            ? (ushort)mainCommand.Icon
            : (ushort)0;
    }

    private static ushort GetVendorGeneralActionIconId(uint generalActionId)
    {
        var sheet = Dalamud.GameData.GetExcelSheet<GeneralAction>();
        return sheet != null && sheet.TryGetRow(generalActionId, out var generalAction)
            ? (ushort)generalAction.Icon
            : (ushort)0;
    }

    private ushort GetVendorGilFilterIconId(VendorGilFilter filter)
    {
        if (_vendorGilFilterIconIds.TryGetValue(filter, out var iconId))
        {
            return iconId;
        }

        iconId = filter switch
        {
            VendorGilFilter.All => GetVendorCurrencyIconId(VendorShopResolver.GilCurrencyItemId),
            VendorGilFilter.Gatherable => GetVendorClassJobIconId(MinerClassJobId),
            VendorGilFilter.Fish => GetVendorClassJobIconId(FisherClassJobId),
            VendorGilFilter.Craftable => GetVendorMainCommandIconId(CraftingLogMainCommandId),
            VendorGilFilter.Housing => GetVendorMainCommandIconId(HousingMainCommandId),
            VendorGilFilter.Dyes => GetVendorGeneralActionIconId(DyeGeneralActionId),
            _ => 0,
        };

        if (iconId == 0 && filter != VendorGilFilter.Other)
        {
            GatherBuddy.Log.Debug($"[VulcanWindow] Failed to resolve an icon for vendor gil filter {filter}.");
        }

        _vendorGilFilterIconIds[filter] = iconId;
        return iconId;
    }

    private static float VendorFilterButtonSize
        => VulcanUiScaling.Scaled(34f);

    private static bool DrawVendorFilterButton(ref M3Flow flow, string id, string label, ushort iconId, bool selected, string tooltip)
    {
        if (iconId != 0)
        {
            flow.Next(VendorFilterButtonSize);
            return M3Widgets.GameIconButton($"##{id}", iconId, VendorFilterButtonSize, tooltip: tooltip, selected: selected);
        }

        flow.Next(M3Widgets.ChipWidth(label));
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + MathF.Max(0f, (VendorFilterButtonSize - M3Widgets.ChipHeight) * 0.5f));
        return M3Widgets.Chip($"##{id}", label, selected, tooltip: tooltip);
    }

    private void DrawVendorTopLevelCurrencyFilters()
    {
        var currentGcCurrencyItemId = GetCurrentGrandCompanyCurrencyItemId();
        var topLevelFilters = new List<(VendorShopType ShopType, VendorCurrencyGroup? Group, string Label, uint CurrencyItemId)>
        {
            (VendorShopType.GilShop,           null, "Gil",      VendorShopResolver.GilCurrencyItemId),
            (VendorShopType.GrandCompanySeals, null, "GC Seals", currentGcCurrencyItemId),
        };
        foreach (var (specialGroup, specialLabel, specialCurrencyItemId) in SpecialCurrencyGroups)
        {
            topLevelFilters.Add((VendorShopType.SpecialCurrency, specialGroup, specialLabel, specialCurrencyItemId));
        }

        var flow = new M3Flow(M3.Space1);
        foreach (var (shopType, group, label, currencyItemId) in topLevelFilters)
        {
            var count = GetVendorTopLevelFilterCount(shopType, group);
            if (count == 0)
            {
                continue;
            }

            if (DrawVendorFilterButton(ref flow, $"vendorTopLevel_{shopType}_{group}", label,
                    currencyItemId != 0 ? GetVendorCurrencyIconId(currencyItemId) : (ushort)0,
                    IsVendorTopLevelFilterSelected(shopType, group), $"{label}\n{count:N0} items"))
            {
                SelectVendorTopLevelFilter(shopType, group);
            }
        }
    }

    private void DrawVendorGilFilterButtons()
    {
        var gilCounts = GetGilCounts();
        var flow = new M3Flow(M3.Space1);
        foreach (var (filter, label) in GilFilters)
        {
            gilCounts.TryGetValue(filter, out var count);
            var isSelected = _vendorGilFilter == filter;
            if (DrawVendorFilterButton(ref flow, $"vendorGil_{filter}", label, GetVendorGilFilterIconId(filter), isSelected, $"{label}\n{count:N0} items")
             && !isSelected)
            {
                _vendorGilFilter = filter;
                _vendorSelectedCurrencyItemId = null;
                _vendorFilterDirty = true;
            }
        }
    }

    private List<(uint CurrencyItemId, string Label, int Count)> GetSpecificCurrencyFilters()
    {
        var filters = new List<(uint CurrencyItemId, string Label, int Count)>();
        var filterIndexes = new Dictionary<uint, int>();
        foreach (var entry in GetVendorBaseEntries())
        {
            if (filterIndexes.TryGetValue(entry.CurrencyItemId, out var filterIndex))
            {
                var filter = filters[filterIndex];
                filters[filterIndex] = (filter.CurrencyItemId, filter.Label, filter.Count + 1);
            }
            else
            {
                filterIndexes.Add(entry.CurrencyItemId, filters.Count);
                filters.Add((entry.CurrencyItemId, GetVendorCurrencyName(entry.CurrencyItemId, entry.CurrencyName), 1));
            }
        }

        filters.StableSort(static (lhs, rhs) => StringComparer.OrdinalIgnoreCase.Compare(lhs.Label, rhs.Label));
        return filters;
    }

    private void DrawVendorSpecificCurrencyButtons()
    {
        var currencyFilters = GetSpecificCurrencyFilters();
        if (currencyFilters.Count <= 1)
        {
            return;
        }

        var flow = new M3Flow(M3.Space1);
        var isAllSelected = _vendorSelectedCurrencyItemId == null;
        if (DrawVendorFilterButton(ref flow, "vendorCurrency_all", "All", 0, isAllSelected, "All currencies") && !isAllSelected)
        {
            _vendorSelectedCurrencyItemId = null;
            _vendorFilterDirty = true;
        }

        foreach (var (currencyItemId, label, count) in currencyFilters)
        {
            if (DrawVendorFilterButton(ref flow, $"vendorCurrency_{currencyItemId}", label, GetVendorCurrencyIconId(currencyItemId),
                    _vendorSelectedCurrencyItemId == currencyItemId, $"{label}\n{count:N0} items")
             && _vendorSelectedCurrencyItemId != currencyItemId)
            {
                _vendorSelectedCurrencyItemId = currencyItemId;
                _vendorFilterDirty = true;
            }
        }
    }

    private void DrawVendorSidebar()
    {
        M3Widgets.SectionLabel("Currency");
        DrawVendorTopLevelCurrencyFilters();

        if (_vendorCategory == VendorShopType.GilShop)
        {
            M3Widgets.SectionLabel("Source");
            DrawVendorGilFilterButtons();
        }
        else if (GetSpecificCurrencyFilters().Count > 1)
        {
            M3Widgets.SectionLabel("Specific currency");
            DrawVendorSpecificCurrencyButtons();
        }
    }

    private IEnumerable<VendorShopEntry> GetVendorBaseEntries()
    {
        switch (_vendorCategory)
        {
            case VendorShopType.GilShop:
                {
                    var gilFilter = _vendorGilFilter;
                    foreach (var entry in VendorShopResolver.GilShopEntries)
                    {
                        if (MatchesGilFilter(entry, gilFilter))
                        {
                            yield return entry;
                        }
                    }

                    break;
                }
            case VendorShopType.GrandCompanySeals:
                foreach (var entry in VendorShopResolver.GcShopEntries)
                {
                    if (VendorShopResolver.MatchesCurrentGrandCompany(entry))
                    {
                        yield return entry;
                    }
                }

                break;
            case VendorShopType.SpecialCurrency:
                foreach (var entry in VendorShopResolver.SpecialShopEntries)
                {
                    if (_vendorSelectedGroup == null || entry.Group == _vendorSelectedGroup)
                    {
                        yield return entry;
                    }
                }

                break;
        }
    }

    private string GetVendorSortLabel()
        => _vendorSortColumn switch
        {
            VendorSortColumn.Name => "Name",
            VendorSortColumn.Cost => "Cost",
            VendorSortColumn.Currency => "Currency",
            VendorSortColumn.Vendor => "Vendor",
            VendorSortColumn.Location => "Location",
            _ => "Sort",
        };

    private string GetVendorSortVendorName(VendorDisplayRow row)
        => GetSelectedVendorOption(row)?.Npc.Name
        ?? row.FallbackVendorName;

    private string GetVendorSortZoneName(VendorDisplayRow row)
        => GetSelectedVendorOption(row)?.ZoneName ?? "Unknown";

    private readonly record struct VendorSortKey(VendorDisplayRow Row, string Text, int LocationMissing, int OtherTerritory, int Index);

    private static int CompareVendorSortKeys(VendorSortKey lhs, VendorSortKey rhs, VendorSortColumn column, bool descending)
    {
        var (first, second) = descending ? (rhs, lhs) : (lhs, rhs);
        int order;
        switch (column)
        {
            case VendorSortColumn.Cost:
                order = first.Row.Entry.Cost.CompareTo(second.Row.Entry.Cost);
                if (order == 0)
                {
                    order = StringComparer.OrdinalIgnoreCase.Compare(lhs.Row.Entry.ItemName, rhs.Row.Entry.ItemName);
                }

                break;
            case VendorSortColumn.Currency:
                order = StringComparer.OrdinalIgnoreCase.Compare(first.Text, second.Text);
                if (order == 0)
                {
                    order = lhs.Row.Entry.Cost.CompareTo(rhs.Row.Entry.Cost);
                }

                if (order == 0)
                {
                    order = StringComparer.OrdinalIgnoreCase.Compare(lhs.Row.Entry.ItemName, rhs.Row.Entry.ItemName);
                }

                break;
            case VendorSortColumn.Vendor:
                order = StringComparer.OrdinalIgnoreCase.Compare(first.Text, second.Text);
                if (order == 0)
                {
                    order = StringComparer.OrdinalIgnoreCase.Compare(lhs.Row.Entry.ItemName, rhs.Row.Entry.ItemName);
                }

                break;
            case VendorSortColumn.Location:
                order = lhs.LocationMissing.CompareTo(rhs.LocationMissing);
                if (order == 0)
                {
                    order = first.OtherTerritory.CompareTo(second.OtherTerritory);
                }

                if (order == 0)
                {
                    order = StringComparer.OrdinalIgnoreCase.Compare(first.Text, second.Text);
                }

                if (order == 0)
                {
                    order = StringComparer.OrdinalIgnoreCase.Compare(lhs.Row.Entry.ItemName, rhs.Row.Entry.ItemName);
                }

                break;
            default:
                order = StringComparer.OrdinalIgnoreCase.Compare(first.Row.Entry.ItemName, second.Row.Entry.ItemName);
                if (order == 0)
                {
                    order = lhs.Row.Entry.Cost.CompareTo(rhs.Row.Entry.Cost);
                }

                break;
        }

        return order != 0 ? order : lhs.Index.CompareTo(rhs.Index);
    }

    private void SortVendorDisplayRows(List<VendorDisplayRow> rows)
    {
        if (rows.Count <= 1)
        {
            return;
        }

        var column = _vendorSortColumn;
        var descending = _vendorSortDirection != VendorSortDirection.Ascending;
        var currentTerritory = Dalamud.ClientState.TerritoryType;
        var keyedRows = new List<VendorSortKey>(rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var option = column is VendorSortColumn.Vendor or VendorSortColumn.Location ? GetSelectedVendorOption(row) : null;
            var text = column switch
            {
                VendorSortColumn.Currency => GetVendorCurrencyName(row.Entry.CurrencyItemId, row.Entry.CurrencyName),
                VendorSortColumn.Vendor => option?.Npc.Name ?? row.FallbackVendorName,
                VendorSortColumn.Location => option?.ZoneName ?? "Unknown",
                _ => string.Empty,
            };
            keyedRows.Add(new VendorSortKey(row, text, option?.Location == null ? 1 : 0, option?.Location?.TerritoryId == currentTerritory ? 0 : 1, i));
        }

        keyedRows.Sort((lhs, rhs) => CompareVendorSortKeys(lhs, rhs, column, descending));
        for (var i = 0; i < keyedRows.Count; i++)
        {
            rows[i] = keyedRows[i].Row;
        }
    }

    private void DrawVendorSortControl()
    {
        var label = GetVendorSortLabel();
        if (M3Widgets.Button("##vendorSortBtn", label, M3ButtonStyle.Text, FontAwesomeIcon.Sort, tooltip: "Sort by"))
        {
            M3Menu.Open("##vendorSortMenu");
        }

        ImGui.SameLine(0, M3.Space1);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (M3Widgets.ButtonHeight - M3Widgets.IconButtonSize) * 0.5f);
        var ascending = _vendorSortDirection == VendorSortDirection.Ascending;
        if (M3Widgets.IconButton("##vendorSortDirection", ascending ? FontAwesomeIcon.ArrowUp : FontAwesomeIcon.ArrowDown,
                ascending ? "Ascending, click for descending" : "Descending, click for ascending"))
        {
            _vendorSortDirection = ascending ? VendorSortDirection.Descending : VendorSortDirection.Ascending;
            _vendorFilterDirty = true;
        }

        using var menu = M3Menu.Begin("##vendorSortMenu");
        if (!menu.IsOpen)
        {
            return;
        }

        foreach (var sortColumn in Enum.GetValues<VendorSortColumn>())
        {
            if (M3Menu.Item($"##vendorSort{sortColumn}", sortColumn.ToString(), selected: _vendorSortColumn == sortColumn))
            {
                _vendorSortColumn = sortColumn;
                _vendorFilterDirty = true;
            }
        }
    }

    private float GetVendorSortControlWidth()
        => M3Widgets.ButtonWidth(FontAwesomeIcon.Sort, GetVendorSortLabel()) + M3.Space1 + M3Widgets.IconButtonSize;

    private void DrawVendorCostCell(VendorDisplayRow row, float iconSize)
    {
        var rowStart = ImGui.GetCursorScreenPos();
        if (row.CurrencyIconId != 0)
        {
            M3Widgets.GameIcon(row.CurrencyIconId, iconSize);
            ImGui.SameLine(0, M3.Space1);
        }

        ImGui.SetCursorScreenPos(new Vector2(ImGui.GetCursorScreenPos().X, rowStart.Y + (iconSize - ImGui.GetTextLineHeight()) * 0.5f));
        ImGui.TextUnformatted(row.CostText);
        M3Tooltip.Hovered(GetVendorCurrencyName(row.Entry.CurrencyItemId, row.Entry.CurrencyName));
    }

    private void DrawVendorItemTable()
    {
        var locationCacheReady = VendorNpcLocationCache.IsInitialized;
        if (_vendorDisplayBuiltWithResolvedLocations != locationCacheReady)
        {
            GatherBuddy.Log.Debug($"[VulcanWindow] Rebuilding vendor display after location cache state change ({_vendorDisplayBuiltWithResolvedLocations} -> {locationCacheReady})");
        }

        if (_vendorFilterDirty || _vendorDisplayBuiltWithResolvedLocations != locationCacheReady)
        {
            RebuildVendorDisplay();
        }

        if (DrawSearchInputWithInlineClear("##vendorSearch", "Search items", ref _vendorSearch, 256))
        {
            _vendorFilterDirty = true;
        }

        ImGui.Dummy(new Vector2(0, M3.Space1));
        var buyListManager = GatherBuddy.VendorBuyListManager;
        var purchaseManager = GatherBuddy.VendorPurchaseManager;
        if (M3Widgets.Button("##openBuyList", "Open buy list", M3ButtonStyle.Tonal, FontAwesomeIcon.ShoppingCart))
        {
            VendorBuyListManager.OpenWindow();
        }

        ImGui.SameLine(0, M3.Space2);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (M3Widgets.ButtonHeight - M3Badge.Size("0").Y) * 0.5f);
        M3Badge.Draw(VendorBuyListManager.Entries.Count == 1 ? $"{VendorBuyListManager.ActiveListName}: 1 item" : $"{VendorBuyListManager.ActiveListName}: {VendorBuyListManager.Entries.Count} items",
            M3.Scheme.Tertiary, "The active vendor buy list");

        if (!string.IsNullOrWhiteSpace(buyListManager.StatusText))
        {
            M3Text.Draw($"Buy list: {buyListManager.StatusText}", M3TextStyle.Warning, true);
        }

        if (purchaseManager.IsRunning)
        {
            M3Text.Draw(purchaseManager.StatusText, M3TextStyle.Info, true);
        }

        if (!VendorAutomationRequirements.IsAvailable)
        {
            M3Widgets.Banner("##vendorAutomation", VendorAutomationRequirements.UnavailableStatusText, M3Severity.Warning, FontAwesomeIcon.Robot,
                tooltip: VendorAutomationRequirements.UnavailableHelpText);
            M3Widgets.PluginPill("##vendorPlugins", RequiredVendorAutomationPlugins, any: true);
            M3Text.Draw(VendorAutomationRequirements.UnavailableHelpText, M3TextStyle.Muted, true);
        }

        if (_vendorCategory == VendorShopType.GrandCompanySeals && GetCurrentGrandCompanyEntryCount() == 0)
        {
            M3Widgets.LinearProgressIndeterminate(new Vector2(ImGui.GetContentRegionAvail().X, 4f * M3.Scale));
            M3Text.Draw("Loading Grand Company seal data...", M3TextStyle.Muted);
            return;
        }

        ImGui.Dummy(new Vector2(0, M3.Space1));
        var overflow = _vendorDisplay.Count > 500;
        var rowTop = ImGui.GetCursorScreenPos().Y;
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (M3Widgets.ButtonHeight - ImGui.GetTextLineHeight()) * 0.5f);
        M3Text.Draw(overflow ? $"Showing 500 of {_vendorDisplay.Count}, refine your search" : $"{_vendorDisplay.Count} results", M3TextStyle.Muted);
        ImGui.SameLine(MathF.Max(ImGui.GetCursorPosX(), ImGui.GetWindowContentRegionMax().X - GetVendorSortControlWidth()));
        ImGui.SetCursorScreenPos(new Vector2(ImGui.GetCursorScreenPos().X, rowTop));
        DrawVendorSortControl();

        var showAutomationControls = _vendorCategory is VendorShopType.GilShop or VendorShopType.SpecialCurrency or VendorShopType.GrandCompanySeals;
        string[] headers = showAutomationControls
            ? ["Item", "Cost", "Quantity", "Vendor", "Location", "##flag", "##list", "##go"]
            : ["Item", "Cost", "Vendor", "Location", "##flag", "##go"];
        using var style = M3Table.PushStyle();
        using var table = ImRaii.Table("##vendorTable", headers.Length,
            ImGuiTableFlags.BordersOuterH | ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.PadOuterX,
            new Vector2(-1, -1));
        if (!table)
        {
            return;
        }

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Cost", ImGuiTableColumnFlags.WidthFixed, VulcanUiScaling.Scaled(110f));
        if (showAutomationControls)
        {
            ImGui.TableSetupColumn("Quantity", ImGuiTableColumnFlags.WidthFixed, GetVendorQuantityInputWidth());
        }

        ImGui.TableSetupColumn("Vendor", ImGuiTableColumnFlags.WidthFixed, VulcanUiScaling.Scaled(180f));
        ImGui.TableSetupColumn("Location", ImGuiTableColumnFlags.WidthFixed, VulcanUiScaling.Scaled(170f));
        ImGui.TableSetupColumn("##flag", ImGuiTableColumnFlags.WidthFixed, ImGui.GetFrameHeight());
        if (showAutomationControls)
        {
            ImGui.TableSetupColumn("##list", ImGuiTableColumnFlags.WidthFixed, ImGui.GetFrameHeight());
        }

        ImGui.TableSetupColumn("##go", ImGuiTableColumnFlags.WidthFixed, ImGui.GetFrameHeight());
        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        foreach (var header in headers)
        {
            ImGui.TableNextColumn();
            if (!header.StartsWith("##"))
            {
                M3Table.Header(header, center: false);
            }
        }

        var iconSize = VulcanUiScaling.Scaled(20f);
        var limit = overflow ? 500 : _vendorDisplay.Count;
        var clipper = ImGui.ImGuiListClipper();
        clipper.Begin(limit);
        while (clipper.Step())
        {
            for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                DrawVendorTableRow(_vendorDisplay[i], showAutomationControls, iconSize);
            }
        }

        clipper.End();
        clipper.Destroy();
    }

    private void DrawVendorTableRow(VendorDisplayRow row, bool showGilControls, float iconSize)
    {
        var entry = row.Entry;
        var selectedNpc = GetSelectedVendorOption(row);

        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        var rowStart = ImGui.GetCursorScreenPos();
        if (row.Icon.TryGetWrap(out var wrap, out _))
        {
            Ui.HoverIcon(wrap, new Vector2(iconSize));
        }
        else
        {
            ImGui.Dummy(new Vector2(iconSize));
        }

        ImGui.SameLine(0, M3.Space1);
        ImGui.SetCursorScreenPos(new Vector2(ImGui.GetCursorScreenPos().X, rowStart.Y + (iconSize - ImGui.GetTextLineHeight()) * 0.5f));
        ImGui.TextUnformatted(entry.ItemName);

        ImGui.TableNextColumn();
        DrawVendorCostCell(row, iconSize);
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
        var quantity = GetVendorPurchaseQuantity(row.Entry);
        using var id = ImRaii.PushId(row.IdSuffix);
        if (M3Widgets.NumberField("##vendorQty", ref quantity, GetVendorQuantityInputWidth(), 1, 1, 99999))
        {
            SetVendorPurchaseQuantity(row.Entry, quantity);
        }
    }

    private static void DrawVendorNpcCell(VendorDisplayRow row, VendorDisplayNpcOption? selectedNpc)
    {
        if (row.NpcOptions.Count == 0)
        {
            M3Text.Draw(row.FallbackVendorName, M3TextStyle.Faint);
            return;
        }

        if (row.NpcOptions.Count == 1)
        {
            ImGui.TextUnformatted(row.NpcOptions[0].Npc.Name);
            return;
        }

        var options = row.NpcOptions;
        var index = selectedNpc == null ? 0 : options.IndexOf(o => VendorPreferenceHelper.MatchesVendor(o.Npc, selectedNpc.Npc));
        if (M3Widgets.Combo($"##vnpc_{row.IdSuffix}", ref index, options.Count, i => GetVendorNpcDisplayLabel(row, options[i]),
                ImGui.GetContentRegionAvail().X, tooltip: $"{options.Count} vendors sell this. Pick the one to use."))
        {
            SetVendorNpcPref(row.Entry, options[index].Npc);
        }
    }

    private static void DrawVendorAddToListMenu(VendorShopEntry entry, VendorNpc vendor, uint targetQuantity)
    {
        var buyListManager = GatherBuddy.VendorBuyListManager;
        if (buyListManager == null)
        {
            return;
        }

        M3Menu.Heading(entry.ItemName);
        if (M3Menu.Item("##newList", "New buy list...", FontAwesomeIcon.Plus))
        {
            var vendorBuyListWindow = GatherBuddy.VendorBuyListWindow;
            if (vendorBuyListWindow == null)
            {
                GatherBuddy.Log.Warning($"[VulcanWindow] Unable to open Create Vendor List popup for {entry.ItemName}: vendor buy list window unavailable.");
            }
            else if (!vendorBuyListWindow.OpenCreateListPopup(entry, vendor, targetQuantity))
            {
                GatherBuddy.Log.Debug($"[VulcanWindow] Unable to create a new vendor buy list for {entry.ItemName} with target {targetQuantity:N0}.");
            }
        }

        using var sub = M3Menu.BeginSub("##existing", "Add to a buy list", FontAwesomeIcon.ListUl, VendorBuyListManager.Lists.Count > 0);
        if (!sub.IsOpen)
        {
            return;
        }

        var index = 0;
        foreach (var list in VendorBuyListManager.GetListsByCreation(true))
        {
            if (M3Menu.Item($"##buyList{index++}", list.Name)
             && !buyListManager.TryAddTarget(list.Id, entry, vendor, targetQuantity, selectList: true, openWindow: true, announce: true))
            {
                GatherBuddy.Log.Debug($"[VulcanWindow] Unable to add {entry.ItemName} to vendor list '{list.Name}' with target {targetQuantity:N0}.");
            }
        }
    }

    private void DrawVendorAddToListButton(VendorDisplayRow row, VendorDisplayNpcOption? selectedNpc)
    {
        if (selectedNpc == null)
        {
            DrawVendorIconButton($"vendor_add_disabled_{row.IdSuffix}", FontAwesomeIcon.Plus, VendorBuyListButtonColor, "No vendor available for the buy list", true);
            return;
        }

        if (!VendorPurchaseManager.IsPurchaseSupported(row.Entry, selectedNpc.Npc))
        {
            DrawVendorIconButton($"vendor_add_disabled_{row.IdSuffix}", FontAwesomeIcon.Plus, VendorBuyListButtonColor,
                "Automation is not available for the selected vendor route", true);
            return;
        }

        var buyListManager = GatherBuddy.VendorBuyListManager;
        if (buyListManager == null)
        {
            DrawVendorIconButton($"vendor_add_disabled_{row.IdSuffix}", FontAwesomeIcon.Plus, VendorBuyListButtonColor, "Vendor buy list manager unavailable", true);
            return;
        }

        var targetQuantity = (uint)System.Math.Max(1, GetVendorPurchaseQuantity(row.Entry));
        if (DrawVendorIconButton($"vendor_add_{row.IdSuffix}", FontAwesomeIcon.Plus, VendorBuyListButtonColor,
                "Add to the active buy list. Right-click for more options.")
         && !buyListManager.TryAddTarget(row.Entry, selectedNpc.Npc, targetQuantity, openWindow: false, announce: false))
        {
            GatherBuddy.Log.Debug($"[VulcanWindow] Unable to add {row.Entry.ItemName} to active vendor list '{VendorBuyListManager.ActiveListName}' with target {targetQuantity:N0}.");
        }

        using var menu = M3Menu.BeginContext($"##vendorAddToListPopup_{row.IdSuffix}");
        if (menu.IsOpen)
        {
            DrawVendorAddToListMenu(row.Entry, selectedNpc.Npc, targetQuantity);
        }
    }

    private void DrawVendorGoButton(VendorDisplayRow row, VendorDisplayNpcOption? selectedNpc)
    {
        var location = selectedNpc?.Location;
        if (selectedNpc == null || location == null)
        {
            DrawVendorIconButton($"vendor_go_disabled_{row.IdSuffix}", FontAwesomeIcon.ShoppingCart, VendorAutomationButtonColor, "No location data available", true);
            return;
        }

        var entry = row.Entry;
        var canPurchaseHere = VendorPurchaseManager.IsPurchaseSupported(entry, selectedNpc.Npc);
        if (canPurchaseHere && !VendorAutomationRequirements.IsAvailable)
        {
            DrawVendorIconButton($"vendor_go_disabled_{row.IdSuffix}", FontAwesomeIcon.ShoppingCart, VendorAutomationButtonColor,
                VendorAutomationRequirements.UnavailableHelpText, true);
            return;
        }

        var requestedQuantity = canPurchaseHere ? GetVendorPurchaseQuantity(entry) : 1;
        var navigator = GatherBuddy.VendorNavigator;
        var purchaseManager = GatherBuddy.VendorPurchaseManager;
        var isPurchaseActive = purchaseManager.IsRunningFor(entry, selectedNpc.Npc);
        var isActive = isPurchaseActive || navigator.IsActive && navigator.CurrentTarget?.NpcId == selectedNpc.Npc.NpcId;
        if (isActive)
        {
            if (!DrawVendorIconButton($"vendor_go_active_{row.IdSuffix}", FontAwesomeIcon.Stop, M3.Scheme.Warning, isPurchaseActive
                    ? $"{purchaseManager.StatusText}. Click to cancel."
                    : $"Going to {selectedNpc.Npc.Name}. Click to cancel."))
            {
                return;
            }

            if (isPurchaseActive)
            {
                purchaseManager.Stop();
            }
            else
            {
                navigator.Stop();
            }

            return;
        }

        if (!DrawVendorIconButton($"vendor_go_{row.IdSuffix}", FontAwesomeIcon.ShoppingCart, VendorAutomationButtonColor, canPurchaseHere
                ? $"Go to {selectedNpc.Npc.Name} and buy {requestedQuantity:N0}× {entry.ItemName}"
                : $"Go to {selectedNpc.Npc.Name}"))
        {
            return;
        }

        if (canPurchaseHere)
        {
            purchaseManager.StartPurchase(entry, selectedNpc.Npc, location, (uint)requestedQuantity);
        }
        else
        {
            navigator.StartNavigation(location);
        }
    }

    private void RebuildVendorDisplay()
    {
        _vendorFilterDirty = false;
        var locationCacheReady = VendorNpcLocationCache.IsInitialized;
        var selectedCurrencyItemId = _vendorSelectedCurrencyItemId;
        var search = string.IsNullOrWhiteSpace(_vendorSearch) ? null : _vendorSearch;
        var display = new List<VendorDisplayRow>();
        foreach (var entry in GetVendorBaseEntries())
        {
            if (selectedCurrencyItemId.HasValue && entry.CurrencyItemId != selectedCurrencyItemId.Value
             || search != null && !entry.ItemName.Contains(search, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            display.Add(BuildVendorDisplayRow(entry, locationCacheReady));
        }

        _vendorDisplay = display;
        SortVendorDisplayRows(_vendorDisplay);
        _vendorDisplayBuiltWithResolvedLocations = locationCacheReady;
        _vendorDisplayShopRevision = VendorShopResolver.Revision;
        _vendorDisplayLocationRevision = VendorNpcLocationCache.Revision;
    }

    private void InvalidateVendorDisplayForCacheRevisions()
    {
        if (_vendorDisplayShopRevision != VendorShopResolver.Revision)
        {
            _vendorGroupCounts = null;
            _vendorGilCounts = null;
            _vendorFilterDirty = true;
        }

        if (_vendorDisplayLocationRevision != VendorNpcLocationCache.Revision)
        {
            _vendorFilterDirty = true;
        }
    }
}
