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

    public void AddRecipe(uint recipeId, int quantity)
    {
        var existing = Recipes.FirstOrDefault(r => r.RecipeId == recipeId);
        if (existing != null)
        {
            existing.Quantity += quantity;
        }
        else
        {
            Recipes.Add(new CraftingListItem(recipeId, quantity));
        }
    }

    public void AddRecipeWithPrecrafts(uint recipeId, int quantity, bool skipIfEnough = false)
    {
        var recipe = RecipeManager.GetRecipe(recipeId);
        if (recipe == null)
            return;

        OriginalRecipes.Add(new CraftingListItem(recipeId, quantity));
        var neededAmounts = new Dictionary<uint, int>();
        CollectIngredientsNeeded(recipe.Value, quantity, neededAmounts, skipIfEnough);
        
        foreach (var kvp in neededAmounts)
        {
            var subRecipe = RecipeManager.GetRecipe(kvp.Key);
            if (subRecipe != null)
            {
                var quantityToCraft = (int)System.Math.Ceiling((double)kvp.Value / subRecipe.Value.AmountResult);
                AddRecipe(kvp.Key, quantityToCraft);
            }
        }
        
        AddRecipe(recipeId, quantity);
    }

    private unsafe void CollectIngredientsNeeded(Recipe recipe, int multiplier, Dictionary<uint, int> neededAmounts, bool skipIfEnough = false)
    {
        var ingredients = RecipeManager.GetIngredients(recipe);
        
        foreach (var (itemId, amount) in ingredients)
        {
            var subRecipe = RecipeManager.GetRecipeForItem(itemId);
            if (subRecipe.HasValue)
            {
                var actualAmount = amount * multiplier;
                var quantityNeeded = actualAmount;
                
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
                            var totalInInventory = nqCount + hqCount;
                            
                            if (totalInInventory >= actualAmount)
                            {
                                continue;
                            }
                            
                            quantityNeeded = actualAmount - totalInInventory;
                        }
                    }
                    catch { }
                }
                
                if (neededAmounts.ContainsKey(subRecipe.Value.RowId))
                    neededAmounts[subRecipe.Value.RowId] += quantityNeeded;
                else
                    neededAmounts[subRecipe.Value.RowId] = quantityNeeded;
                
                CollectIngredientsNeeded(subRecipe.Value, quantityNeeded, neededAmounts, skipIfEnough);
            }
        }
    }

    public void Clear()
    {
        Recipes.Clear();
        OriginalRecipes.Clear();
        ExpandedList.Clear();
    }
}
