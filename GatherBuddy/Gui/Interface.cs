using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using GatherBuddy.Alarms;
using GatherBuddy.RebornUI;
using GatherBuddy.Time;
using RebornMaterial;
using System;
using System.Collections.Generic;
using System.Numerics;
using Functions = GatherBuddy.Plugin.Functions;

namespace GatherBuddy.Gui;

public partial class Interface : M3ShellWindow, IDisposable
{
    private const string PluginName = "GatherBuddy Reborn";

    private const string SectionBrowse = "Browse";
    private const string SectionPlan = "Plan";
    private const string SectionFishing = "Fishing";
    private const string SectionSettings = "Settings";
    private const string SectionTools = "Tools";

    public const string ItemsPageId = "gatherables";
    public const string FishPageId = "fish";
    public const string AutoGatherPageId = "auto_gather";
    public const string ConfigPresetsPageId = "config_presets";

    private static readonly Vector2 DefaultSize = new(1040f, 720f);
    private static readonly WindowSizeConstraints DefaultConstraints = new()
    {
        MinimumSize = new Vector2(480f, 360f),
        MaximumSize = new Vector2(6000f, 6000f),
    };

    private static readonly M3WindowAction[] MainWindowActions =
    [
        new("##gbr_open_vulcan", FontAwesomeIcon.Hammer, "Open Vulcan, the crafting window"),
        new("##gbr_open_collectables", FontAwesomeIcon.BoxOpen, "Open the collectables window"),
    ];

    private static GatherBuddy _plugin = null!;
    private TimeStamp _earliestKeyboardToggle = TimeStamp.Epoch;
    private readonly GatherBuddySettings _settings;

    private static List<ExtendedFish>? _extendedFishList;

    public static IReadOnlyList<ExtendedFish> ExtendedFishList
        => _extendedFishList ??= CreateExtendedFishList();

    private static List<ExtendedFish> CreateExtendedFishList()
    {
        var extendedFish = new List<ExtendedFish>();
        foreach (var fish in GatherBuddy.GameData.Fishes.Values)
        {
            if (fish.FishingSpots.Count > 0)
            {
                extendedFish.Add(new ExtendedFish(fish));
            }
        }

        return extendedFish;
    }

    public static ExtendedFish? FindExtendedFish(Classes.Fish fish)
    {
        var extendedFishList = ExtendedFishList;
        for (var i = 0; i < extendedFishList.Count; ++i)
        {
            if (extendedFishList[i].Data == fish)
            {
                return extendedFishList[i];
            }
        }

        return null;
    }

    internal static Sounds[] AlarmSoundIds
        => AlarmCache.SoundIds;

    internal static string[] AlarmSoundNames
        => AlarmCache.SoundIdNames;

    public Interface(GatherBuddy plugin)
        : base($"{PluginName}###GatherBuddyMain", "gbr", PluginName, PluginName, DefaultSize, DefaultConstraints)
    {
        BlocksGameInput = false;
        _plugin = plugin;
        _gatherGroupCache = new GatherGroupCache(_plugin.GatherGroupManager);
        _autoGatherListsCache = new AutoGatherListsCache();
        _gatherWindowCache = new GatherWindowCache();
        _locationTable = new LocationTable();
        _alarmCache = new AlarmCache(_plugin.AlarmManager);
        _recordTable = new RecordTable();
        _settings = new GatherBuddySettings(plugin, this);
        IsOpen = GatherBuddy.Config.OpenOnStart;
        BuildPages();
    }

