using System;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;
using GatherBuddy.Crafting;
using GatherBuddy.Plugin;
using Lumina.Excel.Sheets;

namespace GatherBuddy.Gui;

public partial class VulcanWindow
{
    public void StartCraftingList(CraftingListDefinition list)
    {
        if (list.Recipes.Count == 0)
        {
            GatherBuddy.Log.Warning("[VulcanWindow] Cannot start empty list");
            return;
        }

        if (list.QuickSynthAll)
            GatherBuddy.Log.Debug($"[VulcanWindow] Quick Synth All active (PreferNQ={list.QuickSynthAllPreferNQ}, PrecraftsOnly={list.QuickSynthAllPrecraftsOnly})");

        var craftingQueue = new CraftingListQueue();
        craftingQueue.AddFromList(list.Recipes.Where(r => !r.Options.Skipping), list.SkipIfEnough, list.SkipFinalIfEnough);

        craftingQueue.BuildExpandedList();
        var sortedRecipes = GetRecipesInDependencyOrder(craftingQueue.Recipes, craftingQueue.OriginalRecipes);

        var expandedQueue = new List<CraftingListItem>();
        foreach (var recipeItem in sortedRecipes)
        {
            var isOriginal    = recipeItem.IsOriginalRecipe;
            var originalItem  = isOriginal ? list.Recipes.FirstOrDefault(r => r.RecipeId == recipeItem.RecipeId) : null;
            var recipeOptions = list.GetRecipeOptions(recipeItem.RecipeId, isOriginal);
            var recipeData    = RecipeManager.GetRecipe(recipeItem.RecipeId);
            var forceQuickSynth = recipeData != null && list.ShouldForceQuickSynth(recipeData.Value, isOriginal);
            var forcePreferNQ = list.ShouldForcePreferNQ(isOriginal);

            for (var i = 0; i < recipeItem.Quantity; i++)
            {
                var queueItem = new CraftingListItem(recipeItem.RecipeId, 1);

                queueItem.Options.NQOnly = recipeOptions.NQOnly || forceQuickSynth;

                queueItem.Options.Skipping  = recipeOptions.Skipping;
                queueItem.IsOriginalRecipe  = isOriginal;

                if (isOriginal && originalItem != null)
                    queueItem.ConsumableOverrides = originalItem.ConsumableOverrides.Clone();

                var craftSettings = isOriginal
                    ? originalItem?.CraftSettings
                    : list.PrecraftCraftSettings.GetValueOrDefault(recipeItem.RecipeId);
                var (effectiveMacroId, effectiveSolverOverride) = ResolveEffectiveMacroSelection(craftSettings, !isOriginal, list);
                queueItem.CraftSettings = BuildEffectiveQueueCraftSettings(craftSettings, effectiveMacroId, effectiveSolverOverride, forcePreferNQ);

                IReadOnlyDictionary<uint, int>? effectivePrefs = queueItem.CraftSettings?.IngredientPreferences;
                if (!forcePreferNQ && isOriginal && originalItem != null && originalItem.IngredientPreferences.Count > 0)
                    effectivePrefs = originalItem.IngredientPreferences;
                if (effectivePrefs != null && effectivePrefs.Count > 0)
                    queueItem.IngredientPreferences = new Dictionary<uint, int>(effectivePrefs);

                expandedQueue.Add(queueItem);
            }
        }

        var materials             = list.ListMaterials();
        var retainerPrecraftItems = new Dictionary<uint, int>();
        var retainerPlanningList  = list.RetainerRestock ? list.CreateRetainerPlanningSnapshot() : null;

        if (list.RetainerRestock && AllaganTools.Enabled)
        {
            var (corrected, precraftItems) = RetainerTaskExecutor.PlanRetainerRestock(list, expandedQueue);
            materials             = corrected;
            retainerPrecraftItems = precraftItems;
        }

        GatherBuddy.Log.Information($"[VulcanWindow] Starting crafting list '{list.Name}' with {expandedQueue.Count} crafts from {sortedRecipes.Count} recipes");
        CraftingGatherBridge.StartQueueCraftAndGather(
            expandedQueue, materials, list.Consumables, list.SkipIfEnough,
            list.RetainerRestock, retainerPrecraftItems,
            list.Ephemeral ? (int?)list.ID : null, retainerPlanningList);
    }

