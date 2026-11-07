using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Windowing;
using GatherBuddy.AutoGather.Collectables;
using GatherBuddy.Config;
using GatherBuddy.Helpers;
using GatherBuddy.Vulcan.Vendors;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ImRaii = ElliLib.Raii.ImRaii;

namespace GatherBuddy.Gui;

public sealed class CollectablesWindow : Window
{
    public const string WindowId = "Collectables###GatherBuddyCollectablesWindow";
    private const string SetupGuidePopupId = "Collectables Setup Guide###GatherBuddyCollectablesSetupGuide";
    private static readonly ImGuiEx.RequiredPluginInfo[] RequiredCollectablePlugins =
    [
        new("InventoryTools", "Allagan Tools"),
        new(CollectableTurnInRequirements.AllaganItemSearchInternalName, "Allagan Item Search"),
    ];

    private bool _wasFocusedLastFrame;

    public CollectablesWindow()
        : base(WindowId)
    {
        Size = VulcanUiScaling.Scaled(860f, 560f);
        SizeCondition = ImGuiCond.FirstUseEver;
        RespectCloseHotkey = true;
        ShowCloseButton = true;
        IsOpen = false;
    }

    public void Open()
        => IsOpen = true;

    public override void Draw()
    {
        using var theme = VulcanUiStyle.PushTheme();

        var isFocused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
        if (isFocused)
        {
            GatherBuddy.ControllerSupport?.UpdateFocusedWindow(WindowId);
            _wasFocusedLastFrame = true;
        }
        else if (_wasFocusedLastFrame)
        {
            GatherBuddy.ControllerSupport?.UpdateFocusedWindow(null);
            _wasFocusedLastFrame = false;
        }

        var manager = GatherBuddy.CollectableManager;
        var config = GatherBuddy.Config.CollectableConfig;
        var routes = CollectableTurnInRouteResolver.GetAvailableRoutes();
        var selectedRoute = CollectableTurnInRouteResolver.ResolvePreferredRoute(config.PreferredTurnInRoute, routes);
        var vendorBuyListManager = GatherBuddy.VendorBuyListManager;
        var selectedGatheringList = vendorBuyListManager.Lists.FirstOrDefault(list => list.Id == config.GatheringPurchaseListId);
        var selectedCraftingList = vendorBuyListManager.Lists.FirstOrDefault(list => list.Id == config.CraftingPurchaseListId);

        ImGui.TextColored(ImGuiColors.DalamudGrey3, "Configure shared collectables turn-ins, purchase automation, and manual runs.");
        ImGui.Spacing();
        DrawExecutionControls(manager);
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        DrawAutomationSettings(manager, config);
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        DrawTurnInRouteSettings(routes, selectedRoute);
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        DrawPurchaseSettings(config, vendorBuyListManager, selectedGatheringList, selectedCraftingList);
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        DrawStatus(manager, selectedGatheringList, selectedCraftingList);
        DrawSetupGuidePopup();
    }

    private static void DrawExecutionControls(CollectableManager manager)
    {
        var turnInsAvailable = CollectableTurnInRequirements.IsAvailable;
        if (ImGui.Button("Setup Guide", VulcanUiScaling.Scaled(120f, 0f)))
        {
            ImGui.OpenPopup(SetupGuidePopupId);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Explain how to build and assign collectables purchase lists using Vulcan Vendors and Vendor Buy Lists.");
        }

        ImGui.SameLine();
        if (manager.IsRunning)
        {
            if (ImGui.Button("Stop Collectables Run", VulcanUiScaling.Scaled(180f, 0f)))
            {
                manager.Stop();
            }
        }
        else
        {
            using var disabledRunButton = ImRaii.Disabled(!turnInsAvailable);
            if (ImGui.Button("Run Turn-Ins Now", VulcanUiScaling.Scaled(180f, 0f)) && turnInsAvailable)
            {
                manager.Start(CollectableRunSource.Manual);
            }

            if (ImGui.IsItemHovered(turnInsAvailable ? ImGuiHoveredFlags.None : ImGuiHoveredFlags.AllowWhenDisabled))
            {
                ImGui.SetTooltip(turnInsAvailable
                    ? "Runs collectable turn-ins immediately."
                    : CollectableTurnInRequirements.UnavailableHelpText);
            }
        }

        ImGuiEx.PluginAvailabilityIndicator(RequiredCollectablePlugins, "Requires one of these plugins:", all: false);

        if (ImGui.Button("Open Vulcan", VulcanUiScaling.Scaled(120f, 0f)))
        {
            GatherBuddy.VulcanWindow?.RestoreWindow();
        }

        ImGui.SameLine();
        if (ImGui.Button("Open Vendor Buy Lists", VulcanUiScaling.Scaled(170f, 0f)))
        {
            GatherBuddy.VendorBuyListWindow?.Open();
        }
    }

