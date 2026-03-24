using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.GamePad;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using Dalamud.Interface.Colors;
using FFXIVClientStructs.FFXIV.Client.Game;
using ElliLib;
using GatherBuddy.Crafting;
using GatherBuddy.Plugin;
using GatherBuddy.Vulcan;
using Lumina.Excel.Sheets;
using ElliLib.Raii;
using ImRaii = ElliLib.Raii.ImRaii;

namespace GatherBuddy.Gui;

public partial class VulcanWindow : Window, IDisposable
{
    // Shared state
    private CraftingListDefinition? _editingList  = null;
    private CraftingListDefinition? _previewList  = null;
    private CraftingListEditor?     _listEditor   = null;
    private bool                    _deferEditorDraw = false;

    private bool _isMinimized = false;
    private bool _wasFocusedLastFrame = false;
    
    // TeamCraft import state
    private bool _showTeamCraftImport = false;
    private string _teamCraftListName = string.Empty;
    private string _teamCraftFinalItems = string.Empty;
    
    // Debug tab state
    private uint _debugSelectedJobId = 8;
    private string? _debugLastTestResult;
    private string _repairNPCSearchInput = "";

    public CraftingListDefinition? CurrentCraftingList
        => _editingList;

    public VulcanWindow() : base("Vulcan - Crafting###VulcanWindow")
    {
        Flags |= ImGuiWindowFlags.NoScrollbar;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(500, 300),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        
        CraftingGameInterop.CraftFinished += OnCraftFinished;
    }
    
    private void OnCraftFinished(Recipe? recipe, bool cancelled)
    {
        if (!cancelled && recipe != null)
        {
            _craftedStatusDirty = true;
        }
    }
    
    private void MinimizeWindow()
    {
        _isMinimized = true;
        IsOpen = false;
    }
    
    public void RestoreWindow()
    {
        _isMinimized = false;
        IsOpen = true;
    }

    public void OpenToList(string argument)
    {
        CraftingListDefinition? list;
        if (int.TryParse(argument, out var listId))
            list = GatherBuddy.CraftingListManager.GetListByID(listId);
        else
            list = GatherBuddy.CraftingListManager.GetListByName(argument);

        if (list == null)
        {
            GatherBuddy.Log.Warning($"[VulcanWindow] OpenToList: No list found matching '{argument}'");
            _isMinimized = false;
            IsOpen = true;
            return;
        }

        _isMinimized = false;
        IsOpen = true;
        _editingList = list;
        _listEditor = new CraftingListEditor(list);
        _listEditor.OnStartCrafting = (l) => { StartCraftingList(l); MinimizeWindow(); };
        GatherBuddy.CraftingMaterialsWindow?.SetEditor(_listEditor);
        _deferEditorDraw = true;
    }

    public override void PreDraw()
    {
        if (!IsOpen)
            return;
    }

    public override void Draw()
    {
        GatherBuddy.ControllerSupport?.TabNavigation.Update(Dalamud.GamepadState, 7);
        
        // Track window focus for controller input blocking
        var isFocused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
        if (isFocused)
        {
            GatherBuddy.ControllerSupport?.UpdateFocusedWindow("Vulcan - Crafting###VulcanWindow");
            _wasFocusedLastFrame = true;
        }
        else if (_wasFocusedLastFrame)
        {
            // We just lost focus, clear it
            GatherBuddy.ControllerSupport?.UpdateFocusedWindow(null);
            _wasFocusedLastFrame = false;
        }
        
        ImGui.Text("Crafting System");
        ImGui.Separator();

        using (var tab = ImRaii.TabBar("VulcanTabs###VulcanTabs", ImGuiTabBarFlags.None))
        {
            if (tab)
            {
                DrawCraftingListsTab();
                DrawCraftingTab();
                DrawMacrosTab();
                DrawStandardSolverConfigTab();
                DrawSolutionsTab();
                DrawSettingsTab();
                DrawDebugTab();
            }
        }
        
        _craftSettingsPopup.Draw();
        
        GatherBuddy.ControllerSupport?.UpdateEndOfFrame();
    }

    private void DrawCraftingListsTab()
    {
        if (GatherBuddy.ControllerSupport != null)
        {
            using var tabItem = GatherBuddy.ControllerSupport.TabNavigation.TabItem("Crafting Lists##craftingListsTab", 0, 7);
            if (!tabItem)
                return;
            DrawCraftingListsTabContent();
        }
        else
        {
            using var tabItem = ImRaii.TabItem("Crafting Lists##craftingListsTab");
            if (!tabItem)
                return;
            DrawCraftingListsTabContent();
        }
    }
    
    private void DrawCraftingListsTabContent()
    {

        if (_editingList != null && _listEditor != null)
        {
            if (_deferEditorDraw)
            {
                _deferEditorDraw = false;
                ImGui.Text("Loading...");
                return;
            }
            
            var refreshedList = GatherBuddy.CraftingListManager.GetListByID(_editingList.ID);
            if (refreshedList == null)
            {
                _editingList = null;
                GatherBuddy.CraftingMaterialsWindow?.SetEditor(null);
                _listEditor = null;
                DrawListManager();
                return;
            }

            _editingList = refreshedList;

            if (ImGui.SmallButton("\u2190 Lists##backToLists"))
            {
                _editingList = null;
                _listEditor  = null;
                GatherBuddy.CraftingMaterialsWindow?.SetEditor(null);
                return;
            }

            ImGui.Spacing();
            ImGui.TextColored(ImGuiColors.ParsedGold, _editingList.Name);
            ImGui.TextColored(ImGuiColors.DalamudGrey3, "Crafting List");
            ImGui.Separator();
            ImGui.Spacing();

            if (_listEditor != null)
                _listEditor.Draw();
        }
        else
        {
            DrawListManager();
        }
    }

    private void DrawListManager()
    {
        ImGui.TextColored(ImGuiColors.DalamudYellow, "Crafting Lists");
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button("Create New List", new Vector2(130, 0)))
            ImGui.OpenPopup("CreateListPopup");
        ImGui.SameLine();
        if (ImGui.Button("TeamCraft Import", new Vector2(130, 0)))
            _showTeamCraftImport = true;
        ImGui.SameLine();
        if (ImGui.Button("Import List", new Vector2(130, 0)))
        {
            _importListText  = string.Empty;
            _importListError = null;
            ImGui.OpenPopup("ImportListPopup");
        }

        ImGui.Spacing();

        var avail  = ImGui.GetContentRegionAvail();
        var leftW  = 220f;
        var rightW = avail.X - leftW - ImGui.GetStyle().ItemSpacing.X;

        using (ImRaii.PushColor(ImGuiCol.ChildBg, new Vector4(0.08f, 0.08f, 0.10f, 1.00f)))
        {
            ImGui.BeginChild("##listSelectorPanel", new Vector2(leftW, avail.Y), true);
            DrawListSelectorPanel();
            ImGui.EndChild();
        }

        ImGui.SameLine();

