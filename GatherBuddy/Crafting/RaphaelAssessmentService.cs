using GatherBuddy.Vulcan;
using Lumina.Excel.Sheets;
using System.Collections.Generic;

namespace GatherBuddy.Crafting;

public enum RaphaelAssessmentState
{
    NotApplicable,
    Unavailable,
    NotGenerated,
    Generating,
    Failed,
    Ready,
}

public enum RaphaelAssessmentOutcome
{
    None,
    SimulationFailed,
    Incomplete,
    FailedDurability,
    FailedQualityRequirement,
    NoQualityRequired,
    MinimumQualityMet,
    CollectibleTier1,
    CollectibleTier2,
    CollectibleTier3,
    PartialQuality,
    FullQuality,
}

public sealed record RaphaelAssessment(
    RaphaelAssessmentState State,
    RaphaelAssessmentOutcome Outcome,
    string Summary,
    string Details,
    RaphaelSolveRequest? Request = null,
    string? FailureReason = null,
    int Progress = 0,
    int RequiredProgress = 0,
    int Quality = 0,
    int MaxQuality = 0,
    int QualityTarget = 0,
    int StepCount = 0)
{
    public float ProgressPercent => RequiredProgress <= 0 ? 0f : Progress * 100f / RequiredProgress;
    public float QualityPercent => MaxQuality <= 0 ? 0f : Quality * 100f / MaxQuality;
}

public static class RaphaelAssessmentService
{
    public static bool TryAssessRecipe(uint recipeId, RecipeCraftSettings? settings, out RaphaelAssessment assessment)
    {
        if (!TryGetRecipe(recipeId, out var recipe, out assessment))
        {
            return false;
        }

        if (IsQualityAgnostic(recipe))
        {
            assessment = CreateNotApplicableAssessment(recipe.RowId, null);
            return true;
        }

        var item = new CraftingListItem(recipe.RowId, 1)
        {
            IsOriginalRecipe = true,
            CraftSettings = settings?.Clone(),
        };
        var executionContext = CraftingContextResolver.ResolveExecutionContext(item, recipe, null);
        return TryAssessExecutionContext(recipe, executionContext, out assessment);
    }

    public static bool TryAssessListRecipe(uint recipeId, CraftingListDefinition list, RecipeCraftSettings? settings, out RaphaelAssessment assessment)
        => TryAssessListContext(recipeId, list, true, settings, out assessment);

    public static bool TryAssessListPrecraft(uint recipeId, CraftingListDefinition list, RecipeCraftSettings? settings, out RaphaelAssessment assessment)
        => TryAssessListContext(recipeId, list, false, settings, out assessment);

    public static bool TryAssessListQueueItem(uint recipeId, bool isOriginalRecipe, CraftingListDefinition list, out RaphaelAssessment assessment)
        => TryAssessListContext(recipeId, list, isOriginalRecipe, null, out assessment);

    public static bool TryAssessCachedSolution(CachedRaphaelSolution solution, out RaphaelAssessment assessment)
    {
        assessment = CreateUnavailableAssessment(solution.Request.RecipeId, solution.Request, "Recipe data unavailable for cached Raphael solution.");
        if (!TryGetRecipe(solution.Request.RecipeId, out var recipe, out _))
        {
            return false;
        }

        if (!TryBuildCraftState(solution.Request, recipe, out var craft))
        {
            GatherBuddy.Log.Debug($"[RaphaelAssessment] Unable to build craft state for cached solution {solution.Key}");
            return false;
        }

        assessment = Assess(solution.Request, craft, solution);
        return true;
    }

    public static bool TryQueueWarmupForRecipe(uint recipeId, RecipeCraftSettings? settings)
    {
        if (!TryGetRecipe(recipeId, out var recipe, out _))
        {
            return false;
        }

        if (IsQualityAgnostic(recipe))
        {
            return false;
        }

        var item = new CraftingListItem(recipe.RowId, 1)
        {
            IsOriginalRecipe = true,
            CraftSettings = settings?.Clone(),
        };
        var executionContext = CraftingContextResolver.ResolveExecutionContext(item, recipe, null);
        if (!CraftingContextResolver.UsesRaphaelSolver(executionContext))
        {
            return false;
        }

        return TryQueueWarmupForExecutionContext(recipe, executionContext, RaphaelSolvePriority.Urgent);
    }

    public static bool TryQueueWarmupForListRecipe(uint recipeId, CraftingListDefinition list, RecipeCraftSettings? settings)
        => TryQueueWarmupForListContext(recipeId, list, true, settings, RaphaelSolvePriority.Urgent);

