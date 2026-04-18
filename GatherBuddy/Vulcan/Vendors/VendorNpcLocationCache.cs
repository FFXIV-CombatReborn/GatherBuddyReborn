using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using GatherBuddy.Plugin;
using Lumina.Data.Files;
using Lumina.Data.Parsing.Layer;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using LuminaSupplemental.Excel.Model;
using LuminaSupplemental.Excel.Services;

namespace GatherBuddy.Vulcan.Vendors;

public static class VendorNpcLocationCache
{
    private const string DataShareLocationsTag = "AllaganLib.Data.NpcLevelCache.Locations.1";
    private static readonly object SupplementalNpcPlacesLock = new();
    private static readonly TimeSpan RetryCooldown = TimeSpan.FromSeconds(2);
    private static readonly Dictionary<uint, Dictionary<uint, uint>> MapRowIdsByTerritoryAndLayerIndex = new();
    private static volatile bool _initialized;
    private static volatile bool _initializing;
    private static volatile bool _lastBuildHadDataShareLocations;
    private static DateTime _lastBuildAttemptUtc = DateTime.MinValue;
    private static Dictionary<uint, List<VendorNpcLocation>> _locations = new();
    private static HashSet<uint> _lastVendorNpcIds = new();
    private static Dictionary<(uint TerritoryTypeId, sbyte MapIndex), uint>? _mapRowIdsByTerritoryAndMapIndex;
    private static List<ENpcPlace>? _supplementalNpcPlaces;
    private static bool _supplementalNpcPlacesLoaded;

    public static bool IsInitialized  => _initialized;
    public static bool IsInitializing => _initializing;
    public static int RequestedNpcCount => _lastVendorNpcIds.Count;
    public static int ResolvedNpcCount  => _locations.Count;

    public static void InitializeAsync(IReadOnlySet<uint> vendorNpcIds)
    {
        if (vendorNpcIds.Count == 0)
            return;

        var previousNpcIds = _lastVendorNpcIds;
        var requestedChanged = !ReferenceEquals(previousNpcIds, vendorNpcIds)
            && (previousNpcIds.Count != vendorNpcIds.Count || !previousNpcIds.SetEquals(vendorNpcIds));
        if (requestedChanged)
            _lastVendorNpcIds = vendorNpcIds as HashSet<uint> ?? vendorNpcIds.ToHashSet();
        else if (!ReferenceEquals(previousNpcIds, vendorNpcIds) && vendorNpcIds is HashSet<uint> requestedNpcIdSet)
            _lastVendorNpcIds = requestedNpcIdSet;

        var requestedNpcIds = _lastVendorNpcIds;
        var shouldRefreshForDataShare = _initialized
            && !requestedChanged
            && _locations.Count < requestedNpcIds.Count
            && !_lastBuildHadDataShareLocations
            && HasDataShareLocations();
        if (_initializing)
        {
            if (requestedChanged)
                GatherBuddy.Log.Debug($"[VendorNpcLocationCache] Vendor NPC set changed during active build ({previousNpcIds.Count} -> {requestedNpcIds.Count}), scheduling a rebuild after the current pass completes");
            return;
        }
        if (_initialized && !requestedChanged && !shouldRefreshForDataShare)
            return;
        if ((DateTime.UtcNow - _lastBuildAttemptUtc) < RetryCooldown)
            return;

        if (_initialized && requestedChanged)
        {
            GatherBuddy.Log.Debug($"[VendorNpcLocationCache] Vendor NPC set changed ({previousNpcIds.Count} -> {requestedNpcIds.Count}), rebuilding location cache");
            _initialized = false;
        }
        else if (shouldRefreshForDataShare)
        {
            GatherBuddy.Log.Debug($"[VendorNpcLocationCache] DataShare locations became available after an early fallback build, rebuilding location cache ({_locations.Count}/{requestedNpcIds.Count})");
            _initialized = false;
        }
        StartBuild(requestedNpcIds);
    }

