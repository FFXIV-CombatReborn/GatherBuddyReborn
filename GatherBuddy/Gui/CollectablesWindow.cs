using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using GatherBuddy.AutoGather.Collectables;
using GatherBuddy.Config;
using GatherBuddy.Helpers;
using GatherBuddy.RebornUI.Settings;
using GatherBuddy.Vulcan.Vendors;
using RebornMaterial;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace GatherBuddy.Gui;

public sealed class CollectablesWindow : M3PanelWindow
{
    public const string WindowId = "Collectables###GatherBuddyCollectablesWindow";
    private const string SetupGuideDialogId = "Collectables Setup Guide###GatherBuddyCollectablesSetupGuide";

    private static readonly M3PluginRequirement[] RequiredCollectablePlugins =
    [
        new("InventoryTools", "Allagan Tools"),
        new(CollectableTurnInRequirements.AllaganItemSearchInternalName, "Allagan Item Search"),
    ];

    private static readonly M3WindowAction[] CollectablesWindowActions =
    [
        new("##collectables_guide", FontAwesomeIcon.QuestionCircle, "Setup guide"),
        new("##collectables_vulcan", FontAwesomeIcon.Hammer, "Open Vulcan"),
        new("##collectables_vendor_lists", FontAwesomeIcon.ShoppingCart, "Open Vendor Buy Lists"),
    ];

    private static readonly Vector2 DefaultSize = new(760f, 680f);
    private static readonly WindowSizeConstraints DefaultConstraints = new()
    {
        MinimumSize = new Vector2(420f, 320f),
        MaximumSize = new Vector2(5000f, 5000f),
    };

    private bool _openSetupGuide;

    public CollectablesWindow()
        : base(WindowId, "Collectables", DefaultSize, DefaultConstraints)
    {
        RespectCloseHotkey = true;
        IsOpen = false;
    }

    public void Open()
        => OpenAndRestore();

    protected override M3WindowAction[] WindowActions
        => CollectablesWindowActions;

    protected override void OnWindowAction(int index)
    {
        switch (index)
        {
            case 0:
                _openSetupGuide = true;
                break;
            case 1:
                GatherBuddy.VulcanWindow?.RestoreWindow();
                break;
            case 2:
                GatherBuddy.VendorBuyListWindow?.Open();
                break;
        }
    }

    protected override (string Text, Vector4? Color) Status
    {
        get
        {
            var manager = GatherBuddy.CollectableManager;
            if (manager == null)
            {
                return (string.Empty, null);
            }

            var text = string.IsNullOrWhiteSpace(manager.StatusText) ? "Idle" : manager.StatusText;
            return (text, manager.IsRunning ? M3.Scheme.Primary : null);
        }
    }

    protected override void DrawContents()
    {
        var manager = GatherBuddy.CollectableManager;
        var config = GatherBuddy.Config.CollectableConfig;
        var routes = CollectableTurnInRouteResolver.GetAvailableRoutes();
        var selectedRoute = CollectableTurnInRouteResolver.ResolvePreferredRoute(config.PreferredTurnInRoute, routes);
        var vendorBuyListManager = GatherBuddy.VendorBuyListManager;
        var selectedGatheringList = VendorBuyListManager.GetList(config.GatheringPurchaseListId);
        var selectedCraftingList = VendorBuyListManager.GetList(config.CraftingPurchaseListId);

        if (_openSetupGuide)
        {
            _openSetupGuide = false;
            M3Dialog.Open(SetupGuideDialogId);
        }

        DrawTurnInCard(manager, config);
        DrawAutomationCard(manager, config);
        DrawRouteCard(routes, selectedRoute);
        DrawPurchaseCard(config, selectedGatheringList, selectedCraftingList);
        DrawStatusCard(selectedGatheringList, selectedCraftingList);
        DrawSetupGuide();
    }

