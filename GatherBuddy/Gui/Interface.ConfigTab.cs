using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text;
using Dalamud.Interface.Utility;

using FFXIVClientStructs.STD;
using GatherBuddy.Alarms;
using GatherBuddy.AutoGather;
using GatherBuddy.AutoGather.Collectables;
using GatherBuddy.AutoGather.Collectables.Data;
using GatherBuddy.Classes;
using GatherBuddy.Config;
using GatherBuddy.Enums;
using GatherBuddy.FishTimer;
using GatherBuddy.Utilities;
using Dalamud.Utility;
using ElliLib;
using ElliLib.Widgets;
using FishRecord = GatherBuddy.FishTimer.FishRecord;
using GatheringType = GatherBuddy.Enums.GatheringType;
using ImRaii = ElliLib.Raii.ImRaii;

namespace GatherBuddy.Gui;

public partial class Interface
{
    private static class ConfigFunctions
    {
        public static Interface _base = null!;
        
        private static string _fishFilterText = "";
        private static Fish? _selectedFish = null;
        private static string _presetName = "";
        private static string _scripShopFilterText = "";

        public static void DrawSetInput(string jobName, string oldName, Action<string> setName)
        {
            var tmp = oldName;
            ImGui.SetNextItemWidth(SetInputWidth);
            if (ImGui.InputText($"{jobName} Set", ref tmp, 15) && tmp != oldName)
            {
                setName(tmp);
                GatherBuddy.Config.Save();
            }

            ImGuiUtil.HoverTooltip($"Set the name of your {jobName.ToLowerInvariant()} set. Can also be the numerical id instead.");
        }

        private static void DrawCheckbox(string label, string description, bool oldValue, Action<bool> setter)
        {
            if (ImGuiUtil.Checkbox(label, description, oldValue, setter))
                GatherBuddy.Config.Save();
        }

        private static void DrawChatTypeSelector(string label, string description, XivChatType currentValue, Action<XivChatType> setter)
        {
            ImGui.SetNextItemWidth(SetInputWidth);
            if (Widget.DrawChatTypeSelector(label, description, currentValue, setter))
                GatherBuddy.Config.Save();
        }

        // Auto-Gather Config
        public static void DrawAutoGatherBox()
            => DrawCheckbox("Enable Gathering Window Interaction (DISABLING THIS IS UNSUPPORTED)",
                "Toggle whether to automatically gather items. (Disable this for 'nav only mode')",
                GatherBuddy.Config.AutoGatherConfig.DoGathering, b => GatherBuddy.Config.AutoGatherConfig.DoGathering = b);

        public static void DrawGoHomeBox()
        {
            DrawCheckbox("Go home when done",                       "Uses the '/li auto' command to take you home when done gathering",
                GatherBuddy.Config.AutoGatherConfig.GoHomeWhenDone, b => GatherBuddy.Config.AutoGatherConfig.GoHomeWhenDone = b);
            ImGui.SameLine();
            ImGuiEx.PluginAvailabilityIndicator([new("Lifestream")]);
            DrawCheckbox("Go home when idle",                       "Uses the '/li auto' command to take you home when waiting for timed nodes",
                GatherBuddy.Config.AutoGatherConfig.GoHomeWhenIdle, b => GatherBuddy.Config.AutoGatherConfig.GoHomeWhenIdle = b);
            ImGui.SameLine();
            ImGuiEx.PluginAvailabilityIndicator([new("Lifestream")]);
        }

        public static void DrawUseSkillsForFallabckBox()
            => DrawCheckbox("Use skills for fallback items", "Use skills when gathering items from fallback presets",
                GatherBuddy.Config.AutoGatherConfig.UseSkillsForFallbackItems,
                b => GatherBuddy.Config.AutoGatherConfig.UseSkillsForFallbackItems = b);

        public static void DrawAbandonNodesBox()
            => DrawCheckbox("Abandon nodes without needed items",
                "Stop gathering and abandon the node when you have gathered enough items,\n"
              + "or if the node didn't have any needed items on the first place.",
                GatherBuddy.Config.AutoGatherConfig.AbandonNodes, b => GatherBuddy.Config.AutoGatherConfig.AbandonNodes = b);

        public static void DrawCheckRetainersBox()
        {
            DrawCheckbox("Check Retainer Inventories", "Use Allagan Tools to check retainer inventories when doing inventory calculations",
                GatherBuddy.Config.AutoGatherConfig.CheckRetainers, b => GatherBuddy.Config.AutoGatherConfig.CheckRetainers = b);
            ImGui.SameLine();
            ImGuiEx.PluginAvailabilityIndicator([new("InventoryTools", "Allagan Tools")]);
        }

        public static void DrawHonkVolumeSlider()
        {
            ImGui.SetNextItemWidth(150);
            var volume = GatherBuddy.Config.AutoGatherConfig.SoundPlaybackVolume;
            if (ImGui.DragInt("Playback Volume", ref volume, 1, 0, 100))
            {
                if (volume < 0)
                    volume = 0;
                else if (volume > 100)
                    volume = 100;
                GatherBuddy.Config.AutoGatherConfig.SoundPlaybackVolume = volume;
                GatherBuddy.Config.Save();
            }

            ImGuiUtil.HoverTooltip(
                "The volume of the sound played when auto-gathering shuts down because your list is complete.\nHold CTRL and click to enter custom value");
        }

        public static void DrawHonkModeBox()
            => DrawCheckbox("Play a sound when done gathering", "Play a sound when auto-gathering shuts down because your list is complete",
                GatherBuddy.Config.AutoGatherConfig.HonkMode,   b => GatherBuddy.Config.AutoGatherConfig.HonkMode = b);

        public static void DrawRepairBox()
            => DrawCheckbox("Repair gear when needed",        "Repair gear when it is almost broken",
                GatherBuddy.Config.AutoGatherConfig.DoRepair, b => GatherBuddy.Config.AutoGatherConfig.DoRepair = b);

        public static void DrawRepairThreshold()
        {
            ImGui.SetNextItemWidth(150);
            var tmp = GatherBuddy.Config.AutoGatherConfig.RepairThreshold;
            if (ImGui.DragInt("Repair Threshold", ref tmp, 1, 1, 100))
            {
                GatherBuddy.Config.AutoGatherConfig.RepairThreshold = tmp;
                GatherBuddy.Config.Save();
            }

            ImGuiUtil.HoverTooltip("The percentage of durability at which you will repair your gear.");
        }

        public static void DrawFishingSpotMinutes()
        {
            ImGui.SetNextItemWidth(150);
            var tmp = GatherBuddy.Config.AutoGatherConfig.MaxFishingSpotMinutes;
            if (ImGui.DragInt("Max Fishing Spot Minutes", ref tmp, 1, 1, 40))
            {
                GatherBuddy.Config.AutoGatherConfig.MaxFishingSpotMinutes = tmp;
                GatherBuddy.Config.Save();
            }

            ImGuiUtil.HoverTooltip("The maximum number of minutes you will fish at a fishing spot.");
        }

        public static void DrawAutoretainerBox()
        {
            DrawCheckbox("Wait for AutoRetainer Multi-mode", "Pause GBR automatically when AutoRetainer has retainers to process during Multi-mode",
                GatherBuddy.Config.AutoGatherConfig.AutoRetainerMultiMode, b => GatherBuddy.Config.AutoGatherConfig.AutoRetainerMultiMode = b);
            ImGui.SameLine();
            ImGuiEx.PluginAvailabilityIndicator([new ImGuiEx.RequiredPluginInfo("AutoRetainer")]);
        }

        public static void DrawAutoretainerThreshold()
        {
            ImGui.SetNextItemWidth(150);
            var tmp = GatherBuddy.Config.AutoGatherConfig.AutoRetainerMultiModeThreshold;
            if (ImGui.DragInt("AutoRetainer Threshold (Seconds)", ref tmp, 1, 0, 3600))
            {
                GatherBuddy.Config.AutoGatherConfig.AutoRetainerMultiModeThreshold = tmp;
                GatherBuddy.Config.Save();
            }

            ImGuiUtil.HoverTooltip("How many seconds before a retainer venture completes GBR should pause and wait for MultiMode.");
        }

        public static void DrawAutoretainerTimedNodeDelayBox()
            => DrawCheckbox("Delay AutoRetainer for timed nodes",
                "Wait to process retainers until after active/upcoming timed nodes are gathered.",
                GatherBuddy.Config.AutoGatherConfig.AutoRetainerDelayForTimedNodes,
                b => GatherBuddy.Config.AutoGatherConfig.AutoRetainerDelayForTimedNodes = b);

        public static void DrawLifestreamCommandTextInput()
        {
            ImGui.SetNextItemWidth(150);
            var tmp = GatherBuddy.Config.AutoGatherConfig.LifestreamCommand;
            if (ImGui.InputText("Lifestream Command", ref tmp, 100))
            {
                if (string.IsNullOrEmpty(tmp))
                    tmp = "auto";
                GatherBuddy.Config.AutoGatherConfig.LifestreamCommand = tmp;
                GatherBuddy.Config.Save();
            }

            ImGuiUtil.HoverTooltip(
                "The command used when idling or done gathering. DO NOT include '/li'\nBe careful when changing this, GBR does not validate this command!");
        }

        public static void DrawFishCollectionBox()
            => DrawCheckbox("Opt-in to fishing data collection",
                "With this enabled, whenever you catch a fish the data for that fish will be uploaded to a remote server\n"
              + "The purpose of this data collection is to allow for a usable auto-fishing feature to be built\n"
              + "No personal information about you or your character will be collected, only data relevant to the caught fish\n"
              + "You can opt-out again at any time by simply disabling this checkbox.", GatherBuddy.Config.AutoGatherConfig.FishDataCollection,
                b => GatherBuddy.Config.AutoGatherConfig.FishDataCollection = b);

        public static void DrawMaterialExtraction()
            => DrawCheckbox("Enable materia extraction",
                "Automatically extract materia from items with a complete spiritbond",
                GatherBuddy.Config.AutoGatherConfig.DoMaterialize,
                b => GatherBuddy.Config.AutoGatherConfig.DoMaterialize = b);

        public static void DrawAetherialReduction()
            => DrawCheckbox("Enable Aetherial Reduction",
                "Automatically perform Aetherial Reduction when idling or the inventory is full",
                GatherBuddy.Config.AutoGatherConfig.DoReduce,
                b => GatherBuddy.Config.AutoGatherConfig.DoReduce = b);

        public static void DrawUseFlagBox()
            => DrawCheckbox("Disable map marker navigation",            "Whether or not to navigate using map markers (timed nodes only)",
                GatherBuddy.Config.AutoGatherConfig.DisableFlagPathing, b => GatherBuddy.Config.AutoGatherConfig.DisableFlagPathing = b);