    public static int QueueWarmupForAddedListRecipe(uint recipeId, CraftingListDefinition list)
    {
        var queued = 0;
        var originalSettings = list.Recipes.Find(item => item.RecipeId == recipeId)?.CraftSettings;
        if (TryQueueWarmupForListRecipe(recipeId, list, originalSettings))
        {
            queued++;
        }

        var dependentPrecraftRecipeIds = CollectDependentPrecraftRecipeIds(recipeId, list);
        if (dependentPrecraftRecipeIds.Count == 0)
        {
            return queued;
        }

        var plan = list.CreatePlan();
        foreach (var plannedItem in plan.Recipes)
        {
            if (plannedItem.IsOriginalRecipe || !dependentPrecraftRecipeIds.Contains(plannedItem.RecipeId))
            {
                continue;
            }

            if (TryQueueWarmupForPrecraft(plannedItem.RecipeId, list, list.PrecraftCraftSettings.GetValueOrDefault(plannedItem.RecipeId)))
            {
                queued++;
            }
        }

        if (queued > 0)
        {
            GatherBuddy.Log.Debug($"[RaphaelAssessment] Queued {queued} Raphael warmup request(s) for added recipe {recipeId} in list '{list.Name}' including dependent precrafts where needed");
        }

        return queued;
    }

    public static bool TryQueueWarmupForPrecraft(uint recipeId, CraftingListDefinition list, RecipeCraftSettings? settings)
        => TryQueueWarmupForListContext(recipeId, list, false, settings, RaphaelSolvePriority.Urgent);

    public static int QueueWarmupForList(CraftingListDefinition list)
    {
        var queuedRequests = new Dictionary<string, RaphaelSolveRequest>();
        var plan = list.CreatePlan();
        foreach (var plannedItem in plan.Recipes)
        {
            if (!TryGetRecipe(plannedItem.RecipeId, out var recipe, out _))
            {
                continue;
            }

            if (IsQualityAgnostic(recipe))
            {
                continue;
            }

            if (!CraftingContextResolver.TryResolveListExecutionContext(list, plannedItem.RecipeId, plannedItem.IsOriginalRecipe, out var executionContext)
             || !CraftingContextResolver.UsesRaphaelSolver(executionContext))
            {
                continue;
            }

            if (!TryBuildSimulationContext(recipe, executionContext, out var context))
            {
                continue;
            }

            var request = context.RaphaelRequest;
            if (!GatherBuddy.RaphaelSolveCoordinator.IsKnown(request))
            {
                queuedRequests.TryAdd(request.GetKey(), request);
            }
        }

        if (queuedRequests.Count == 0)
        {
            return 0;
        }

        GatherBuddy.Log.Debug($"[RaphaelAssessment] Queueing {queuedRequests.Count} background Raphael warmup request(s) for list '{list.Name}'");
        GatherBuddy.RaphaelSolveCoordinator.EnqueueSolvesFromRequests(queuedRequests.Values, RaphaelSolvePriority.Background);
        return queuedRequests.Count;
    }

    private static bool TryAssessListContext(
        uint recipeId,
        CraftingListDefinition list,
        bool isOriginalRecipe,
        RecipeCraftSettings? settings,
        out RaphaelAssessment assessment)
    {
        if (!TryGetRecipe(recipeId, out var recipe, out assessment))
        {
            return false;
        }

        if (IsQualityAgnostic(recipe))
        {
            assessment = CreateNotApplicableAssessment(recipe.RowId, null);
            return true;
        }

        CraftingExecutionContext executionContext;
        var hasExecutionContext = settings == null
            ? CraftingContextResolver.TryResolveListExecutionContext(list, recipeId, isOriginalRecipe, out executionContext)
            : CraftingContextResolver.TryResolveListExecutionContext(list, recipeId, isOriginalRecipe, settings, out executionContext);
        if (!hasExecutionContext)
        {
            assessment = CreateUnavailableAssessment(recipeId, null, "List crafting settings could not be resolved.");
            return false;
        }

        return TryAssessExecutionContext(recipe, executionContext, out assessment);
    }

    private static bool TryAssessExecutionContext(
        Recipe recipe,
        CraftingExecutionContext executionContext,
        out RaphaelAssessment assessment)
    {
        if (!TryBuildSimulationContext(recipe, executionContext, out var context))
        {
            assessment = CreateUnavailableAssessment(recipe.RowId, null, "No current or gearset stats available for this recipe.");
            return false;
        }

        assessment = Assess(context.RaphaelRequest, context.SimulationState);
        return true;
    }

