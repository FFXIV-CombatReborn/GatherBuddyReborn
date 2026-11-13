using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using GatherBuddy.Vulcan.Vendors;
using RebornMaterial;
using System;
using System.Numerics;
using ImRaii = Dalamud.Interface.Utility.Raii.ImRaii;

namespace GatherBuddy.Gui;

public sealed partial class VendorBuyListWindow
{
    private string _teamCraftImportListName = string.Empty;
    private string _teamCraftImportText = string.Empty;
    private string? _teamCraftImportError;
    private bool _teamCraftImportIntoExistingList;
    private Guid? _teamCraftImportTargetListId;
    private bool _focusTeamCraftImportListName;

    internal static RebornUI.HostedPanelWindow CreateTeamCraftImportWindow()
        => new("GatherBuddyVendorTeamCraftImport", "Vendor TeamCraft Import", new Vector2(540f, 480f), new Vector2(420f, 380f), bodyScrolls: false);

    private void OpenTeamCraftImportWindow(VendorBuyListManager manager)
    {
        _teamCraftImportListName = "Imported from TeamCraft";
        _teamCraftImportText = string.Empty;
        _teamCraftImportError = null;
        _teamCraftImportIntoExistingList = false;
        _teamCraftImportTargetListId = GetDefaultTeamCraftImportTargetListId(manager);
        _focusTeamCraftImportListName = true;
        GatherBuddy.VendorTeamCraftImportWindow?.Show(this, DrawTeamCraftImport);
    }

    private void ResetTeamCraftImportWindowState()
    {
        _teamCraftImportListName = string.Empty;
        _teamCraftImportText = string.Empty;
        _teamCraftImportError = null;
        _teamCraftImportIntoExistingList = false;
        _teamCraftImportTargetListId = null;
        _focusTeamCraftImportListName = false;
        GatherBuddy.VendorTeamCraftImportWindow?.Hide(this);
    }

    private void DrawTeamCraftImport()
    {
        var manager = GatherBuddy.VendorBuyListManager;
        if (manager == null)
        {
            return;
        }

        M3Text.Draw("Paste the 'Vendors' section of a TeamCraft list below.", M3TextStyle.Muted, true);
        ImGui.Dummy(new Vector2(0f, M3.Space1));
        NormalizeTeamCraftImportTargetListId(manager);
        var footerHeight = M3Widgets.ButtonHeight + M3.Space3 + ImGui.GetStyle().ItemSpacing.Y;
        using (var content = ImRaii.Child("##vendorTeamCraftImportContent", new Vector2(0, -footerHeight), false))
        {
            if (content)
            {
                DrawTeamCraftImportForm(manager);
            }
        }

        ImGui.Dummy(new Vector2(0f, M3.Space1));
        var selectedTargetList = _teamCraftImportIntoExistingList
            ? GetTeamCraftImportTargetList(manager)
            : null;
        var canImport = !string.IsNullOrWhiteSpace(_teamCraftImportText) && !(_teamCraftImportIntoExistingList && selectedTargetList == null);
        if (M3Widgets.Button("##vendorTeamCraftImport", "Import", M3ButtonStyle.Filled, FontAwesomeIcon.FileImport, enabled: canImport))
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

        ImGui.SameLine();
        if (M3Widgets.Button("##vendorTeamCraftCancel", "Cancel", M3ButtonStyle.Text))
        {
            ResetTeamCraftImportWindowState();
        }
    }

    private static readonly M3Segment[] TeamCraftDestinationSegments =
    [
        new("New list", FontAwesomeIcon.Plus),
        new("Existing list", FontAwesomeIcon.ListUl),
    ];

