using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using ElliLib;
using ElliLib.Table;
using GatherBuddy.Crafting;
using GatherBuddy.Plugin;
using Lumina.Excel.Sheets;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using ImRaii = ElliLib.Raii.ImRaii;

namespace GatherBuddy.Gui;

public partial class VulcanWindow
{
    private static Vector2 IconSize => ImGuiHelpers.ScaledVector2(40, 40);
    private static Vector2 LineIconSize => new(ImGui.GetTextLineHeight(), ImGui.GetTextLineHeight());
    private static Vector2 ItemSpacing => ImGui.GetStyle().ItemSpacing;
    private static float Scale => ImGuiHelpers.GlobalScale;
    private static readonly Dictionary<uint, int> CachedRetainerIngredientCounts = new();
    private static readonly Dictionary<uint, DateTime> RetainerIngredientRefreshTimes = new();
    private const double RetainerIngredientRefreshIntervalSeconds = 1.0;
    
    private static float TextWidth(string text)
        => ImGui.CalcTextSize(text).X + ItemSpacing.X;

    private enum SortColumn { Name, Job, Level, Crafted }
    private enum SortDirection { Ascending, Descending }
    
    private static List<ExtendedRecipe>? _extendedRecipeList;
    private static List<ExtendedRecipe>? _filteredRecipes;
    private static bool _filtersDirty = true;
    private static ExtendedRecipe? _selectedRecipe;
    private static SortColumn _sortColumn = SortColumn.Level;
    private static SortDirection _sortDirection = SortDirection.Ascending;
    private static bool _hideCrafted = false;
    private static bool _filterByEquipLevel = false;
    private static RecipeTable? _recipeTable;
    private static string _recipeSearchText = "";
    private static HashSet<uint> _selectedJobFilters = new();
    private static int _minLevel = 1;
    private static int _maxLevel = 100;
    private static bool _filterBrowserMasterRecipes = false;
    private static bool _filterBrowserHousingRecipes = false;
    private static bool _filterBrowserCollectables = false;
    private static bool _filterBrowserExpertRecipes = false;
    private static bool _filterBrowserQuestRecipes = false;
    private static bool _filterBrowserLevelingOnly = false;
    private static bool _isInitialized = false;
    private static bool _craftedStatusDirty = false;
    private static int _browserCraftQuantity = 1;
    private static RecipeCraftSettingsPopup _craftSettingsPopup = new();
    private static string _contextMenuListSearch   = string.Empty;
    private static int    _contextMenuAddQuantity   = 1;
    private static string _contextMenuNewListName   = string.Empty;
    private static bool   _contextMenuNewListEphemeral = false;
    private static readonly uint[] CraftTypeToClassJobId = { 8, 9, 10, 11, 12, 13, 14, 15 };
    private static readonly string[] JobNames = { "CRP", "BSM", "ARM", "GSM", "LTW", "WVR", "ALC", "CUL" };

    private static void InitializeRecipeList()
    {
        if (_isInitialized)
            return;

        var tempList = new List<ExtendedRecipe>();
        var recipeSheet = Dalamud.GameData.GetExcelSheet<Recipe>();
        if (recipeSheet != null)
        {
            foreach (var recipe in recipeSheet)
            {
                if (recipe.ItemResult.RowId > 0)
                {
                    tempList.Add(new ExtendedRecipe(recipe, lazyLoad: false));
                }
            }
        }
        _extendedRecipeList = tempList;

        _isInitialized = true;
        _filtersDirty = true;
    }

    private static void UpdateFilteredList()
    {
        if (!_filtersDirty || _extendedRecipeList == null)
            return;

        var filtered = _extendedRecipeList.Where(PassesFilters).ToList();
        
        filtered = _sortColumn switch
        {
            SortColumn.Name => _sortDirection == SortDirection.Ascending 
                ? filtered.OrderBy(r => r.Name).ToList()
                : filtered.OrderByDescending(r => r.Name).ToList(),
            SortColumn.Job => _sortDirection == SortDirection.Ascending
                ? filtered.OrderBy(r => r.JobId).ToList()
                : filtered.OrderByDescending(r => r.JobId).ToList(),
            SortColumn.Level => _sortDirection == SortDirection.Ascending 
                ? filtered.OrderBy(r => _filterByEquipLevel ? r.ItemEquipLevel : r.Level).ToList()
                : filtered.OrderByDescending(r => _filterByEquipLevel ? r.ItemEquipLevel : r.Level).ToList(),
            SortColumn.Crafted => _sortDirection == SortDirection.Ascending
                ? filtered.OrderBy(r => r.IsCrafted).ToList()
                : filtered.OrderByDescending(r => r.IsCrafted).ToList(),
            _ => filtered
        };
        
        _filteredRecipes = filtered;
        _filtersDirty = false;
        GatherBuddy.Log.Debug($"[VulcanWindow] Filtered to {_filteredRecipes.Count} recipes");
    }


    private static bool IsLevelingRecipe(Recipe recipe)
        => recipe.SecretRecipeBook.RowId == 0 && recipe.RecipeNotebookList.RowId < 1000;

    private static bool IsHousingRecipe(Recipe recipe)
        => recipe.ItemResult.Value.ItemSearchCategory.RowId is 56 or >= 65 and <= 72;

