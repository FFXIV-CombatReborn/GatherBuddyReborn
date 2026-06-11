using System;
using System.Collections.Generic;
using System.IO;
using GatherBuddy.Plugin;
using Newtonsoft.Json;

namespace GatherBuddy.Deimos;

public enum DeimosMode
{
    Standard,    // run enabled missions (prefers needed research XP)
    MissionGold, // run enabled missions that aren't golded yet
    Leveling,    // run missions matching the job's level tier (10/50/90)
}

/// <summary>
/// Deimos settings, lives by itself for modularity
/// </summary>
public sealed class DeimosConfig
{
    public const int CurrentVersion = 1;

    private const string FileName = "deimos.json";

    public int Version { get; set; } = CurrentVersion;

    /// mission ids the user wants to run; empty = run everything
    public HashSet<uint> EnabledMissions { get; set; } = new();

    public DeimosMode Mode { get; set; } = DeimosMode.Standard;

    /// skip expert-craft missions when grabbing
    public bool SkipExpertCrafts { get; set; } = false;

    /// crafter jobs to cycle through (in order); empty = just the current job
    public List<uint> JobPriority { get; set; } = new();

    /// also consider provisional / critical / mastery board missions
    public bool IncludeProvisionals { get; set; } = false;
    public bool IncludeCriticals    { get; set; } = false;
    public bool IncludeMastery      { get; set; } = false;

    /// when nothing wanted is on the board, grab+abandon fodder to refresh it
    public bool AutoReroll { get; set; } = false;
    public int  MaxRerolls { get; set; } = 3;

    /// between-mission upkeep
    public bool AutoRepair         { get; set; } = true;
    public int  RepairThreshold    { get; set; } = 30;
    public bool RepairAtVendor     { get; set; } = false;
    public bool AutoExtractMateria { get; set; } = false;

    /// hub gamba wheel
    public bool AutoGamba             { get; set; } = false;
    public int  GambaKeepLunarCredits { get; set; } = 0;

    /// stop-when conditions (0 = off)
    public int StopAfterMissions  { get; set; } = 0;
    public int StopAtLunarCredits { get; set; } = 0;
    public int StopAtCosmoCredits { get; set; } = 0;

    [JsonIgnore] private bool _dirty;

    public void MarkDirty() => _dirty = true;

    public static DeimosConfig Load()
    {
        try
        {
            var file = Functions.ObtainSaveFile(FileName);
            if (file is { Exists: true })
            {
                var loaded = JsonConvert.DeserializeObject<DeimosConfig>(File.ReadAllText(file.FullName));
                if (loaded != null)
                    return loaded;
            }
        }
        catch (Exception ex)
        {
            DeimosLog.Error($"Failed to load {FileName}: {ex.Message}");
        }

        return new DeimosConfig();
    }

    public void Save()
    {
        try
        {
            var file = Functions.ObtainSaveFile(FileName);
            if (file == null)
                return;

            File.WriteAllText(file.FullName, JsonConvert.SerializeObject(this, Formatting.Indented));
            _dirty = false;
        }
        catch (Exception ex)
        {
            DeimosLog.Error($"Failed to save {FileName}: {ex.Message}");
        }
    }

    public void SaveIfDirty()
    {
        if (_dirty)
            Save();
    }
}
