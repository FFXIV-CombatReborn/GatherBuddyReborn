using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Inventory;
using Dalamud.Game.Inventory.InventoryEventArgTypes;
using Dalamud.Interface.Colors;
using Lumina.Excel.Sheets;
using ElliLib;
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
    
    private Dictionary<uint, (int NQ, int HQ)> _cachedInventorySplitCounts = new();
    private Dictionary<uint, DateTime> _inventoryRefreshTimes = new();
    private RetainerItemSnapshot _cachedRetainerSnapshot = RetainerItemSnapshot.Empty;
    private uint[] _cachedRetainerSnapshotItemIds = [];
    private DateTime _cachedRetainerSnapshotAt = DateTime.MinValue;
    private readonly HashSet<uint> _watchedInventoryItemIds = new();
    private readonly HashSet<uint> _watchedOriginalResultItemIds = new();
    private readonly HashSet<uint> _watchedPrecraftResultItemIds = new();
    private readonly object _inventoryChangeLock = new();
    private DateTime _lastGraphAffectingInventoryChange = DateTime.MinValue;
    private string _watchedInventoryHash = string.Empty;
    private bool _pendingQueueRefreshFromInventory;
    private bool _pendingMaterialsRefreshFromInventory;
    private const double InventoryRefreshIntervalSeconds = 0.5;
    private const double RetainerSnapshotRetryIntervalSeconds = 1.0;
    private const double InventoryChangeDebounceSeconds = 0.2;
    
    private RecipeCraftSettingsPopup _craftSettingsPopup = new();
    private CraftingListConsumablesPopup _consumablesPopup = new();
    
    private int _editingQuantityIndex = -1;
    private int _tempQuantityInput = 0;
    private readonly HashSet<int> _selectedRecipeIndices = new();
    private int _lastClickedRecipeIndex = -1;
    private Dictionary<uint, int>? _cachedPrecraftMaterials = null;
    private Dictionary<uint, IngredientQualityDemand>? _cachedIngredientDemands = null;
    private Dictionary<uint, IngredientQualityDemand>? _cachedCraftMaterialDemands = null;
    private string _cachedPrecraftMaterialsHash = string.Empty;
    private Dictionary<uint, int>? _cachedDisplayMaterials = null;
    private Dictionary<uint, int>? _cachedDisplayPrecraftMaterials = null;
    private Dictionary<uint, IngredientQualityDemand>? _cachedDisplayIngredientDemands = null;
    private Dictionary<uint, IngredientQualityDemand>? _cachedDisplayCraftMaterialDemands = null;
    private string _cachedDisplayMaterialsHash = string.Empty;
    private sealed class QueueDisplayRow
    {
        public int QueueIndex { get; init; }
        public int Quantity { get; init; }
        public bool IsOriginalRecipe { get; init; }
        public Recipe Recipe { get; init; }
        public string ItemName { get; init; } = string.Empty;
        public string Label { get; init; } = string.Empty;
        public Vector4 BaseTextColor { get; init; }
        public bool EffectiveQuickSynth { get; init; }
        public bool ForceQuickSynth { get; init; }
        public MacroValidationResult? Validation { get; init; }
    }

    private sealed class RecipeDisplayRow
    {
        public int ListIndex { get; init; }
        public Recipe Recipe { get; init; }
        public string ItemName { get; init; } = string.Empty;
        public string Label { get; init; } = string.Empty;
        public Vector4 TextColor { get; init; }
        public MacroValidationResult? Validation { get; init; }
    }

    private List<QueueDisplayRow>? _cachedQueueDisplayRows = null;
    private List<QueueDisplayRow>? _cachedOriginalQueueDisplayRows = null;
    private string _cachedQueueDisplayRowsHash = string.Empty;
    private bool _cachedQueueDisplayRowsValid = false;
    private List<RecipeDisplayRow>? _cachedRecipeDisplayRows = null;
    private bool _cachedRecipeDisplayRowsValid = false;
    private (int HardFails, int Warnings) _cachedValidationIssueCounts;
    private string _cachedValidationIssueCountsHash = string.Empty;
    private bool _cachedValidationIssueCountsValid = false;

    private string _editingName        = string.Empty;
    private string _editingDescription = string.Empty;
    private bool   _nameConflict       = false;
    private bool   _editingDescActive  = false;
    private bool   _focusDescNext      = false;
    private long _materialCacheVersion;
    
    internal bool HasCachedMaterials    => _cachedMaterials != null;
    internal bool HasCachedDisplayMaterials => _cachedDisplayMaterials != null;
    internal bool IsGeneratingMaterials => _isGeneratingMaterials;
    internal string ListName            => GetPlanningList().Name;
    internal bool SkipIfEnoughEnabled   => GetPlanningList().SkipIfEnough;
    internal bool RetainerRestockEnabled => GetPlanningList().RetainerRestock;
    internal long MaterialCacheVersion  => Interlocked.Read(ref _materialCacheVersion);
    
    public Action<CraftingListDefinition>? OnStartCrafting { get; set; }

    public CraftingListEditor(CraftingListDefinition list)
    {
        _list               = list;
        _editingName        = list.Name;
        _editingDescription = list.Description;
        _craftSettingsPopup.OnSaved = HandleEditorSettingsSaved;
        _consumablesPopup.OnSaved = HandleEditorSettingsSaved;
        RefreshInventoryCounts();
        Dalamud.GameInventory.InventoryChanged += OnInventoryChanged;
        TriggerQueueRegeneration();
    }

    private CraftingExecutionPlan? GetActiveExecutionPlan()
        => CraftingGatherBridge.GetActiveExecutionPlan(_list.ID);

    private CraftingListDefinition GetPlanningList()
        => GetActiveExecutionPlan()?.PlanningSnapshot ?? _list;

    private bool TryCacheActiveExecutionPlan(string hash)
    {
        var activeExecutionPlan = GetActiveExecutionPlan();
        if (activeExecutionPlan == null)
            return false;

        if (!_cachedQueueValid || _cachedSortedQueue == null || _cachedListHash != hash)
        {
            _cachedSortedQueue = BuildDisplayQueue(activeExecutionPlan.ResolvedPlan);
            _cachedListHash = hash;
            _cachedQueueValid = true;
        }

        if (!_cachedMaterialsValid || _cachedMaterials == null || _cachedMaterialsHash != hash
         || _cachedPrecraftMaterials == null || _cachedPrecraftMaterialsHash != hash
         || _cachedIngredientDemands == null || _cachedCraftMaterialDemands == null)
        {
            _cachedMaterials = activeExecutionPlan.Materials;
            _cachedIngredientDemands = activeExecutionPlan.IngredientDemands;
            _cachedPrecraftMaterials = BuildCraftPanelMaterials(activeExecutionPlan.ResolvedPlan);
            _cachedCraftMaterialDemands = BuildCraftPanelDemands(activeExecutionPlan.ResolvedPlan, _cachedPrecraftMaterials);
            _cachedMaterialsHash = hash;
            _cachedPrecraftMaterialsHash = hash;
            _cachedMaterialsValid = true;
        }

        if (_cachedDisplayMaterials == null || _cachedDisplayMaterialsHash != hash
         || _cachedDisplayPrecraftMaterials == null
         || _cachedDisplayIngredientDemands == null
         || _cachedDisplayCraftMaterialDemands == null)
        {
            CacheDisplayMaterialPlan(CreateDisplayMaterialPlan(), hash);
        }
        return true;
    }
    
    public void Dispose()
    {
        Dalamud.GameInventory.InventoryChanged -= OnInventoryChanged;
        _queueCancellationSource?.Cancel();
        _queueCancellationSource?.Dispose();
        _materialsCancellationSource?.Cancel();
        _materialsCancellationSource?.Dispose();
    }
    
    public void RefreshInventoryCounts()
    {
        _cachedInventorySplitCounts.Clear();
        _inventoryRefreshTimes.Clear();
        InvalidateRetainerSnapshot();
    }

    internal void RefreshFromExternalListChange()
    {
        GatherBuddy.Log.Debug($"[CraftingListEditor] Refreshing cached queue/materials for externally modified list '{_list.Name}'");
        _selectedRecipeIndices.Clear();
        _lastClickedRecipeIndex = -1;
        _cachedQueueValid = false;
        InvalidateMaterialCaches();
        InvalidatePresentationCaches();
        TriggerQueueRegeneration();
        TriggerMaterialsRegeneration();
        if (!_editingDescActive)
            _editingDescription = _list.Description;
    }

    private void HandleEditorSettingsSaved()
    {
        GatherBuddy.Log.Debug($"[CraftingListEditor] Refreshing presentation caches after settings change for list '{_list.Name}'");
        InvalidatePresentationCaches();
        _cachedQueueValid = false;
        InvalidateMaterialCaches();
        TriggerQueueRegeneration();
        TriggerMaterialsRegeneration();
    }
    private void InvalidateQueuePresentationCaches()
    {
        _cachedQueueDisplayRows = null;
        _cachedOriginalQueueDisplayRows = null;
        _cachedQueueDisplayRowsHash = string.Empty;
        _cachedQueueDisplayRowsValid = false;
        _cachedValidationIssueCounts = default;
        _cachedValidationIssueCountsHash = string.Empty;
        _cachedValidationIssueCountsValid = false;
    }

    private void InvalidatePresentationCaches()
    {
        InvalidateQueuePresentationCaches();
        _cachedRecipeDisplayRows = null;
        _cachedRecipeDisplayRowsValid = false;
    }
    public void Draw()
    {
        ProcessPendingInventoryChanges();
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

    private void OnInventoryChanged(IReadOnlyCollection<InventoryEventArgs> events)
    {
        EnsureWatchedInventoryItems();
        var changedItemIds = new HashSet<uint>();
        var planningList = GetPlanningList();

        var graphAffected = false;
        foreach (var inventoryEvent in events)
        {
            foreach (var itemId in GetAffectedTrackedInventoryItemIds(inventoryEvent))
            {
                if (itemId == 0 || !_watchedInventoryItemIds.Contains(itemId))
                    continue;

                changedItemIds.Add(itemId);

                if ((planningList.SkipIfEnough && _watchedPrecraftResultItemIds.Contains(itemId))
                 || (planningList.SkipIfEnough && planningList.SkipFinalIfEnough && _watchedOriginalResultItemIds.Contains(itemId)))
                {
                    graphAffected = true;
                }
            }
        }

        if (changedItemIds.Count == 0)
            return;

        foreach (var itemId in changedItemIds)
        {
            _cachedInventorySplitCounts.Remove(itemId);
            _inventoryRefreshTimes.Remove(itemId);
        }

        Interlocked.Increment(ref _materialCacheVersion);

        if (!graphAffected)
            return;

        lock (_inventoryChangeLock)
        {
            _pendingQueueRefreshFromInventory = true;
            _pendingMaterialsRefreshFromInventory = true;
            _lastGraphAffectingInventoryChange = DateTime.Now;
        }
    }

    private static IEnumerable<uint> GetAffectedTrackedInventoryItemIds(InventoryEventArgs inventoryEvent)
    {
        switch (inventoryEvent)
        {
            case InventoryComplexEventArgs complexEvent:
            {
                if (IsTrackedInventoryContainer(complexEvent.SourceInventory))
                {
                    var sourceItemId = complexEvent.SourceEvent.Item.BaseItemId;
                    if (sourceItemId > 0)
                        yield return sourceItemId;
                }

                if (IsTrackedInventoryContainer(complexEvent.TargetInventory))
                {
                    var targetItemId = complexEvent.TargetEvent.Item.BaseItemId;
                    if (targetItemId > 0)
                        yield return targetItemId;
                }

                yield break;
            }
            case InventoryItemAddedArgs addedEvent when IsTrackedInventoryContainer(addedEvent.Inventory):
            {
                var itemId = addedEvent.Item.BaseItemId;
                if (itemId > 0)
                    yield return itemId;
                yield break;
            }
            case InventoryItemRemovedArgs removedEvent when IsTrackedInventoryContainer(removedEvent.Inventory):
            {
                var itemId = removedEvent.Item.BaseItemId;
                if (itemId > 0)
                    yield return itemId;
                yield break;
            }
            case InventoryItemChangedArgs changedEvent when IsTrackedInventoryContainer(changedEvent.Inventory):
            {
                var oldItemId = changedEvent.OldItemState.BaseItemId;
                if (oldItemId > 0)
                    yield return oldItemId;

                var itemId = changedEvent.Item.BaseItemId;
                if (itemId > 0 && itemId != oldItemId)
                    yield return itemId;
                yield break;
            }
            default:
            {
                if (!IsTrackedInventoryContainer(inventoryEvent.Item.ContainerType))
                    yield break;

                var itemId = inventoryEvent.Item.BaseItemId;
                if (itemId > 0)
                    yield return itemId;
                yield break;
            }
        }
    }

    private void ProcessPendingInventoryChanges()
    {
        bool refreshQueue;
        bool refreshMaterials;
        lock (_inventoryChangeLock)
        {
            if (!_pendingQueueRefreshFromInventory && !_pendingMaterialsRefreshFromInventory)
                return;

            if ((DateTime.Now - _lastGraphAffectingInventoryChange).TotalSeconds < InventoryChangeDebounceSeconds)
                return;

            refreshQueue = _pendingQueueRefreshFromInventory;
            refreshMaterials = _pendingMaterialsRefreshFromInventory;
            _pendingQueueRefreshFromInventory = false;
            _pendingMaterialsRefreshFromInventory = false;
        }

        if (refreshQueue)
        {
            _cachedQueueValid = false;
            InvalidateQueuePresentationCaches();
            TriggerQueueRegeneration();
        }

        if (refreshMaterials)
        {
            InvalidateMaterialCaches();
            TriggerMaterialsRegeneration();
        }
    }

    private void EnsureWatchedInventoryItems()
    {
        var currentHash = ComputeListHash();
        if (currentHash == _watchedInventoryHash)
            return;

        _watchedInventoryItemIds.Clear();
        _watchedOriginalResultItemIds.Clear();
        _watchedPrecraftResultItemIds.Clear();

        var visitedRecipes = new HashSet<uint>();
        foreach (var item in GetPlanningList().Recipes)
        {
            if (item.Options.Skipping || item.Quantity <= 0)
                continue;

            var recipe = RecipeManager.GetRecipe(item.RecipeId);
            if (recipe == null)
                continue;

            CollectWatchedInventoryItems(recipe.Value, true, visitedRecipes);
        }

        _watchedInventoryHash = currentHash;
    }

    private void CollectWatchedInventoryItems(Recipe recipe, bool isOriginalRecipe, HashSet<uint> visitedRecipes)
    {
        var resultItemId = recipe.ItemResult.RowId;
        if (resultItemId > 0)
        {
            _watchedInventoryItemIds.Add(resultItemId);
            if (isOriginalRecipe)
                _watchedOriginalResultItemIds.Add(resultItemId);
            else
                _watchedPrecraftResultItemIds.Add(resultItemId);
        }

        if (!visitedRecipes.Add(recipe.RowId))
            return;

        foreach (var (itemId, _) in RecipeManager.GetIngredients(recipe))
        {
            if (itemId > 0)
                _watchedInventoryItemIds.Add(itemId);

            var subRecipe = RecipeManager.GetRecipeForItem(itemId);
            if (subRecipe.HasValue)
                CollectWatchedInventoryItems(subRecipe.Value, false, visitedRecipes);
        }
    }

    private static bool IsTrackedInventoryContainer(GameInventoryType inventoryType)
        => inventoryType is GameInventoryType.Inventory1
            or GameInventoryType.Inventory2
            or GameInventoryType.Inventory3
            or GameInventoryType.Inventory4
            or GameInventoryType.Crystals;

    private void DrawQueuePane()
    {
        ImGui.TextColored(ImGuiColors.DalamudYellow, "Craft Queue");
        ImGui.Separator();
        ImGui.Spacing();
        var planningList = GetPlanningList();
        var activeExecutionPlan = GetActiveExecutionPlan();
        if (planningList.Recipes.Count == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "No recipes in queue.");
            ImGui.Spacing();
            ImGui.TextWrapped("Add recipes using the panel on the right.");
            return;
        }


        var lineH   = ImGui.GetTextLineHeightWithSpacing();
        var spacing = ImGui.GetStyle().ItemSpacing.Y;
        var frameH  = ImGui.GetFrameHeightWithSpacing();
        var footerRows = 7 + (_list.QuickSynthAll ? 2 : 0) + (_list.SkipIfEnough ? 1 : 0);
        var bottomH = frameH * footerRows + spacing * 2;
        var queueH  = Math.Max(ImGui.GetContentRegionAvail().Y - bottomH, lineH * 3);

        ImGui.BeginChild("QueueList", new Vector2(-1, queueH), false);

        if (_isGeneratingQueue)
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, "Calculating craft queue...");
        }
        else
        {
            var displayRows = GetDisplayQueueRows(planningList, activeExecutionPlan);
            if (displayRows.Count == 0)
            {
                ImGui.TextColored(ImGuiColors.DalamudGrey, "Queue is empty.");
            }
            else
            {
                var clipper = ImGui.ImGuiListClipper();
                clipper.Begin(displayRows.Count);
                while (clipper.Step())
                {
                    for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                        DrawQueueRow(displayRows[i], planningList);
                }
                clipper.End();
                clipper.Destroy();
            }
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
            InvalidateMaterialCaches();
            InvalidatePresentationCaches();
            GatherBuddy.CraftingListManager.SaveList(_list);
            TriggerQueueRegeneration();
            RefreshInventoryCounts();
        }

        if (_list.SkipIfEnough)
        {
            ImGui.Indent();
            var skipFinalIfEnough = _list.SkipFinalIfEnough;
            if (ImGui.Checkbox("Include Final Crafts##sife", ref skipFinalIfEnough))
            {
                _list.SkipFinalIfEnough = skipFinalIfEnough;
                _cachedQueueValid       = false;
                InvalidatePresentationCaches();
                GatherBuddy.CraftingListManager.SaveList(_list);
                TriggerQueueRegeneration();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Also reduce final crafts based on how many you already have. Useful for resuming an interrupted list.");
            ImGui.Unindent();
        }

        var quickSynthAll = _list.QuickSynthAll;
        if (ImGui.Checkbox("Quick Synth All##qsa", ref quickSynthAll))
        {
            _list.QuickSynthAll = quickSynthAll;
            GatherBuddy.CraftingListManager.SaveList(_list);
            _cachedQueueValid     = false;
            InvalidateMaterialCaches();
            InvalidatePresentationCaches();
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
                InvalidateMaterialCaches();
                InvalidatePresentationCaches();
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
                InvalidateMaterialCaches();
                InvalidatePresentationCaches();
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
                _cachedQueueValid = false;
                InvalidateMaterialCaches();
                InvalidatePresentationCaches();
                TriggerQueueRegeneration();
                TriggerMaterialsRegeneration();
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
        if (ImGui.Button("Add to New Gathering List##gatherList", new Vector2(halfW, 22)))
        {
            var materials = new Dictionary<uint, int>(GetCachedMaterials());
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
                ImGui.BeginChild("##notesDisplay", new Vector2(-1, 60f), true);

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

        ImGui.Spacing();
        var buttonWidth = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) / 2f;
        if (ImGui.Button("Export List##exportList", new Vector2(buttonWidth, 0)))
        {
            var exported = GatherBuddy.CraftingListManager.ExportList(_list.ID);
            if (exported != null)
            {
                ImGui.SetClipboardText(exported);
                GatherBuddy.Log.Information($"[CraftingListEditor] Exported list '{_list.Name}' to clipboard");
            }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Copy GatherBuddy's list export string to the clipboard.");

        ImGui.SameLine();
        if (ImGui.Button("TeamCraft Export##teamCraftExport", new Vector2(-1, 0)))
        {
            var (exported, error) = GatherBuddy.CraftingListManager.ExportListToTeamCraft(_list.ID);
            if (exported != null)
            {
                ImGui.SetClipboardText(exported);
                GatherBuddy.Log.Information($"[CraftingListEditor] Exported list '{_list.Name}' to TeamCraft and copied the link to the clipboard");
            }
            else if (!string.IsNullOrEmpty(error))
            {
                GatherBuddy.Log.Warning($"[CraftingListEditor] Failed to export '{_list.Name}' to TeamCraft: {error}");
            }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Copy a TeamCraft import link built from this list's Recipe List entries.");
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
                InvalidateMaterialCaches();
                InvalidatePresentationCaches();
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

        var indicesToRemove = new List<int>();

        if (_selectedRecipeIndices.Count > 1)
        {
            if (ImGui.Button($"Remove Selected ({_selectedRecipeIndices.Count})##removeSelected"))
                indicesToRemove.AddRange(_selectedRecipeIndices);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Remove the {_selectedRecipeIndices.Count} selected recipes from this list.");
        }
        var recipeRows = GetRecipeDisplayRows();
        var clipper = ImGui.ImGuiListClipper();
        clipper.Begin(recipeRows.Count);
        while (clipper.Step())
        {
            for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                DrawRecipeListRow(recipeRows[i], indicesToRemove);
        }
        clipper.End();
        clipper.Destroy();

        if (indicesToRemove.Count > 0)
        {
            foreach (var idx in indicesToRemove.Distinct().OrderByDescending(x => x))
                _list.Recipes.RemoveAt(idx);
            _selectedRecipeIndices.Clear();
            _lastClickedRecipeIndex = -1;
            GatherBuddy.CraftingListManager.SaveList(_list);
            _cachedQueueValid = false;
            InvalidateMaterialCaches();
            InvalidatePresentationCaches();
            TriggerQueueRegeneration();
        }
    }

    private IReadOnlyList<QueueDisplayRow> GetDisplayQueueRows(CraftingListDefinition planningList, CraftingExecutionPlan? activeExecutionPlan)
    {
        EnsureQueueDisplayRows(planningList, activeExecutionPlan);
        return _showPrecrafts
            ? (IReadOnlyList<QueueDisplayRow>)(_cachedQueueDisplayRows ?? new List<QueueDisplayRow>())
            : (IReadOnlyList<QueueDisplayRow>)(_cachedOriginalQueueDisplayRows ?? new List<QueueDisplayRow>());
    }

    private void EnsureQueueDisplayRows(CraftingListDefinition planningList, CraftingExecutionPlan? activeExecutionPlan)
    {
        if (!_cachedQueueValid || _cachedSortedQueue == null)
            return;

        var currentHash = ComputeListHash();
        if (_cachedQueueDisplayRowsValid && _cachedQueueDisplayRowsHash == currentHash
         && _cachedQueueDisplayRows != null && _cachedOriginalQueueDisplayRows != null)
            return;

        try
        {
            var sortedQueue = GetSortedQueue();
            _cachedQueueDisplayRows = BuildQueueDisplayRows(sortedQueue, planningList);
            IReadOnlyList<CraftingListItem> originalQueue = activeExecutionPlan != null
                ? activeExecutionPlan.OriginalRecipesView
                : sortedQueue
                    .Where(queueItem => queueItem.IsOriginalRecipe)
                    .Select(queueItem => new CraftingListItem(queueItem.RecipeId, queueItem.Quantity)
                    {
                        IsOriginalRecipe = true,
                    })
                    .ToList();
            _cachedOriginalQueueDisplayRows = BuildQueueDisplayRows(originalQueue, planningList);
            _cachedQueueDisplayRowsHash = currentHash;
            _cachedQueueDisplayRowsValid = true;
            _cachedValidationIssueCountsHash = string.Empty;
            _cachedValidationIssueCountsValid = false;
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[CraftingListEditor] Failed to rebuild queue display cache for list '{_list.Name}': {ex.Message}");
            _cachedQueueDisplayRows = new List<QueueDisplayRow>();
            _cachedOriginalQueueDisplayRows = new List<QueueDisplayRow>();
            _cachedQueueDisplayRowsHash = currentHash;
            _cachedQueueDisplayRowsValid = true;
            _cachedValidationIssueCountsHash = string.Empty;
            _cachedValidationIssueCountsValid = false;
        }
    }

    private List<QueueDisplayRow> BuildQueueDisplayRows(IReadOnlyList<CraftingListItem> sourceQueue, CraftingListDefinition planningList)
    {
        var rows = new List<QueueDisplayRow>(sourceQueue.Count);
        for (var i = 0; i < sourceQueue.Count; i++)
        {
            var queueItem = sourceQueue[i];
            var recipeData = RecipeManager.GetRecipe(queueItem.RecipeId);
            if (recipeData == null)
                continue;

            var itemName = recipeData.Value.ItemResult.Value.Name.ExtractText();
            var jobName = GetCraftingJobName(recipeData.Value.CraftType.RowId);
            var recipeOptions = planningList.GetRecipeOptions(queueItem.RecipeId, queueItem.IsOriginalRecipe);
            var effectiveQuickSynth = recipeOptions.NQOnly || planningList.ShouldForceQuickSynth(recipeData.Value, queueItem.IsOriginalRecipe);
            var forceQuickSynth = planningList.ShouldForceQuickSynth(recipeData.Value, queueItem.IsOriginalRecipe);
            var forcePreferNQNoQuickSynth = !recipeData.Value.CanQuickSynth && planningList.ShouldForcePreferNQ(queueItem.IsOriginalRecipe);
            var queueItemCraftSettings = GetEffectiveCraftSettings(queueItem.RecipeId, queueItem.IsOriginalRecipe);
            var validation = WillUseQuickSynth(recipeData.Value, queueItem.RecipeId, queueItem.IsOriginalRecipe)
                ? null
                : MacroValidator.GetOrCompute(queueItem.RecipeId,
                    ResolveEffectiveMacroId(queueItemCraftSettings, !queueItem.IsOriginalRecipe),
                    queueItemCraftSettings,
                    planningList.Consumables);
            rows.Add(new QueueDisplayRow
            {
                QueueIndex = i,
                Quantity = queueItem.Quantity,
                IsOriginalRecipe = queueItem.IsOriginalRecipe,
                Recipe = recipeData.Value,
                ItemName = itemName,
                Label = $"{(effectiveQuickSynth ? "[QS] " : forcePreferNQNoQuickSynth ? "[NQ] " : string.Empty)}{i + 1}. {itemName} x{queueItem.Quantity} ({jobName})",
                BaseTextColor = effectiveQuickSynth
                    ? new Vector4(0.3f, 0.9f, 0.9f, 1f)
                    : queueItem.IsOriginalRecipe
                        ? new Vector4(1f, 1f, 1f, 1f)
                        : new Vector4(0.7f, 0.7f, 0.7f, 1f),
                EffectiveQuickSynth = effectiveQuickSynth,
                ForceQuickSynth = forceQuickSynth,
                Validation = validation,
            });
        }

        return rows;
    }

    private void DrawQueueRow(QueueDisplayRow row, CraftingListDefinition planningList)
    {
        var willBeSkipped = planningList.SkipIfEnough
            && (!row.IsOriginalRecipe
                ? WillBeSkippedDueToInventory(row.Recipe)
                : planningList.SkipFinalIfEnough && row.Quantity == 0);
        var textColor = willBeSkipped
            ? new Vector4(1f, 0.3f, 0.3f, 1f)
            : row.BaseTextColor;

        if (row.Validation != null)
            DrawValidationMarker(row.Validation);

        ImGui.PushStyleColor(ImGuiCol.Text, textColor);
        var isSelected = _selectedQueueIndex == row.QueueIndex;
        if (ImGui.Selectable(row.Label, isSelected))
            _selectedQueueIndex = row.QueueIndex;
        ImGui.PopStyleColor();

        var isPopupOpen = GatherBuddy.ControllerSupport != null
            ? GatherBuddy.ControllerSupport.ContextMenu.BeginPopupContextItemWithGamepad($"queue_ctx_{row.QueueIndex}", Dalamud.GamepadState)
            : ImGui.BeginPopupContextItem($"queue_ctx_{row.QueueIndex}");

        if (!isPopupOpen)
            return;

        if (ImGui.MenuItem("Craft Settings..."))
        {
            if (row.IsOriginalRecipe)
            {
                var listItem = _list.Recipes.FirstOrDefault(recipeItem => recipeItem.RecipeId == row.Recipe.RowId);
                if (listItem != null)
                    _craftSettingsPopup.OpenForListItem(listItem, _list, row.ItemName);
            }
            else
            {
                _craftSettingsPopup.OpenForPrecraft(row.Recipe.RowId, row.ItemName, _list);
            }
        }

        var resultItemId = row.Recipe.ItemResult.RowId;
        var altRecipes = RecipeManager.GetRecipesForItem(resultItemId);
        if (altRecipes.Count > 1 && ImGui.BeginMenu("Change Job..."))
        {
            if (row.IsOriginalRecipe)
            {
                foreach (var alt in altRecipes)
                {
                    var altJob = GetCraftingJobName(alt.CraftType.RowId);
                    var isCurrent = alt.RowId == row.Recipe.RowId;
                    if (ImGui.MenuItem(altJob, string.Empty, isCurrent) && !isCurrent)
                    {
                        var listItem = _list.Recipes.FirstOrDefault(recipeItem => recipeItem.RecipeId == row.Recipe.RowId);
                        if (listItem != null)
                        {
                            listItem.RecipeId = alt.RowId;
                            GatherBuddy.CraftingListManager.SaveList(_list);
                            _cachedQueueValid = false;
                            InvalidateMaterialCaches();
                            InvalidatePresentationCaches();
                            TriggerQueueRegeneration();
                            TriggerMaterialsRegeneration();
                        }
                    }
                }
            }
            else
            {
                var activeRecipeId = _list.PrecraftRecipeOverrides.TryGetValue(resultItemId, out var overrideRecipeId)
                    ? overrideRecipeId
                    : altRecipes[0].RowId;
                foreach (var alt in altRecipes)
                {
                    var altJob = GetCraftingJobName(alt.CraftType.RowId);
                    var isCurrent = alt.RowId == activeRecipeId;
                    if (ImGui.MenuItem(altJob, string.Empty, isCurrent) && !isCurrent)
                    {
                        _list.PrecraftRecipeOverrides[resultItemId] = alt.RowId;
                        GatherBuddy.CraftingListManager.SaveList(_list);
                        _cachedQueueValid = false;
                        InvalidateMaterialCaches();
                        InvalidatePresentationCaches();
                        TriggerQueueRegeneration();
                        TriggerMaterialsRegeneration();
                    }
                }
                if (_list.PrecraftRecipeOverrides.ContainsKey(resultItemId))
                {
                    ImGui.Separator();
                    if (ImGui.MenuItem("Reset to Default"))
                    {
                        _list.PrecraftRecipeOverrides.Remove(resultItemId);
                        GatherBuddy.CraftingListManager.SaveList(_list);
                        _cachedQueueValid = false;
                        InvalidateMaterialCaches();
                        InvalidatePresentationCaches();
                        TriggerQueueRegeneration();
                        TriggerMaterialsRegeneration();
                    }
                }
            }
            ImGui.EndMenu();
        }

        ImGui.Separator();

        var recipeOptions = planningList.GetRecipeOptions(row.Recipe.RowId, row.IsOriginalRecipe);
        if (row.Recipe.CanQuickSynth)
        {
            using (ImRaii.Disabled(row.ForceQuickSynth))
            {
                if (ImGui.MenuItem("Quick Synthesis", "", row.EffectiveQuickSynth))
                {
                    _list.SetRecipeQuickSynth(row.Recipe.RowId, !recipeOptions.NQOnly, row.IsOriginalRecipe);
                    GatherBuddy.CraftingListManager.SaveList(_list);
                    _cachedQueueValid = false;
                    InvalidateMaterialCaches();
                    InvalidatePresentationCaches();
                    TriggerQueueRegeneration();
                    TriggerMaterialsRegeneration();
                }
            }
            if (ImGui.IsItemHovered(row.ForceQuickSynth ? ImGuiHoveredFlags.AllowWhenDisabled : ImGuiHoveredFlags.None))
                ImGui.SetTooltip(row.ForceQuickSynth
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

    private IReadOnlyList<RecipeDisplayRow> GetRecipeDisplayRows()
    {
        if (_cachedRecipeDisplayRowsValid && _cachedRecipeDisplayRows != null)
            return _cachedRecipeDisplayRows;

        try
        {
            _cachedRecipeDisplayRows = BuildRecipeDisplayRows();
            _cachedRecipeDisplayRowsValid = true;
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[CraftingListEditor] Failed to rebuild recipe display cache for list '{_list.Name}': {ex.Message}");
            _cachedRecipeDisplayRows = new List<RecipeDisplayRow>();
            _cachedRecipeDisplayRowsValid = true;
        }

        return _cachedRecipeDisplayRows;
    }

    private List<RecipeDisplayRow> BuildRecipeDisplayRows()
    {
        var rows = new List<RecipeDisplayRow>(_list.Recipes.Count);
        for (var i = 0; i < _list.Recipes.Count; i++)
        {
            var item = _list.Recipes[i];
            var recipe = RecipeManager.GetRecipe(item.RecipeId);
            if (recipe == null)
                continue;

            var itemName = recipe.Value.ItemResult.Value.Name.ExtractText();
            var jobName = GetCraftingJobName(recipe.Value.CraftType.RowId);
            var effectiveCraftSettings = GetEffectiveCraftSettings(item.RecipeId, true);
            var effectiveQuickSynth = IsEffectivelyQuickSynth(recipe.Value, item.RecipeId, true);
            var forcePreferNQNoQuickSynth = !recipe.Value.CanQuickSynth && _list.ShouldForcePreferNQ(true);
            var validation = WillUseQuickSynth(recipe.Value, item.RecipeId, true)
                ? null
                : MacroValidator.GetOrCompute(item.RecipeId,
                    ResolveEffectiveMacroId(effectiveCraftSettings, false),
                    effectiveCraftSettings,
                    _list.Consumables);
            rows.Add(new RecipeDisplayRow
            {
                ListIndex = i,
                Recipe = recipe.Value,
                ItemName = itemName,
                Label = $"{(effectiveQuickSynth ? "[QS] " : forcePreferNQNoQuickSynth ? "[NQ] " : string.Empty)}{(item.CraftSettings?.HasAnySettings() == true ? "[SET] " : string.Empty)}{(effectiveCraftSettings?.IngredientPreferences.Count > 0 ? "[HQ] " : string.Empty)}{(item.Options.Skipping ? "[SKIP] " : string.Empty)}{itemName} x{item.Quantity} ({jobName})##recipe_{i}",
                TextColor = item.Options.Skipping
                    ? new Vector4(0.7f, 0.7f, 0.7f, 1f)
                    : effectiveQuickSynth
                        ? new Vector4(0.3f, 0.9f, 0.9f, 1f)
                        : new Vector4(1f, 1f, 1f, 1f),
                Validation = validation,
            });
        }

        return rows;
    }

    private void DrawRecipeListRow(RecipeDisplayRow row, List<int> indicesToRemove)
    {
        if (row.ListIndex >= _list.Recipes.Count)
            return;

        var item = _list.Recipes[row.ListIndex];
        if (row.Validation != null)
            DrawValidationMarker(row.Validation);

        var isSelected = _selectedRecipeIndices.Contains(row.ListIndex);
        ImGui.PushStyleColor(ImGuiCol.Text, row.TextColor);
        var clicked = ImGui.Selectable(row.Label, isSelected);
        ImGui.PopStyleColor();

        if (clicked)
        {
            if (ImGui.GetIO().KeyShift && _lastClickedRecipeIndex >= 0)
            {
                if (!ImGui.GetIO().KeyCtrl)
                    _selectedRecipeIndices.Clear();
                var min = Math.Min(_lastClickedRecipeIndex, row.ListIndex);
                var max = Math.Max(_lastClickedRecipeIndex, row.ListIndex);
                for (var i = min; i <= max; i++)
                    _selectedRecipeIndices.Add(i);
            }
            else if (ImGui.GetIO().KeyCtrl)
            {
                if (!_selectedRecipeIndices.Remove(row.ListIndex))
                    _selectedRecipeIndices.Add(row.ListIndex);
                _lastClickedRecipeIndex = row.ListIndex;
            }
            else
            {
                _selectedRecipeIndices.Clear();
                _selectedRecipeIndices.Add(row.ListIndex);
                _lastClickedRecipeIndex = row.ListIndex;
            }
        }

        var isPopupOpen = GatherBuddy.ControllerSupport != null
            ? GatherBuddy.ControllerSupport.ContextMenu.BeginPopupContextItemWithGamepad($"context_{row.ListIndex}", Dalamud.GamepadState)
            : ImGui.BeginPopupContextItem($"context_{row.ListIndex}");

        if (isPopupOpen)
        {
            if (ImGui.MenuItem("Craft Settings..."))
                _craftSettingsPopup.OpenForListItem(item, _list, row.ItemName);

            var resultItemId = row.Recipe.ItemResult.RowId;
            var altRecipes = RecipeManager.GetRecipesForItem(resultItemId);
            if (altRecipes.Count > 1 && ImGui.BeginMenu("Change Job..."))
            {
                foreach (var alt in altRecipes)
                {
                    var altJob = GetCraftingJobName(alt.CraftType.RowId);
                    var isCurrent = alt.RowId == item.RecipeId;
                    if (ImGui.MenuItem(altJob, string.Empty, isCurrent) && !isCurrent)
                    {
                        item.RecipeId = alt.RowId;
                        GatherBuddy.CraftingListManager.SaveList(_list);
                        _cachedQueueValid = false;
                        InvalidateMaterialCaches();
                        InvalidatePresentationCaches();
                        TriggerQueueRegeneration();
                        TriggerMaterialsRegeneration();
                    }
                }
                ImGui.EndMenu();
            }

            ImGui.Separator();

            ImGui.Text("Quantity:");
            ImGui.SetNextItemWidth(100);
            if (_editingQuantityIndex != row.ListIndex)
            {
                _tempQuantityInput = item.Quantity;
                _editingQuantityIndex = row.ListIndex;
            }

            if (ImGui.InputInt($"##qty_{row.ListIndex}", ref _tempQuantityInput, 1))
            {
                if (_tempQuantityInput < 1)
                    _tempQuantityInput = 1;
            }

            if (ImGui.IsItemDeactivatedAfterEdit() && _tempQuantityInput != item.Quantity)
            {
                _list.UpdateRecipeQuantity(item.RecipeId, _tempQuantityInput);
                GatherBuddy.CraftingListManager.SaveList(_list);
                _cachedQueueValid = false;
                InvalidateMaterialCaches();
                InvalidatePresentationCaches();
                TriggerQueueRegeneration();
            }

            ImGui.Separator();

            if (ImGui.MenuItem(item.Options.Skipping ? "Enable" : "Skip"))
            {
                item.Options.Skipping = !item.Options.Skipping;
                GatherBuddy.CraftingListManager.SaveList(_list);
                _cachedQueueValid = false;
                InvalidateMaterialCaches();
                InvalidatePresentationCaches();
                TriggerQueueRegeneration();
            }

            if (_selectedRecipeIndices.Count > 1 && _selectedRecipeIndices.Contains(row.ListIndex))
            {
                if (ImGui.MenuItem($"Remove Selected ({_selectedRecipeIndices.Count})"))
                    indicesToRemove.AddRange(_selectedRecipeIndices);
            }
            else
            {
                if (ImGui.MenuItem("Remove"))
                    indicesToRemove.Add(row.ListIndex);
            }

            ImGui.EndPopup();
        }
        else if (_editingQuantityIndex == row.ListIndex)
        {
            _editingQuantityIndex = -1;
        }
    }

    private static void DrawValidationMarker(MacroValidationResult validation)
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

    private string ComputeListHash()
    {
        var activeExecutionPlan = GetActiveExecutionPlan();
        var planningList = activeExecutionPlan?.PlanningSnapshot ?? _list;
        var hashParts = new List<string>();
        hashParts.Add($"SkipIfEnough:{planningList.SkipIfEnough}");
        hashParts.Add($"SkipFinalIfEnough:{planningList.SkipFinalIfEnough}");
        hashParts.Add($"RetainerRestock:{planningList.RetainerRestock}");
        foreach (var item in planningList.Recipes)
        {
            hashParts.Add($"{item.RecipeId}:{item.Quantity}:{item.Options.Skipping}");
        }
        if (activeExecutionPlan != null)
            hashParts.Add($"ExecutionPlanVersion:{activeExecutionPlan.Version}");
        return string.Join("|", hashParts);
    }

    private void InvalidateMaterialCaches()
    {
        _cachedMaterialsValid = false;
        _cachedMaterials = null;
        _cachedMaterialsHash = string.Empty;
        _cachedPrecraftMaterials = null;
        _cachedPrecraftMaterialsHash = string.Empty;
        _cachedIngredientDemands = null;
        _cachedCraftMaterialDemands = null;
        _cachedDisplayMaterials = null;
        _cachedDisplayPrecraftMaterials = null;
        _cachedDisplayIngredientDemands = null;
        _cachedDisplayCraftMaterialDemands = null;
        _cachedDisplayMaterialsHash = string.Empty;
        Interlocked.Increment(ref _materialCacheVersion);
    }

    private void CacheMaterialPlan(CraftingListPlan plan, string hash)
    {
        _cachedMaterials = plan.Materials;
        _cachedPrecraftMaterials = BuildCraftPanelMaterials(plan);
        _cachedIngredientDemands = plan.IngredientDemands;
        _cachedCraftMaterialDemands = BuildCraftPanelDemands(plan, _cachedPrecraftMaterials);
        _cachedMaterialsHash = hash;
        _cachedPrecraftMaterialsHash = hash;
        _cachedMaterialsValid = true;
    }

    private void CacheDisplayMaterialPlan(CraftingListPlan plan, string hash)
    {
        _cachedDisplayMaterials = plan.Materials;
        _cachedDisplayPrecraftMaterials = BuildCraftPanelMaterials(plan, GetPlanningList().Recipes);
        _cachedDisplayIngredientDemands = plan.IngredientDemands;
        _cachedDisplayCraftMaterialDemands = BuildCraftPanelDemands(plan, _cachedDisplayPrecraftMaterials, GetPlanningList().Recipes);
        _cachedDisplayMaterialsHash = hash;
    }

    private CraftingListPlan CreateDisplayMaterialPlan()
    {
        var displayList = GetPlanningList().CreateRetainerPlanningSnapshot();
        var useRetainers = displayList.SkipIfEnough && displayList.RetainerRestock && AllaganTools.Enabled;
        return CraftingListPlanner.Build(displayList, new CraftingListPlannerOptions(
            UseRetainerCraftableAvailability: useRetainers,
            ConsumeIntermediateAvailability: true,
            ConsumeFinalAvailability: true));
    }

    private Dictionary<uint, int> BuildCraftPanelMaterials(CraftingListPlan plan, IEnumerable<CraftingListItem>? finalSourceRecipes = null)
    {
        var itemSheet = Dalamud.GameData.GetExcelSheet<Item>();
        var craftPanelMaterials = new Dictionary<uint, int>();

        foreach (var (itemId, quantity) in plan.Precrafts)
        {
            if (quantity <= 0)
                continue;

            if (itemSheet != null && itemSheet.TryGetRow(itemId, out var item) && IsEquippableCraftPanelItem(item))
                continue;

            craftPanelMaterials[itemId] = quantity;
        }

        foreach (var originalRecipe in finalSourceRecipes ?? plan.OriginalRecipes)
        {
            if (originalRecipe.Options.Skipping || originalRecipe.Quantity <= 0)
                continue;
            var recipe = RecipeManager.GetRecipe(originalRecipe.RecipeId);
            if (recipe == null)
                continue;

            var resultItem = recipe.Value.ItemResult.Value;
            if (IsEquippableCraftPanelItem(resultItem))
                continue;

            var resultItemId = resultItem.RowId;
            var finalItemCount = originalRecipe.Quantity * (int)recipe.Value.AmountResult;
            if (craftPanelMaterials.TryGetValue(resultItemId, out var existingCount))
                craftPanelMaterials[resultItemId] = existingCount + finalItemCount;
            else
                craftPanelMaterials[resultItemId] = finalItemCount;
        }

        return craftPanelMaterials;
    }

    private static bool IsEquippableCraftPanelItem(Item item)
        => item.RowId > 0 && item.EquipSlotCategory.RowId > 0;

    private static Dictionary<uint, IngredientQualityDemand> BuildCraftPanelDemands(CraftingListPlan plan, IReadOnlyDictionary<uint, int> craftPanelMaterials, IEnumerable<CraftingListItem>? finalSourceRecipes = null)
    {
        var demands = plan.IngredientDemands
            .Where(kvp => craftPanelMaterials.ContainsKey(kvp.Key))
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        foreach (var originalRecipe in finalSourceRecipes ?? plan.OriginalRecipes)
        {
            if (originalRecipe.Options.Skipping || originalRecipe.Quantity <= 0)
                continue;
            var recipe = RecipeManager.GetRecipe(originalRecipe.RecipeId);
            if (recipe == null)
                continue;

            var resultItemId = recipe.Value.ItemResult.RowId;
            if (!craftPanelMaterials.ContainsKey(resultItemId))
                continue;

            var finalItemCount = originalRecipe.Quantity * (int)recipe.Value.AmountResult;
            var finalDemand = IngredientQualityDemand.FromPreferNQ(finalItemCount);
            demands[resultItemId] = demands.TryGetValue(resultItemId, out var existing)
                ? existing.Add(finalDemand)
                : finalDemand;
        }

        return demands;
    }
    
    private void TriggerQueueRegeneration()
    {
        var currentHash = ComputeListHash();
        if (_cachedQueueValid && _cachedSortedQueue != null && currentHash == _cachedListHash)
        {
            return;
        }

        if (TryCacheActiveExecutionPlan(currentHash))
        {
            _queueCancellationSource?.Cancel();
            _queueCancellationSource?.Dispose();
            _queueCancellationSource = null;
            _queueGenerationTask = null;
            _isGeneratingQueue = false;
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
    
    private bool ShouldUseRetainerCraftablePlanning()
    {
        var planningList = GetPlanningList();
        return planningList.SkipIfEnough && planningList.RetainerRestock && AllaganTools.Enabled;
    }

    internal void TriggerMaterialsRegeneration()
    {
        ProcessPendingInventoryChanges();
        var currentHash = ComputeListHash();
        if (_cachedMaterialsValid && _cachedMaterials != null && currentHash == _cachedMaterialsHash)
        {
            return;
        }

        if (TryCacheActiveExecutionPlan(currentHash))
        {
            _materialsCancellationSource?.Cancel();
            _materialsCancellationSource?.Dispose();
            _materialsCancellationSource = null;
            _materialsGenerationTask = null;
            _isGeneratingMaterials = false;
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
                var plan = GetPlanningList().CreatePlan(ShouldUseRetainerCraftablePlanning());
                var displayPlan = CreateDisplayMaterialPlan();
                
                if (!token.IsCancellationRequested)
                {
                    CacheMaterialPlan(plan, hash);
                    CacheDisplayMaterialPlan(displayPlan, hash);
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
        ProcessPendingInventoryChanges();
        if (_cachedSortedQueue != null && _cachedQueueValid)
        {
            return _cachedSortedQueue;
        }
        return new List<CraftingListItem>();
    }
    
    private List<CraftingListItem> GenerateSortedQueueSync()
    {
        var activeExecutionPlan = GetActiveExecutionPlan();
        if (activeExecutionPlan != null)
            return BuildDisplayQueue(activeExecutionPlan.ResolvedPlan);

        var plan = GetPlanningList().CreatePlan(ShouldUseRetainerCraftablePlanning());
        return BuildDisplayQueue(plan);
    }

    private List<CraftingListItem> BuildDisplayQueue(CraftingListPlan plan)
        => CraftingListQueueBuilder.CreateGroupedQueue(plan);

    private RecipeCraftSettings? GetEffectiveCraftSettings(uint recipeId, bool isOriginalRecipe)
    {
        var planningList = GetPlanningList();
        var sourceSettings = isOriginalRecipe
            ? planningList.Recipes.FirstOrDefault(r => r.RecipeId == recipeId)?.CraftSettings
            : planningList.PrecraftCraftSettings.GetValueOrDefault(recipeId);
        return sourceSettings?.Clone();
    }

    private bool IsEffectivelyQuickSynth(Recipe recipe, uint recipeId, bool isOriginalRecipe)
    {
        var planningList = GetPlanningList();
        var recipeOptions = planningList.GetRecipeOptions(recipeId, isOriginalRecipe);
        return recipeOptions.NQOnly || planningList.ShouldForceQuickSynth(recipe, isOriginalRecipe);
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
        ProcessPendingInventoryChanges();
        var currentHash = ComputeListHash();
        if (TryCacheActiveExecutionPlan(currentHash))
            return _cachedMaterials!;
        if (_cachedMaterialsValid && _cachedMaterials != null && currentHash == _cachedMaterialsHash)
        {
            return _cachedMaterials;
        }
        CacheMaterialPlan(GetPlanningList().CreatePlan(ShouldUseRetainerCraftablePlanning()), currentHash);

        return _cachedMaterials!;
    }

    internal Dictionary<uint, int> GetCachedPrecraftMaterials()
    {
        ProcessPendingInventoryChanges();
        var currentHash = ComputeListHash();
        if (TryCacheActiveExecutionPlan(currentHash))
            return _cachedPrecraftMaterials!;
        if (_cachedPrecraftMaterials != null && currentHash == _cachedPrecraftMaterialsHash)
            return _cachedPrecraftMaterials;
        CacheMaterialPlan(GetPlanningList().CreatePlan(ShouldUseRetainerCraftablePlanning()), currentHash);
        _cachedPrecraftMaterialsHash = currentHash;
        return _cachedPrecraftMaterials!;
    }

    internal Dictionary<uint, int> GetDisplayMaterials()
    {
        ProcessPendingInventoryChanges();
        var currentHash = ComputeListHash();
        if (TryCacheActiveExecutionPlan(currentHash))
            return _cachedDisplayMaterials!;
        if (_cachedDisplayMaterials != null && currentHash == _cachedDisplayMaterialsHash)
            return _cachedDisplayMaterials;
        CacheDisplayMaterialPlan(CreateDisplayMaterialPlan(), currentHash);
        return _cachedDisplayMaterials!;
    }

    internal Dictionary<uint, int> GetDisplayPrecraftMaterials()
    {
        ProcessPendingInventoryChanges();
        var currentHash = ComputeListHash();
        if (TryCacheActiveExecutionPlan(currentHash))
            return _cachedDisplayPrecraftMaterials!;
        if (_cachedDisplayPrecraftMaterials != null && currentHash == _cachedDisplayMaterialsHash)
            return _cachedDisplayPrecraftMaterials;
        CacheDisplayMaterialPlan(CreateDisplayMaterialPlan(), currentHash);
        return _cachedDisplayPrecraftMaterials!;
    }

    internal IReadOnlyDictionary<uint, IngredientQualityDemand> GetCachedIngredientDemands()
    {
        ProcessPendingInventoryChanges();
        var currentHash = ComputeListHash();
        if (TryCacheActiveExecutionPlan(currentHash))
            return _cachedIngredientDemands!;
        if (_cachedIngredientDemands != null && currentHash == _cachedMaterialsHash)
            return _cachedIngredientDemands;
        CacheMaterialPlan(GetPlanningList().CreatePlan(ShouldUseRetainerCraftablePlanning()), currentHash);
        return _cachedIngredientDemands!;
    }

    internal IReadOnlyDictionary<uint, IngredientQualityDemand> GetDisplayIngredientDemands()
    {
        ProcessPendingInventoryChanges();
        var currentHash = ComputeListHash();
        if (TryCacheActiveExecutionPlan(currentHash))
            return _cachedDisplayIngredientDemands!;
        if (_cachedDisplayIngredientDemands != null && currentHash == _cachedDisplayMaterialsHash)
            return _cachedDisplayIngredientDemands;
        CacheDisplayMaterialPlan(CreateDisplayMaterialPlan(), currentHash);
        return _cachedDisplayIngredientDemands!;
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
    
    internal int GetInventoryCount(uint itemId)
    {
        var (nqCount, hqCount) = GetInventorySplitCounts(itemId);
        return nqCount + hqCount;
    }

    internal unsafe (int NQ, int HQ) GetInventorySplitCounts(uint itemId)
    {
        var now = DateTime.Now;
        
        if (_inventoryRefreshTimes.TryGetValue(itemId, out var lastRefresh))
        {
            if ((now - lastRefresh).TotalSeconds < InventoryRefreshIntervalSeconds
             && _cachedInventorySplitCounts.TryGetValue(itemId, out var cachedCounts))
                return cachedCounts;
        }
        
        try
        {
            var inventory = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
            if (inventory == null)
                return (0, 0);
            
            var counts = (
                (int)inventory->GetInventoryItemCount(itemId, false, false, false),
                (int)inventory->GetInventoryItemCount(itemId, true, false, false));
            _cachedInventorySplitCounts[itemId] = counts;
            _inventoryRefreshTimes[itemId] = now;
            return counts;
        }
        catch
        {
            return (0, 0);
        }
    }

    internal int GetRetainerCount(uint itemId)
        => RetainerItemQuery.GetTotalCount(itemId);
    internal void InvalidateRetainerSnapshot()
    {
        _cachedRetainerSnapshot = RetainerItemSnapshot.Empty;
        _cachedRetainerSnapshotItemIds = [];
        _cachedRetainerSnapshotAt = DateTime.MinValue;
        Interlocked.Increment(ref _materialCacheVersion);
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

    internal int GetQualityAwareAvailableCount(uint itemId, int retNQ, int retHQ, bool countRetainersTowardNeed)
        => GetQualityAwareAvailableCount(itemId, GetIngredientDemand(itemId), retNQ, retHQ, countRetainersTowardNeed);

    internal int GetCraftMaterialAvailableCount(uint itemId, int retNQ, int retHQ, bool countRetainersTowardNeed)
    {
        var demand = _cachedCraftMaterialDemands != null && _cachedCraftMaterialDemands.TryGetValue(itemId, out var craftDemand)
            ? craftDemand
            : GetIngredientDemand(itemId);
        return GetQualityAwareAvailableCount(itemId, demand, retNQ, retHQ, countRetainersTowardNeed);
    }

    internal int GetDisplayMaterialAvailableCount(uint itemId, int retNQ, int retHQ, bool countRetainersTowardNeed)
        => GetQualityAwareAvailableCount(itemId, GetDisplayIngredientDemand(itemId), retNQ, retHQ, countRetainersTowardNeed);

    internal int GetDisplayCraftMaterialAvailableCount(uint itemId, int retNQ, int retHQ, bool countRetainersTowardNeed)
    {
        var demand = _cachedDisplayCraftMaterialDemands != null && _cachedDisplayCraftMaterialDemands.TryGetValue(itemId, out var craftDemand)
            ? craftDemand
            : GetDisplayIngredientDemand(itemId);
        return GetQualityAwareAvailableCount(itemId, demand, retNQ, retHQ, countRetainersTowardNeed);
    }

    private int GetQualityAwareAvailableCount(uint itemId, IngredientQualityDemand demand, int retNQ, int retHQ, bool countRetainersTowardNeed)
    {
        var (inventoryNQ, inventoryHQ) = GetInventorySplitCounts(itemId);
        var availableNQ = inventoryNQ + (countRetainersTowardNeed ? retNQ : 0);
        var availableHQ = inventoryHQ + (countRetainersTowardNeed ? retHQ : 0);
        if (demand.Total <= 0)
            return availableNQ + availableHQ;

        var remaining = demand.ConsumeSplit(availableNQ, availableHQ, out _, out _);
        return demand.Total - remaining.Total;
    }

    private IngredientQualityDemand GetIngredientDemand(uint itemId)
    {
        var ingredientDemands = GetCachedIngredientDemands();
        return ingredientDemands.TryGetValue(itemId, out var demand)
            ? demand
            : default;
    }

    private IngredientQualityDemand GetDisplayIngredientDemand(uint itemId)
    {
        var ingredientDemands = GetDisplayIngredientDemands();
        return ingredientDemands.TryGetValue(itemId, out var demand)
            ? demand
            : default;
    }
    

    private bool WillBeSkippedDueToInventory(Recipe recipe)
    {
        var demand = GetIngredientDemand(recipe.ItemResult.RowId);
        if (demand.Total <= 0)
            return false;

        var (nqCount, hqCount) = GetInventorySplitCounts(recipe.ItemResult.RowId);
        return demand.ConsumeSplit(nqCount, hqCount, out _, out _).Total == 0;
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
        var planningList = GetPlanningList();
        var isSpecific = settings != null
            && (settings.MacroMode == MacroOverrideMode.Specific
                || (settings.MacroMode == MacroOverrideMode.Inherit
                    && (!string.IsNullOrEmpty(settings.SelectedMacroId) || settings.SolverOverride != SolverOverrideMode.Default)));
        if (isSpecific)
            return settings?.SolverOverride == SolverOverrideMode.Default ? settings?.SelectedMacroId : null;
        var defaultSolverOverride = isPrecraft ? planningList.DefaultPrecraftSolverOverride : planningList.DefaultFinalSolverOverride;
        if (defaultSolverOverride != SolverOverrideMode.Default)
            return null;
        return isPrecraft ? planningList.DefaultPrecraftMacroId : planningList.DefaultFinalMacroId;
    }

    private (int hardFails, int warnings) CountValidationIssues()
    {
        ProcessPendingInventoryChanges();

        var currentHash = ComputeListHash();
        if (_cachedValidationIssueCountsValid && _cachedValidationIssueCountsHash == currentHash)
            return _cachedValidationIssueCounts;

        var hardFails = 0;
        var warnings  = 0;

        foreach (var row in GetRecipeDisplayRows())
            AccumulateValidationIssue(row.Validation, ref hardFails, ref warnings);

        if (_cachedQueueValid && _cachedSortedQueue != null)
        {
            EnsureQueueDisplayRows(GetPlanningList(), GetActiveExecutionPlan());
            if (_cachedQueueDisplayRows != null)
            {
                foreach (var row in _cachedQueueDisplayRows)
                {
                    if (row.IsOriginalRecipe)
                        continue;

                    AccumulateValidationIssue(row.Validation, ref hardFails, ref warnings);
                }
            }

            _cachedValidationIssueCounts = (hardFails, warnings);
            _cachedValidationIssueCountsHash = currentHash;
            _cachedValidationIssueCountsValid = true;
        }

        return (hardFails, warnings);
    }

    private static void AccumulateValidationIssue(MacroValidationResult? validation, ref int hardFails, ref int warnings)
    {
        if (validation == null || validation.IsValid || validation.Failure == MacroValidationFailure.NoStats)
            return;

        if (validation.Failure is MacroValidationFailure.CPExhausted or MacroValidationFailure.DurabilityFailed)
            hardFails++;
        else
            warnings++;
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
