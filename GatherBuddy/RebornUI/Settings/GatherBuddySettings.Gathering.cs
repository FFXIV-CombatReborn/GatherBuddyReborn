using Dalamud.Interface;
using GatherBuddy.Alarms;
using GatherBuddy.Config;
using GatherBuddy.Gui;
using GatherBuddy.RebornUI.Settings;
using RebornMaterial;
using System;
using GatheringType = GatherBuddy.Enums.GatheringType;

namespace GatherBuddy.RebornUI;

internal sealed partial class GatherBuddySettings
{
    private const string ChatNote = "The message only goes to your own chat log, whatever the channel; nobody else sees it.";

    private static readonly string[] PreferredJobLabels = ["No Preference", "Miner", "Botanist"];
    private static readonly GatheringType[] PreferredJobs = [GatheringType.Multiple, GatheringType.Miner, GatheringType.Botanist];

    private static readonly M3Segment[] AetheryteSegments =
    [
        new("Less travel time", FontAwesomeIcon.Route, "Prefer aetherytes closer to the target."),
        new("Cheaper", FontAwesomeIcon.Coins, "Prefer aetherytes that are cheaper to teleport to."),
    ];

    private M3SettingsPage BuildGatheringPage()
        => new(GatheringPageId, "Gathering settings", FontAwesomeIcon.MapMarkedAlt, "What /gather does, gear sets, alarms and chat messages",
            new M3SettingsGroup("gather_command", "Gather command", FontAwesomeIcon.Terminal, "What happens when you use /gather on an item.",
                Custom("Preferred job no preference miner botanist", DrawPreferredJob),
                Toggle("Enable gear change",
                    "Automatically switch to the correct job gear for a node, using the miner, botanist and fisher sets below.",
                    () => Config.UseGearChange, v => Config.UseGearChange = v),
                Toggle("Enable teleport", "Automatically teleport to the chosen node.",
                    () => Config.UseTeleport, v => Config.UseTeleport = v),
                Toggle("Open map with location",
                    "Automatically open the map of the node's territory with its gathering location highlighted.",
                    () => Config.UseCoordinates, v => Config.UseCoordinates = v),
                Toggle("Place flag marker on map",
                    "Automatically set a red flag marker on the approximate location of the chosen node, without opening the map.",
                    () => Config.UseFlag, v => Config.UseFlag = v),
                Toggle("Place custom waymarks", "Place the custom waymarks you have set up for certain locations.",
                    () => Config.PlaceCustomWaymarks, v => Config.PlaceCustomWaymarks = v),
                Custom("Prefer cheaper aetherytes prefer less travel time aetheryte preference", DrawAetherytePreference),
                Toggle("Skip nearby teleports",
                    "Skip teleporting if you are in the same map and already closer to the target than the selected aetheryte.",
                    () => Config.SkipTeleportIfClose, v => Config.SkipTeleportIfClose = v),
                Toggle("Add in-game context menus", "Add a 'Gather' entry to in-game right-click menus for gatherable items.",
                    () => Config.AddIngameContextMenus, SetContextMenus, "right-click")),
            new M3SettingsGroup("set_names", "Gear sets", FontAwesomeIcon.Tshirt, "Used by gear change. The name or the number of each gear set.",
                Custom("Miner set name", () => SettingRows.Text("Miner set", "The name of your miner set, or its number.",
                    Config.MinerSetName, v => Config.MinerSetName = v, "Miner", 15)),
                Custom("Botanist set name", () => SettingRows.Text("Botanist set", "The name of your botanist set, or its number.",
                    Config.BotanistSetName, v => Config.BotanistSetName = v, "Botanist", 15)),
                Custom("Fisher set name", () => SettingRows.Text("Fisher set", "The name of your fisher set, or its number.",
                    Config.FisherSetName, v => Config.FisherSetName = v, "Fisher", 15))),
            new M3SettingsGroup("alarms", "Alarms", FontAwesomeIcon.Bell, null,
                Toggle("Enable alarms", "Turn all alarms on or off.", () => Config.AlarmsEnabled, SetAlarmsEnabled),
                Toggle("Enable alarms in duty", "Let alarms trigger while you are bound by a duty.",
                    () => Config.AlarmsInDuty, v => Config.AlarmsInDuty = v),
                Toggle("Enable alarms only in-game", "Only let alarms trigger while you are logged in to a character.",
                    () => Config.AlarmsOnlyWhenLoggedIn, v => Config.AlarmsOnlyWhenLoggedIn = v),
                Custom("Weather change alarm sound", () => DrawAlarmSound("Weather change alarm",
                    "A sound played every 8 Eorzea hours, on regular weather changes.", Config.WeatherAlarm, AlarmManager.SetWeatherAlarm)),
                Custom("Eorzea hour change alarm sound", () => DrawAlarmSound("Eorzea hour change alarm",
                    "A sound played every time the Eorzea hour changes.", Config.HourAlarm, AlarmManager.SetHourAlarm))),
            new M3SettingsGroup("messages", "Chat messages", FontAwesomeIcon.CommentDots, null,
                Custom("Chat type for messages channel", () => SettingRows.Combo("Chat type for messages",
                    $"The chat type used for GatherBuddy's regular messages. {ChatNote}", Config.ChatTypeMessage, v => Config.ChatTypeMessage = v,
                    search: true)),
                Custom("Chat type for errors channel", () => SettingRows.Combo("Chat type for errors",
                    $"The chat type used for GatherBuddy's error messages. {ChatNote}", Config.ChatTypeError, v => Config.ChatTypeError = v,
                    search: true)),
                Toggle("Print map location", "Write a map link to the approximate location of the chosen node to chat.",
                    () => Config.WriteCoordinates, v => Config.WriteCoordinates = v),
                Toggle("Print node uptimes on gather", "Print the uptimes of nodes you /gather to chat, if they are not always up.",
                    () => Config.PrintUptime, v => Config.PrintUptime = v),
                Toggle("Print clipboard information",
                    "Print to chat whenever you copy something to the clipboard. Failures are always printed.",
                    () => Config.PrintClipboardMessages, v => Config.PrintClipboardMessages = v),
                Custom("Alarm chat format Alarm Item Offset DurationString Location", () => SettingRows.FormatText("Alarm chat format",
                    "Leave empty for no chat message. {Alarm} is the alarm name in brackets, {Item} the item link, {Offset} the alarm offset in seconds, "
                  + "{DurationString} says how long it will be up or is up for, and {Location} is the map flag link and location name.",
                    Config.AlarmFormat, Configuration.DefaultAlarmFormat, v => Config.AlarmFormat = v)),
                Custom("Identified gatherable chat format Input Item", () => SettingRows.FormatText("Identified gatherable chat format",
                    "Leave empty for no chat message. {Input} is the text you searched for and {Item} the item link.",
                    Config.IdentifiedGatherableFormat, Configuration.DefaultIdentifiedGatherableFormat, v => Config.IdentifiedGatherableFormat = v))));