    private static bool HasDataShareLocations()
        => TryGetDataShareLocations(out var dataShare) && dataShare != null && dataShare.Count > 0;

    private static bool TryGetDataShareLocations(out Dictionary<uint, IReadOnlyCollection<Tuple<uint, uint, uint, double, double, bool>>>? dataShare)
        => Dalamud.PluginInterface.TryGetData(DataShareLocationsTag, out dataShare) && dataShare != null;

    public static void ReloadAsync()
    {
        if (_initializing || _lastVendorNpcIds.Count == 0)
            return;
        _initialized = false;
        StartBuild(_lastVendorNpcIds);
    }

    public static VendorNpcLocation? TryGetFirstLocation(uint npcId)
        => _locations.TryGetValue(npcId, out var list) && list.Count > 0 ? list[0] : null;


    private static void StartBuild(IReadOnlySet<uint> vendorNpcIds)
    {
        _initializing = true;
        _lastBuildAttemptUtc = DateTime.UtcNow;
        var npcIds = vendorNpcIds.ToHashSet();
        Task.Run(() => Build(npcIds));
    }

    private static void Build(IReadOnlySet<uint> vendorNpcIds)
    {
        var success = false;
        HashSet<uint>? nextVendorNpcIds = null;
        try
        {
            var residentSheet = Dalamud.GameData.GetExcelSheet<ENpcResident>();
            var mapSheet = Dalamud.GameData.GetExcelSheet<Map>();
            if (residentSheet == null)
            {
                GatherBuddy.Log.Warning("[VendorNpcLocationCache] ENpcResident sheet unavailable; deferring location cache build");
                return;
            }

            if (mapSheet == null)
            {
                GatherBuddy.Log.Warning("[VendorNpcLocationCache] Map sheet unavailable; deferring location cache build");
                return;
            }

            var result = new Dictionary<uint, List<VendorNpcLocation>>();
            var npcNames = new Dictionary<uint, string>();
            var hadDataShareLocations = false;

            foreach (var npc in residentSheet)
            {
                if (!vendorNpcIds.Contains(npc.RowId))
                    continue;

                var name = npc.Singular.ExtractText();
                if (!string.IsNullOrWhiteSpace(name))
                    npcNames[npc.RowId] = name;
            }

            var dataShareFirst = GatherBuddy.Config.VendorNpcLocationsDataShareFirst;

            if (dataShareFirst)
            {
                hadDataShareLocations = ResolveFromDataShare(result, vendorNpcIds, npcNames, mapSheet);
                ResolveFromLevelSheet(result, vendorNpcIds, npcNames, mapSheet);
                ResolveFromSupplementalNpcPlaces(result, vendorNpcIds, npcNames, mapSheet);
                ResolveFromLgb(result, vendorNpcIds, npcNames, mapSheet);
            }
            else
            {
                ResolveFromLgb(result, vendorNpcIds, npcNames, mapSheet);
                ResolveFromLevelSheet(result, vendorNpcIds, npcNames, mapSheet);
                ResolveFromSupplementalNpcPlaces(result, vendorNpcIds, npcNames, mapSheet);
                hadDataShareLocations = ResolveFromDataShare(result, vendorNpcIds, npcNames, mapSheet);
            }

            _locations = result;
            _lastBuildHadDataShareLocations = hadDataShareLocations;

            var resolvedCount = CountResolvedNpcIds(result, vendorNpcIds, mapSheet);
            GatherBuddy.Log.Debug($"[VendorNpcLocationCache] Final: {resolvedCount}/{vendorNpcIds.Count} vendor NPCs resolved");
            LogUnresolvedNpcSample(result, vendorNpcIds, npcNames, mapSheet);

            if (!_lastVendorNpcIds.SetEquals(vendorNpcIds))
                nextVendorNpcIds = _lastVendorNpcIds.ToHashSet();
            success = true;
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Warning($"[VendorNpcLocationCache] Build failed: {ex.Message}");
        }
        finally
        {
            _initialized = success;
            _initializing = false;
            if (!success)
            {
                GatherBuddy.Log.Debug("[VendorNpcLocationCache] Vendor NPC location cache is still uninitialized and will retry when requested");
            }
            else if (nextVendorNpcIds is { Count: > 0 })
            {
                GatherBuddy.Log.Debug($"[VendorNpcLocationCache] Vendor NPC set changed during build ({vendorNpcIds.Count} -> {nextVendorNpcIds.Count}), rebuilding location cache");
                _initialized = false;
                StartBuild(nextVendorNpcIds);
            }
        }
    }

