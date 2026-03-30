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

        var craftingQueue = new CraftingListQueue();
        foreach (var item in list.Recipes)
        {
            if (!item.Options.Skipping)
                craftingQueue.AddRecipeWithPrecrafts(item.RecipeId, item.Quantity, list.SkipIfEnough);
        }

        craftingQueue.BuildExpandedList();
        var sortedRecipes = GetRecipesInDependencyOrder(craftingQueue.Recipes, craftingQueue.OriginalRecipes);

        var expandedQueue = new List<CraftingListItem>();
        foreach (var recipeItem in sortedRecipes)
        {
            var originalItem  = list.Recipes.FirstOrDefault(r => r.RecipeId == recipeItem.RecipeId);
            var recipeOptions = list.GetRecipeOptions(recipeItem.RecipeId);
            var isOriginal    = originalItem != null;

            for (var i = 0; i < recipeItem.Quantity; i++)
            {
                var queueItem = new CraftingListItem(recipeItem.RecipeId, 1);

                queueItem.Options.NQOnly = recipeOptions.NQOnly;
                if (list.QuickSynthAll)
                {
                    var recipeData = RecipeManager.GetRecipe(recipeItem.RecipeId);
                    if (recipeData?.CanQuickSynth == true)
                        queueItem.Options.NQOnly = true;
                }

                queueItem.Options.Skipping  = recipeOptions.Skipping;
                queueItem.IsOriginalRecipe  = isOriginal;

                if (originalItem != null)
                    queueItem.ConsumableOverrides = originalItem.ConsumableOverrides.Clone();

                var craftSettings    = originalItem?.CraftSettings ?? list.PrecraftCraftSettings.GetValueOrDefault(recipeItem.RecipeId);
                var effectiveMacroId = ResolveEffectiveMacroId(craftSettings, !isOriginal, list);
                if (craftSettings != null)
                {
                    queueItem.CraftSettings = new RecipeCraftSettings
                    {
                        FoodMode              = craftSettings.FoodMode,
                        FoodItemId            = craftSettings.FoodItemId,
                        FoodHQ                = craftSettings.FoodHQ,
                        MedicineMode          = craftSettings.MedicineMode,
                        MedicineItemId        = craftSettings.MedicineItemId,
                        MedicineHQ            = craftSettings.MedicineHQ,
                        ManualMode            = craftSettings.ManualMode,
                        ManualItemId          = craftSettings.ManualItemId,
                        SquadronManualMode    = craftSettings.SquadronManualMode,
                        SquadronManualItemId  = craftSettings.SquadronManualItemId,
                        IngredientPreferences = new Dictionary<uint, int>(craftSettings.IngredientPreferences),
                        UseAllNQ              = craftSettings.UseAllNQ,
                        SelectedMacroId       = effectiveMacroId,
                        SolverOverride        = craftSettings.SolverOverride,
                    };
                }
                else if (effectiveMacroId != null)
                {
                    queueItem.CraftSettings = new RecipeCraftSettings { SelectedMacroId = effectiveMacroId };
                }

                if (originalItem != null)
                {
                    var topLevelPrefs    = originalItem.IngredientPreferences;
                    var craftSettingPrefs = originalItem.CraftSettings?.IngredientPreferences;
                    var effectivePrefs   = topLevelPrefs.Count > 0 ? topLevelPrefs : craftSettingPrefs;
                    if (effectivePrefs != null && effectivePrefs.Count > 0)
                        queueItem.IngredientPreferences = new Dictionary<uint, int>(effectivePrefs);
                }

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

    private List<CraftingListItem> GetRecipesInDependencyOrder(List<CraftingListItem> recipes, List<CraftingListItem> originalRecipesList)
    {
        var originalRecipes = new HashSet<uint>();
        foreach (var item in originalRecipesList)
            originalRecipes.Add(item.RecipeId);

        var precrafts     = new List<CraftingListItem>();
        var finalProducts = new List<CraftingListItem>();

        foreach (var recipe in recipes)
        {
            if (originalRecipes.Contains(recipe.RecipeId))
                finalProducts.Add(recipe);
            else
                precrafts.Add(recipe);
        }

        var result    = new List<CraftingListItem>();
        var processed = new HashSet<uint>();

        var precraftsByJob = precrafts
            .GroupBy(r => RecipeManager.GetRecipe(r.RecipeId)?.CraftType.RowId ?? uint.MaxValue)
            .OrderBy(g => g.Key);

        foreach (var jobGroup in precraftsByJob)
        {
            foreach (var recipeItem in jobGroup.ToList())
                ProcessRecipeWithDependencies(recipeItem, recipes, processed, result);
        }

        var sortedFinalProducts = finalProducts
            .GroupBy(r => RecipeManager.GetRecipe(r.RecipeId)?.CraftType.RowId ?? uint.MaxValue)
            .OrderBy(g => g.Key)
            .SelectMany(g => g)
            .ToList();

        foreach (var recipeItem in sortedFinalProducts)
        {
            if (!processed.Contains(recipeItem.RecipeId))
            {
                processed.Add(recipeItem.RecipeId);
                result.Add(recipeItem);
            }
        }

        return result;
    }

    private static string? ResolveEffectiveMacroId(RecipeCraftSettings? settings, bool isPrecraft, CraftingListDefinition list)
    {
        var isSpecific = settings?.MacroMode == MacroOverrideMode.Specific
            || (settings?.MacroMode == MacroOverrideMode.Inherit && !string.IsNullOrEmpty(settings?.SelectedMacroId));
        if (isSpecific)
            return settings?.SelectedMacroId;
        return isPrecraft ? list.DefaultPrecraftMacroId : list.DefaultFinalMacroId;
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
                var depItem = allRecipes.FirstOrDefault(r => r.RecipeId == depRecipe.Value.RowId);
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
