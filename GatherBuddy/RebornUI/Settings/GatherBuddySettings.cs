using GatherBuddy.AutoGather;
using GatherBuddy.Config;
using GatherBuddy.Gui;
using GatherBuddy.RebornUI.Settings;
using RebornMaterial;
using System;
using System.Collections.Generic;

namespace GatherBuddy.RebornUI;

internal sealed partial class GatherBuddySettings
{
    public const string AutoGatherPageId = "settings_autogather";
    public const string GatheringPageId = "settings_gathering";
    public const string InterfacePageId = "settings_interface";

    private readonly GatherBuddy _plugin;
    private readonly Interface _interface;
    private readonly List<M3SettingsSearchResult> _results = [];

    public GatherBuddySettings(GatherBuddy plugin, Interface ui)
    {
        _plugin = plugin;
        _interface = ui;
        AutoGatherPage = BuildAutoGatherPage();
        GatheringPage = BuildGatheringPage();
        InterfacePage = BuildInterfacePage();
        Pages = [AutoGatherPage, GatheringPage, InterfacePage];
    }

    public M3SettingsPage AutoGatherPage { get; }
    public M3SettingsPage GatheringPage { get; }
    public M3SettingsPage InterfacePage { get; }
    public IReadOnlyList<M3SettingsPage> Pages { get; }

    public int ResultCount
        => _results.Count;

    public void UpdateSearch(string text)
        => M3SettingsSearch.Find(Pages, text, _results);

    public M3SettingsPage? DrawSearch(string text)
        => M3SettingsSearch.Draw(_results, text);

    private static Configuration Config
        => GatherBuddy.Config;

    private static AutoGatherConfig AutoCfg
        => GatherBuddy.Config.AutoGatherConfig;

    private static string Keywords(string label, string? supporting, string? keywords)
        => $"{label} {supporting} {keywords}";

    private static M3SettingsEntry Toggle(string label, string? supporting, Func<bool> get, Action<bool> set, string? keywords = null,
        Func<bool>? visible = null)
        => new(Keywords(label, supporting, keywords), () => SettingRows.Switch(label, supporting, get(), set), visible);

    private static M3SettingsEntry PluginToggle(string label, string? supporting, Func<bool> get, Action<bool> set,
        params M3PluginRequirement[] plugins)
    {
        var names = string.Join(' ', Array.ConvertAll(plugins, p => $"{p.InternalName} {p.DisplayName}"));
        return new M3SettingsEntry(Keywords(label, supporting, names), () => SettingRows.PluginSwitch(label, supporting, get(), set, plugins));
    }

    private static M3SettingsEntry Int(string label, string? supporting, Func<int> get, Action<int> set, int min, int max, int step = 1,
        string? unit = null, string? keywords = null)
        => new(Keywords(label, supporting, keywords), () => SettingRows.Number(label, supporting, get(), min, max, set, step, unit));

    private static M3SettingsEntry IntSlider(string label, string? supporting, Func<int> get, Action<int> set, int min, int max,
        string? keywords = null)
        => new(Keywords(label, supporting, keywords), () => SettingRows.Slider(label, supporting, get(), min, max, set));

    private static M3SettingsEntry Float(string label, string? supporting, Func<float> get, Action<float> set, float min, float max, float step,
        string format = "%.1f", string? unit = null, string? keywords = null)
        => new(Keywords(label, supporting, keywords), () => SettingRows.Number(label, supporting, get(), min, max, set, step, format, unit));

    private static M3SettingsEntry Custom(string search, Action draw, Func<bool>? visible = null)
        => new(search, draw, visible);
}
