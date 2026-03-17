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
    private Dictionary<uint, int> _cachedRetainerCounts = new();
    private Dictionary<uint, DateTime> _retainerRefreshTimes = new();
    private const double InventoryRefreshIntervalSeconds = 0.5;
    
    private RecipeCraftSettingsPopup _craftSettingsPopup = new();
    private CraftingListConsumablesPopup _consumablesPopup = new();
    
    private int _editingQuantityIndex = -1;
    private int _tempQuantityInput = 0;
    private Dictionary<uint, int>? _cachedPrecraftMaterials = null;
    private string _cachedPrecraftMaterialsHash = string.Empty;
    
    internal bool HasCachedMaterials    => _cachedMaterials != null;
    internal bool IsGeneratingMaterials => _isGeneratingMaterials;
    internal string ListName            => _list.Name;
    
    public Action<CraftingListDefinition>? OnStartCrafting { get; set; }

    public CraftingListEditor(CraftingListDefinition list)
    {
        _list = list;
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
        _cachedRetainerCounts.Clear();
        _retainerRefreshTimes.Clear();
    }
    public void Draw()
    {
        var availableWidth = ImGui.GetContentRegionAvail().X;
        var availableHeight = ImGui.GetContentRegionAvail().Y;
        
        var leftPaneWidth = availableWidth * 0.4f;
        var rightPaneWidth = availableWidth - leftPaneWidth - 8;
        
        ImGui.BeginChild("LeftPane", new Vector2(leftPaneWidth, availableHeight), true);
        DrawQueuePane();
        ImGui.EndChild();
        
        ImGui.SameLine();
        
        ImGui.BeginChild("RightPane", new Vector2(rightPaneWidth, availableHeight), true);
        DrawDetailsPane();
        ImGui.EndChild();
        
        _craftSettingsPopup.Draw();
        _consumablesPopup.Draw();
    }

    private void DrawQueuePane()
    {
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.3f, 0.9f, 0.9f, 1));
        ImGui.Text("CRAFT QUEUE");
        ImGui.PopStyleColor();
        
        ImGui.Separator();
        ImGui.Spacing();
        
        if (_list.Recipes.Count == 0)
        {
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1), "No recipes in queue.");
            ImGui.Spacing();
            ImGui.TextWrapped("Add recipes using the panel on the right.");
            return;
        }
        
        if (_isGeneratingQueue)
        {
            ImGui.TextColored(new Vector4(0.3f, 0.9f, 0.9f, 1), "Calculating craft queue...");
            return;
        }
        
        var (hardFails, warnings) = CountValidationIssues();
        if (hardFails > 0)
            ImGui.PushStyleColor(ImGuiCol.Button, ImGuiColors.DalamudRed);
        else if (warnings > 0)
            ImGui.PushStyleColor(ImGuiCol.Button, ImGuiColors.DalamudYellow);

        if (ImGui.Button("Start Crafting", new Vector2(-1, 22)))
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
            ImGui.TextColored(ImGuiColors.DalamudRed, $"{hardFails} macro(s) are predicted to FAIL their craft.");
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
        
        if (ImGui.Button("Generate Gather List", new Vector2(-1, 22)))
        {
            var materials = _list.ListMaterials();
            CraftingGatherBridge.CreatePersistentGatherList($"{_list.Name}...Auto-Generated", materials);
        }
        
        var matsBtnLabel = GatherBuddy.CraftingMaterialsWindow?.IsOpen == true ? "Hide Materials" : "View Materials";
        if (ImGui.Button(matsBtnLabel, new Vector2(-1, 22)) && GatherBuddy.CraftingMaterialsWindow != null)
            GatherBuddy.CraftingMaterialsWindow.IsOpen = !GatherBuddy.CraftingMaterialsWindow.IsOpen;
        
        if (ImGui.Button("Edit List Consumables/Macros", new Vector2(-1, 22)))
            _consumablesPopup.OpenListDefaults(_list);
        
        ImGui.Spacing();
        ImGui.Checkbox("Show Precrafts", ref _showPrecrafts);
        
        var skipIfEnough = _list.SkipIfEnough;
        if (ImGui.Checkbox("Skip if Already Have Enough", ref skipIfEnough))
        {
            _list.SkipIfEnough = skipIfEnough;
            GatherBuddy.CraftingListManager.SaveList(_list);
            _cachedQueueValid = false;
            _cachedMaterialsValid = false;
            TriggerQueueRegeneration();
            RefreshInventoryCounts();
        }

        var quickSynthAll = _list.QuickSynthAll;
        if (ImGui.Checkbox("Quick Synth All", ref quickSynthAll))
        {
            _list.QuickSynthAll = quickSynthAll;
            GatherBuddy.CraftingListManager.SaveList(_list);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Force Quick Synthesis on every eligible item in this list, overriding per-item solver settings.");
        ImGui.Spacing();
        
        ImGui.Separator();
        
        var sortedQueue = GetSortedQueue();
        var displayQueue = _showPrecrafts ? sortedQueue : _list.Recipes.Select(r => new CraftingListItem(r.RecipeId, r.Quantity)).ToList();
        
        if (displayQueue.Count == 0)
        {
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1), "Queue is empty.");
            return;
        }
        
        var height = ImGui.GetTextLineHeightWithSpacing();
        var childHeight = ImGui.GetContentRegionAvail().Y;
        
        ImGui.BeginChild("QueueList", new Vector2(-1, childHeight), false);
        
        var originalRecipes = new HashSet<uint>(_list.Recipes.Select(r => r.RecipeId));
        
        void DrawQueueItem(int idx)
        {
            var queueItem = displayQueue[idx];
            var recipeData = RecipeManager.GetRecipe(queueItem.RecipeId);
            if (recipeData == null) return;
            
            var itemName = recipeData.Value.ItemResult.Value.Name.ExtractText();
            var jobName = GetCraftingJobName(recipeData.Value.CraftType.RowId);
            
            var isOriginalRecipe = originalRecipes.Contains(queueItem.RecipeId);
            var willBeSkipped = _list.SkipIfEnough && WillBeSkippedDueToInventory(recipeData.Value, queueItem.Quantity);

            var recipeOptions = _list.GetRecipeOptions(queueItem.RecipeId);
            var quickSynthPrefix = recipeOptions.NQOnly ? "[QS] " : "";

            Vector4 textColor;
            if (willBeSkipped)
            {
                textColor = new Vector4(1, 0.3f, 0.3f, 1);
            }
            else if (recipeOptions.NQOnly)
            {
                textColor = new Vector4(0.3f, 0.9f, 0.9f, 1);
            }
            else if (isOriginalRecipe)
            {
                textColor = new Vector4(1, 1, 1, 1);
            }
            else
            {
                textColor = new Vector4(0.7f, 0.7f, 0.7f, 1);
            }

            var queueItemCraftSettings = isOriginalRecipe
                ? _list.Recipes.FirstOrDefault(r => r.RecipeId == queueItem.RecipeId)?.CraftSettings
                : _list.PrecraftCraftSettings.GetValueOrDefault(queueItem.RecipeId);
            var queueItemValidation = MacroValidator.GetOrCompute(queueItem.RecipeId, ResolveEffectiveMacroId(queueItemCraftSettings, !isOriginalRecipe), queueItemCraftSettings, _list.Consumables);
            if (queueItemValidation != null)
            {
                var dotColor = queueItemValidation.IsValid
                    ? ImGuiColors.ParsedGreen
                    : (queueItemValidation.Failure is MacroValidationFailure.InsufficientProgress or MacroValidationFailure.ActionUnusable
                        ? ImGuiColors.DalamudYellow
                        : ImGuiColors.DalamudRed);
                ImGui.TextColored(dotColor, "\u25cf");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(queueItemValidation.IsValid
                        ? $"Macro: PASS\nProgress: {queueItemValidation.FinalProgress}/{queueItemValidation.RequiredProgress}\nQuality: {queueItemValidation.FinalQuality}\nDurability: {queueItemValidation.FinalDurability}"
                        : $"Macro: {queueItemValidation.Failure} at step {queueItemValidation.FailedAtStep}\nProgress: {queueItemValidation.FinalProgress}/{queueItemValidation.RequiredProgress}");
                ImGui.SameLine();
            }

            ImGui.PushStyleColor(ImGuiCol.Text, textColor);
            var isSelected = _selectedQueueIndex == idx;
            var label = $"{quickSynthPrefix}{idx + 1}. {itemName} x{queueItem.Quantity} ({jobName})";

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
                    var useQuickSynth = recipeOptions.NQOnly;
                if (ImGui.MenuItem("Quick Synthesis", "", useQuickSynth))
                {
                    _list.SetRecipeQuickSynth(queueItem.RecipeId, !useQuickSynth);
                    GatherBuddy.CraftingListManager.SaveList(_list);
                    _cachedQueueValid = false;
                    _cachedMaterialsValid = false;
                    TriggerQueueRegeneration();
                }
                    
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip("Use quick synthesis for this recipe (NQ only)");
                    }
                }
                else
                {
                    ImGui.TextDisabled("Quick Synthesis not available");
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip("Recipe must be unlocked and previously crafted to use Quick Synthesis");
                    }
                }
                
                ImGui.EndPopup();
            }
        }
        
        for (int i = 0; i < displayQueue.Count; i++)
        {
            DrawQueueItem(i);
        }
        
        ImGui.EndChild();
    }
    
    private void DrawDetailsPane()
    {
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.3f, 0.9f, 0.9f, 1));
        ImGui.Text("DETAILS & MANAGEMENT");
        ImGui.PopStyleColor();
        
        ImGui.Separator();
        ImGui.Spacing();
        
        if (ImGui.CollapsingHeader("Add Recipe", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawAddRecipeSection();
        }
        
        ImGui.Spacing();
        
        if (ImGui.CollapsingHeader("Recipe List Management", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawRecipeListSection();
        }
        
        ImGui.Spacing();

        if (ImGui.CollapsingHeader("List Consumables", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawListConsumablesSection();
        }
        
    }

    private void DrawListConsumablesSection()
    {
        var hadAny = false;
        if (_list.Consumables.FoodItemId.HasValue)
        {
            ImGui.Text($"Food: {GetItemLabel(_list.Consumables.FoodItemId.Value, _list.Consumables.FoodHQ)}");
            hadAny = true;
        }
        if (_list.Consumables.MedicineItemId.HasValue)
        {
            ImGui.Text($"Medicine: {GetItemLabel(_list.Consumables.MedicineItemId.Value, _list.Consumables.MedicineHQ)}");
            hadAny = true;
        }
        if (_list.Consumables.ManualItemId.HasValue)
        {
            ImGui.Text($"Manual: {GetItemLabel(_list.Consumables.ManualItemId.Value, false)}");
            hadAny = true;
        }
        if (_list.Consumables.SquadronManualItemId.HasValue)
        {
            ImGui.Text($"Squadron: {GetItemLabel(_list.Consumables.SquadronManualItemId.Value, false)}");
            hadAny = true;
        }

        if (hadAny)
            ImGui.Spacing();
    }
    
    private void DrawAddRecipeSection()
    {
        ImGui.Text("Add Recipe to List");
        ImGui.Spacing();

        if (_recipeCombo == null)
        {
            InitializeRecipeCombo();
        }

        DrawRecipeComboWithKeywordFilter();

        ImGui.Text("Quantity:");
        ImGui.SetNextItemWidth(100);
        ImGui.InputInt("##quantity", ref _searchQuantity, 1);
        if (_searchQuantity < 1)
            _searchQuantity = 1;

        ImGui.Spacing();
        
        var buttonEnabled = _selectedRecipe != null;
        
        if (!buttonEnabled)
            ImGui.BeginDisabled();
        
        var buttonClicked = ImGui.Button("Add Recipe", new Vector2(100, 0));
        
        if (!buttonClicked && ImGui.IsItemHovered() && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
        {
            buttonClicked = true;
        }
        
        if (buttonClicked && _selectedRecipe != null)
        {
            _list.AddRecipe(_selectedRecipe.Value.RowId, _searchQuantity);
            GatherBuddy.CraftingListManager.SaveList(_list);
            _cachedQueueValid = false;
            _cachedMaterialsValid = false;
            TriggerQueueRegeneration();
            _selectedRecipe = null;
            _searchQuantity = 1;
        }
        
        if (!buttonEnabled)
            ImGui.EndDisabled();
        
        if (ImGui.IsItemHovered() && _selectedRecipe != null)
            ImGui.SetTooltip($"Add {_recipeLabels[_selectedRecipe.Value.RowId]} x{_searchQuantity} to list");
    }

    private void DrawRecipeComboWithKeywordFilter()
    {
        ImGui.SetNextItemWidth(300);
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
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1), "No recipes added yet.");
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
            
            var skipIndicator = item.Options.Skipping ? "[SKIP] " : "";
            var hqIndicator = (item.IngredientPreferences.Count > 0 || item.CraftSettings?.IngredientPreferences.Count > 0) ? "[HQ] " : "";
            var quickSynthIndicator = item.Options.NQOnly ? "[QS] " : "";
            var craftSettingsIndicator = item.CraftSettings?.HasAnySettings() == true ? "[SET] " : "";
            var textColor = item.Options.Skipping ? new Vector4(0.7f, 0.7f, 0.7f, 1) : new Vector4(1, 1, 1, 1);

            var validation = MacroValidator.GetOrCompute(item.RecipeId, ResolveEffectiveMacroId(item.CraftSettings, false), item.CraftSettings, _list.Consumables);
            if (validation != null)
            {
                var dotColor = validation.IsValid
                    ? ImGuiColors.ParsedGreen
                    : (validation.Failure is MacroValidationFailure.InsufficientProgress or MacroValidationFailure.ActionUnusable
                        ? ImGuiColors.DalamudYellow
                        : ImGuiColors.DalamudRed);
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
                
                var materials = _list.ListMaterials();
                
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
        
        var originalRecipes = new HashSet<uint>();
        foreach (var item in _list.Recipes)
        {
            originalRecipes.Add(item.RecipeId);
        }
        
        var precrafts = new List<CraftingListItem>();
        var finalProducts = new List<CraftingListItem>();
        
        foreach (var recipe in queue.Recipes)
        {
            if (originalRecipes.Contains(recipe.RecipeId))
            {
                finalProducts.Add(recipe);
            }
            else
            {
                precrafts.Add(recipe);
            }
        }
        
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
                ProcessPrecraftWithDependencies(recipeItem, queue.Recipes, processedPrecrafts, sortedPrecrafts);
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
    
    internal Dictionary<uint, int> GetCachedMaterials()
    {
        var currentHash = ComputeListHash();
        if (_cachedMaterialsValid && _cachedMaterials != null && currentHash == _cachedMaterialsHash)
        {
            return _cachedMaterials;
        }
        
        _cachedMaterials = _list.ListMaterials();
        _cachedMaterialsHash = currentHash;
        _cachedMaterialsValid = true;
        
        return _cachedMaterials;
    }

    internal Dictionary<uint, int> GetCachedPrecraftMaterials()
    {
        var currentHash = ComputeListHash();
        if (_cachedPrecraftMaterials != null && currentHash == _cachedPrecraftMaterialsHash)
            return _cachedPrecraftMaterials;

        _cachedPrecraftMaterials = _list.ListPrecrafts();
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
    {
        return (int)RetainerCache.GetRetainerItemCount(itemId);
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
                var depItem = allRecipes.FirstOrDefault(r => r.RecipeId == depRecipe.Value.RowId);
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
        var isSpecific = settings?.MacroMode == MacroOverrideMode.Specific
            || (settings?.MacroMode == MacroOverrideMode.Inherit && !string.IsNullOrEmpty(settings?.SelectedMacroId));
        if (isSpecific)
            return settings?.SelectedMacroId;
        return isPrecraft ? _list.DefaultPrecraftMacroId : _list.DefaultFinalMacroId;
    }

    private (int hardFails, int warnings) CountValidationIssues()
    {
        var hardFails = 0;
        var warnings  = 0;

        foreach (var item in _list.Recipes)
        {
            var macroId = ResolveEffectiveMacroId(item.CraftSettings, false);
            if (string.IsNullOrEmpty(macroId))
                continue;
            var result = MacroValidator.GetOrCompute(item.RecipeId, macroId, item.CraftSettings, _list.Consumables);
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

        var originalRecipeIds = new HashSet<uint>(_list.Recipes.Select(r => r.RecipeId));
        foreach (var queueItem in GetSortedQueue())
        {
            if (originalRecipeIds.Contains(queueItem.RecipeId))
                continue;
            var craftSettings = _list.PrecraftCraftSettings.GetValueOrDefault(queueItem.RecipeId);
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
