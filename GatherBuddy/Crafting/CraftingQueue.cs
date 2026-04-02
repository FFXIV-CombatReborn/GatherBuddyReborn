using System.Collections.Generic;
using System.Linq;
using Lumina.Excel.Sheets;

namespace GatherBuddy.Crafting;

public class ListItemOptions
{
    public bool Skipping { get; set; } = false;
    public bool NQOnly { get; set; } = false;
}

public enum ConsumableOverrideMode
{
    Inherit,
    None,
    Specific
}

public class ConsumableOverride
{
    public ConsumableOverrideMode Mode { get; set; } = ConsumableOverrideMode.Inherit;
    public uint? ItemId { get; set; }
    public bool HQ { get; set; }

    public ConsumableOverride Clone()
        => new()
        {
            Mode = Mode,
            ItemId = ItemId,
            HQ = HQ,
        };
}

public class CraftingListConsumableSettings
{
    public uint? FoodItemId { get; set; }
    public bool FoodHQ { get; set; }
    public uint? MedicineItemId { get; set; }
    public bool MedicineHQ { get; set; }
    public uint? ManualItemId { get; set; }
    public uint? SquadronManualItemId { get; set; }

    public CraftingListConsumableSettings Clone()
        => new()
        {
            FoodItemId = FoodItemId,
            FoodHQ = FoodHQ,
            MedicineItemId = MedicineItemId,
            MedicineHQ = MedicineHQ,
            ManualItemId = ManualItemId,
            SquadronManualItemId = SquadronManualItemId,
        };
}

public class CraftingListConsumableOverrides
{
    public ConsumableOverride Food { get; set; } = new();
    public ConsumableOverride Medicine { get; set; } = new();
    public ConsumableOverride Manual { get; set; } = new();
    public ConsumableOverride SquadronManual { get; set; } = new();

    public bool HasAnyOverrides()
        => Food.Mode != ConsumableOverrideMode.Inherit
            || Medicine.Mode != ConsumableOverrideMode.Inherit
            || Manual.Mode != ConsumableOverrideMode.Inherit
            || SquadronManual.Mode != ConsumableOverrideMode.Inherit;

    public CraftingListConsumableOverrides Clone()
        => new()
        {
            Food = Food.Clone(),
            Medicine = Medicine.Clone(),
            Manual = Manual.Clone(),
            SquadronManual = SquadronManual.Clone(),
        };
}

public class IngredientPreference
{
    public uint ItemId { get; set; }
    public int DesiredHQ { get; set; }
}

public class CraftingListItem
{
    public uint RecipeId { get; set; }
    public int Quantity { get; set; }
    public ListItemOptions Options { get; set; } = new();
    public Dictionary<uint, int> IngredientPreferences { get; set; } = new();
    public CraftingListConsumableOverrides ConsumableOverrides { get; set; } = new();
    public bool IsOriginalRecipe { get; set; } = false;
    public RecipeCraftSettings? CraftSettings { get; set; }

    public CraftingListItem(uint recipeId, int quantity)
    {
        RecipeId = recipeId;
        Quantity = quantity;
    }
}

public class CraftingListQueue
{
    public List<CraftingListItem> Recipes { get; set; } = new();
    public List<CraftingListItem> OriginalRecipes { get; set; } = new();
    public List<uint> ExpandedList { get; set; } = new();

    public void BuildExpandedList()
    {
        ExpandedList.Clear();
        foreach (var recipe in Recipes)
        {
            ExpandedList.AddRange(Enumerable.Repeat(recipe.RecipeId, recipe.Quantity));
        }
    }

    public void AddRecipe(uint recipeId, int quantity, bool isOriginalRecipe)
    {
        var existing = Recipes.FirstOrDefault(r => r.RecipeId == recipeId && r.IsOriginalRecipe == isOriginalRecipe);
        if (existing != null)
        {
            existing.Quantity += quantity;
        }
        else
        {
            Recipes.Add(new CraftingListItem(recipeId, quantity)
            {
                IsOriginalRecipe = isOriginalRecipe,
            });
        }
    }