    private static bool ResolveFromDataShare(
        Dictionary<uint, List<VendorNpcLocation>> result,
        IReadOnlySet<uint> vendorNpcIds,
        IReadOnlyDictionary<uint, string> npcNames,
        ExcelSheet<Map> mapSheet)
    {
        if (!TryGetDataShareLocations(out var dataShare) || dataShare == null)
            return false;

        var pendingNpcIds = GetPendingNpcIds(result, vendorNpcIds, npcNames, mapSheet);
        if (pendingNpcIds.Count == 0)
            return true;

        var before = CountResolvedNpcIds(result, vendorNpcIds, mapSheet);
        foreach (var npcId in pendingNpcIds)
        {
            if (!dataShare.TryGetValue(npcId, out var locations))
                continue;
            if (!npcNames.TryGetValue(npcId, out var name))
                continue;

            foreach (var location in locations
                         .Where(location => location.Item3 > 0)
                         .OrderByDescending(location => location.Item6)
                         .ThenBy(location => location.Item3)
                         .ThenBy(location => location.Item1)
                         .ThenBy(location => location.Item4)
                         .ThenBy(location => location.Item5))
            {
                var vendorLocation = TryCreateDataShareLocation(npcId, name, location, mapSheet);
                if (vendorLocation == null)
                    continue;

                AddLocation(result, vendorLocation, mapSheet);
            }
        }

        GatherBuddy.Log.Debug($"[VendorNpcLocationCache] DataShare pass resolved {CountResolvedNpcIds(result, vendorNpcIds, mapSheet) - before} NPCs");
        return true;
    }

    private static VendorNpcLocation? TryCreateDataShareLocation(
        uint npcId,
        string name,
        Tuple<uint, uint, uint, double, double, bool> location,
        ExcelSheet<Map> mapSheet)
    {
        if (location.Item6)
            return TryCreateConvertedMapLocation(npcId, name, location.Item3, location.Item1, (float)location.Item4, (float)location.Item5, mapSheet);

        return new VendorNpcLocation(npcId, name, location.Item3, location.Item1, new Vector3((float)location.Item4, 0f, (float)location.Item5));
    }

    private static float ConvertMapCoordToWorldCoord(float mapCoord, uint sizeFactor, int offset)
    {
        const double factor = 0.019999999552965164d;
        return sizeFactor == 0
            ? 0f
            : (float)((mapCoord - 1.0d - (factor * offset) - (2048.0d / sizeFactor)) / factor);
    }

    private static float ConvertWorldCoordToMapCoord(float worldCoord, uint sizeFactor, int offset)
    {
        const double factor = 0.019999999552965164d;
        return sizeFactor == 0
            ? 0f
            : (float)((factor * offset) + (2048.0d / sizeFactor) + (factor * worldCoord) + 1.0d);
    }

    private static bool TryGetMapCoordinates(VendorNpcLocation location, ExcelSheet<Map> mapSheet, out Vector2 mapCoordinates)
    {
        if (!mapSheet.TryGetRow(location.MapRowId, out var map))
        {
            mapCoordinates = default;
            return false;
        }

        mapCoordinates = new Vector2(
            ConvertWorldCoordToMapCoord(location.Position.X, map.SizeFactor, map.OffsetX),
            ConvertWorldCoordToMapCoord(location.Position.Z, map.SizeFactor, map.OffsetY));
        return true;
    }

    private static bool IsLikelyValidMapCoordinate(float value)
        => value is >= 0f and <= 50f;

