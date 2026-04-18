using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using GatherBuddy.Plugin;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace GatherBuddy.Vulcan.Vendors;

public static class VendorShopResolver
{
    private readonly record struct ResolvedSpecialShopCost(uint CurrencyItemId, uint Amount, string CurrencyName, VendorCurrencyGroup Group, int OriginalIndex);
    private readonly record struct InclusionShopRoute(uint InclusionShopId, int PageIndex, int SubPageIndex);
    public const            uint          GilCurrencyItemId        = 1;
    public const            uint          AlliedSealCurrencyItemId = 27;
    public const            uint          CenturioSealCurrencyItemId = 10307;
    public const            uint          SackOfNutsCurrencyItemId = 26533;
    public const            uint          BicolorCurrencyItemId    = 26807;
    public const            uint          MgpCurrencyItemId        = 29;
    public const            uint          WolfMarkCurrencyItemId   = 25;
    private static readonly HashSet<uint> TomestoneIds   = new() { 28, 48, 49 };                 // Poetics, Mathematics, Mnemonics
    private static readonly HashSet<uint> HuntSealIds    = new() { AlliedSealCurrencyItemId, CenturioSealCurrencyItemId, SackOfNutsCurrencyItemId };
    private static readonly HashSet<uint> ScripIds       = new() { 33913, 33914, 41784, 41785 }; // Purple/Orange Crafter/Gatherer

    // Sourced from AllaganLib SpecialShopListing.currencies
    private static readonly Dictionary<uint, uint> CurrencyTypeMap = new()
    {
        { 1,  10309 }, { 2,  33913 }, { 3,  10311 }, { 4,  33914 }, { 5,  10307 },
        { 6,  41784 }, { 7,  41785 }, { 8,  21072 }, { 9,  21073 }, { 10, 21074 },
        { 11, 21075 }, { 12, 21076 }, { 13, 21077 }, { 14, 21078 }, { 15, 21079 },
        { 16, 21080 }, { 17, 21081 }, { 18, 21172 }, { 19, 21173 }, { 20, 21935 },
        { 21, 22525 }, { 22, 26533 }, { 23, 26807 }, { 24, 28063 }, { 25, 28186 },
        { 26, 28187 }, { 27, 28188 }, { 28, 30341 },
    };

    // Sourced from AllaganLib NpcShopCache (gaps not covered by ENpcBase.ENpcData)
    private static readonly (uint NpcId, uint ShopId)[] ExtraSpecialShops =
    [
        (1027998, 1769957), (1027538, 1769958), (1027385, 1769959), (1027497, 1769960),
        (1027892, 1769961), (1027665, 1769962), (1027709, 1769963), (1027766, 1769964),
        (1018655, 1769743), (1018655, 1769744), (1018655, 1770537), (1016289, 1769635),
        (1025047, 1769820), (1025047, 1769821), (1025047, 1769822), (1025047, 1769823),
        (1025047, 1769824), (1025047, 1769825), (1025047, 1769826), (1025047, 1769827),
        (1025047, 1769828), (1025047, 1769829), (1025047, 1769830), (1025047, 1769831),
        (1025047, 1769832), (1025047, 1769833), (1025047, 1769834), (1027123, 1769934),
        (1027123, 1769935), (1033921, 1770282), (1036895, 1770087), (1034007, 1770087),
    ];

    private static readonly (uint NpcId, uint ShopId)[] ExtraGilShops =
    [
        (1025763, 262919),
    ];
    private static volatile bool _initialized;
    private static volatile bool _initializing;
    private static volatile bool _lastBuildHadCompleteDataShare;
    private static readonly TimeSpan RetryCooldown = TimeSpan.FromSeconds(2);
    private static DateTime _lastInitializeAttemptUtc = DateTime.MinValue;

    private static List<VendorShopEntry> _gilShopEntries     = new();
    private static List<VendorShopEntry> _specialShopEntries = new();
    private static List<VendorShopEntry> _gcShopEntries      = new();
    private static HashSet<uint>         _allVendorNpcIds    = new();

    private static HashSet<uint> _gatherableIds = new();
    private static HashSet<uint> _fishIds       = new();
    private static HashSet<uint> _craftableIds  = new();
    private static HashSet<uint> _housingItemIds = new();
    private static HashSet<uint> _dyeItemIds     = new();

    public static bool IsInitialized  => _initialized;
    public static bool IsInitializing => _initializing;

    public static IReadOnlyList<VendorShopEntry> GilShopEntries     => _gilShopEntries;
    public static IReadOnlyList<VendorShopEntry> SpecialShopEntries => _specialShopEntries;
    public static IReadOnlyList<VendorShopEntry> GcShopEntries      => _gcShopEntries;
    public static IReadOnlySet<uint>             AllVendorNpcIds    => _allVendorNpcIds;

    public static IReadOnlySet<uint> GatherableIds => _gatherableIds;
    public static IReadOnlySet<uint> FishIds       => _fishIds;
    public static IReadOnlySet<uint> CraftableIds  => _craftableIds;
    public static IReadOnlySet<uint> HousingItemIds => _housingItemIds;
    public static IReadOnlySet<uint> DyeItemIds     => _dyeItemIds;

    public static HashSet<uint> GetAllVendorNpcIds()
        => new(_allVendorNpcIds);

    public static void InitializeAsync()
    {
        if (_initializing) return;
        if (_initialized && _lastBuildHadCompleteDataShare) return;
        if ((DateTime.UtcNow - _lastInitializeAttemptUtc) < RetryCooldown) return;
        _lastInitializeAttemptUtc = DateTime.UtcNow;
        var (gilMap, specialMap, gcMap, inclusionMap) = GetShopNpcMapsFromDataShare();
        var hasCompleteDataShare = HasCompleteShopDataShare(gilMap, specialMap, gcMap, inclusionMap);
        var shouldRefreshForDataShare = _initialized
            && !_lastBuildHadCompleteDataShare
            && hasCompleteDataShare;
        if (_initialized && !shouldRefreshForDataShare) return;

        if (shouldRefreshForDataShare)
        {
            GatherBuddy.Log.Debug($"[VendorShopResolver] Full AllaganTools DataShare became available after an early fallback build, rebuilding vendor shop cache ({AllVendorNpcIds.Count} NPCs; {DescribeShopDataShareAvailability(gilMap, specialMap, gcMap, inclusionMap)})");
            _initialized = false;
        }
        _initializing = true;
        Task.Run(Initialize);
    }

