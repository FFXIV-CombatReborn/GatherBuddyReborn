using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using GatherBuddy.Automation;
using GatherBuddy.Plugin;
using GatherBuddy.Vulcan;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace GatherBuddy.Crafting;

public class CraftingQueueProcessor
{
    public enum QueueState
    {
        Idle,
        NavigatingToRetainerBell,
        WithdrawingFromRetainer,
        WaitingForGather,
        WaitingForJobSwitch,
        Repairing,
        ExtractingMateria,
        WaitingForRaphaelSolution,
        ReadyForCraft,
        Crafting,
        Complete
    }

    private QueueState _currentState = QueueState.Idle;
    private List<CraftingListItem> _queue = new();
    private int _currentQueueIndex = 0;
    private List<Func<CraftingTasks.TaskResult>> _tasks = new();
    private RaphaelSolveCoordinator? _raphaelCoordinator = null;
    private CraftingListConsumableSettings? _listConsumables = null;
    private DateTime _consumableDelayUntil = DateTime.MinValue;
    private bool _retainerRestock = false;
    private Dictionary<uint, int> _allMaterials = new();
    private Dictionary<uint, int> _retainerPrecraftItems = new();
    private Dictionary<uint, int> _retainerSkipAmounts = new();
    private Dictionary<uint, (int TargetHQ, int TargetNQ, bool IsExplicit)> _precraftQualityTargets = new();
    private List<CraftingListItem> _expandedQueueForRetainer = new();
    private CraftingListDefinition? _retainerPlanningList = null;
    private RetainerTaskExecutor? _retainerExecutor = null;
    private RetainerBellNavigator? _retainerBellNavigator = null;

    private bool _paused = false;
    private bool _pausedDuringGather = false;
    private uint _currentProcessedRecipeId = 0;
    private int _currentProcessedRecipeCount = 0;
    private int _currentProcessedRecipeTotal = 0;
    private DateTime _craftHangSince = DateTime.MinValue;
    private bool _lastCraftWasQuickSynth = false;
    private Dictionary<uint, RaphaelSolveRequest> _enqueuedRaphaelRequests = new();
    private uint _jobSwitchRequestedFor = 0u;

    public QueueState CurrentState => _currentState;
    public int CurrentQueueIndex => _currentQueueIndex;
    public int QueueCount => _queue.Count;
    public CraftingListItem? CurrentRecipeItem => _currentQueueIndex < _queue.Count ? _queue[_currentQueueIndex] : null;
    public bool Paused => _paused;
    public uint CurrentProcessedRecipeId => _currentProcessedRecipeId;
    public int CurrentProcessedRecipeCount => _currentProcessedRecipeCount;
    public int CurrentProcessedRecipeTotal => _currentProcessedRecipeTotal;
    public bool HasPendingTasks() => _tasks.Count > 0;

    public delegate void StateChangedHandler(QueueState state);
    public delegate void QueueCompletedHandler();
    
    public event StateChangedHandler? StateChanged;
    public event QueueCompletedHandler? QueueCompleted;
    
    public CraftingQueueProcessor()
    {
        CraftingGameInterop.CraftFinished += OnCraftFinished;
        CraftingGameInterop.QuickSynthProgress += OnQuickSynthProgress;
    }

    public void StartQueue(List<CraftingListItem> queue, CraftingListConsumableSettings? listConsumables = null, RaphaelSolveCoordinator? raphaelCoordinator = null, bool skipIfEnough = false, bool retainerRestock = false, Dictionary<uint, int>? materials = null, Dictionary<uint, int>? retainerPrecraftItems = null, CraftingListDefinition? retainerPlanningList = null)
    {
        YesAlready.Lock();
        _queue = new List<CraftingListItem>(queue);
        _currentQueueIndex = 0;
        _raphaelCoordinator = raphaelCoordinator;
        _listConsumables = listConsumables;
        _consumableDelayUntil = DateTime.MinValue;
        _enqueuedRaphaelRequests.Clear();
        _jobSwitchRequestedFor = 0u;
        _retainerRestock = retainerRestock;
        _allMaterials = materials ?? new();
        _retainerPrecraftItems = retainerPrecraftItems ?? new();
        _retainerSkipAmounts = new();
        _precraftQualityTargets = new();
        _expandedQueueForRetainer = new List<CraftingListItem>(queue);
        _retainerPlanningList = retainerPlanningList;
        _retainerExecutor = null;
        _retainerBellNavigator = null;

        if (retainerRestock && AllaganTools.Enabled && materials != null && materials.Count > 0)
        {
            GatherBuddy.Log.Information("[CraftingQueueProcessor] Retainer restock enabled");
            if (GatherBuddy.Config.VulcanRetainerBellConfig.AutoNavigateToRetainerBell)
            {
                GatherBuddy.Log.Debug("[CraftingQueueProcessor] Auto-navigation to retainer bell enabled");
                _currentState = QueueState.NavigatingToRetainerBell;
                QueueRetainerBellNavigationTasks();
            }
            else
            {
                GatherBuddy.Log.Debug("[CraftingQueueProcessor] Proceeding directly to retainer withdrawal");
                _currentState = QueueState.WithdrawingFromRetainer;
                QueueRetainerWithdrawalTasks();
            }
        }
        else
        {
            _currentState = QueueState.WaitingForGather;
        }
        GatherBuddy.Log.Information($"[CraftingQueueProcessor] Starting queue with {_queue.Count} recipes");
        StateChanged?.Invoke(_currentState);
        
        var solverMode = GatherBuddy.Config.RaphaelSolverConfig.SolverMode;
        if (_raphaelCoordinator != null && solverMode == RaphaelSolverMode.PureRaphael)
        {
            GatherBuddy.Log.Debug($"[CraftingQueueProcessor] Building CraftStates to extract accurate stats for Raphael");
            EnqueueRaphaelSolvesFromCraftStates(_queue);
        }
        else if (solverMode == RaphaelSolverMode.StandardSolver)
        {
            var raphaelOverrideItems = _queue.Where(r => r.CraftSettings?.SolverOverride == SolverOverrideMode.RaphaelSolver).ToList();
            if (raphaelOverrideItems.Count > 0 && _raphaelCoordinator != null)
            {
                GatherBuddy.Log.Debug($"[CraftingQueueProcessor] Enqueuing Raphael solves for {raphaelOverrideItems.Count} override item(s)");
                EnqueueRaphaelSolvesFromCraftStates(raphaelOverrideItems);
            }
        }
    }

    public void OnGatherComplete()
    {
        if (_currentState != QueueState.WaitingForGather)
            return;

        GatherBuddy.Log.Debug($"[CraftingQueueProcessor] Gather complete, moving to job check");
        YesAlready.Lock();
        CraftingGatherBridge.DeleteTemporaryGatherList();
        
        _currentState = QueueState.WaitingForJobSwitch;
        StateChanged?.Invoke(_currentState);
    }

    public void Update()
    {
        if (_paused)
        {
            return;
        }

        ProcessTasks();
        
        switch (_currentState)
        {
            case QueueState.Idle:
                break;
            case QueueState.NavigatingToRetainerBell:
                break;
            case QueueState.WithdrawingFromRetainer:
                break;
            case QueueState.WaitingForGather:
                break;
            case QueueState.WaitingForJobSwitch:
                UpdateJobSwitch();
                break;
            case QueueState.Repairing:
                break;
            case QueueState.ExtractingMateria:
                break;
            case QueueState.WaitingForRaphaelSolution:
                CheckRaphaelSolutionReady();
                break;
            case QueueState.ReadyForCraft:
                StartNextCraft();
                break;
            case QueueState.Crafting:
                if (CraftingGameInterop.CurrentState == CraftingGameInterop.CraftState.QuickSynthesis &&
                    (NeedsRepair() || NeedsMateria()))
                {
                    GatherBuddy.Log.Information("[CraftingQueueProcessor] Interrupting quick synth for repair/materia");
                    CloseQuickSynthWindow();
                }
                else if (CraftingGameInterop.CurrentState == CraftingGameInterop.CraftState.IdleNormal)
                {
                    if (_craftHangSince == DateTime.MinValue)
                    {
                        GatherBuddy.Log.Debug("[CraftingQueueProcessor] Game returned to IdleNormal while in Crafting state, starting hang watchdog");
                        _craftHangSince = DateTime.Now;
                    }
                    else if ((DateTime.Now - _craftHangSince).TotalSeconds > 3.0)
                    {
                        GatherBuddy.Log.Warning("[CraftingQueueProcessor] Craft hang detected: game idle but craft never started, auto-recovering to WaitingForJobSwitch");
                        _craftHangSince = DateTime.MinValue;
                        _currentState = QueueState.WaitingForJobSwitch;
                        StateChanged?.Invoke(_currentState);
                    }
                }
                else
                {
                    _craftHangSince = DateTime.MinValue;
                }
                break;
            case QueueState.Complete:
                break;
        }
    }

