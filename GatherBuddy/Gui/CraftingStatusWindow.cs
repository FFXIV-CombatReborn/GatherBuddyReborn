using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using GatherBuddy.Crafting;
using RebornMaterial;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace GatherBuddy.Gui;

public class CraftingStatusWindow : M3Window
{
    private CraftingQueueProcessor? _queueProcessor;
    private bool? _pendingCollapseState = null;
    private bool _requestFocus = false;
    private IReadOnlyList<CraftingListItem>? _cachedEstimateQueue = null;
    private CraftingListConsumableSettings? _cachedEstimateConsumables = null;
    private long[] _cachedRemainingEstimatesByIndex = [];
    private int _cachedEstimateQueueCount = -1;
    private int _cachedEstimateActionDelayMs = -1;

    public CraftingStatusWindow()
        : base("Crafting Status###GatherBuddyCraftingStatus", ImGuiWindowFlags.AlwaysAutoResize)
    {
        IsOpen = false;
        ShowCloseButton = true;
        RespectCloseHotkey = true;
        SizeCondition = ImGuiCond.Appearing;
    }

    public void SetQueueProcessor(CraftingQueueProcessor? processor)
    {
        _queueProcessor = processor;
        ResetRemainingEstimateCache();
        if (processor == null)
        {
            IsOpen = false;
            _pendingCollapseState = null;
            _requestFocus = false;
            return;
        }

        OpenOrRestore();
    }

    public bool HasActiveQueue
        => _queueProcessor != null;

    public void OpenOrRestore()
    {
        if (_queueProcessor == null)
        {
            return;
        }

        IsOpen = true;
        _pendingCollapseState = false;
        _requestFocus = true;
    }

    public override bool DrawConditions()
    {
        return _queueProcessor != null && IsOpen;
    }

    protected override void PreDrawThemed()
    {
        if (!IsOpen)
        {
            return;
        }

        if (_pendingCollapseState.HasValue)
        {
            ImGui.SetNextWindowCollapsed(_pendingCollapseState.Value, ImGuiCond.Always);
            _pendingCollapseState = null;
        }

        if (_requestFocus)
        {
            ImGui.SetNextWindowFocus();
            _requestFocus = false;
        }
    }

