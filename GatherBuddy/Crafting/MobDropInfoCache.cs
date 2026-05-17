using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using GatherBuddy.Plugin;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using LuminaSupplemental.Excel.Model;
using LuminaSupplemental.Excel.Services;

namespace GatherBuddy.Crafting;

public readonly record struct MobDropClusterInfo(
    int AreaIndex,
    int SpawnPointCount,
    uint TerritoryTypeId,
    uint MapRowId,
    float MapX,
    float MapY)
{
    public bool HasCoordinates => MapRowId != 0 && TerritoryTypeId != 0 && MapX > 0f && MapY > 0f;
}

public sealed record MobDropZoneInfo(
    string ZoneName,
    uint TerritoryTypeId,
    IReadOnlyList<MobDropClusterInfo> Clusters)
{
    public int ClusterCount => Clusters.Count;
}

public sealed record MobDropMobInfo(
    uint BNpcNameId,
    string MobName,
    IReadOnlyList<MobDropZoneInfo> Zones)
{
    public int ZoneCount => Zones.Count;
    public int ClusterCount => Zones.Sum(zone => zone.ClusterCount);
}

public sealed record MobDropItemInfo(
    IReadOnlyList<MobDropMobInfo> Mobs)
{
    public static MobDropItemInfo Empty { get; } = new(Array.Empty<MobDropMobInfo>());
    public bool HasData => Mobs.Count > 0;
    public int MobCount => Mobs.Count;
    public int ZoneCount => Mobs
        .SelectMany(mob => mob.Zones)
        .Select(zone => (zone.TerritoryTypeId, zone.ZoneName))
        .Distinct()
        .Count();
    public int ClusterCount => Mobs.Sum(mob => mob.ClusterCount);
}

public static class MobDropInfoCache
{
    private const float ClusterMergeDistance = 0.7f;
    private const float ClusterMergeDistanceSquared = ClusterMergeDistance * ClusterMergeDistance;
    private const string UnknownZoneName = "Unknown zone";

    private static Dictionary<uint, MobDropItemInfo> _dropsByItemId = new();
    private static HashSet<uint> _dropItemIds = new();
    private static readonly object _initializeLock = new();
    private static volatile bool _initialized;
    private static volatile bool _initializing;

    public static bool IsInitialized => _initialized;

    public static MobDropItemInfo GetDropInfoForItem(uint itemId)
    {
        EnsureInitializeStarted();
        if (!_initialized)
            return MobDropItemInfo.Empty;
        return _dropsByItemId.TryGetValue(itemId, out var dropInfo)
            ? dropInfo
            : MobDropItemInfo.Empty;
    }

    public static bool IsKnownDropItem(uint itemId)
    {
        EnsureInitializeStarted();
        return _initialized && _dropItemIds.Contains(itemId);
    }

    public static void EnsureInitializeStarted()
    {
        if (_initialized || _initializing)
            return;
        lock (_initializeLock)
        {
            if (_initialized || _initializing)
                return;
            _initializing = true;
        }
        Task.Run(BuildSafe);
    }

    private static void BuildSafe()
    {
        var success = false;
        try
        {
            Build();
            success = true;
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Warning($"[MobDropInfoCache] Build failed: {ex.Message}");
        }
        finally
        {
            _initialized = success;
            _initializing = false;
        }
    }

    private static void Build()
    {
        var gameData = Dalamud.GameData.GameData;
        var language = gameData.Options.DefaultExcelLanguage;

        var mobDrops = CsvLoader.LoadResource<MobDrop>(
            CsvLoader.MobDropResourceName,
            true,
            out _,
            out _,
            gameData,
            language);
        var mobSpawns = CsvLoader.LoadResource<MobSpawnPosition>(
            CsvLoader.MobSpawnResourceName,
            true,
            out _,
            out _,
            gameData,
            language);

        if (mobDrops == null || mobDrops.Count == 0)
            return;

        var bNpcNameSheet = Dalamud.GameData.GetExcelSheet<BNpcName>();
        var territorySheet = Dalamud.GameData.GetExcelSheet<TerritoryType>();
        var mapSheet = Dalamud.GameData.GetExcelSheet<Map>();
        if (bNpcNameSheet == null || territorySheet == null || mapSheet == null)
            return;

        var spawnsByName = new Dictionary<uint, List<MobSpawnPosition>>();
        if (mobSpawns != null)
        {
            foreach (var spawn in mobSpawns)
            {
                if (spawn.BNpcNameId == 0 || spawn.TerritoryTypeId == 0)
                    continue;
                if (!spawnsByName.TryGetValue(spawn.BNpcNameId, out var list))
                    spawnsByName[spawn.BNpcNameId] = list = new List<MobSpawnPosition>();
                list.Add(spawn);
            }
        }

        var dropsByItemId = new Dictionary<uint, List<MobDropMobInfo>>();
        var dropItemIds = new HashSet<uint>();
        var addedMobKeys = new HashSet<(uint ItemId, uint BNpcNameId)>();
        foreach (var drop in mobDrops)
        {
            if (drop.ItemId == 0 || drop.BNpcNameId == 0)
                continue;
            if (!bNpcNameSheet.TryGetRow(drop.BNpcNameId, out var bNpcName))
                continue;

            var mobName = bNpcName.Singular.ExtractText();
            if (string.IsNullOrWhiteSpace(mobName))
                continue;
            dropItemIds.Add(drop.ItemId);
            if (!addedMobKeys.Add((drop.ItemId, drop.BNpcNameId)))
                continue;

            spawnsByName.TryGetValue(drop.BNpcNameId, out var spawns);
            var zones = BuildZonesForMob(drop.ItemId, drop.BNpcNameId, mobName, spawns, territorySheet, mapSheet);
            if (zones.Count == 0)
            {
                GatherBuddy.Log.Debug($"[MobDropInfoCache] Filtered all displayable zones for mob {mobName} ({drop.BNpcNameId}) dropping item {drop.ItemId}");
                continue;
            }
            if (!dropsByItemId.TryGetValue(drop.ItemId, out var mobList))
                dropsByItemId[drop.ItemId] = mobList = new List<MobDropMobInfo>();
            mobList.Add(new MobDropMobInfo(drop.BNpcNameId, mobName, zones));
        }

        var finalDropsByItemId = new Dictionary<uint, MobDropItemInfo>(dropsByItemId.Count);
        foreach (var (itemId, mobs) in dropsByItemId)
        {
            finalDropsByItemId[itemId] = new MobDropItemInfo(
                mobs.OrderBy(mob => mob.MobName, StringComparer.OrdinalIgnoreCase).ToList());
        }

        _dropsByItemId = finalDropsByItemId;
        _dropItemIds = dropItemIds;
    }