    private void ProcessTasks()
    {
        while (_tasks.Count > 0)
        {
            var result = _tasks[0]();
            switch (result)
            {
                case CraftingTasks.TaskResult.Done:
                    _tasks.RemoveAt(0);
                    break;
                case CraftingTasks.TaskResult.Retry:
                    return;
                case CraftingTasks.TaskResult.Abort:
                    _tasks.Clear();
                    if (_currentState == QueueState.Repairing)
                    {
                        GatherBuddy.Log.Warning("[CraftingQueueProcessor] Repair task aborted, recovering to WaitingForJobSwitch");
                        CraftingTasks.ResetRepairState();
                        _currentState = QueueState.WaitingForJobSwitch;
                        StateChanged?.Invoke(_currentState);
                    }
                    return;
            }
        }
    }

    private unsafe void UpdateJobSwitch()
    {
        if (_currentQueueIndex >= _queue.Count)
        {
            CompleteQueue();
            return;
        }

        while (_currentQueueIndex < _queue.Count && _queue[_currentQueueIndex].Options.Skipping)
        {
            GatherBuddy.Log.Debug($"[CraftingQueueProcessor] UpdateJobSwitch: skipping recipe {_queue[_currentQueueIndex].RecipeId}");
            _currentQueueIndex++;
        }

        if (_currentQueueIndex >= _queue.Count)
        {
            CompleteQueue();
            return;
        }

        var recipeItem = _queue[_currentQueueIndex];
        var recipe = RecipeManager.GetRecipe(recipeItem.RecipeId);
        if (recipe == null)
        {
            GatherBuddy.Log.Error($"[CraftingQueueProcessor] Could not find recipe {recipeItem.RecipeId}");
            SkipToNextRecipe();
            return;
        }

        var requiredJob = (uint)(recipe.Value.CraftType.RowId + 8);
        var currentJob = Dalamud.ClientState.LocalPlayer?.ClassJob.RowId ?? 0;

        if (currentJob != requiredJob)
        {
            if (Dalamud.Conditions[ConditionFlag.BetweenAreas] || Dalamud.Conditions[ConditionFlag.BetweenAreas51] ||
                (Lifestream.Enabled && Lifestream.IsBusy()) || !GenericHelpers.IsScreenReady())
            {
                GatherBuddy.Log.Debug("[CraftingQueueProcessor] Deferring job switch: zone transition, Lifestream active, or screen not ready");
                _jobSwitchRequestedFor = 0u;
                return;
            }

            if (_tasks.Count == 0 && _jobSwitchRequestedFor != requiredJob)
            {
                GatherBuddy.Log.Information($"[CraftingQueueProcessor] Job switch needed: {requiredJob}");
                bool needExitCraft = CraftingGameInterop.CurrentState == CraftingGameInterop.CraftState.IdleBetween;
                
                if (needExitCraft)
                {
                    GatherBuddy.Log.Debug("[CraftingQueueProcessor] Queueing TaskExitCraft before job switch");
                    _tasks.Add(() => CraftingTasks.TaskExitCraft());
                }
                
                _tasks.Add(() =>
                {
                    if (Dalamud.Conditions[ConditionFlag.BetweenAreas] || Dalamud.Conditions[ConditionFlag.BetweenAreas51])
                    {
                        GatherBuddy.Log.Debug("[CraftingQueueProcessor] Waiting for zone transition to complete before job switch");
                        return CraftingTasks.TaskResult.Retry;
                    }
                    if (Lifestream.Enabled && Lifestream.IsBusy())
                    {
                        GatherBuddy.Log.Debug("[CraftingQueueProcessor] Waiting for Lifestream to finish before job switch");
                        return CraftingTasks.TaskResult.Retry;
                    }
                    if (!GenericHelpers.IsScreenReady())
                    {
                        GatherBuddy.Log.Debug("[CraftingQueueProcessor] Waiting for screen ready before job switch");
                        return CraftingTasks.TaskResult.Retry;
                    }
                    SwitchJob(requiredJob);
                    return CraftingTasks.TaskResult.Done;
                });
                _jobSwitchRequestedFor = requiredJob;
            }
            else if (_tasks.Count == 0 && _jobSwitchRequestedFor == requiredJob)
            {
                GatherBuddy.Log.Debug("[CraftingQueueProcessor] Job switch task completed but job unchanged, resetting for retry");
                _jobSwitchRequestedFor = 0u;
            }
        }
        else
        {
            _jobSwitchRequestedFor = 0u;
            TransitionToRaphaelOrCraft();
        }
    }

    private void TransitionToRaphaelOrCraft()
    {
        if (NeedsMateria())
        {
            GatherBuddy.Log.Information("[CraftingQueueProcessor] Equipment has 100% spiritbond, extracting materia");
            QueueMateriaTasks();
            _currentState = QueueState.ExtractingMateria;
            StateChanged?.Invoke(_currentState);
            return;
        }

        if (NeedsRepair())
        {
            GatherBuddy.Log.Information("[CraftingQueueProcessor] Equipment needs repair before crafting");
            QueueRepairTasks();
            _currentState = QueueState.Repairing;
            StateChanged?.Invoke(_currentState);
            return;
        }

        var solverMode = GatherBuddy.Config.RaphaelSolverConfig.SolverMode;
        var itemSolverOverride = _currentQueueIndex < _queue.Count
            ? (_queue[_currentQueueIndex].CraftSettings?.SolverOverride ?? SolverOverrideMode.Default)
            : SolverOverrideMode.Default;
        var useRaphael = itemSolverOverride == SolverOverrideMode.RaphaelSolver
            || (itemSolverOverride == SolverOverrideMode.Default && solverMode == RaphaelSolverMode.PureRaphael);

        if (_raphaelCoordinator == null || !useRaphael)
        {
            GatherBuddy.Log.Debug($"[CraftingQueueProcessor] Not using Raphael solver, proceeding to craft");
            _currentState = QueueState.ReadyForCraft;
            StateChanged?.Invoke(_currentState);
            return;
        }

        if (_currentQueueIndex < _queue.Count)
        {
            var currentItem = _queue[_currentQueueIndex];
            var currentRecipe = RecipeManager.GetRecipe(currentItem.RecipeId);
            if (currentRecipe != null)
            {
                var r = currentRecipe.Value;
                var isNQOnly = !r.CanHq && !r.IsExpert && !r.ItemResult.Value.AlwaysCollectable && r.RequiredQuality == 0;
                var willQuickSynth = currentItem.Options.NQOnly && r.CanQuickSynth && HasRecipeCraftedBefore(r);
                if (isNQOnly || willQuickSynth)
                {
                    GatherBuddy.Log.Debug($"[CraftingQueueProcessor] Bypassing Raphael wait for recipe {currentItem.RecipeId} (NQOnly={isNQOnly}, WillQuickSynth={willQuickSynth})");
                    _currentState = QueueState.ReadyForCraft;
                    StateChanged?.Invoke(_currentState);
                    return;
                }
            }
        }

        if (_currentQueueIndex >= _queue.Count)
        {
            _currentState = QueueState.ReadyForCraft;
            StateChanged?.Invoke(_currentState);
            return;
        }
        
        var recipeItem = _queue[_currentQueueIndex];
        if (_enqueuedRaphaelRequests.TryGetValue(recipeItem.RecipeId, out var enqueuedRequest))
        {
            var currentRequest = BuildRaphaelRequestForRecipe(recipeItem.RecipeId);
            if (currentRequest != null && enqueuedRequest.GetKey() != currentRequest.GetKey())
            {
                GatherBuddy.Log.Warning($"[CraftingQueueProcessor] Stats mismatch for recipe {recipeItem.RecipeId} — saved gearset may be outdated.");
                GatherBuddy.Log.Warning($"[CraftingQueueProcessor] Enqueued key: {enqueuedRequest.GetKey()}, Current key: {currentRequest.GetKey()}");
                _enqueuedRaphaelRequests[recipeItem.RecipeId] = currentRequest;
                _raphaelCoordinator!.EnqueueSolvesFromRequests(new[] { currentRequest });
                _currentState = QueueState.WaitingForRaphaelSolution;
                StateChanged?.Invoke(_currentState);
                return;
            }
        }
        if (IsRaphaelSolutionReady(recipeItem.RecipeId))
        {
            GatherBuddy.Log.Debug($"[CraftingQueueProcessor] Raphael solution ready for recipe {recipeItem.RecipeId}");
            _currentState = QueueState.ReadyForCraft;
            StateChanged?.Invoke(_currentState);
        }
        else if (IsRaphaelSolutionFailed(recipeItem.RecipeId))
        {
            SkipFailedRaphaelItem(recipeItem.RecipeId);
        }
        else
        {
            GatherBuddy.Log.Debug($"[CraftingQueueProcessor] Waiting for Raphael solution for recipe {recipeItem.RecipeId}");
            _currentState = QueueState.WaitingForRaphaelSolution;
            StateChanged?.Invoke(_currentState);
        }
    }
    
