using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Lumina.Excel.Sheets;
using ElliLib;
using ElliLib.Raii;
using ElliLib.Widgets;
using ImRaii = ElliLib.Raii.ImRaii;
using GatherBuddy.Crafting;
using GatherBuddy.Plugin;
using GatherBuddy.Vulcan;

namespace GatherBuddy.Gui;

public class CraftingListEditor
{
    private CraftingListDefinition _list;
    private int _searchQuantity = 1;
    private Recipe? _selectedRecipe = null;
    private Dictionary<uint, string> _recipeLabels = new();
    private bool _showMaterials = true;
    private ClippedSelectableCombo<Recipe>? _recipeCombo = null;
    private List<Recipe> _allRecipes = new();
    private List<Recipe> _keywordFilteredRecipes = new();
    private string _lastComboFilter = string.Empty;
    
    private List<CraftingListItem>? _cachedSortedQueue = null;
    private int _cachedRecipeCount = -1;
    private bool _cachedQueueValid = false;
    private string _cachedListHash = string.Empty;
    private int _selectedQueueIndex = -1;
    private bool _showPrecrafts = true;
    
    private Dictionary<uint, int>? _cachedMaterials = null;
    private string _cachedMaterialsHash = string.Empty;
    private bool _cachedMaterialsValid = false;
    
    private Task? _queueGenerationTask = null;
    private CancellationTokenSource? _queueCancellationSource = null;
    private bool _isGeneratingQueue = false;
    
    private Task? _materialsGenerationTask = null;
    private CancellationTokenSource? _materialsCancellationSource = null;
    private bool _isGeneratingMaterials = false;
    
    private Dictionary<uint, int> _cachedInventoryCounts = new();
    private Dictionary<uint, DateTime> _inventoryRefreshTimes = new();
    private RetainerItemSnapshot _cachedRetainerSnapshot = RetainerItemSnapshot.Empty;
    private uint[] _cachedRetainerSnapshotItemIds = [];
    private DateTime _cachedRetainerSnapshotAt = DateTime.MinValue;
    private const double InventoryRefreshIntervalSeconds = 0.5;
    private const double RetainerSnapshotRetryIntervalSeconds = 1.0;
    
    private RecipeCraftSettingsPopup _craftSettingsPopup = new();
    private CraftingListConsumablesPopup _consumablesPopup = new();
    
    private int _editingQuantityIndex = -1;
    private int _tempQuantityInput = 0;
    private Dictionary<uint, int>? _cachedPrecraftMaterials = null;
    private string _cachedPrecraftMaterialsHash = string.Empty;

    private string _editingName        = string.Empty;
    private string _editingDescription = string.Empty;
    private bool   _nameConflict       = false;
    private bool   _editingDescActive  = false;
    private bool   _focusDescNext      = false;
    
    internal bool HasCachedMaterials    => _cachedMaterials != null;
    internal bool IsGeneratingMaterials => _isGeneratingMaterials;
    internal string ListName            => _list.Name;
    
    public Action<CraftingListDefinition>? OnStartCrafting { get; set; }

    public CraftingListEditor(CraftingListDefinition list)
    {
        _list               = list;
        _editingName        = list.Name;
        _editingDescription = list.Description;
        RefreshInventoryCounts();
        TriggerQueueRegeneration();
    }
    
    public void Dispose()
    {
        _queueCancellationSource?.Cancel();
        _queueCancellationSource?.Dispose();
        _materialsCancellationSource?.Cancel();
        _materialsCancellationSource?.Dispose();
    }
    
    public void RefreshInventoryCounts()
    {
        _cachedInventoryCounts.Clear();
        _inventoryRefreshTimes.Clear();
        InvalidateRetainerSnapshot();
    }

