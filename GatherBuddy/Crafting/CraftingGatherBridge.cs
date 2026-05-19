using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GatherBuddy.Automation;
using GatherBuddy.AutoGather.Lists;
using GatherBuddy.AutoGather.Collectables;
using GatherBuddy.Helpers;
using Lumina.Excel.Sheets;
using GatherBuddy.Plugin;
using GatherBuddy.Vulcan.Vendors;

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
    private static bool _waitingForCollectables = false;
    private static bool _collectablesStartPending = false;
    private static DateTime _nextCollectablesRetry = DateTime.MinValue;
    private static DateTime _lastCollectablesWaitLog = DateTime.MinValue;
    private static DateTime _lastCollectablesExitAttempt = DateTime.MinValue;
    private static DateTime _lastCollectablesHardFailLog = DateTime.MinValue;
    private static bool _waitingForCollectablesHomeReturn = false;
    private static bool _collectablesHomeReturnStarted = false;
    
    public static bool PreserveListOnDisable { get; set; } = false;

    public static void Initialize(global::GatherBuddy.GatherBuddy plugin)
    {
        _plugin = plugin;
    }

    private static int RoundUpToBatchSize(int quantity, int batchSize)
        => batchSize <= 1
            ? quantity
            : (int)Math.Ceiling((double)quantity / batchSize) * batchSize;

    public static void BindCollectableManager(CollectableManager manager)
    {
        manager.OnFinishCollecting -= OnCollectablesFinished;
        manager.OnError -= OnCollectablesError;
        manager.OnFinishCollecting += OnCollectablesFinished;
        manager.OnError += OnCollectablesError;
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
                var gatherQuantity = GetCraftingGatherTargetQuantity(itemId, quantity, out var gatherItemId);
                if (gatherQuantity <= 0)
                    continue;

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

    private static int GetCraftingGatherTargetQuantity(uint itemId, int quantity, out uint gatherItemId)
    {
        gatherItemId = itemId;
        if (quantity <= 0)
            return 0;

        if (!AutoGather.Helpers.Diadem.ApprovedToRawItemIds.TryGetValue(itemId, out var rawItemId))
            return quantity;
        var approvedDeficit = Math.Max(0, quantity - GetInventoryCount(itemId));
        if (approvedDeficit <= 0)
            return 0;

        gatherItemId = rawItemId;
        var batchSize = AutoGather.Helpers.Diadem.ApprovedInspectionBatchSizes.TryGetValue(itemId, out var configuredBatchSize) && configuredBatchSize > 0
            ? (int)configuredBatchSize
            : 1;
        return RoundUpToBatchSize(approvedDeficit, batchSize);
    }
    
    public static void Update()
    {
        if (_isQueueMode && _queueProcessor != null)
        {
            UpdateCollectablesHomeReturnBeforeResume();
            TryStartCollectablesInterruption();
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
        ResetCollectablesInterruptionState();
        _lastCollectablesHardFailLog = DateTime.MinValue;
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
                var gatherQuantity = GetCraftingGatherTargetQuantity(itemId, quantity, out var gatherItemId);
                if (gatherQuantity <= 0)
                    continue;
                
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

    private static void TryStartCollectablesInterruption()
    {
        if (_queueProcessor == null
         || _queueProcessor.CurrentState is CraftingQueueProcessor.QueueState.Idle or CraftingQueueProcessor.QueueState.Complete
         || GatherBuddy.CollectableManager == null
         || GatherBuddy.CollectableManager.IsRunning
         || _waitingForCollectablesHomeReturn
         || DateTime.UtcNow < _nextCollectablesRetry)
            return;

        if (_waitingForCollectables)
            return;

        var collectableConfig = GatherBuddy.Config.CollectableConfig;
        if (!collectableConfig.AutoTurnInCollectables)
        {
            if (_collectablesStartPending)
            {
                GatherBuddy.Log.Information("[CraftingGatherBridge] Collectables interruption was pending when auto turn-ins were forced off, resuming the queue without starting collectables");
                ResetCollectablesInterruptionState();
                _queueProcessor.Resume();
            }
            LogCollectablesHardFailState(collectableConfig.AutoTurnInHardFailReason);
            return;
        }

        if (!CollectableTurnInRequirements.IsAvailable)
        {
            if (_collectablesStartPending)
            {
                GatherBuddy.Log.Debug("[CraftingGatherBridge] Collectables interruption was pending when neither AllaganTools nor AllaganItemSearch was loaded, resuming the queue without starting collectables");
                ResetCollectablesInterruptionState();
                _queueProcessor.Resume();
            }
            return;
        }

        if (!_collectablesStartPending)
        {
            if (_queueProcessor.Paused)
                return;

            var thresholdState = CollectableInventoryHelper.GetThresholdState(GatherBuddy.Config.CollectableConfig);
            if (!thresholdState.ThresholdReached)
                return;
            _queueProcessor.Pause();
            _collectablesStartPending = true;
            _lastCollectablesWaitLog = DateTime.MinValue;
            _lastCollectablesExitAttempt = DateTime.MinValue;
        }

        TryExitCraftingUiForCollectables();
        if (!IsReadyToStartCollectables(out var waitReason))
        {
            LogCollectablesWaitReason(waitReason);
            return;
        }

        if (IsWaitingForCollectablesRouteData(out waitReason))
        {
            LogCollectablesWaitReason(waitReason);
            return;
        }

        if (GatherBuddy.CollectableManager.Start(CollectableRunSource.VulcanQueue, returnHomeAfterCompletion: true))
        {
            _collectablesStartPending = false;
            _waitingForCollectables = true;
            _lastCollectablesWaitLog = DateTime.MinValue;
            _lastCollectablesExitAttempt = DateTime.MinValue;
            return;
        }

        if (IsWaitingForCollectablesRouteData(out waitReason))
        {
            LogCollectablesWaitReason(waitReason);
            return;
        }

        GatherBuddy.Log.Warning($"[CraftingGatherBridge] Failed to start collectables interruption: {GatherBuddy.CollectableManager.StatusText}");
        ResetCollectablesInterruptionState();
        _nextCollectablesRetry = DateTime.UtcNow.AddSeconds(5);
        _queueProcessor.Resume();
    }

    private static void TryExitCraftingUiForCollectables()
    {
        if (CraftingGameInterop.CurrentState != CraftingGameInterop.CraftState.IdleBetween)
            return;

        if (_lastCollectablesExitAttempt != DateTime.MinValue
         && (DateTime.UtcNow - _lastCollectablesExitAttempt) < TimeSpan.FromMilliseconds(500))
            return;

        _lastCollectablesExitAttempt = DateTime.UtcNow;
        CraftingTasks.TaskExitCraft();
    }

    private static bool IsReadyToStartCollectables(out string waitReason)
    {
        if (Dalamud.Conditions[ConditionFlag.BetweenAreas] || Dalamud.Conditions[ConditionFlag.BetweenAreas51])
        {
            waitReason = "area transition is still active";
            return false;
        }

        if (Lifestream.Enabled && Lifestream.IsBusy())
        {
            waitReason = "Lifestream is still busy";
            return false;
        }

        if (!GenericHelpers.IsScreenReady())
        {
            waitReason = "the screen is not ready";
            return false;
        }

        if (Dalamud.Conditions[ConditionFlag.ExecutingCraftingAction])
        {
            waitReason = "a crafting action is still executing";
            return false;
        }

        if (Dalamud.Conditions[ConditionFlag.PreparingToCraft])
        {
            waitReason = "craft preparation is still active";
            return false;
        }

        if (Dalamud.Conditions[ConditionFlag.Crafting])
        {
            waitReason = $"crafting state is still {CraftingGameInterop.CurrentState}";
            return false;
        }

        if (CraftingGameInterop.CurrentState != CraftingGameInterop.CraftState.IdleNormal)
        {
            waitReason = $"crafting has not returned to IdleNormal yet ({CraftingGameInterop.CurrentState})";
            return false;
        }

        if (IsCraftingAddonVisible("RecipeNote") || IsCraftingAddonVisible("Synthesis") || IsCraftingAddonVisible("SynthesisSimple") || IsCraftingAddonVisible("WKSRecipeNotebook"))
        {
            waitReason = "crafting windows are still visible";
            return false;
        }

        waitReason = string.Empty;
        return true;
    }

    private static unsafe bool IsCraftingAddonVisible(string addonName)
    {
        var addon = (AtkUnitBase*)(nint)Dalamud.GameGui.GetAddonByName(addonName);
        return addon != null && addon->IsVisible;
    }

    private static bool IsWaitingForCollectablesRouteData(out string waitReason)
    {
        if (!CollectableTurnInRouteResolver.HasLookupData)
        {
            waitReason = string.Empty;
            return false;
        }

        var collectableNpcIds = CollectableTurnInRouteResolver.GetCollectableNpcIds();
        if (collectableNpcIds.Count == 0)
        {
            waitReason = string.Empty;
            return false;
        }

        VendorNpcLocationCache.InitializeAsync(collectableNpcIds);
        if (VendorNpcLocationCache.IsInitialized)
        {
            waitReason = string.Empty;
            return false;
        }

        waitReason = VendorNpcLocationCache.IsInitializing
            ? $"collectables route locations are still loading ({VendorNpcLocationCache.ResolvedNpcCount}/{VendorNpcLocationCache.RequestedNpcCount} NPCs resolved)"
            : "collectables route locations are still loading";
        return true;
    }

    private static void LogCollectablesWaitReason(string waitReason)
    {
        if (_lastCollectablesWaitLog != DateTime.MinValue && (DateTime.UtcNow - _lastCollectablesWaitLog) < TimeSpan.FromSeconds(10))
            return;

        GatherBuddy.Log.Debug($"[CraftingGatherBridge] Waiting to start collectables interruption: {waitReason}");
        _lastCollectablesWaitLog = DateTime.UtcNow;
    }

    private static void OnCollectablesFinished()
    {
        if (!_waitingForCollectables && !_collectablesStartPending)
            return;
        ResetCollectablesInterruptionState();
        _queueProcessor?.Resume();
    }

    private static void OnCollectablesError(string error)
    {
        if (!_waitingForCollectables && !_collectablesStartPending)
            return;

        GatherBuddy.Log.Error($"[CraftingGatherBridge] Collectables interruption failed: {error}");
        var hardFailReason = GatherBuddy.Config.CollectableConfig.AutoTurnInHardFailReason;
        if (!GatherBuddy.Config.CollectableConfig.AutoTurnInCollectables && !string.IsNullOrWhiteSpace(hardFailReason))
        {
            LogCollectablesHardFailState(hardFailReason);
            StartCollectablesHomeReturnBeforeResume(hardFailReason);
            return;
        }

        ResetCollectablesInterruptionState();
        _nextCollectablesRetry = DateTime.UtcNow.AddSeconds(5);
        _lastCollectablesWaitLog = DateTime.MinValue;
        _lastCollectablesExitAttempt = DateTime.MinValue;
        _lastCollectablesHardFailLog = DateTime.MinValue;
        _queueProcessor?.Resume();
    }

    private static void StartCollectablesHomeReturnBeforeResume(string hardFailReason)
    {
        _collectablesStartPending = false;
        _waitingForCollectables = false;
        _waitingForCollectablesHomeReturn = true;
        _collectablesHomeReturnStarted = false;
        _nextCollectablesRetry = DateTime.MinValue;
        _lastCollectablesWaitLog = DateTime.MinValue;
        _lastCollectablesExitAttempt = DateTime.MinValue;
        GatherBuddy.Log.Warning("[CraftingGatherBridge] Returning home before resuming the queue after collectables hard fail");
    }

    private static void UpdateCollectablesHomeReturnBeforeResume()
    {
        if (!_waitingForCollectablesHomeReturn)
            return;

        if (!_collectablesHomeReturnStarted)
        {
            if (Lifestream.Enabled && Lifestream.IsBusy())
                return;

            if (!HomeNavigationHelper.TryStartReturnHome(out var error))
            {
                if (string.IsNullOrWhiteSpace(error))
                    return;

                GatherBuddy.Log.Warning($"[CraftingGatherBridge] {error}");
                GatherBuddy.Log.Warning("[CraftingGatherBridge] Resuming the queue without a home return after collectables hard fail");
                ResetCollectablesInterruptionState();
                _queueProcessor?.Resume();
                return;
            }

            _collectablesHomeReturnStarted = true;
            return;
        }

        if (!HomeNavigationHelper.IsReturnComplete())
            return;

        GatherBuddy.Log.Information("[CraftingGatherBridge] Home return complete, resuming the queue after collectables hard fail");
        ResetCollectablesInterruptionState();
        _queueProcessor?.Resume();
    }
    
    public static void StopQueue()
    {
        if (_queueProcessor != null)
        {
            GatherBuddy.Log.Information("[CraftingGatherBridge] Stopping queue processor");
            ResetCollectablesInterruptionState();
            _lastCollectablesHardFailLog = DateTime.MinValue;
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

    private static void LogCollectablesHardFailState(string hardFailReason)
    {
        if (string.IsNullOrWhiteSpace(hardFailReason))
            return;

        if (_lastCollectablesHardFailLog != DateTime.MinValue && (DateTime.UtcNow - _lastCollectablesHardFailLog) < TimeSpan.FromSeconds(30))
            return;

        GatherBuddy.Log.Warning($"[CraftingGatherBridge] Skipping collectables interruption because auto turn-ins were forced off: {hardFailReason}");
        _lastCollectablesHardFailLog = DateTime.UtcNow;
    }

    private static void ResetCollectablesInterruptionState()
    {
        _collectablesStartPending = false;
        _waitingForCollectables = false;
        _waitingForCollectablesHomeReturn = false;
        _collectablesHomeReturnStarted = false;
        _nextCollectablesRetry = DateTime.MinValue;
        _lastCollectablesWaitLog = DateTime.MinValue;
        _lastCollectablesExitAttempt = DateTime.MinValue;
    }
}
