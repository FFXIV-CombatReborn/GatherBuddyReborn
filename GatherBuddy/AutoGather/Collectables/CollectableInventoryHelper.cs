using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FFXIVClientStructs.FFXIV.Client.Game;
using GatherBuddy.Config;
using Lumina.Excel.Sheets;

namespace GatherBuddy.AutoGather.Collectables;

public readonly record struct CollectableTurnInItem(uint ItemId, string ItemName, int Count, int JobId);

public readonly record struct CollectableTurnInThresholdState(
    int  CollectableCount,
    int  UsedSlots,
    int  TotalSlots,
    bool ThresholdReached,
    bool InventoryFullMode);

public static class CollectableInventoryHelper
{
    private readonly record struct TurnInItemMetadata(string ItemName, int JobId);
    private static readonly object TurnInItemMetadataLock = new();
    private static readonly Dictionary<uint, TurnInItemMetadata> EmptyTurnInItemMetadata = new();
    private static HashSet<uint>? _collectableShopItemIds;
    private static HashSet<uint>? _fishItemIds;
    private static Dictionary<uint, TurnInItemMetadata>? _turnInItemMetadata;
    private static bool _turnInItemMetadataInitializing;

    public static bool IsTurnInItemMetadataReady
        => _turnInItemMetadata != null;

    public static bool IsTurnInItemMetadataLoading
        => _turnInItemMetadataInitializing;

    public static void ResetCaches()
    {
        _collectableShopItemIds = null;
        _fishItemIds            = null;
        _turnInItemMetadata     = null;
        _turnInItemMetadataInitializing = false;
        ItemJobResolver.ResetCache();
    }

    public static void InitializeAsync()
    {
        if (_turnInItemMetadata != null || _turnInItemMetadataInitializing)
            return;

        lock (TurnInItemMetadataLock)
        {
            if (_turnInItemMetadata != null || _turnInItemMetadataInitializing)
                return;

            _turnInItemMetadataInitializing = true;
        }

        Task.Run(() =>
        {
            try
            {
                var metadata = BuildTurnInItemMetadata();
                if (metadata == null)
                    return;

                lock (TurnInItemMetadataLock)
                    _turnInItemMetadata = metadata;
            }
            catch (Exception ex)
            {
                GatherBuddy.Log.Error($"[CollectableInventoryHelper] Failed to build turn-in item metadata: {ex}");
            }
            finally
            {
                _turnInItemMetadataInitializing = false;
            }
        });
    }

    public static IReadOnlyList<CollectableTurnInItem> GetTurnInItems()
    {
        var turnInItemMetadata = GetTurnInItemMetadata();
        if (turnInItemMetadata.Count == 0)
            return Array.Empty<CollectableTurnInItem>();
        var items = new List<CollectableTurnInItem>();
        foreach (var group in ItemHelper.GetCurrentInventoryItems()
                     .Where(item => item.IsCollectable && turnInItemMetadata.ContainsKey(item.BaseItemId))
                     .GroupBy(item => item.BaseItemId)
                     .OrderBy(group => group.Key))
        {
            var metadata = turnInItemMetadata[group.Key];
            items.Add(new CollectableTurnInItem(group.Key, metadata.ItemName, group.Count(), metadata.JobId));
        }

        return items;
    }


    public static unsafe CollectableTurnInThresholdState GetThresholdState(CollectableConfig config)
    {
        if (!config.AutoTurnInCollectables)
            return new CollectableTurnInThresholdState(0, 0, 0, false, config.UseInventoryFullThreshold);

        var (usedSlots, totalSlots) = GetInventoryUsage();
        var turnInItemMetadata = GetTurnInItemMetadata();
        if (turnInItemMetadata.Count == 0)
            return new CollectableTurnInThresholdState(0, usedSlots, totalSlots, false, config.UseInventoryFullThreshold);
        var collectableCount = ItemHelper.GetCurrentInventoryItems()
            .Count(item => item.IsCollectable && turnInItemMetadata.ContainsKey(item.BaseItemId));

        var thresholdReached = false;
        if (config.UseInventoryFullThreshold)
            thresholdReached = collectableCount > 0 && usedSlots >= config.InventoryFullThreshold;
        else if (config.CollectableInventoryThreshold > 0)
            thresholdReached = collectableCount >= config.CollectableInventoryThreshold;

        if (!thresholdReached && totalSlots > 0)
            thresholdReached = collectableCount > 0 && usedSlots >= totalSlots;

        return new CollectableTurnInThresholdState(
            collectableCount,
            usedSlots,
            totalSlots,
            thresholdReached,
            config.UseInventoryFullThreshold);
    }

