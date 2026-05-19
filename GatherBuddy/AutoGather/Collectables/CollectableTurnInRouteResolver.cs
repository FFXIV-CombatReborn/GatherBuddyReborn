using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using GatherBuddy.Automation;
using GatherBuddy.AutoGather.Collectables.Data;
using GatherBuddy.Plugin;
using GatherBuddy.Vulcan.Vendors;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace GatherBuddy.AutoGather.Collectables;

public sealed record CollectableTurnInRouteOption(
    uint              ShopId,
    VendorNpc         Vendor,
    VendorNpcLocation Location,
    string            ZoneName,
    string            DisplayName);

public static class CollectableTurnInRouteResolver
{
    private const string CollectablesShopLookupTag = "AllaganLib.Data.NpcShopCache.CollectablesShopIdToNpcLookup.1";
    private static readonly TimeSpan LuminaShopLookupRetryCooldown = TimeSpan.FromSeconds(2);
    private static readonly Type[] CustomTalkScriptTypes =
    [
        typeof(FateShop),
        typeof(GilShop),
        typeof(SpecialShop),
        typeof(GCShop),
        typeof(FccShop),
        typeof(InclusionShop),
        typeof(CollectablesShop),
        typeof(TopicSelect),
        typeof(PreHandler),
        typeof(CustomTalk),
    ];
    private sealed record LuminaCollectablesLookupData(
        Dictionary<uint, HashSet<uint>> ShopLookup,
        Dictionary<uint, List<uint>> OrderedShopsByNpcId);
    private static readonly object LuminaShopLookupLock = new();
    private static readonly object RouteCacheLock = new();
    private static readonly Dictionary<uint, HashSet<uint>> EmptyShopLookup = [];
    private static IReadOnlyList<CollectableTurnInRouteOption> _cachedRoutes = Array.Empty<CollectableTurnInRouteOption>();
    private static int _cachedRoutesKey;
    private static LuminaCollectablesLookupData? _luminaShopLookupData;
    private static volatile bool _luminaShopLookupInitializing;
    private static DateTime _lastLuminaShopLookupBuildAttemptUtc = DateTime.MinValue;
    private static string? _lastRouteDiagnosticsKey;

    public static bool HasLookupData
        => GetShopLookup().Count > 0;

    public static IReadOnlySet<uint> GetCollectableNpcIds()
    {
        var lookup = GetShopLookup();
        if (lookup.Count == 0)
            return new HashSet<uint>();

        return GetCollectableNpcIds(lookup);
    }

    public static IReadOnlyList<CollectableTurnInRouteOption> GetAvailableRoutes()
    {
        var lookup = GetShopLookup();
        if (lookup.Count == 0)
            return Array.Empty<CollectableTurnInRouteOption>();
        var collectableNpcIds = GetCollectableNpcIds(lookup)
            .OrderBy(npcId => npcId)
            .ToArray();
        if (collectableNpcIds.Length == 0)
            return Array.Empty<CollectableTurnInRouteOption>();
        var collectableNpcIdSet = collectableNpcIds.ToHashSet();
        VendorNpcLocationCache.InitializeAsync(collectableNpcIdSet);

        var routeCacheKey = BuildRouteCacheKey(lookup, collectableNpcIds);
        if (TryGetCachedRoutes(routeCacheKey, out var cachedRoutes))
            return SortRoutes(cachedRoutes, true);

        if (!CanRefreshRoutesNow())
            return TryGetAnyCachedRoutes(out var unsafeCachedRoutes)
                ? SortRoutes(unsafeCachedRoutes, true)
                : Array.Empty<CollectableTurnInRouteOption>();

        if (!VendorNpcLocationCache.IsInitialized)
            return TryGetAnyCachedRoutes(out var initializingCachedRoutes)
                ? SortRoutes(initializingCachedRoutes, true)
                : Array.Empty<CollectableTurnInRouteOption>();

        var routes = BuildAvailableRoutes(lookup, collectableNpcIds);
        CacheRoutes(routeCacheKey, routes);
        return SortRoutes(routes, true);
    }

    public static CollectableTurnInRouteOption? ResolvePreferredRoute(CollectableTurnInRoute? preference)
        => ResolvePreferredRoute(preference, GetAvailableRoutes());

