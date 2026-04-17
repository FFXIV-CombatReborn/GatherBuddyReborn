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
    private string?                 _previewFolderPath = null;
    private CraftingListEditor?     _listEditor   = null;
    private bool                    _deferEditorDraw = false;
    private bool                    _craftingListsRequestFocus = false;
    private bool                    _recipesTabRequestFocus    = false;
    private uint?                   _pendingRecipeItemId       = null;
    private bool                    _openCreateListPopup = false;
    private bool                    _openCreateFolderPopup = false;

    private bool _isMinimized = false;
    private bool _wasFocusedLastFrame = false;
    
    // TeamCraft import state
    private static readonly Vector2 DefaultTeamCraftImportWindowSize = new(520, 310);
    private bool _showTeamCraftImport    = false;
    private string _teamCraftListName    = string.Empty;
    private string _teamCraftFinalItems  = string.Empty;
    private bool _teamCraftEphemeral     = false;
    private Vector2 _teamCraftImportWindowSize;
    private bool _teamCraftImportWindowSizeDirty;
    
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
        _teamCraftImportWindowSize = NormalizeTeamCraftImportWindowSize(GatherBuddy.Config.TeamCraftImportWindowSize);
        
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

    public void OpenToMarketboardItem(uint itemId)
    {
        _isMinimized       = false;
        IsOpen             = true;
        _mbRequestFocus    = true;
        _mbSelectedItemId  = itemId;
        _mbDetailLastItemId = 0;
    }

    public void OpenToRecipe(uint itemId)
    {
        _isMinimized            = false;
        IsOpen                  = true;
        _recipesTabRequestFocus = true;
        _pendingRecipeItemId    = itemId;
        GatherBuddy.Log.Debug($"[VulcanWindow] OpenToRecipe requested for item {itemId}");
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
        OpenCraftingList(list);
    }

    public void OpenCreateListPopup(uint recipeId)
    {
        var recipe = RecipeManager.GetRecipe(recipeId);
        if (!recipe.HasValue)
        {
            GatherBuddy.Log.Warning($"[VulcanWindow] OpenCreateListPopup: No recipe found for id {recipeId}");
            return;
        }

        PrepareCreateListPopup();
        _isMinimized = false;
        IsOpen = true;
        _craftingListsRequestFocus = true;
        _openCreateListPopup = true;
        _newListRecipeId = recipe.Value.RowId;
        _newListRecipeName = recipe.Value.ItemResult.Value.Name.ExtractText();
        GatherBuddy.Log.Debug($"[VulcanWindow] Queued Create List popup for recipe {_newListRecipeName} ({_newListRecipeId.Value})");
    }
    private void DisposeListEditor()
    {
        _listEditor?.Dispose();
        _listEditor = null;
        GatherBuddy.CraftingMaterialsWindow?.SetEditor(null);
    }

    private void OpenCraftingList(CraftingListDefinition list)
    {
        DisposeListEditor();
        _editingList = list;
        _listEditor = new CraftingListEditor(list);
        _listEditor.OnStartCrafting = (l) => { StartCraftingList(l); MinimizeWindow(); };
        GatherBuddy.CraftingMaterialsWindow?.SetEditor(_listEditor);
        _deferEditorDraw = true;
    }

    internal void RefreshOpenCraftingList(int listId)
    {
        if (_editingList?.ID != listId || _listEditor == null)
            return;

        _listEditor.RefreshFromExternalListChange();
    }

    private void PrepareCreateListPopup(string? folderPath = null)
    {
        ResetCreateListPopupState();
        _newListFolderPath = CraftingListManager.NormalizeFolderPath(folderPath);
    }

    private void QueueCreateFolderPopup(string? parentFolderPath = null)
    {
        ResetCreateFolderPopupState();
        _newFolderParentPath = CraftingListManager.NormalizeFolderPath(parentFolderPath);
        _openCreateFolderPopup = true;
        GatherBuddy.Log.Debug($"[VulcanWindow] Queued Create Folder popup for parent '{_newFolderParentPath}'");
    }

    public override void PreDraw()
    {
        if (!IsOpen)
            return;

        if (_recipesTabRequestFocus)
            ImGui.SetNextWindowFocus();
    }

    public override void Draw()
    {
        GatherBuddy.ControllerSupport?.TabNavigation.Update(Dalamud.GamepadState, 10);
        
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
                    DrawRecipesTab();
                    DrawWorkshopsTab();
                    DrawMacrosTab();
                    DrawStandardSolverConfigTab();
                    DrawSolutionsTab();
                    DrawSettingsTab();
                    DrawDebugTab();
                    DrawMarketboardTab();
                    DrawVendorsTab();
                }
            }
        
        _craftSettingsPopup.Draw();
        
        GatherBuddy.ControllerSupport?.UpdateEndOfFrame();
    }

    public void Dispose()
    {
        CraftingGameInterop.CraftFinished -= OnCraftFinished;
        DisposeListEditor();
    }
}

