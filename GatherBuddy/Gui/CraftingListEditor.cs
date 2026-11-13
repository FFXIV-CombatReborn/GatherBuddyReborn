using Dalamud.Bindings.ImGui;
using Dalamud.Game.Inventory;
using Dalamud.Game.Inventory.InventoryEventArgTypes;
using Dalamud.Interface;
using GatherBuddy.Crafting;
using GatherBuddy.Plugin;
using GatherBuddy.Vulcan;
using Lumina.Excel.Sheets;
using RebornMaterial;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using ImRaii = Dalamud.Interface.Utility.Raii.ImRaii;

namespace GatherBuddy.Gui;

public class CraftingListEditor
{
    private sealed class QueueCacheSnapshot
    {
        public string Hash { get; init; } = string.Empty;
        public List<CraftingListItem> SortedQueue { get; init; } = [];
    }

    private sealed class MaterialCacheSnapshot
    {
        public string Hash { get; init; } = string.Empty;
        public Dictionary<uint, int> Materials { get; init; } = [];
        public Dictionary<uint, int> PrecraftMaterials { get; init; } = [];
        public Dictionary<uint, IngredientQualityDemand> IngredientDemands { get; init; } = [];
        public Dictionary<uint, IngredientQualityDemand> CraftMaterialDemands { get; init; } = [];
        public Dictionary<uint, int> DisplayMaterials { get; init; } = [];
        public Dictionary<uint, int> DisplayPrecraftMaterials { get; init; } = [];
        public Dictionary<uint, IngredientQualityDemand> DisplayIngredientDemands { get; init; } = [];
        public Dictionary<uint, IngredientQualityDemand> DisplayCraftMaterialDemands { get; init; } = [];
    }
    private readonly CraftingListDefinition _list;
    private int _searchQuantity = 1;
    private Recipe? _selectedRecipe = null;
    private readonly Dictionary<uint, string> _recipeLabels = [];
    private readonly Dictionary<uint, string> _recipeNames = [];
    private readonly Dictionary<uint, string> _recipeDetails = [];
    private bool _recipesLoaded;
    private string? _appliedRecipeFilter;
    private float _queueIndexWidth;
    private readonly List<Recipe> _allRecipes = [];
    private readonly List<Recipe> _keywordFilteredRecipes = [];
    private string _lastComboFilter = string.Empty;

    private QueueCacheSnapshot? _queueCache = null;
    private int _queueGenerationVersion = 0;
    private int _selectedQueueIndex = -1;
    private bool _showPrecrafts = true;

    private MaterialCacheSnapshot? _materialCache = null;

    private Task? _queueGenerationTask = null;
    private CancellationTokenSource? _queueCancellationSource = null;
    private volatile bool _isGeneratingQueue = false;

    private Task? _materialsGenerationTask = null;
    private CancellationTokenSource? _materialsCancellationSource = null;
    private volatile bool _isGeneratingMaterials = false;
    private int _materialGenerationVersion = 0;

    private readonly Dictionary<uint, (int NQ, int HQ)> _cachedInventorySplitCounts = [];
    private readonly Dictionary<uint, DateTime> _inventoryRefreshTimes = [];
    private RetainerItemSnapshot _cachedRetainerSnapshot = RetainerItemSnapshot.Empty;
    private uint[] _cachedRetainerSnapshotItemIds = [];
    private DateTime _cachedRetainerSnapshotAt = DateTime.MinValue;
    private readonly HashSet<uint> _watchedInventoryItemIds = [];
    private readonly HashSet<uint> _watchedOriginalResultItemIds = [];
    private readonly HashSet<uint> _watchedPrecraftResultItemIds = [];
    private readonly Lock _inventoryChangeLock = new();
    private DateTime _lastGraphAffectingInventoryChange = DateTime.MinValue;
    private string _watchedInventoryHash = string.Empty;
    private bool _pendingQueueRefreshFromInventory;
    private bool _pendingMaterialsRefreshFromInventory;
    private const double InventoryRefreshIntervalSeconds = 0.5;
    private const double RetainerSnapshotRetryIntervalSeconds = 1.0;
    private const double InventoryChangeDebounceSeconds = 0.2;

    private readonly RecipeCraftSettingsPopup _craftSettingsPopup = new();
    private readonly CraftingListConsumablesPopup _consumablesPopup = new();

    private readonly HashSet<int> _selectedRecipeIndices = [];
    private int _lastClickedRecipeIndex = -1;
    private int _lastRaphaelActiveSolves = -1;
    private int _lastRaphaelPendingSolves = -1;
    private int _lastRaphaelCachedSolutions = -1;
    private sealed class QueueDisplayRow
    {
        public int QueueIndex { get; init; }
        public int Quantity { get; init; }
        public bool IsOriginalRecipe { get; init; }
        public Recipe Recipe { get; init; }
        public string ItemName { get; init; } = string.Empty;
        public string IndexText { get; init; } = string.Empty;
        public string QuantityText { get; init; } = string.Empty;
        public bool EffectiveQuickSynth { get; init; }
        public bool ForceQuickSynth { get; init; }
        public bool ForcePreferNQ { get; init; }
        public MacroValidationResult? Validation { get; init; }
        public RaphaelAssessment? RaphaelAssessment { get; init; }
    }

    private sealed class RecipeDisplayRow
    {
        public int ListIndex { get; init; }
        public Recipe Recipe { get; init; }
        public string ItemName { get; init; } = string.Empty;
        public bool EffectiveQuickSynth { get; init; }
        public bool ForcePreferNQ { get; init; }
        public bool HasCustomSettings { get; init; }
        public bool HasIngredientPreferences { get; init; }
        public MacroValidationResult? Validation { get; init; }
        public RaphaelAssessment? RaphaelAssessment { get; init; }
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

    private string _editingName = string.Empty;
    private string _editingDescription = string.Empty;
    private bool _nameConflict = false;
    private bool _editingDescActive = false;
    private bool _focusDescNext = false;
    private long _materialCacheVersion;

    internal bool HasCachedMaterials => GetMaterialCache() != null;
    internal bool HasCachedDisplayMaterials => GetMaterialCache() != null;
    internal bool IsGeneratingMaterials => _isGeneratingMaterials;
    internal string ListName => GetPlanningList().Name;
    internal bool SkipIfEnoughEnabled => GetPlanningList().SkipIfEnough;
    internal bool RetainerRestockEnabled => GetPlanningList().RetainerRestock;
    internal CraftingListDefinition PlanningList => GetPlanningList();
    internal long MaterialCacheVersion => Interlocked.Read(ref _materialCacheVersion);

    public Action<CraftingListDefinition>? OnStartCrafting { get; set; }

    public CraftingListEditor(CraftingListDefinition list)
    {
        _list = list;
        _editingName = list.Name;
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

    private CraftingListDefinition CreatePlanningSnapshot()
        => GetPlanningList().CreateRetainerPlanningSnapshot();

    private QueueCacheSnapshot? GetQueueCache()
        => Volatile.Read(ref _queueCache);

    private MaterialCacheSnapshot? GetMaterialCache()
        => Volatile.Read(ref _materialCache);

    private void PublishQueueCache(QueueCacheSnapshot snapshot)
        => Volatile.Write(ref _queueCache, snapshot);

    private bool TryPublishQueueCache(QueueCacheSnapshot snapshot, int generation, CancellationToken token)
    {
        if (token.IsCancellationRequested || generation != Volatile.Read(ref _queueGenerationVersion))
        {
            GatherBuddy.Log.Debug($"[CraftingListEditor] Discarded stale queue cache for list '{_list.Name}'");
            return false;
        }

        PublishQueueCache(snapshot);
        return true;
    }

    private void InvalidateQueueCache()
    {
        Volatile.Write(ref _queueCache, null);
        Interlocked.Increment(ref _queueGenerationVersion);
    }

    private void PublishMaterialCache(MaterialCacheSnapshot snapshot)
        => Volatile.Write(ref _materialCache, snapshot);

    private bool TryPublishMaterialCache(MaterialCacheSnapshot snapshot, int generation, CancellationToken token)
    {
        if (token.IsCancellationRequested || generation != Volatile.Read(ref _materialGenerationVersion))
        {
            GatherBuddy.Log.Debug($"[CraftingListEditor] Discarded stale material cache for list '{_list.Name}'");
            return false;
        }

        PublishMaterialCache(snapshot);
        return true;
    }

    private bool TryCacheActiveExecutionPlan(string hash)
    {
        var activeExecutionPlan = GetActiveExecutionPlan();
        if (activeExecutionPlan == null)
        {
            return false;
        }

        var queueCache = GetQueueCache();
        if (queueCache == null || queueCache.Hash != hash)
        {
            PublishQueueCache(BuildQueueCacheSnapshot(activeExecutionPlan.ResolvedPlan, hash));
        }

        var materialCache = GetMaterialCache();
        if (materialCache == null || materialCache.Hash != hash)
        {
            PublishMaterialCache(BuildMaterialCacheSnapshot(activeExecutionPlan.ResolvedPlan, activeExecutionPlan.PlanningSnapshot, hash));
        }

        return true;
    }

    public void Dispose()
    {
        _craftSettingsPopup.Close();
        _consumablesPopup.Close();
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
        InvalidateQueueCache();
        InvalidateMaterialCaches();
        InvalidatePresentationCaches();
        TriggerQueueRegeneration();
        TriggerMaterialsRegeneration();
        if (!_editingDescActive)
        {
            _editingDescription = _list.Description;
        }
    }

    private void HandleEditorSettingsSaved()
    {
        GatherBuddy.Log.Debug($"[CraftingListEditor] Refreshing presentation caches after settings change for list '{_list.Name}'");
        InvalidatePresentationCaches();
        InvalidateQueueCache();
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
        RefreshRaphaelAssessmentCaches();
        var avail = ImGui.GetContentRegionAvail();
        var leftPaneWidth = MathF.Floor(avail.X * 0.4f);

        using (var left = M3Panel.Begin("LeftPane", new Vector2(leftPaneWidth, avail.Y), false, VulcanUiStyle.PanelBackground))
        {
            if (left)
            {
                DrawQueuePane();
            }
        }

        ImGui.SameLine();
        using var right = ImRaii.Child("RightPane", new Vector2(0f, avail.Y), false);
        if (right)
        {
            DrawDetailsPane();
        }
    }

    private void RefreshRaphaelAssessmentCaches()
    {
        var activeSolves = GatherBuddy.RaphaelSolveCoordinator.ActiveSolves;
        var pendingSolves = GatherBuddy.RaphaelSolveCoordinator.PendingSolves;
        var cachedSolutions = GatherBuddy.RaphaelSolveCoordinator.CachedSolutionCount;
        if (activeSolves == _lastRaphaelActiveSolves
         && pendingSolves == _lastRaphaelPendingSolves
         && cachedSolutions == _lastRaphaelCachedSolutions)
        {
            return;
        }

        _lastRaphaelActiveSolves = activeSolves;
        _lastRaphaelPendingSolves = pendingSolves;
        _lastRaphaelCachedSolutions = cachedSolutions;
        InvalidatePresentationCaches();
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
                {
                    continue;
                }

                changedItemIds.Add(itemId);

                if (planningList.SkipIfEnough && _watchedPrecraftResultItemIds.Contains(itemId)
                 || planningList.SkipIfEnough && planningList.SkipFinalIfEnough && _watchedOriginalResultItemIds.Contains(itemId))
                {
                    graphAffected = true;
                }
            }
        }

