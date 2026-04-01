using System;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;
using GatherBuddy.Plugin;

namespace GatherBuddy.Crafting;

internal static class RetainerItemQuery
{
    private static readonly uint[] RetainerInventoryTypes =
    [
        (uint)InventoryType.RetainerCrystals,
        (uint)InventoryType.RetainerPage1,
        (uint)InventoryType.RetainerPage2,
        (uint)InventoryType.RetainerPage3,
        (uint)InventoryType.RetainerPage4,
        (uint)InventoryType.RetainerPage5,
        (uint)InventoryType.RetainerPage6,
        (uint)InventoryType.RetainerPage7,
    ];

    public static bool IsReady
        => AllaganTools.Enabled && AllaganTools.IsInitialized();

    public static int GetTotalCount(uint itemId)
    {
        if (!IsReady)
            return 0;

        try
        {
            return (int)AllaganTools.ItemCountOwned(itemId, true, RetainerInventoryTypes);
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Debug($"[RetainerItemQuery] Failed to query retainer total for item {itemId}: {ex.Message}");
            return 0;
        }
    }

    public static RetainerItemSnapshot CreateSnapshot(IEnumerable<uint> itemIds)
        => new(itemIds);

    internal static HashSet<ulong> GetOwnedRetainerIds()
    {
        if (!IsReady)
            return [];

        try
        {
            return AllaganTools.GetCharactersOwnedByActive(false)
                .Where(id => id != 0)
                .ToHashSet();
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Debug($"[RetainerItemQuery] Failed to query owned retainer ids: {ex.Message}");
            return [];
        }
    }
}

internal sealed class RetainerItemSnapshot
{
    public static RetainerItemSnapshot Empty { get; } = new([]);

    private readonly Dictionary<uint, (int NQ, int HQ)> _counts = new();
    public bool IsComplete { get; }

    public RetainerItemSnapshot(IEnumerable<uint> itemIds)
    {
        if (!RetainerItemQuery.IsReady)
        {
            IsComplete = false;
            return;
        }

        var isComplete = true;

        foreach (var itemId in itemIds.Where(id => id > 0).Distinct())
        {
            var (counts, itemComplete) = QuerySplitCounts(itemId);
            _counts[itemId] = counts;
            isComplete &= itemComplete;
        }

        IsComplete = isComplete;
    }

    public int GetCountNQ(uint itemId)
        => _counts.GetValueOrDefault(itemId).NQ;

    public int GetCountHQ(uint itemId)
        => _counts.GetValueOrDefault(itemId).HQ;

    public int GetTotalCount(uint itemId)
    {
        var counts = _counts.GetValueOrDefault(itemId);
        return counts.NQ + counts.HQ;
    }

    private static ((int NQ, int HQ) Counts, bool IsComplete) QuerySplitCounts(uint itemId)
    {
        try
        {

            var totalNQ = 0;
            var totalHQ = 0;
            foreach (var retainerId in RetainerItemQuery.GetOwnedRetainerIds())
            {
                for (uint page = 10000; page <= 10006; page++)
                {
                    var pageHQ = (int)AllaganTools.ItemCountHQ(itemId, retainerId, page);
                    totalHQ += pageHQ;
                    totalNQ += (int)AllaganTools.ItemCount(itemId, retainerId, page) - pageHQ;
                }

                var crystalHQ = (int)AllaganTools.ItemCountHQ(itemId, retainerId, 12001);
                totalHQ += crystalHQ;
                totalNQ += (int)AllaganTools.ItemCount(itemId, retainerId, 12001) - crystalHQ;
            }
            var totalOwned = RetainerItemQuery.GetTotalCount(itemId);
            var splitTotal = totalNQ + totalHQ;
            var isComplete = true;
            if (splitTotal < totalOwned)
            {
                totalNQ += totalOwned - splitTotal;
                isComplete = false;
                GatherBuddy.Log.Debug($"[RetainerItemQuery] Split retainer counts incomplete for item {itemId}; using pooled fallback for {totalOwned - splitTotal} item(s)");
            }

            return ((Math.Max(0, totalNQ), Math.Max(0, totalHQ)), isComplete);
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Debug($"[RetainerItemQuery] Failed to query retainer split counts for item {itemId}: {ex.Message}");
            return ((0, 0), false);
        }
    }
}
