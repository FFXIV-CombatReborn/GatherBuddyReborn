using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text;
using Dalamud.Interface.Colors;
using Lumina.Excel.Sheets;
using ElliLib.Raii;
using ImRaii = ElliLib.Raii.ImRaii;
using GatherBuddy.Crafting;
using GatherBuddy.Vulcan;
using FFXIVClientStructs.FFXIV.Client.Game;
using Dalamud.Plugin.Services;

namespace GatherBuddy.Gui;

public class RecipeCraftSettingsPopup
{
    private static int _nextInstanceId;
    private static int _activeInstanceId;
    private bool _isOpen;
    private bool _shouldOpen;
    private uint _recipeId;
    private string _recipeName = string.Empty;
    private readonly int _instanceId = Interlocked.Increment(ref _nextInstanceId);
    private RecipeCraftSettings _editingSettings = new();
    private CraftingListItem? _editingListItem;
    private CraftingListDefinition? _editingList;
    private bool _isPrecraftMode;
    
    private List<(uint ItemId, string Name, bool IsHQ)> _foodItems = new();
    private List<(uint ItemId, string Name, bool IsHQ)> _medicineItems = new();
    private List<(uint ItemId, string Name)> _manualItems = new();
    private List<(uint ItemId, string Name)> _squadronManualItems = new();
    private string _foodSearch = string.Empty;
    private string _medicineSearch = string.Empty;
    private string _macroSearch = string.Empty;

    private MacroValidationResult? _validationResult;
    private string? _resolvedMacroId;
    private string? _lastValidatedMacroId;
    private uint?   _lastValidatedFoodId;
    private bool    _lastValidatedFoodHQ;
    private uint?   _lastValidatedMedicineId;
    private bool    _lastValidatedMedicineHQ;
    private int     _lastValidatedStartingQuality;
    
    private static readonly string[] OverrideModeLabels = { "Inherit", "None", "Specific" };
    private static readonly string[] MacroModeLabels    = { "Inherit", "Specific" };
    
    private class IngredientData
    {
        public uint ItemId { get; set; }
        public string ItemName { get; set; } = string.Empty;
        public int TotalNeeded { get; set; }
        public int AvailableNQ { get; set; }
        public int AvailableHQ { get; set; }
        public int DesiredHQ { get; set; }
        public bool CanBeHQ { get; set; }
    }

    private void Activate()
    {
        _activeInstanceId = _instanceId;
        _shouldOpen = true;
    }
    
    private List<IngredientData> _ingredients = new();
    private bool _useAllNQ = false;
    

    public void Open(uint recipeId, string recipeName)
    {
        GatherBuddy.Log.Debug($"[RecipeCraftSettingsPopup] Opening popup for recipe {recipeId}: {recipeName}");
        _recipeId = recipeId;
        _recipeName = recipeName;
        _editingListItem = null;
        _editingList = null;
        _isPrecraftMode = false;
        
        var existing = GatherBuddy.RecipeBrowserSettings.Get(recipeId);
        if (existing != null)
        {
            _editingSettings = new RecipeCraftSettings
            {
                FoodMode = existing.FoodMode,
                FoodItemId = existing.FoodItemId,
                FoodHQ = existing.FoodHQ,
                MedicineMode = existing.MedicineMode,
                MedicineItemId = existing.MedicineItemId,
                MedicineHQ = existing.MedicineHQ,
                ManualMode = existing.ManualMode,
                ManualItemId = existing.ManualItemId,
                SquadronManualMode = existing.SquadronManualMode,
                SquadronManualItemId = existing.SquadronManualItemId,
                IngredientPreferences = new Dictionary<uint, int>(existing.IngredientPreferences),
                UseAllNQ = existing.UseAllNQ,
                SelectedMacroId = existing.SelectedMacroId,
                SolverOverride = existing.SolverOverride,
            };
        }
        else
        {
            _editingSettings = new RecipeCraftSettings();
        }
        
        LoadConsumables();
        LoadIngredients();
        ResetValidationState();
        Activate();
        GatherBuddy.Log.Debug($"[RecipeCraftSettingsPopup] Setting shouldOpen flag");
    }
    
    public void OpenForListItem(CraftingListItem item, CraftingListDefinition list, string recipeName)
    {
        GatherBuddy.Log.Debug($"[RecipeCraftSettingsPopup] Opening popup for list item {item.RecipeId}: {recipeName}");
        _recipeId = item.RecipeId;
        _recipeName = recipeName;
        _editingListItem = item;
        _editingList = list;
        _isPrecraftMode = false;
        
        var cs = item.CraftSettings;
        _editingSettings = new RecipeCraftSettings
        {
            FoodMode = cs?.FoodMode ?? ConsumableOverrideMode.Inherit,
            FoodItemId = cs?.FoodItemId,
            FoodHQ = cs?.FoodHQ ?? false,
            MedicineMode = cs?.MedicineMode ?? ConsumableOverrideMode.Inherit,
            MedicineItemId = cs?.MedicineItemId,
            MedicineHQ = cs?.MedicineHQ ?? false,
            ManualMode = cs?.ManualMode ?? ConsumableOverrideMode.Inherit,
            ManualItemId = cs?.ManualItemId,
            SquadronManualMode = cs?.SquadronManualMode ?? ConsumableOverrideMode.Inherit,
            SquadronManualItemId = cs?.SquadronManualItemId,
            IngredientPreferences = cs != null ? new Dictionary<uint, int>(cs.IngredientPreferences) : new(),
            UseAllNQ = cs?.UseAllNQ ?? false,
            MacroMode = (cs?.MacroMode == MacroOverrideMode.Specific || !string.IsNullOrEmpty(cs?.SelectedMacroId) || cs?.SolverOverride != SolverOverrideMode.Default)
                ? MacroOverrideMode.Specific
                : MacroOverrideMode.Inherit,
            SelectedMacroId = cs?.SelectedMacroId,
            SolverOverride = cs?.SolverOverride ?? SolverOverrideMode.Default,
        };
        
        LoadConsumables();
        LoadIngredients();
        ResetValidationState();
        Activate();
        GatherBuddy.Log.Debug($"[RecipeCraftSettingsPopup] Setting shouldOpen flag for list item");
    }

