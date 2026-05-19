using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.GamePad;
using GatherBuddy.Crafting;
using GatherBuddy.Plugin;
using GatherBuddy.Vulcan.Vendors;
using Lumina.Excel.Sheets;
using ImRaii = ElliLib.Raii.ImRaii;

namespace GatherBuddy.Gui;

public partial class VulcanWindow
{
    private void DrawDebugTab()
    {
        IDisposable tabItem;
        bool tabOpen;
        
        if (GatherBuddy.ControllerSupport != null)
        {
        var handle = GatherBuddy.ControllerSupport.TabNavigation.TabItem("Debug##debugTab", 7, 9);
            tabItem = handle;
            tabOpen = handle;
        }
        else
        {
            var handle = ImRaii.TabItem("Debug##debugTab");
            tabItem = handle;
            tabOpen = handle.Success;
        }
        
        using (tabItem)
        {
            if (!tabOpen)
                return;

        ImGui.BeginGroup();
        ImGui.Text("Context Menu Settings");
        ImGui.Spacing();

        ImGui.Text("  Max Recent Lists:");
        ImGui.SameLine();
        var maxRecentLists = GatherBuddy.Config.MaxRecentCraftingListsInContextMenu;
        ImGui.SetNextItemWidth(100);
        if (ImGui.InputInt("###MaxRecentLists", ref maxRecentLists, 1, 1))
        {
            GatherBuddy.Config.MaxRecentCraftingListsInContextMenu = Math.Max(1, Math.Min(50, maxRecentLists));
            GatherBuddy.Config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Maximum number of recent crafting lists to show in context menus (1-50)");

        ImGui.EndGroup();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        DrawVendorNpcLocationDebug();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.BeginGroup();
        ImGui.Text("Repair Status");
        ImGui.Spacing();
        ImGui.Text($"  Min Equipped: {Crafting.RepairManager.GetMinEquippedPercent()}%");
        ImGui.Text($"  Can Self Repair: {Crafting.RepairManager.CanRepairAny()}");
        ImGui.Text($"  Repair NPC Nearby: {Crafting.RepairManager.RepairNPCNearby(out _)}");
        if (Crafting.RepairManager.RepairNPCNearby(out _))
        {
            ImGui.Text($"  NPC Repair Price: {Crafting.RepairManager.GetNPCRepairPrice()} gil");
        }
        ImGui.EndGroup();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.BeginGroup();
        ImGui.Text("Materia Extraction Status");
        ImGui.Spacing();
        ImGui.Text($"  Extraction Unlocked: {Crafting.MateriaManager.IsExtractionUnlocked()}");
        ImGui.Text($"  Items Ready: {Crafting.MateriaManager.ReadySpiritbondItemCount()}");
        ImGui.Text($"  Free Slots: {Crafting.MateriaManager.HasFreeInventorySlots()}");
        ImGui.EndGroup();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.BeginGroup();
        ImGui.Text("Gearset Stat Test");
        ImGui.Text("  Select Job:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(150);
        if (ImGui.BeginCombo("###JobSelector", GetDebugJobName(_debugSelectedJobId)))
        {
            var jobs = new[] { (8u, "Carpenter (CRP)"), (9u, "Blacksmith (BSM)"), (10u, "Armorer (ARM)"), (11u, "Goldsmith (GSM)"), (12u, "Leatherworker (LTW)"), (13u, "Weaver (WVR)"), (14u, "Alchemist (ALC)"), (15u, "Culinarian (CUL)") };
            foreach (var (jobId, jobName) in jobs)
            {
                if (ImGui.Selectable(jobName, _debugSelectedJobId == jobId))
                {
                    _debugSelectedJobId = jobId;
                    _debugLastTestResult = null;
                }
            }
            ImGui.EndCombo();
        }

        ImGui.Spacing();
        if (ImGui.Button("Test Stat Read", new Vector2(150, 0)))
        {
            var stats = GearsetStatsReader.ReadGearsetStatsForJob(_debugSelectedJobId);
            if (stats != null)
            {
                _debugLastTestResult = $"Success: Craftsmanship={stats.Craftsmanship}, Control={stats.Control}, CP={stats.CP}, Manipulation={stats.Manipulation}";
            }
            else
            {
                _debugLastTestResult = "Failed: Could not read gearset stats for this job";
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Refresh Gearset", new Vector2(150, 0)))
        {
            GearsetStatsReader.RefreshGearsetFromCurrentEquipped(_debugSelectedJobId);
            _debugLastTestResult = "Gearset refreshed from currently equipped items";
        }

        if (_debugLastTestResult != null)
        {
            ImGui.Spacing();
            ImGui.TextWrapped(_debugLastTestResult);
        }
        ImGui.EndGroup();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawGamepadInputTest();
        }
    }

    private static void DrawVendorNpcLocationDebug()
    {
        ImGui.BeginGroup();
        ImGui.Text("Vendor NPC Location Source");
        ImGui.Spacing();

        var dataShareFirst = GatherBuddy.Config.VendorNpcLocationsDataShareFirst;
        if (ImGui.RadioButton("DataShare first###vendorNpcLocationDataShareFirst", dataShareFirst))
            SetVendorNpcLocationSourcePreference(true);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Prefer AllaganTools DataShare locations, then fill gaps from the Level sheet, ENpcPlace supplemental data, and planevent.lgb.");

        ImGui.SameLine();

        if (ImGui.RadioButton("LGB first###vendorNpcLocationLgbFirst", !dataShareFirst))
            SetVendorNpcLocationSourcePreference(false);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Prefer planevent.lgb locations, then fill gaps from the Level sheet, ENpcPlace supplemental data, and AllaganTools DataShare.");

        ImGui.Spacing();
        ImGui.Text($"  Source Order: {(dataShareFirst ? "DataShare -> Level -> Supplemental -> LGB" : "LGB -> Level -> Supplemental -> DataShare")}");
        ImGui.Text($"  Cache Status: {GetVendorNpcLocationCacheStatus()}");
        ImGui.EndGroup();
    }
    
    private void DrawGamepadInputTest()
    {
        ImGui.BeginGroup();
        ImGui.Text("Gamepad Input Test");
        ImGui.Separator();
        ImGui.Spacing();
        
        var gamepad = Dalamud.GamepadState;
        
        ImGui.Text("Left Stick:");
        ImGui.SameLine();
        ImGui.Text($"X: {gamepad.LeftStick.X:F3}, Y: {gamepad.LeftStick.Y:F3}");
        
        ImGui.Text("Right Stick:");
        ImGui.SameLine();
        ImGui.Text($"X: {gamepad.RightStick.X:F3}, Y: {gamepad.RightStick.Y:F3}");
        
        ImGui.Spacing();
        ImGui.Text("D-Pad:");
        ImGui.SameLine();
        var dpad = "None";
        if (gamepad.Pressed(GamepadButtons.DpadUp) > 0) dpad = "Up";
        if (gamepad.Pressed(GamepadButtons.DpadDown) > 0) dpad = "Down";
        if (gamepad.Pressed(GamepadButtons.DpadLeft) > 0) dpad = "Left";
        if (gamepad.Pressed(GamepadButtons.DpadRight) > 0) dpad = "Right";
        ImGui.Text(dpad);
        
        ImGui.Spacing();
        ImGui.Text("Face Buttons:");
        var faceButtons = new List<string>();
        if (gamepad.Pressed(GamepadButtons.South) > 0) faceButtons.Add("A/Cross");
        if (gamepad.Pressed(GamepadButtons.East) > 0) faceButtons.Add("B/Circle");
        if (gamepad.Pressed(GamepadButtons.West) > 0) faceButtons.Add("X/Square");
        if (gamepad.Pressed(GamepadButtons.North) > 0) faceButtons.Add("Y/Triangle");
        ImGui.SameLine();
        ImGui.Text(faceButtons.Count > 0 ? string.Join(", ", faceButtons) : "None");
        
        ImGui.Spacing();
        ImGui.Text("Shoulder Buttons:");
        var shoulderButtons = new List<string>();
        if (gamepad.Pressed(GamepadButtons.L1) > 0) shoulderButtons.Add("L1");
        if (gamepad.Pressed(GamepadButtons.R1) > 0) shoulderButtons.Add("R1");
        if (gamepad.Pressed(GamepadButtons.L2) > 0) shoulderButtons.Add("L2");
        if (gamepad.Pressed(GamepadButtons.R2) > 0) shoulderButtons.Add("R2");
        ImGui.SameLine();
        ImGui.Text(shoulderButtons.Count > 0 ? string.Join(", ", shoulderButtons) : "None");
        
        ImGui.Spacing();
        ImGui.Text("ImGui Navigation State:");
        var io = ImGui.GetIO();
        ImGui.Text($"  NavActive: {io.NavActive}");
        ImGui.Text($"  NavVisible: {io.NavVisible}");
        ImGui.Text($"  ConfigFlags: {io.ConfigFlags}");
        
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        
        var navKeyboardEnabled = (io.ConfigFlags & ImGuiConfigFlags.NavEnableKeyboard) != 0;
        var navGamepadEnabled = (io.ConfigFlags & ImGuiConfigFlags.NavEnableGamepad) != 0;
        
        if (ImGui.Button(navGamepadEnabled ? "Disable Gamepad Nav" : "Enable Gamepad Nav", new Vector2(200, 0)))
        {
            io = ImGui.GetIO();
            if (navGamepadEnabled)
            {
                io.ConfigFlags &= ~ImGuiConfigFlags.NavEnableGamepad;
                GatherBuddy.Log.Information("[VulcanWindow] Disabled ImGui gamepad navigation");
            }
            else
            {
                io.ConfigFlags |= ImGuiConfigFlags.NavEnableGamepad;
                GatherBuddy.Log.Information("[VulcanWindow] Enabled ImGui gamepad navigation");
            }
        }
        
        ImGui.SameLine();
        if (ImGui.Button(navKeyboardEnabled ? "Disable Keyboard Nav" : "Enable Keyboard Nav", new Vector2(200, 0)))
        {
            io = ImGui.GetIO();
            if (navKeyboardEnabled)
            {
                io.ConfigFlags &= ~ImGuiConfigFlags.NavEnableKeyboard;
                GatherBuddy.Log.Information("[VulcanWindow] Disabled ImGui keyboard navigation");
            }
            else
            {
                io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;
                GatherBuddy.Log.Information("[VulcanWindow] Enabled ImGui keyboard navigation");
            }
        }
        
        ImGui.TextColored(new Vector4(1, 1, 0, 1), "Note: Press Tab or use D-pad to start navigating");
        
        ImGui.EndGroup();
    }


    private static string GetDebugJobName(uint jobId) => jobId switch
    {
        8 => "Carpenter (CRP)",
        9 => "Blacksmith (BSM)",
        10 => "Armorer (ARM)",
        11 => "Goldsmith (GSM)",
        12 => "Leatherworker (LTW)",
        13 => "Weaver (WVR)",
        14 => "Alchemist (ALC)",
        15 => "Culinarian (CUL)",
        _ => "Unknown"
    };
    
    private static string GetTerritoryName(uint territoryId)
    {
        var territorySheet = Dalamud.GameData.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>();
        if (territorySheet?.TryGetRow(territoryId, out var territory) == true)
        {
            return territory.PlaceName.ValueNullable?.Name.ExtractText() ?? "Unknown";
        }
        return "Unknown";
    }

    private static void SetVendorNpcLocationSourcePreference(bool dataShareFirst)
    {
        if (GatherBuddy.Config.VendorNpcLocationsDataShareFirst == dataShareFirst)
            return;

        GatherBuddy.Config.VendorNpcLocationsDataShareFirst = dataShareFirst;
        GatherBuddy.Config.Save();
        GatherBuddy.Log.Debug($"[VulcanWindow] Vendor NPC location source order set to {(dataShareFirst ? "DataShare -> Level -> Supplemental -> LGB" : "LGB -> Level -> Supplemental -> DataShare")}");
        VendorNpcLocationCache.ReloadAsync();
    }

    private static string GetVendorNpcLocationCacheStatus()
    {
        if (VendorNpcLocationCache.IsInitializing)
            return $"Rebuilding ({VendorNpcLocationCache.ResolvedNpcCount}/{VendorNpcLocationCache.RequestedNpcCount})";
        if (VendorNpcLocationCache.IsInitialized)
            return $"Ready ({VendorNpcLocationCache.ResolvedNpcCount}/{VendorNpcLocationCache.RequestedNpcCount})";
        if (VendorShopResolver.IsInitializing)
            return "Waiting for vendor data";
        if (!VendorShopResolver.IsInitialized)
            return "Vendor data not initialized";
        return "Location cache not initialized";
    }
}