    private static IReadOnlyList<MobDropZoneInfo> BuildZonesForMob(
        uint itemId,
        uint bNpcNameId,
        string mobName,
        IReadOnlyList<MobSpawnPosition>? spawns,
        ExcelSheet<TerritoryType> territorySheet,
        ExcelSheet<Map> mapSheet)
    {
        if (spawns == null || spawns.Count == 0)
        {
            GatherBuddy.Log.Debug($"[MobDropInfoCache] No spawn data for mob {mobName} ({bNpcNameId}) dropping item {itemId}");
            return
            [
                new MobDropZoneInfo(UnknownZoneName, 0u,
                [
                    new MobDropClusterInfo(1, 0, 0u, 0u, 0f, 0f),
                ]),
            ];
        }

        var zones = new List<MobDropZoneInfo>();
        foreach (var territoryGroup in spawns
                     .GroupBy(spawn => spawn.TerritoryTypeId)
                     .OrderBy(group => ResolveZoneName(group.Key, territorySheet), StringComparer.OrdinalIgnoreCase))
        {
            var territoryTypeId = territoryGroup.Key;
            if (!ShouldDisplayTerritory(territoryTypeId, territorySheet))
                continue;
            var zoneName = ResolveZoneName(territoryTypeId, territorySheet);
            var mapRowId = 0u;
            Map? mapRow = null;
            if (territoryTypeId != 0 && territorySheet.TryGetRow(territoryTypeId, out var territory))
            {
                mapRowId = territory.Map.RowId;
                mapRow = mapRowId != 0 && mapSheet.TryGetRow(mapRowId, out var foundMap) ? foundMap : null;
            }
            if (territoryTypeId != 0 && mapRowId == 0)
                GatherBuddy.Log.Debug($"[MobDropInfoCache] {mobName} in {zoneName} has no map row; area flags will be text-only");

            var clusters = BuildClusters(
                territoryGroup.Select(spawn => spawn.Position).ToList(),
                territoryTypeId,
                zoneName,
                mobName,
                mapRow);
            zones.Add(new MobDropZoneInfo(zoneName, territoryTypeId, clusters));
        }

        return zones;
    }

    private static bool ShouldDisplayTerritory(
        uint territoryTypeId,
        ExcelSheet<TerritoryType> territorySheet)
    {
        if (territoryTypeId == 0)
            return true;
        if (!territorySheet.TryGetRow(territoryTypeId, out var territory))
            return true;
        if (territory.ContentFinderCondition.RowId != 0)
            return false;

        if (territory.QuestBattle.RowId != 0)
            return false;

        return true;
    }

    private static List<MobDropClusterInfo> BuildClusters(
        IReadOnlyList<Vector3> positions,
        uint territoryTypeId,
        string zoneName,
        string mobName,
        Map? mapRow)
    {
        var normalizedPositions = positions
            .Select(position => NormalizeSpawnPosition(position, mapRow))
            .Where(position => position.HasValue)
            .Select(position => position!.Value)
            .ToList();

        var clusteredPoints = ClusterPositions(normalizedPositions.Select(position => position.Coordinates).ToList());

        var clusters = new List<MobDropClusterInfo>(clusteredPoints.Count);
        for (var i = 0; i < clusteredPoints.Count; i++)
        {
            var clusterPoints = clusteredPoints[i];
            var representativePoint = GetRepresentativePoint(clusterPoints);
            var coordX = representativePoint.X;
            var coordY = representativePoint.Y;
            if (!IsLikelyValidMapCoordinate(coordX) || !IsLikelyValidMapCoordinate(coordY))
            {
                coordX = 0f;
                coordY = 0f;
                GatherBuddy.Log.Debug($"[MobDropInfoCache] {mobName} area {i + 1} in {zoneName} produced out-of-range map coordinates");
            }

            clusters.Add(new MobDropClusterInfo(
                i + 1,
                clusterPoints.Count,
                territoryTypeId,
                mapRow?.RowId ?? 0u,
                coordX,
                coordY));
        }

        if (clusters.Count == 0)
            clusters.Add(new MobDropClusterInfo(1, positions.Count, territoryTypeId, mapRow?.RowId ?? 0u, 0f, 0f));
        return clusters;
    }

