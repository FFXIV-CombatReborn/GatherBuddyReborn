using System;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;
using GatherBuddy.AutoGather;
using GatherBuddy.AutoHookIntegration.Models;
using GatherBuddy.Classes;
using GatherBuddy.Enums;
using GatherBuddy.FishTimer;
using GatherBuddy.Models;

namespace GatherBuddy.AutoHookIntegration;

public class AutoHookPresetBuilder
{
    private const uint VersatileLureId = 29717;
    private const uint AmbitiousLureId = 37594;
    private const uint ModestLureId = 37595;
    
    private static unsafe int GetInventoryItemCount(uint itemRowId)
    {
        return InventoryManager.Instance()->GetInventoryItemCount(itemRowId < 100000 ? itemRowId : itemRowId - 100000, itemRowId >= 100000);
    }
    private static HashSet<Fish> CollectAllFishInMoochChains(Fish[] fishList)
    {
        var allFish = new HashSet<Fish>();
        
        foreach (var fish in fishList)
        {
            allFish.Add(fish);
            
            // Add all fish in the mooch chain
            if (fish.Mooches.Length > 0)
            {
                foreach (var moochFish in fish.Mooches)
                {
                    allFish.Add(moochFish);
                }
            }
            
            // Add predator fish for Fisher's Intuition (skip spearfish predators)
            if (fish.Predators.Length > 0)
            {
                foreach (var (predatorFish, _) in fish.Predators)
                {
                    if (predatorFish.IsSpearFish)
                    {
                        GatherBuddy.Log.Warning($"[AutoHook] Skipping spearfish predator {predatorFish.Name[GatherBuddy.Language]} for {fish.Name[GatherBuddy.Language]}");
                        continue;
                    }
                    
                    GatherBuddy.Log.Debug($"[AutoHook] Adding predator fish {predatorFish.Name[GatherBuddy.Language]} for {fish.Name[GatherBuddy.Language]}");
                    allFish.Add(predatorFish);
                    
                    // Also include predator's mooch chain so we can actually catch them
                    if (predatorFish.Mooches.Length > 0)
                    {
                        foreach (var moochFish in predatorFish.Mooches)
                        {
                            allFish.Add(moochFish);
                        }
                    }
                }
            }
        }
        
        return allFish;
    }
    
    private static HashSet<Fish> CollectFishInMoochChainsOnly(Fish[] fishList)
    {
        var allFish = new HashSet<Fish>();
        
        foreach (var fish in fishList)
        {
            allFish.Add(fish);

            if (fish.Mooches.Length > 0)
            {
                foreach (var moochFish in fish.Mooches)
                {
                    allFish.Add(moochFish);
                }
            }
        }
        
        return allFish;
    }
    
    private static HashSet<Fish> CollectFishFromSameFishingSpots(Fish[] targetFish, HashSet<Fish> existingFish)
    {
        var additionalFish = new HashSet<Fish>();
        
        var targetBiteTypes = targetFish
            .Select(f => f.Mooches.Length > 0 ? f.Mooches[0].BiteType : f.BiteType)
            .Where(bt => bt != BiteType.Unknown && bt != BiteType.None)
            .Distinct()
            .ToList();
        
        if (targetBiteTypes.Count == 0)
        {
            GatherBuddy.Log.Debug($"[AutoHook] No valid target bite types found, skipping Surface Slap fish collection");
            return additionalFish;
        }
        
        var fishingSpots = targetFish.SelectMany(f => f.FishingSpots).Distinct().ToList();
        
        GatherBuddy.Log.Debug($"[AutoHook] Collecting fish from {fishingSpots.Count} fishing spots for Surface Slap (target bite types: {string.Join(", ", targetBiteTypes)})");
        
        foreach (var spot in fishingSpots)
        {
            var fishAtSpot = GatherBuddy.GameData.Fishes.Values
                .Where(f => f.FishingSpots.Contains(spot) && !f.IsSpearFish)
                .ToList();
            
            GatherBuddy.Log.Debug($"[AutoHook] Found {fishAtSpot.Count} fish at {spot.Name}");
            
            foreach (var fish in fishAtSpot)
            {
                if (existingFish.Contains(fish))
                    continue;
                
                if (targetBiteTypes.Contains(fish.BiteType))
                    additionalFish.Add(fish);
            }
        }
        
        return additionalFish;
    }
    
