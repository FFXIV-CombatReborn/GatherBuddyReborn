using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using GatherBuddy.Crafting;
using System;
using System.Collections.Generic;

namespace GatherBuddy.Gui;

public class CraftingStatusWindow : Window
{
    private CraftingQueueProcessor? _queueProcessor;
    private bool? _pendingCollapseState = null;
    private bool _requestFocus = false;
    private bool _wasFocusedLastFrame = false;
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

    public override void PreDraw()
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

        var isFocused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
        if (isFocused)
        {
            GatherBuddy.ControllerSupport?.UpdateFocusedWindow("Crafting Status###GatherBuddyCraftingStatus");
            _wasFocusedLastFrame = true;
        }
        else if (_wasFocusedLastFrame)
        {
            GatherBuddy.ControllerSupport?.UpdateFocusedWindow(null);
            _wasFocusedLastFrame = false;
        }

        var currentState = _queueProcessor.CurrentState;
        var currentIndex = _queueProcessor.CurrentQueueIndex;
        var totalCount = _queueProcessor.QueueCount;
        var currentItem = _queueProcessor.CurrentRecipeItem;

        ImGui.TextColored(new System.Numerics.Vector4(0.0f, 1.0f, 0.8f, 1.0f), "Crafting Queue Active");
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.Text($"State: {GetStateDisplayName(currentState)}");
        ImGui.Text($"Progress: {Math.Min(currentIndex + 1, totalCount)} / {totalCount}");

        if (_queueProcessor.Paused && !string.IsNullOrWhiteSpace(_queueProcessor.PauseReason))
        {
            ImGui.PushTextWrapPos();
            ImGui.TextColored(new System.Numerics.Vector4(1.0f, 0.82f, 0.24f, 1.0f), _queueProcessor.PauseReason);
            ImGui.PopTextWrapPos();
        }

        if (currentItem != null)
        {
            var recipeSheet = Dalamud.GameData.GetExcelSheet<Lumina.Excel.Sheets.Recipe>();
            if (recipeSheet != null && recipeSheet.TryGetRow(currentItem.RecipeId, out var recipe))
            {
                var itemName = recipe.ItemResult.Value.Name.ExtractText();
                ImGui.Text($"Current Recipe: {itemName}");
            }
        }

        if (ShouldShowRemainingEstimate(currentState))
        {
            var remainingMs = GetRemainingEstimate(currentIndex);
            ImGui.Text($"Estimated Time Remaining: ~{CraftingTimeEstimator.FormatDuration(remainingMs)}");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Estimate based on the active craft path for each queue item. Macros, Standard Solver, and Progress Only are simulated, while Raphael uses cached solution length when available and falls back to a provisional default until ready. Excludes time spent gathering, repairing, or switching jobs.");
            }
        }
        else if (!ShouldRetainRemainingEstimateCache(currentState))
        {
            ResetRemainingEstimateCache();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (currentState == CraftingQueueProcessor.QueueState.Complete)
        {
            ImGui.TextColored(new System.Numerics.Vector4(0.0f, 1.0f, 0.0f, 1.0f), "Queue Complete!");
            ImGui.Spacing();
            if (ImGui.Button("Close"))
            {
                IsOpen = false;
                _queueProcessor = null;
            }
        }
        else
        {
            ImGui.TextDisabled(_queueProcessor.Paused ? "Queue is paused." : "Queue is processing...");
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            if (!_queueProcessor.Paused)
            {
                if (ImGui.Button("Pause"))
                {
                    _queueProcessor.Pause();
                }
            }
            else
            {
                if (ImGui.Button("Resume"))
                {
                    _queueProcessor.Resume();
                }
            }

            ImGui.SameLine();
            if (ImGui.Button("Stop"))
            {
                CraftingGatherBridge.StopQueue();
            }

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            var delay = GatherBuddy.Config.VulcanExecutionDelayMs;
            ImGui.SetNextItemWidth(VulcanUiScaling.Scaled(150f));
            if (ImGui.SliderInt("Action Delay (ms)", ref delay, 0, 1000))
            {
                GatherBuddy.Config.VulcanExecutionDelayMs = Math.Clamp(delay, 0, 1000);
                GatherBuddy.Config.Save();
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Delay between each crafting action");
            }

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            if (ImGui.Button("Open Vulcan Window"))
            {
                if (GatherBuddy.VulcanWindow != null)
                {
                    GatherBuddy.VulcanWindow.IsOpen = true;
                }
            }
        }
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