    private static List<List<Vector2>> ClusterPositions(IReadOnlyList<Vector2> positions)
    {
        var orderedPositions = positions
            .OrderBy(position => position.X)
            .ThenBy(position => position.Y)
            .ToList();
        var clusters = new List<List<Vector2>>();
        var centroids = new List<Vector2>();
        foreach (var position in orderedPositions)
        {
            var bestIndex = -1;
            var bestDistanceSquared = float.PositiveInfinity;
            for (var i = 0; i < centroids.Count; i++)
            {
                var distanceSquared = DistanceSquared2D(centroids[i], position);
                if (distanceSquared > ClusterMergeDistanceSquared || distanceSquared >= bestDistanceSquared)
                    continue;
                bestIndex = i;
                bestDistanceSquared = distanceSquared;
            }

            if (bestIndex < 0)
            {
                clusters.Add([position]);
                centroids.Add(position);
                continue;
            }

            clusters[bestIndex].Add(position);
            centroids[bestIndex] = AveragePosition(clusters[bestIndex]);
        }

        return clusters
            .Select((points, index) => (Points: points, Centroid: centroids[index]))
            .OrderByDescending(cluster => cluster.Points.Count)
            .ThenBy(cluster => cluster.Centroid.X)
            .ThenBy(cluster => cluster.Centroid.Y)
            .Select(cluster => cluster.Points)
            .ToList();
    }

    private static Vector2 AveragePosition(IReadOnlyList<Vector2> points)
    {
        if (points.Count == 0)
            return Vector2.Zero;

        var total = Vector2.Zero;
        for (var i = 0; i < points.Count; i++)
            total += points[i];
        return total / points.Count;
    }

    private static Vector2 GetRepresentativePoint(IReadOnlyList<Vector2> points)
    {
        if (points.Count == 0)
            return Vector2.Zero;
        if (points.Count == 1)
            return points[0];

        var bestPoint = points[0];
        var bestScore = double.PositiveInfinity;
        for (var i = 0; i < points.Count; i++)
        {
            var score = 0d;
            for (var j = 0; j < points.Count; j++)
                score += DistanceSquared2D(points[i], points[j]);
            if (score >= bestScore)
                continue;
            bestScore = score;
            bestPoint = points[i];
        }

        return bestPoint;
    }

    private static float DistanceSquared2D(Vector2 left, Vector2 right)
    {
        var deltaX = left.X - right.X;
        var deltaY = left.Y - right.Y;
        return (deltaX * deltaX) + (deltaY * deltaY);
    }

    private static string ResolveZoneName(uint territoryTypeId, ExcelSheet<TerritoryType> territorySheet)
    {
        if (territoryTypeId == 0)
            return UnknownZoneName;
        if (territorySheet.TryGetRow(territoryTypeId, out var territory))
        {
            var zoneName = territory.PlaceName.ValueNullable?.Name.ExtractText() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(zoneName))
                return zoneName;
        }

        return $"Territory {territoryTypeId}";
    }

    private static NormalizedSpawnPoint? NormalizeSpawnPosition(Vector3 position, Map? mapRow)
    {
        if (IsLikelyValidMapCoordinate(position.X) && IsLikelyValidMapCoordinate(position.Y))
            return new NormalizedSpawnPoint(new Vector2(position.X, position.Y));

        if (!mapRow.HasValue)
            return null;

        var mapX = ConvertWorldCoordToMapCoord(position.X, mapRow.Value.SizeFactor, mapRow.Value.OffsetX);
        var mapY = ConvertWorldCoordToMapCoord(position.Z, mapRow.Value.SizeFactor, mapRow.Value.OffsetY);
        if (!IsLikelyValidMapCoordinate(mapX) || !IsLikelyValidMapCoordinate(mapY))
            return null;
        return new NormalizedSpawnPoint(new Vector2(mapX, mapY));
    }

    private static bool IsLikelyValidMapCoordinate(float value)
        => value is > 0f and < 50f;

    private static float ConvertWorldCoordToMapCoord(float worldCoord, uint sizeFactor, int offset)
    {
        const double factor = 0.019999999552965164d;
        return sizeFactor == 0
            ? 0f
            : (float)((factor * offset) + (2048.0d / sizeFactor) + (factor * worldCoord) + 1.0d);
    }

    private readonly record struct NormalizedSpawnPoint(Vector2 Coordinates);
}