        public static void DrawFarNodeFilterDistance()
        {
            ImGui.SetNextItemWidth(150);
            var tmp = GatherBuddy.Config.AutoGatherConfig.FarNodeFilterDistance;
            if (ImGui.DragFloat("Far Node Filter Distance", ref tmp, 0.1f, 0.1f, 100f))
            {
                GatherBuddy.Config.AutoGatherConfig.FarNodeFilterDistance = tmp;
                GatherBuddy.Config.Save();
            }

            ImGuiUtil.HoverTooltip(
                "When looking for non-empty nodes GBR will filter out any nodes that are closer to you than this. Prevents checking nodes you can already see are empty.");
        }

        public static void DrawTimedNodePrecog()
        {
            ImGui.SetNextItemWidth(150);
            var tmp = GatherBuddy.Config.AutoGatherConfig.TimedNodePrecog;
            if (ImGui.DragInt("Timed Node Precognition (Seconds)", ref tmp, 1, 0, 600))
            {
                GatherBuddy.Config.AutoGatherConfig.TimedNodePrecog = tmp;
                GatherBuddy.Config.Save();
            }

            ImGuiUtil.HoverTooltip("How far in advance of the node actually being up GBR should consider the node to be up");
        }

        public static void DrawExecutionDelay()
        {
            ImGui.SetNextItemWidth(150);
            var tmp = (int)GatherBuddy.Config.AutoGatherConfig.ExecutionDelay;
            if (ImGui.DragInt("Execution delay (Milliseconds)", ref tmp, 1, 0, 1500))
            {
                GatherBuddy.Config.AutoGatherConfig.ExecutionDelay = (uint)Math.Min(Math.Max(0, tmp), 10000);
                GatherBuddy.Config.Save();
            }

            ImGuiUtil.HoverTooltip("Delay executing each action by the specified amount.");
        }

        public static void DrawUseGivingLandOnCooldown()
            => DrawCheckbox("Gather any crystals when The Giving Land is off cooldown",
                "Gather random crystals on any regular node when The Giving Land is avaiable regardles of current target item.",
                GatherBuddy.Config.AutoGatherConfig.UseGivingLandOnCooldown,
                b => GatherBuddy.Config.AutoGatherConfig.UseGivingLandOnCooldown = b);

        public static void DrawMountUpDistance()
        {
            ImGui.SetNextItemWidth(150);
            var tmp = GatherBuddy.Config.AutoGatherConfig.MountUpDistance;
            if (ImGui.DragFloat("Mount Up Distance", ref tmp, 0.1f, 0.1f, 100f))
            {
                GatherBuddy.Config.AutoGatherConfig.MountUpDistance = tmp;
                GatherBuddy.Config.Save();
            }

            ImGuiUtil.HoverTooltip("The distance at which you will mount up to move to a node.");
        }

        public static void DrawLandingDistance()
        {
            ImGui.SetNextItemWidth(150);
            var tmp = GatherBuddy.Config.AutoGatherConfig.LandingDistance;
            if (ImGui.DragFloat("Landing Distance", ref tmp, 0.1f, 0.0f, 50f))
            {
                GatherBuddy.Config.AutoGatherConfig.LandingDistance = tmp;
                GatherBuddy.Config.Save();
            }

            ImGuiUtil.HoverTooltip(
                "The distance at which you will try to land.\n\n" +
                "Low values increase the chance of being unable to dismount properly.\n" +
                "High values may produce weird-looking paths.\n" +
                "Reasonable values are between 5 and 10 yalms."
            );
        }

        public static void DrawMoveWhileMounting()
            => DrawCheckbox("Move while mounting up",
                "Begin pathfinding to the next node while summoning a mount",
                GatherBuddy.Config.AutoGatherConfig.MoveWhileMounting,
                b => GatherBuddy.Config.AutoGatherConfig.MoveWhileMounting = b);

        public static void DrawAntiStuckCooldown()
        {
            ImGui.SetNextItemWidth(150);
            var tmp = GatherBuddy.Config.AutoGatherConfig.NavResetCooldown;
            if (ImGui.DragFloat("Anti-Stuck Cooldown", ref tmp, 0.1f, 0.1f, 10f))
            {
                GatherBuddy.Config.AutoGatherConfig.NavResetCooldown = tmp;
                GatherBuddy.Config.Save();
            }

            ImGuiUtil.HoverTooltip("The time in seconds before the navigation system will reset if you are stuck.");
        }

        public static void DrawForceWalkingBox()
            => DrawCheckbox("Force Walking",                      "Force walking to nodes instead of using mounts.",
                GatherBuddy.Config.AutoGatherConfig.ForceWalking, b => GatherBuddy.Config.AutoGatherConfig.ForceWalking = b);

        public static void DrawUseNavigationBox()
            => DrawCheckbox("Use vnavmesh Navigation",             "Use vnavmesh Navigation to move your character automatically",
                GatherBuddy.Config.AutoGatherConfig.UseNavigation, b => GatherBuddy.Config.AutoGatherConfig.UseNavigation = b);

        public static void DrawStuckThreshold()
        {
            ImGui.SetNextItemWidth(150);
            var tmp = GatherBuddy.Config.AutoGatherConfig.NavResetThreshold;
            if (ImGui.DragFloat("Stuck Threshold", ref tmp, 0.1f, 0.1f, 10f))
            {
                GatherBuddy.Config.AutoGatherConfig.NavResetThreshold = tmp;
                GatherBuddy.Config.Save();
            }

            ImGuiUtil.HoverTooltip("The time in seconds before the navigation system will consider you stuck.");
        }

        public static void DrawSortingMethodCombo()
        {
            var v = GatherBuddy.Config.AutoGatherConfig.SortingMethod;
            ImGui.SetNextItemWidth(150);

            using var combo = ImRaii.Combo("Item Sorting Method", v.ToString());
            ImGuiUtil.HoverTooltip("What method to use when sorting items internally");
            if (!combo)
                return;

            if (ImGui.Selectable(AutoGatherConfig.SortingType.Location.ToString(), v == AutoGatherConfig.SortingType.Location))
            {
                GatherBuddy.Config.AutoGatherConfig.SortingMethod = AutoGatherConfig.SortingType.Location;
                GatherBuddy.Config.Save();
            }

            if (ImGui.Selectable(AutoGatherConfig.SortingType.None.ToString(), v == AutoGatherConfig.SortingType.None))
            {
                GatherBuddy.Config.AutoGatherConfig.SortingMethod = AutoGatherConfig.SortingType.None;
                GatherBuddy.Config.Save();
            }
        }

        // General Config
        public static void DrawOpenOnStartBox()
            => DrawCheckbox("Open Config UI On Start",
                "Toggle whether the GatherBuddy GUI should be visible after you start the game.",
                GatherBuddy.Config.OpenOnStart, b => GatherBuddy.Config.OpenOnStart = b);

        public static void DrawLockPositionBox()
            => DrawCheckbox("Lock Config UI Movement",
                "Toggle whether the GatherBuddy GUI movement should be locked.",
                GatherBuddy.Config.MainWindowLockPosition, b =>
                {
                    GatherBuddy.Config.MainWindowLockPosition = b;
                    _base.UpdateFlags();
                });

        public static void DrawLockResizeBox()
            => DrawCheckbox("Lock Config UI Size",
                "Toggle whether the GatherBuddy GUI size should be locked.",
                GatherBuddy.Config.MainWindowLockResize, b =>
                {
                    GatherBuddy.Config.MainWindowLockResize = b;
                    _base.UpdateFlags();
                });

        public static void DrawRespectEscapeBox()
            => DrawCheckbox("Escape Closes Main Window",
                "Toggle whether pressing escape while having the main window focused shall close it.",
                GatherBuddy.Config.CloseOnEscape, b =>
                {
                    GatherBuddy.Config.CloseOnEscape = b;
                    _base.UpdateFlags();
                });

        public static void DrawGearChangeBox()
            => DrawCheckbox("Enable Gear Change",
                "Toggle whether to automatically switch gear to the correct job gear for a node.\nUses Miner Set, Botanist Set and Fisher Set.",
                GatherBuddy.Config.UseGearChange, b => GatherBuddy.Config.UseGearChange = b);

        public static void DrawTeleportBox()
            => DrawCheckbox("Enable Teleport",
                "Toggle whether to automatically teleport to a chosen node.",
                GatherBuddy.Config.UseTeleport, b => GatherBuddy.Config.UseTeleport = b);

        public static void DrawMapOpenBox()
            => DrawCheckbox("Open Map With Location",
                "Toggle whether to automatically open the map of the territory of the chosen node with its gathering location highlighted.",
                GatherBuddy.Config.UseCoordinates, b => GatherBuddy.Config.UseCoordinates = b);

        public static void DrawPlaceMarkerBox()
            => DrawCheckbox("Place Flag Marker on Map",
                "Toggle whether to automatically set a red flag marker on the approximate location of the chosen node without opening the map.",
                GatherBuddy.Config.UseFlag, b => GatherBuddy.Config.UseFlag = b);

        public static void DrawMapMarkerPrintBox()
            => DrawCheckbox("Print Map Location",
                "Toggle whether to automatically write a map link to the approximate location of the chosen node to chat.",
                GatherBuddy.Config.WriteCoordinates, b => GatherBuddy.Config.WriteCoordinates = b);

        public static void DrawPlaceWaymarkBox()
            => DrawCheckbox("Place Custom Waymarks",
                "Toggle whether to place custom Waymarks you set manually set up for certain locations.",
                GatherBuddy.Config.PlaceCustomWaymarks, b => GatherBuddy.Config.PlaceCustomWaymarks = b);

        public static void DrawPrintUptimesBox()
            => DrawCheckbox("Print Node Uptimes On Gather",
                "Print the uptimes of nodes you try to /gather in the chat if they are not always up.",
                GatherBuddy.Config.PrintUptime, b => GatherBuddy.Config.PrintUptime = b);

        public static void DrawSkipTeleportBox()
            => DrawCheckbox("Skip Nearby Teleports",
                "Skips teleports if you are in the same map and closer to the target than the selected aetheryte already.",
                GatherBuddy.Config.SkipTeleportIfClose, b => GatherBuddy.Config.SkipTeleportIfClose = b);

        public static void DrawShowStatusLineBox()
            => DrawCheckbox("Show Status Line",
                "Show a status line below the gatherables and fish tables.",
                GatherBuddy.Config.ShowStatusLine, v => GatherBuddy.Config.ShowStatusLine = v);

