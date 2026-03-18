using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GatherBuddy.Automation;
using GatherBuddy.Vulcan;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;

namespace GatherBuddy.Crafting;

public static class CraftingGameInterop
{
    public enum CraftState
    {
        IdleNormal,
        PreparingCraft,
        WaitStart,
        InProgress,
        WaitAction,
        WaitFinish,
        IdleBetween,
        QuickSynthesis,
        InvalidState
    }

    private static CraftState _currentState = CraftState.IdleNormal;
    private static Recipe? _currentRecipe = null;
    private static uint? _currentRecipeId = null;
    private static Vulcan.CraftState? _vulcanCraftState = null;
    private static Vulcan.StepState? _vulcanStepState = null;
    private static CraftingActionExecutor? _actionExecutor = null;
    private static Dictionary<uint, int>? _currentIngredientPreferences = null;
    private static bool _currentUseAllNQ = false;
    private static int _quickSynthTarget = 0;
    private static int _quickSynthCompleted = 0;
    private static bool _quickSynthWindowSeen = false;
    private static Dictionary<uint, bool> _equipmentItemCache = new();
    private static Vulcan.UserMacroLibrary? _userMacroLibrary = null;
    private static string? _currentSelectedMacroId = null;
    private static DateTime _taskManagerIdleSince = DateTime.MinValue;
    private static DateTime _nextActionAllowedAt = DateTime.MinValue;

    public static Vulcan.UserMacroLibrary UserMacroLibrary => _userMacroLibrary ??= new();
    public static event Action<CraftState>? StateChanged;
    public static event Action<Recipe?, uint>? CraftStarted;
    public static event Action<Recipe?, bool>? CraftFinished;
    public static event Action<Recipe?>? CraftAdvanced;
    public static event Action<int, int>? QuickSynthProgress;

    public static CraftState CurrentState => _currentState;
    public static Recipe? CurrentRecipe => _currentRecipe;

    public static void Initialize()
    {
        _currentState = CraftState.IdleNormal;
        _actionExecutor = new CraftingActionExecutor();
        _userMacroLibrary = new Vulcan.UserMacroLibrary();
        _userMacroLibrary.LoadFromConfig();
        CraftingProcessor.Setup();
        
        // Register UserMacro solver first (highest priority)
        CraftingProcessor.RegisterSolver(new Vulcan.UserMacroSolverDefinition(_userMacroLibrary));
        GatherBuddy.Log.Debug($"[CraftingGameInterop] Registered UserMacro solver");
        
        var solverMode = GatherBuddy.Config.RaphaelSolverConfig.SolverMode;
        switch (solverMode)
        {
            case RaphaelSolverMode.PureRaphael:
                CraftingProcessor.RegisterSolver(new Vulcan.RaphaelSolverDefinition(GatherBuddy.RaphaelSolveCoordinator));
                GatherBuddy.Log.Debug($"[CraftingGameInterop] Registered Raphael solver");
                break;
            case RaphaelSolverMode.StandardSolver:
                CraftingProcessor.RegisterSolver(new Vulcan.StandardSolverDefinition());
                GatherBuddy.Log.Debug($"[CraftingGameInterop] Registered StandardSolver");
                break;
        }
    }

    public static void ReloadSolvers()
    {
        CraftingProcessor.Setup();
        
        // Re-register UserMacro solver first (highest priority)
        if (_userMacroLibrary != null)
        {
            CraftingProcessor.RegisterSolver(new Vulcan.UserMacroSolverDefinition(_userMacroLibrary));
            GatherBuddy.Log.Debug($"[CraftingGameInterop] Reloaded: Registered UserMacro solver");
        }
        
        var solverMode = GatherBuddy.Config.RaphaelSolverConfig.SolverMode;
        switch (solverMode)
        {
            case RaphaelSolverMode.PureRaphael:
                CraftingProcessor.RegisterSolver(new Vulcan.RaphaelSolverDefinition(GatherBuddy.RaphaelSolveCoordinator));
                GatherBuddy.Log.Debug($"[CraftingGameInterop] Reloaded: Registered Raphael solver");
                break;
            case RaphaelSolverMode.StandardSolver:
                CraftingProcessor.RegisterSolver(new Vulcan.StandardSolverDefinition());
                GatherBuddy.Log.Debug($"[CraftingGameInterop] Reloaded: Registered StandardSolver");
                break;
        }
    }

    public static void ReloadSolversForCraft(RaphaelSolverMode mode)
    {
        GatherBuddy.Log.Debug($"[CraftingGameInterop] ReloadSolversForCraft: {mode}");
        CraftingProcessor.Setup();

        if (_userMacroLibrary != null)
        {
            CraftingProcessor.RegisterSolver(new Vulcan.UserMacroSolverDefinition(_userMacroLibrary));
            GatherBuddy.Log.Debug($"[CraftingGameInterop] Registered UserMacro solver");
        }

        switch (mode)
        {
            case RaphaelSolverMode.PureRaphael:
                CraftingProcessor.RegisterSolver(new Vulcan.RaphaelSolverDefinition(GatherBuddy.RaphaelSolveCoordinator));
                GatherBuddy.Log.Debug($"[CraftingGameInterop] Registered Raphael solver");
                break;
            case RaphaelSolverMode.StandardSolver:
                CraftingProcessor.RegisterSolver(new Vulcan.StandardSolverDefinition());
                GatherBuddy.Log.Debug($"[CraftingGameInterop] Registered StandardSolver");
                break;
        }
    }

    public static void Dispose()
    {
        _currentRecipe = null;
        _currentRecipeId = null;
        _currentState = CraftState.IdleNormal;
        _vulcanCraftState = null;
        _vulcanStepState = null;
        _currentSelectedMacroId = null;
        CraftingProcessor.Dispose();
    }