    public static List<AHCustomPresetConfig> BuildPresetsFromFish(string presetName, IEnumerable<Fish> fishList, ConfigPreset? gbrPreset = null)
    {
        var fishArray = fishList.ToArray();
        var presets = new List<AHCustomPresetConfig>();
        
        var hasIntuitionFish = fishArray.Any(f => f.Predators.Length > 0 && f.Predators.All(p => !p.Item1.IsSpearFish));
        
        if (hasIntuitionFish)
        {
            GatherBuddy.Log.Information($"[AutoHook] Detected intuition fish, generating two presets for {presetName}");
            
            var predatorPresetName = $"{presetName}_Predators";
            var targetPresetName = $"{presetName}_Target";
            
            var predatorPreset = BuildPredatorPreset(predatorPresetName, targetPresetName, fishArray, gbrPreset);
            presets.Add(predatorPreset);
            
            var targetPreset = BuildTargetPreset(targetPresetName, predatorPresetName, fishArray, gbrPreset);
            presets.Add(targetPreset);
        }
        else
        {
            var preset = BuildSinglePreset(presetName, fishArray, gbrPreset);
            presets.Add(preset);
        }
        
        return presets;
    }
    
    public static AHCustomPresetConfig BuildPresetFromFish(string presetName, IEnumerable<Fish> fishList, ConfigPreset? gbrPreset = null)
    {
        var presets = BuildPresetsFromFish(presetName, fishList, gbrPreset);
        return presets[0];
    }
    
    private static AHCustomPresetConfig BuildPredatorPreset(string presetName, string targetPresetName, Fish[] targetFish, ConfigPreset? gbrPreset)
    {
        var preset = new AHCustomPresetConfig(presetName);
        
        var predators = new HashSet<Fish>();
        foreach (var fish in targetFish)
        {
            if (fish.Predators.Length == 0)
                continue;
                
            foreach (var (predatorFish, _) in fish.Predators)
            {
                if (predatorFish.IsSpearFish)
                    continue;
                    
                predators.Add(predatorFish);
                
                if (predatorFish.Mooches.Length > 0)
                {
                    foreach (var moochFish in predatorFish.Mooches)
                    {
                        predators.Add(moochFish);
                    }
                }
            }
        }
        
        GatherBuddy.Log.Debug($"[AutoHook] Predator preset '{presetName}': {predators.Count} fish total");
        
        var fishWithBait = predators.Where(f => f.Mooches.Length == 0).ToList();
        var fishWithMooch = predators.Where(f => f.Mooches.Length > 0).ToList();
        
        uint? actualBaitId = null;
        
        var baitGroups = fishWithBait.GroupBy(f => f.InitialBait.Id);
        foreach (var group in baitGroups)
        {
            var baitId = group.Key;
            if (baitId == 0) continue;

            var effectiveBaitId = baitId;
            if (GetInventoryItemCount(baitId) == 0)
            {
                GatherBuddy.Log.Warning($"[AutoHook] User does not have bait {baitId} in inventory, using Versatile Lure ({VersatileLureId}) instead");
                effectiveBaitId = VersatileLureId;
            }

            if (actualBaitId == null)
                actualBaitId = effectiveBaitId;

            var hookConfig = new AHHookConfig((int)effectiveBaitId);
            
            foreach (var fish in group)
            {
                ConfigureHookForFish(hookConfig, fish);
            }
            
            preset.ListOfBaits.Add(hookConfig);
        }
        
        var moochGroups = fishWithMooch.GroupBy(f => f.Mooches[^1].ItemId);
        foreach (var group in moochGroups)
        {
            var moochFishId = group.Key;
            var hookConfig = new AHHookConfig((int)moochFishId);
            
            foreach (var fish in group)
            {
                ConfigureHookForFish(hookConfig, fish, configureLures: false);
            }
            
            preset.ListOfMooch.Add(hookConfig);
        }
        
        var finalPredatorFish = new HashSet<Fish>();
        foreach (var target in targetFish)
        {
            if (target.Predators.Length == 0)
                continue;
            foreach (var (predatorFish, _) in target.Predators)
            {
                if (!predatorFish.IsSpearFish)
                    finalPredatorFish.Add(predatorFish);
            }
        }
        
        foreach (var fish in predators)
        {
            AddFishConfig(preset, fish, finalPredatorFish.ToArray(), predators);
        }

        ConfigureExtraCfg(preset, actualBaitId);
        
        preset.ExtraCfg.SwapPresetIntuitionGain = true;
        preset.ExtraCfg.PresetToSwapIntuitionGain = targetPresetName;
        
        ConfigureAutoCasts(preset, predators.ToArray(), gbrPreset);
        
        return preset;
    }
    