    private void CheckRaphaelSolutionReady()
    {
        if (_currentQueueIndex >= _queue.Count)
        {
            _currentState = QueueState.ReadyForCraft;
            StateChanged?.Invoke(_currentState);
            return;
        }

        var recipeItem = _queue[_currentQueueIndex];

        if (IsRaphaelSolutionReady(recipeItem.RecipeId))
        {
            GatherBuddy.Log.Information($"[CraftingQueueProcessor] Raphael solution ready for recipe {recipeItem.RecipeId}");
            _currentState = QueueState.ReadyForCraft;
            StateChanged?.Invoke(_currentState);
        }
        else if (IsRaphaelSolutionFailed(recipeItem.RecipeId))
        {
            SkipFailedRaphaelItem(recipeItem.RecipeId);
        }
        else
        {
            var request = BuildRaphaelRequestForRecipe(recipeItem.RecipeId);
            if (request != null && _raphaelCoordinator != null)
                _raphaelCoordinator.ReenqueueIfMissing(request);
        }
    }
    
    private bool IsRaphaelSolutionReady(uint recipeId)
    {
        if (_raphaelCoordinator == null)
            return false;
        
        var request = BuildRaphaelRequestForRecipe(recipeId);
        if (request == null)
            return false;
        
        return _raphaelCoordinator.TryGetSolution(request, out var solution) && solution != null && !solution.IsFailed;
    }
    
    private bool IsRaphaelSolutionFailed(uint recipeId)
    {
        if (_raphaelCoordinator == null)
            return false;
        var request = BuildRaphaelRequestForRecipe(recipeId);
        return request != null && _raphaelCoordinator.HasFailedSolution(request, out _);
    }

    private unsafe void StartNextCraft()
    {
        if (_currentQueueIndex >= _queue.Count)
        {
            CompleteQueue();
            return;
        }

        if (_consumableDelayUntil != DateTime.MinValue)
        {
            if (DateTime.Now < _consumableDelayUntil)
                return;
            _consumableDelayUntil = DateTime.MinValue;
        }

        if (CraftingGameInterop.CurrentState == CraftingGameInterop.CraftState.QuickSynthesis)
        {
            var isComplete = IsQuickSynthesisComplete();
            if (isComplete)
            {
                GatherBuddy.Log.Information($"[CraftingQueueProcessor] Quick synthesis complete, closing window");
                CloseQuickSynthWindow();
            }
            return;
        }
        
        if (CraftingGameInterop.CurrentState != CraftingGameInterop.CraftState.IdleNormal && 
            CraftingGameInterop.CurrentState != CraftingGameInterop.CraftState.IdleBetween)
            return;

        var recipeItem = _queue[_currentQueueIndex];

        if (recipeItem.Options.Skipping)
        {
            GatherBuddy.Log.Debug($"[CraftingQueueProcessor] Skipping recipe {recipeItem.RecipeId} (Skipping flag)");
            SkipToNextRecipe();
            return;
        }

        var recipe = RecipeManager.GetRecipe(recipeItem.RecipeId);
        if (recipe == null)
        {
            GatherBuddy.Log.Error($"[CraftingQueueProcessor] Could not find recipe {recipeItem.RecipeId}");
            SkipToNextRecipe();
            return;
        }

        var consumableSettings = BuildConsumableSettings(recipeItem);
        if (consumableSettings != null)
        {
            var allApplied = ConsumableChecker.ApplyConsumables(consumableSettings);
            if (!allApplied)
            {
                if (CraftingGameInterop.CurrentState == CraftingGameInterop.CraftState.IdleBetween)
                {
                    GatherBuddy.Log.Debug("[CraftingQueueProcessor] Need to apply consumables, exiting crafting log first");
                    _tasks.Add(() => CraftingTasks.TaskExitCraft());
                    _consumableDelayUntil = DateTime.Now.AddSeconds(5);
                }
                else
                {
                    GatherBuddy.Log.Debug("[CraftingQueueProcessor] Applied consumables, delaying craft start by 3 seconds");
                    _consumableDelayUntil = DateTime.Now.AddSeconds(3);
                }
                return;
            }
        }

        var hasCraftedBefore = HasRecipeCraftedBefore(recipe.Value);
        var useQuickSynthesis = recipeItem.Options.NQOnly && recipe.Value.CanQuickSynth && hasCraftedBefore;
        if (recipeItem.Options.NQOnly && recipe.Value.CanQuickSynth && !hasCraftedBefore)
            GatherBuddy.Log.Information($"[CraftingQueueProcessor] Recipe not yet crafted — using normal craft first: {recipe.Value.ItemResult.Value.Name.ExtractText()}");
        uint craftQuantity = (uint)recipeItem.Quantity;
        
        if (useQuickSynthesis)
        {
            int lastIndex = _queue.FindLastIndex(item => item.RecipeId == recipeItem.RecipeId);
            if (lastIndex >= _currentQueueIndex)
            {
                craftQuantity = (uint)(lastIndex - _currentQueueIndex + 1);
                craftQuantity = Math.Min(craftQuantity, 99);
            }
            GatherBuddy.Log.Information($"[CraftingQueueProcessor] Using Quick Synthesis for {recipe.Value.ItemResult.Value.Name.ExtractText()} x{craftQuantity}");
        }
        
        UpdateCurrentRecipeTracking((int)craftQuantity);
        
        var useAllNQ = recipeItem.CraftSettings?.UseAllNQ ?? false;
        var ingredientPrefs = recipeItem.IngredientPreferences;
        CraftingGameInterop.SetIngredientPreferences(
            ingredientPrefs.Count > 0 || useAllNQ ? ingredientPrefs : null,
            useAllNQ);
        
        var selectedMacroId = recipeItem.CraftSettings?.SelectedMacroId;
        CraftingGameInterop.SetSelectedMacro(selectedMacroId);
        if (!string.IsNullOrEmpty(selectedMacroId))
        {
            GatherBuddy.Log.Information($"[CraftingQueueProcessor] Using macro: {selectedMacroId}");
        }

        var craftSolverOverride = recipeItem.CraftSettings?.SolverOverride ?? SolverOverrideMode.Default;
        var effectiveSolverMode = craftSolverOverride switch
        {
            SolverOverrideMode.StandardSolver     => RaphaelSolverMode.StandardSolver,
            SolverOverrideMode.RaphaelSolver      => RaphaelSolverMode.PureRaphael,
            SolverOverrideMode.ProgressOnlySolver => RaphaelSolverMode.ProgressOnly,
            _                                      => GatherBuddy.Config.RaphaelSolverConfig.SolverMode,
        };
        CraftingGameInterop.ReloadSolversForCraft(effectiveSolverMode);
        GatherBuddy.Log.Debug($"[CraftingQueueProcessor] Effective solver mode for this craft: {effectiveSolverMode}");

        _lastCraftWasQuickSynth = useQuickSynthesis;
        GatherBuddy.Log.Information($"[CraftingQueueProcessor] Starting craft {_currentQueueIndex + 1}/{_queue.Count}: {recipe.Value.ItemResult.Value.Name} x{craftQuantity}");
        CraftingGameInterop.StartCraft(recipe.Value, craftQuantity, useQuickSynthesis);
        _currentState = QueueState.Crafting;
        StateChanged?.Invoke(_currentState);
    }