        public static void DrawHideClippyBox()
            => DrawCheckbox("Hide GatherClippy Button",
                "Permanently hide the GatherClippy Button in the Gatherables and Fish tabs.",
                GatherBuddy.Config.HideClippy, v => GatherBuddy.Config.HideClippy = v);

        private const string ChatInformationString =
            "Note that the message only gets printed to your chat log, regardless of the selected channel"
          + " - other people will not see your 'Say' message.";

        public static void DrawPrintTypeSelector()
            => DrawChatTypeSelector("Chat Type for Messages",
                "The chat type used to print regular messages issued by GatherBuddy.\n"
              + ChatInformationString,
                GatherBuddy.Config.ChatTypeMessage, t => GatherBuddy.Config.ChatTypeMessage = t);

        public static void DrawErrorTypeSelector()
            => DrawChatTypeSelector("Chat Type for Errors",
                "The chat type used to print error messages issued by GatherBuddy.\n"
              + ChatInformationString,
                GatherBuddy.Config.ChatTypeError, t => GatherBuddy.Config.ChatTypeError = t);

        public static void DrawContextMenuBox()
            => DrawCheckbox("Add In-Game Context Menus",
                "Add a 'Gather' entry to in-game right-click context menus for gatherable items.",
                GatherBuddy.Config.AddIngameContextMenus, b =>
                {
                    GatherBuddy.Config.AddIngameContextMenus = b;
                    if (b)
                        _plugin.ContextMenu.Enable();
                    else
                        _plugin.ContextMenu.Disable();
                });

        public static void DrawPreferredJobSelect()
        {
            var v       = GatherBuddy.Config.PreferredGatheringType;
            var current = v == GatheringType.Multiple ? "No Preference" : v.ToString();
            ImGui.SetNextItemWidth(SetInputWidth);
            using var combo = ImRaii.Combo("Preferred Job", current);
            ImGuiUtil.HoverTooltip(
                "Choose your job preference when gathering items that can be gathered by miners as well as botanists.\n"
              + "This effectively turns the regular gather command to /gathermin or /gatherbtn when an item can be gathered by both, "
              + "ignoring the other options even on successive tries.");
            if (!combo)
                return;

            if (ImGui.Selectable("No Preference", v == GatheringType.Multiple) && v != GatheringType.Multiple)
            {
                GatherBuddy.Config.PreferredGatheringType = GatheringType.Multiple;
                GatherBuddy.Config.Save();
            }

            if (ImGui.Selectable(GatheringType.Miner.ToString(), v == GatheringType.Miner) && v != GatheringType.Miner)
            {
                GatherBuddy.Config.PreferredGatheringType = GatheringType.Miner;
                GatherBuddy.Config.Save();
            }

            if (ImGui.Selectable(GatheringType.Botanist.ToString(), v == GatheringType.Botanist) && v != GatheringType.Botanist)
            {
                GatherBuddy.Config.PreferredGatheringType = GatheringType.Botanist;
                GatherBuddy.Config.Save();
            }
        }

        public static void DrawPrintClipboardBox()
            => DrawCheckbox("Print Clipboard Information",
                "Print to the chat whenever you save an object to the clipboard. Failures will be printed regardless.",
                GatherBuddy.Config.PrintClipboardMessages, b => GatherBuddy.Config.PrintClipboardMessages = b);

        // Weather Tab
        public static void DrawWeatherTabNamesBox()
            => DrawCheckbox("Show Names in Weather Tab",
                "Toggle whether to write the names in the table for the weather tab, or just the icons with names on hover.",
                GatherBuddy.Config.ShowWeatherNames, b => GatherBuddy.Config.ShowWeatherNames = b);

        // Alarms
        public static void DrawAlarmToggle()
            => DrawCheckbox("Enable Alarms", "Toggle all alarms on or off.", GatherBuddy.Config.AlarmsEnabled,
                b =>
                {
                    if (b)
                        _plugin.AlarmManager.Enable();
                    else
                        _plugin.AlarmManager.Disable();
                });

        private static bool _gatherDebug = false;

        public static void DrawAlarmsInDutyToggle()
            => DrawCheckbox("Enable Alarms in Duty", "Set whether alarms should trigger while you are bound by a duty.",
                GatherBuddy.Config.AlarmsInDuty,     b => GatherBuddy.Config.AlarmsInDuty = b);

        public static void DrawAlarmsOnlyWhenLoggedInToggle()
            => DrawCheckbox("Enable Alarms Only In-Game",  "Set whether alarms should trigger while you are not logged into any character.",
                GatherBuddy.Config.AlarmsOnlyWhenLoggedIn, b => GatherBuddy.Config.AlarmsOnlyWhenLoggedIn = b);

        private static void DrawAlarmPicker(string label, string description, Sounds current, Action<Sounds> setter)
        {
            var cur = (int)current;
            ImGui.SetNextItemWidth(90 * ImGuiHelpers.GlobalScale);
            if (ImGui.Combo(new ImU8String(label), ref cur, AlarmCache.SoundIdNames))
                setter((Sounds)cur);
            ImGuiUtil.HoverTooltip(description);
        }

        public static void DrawWeatherAlarmPicker()
            => DrawAlarmPicker("Weather Change Alarm", "Choose a sound that is played every 8 Eorzea hours on regular weather changes.",
                GatherBuddy.Config.WeatherAlarm,       _plugin.AlarmManager.SetWeatherAlarm);

        public static void DrawHourAlarmPicker()
            => DrawAlarmPicker("Eorzea Hour Change Alarm", "Choose a sound that is played every time the current Eorzea hour changes.",
                GatherBuddy.Config.HourAlarm,              _plugin.AlarmManager.SetHourAlarm);

        // Fish Timer
        public static void DrawFishTimerBox()
            => DrawCheckbox("Show Fish Timer",
                "Toggle whether to show the fish timer window while fishing.",
                GatherBuddy.Config.ShowFishTimer, b => GatherBuddy.Config.ShowFishTimer = b);

        public static void DrawFishTimerEditBox()
            => DrawCheckbox("Edit Fish Timer",
                "Enable editing the fish timer window.",
                GatherBuddy.Config.FishTimerEdit, b => GatherBuddy.Config.FishTimerEdit = b);

        public static void DrawFishTimerClickthroughBox()
            => DrawCheckbox("Enable Fish Timer Clickthrough",
                "Allow clicking through the fish timer and disabling the context menus instead.",
                GatherBuddy.Config.FishTimerClickthrough, b => GatherBuddy.Config.FishTimerClickthrough = b);

        public static void DrawFishTimerHideBox()
            => DrawCheckbox("Hide Uncaught Fish in Fish Timer",
                "Hide all fish from the fish timer window that have not been recorded with the given combination of snagging and bait.",
                GatherBuddy.Config.HideUncaughtFish, b => GatherBuddy.Config.HideUncaughtFish = b);

        public static void DrawFishTimerHideBox2()
            => DrawCheckbox("Hide Unavailable Fish in Fish Timer",
                "Hide all fish from the fish timer window that have have known requirements that are unfulfilled, like Fisher's Intuition or Snagging.",
                GatherBuddy.Config.HideUnavailableFish, b => GatherBuddy.Config.HideUnavailableFish = b);

        public static void DrawFishTimerUptimesBox()
            => DrawCheckbox("Show Uptimes in Fish Timer",
                "Show the uptimes for restricted fish in the fish timer window.",
                GatherBuddy.Config.ShowFishTimerUptimes, b => GatherBuddy.Config.ShowFishTimerUptimes = b);

        public static void DrawKeepRecordsBox()
            => DrawCheckbox("Keep Fish Records",
                "Store Fish Records on your computer. This is necessary for bite timings for the fish timer window.",
                GatherBuddy.Config.StoreFishRecords, b => GatherBuddy.Config.StoreFishRecords = b);

        public static void DrawShowLocalTimeInRecordsBox()
            => DrawCheckbox("Use Local Time in Records",
                "When displaying timestamps in the Fish Records Tab, use local time instead of Unix time.",
                GatherBuddy.Config.UseUnixTimeFishRecords, b => GatherBuddy.Config.UseUnixTimeFishRecords = b);
        
        public static void DrawFishTimerScale()
        {
            var value = GatherBuddy.Config.FishTimerScale / 1000f;
            ImGui.SetNextItemWidth(SetInputWidth);
            var ret = ImGui.DragFloat("Fish Timer Bite Time Scale", ref value, 0.1f, FishRecord.MinBiteTime / 500f,
                FishRecord.MaxBiteTime / 1000f,
                "%2.3f Seconds");

            ImGuiUtil.HoverTooltip("The fishing timer window bite times are scaled to this value.\n"
              + "If your bite time exceeds the value, the progress bar and bite windows will not be displayed.\n"
              + "You should probably keep this as high as your highest bite window and as low as possible. About 40 seconds is usually enough.");

            if (!ret)
                return;

            var newValue = (ushort)Math.Clamp((int)(value * 1000f + 0.9), FishRecord.MinBiteTime * 2, FishRecord.MaxBiteTime);
            if (newValue == GatherBuddy.Config.FishTimerScale)
                return;

            GatherBuddy.Config.FishTimerScale = newValue;
            GatherBuddy.Config.Save();
        }

        public static void DrawFishTimerIntervals()
        {
            int value = GatherBuddy.Config.ShowSecondIntervals;
            ImGui.SetNextItemWidth(SetInputWidth);
            var ret = ImGui.DragInt("Fish Timer Interval Separators", ref value, 0.01f, 0, 16);
            ImGuiUtil.HoverTooltip("The fishing timer window can show a number of interval lines and corresponding seconds between 0 and 16.\n"
              + "Set to 0 to turn this feature off.");
            if (!ret)
                return;

            var newValue = (byte)Math.Clamp(value, 0, 16);
            if (newValue == GatherBuddy.Config.ShowSecondIntervals)
                return;

            GatherBuddy.Config.ShowSecondIntervals = newValue;
            GatherBuddy.Config.Save();
        }
        
        public static void DrawFishTimerIntervalsRounding()
        {
            var value = GatherBuddy.Config.SecondIntervalsRounding;
            ImGui.SetNextItemWidth(SetInputWidth);
            var ret = ImGui.DragInt("Fish Timer Interval Rounding", ref value, 0.01f, 0, 3);
            ImGuiUtil.HoverTooltip("Round the displayed second value to this number of digits past the decimal. \n"
                + "Set to 0 to display only whole numbers.");
            if (!ret)
                return;

            var newValue = (byte)Math.Clamp(value, 0, 3);
            if (newValue == GatherBuddy.Config.SecondIntervalsRounding)
                return;

            GatherBuddy.Config.SecondIntervalsRounding = newValue;
            GatherBuddy.Config.Save();
        }

