using GatherBuddy.Vulcan;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace GatherBuddy.Crafting;

public static class CraftingTimeEstimator
{
    private const int ActionExecutionMs = 3000;
    private const int QuickSynthCraftMs = 3000;
    private const int CraftOverheadMs = 5000;
    private const int DefaultActionCount = 15;

    private static readonly ConcurrentDictionary<string, int> _actionCountCache = new();

    public static long EstimateRemainingMs(IReadOnlyList<CraftingListItem> queue, int fromIndex, CraftingListConsumableSettings? listConsumables = null)
    {
        if (queue.Count == 0 || fromIndex >= queue.Count)
        {
            return 0;
        }

        var configuredActionDelayMs = GatherBuddy.Config.VulcanExecutionDelayMs;
        long total = 0;
        for (var i = fromIndex < 0 ? 0 : fromIndex; i < queue.Count; i++)
        {
            total += EstimateItemMs(queue[i], configuredActionDelayMs, listConsumables);
        }

        return total;
    }

    public static long EstimateItemMs(CraftingListItem item, int configuredActionDelayMs, CraftingListConsumableSettings? listConsumables = null)
    {
        var recipe = RecipeManager.GetRecipe(item.RecipeId);
        CraftingExecutionContext? executionContext = null;
        if (recipe.HasValue)
        {
            executionContext = CraftingContextResolver.ResolveExecutionContext(item, recipe.Value, listConsumables);
            if (executionContext.UseQuickSynthesis)
            {
                return QuickSynthCraftMs;
            }
        }

        var actions = ResolveActionCount(item, recipe, executionContext, listConsumables);
        var estimatedActionDurationMs = ActionExecutionMs + configuredActionDelayMs;
        return (long)actions * estimatedActionDurationMs + CraftOverheadMs;
    }

    private static int ResolveActionCount(
        CraftingListItem item,
        Recipe? recipe,
        CraftingExecutionContext? executionContext,
        CraftingListConsumableSettings? listConsumables)
    {
        if (!recipe.HasValue)
        {
            return DefaultActionCount;
        }

        var resolvedExecutionContext = executionContext ?? CraftingContextResolver.ResolveExecutionContext(item, recipe.Value, listConsumables);
        if (!CraftingContextResolver.TryBuildSimulationContext(recipe.Value, resolvedExecutionContext, CraftingStatsSource.PreferCurrentJobStats, out var simulationContext))
        {
            return DefaultActionCount;
        }

        if (!resolvedExecutionContext.ForceProgressOnlyUnlockCraft
            && TryResolveMacroActionCount(resolvedExecutionContext, simulationContext.SimulationState, resolvedExecutionContext.EffectiveSolverMode, out var macroActionCount))
        {
            return macroActionCount;
        }

        return ResolveNonMacroActionCount(simulationContext.SimulationState, resolvedExecutionContext.EffectiveSolverMode);
    }

    private static int ResolveNonMacroActionCount(CraftState craft, VulcanSolverMode effectiveSolverMode)
    {
        switch (effectiveSolverMode)
        {
            case VulcanSolverMode.PureRaphael:
                return ResolveRaphaelActionCount(craft);
            case VulcanSolverMode.StandardSolver:
                if (SupportsStandardSolver(craft))
                {
                    return ResolveSimulatedActionCount(
                        $"standard/{BuildCraftStateKey(craft)}/{BuildStandardSolverConfigKey()}",
                        craft,
                        () => new StandardSolver(GatherBuddy.Config.StandardSolverConfig));
                }

                if (SupportsProgressOnlySolver(craft))
                {
                    return ResolveSimulatedActionCount(
                        $"progress/{BuildCraftStateKey(craft)}",
                        craft,
                        () => new ProgressOnlySolver());
                }

                return DefaultActionCount;
            case VulcanSolverMode.ProgressOnly:
                if (SupportsProgressOnlySolver(craft))
                {
                    return ResolveSimulatedActionCount(
                        $"progress/{BuildCraftStateKey(craft)}",
                        craft,
                        () => new ProgressOnlySolver());
                }

                return DefaultActionCount;
            default:
                return DefaultActionCount;
        }
    }