    public void OpenForPrecraft(uint recipeId, string recipeName, CraftingListDefinition list)
    {
        GatherBuddy.Log.Debug($"[RecipeCraftSettingsPopup] Opening popup for precraft {recipeId}: {recipeName}");
        _recipeId = recipeId;
        _recipeName = recipeName;
        _editingListItem = null;
        _editingList = list;
        _isPrecraftMode = true;

        var cs = list.PrecraftCraftSettings.GetValueOrDefault(recipeId);
        _editingSettings = new RecipeCraftSettings
        {
            FoodMode = cs?.FoodMode ?? ConsumableOverrideMode.Inherit,
            FoodItemId = cs?.FoodItemId,
            FoodHQ = cs?.FoodHQ ?? false,
            MedicineMode = cs?.MedicineMode ?? ConsumableOverrideMode.Inherit,
            MedicineItemId = cs?.MedicineItemId,
            MedicineHQ = cs?.MedicineHQ ?? false,
            ManualMode = cs?.ManualMode ?? ConsumableOverrideMode.Inherit,
            ManualItemId = cs?.ManualItemId,
            SquadronManualMode = cs?.SquadronManualMode ?? ConsumableOverrideMode.Inherit,
            SquadronManualItemId = cs?.SquadronManualItemId,
            IngredientPreferences = cs != null ? new Dictionary<uint, int>(cs.IngredientPreferences) : new(),
            UseAllNQ = cs?.UseAllNQ ?? false,
            MacroMode = (cs?.MacroMode == MacroOverrideMode.Specific || !string.IsNullOrEmpty(cs?.SelectedMacroId) || cs?.SolverOverride != SolverOverrideMode.Default)
                ? MacroOverrideMode.Specific
                : MacroOverrideMode.Inherit,
            SelectedMacroId = cs?.SelectedMacroId,
            SolverOverride = cs?.SolverOverride ?? SolverOverrideMode.Default,
        };

        LoadConsumables();
        LoadIngredients();
        ResetValidationState();
        Activate();
        GatherBuddy.Log.Debug($"[RecipeCraftSettingsPopup] Setting shouldOpen flag for precraft");
    }