    private static void Initialize()
    {
        var success = false;
        try
        {
            var npcNames = BuildNpcNameLookup();
            if (npcNames.Count == 0)
            {
                GatherBuddy.Log.Warning("[VendorShopResolver] ENpcResident lookup is empty; deferring vendor cache initialization");
                return;
            }

            var (gilMap, specialMap, gcMap, inclusionMap) = GetShopNpcMapsFromDataShare();
            var hasCompleteDataShare = HasCompleteShopDataShare(gilMap, specialMap, gcMap, inclusionMap);
            var hasAnyDataShare      = HasAnyShopDataShare(gilMap, specialMap, gcMap, inclusionMap);
            var dataShareAvailability = DescribeShopDataShareAvailability(gilMap, specialMap, gcMap, inclusionMap);

            GatherBuddy.Log.Debug($"[VendorShopResolver] AllaganTools DataShare: {(hasCompleteDataShare ? "ready" : hasAnyDataShare ? "partial" : "not available")} ({dataShareAvailability})");

            if (!hasCompleteDataShare)
            {
                var (lGil, lSpecial, lGc, lInclusion) = BuildNpcShopMapsFromLumina();
                gilMap       = UseFallbackIfUnavailable(gilMap, lGil);
                specialMap   = UseFallbackIfUnavailable(specialMap, lSpecial);
                gcMap        = UseFallbackIfUnavailable(gcMap, lGc);
                inclusionMap = UseFallbackIfUnavailable(inclusionMap, lInclusion);
                GatherBuddy.Log.Debug($"[VendorShopResolver] Using Lumina fallback for missing shop maps ({dataShareAvailability})");
            }

            var directSpecialMap = CloneNpcMap(specialMap);
            var inclusionRoutes  = BuildSpecialShopInclusionRoutes();
            AugmentSpecialMapFromInclusion(specialMap, inclusionMap);

            var tomestoneLookup = BuildTomestoneLookup();
            BuildCraftingRelevantIds();

            var gilEntries     = BuildGilShopEntries(gilMap, npcNames);
            var specialEntries = BuildSpecialShopEntries(specialMap, directSpecialMap, inclusionMap, inclusionRoutes, npcNames, tomestoneLookup);
            var gcEntries      = BuildGcShopEntries(gcMap, npcNames);

            if (gilEntries.Count == 0 && specialEntries.Count == 0 && gcEntries.Count == 0)
            {
                GatherBuddy.Log.Warning("[VendorShopResolver] No vendor shop entries were built; deferring vendor cache initialization");
                return;
            }

            _gilShopEntries     = gilEntries;
            _specialShopEntries = specialEntries;
            _gcShopEntries      = gcEntries;
            _lastBuildHadCompleteDataShare = hasCompleteDataShare;

            GatherBuddy.Log.Debug($"[VendorShopResolver] {_gilShopEntries.Count} gil, {_specialShopEntries.Count} special, {_gcShopEntries.Count} GC");

            _allVendorNpcIds = BuildVendorNpcIdSet(_gilShopEntries, _specialShopEntries, _gcShopEntries);
            VendorNpcLocationCache.InitializeAsync(_allVendorNpcIds);
            success = true;
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Warning($"[VendorShopResolver] Init failed: {ex.Message}");
        }
        finally
        {
            _initialized  = success;
            _initializing = false;
            if (!success)
                GatherBuddy.Log.Debug("[VendorShopResolver] Vendor cache is still uninitialized and will retry when requested");
        }
    }

    private static HashSet<uint> BuildVendorNpcIdSet(
        IEnumerable<VendorShopEntry> gilEntries,
        IEnumerable<VendorShopEntry> specialEntries,
        IEnumerable<VendorShopEntry> gcEntries)
        => new(
            gilEntries    .SelectMany(entry => entry.Npcs.Select(npc => npc.NpcId))
            .Concat(specialEntries.SelectMany(entry => entry.Npcs.Select(npc => npc.NpcId)))
            .Concat(gcEntries     .SelectMany(entry => entry.Npcs.Select(npc => npc.NpcId)))
            .Where(npcId => npcId != 0));

    private static Dictionary<uint, HashSet<uint>>? TryGetShopNpcMap(string lookupName)
    {
        Dalamud.PluginInterface.TryGetData<Dictionary<uint, HashSet<uint>>>(
            $"AllaganLib.Data.NpcShopCache.{lookupName}.1", out var data);
        return data;
    }

    private static (
        Dictionary<uint, HashSet<uint>>? GilMap,
        Dictionary<uint, HashSet<uint>>? SpecialMap,
        Dictionary<uint, HashSet<uint>>? GcMap,
        Dictionary<uint, HashSet<uint>>? InclusionMap)
        GetShopNpcMapsFromDataShare()
        => (
            TryGetShopNpcMap("GilShopIdToNpcIdLookup"),
            TryGetShopNpcMap("SpecialShopIdToNpcIdLookup"),
            TryGetShopNpcMap("GcShopIdToNpcIdLookup"),
            TryGetShopNpcMap("InclusionShopIdToNpcLookup"));

    private static bool HasCompleteShopDataShare(
        Dictionary<uint, HashSet<uint>>? gilMap,
        Dictionary<uint, HashSet<uint>>? specialMap,
        Dictionary<uint, HashSet<uint>>? gcMap,
        Dictionary<uint, HashSet<uint>>? inclusionMap)
        => HasShopNpcMapData(gilMap)
        && HasShopNpcMapData(specialMap)
        && HasShopNpcMapData(gcMap)
        && HasShopNpcMapData(inclusionMap);

