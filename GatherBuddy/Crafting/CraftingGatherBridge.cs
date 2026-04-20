using System;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;
using GatherBuddy.AutoGather.Lists;
using Lumina.Excel.Sheets;
using GatherBuddy.Plugin;

namespace GatherBuddy.Crafting;

public static class CraftingGatherBridge
{
    private static AutoGatherList? _gatherList;
    private static global::GatherBuddy.GatherBuddy? _plugin;
    private static uint _recipeIdToCraft = 0;
    private static bool _waitingForGatherComplete = false;
    private static DateTime _jobSwitchTime = DateTime.MinValue;
    private static bool _waitingForJobSwitch = false;
    private static CraftingQueueProcessor? _queueProcessor = null;
    private static CraftingExecutionPlan? _activeExecutionPlan = null;
    private static bool _isQueueMode = false;
    private static List<AutoGatherList> _disabledGatherLists = new();
    private static int? _ephemeralListId = null;
    
    public static bool PreserveListOnDisable { get; set; } = false;

    public static void Initialize(global::GatherBuddy.GatherBuddy plugin)
    {
        _plugin = plugin;
    }
    
    public static uint RecipeToCraft => _recipeIdToCraft;
    public static bool WaitingForGatherComplete => _waitingForGatherComplete;
    
    public static AutoGatherList? GetTemporaryGatherList() => _gatherList;
    public static CraftingExecutionPlan? GetActiveExecutionPlan()
        => _activeExecutionPlan;

    public static CraftingExecutionPlan? GetActiveExecutionPlan(int listId)
        => _activeExecutionPlan != null && _activeExecutionPlan.MatchesList(listId)
            ? _activeExecutionPlan
            : null;
    
    public static void DeleteTemporaryGatherList()
    {
        if (_gatherList != null && _plugin != null)
        {
            try
            {
                _plugin.AutoGatherListsManager.DeleteList(_gatherList);
                GatherBuddy.Log.Debug($"[CraftingGatherBridge] Deleted temporary gather list: {_gatherList.Name}");
                _gatherList = null;
            }
            catch (Exception ex)
            {
                GatherBuddy.Log.Warning($"[CraftingGatherBridge] Failed to delete temporary gather list: {ex.Message}");
            }
        }
    }
    
    public static void CreatePersistentGatherList(string listName, Dictionary<uint, int> materials)
    {
        if (_plugin == null)
        {
            GatherBuddy.Log.Warning("[CraftingGatherBridge] Cannot create gather list: plugin not initialized");
            return;
        }
        
        try
        {
            var gatherList = new AutoGatherList()
            {
                Name = listName,
                Enabled = false
            };

            foreach (var (itemId, quantity) in materials)
            {
                var gatherItemId = itemId;
                var gatherQuantity = quantity;

                if (AutoGather.Helpers.Diadem.ApprovedToRawItemIds.TryGetValue(itemId, out var rawItemId))
                {
                    gatherItemId = rawItemId;
                    GatherBuddy.Log.Debug($"[CraftingGatherBridge] Converted approved item {itemId} to raw item {rawItemId}");
                }

                if (GatherBuddy.GameData.Gatherables.TryGetValue(gatherItemId, out var gatherable))
                    gatherList.Add(gatherable, (uint)gatherQuantity);
                else if (GatherBuddy.GameData.Fishes.TryGetValue(gatherItemId, out var fish))
                    gatherList.Add(fish, (uint)gatherQuantity);
                else
                    GatherBuddy.Log.Debug($"[CraftingGatherBridge] Item {gatherItemId} not found in gatherables or fish, skipping");
            }

            if (gatherList.Items.Count > 0)
            {
                _plugin.AutoGatherListsManager.AddList(gatherList);
                _plugin.AutoGatherListsManager.SetActiveItems();
                GatherBuddy.Log.Information($"[CraftingGatherBridge] Created gather list '{listName}' with {gatherList.Items.Count} items.");
            }
            else
            {
                GatherBuddy.Log.Warning($"[CraftingGatherBridge] No gatherable items found for list '{listName}'.");
            }
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[CraftingGatherBridge] Failed to create gather list '{listName}': {ex.Message}");
        }
    }
    
    public static void Update()
    {
        if (_isQueueMode && _queueProcessor != null)
        {
            _queueProcessor.Update();
            
            if (_queueProcessor.CurrentState == CraftingQueueProcessor.QueueState.Complete && !_queueProcessor.HasPendingTasks())
            {
                GatherBuddy.Log.Information("[CraftingGatherBridge] All completion tasks done, cleaning up");
                RestoreDisabledGatherLists();
                GatherBuddy.CraftingStatusWindow?.SetQueueProcessor(null);
                _queueProcessor = null;
                _activeExecutionPlan = null;
                _isQueueMode = false;

                if (_ephemeralListId.HasValue)
                {
                    GatherBuddy.Log.Information($"[CraftingGatherBridge] Deleting ephemeral crafting list {_ephemeralListId.Value}");
                    GatherBuddy.CraftingListManager.DeleteList(_ephemeralListId.Value);
                    _ephemeralListId = null;
                }
            }
        }
        
        if (!_waitingForJobSwitch)
            return;
        
        var timeSinceSwitch = (DateTime.Now - _jobSwitchTime).TotalSeconds;
        if (timeSinceSwitch >= 2)
        {
            GatherBuddy.Log.Debug($"[CraftingGatherBridge] Job switch wait complete, retrying gather-to-craft");
            _waitingForJobSwitch = false;
            _jobSwitchTime = DateTime.MinValue;
            OnGatherComplete();
        }
    }
    
