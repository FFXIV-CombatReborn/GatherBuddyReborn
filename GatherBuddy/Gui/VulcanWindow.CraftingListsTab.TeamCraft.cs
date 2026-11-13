using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using GatherBuddy.Crafting;
using Lumina.Excel.Sheets;
using RebornMaterial;
using System;
using System.Collections.Generic;
using System.Numerics;
using ImRaii = Dalamud.Interface.Utility.Raii.ImRaii;

namespace GatherBuddy.Gui;

public partial class VulcanWindow
{
    internal static RebornUI.HostedPanelWindow CreateTeamCraftImportWindow()
        => new("GatherBuddyTeamCraftImport", "TeamCraft Import", new Vector2(540f, 500f), new Vector2(420f, 400f), bodyScrolls: false);

    private void OpenTeamCraftImport()
    {
        _teamCraftImportError = null;
        GatherBuddy.TeamCraftImportWindow?.Show(this, DrawTeamCraftImport);
    }

    private void DrawTeamCraftImport()
    {
        M3Text.Draw("Open your list on TeamCraft, copy the 'Final Items' section with 'Copy as Text', and paste it below. Precrafts are added automatically.",
            M3TextStyle.Muted, true);
        ImGui.Dummy(new Vector2(0f, M3.Space1));
        var footerHeight = M3Widgets.ButtonHeight + M3.Space3 + ImGui.GetStyle().ItemSpacing.Y;
        using (var content = ImRaii.Child("##teamCraftImportContent", new Vector2(0, -footerHeight), false))
        {
            if (content)
            {
                DrawTeamCraftImportForm();
            }
        }

        ImGui.Dummy(new Vector2(0f, M3.Space1));
        if (M3Widgets.Button("##teamCraftImport", "Import", M3ButtonStyle.Filled, FontAwesomeIcon.FileImport,
                enabled: !string.IsNullOrWhiteSpace(_teamCraftFinalItems)))
        {
            var importedList = ParseTeamCraftImport(_teamCraftEphemeral);
            if (importedList != null)
            {
                DisposeListEditor();
                _editingList = importedList;
                _listEditor = new CraftingListEditor(importedList)
                {
                    OnStartCrafting = l => { StartCraftingList(l); MinimizeWindow(); }
                };
                _listEditor.RefreshInventoryCounts();
                GatherBuddy.CraftingMaterialsWindow?.SetEditor(_listEditor);
                GatherBuddy.CraftingTreeWindow?.SetEditor(_listEditor);
                _deferEditorDraw = true;

                ResetTeamCraftImportState();
                OpenToCraftingLists();
                M3Snackbar.Show($"Imported {importedList.Name} from TeamCraft.", severity: M3Severity.Success);
                GatherBuddy.Log.Information($"[VulcanWindow] Successfully imported TeamCraft list: {importedList.Name}");
            }
            else
            {
                _teamCraftImportError = "None of those items have a recipe. Check that you copied the 'Final Items' section.";
            }
        }

        ImGui.SameLine();
        if (M3Widgets.Button("##teamCraftCancel", "Cancel", M3ButtonStyle.Text))
        {
            ResetTeamCraftImportState();
        }
    }

    private void DrawTeamCraftImportForm()
    {
        var width = ImGui.GetContentRegionAvail().X;
        M3Text.Draw("List name", M3TextStyle.Label);
        M3TextField.Draw("##ImportListName", "Imported from TeamCraft", ref _teamCraftListName, width, 256);
        ImGui.Dummy(new Vector2(0f, M3.Space1));
        M3Widgets.RowSwitch("Ephemeral##teamCraftEphemeral", ref _teamCraftEphemeral, EphemeralSupporting);

        M3Text.Draw("Final items", M3TextStyle.Label);
        var errorHeight = _teamCraftImportError != null ? ImGui.GetTextLineHeight() * 2f + 32f * M3.Scale : 0f;
        var finalItemsHeight = Math.Max(VulcanUiScaling.Scaled(150f), ImGui.GetContentRegionAvail().Y - errorHeight);
        if (ImGui.InputTextMultiline("##FinalItems", ref _teamCraftFinalItems, 500000, new Vector2(-1, finalItemsHeight)))
        {
            _teamCraftImportError = null;
        }

        if (_teamCraftImportError != null)
        {
            ImGui.Dummy(new Vector2(0f, M3.Space1));
            M3Widgets.Banner("##teamCraftImportError", _teamCraftImportError, M3Severity.Error, FontAwesomeIcon.ExclamationCircle);
        }
    }

    private void ResetTeamCraftImportState()
    {
        _teamCraftListName = string.Empty;
        _teamCraftFinalItems = string.Empty;
        _teamCraftEphemeral = false;
        _teamCraftImportError = null;
        GatherBuddy.TeamCraftImportWindow?.Hide(this);
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
            var existingItem = newList.Recipes.Find(r => r.RecipeId == recipeId);
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

    private static void ParseTeamCraftSection(string text, List<(uint recipeId, int quantity)> output)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        using var reader = new System.IO.StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                continue;
            }

            if (!parts[0].EndsWith('x'))
            {
                continue;
            }

            if (!int.TryParse(parts[0].AsSpan(0, parts[0].Length - 1), out var numberOfItems))
            {
                continue;
            }

            var itemName = string.Join(" ", parts, 1, parts.Length - 1).Trim();
            GatherBuddy.Log.Debug($"[VulcanWindow] TeamCraft import: Parsing {numberOfItems}x {itemName}");

            var recipeSheet = Dalamud.GameData.GetExcelSheet<Recipe>();
            if (recipeSheet == null)
            {
                continue;
            }

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