    public void AddFromList(IEnumerable<CraftingListItem> items, bool skipIfEnough = false, bool skipFinalIfEnough = false)
    {
        var pendingCraftCounts = new Dictionary<uint, int>();

        foreach (var item in items)
        {
            var recipe = RecipeManager.GetRecipe(item.RecipeId);
            if (recipe == null)
                continue;

            var quantity = skipFinalIfEnough
                ? ComputeAdjustedQuantity(recipe.Value, item.Quantity)
                : item.Quantity;

            if (quantity <= 0)
                continue;

            OriginalRecipes.Add(new CraftingListItem(item.RecipeId, quantity)
            {
                IsOriginalRecipe = true,
            });
            AddRecipe(item.RecipeId, quantity, true);
            pendingCraftCounts[item.RecipeId] = pendingCraftCounts.GetValueOrDefault(item.RecipeId) + quantity;
        }

        while (pendingCraftCounts.Count > 0)
        {
            var nextLevelNeeds = new Dictionary<uint, int>();

            foreach (var (recipeId, craftCount) in pendingCraftCounts)
            {
                var recipe = RecipeManager.GetRecipe(recipeId);
                if (recipe != null)
                    CollectDirectSubRecipeNeeds(recipe.Value, craftCount, nextLevelNeeds, skipIfEnough);
            }

            pendingCraftCounts.Clear();

            foreach (var (subRecipeId, itemsNeeded) in nextLevelNeeds)
            {
                var subRecipe = RecipeManager.GetRecipe(subRecipeId);
                if (subRecipe == null)
                    continue;

                var craftCount = (int)System.Math.Ceiling((double)itemsNeeded / subRecipe.Value.AmountResult);
                AddRecipe(subRecipeId, craftCount, false);
                pendingCraftCounts[subRecipeId] = craftCount;
            }
        }
    }

    private static unsafe int ComputeAdjustedQuantity(Recipe recipe, int requestedCrafts)
    {
        try
        {
            var inventory = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
            if (inventory == null)
                return requestedCrafts;
            var itemId = recipe.ItemResult.RowId;
            var amountPerCraft = recipe.AmountResult;
            var targetItems = requestedCrafts * amountPerCraft;
            var inInventory = (int)(inventory->GetInventoryItemCount(itemId, false, false, false)
                                  + inventory->GetInventoryItemCount(itemId, true, false, false));
            var stillNeeded = System.Math.Max(0, targetItems - inInventory);
            return (int)System.Math.Ceiling((double)stillNeeded / amountPerCraft);
        }
        catch
        {
            return requestedCrafts;
        }
    }

    private unsafe void CollectDirectSubRecipeNeeds(
        Recipe recipe, int craftCount, Dictionary<uint, int> subRecipeNeeds, bool skipIfEnough)
    {
        var ingredients = RecipeManager.GetIngredients(recipe);
        foreach (var (itemId, amountPerCraft) in ingredients)
        {
            var subRecipe = RecipeManager.GetRecipeForItem(itemId);
            if (!subRecipe.HasValue)
                continue;

            var itemsNeeded = amountPerCraft * craftCount;

            if (skipIfEnough)
            {
                try
                {
                    var inventory = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
                    if (inventory != null)
                    {
                        var resultItemId = subRecipe.Value.ItemResult.RowId;
                        var nqCount = inventory->GetInventoryItemCount(resultItemId, false, false, false);
                        var hqCount = inventory->GetInventoryItemCount(resultItemId, true, false, false);
                        var inInventory = (int)(nqCount + hqCount);

                        if (inInventory >= itemsNeeded)
                            continue;

                        itemsNeeded -= inInventory;
                    }
                }
                catch { }
            }

            subRecipeNeeds[subRecipe.Value.RowId] = subRecipeNeeds.GetValueOrDefault(subRecipe.Value.RowId) + itemsNeeded;
        }
    }

    public void Clear()
    {
        Recipes.Clear();
        OriginalRecipes.Clear();
        ExpandedList.Clear();
    }
}