    private static bool HasAnyShopDataShare(
        Dictionary<uint, HashSet<uint>>? gilMap,
        Dictionary<uint, HashSet<uint>>? specialMap,
        Dictionary<uint, HashSet<uint>>? gcMap,
        Dictionary<uint, HashSet<uint>>? inclusionMap)
        => HasShopNpcMapData(gilMap)
        || HasShopNpcMapData(specialMap)
        || HasShopNpcMapData(gcMap)
        || HasShopNpcMapData(inclusionMap);

    private static string DescribeShopDataShareAvailability(
        Dictionary<uint, HashSet<uint>>? gilMap,
        Dictionary<uint, HashSet<uint>>? specialMap,
        Dictionary<uint, HashSet<uint>>? gcMap,
        Dictionary<uint, HashSet<uint>>? inclusionMap)
        => $"gil={(HasShopNpcMapData(gilMap) ? "ready" : "missing")}, special={(HasShopNpcMapData(specialMap) ? "ready" : "missing")}, gc={(HasShopNpcMapData(gcMap) ? "ready" : "missing")}, inclusion={(HasShopNpcMapData(inclusionMap) ? "ready" : "missing")}";

    private static Dictionary<uint, HashSet<uint>> UseFallbackIfUnavailable(
        Dictionary<uint, HashSet<uint>>? preferredMap,
        Dictionary<uint, HashSet<uint>> fallbackMap)
        => HasShopNpcMapData(preferredMap) ? preferredMap! : fallbackMap;

    private static bool HasShopNpcMapData(Dictionary<uint, HashSet<uint>>? map)
        => map is { Count: > 0 };

    private static Dictionary<uint, HashSet<uint>> CloneNpcMap(Dictionary<uint, HashSet<uint>> source)
        => source.ToDictionary(entry => entry.Key, entry => new HashSet<uint>(entry.Value));

    private static void BuildCraftingRelevantIds()
    {
        try
        {
            _gatherableIds = new HashSet<uint>(GatherBuddy.GameData.Gatherables.Keys);
            _fishIds       = new HashSet<uint>(GatherBuddy.GameData.Fishes.Keys);

            _craftableIds = new HashSet<uint>();
            _housingItemIds = new HashSet<uint>();
            _dyeItemIds = new HashSet<uint>();
            var recipeSheet = Dalamud.GameData.GetExcelSheet<Recipe>();
            if (recipeSheet != null)
                foreach (var recipe in recipeSheet)
                    if (recipe.ItemResult.RowId > 0)
                        _craftableIds.Add(recipe.ItemResult.RowId);

            var itemSheet = Dalamud.GameData.GetExcelSheet<Item>();
            if (itemSheet != null)
                foreach (var item in itemSheet)
                {
                    if (IsHousingItem(item))
                        _housingItemIds.Add(item.RowId);
                    if (IsDyeItem(item))
                        _dyeItemIds.Add(item.RowId);
                }
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Warning($"[VendorShopResolver] BuildCraftingRelevantIds failed: {ex.Message}");
        }
    }

    private static bool IsHousingItem(Item item)
        => item.ItemSearchCategory.RowId is 56 or >= 65 and <= 72;

    private static bool IsDyeItem(Item item)
        => item.ItemSearchCategory.RowId == 54;

    private static Dictionary<uint, uint> BuildTomestoneLookup()
    {
        var lookup = new Dictionary<uint, uint>();
        var sheet  = Dalamud.GameData.GetExcelSheet<TomestonesItem>();
        if (sheet == null) return lookup;
        foreach (var row in sheet)
            if (row.Tomestones.RowId > 0 && row.CurrencyInventorySlot > 0)
                lookup.TryAdd(row.Tomestones.RowId, row.Item.RowId);
        return lookup;
    }

    private static HashSet<uint> BuildTomestoneItemIdSet(Dictionary<uint, uint> tomestoneLookup)
        => tomestoneLookup.Values.Concat(TomestoneIds).ToHashSet();

    // Mirrors AllaganLib SpecialShopListing.ConvertCurrencyId
    private static uint ConvertCurrencyId(uint shopId, uint rawId, ushort useCurrencyType, Dictionary<uint, uint> tomes)
    {
        if (shopId == 1770637)
        {
            if (CurrencyTypeMap.TryGetValue(rawId, out var v)) return v;
        }
        if (shopId == 1770446 || (shopId == 1770699 && rawId < 10) || (shopId == 1770803 && rawId < 10))
        {
            if (tomes.TryGetValue(rawId, out var v) || CurrencyTypeMap.TryGetValue(rawId, out v)) return v;
        }
        if (useCurrencyType == 16 && rawId != 25 && CurrencyTypeMap.TryGetValue(rawId, out var r16)) rawId = r16;
        if (useCurrencyType == 2  && rawId < 10  && tomes.TryGetValue(rawId, out var r2))             rawId = r2;
        if (shopId == 1770637 && rawId < 10 && CurrencyTypeMap.TryGetValue(rawId, out var r637))      rawId = r637;
        if ((useCurrencyType == 16 || useCurrencyType == 4) && rawId < 10 && shopId != 1770637)
            if (tomes.TryGetValue(rawId, out var v) || CurrencyTypeMap.TryGetValue(rawId, out v)) rawId = v;
        return rawId;
    }

    private static Dictionary<uint, string> BuildNpcNameLookup()
    {
        var lookup = new Dictionary<uint, string>();
        var sheet  = Dalamud.GameData.GetExcelSheet<ENpcResident>();
        if (sheet == null) return lookup;
        foreach (var npc in sheet)
        {
            var name = npc.Singular.ExtractText();
            if (!string.IsNullOrWhiteSpace(name))
                lookup[npc.RowId] = name;
        }
        return lookup;
    }

