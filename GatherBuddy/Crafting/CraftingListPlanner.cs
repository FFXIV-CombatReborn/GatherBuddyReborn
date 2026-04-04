using System;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace GatherBuddy.Crafting;

public sealed class CraftingListPlan
{
    public List<CraftingListItem> OriginalRecipes { get; } = new();
    public List<CraftingListItem> Recipes { get; } = new();
    public Dictionary<uint, int> Materials { get; } = new();
    public Dictionary<uint, int> Precrafts { get; } = new();
    public Dictionary<uint, IngredientQualityDemand> IngredientDemands { get; } = new();
    public Dictionary<uint, int> RetainerConsumedCraftables { get; } = new();
}

public readonly record struct CraftingListPlannerOptions(bool UseRetainerCraftableAvailability = false);

public static class CraftingListPlanner
{
    public static CraftingListPlan Build(CraftingListDefinition list, CraftingListPlannerOptions options = default)
        => new Planner(list, options).Build();

    private sealed class Planner
    {
        private readonly CraftingListDefinition _list;
        private readonly CraftingListPlan _plan = new();
        private readonly AvailabilityLedger _availability;
        private readonly bool _useRetainers;
        private readonly Dictionary<uint, CraftingListItem> _originalRecipeLookup;

        public Planner(CraftingListDefinition list, CraftingListPlannerOptions options)
        {
            _list = list;
            _useRetainers = options.UseRetainerCraftableAvailability;
            _availability = new AvailabilityLedger(_useRetainers);
            _originalRecipeLookup = list.Recipes
                .GroupBy(item => item.RecipeId)
                .ToDictionary(group => group.Key, group => group.First());
        }

        public CraftingListPlan Build()
        {
            foreach (var item in _list.Recipes)
            {
                if (item.Options.Skipping || item.Quantity <= 0)
                    continue;

                var recipe = RecipeManager.GetRecipe(item.RecipeId);
                if (!recipe.HasValue)
                    continue;

                PlanOriginalRecipe(item, recipe.Value);
            }

            return _plan;
        }

        private void PlanOriginalRecipe(CraftingListItem item, Recipe recipe)
        {
            var resultItemId = recipe.ItemResult.RowId;
            var requestedItemCount = item.Quantity * (int)recipe.AmountResult;
            var remainingItemCount = requestedItemCount;

            remainingItemCount -= _availability.ConsumePlanned(resultItemId, remainingItemCount);

            if (_list.SkipIfEnough && _list.SkipFinalIfEnough)
                remainingItemCount -= _availability.ConsumeInventory(resultItemId, remainingItemCount);

            if (remainingItemCount <= 0)
                return;

            var craftCount = DivideRoundUp(remainingItemCount, (int)recipe.AmountResult);
            AddRecipe(_plan.OriginalRecipes, item.RecipeId, craftCount, true);
            AddRecipe(_plan.Recipes, item.RecipeId, craftCount, true);

            var surplus = craftCount * (int)recipe.AmountResult - remainingItemCount;
            _availability.AddPlanned(resultItemId, surplus);

            PlanIngredients(recipe, craftCount, true);
        }

        private void PlanIngredients(Recipe recipe, int craftCount, bool isOriginalRecipe)
        {
            var qualityPolicy = ResolveQualityPolicy(recipe, isOriginalRecipe);
            foreach (var (itemId, _) in RecipeManager.GetIngredients(recipe))
            {
                var itemDemand = qualityPolicy.GetDemand(itemId).Scale(craftCount);
                AddDemand(_plan.IngredientDemands, itemId, itemDemand);
                var subRecipe = RecipeManager.GetRecipeForItem(itemId);
                if (!subRecipe.HasValue)
                {
                    AddCount(_plan.Materials, itemId, itemDemand.Total);
                    continue;
                }

                AddCount(_plan.Precrafts, itemId, itemDemand.Total);
                PlanPrecraftDemand(subRecipe.Value, itemDemand);
            }
        }

