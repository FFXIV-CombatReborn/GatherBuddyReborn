using System.Collections.Generic;
using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Windowing;
using GatherBuddy.AutoGather.Collectables;
using GatherBuddy.AutoGather.Collectables.Data;
using GatherBuddy.Config;
using GatherBuddy.Helpers;
using GatherBuddy.Vulcan.Vendors;
using ImRaii = ElliLib.Raii.ImRaii;

namespace GatherBuddy.Gui;

public sealed class CollectablesWindow : Window
{
    public const string WindowId = "Collectables###GatherBuddyCollectablesWindow";

    private bool _wasFocusedLastFrame;

    public CollectablesWindow()
        : base(WindowId)
    {
        Size = new Vector2(860f, 560f);
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
    }

    private static void DrawExecutionControls(CollectableManager manager)
    {
        if (manager.IsRunning)
        {
            if (ImGui.Button("Stop Collectables Run", new Vector2(180f, 0f)))
                manager.Stop();
        }
        else
        {
            if (ImGui.Button("Run Turn-Ins Now", new Vector2(180f, 0f)))
                manager.Start(CollectableRunSource.Manual);
        }

        ImGui.SameLine();
        if (ImGui.Button("Open Vendor Buy Lists", new Vector2(170f, 0f)))
            GatherBuddy.VendorBuyListWindow?.Open();

        ImGui.SameLine();
        if (ImGui.Button("Open Vulcan", new Vector2(110f, 0f)))
            GatherBuddy.VulcanWindow?.RestoreWindow();
    }

    private static void DrawAutomationSettings(CollectableManager manager, CollectableConfig config)
    {
        var autoTurnIn = config.AutoTurnInCollectables;
        var previousHardFailReason = config.AutoTurnInHardFailReason;
        if (ImGui.Checkbox("Auto turn in collectables", ref autoTurnIn))
        {
            config.AutoTurnInCollectables = autoTurnIn;
            if (autoTurnIn)
            {
                config.AutoTurnInHardFailReason = string.Empty;
                if (!manager.IsRunning && string.Equals(manager.StatusText, previousHardFailReason, StringComparison.Ordinal))
                    manager.ClearStatus();
            }
            GatherBuddy.Config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Lets Auto-Gather and Vulcan queues run collectable turn-ins automatically.");

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
            ImGui.SetTooltip("Runs the selected buy list after turn-ins, or during scrip-cap recovery if space must be cleared.");

        var returnHome = HomeNavigationHelper.ShouldReturnHomeAfterCollectables();
        if (ImGui.Checkbox("Return home after Vulcan queue turn-ins", ref returnHome))
        {
            GatherBuddy.Config.AutoGatherConfig.GoHomeWhenIdle = returnHome;
            GatherBuddy.Config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("After a queue collectables interruption, return home before crafting resumes.");

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
            ImGui.SetNextItemWidth(130f);
            if (ImGui.DragInt("Inventory threshold", ref inventoryThreshold, 1f, 1, 140))
            {
                config.InventoryFullThreshold = Math.Clamp(inventoryThreshold, 1, 140);
                GatherBuddy.Config.Save();
            }
        }
        else
        {
            var collectableThreshold = config.CollectableInventoryThreshold;
            ImGui.SetNextItemWidth(130f);
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
                    continue;

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

    private static void DrawPurchaseSettings(
        CollectableConfig config,
        VendorBuyListManager manager,
        VendorBuyListDefinition? selectedGatheringList,
        VendorBuyListDefinition? selectedCraftingList)
    {
        ImGui.TextColored(ImGuiColors.ParsedGold, "Purchase Lists");

        var reserveScripAmount = config.ReserveScripAmount;
        ImGui.SetNextItemWidth(130f);
        if (ImGui.DragInt("Reserve scrips", ref reserveScripAmount, 1f, 0, 4000))
        {
            config.ReserveScripAmount = Math.Clamp(reserveScripAmount, 0, 4000);
            GatherBuddy.Config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Keeps at least this many of each scrip when collectables buy lists spend scrips.");

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
            ImGui.TextColored(ImGuiColors.DalamudGrey3, GetPurchaseListSummary(manager, selectedGatheringList));

        using (var disabled = ImRaii.Disabled(manager.ActiveList == null || manager.ActiveList.Id == config.GatheringPurchaseListId))
        {
            if (ImGui.Button("Use Active Vendor List for Gathering", new Vector2(250f, 0f)) && manager.ActiveList != null)
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
            ImGui.TextColored(ImGuiColors.DalamudGrey3, GetPurchaseListSummary(manager, selectedCraftingList));

        using var disabledCrafting = ImRaii.Disabled(manager.ActiveList == null || manager.ActiveList.Id == config.CraftingPurchaseListId);
        if (ImGui.Button("Use Active Vendor List for Crafting", new Vector2(250f, 0f)) && manager.ActiveList != null)
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
            return;

        if (ImGui.Selectable("No list selected", selectedListId == Guid.Empty))
        {
            setter(Guid.Empty);
            GatherBuddy.Config.Save();
        }

        foreach (var list in manager.Lists.OrderBy(list => list.CreatedAt))
        {
            var isSelected = list.Id == selectedListId;
            if (!ImGui.Selectable(list.Name, isSelected))
                continue;

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
            return;

        if (selectedGatheringList == null)
            DrawWrappedColoredText(ImGuiColors.DalamudYellow, "Select a gathering purchase list for Auto-Gather and gathering manual turn-ins.");

        if (selectedCraftingList == null)
            DrawWrappedColoredText(ImGuiColors.DalamudYellow, "Select a crafting purchase list for Vulcan and crafting manual turn-ins.");
    }

    private static void DrawWrappedColoredText(Vector4 color, string text)
    {
        ImGui.PushTextWrapPos();
        ImGui.TextColored(color, text);
        ImGui.PopTextWrapPos();
    }
}