    public override void Draw()
    {
        if (_queueProcessor == null)
        {
            return;
        }

        ReportFocus();

        var currentState = _queueProcessor.CurrentState;
        var currentIndex = _queueProcessor.CurrentQueueIndex;
        var totalCount = _queueProcessor.QueueCount;
        var currentItem = _queueProcessor.CurrentRecipeItem;
        var complete = currentState == CraftingQueueProcessor.QueueState.Complete;
        var paused = _queueProcessor.Paused;

        var scale = M3.Scale;
        var width = 300f * scale;
        using var wrap = ImRaii.TextWrapPos(ImGui.GetCursorPosX() + width);

        using (ImRaii.PushFont(M3.TitleMedium))
        {
            ImGui.TextUnformatted("Crafting queue");
        }

        var (stateLabel, stateTone, stateIcon) = DescribeState(currentState, paused);
        ImGui.SameLine(width - M3Widgets.PillSize(stateLabel, stateIcon).X + ImGui.GetStyle().WindowPadding.X);
        M3Widgets.Pill("##craft_state", stateLabel, stateTone, stateIcon);

        ImGui.Dummy(new Vector2(0f, M3.Space1));
        var done = Math.Min(currentIndex + 1, totalCount);
        var fraction = complete ? 1f : totalCount > 0 ? Math.Clamp((float)currentIndex / totalCount, 0f, 1f) : 0f;
        M3Widgets.LinearProgress(new Vector2(width, 6f * scale), fraction);
        M3Text.Draw(complete ? $"All {totalCount} crafts done" : $"Craft {done} of {totalCount}", M3TextStyle.Muted);

        if (paused && !string.IsNullOrWhiteSpace(_queueProcessor.PauseReason))
        {
            ImGui.Dummy(new Vector2(0f, M3.Space1));
            M3Text.Draw(_queueProcessor.PauseReason, M3TextStyle.Warning, true);
        }

        if (currentItem != null)
        {
            var recipeSheet = Dalamud.GameData.GetExcelSheet<Lumina.Excel.Sheets.Recipe>();
            if (recipeSheet != null && recipeSheet.TryGetRow(currentItem.RecipeId, out var recipe))
            {
                var itemName = recipe.ItemResult.Value.Name.ExtractText();
                ImGui.Dummy(new Vector2(0f, M3.Space1));
                M3Text.Draw("Current recipe", M3TextStyle.Label);
                M3Text.Draw(itemName, M3TextStyle.Body, true);
            }
        }

        if (ShouldShowRemainingEstimate(currentState))
        {
            var remainingMs = GetRemainingEstimate(currentIndex);
            M3Text.Draw($"About {CraftingTimeEstimator.FormatDuration(remainingMs)} left", M3TextStyle.Muted);
            M3Tooltip.Hovered("Estimate based on the active craft path for each queue item. Macros, Standard Solver, and Progress Only are simulated, "
              + "while Raphael uses cached solution length when available and falls back to a provisional default until ready. "
              + "Excludes time spent gathering, repairing, or switching jobs.");
        }
        else if (!ShouldRetainRemainingEstimateCache(currentState))
        {
            ResetRemainingEstimateCache();
        }

        ImGui.Dummy(new Vector2(width, M3.Space2));

        if (complete)
        {
            M3Text.Draw("Queue complete!", M3TextStyle.Success);
            ImGui.Dummy(new Vector2(0f, M3.Space1));
            if (M3Widgets.Button("##craft_close", "Close", M3ButtonStyle.Filled, FontAwesomeIcon.Check))
            {
                IsOpen = false;
                _queueProcessor = null;
            }

            return;
        }

        if (paused)
        {
            if (M3Widgets.Button("##craft_resume", "Resume", M3ButtonStyle.Filled, FontAwesomeIcon.Play))
            {
                _queueProcessor.Resume();
            }
        }
        else if (M3Widgets.Button("##craft_pause", "Pause", M3ButtonStyle.Tonal, FontAwesomeIcon.Pause))
        {
            _queueProcessor.Pause();
        }

        ImGui.SameLine();
        if (M3Widgets.Button("##craft_stop", "Stop", M3ButtonStyle.Danger, FontAwesomeIcon.Stop, tooltip: "Stop the crafting queue."))
        {
            CraftingGatherBridge.StopQueue();
        }

        ImGui.Dummy(new Vector2(0f, M3.Space1));
        M3Text.Draw("Action delay", M3TextStyle.Label);
        var delay = GatherBuddy.Config.VulcanExecutionDelayMs;
        var gutter = M3Widgets.SliderValueGutter("1000 ms");
        if (M3Widgets.SliderInt("##craft_action_delay", ref delay, 0, 1000, $"{delay} ms", width - gutter))
        {
            GatherBuddy.Config.VulcanExecutionDelayMs = Math.Clamp(delay, 0, 1000);
            GatherBuddy.Config.Save();
        }

        M3Tooltip.Hovered("Delay between each crafting action.");

        ImGui.Dummy(new Vector2(0f, M3.Space1));
        if (M3Widgets.Button("##craft_open_vulcan", "Open Vulcan", M3ButtonStyle.Text, FontAwesomeIcon.Hammer))
        {
            GatherBuddy.VulcanWindow?.RestoreWindow();
        }
    }

    private static (string Label, Vector4 Tone, FontAwesomeIcon Icon) DescribeState(CraftingQueueProcessor.QueueState state, bool paused)
    {
        if (paused && state != CraftingQueueProcessor.QueueState.Complete)
        {
            return ("Paused", M3.Severity(M3Severity.Warning), FontAwesomeIcon.Pause);
        }

        var label = GetStateDisplayName(state);
        return state switch
        {
            CraftingQueueProcessor.QueueState.Complete => (label, M3.Severity(M3Severity.Success), FontAwesomeIcon.CheckCircle),
            CraftingQueueProcessor.QueueState.Crafting => (label, M3.Scheme.Primary, FontAwesomeIcon.Hammer),
            CraftingQueueProcessor.QueueState.ReadyForCraft => (label, M3.Scheme.Primary, FontAwesomeIcon.Hammer),
            CraftingQueueProcessor.QueueState.Idle => (label, M3.Scheme.Outline, FontAwesomeIcon.None),
            _ => (label, M3.Severity(M3Severity.Info), FontAwesomeIcon.HourglassHalf),
        };
    }

    private void ResetRemainingEstimateCache()
    {
        _cachedEstimateQueue = null;
        _cachedEstimateConsumables = null;
        _cachedRemainingEstimatesByIndex = [];
        _cachedEstimateQueueCount = -1;
        _cachedEstimateActionDelayMs = -1;
    }

    private long GetRemainingEstimate(int currentIndex)
    {
        if (_queueProcessor == null)
        {
            return 0;
        }

        var queue = _queueProcessor.Queue;
        var consumables = _queueProcessor.ListConsumables;
        var actionDelayMs = GatherBuddy.Config.VulcanExecutionDelayMs;
        if (ReferenceEquals(_cachedEstimateQueue, queue)
         && ReferenceEquals(_cachedEstimateConsumables, consumables)
         && _cachedEstimateQueueCount == queue.Count
         && _cachedEstimateActionDelayMs == actionDelayMs)
        {
            return GetCachedRemainingEstimateAtIndex(currentIndex);
        }

        _cachedEstimateQueue = queue;
        _cachedEstimateConsumables = consumables;
        _cachedEstimateQueueCount = queue.Count;
        _cachedEstimateActionDelayMs = actionDelayMs;
        _cachedRemainingEstimatesByIndex = BuildRemainingEstimates(queue, actionDelayMs, consumables);
        return GetCachedRemainingEstimateAtIndex(currentIndex);
    }