    private static RecipeCraftSettings? BuildEffectiveQueueCraftSettings(
        RecipeCraftSettings? sourceSettings,
        string? effectiveMacroId,
        SolverOverrideMode effectiveSolverOverride,
        bool forcePreferNQ)
    {
        RecipeCraftSettings? settings = sourceSettings?.Clone();
        if (settings == null && (effectiveMacroId != null || effectiveSolverOverride != SolverOverrideMode.Default || forcePreferNQ))
            settings = new RecipeCraftSettings();

        if (settings == null)
            return null;

        settings.SelectedMacroId = effectiveMacroId;
        settings.SolverOverride = effectiveSolverOverride;
        if (forcePreferNQ)
        {
            settings.UseAllNQ = true;
            settings.IngredientPreferences.Clear();
        }

        return settings;
    }

    private List<CraftingListItem> GetRecipesInDependencyOrder(List<CraftingListItem> recipes, List<CraftingListItem> originalRecipesList)
    {
        var precrafts     = recipes.Where(recipe => !recipe.IsOriginalRecipe).ToList();
        var finalProducts = new List<CraftingListItem>(originalRecipesList);

        var result    = new List<CraftingListItem>();
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

    private void ProcessRecipeWithDependencies(
        CraftingListItem recipeItem,
        List<CraftingListItem> allRecipes,
        HashSet<uint> processed,
        List<CraftingListItem> result)
    {
        if (processed.Contains(recipeItem.RecipeId))
            return;

        var recipe = RecipeManager.GetRecipe(recipeItem.RecipeId);
        if (recipe == null)
            return;

        var ingredients = RecipeManager.GetIngredients(recipe.Value);
        foreach (var (itemId, _) in ingredients)
        {
            var depRecipe = RecipeManager.GetRecipeForItem(itemId);
            if (depRecipe.HasValue)
            {
                var depItem = allRecipes.FirstOrDefault(r => r.RecipeId == depRecipe.Value.RowId && !r.IsOriginalRecipe);
                if (depItem != null)
                    ProcessRecipeWithDependencies(depItem, allRecipes, processed, result);
            }
        }

        processed.Add(recipeItem.RecipeId);
        result.Add(recipeItem);
    }

    private string GetCraftingJobName(uint craftTypeId)
    {
        var classJobSheet = Dalamud.GameData.GetExcelSheet<ClassJob>();
        if (classJobSheet != null)
        {
            var classJobId = craftTypeId + 8;
            var classJob   = classJobSheet.GetRow(classJobId);
            if (classJob.RowId > 0)
                return classJob.Abbreviation.ExtractText();
        }

        return "Unknown";
    }

    private unsafe int GetInventoryCount(uint itemId)
    {
        try
        {
            var inventory = InventoryManager.Instance();
            if (inventory == null)
                return 0;

            var total      = 0;
            var baseItemId = itemId >= 1_000_000 ? itemId - 1_000_000 : itemId;
            var hqItemId   = baseItemId + 1_000_000;
            var inventories = new[]
            {
                InventoryType.Inventory1, InventoryType.Inventory2,
                InventoryType.Inventory3, InventoryType.Inventory4,
                InventoryType.Crystals,
            };

            foreach (var invType in inventories)
            {
                var container = inventory->GetInventoryContainer(invType);
                if (container == null)
                    continue;

                for (var i = 0; i < container->Size; i++)
                {
                    var item = container->GetInventorySlot(i);
                    if (item == null || item->ItemId == 0)
                        continue;

                    if (item->ItemId == baseItemId || item->ItemId == hqItemId)
                        total += (int)item->Quantity;
                }
            }

            return total;
        }
        catch
        {
            return 0;
        }
    }
}