    public static void OnCraftFinished(Recipe? recipe, bool cancelled)
    {
        if (_isQueueMode && _queueProcessor != null)
        {
            _queueProcessor.OnCraftFinished(recipe, cancelled);
        }
    }

    public static void StartGatherAndCraft(uint recipeId, Dictionary<uint, int> missing)
    {
        _isQueueMode = false;
        _recipeIdToCraft = recipeId;
        _waitingForGatherComplete = true;
        CreateGatherListForMissingIngredients(missing);
    }
    
    public static void StartQueueCraftAndGather(CraftingExecutionPlan executionPlan, CraftingListConsumableSettings? listConsumables = null, int? ephemeralListId = null)
    {
        _isQueueMode = true;
        _ephemeralListId = ephemeralListId;
        _activeExecutionPlan = executionPlan;
        _queueProcessor = new CraftingQueueProcessor();
        _queueProcessor.QueueCompleted += OnQueueCompleted;
        _waitingForGatherComplete = true;
        GatherBuddy.Log.Information($"[CraftingGatherBridge] Starting queue automation with {executionPlan.QueueView.Count} recipes, retainerRestock={executionPlan.RetainerRestock}");
        _queueProcessor.StartQueue(executionPlan, listConsumables, GatherBuddy.RaphaelSolveCoordinator);
        var hasRetainerWork = executionPlan.RetainerRestock && AllaganTools.Enabled
            && (executionPlan.Materials.Count > 0 || executionPlan.RetainerConsumedCraftables.Count > 0);
        if (!hasRetainerWork)
            CreateGatherListForMissingIngredients(executionPlan.Materials);

        GatherBuddy.CraftingStatusWindow?.SetQueueProcessor(_queueProcessor);
    }
    