    private static HashSet<uint> CollectDependentPrecraftRecipeIds(uint recipeId, CraftingListDefinition list)
    {
        var recipeIds = new HashSet<uint>();
        var visitedRecipeIds = new HashSet<uint>();
        CollectDependentPrecraftRecipeIds(recipeId, list, recipeIds, visitedRecipeIds);
        return recipeIds;
    }

    private static void CollectDependentPrecraftRecipeIds(uint recipeId, CraftingListDefinition list, HashSet<uint> recipeIds, HashSet<uint> visitedRecipeIds)
    {
        if (!visitedRecipeIds.Add(recipeId))
        {
            return;
        }

        var recipe = RecipeManager.GetRecipe(recipeId);
        if (!recipe.HasValue)
        {
            GatherBuddy.Log.Debug($"[RaphaelAssessment] Unable to resolve recipe {recipeId} while collecting dependent precraft warmups");
            return;
        }

        foreach (var (itemId, _) in RecipeManager.GetIngredients(recipe.Value))
        {
            var subRecipe = ResolveSubRecipe(itemId, list);
            if (!subRecipe.HasValue)
            {
                continue;
            }

            recipeIds.Add(subRecipe.Value.RowId);
            CollectDependentPrecraftRecipeIds(subRecipe.Value.RowId, list, recipeIds, visitedRecipeIds);
        }
    }

    private static Recipe? ResolveSubRecipe(uint itemId, CraftingListDefinition list)
    {
        if (list.PrecraftRecipeOverrides.TryGetValue(itemId, out var overrideRecipeId))
        {
            var overrideRecipe = RecipeManager.GetRecipe(overrideRecipeId);
            if (overrideRecipe.HasValue)
            {
                return overrideRecipe;
            }
        }

        return RecipeManager.GetRecipeForItem(itemId);
    }

    private static bool TryQueueWarmupForListContext(
        uint recipeId,
        CraftingListDefinition list,
        bool isOriginalRecipe,
        RecipeCraftSettings? settings,
        RaphaelSolvePriority priority)
    {
        if (!TryGetRecipe(recipeId, out var recipe, out _))
        {
            return false;
        }

        if (IsQualityAgnostic(recipe))
        {
            return false;
        }

        if (!CraftingContextResolver.TryResolveListExecutionContext(list, recipeId, isOriginalRecipe, settings, out var executionContext)
         || !CraftingContextResolver.UsesRaphaelSolver(executionContext))
        {
            return false;
        }

        return TryQueueWarmupForExecutionContext(recipe, executionContext, priority);
    }

    private static bool TryQueueWarmupForExecutionContext(
        Recipe recipe,
        CraftingExecutionContext executionContext,
        RaphaelSolvePriority priority)
    {
        if (!TryBuildSimulationContext(recipe, executionContext, out var context))
        {
            GatherBuddy.Log.Debug($"[RaphaelAssessment] Unable to queue Raphael warmup for recipe {recipe.RowId}: no usable stats");
            return false;
        }

        var request = context.RaphaelRequest;
        var queued = GatherBuddy.RaphaelSolveCoordinator.EnqueueOrPromoteRequest(request, priority);
        if (queued)
        {
            GatherBuddy.Log.Debug($"[RaphaelAssessment] Queueing Raphael warmup for recipe {recipe.RowId} with key {request.GetKey()} at {priority} priority");
        }

        return queued;
    }

    private static bool TryBuildSimulationContext(
        Recipe recipe,
        CraftingExecutionContext executionContext,
        out CraftingSimulationContext context)
    {
        if (!CraftingContextResolver.TryBuildSimulationContext(
                recipe,
                executionContext,
                CraftingStatsSource.AlwaysGearsetStats,
                CraftingSimulationIntent.ValidatorPreview,
                out context))
        {
            GatherBuddy.Log.Debug($"[RaphaelAssessment] Failed to build simulation context for recipe {recipe.RowId}");
            return false;
        }

        return true;
    }

