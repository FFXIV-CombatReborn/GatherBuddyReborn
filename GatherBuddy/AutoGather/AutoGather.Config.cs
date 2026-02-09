using System.Collections.Generic;
using System.Numerics;

namespace GatherBuddy.AutoGather
{
    public class AutoGatherConfig
    {
        public float                           MountUpDistance               { get; set; } = 15.0f;
        public float                           LandingDistance               { get; set; } = 6.0f;
        public uint                            AutoGatherMountId             { get; set; } = 1;
        public bool MoveWhileMounting { get; set; } = false;
        public Dictionary<uint, List<Vector3>> BlacklistedNodesByTerritoryId { get; set; } = [];

        public bool UseGivingLandOnCooldown { get; set; } = false;

        public int TimedNodePrecog { get; set; } = 20;
        public bool DoGathering { get; set; } = true;
        public bool AutoRetainerMultiMode { get; set; } = false;
        public int AutoRetainerMultiModeThreshold { get; set; } = 300;
        public bool AutoRetainerDelayForTimedNodes { get; set; } = true;
        public float NavResetCooldown { get; set; } = 3.0f;
        public float NavResetThreshold { get; set; } = 2.0f;
        public bool ForceWalking { get; set; } = false;
        public float FarNodeFilterDistance { get; set; } = 50.0f;
        public bool DisableFlagPathing { get; set; } = false;
        public bool DoMaterialize { get; set; } = false;
        public bool DoReduce { get; set; } = false;
        public bool AlwaysReduceAllItems { get; set; } = false;
        public bool DoRepair { get; set; } = false;
        public int RepairThreshold { get; set; } = 50;
        public bool HonkMode { get; set; } = true;
        public SortingType SortingMethod { get; set; } = SortingType.Location;
        public bool TeleportToNextNode { get; set; } = false;
        public bool GoHomeWhenIdle { get; set; } = true;
        public bool GoHomeWhenDone { get; set; } = true;
        public bool UseSkillsForFallbackItems { get; set; } = false;
        public bool AbandonNodes { get; set; } = false;
        public uint ExecutionDelay { get; set; } = 0;
        public bool CheckRetainers { get; set; } = false;
        public string LifestreamCommand { get; set; } = "auto";
        public int SoundPlaybackVolume { get; set; } = 100;
        public bool FishDataCollection { get; set; } = false;
        public bool AlwaysGatherMaps { get; set; } = false;
        public int MaxFishingSpotMinutes { get; set; } = 0;
        public bool UseNavigation { get; set; } = true;
        public bool UseAutoHook { get; set; } = true;
        public bool DisableAutoHookOnStop { get; set; } = false;
        public bool UseExistingAutoHookPresets { get; set; } = false;
        public bool EnableSurfaceSlap { get; set; } = false;
        public int SurfaceSlapGPThreshold { get; set; } = 200;
        public bool SurfaceSlapGPAbove { get; set; } = true;
        public bool EnableIdenticalCast { get; set; } = false;
        public int IdenticalCastGPThreshold { get; set; } = 350;
        public bool IdenticalCastGPAbove { get; set; } = true;
        public bool EnableAmbitiousLure { get; set; } = false;
        public int AmbitiousLureGPThreshold { get; set; } = 200;
        public bool AmbitiousLureGPAbove { get; set; } = true;
        public bool EnableModestLure { get; set; } = false;
        public int ModestLureGPThreshold { get; set; } = 200;
        public bool ModestLureGPAbove { get; set; } = false;
        public bool UseHookTimers { get; set; } = false;
        public bool AutoCollectablesFishing { get; set; } = true;
        public bool DiademAutoAetherCannon { get; set; } = false;
        public bool DiademWindmireJumps { get; set; } = false;
        public bool DiademFarmCloudedNodes { get; set; } = true;
        public bool DeferRepairDuringFishingBuffs { get; set; } = true;
        public bool DeferReductionDuringFishingBuffs { get; set; } = true;
        public bool DeferMateriaExtractionDuringFishingBuffs { get; set; } = true;
        public bool UseFood { get; set; } = false;
        public uint FoodItemId { get; set; } = 0;
        public bool UseMedicine { get; set; } = false;
        public uint MedicineItemId { get; set; } = 0;
        public bool UseCordialForFishing { get; set; } = false;
        public int CordialForFishingGPThreshold { get; set; } = 0;
        public bool UsePatience { get; set; } = true;
        public bool UsePrizeCatch { get; set; } = false;
        public int PrizeCatchGPThreshold { get; set; } = 200;
        public bool PrizeCatchGPAbove { get; set; } = true;
        public bool UseChum { get; set; } = false;
        public int ChumGPThreshold { get; set; } = 100;
        public bool ChumGPAbove { get; set; } = true;
        public bool DisableRandomLandingPositions { get; set; } = false;

        public enum SortingType
        {
            None = 0,
            Location = 1,
        }
    }
}