    private static void DrawTurnInCard(CollectableManager manager, CollectableConfig config)
    {
        using var card = M3Card.Begin("collectables_turn_in", "Turn-ins", FontAwesomeIcon.ExchangeAlt, M3.AccentFor(0), M3CardStyle.Filled,
            "Shared collectables turn-ins and purchase automation for Auto-Gather and Vulcan.");

        var turnInsAvailable = CollectableTurnInRequirements.IsAvailable;
        if (!turnInsAvailable)
        {
            M3Widgets.Banner("##collectables_unavailable", CollectableTurnInRequirements.UnavailableHelpText, M3Severity.Warning,
                FontAwesomeIcon.ExclamationTriangle);
            ImGui.Dummy(new Vector2(0f, M3.Space1));
        }

        if (!config.AutoTurnInCollectables && !string.IsNullOrWhiteSpace(config.AutoTurnInHardFailReason))
        {
            M3Widgets.Banner("##collectables_hard_fail",
                $"Auto turnins were disabled after a collectables failure: {config.AutoTurnInHardFailReason}", M3Severity.Error,
                FontAwesomeIcon.ExclamationCircle);
            ImGui.Dummy(new Vector2(0f, M3.Space1));
        }

        var running = manager.IsRunning;
        var buttonLabel = running ? "Stop run" : "Run turn-ins";
        var buttonIcon = running ? FontAwesomeIcon.Stop : FontAwesomeIcon.Play;
        var pill = M3Widgets.PluginPillSize(RequiredCollectablePlugins, true);
        var button = new Vector2(M3Widgets.ButtonWidth(buttonIcon, buttonLabel), M3Widgets.ButtonHeight);
        var gap = M3.Space2;
        var controlSize = new Vector2(pill.X + gap + button.X, MathF.Max(pill.Y, button.Y));

        var row = M3SettingRow.Begin(running ? "A collectables run is going" : "Turn in now",
            running ? "Stop it here if you need to." : "Runs collectable turn-ins straight away.", controlSize);
        var origin = row.ControlPosition;
        ImGui.SetCursorScreenPos(origin + new Vector2(0f, (controlSize.Y - pill.Y) * 0.5f));
        M3Widgets.PluginPill("##collectables_plugins", RequiredCollectablePlugins, true);
        ImGui.SetCursorScreenPos(origin + new Vector2(pill.X + gap, (controlSize.Y - button.Y) * 0.5f));
        if (running)
        {
            if (M3Widgets.Button("##collectables_stop", buttonLabel, M3ButtonStyle.Danger, buttonIcon))
            {
                manager.Stop();
            }
        }
        else if (M3Widgets.Button("##collectables_run", buttonLabel, M3ButtonStyle.Filled, buttonIcon, enabled: turnInsAvailable,
                     tooltip: turnInsAvailable ? null : CollectableTurnInRequirements.UnavailableHelpText) && turnInsAvailable)
        {
            manager.Start(CollectableRunSource.Manual);
        }

        M3SettingRow.End(row);
    }

    private static void DrawAutomationCard(CollectableManager manager, CollectableConfig config)
    {
        using var card = M3Card.Begin("collectables_automation", "Automation", FontAwesomeIcon.Robot, M3.AccentFor(1));

        var turnInsAvailable = CollectableTurnInRequirements.IsAvailable;
        var previousHardFailReason = config.AutoTurnInHardFailReason;
        SettingRows.PluginSwitch("Auto turn in collectables",
            turnInsAvailable ? "Lets Auto-Gather and Vulcan queues run collectable turn-ins automatically." : CollectableTurnInRequirements.UnavailableHelpText,
            config.AutoTurnInCollectables, v =>
            {
                config.AutoTurnInCollectables = v;
                if (!v)
                {
                    return;
                }

                config.AutoTurnInHardFailReason = string.Empty;
                if (!manager.IsRunning && string.Equals(manager.StatusText, previousHardFailReason, StringComparison.Ordinal))
                {
                    manager.ClearStatus();
                }
            }, true, turnInsAvailable || config.AutoTurnInCollectables, RequiredCollectablePlugins);

        SettingRows.Switch("Run vendor purchase list after turn-in",
            "Runs the selected buy list after turn-ins, or during scrip-cap recovery if space must be cleared.",
            config.BuyAfterEachCollect, v => config.BuyAfterEachCollect = v);
        SettingRows.Switch("Return home after Vulcan queue turn-ins",
            "After a queue is interrupted for collectables, return home before crafting resumes.",
            HomeNavigationHelper.ShouldReturnHomeAfterCollectables(), v => GatherBuddy.Config.AutoGatherConfig.GoHomeWhenIdle = v);
        SettingRows.Switch("Use inventory-full threshold",
            "Turn in when your inventory fills up, instead of after carrying a number of collectables.",
            config.UseInventoryFullThreshold, v => config.UseInventoryFullThreshold = v);

        using var sub = M3SubGroup.Begin();
        if (config.UseInventoryFullThreshold)
        {
            SettingRows.Number("Inventory threshold", "Turn in once this many inventory slots are used.",
                config.InventoryFullThreshold, 1, 140, v => config.InventoryFullThreshold = v, 1, "slots");
        }
        else
        {
            SettingRows.Number("Collectable threshold", "Turn in once you are carrying this many collectables.",
                config.CollectableInventoryThreshold, 1, 140, v => config.CollectableInventoryThreshold = v);
        }
    }