    private static void DrawPreferredJob()
    {
        var current = Config.PreferredGatheringType;
        var index = Math.Max(0, Array.IndexOf(PreferredJobs, current));
        SettingRows.Combo("Preferred job",
            "For items both miners and botanists can gather, turn /gather into /gathermin or /gatherbtn. The other job is ignored even on later tries.",
            index, PreferredJobLabels, i => Config.PreferredGatheringType = PreferredJobs[i]);
    }

    private static void DrawAetherytePreference()
    {
        var index = Config.AetherytePreference == AetherytePreference.Cost ? 1 : 0;
        if (!M3Widgets.RowSegmented("Aetheryte preference", ref index, AetheryteSegments,
                "When an item isn't timed and has several sources, prefer aetherytes closer to the target or cheaper teleports."))
        {
            return;
        }

        var preference = index == 1 ? AetherytePreference.Cost : AetherytePreference.Distance;
        if (preference == Config.AetherytePreference)
        {
            return;
        }

        Config.AetherytePreference = preference;
        GatherBuddy.UptimeManager.ResetLocations();
        Config.Save();
    }

    private void SetContextMenus(bool enabled)
    {
        Config.AddIngameContextMenus = enabled;
        if (enabled)
        {
            _plugin.ContextMenu.Enable();
        }
        else
        {
            _plugin.ContextMenu.Disable();
        }
    }

    private void SetAlarmsEnabled(bool enabled)
    {
        if (enabled)
        {
            _plugin.AlarmManager.Enable();
        }
        else
        {
            _plugin.AlarmManager.Disable();
        }
    }

    private static void DrawAlarmSound(string label, string supporting, Sounds current, Action<Sounds> setter)
    {
        var sounds = Interface.AlarmSoundIds;
        var index = Array.IndexOf(sounds, current);
        SettingRows.Combo(label, supporting, index, Interface.AlarmSoundNames, i => setter(sounds[i]));
    }
}