    private RecipeCraftSettings? BuildConsumableSettings(CraftingListItem? recipeItem)
    {
        if (_listConsumables == null && recipeItem?.ConsumableOverrides?.HasAnyOverrides() != true && recipeItem?.CraftSettings == null)
            return null;

        var foodItemId = _listConsumables?.FoodItemId;
        var foodHq = _listConsumables?.FoodHQ ?? false;
        var medicineItemId = _listConsumables?.MedicineItemId;
        var medicineHq = _listConsumables?.MedicineHQ ?? false;
        var manualItemId = _listConsumables?.ManualItemId;
        var squadronManualItemId = _listConsumables?.SquadronManualItemId;

        if (recipeItem?.CraftSettings != null && recipeItem.CraftSettings.HasAnySettings())
        {
            var cs = recipeItem.CraftSettings;
            var effectiveFoodMode = cs.FoodMode == ConsumableOverrideMode.Inherit && cs.FoodItemId.HasValue ? ConsumableOverrideMode.Specific : cs.FoodMode;
            var effectiveMedicineMode = cs.MedicineMode == ConsumableOverrideMode.Inherit && cs.MedicineItemId.HasValue ? ConsumableOverrideMode.Specific : cs.MedicineMode;
            var effectiveManualMode = cs.ManualMode == ConsumableOverrideMode.Inherit && cs.ManualItemId.HasValue ? ConsumableOverrideMode.Specific : cs.ManualMode;
            var effectiveSquadronMode = cs.SquadronManualMode == ConsumableOverrideMode.Inherit && cs.SquadronManualItemId.HasValue ? ConsumableOverrideMode.Specific : cs.SquadronManualMode;

            ApplyOverride(new ConsumableOverride { Mode = effectiveFoodMode, ItemId = cs.FoodItemId, HQ = cs.FoodHQ }, ref foodItemId, ref foodHq);
            ApplyOverride(new ConsumableOverride { Mode = effectiveMedicineMode, ItemId = cs.MedicineItemId, HQ = cs.MedicineHQ }, ref medicineItemId, ref medicineHq);
            ApplyOverride(new ConsumableOverride { Mode = effectiveManualMode, ItemId = cs.ManualItemId }, ref manualItemId);
            ApplyOverride(new ConsumableOverride { Mode = effectiveSquadronMode, ItemId = cs.SquadronManualItemId }, ref squadronManualItemId);
        }
        else if (recipeItem?.ConsumableOverrides != null)
        {
            ApplyOverride(recipeItem.ConsumableOverrides.Food, ref foodItemId, ref foodHq);
            ApplyOverride(recipeItem.ConsumableOverrides.Medicine, ref medicineItemId, ref medicineHq);
            ApplyOverride(recipeItem.ConsumableOverrides.Manual, ref manualItemId);
            ApplyOverride(recipeItem.ConsumableOverrides.SquadronManual, ref squadronManualItemId);
        }

        if (!foodItemId.HasValue && !medicineItemId.HasValue && !manualItemId.HasValue && !squadronManualItemId.HasValue)
            return null;

        return new RecipeCraftSettings
        {
            FoodItemId = foodItemId,
            FoodHQ = foodHq,
            MedicineItemId = medicineItemId,
            MedicineHQ = medicineHq,
            ManualItemId = manualItemId,
            SquadronManualItemId = squadronManualItemId,
        };
    }

