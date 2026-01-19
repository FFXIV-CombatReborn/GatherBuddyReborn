using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GatherBuddy.AutoGather.AtkReaders;
using GatherBuddy.AutoGather.Helpers;
using GatherBuddy.AutoGather.Lists;
using GatherBuddy.Classes;
using GatherBuddy.Enums;
using GatherBuddy.Helpers;
using GatherBuddy.Interfaces;
using GatherBuddy.Plugin;
using GatherBuddy.Time;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using LuminaTerritoryType = Lumina.Excel.Sheets.TerritoryType;

namespace GatherBuddy.AutoGather
{
    public partial class AutoGather
    {
        public bool IsPathing
            => GatherBuddy.Config.AutoGatherConfig.UseNavigation && VNavmesh.Path.IsRunning();

        public bool IsPathGenerating
            => _navState.task != null;

        public bool NavReady
            => GatherBuddy.Config.AutoGatherConfig.UseNavigation && VNavmesh.Nav.IsReady();

        private bool IsBlacklisted(Vector3 g)
        {
            var blacklist = GatherBuddy.Config.AutoGatherConfig.BlacklistedNodesByTerritoryId;
            return blacklist.TryGetValue(Dalamud.ClientState.TerritoryType, out var points)
                    && points.Contains(g);
        }

        public bool IsGathering
            => Dalamud.Conditions[ConditionFlag.Gathering] || Dalamud.Conditions[ConditionFlag.ExecutingGatheringAction];

        public bool IsFishing
            => Dalamud.Conditions[ConditionFlag.Fishing];

        enum PathfindingStage
        {
            InitialCombinedPathfinding = 0,
            FallbackDirectPathfinding = 1,
            RetryCombinedPathfinding = 2,
            Done = 3
        }
        private (Task<List<Vector3>>? task, CancellationTokenSource? cts, Vector3 destination, bool flying, bool mountingUp, bool direct, bool offset, PathfindingStage stage, long lastTry, int landWP) _navState;
        public Vector3 CurrentDestination { get { return _navState.destination; } }

        private ILocation? CurrentFarNodeLocation;
        public bool LureSuccess { get; private set; } = false;

        public unsafe GatheringReader? GatheringWindowReader
            => Automation.GenericHelpers.TryGetAddonByName("Gathering", out AtkUnitBase* addon)
                ? new GatheringReader(addon)
                : null;

        public unsafe GatheringMasterpieceReader? MasterpieceReader
            => Automation.GenericHelpers.TryGetAddonByName("GatheringMasterpiece", out AtkUnitBase* add)
                ? new GatheringMasterpieceReader(add)
                : null;

        public static IReadOnlyList<InventoryType> InventoryTypes { get; } =
        [
            InventoryType.Inventory1,
            InventoryType.Inventory2,
            InventoryType.Inventory3,
            InventoryType.Inventory4,
        ];

        public GatheringType JobAsGatheringType
        {
            get
            {
                var job = Player.Job;
                return job switch
                {
                    16 => GatheringType.Miner,     // MIN
                    17 => GatheringType.Botanist,  // BTN
                    18 => GatheringType.Fisher,    // FSH
                    _ => GatheringType.Unknown
                };
            }
        }

        public bool ShouldUseFlag
            => !GatherBuddy.Config.AutoGatherConfig.DisableFlagPathing;

        public unsafe bool ShouldFly(Vector3 destination)
        {
            if (Dalamud.Conditions[ConditionFlag.InFlight] || Dalamud.Conditions[ConditionFlag.Diving])
                return true;

            if (GatherBuddy.Config.AutoGatherConfig.ForceWalking || Dalamud.Objects.LocalPlayer == null)
            {
                return false;
            }

            if (Diadem.IsInside)
            {
                return Vector3.Distance(Dalamud.Objects.LocalPlayer.Position, destination)
                 >= GatherBuddy.Config.AutoGatherConfig.MountUpDistance;
            }

            var territory = Dalamud.ClientState.TerritoryType;
            var territoryRow = Dalamud.GameData.GameData.GetExcelSheet<LuminaTerritoryType>();
            if (territoryRow == null)
                return false;

            var playerState = PlayerState.Instance();
            if (playerState == null)
                return false;

            var aetherCurrentComp = territoryRow.GetRow(territory).AetherCurrentCompFlgSet.RowId;
            if (aetherCurrentComp == 0)
                return false;

            return playerState->IsAetherCurrentZoneComplete(aetherCurrentComp) && Vector3.Distance(Dalamud.Objects.LocalPlayer.Position, destination)
             >= GatherBuddy.Config.AutoGatherConfig.MountUpDistance;
        }

        public unsafe Vector2? TimedNodePosition
        {
            get
            {
                var map     = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentMap.Instance();

                foreach (var marker in map->MiniMapGatheringMarkers)
                    if (marker.MapMarker.X != 0 && marker.MapMarker.Y != 0)
                        return new Vector2(marker.MapMarker.X / 16f, marker.MapMarker.Y / 16f);

                return null;
            }
        }

        public  string      AutoStatus { get; private set; } = "Idle";
        public  int         LastCollectability = 0;
        public  int         LastIntegrity      = 0;
        private bool LuckUsed;
        private bool        WentHome;

        internal IEnumerable<GatherTarget> ItemsToGather
            => _activeItemList;