    private static AHCustomPresetConfig BuildTargetPreset(string presetName, string predatorPresetName, Fish[] targetFish, ConfigPreset? gbrPreset)
    {
        var preset = new AHCustomPresetConfig(presetName);
        
        var allFishWithMooches = CollectFishInMoochChainsOnly(targetFish);
        
        if (GatherBuddy.Config.AutoGatherConfig.EnableSurfaceSlap)
        {
            var additionalFish = CollectFishFromSameFishingSpots(targetFish, allFishWithMooches);
            foreach (var fish in additionalFish)
            {
                allFishWithMooches.Add(fish);
            }
        }
        
        var moochChainFish = CollectFishInMoochChainsOnly(targetFish);
        var fishWithBait = moochChainFish.Where(f => f.Mooches.Length == 0).ToList();
        var fishWithMooch = moochChainFish.Where(f => f.Mooches.Length > 0).ToList();
        
        uint? actualBaitId = null;
        
        var baitGroups = fishWithBait.GroupBy(f => f.InitialBait.Id);
        foreach (var group in baitGroups)
        {
            var baitId = group.Key;
            if (baitId == 0) continue;

            var effectiveBaitId = baitId;
            if (GetInventoryItemCount(baitId) == 0)
            {
                GatherBuddy.Log.Warning($"[AutoHook] User does not have bait {baitId} in inventory, using Versatile Lure ({VersatileLureId}) instead");
                effectiveBaitId = VersatileLureId;
            }

            if (actualBaitId == null)
                actualBaitId = effectiveBaitId;

            var hookConfig = new AHHookConfig((int)effectiveBaitId);
            
            foreach (var fish in group)
            {
                ConfigureHookForFish(hookConfig, fish);
            }
            
            preset.ListOfBaits.Add(hookConfig);
        }
        
        var moochGroups = fishWithMooch.GroupBy(f => f.Mooches[^1].ItemId);
        foreach (var group in moochGroups)
        {
            var moochFishId = group.Key;
            var hookConfig = new AHHookConfig((int)moochFishId);
            
            foreach (var fish in group)
            {
                ConfigureHookForFish(hookConfig, fish, configureLures: false);
            }
            
            preset.ListOfMooch.Add(hookConfig);
        }
        
        foreach (var fish in allFishWithMooches)
        {
            AddFishConfig(preset, fish, targetFish, allFishWithMooches);
        }

        ConfigureExtraCfg(preset, actualBaitId);
        
        preset.ExtraCfg.SwapPresetIntuitionLost = true;
        preset.ExtraCfg.PresetToSwapIntuitionLost = predatorPresetName;
        
        ConfigureAutoCasts(preset, targetFish, gbrPreset);
        
        return preset;
    }
    