    private static float GetCoordinateOverflow(float value)
        => value < 0f ? -value : value > 50f ? value - 50f : 0f;

    private static bool IsUsableLocation(VendorNpcLocation location, ExcelSheet<Map> mapSheet)
    {
        if (!TryGetMapCoordinates(location, mapSheet, out var mapCoordinates))
            return true;

        return IsLikelyValidMapCoordinate(mapCoordinates.X)
            && IsLikelyValidMapCoordinate(mapCoordinates.Y);
    }

    private static float GetLocationOverflow(VendorNpcLocation location, ExcelSheet<Map> mapSheet)
    {
        if (!TryGetMapCoordinates(location, mapSheet, out var mapCoordinates))
            return 0f;

        return GetCoordinateOverflow(mapCoordinates.X) + GetCoordinateOverflow(mapCoordinates.Y);
    }

    private static bool HasUsableLocation(IReadOnlyCollection<VendorNpcLocation> locations, ExcelSheet<Map> mapSheet)
        => locations.Any(location => IsUsableLocation(location, mapSheet));

    private static void SortLocations(List<VendorNpcLocation> locations, ExcelSheet<Map> mapSheet)
    {
        if (locations.Count <= 1)
            return;

        var sorted = locations
            .OrderBy(location => IsUsableLocation(location, mapSheet) ? 0 : 1)
            .ThenBy(location => GetLocationOverflow(location, mapSheet))
            .ThenBy(location => location.TerritoryId)
            .ThenBy(location => location.MapRowId)
            .ToList();

        locations.Clear();
        locations.AddRange(sorted);
    }

    private static void ResolveFromLevelSheet(
        Dictionary<uint, List<VendorNpcLocation>> result,
        IReadOnlySet<uint> vendorNpcIds,
        IReadOnlyDictionary<uint, string> npcNames,
        ExcelSheet<Map> mapSheet)
    {
        var pendingNpcIds = GetPendingNpcIds(result, vendorNpcIds, npcNames, mapSheet);
        if (pendingNpcIds.Count == 0)
            return;

        var levelSheet = Dalamud.GameData.GetExcelSheet<Level>();
        if (levelSheet == null)
        {
            GatherBuddy.Log.Debug("[VendorNpcLocationCache] Level sheet unavailable for fallback location lookup");
            return;
        }

        var before = CountResolvedNpcIds(result, vendorNpcIds, mapSheet);
        foreach (var level in levelSheet.Where(level => level.Object.RowId is > 1000000 and < 11000000))
        {
            var npcId = level.Object.RowId;
            if (!pendingNpcIds.Contains(npcId))
                continue;
            if (!npcNames.TryGetValue(npcId, out var name))
                continue;

            var territoryId = level.Territory.RowId;
            var mapRowId = level.Map.RowId != 0
                ? level.Map.RowId
                : level.Territory.ValueNullable?.Map.RowId ?? 0;
            if (mapRowId == 0 && territoryId != 0)
                mapRowId = GetMapRowIdByTerritoryTypeAndMapIndex(territoryId, (sbyte)0, mapSheet);

            AddLocation(result, new VendorNpcLocation(npcId, name, territoryId, mapRowId, new Vector3(level.X, 0f, level.Z)), mapSheet);
        }

        GatherBuddy.Log.Debug($"[VendorNpcLocationCache] Level sheet pass resolved {CountResolvedNpcIds(result, vendorNpcIds, mapSheet) - before} NPCs");
    }

