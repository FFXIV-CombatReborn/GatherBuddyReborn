using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text;
using Dalamud.Interface;
using FFXIVClientStructs.FFXIV.Client.Game;
using GatherBuddy.Crafting;
using GatherBuddy.Vulcan;
using Lumina.Excel.Sheets;
using RebornMaterial;
using System;
using System.Collections.Generic;
using System.Numerics;
using ImRaii = Dalamud.Interface.Utility.Raii.ImRaii;

namespace GatherBuddy.Gui;

public class RecipeCraftSettingsPopup
{
    private uint _recipeId;
    private string _recipeName = string.Empty;
    private RecipeCraftSettings _editingSettings = new();
    private CraftingListItem? _editingListItem;
    private CraftingListDefinition? _editingList;
    private bool _isPrecraftMode;

    private List<(uint ItemId, string Name, bool IsHQ)> _foodItems = [];
    private List<(uint ItemId, string Name, bool IsHQ)> _medicineItems = [];
    private List<(uint ItemId, string Name)> _manualItems = [];
    private List<(uint ItemId, string Name)> _squadronManualItems = [];

    private MacroValidationResult? _validationResult;
    private string? _resolvedMacroId;
    private string? _lastValidatedMacroId;
    private uint? _lastValidatedFoodId;
    private bool _lastValidatedFoodHQ;
    private uint? _lastValidatedMedicineId;
    private bool _lastValidatedMedicineHQ;
    private int _lastValidatedStartingQuality;


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

    internal static RebornUI.HostedPanelWindow CreateWindow()
        => new("GatherBuddyRecipeCraftSettings", "Craft Settings", new Vector2(500f, 600f), new Vector2(460f, 320f), bodyScrolls: false);

    private void Activate()
    {
        GatherBuddy.RecipeCraftSettingsWindow?.Show(this, DrawContents, () => Subtitle);
    }

    public void Close()
    {
        GatherBuddy.RecipeCraftSettingsWindow?.Hide(this);
    }

    private string Subtitle
        => _editingList == null ? _recipeName
            : _isPrecraftMode ? $"{_recipeName} · precraft in {_editingList.Name}"
            : $"{_recipeName} · {_editingList.Name}";