    private static (Dictionary<uint, HashSet<uint>> gil, Dictionary<uint, HashSet<uint>> special, Dictionary<uint, HashSet<uint>> gc, Dictionary<uint, HashSet<uint>> inclusion)
        BuildNpcShopMapsFromLumina()
    {
        var gilMap       = new Dictionary<uint, HashSet<uint>>();
        var specialMap   = new Dictionary<uint, HashSet<uint>>();
        var gcMap        = new Dictionary<uint, HashSet<uint>>();
        var inclusionMap = new Dictionary<uint, HashSet<uint>>();

        var eNpcBaseSheet = Dalamud.GameData.GetExcelSheet<ENpcBase>();
        if (eNpcBaseSheet == null) return (gilMap, specialMap, gcMap, inclusionMap);

        var gilShopIds       = new HashSet<uint>(Dalamud.GameData.GetExcelSheet<GilShop>()?.Select(r => r.RowId)       ?? Enumerable.Empty<uint>());
        var specialShopIds   = new HashSet<uint>(Dalamud.GameData.GetExcelSheet<SpecialShop>()?.Select(r => r.RowId)   ?? Enumerable.Empty<uint>());
        var gcShopIds        = new HashSet<uint>(Dalamud.GameData.GetExcelSheet<GCShop>()?.Select(r => r.RowId)        ?? Enumerable.Empty<uint>());
        var inclusionShopIds = new HashSet<uint>(Dalamud.GameData.GetExcelSheet<InclusionShop>()?.Select(r => r.RowId) ?? Enumerable.Empty<uint>());
        var fateShopSheet    = Dalamud.GameData.GetExcelSheet<FateShop>();

        foreach (var (npcId, shopId) in ExtraSpecialShops)
            AddToMap(specialMap, shopId, npcId);
        foreach (var (npcId, shopId) in ExtraGilShops)
            AddToMap(gilMap, shopId, npcId);

        foreach (var npc in eNpcBaseSheet)
        {
            if (fateShopSheet != null && fateShopSheet.TryGetRow(npc.RowId, out var fateShop))
                foreach (var ss in fateShop.SpecialShop)
                    if (ss.RowId > 0) AddToMap(specialMap, ss.RowId, npc.RowId);

            foreach (var dataEntry in npc.ENpcData)
            {
                var id = dataEntry.RowId;
                if (id == 0) continue;
                if (gilShopIds.Contains(id))           AddToMap(gilMap,       id, npc.RowId);
                else if (specialShopIds.Contains(id))  AddToMap(specialMap,   id, npc.RowId);
                else if (gcShopIds.Contains(id))       AddToMap(gcMap,        id, npc.RowId);
                else if (inclusionShopIds.Contains(id))AddToMap(inclusionMap, id, npc.RowId);
            }
        }
        return (gilMap, specialMap, gcMap, inclusionMap);
    }

    private static void AugmentSpecialMapFromInclusion(
        Dictionary<uint, HashSet<uint>> specialMap,
        Dictionary<uint, HashSet<uint>> inclusionMap)
    {
        var specialToInclusion = BuildSpecialShopToInclusionShopMap();
        foreach (var (specialId, inclusionIds) in specialToInclusion)
        {
            foreach (var inclusionId in inclusionIds)
            {
                if (!inclusionMap.TryGetValue(inclusionId, out var npcIds)) continue;
                foreach (var npcId in npcIds)
                    AddToMap(specialMap, specialId, npcId);
            }
        }
    }

    private static Dictionary<uint, HashSet<uint>> BuildSpecialShopToInclusionShopMap()
    {
        var result         = new Dictionary<uint, HashSet<uint>>();
        var inclusionSheet = Dalamud.GameData.GetExcelSheet<InclusionShop>();
        var catSheet       = Dalamud.GameData.GetExcelSheet<InclusionShopCategory>();
        var seriesSheet    = Dalamud.GameData.GetSubrowExcelSheet<InclusionShopSeries>();
        if (inclusionSheet == null || catSheet == null || seriesSheet == null) return result;
        var seriesToInclusion = new Dictionary<uint, HashSet<uint>>();
        foreach (var inclusionShop in inclusionSheet)
        {
            foreach (var catRef in inclusionShop.Category)
            {
                if (catRef.RowId == 0) continue;
                if (!catSheet.TryGetRow(catRef.RowId, out var cat)) continue;
                var seriesId = cat.InclusionShopSeries.RowId;
                if (seriesId == 0) continue;
                if (!seriesToInclusion.TryGetValue(seriesId, out var inclusionIds))
                    seriesToInclusion[seriesId] = inclusionIds = new HashSet<uint>();
                inclusionIds.Add(inclusionShop.RowId);
            }
        }

        foreach (var seriesRow in seriesSheet)
        {
            if (!seriesToInclusion.TryGetValue(seriesRow.RowId, out var inclusionIds)) continue;
            foreach (var subRow in seriesRow)
            {
                if (subRow.SpecialShop.RowId == 0) continue;
                if (!result.TryGetValue(subRow.SpecialShop.RowId, out var specialInclusionIds))
                    result[subRow.SpecialShop.RowId] = specialInclusionIds = new HashSet<uint>();
                foreach (var inclusionId in inclusionIds)
                    specialInclusionIds.Add(inclusionId);
            }
        }

        return result;
    }

