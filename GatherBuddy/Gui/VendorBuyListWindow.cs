using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using GatherBuddy.Utility;
using GatherBuddy.Vulcan.Vendors;
using Lumina.Excel.Sheets;
using RebornMaterial;
using System;
using System.Collections.Generic;
using System.Numerics;
using ImRaii = Dalamud.Interface.Utility.Raii.ImRaii;

namespace GatherBuddy.Gui;

public sealed partial class VendorBuyListWindow : M3PanelWindow
{
    private sealed record VendorListNpcOption(VendorNpc Npc, VendorNpcLocation? Location, string ZoneName);
    private readonly record struct VendorCurrencyRequirement(
        uint CurrencyItemId,
        string CurrencyName,
        ushort IconId,
        ulong AvailableAmount,
        ulong RequiredAmount);

    public const string WindowId = "Vendor Buy List###VendorBuyListWindow";
    private const string ListNameDialogId = "Vendor Buy List Name###VendorBuyListNamePopup";
    private const float HoldToConfirmSeconds = 0.6f;

    private static readonly M3PluginRequirement[] RequiredVendorAutomationPlugins =
    [
        new("InventoryTools", "Allagan Tools"),
        new(VendorAutomationRequirements.AllaganItemSearchInternalName, "Allagan Item Search"),
    ];

    private static readonly M3WindowAction[] VendorWindowActions =
    [
        new("##vendor_open_vulcan", FontAwesomeIcon.Store, "Open Vulcan's Vendors page, to add items"),
        new("##vendor_open_collectables", FontAwesomeIcon.BoxOpen, "Open the collectables window"),
    ];

    private static readonly Vector2 DefaultSize = new(980f, 600f);
    private static readonly WindowSizeConstraints DefaultConstraints = new()
    {
        MinimumSize = new Vector2(560f, 320f),
        MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
    };

    private readonly Dictionary<uint, ushort> _currencyIconIds = [];
    private readonly Dictionary<uint, string> _currencyNames = [];

    private readonly Dictionary<uint, string> _zoneNames = [];
    private Guid? _editingListId;
    private string _listNameInput = string.Empty;
    private bool _focusListNameInput;
    private bool _openListNamePopup;

    public VendorBuyListWindow()
        : base(WindowId, "Vendor Buy Lists", DefaultSize, DefaultConstraints)
    {
        RespectCloseHotkey = true;
        IsOpen = false;
    }

    public void Open()
        => OpenAndRestore();

    public bool OpenCreateListPopup(uint itemId)
    {
        var manager = GatherBuddy.VendorBuyListManager;
        if (manager == null)
        {
            return false;
        }

        var list = manager.CreateList("Vendor List", false);
        if (!manager.TryIncrementTarget(list.Id, itemId, 1, selectList: true, openWindow: false, announce: false))
        {
            GatherBuddy.Log.Warning($"[VendorBuyListWindow] Failed to add item {itemId} to newly created vendor list '{list.Name}'.");
            return false;
        }

        Open();
        if (!manager.IsBusy)
        {
            OpenListNamePopup(list.Id, list.Name);
        }

        return true;
    }

    public bool OpenCreateListPopup(IReadOnlyList<VendorBuyListManager.VendorTargetRequest> requests)
        => OpenCreateListPopup("Vendor List", requests);

    public bool OpenCreateListPopup(string listName, IReadOnlyList<VendorBuyListManager.VendorTargetRequest> requests)
    {
        var manager = GatherBuddy.VendorBuyListManager;
        if (manager == null || requests.Count == 0)
        {
            return false;
        }

        var list = manager.CreateList(listName, false);
        var addedCount = manager.TrySetTargets(list.Id, requests, selectList: true, openWindow: false, announce: false);
        if (addedCount == 0)
        {
            GatherBuddy.Log.Warning(
                $"[VendorBuyListWindow] Failed to add {requests.Count:N0} vendor targets to newly created vendor list '{list.Name}'.");
            return false;
        }

        Open();
        if (!manager.IsBusy)
        {
            OpenListNamePopup(list.Id, list.Name);
        }

        return true;
    }

    public bool OpenCreateListPopup(VendorShopEntry entry, VendorNpc vendor, uint targetQuantity)
    {
        var manager = GatherBuddy.VendorBuyListManager;
        if (manager == null)
        {
            return false;
        }

        var list = manager.CreateList("Vendor List", false);
        if (!manager.TryAddTarget(list.Id, entry, vendor, targetQuantity, selectList: true, openWindow: false, announce: false))
        {
            GatherBuddy.Log.Warning($"[VendorBuyListWindow] Failed to add {entry.ItemName} to newly created vendor list '{list.Name}'.");
            return false;
        }

        Open();
        if (!manager.IsBusy)
        {
            OpenListNamePopup(list.Id, list.Name);
        }

        return true;
    }

    protected override bool BodyScrolls
        => false;

    protected override M3WindowAction[] WindowActions
        => VendorWindowActions;

