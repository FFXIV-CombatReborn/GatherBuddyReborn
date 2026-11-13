using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using GatherBuddy.Crafting;
using GatherBuddy.Plugin;
using GatherBuddy.Utility;
using Lumina.Excel.Sheets;
using RebornMaterial;
using System;
using System.Numerics;
using System.Threading.Tasks;
using Functions = GatherBuddy.Plugin.Functions;

namespace GatherBuddy.Gui;

public partial class VulcanWindow : M3ShellWindow, IDisposable
{
    public const string WindowId = "Vulcan - Crafting###VulcanWindow";

    private const string SectionCraft = "Craft";
    private const string SectionBuySell = "Buy and sell";
    private const string SectionSolvers = "Solvers";
    private const string SectionSettings = "General";
    private const string SectionTools = "Tools";

    private const string CraftingListsPageId = "crafting_lists";
    private const string RecipesPageId = "recipes";
    private const string VendorsPageId = "vendors";
    private const string MarketboardPageId = "marketboard";

    private static readonly Vector2 DefaultSize = new(1100f, 760f);
    private static readonly WindowSizeConstraints DefaultConstraints = new()
    {
        MinimumSize = new Vector2(500f, 300f),
        MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
    };

    private static readonly M3WindowAction[] VulcanWindowActions =
    [
        new("##vulcan_open_collectables", FontAwesomeIcon.BoxOpen, "Open the collectables turn-in and purchase window"),
        new("##vulcan_open_craft_status", FontAwesomeIcon.Tasks, "Reopen the Craft Status window for the active crafting queue"),
    ];

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

    private DateTime _recipesTabHotkeyAvailableAt = DateTime.MinValue;

    // TeamCraft import state
    private string _teamCraftListName = string.Empty;
    private string _teamCraftFinalItems = string.Empty;
    private bool _teamCraftEphemeral = false;
    private string? _teamCraftImportError;
    private const string ArtisanPluginName = "Artisan";
    private const double ArtisanToggleTimeoutSeconds = 10.0;
    private bool? _pendingArtisanEnabledState = null;
    private DateTime _artisanToggleRequestedAt = DateTime.MinValue;
    private Task? _artisanToggleTask = null;

    private ArtisanChip _artisanChip;

    // Debug tab state
    private uint _debugSelectedJobId = 8;
    private string? _debugLastTestResult;

    public CraftingListDefinition? CurrentCraftingList
        => _editingList;

    private readonly record struct ArtisanChip(string Label, FontAwesomeIcon Icon, bool Selected, bool Enabled, string Tooltip, bool Loaded);

    public VulcanWindow()
        : base(WindowId, "vulcan", "Vulcan", "Vulcan", DefaultSize, DefaultConstraints)
    {
        BuildPages();
        CraftingGameInterop.CraftFinished += OnCraftFinished;
    }

    private void BuildPages()
    {
        AddPage(new M3Page(CraftingListsPageId, "Crafting Lists", FontAwesomeIcon.ClipboardList, SectionCraft,
            "Lists of things to craft, with their materials and precrafts", DrawCraftingListsPage, false));
        AddPage(new M3Page(RecipesPageId, "Recipes", FontAwesomeIcon.BookOpen, SectionCraft,
            "Browse every recipe, craft it now or add it to a list", Panels(DrawRecipesPage), false));
        AddPage(new M3Page("workshops", "Workshops", FontAwesomeIcon.Industry, SectionCraft,
            "Company workshop projects and what they still need", Panels(DrawWorkshopsPage), false));

        AddPage(new M3Page(VendorsPageId, "Vendors", FontAwesomeIcon.Store, SectionBuySell,
            "Find vendors for items and keep buy lists", Panels(DrawVendorsPage), false));
        AddPage(new M3Page(MarketboardPageId, "Marketboard", FontAwesomeIcon.ChartLine, SectionBuySell,
            "Marketboard prices and sale history", Panels(DrawMarketboardPage), false));

        AddPage(new M3Page("macros", "Macros", FontAwesomeIcon.Scroll, SectionSolvers,
            "Your own crafting macros", Panels(DrawMacrosPage), false));
        AddPage(new M3Page("standard_solver", "Standard Solver", FontAwesomeIcon.Cogs, SectionSolvers,
            "How the dynamic Standard Solver behaves", DrawStandardSolverPage));
        AddPage(new M3Page("solutions", "Solutions", FontAwesomeIcon.Lightbulb, SectionSolvers,
            "Saved Raphael solutions", Panels(DrawSolutionsPage), false));

        AddPage(new M3Page("settings", "Settings", FontAwesomeIcon.Cog, SectionSettings,
            "Solver, repair, materia and retainer settings for crafting", DrawSettingsPage));

        AddPage(new M3Page("debug", "Debug", FontAwesomeIcon.Bug, SectionTools,
            "Internal crafting data, for troubleshooting", Panels(DrawDebugPage), false, tool: true));
    }

    private static System.Action Panels(System.Action draw)
        => VulcanUiStyle.Panels(draw);