    public void Draw()
    {
        if (_activeInstanceId != 0 && _activeInstanceId != _instanceId)
        {
            _isOpen = false;
            _shouldOpen = false;
            return;
        }
        if (_shouldOpen)
        {
            GatherBuddy.Log.Debug($"[RecipeCraftSettingsPopup] Opening window in Draw()");
            _shouldOpen = false;
            _isOpen = true;
        }
        
        if (!_isOpen) return;
        
        ImGui.SetNextWindowSize(new Vector2(450, 0), ImGuiCond.Appearing);
        
        if (ImGui.Begin($"Craft Settings - {_recipeName}###RecipeCraftSettings_{_instanceId}", ref _isOpen, ImGuiWindowFlags.NoResize | ImGuiWindowFlags.AlwaysAutoResize))
        {
            RefreshValidationIfNeeded();
            DrawMacroSelector();
            DrawMacroValidationStatus();
            DrawFoodSelector();
            DrawMedicineSelector();
            DrawManualSelector();
            DrawSquadronManualSelector();
            DrawIngredientQuality();

            ImGui.Separator();
            ImGui.Spacing();

            if (ImGui.Button("Save", new Vector2(100, 0)))
            {
                if (!string.IsNullOrEmpty(_resolvedMacroId))
                    MacroValidator.Invalidate(_recipeId, _resolvedMacroId);
                SaveIngredientPreferences();
                
                if (_editingListItem != null && _editingList != null)
                {
                    if (_editingSettings.HasAnySettings())
                    {
                        _editingListItem.CraftSettings = new RecipeCraftSettings
                        {
                            FoodMode = _editingSettings.FoodMode,
                            FoodItemId = _editingSettings.FoodMode == ConsumableOverrideMode.Specific ? _editingSettings.FoodItemId : null,
                            FoodHQ = _editingSettings.FoodMode == ConsumableOverrideMode.Specific ? _editingSettings.FoodHQ : false,
                            MedicineMode = _editingSettings.MedicineMode,
                            MedicineItemId = _editingSettings.MedicineMode == ConsumableOverrideMode.Specific ? _editingSettings.MedicineItemId : null,
                            MedicineHQ = _editingSettings.MedicineMode == ConsumableOverrideMode.Specific ? _editingSettings.MedicineHQ : false,
                            ManualMode = _editingSettings.ManualMode,
                            ManualItemId = _editingSettings.ManualMode == ConsumableOverrideMode.Specific ? _editingSettings.ManualItemId : null,
                            SquadronManualMode = _editingSettings.SquadronManualMode,
                            SquadronManualItemId = _editingSettings.SquadronManualMode == ConsumableOverrideMode.Specific ? _editingSettings.SquadronManualItemId : null,
                            IngredientPreferences = new Dictionary<uint, int>(_editingSettings.IngredientPreferences),
                            UseAllNQ = _editingSettings.UseAllNQ,
                            MacroMode = _editingSettings.MacroMode,
                            SelectedMacroId = _editingSettings.MacroMode == MacroOverrideMode.Specific ? _editingSettings.SelectedMacroId : null,
                            SolverOverride = _editingSettings.MacroMode == MacroOverrideMode.Specific ? _editingSettings.SolverOverride : SolverOverrideMode.Default,
                        };
                    }
                    else
                    {
                        _editingListItem.CraftSettings = null;
                    }
                    GatherBuddy.CraftingListManager.SaveList(_editingList);
                }
                else if (_isPrecraftMode && _editingList != null)
                {
                    _editingList.SetPrecraftCraftSettings(_recipeId, _editingSettings.HasAnySettings() ? new RecipeCraftSettings
                    {
                        FoodMode = _editingSettings.FoodMode,
                        FoodItemId = _editingSettings.FoodMode == ConsumableOverrideMode.Specific ? _editingSettings.FoodItemId : null,
                        FoodHQ = _editingSettings.FoodMode == ConsumableOverrideMode.Specific ? _editingSettings.FoodHQ : false,
                        MedicineMode = _editingSettings.MedicineMode,
                        MedicineItemId = _editingSettings.MedicineMode == ConsumableOverrideMode.Specific ? _editingSettings.MedicineItemId : null,
                        MedicineHQ = _editingSettings.MedicineMode == ConsumableOverrideMode.Specific ? _editingSettings.MedicineHQ : false,
                        ManualMode = _editingSettings.ManualMode,
                        ManualItemId = _editingSettings.ManualMode == ConsumableOverrideMode.Specific ? _editingSettings.ManualItemId : null,
                        SquadronManualMode = _editingSettings.SquadronManualMode,
                        SquadronManualItemId = _editingSettings.SquadronManualMode == ConsumableOverrideMode.Specific ? _editingSettings.SquadronManualItemId : null,
                        IngredientPreferences = new Dictionary<uint, int>(_editingSettings.IngredientPreferences),
                        UseAllNQ = _editingSettings.UseAllNQ,
                        MacroMode = _editingSettings.MacroMode,
                        SelectedMacroId = _editingSettings.MacroMode == MacroOverrideMode.Specific ? _editingSettings.SelectedMacroId : null,
                        SolverOverride = _editingSettings.MacroMode == MacroOverrideMode.Specific ? _editingSettings.SolverOverride : SolverOverrideMode.Default,
                    } : null);
                    GatherBuddy.CraftingListManager.SaveList(_editingList);
                }
                else
                {
                    _editingSettings.FoodMode = _editingSettings.FoodItemId.HasValue ? ConsumableOverrideMode.Specific : ConsumableOverrideMode.Inherit;
                    _editingSettings.MedicineMode = _editingSettings.MedicineItemId.HasValue ? ConsumableOverrideMode.Specific : ConsumableOverrideMode.Inherit;
                    _editingSettings.ManualMode = _editingSettings.ManualItemId.HasValue ? ConsumableOverrideMode.Specific : ConsumableOverrideMode.Inherit;
                    _editingSettings.SquadronManualMode = _editingSettings.SquadronManualItemId.HasValue ? ConsumableOverrideMode.Specific : ConsumableOverrideMode.Inherit;
                    GatherBuddy.RecipeBrowserSettings.Set(_recipeId, _editingSettings);
                    GatherBuddy.RecipeBrowserSettings.Save();
                }
                _isOpen = false;
            }

            ImGui.SameLine();
            if (ImGui.Button("Clear All", new Vector2(100, 0)))
            {
                _editingSettings.Clear();
                
                if (_editingListItem != null && _editingList != null)
                {
                    _editingListItem.CraftSettings = null;
                    GatherBuddy.CraftingListManager.SaveList(_editingList);
                }
                else if (_isPrecraftMode && _editingList != null)
                {
                    _editingList.SetPrecraftCraftSettings(_recipeId, null);
                    GatherBuddy.CraftingListManager.SaveList(_editingList);
                }
                else
                {
                    GatherBuddy.RecipeBrowserSettings.Remove(_recipeId);
                    GatherBuddy.RecipeBrowserSettings.Save();
                }
                _isOpen = false;
            }

            ImGui.SameLine();
            if (ImGui.Button("Cancel", new Vector2(100, 0)))
            {
                _isOpen = false;
            }

            ImGui.End();
        }

        if (!_isOpen && _activeInstanceId == _instanceId)
            _activeInstanceId = 0;
    }

    private void ResetValidationState()
    {
        _validationResult             = null;
        _resolvedMacroId              = null;
        _lastValidatedMacroId         = null;
        _lastValidatedFoodId          = null;
        _lastValidatedFoodHQ          = false;
        _lastValidatedMedicineId      = null;
        _lastValidatedMedicineHQ      = false;
        _lastValidatedStartingQuality = -1;
        _macroSearch                  = string.Empty;
    }

    private string? ResolveEffectiveMacroIdForEdit()
    {
        if (_editingListItem == null && !_isPrecraftMode)
            return _editingSettings.SelectedMacroId;
        if (_editingSettings.MacroMode == MacroOverrideMode.Specific)
            return _editingSettings.SelectedMacroId;
        return _isPrecraftMode ? _editingList?.DefaultPrecraftMacroId : _editingList?.DefaultFinalMacroId;
    }