    private static void DrawAutomationSettings(CollectableManager manager, CollectableConfig config)
    {
        var turnInsAvailable = CollectableTurnInRequirements.IsAvailable;
        var autoTurnIn = config.AutoTurnInCollectables;
        var previousHardFailReason = config.AutoTurnInHardFailReason;
        if (!turnInsAvailable && !autoTurnIn)
        {
            using var disabledAutoTurnIn = ImRaii.Disabled(true);
            ImGui.Checkbox("Auto turn in collectables", ref autoTurnIn);
        }
        else if (ImGui.Checkbox("Auto turn in collectables", ref autoTurnIn))
        {
            config.AutoTurnInCollectables = autoTurnIn;
            if (autoTurnIn)
            {
                config.AutoTurnInHardFailReason = string.Empty;
                if (!manager.IsRunning && string.Equals(manager.StatusText, previousHardFailReason, StringComparison.Ordinal))
                {
                    manager.ClearStatus();
                }
            }
            GatherBuddy.Config.Save();
        }
        var autoTurnInHovered = ImGui.IsItemHovered(turnInsAvailable || autoTurnIn ? ImGuiHoveredFlags.None : ImGuiHoveredFlags.AllowWhenDisabled);
        ImGuiEx.PluginAvailabilityIndicator(RequiredCollectablePlugins, "Requires one of these plugins:", all: false);
        if (autoTurnInHovered)
        {
            ImGui.SetTooltip(turnInsAvailable
                ? "Lets Auto-Gather and Vulcan queues run collectable turn-ins automatically."
                : CollectableTurnInRequirements.UnavailableHelpText);
        }

        if (!turnInsAvailable)
        {
            DrawWrappedColoredText(ImGuiColors.DalamudYellow, CollectableTurnInRequirements.UnavailableHelpText);
            ImGui.Spacing();
        }

        if (!config.AutoTurnInCollectables && !string.IsNullOrWhiteSpace(config.AutoTurnInHardFailReason))
        {
            DrawWrappedColoredText(ImGuiColors.DalamudRed, "Auto turn-ins were forced off after a collectables hard failure.");
            DrawWrappedColoredText(ImGuiColors.DalamudYellow, config.AutoTurnInHardFailReason);
            ImGui.Spacing();
        }

        var runPurchaseList = config.BuyAfterEachCollect;
        if (ImGui.Checkbox("Run vendor purchase list after turn-in", ref runPurchaseList))
        {
            config.BuyAfterEachCollect = runPurchaseList;
            GatherBuddy.Config.Save();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Runs the selected buy list after turn-ins, or during scrip-cap recovery if space must be cleared.");
        }

        var returnHome = HomeNavigationHelper.ShouldReturnHomeAfterCollectables();
        if (ImGui.Checkbox("Return home after Vulcan queue turn-ins", ref returnHome))
        {
            GatherBuddy.Config.AutoGatherConfig.GoHomeWhenIdle = returnHome;
            GatherBuddy.Config.Save();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("After a queue collectables interruption, return home before crafting resumes.");
        }

        ImGui.Spacing();
        var useInventoryFullThreshold = config.UseInventoryFullThreshold;
        if (ImGui.Checkbox("Use inventory-full threshold", ref useInventoryFullThreshold))
        {
            config.UseInventoryFullThreshold = useInventoryFullThreshold;
            GatherBuddy.Config.Save();
        }

        ImGui.SameLine();
        if (useInventoryFullThreshold)
        {
            var inventoryThreshold = config.InventoryFullThreshold;
            ImGui.SetNextItemWidth(VulcanUiScaling.Scaled(130f));
            if (ImGui.DragInt("Inventory threshold", ref inventoryThreshold, 1f, 1, 140))
            {
                config.InventoryFullThreshold = Math.Clamp(inventoryThreshold, 1, 140);
                GatherBuddy.Config.Save();
            }
        }
        else
        {
            var collectableThreshold = config.CollectableInventoryThreshold;
            ImGui.SetNextItemWidth(VulcanUiScaling.Scaled(130f));
            if (ImGui.DragInt("Collectable threshold", ref collectableThreshold, 1f, 1, 140))
            {
                config.CollectableInventoryThreshold = Math.Clamp(collectableThreshold, 1, 140);
                GatherBuddy.Config.Save();
            }
        }
    }

