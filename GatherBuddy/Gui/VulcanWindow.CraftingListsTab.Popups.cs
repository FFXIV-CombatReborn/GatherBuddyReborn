using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using GatherBuddy.Crafting;
using RebornMaterial;
using System.Numerics;

namespace GatherBuddy.Gui;

public partial class VulcanWindow
{
    private const string CreateListDialogId = "New crafting list###gbr_create_crafting_list";
    private const string CreateFolderDialogId = "New crafting list folder###gbr_create_crafting_folder";
    private const string ImportListDialogId = "Import crafting list###gbr_import_crafting_list";
    private const string EphemeralSupporting = "Delete this list automatically after crafting completes. You can turn this off later in the list editor.";

    private string _newListName = string.Empty;
    private bool _newListEphemeral = false;
    private string _newListFolderPath = string.Empty;
    private uint? _newListRecipeId = null;
    private string _newListRecipeName = string.Empty;
    private string _newFolderName = string.Empty;
    private string _newFolderParentPath = string.Empty;
    private string _importListText = string.Empty;
    private bool _importListEphemeral = false;
    private string? _importListError = null;

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

    private static bool SubmittedWithEnter()
        => ImGui.IsItemDeactivated() && (ImGui.IsKeyPressed(ImGuiKey.Enter) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter));

    private void DrawImportListPopup()
    {
        if (!ImGui.IsPopupOpen(ImportListDialogId))
        {
            return;
        }

        using var dialog = M3Dialog.Begin(ImportListDialogId, "Import a list", "Paste a list export string from Vulcan below.", FontAwesomeIcon.None,
            520f);
        if (!dialog.IsOpen)
        {
            return;
        }

        if (ImGui.IsWindowAppearing())
        {
            ImGui.SetKeyboardFocusHere();
        }

        var width = ImGui.GetContentRegionAvail().X;
        ImGui.InputTextMultiline("##importListText", ref _importListText, 65536, new Vector2(width, VulcanUiScaling.Scaled(120f)));
        if (_importListError != null)
        {
            ImGui.Dummy(new Vector2(0f, M3.Space1));
            M3Widgets.Banner("##import_list_error", _importListError, M3Severity.Error, FontAwesomeIcon.ExclamationCircle);
        }

        ImGui.Dummy(new Vector2(0f, M3.Space1));
        M3Widgets.RowSwitch("Ephemeral##importListEphemeral", ref _importListEphemeral, EphemeralSupporting);

        var pressed = M3Dialog.Actions(
        [
            new M3DialogAction("Cancel"),
            new M3DialogAction("Import", M3ButtonStyle.Filled, FontAwesomeIcon.FileImport, !string.IsNullOrWhiteSpace(_importListText)),
        ], false);
        if (pressed == 0)
        {
            ClearImportListState();
            ImGui.CloseCurrentPopup();
            return;
        }

        if (pressed != 1)
        {
            return;
        }

        var (imported, error) = GatherBuddy.CraftingListManager.ImportList(_importListText);
        if (imported == null)
        {
            _importListError = error ?? "That doesn't look like a list export string.";
            return;
        }

        if (_importListEphemeral)
        {
            imported.Ephemeral = true;
            GatherBuddy.CraftingListManager.SaveList(imported);
        }

        OpenCraftingList(imported);
        _deferEditorDraw = true;
        ClearImportListState();
        ImGui.CloseCurrentPopup();
        M3Snackbar.Show($"Imported {imported.Name}.", severity: M3Severity.Success);
    }

    private void ClearImportListState()
    {
        _importListText = string.Empty;
        _importListEphemeral = false;
        _importListError = null;
    }

    private void DrawCreateListPopup()
    {
        if (!ImGui.IsPopupOpen(CreateListDialogId))
        {
            return;
        }

        var supporting = string.IsNullOrEmpty(_newListFolderPath) ? null : $"In {CraftingListManager.FormatFolderPath(_newListFolderPath)}";
        using var dialog = M3Dialog.Begin(CreateListDialogId, "New crafting list", supporting, FontAwesomeIcon.None, 420f);
        if (!dialog.IsOpen)
        {
            return;
        }

        if (_newListRecipeId.HasValue)
        {
            M3Widgets.Pill("##new_list_recipe", $"Starts with {_newListRecipeName}", M3.Scheme.Primary, FontAwesomeIcon.Hammer);
            ImGui.Dummy(new Vector2(0f, M3.Space1));
        }

        if (ImGui.IsWindowAppearing())
        {
            ImGui.SetKeyboardFocusHere();
        }

        M3TextField.Draw("##newListName", "List name", ref _newListName, ImGui.GetContentRegionAvail().X, 256);
        var submitted = SubmittedWithEnter();
        if (!string.IsNullOrWhiteSpace(_newListName) && !GatherBuddy.CraftingListManager.IsNameUnique(_newListName))
        {
            M3Text.Draw("A list with this name already exists. It will be renamed automatically.", M3TextStyle.Warning, true);
        }

        ImGui.Dummy(new Vector2(0f, M3.Space1));
        M3Widgets.RowSwitch("Ephemeral##newListEphemeral", ref _newListEphemeral, EphemeralSupporting);

        var canCreate = !string.IsNullOrWhiteSpace(_newListName);
        var pressed = M3Dialog.Actions(
        [
            new M3DialogAction("Cancel"),
            new M3DialogAction("Create", M3ButtonStyle.Filled, FontAwesomeIcon.Check, canCreate),
        ], false);
        if (pressed == 0)
        {
            ResetCreateListPopupState();
            ImGui.CloseCurrentPopup();
            return;
        }

        if (pressed != 1 && !(submitted && canCreate))
        {
            return;
        }

        var newList = GatherBuddy.CraftingListManager.CreateNewList(_newListName, _newListEphemeral, _newListFolderPath);
        if (_newListRecipeId.HasValue)
        {
            newList.AddRecipe(_newListRecipeId.Value, 1);
            if (!GatherBuddy.CraftingListManager.SaveList(newList))
            {
                GatherBuddy.Log.Warning($"[VulcanWindow] Failed to save list '{newList.Name}' after adding {_newListRecipeName}");
            }
            else
            {
                RaphaelAssessmentService.QueueWarmupForAddedListRecipe(_newListRecipeId.Value, newList);
                GatherBuddy.Log.Information($"[VulcanWindow] Created list '{newList.Name}' and added {_newListRecipeName}");
            }
        }

        OpenCraftingList(newList);
        ResetCreateListPopupState();
        ImGui.CloseCurrentPopup();
    }

    private void DrawCreateFolderPopup()
    {
        if (!ImGui.IsPopupOpen(CreateFolderDialogId))
        {
            return;
        }

        var supporting = string.IsNullOrEmpty(_newFolderParentPath) ? null : $"Inside {CraftingListManager.FormatFolderPath(_newFolderParentPath)}";
        using var dialog = M3Dialog.Begin(CreateFolderDialogId, "New folder", supporting, FontAwesomeIcon.None, 400f);
        if (!dialog.IsOpen)
        {
            return;
        }

        if (ImGui.IsWindowAppearing())
        {
            ImGui.SetKeyboardFocusHere();
        }

        M3TextField.Draw("##newFolderName", "Folder name", ref _newFolderName, ImGui.GetContentRegionAvail().X, 256);
        var submitted = SubmittedWithEnter();
        var isAvailable = GatherBuddy.CraftingListManager.IsFolderNameAvailable(_newFolderName, _newFolderParentPath);
        if (!string.IsNullOrWhiteSpace(_newFolderName) && !isAvailable)
        {
            M3Text.Draw("Folder names must be unique within the current location and cannot contain / or \\.", M3TextStyle.Error, true);
        }

        var pressed = M3Dialog.Actions(
        [
            new M3DialogAction("Cancel"),
            new M3DialogAction("Create", M3ButtonStyle.Filled, FontAwesomeIcon.Check, isAvailable),
        ], false);
        if (pressed == 0)
        {
            ResetCreateFolderPopupState();
            ImGui.CloseCurrentPopup();
            return;
        }

        if ((pressed == 1 || submitted && isAvailable) && GatherBuddy.CraftingListManager.CreateFolder(_newFolderName, _newFolderParentPath))
        {
            ResetCreateFolderPopupState();
            ImGui.CloseCurrentPopup();
        }
    }
}