    private static AHCustomPresetConfig BuildSinglePreset(string presetName, Fish[] fishArray, ConfigPreset? gbrPreset)
    {
        var preset = new AHCustomPresetConfig(presetName);
        
        var allFishWithMooches = CollectAllFishInMoochChains(fishArray);
        
        if (GatherBuddy.Config.AutoGatherConfig.EnableSurfaceSlap)
        {
            var additionalFish = CollectFishFromSameFishingSpots(fishArray, allFishWithMooches);
            foreach (var fish in additionalFish)
            {
                allFishWithMooches.Add(fish);
            }
        }
        
        var moochChainFish = CollectAllFishInMoochChains(fishArray);
        var surfaceSlapFish = allFishWithMooches.Where(f => !moochChainFish.Contains(f)).ToList();
        var fishWithBait = moochChainFish.Where(f => f.Mooches.Length == 0).ToList();
        var fishWithMooch = moochChainFish.Where(f => f.Mooches.Length > 0).ToList();
        
        uint? actualBaitId = null;
        
        var baitGroups = fishWithBait.GroupBy(f => f.InitialBait.Id);
        foreach (var group in baitGroups)
        {
            var baitId = group.Key;
            if (baitId == 0) continue;

            var effectiveBaitId = baitId;
            if (GetInventoryItemCount(baitId) == 0)
            {
                GatherBuddy.Log.Warning($"[AutoHook] User does not have bait {baitId} in inventory, using Versatile Lure ({VersatileLureId}) instead");
                effectiveBaitId = VersatileLureId;
            }

            if (actualBaitId == null)
                actualBaitId = effectiveBaitId;

            var hookConfig = new AHHookConfig((int)effectiveBaitId);
            
            foreach (var fish in group)
            {
                ConfigureHookForFish(hookConfig, fish);
            }
            
            preset.ListOfBaits.Add(hookConfig);
        }
        
        var moochGroups = fishWithMooch.GroupBy(f => f.Mooches[^1].ItemId);
        foreach (var group in moochGroups)
        {
            var moochFishId = group.Key;
            var hookConfig = new AHHookConfig((int)moochFishId);
            
            foreach (var fish in group)
            {
                ConfigureHookForFish(hookConfig, fish, configureLures: false);
            }
            
            preset.ListOfMooch.Add(hookConfig);
        }
        
        // Add all fish configs
        foreach (var fish in allFishWithMooches)
        {
            AddFishConfig(preset, fish, fishArray, allFishWithMooches);
        }

        ConfigureExtraCfg(preset, actualBaitId);
        ConfigureAutoCasts(preset, fishArray, gbrPreset);
        
        return preset;
    }

    public static AHCustomPresetConfig BuildPresetFromRecords(string presetName, IEnumerable<FishRecord> records)
    {
        var preset = new AHCustomPresetConfig(presetName);
        
        var baitGroups = records
            .Where(r => r.HasBait && r.HasCatch)
            .GroupBy(r => r.BaitId);
        
        foreach (var group in baitGroups)
        {
            var baitId = (int)group.Key;
            var hookConfig = new AHHookConfig(baitId);
            
            foreach (var record in group)
            {
                ConfigureHookFromRecord(hookConfig, record);
            }
            
            preset.ListOfBaits.Add(hookConfig);
        }
        
        return preset;
    }

    private static void ConfigureHookForFish(AHHookConfig hookConfig, Fish fish, bool configureLures = true)
    {
        var ahBiteType = ConvertBiteType(fish.BiteType);
        var ahHookType = ConvertHookSet(fish.HookSet);
        
        if (ahBiteType == AHBiteType.Unknown || ahHookType == AHHookType.Unknown)
        {
            GatherBuddy.Log.Warning($"[AutoHook] Unknown bite/hook type for {fish.Name[GatherBuddy.Language]}, skipping");
            return;
        }

        var biteTimers = GatherBuddy.BiteTimerService.GetBiteTimers(fish.ItemId);
        var minTime = biteTimers?.WhiskerMin ?? 0;
        var maxTime = biteTimers?.WhiskerMax ?? 0;

        var requiredLure = fish.Lure != Lure.None ? fish.Lure : (Lure?)null;
        if (configureLures)
        {
            ConfigureLures(hookConfig.NormalHook, fish.HookSet, requiredLure);
        }
        SetHookConfiguration(hookConfig.NormalHook, ahBiteType, ahHookType, minTime, maxTime);

        if (fish.Predators.Length > 0)
        {
            hookConfig.IntuitionHook.UseCustomStatusHook = true;
            if (configureLures)
            {
                ConfigureLures(hookConfig.IntuitionHook, fish.HookSet, requiredLure);
            }
            SetHookConfiguration(hookConfig.IntuitionHook, ahBiteType, ahHookType, minTime, maxTime);
        }
    }