    private static void ResolveFromSupplementalNpcPlaces(
        Dictionary<uint, List<VendorNpcLocation>> result,
        IReadOnlySet<uint> vendorNpcIds,
        IReadOnlyDictionary<uint, string> npcNames,
        ExcelSheet<Map> mapSheet)
    {
        var pendingNpcIds = GetPendingNpcIds(result, vendorNpcIds, npcNames, mapSheet);
        if (pendingNpcIds.Count == 0)
            return;

        if (!TryLoadSupplementalNpcPlaces(out var npcPlaces) || npcPlaces.Count == 0)
        {
            GatherBuddy.Log.Debug("[VendorNpcLocationCache] ENpcPlace supplemental data unavailable for fallback lookup");
            return;
        }

        var before = CountResolvedNpcIds(result, vendorNpcIds, mapSheet);
        foreach (var npcPlace in npcPlaces)
        {
            var npcId = npcPlace.ENpcResidentId;
            if (!pendingNpcIds.Contains(npcId))
                continue;
            if (!npcNames.TryGetValue(npcId, out var name))
                continue;

            var territory = npcPlace.TerritoryType.ValueNullable;
            if (territory == null)
                continue;

            var mapRowId = territory.Value.Map.RowId != 0
                ? territory.Value.Map.RowId
                : GetMapRowIdByTerritoryTypeAndMapIndex(territory.Value.RowId, (sbyte)0, mapSheet);
            var vendorLocation = TryCreateConvertedMapLocation(
                npcId,
                name,
                territory.Value.RowId,
                mapRowId,
                npcPlace.Position.X,
                npcPlace.Position.Y,
                mapSheet);
            if (vendorLocation == null)
                continue;

            AddLocation(result, vendorLocation, mapSheet);
        }

        GatherBuddy.Log.Debug($"[VendorNpcLocationCache] ENpcPlace supplemental pass resolved {CountResolvedNpcIds(result, vendorNpcIds, mapSheet) - before} NPCs");
    }

    private static void ResolveFromLgb(
        Dictionary<uint, List<VendorNpcLocation>> result,
        IReadOnlySet<uint> vendorNpcIds,
        IReadOnlyDictionary<uint, string> npcNames,
        ExcelSheet<Map> mapSheet)
    {
        var pendingNpcIds = GetPendingNpcIds(result, vendorNpcIds, npcNames, mapSheet);
        if (pendingNpcIds.Count == 0)
            return;

        var territorySheet = Dalamud.GameData.GetExcelSheet<TerritoryType>();
        if (territorySheet == null)
        {
            GatherBuddy.Log.Debug("[VendorNpcLocationCache] TerritoryType sheet unavailable for LGB lookup");
            return;
        }

        var before = CountResolvedNpcIds(result, vendorNpcIds, mapSheet);
        foreach (var territory in territorySheet)
        {
            try
            {
                var bg = territory.Bg.ExtractText();
                if (string.IsNullOrEmpty(bg))
                    continue;
                var levelIdx = bg.IndexOf("/level/", StringComparison.Ordinal);
                if (levelIdx < 0)
                    continue;

                var lgb = Dalamud.GameData.GetFile<LgbFile>($"bg/{bg.Substring(0, levelIdx + 1)}level/planevent.lgb");
                if (lgb == null)
                    continue;

                for (var layerIndex = 0; layerIndex < lgb.Layers.Length; layerIndex++)
                {
                    var layer = lgb.Layers[layerIndex];
                    var layerSetId = layer.LayerSetReferences.Length != 0
                        ? layer.LayerSetReferences[0].LayerSetId
                        : (uint?)null;
                    var mapRowId = GetMapRowIdForLayer(territory, layerSetId, (uint)layerIndex + 1, mapSheet);

                    foreach (var obj in layer.InstanceObjects)
                    {
                        if (obj.AssetType != LayerEntryType.EventNPC)
                            continue;

                        var npcId = ((LayerCommon.ENPCInstanceObject)obj.Object).ParentData.ParentData.BaseId;
                        if (!pendingNpcIds.Contains(npcId))
                            continue;
                        if (!npcNames.TryGetValue(npcId, out var name))
                            continue;

                        var position = new Vector3(
                            obj.Transform.Translation.X,
                            obj.Transform.Translation.Y,
                            obj.Transform.Translation.Z);
                        AddLocation(result, new VendorNpcLocation(npcId, name, territory.RowId, mapRowId, position), mapSheet);
                    }
                }
            }
            catch
            {
            }
        }

        GatherBuddy.Log.Debug($"[VendorNpcLocationCache] LGB pass resolved {CountResolvedNpcIds(result, vendorNpcIds, mapSheet) - before} NPCs");
    }