    public static CollectableTurnInRouteOption? ResolvePreferredRoute(CollectableTurnInRoute? preference, IReadOnlyList<CollectableTurnInRouteOption> routes)
    {
        if (routes.Count == 0)
            return null;

        if (preference == null || preference.NpcId == 0)
            return routes[0];

        var exactMatch = routes.FirstOrDefault(route => MatchesPreference(route, preference));
        if (exactMatch != null)
            return exactMatch;

        var territoryMatch = routes.FirstOrDefault(route =>
            route.Vendor.NpcId == preference.NpcId
         && route.Location.TerritoryId == preference.TerritoryId);
        if (territoryMatch != null)
            return territoryMatch;

        var npcMatch = routes.FirstOrDefault(route => route.Vendor.NpcId == preference.NpcId);
        if (npcMatch != null)
            return npcMatch;

        if (preference.ShopId != 0)
        {
            var shopMatch = routes.FirstOrDefault(route => route.ShopId == preference.ShopId);
            if (shopMatch != null)
                return shopMatch;
        }

        return routes[0];
    }

    public static CollectableTurnInRoute ToPreference(CollectableTurnInRouteOption route)
        => new()
        {
            ShopId      = route.ShopId,
            NpcId       = route.Vendor.NpcId,
            NpcName     = route.Vendor.Name,
            TerritoryId = route.Location.TerritoryId,
            MapRowId    = route.Location.MapRowId,
            Location    = route.Location.Position,
            Source      = route.Location.Source,
        };

    private static bool TryGetDataShareShopLookup(out Dictionary<uint, HashSet<uint>>? lookup)
        => Dalamud.PluginInterface.TryGetData(CollectablesShopLookupTag, out lookup) && lookup != null;

    private static Dictionary<uint, HashSet<uint>> GetShopLookup()
    {
        var lookup = new Dictionary<uint, HashSet<uint>>();
        if (TryGetDataShareShopLookup(out var dataShareLookup) && dataShareLookup != null)
            MergeShopLookup(lookup, dataShareLookup);
        MergeShopLookup(lookup, GetLuminaShopLookup());
        return lookup;
    }

    private static IReadOnlyDictionary<uint, HashSet<uint>> GetLuminaShopLookup()
    {
        EnsureLuminaShopLookupAsync();
        return _luminaShopLookupData?.ShopLookup ?? EmptyShopLookup;
    }

    private static void EnsureLuminaShopLookupAsync()
    {
        if (_luminaShopLookupData != null || _luminaShopLookupInitializing)
            return;
        if ((DateTime.UtcNow - _lastLuminaShopLookupBuildAttemptUtc) < LuminaShopLookupRetryCooldown)
            return;

        lock (LuminaShopLookupLock)
        {
            if (_luminaShopLookupData != null || _luminaShopLookupInitializing)
                return;
            if ((DateTime.UtcNow - _lastLuminaShopLookupBuildAttemptUtc) < LuminaShopLookupRetryCooldown)
                return;

            _luminaShopLookupInitializing = true;
            _lastLuminaShopLookupBuildAttemptUtc = DateTime.UtcNow;
            Task.Run(BuildLuminaShopLookup);
        }
    }

    private static bool CanRefreshRoutesNow()
        => Dalamud.PlayerState.IsLoaded
        && Dalamud.Objects.LocalPlayer != null
        && !Functions.BetweenAreas()
        && GenericHelpers.IsScreenReady();

    private static int BuildRouteCacheKey(IReadOnlyDictionary<uint, HashSet<uint>> lookup, IReadOnlyList<uint> collectableNpcIds)
    {
        var hash = new HashCode();
        hash.Add(lookup.Count);
        hash.Add(VendorNpcLocationCache.IsInitialized);
        hash.Add(VendorNpcLocationCache.IsInitializing);
        hash.Add(VendorNpcLocationCache.RequestedNpcCount);
        hash.Add(VendorNpcLocationCache.ResolvedNpcCount);

        foreach (var (shopId, npcIds) in lookup.OrderBy(entry => entry.Key))
        {
            hash.Add(shopId);
            foreach (var npcId in npcIds.OrderBy(id => id))
                hash.Add(npcId);
        }

        foreach (var npcId in collectableNpcIds)
        {
            hash.Add(npcId);
            var locations = VendorNpcLocationCache.GetLocations(npcId);
            hash.Add(locations.Count);
            foreach (var location in locations)
            {
                hash.Add(location.TerritoryId);
                hash.Add(location.MapRowId);
                hash.Add(location.Position.X);
                hash.Add(location.Position.Y);
                hash.Add(location.Position.Z);
                hash.Add((int)location.Source);
            }
        }

        return hash.ToHashCode();
    }

