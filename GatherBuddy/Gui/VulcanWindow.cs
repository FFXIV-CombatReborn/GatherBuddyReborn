using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.GamePad;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using Dalamud.Interface.Colors;
using FFXIVClientStructs.FFXIV.Client.Game;
using ElliLib;
using GatherBuddy.Crafting;
using GatherBuddy.Plugin;
using GatherBuddy.Utility;
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
    private uint?                   _pendingRecipeId           = null;
    private uint?                   _pendingRecipeScrollId     = null;
    private bool                    _openCreateListPopup = false;
    private bool                    _openCreateFolderPopup = false;

    private bool? _pendingCollapseState = null;
    private bool _wasFocusedLastFrame = false;
    
    // TeamCraft import state
    private static readonly Vector2 DefaultTeamCraftImportWindowSize = new(520, 310);
    private bool _showTeamCraftImport    = false;
    private string _teamCraftListName    = string.Empty;
    private string _teamCraftFinalItems  = string.Empty;
    private bool _teamCraftEphemeral     = false;
    private Vector2 _teamCraftImportWindowSize;
    private bool _teamCraftImportWindowSizeDirty;
    private const string ArtisanPluginName = "Artisan";
    private const double ArtisanToggleTimeoutSeconds = 10.0;
    private bool? _pendingArtisanEnabledState = null;
    private DateTime _artisanToggleRequestedAt = DateTime.MinValue;
    private Task? _artisanToggleTask = null;
    
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
        _pendingCollapseState = true;
    }

    public void RestoreWindow()
    {
        _pendingCollapseState = false;
        IsOpen = true;
    }

    public void OpenToMarketboardItem(uint itemId)
    {
        _pendingCollapseState = false;
        IsOpen             = true;
        _mbRequestFocus    = true;
        _mbSelectedItemId  = itemId;
        _mbDetailLastItemId = 0;
    }

    public void OpenToRecipe(uint recipeId)
    {
        _pendingCollapseState   = false;
        IsOpen                  = true;
        _recipesTabRequestFocus = true;
        _pendingRecipeId        = recipeId;
        GatherBuddy.Log.Debug($"[VulcanWindow] OpenToRecipe requested for recipe {recipeId}");
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
            _pendingCollapseState = false;
            IsOpen = true;
            return;
        }

        _pendingCollapseState = false;
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
        _pendingCollapseState = false;
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

        if (_pendingCollapseState.HasValue)
        {
            ImGui.SetNextWindowCollapsed(_pendingCollapseState.Value, ImGuiCond.Always);
            _pendingCollapseState = null;
        }

        if (_recipesTabRequestFocus)
            ImGui.SetNextWindowFocus();
    }

    public override void Draw()
    {
        using var theme = VulcanUiStyle.PushTheme();
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
        
        DrawHeader();
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

    private void DrawHeader()
    {
        var artisanToggleState = DalamudPluginToggleHelper.GetPluginToggleState(ArtisanPluginName);
        var artisanInstalled = artisanToggleState.IsInstalled;
        var artisanLoaded = artisanToggleState.IsLoaded;
        var artisanToggleInProgress = UpdatePendingArtisanToggle(artisanInstalled, artisanLoaded);
        var artisanToggleBlocked = artisanInstalled && !artisanToggleState.CanToggle && !artisanToggleInProgress;

        ImGui.AlignTextToFramePadding();
        ImGui.Text("Crafting System");
        ImGui.SameLine();

        var buttonLabel = artisanToggleInProgress
            ? _pendingArtisanEnabledState == true
                ? "Enabling Artisan..."
                : "Disabling Artisan..."
            : artisanInstalled
                ? artisanLoaded
                    ? "Disable Artisan"
                    : "Enable Artisan"
                : "Artisan Missing";
        using (ImRaii.Disabled(!artisanInstalled || artisanToggleInProgress || artisanToggleBlocked))
        {
            if (ImGui.SmallButton($"{buttonLabel}##toggleArtisan"))
                TryToggleArtisan(!artisanLoaded);
        }

    }

    private bool UpdatePendingArtisanToggle(bool artisanInstalled, bool artisanLoaded)
    {
        if (_pendingArtisanEnabledState == null)
            return false;

        if (!artisanInstalled)
        {
            GatherBuddy.Log.Warning("[VulcanWindow] Artisan toggle was pending, but Artisan is no longer installed.");
            ClearPendingArtisanToggle();
            return false;
        }

        if (_artisanToggleTask is { IsFaulted: true })
        {
            var exception = _artisanToggleTask.Exception?.GetBaseException();
            GatherBuddy.Log.Error($"[VulcanWindow] Failed to {(_pendingArtisanEnabledState.Value ? "enable" : "disable")} Artisan: {exception?.Message ?? "unknown error"}");
            if (exception != null)
                GatherBuddy.Log.Debug($"[VulcanWindow] Artisan toggle exception: {exception}");
            Communicator.PrintError($"Failed to {(_pendingArtisanEnabledState.Value ? "enable" : "disable")} Artisan.");
            ClearPendingArtisanToggle();
            return false;
        }

        if (_artisanToggleTask is { IsCanceled: true })
        {
            GatherBuddy.Log.Warning($"[VulcanWindow] Artisan toggle was cancelled while trying to {(_pendingArtisanEnabledState.Value ? "enable" : "disable")} Artisan.");
            Communicator.PrintError($"Failed to {(_pendingArtisanEnabledState.Value ? "enable" : "disable")} Artisan.");
            ClearPendingArtisanToggle();
            return false;
        }

        if (artisanLoaded == _pendingArtisanEnabledState.Value)
        {
            GatherBuddy.Log.Debug($"[VulcanWindow] Artisan {(artisanLoaded ? "enabled" : "disabled")} successfully.");
            ClearPendingArtisanToggle();
            return false;
        }

        if ((DateTime.UtcNow - _artisanToggleRequestedAt).TotalSeconds <= ArtisanToggleTimeoutSeconds)
            return true;

        GatherBuddy.Log.Warning($"[VulcanWindow] Timed out waiting for Artisan to {(_pendingArtisanEnabledState.Value ? "enable" : "disable")}.");
        Communicator.PrintError($"Timed out trying to {(_pendingArtisanEnabledState.Value ? "enable" : "disable")} Artisan.");
        ClearPendingArtisanToggle();
        return false;
    }

    private void TryToggleArtisan(bool enable)
    {
        if (!DalamudPluginToggleHelper.TrySetPluginEnabled(ArtisanPluginName, enable, out var toggleTask, out var failureReason))
        {
            GatherBuddy.Log.Warning($"[VulcanWindow] Failed to invoke reflected Artisan toggle for state {(enable ? "enabled" : "disabled")}: {failureReason ?? "unknown reason"}.");
            Communicator.PrintError(failureReason ?? $"Failed to {(enable ? "enable" : "disable")} Artisan.");
            return;
        }

        _pendingArtisanEnabledState = enable;
        _artisanToggleRequestedAt = DateTime.UtcNow;
        _artisanToggleTask = toggleTask;
        GatherBuddy.Log.Debug($"[VulcanWindow] Requested to {(enable ? "enable" : "disable")} Artisan via reflected Dalamud plugin manager access.");
    }

    private void ClearPendingArtisanToggle()
    {
        _pendingArtisanEnabledState = null;
        _artisanToggleRequestedAt = DateTime.MinValue;
        _artisanToggleTask = null;
    }

    public void Dispose()
    {
        CraftingGameInterop.CraftFinished -= OnCraftFinished;
        DisposeListEditor();
    }
}