    private static void ApplyOverride(ConsumableOverride? overrideSetting, ref uint? itemId, ref bool hq)
    {
        if (overrideSetting == null)
            return;

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
            return;

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

    private void OnQuickSynthProgress(int current, int max)
    {
        if (current == 0)
            return;
        
        GatherBuddy.Log.Debug($"[CraftingQueueProcessor] Quick synth progress: {current}/{max}, incrementing index");
        _currentQueueIndex++;
        _currentProcessedRecipeCount++;
        
        if (current == max)
        {
            GatherBuddy.Log.Debug($"[CraftingQueueProcessor] Quick synth batch complete");
        }
    }
    
    public void OnCraftFinished(Recipe? recipe, bool cancelled)
    {
        if (_currentState != QueueState.Crafting)
            return;

        _craftHangSince = DateTime.MinValue;

        if (cancelled)
        {
            GatherBuddy.Log.Warning($"[CraftingQueueProcessor] Craft cancelled at index {_currentQueueIndex}");
            CompleteQueue();
            return;
        }

        if (!_lastCraftWasQuickSynth)
        {
            GatherBuddy.Log.Debug($"[CraftingQueueProcessor] Normal craft completed, moving to next");
            _currentQueueIndex++;
            UpdateCurrentRecipeTracking();
        }
        else
        {
            GatherBuddy.Log.Debug("[CraftingQueueProcessor] Quick synth batch completed (index already advanced by progress events)");
        }
        
        if (_currentQueueIndex >= _queue.Count)
        {
            CompleteQueue();
        }
        else
        {
            _currentState = QueueState.WaitingForJobSwitch;
            StateChanged?.Invoke(_currentState);
        }
    }


    private void SkipFailedRaphaelItem(uint recipeId)
    {
        var recipe = RecipeManager.GetRecipe(recipeId);
        var itemName = recipe != null ? recipe.Value.ItemResult.Value.Name.ExtractText() : $"Recipe {recipeId}";

        string failureReason = "unknown";
        if (_raphaelCoordinator != null)
        {
            var request = BuildRaphaelRequestForRecipe(recipeId);
            if (request != null)
                _raphaelCoordinator.HasFailedSolution(request, out failureReason);
        }

        GatherBuddy.Log.Warning($"[CraftingQueueProcessor] Skipping '{itemName}' (recipe {recipeId}) - Raphael solution failed: {failureReason ?? "unknown"}");
        _currentQueueIndex++;

        if (_currentQueueIndex >= _queue.Count)
            CompleteQueue();
        else
        {
            _currentState = QueueState.WaitingForJobSwitch;
            StateChanged?.Invoke(_currentState);
        }
    }

    private RaphaelSolveRequest? BuildRaphaelRequestForRecipe(uint recipeId)
    {
        var recipe = RecipeManager.GetRecipe(recipeId);
        if (recipe == null) return null;

        var requiredJob = (uint)(recipe.Value.CraftType.RowId + 8);
        var currentJob = Dalamud.ClientState.LocalPlayer?.ClassJob.RowId ?? 0;
        var recipeItem = _queue.FirstOrDefault(r => r.RecipeId == recipeId);
        var consumableSettings = BuildConsumableSettings(recipeItem);

        GameStateBuilder.PlayerStats? stats;
        if (currentJob == requiredJob)
        {
            stats = CraftingStateBuilder.GetCurrentPlayerStats();
            if (stats != null && consumableSettings != null)
            {
                var unapplied = new RecipeCraftSettings
                {
                    FoodItemId     = consumableSettings.FoodItemId.HasValue     && !ConsumableChecker.HasFoodBuff(consumableSettings.FoodItemId.Value)         ? consumableSettings.FoodItemId     : null,
                    FoodHQ         = consumableSettings.FoodHQ,
                    MedicineItemId = consumableSettings.MedicineItemId.HasValue && !ConsumableChecker.HasMedicineBuff(consumableSettings.MedicineItemId.Value) ? consumableSettings.MedicineItemId : null,
                    MedicineHQ     = consumableSettings.MedicineHQ,
                };
                stats = GearsetStatsReader.ApplyConsumablesToStats(stats, unapplied);
            }
        }
        else
        {
            stats = GearsetStatsReader.ReadGearsetStatsForJob(requiredJob);
            if (stats != null && consumableSettings != null)
                stats = GearsetStatsReader.ApplyConsumablesToStats(stats, consumableSettings);
        }

        if (stats == null) return null;

        var ingredientPreferences = recipeItem?.IngredientPreferences;
        int initialQuality = ingredientPreferences != null && ingredientPreferences.Count > 0
            ? QualityCalculator.CalculateInitialQuality(recipe.Value, ingredientPreferences)
            : 0;

        var specialist = GatherBuddy.Config.RaphaelSolverConfig.RaphaelAllowSpecialistActions && stats.Specialist;
        return new RaphaelSolveRequest(
            RecipeId: recipeId,
            Level: stats.Level,
            Craftsmanship: stats.Craftsmanship,
            Control: stats.Control,
            CP: stats.CP,
            Manipulation: stats.Manipulation,
            Specialist: specialist,
            InitialQuality: initialQuality
        );
    }

    private void SkipToNextRecipe()
    {
        _currentQueueIndex++;
        if (_currentQueueIndex >= _queue.Count)
        {
            CompleteQueue();
        }
        else
        {
            _currentState = QueueState.WaitingForJobSwitch;
            StateChanged?.Invoke(_currentState);
        }
    }

    private void CompleteQueue()
    {
        GatherBuddy.Log.Information($"[CraftingQueueProcessor] Queue complete!");
        YesAlready.Unlock();
        GatherBuddy.AutoGather.Enabled = false;
        
        var craftState = CraftingGameInterop.CurrentState;
        bool needExitCraft = craftState == CraftingGameInterop.CraftState.IdleBetween ||
                            craftState == CraftingGameInterop.CraftState.WaitFinish ||
                            craftState == CraftingGameInterop.CraftState.QuickSynthesis;
        if (needExitCraft)
        {
            GatherBuddy.Log.Debug($"[CraftingQueueProcessor] Queueing TaskExitCraft to close crafting log");
            _tasks.Add(() => CraftingTasks.TaskExitCraft());
        }
        
        _currentState = QueueState.Complete;
        StateChanged?.Invoke(_currentState);
        QueueCompleted?.Invoke();
    }

    private string GetJobName(uint jobId)
    {
        return jobId switch
        {
            8 => "Carpenter",
            9 => "Blacksmith",
            10 => "Armorer",
            11 => "Goldsmith",
            12 => "Leatherworker",
            13 => "Weaver",
            14 => "Alchemist",
            15 => "Culinarian",
            _ => $"Job {jobId}"
        };
    }

    private unsafe void SwitchJob(uint jobId)
    {
        try
        {
            var gearsetModule = RaptureGearsetModule.Instance();
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

            var jobName = GetJobName(jobId);
            GatherBuddy.Log.Error($"[CraftingQueueProcessor] No gearset found for {jobName} (Job ID: {jobId})");
            Dalamud.Chat.PrintError($"[GatherBuddy] Cannot continue crafting: No gearset found for {jobName}. Please create a gearset for this job.");
            CompleteQueue();
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"Failed to switch job: {ex.Message}");
        }
    }

    private unsafe void EnqueueRaphaelSolvesFromCraftStates(List<CraftingListItem> queue)
    {
        if (_raphaelCoordinator == null)
            return;

        var recipeSheet = Dalamud.GameData.GetExcelSheet<Recipe>();
        if (recipeSheet == null)
        {
            GatherBuddy.Log.Warning($"[CraftingQueueProcessor] Cannot get recipe sheet for Raphael enqueue");
            return;
        }

        var requests = new List<RaphaelSolveRequest>();
        foreach (var item in queue)
        {
            try
            {
                if (!recipeSheet.TryGetRow(item.RecipeId, out var recipe))
                    continue;

                var isNQOnly = !recipe.CanHq && !recipe.IsExpert && !recipe.ItemResult.Value.AlwaysCollectable && recipe.RequiredQuality == 0;
                var willQuickSynth = item.Options.NQOnly && recipe.CanQuickSynth && HasRecipeCraftedBefore(recipe);
                if (isNQOnly || willQuickSynth)
                    continue;

                var requiredJob = (uint)(recipe.CraftType.RowId + 8);
                var gearsetStats = GearsetStatsReader.ReadGearsetStatsForJob(requiredJob);

                if (gearsetStats == null)
                {
                    GatherBuddy.Log.Warning($"[CraftingQueueProcessor] Could not read gearset stats for job {requiredJob}, no gearset found");
                    continue;
                }

                var consumableSettings = BuildConsumableSettings(item);
                if (consumableSettings != null)
                    gearsetStats = GearsetStatsReader.ApplyConsumablesToStats(gearsetStats, consumableSettings);

                int initialQuality = item.IngredientPreferences != null && item.IngredientPreferences.Count > 0
                    ? QualityCalculator.CalculateInitialQuality(recipe, item.IngredientPreferences)
                    : 0;

                var specialist = GatherBuddy.Config.RaphaelSolverConfig.RaphaelAllowSpecialistActions && gearsetStats.Specialist;
                var request = new RaphaelSolveRequest(
                    RecipeId: recipe.RowId,
                    Level: gearsetStats.Level,
                    Craftsmanship: gearsetStats.Craftsmanship,
                    Control: gearsetStats.Control,
                    CP: gearsetStats.CP,
                    Manipulation: gearsetStats.Manipulation,
                    Specialist: specialist,
                    InitialQuality: initialQuality
                );

                requests.Add(request);
                _enqueuedRaphaelRequests.TryAdd(recipe.RowId, request);
            }
            catch (Exception ex)
            {
                GatherBuddy.Log.Warning($"[CraftingQueueProcessor] Failed to read gearset stats for recipe {item.RecipeId}: {ex.Message}");
            }
        }

        if (requests.Count > 0)
        {
            GatherBuddy.Log.Debug($"[CraftingQueueProcessor] Enqueuing {requests.Count} requests with effective consumables");
            _raphaelCoordinator.ClearIfAutoEnabled();
            _raphaelCoordinator.EnqueueSolvesFromRequests(requests);
        }
    }

    private static bool HasRecipeCraftedBefore(Lumina.Excel.Sheets.Recipe recipe)
    {
        if (recipe.SecretRecipeBook.RowId > 0)
            return true;
        return QuestManager.IsRecipeComplete(recipe.RowId);
    }

    private bool NeedsRepair()
    {
        if (!GatherBuddy.Config.VulcanRepairConfig.Enabled)
            return false;

        var repairThreshold = GatherBuddy.Config.VulcanRepairConfig.RepairThreshold;
        return RepairManager.NeedsRepair(repairThreshold);
    }