        private void PlanPrecraftDemand(Recipe recipe, IngredientQualityDemand itemDemand)
        {
            var resultItemId = recipe.ItemResult.RowId;
            var remainingDemand = _availability.ConsumePlanned(resultItemId, itemDemand);

            if (_list.SkipIfEnough)
            {
                remainingDemand = _availability.ConsumeInventory(resultItemId, remainingDemand);

                if (_useRetainers)
                {
                    var remainingAfterRetainers = _availability.ConsumeRetainers(resultItemId, remainingDemand);
                    var fromRetainers = remainingDemand.Total - remainingAfterRetainers.Total;
                    remainingDemand = remainingAfterRetainers;
                    AddCount(_plan.RetainerConsumedCraftables, resultItemId, fromRetainers);
                }
            }

            if (remainingDemand.Total <= 0)
                return;

            var craftCount = DivideRoundUp(remainingDemand.Total, (int)recipe.AmountResult);
            AddRecipe(_plan.Recipes, recipe.RowId, craftCount, false);

            var surplus = craftCount * (int)recipe.AmountResult - remainingDemand.Total;
            _availability.AddPlanned(resultItemId, surplus);

            PlanIngredients(recipe, craftCount, false);
        }

        private CraftingQualityPolicy ResolveQualityPolicy(Recipe recipe, bool isOriginalRecipe)
        {
            var settings = isOriginalRecipe
                ? _originalRecipeLookup.GetValueOrDefault(recipe.RowId)?.CraftSettings
                : _list.PrecraftCraftSettings.GetValueOrDefault(recipe.RowId);
            var overrideMode = _list.GetQualityOverrideMode(recipe, isOriginalRecipe);
            return CraftingQualityPolicyResolver.Resolve(recipe, settings, overrideMode);
        }

        private static void AddRecipe(List<CraftingListItem> target, uint recipeId, int craftCount, bool isOriginalRecipe)
        {
            if (craftCount <= 0)
                return;

            var existing = target.FirstOrDefault(item => item.RecipeId == recipeId && item.IsOriginalRecipe == isOriginalRecipe);
            if (existing != null)
            {
                existing.Quantity += craftCount;
                return;
            }

            target.Add(new CraftingListItem(recipeId, craftCount)
            {
                IsOriginalRecipe = isOriginalRecipe,
            });
        }

        private static void AddCount(Dictionary<uint, int> target, uint itemId, int amount)
        {
            if (amount <= 0)
                return;

            target[itemId] = target.GetValueOrDefault(itemId) + amount;
        }

        private static void AddDemand(Dictionary<uint, IngredientQualityDemand> target, uint itemId, IngredientQualityDemand demand)
        {
            if (demand.Total <= 0)
                return;

            target[itemId] = target.TryGetValue(itemId, out var existing)
                ? existing.Add(demand)
                : demand;
        }

        private static int DivideRoundUp(int value, int divisor)
            => (int)Math.Ceiling((double)value / divisor);
    }

    private sealed class AvailabilityLedger
    {
        private readonly bool _useRetainers;
        private readonly Dictionary<uint, int> _plannedAvailable = new();
        private readonly Dictionary<uint, (int NQ, int HQ)> _inventoryAvailable = new();
        private readonly Dictionary<uint, (int NQ, int HQ)> _retainerAvailable = new();

        public AvailabilityLedger(bool useRetainers)
        {
            _useRetainers = useRetainers;
        }

        public int ConsumePlanned(uint itemId, int requested)
            => Consume(_plannedAvailable, itemId, requested, _ => 0);

        public int ConsumeInventory(uint itemId, int requested)
            => ConsumeTotal(_inventoryAvailable, itemId, requested, GetInventorySplitCounts);