    private static Dictionary<uint, TurnInItemMetadata> GetTurnInItemMetadata()
    {
        if (_turnInItemMetadata != null)
            return _turnInItemMetadata;
        InitializeAsync();
        return EmptyTurnInItemMetadata;
    }

    private static Dictionary<uint, TurnInItemMetadata>? BuildTurnInItemMetadata()
    {
        var itemSheet = Dalamud.GameData.GetExcelSheet<Item>();
        if (itemSheet == null)
        {
            GatherBuddy.Log.Warning("[CollectableInventoryHelper] Item sheet unavailable while building turn-in item metadata");
            return null;
        }

        var collectableShopItemIds = GetCollectableShopItemIds();
        if (collectableShopItemIds.Count == 0)
            return new Dictionary<uint, TurnInItemMetadata>();

        var fishItemIds = GetFishItemIds();
        var turnInItemMetadata = new Dictionary<uint, TurnInItemMetadata>(collectableShopItemIds.Count);
        foreach (var itemId in collectableShopItemIds)
        {
            if (!itemSheet.TryGetRow(itemId, out var item))
                continue;

            if (fishItemIds.Contains(itemId) && item.AetherialReduce > 0)
                continue;

            var itemName = item.Name.ExtractText();
            if (string.IsNullOrWhiteSpace(itemName))
                continue;

            var jobId = ItemJobResolver.GetJobIdForItem(itemId, Dalamud.GameData);
            if (jobId < 0)
                continue;

            turnInItemMetadata[itemId] = new TurnInItemMetadata(itemName, jobId);
        }

        return turnInItemMetadata;
    }

    private static HashSet<uint> GetCollectableShopItemIds()
    {
        if (_collectableShopItemIds != null)
            return _collectableShopItemIds;

        var shopSheet = Dalamud.GameData.GetSubrowExcelSheet<CollectablesShopItem>();
        _collectableShopItemIds = shopSheet == null
            ? new HashSet<uint>()
            : shopSheet.SelectMany(sheet => sheet)
                .Select(row => row.Item.RowId)
                .Where(itemId => itemId != 0)
                .ToHashSet();
        return _collectableShopItemIds;
    }

    private static HashSet<uint> GetFishItemIds()
    {
        if (_fishItemIds != null)
            return _fishItemIds;

        var fishSheet = Dalamud.GameData.GetExcelSheet<FishParameter>();
        _fishItemIds = fishSheet == null
            ? new HashSet<uint>()
            : fishSheet.Select(row => row.Item.RowId)
                .Where(itemId => itemId != 0)
                .ToHashSet();
        return _fishItemIds;
    }

    private static unsafe (int UsedSlots, int TotalSlots) GetInventoryUsage()
    {
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
            return (0, 0);

        var usedSlots = 0;
        var totalSlots = 0;
        foreach (var inventoryType in global::GatherBuddy.AutoGather.AutoGather.InventoryTypes)
        {
            var container = inventoryManager->GetInventoryContainer(inventoryType);
            if (container == null || !container->IsLoaded)
                continue;

            totalSlots += (int)container->Size;
            for (var slotIndex = 0; slotIndex < container->Size; slotIndex++)
            {
                var slot = container->GetInventorySlot(slotIndex);
                if (slot == null || slot->ItemId == 0)
                    continue;

                usedSlots++;
            }
        }

        return (usedSlots, totalSlots);
    }
}
