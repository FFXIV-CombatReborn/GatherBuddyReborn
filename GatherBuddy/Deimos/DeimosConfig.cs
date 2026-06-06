using System;
using System.Collections.Generic;
using System.IO;
using GatherBuddy.Plugin;
using Newtonsoft.Json;

namespace GatherBuddy.Deimos;

public enum DeimosMode
{
    Standard,    // run enabled missions
    MissionGold, // run enabled missions that aren't golded yet
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
