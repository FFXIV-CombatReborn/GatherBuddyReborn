using FFXIVClientStructs.FFXIV.Client.Game;
using GatherBuddy.Vulcan;
using Lumina.Excel.Sheets;
using System.Collections.Generic;
using System.Linq;

namespace GatherBuddy.Crafting;

public enum CraftingStatsSource
{
    PreferCurrentJobStats,
    AlwaysGearsetStats,
}

public enum CraftingSimulationIntent
{
    Execution,
    ValidatorPreview,
}

public sealed record CraftingExecutionContext(
    RecipeCraftSettings? ConsumableSettings,
    CraftingQualityPolicy QualityPolicy,
    VulcanSolverMode EffectiveSolverMode,
    bool ForceProgressOnlyUnlockCraft,
    bool HasCraftedBefore,
    bool UseQuickSynthesis,
    string? SelectedMacroId
);

public sealed record CraftingSimulationContext(
    CraftingExecutionContext ExecutionContext,
    GameStateBuilder.PlayerStats Stats,
    CraftState SimulationState,
    RaphaelSolveRequest RaphaelRequest
);

public static class CraftingContextResolver
{
    public static CraftingExecutionContext ResolveExecutionContext(CraftingListItem item, Recipe recipe, CraftingListConsumableSettings? listConsumables)
    {
        var consumableSettings = BuildConsumableSettings(item, listConsumables);
        var qualityPolicy = GetQualityPolicy(item, recipe);
        var hasCraftedBefore = HasRecipeCraftedBefore(recipe);
        var useQuickSynthesis = item.Options.NQOnly && recipe.CanQuickSynth && hasCraftedBefore;
        var forceProgressOnlyUnlockCraft = item.Options.NQOnly
            && recipe.CanQuickSynth
            && !hasCraftedBefore
            && qualityPolicy.OverrideMode == CraftingQualityOverrideMode.RequireNQOnly;
        var craftSolverOverride = forceProgressOnlyUnlockCraft
            ? SolverOverrideMode.ProgressOnlySolver
            : item.CraftSettings?.SolverOverride ?? SolverOverrideMode.Default;
        var effectiveSolverMode = craftSolverOverride switch
        {
            SolverOverrideMode.StandardSolver => VulcanSolverMode.StandardSolver,
            SolverOverrideMode.RaphaelSolver => VulcanSolverMode.PureRaphael,
            SolverOverrideMode.ProgressOnlySolver => VulcanSolverMode.ProgressOnly,
            _ => GatherBuddy.Config.RaphaelSolverConfig.SolverMode,
        };
        var selectedMacroId = forceProgressOnlyUnlockCraft ? null : item.CraftSettings?.SelectedMacroId;
        return new(
            consumableSettings,
            qualityPolicy,
            effectiveSolverMode,
            forceProgressOnlyUnlockCraft,
            hasCraftedBefore,
            useQuickSynthesis,
            selectedMacroId);
    }

    public static bool UsesSelectedMacro(CraftingExecutionContext executionContext)
        => !string.IsNullOrEmpty(executionContext.SelectedMacroId)
            && CraftingGameInterop.UserMacroLibrary.GetMacroByStringId(executionContext.SelectedMacroId) != null;

    public static bool UsesRaphaelSolver(CraftingExecutionContext executionContext)
        => !executionContext.UseQuickSynthesis
            && executionContext.EffectiveSolverMode == VulcanSolverMode.PureRaphael
            && !UsesSelectedMacro(executionContext);

    public static bool TryResolveListExecutionContext(
        CraftingListDefinition list,
        uint recipeId,
        bool isOriginalRecipe,
        out CraftingExecutionContext context)
    {
        context = null!;
        if (!TryCreateListSourceItem(list, recipeId, isOriginalRecipe, out var sourceItem))
        {
            GatherBuddy.Log.Debug(
                $"[CraftingContextResolver] Unable to resolve list execution context source for recipe {recipeId} (Original={isOriginalRecipe}) in list '{list.Name}'");
            return false;
        }

        return TryResolveListExecutionContext(list, sourceItem, out context);
    }

