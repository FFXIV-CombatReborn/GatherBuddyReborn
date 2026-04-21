using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using GatherBuddy.Crafting;
using GatherBuddy.Plugin;
using Lumina.Excel.Sheets;

namespace GatherBuddy.Gui;

public partial class VulcanWindow
{
    private void DrawTeamCraftImportWindow()
    {
        if (!_showTeamCraftImport)
            return;
        ImGui.SetNextWindowSize(_teamCraftImportWindowSize, ImGuiCond.Appearing);
        var isOpen = _showTeamCraftImport;
        var drawWindow = ImGui.Begin("TeamCraft Import###TCImport", ref isOpen, ImGuiWindowFlags.NoCollapse);
        _showTeamCraftImport = isOpen;

        var currentWindowSize = NormalizeTeamCraftImportWindowSize(ImGui.GetWindowSize());
        if (HasTeamCraftImportWindowSizeChanged(currentWindowSize, _teamCraftImportWindowSize))
        {
            _teamCraftImportWindowSize = currentWindowSize;
            _teamCraftImportWindowSizeDirty = true;
        }

        if (_teamCraftImportWindowSizeDirty && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
            SaveTeamCraftImportWindowSize();

        if (!drawWindow)
        {
            if (!_showTeamCraftImport)
                SaveTeamCraftImportWindowSize(true);
            ImGui.End();
            return;
        }

        ImGui.TextColored(ImGuiColors.DalamudGrey3, "Open your list on TeamCraft, copy the 'Final Items' section using");
        ImGui.TextColored(ImGuiColors.DalamudGrey3, "'Copy as Text', then paste below. Precrafts are generated automatically.");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        var footerHeight = ImGui.GetFrameHeightWithSpacing() + ImGui.GetStyle().ItemSpacing.Y * 3f + 2f;
        ImGui.BeginChild("##teamCraftImportContent", new Vector2(0, -footerHeight), false);
        ImGui.Text("List Name:");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##ImportListName", ref _teamCraftListName, 256);

        ImGui.Spacing();
        ImGui.Checkbox("Ephemeral##teamCraftEphemeral", ref _teamCraftEphemeral);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Delete this list automatically after crafting completes.\nCan be disabled later in the list editor.");

        ImGui.Spacing();
        ImGui.Text("Final Items:");
        var finalItemsHeight = Math.Max(150f, ImGui.GetContentRegionAvail().Y);
        ImGui.InputTextMultiline("##FinalItems", ref _teamCraftFinalItems, 500000, new Vector2(-1, finalItemsHeight));
        ImGui.EndChild();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button("Import", new Vector2(100, 0)))
        {
            var importedList = ParseTeamCraftImport(_teamCraftEphemeral);
            if (importedList != null)
            {
                DisposeListEditor();
                _editingList = importedList;
                _listEditor  = new CraftingListEditor(importedList);
                _listEditor.OnStartCrafting = l => { StartCraftingList(l); MinimizeWindow(); };
                _listEditor.RefreshInventoryCounts();
                GatherBuddy.CraftingMaterialsWindow?.SetEditor(_listEditor);
                _deferEditorDraw = true;

                _teamCraftListName   = string.Empty;
                _teamCraftFinalItems = string.Empty;
                _teamCraftEphemeral  = false;
                _showTeamCraftImport = false;

                GatherBuddy.Log.Information($"[VulcanWindow] Successfully imported TeamCraft list: {importedList.Name}");
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(100, 0)))
        {
            _teamCraftListName   = string.Empty;
            _teamCraftFinalItems = string.Empty;
            _teamCraftEphemeral  = false;
            _showTeamCraftImport = false;
        }
        if (!_showTeamCraftImport)
            SaveTeamCraftImportWindowSize(true);

        ImGui.End();
    }

    private static Vector2 NormalizeTeamCraftImportWindowSize(Vector2 size)
        => size.X > 0 && size.Y > 0 ? size : DefaultTeamCraftImportWindowSize;

    private static bool HasTeamCraftImportWindowSizeChanged(Vector2 lhs, Vector2 rhs)
        => MathF.Abs(lhs.X - rhs.X) > 0.5f || MathF.Abs(lhs.Y - rhs.Y) > 0.5f;

    private void SaveTeamCraftImportWindowSize(bool force = false)
    {
        if (!force && !_teamCraftImportWindowSizeDirty)
            return;

        var normalizedSize = NormalizeTeamCraftImportWindowSize(_teamCraftImportWindowSize);
        if (HasTeamCraftImportWindowSizeChanged(GatherBuddy.Config.TeamCraftImportWindowSize, normalizedSize))
        {
            GatherBuddy.Config.TeamCraftImportWindowSize = normalizedSize;
            GatherBuddy.Config.Save();
            GatherBuddy.Log.Debug($"[VulcanWindow] Saved TeamCraft import window size: {normalizedSize.X}x{normalizedSize.Y}");
        }

        _teamCraftImportWindowSizeDirty = false;
    }

    private CraftingListDefinition? ParseTeamCraftImport(bool ephemeral = false)
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

        var newList = GatherBuddy.CraftingListManager.CreateNewList(listName, ephemeral);

        foreach (var (recipeId, quantity) in recipesToAdd)
        {
            var existingItem = newList.Recipes.FirstOrDefault(r => r.RecipeId == recipeId);
            if (existingItem != null)
                existingItem.Quantity += quantity;
            else
                newList.Recipes.Add(new CraftingListItem(recipeId, quantity));
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

            if (!parts[0].EndsWith('x'))
                continue;

            if (!int.TryParse(parts[0].Substring(0, parts[0].Length - 1), out var numberOfItems))
                continue;

            var itemName = string.Join(" ", parts.Skip(1)).Trim();
            GatherBuddy.Log.Debug($"[VulcanWindow] TeamCraft import: Parsing {numberOfItems}x {itemName}");

            var recipeSheet = Dalamud.GameData.GetExcelSheet<Recipe>();
            if (recipeSheet == null)
                continue;

            Recipe? foundRecipe = null;
            foreach (var recipe in recipeSheet)
            {
                if (recipe.ItemResult.RowId > 0 && recipe.ItemResult.Value.Name.ExtractText() == itemName)
                {
                    foundRecipe = recipe;
                    break;
                }
            }

            if (foundRecipe != null)
            {
                var craftsNeeded = (int)Math.Ceiling(numberOfItems / (double)foundRecipe.Value.AmountResult);
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