    protected override void OnWindowAction(int index)
    {
        switch (index)
        {
            case 0:
                GatherBuddy.VulcanWindow?.OpenToVendors();
                break;
            case 1:
                GatherBuddy.CollectablesWindow?.Open();
                break;
        }
    }

    protected override (string Text, Vector4? Color) Status
    {
        get
        {
            var manager = GatherBuddy.VendorBuyListManager;
            if (manager == null)
            {
                return (string.Empty, null);
            }

            if (!string.IsNullOrWhiteSpace(manager.StatusText))
            {
                return (manager.StatusText, manager.IsRunning ? M3.Scheme.Primary : null);
            }

            return (VendorBuyListManager.Lists.Count == 1 ? "1 list" : $"{VendorBuyListManager.Lists.Count} lists", null);
        }
    }

    protected override void DrawContents()
    {
        var manager = GatherBuddy.VendorBuyListManager;
        if (manager == null)
        {
            return;
        }

        var activeList = VendorBuyListManager.ActiveList;
        if (activeList == null)
        {
            M3Widgets.EmptyState("##vendor_no_lists", "No vendor lists", "Lists appear here once one is made.", FontAwesomeIcon.ShoppingCart);
            return;
        }

        var avail = ImGui.GetContentRegionAvail();
        var leftWidth = System.Math.Clamp(avail.X * 0.26f, 200f * M3.Scale, 270f * M3.Scale);

        using (var left = M3Panel.Begin("##vendorBuyListLeftPanel", new Vector2(leftWidth, avail.Y), fill: VulcanUiStyle.PanelBackground))
        {
            if (left)
            {
                DrawListSidebar(manager, activeList);
            }
        }

        ImGui.SameLine();
        using (var right = ImRaii.Child("##vendorBuyListRightPanel", new Vector2(0f, avail.Y), false))
        {
            if (right)
            {
                DrawActiveListPanel(manager, activeList);
            }
        }

        DrawListNameDialog(manager);
    }

    private void DrawListSidebar(VendorBuyListManager manager, VendorBuyListDefinition activeList)
    {
        var canModifyLists = !manager.IsBusy;
        var width = ImGui.GetContentRegionAvail().X;

        M3Widgets.SectionLabel($"Lists · {VendorBuyListManager.Lists.Count}");
        if (M3Widgets.Button("##vendor_new_list", "New list", M3ButtonStyle.Tonal, FontAwesomeIcon.Plus, width, canModifyLists,
                canModifyLists ? null : "Wait until the running list finishes."))
        {
            var list = manager.CreateList("Vendor List");
            OpenListNamePopup(list.Id, list.Name);
        }

        if (M3Widgets.Button("##vendor_teamcraft_import", "TeamCraft import", M3ButtonStyle.Text, FontAwesomeIcon.FileImport, width, canModifyLists,
                canModifyLists ? "Import the 'Vendors' section of a TeamCraft list." : "Wait until the running list finishes."))
        {
            OpenTeamCraftImportWindow(manager);
        }

        ImGui.Dummy(new Vector2(0f, M3.Space1));
        using var scroll = ImRaii.Child("##vendorBuyListSelectorScroll", new Vector2(-1f, 0f), false);
        if (!scroll)
        {
            return;
        }

        foreach (var list in VendorBuyListManager.GetListsByCreation(false))
        {
            DrawListSidebarEntry(manager, list, activeList.Id == list.Id, canModifyLists);
        }
    }

    private static void DrawListSidebarEntry(VendorBuyListManager manager, VendorBuyListDefinition list, bool isSelected, bool canModifyLists)
    {
        var s = M3.Scheme;
        var scale = M3.Scale;
        var pendingCount = VendorBuyListManager.GetPendingEntryCount(list);
        var running = manager.IsRunning && manager.RunningListId == list.Id;
        var summary = $"{list.Entries.Count} entry(s)";
        if (pendingCount > 0)
        {
            summary += $" · {pendingCount} pending";
        }

        if (running)
        {
            summary += " · running";
        }

        var width = MathF.Max(1f, ImGui.GetContentRegionAvail().X);
        var padding = new Vector2(12f, 6f) * scale;
        float summaryHeight;
        using (ImRaii.PushFont(M3.LabelSmall))
        {
            summaryHeight = ImGui.GetTextLineHeight();
        }

        var height = padding.Y * 2f + ImGui.GetTextLineHeight() + 2f * scale + summaryHeight;
        var pressed = ImGui.InvisibleButton($"##vendorBuyList_{list.Id}", new Vector2(width, height)) && canModifyLists;
        var hovered = canModifyLists && ImGui.IsItemHovered();
        var held = canModifyLists && ImGui.IsItemActive();
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var drawList = ImGui.GetWindowDrawList();
        var dim = canModifyLists || isSelected ? 1f : M3.DisabledContent;

        if (isSelected)
        {
            drawList.AddRectFilled(min, max, M3.U32(s.SecondaryContainer, 0.9f), M3.ShapeMedium);
        }

        if (hovered || held)
        {
            drawList.AddRectFilled(min, max, M3.U32(s.OnSurface, held ? M3.StatePressed : M3.StateHover), M3.ShapeMedium);
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }

        var textWidth = MathF.Max(1f, width - padding.X * 2f);
        var name = M3Navigation.Truncate(list.Name, textWidth);
        drawList.AddText(min + padding, M3.U32(isSelected ? s.OnSecondaryContainer : s.OnSurface, dim), name);
        using (ImRaii.PushFont(M3.LabelSmall))
        {
            var summaryColor = running ? s.Primary : isSelected ? M3.Alpha(s.OnSecondaryContainer, 0.8f) : s.OnSurfaceVariant;
            drawList.AddText(new Vector2(min.X + padding.X, min.Y + padding.Y + ImGui.GetTextLineHeight() + 2f * scale),
                M3.U32(summaryColor, dim), M3Navigation.Truncate(summary, textWidth));
        }

        if (ImGui.IsItemHovered() && name != list.Name)
        {
            M3Tooltip.Show(list.Name);
        }

        if (pressed)
        {
            manager.SetActiveList(list.Id);
        }

        ImGui.Dummy(new Vector2(0f, 2f * scale));
    }

