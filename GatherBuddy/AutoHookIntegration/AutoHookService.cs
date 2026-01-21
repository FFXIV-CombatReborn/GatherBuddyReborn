using System;
using System.Collections.Generic;
using GatherBuddy.AutoGather;
using GatherBuddy.AutoHookIntegration.Models;
using GatherBuddy.Classes;
using GatherBuddy.FishTimer;
using GatherBuddy.Plugin;

namespace GatherBuddy.AutoHookIntegration;

public class AutoHookService
{
    public static bool IsAutoHookAvailable()
    {
        return AutoHook.Enabled;
    }

    public static bool ExportPresetToAutoHook(string presetName, IEnumerable<Fish> fishList, ConfigPreset? gbrPreset = null, bool selectPreset = false)
    {
        if (!IsAutoHookAvailable())
        {
            GatherBuddy.Log.Error("[AutoHook Integration] AutoHook plugin is not available");
            return false;
        }

        try
        {
            var presets = AutoHookPresetBuilder.BuildPresetsFromFish(presetName, fishList, gbrPreset);
            GatherBuddy.Log.Debug($"[AutoHook Integration] Built {presets.Count} preset(s), starting export...");
            
            foreach (var preset in presets)
            {
                var exportString = AutoHookExporter.ExportPreset(preset);
                GatherBuddy.Log.Debug($"[AutoHook Integration] Export string for '{preset.PresetName}' created, length: {exportString?.Length ?? 0}");
                
                AutoHook.ImportAndSelectPreset?.Invoke(exportString);
            }
            
            if (selectPreset)
            {
                var firstPresetName = presets[0].PresetName;
                AutoHook.SetPreset?.Invoke(firstPresetName);
                GatherBuddy.Log.Debug($"[AutoHook Integration] Selected preset '{firstPresetName}'");
            }
            
            if (presets.Count > 1)
            {
                GatherBuddy.Log.Information($"[AutoHook Integration] Successfully exported {presets.Count} presets: '{presets[0].PresetName}' and '{presets[1].PresetName}'");
            }
            else
            {
                GatherBuddy.Log.Information($"[AutoHook Integration] Successfully exported preset '{presets[0].PresetName}' to AutoHook");
            }
            
            return true;
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[AutoHook Integration] Failed to export preset: {ex.Message}");
            if (ex.InnerException != null)
                GatherBuddy.Log.Error($"[AutoHook Integration] Inner exception: {ex.InnerException.Message}");
            GatherBuddy.Log.Error($"[AutoHook Integration] Stack trace: {ex.StackTrace}");
            return false;
        }
    }

    public static bool ExportPresetFromRecords(string presetName, IEnumerable<FishRecord> records)
    {
        if (!IsAutoHookAvailable())
        {
            GatherBuddy.Log.Error("[AutoHook Integration] AutoHook plugin is not available");
            return false;
        }

        try
        {
            var preset = AutoHookPresetBuilder.BuildPresetFromRecords(presetName, records);
            var exportString = AutoHookExporter.ExportPreset(preset);
            
            AutoHook.ImportAndSelectPreset?.Invoke(exportString);
            
            GatherBuddy.Log.Information($"[AutoHook Integration] Successfully exported preset '{presetName}' to AutoHook from records");
            return true;
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[AutoHook Integration] Failed to export preset from records: {ex.Message}");
            return false;
        }
    }

    public static string ExportPresetToClipboard(string presetName, IEnumerable<Fish> fishList, ConfigPreset? gbrPreset = null)
    {
        try
        {
            var preset = AutoHookPresetBuilder.BuildPresetFromFish(presetName, fishList, gbrPreset);
            var exportString = AutoHookExporter.ExportPreset(preset);
            
            GatherBuddy.Log.Information($"[AutoHook Integration] Preset '{presetName}' exported to string");
            return exportString;
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[AutoHook Integration] Failed to create preset string: {ex.Message}");
            return string.Empty;
        }
    }

    public static string ExportPresetFromRecordsToClipboard(string presetName, IEnumerable<FishRecord> records)
    {
        try
        {
            var preset = AutoHookPresetBuilder.BuildPresetFromRecords(presetName, records);
            var exportString = AutoHookExporter.ExportPreset(preset);
            
            GatherBuddy.Log.Information($"[AutoHook Integration] Preset '{presetName}' exported from records to string");
            return exportString;
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[AutoHook Integration] Failed to create preset string from records: {ex.Message}");
            return string.Empty;
        }
    }

    public static bool ExportSpearfishingPresetToAutoHook(string presetName, IEnumerable<Fish> fishList)
    {
        if (!IsAutoHookAvailable())
        {
            GatherBuddy.Log.Error("[AutoHook Integration] AutoHook plugin is not available");
            return false;
        }

        try
        {
            var preset = AutoHookSpearfishingPresetBuilder.BuildSpearfishingPreset(presetName, fishList);
            var exportString = AutoHookSpearfishingExporter.ExportPreset(preset);
            
            AutoHook.ImportAndSelectPreset?.Invoke(exportString);
            
            GatherBuddy.Log.Information($"[AutoHook Integration] Successfully exported spearfishing preset '{presetName}' to AutoHook");
            return true;
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[AutoHook Integration] Failed to export spearfishing preset: {ex.Message}");
            if (ex.InnerException != null)
                GatherBuddy.Log.Error($"[AutoHook Integration] Inner exception: {ex.InnerException.Message}");
            GatherBuddy.Log.Error($"[AutoHook Integration] Stack trace: {ex.StackTrace}");
            return false;
        }
    }
}
