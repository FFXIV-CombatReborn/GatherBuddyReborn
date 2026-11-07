using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text;
using FFXIVClientStructs.FFXIV.Client.Game;
using GatherBuddy.Crafting;
using GatherBuddy.Vulcan;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace GatherBuddy.Gui;

public class CraftingListConsumablesPopup
{
    private bool _isOpen;
    private string _title = "";
    private CraftingListDefinition? _list;
    private CraftingListConsumableSettings _workingDefaults = new();
    private string? _workingDefaultPrecraftMacroId;
    private string? _workingDefaultFinalMacroId;
    private SolverOverrideMode _workingDefaultPrecraftSolverOverride = SolverOverrideMode.Default;
    private SolverOverrideMode _workingDefaultFinalSolverOverride = SolverOverrideMode.Default;
    private bool _workingUseAllHQ;
    private string _foodSearch = string.Empty;
    private string _medicineSearch = string.Empty;
    private string _precraftMacroSearch = string.Empty;
    private string _finalMacroSearch = string.Empty;
    private List<(uint ItemId, string Name, bool IsHQ)> _foodItems = [];
    private List<(uint ItemId, string Name, bool IsHQ)> _medicineItems = [];
    private List<(uint ItemId, string Name)> _manualItems = [];
    private List<(uint ItemId, string Name)> _squadronManualItems = [];
    public System.Action? OnSaved { get; set; }
    private static float SelectorLabelWidth => VulcanUiScaling.Scaled(160f);
    private static float SelectorComboWidth => VulcanUiScaling.Scaled(240f);
    private static Vector2 SaveButtonSize => VulcanUiScaling.Scaled(140f, 0f);
    private static Vector2 CancelButtonSize => VulcanUiScaling.Scaled(100f, 0f);

    public void OpenListDefaults(CraftingListDefinition list)
    {
        _list = list;
        _title = $"List Consumables/Macros - {list.Name}";
        _workingDefaults = list.Consumables.Clone();
        _workingDefaultPrecraftMacroId = list.DefaultPrecraftMacroId;
        _workingDefaultFinalMacroId = list.DefaultFinalMacroId;
        _workingDefaultPrecraftSolverOverride = list.DefaultPrecraftSolverOverride;
        _workingDefaultFinalSolverOverride = list.DefaultFinalSolverOverride;
        _workingUseAllHQ = list.UseAllHQ;
        _foodSearch = string.Empty;
        _medicineSearch = string.Empty;
        _precraftMacroSearch = string.Empty;
        _finalMacroSearch = string.Empty;
        EnsureConsumablesLoaded();
        _isOpen = true;
    }

    public void Draw()
    {
        if (!_isOpen)
        {
            return;
        }

        if (ImGui.Begin(_title, ref _isOpen, ImGuiWindowFlags.NoResize | ImGuiWindowFlags.AlwaysAutoResize))
        {
            DrawListDefaults();

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            if (ImGui.Button("Save Defaults", SaveButtonSize))
            {
                Save();
            }

            ImGui.SameLine();
            if (ImGui.Button("Cancel", CancelButtonSize))
            {
                _isOpen = false;
            }

            ImGui.End();
        }
    }

    private void DrawListDefaults()
    {
        ImGui.Text("Default consumables for this list:");
        ImGui.Spacing();
        DrawMacroSection();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.Checkbox("Assume All HQ Materials##ListUseAllHQ", ref _workingUseAllHQ);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Use all HQ-capable ingredients as the list default for planning and Raphael warmup.\nItem and precraft settings can still override this.");
        }

        ImGui.Spacing();

        var foodItemId = _workingDefaults.FoodItemId;
        var foodHq = _workingDefaults.FoodHQ;
        var medicineItemId = _workingDefaults.MedicineItemId;
        var medicineHq = _workingDefaults.MedicineHQ;
        var manualItemId = _workingDefaults.ManualItemId;
        var squadronManualItemId = _workingDefaults.SquadronManualItemId;

        DrawFoodSelector(ref foodItemId, ref foodHq, "##ListFood", ref _foodSearch);
        DrawMedicineSelector(ref medicineItemId, ref medicineHq, "##ListMedicine", ref _medicineSearch);
        DrawManualSelector(ref manualItemId, "##ListManual");
        DrawSquadronManualSelector(ref squadronManualItemId, "##ListSquadron");

