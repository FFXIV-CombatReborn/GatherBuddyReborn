using System.Collections.Concurrent;
using System.Collections.Generic;
using Lumina.Excel.Sheets;

namespace GatherBuddy.Crafting;

public static class CraftingTimeEstimator
{
    private const int ActionExecutionMs = 3000;
    private const int QuickSynthCraftMs = 3000;
    private const int CraftOverheadMs   = 5000;
    private const int DefaultActionCount = 15;

    private static readonly ConcurrentDictionary<string, int> _actionCountCache = new();

    public static long EstimateRemainingMs(IReadOnlyList<CraftingListItem> queue, int fromIndex)
    {
        if (queue.Count == 0 || fromIndex >= queue.Count)
            return 0;

        var actionDelayMs = ActionExecutionMs + GatherBuddy.Config.VulcanExecutionDelayMs;
        long total = 0;
        for (var i = fromIndex < 0 ? 0 : fromIndex; i < queue.Count; i++)
            total += EstimateItemMs(queue[i], actionDelayMs);
        return total;
    }

    public static long EstimateItemMs(CraftingListItem item, int actionDelayMs)
    {
        var recipe = RecipeManager.GetRecipe(item.RecipeId);
        if (recipe.HasValue && IsLikelyQuickSynth(item, recipe.Value))
            return QuickSynthCraftMs;

        var actions = ResolveActionCount(item, recipe);
        return (long)actions * actionDelayMs + CraftOverheadMs;
    }

    private static bool IsLikelyQuickSynth(CraftingListItem item, Recipe recipe)
        => item.Options.NQOnly && recipe.CanQuickSynth;

    private static int ResolveActionCount(CraftingListItem item, Recipe? recipe)
    {
        var macroId = item.CraftSettings?.SelectedMacroId;
        if (!string.IsNullOrEmpty(macroId))
        {
            var macro = CraftingGameInterop.UserMacroLibrary.GetMacroByStringId(macroId);
            if (macro != null && macro.Actions.Count > 0)
                return macro.Actions.Count;
        }

        return ResolveRaphaelActionCount(item, recipe);
    }

    private static int ResolveRaphaelActionCount(CraftingListItem item, Recipe? recipe)
    {
        if (!recipe.HasValue)
            return DefaultActionCount;

        var requiredJob = recipe.Value.CraftType.RowId + 8;
        var stats = GearsetStatsReader.ReadGearsetStatsForJob(requiredJob);
        if (stats == null)
            return DefaultActionCount;

        if (item.CraftSettings != null)
            stats = GearsetStatsReader.ApplyConsumablesToStats(stats, item.CraftSettings);

        item.QualityPolicy ??= CraftingQualityPolicyResolver.Resolve(recipe.Value, item.CraftSettings);
        var initialQuality = item.QualityPolicy.CalculateGuaranteedInitialQuality(recipe.Value);

        var specialist = GatherBuddy.Config.RaphaelSolverConfig.RaphaelAllowSpecialistActions && stats.Specialist;
        var request = new RaphaelSolveRequest(
            RecipeId: item.RecipeId,
            Level: stats.Level,
            Craftsmanship: stats.Craftsmanship,
            Control: stats.Control,
            CP: stats.CP,
            Manipulation: stats.Manipulation,
            Specialist: specialist,
            InitialQuality: initialQuality);

        var key = request.GetKey();
        if (_actionCountCache.TryGetValue(key, out var cachedCount))
            return cachedCount;

        var coord = GatherBuddy.RaphaelSolveCoordinator;
        if (coord.TryGetSolution(request, out var solved) && solved != null && solved.ActionIds.Count > 0)
        {
            _actionCountCache[key] = solved.ActionIds.Count;
            coord.RemoveCachedSolution(request);
            return solved.ActionIds.Count;
        }

        if (coord.HasFailedSolution(request, out _))
            return DefaultActionCount;

        if (!coord.IsKnown(request))
            coord.EnqueueSolvesFromRequests(new[] { request });

        return DefaultActionCount;
    }

    public static string FormatDuration(long ms)
    {
        if (ms < 0)
            ms = 0;
        var totalSeconds = (int)((ms + 500) / 1000);
        var hours = totalSeconds / 3600;
        var minutes = (totalSeconds % 3600) / 60;
        var seconds = totalSeconds % 60;
        if (hours > 0)
            return $"{hours}h {minutes}m {seconds}s";
        if (minutes > 0)
            return $"{minutes}m {seconds}s";
        return $"{seconds}s";
    }
}