    internal void RefreshFromExternalListChange()
    {
        GatherBuddy.Log.Debug($"[CraftingListEditor] Refreshing cached queue/materials for externally modified list '{_list.Name}'");
        _cachedQueueValid = false;
        _cachedMaterialsValid = false;
        _cachedPrecraftMaterials = null;
        _cachedPrecraftMaterialsHash = string.Empty;
        TriggerQueueRegeneration();
        TriggerMaterialsRegeneration();
    }
    public void Draw()
    {
        var availableWidth = ImGui.GetContentRegionAvail().X;
        var availableHeight = ImGui.GetContentRegionAvail().Y;
        
        var leftPaneWidth = availableWidth * 0.4f;
        var rightPaneWidth = availableWidth - leftPaneWidth - 8;
        
        using (ImRaii.PushColor(ImGuiCol.ChildBg, new Vector4(0.08f, 0.08f, 0.10f, 1.00f)))
        {
            ImGui.BeginChild("LeftPane", new Vector2(leftPaneWidth, availableHeight), true,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
            DrawQueuePane();
            ImGui.EndChild();
        }

        ImGui.SameLine();

        using (ImRaii.PushColor(ImGuiCol.ChildBg, new Vector4(0.08f, 0.08f, 0.10f, 1.00f)))
        {
            ImGui.BeginChild("RightPane", new Vector2(rightPaneWidth, availableHeight), true);
            DrawDetailsPane();
            ImGui.EndChild();
        }
        
        _craftSettingsPopup.Draw();
        _consumablesPopup.Draw();
    }

    private void DrawQueuePane()
    {
        ImGui.TextColored(ImGuiColors.DalamudYellow, "Craft Queue");
        ImGui.Separator();
        ImGui.Spacing();

        if (_list.Recipes.Count == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "No recipes in queue.");
            ImGui.Spacing();
            ImGui.TextWrapped("Add recipes using the panel on the right.");
            return;
        }

        var sortedQueue  = GetSortedQueue();
        var displayQueue = _showPrecrafts
            ? sortedQueue
            : _list.Recipes.Select(r => new CraftingListItem(r.RecipeId, r.Quantity)
                {
                    IsOriginalRecipe = true,
                }).ToList();

        var lineH   = ImGui.GetTextLineHeightWithSpacing();
        var spacing = ImGui.GetStyle().ItemSpacing.Y;
        var frameH  = ImGui.GetFrameHeightWithSpacing();
        var footerRows = _list.QuickSynthAll ? 9 : 7;
        var bottomH = frameH * footerRows + spacing * 2;
        var queueH  = Math.Max(ImGui.GetContentRegionAvail().Y - bottomH, lineH * 3);

        ImGui.BeginChild("QueueList", new Vector2(-1, queueH), false);

        if (_isGeneratingQueue)
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, "Calculating craft queue...");
        }
        else if (displayQueue.Count == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "Queue is empty.");
        }
        else
        {
            void DrawQueueItem(int idx)
            {
                var queueItem  = displayQueue[idx];
                var recipeData = RecipeManager.GetRecipe(queueItem.RecipeId);
                if (recipeData == null) return;

                var itemName = recipeData.Value.ItemResult.Value.Name.ExtractText();
                var jobName  = GetCraftingJobName(recipeData.Value.CraftType.RowId);
                var isOriginalRecipe = queueItem.IsOriginalRecipe;
                var willBeSkipped    = _list.SkipIfEnough && WillBeSkippedDueToInventory(recipeData.Value, queueItem.Quantity);
                var recipeOptions    = _list.GetRecipeOptions(queueItem.RecipeId, isOriginalRecipe);
                var effectiveQuickSynth = IsEffectivelyQuickSynth(recipeData.Value, queueItem.RecipeId, isOriginalRecipe);
                var forceQuickSynth = _list.ShouldForceQuickSynth(recipeData.Value, isOriginalRecipe);
                var quickSynthPrefix = effectiveQuickSynth ? "[QS] " : "";

                Vector4 textColor;
                if (willBeSkipped)
                    textColor = new Vector4(1, 0.3f, 0.3f, 1);
                else if (effectiveQuickSynth)
                    textColor = new Vector4(0.3f, 0.9f, 0.9f, 1);
                else if (isOriginalRecipe)
                    textColor = new Vector4(1, 1, 1, 1);
                else
                    textColor = new Vector4(0.7f, 0.7f, 0.7f, 1);
                var queueItemCraftSettings = GetEffectiveCraftSettings(queueItem.RecipeId, isOriginalRecipe);
                var queueItemValidation = WillUseQuickSynth(recipeData.Value, queueItem.RecipeId, isOriginalRecipe)
                    ? null
                    : MacroValidator.GetOrCompute(queueItem.RecipeId, ResolveEffectiveMacroId(queueItemCraftSettings, !isOriginalRecipe), queueItemCraftSettings, _list.Consumables);
                if (queueItemValidation != null)
                {
                    var dotColor = queueItemValidation.IsValid
                        ? new Vector4(0.30f, 0.70f, 0.30f, 1f)
                        : (queueItemValidation.Failure is MacroValidationFailure.InsufficientProgress or MacroValidationFailure.ActionUnusable
                            ? new Vector4(0.78f, 0.62f, 0.15f, 1f)
                            : new Vector4(0.78f, 0.25f, 0.25f, 1f));
                    ImGui.TextColored(dotColor, "\u25cf");
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(queueItemValidation.IsValid
                            ? $"Macro: PASS\nProgress: {queueItemValidation.FinalProgress}/{queueItemValidation.RequiredProgress}\nQuality: {queueItemValidation.FinalQuality}\nDurability: {queueItemValidation.FinalDurability}"
                            : $"Macro: {queueItemValidation.Failure} at step {queueItemValidation.FailedAtStep}\nProgress: {queueItemValidation.FinalProgress}/{queueItemValidation.RequiredProgress}");
                    ImGui.SameLine();
                }

                ImGui.PushStyleColor(ImGuiCol.Text, textColor);
                var isSelected = _selectedQueueIndex == idx;
                var label      = $"{quickSynthPrefix}{idx + 1}. {itemName} x{queueItem.Quantity} ({jobName})";
                if (ImGui.Selectable(label, isSelected))
                    _selectedQueueIndex = idx;
                ImGui.PopStyleColor();

                var isPopupOpen = GatherBuddy.ControllerSupport != null
                    ? GatherBuddy.ControllerSupport.ContextMenu.BeginPopupContextItemWithGamepad($"queue_ctx_{idx}", Dalamud.GamepadState)
                    : ImGui.BeginPopupContextItem($"queue_ctx_{idx}");

                if (isPopupOpen)
                {
                    if (ImGui.MenuItem("Craft Settings..."))
                    {
                        if (isOriginalRecipe)
                        {
                            var listItem = _list.Recipes.FirstOrDefault(r => r.RecipeId == queueItem.RecipeId);
                            if (listItem != null)
                                _craftSettingsPopup.OpenForListItem(listItem, _list, itemName);
                        }
                        else
                        {
                            _craftSettingsPopup.OpenForPrecraft(queueItem.RecipeId, itemName, _list);
                        }
                    }

                    ImGui.Separator();

                    if (recipeData.Value.CanQuickSynth)
                    {
                        using (ImRaii.Disabled(forceQuickSynth))
                        {
                            if (ImGui.MenuItem("Quick Synthesis", "", effectiveQuickSynth))
                            {
                                _list.SetRecipeQuickSynth(queueItem.RecipeId, !recipeOptions.NQOnly, isOriginalRecipe);
                                GatherBuddy.CraftingListManager.SaveList(_list);
                                _cachedQueueValid     = false;
                                _cachedMaterialsValid = false;
                                TriggerQueueRegeneration();
                                TriggerMaterialsRegeneration();
                            }
                        }
                        if (ImGui.IsItemHovered(forceQuickSynth ? ImGuiHoveredFlags.AllowWhenDisabled : ImGuiHoveredFlags.None))
                            ImGui.SetTooltip(forceQuickSynth
                                ? "Forced on by Quick Synth All for this recipe. Disable the list-level override to edit the per-item quick synth setting."
                                : "Use quick synthesis for this recipe (NQ only)");
                    }
                    else
                    {
                        ImGui.TextDisabled("Quick Synthesis not available");
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip("Recipe must be unlocked and previously crafted to use Quick Synthesis");
                    }

                    ImGui.EndPopup();
                }
            }

            for (int i = 0; i < displayQueue.Count; i++)
                DrawQueueItem(i);
        }

        ImGui.EndChild();

        ImGui.BeginChild("QueueFooter", new Vector2(-1, 0), false,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);

        ImGui.Separator();
        ImGui.Spacing();

        ImGui.Checkbox("Show Precrafts##sp", ref _showPrecrafts);

        var skipIfEnough = _list.SkipIfEnough;
        if (ImGui.Checkbox("Skip if Already Have Enough##sie", ref skipIfEnough))
        {
            _list.SkipIfEnough    = skipIfEnough;
            _cachedQueueValid     = false;
            _cachedMaterialsValid = false;
            GatherBuddy.CraftingListManager.SaveList(_list);
            TriggerQueueRegeneration();
            RefreshInventoryCounts();
        }

        var quickSynthAll = _list.QuickSynthAll;
        if (ImGui.Checkbox("Quick Synth All##qsa", ref quickSynthAll))
        {
            _list.QuickSynthAll = quickSynthAll;
            GatherBuddy.CraftingListManager.SaveList(_list);
            _cachedQueueValid     = false;
            _cachedMaterialsValid = false;
            TriggerQueueRegeneration();
            TriggerMaterialsRegeneration();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Force Quick Synthesis on eligible items in this list. Additional override options appear below when enabled.");

        if (_list.QuickSynthAll)
        {
            ImGui.Indent();

            var quickSynthAllPreferNQ = _list.QuickSynthAllPreferNQ;
            if (ImGui.Checkbox("Prefer NQ##qsapnq", ref quickSynthAllPreferNQ))
            {
                _list.QuickSynthAllPreferNQ = quickSynthAllPreferNQ;
                GatherBuddy.CraftingListManager.SaveList(_list);
                _cachedQueueValid     = false;
                _cachedMaterialsValid = false;
                TriggerQueueRegeneration();
                TriggerMaterialsRegeneration();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Override ingredient quality preferences for affected crafts and prefer NQ materials unless HQ is required as fallback.");

            var quickSynthAllPrecraftsOnly = _list.QuickSynthAllPrecraftsOnly;
            if (ImGui.Checkbox("Precrafts Only##qsapo", ref quickSynthAllPrecraftsOnly))
            {
                _list.QuickSynthAllPrecraftsOnly = quickSynthAllPrecraftsOnly;
                GatherBuddy.CraftingListManager.SaveList(_list);
                _cachedQueueValid     = false;
                _cachedMaterialsValid = false;
                TriggerQueueRegeneration();
                TriggerMaterialsRegeneration();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Apply the Quick Synth All and Prefer NQ overrides only to generated precrafts, leaving final list items unchanged.");

            ImGui.Unindent();
        }

        var allaganEnabled = AllaganTools.Enabled;
        using (ImRaii.Disabled(!allaganEnabled))
        {
            var retainerRestock = _list.RetainerRestock;
            if (ImGui.Checkbox("Restock from Retainers##rrr", ref retainerRestock))
            {
                _list.RetainerRestock = retainerRestock;
                GatherBuddy.CraftingListManager.SaveList(_list);
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(allaganEnabled
                ? "Withdraw needed materials from retainers before generating the gather list. Respects HQ/NQ preferences."
                : "Requires Allagan Tools to be installed and enabled.");


        ImGui.Spacing();

        if (IPCSubscriber.IsReady("Artisan"))
        {
            ImGuiUtil.DrawDisabledButton("Artisan Detected", new Vector2(-1, 22),
                "Artisan plugin is loaded. Please unload Artisan to use Vulcan's crafting system.", true);
        }
        else
        {
            var (hardFails, warnings) = CountValidationIssues();
            if (hardFails > 0)
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.50f, 0.15f, 0.15f, 1f));
            else if (warnings > 0)
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.55f, 0.40f, 0.05f, 1f));

            if (ImGui.Button("Start Gather/Crafting", new Vector2(-1, 22)))
            {
                if (hardFails > 0)
                    ImGui.OpenPopup("ConfirmFailedMacros##startCraft");
                else
                    OnStartCrafting?.Invoke(_list);
            }

            if (hardFails > 0 || warnings > 0)
            {
                ImGui.PopStyleColor();
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(hardFails > 0
                        ? $"{hardFails} macro(s) will fail this craft. Click to confirm and start anyway."
                        : $"{warnings} macro(s) have warnings.");
            }

            if (ImGui.BeginPopupModal("ConfirmFailedMacros##startCraft", ImGuiWindowFlags.AlwaysAutoResize))
            {
                ImGui.TextColored(new Vector4(0.78f, 0.25f, 0.25f, 1f), $"{hardFails} macro(s) are predicted to FAIL their craft.");
                ImGui.TextWrapped("These items may not be completed. Start crafting anyway?");
                ImGui.Spacing();
                if (ImGui.Button("Start Anyway", new Vector2(120, 0)))
                {
                    OnStartCrafting?.Invoke(_list);
                    ImGui.CloseCurrentPopup();
                }
                ImGui.SameLine();
                if (ImGui.Button("Cancel", new Vector2(80, 0)))
                    ImGui.CloseCurrentPopup();
                ImGui.EndPopup();
            }
        }

        var halfW = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) / 2f;
        if (ImGui.Button("Gather List##gatherList", new Vector2(halfW, 22)))
        {
            var materials = _list.ListMaterials();
            CraftingGatherBridge.CreatePersistentGatherList($"{_list.Name}...Auto-Generated", materials);
        }
        ImGui.SameLine();
        var matsBtnLabel = GatherBuddy.CraftingMaterialsWindow?.IsOpen == true ? "Hide Materials" : "View Materials";
        if (ImGui.Button($"{matsBtnLabel}##viewMats", new Vector2(-1, 22)) && GatherBuddy.CraftingMaterialsWindow != null)
            GatherBuddy.CraftingMaterialsWindow.IsOpen = !GatherBuddy.CraftingMaterialsWindow.IsOpen;

        ImGui.EndChild();
    }
    
    private void DrawDetailsPane()
    {
        ImGui.TextColored(ImGuiColors.DalamudYellow, "List Info");
        ImGui.Separator();
        ImGui.Spacing();
        DrawListInfoSection();

        ImGui.Spacing();
        ImGui.TextColored(ImGuiColors.DalamudYellow, "List Consumables");
        ImGui.Separator();
        ImGui.Spacing();
        DrawListConsumablesSection();

        ImGui.Spacing();
        ImGui.TextColored(ImGuiColors.DalamudYellow, "Add Recipe");
        ImGui.Separator();
        ImGui.Spacing();
        DrawAddRecipeSection();

        ImGui.Spacing();
        ImGui.TextColored(ImGuiColors.DalamudYellow, "Recipe List");
        ImGui.Separator();
        ImGui.Spacing();
        DrawRecipeListSection();
        
    }

    private void DrawListInfoSection()
    {
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("##listName", ref _editingName, 128))
            _nameConflict = false;

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            var trimmed = _editingName.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                _editingName = _list.Name;
            }
            else if (GatherBuddy.CraftingListManager.IsNameUnique(trimmed, _list.ID))
            {
                _list.Name   = trimmed;
                _editingName = trimmed;
                GatherBuddy.CraftingListManager.SaveList(_list);
                GatherBuddy.Log.Debug($"[CraftingListEditor] Renamed list to '{trimmed}'");
            }
            else
            {
                _nameConflict = true;
            }
        }

        if (_nameConflict)
            ImGui.TextColored(ImGuiColors.DalamudRed, "A list with that name already exists.");

        ImGui.Spacing();
        ImGui.TextColored(ImGuiColors.DalamudGrey3, "Notes");

        if (_editingDescActive)
        {
            if (_focusDescNext)
            {
                ImGui.SetKeyboardFocusHere();
                _focusDescNext = false;
            }
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextMultiline("##listDesc", ref _editingDescription, 512, new Vector2(-1, 60));
            if (ImGui.IsItemDeactivated())
            {
                _list.Description = _editingDescription;
                GatherBuddy.CraftingListManager.SaveList(_list);
                _editingDescActive = false;
                GatherBuddy.Log.Debug($"[CraftingListEditor] Updated description for list '{_list.Name}'");
            }
        }
        else
        {
            using (ImRaii.PushColor(ImGuiCol.ChildBg, new Vector4(0.15f, 0.15f, 0.18f, 1f)))
            {
                ImGui.BeginChild("##notesDisplay", new Vector2(-1, 60f), true,
                    ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);

                if (string.IsNullOrEmpty(_editingDescription))
                    ImGui.TextColored(ImGuiColors.DalamudGrey, "Click to add notes...");
                else
                {
                    using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey3))
                        ImGui.TextWrapped(_editingDescription);
                }

                if (ImGui.IsWindowHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                {
                    _editingDescActive = true;
                    _focusDescNext     = true;
                }

                ImGui.EndChild();
            }
        }
    }

    private void DrawListConsumablesSection()
    {
        var labelColor = new Vector4(0.80f, 0.80f, 0.80f, 1f);
        var valueX     = 80f;
        var hasAny     = false;

        if (_list.Consumables.FoodItemId.HasValue)
        {
            ImGui.TextColored(labelColor, "Food:");
            ImGui.SameLine(valueX);
            ImGui.TextColored(labelColor, GetItemLabel(_list.Consumables.FoodItemId.Value, _list.Consumables.FoodHQ));
            hasAny = true;
        }
        if (_list.Consumables.MedicineItemId.HasValue)
        {
            ImGui.TextColored(labelColor, "Medicine:");
            ImGui.SameLine(valueX);
            ImGui.TextColored(labelColor, GetItemLabel(_list.Consumables.MedicineItemId.Value, _list.Consumables.MedicineHQ));
            hasAny = true;
        }
        if (_list.Consumables.ManualItemId.HasValue)
        {
            ImGui.TextColored(labelColor, "Manual:");
            ImGui.SameLine(valueX);
            ImGui.TextColored(labelColor, GetItemLabel(_list.Consumables.ManualItemId.Value, false));
            hasAny = true;
        }
        if (_list.Consumables.SquadronManualItemId.HasValue)
        {
            ImGui.TextColored(labelColor, "Squadron:");
            ImGui.SameLine(valueX);
            ImGui.TextColored(labelColor, GetItemLabel(_list.Consumables.SquadronManualItemId.Value, false));
            hasAny = true;
        }
        if (!hasAny)
            ImGui.TextColored(ImGuiColors.DalamudGrey, "None set.");

        ImGui.Spacing();
        if (ImGui.Button("Edit Consumables & Macros##editConsumables", new Vector2(0, 0)))
            _consumablesPopup.OpenListDefaults(_list);
    }
    
    private void DrawAddRecipeSection()
    {
        if (_recipeCombo == null)
            InitializeRecipeCombo();

        DrawRecipeComboWithKeywordFilter();

        ImGui.SetNextItemWidth(120);
        ImGui.InputInt("##quantity", ref _searchQuantity, 1);
        if (_searchQuantity < 1)
            _searchQuantity = 1;
        ImGui.SameLine();

        using (ImRaii.Disabled(_selectedRecipe == null))
        {
            var clicked = ImGui.Button("Add to List##addRecipeBtn", new Vector2(0, 0));
            if (!clicked && ImGui.IsItemHovered() && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
                clicked = true;
            if (clicked && _selectedRecipe != null)
            {
                _list.AddRecipe(_selectedRecipe.Value.RowId, _searchQuantity);
                GatherBuddy.CraftingListManager.SaveList(_list);
                _cachedQueueValid     = false;
                _cachedMaterialsValid = false;
                TriggerQueueRegeneration();
                _selectedRecipe = null;
                _searchQuantity = 1;
            }
        }

        if (ImGui.IsItemHovered() && _selectedRecipe != null)
            ImGui.SetTooltip($"Add {_recipeLabels[_selectedRecipe.Value.RowId]} x{_searchQuantity} to list");
    }

    private void DrawRecipeComboWithKeywordFilter()
    {
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##recipeComboCustom", _selectedRecipe.HasValue ? _recipeLabels.GetValueOrDefault(_selectedRecipe.Value.RowId, "Select recipe") : "Select recipe"))
        {
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##filterRecipes", "Type to filter...", ref _lastComboFilter, 256);

            var filterKeywords = _lastComboFilter.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(k => k.ToLowerInvariant())
                .ToArray();

            var displayRecipes = _allRecipes;
            if (filterKeywords.Length > 0)
            {
                displayRecipes = _allRecipes.Where(r =>
                {
                    var label = _recipeLabels[r.RowId].ToLowerInvariant();
                    return filterKeywords.All(keyword => label.Contains(keyword));
                }).ToList();
            }

            var height = ImGui.GetTextLineHeightWithSpacing();
            void DrawRecipeItem(Recipe recipe)
            {
                if (ImGui.Selectable(_recipeLabels[recipe.RowId], _selectedRecipe?.RowId == recipe.RowId))
                {
                    _selectedRecipe = recipe;
                    ImGui.CloseCurrentPopup();
                }
            }

            ImGuiClip.ClippedDraw(displayRecipes, DrawRecipeItem, height);

            ImGui.EndCombo();
        }
    }

    private void InitializeRecipeCombo()
    {
        var recipeSheet = Dalamud.GameData.GetExcelSheet<Recipe>();
        if (recipeSheet == null)
            return;

        _allRecipes.Clear();
        foreach (var recipe in recipeSheet)
        {
            try
            {
                if (recipe.ItemResult.RowId == 0 || recipe.Number == 0)
                    continue;

                var recipeNameOriginal = recipe.ItemResult.Value.Name.ExtractText();
                if (!_recipeLabels.ContainsKey(recipe.RowId))
                {
                    var jobName = GetCraftingJobName(recipe.CraftType.RowId);
                    _recipeLabels[recipe.RowId] = $"{recipeNameOriginal} ({jobName} {recipe.RecipeLevelTable.Value.ClassJobLevel})";
                }

                _allRecipes.Add(recipe);
            }
            catch
            {
            }
        }

        _allRecipes.Sort((a, b) =>
        {
            var levelCmp = b.RecipeLevelTable.Value.ClassJobLevel.CompareTo(a.RecipeLevelTable.Value.ClassJobLevel);
            if (levelCmp != 0) return levelCmp;
            return a.ItemResult.Value.Name.ExtractText().CompareTo(b.ItemResult.Value.Name.ExtractText());
        });

        _recipeCombo = new ClippedSelectableCombo<Recipe>("RecipeCombo", "Recipe", 300, _allRecipes, r => _recipeLabels[r.RowId]);
    }

    private void DrawRecipeListSection()
    {
        if (_list.Recipes.Count == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "No recipes added yet.");
            return;
        }

        int indexToRemove = -1;
        
        for (int i = 0; i < _list.Recipes.Count; i++)
        {
            var item = _list.Recipes[i];
            var recipe = RecipeManager.GetRecipe(item.RecipeId);
            if (recipe == null)
                continue;

            var itemName = recipe.Value.ItemResult.Value.Name.ExtractText();
            var jobName = GetCraftingJobName(recipe.Value.CraftType.RowId);
            var effectiveCraftSettings = GetEffectiveCraftSettings(item.RecipeId, true);
            var skipIndicator = item.Options.Skipping ? "[SKIP] " : "";
            var hqIndicator = !_list.ShouldForcePreferNQ(true)
                && (item.IngredientPreferences.Count > 0 || effectiveCraftSettings?.IngredientPreferences.Count > 0)
                    ? "[HQ] "
                    : "";
            var effectiveQuickSynth = IsEffectivelyQuickSynth(recipe.Value, item.RecipeId, true);
            var quickSynthIndicator = effectiveQuickSynth ? "[QS] " : "";
            var craftSettingsIndicator = item.CraftSettings?.HasAnySettings() == true ? "[SET] " : "";
            var textColor = item.Options.Skipping
                ? new Vector4(0.7f, 0.7f, 0.7f, 1)
                : effectiveQuickSynth
                    ? new Vector4(0.3f, 0.9f, 0.9f, 1)
                    : new Vector4(1, 1, 1, 1);
            var validation = WillUseQuickSynth(recipe.Value, item.RecipeId, true)
                ? null
                : MacroValidator.GetOrCompute(item.RecipeId, ResolveEffectiveMacroId(effectiveCraftSettings, false), effectiveCraftSettings, _list.Consumables);
            if (validation != null)
            {
                var dotColor = validation.IsValid
                    ? new Vector4(0.30f, 0.70f, 0.30f, 1f)
                    : (validation.Failure is MacroValidationFailure.InsufficientProgress or MacroValidationFailure.ActionUnusable
                        ? new Vector4(0.78f, 0.62f, 0.15f, 1f)
                        : new Vector4(0.78f, 0.25f, 0.25f, 1f));
                ImGui.TextColored(dotColor, "\u25cf");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(validation.IsValid
                        ? $"Macro: PASS\nProgress: {validation.FinalProgress}/{validation.RequiredProgress}\nQuality: {validation.FinalQuality}\nDurability: {validation.FinalDurability}"
                        : $"Macro: {validation.Failure} at step {validation.FailedAtStep}\nProgress: {validation.FinalProgress}/{validation.RequiredProgress}");
                ImGui.SameLine();
            }

            ImGui.PushStyleColor(ImGuiCol.Text, textColor);
            ImGui.Selectable($"{quickSynthIndicator}{craftSettingsIndicator}{hqIndicator}{skipIndicator}{itemName} x{item.Quantity} ({jobName})##recipe_{i}", false);
            ImGui.PopStyleColor();
            
            var isPopupOpen = GatherBuddy.ControllerSupport != null
                ? GatherBuddy.ControllerSupport.ContextMenu.BeginPopupContextItemWithGamepad($"context_{i}", Dalamud.GamepadState)
                : ImGui.BeginPopupContextItem($"context_{i}");
            
            if (isPopupOpen)
            {
                if (ImGui.MenuItem("Craft Settings..."))
                {
                    _craftSettingsPopup.OpenForListItem(item, _list, itemName);
                }
                
                ImGui.Separator();
                
                ImGui.Text("Quantity:");
                ImGui.SetNextItemWidth(100);
                if (_editingQuantityIndex != i)
                {
                    _tempQuantityInput = item.Quantity;
                    _editingQuantityIndex = i;
                }
                
                if (ImGui.InputInt($"##qty_{i}", ref _tempQuantityInput, 1))
                {
                    if (_tempQuantityInput < 1)
                        _tempQuantityInput = 1;
                }
                
                if (ImGui.IsItemDeactivatedAfterEdit() && _tempQuantityInput != item.Quantity)
                {
                    _list.UpdateRecipeQuantity(item.RecipeId, _tempQuantityInput);
                    GatherBuddy.CraftingListManager.SaveList(_list);
                    _cachedQueueValid = false;
                    _cachedMaterialsValid = false;
                    TriggerQueueRegeneration();
                }
                
                ImGui.Separator();
                
                if (ImGui.MenuItem(item.Options.Skipping ? "Enable" : "Skip"))
                {
                    item.Options.Skipping = !item.Options.Skipping;
                    GatherBuddy.CraftingListManager.SaveList(_list);
                    _cachedQueueValid = false;
                    _cachedMaterialsValid = false;
                    TriggerQueueRegeneration();
                }
                
                if (ImGui.MenuItem("Remove"))
                {
                    indexToRemove = i;
                }
                
                ImGui.EndPopup();
            }
            else if (_editingQuantityIndex == i)
            {
                _editingQuantityIndex = -1;
            }
        }

        if (indexToRemove >= 0)
        {
            _list.Recipes.RemoveAt(indexToRemove);
            GatherBuddy.CraftingListManager.SaveList(_list);
            _cachedQueueValid = false;
            _cachedMaterialsValid = false;
            TriggerQueueRegeneration();
        }
    }

    private string ComputeListHash()
    {
        var hashParts = new List<string>();
        hashParts.Add($"SkipIfEnough:{_list.SkipIfEnough}");
        foreach (var item in _list.Recipes)
        {
            hashParts.Add($"{item.RecipeId}:{item.Quantity}:{item.Options.Skipping}");
        }
        return string.Join("|", hashParts);
    }
    
    private void TriggerQueueRegeneration()
    {
        var currentHash = ComputeListHash();
        if (_cachedQueueValid && _cachedSortedQueue != null && currentHash == _cachedListHash)
        {
            return;
        }
        
        _queueCancellationSource?.Cancel();
        _queueCancellationSource?.Dispose();
        _queueCancellationSource = new CancellationTokenSource();
        
        _isGeneratingQueue = true;
        var token = _queueCancellationSource.Token;
        var hash = currentHash;
        
        _queueGenerationTask = Task.Run(() =>
        {
            try
            {
                if (token.IsCancellationRequested) return;
                
                var queue = GenerateSortedQueueSync();
                
                if (!token.IsCancellationRequested)
                {
                    _cachedSortedQueue = queue;
                    _cachedListHash = hash;
                    _cachedQueueValid = true;
                }
            }
            catch (Exception ex)
            {
                GatherBuddy.Log.Error($"Error generating queue: {ex.Message}");
            }
            finally
            {
                _isGeneratingQueue = false;
            }
        }, token);
    }
    
    private static Dictionary<uint, int>? BuildRetainerAdditionalAvailable(CraftingListDefinition list)
    {
        if (!list.RetainerRestock || !AllaganTools.Enabled)
            return null;

        var precrafts = list.ListPrecrafts();
        if (precrafts.Count == 0) return null;

        var available = new Dictionary<uint, int>();
        foreach (var (itemId, needed) in precrafts)
        {
            var inRetainer = RetainerItemQuery.GetTotalCount(itemId);
            if (inRetainer > 0)
                available[itemId] = Math.Min(needed, inRetainer);
        }
        return available.Count > 0 ? available : null;
    }

    internal void TriggerMaterialsRegeneration()
    {
        var currentHash = ComputeListHash();
        if (_cachedMaterialsValid && _cachedMaterials != null && currentHash == _cachedMaterialsHash)
        {
            return;
        }
        
        _materialsCancellationSource?.Cancel();
        _materialsCancellationSource?.Dispose();
        _materialsCancellationSource = new CancellationTokenSource();
        
        _isGeneratingMaterials = true;
        var token = _materialsCancellationSource.Token;
        var hash = currentHash;
        
        _materialsGenerationTask = Task.Run(() =>
        {
            try
            {
                if (token.IsCancellationRequested) return;

                var additionalAvailable = BuildRetainerAdditionalAvailable(_list);
                var materials = _list.ListMaterials(additionalAvailable);
                
                if (!token.IsCancellationRequested)
                {
                    _cachedMaterials = materials;
                    _cachedMaterialsHash = hash;
                    _cachedMaterialsValid = true;
                }
            }
            catch (Exception ex)
            {
                GatherBuddy.Log.Error($"Error generating materials: {ex.Message}");
            }
            finally
            {
                _isGeneratingMaterials = false;
            }
        }, token);
    }
    
    private List<CraftingListItem> GetSortedQueue()
    {
        if (_cachedSortedQueue != null && _cachedQueueValid)
        {
            return _cachedSortedQueue;
        }
        return new List<CraftingListItem>();
    }
    
    private List<CraftingListItem> GenerateSortedQueueSync()
    {
        var queue = new CraftingListQueue();
        foreach (var item in _list.Recipes)
        {
            if (!item.Options.Skipping)
            {
                queue.AddRecipeWithPrecrafts(item.RecipeId, item.Quantity, _list.SkipIfEnough);
            }
        }
        
        var precrafts     = queue.Recipes.Where(recipe => !recipe.IsOriginalRecipe).ToList();
        var finalProducts = new List<CraftingListItem>(queue.OriginalRecipes);
        
        var precraftsByJob = precrafts
            .GroupBy(r => RecipeManager.GetRecipe(r.RecipeId)?.CraftType.RowId ?? uint.MaxValue)
            .OrderBy(g => g.Key);
        
        var sortedPrecrafts = new List<CraftingListItem>();
        var processedPrecrafts = new HashSet<uint>();
        
        foreach (var jobGroup in precraftsByJob)
        {
            var jobRecipes = jobGroup.ToList();
            foreach (var recipeItem in jobRecipes)
            {
                ProcessPrecraftWithDependencies(recipeItem, precrafts, processedPrecrafts, sortedPrecrafts);
            }
        }
        
        var sortedFinalProducts = finalProducts
            .GroupBy(r => RecipeManager.GetRecipe(r.RecipeId)?.CraftType.RowId ?? uint.MaxValue)
            .OrderBy(g => g.Key)
            .SelectMany(g => g)
            .ToList();
        
        var result = new List<CraftingListItem>();
        result.AddRange(sortedPrecrafts);
        result.AddRange(sortedFinalProducts);
        
        return result;
    }

    private RecipeCraftSettings? GetEffectiveCraftSettings(uint recipeId, bool isOriginalRecipe)
    {
        var sourceSettings = isOriginalRecipe
            ? _list.Recipes.FirstOrDefault(r => r.RecipeId == recipeId)?.CraftSettings
            : _list.PrecraftCraftSettings.GetValueOrDefault(recipeId);

        RecipeCraftSettings? effectiveSettings = sourceSettings?.Clone();
        if (_list.ShouldForcePreferNQ(isOriginalRecipe))
        {
            effectiveSettings ??= new RecipeCraftSettings();
            effectiveSettings.UseAllNQ = true;
            effectiveSettings.IngredientPreferences.Clear();
        }

        return effectiveSettings;
    }

    private bool IsEffectivelyQuickSynth(Recipe recipe, uint recipeId, bool isOriginalRecipe)
    {
        var recipeOptions = _list.GetRecipeOptions(recipeId, isOriginalRecipe);
        return recipeOptions.NQOnly || _list.ShouldForceQuickSynth(recipe, isOriginalRecipe);
    }

    private bool WillUseQuickSynth(Recipe recipe, uint recipeId, bool isOriginalRecipe)
        => IsEffectivelyQuickSynth(recipe, recipeId, isOriginalRecipe) && recipe.CanQuickSynth && HasRecipeCraftedBefore(recipe);

    private static bool HasRecipeCraftedBefore(Recipe recipe)
    {
        if (recipe.SecretRecipeBook.RowId > 0)
            return true;

        return FFXIVClientStructs.FFXIV.Client.Game.QuestManager.IsRecipeComplete(recipe.RowId);
    }
    
    internal Dictionary<uint, int> GetCachedMaterials()
    {
        var currentHash = ComputeListHash();
        if (_cachedMaterialsValid && _cachedMaterials != null && currentHash == _cachedMaterialsHash)
        {
            return _cachedMaterials;
        }

        var additionalAvailable = BuildRetainerAdditionalAvailable(_list);
        _cachedMaterials = _list.ListMaterials(additionalAvailable);
        _cachedMaterialsHash = currentHash;
        _cachedMaterialsValid = true;

        return _cachedMaterials;
    }

    internal Dictionary<uint, int> GetCachedPrecraftMaterials()
    {
        var currentHash = ComputeListHash();
        if (_cachedPrecraftMaterials != null && currentHash == _cachedPrecraftMaterialsHash)
            return _cachedPrecraftMaterials;

        var allPrecrafts = _list.ListPrecrafts();

        if (_list.RetainerRestock && _list.SkipIfEnough && AllaganTools.Enabled)
        {
            var adjusted = new Dictionary<uint, int>();
            foreach (var (itemId, needed) in allPrecrafts)
            {
                var inBag      = GetInventoryCount(itemId);
                var inRetainer = RetainerItemQuery.GetTotalCount(itemId);
                var stillNeeded = Math.Max(0, needed - inBag - inRetainer);
                if (stillNeeded > 0)
                    adjusted[itemId] = stillNeeded;
            }
            _cachedPrecraftMaterials = adjusted;
        }
        else
        {
            _cachedPrecraftMaterials = allPrecrafts;
        }

        _cachedPrecraftMaterialsHash = currentHash;
        return _cachedPrecraftMaterials;
    }

    private static string GetConsumableSummary(CraftingListConsumableSettings settings)
    {
        var parts = new List<string>();

        if (settings.FoodItemId.HasValue)
            parts.Add($"Food: {GetItemLabel(settings.FoodItemId.Value, settings.FoodHQ)}");
        if (settings.MedicineItemId.HasValue)
            parts.Add($"Medicine: {GetItemLabel(settings.MedicineItemId.Value, settings.MedicineHQ)}");
        if (settings.ManualItemId.HasValue)
            parts.Add($"Manual: {GetItemLabel(settings.ManualItemId.Value, false)}");
        if (settings.SquadronManualItemId.HasValue)
            parts.Add($"Squadron: {GetItemLabel(settings.SquadronManualItemId.Value, false)}");

        return parts.Count > 0 ? string.Join(" | ", parts) : "None";
    }

    private static string GetItemLabel(uint itemId, bool hq)
    {
        var itemSheet = Dalamud.GameData.GetExcelSheet<Item>();
        if (itemSheet != null && itemSheet.TryGetRow(itemId, out var item))
            return item.Name.ExtractText() + (hq ? " HQ" : "");
        return itemId.ToString();
    }
    
    internal unsafe int GetInventoryCount(uint itemId)
    {
        var now = DateTime.Now;
        
        if (_inventoryRefreshTimes.TryGetValue(itemId, out var lastRefresh))
        {
            if ((now - lastRefresh).TotalSeconds < InventoryRefreshIntervalSeconds)
            {
                return _cachedInventoryCounts.GetValueOrDefault(itemId, 0);
            }
        }
        
        try
        {
            var inventory = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
            if (inventory == null)
                return 0;
            
            var count = inventory->GetInventoryItemCount(itemId, false, false, false)
                      + inventory->GetInventoryItemCount(itemId, true, false, false);
            _cachedInventoryCounts[itemId] = count;
            _inventoryRefreshTimes[itemId] = now;
            return count;
        }
        catch
        {
            return 0;
        }
    }

    internal int GetRetainerCount(uint itemId)
        => RetainerItemQuery.GetTotalCount(itemId);
    internal void InvalidateRetainerSnapshot()
    {
        _cachedRetainerSnapshot = RetainerItemSnapshot.Empty;
        _cachedRetainerSnapshotItemIds = [];
        _cachedRetainerSnapshotAt = DateTime.MinValue;
    }

    internal RetainerItemSnapshot GetRetainerSnapshot(IEnumerable<uint> itemIds, bool forceRefresh = false)
    {
        if (!AllaganTools.Enabled)
            return RetainerItemSnapshot.Empty;

        var snapshotItemIds = itemIds
            .Where(id => id > 0)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();

        if (snapshotItemIds.Length == 0)
            return RetainerItemSnapshot.Empty;

        if (!forceRefresh && _cachedRetainerSnapshotItemIds.SequenceEqual(snapshotItemIds))
        {
            if (_cachedRetainerSnapshot.IsComplete)
                return _cachedRetainerSnapshot;

            if ((DateTime.Now - _cachedRetainerSnapshotAt).TotalSeconds < RetainerSnapshotRetryIntervalSeconds)
                return _cachedRetainerSnapshot;
        }

        _cachedRetainerSnapshot = RetainerItemQuery.CreateSnapshot(snapshotItemIds);
        _cachedRetainerSnapshotItemIds = snapshotItemIds;
        _cachedRetainerSnapshotAt = DateTime.Now;
        return _cachedRetainerSnapshot;
    }
    
    private unsafe bool WillBeSkippedDueToInventory(Recipe recipe, int quantityToCraft)
    {
        try
        {
            var inventory = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
            if (inventory == null)
                return false;
            
            var resultItemId = recipe.ItemResult.RowId;
            var amountPerCraft = recipe.AmountResult;
            var totalNeeded = quantityToCraft * amountPerCraft;
            
            var nqCount = inventory->GetInventoryItemCount(resultItemId, false, false, false);
            var hqCount = inventory->GetInventoryItemCount(resultItemId, true, false, false);
            var totalCount = nqCount + hqCount;
            
            return totalCount >= totalNeeded;
        }
        catch
        {
            return false;
        }
    }

    private void ProcessPrecraftWithDependencies(CraftingListItem recipeItem, List<CraftingListItem> allRecipes, HashSet<uint> processed, List<CraftingListItem> result)
    {
        if (processed.Contains(recipeItem.RecipeId))
            return;
        
        var recipe = RecipeManager.GetRecipe(recipeItem.RecipeId);
        if (recipe == null)
            return;
        
        var ingredients = RecipeManager.GetIngredients(recipe.Value);
        foreach (var (itemId, _) in ingredients)
        {
            var depRecipe = RecipeManager.GetRecipeForItem(itemId);
            if (depRecipe.HasValue)
            {
                var depItem = allRecipes.FirstOrDefault(r => r.RecipeId == depRecipe.Value.RowId && !r.IsOriginalRecipe);
                if (depItem != null)
                {
                    ProcessPrecraftWithDependencies(depItem, allRecipes, processed, result);
                }
            }
        }
        
        processed.Add(recipeItem.RecipeId);
        result.Add(recipeItem);
    }
    
    private string? ResolveEffectiveMacroId(RecipeCraftSettings? settings, bool isPrecraft)
    {
        var isSpecific = settings != null
            && (settings.MacroMode == MacroOverrideMode.Specific
                || (settings.MacroMode == MacroOverrideMode.Inherit
                    && (!string.IsNullOrEmpty(settings.SelectedMacroId) || settings.SolverOverride != SolverOverrideMode.Default)));
        if (isSpecific)
            return settings?.SolverOverride == SolverOverrideMode.Default ? settings?.SelectedMacroId : null;

        var defaultSolverOverride = isPrecraft ? _list.DefaultPrecraftSolverOverride : _list.DefaultFinalSolverOverride;
        if (defaultSolverOverride != SolverOverrideMode.Default)
            return null;
        return isPrecraft ? _list.DefaultPrecraftMacroId : _list.DefaultFinalMacroId;
    }

    private (int hardFails, int warnings) CountValidationIssues()
    {
        var hardFails = 0;
        var warnings  = 0;

        foreach (var item in _list.Recipes)
        {
            var recipe = RecipeManager.GetRecipe(item.RecipeId);
            if (recipe != null && WillUseQuickSynth(recipe.Value, item.RecipeId, true))
                continue;

            var craftSettings = GetEffectiveCraftSettings(item.RecipeId, true);
            var macroId = ResolveEffectiveMacroId(craftSettings, false);
            if (string.IsNullOrEmpty(macroId))
                continue;
            var result = MacroValidator.GetOrCompute(item.RecipeId, macroId, craftSettings, _list.Consumables);
            if (result == null || result.Failure == MacroValidationFailure.NoStats)
                continue;
            if (!result.IsValid)
            {
                if (result.Failure is MacroValidationFailure.CPExhausted or MacroValidationFailure.DurabilityFailed)
                    hardFails++;
                else
                    warnings++;
            }
        }

        foreach (var queueItem in GetSortedQueue())
        {
            if (queueItem.IsOriginalRecipe)
                continue;

            var recipe = RecipeManager.GetRecipe(queueItem.RecipeId);
            if (recipe != null && WillUseQuickSynth(recipe.Value, queueItem.RecipeId, false))
                continue;

            var craftSettings = GetEffectiveCraftSettings(queueItem.RecipeId, false);
            var macroId = ResolveEffectiveMacroId(craftSettings, true);
            if (string.IsNullOrEmpty(macroId))
                continue;
            var result = MacroValidator.GetOrCompute(queueItem.RecipeId, macroId, craftSettings, _list.Consumables);
            if (result == null || result.Failure == MacroValidationFailure.NoStats)
                continue;
            if (!result.IsValid)
            {
                if (result.Failure is MacroValidationFailure.CPExhausted or MacroValidationFailure.DurabilityFailed)
                    hardFails++;
                else
                    warnings++;
            }
        }

        return (hardFails, warnings);
    }

    private string GetCraftingJobName(uint craftTypeId)
    {
        var classJobSheet = Dalamud.GameData.GetExcelSheet<ClassJob>();
        if (classJobSheet != null)
        {
            var classJobId = craftTypeId + 8;
            var classJob = classJobSheet.GetRow(classJobId);
            if (classJob.RowId > 0)
                return classJob.Abbreviation.ExtractText();
        }
        return "Unknown";
    }
}