    public static bool TryResolveListExecutionContext(
        CraftingListDefinition list,
        uint recipeId,
        bool isOriginalRecipe,
        RecipeCraftSettings? sourceSettingsOverride,
        out CraftingExecutionContext context)
    {
        context = null!;
        if (!TryCreateListSourceItem(list, recipeId, isOriginalRecipe, out var sourceItem))
        {
            GatherBuddy.Log.Debug(
                $"[CraftingContextResolver] Unable to resolve list execution context source override for recipe {recipeId} (Original={isOriginalRecipe}) in list '{list.Name}'");
            return false;
        }

        return TryResolveListExecutionContext(list, sourceItem, sourceSettingsOverride, out context);
    }

    public static bool TryResolveListExecutionContext(
        CraftingListDefinition list,
        CraftingListItem sourceItem,
        out CraftingExecutionContext context)
        => TryResolveListExecutionContext(list, sourceItem, null, false, out context);

    public static bool TryResolveListExecutionContext(
        CraftingListDefinition list,
        CraftingListItem sourceItem,
        RecipeCraftSettings? sourceSettingsOverride,
        out CraftingExecutionContext context)
        => TryResolveListExecutionContext(list, sourceItem, sourceSettingsOverride, true, out context);

    private static bool TryResolveListExecutionContext(
        CraftingListDefinition list,
        CraftingListItem sourceItem,
        RecipeCraftSettings? sourceSettingsOverride,
        bool useSourceSettingsOverride,
        out CraftingExecutionContext context)
    {
        context = null!;
        var recipe = RecipeManager.GetRecipe(sourceItem.RecipeId);
        if (!recipe.HasValue)
        {
            GatherBuddy.Log.Debug(
                $"[CraftingContextResolver] Unable to resolve execution context for missing recipe {sourceItem.RecipeId} in list '{list.Name}'");
            return false;
        }

        var normalizedSourceItem = new CraftingListItem(sourceItem.RecipeId, sourceItem.Quantity)
        {
            Options = new ListItemOptions
            {
                Skipping = sourceItem.Options.Skipping,
                NQOnly = sourceItem.Options.NQOnly,
            },
            IngredientPreferences = [with(sourceItem.IngredientPreferences)],
            ConsumableOverrides = sourceItem.ConsumableOverrides.Clone(),
            IsOriginalRecipe = sourceItem.IsOriginalRecipe,
            CraftSettings = useSourceSettingsOverride ? sourceSettingsOverride?.Clone() : sourceItem.CraftSettings?.Clone(),
        };
        var effectiveItem = BuildEffectiveListExecutionItem(normalizedSourceItem, recipe.Value, list);
        context = ResolveExecutionContext(effectiveItem, recipe.Value, list.Consumables);
        return true;
    }

    public static bool TryBuildSimulationContext(
        CraftingListItem item,
        Recipe recipe,
        CraftingListConsumableSettings? listConsumables,
        CraftingStatsSource statsSource,
        out CraftingSimulationContext context)
    {
        var executionContext = ResolveExecutionContext(item, recipe, listConsumables);
        return TryBuildSimulationContext(recipe, executionContext, statsSource, CraftingSimulationIntent.Execution, out context);
    }

    public static bool TryBuildSimulationContext(
        Recipe recipe,
        CraftingExecutionContext executionContext,
        CraftingStatsSource statsSource,
        out CraftingSimulationContext context)
        => TryBuildSimulationContext(recipe, executionContext, statsSource, CraftingSimulationIntent.Execution, out context);