    private readonly List<IngredientData> _ingredients = [];
    private bool _useAllNQ = false;
    public System.Action? OnSaved { get; set; }
    private static float SettingLabelWidth => VulcanUiScaling.Scaled(130f);
    private static float IngredientCountColumnWidth => VulcanUiScaling.Scaled(46f);
    private static float IngredientUseHqColumnWidth => VulcanUiScaling.Scaled(130f);
    private static float IngredientInputWidth => VulcanUiScaling.Scaled(120f);

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
    }

    private void DrawContents()
    {
        RefreshValidationIfNeeded();
        var footer = M3Widgets.ButtonHeight + M3.Space1 + ImGui.GetStyle().ItemSpacing.Y * 2f;
        using (var body = ImRaii.Child("##craftSettingsBody", new Vector2(-1, -footer), false))
        {
            if (body)
            {
                using (M3Card.Begin("##craftSolver", "Macro or solver", FontAwesomeIcon.Scroll, M3.AccentFor(0)))
                {
                    DrawMacroSelector();
                    DrawMacroValidationStatus();
                    DrawRaphaelValidationStatus();
                }

                using (M3Card.Begin("##craftConsumables", "Consumables", FontAwesomeIcon.Utensils, M3.AccentFor(1)))
                {
                    DrawFoodSelector();
                    DrawMedicineSelector();
                    DrawManualSelector();
                    DrawSquadronManualSelector();
                }

                DrawIngredientQuality();
            }
        }

        ImGui.Dummy(new Vector2(0, M3.Space1));
        if (M3Widgets.Button("##save", "Save", M3ButtonStyle.Filled, FontAwesomeIcon.Save))
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
            M3Snackbar.Show($"Saved the craft settings for {_recipeName}.");
        }

        ImGui.SameLine(0, M3.Space2);
        if (M3Widgets.Button("##clearAll", "Clear all", M3ButtonStyle.Text, FontAwesomeIcon.Eraser,
                tooltip: "Remove every setting for this recipe."))
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

        ImGui.SameLine(0, M3.Space2);
        if (M3Widgets.Button("##cancel", "Close", M3ButtonStyle.Text))
        {
            Close();
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
            M3Text.Draw("Raphael isn't used with this choice.", M3TextStyle.Faint);
            return;
        }

        var assessment = GetRaphaelAssessment();
        var color = assessment.State switch
        {
            RaphaelAssessmentState.Ready when assessment.Outcome is RaphaelAssessmentOutcome.FullQuality
                or RaphaelAssessmentOutcome.CollectibleTier3
                or RaphaelAssessmentOutcome.MinimumQualityMet
                or RaphaelAssessmentOutcome.NoQualityRequired
                => M3.Scheme.Success,
            RaphaelAssessmentState.Ready => M3.Scheme.Warning,
            RaphaelAssessmentState.Failed => M3.Scheme.Error,
            RaphaelAssessmentState.Unavailable => M3.Scheme.Warning,
            _ => M3.Scheme.Outline,
        };

        M3Badge.Draw($"Raphael: {assessment.Summary}", color, assessment.Details);
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

        if (_validationResult == null || _validationResult.Failure == MacroValidationFailure.NoStats)
        {
            M3Text.Draw("Validation: no gear stats are available for this job.", M3TextStyle.Faint);
            return;
        }

        var details = $"Progress: {_validationResult.FinalProgress}/{_validationResult.RequiredProgress}\n"
          + $"Quality: {_validationResult.FinalQuality}\n"
          + $"Remaining durability: {_validationResult.FinalDurability}";
        if (_validationResult.IsValid)
        {
            M3Badge.Draw("Validation: passes", M3.Scheme.Success, "The macro completes the craft.\n" + details
              + (_validationResult.SkippedConditionGatedCount > 0
                    ? $"\nSkipped {_validationResult.SkippedConditionGatedCount} condition-gated actions"
                    : string.Empty));
            return;
        }

        var (color, label) = _validationResult.Failure switch
        {
            MacroValidationFailure.CPExhausted => (M3.Scheme.Error, "fails, CP runs out"),
            MacroValidationFailure.DurabilityFailed => (M3.Scheme.Error, "fails, durability breaks"),
            MacroValidationFailure.InsufficientProgress => (M3.Scheme.Warning, "warning, not enough progress"),
            MacroValidationFailure.ActionUnusable => (M3.Scheme.Warning, "warning, an action can't be used"),
            _ => (M3.Scheme.Error, "fails"),
        };
        M3Badge.Draw($"Validation: {label} at step {_validationResult.FailedAtStep}", color, details);
    }

    private static readonly M3Segment[] MacroModeSegments = [new("Inherit", FontAwesomeIcon.LevelDownAlt, "Use the list's choice"), new("Specific", FontAwesomeIcon.Check)];

    private static readonly M3Segment[] OverrideModeSegments =
    [
        new("Inherit", FontAwesomeIcon.LevelDownAlt, "Use the list's choice"),
        new("None", FontAwesomeIcon.Ban, "Use nothing"),
        new("Specific", FontAwesomeIcon.Check),
    ];

    private bool InListContext
        => _editingListItem != null || _isPrecraftMode;

    private static void SettingLabel(string label)
    {
        var height = MathF.Max(M3Widgets.ComboHeight, M3Widgets.SegmentedHeight);
        var start = ImGui.GetCursorPosY();
        ImGui.SetCursorPosY(start + (height - ImGui.GetTextLineHeight()) * 0.5f);
        ImGui.TextUnformatted(label);
        ImGui.SameLine(SettingLabelWidth);
        ImGui.SetCursorPosY(start);
    }

    private static bool DrawModeChoice(string id, ref int mode, M3Segment[] segments, string inheritedLabel, int specificIndex)
    {
        var picked = M3Widgets.SegmentedButtons(id, segments, mode, M3Widgets.SegmentedWidth(segments));
        if (picked != mode)
        {
            mode = picked;
        }

        if (mode == 0)
        {
            ImGui.SameLine(0, M3.Space2);
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (M3Widgets.SegmentedHeight - ImGui.GetTextLineHeight()) * 0.5f);
            M3Text.Draw($"List: {inheritedLabel}", M3TextStyle.Muted);
            return false;
        }

        if (mode != specificIndex)
        {
            return false;
        }

        ImGui.SetCursorPosX(SettingLabelWidth);
        return true;
    }

    private void DrawMacroSelector()
    {
        SettingLabel("Macro");
        var allMacros = CraftingGameInterop.UserMacroLibrary.GetAllMacros();
        if (InListContext)
        {
            var mode = (int)_editingSettings.MacroMode;
            var inheritedId = _isPrecraftMode ? _editingList?.DefaultPrecraftMacroId : _editingList?.DefaultFinalMacroId;
            var inheritedSolverOverride = _isPrecraftMode
                ? _editingList?.DefaultPrecraftSolverOverride ?? SolverOverrideMode.Default
                : _editingList?.DefaultFinalSolverOverride ?? SolverOverrideMode.Default;
            var specific = DrawModeChoice("##MacroMode", ref mode, MacroModeSegments, GetMacroSelectionName(inheritedId, inheritedSolverOverride, allMacros), 1);
            _editingSettings.MacroMode = (MacroOverrideMode)mode;
            if (!specific)
            {
                return;
            }
        }

        DrawMacroCombo(allMacros);
    }

    private static readonly (string Label, SolverOverrideMode Mode, string? Tooltip)[] SolverChoices =
    [
        ("Default, use the solver", SolverOverrideMode.Default, null),
        ("Standard solver", SolverOverrideMode.StandardSolver, null),
        ("Raphael solver", SolverOverrideMode.RaphaelSolver, null),
        ("Progress only", SolverOverrideMode.ProgressOnlySolver, "Only progress-building actions, no quality steps. Fast NQ crafts."),
    ];

    private void DrawMacroCombo(List<UserMacro> allMacros)
    {
        int index;
        if (!string.IsNullOrEmpty(_editingSettings.SelectedMacroId) && _editingSettings.SolverOverride == SolverOverrideMode.Default)
        {
            var macroIndex = allMacros.FindIndex(m => m.Id == _editingSettings.SelectedMacroId);
            index = macroIndex < 0 ? -1 : SolverChoices.Length + macroIndex;
        }
        else
        {
            index = Array.FindIndex(SolverChoices, c => c.Mode == _editingSettings.SolverOverride);
        }

        string Label(int i)
        {
            if (i < SolverChoices.Length)
            {
                return SolverChoices[i].Label;
            }

            var macro = allMacros[i - SolverChoices.Length];
            return macro.MinCraftsmanship > 0 || macro.MinControl > 0 || macro.MinCP > 0
                ? $"{macro.Name} ({macro.MinCraftsmanship}/{macro.MinControl}/{macro.MinCP})"
                : macro.Name;
        }

        if (!M3Widgets.Combo("##MacroSelector", ref index, SolverChoices.Length + allMacros.Count, Label, ImGui.GetContentRegionAvail().X - M3Card.RightInset,
                GetMacroSelectionName(_editingSettings.SelectedMacroId, _editingSettings.SolverOverride, allMacros), allMacros.Count > 6,
                itemTooltip: i => i < SolverChoices.Length ? SolverChoices[i].Tooltip : null))
        {
            return;
        }

        if (index < SolverChoices.Length)
        {
            _editingSettings.SelectedMacroId = null;
            _editingSettings.SolverOverride = SolverChoices[index].Mode;
        }
        else
        {
            _editingSettings.SelectedMacroId = allMacros[index - SolverChoices.Length].Id;
            _editingSettings.SolverOverride = SolverOverrideMode.Default;
        }
    }

    private static string GetMacroSelectionName(string? macroId, SolverOverrideMode solverOverride, List<UserMacro> allMacros)
        => solverOverride switch
        {
            SolverOverrideMode.StandardSolver => "Standard solver",
            SolverOverrideMode.RaphaelSolver => "Raphael solver",
            SolverOverrideMode.ProgressOnlySolver => "Progress only",
            _ when !string.IsNullOrEmpty(macroId) => allMacros.Find(m => m.Id == macroId)?.Name ?? "(macro not found)",
            _ => "Default, use the solver",
        };

    private static string ItemLabel(string name, bool hq)
        => hq ? $"{name} {(char)SeIconChar.HighQuality}" : name;

    private static bool DrawConsumableCombo(string id, List<(uint ItemId, string Name, bool IsHQ)> items, ref uint? itemId, ref bool hq)
    {
        var current = itemId;
        var currentHq = hq;
        var index = current.HasValue ? items.FindIndex(i => i.ItemId == current.Value && i.IsHQ == currentHq) + 1 : 0;
        if (!M3Widgets.Combo(id, ref index, items.Count + 1, i => i == 0 ? "None" : ItemLabel(items[i - 1].Name, items[i - 1].IsHQ),
                ImGui.GetContentRegionAvail().X - M3Card.RightInset, "None", items.Count > 8))
        {
            return false;
        }

        if (index <= 0)
        {
            itemId = null;
            hq = false;
        }
        else
        {
            itemId = items[index - 1].ItemId;
            hq = items[index - 1].IsHQ;
        }

        return true;
    }

    private static bool DrawManualCombo(string id, List<(uint ItemId, string Name)> items, ref uint? itemId)
    {
        var current = itemId ?? 0;
        var index = current == 0 ? 0 : items.FindIndex(i => i.ItemId == current) + 1;
        if (!M3Widgets.Combo(id, ref index, items.Count + 1, i => i == 0 ? "None" : items[i - 1].Name, ImGui.GetContentRegionAvail().X - M3Card.RightInset, "None"))
        {
            return false;
        }

        itemId = index <= 0 ? null : items[index - 1].ItemId;
        return true;
    }

    private void DrawFoodSelector()
    {
        SettingLabel("Food");
        if (InListContext)
        {
            var mode = (int)_editingSettings.FoodMode;
            var specific = DrawModeChoice("##FoodMode", ref mode, OverrideModeSegments,
                GetConsumableDisplayName(_editingList?.Consumables.FoodItemId, _editingList?.Consumables.FoodHQ ?? false), 2);
            _editingSettings.FoodMode = (ConsumableOverrideMode)mode;
            if (!specific)
            {
                return;
            }
        }

        var foodId = _editingSettings.FoodItemId;
        var foodHq = _editingSettings.FoodHQ;
        if (DrawConsumableCombo("##FoodSelector", _foodItems, ref foodId, ref foodHq))
        {
            _editingSettings.FoodItemId = foodId;
            _editingSettings.FoodHQ = foodHq;
        }
    }

    private void DrawMedicineSelector()
    {
        SettingLabel("Medicine");
        if (InListContext)
        {
            var mode = (int)_editingSettings.MedicineMode;
            var specific = DrawModeChoice("##MedicineMode", ref mode, OverrideModeSegments,
                GetConsumableDisplayName(_editingList?.Consumables.MedicineItemId, _editingList?.Consumables.MedicineHQ ?? false), 2);
            _editingSettings.MedicineMode = (ConsumableOverrideMode)mode;
            if (!specific)
            {
                return;
            }
        }

        var medicineId = _editingSettings.MedicineItemId;
        var medicineHq = _editingSettings.MedicineHQ;
        if (DrawConsumableCombo("##MedicineSelector", _medicineItems, ref medicineId, ref medicineHq))
        {
            _editingSettings.MedicineItemId = medicineId;
            _editingSettings.MedicineHQ = medicineHq;
        }
    }

    private void DrawManualSelector()
    {
        SettingLabel("Manual");
        if (InListContext)
        {
            var mode = (int)_editingSettings.ManualMode;
            var listManualId = _editingList?.Consumables.ManualItemId;
            var specific = DrawModeChoice("##ManualMode", ref mode, OverrideModeSegments, listManualId.HasValue ? GetItemName(listManualId.Value) : "None", 2);
            _editingSettings.ManualMode = (ConsumableOverrideMode)mode;
            if (!specific)
            {
                return;
            }
        }

        var manualId = _editingSettings.ManualItemId;
        if (DrawManualCombo("##ManualSelector", _manualItems, ref manualId))
        {
            _editingSettings.ManualItemId = manualId;
        }
    }

    private void DrawSquadronManualSelector()
    {
        SettingLabel("Squadron manual");
        if (InListContext)
        {
            var mode = (int)_editingSettings.SquadronManualMode;
            var listSquadronId = _editingList?.Consumables.SquadronManualItemId;
            var specific = DrawModeChoice("##SquadronMode", ref mode, OverrideModeSegments, listSquadronId.HasValue ? GetItemName(listSquadronId.Value) : "None", 2);
            _editingSettings.SquadronManualMode = (ConsumableOverrideMode)mode;
            if (!specific)
            {
                return;
            }
        }

        var squadronId = _editingSettings.SquadronManualItemId;
        if (DrawManualCombo("##SquadronManualSelector", _squadronManualItems, ref squadronId))
        {
            _editingSettings.SquadronManualItemId = squadronId;
        }
    }

    private static string GetConsumableDisplayName(uint? itemId, bool hq)
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

        _foodItems = SortConsumables(_foodItems);
        _medicineItems = SortConsumables(_medicineItems);
        _manualItems = SortConsumables(_manualItems);
        _squadronManualItems = SortConsumables(_squadronManualItems);
    }

    private static List<(uint ItemId, string Name, bool IsHQ)> SortConsumables(List<(uint ItemId, string Name, bool IsHQ)> items)
    {
        var keyedItems = new List<((uint ItemId, string Name, bool IsHQ) Item, bool Missing, int Index)>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            keyedItems.Add((items[i], !HasItemInInventory(items[i].ItemId, items[i].IsHQ), i));
        }

        keyedItems.Sort(static (lhs, rhs) =>
        {
            var order = lhs.Missing.CompareTo(rhs.Missing);
            if (order != 0)
            {
                return order;
            }

            order = string.Compare(lhs.Item.Name, rhs.Item.Name, StringComparison.CurrentCulture);
            if (order != 0)
            {
                return order;
            }

            order = (lhs.Item.IsHQ ? 0 : 1).CompareTo(rhs.Item.IsHQ ? 0 : 1);
            return order != 0 ? order : lhs.Index.CompareTo(rhs.Index);
        });
        return keyedItems.ConvertAll(static keyed => keyed.Item);
    }

    private static List<(uint ItemId, string Name)> SortConsumables(List<(uint ItemId, string Name)> items)
    {
        var keyedItems = new List<((uint ItemId, string Name) Item, bool Missing, int Index)>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            keyedItems.Add((items[i], !HasItemInInventory(items[i].ItemId, false), i));
        }

        keyedItems.Sort(static (lhs, rhs) =>
        {
            var order = lhs.Missing.CompareTo(rhs.Missing);
            if (order != 0)
            {
                return order;
            }

            order = string.Compare(lhs.Item.Name, rhs.Item.Name, StringComparison.CurrentCulture);
            return order != 0 ? order : lhs.Index.CompareTo(rhs.Index);
        });
        return keyedItems.ConvertAll(static keyed => keyed.Item);
    }

    private static bool HasParam(ItemFood? consumable, Predicate<uint> match)
    {
        if (consumable == null)
        {
            return false;
        }

        foreach (var param in consumable.Value.Params)
        {
            if (match(param.BaseParam.RowId))
            {
                return true;
            }
        }

        return false;
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

        return HasParam(GetItemConsumableProperties(item, false), static id => id is 11 or 70 or 71);
    }

    private static bool IsCraftersPot(Item item)
    {
        if (item.ItemUICategory.RowId != 44)
        {
            return false;
        }

        return HasParam(GetItemConsumableProperties(item, false), static id => id is 11 or 70 or 71 or 69 or 68);
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

    private static string GetItemName(uint itemId)
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

    private static int CalculateMaxQuality(Recipe recipe)
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

        var recipe = RecipeManager.GetRecipe(_recipeId);
        var subtitle = recipe.HasValue ? $"Starting quality {CalculateCurrentQuality(recipe.Value)} of {CalculateMaxQuality(recipe.Value)}" : null;
        using var card = M3Card.Begin("##craftIngredients", "Ingredient quality", FontAwesomeIcon.Gem, M3.AccentFor(2), subtitle: subtitle);
        M3Widgets.LabeledCheckbox("Prefer NQ##ingredientNQ", ref _useAllNQ,
            tooltip: "Use NQ materials by default, falling back to HQ only when there isn't enough NQ.\nThe HQ amounts below still apply.");
        if (_editingList is { UseAllHQ: true })
        {
            M3Text.Draw("The list uses all HQ by default. Change the amounts below to override it for this item.", M3TextStyle.Muted, true);
        }

        using var style = M3Table.PushStyle();
        using var table = ImRaii.Table("##ingredients", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.PadOuterX,
            new Vector2(ImGui.GetContentRegionAvail().X - M3Card.RightInset, 0));
        if (!table)
        {
            return;
        }

        ImGui.TableSetupColumn("Ingredient", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Need", ImGuiTableColumnFlags.WidthFixed, IngredientCountColumnWidth);
        ImGui.TableSetupColumn("NQ", ImGuiTableColumnFlags.WidthFixed, IngredientCountColumnWidth);
        ImGui.TableSetupColumn("HQ", ImGuiTableColumnFlags.WidthFixed, IngredientCountColumnWidth);
        ImGui.TableSetupColumn("Use HQ", ImGuiTableColumnFlags.WidthFixed, IngredientUseHqColumnWidth);
        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        ImGui.TableNextColumn();
        M3Table.Header("Ingredient", center: false);
        ImGui.TableNextColumn();
        M3Table.Header("Need");
        ImGui.TableNextColumn();
        M3Table.Header("NQ", "In your bags");
        ImGui.TableNextColumn();
        M3Table.Header("HQ", "In your bags");
        ImGui.TableNextColumn();
        M3Table.Header("Use HQ", "How many HQ ones to use");

        for (var i = 0; i < _ingredients.Count; i++)
        {
            var ing = _ingredients[i];
            using var id = ImRaii.PushId(i);
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(ing.ItemName);
            ImGui.TableNextColumn();
            M3Table.CenteredText(ing.TotalNeeded.ToString());
            ImGui.TableNextColumn();
            M3Table.CenteredText(ing.AvailableNQ.ToString(), ing.AvailableNQ == 0 ? M3Text.ColorOf(M3TextStyle.Faint) : null);
            ImGui.TableNextColumn();
            M3Table.CenteredText(ing.AvailableHQ.ToString(), ing.AvailableHQ == 0 ? M3Text.ColorOf(M3TextStyle.Faint) : M3.Scheme.Info);
            ImGui.TableNextColumn();
            if (ing.CanBeHQ)
            {
                var desiredHQ = ing.DesiredHQ;
                if (M3Widgets.NumberField("##hq", ref desiredHQ, IngredientInputWidth, 1, 0, ing.TotalNeeded))
                {
                    ing.DesiredHQ = Math.Clamp(desiredHQ, 0, ing.TotalNeeded);
                }
            }
            else
            {
                M3Table.CenteredText("N/A", M3Text.ColorOf(M3TextStyle.Faint));
            }
        }
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