        public static void DrawHideFishPopupBox()
            => DrawCheckbox("Hide Catch Popup",
                "Prevents the popup window that shows you your caught fish and its size, amount and quality from being shown.",
                GatherBuddy.Config.HideFishSizePopup, b => GatherBuddy.Config.HideFishSizePopup = b);

        public static void DrawCollectableHintPopupBox()
            => DrawCheckbox("Show Collectable Hints",
                "Show if a fish is collectable in the fish timer window.",
                GatherBuddy.Config.ShowCollectableHints, b => GatherBuddy.Config.ShowCollectableHints = b);

        public static void DrawDoubleHookHintPopupBox()
            => DrawCheckbox("Show Multi Hook Hints",
                "Show if a fish can be double or triple hooked in Cosmic Exploration.", // TODO: add ocean fishing when implemented.
                GatherBuddy.Config.ShowMultiHookHints, b => GatherBuddy.Config.ShowMultiHookHints = b);
        
        
        // Fish Stats Window
        public static void DrawEnableFishStats()
            => DrawCheckbox("Enable Fish Stats",
                "New tab for aggregating and reporting fish stats based on local records. Currently in testing.",
                GatherBuddy.Config.EnableFishStats, b => GatherBuddy.Config.EnableFishStats = b);
        public static void DrawEnableReportTime()  
            => DrawCheckbox("Copy Time Stats when reporting.",
                "When copying the report, add min and max times to the report.",
                GatherBuddy.Config.EnableReportTime, b => GatherBuddy.Config.EnableReportTime = b);
        public static void DrawEnableReportSize()  
            => DrawCheckbox("Copy Sizes Stats when reporting.",
                "When copying the report, add min and max sizes to the report.",
                GatherBuddy.Config.EnableReportSize, b => GatherBuddy.Config.EnableReportSize = b);
        public static void DrawEnableReportMulti() 
            => DrawCheckbox("Copy Multi Hook Stats when reporting.",
                "When copying the report, add stats about multi-hook yields to the report.",
                GatherBuddy.Config.EnableReportMulti, b => GatherBuddy.Config.EnableReportMulti = b);
        public static void DrawEnableGraphs()      
            => DrawCheckbox("Enable Graphs.",
                "When viewing a fishing spot, enable visualization of fish report data. Extreme Testing!",
                GatherBuddy.Config.EnableFishStatsGraphs, b => GatherBuddy.Config.EnableFishStatsGraphs = b);

        // Spearfishing Helper
        public static void DrawSpearfishHelperBox()
            => DrawCheckbox("Show Spearfishing Helper",
                "Toggle whether to show the Spearfishing Helper while spearfishing.",
                GatherBuddy.Config.ShowSpearfishHelper, b => GatherBuddy.Config.ShowSpearfishHelper = b);

        public static void DrawSpearfishNamesBox()
            => DrawCheckbox("Show Fish Name Overlay",
                "Toggle whether to show the identified names of fish in the spearfishing window.",
                GatherBuddy.Config.ShowSpearfishNames, b => GatherBuddy.Config.ShowSpearfishNames = b);

        public static void DrawAvailableSpearfishBox()
            => DrawCheckbox("Show List of Available Fish",
                "Toggle whether to show the list of fish available in your current spearfishing spot on the side of the spearfishing window.",
                GatherBuddy.Config.ShowAvailableSpearfish, b => GatherBuddy.Config.ShowAvailableSpearfish = b);

        public static void DrawSpearfishSpeedBox()
            => DrawCheckbox("Show Speed of Fish in Overlay",
                "Toggle whether to show the speed of fish in the spearfishing window in addition to their names.",
                GatherBuddy.Config.ShowSpearfishSpeed, b => GatherBuddy.Config.ShowSpearfishSpeed = b);

        public static void DrawSpearfishCenterLineBox()
            => DrawCheckbox("Show Center Line",
                "Toggle whether to show a straight line up from the center of the spearfishing gig in the spearfishing window.",
                GatherBuddy.Config.ShowSpearfishCenterLine, b => GatherBuddy.Config.ShowSpearfishCenterLine = b);

        public static void DrawSpearfishIconsAsTextBox()
            => DrawCheckbox("Show Speed and Size as Text",
                "Toggle whether to show the speed and size of available fish as text instead of icons.",
                GatherBuddy.Config.ShowSpearfishListIconsAsText, b => GatherBuddy.Config.ShowSpearfishListIconsAsText = b);

        public static void DrawSpearfishFishNameFixed()
            => DrawCheckbox("Show Fish Names in Fixed Position",
                "Toggle whether to show the identified names of fish on the moving fish themselves or in a fixed position.",
                GatherBuddy.Config.FixNamesOnPosition, b => GatherBuddy.Config.FixNamesOnPosition = b);

        public static void DrawSpearfishFishNamePercentage()
        {
            if (!GatherBuddy.Config.FixNamesOnPosition)
                return;

            var tmp = (int)GatherBuddy.Config.FixNamesPercentage;
            ImGui.SetNextItemWidth(SetInputWidth);
            if (!ImGui.DragInt("Fish Name Position Percentage", ref tmp, 0.1f, 0, 100, "%i%%"))
                return;

            tmp = Math.Clamp(tmp, 0, 100);
            if (tmp == GatherBuddy.Config.FixNamesPercentage)
                return;

            GatherBuddy.Config.FixNamesPercentage = (byte)tmp;
            GatherBuddy.Config.Save();
        }

        // Gather Window
        public static void DrawShowGatherWindowBox()
            => DrawCheckbox("Show Gather Window",
                "Show a small window with pinned Gatherables and their uptimes.",
                GatherBuddy.Config.ShowGatherWindow, b => GatherBuddy.Config.ShowGatherWindow = b);

        public static void DrawGatherWindowAnchorBox()
            => DrawCheckbox("Anchor Gather Window to Bottom Left",
                "Lets the Gather Window grow to the top and shrink from the top instead of the bottom.",
                GatherBuddy.Config.GatherWindowBottomAnchor, b => GatherBuddy.Config.GatherWindowBottomAnchor = b);

        public static void DrawGatherWindowTimersBox()
            => DrawCheckbox("Show Gather Window Timers",
                "Show the uptimes for gatherables in the gather window.",
                GatherBuddy.Config.ShowGatherWindowTimers, b => GatherBuddy.Config.ShowGatherWindowTimers = b);

        public static void DrawGatherWindowAlarmsBox()
            => DrawCheckbox("Show Active Alarms in Gather Window",
                "Additionally show active alarms as a last gather window preset, obeying the regular rules for the window.",
                GatherBuddy.Config.ShowGatherWindowAlarms, b =>
                {
                    GatherBuddy.Config.ShowGatherWindowAlarms = b;
                    _plugin.GatherWindowManager.SetShowGatherWindowAlarms(b);
                });

        public static void DrawSortGatherWindowBox()
            => DrawCheckbox("Sort Gather Window by Uptime",
                "Sort the items selected for the gather window by their uptimes.",
                GatherBuddy.Config.SortGatherWindowByUptime, b => GatherBuddy.Config.SortGatherWindowByUptime = b);

        public static void DrawGatherWindowShowOnlyAvailableBox()
            => DrawCheckbox("Show Only Available Items",
                "Show only those items from your gather window setup that are currently available.",
                GatherBuddy.Config.ShowGatherWindowOnlyAvailable, b => GatherBuddy.Config.ShowGatherWindowOnlyAvailable = b);

        public static void DrawHideGatherWindowCompletedItemsBox()
            => DrawCheckbox("Hide Completed Items",
                "Hide items that have the required inventory amount present in inventory.",
                GatherBuddy.Config.HideGatherWindowCompletedItems, b => GatherBuddy.Config.HideGatherWindowCompletedItems = b);

        public static void DrawHideGatherWindowInDutyBox()
            => DrawCheckbox("Hide Gather Window in Duty",
                "Hide the gather window when bound by any duty.",
                GatherBuddy.Config.HideGatherWindowInDuty, b => GatherBuddy.Config.HideGatherWindowInDuty = b);

        public static void DrawGatherWindowHoldKey()
        {
            DrawCheckbox("Only Show Gather Window if Holding Key",
                "Only show the gather window if you are holding your selected key.",
                GatherBuddy.Config.OnlyShowGatherWindowHoldingKey, b => GatherBuddy.Config.OnlyShowGatherWindowHoldingKey = b);

            if (!GatherBuddy.Config.OnlyShowGatherWindowHoldingKey)
                return;

            ImGui.SetNextItemWidth(SetInputWidth);
            Widget.KeySelector("Hotkey to Hold", "Set the hotkey to hold to keep the window visible.",
                GatherBuddy.Config.GatherWindowHoldKey,
                k => GatherBuddy.Config.GatherWindowHoldKey = k, Configuration.ValidKeys);
        }

        public static void DrawGatherWindowLockBox()
            => DrawCheckbox("Lock Gather Window Position",
                "Prevent moving the gather window by dragging it around.",
                GatherBuddy.Config.LockGatherWindow, b => GatherBuddy.Config.LockGatherWindow = b);


        public static void DrawGatherWindowHotkeyInput()
        {
            if (Widget.ModifiableKeySelector("Hotkey to Open Gather Window", "Set a hotkey to open the Gather Window.", SetInputWidth,
                    GatherBuddy.Config.GatherWindowHotkey, k => GatherBuddy.Config.GatherWindowHotkey = k, Configuration.ValidKeys))
                GatherBuddy.Config.Save();
        }

        public static void DrawMainInterfaceHotkeyInput()
        {
            if (Widget.ModifiableKeySelector("Hotkey to Open Main Interface", "Set a hotkey to open the main GatherBuddy interface.",
                    SetInputWidth,
                    GatherBuddy.Config.MainInterfaceHotkey, k => GatherBuddy.Config.MainInterfaceHotkey = k, Configuration.ValidKeys))
                GatherBuddy.Config.Save();
        }


        public static void DrawGatherWindowDeleteModifierInput()
        {
            ImGui.SetNextItemWidth(SetInputWidth);
            if (Widget.ModifierSelector("Modifier to Delete Items on Right-Click",
                    "Set the modifier key to be used while right-clicking items in the gather window to delete them.",
                    GatherBuddy.Config.GatherWindowDeleteModifier, k => GatherBuddy.Config.GatherWindowDeleteModifier = k))
                GatherBuddy.Config.Save();
        }


