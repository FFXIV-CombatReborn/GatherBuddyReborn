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
    private GatherTarget? _currentAutoHookTarget;
    private string? _currentAutoHookPresetName;
    private bool _isCurrentPresetUserOwned;

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

        if (_currentAutoHookTarget?.Fish?.ItemId == target.Fish.ItemId 
            && _currentAutoHookPresetName != null)
        {
            if (target.Fish.IsSpearFish)
            {
                AutoHook.SetAutoGigState?.Invoke(true);
            }
            else
            {
                AutoHook.SetPluginState?.Invoke(true);
                AutoHook.SetAutoStartFishing?.Invoke(true); 
            }
            GatherBuddy.Log.Verbose($"[AutoGather] Re-enabled existing AutoHook preset '{_currentAutoHookPresetName}'");
            return;
        }

        try
        {
            // Check if the target fish is an intuition fish
            bool isIntuitionFish = target.Fish.Predators.Length > 0 && target.Fish.Predators.All(p => !p.Item1.IsSpearFish);
            
            // For intuition fish, always use target fish (two-preset system handles predators)
            // For non-intuition fish with predators, check if we should use predator instead
            var presetFish = target.Fish;
            if (!isIntuitionFish && target.Fish != null && target.Fish.Predators.Any())
            {
                // Only check FIRST predator for shadow node spawning (rest are caught within shadow node)
                var (firstPredator, requiredCount) = target.Fish.Predators.First();
                var caughtCount = SpearfishingSessionCatches.TryGetValue(firstPredator.ItemId, out var count) ? count : 0;
                var firstPredatorMet = caughtCount >= requiredCount;
                
                if (!firstPredatorMet)
                {
                    // Use first predator fish as preset
                    presetFish = firstPredator;
                    GatherBuddy.Log.Debug($"[AutoGather] Target fish {target.Fish.Name[GatherBuddy.Language]} first predator not met, using prerequisite fish {presetFish.Name[GatherBuddy.Language]} for preset");
                }
                else
                {
                    GatherBuddy.Log.Debug($"[AutoGather] Target fish {target.Fish.Name[GatherBuddy.Language]} first predator met, using target fish for preset");
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
            
            if (target.Fish.IsSpearFish)
            {
                if (AutoHook.SetAutoGigState == null)
                {
                    GatherBuddy.Log.Error("[AutoGather] SetAutoGigState IPC is null!");
                }
                else
                {
                    AutoHook.SetAutoGigState.Invoke(true);
                    _autoHookSetupComplete = true;
                    GatherBuddy.Log.Information("[AutoGather] Called SetAutoGigState(true) via IPC");
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

    private void CleanupAutoHook()
    {
        if (!AutoHook.Enabled)
            return;

        try
        {
            if (_currentAutoHookPresetName != null)
            {
                if (_isCurrentPresetUserOwned)
                {
                    GatherBuddy.Log.Debug($"[AutoGather] Preserving user-owned preset '{_currentAutoHookPresetName}'");
                }
                else
                {
                    AutoHook.SetPreset?.Invoke(_currentAutoHookPresetName);
                    AutoHook.DeleteSelectedPreset?.Invoke();
                    GatherBuddy.Log.Debug($"[AutoGather] Deleted GBR-generated preset '{_currentAutoHookPresetName}'");
                }
            }
            
            AutoHook.SetPluginState?.Invoke(false);
            AutoHook.SetAutoStartFishing?.Invoke(false);
            AutoHook.SetAutoGigState?.Invoke(false);
            GatherBuddy.Log.Debug("[AutoGather] AutoHook/AutoGig disabled");
            
            if (_currentAutoHookTarget.HasValue && _currentAutoHookTarget.Value.Fish?.IsSpearFish == true)
            {
                GatherBuddy.Log.Debug("[AutoGather] Calling UpdateSpearfishingCatches from CleanupAutoHook");
                UpdateSpearfishingCatches();
            }
            
            _currentAutoHookTarget = null;
            _currentAutoHookPresetName = null;
            _isCurrentPresetUserOwned = false;
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