    private static int ResolveSimulatedActionCount(string key, CraftState craft, Func<Solver> solverFactory)
    {
        if (_actionCountCache.TryGetValue(key, out var cachedCount))
        {
            return cachedCount;
        }

        var solver = solverFactory();
        if (!SolverUtils.TryEstimateActionCount(solver, craft, craft.InitialQuality, out var actionCount))
        {
            return DefaultActionCount;
        }

        _actionCountCache[key] = actionCount;
        return actionCount;
    }

    private static bool TryResolveMacroActionCount(CraftingExecutionContext executionContext, CraftState craft, VulcanSolverMode effectiveSolverMode, out int actionCount)
    {
        actionCount = 0;
        if (!TrySelectMacroForEstimate(executionContext.SelectedMacroId, craft, out var macro))
        {
            return false;
        }

        var fallback = CreateMacroFallbackSolver(craft, effectiveSolverMode, out var fallbackKey);
        var cacheKey =
            $"macro/{BuildCraftStateKey(craft)}/{BuildMacroFingerprint(macro)}/{(GatherBuddy.Config.SkipMacroStepIfUnable ? 1 : 0)}/{(GatherBuddy.Config.MacroFallbackEnabled ? 1 : 0)}/{fallbackKey}";
        if (_actionCountCache.TryGetValue(cacheKey, out actionCount))
        {
            return true;
        }

        var solver = new UserMacroSolver(macro, craft, fallback);
        if (!SolverUtils.TryEstimateActionCount(solver, craft, craft.InitialQuality, out actionCount))
        {
            return false;
        }

        _actionCountCache[cacheKey] = actionCount;
        return true;
    }

    private static bool TrySelectMacroForEstimate(string? selectedMacroId, CraftState craft, out UserMacro macro)
    {
        macro = null!;
        if (!string.IsNullOrEmpty(selectedMacroId))
        {
            var selectedMacro = CraftingGameInterop.UserMacroLibrary.GetMacroByStringId(selectedMacroId);
            if (selectedMacro != null)
            {
                if (MacroMeetsStats(selectedMacro, craft))
                {
                    macro = selectedMacro;
                    return true;
                }
                return false;
            }
        }

        foreach (var candidate in CraftingGameInterop.UserMacroLibrary.GetMacrosForRecipe(craft.RecipeId))
        {
            if (!MacroMeetsStats(candidate, craft))
            {
                continue;
            }

            macro = candidate;
            return true;
        }

        return false;
    }

    private static Solver? CreateMacroFallbackSolver(CraftState craft, VulcanSolverMode effectiveSolverMode, out string fallbackKey)
    {
        if (!GatherBuddy.Config.MacroFallbackEnabled)
        {
            fallbackKey = "none";
            return null;
        }

        switch (effectiveSolverMode)
        {
            case VulcanSolverMode.PureRaphael:
                if (TryGetRaphaelSolution(craft, out var raphaelSolution))
                {
                    fallbackKey = $"raphael/{raphaelSolution.Key}/{raphaelSolution.GeneratedAt.Ticks}";
                    return new RaphaelMacroSolver(raphaelSolution, craft);
                }

                fallbackKey = "raphael/pending";
                return null;
            case VulcanSolverMode.StandardSolver:
                if (SupportsStandardSolver(craft))
                {
                    fallbackKey = $"standard/{BuildStandardSolverConfigKey()}";
                    return new StandardSolver(GatherBuddy.Config.StandardSolverConfig);
                }

                if (SupportsProgressOnlySolver(craft))
                {
                    fallbackKey = "progress";
                    return new ProgressOnlySolver();
                }

                fallbackKey = "none";
                return null;
            case VulcanSolverMode.ProgressOnly:
                if (SupportsProgressOnlySolver(craft))
                {
                    fallbackKey = "progress";
                    return new ProgressOnlySolver();
                }

                fallbackKey = "none";
                return null;
            default:
                fallbackKey = "none";
                return null;
        }
    }

