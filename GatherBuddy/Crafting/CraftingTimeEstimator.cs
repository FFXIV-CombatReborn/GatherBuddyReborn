using System.Collections.Generic;
using Lumina.Excel.Sheets;

namespace GatherBuddy.Crafting;

public static class CraftingTimeEstimator
{
    private const int ActionExecutionMs = 3000;
    private const int QuickSynthCraftMs = 10_000;
    private const int CraftOverheadMs   = 5_000;
    private const int DefaultActionCount = 25;

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

        var actions = ResolveActionCount(item);
        return (long)actions * actionDelayMs + CraftOverheadMs;
    }

    private static bool IsLikelyQuickSynth(CraftingListItem item, Recipe recipe)
        => item.Options.NQOnly && recipe.CanQuickSynth;

    private static int ResolveActionCount(CraftingListItem item)
    {
        var macroId = item.CraftSettings?.SelectedMacroId;
        if (!string.IsNullOrEmpty(macroId))
        {
            var macro = CraftingGameInterop.UserMacroLibrary.GetMacroByStringId(macroId);
            if (macro != null && macro.Actions.Count > 0)
                return macro.Actions.Count;
        }

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
