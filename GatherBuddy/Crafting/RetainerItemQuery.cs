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

    public static int GetTotalCount(uint itemId)
    {
        if (!AllaganTools.Enabled)
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
}

internal sealed class RetainerItemSnapshot
{
    public static RetainerItemSnapshot Empty { get; } = new([]);

    private readonly Dictionary<uint, (int NQ, int HQ)> _counts = new();

    public RetainerItemSnapshot(IEnumerable<uint> itemIds)
    {
        if (!AllaganTools.Enabled)
            return;

        foreach (var itemId in itemIds.Where(id => id > 0).Distinct())
        {
            _counts[itemId] = QuerySplitCounts(itemId);
        }
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

    private static unsafe (int NQ, int HQ) QuerySplitCounts(uint itemId)
    {
        try
        {
            var retainerMgr = RetainerManager.Instance();
            if (retainerMgr == null)
                return (0, 0);

            var totalNQ = 0;
            var totalHQ = 0;
            var retainerCount = retainerMgr->GetRetainerCount();

            for (uint i = 0; i < retainerCount; i++)
            {
                var retainer = retainerMgr->GetRetainerBySortedIndex(i);
                if (retainer == null || retainer->RetainerId == 0)
                    continue;

                var retainerId = retainer->RetainerId;
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

            return (Math.Max(0, totalNQ), Math.Max(0, totalHQ));
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Debug($"[RetainerItemQuery] Failed to query retainer split counts for item {itemId}: {ex.Message}");
            return (0, 0);
        }
    }
}
