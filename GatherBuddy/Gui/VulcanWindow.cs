using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using GatherBuddy.Crafting;
using GatherBuddy.Plugin;
using GatherBuddy.Utility;
using Lumina.Excel.Sheets;
using System;
using System.Numerics;
using System.Threading.Tasks;
using Functions = GatherBuddy.Plugin.Functions;
using ImRaii = ElliLib.Raii.ImRaii;

namespace GatherBuddy.Gui;

public partial class VulcanWindow : Window, IDisposable
{
    // Shared state
    private CraftingListDefinition? _editingList = null;
    private CraftingListDefinition? _previewList = null;
    private string? _previewFolderPath = null;
    private CraftingListEditor? _listEditor = null;
    private bool _deferEditorDraw = false;
    private bool _craftingListsRequestFocus = false;
    private bool _recipesTabRequestFocus = false;
    private bool _vendorsTabRequestFocus = false;
    private uint? _pendingRecipeId = null;
    private uint? _pendingRecipeScrollId = null;
    private bool _openCreateListPopup = false;
    private bool _openCreateFolderPopup = false;

    private bool? _pendingCollapseState = null;
    private bool _wasFocusedLastFrame = false;
    private DateTime _recipesTabHotkeyAvailableAt = DateTime.MinValue;

    // TeamCraft import state
    private static readonly Vector2 LegacyTeamCraftImportWindowSize = new(520f, 310f);
    private static readonly Vector2 DefaultTeamCraftImportWindowSize = VulcanUiScaling.Scaled(520f, 310f);
    private bool _showTeamCraftImport = false;
    private string _teamCraftListName = string.Empty;
    private string _teamCraftFinalItems = string.Empty;
    private bool _teamCraftEphemeral = false;
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
            MinimumSize = VulcanUiScaling.Scaled(500f, 300f),
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
    public void OpenToMarketboard()
    {
        _pendingCollapseState = false;
        IsOpen = true;
        _mbRequestFocus = true;
    }

    public void OpenToMarketboardItem(uint itemId)
    {
        OpenToMarketboard();
        _mbSelectedItemId = itemId;
        _mbDetailLastItemId = 0;
    }
    public void OpenToRecipes()
    {
        _pendingCollapseState = false;
        IsOpen = true;
        _recipesTabRequestFocus = true;
        _pendingRecipeId = null;
    }

    public void OpenToRecipe(uint recipeId)
    {
        OpenToRecipes();
        _pendingRecipeId = recipeId;
        GatherBuddy.Log.Debug($"[VulcanWindow] OpenToRecipe requested for recipe {recipeId}");
    }

    public void OpenToVendors()
    {
        _pendingCollapseState = false;
        IsOpen = true;
        _vendorsTabRequestFocus = true;
    }

    public void OpenToList(string argument)
    {
        CraftingListDefinition? list;
        if (int.TryParse(argument, out var listId))
        {
            list = GatherBuddy.CraftingListManager.GetListByID(listId);
        }
        else
        {
            list = GatherBuddy.CraftingListManager.GetListByName(argument);
        }

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
        GatherBuddy.CraftingTreeWindow?.SetEditor(null);
    }

    private void OpenCraftingList(CraftingListDefinition list)
    {
        DisposeListEditor();
        _editingList = list;
        _listEditor = new CraftingListEditor(list)
        {
            OnStartCrafting = (l) => { StartCraftingList(l); MinimizeWindow(); }
        };
        GatherBuddy.CraftingMaterialsWindow?.SetEditor(_listEditor);
        GatherBuddy.CraftingTreeWindow?.SetEditor(_listEditor);
        _deferEditorDraw = true;
    }

    internal void RefreshOpenCraftingList(int listId)
    {
        if (_editingList?.ID != listId || _listEditor == null)
        {
            return;
        }

        _listEditor.RefreshFromExternalListChange();
    }

