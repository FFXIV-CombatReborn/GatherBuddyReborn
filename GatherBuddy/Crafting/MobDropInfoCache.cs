using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using GatherBuddy.Plugin;
using Lumina.Excel.Sheets;
using LuminaSupplemental.Excel.Model;
using LuminaSupplemental.Excel.Services;

namespace GatherBuddy.Crafting;

public readonly record struct MobDropInfo(string MobName, string LocationLabel);

public static class MobDropInfoCache
{
    private static readonly Dictionary<uint, IReadOnlyList<MobDropInfo>> _dropsByItemId = new();
    private static readonly object _initializeLock = new();
    private static volatile bool _initialized;
    private static volatile bool _initializing;

    public static bool IsInitialized => _initialized;

    public static IReadOnlyList<MobDropInfo> GetDropsForItem(uint itemId)
    {
        EnsureInitializeStarted();
        if (!_initialized)
            return Array.Empty<MobDropInfo>();
        return _dropsByItemId.TryGetValue(itemId, out var drops)
            ? drops
            : Array.Empty<MobDropInfo>();
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
        try
        {
            Build();
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Warning($"[MobDropInfoCache] Build failed: {ex.Message}");
        }
        finally
        {
            _initialized = true;
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

        var dropsByItemId = new Dictionary<uint, List<MobDropInfo>>();
        var addedKeys = new HashSet<(uint ItemId, uint BNpcNameId, uint TerritoryTypeId)>();

        foreach (var drop in mobDrops)
        {
            if (drop.ItemId == 0 || drop.BNpcNameId == 0)
                continue;
            if (!bNpcNameSheet.TryGetRow(drop.BNpcNameId, out var bNpcName))
                continue;

            var mobName = bNpcName.Singular.ExtractText();
            if (string.IsNullOrWhiteSpace(mobName))
                continue;

            if (!spawnsByName.TryGetValue(drop.BNpcNameId, out var spawns) || spawns.Count == 0)
            {
                AddDrop(drop.ItemId, drop.BNpcNameId, 0u, new MobDropInfo(mobName, string.Empty));
                continue;
            }

            foreach (var territoryGroup in spawns.GroupBy(s => s.TerritoryTypeId))
            {
                if (!territorySheet.TryGetRow(territoryGroup.Key, out var territory))
                    continue;

                var territoryName = territory.PlaceName.ValueNullable?.Name.ExtractText() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(territoryName))
                    continue;

                var mapRowId = territory.Map.RowId;
                Map? mapRow = mapRowId != 0 && mapSheet.TryGetRow(mapRowId, out var m) ? m : null;

                var avgPos = territoryGroup
                    .Select(s => s.Position)
                    .Aggregate(System.Numerics.Vector3.Zero, (a, b) => a + b) / territoryGroup.Count();

                var label = territoryName;
                if (mapRow.HasValue)
                {
                    var mapX = ConvertWorldCoordToMapCoord(avgPos.X, mapRow.Value.SizeFactor, mapRow.Value.OffsetX);
                    var mapY = ConvertWorldCoordToMapCoord(avgPos.Z, mapRow.Value.SizeFactor, mapRow.Value.OffsetY);
                    if (mapX is > 0f and < 50f && mapY is > 0f and < 50f)
                        label = $"{territoryName} ({mapX.ToString("F1", CultureInfo.InvariantCulture)}, {mapY.ToString("F1", CultureInfo.InvariantCulture)})";
                }

                AddDrop(drop.ItemId, drop.BNpcNameId, territoryGroup.Key, new MobDropInfo(mobName, label));
            }
        }

        foreach (var (itemId, list) in dropsByItemId)
        {
            list.Sort((a, b) =>
            {
                var nameCmp = string.Compare(a.MobName, b.MobName, StringComparison.OrdinalIgnoreCase);
                return nameCmp != 0 ? nameCmp : string.Compare(a.LocationLabel, b.LocationLabel, StringComparison.OrdinalIgnoreCase);
            });
            _dropsByItemId[itemId] = list;
        }

        GatherBuddy.Log.Debug($"[MobDropInfoCache] Loaded drops for {_dropsByItemId.Count} items");

        void AddDrop(uint itemId, uint bNpcNameId, uint territoryTypeId, MobDropInfo info)
        {
            if (!addedKeys.Add((itemId, bNpcNameId, territoryTypeId)))
                return;
            if (!dropsByItemId.TryGetValue(itemId, out var list))
                dropsByItemId[itemId] = list = new List<MobDropInfo>();
            list.Add(info);
        }
    }

    private static float ConvertWorldCoordToMapCoord(float worldCoord, uint sizeFactor, int offset)
    {
        const double factor = 0.019999999552965164d;
        return sizeFactor == 0
            ? 0f
            : (float)((factor * offset) + (2048.0d / sizeFactor) + (factor * worldCoord) + 1.0d);
    }
}
