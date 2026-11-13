using Dalamud.Game.Inventory;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace GatherBuddy.Crafting;

public static class RecipeExtensions
{
    public static List<(Item Item, int Amount)> Ingredients(this Recipe recipe)
    {
        var result = new List<(Item, int)>();

        for (var i = 0; i < recipe.Ingredient.Count; i++)
        {
            try
            {
                var item = recipe.Ingredient[i].Value;
                var amount = recipe.AmountIngredient[i];
                if (item.RowId > 0)
                {
                    result.Add((item, amount));
                }
            }
            catch { }
        }
        return result;
    }
}

public static class RecipeManager
{
    private static readonly Lazy<Dictionary<uint, List<Recipe>>> _recipesByItemId = new(BuildRecipeIndex, LazyThreadSafetyMode.ExecutionAndPublication);

    public static Recipe? GetRecipe(uint recipeId)
    {
        var sheet = Dalamud.GameData.GetExcelSheet<Recipe>();
        if (sheet != null && sheet.TryGetRow(recipeId, out var row))
        {
            return row;
        }

        return null;
    }

    public static Recipe? GetRecipeForItem(uint itemId)
    {
        var recipes = GetRecipesForItem(itemId);
        return recipes.Count > 0 ? recipes[0] : (Recipe?)null;
    }

    public static IReadOnlyList<Recipe> GetRecipesForItem(uint itemId)
    {
        var recipesByItemId = _recipesByItemId.Value;
        return recipesByItemId.GetValueOrDefault(itemId) ?? (IReadOnlyList<Recipe>)[];
    }

    private static Dictionary<uint, List<Recipe>> BuildRecipeIndex()
    {
        GatherBuddy.Log.Debug("[RecipeManager] Building recipe index");
        var sheet = Dalamud.GameData.GetExcelSheet<Recipe>();
        var recipesByItemId = new Dictionary<uint, List<Recipe>>();
        if (sheet == null)
        {
            GatherBuddy.Log.Debug("[RecipeManager] Recipe sheet unavailable while building recipe index");
            return recipesByItemId;
        }
        foreach (var recipe in sheet)
        {
            if (recipe.ItemResult.RowId == 0)
            {
                continue;
            }

            if (!recipesByItemId.TryGetValue(recipe.ItemResult.RowId, out var list))
            {
                list = [];
                recipesByItemId[recipe.ItemResult.RowId] = list;
            }
            list.Add(recipe);
        }
        GatherBuddy.Log.Debug($"[RecipeManager] Built recipe index: {recipesByItemId.Count} distinct result items");
        return recipesByItemId;
    }

    public static List<(uint itemId, int amount)> GetIngredients(Recipe recipe)
    {
        var result = new List<(uint, int)>();
        foreach (var (item, amount) in recipe.Ingredients())
        {
            if (item.RowId > 0)
            {
                result.Add((item.RowId, amount));
            }
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
                {
                    resolved[itemId] += actualAmount;
                }
                else
                {
                    resolved[itemId] = actualAmount;
                }
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
            {
                missing[itemId] = needed - haveCount;
            }
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
            var count = 0;
            foreach (var invType in inventories)
            {
                var items = Dalamud.GameInventory.GetInventoryItems(invType);
                foreach (var item in items)
                {
                    if (item.ItemId == itemId)
                    {
                        count += (int)item.Quantity;
                    }
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

    public static List<Recipe> FindByItemName(string name)
    {
        var sheet = Dalamud.GameData.GetExcelSheet<Recipe>();
        if (sheet == null)
        {
            return [];
        }

        var exact = sheet
            .Where(r => r.ItemResult.RowId > 0 &&
                        r.ItemResult.Value.Name.ExtractText().Equals(name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (exact.Count > 0)
        {
            return exact;
        }

        return [.. sheet
            .Where(r => r.ItemResult.RowId > 0 &&
                        r.ItemResult.Value.Name.ExtractText().Contains(name, StringComparison.OrdinalIgnoreCase))
            .Take(10)];
    }
}