    private unsafe void QueueRepairTasks()
    {
        GatherBuddy.Log.Debug("[CraftingQueueProcessor] Queueing repair tasks");
        CraftingTasks.ResetRepairState();
        
        bool needExitCraft = CraftingGameInterop.CurrentState == CraftingGameInterop.CraftState.IdleBetween;
        if (needExitCraft)
        {
            GatherBuddy.Log.Debug("[CraftingQueueProcessor] Queueing TaskExitCraft before repair");
            _tasks.Add(() => CraftingTasks.TaskExitCraft());
        }

        var canSelfRepair = RepairManager.CanRepairAny();
        var hasRepairNPC = RepairManager.RepairNPCNearby(out var npc);
        var prioritizeNPC = GatherBuddy.Config.VulcanRepairConfig.PrioritizeNPCRepair;
        var preferredNPC = GatherBuddy.Config.VulcanRepairConfig.PreferredRepairNPC;

        if (prioritizeNPC && preferredNPC != null)
        {
            if (hasRepairNPC && npc != null && npc.DataId == preferredNPC.DataId)
            {
                var repairPrice = RepairManager.GetNPCRepairPrice();
                var gilCount = InventoryManager.Instance()->GetInventoryItemCount(1);

            if (gilCount >= repairPrice)
            {
                GatherBuddy.Log.Information($"[CraftingQueueProcessor] Using nearby preferred NPC repair (cost: {repairPrice} gil)");
                _tasks.Add(() => CraftingTasks.TaskInteractWithRepairNPC());
                _tasks.Add(() => CraftingTasks.TaskSelectRepairFromMenu());
                _tasks.Add(() => CraftingTasks.TaskExecuteRepair());
                _tasks.Add(() => CraftingTasks.TaskCloseRepairWindow());
                _tasks.Add(() => { TransitionFromRepairComplete(); return CraftingTasks.TaskResult.Done; });
                return;
            }
                else
                {
                    GatherBuddy.Log.Warning($"[CraftingQueueProcessor] Not enough gil for NPC repair ({gilCount}/{repairPrice})");
                }
            }
        }

        if (prioritizeNPC && preferredNPC != null)
        {
            var repairPrice = RepairManager.GetNPCRepairPrice();
            var gilCount = InventoryManager.Instance()->GetInventoryItemCount(1);

            if (gilCount >= repairPrice)
            {
                GatherBuddy.Log.Information($"[CraftingQueueProcessor] Navigating to preferred repair NPC: {preferredNPC.Name}");
                _tasks.Add(() => CraftingTasks.TaskNavigateToRepairNPC(preferredNPC));
                _tasks.Add(() => CraftingTasks.TaskInteractWithRepairNPC());
                _tasks.Add(() => CraftingTasks.TaskSelectRepairFromMenu());
                _tasks.Add(() => CraftingTasks.TaskExecuteRepair());
                _tasks.Add(() => CraftingTasks.TaskCloseRepairWindow());
                _tasks.Add(() => { TransitionFromRepairComplete(); return CraftingTasks.TaskResult.Done; });
                return;
            }
            else
            {
                GatherBuddy.Log.Warning($"[CraftingQueueProcessor] Not enough gil for NPC repair ({gilCount}/{repairPrice})");
            }
        }
        
        if (prioritizeNPC && preferredNPC == null && hasRepairNPC && npc != null)
        {
            var repairPrice = RepairManager.GetNPCRepairPrice();
            var gilCount = InventoryManager.Instance()->GetInventoryItemCount(1);

            if (gilCount >= repairPrice)
            {
                GatherBuddy.Log.Information($"[CraftingQueueProcessor] Using nearby repair NPC (no preferred set, cost: {repairPrice} gil)");
                _tasks.Add(() => CraftingTasks.TaskInteractWithRepairNPC());
                _tasks.Add(() => CraftingTasks.TaskSelectRepairFromMenu());
                _tasks.Add(() => CraftingTasks.TaskExecuteRepair());
                _tasks.Add(() => CraftingTasks.TaskCloseRepairWindow());
                _tasks.Add(() => { TransitionFromRepairComplete(); return CraftingTasks.TaskResult.Done; });
                return;
            }
            else
            {
                GatherBuddy.Log.Warning($"[CraftingQueueProcessor] Not enough gil for NPC repair ({gilCount}/{repairPrice})");
            }
        }

        if (prioritizeNPC && preferredNPC == null && !hasRepairNPC)
        {
            var currentZoneNPC = FindNearestRepairNPCInCurrentZone();
            if (currentZoneNPC != null)
            {
                var repairPrice = RepairManager.GetNPCRepairPrice();
                var gilCount = InventoryManager.Instance()->GetInventoryItemCount(1);

            if (gilCount >= repairPrice)
            {
                GatherBuddy.Log.Information($"[CraftingQueueProcessor] Navigating to nearest repair NPC in current zone: {currentZoneNPC.Name}");
                _tasks.Add(() => CraftingTasks.TaskNavigateToRepairNPC(currentZoneNPC));
                _tasks.Add(() => CraftingTasks.TaskInteractWithRepairNPC());
                _tasks.Add(() => CraftingTasks.TaskSelectRepairFromMenu());
                _tasks.Add(() => CraftingTasks.TaskExecuteRepair());
                _tasks.Add(() => CraftingTasks.TaskCloseRepairWindow());
                _tasks.Add(() => { TransitionFromRepairComplete(); return CraftingTasks.TaskResult.Done; });
                return;
            }
                else
                {
                    GatherBuddy.Log.Warning($"[CraftingQueueProcessor] Not enough gil for NPC repair ({gilCount}/{repairPrice})");
                }
            }
        }

        if (canSelfRepair)
        {
            GatherBuddy.Log.Information("[CraftingQueueProcessor] Using self-repair");
            _tasks.Add(() => CraftingTasks.TaskOpenRepairWindow());
            _tasks.Add(() => CraftingTasks.TaskExecuteRepair(isSelfRepair: true));
            _tasks.Add(() => CraftingTasks.TaskWaitForRepairAutoClose());
            _tasks.Add(() => CraftingTasks.TaskCloseRepairWindow());
            _tasks.Add(() => { TransitionFromRepairComplete(); return CraftingTasks.TaskResult.Done; });
            return;
        }

        GatherBuddy.Log.Error("[CraftingQueueProcessor] Cannot repair: no dark matter and no repair NPC available");
        _tasks.Add(() => { CompleteQueue(); return CraftingTasks.TaskResult.Abort; });
    }

    private void QueueRetainerBellNavigationTasks()
    {
        var bell = RetainerTaskExecutor.FindNearestBellForNavigation();
        if (bell == null)
        {
            GatherBuddy.Log.Warning("[CraftingQueueProcessor] No retainer bell found in current zone, skipping navigation");
            _currentState = QueueState.WithdrawingFromRetainer;
            QueueRetainerWithdrawalTasks();
            return;
        }

        _retainerBellNavigator = new RetainerBellNavigator();
        if (!_retainerBellNavigator.StartNavigation(bell))
        {
            GatherBuddy.Log.Warning("[CraftingQueueProcessor] Failed to start retainer bell navigation");
            _currentState = QueueState.WithdrawingFromRetainer;
            QueueRetainerWithdrawalTasks();
            return;
        }

        _tasks.Add(() =>
        {
            if (_retainerBellNavigator == null)
                return CraftingTasks.TaskResult.Done;

            _retainerBellNavigator.Update();
            if (_retainerBellNavigator.IsComplete)
            {
                if (_retainerBellNavigator.IsFailed)
                    GatherBuddy.Log.Warning("[CraftingQueueProcessor] Retainer bell navigation failed, proceeding to withdrawal anyway");
                else
                    GatherBuddy.Log.Information("[CraftingQueueProcessor] Arrived at retainer bell");
                return CraftingTasks.TaskResult.Done;
            }
            return CraftingTasks.TaskResult.Retry;
        });

        _tasks.Add(() =>
        {
            GatherBuddy.Log.Debug("[CraftingQueueProcessor] Navigation complete, starting retainer withdrawal");
            _currentState = QueueState.WithdrawingFromRetainer;
            QueueRetainerWithdrawalTasks();
            return CraftingTasks.TaskResult.Done;
        });
    }