    private static void ConfigureHookFromRecord(AHHookConfig hookConfig, FishRecord record)
    {
        var ahBiteType = ConvertBiteType(record.Tug);
        var ahHookType = ConvertHookSet(record.Hook);
        
        if (ahBiteType == AHBiteType.Unknown || ahHookType == AHHookType.Unknown)
            return;

        var biteTimeSeconds = record.Bite / 1000.0;
        var minTime = Math.Max(0, biteTimeSeconds - 1.0);
        var maxTime = biteTimeSeconds + 1.0;

        SetHookConfiguration(hookConfig.NormalHook, ahBiteType, ahHookType, minTime, maxTime);

        if (record.Flags.HasFlag(Effects.Intuition))
        {
            hookConfig.IntuitionHook.UseCustomStatusHook = true;
            SetHookConfiguration(hookConfig.IntuitionHook, ahBiteType, ahHookType, minTime, maxTime);
        }
    }

    private static void SetHookConfiguration(
        AHBaseHookset hookset, 
        AHBiteType biteType, 
        AHHookType hookType,
        double minTime = 0,
        double maxTime = 0)
    {
        // Get all three bite configs for this bite type (Patience, Double, Triple)
        var (patienceConfig, doubleConfig, tripleConfig) = biteType switch
        {
            AHBiteType.Weak => (hookset.PatienceWeak, hookset.DoubleWeak, hookset.TripleWeak),
            AHBiteType.Strong => (hookset.PatienceStrong, hookset.DoubleStrong, hookset.TripleStrong),
            AHBiteType.Legendary => (hookset.PatienceLegendary, hookset.DoubleLegendary, hookset.TripleLegendary),
            _ => (null, null, null)
        };

        if (patienceConfig == null) return;
        
        patienceConfig.HooksetEnabled = true;
        patienceConfig.HooksetType = hookType;
        
        doubleConfig!.HooksetEnabled = true;
        doubleConfig.HooksetType = hookType;
        
        tripleConfig!.HooksetEnabled = true;
        tripleConfig.HooksetType = hookType;

        if (GatherBuddy.Config.AutoGatherConfig.UseHookTimers && (minTime > 0 || maxTime > 0))
        {
            patienceConfig.HookTimerEnabled = true;
            patienceConfig.MinHookTimer = minTime;
            patienceConfig.MaxHookTimer = maxTime;
            
            doubleConfig.HookTimerEnabled = true;
            doubleConfig.MinHookTimer = minTime;
            doubleConfig.MaxHookTimer = maxTime;
            
            tripleConfig.HookTimerEnabled = true;
            tripleConfig.MinHookTimer = minTime;
            tripleConfig.MaxHookTimer = maxTime;
        }
    }

    private static AHBiteType ConvertBiteType(BiteType gbBiteType)
    {
        return gbBiteType switch
        {
            BiteType.Weak => AHBiteType.Weak,
            BiteType.Strong => AHBiteType.Strong,
            BiteType.Legendary => AHBiteType.Legendary,
            BiteType.None => AHBiteType.None,
            _ => AHBiteType.Unknown
        };
    }

    private static AHHookType ConvertHookSet(HookSet gbHookSet)
    {
        return gbHookSet switch
        {
            HookSet.Hook => AHHookType.Normal,
            HookSet.Precise => AHHookType.Precision,
            HookSet.Powerful => AHHookType.Powerful,
            HookSet.DoubleHook => AHHookType.Double,
            HookSet.TripleHook => AHHookType.Triple,
            HookSet.Stellar => AHHookType.Stellar,
            HookSet.None => AHHookType.None,
            _ => AHHookType.Unknown
        };
    }

