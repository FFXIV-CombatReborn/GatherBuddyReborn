using System;
using System.Collections.Generic;
using Lumina.Excel.Sheets;

namespace GatherBuddy.Crafting;

public static class QualityCalculator
{
    public static int CalculateInitialQuality(Recipe recipe, Dictionary<uint, int> ingredientPreferences)
    {
        if (recipe.MaterialQualityFactor == 0)
            return 0;
        
        var ingredients = RecipeManager.GetIngredients(recipe);
        if (ingredients.Count == 0)
            return 0;
        
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
        
        return (int)startingQuality;
    }
}