    private static bool DrawSearchInputWithInlineClear(string id, string hint, ref string value, int maxLength)
    {
        ImGui.SetNextItemWidth(-1f);
        var changed = ImGui.InputTextWithHint(id, hint, ref value, maxLength);
        ImGui.SetItemAllowOverlap();
        if (string.IsNullOrEmpty(value))
        {
            return changed;
        }

        var frameMin = ImGui.GetItemRectMin();
        var frameMax = ImGui.GetItemRectMax();
        var cursorPos = ImGui.GetCursorPos();
        var style = ImGui.GetStyle();
        var drawList = ImGui.GetWindowDrawList();
        var frameHeight = frameMax.Y - frameMin.Y;
        var buttonSize = new Vector2(
            System.Math.Max(1f, frameHeight - style.FramePadding.Y * 2f),
            System.Math.Max(1f, frameHeight - style.FramePadding.Y * 2f));
        var buttonPos = new Vector2(
            frameMax.X - buttonSize.X - style.FramePadding.X,
            frameMin.Y + (frameHeight - buttonSize.Y) * 0.5f);
        var buttonBackgroundMin = new Vector2(buttonPos.X - style.FramePadding.X, frameMin.Y + 1f);
        var buttonBackgroundMax = new Vector2(frameMax.X - 1f, frameMax.Y - 1f);
        var frameColor = ImGui.GetColorU32(ImGui.IsItemActive()
            ? ImGuiCol.FrameBgActive
            : ImGui.IsItemHovered()
                ? ImGuiCol.FrameBgHovered
                : ImGuiCol.FrameBg);
        drawList.AddRectFilled(buttonBackgroundMin, buttonBackgroundMax, frameColor);

        ImGui.SetCursorScreenPos(buttonPos);
        bool clearClicked;
        bool clearHovered;
        using (ImRaii.PushId(id))
        {
            clearClicked = ImGui.InvisibleButton("##clear", buttonSize);
            clearHovered = ImGui.IsItemHovered();
        }

        if (clearHovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }

        var iconColor = clearHovered
            ? ImGui.GetColorU32(ImGuiCol.Text)
            : ImGui.ColorConvertFloat4ToU32(new Vector4(0.62f, 0.62f, 0.68f, 1f));
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            var iconText = FontAwesomeIcon.Times.ToIconString();
            var iconSize = ImGui.CalcTextSize(iconText);
            var iconPos = buttonPos + (buttonSize - iconSize) * 0.5f;
            drawList.AddText(iconPos, iconColor, iconText);
        }

        ImGui.SetCursorPos(cursorPos);
        if (!clearClicked)
        {
            return changed;
        }

        value = string.Empty;
        return true;
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
        {
            return;
        }

        if (_pendingCollapseState.HasValue)
        {
            ImGui.SetNextWindowCollapsed(_pendingCollapseState.Value, ImGuiCond.Always);
            _pendingCollapseState = null;
        }

        if (_recipesTabRequestFocus)
        {
            ImGui.SetNextWindowFocus();
        }
    }

    public override void PreOpenCheck()
    {
        if (_recipesTabHotkeyAvailableAt > DateTime.UtcNow || !Functions.CheckKeyState(GatherBuddy.Config.VulcanRecipesTabHotkey, false))
        {
            return;
        }

        _recipesTabHotkeyAvailableAt = DateTime.UtcNow.AddMilliseconds(500);
        OpenToRecipes();
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
            {
                TryToggleArtisan(!artisanLoaded);
            }
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Collectables##openCollectables"))
        {
            if (GatherBuddy.CollectablesWindow == null)
            {
                GatherBuddy.Log.Debug("[VulcanWindow] Collectables header button clicked, but the collectables window was unavailable.");
            }
            else
            {
                GatherBuddy.Log.Debug("[VulcanWindow] Opening collectables from the Vulcan header.");
                GatherBuddy.CollectablesWindow.Open();
            }
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Open the shared collectables turn-in and purchase automation window.");
        }

        ImGui.SameLine();
        var hasActiveCraftStatus = GatherBuddy.CraftingStatusWindow?.HasActiveQueue == true;
        using (ImRaii.Disabled(!hasActiveCraftStatus))
        {
            if (ImGui.SmallButton("Craft Status##openCraftStatus"))
            {
                GatherBuddy.CraftingStatusWindow?.OpenOrRestore();
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(hasActiveCraftStatus
                ? "Reopen or restore the Craft Status window for the active crafting queue."
                : "No active crafting queue.");
        }
    }

    private bool UpdatePendingArtisanToggle(bool artisanInstalled, bool artisanLoaded)
    {
        if (_pendingArtisanEnabledState == null)
        {
            return false;
        }

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
            {
                GatherBuddy.Log.Debug($"[VulcanWindow] Artisan toggle exception: {exception}");
            }

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
        {
            return true;
        }

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

