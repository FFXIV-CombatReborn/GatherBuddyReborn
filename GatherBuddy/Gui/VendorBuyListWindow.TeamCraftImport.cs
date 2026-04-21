using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using ElliLib.Raii;
using GatherBuddy.Vulcan.Vendors;
using ImRaii = ElliLib.Raii.ImRaii;

namespace GatherBuddy.Gui;

public sealed partial class VendorBuyListWindow
{
    private static readonly Vector2 DefaultTeamCraftImportWindowSize = new(520, 310);
    private const string TeamCraftImportWindowId = "Vendor TeamCraft Import###VendorTeamCraftImport";

    private bool _showTeamCraftImport;

    private string _teamCraftImportListName = string.Empty;
    private string _teamCraftImportText = string.Empty;
    private string? _teamCraftImportError;
    private bool _teamCraftImportIntoExistingList;
    private Guid? _teamCraftImportTargetListId;
    private bool _focusTeamCraftImportListName;
    private Vector2 _teamCraftImportWindowSize;
    private bool _teamCraftImportWindowSizeDirty;

    private void OpenTeamCraftImportWindow(VendorBuyListManager manager)
    {
        _teamCraftImportListName = "Imported from TeamCraft";
        _teamCraftImportText = string.Empty;
        _teamCraftImportError = null;
        _teamCraftImportIntoExistingList = false;
        _teamCraftImportTargetListId = GetDefaultTeamCraftImportTargetListId(manager);
        _focusTeamCraftImportListName = true;
        _showTeamCraftImport = true;
    }

    private void ResetTeamCraftImportWindowState()
    {
        _teamCraftImportListName = string.Empty;
        _teamCraftImportText = string.Empty;
        _teamCraftImportError = null;
        _teamCraftImportIntoExistingList = false;
        _teamCraftImportTargetListId = null;
        _focusTeamCraftImportListName = false;
        _showTeamCraftImport = false;
    }