    public static void CreateGatherListForMissingIngredients(Dictionary<uint, int> missing)
    {
        try
        {
            if (_plugin != null)
            {
                var enabledLists = _plugin.AutoGatherListsManager.Lists.Where(l => l.Enabled && !l.Fallback).ToList();
                if (enabledLists.Count > 0)
                {
                    _disabledGatherLists.Clear();
                    foreach (var existingList in enabledLists)
                    {
                        existingList.Enabled = false;
                        _disabledGatherLists.Add(existingList);
                        GatherBuddy.Log.Debug($"[CraftingGatherBridge] Disabled gather list '{existingList.Name}' before starting craft gather");
                    }
                    _plugin.AutoGatherListsManager.Save();
                }
            }

            _gatherList = new AutoGatherList()
            {
                Name = "Crafting Materials (Auto-Generated)",
                Enabled = true
            };

            foreach (var (itemId, quantity) in missing)
            {
                var gatherItemId = itemId;
                var gatherQuantity = quantity;
                
                if (AutoGather.Helpers.Diadem.ApprovedToRawItemIds.TryGetValue(itemId, out var rawItemId))
                {
                    gatherItemId = rawItemId;
                    GatherBuddy.Log.Debug($"[CraftingGatherBridge] Converted approved item {itemId} to raw item {rawItemId}, quantity unchanged: {gatherQuantity}");
                }
                
                if (GatherBuddy.GameData.Gatherables.TryGetValue(gatherItemId, out var gatherable))
                    _gatherList.Add(gatherable, (uint)gatherQuantity);
                else if (GatherBuddy.GameData.Fishes.TryGetValue(gatherItemId, out var fish))
                    _gatherList.Add(fish, (uint)gatherQuantity);
                else
                    GatherBuddy.Log.Debug($"[CraftingGatherBridge] Item {gatherItemId} not found in gatherables or fish, skipping");
            }

            if (_gatherList.Items.Count > 0 && _plugin != null)
            {
                _plugin.AutoGatherListsManager.AddList(_gatherList);
                _plugin.AutoGatherListsManager.SetActiveItems();

                if (IsGatheringComplete())
                {
                    GatherBuddy.Log.Debug($"[CraftingGatherBridge] Gather list created but all items already in inventory, proceeding directly to crafting");
                    OnGatherComplete();
                }
                else
                {
                    _waitingForGatherComplete = true;
                    GatherBuddy.AutoGather.Enabled = true;
                    GatherBuddy.Log.Information($"Created crafting gather list with {_gatherList.Items.Count} items. Starting auto-gather.");
                }
            }
            else
            {
                GatherBuddy.Log.Debug($"[CraftingGatherBridge] No gatherable items needed, proceeding directly to crafting");
                OnGatherComplete();
            }
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"Failed to create gather list: {ex.Message}");
        }
    }
    
    public static void OnGatherComplete()
    {
        if (_isQueueMode && _queueProcessor != null)
        {
            _waitingForGatherComplete = false;
            GatherBuddy.Log.Debug($"[CraftingGatherBridge] Gather complete for queue mode");
            _queueProcessor.OnGatherComplete();
            return;
        }
        
        if (_recipeIdToCraft == 0)
            return;
        
        var recipeSheet = Dalamud.GameData.GetExcelSheet<Recipe>();
        if (recipeSheet == null || !recipeSheet.TryGetRow(_recipeIdToCraft, out var recipe))
        {
            GatherBuddy.Log.Error($"Could not find recipe {_recipeIdToCraft}");
            _recipeIdToCraft = 0;
            _waitingForGatherComplete = false;
            return;
        }
        
        var requiredCraftJob = (uint)(recipe.CraftType.RowId + 8);
        var currentJob = Dalamud.Objects.LocalPlayer?.ClassJob.RowId ?? 0;
        
        if (currentJob != requiredCraftJob)
        {
            if (!_waitingForJobSwitch)
            {
                GatherBuddy.Log.Information($"Switching from job {currentJob} to job {requiredCraftJob} for crafting");
                SwitchJob(requiredCraftJob);
                _jobSwitchTime = DateTime.Now;
                _waitingForJobSwitch = true;
            }
            return;
        }
        
        _waitingForGatherComplete = false;
        _waitingForJobSwitch = false;
        GatherBuddy.Log.Information($"Gathering complete. Starting craft for recipe {_recipeIdToCraft}");
        
        DeleteTemporaryGatherList();
        
        CraftingGameInterop.StartCraft(recipe, 1);
        _recipeIdToCraft = 0;
    }
    
    private static unsafe void SwitchJob(uint jobId)
    {
        try
        {
            var gearsetModule = FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureGearsetModule.Instance();
            if (gearsetModule == null)
            {
                GatherBuddy.Log.Error("Failed to get gearset module");
                return;
            }
            
            for (int i = 0; i < 100; i++)
            {
                if (gearsetModule->Entries[i].ClassJob == jobId)
                {
                    gearsetModule->EquipGearset(i);
                    GatherBuddy.Log.Information($"Equipped gearset {i} for job {jobId}");
                    return;
                }
            }
            
            GatherBuddy.Log.Warning($"No gearset found for job {jobId}");
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"Failed to switch job: {ex.Message}");
        }
    }

    public static bool IsGatheringComplete()
    {
        if (_gatherList == null)
            return _waitingForGatherComplete;

        var allComplete = true;
        foreach (var item in _gatherList.Items)
        {
            var have = GetInventoryCount(item.ItemId);
            var needed = _gatherList.Quantities.TryGetValue(item, out var qty) ? qty : 0;
            if (have < needed)
            {
                allComplete = false;
                break;
            }
        }

        return allComplete;
    }

    private static unsafe int GetInventoryCount(uint itemId)
    {
        try
        {
            var inventory = InventoryManager.Instance();
            if (inventory == null)
                return 0;
            return inventory->GetInventoryItemCount(itemId, false, false, false);
        }
        catch
        {
            return 0;
        }
    }
    
    public static void TestRepairSystem()
    {
        if (_queueProcessor != null && _isQueueMode)
        {
            GatherBuddy.Log.Warning("[CraftingGatherBridge] Cannot test repair - queue is already running");
            return;
        }
        
        GatherBuddy.Log.Information("[CraftingGatherBridge] Starting repair system test");
        _isQueueMode = true;
        _queueProcessor = new CraftingQueueProcessor();
        _queueProcessor.TestRepair();
        
        GatherBuddy.CraftingStatusWindow?.SetQueueProcessor(_queueProcessor);
    }
    
    private static void RestoreDisabledGatherLists()
    {
        if (_disabledGatherLists.Count == 0 || _plugin == null)
            return;

        foreach (var list in _disabledGatherLists)
        {
            list.Enabled = true;
            GatherBuddy.Log.Debug($"[CraftingGatherBridge] Re-enabled gather list '{list.Name}'");
        }
        _plugin.AutoGatherListsManager.SetActiveItems();
        _plugin.AutoGatherListsManager.Save();
        _disabledGatherLists.Clear();
    }

    private static void OnQueueCompleted()
    {
        GatherBuddy.Log.Information("[CraftingGatherBridge] Queue completed, will clean up after tasks finish");
    }
    
    public static void StopQueue()
    {
        if (_queueProcessor != null)
        {
            GatherBuddy.Log.Information("[CraftingGatherBridge] Stopping queue processor");
            _ephemeralListId = null;
            GatherBuddy.AutoGather.Enabled = false;
            DeleteTemporaryGatherList();
            _queueProcessor.Reset();
            _queueProcessor = null;
            _activeExecutionPlan = null;
            _isQueueMode = false;
            RestoreDisabledGatherLists();
            GatherBuddy.CraftingStatusWindow?.SetQueueProcessor(null);
        }
        else
        {
            GatherBuddy.Log.Information("[CraftingGatherBridge] No queue processor running");
        }
    }
}