        public static void DrawAetherytePreference()
        {
            var tmp     = GatherBuddy.Config.AetherytePreference == AetherytePreference.Cost;
            var oldPref = GatherBuddy.Config.AetherytePreference;
            if (ImGui.RadioButton("Prefer Cheaper Aetherytes", tmp))
                GatherBuddy.Config.AetherytePreference = AetherytePreference.Cost;
            var hovered = ImGui.IsItemHovered();
            ImGui.SameLine();
            if (ImGui.RadioButton("Prefer Less Travel Time", !tmp))
                GatherBuddy.Config.AetherytePreference = AetherytePreference.Distance;
            hovered |= ImGui.IsItemHovered();
            if (hovered)
                ImGui.SetTooltip(
                    "Specify whether you prefer aetherytes that are closer to your target (less travel time)"
                  + " or aetherytes that are cheaper to teleport to when scanning through all available nodes for an item."
                  + " Only matters if the item is not timed and has multiple sources.");

            if (oldPref != GatherBuddy.Config.AetherytePreference)
            {
                GatherBuddy.UptimeManager.ResetLocations();
                GatherBuddy.Config.Save();
            }
        }

        public static void DrawAlarmFormatInput()
            => DrawFormatInput("Alarm Chat Format",
                "Keep empty to have no chat output.\nCan replace:\n- {Alarm} with the alarm name in brackets.\n- {Item} with the item link.\n- {Offset} with the alarm offset in seconds.\n- {DurationString} with 'will be up for the next ...' or 'is currently up for ...'.\n- {Location} with the map flag link and location name.",
                GatherBuddy.Config.AlarmFormat, Configuration.DefaultAlarmFormat, s => GatherBuddy.Config.AlarmFormat = s);

        public static void DrawIdentifiedGatherableFormatInput()
            => DrawFormatInput("Identified Gatherable Chat Format",
                "Keep empty to have no chat output.\nCan replace:\n- {Input} with the entered search text.\n- {Item} with the item link.",
                GatherBuddy.Config.IdentifiedGatherableFormat, Configuration.DefaultIdentifiedGatherableFormat,
                s => GatherBuddy.Config.IdentifiedGatherableFormat = s);

        public static void DrawAlwaysMapsBox()
            => DrawCheckbox("Always gather maps when available",      "GBR will always grab maps first if it sees one in a node",
                GatherBuddy.Config.AutoGatherConfig.AlwaysGatherMaps, b => GatherBuddy.Config.AutoGatherConfig.AlwaysGatherMaps = b);

        public static void DrawUseExistingAutoHookPresetsBox()
        {
            DrawCheckbox("Use existing AutoHook presets",
                "Use your own AutoHook presets instead of GBR-generated ones.\n"
              + "Name your preset using the fish's Item ID (e.g., '46188' for Goldentail).\n"
              + "Find Fish IDs by hovering over fish in the Fish tab.\n"
              + "Your presets will never be deleted - only GBR-generated presets are cleaned up.",
                GatherBuddy.Config.AutoGatherConfig.UseExistingAutoHookPresets,
                b => GatherBuddy.Config.AutoGatherConfig.UseExistingAutoHookPresets = b);
            ImGui.SameLine();
            ImGuiEx.PluginAvailabilityIndicator([new("AutoHook")]);
        }

        public static void DrawSurfaceSlapConfig()
        {
            DrawCheckbox("Enable automatic Surface Slap",
                "Automatically enable Surface Slap for non-target fish that share the same bite type as your target fish.\n"
              + "This helps remove unwanted fish to increase catch rates of your target.",
                GatherBuddy.Config.AutoGatherConfig.EnableSurfaceSlap,
                b => GatherBuddy.Config.AutoGatherConfig.EnableSurfaceSlap = b);
            
            if (GatherBuddy.Config.AutoGatherConfig.EnableSurfaceSlap)
            {
                ImGui.Indent();
                
                var gpAbove = GatherBuddy.Config.AutoGatherConfig.SurfaceSlapGPAbove;
                if (ImGui.RadioButton("Use Surface Slap when GP is Above", gpAbove))
                {
                    GatherBuddy.Config.AutoGatherConfig.SurfaceSlapGPAbove = true;
                    GatherBuddy.Config.Save();
                }
                
                ImGui.SameLine();
                if (ImGui.RadioButton("Below", !gpAbove))
                {
                    GatherBuddy.Config.AutoGatherConfig.SurfaceSlapGPAbove = false;
                    GatherBuddy.Config.Save();
                }
                
                var gpThreshold = GatherBuddy.Config.AutoGatherConfig.SurfaceSlapGPThreshold;
                ImGui.SetNextItemWidth(SetInputWidth);
                if (ImGui.DragInt("GP Threshold", ref gpThreshold, 1, 0, 10000))
                {
                    GatherBuddy.Config.AutoGatherConfig.SurfaceSlapGPThreshold = Math.Max(0, gpThreshold);
                    GatherBuddy.Config.Save();
                }
                ImGuiUtil.HoverTooltip("Surface Slap will be used when your GP is above/below this threshold.");
                
                ImGui.Unindent();
            }
        }

        public static void DrawIdenticalCastConfig()
        {
            DrawCheckbox("Enable automatic Identical Cast",
                "Automatically enable Identical Cast for your target fish to increase catch rates.\n"
              + "Identical Cast improves catch rate when used on the same fishing hole.",
                GatherBuddy.Config.AutoGatherConfig.EnableIdenticalCast,
                b => GatherBuddy.Config.AutoGatherConfig.EnableIdenticalCast = b);
            
            if (GatherBuddy.Config.AutoGatherConfig.EnableIdenticalCast)
            {
                ImGui.Indent();
                
                var gpAbove = GatherBuddy.Config.AutoGatherConfig.IdenticalCastGPAbove;
                if (ImGui.RadioButton("Use Identical Cast when GP is Above", gpAbove))
                {
                    GatherBuddy.Config.AutoGatherConfig.IdenticalCastGPAbove = true;
                    GatherBuddy.Config.Save();
                }
                
                ImGui.SameLine();
                if (ImGui.RadioButton("Below##IdenticalCast", !gpAbove))
                {
                    GatherBuddy.Config.AutoGatherConfig.IdenticalCastGPAbove = false;
                    GatherBuddy.Config.Save();
                }
                
                var gpThreshold = GatherBuddy.Config.AutoGatherConfig.IdenticalCastGPThreshold;
                ImGui.SetNextItemWidth(SetInputWidth);
                if (ImGui.DragInt("GP Threshold##IdenticalCast", ref gpThreshold, 1, 0, 10000))
                {
                    GatherBuddy.Config.AutoGatherConfig.IdenticalCastGPThreshold = Math.Max(0, gpThreshold);
                    GatherBuddy.Config.Save();
                }
                ImGuiUtil.HoverTooltip("Identical Cast will be used when your GP is above/below this threshold.");
                
                ImGui.Unindent();
            }
        }

        public static void DrawAmbitiousLureConfig()
        {
            DrawCheckbox("Enable automatic Ambitious Lure",
                "Automatically enable Ambitious Lure for fish that use Powerful Hookset.",
                GatherBuddy.Config.AutoGatherConfig.EnableAmbitiousLure,
                b => GatherBuddy.Config.AutoGatherConfig.EnableAmbitiousLure = b);
            
            if (GatherBuddy.Config.AutoGatherConfig.EnableAmbitiousLure)
            {
                ImGui.Indent();
                
                var gpAbove = GatherBuddy.Config.AutoGatherConfig.AmbitiousLureGPAbove;
                if (ImGui.RadioButton("Use Ambitious Lure when GP is Above", gpAbove))
                {
                    GatherBuddy.Config.AutoGatherConfig.AmbitiousLureGPAbove = true;
                    GatherBuddy.Config.Save();
                }
                
                ImGui.SameLine();
                if (ImGui.RadioButton("Below##AmbitiousLure", !gpAbove))
                {
                    GatherBuddy.Config.AutoGatherConfig.AmbitiousLureGPAbove = false;
                    GatherBuddy.Config.Save();
                }
                
                var gpThreshold = GatherBuddy.Config.AutoGatherConfig.AmbitiousLureGPThreshold;
                ImGui.SetNextItemWidth(SetInputWidth);
                if (ImGui.DragInt("GP Threshold##AmbitiousLure", ref gpThreshold, 1, 0, 10000))
                {
                    GatherBuddy.Config.AutoGatherConfig.AmbitiousLureGPThreshold = Math.Max(0, gpThreshold);
                    GatherBuddy.Config.Save();
                }
                ImGuiUtil.HoverTooltip("Ambitious Lure will be used when your GP is above/below this threshold.");
                
                ImGui.Unindent();
            }
        }

        public static void DrawModestLureConfig()
        {
            DrawCheckbox("Enable automatic Modest Lure",
                "Automatically enable Modest Lure for fish that use Precision Hookset.",
                GatherBuddy.Config.AutoGatherConfig.EnableModestLure,
                b => GatherBuddy.Config.AutoGatherConfig.EnableModestLure = b);
            
            if (GatherBuddy.Config.AutoGatherConfig.EnableModestLure)
            {
                ImGui.Indent();
                
                var gpAbove = GatherBuddy.Config.AutoGatherConfig.ModestLureGPAbove;
                if (ImGui.RadioButton("Use Modest Lure when GP is Above", gpAbove))
                {
                    GatherBuddy.Config.AutoGatherConfig.ModestLureGPAbove = true;
                    GatherBuddy.Config.Save();
                }
                
                ImGui.SameLine();
                if (ImGui.RadioButton("Below##ModestLure", !gpAbove))
                {
                    GatherBuddy.Config.AutoGatherConfig.ModestLureGPAbove = false;
                    GatherBuddy.Config.Save();
                }
                
                var gpThreshold = GatherBuddy.Config.AutoGatherConfig.ModestLureGPThreshold;
                ImGui.SetNextItemWidth(SetInputWidth);
                if (ImGui.DragInt("GP Threshold##ModestLure", ref gpThreshold, 1, 0, 10000))
                {
                    GatherBuddy.Config.AutoGatherConfig.ModestLureGPThreshold = Math.Max(0, gpThreshold);
                    GatherBuddy.Config.Save();
                }
                ImGuiUtil.HoverTooltip("Modest Lure will be used when your GP is above/below this threshold.");
                
                ImGui.Unindent();
            }
        }

        public static void DrawUseHookTimersBox()
        {
            DrawCheckbox("Use Hook Timers in AutoHook Presets",
                "Enable bite timer windows in generated AutoHook presets.",
                GatherBuddy.Config.AutoGatherConfig.UseHookTimers,
                b => GatherBuddy.Config.AutoGatherConfig.UseHookTimers = b);
            ImGui.SameLine();
            ImGuiEx.PluginAvailabilityIndicator([new("AutoHook")]);
        }

        public static void DrawAutoCollectablesFishingBox()
            => DrawCheckbox("Auto Collectables",
                "Auto accept/decline collectable fish based on minimum collectability.",
                GatherBuddy.Config.AutoGatherConfig.AutoCollectablesFishing,
                b => GatherBuddy.Config.AutoGatherConfig.AutoCollectablesFishing = b);