    private static void ConfigureLures(AHBaseHookset hookset, HookSet hookSet, Lure? requiredLure = null)
    {
        uint lureId = 0;
        int gpThreshold = 0;
        bool gpThresholdAbove = true;
        bool isRequiredLure = false;
        
        if (requiredLure == Lure.Ambitious)
        {
            lureId = AmbitiousLureId;
            gpThreshold = GatherBuddy.Config.AutoGatherConfig.AmbitiousLureGPThreshold;
            gpThresholdAbove = GatherBuddy.Config.AutoGatherConfig.AmbitiousLureGPAbove;
            isRequiredLure = true;
        }
        else if (requiredLure == Lure.Modest)
        {
            lureId = ModestLureId;
            gpThreshold = GatherBuddy.Config.AutoGatherConfig.ModestLureGPThreshold;
            gpThresholdAbove = GatherBuddy.Config.AutoGatherConfig.ModestLureGPAbove;
            isRequiredLure = true;
        }
        else if (hookSet == HookSet.Powerful && GatherBuddy.Config.AutoGatherConfig.EnableAmbitiousLure)
        {
            lureId = AmbitiousLureId;
            gpThreshold = GatherBuddy.Config.AutoGatherConfig.AmbitiousLureGPThreshold;
            gpThresholdAbove = GatherBuddy.Config.AutoGatherConfig.AmbitiousLureGPAbove;
        }
        else if (hookSet == HookSet.Precise && GatherBuddy.Config.AutoGatherConfig.EnableModestLure)
        {
            lureId = ModestLureId;
            gpThreshold = GatherBuddy.Config.AutoGatherConfig.ModestLureGPThreshold;
            gpThresholdAbove = GatherBuddy.Config.AutoGatherConfig.ModestLureGPAbove;
        }
        
        if (lureId == 0)
            return;

        hookset.CastLures = new AHLuresConfig
        {
            Enabled = true,
            Id = lureId,
            GpThreshold = gpThreshold,
            GpThresholdAbove = gpThresholdAbove,
            LureStacks = 3,
            CancelAttempt = isRequiredLure,
            LureTarget = isRequiredLure ? 1 : 0,
            OnlyWhenActiveSlap = false,
            OnlyWhenNotActiveSlap = false,
            OnlyWhenActiveIdentical = false,
            OnlyWhenNotActiveIdentical = false,
            OnlyCastLarge = false
        };
    }

    private static void AddFishConfig(AHCustomPresetConfig preset, Fish fish, Fish[] targetFishList, HashSet<Fish> allFish)
    {
        bool isTargetFish = targetFishList.Any(f => f.ItemId == fish.ItemId);
        if (isTargetFish)
            return;
        
        var targetFish = targetFishList.FirstOrDefault();
        if (targetFish == null)
        {
            return;
        }
        
        var targetMoochChain = new HashSet<Fish>();
        var currentFish = targetFish;
        while (currentFish.Mooches.Length > 0)
        {
            var moochFish = currentFish.Mooches[^1];
            if (targetMoochChain.Contains(moochFish))
            {
                break;
            }
            targetMoochChain.Add(moochFish);
            currentFish = moochFish;
        }
        
        bool isPartOfTargetMoochChain = targetMoochChain.Contains(fish);
        
        bool isSourceFish = false;
        if (targetMoochChain.Count > 0)
        {
            var sourcefish = targetMoochChain.Last();
            isSourceFish = fish.BiteType == sourcefish.BiteType && 
                          fish.BiteType != BiteType.Unknown && 
                          fish.BiteType != BiteType.None &&
                          !isPartOfTargetMoochChain &&
                          !targetFishList.Any(f => f.ItemId == fish.ItemId);
        }
        
        if (!isPartOfTargetMoochChain && !isSourceFish)
            return;
        
        AHAutoMooch? mooch = null;
        if (isPartOfTargetMoochChain)
        {
            mooch = new AHAutoMooch(fish.ItemId);
        }
        
        var surfaceSlap = DetermineSurfaceSlap(fish, targetFishList, allFish);
        var identicalCast = DetermineIdenticalCast(fish, targetFishList);
        
        var fishConfig = new AHFishConfig((int)fish.ItemId)
        {
            Enabled = true,
            SurfaceSlap = surfaceSlap,
            IdenticalCast = identicalCast,
            Mooch = mooch,
            NeverMooch = false
        };

        preset.ListOfFish.Add(fishConfig);
    }
    