    private void DrawActiveListPanel(VendorBuyListManager manager, VendorBuyListDefinition activeList)
    {
        var entries = new List<VendorBuyListEntry>(VendorBuyListManager.Entries);
        var pending = VendorBuyListManager.GetPendingEntryCount();
        var currencyRequirements = BuildCurrencyRequirements(entries, manager);
        var vendorAutomationAvailable = VendorAutomationRequirements.IsAvailable;

        DrawActiveListHeader(manager, activeList, entries.Count, pending);

        if (!vendorAutomationAvailable)
        {
            M3Widgets.Banner("##vendor_automation_unavailable",
                $"{VendorAutomationRequirements.UnavailableStatusText} {VendorAutomationRequirements.UnavailableHelpText}", M3Severity.Warning,
                FontAwesomeIcon.ExclamationTriangle);
            ImGui.Dummy(new Vector2(0f, M3.Space1));
        }

        DrawRunControls(manager, entries.Count, pending, vendorAutomationAvailable);

        if (entries.Count == 0)
        {
            ImGui.Dummy(new Vector2(0f, M3.Space3));
            if (M3Widgets.EmptyState("##vendor_list_empty", "This list is empty",
                    "Add supported vendor items from Vulcan's Vendors page, the Materials window, or a TeamCraft import.", FontAwesomeIcon.ShoppingCart,
                    "Open Vulcan's Vendors page"))
            {
                GatherBuddy.VulcanWindow?.OpenToVendors();
            }

            return;
        }

        DrawCurrencyRequirementSummary(currencyRequirements);
        M3Widgets.SectionLabel("Entries");
        DrawEntryTable(entries, manager);
    }

    private void DrawActiveListHeader(VendorBuyListManager manager, VendorBuyListDefinition activeList, int entryCount, int pending)
    {
        var s = M3.Scheme;
        var canModifyLists = !manager.IsBusy;
        var renameSize = M3Widgets.IconButtonSize;
        var deleteWidth = M3Widgets.ButtonWidth(FontAwesomeIcon.Trash, "Delete");
        var gap = M3.Space1;
        var width = ImGui.GetContentRegionAvail().X;
        var origin = ImGui.GetCursorScreenPos();
        var controlsWidth = renameSize + gap + deleteWidth;
        var textWidth = MathF.Max(32f * M3.Scale, width - controlsWidth - M3.Space2);

        float titleHeight;
        string title;
        using (ImRaii.PushFont(M3.TitleLarge))
        {
            title = M3Navigation.Truncate(activeList.Name, textWidth);
            titleHeight = ImGui.CalcTextSize(title).Y;
        }

        var summary = $"{entryCount} entry(s) · {pending} pending";
        var height = MathF.Max(M3Widgets.ButtonHeight, titleHeight + 2f * M3.Scale + ImGui.GetTextLineHeight());
        var drawList = ImGui.GetWindowDrawList();
        var textTop = origin.Y + (height - titleHeight - 2f * M3.Scale - ImGui.GetTextLineHeight()) * 0.5f;
        using (ImRaii.PushFont(M3.TitleLarge))
        {
            drawList.AddText(new Vector2(origin.X, textTop), M3.U32(s.OnSurface), title);
        }

        drawList.AddText(new Vector2(origin.X, textTop + titleHeight + 2f * M3.Scale), M3.U32(s.OnSurfaceVariant), summary);

        var controlsX = origin.X + width - controlsWidth;
        ImGui.SetCursorScreenPos(new Vector2(controlsX, origin.Y + (height - renameSize) * 0.5f));
        if (M3Widgets.IconButton("##vendor_rename_list", FontAwesomeIcon.Pen, "Rename this list", enabled: canModifyLists))
        {
            OpenListNamePopup(activeList.Id, activeList.Name);
        }

        ImGui.SetCursorScreenPos(new Vector2(controlsX + renameSize + gap, origin.Y + (height - M3Widgets.ButtonHeight) * 0.5f));
        var canDelete = canModifyLists && VendorBuyListManager.Lists.Count > 1;
        if (M3Widgets.HoldButton("##vendor_delete_list", "Delete", HoldToConfirmSeconds, M3ButtonStyle.Danger, FontAwesomeIcon.Trash, deleteWidth, canDelete,
                VendorBuyListManager.Lists.Count <= 1 ? "The last list can't be deleted." : "Hold to delete this list."))
        {
            manager.DeleteList(activeList.Id);
        }

        ImGui.SetCursorScreenPos(new Vector2(origin.X, origin.Y + height));
        ImGui.Dummy(new Vector2(width, M3.Space2));
    }