        if (changedItemIds.Count == 0)
        {
            return;
        }

        foreach (var itemId in changedItemIds)
        {
            _cachedInventorySplitCounts.Remove(itemId);
            _inventoryRefreshTimes.Remove(itemId);
        }

        Interlocked.Increment(ref _materialCacheVersion);

        if (!graphAffected)
        {
            return;
        }

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
                        {
                            yield return sourceItemId;
                        }
                    }

                    if (IsTrackedInventoryContainer(complexEvent.TargetInventory))
                    {
                        var targetItemId = complexEvent.TargetEvent.Item.BaseItemId;
                        if (targetItemId > 0)
                        {
                            yield return targetItemId;
                        }
                    }

                    yield break;
                }
            case InventoryItemAddedArgs addedEvent when IsTrackedInventoryContainer(addedEvent.Inventory):
                {
                    var itemId = addedEvent.Item.BaseItemId;
                    if (itemId > 0)
                    {
                        yield return itemId;
                    }

                    yield break;
                }
            case InventoryItemRemovedArgs removedEvent when IsTrackedInventoryContainer(removedEvent.Inventory):
                {
                    var itemId = removedEvent.Item.BaseItemId;
                    if (itemId > 0)
                    {
                        yield return itemId;
                    }

                    yield break;
                }
            case InventoryItemChangedArgs changedEvent when IsTrackedInventoryContainer(changedEvent.Inventory):
                {
                    var oldItemId = changedEvent.OldItemState.BaseItemId;
                    if (oldItemId > 0)
                    {
                        yield return oldItemId;
                    }

                    var itemId = changedEvent.Item.BaseItemId;
                    if (itemId > 0 && itemId != oldItemId)
                    {
                        yield return itemId;
                    }

                    yield break;
                }
            default:
                {
                    if (!IsTrackedInventoryContainer(inventoryEvent.Item.ContainerType))
                    {
                        yield break;
                    }

                    var itemId = inventoryEvent.Item.BaseItemId;
                    if (itemId > 0)
                    {
                        yield return itemId;
                    }

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
            {
                return;
            }

            if ((DateTime.Now - _lastGraphAffectingInventoryChange).TotalSeconds < InventoryChangeDebounceSeconds)
            {
                return;
            }

            refreshQueue = _pendingQueueRefreshFromInventory;
            refreshMaterials = _pendingMaterialsRefreshFromInventory;
            _pendingQueueRefreshFromInventory = false;
            _pendingMaterialsRefreshFromInventory = false;
        }

        if (refreshQueue)
        {
            InvalidateQueueCache();
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
        {
            return;
        }

        _watchedInventoryItemIds.Clear();
        _watchedOriginalResultItemIds.Clear();
        _watchedPrecraftResultItemIds.Clear();

        var visitedRecipes = new HashSet<uint>();
        foreach (var item in GetPlanningList().Recipes)
        {
            if (item.Options.Skipping || item.Quantity <= 0)
            {
                continue;
            }

            var recipe = RecipeManager.GetRecipe(item.RecipeId);
            if (recipe == null)
            {
                continue;
            }

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
            {
                _watchedOriginalResultItemIds.Add(resultItemId);
            }
            else
            {
                _watchedPrecraftResultItemIds.Add(resultItemId);
            }
        }

        if (!visitedRecipes.Add(recipe.RowId))
        {
            return;
        }

        foreach (var (itemId, _) in RecipeManager.GetIngredients(recipe))
        {
            if (itemId > 0)
            {
                _watchedInventoryItemIds.Add(itemId);
            }

            var subRecipe = RecipeManager.GetRecipeForItem(itemId);
            if (subRecipe.HasValue)
            {
                CollectWatchedInventoryItems(subRecipe.Value, false, visitedRecipes);
            }
        }
    }

    private static bool IsTrackedInventoryContainer(GameInventoryType inventoryType)
        => inventoryType is GameInventoryType.Inventory1
            or GameInventoryType.Inventory2
            or GameInventoryType.Inventory3
            or GameInventoryType.Inventory4
            or GameInventoryType.Crystals;

    private const string ConfirmFailedMacrosDialogId = "Start crafting anyway###gbr_confirm_failed_macros";
    private const string RecipePickerPopupId = "##recipe_picker_menu";
    private const string ArtisanLoadedTooltip = "Artisan plugin is loaded. Please unload Artisan to use Vulcan's crafting system.";
    private static readonly M3PluginRequirement[] AllaganToolsPlugin = [new("InventoryTools", "Allagan Tools")];
    private static readonly Vector4 HueQuickSynth = new(0.30f, 0.90f, 0.90f, 1f);

    private static float QueueRowHeight
        => MathF.Max(ImGui.GetFrameHeight(), M3.FitText(30f, 4f));

    private void DrawQueuePane()
    {
        var planningList = GetPlanningList();
        var activeExecutionPlan = GetActiveExecutionPlan();
        var hasRecipes = planningList.Recipes.Count > 0;
        DrawQueueHeader(hasRecipes);
        if (!hasRecipes)
        {
            M3Widgets.EmptyState("##queue_empty", "Nothing to craft yet", "Add recipes on the right to build the queue.", FontAwesomeIcon.Hammer);
            return;
        }

        var spacing = ImGui.GetStyle().ItemSpacing.Y;
        var footerHeight = M3Widgets.ButtonHeight * 2f + 8f * M3.Scale + spacing * 4f;
        var queueHeight = MathF.Max(ImGui.GetContentRegionAvail().Y - footerHeight, ImGui.GetTextLineHeightWithSpacing() * 3f);
        using (var queue = ImRaii.Child("QueueList", new Vector2(-1f, queueHeight), false))
        {
            if (queue)
            {
                DrawQueueRows(planningList, activeExecutionPlan);
            }
        }

        DrawQueueFooter();
    }

    private void DrawQueueHeader(bool hasRecipes)
    {
        const string title = "Craft queue";
        const string precraftsLabel = "Precrafts";
        var start = ImGui.GetCursorPos();
        var width = ImGui.GetContentRegionAvail().X;
        float titleHeight;
        using (ImRaii.PushFont(M3.TitleMedium))
        {
            titleHeight = ImGui.CalcTextSize(title).Y;
        }

        var rowHeight = hasRecipes ? MathF.Max(titleHeight, M3Widgets.ChipHeight) : titleHeight;
        ImGui.SetCursorPos(new Vector2(start.X, start.Y + (rowHeight - titleHeight) * 0.5f));
        using (ImRaii.PushFont(M3.TitleMedium))
        {
            ImGui.TextUnformatted(title);
        }

        if (hasRecipes)
        {
            var chipWidth = M3Widgets.ChipWidth(precraftsLabel, FontAwesomeIcon.LayerGroup);
            ImGui.SetCursorPos(new Vector2(start.X + MathF.Max(0f, width - chipWidth), start.Y + (rowHeight - M3Widgets.ChipHeight) * 0.5f));
            if (M3Widgets.Chip("##queue_show_precrafts", precraftsLabel, _showPrecrafts, FontAwesomeIcon.LayerGroup,
                    "Show the precrafts that get made before the final items"))
            {
                _showPrecrafts = !_showPrecrafts;
            }
        }

        ImGui.SetCursorPos(new Vector2(start.X, start.Y + rowHeight));
        ImGui.Dummy(new Vector2(0f, M3.Space1));
    }

    private void DrawQueueRows(CraftingListDefinition planningList, CraftingExecutionPlan? activeExecutionPlan)
    {
        if (_isGeneratingQueue)
        {
            M3Widgets.LinearProgressIndeterminate(new Vector2(0f, 4f * M3.Scale));
            M3Text.Draw("Working out the craft queue...", M3TextStyle.Muted);
            return;
        }

        var displayRows = GetDisplayQueueRows(planningList, activeExecutionPlan);
        if (displayRows.Count == 0)
        {
            M3Text.Draw("The queue is empty.", M3TextStyle.Muted);
            return;
        }

        _queueIndexWidth = ImGui.CalcTextSize($"{displayRows.Count}.").X;
        var rowHeight = QueueRowHeight;
        var clipper = ImGui.ImGuiListClipper();
        clipper.Begin(displayRows.Count);
        while (clipper.Step())
        {
            for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                DrawQueueRow(displayRows[i], planningList, rowHeight);
            }
        }

        clipper.End();
        clipper.Destroy();
    }

    private void DrawQueueFooter()
    {
        var width = ImGui.GetContentRegionAvail().X;
        M3Widgets.Divider(4f);
        if (IPCSubscriber.IsReady("Artisan"))
        {
            M3Widgets.Button("##queue_start", "Artisan detected", M3ButtonStyle.Filled, FontAwesomeIcon.Ban, width, false, ArtisanLoadedTooltip);
        }
        else
        {
            var (hardFails, warnings) = CountValidationIssues();
            var style = hardFails > 0 ? M3ButtonStyle.Danger : M3ButtonStyle.Filled;
            Vector4? accent = hardFails == 0 && warnings > 0 ? M3.Scheme.Warning : null;
            var tooltip = hardFails > 0
                ? $"{hardFails} macro(s) will fail their craft. Click to confirm and start anyway."
                : warnings > 0
                    ? $"{warnings} macro(s) have warnings."
                    : "Gather anything that's missing, then craft the queue.";
            if (M3Widgets.Button("##queue_start", "Gather & craft", style, FontAwesomeIcon.Play, width, tooltip: tooltip, accent: accent))
            {
                if (hardFails > 0)
                {
                    M3Dialog.Open(ConfirmFailedMacrosDialogId);
                }
                else
                {
                    OnStartCrafting?.Invoke(_list);
                }
            }

            DrawConfirmFailedMacrosDialog(hardFails);
        }

        var gap = M3.Space1;
        var third = MathF.Floor((width - gap * 2f) / 3f);
        if (ToolButton("##queue_gather_list", "Gather list", FontAwesomeIcon.Leaf, third, M3ButtonStyle.Outlined,
                "Make a gather list from this list's materials"))
        {
            var materials = new Dictionary<uint, int>(GetCachedMaterials());
            var gatherListName = $"{_list.Name}...Auto-Generated";
            CraftingGatherBridge.CreatePersistentGatherList(gatherListName, materials);
            M3Snackbar.Show($"Made the gather list '{gatherListName}'.");
        }

        ImGui.SameLine(0, gap);
        var materialsWindow = GatherBuddy.CraftingMaterialsWindow;
        var materialsShown = materialsWindow is { IsOpen: true, IsMinimized: false };
        if (ToolButton("##queue_materials", "Materials", FontAwesomeIcon.Boxes, third,
                materialsShown ? M3ButtonStyle.Tonal : M3ButtonStyle.Outlined,
                materialsShown ? "Hide the materials window" : "Show what this list needs")
         && materialsWindow != null)
        {
            if (materialsShown)
            {
                materialsWindow.IsOpen = false;
            }
            else
            {
                materialsWindow.SetEditor(this);
                materialsWindow.OpenAndRestore();
            }
        }

        ImGui.SameLine(0, gap);
        var treeWindow = GatherBuddy.CraftingTreeWindow;
        var treeShown = treeWindow is { IsOpen: true, IsMinimized: false };
        if (ToolButton("##queue_tree", "Tree", FontAwesomeIcon.Sitemap, MathF.Max(1f, width - third * 2f - gap * 2f),
                treeShown ? M3ButtonStyle.Tonal : M3ButtonStyle.Outlined,
                treeShown ? "Hide the crafting tree" : "Show how the recipes break down")
         && treeWindow != null)
        {
            if (treeShown)
            {
                treeWindow.IsOpen = false;
            }
            else
            {
                treeWindow.SetEditor(this);
                treeWindow.OpenAndRestore();
            }
        }
    }

    private static bool ToolButton(string id, string label, FontAwesomeIcon icon, float width, M3ButtonStyle style, string tooltip)
    {
        var fits = M3Widgets.ButtonWidth(icon, label) <= width;
        return M3Widgets.Button(id, fits ? label : string.Empty, style, icon, width, tooltip: fits ? tooltip : $"{label}: {tooltip}");
    }

    private void DrawConfirmFailedMacrosDialog(int hardFails)
    {
        if (!ImGui.IsPopupOpen(ConfirmFailedMacrosDialogId))
        {
            return;
        }

        using var dialog = M3Dialog.Begin(ConfirmFailedMacrosDialogId, "Start anyway?",
            $"{hardFails} macro(s) are predicted to fail their craft, so those items may not be finished.", FontAwesomeIcon.ExclamationTriangle, 380f);
        if (!dialog.IsOpen)
        {
            return;
        }

        if (M3Dialog.Actions(
            [
                new M3DialogAction("Cancel"),
                new M3DialogAction("Start anyway", M3ButtonStyle.Danger, FontAwesomeIcon.Play),
            ]) == 1)
        {
            OnStartCrafting?.Invoke(_list);
        }
    }

    private void DrawDetailsPane()
    {
        DrawListInfoCard();
        DrawQueueOptionsCard();
        DrawConsumablesCard();
        DrawAddRecipeCard();
        DrawRecipeListCard();
    }

    private static float CardContentWidth()
        => MathF.Max(1f, ImGui.GetContentRegionAvail().X - M3Card.RightInset);

    private void DrawListInfoCard()
    {
        using var card = M3Card.Begin("##list_info_card", "List", FontAwesomeIcon.ClipboardList, M3.AccentFor(0));
        var width = CardContentWidth();
        if (M3TextField.Draw("##listName", "List name", ref _editingName, width, 128))
        {
            _nameConflict = false;
        }

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            var trimmed = _editingName.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                _editingName = _list.Name;
            }
            else if (GatherBuddy.CraftingListManager.IsNameUnique(trimmed, _list.ID))
            {
                _list.Name = trimmed;
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
        {
            M3Text.Draw("A list with that name already exists.", M3TextStyle.Error);
        }

        ImGui.Dummy(new Vector2(0f, M3.Space1));
        M3Text.Draw("Notes", M3TextStyle.Label);
        DrawNotes(width);
        ImGui.Dummy(new Vector2(0f, M3.Space1));

        const string exportLabel = "Copy export string";
        const string teamCraftLabel = "Copy TeamCraft link";
        var flow = new M3Flow(M3.Space1, width);
        flow.Next(M3Widgets.ButtonWidth(FontAwesomeIcon.Copy, exportLabel));
        if (M3Widgets.Button("##exportList", exportLabel, M3ButtonStyle.Text, FontAwesomeIcon.Copy,
                tooltip: "Copy GatherBuddy's list export string to the clipboard."))
        {
            VulcanWindow.CopyListExport(_list);
        }

        flow.Next(M3Widgets.ButtonWidth(FontAwesomeIcon.Link, teamCraftLabel));
        if (M3Widgets.Button("##teamCraftExport", teamCraftLabel, M3ButtonStyle.Text, FontAwesomeIcon.Link,
                tooltip: "Copy a TeamCraft import link built from this list's recipes."))
        {
            VulcanWindow.CopyTeamCraftLink(_list);
        }
    }

    private void DrawNotes(float width)
    {
        var s = M3.Scheme;
        var minHeight = VulcanUiScaling.Scaled(60f);
        if (_editingDescActive)
        {
            if (_focusDescNext)
            {
                ImGui.SetKeyboardFocusHere();
                _focusDescNext = false;
            }

            ImGui.InputTextMultiline("##listDesc", ref _editingDescription, 512, new Vector2(width, minHeight));
            if (ImGui.IsItemDeactivated())
            {
                _list.Description = _editingDescription;
                GatherBuddy.CraftingListManager.SaveList(_list);
                _editingDescActive = false;
                GatherBuddy.Log.Debug($"[CraftingListEditor] Updated description for list '{_list.Name}'");
            }

            return;
        }

        var style = ImGui.GetStyle();
        var hasNotes = !string.IsNullOrEmpty(_editingDescription);
        var textHeight = hasNotes
            ? ImGui.CalcTextSize(_editingDescription, false, MathF.Max(1f, width - style.WindowPadding.X * 2f - style.ScrollbarSize)).Y
            : ImGui.GetTextLineHeight();
        var height = Math.Clamp(textHeight + style.WindowPadding.Y * 2f, minHeight, VulcanUiScaling.Scaled(150f));
        using (ImRaii.PushColor(ImGuiCol.ChildBg, M3.Alpha(s.SurfaceContainerHighest, 0.45f))
                   .Push(ImGuiCol.Border, M3.Alpha(s.OutlineVariant, 0.5f)))
        using (ImRaii.PushStyle(ImGuiStyleVar.ChildBorderSize, 1f)
                   .Push(ImGuiStyleVar.ChildRounding, M3.ShapeSmall))
        {
            using var notes = ImRaii.Child("##notesDisplay", new Vector2(width, height), true);
            if (!notes)
            {
                return;
            }

            if (hasNotes)
            {
                M3Text.Draw(_editingDescription, M3TextStyle.Muted, true);
            }
            else
            {
                M3Text.Draw("Click to add notes...", M3TextStyle.Faint);
            }

            if (ImGui.IsWindowHovered())
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.TextInput);
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                {
                    _editingDescActive = true;
                    _focusDescNext = true;
                }
            }
        }
    }

    private void DrawQueueOptionsCard()
    {
        using var card = M3Card.Begin("##queue_options_card", "Queue options", FontAwesomeIcon.SlidersH, M3.AccentFor(1));
        var skipIfEnough = _list.SkipIfEnough;
        if (M3Widgets.RowSwitch("Skip if already have enough##sie", ref skipIfEnough, "Leave out precrafts you already have enough of."))
        {
            _list.SkipIfEnough = skipIfEnough;
            InvalidateQueueCache();
            InvalidateMaterialCaches();
            InvalidatePresentationCaches();
            GatherBuddy.CraftingListManager.SaveList(_list);
            TriggerQueueRegeneration();
            RefreshInventoryCounts();
        }

        if (_list.SkipIfEnough)
        {
            using var skipOptions = M3SubGroup.Begin();
            var skipFinalIfEnough = _list.SkipFinalIfEnough;
            if (M3Widgets.RowSwitch("Include final crafts##sife", ref skipFinalIfEnough,
                    "Also reduce final crafts based on how many you already have. Useful for resuming an interrupted list."))
            {
                _list.SkipFinalIfEnough = skipFinalIfEnough;
                InvalidateQueueCache();
                InvalidatePresentationCaches();
                GatherBuddy.CraftingListManager.SaveList(_list);
                TriggerQueueRegeneration();
            }
        }

        var quickSynthAll = _list.QuickSynthAll;
        if (M3Widgets.RowSwitch("Quick synth all##qsa", ref quickSynthAll, "Force quick synthesis on eligible items in this list."))
        {
            _list.QuickSynthAll = quickSynthAll;
            SaveListAndRefresh(true);
        }

        if (_list.QuickSynthAll)
        {
            using var quickSynthOptions = M3SubGroup.Begin();
            var quickSynthAllPreferNQ = _list.QuickSynthAllPreferNQ;
            if (M3Widgets.RowSwitch("Prefer NQ##qsapnq", ref quickSynthAllPreferNQ,
                    "Turn on quick synthesis' 'Synthesize NQ items only' option for these crafts."))
            {
                _list.QuickSynthAllPreferNQ = quickSynthAllPreferNQ;
                SaveListAndRefresh(true);
            }

            var quickSynthAllPrecraftsOnly = _list.QuickSynthAllPrecraftsOnly;
            if (M3Widgets.RowSwitch("Precrafts only##qsapo", ref quickSynthAllPrecraftsOnly,
                    "Apply Quick synth all and Prefer NQ only to generated precrafts, leaving the final items unchanged."))
            {
                _list.QuickSynthAllPrecraftsOnly = quickSynthAllPrecraftsOnly;
                SaveListAndRefresh(true);
            }
        }

        var allaganEnabled = AllaganTools.Enabled;
        var retainerRestock = _list.RetainerRestock;
        if (M3Widgets.RowPluginSwitch("Restock from retainers##rrr", ref retainerRestock, AllaganToolsPlugin,
                allaganEnabled
                    ? "Withdraw needed materials from retainers before generating the gather list. Respects HQ/NQ preferences."
                    : "Requires Allagan Tools to be installed and enabled.",
                enabled: allaganEnabled))
        {
            _list.RetainerRestock = retainerRestock;
            SaveListAndRefresh(true);
        }
    }

    private void SaveListAndRefresh(bool regenerateMaterials)
    {
        GatherBuddy.CraftingListManager.SaveList(_list);
        InvalidateQueueCache();
        InvalidateMaterialCaches();
        InvalidatePresentationCaches();
        TriggerQueueRegeneration();
        if (regenerateMaterials)
        {
            TriggerMaterialsRegeneration();
        }
    }

    private void DrawConsumablesCard()
    {
        using var card = M3Card.Begin("##consumables_card", "Consumables & macros", FontAwesomeIcon.Flask, M3.AccentFor(2));
        var consumables = _list.Consumables;
        var hasAny = false;
        if (consumables.FoodItemId.HasValue)
        {
            M3Widgets.RowValue("Food", GetItemLabel(consumables.FoodItemId.Value, consumables.FoodHQ));
            hasAny = true;
        }

        if (consumables.MedicineItemId.HasValue)
        {
            M3Widgets.RowValue("Medicine", GetItemLabel(consumables.MedicineItemId.Value, consumables.MedicineHQ));
            hasAny = true;
        }

        if (consumables.ManualItemId.HasValue)
        {
            M3Widgets.RowValue("Manual", GetItemLabel(consumables.ManualItemId.Value, false));
            hasAny = true;
        }

        if (consumables.SquadronManualItemId.HasValue)
        {
            M3Widgets.RowValue("Squadron manual", GetItemLabel(consumables.SquadronManualItemId.Value, false));
            hasAny = true;
        }

        if (_list.UseAllHQ)
        {
            M3Widgets.RowValue("HQ materials", "All HQ");
            hasAny = true;
        }

        if (!hasAny)
        {
            M3Text.Draw("None set.", M3TextStyle.Muted);
        }

        ImGui.Dummy(new Vector2(0f, M3.Space1));
        if (M3Widgets.Button("##editConsumables", "Edit consumables & macros", M3ButtonStyle.Tonal, FontAwesomeIcon.Pen))
        {
            _consumablesPopup.OpenListDefaults(_list);
        }
    }

    private void DrawAddRecipeCard()
    {
        using var card = M3Card.Begin("##add_recipe_card", "Add recipe", FontAwesomeIcon.Plus, M3.AccentFor(0));
        EnsureRecipesLoaded();
        DrawRecipePicker(CardContentWidth());
        ImGui.Dummy(new Vector2(0f, M3.Space1));

        M3Widgets.NumberField("##quantity", ref _searchQuantity, M3Widgets.NumberFieldWidth(true), 1, 1);
        M3Tooltip.Hovered("How many times to craft it");
        ImGui.SameLine(0, M3.Space2);

        var canAdd = _selectedRecipe != null;
        var tooltip = canAdd
            ? $"Add {_recipeLabels.GetValueOrDefault(_selectedRecipe!.Value.RowId, "it")} x{_searchQuantity} to the list"
            : "Pick a recipe first";
        var clicked = M3Widgets.Button("##addRecipeBtn", "Add to list", M3ButtonStyle.Filled, FontAwesomeIcon.Plus, enabled: canAdd, tooltip: tooltip);
        if (!clicked && canAdd && ImGui.IsItemHovered() && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
        {
            clicked = true;
        }

        if (!clicked || _selectedRecipe == null)
        {
            return;
        }

        _list.AddRecipe(_selectedRecipe.Value.RowId, _searchQuantity);
        GatherBuddy.CraftingListManager.SaveList(_list);
        RaphaelAssessmentService.QueueWarmupForAddedListRecipe(_selectedRecipe.Value.RowId, _list);
        InvalidateQueueCache();
        InvalidateMaterialCaches();
        InvalidatePresentationCaches();
        TriggerQueueRegeneration();
        _selectedRecipe = null;
        _searchQuantity = 1;
    }

    private void DrawRecipePicker(float width)
    {
        var scale = M3.Scale;
        var open = ImGui.IsPopupOpen(RecipePickerPopupId);
        var selected = _selectedRecipe;
        var label = selected.HasValue
            ? _recipeLabels.GetValueOrDefault(selected.Value.RowId, "Pick a recipe")
            : "Search for a recipe to add";
        var icon = selected.HasValue ? M3ActionIcon.GameIcon(selected.Value.ItemResult.Value.Icon) : null;
        if (M3Widgets.PickerField("##recipe_picker_field", label, width, open, !selected.HasValue, icon))
        {
            ImGui.OpenPopup(RecipePickerPopupId);
        }

        var fieldMin = ImGui.GetItemRectMin();
        var fieldMax = ImGui.GetItemRectMax();
        ImGui.SetNextWindowPos(new Vector2(fieldMin.X, fieldMax.Y + 4f * scale), ImGuiCond.Appearing);
        ImGui.SetNextWindowSizeConstraints(new Vector2(width, 0f), new Vector2(width, 440f * scale));
        using var menu = M3Menu.Begin(RecipePickerPopupId);
        if (!menu.IsOpen)
        {
            return;
        }

        if (ImGui.IsWindowAppearing())
        {
            ImGui.SetKeyboardFocusHere();
        }

        M3Widgets.SearchField("##recipe_search", "Search by name, job or level", ref _lastComboFilter, ImGui.GetContentRegionAvail().X);
        UpdateRecipeFilter();
        ImGui.Dummy(new Vector2(0f, M3.Space1));
        if (_keywordFilteredRecipes.Count == 0)
        {
            M3Text.Draw("No recipes match.", M3TextStyle.Muted);
            return;
        }

        var rowHeight = M3Widgets.MenuItemHeight;
        var listHeight = MathF.Min(_keywordFilteredRecipes.Count * rowHeight, 360f * scale);
        using var list = ImRaii.Child("##recipe_results", new Vector2(0f, listHeight), false);
        if (!list)
        {
            return;
        }

        var clipper = ImGui.ImGuiListClipper();
        clipper.Begin(_keywordFilteredRecipes.Count, rowHeight);
        while (clipper.Step())
        {
            for (var row = clipper.DisplayStart; row < clipper.DisplayEnd; row++)
            {
                var recipe = _keywordFilteredRecipes[row];
                var rowId = recipe.RowId;
                if (M3Widgets.MenuItem($"##recipe_{rowId}", _recipeNames.GetValueOrDefault(rowId, string.Empty), selected?.RowId == rowId,
                        shortcut: _recipeDetails.GetValueOrDefault(rowId), texture: M3ActionIcon.GameIcon(recipe.ItemResult.Value.Icon)))
                {
                    _selectedRecipe = recipe;
                    ImGui.CloseCurrentPopup();
                }
            }
        }

        clipper.End();
        clipper.Destroy();
    }

    private void UpdateRecipeFilter()
    {
        if (_appliedRecipeFilter == _lastComboFilter)
        {
            return;
        }

        _appliedRecipeFilter = _lastComboFilter;
        _keywordFilteredRecipes.Clear();
        var keywords = _lastComboFilter.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var recipe in _allRecipes)
        {
            var label = _recipeLabels.GetValueOrDefault(recipe.RowId, string.Empty);
            var matches = true;
            foreach (var keyword in keywords)
            {
                if (!label.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                _keywordFilteredRecipes.Add(recipe);
            }
        }
    }

    private void EnsureRecipesLoaded()
    {
        if (_recipesLoaded)
        {
            return;
        }

        var recipeSheet = Dalamud.GameData.GetExcelSheet<Recipe>();
        if (recipeSheet == null)
        {
            return;
        }

        _allRecipes.Clear();
        foreach (var recipe in recipeSheet)
        {
            try
            {
                if (recipe.ItemResult.RowId == 0 || recipe.Number == 0)
                {
                    continue;
                }

                if (!_recipeLabels.ContainsKey(recipe.RowId))
                {
                    var name = recipe.ItemResult.Value.Name.ExtractText();
                    var detail = $"{GetCraftingJobName(recipe.CraftType.RowId)} {recipe.RecipeLevelTable.Value.ClassJobLevel}";
                    _recipeNames[recipe.RowId] = name;
                    _recipeDetails[recipe.RowId] = detail;
                    _recipeLabels[recipe.RowId] = $"{name} ({detail})";
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
            if (levelCmp != 0)
            {
                return levelCmp;
            }

            return _recipeNames.GetValueOrDefault(a.RowId, string.Empty).CompareTo(_recipeNames.GetValueOrDefault(b.RowId, string.Empty));
        });

        _recipesLoaded = true;
        _appliedRecipeFilter = null;
    }

    private void DrawRecipeListCard()
    {
        var count = _list.Recipes.Count;
        using var card = M3Card.Begin("##recipe_list_card", "Recipes", FontAwesomeIcon.ListUl, M3.AccentFor(1),
            subtitle: count > 1 ? "Click to select. Hold Ctrl or Shift to select more. Right-click a recipe for its settings." : null);
        if (count == 0)
        {
            M3Text.Draw("No recipes yet. Add one above.", M3TextStyle.Muted);
            return;
        }

        var indicesToRemove = new List<int>();
        if (_selectedRecipeIndices.Count > 1)
        {
            DrawRecipeBulkActions(indicesToRemove);
        }

        var recipeRows = GetRecipeDisplayRows();
        var clipper = ImGui.ImGuiListClipper();
        clipper.Begin(recipeRows.Count);
        while (clipper.Step())
        {
            for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                DrawRecipeListRow(recipeRows[i], indicesToRemove);
            }
        }

        clipper.End();
        clipper.Destroy();

        if (indicesToRemove.Count == 0)
        {
            return;
        }

        indicesToRemove.Sort();
        var lastRemoved = -1;
        for (var i = indicesToRemove.Count - 1; i >= 0; --i)
        {
            var idx = indicesToRemove[i];
            if (idx == lastRemoved || idx < 0 || idx >= _list.Recipes.Count)
            {
                continue;
            }

            _list.Recipes.RemoveAt(idx);
            lastRemoved = idx;
        }

        _selectedRecipeIndices.Clear();
        _lastClickedRecipeIndex = -1;
        SaveListAndRefresh(false);
    }

    private void DrawRecipeBulkActions(List<int> indicesToRemove)
    {
        var selectionCount = _selectedRecipeIndices.Count;
        var top = ImGui.GetCursorPosY();
        ImGui.SetCursorPosY(top + MathF.Max(0f, (M3Widgets.ButtonHeight - ImGui.GetTextLineHeight()) * 0.5f));
        M3Text.Draw($"{selectionCount} selected", M3TextStyle.Muted);
        ImGui.SameLine(0, M3.Space2);
        ImGui.SetCursorPosY(top);
        if (M3Widgets.Button("##skipSelected", "Skip", M3ButtonStyle.Text, FontAwesomeIcon.Ban,
                tooltip: $"Mark all {selectionCount} selected recipes as skipped."))
        {
            BulkSetSkipping(true);
        }

        ImGui.SameLine(0, M3.Space1);
        if (M3Widgets.Button("##enableSelected", "Enable", M3ButtonStyle.Text, FontAwesomeIcon.Check,
                tooltip: $"Re-enable all {selectionCount} selected recipes."))
        {
            BulkSetSkipping(false);
        }

        ImGui.SameLine(0, M3.Space1);
        if (M3Widgets.Button("##removeSelected", "Remove", M3ButtonStyle.Text, FontAwesomeIcon.Trash,
                tooltip: $"Remove the {selectionCount} selected recipes from this list.", accent: M3.Scheme.Error))
        {
            indicesToRemove.AddRange(_selectedRecipeIndices);
        }

        ImGui.Dummy(new Vector2(0f, M3.Space1));
    }

    private List<QueueDisplayRow> GetDisplayQueueRows(CraftingListDefinition planningList, CraftingExecutionPlan? activeExecutionPlan)
    {
        EnsureQueueDisplayRows(planningList, activeExecutionPlan);
        return _showPrecrafts
            ? (_cachedQueueDisplayRows ?? [])
            : (_cachedOriginalQueueDisplayRows ?? []);
    }

    private void EnsureQueueDisplayRows(CraftingListDefinition planningList, CraftingExecutionPlan? activeExecutionPlan)
    {
        var queueCache = GetQueueCache();
        if (queueCache == null)
        {
            return;
        }

        var currentHash = ComputeListHash();
        if (_cachedQueueDisplayRowsValid && _cachedQueueDisplayRowsHash == currentHash
         && _cachedQueueDisplayRows != null && _cachedOriginalQueueDisplayRows != null)
        {
            return;
        }

        try
        {
            var sortedQueue = queueCache.SortedQueue;
            _cachedQueueDisplayRows = BuildQueueDisplayRows(sortedQueue, planningList);
            var originalQueue = activeExecutionPlan != null
                ? activeExecutionPlan.OriginalRecipesView
                : BuildOriginalQueue(sortedQueue);
            _cachedOriginalQueueDisplayRows = BuildQueueDisplayRows(originalQueue, planningList);
            _cachedQueueDisplayRowsHash = currentHash;
            _cachedQueueDisplayRowsValid = true;
            _cachedValidationIssueCountsHash = string.Empty;
            _cachedValidationIssueCountsValid = false;
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[CraftingListEditor] Failed to rebuild queue display cache for list '{_list.Name}': {ex.Message}");
            _cachedQueueDisplayRows = [];
            _cachedOriginalQueueDisplayRows = [];
            _cachedQueueDisplayRowsHash = currentHash;
            _cachedQueueDisplayRowsValid = true;
            _cachedValidationIssueCountsHash = string.Empty;
            _cachedValidationIssueCountsValid = false;
        }
    }

    private static List<CraftingListItem> BuildOriginalQueue(List<CraftingListItem> sortedQueue)
    {
        var originalQueue = new List<CraftingListItem>();
        foreach (var queueItem in sortedQueue)
        {
            if (queueItem.IsOriginalRecipe)
            {
                originalQueue.Add(new CraftingListItem(queueItem.RecipeId, queueItem.Quantity)
                {
                    IsOriginalRecipe = true,
                });
            }
        }

        return originalQueue;
    }

    private CraftingListItem? FindListItem(uint recipeId)
    {
        foreach (var recipeItem in _list.Recipes)
        {
            if (recipeItem.RecipeId == recipeId)
            {
                return recipeItem;
            }
        }

        return null;
    }

    private List<QueueDisplayRow> BuildQueueDisplayRows(IReadOnlyList<CraftingListItem> sourceQueue, CraftingListDefinition planningList)
    {
        var rows = new List<QueueDisplayRow>(sourceQueue.Count);
        for (var i = 0; i < sourceQueue.Count; i++)
        {
            var queueItem = sourceQueue[i];
            var recipeData = RecipeManager.GetRecipe(queueItem.RecipeId);
            if (recipeData == null)
            {
                continue;
            }

            var itemName = recipeData.Value.ItemResult.Value.Name.ExtractText();
            var recipeOptions = planningList.GetRecipeOptions(queueItem.RecipeId, queueItem.IsOriginalRecipe);
            var effectiveQuickSynth = recipeOptions.NQOnly || planningList.ShouldForceQuickSynth(recipeData.Value, queueItem.IsOriginalRecipe);
            var forceQuickSynth = planningList.ShouldForceQuickSynth(recipeData.Value, queueItem.IsOriginalRecipe);
            var forcePreferNQNoQuickSynth = !recipeData.Value.CanQuickSynth && planningList.ShouldForcePreferNQ(queueItem.IsOriginalRecipe);
            var queueItemCraftSettings = GetEffectiveCraftSettings(queueItem.RecipeId, queueItem.IsOriginalRecipe);
            var hasExecutionContext = CraftingContextResolver.TryResolveListExecutionContext(
                planningList,
                queueItem.RecipeId,
                queueItem.IsOriginalRecipe,
                out var executionContext);
            var usesQuickSynth = hasExecutionContext
                ? executionContext.UseQuickSynthesis
                : WillUseQuickSynth(recipeData.Value, queueItem.RecipeId, queueItem.IsOriginalRecipe);
            var validation = usesQuickSynth
                ? null
                : MacroValidator.GetOrCompute(queueItem.RecipeId,
                    ResolveEffectiveMacroId(queueItemCraftSettings, !queueItem.IsOriginalRecipe),
                    queueItemCraftSettings,
                    planningList.Consumables);
            RaphaelAssessment? raphaelAssessment = null;
            if (hasExecutionContext
             && CraftingContextResolver.UsesRaphaelSolver(executionContext))
            {
                RaphaelAssessmentService.TryAssessListQueueItem(queueItem.RecipeId, queueItem.IsOriginalRecipe, planningList, out var resolvedAssessment);
                raphaelAssessment = resolvedAssessment;
            }
            rows.Add(new QueueDisplayRow
            {
                QueueIndex = i,
                Quantity = queueItem.Quantity,
                IsOriginalRecipe = queueItem.IsOriginalRecipe,
                Recipe = recipeData.Value,
                ItemName = itemName,
                IndexText = $"{i + 1}.",
                QuantityText = $" ×{queueItem.Quantity}",
                EffectiveQuickSynth = effectiveQuickSynth,
                ForceQuickSynth = forceQuickSynth,
                ForcePreferNQ = forcePreferNQNoQuickSynth,
                Validation = validation,
                RaphaelAssessment = raphaelAssessment,
            });
        }

        return rows;
    }

    private void DrawQueueRow(QueueDisplayRow row, CraftingListDefinition planningList, float rowHeight)
    {
        var s = M3.Scheme;
        var willBeSkipped = planningList.SkipIfEnough
            && (!row.IsOriginalRecipe
                ? WillBeSkippedDueToInventory(row.Recipe)
                : planningList.SkipFinalIfEnough && row.Quantity == 0);

        using var id = ImRaii.PushId(row.QueueIndex);
        var width = MathF.Max(1f, ImGui.GetContentRegionAvail().X);
        if (ImGui.InvisibleButton("##queue_row", new Vector2(width, rowHeight)))
        {
            _selectedQueueIndex = row.QueueIndex;
        }

        var hovered = ImGui.IsItemHovered();
        var held = ImGui.IsItemActive();
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var drawList = ImGui.GetWindowDrawList();
        DrawQueueRowMenu(row, planningList);

        if (_selectedQueueIndex == row.QueueIndex)
        {
            drawList.AddRectFilled(min, max, M3.U32(s.SecondaryContainer, 0.9f), M3.ShapeSmall);
        }

        if (hovered || held)
        {
            drawList.AddRectFilled(min, max, M3.U32(s.OnSurface, held ? M3.StatePressed : M3.StateHover), M3.ShapeSmall);
        }

        _rowTags.Clear();
        if (row.EffectiveQuickSynth)
        {
            _rowTags.Add(new RowTag("QS", M3.CustomColor(HueQuickSynth).Color, "Quick synthesis"));
        }
        else if (row.ForcePreferNQ)
        {
            _rowTags.Add(new RowTag("NQ", s.OnSurfaceVariant, "Prefer NQ: crafted from NQ ingredients"));
        }

        if (willBeSkipped)
        {
            _rowTags.Add(new RowTag("Skipped", s.Error, "Skipped: you already have enough"));
        }

        var nameColor = willBeSkipped ? s.Error : row.IsOriginalRecipe ? s.OnSurface : s.OnSurfaceVariant;
        var tooltip = DrawRowContent(drawList, min, max, hovered, row.RaphaelAssessment, row.Validation, row.IndexText, _queueIndexWidth,
            row.ItemName, row.QuantityText, nameColor, false, row.Recipe);
        if (tooltip != null)
        {
            M3Tooltip.Show(tooltip);
        }
    }

    private void DrawQueueRowMenu(QueueDisplayRow row, CraftingListDefinition planningList)
    {
        using var menu = M3Menu.BeginContext("##queue_menu");
        if (!menu.IsOpen)
        {
            return;
        }

        if (M3Menu.Item("##queue_settings", "Craft settings...", FontAwesomeIcon.Cog))
        {
            if (row.IsOriginalRecipe)
            {
                var listItem = FindListItem(row.Recipe.RowId);
                if (listItem != null)
                {
                    _craftSettingsPopup.OpenForListItem(listItem, _list, row.ItemName);
                }
            }
            else
            {
                _craftSettingsPopup.OpenForPrecraft(row.Recipe.RowId, row.ItemName, _list);
            }
        }

        var resultItemId = row.Recipe.ItemResult.RowId;
        var altRecipes = RecipeManager.GetRecipesForItem(resultItemId);
        if (altRecipes.Count > 1)
        {
            var picked = false;
            using (var jobs = M3Menu.BeginSub("##queue_change_job", "Change job", FontAwesomeIcon.ExchangeAlt))
            {
                if (jobs.IsOpen)
                {
                    picked = row.IsOriginalRecipe
                        ? DrawFinalJobChoices(row.Recipe.RowId, altRecipes)
                        : DrawPrecraftJobChoices(resultItemId, altRecipes);
                }
            }

            if (picked)
            {
                M3Menu.Close();
            }
        }

        M3Menu.Divider();
        if (row.Recipe.CanQuickSynth)
        {
            var recipeOptions = planningList.GetRecipeOptions(row.Recipe.RowId, row.IsOriginalRecipe);
            if (M3Menu.Item("##queue_quick_synth", "Quick synthesis", FontAwesomeIcon.FastForward, !row.ForceQuickSynth, row.EffectiveQuickSynth,
                    tooltip: row.ForceQuickSynth
                        ? "Forced on by Quick Synth All for this recipe. Disable the list-level override to edit the per-item quick synth setting."
                        : "Use quick synthesis for this recipe (NQ only)"))
            {
                _list.SetRecipeQuickSynth(row.Recipe.RowId, !recipeOptions.NQOnly, row.IsOriginalRecipe);
                SaveListAndRefresh(true);
            }
        }
        else
        {
            M3Menu.Item("##queue_quick_synth", "Quick synthesis unavailable", FontAwesomeIcon.FastForward, false,
                tooltip: "Recipe must be unlocked and previously crafted to use Quick Synthesis");
        }
    }

    private bool DrawFinalJobChoices(uint currentRecipeId, IReadOnlyList<Recipe> altRecipes)
    {
        var picked = false;
        foreach (var alt in altRecipes)
        {
            var isCurrent = alt.RowId == currentRecipeId;
            if (!M3Menu.Item($"##job_{alt.RowId}", GetCraftingJobName(alt.CraftType.RowId), selected: isCurrent))
            {
                continue;
            }

            picked = true;
            if (isCurrent)
            {
                continue;
            }

            var listItem = FindListItem(currentRecipeId);
            if (listItem != null)
            {
                listItem.RecipeId = alt.RowId;
                SaveListAndRefresh(true);
            }
        }

        return picked;
    }

    private bool DrawPrecraftJobChoices(uint resultItemId, IReadOnlyList<Recipe> altRecipes)
    {
        var picked = false;
        var activeRecipeId = _list.PrecraftRecipeOverrides.TryGetValue(resultItemId, out var overrideRecipeId)
            ? overrideRecipeId
            : altRecipes[0].RowId;
        foreach (var alt in altRecipes)
        {
            var isCurrent = alt.RowId == activeRecipeId;
            if (!M3Menu.Item($"##job_{alt.RowId}", GetCraftingJobName(alt.CraftType.RowId), selected: isCurrent))
            {
                continue;
            }

            picked = true;
            if (!isCurrent)
            {
                _list.PrecraftRecipeOverrides[resultItemId] = alt.RowId;
                SaveListAndRefresh(true);
            }
        }

        if (_list.PrecraftRecipeOverrides.ContainsKey(resultItemId))
        {
            M3Menu.Divider();
            if (M3Menu.Item("##job_reset", "Reset to default", FontAwesomeIcon.Undo))
            {
                picked = true;
                _list.PrecraftRecipeOverrides.Remove(resultItemId);
                SaveListAndRefresh(true);
            }
        }

        return picked;
    }

    private List<RecipeDisplayRow> GetRecipeDisplayRows()
    {
        if (_cachedRecipeDisplayRowsValid && _cachedRecipeDisplayRows != null)
        {
            return _cachedRecipeDisplayRows;
        }

        try
        {
            _cachedRecipeDisplayRows = BuildRecipeDisplayRows();
            _cachedRecipeDisplayRowsValid = true;
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[CraftingListEditor] Failed to rebuild recipe display cache for list '{_list.Name}': {ex.Message}");
            _cachedRecipeDisplayRows = [];
            _cachedRecipeDisplayRowsValid = true;
        }

        return _cachedRecipeDisplayRows;
    }

    private List<RecipeDisplayRow> BuildRecipeDisplayRows()
    {
        var planningList = GetPlanningList();
        var rows = new List<RecipeDisplayRow>(_list.Recipes.Count);
        for (var i = 0; i < _list.Recipes.Count; i++)
        {
            var item = _list.Recipes[i];
            var recipe = RecipeManager.GetRecipe(item.RecipeId);
            if (recipe == null)
            {
                continue;
            }

            var itemName = recipe.Value.ItemResult.Value.Name.ExtractText();
            var effectiveCraftSettings = GetEffectiveCraftSettings(item.RecipeId, true);
            var effectiveQuickSynth = IsEffectivelyQuickSynth(recipe.Value, item.RecipeId, true);
            var forcePreferNQNoQuickSynth = !recipe.Value.CanQuickSynth && planningList.ShouldForcePreferNQ(true);
            var hasExecutionContext = CraftingContextResolver.TryResolveListExecutionContext(
                planningList,
                item,
                out var executionContext);
            var usesQuickSynth = hasExecutionContext
                ? executionContext.UseQuickSynthesis
                : WillUseQuickSynth(recipe.Value, item.RecipeId, true);
            var validation = usesQuickSynth
                ? null
                : MacroValidator.GetOrCompute(item.RecipeId,
                    ResolveEffectiveMacroId(effectiveCraftSettings, false),
                    effectiveCraftSettings,
                    planningList.Consumables);
            RaphaelAssessment? raphaelAssessment = null;
            if (hasExecutionContext
             && CraftingContextResolver.UsesRaphaelSolver(executionContext)
             && RaphaelAssessmentService.TryAssessListRecipe(item.RecipeId, planningList, effectiveCraftSettings, out var resolvedAssessment))
            {
                raphaelAssessment = resolvedAssessment;
            }

            rows.Add(new RecipeDisplayRow
            {
                ListIndex = i,
                Recipe = recipe.Value,
                ItemName = itemName,
                EffectiveQuickSynth = effectiveQuickSynth,
                ForcePreferNQ = forcePreferNQNoQuickSynth,
                HasCustomSettings = item.CraftSettings?.HasAnySettings() == true,
                HasIngredientPreferences = effectiveCraftSettings?.IngredientPreferences.Count > 0,
                Validation = validation,
                RaphaelAssessment = raphaelAssessment,
            });
        }

        return rows;
    }

    private void DrawRecipeListRow(RecipeDisplayRow row, List<int> indicesToRemove)
    {
        if (row.ListIndex >= _list.Recipes.Count)
        {
            return;
        }

        var s = M3.Scheme;
        var scale = M3.Scale;
        var item = _list.Recipes[row.ListIndex];
        using var id = ImRaii.PushId(row.ListIndex);

        var rowHeight = M3Widgets.NumberFieldHeight;
        var buttonSize = MathF.Min(M3Widgets.IconButtonSize, rowHeight);
        var quantityWidth = M3Widgets.NumberFieldWidth(true);
        var gap = M3.Space1;
        var rowWidth = CardContentWidth();
        var labelWidth = MathF.Max(60f * scale, rowWidth - quantityWidth - buttonSize * 2f - gap * 2f);
        var top = ImGui.GetCursorScreenPos();

        if (ImGui.InvisibleButton("##recipe_row", new Vector2(labelWidth, rowHeight)))
        {
            SelectRecipeRow(row.ListIndex);
        }

        var hovered = ImGui.IsItemHovered();
        var held = ImGui.IsItemActive();
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var drawList = ImGui.GetWindowDrawList();
        DrawRecipeRowMenu(row, item);

        if (_selectedRecipeIndices.Contains(row.ListIndex))
        {
            drawList.AddRectFilled(min, max, M3.U32(s.SecondaryContainer, 0.9f), M3.ShapeSmall);
        }

        if (hovered || held)
        {
            drawList.AddRectFilled(min, max, M3.U32(s.OnSurface, held ? M3.StatePressed : M3.StateHover), M3.ShapeSmall);
        }

        var skipping = item.Options.Skipping;
        _rowTags.Clear();
        if (row.EffectiveQuickSynth)
        {
            _rowTags.Add(new RowTag("QS", M3.CustomColor(HueQuickSynth).Color, "Quick synthesis"));
        }
        else if (row.ForcePreferNQ)
        {
            _rowTags.Add(new RowTag("NQ", s.OnSurfaceVariant, "Prefer NQ: crafted from NQ ingredients"));
        }

        if (row.HasCustomSettings)
        {
            _rowTags.Add(new RowTag("Settings", s.Primary, "Has its own craft settings. Right-click for Craft settings."));
        }

        if (row.HasIngredientPreferences)
        {
            _rowTags.Add(new RowTag("HQ", s.Tertiary, "Uses HQ ingredient preferences"));
        }

        if (skipping)
        {
            _rowTags.Add(new RowTag("Skipped", s.Error, "Skipped in the queue"));
        }

        var nameColor = skipping ? M3.Alpha(s.OnSurfaceVariant, 0.7f) : s.OnSurface;
        var tooltip = DrawRowContent(drawList, min, max, hovered, row.RaphaelAssessment, row.Validation, null, 0f, row.ItemName, null, nameColor,
            skipping, row.Recipe);
        if (tooltip != null)
        {
            M3Tooltip.Show(tooltip);
        }

        ImGui.SetCursorScreenPos(new Vector2(min.X + labelWidth + gap, top.Y));
        var io = ImGui.GetIO();
        var quantity = item.Quantity;
        var step = io.KeyShift ? 100 : io.KeyCtrl ? 10 : 1;
        if (M3Widgets.NumberField("##qty", ref quantity, quantityWidth, step, 1))
        {
            quantity = Math.Max(1, quantity);
            if (quantity != item.Quantity)
            {
                _list.UpdateRecipeQuantity(item.RecipeId, quantity);
                SaveListAndRefresh(false);
            }
        }

        M3Tooltip.Hovered("Click - or + to change it by 1.\nHold Ctrl for 10, Shift for 100.");

        ImGui.SameLine(0, gap);
        ImGui.SetCursorScreenPos(new Vector2(ImGui.GetCursorScreenPos().X, top.Y + (rowHeight - buttonSize) * 0.5f));
        if (M3Widgets.IconButton("##skip", skipping ? FontAwesomeIcon.Check : FontAwesomeIcon.Ban,
                skipping ? "Re-enable this recipe in the queue." : "Skip this recipe in the queue.", tint: skipping ? s.Primary : null,
                diameter: buttonSize))
        {
            item.Options.Skipping = !skipping;
            SaveListAndRefresh(false);
        }

        ImGui.SameLine(0, 0);
        if (M3Widgets.IconButton("##remove", FontAwesomeIcon.Trash, "Remove this recipe from the list.", diameter: buttonSize))
        {
            indicesToRemove.Add(row.ListIndex);
        }
    }

    private void SelectRecipeRow(int index)
    {
        var io = ImGui.GetIO();
        if (io.KeyShift && _lastClickedRecipeIndex >= 0)
        {
            if (!io.KeyCtrl)
            {
                _selectedRecipeIndices.Clear();
            }

            var min = Math.Min(_lastClickedRecipeIndex, index);
            var max = Math.Max(_lastClickedRecipeIndex, index);
            for (var i = min; i <= max; i++)
            {
                _selectedRecipeIndices.Add(i);
            }
        }
        else if (io.KeyCtrl)
        {
            if (!_selectedRecipeIndices.Remove(index))
            {
                _selectedRecipeIndices.Add(index);
            }

            _lastClickedRecipeIndex = index;
        }
        else
        {
            _selectedRecipeIndices.Clear();
            _selectedRecipeIndices.Add(index);
            _lastClickedRecipeIndex = index;
        }
    }

    private void DrawRecipeRowMenu(RecipeDisplayRow row, CraftingListItem item)
    {
        using var menu = M3Menu.BeginContext("##recipe_menu");
        if (!menu.IsOpen)
        {
            return;
        }

        if (M3Menu.Item("##recipe_settings", "Craft settings...", FontAwesomeIcon.Cog))
        {
            _craftSettingsPopup.OpenForListItem(item, _list, row.ItemName);
        }

        var altRecipes = RecipeManager.GetRecipesForItem(row.Recipe.ItemResult.RowId);
        if (altRecipes.Count <= 1)
        {
            return;
        }

        var picked = false;
        using (var jobs = M3Menu.BeginSub("##recipe_change_job", "Change job", FontAwesomeIcon.ExchangeAlt))
        {
            if (jobs.IsOpen)
            {
                foreach (var alt in altRecipes)
                {
                    var isCurrent = alt.RowId == item.RecipeId;
                    if (!M3Menu.Item($"##job_{alt.RowId}", GetCraftingJobName(alt.CraftType.RowId), selected: isCurrent))
                    {
                        continue;
                    }

                    picked = true;
                    if (!isCurrent)
                    {
                        item.RecipeId = alt.RowId;
                        SaveListAndRefresh(true);
                    }
                }
            }
        }

        if (picked)
        {
            M3Menu.Close();
        }
    }

    private void BulkSetSkipping(bool skipping)
    {
        var changed = false;
        foreach (var idx in _selectedRecipeIndices)
        {
            if (idx < 0 || idx >= _list.Recipes.Count)
            {
                continue;
            }

            var recipe = _list.Recipes[idx];
            if (recipe.Options.Skipping == skipping)
            {
                continue;
            }

            recipe.Options.Skipping = skipping;
            changed = true;
        }
        if (!changed)
        {
            return;
        }

        GatherBuddy.CraftingListManager.SaveList(_list);
        InvalidateQueueCache();
        InvalidateMaterialCaches();
        InvalidatePresentationCaches();
        TriggerQueueRegeneration();
    }

    private readonly record struct RowTag(string Label, Vector4 Tone, string Tooltip);

    private readonly List<RowTag> _rowTags = [];

    private static Vector4 ValidationColor(MacroValidationResult validation)
    {
        var s = M3.Scheme;
        if (validation.IsValid)
        {
            return s.Success;
        }

        return validation.Failure is MacroValidationFailure.InsufficientProgress or MacroValidationFailure.ActionUnusable
            ? s.Warning
            : s.Error;
    }

    private static string ValidationTooltip(MacroValidationResult validation)
        => validation.IsValid
            ? $"Macro: PASS\nProgress: {validation.FinalProgress}/{validation.RequiredProgress}\nQuality: {validation.FinalQuality}\nDurability: {validation.FinalDurability}"
            : $"Macro: {validation.Failure} at step {validation.FailedAtStep}\nProgress: {validation.FinalProgress}/{validation.RequiredProgress}";

    private static Vector4 RaphaelColor(RaphaelAssessment assessment)
    {
        var s = M3.Scheme;
        return assessment.State switch
        {
            RaphaelAssessmentState.Ready when assessment.Outcome is RaphaelAssessmentOutcome.FullQuality
                or RaphaelAssessmentOutcome.CollectibleTier3
                or RaphaelAssessmentOutcome.MinimumQualityMet
                or RaphaelAssessmentOutcome.NoQualityRequired
                => s.Success,
            RaphaelAssessmentState.Ready => s.Warning,
            RaphaelAssessmentState.Generating => s.Info,
            RaphaelAssessmentState.Failed => s.Error,
            RaphaelAssessmentState.Unavailable => s.Warning,
            _ => s.OnSurfaceVariant,
        };
    }

    private static string RaphaelTooltip(RaphaelAssessment assessment)
        => $"Raphael: {assessment.Summary}\n{assessment.Details}";

    private string? DrawRowContent(ImDrawListPtr drawList, Vector2 min, Vector2 max, bool hovered, RaphaelAssessment? raphael,
        MacroValidationResult? validation, string? prefix, float prefixWidth, string name, string? quantity, Vector4 nameColor, bool strike, Recipe recipe)
    {
        var s = M3.Scheme;
        var scale = M3.Scale;
        var centerY = (min.Y + max.Y) * 0.5f;
        var lineHeight = ImGui.GetTextLineHeight();
        var textY = centerY - lineHeight * 0.5f;
        var x = min.X + 8f * scale;
        string? tooltip = null;

        if (raphael != null)
        {
            if (M3Draw.StatusDot(drawList, new Vector2(x + 4f * scale, centerY), RaphaelColor(raphael)) && hovered)
            {
                tooltip = RaphaelTooltip(raphael);
            }

            x += 14f * scale;
        }

        if (validation != null)
        {
            if (M3Draw.StatusDot(drawList, new Vector2(x + 4f * scale, centerY), ValidationColor(validation)) && hovered)
            {
                tooltip ??= ValidationTooltip(validation);
            }

            x += 14f * scale;
        }

        if (prefix != null)
        {
            drawList.AddText(new Vector2(x, textY), M3.U32(s.OnSurfaceVariant, 0.75f), prefix);
            x += MathF.Max(prefixWidth, ImGui.CalcTextSize(prefix).X) + 6f * scale;
        }

        var crafter = CraftingRowIcons.GetCrafterIcon(recipe);
        var iconSize = MathF.Min(max.Y - min.Y - 6f * scale, lineHeight + 2f * scale);
        var iconMin = new Vector2(max.X - 8f * scale - iconSize, centerY - iconSize * 0.5f);
        var icon = M3ActionIcon.GameIcon(crafter.IconId);
        if (icon != null)
        {
            M3Draw.Texture(drawList, icon, iconMin, iconSize);
        }

        if (hovered && tooltip == null && !string.IsNullOrEmpty(crafter.Tooltip) && ImGui.IsMouseHoveringRect(iconMin, iconMin + new Vector2(iconSize)))
        {
            tooltip = crafter.Tooltip;
        }

        var right = iconMin.X - 6f * scale;
        for (var i = _rowTags.Count - 1; i >= 0; i--)
        {
            var tag = _rowTags[i];
            var size = M3Badge.Size(tag.Label);
            var tagMin = new Vector2(right - size.X, centerY - size.Y * 0.5f);
            if (tagMin.X < x + 40f * scale)
            {
                break;
            }

            M3Badge.Paint(drawList, tagMin, tag.Label, tag.Tone);
            if (hovered && tooltip == null && ImGui.IsMouseHoveringRect(tagMin, tagMin + size))
            {
                tooltip = tag.Tooltip;
            }

            right = tagMin.X - 4f * scale;
        }

        var quantityWidth = string.IsNullOrEmpty(quantity) ? 0f : ImGui.CalcTextSize(quantity).X;
        var shown = M3Navigation.Truncate(name, MathF.Max(8f * scale, right - 4f * scale - x - quantityWidth));
        var nameWidth = ImGui.CalcTextSize(shown).X;
        drawList.AddText(new Vector2(x, textY), M3.U32(nameColor), shown);
        if (strike)
        {
            drawList.AddLine(new Vector2(x, centerY), new Vector2(x + nameWidth, centerY), M3.U32(nameColor), MathF.Max(1f, scale));
        }

        if (quantityWidth > 0f)
        {
            drawList.AddText(new Vector2(x + nameWidth, textY), M3.U32(s.OnSurfaceVariant), quantity);
        }

        if (hovered && tooltip == null && shown != name && ImGui.IsMouseHoveringRect(new Vector2(x, min.Y), new Vector2(x + nameWidth, max.Y)))
        {
            tooltip = name;
        }

        return tooltip;
    }

    private string ComputeListHash()
    {
        var activeExecutionPlan = GetActiveExecutionPlan();
        var planningList = activeExecutionPlan?.PlanningSnapshot ?? _list;
        var hashParts = new List<string>
        {
            $"SkipIfEnough:{planningList.SkipIfEnough}",
            $"SkipFinalIfEnough:{planningList.SkipFinalIfEnough}",
            $"RetainerRestock:{planningList.RetainerRestock}"
        };
        foreach (var item in planningList.Recipes)
        {
            hashParts.Add($"{item.RecipeId}:{item.Quantity}:{item.Options.Skipping}");
        }
        if (activeExecutionPlan != null)
        {
            hashParts.Add($"ExecutionPlanVersion:{activeExecutionPlan.Version}");
        }

        return string.Join("|", hashParts);
    }

    private void InvalidateMaterialCaches()
    {
        Volatile.Write(ref _materialCache, null);
        Interlocked.Increment(ref _materialGenerationVersion);
        Interlocked.Increment(ref _materialCacheVersion);
    }
    private static QueueCacheSnapshot BuildQueueCacheSnapshot(CraftingListPlan plan, string hash)
        => new()
        {
            Hash = hash,
            SortedQueue = BuildDisplayQueue(plan),
        };

    private static MaterialCacheSnapshot BuildMaterialCacheSnapshot(CraftingListDefinition planningList, string hash)
        => BuildMaterialCacheSnapshot(
            planningList.CreatePlan(ShouldUseRetainerCraftablePlanning(planningList)),
            planningList,
            hash);

    private static MaterialCacheSnapshot BuildMaterialCacheSnapshot(CraftingListPlan plan, CraftingListDefinition planningList, string hash)
    {
        var precraftMaterials = BuildCraftPanelMaterials(plan);
        var displayPlan = BuildDisplayMaterialPlan(planningList);
        var displayPrecraftMaterials = BuildCraftPanelMaterials(displayPlan, planningList.Recipes);
        return new MaterialCacheSnapshot
        {
            Hash = hash,
            Materials = [with(plan.Materials)],
            PrecraftMaterials = precraftMaterials,
            IngredientDemands = [with(plan.IngredientDemands)],
            CraftMaterialDemands = BuildCraftPanelDemands(plan, precraftMaterials),
            DisplayMaterials = [with(displayPlan.Materials)],
            DisplayPrecraftMaterials = displayPrecraftMaterials,
            DisplayIngredientDemands = [with(displayPlan.IngredientDemands)],
            DisplayCraftMaterialDemands = BuildCraftPanelDemands(displayPlan, displayPrecraftMaterials, planningList.Recipes),
        };
    }


    private static CraftingListPlan BuildDisplayMaterialPlan(CraftingListDefinition planningList)
        => CraftingListPlanner.Build(planningList, new CraftingListPlannerOptions(
            UseRetainerCraftableAvailability: ShouldUseRetainerCraftablePlanning(planningList),
            ConsumeIntermediateAvailability: true,
            ConsumeFinalAvailability: true));

    private static bool ShouldUseRetainerCraftablePlanning(CraftingListDefinition planningList)
        => planningList.SkipIfEnough && planningList.RetainerRestock && AllaganTools.Enabled;

    private MaterialCacheSnapshot EnsureMaterialCache(string hash)
    {
        if (TryCacheActiveExecutionPlan(hash))
        {
            return GetMaterialCache()!;
        }

        var materialCache = GetMaterialCache();
        if (materialCache != null && materialCache.Hash == hash)
        {
            return materialCache;
        }

        var planningSnapshot = CreatePlanningSnapshot();
        materialCache = BuildMaterialCacheSnapshot(planningSnapshot, hash);
        PublishMaterialCache(materialCache);
        return materialCache;
    }

    private static Dictionary<uint, int> BuildCraftPanelMaterials(CraftingListPlan plan, IEnumerable<CraftingListItem>? finalSourceRecipes = null)
    {
        var itemSheet = Dalamud.GameData.GetExcelSheet<Item>();
        var craftPanelMaterials = new Dictionary<uint, int>();

        foreach (var (itemId, quantity) in plan.Precrafts)
        {
            if (quantity <= 0)
            {
                continue;
            }

            if (itemSheet != null && itemSheet.TryGetRow(itemId, out var item) && IsEquippableCraftPanelItem(item))
            {
                continue;
            }

            craftPanelMaterials[itemId] = quantity;
        }

        foreach (var originalRecipe in finalSourceRecipes ?? plan.OriginalRecipes)
        {
            if (originalRecipe.Options.Skipping || originalRecipe.Quantity <= 0)
            {
                continue;
            }

            var recipe = RecipeManager.GetRecipe(originalRecipe.RecipeId);
            if (recipe == null)
            {
                continue;
            }

            var resultItem = recipe.Value.ItemResult.Value;
            if (IsEquippableCraftPanelItem(resultItem))
            {
                continue;
            }

            var resultItemId = resultItem.RowId;
            var finalItemCount = originalRecipe.Quantity * (int)recipe.Value.AmountResult;
            if (craftPanelMaterials.TryGetValue(resultItemId, out var existingCount))
            {
                craftPanelMaterials[resultItemId] = existingCount + finalItemCount;
            }
            else
            {
                craftPanelMaterials[resultItemId] = finalItemCount;
            }
        }

        return craftPanelMaterials;
    }

    private static bool IsEquippableCraftPanelItem(Item item)
        => item.RowId > 0 && item.EquipSlotCategory.RowId > 0;

    private static Dictionary<uint, IngredientQualityDemand> BuildCraftPanelDemands(CraftingListPlan plan, Dictionary<uint, int> craftPanelMaterials, IEnumerable<CraftingListItem>? finalSourceRecipes = null)
    {
        var demands = new Dictionary<uint, IngredientQualityDemand>();
        foreach (var (itemId, demand) in plan.IngredientDemands)
        {
            if (craftPanelMaterials.ContainsKey(itemId))
            {
                demands.Add(itemId, demand);
            }
        }

        foreach (var originalRecipe in finalSourceRecipes ?? plan.OriginalRecipes)
        {
            if (originalRecipe.Options.Skipping || originalRecipe.Quantity <= 0)
            {
                continue;
            }

            var recipe = RecipeManager.GetRecipe(originalRecipe.RecipeId);
            if (recipe == null)
            {
                continue;
            }

            var resultItemId = recipe.Value.ItemResult.RowId;
            if (!craftPanelMaterials.ContainsKey(resultItemId))
            {
                continue;
            }

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
        var queueCache = GetQueueCache();
        if (queueCache != null && queueCache.Hash == currentHash)
        {
            return;
        }

        var generation = Interlocked.Increment(ref _queueGenerationVersion);

        if (TryCacheActiveExecutionPlan(currentHash))
        {
            _queueCancellationSource?.Cancel();
            _queueCancellationSource?.Dispose();
            _queueCancellationSource = null;
            _queueGenerationTask = null;
            _isGeneratingQueue = false;
            return;
        }

        var planningSnapshot = CreatePlanningSnapshot();

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
                if (token.IsCancellationRequested)
                {
                    return;
                }

                var queueCacheSnapshot = BuildQueueCacheSnapshot(
                    planningSnapshot.CreatePlan(ShouldUseRetainerCraftablePlanning(planningSnapshot)),
                    hash);
                TryPublishQueueCache(queueCacheSnapshot, generation, token);
            }
            catch (Exception ex)
            {
                GatherBuddy.Log.Error($"Error generating queue: {ex.Message}");
            }
            finally
            {
                if (generation == Volatile.Read(ref _queueGenerationVersion))
                {
                    _isGeneratingQueue = false;
                }
            }
        }, token);
    }

    internal void TriggerMaterialsRegeneration()
    {
        ProcessPendingInventoryChanges();
        var currentHash = ComputeListHash();
        var materialCache = GetMaterialCache();
        if (materialCache != null && materialCache.Hash == currentHash)
        {
            return;
        }

        var generation = Interlocked.Increment(ref _materialGenerationVersion);

        if (TryCacheActiveExecutionPlan(currentHash))
        {
            _materialsCancellationSource?.Cancel();
            _materialsCancellationSource?.Dispose();
            _materialsCancellationSource = null;
            _materialsGenerationTask = null;
            _isGeneratingMaterials = false;
            return;
        }

        var planningSnapshot = CreatePlanningSnapshot();

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
                if (token.IsCancellationRequested)
                {
                    return;
                }

                var materialCacheSnapshot = BuildMaterialCacheSnapshot(planningSnapshot, hash);
                TryPublishMaterialCache(materialCacheSnapshot, generation, token);
            }
            catch (Exception ex)
            {
                GatherBuddy.Log.Error($"Error generating materials: {ex.Message}");
            }
            finally
            {
                if (generation == Volatile.Read(ref _materialGenerationVersion))
                {
                    _isGeneratingMaterials = false;
                }
            }
        }, token);
    }

    private List<CraftingListItem> GetSortedQueue()
    {
        ProcessPendingInventoryChanges();
        var queueCache = GetQueueCache();
        if (queueCache != null)
        {
            return queueCache.SortedQueue;
        }

        return [];
    }


    private static List<CraftingListItem> BuildDisplayQueue(CraftingListPlan plan)
        => CraftingListQueueBuilder.CreateGroupedQueue(plan);

    private RecipeCraftSettings? GetEffectiveCraftSettings(uint recipeId, bool isOriginalRecipe)
    {
        var planningList = GetPlanningList();
        var sourceSettings = isOriginalRecipe
            ? planningList.Recipes.Find(r => r.RecipeId == recipeId)?.CraftSettings
            : planningList.PrecraftCraftSettings.GetValueOrDefault(recipeId);
        var recipe = RecipeManager.GetRecipe(recipeId);
        if (!recipe.HasValue)
        {
            return sourceSettings?.Clone();
        }

        var forcePreferNQ = !recipe.Value.CanQuickSynth && planningList.ShouldForcePreferNQ(isOriginalRecipe);
        return CraftingQualityPolicyResolver.BuildEffectiveSettings(recipe.Value, sourceSettings, planningList.UseAllHQ, forcePreferNQ);
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
        {
            return true;
        }

        return FFXIVClientStructs.FFXIV.Client.Game.QuestManager.IsRecipeComplete(recipe.RowId);
    }

    internal Dictionary<uint, int> GetCachedMaterials()
    {
        ProcessPendingInventoryChanges();
        var materialCache = EnsureMaterialCache(ComputeListHash());
        return materialCache.Materials;
    }

    internal Dictionary<uint, int> GetCachedPrecraftMaterials()
    {
        ProcessPendingInventoryChanges();
        var materialCache = EnsureMaterialCache(ComputeListHash());
        return materialCache.PrecraftMaterials;
    }

    internal Dictionary<uint, int> GetDisplayMaterials()
    {
        ProcessPendingInventoryChanges();
        var materialCache = EnsureMaterialCache(ComputeListHash());
        return materialCache.DisplayMaterials;
    }

    internal Dictionary<uint, int> GetDisplayPrecraftMaterials()
    {
        ProcessPendingInventoryChanges();
        var materialCache = EnsureMaterialCache(ComputeListHash());
        return materialCache.DisplayPrecraftMaterials;
    }

    internal IReadOnlyDictionary<uint, IngredientQualityDemand> GetCachedIngredientDemands()
    {
        ProcessPendingInventoryChanges();
        var materialCache = EnsureMaterialCache(ComputeListHash());
        return materialCache.IngredientDemands;
    }

    internal IReadOnlyDictionary<uint, IngredientQualityDemand> GetDisplayIngredientDemands()
    {
        ProcessPendingInventoryChanges();
        var materialCache = EnsureMaterialCache(ComputeListHash());
        return materialCache.DisplayIngredientDemands;
    }

    private static string GetConsumableSummary(CraftingListConsumableSettings settings)
    {
        var parts = new List<string>();

        if (settings.FoodItemId.HasValue)
        {
            parts.Add($"Food: {GetItemLabel(settings.FoodItemId.Value, settings.FoodHQ)}");
        }

        if (settings.MedicineItemId.HasValue)
        {
            parts.Add($"Medicine: {GetItemLabel(settings.MedicineItemId.Value, settings.MedicineHQ)}");
        }

        if (settings.ManualItemId.HasValue)
        {
            parts.Add($"Manual: {GetItemLabel(settings.ManualItemId.Value, false)}");
        }

        if (settings.SquadronManualItemId.HasValue)
        {
            parts.Add($"Squadron: {GetItemLabel(settings.SquadronManualItemId.Value, false)}");
        }

        return parts.Count > 0 ? string.Join(" | ", parts) : "None";
    }

    private static string GetItemLabel(uint itemId, bool hq)
    {
        var itemSheet = Dalamud.GameData.GetExcelSheet<Item>();
        if (itemSheet != null && itemSheet.TryGetRow(itemId, out var item))
        {
            return item.Name.ExtractText() + (hq ? " HQ" : "");
        }

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
            {
                return cachedCounts;
            }
        }

        try
        {
            var inventory = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
            if (inventory == null)
            {
                return (0, 0);
            }

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

    internal static int GetRetainerCount(uint itemId)
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
        {
            return RetainerItemSnapshot.Empty;
        }

        var uniqueItemIds = new HashSet<uint>();
        foreach (var id in itemIds)
        {
            if (id > 0)
            {
                uniqueItemIds.Add(id);
            }
        }

        var snapshotItemIds = new uint[uniqueItemIds.Count];
        uniqueItemIds.CopyTo(snapshotItemIds);
        Array.Sort(snapshotItemIds);

        if (snapshotItemIds.Length == 0)
        {
            return RetainerItemSnapshot.Empty;
        }

        if (!forceRefresh && _cachedRetainerSnapshotItemIds.AsSpan().SequenceEqual(snapshotItemIds))
        {
            if (_cachedRetainerSnapshot.IsComplete)
            {
                return _cachedRetainerSnapshot;
            }

            if ((DateTime.Now - _cachedRetainerSnapshotAt).TotalSeconds < RetainerSnapshotRetryIntervalSeconds)
            {
                return _cachedRetainerSnapshot;
            }
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
        var materialCache = GetMaterialCache();
        var demand = materialCache != null && materialCache.CraftMaterialDemands.TryGetValue(itemId, out var craftDemand)
            ? craftDemand
            : GetIngredientDemand(itemId);
        return GetQualityAwareAvailableCount(itemId, demand, retNQ, retHQ, countRetainersTowardNeed);
    }

    internal int GetDisplayMaterialAvailableCount(uint itemId, int retNQ, int retHQ, bool countRetainersTowardNeed)
        => GetQualityAwareAvailableCount(itemId, GetDisplayIngredientDemand(itemId), retNQ, retHQ, countRetainersTowardNeed);

    internal int GetDisplayCraftMaterialAvailableCount(uint itemId, int retNQ, int retHQ, bool countRetainersTowardNeed)
    {
        var materialCache = GetMaterialCache();
        var demand = materialCache != null && materialCache.DisplayCraftMaterialDemands.TryGetValue(itemId, out var craftDemand)
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
        {
            return availableNQ + availableHQ;
        }

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
        {
            return false;
        }

        var (nqCount, hqCount) = GetInventorySplitCounts(recipe.ItemResult.RowId);
        return demand.ConsumeSplit(nqCount, hqCount, out _, out _).Total == 0;
    }

    private static void ProcessPrecraftWithDependencies(CraftingListItem recipeItem, List<CraftingListItem> allRecipes, HashSet<uint> processed, List<CraftingListItem> result)
    {
        if (processed.Contains(recipeItem.RecipeId))
        {
            return;
        }

        var recipe = RecipeManager.GetRecipe(recipeItem.RecipeId);
        if (recipe == null)
        {
            return;
        }

        var ingredients = RecipeManager.GetIngredients(recipe.Value);
        foreach (var (itemId, _) in ingredients)
        {
            var depRecipe = RecipeManager.GetRecipeForItem(itemId);
            if (depRecipe.HasValue)
            {
                var depItem = allRecipes.Find(r => r.RecipeId == depRecipe.Value.RowId && !r.IsOriginalRecipe);
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
                || settings.MacroMode == MacroOverrideMode.Inherit
                    && (!string.IsNullOrEmpty(settings.SelectedMacroId) || settings.SolverOverride != SolverOverrideMode.Default));
        if (isSpecific)
        {
            return settings?.SolverOverride == SolverOverrideMode.Default ? settings?.SelectedMacroId : null;
        }

        var defaultSolverOverride = isPrecraft ? planningList.DefaultPrecraftSolverOverride : planningList.DefaultFinalSolverOverride;
        if (defaultSolverOverride != SolverOverrideMode.Default)
        {
            return null;
        }

        return isPrecraft ? planningList.DefaultPrecraftMacroId : planningList.DefaultFinalMacroId;
    }

    private (int hardFails, int warnings) CountValidationIssues()
    {
        ProcessPendingInventoryChanges();

        var currentHash = ComputeListHash();
        if (_cachedValidationIssueCountsValid && _cachedValidationIssueCountsHash == currentHash)
        {
            return _cachedValidationIssueCounts;
        }

        var hardFails = 0;
        var warnings = 0;

        foreach (var row in GetRecipeDisplayRows())
        {
            AccumulateValidationIssue(row.Validation, ref hardFails, ref warnings);
        }

        if (GetQueueCache() != null)
        {
            EnsureQueueDisplayRows(GetPlanningList(), GetActiveExecutionPlan());
            if (_cachedQueueDisplayRows != null)
            {
                foreach (var row in _cachedQueueDisplayRows)
                {
                    if (row.IsOriginalRecipe)
                    {
                        continue;
                    }

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
        {
            return;
        }

        if (validation.Failure is MacroValidationFailure.CPExhausted or MacroValidationFailure.DurabilityFailed)
        {
            hardFails++;
        }
        else
        {
            warnings++;
        }
    }

    private static string GetCraftingJobName(uint craftTypeId)
    {
        var classJobSheet = Dalamud.GameData.GetExcelSheet<ClassJob>();
        if (classJobSheet != null)
        {
            var classJobId = craftTypeId + 8;
            var classJob = classJobSheet.GetRow(classJobId);
            if (classJob.RowId > 0)
            {
                return classJob.Abbreviation.ExtractText();
            }
        }
        return "Unknown";
    }
}
