using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using GatherBuddy.AutoHookIntegration.Models;

namespace GatherBuddy.AutoHookIntegration;

public class BiteTimerService
{
    private readonly Dictionary<uint, BiteTimerData> _biteTimers = new();
    private bool _isLoaded;

    public BiteTimerService(string pluginConfigDirectory)
    {
        LoadBiteTimers(pluginConfigDirectory);
    }

    private void LoadBiteTimers(string pluginConfigDirectory)
    {
        try
        {
            var biteTimersPath = FindAutoHookBiteTimersFile(pluginConfigDirectory);
            if (biteTimersPath == null)
            {
                GatherBuddy.Log.Warning("[BiteTimerService] AutoHook bitetimers.json not found. Bite timers will not be available for preset generation.");
                return;
            }


            var json = File.ReadAllText(biteTimersPath);
            var biteTimersArray = JsonSerializer.Deserialize<List<BiteTimerData>>(json);

            if (biteTimersArray == null)
            {
                GatherBuddy.Log.Error("[BiteTimerService] Failed to deserialize bitetimers.json");
                return;
            }

            foreach (var biteTimer in biteTimersArray)
            {
                _biteTimers[biteTimer.ItemId] = biteTimer;
            }

            _isLoaded = true;
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[BiteTimerService] Error loading bite timers: {ex.Message}");
        }
    }

    private string? FindAutoHookBiteTimersFile(string pluginConfigDirectory)
    {
        try
        {
            var configDir = new DirectoryInfo(pluginConfigDirectory);
            var xivLauncherRoot = configDir.Parent?.Parent;
            
            if (xivLauncherRoot == null || !xivLauncherRoot.Exists)
            {
                GatherBuddy.Log.Debug("[BiteTimerService] Could not find XIVLauncher root directory");
                return null;
            }

            var installedPluginsDir = Path.Combine(xivLauncherRoot.FullName, "installedPlugins", "AutoHook");
            
            if (!Directory.Exists(installedPluginsDir))
            {
                GatherBuddy.Log.Debug("[BiteTimerService] AutoHook plugin directory not found");
                return null;
            }

            var versionDirs = Directory.GetDirectories(installedPluginsDir)
                .OrderByDescending(d => d)
                .ToArray();

            foreach (var versionDir in versionDirs)
            {
                var biteTimersPath = Path.Combine(versionDir, "Data", "FishData", "bitetimers.json");
                
                if (File.Exists(biteTimersPath))
                {
                    return biteTimersPath;
                }
            }

            GatherBuddy.Log.Debug("[BiteTimerService] bitetimers.json not found in any AutoHook version directory");
            return null;
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[BiteTimerService] Error finding AutoHook directory: {ex.Message}");
            return null;
        }
    }

    public BiteTimerData? GetBiteTimers(uint fishItemId)
    {
        if (!_isLoaded)
            return null;

        return _biteTimers.GetValueOrDefault(fishItemId);
    }

    public bool IsLoaded => _isLoaded;
}