        public IngredientQualityDemand ConsumePlanned(uint itemId, IngredientQualityDemand demand)
        {
            if (demand.Total <= 0)
                return demand;

            var available = _plannedAvailable.GetValueOrDefault(itemId);
            if (available <= 0)
                return demand;

            var remaining = demand.ConsumeUnknownQuality(available, out var consumed);
            _plannedAvailable[itemId] = Math.Max(0, available - consumed);
            return remaining;
        }

        public IngredientQualityDemand ConsumeInventory(uint itemId, IngredientQualityDemand demand)
            => ConsumeSplit(_inventoryAvailable, itemId, demand, GetInventorySplitCounts);

        public IngredientQualityDemand ConsumeRetainers(uint itemId, IngredientQualityDemand demand)
            => _useRetainers
                ? ConsumeSplit(_retainerAvailable, itemId, demand, GetRetainerSplitCounts)
                : demand;

        public void AddPlanned(uint itemId, int amount)
        {
            if (amount <= 0)
                return;

            _plannedAvailable[itemId] = _plannedAvailable.GetValueOrDefault(itemId) + amount;
        }

        private static int Consume(Dictionary<uint, int> ledger, uint itemId, int requested, Func<uint, int> valueFactory)
        {
            if (requested <= 0)
                return 0;

            if (!ledger.TryGetValue(itemId, out var available))
            {
                available = valueFactory(itemId);
                ledger[itemId] = available;
            }

            if (available <= 0)
                return 0;

            var consumed = Math.Min(requested, available);
            ledger[itemId] = available - consumed;
            return consumed;
        }

        private static int ConsumeTotal(
            Dictionary<uint, (int NQ, int HQ)> ledger,
            uint itemId,
            int requested,
            Func<uint, (int NQ, int HQ)> valueFactory)
        {
            if (requested <= 0)
                return 0;

            if (!ledger.TryGetValue(itemId, out var available))
            {
                available = valueFactory(itemId);
                ledger[itemId] = available;
            }

            var totalAvailable = available.NQ + available.HQ;
            if (totalAvailable <= 0)
                return 0;

            var consumed = Math.Min(requested, totalAvailable);
            var remainingNQ = available.NQ;
            var remainingHQ = available.HQ;
            var consumeNQ = Math.Min(consumed, remainingNQ);
            remainingNQ -= consumeNQ;
            remainingHQ = Math.Max(0, remainingHQ - (consumed - consumeNQ));
            ledger[itemId] = (remainingNQ, remainingHQ);
            return consumed;
        }

        private static IngredientQualityDemand ConsumeSplit(
            Dictionary<uint, (int NQ, int HQ)> ledger,
            uint itemId,
            IngredientQualityDemand demand,
            Func<uint, (int NQ, int HQ)> valueFactory)
        {
            if (demand.Total <= 0)
                return demand;

            if (!ledger.TryGetValue(itemId, out var available))
            {
                available = valueFactory(itemId);
                ledger[itemId] = available;
            }

            if (available.NQ <= 0 && available.HQ <= 0)
                return demand;

            var remaining = demand.ConsumeSplit(available.NQ, available.HQ, out var consumedNQ, out var consumedHQ);
            ledger[itemId] = (Math.Max(0, available.NQ - consumedNQ), Math.Max(0, available.HQ - consumedHQ));
            return remaining;
        }

        private static unsafe (int NQ, int HQ) GetInventorySplitCounts(uint itemId)
        {
            try
            {
                var inventory = InventoryManager.Instance();
                if (inventory == null)
                    return (0, 0);

                return (
                    (int)inventory->GetInventoryItemCount(itemId, false, false, false),
                    (int)inventory->GetInventoryItemCount(itemId, true, false, false));
            }
            catch
            {
                return (0, 0);
            }
        }

        private static (int NQ, int HQ) GetRetainerSplitCounts(uint itemId)
        {
            try
            {
                var snapshot = RetainerItemQuery.CreateSnapshot(new[] { itemId });
                return (snapshot.GetCountNQ(itemId), snapshot.GetCountHQ(itemId));
            }
            catch
            {
                return (0, 0);
            }
        }
    }
}