    private static void DrawTurnInRouteSettings(IReadOnlyList<CollectableTurnInRouteOption> routes, CollectableTurnInRouteOption? selectedRoute)
    {
        ImGui.TextColored(ImGuiColors.ParsedGold, "Turn-In Route");
        if (routes.Count == 0)
        {
            var status = CollectableTurnInRouteResolver.HasLookupData
                ? VendorNpcLocationCache.IsInitializing
                    ? "Collectables route locations are still loading."
                    : "No collectables turn-in routes are currently available."
                : "Collectables route data is unavailable.";
            ImGui.TextColored(ImGuiColors.DalamudGrey3, status);
            return;
        }

        var previewLabel = selectedRoute?.DisplayName ?? "Select a turn-in route...";
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("Preferred turn-in route", previewLabel))
        {
            foreach (var route in routes)
            {
                var isSelected = selectedRoute != null
                    && route.ShopId == selectedRoute.ShopId
                    && route.Vendor.NpcId == selectedRoute.Vendor.NpcId
                    && route.Location.TerritoryId == selectedRoute.Location.TerritoryId
                    && route.Location.MapRowId == selectedRoute.Location.MapRowId
                    && Vector3.DistanceSquared(route.Location.Position, selectedRoute.Location.Position) < 0.01f;
                if (!ImGui.Selectable(route.DisplayName, isSelected))
                {
                    continue;
                }

                GatherBuddy.Config.CollectableConfig.PreferredTurnInRoute = CollectableTurnInRouteResolver.ToPreference(route);
                GatherBuddy.Config.Save();
            }
            ImGui.EndCombo();
        }