        _workingDefaults.FoodItemId = foodItemId;
        _workingDefaults.FoodHQ = foodHq;
        _workingDefaults.MedicineItemId = medicineItemId;
        _workingDefaults.MedicineHQ = medicineHq;
        _workingDefaults.ManualItemId = manualItemId;
        _workingDefaults.SquadronManualItemId = squadronManualItemId;
    }

    private void DrawMacroSection()
    {
        var allMacros = CraftingGameInterop.UserMacroLibrary.GetAllMacros();
        DrawMacroSelector(
            "Precraft Macro:",
            "##PrecraftMacro",
            "##PrecraftMacroSearch",
            ref _workingDefaultPrecraftMacroId,
            ref _workingDefaultPrecraftSolverOverride,
            ref _precraftMacroSearch,
            allMacros);

        DrawMacroSelector(
            "Final Craft Macro:",
            "##FinalMacro",
            "##FinalMacroSearch",
            ref _workingDefaultFinalMacroId,
            ref _workingDefaultFinalSolverOverride,
            ref _finalMacroSearch,
            allMacros);
    }

    private void DrawMacroSelector(
        string label,
        string comboId,
        string searchId,
        ref string? selectedMacroId,
        ref SolverOverrideMode solverOverride,
        ref string macroSearch,
        List<UserMacro> allMacros)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.Text(label);
        ImGui.SameLine(SelectorLabelWidth);

        var currentName = GetMacroSelectionName(selectedMacroId, solverOverride, allMacros);
        ImGui.SetNextItemWidth(SelectorComboWidth);
        if (!ImGui.BeginCombo(comboId, currentName))
        {
            return;
        }

        var isDefault = solverOverride == SolverOverrideMode.Default && string.IsNullOrEmpty(selectedMacroId);
        if (ImGui.Selectable("Default (Use Solver)", isDefault))
        {
            selectedMacroId = null;
            solverOverride = SolverOverrideMode.Default;
        }
        if (ImGui.Selectable("Standard Solver", solverOverride == SolverOverrideMode.StandardSolver))
        {
            selectedMacroId = null;
            solverOverride = SolverOverrideMode.StandardSolver;
        }
        if (ImGui.Selectable("Raphael Solver", solverOverride == SolverOverrideMode.RaphaelSolver))
        {
            selectedMacroId = null;
            solverOverride = SolverOverrideMode.RaphaelSolver;
        }
        if (ImGui.Selectable("Progress Only", solverOverride == SolverOverrideMode.ProgressOnlySolver))
        {
            selectedMacroId = null;
            solverOverride = SolverOverrideMode.ProgressOnlySolver;
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Use only progress-building actions, no quality steps. Fast NQ crafts.");
        }

        if (allMacros.Count > 0)
        {
            ImGui.Separator();
            ImGui.InputTextWithHint(searchId, "Search macros...", ref macroSearch, 128);
            var searchValue = macroSearch;
            var filteredMacros = string.IsNullOrWhiteSpace(searchValue)
                ? allMacros
                : [.. allMacros.Where(m => m.Name.Contains(searchValue, StringComparison.OrdinalIgnoreCase))];
            foreach (var macro in filteredMacros)
            {
                var isSelected = selectedMacroId == macro.Id;
                var displayName = macro.MinCraftsmanship > 0 || macro.MinControl > 0 || macro.MinCP > 0
                    ? $"{macro.Name} ({macro.MinCraftsmanship}/{macro.MinControl}/{macro.MinCP})"
                    : macro.Name;
                if (ImGui.Selectable(displayName, isSelected))
                {
                    selectedMacroId = macro.Id;
                    solverOverride = SolverOverrideMode.Default;
                }
            }
        }

        ImGui.EndCombo();
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

    private void DrawFoodSelector(ref uint? itemId, ref bool hq, string idSuffix, ref string search)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.Text("Food:");
        ImGui.SameLine(SelectorLabelWidth);

        var current = GetDisplayName(itemId, hq);
        ImGui.SetNextItemWidth(SelectorComboWidth);
        if (ImGui.BeginCombo($"##Food{idSuffix}", current))
        {
            if (ImGui.Selectable("None", !itemId.HasValue))
            {
                itemId = null;
                hq = false;
            }

            ImGui.Separator();
            ImGui.InputTextWithHint($"##FoodSearch{idSuffix}", "Search food...", ref search, 128);

            var searchValue = search;
            var filtered = string.IsNullOrWhiteSpace(searchValue)
                ? _foodItems
                : [.. _foodItems.Where(f => f.Name.Contains(searchValue, StringComparison.OrdinalIgnoreCase))];

            foreach (var entry in filtered)
            {
                var displayName = entry.Name + (entry.IsHQ ? $" {(char)SeIconChar.HighQuality}" : "");
                var isSelected = itemId == entry.ItemId && hq == entry.IsHQ;
                if (ImGui.Selectable(displayName, isSelected))
                {
                    itemId = entry.ItemId;
                    hq = entry.IsHQ;
                }
            }

            ImGui.EndCombo();
        }
    }

    private void DrawMedicineSelector(ref uint? itemId, ref bool hq, string idSuffix, ref string search)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.Text("Medicine:");
        ImGui.SameLine(SelectorLabelWidth);

        var current = GetDisplayName(itemId, hq);
        ImGui.SetNextItemWidth(SelectorComboWidth);
        if (ImGui.BeginCombo($"##Medicine{idSuffix}", current))
        {
            if (ImGui.Selectable("None", !itemId.HasValue))
            {
                itemId = null;
                hq = false;
            }

            ImGui.Separator();
            ImGui.InputTextWithHint($"##MedicineSearch{idSuffix}", "Search medicine...", ref search, 128);

            var searchValue = search;
            var filtered = string.IsNullOrWhiteSpace(searchValue)
                ? _medicineItems
                : [.. _medicineItems.Where(m => m.Name.Contains(searchValue, StringComparison.OrdinalIgnoreCase))];

            foreach (var entry in filtered)
            {
                var displayName = entry.Name + (entry.IsHQ ? $" {(char)SeIconChar.HighQuality}" : "");
                var isSelected = itemId == entry.ItemId && hq == entry.IsHQ;
                if (ImGui.Selectable(displayName, isSelected))
                {
                    itemId = entry.ItemId;
                    hq = entry.IsHQ;
                }
            }

            ImGui.EndCombo();
        }
    }

    private void DrawManualSelector(ref uint? itemId, string idSuffix)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.Text("Manual:");
        ImGui.SameLine(SelectorLabelWidth);

        var manualId = itemId ?? 0;
        var current = manualId == 0 ? "None" : GetItemName(manualId);
        ImGui.SetNextItemWidth(SelectorComboWidth);
        if (ImGui.BeginCombo($"##Manual{idSuffix}", current))
        {
            if (ImGui.Selectable("None", manualId == 0))
            {
                itemId = null;
            }

            foreach (var (id, name) in _manualItems)
            {
                var isSelected = id == manualId;
                if (ImGui.Selectable(name, isSelected))
                {
                    itemId = id;
                }
            }

            ImGui.EndCombo();
        }
    }

    private void DrawSquadronManualSelector(ref uint? itemId, string idSuffix)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.Text("Squadron Manual:");
        ImGui.SameLine(SelectorLabelWidth);

        var squadronId = itemId ?? 0;
        var current = squadronId == 0 ? "None" : GetItemName(squadronId);
        ImGui.SetNextItemWidth(SelectorComboWidth);
        if (ImGui.BeginCombo($"##Squadron{idSuffix}", current))
        {
            if (ImGui.Selectable("None", squadronId == 0))
            {
                itemId = null;
            }

            foreach (var (id, name) in _squadronManualItems)
            {
                var isSelected = id == squadronId;
                if (ImGui.Selectable(name, isSelected))
                {
                    itemId = id;
                }
            }

            ImGui.EndCombo();
        }
    }

    private void EnsureConsumablesLoaded()
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

    private static string GetDisplayName(uint? itemId, bool hq)
        => itemId.HasValue ? GetItemName(itemId.Value) + (hq ? $" {(char)SeIconChar.HighQuality}" : "") : "None";

    private static string GetItemName(uint itemId)
    {
        var itemSheet = Dalamud.GameData.GetExcelSheet<Item>();
        if (itemSheet != null && itemSheet.TryGetRow(itemId, out var item))
        {
            return item.Name.ExtractText();
        }

        return "Unknown";
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

    private void Save()
    {
        if (_list == null)
        {
            return;
        }

        _list.Consumables = _workingDefaults;
        _list.DefaultPrecraftMacroId = _workingDefaultPrecraftMacroId;
        _list.DefaultFinalMacroId = _workingDefaultFinalMacroId;
        _list.DefaultPrecraftSolverOverride = _workingDefaultPrecraftSolverOverride;
        _list.DefaultFinalSolverOverride = _workingDefaultFinalSolverOverride;
        _list.UseAllHQ = _workingUseAllHQ;
        GatherBuddy.CraftingListManager.SaveList(_list);
        RaphaelAssessmentService.QueueWarmupForList(_list);
        MacroValidator.InvalidateAll();
        OnSaved?.Invoke();
    }
}