        public static void DrawDeferRepairDuringFishingBuffsBox()
            => DrawCheckbox("Defer repairs during fishing buffs",
                "Prevents GBR from stopping fishing for repairs when you have active fishing skill buffs.\n"
              + "Buffs like Patience, Surface Slap, Identical Cast, Prize Catch, etc. will be respected.",
                GatherBuddy.Config.AutoGatherConfig.DeferRepairDuringFishingBuffs,
                b => GatherBuddy.Config.AutoGatherConfig.DeferRepairDuringFishingBuffs = b);

        public static void DrawDeferReductionDuringFishingBuffsBox()
            => DrawCheckbox("Defer aetherial reduction during fishing buffs",
                "Prevents GBR from stopping fishing for aetherial reduction when you have active fishing skill buffs.",
                GatherBuddy.Config.AutoGatherConfig.DeferReductionDuringFishingBuffs,
                b => GatherBuddy.Config.AutoGatherConfig.DeferReductionDuringFishingBuffs = b);

        public static void DrawDeferMateriaExtractionDuringFishingBuffsBox()
            => DrawCheckbox("Defer materia extraction during fishing buffs",
                "Prevents GBR from stopping fishing for materia extraction when you have active fishing skill buffs.",
                GatherBuddy.Config.AutoGatherConfig.DeferMateriaExtractionDuringFishingBuffs,
                b => GatherBuddy.Config.AutoGatherConfig.DeferMateriaExtractionDuringFishingBuffs = b);

        public static void DrawFishingCordialConfig()
        {
            DrawCheckbox("Use Cordial",
                "Automatically use cordials in generated fishing presets when GP falls below the minimum threshold.",
                GatherBuddy.Config.AutoGatherConfig.UseCordialForFishing,
                b => GatherBuddy.Config.AutoGatherConfig.UseCordialForFishing = b);

            if (GatherBuddy.Config.AutoGatherConfig.UseCordialForFishing)
            {
                ImGui.Indent();
                ImGui.SetNextItemWidth(150);
                var gpThreshold = GatherBuddy.Config.AutoGatherConfig.CordialForFishingGPThreshold;
                if (ImGui.DragInt("GP Threshold", ref gpThreshold, 1, 0, 10000))
                {
                    GatherBuddy.Config.AutoGatherConfig.CordialForFishingGPThreshold = Math.Max(0, gpThreshold);
                    GatherBuddy.Config.Save();
                }
                ImGuiUtil.HoverTooltip("Use cordial when GP falls below this threshold (prevents overcapping).");
                ImGui.Unindent();
            }
        }

        public static void DrawUsePatienceBox()
            => DrawCheckbox("Use Patience/Patience II",
                "Automatically use Patience/Patience II in generated fishing presets when fishing for:\n"
              + "• Fish requiring mooch chains\n"
              + "• Collectable fish\n"
              + "• Fish that can be used for aetherial reduction",
                GatherBuddy.Config.AutoGatherConfig.UsePatience,
                b => GatherBuddy.Config.AutoGatherConfig.UsePatience = b);

        public static void DrawPrizeCatchConfig()
        {
            DrawCheckbox("Use Prize Catch",
                "Automatically use Prize Catch in generated fishing presets.\n"
              + "Recommended for mooching or Surface Slap fishing.",
                GatherBuddy.Config.AutoGatherConfig.UsePrizeCatch,
                b => GatherBuddy.Config.AutoGatherConfig.UsePrizeCatch = b);
            
            if (GatherBuddy.Config.AutoGatherConfig.UsePrizeCatch)
            {
                ImGui.Indent();
                
                var gpAbove = GatherBuddy.Config.AutoGatherConfig.PrizeCatchGPAbove;
                if (ImGui.RadioButton("Use Prize Catch when GP is Above", gpAbove))
                {
                    GatherBuddy.Config.AutoGatherConfig.PrizeCatchGPAbove = true;
                    GatherBuddy.Config.Save();
                }
                
                ImGui.SameLine();
                if (ImGui.RadioButton("Below##PrizeCatch", !gpAbove))
                {
                    GatherBuddy.Config.AutoGatherConfig.PrizeCatchGPAbove = false;
                    GatherBuddy.Config.Save();
                }
                
                var gpThreshold = GatherBuddy.Config.AutoGatherConfig.PrizeCatchGPThreshold;
                ImGui.SetNextItemWidth(SetInputWidth);
                if (ImGui.DragInt("GP Threshold##PrizeCatch", ref gpThreshold, 1, 0, 10000))
                {
                    GatherBuddy.Config.AutoGatherConfig.PrizeCatchGPThreshold = Math.Max(0, gpThreshold);
                    GatherBuddy.Config.Save();
                }
                ImGuiUtil.HoverTooltip("Prize Catch will be used when your GP is above/below this threshold.");
                
                ImGui.Unindent();
            }
        }

        public static void DrawChumConfig()
        {
            DrawCheckbox("Use Chum",
                "Automatically use Chum in generated fishing presets.",
                GatherBuddy.Config.AutoGatherConfig.UseChum,
                b => GatherBuddy.Config.AutoGatherConfig.UseChum = b);
            
            if (GatherBuddy.Config.AutoGatherConfig.UseChum)
            {
                ImGui.Indent();
                
                var gpAbove = GatherBuddy.Config.AutoGatherConfig.ChumGPAbove;
                if (ImGui.RadioButton("Use Chum when GP is Above", gpAbove))
                {
                    GatherBuddy.Config.AutoGatherConfig.ChumGPAbove = true;
                    GatherBuddy.Config.Save();
                }
                
                ImGui.SameLine();
                if (ImGui.RadioButton("Below##Chum", !gpAbove))
                {
                    GatherBuddy.Config.AutoGatherConfig.ChumGPAbove = false;
                    GatherBuddy.Config.Save();
                }
                
                var gpThreshold = GatherBuddy.Config.AutoGatherConfig.ChumGPThreshold;
                ImGui.SetNextItemWidth(SetInputWidth);
                if (ImGui.DragInt("GP Threshold##Chum", ref gpThreshold, 1, 0, 10000))
                {
                    GatherBuddy.Config.AutoGatherConfig.ChumGPThreshold = Math.Max(0, gpThreshold);
                    GatherBuddy.Config.Save();
                }
                ImGuiUtil.HoverTooltip("Chum will be used when your GP is above/below this threshold.");
                
                ImGui.Unindent();
            }
        }

        public static void DrawFishingConsumablesConfig()
        {
            DrawCheckbox("Use Food",
                "Automatically use configured food when food buff expires (only when NOT fishing or no active fishing buffs).",
                GatherBuddy.Config.AutoGatherConfig.UseFood,
                b => GatherBuddy.Config.AutoGatherConfig.UseFood = b);

            if (GatherBuddy.Config.AutoGatherConfig.UseFood)
            {
                ImGui.Indent();
                ImGui.SetNextItemWidth(150);
                DrawConsumableCombo("Select food", AutoGather.AutoGather.PossibleFoods, 
                    GatherBuddy.Config.AutoGatherConfig.FoodItemId, 
                    id => 
                    {
                        GatherBuddy.Config.AutoGatherConfig.FoodItemId = id;
                        GatherBuddy.Config.Save();
                    });
                ImGui.Unindent();
            }

            DrawCheckbox("Use Medicine",
                "Automatically use configured medicine (like Draft of Spiritbond) when medicine buff expires (only when NOT fishing or no active fishing buffs).",
                GatherBuddy.Config.AutoGatherConfig.UseMedicine,
                b => GatherBuddy.Config.AutoGatherConfig.UseMedicine = b);

            if (GatherBuddy.Config.AutoGatherConfig.UseMedicine)
            {
                ImGui.Indent();
                ImGui.SetNextItemWidth(150);
                DrawConsumableCombo("Select medicine", AutoGather.AutoGather.PossiblePotions, 
                    GatherBuddy.Config.AutoGatherConfig.MedicineItemId, 
                    id => 
                    {
                        GatherBuddy.Config.AutoGatherConfig.MedicineItemId = id;
                        GatherBuddy.Config.Save();
                    });
                ImGui.Unindent();
            }
        }

        private static void DrawConsumableCombo(string label, Lumina.Excel.Sheets.Item[] items, uint currentItemId, Action<uint> onChanged)
        {
            var list = items
                .SelectMany(item => new[]
                {
                    (item, rowid: item.RowId, isHq: false),
                    (item, rowid: item.RowId + 1_000_000, isHq: true)
                })
                .Where(x => !x.isHq || x.item.CanBeHq)
                .Select(x => (name: ItemUtil.GetItemName(x.rowid, includeIcon: true).ExtractText(), x.rowid, count: AutoGather.AutoGather.GetInventoryItemCount(x.rowid)))
                .Where(x => !string.IsNullOrEmpty(x.name))
                .OrderBy(x => x.count == 0)
                .ThenBy(x => x.name)
                .Select(x => x with { name = $"{x.name} ({x.count})" })
                .ToList();

            var selected = (currentItemId > 0 ? list.FirstOrDefault(x => x.rowid == currentItemId).name : null) ?? string.Empty;
            using var combo = ImRaii.Combo(label, selected);
            if (combo)
            {
                if (ImGui.Selectable(string.Empty, currentItemId <= 0))
                {
                    onChanged(0);
                }

                bool? separatorState = null;
                foreach (var (itemname, rowid, count) in list)
                {
                    if (count != 0)
                        separatorState = true;
                    else if (separatorState ?? false)
                    {
                        ImGui.Separator();
                        separatorState = false;
                    }

                    if (ImGui.Selectable(itemname, currentItemId == rowid))
                    {
                        onChanged(rowid);
                    }
                }
            }
        }
        
        public static void DrawDiademAutoAetherCannonBox()
            => DrawCheckbox("Diadem Auto-Aethercannon",
                "Automatically target and fire aethercannon at nearby enemies when gauge is ready (≥200).\n"
              + "Only fires while not pathing/navigating. 2-second cooldown between uses.",
                GatherBuddy.Config.AutoGatherConfig.DiademAutoAetherCannon,
                b => GatherBuddy.Config.AutoGatherConfig.DiademAutoAetherCannon = b);
        
        public static void DrawCollectableAutoTurninBox()
            => DrawCheckbox("Auto-turn in collectables",
                "Automatically turn in collectables when inventory threshold is reached during gathering",
                GatherBuddy.Config.CollectableConfig.AutoTurnInCollectables,
                b => GatherBuddy.Config.CollectableConfig.AutoTurnInCollectables = b);
        
