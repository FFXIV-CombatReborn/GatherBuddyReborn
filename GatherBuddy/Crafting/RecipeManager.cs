using Dalamud.Game.Inventory;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using System.Collections.Generic;
using System.Linq;

namespace GatherBuddy.Crafting;

public static class RecipeExtensions
{
    public static List<(Item Item, int Amount)> Ingredients(this Recipe recipe)
    {
        var result = new List<(Item, int)>();
        
        for (int i = 0; i < recipe.Ingredient.Count; i++)
        {
            try
            {
                var item = recipe.Ingredient[i].Value;
                var amount = recipe.AmountIngredient[i];
                if (item.RowId > 0)
                    result.Add((item, amount));
            }
            catch { }
        }
        return result;
    }
}

public static class RecipeManager
{
    public static Recipe? GetRecipe(uint recipeId)
    {
        var sheet = Dalamud.GameData.GetExcelSheet<Recipe>();
        if (sheet != null && sheet.TryGetRow(recipeId, out var row))
            return row;
        return null;
    }

    public static Recipe? GetRecipeForItem(uint itemId)
    {
        var sheet = Dalamud.GameData.GetExcelSheet<Recipe>();
        if (sheet == null)
            return null;
        
        foreach (var recipe in sheet)
        {
            if (recipe.ItemResult.RowId == itemId)
                return recipe;
        }
        return null;
    }

    public static List<(uint itemId, int amount)> GetIngredients(Recipe recipe)
    {
        var result = new List<(uint, int)>();
        foreach (var (item, amount) in recipe.Ingredients())
        {
            if (item.RowId > 0)
                result.Add((item.RowId, amount));
        }
        return result;
    }

    public static Dictionary<uint, int> GetResolvedIngredients(Recipe recipe)
    {
        var resolved = new Dictionary<uint, int>();
        ResolveIngredientsRecursive(recipe, resolved, 1);
        return resolved;
    }

    private static void ResolveIngredientsRecursive(Recipe recipe, Dictionary<uint, int> resolved, int multiplier)
    {
        var ingredients = GetIngredients(recipe);

        foreach (var (itemId, amount) in ingredients)
        {
            var actualAmount = amount * multiplier;
            var subRecipe = GetRecipeForItem(itemId);
            if (subRecipe.HasValue)
            {
                var quantityToCraft = System.Math.Ceiling((double)actualAmount / subRecipe.Value.AmountResult);
                ResolveIngredientsRecursive(subRecipe.Value, resolved, (int)quantityToCraft);
            }
            else
            {
                if (resolved.ContainsKey(itemId))
                    resolved[itemId] += actualAmount;
                else
                    resolved[itemId] = actualAmount;
            }
        }
    }

    public static Dictionary<uint, int> GetMissingIngredients(Recipe recipe)
    {
        var missing = new Dictionary<uint, int>();
        var ingredients = GetResolvedIngredients(recipe);

        foreach (var (itemId, needed) in ingredients)
        {
            var haveCount = GetInventoryCount(itemId);
            if (haveCount < needed)
                missing[itemId] = needed - haveCount;
        }

        return missing;
    }

    private static int GetInventoryCount(uint itemId)
    {
        try
        {
            var inventories = new GameInventoryType[]
            {
                GameInventoryType.Inventory1, GameInventoryType.Inventory2,
                GameInventoryType.Inventory3, GameInventoryType.Inventory4
            };
            int count = 0;
            foreach (var invType in inventories)
            {
                var items = Dalamud.GameInventory.GetInventoryItems(invType);
                foreach (var item in items)
                {
                    if (item.ItemId == itemId)
                        count += (int)item.Quantity;
                }
            }
            return count;
        }
        catch
        {
            return 0;
        }
    }

    public static bool CanCraft(Recipe recipe)
    {
        var missing = GetMissingIngredients(recipe);
        return missing.Count == 0;
    }
}
