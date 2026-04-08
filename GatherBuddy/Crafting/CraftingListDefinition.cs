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
    public string FolderPath { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<CraftingListItem> Recipes { get; set; } = new();
    public List<uint> ExpandedList { get; set; } = new();
    public CraftingListConsumableSettings Consumables { get; set; } = new();
    public Dictionary<uint, ListItemOptions> PrecraftOptions { get; set; } = new();
    public Dictionary<uint, RecipeCraftSettings> PrecraftCraftSettings { get; set; } = new();
    public string? DefaultPrecraftMacroId { get; set; }
    public string? DefaultFinalMacroId { get; set; }
    public SolverOverrideMode DefaultPrecraftSolverOverride { get; set; } = SolverOverrideMode.Default;
    public SolverOverrideMode DefaultFinalSolverOverride { get; set; } = SolverOverrideMode.Default;
    
    public Dictionary<uint, uint> PrecraftRecipeOverrides { get; set; } = new();
    public bool SkipIfEnough { get; set; } = false;
    public bool SkipFinalIfEnough { get; set; } = false;
    public bool QuickSynthAll { get; set; } = false;
    public bool QuickSynthAllPreferNQ { get; set; } = false;
    public bool QuickSynthAllPrecraftsOnly { get; set; } = false;
    public bool Materia { get; set; } = false;
    public bool Repair { get; set; } = false;
    public int RepairPercent { get; set; } = 50;
    public bool RetainerRestock { get; set; } = false;
    public bool Ephemeral { get; set; } = false;

    public bool ShouldApplyQuickSynthAllOverrides(bool isOriginalRecipe)
        => QuickSynthAll && (!QuickSynthAllPrecraftsOnly || !isOriginalRecipe);

    public bool ShouldForceQuickSynth(Recipe recipe, bool isOriginalRecipe)
        => ShouldApplyQuickSynthAllOverrides(isOriginalRecipe) && recipe.CanQuickSynth;

    public bool ShouldForcePreferNQ(bool isOriginalRecipe)
        => QuickSynthAllPreferNQ && ShouldApplyQuickSynthAllOverrides(isOriginalRecipe);

    public CraftingQualityOverrideMode GetQualityOverrideMode(Recipe recipe, bool isOriginalRecipe)
    {
        if (!ShouldForceQuickSynth(recipe, isOriginalRecipe))
            return CraftingQualityOverrideMode.None;

        return QuickSynthAllPreferNQ
            ? CraftingQualityOverrideMode.RequireNQOnly
            : CraftingQualityOverrideMode.PreferNQWithHQFallback;
    }

    public CraftingListPlan CreatePlan(bool useRetainerCraftableAvailability = false)
        => CraftingListPlanner.Build(this, new CraftingListPlannerOptions(useRetainerCraftableAvailability));

    public void BuildExpandedList()
    {
        var plan = CreatePlan();
        ExpandedList.Clear();
        foreach (var recipe in plan.OriginalRecipes)
            ExpandedList.AddRange(Enumerable.Repeat(recipe.RecipeId, recipe.Quantity));
    }

    public CraftingListDefinition CreateRetainerPlanningSnapshot()
    {
        var snapshot = new CraftingListDefinition
        {
            ID = ID,
            Name = Name,
            Description = Description,
            FolderPath = FolderPath,
            CreatedAt = CreatedAt,
            Consumables = Consumables.Clone(),
            DefaultPrecraftMacroId = DefaultPrecraftMacroId,
            DefaultFinalMacroId = DefaultFinalMacroId,
            DefaultPrecraftSolverOverride = DefaultPrecraftSolverOverride,
            DefaultFinalSolverOverride = DefaultFinalSolverOverride,
            SkipIfEnough = SkipIfEnough,
            SkipFinalIfEnough = SkipFinalIfEnough,
            QuickSynthAll = QuickSynthAll,
            QuickSynthAllPreferNQ = QuickSynthAllPreferNQ,
            QuickSynthAllPrecraftsOnly = QuickSynthAllPrecraftsOnly,
            Materia = Materia,
            Repair = Repair,
            RepairPercent = RepairPercent,
            RetainerRestock = RetainerRestock,
            Ephemeral = Ephemeral,
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
                IngredientPreferences = new Dictionary<uint, int>(recipe.IngredientPreferences),
                ConsumableOverrides = recipe.ConsumableOverrides.Clone(),
                IsOriginalRecipe = recipe.IsOriginalRecipe,
                CraftSettings = recipe.CraftSettings?.Clone(),
            });
        }

        foreach (var (recipeId, options) in PrecraftOptions)
        {
            snapshot.PrecraftOptions[recipeId] = new ListItemOptions
            {
                Skipping = options.Skipping,
                NQOnly = options.NQOnly,
            };
        }

        foreach (var (recipeId, settings) in PrecraftCraftSettings)
            snapshot.PrecraftCraftSettings[recipeId] = settings.Clone();

        foreach (var (itemId, recipeId) in PrecraftRecipeOverrides)
            snapshot.PrecraftRecipeOverrides[itemId] = recipeId;

        return snapshot;
    }

    public Dictionary<uint, int> ListMaterials() => new(CreatePlan().Materials);

    public Dictionary<uint, int> ListPrecrafts()
        => new(CreatePlan().Precrafts);

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

    public ListItemOptions GetRecipeOptions(uint recipeId, bool isOriginalRecipe)
    {
        if (isOriginalRecipe)
        {
            var mainItem = Recipes.FirstOrDefault(r => r.RecipeId == recipeId);
            if (mainItem != null)
                return mainItem.Options;
        }
        
        if (!PrecraftOptions.TryGetValue(recipeId, out var options))
            PrecraftOptions[recipeId] = options = new ListItemOptions();
        
        return options;
    }
    
    public void SetRecipeQuickSynth(uint recipeId, bool useQuickSynth, bool isOriginalRecipe)
    {
        GetRecipeOptions(recipeId, isOriginalRecipe).NQOnly = useQuickSynth;
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