    private void BuildPages()
    {
        AddPage(new M3Page(ItemsPageId, "Gatherables", FontAwesomeIcon.Seedling, SectionBrowse,
            "Every item miners and botanists can gather, with where and when to find it", Panels(DrawItemPage), false,
            tooltip: "Breaking rocks with a pickaxe or felling trees counts as gathering, why do you ask?"));
        AddPage(new M3Page(FishPageId, "Fish", FontAwesomeIcon.Fish, SectionBrowse,
            "Every fish, with its bait, conditions and uptimes", Panels(DrawFishPage), false,
            tooltip: "There are plenty of fish in the sea. And the air. And the sand. And the lava. And space, for some reason."));
        AddPage(new M3Page("weather", "Weather", FontAwesomeIcon.CloudSun, SectionBrowse,
            "The weather forecast in every zone for the coming days, and the last weather", Panels(DrawWeatherPage), false,
            tooltip: "Yes, 'Gloom' is weather."));
        AddPage(new M3Page("locations", "Locations", FontAwesomeIcon.MapMarkedAlt, SectionBrowse,
            "Custom aetherytes and map marker locations for specific nodes", Panels(DrawLocationsPage), false));

        AddPage(new M3Page(AutoGatherPageId, "Auto-Gather", FontAwesomeIcon.Robot, SectionPlan,
            "Lists of items for auto-gather to collect", Panels(DrawAutoGatherPage), false,
            tooltip: "You read that right! Auto-gather!"));
        AddPage(new M3Page("alarms", "Alarms", FontAwesomeIcon.Bell, SectionPlan,
            "Alarms that tell you when nodes and fish come up", Panels(DrawAlarmPage), false,
            tooltip: "Do you often find yourself late for a very important date? Set up your very own alarm clock."));
        AddPage(new M3Page("gather_groups", "Gather Groups", FontAwesomeIcon.LayerGroup, SectionPlan,
            "Gather different things depending on the time of day, and share them with others", Panels(DrawGatherGroupPage), false));
        AddPage(new M3Page("gather_window", "Gather Window", FontAwesomeIcon.ListUl, SectionPlan,
            "Presets for the small window that shows only the items you care about", Panels(DrawGatherWindowPage), false));

        AddPage(new M3Page("fish_records", "Fish Records", FontAwesomeIcon.Book, SectionFishing,
            "Find, clean up and share the fishing data you have collected", Panels(DrawRecordPage), false));
        AddPage(new M3Page("fish_stats", "Fishing Spot Stats", FontAwesomeIcon.ChartBar, SectionFishing,
            "Your fish records summed up per fishing spot", Panels(DrawStatsPage), false, () => GatherBuddy.Config.EnableFishStats));

        foreach (var page in _settings.Pages)
        {
            AddPage(new M3Page(page.Id, page.Label, page.Icon, SectionSettings, page.Description, page.Draw));
        }

        AddPage(new M3Page(ConfigPresetsPageId, "Config Presets", FontAwesomeIcon.SlidersH, SectionSettings,
            "Which actions and consumables auto-gather uses, for which items", DrawConfigPresetsPage, false));

        AddPage(new M3Page("debug", "Debug", FontAwesomeIcon.Bug, SectionTools,
            "Internal data, for troubleshooting", Panels(DrawDebugPage), false, tool: true,
            tooltip: "I really hope there is a good reason for you seeing this."));
    }

    private static Action Panels(Action draw)
        => VulcanUiStyle.Panels(draw);

    protected override M3WindowAction[] WindowActions
        => MainWindowActions;

    protected override ImGuiWindowFlags ExtraFlags
        => (GatherBuddy.Config.MainWindowLockPosition ? ImGuiWindowFlags.NoMove : ImGuiWindowFlags.None)
          | (GatherBuddy.Config.MainWindowLockResize ? ImGuiWindowFlags.NoResize : ImGuiWindowFlags.None);

    protected override void OnWindowAction(int index)
    {
        switch (index)
        {
            case 0:
                if (GatherBuddy.VulcanWindow == null)
                {
                    M3Snackbar.Show("The Vulcan window isn't available.", severity: M3Severity.Error);
                }
                else
                {
                    GatherBuddy.VulcanWindow.RestoreWindow();
                }

                break;
            case 1:
                if (GatherBuddy.CollectablesWindow == null)
                {
                    M3Snackbar.Show("The collectables window isn't available.", severity: M3Severity.Error);
                }
                else
                {
                    GatherBuddy.CollectablesWindow.Open();
                }

                break;
        }
    }

    protected override void BeforeDrawShell()
        => SetupValues();

    protected override void OnPageChanged(M3Page page)
        => FlushConfigPresets();

    public override void OnClose()
    {
        FlushConfigPresets();
        base.OnClose();
    }
    public new void Toggle()
    {
        if (IsOpen && IsMinimized)
        {
            Restore();
            BringToFront();
            return;
        }

        IsOpen = !IsOpen;
    }

    public void Open(string pageId)
    {
        ShowPage(pageId);
        OpenAndRestore();
    }

    public void OpenSettings()
        => Open(GatherBuddySettings.AutoGatherPageId);

    public override void PreOpenCheck()
    {
        if (_earliestKeyboardToggle > GatherBuddy.Time.ServerTime || !Functions.CheckKeyState(GatherBuddy.Config.MainInterfaceHotkey, false))
        {
            return;
        }

        _earliestKeyboardToggle = GatherBuddy.Time.ServerTime.AddMilliseconds(500);
        Toggle();
    }

    #region Search

    protected override bool SupportsSearch
        => true;

    protected override string SearchHint
        => "Search settings";

    protected override void OnSearchChanged(string text)
        => _settings.UpdateSearch(text);

    protected override string SearchSubtitle(string text)
        => _settings.ResultCount == 1 ? "1 matching group of settings" : $"{_settings.ResultCount} matching groups of settings";

    protected override void DrawSearchResults(string text)
    {
        if (_settings.DrawSearch(text) is { } page)
        {
            ShowPage(page.Id);
        }
    }

    #endregion

    public void Dispose()
    {
        FlushConfigPresets();
        _headerCache.Dispose();
        _weatherTable.Dispose();
        _itemTable.Dispose();
        _autoGatherListsCache.Dispose();
        GC.SuppressFinalize(this);
    }
}