    private static AHAutoSurfaceSlap DetermineSurfaceSlap(Fish fish, Fish[] targetFishList, HashSet<Fish> allFish)
    {
        if (fish.SurfaceSlap != null)
            return new AHAutoSurfaceSlap(true);
        
        if (!GatherBuddy.Config.AutoGatherConfig.EnableSurfaceSlap)
            return new AHAutoSurfaceSlap(false);
        
        bool isTargetFish = targetFishList.Any(f => f.ItemId == fish.ItemId);
        if (isTargetFish)
            return new AHAutoSurfaceSlap(false);
        
        bool isMoochFish = allFish.Any(f => f.Mooches.Contains(fish));
        if (isMoochFish)
        {
            return new AHAutoSurfaceSlap(false);
        }
        
        var fishBiteType = fish.BiteType;
        if (fishBiteType == BiteType.Unknown || fishBiteType == BiteType.None)
        {
            return new AHAutoSurfaceSlap(false);
        }
        
        var targetFish = targetFishList.FirstOrDefault();
        if (targetFish == null)
        {
            return new AHAutoSurfaceSlap(false);
        }
        
        BiteType relevantBiteType;
        if (targetFish.Mooches.Length > 0)
            relevantBiteType = targetFish.Mooches[0].BiteType;
        else
            relevantBiteType = targetFish.BiteType;
        
        bool sharesBiteType = fishBiteType == relevantBiteType && 
            relevantBiteType != BiteType.Unknown && 
            relevantBiteType != BiteType.None;
        
        if (sharesBiteType)
        {
            var gpThreshold = GatherBuddy.Config.AutoGatherConfig.SurfaceSlapGPThreshold;
            var gpAbove = GatherBuddy.Config.AutoGatherConfig.SurfaceSlapGPAbove;
            return new AHAutoSurfaceSlap(
                enabled: true,
                gpThreshold: gpThreshold,
                gpThresholdAbove: gpAbove
            );
        }
        
        return new AHAutoSurfaceSlap(false);
    }
    
    private static AHAutoIdenticalCast DetermineIdenticalCast(Fish fish, Fish[] targetFishList)
    {
        if (!GatherBuddy.Config.AutoGatherConfig.EnableIdenticalCast)
        {
            return new AHAutoIdenticalCast(false);
        }
        
        bool isTargetFish = targetFishList.Any(f => f.ItemId == fish.ItemId);
        if (!isTargetFish)
        {
            return new AHAutoIdenticalCast(false);
        }
        
        var gpThreshold = GatherBuddy.Config.AutoGatherConfig.IdenticalCastGPThreshold;
        var gpAbove = GatherBuddy.Config.AutoGatherConfig.IdenticalCastGPAbove;
        return new AHAutoIdenticalCast(
            enabled: true,
            gpThreshold: gpThreshold,
            gpThresholdAbove: gpAbove
        );
    }

    private static void ConfigureExtraCfg(AHCustomPresetConfig preset, uint? baitId)
    {
        if (baitId == null)
        {
            GatherBuddy.Log.Warning($"[AutoHook] No bait ID available for ExtraCfg, skipping Force Bait Swap configuration");
            return;
        }
        
        preset.ExtraCfg = new AHExtraCfg
        {
            Enabled = true,
            ForceBaitSwap = true,
            ForcedBaitId = baitId.Value
        };
    }
    