    private void RefreshValidationIfNeeded()
    {
        SaveIngredientPreferences();
        var macroId = ResolveEffectiveMacroIdForEdit();
        _resolvedMacroId = macroId;
        var listConsumables = _editingList?.Consumables;

        var effectiveFoodId = _editingSettings.FoodMode switch
        {
            ConsumableOverrideMode.Specific => _editingSettings.FoodItemId,
            ConsumableOverrideMode.Inherit  => listConsumables?.FoodItemId,
            _                               => null,
        };
        var effectiveFoodHQ = _editingSettings.FoodMode switch
        {
            ConsumableOverrideMode.Specific => _editingSettings.FoodHQ,
            ConsumableOverrideMode.Inherit  => listConsumables?.FoodHQ ?? false,
            _                               => false,
        };
        var effectiveMedicineId = _editingSettings.MedicineMode switch
        {
            ConsumableOverrideMode.Specific => _editingSettings.MedicineItemId,
            ConsumableOverrideMode.Inherit  => listConsumables?.MedicineItemId,
            _                               => null,
        };
        var effectiveMedicineHQ = _editingSettings.MedicineMode switch
        {
            ConsumableOverrideMode.Specific => _editingSettings.MedicineHQ,
            ConsumableOverrideMode.Inherit  => listConsumables?.MedicineHQ ?? false,
            _                               => false,
        };

        var recipe = RecipeManager.GetRecipe(_recipeId);
        var ingredientPrefs = _editingSettings.UseAllNQ ? new Dictionary<uint, int>() : _editingSettings.IngredientPreferences;
        var startingQuality = recipe.HasValue ? QualityCalculator.CalculateInitialQuality(recipe.Value, ingredientPrefs) : 0;

        if (macroId == _lastValidatedMacroId
            && effectiveFoodId == _lastValidatedFoodId
            && effectiveFoodHQ == _lastValidatedFoodHQ
            && effectiveMedicineId == _lastValidatedMedicineId
            && effectiveMedicineHQ == _lastValidatedMedicineHQ
            && startingQuality == _lastValidatedStartingQuality)
            return;

        _lastValidatedMacroId         = macroId;
        _lastValidatedFoodId          = effectiveFoodId;
        _lastValidatedFoodHQ          = effectiveFoodHQ;
        _lastValidatedMedicineId      = effectiveMedicineId;
        _lastValidatedMedicineHQ      = effectiveMedicineHQ;
        _lastValidatedStartingQuality = startingQuality;

        if (string.IsNullOrEmpty(macroId))
        {
            _validationResult = null;
            return;
        }

        MacroValidator.Invalidate(_recipeId, macroId);
        _validationResult = MacroValidator.GetOrCompute(_recipeId, macroId, _editingSettings, listConsumables);
        GatherBuddy.Log.Debug($"[RecipeCraftSettingsPopup] Validation refreshed: {(_validationResult == null ? "null" : _validationResult.IsValid ? "PASS" : $"FAIL ({_validationResult.Failure})")}");
    }