        using (ImRaii.PushColor(ImGuiCol.ChildBg, new Vector4(0.08f, 0.08f, 0.10f, 1.00f)))
        {
            ImGui.BeginChild("##listPreviewPanel", new Vector2(rightW, avail.Y), true);
            DrawListPreviewPanel();
            ImGui.EndChild();
        }

        DrawCreateListPopup();
        DrawImportListPopup();
        DrawTeamCraftImportWindow();
    }

    private void DrawListSelectorPanel()
    {
        if (GatherBuddy.CraftingListManager.Lists.Count == 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(ImGuiColors.DalamudGrey, "No lists yet.");
            ImGui.TextColored(ImGuiColors.DalamudGrey, "Click 'Create New List' to get started.");
            return;
        }

        var lists = GatherBuddy.CraftingListManager.Lists.ToList();
        foreach (var list in lists)
        {
            var isHighlighted = _previewList?.ID == list.ID;
            if (isHighlighted)
                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.ParsedGold);

            if (ImGui.Selectable($"{list.Name}##list_{list.ID}", isHighlighted))
            {
                _editingList = list;
                _listEditor  = new CraftingListEditor(list);
                _listEditor.OnStartCrafting = (l) => { StartCraftingList(l); MinimizeWindow(); };
                GatherBuddy.CraftingMaterialsWindow?.SetEditor(_listEditor);
                _deferEditorDraw = true;
            }

            if (isHighlighted)
                ImGui.PopStyleColor();

            if (ImGui.IsItemHovered())
                _previewList = list;

            var isPopupOpen = GatherBuddy.ControllerSupport != null
                ? GatherBuddy.ControllerSupport.ContextMenu.BeginPopupContextItemWithGamepad($"ListContextMenu_{list.ID}", Dalamud.GamepadState)
                : ImGui.BeginPopupContextItem($"ListContextMenu_{list.ID}");

            if (isPopupOpen)
            {
                if (ImGui.Selectable("Edit"))
                {
                    _editingList = list;
                    _listEditor  = new CraftingListEditor(list);
                    _listEditor.OnStartCrafting = (l) => { StartCraftingList(l); MinimizeWindow(); };
                    GatherBuddy.CraftingMaterialsWindow?.SetEditor(_listEditor);
                    _deferEditorDraw = true;
                }
                if (ImGui.Selectable("Start"))
                    StartCraftingList(list);
                if (ImGui.Selectable("Export to Clipboard"))
                {
                    var exported = GatherBuddy.CraftingListManager.ExportList(list.ID);
                    if (exported != null)
                    {
                        ImGui.SetClipboardText(exported);
                        GatherBuddy.Log.Information($"[VulcanWindow] Exported list '{list.Name}' to clipboard");
                    }
                }
                ImGui.Separator();
                if (ImGui.Selectable("Delete"))
                {
                    if (_previewList?.ID == list.ID)
                        _previewList = null;
                    GatherBuddy.CraftingListManager.DeleteList(list.ID);
                }
                ImGui.EndPopup();
            }
        }
    }

    private void DrawListPreviewPanel()
    {
        if (_previewList == null)
        {
            var h = ImGui.GetContentRegionAvail().Y;
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + h / 2f - ImGui.GetTextLineHeight());
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 8);
            ImGui.TextColored(ImGuiColors.DalamudGrey, "Hover over a list to preview it.");
            return;
        }

        var list = GatherBuddy.CraftingListManager.GetListByID(_previewList.ID);
        if (list == null)
        {
            _previewList = null;
            return;
        }
        _previewList = list;

        ImGui.Spacing();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 8);
        ImGui.TextColored(ImGuiColors.ParsedGold, list.Name);

        if (!string.IsNullOrWhiteSpace(list.Description))
        {
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 8);
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey3))
                ImGui.TextWrapped(list.Description);
        }

        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 8);
        var recipeWord = list.Recipes.Count == 1 ? "recipe" : "recipes";
        ImGui.TextColored(ImGuiColors.DalamudGrey3,
            $"{list.Recipes.Count} {recipeWord}  \u00b7  Created {list.CreatedAt.ToLocalTime():yyyy-MM-dd}");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var style   = ImGui.GetStyle();
        var buttonH = 22f * 2 + style.ItemSpacing.Y * 3 + 4f;
        var listH   = Math.Max(ImGui.GetContentRegionAvail().Y - buttonH, 40f);

        ImGui.BeginChild("##previewRecipeList", new Vector2(-1, listH), false);

        if (list.Recipes.Count == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "No recipes in this list.");
        }
        else
        {
            var iconSz = new Vector2(22f, 22f);
            foreach (var item in list.Recipes)
            {
                var recipe = RecipeManager.GetRecipe(item.RecipeId);
                if (recipe == null) continue;

                var resultItem = recipe.Value.ItemResult.Value;
                var icon = Icons.DefaultStorage.TextureProvider
                    .GetFromGameIcon(new GameIconLookup(resultItem.Icon));
                if (icon.TryGetWrap(out var wrap, out _))
                    ImGui.Image(wrap.Handle, iconSz);
                else
                    ImGui.Dummy(iconSz);

                ImGui.SameLine(0, 6);
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (iconSz.Y - ImGui.GetTextLineHeight()) / 2f);
                ImGui.Text(resultItem.Name.ExtractText());
                ImGui.SameLine();
                ImGui.TextColored(ImGuiColors.DalamudGrey3,
                    $"x{item.Quantity}  ({JobNames[recipe.Value.CraftType.RowId]})");
            }
        }

        ImGui.EndChild();

        ImGui.Separator();
        ImGui.Spacing();

        var halfW = (ImGui.GetContentRegionAvail().X - style.ItemSpacing.X) / 2f;
        if (ImGui.Button("Edit List##previewEdit", new Vector2(halfW, 22)))
        {
            _editingList = list;
            _listEditor  = new CraftingListEditor(list);
            _listEditor.OnStartCrafting = (l) => { StartCraftingList(l); MinimizeWindow(); };
            GatherBuddy.CraftingMaterialsWindow?.SetEditor(_listEditor);
            _deferEditorDraw = true;
        }
        ImGui.SameLine();
        if (IPCSubscriber.IsReady("Artisan"))
        {
            ImGuiUtil.DrawDisabledButton("Artisan Detected##previewStart", new Vector2(-1, 22),
                "Artisan plugin is loaded. Please unload Artisan to use Vulcan's crafting system.", true);
        }
        else if (ImGui.Button("Start Crafting##previewStart", new Vector2(-1, 22)))
        {
            StartCraftingList(list);
            MinimizeWindow();
        }

        if (ImGui.Button("Export##previewExport", new Vector2(halfW, 22)))
        {
            var exported = GatherBuddy.CraftingListManager.ExportList(list.ID);
            if (exported != null)
            {
                ImGui.SetClipboardText(exported);
                GatherBuddy.Log.Information($"[VulcanWindow] Exported list '{list.Name}' to clipboard");
            }
        }
        ImGui.SameLine();
        using (ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.45f, 0.12f, 0.12f, 1f)))
        {
            if (ImGui.Button("Delete##previewDelete", new Vector2(-1, 22)))
            {
                GatherBuddy.CraftingListManager.DeleteList(list.ID);
                _previewList = null;
            }
        }
    }

    private string _newListName    = string.Empty;
    private string _importListText  = string.Empty;
    private string? _importListError = null;

    private void DrawImportListPopup()
    {
        ImGui.SetNextWindowSize(new Vector2(540, 260), ImGuiCond.FirstUseEver);
        if (!ImGui.BeginPopupModal("ImportListPopup", ImGuiWindowFlags.None))
            return;

        ImGui.TextWrapped("Paste an exported list string below and click Import.");
        ImGui.Spacing();
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextMultiline("##importListText", ref _importListText, 65536, new Vector2(-1, 120));

        if (_importListError != null)
        {
            ImGui.Spacing();
            ImGui.TextColored(ImGuiColors.DalamudRed, _importListError);
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        using (ElliLib.Raii.ImRaii.Disabled(string.IsNullOrWhiteSpace(_importListText)))
        {
            if (ImGui.Button("Import", new Vector2(120, 0)))
            {
                var (imported, error) = GatherBuddy.CraftingListManager.ImportList(_importListText);
                if (imported != null)
                {
                    _editingList = imported;
                    _listEditor  = new CraftingListEditor(imported);
                    _listEditor.OnStartCrafting = (l) => { StartCraftingList(l); MinimizeWindow(); };
                    GatherBuddy.CraftingMaterialsWindow?.SetEditor(_listEditor);
                    _deferEditorDraw = true;
                    _importListText  = string.Empty;
                    _importListError = null;
                    ImGui.CloseCurrentPopup();
                }
                else
                {
                    _importListError = error;
                }
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(100, 0)))
        {
            _importListText  = string.Empty;
            _importListError = null;
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private void DrawCreateListPopup()
    {
        if (ImGui.BeginPopupModal("CreateListPopup", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.Text("Enter list name:");
            ImGui.InputText("##newListName", ref _newListName, 256);

            if (!string.IsNullOrWhiteSpace(_newListName) && !GatherBuddy.CraftingListManager.IsNameUnique(_newListName))
            {
                ImGui.TextColored(new Vector4(1, 0.7f, 0, 1), "A list with this name already exists.");
                ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1), "It will be renamed automatically.");
            }

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            if (ImGui.Button("Create", new Vector2(100, 0)) && !string.IsNullOrWhiteSpace(_newListName))
            {
                var newList = GatherBuddy.CraftingListManager.CreateNewList(_newListName);
                _editingList = newList;
                _listEditor = new CraftingListEditor(newList);
                _listEditor.OnStartCrafting = (l) => { StartCraftingList(l); MinimizeWindow(); };
                GatherBuddy.CraftingMaterialsWindow?.SetEditor(_listEditor);
                _deferEditorDraw = true;
                _newListName = string.Empty;
                ImGui.CloseCurrentPopup();
            }

            ImGui.SameLine();
            if (ImGui.Button("Cancel", new Vector2(100, 0)))
            {
                _newListName = string.Empty;
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }
    }

    public void StartCraftingList(CraftingListDefinition list)
    {
        if (list.Recipes.Count == 0)
        {
            GatherBuddy.Log.Warning("[VulcanWindow] Cannot start empty list");
            return;
        }

        var craftingQueue = new CraftingListQueue();
        foreach (var item in list.Recipes)
        {
            if (!item.Options.Skipping)
            {
                craftingQueue.AddRecipeWithPrecrafts(item.RecipeId, item.Quantity, list.SkipIfEnough);
            }
        }

        craftingQueue.BuildExpandedList();
        var sortedRecipes = GetRecipesInDependencyOrder(craftingQueue.Recipes, craftingQueue.OriginalRecipes);
        
        var expandedQueue = new List<CraftingListItem>();
        foreach (var recipeItem in sortedRecipes)
        {
            var originalItem = list.Recipes.FirstOrDefault(r => r.RecipeId == recipeItem.RecipeId);
            var recipeOptions = list.GetRecipeOptions(recipeItem.RecipeId);
            var isOriginal = originalItem != null;
            
            for (int i = 0; i < recipeItem.Quantity; i++)
            {
                var queueItem = new CraftingListItem(recipeItem.RecipeId, 1);
                
                queueItem.Options.NQOnly = recipeOptions.NQOnly;
                if (list.QuickSynthAll)
                {
                    var recipeData = RecipeManager.GetRecipe(recipeItem.RecipeId);
                    if (recipeData?.CanQuickSynth == true)
                        queueItem.Options.NQOnly = true;
                }
                queueItem.Options.Skipping = recipeOptions.Skipping;
                queueItem.IsOriginalRecipe = isOriginal;
                
                if (originalItem != null)
                {
                    queueItem.ConsumableOverrides = originalItem.ConsumableOverrides.Clone();
                }
                var craftSettings = originalItem?.CraftSettings ?? list.PrecraftCraftSettings.GetValueOrDefault(recipeItem.RecipeId);
                var effectiveMacroId = ResolveEffectiveMacroId(craftSettings, !isOriginal, list);
                if (craftSettings != null)
                {
                    queueItem.CraftSettings = new RecipeCraftSettings
                    {
                        FoodMode = craftSettings.FoodMode,
                        FoodItemId = craftSettings.FoodItemId,
                        FoodHQ = craftSettings.FoodHQ,
                        MedicineMode = craftSettings.MedicineMode,
                        MedicineItemId = craftSettings.MedicineItemId,
                        MedicineHQ = craftSettings.MedicineHQ,
                        ManualMode = craftSettings.ManualMode,
                        ManualItemId = craftSettings.ManualItemId,
                        SquadronManualMode = craftSettings.SquadronManualMode,
                        SquadronManualItemId = craftSettings.SquadronManualItemId,
                        IngredientPreferences = new Dictionary<uint, int>(craftSettings.IngredientPreferences),
                        UseAllNQ = craftSettings.UseAllNQ,
                        SelectedMacroId = effectiveMacroId,
                        SolverOverride = craftSettings.SolverOverride,
                    };
                }
                else if (effectiveMacroId != null)
                {
                    queueItem.CraftSettings = new RecipeCraftSettings { SelectedMacroId = effectiveMacroId };
                }
                if (originalItem != null)
                {
                    var topLevelPrefs = originalItem.IngredientPreferences;
                    var craftSettingsPrefs = originalItem.CraftSettings?.IngredientPreferences;
                    var effectivePrefs = topLevelPrefs.Count > 0 ? topLevelPrefs : craftSettingsPrefs;
                    if (effectivePrefs != null && effectivePrefs.Count > 0)
                        queueItem.IngredientPreferences = new Dictionary<uint, int>(effectivePrefs);
                }
                expandedQueue.Add(queueItem);
            }
        }
        
        var materials = list.ListMaterials();
        var retainerPrecraftItems = new System.Collections.Generic.Dictionary<uint, int>();

        if (list.RetainerRestock && AllaganTools.Enabled)
        {
            var (corrected, precraftItems) = Crafting.RetainerTaskExecutor.PlanRetainerRestock(list, expandedQueue);
            materials             = corrected;
            retainerPrecraftItems = precraftItems;
        }

        GatherBuddy.Log.Information($"[VulcanWindow] Starting crafting list '{list.Name}' with {expandedQueue.Count} crafts from {sortedRecipes.Count} recipes");
        CraftingGatherBridge.StartQueueCraftAndGather(expandedQueue, materials, list.Consumables, list.SkipIfEnough, list.RetainerRestock, retainerPrecraftItems);
    }

    private List<CraftingListItem> GetRecipesInDependencyOrder(List<CraftingListItem> recipes, List<CraftingListItem> originalRecipesList)
    {
        var originalRecipes = new HashSet<uint>();
        
        foreach (var item in originalRecipesList)
        {
            originalRecipes.Add(item.RecipeId);
        }
        
        var precrafts = new List<CraftingListItem>();
        var finalProducts = new List<CraftingListItem>();
        
        foreach (var recipe in recipes)
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
        
        var result = new List<CraftingListItem>();
        var processed = new HashSet<uint>();
        
        var precraftsByJob = precrafts
            .GroupBy(r => RecipeManager.GetRecipe(r.RecipeId)?.CraftType.RowId ?? uint.MaxValue)
            .OrderBy(g => g.Key);
        
        foreach (var jobGroup in precraftsByJob)
        {
            var jobRecipes = jobGroup.ToList();
            foreach (var recipeItem in jobRecipes)
            {
                ProcessRecipeWithDependencies(recipeItem, recipes, processed, result);
            }
        }
        
        var sortedFinalProducts = finalProducts
            .GroupBy(r => RecipeManager.GetRecipe(r.RecipeId)?.CraftType.RowId ?? uint.MaxValue)
            .OrderBy(g => g.Key)
            .SelectMany(g => g)
            .ToList();
        
        foreach (var recipeItem in sortedFinalProducts)
        {
            if (!processed.Contains(recipeItem.RecipeId))
            {
                processed.Add(recipeItem.RecipeId);
                result.Add(recipeItem);
            }
        }
        
        return result;
    }

    private static string? ResolveEffectiveMacroId(RecipeCraftSettings? settings, bool isPrecraft, CraftingListDefinition list)
    {
        var isSpecific = settings?.MacroMode == MacroOverrideMode.Specific
            || (settings?.MacroMode == MacroOverrideMode.Inherit && !string.IsNullOrEmpty(settings?.SelectedMacroId));
        if (isSpecific)
            return settings?.SelectedMacroId;
        return isPrecraft ? list.DefaultPrecraftMacroId : list.DefaultFinalMacroId;
    }

    private void ProcessRecipeWithDependencies(CraftingListItem recipeItem, List<CraftingListItem> allRecipes, HashSet<uint> processed, List<CraftingListItem> result)
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
                    ProcessRecipeWithDependencies(depItem, allRecipes, processed, result);
                }
            }
        }
        
        processed.Add(recipeItem.RecipeId);
        result.Add(recipeItem);
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

    private unsafe int GetInventoryCount(uint itemId)
    {
        try
        {
            var inventory = InventoryManager.Instance();
            if (inventory == null)
                return 0;
            var total = 0;
            var baseItemId = itemId >= 1_000_000 ? itemId - 1_000_000 : itemId;
            var hqItemId = baseItemId + 1_000_000;
            var inventories = new InventoryType[]
            {
                InventoryType.Inventory1, InventoryType.Inventory2,
                InventoryType.Inventory3, InventoryType.Inventory4,
                InventoryType.Crystals
            };

            foreach (var invType in inventories)
            {
                var container = inventory->GetInventoryContainer(invType);
                if (container == null)
                    continue;

                for (var i = 0; i < container->Size; i++)
                {
                    var item = container->GetInventorySlot(i);
                    if (item == null || item->ItemId == 0)
                        continue;

                    if (item->ItemId == baseItemId || item->ItemId == hqItemId)
                        total += (int)item->Quantity;
                }
            }

            return total;
        }
        catch
        {
            return 0;
        }
    }

    private void DrawStandardSolverConfigTab()
    {
        IDisposable tabItem;
        bool tabOpen;
        
        if (GatherBuddy.ControllerSupport != null)
        {
            var handle = GatherBuddy.ControllerSupport.TabNavigation.TabItem("Standard Solver##standardSolverTab", 3, 7);
            tabItem = handle;
            tabOpen = handle;
        }
        else
        {
            var handle = ImRaii.TabItem("Standard Solver##standardSolverTab");
            tabItem = handle;
            tabOpen = handle.Success;
        }
        
        using (tabItem)
        {
            if (!tabOpen)
                return;

        var config = GatherBuddy.Config.StandardSolverConfig;

        ImGui.Text("Standard Solver Configuration");
        ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1), "Configure the dynamic Standard Solver behavior");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.BeginGroup();
        ImGui.Text("Tricks of the Trade Settings");
        ImGui.Spacing();
        
        var useTricksGood = config.UseTricksGood;
        if (ImGui.Checkbox("Use Tricks on Good Condition", ref useTricksGood))
        {
            config.UseTricksGood = useTricksGood;
            GatherBuddy.Config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Use Tricks of the Trade when condition is Good");

        var useTricksExcellent = config.UseTricksExcellent;
        if (ImGui.Checkbox("Use Tricks on Excellent Condition", ref useTricksExcellent))
        {
            config.UseTricksExcellent = useTricksExcellent;
            GatherBuddy.Config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Use Tricks of the Trade when condition is Excellent");
        ImGui.EndGroup();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.BeginGroup();
        ImGui.Text("Quality Settings");
        ImGui.Spacing();

        var maxPercentage = config.MaxPercentage;
        ImGui.SetNextItemWidth(150);
        if (ImGui.SliderInt("Target HQ %##maxPercentage", ref maxPercentage, 0, 100))
        {
            config.MaxPercentage = maxPercentage;
            GatherBuddy.Config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Target HQ percentage for normal crafts (0-100)");

        var useQualityStarter = config.UseQualityStarter;
        if (ImGui.Checkbox("Use Quality Starter (Reflect)", ref useQualityStarter))
        {
            config.UseQualityStarter = useQualityStarter;
            GatherBuddy.Config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Use Reflect at the start for quality instead of Muscle Memory for progress");

        var maxIQPrepTouch = config.MaxIQPrepTouch;
        ImGui.SetNextItemWidth(150);
        if (ImGui.SliderInt("Max IQ for Prep Touch##maxIQPrepTouch", ref maxIQPrepTouch, 0, 10))
        {
            config.MaxIQPrepTouch = maxIQPrepTouch;
            GatherBuddy.Config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Maximum Inner Quiet stacks before using Preparatory Touch");
        ImGui.EndGroup();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.BeginGroup();
        ImGui.Text("Collectible Settings");
        ImGui.Spacing();

        ImGui.SetNextItemWidth(200);
        var collectibleModes = new[] { "Tier 1 (Min)", "Tier 2 (Mid)", "Tier 3 (Max)" };
        var collectibleMode = Math.Clamp(config.SolverCollectibleMode - 1, 0, collectibleModes.Length - 1);
        if (ImGui.Combo("Collectible Target##collectibleMode", ref collectibleMode, collectibleModes, collectibleModes.Length))
        {
            config.SolverCollectibleMode = collectibleMode + 1;
            GatherBuddy.Config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Which collectible tier to aim for (1=lowest, 3=highest)");
        ImGui.EndGroup();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.BeginGroup();
        ImGui.Text("Specialist Settings");
        ImGui.Spacing();

        var useSpecialist = config.UseSpecialist;
        if (ImGui.Checkbox("Use Specialist Actions", ref useSpecialist))
        {
            config.UseSpecialist = useSpecialist;
            GatherBuddy.Config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Use Careful Observation and Heart & Soul when available");
        ImGui.EndGroup();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.BeginGroup();
        ImGui.Text("Material Miracle Settings");
        ImGui.Spacing();

        var useMaterialMiracle = config.UseMaterialMiracle;
        if (ImGui.Checkbox("Use Material Miracle", ref useMaterialMiracle))
        {
            config.UseMaterialMiracle = useMaterialMiracle;
            GatherBuddy.Config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Use Material Miracle action during crafts");

        if (config.UseMaterialMiracle)
        {
            var minSteps = config.MinimumStepsBeforeMiracle;
            ImGui.SetNextItemWidth(150);
            if (ImGui.SliderInt("Min Steps Before Miracle##minSteps", ref minSteps, 1, 10))
            {
                config.MinimumStepsBeforeMiracle = minSteps;
                GatherBuddy.Config.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Minimum crafting steps before using Material Miracle");

            var materialMiracleMulti = config.MaterialMiracleMulti;
            if (ImGui.Checkbox("Allow Multiple Material Miracles", ref materialMiracleMulti))
            {
                config.MaterialMiracleMulti = materialMiracleMulti;
                GatherBuddy.Config.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Allow using Material Miracle multiple times in a single craft");
        }
        ImGui.EndGroup();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button("Reset to Defaults", new Vector2(200, 0)))
        {
            GatherBuddy.Config.StandardSolverConfig = new Vulcan.StandardSolverConfig();
            GatherBuddy.Config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Reset all Standard Solver settings to their default values");
        }
    }

    private void DrawSettingsTab()
    {
        IDisposable tabItem;
        bool tabOpen;

        if (GatherBuddy.ControllerSupport != null)
        {
            var handle = GatherBuddy.ControllerSupport.TabNavigation.TabItem("Settings##settingsTab", 5, 7);
            tabItem = handle;
            tabOpen = handle;
        }
        else
        {
            var handle = ImRaii.TabItem("Settings##settingsTab");
            tabItem = handle;
            tabOpen = handle.Success;
        }

        using (tabItem)
        {
            if (!tabOpen)
                return;

            var coordinator = GatherBuddy.RaphaelSolveCoordinator;
            var raphaelConfig = GatherBuddy.Config.RaphaelSolverConfig;

            var currentMode = raphaelConfig.SolverMode;
            var modeNames = new[] { "Pure Raphael", "Standard Solver", "Progress Only" };
            var safeModeIndex = Math.Clamp((int)currentMode, 0, modeNames.Length - 1);
            ImGui.SetNextItemWidth(150);
            if (ImGui.BeginCombo("Solver Mode###SolverMode", modeNames[safeModeIndex]))
            {
                if (ImGui.Selectable("Pure Raphael", currentMode == RaphaelSolverMode.PureRaphael))
                {
                    raphaelConfig.SolverMode = RaphaelSolverMode.PureRaphael;
                    GatherBuddy.Config.Save();
                    CraftingGameInterop.ReloadSolvers();
                }
                if (ImGui.IsItemHovered())
                {
                    ImGui.BeginTooltip();
                    ImGui.TextUnformatted("Pure Raphael: Static rotations generated by Raphael solver");
                    ImGui.TextUnformatted("Consistent, optimal results");
                    ImGui.EndTooltip();
                }

                if (ImGui.Selectable("Standard Solver", currentMode == RaphaelSolverMode.StandardSolver))
                {
                    raphaelConfig.SolverMode = RaphaelSolverMode.StandardSolver;
                    GatherBuddy.Config.Save();
                    CraftingGameInterop.ReloadSolvers();
                }
                if (ImGui.IsItemHovered())
                {
                    ImGui.BeginTooltip();
                    ImGui.TextUnformatted("Standard Solver: Dynamic solver adapted from Artisan");
                    ImGui.TextUnformatted("Reacts to conditions, more flexible");
                    ImGui.EndTooltip();
                }

                if (ImGui.Selectable("Progress Only", currentMode == RaphaelSolverMode.ProgressOnly))
                {
                    raphaelConfig.SolverMode = RaphaelSolverMode.ProgressOnly;
                    GatherBuddy.Config.Save();
                    CraftingGameInterop.ReloadSolvers();
                }
                if (ImGui.IsItemHovered())
                {
                    ImGui.BeginTooltip();
                    ImGui.TextUnformatted("Progress Only: Completes crafts without quality actions");
                    ImGui.TextUnformatted("Fastest execution, no quality output");
                    ImGui.EndTooltip();
                }
                ImGui.EndCombo();
            }

            var delay = GatherBuddy.Config.VulcanExecutionDelayMs;
            ImGui.SetNextItemWidth(150);
            if (ImGui.SliderInt("Action Delay (ms)", ref delay, 0, 1000))
            {
                GatherBuddy.Config.VulcanExecutionDelayMs = Math.Clamp(delay, 0, 1000);
                GatherBuddy.Config.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Delay in milliseconds between each crafting action (0 = instant, max 1000ms)");

            DrawVulcanRepairConfig();

            DrawVulcanMateriaConfig();

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            ImGui.Text("Raphael Solver");
            ImGui.Separator();
            ImGui.Spacing();

            ImGui.BeginGroup();
            ImGui.Text("  Max Concurrent: ");
            ImGui.SameLine();
            var maxConcurrent = raphaelConfig.MaxConcurrentRaphaelProcesses;
            ImGui.SetNextItemWidth(100);
            if (ImGui.InputInt("###MaxConcurrent", ref maxConcurrent, 1, 1))
            {
                raphaelConfig.MaxConcurrentRaphaelProcesses = Math.Max(1, maxConcurrent);
                GatherBuddy.Config.Save();
            }

            ImGui.Text("  Solve Timeout (minutes): ");
            ImGui.SameLine();
            var timeoutMinutes = raphaelConfig.RaphaelTimeoutMinutes;
            ImGui.SetNextItemWidth(100);
            if (ImGui.InputInt("###RaphaelTimeout", ref timeoutMinutes, 1, 1))
            {
                raphaelConfig.RaphaelTimeoutMinutes = Math.Max(1, Math.Min(60, timeoutMinutes));
                GatherBuddy.Config.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Timeout in minutes per Raphael solve. If exceeded, the solution is marked as failed and the craft will be skipped.");

            ImGui.Text("  Cache Max Age (days): ");
            ImGui.SameLine();
            var maxAgeDays = raphaelConfig.SolutionCacheMaxAgeDays;
            ImGui.SetNextItemWidth(100);
            if (ImGui.InputInt("###CacheMaxAge", ref maxAgeDays, 1, 10))
            {
                raphaelConfig.SolutionCacheMaxAgeDays = Math.Max(1, Math.Min(365, maxAgeDays));
                GatherBuddy.Config.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Solutions older than this many days are discarded on plugin load.");

            ImGui.Spacing();
            var backloadProgress = raphaelConfig.RaphaelBackloadProgress;
            if (ImGui.Checkbox("  Backload Progress###RaphaelBackloadProgress", ref backloadProgress))
            {
                raphaelConfig.RaphaelBackloadProgress = backloadProgress;
                GatherBuddy.Config.Save();
                coordinator.Clear();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Only use progress-increasing actions at the end of the rotation.\nMay decrease achievable quality. Disable for maximum quality output.\nChanging this clears the solution cache.");

            var allowSpecialist = raphaelConfig.RaphaelAllowSpecialistActions;
            if (ImGui.Checkbox("  Allow Specialist Actions###RaphaelAllowSpecialist", ref allowSpecialist))
            {
                raphaelConfig.RaphaelAllowSpecialistActions = allowSpecialist;
                GatherBuddy.Config.Save();
                coordinator.Clear();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("When disabled (default), Raphael generates non-specialist rotations even if you are a specialist.\nEnable only if you want Raphael to use specialist actions.\nChanging this clears the solution cache.");

            var activeColor = coordinator.ActiveSolves > 0 ? ImGuiColors.HealerGreen : ImGuiColors.DalamudGrey;
            ImGui.TextColored(activeColor, $"  Active Solves: {coordinator.ActiveSolves}/{raphaelConfig.MaxConcurrentRaphaelProcesses}");

            var pendingColor = coordinator.PendingSolves > 0 ? ImGuiColors.DalamudOrange : ImGuiColors.DalamudGrey;
            ImGui.TextColored(pendingColor, $"  Pending: {coordinator.PendingSolves}");

            var cachedColor = coordinator.CachedSolutionCount > 0 ? ImGuiColors.HealerGreen : ImGuiColors.DalamudGrey;
            ImGui.TextColored(cachedColor, $"  Cached Solutions: {coordinator.CachedSolutionCount}");

            if (ImGui.Button("Clear Cache", new Vector2(150, 0)))
            {
                coordinator.Clear();
            }
            ImGui.EndGroup();
        }
    }

    private void DrawDebugTab()
    {
        IDisposable tabItem;
        bool tabOpen;
        
        if (GatherBuddy.ControllerSupport != null)
        {
            var handle = GatherBuddy.ControllerSupport.TabNavigation.TabItem("Debug##debugTab", 6, 7);
            tabItem = handle;
            tabOpen = handle;
        }
        else
        {
            var handle = ImRaii.TabItem("Debug##debugTab");
            tabItem = handle;
            tabOpen = handle.Success;
        }
        
        using (tabItem)
        {
            if (!tabOpen)
                return;

        ImGui.BeginGroup();
        ImGui.Text("Context Menu Settings");
        ImGui.Spacing();

        ImGui.Text("  Max Recent Lists:");
        ImGui.SameLine();
        var maxRecentLists = GatherBuddy.Config.MaxRecentCraftingListsInContextMenu;
        ImGui.SetNextItemWidth(100);
        if (ImGui.InputInt("###MaxRecentLists", ref maxRecentLists, 1, 1))
        {
            GatherBuddy.Config.MaxRecentCraftingListsInContextMenu = Math.Max(1, Math.Min(50, maxRecentLists));
            GatherBuddy.Config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Maximum number of recent crafting lists to show in context menus (1-50)");

        ImGui.EndGroup();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.BeginGroup();
        ImGui.Text("Repair Status");
        ImGui.Spacing();
        ImGui.Text($"  Min Equipped: {Crafting.RepairManager.GetMinEquippedPercent()}%");
        ImGui.Text($"  Can Self Repair: {Crafting.RepairManager.CanRepairAny()}");
        ImGui.Text($"  Repair NPC Nearby: {Crafting.RepairManager.RepairNPCNearby(out _)}");
        if (Crafting.RepairManager.RepairNPCNearby(out _))
        {
            ImGui.Text($"  NPC Repair Price: {Crafting.RepairManager.GetNPCRepairPrice()} gil");
        }
        ImGui.EndGroup();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.BeginGroup();
        ImGui.Text("Materia Extraction Status");
        ImGui.Spacing();
        ImGui.Text($"  Extraction Unlocked: {Crafting.MateriaManager.IsExtractionUnlocked()}");
        ImGui.Text($"  Items Ready: {Crafting.MateriaManager.ReadySpiritbondItemCount()}");
        ImGui.Text($"  Free Slots: {Crafting.MateriaManager.HasFreeInventorySlots()}");
        ImGui.EndGroup();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.BeginGroup();
        ImGui.Text("Gearset Stat Test");
        ImGui.Text("  Select Job:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(150);
        if (ImGui.BeginCombo("###JobSelector", GetDebugJobName(_debugSelectedJobId)))
        {
            var jobs = new[] { (8u, "Carpenter (CRP)"), (9u, "Blacksmith (BSM)"), (10u, "Armorer (ARM)"), (11u, "Goldsmith (GSM)"), (12u, "Leatherworker (LTW)"), (13u, "Weaver (WVR)"), (14u, "Alchemist (ALC)"), (15u, "Culinarian (CUL)") };
            foreach (var (jobId, jobName) in jobs)
            {
                if (ImGui.Selectable(jobName, _debugSelectedJobId == jobId))
                {
                    _debugSelectedJobId = jobId;
                    _debugLastTestResult = null;
                }
            }
            ImGui.EndCombo();
        }

        ImGui.Spacing();
        if (ImGui.Button("Test Stat Read", new Vector2(150, 0)))
        {
            var stats = GearsetStatsReader.ReadGearsetStatsForJob(_debugSelectedJobId);
            if (stats != null)
            {
                _debugLastTestResult = $"Success: Craftsmanship={stats.Craftsmanship}, Control={stats.Control}, CP={stats.CP}, Manipulation={stats.Manipulation}";
            }
            else
            {
                _debugLastTestResult = "Failed: Could not read gearset stats for this job";
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Refresh Gearset", new Vector2(150, 0)))
        {
            GearsetStatsReader.RefreshGearsetFromCurrentEquipped(_debugSelectedJobId);
            _debugLastTestResult = "Gearset refreshed from currently equipped items";
        }

        if (_debugLastTestResult != null)
        {
            ImGui.Spacing();
            ImGui.TextWrapped(_debugLastTestResult);
        }
        ImGui.EndGroup();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawGamepadInputTest();
        }
    }
    
    private void DrawGamepadInputTest()
    {
        ImGui.BeginGroup();
        ImGui.Text("Gamepad Input Test");
        ImGui.Separator();
        ImGui.Spacing();
        
        var gamepad = Dalamud.GamepadState;
        
        ImGui.Text("Left Stick:");
        ImGui.SameLine();
        ImGui.Text($"X: {gamepad.LeftStick.X:F3}, Y: {gamepad.LeftStick.Y:F3}");
        
        ImGui.Text("Right Stick:");
        ImGui.SameLine();
        ImGui.Text($"X: {gamepad.RightStick.X:F3}, Y: {gamepad.RightStick.Y:F3}");
        
        ImGui.Spacing();
        ImGui.Text("D-Pad:");
        ImGui.SameLine();
        var dpad = "None";
        if (gamepad.Pressed(GamepadButtons.DpadUp) > 0) dpad = "Up";
        if (gamepad.Pressed(GamepadButtons.DpadDown) > 0) dpad = "Down";
        if (gamepad.Pressed(GamepadButtons.DpadLeft) > 0) dpad = "Left";
        if (gamepad.Pressed(GamepadButtons.DpadRight) > 0) dpad = "Right";
        ImGui.Text(dpad);
        
        ImGui.Spacing();
        ImGui.Text("Face Buttons:");
        var faceButtons = new List<string>();
        if (gamepad.Pressed(GamepadButtons.South) > 0) faceButtons.Add("A/Cross");
        if (gamepad.Pressed(GamepadButtons.East) > 0) faceButtons.Add("B/Circle");
        if (gamepad.Pressed(GamepadButtons.West) > 0) faceButtons.Add("X/Square");
        if (gamepad.Pressed(GamepadButtons.North) > 0) faceButtons.Add("Y/Triangle");
        ImGui.SameLine();
        ImGui.Text(faceButtons.Count > 0 ? string.Join(", ", faceButtons) : "None");
        
        ImGui.Spacing();
        ImGui.Text("Shoulder Buttons:");
        var shoulderButtons = new List<string>();
        if (gamepad.Pressed(GamepadButtons.L1) > 0) shoulderButtons.Add("L1");
        if (gamepad.Pressed(GamepadButtons.R1) > 0) shoulderButtons.Add("R1");
        if (gamepad.Pressed(GamepadButtons.L2) > 0) shoulderButtons.Add("L2");
        if (gamepad.Pressed(GamepadButtons.R2) > 0) shoulderButtons.Add("R2");
        ImGui.SameLine();
        ImGui.Text(shoulderButtons.Count > 0 ? string.Join(", ", shoulderButtons) : "None");
        
        ImGui.Spacing();
        ImGui.Text("ImGui Navigation State:");
        var io = ImGui.GetIO();
        ImGui.Text($"  NavActive: {io.NavActive}");
        ImGui.Text($"  NavVisible: {io.NavVisible}");
        ImGui.Text($"  ConfigFlags: {io.ConfigFlags}");
        
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        
        var navKeyboardEnabled = (io.ConfigFlags & ImGuiConfigFlags.NavEnableKeyboard) != 0;
        var navGamepadEnabled = (io.ConfigFlags & ImGuiConfigFlags.NavEnableGamepad) != 0;
        
        if (ImGui.Button(navGamepadEnabled ? "Disable Gamepad Nav" : "Enable Gamepad Nav", new Vector2(200, 0)))
        {
            io = ImGui.GetIO();
            if (navGamepadEnabled)
            {
                io.ConfigFlags &= ~ImGuiConfigFlags.NavEnableGamepad;
                GatherBuddy.Log.Information("[VulcanWindow] Disabled ImGui gamepad navigation");
            }
            else
            {
                io.ConfigFlags |= ImGuiConfigFlags.NavEnableGamepad;
                GatherBuddy.Log.Information("[VulcanWindow] Enabled ImGui gamepad navigation");
            }
        }
        
        ImGui.SameLine();
        if (ImGui.Button(navKeyboardEnabled ? "Disable Keyboard Nav" : "Enable Keyboard Nav", new Vector2(200, 0)))
        {
            io = ImGui.GetIO();
            if (navKeyboardEnabled)
            {
                io.ConfigFlags &= ~ImGuiConfigFlags.NavEnableKeyboard;
                GatherBuddy.Log.Information("[VulcanWindow] Disabled ImGui keyboard navigation");
            }
            else
            {
                io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;
                GatherBuddy.Log.Information("[VulcanWindow] Enabled ImGui keyboard navigation");
            }
        }
        
        ImGui.TextColored(new Vector4(1, 1, 0, 1), "Note: Press Tab or use D-pad to start navigating");
        
        ImGui.EndGroup();
    }


    private static string GetDebugJobName(uint jobId) => jobId switch
    {
        8 => "Carpenter (CRP)",
        9 => "Blacksmith (BSM)",
        10 => "Armorer (ARM)",
        11 => "Goldsmith (GSM)",
        12 => "Leatherworker (LTW)",
        13 => "Weaver (WVR)",
        14 => "Alchemist (ALC)",
        15 => "Culinarian (CUL)",
        _ => "Unknown"
    };
    
    private static string GetTerritoryName(uint territoryId)
    {
        var territorySheet = Dalamud.GameData.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>();
        if (territorySheet?.TryGetRow(territoryId, out var territory) == true)
        {
            return territory.PlaceName.ValueNullable?.Name.ExtractText() ?? "Unknown";
        }
        return "Unknown";
    }

    private void DrawVulcanRepairConfig()
    {
        var config = GatherBuddy.Config.VulcanRepairConfig;

        var enabled = config.Enabled;
        if (ImGui.Checkbox("Enable Repair", ref enabled))
        {
            config.Enabled = enabled;
            GatherBuddy.Config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Automatically repair equipment between crafts when needed");

        var threshold = config.RepairThreshold;
        ImGui.SetNextItemWidth(150);
        if (ImGui.SliderInt("Repair Threshold (%)", ref threshold, 0, 99))
        {
            config.RepairThreshold = threshold;
            GatherBuddy.Config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Repair when minimum equipment condition drops below this percentage");

        var prioritizeNPC = config.PrioritizeNPCRepair;
        if (ImGui.Checkbox("Prioritize NPC Repair", ref prioritizeNPC))
        {
            config.PrioritizeNPCRepair = prioritizeNPC;
            GatherBuddy.Config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Use NPC repair if available and you have enough gil, otherwise use self-repair");
        
        if (config.PrioritizeNPCRepair)
        {
            ImGui.Spacing();
            ImGui.Text("Preferred Repair NPC:");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Select a repair NPC to travel to when repair is needed");
            
            ImGui.SetNextItemWidth(300);
            var currentNPC = config.PreferredRepairNPC;
            var displayText = currentNPC != null 
                ? $"{currentNPC.Name} ({GetTerritoryName(currentNPC.TerritoryType)})"
                : "Current Zone NPCs";
            
            if (ImGui.BeginCombo("##PreferredRepairNPC", displayText))
            {
                ImGui.SetNextItemWidth(280);
                ImGui.InputTextWithHint("##RepairNPCSearch", "Search NPCs...", ref _repairNPCSearchInput, 256);
                ImGui.Separator();
                
                if (ImGui.Selectable("Current Zone NPCs", currentNPC == null))
                {
                    config.PreferredRepairNPC = null;
                    config.PreferredRepairNPCDataId = 0;
                    GatherBuddy.Config.Save();
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Use any repair NPC in the current zone");
                
                var repairNPCs = Crafting.RepairNPCHelper.RepairNPCs;
                if (repairNPCs.Count == 0)
                {
                    ImGui.TextDisabled("No repair NPCs loaded yet...");
                }
                else
                {
                    var searchLower = _repairNPCSearchInput.ToLowerInvariant();
                    var filteredNPCs = string.IsNullOrWhiteSpace(_repairNPCSearchInput)
                        ? repairNPCs
                        : repairNPCs.Where(npc => 
                            npc.Name.ToLowerInvariant().Contains(searchLower) ||
                            GetTerritoryName(npc.TerritoryType).ToLowerInvariant().Contains(searchLower)).ToList();
                    
                    if (filteredNPCs.Count == 0)
                    {
                        ImGui.TextDisabled("No NPCs match your search...");
                    }
                    else
                    {
                        foreach (var npc in filteredNPCs)
                        {
                            var territoryName = GetTerritoryName(npc.TerritoryType);
                            var npcLabel = $"{npc.Name} - {territoryName}";
                            
                            if (ImGui.Selectable(npcLabel, currentNPC?.DataId == npc.DataId))
                            {
                                config.PreferredRepairNPC = npc;
                                config.PreferredRepairNPCDataId = npc.DataId;
                                GatherBuddy.Config.Save();
                            }
                        }
                    }
                }
                
                ImGui.EndCombo();
            }
        }
    }
    
    private void DrawVulcanMateriaConfig()
    {
        var config = GatherBuddy.Config.VulcanMateriaConfig;

        var enabled = config.Enabled;
        if (ImGui.Checkbox("Enable Materia Extraction", ref enabled))
        {
            config.Enabled = enabled;
            GatherBuddy.Config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Automatically extract materia from fully spiritbonded equipment between crafts");
    }

    private void DrawTeamCraftImportWindow()
    {
        if (!_showTeamCraftImport)
            return;

        ImGui.SetNextWindowSize(new Vector2(520, 310), ImGuiCond.Appearing);
        if (ImGui.Begin("TeamCraft Import###TCImport", ref _showTeamCraftImport, ImGuiWindowFlags.NoCollapse))
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey3, "Open your list on TeamCraft, copy the 'Final Items' section using");
            ImGui.TextColored(ImGuiColors.DalamudGrey3, "'Copy as Text', then paste below. Precrafts are generated automatically.");
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            ImGui.Text("List Name:");
            ImGui.SetNextItemWidth(-1);
            ImGui.InputText("##ImportListName", ref _teamCraftListName, 256);

            ImGui.Spacing();
            ImGui.Text("Final Items:");
            ImGui.InputTextMultiline("##FinalItems", ref _teamCraftFinalItems, 500000, new Vector2(-1, 150));

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            if (ImGui.Button("Import", new Vector2(100, 0)))
            {
                var importedList = ParseTeamCraftImport();
                if (importedList != null)
                {
                    _editingList = importedList;
                    _listEditor  = new CraftingListEditor(importedList);
                    _listEditor.OnStartCrafting = (l) => { StartCraftingList(l); MinimizeWindow(); };
                    _listEditor.RefreshInventoryCounts();
                    GatherBuddy.CraftingMaterialsWindow?.SetEditor(_listEditor);
                    _deferEditorDraw = true;

                    _teamCraftListName  = string.Empty;
                    _teamCraftFinalItems = string.Empty;
                    _showTeamCraftImport = false;

                    GatherBuddy.Log.Information($"[VulcanWindow] Successfully imported TeamCraft list: {importedList.Name}");
                }
            }

            ImGui.SameLine();
            if (ImGui.Button("Cancel", new Vector2(100, 0)))
            {
                _teamCraftListName   = string.Empty;
                _teamCraftFinalItems = string.Empty;
                _showTeamCraftImport = false;
            }

            ImGui.End();
        }
    }
    
    private CraftingListDefinition? ParseTeamCraftImport()
    {
        if (string.IsNullOrWhiteSpace(_teamCraftFinalItems))
        {
            GatherBuddy.Log.Warning("[VulcanWindow] TeamCraft import: Final items field is empty");
            return null;
        }

        var recipesToAdd = new List<(uint recipeId, int quantity)>();

        ParseTeamCraftSection(_teamCraftFinalItems, recipesToAdd);
        
        if (recipesToAdd.Count == 0)
        {
            GatherBuddy.Log.Warning("[VulcanWindow] TeamCraft import: No valid recipes found");
            return null;
        }
        
        var listName = string.IsNullOrWhiteSpace(_teamCraftListName) 
            ? "Imported from TeamCraft" 
            : _teamCraftListName;
        
        var newList = GatherBuddy.CraftingListManager.CreateNewList(listName);
        
        foreach (var (recipeId, quantity) in recipesToAdd)
        {
            var existingItem = newList.Recipes.FirstOrDefault(r => r.RecipeId == recipeId);
            if (existingItem != null)
            {
                existingItem.Quantity += quantity;
            }
            else
            {
                newList.Recipes.Add(new CraftingListItem(recipeId, quantity));
            }
        }
        
        GatherBuddy.CraftingListManager.SaveList(newList);
        GatherBuddy.Log.Debug($"[VulcanWindow] TeamCraft import: Created list '{listName}' with {newList.Recipes.Count} unique recipes");
        
        return newList;
    }
    
    private void ParseTeamCraftSection(string text, List<(uint recipeId, int quantity)> output)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        
        using var reader = new System.IO.StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                continue;
            
            if (parts[0].EndsWith('x'))
            {
                if (!int.TryParse(parts[0].Substring(0, parts[0].Length - 1), out int numberOfItems))
                    continue;
                
                var itemName = string.Join(" ", parts.Skip(1)).Trim();
                
                GatherBuddy.Log.Debug($"[VulcanWindow] TeamCraft import: Parsing {numberOfItems}x {itemName}");
                
                var recipeSheet = Dalamud.GameData.GetExcelSheet<Recipe>();
                if (recipeSheet == null)
                    continue;
                
                Recipe? foundRecipe = null;
                foreach (var recipe in recipeSheet)
                {
                    if (recipe.ItemResult.RowId > 0)
                    {
                        var item = recipe.ItemResult.Value;
                        if (item.Name.ExtractText() == itemName)
                        {
                            foundRecipe = recipe;
                            break;
                        }
                    }
                }
                
                if (foundRecipe != null)
                {
                    int craftsNeeded = (int)Math.Ceiling(numberOfItems / (double)foundRecipe.Value.AmountResult);
                    output.Add((foundRecipe.Value.RowId, craftsNeeded));
                    GatherBuddy.Log.Debug($"[VulcanWindow] TeamCraft import: Found recipe {foundRecipe.Value.RowId}, need {craftsNeeded} crafts for {numberOfItems} items");
                }
                else
                {
                    GatherBuddy.Log.Warning($"[VulcanWindow] TeamCraft import: Could not find recipe for item '{itemName}'");
                }
            }
        }
    }

    public void Dispose()
    {
        CraftingGameInterop.CraftFinished -= OnCraftFinished;
    }
}