    private void OnCraftFinished(Recipe? recipe, bool cancelled)
    {
        if (!cancelled && recipe != null)
        {
            _craftedStatusDirty = true;
        }
    }

    private void MinimizeWindow()
        => Minimize();

    public void RestoreWindow()
        => OpenAndRestore();

    public void OpenToMarketboard()
    {
        Restore();
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
        Restore();
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
        Restore();
        IsOpen = true;
        _vendorsTabRequestFocus = true;
    }

    public void OpenToCraftingLists()
    {
        OpenAndRestore();
        _craftingListsRequestFocus = true;
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

        Restore();
        IsOpen = true;
        if (list == null)
        {
            GatherBuddy.Log.Warning($"[VulcanWindow] OpenToList: No list found matching '{argument}'");
            return;
        }

        _craftingListsRequestFocus = true;
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
        Restore();
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
        => M3Widgets.SearchField(id, hint.TrimEnd('.'), ref value, ImGui.GetContentRegionAvail().X, maxLength);

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

    protected override M3WindowAction[] WindowActions
        => VulcanWindowActions;

    protected override void OnWindowAction(int index)
    {
        switch (index)
        {
            case 0:
                if (GatherBuddy.CollectablesWindow == null)
                {
                    GatherBuddy.Log.Debug("[VulcanWindow] Collectables action clicked, but the collectables window was unavailable.");
                    M3Snackbar.Show("The collectables window isn't available.", severity: M3Severity.Error);
                }
                else
                {
                    GatherBuddy.Log.Debug("[VulcanWindow] Opening collectables from the Vulcan title bar.");
                    GatherBuddy.CollectablesWindow.Open();
                }

                break;
            case 1:
                if (GatherBuddy.CraftingStatusWindow?.HasActiveQueue == true)
                {
                    GatherBuddy.CraftingStatusWindow.OpenOrRestore();
                }
                else
                {
                    M3Snackbar.Show("No crafting queue is running.");
                }

                break;
        }
    }

    protected override void PreDrawFold()
    {
        if (_recipesTabRequestFocus)
        {
            _recipesTabRequestFocus = false;
            ShowPage(RecipesPageId);
            ImGui.SetNextWindowFocus();
        }

        if (_craftingListsRequestFocus)
        {
            _craftingListsRequestFocus = false;
            ShowPage(CraftingListsPageId);
        }

        if (_vendorsTabRequestFocus)
        {
            _vendorsTabRequestFocus = false;
            ShowPage(VendorsPageId);
        }

        if (_mbRequestFocus)
        {
            _mbRequestFocus = false;
            ShowPage(MarketboardPageId);
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

    protected override void BeforeDrawShell()
    {
        _artisanChip = BuildArtisanChip();
    }

    protected override float TopAppBarTrailingWidth
        => _artisanChip.Label is { } label ? M3Widgets.ChipWidth(label, _artisanChip.Icon) + M3.Space2 : 0f;

    protected override void DrawTopAppBarTrailing(in M3TopAppBar.Scope bar)
    {
        var chip = _artisanChip;
        if (chip.Label == null)
        {
            return;
        }

        ImGui.SetCursorScreenPos(bar.TrailingPosition(M3Widgets.ChipHeight));
        if (M3Widgets.Chip("##vulcan_artisan", chip.Label, chip.Selected, chip.Icon, chip.Tooltip, enabled: chip.Enabled))
        {
            TryToggleArtisan(!chip.Loaded);
        }
    }

    private ArtisanChip BuildArtisanChip()
    {
        var state = DalamudPluginToggleHelper.GetPluginToggleState(ArtisanPluginName);
        var installed = state.IsInstalled;
        var loaded = state.IsLoaded;
        var inProgress = UpdatePendingArtisanToggle(installed, loaded);
        var blocked = installed && !state.CanToggle && !inProgress;

        if (!installed)
        {
            return new ArtisanChip("Artisan missing", FontAwesomeIcon.Ban, false, false,
                "Artisan isn't installed. Once it is installed, Vulcan can turn it on and off for you while crafting.", false);
        }

        if (inProgress)
        {
            return new ArtisanChip(_pendingArtisanEnabledState == true ? "Enabling Artisan…" : "Disabling Artisan…", FontAwesomeIcon.Sync, loaded, false,
                "Waiting for Dalamud to finish.", loaded);
        }

        if (blocked)
        {
            return new ArtisanChip(loaded ? "Artisan on" : "Artisan off", loaded ? FontAwesomeIcon.ToggleOn : FontAwesomeIcon.ToggleOff, loaded, false,
                "Artisan can't be turned on or off from here right now.", loaded);
        }

        return new ArtisanChip(loaded ? "Artisan on" : "Artisan off", loaded ? FontAwesomeIcon.ToggleOn : FontAwesomeIcon.ToggleOff, loaded, true,
            loaded ? "Artisan is enabled. Click to disable it." : "Artisan is disabled. Click to enable it.", loaded);
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
        GC.SuppressFinalize(this);
    }
}
