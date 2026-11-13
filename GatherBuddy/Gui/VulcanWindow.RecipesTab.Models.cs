using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using FFXIVClientStructs.FFXIV.Client.Game;
using GatherBuddy.Crafting;
using Lumina.Excel.Sheets;
using RebornMaterial;
using System;
using System.Numerics;
using ImRaii = Dalamud.Interface.Utility.Raii.ImRaii;

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
            {
                UpdateBasicInfo();
            }
            else
            {
                Update();
            }
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
                {
                    JobAbbreviation = job.Abbreviation.ExtractText();
                }
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
                {
                    JobAbbreviation = job.Abbreviation.ExtractText();
                }
            }

            _isFullyLoaded = true;

            UpdateCraftedStatus();
        }

        public unsafe void SetTooltip(Vector2 iconSize)
        {
            using var tooltip = ImRaii.Tooltip();
            using (ImRaii.PushFont(M3.TitleMedium))
            {
                ImGui.TextUnformatted(Name);
            }

            M3Text.Draw($"Level {Level} {JobAbbreviation}, makes {Recipe.AmountResult}, recipe {Recipe.RowId}", M3TextStyle.Muted);
            M3Widgets.Divider(4f);

            var directIngredients = RecipeManager.GetIngredients(Recipe);
            if (directIngredients.Count > 0)
            {
                M3Widgets.SectionLabel("Ingredients");
                var itemSheet = Dalamud.GameData.GetExcelSheet<Item>();
                var size = iconSize.Y * 0.75f;
                foreach (var (itemId, needed) in directIngredients)
                {
                    if (itemSheet == null || !itemSheet.TryGetRow(itemId, out var item))
                    {
                        continue;
                    }

                    var have = GetInventoryCount(itemId);
                    var rowStart = ImGui.GetCursorScreenPos();
                    M3Widgets.GameIcon(item.Icon, size);
                    ImGui.SameLine(0, M3.Space2);
                    ImGui.SetCursorScreenPos(new Vector2(ImGui.GetCursorScreenPos().X, rowStart.Y + (size - ImGui.GetTextLineHeight()) * 0.5f));
                    ImGui.TextUnformatted(item.Name.ExtractText());
                    ImGui.SameLine(0, M3.Space2);
                    M3Text.Draw($"{have} / {needed}", have >= needed ? M3TextStyle.Success : M3TextStyle.Error);
                }
            }
            else
            {
                M3Text.Draw("No materials required", M3TextStyle.Faint);
            }

            var settings = GatherBuddy.RecipeBrowserSettings.Get(Recipe.RowId);
            if (settings == null || !settings.HasAnySettings())
            {
                return;
            }

            M3Widgets.Divider(4f);
            M3Widgets.SectionLabel("Craft settings");
            var sheet = Dalamud.GameData.GetExcelSheet<Item>();
            if (sheet == null)
            {
                return;
            }

            void Setting(string label, uint? id)
            {
                if (id.HasValue && sheet.TryGetRow(id.Value, out var row))
                {
                    M3Text.Draw($"{label}: {row.Name.ExtractText()}");
                }
            }

            Setting("Food", settings.FoodItemId);
            Setting("Medicine", settings.MedicineItemId);
            Setting("Manual", settings.ManualItemId);
            Setting("Squadron manual", settings.SquadronManualItemId);
        }
        private static unsafe int GetInventoryCount(uint itemId)
        {
            try
            {
                var inventory = InventoryManager.Instance();
                if (inventory == null)
                {
                    return 0;
                }

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
                    {
                        continue;
                    }

                    for (var i = 0; i < container->Size; i++)
                    {
                        var item = container->GetInventorySlot(i);
                        if (item == null || item->ItemId == 0)
                        {
                            continue;
                        }

                        if (item->ItemId == baseItemId || item->ItemId == hqItemId)
                        {
                            total += (int)item->Quantity;
                        }
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
}
