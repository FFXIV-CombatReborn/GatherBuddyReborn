using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Configuration;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Text;
using GatherBuddy.Alarms;
using GatherBuddy.AutoGather;
using GatherBuddy.Crafting;
using Newtonsoft.Json;
using GatherBuddy.Enums;
using ElliLib.Classes;
using GatherBuddy.Vulcan.Vendors;

namespace GatherBuddy.Config;

public partial class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 14;

    // Set Names
    public string BotanistSetName { get; set; } = "Botanist";
    public string MinerSetName    { get; set; } = "Miner";
    public string FisherSetName   { get; set; } = "Fisher";

    // formats
    public string IdentifiedGatherableFormat { get; set; } = DefaultIdentifiedGatherableFormat;
    public string AlarmFormat                { get; set; } = DefaultAlarmFormat;


    // Interface
    public AetherytePreference AetherytePreference { get; set; } = AetherytePreference.Distance;
    public ItemFilter          ShowItems           { get; set; } = ItemFilter.All;
    public FishFilter          ShowFish            { get; set; } = FishFilter.All;
    public PatchFlag           HideFishPatch       { get; set; } = 0;
    public JobFlags            LocationFilter      { get; set; } = (JobFlags)0x3F;


    // General Config
    public bool             OpenOnStart               { get; set; } = false;
    public bool             MainWindowLockPosition    { get; set; } = false;
    public bool             MainWindowLockResize      { get; set; } = false;
    public bool             CloseOnEscape             { get; set; } = true;
    public bool             UseGearChange             { get; set; } = true;
    public bool             UseTeleport               { get; set; } = true;
    public bool             UseCoordinates            { get; set; } = true;
    public bool             UseFlag                   { get; set; } = true;
    public bool             WriteCoordinates          { get; set; } = true;
    public bool             PrintUptime               { get; set; } = true;
    public bool             SkipTeleportIfClose       { get; set; } = true;
    public XivChatType      ChatTypeMessage           { get; set; } = XivChatType.Echo;
    public XivChatType      ChatTypeError             { get; set; } = XivChatType.ErrorMessage;
    public bool             AddIngameContextMenus     { get; set; } = true;
    public bool             StoreFishRecords          { get; set; } = true;
    public bool             UseUnixTimeFishRecords    { get; set; } = true;
    public bool             PrintClipboardMessages    { get; set; } = true;
    public bool             HideClippy                { get; set; } = false;
    public bool             ShowStatusLine            { get; set; } = true;
    public ModifiableHotkey MainInterfaceHotkey       { get; set; } = new();
    public bool             PlaceCustomWaymarks       { get; set; } = true;
    public GatheringType    PreferredGatheringType    { get; set; } = GatheringType.Multiple;

    // AutoGather Config
    public AutoGatherConfig AutoGatherConfig              { get; set; } = new();
    public float            AutoGatherListSelectorWidth { get; set; } = 225f;

    // Collectable Config
    public CollectableConfig CollectableConfig { get; set; } = new();

    // Vulcan Configs
    public RaphaelSolveCoordinatorConfig RaphaelSolverConfig { get; set; } = new();
    public Vulcan.StandardSolverConfig StandardSolverConfig { get; set; } = new();
    public VulcanRepairConfig VulcanRepairConfig { get; set; } = new();
    public VulcanMateriaConfig VulcanMateriaConfig { get; set; } = new();
    public VulcanRetainerBellConfig VulcanRetainerBellConfig { get; set; } = new();
    public int VulcanExecutionDelayMs { get; set; } = 300;
    public string CraftingLists { get; set; } = string.Empty;
    public int MaxRecentCraftingListsInContextMenu { get; set; } = 10;
    public Vector2 TeamCraftImportWindowSize { get; set; } = new(520, 310);
    public Vector2 VendorTeamCraftImportWindowSize { get; set; } = new(520, 310);
    public string RecipeBrowserSettings { get; set; } = string.Empty;
    public string UserMacros             { get; set; } = string.Empty;
    public bool   SkipMacroStepIfUnable { get; set; } = true;
    public bool   MacroFallbackEnabled  { get; set; } = true;
    public Dictionary<string, uint> VendorNpcPreferences { get; set; } = new();
    public Dictionary<string, string> VendorRoutePreferences { get; set; } = new();
    [JsonProperty("VendorBuyListEntries", NullValueHandling = NullValueHandling.Ignore)]
    public List<VendorBuyListEntry>? LegacyVendorBuyListEntries { get; set; }
    public List<VendorBuyListDefinition> VendorBuyLists { get; set; } = new();
    public Guid ActiveVendorBuyListId { get; set; } = Guid.Empty;
    public bool   VendorNpcLocationsDataShareFirst { get; set; } = true;

    // Weather tab
    public bool ShowWeatherNames { get; set; } = true;

    // Alarms
    public bool   AlarmsEnabled          { get; set; } = false;
    public bool   AlarmsInDuty           { get; set; } = true;
    public bool   AlarmsOnlyWhenLoggedIn { get; set; } = false;
    public Sounds WeatherAlarm           { get; set; } = Sounds.None;
    public Sounds HourAlarm              { get; set; } = Sounds.None;

    // Colors
    public Dictionary<ColorId, uint> Colors { get; set; }
        = Enum.GetValues<ColorId>().ToDictionary(c => c, c => c.Data().DefaultColor);

    public int SeColorNames     = DefaultSeColorNames;
    public int SeColorCommands  = DefaultSeColorCommands;
    public int SeColorArguments = DefaultSeColorArguments;
    public int SeColorAlarm     = DefaultSeColorAlarm;

    // Fish Timer
    public bool   ShowFishTimer           { get; set; } = true;
    public bool   FishTimerEdit           { get; set; } = true;
    public bool   FishTimerClickthrough   { get; set; } = false;
    public bool   HideUncaughtFish        { get; set; } = false;
    public bool   HideUnavailableFish     { get; set; } = false;
    public bool   ShowFishTimerUptimes    { get; set; } = true;
    public bool   HideFishSizePopup       { get; set; } = false;
    public ushort FishTimerScale          { get; set; } = 40000;
    public byte   ShowSecondIntervals     { get; set; } = 7;
    public int    SecondIntervalsRounding { get; set; } = 1;
    public bool   ShowCollectableHints    { get; set; } = true;
    public bool   ShowMultiHookHints      { get; set; } = true;
    public bool   ShowOceanTypeHints      { get; set; } = true;
    
    // Fish Stats Tab
    public bool EnableFishStats       { get; set; } = false;
    public bool EnableReportTime      { get; set; } = true;
    public bool EnableReportSize      { get; set; } = true;
    public bool EnableReportMulti     { get; set; } = true;
    public bool EnableFishStatsGraphs { get; set; } = false;
    public int  FishStatsSelectedIdx  { get; set; }

    // Spearfish Helper
    public bool ShowSpearfishHelper          { get; set; } = true;
    public bool ShowSpearfishNames           { get; set; } = true;
    public bool ShowAvailableSpearfish       { get; set; } = true;
    public bool ShowSpearfishSpeed           { get; set; } = false;
    public bool ShowSpearfishCenterLine      { get; set; } = true;
    public bool ShowSpearfishListIconsAsText { get; set; } = false;
    public bool FixNamesOnPosition           { get; set; } = false;
    public byte FixNamesPercentage           { get; set; } = 55;


    // Gather Window
    public bool             ShowGatherWindow               { get; set; } = true;
    public bool             ShowGatherWindowTimers         { get; set; } = true;
    public bool             ShowGatherWindowAlarms         { get; set; } = true;
    public bool             SortGatherWindowByUptime       { get; set; } = false;
    public bool             ShowGatherWindowOnlyAvailable  { get; set; } = false;
    public bool             HideGatherWindowCompletedItems { get; set; } = false;
    public bool             HideGatherWindowInDuty         { get; set; } = true;
    public bool             OnlyShowGatherWindowHoldingKey { get; set; } = false;
    public bool             LockGatherWindow               { get; set; } = false;
    public bool             GatherWindowBottomAnchor       { get; set; } = false;
    public ModifiableHotkey GatherWindowHotkey             { get; set; } = new(VirtualKey.G, VirtualKey.CONTROL);
    public ModifierHotkey   GatherWindowDeleteModifier     { get; set; } = VirtualKey.CONTROL;
    public VirtualKey       GatherWindowHoldKey            { get; set; } = VirtualKey.MENU;

    public void Save()
        => Dalamud.PluginInterface.SavePluginConfig(this);

    public bool ShouldSerializeLegacyVendorBuyListEntries()
        => false;


    // Add missing colors to the dictionary if necessary.
    private void AddColors()
    {
        var save = false;
        foreach (var color in Enum.GetValues<ColorId>())
            save |= Colors.TryAdd(color, color.Data().DefaultColor);
        if (save)
            Save();
    }

    public static Configuration Load()
    {
        try
        {
            if (Dalamud.PluginInterface.GetPluginConfig() is Configuration config)
            {
                config.AddColors();
                config.Migrate4To5();
                config.Migrate5To6();
                config.Migrate6To7();
                config.Migrate7To8();
                config.Migrate8To9();
                config.Migrate9To10();
                config.Migrate10To11();
                config.Migrate11To12();
                config.Migrate12To13();
                config.Migrate13To14();
                config.VendorNpcPreferences ??= new();
                config.VendorRoutePreferences ??= new();
                config.VendorBuyLists ??= new();
                if (config.EnsureVendorBuyListState())
                    config.Save();
                return config;
            }
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"Failed to load configuration, creating new one: {ex}");
            try
            {
                var configPath = Dalamud.PluginInterface.ConfigFile.FullName;
                var backupPath = $"{configPath}.backup_{DateTime.Now:yyyyMMdd_HHmmss}";
                if (File.Exists(configPath))
                {
                    File.Copy(configPath, backupPath);
                    GatherBuddy.Log.Warning($"Corrupted config backed up to: {backupPath}");
                }
            }
            catch (Exception backupEx)
            {
                GatherBuddy.Log.Error($"Failed to backup corrupted config: {backupEx}");
            }
        }

        var newConfig = new Configuration();
        newConfig.EnsureVendorBuyListState();
        newConfig.Save();
        return newConfig;
    }

    public void Migrate4To5()
    {
        if (Version >= 5)
            return;

        ShowFish |= FishFilter.Collectible | FishFilter.NotCollectible;
        Version  =  5;
        Save();
    }

    public void Migrate5To6()
    {
        if (Version >= 6)
            return;

        ShowItems |= ItemFilter.Dawntrail;
        Version   =  6;
        Save();
    }

    public void Migrate6To7()
    {
        if (Version >= 7)
            return;

        if (AutoGatherConfig == null)
        {
            AutoGatherConfig = new();
        }

        Version = 7;
        Save();
    }

    public void Migrate7To8()
    {
        if (Version >= 8)
            return;

        if (CollectableConfig == null)
        {
            CollectableConfig = new();
        }

        Version = 8;
        Save();
    }
    
    public void Migrate8To9()
    {
        if (Version >= 9)
            return;
        
        if (CollectableConfig.PreferredCollectableShop == null || 
            string.IsNullOrEmpty(CollectableConfig.PreferredCollectableShop.Name))
        {
            CollectableConfig.PreferredCollectableShop = AutoGather.Collectables.CollectableNpcLocations.GetDefaultShop();
        }

        Version = 9;
        Save();
    }

    public void Migrate9To10()
    {
        if (Version >= 10)
            return;

        Version = 10;
        Save();
    }

    public void Migrate10To11()
    {
        if (Version >= 11)
            return;

        VendorNpcPreferences ??= new();
        LegacyVendorBuyListEntries ??= [];
        Version = 11;
        Save();
    }

    public void Migrate11To12()
    {
        if (Version >= 12)
            return;

        VendorNpcPreferences ??= new();
        VendorRoutePreferences ??= new();
        LegacyVendorBuyListEntries ??= [];
        VendorBuyLists ??= new();
        EnsureVendorBuyListState();
        Version = 12;
        Save();
    }
    public void Migrate12To13()
    {
        if (Version >= 13)
            return;

        EnsureVendorBuyListState();
        Version = 13;
        Save();
    }

    public void Migrate13To14()
    {
        if (Version >= 14)
            return;

        EnsureVendorBuyListState();
        if (LegacyVendorBuyListEntries != null)
            foreach (var entry in LegacyVendorBuyListEntries)
                entry.Enabled = true;
        foreach (var list in VendorBuyLists)
            foreach (var entry in list.Entries)
                entry.Enabled = true;

        Version = 14;
        Save();
    }

    public bool EnsureVendorBuyListState()
    {
        VendorNpcPreferences ??= new();
        VendorRoutePreferences ??= new();
        VendorBuyLists ??= new();
        var legacyVendorBuyListEntries = LegacyVendorBuyListEntries ?? [];

        var changed = false;
        if (VendorBuyLists.Count == 0)
        {
            VendorBuyLists.Add(new VendorBuyListDefinition
            {
                Name = "Default",
                Entries = new List<VendorBuyListEntry>(legacyVendorBuyListEntries),
            });
            changed = true;
        }

        if (LegacyVendorBuyListEntries != null)
        {
            LegacyVendorBuyListEntries = null;
            changed = true;
        }

        if (VendorBuyLists.Count > 0 && (ActiveVendorBuyListId == Guid.Empty || VendorBuyLists.All(list => list.Id != ActiveVendorBuyListId)))
        {
            ActiveVendorBuyListId = VendorBuyLists[0].Id;
            changed = true;
        }

        return changed;
    }
}

public class VulcanMateriaConfig
{
    public bool Enabled { get; set; } = false;
}

public class VulcanRetainerBellConfig
{
    public bool AutoNavigateToRetainerBell { get; set; } = true;
}

public class VulcanRepairConfig
{
    public bool Enabled { get; set; } = true;
    public int RepairThreshold { get; set; } = 10;
    public bool PrioritizeNPCRepair { get; set; } = true;

    [JsonIgnore]
    public RepairNPCData? PreferredRepairNPC { get; set; } = null;

    public uint PreferredRepairNPCDataId { get; set; } = 0;
}
