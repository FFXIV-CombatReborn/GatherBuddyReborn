using System;
using System.IO;
using System.Linq;
using GatherBuddy.Helpers;
using GatherBuddy.AutoGather.Lists;
using GatherBuddy.AutoHookIntegration;
using GatherBuddy.Plugin;
using Newtonsoft.Json.Linq;

namespace GatherBuddy.AutoGather;

public partial class AutoGather
{
    private const string AutoHookGlobalPresetSelectionSentinel = "__GBR_USE_AUTOHOOK_GLOBAL_PRESET__";
    private const string AutoHookGlobalPresetDisplayName = "Global Preset";
    private GatherTarget? _currentAutoHookTarget;
    private string? _currentAutoHookPresetName;
    private string? _currentAutoHookTargetPresetName;
    private bool _isCurrentPresetUserOwned;
    private bool _isUsingAutoHookGlobalPreset;
    private bool? _autoHookPluginStateBeforeAutoGig;

    private void CleanupAutoHookIfNeeded(GatherTarget newTarget)
    {
        if (!GatherBuddy.Config.AutoGatherConfig.UseAutoHook)
            return;

        if (_currentAutoHookTarget == null || _currentAutoHookPresetName == null)
            return;

        if (_currentAutoHookTarget.Value.FishingSpot?.Id != newTarget.FishingSpot?.Id)
        {
            CleanupAutoHook();
        }
    }