        if (selectedRoute != null)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey3,
                $"NPC: {selectedRoute.Vendor.Name} · Territory: {selectedRoute.ZoneName} · Source: {selectedRoute.Location.Source}");
        }
    }


    private static void DrawSetupGuidePopup()
    {
        ImGui.SetNextWindowSize(VulcanUiScaling.Scaled(640f, 430f), ImGuiCond.Appearing);
        if (!ImGui.BeginPopup(SetupGuidePopupId, ImGuiWindowFlags.NoResize))
        {
            return;
        }

        ImGui.TextColored(ImGuiColors.ParsedGold, "Collectables Setup Guide");
        DrawWrappedText("Use Vulcan's Vendors tab to build the scrip purchase list, then assign that list here so collectables runs know what to buy after turn-ins.");
        ImGui.Spacing();

        if (ImGui.Button("Open Vulcan", VulcanUiScaling.Scaled(120f, 0f)))
        {
            GatherBuddy.VulcanWindow?.RestoreWindow();
        }

        ImGui.SameLine();
        if (ImGui.Button("Open Vendor Buy Lists", VulcanUiScaling.Scaled(170f, 0f)))
        {
            GatherBuddy.VendorBuyListWindow?.Open();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawSetupGuideStep(
            "1. Build the purchase list in Vulcan Vendors",
            "Open Vulcan, switch to the Vendors tab, search for the scrip item you want, set Qty, then use the + button to add it to the active vendor list. Right-click the + button if you want to create a new list or add the item to a different existing list.");
        DrawSetupGuideStep(
            "2. Review the list in Vendor Buy Lists",
            "Open Vendor Buy Lists to rename the list, adjust target quantities, and confirm the selected vendor route if an item has multiple NPC options.");
        DrawSetupGuideStep(
            "3. Assign the list in Collectables",
            "Choose a list under Gathering collectables purchase list for Auto-Gather runs and gathering manual turn-ins. Choose a list under Crafting collectables purchase list for Vulcan queue runs and crafting manual turn-ins. The 'Use Active Vendor List' buttons copy the currently active vendor list into that slot.");
        DrawSetupGuideStep(
            "4. Enable the purchase behavior you want",
            "Turn on Run vendor purchase list after turn-in if you want collectables runs to spend scrips automatically. Reserve scrips keeps a buffer so the list does not spend your last scrip. Turn on Auto turn in collectables if you want Auto-Gather or Vulcan queue runs to trigger turn-ins automatically.");

        ImGui.Spacing();
        DrawWrappedColoredText(ImGuiColors.DalamudYellow,
            "If turn-ins or purchase automation are unavailable, install or enable Allagan Tools or Allagan Item Search first.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button("Close", VulcanUiScaling.Scaled(100f, 0f)))
        {
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }
    private static void DrawPurchaseSettings(
        CollectableConfig config,
        VendorBuyListManager manager,
        VendorBuyListDefinition? selectedGatheringList,
        VendorBuyListDefinition? selectedCraftingList)
    {
        ImGui.TextColored(ImGuiColors.ParsedGold, "Purchase Lists");

        var reserveScripAmount = config.ReserveScripAmount;
        ImGui.SetNextItemWidth(VulcanUiScaling.Scaled(130f));
        if (ImGui.DragInt("Reserve scrips", ref reserveScripAmount, 1f, 0, 4000))
        {
            config.ReserveScripAmount = Math.Clamp(reserveScripAmount, 0, 4000);
            GatherBuddy.Config.Save();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Keeps at least this many of each scrip when collectables buy lists spend scrips.");
        }

        ImGui.Spacing();

        if (manager.Lists.Count == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey3, "No vendor buy lists are available.");
            return;
        }

        DrawPurchaseListSelector(
            "Gathering collectables purchase list",
            config.GatheringPurchaseListId,
            id => config.GatheringPurchaseListId = id,
            manager);
        if (selectedGatheringList != null)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey3, GetPurchaseListSummary(manager, selectedGatheringList));
        }

        using (var disabled = ImRaii.Disabled(manager.ActiveList == null || manager.ActiveList.Id == config.GatheringPurchaseListId))
        {
            if (ImGui.Button("Use Active Vendor List for Gathering", VulcanUiScaling.Scaled(250f, 0f)) && manager.ActiveList != null)
            {
                config.GatheringPurchaseListId = manager.ActiveList.Id;
                GatherBuddy.Config.Save();
            }
        }

        ImGui.Spacing();
        DrawPurchaseListSelector(
            "Crafting collectables purchase list",
            config.CraftingPurchaseListId,
            id => config.CraftingPurchaseListId = id,
            manager);
        if (selectedCraftingList != null)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey3, GetPurchaseListSummary(manager, selectedCraftingList));
        }

        using var disabledCrafting = ImRaii.Disabled(manager.ActiveList == null || manager.ActiveList.Id == config.CraftingPurchaseListId);
        if (ImGui.Button("Use Active Vendor List for Crafting", VulcanUiScaling.Scaled(250f, 0f)) && manager.ActiveList != null)
        {
            config.CraftingPurchaseListId = manager.ActiveList.Id;
            GatherBuddy.Config.Save();
        }
    }

    private static void DrawPurchaseListSelector(string label, Guid selectedListId, Action<Guid> setter, VendorBuyListManager manager)
    {
        var selectedList = manager.Lists.FirstOrDefault(list => list.Id == selectedListId);
        var previewLabel = selectedList?.Name ?? "No list selected";
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo(label, previewLabel))
        {
            return;
        }

        if (ImGui.Selectable("No list selected", selectedListId == Guid.Empty))
        {
            setter(Guid.Empty);
            GatherBuddy.Config.Save();
        }

        foreach (var list in manager.Lists.OrderBy(list => list.CreatedAt))
        {
            var isSelected = list.Id == selectedListId;
            if (!ImGui.Selectable(list.Name, isSelected))
            {
                continue;
            }

            setter(list.Id);
            GatherBuddy.Config.Save();
        }

        ImGui.EndCombo();
    }

    private static string GetPurchaseListSummary(VendorBuyListManager manager, VendorBuyListDefinition selectedList)
    {
        var pendingCount = selectedList.Entries.Count(managerEntry => manager.GetRemainingQuantity(managerEntry) > 0);
        return $"{selectedList.Entries.Count} entry(s) · {pendingCount} pending with current inventory";
    }

    private static void DrawStatus(
        CollectableManager manager,
        VendorBuyListDefinition? selectedGatheringList,
        VendorBuyListDefinition? selectedCraftingList)
    {
        ImGui.TextColored(ImGuiColors.ParsedGold, "Status");
        var stateColor = manager.IsRunning ? ImGuiColors.ParsedGold : ImGuiColors.DalamudGrey3;
        DrawWrappedColoredText(stateColor, string.IsNullOrWhiteSpace(manager.StatusText) ? "Idle" : manager.StatusText);
        if (!CollectableTurnInRequirements.IsAvailable)
        {
            DrawWrappedColoredText(ImGuiColors.DalamudYellow, CollectableTurnInRequirements.UnavailableHelpText);
        }

        CollectableInventoryHelper.InitializeAsync();
        if (!CollectableInventoryHelper.IsTurnInItemMetadataReady)
        {
            var status = CollectableInventoryHelper.IsTurnInItemMetadataLoading
                ? "Collectables item data is still loading."
                : "Collectables item data is unavailable.";
            DrawWrappedColoredText(ImGuiColors.DalamudGrey3, status);
        }
        else
        {
            var thresholdState = CollectableInventoryHelper.GetThresholdState(GatherBuddy.Config.CollectableConfig);
            ImGui.TextColored(ImGuiColors.DalamudGrey3,
                $"Collectables: {thresholdState.CollectableCount} · Inventory: {thresholdState.UsedSlots}/{thresholdState.TotalSlots}");
        }
        if (!GatherBuddy.Config.CollectableConfig.BuyAfterEachCollect)
        {
            return;
        }

        if (selectedGatheringList == null)
        {
            DrawWrappedColoredText(ImGuiColors.DalamudYellow, "Select a gathering purchase list for Auto-Gather and gathering manual turn-ins.");
        }

        if (selectedCraftingList == null)
        {
            DrawWrappedColoredText(ImGuiColors.DalamudYellow, "Select a crafting purchase list for Vulcan and crafting manual turn-ins.");
        }
    }

    private static void DrawWrappedColoredText(Vector4 color, string text)
    {
        ImGui.PushTextWrapPos();
        ImGui.TextColored(color, text);
        ImGui.PopTextWrapPos();
    }

    private static void DrawWrappedText(string text)
    {
        ImGui.PushTextWrapPos();
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
    }

    private static void DrawSetupGuideStep(string title, string description)
    {
        ImGui.TextColored(ImGuiColors.ParsedGold, title);
        DrawWrappedColoredText(ImGuiColors.DalamudGrey3, description);
        ImGui.Spacing();
    }
}
