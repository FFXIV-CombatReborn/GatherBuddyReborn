using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Textures;
using ElliLib;
using ElliLib.Raii;
using FFXIVClientStructs.FFXIV.Client.Game;
using GatherBuddy.Crafting;
using GatherBuddy.Plugin;
using Lumina.Excel.Sheets;
using ImRaii = ElliLib.Raii.ImRaii;

namespace GatherBuddy.Gui;

public partial class VulcanWindow
{
    private void DrawCraftingListsTab()
    {
        IDisposable tabItem;
        bool tabOpen;

        if (GatherBuddy.ControllerSupport != null && !_craftingListsRequestFocus)
        {
            var handle = GatherBuddy.ControllerSupport.TabNavigation.TabItem("Crafting Lists##craftingListsTab", 0, 9);
            tabItem = handle;
            tabOpen = handle;
        }
        else
        {
            ImRaii.IEndObject handle;
            if (_craftingListsRequestFocus)
            {
                bool dummy = true;
                handle = ImRaii.TabItem("Crafting Lists##craftingListsTab", ref dummy, ImGuiTabItemFlags.SetSelected);
            }
            else
            {
                handle = ImRaii.TabItem("Crafting Lists##craftingListsTab");
            }
            tabItem = handle;
            tabOpen = handle.Success;
            if (tabOpen)
                _craftingListsRequestFocus = false;
        }

        using (tabItem)
        {
            if (!tabOpen)
                return;

            DrawCraftingListsTabContent();
        }
    }
    
    private void DrawCraftingListsTabContent()
    {
        if (_openCreateListPopup)
        {
            ImGui.OpenPopup("CreateListPopup");
            _openCreateListPopup = false;
        }
        if (_openCreateFolderPopup)
        {
            ImGui.OpenPopup("CreateFolderPopup");
            _openCreateFolderPopup = false;
        }

        if (_editingList != null && _listEditor != null)
        {
            if (_deferEditorDraw)
            {
                _deferEditorDraw = false;
                ImGui.Text("Loading...");
            }
            else
            {
                var refreshedList = GatherBuddy.CraftingListManager.GetListByID(_editingList.ID);
                if (refreshedList == null)
                {
                    _editingList = null;
                    DisposeListEditor();
                    DrawListManager();
                }
                else
                {
                    _editingList = refreshedList;

                    if (ImGui.SmallButton("\u2190 Lists##backToLists"))
                    {
                        _editingList = null;
                        DisposeListEditor();
                        DrawListManager();
                    }
                    else
                    {
                        ImGui.Spacing();
                        ImGui.TextColored(ImGuiColors.ParsedGold, _editingList.Name);
                        if (_editingList.Ephemeral)
                        {
                            var ephemeral = _editingList.Ephemeral;
                            if (ImGui.Checkbox("Ephemeral##listHeaderEphemeral", ref ephemeral))
                            {
                                _editingList.Ephemeral = ephemeral;
                                GatherBuddy.CraftingListManager.SaveList(_editingList);
                            }
                            if (ImGui.IsItemHovered())
                                ImGui.SetTooltip("Automatically delete this list after crafting completes. Has no effect if stopped manually.");
                        }
                        else
                        {
                            ImGui.TextColored(ImGuiColors.DalamudGrey3, "Crafting List");
                        }
                        ImGui.Separator();
                        ImGui.Spacing();

                        if (_listEditor != null)
                            _listEditor.Draw();
                    }
                }
            }
        }
        else
        {
            DrawListManager();
        }

        DrawCreateListPopup();
        DrawCreateFolderPopup();
        DrawImportListPopup();
        DrawTeamCraftImportWindow();
    }

    private void DrawListManager()
    {
        ImGui.TextColored(ImGuiColors.DalamudYellow, "Crafting Lists");
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button("Create New List", new Vector2(115, 0)))
        {
            PrepareCreateListPopup();
            ImGui.OpenPopup("CreateListPopup");
        }
        ImGui.SameLine();
        if (ImGui.Button("New Folder", new Vector2(95, 0)))
            QueueCreateFolderPopup();
        ImGui.SameLine();
        if (ImGui.Button("TeamCraft Import", new Vector2(115, 0)))
            _showTeamCraftImport = true;
        ImGui.SameLine();
        if (ImGui.Button("Import List", new Vector2(95, 0)))
        {
            _importListText  = string.Empty;
            _importListError = null;
            ImGui.OpenPopup("ImportListPopup");
        }

