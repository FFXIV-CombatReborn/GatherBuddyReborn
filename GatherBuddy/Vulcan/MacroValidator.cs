using System.Collections.Generic;
using GatherBuddy.Crafting;
using Lumina.Excel.Sheets;

namespace GatherBuddy.Vulcan;

public enum MacroValidationFailure
{
    None,
    NoStats,
    CPExhausted,
    DurabilityFailed,
    InsufficientProgress,
    ActionUnusable,
}

public class MacroValidationResult
{
    public bool                  IsValid                    { get; init; }
    public MacroValidationFailure Failure                   { get; init; }
    public int                   FailedAtStep               { get; init; }
    public int                   FinalProgress              { get; init; }
    public int                   RequiredProgress           { get; init; }
    public int                   FinalQuality               { get; init; }
    public int                   FinalDurability            { get; init; }
    public int                   SkippedConditionGatedCount { get; init; }
}

public static class MacroValidator
{
    private static readonly Dictionary<(uint, string, uint, bool, uint, bool, int), MacroValidationResult> _cache = new();

    public static MacroValidationResult? GetOrCompute(uint recipeId, string? macroId, RecipeCraftSettings? settings = null, CraftingListConsumableSettings? listConsumables = null)
    {
        if (string.IsNullOrEmpty(macroId))
            return null;

        var recipe = RecipeManager.GetRecipe(recipeId);
        if (!recipe.HasValue)
            return null;

        var foodId     = ResolveId(settings?.FoodMode,     settings?.FoodItemId,    listConsumables?.FoodItemId);
        var foodHQ     = ResolveHQ(settings?.FoodMode,     settings?.FoodHQ ?? false, listConsumables?.FoodHQ ?? false);
        var medicineId = ResolveId(settings?.MedicineMode, settings?.MedicineItemId, listConsumables?.MedicineItemId);
        var medicineHQ = ResolveHQ(settings?.MedicineMode, settings?.MedicineHQ ?? false, listConsumables?.MedicineHQ ?? false);

        var qualityPolicy = CraftingQualityPolicyResolver.Resolve(recipe.Value, settings);
        var startingQuality = qualityPolicy.CalculateGuaranteedInitialQuality(recipe.Value);

        var key = (recipeId, macroId, foodId ?? 0, foodHQ, medicineId ?? 0, medicineHQ, startingQuality);
        if (_cache.TryGetValue(key, out var cached))
            return cached;

        var macro = CraftingGameInterop.UserMacroLibrary.GetMacroByStringId(macroId);
        if (macro == null)
            return null;

        var jobId     = (uint)(recipe.Value.CraftType.RowId + 8);
        var baseStats = GearsetStatsReader.ReadGearsetStatsForJob(jobId);
        var stats     = baseStats != null ? GearsetStatsReader.ApplyConsumablesToStats(baseStats, foodId, foodHQ, medicineId, medicineHQ) : null;

        var result = Validate(macro, recipe.Value, stats, startingQuality);
        _cache[key] = result;
        return result;
    }

    public static void Invalidate(uint recipeId, string macroId)
    {
        var toRemove = new List<(uint, string, uint, bool, uint, bool, int)>();
        foreach (var key in _cache.Keys)
            if (key.Item1 == recipeId && key.Item2 == macroId)
                toRemove.Add(key);
        foreach (var key in toRemove)
            _cache.Remove(key);
    }

    public static void InvalidateByMacroId(string macroId)
    {
        var toRemove = new List<(uint, string, uint, bool, uint, bool, int)>();
        foreach (var key in _cache.Keys)
            if (key.Item2 == macroId)
                toRemove.Add(key);
        foreach (var key in toRemove)
            _cache.Remove(key);
    }

    public static void InvalidateAll() => _cache.Clear();

    public static MacroValidationResult Validate(UserMacro macro, Recipe recipe, GameStateBuilder.PlayerStats? stats, int startingQuality = 0)
    {
        if (stats == null)
            return new MacroValidationResult { IsValid = false, Failure = MacroValidationFailure.NoStats };

        var recipeInfo = CraftingStateBuilder.BuildRecipeInfo(recipe);
        var craft      = GameStateBuilder.BuildCraftState(recipeInfo, stats);
        var step       = GameStateBuilder.BuildInitialStepState(craft, startingQuality);

        var skipped = 0;
        for (var i = 0; i < macro.Actions.Count; i++)
        {
            if (step.Progress >= craft.CraftProgress || step.Durability <= 0)
                break;

            var skill = (VulcanSkill)macro.Actions[i];

            if (!Simulator.CanUseAction(craft, step, skill))
            {
                if (IsConditionGated(skill))
                {
                    skipped++;
                    continue;
                }

                var failure = step.RemainingCP < Simulator.GetCPCost(step, skill)
                    ? MacroValidationFailure.CPExhausted
                    : MacroValidationFailure.ActionUnusable;

                return new MacroValidationResult
                {
                    IsValid = false, Failure = failure, FailedAtStep = i + 1,
                    FinalProgress = step.Progress, RequiredProgress = craft.CraftProgress,
                    FinalQuality = step.Quality, FinalDurability = step.Durability,
                    SkippedConditionGatedCount = skipped,
                };
            }

            var (_, next) = Simulator.Execute(craft, step, skill, 0.0f, 1.0f);
            step = next;

            if (step.Durability <= 0 && step.Progress < craft.CraftProgress)
                return new MacroValidationResult
                {
                    IsValid = false, Failure = MacroValidationFailure.DurabilityFailed, FailedAtStep = i + 1,
                    FinalProgress = step.Progress, RequiredProgress = craft.CraftProgress,
                    FinalQuality = step.Quality, FinalDurability = 0,
                    SkippedConditionGatedCount = skipped,
                };
        }

        if (step.Progress < craft.CraftProgress)
            return new MacroValidationResult
            {
                IsValid = false, Failure = MacroValidationFailure.InsufficientProgress, FailedAtStep = macro.Actions.Count,
                FinalProgress = step.Progress, RequiredProgress = craft.CraftProgress,
                FinalQuality = step.Quality, FinalDurability = step.Durability,
                SkippedConditionGatedCount = skipped,
            };

        return new MacroValidationResult
        {
            IsValid = true,
            FinalProgress = step.Progress, RequiredProgress = craft.CraftProgress,
            FinalQuality = step.Quality, FinalDurability = step.Durability,
            SkippedConditionGatedCount = skipped,
        };
    }

    private static bool IsConditionGated(VulcanSkill action)
        => action is VulcanSkill.IntensiveSynthesis or VulcanSkill.PreciseTouch or VulcanSkill.TricksOfTrade;

    private static uint? ResolveId(ConsumableOverrideMode? mode, uint? specificId, uint? inheritId)
        => mode switch
        {
            ConsumableOverrideMode.None     => null,
            ConsumableOverrideMode.Specific => specificId,
            _                               => inheritId,
        };

    private static bool ResolveHQ(ConsumableOverrideMode? mode, bool specificHQ, bool inheritHQ)
        => mode switch
        {
            ConsumableOverrideMode.None     => false,
            ConsumableOverrideMode.Specific => specificHQ,
            _                               => inheritHQ,
        };
}
