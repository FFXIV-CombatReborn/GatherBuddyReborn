using System;
using System.Collections.Generic;
using Lumina.Excel.Sheets;

namespace GatherBuddy.Crafting;

public static class QualityCalculator
{
    public static int CalculateInitialQuality(Recipe recipe, Dictionary<uint, int> ingredientPreferences)
    {
        GatherBuddy.Log.Debug($"[QualityCalculator] Calculating quality for recipe {recipe.RowId}, preferences count: {ingredientPreferences?.Count ?? 0}");
        
        if (recipe.MaterialQualityFactor == 0)
        {
            GatherBuddy.Log.Debug($"[QualityCalculator] MaterialQualityFactor is 0, returning 0");
            return 0;
        }
        
        var ingredients = RecipeManager.GetIngredients(recipe);
        if (ingredients.Count == 0)
        {
            GatherBuddy.Log.Debug($"[QualityCalculator] No ingredients, returning 0");
            return 0;
        }
        
        long sumLevel = 0;
        long sumLevelHQ = 0;
        
        var itemSheet = Dalamud.GameData.GetExcelSheet<Item>();
        if (itemSheet == null)
            return 0;
        
        foreach (var (itemId, amount) in ingredients)
        {
            if (!itemSheet.TryGetRow(itemId, out var item))
                continue;
            
            if (!item.CanBeHq)
                continue;
            
            long itemLevel = item.LevelItem.RowId;
            sumLevel += itemLevel * amount;
            
            int numHQ = 0;
            if (ingredientPreferences != null && ingredientPreferences.TryGetValue(itemId, out var preferredHQ))
            {
                numHQ = Math.Min(preferredHQ, amount);
            }
            
            sumLevelHQ += itemLevel * numHQ;
        }
        
        if (sumLevel == 0)
            return 0;
        
        var lt = recipe.RecipeLevelTable.Value;
        long recipeMaxQuality = lt.Quality * recipe.QualityFactor / 100;
        long materialQualityFactor = recipe.MaterialQualityFactor;
        
        long startingQuality = (sumLevelHQ * recipeMaxQuality * materialQualityFactor / 100) / sumLevel;
        
        GatherBuddy.Log.Debug($"[QualityCalculator] Recipe {recipe.RowId}: sumLevel={sumLevel}, sumLevelHQ={sumLevelHQ}, " +
            $"recipeMaxQuality={recipeMaxQuality}, materialQualityFactor={materialQualityFactor}, " +
            $"startingQuality={startingQuality}");
        
        return (int)startingQuality;
    }
}