    public static void SetIngredientPreferences(Dictionary<uint, int>? preferences, bool useAllNQ = false)
    {
        _currentIngredientPreferences = preferences;
        _currentUseAllNQ = useAllNQ;
    }
    
    public static void SetSelectedMacro(string? macroId)
    {
        _currentSelectedMacroId = macroId;
    }
    
    public static string? GetSelectedMacro()
    {
        return _currentSelectedMacroId;
    }
    
    public static void StartCraft(Recipe recipe, uint quantity, bool useQuickSynthesis = false)
    {
        if (recipe.RowId == 0)
            return;

        _currentRecipe = recipe;
        _currentRecipeId = recipe.RowId;
        _currentState = CraftState.PreparingCraft;
        _taskManagerIdleSince = DateTime.MinValue;
        GatherBuddy.Log.Debug($"[Crafting] StartCraft - entering PreparingCraft state (QuickSynth={useQuickSynthesis})");
        
        var tm = GatherBuddy.AutoGather?.TaskManager;
        if (tm == null)
            return;
        
        tm.Enqueue(() => OpenRecipe(recipe.RowId), 3000, "OpenRecipe");
        tm.Enqueue(() => WaitForRecipeOpen(), 3000, true, "WaitForRecipeOpen");
        
        if (useQuickSynthesis)
        {
            tm.DelayNext(500);
            tm.Enqueue(() => { ExecuteQuickSynthesis((int)quantity); return true; }, 3000, "ExecuteQuickSynthesis");
        }
        else
        {
            tm.DelayNext(1500);
            tm.Enqueue(() => WaitForIngredientsAssigned(), 3000, "WaitForIngredientsAssigned");
            tm.Enqueue(() => ExecuteCraft(), 3000, "ExecuteCraft");
        }
        
        GatherBuddy.Log.Information($"[Crafting] Starting craft of {recipe.ItemResult.Value.Name.ExtractText()} (qty: {quantity}, QuickSynth={useQuickSynthesis})");
    }

    private static unsafe bool OpenRecipe(uint recipeId)
    {
        try
        {
            var recipeNote = FFXIVClientStructs.FFXIV.Client.Game.UI.RecipeNote.Instance();
            if (recipeNote != null && recipeNote->RecipeList != null)
            {
                var selectedRecipe = recipeNote->RecipeList->SelectedRecipe;
                if (selectedRecipe != null && selectedRecipe->RecipeId == recipeId)
                {
                    GatherBuddy.Log.Debug($"[Crafting] Recipe {recipeId} already selected, skipping OpenRecipe");
                    return true;
                }
            }
            
            var agent = AgentRecipeNote.Instance();
            if (agent == null)
                return false;
            
            agent->OpenRecipeByRecipeId(recipeId);
            GatherBuddy.Log.Debug($"[Crafting] Opened recipe {recipeId}");
            return true;
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[Crafting] Failed to open recipe: {ex.Message}");
            return false;
        }
    }

    private static unsafe bool WaitForRecipeOpen()
    {
        try
        {
            var addon = Dalamud.GameGui.GetAddonByName("RecipeNote");
            if (addon != null && addon.Address != nint.Zero)
            {
                var atkUnit = (AtkUnitBase*)addon.Address;
                if (atkUnit != null && atkUnit->IsVisible)
                {
                    GatherBuddy.Log.Debug($"[Crafting] Recipe window opened");
                    return true;
                }
            }
        }
        catch { }
        
        return false;
    }

    private static unsafe bool WaitForIngredientsAssigned()
    {
        try
        {
            var addon = Dalamud.GameGui.GetAddonByName("RecipeNote");
            if (addon == null || addon.Address == nint.Zero)
            {
                GatherBuddy.Log.Debug("[Crafting] WaitForIngredientsAssigned: RecipeNote not found, re-opening");
                if (_currentRecipeId.HasValue)
                    OpenRecipe(_currentRecipeId.Value);
                return false;
            }

            var atkUnit = (AtkUnitBase*)addon.Address;
            if (atkUnit == null || !atkUnit->IsVisible)
            {
                GatherBuddy.Log.Debug("[Crafting] WaitForIngredientsAssigned: RecipeNote not visible, re-opening");
                if (_currentRecipeId.HasValue)
                    OpenRecipe(_currentRecipeId.Value);
                return false;
            }

            SelectIngredientsForCraft();
            GatherBuddy.Log.Debug($"[Crafting] Ingredients assigned, ready to craft");
            return true;
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[Crafting] Failed to validate ingredients: {ex.Message}");
            return false;
        }
    }

