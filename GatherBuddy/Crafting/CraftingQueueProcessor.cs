using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using GatherBuddy.Automation;
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
    private bool _paused = false;
    private bool _pausedDuringGather = false;
    private uint _currentProcessedRecipeId = 0;
    private int _currentProcessedRecipeCount = 0;
    private int _currentProcessedRecipeTotal = 0;
    private DateTime _craftHangSince = DateTime.MinValue;
    private bool _lastCraftWasQuickSynth = false;

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

    public void StartQueue(List<CraftingListItem> queue, CraftingListConsumableSettings? listConsumables = null, RaphaelSolveCoordinator? raphaelCoordinator = null, bool skipIfEnough = false)
    {
        _queue = new List<CraftingListItem>(queue);
        _currentQueueIndex = 0;
        _currentState = QueueState.WaitingForGather;
        _raphaelCoordinator = raphaelCoordinator;
        _listConsumables = listConsumables;
        _consumableDelayUntil = DateTime.MinValue;
        GatherBuddy.Log.Information($"[CraftingQueueProcessor] Starting queue with {_queue.Count} recipes");
        GatherBuddy.Log.Debug($"[CraftingQueueProcessor] RaphaelCoordinator is {(raphaelCoordinator != null ? "present" : "null")}");
        StateChanged?.Invoke(_currentState);
        
        var solverMode = GatherBuddy.Config.RaphaelSolverConfig.SolverMode;
        if (_raphaelCoordinator != null && solverMode == RaphaelSolverMode.PureRaphael)
        {
            GatherBuddy.Log.Debug($"[CraftingQueueProcessor] Building CraftStates to extract accurate stats for Raphael");
            EnqueueRaphaelSolvesFromCraftStates(_queue);
        }
        else if (solverMode == RaphaelSolverMode.StandardSolver)
        {
            GatherBuddy.Log.Debug($"[CraftingQueueProcessor] StandardSolver mode - skipping Raphael solve generation");
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
                if (CraftingGameInterop.CurrentState == CraftingGameInterop.CraftState.IdleNormal)
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
            if (_tasks.Count == 0)
            {
                GatherBuddy.Log.Information($"[CraftingQueueProcessor] Job switch needed: {requiredJob}");
                bool needExitCraft = CraftingGameInterop.CurrentState == CraftingGameInterop.CraftState.IdleBetween;
                
                if (needExitCraft)
                {
                    GatherBuddy.Log.Debug("[CraftingQueueProcessor] Queueing TaskExitCraft before job switch");
                    _tasks.Add(() => CraftingTasks.TaskExitCraft());
                }
                
                _tasks.Add(() => { SwitchJob(requiredJob); return CraftingTasks.TaskResult.Done; });
            }
            
            if (_tasks.Count == 0)
            {
                GatherBuddy.Log.Debug($"[CraftingQueueProcessor] Job switch complete");
                TransitionToRaphaelOrCraft();
            }
        }
        else
        {
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
        
        if (_currentQueueIndex >= _queue.Count)
        {
            _currentState = QueueState.ReadyForCraft;
            StateChanged?.Invoke(_currentState);
            return;
        }
        
        var recipeItem = _queue[_currentQueueIndex];
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
    }
    
    private bool IsRaphaelSolutionReady(uint recipeId)
    {
        if (_raphaelCoordinator == null)
            return false;
        
        var recipe = RecipeManager.GetRecipe(recipeId);
        if (recipe == null)
            return false;
        
        var requiredJob = (uint)(recipe.Value.CraftType.RowId + 8);
        var gearsetStats = GearsetStatsReader.ReadGearsetStatsForJob(requiredJob);
        if (gearsetStats == null)
            return false;

        var recipeItem = _queue.FirstOrDefault(r => r.RecipeId == recipeId);
        var consumableSettings = BuildConsumableSettings(recipeItem);
        if (consumableSettings != null)
            gearsetStats = GearsetStatsReader.ApplyConsumablesToStats(gearsetStats, consumableSettings);
        var ingredientPreferences = recipeItem?.IngredientPreferences;
        int initialQuality = ingredientPreferences != null && ingredientPreferences.Count > 0
            ? QualityCalculator.CalculateInitialQuality(recipe.Value, ingredientPreferences)
            : 0;
        
        var request = new RaphaelSolveRequest(
            RecipeId: recipeId,
            Level: gearsetStats.Level,
            Craftsmanship: gearsetStats.Craftsmanship,
            Control: gearsetStats.Control,
            CP: gearsetStats.CP,
            Manipulation: gearsetStats.Manipulation,
            Specialist: gearsetStats.Specialist,
            InitialQuality: initialQuality
        );
        
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
        {
            GatherBuddy.Log.Debug($"[CraftingQueueProcessor] Waiting for crafting to be idle. Current state: {CraftingGameInterop.CurrentState}");
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

        var hasCraftedBefore = QuestManager.IsRecipeComplete(recipe.Value.RowId);
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
        var gearsetStats = GearsetStatsReader.ReadGearsetStatsForJob(requiredJob);
        if (gearsetStats == null) return null;

        var recipeItem = _queue.FirstOrDefault(r => r.RecipeId == recipeId);
        var consumableSettings = BuildConsumableSettings(recipeItem);
        if (consumableSettings != null)
            gearsetStats = GearsetStatsReader.ApplyConsumablesToStats(gearsetStats, consumableSettings);

        var ingredientPreferences = recipeItem?.IngredientPreferences;
        int initialQuality = ingredientPreferences != null && ingredientPreferences.Count > 0
            ? QualityCalculator.CalculateInitialQuality(recipe.Value, ingredientPreferences)
            : 0;

        return new RaphaelSolveRequest(
            RecipeId: recipeId,
            Level: gearsetStats.Level,
            Craftsmanship: gearsetStats.Craftsmanship,
            Control: gearsetStats.Control,
            CP: gearsetStats.CP,
            Manipulation: gearsetStats.Manipulation,
            Specialist: gearsetStats.Specialist,
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
        GatherBuddy.AutoGather.Enabled = false;
        
        var craftState = CraftingGameInterop.CurrentState;
        GatherBuddy.Log.Debug($"[CraftingQueueProcessor] Current craft state during CompleteQueue: {craftState}");
        bool needExitCraft = craftState == CraftingGameInterop.CraftState.IdleBetween || 
                            craftState == CraftingGameInterop.CraftState.WaitFinish ||
                            craftState == CraftingGameInterop.CraftState.QuickSynthesis;
        if (needExitCraft)
        {
            GatherBuddy.Log.Debug($"[CraftingQueueProcessor] Queueing TaskExitCraft to close crafting log");
            _tasks.Add(() => CraftingTasks.TaskExitCraft());
        }
        else
        {
            GatherBuddy.Log.Debug($"[CraftingQueueProcessor] Not closing crafting log - state is {craftState}");
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

                var request = new RaphaelSolveRequest(
                    RecipeId: recipe.RowId,
                    Level: gearsetStats.Level,
                    Craftsmanship: gearsetStats.Craftsmanship,
                    Control: gearsetStats.Control,
                    CP: gearsetStats.CP,
                    Manipulation: gearsetStats.Manipulation,
                    Specialist: gearsetStats.Specialist,
                    InitialQuality: initialQuality
                );

                requests.Add(request);
                GatherBuddy.Log.Debug($"[CraftingQueueProcessor] Recipe {recipe.RowId} (Job {requiredJob}): Craft={gearsetStats.Craftsmanship}, Ctrl={gearsetStats.Control}, CP={gearsetStats.CP}, IQ={initialQuality}");
            }
            catch (Exception ex)
            {
                GatherBuddy.Log.Warning($"[CraftingQueueProcessor] Failed to read gearset stats for recipe {item.RecipeId}: {ex.Message}");
            }
        }

        if (requests.Count > 0)
        {
            GatherBuddy.Log.Debug($"[CraftingQueueProcessor] Enqueuing {requests.Count} requests with effective consumables");
            _raphaelCoordinator.EnqueueSolvesFromRequests(requests);
        }
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
            _tasks.Add(() => CraftingTasks.TaskCloseRepairWindow());
            _tasks.Add(() => { TransitionFromRepairComplete(); return CraftingTasks.TaskResult.Done; });
            return;
        }

        GatherBuddy.Log.Error("[CraftingQueueProcessor] Cannot repair: no dark matter and no repair NPC available");
        _tasks.Add(() => { CompleteQueue(); return CraftingTasks.TaskResult.Abort; });
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
        _tasks.Clear();
        
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
        _queue.Clear();
        _currentQueueIndex = 0;
        _currentState = QueueState.Idle;
        _tasks.Clear();
        _currentProcessedRecipeId = 0;
        _currentProcessedRecipeCount = 0;
        _currentProcessedRecipeTotal = 0;
        _craftHangSince = DateTime.MinValue;
    }
    
    public void TestRepair()
    {
        GatherBuddy.Log.Information("[CraftingQueueProcessor] Testing repair system...");
        _currentState = QueueState.Repairing;
        StateChanged?.Invoke(_currentState);
        QueueRepairTasks();
    }
}