        internal ReadOnlyDictionary<GatheringNode, TimeInterval> DebugVisitedTimedLocations
            => _activeItemList.DebugVisitedTimedLocations;

        public readonly HashSet<Vector3> FarNodesSeenSoFar = new(8);
        public readonly List<uint>       VisitedNodes      = new(4);        
        // Distance at which a node is expected to become visible, and it is given up on if it does not.
        public const float NodeVisibilityDistance = 50f;

        private int _diademPathIndex = -1;
        
        private uint _lastTerritory = 0;
        
        public readonly Dictionary<uint, int> SpearfishingSessionCatches = new();
        private readonly Dictionary<uint, int> _spearfishingInventorySnapshot = new();
        private readonly Dictionary<uint, bool> _spawnRequirementsMetCache = new();
        private DateTime _lastAutoHookSetupTime = DateTime.MinValue;
        private bool _autoHookSetupComplete = false;
        private bool _wasGatheringSpearfish = false;
        private bool _wasAtShadowNode = false;

        private IEnumerator<Actions.BaseAction?>? ActionSequence;
        private long _lastNodeInteractionTime = 0;

        private static unsafe T* GetAddon<T>(string name) where T : unmanaged
        {
            var addon = (AtkUnitBase*)(nint)Dalamud.GameGui.GetAddonByName(name);
            if (addon != null && addon->IsFullyLoaded() && addon->IsReady)
                return (T*)addon;
            else
                return null;
        }

        public static unsafe AddonGathering* GatheringAddon
            => GetAddon<AddonGathering>("Gathering");

        public static unsafe AddonGatheringMasterpiece* MasterpieceAddon
            => GetAddon<AddonGatheringMasterpiece>("GatheringMasterpiece");

        public static unsafe AddonMaterializeDialog* MaterializeAddon
            => GetAddon<AddonMaterializeDialog>("Materialize");

        public static unsafe AddonSelectYesno* SelectYesnoAddon
            => GetAddon<AddonSelectYesno>("SelectYesno");

        public static unsafe AtkUnitBase* PurifyItemSelectorAddon
            => GetAddon<AtkUnitBase>("PurifyItemSelector");

        public static unsafe AtkUnitBase* PurifyResultAddon
            => GetAddon<AtkUnitBase>("PurifyResult");

        public static unsafe AddonRepair* RepairAddon
            => GetAddon<AddonRepair>("Repair");

        public IEnumerable<IGatherable> ItemsToGatherInZone
            => _activeItemList
                .Where(i => i.Node?.Territory.Id == Dalamud.ClientState.TerritoryType)
                .Where(i => LocationMatchesJob(i.Location))
                .Select(i => i.Item);

        private bool LocationMatchesJob(ILocation loc)
            => loc.GatheringType.ToGroup() == JobAsGatheringType;

        public bool CanAct
        {
            get
            {
                if (Dalamud.Objects.LocalPlayer == null)
                    return false;
                if (Dalamud.Conditions[ConditionFlag.BetweenAreas]
                 || Dalamud.Conditions[ConditionFlag.BetweenAreas51]
                 || Dalamud.Conditions[ConditionFlag.OccupiedInQuestEvent]
                 || Dalamud.Conditions[ConditionFlag.OccupiedSummoningBell]
                 || Dalamud.Conditions[ConditionFlag.BeingMoved]
                 || Dalamud.Conditions[ConditionFlag.Casting]
                 || Dalamud.Conditions[ConditionFlag.Casting87]
                 || Dalamud.Conditions[ConditionFlag.Jumping]
                 || Dalamud.Conditions[ConditionFlag.Jumping61]
                 || Dalamud.Conditions[ConditionFlag.LoggingOut]
                 || Dalamud.Conditions[ConditionFlag.Occupied]
                 || Dalamud.Conditions[ConditionFlag.Occupied39]
                 || Dalamud.Conditions[ConditionFlag.Unconscious]
                 || Dalamud.Conditions[ConditionFlag.ExecutingGatheringAction]
                 || Dalamud.Conditions[ConditionFlag.MountOrOrnamentTransition] // Protection against Pandora's auto mounting
                    //Node is open? Fades off shortly after closing the node, can't use items (but can mount) while it's set
                 || Dalamud.Conditions[85] && !Dalamud.Conditions[ConditionFlag.Gathering]
                 || Dalamud.Objects.LocalPlayer.IsDead
                 || Player.IsAnimationLocked)
                    return false;

                return true;
            }
        }

        private static unsafe bool HasGivingLandBuff
            => Dalamud.Objects.LocalPlayer?.StatusList.Any(s => s.StatusId == 1802) ?? false;

        public static unsafe bool IsGivingLandOffCooldown
            => ActionManager.Instance()->IsActionOffCooldown(ActionType.Action, Actions.GivingLand.ActionId);

        //Should be near the upper bound to reduce the probability of overcapping.
        private const int GivingLandYield = 30;

        private static unsafe uint FreeInventorySlots
            => InventoryManager.Instance()->GetEmptySlotsInBag();

        public static TimeStamp AdjustedServerTime
            => GatherBuddy.Time.ServerTime.AddSeconds(GatherBuddy.Config.AutoGatherConfig.TimedNodePrecog);

        private ConfigPreset MatchConfigPreset(Gatherable? item)
            => _plugin.Interface.MatchConfigPreset(item);

        private ConfigPreset MatchConfigPreset(Fish? item)
            => _plugin.Interface.MatchConfigPreset(item);
    }
}
