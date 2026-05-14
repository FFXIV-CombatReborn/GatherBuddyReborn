using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace GatherBuddy.AutoGather.Collectables;

public static class ItemJobResolver
{
    private static readonly object JobIdCacheLock = new();
    private static Dictionary<uint, int>? _jobIdsByItemId;

    public static void ResetCache()
    {
        lock (JobIdCacheLock)
            _jobIdsByItemId = null;
    }

    public static int GetJobIdForItem(uint itemId, IDataManager data)
    {
        if (itemId == 0)
            return -1;

        var jobIdsByItemId = GetJobIdsByItemId(data);
        return jobIdsByItemId.TryGetValue(itemId, out var jobId) ? jobId : -1;
    }

    private static Dictionary<uint, int> GetJobIdsByItemId(IDataManager data)
    {
        if (_jobIdsByItemId != null)
            return _jobIdsByItemId;

        lock (JobIdCacheLock)
        {
            if (_jobIdsByItemId != null)
                return _jobIdsByItemId;

            _jobIdsByItemId = BuildJobIdsByItemId(data);
            return _jobIdsByItemId;
        }
    }

    private static Dictionary<uint, int> BuildJobIdsByItemId(IDataManager data)
    {
        var jobIdsByItemId = new Dictionary<uint, int>();

        AddCraftingJobIds(data, jobIdsByItemId);
        AddFishingJobIds(data, jobIdsByItemId);
        AddSpearfishingJobIds(data, jobIdsByItemId);
        AddGatheringJobIds(data, jobIdsByItemId);
        return jobIdsByItemId;
    }

    private static void AddCraftingJobIds(IDataManager data, IDictionary<uint, int> jobIdsByItemId)
    {
        var recipeSheet = data.GetExcelSheet<Recipe>();
        if (recipeSheet == null)
            return;

        foreach (var recipe in recipeSheet)
        {
            var itemId = recipe.ItemResult.RowId;
            if (itemId != 0 && !jobIdsByItemId.ContainsKey(itemId))
                jobIdsByItemId[itemId] = (int)recipe.CraftType.RowId;
        }
    }

    private static void AddFishingJobIds(IDataManager data, IDictionary<uint, int> jobIdsByItemId)
    {
        var fishSheet = data.GetExcelSheet<FishParameter>();
        if (fishSheet == null)
            return;

        foreach (var fish in fishSheet)
        {
            var itemId = fish.Item.RowId;
            if (itemId != 0)
                jobIdsByItemId[itemId] = 10;
        }
    }

    private static void AddSpearfishingJobIds(IDataManager data, IDictionary<uint, int> jobIdsByItemId)
    {
        var spearSheet = data.GetExcelSheet<SpearfishingItem>();
        if (spearSheet == null)
            return;

        foreach (var spear in spearSheet)
        {
            var itemId = spear.Item.RowId;
            if (itemId != 0)
                jobIdsByItemId[itemId] = 10;
        }
    }

    private static void AddGatheringJobIds(IDataManager data, IDictionary<uint, int> jobIdsByItemId)
    {
        var gatheringItemSheet = data.GetExcelSheet<GatheringItem>();
        var gatheringPointBaseSheet = data.GetExcelSheet<GatheringPointBase>();
        if (gatheringItemSheet == null || gatheringPointBaseSheet == null)
            return;

        var itemIdsByGatheringItemId = gatheringItemSheet
            .Where(item => item.RowId != 0 && item.Item.RowId != 0)
            .ToDictionary(item => item.RowId, item => item.Item.RowId);

        foreach (var gatheringPointBase in gatheringPointBaseSheet)
        {
            var jobId = MapTypeToJob(gatheringPointBase);
            if (jobId < 0)
                continue;

            foreach (var gatheringItem in gatheringPointBase.Item)
            {
                if (gatheringItem.RowId != 0 && itemIdsByGatheringItemId.TryGetValue(gatheringItem.RowId, out var itemId))
                    jobIdsByItemId[itemId] = jobId;
            }
        }

        var gatheringItemPointSheet = data.GetSubrowExcelSheet<GatheringItemPoint>();
        if (gatheringItemPointSheet == null)
            return;

        foreach (var gatheringPoint in gatheringItemPointSheet.SelectMany(sheet => sheet))
        {
            if (!itemIdsByGatheringItemId.TryGetValue(gatheringPoint.RowId, out var itemId))
                continue;

            var gp = gatheringPoint.GatheringPoint.ValueNullable;
            if (gp == null)
                continue;

            var baseRow = gp.Value.GatheringPointBase.ValueNullable;
            if (baseRow == null)
                continue;

            var jobId = MapTypeToJob(baseRow.Value);
            if (jobId >= 0)
                jobIdsByItemId[itemId] = jobId;
        }
    }

    private static int MapTypeToJob(GatheringPointBase b)
    {
        var type = b.GatheringType.ValueNullable;
        if (type == null) return -1;
        var id = type.Value.RowId;
        return id switch
        {
            0 or 1 or 6 => 8,  // Miner
            2 or 3 or 5 => 9,  // Botanist
            4 or 7      => 10, // Fisher
            _           => -1,
        };
    }
}
