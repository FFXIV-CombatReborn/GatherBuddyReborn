using System;
using System.Collections.Generic;
using System.Linq;
using Lumina.Excel.Sheets;

namespace GatherBuddy.Crafting;

public enum CraftingQualityOverrideMode
{
    None,
    PreferNQWithHQFallback,
    RequireNQOnly,
}

public readonly record struct IngredientQualityDemand(int RequiredHQ, int RequiredNQ, int PreferHQ, int PreferNQ)
{
    public int Total
        => RequiredHQ + RequiredNQ + PreferHQ + PreferNQ;

    public int GuaranteedHQ
        => RequiredHQ;

    public bool HasHardRequirements
        => RequiredHQ > 0 || RequiredNQ > 0;

    public static IngredientQualityDemand FromPreferHQ(int total)
        => new(0, 0, Math.Max(0, total), 0);

    public static IngredientQualityDemand FromPreferNQ(int total)
        => new(0, 0, 0, Math.Max(0, total));

    public static IngredientQualityDemand FromRequireNQOnly(int total)
        => new(0, Math.Max(0, total), 0, 0);

    public static IngredientQualityDemand FromRequiredHQ(int requiredHQ, int total)
    {
        requiredHQ = Math.Clamp(requiredHQ, 0, Math.Max(0, total));
        return new(requiredHQ, 0, 0, Math.Max(0, total - requiredHQ));
    }

    public IngredientQualityDemand Scale(int factor)
        => factor <= 1
            ? this
            : new(RequiredHQ * factor, RequiredNQ * factor, PreferHQ * factor, PreferNQ * factor);

    public IngredientQualityDemand Add(IngredientQualityDemand other)
        => new(
            RequiredHQ + other.RequiredHQ,
            RequiredNQ + other.RequiredNQ,
            PreferHQ + other.PreferHQ,
            PreferNQ + other.PreferNQ);

    public IngredientQualityDemand ConsumeUnknownQuality(int available, out int consumed)
    {
        consumed = 0;
        if (available <= 0 || Total <= 0)
            return this;

        var remainingPreferNQ = PreferNQ;
        var remainingPreferHQ = PreferHQ;

        var takePreferNQ = Math.Min(available, remainingPreferNQ);
        remainingPreferNQ -= takePreferNQ;
        available -= takePreferNQ;
        consumed += takePreferNQ;

        var takePreferHQ = Math.Min(available, remainingPreferHQ);
        remainingPreferHQ -= takePreferHQ;
        consumed += takePreferHQ;

        return new(RequiredHQ, RequiredNQ, remainingPreferHQ, remainingPreferNQ);
    }

    public IngredientQualityDemand ConsumeSplit(int availableNQ, int availableHQ, out int consumedNQ, out int consumedHQ)
    {
        consumedNQ = 0;
        consumedHQ = 0;

        var remainingRequiredHQ = RequiredHQ;
        var remainingRequiredNQ = RequiredNQ;
        var remainingPreferHQ = PreferHQ;
        var remainingPreferNQ = PreferNQ;

        var takeHQ = Math.Min(remainingRequiredHQ, availableHQ);
        remainingRequiredHQ -= takeHQ;
        availableHQ -= takeHQ;
        consumedHQ += takeHQ;

        var takeNQ = Math.Min(remainingRequiredNQ, availableNQ);
        remainingRequiredNQ -= takeNQ;
        availableNQ -= takeNQ;
        consumedNQ += takeNQ;

        takeNQ = Math.Min(remainingPreferNQ, availableNQ);
        remainingPreferNQ -= takeNQ;
        availableNQ -= takeNQ;
        consumedNQ += takeNQ;

        takeHQ = Math.Min(remainingPreferNQ, availableHQ);
        remainingPreferNQ -= takeHQ;
        availableHQ -= takeHQ;
        consumedHQ += takeHQ;

        takeHQ = Math.Min(remainingPreferHQ, availableHQ);
        remainingPreferHQ -= takeHQ;
        availableHQ -= takeHQ;
        consumedHQ += takeHQ;

        takeNQ = Math.Min(remainingPreferHQ, availableNQ);
        remainingPreferHQ -= takeNQ;
        availableNQ -= takeNQ;
        consumedNQ += takeNQ;

        return new(remainingRequiredHQ, remainingRequiredNQ, remainingPreferHQ, remainingPreferNQ);
    }
}

public sealed class CraftingQualityPolicy
{
    private readonly Dictionary<uint, IngredientQualityDemand> _ingredientDemands;