    private static Dictionary<uint, List<InclusionShopRoute>> BuildSpecialShopInclusionRoutes()
    {
        var result         = new Dictionary<uint, List<InclusionShopRoute>>();
        var inclusionSheet = Dalamud.GameData.GetExcelSheet<InclusionShop>();
        var catSheet       = Dalamud.GameData.GetExcelSheet<InclusionShopCategory>();
        var seriesSheet    = Dalamud.GameData.GetSubrowExcelSheet<InclusionShopSeries>();
        if (inclusionSheet == null || catSheet == null || seriesSheet == null)
            return result;

        var seriesToInclusionPages = new Dictionary<uint, List<(uint InclusionShopId, int PageIndex)>>();
        foreach (var inclusionShop in inclusionSheet)
        {
            for (var pageIndex = 0; pageIndex < inclusionShop.Category.Count; pageIndex++)
            {
                var categoryRef = inclusionShop.Category[pageIndex];
                if (categoryRef.RowId == 0 || !catSheet.TryGetRow(categoryRef.RowId, out var category))
                    continue;

                var seriesId = category.InclusionShopSeries.RowId;
                if (seriesId == 0)
                    continue;

                if (!seriesToInclusionPages.TryGetValue(seriesId, out var inclusionPages))
                    seriesToInclusionPages[seriesId] = inclusionPages = [];
                inclusionPages.Add((inclusionShop.RowId, pageIndex));
            }
        }

        foreach (var seriesRow in seriesSheet)
        {
            if (!seriesToInclusionPages.TryGetValue(seriesRow.RowId, out var inclusionPages))
                continue;

            for (var subPageIndex = 0; subPageIndex < seriesRow.Count; subPageIndex++)
            {
                var subRow = seriesRow[subPageIndex];
                if (subRow.SpecialShop.RowId == 0)
                    continue;

                if (!result.TryGetValue(subRow.SpecialShop.RowId, out var routes))
                    result[subRow.SpecialShop.RowId] = routes = [];

                foreach (var (inclusionShopId, pageIndex) in inclusionPages)
                {
                    var route = new InclusionShopRoute(inclusionShopId, pageIndex, subPageIndex + 1);
                    if (!routes.Contains(route))
                        routes.Add(route);
                }

            }
        }
        return result;
    }

    private static bool AddToMap(Dictionary<uint, HashSet<uint>> map, uint shopId, uint npcId)
    {
        if (!map.TryGetValue(shopId, out var set))
            map[shopId] = set = new HashSet<uint>();
        return set.Add(npcId);
    }

    private static List<VendorNpc> ResolveNpcs(uint shopId, Dictionary<uint, HashSet<uint>> npcMap, Dictionary<uint, string> npcNames)
    {
        if (!npcMap.TryGetValue(shopId, out var npcIds)) return new List<VendorNpc>();
        var result = new List<VendorNpc>();
        foreach (var npcId in npcIds)
            if (npcNames.TryGetValue(npcId, out var name))
                result.Add(new VendorNpc(npcId, name, shopId));
        return result;
    }