        ImGui.Spacing();

        var avail  = ImGui.GetContentRegionAvail();
        var leftW  = 220f;
        var rightW = avail.X - leftW - ImGui.GetStyle().ItemSpacing.X;

        using (ImRaii.PushColor(ImGuiCol.ChildBg, new Vector4(0.08f, 0.08f, 0.10f, 1.00f)))
        {
            ImGui.BeginChild("##listSelectorPanel", new Vector2(leftW, avail.Y), true);
            DrawListSelectorPanel();
            ImGui.EndChild();
        }

        ImGui.SameLine();

        using (ImRaii.PushColor(ImGuiCol.ChildBg, new Vector4(0.08f, 0.08f, 0.10f, 1.00f)))
        {
            ImGui.BeginChild("##listPreviewPanel", new Vector2(rightW, avail.Y), true);
            DrawListPreviewPanel();
            ImGui.EndChild();
        }

    }

    private void DrawListSelectorPanel()
    {
        var rootFolders = GatherBuddy.CraftingListManager.GetDirectSubfolderPaths();
        var rootLists = GatherBuddy.CraftingListManager.GetListsInFolder();
        if (rootFolders.Count == 0 && rootLists.Count == 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(ImGuiColors.DalamudGrey, "No lists yet.");
            ImGui.TextColored(ImGuiColors.DalamudGrey, "Click 'Create New List' to get started.");
            return;
        }
        foreach (var folderPath in rootFolders)
            DrawListFolderNode(folderPath);

        foreach (var list in rootLists)
            DrawCraftingListSelectorEntry(list);
    }

    private void DrawListFolderNode(string folderPath)
    {
        var childFolders = GatherBuddy.CraftingListManager.GetDirectSubfolderPaths(folderPath);
        var childLists = GatherBuddy.CraftingListManager.GetListsInFolder(folderPath);
        var hasChildren = childFolders.Count > 0 || childLists.Count > 0;

        var flags = ImGuiTreeNodeFlags.SpanAvailWidth;
        if (!hasChildren)
            flags |= ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen;

        var label = $"{CraftingListManager.GetFolderDisplayName(folderPath)}##folder_{folderPath}";
        var open = ImGui.TreeNodeEx(label, flags);
        if (ImGui.IsItemHovered())
        {
            _previewFolderPath = folderPath;
            _previewList = null;
        }

        var isPopupOpen = GatherBuddy.ControllerSupport != null
            ? GatherBuddy.ControllerSupport.ContextMenu.BeginPopupContextItemWithGamepad($"FolderContextMenu_{folderPath}", Dalamud.GamepadState)
            : ImGui.BeginPopupContextItem($"FolderContextMenu_{folderPath}");
        if (isPopupOpen)
        {
            if (ImGui.Selectable("Create New List Here"))
            {
                PrepareCreateListPopup(folderPath);
                _openCreateListPopup = true;
                GatherBuddy.Log.Debug($"[VulcanWindow] Queued Create List popup for folder '{folderPath}'");
            }

            if (ImGui.Selectable("Create Subfolder"))
                QueueCreateFolderPopup(folderPath);

            var canDelete = GatherBuddy.CraftingListManager.CanDeleteFolder(folderPath);
            using (ImRaii.Disabled(!canDelete))
            {
                if (ImGui.Selectable("Delete Folder"))
                    GatherBuddy.CraftingListManager.DeleteFolder(folderPath);
            }
            if (!canDelete && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Move or delete the lists in this folder before removing it.");

            ImGui.EndPopup();
        }

        if (!hasChildren || !open)
            return;

        foreach (var childFolderPath in childFolders)
            DrawListFolderNode(childFolderPath);

        foreach (var list in childLists)
            DrawCraftingListSelectorEntry(list);

        ImGui.TreePop();
    }

    private void DrawCraftingListSelectorEntry(CraftingListDefinition list)
    {
        var isHighlighted = _previewList?.ID == list.ID;
        if (isHighlighted)
            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.ParsedGold);

        if (ImGui.Selectable($"{list.Name}##list_{list.ID}", isHighlighted))
            OpenCraftingList(list);

        if (isHighlighted)
            ImGui.PopStyleColor();

        if (ImGui.IsItemHovered())
        {
            _previewList = list;
            _previewFolderPath = null;
        }

        var isPopupOpen = GatherBuddy.ControllerSupport != null
            ? GatherBuddy.ControllerSupport.ContextMenu.BeginPopupContextItemWithGamepad($"ListContextMenu_{list.ID}", Dalamud.GamepadState)
            : ImGui.BeginPopupContextItem($"ListContextMenu_{list.ID}");

        if (!isPopupOpen)
            return;

        if (ImGui.Selectable("Edit"))
            OpenCraftingList(list);

        if (ImGui.Selectable("Start"))
            StartCraftingList(list);

        if (ImGui.BeginMenu("Move to Folder"))
        {
            var isRoot = string.IsNullOrEmpty(list.FolderPath);
            if (ImGui.MenuItem("Root", string.Empty, isRoot) && !isRoot)
                GatherBuddy.CraftingListManager.MoveListToFolder(list, null);

            foreach (var folderPath in GatherBuddy.CraftingListManager.GetAllFolderPaths())
            {
                var isCurrentFolder = list.FolderPath.Equals(folderPath, StringComparison.OrdinalIgnoreCase);
                if (ImGui.MenuItem(CraftingListManager.FormatFolderPath(folderPath), string.Empty, isCurrentFolder) && !isCurrentFolder)
                    GatherBuddy.CraftingListManager.MoveListToFolder(list, folderPath);
            }
            ImGui.EndMenu();
        }

        if (ImGui.Selectable("Export to Clipboard"))
        {
            var exported = GatherBuddy.CraftingListManager.ExportList(list.ID);
            if (exported != null)
            {
                ImGui.SetClipboardText(exported);
                GatherBuddy.Log.Information($"[VulcanWindow] Exported list '{list.Name}' to clipboard");
            }
        }

        ImGui.Separator();
        if (ImGui.Selectable("Delete"))
        {
            if (_previewList?.ID == list.ID)
                _previewList = null;
            GatherBuddy.CraftingListManager.DeleteList(list.ID);
        }

        ImGui.EndPopup();
    }

    private void DrawListPreviewPanel()
    {
        if (!string.IsNullOrEmpty(_previewFolderPath))
        {
            DrawFolderPreviewPanel(_previewFolderPath);
            return;
        }
        if (_previewList == null)
        {
            var h = ImGui.GetContentRegionAvail().Y;
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + h / 2f - ImGui.GetTextLineHeight());
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 8);
            ImGui.TextColored(ImGuiColors.DalamudGrey, "Hover over a list or folder to preview it.");
            return;
        }

        var list = GatherBuddy.CraftingListManager.GetListByID(_previewList.ID);
        if (list == null)
        {
            _previewList = null;
            return;
        }
        _previewList = list;

        ImGui.Spacing();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 8);
        ImGui.TextColored(ImGuiColors.ParsedGold, list.Name);

        if (!string.IsNullOrEmpty(list.FolderPath))
        {
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 8);
            ImGui.TextColored(ImGuiColors.DalamudGrey3, $"Folder: {CraftingListManager.FormatFolderPath(list.FolderPath)}");
        }

        if (!string.IsNullOrWhiteSpace(list.Description))
        {
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 8);
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey3))
                ImGui.TextWrapped(list.Description);
        }

        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 8);
        var recipeWord = list.Recipes.Count == 1 ? "recipe" : "recipes";
        ImGui.TextColored(ImGuiColors.DalamudGrey3,
            $"{list.Recipes.Count} {recipeWord}  \u00b7  Created {list.CreatedAt.ToLocalTime():yyyy-MM-dd}");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var style   = ImGui.GetStyle();
        var buttonH = 22f * 2 + style.ItemSpacing.Y * 3 + 4f;
        var listH   = Math.Max(ImGui.GetContentRegionAvail().Y - buttonH, 40f);

        ImGui.BeginChild("##previewRecipeList", new Vector2(-1, listH), false);

        if (list.Recipes.Count == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "No recipes in this list.");
        }
        else
        {
            var iconSz = new Vector2(22f, 22f);
            foreach (var item in list.Recipes)
            {
                var recipe = RecipeManager.GetRecipe(item.RecipeId);
                if (recipe == null) continue;

                var resultItem = recipe.Value.ItemResult.Value;
                var textY = ImGui.GetCursorPosY() + (iconSz.Y - ImGui.GetTextLineHeight()) / 2f;
                var icon = Icons.DefaultStorage.TextureProvider
                    .GetFromGameIcon(new GameIconLookup(resultItem.Icon));
                if (icon.TryGetWrap(out var wrap, out _))
                    ImGui.Image(wrap.Handle, iconSz);
                else
                    ImGui.Dummy(iconSz);

                ImGui.SameLine(0, 6);
                ImGui.SetCursorPosY(textY);
                ImGui.Text(resultItem.Name.ExtractText());
                ImGui.SameLine();
                ImGui.SetCursorPosY(textY);
                ImGui.TextColored(ImGuiColors.DalamudGrey3,
                    $"x{item.Quantity}  ({JobNames[recipe.Value.CraftType.RowId]})");
            }
        }

        ImGui.EndChild();

        ImGui.Separator();
        ImGui.Spacing();

        var halfW = (ImGui.GetContentRegionAvail().X - style.ItemSpacing.X) / 2f;
        if (ImGui.Button("Edit List##previewEdit", new Vector2(halfW, 22)))
            OpenCraftingList(list);
        ImGui.SameLine();
        if (IPCSubscriber.IsReady("Artisan"))
        {
            ImGuiUtil.DrawDisabledButton("Artisan Detected##previewStart", new Vector2(-1, 22),
                "Artisan plugin is loaded. Please unload Artisan to use Vulcan's crafting system.", true);
        }
        else if (ImGui.Button("Start Crafting##previewStart", new Vector2(-1, 22)))
        {
            StartCraftingList(list);
            MinimizeWindow();
        }

        if (ImGui.Button("Export##previewExport", new Vector2(halfW, 22)))
        {
            var exported = GatherBuddy.CraftingListManager.ExportList(list.ID);
            if (exported != null)
            {
                ImGui.SetClipboardText(exported);
                GatherBuddy.Log.Information($"[VulcanWindow] Exported list '{list.Name}' to clipboard");
            }
        }
        ImGui.SameLine();
        using (ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.45f, 0.12f, 0.12f, 1f)))
        {
            if (ImGui.Button("Delete##previewDelete", new Vector2(-1, 22)))
            {
                GatherBuddy.CraftingListManager.DeleteList(list.ID);
                _previewList = null;
            }
        }
    }

    private void DrawFolderPreviewPanel(string folderPath)
    {
        if (!GatherBuddy.CraftingListManager.GetAllFolderPaths().Any(path => path.Equals(folderPath, StringComparison.OrdinalIgnoreCase)))
        {
            _previewFolderPath = null;
            return;
        }

        var entries = GetFolderPreviewEntries(folderPath);

        ImGui.Spacing();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 8);
        ImGui.TextColored(ImGuiColors.ParsedGold, CraftingListManager.GetFolderDisplayName(folderPath));
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 8);
        ImGui.TextColored(ImGuiColors.DalamudGrey3, $"Folder: {CraftingListManager.FormatFolderPath(folderPath)}");
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 8);
        var listWord = entries.Count == 1 ? "list" : "lists";
        ImGui.TextColored(ImGuiColors.DalamudGrey3, $"{entries.Count} {listWord} in this folder tree");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.BeginChild("##previewFolderList", new Vector2(-1, 0), false);
        if (entries.Count == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "No lists in this folder.");
        }
        else
        {
            foreach (var (label, list) in entries)
            {
                ImGui.TextColored(ImGuiColors.DalamudGrey3, label);
                ImGui.SameLine();
                ImGui.TextColored(ImGuiColors.DalamudGrey, $"· {list.Recipes.Count} {(list.Recipes.Count == 1 ? "recipe" : "recipes")}");
            }
        }
        ImGui.EndChild();
    }

    private List<(string Label, CraftingListDefinition List)> GetFolderPreviewEntries(string folderPath, string? labelPrefix = null)
    {
        var entries = new List<(string Label, CraftingListDefinition List)>();

        foreach (var list in GatherBuddy.CraftingListManager.GetListsInFolder(folderPath).OrderBy(list => list.Name, StringComparer.OrdinalIgnoreCase))
        {
            var label = string.IsNullOrEmpty(labelPrefix)
                ? list.Name
                : $"{labelPrefix} / {list.Name}";
            entries.Add((label, list));
        }

        foreach (var childFolderPath in GatherBuddy.CraftingListManager.GetDirectSubfolderPaths(folderPath))
        {
            var childPrefix = string.IsNullOrEmpty(labelPrefix)
                ? CraftingListManager.GetFolderDisplayName(childFolderPath)
                : $"{labelPrefix} / {CraftingListManager.GetFolderDisplayName(childFolderPath)}";
            entries.AddRange(GetFolderPreviewEntries(childFolderPath, childPrefix));
        }

        return entries;
    }


}