    private static unsafe void SelectIngredientsForCraft()
    {
        try
        {
            GatherBuddy.Log.Debug($"[Crafting] SelectIngredientsForCraft called");
            var addon = Dalamud.GameGui.GetAddonByName("RecipeNote");
            if (addon == null || addon.Address == nint.Zero)
            {
                GatherBuddy.Log.Debug($"[Crafting] RecipeNote addon not found");
                return;
            }

            var atkUnit = (AtkUnitBase*)addon.Address;
            var recipeNote = FFXIVClientStructs.FFXIV.Client.Game.UI.RecipeNote.Instance();
            if (recipeNote == null || recipeNote->RecipeList == null)
            {
                GatherBuddy.Log.Debug($"[Crafting] RecipeNote or RecipeList is null");
                return;
            }

            var recipeData = recipeNote->RecipeList;
            var selectedRecipe = recipeData->SelectedRecipe;
            if (selectedRecipe == null)
            {
                GatherBuddy.Log.Debug($"[Crafting] SelectedRecipe is null");
                return;
            }

            GatherBuddy.Log.Debug($"[Crafting] Processing ingredients...");
            var ingredients = RecipeNoteExt.GetIngredientsSpan(selectedRecipe);
            for (int i = 0; i < ingredients.Length; i++)
            {
                var ingredient = ingredients[i];
                if (ingredient.ItemId == 0)
                {
                    GatherBuddy.Log.Debug($"[Crafting] Ingredient {i}: ItemId is 0, stopping");
                    break;
                }

                if (ingredient.NumTotal == 0)
                {
                    GatherBuddy.Log.Debug($"[Crafting] Ingredient {i}: NumTotal is 0, skipping");
                    continue;
                }

                GatherBuddy.Log.Debug($"[Crafting] Ingredient {i}: ItemId={ingredient.ItemId}, NQ avail={ingredient.NumAvailableNQ}, HQ avail={ingredient.NumAvailableHQ}, needed={ingredient.NumTotal}");

                if (IsEquipmentIngredient(ingredient.ItemId))
                {
                    GatherBuddy.Log.Debug($"[Crafting] Ingredient {i} is equipment, using equipment selection");
                    
                    bool preferHQ = false;
                    if (_currentIngredientPreferences != null && _currentIngredientPreferences.TryGetValue(ingredient.ItemId, out var preferredHQ))
                    {
                        preferHQ = preferredHQ > 0;
                        GatherBuddy.Log.Debug($"[Crafting] User preference: preferHQ={preferHQ}");
                    }
                    else if (!_currentUseAllNQ && ingredient.NumAvailableHQ > 0)
                    {
                        preferHQ = true;
                        GatherBuddy.Log.Debug($"[Crafting] No preference, HQ-first default");
                    }
                    
                    if (!SelectEquipmentIngredient(atkUnit, (uint)i, ingredient.ItemId, preferHQ))
                    {
                        if (!preferHQ && ingredient.NumAvailableHQ > 0)
                        {
                            GatherBuddy.Log.Warning($"[Crafting] NQ selection failed, trying HQ as fallback");
                            SelectEquipmentIngredient(atkUnit, (uint)i, ingredient.ItemId, true);
                        }
                        else if (preferHQ && ingredient.NumAvailableNQ > 0)
                        {
                            GatherBuddy.Log.Warning($"[Crafting] HQ selection failed, trying NQ");
                            SelectEquipmentIngredient(atkUnit, (uint)i, ingredient.ItemId, false);
                        }
                    }
                    
                    System.Threading.Thread.Sleep(150);
                    continue;
                }

                int desiredHQ = 0;
                if (_currentIngredientPreferences != null && _currentIngredientPreferences.TryGetValue(ingredient.ItemId, out var preferredHQ2))
                {
                    desiredHQ = Math.Min(preferredHQ2, Math.Min(ingredient.NumTotal, ingredient.NumAvailableHQ));
                    var nqShortfall = Math.Max(0, (ingredient.NumTotal - desiredHQ) - ingredient.NumAvailableNQ);
                    desiredHQ = Math.Min(ingredient.NumAvailableHQ, desiredHQ + nqShortfall);
                    GatherBuddy.Log.Debug($"[Crafting] Using preference: {desiredHQ} HQ for item {ingredient.ItemId}");
                }
                else if (_currentUseAllNQ)
                {
                    desiredHQ = Math.Max(0, ingredient.NumTotal - ingredient.NumAvailableNQ);
                    desiredHQ = Math.Min(desiredHQ, ingredient.NumAvailableHQ);
                    GatherBuddy.Log.Debug($"[Crafting] Prefer NQ: {desiredHQ} HQ for item {ingredient.ItemId} (need={ingredient.NumTotal}, NQ avail={ingredient.NumAvailableNQ})");
                }
                else
                {
                    desiredHQ = Math.Min(ingredient.NumTotal, ingredient.NumAvailableHQ);
                    GatherBuddy.Log.Debug($"[Crafting] HQ-first default: {desiredHQ} HQ for item {ingredient.ItemId}");
                }
                
                if (desiredHQ > 0)
                {
                    GatherBuddy.Log.Debug($"[Crafting] Clicking HQ for ingredient {i}, {desiredHQ} times");
                    for (int m = 0; m < desiredHQ; m++)
                    {
                        ClickMaterial(atkUnit, (uint)i, true);
                    }
                }
                
                int desiredNQ = ingredient.NumTotal - desiredHQ;
                if (desiredNQ > 0 && ingredient.NumAvailableNQ >= desiredNQ)
                {
                    GatherBuddy.Log.Debug($"[Crafting] Clicking NQ for ingredient {i}, {desiredNQ} times");
                    for (int m = 0; m < desiredNQ; m++)
                    {
                        ClickMaterial(atkUnit, (uint)i, false);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Debug($"[Crafting] Error selecting ingredients: {ex.Message}\n{ex.StackTrace}");
        }
    }

    private static unsafe void ClickMaterial(AtkUnitBase* recipeNoteUnit, uint index, bool hq)
    {
        try
        {
            uint callbackIndex = index;
            if (hq)
                callbackIndex += 0x10_000;
            
            GatherBuddy.Log.Debug($"[Crafting] Firing Material callback: index={callbackIndex}, hq={hq}");
            Callback.Fire(recipeNoteUnit, false, 6, callbackIndex, 0);
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Debug($"[Crafting] Error clicking material: {ex.Message}");
        }
    }
    
    private static bool IsEquipmentIngredient(uint itemId)
    {
        if (_equipmentItemCache.TryGetValue(itemId, out var cached))
            return cached;
        
        try
        {
            var itemSheet = Dalamud.GameData.GetExcelSheet<Item>();
            if (itemSheet == null)
                return false;
            
            if (!itemSheet.TryGetRow(itemId, out var item))
                return false;
            
            bool isEquipment = item.EquipSlotCategory.RowId > 0;
            _equipmentItemCache[itemId] = isEquipment;
            return isEquipment;
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Debug($"[Crafting] Error checking if item {itemId} is equipment: {ex.Message}");
            return false;
        }
    }
    
    private static unsafe bool SelectEquipmentIngredient(AtkUnitBase* recipeNoteUnit, uint index, uint itemId, bool preferHQ)
    {
        try
        {
            GatherBuddy.Log.Debug($"[Crafting] Selecting equipment ingredient at index {index}, itemId={itemId}, preferHQ={preferHQ}");
            
            var componentNode = recipeNoteUnit->GetComponentNodeById(89 + index);
            if (componentNode == null || !componentNode->AtkResNode.IsVisible())
            {
                GatherBuddy.Log.Warning($"[Crafting] Ingredient node {index} not found or not visible");
                return false;
            }
            
            if (componentNode->Component == null)
            {
                GatherBuddy.Log.Warning($"[Crafting] Component is null for ingredient {index}");
                return false;
            }
            
            var selectionButton = componentNode->Component->GetNodeById(7);
            if (selectionButton == null || !selectionButton->IsVisible())
            {
                GatherBuddy.Log.Debug($"[Crafting] Selection button not visible for ingredient {index}, using normal click");
                return false;
            }
            
            var clickButtonNode = componentNode->Component->GetNodeById(5);
            if (clickButtonNode == null)
            {
                GatherBuddy.Log.Warning($"[Crafting] Click button node not found for ingredient {index}");
                return false;
            }
            
            var clickButton = clickButtonNode->GetAsAtkComponentButton();
            if (clickButton == null)
            {
                GatherBuddy.Log.Warning($"[Crafting] Click button not found for ingredient {index}");
                return false;
            }
            
            GatherBuddy.Log.Debug($"[Crafting] Opening context menu for equipment selection");
            
            var buttonClickEvent = stackalloc AtkEvent[1];
            var eventData = (AtkEventData*)clickButtonNode;
            recipeNoteUnit->ReceiveEvent(AtkEventType.ButtonClick, 5, buttonClickEvent, eventData);
            
            System.Threading.Thread.Sleep(150);
            
            var contextMenuAddon = Dalamud.GameGui.GetAddonByName("ContextIconMenu");
            if (contextMenuAddon.Address == nint.Zero)
            {
                GatherBuddy.Log.Warning($"[Crafting] ContextIconMenu addon not found after button click");
                return false;
            }
            
            var contextMenu = (AtkUnitBase*)contextMenuAddon.Address;
            if (contextMenu == null || !contextMenu->IsVisible)
            {
                GatherBuddy.Log.Warning($"[Crafting] ContextIconMenu not visible after button click");
                return false;
            }
            
            uint selectItemId = itemId;
            if (preferHQ)
            {
                selectItemId += 1_000_000;
            }
            
            GatherBuddy.Log.Debug($"[Crafting] Firing context menu callback for itemId={selectItemId}");
            Callback.Fire(contextMenu, true, 0, 0, 0, selectItemId, 0);
            
            System.Threading.Thread.Sleep(50);
            
            return true;
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[Crafting] Error selecting equipment ingredient: {ex.Message}\n{ex.StackTrace}");
            return false;
        }
    }
    
    public static void DebugClickRecipeNote(uint ingredientIndex, int clickCount, bool isHQ, bool autoOpen = false, uint recipeId = 0)
    {
        _ = DebugClickRecipeNoteAsync(ingredientIndex, clickCount, isHQ, autoOpen, recipeId);
    }
    
    private static async System.Threading.Tasks.Task DebugClickRecipeNoteAsync(uint ingredientIndex, int clickCount, bool isHQ, bool autoOpen, uint recipeId)
    {
        try
        {
            if (autoOpen)
            {
                GatherBuddy.Log.Information(recipeId > 0 
                    ? $"[Debug] Opening Recipe Note for recipe {recipeId}..." 
                    : "[Debug] Opening Recipe Note...");
                    
                if (!OpenRecipeNoteUI(recipeId))
                {
                    GatherBuddy.Log.Warning("[Debug] Failed to open Recipe Note");
                    return;
                }
                
                GatherBuddy.Log.Information("[Debug] Waiting for Recipe Note to open...");
                for (int i = 0; i < 50; i++) // 5 second timeout
                {
                    var (isOpen, _, _, _, _, _, _) = GetIngredientState(ingredientIndex);
                    if (isOpen)
                    {
                        GatherBuddy.Log.Information("[Debug] Recipe Note opened successfully");
                        await System.Threading.Tasks.Task.Delay(200);
                        break;
                    }
                    await System.Threading.Tasks.Task.Delay(100);
                }
            }
            
            var (valid, beforeNQ, beforeHQ, itemId, availNQ, availHQ, needed) = GetIngredientState(ingredientIndex);
            if (!valid) return;
            
            GatherBuddy.Log.Information($"[Debug] Testing clicks on ingredient {ingredientIndex}: ItemId={itemId}, " +
                $"NQ avail={availNQ}, HQ avail={availHQ}, needed={needed}, clicking {clickCount} times (HQ={isHQ})");
            GatherBuddy.Log.Information($"[Debug] Before: Assigned NQ={beforeNQ}, Assigned HQ={beforeHQ}");
            
            for (int i = 0; i < clickCount; i++)
            {
                ClickMaterialSafe(ingredientIndex, isHQ);
                await System.Threading.Tasks.Task.Delay(50); 
            }
            
            await System.Threading.Tasks.Task.Delay(200);
            
            var (validAfter, afterNQ, afterHQ, _, _, _, _) = GetIngredientState(ingredientIndex);
            if (!validAfter) return;
            
            GatherBuddy.Log.Information($"[Debug] After: Assigned NQ={afterNQ}, Assigned HQ={afterHQ}");
            var actualClicks = isHQ ? (afterHQ - beforeHQ) : (afterNQ - beforeNQ);
            GatherBuddy.Log.Information($"[Debug] Result: Requested {clickCount} clicks, {actualClicks} were registered (success rate: {actualClicks * 100.0 / clickCount:F1}%)");
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[Debug] Error testing Recipe Note clicks: {ex.Message}\n{ex.StackTrace}");
        }
    }
    
    private static unsafe (bool valid, int assignedNQ, int assignedHQ, uint itemId, int availNQ, int availHQ, int needed) GetIngredientState(uint ingredientIndex)
    {
        var addon = Dalamud.GameGui.GetAddonByName("RecipeNote");
        if (addon == null || addon.Address == nint.Zero)
        {
            GatherBuddy.Log.Warning("[Debug] RecipeNote window not open");
            return (false, 0, 0, 0, 0, 0, 0);
        }
        
        var atkUnit = (AtkUnitBase*)addon.Address;
        if (atkUnit == null || !atkUnit->IsVisible)
        {
            GatherBuddy.Log.Warning("[Debug] RecipeNote window not visible");
            return (false, 0, 0, 0, 0, 0, 0);
        }
        
        var recipeNote = FFXIVClientStructs.FFXIV.Client.Game.UI.RecipeNote.Instance();
        if (recipeNote == null || recipeNote->RecipeList == null)
        {
            GatherBuddy.Log.Warning("[Debug] RecipeNote data not available");
            return (false, 0, 0, 0, 0, 0, 0);
        }
        
        var selectedRecipe = recipeNote->RecipeList->SelectedRecipe;
        if (selectedRecipe == null)
        {
            GatherBuddy.Log.Warning("[Debug] No recipe selected");
            return (false, 0, 0, 0, 0, 0, 0);
        }
        
        var ingredients = RecipeNoteExt.GetIngredientsSpan(selectedRecipe);
        if (ingredientIndex >= ingredients.Length)
        {
            GatherBuddy.Log.Warning($"[Debug] Ingredient index {ingredientIndex} out of range (recipe has {ingredients.Length} ingredients)");
            return (false, 0, 0, 0, 0, 0, 0);
        }
        
        var ingredient = ingredients[(int)ingredientIndex];
        return (true, ingredient.NumAssignedNQ, ingredient.NumAssignedHQ, ingredient.ItemId, 
                ingredient.NumAvailableNQ, ingredient.NumAvailableHQ, ingredient.NumTotal);
    }
    
    private static unsafe void ClickMaterialSafe(uint ingredientIndex, bool isHQ)
    {
        var addon = Dalamud.GameGui.GetAddonByName("RecipeNote");
        if (addon == null || addon.Address == nint.Zero) return;
        
        var atkUnit = (AtkUnitBase*)addon.Address;
        if (atkUnit == null) return;
        
        ClickMaterial(atkUnit, ingredientIndex, isHQ);
    }
    
    private static unsafe bool OpenRecipeNoteUI(uint recipeId = 0)
    {
        try
        {
            var addon = Dalamud.GameGui.GetAddonByName("RecipeNote");
            if (addon != null && addon.Address != nint.Zero)
            {
                var atkUnit = (AtkUnitBase*)addon.Address;
                if (atkUnit != null && atkUnit->IsVisible)
                {
                    if (recipeId > 0)
                    {
                        GatherBuddy.Log.Debug($"[Debug] Recipe Note already open, switching to recipe {recipeId}");
                    }
                    else
                    {
                        GatherBuddy.Log.Debug("[Debug] Recipe Note already open");
                        return true;
                    }
                }
            }
            
            var agent = AgentRecipeNote.Instance();
            if (agent == null)
            {
                GatherBuddy.Log.Warning("[Debug] AgentRecipeNote not available");
                return false;
            }
            
            agent->OpenRecipeByRecipeId(recipeId);
            return true;
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[Debug] Failed to open Recipe Note: {ex.Message}");
            return false;
        }
    }

    private static unsafe bool ExecuteCraft()
    {
        try
        {
            var addon = Dalamud.GameGui.GetAddonByName("RecipeNote");
            if (addon == null || addon.Address == nint.Zero)
                return false;

            var atkUnit = (AtkUnitBase*)addon.Address;
            if (atkUnit == null || !atkUnit->IsVisible)
                return false;

            GatherBuddy.Log.Debug($"[Crafting] Executing craft action");
            Callback.Fire(atkUnit, true, 8);
            GatherBuddy.Log.Information($"[Crafting] Craft started");
            return true;
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[Crafting] Failed to execute craft: {ex.Message}");
            return false;
        }
    }
    
    public static unsafe void ExecuteQuickSynthesis(int quantity)
    {
        try
        {
            var recipeNoteAddon = Dalamud.GameGui.GetAddonByName("RecipeNote");
            if (recipeNoteAddon == null || recipeNoteAddon.Address == nint.Zero)
            {
                GatherBuddy.Log.Warning("[Crafting] RecipeNote not open for quick synthesis");
                return;
            }

            var atkUnit = (AtkUnitBase*)recipeNoteAddon.Address;
            if (atkUnit == null || !atkUnit->IsVisible)
            {
                GatherBuddy.Log.Warning("[Crafting] RecipeNote not visible for quick synthesis");
                return;
            }

            _quickSynthTarget = quantity;
            _quickSynthCompleted = 0;
            _quickSynthWindowSeen = false;
            
            GatherBuddy.Log.Debug($"[Crafting] Opening quick synthesis dialog");
            Callback.Fire(atkUnit, true, 9);
            
            var tm = GatherBuddy.AutoGather?.TaskManager;
            if (tm == null)
                return;
                
            tm.DelayNext(200);
            tm.Enqueue(() => ConfirmQuickSynthesis(quantity), 3000, "ConfirmQuickSynthesis");
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[Crafting] Failed to execute quick synthesis: {ex.Message}");
        }
    }
    
    private static unsafe bool ConfirmQuickSynthesis(int quantity)
    {
        try
        {
            var quickSynthDialogAddon = Dalamud.GameGui.GetAddonByName("SynthesisSimpleDialog");
            if (quickSynthDialogAddon == null || quickSynthDialogAddon.Address == nint.Zero)
            {
                GatherBuddy.Log.Debug("[Crafting] Quick synthesis dialog not open yet");
                return false;
            }

            var dialogUnit = (AtkUnitBase*)quickSynthDialogAddon.Address;
            if (dialogUnit == null || !dialogUnit->IsVisible)
            {
                GatherBuddy.Log.Debug("[Crafting] Quick synthesis dialog not visible");
                return false;
            }

            var clampedQuantity = Math.Min(quantity, 99);
            GatherBuddy.Log.Information($"[Crafting] Confirming quick synthesis for {clampedQuantity} items");
            
            var values = stackalloc AtkValue[3];
            values[0] = new()
            {
                Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int,
                Int = clampedQuantity,
            };
            values[1] = new()
            {
                Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Bool,
                Byte = 1,
            };
            values[2] = new()
            {
                Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Bool,
                Byte = 1
            };
            Callback.Fire(dialogUnit, true, values[0], values[1], values[2]);
            
            _currentState = CraftState.QuickSynthesis;
            StateChanged?.Invoke(_currentState);
            
            return true;
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[Crafting] Failed to confirm quick synthesis: {ex.Message}");
            return false;
        }
    }

    private static async void ExecuteSolverRecommendation(Vulcan.CraftState craft, Vulcan.StepState step, Solver.Recommendation recommendation)
    {
        if (_actionExecutor == null || recommendation.Action == VulcanSkill.None)
            return;

        try
        {
            var canExecute = _actionExecutor.CanExecuteAction(recommendation.Action, craft, step);
            if (canExecute)
            {
                var success = await _actionExecutor.TryExecuteActionAsync(recommendation.Action);
                if (success)
                {
                    var (result, nextStep) = Vulcan.Simulator.Execute(craft, step, recommendation.Action, 0.5f, 0.5f);
                    if (result == Vulcan.Simulator.ExecuteResult.Succeeded || result == Vulcan.Simulator.ExecuteResult.Failed)
                    {
                        _vulcanStepState = nextStep;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[Crafting] Error executing solver action: {ex.Message}");
        }
    }

    private static void CheckGatherToCraftTransition()
    {
        if (!CraftingGatherBridge.WaitingForGatherComplete)
            return;
        
        if (!GatherBuddy.AutoGather.Enabled && CraftingGatherBridge.IsGatheringComplete())
        {
            CraftingGatherBridge.OnGatherComplete();
        }
    }
    
    private static DateTime _lastJobSwitchAttempt = DateTime.MinValue;
    
    private static void RetryGatherToCraftAfterJobSwitch()
    {
        var timeSinceAttempt = (DateTime.Now - _lastJobSwitchAttempt).TotalSeconds;
        if (timeSinceAttempt < 2)
            return;
        
        _lastJobSwitchAttempt = DateTime.MinValue;
        CraftingGatherBridge.OnGatherComplete();
    }
    

    public static void Update()
    {
        CheckGatherToCraftTransition();
        
        if (_currentState != CraftState.IdleNormal)
            GatherBuddy.Log.Verbose($"[Crafting] Update: state={_currentState}, Crafting={Dalamud.Conditions[ConditionFlag.Crafting]}, ExecutingAction={Dalamud.Conditions[ConditionFlag.ExecutingCraftingAction]}");
        
        var newState = _currentState switch
        {
            CraftState.IdleNormal => TransitionFromIdleNormal(),
            CraftState.PreparingCraft => TransitionFromPreparingCraft(),
            CraftState.WaitStart => TransitionFromWaitStart(),
            CraftState.InProgress => TransitionFromInProgress(),
            CraftState.WaitAction => TransitionFromWaitAction(),
            CraftState.WaitFinish => TransitionFromWaitFinish(),
            CraftState.IdleBetween => TransitionFromIdleBetween(),
            CraftState.QuickSynthesis => TransitionFromQuickSynthesis(),
            _ => TransitionFromInvalid()
        };

        if (newState != _currentState)
        {
            GatherBuddy.Log.Debug($"[Crafting] State transition: {_currentState} -> {newState}");
            _currentState = newState;
            StateChanged?.Invoke(newState);
        }
    }

    private static CraftState TransitionFromIdleNormal()
    {
        if (Dalamud.Conditions[ConditionFlag.Crafting])
            return CraftState.IdleBetween;

        if (Dalamud.Conditions[ConditionFlag.ExecutingCraftingAction])
            return CraftState.WaitStart;

        return CraftState.IdleNormal;
    }

    private static CraftState TransitionFromPreparingCraft()
    {
        if (Dalamud.Conditions[ConditionFlag.ExecutingCraftingAction])
        {
            GatherBuddy.Log.Debug($"[Crafting] Craft action executing, transitioning to WaitStart");
            _taskManagerIdleSince = DateTime.MinValue;
            return CraftState.WaitStart;
        }

        if (Dalamud.Conditions[ConditionFlag.Crafting])
        {
            GatherBuddy.Log.Debug($"[Crafting] Crafting flag set, ready to craft");
            _taskManagerIdleSince = DateTime.MinValue;
            return CraftState.IdleBetween;
        }

        var tm = GatherBuddy.AutoGather?.TaskManager;
        if (tm != null && !tm.IsBusy)
        {
            if (_taskManagerIdleSince == DateTime.MinValue)
                _taskManagerIdleSince = DateTime.Now;

            if ((DateTime.Now - _taskManagerIdleSince).TotalSeconds > 1.0)
            {
                GatherBuddy.Log.Warning("[Crafting] PreparingCraft: craft tasks completed but no crafting conditions after 1s, resetting to IdleNormal");
                _taskManagerIdleSince = DateTime.MinValue;
                return CraftState.IdleNormal;
            }
        }
        else
        {
            _taskManagerIdleSince = DateTime.MinValue;
        }

        return CraftState.PreparingCraft;
    }

    private static unsafe CraftState TransitionFromQuickSynthesis()
    {
        var quickSynthAddon = Dalamud.GameGui.GetAddonByName("SynthesisSimple");
        
        if (quickSynthAddon != null && quickSynthAddon.Address != nint.Zero)
        {
            var atkUnit = (AtkUnitBase*)quickSynthAddon.Address;
            if (atkUnit != null && atkUnit->IsVisible && atkUnit->AtkValuesCount >= 5)
            {
                _quickSynthWindowSeen = true;
                
                var current = atkUnit->AtkValues[3].Int;
                var max = atkUnit->AtkValues[4].Int;
                
                if (_quickSynthCompleted != current)
                {
                    _quickSynthCompleted = current;
                    GatherBuddy.Log.Debug($"[Crafting] Quick synthesis progress: {current}/{max}");
                    QuickSynthProgress?.Invoke(current, max);
                }
                
                if (current >= max && max > 0)
                {
                    GatherBuddy.Log.Debug($"[Crafting] Quick synthesis complete ({current}/{max}), closing window");
                    Callback.Fire(atkUnit, true, -1);
                    return CraftState.QuickSynthesis;
                }
                
                return CraftState.QuickSynthesis;
            }
        }
        
        if (!_quickSynthWindowSeen)
        {
            return CraftState.QuickSynthesis;
        }
        
        if (Dalamud.Conditions[ConditionFlag.PreparingToCraft])
        {
            GatherBuddy.Log.Debug("[Crafting] Quick synthesis complete, back in crafting menu");
            var finishedRecipe = _currentRecipe;
            _quickSynthTarget = 0;
            _quickSynthCompleted = 0;
            _quickSynthWindowSeen = false;
            _currentRecipe = null;
            _currentRecipeId = null;
            CraftFinished?.Invoke(finishedRecipe, false);
            return CraftState.IdleBetween;
        }
        
        return CraftState.QuickSynthesis;
    }
    
    private static CraftState TransitionFromIdleBetween()
    {
        var preparingFlag = Dalamud.Conditions[ConditionFlag.PreparingToCraft];
        var craftingFlag = Dalamud.Conditions[ConditionFlag.Crafting];
        var executingFlag = Dalamud.Conditions[ConditionFlag.ExecutingCraftingAction];
        
        GatherBuddy.Log.Verbose($"[Crafting] IdleBetween check: Preparing={preparingFlag}, Crafting={craftingFlag}, Executing={executingFlag}");
        
        var tm = GatherBuddy.AutoGather?.TaskManager;
        if (tm != null && tm.IsBusy)
        {
            GatherBuddy.Log.Debug($"[Crafting] TaskManager busy, staying in IdleBetween");
            return CraftState.IdleBetween;
        }
        
        if (preparingFlag)
            return CraftState.IdleBetween;

        if (executingFlag)
            return CraftState.WaitStart;

        return TransitionFromIdleBetweenToExit();
    }

    private static unsafe CraftState TransitionFromIdleBetweenToExit()
    {
        GatherBuddy.Log.Information($"[Crafting] Exiting crafting menu, closing windows");
        try
        {
            var recipeAddon = Dalamud.GameGui.GetAddonByName("RecipeNote");
            if (recipeAddon != null && recipeAddon.Address != nint.Zero)
            {
                var atkUnit = (AtkUnitBase*)recipeAddon.Address;
                if (atkUnit != null && atkUnit->IsVisible)
                {
                    GatherBuddy.Log.Information("[Crafting] Closing RecipeNote window on exit from IdleBetween");
                    atkUnit->Close(true);
                }
            }
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[Crafting] Error closing recipe note on exit: {ex.Message}");
        }
        return CraftState.IdleNormal;
    }

    private static CraftState TransitionFromWaitStart()
    {
        if (Dalamud.Conditions[ConditionFlag.ExecutingCraftingAction])
            return CraftState.WaitStart;

        if (!Dalamud.Conditions[ConditionFlag.Crafting])
            return CraftState.IdleNormal;

        if (_currentRecipeId == null)
            return CraftState.WaitStart;

        if (!TryGetRecipe(_currentRecipeId.Value, out var recipe))
            return CraftState.InvalidState;

        if (recipe == null)
            return CraftState.InvalidState;

        _currentRecipe = recipe;
        CraftStarted?.Invoke(recipe, _currentRecipeId.Value);

        var actualRecipe = recipe.Value;
        GatherBuddy.Log.Debug($"[Crafting] Building craft state for recipe {_currentRecipeId}");
        _vulcanCraftState = CraftingStateBuilder.BuildCraftState(actualRecipe);
        if (_currentIngredientPreferences != null && _currentIngredientPreferences.Count > 0)
        {
            var iq = QualityCalculator.CalculateInitialQuality(actualRecipe, _currentIngredientPreferences);
            GatherBuddy.Log.Debug($"[Crafting] Setting InitialQuality={iq} from ingredient preferences for Raphael key");
            _vulcanCraftState = _vulcanCraftState with { InitialQuality = iq };
        }
        _vulcanStepState = CraftingStateBuilder.BuildInitialStepState(_vulcanCraftState);
        GatherBuddy.Log.Debug($"[Crafting] CraftState null={_vulcanCraftState == null}, StepState null={_vulcanStepState == null}");
        if (_vulcanCraftState != null && _vulcanStepState != null)
        {
            GatherBuddy.Log.Debug($"[Crafting] Calling OnCraftStarted");
            CraftingProcessor.OnCraftStarted(_vulcanCraftState, _vulcanStepState, _currentRecipeId.Value, false);
            var recommendation = CraftingProcessor.NextRecommendation;
            GatherBuddy.Log.Debug($"[Crafting] OnCraftStarted recommendation: {recommendation.Action}");
            if (recommendation.Action != VulcanSkill.None)
            {
                ExecuteSolverRecommendation(_vulcanCraftState, _vulcanStepState, recommendation);
            }
        }

        return CraftState.InProgress;
    }

    private static CraftState TransitionFromInProgress()
    {
        if (!Dalamud.Conditions[ConditionFlag.Crafting])
            return Finish(cancelled: true);

        if (Dalamud.Conditions[ConditionFlag.ExecutingCraftingAction])
            return CraftState.WaitAction;

        if (_nextActionAllowedAt != DateTime.MinValue && DateTime.Now < _nextActionAllowedAt)
            return CraftState.InProgress;

        if (_vulcanCraftState != null && _vulcanStepState != null)
        {
            var actualState = SynthesisReader.ReadCurrentStepState(_vulcanCraftState, _vulcanStepState);
            if (actualState != null)
            {
                if (actualState.Durability <= 0)
                {
                    GatherBuddy.Log.Debug($"[Crafting] Durability depleted, finishing craft");
                    return Finish(cancelled: false);
                }
                
                if (actualState.Progress >= _vulcanCraftState.CraftProgress)
                {
                    GatherBuddy.Log.Debug($"[Crafting] Progress complete, transitioning to WaitFinish");
                    return CraftState.WaitFinish;
                }
                
                actualState.PrevComboAction = _vulcanStepState.PrevComboAction;
                actualState.PrevActionFailed = _vulcanStepState.PrevActionFailed;
                _vulcanStepState = actualState;
            }
            else
            {
                GatherBuddy.Log.Debug($"[Crafting] Could not read actual state from Synthesis window, using simulation");
                if (_vulcanStepState.Progress >= _vulcanCraftState.CraftProgress)
                    return CraftState.WaitFinish;
            }
            
            if (_currentRecipe != null)
                CraftAdvanced?.Invoke(_currentRecipe);
            
            CraftingProcessor.OnCraftAdvanced(_vulcanCraftState, _vulcanStepState, _currentRecipeId);
            var recommendation = CraftingProcessor.NextRecommendation;
            if (recommendation.Action != VulcanSkill.None)
            {
                var delayMs = GatherBuddy.Config.VulcanExecutionDelayMs;
                if (delayMs > 0 && _nextActionAllowedAt == DateTime.MinValue)
                {
                    GatherBuddy.Log.Debug($"[Crafting] Delaying next action by {delayMs}ms");
                    _nextActionAllowedAt = DateTime.Now.AddMilliseconds(delayMs);
                    return CraftState.InProgress;
                }
                _nextActionAllowedAt = DateTime.MinValue;
                ExecuteSolverRecommendation(_vulcanCraftState, _vulcanStepState, recommendation);
            }
        }

        return CraftState.InProgress;
    }

    private static CraftState TransitionFromWaitAction()
    {
        if (Dalamud.Conditions[ConditionFlag.ExecutingCraftingAction])
            return CraftState.WaitAction;

        if (!Dalamud.Conditions[ConditionFlag.Crafting])
            return Finish(cancelled: true);

        return CraftState.InProgress;
    }

    private static CraftState TransitionFromWaitFinish()
    {
        if (Dalamud.Conditions[ConditionFlag.ExecutingCraftingAction])
            return CraftState.WaitFinish;

        var synthesisAddon = Dalamud.GameGui.GetAddonByName("Synthesis");
        var synthVisible = synthesisAddon != null && synthesisAddon.Address != nint.Zero;
        if (!synthVisible)
        {
            GatherBuddy.Log.Debug($"[Crafting] Craft finished, closing windows");
            return Finish(cancelled: false);
        }

        return CraftState.WaitFinish;
    }

    private static CraftState TransitionFromInvalid()
    {
        if (!Dalamud.Conditions[ConditionFlag.Crafting] && !Dalamud.Conditions[ConditionFlag.PreparingToCraft] && !Dalamud.Conditions[ConditionFlag.ExecutingCraftingAction])
            return CraftState.IdleNormal;

        if (Dalamud.Conditions[ConditionFlag.Crafting] && Dalamud.Conditions[ConditionFlag.PreparingToCraft])
            return CraftState.IdleBetween;

        return CraftState.InvalidState;
    }

    private static unsafe CraftState Finish(bool cancelled)
    {
        _nextActionAllowedAt = DateTime.MinValue;

        if (_currentRecipe != null)
            CraftFinished?.Invoke(_currentRecipe, cancelled);

        if (_vulcanCraftState != null && _vulcanStepState != null)
        {
            CraftingProcessor.OnCraftFinished(_vulcanCraftState, _vulcanStepState, _currentRecipeId, cancelled);
        }
        
        _currentSelectedMacroId = null;

        _currentRecipe = null;
        _currentRecipeId = null;
        _vulcanCraftState = null;
        _vulcanStepState = null;
        
        GatherBuddy.Log.Debug($"[Crafting] Craft finished. Preparing={Dalamud.Conditions[ConditionFlag.PreparingToCraft]}, Crafting={Dalamud.Conditions[ConditionFlag.Crafting]}");
        
        if (Dalamud.Conditions[ConditionFlag.PreparingToCraft])
            return CraftState.IdleBetween;

        return CraftState.IdleNormal;
    }

    private static uint? GetRecipeIdFromUI()
    {
        try
        {
            var addon = Dalamud.GameGui.GetAddonByName("Synthesis");
            if (addon == null)
                return null;

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryGetRecipe(uint recipeId, out Recipe? recipe)
    {
        recipe = null;
        var sheet = Dalamud.GameData.GetExcelSheet<Recipe>();
        if (sheet != null && sheet.TryGetRow(recipeId, out var row))
        {
            recipe = row;
            return true;
        }

        return false;
    }
}