    private static HashSet<uint> GetPendingNpcIds(
        IReadOnlyDictionary<uint, List<VendorNpcLocation>> result,
        IReadOnlySet<uint> vendorNpcIds,
        IReadOnlyDictionary<uint, string> npcNames,
        ExcelSheet<Map> mapSheet)
        => vendorNpcIds
            .Where(id => npcNames.ContainsKey(id)
                && (!result.TryGetValue(id, out var existingLocations) || !HasUsableLocation(existingLocations, mapSheet)))
            .ToHashSet();

    private static int CountResolvedNpcIds(
        IReadOnlyDictionary<uint, List<VendorNpcLocation>> result,
        IEnumerable<uint> vendorNpcIds,
        ExcelSheet<Map> mapSheet)
        => vendorNpcIds.Count(id => result.TryGetValue(id, out var locations) && HasUsableLocation(locations, mapSheet));

    private static void LogUnresolvedNpcSample(
        IReadOnlyDictionary<uint, List<VendorNpcLocation>> result,
        IReadOnlySet<uint> vendorNpcIds,
        IReadOnlyDictionary<uint, string> npcNames,
        ExcelSheet<Map> mapSheet)
    {
        var unresolvedNpcIds = vendorNpcIds
            .Where(id => !result.TryGetValue(id, out var locations) || !HasUsableLocation(locations, mapSheet))
            .ToList();
        if (unresolvedNpcIds.Count == 0)
            return;

        var sample = unresolvedNpcIds
            .Take(10)
            .Select(id => npcNames.TryGetValue(id, out var name) ? $"{name} [{id}]" : id.ToString())
            .ToList();
        var suffix = unresolvedNpcIds.Count > sample.Count ? ", ..." : string.Empty;
        GatherBuddy.Log.Debug($"[VendorNpcLocationCache] Unresolved after all passes: {unresolvedNpcIds.Count}/{vendorNpcIds.Count} ({string.Join(", ", sample)}{suffix})");
    }

    private static bool AddLocation(
        Dictionary<uint, List<VendorNpcLocation>> result,
        VendorNpcLocation location,
        ExcelSheet<Map> mapSheet)
    {
        if (!result.TryGetValue(location.NpcId, out var list))
            result[location.NpcId] = list = new List<VendorNpcLocation>();

        if (list.Any(existing => AreLocationsEquivalent(existing, location, mapSheet)))
            return false;

        list.Add(location);
        SortLocations(list, mapSheet);
        return true;
    }

    private static bool AreLocationsEquivalent(
        VendorNpcLocation left,
        VendorNpcLocation right,
        ExcelSheet<Map> mapSheet)
    {
        if (left.NpcId != right.NpcId || left.TerritoryId != right.TerritoryId || left.MapRowId != right.MapRowId)
            return false;

        if (Vector3.DistanceSquared(left.Position, right.Position) <= 0.25f)
            return true;

        if (!TryGetMapCoordinates(left, mapSheet, out var leftMapCoordinates)
         || !TryGetMapCoordinates(right, mapSheet, out var rightMapCoordinates))
            return false;

        return (int)leftMapCoordinates.X == (int)rightMapCoordinates.X
            && (int)leftMapCoordinates.Y == (int)rightMapCoordinates.Y;
    }

    private static VendorNpcLocation? TryCreateConvertedMapLocation(
        uint npcId,
        string name,
        uint territoryId,
        uint mapRowId,
        float mapX,
        float mapY,
        ExcelSheet<Map> mapSheet)
    {
        if (mapRowId == 0 || !mapSheet.TryGetRow(mapRowId, out var map))
            return null;

        return new VendorNpcLocation(
            npcId,
            name,
            territoryId,
            mapRowId,
            new Vector3(
                ConvertMapCoordToWorldCoord(mapX, map.SizeFactor, map.OffsetX),
                0f,
                ConvertMapCoordToWorldCoord(mapY, map.SizeFactor, map.OffsetY)));
    }

