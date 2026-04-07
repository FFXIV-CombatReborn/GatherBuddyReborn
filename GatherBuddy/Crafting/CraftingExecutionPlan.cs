using System.Collections.Generic;
using System.Linq;
using GatherBuddy.Plugin;

namespace GatherBuddy.Crafting;

public sealed class CraftingExecutionPlan
{
    private readonly CraftingListDefinition _planningSnapshot;
    private readonly bool _useRetainerCraftableAvailability;

    public int ListId { get; }
    public string ListName { get; }
    public int Version { get; private set; }
    public bool SkipIfEnough { get; }
    public bool SkipFinalIfEnough { get; }
    public bool RetainerRestock { get; }
    public CraftingListPlan ResolvedPlan { get; private set; }

    internal List<CraftingListItem> Queue { get; private set; } = [];
    internal List<CraftingListItem> OriginalRecipes { get; private set; } = [];
    internal Dictionary<uint, int> Materials { get; private set; } = [];
    internal Dictionary<uint, int> Precrafts { get; private set; } = [];
    internal Dictionary<uint, int> RetainerConsumedCraftables { get; private set; } = [];
    internal Dictionary<uint, IngredientQualityDemand> IngredientDemands { get; private set; } = [];
    internal CraftingListDefinition PlanningSnapshot => _planningSnapshot;

    public IReadOnlyList<CraftingListItem> QueueView => Queue;
    public IReadOnlyList<CraftingListItem> OriginalRecipesView => OriginalRecipes;
    public IReadOnlyDictionary<uint, int> MaterialsView => Materials;
    public IReadOnlyDictionary<uint, int> PrecraftsView => Precrafts;
    public IReadOnlyDictionary<uint, int> RetainerConsumedCraftablesView => RetainerConsumedCraftables;
    public IReadOnlyDictionary<uint, IngredientQualityDemand> IngredientDemandsView => IngredientDemands;

    private CraftingExecutionPlan(
        CraftingListDefinition planningSnapshot,
        bool useRetainerCraftableAvailability,
        CraftingListPlan resolvedPlan)
    {
        _planningSnapshot = planningSnapshot;
        _useRetainerCraftableAvailability = useRetainerCraftableAvailability;
        ListId = planningSnapshot.ID;
        ListName = planningSnapshot.Name;
        SkipIfEnough = planningSnapshot.SkipIfEnough;
        SkipFinalIfEnough = planningSnapshot.SkipFinalIfEnough;
        RetainerRestock = planningSnapshot.RetainerRestock;
        ApplyResolvedPlan(resolvedPlan);
    }

    public static CraftingExecutionPlan Create(CraftingListDefinition list)
    {
        var planningSnapshot = list.CreateRetainerPlanningSnapshot();
        var useRetainerCraftableAvailability = planningSnapshot.SkipIfEnough
            && planningSnapshot.RetainerRestock
            && AllaganTools.Enabled;
        var resolvedPlan = planningSnapshot.CreatePlan(useRetainerCraftableAvailability);
        return new CraftingExecutionPlan(planningSnapshot, useRetainerCraftableAvailability, resolvedPlan);
    }

    public bool MatchesList(int listId)
        => ListId == listId;

    public void RefreshForRetainerWithdrawal()
    {
        if (!_useRetainerCraftableAvailability)
            return;

        ApplyResolvedPlan(_planningSnapshot.CreatePlan(true));
    }

    public void RefreshFromCurrentInventory()
        => ApplyResolvedPlan(_planningSnapshot.CreatePlan(false));

    public Dictionary<uint, IngredientQualityDemand> BuildQualityTargetsForItems(IReadOnlyDictionary<uint, int> requestedItems)
    {
        var targets = requestedItems.Keys.ToDictionary(
            itemId => itemId,
            itemId => IngredientDemands.GetValueOrDefault(itemId));

        foreach (var (itemId, totalNeeded) in requestedItems)
        {
            var target = targets[itemId];
            if (target.Total < totalNeeded)
            {
                targets[itemId] = target.Add(IngredientQualityDemand.FromPreferHQ(totalNeeded - target.Total));
                continue;
            }

            if (target.Total > totalNeeded)
                targets[itemId] = target.ConsumeUnknownQuality(target.Total - totalNeeded, out _);
        }

        return targets;
    }

    private void ApplyResolvedPlan(CraftingListPlan resolvedPlan)
    {
        Version++;
        ResolvedPlan = resolvedPlan;
        Materials = new Dictionary<uint, int>(resolvedPlan.Materials);
        Precrafts = new Dictionary<uint, int>(resolvedPlan.Precrafts);
        RetainerConsumedCraftables = new Dictionary<uint, int>(resolvedPlan.RetainerConsumedCraftables);
        IngredientDemands = new Dictionary<uint, IngredientQualityDemand>(resolvedPlan.IngredientDemands);
        OriginalRecipes = resolvedPlan.OriginalRecipes
            .Select(item => new CraftingListItem(item.RecipeId, item.Quantity)
            {
                IsOriginalRecipe = true,
            })
            .ToList();
        Queue = CraftingListQueueBuilder.CreateExpandedQueue(_planningSnapshot, resolvedPlan);
    }
}