    private void DrawTeamCraftImportWindow(VendorBuyListManager manager)
    {
        if (!_showTeamCraftImport)
            return;

        ImGui.SetNextWindowSize(_teamCraftImportWindowSize, ImGuiCond.Appearing);
        var isOpen = _showTeamCraftImport;
        var drawWindow = ImGui.Begin(TeamCraftImportWindowId, ref isOpen, ImGuiWindowFlags.NoCollapse);
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

        ImGui.TextColored(ImGuiColors.DalamudGrey3, "Paste the TeamCraft 'Vendors' text section below.");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        NormalizeTeamCraftImportTargetListId(manager);
        var footerHeight = ImGui.GetFrameHeightWithSpacing() + ImGui.GetStyle().ItemSpacing.Y * 3f + 2f;
        ImGui.BeginChild("##vendorTeamCraftImportContent", new Vector2(0, -footerHeight), false);
        if (manager.Lists.Count > 0)
        {
            ImGui.Text("Destination");
            if (ImGui.RadioButton("New List##vendorTeamCraftNewList", !_teamCraftImportIntoExistingList))
            {
                _teamCraftImportIntoExistingList = false;
                _teamCraftImportError = null;
                _focusTeamCraftImportListName = true;
            }
            ImGui.SameLine();
            if (ImGui.RadioButton("Existing List##vendorTeamCraftExistingList", _teamCraftImportIntoExistingList))
            {
                _teamCraftImportIntoExistingList = true;
                _teamCraftImportError = null;
            }
            ImGui.Spacing();
        }

        if (_teamCraftImportIntoExistingList)
        {
            var selectedList = GetTeamCraftImportTargetList(manager);
            ImGui.Text("Target List");
            ImGui.SetNextItemWidth(-1);
            if (ImGui.BeginCombo("##vendorTeamCraftImportTargetList", selectedList?.Name ?? "Select a list"))
            {
                foreach (var list in manager.Lists)
                {
                    var isSelected = selectedList != null && list.Id == selectedList.Id;
                    if (ImGui.Selectable(list.Name, isSelected))
                    {
                        _teamCraftImportTargetListId = list.Id;
                        _teamCraftImportError = null;
                    }
                    if (isSelected)
                        ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }
        }
        else
        {
            ImGui.Text("List Name");
            if (_focusTeamCraftImportListName)
            {
                ImGui.SetKeyboardFocusHere();
                _focusTeamCraftImportListName = false;
            }

            ImGui.SetNextItemWidth(-1);
            ImGui.InputText("##vendorTeamCraftImportListName", ref _teamCraftImportListName, 128);
        }

        ImGui.Spacing();
        ImGui.Text("Vendor Items");
        ImGui.SetNextItemWidth(-1);
        var errorHeight = _teamCraftImportError != null
            ? ImGui.GetTextLineHeightWithSpacing() + ImGui.GetStyle().ItemSpacing.Y
            : 0f;
        var vendorItemsHeight = Math.Max(150f, ImGui.GetContentRegionAvail().Y - errorHeight);
        ImGui.InputTextMultiline("##vendorTeamCraftImportText", ref _teamCraftImportText, 500000, new Vector2(-1, vendorItemsHeight));

        if (_teamCraftImportError != null)
        {
            ImGui.Spacing();
            ImGui.TextColored(ImGuiColors.DalamudRed, _teamCraftImportError);
        }
        ImGui.EndChild();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        var selectedTargetList = _teamCraftImportIntoExistingList
            ? GetTeamCraftImportTargetList(manager)
            : null;
        using (ImRaii.Disabled(string.IsNullOrWhiteSpace(_teamCraftImportText)
            || (_teamCraftImportIntoExistingList && selectedTargetList == null)))
        {
            if (ImGui.Button("Import", new Vector2(120f, 0)))
            {
                var result = manager.ImportTeamCraftList(
                    _teamCraftImportText,
                    _teamCraftImportIntoExistingList ? _teamCraftImportTargetListId : null,
                    _teamCraftImportListName);
                if (result.List != null)
                {
                    ResetTeamCraftImportWindowState();
                }
                else
                {
                    _teamCraftImportError = result.Error;
                }
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(100f, 0)))
        {
            ResetTeamCraftImportWindowState();
        }

        if (!_showTeamCraftImport)
            SaveTeamCraftImportWindowSize(true);

        ImGui.End();
    }

    private static Guid? GetDefaultTeamCraftImportTargetListId(VendorBuyListManager manager)
    {
        var activeListId = manager.ActiveList?.Id;
        if (activeListId.HasValue)
            return activeListId.Value;

        return manager.Lists.Count > 0
            ? manager.Lists[0].Id
            : null;
    }

    private void NormalizeTeamCraftImportTargetListId(VendorBuyListManager manager)
    {
        if (manager.Lists.Count == 0)
        {
            _teamCraftImportIntoExistingList = false;
            _teamCraftImportTargetListId = null;
            return;
        }

        if (_teamCraftImportTargetListId.HasValue)
        {
            foreach (var list in manager.Lists)
            {
                if (list.Id == _teamCraftImportTargetListId.Value)
                    return;
            }
        }

        _teamCraftImportTargetListId = GetDefaultTeamCraftImportTargetListId(manager);
    }

    private VendorBuyListDefinition? GetTeamCraftImportTargetList(VendorBuyListManager manager)
    {
        NormalizeTeamCraftImportTargetListId(manager);
        if (!_teamCraftImportTargetListId.HasValue)
            return null;

        foreach (var list in manager.Lists)
        {
            if (list.Id == _teamCraftImportTargetListId.Value)
                return list;
        }

        return null;
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
        if (HasTeamCraftImportWindowSizeChanged(GatherBuddy.Config.VendorTeamCraftImportWindowSize, normalizedSize))
        {
            GatherBuddy.Config.VendorTeamCraftImportWindowSize = normalizedSize;
            GatherBuddy.Config.Save();
            GatherBuddy.Log.Debug($"[VendorBuyListWindow] Saved vendor TeamCraft import window size: {normalizedSize.X}x{normalizedSize.Y}");
        }

        _teamCraftImportWindowSizeDirty = false;
    }
}
