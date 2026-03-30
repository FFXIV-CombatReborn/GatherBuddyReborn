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
    private void DrawFilterPanel()
    {
        ImGui.Spacing();
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##recipeSearch", "Search...", ref _recipeSearchText, 256))
        {
            _filtersDirty = true;
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextColored(new Vector4(0.7f, 0.9f, 1.0f, 1.0f), "Crafting Jobs");
        ImGui.Spacing();

        var columns = 4;
        var buttonPad = 4f;
        var framePad = ImGui.GetStyle().FramePadding;
        var regionWidth = ImGui.GetContentRegionAvail().X;
        var btnSide = (regionWidth - (columns - 1) * buttonPad) / columns;
        var iconSide = btnSide - framePad.X * 2;
        if (iconSide < 16) iconSide = 16;
        if (iconSide > 26) { iconSide = 26; btnSide = iconSide + framePad.X * 2; }
        var iconSize = new Vector2(iconSide, iconSide);
        var selectedColor = new Vector4(0.25f, 0.50f, 0.85f, 1.00f);

        var isAllSelected = _selectedJobFilters.Count == 0;
        if (isAllSelected)
            ImGui.PushStyleColor(ImGuiCol.Button, selectedColor);
        
        ImGui.PushID("jobAll");
        if (ImGui.Button("All", new Vector2(btnSide, btnSide)))
        {
            _selectedJobFilters.Clear();
            _filtersDirty = true;
        }
        ImGui.PopID();
        
        if (isAllSelected)
            ImGui.PopStyleColor();
        
        ImGui.SameLine(0, buttonPad);

        for (var i = 0; i < JobNames.Length; i++)
        {
            var classJobId = CraftTypeToClassJobId[i];
            var jobId = classJobId;
            var isSelected = _selectedJobFilters.Contains(jobId);
            var jobIconId = 62100 + classJobId;

            if (isSelected)
                ImGui.PushStyleColor(ImGuiCol.Button, selectedColor);

            var wrap = Icons.DefaultStorage.TextureProvider
                .GetFromGameIcon(new GameIconLookup(jobIconId))
                .GetWrapOrDefault();

            var clicked = false;
            ImGui.PushID($"job{i}");
            if (wrap != null)
                clicked = ImGui.ImageButton(wrap.Handle, iconSize);
            else
                clicked = ImGui.Button(JobNames[i], new Vector2(iconSize.X + 8, iconSize.Y + 8));
            ImGui.PopID();

            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.Text(JobNames[i]);
                ImGui.EndTooltip();
            }

            if (clicked)
            {
                if (_selectedJobFilters.Contains(jobId))
                    _selectedJobFilters.Remove(jobId);
                else
                    _selectedJobFilters.Add(jobId);
                _filtersDirty = true;
            }

            if (isSelected)
                ImGui.PopStyleColor();

            if ((i + 2) % columns != 0 && i < JobNames.Length - 1)
                ImGui.SameLine(0, buttonPad);
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextColored(new Vector4(0.7f, 0.9f, 1.0f, 1.0f), "Level Range");
        ImGui.Spacing();

        if (ImGui.Checkbox("Item Equip Level", ref _filterByEquipLevel))
            _filtersDirty = true;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Filter and sort by item equip level instead of craft level");
        ImGui.Spacing();

        var sliderWidth = 150f;
        ImGui.SetNextItemWidth(sliderWidth);
        if (ImGui.SliderInt("##minLevel", ref _minLevel, 1, 100, "Min: %d", ImGuiSliderFlags.AlwaysClamp))
        {
            _minLevel = Math.Clamp(_minLevel, 1, _maxLevel);
            _filtersDirty = true;
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Ctrl+Click to type a value");
        }

        ImGui.SetNextItemWidth(sliderWidth);
        if (ImGui.SliderInt("##maxLevel", ref _maxLevel, 1, 100, "Max: %d", ImGuiSliderFlags.AlwaysClamp))
        {
            _maxLevel = Math.Clamp(_maxLevel, _minLevel, 100);
            _filtersDirty = true;
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Ctrl+Click to type a value");
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextColored(new Vector4(0.7f, 0.9f, 1.0f, 1.0f), "Filters");
        ImGui.Spacing();

        if (ImGui.Checkbox("Regular Only", ref _filterBrowserRegularOnly))
        {
            if (_filterBrowserRegularOnly)
            {
                _filterBrowserMasterRecipes = false;
                _filterBrowserCollectables = false;
                _filterBrowserExpertRecipes = false;
                _filterBrowserQuestRecipes = false;
            }
            _filtersDirty = true;
        }
        
        if (ImGui.Checkbox("Hide Crafted", ref _hideCrafted))
        {
            _filtersDirty = true;
        }

        if (ImGui.Checkbox("Collectables", ref _filterBrowserCollectables))
        {
            if (_filterBrowserCollectables)
                _filterBrowserRegularOnly = false;
            _filtersDirty = true;
        }

        if (ImGui.Checkbox("Master Recipes", ref _filterBrowserMasterRecipes))
        {
            if (_filterBrowserMasterRecipes)
                _filterBrowserRegularOnly = false;
            _filtersDirty = true;
        }

        if (ImGui.Checkbox("Expert Recipes", ref _filterBrowserExpertRecipes))
        {
            if (_filterBrowserExpertRecipes)
                _filterBrowserRegularOnly = false;
            _filtersDirty = true;
        }

        if (ImGui.Checkbox("Quest Recipes", ref _filterBrowserQuestRecipes))
        {
            if (_filterBrowserQuestRecipes)
                _filterBrowserRegularOnly = false;
            _filtersDirty = true;
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var count = _filteredRecipes?.Count ?? 0;
        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1), $"{count} recipes");
    }

    private void DrawResultsList()
    {
        if (_filteredRecipes == null || _filteredRecipes.Count == 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1), "No recipes match filters.");
            return;
        }

        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1), $"  {_filteredRecipes.Count} recipes");
        ImGui.SameLine(ImGui.GetContentRegionAvail().X - 180);
        
        var sortLabel = _sortColumn switch
        {
            SortColumn.Level => _filterByEquipLevel ? "Equip Lv" : "Level",
            SortColumn.Crafted => "Crafted",
            _ => "Sort"
        };
        var sortIcon = _sortDirection == SortDirection.Ascending ? FontAwesomeIcon.ArrowUp : FontAwesomeIcon.ArrowDown;
        
        ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1), "Sort:");
        ImGui.SameLine();
        
        if (ImGui.Button($"{sortLabel}##sortBtn", new Vector2(90, 0)))
        {
            ImGui.OpenPopup("##sortMenu");
        }
        
        ImGui.SameLine(0, 4);
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            ImGui.Text(sortIcon.ToIconString());
        }
        
        if (ImGui.BeginPopup("##sortMenu"))
        {
            if (ImGui.MenuItem("Level", "", _sortColumn == SortColumn.Level))
            {
                if (_sortColumn == SortColumn.Level)
                    _sortDirection = _sortDirection == SortDirection.Ascending ? SortDirection.Descending : SortDirection.Ascending;
                else
                    _sortColumn = SortColumn.Level;
                _filtersDirty = true;
            }
            if (ImGui.MenuItem("Crafted", "", _sortColumn == SortColumn.Crafted))
            {
                if (_sortColumn == SortColumn.Crafted)
                    _sortDirection = _sortDirection == SortDirection.Ascending ? SortDirection.Descending : SortDirection.Ascending;
                else
                    _sortColumn = SortColumn.Crafted;
                _filtersDirty = true;
            }
            ImGui.EndPopup();
        }
        
        ImGui.Separator();

        var iconSm = new Vector2(28, 28);
        var jobIconSm = new Vector2(20, 20);
        const float rightGroupWidth = 70f;
        var contentMaxX = ImGui.GetContentRegionMax().X;
        var itemHeight = iconSm.Y + ImGui.GetStyle().ItemSpacing.Y;

        ElliLib.ImGuiClip.ClippedDraw(_filteredRecipes, recipe =>
        {
            var isSelected = _selectedRecipe?.Recipe.RowId == recipe.Recipe.RowId;
            var rowStartY = ImGui.GetCursorPosY();

            if (recipe.Icon.TryGetWrap(out var wrap, out _))
                ImGui.Image(wrap.Handle, iconSm);
            else
                ImGui.Dummy(iconSm);
            
            ImGui.SameLine(0, 4);

            var cursorY = ImGui.GetCursorPosY();
            ImGui.SetCursorPosY(cursorY + (iconSm.Y - ImGui.GetTextLineHeight()) / 2);

            var hasSettings = GatherBuddy.RecipeBrowserSettings.Has(recipe.Recipe.RowId);
            if (hasSettings)
            {
                using var font = ImRaii.PushFont(UiBuilder.IconFont);
                ImGui.TextColored(new Vector4(0.3f, 0.9f, 0.9f, 1), FontAwesomeIcon.Cog.ToIconString());
                ImGui.SameLine();
            }

            var label = $"{recipe.Name}##browse{recipe.Recipe.RowId}";
            if (ImGui.Selectable(label, isSelected, ImGuiSelectableFlags.None, new Vector2(contentMaxX - ImGui.GetCursorPosX() - rightGroupWidth, 0)))
            {
                _selectedRecipe = recipe;
            }

            var isPopupOpen = GatherBuddy.ControllerSupport != null
                ? GatherBuddy.ControllerSupport.ContextMenu.BeginPopupContextItemWithGamepad($"RecipeContextMenu##{recipe.Recipe.RowId}", Dalamud.GamepadState)
                : ImGui.BeginPopupContextItem($"RecipeContextMenu##{recipe.Recipe.RowId}");
            
            if (isPopupOpen)
            {
                if (ImGui.MenuItem("Show Recipe Properties (Debug)"))
                {
                    GatherBuddy.Log.Information($"=== Recipe Properties for {recipe.Name} ===");
                    GatherBuddy.Log.Information($"Recipe.RowId: {recipe.Recipe.RowId}");
                    GatherBuddy.Log.Information($"Recipe.Quest.RowId: {recipe.Recipe.Quest.RowId}");
                    GatherBuddy.Log.Information($"Recipe.IsSecondary: {recipe.Recipe.IsSecondary}");
                    GatherBuddy.Log.Information($"Recipe.IsExpert: {recipe.Recipe.IsExpert}");
                    GatherBuddy.Log.Information($"Recipe.SecretRecipeBook.RowId: {recipe.Recipe.SecretRecipeBook.RowId}");
                    GatherBuddy.Log.Information($"Recipe.CanQuickSynth: {recipe.Recipe.CanQuickSynth}");
                    GatherBuddy.Log.Information($"Recipe.CanHq: {recipe.Recipe.CanHq}");
                    GatherBuddy.Log.Information($"Recipe.IsSpecializationRequired: {recipe.Recipe.IsSpecializationRequired}");
                    GatherBuddy.Log.Information($"Recipe.DifficultyFactor: {recipe.Recipe.DifficultyFactor}");
                    GatherBuddy.Log.Information($"Recipe.QualityFactor: {recipe.Recipe.QualityFactor}");
                    GatherBuddy.Log.Information($"Recipe.RecipeLevelTable.RowId: {recipe.Recipe.RecipeLevelTable.RowId}");
                    var resultItem = recipe.Recipe.ItemResult.Value;
                    GatherBuddy.Log.Information($"Item.RowId: {resultItem.RowId}");
                    GatherBuddy.Log.Information($"Item.AlwaysCollectable: {resultItem.AlwaysCollectable}");
                    GatherBuddy.Log.Information($"Item.IsUnique: {resultItem.IsUnique}");
                    GatherBuddy.Log.Information($"Item.IsUntradable: {resultItem.IsUntradable}");
                    GatherBuddy.Log.Information($"Item.ItemSearchCategory.RowId: {resultItem.ItemSearchCategory.RowId}");
                    GatherBuddy.Log.Information($"Item.ItemUICategory.RowId: {resultItem.ItemUICategory.RowId}");
                    GatherBuddy.Log.Information($"Item.Rarity: {resultItem.Rarity}");
                }
                
                ImGui.Separator();

                var lists = GatherBuddy.CraftingListManager.Lists;

                if (ImGui.IsWindowAppearing())
                {
                    _contextMenuListSearch      = string.Empty;
                    _contextMenuAddQuantity     = 1;
                    _contextMenuNewListName     = string.Empty;
                    _contextMenuNewListEphemeral = false;
                }

                ImGui.TextColored(new Vector4(0.7f, 1.0f, 0.7f, 1.0f), "Create New List:");
                ImGui.SetNextItemWidth(-1);
                var createEnter = ImGui.InputTextWithHint("##NewListName", "List name...", ref _contextMenuNewListName, 128, ImGuiInputTextFlags.EnterReturnsTrue);
                ImGui.Checkbox("Ephemeral##ctxNewListEphemeral", ref _contextMenuNewListEphemeral);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Delete this list automatically after crafting completes.\nCan be disabled later in the list editor.");
                if ((ImGui.Button("Create & Add", new Vector2(-1, 0)) || createEnter) && !string.IsNullOrWhiteSpace(_contextMenuNewListName))
                {
                    var newList = GatherBuddy.CraftingListManager.CreateNewList(_contextMenuNewListName.Trim(), _contextMenuNewListEphemeral);
                    newList.Recipes.Add(new CraftingListItem(recipe.Recipe.RowId, _contextMenuAddQuantity));
                    GatherBuddy.CraftingListManager.SaveList(newList);
                    GatherBuddy.Log.Information($"[VulcanWindow] Created list '{newList.Name}' and added {recipe.Name} x{_contextMenuAddQuantity}");
                    ImGui.CloseCurrentPopup();
                }

                if (lists.Count > 0)
                {
                    ImGui.Spacing();
                    ImGui.Separator();

                    var filteredLists = string.IsNullOrWhiteSpace(_contextMenuListSearch)
                        ? lists
                        : lists.Where(l => l.Name.Contains(_contextMenuListSearch, StringComparison.OrdinalIgnoreCase)).ToList();

                    var rowH = ImGui.GetTextLineHeightWithSpacing();

                    ImGui.TextColored(new Vector4(1.0f, 0.9f, 0.6f, 1.0f), $"Add {recipe.Name} to list:");
                    ImGui.AlignTextToFramePadding();
                    ImGui.Text("Qty:");
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(100);
                    ImGui.InputInt("##ContextQty", ref _contextMenuAddQuantity, 1);
                    if (_contextMenuAddQuantity < 1) _contextMenuAddQuantity = 1;
                    ImGui.SetNextItemWidth(-1);
                    ImGui.InputTextWithHint("##ContextListSearch", "Search lists...", ref _contextMenuListSearch, 128);

                    var singleH = filteredLists.Count > 0 ? Math.Min(filteredLists.Count * rowH, 150f) : rowH;
                    ImGui.BeginChild("##SingleAddScroll", new Vector2(0, singleH), true);
                    if (filteredLists.Count == 0)
                        ImGui.TextDisabled("No matches");
                    foreach (var list in filteredLists)
                    {
                        if (ImGui.MenuItem(list.Name))
                        {
                            list.Recipes.Add(new CraftingListItem(recipe.Recipe.RowId, _contextMenuAddQuantity));
                            GatherBuddy.CraftingListManager.SaveList(list);
                            GatherBuddy.Log.Information($"Added {recipe.Name} x{_contextMenuAddQuantity} to crafting list '{list.Name}'");
                        }
                    }
                    ImGui.EndChild();

                    ImGui.Spacing();
                    ImGui.Separator();
                    ImGui.Spacing();

                    ImGui.TextColored(new Vector4(0.6f, 0.9f, 1.0f, 1.0f), "Add all uncrafted (filtered) to:");

                    var bulkH = filteredLists.Count > 0 ? Math.Min(filteredLists.Count * rowH, 150f) : rowH;
                    ImGui.BeginChild("##BulkAddScroll", new Vector2(0, bulkH), true);
                    if (filteredLists.Count == 0)
                        ImGui.TextDisabled("No matches");
                    foreach (var list in filteredLists)
                    {
                        if (ImGui.MenuItem($"{list.Name} (bulk)##bulk_{list.ID}"))
                        {
                            var uncraftedCount = 0;
                            if (_filteredRecipes != null)
                            {
                                foreach (var r in _filteredRecipes)
                                {
                                    if (!r.IsCrafted)
                                    {
                                        list.Recipes.Add(new CraftingListItem(r.Recipe.RowId, 1));
                                        uncraftedCount++;
                                    }
                                }
                            }
                            GatherBuddy.CraftingListManager.SaveList(list);
                            GatherBuddy.Log.Information($"Added {uncraftedCount} uncrafted recipes to list '{list.Name}'");
                        }
                    }
                    ImGui.EndChild();
                }
                else
                {
                    ImGui.TextDisabled("No crafting lists available");
                }

                ImGui.EndPopup();
            }

            ImGui.SetCursorPosX(contentMaxX - rightGroupWidth);
            ImGui.SetCursorPosY(rowStartY + (iconSm.Y - ImGui.GetTextLineHeight()) / 2);
            
            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                if (recipe.IsCrafted)
                {
                    ImGui.TextColored(new Vector4(0.0f, 0.5f, 0.0f, 1), FontAwesomeIcon.Check.ToIconString());
                }
                else
                {
                    ImGui.TextColored(new Vector4(0.5f, 0.0f, 0.0f, 1), FontAwesomeIcon.Times.ToIconString());
                }
            }
            
            ImGui.SameLine(0, 4);
            ImGui.SetCursorPosY(rowStartY + (iconSm.Y - jobIconSm.Y) / 2);
            
            var jobIconId = 62100 + CraftTypeToClassJobId[recipe.Recipe.CraftType.RowId];
            var jobWrap = Icons.DefaultStorage.TextureProvider
                .GetFromGameIcon(new GameIconLookup(jobIconId))
                .GetWrapOrDefault();
            if (jobWrap != null)
                ImGui.Image(jobWrap.Handle, jobIconSm);
            
            ImGui.SameLine(0, 2);
            ImGui.SetCursorPosY(rowStartY + (iconSm.Y - ImGui.GetTextLineHeight()) / 2);
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1), _filterByEquipLevel ? $"{recipe.ItemEquipLevel}" : $"{recipe.Level}");
            ImGui.SetCursorPosY(rowStartY + itemHeight);
        }, itemHeight);
    }

    private void DrawDetailsPanel()
    {
        if (_selectedRecipe == null)
        {
            var center = ImGui.GetContentRegionAvail();
            ImGui.SetCursorPos(new Vector2(12, center.Y / 2 - 20));
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1), "Select a recipe to view details");
            ImGui.SetCursorPosX(12);
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1), "and start crafting.");
            return;
        }

        var recipe = _selectedRecipe;

        ImGui.Spacing();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 12);
        ImGui.TextColored(new Vector4(0.65f, 0.65f, 0.65f, 1.0f), $"Recipe ID: {recipe.Recipe.RowId}");
        ImGui.Spacing();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 12);
        if (recipe.Icon.TryGetWrap(out var wrap, out _))
            ImGui.Image(wrap.Handle, new Vector2(48, 48));
        else
            ImGui.Dummy(new Vector2(48, 48));
        
        ImGui.SameLine(0, 12);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (48 - ImGui.GetTextLineHeight()) / 2);
        ImGui.TextColored(new Vector4(1.0f, 0.9f, 0.6f, 1.0f), recipe.Name);

        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 12);
        
        var r = recipe.Recipe;
        if (r.SecretRecipeBook.RowId > 0)
        {
            ImGui.TextColored(new Vector4(0.9f, 0.7f, 1.0f, 1.0f), "[Master]");
            ImGui.SameLine();
        }
        if (r.ItemResult.Value.AlwaysCollectable)
        {
            ImGui.TextColored(new Vector4(0.7f, 1.0f, 0.9f, 1.0f), "[Collectable]");
            ImGui.SameLine();
        }
        if (r.IsExpert)
        {
            ImGui.TextColored(new Vector4(1.0f, 0.5f, 0.5f, 1.0f), "[Expert]");
            ImGui.SameLine();
        }
        if (r.ItemResult.Value.ItemSearchCategory.RowId == 0)
        {
            ImGui.TextColored(new Vector4(0.5f, 0.9f, 1.0f, 1.0f), "[Quest]");
            ImGui.SameLine();
        }
        if (recipe.IsCrafted)
        {
            ImGui.TextColored(new Vector4(0.3f, 1.0f, 0.3f, 1.0f), "[Crafted]");
            ImGui.SameLine();
        }
        ImGui.NewLine();

        ImGui.Spacing();

        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 12);
        var classLineY = ImGui.GetCursorPosY();
        var classMidY  = classLineY + (24 - ImGui.GetTextLineHeight()) / 2;
        var jobIconId  = 62100 + CraftTypeToClassJobId[r.CraftType.RowId];
        var jobWrap    = Icons.DefaultStorage.TextureProvider
            .GetFromGameIcon(new GameIconLookup(jobIconId))
            .GetWrapOrDefault();
        ImGui.SetCursorPosY(classMidY);
        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1), "Class:");
        ImGui.SameLine();
        ImGui.SetCursorPosY(classLineY);
        if (jobWrap != null)
            ImGui.Image(jobWrap.Handle, new Vector2(24, 24));
        ImGui.SameLine(0, 2);
        ImGui.SetCursorPosY(classMidY);
        ImGui.TextColored(new Vector4(0.8f, 0.9f, 1.0f, 1.0f), recipe.JobAbbreviation);
        ImGui.SameLine(0, 16);
        ImGui.SetCursorPosY(classMidY);
        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1), "Level:");
        ImGui.SameLine();
        ImGui.SetCursorPosY(classMidY);
        ImGui.TextColored(new Vector4(0.8f, 0.9f, 1.0f, 1.0f), recipe.Level.ToString());
        ImGui.SameLine(0, 16);
        ImGui.SetCursorPosY(classMidY);
        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1), "Yield:");
        ImGui.SameLine();
        ImGui.SetCursorPosY(classMidY);
        ImGui.TextColored(new Vector4(0.8f, 0.9f, 1.0f, 1.0f), r.AmountResult.ToString());

        ImGui.Spacing();
        var lt = r.RecipeLevelTable.Value;
        var difficulty = (int)(lt.Difficulty * r.DifficultyFactor / 100);
        var qualityMax  = (int)(lt.Quality    * r.QualityFactor    / 100);
        var durability  = (int)(lt.Durability  * r.DurabilityFactor  / 100);
        var statLabelColor = new Vector4(0.6f, 0.6f, 0.6f, 1.0f);
        var statValueColor = new Vector4(0.8f, 0.9f, 1.0f, 1.0f);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 12);
        ImGui.TextColored(statLabelColor, "Difficulty:"); ImGui.SameLine(0, 4);
        ImGui.TextColored(statValueColor, $"{difficulty}"); ImGui.SameLine(0, 16);
        ImGui.TextColored(statLabelColor, "Durability:"); ImGui.SameLine(0, 4);
        ImGui.TextColored(statValueColor, $"{durability}"); ImGui.SameLine(0, 16);
        ImGui.TextColored(statLabelColor, "Max Quality:"); ImGui.SameLine(0, 4);
        ImGui.TextColored(statValueColor, $"{qualityMax}");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var directIngredients = RecipeManager.GetIngredients(r);
        var itemSheet = Dalamud.GameData.GetExcelSheet<Item>();
        var showRetainer = AllaganTools.Enabled;

        DrawIngredientSectionHeader("Ingredients", showRetainer);

        var craftable = directIngredients.Count > 0 ? int.MaxValue : 0;
        foreach (var (ingId, ingAmt) in directIngredients)
        {
            if (ingAmt <= 0) continue;
            var (ingNq, ingHq) = GetInventoryCountSplit(ingId);
            craftable = Math.Min(craftable, (ingNq + ingHq) / ingAmt);
        }
        if (craftable == int.MaxValue) craftable = 0;

        foreach (var (itemId, needed) in directIngredients)
        {
            if (itemSheet == null || !itemSheet.TryGetRow(itemId, out var item))
                continue;
            DrawIngredientRow(itemId, needed, item, showRetainer);
        }

        ImGui.Spacing();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 12);
        var (resultNq, resultHq) = GetInventoryCountSplit(r.ItemResult.RowId);
        var bagTotal = resultNq + resultHq;
        ImGui.TextColored(statLabelColor, "Craftable:"); ImGui.SameLine(0, 4);
        ImGui.TextColored(craftable > 0 ? statValueColor : new Vector4(1f, 0.4f, 0.4f, 1f), $"{craftable}");
        ImGui.SameLine(0, 16);
        ImGui.TextColored(statLabelColor, "In Bag:"); ImGui.SameLine(0, 4);
        ImGui.TextColored(bagTotal > 0 ? statValueColor : new Vector4(0.5f, 0.5f, 0.5f, 1f),
            resultHq > 0 ? $"{resultNq}+{resultHq}hq" : $"{resultNq}");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawIngredientSectionHeader("All Materials (including precrafts)", showRetainer);

        var resolvedIngredients = RecipeManager.GetResolvedIngredients(r);
        foreach (var (itemId, needed) in resolvedIngredients.OrderBy(x => x.Key))
        {
            if (itemSheet == null || !itemSheet.TryGetRow(itemId, out var item))
                continue;
            DrawIngredientRow(itemId, needed, item, showRetainer);
        }

        ImGui.Spacing();
        ImGui.Spacing();

        var settings = GatherBuddy.RecipeBrowserSettings.Get(recipe.Recipe.RowId);
        if (settings != null && settings.HasAnySettings())
        {
            ImGui.Separator();
            ImGui.Spacing();
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 12);
            ImGui.TextColored(new Vector4(0.3f, 0.9f, 0.9f, 1.0f), "Configured Settings:");
            ImGui.Spacing();
            
            if (itemSheet != null)
            {
                if (settings.FoodItemId.HasValue && itemSheet.TryGetRow(settings.FoodItemId.Value, out var food))
                {
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 24);
                    ImGui.Text($"Food: {food.Name.ExtractText()}");
                }
                if (settings.MedicineItemId.HasValue && itemSheet.TryGetRow(settings.MedicineItemId.Value, out var medicine))
                {
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 24);
                    ImGui.Text($"Medicine: {medicine.Name.ExtractText()}");
                }
                if (settings.ManualItemId.HasValue && itemSheet.TryGetRow(settings.ManualItemId.Value, out var manual))
                {
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 24);
                    ImGui.Text($"Manual: {manual.Name.ExtractText()}");
                }
            }
            ImGui.Spacing();
        }

        var avail = ImGui.GetContentRegionAvail();
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + Math.Max(0, avail.Y - 96));

        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 12);
        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1), "Qty:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(100);
        ImGui.InputInt("##browserQty", ref _browserCraftQuantity, 1);
        if (_browserCraftQuantity < 1) _browserCraftQuantity = 1;

        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 12);
        var topRowButtonWidth = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) / 2f;
        var artisanLoaded = IPCSubscriber.IsReady("Artisan");
        if (artisanLoaded)
        {
            ImGuiUtil.DrawDisabledButton("Artisan Detected", new Vector2(topRowButtonWidth, 22),
                "Artisan plugin is loaded. Please unload Artisan to use Vulcan's crafting system.", true);
        }
        else if (ImGui.Button("Start Craft", new Vector2(topRowButtonWidth, 22)))
        {
            StartBrowserCraft(recipe.Recipe, _browserCraftQuantity);
            MinimizeWindow();
        }
        ImGui.SameLine();
        if (ImGui.Button("Settings", new Vector2(topRowButtonWidth, 22)))
            _craftSettingsPopup.Open(recipe.Recipe.RowId, recipe.Name);

        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 12);
        var canQuickSynth = recipe.Recipe.CanQuickSynth;
        var qsTooltip = artisanLoaded
            ? "Artisan plugin is loaded. Please unload Artisan to use Vulcan's crafting system."
            : canQuickSynth
                ? $"Quick synthesize {recipe.Name} x{_browserCraftQuantity}"
                : "This recipe cannot be quick synthesized.";
        if (ImGuiUtil.DrawDisabledButton("Quick Synth", new Vector2(-1, 22), qsTooltip, !canQuickSynth || artisanLoaded))
        {
            StartBrowserQuickSynth(recipe.Recipe, _browserCraftQuantity);
            MinimizeWindow();
        }
    }

    private static void DrawIngredientSectionHeader(string title, bool showRetainer)
    {
        const float colWidth = 40f;
        var headerY     = ImGui.GetCursorPosY();
        var contentMaxX = ImGui.GetContentRegionMax().X;
        var nqColStart  = showRetainer ? contentMaxX - colWidth * 3 : contentMaxX - colWidth * 2;
        var hqColStart  = showRetainer ? contentMaxX - colWidth * 2 : contentMaxX - colWidth;

        var titleStartX   = ImGui.GetCursorPosX() + 12;
        var titleMaxWidth = nqColStart - titleStartX - 8f;
        if (titleMaxWidth > 0 && ImGui.CalcTextSize(title).X > titleMaxWidth)
        {
            while (title.Length > 0 && ImGui.CalcTextSize(title + "...").X > titleMaxWidth)
                title = title[..^1];
            title += "...";
        }
        ImGui.SetCursorPosX(titleStartX);
        ImGui.TextColored(new Vector4(0.7f, 0.9f, 1.0f, 1.0f), title);

        var colHeaderColor = new Vector4(0.5f, 0.5f, 0.5f, 1.0f);

        var nqW = ImGui.CalcTextSize("NQ").X;
        ImGui.SetCursorPosX(nqColStart + (colWidth - nqW) / 2);
        ImGui.SetCursorPosY(headerY);
        ImGui.TextColored(colHeaderColor, "NQ");

        var hqW = ImGui.CalcTextSize("HQ").X;
        ImGui.SetCursorPosX(hqColStart + (colWidth - hqW) / 2);
        ImGui.SetCursorPosY(headerY);
        ImGui.TextColored(colHeaderColor, "HQ");

        if (showRetainer)
        {
            var retColStart = contentMaxX - colWidth;
            var retW = ImGui.CalcTextSize("Ret").X;
            ImGui.SetCursorPosX(retColStart + (colWidth - retW) / 2);
            ImGui.SetCursorPosY(headerY);
            ImGui.TextColored(colHeaderColor, "Ret");
        }

        ImGui.Spacing();
    }

    private static void DrawIngredientRow(uint itemId, int needed, Item item, bool showRetainer)
    {
        const float colWidth    = 40f;
        const float xnWidth     = 32f;
        const float iconSize    = 24f;
        const float xnIconGap   = 4f;
        const float iconNameGap = 6f;

        var rowStartX   = ImGui.GetCursorPosX() + 12;
        var rowY        = ImGui.GetCursorPosY();
        var textY       = rowY + (iconSize - ImGui.GetTextLineHeight()) / 2;
        var contentMaxX = ImGui.GetContentRegionMax().X;
        var nqColStart  = showRetainer ? contentMaxX - colWidth * 3 : contentMaxX - colWidth * 2;
        var hqColStart  = showRetainer ? contentMaxX - colWidth * 2 : contentMaxX - colWidth;

        var xnText  = $"\u00d7{needed}";
        var xnTextW = ImGui.CalcTextSize(xnText).X;
        ImGui.SetCursorPosX(rowStartX + (xnWidth - xnTextW) / 2);
        ImGui.SetCursorPosY(textY);
        ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), xnText);

        var iconX = rowStartX + xnWidth + xnIconGap;
        ImGui.SetCursorPosX(iconX);
        ImGui.SetCursorPosY(rowY);
        var icon = Icons.DefaultStorage.TextureProvider.GetFromGameIcon(new GameIconLookup(item.Icon));
        if (icon.TryGetWrap(out var ingWrap, out _))
            ImGui.Image(ingWrap.Handle, new Vector2(iconSize, iconSize));
        else
            ImGui.Dummy(new Vector2(iconSize, iconSize));

        var nameStartX   = iconX + iconSize + iconNameGap;
        var nameMaxWidth = nqColStart - nameStartX - 6f;
        ImGui.SetCursorPosX(nameStartX);
        ImGui.SetCursorPosY(textY);
        var name = item.Name.ExtractText();
        if (nameMaxWidth > 0 && ImGui.CalcTextSize(name).X > nameMaxWidth)
        {
            while (name.Length > 0 && ImGui.CalcTextSize(name + "...").X > nameMaxWidth)
                name = name[..^1];
            name += "...";
        }
        ImGui.Text(name);

        var (nq, hq) = GetInventoryCountSplit(itemId);
        var total     = nq + hq;
        var haveColor = total >= needed ? new Vector4(0.3f, 1.0f, 0.3f, 1.0f) : new Vector4(1.0f, 0.5f, 0.5f, 1.0f);

        var nqStr = nq > 9999 ? "9999+" : $"{nq}";
        ImGui.SetCursorPosX(nqColStart + (colWidth - ImGui.CalcTextSize(nqStr).X) / 2);
        ImGui.SetCursorPosY(textY);
        ImGui.TextColored(haveColor, nqStr);

        var hqStr = hq > 9999 ? "9999+" : $"{hq}";
        ImGui.SetCursorPosX(hqColStart + (colWidth - ImGui.CalcTextSize(hqStr).X) / 2);
        ImGui.SetCursorPosY(textY);
        ImGui.TextColored(new Vector4(0.6f, 0.85f, 1.0f, 1.0f), hqStr);

        if (showRetainer)
        {
            var retColStart = contentMaxX - colWidth;
            var retCount    = GetRetainerItemCount(itemId);
            var retStr      = retCount > 9999 ? "9999+" : $"{retCount}";
            ImGui.SetCursorPosX(retColStart + (colWidth - ImGui.CalcTextSize(retStr).X) / 2);
            ImGui.SetCursorPosY(textY);
            ImGui.TextColored(new Vector4(0.5f, 0.8f, 1.0f, 1.0f), retStr);
        }

        ImGui.SetCursorPosY(rowY + iconSize + ImGui.GetStyle().ItemSpacing.Y);
    }

    private static unsafe (int nq, int hq) GetInventoryCountSplit(uint itemId)
    {
        try
        {
            var inventory = InventoryManager.Instance();
            if (inventory == null) return (0, 0);
            var nq = (int)inventory->GetInventoryItemCount(itemId, false, false, false);
            var hq = (int)inventory->GetInventoryItemCount(itemId, true,  false, false);
            return (nq, hq);
        }
        catch { return (0, 0); }
    }

    private static int GetRetainerItemCount(uint itemId)
    {
        if (!AllaganTools.Enabled)
            return 0;

        var now = DateTime.Now;
        if (RetainerIngredientRefreshTimes.TryGetValue(itemId, out var lastRefresh)
         && (now - lastRefresh).TotalSeconds < RetainerIngredientRefreshIntervalSeconds)
            return CachedRetainerIngredientCounts.GetValueOrDefault(itemId, 0);

        var count = RetainerItemQuery.GetTotalCount(itemId);
        CachedRetainerIngredientCounts[itemId] = count;
        RetainerIngredientRefreshTimes[itemId] = now;
        return count;
    }

}