    private static bool TryGetCachedRoutes(int routeCacheKey, out IReadOnlyList<CollectableTurnInRouteOption> routes)
    {
        lock (RouteCacheLock)
        {
            if (_cachedRoutes.Count == 0 || _cachedRoutesKey != routeCacheKey)
            {
                routes = Array.Empty<CollectableTurnInRouteOption>();
                return false;
            }

            routes = _cachedRoutes;
            return true;
        }
    }

    private static bool TryGetAnyCachedRoutes(out IReadOnlyList<CollectableTurnInRouteOption> routes)
    {
        lock (RouteCacheLock)
        {
            if (_cachedRoutes.Count == 0)
            {
                routes = Array.Empty<CollectableTurnInRouteOption>();
                return false;
            }

            routes = _cachedRoutes;
            return true;
        }
    }


    private static void CacheRoutes(int routeCacheKey, IReadOnlyList<CollectableTurnInRouteOption> routes)
    {
        if (routes.Count == 0)
            return;

        lock (RouteCacheLock)
        {
            _cachedRoutesKey = routeCacheKey;
            _cachedRoutes = routes.ToList();
        }
    }

    private static IReadOnlyList<CollectableTurnInRouteOption> BuildAvailableRoutes(
        IReadOnlyDictionary<uint, HashSet<uint>> lookup,
        IReadOnlyList<uint> collectableNpcIds)
    {
        var residentSheet = Dalamud.GameData.GetExcelSheet<ENpcResident>();
        if (residentSheet == null)
            return Array.Empty<CollectableTurnInRouteOption>();

        var routes = new List<CollectableTurnInRouteOption>();
        var shopIdsByNpcId = GetShopIdsByNpcId(lookup);
        foreach (var (npcId, shopIds) in shopIdsByNpcId.OrderBy(entry => entry.Key))
        {
            if (!residentSheet.TryGetRow(npcId, out var resident))
                continue;

            var npcName = resident.Singular.ExtractText();
            if (string.IsNullOrWhiteSpace(npcName))
                continue;

            var location = SelectBestRouteLocation(VendorNpcLocationCache.GetLocations(npcId));
            if (location == null || location.TerritoryId == 0)
                continue;

            var shopId = SelectPreferredShopId(npcId, shopIds);
            if (shopId == 0)
                continue;

            var routeNpcName = string.IsNullOrWhiteSpace(location.NpcName) ? npcName : location.NpcName;
            var vendor = new VendorNpc(npcId, routeNpcName, shopId, VendorMenuShopType.CollectablesShop);
            var zoneName = GetZoneName(location.TerritoryId);
            var displayName = string.IsNullOrWhiteSpace(zoneName)
                ? routeNpcName
                : $"{zoneName} — {routeNpcName}";
            routes.Add(new CollectableTurnInRouteOption(shopId, vendor, location, zoneName, displayName));
        }

        LogRouteDiagnostics(lookup, collectableNpcIds.ToHashSet(), residentSheet, routes);
        return SortRoutes(routes, false);
    }

