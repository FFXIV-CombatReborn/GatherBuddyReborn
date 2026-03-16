using System;
using System.Collections.Generic;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using GatherBuddy.Crafting;

namespace GatherBuddy.Gui;

public class CraftingStatusWindow : Window
{
    private CraftingQueueProcessor? _queueProcessor;
    private bool _wasFocusedLastFrame = false;

    public CraftingStatusWindow() 
        : base("Crafting Status###GatherBuddyCraftingStatus", 
               ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse)
    {
        IsOpen = false;
        ShowCloseButton = true;
        RespectCloseHotkey = true;
        SizeCondition = ImGuiCond.Appearing;
    }

    public void SetQueueProcessor(CraftingQueueProcessor? processor)
    {
        _queueProcessor = processor;
        IsOpen = processor != null;
    }

    public override bool DrawConditions()
    {
        return _queueProcessor != null && IsOpen;
    }

    public override void Draw()
    {
        if (_queueProcessor == null)
            return;
        
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

        if (currentItem != null)
        {
            var recipeSheet = Dalamud.GameData.GetExcelSheet<Lumina.Excel.Sheets.Recipe>();
            if (recipeSheet != null && recipeSheet.TryGetRow(currentItem.RecipeId, out var recipe))
            {
                var itemName = recipe.ItemResult.Value.Name.ExtractText();
                ImGui.Text($"Current Recipe: {itemName}");
            }
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
            ImGui.TextDisabled("Queue is processing...");
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
                _queueProcessor.Stop();
                IsOpen = false;
                _queueProcessor = null;
            }
            
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            var delay = GatherBuddy.Config.VulcanExecutionDelayMs;
            ImGui.SetNextItemWidth(150);
            if (ImGui.SliderInt("Action Delay (ms)", ref delay, 0, 1000))
            {
                GatherBuddy.Config.VulcanExecutionDelayMs = Math.Clamp(delay, 0, 1000);
                GatherBuddy.Config.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Delay between each crafting action");

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

    private static string GetStateDisplayName(CraftingQueueProcessor.QueueState state)
    {
        return state switch
        {
            CraftingQueueProcessor.QueueState.Idle => "Idle",
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
            return (0, 0);
        
        var currentItem = _queueProcessor.CurrentRecipeItem;
        if (currentItem == null)
            return (0, 0);
        
        var currentIndex = _queueProcessor.CurrentQueueIndex;
        var currentRecipeId = currentItem.RecipeId;
        
        int firstIndex = currentIndex;
        while (firstIndex > 0)
        {
            var prevItem = GetQueueItemAt(firstIndex - 1);
            if (prevItem == null || prevItem.RecipeId != currentRecipeId)
                break;
            firstIndex--;
        }
        
        int lastIndex = currentIndex;
        while (lastIndex < _queueProcessor.QueueCount - 1)
        {
            var nextItem = GetQueueItemAt(lastIndex + 1);
            if (nextItem == null || nextItem.RecipeId != currentRecipeId)
                break;
            lastIndex++;
        }
        
        int currentCraftNumber = currentIndex - firstIndex + 1;
        int totalCrafts = lastIndex - firstIndex + 1;
        
        return (currentCraftNumber, totalCrafts);
    }
    
    private CraftingListItem? GetQueueItemAt(int index)
    {
        if (_queueProcessor == null || index < 0 || index >= _queueProcessor.QueueCount)
            return null;
        
        try
        {
            var queueField = typeof(CraftingQueueProcessor).GetField("_queue", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (queueField?.GetValue(_queueProcessor) is List<CraftingListItem> queue)
            {
                return queue[index];
            }
        }
        catch { }
        
        return null;
    }
}
