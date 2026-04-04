using System.Collections.Generic;
using System.Linq;
using Lumina.Excel.Sheets;
using Newtonsoft.Json;

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
    [JsonIgnore] public CraftingQualityPolicy? QualityPolicy { get; set; }

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

    public void AddRecipe(uint recipeId, int quantity, bool isOriginalRecipe)
    {
        var existing = Recipes.FirstOrDefault(r => r.RecipeId == recipeId && r.IsOriginalRecipe == isOriginalRecipe);
        if (existing != null)
        {
            existing.Quantity += quantity;
        }
        else
        {
            Recipes.Add(new CraftingListItem(recipeId, quantity)
            {
                IsOriginalRecipe = isOriginalRecipe,
            });
        }
    }

    public void AddFromList(IEnumerable<CraftingListItem> items, bool skipIfEnough = false, bool skipFinalIfEnough = false)
    {
        Clear();

        var list = new CraftingListDefinition
        {
            SkipIfEnough = skipIfEnough,
            SkipFinalIfEnough = skipFinalIfEnough,
            Recipes = items
                .Select(item => new CraftingListItem(item.RecipeId, item.Quantity)
                {
                    Options = new ListItemOptions
                    {
                        Skipping = item.Options.Skipping,
                        NQOnly = item.Options.NQOnly,
                    },
                })
                .ToList(),
        };

        var plan = list.CreatePlan();
        OriginalRecipes.AddRange(plan.OriginalRecipes.Select(item => new CraftingListItem(item.RecipeId, item.Quantity)
        {
            IsOriginalRecipe = true,
        }));
        Recipes.AddRange(plan.Recipes.Select(item => new CraftingListItem(item.RecipeId, item.Quantity)
        {
            IsOriginalRecipe = item.IsOriginalRecipe,
        }));
    }

    public void Clear()
    {
        Recipes.Clear();
        OriginalRecipes.Clear();
        ExpandedList.Clear();
    }
}