    private long GetCachedRemainingEstimateAtIndex(int currentIndex)
    {
        if (_cachedRemainingEstimatesByIndex.Length == 0)
        {
            return 0;
        }

        var safeIndex = Math.Clamp(currentIndex, 0, _cachedRemainingEstimatesByIndex.Length - 1);
        return _cachedRemainingEstimatesByIndex[safeIndex];
    }

    private static long[] BuildRemainingEstimates(IReadOnlyList<CraftingListItem> queue, int actionDelayMs, CraftingListConsumableSettings? consumables)
    {
        if (queue.Count == 0)
        {
            return [];
        }

        var remainingByIndex = new long[queue.Count];
        long remainingMs = 0;
        for (var i = queue.Count - 1; i >= 0; i--)
        {
            remainingMs += CraftingTimeEstimator.EstimateItemMs(queue[i], actionDelayMs, consumables);
            remainingByIndex[i] = remainingMs;
        }

        return remainingByIndex;
    }

    private static bool ShouldShowRemainingEstimate(CraftingQueueProcessor.QueueState state)
    {
        return state == CraftingQueueProcessor.QueueState.ReadyForCraft
            || state == CraftingQueueProcessor.QueueState.Crafting;
    }

    private static bool ShouldRetainRemainingEstimateCache(CraftingQueueProcessor.QueueState state)
    {
        return state == CraftingQueueProcessor.QueueState.ReadyForCraft
            || state == CraftingQueueProcessor.QueueState.Crafting
            || state == CraftingQueueProcessor.QueueState.WaitingForJobSwitch
            || state == CraftingQueueProcessor.QueueState.Repairing
            || state == CraftingQueueProcessor.QueueState.ExtractingMateria;
    }

    private static string GetStateDisplayName(CraftingQueueProcessor.QueueState state)
    {
        return state switch
        {
            CraftingQueueProcessor.QueueState.Idle => "Idle",
            CraftingQueueProcessor.QueueState.NavigatingToRetainerBell => "Navigating to Retainer Bell",
            CraftingQueueProcessor.QueueState.WaitingForGather => "Gathering Materials",
            CraftingQueueProcessor.QueueState.WaitingForJobSwitch => "Switching Job",
            CraftingQueueProcessor.QueueState.Repairing => "Repairing Equipment",
            CraftingQueueProcessor.QueueState.ExtractingMateria => "Extracting Materia",
            CraftingQueueProcessor.QueueState.WaitingForRaphaelSolution => "Solving with Raphael",
            CraftingQueueProcessor.QueueState.ReadyForCraft => "Ready to Craft",
            CraftingQueueProcessor.QueueState.Crafting => "Crafting",
            CraftingQueueProcessor.QueueState.Complete => "Complete",
            _ => "Unknown"
        };
    }

    private (int currentCraft, int totalCrafts) GetCurrentRecipeCraftNumbers()
    {
        if (_queueProcessor == null)
        {
            return (0, 0);
        }

        var currentItem = _queueProcessor.CurrentRecipeItem;
        if (currentItem == null)
        {
            return (0, 0);
        }

        var currentIndex = _queueProcessor.CurrentQueueIndex;
        var currentRecipeId = currentItem.RecipeId;

        var firstIndex = currentIndex;
        while (firstIndex > 0)
        {
            var prevItem = GetQueueItemAt(firstIndex - 1);
            if (prevItem == null || prevItem.RecipeId != currentRecipeId)
            {
                break;
            }

            firstIndex--;
        }

        var lastIndex = currentIndex;
        while (lastIndex < _queueProcessor.QueueCount - 1)
        {
            var nextItem = GetQueueItemAt(lastIndex + 1);
            if (nextItem == null || nextItem.RecipeId != currentRecipeId)
            {
                break;
            }

            lastIndex++;
        }

        var currentCraftNumber = currentIndex - firstIndex + 1;
        var totalCrafts = lastIndex - firstIndex + 1;

        return (currentCraftNumber, totalCrafts);
    }

    private CraftingListItem? GetQueueItemAt(int index)
    {
        if (_queueProcessor == null || index < 0 || index >= _queueProcessor.QueueCount)
        {
            return null;
        }

        return _queueProcessor.Queue[index];
    }
}
