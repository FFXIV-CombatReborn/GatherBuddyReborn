using System;
using System.Collections.Generic;
using System.Linq;
using Lumina.Excel.Sheets;

namespace GatherBuddy.Crafting;

public class CraftingListDefinition
{
    public int ID { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<CraftingListItem> Recipes { get; set; } = new();
    public List<uint> ExpandedList { get; set; } = new();
    public CraftingListConsumableSettings Consumables { get; set; } = new();
    public Dictionary<uint, ListItemOptions> PrecraftOptions { get; set; } = new();
    public Dictionary<uint, RecipeCraftSettings> PrecraftCraftSettings { get; set; } = new();
    public string? DefaultPrecraftMacroId { get; set; }
    public string? DefaultFinalMacroId { get; set; }
    
    public bool SkipIfEnough { get; set; } = false;
    public bool QuickSynthAll { get; set; } = false;
    public bool Materia { get; set; } = false;
    public bool Repair { get; set; } = false;
    public int RepairPercent { get; set; } = 50;
    public bool RetainerRestock { get; set; } = false;
    public bool Ephemeral { get; set; } = false;

    public void BuildExpandedList()
    {
        ExpandedList.Clear();
        foreach (var recipe in Recipes)
        {
            if (!recipe.Options.Skipping)
            {
                ExpandedList.AddRange(Enumerable.Repeat(recipe.RecipeId, recipe.Quantity));
            }
        }
    }

    public CraftingListDefinition CreateRetainerPlanningSnapshot()
    {
        var snapshot = new CraftingListDefinition
        {
            SkipIfEnough = SkipIfEnough,
        };

        foreach (var recipe in Recipes)
        {
            snapshot.Recipes.Add(new CraftingListItem(recipe.RecipeId, recipe.Quantity)
            {
                Options = new ListItemOptions
                {
                    Skipping = recipe.Options.Skipping,
                    NQOnly = recipe.Options.NQOnly,
                },
            });
        }

        return snapshot;
    }

    public Dictionary<uint, int> ListMaterials() => ListMaterials(null, null);

    public Dictionary<uint, int> ListMaterials(Dictionary<uint, int>? additionalAvailable) => ListMaterials(additionalAvailable, null);

    public Dictionary<uint, int> ListMaterials(
        Dictionary<uint, int>? additionalAvailable,
        Dictionary<uint, (int TargetHQ, int TargetNQ, bool IsExplicit)>? qualityTargets)
    {
        var materials = new Dictionary<uint, int>();
        foreach (var item in Recipes)
        {
            if (item.Options.Skipping || item.Quantity == 0)
                continue;
                
            var recipe = RecipeManager.GetRecipe(item.RecipeId);
            if (recipe == null)
                continue;

            var resolved = new Dictionary<uint, int>();
            ResolveIngredientsForQuantity(recipe.Value, item.Quantity, resolved, SkipIfEnough, additionalAvailable, qualityTargets);
            
            foreach (var (itemId, amount) in resolved)
            {
                if (materials.ContainsKey(itemId))
                    materials[itemId] += amount;
                else
                    materials[itemId] = amount;
            }
        }
        return materials;
    }

    private unsafe void ResolveIngredientsForQuantity(
        Recipe recipe, int quantity, Dictionary<uint, int> resolved,
        bool skipIfEnough = false,
        Dictionary<uint, int>? additionalAvailable = null,
        Dictionary<uint, (int TargetHQ, int TargetNQ, bool IsExplicit)>? qualityTargets = null)
    {
        var ingredients = RecipeManager.GetIngredients(recipe);
        
        foreach (var (itemId, amount) in ingredients)
        {
            var actualAmount = amount * quantity;
            var subRecipe = RecipeManager.GetRecipeForItem(itemId);
            
            if (subRecipe.HasValue)
            {
                var quantityToCraft = (int)System.Math.Ceiling((double)actualAmount / subRecipe.Value.AmountResult);
                
                if (skipIfEnough)
                {
                    try
                    {
                        var inventory = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
                        if (inventory != null)
                        {
                            var resultItemId = subRecipe.Value.ItemResult.RowId;
                            var amountPerCraft = subRecipe.Value.AmountResult;
                            var nqCount = inventory->GetInventoryItemCount(resultItemId, false, false, false);
                            var hqCount = inventory->GetInventoryItemCount(resultItemId, true, false, false);
                            int totalInInventory;
                            if (qualityTargets != null && qualityTargets.TryGetValue(resultItemId, out var qt) && qt.TargetNQ == 0)
                                totalInInventory = (int)hqCount;
                            else
                                totalInInventory = (int)(nqCount + hqCount);

                            if (additionalAvailable != null && additionalAvailable.TryGetValue(resultItemId, out var fromRetainer))
                                totalInInventory += fromRetainer;
                            
                            if (totalInInventory >= actualAmount)
                            {
                                continue;
                            }
                            
                            var stillNeeded = actualAmount - totalInInventory;
                            quantityToCraft = (int)System.Math.Ceiling((double)stillNeeded / amountPerCraft);
                        }
                    }
                    catch { }
                }
                
                ResolveIngredientsForQuantity(subRecipe.Value, quantityToCraft, resolved, skipIfEnough, additionalAvailable, qualityTargets);
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

    public Dictionary<uint, int> ListPrecrafts()
    {
        var precrafts = new Dictionary<uint, int>();
        foreach (var item in Recipes)
        {
            if (item.Options.Skipping || item.Quantity == 0)
                continue;

            var recipe = RecipeManager.GetRecipe(item.RecipeId);
            if (recipe == null)
                continue;

            CollectPrecrafts(recipe.Value, item.Quantity, precrafts, SkipIfEnough);
        }
        return precrafts;
    }

    private unsafe void CollectPrecrafts(Recipe recipe, int parentCraftCount, Dictionary<uint, int> precrafts, bool skipIfEnough)
    {
        var ingredients = RecipeManager.GetIngredients(recipe);
        foreach (var (itemId, amount) in ingredients)
        {
            var subRecipe = RecipeManager.GetRecipeForItem(itemId);
            if (!subRecipe.HasValue)
                continue;

            var itemsNeeded = amount * parentCraftCount;
            precrafts[itemId] = precrafts.GetValueOrDefault(itemId) + itemsNeeded;

            var toRecurse = itemsNeeded;
            if (skipIfEnough)
            {
                try
                {
                    var inv = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
                    if (inv != null)
                    {
                        var resultId = subRecipe.Value.ItemResult.RowId;
                        var have = (int)inv->GetInventoryItemCount(resultId, false, false, false)
                                 + (int)inv->GetInventoryItemCount(resultId, true, false, false);
                        if (have >= itemsNeeded)
                            continue;
                        toRecurse = itemsNeeded - have;
                    }
                }
                catch { }
            }

            var subCraftCount = (int)System.Math.Ceiling((double)toRecurse / subRecipe.Value.AmountResult);
            CollectPrecrafts(subRecipe.Value, subCraftCount, precrafts, skipIfEnough);
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

    public void RemoveRecipe(uint recipeId)
    {
        Recipes.RemoveAll(r => r.RecipeId == recipeId);
    }

    public void UpdateRecipeQuantity(uint recipeId, int quantity)
    {
        var existing = Recipes.FirstOrDefault(r => r.RecipeId == recipeId);
        if (existing != null)
        {
            existing.Quantity = quantity;
        }
    }

    public ListItemOptions GetRecipeOptions(uint recipeId)
    {
        var mainItem = Recipes.FirstOrDefault(r => r.RecipeId == recipeId);
        if (mainItem != null)
            return mainItem.Options;
        
        if (!PrecraftOptions.ContainsKey(recipeId))
            PrecraftOptions[recipeId] = new ListItemOptions();
        
        return PrecraftOptions[recipeId];
    }
    
    public void SetRecipeQuickSynth(uint recipeId, bool useQuickSynth)
    {
        var mainItem = Recipes.FirstOrDefault(r => r.RecipeId == recipeId);
        if (mainItem != null)
        {
            mainItem.Options.NQOnly = useQuickSynth;
        }
        else
        {
            if (!PrecraftOptions.ContainsKey(recipeId))
                PrecraftOptions[recipeId] = new ListItemOptions();
            
            PrecraftOptions[recipeId].NQOnly = useQuickSynth;
        }
    }

    public void SetPrecraftCraftSettings(uint recipeId, RecipeCraftSettings? settings)
    {
        if (settings == null || !settings.HasAnySettings())
            PrecraftCraftSettings.Remove(recipeId);
        else
            PrecraftCraftSettings[recipeId] = settings;
    }

    public void Clear()
    {
        Recipes.Clear();
        ExpandedList.Clear();
        PrecraftOptions.Clear();
        PrecraftCraftSettings.Clear();
    }
}