    private static RaphaelAssessment Assess(RaphaelSolveRequest request, CraftState craft, CachedRaphaelSolution? cachedSolution = null)
    {
        if (cachedSolution == null)
        {
            if (GatherBuddy.RaphaelSolveCoordinator.TryGetSolution(request, out var resolved) && resolved != null)
            {
                cachedSolution = resolved;
            }
        }

        if (cachedSolution != null && !cachedSolution.IsFailed && cachedSolution.ActionIds.Count > 0)
        {
            return AssessReadySolution(request, craft, cachedSolution);
        }

        if (GatherBuddy.RaphaelSolveCoordinator.HasFailedSolution(request, out var failureReason))
        {
            return new RaphaelAssessment(
                RaphaelAssessmentState.Failed,
                RaphaelAssessmentOutcome.None,
                "Raphael validation failed.",
                string.IsNullOrWhiteSpace(failureReason) ? "Raphael could not generate a solution for this configuration." : failureReason!,
                request,
                failureReason);
        }

        if (GatherBuddy.RaphaelSolveCoordinator.IsKnown(request))
        {
            return new RaphaelAssessment(
                RaphaelAssessmentState.Generating,
                RaphaelAssessmentOutcome.None,
                "Raphael validation generating...",
                "A solve request exists for this exact recipe, stat, and quality configuration.",
                request);
        }

        return new RaphaelAssessment(
            RaphaelAssessmentState.NotGenerated,
            RaphaelAssessmentOutcome.None,
            "No Raphael validation generated yet.",
            "Save this configuration or queue Raphael validation to test this exact recipe setup.",
            request);
    }

    private static RaphaelAssessment AssessReadySolution(RaphaelSolveRequest request, CraftState craft, CachedRaphaelSolution solution)
    {
        var solver = new RaphaelMacroSolver(solution, craft);
        var finalStep = SolverUtils.SimulateSolverExecution(solver, craft, craft.InitialQuality);
        if (finalStep == null)
        {
            return new RaphaelAssessment(
                RaphaelAssessmentState.Ready,
                RaphaelAssessmentOutcome.SimulationFailed,
                "Raphael generated a solve, but simulation could not validate it.",
                $"Generated solution with {solution.ActionIds.Count} step(s), but the simulator could not confirm completion.",
                request,
                StepCount: solution.ActionIds.Count);
        }

        var outcome = ClassifyOutcome(craft, finalStep);
        var qualityTarget = ResolveQualityTarget(craft, outcome);
        var progressPercent = craft.CraftProgress <= 0 ? 0f : finalStep.Progress * 100f / craft.CraftProgress;
        var qualityPercent = craft.CraftQualityMax <= 0 ? 0f : finalStep.Quality * 100f / craft.CraftQualityMax;
        var hqChance = Calculations.GetHQChance(qualityPercent);
        var summary = BuildReadySummary(outcome, hqChance);
        var details = outcome == RaphaelAssessmentOutcome.PartialQuality
            ? $"Progress {finalStep.Progress}/{craft.CraftProgress} ({progressPercent:F0}%), Quality {finalStep.Quality}/{craft.CraftQualityMax}, HQ Chance {hqChance}%, Steps {solution.ActionIds.Count}."
            : $"Progress {finalStep.Progress}/{craft.CraftProgress} ({progressPercent:F0}%), Quality {finalStep.Quality}/{craft.CraftQualityMax} ({qualityPercent:F0}%), Steps {solution.ActionIds.Count}.";

        return new RaphaelAssessment(
            RaphaelAssessmentState.Ready,
            outcome,
            summary,
            details,
            request,
            Progress: finalStep.Progress,
            RequiredProgress: craft.CraftProgress,
            Quality: finalStep.Quality,
            MaxQuality: craft.CraftQualityMax,
            QualityTarget: qualityTarget,
            StepCount: solution.ActionIds.Count);
    }

    private static RaphaelAssessmentOutcome ClassifyOutcome(CraftState craft, StepState step)
    {
        if (step.Progress < craft.CraftProgress)
        {
            return step.Durability > 0 ? RaphaelAssessmentOutcome.Incomplete : RaphaelAssessmentOutcome.FailedDurability;
        }

        if (craft.CraftCollectible || craft.CraftExpert)
        {
            if ((craft.IshgardExpert || craft.IsCosmic) && step.Quality >= craft.CraftQualityMax)
            {
                return RaphaelAssessmentOutcome.FullQuality;
            }

            if (step.Quality >= craft.CraftQualityMin3 && craft.CraftQualityMin3 > 0)
            {
                return RaphaelAssessmentOutcome.CollectibleTier3;
            }

            if (step.Quality >= craft.CraftQualityMin2 && craft.CraftQualityMin2 > 0 && craft.CraftQualityMin2 != craft.CraftQualityMin1)
            {
                return RaphaelAssessmentOutcome.CollectibleTier2;
            }

            if (step.Quality >= craft.CraftQualityMin1 && craft.CraftQualityMin1 > 0)
            {
                return RaphaelAssessmentOutcome.CollectibleTier1;
            }

            return RaphaelAssessmentOutcome.FailedQualityRequirement;
        }

        if (craft.CraftHQ)
        {
            return step.Quality >= craft.CraftQualityMax ? RaphaelAssessmentOutcome.FullQuality : RaphaelAssessmentOutcome.PartialQuality;
        }

        if (craft.CraftQualityMin1 > 0)
        {
            return step.Quality >= craft.CraftQualityMin1 ? RaphaelAssessmentOutcome.MinimumQualityMet : RaphaelAssessmentOutcome.FailedQualityRequirement;
        }

        return RaphaelAssessmentOutcome.NoQualityRequired;
    }

