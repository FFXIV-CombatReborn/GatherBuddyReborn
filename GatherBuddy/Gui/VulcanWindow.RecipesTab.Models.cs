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
    public class ExtendedRecipe
    {
        public Recipe Recipe;
        public ISharedImmediateTexture Icon = null!;
        public string Name = string.Empty;
        public string JobAbbreviation = string.Empty;
        public uint JobId;
        public uint Level;
        public uint ItemEquipLevel;
        public bool IsCrafted;
        internal bool _isFullyLoaded = false;

        public ExtendedRecipe(Recipe recipe, bool lazyLoad = false)
        {
            Recipe = recipe;
            if (lazyLoad)
                UpdateBasicInfo();
            else
                Update();
        }

        private void UpdateBasicInfo()
        {
            var itemSheet = Dalamud.GameData.GetExcelSheet<Item>();
            if (itemSheet != null && Recipe.ItemResult.RowId > 0)
            {
                var item = itemSheet.GetRow(Recipe.ItemResult.RowId);
                if (item.RowId > 0)
                {
                    Name = item.Name.ExtractText();
                    Icon = Icons.DefaultStorage.TextureProvider.GetFromGameIcon(new GameIconLookup(item.Icon));
                    ItemEquipLevel = (uint)item.LevelEquip;
                }
            }

            JobId = Recipe.CraftType.RowId + 8;
            Level = Recipe.RecipeLevelTable.Value.ClassJobLevel;

            var classJobSheet = Dalamud.GameData.GetExcelSheet<ClassJob>();
            if (classJobSheet != null)
            {
                var job = classJobSheet.GetRow(JobId);
                if (job.RowId > 0)
                    JobAbbreviation = job.Abbreviation.ExtractText();
            }

            _isFullyLoaded = false;
            
            UpdateCraftedStatus();
        }

        public unsafe void UpdateCraftedStatus()
        {
            try
            {
                IsCrafted = FFXIVClientStructs.FFXIV.Client.Game.QuestManager.IsRecipeComplete(Recipe.RowId);
            }
            catch (Exception ex)
            {
                GatherBuddy.Log.Debug($"[VulcanWindow] Failed to check crafted status for recipe {Recipe.RowId}: {ex.Message}");
                IsCrafted = false;
            }
        }

        public unsafe void Update()
        {
            var itemSheet = Dalamud.GameData.GetExcelSheet<Item>();
            if (itemSheet != null && Recipe.ItemResult.RowId > 0)
            {
                var item = itemSheet.GetRow(Recipe.ItemResult.RowId);
                if (item.RowId > 0)
                {
                    Name = item.Name.ExtractText();
                    Icon = Icons.DefaultStorage.TextureProvider.GetFromGameIcon(new GameIconLookup(item.Icon));
                    ItemEquipLevel = (uint)item.LevelEquip;
                }
            }

            JobId = Recipe.CraftType.RowId + 8;
            Level = Recipe.RecipeLevelTable.Value.ClassJobLevel;

            var classJobSheet = Dalamud.GameData.GetExcelSheet<ClassJob>();
            if (classJobSheet != null)
            {
                var job = classJobSheet.GetRow(JobId);
                if (job.RowId > 0)
                    JobAbbreviation = job.Abbreviation.ExtractText();
            }

            _isFullyLoaded = true;
            
            UpdateCraftedStatus();
        }

        public unsafe void SetTooltip(Vector2 iconSize)
        {
            using var tooltip = ImRaii.Tooltip();
            using var style = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, ImGui.GetStyle().ItemSpacing * new Vector2(1f, 1.5f));

            ImGui.TextColored(new Vector4(0.8f, 0.9f, 1.0f, 1.0f), Name);
            ImGui.Text($"Recipe ID: {Recipe.RowId}");
            ImGui.Text($"Level: {Level} {JobAbbreviation}");
            ImGui.Text($"Yields: {Recipe.AmountResult}x");

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            var directIngredients = RecipeManager.GetIngredients(Recipe);
            if (directIngredients.Count > 0)
            {
                ImGui.TextColored(new Vector4(1.0f, 0.9f, 0.6f, 1.0f), "Required Ingredients:");
                ImGui.Spacing();

                var itemSheet = Dalamud.GameData.GetExcelSheet<Item>();
                
                foreach (var (itemId, needed) in directIngredients)
                {
                    if (itemSheet == null || !itemSheet.TryGetRow(itemId, out var item))
                        continue;

                    var have = GetInventoryCount(itemId);
                    var icon = Icons.DefaultStorage.TextureProvider.GetFromGameIcon(new GameIconLookup(item.Icon));
                    
                    using var itemStyle = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, ImGui.GetStyle().ItemSpacing / 2);
                    
                    if (icon.TryGetWrap(out var wrap, out _))
                        ImGui.Image(wrap.Handle, iconSize * 0.75f);
                    else
                        ImGui.Dummy(iconSize * 0.75f);

                    ImGui.SameLine();

                    var color = have >= needed
                        ? new Vector4(0.3f, 1.0f, 0.3f, 1.0f)
                        : new Vector4(1.0f, 0.3f, 0.3f, 1.0f);

                    var itemName = item.Name.ExtractText();
                    ImGui.TextColored(color, $"{itemName}: {have} / {needed}");
                }
            }
            else
            {
                ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), "No materials required");
            }
            
            var settings = GatherBuddy.RecipeBrowserSettings.Get(Recipe.RowId);
            if (settings != null && settings.HasAnySettings())
            {
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();
                
                ImGui.TextColored(new Vector4(0.3f, 0.9f, 0.9f, 1.0f), "Configured Settings:");
                ImGui.Spacing();
                
                var itemSheet = Dalamud.GameData.GetExcelSheet<Item>();
                if (itemSheet != null)
                {
                    if (settings.FoodItemId.HasValue && itemSheet.TryGetRow(settings.FoodItemId.Value, out var food))
                        ImGui.Text($"Food: {food.Name.ExtractText()}");
                    if (settings.MedicineItemId.HasValue && itemSheet.TryGetRow(settings.MedicineItemId.Value, out var medicine))
                        ImGui.Text($"Medicine: {medicine.Name.ExtractText()}");
                    if (settings.ManualItemId.HasValue && itemSheet.TryGetRow(settings.ManualItemId.Value, out var manual))
                        ImGui.Text($"Manual: {manual.Name.ExtractText()}");
                    if (settings.SquadronManualItemId.HasValue && itemSheet.TryGetRow(settings.SquadronManualItemId.Value, out var sqManual))
                        ImGui.Text($"Squadron Manual: {sqManual.Name.ExtractText()}");
                }
            }
        }

        private static unsafe int GetInventoryCount(uint itemId)
        {
            try
            {
                var inventory = InventoryManager.Instance();
                if (inventory == null)
                    return 0;

                var baseItemId = itemId >= 1_000_000 ? itemId - 1_000_000 : itemId;
                var hqItemId = baseItemId + 1_000_000;
                var total = 0;
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
    }

    private sealed class RecipeTable : Table<ExtendedRecipe>
    {
        private static float _nameColumnWidth;
        private static float _jobColumnWidth;
        private static float _levelColumnWidth;
        private static float _craftedColumnWidth;
        private static float _globalScale;

        private static readonly NameColumn _nameColumn = new() { Label = "Recipe Name..." };
        private static readonly JobColumn _jobColumn = new() { Label = "Job" };
        private static readonly LevelColumn _levelColumn = new() { Label = "Level" };
        private static readonly CraftedColumn _craftedColumn = new() { Label = "Crafted" };

        protected override void PreDraw()
        {
            if (_globalScale != ImGuiHelpers.GlobalScale)
            {
                _globalScale = ImGuiHelpers.GlobalScale;
                _nameColumnWidth = Math.Max(300f, Items.Any() ? Items.Max(i => TextWidth(i.Name)) + ItemSpacing.X + LineIconSize.X : 300f) / Scale;
                _jobColumnWidth = TextWidth("CRP") / Scale + Table.ArrowWidth;
                _levelColumnWidth = TextWidth("100") / Scale + Table.ArrowWidth;
                _craftedColumnWidth = TextWidth("Crafted") / Scale + Table.ArrowWidth;
            }
        }

        public RecipeTable()
            : base("##RecipeTable", _extendedRecipeList ?? new List<ExtendedRecipe>(), _nameColumn, _jobColumn, _levelColumn, _craftedColumn)
        {
            Sortable = true;
            Flags |= ImGuiTableFlags.Hideable | ImGuiTableFlags.Reorderable | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY;
            Flags &= ~ImGuiTableFlags.NoBordersInBodyUntilResize;
        }

        public void UpdateFilter()
        {
            FilterDirty = true;
        }

        private sealed class NameColumn : ColumnString<ExtendedRecipe>
        {
            public NameColumn()
                => Flags |= ImGuiTableColumnFlags.NoHide | ImGuiTableColumnFlags.NoReorder;

            public override string ToName(ExtendedRecipe item)
                => item.Name;

            public override float Width
                => _nameColumnWidth * ImGuiHelpers.GlobalScale;

            public override bool DrawFilter()
            {
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(Label);
                return false;
            }

            public override void DrawColumn(ExtendedRecipe item, int id)
            {
                using var style = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, ItemSpacing / 2);
                
                if (item.Icon.TryGetWrap(out var wrap, out _))
                    ImGui.Image(wrap.Handle, LineIconSize);
                else
                    ImGui.Dummy(LineIconSize);
                
                ImGui.SameLine();
                
                var hasSettings = GatherBuddy.RecipeBrowserSettings.Has(item.Recipe.RowId);
                if (hasSettings)
                {
                    using var font = ImRaii.PushFont(UiBuilder.IconFont);
                    ImGui.TextColored(new Vector4(0.3f, 0.9f, 0.9f, 1), FontAwesomeIcon.Cog.ToIconString());
                    ImGui.SameLine();
                }
                
                var selected = ImGui.Selectable(item.Name);
                var hovered = ImGui.IsItemHovered();

                var isPopupOpen = GatherBuddy.ControllerSupport != null
                    ? GatherBuddy.ControllerSupport.ContextMenu.BeginPopupContextItemWithGamepad($"RecipeContextMenu##{item.Recipe.RowId}", Dalamud.GamepadState)
                    : ImGui.BeginPopupContextItem($"RecipeContextMenu##{item.Recipe.RowId}");
                
                if (isPopupOpen)
                {
                    if (ImGui.MenuItem("Configure Craft Settings"))
                    {
                        _craftSettingsPopup.Open(item.Recipe.RowId, item.Name);
                    }
                    
                    if (ImGui.MenuItem("Show Recipe Properties (Debug)"))
                    {
                        GatherBuddy.Log.Information($"=== Recipe Properties for {item.Name} ===");
                        GatherBuddy.Log.Information($"Recipe.RowId: {item.Recipe.RowId}");
                        GatherBuddy.Log.Information($"Recipe.Quest.RowId: {item.Recipe.Quest.RowId}");
                        GatherBuddy.Log.Information($"Recipe.IsSecondary: {item.Recipe.IsSecondary}");
                        GatherBuddy.Log.Information($"Recipe.IsExpert: {item.Recipe.IsExpert}");
                        GatherBuddy.Log.Information($"Recipe.SecretRecipeBook.RowId: {item.Recipe.SecretRecipeBook.RowId}");
                        GatherBuddy.Log.Information($"Recipe.CanQuickSynth: {item.Recipe.CanQuickSynth}");
                        GatherBuddy.Log.Information($"Recipe.CanHq: {item.Recipe.CanHq}");
                        GatherBuddy.Log.Information($"Recipe.IsSpecializationRequired: {item.Recipe.IsSpecializationRequired}");
                        GatherBuddy.Log.Information($"Recipe.DifficultyFactor: {item.Recipe.DifficultyFactor}");
                        GatherBuddy.Log.Information($"Recipe.QualityFactor: {item.Recipe.QualityFactor}");
                        GatherBuddy.Log.Information($"Recipe.RecipeLevelTable.RowId: {item.Recipe.RecipeLevelTable.RowId}");
                        GatherBuddy.Log.Information($"Recipe.RecipeNotebookList.RowId: {item.Recipe.RecipeNotebookList.RowId}");
                        var resultItem = item.Recipe.ItemResult.Value;
                        GatherBuddy.Log.Information($"Item.RowId: {resultItem.RowId}");
                        GatherBuddy.Log.Information($"Item.AlwaysCollectable: {resultItem.AlwaysCollectable}");
                        GatherBuddy.Log.Information($"Item.IsUnique: {resultItem.IsUnique}");
                        GatherBuddy.Log.Information($"Item.IsUntradable: {resultItem.IsUntradable}");
                        GatherBuddy.Log.Information($"Item.ItemSearchCategory.RowId: {resultItem.ItemSearchCategory.RowId}");
                        GatherBuddy.Log.Information($"Item.ItemUICategory.RowId: {resultItem.ItemUICategory.RowId}");
                        GatherBuddy.Log.Information($"Item.Rarity: {resultItem.Rarity}");
                        LogRecipeNotebookDivisionInfo(item.Recipe);
                    }
                    
                    ImGui.Separator();
                    
                    var lists = GatherBuddy.CraftingListManager.Lists;
                    
                    if (lists.Count > 0)
                    {
                        ImGui.TextColored(new Vector4(1.0f, 0.9f, 0.6f, 1.0f), $"Add {item.Name} to list:");
                        ImGui.Separator();
                        
                        foreach (var list in lists)
                        {
                            if (ImGui.MenuItem(list.Name))
                            {
                                list.Recipes.Add(new CraftingListItem(item.Recipe.RowId, 1));
                                GatherBuddy.CraftingListManager.SaveList(list);
                                GatherBuddy.VulcanWindow?.RefreshOpenCraftingList(list.ID);
                                GatherBuddy.Log.Information($"Added {item.Name} to crafting list '{list.Name}'");
                            }
                        }
                        
                        ImGui.Spacing();
                        ImGui.Separator();
                        ImGui.Spacing();
                        
                        ImGui.TextColored(new Vector4(0.6f, 0.9f, 1.0f, 1.0f), "Add all uncrafted (filtered) to:");
                        ImGui.Separator();
                        
                        foreach (var list in lists)
                        {
                            if (ImGui.MenuItem($"{list.Name} (bulk)##bulk_{list.ID}"))
                            {
                                var uncraftedCount = 0;
                                if (_recipeTable != null)
                                {
                                    foreach (var (recipe, _) in _recipeTable.GetFilteredItems())
                                    {
                                        if (!recipe.IsCrafted)
                                        {
                                            list.Recipes.Add(new CraftingListItem(recipe.Recipe.RowId, 1));
                                            uncraftedCount++;
                                        }
                                    }
                                }
                                GatherBuddy.CraftingListManager.SaveList(list);
                                GatherBuddy.VulcanWindow?.RefreshOpenCraftingList(list.ID);
                                GatherBuddy.Log.Information($"Added {uncraftedCount} uncrafted recipes to list '{list.Name}'");
                            }
                        }
                    }
                    else
                    {
                        ImGui.TextDisabled("No crafting lists available");
                        ImGui.TextDisabled("Create one in the Crafting Lists tab");
                    }
                    
                    ImGui.EndPopup();
                }

                if (selected)
                {
                    SwitchJobIfNeeded(item.JobId);
                    StartCraftWithRaphael(item.Recipe);
                }

                if (hovered)
                {
                    item.SetTooltip(IconSize);
                }
            }

            public override bool FilterFunc(ExtendedRecipe item)
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

                if (item.Level < _minLevel || item.Level > _maxLevel)
                    return false;

                if (!PassesRecipeTypeFilters(item.Recipe))
                    return false;

                return true;
            }
        }

        private sealed class JobColumn : ColumnString<ExtendedRecipe>
        {
            public override string ToName(ExtendedRecipe item)
                => item.JobAbbreviation;

            public override float Width
                => _jobColumnWidth * ImGuiHelpers.GlobalScale;

            public override bool DrawFilter()
            {
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(Label);
                return false;
            }

            public override void DrawColumn(ExtendedRecipe item, int _)
            {
                ImGui.Text(item.JobAbbreviation);
            }

            public override int Compare(ExtendedRecipe lhs, ExtendedRecipe rhs)
                => lhs.JobId.CompareTo(rhs.JobId);
        }

        private sealed class LevelColumn : Column<ExtendedRecipe>
        {
            public override float Width
                => _levelColumnWidth * ImGuiHelpers.GlobalScale;

            public override void DrawColumn(ExtendedRecipe item, int _)
            {
                ImGui.Text(item.Level.ToString());
            }

            public override int Compare(ExtendedRecipe lhs, ExtendedRecipe rhs)
                => lhs.Level.CompareTo(rhs.Level);
        }

        private sealed class CraftedColumn : Column<ExtendedRecipe>
        {
            public override float Width
                => _craftedColumnWidth * ImGuiHelpers.GlobalScale;

            public override void DrawColumn(ExtendedRecipe item, int _)
            {
                using var font = ImRaii.PushFont(UiBuilder.IconFont);
                if (item.IsCrafted)
                {
                    using var color = ImRaii.PushColor(ImGuiCol.Text, 0xFF008000);
                    ImGuiUtil.Center(FontAwesomeIcon.Check.ToIconString());
                }
                else
                {
                    using var color = ImRaii.PushColor(ImGuiCol.Text, 0xFF000080);
                    ImGuiUtil.Center(FontAwesomeIcon.Times.ToIconString());
                }
            }

            public override int Compare(ExtendedRecipe lhs, ExtendedRecipe rhs)
            {
                if (lhs.IsCrafted != rhs.IsCrafted)
                    return rhs.IsCrafted.CompareTo(lhs.IsCrafted);
                return 0;
            }
        }


    }
}