    private static void DrawRouteCard(IReadOnlyList<CollectableTurnInRouteOption> routes, CollectableTurnInRouteOption? selectedRoute)
    {
        using var card = M3Card.Begin("collectables_route", "Turn-in route", FontAwesomeIcon.Route, M3.AccentFor(2), M3CardStyle.Filled,
            "Where collectables runs go to turn in.");

        if (routes.Count == 0)
        {
            var status = CollectableTurnInRouteResolver.HasLookupData
                ? VendorNpcLocationCache.IsInitializing
                    ? "Collectables route locations are still loading."
                    : "No collectables turn-in routes are available right now."
                : "Collectables route data is unavailable.";
            M3Text.Draw(status, M3TextStyle.Muted, true);
            return;
        }

        var index = -1;
        if (selectedRoute != null)
        {
            for (var i = 0; i < routes.Count; ++i)
            {
                if (SameRoute(routes[i], selectedRoute))
                {
                    index = i;
                    break;
                }
            }
        }

        var supporting = selectedRoute != null
            ? $"NPC: {selectedRoute.Vendor.Name} · Territory: {selectedRoute.ZoneName} · Source: {selectedRoute.Location.Source}"
            : "Pick where collectables runs turn in.";
        var previous = index;
        if (M3Widgets.RowCombo("Preferred turn-in route", ref index, routes.Count, i => routes[i].DisplayName, supporting, search: routes.Count > 8)
         && index != previous && index >= 0 && index < routes.Count)
        {
            GatherBuddy.Config.CollectableConfig.PreferredTurnInRoute = CollectableTurnInRouteResolver.ToPreference(routes[index]);
            GatherBuddy.Config.Save();
        }
    }

    private static bool SameRoute(CollectableTurnInRouteOption route, CollectableTurnInRouteOption selected)
        => route.ShopId == selected.ShopId
         && route.Vendor.NpcId == selected.Vendor.NpcId
         && route.Location.TerritoryId == selected.Location.TerritoryId
         && route.Location.MapRowId == selected.Location.MapRowId
         && Vector3.DistanceSquared(route.Location.Position, selected.Location.Position) < 0.01f;

    private static void DrawPurchaseCard(CollectableConfig config, VendorBuyListDefinition? selectedGatheringList,
        VendorBuyListDefinition? selectedCraftingList)
    {
        using var card = M3Card.Begin("collectables_purchase", "Purchase lists", FontAwesomeIcon.ShoppingCart, M3.AccentFor(0), M3CardStyle.Filled,
            "The vendor buy lists that collectables runs spend scrips on.");

        SettingRows.Number("Reserve scrips", "Keep at least this many of each scrip when buy lists spend scrips.",
            config.ReserveScripAmount, 0, 4000, v => config.ReserveScripAmount = v, 50);

        if (VendorBuyListManager.Lists.Count == 0)
        {
            M3Text.Draw("No vendor buy lists yet. Make one in Vendor Buy Lists, or from Vulcan's Vendors page.", M3TextStyle.Muted, true);
            return;
        }

        DrawPurchaseList("Gathering purchase list", "For Auto-Gather runs and gathering manual turn-ins.", config.GatheringPurchaseListId,
            id => config.GatheringPurchaseListId = id, selectedGatheringList);
        DrawPurchaseList("Crafting purchase list", "For Vulcan queue runs and crafting manual turn-ins.", config.CraftingPurchaseListId,
            id => config.CraftingPurchaseListId = id, selectedCraftingList);
    }

    private static void DrawPurchaseList(string label, string purpose, Guid selectedListId, Action<Guid> setter,
        VendorBuyListDefinition? selectedList)
    {
        var lists = VendorBuyListManager.GetListsByCreation(false);
        var index = selectedListId == Guid.Empty ? 0 : lists.FindIndex(list => list.Id == selectedListId) + 1;
        var supporting = selectedList != null ? $"{purpose} {GetPurchaseListSummary(selectedList)}." : purpose;
        var previous = index;
        if (M3Widgets.RowCombo($"{label}##{label}", ref index, lists.Count + 1, i => i == 0 ? "No list selected" : lists[i - 1].Name, supporting,
                search: lists.Count > 8)
         && index != previous && index >= 0 && index <= lists.Count)
        {
            setter(index == 0 ? Guid.Empty : lists[index - 1].Id);
            GatherBuddy.Config.Save();
        }

        var active = VendorBuyListManager.ActiveList;
        using var sub = M3SubGroup.Begin();
        if (M3Widgets.RowButton($"Use the active vendor list##{label}_active", "Use active list",
                active != null ? $"The active list is {active.Name}." : "There is no active vendor list.", M3ButtonStyle.Text, FontAwesomeIcon.ArrowLeft,
                active != null && active.Id != selectedListId)
         && active != null)
        {
            setter(active.Id);
            GatherBuddy.Config.Save();
        }
    }

    private static string GetPurchaseListSummary(VendorBuyListDefinition selectedList)
    {
        var pendingCount = VendorBuyListManager.GetPendingEntryCount(selectedList);
        return $"{selectedList.Entries.Count} entry(s) · {pendingCount} pending with current inventory";
    }