    private void SetupAutoHookForFishing(GatherTarget target)
    {
        if (!GatherBuddy.Config.AutoGatherConfig.UseAutoHook)
            return;

        if (!AutoHook.Enabled)
        {
            GatherBuddy.Log.Debug("[AutoGather] AutoHook not available, skipping preset generation");
            return;
        }

        if (target.Fish == null)
            return;

        CleanupAutoHookIfNeeded(target);

        var shouldUseGlobalAutoHookPreset = GatherBuddy.Config.AutoGatherConfig.UseAutoHookGlobalPreset && !target.Fish.IsSpearFish;
        if (_currentAutoHookTarget?.Fish?.ItemId == target.Fish.ItemId
            && _currentAutoHookPresetName != null
            && _isUsingAutoHookGlobalPreset != shouldUseGlobalAutoHookPreset)
        {
            GatherBuddy.Log.Debug("[AutoGather] AutoHook preset mode changed, resetting current AutoHook state.");
            CleanupAutoHook();
        }

        if (_currentAutoHookTarget?.Fish?.ItemId == target.Fish.ItemId 
            && _currentAutoHookPresetName != null
            && _isUsingAutoHookGlobalPreset == shouldUseGlobalAutoHookPreset)
        {
            if (_isUsingAutoHookGlobalPreset)
                AutoHook.SetPreset?.Invoke(AutoHookGlobalPresetSelectionSentinel);
            if (target.Fish.IsSpearFish)
            {
                if (!EnableAutoGig())
                    return;
            }
            else
            {
                AutoHook.SetPluginState?.Invoke(true);
                AutoHook.SetAutoStartFishing?.Invoke(true); 
            }
            GatherBuddy.Log.Verbose(
                $"[AutoGather] Re-enabled existing AutoHook preset '{(_isUsingAutoHookGlobalPreset ? AutoHookGlobalPresetDisplayName : _currentAutoHookPresetName)}'");
            return;
        }

        try
        {
            if (shouldUseGlobalAutoHookPreset)
            {
                GatherBuddy.Log.Information(
                    $"[AutoGather] Using AutoHook global preset for {target.Fish.Name[GatherBuddy.Language]}.");
                AutoHook.SetPreset?.Invoke(AutoHookGlobalPresetSelectionSentinel);

                _currentAutoHookTarget = target;
                _currentAutoHookPresetName = AutoHookGlobalPresetDisplayName;
                _currentAutoHookTargetPresetName = null;
                _isCurrentPresetUserOwned = false;
                _isUsingAutoHookGlobalPreset = true;

                if (AutoHook.SetPluginState == null)
                {
                    GatherBuddy.Log.Error("[AutoGather] SetPluginState IPC is null!");
                    return;
                }

                AutoHook.SetPluginState.Invoke(true);
                AutoHook.SetAutoStartFishing?.Invoke(true);
                _autoHookSetupComplete = true;
                GatherBuddy.Log.Information("[AutoGather] AutoHook enabled with global preset for fishing");
                return;
            }
            // Check if the target fish is an intuition fish
            bool isIntuitionFish = target.Fish.Predators.Length > 0 && target.Fish.Predators.All(p => !p.Item1.IsSpearFish);
            
            // For intuition fish, always use target fish (two-preset system handles predators)
            // For non-intuition fish with predators, check if we should use predator instead
            var presetFish = target.Fish;
            if (!isIntuitionFish && target.Fish.Predators.Any())
            {
                var firstPredator = target.Fish.Predators.First().Item1;
                if (target.FishingSpot?.IsShadowNode != true)
                {
                    presetFish = firstPredator;
                    GatherBuddy.Log.Debug($"[AutoGather] Target fish {target.Fish.Name[GatherBuddy.Language]} is at its parent node, using prerequisite fish {presetFish.Name[GatherBuddy.Language]} for preset");
                }
                else
                {
                    GatherBuddy.Log.Debug($"[AutoGather] Target fish {target.Fish.Name[GatherBuddy.Language]} is at its shadow node, using target fish for preset");
                }
            }
            
            var fishName = presetFish.Name[GatherBuddy.Language];
            var fishId = presetFish.ItemId;
            string? presetName = null;
            bool isUserPreset = false;

            if (GatherBuddy.Config.AutoGatherConfig.UseExistingAutoHookPresets)
            {
                string? userPresetName = null;
                if (isIntuitionFish)
                {
                    // For intuition fish, look for presets named after target fish ID
                    var targetFishId = target.Fish.ItemId;
                    userPresetName = FindAutoHookPreset($"{targetFishId}_Predators");
                    if (userPresetName == null)
                    {
                        userPresetName = FindAutoHookPreset(targetFishId.ToString());
                    }
                }
                else
                {
                    userPresetName = FindAutoHookPreset(fishId.ToString());
                }
                
                if (userPresetName != null)
                {
                    presetName = userPresetName;
                    isUserPreset = true;

                    GatherBuddy.Log.Information($"[AutoGather] Found user preset '{presetName}' for {fishName}");
                    AutoHook.SetPreset?.Invoke(presetName);
                }
                else
                {
                    GatherBuddy.Log.Debug($"[AutoGather] No user preset found for fish ID {fishId}, will generate one");
                }
            }

            if (presetName == null)
            {
                // Build fish list: if at shadow node with multiple predators, include them all
                var fishList = new[] { presetFish };
                if (presetFish == target.Fish && target.Fish.Predators.Count() > 1 && target.FishingSpot?.IsShadowNode == true)
                {
                    // At shadow node targeting fish with multiple predators - include all predators (skip first) + target
                    var predatorFish = target.Fish.Predators.Skip(1).Select(p => p.Item1).ToList();
                    fishList = predatorFish.Append(target.Fish).ToArray();
                    GatherBuddy.Log.Debug($"[AutoGather] Building preset with {fishList.Length} fish for multi-predator chain: {string.Join(", ", fishList.Select(f => f.Name[GatherBuddy.Language]))}");
                }
                
                presetName = $"GBR_{fishName.Replace(" ", "")}_{DateTime.Now:HHmmss}";

                // For intuition fish generator will generate _Predators and _Target presets
                if (isIntuitionFish)
                {
                    GatherBuddy.Log.Information($"[AutoGather] Creating intuition presets for {fishName}");
                    presetName = presetName + "_Predators";
                }
                else
                {
                    GatherBuddy.Log.Information($"[AutoGather] Creating AutoHook preset '{presetName}' for {fishName}");
                }
                
                bool success;
                if (presetFish.IsSpearFish)
                {
                    success = AutoHookService.ExportSpearfishingPresetToAutoHook(presetName.Replace("_Predators", ""), fishList);
                }
                else
                {
                    var gbrPreset = MatchConfigPreset(presetFish);
                    success = AutoHookService.ExportPresetToAutoHook(presetName.Replace("_Predators", ""), fishList, gbrPreset, selectPreset: true);
                }
                
                if (!success)
                {
                    GatherBuddy.Log.Error($"[AutoGather] Failed to create AutoHook preset");
                    return;
                }
            }

            _currentAutoHookTarget = target;
            _currentAutoHookPresetName = presetName;
            _isCurrentPresetUserOwned = isUserPreset;
            _isUsingAutoHookGlobalPreset = false;
            
            if (isIntuitionFish && !isUserPreset)
            {
                var baseName = presetName.Replace("_Predators", "");
                _currentAutoHookTargetPresetName = baseName + "_Target";
            }
            else
            {
                _currentAutoHookTargetPresetName = null;
            }
            
            if (target.Fish.IsSpearFish)
            {
                if (EnableAutoGig())
                {
                    GatherBuddy.Log.Information("[AutoGather] AutoHook and AutoGig enabled for spearfishing");
                }
            }
            else
            {
                if (AutoHook.SetPluginState == null)
                {
                    GatherBuddy.Log.Error("[AutoGather] SetPluginState IPC is null!");
                }
                else
                {
                    AutoHook.SetPluginState.Invoke(true);
                    AutoHook.SetAutoStartFishing?.Invoke(true);
                    _autoHookSetupComplete = true;
                    GatherBuddy.Log.Information("[AutoGather] AutoHook enabled for fishing");
                }
            }
            
            var presetType = isUserPreset ? "user" : "generated";
            GatherBuddy.Log.Information($"[AutoGather] AutoHook preset '{presetName}' ({presetType}) for {fishName} selected and activated successfully");
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[AutoGather] Exception setting up AutoHook: {ex.Message}");
        }
    }