    private void DrawTeamCraftImportForm(VendorBuyListManager manager)
    {
        var width = ImGui.GetContentRegionAvail().X;
        if (VendorBuyListManager.Lists.Count > 0)
        {
            M3Text.Draw("Destination", M3TextStyle.Label);
            var destination = _teamCraftImportIntoExistingList ? 1 : 0;
            var clicked = M3Widgets.SegmentedButtons("##vendorTeamCraftDestination", TeamCraftDestinationSegments, destination);
            if (clicked >= 0 && clicked != destination)
            {
                _teamCraftImportIntoExistingList = clicked == 1;
                _teamCraftImportError = null;
                _focusTeamCraftImportListName = clicked == 0;
            }

            ImGui.Dummy(new Vector2(0f, M3.Space1));
        }

        if (_teamCraftImportIntoExistingList)
        {
            var selectedList = GetTeamCraftImportTargetList(manager);
            var lists = VendorBuyListManager.Lists;
            var index = -1;
            for (var i = 0; selectedList != null && i < lists.Count; ++i)
            {
                if (lists[i].Id == selectedList.Id)
                {
                    index = i;
                    break;
                }
            }

            M3Text.Draw("Target list", M3TextStyle.Label);
            if (M3Widgets.Combo("##vendorTeamCraftImportTargetList", ref index, lists.Count, i => lists[i].Name, width, "Select a list",
                    lists.Count > 8)
             && index >= 0 && index < lists.Count)
            {
                _teamCraftImportTargetListId = lists[index].Id;
                _teamCraftImportError = null;
            }
        }
        else
        {
            M3Text.Draw("List name", M3TextStyle.Label);
            if (_focusTeamCraftImportListName)
            {
                ImGui.SetKeyboardFocusHere();
                _focusTeamCraftImportListName = false;
            }

            M3TextField.Draw("##vendorTeamCraftImportListName", "Imported from TeamCraft", ref _teamCraftImportListName, width, 128);
        }

        ImGui.Dummy(new Vector2(0f, M3.Space1));
        M3Text.Draw("Vendor items", M3TextStyle.Label);
        var errorHeight = _teamCraftImportError != null ? ImGui.GetTextLineHeight() * 2f + 32f * M3.Scale : 0f;
        var vendorItemsHeight = Math.Max(VulcanUiScaling.Scaled(150f), ImGui.GetContentRegionAvail().Y - errorHeight);
        ImGui.InputTextMultiline("##vendorTeamCraftImportText", ref _teamCraftImportText, 500000, new Vector2(-1, vendorItemsHeight));

        if (_teamCraftImportError != null)
        {
            ImGui.Dummy(new Vector2(0f, M3.Space1));
            M3Widgets.Banner("##vendorTeamCraftImportError", _teamCraftImportError, M3Severity.Error, FontAwesomeIcon.ExclamationCircle);
        }
    }

    private static Guid? GetDefaultTeamCraftImportTargetListId(VendorBuyListManager manager)
    {
        var activeListId = VendorBuyListManager.ActiveList?.Id;
        if (activeListId.HasValue)
        {
            return activeListId.Value;
        }

        return VendorBuyListManager.Lists.Count > 0
            ? global::GatherBuddy.Vulcan.Vendors.VendorBuyListManager.Lists[0].Id
            : null;
    }

    private void NormalizeTeamCraftImportTargetListId(VendorBuyListManager manager)
    {
        if (VendorBuyListManager.Lists.Count == 0)
        {
            _teamCraftImportIntoExistingList = false;
            _teamCraftImportTargetListId = null;
            return;
        }

        if (_teamCraftImportTargetListId.HasValue)
        {
            foreach (var list in VendorBuyListManager.Lists)
            {
                if (list.Id == _teamCraftImportTargetListId.Value)
                {
                    return;
                }
            }
        }

        _teamCraftImportTargetListId = GetDefaultTeamCraftImportTargetListId(manager);
    }

    private VendorBuyListDefinition? GetTeamCraftImportTargetList(VendorBuyListManager manager)
    {
        NormalizeTeamCraftImportTargetListId(manager);
        if (!_teamCraftImportTargetListId.HasValue)
        {
            return null;
        }

        foreach (var list in VendorBuyListManager.Lists)
        {
            if (list.Id == _teamCraftImportTargetListId.Value)
            {
                return list;
            }
        }

        return null;
    }
}