    private static List<VendorNpc> ResolveSpecialShopNpcs(
        uint shopId,
        Dictionary<uint, HashSet<uint>> displayNpcMap,
        Dictionary<uint, HashSet<uint>> directSpecialMap,
        Dictionary<uint, HashSet<uint>> inclusionMap,
        Dictionary<uint, List<InclusionShopRoute>> inclusionRoutes,
        Dictionary<uint, string> npcNames)
    {
        var resultByNpcId = new Dictionary<uint, VendorNpc>();

        if (directSpecialMap.TryGetValue(shopId, out var directNpcIds))
        {
            foreach (var npcId in directNpcIds)
            {
                if (!npcNames.TryGetValue(npcId, out var name))
                    continue;

                AddSpecialShopVendor(resultByNpcId, new VendorNpc(npcId, name, shopId, VendorMenuShopType.SpecialShop));
            }
        }

        if (inclusionRoutes.TryGetValue(shopId, out var routes))
        {
            foreach (var route in routes.OrderBy(route => route.PageIndex).ThenBy(route => route.SubPageIndex).ThenBy(route => route.InclusionShopId))
            {
                if (!inclusionMap.TryGetValue(route.InclusionShopId, out var inclusionNpcIds))
                    continue;

                foreach (var npcId in inclusionNpcIds)
                {
                    if (!npcNames.TryGetValue(npcId, out var name))
                        continue;

                    AddSpecialShopVendor(resultByNpcId, new VendorNpc(
                        npcId,
                        name,
                        route.InclusionShopId,
                        VendorMenuShopType.InclusionShop,
                        route.PageIndex,
                        route.SubPageIndex));
                }
            }
        }

        if (resultByNpcId.Count == 0 && displayNpcMap.TryGetValue(shopId, out var fallbackNpcIds))
        {
            foreach (var npcId in fallbackNpcIds)
            {
                if (!npcNames.TryGetValue(npcId, out var name))
                    continue;

                AddSpecialShopVendor(resultByNpcId, new VendorNpc(npcId, name, shopId, VendorMenuShopType.SpecialShop));
            }
        }

        return resultByNpcId.Values
            .OrderBy(npc => npc.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(npc => npc.NpcId)
            .ToList();
    }

    private static void AddSpecialShopVendor(Dictionary<uint, VendorNpc> vendors, VendorNpc candidate)
    {
        if (!vendors.TryGetValue(candidate.NpcId, out var existing)
         || GetSpecialShopVendorPriority(candidate) < GetSpecialShopVendorPriority(existing))
            vendors[candidate.NpcId] = candidate;
    }

    private static int GetSpecialShopVendorPriority(VendorNpc npc)
        => npc.MenuShopType switch
        {
            VendorMenuShopType.InclusionShop => 0,
            VendorMenuShopType.SpecialShop   => 1,
            _                               => 2,
        };

    private static List<VendorShopEntry> DeduplicateEntries(List<VendorShopEntry> entries)
    {
        var indexByKey = new Dictionary<(VendorShopType ShopType, uint ItemId, uint Cost, uint CurrencyItemId), int>();
        var result     = new List<VendorShopEntry>();
        var npcIdSets  = new List<HashSet<(uint NpcId, VendorMenuShopType MenuShopType, uint ShopId, int InclusionPageIndex, int InclusionSubPageIndex, uint SourceShopId, int ShopItemIndex, int GcRankIndex, int GcCategoryIndex)>>();

        foreach (var entry in entries)
        {
            var key = (
                entry.ShopType,
                entry.ItemId,
                entry.Cost,
                entry.CurrencyItemId);
            if (!indexByKey.TryGetValue(key, out var idx))
            {
                idx = result.Count;
                indexByKey[key] = idx;
                result.Add(entry);
                npcIdSets.Add(new HashSet<(uint NpcId, VendorMenuShopType MenuShopType, uint ShopId, int InclusionPageIndex, int InclusionSubPageIndex, uint SourceShopId, int ShopItemIndex, int GcRankIndex, int GcCategoryIndex)>(
                    entry.Npcs.Select(n => (n.NpcId, n.MenuShopType, n.ShopId, n.InclusionPageIndex, n.InclusionSubPageIndex, n.SourceShopId, n.ShopItemIndex, n.GcRankIndex, n.GcCategoryIndex))));
            }
            else
            {
                foreach (var npc in entry.Npcs)
                    if (npcIdSets[idx].Add((npc.NpcId, npc.MenuShopType, npc.ShopId, npc.InclusionPageIndex, npc.InclusionSubPageIndex, npc.SourceShopId, npc.ShopItemIndex, npc.GcRankIndex, npc.GcCategoryIndex)))
                        result[idx].Npcs.Add(npc);
            }
        }
        return result;
    }

    private static List<VendorShopEntry> MergeEquivalentOtherEntriesIntoTomestones(List<VendorShopEntry> entries)
    {
        if (entries.Count == 0)
            return entries;

        var mergedEntries = new List<VendorShopEntry>();
        var mergedRowCount = 0;
        var mergedNpcCount = 0;

        foreach (var group in entries.GroupBy(entry => entry.ItemId))
        {
            var itemEntries = group.ToList();
            var tomestoneEntriesByCost = itemEntries
                .Where(entry => entry.Group == VendorCurrencyGroup.Tomestones)
                .GroupBy(entry => entry.Cost)
                .ToDictionary(costGroup => costGroup.Key, costGroup => costGroup.First());

            if (tomestoneEntriesByCost.Count == 0)
            {
                mergedEntries.AddRange(itemEntries);
                continue;
            }

            foreach (var otherEntry in itemEntries.Where(entry => entry.Group == VendorCurrencyGroup.Other).ToList())
            {
                if (!tomestoneEntriesByCost.TryGetValue(otherEntry.Cost, out var tomestoneEntry))
                    continue;

                var npcSet = new HashSet<(uint NpcId, VendorMenuShopType MenuShopType, uint ShopId, int InclusionPageIndex, int InclusionSubPageIndex, uint SourceShopId, int ShopItemIndex, int GcRankIndex, int GcCategoryIndex)>(
                    tomestoneEntry.Npcs.Select(npc => (npc.NpcId, npc.MenuShopType, npc.ShopId, npc.InclusionPageIndex, npc.InclusionSubPageIndex, npc.SourceShopId, npc.ShopItemIndex, npc.GcRankIndex, npc.GcCategoryIndex)));
                foreach (var npc in otherEntry.Npcs)
                {
                    if (!npcSet.Add((npc.NpcId, npc.MenuShopType, npc.ShopId, npc.InclusionPageIndex, npc.InclusionSubPageIndex, npc.SourceShopId, npc.ShopItemIndex, npc.GcRankIndex, npc.GcCategoryIndex)))
                        continue;

                    tomestoneEntry.Npcs.Add(npc);
                    mergedNpcCount++;
                }

                itemEntries.Remove(otherEntry);
                mergedRowCount++;
            }

            mergedEntries.AddRange(itemEntries);
        }

        if (mergedRowCount > 0)
            GatherBuddy.Log.Debug($"[VendorShopResolver] Merged {mergedRowCount} Other rows into matching Tomestones rows, adding {mergedNpcCount} NPC mappings");

        return mergedEntries;
    }

    private static bool IsLegacyTomestoneCurrency(uint currencyItemId, IReadOnlySet<uint> tomestoneItemIds)
        => currencyItemId != 0
        && tomestoneItemIds.Contains(currencyItemId)
        && !TomestoneIds.Contains(currencyItemId);

    private static VendorCurrencyGroup ClassifyCurrency(uint currencyItemId, IReadOnlySet<uint> tomestoneItemIds)
    {
        if (currencyItemId == BicolorCurrencyItemId) return VendorCurrencyGroup.BicolorGemstones;
        if (TomestoneIds.Contains(currencyItemId) || IsLegacyTomestoneCurrency(currencyItemId, tomestoneItemIds)) return VendorCurrencyGroup.Tomestones;
        if (HuntSealIds.Contains(currencyItemId))    return VendorCurrencyGroup.HuntSeals;
        if (ScripIds.Contains(currencyItemId))       return VendorCurrencyGroup.Scrips;
        if (currencyItemId == MgpCurrencyItemId)     return VendorCurrencyGroup.MGP;
        if (currencyItemId == WolfMarkCurrencyItemId) return VendorCurrencyGroup.PvP;
        return VendorCurrencyGroup.Other;
    }
    private static uint NormalizeCurrencyItemIdForVendorGrouping(uint currencyItemId, IReadOnlySet<uint> tomestoneItemIds)
        => IsLegacyTomestoneCurrency(currencyItemId, tomestoneItemIds)
            ? 28u
            : currencyItemId;

    private static int GetSpecialShopCostSelectionPriority(VendorCurrencyGroup group)
        => group == VendorCurrencyGroup.Other ? 1 : 0;

    private static bool TryResolvePreferredSpecialShopCost(
        uint shopId,
        SpecialShop.ItemStruct entry,
        ushort useCurrencyType,
        ExcelSheet<Item> itemSheet,
        Dictionary<uint, uint> tomestoneLookup,
        IReadOnlySet<uint> tomestoneItemIds,
        out ResolvedSpecialShopCost selectedCost,
        out int resolvedCostCount,
        out bool hasAdditionalDistinctCosts)
    {
        var resolvedCosts = new List<ResolvedSpecialShopCost>();
        var originalIndex = 0;
        foreach (var cost in entry.ItemCosts)
        {
            if (cost.ItemCost.RowId == 0 || cost.CurrencyCost == 0)
            {
                originalIndex++;
                continue;
            }

            var resolvedCurrencyItemId = ConvertCurrencyId(shopId, cost.ItemCost.RowId, useCurrencyType, tomestoneLookup);
            if (resolvedCurrencyItemId == 0)
            {
                originalIndex++;
                continue;
            }

            var currencyName = itemSheet.TryGetRow(resolvedCurrencyItemId, out var currencyItem)
                ? currencyItem.Name.ExtractText()
                : string.Empty;
            var currencyItemId = NormalizeCurrencyItemIdForVendorGrouping(resolvedCurrencyItemId, tomestoneItemIds);
            resolvedCosts.Add(new ResolvedSpecialShopCost(
                currencyItemId,
                (uint)cost.CurrencyCost,
                currencyName,
                ClassifyCurrency(currencyItemId, tomestoneItemIds),
                originalIndex));
            originalIndex++;
        }

        resolvedCostCount = resolvedCosts.Count;
        hasAdditionalDistinctCosts = false;
        if (resolvedCosts.Count == 0)
        {
            selectedCost = default;
            return false;
        }

        selectedCost = resolvedCosts
            .OrderBy(cost => GetSpecialShopCostSelectionPriority(cost.Group))
            .ThenByDescending(cost => cost.Amount)
            .ThenBy(cost => cost.OriginalIndex)
            .First();
        var selectedCurrencyItemId = selectedCost.CurrencyItemId;
        hasAdditionalDistinctCosts = resolvedCosts.Any(cost => cost.CurrencyItemId != selectedCurrencyItemId);
        return true;
    }

    private static List<VendorShopEntry> BuildGilShopEntries(
        Dictionary<uint, HashSet<uint>> npcMap, Dictionary<uint, string> npcNames)
    {
        var entries  = new List<VendorShopEntry>();
        var gilSheet = Dalamud.GameData.GetSubrowExcelSheet<GilShopItem>();
        var itemSheet = Dalamud.GameData.GetExcelSheet<Item>();
        if (gilSheet == null || itemSheet == null) return entries;

        foreach (var shopRow in gilSheet)
        {
            var npcs = ResolveNpcs(shopRow.RowId, npcMap, npcNames);
            foreach (var row in shopRow)
            {
                var itemId = row.Item.RowId;
                if (itemId == 0) continue;
                if (!itemSheet.TryGetRow(itemId, out var item)) continue;
                var price = item.PriceMid;
                if (price == 0) continue;

                entries.Add(new VendorShopEntry(
                    itemId, item.Name.ExtractText(), (ushort)item.Icon,
                    price, GilCurrencyItemId, "Gil", new List<VendorNpc>(npcs), VendorShopType.GilShop, VendorCurrencyGroup.Gil));
            }
        }

        var deduped = DeduplicateEntries(entries);
        deduped.Sort((a, b) => string.Compare(a.ItemName, b.ItemName, StringComparison.OrdinalIgnoreCase));
        return deduped;
    }

    private static List<VendorShopEntry> BuildSpecialShopEntries(
        Dictionary<uint, HashSet<uint>> displayNpcMap,
        Dictionary<uint, HashSet<uint>> directSpecialMap,
        Dictionary<uint, HashSet<uint>> inclusionMap,
        Dictionary<uint, List<InclusionShopRoute>> inclusionRoutes,
        Dictionary<uint, string> npcNames,
        Dictionary<uint, uint> tomestoneLookup)
    {
        var entries              = new List<VendorShopEntry>();
        var multiCostListings    = 0;
        var reprioritizedListings = 0;
        var skippedExchangeListings = 0;
        var specialSheet         = Dalamud.GameData.GetExcelSheet<SpecialShop>();
        var itemSheet            = Dalamud.GameData.GetExcelSheet<Item>();
        if (specialSheet == null || itemSheet == null) return entries;
        var tomestoneItemIds     = BuildTomestoneItemIdSet(tomestoneLookup);

        foreach (var shop in specialSheet)
        {
            var npcs            = ResolveSpecialShopNpcs(shop.RowId, displayNpcMap, directSpecialMap, inclusionMap, inclusionRoutes, npcNames);
            var useCurrencyType = shop.UseCurrencyType;
            var shopItemIndex   = 0;

            foreach (var entry in shop.Item)
            {
                if (!TryResolvePreferredSpecialShopCost(shop.RowId, entry, useCurrencyType, itemSheet, tomestoneLookup, tomestoneItemIds, out var selectedCost, out var resolvedCostCount, out var hasAdditionalDistinctCosts))
                    continue;
                if (resolvedCostCount > 1)
                {
                    multiCostListings++;
                    if (selectedCost.OriginalIndex != 0)
                        reprioritizedListings++;
                    if (hasAdditionalDistinctCosts)
                    {
                        skippedExchangeListings++;
                        continue;
                    }
                }
                var addedListing = false;

                foreach (var received in entry.ReceiveItems)
                {
                    var itemId = received.Item.RowId;
                    if (itemId == 0) continue;
                    if (!itemSheet.TryGetRow(itemId, out var item)) continue;
                    var routedNpcs = npcs
                        .Select(npc => npc with
                        {
                            ShopItemIndex = shopItemIndex,
                            SourceShopId  = shop.RowId,
                        })
                        .ToList();

                    entries.Add(new VendorShopEntry(
                        itemId, item.Name.ExtractText(), (ushort)item.Icon,
                        selectedCost.Amount, selectedCost.CurrencyItemId, selectedCost.CurrencyName, routedNpcs,
                        VendorShopType.SpecialCurrency, selectedCost.Group));
                    addedListing = true;
                }

                if (addedListing)
                    shopItemIndex++;
            }
        }

        var deduped = MergeEquivalentOtherEntriesIntoTomestones(DeduplicateEntries(entries));
        if (multiCostListings > 0)
            GatherBuddy.Log.Debug($"[VendorShopResolver] SpecialShop cost selection: {multiCostListings} listings had multiple costs, {reprioritizedListings} selected a non-first recognized currency, {skippedExchangeListings} exchange-style listings were skipped");
        deduped.Sort((a, b) =>
        {
            var cmp = a.Group.CompareTo(b.Group);
            return cmp != 0 ? cmp : string.Compare(a.ItemName, b.ItemName, StringComparison.OrdinalIgnoreCase);
        });
        return deduped;
    }


    public static unsafe byte GetCurrentGrandCompanyId()
    {
        var playerState = PlayerState.Instance();
        return playerState == null ? (byte)0 : playerState->GrandCompany;
    }
    public static uint GetGrandCompanySealCurrencyItemId(uint grandCompanyId)
        => grandCompanyId switch
        {
            1 => 20u,
            2 => 21u,
            3 => 22u,
            _ => 0u,
        };

    public static bool TryGetGrandCompanyIdFromSealCurrencyItemId(uint currencyItemId, out byte grandCompanyId)
    {
        if (currencyItemId == GetGrandCompanySealCurrencyItemId(1))
        {
            grandCompanyId = 1;
            return true;
        }

        if (currencyItemId == GetGrandCompanySealCurrencyItemId(2))
        {
            grandCompanyId = 2;
            return true;
        }

        if (currencyItemId == GetGrandCompanySealCurrencyItemId(3))
        {
            grandCompanyId = 3;
            return true;
        }

        grandCompanyId = 0;
        return false;
    }

    public static uint GetCurrentGrandCompanySealCurrencyItemId()
        => GetGrandCompanySealCurrencyItemId(GetCurrentGrandCompanyId());

    public static bool MatchesCurrentGrandCompany(VendorShopEntry entry)
        => entry.ShopType != VendorShopType.GrandCompanySeals
        || GetCurrentGrandCompanySealCurrencyItemId() == 0
        || entry.CurrencyItemId == GetCurrentGrandCompanySealCurrencyItemId();

    private static string GetGrandCompanySealCurrencyName(ExcelSheet<Item> itemSheet, uint grandCompanyId)
    {
        var currencyItemId = GetGrandCompanySealCurrencyItemId(grandCompanyId);
        return currencyItemId != 0 && itemSheet.TryGetRow(currencyItemId, out var currencyItem)
            ? currencyItem.Name.ExtractText()
            : "GC Seals";
    }

    private static int GetGrandCompanyRankTabIndex(uint requiredGrandCompanyRank)
        => requiredGrandCompanyRank switch
        {
            <= 4u => 0,
            <= 8u => 1,
            _     => 2,
        };

    private static int GetGrandCompanyCategoryTabIndex(int subCategory)
        => subCategory switch
        {
            1 => 2,
            2 => 0,
            3 => 1,
            4 => 3,
            _ => -1,
        };

    private static List<VendorShopEntry> BuildGcShopEntries(
        Dictionary<uint, HashSet<uint>> npcMap, Dictionary<uint, string> npcNames)
    {
        var entries      = new List<VendorShopEntry>();
        var catSheet     = Dalamud.GameData.GetSubrowExcelSheet<GCScripShopItem>();
        var itemSheet    = Dalamud.GameData.GetExcelSheet<Item>();
        var gcShopSheet  = Dalamud.GameData.GetExcelSheet<GCShop>();
        var gcCatSheet   = Dalamud.GameData.GetExcelSheet<GCScripShopCategory>();
        if (catSheet == null || itemSheet == null || gcCatSheet == null) return entries;

        // GrandCompany.RowId > GCShop.RowId (one shop per grand company)
        var grandCompanyToGcShop = new Dictionary<uint, uint>();
        if (gcShopSheet != null)
            foreach (var gcShop in gcShopSheet)
                if (gcShop.GrandCompany.RowId > 0)
                    grandCompanyToGcShop.TryAdd(gcShop.GrandCompany.RowId, gcShop.RowId);

        foreach (var categoryRow in catSheet)
        {
            if (!gcCatSheet.TryGetRow(categoryRow.RowId, out var category))
                continue;

            var grandCompanyId = category.GrandCompany.RowId;
            if (grandCompanyId == 0)
                continue;
            if (!grandCompanyToGcShop.TryGetValue(grandCompanyId, out var gcShopId))
                continue;

            var currencyItemId = GetGrandCompanySealCurrencyItemId(grandCompanyId);
            var currencyName = GetGrandCompanySealCurrencyName(itemSheet, grandCompanyId);
            var gcCategoryIndex = GetGrandCompanyCategoryTabIndex(category.SubCategory);
            if (gcCategoryIndex < 0)
                continue;

            var baseNpcs = ResolveNpcs(gcShopId, npcMap, npcNames);

            foreach (var row in categoryRow)
            {
                var itemId = row.Item.RowId;
                if (itemId == 0) continue;
                if (!itemSheet.TryGetRow(itemId, out var item)) continue;
                var cost = row.CostGCSeals;
                if (cost == 0) continue;
                var gcRankIndex = GetGrandCompanyRankTabIndex(row.RequiredGrandCompanyRank.RowId);
                var npcs = baseNpcs
                    .Select(npc => npc with
                    {
                        MenuShopType = VendorMenuShopType.GrandCompanyShop,
                        GcRankIndex = gcRankIndex,
                        GcCategoryIndex = gcCategoryIndex,
                    })
                    .ToList();

                entries.Add(new VendorShopEntry(
                    itemId, item.Name.ExtractText(), (ushort)item.Icon,
                    cost, currencyItemId, currencyName, new List<VendorNpc>(npcs),
                    VendorShopType.GrandCompanySeals, VendorCurrencyGroup.GrandCompanySeals));
            }
        }

        var deduped = DeduplicateEntries(entries);
        deduped.Sort((a, b) => string.Compare(a.ItemName, b.ItemName, StringComparison.OrdinalIgnoreCase));
        return deduped;
    }
}