    private static void DrawRunControls(VendorBuyListManager manager, int entryCount, int pending, bool vendorAutomationAvailable)
    {
        if (!manager.IsRunning)
        {
            var canStart = entryCount > 0 && pending > 0 && !manager.IsBusy && vendorAutomationAvailable;
            var tooltip = !vendorAutomationAvailable ? VendorAutomationRequirements.UnavailableHelpText
                : pending == 0 ? "Nothing on this list needs buying right now."
                : "Run the active vendor list.";
            if (M3Widgets.Button("##vendor_start", "Start list", M3ButtonStyle.Filled, FontAwesomeIcon.Play, enabled: canStart, tooltip: tooltip)
             && vendorAutomationAvailable)
            {
                manager.Start();
            }
        }
        else if (M3Widgets.Button("##vendor_stop", "Stop", M3ButtonStyle.Danger, FontAwesomeIcon.Stop))
        {
            manager.Stop();
        }

        ImGui.SameLine();
        if (M3Widgets.HoldButton("##vendor_clear", "Clear list", HoldToConfirmSeconds, M3ButtonStyle.Outlined, FontAwesomeIcon.Eraser,
                enabled: entryCount > 0 && !manager.IsBusy, tooltip: "Hold to remove every entry from this list."))
        {
            manager.Clear();
        }

        ImGui.SameLine();
        var pillHeight = M3Widgets.PluginPillSize(RequiredVendorAutomationPlugins, true).Y;
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + MathF.Max(0f, (M3Widgets.ButtonHeight - pillHeight) * 0.5f));
        M3Widgets.PluginPill("##vendor_plugins", RequiredVendorAutomationPlugins, true);
        ImGui.Dummy(new Vector2(0f, M3.Space1));
    }

    private List<VendorCurrencyRequirement> BuildCurrencyRequirements(IReadOnlyList<VendorBuyListEntry> entries, VendorBuyListManager manager)
    {
        var requirements = new Dictionary<uint, VendorCurrencyRequirement>();
        foreach (var entry in entries)
        {
            var remainingQuantity = VendorBuyListManager.GetRemainingQuantity(entry);
            if (remainingQuantity == 0 || entry.Cost == 0)
            {
                continue;
            }

            var currencyItemId = entry.CurrencyItemId;
            var currencyName = GetCurrencyName(currencyItemId, entry.CurrencyName);
            var currencyGroup = VendorShopResolver.GetCurrencyGroup(entry.ShopType, currencyItemId);
            var availability = VendorCurrencyAvailabilityResolver.Resolve(currencyGroup, currencyItemId, currencyName);
            var iconId = GetCurrencyIconId(currencyItemId);
            var requiredAmount = (ulong)remainingQuantity * entry.Cost;
            if (requirements.TryGetValue(currencyItemId, out var existing))
            {
                requirements[currencyItemId] = existing with
                {
                    RequiredAmount = existing.RequiredAmount + requiredAmount,
                    CurrencyName = string.IsNullOrWhiteSpace(existing.CurrencyName) ? availability.CurrencyName : existing.CurrencyName,
                    IconId = existing.IconId != 0 ? existing.IconId : iconId,
                    AvailableAmount = existing.AvailableAmount != 0 ? existing.AvailableAmount : availability.AvailableAmount,
                };
                continue;
            }

            requirements.Add(currencyItemId, new VendorCurrencyRequirement(
                currencyItemId,
                availability.CurrencyName,
                iconId,
                availability.AvailableAmount,
                requiredAmount));
        }

        var sortedRequirements = new List<VendorCurrencyRequirement>(requirements.Values);
        sortedRequirements.Sort(static (lhs, rhs) =>
        {
            var order = StringComparer.OrdinalIgnoreCase.Compare(lhs.CurrencyName, rhs.CurrencyName);
            return order != 0 ? order : lhs.CurrencyItemId.CompareTo(rhs.CurrencyItemId);
        });
        return sortedRequirements;
    }

    private static void DrawCurrencyRequirementSummary(IReadOnlyList<VendorCurrencyRequirement> requirements)
    {
        M3Widgets.SectionLabel("Currency · have / need");
        if (requirements.Count == 0)
        {
            M3Text.Draw("No currency needed.", M3TextStyle.Muted);
            ImGui.Dummy(new Vector2(0f, M3.Space1));
            return;
        }

        var flow = new M3Flow(M3.Space2);
        foreach (var requirement in requirements)
        {
            flow.Next(CurrencyChipWidth(requirement));
            DrawCurrencyChip(requirement);
        }

        ImGui.Dummy(new Vector2(0f, M3.Space1));
    }

    private static float CurrencyIconSize
        => MathF.Max(18f * M3.Scale, ImGui.GetTextLineHeight());

    private static string CurrencyChipText(VendorCurrencyRequirement requirement)
        => $"{requirement.AvailableAmount:N0} / {requirement.RequiredAmount:N0}";

    private static float CurrencyChipWidth(VendorCurrencyRequirement requirement)
    {
        var scale = M3.Scale;
        var icon = requirement.IconId != 0 ? CurrencyIconSize + 6f * scale : 0f;
        return 12f * scale + icon + ImGui.CalcTextSize(CurrencyChipText(requirement)).X + 12f * scale;
    }

    private static void DrawCurrencyChip(VendorCurrencyRequirement requirement)
    {
        var s = M3.Scheme;
        var scale = M3.Scale;
        var enough = requirement.AvailableAmount >= requirement.RequiredAmount;
        var tone = enough ? M3.Severity(M3Severity.Success) : M3.Severity(M3Severity.Error);
        var iconSize = CurrencyIconSize;
        var height = iconSize + 8f * scale;
        var size = new Vector2(CurrencyChipWidth(requirement), height);

        ImGui.Dummy(size);
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(min, max, M3.U32(tone, 0.12f), height * 0.5f);
        drawList.AddRect(min, max, M3.U32(tone, 0.45f), height * 0.5f, ImDrawFlags.None, 1f * scale);

        var x = min.X + 12f * scale;
        if (requirement.IconId != 0)
        {
            var icon = Icons.DefaultStorage.TextureProvider.GetFromGameIcon(new GameIconLookup(requirement.IconId));
            if (icon.TryGetWrap(out var wrap, out _))
            {
                M3Draw.Texture(drawList, wrap, new Vector2(x, min.Y + (height - iconSize) * 0.5f), iconSize);
            }

            x += iconSize + 6f * scale;
        }

        var text = CurrencyChipText(requirement);
        var textSize = ImGui.CalcTextSize(text);
        drawList.AddText(new Vector2(x, min.Y + (height - textSize.Y) * 0.5f), M3.U32(s.OnSurface), text);

        if (ImGui.IsItemHovered())
        {
            M3Tooltip.Show($"{requirement.CurrencyName}\nHave: {requirement.AvailableAmount:N0}\nNeed: {requirement.RequiredAmount:N0}"
              + (enough ? string.Empty : $"\nShort by {requirement.RequiredAmount - requirement.AvailableAmount:N0}"));
        }
    }

    private void DrawEntryTable(List<VendorBuyListEntry> entries, VendorBuyListManager manager)
    {
        const ImGuiTableFlags tableFlags = ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg
                                         | ImGuiTableFlags.SizingStretchProp;
        using var panel = VulcanUiStyle.PushTheme();
        if (!ImGui.BeginTable("##vendorBuyListTable", 8, tableFlags, new Vector2(-1, -1)))
        {
            return;
        }

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 1.25f);
        ImGui.TableSetupColumn("Cost", ImGuiTableColumnFlags.WidthFixed, VulcanUiScaling.Scaled(58f));
        ImGui.TableSetupColumn("Want", ImGuiTableColumnFlags.WidthFixed, VulcanUiScaling.Scaled(86f));
        ImGui.TableSetupColumn("Have", ImGuiTableColumnFlags.WidthFixed, VulcanUiScaling.Scaled(54f));
        ImGui.TableSetupColumn("Need", ImGuiTableColumnFlags.WidthFixed, VulcanUiScaling.Scaled(54f));
        ImGui.TableSetupColumn("Vendor", ImGuiTableColumnFlags.WidthStretch, 0.80f);
        ImGui.TableSetupColumn("Location", ImGuiTableColumnFlags.WidthStretch, 1.00f);
        ImGui.TableSetupColumn("##remove", ImGuiTableColumnFlags.WidthFixed, ImGui.GetFrameHeight() + VulcanUiScaling.Scaled(8f));
        ImGui.TableHeadersRow();

        var iconSize = VulcanUiScaling.Scaled(20f);
        var iconVec = new Vector2(iconSize, iconSize);
        var clipper = ImGui.ImGuiListClipper();
        clipper.Begin(entries.Count);
        while (clipper.Step())
        {
            for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                DrawEntryRow(entries[i], manager, iconVec, iconSize);
            }
        }

        clipper.End();
        clipper.Destroy();

        ImGui.EndTable();
    }

    private void OpenListNamePopup(Guid listId, string currentName)
    {
        _editingListId = listId;
        _listNameInput = currentName;
        _focusListNameInput = true;
        _openListNamePopup = true;
    }

    private void DrawListNameDialog(VendorBuyListManager manager)
    {
        if (_openListNamePopup)
        {
            M3Dialog.Open(ListNameDialogId);
            _openListNamePopup = false;
        }

        if (!ImGui.IsPopupOpen(ListNameDialogId))
        {
            _editingListId = null;
            return;
        }

        using var dialog = M3Dialog.Begin(ListNameDialogId, "List name", "Press Enter or Save to keep it.", FontAwesomeIcon.None, 380f);
        if (!dialog.IsOpen)
        {
            return;
        }

        if (_focusListNameInput)
        {
            ImGui.SetKeyboardFocusHere();
            _focusListNameInput = false;
        }

        M3TextField.Draw("##vendorBuyListNameInput", "List name", ref _listNameInput, ImGui.GetContentRegionAvail().X, 128);
        var submitted = ImGui.IsItemDeactivated() && (ImGui.IsKeyPressed(ImGuiKey.Enter) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter));

        var pressed = M3Dialog.Actions(
        [
            new M3DialogAction("Cancel"),
            new M3DialogAction("Save", M3ButtonStyle.Filled, FontAwesomeIcon.Check),
        ]);

        if (submitted || pressed == 1)
        {
            if (_editingListId.HasValue)
            {
                manager.RenameList(_editingListId.Value, _listNameInput);
            }

            _editingListId = null;
            if (submitted)
            {
                ImGui.CloseCurrentPopup();
            }
        }
        else if (pressed == 0)
        {
            _editingListId = null;
        }
    }

    private void DrawEntryRow(VendorBuyListEntry entry, VendorBuyListManager manager, Vector2 iconVec, float iconSize)
    {
        var s = M3.Scheme;
        var success = M3.Severity(M3Severity.Success);
        VendorBuyListManager.TryResolveLiveEntry(entry, out var liveEntry, out var resolvedVendor, out _);
        var vendorOptions = BuildVendorOptions(liveEntry);
        var selectedVendor = GetSelectedVendorOption(vendorOptions, resolvedVendor);
        var isActive = manager.ActiveEntryId == entry.Id;
        var isEnabled = entry.Enabled;
        var currentCount = System.Math.Max(0, VendorBuyListManager.GetCurrentInventoryAndArmoryCount(entry.ItemId));
        var remaining = VendorBuyListManager.GetRemainingQuantity(entry);
        var targetCount = entry.TargetQuantity > int.MaxValue ? int.MaxValue : (int)entry.TargetQuantity;

        ImGui.TableNextRow();
        if (isActive)
        {
            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, M3.U32(s.Primary, 0.16f));
        }

        ImGui.TableNextColumn();
        if (M3Widgets.Checkbox($"##vendorBuyListEnabled_{entry.Id}", ref isEnabled, !(isActive || manager.IsBusy)))
        {
            manager.SetEntryEnabled(entry.Id, isEnabled);
        }

        M3Tooltip.Hovered(isEnabled ? "Enabled. Click to skip this entry." : "Skipped. Click to buy it again.");

        ImGui.SameLine(0, VulcanUiScaling.Scaled(4f));
        var icon = Icons.DefaultStorage.TextureProvider.GetFromGameIcon(new GameIconLookup(entry.IconId));
        if (icon.TryGetWrap(out var wrap, out _))
        {
            ImGui.Image(wrap.Handle, iconVec);
        }
        else
        {
            ImGui.Dummy(iconVec);
        }

        ImGui.SameLine(0, VulcanUiScaling.Scaled(4f));
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (iconSize - ImGui.GetTextLineHeight()) / 2f);
        var nameColor = isActive ? s.Primary : !entry.Enabled ? M3.Alpha(s.OnSurfaceVariant, 0.7f) : s.OnSurface;
        using (ImRaii.PushColor(ImGuiCol.Text, nameColor))
        {
            ImGui.TextUnformatted(entry.ItemName);
        }

        M3Tooltip.Hovered(isActive ? $"{entry.ItemName}\nBuying this now." : entry.ItemName);

        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted($"{entry.Cost:N0}");

        ImGui.TableNextColumn();
        if (M3Widgets.NumberField($"##vendorBuyListTarget_{entry.Id}", ref targetCount, ImGui.GetContentRegionAvail().X, 1, 1, 99999,
                enabled: !(isActive || manager.IsBusy)))
        {
            manager.UpdateTargetQuantity(entry.Id, (uint)System.Math.Max(1, targetCount));
        }

        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted($"{currentCount:N0}");

        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        if (!entry.Enabled)
        {
            M3Text.Draw("Off", M3TextStyle.Faint);
        }
        else if (remaining > 0)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, success))
            {
                ImGui.TextUnformatted($"{remaining:N0}");
            }
        }
        else
        {
            M3Text.Draw("0", M3TextStyle.Faint);
        }

        ImGui.TableNextColumn();
        selectedVendor = DrawEntryVendorCell(entry, manager, isActive, vendorOptions, selectedVendor);

        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        if (selectedVendor?.Location == null)
        {
            M3Text.Draw("Unknown", M3TextStyle.Faint);
        }
        else
        {
            var here = Dalamud.ClientState.TerritoryType == selectedVendor.Location.TerritoryId;
            using (ImRaii.PushColor(ImGuiCol.Text, here ? success : s.OnSurface))
            {
                ImGui.TextUnformatted(selectedVendor.ZoneName);
            }

            M3Tooltip.Hovered(here ? $"{selectedVendor.ZoneName}\nYou are in this zone." : selectedVendor.ZoneName);
        }

        ImGui.TableNextColumn();
        if (M3Widgets.IconButton($"##vendorBuyListRemove_{entry.Id}", FontAwesomeIcon.Trash, "Remove from the list", diameter: ImGui.GetFrameHeight(),
                enabled: !isActive && !manager.IsBusy))
        {
            manager.RemoveEntry(entry.Id);
        }
    }

    private List<VendorListNpcOption> BuildVendorOptions(VendorShopEntry? liveEntry)
    {
        var options = new List<VendorListNpcOption>();
        if (liveEntry == null)
        {
            return options;
        }

        var locationCacheReady = VendorNpcLocationCache.IsInitialized;
        var selectableNpcs = VendorDevExclusions.GetSelectableNpcs(liveEntry.Npcs, "building the vendor buy list", liveEntry.ItemName);
        foreach (var npc in selectableNpcs)
        {
            var location = VendorNpcLocationCache.TryGetFirstLocation(npc.NpcId);
            if (locationCacheReady && location == null)
            {
                continue;
            }

            options.Add(new VendorListNpcOption(
                npc,
                location,
                location != null ? GetZoneName(location) : string.Empty));
        }

        return options;
    }

    private static VendorListNpcOption? FindVendorOption(IReadOnlyList<VendorListNpcOption> vendorOptions, Predicate<VendorListNpcOption> match)
    {
        for (var i = 0; i < vendorOptions.Count; i++)
        {
            if (match(vendorOptions[i]))
            {
                return vendorOptions[i];
            }
        }

        return null;
    }

    private static bool HasMultipleVendorOptions(IReadOnlyList<VendorListNpcOption> vendorOptions, VendorListNpcOption option, bool matchZone)
    {
        var matches = 0;
        for (var i = 0; i < vendorOptions.Count; i++)
        {
            var other = vendorOptions[i];
            if (other.Npc.Name.Equals(option.Npc.Name, StringComparison.OrdinalIgnoreCase)
             && (!matchZone || string.Equals(other.ZoneName, option.ZoneName, StringComparison.OrdinalIgnoreCase))
             && ++matches > 1)
            {
                return true;
            }
        }

        return false;
    }

    private static VendorListNpcOption? GetSelectedVendorOption(IReadOnlyList<VendorListNpcOption> vendorOptions, VendorNpc? resolvedVendor)
    {
        if (vendorOptions.Count == 0)
        {
            return null;
        }

        if (resolvedVendor != null)
        {
            var selectedVendor = FindVendorOption(vendorOptions, option => VendorPreferenceHelper.MatchesVendor(option.Npc, resolvedVendor))
                ?? FindVendorOption(vendorOptions, option => option.Npc.NpcId == resolvedVendor.NpcId && option.Npc.MenuShopType == resolvedVendor.MenuShopType && option.Npc.ShopId == resolvedVendor.ShopId)
                ?? FindVendorOption(vendorOptions, option => option.Npc.NpcId == resolvedVendor.NpcId && option.Npc.MenuShopType == resolvedVendor.MenuShopType)
                ?? FindVendorOption(vendorOptions, option => option.Npc.NpcId == resolvedVendor.NpcId);
            if (selectedVendor != null)
            {
                return selectedVendor;
            }
        }

        return vendorOptions[0];
    }

    private static VendorListNpcOption? DrawEntryVendorCell(VendorBuyListEntry entry, VendorBuyListManager manager, bool isActive,
        IReadOnlyList<VendorListNpcOption> vendorOptions, VendorListNpcOption? selectedVendor)
    {
        if (vendorOptions.Count == 0)
        {
            ImGui.AlignTextToFramePadding();
            M3Text.Draw(entry.VendorNpcName, M3TextStyle.Faint);
            M3Tooltip.Hovered(entry.VendorNpcName);
            return null;
        }

        selectedVendor ??= vendorOptions[0];
        if (vendorOptions.Count == 1)
        {
            var label = GetVendorNpcDisplayLabel(vendorOptions, selectedVendor);
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(label);
            M3Tooltip.Hovered(label);
            return selectedVendor;
        }

        var selectedLabel = GetVendorNpcDisplayLabel(vendorOptions, selectedVendor);
        var current = selectedVendor;
        var index = vendorOptions.IndexOf(o => VendorPreferenceHelper.MatchesVendor(o.Npc, current.Npc));
        if (M3Widgets.Combo($"##vendorBuyListVendor_{entry.Id}", ref index, vendorOptions.Count, i => GetVendorNpcDisplayLabel(vendorOptions, vendorOptions[i]),
                ImGui.GetContentRegionAvail().X, selectedLabel, enabled: !(isActive || manager.IsBusy),
                tooltip: $"{selectedLabel}\n{vendorOptions.Count} vendors sell this; pick the one to buy from.")
         && index >= 0
         && manager.UpdateEntryVendor(entry.Id, vendorOptions[index].Npc))
        {
            selectedVendor = vendorOptions[index];
        }

        return selectedVendor;
    }

    private static string GetVendorNpcDisplayLabel(IReadOnlyList<VendorListNpcOption> vendorOptions, VendorListNpcOption option)
    {
        var duplicateRoute = HasMultipleVendorOptions(vendorOptions, option, true);
        if (duplicateRoute)
        {
            var routeLabel = GetVendorRouteLabel(option.Npc);
            if (!string.IsNullOrWhiteSpace(option.ZoneName) && !string.IsNullOrWhiteSpace(routeLabel))
            {
                return $"{option.Npc.Name} ({option.ZoneName} · {routeLabel})";
            }

            if (!string.IsNullOrWhiteSpace(option.ZoneName))
            {
                return $"{option.Npc.Name} ({option.ZoneName})";
            }

            if (!string.IsNullOrWhiteSpace(routeLabel))
            {
                return $"{option.Npc.Name} [{routeLabel}]";
            }

            return $"{option.Npc.Name} [{option.Npc.NpcId}]";
        }

        var duplicateName = HasMultipleVendorOptions(vendorOptions, option, false);
        if (!duplicateName)
        {
            return option.Npc.Name;
        }

        if (!string.IsNullOrWhiteSpace(option.ZoneName))
        {
            return $"{option.Npc.Name} ({option.ZoneName})";
        }

        return $"{option.Npc.Name} [{option.Npc.NpcId}]";
    }

    private static string GetVendorRouteLabel(VendorNpc npc)
    {
        var routeParts = new List<string>();
        if (npc.GcRankIndex >= 0)
        {
            routeParts.Add($"Rank {npc.GcRankIndex + 1}");
        }

        if (npc.GcCategoryIndex >= 0)
        {
            routeParts.Add($"Category {npc.GcCategoryIndex + 1}");
        }

        if (npc.InclusionPageIndex >= 0)
        {
            routeParts.Add($"Page {npc.InclusionPageIndex + 1}");
        }

        if (npc.InclusionSubPageIndex > 0)
        {
            routeParts.Add($"Tab {npc.InclusionSubPageIndex}");
        }

        if (routeParts.Count == 0 && npc.SourceShopId != 0)
        {
            routeParts.Add($"Route {npc.SourceShopId}");
        }

        return string.Join(" / ", routeParts);
    }

    private string GetZoneName(VendorNpcLocation location)
    {
        if (_zoneNames.TryGetValue(location.TerritoryId, out var zoneName))
        {
            return zoneName;
        }

        var territorySheet = Dalamud.GameData.GetExcelSheet<TerritoryType>();
        if (territorySheet == null || !territorySheet.TryGetRow(location.TerritoryId, out var territory))
        {
            return _zoneNames[location.TerritoryId] = $"Territory {location.TerritoryId}";
        }

        zoneName = territory.PlaceName.RowId != 0
            ? territory.PlaceName.Value.Name.ToString()
            : $"Territory {location.TerritoryId}";
        _zoneNames[location.TerritoryId] = zoneName;
        return zoneName;
    }

    private ushort GetCurrencyIconId(uint currencyItemId)
    {
        if (_currencyIconIds.TryGetValue(currencyItemId, out var iconId))
        {
            return iconId;
        }

        var itemSheet = Dalamud.GameData.GetExcelSheet<Item>();
        iconId = currencyItemId != 0 && itemSheet != null && itemSheet.TryGetRow(currencyItemId, out var item)
            ? (ushort)item.Icon
            : (ushort)0;
        _currencyIconIds[currencyItemId] = iconId;
        return iconId;
    }

    private string GetCurrencyName(uint currencyItemId, string fallbackName)
    {
        if (!string.IsNullOrWhiteSpace(fallbackName))
        {
            return fallbackName;
        }

        if (_currencyNames.TryGetValue(currencyItemId, out var currencyName))
        {
            return currencyName;
        }

        var itemSheet = Dalamud.GameData.GetExcelSheet<Item>();
        currencyName = currencyItemId != 0 && itemSheet != null && itemSheet.TryGetRow(currencyItemId, out var item)
            ? item.Name.ExtractText()
            : currencyItemId == 0
                ? "Currency"
                : $"Currency {currencyItemId}";
        _currencyNames[currencyItemId] = currencyName;
        return currencyName;
    }
}