    private void DrawMacroValidationStatus()
    {
        if (string.IsNullOrEmpty(_resolvedMacroId))
            return;

        ImGui.Spacing();

        if (_validationResult == null || _validationResult.Failure == MacroValidationFailure.NoStats)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "Validation: No gear stats available for this job.");
            return;
        }

        if (_validationResult.IsValid)
        {
            ImGui.TextColored(ImGuiColors.ParsedGreen, "Validation: PASS");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    $"Macro will complete the craft.\n" +
                    $"Progress: {_validationResult.FinalProgress}/{_validationResult.RequiredProgress}\n" +
                    $"Quality: {_validationResult.FinalQuality}\n" +
                    $"Remaining Durability: {_validationResult.FinalDurability}" +
                    (_validationResult.SkippedConditionGatedCount > 0
                        ? $"\nSkipped {_validationResult.SkippedConditionGatedCount} condition-gated action(s)"
                        : ""));
        }
        else
        {
            var (color, label) = _validationResult.Failure switch
            {
                MacroValidationFailure.CPExhausted          => (ImGuiColors.DalamudRed,    "FAIL \u2014 CP exhausted"),
                MacroValidationFailure.DurabilityFailed     => (ImGuiColors.DalamudRed,    "FAIL \u2014 durability broke"),
                MacroValidationFailure.InsufficientProgress => (ImGuiColors.DalamudYellow, "WARN \u2014 insufficient progress"),
                MacroValidationFailure.ActionUnusable       => (ImGuiColors.DalamudYellow, "WARN \u2014 action unusable"),
                _                                           => (ImGuiColors.DalamudRed,    "FAIL"),
            };
            ImGui.TextColored(color, $"Validation: {label} (step {_validationResult.FailedAtStep})");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    $"Progress: {_validationResult.FinalProgress}/{_validationResult.RequiredProgress}\n" +
                    $"Quality: {_validationResult.FinalQuality}\n" +
                    $"Remaining Durability: {_validationResult.FinalDurability}");
        }
    }

    private void DrawMacroSelector()
    {
        ImGui.AlignTextToFramePadding();
        ImGui.Text("Macro:");
        ImGui.SameLine(120);

        var allMacros = CraftingGameInterop.UserMacroLibrary.GetAllMacros();

        if (_editingListItem != null || _isPrecraftMode)
        {
            var mode = (int)_editingSettings.MacroMode;
            ImGui.SetNextItemWidth(90);
            if (ImGui.Combo("##MacroMode", ref mode, MacroModeLabels, MacroModeLabels.Length))
                _editingSettings.MacroMode = (MacroOverrideMode)mode;

            if (_editingSettings.MacroMode == MacroOverrideMode.Inherit)
            {
                ImGui.SameLine();
                var inheritedId = _isPrecraftMode ? _editingList?.DefaultPrecraftMacroId : _editingList?.DefaultFinalMacroId;
                var inheritedName = string.IsNullOrEmpty(inheritedId)
                    ? "None (Use Solver)"
                    : (allMacros.FirstOrDefault(m => m.Id == inheritedId)?.Name ?? "(Macro Not Found)");
                ImGui.TextColored(new Vector4(0.6f, 0.8f, 1f, 1f), $"List: {inheritedName}");
                return;
            }

            ImGui.SameLine();
        }

        DrawMacroCombo(allMacros);
    }

    private void DrawMacroCombo(System.Collections.Generic.List<UserMacro> allMacros)
    {
        var currentMacroName = _editingSettings.SolverOverride switch
        {
            SolverOverrideMode.StandardSolver    => "Standard Solver",
            SolverOverrideMode.RaphaelSolver     => "Raphael Solver",
            SolverOverrideMode.ProgressOnlySolver => "Progress Only",
            _ when !string.IsNullOrEmpty(_editingSettings.SelectedMacroId) =>
                allMacros.FirstOrDefault(m => m.Id == _editingSettings.SelectedMacroId)?.Name ?? "(Macro Not Found)",
            _ => "Default (Use Solver)"
        };

        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##MacroSelector", currentMacroName))
        {
            var isDefault = _editingSettings.SolverOverride == SolverOverrideMode.Default && string.IsNullOrEmpty(_editingSettings.SelectedMacroId);
            if (ImGui.Selectable("Default (Use Solver)", isDefault))
            {
                _editingSettings.SelectedMacroId = null;
                _editingSettings.SolverOverride = SolverOverrideMode.Default;
            }
            if (ImGui.Selectable("Standard Solver", _editingSettings.SolverOverride == SolverOverrideMode.StandardSolver))
            {
                _editingSettings.SelectedMacroId = null;
                _editingSettings.SolverOverride = SolverOverrideMode.StandardSolver;
            }
            if (ImGui.Selectable("Raphael Solver", _editingSettings.SolverOverride == SolverOverrideMode.RaphaelSolver))
            {
                _editingSettings.SelectedMacroId = null;
                _editingSettings.SolverOverride = SolverOverrideMode.RaphaelSolver;
            }
            if (ImGui.Selectable("Progress Only", _editingSettings.SolverOverride == SolverOverrideMode.ProgressOnlySolver))
            {
                _editingSettings.SelectedMacroId = null;
                _editingSettings.SolverOverride = SolverOverrideMode.ProgressOnlySolver;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Use only progress-building actions, no quality steps. Fast NQ crafts.");

            if (allMacros.Count > 0)
            {
                ImGui.Separator();
                ImGui.InputTextWithHint("##MacroSearch", "Search macros...", ref _macroSearch, 128);
                var filteredMacros = string.IsNullOrWhiteSpace(_macroSearch)
                    ? allMacros
                    : allMacros.Where(m => m.Name.Contains(_macroSearch, StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var macro in filteredMacros)
                {
                    var isSelected = _editingSettings.SelectedMacroId == macro.Id;
                    var displayName = macro.MinCraftsmanship > 0 || macro.MinControl > 0 || macro.MinCP > 0
                        ? $"{macro.Name} ({macro.MinCraftsmanship}/{macro.MinControl}/{macro.MinCP})"
                        : macro.Name;
                    if (ImGui.Selectable(displayName, isSelected))
                    {
                        _editingSettings.SelectedMacroId = macro.Id;
                        _editingSettings.SolverOverride = SolverOverrideMode.Default;
                    }
                }
            }

            ImGui.EndCombo();
        }
    }

    private void DrawFoodSelector()
    {
        ImGui.AlignTextToFramePadding();
        ImGui.Text("Food:");
        ImGui.SameLine(120);

        if (_editingListItem != null || _isPrecraftMode)
        {
            var mode = (int)_editingSettings.FoodMode;
            ImGui.SetNextItemWidth(90);
            if (ImGui.Combo("##FoodMode", ref mode, OverrideModeLabels, OverrideModeLabels.Length))
                _editingSettings.FoodMode = (ConsumableOverrideMode)mode;

            if (_editingSettings.FoodMode == ConsumableOverrideMode.Inherit)
            {
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.6f, 0.8f, 1f, 1f), $"List: {GetConsumableDisplayName(_editingList?.Consumables.FoodItemId, _editingList?.Consumables.FoodHQ ?? false)}");
            }
            else if (_editingSettings.FoodMode == ConsumableOverrideMode.Specific)
            {
                ImGui.SameLine();
                DrawFoodCombo();
            }
            return;
        }

        DrawFoodCombo();
    }

    private void DrawFoodCombo()
    {
        var currentFood = _editingSettings.FoodItemId.HasValue
            ? GetItemName(_editingSettings.FoodItemId.Value) + (_editingSettings.FoodHQ ? $" {(char)SeIconChar.HighQuality}" : "")
            : "None";
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##FoodSelector", currentFood))
        {
            if (ImGui.Selectable("None", !_editingSettings.FoodItemId.HasValue))
            {
                _editingSettings.FoodItemId = null;
                _editingSettings.FoodHQ = false;
            }
            ImGui.Separator();
            ImGui.InputTextWithHint("##FoodSearch", "Search food...", ref _foodSearch, 128);
            var filteredFood = string.IsNullOrWhiteSpace(_foodSearch)
                ? _foodItems
                : _foodItems.Where(f => f.Name.Contains(_foodSearch, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var (itemId, name, isHQ) in filteredFood)
            {
                var displayName = name + (isHQ ? $" {(char)SeIconChar.HighQuality}" : "");
                if (ImGui.Selectable(displayName, _editingSettings.FoodItemId == itemId && _editingSettings.FoodHQ == isHQ))
                {
                    _editingSettings.FoodItemId = itemId;
                    _editingSettings.FoodHQ = isHQ;
                }
            }
            ImGui.EndCombo();
        }
    }

    private void DrawMedicineSelector()
    {
        ImGui.AlignTextToFramePadding();
        ImGui.Text("Medicine:");
        ImGui.SameLine(120);

        if (_editingListItem != null || _isPrecraftMode)
        {
            var mode = (int)_editingSettings.MedicineMode;
            ImGui.SetNextItemWidth(90);
            if (ImGui.Combo("##MedicineMode", ref mode, OverrideModeLabels, OverrideModeLabels.Length))
                _editingSettings.MedicineMode = (ConsumableOverrideMode)mode;

            if (_editingSettings.MedicineMode == ConsumableOverrideMode.Inherit)
            {
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.6f, 0.8f, 1f, 1f), $"List: {GetConsumableDisplayName(_editingList?.Consumables.MedicineItemId, _editingList?.Consumables.MedicineHQ ?? false)}");
            }
            else if (_editingSettings.MedicineMode == ConsumableOverrideMode.Specific)
            {
                ImGui.SameLine();
                DrawMedicineCombo();
            }
            return;
        }

        DrawMedicineCombo();
    }

    private void DrawMedicineCombo()
    {
        var currentMedicine = _editingSettings.MedicineItemId.HasValue
            ? GetItemName(_editingSettings.MedicineItemId.Value) + (_editingSettings.MedicineHQ ? $" {(char)SeIconChar.HighQuality}" : "")
            : "None";
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##MedicineSelector", currentMedicine))
        {
            if (ImGui.Selectable("None", !_editingSettings.MedicineItemId.HasValue))
            {
                _editingSettings.MedicineItemId = null;
                _editingSettings.MedicineHQ = false;
            }
            ImGui.Separator();
            ImGui.InputTextWithHint("##MedicineSearch", "Search medicine...", ref _medicineSearch, 128);
            var filteredMedicine = string.IsNullOrWhiteSpace(_medicineSearch)
                ? _medicineItems
                : _medicineItems.Where(m => m.Name.Contains(_medicineSearch, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var (itemId, name, isHQ) in filteredMedicine)
            {
                var displayName = name + (isHQ ? $" {(char)SeIconChar.HighQuality}" : "");
                if (ImGui.Selectable(displayName, _editingSettings.MedicineItemId == itemId && _editingSettings.MedicineHQ == isHQ))
                {
                    _editingSettings.MedicineItemId = itemId;
                    _editingSettings.MedicineHQ = isHQ;
                }
            }
            ImGui.EndCombo();
        }
    }

    private void DrawManualSelector()
    {
        ImGui.AlignTextToFramePadding();
        ImGui.Text("Manual:");
        ImGui.SameLine(120);

        if (_editingListItem != null || _isPrecraftMode)
        {
            var mode = (int)_editingSettings.ManualMode;
            ImGui.SetNextItemWidth(90);
            if (ImGui.Combo("##ManualMode", ref mode, OverrideModeLabels, OverrideModeLabels.Length))
                _editingSettings.ManualMode = (ConsumableOverrideMode)mode;

            if (_editingSettings.ManualMode == ConsumableOverrideMode.Inherit)
            {
                ImGui.SameLine();
                var listManualId = _editingList?.Consumables.ManualItemId;
                ImGui.TextColored(new Vector4(0.6f, 0.8f, 1f, 1f), $"List: {(listManualId.HasValue ? GetItemName(listManualId.Value) : "None")}");
            }
            else if (_editingSettings.ManualMode == ConsumableOverrideMode.Specific)
            {
                ImGui.SameLine();
                DrawManualCombo();
            }
            return;
        }

        DrawManualCombo();
    }

    private void DrawManualCombo()
    {
        var manualId = _editingSettings.ManualItemId ?? 0;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##ManualSelector", manualId == 0 ? "None" : GetItemName(manualId)))
        {
            if (ImGui.Selectable("None", manualId == 0))
                _editingSettings.ManualItemId = null;
            foreach (var (itemId, name) in _manualItems)
            {
                if (ImGui.Selectable(name, itemId == manualId))
                    _editingSettings.ManualItemId = itemId;
            }
            ImGui.EndCombo();
        }
    }

    private void DrawSquadronManualSelector()
    {
        ImGui.AlignTextToFramePadding();
        ImGui.Text("Squadron Manual:");
        ImGui.SameLine(120);

        if (_editingListItem != null || _isPrecraftMode)
        {
            var mode = (int)_editingSettings.SquadronManualMode;
            ImGui.SetNextItemWidth(90);
            if (ImGui.Combo("##SquadronMode", ref mode, OverrideModeLabels, OverrideModeLabels.Length))
                _editingSettings.SquadronManualMode = (ConsumableOverrideMode)mode;

            if (_editingSettings.SquadronManualMode == ConsumableOverrideMode.Inherit)
            {
                ImGui.SameLine();
                var listSquadronId = _editingList?.Consumables.SquadronManualItemId;
                ImGui.TextColored(new Vector4(0.6f, 0.8f, 1f, 1f), $"List: {(listSquadronId.HasValue ? GetItemName(listSquadronId.Value) : "None")}");
            }
            else if (_editingSettings.SquadronManualMode == ConsumableOverrideMode.Specific)
            {
                ImGui.SameLine();
                DrawSquadronManualCombo();
            }
            return;
        }

        DrawSquadronManualCombo();
    }

    private void DrawSquadronManualCombo()
    {
        var squadronId = _editingSettings.SquadronManualItemId ?? 0;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##SquadronManualSelector", squadronId == 0 ? "None" : GetItemName(squadronId)))
        {
            if (ImGui.Selectable("None", squadronId == 0))
                _editingSettings.SquadronManualItemId = null;
            foreach (var (itemId, name) in _squadronManualItems)
            {
                if (ImGui.Selectable(name, itemId == squadronId))
                    _editingSettings.SquadronManualItemId = itemId;
            }
            ImGui.EndCombo();
        }
    }

    private string GetConsumableDisplayName(uint? itemId, bool hq)
        => itemId.HasValue ? GetItemName(itemId.Value) + (hq ? $" {(char)SeIconChar.HighQuality}" : "") : "None";

    private void LoadConsumables()
    {
        _foodItems.Clear();
        _medicineItems.Clear();
        _manualItems.Clear();
        _squadronManualItems.Clear();

        var itemSheet = Dalamud.GameData.GetExcelSheet<Item>();
        if (itemSheet == null)
            return;

        foreach (var item in itemSheet)
        {
            if (IsCraftersFood(item))
            {
                var name = item.Name.ExtractText();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    _foodItems.Add((item.RowId, name, false));
                    if (item.CanBeHq)
                        _foodItems.Add((item.RowId, name, true));
                }
            }
            else if (IsCraftersPot(item))
            {
                var name = item.Name.ExtractText();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    _medicineItems.Add((item.RowId, name, false));
                    if (item.CanBeHq)
                        _medicineItems.Add((item.RowId, name, true));
                }
            }
            else if (IsCraftersManual(item))
            {
                var name = item.Name.ExtractText();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    _manualItems.Add((item.RowId, name));
                }
            }
            else if (IsSquadronManual(item))
            {
                var name = item.Name.ExtractText();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    _squadronManualItems.Add((item.RowId, name));
                }
            }
        }

        _foodItems = _foodItems
            .OrderBy(f => !HasItemInInventory(f.ItemId, f.IsHQ))
            .ThenBy(f => f.Name)
            .ThenBy(f => f.IsHQ ? 0 : 1)
            .ToList();
        _medicineItems = _medicineItems
            .OrderBy(m => !HasItemInInventory(m.ItemId, m.IsHQ))
            .ThenBy(m => m.Name)
            .ThenBy(m => m.IsHQ ? 0 : 1)
            .ToList();
        _manualItems = _manualItems
            .OrderBy(m => !HasItemInInventory(m.ItemId, false))
            .ThenBy(m => m.Name)
            .ToList();
        _squadronManualItems = _squadronManualItems
            .OrderBy(m => !HasItemInInventory(m.ItemId, false))
            .ThenBy(m => m.Name)
            .ToList();
    }
    
    private static ItemFood? GetItemConsumableProperties(Item item, bool hq)
    {
        if (!item.ItemAction.IsValid)
            return null;
        var action = item.ItemAction.Value;
        var actionParams = hq ? action.DataHQ : action.Data;
        if (actionParams[0] is not 48 and not 49)
            return null;
        return Dalamud.GameData.GetExcelSheet<ItemFood>()?.GetRow(actionParams[1]);
    }
    
    private static bool IsCraftersFood(Item item)
    {
        if (item.ItemUICategory.RowId != 46)
            return false;
        var consumable = GetItemConsumableProperties(item, false);
        return consumable != null && consumable.Value.Params.Any(p => p.BaseParam.RowId is 11 or 70 or 71);
    }
    
    private static bool IsCraftersPot(Item item)
    {
        if (item.ItemUICategory.RowId != 44)
            return false;
        var consumable = GetItemConsumableProperties(item, false);
        return consumable != null && consumable.Value.Params.Any(p => p.BaseParam.RowId is 11 or 70 or 71 or 69 or 68);
    }

    private static bool IsCraftersManual(Item item)
    {
        if (item.ItemUICategory.RowId != 63)
            return false;
        if (!item.ItemAction.IsValid)
            return false;
        var action = item.ItemAction.Value;
        return action.Action.RowId == 816 && action.Data[0] is 300 or 301 or 1751 or 5329;
    }

    private static bool IsSquadronManual(Item item)
    {
        if (item.ItemUICategory.RowId != 63)
            return false;
        if (!item.ItemAction.IsValid)
            return false;
        var action = item.ItemAction.Value;
        return action.Action.RowId == 816 && action.Data[0] is 2291 or 2292 or 2293 or 2294;
    }

    private string GetItemName(uint itemId)
    {
        var itemSheet = Dalamud.GameData.GetExcelSheet<Item>();
        if (itemSheet != null && itemSheet.TryGetRow(itemId, out var item))
            return item.Name.ExtractText();
        return "Unknown";
    }
    
    private void LoadIngredients()
    {
        _ingredients.Clear();
        _useAllNQ = _editingSettings.UseAllNQ;
        
        var recipe = RecipeManager.GetRecipe(_recipeId);
        if (!recipe.HasValue)
            return;
        
        var ingredients = RecipeManager.GetIngredients(recipe.Value);
        foreach (var (itemId, amount) in ingredients)
        {
            var item = Dalamud.GameData.GetExcelSheet<Item>()?.GetRow(itemId);
            if (item == null) continue;
            
            var (availNQ, availHQ) = GetInventoryCountSplit(itemId);
            var desiredHQ = _editingSettings.IngredientPreferences.GetValueOrDefault(itemId, 0);
            var canBeHQ = item.Value.CanBeHq;
            
            _ingredients.Add(new IngredientData
            {
                ItemId = itemId,
                ItemName = item.Value.Name.ExtractText(),
                TotalNeeded = amount,
                AvailableNQ = availNQ,
                AvailableHQ = availHQ,
                DesiredHQ = canBeHQ ? desiredHQ : 0,
                CanBeHQ = canBeHQ
            });
        }
    }
    
    private void SaveIngredientPreferences()
    {
        _editingSettings.IngredientPreferences.Clear();
        foreach (var ing in _ingredients)
        {
            if (ing.DesiredHQ > 0)
                _editingSettings.IngredientPreferences[ing.ItemId] = ing.DesiredHQ;
        }
        _editingSettings.UseAllNQ = _useAllNQ;
    }
    
    private int CalculateCurrentQuality(Recipe recipe)
    {
        var preferences = new Dictionary<uint, int>();
        foreach (var ing in _ingredients)
        {
            if (ing.DesiredHQ > 0)
                preferences[ing.ItemId] = ing.DesiredHQ;
        }
        return QualityCalculator.CalculateInitialQuality(recipe, preferences);
    }
    
    private int CalculateMaxQuality(Recipe recipe)
    {
        var lt = recipe.RecipeLevelTable.Value;
        return (int)(lt.Quality * recipe.QualityFactor / 100);
    }
    
    private void DrawIngredientQuality()
    {
        if (_ingredients.Count == 0)
            return;
        
        ImGui.Separator();
        ImGui.Spacing();
        
        var recipe = RecipeManager.GetRecipe(_recipeId);
        if (recipe.HasValue)
        {
            var currentQuality = CalculateCurrentQuality(recipe.Value);
            var maxQuality = CalculateMaxQuality(recipe.Value);
            
            ImGui.TextColored(new Vector4(0.3f, 0.9f, 0.9f, 1), "Ingredient Quality:");
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1.0f, 1.0f, 0.5f, 1), $"{currentQuality}/{maxQuality}");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Starting quality from HQ materials: {currentQuality}\nRecipe's maximum quality: {maxQuality}");
        }
        else
        {
            ImGui.TextColored(new Vector4(0.3f, 0.9f, 0.9f, 1), "Ingredient Quality:");
        }
        
        ImGui.Spacing();
        
        ImGui.Checkbox("Prefer NQ##ingredientNQ", ref _useAllNQ);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Use NQ materials by default, falling back to HQ only when there isn't enough NQ.\nPer-ingredient HQ amounts below still apply.");
        
        ImGui.Spacing();
        
        ImGui.BeginChild("IngredientsScrollRegion", new Vector2(0, 150), true);
        
        ImGui.Columns(5, "IngredientColumns", true);
        ImGui.SetColumnWidth(0, 150);
        ImGui.SetColumnWidth(1, 50);
        ImGui.SetColumnWidth(2, 50);
        ImGui.SetColumnWidth(3, 50);
        ImGui.SetColumnWidth(4, 100);
        
        ImGui.Text("Ingredient");
        ImGui.NextColumn();
        ImGui.Text("Need");
        ImGui.NextColumn();
        ImGui.Text("NQ");
        ImGui.NextColumn();
        ImGui.Text("HQ");
        ImGui.NextColumn();
        ImGui.Text("Use HQ");
        ImGui.NextColumn();
        ImGui.Separator();
        
        for (int i = 0; i < _ingredients.Count; i++)
        {
            var ing = _ingredients[i];
            
            ImGui.Text(ing.ItemName);
            ImGui.NextColumn();
            
            ImGui.Text(ing.TotalNeeded.ToString());
            ImGui.NextColumn();
            
            ImGui.Text(ing.AvailableNQ.ToString());
            ImGui.NextColumn();
            
            ImGui.Text(ing.AvailableHQ.ToString());
            ImGui.NextColumn();
            
            if (ing.CanBeHQ)
            {
                ImGui.SetNextItemWidth(80);
                int desiredHQ = ing.DesiredHQ;
                if (ImGui.InputInt($"##hq_{i}", ref desiredHQ, 1))
                {
                    ing.DesiredHQ = Math.Clamp(desiredHQ, 0, ing.TotalNeeded);
                }
            }
            else
            {
                ImGui.TextDisabled("N/A");
            }
            ImGui.NextColumn();
        }
        
        ImGui.Columns(1);
        ImGui.EndChild();
        ImGui.Spacing();
    }
    
    private static unsafe bool HasItemInInventory(uint itemId, bool hq)
    {
        try
        {
            var inventoryManager = InventoryManager.Instance();
            if (inventoryManager == null)
                return false;
            
            var inventories = new InventoryType[]
            {
                InventoryType.Inventory1, InventoryType.Inventory2,
                InventoryType.Inventory3, InventoryType.Inventory4
            };
            
            foreach (var invType in inventories)
            {
                var container = inventoryManager->GetInventoryContainer(invType);
                if (container == null) continue;
                
                for (int i = 0; i < container->Size; i++)
                {
                    var item = container->GetInventorySlot(i);
                    if (item == null || item->ItemId == 0) continue;
                    
                    if (item->ItemId == itemId)
                    {
                        bool itemIsHQ = (item->Flags & InventoryItem.ItemFlags.HighQuality) != 0;
                        if (hq == itemIsHQ && item->Quantity > 0)
                            return true;
                    }
                }
            }
            
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static unsafe (int nq, int hq) GetInventoryCountSplit(uint itemId)
    {
        try
        {
            var inventoryManager = InventoryManager.Instance();
            if (inventoryManager == null)
                return (0, 0);
            
            int nqCount = 0;
            int hqCount = 0;
            
            var inventories = new InventoryType[]
            {
                InventoryType.Inventory1, InventoryType.Inventory2,
                InventoryType.Inventory3, InventoryType.Inventory4
            };
            
            foreach (var invType in inventories)
            {
                var container = inventoryManager->GetInventoryContainer(invType);
                if (container == null) continue;
                
                for (int i = 0; i < container->Size; i++)
                {
                    var item = container->GetInventorySlot(i);
                    if (item == null || item->ItemId == 0) continue;
                    
                    if (item->ItemId == itemId)
                    {
                        if ((item->Flags & InventoryItem.ItemFlags.HighQuality) != 0)
                            hqCount += (int)item->Quantity;
                        else
                            nqCount += (int)item->Quantity;
                    }
                }
            }
            
            return (nqCount, hqCount);
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[RecipeCraftSettingsPopup] Error reading inventory: {ex.Message}");
            return (0, 0);
        }
    }
}