        public static void DrawCollectableThreshold()
        {
            var useInventoryFull = GatherBuddy.Config.CollectableConfig.UseInventoryFullThreshold;
            
            if (ImGui.RadioButton("Collectable Count Threshold", !useInventoryFull))
            {
                GatherBuddy.Config.CollectableConfig.UseInventoryFullThreshold = false;
                GatherBuddy.Config.Save();
            }
            ImGuiUtil.HoverTooltip("Turn in collectables when you have a certain number of collectables.");
            
            if (!useInventoryFull)
            {
                ImGui.Indent();
                var threshold = GatherBuddy.Config.CollectableConfig.CollectableInventoryThreshold;
                ImGui.SetNextItemWidth(150);
                if (ImGui.DragInt("Collectable Count", ref threshold, 1, 1, 999))
                {
                    GatherBuddy.Config.CollectableConfig.CollectableInventoryThreshold = Math.Max(1, threshold);
                    GatherBuddy.Config.Save();
                }
                ImGuiUtil.HoverTooltip("Turn in when you have this many collectables.");
                ImGui.Unindent();
            }
            
            if (ImGui.RadioButton("Inventory Full Threshold", useInventoryFull))
            {
                GatherBuddy.Config.CollectableConfig.UseInventoryFullThreshold = true;
                GatherBuddy.Config.Save();
            }
            ImGuiUtil.HoverTooltip("Turn in collectables when your inventory reaches a certain number of occupied slots.");
            
            if (useInventoryFull)
            {
                ImGui.Indent();
                var fullThreshold = GatherBuddy.Config.CollectableConfig.InventoryFullThreshold;
                ImGui.SetNextItemWidth(150);
                if (ImGui.DragInt("Inventory Slots Used", ref fullThreshold, 1, 1, 140))
                {
                    GatherBuddy.Config.CollectableConfig.InventoryFullThreshold = Math.Max(1, Math.Min(140, fullThreshold));
                    GatherBuddy.Config.Save();
                }
                ImGuiUtil.HoverTooltip("Turn in when your inventory has this many occupied slots (max 140).");
                ImGui.Unindent();
            }
        }
        
        
        public static void DrawBuyAfterEachCollectBox()
            => DrawCheckbox("Buy scrip shop items after each turn-in",
                "Automatically purchase scrip shop items after turning in collectables",
                GatherBuddy.Config.CollectableConfig.BuyAfterEachCollect,
                b => GatherBuddy.Config.CollectableConfig.BuyAfterEachCollect = b);
        
        public static void DrawScripShopItemManager()
        {
            var shopItems = ScripShopItemManager.ShopItems;
            var purchaseList = GatherBuddy.Config.CollectableConfig.ScripShopItems;
            
            ImGui.TextUnformatted("Items in purchase queue:");
            ImGui.Spacing();
            
            if (purchaseList.Count == 0)
            {
                ImGui.TextDisabled("No items in queue. Add items below.");
            }
            else
            {
                ItemToPurchase? toRemove = null;
                
                foreach (var purchaseItem in purchaseList)
                {
                    using var id = ImRaii.PushId($"{purchaseItem.Name}");
                    
                    if (purchaseItem.Item != null && purchaseItem.Item.IconTexture.TryGetWrap(out var wrap, out _))
                    {
                        ImGui.Image(wrap.Handle, new Vector2(24, 24));
                        ImGui.SameLine();
                    }
                    
                    ImGui.AlignTextToFramePadding();
                    ImGui.Text($"{purchaseItem.Name}");
                    ImGui.SameLine(300);
                    
                    unsafe
                    {
                        var inventory = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
                        var currentInventory = purchaseItem.Item != null ? inventory->GetInventoryItemCount(purchaseItem.Item.ItemId) : 0;
                        ImGui.Text($"{currentInventory}");
                    }
                    
                    ImGui.SameLine();
                    ImGui.Text("/");
                    ImGui.SameLine();
                    
                    var quantity = purchaseItem.Quantity;
                    ImGui.SetNextItemWidth(80);
                    if (ImGui.InputInt($"##{purchaseItem.Name}_Quantity", ref quantity, 1, 10))
                    {
                        purchaseItem.Quantity = Math.Max(0, quantity);
                        GatherBuddy.Config.Save();
                    }
                    
                    ImGui.SameLine();
                    if (ImGui.Button($"Remove##{purchaseItem.Name}"))
                    {
                        toRemove = purchaseItem;
                    }
                }
                
                if (toRemove != null)
                {
                    purchaseList.Remove(toRemove);
                    GatherBuddy.Config.Save();
                }
            }
            
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            ImGui.TextUnformatted("Add Item:");
            
            if (shopItems.Count() == 0)
            {
                ImGui.TextDisabled("No scrip shop items available. Data may not be loaded.");
            }
            else
            {
                if (ImGui.BeginCombo("###AddScripShopItem", "Select item..."))
                {
                    ImGui.SetNextItemWidth(SetInputWidth - 20);
                    ImGui.InputTextWithHint("###ScripShopFilter", "Search...", ref _scripShopFilterText, 100);
                    ImGui.Separator();
                    
                    foreach (var item in shopItems)
                    {
                        if (item.Page < 3)
                            continue;
                        
                        if (_scripShopFilterText.Length > 0 && !item.Name.Contains(_scripShopFilterText, StringComparison.OrdinalIgnoreCase))
                            continue;
                        
                        using var id = ImRaii.PushId($"AddItem_{item.Name}");
                        
                        var alreadyAdded = purchaseList.Any(p => p.Name == item.Name);
                        if (alreadyAdded)
                        {
                            ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 0.5f);
                        }
                        
                        if (ImGui.Selectable(item.Name, false, alreadyAdded ? ImGuiSelectableFlags.Disabled : ImGuiSelectableFlags.None))
                        {
                            if (!alreadyAdded)
                            {
                                purchaseList.Add(new ItemToPurchase { Item = item, Quantity = 1 });
                                GatherBuddy.Config.Save();
                                _scripShopFilterText = "";
                            }
                        }
                        
                        if (alreadyAdded)
                        {
                            ImGui.PopStyleVar();
                        }
                    }
                    
                    ImGui.EndCombo();
                }
            }
        }
        
        public static void DrawManualPresetGenerator()
        {
            ImGui.Separator();
            ImGui.TextUnformatted("Manual Preset Generator");
            ImGui.Spacing();
            
            var availableFish = GatherBuddy.GameData.Fishes.Values.Where(f => !f.IsSpearFish).ToList();
            
            ImGui.TextUnformatted("Select Target Fish:");
            ImGui.SetNextItemWidth(SetInputWidth);
            
            if (ImGui.BeginCombo("###FishSelector", _selectedFish?.Name[GatherBuddy.Language] ?? "None"))
            {
                ImGui.SetNextItemWidth(SetInputWidth - 20);
                ImGui.InputTextWithHint("###FishFilter", "Search...", ref _fishFilterText, 100);
                ImGui.Separator();
                
                using (var child = ImRaii.Child("###FishList", new Vector2(0, 200 * ImGuiHelpers.GlobalScale), false))
                {
                    for (int i = 0; i < availableFish.Count; i++)
                    {
                        var fish = availableFish[i];
                        var fishName = fish.Name[GatherBuddy.Language];
                        
                        if (_fishFilterText.Length > 0 && !fishName.ToLower().Contains(_fishFilterText.ToLower()))
                            continue;
                        
                        using var id = ImRaii.PushId($"{fish.ItemId}###{i}");
                        if (ImGui.Selectable(fishName, _selectedFish?.ItemId == fish.ItemId))
                        {
                            _selectedFish = fish;
                            _presetName = fish.ItemId.ToString();
                            _fishFilterText = "";
                            ImGui.CloseCurrentPopup();
                        }
                    }
                }
                
                ImGui.EndCombo();
            }
            
            if (_selectedFish != null)
            {
                ImGui.Spacing();
                ImGui.TextUnformatted("Preset Name:");
                ImGui.SetNextItemWidth(SetInputWidth);
                ImGui.InputText("###PresetNameInput", ref _presetName, 64);
                ImGuiUtil.HoverTooltip("The preset name should match the fish's Item ID for GBR to use it automatically.");
                
                ImGui.Spacing();
                if (ImGui.Button("Generate Preset"))
                {
                    GenerateManualPreset(_selectedFish, _presetName);
                }
            }
        }
        