    private static uint GetMapRowIdForLayer(TerritoryType territory, uint? layerSetId, uint fallbackLayerIndex, ExcelSheet<Map> mapSheet)
    {
        if (territory.Map.RowId != 0)
            return territory.Map.RowId;

        var layerIndex = layerSetId ?? fallbackLayerIndex;
        return GetMapRowIdAtLayerIndex(territory.RowId, layerIndex, mapSheet);
    }

    private static uint GetMapRowIdAtLayerIndex(uint territoryTypeId, uint layerIndex, ExcelSheet<Map> mapSheet)
    {
        MapRowIdsByTerritoryAndLayerIndex.TryAdd(territoryTypeId, new Dictionary<uint, uint>());
        var cache = MapRowIdsByTerritoryAndLayerIndex[territoryTypeId];
        if (cache.TryGetValue(layerIndex, out var mapRowId))
            return mapRowId;

        mapRowId = GetMapRowIdByTerritoryTypeAndMapIndex(territoryTypeId, (sbyte)layerIndex, mapSheet);
        if (mapRowId == 0 && cache.Count > 0)
        {
            var maxLayer = cache.Keys.Max();
            var actualLayer = ((layerIndex - 1) % maxLayer) + 1;
            if (cache.TryGetValue(actualLayer, out var existingMapRowId) && existingMapRowId != 0)
                mapRowId = existingMapRowId;
        }

        cache[layerIndex] = mapRowId;
        return mapRowId;
    }

    private static uint GetMapRowIdByTerritoryTypeAndMapIndex(uint territoryTypeId, sbyte mapIndex, ExcelSheet<Map> mapSheet)
    {
        if (_mapRowIdsByTerritoryAndMapIndex == null)
        {
            _mapRowIdsByTerritoryAndMapIndex = new Dictionary<(uint TerritoryTypeId, sbyte MapIndex), uint>();
            foreach (var map in mapSheet)
                _mapRowIdsByTerritoryAndMapIndex.TryAdd((map.TerritoryType.RowId, map.MapIndex), map.RowId);
        }

        if (_mapRowIdsByTerritoryAndMapIndex.TryGetValue((territoryTypeId, mapIndex), out var mapRowId))
            return mapRowId;

        return _mapRowIdsByTerritoryAndMapIndex.TryGetValue((territoryTypeId, (sbyte)0), out mapRowId)
            ? mapRowId
            : 0;
    }

    private static bool TryLoadSupplementalNpcPlaces(out List<ENpcPlace> npcPlaces)
    {
        lock (SupplementalNpcPlacesLock)
        {
            if (!_supplementalNpcPlacesLoaded)
            {
                _supplementalNpcPlacesLoaded = true;
                try
                {
                    var gameData = Dalamud.GameData.GameData;
                    _supplementalNpcPlaces = CsvLoader.LoadResource<ENpcPlace>(
                        CsvLoader.ENpcPlaceResourceName,
                        true,
                        out var failedLines,
                        out var exceptions,
                        gameData,
                        gameData.Options.DefaultExcelLanguage);

                    if (exceptions.Count != 0)
                        GatherBuddy.Log.Warning($"[VendorNpcLocationCache] ENpcPlace supplemental load reported {exceptions.Count} parser exceptions");
                    if (failedLines.Count != 0)
                        GatherBuddy.Log.Warning($"[VendorNpcLocationCache] ENpcPlace supplemental load failed on {failedLines.Count} lines");
                }
                catch (Exception ex)
                {
                    _supplementalNpcPlaces = new List<ENpcPlace>();
                    GatherBuddy.Log.Warning($"[VendorNpcLocationCache] Failed to load ENpcPlace supplemental data: {ex.Message}");
                }
            }

            npcPlaces = _supplementalNpcPlaces ?? new List<ENpcPlace>();
            return npcPlaces.Count > 0;
        }
    }
}