    private static void DrawStatusCard(VendorBuyListDefinition? selectedGatheringList, VendorBuyListDefinition? selectedCraftingList)
    {
        using var card = M3Card.Begin("collectables_status", "Inventory", FontAwesomeIcon.Box, M3.AccentFor(1));

        CollectableInventoryHelper.InitializeAsync();
        if (!CollectableInventoryHelper.IsTurnInItemMetadataReady)
        {
            M3Text.Draw(CollectableInventoryHelper.IsTurnInItemMetadataLoading
                ? "Collectables item data is still loading."
                : "Collectables item data is unavailable.", M3TextStyle.Muted, true);
        }
        else
        {
            var thresholdState = CollectableInventoryHelper.GetThresholdState(GatherBuddy.Config.CollectableConfig);
            M3Widgets.RowValue("Collectables carried", thresholdState.CollectableCount.ToString());
            M3Widgets.RowValue("Inventory", $"{thresholdState.UsedSlots} / {thresholdState.TotalSlots}");
        }

        if (!GatherBuddy.Config.CollectableConfig.BuyAfterEachCollect)
        {
            return;
        }

        if (selectedGatheringList == null)
        {
            ImGui.Dummy(new Vector2(0f, M3.Space1));
            M3Widgets.Banner("##collectables_no_gathering_list", "Select a gathering purchase list for Auto-Gather and gathering manual turn-ins.",
                M3Severity.Warning, FontAwesomeIcon.ExclamationTriangle);
        }

        if (selectedCraftingList == null)
        {
            ImGui.Dummy(new Vector2(0f, M3.Space1));
            M3Widgets.Banner("##collectables_no_crafting_list", "Select a crafting purchase list for Vulcan and crafting manual turn-ins.",
                M3Severity.Warning, FontAwesomeIcon.ExclamationTriangle);
        }
    }

    private static void DrawSetupGuide()
    {
        using var dialog = M3Dialog.Begin(SetupGuideDialogId, "Collectables setup guide",
            "Build the scrip purchase list in Vulcan's Vendors page, then assign it here so collectables runs know what to buy after turn-ins.",
            FontAwesomeIcon.QuestionCircle, 600f);
        if (!dialog.IsOpen)
        {
            return;
        }

        var width = ImGui.GetContentRegionAvail().X;
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width);
        GuideStep("1. Build the purchase list in Vulcan Vendors",
            "Open Vulcan, switch to the Vendors page, search for the scrip item you want, set the quantity, then use the + button to add it to the active vendor list. "
          + "Right-click the + button to make a new list or add the item to another list.");
        GuideStep("2. Review the list in Vendor Buy Lists",
            "Open Vendor Buy Lists to rename the list, adjust target quantities, and confirm the vendor route if an item has several NPCs.");
        GuideStep("3. Assign the list in Collectables",
            "Pick a gathering purchase list for Auto-Gather runs and gathering manual turn-ins, and a crafting purchase list for Vulcan queue runs and "
          + "crafting manual turn-ins. 'Use active list' copies the active vendor list into that slot.");
        GuideStep("4. Turn on the purchase behaviour you want",
            "Turn on 'Run vendor purchase list after turn-in' to let collectables runs spend scrips. Reserve scrips keeps a buffer so the list never spends your last scrip. "
          + "Turn on 'Auto turn in collectables' to let Auto-Gather and Vulcan queues start turn-ins on their own.");
        ImGui.PopTextWrapPos();

        ImGui.Dummy(new Vector2(0f, M3.Space1));
        M3Widgets.Banner("##collectables_guide_plugins",
            "If turn-ins or purchase automation are unavailable, install or enable Allagan Tools or Allagan Item Search first.", M3Severity.Info,
            FontAwesomeIcon.InfoCircle);

        var pressed = M3Dialog.Actions(
        [
            new M3DialogAction("Open Vulcan", M3ButtonStyle.Text, FontAwesomeIcon.Hammer),
            new M3DialogAction("Open Vendor Buy Lists", M3ButtonStyle.Text, FontAwesomeIcon.ShoppingCart),
            new M3DialogAction("Close", M3ButtonStyle.Filled),
        ]);
        switch (pressed)
        {
            case 0:
                GatherBuddy.VulcanWindow?.RestoreWindow();
                break;
            case 1:
                GatherBuddy.VendorBuyListWindow?.Open();
                break;
        }
    }

    private static void GuideStep(string title, string description)
    {
        M3Text.Draw(title, M3TextStyle.Title);
        M3Text.Draw(description, M3TextStyle.Muted, true);
        ImGui.Dummy(new Vector2(0f, M3.Space2));
    }
}
