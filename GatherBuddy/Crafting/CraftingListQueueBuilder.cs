using System.Collections.Generic;
using System.Linq;
using Lumina.Excel.Sheets;

namespace GatherBuddy.Crafting;

public static class CraftingListQueueBuilder
{
    public static List<CraftingListItem> CreateExpandedQueue(CraftingListDefinition list, bool useRetainerCraftableAvailability = false)
        => CreateExpandedQueue(list, list.CreatePlan(useRetainerCraftableAvailability));

    public static List<CraftingListItem> CreateExpandedQueue(CraftingListDefinition list, CraftingListPlan plan)
    {
        var sortedRecipes = GetRecipesInDependencyOrder(plan.Recipes, plan.OriginalRecipes);
        var expandedQueue = new List<CraftingListItem>();

        foreach (var recipeItem in sortedRecipes)
        {
            var isOriginal = recipeItem.IsOriginalRecipe;
            var originalItem = isOriginal ? list.Recipes.FirstOrDefault(r => r.RecipeId == recipeItem.RecipeId) : null;
            var recipeOptions = list.GetRecipeOptions(recipeItem.RecipeId, isOriginal);
            var recipeData = RecipeManager.GetRecipe(recipeItem.RecipeId);
            if (!recipeData.HasValue)
                continue;

            var forceQuickSynth = list.ShouldForceQuickSynth(recipeData.Value, isOriginal);
            var qualityOverrideMode = list.GetQualityOverrideMode(recipeData.Value, isOriginal);

            for (var i = 0; i < recipeItem.Quantity; i++)
            {
                var queueItem = new CraftingListItem(recipeItem.RecipeId, 1)
                {
                    IsOriginalRecipe = isOriginal,
                };

                queueItem.Options.NQOnly = recipeOptions.NQOnly || forceQuickSynth;
                queueItem.Options.Skipping = recipeOptions.Skipping;

                if (isOriginal && originalItem != null)
                    queueItem.ConsumableOverrides = originalItem.ConsumableOverrides.Clone();

                var craftSettings = isOriginal
                    ? originalItem?.CraftSettings
                    : list.PrecraftCraftSettings.GetValueOrDefault(recipeItem.RecipeId);
                var (effectiveMacroId, effectiveSolverOverride) = ResolveEffectiveMacroSelection(craftSettings, !isOriginal, list);
                queueItem.CraftSettings = BuildEffectiveQueueCraftSettings(craftSettings, effectiveMacroId, effectiveSolverOverride);
                queueItem.QualityPolicy = CraftingQualityPolicyResolver.Resolve(recipeData.Value, queueItem.CraftSettings, qualityOverrideMode);
                queueItem.IngredientPreferences = queueItem.QualityPolicy.BuildGuaranteedHQPreferences();

                expandedQueue.Add(queueItem);
            }
        }

        return expandedQueue;
    }

    private static RecipeCraftSettings? BuildEffectiveQueueCraftSettings(
        RecipeCraftSettings? sourceSettings,
        string? effectiveMacroId,
        SolverOverrideMode effectiveSolverOverride)
    {
        RecipeCraftSettings? settings = sourceSettings?.Clone();
        if (settings == null && (effectiveMacroId != null || effectiveSolverOverride != SolverOverrideMode.Default))
            settings = new RecipeCraftSettings();

        if (settings == null)
            return null;

        settings.SelectedMacroId = effectiveMacroId;
        settings.SolverOverride = effectiveSolverOverride;

        return settings;
    }

    private static (string? MacroId, SolverOverrideMode SolverOverride) ResolveEffectiveMacroSelection(RecipeCraftSettings? settings, bool isPrecraft, CraftingListDefinition list)
    {
        var isSpecific = settings != null
            && (settings.MacroMode == MacroOverrideMode.Specific
                || (settings.MacroMode == MacroOverrideMode.Inherit
                    && (!string.IsNullOrEmpty(settings.SelectedMacroId) || settings.SolverOverride != SolverOverrideMode.Default)));
        if (isSpecific)
            return (settings?.SelectedMacroId, settings?.SolverOverride ?? SolverOverrideMode.Default);

        return isPrecraft
            ? (list.DefaultPrecraftMacroId, list.DefaultPrecraftSolverOverride)
            : (list.DefaultFinalMacroId, list.DefaultFinalSolverOverride);
    }

    private static List<CraftingListItem> GetRecipesInDependencyOrder(List<CraftingListItem> recipes, List<CraftingListItem> originalRecipesList)
    {
        var precrafts = recipes.Where(recipe => !recipe.IsOriginalRecipe).ToList();
        var finalProducts = new List<CraftingListItem>(originalRecipesList);

        var result = new List<CraftingListItem>();
        var processed = new HashSet<uint>();

        var precraftsByJob = precrafts
            .GroupBy(r => RecipeManager.GetRecipe(r.RecipeId)?.CraftType.RowId ?? uint.MaxValue)
            .OrderBy(g => g.Key);

        foreach (var jobGroup in precraftsByJob)
        {
            foreach (var recipeItem in jobGroup.ToList())
                ProcessRecipeWithDependencies(recipeItem, precrafts, processed, result);
        }

        var sortedFinalProducts = finalProducts
            .GroupBy(r => RecipeManager.GetRecipe(r.RecipeId)?.CraftType.RowId ?? uint.MaxValue)
            .OrderBy(g => g.Key)
            .SelectMany(g => g)
            .ToList();

        foreach (var recipeItem in sortedFinalProducts)
            result.Add(recipeItem);

        return result;
    }

    private static void ProcessRecipeWithDependencies(
        CraftingListItem recipeItem,
        List<CraftingListItem> allRecipes,
        HashSet<uint> processed,
        List<CraftingListItem> result)
    {
        if (processed.Contains(recipeItem.RecipeId))
            return;

        var recipe = RecipeManager.GetRecipe(recipeItem.RecipeId);
        if (!recipe.HasValue)
            return;

        foreach (var (itemId, _) in RecipeManager.GetIngredients(recipe.Value))
        {
            var depRecipe = RecipeManager.GetRecipeForItem(itemId);
            if (!depRecipe.HasValue)
                continue;

            var depItem = allRecipes.FirstOrDefault(r => r.RecipeId == depRecipe.Value.RowId && !r.IsOriginalRecipe);
            if (depItem != null)
                ProcessRecipeWithDependencies(depItem, allRecipes, processed, result);
        }

        processed.Add(recipeItem.RecipeId);
        result.Add(recipeItem);
    }
}