    public static bool TryBuildSimulationContext(
        Recipe recipe,
        CraftingExecutionContext executionContext,
        CraftingStatsSource statsSource,
        CraftingSimulationIntent intent,
        out CraftingSimulationContext context)
    {
        context = null!;

        var requiredJob = (uint)(recipe.CraftType.RowId + 8);
        var stats = ResolvePlayerStats(requiredJob, executionContext.ConsumableSettings, statsSource, intent);
        if (stats == null)
        {
            return false;
        }

        var initialQuality = executionContext.QualityPolicy.CalculateGuaranteedInitialQuality(recipe);
        var craft = GameStateBuilder.BuildCraftState(CraftingStateBuilder.BuildRecipeInfo(recipe), stats) with { InitialQuality = initialQuality };
        var validationContext = intent == CraftingSimulationIntent.ValidatorPreview
            ? BuildValidatorPreviewContext(executionContext.ConsumableSettings)
            : null;
        var request = RaphaelSolveRequest.FromCraftState(craft, GatherBuddy.Config.RaphaelSolverConfig.RaphaelAllowSpecialistActions, validationContext);
        context = new(executionContext, stats, craft, request);
        return true;
    }

    public static bool HasRecipeCraftedBefore(Recipe recipe)
    {
        if (recipe.SecretRecipeBook.RowId > 0)
        {
            return true;
        }

        return QuestManager.IsRecipeComplete(recipe.RowId);
    }

    public static CraftingQualityPolicy GetQualityPolicy(CraftingListItem item, Recipe recipe)
    {
        item.QualityPolicy ??= CraftingQualityPolicyResolver.Resolve(recipe, item.CraftSettings);
        if (item.IngredientPreferences.Count == 0)
        {
            item.IngredientPreferences = item.QualityPolicy.BuildGuaranteedHQPreferences();
        }

        return item.QualityPolicy;
    }

    public static RecipeCraftSettings? BuildConsumableSettings(CraftingListItem item, CraftingListConsumableSettings? listConsumables)
    {
        if (listConsumables == null && !item.ConsumableOverrides.HasAnyOverrides() && item.CraftSettings == null)
        {
            return null;
        }

        var foodItemId = listConsumables?.FoodItemId;
        var foodHQ = listConsumables?.FoodHQ ?? false;
        var medicineItemId = listConsumables?.MedicineItemId;
        var medicineHQ = listConsumables?.MedicineHQ ?? false;
        var manualItemId = listConsumables?.ManualItemId;
        var squadronManualItemId = listConsumables?.SquadronManualItemId;

        if (item.CraftSettings != null && item.CraftSettings.HasAnySettings())
        {
            var craftSettings = item.CraftSettings;
            var effectiveFoodMode = craftSettings.FoodMode == ConsumableOverrideMode.Inherit && craftSettings.FoodItemId.HasValue
                ? ConsumableOverrideMode.Specific
                : craftSettings.FoodMode;
            var effectiveMedicineMode = craftSettings.MedicineMode == ConsumableOverrideMode.Inherit && craftSettings.MedicineItemId.HasValue
                ? ConsumableOverrideMode.Specific
                : craftSettings.MedicineMode;
            var effectiveManualMode = craftSettings.ManualMode == ConsumableOverrideMode.Inherit && craftSettings.ManualItemId.HasValue
                ? ConsumableOverrideMode.Specific
                : craftSettings.ManualMode;
            var effectiveSquadronMode = craftSettings.SquadronManualMode == ConsumableOverrideMode.Inherit && craftSettings.SquadronManualItemId.HasValue
                ? ConsumableOverrideMode.Specific
                : craftSettings.SquadronManualMode;

            ApplyOverride(new ConsumableOverride { Mode = effectiveFoodMode, ItemId = craftSettings.FoodItemId, HQ = craftSettings.FoodHQ }, ref foodItemId, ref foodHQ);
            ApplyOverride(new ConsumableOverride { Mode = effectiveMedicineMode, ItemId = craftSettings.MedicineItemId, HQ = craftSettings.MedicineHQ }, ref medicineItemId, ref medicineHQ);
            ApplyOverride(new ConsumableOverride { Mode = effectiveManualMode, ItemId = craftSettings.ManualItemId }, ref manualItemId);
            ApplyOverride(new ConsumableOverride { Mode = effectiveSquadronMode, ItemId = craftSettings.SquadronManualItemId }, ref squadronManualItemId);
        }
        else
        {
            ApplyOverride(item.ConsumableOverrides.Food, ref foodItemId, ref foodHQ);
            ApplyOverride(item.ConsumableOverrides.Medicine, ref medicineItemId, ref medicineHQ);
            ApplyOverride(item.ConsumableOverrides.Manual, ref manualItemId);
            ApplyOverride(item.ConsumableOverrides.SquadronManual, ref squadronManualItemId);
        }

        if (!foodItemId.HasValue && !medicineItemId.HasValue && !manualItemId.HasValue && !squadronManualItemId.HasValue)
        {
            return null;
        }

        return new RecipeCraftSettings
        {
            FoodItemId = foodItemId,
            FoodHQ = foodHQ,
            MedicineItemId = medicineItemId,
            MedicineHQ = medicineHQ,
            ManualItemId = manualItemId,
            SquadronManualItemId = squadronManualItemId,
        };
    }