    private static void ConfigureAutoCasts(AHCustomPresetConfig preset, Fish[] fishList, ConfigPreset? gbrPreset)
    {
        var hasMooches = fishList.Any(f => f.Mooches.Length > 0);
        var needsCollect = fishList.Any(f => f.ItemData.IsCollectable);
        var canBeReduced = fishList.Any(f => f.ItemData.AetherialReduce != 0);
        
        GatherBuddy.Log.Debug($"[AutoHook] ConfigureAutoCasts: Fish count={fishList.Length}, needsCollect={needsCollect}");
        foreach (var fish in fishList)
        {
            GatherBuddy.Log.Debug($"[AutoHook]   Fish: {fish.Name[GatherBuddy.Language]} (ID:{fish.ItemId}), IsCollectable={fish.ItemData.IsCollectable}");
        }
        var shouldUsePatience = hasMooches || needsCollect || canBeReduced;
        var needsPatience = shouldUsePatience && GatherBuddy.Config.AutoGatherConfig.UsePatience;
        
        var useCordials = GatherBuddy.Config.AutoGatherConfig.UseCordialForFishing;
        var cordialGpThreshold = GatherBuddy.Config.AutoGatherConfig.CordialForFishingGPThreshold;
        
        var hasSurfaceSlap = fishList.Any(f => f.SurfaceSlap != null);
        var shouldUsePrizeCatch = hasSurfaceSlap || hasMooches;
        var usePrizeCatch = shouldUsePrizeCatch && GatherBuddy.Config.AutoGatherConfig.UsePrizeCatch;
        var useChum = GatherBuddy.Config.AutoGatherConfig.UseChum;
        
        var fisherLevel = DiscipleOfLand.FisherLevel;
        const uint patienceId = 4102;
        const uint patience2Id = 4106;
        var patienceActionId = fisherLevel >= 60 ? patience2Id : patienceId;
        var patienceGpCost = fisherLevel >= 60 ? 560 : 200;
        
        AHAutoPatience? patienceConfig = null;
        if (needsPatience)
        {
            patienceConfig = new AHAutoPatience
            {
                Enabled = true,
                Id = patienceActionId,
                GpThreshold = patienceGpCost,
                GpThresholdAbove = true
            };
        }

        preset.AutoCastsCfg = new AHAutoCastsConfig
        {
            EnableAll = true,
            DontCancelMooch = true,
            TurnCollectOffWithoutAnimCancel = true,
            CastLine = new AHAutoCastLine
            {
                Enabled = true
            },
            CastMooch = hasMooches ? new AHAutoMoochCast
            {
                Enabled = true
            } : null,
            CastPatience = patienceConfig,
            CastCollect = needsCollect ? new AHAutoCollect
            {
                Enabled = true
            } : null,
            CastCordial = useCordials ? new AHAutoCordial
            {
                Enabled = true,
                GpThreshold = cordialGpThreshold,
                GpThresholdAbove = false
            } : null,
            CastPrizeCatch = usePrizeCatch ? new AHAutoPrizeCatch
            {
                Enabled = true,
                UseWhenMoochIIOnCD = false,
                UseOnlyWithIdenticalCast = false,
                UseOnlyWithActiveSlap = hasSurfaceSlap,
                GpThreshold = GatherBuddy.Config.AutoGatherConfig.PrizeCatchGPThreshold,
                GpThresholdAbove = GatherBuddy.Config.AutoGatherConfig.PrizeCatchGPAbove
            } : null,
            CastChum = useChum ? new AHAutoChum
            {
                Enabled = true,
                GpThreshold = GatherBuddy.Config.AutoGatherConfig.ChumGPThreshold,
                GpThresholdAbove = GatherBuddy.Config.AutoGatherConfig.ChumGPAbove
            } : null,
            CastThaliaksFavor = new AHAutoThaliaksFavor
            {
                Enabled = true,
                ThaliaksFavorStacks = 3,
                ThaliaksFavorRecover = 150,
                UseWhenCordialCD = useCordials
            }
        };
        
        GatherBuddy.Log.Debug($"[AutoHook] CastCollect configured: {(preset.AutoCastsCfg.CastCollect != null ? $"Enabled={preset.AutoCastsCfg.CastCollect.Enabled}" : "null")}");
    }
}