    private static void LogRecipeNotebookDivisionInfo(Recipe recipe)
    {

        GatherBuddy.Log.Information($"Recipe.NotebookList.RowId: {recipe.RecipeNotebookList.RowId}");
        GatherBuddy.Log.Information($"Recipe.SecretRecipeBook.RowId: {recipe.SecretRecipeBook.RowId}");
        GatherBuddy.Log.Information($"Recipe.IsLevelBasedByNotebookList: {IsLevelingRecipe(recipe)}");
        GatherBuddy.Log.Information($"Recipe.IsHousingByItemSearchCategory: {IsHousingRecipe(recipe)}");
    }

    private static bool PassesRecipeTypeFilters(Recipe recipe)
    {
        if (_filterBrowserLevelingOnly)
            return IsLevelingRecipe(recipe);

        if (_filterBrowserHousingRecipes && !IsHousingRecipe(recipe))
            return false;

        if (_filterBrowserMasterRecipes && recipe.SecretRecipeBook.RowId == 0)
            return false;

        if (_filterBrowserCollectables && !recipe.ItemResult.Value.AlwaysCollectable)
            return false;

        if (_filterBrowserExpertRecipes && !recipe.IsExpert)
            return false;

        if (_filterBrowserQuestRecipes && recipe.ItemResult.Value.ItemSearchCategory.RowId != 0)
            return false;

        return true;
    }

    private static bool PassesFilters(ExtendedRecipe item)
    {
        if (!string.IsNullOrWhiteSpace(_recipeSearchText))
        {
            if (!item.Name.Contains(_recipeSearchText, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        if (_selectedJobFilters.Count > 0)
        {
            if (!_selectedJobFilters.Contains(item.JobId))
                return false;
        }

        var levelValue = _filterByEquipLevel ? item.ItemEquipLevel : item.Level;
        if (levelValue < _minLevel || levelValue > _maxLevel)
            return false;
        
        if (_hideCrafted && item.IsCrafted)
            return false;

        if (!PassesRecipeTypeFilters(item.Recipe))
            return false;

        return true;
    }

    private void DrawRecipesTab()
    {
        IDisposable tabItem;
        bool tabOpen;
        
        if (GatherBuddy.ControllerSupport != null && !_recipesTabRequestFocus)
        {
            var handle = GatherBuddy.ControllerSupport.TabNavigation.TabItem("Recipes##recipesTab", 1, 8);
            tabItem = handle;
            tabOpen = handle;
        }
        else
        {
            ImRaii.IEndObject handle;
            if (_recipesTabRequestFocus)
            {
                unsafe
                {
                    var labelBytes = System.Text.Encoding.UTF8.GetBytes("Recipes##recipesTab\0");
                    fixed (byte* ptr = labelBytes)
                    {
                        handle = ImRaii.TabItem(ptr, ImGuiTabItemFlags.SetSelected);
                    }
                }
            }
            else
            {
                handle = ImRaii.TabItem("Recipes##recipesTab");
            }
            tabItem = handle;
            tabOpen = handle.Success;
            if (tabOpen)
                _recipesTabRequestFocus = false;
        }
        
        using (tabItem)
        {
            if (!tabOpen)
                return;

        if (!_isInitialized)
        {
            InitializeRecipeList();
        }

        if (_pendingRecipeItemId.HasValue && _extendedRecipeList != null)
        {
            var targetItemId = _pendingRecipeItemId.Value;
            _pendingRecipeItemId = null;
            var target = _extendedRecipeList.FirstOrDefault(r => r.Recipe.ItemResult.RowId == targetItemId);
            if (target != null)
            {
                _selectedRecipe = target;
                GatherBuddy.Log.Debug($"[VulcanWindow] Navigated to recipe for item {targetItemId}: {target.Name}");
            }
            else
            {
                GatherBuddy.Log.Debug($"[VulcanWindow] No recipe found in browser for item {targetItemId}");
            }
        }

        if (_craftedStatusDirty && _extendedRecipeList != null)
        {
            foreach (var recipe in _extendedRecipeList)
            {
                recipe.UpdateCraftedStatus();
            }
            _craftedStatusDirty = false;
        }

        UpdateFilteredList();

        ImGui.Spacing();
        var avail = ImGui.GetContentRegionAvail();

        using (var color = ImRaii.PushColor(ImGuiCol.ChildBg, new Vector4(0.08f, 0.08f, 0.10f, 1.00f)))
        {
            ImGui.BeginChild("##FilterPanel", new Vector2(180, avail.Y), true);
            DrawFilterPanel();
            ImGui.EndChild();
        }

        ImGui.SameLine();

        using (var color = ImRaii.PushColor(ImGuiCol.ChildBg, new Vector4(0.08f, 0.08f, 0.10f, 1.00f)))
        {
            ImGui.BeginChild("##ResultsList", new Vector2(avail.X * 0.40f, avail.Y), true);
            DrawResultsList();
            ImGui.EndChild();
        }

        ImGui.SameLine();

        using (var color = ImRaii.PushColor(ImGuiCol.ChildBg, new Vector4(0.08f, 0.08f, 0.10f, 1.00f)))
        {
            ImGui.BeginChild("##DetailsPanel", new Vector2(0, avail.Y), true);
            DrawDetailsPanel();
            ImGui.EndChild();
        }
        }
    }

}