    private static GameStateBuilder.PlayerStats? ResolvePlayerStats(
        uint requiredJob,
        RecipeCraftSettings? consumableSettings,
        CraftingStatsSource statsSource,
        CraftingSimulationIntent intent)
    {
        if (intent == CraftingSimulationIntent.ValidatorPreview)
        {
            var stats = GearsetStatsReader.ReadGearsetStatsForJob(requiredJob);
            var previewConsumables = ConsumableChecker.GetValidatorPreviewCraftStatConsumables(consumableSettings);
            if (stats != null && previewConsumables != null)
            {
                stats = GearsetStatsReader.ApplyConsumablesToStats(stats, previewConsumables);
            }

            return stats;
        }

        if (statsSource == CraftingStatsSource.AlwaysGearsetStats)
        {
            var stats = GearsetStatsReader.ReadGearsetStatsForJob(requiredJob);
            var projectedConsumables = ConsumableChecker.GetProjectedCraftStatConsumables(consumableSettings);
            if (stats != null && projectedConsumables != null)
            {
                stats = GearsetStatsReader.ApplyConsumablesToStats(stats, projectedConsumables);
            }

            return stats;
        }

        var currentJob = Dalamud.Objects.LocalPlayer?.ClassJob.RowId ?? 0;
        if (currentJob == requiredJob)
        {
            var stats = CraftingStateBuilder.GetCurrentPlayerStats();
            var pendingConsumables = ConsumableChecker.GetPendingCraftStatConsumables(consumableSettings);
            if (stats != null && pendingConsumables != null)
            {
                stats = GearsetStatsReader.ApplyConsumablesToStats(stats, pendingConsumables);
            }

            return stats;
        }

        var gearsetStats = GearsetStatsReader.ReadGearsetStatsForJob(requiredJob);
        var projected = ConsumableChecker.GetProjectedCraftStatConsumables(consumableSettings);
        if (gearsetStats != null && projected != null)
        {
            gearsetStats = GearsetStatsReader.ApplyConsumablesToStats(gearsetStats, projected);
        }

        return gearsetStats;
    }

    private static string BuildValidatorPreviewContext(RecipeCraftSettings? settings)
        => $"validator:{settings?.FoodItemId ?? 0}:{(settings?.FoodHQ == true ? 1 : 0)}:{settings?.MedicineItemId ?? 0}:{(settings?.MedicineHQ == true ? 1 : 0)}";