        private static void GenerateManualPreset(Fish fish, string presetName)
        {
            if (string.IsNullOrWhiteSpace(presetName))
                presetName = fish.ItemId.ToString();
            
            var success = AutoHookIntegration.AutoHookService.ExportPresetToAutoHook(presetName, [fish]);
            
            if (success)
            {
                Dalamud.Chat.Print($"[GatherBuddy] Generated preset '{presetName}' for {fish.Name[GatherBuddy.Language]}");
            }
            else
            {
                Dalamud.Chat.PrintError($"[GatherBuddy] Failed to generate preset for {fish.Name[GatherBuddy.Language]}");
            }
        }
    }


    private void DrawConfigTab()
    {
        using var id  = ImRaii.PushId("Config");
        using var tab = ImRaii.TabItem("Config");
        ImGuiUtil.HoverTooltip("Set up your very own GatherBuddy to your meticulous specifications.\n"
          + "If you treat him well, he might even become a real boy.");

        if (!tab)
            return;

        using var child = ImRaii.Child("ConfigTab");
        if (!child)
            return;

        if (ImGui.CollapsingHeader("Auto-Gather"))
        {
            if (ImGui.TreeNodeEx("General##autoGeneral"))
            {
                AutoGatherUI.DrawMountSelector();
                ConfigFunctions.DrawMountUpDistance();
                ConfigFunctions.DrawLandingDistance();
                ConfigFunctions.DrawMoveWhileMounting();
                ConfigFunctions.DrawHonkModeBox();
                if (GatherBuddy.Config.AutoGatherConfig.HonkMode)
                {
                    ConfigFunctions.DrawHonkVolumeSlider();
                }
                ConfigFunctions.DrawCheckRetainersBox();
                ConfigFunctions.DrawGoHomeBox();
                ConfigFunctions.DrawUseGivingLandOnCooldown();
                ConfigFunctions.DrawUseSkillsForFallabckBox();
                ConfigFunctions.DrawAbandonNodesBox();
                ConfigFunctions.DrawAlwaysMapsBox();
                ImGui.TreePop();
            }

            if (ImGui.TreeNodeEx("Fishing"))
            {
                ConfigFunctions.DrawUseExistingAutoHookPresetsBox();
                ConfigFunctions.DrawFishingSpotMinutes();
                ConfigFunctions.DrawFishCollectionBox();
                ConfigFunctions.DrawAutoCollectablesFishingBox();
                ConfigFunctions.DrawDeferRepairDuringFishingBuffsBox();
                ConfigFunctions.DrawDeferReductionDuringFishingBuffsBox();
                ConfigFunctions.DrawDeferMateriaExtractionDuringFishingBuffsBox();
                ConfigFunctions.DrawFishingCordialConfig();
                ConfigFunctions.DrawFishingConsumablesConfig();
                ConfigFunctions.DrawUseHookTimersBox();
                ConfigFunctions.DrawUsePatienceBox();
                ConfigFunctions.DrawPrizeCatchConfig();
                ConfigFunctions.DrawChumConfig();
                ConfigFunctions.DrawSurfaceSlapConfig();
                ConfigFunctions.DrawIdenticalCastConfig();
                ConfigFunctions.DrawAmbitiousLureConfig();
                ConfigFunctions.DrawModestLureConfig();
                ConfigFunctions.DrawManualPresetGenerator();
                ImGui.TreePop();
            }

            if (ImGui.TreeNodeEx("Advanced"))
            {
                ConfigFunctions.DrawRepairBox();
                if (GatherBuddy.Config.AutoGatherConfig.DoRepair)
                {
                    ConfigFunctions.DrawRepairThreshold();
                }
                ConfigFunctions.DrawMaterialExtraction();
                ConfigFunctions.DrawAetherialReduction();
                ConfigFunctions.DrawAutoretainerBox();
                if (GatherBuddy.Config.AutoGatherConfig.AutoRetainerMultiMode)
                {
                    ConfigFunctions.DrawAutoretainerThreshold();
                    ConfigFunctions.DrawAutoretainerTimedNodeDelayBox();
                }
                ConfigFunctions.DrawDiademAutoAetherCannonBox();
                ConfigFunctions.DrawSortingMethodCombo();
                ConfigFunctions.DrawLifestreamCommandTextInput();
                ConfigFunctions.DrawAntiStuckCooldown();
                ConfigFunctions.DrawStuckThreshold();
                ConfigFunctions.DrawTimedNodePrecog();
                ConfigFunctions.DrawExecutionDelay();
                ConfigFunctions.DrawAutoGatherBox();
                ConfigFunctions.DrawUseFlagBox();
                ConfigFunctions.DrawUseNavigationBox();
                ConfigFunctions.DrawForceWalkingBox();
                ImGui.TreePop();
            }
            
            if (ImGui.TreeNodeEx("Collectable"))
            {
                ConfigFunctions.DrawCollectableAutoTurninBox();
                if (GatherBuddy.Config.CollectableConfig.AutoTurnInCollectables)
                {
                    ConfigFunctions.DrawCollectableThreshold();
                }
                ConfigFunctions.DrawBuyAfterEachCollectBox();
                
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();
                
                if (ImGui.CollapsingHeader("Scrip Shop Purchase List"))
                {
                    ConfigFunctions.DrawScripShopItemManager();
                }
                
                ImGui.TreePop();
            }
        }

        if (ImGui.CollapsingHeader("General"))
        {
            if (ImGui.TreeNodeEx("Gather Command"))
            {
                ConfigFunctions.DrawPreferredJobSelect();
                ConfigFunctions.DrawGearChangeBox();
                ConfigFunctions.DrawTeleportBox();
                ConfigFunctions.DrawMapOpenBox();
                ConfigFunctions.DrawPlaceMarkerBox();
                ConfigFunctions.DrawPlaceWaymarkBox();
                ConfigFunctions.DrawAetherytePreference();
                ConfigFunctions.DrawSkipTeleportBox();
                ConfigFunctions.DrawContextMenuBox();
                ImGui.TreePop();
            }

            if (ImGui.TreeNodeEx("Set Names"))
            {
                ConfigFunctions.DrawSetInput("Miner",    GatherBuddy.Config.MinerSetName,    s => GatherBuddy.Config.MinerSetName    = s);
                ConfigFunctions.DrawSetInput("Botanist", GatherBuddy.Config.BotanistSetName, s => GatherBuddy.Config.BotanistSetName = s);
                ConfigFunctions.DrawSetInput("Fisher",   GatherBuddy.Config.FisherSetName,   s => GatherBuddy.Config.FisherSetName   = s);
                ImGui.TreePop();
            }

            if (ImGui.TreeNodeEx("Alarms"))
            {
                ConfigFunctions.DrawAlarmToggle();
                ConfigFunctions.DrawAlarmsInDutyToggle();
                ConfigFunctions.DrawAlarmsOnlyWhenLoggedInToggle();
                ConfigFunctions.DrawWeatherAlarmPicker();
                ConfigFunctions.DrawHourAlarmPicker();
                ImGui.TreePop();
            }

            if (ImGui.TreeNodeEx("Messages"))
            {
                ConfigFunctions.DrawPrintTypeSelector();
                ConfigFunctions.DrawErrorTypeSelector();
                ConfigFunctions.DrawMapMarkerPrintBox();
                ConfigFunctions.DrawPrintUptimesBox();
                ConfigFunctions.DrawPrintClipboardBox();
                ConfigFunctions.DrawAlarmFormatInput();
                ConfigFunctions.DrawIdentifiedGatherableFormatInput();
                ImGui.TreePop();
            }

            ImGui.NewLine();
        }

        if (ImGui.CollapsingHeader("Interface"))
        {
            if (ImGui.TreeNodeEx("Config Window"))
            {
                ConfigFunctions._base = this;
                ConfigFunctions.DrawOpenOnStartBox();
                ConfigFunctions.DrawRespectEscapeBox();
                ConfigFunctions.DrawLockPositionBox();
                ConfigFunctions.DrawLockResizeBox();
                ConfigFunctions.DrawWeatherTabNamesBox();
                ConfigFunctions.DrawShowStatusLineBox();
                ConfigFunctions.DrawHideClippyBox();
                ConfigFunctions.DrawMainInterfaceHotkeyInput();
                ImGui.TreePop();
            }

            if (ImGui.TreeNodeEx("Fish Timer"))
            {
                ConfigFunctions.DrawKeepRecordsBox();
                ConfigFunctions.DrawShowLocalTimeInRecordsBox();
                ConfigFunctions.DrawFishTimerBox();
                ConfigFunctions.DrawFishTimerEditBox();
                ConfigFunctions.DrawFishTimerClickthroughBox();
                ConfigFunctions.DrawFishTimerHideBox();
                ConfigFunctions.DrawFishTimerHideBox2();
                ConfigFunctions.DrawFishTimerUptimesBox();
                ConfigFunctions.DrawFishTimerScale();
                ConfigFunctions.DrawFishTimerIntervals();
                ConfigFunctions.DrawFishTimerIntervalsRounding();
                ConfigFunctions.DrawHideFishPopupBox();
                ConfigFunctions.DrawCollectableHintPopupBox();
                ConfigFunctions.DrawDoubleHookHintPopupBox();
                ImGui.TreePop();
            }

            if (ImGui.TreeNodeEx("Fish Stats [Testing]"))
            {
                ConfigFunctions.DrawEnableFishStats();
                ConfigFunctions.DrawEnableReportTime();
                ConfigFunctions.DrawEnableReportSize();
                ConfigFunctions.DrawEnableReportMulti();
                ConfigFunctions.DrawEnableGraphs();
                ImGui.TreePop();
            }

            if (ImGui.TreeNodeEx("Gather Window"))
            {
                ConfigFunctions.DrawShowGatherWindowBox();
                ConfigFunctions.DrawGatherWindowAnchorBox();
                ConfigFunctions.DrawGatherWindowTimersBox();
                ConfigFunctions.DrawGatherWindowAlarmsBox();
                ConfigFunctions.DrawSortGatherWindowBox();
                ConfigFunctions.DrawGatherWindowShowOnlyAvailableBox();
                ConfigFunctions.DrawHideGatherWindowCompletedItemsBox();
                ConfigFunctions.DrawHideGatherWindowInDutyBox();
                ConfigFunctions.DrawGatherWindowHoldKey();
                ConfigFunctions.DrawGatherWindowLockBox();
                ConfigFunctions.DrawGatherWindowHotkeyInput();
                ConfigFunctions.DrawGatherWindowDeleteModifierInput();
                ImGui.TreePop();
            }

            if (ImGui.TreeNodeEx("Spearfishing Helper"))
            {
                ConfigFunctions.DrawSpearfishHelperBox();
                ConfigFunctions.DrawSpearfishNamesBox();
                ConfigFunctions.DrawSpearfishSpeedBox();
                ConfigFunctions.DrawAvailableSpearfishBox();
                ConfigFunctions.DrawSpearfishIconsAsTextBox();
                ConfigFunctions.DrawSpearfishCenterLineBox();
                ConfigFunctions.DrawSpearfishFishNameFixed();
                ConfigFunctions.DrawSpearfishFishNamePercentage();
                ImGui.TreePop();
            }

            ImGui.NewLine();
        }

        if (ImGui.CollapsingHeader("Colors"))
        {
            foreach (var color in Enum.GetValues<ColorId>())
            {
                var (defaultColor, name, description) = color.Data();
                var currentColor = GatherBuddy.Config.Colors.TryGetValue(color, out var current) ? current : defaultColor;
                if (Widget.ColorPicker(name, description, currentColor, c => GatherBuddy.Config.Colors[color] = c, defaultColor))
                    GatherBuddy.Config.Save();
            }

            ImGui.NewLine();

            if (Widget.PaletteColorPicker("Names in Chat", Vector2.One * ImGui.GetFrameHeight(), GatherBuddy.Config.SeColorNames,
                    Configuration.DefaultSeColorNames, Configuration.ForegroundColors, out var idx))
                GatherBuddy.Config.SeColorNames = idx;
            if (Widget.PaletteColorPicker("Commands in Chat", Vector2.One * ImGui.GetFrameHeight(), GatherBuddy.Config.SeColorCommands,
                    Configuration.DefaultSeColorCommands, Configuration.ForegroundColors, out idx))
                GatherBuddy.Config.SeColorCommands = idx;
            if (Widget.PaletteColorPicker("Arguments in Chat", Vector2.One * ImGui.GetFrameHeight(), GatherBuddy.Config.SeColorArguments,
                    Configuration.DefaultSeColorArguments, Configuration.ForegroundColors, out idx))
                GatherBuddy.Config.SeColorArguments = idx;
            if (Widget.PaletteColorPicker("Alarm Message in Chat", Vector2.One * ImGui.GetFrameHeight(), GatherBuddy.Config.SeColorAlarm,
                    Configuration.DefaultSeColorAlarm, Configuration.ForegroundColors, out idx))
                GatherBuddy.Config.SeColorAlarm = idx;

            ImGui.NewLine();
        }
    }
}

