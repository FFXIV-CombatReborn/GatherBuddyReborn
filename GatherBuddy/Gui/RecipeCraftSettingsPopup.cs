using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text;
using Dalamud.Interface.Colors;
using FFXIVClientStructs.FFXIV.Client.Game;
using GatherBuddy.Crafting;
using GatherBuddy.Vulcan;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;

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

    private List<(uint ItemId, string Name, bool IsHQ)> _foodItems = [];
    private List<(uint ItemId, string Name, bool IsHQ)> _medicineItems = [];
    private List<(uint ItemId, string Name)> _manualItems = [];
    private List<(uint ItemId, string Name)> _squadronManualItems = [];
    private string _foodSearch = string.Empty;
    private string _medicineSearch = string.Empty;
    private string _macroSearch = string.Empty;

    private MacroValidationResult? _validationResult;
    private string? _resolvedMacroId;
    private string? _lastValidatedMacroId;
    private uint? _lastValidatedFoodId;
    private bool _lastValidatedFoodHQ;
    private uint? _lastValidatedMedicineId;
    private bool _lastValidatedMedicineHQ;
    private int _lastValidatedStartingQuality;

    private static readonly string[] OverrideModeLabels = { "Inherit", "None", "Specific" };
    private static readonly string[] MacroModeLabels = { "Inherit", "Specific" };

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

    private readonly List<IngredientData> _ingredients = [];
    private bool _useAllNQ = false;
    public System.Action? OnSaved { get; set; }
    private static float SettingLabelWidth => VulcanUiScaling.Scaled(120f);
    private static float OverrideModeWidth => VulcanUiScaling.Scaled(90f);
    private static Vector2 ActionButtonSize => VulcanUiScaling.Scaled(100f, 0f);
    private static float IngredientNameColumnWidth => VulcanUiScaling.Scaled(150f);
    private static float IngredientCountColumnWidth => VulcanUiScaling.Scaled(50f);
    private static float IngredientUseHqColumnWidth => VulcanUiScaling.Scaled(100f);
    private static float IngredientInputWidth => VulcanUiScaling.Scaled(80f);


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
                IngredientPreferences = [with(existing.IngredientPreferences)],
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
            IngredientPreferences = cs != null ? [with(cs.IngredientPreferences)] : [],
            UseAllNQ = cs?.UseAllNQ ?? false,
            MacroMode = (cs != null && (cs.MacroMode == MacroOverrideMode.Specific || !string.IsNullOrEmpty(cs.SelectedMacroId) || cs.SolverOverride != SolverOverrideMode.Default))
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
            IngredientPreferences = cs != null ? [with(cs.IngredientPreferences)] : [],
            UseAllNQ = cs?.UseAllNQ ?? false,
            MacroMode = (cs != null && (cs.MacroMode == MacroOverrideMode.Specific || !string.IsNullOrEmpty(cs.SelectedMacroId) || cs.SolverOverride != SolverOverrideMode.Default))
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

        if (!_isOpen)
        {
            return;
        }

        ImGui.SetNextWindowSize(VulcanUiScaling.Scaled(450f, 450f), ImGuiCond.Appearing);
        ImGui.SetNextWindowSizeConstraints(VulcanUiScaling.Scaled(450f, 0f), new Vector2(float.MaxValue, float.MaxValue));

        if (ImGui.Begin($"Craft Settings - {_recipeName}###RecipeCraftSettings_{_instanceId}", ref _isOpen))
        {
            RefreshValidationIfNeeded();
            DrawMacroSelector();
            DrawMacroValidationStatus();
            DrawRaphaelValidationStatus();
            DrawFoodSelector();
            DrawMedicineSelector();
            DrawManualSelector();
            DrawSquadronManualSelector();
            DrawIngredientQuality();

            ImGui.Separator();
            ImGui.Spacing();

            if (ImGui.Button("Save", ActionButtonSize))
            {
                if (!string.IsNullOrEmpty(_resolvedMacroId))
                {
                    MacroValidator.Invalidate(_recipeId, _resolvedMacroId);
                }

                SaveIngredientPreferences();
                if (_editingListItem != null && _editingList != null)
                {
                    _editingListItem.CraftSettings = BuildPersistedSettings();
                    _editingListItem.IngredientPreferences.Clear();
                    GatherBuddy.CraftingListManager.SaveList(_editingList);
                }
                else if (_isPrecraftMode && _editingList != null)
                {
                    _editingList.SetPrecraftCraftSettings(_recipeId, BuildPersistedSettings());
                    GatherBuddy.CraftingListManager.SaveList(_editingList);
                }
                else
                {
                    _editingSettings = BuildPersistedSettings() ?? new RecipeCraftSettings();
                    GatherBuddy.RecipeBrowserSettings.Set(_recipeId, _editingSettings);
                    GatherBuddy.RecipeBrowserSettings.Save();
                }
                QueueCurrentRaphaelWarmup();
                OnSaved?.Invoke();
            }

            ImGui.SameLine();
            if (ImGui.Button("Clear All", ActionButtonSize))
            {
                _editingSettings.Clear();

                if (_editingListItem != null && _editingList != null)
                {
                    _editingListItem.CraftSettings = null;
                    _editingListItem.IngredientPreferences.Clear();
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
                QueueCurrentRaphaelWarmup();
                OnSaved?.Invoke();
            }

            ImGui.SameLine();
            if (ImGui.Button("Cancel", ActionButtonSize))
            {
                _isOpen = false;
            }

            ImGui.End();
        }

        if (!_isOpen && _activeInstanceId == _instanceId)
        {
            _activeInstanceId = 0;
        }
    }

    private void ResetValidationState()
    {
        _validationResult = null;
        _resolvedMacroId = null;
        _lastValidatedMacroId = null;
        _lastValidatedFoodId = null;
        _lastValidatedFoodHQ = false;
        _lastValidatedMedicineId = null;
        _lastValidatedMedicineHQ = false;
        _lastValidatedStartingQuality = -1;
        _macroSearch = string.Empty;
    }

    private RecipeCraftSettings BuildCurrentSettings()
    {
        var settings = _editingSettings.Clone();
        if (_editingListItem == null && !_isPrecraftMode)
        {
            settings.FoodMode = settings.FoodItemId.HasValue ? ConsumableOverrideMode.Specific : ConsumableOverrideMode.Inherit;
            settings.MedicineMode = settings.MedicineItemId.HasValue ? ConsumableOverrideMode.Specific : ConsumableOverrideMode.Inherit;
            settings.ManualMode = settings.ManualItemId.HasValue ? ConsumableOverrideMode.Specific : ConsumableOverrideMode.Inherit;
            settings.SquadronManualMode = settings.SquadronManualItemId.HasValue ? ConsumableOverrideMode.Specific : ConsumableOverrideMode.Inherit;
        }

        return settings;
    }

    private RecipeCraftSettings GetSettingsForEvaluation()
    {
        var settings = BuildCurrentSettings();
        var recipe = RecipeManager.GetRecipe(_recipeId);
        if (_editingList == null || !recipe.HasValue)
        {
            return settings;
        }

        return CraftingQualityPolicyResolver.BuildEffectiveSettings(recipe.Value, settings, _editingList.UseAllHQ) ?? settings;
    }

    private RecipeCraftSettings? BuildPersistedSettings()
    {
        var settings = BuildCurrentSettings();
        if (_editingListItem == null && !_isPrecraftMode)
        {
            settings.MacroMode = MacroOverrideMode.Specific;
        }

        if (!settings.HasAnySettings())
        {
            return null;
        }

        return new RecipeCraftSettings
        {
            FoodMode = settings.FoodMode,
            FoodItemId = settings.FoodMode == ConsumableOverrideMode.Specific ? settings.FoodItemId : null,
            FoodHQ = settings.FoodMode == ConsumableOverrideMode.Specific && settings.FoodItemId.HasValue && settings.FoodHQ,
            MedicineMode = settings.MedicineMode,
            MedicineItemId = settings.MedicineMode == ConsumableOverrideMode.Specific ? settings.MedicineItemId : null,
            MedicineHQ = settings.MedicineMode == ConsumableOverrideMode.Specific && settings.MedicineItemId.HasValue && settings.MedicineHQ,
            ManualMode = settings.ManualMode,
            ManualItemId = settings.ManualMode == ConsumableOverrideMode.Specific ? settings.ManualItemId : null,
            SquadronManualMode = settings.SquadronManualMode,
            SquadronManualItemId = settings.SquadronManualMode == ConsumableOverrideMode.Specific ? settings.SquadronManualItemId : null,
            IngredientPreferences = [with(settings.IngredientPreferences)],
            UseAllNQ = settings.UseAllNQ,
            MacroMode = settings.MacroMode,
            SelectedMacroId = settings.MacroMode == MacroOverrideMode.Specific ? settings.SelectedMacroId : null,
            SolverOverride = settings.MacroMode == MacroOverrideMode.Specific ? settings.SolverOverride : SolverOverrideMode.Default,
        };
    }

    private RaphaelAssessment GetRaphaelAssessment()
    {
        var evaluationSettings = GetSettingsForEvaluation();
        if (_isPrecraftMode && _editingList != null)
        {
            if (RaphaelAssessmentService.TryAssessListPrecraft(_recipeId, _editingList, evaluationSettings, out var precraftAssessment))
            {
                return precraftAssessment;
            }
        }
        else if (_editingList != null)
        {
            if (RaphaelAssessmentService.TryAssessListRecipe(_recipeId, _editingList, evaluationSettings, out var listAssessment))
            {
                return listAssessment;
            }
        }
        else if (RaphaelAssessmentService.TryAssessRecipe(_recipeId, evaluationSettings, out var recipeAssessment))
        {
            return recipeAssessment;
        }

        return new RaphaelAssessment(
            RaphaelAssessmentState.Unavailable,
            RaphaelAssessmentOutcome.None,
            "Raphael validation is unavailable.",
            "No usable stats are available for this recipe.");
    }

    private bool UsesRaphaelSolverForEdit()
    {
        if (_editingList == null)
        {
            var recipe = RecipeManager.GetRecipe(_recipeId);
            if (!recipe.HasValue)
            {
                return true;
            }

            var item = new CraftingListItem(_recipeId, 1)
            {
                IsOriginalRecipe = true,
                CraftSettings = GetSettingsForEvaluation(),
            };
            var recipeExecutionContext = CraftingContextResolver.ResolveExecutionContext(item, recipe.Value, null);
            return CraftingContextResolver.UsesRaphaelSolver(recipeExecutionContext);
        }

        if (_editingListItem != null)
        {
            return !CraftingContextResolver.TryResolveListExecutionContext(_editingList, _editingListItem, GetSettingsForEvaluation(), out var itemExecutionContext)
                || CraftingContextResolver.UsesRaphaelSolver(itemExecutionContext);
        }

        return !CraftingContextResolver.TryResolveListExecutionContext(_editingList, _recipeId, !_isPrecraftMode, GetSettingsForEvaluation(), out var listExecutionContext)
            || CraftingContextResolver.UsesRaphaelSolver(listExecutionContext);
    }

    private void DrawRaphaelValidationStatus()
    {
        SaveIngredientPreferences();
        if (!UsesRaphaelSolverForEdit())
        {
            ImGui.Spacing();
            ImGui.TextColored(ImGuiColors.DalamudGrey, "Raphael: inactive for current solver selection.");
            return;
        }
        var assessment = GetRaphaelAssessment();
        var color = assessment.State switch
        {
            RaphaelAssessmentState.Ready when assessment.Outcome is RaphaelAssessmentOutcome.FullQuality
                or RaphaelAssessmentOutcome.CollectibleTier3
                or RaphaelAssessmentOutcome.MinimumQualityMet
                or RaphaelAssessmentOutcome.NoQualityRequired
                => ImGuiColors.ParsedGreen,
            RaphaelAssessmentState.Ready => ImGuiColors.DalamudYellow,
            RaphaelAssessmentState.Generating => ImGuiColors.DalamudGrey,
            RaphaelAssessmentState.Failed => ImGuiColors.DalamudRed,
            RaphaelAssessmentState.Unavailable => ImGuiColors.DalamudYellow,
            _ => ImGuiColors.DalamudGrey,
        };

        ImGui.Spacing();
        ImGui.TextColored(color, $"Raphael: {assessment.Summary}");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(assessment.Details);
        }
    }

    private void QueueCurrentRaphaelWarmup()
    {
        var persistedSettings = BuildPersistedSettings();
        if (_isPrecraftMode && _editingList != null)
        {
            RaphaelAssessmentService.TryQueueWarmupForPrecraft(_recipeId, _editingList, persistedSettings);
            return;
        }

        if (_editingList != null)
        {
            RaphaelAssessmentService.TryQueueWarmupForListRecipe(_recipeId, _editingList, persistedSettings);
            return;
        }

        RaphaelAssessmentService.TryQueueWarmupForRecipe(_recipeId, persistedSettings);
    }

    private string? ResolveEffectiveMacroIdForEdit()
    {
        if (_editingListItem == null && !_isPrecraftMode)
        {
            return _editingSettings.SolverOverride == SolverOverrideMode.Default
                ? _editingSettings.SelectedMacroId
                : null;
        }

        if (_editingSettings.MacroMode == MacroOverrideMode.Specific)
        {
            return _editingSettings.SolverOverride == SolverOverrideMode.Default
                ? _editingSettings.SelectedMacroId
                : null;
        }

        var inheritedSolverOverride = _isPrecraftMode
            ? _editingList?.DefaultPrecraftSolverOverride ?? SolverOverrideMode.Default
            : _editingList?.DefaultFinalSolverOverride ?? SolverOverrideMode.Default;
        if (inheritedSolverOverride != SolverOverrideMode.Default)
        {
            return null;
        }

        return _isPrecraftMode ? _editingList?.DefaultPrecraftMacroId : _editingList?.DefaultFinalMacroId;
    }

    private void RefreshValidationIfNeeded()
    {
        SaveIngredientPreferences();
        var evaluationSettings = GetSettingsForEvaluation();
        var macroId = ResolveEffectiveMacroIdForEdit();
        _resolvedMacroId = macroId;
        var listConsumables = _editingList?.Consumables;
        var effectiveFoodId = evaluationSettings.FoodMode switch
        {
            ConsumableOverrideMode.Specific => evaluationSettings.FoodItemId,
            ConsumableOverrideMode.Inherit => listConsumables?.FoodItemId,
            _ => null,
        };
        var effectiveFoodHQ = evaluationSettings.FoodMode switch
        {
            ConsumableOverrideMode.Specific => evaluationSettings.FoodHQ,
            ConsumableOverrideMode.Inherit => listConsumables?.FoodHQ ?? false,
            _ => false,
        };
        var effectiveMedicineId = evaluationSettings.MedicineMode switch
        {
            ConsumableOverrideMode.Specific => evaluationSettings.MedicineItemId,
            ConsumableOverrideMode.Inherit => listConsumables?.MedicineItemId,
            _ => null,
        };
        var effectiveMedicineHQ = evaluationSettings.MedicineMode switch
        {
            ConsumableOverrideMode.Specific => evaluationSettings.MedicineHQ,
            ConsumableOverrideMode.Inherit => listConsumables?.MedicineHQ ?? false,
            _ => false,
        };

        var recipe = RecipeManager.GetRecipe(_recipeId);
        var startingQuality = recipe.HasValue
            ? CraftingQualityPolicyResolver.Resolve(recipe.Value, evaluationSettings).CalculateGuaranteedInitialQuality(recipe.Value)
            : 0;

        if (macroId == _lastValidatedMacroId
            && effectiveFoodId == _lastValidatedFoodId
            && effectiveFoodHQ == _lastValidatedFoodHQ
            && effectiveMedicineId == _lastValidatedMedicineId
            && effectiveMedicineHQ == _lastValidatedMedicineHQ
            && startingQuality == _lastValidatedStartingQuality)
        {
            return;
        }

        _lastValidatedMacroId = macroId;
        _lastValidatedFoodId = effectiveFoodId;
        _lastValidatedFoodHQ = effectiveFoodHQ;
        _lastValidatedMedicineId = effectiveMedicineId;
        _lastValidatedMedicineHQ = effectiveMedicineHQ;
        _lastValidatedStartingQuality = startingQuality;

        if (string.IsNullOrEmpty(macroId))
        {
            _validationResult = null;
            return;
        }

        MacroValidator.Invalidate(_recipeId, macroId);
        _validationResult = MacroValidator.GetOrCompute(_recipeId, macroId, evaluationSettings, listConsumables);
        GatherBuddy.Log.Debug($"[RecipeCraftSettingsPopup] Validation refreshed: {(_validationResult == null ? "null" : _validationResult.IsValid ? "PASS" : $"FAIL ({_validationResult.Failure})")}");
    }

    private void DrawMacroValidationStatus()
    {
        if (string.IsNullOrEmpty(_resolvedMacroId))
        {
            return;
        }

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
            {
                ImGui.SetTooltip(
                    $"Macro will complete the craft.\n" +
                    $"Progress: {_validationResult.FinalProgress}/{_validationResult.RequiredProgress}\n" +
                    $"Quality: {_validationResult.FinalQuality}\n" +
                    $"Remaining Durability: {_validationResult.FinalDurability}" +
                    (_validationResult.SkippedConditionGatedCount > 0
                        ? $"\nSkipped {_validationResult.SkippedConditionGatedCount} condition-gated action(s)"
                        : ""));
            }
        }
        else
        {
            var (color, label) = _validationResult.Failure switch
            {
                MacroValidationFailure.CPExhausted => (ImGuiColors.DalamudRed, "FAIL \u2014 CP exhausted"),
                MacroValidationFailure.DurabilityFailed => (ImGuiColors.DalamudRed, "FAIL \u2014 durability broke"),
                MacroValidationFailure.InsufficientProgress => (ImGuiColors.DalamudYellow, "WARN \u2014 insufficient progress"),
                MacroValidationFailure.ActionUnusable => (ImGuiColors.DalamudYellow, "WARN \u2014 action unusable"),
                _ => (ImGuiColors.DalamudRed, "FAIL"),
            };
            ImGui.TextColored(color, $"Validation: {label} (step {_validationResult.FailedAtStep})");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    $"Progress: {_validationResult.FinalProgress}/{_validationResult.RequiredProgress}\n" +
                    $"Quality: {_validationResult.FinalQuality}\n" +
                    $"Remaining Durability: {_validationResult.FinalDurability}");
            }
        }
    }

    private void DrawMacroSelector()
    {
        ImGui.AlignTextToFramePadding();
        ImGui.Text("Macro:");
        ImGui.SameLine(SettingLabelWidth);

        var allMacros = CraftingGameInterop.UserMacroLibrary.GetAllMacros();

        if (_editingListItem != null || _isPrecraftMode)
        {
            var mode = (int)_editingSettings.MacroMode;
            ImGui.SetNextItemWidth(OverrideModeWidth);
            if (ImGui.Combo("##MacroMode", ref mode, MacroModeLabels, MacroModeLabels.Length))
            {
                _editingSettings.MacroMode = (MacroOverrideMode)mode;
            }

            if (_editingSettings.MacroMode == MacroOverrideMode.Inherit)
            {
                ImGui.SameLine();
                var inheritedId = _isPrecraftMode ? _editingList?.DefaultPrecraftMacroId : _editingList?.DefaultFinalMacroId;
                var inheritedSolverOverride = _isPrecraftMode
                    ? _editingList?.DefaultPrecraftSolverOverride ?? SolverOverrideMode.Default
                    : _editingList?.DefaultFinalSolverOverride ?? SolverOverrideMode.Default;
                var inheritedName = GetMacroSelectionName(inheritedId, inheritedSolverOverride, allMacros);
                ImGui.TextColored(new Vector4(0.6f, 0.8f, 1f, 1f), $"List: {inheritedName}");
                return;
            }

            ImGui.SameLine();
        }

        DrawMacroCombo(allMacros);
    }

    private void DrawMacroCombo(System.Collections.Generic.List<UserMacro> allMacros)
    {
        var currentMacroName = GetMacroSelectionName(_editingSettings.SelectedMacroId, _editingSettings.SolverOverride, allMacros);

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
            {
                ImGui.SetTooltip("Use only progress-building actions, no quality steps. Fast NQ crafts.");
            }

            if (allMacros.Count > 0)
            {
                ImGui.Separator();
                ImGui.InputTextWithHint("##MacroSearch", "Search macros...", ref _macroSearch, 128);
                var filteredMacros = string.IsNullOrWhiteSpace(_macroSearch)
                    ? allMacros
                    : [.. allMacros.Where(m => m.Name.Contains(_macroSearch, StringComparison.OrdinalIgnoreCase))];
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

    private static string GetMacroSelectionName(string? macroId, SolverOverrideMode solverOverride, List<UserMacro> allMacros)
    {
        return solverOverride switch
        {
            SolverOverrideMode.StandardSolver => "Standard Solver",
            SolverOverrideMode.RaphaelSolver => "Raphael Solver",
            SolverOverrideMode.ProgressOnlySolver => "Progress Only",
            _ when !string.IsNullOrEmpty(macroId) => allMacros.FirstOrDefault(m => m.Id == macroId)?.Name ?? "(Macro Not Found)",
            _ => "Default (Use Solver)",
        };
    }

    private void DrawFoodSelector()
    {
        ImGui.AlignTextToFramePadding();
        ImGui.Text("Food:");
        ImGui.SameLine(SettingLabelWidth);

        if (_editingListItem != null || _isPrecraftMode)
        {
            var mode = (int)_editingSettings.FoodMode;
            ImGui.SetNextItemWidth(OverrideModeWidth);
            if (ImGui.Combo("##FoodMode", ref mode, OverrideModeLabels, OverrideModeLabels.Length))
            {
                _editingSettings.FoodMode = (ConsumableOverrideMode)mode;
            }

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
                : [.. _foodItems.Where(f => f.Name.Contains(_foodSearch, StringComparison.OrdinalIgnoreCase))];
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
        ImGui.SameLine(SettingLabelWidth);

        if (_editingListItem != null || _isPrecraftMode)
        {
            var mode = (int)_editingSettings.MedicineMode;
            ImGui.SetNextItemWidth(OverrideModeWidth);
            if (ImGui.Combo("##MedicineMode", ref mode, OverrideModeLabels, OverrideModeLabels.Length))
            {
                _editingSettings.MedicineMode = (ConsumableOverrideMode)mode;
            }

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
                : [.. _medicineItems.Where(m => m.Name.Contains(_medicineSearch, StringComparison.OrdinalIgnoreCase))];
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
        ImGui.SameLine(SettingLabelWidth);

        if (_editingListItem != null || _isPrecraftMode)
        {
            var mode = (int)_editingSettings.ManualMode;
            ImGui.SetNextItemWidth(OverrideModeWidth);
            if (ImGui.Combo("##ManualMode", ref mode, OverrideModeLabels, OverrideModeLabels.Length))
            {
                _editingSettings.ManualMode = (ConsumableOverrideMode)mode;
            }

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
            {
                _editingSettings.ManualItemId = null;
            }

            foreach (var (itemId, name) in _manualItems)
            {
                if (ImGui.Selectable(name, itemId == manualId))
                {
                    _editingSettings.ManualItemId = itemId;
                }
            }
            ImGui.EndCombo();
        }
    }

    private void DrawSquadronManualSelector()
    {
        ImGui.AlignTextToFramePadding();
        ImGui.Text("Squadron Manual:");
        ImGui.SameLine(SettingLabelWidth);

        if (_editingListItem != null || _isPrecraftMode)
        {
            var mode = (int)_editingSettings.SquadronManualMode;
            ImGui.SetNextItemWidth(OverrideModeWidth);
            if (ImGui.Combo("##SquadronMode", ref mode, OverrideModeLabels, OverrideModeLabels.Length))
            {
                _editingSettings.SquadronManualMode = (ConsumableOverrideMode)mode;
            }

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
            {
                _editingSettings.SquadronManualItemId = null;
            }

            foreach (var (itemId, name) in _squadronManualItems)
            {
                if (ImGui.Selectable(name, itemId == squadronId))
                {
                    _editingSettings.SquadronManualItemId = itemId;
                }
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
        {
            return;
        }

        foreach (var item in itemSheet)
        {
            if (IsCraftersFood(item))
            {
                var name = item.Name.ExtractText();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    _foodItems.Add((item.RowId, name, false));
                    if (item.CanBeHq)
                    {
                        _foodItems.Add((item.RowId, name, true));
                    }
                }
            }
            else if (IsCraftersPot(item))
            {
                var name = item.Name.ExtractText();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    _medicineItems.Add((item.RowId, name, false));
                    if (item.CanBeHq)
                    {
                        _medicineItems.Add((item.RowId, name, true));
                    }
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

        _foodItems = [.. _foodItems
            .OrderBy(f => !HasItemInInventory(f.ItemId, f.IsHQ))
            .ThenBy(f => f.Name)
            .ThenBy(f => f.IsHQ ? 0 : 1)];
        _medicineItems = [.. _medicineItems
            .OrderBy(m => !HasItemInInventory(m.ItemId, m.IsHQ))
            .ThenBy(m => m.Name)
            .ThenBy(m => m.IsHQ ? 0 : 1)];
        _manualItems = [.. _manualItems
            .OrderBy(m => !HasItemInInventory(m.ItemId, false))
            .ThenBy(m => m.Name)];
        _squadronManualItems = [.. _squadronManualItems
            .OrderBy(m => !HasItemInInventory(m.ItemId, false))
            .ThenBy(m => m.Name)];
    }

    private static ItemFood? GetItemConsumableProperties(Item item, bool hq)
    {
        if (!item.ItemAction.IsValid)
        {
            return null;
        }

        var action = item.ItemAction.Value;
        var actionParams = hq ? action.DataHQ : action.Data;
        if (actionParams[0] is not 48 and not 49)
        {
            return null;
        }

        return Dalamud.GameData.GetExcelSheet<ItemFood>()?.GetRow(actionParams[1]);
    }

    private static bool IsCraftersFood(Item item)
    {
        if (item.ItemUICategory.RowId != 46)
        {
            return false;
        }

        var consumable = GetItemConsumableProperties(item, false);
        return consumable != null && consumable.Value.Params.Any(p => p.BaseParam.RowId is 11 or 70 or 71);
    }

    private static bool IsCraftersPot(Item item)
    {
        if (item.ItemUICategory.RowId != 44)
        {
            return false;
        }

        var consumable = GetItemConsumableProperties(item, false);
        return consumable != null && consumable.Value.Params.Any(p => p.BaseParam.RowId is 11 or 70 or 71 or 69 or 68);
    }

    private static bool IsCraftersManual(Item item)
    {
        if (item.ItemUICategory.RowId != 63)
        {
            return false;
        }

        if (!item.ItemAction.IsValid)
        {
            return false;
        }

        var action = item.ItemAction.Value;
        return action.Action.RowId == 816 && action.Data[0] is 300 or 301 or 1751 or 5329;
    }

    private static bool IsSquadronManual(Item item)
    {
        if (item.ItemUICategory.RowId != 63)
        {
            return false;
        }

        if (!item.ItemAction.IsValid)
        {
            return false;
        }

        var action = item.ItemAction.Value;
        return action.Action.RowId == 816 && action.Data[0] is 2291 or 2292 or 2293 or 2294;
    }

    private string GetItemName(uint itemId)
    {
        var itemSheet = Dalamud.GameData.GetExcelSheet<Item>();
        if (itemSheet != null && itemSheet.TryGetRow(itemId, out var item))
        {
            return item.Name.ExtractText();
        }

        return "Unknown";
    }

    private void LoadIngredients()
    {
        _ingredients.Clear();
        var recipe = RecipeManager.GetRecipe(_recipeId);
        if (!recipe.HasValue)
        {
            return;
        }

        var qualityDisplaySettings = _editingList != null
            ? CraftingQualityPolicyResolver.BuildEffectiveSettings(recipe.Value, _editingSettings, _editingList.UseAllHQ) ?? _editingSettings.Clone()
            : _editingSettings.Clone();
        _useAllNQ = qualityDisplaySettings.UseAllNQ;

        var ingredients = RecipeManager.GetIngredients(recipe.Value);
        foreach (var (itemId, amount) in ingredients)
        {
            var item = Dalamud.GameData.GetExcelSheet<Item>()?.GetRow(itemId);
            if (item == null)
            {
                continue;
            }

            var (availNQ, availHQ) = GetInventoryCountSplit(itemId);
            var desiredHQ = qualityDisplaySettings.IngredientPreferences.GetValueOrDefault(itemId, 0);
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
            {
                _editingSettings.IngredientPreferences[ing.ItemId] = ing.DesiredHQ;
            }
        }
        _editingSettings.UseAllNQ = _useAllNQ;

        var recipe = RecipeManager.GetRecipe(_recipeId);
        if (_editingList != null
            && _editingList.UseAllHQ
            && !_editingSettings.UseAllNQ
            && recipe.HasValue
            && CraftingQualityPolicyResolver.MatchesAllHQPreferences(recipe.Value, _editingSettings.IngredientPreferences))
        {
            _editingSettings.IngredientPreferences.Clear();
        }
    }

    private int CalculateCurrentQuality(Recipe recipe)
    {
        var preferences = new Dictionary<uint, int>();
        foreach (var ing in _ingredients)
        {
            if (ing.DesiredHQ > 0)
            {
                preferences[ing.ItemId] = ing.DesiredHQ;
            }
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
        {
            return;
        }

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
            {
                ImGui.SetTooltip($"Starting quality from HQ materials: {currentQuality}\nRecipe's maximum quality: {maxQuality}");
            }
        }
        else
        {
            ImGui.TextColored(new Vector4(0.3f, 0.9f, 0.9f, 1), "Ingredient Quality:");
        }

        ImGui.Spacing();

        ImGui.Checkbox("Prefer NQ##ingredientNQ", ref _useAllNQ);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Use NQ materials by default, falling back to HQ only when there isn't enough NQ.\nPer-ingredient HQ amounts below still apply.");
        }

        if (_editingList != null && _editingList.UseAllHQ)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "List default is All HQ.\nChange the ingredient counts below to override this item.");
        }

        ImGui.Spacing();

        ImGui.BeginChild("IngredientsScrollRegion", VulcanUiScaling.Scaled(-1f, 150f), true);

        ImGui.Columns(5, "IngredientColumns", true);
        ImGui.SetColumnWidth(0, IngredientNameColumnWidth);
        ImGui.SetColumnWidth(1, IngredientCountColumnWidth);
        ImGui.SetColumnWidth(2, IngredientCountColumnWidth);
        ImGui.SetColumnWidth(3, IngredientCountColumnWidth);
        ImGui.SetColumnWidth(4, IngredientUseHqColumnWidth);

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

        for (var i = 0; i < _ingredients.Count; i++)
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
                ImGui.SetNextItemWidth(IngredientInputWidth);
                var desiredHQ = ing.DesiredHQ;
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
            return inventoryManager != null
                && inventoryManager->GetInventoryItemCount(itemId, hq, false, false) > 0;
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
            {
                return (0, 0);
            }

            var nqCount = 0;
            var hqCount = 0;

            var inventories = new InventoryType[]
            {
                InventoryType.Inventory1, InventoryType.Inventory2,
                InventoryType.Inventory3, InventoryType.Inventory4
            };

            foreach (var invType in inventories)
            {
                var container = inventoryManager->GetInventoryContainer(invType);
                if (container == null)
                {
                    continue;
                }

                for (var i = 0; i < container->Size; i++)
                {
                    var item = container->GetInventorySlot(i);
                    if (item == null || item->ItemId == 0)
                    {
                        continue;
                    }

                    if (item->ItemId == itemId)
                    {
                        if ((item->Flags & InventoryItem.ItemFlags.HighQuality) != 0)
                        {
                            hqCount += (int)item->Quantity;
                        }
                        else
                        {
                            nqCount += (int)item->Quantity;
                        }
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