    private static bool TryCreateListSourceItem(CraftingListDefinition list, uint recipeId, bool isOriginalRecipe, out CraftingListItem item)
    {
        item = null!;
        if (isOriginalRecipe)
        {
            var originalItem = list.Recipes.FirstOrDefault(candidate => candidate.RecipeId == recipeId);
            if (originalItem == null)
            {
                return false;
            }

            item = new CraftingListItem(recipeId, originalItem.Quantity)
            {
                Options = new ListItemOptions
                {
                    Skipping = originalItem.Options.Skipping,
                    NQOnly = originalItem.Options.NQOnly,
                },
                IngredientPreferences = [with(originalItem.IngredientPreferences)],
                ConsumableOverrides = originalItem.ConsumableOverrides.Clone(),
                IsOriginalRecipe = true,
                CraftSettings = originalItem.CraftSettings?.Clone(),
            };
            return true;
        }

        list.PrecraftOptions.TryGetValue(recipeId, out var precraftOptions);
        item = new CraftingListItem(recipeId, 1)
        {
            Options = new ListItemOptions
            {
                Skipping = precraftOptions?.Skipping ?? false,
                NQOnly = precraftOptions?.NQOnly ?? false,
            },
            IsOriginalRecipe = false,
            CraftSettings = list.PrecraftCraftSettings.GetValueOrDefault(recipeId)?.Clone(),
        };
        return true;
    }

    private static CraftingListItem BuildEffectiveListExecutionItem(CraftingListItem sourceItem, Recipe recipe, CraftingListDefinition list)
    {
        var (effectiveMacroId, effectiveSolverOverride) = CraftingListQueueBuilder.ResolveEffectiveMacroSelection(
            sourceItem.CraftSettings,
            !sourceItem.IsOriginalRecipe,
            list);
        var effectiveSettings = CraftingListQueueBuilder.BuildEffectiveQueueCraftSettings(
            recipe,
            sourceItem.CraftSettings,
            effectiveMacroId,
            effectiveSolverOverride,
            list.UseAllHQ,
            !recipe.CanQuickSynth && list.ShouldForcePreferNQ(sourceItem.IsOriginalRecipe));
        var qualityPolicy = CraftingQualityPolicyResolver.Resolve(
            recipe,
            effectiveSettings,
            list.GetQualityOverrideMode(recipe, sourceItem.IsOriginalRecipe));
        return new(sourceItem.RecipeId, sourceItem.Quantity)
        {
            Options = new ListItemOptions
            {
                Skipping = sourceItem.Options.Skipping,
                NQOnly = sourceItem.Options.NQOnly || list.ShouldForceQuickSynth(recipe, sourceItem.IsOriginalRecipe),
            },
            IngredientPreferences = qualityPolicy.BuildGuaranteedHQPreferences(),
            ConsumableOverrides = sourceItem.ConsumableOverrides.Clone(),
            IsOriginalRecipe = sourceItem.IsOriginalRecipe,
            CraftSettings = effectiveSettings,
            QualityPolicy = qualityPolicy,
        };
    }

    private static void ApplyOverride(ConsumableOverride? overrideSetting, ref uint? itemId, ref bool hq)
    {
        if (overrideSetting == null)
        {
            return;
        }

        switch (overrideSetting.Mode)
        {
            case ConsumableOverrideMode.Inherit:
                return;
            case ConsumableOverrideMode.None:
                itemId = null;
                hq = false;
                return;
            case ConsumableOverrideMode.Specific:
                itemId = overrideSetting.ItemId;
                hq = overrideSetting.HQ;
                return;
        }
    }

    private static void ApplyOverride(ConsumableOverride? overrideSetting, ref uint? itemId)
    {
        if (overrideSetting == null)
        {
            return;
        }

        switch (overrideSetting.Mode)
        {
            case ConsumableOverrideMode.Inherit:
                return;
            case ConsumableOverrideMode.None:
                itemId = null;
                return;
            case ConsumableOverrideMode.Specific:
                itemId = overrideSetting.ItemId;
                return;
        }
    }
}