    private static IReadOnlyList<CollectableTurnInRouteOption> SortRoutes(IEnumerable<CollectableTurnInRouteOption> routes, bool prioritizeCurrentTerritory)
        => routes
            .OrderBy(route => prioritizeCurrentTerritory && route.Location.TerritoryId == Dalamud.ClientState.TerritoryType ? 0 : 1)
            .ThenBy(route => route.ZoneName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(route => route.Vendor.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(route => route.Location.MapRowId)
            .ThenBy(route => route.Location.Position.X)
            .ThenBy(route => route.Location.Position.Z)
            .ToList();

    private static void BuildLuminaShopLookup()
    {
        try
        {
            var lookup = new Dictionary<uint, HashSet<uint>>();
            var orderedShopsByNpcId = new Dictionary<uint, List<uint>>();
            var npcBaseSheet = Dalamud.GameData.GetExcelSheet<ENpcBase>();
            if (npcBaseSheet == null)
            {
                GatherBuddy.Log.Warning("[CollectableTurnInRouteResolver] ENpcBase sheet unavailable; deferring local collectables shop lookup build");
                return;
            }

            foreach (var npc in npcBaseSheet)
            {
                var shopIds = new List<uint>();
                var seenShopIds = new HashSet<uint>();
                var visitedEntries = new HashSet<string>(StringComparer.Ordinal);
                foreach (var menuEntry in npc.ENpcData)
                    CollectCollectablesShopIds(shopIds, seenShopIds, menuEntry, visitedEntries);

                if (shopIds.Count == 0)
                    continue;

                orderedShopsByNpcId[npc.RowId] = shopIds;
                foreach (var shopId in shopIds)
                    AddShopLookupEntry(lookup, shopId, npc.RowId);
            }

            lock (LuminaShopLookupLock)
                _luminaShopLookupData = new LuminaCollectablesLookupData(lookup, orderedShopsByNpcId);
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Warning($"[CollectableTurnInRouteResolver] Failed to build local collectables shop lookup: {ex.Message}");
        }
        finally
        {
            _luminaShopLookupInitializing = false;
        }
    }

    private static void CollectCollectablesShopIds(List<uint> shopIds, HashSet<uint> seenShopIds, IEnumerable<RowRef> menuEntries, HashSet<string> visitedEntries)
    {
        foreach (var menuEntry in menuEntries)
            CollectCollectablesShopIds(shopIds, seenShopIds, menuEntry, visitedEntries);
    }

    private static void CollectCollectablesShopIds(List<uint> shopIds, HashSet<uint> seenShopIds, RowRef menuEntry, HashSet<string> visitedEntries)
    {
        if (menuEntry.RowId == 0)
            return;

        var visitedKey = GetMenuEntryVisitKey(menuEntry);
        if (!visitedEntries.Add(visitedKey))
            return;

        if (menuEntry.Is<CollectablesShop>())
        {
            if (seenShopIds.Add(menuEntry.RowId))
                shopIds.Add(menuEntry.RowId);
            return;
        }

        var preHandlerSheet = Dalamud.GameData.GetExcelSheet<PreHandler>();
        if (menuEntry.Is<PreHandler>() && preHandlerSheet != null && preHandlerSheet.TryGetRow(menuEntry.RowId, out var preHandler))
        {
            CollectCollectablesShopIds(shopIds, seenShopIds, preHandler.Target, visitedEntries);
            return;
        }

        var topicSelectSheet = Dalamud.GameData.GetExcelSheet<TopicSelect>();
        if (menuEntry.Is<TopicSelect>() && topicSelectSheet != null && topicSelectSheet.TryGetRow(menuEntry.RowId, out var topicSelect))
        {
            foreach (var childEntry in topicSelect.Shop)
                CollectCollectablesShopIds(shopIds, seenShopIds, childEntry, visitedEntries);
            return;
        }

        var customTalkSheet = Dalamud.GameData.GetExcelSheet<CustomTalk>();
        if (menuEntry.Is<CustomTalk>() && customTalkSheet != null && customTalkSheet.TryGetRow(menuEntry.RowId, out var customTalk))
        {
            CollectCollectablesShopIds(shopIds, seenShopIds, customTalk.SpecialLinks, visitedEntries);
            foreach (var scriptEntry in customTalk.Script)
            {
                if (!TryResolveCustomTalkScriptArg(scriptEntry.ScriptArg, out var scriptTarget))
                    continue;

                CollectCollectablesShopIds(shopIds, seenShopIds, scriptTarget, visitedEntries);
            }
        }
    }

    private static bool TryResolveCustomTalkScriptArg(uint scriptArg, out RowRef scriptTarget)
    {
        scriptTarget = default;
        if (scriptArg == 0)
            return false;

        var typeHash = RowRef.CreateTypeHash(CustomTalkScriptTypes);
        scriptTarget = RowRef.GetFirstValidRowOrUntyped(
            Dalamud.GameData.Excel,
            scriptArg,
            CustomTalkScriptTypes,
            typeHash,
            Dalamud.GameData.GameData.Options.DefaultExcelLanguage);
        return scriptTarget.RowId != 0;
    }

    private static string GetMenuEntryVisitKey(RowRef menuEntry)
    {
        var typeName = menuEntry.Is<CollectablesShop>() ? nameof(CollectablesShop)
            : menuEntry.Is<CustomTalk>() ? nameof(CustomTalk)
            : menuEntry.Is<PreHandler>() ? nameof(PreHandler)
            : menuEntry.Is<TopicSelect>() ? nameof(TopicSelect)
            : menuEntry.Is<FateShop>() ? nameof(FateShop)
            : menuEntry.Is<GilShop>() ? nameof(GilShop)
            : menuEntry.Is<SpecialShop>() ? nameof(SpecialShop)
            : menuEntry.Is<GCShop>() ? nameof(GCShop)
            : menuEntry.Is<FccShop>() ? nameof(FccShop)
            : menuEntry.Is<InclusionShop>() ? nameof(InclusionShop)
            : "Unknown";
        return $"{typeName}:{menuEntry.RowId}";
    }

    private static void MergeShopLookup(Dictionary<uint, HashSet<uint>> target, IReadOnlyDictionary<uint, HashSet<uint>> source)
    {
        foreach (var (shopId, npcIds) in source)
            foreach (var npcId in npcIds)
                AddShopLookupEntry(target, shopId, npcId);
    }

    private static void AddShopLookupEntry(Dictionary<uint, HashSet<uint>> lookup, uint shopId, uint npcId)
    {
        if (shopId == 0 || npcId == 0)
            return;

        if (!lookup.TryGetValue(shopId, out var npcIds))
            lookup[shopId] = npcIds = new HashSet<uint>();
        npcIds.Add(npcId);
    }

    private static HashSet<uint> GetCollectableNpcIds(IReadOnlyDictionary<uint, HashSet<uint>> lookup)
        => lookup.Values
            .SelectMany(npcIds => npcIds)
            .Where(npcId => npcId != 0)
            .ToHashSet();

    private static Dictionary<uint, List<uint>> GetShopIdsByNpcId(IReadOnlyDictionary<uint, HashSet<uint>> lookup)
        => lookup
            .SelectMany(entry => entry.Value.Select(npcId => (NpcId: npcId, ShopId: entry.Key)))
            .GroupBy(entry => entry.NpcId)
            .ToDictionary(
                group => group.Key,
                group => group.Select(entry => entry.ShopId).Distinct().ToList());

    private static uint SelectPreferredShopId(uint npcId, IReadOnlyList<uint> shopIds)
    {
        if (shopIds.Count == 0)
            return 0;
        if (shopIds.Count == 1)
            return shopIds[0];

        foreach (var shopId in GetOrderedShopIdsForNpc(npcId))
        {
            if (shopIds.Contains(shopId))
                return shopId;
        }

        return shopIds.Min();
    }

    private static IReadOnlyList<uint> GetOrderedShopIdsForNpc(uint npcId)
    {
        if (_luminaShopLookupData != null && _luminaShopLookupData.OrderedShopsByNpcId.TryGetValue(npcId, out var cachedShopIds))
            return cachedShopIds;

        return TryBuildOrderedShopIdsForNpc(npcId, out var fallbackShopIds)
            ? fallbackShopIds
            : Array.Empty<uint>();
    }

    private static bool TryBuildOrderedShopIdsForNpc(uint npcId, out List<uint> shopIds)
    {
        shopIds = [];
        var npcBaseSheet = Dalamud.GameData.GetExcelSheet<ENpcBase>();
        if (npcBaseSheet == null || !npcBaseSheet.TryGetRow(npcId, out var npc))
            return false;

        var seenShopIds = new HashSet<uint>();
        var visitedEntries = new HashSet<string>(StringComparer.Ordinal);
        foreach (var menuEntry in npc.ENpcData)
            CollectCollectablesShopIds(shopIds, seenShopIds, menuEntry, visitedEntries);

        return shopIds.Count > 0;
    }

    private static void LogRouteDiagnostics(
        IReadOnlyDictionary<uint, HashSet<uint>> lookup,
        IReadOnlySet<uint> collectableNpcIds,
        ExcelSheet<ENpcResident> residentSheet,
        IReadOnlyList<CollectableTurnInRouteOption> routes)
    {
        var routeNpcIds = routes
            .Select(route => route.Vendor.NpcId)
            .ToHashSet();
        var missingNpcIds = collectableNpcIds
            .Where(npcId => !routeNpcIds.Contains(npcId))
            .OrderBy(npcId => npcId)
            .ToList();
        var duplicateNpcMappings = lookup
            .SelectMany(entry => entry.Value.Select(npcId => (ShopId: entry.Key, NpcId: npcId)))
            .GroupBy(entry => entry.NpcId)
            .Where(group => group.Select(entry => entry.ShopId).Distinct().Skip(1).Any())
            .OrderBy(group => group.Key)
            .ToList();
        var diagnosticsKey = string.Join("|",
            lookup.Count,
            collectableNpcIds.Count,
            routes.Count,
            string.Join(",", missingNpcIds),
            string.Join(",", duplicateNpcMappings.Select(group => group.Key)));
        if (_lastRouteDiagnosticsKey == diagnosticsKey)
            return;

        _lastRouteDiagnosticsKey = diagnosticsKey;

        if (routes.Count > 0 && missingNpcIds.Count == 0 && duplicateNpcMappings.Count == 0)
            return;

        var issueParts = new List<string>();
        if (routes.Count == 0)
            issueParts.Add("no routes available");
        if (missingNpcIds.Count > 0)
            issueParts.Add($"{missingNpcIds.Count} missing route NPCs");
        if (duplicateNpcMappings.Count > 0)
            issueParts.Add($"{duplicateNpcMappings.Count} NPCs with multiple shop mappings");

        GatherBuddy.Log.Debug(
            $"[CollectableTurnInRouteResolver] Route diagnostics: routes={routes.Count}, collectableNpcs={collectableNpcIds.Count}, shops={lookup.Count} ({string.Join(", ", issueParts)})");

        if (missingNpcIds.Count > 0)
        {
            var missingSample = missingNpcIds
                .Take(5)
                .Select(npcId => DescribeNpc(npcId, residentSheet))
                .ToList();
            var missingSuffix = missingNpcIds.Count > missingSample.Count ? ", ..." : string.Empty;
            GatherBuddy.Log.Debug($"[CollectableTurnInRouteResolver] Missing route NPC sample: {string.Join(", ", missingSample)}{missingSuffix}");
        }

        if (duplicateNpcMappings.Count > 0)
        {
            var duplicateSample = duplicateNpcMappings
                .Take(5)
                .Select(group => $"{DescribeNpc(group.Key, residentSheet)} -> {string.Join("/", group.Select(entry => entry.ShopId).Distinct().OrderBy(shopId => shopId))}")
                .ToList();
            var duplicateSuffix = duplicateNpcMappings.Count > duplicateSample.Count ? ", ..." : string.Empty;
            GatherBuddy.Log.Debug($"[CollectableTurnInRouteResolver] Multi-shop NPC sample: {string.Join(", ", duplicateSample)}{duplicateSuffix}");
        }
    }

    private static string DescribeNpc(uint npcId, ExcelSheet<ENpcResident> residentSheet)
        => residentSheet.TryGetRow(npcId, out var resident)
            ? $"{resident.Singular.ExtractText()} [{npcId}]"
            : npcId.ToString();

    private static VendorNpcLocation? SelectBestRouteLocation(IReadOnlyList<VendorNpcLocation> locations)
        => locations
            .Where(location => location.TerritoryId != 0)
            .OrderBy(location => VendorNavigator.GetPrimaryRouteAetheryteId(location.TerritoryId, location.Position) != 0 ? 0 : 1)
            .ThenBy(location => GetLocationSourcePriority(location.Source))
            .ThenBy(location => location.MapRowId == 0 ? 1 : 0)
            .ThenBy(location => location.TerritoryId)
            .ThenBy(location => location.MapRowId)
            .ThenBy(location => location.Position.X)
            .ThenBy(location => location.Position.Z)
            .FirstOrDefault();

    private static int GetLocationSourcePriority(VendorNpcLocationSource source)
        => source switch
        {
            VendorNpcLocationSource.Override     => 0,
            VendorNpcLocationSource.Level        => 1,
            VendorNpcLocationSource.DataShare    => 2,
            VendorNpcLocationSource.Supplemental => 3,
            VendorNpcLocationSource.Lgb          => 4,
            _                                    => 5,
        };

    private static string GetZoneName(uint territoryId)
    {
        var territorySheet = Dalamud.GameData.GetExcelSheet<TerritoryType>();
        if (territorySheet?.TryGetRow(territoryId, out var territory) != true)
            return $"Territory {territoryId}";

        return territory.PlaceName.ValueNullable?.Name.ExtractText()
            ?? territory.Map.ValueNullable?.PlaceName.ValueNullable?.Name.ExtractText()
            ?? $"Territory {territoryId}";
    }

    private static bool MatchesPreference(CollectableTurnInRouteOption option, CollectableTurnInRoute preference)
        => option.Vendor.NpcId == preference.NpcId
        && option.Location.TerritoryId == preference.TerritoryId
        && option.Location.MapRowId == preference.MapRowId
        && Vector3.DistanceSquared(option.Location.Position, preference.Location) < 0.01f;
}