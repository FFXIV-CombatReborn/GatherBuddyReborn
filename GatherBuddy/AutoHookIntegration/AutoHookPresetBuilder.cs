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
    
    private static HashSet<Fish> CollectFishFromSameFishingSpots(Fish[] targetFish, HashSet<Fish> existingFish)
    {
        var additionalFish = new HashSet<Fish>();
        
        var targetBiteTypes = targetFish
            .Select(f => f.BiteType)
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
                {
                    GatherBuddy.Log.Debug($"[AutoHook] Adding {fish.Name[GatherBuddy.Language]} ({fish.BiteType}) from fishing spot for Surface Slap");
                    additionalFish.Add(fish);
                }
            }
        }
        
        return additionalFish;
    }
    
    public static AHCustomPresetConfig BuildPresetFromFish(string presetName, IEnumerable<Fish> fishList, ConfigPreset? gbrPreset = null)
    {
        var preset = new AHCustomPresetConfig(presetName);
        var fishArray = fishList.ToArray();
        
        var allFishWithMooches = CollectAllFishInMoochChains(fishArray);
        
        if (GatherBuddy.Config.AutoGatherConfig.EnableSurfaceSlap)
        {
            var additionalFish = CollectFishFromSameFishingSpots(fishArray, allFishWithMooches);
            foreach (var fish in additionalFish)
            {
                allFishWithMooches.Add(fish);
            }
        }
        
        var fishWithBait = allFishWithMooches.Where(f => f.Mooches.Length == 0).ToList();
        var fishWithMooch = allFishWithMooches.Where(f => f.Mooches.Length > 0).ToList();
        
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
                ConfigureHookForFish(hookConfig, fish);
            }
            
            preset.ListOfMooch.Add(hookConfig);
        }
        
        GatherBuddy.Log.Debug($"[AutoHook] Created {preset.ListOfBaits.Count} bait configs and {preset.ListOfMooch.Count} mooch configs");
        
        // Debug: Check hookset states
        foreach (var baitCfg in preset.ListOfBaits)
        {
            GatherBuddy.Log.Debug($"[AutoHook] Bait {baitCfg.BaitFish.Id}: Weak={baitCfg.NormalHook.PatienceWeak.HooksetEnabled}, Strong={baitCfg.NormalHook.PatienceStrong.HooksetEnabled}, Legendary={baitCfg.NormalHook.PatienceLegendary.HooksetEnabled}");
        }
        foreach (var moochCfg in preset.ListOfMooch)
        {
            GatherBuddy.Log.Debug($"[AutoHook] Mooch {moochCfg.BaitFish.Id}: Weak={moochCfg.NormalHook.PatienceWeak.HooksetEnabled}, Strong={moochCfg.NormalHook.PatienceStrong.HooksetEnabled}, Legendary={moochCfg.NormalHook.PatienceLegendary.HooksetEnabled}");
        }
        
        // Add all fish configs
        foreach (var fish in allFishWithMooches)
        {
            AddFishConfig(preset, fish, fishArray, allFishWithMooches);
        }
        
        GatherBuddy.Log.Debug($"[AutoHook] Added {preset.ListOfFish.Count} fish configs");

        ConfigureAutoCasts(preset, fishArray, gbrPreset);
        
        if (preset.AutoCastsCfg?.CastPatience != null)
        {
            GatherBuddy.Log.Debug($"[AutoHook] Final preset Patience config: Enabled={preset.AutoCastsCfg.CastPatience.Enabled}, Id={preset.AutoCastsCfg.CastPatience.Id}, GP: {preset.AutoCastsCfg.CastPatience.GpThreshold} (Above={preset.AutoCastsCfg.CastPatience.GpThresholdAbove})");
        }
        
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

    private static void ConfigureHookForFish(AHHookConfig hookConfig, Fish fish)
    {
        var ahBiteType = ConvertBiteType(fish.BiteType);
        var ahHookType = ConvertHookSet(fish.HookSet);
        
        GatherBuddy.Log.Debug($"[AutoHook] Configuring hook for {fish.Name[GatherBuddy.Language]}: BiteType={fish.BiteType}->{ahBiteType}, HookSet={fish.HookSet}->{ahHookType}");
        
        if (ahBiteType == AHBiteType.Unknown || ahHookType == AHHookType.Unknown)
        {
            GatherBuddy.Log.Warning($"[AutoHook] Unknown bite/hook type for {fish.Name[GatherBuddy.Language]}, skipping");
            return;
        }

        var biteTimers = GatherBuddy.BiteTimerService.GetBiteTimers(fish.ItemId);
        var minTime = biteTimers?.WhiskerMin ?? 0;
        var maxTime = biteTimers?.WhiskerMax ?? 0;
        
        if (biteTimers != null)
        {
            GatherBuddy.Log.Debug($"[AutoHook] Using bite timers for {fish.Name[GatherBuddy.Language]}: {minTime:F1}s - {maxTime:F1}s");
        }

        ConfigureLures(hookConfig.NormalHook, fish.HookSet);
        SetHookConfiguration(hookConfig.NormalHook, ahBiteType, ahHookType, minTime, maxTime);

        if (fish.Predators.Length > 0)
        {
            hookConfig.IntuitionHook.UseCustomStatusHook = true;
            ConfigureLures(hookConfig.IntuitionHook, fish.HookSet);
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

        GatherBuddy.Log.Debug($"[AutoHook] Setting {biteType} hookset: Enabled={true}, Type={hookType}");
        
        // Set Patience hookset
        patienceConfig.HooksetEnabled = true;
        patienceConfig.HooksetType = hookType;
        
        // Set Double hookset
        doubleConfig!.HooksetEnabled = true;
        doubleConfig.HooksetType = hookType;
        
        // Set Triple hookset
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

    private static void ConfigureLures(AHBaseHookset hookset, HookSet hookSet)
    {
        bool ambitiousEnabled = false;
        bool modestEnabled = false;
        
        if (hookSet == HookSet.Powerful && GatherBuddy.Config.AutoGatherConfig.EnableAmbitiousLure)
        {
            ambitiousEnabled = true;
        }
        
        if (hookSet == HookSet.Precise && GatherBuddy.Config.AutoGatherConfig.EnableModestLure)
        {
            modestEnabled = true;
        }
        
        if (!ambitiousEnabled && !modestEnabled)
            return;

        hookset.CastLures = new AHLuresConfig
        {
            Enabled = true,
            AmbitiousLureEnabled = ambitiousEnabled,
            AmbitiousLureGpThreshold = GatherBuddy.Config.AutoGatherConfig.AmbitiousLureGPThreshold,
            AmbitiousLureGpThresholdAbove = GatherBuddy.Config.AutoGatherConfig.AmbitiousLureGPAbove,
            ModestLureEnabled = modestEnabled,
            ModestLureGpThreshold = GatherBuddy.Config.AutoGatherConfig.ModestLureGPThreshold,
            ModestLureGpThresholdAbove = GatherBuddy.Config.AutoGatherConfig.ModestLureGPAbove
        };
    }

    private static void AddFishConfig(AHCustomPresetConfig preset, Fish fish, Fish[] targetFishList, HashSet<Fish> allFish)
    {
        // The mooch ID should be the immediate predecessor fish in the chain
        // which is the last element in the Mooches array
        var mooch = new AHAutoMooch();
        if (fish.Mooches.Length > 0)
        {
            mooch = new AHAutoMooch(fish.Mooches[^1].ItemId);
        }
        
        // Configure Surface Slap based on config and bite type matching
        var surfaceSlap = DetermineSurfaceSlap(fish, targetFishList, allFish);
        
        // Configure Identical Cast for target fish
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
        {
            GatherBuddy.Log.Debug($"[AutoHook] Fish {fish.Name[GatherBuddy.Language]} has manual Surface Slap data");
            return new AHAutoSurfaceSlap(true);
        }
        
        if (!GatherBuddy.Config.AutoGatherConfig.EnableSurfaceSlap)
        {
            return new AHAutoSurfaceSlap(false);
        }
        
        bool isTargetFish = targetFishList.Any(f => f.ItemId == fish.ItemId);
        if (isTargetFish)
        {
            GatherBuddy.Log.Debug($"[AutoHook] Fish {fish.Name[GatherBuddy.Language]} is a target fish - no Surface Slap");
            return new AHAutoSurfaceSlap(false);
        }
        
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
        
        bool sharesBiteTypeWithTarget = targetFishList.Any(targetFish => 
            targetFish.BiteType == fishBiteType && 
            targetFish.BiteType != BiteType.Unknown && 
            targetFish.BiteType != BiteType.None
        );
        
        if (sharesBiteTypeWithTarget)
        {
            var gpThreshold = GatherBuddy.Config.AutoGatherConfig.SurfaceSlapGPThreshold;
            var gpAbove = GatherBuddy.Config.AutoGatherConfig.SurfaceSlapGPAbove;
            GatherBuddy.Log.Debug($"[AutoHook] Enabling Surface Slap for {fish.Name[GatherBuddy.Language]} ({fishBiteType}) - shares bite type with target fish. GP: {(gpAbove ? "Above" : "Below")} {gpThreshold}");
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
        // Check if Identical Cast auto-configuration is enabled
        if (!GatherBuddy.Config.AutoGatherConfig.EnableIdenticalCast)
        {
            return new AHAutoIdenticalCast(false);
        }
        
        // Check if this fish is a target fish (Identical Cast ONLY for targets)
        bool isTargetFish = targetFishList.Any(f => f.ItemId == fish.ItemId);
        if (!isTargetFish)
        {
            return new AHAutoIdenticalCast(false);
        }
        
        // Enable Identical Cast for target fish
        var gpThreshold = GatherBuddy.Config.AutoGatherConfig.IdenticalCastGPThreshold;
        var gpAbove = GatherBuddy.Config.AutoGatherConfig.IdenticalCastGPAbove;
        GatherBuddy.Log.Debug($"[AutoHook] Enabling Identical Cast for {fish.Name[GatherBuddy.Language]} (target fish). GP: {(gpAbove ? "Above" : "Below")} {gpThreshold}");
        return new AHAutoIdenticalCast(
            enabled: true,
            gpThreshold: gpThreshold,
            gpThresholdAbove: gpAbove
        );
    }

    private static void ConfigureAutoCasts(AHCustomPresetConfig preset, Fish[] fishList, ConfigPreset? gbrPreset)
    {
        var needsPatience = fishList.Any(f => f.ItemData.Rarity > 0 || f.IsBigFish);
        var needsCollect = fishList.Any(f => f.ItemData.IsCollectable);
        var useCordials = gbrPreset?.Consumables.Cordial.Enabled ?? false;
        
        var hasSurfaceSlap = fishList.Any(f => f.SurfaceSlap != null);
        var hasMooches = fishList.Any(f => f.Mooches.Length > 0);
        var needsPrizeCatch = hasSurfaceSlap || hasMooches;
        
        var fisherLevel = DiscipleOfLand.FisherLevel;
        const uint patienceId = 4102;
        const uint patience2Id = 4106;
        var patienceActionId = fisherLevel >= 60 ? patience2Id : patienceId;
        var patienceGpCost = fisherLevel >= 60 ? 560 : 200;
        GatherBuddy.Log.Debug($"[AutoHook] Fisher level: {fisherLevel}, setting Patience action ID: {patienceActionId}, GP cost: {patienceGpCost}");
        
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
            GatherBuddy.Log.Debug($"[AutoHook] Created Patience config: Enabled={patienceConfig.Enabled}, Id={patienceConfig.Id}, GP: {patienceConfig.GpThreshold} (Above={patienceConfig.GpThresholdAbove})");
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
                Enabled = true
            } : null,
            CastPrizeCatch = needsPrizeCatch ? new AHAutoPrizeCatch
            {
                Enabled = true,
                UseWhenMoochIIOnCD = false,
                UseOnlyWithIdenticalCast = false,
                UseOnlyWithActiveSlap = hasSurfaceSlap
            } : null,
            CastThaliaksFavor = !needsPrizeCatch ? new AHAutoThaliaksFavor
            {
                Enabled = true,
                ThaliaksFavorStacks = 3,
                ThaliaksFavorRecover = 150,
                UseWhenCordialCD = useCordials
            } : null
        };
    }
}
