using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using ElliLib.Raii;
using GatherBuddy.Crafting;
using GatherBuddy.Plugin;
using ImRaii = ElliLib.Raii.ImRaii;

namespace GatherBuddy.Gui;

public partial class VulcanWindow
{
    private string  _newListName         = string.Empty;
    private bool    _newListEphemeral    = false;
    private string  _newListFolderPath   = string.Empty;
    private uint?   _newListRecipeId     = null;
    private string  _newListRecipeName   = string.Empty;
    private string  _newFolderName       = string.Empty;
    private string  _newFolderParentPath = string.Empty;
    private string  _importListText      = string.Empty;
    private bool    _importListEphemeral = false;
    private string? _importListError     = null;

    private void ResetCreateListPopupState()
    {
        _newListName = string.Empty;
        _newListEphemeral = false;
        _newListFolderPath = string.Empty;
        _newListRecipeId = null;
        _newListRecipeName = string.Empty;
        _openCreateListPopup = false;
    }

    private void ResetCreateFolderPopupState()
    {
        _newFolderName = string.Empty;
        _newFolderParentPath = string.Empty;
        _openCreateFolderPopup = false;
    }

    private void DrawImportListPopup()
    {
        ImGui.SetNextWindowSize(new Vector2(540, 260), ImGuiCond.FirstUseEver);
        if (!ImGui.BeginPopupModal("ImportListPopup", ImGuiWindowFlags.None))
            return;

        ImGui.TextWrapped("Paste an exported list string below and click Import.");
        ImGui.Spacing();
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextMultiline("##importListText", ref _importListText, 65536, new Vector2(-1, 120));

        if (_importListError != null)
        {
            ImGui.Spacing();
            ImGui.TextColored(ImGuiColors.DalamudRed, _importListError);
        }

        ImGui.Spacing();
        ImGui.Checkbox("Ephemeral##importListEphemeral", ref _importListEphemeral);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Delete this list automatically after crafting completes.\nCan be disabled later in the list editor.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        using (ImRaii.Disabled(string.IsNullOrWhiteSpace(_importListText)))
        {
            if (ImGui.Button("Import", new Vector2(120, 0)))
            {
                var (imported, error) = GatherBuddy.CraftingListManager.ImportList(_importListText);
                if (imported != null)
                {
                    if (_importListEphemeral)
                    {
                        imported.Ephemeral = true;
                        GatherBuddy.CraftingListManager.SaveList(imported);
                    }

                    OpenCraftingList(imported);
                    _deferEditorDraw    = true;
                    _importListText     = string.Empty;
                    _importListEphemeral = false;
                    _importListError    = null;
                    ImGui.CloseCurrentPopup();
                }
                else
                {
                    _importListError = error;
                }
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(100, 0)))
        {
            _importListText     = string.Empty;
            _importListEphemeral = false;
            _importListError    = null;
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private void DrawCreateListPopup()
    {
        if (!ImGui.BeginPopupModal("CreateListPopup", ImGuiWindowFlags.AlwaysAutoResize))
            return;

        if (!string.IsNullOrEmpty(_newListFolderPath))
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey3, $"Folder: {CraftingListManager.FormatFolderPath(_newListFolderPath)}");
            ImGui.Spacing();
        }

        if (_newListRecipeId.HasValue)
        {
            ImGui.TextColored(ImGuiColors.ParsedGold, $"Add recipe: {_newListRecipeName}");
            ImGui.Spacing();
        }

        ImGui.Text("Enter list name:");
        ImGui.InputText("##newListName", ref _newListName, 256);

        if (!string.IsNullOrWhiteSpace(_newListName) && !GatherBuddy.CraftingListManager.IsNameUnique(_newListName))
        {
            ImGui.TextColored(new Vector4(1, 0.7f, 0, 1), "A list with this name already exists.");
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1), "It will be renamed automatically.");
        }

        ImGui.Spacing();
        ImGui.Checkbox("Ephemeral##newListEphemeral", ref _newListEphemeral);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Delete this list automatically after crafting completes.\nCan be disabled later in the list editor.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button("Create", new Vector2(100, 0)) && !string.IsNullOrWhiteSpace(_newListName))
        {
            var newList = GatherBuddy.CraftingListManager.CreateNewList(_newListName, _newListEphemeral, _newListFolderPath);
            if (_newListRecipeId.HasValue)
            {
                newList.AddRecipe(_newListRecipeId.Value, 1);
                if (!GatherBuddy.CraftingListManager.SaveList(newList))
                    GatherBuddy.Log.Warning($"[VulcanWindow] Failed to save list '{newList.Name}' after adding {_newListRecipeName}");
                else
                    GatherBuddy.Log.Information($"[VulcanWindow] Created list '{newList.Name}' and added {_newListRecipeName}");
            }

            OpenCraftingList(newList);
            ResetCreateListPopupState();
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(100, 0)))
        {
            ResetCreateListPopupState();
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private void DrawCreateFolderPopup()
    {
        if (!ImGui.BeginPopupModal("CreateFolderPopup", ImGuiWindowFlags.AlwaysAutoResize))
            return;

        if (!string.IsNullOrEmpty(_newFolderParentPath))
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey3, $"Parent: {CraftingListManager.FormatFolderPath(_newFolderParentPath)}");
            ImGui.Spacing();
        }

        ImGui.Text("Enter folder name:");
        ImGui.InputText("##newFolderName", ref _newFolderName, 256);

        var isAvailable = GatherBuddy.CraftingListManager.IsFolderNameAvailable(_newFolderName, _newFolderParentPath);
        if (!string.IsNullOrWhiteSpace(_newFolderName) && !isAvailable)
            ImGui.TextColored(ImGuiColors.DalamudRed, "Folder names must be unique within the current location and cannot contain / or \\.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        using (ImRaii.Disabled(!isAvailable))
        {
            if (ImGui.Button("Create", new Vector2(100, 0)))
            {
                if (GatherBuddy.CraftingListManager.CreateFolder(_newFolderName, _newFolderParentPath))
                {
                    ResetCreateFolderPopupState();
                    ImGui.CloseCurrentPopup();
                }
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(100, 0)))
        {
            ResetCreateFolderPopupState();
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }
}