    private void QueueRetainerWithdrawalTasks()
    {
        RefreshRetainerRestockPlanForWithdrawal();
        GatherBuddy.Log.Debug($"[CraftingQueueProcessor] Building retainer withdrawal plan ({_retainerPrecraftItems.Count} precraft(s), {_allMaterials.Count} leaf material(s))");

        var combinedItems = new Dictionary<uint, int>(_allMaterials);
        foreach (var (k, v) in _retainerPrecraftItems)
        {
            if (combinedItems.ContainsKey(k)) combinedItems[k] += v;
            else combinedItems[k] = v;
        }

        var queueRecipeByItem = _expandedQueueForRetainer
            .Select(qi => RecipeManager.GetRecipe(qi.RecipeId))
            .Where(r => r.HasValue)
            .GroupBy(r => r!.Value.ItemResult.RowId)
            .ToDictionary(g => g.Key, g => g.First()!.Value);

        _retainerSkipAmounts = new Dictionary<uint, int>(_retainerPrecraftItems);
        foreach (var (pulledItemId, pullQty) in _retainerPrecraftItems)
        {
            if (!queueRecipeByItem.TryGetValue(pulledItemId, out var pulledRecipe))
                continue;
            int craftsDisplaced = (int)Math.Ceiling((double)pullQty / pulledRecipe.AmountResult);
            foreach (var (subItemId, amtPerCraft) in RecipeManager.GetIngredients(pulledRecipe))
            {
                int addedSkip = amtPerCraft * craftsDisplaced;
                if (_retainerSkipAmounts.ContainsKey(subItemId))
                {
                    _retainerSkipAmounts[subItemId] += addedSkip;
                    GatherBuddy.Log.Debug($"[CraftingQueueProcessor] Skip plan: restored {addedSkip} to {subItemId} (displaced by {pullQty}× {pulledItemId}), total={_retainerSkipAmounts[subItemId]}");
                }
                else if (queueRecipeByItem.ContainsKey(subItemId))
                {
                    _retainerSkipAmounts[subItemId] = addedSkip;
                    GatherBuddy.Log.Debug($"[CraftingQueueProcessor] Skip plan: added {addedSkip} to displaced sub-precraft {subItemId} (sub of {pullQty}× {pulledItemId}), total={addedSkip}");
                }
            }
        }

        var qualityTargets = RetainerTaskExecutor.ComputeQualityTargets(combinedItems, _expandedQueueForRetainer);
        _precraftQualityTargets = qualityTargets
            .Where(kv => _retainerPrecraftItems.ContainsKey(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        _retainerExecutor = new RetainerTaskExecutor(combinedItems, qualityTargets);

        QueueRetainerWithdrawalExecutionTasks();
    }

    private void RefreshRetainerRestockPlanForWithdrawal()
    {
        if (!_retainerRestock || !AllaganTools.Enabled || _retainerPlanningList == null)
            return;

        GatherBuddy.Log.Debug("[CraftingQueueProcessor] Refreshing retainer restock plan before withdrawal");

        var previousMaterials = new Dictionary<uint, int>(_allMaterials);
        var previousPrecraftItems = new Dictionary<uint, int>(_retainerPrecraftItems);
        var (refreshedMaterials, refreshedPrecraftItems) = RetainerTaskExecutor.PlanRetainerRestock(_retainerPlanningList, _expandedQueueForRetainer);

        LogRetainerPlanDifferences("leaf material", previousMaterials, refreshedMaterials);
        LogRetainerPlanDifferences("precraft pull", previousPrecraftItems, refreshedPrecraftItems);

        _allMaterials = refreshedMaterials;
        _retainerPrecraftItems = refreshedPrecraftItems;
    }

    private static void LogRetainerPlanDifferences(string label, Dictionary<uint, int> previousPlan, Dictionary<uint, int> refreshedPlan)
    {
        var changes = previousPlan.Keys
            .Union(refreshedPlan.Keys)
            .Select(itemId => (ItemId: itemId, PreviousAmount: previousPlan.GetValueOrDefault(itemId), RefreshedAmount: refreshedPlan.GetValueOrDefault(itemId)))
            .Where(change => change.PreviousAmount != change.RefreshedAmount)
            .OrderBy(change => change.ItemId)
            .ToList();

        if (changes.Count == 0)
        {
            GatherBuddy.Log.Debug($"[CraftingQueueProcessor] Retainer {label} plan unchanged after refresh");
            return;
        }

        GatherBuddy.Log.Debug($"[CraftingQueueProcessor] Retainer {label} plan refreshed with {changes.Count} change(s)");
        foreach (var change in changes)
            GatherBuddy.Log.Debug($"[CraftingQueueProcessor]   {label}: item {change.ItemId} {change.PreviousAmount} -> {change.RefreshedAmount}");
    }

    private void QueueRetainerWithdrawalExecutionTasks()
    {
        if (_retainerExecutor == null)
        {
            GatherBuddy.Log.Warning("[CraftingQueueProcessor] Retainer withdrawal executor unavailable, proceeding to gather stage");
            TransitionFromRetainerWithdrawComplete();
            return;
        }

        _tasks.Add(() =>
        {
            if (_retainerExecutor == null)
                return CraftingTasks.TaskResult.Done;
            var result = _retainerExecutor.Tick();
            if (result == CraftingTasks.TaskResult.Done)
            {
                if (_retainerExecutor.IsAborted)
                    GatherBuddy.Log.Warning("[CraftingQueueProcessor] Retainer withdrawal aborted, proceeding with original materials");
                else
                    GatherBuddy.Log.Information("[CraftingQueueProcessor] Retainer withdrawal complete");
            }
            return result;
        });

        _tasks.Add(() =>
        {
            TransitionFromRetainerWithdrawComplete();
            return CraftingTasks.TaskResult.Done;
        });
    }

    private unsafe void TransitionFromRetainerWithdrawComplete()
    {

        GatherBuddy.Log.Debug("[CraftingQueueProcessor] Computing remaining materials after retainer withdrawal");

        int stillGatherCount = 0;
        var inventoryMgr = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
        foreach (var (itemId, totalNeeded) in _allMaterials)
        {
            int inBag = 0;
            if (inventoryMgr != null)
                inBag = (int)(inventoryMgr->GetInventoryItemCount(itemId, false, false, false)
                            + inventoryMgr->GetInventoryItemCount(itemId, true,  false, false));
            if (inBag < totalNeeded)
                stillGatherCount++;
        }

        GatherBuddy.Log.Information($"[CraftingQueueProcessor] After retainer withdrawal: {stillGatherCount} item(s) still need gathering");

        ApplyPostWithdrawalSkips();

        _currentState = QueueState.WaitingForGather;
        StateChanged?.Invoke(_currentState);

        CraftingGatherBridge.CreateGatherListForMissingIngredients(_allMaterials);
    }

    private void ApplyPostWithdrawalSkips()
    {
        if (_retainerSkipAmounts.Count == 0) return;
        if (_retainerExecutor?.IsAborted == true) return;

        var skipRemaining = new Dictionary<uint, int>(_retainerSkipAmounts);
        foreach (var queueItem in _queue)
        {
            if (queueItem.Options.Skipping) continue;

            var recipe = RecipeManager.GetRecipe(queueItem.RecipeId);
            if (recipe == null) continue;

            var resultItemId = recipe.Value.ItemResult.RowId;
            if (!skipRemaining.TryGetValue(resultItemId, out var remaining) || remaining <= 0) continue;

            int amountPerCraft = (int)recipe.Value.AmountResult;
            queueItem.Options.Skipping = true;
            skipRemaining[resultItemId] = Math.Max(0, remaining - amountPerCraft);
            GatherBuddy.Log.Debug($"[CraftingQueueProcessor] Post-withdrawal skip: recipe {queueItem.RecipeId} result={resultItemId} (skip remaining={skipRemaining[resultItemId]})");
        }
    }

    private void TransitionFromRepairComplete()
    {
        GatherBuddy.Log.Information("[CraftingQueueProcessor] Repair complete, continuing to craft");
        _currentState = QueueState.WaitingForJobSwitch;
        StateChanged?.Invoke(_currentState);
    }

    private bool NeedsMateria()
    {
        if (!GatherBuddy.Config.VulcanMateriaConfig.Enabled)
            return false;
        if (!MateriaManager.IsExtractionUnlocked())
            return false;
        if (!MateriaManager.HasFreeInventorySlots())
            return false;
        return MateriaManager.IsSpiritbondReadyAny();
    }

    private void QueueMateriaTasks()
    {
        GatherBuddy.Log.Debug("[CraftingQueueProcessor] Queueing materia extraction tasks");

        if (CraftingGameInterop.CurrentState == CraftingGameInterop.CraftState.IdleBetween)
        {
            GatherBuddy.Log.Debug("[CraftingQueueProcessor] Queueing TaskExitCraft before materia extraction");
            _tasks.Add(() => CraftingTasks.TaskExitCraft());
        }

        _tasks.Add(() => CraftingTasks.TaskExtractAllMateria());
        _tasks.Add(() => { TransitionFromMateriaComplete(); return CraftingTasks.TaskResult.Done; });
    }

    private void TransitionFromMateriaComplete()
    {
        GatherBuddy.Log.Information("[CraftingQueueProcessor] Materia extraction complete, continuing to craft");
        _currentState = QueueState.WaitingForJobSwitch;
        StateChanged?.Invoke(_currentState);
    }
    
    private unsafe bool IsQuickSynthesisComplete()
    {
        try
        {
            var quickSynthAddon = Dalamud.GameGui.GetAddonByName("SynthesisSimple");
            if (quickSynthAddon == null || quickSynthAddon.Address == nint.Zero)
                return false;
                
            var atkUnit = (AtkUnitBase*)quickSynthAddon.Address;
            if (atkUnit == null || !atkUnit->IsVisible || atkUnit->AtkValuesCount < 5)
                return false;
                
            var current = atkUnit->AtkValues[3].Int;
            var max = atkUnit->AtkValues[4].Int;
            
            return current >= max && max > 0;
        }
        catch
        {
            return false;
        }
    }
    
    private unsafe void CloseQuickSynthWindow()
    {
        try
        {
            var quickSynthAddon = Dalamud.GameGui.GetAddonByName("SynthesisSimple");
            if (quickSynthAddon == null || quickSynthAddon.Address == nint.Zero)
                return;
                
            var atkUnit = (AtkUnitBase*)quickSynthAddon.Address;
            if (atkUnit == null)
                return;
                
            Callback.Fire(atkUnit, true, -1);
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[CraftingQueueProcessor] Failed to close quick synth window: {ex.Message}");
        }
    }
    
    private RepairNPCData? FindNearestRepairNPCInCurrentZone()
    {
        var currentTerritory = Dalamud.ClientState.TerritoryType;
        var playerPos = Dalamud.ClientState.LocalPlayer?.Position;
        
        if (playerPos == null)
            return null;
        
        var npcsInZone = RepairNPCHelper.RepairNPCs
            .Where(npc => npc.TerritoryType == currentTerritory)
            .ToList();
        
        if (npcsInZone.Count == 0)
        {
            GatherBuddy.Log.Warning($"[CraftingQueueProcessor] No repair NPCs found in current zone ({currentTerritory})");
            return null;
        }
        
        var nearest = npcsInZone
            .OrderBy(npc => System.Numerics.Vector3.Distance(playerPos.Value, npc.Position))
            .First();
        
        GatherBuddy.Log.Debug($"[CraftingQueueProcessor] Found nearest repair NPC in zone: {nearest.Name}");
        return nearest;
    }

    public void Pause()
    {
        if (_paused || _currentState == QueueState.Complete || _currentState == QueueState.Idle)
            return;

        GatherBuddy.Log.Information("[CraftingQueueProcessor] Pausing queue");
        _paused = true;
        if (_currentState == QueueState.NavigatingToRetainerBell)
        {
            GatherBuddy.Log.Debug("[CraftingQueueProcessor] Pausing retainer bell navigation");
            _retainerBellNavigator?.Stop();
            _retainerBellNavigator = null;
        }
        _tasks.Clear();
        YesAlready.Unlock();
        
        if (_currentState == QueueState.WaitingForGather)
        {
            var gatherList = CraftingGatherBridge.GetTemporaryGatherList();
            if (gatherList != null)
            {
                GatherBuddy.Log.Debug("[CraftingQueueProcessor] Pausing auto-gather but keeping list");
                _pausedDuringGather = true;
                CraftingGatherBridge.PreserveListOnDisable = true;
                GatherBuddy.AutoGather.Enabled = false;
                CraftingGatherBridge.PreserveListOnDisable = false;
            }
        }
    }

    public void Resume()
    {
        if (!_paused)
            return;

        GatherBuddy.Log.Information("[CraftingQueueProcessor] Resuming queue");
        _paused = false;
        YesAlready.Lock();

        if (_currentState == QueueState.NavigatingToRetainerBell)
        {
            GatherBuddy.Log.Debug("[CraftingQueueProcessor] Resuming retainer bell navigation");
            QueueRetainerBellNavigationTasks();
            return;
        }

        if (_currentState == QueueState.WithdrawingFromRetainer)
        {
            GatherBuddy.Log.Debug("[CraftingQueueProcessor] Resuming retainer withdrawal");
            if (_retainerExecutor == null)
                QueueRetainerWithdrawalTasks();
            else
                QueueRetainerWithdrawalExecutionTasks();
            return;
        }
        
        if (_pausedDuringGather && _currentState == QueueState.WaitingForGather)
        {
            var gatherList = CraftingGatherBridge.GetTemporaryGatherList();
            if (gatherList != null && gatherList.Items.Count > 0)
            {
                GatherBuddy.Log.Debug("[CraftingQueueProcessor] Resuming auto-gather with existing list");
                GatherBuddy.AutoGather.Enabled = true;
                _pausedDuringGather = false;
            }
            else
            {
                GatherBuddy.Log.Debug("[CraftingQueueProcessor] No items to gather, moving to job switch");
                _pausedDuringGather = false;
                _currentState = QueueState.WaitingForJobSwitch;
                StateChanged?.Invoke(_currentState);
            }
        }
        else if (_currentState == QueueState.Crafting && CraftingGameInterop.CurrentState == CraftingGameInterop.CraftState.IdleNormal)
        {
            _currentState = QueueState.WaitingForJobSwitch;
            StateChanged?.Invoke(_currentState);
        }
    }

    public void Stop()
    {
        GatherBuddy.Log.Information("[CraftingQueueProcessor] Stopping queue");
        _paused = false;
        _tasks.Clear();
        _retainerBellNavigator?.Stop();
        _retainerBellNavigator = null;
        YesAlready.Unlock();
        
        GatherBuddy.AutoGather.Enabled = false;
        CraftingGatherBridge.DeleteTemporaryGatherList();
        
        CompleteQueue();
    }

    private void UpdateCurrentRecipeTracking(int batchSize = 1)
    {
        if (_currentQueueIndex >= _queue.Count)
            return;
        
        var currentRecipeId = _queue[_currentQueueIndex].RecipeId;
        if (_currentProcessedRecipeId != currentRecipeId)
        {
            _currentProcessedRecipeId = currentRecipeId;
            _currentProcessedRecipeCount = 1;
            _currentProcessedRecipeTotal = batchSize;
        }
        else
        {
            _currentProcessedRecipeCount++;
        }
    }
    
    public void Reset()
    {
        YesAlready.Unlock();
        _queue.Clear();
        _currentQueueIndex = 0;
        _currentState = QueueState.Idle;
        _tasks.Clear();
        _currentProcessedRecipeId = 0;
        _currentProcessedRecipeCount = 0;
        _currentProcessedRecipeTotal = 0;
        _craftHangSince = DateTime.MinValue;
        _enqueuedRaphaelRequests.Clear();
        _jobSwitchRequestedFor = 0u;
        _retainerRestock = false;
        _allMaterials = new();
        _retainerPrecraftItems = new();
        _expandedQueueForRetainer = new();
        _retainerPlanningList = null;
        _retainerExecutor = null;
        _retainerBellNavigator?.Stop();
        _retainerBellNavigator = null;
    }
    
    public void TestRepair()
    {
        GatherBuddy.Log.Information("[CraftingQueueProcessor] Testing repair system...");
        YesAlready.Lock();
        _currentState = QueueState.Repairing;
        StateChanged?.Invoke(_currentState);
        QueueRepairTasks();
    }
}