    public uint RecipeId { get; }
    public CraftingQualityOverrideMode OverrideMode { get; }
    public bool HasExplicitHQRequirements { get; }
    public bool AllowHQMaterialsInQuickSynthesis => OverrideMode != CraftingQualityOverrideMode.RequireNQOnly;
    public IReadOnlyDictionary<uint, IngredientQualityDemand> IngredientDemands => _ingredientDemands;

    public CraftingQualityPolicy(
        uint recipeId,
        CraftingQualityOverrideMode overrideMode,
        bool hasExplicitHQRequirements,
        Dictionary<uint, IngredientQualityDemand> ingredientDemands)
    {
        RecipeId = recipeId;
        OverrideMode = overrideMode;
        HasExplicitHQRequirements = hasExplicitHQRequirements;
        _ingredientDemands = ingredientDemands;
    }

    public IngredientQualityDemand GetDemand(uint itemId)
        => _ingredientDemands.TryGetValue(itemId, out var demand)
            ? demand
            : default;

    public Dictionary<uint, int> BuildGuaranteedHQPreferences()
    {
        var guaranteed = new Dictionary<uint, int>();
        foreach (var (itemId, demand) in _ingredientDemands)
        {
            if (demand.GuaranteedHQ > 0)
                guaranteed[itemId] = demand.GuaranteedHQ;
        }

        return guaranteed;
    }

    public int CalculateGuaranteedInitialQuality(Recipe recipe)
        => QualityCalculator.CalculateInitialQuality(recipe, BuildGuaranteedHQPreferences());

    public bool TryResolveIngredientSelection(
        uint itemId,
        int availableNQ,
        int availableHQ,
        out int assignedNQ,
        out int assignedHQ,
        out string details)
    {
        var demand = GetDemand(itemId);
        var remaining = demand.ConsumeSplit(availableNQ, availableHQ, out assignedNQ, out assignedHQ);
        if (remaining.Total == 0)
        {
            details = string.Empty;
            return true;
        }

        details = $"need HQ={demand.RequiredHQ}, NQ={demand.RequiredNQ}, prefer HQ={demand.PreferHQ}, prefer NQ={demand.PreferNQ}; available NQ={availableNQ}, HQ={availableHQ}";
        return false;
    }

    public bool UsesHQFallbackForNQPreference(uint itemId, int assignedHQ)
    {
        var demand = GetDemand(itemId);
        return demand.PreferNQ > 0 && assignedHQ > demand.RequiredHQ;
    }

    public bool UsesNQFallbackForHQPreference(uint itemId, int assignedNQ)
    {
        var demand = GetDemand(itemId);
        return demand.PreferHQ > 0 && assignedNQ > demand.RequiredNQ;
    }
}

public static class CraftingQualityPolicyResolver
{
    public static CraftingQualityPolicy Resolve(
        Recipe recipe,
        RecipeCraftSettings? settings,
        CraftingQualityOverrideMode overrideMode = CraftingQualityOverrideMode.None)
    {
        var explicitPreferences = settings?.IngredientPreferences ?? new Dictionary<uint, int>();
        var hasExplicitHQRequirements = explicitPreferences.Values.Any(preferredHQ => preferredHQ > 0);
        var itemSheet = Dalamud.GameData.GetExcelSheet<Item>();
        var ingredientDemands = new Dictionary<uint, IngredientQualityDemand>();

        foreach (var (itemId, amount) in RecipeManager.GetIngredients(recipe))
        {
            var canBeHQ = itemSheet != null
                && itemSheet.TryGetRow(itemId, out var item)
                && item.CanBeHq;

            IngredientQualityDemand demand;
            if (!canBeHQ)
            {
                demand = IngredientQualityDemand.FromPreferNQ(amount);
            }
            else if (overrideMode == CraftingQualityOverrideMode.RequireNQOnly)
            {
                demand = IngredientQualityDemand.FromRequireNQOnly(amount);
            }
            else if (overrideMode == CraftingQualityOverrideMode.PreferNQWithHQFallback)
            {
                demand = IngredientQualityDemand.FromPreferNQ(amount);
            }
            else if (hasExplicitHQRequirements)
            {
                var requiredHQ = explicitPreferences.TryGetValue(itemId, out var preferredHQ)
                    ? preferredHQ
                    : 0;
                demand = IngredientQualityDemand.FromRequiredHQ(requiredHQ, amount);
            }
            else if (settings?.UseAllNQ == true)
            {
                demand = IngredientQualityDemand.FromPreferNQ(amount);
            }
            else
            {
                demand = IngredientQualityDemand.FromPreferHQ(amount);
            }

            ingredientDemands[itemId] = demand;
        }

        return new CraftingQualityPolicy(
            recipe.RowId,
            overrideMode,
            ingredientDemands.Values.Any(demand => demand.RequiredHQ > 0),
            ingredientDemands);
    }
}