    private static int ResolveQualityTarget(CraftState craft, RaphaelAssessmentOutcome outcome)
        => outcome switch
        {
            RaphaelAssessmentOutcome.FullQuality => craft.CraftQualityMax,
            RaphaelAssessmentOutcome.CollectibleTier3 => craft.CraftQualityMin3,
            RaphaelAssessmentOutcome.CollectibleTier2 => craft.CraftQualityMin2,
            RaphaelAssessmentOutcome.CollectibleTier1 => craft.CraftQualityMin1,
            RaphaelAssessmentOutcome.MinimumQualityMet => craft.CraftQualityMin1,
            _ => 0,
        };

    private static string BuildReadySummary(RaphaelAssessmentOutcome outcome, int hqChance)
        => outcome switch
        {
            RaphaelAssessmentOutcome.FullQuality => "Validated — completes with full quality and progress.",
            RaphaelAssessmentOutcome.CollectibleTier3 => "Validated — reaches collectible tier 3.",
            RaphaelAssessmentOutcome.CollectibleTier2 => "Validated — reaches collectible tier 2.",
            RaphaelAssessmentOutcome.CollectibleTier1 => "Validated — reaches collectible tier 1.",
            RaphaelAssessmentOutcome.MinimumQualityMet => "Validated — meets the required quality target.",
            RaphaelAssessmentOutcome.NoQualityRequired => "Validated — completes progress. No quality target is required.",
            RaphaelAssessmentOutcome.PartialQuality => $"Validated — completes progress at {hqChance}% HQ chance.",
            RaphaelAssessmentOutcome.FailedDurability => "Raphael generated a solve, but simulation fails on durability.",
            RaphaelAssessmentOutcome.FailedQualityRequirement => "Raphael generated a solve, but simulation misses the quality target.",
            RaphaelAssessmentOutcome.Incomplete => "Raphael generated a solve, but simulation does not finish the craft.",
            _ => "Raphael validation is ready.",
        };

    private static bool TryGetRecipe(uint recipeId, out Recipe recipe, out RaphaelAssessment assessment)
    {
        assessment = CreateUnavailableAssessment(recipeId, null, "Recipe data could not be resolved.");
        var resolved = RecipeManager.GetRecipe(recipeId);
        if (!resolved.HasValue)
        {
            GatherBuddy.Log.Debug($"[RaphaelAssessment] Recipe {recipeId} could not be resolved");
            recipe = default;
            return false;
        }

        recipe = resolved.Value;
        return true;
    }

    private static bool TryBuildCraftState(RaphaelSolveRequest request, Recipe recipe, out CraftState craft)
    {
        var stats = new GameStateBuilder.PlayerStats(
            request.Craftsmanship,
            request.Control,
            request.CP,
            request.Level,
            request.Manipulation,
            request.Specialist,
            false);

        craft = GameStateBuilder.BuildCraftState(CraftingStateBuilder.BuildRecipeInfo(recipe), stats);
        craft.InitialQuality = request.InitialQuality;
        return true;
    }

    private static bool IsQualityAgnostic(Recipe recipe)
        => !recipe.CanHq && !recipe.IsExpert && !recipe.ItemResult.Value.AlwaysCollectable && recipe.RequiredQuality == 0;

    private static RaphaelAssessment CreateNotApplicableAssessment(uint recipeId, RaphaelSolveRequest? request)
        => new(
            RaphaelAssessmentState.NotApplicable,
            RaphaelAssessmentOutcome.None,
            "Raphael validation is not needed for this recipe.",
            "This recipe has no quality breakpoint to validate.",
            request ?? new RaphaelSolveRequest(recipeId, 0, 0, 0, 0, false, false, 0));

    private static RaphaelAssessment CreateUnavailableAssessment(uint recipeId, RaphaelSolveRequest? request, string details)
        => new(
            RaphaelAssessmentState.Unavailable,
            RaphaelAssessmentOutcome.None,
            "Raphael validation is unavailable.",
            details,
            request ?? new RaphaelSolveRequest(recipeId, 0, 0, 0, 0, false, false, 0));
}