    private bool EnableAutoGig()
    {
        if (AutoHook.SetPluginState == null)
        {
            GatherBuddy.Log.Error("[AutoGather] SetPluginState IPC is null!");
            return false;
        }

        if (AutoHook.SetAutoGigState == null)
        {
            GatherBuddy.Log.Error("[AutoGather] SetAutoGigState IPC is null!");
            return false;
        }

        _autoHookPluginStateBeforeAutoGig ??= AutoHook.GetPluginState?.Invoke() ?? false;
        AutoHook.SetPluginState.Invoke(true);
        AutoHook.SetAutoGigState.Invoke(true);
        _autoHookSetupComplete = true;
        return true;
    }

    private void CleanupAutoHook()
    {
        if (!AutoHook.Enabled)
            return;

        try
        {
            if (_currentAutoHookPresetName != null)
            {
                if (_isUsingAutoHookGlobalPreset)
                {
                    GatherBuddy.Log.Debug("[AutoGather] Leaving AutoHook on its global preset.");
                }
                else if (_isCurrentPresetUserOwned)
                {
                    GatherBuddy.Log.Debug($"[AutoGather] Preserving user-owned preset '{_currentAutoHookPresetName}'");
                }
                else
                {
                    if (_currentAutoHookTarget.HasValue && _currentAutoHookTarget.Value.Fish?.IsSpearFish == true)
                    {
                        GatherBuddy.Log.Warning(
                            $"[AutoGather] Generated AutoGig preset '{_currentAutoHookPresetName}' remains in AutoHook because AutoHook does not expose AutoGig preset deletion via IPC; remove it manually from AutoHook's AutoGig presets.");
                    }
                    else
                    {
                        AutoHook.SetPreset?.Invoke(_currentAutoHookPresetName);
                        AutoHook.DeleteSelectedPreset?.Invoke();
                        GatherBuddy.Log.Debug($"[AutoGather] Deleted GBR-generated preset '{_currentAutoHookPresetName}'");

                        if (_currentAutoHookTargetPresetName != null)
                        {
                            AutoHook.SetPreset?.Invoke(_currentAutoHookTargetPresetName);
                            AutoHook.DeleteSelectedPreset?.Invoke();
                            GatherBuddy.Log.Debug($"[AutoGather] Deleted GBR-generated preset '{_currentAutoHookTargetPresetName}'");
                        }
                    }
                }
            }
            
            AutoHook.SetAutoStartFishing?.Invoke(false);
            AutoHook.SetAutoGigState?.Invoke(false);
            AutoHook.SetPluginState?.Invoke(_autoHookPluginStateBeforeAutoGig ?? false);
            GatherBuddy.Log.Debug("[AutoGather] AutoHook/AutoGig cleanup complete");
            
            _currentAutoHookTarget = null;
            _currentAutoHookPresetName = null;
            _currentAutoHookTargetPresetName = null;
            _isCurrentPresetUserOwned = false;
            _isUsingAutoHookGlobalPreset = false;
            _autoHookPluginStateBeforeAutoGig = null;
            _autoHookSetupComplete = false;
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[AutoGather] Exception cleaning up AutoHook: {ex.Message}");
        }
    }

    private string? FindAutoHookPreset(string fishId)
    {
        try
        {
            // Resolve AutoHook config path: .../pluginConfigs/AutoHook.json
            var pluginConfigsDir = Dalamud.PluginInterface.ConfigDirectory.Parent?.FullName;
            if (string.IsNullOrEmpty(pluginConfigsDir))
                return null;

            var configPath = Path.Combine(pluginConfigsDir, "AutoHook.json");
            if (!File.Exists(configPath))
            {
                GatherBuddy.Log.Debug($"[AutoGather] AutoHook config not found at {configPath}");
                return null;
            }

            var json = File.ReadAllText(configPath);
            var config = JObject.Parse(json);

            var customPresets = config["HookPresets"]?["CustomPresets"] as JArray;
            if (customPresets == null)
            {
                GatherBuddy.Log.Debug("[AutoGather] No CustomPresets found in AutoHook config");
                return null;
            }

            foreach (var preset in customPresets)
            {
                var presetName = preset?["PresetName"]?.ToString();
                if (presetName != null && presetName.Equals(fishId, StringComparison.Ordinal))
                {
                    GatherBuddy.Log.Debug($"[AutoGather] Found matching preset in config: {presetName}");
                    return presetName;
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[AutoGather] Error reading AutoHook config: {ex.Message}");
            return null;
        }
    }
}