    private static int ResolveRaphaelActionCount(CraftState craft)
    {
        var request = RaphaelSolveRequest.FromCraftState(craft, GatherBuddy.Config.RaphaelSolverConfig.RaphaelAllowSpecialistActions);
        var cacheKey = $"raphael/{request.GetKey()}";
        if (_actionCountCache.TryGetValue(cacheKey, out var cachedCount))
        {
            return cachedCount;
        }

        var coord = GatherBuddy.RaphaelSolveCoordinator;
        if (coord.TryGetSolution(request, out var solved) && solved != null && solved.ActionIds.Count > 0)
        {
            _actionCountCache[cacheKey] = solved.ActionIds.Count;
            return solved.ActionIds.Count;
        }

        if (coord.HasFailedSolution(request, out _))
        {
            return DefaultActionCount;
        }

        return DefaultActionCount;
    }

    private static bool TryGetRaphaelSolution(CraftState craft, out CachedRaphaelSolution solution)
    {
        solution = null!;

        var request = RaphaelSolveRequest.FromCraftState(craft, GatherBuddy.Config.RaphaelSolverConfig.RaphaelAllowSpecialistActions);
        if (!GatherBuddy.RaphaelSolveCoordinator.TryGetSolution(request, out var resolved) || resolved == null || resolved.ActionIds.Count == 0)
        {
            return false;
        }

        solution = resolved;
        return true;
    }


    private static bool MacroMeetsStats(UserMacro macro, CraftState craft)
        => macro.MinCraftsmanship <= craft.StatCraftsmanship
            && macro.MinControl <= craft.StatControl
            && macro.MinCP <= craft.StatCP;

    private static bool SupportsStandardSolver(CraftState craft)
        => !craft.CraftExpert && (craft.CraftHQ || craft.CraftRequiredQuality > 0);

    private static bool SupportsProgressOnlySolver(CraftState craft)
        => !craft.CraftExpert && !craft.CraftCollectible;

    private static string BuildCraftStateKey(CraftState craft)
        => $"{craft.RecipeId}/{craft.StatLevel}/{craft.StatCraftsmanship}/{craft.StatControl}/{craft.StatCP}/{(craft.UnlockedManipulation ? 1 : 0)}/{(craft.Specialist ? 1 : 0)}/{craft.InitialQuality}";

    private static string BuildStandardSolverConfigKey()
    {
        var config = GatherBuddy.Config.StandardSolverConfig;
        return $"{(config.UseTricksGood ? 1 : 0)}/{(config.UseTricksExcellent ? 1 : 0)}/{config.MaxPercentage}/{(config.UseQualityStarter ? 1 : 0)}/{config.SolverCollectibleMode}/{config.MaxIQPrepTouch}/{(config.UseSpecialist ? 1 : 0)}/{(config.UseMaterialMiracle ? 1 : 0)}/{config.MinimumStepsBeforeMiracle}/{(config.MaterialMiracleMulti ? 1 : 0)}";
    }

    private static int BuildMacroFingerprint(UserMacro macro)
    {
        var hash = new HashCode();
        hash.Add(macro.Id);
        foreach (var action in macro.Actions)
        {
            hash.Add(action);
        }

        return hash.ToHashCode();
    }


    public static string FormatDuration(long ms)
    {
        if (ms < 0)
        {
            ms = 0;
        }

        var totalSeconds = (int)((ms + 500) / 1000);
        var hours = totalSeconds / 3600;
        var minutes = totalSeconds % 3600 / 60;
        var seconds = totalSeconds % 60;
        if (hours > 0)
        {
            return $"{hours}h {minutes}m {seconds}s";
        }

        if (minutes > 0)
        {
            return $"{minutes}m {seconds}s";
        }

        return $"{seconds}s";
    }
}
