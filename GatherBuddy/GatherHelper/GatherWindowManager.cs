using Dalamud.Interface.ImGuiNotification;
using GatherBuddy.Alarms;
using GatherBuddy.Interfaces;
using GatherBuddy.Utility;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using Functions = GatherBuddy.Plugin.Functions;

namespace GatherBuddy.GatherHelper;

public partial class GatherWindowManager : IDisposable
{
    public const string FileName = "gather_window.json";

    public List<GatherWindowPreset> Presets = [];

    public List<IGatherable> ActiveItems = [];
    public List<IGatherable> SortedItems = [];

    private readonly AlarmManager _alarms;
    private bool _sortDirty = true;

    public GatherWindowManager(AlarmManager alarms)
    {
        _alarms = alarms;
        GatherBuddy.UptimeManager.UptimeChange += OnUptimeChange;
        SetShowGatherWindowAlarms(GatherBuddy.Config.ShowGatherWindowAlarms);
    }

    public void SetShowGatherWindowAlarms(bool value)
    {
        if (value)
        {
            _alarms.ActiveAlarmsChanged += OnActiveAlarmsChange;
        }
        else
        {
            _alarms.ActiveAlarmsChanged -= OnActiveAlarmsChange;
        }

        SetActiveItems();
    }

    public void Dispose()
        => GatherBuddy.UptimeManager.UptimeChange -= OnUptimeChange;

    private void OnUptimeChange(IGatherable item)
        => _sortDirty = true;

    private void OnActiveAlarmsChange()
        => SetActiveItems();

    public void SetActiveItems()
    {
        ActiveItems.Clear();
        foreach (var preset in Presets)
        {
            if (!preset.Enabled)
            {
                continue;
            }

            foreach (var item in preset.Items)
            {
                if (!ActiveItems.Contains(item))
                {
                    ActiveItems.Add(item);
                }
            }
        }

        if (GatherBuddy.Config.ShowGatherWindowAlarms && GatherBuddy.Config.AlarmsEnabled)
        {
            foreach (var (alarm, _) in _alarms.ActiveAlarms)
            {
                if (!ActiveItems.Contains(alarm.Item))
                {
                    ActiveItems.Add(alarm.Item);
                }
            }
        }

        SortedItems.Clear();
        SortedItems.InsertRange(0, ActiveItems);
        _sortDirty = true;
    }

    public IList<IGatherable> GetList()
    {
        if (!GatherBuddy.Config.SortGatherWindowByUptime)
        {
            return ActiveItems;
        }

        if (_sortDirty)
        {
            SortedItems.StableSort((lhs, rhs)
                => GatherBuddy.UptimeManager.BestLocation(lhs).Interval.Compare(GatherBuddy.UptimeManager.BestLocation(rhs).Interval));
        }

        return SortedItems;
    }

    public void Save()
    {
        var file = Functions.ObtainSaveFile(FileName);
        if (file == null)
        {
            return;
        }

        try
        {
            var text = JsonConvert.SerializeObject(Presets.ConvertAll(p => new GatherWindowPreset.Config(p)), Formatting.Indented);
            File.WriteAllText(file.FullName, text);
        }
        catch (Exception e)
        {
            GatherBuddy.Log.Error($"Error serializing gather window data:\n{e}");
        }
    }

    public static GatherWindowManager Load(AlarmManager alarms)
    {
        var ret = new GatherWindowManager(alarms);
        var file = Functions.ObtainSaveFile(FileName);
        if (file is not { Exists: true })
        {
            ret.Save();
            return ret;
        }

        try
        {
            var text = File.ReadAllText(file.FullName);
            var data = JsonConvert.DeserializeObject<GatherWindowPreset.Config[]>(text)!;
            ret.Presets.Capacity = data.Length;
            var change = false;
            foreach (var cfg in data)
            {
                change |= GatherWindowPreset.FromConfig(cfg, out var preset);
                ret.Presets.Add(preset);
            }

            if (change)
            {
                Dalamud.Notifications.AddNotification(new Notification()
                {
                    Title = "GatherBuddy Error",
                    Content =
                        "Failed to load some gather window presets. See the plugin log for more details. This is not saved, if it keeps happening you need to manually change a preset to cause a save.",
                    MinimizedText = "Failed to load gather window presets.",
                    Type = NotificationType.Error,
                });
            }
        }
        catch (Exception e)
        {
            GatherBuddy.Log.Error($"Error deserializing gather window data:\n{e}");
            ret.Save();
        }

        ret.SetActiveItems();
        return ret;
    }
}
