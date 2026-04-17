using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Memory;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GatherBuddy.Automation;
using GatherBuddy.AutoGather.Helpers;
using GatherBuddy.Plugin;
using GatherBuddy.SeFunctions;
using GatherBuddy.Utility;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using GameObjectStruct = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
using MapType = FFXIVClientStructs.FFXIV.Client.UI.Agent.MapType;
using PlayerState = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState;

namespace GatherBuddy.Vulcan.Vendors;

public class VendorNavigator
{
    private static readonly string[] ResidentialDistrictMenuEntries =
    [
        "Residential District Aethernet.",
        "冒険者居住区転送",
        "Wohngebiet",
        "Quartier résidentiel",
        "冒险者住宅区传送",
        "冒險者住宅區傳送",
        "모험가 거주구 이동",
    ];

    private static readonly string[] GoToWardMenuEntries =
    [
        "Go to specified ward. (Review Tabs)",
        "区を指定して移動（ハウスアピール確認）",
        "Zum angegebenen Bezirk (Zweck der Unterkunft einsehen)",
        "Spécifier le secteur où aller (Voir les attraits)",
        "移动到指定小区（查看房屋宣传标签）",
        "移動到指定小區（查看房屋宣傳標籤）",
        "移動到指定社區（查看房屋宣傳標籤）",
        "구역을 지정하여 이동(주택 정보 확인)",
    ];

    private static readonly string[] HousingTravelConfirmationTexts =
    [
        "Travel to",
        "よろしいですか？",
        "Zu Wohnbezirk",
        "Vous allez vous rendre à",
        "要移动到",
        "要移動到",
        "이동하시겠습니까?",
    ];

    private static readonly uint[] KnownAethernetShardSeedDataIds =
    [
        2000151u,
        2014665u,
        2014664u,
        2003395u,
    ];

    private static HashSet<uint>? _knownAethernetShardDataIds;
    private enum HousingDistrict
    {
        Mist,
        LavenderBeds,
        Goblet,
        Empyreum,
        Shirogane,
    }

    private static float GetPathfindTolerance(IGameObject? liveNpc, NavigationDestinationMode destinationMode)
    {
        if (liveNpc == null || destinationMode != NavigationDestinationMode.TargetInteractionRange)
            return 0f;

        return LiveNpcInteractionDistance;
    }
    private enum State
    {
        Idle,
        Teleporting,
        WaitingForTeleport,
        WaitingForZoneLoad,
        Navigating,
        ReadyToPurchase,
        Failed,
    }

    private enum NavigationDestinationMode
    {
        TargetInteractionRange,
        SafeStagingPoint,
        FinalApproachPoint,
    }

    private static bool TryFindHousingRoute(uint targetTerritoryId, out uint aetheryteId)
    {
        aetheryteId = 0;
        if (!TryGetHousingDistrict(targetTerritoryId, out var district))
            return false;

        aetheryteId = district switch
        {
            HousingDistrict.Mist         => 8u,
            HousingDistrict.LavenderBeds => 2u,
            HousingDistrict.Goblet       => 9u,
            HousingDistrict.Empyreum     => 70u,
            HousingDistrict.Shirogane    => 111u,
            _                            => 0u,
        };

        if (aetheryteId == 0)
            return true;

        if (!Teleporter.IsAttuned(aetheryteId))
        {
            GatherBuddy.Log.Debug($"[VendorNavigator] Housing route for territory {targetTerritoryId} requires unattuned aetheryte {aetheryteId}");
            aetheryteId = 0;
        }
        else
        {
            GatherBuddy.Log.Debug($"[VendorNavigator] Using housing route for territory {targetTerritoryId}: district={district}, cityAetheryte={aetheryteId}, ward=1");
        }

        return true;
    }

    private static bool TryFindNearestShardInTerritory(uint territoryId, Vector3 npcPosition, out string shardName, out float shardDistanceToVendor, out float shardDistanceToPlayer)
    {
        if (TryGetHousingDistrict(territoryId, out var housingDistrict))
            return TryFindNearestResidentialShard(housingDistrict, npcPosition, out shardName, out shardDistanceToVendor, out shardDistanceToPlayer);
        shardName             = string.Empty;
        shardDistanceToVendor = float.MaxValue;
        shardDistanceToPlayer = float.MaxValue;

        var aetheryteSheet = Dalamud.GameData.GetExcelSheet<Aetheryte>();
        if (aetheryteSheet == null)
            return false;

        var npcXZ    = new Vector2(npcPosition.X, npcPosition.Z);
        var player   = Dalamud.Objects.LocalPlayer;
        var playerXZ = player != null ? new Vector2(player.Position.X, player.Position.Z) : Vector2.Zero;

        foreach (var shard in aetheryteSheet)
        {
            if (shard.IsAetheryte || shard.Territory.RowId != territoryId || shard.AethernetName.RowId == 0)
                continue;

            var currentShardName = shard.AethernetName.Value.Name.ExtractText();
            if (string.IsNullOrWhiteSpace(currentShardName))
                continue;

            var shardXZ = GetAetheryteXZ(shard);
            if (!shardXZ.HasValue)
                continue;

            var distanceToVendor = Vector2.Distance(npcXZ, shardXZ.Value);
            if (distanceToVendor >= shardDistanceToVendor)
                continue;

            shardName             = currentShardName;
            shardDistanceToVendor = distanceToVendor;
            shardDistanceToPlayer = player != null ? Vector2.Distance(playerXZ, shardXZ.Value) : float.MaxValue;
        }

        return shardName.Length > 0;
    }

    private static bool TryFindNearestResidentialShard(HousingDistrict district, Vector3 npcPosition, out string shardName, out float shardDistanceToVendor, out float shardDistanceToPlayer)
    {
        shardName             = string.Empty;
        shardDistanceToVendor = float.MaxValue;
        shardDistanceToPlayer = float.MaxValue;

        var housingAethernetSheet = Dalamud.GameData.GetExcelSheet<HousingAethernet>();
        var mapSheet              = Dalamud.GameData.GetExcelSheet<Map>();
        if (housingAethernetSheet == null || mapSheet == null)
            return false;

        var territoryCandidates = GetHousingDistrictTerritoryCandidates(district);
        var player              = Dalamud.Objects.LocalPlayer;
        var playerXZ            = player != null ? new Vector2(player.Position.X, player.Position.Z) : Vector2.Zero;
        var npcXZ               = new Vector2(npcPosition.X, npcPosition.Z);
        foreach (var territoryCandidate in territoryCandidates)
        {
            var housingShards = housingAethernetSheet
                .Where(a => a.TerritoryType.RowId == territoryCandidate)
                .OrderBy(a => a.Order)
                .ToList();
            if (housingShards.Count == 0)
                continue;

            var subdivisionModifier = GetHousingSubdivisionModifier(district);
            var subdivisionStart    = housingShards.Count / 2;
            for (var index = 0; index < housingShards.Count; index++)
            {
                var housingShard = housingShards[index];
                var currentShardName = housingShard.PlaceName.Value.Name.ExtractText();
                if (string.IsNullOrWhiteSpace(currentShardName))
                    continue;

                var shardXZ = GetHousingAetheryteXZ(housingShard, territoryCandidate, mapSheet);
                if (!shardXZ.HasValue)
                    continue;
                var shardPosition = shardXZ.Value;

                if (index >= subdivisionStart)
                    shardPosition += subdivisionModifier;

                var distanceToVendor = Vector2.Distance(npcXZ, shardPosition);
                if (distanceToVendor >= shardDistanceToVendor)
                    continue;

                shardName             = currentShardName;
                shardDistanceToVendor = distanceToVendor;
                shardDistanceToPlayer = player != null ? Vector2.Distance(playerXZ, shardPosition) : float.MaxValue;
            }

            if (shardName.Length > 0)
                return true;
        }

        GatherBuddy.Log.Debug($"[VendorNavigator] No residential shard candidate found for housing district {district}");
        return false;
    }

    private static bool TryGetHousingDistrict(uint territoryId, out HousingDistrict district)
    {
        switch (territoryId)
        {
            case 136:
            case 339:
                district = HousingDistrict.Mist;
                return true;
            case 340:
                district = HousingDistrict.LavenderBeds;
                return true;
            case 341:
                district = HousingDistrict.Goblet;
                return true;
            case 649:
            case 979:
                district = HousingDistrict.Empyreum;
                return true;
            case 641:
                district = HousingDistrict.Shirogane;
                return true;
            default:
                district = default;
                return false;
        }
    }

    private static uint[] GetHousingDistrictTerritoryCandidates(HousingDistrict district)
        => district switch
        {
            HousingDistrict.Mist         => [339u, 136u],
            HousingDistrict.LavenderBeds => [340u],
            HousingDistrict.Goblet       => [341u],
            HousingDistrict.Empyreum     => [979u, 649u],
            HousingDistrict.Shirogane    => [641u],
            _                            => Array.Empty<uint>(),
        };

    private static Vector2 GetHousingSubdivisionModifier(HousingDistrict district)
        => district == HousingDistrict.Empyreum
            ? new Vector2(-704f, -654f)
            : new Vector2(-700f, -700f);

    private static Vector2? GetHousingAetheryteXZ(HousingAethernet housingShard, uint territoryId, ExcelSheet<Map> mapSheet)
    {
        var map = mapSheet.FirstOrDefault(m => m.TerritoryType.RowId == territoryId);
        if (map.RowId == 0)
            return null;
        var markerSheet = Dalamud.GameData.GetSubrowExcelSheet<MapMarker>();
        if (markerSheet == null)
            return null;

        foreach (var markerRow in markerSheet)
        {
            foreach (var marker in markerRow)
            {
                if (marker.DataType != 4 || marker.DataKey.RowId != housingShard.PlaceName.RowId)
                    continue;

                var x = (marker.X - 1024f) / (map.SizeFactor / 100f);
                var z = (marker.Y - 1024f) / (map.SizeFactor / 100f);
                return new Vector2(x, z);
            }
        }

        return null;
    }

    private static bool IsEquivalentTerritory(uint currentTerritoryId, uint targetTerritoryId)
    {
        if (currentTerritoryId == targetTerritoryId)
            return true;

        return TryGetHousingDistrict(currentTerritoryId, out var currentDistrict)
            && TryGetHousingDistrict(targetTerritoryId, out var targetDistrict)
            && currentDistrict == targetDistrict;
    }

    private static bool ShouldForceFreshHousingEntry(uint currentTerritoryId, uint targetTerritoryId)
        => TryGetHousingDistrict(currentTerritoryId, out var currentDistrict)
        && TryGetHousingDistrict(targetTerritoryId, out var targetDistrict)
        && currentDistrict == targetDistrict;

    private const float  LiveNpcInteractionDistance         = 5.0f;
    private const float  CachedInteractionDistance          = 5.0f;
    private const float  AetheryteSourceInteractionDistance = 15.0f;
    private const float  AetheryteSourceHorizontalInteractionDistance = 11.0f;
    private const float  AethernetShardInteractionDistance  = 4.6f;
    private const float  LocalAethernetDestinationProximityThreshold = 10.0f;
    private const float  LocalAethernetEstimatedInteractionCost      = 10.0f;
    private const float  LocalAethernetMinimumSavings                = 15.0f;
    private const float  RepathDistanceThreshold    = 1.5f;
    private const double RepathCooldown             = 0.75;
    private const double TeleportCooldown           = 3.0;
    private const double ZoneLoadWait               = 5.0;
    private const double MountRetryCooldown         = 2.0;
    private const double LandingRetryCooldown       = 1.0;
    private const double VendorApproachDiagnosticsCooldown = 5.0;
    private const double VendorGroundApproachStabilizationSeconds = 2.0;
    private const double VendorGroundProgressTimeout = 2.0;
    private const double VendorGroundProgressRetryCooldown = 1.0;
    private const double VendorGroundInteractionRangeStallTimeout = 0.75;
    private const double VendorGroundInteractionRangeFallbackTimeout = 1.0;
    private const double VendorInteractionRangeFallbackDurationSeconds = 3.0;
    private const float  VendorGroundProgressMinimumMovement = 1.0f;
    private const float  VendorGroundProgressMinimumDistanceGain = 0.75f;
    private const float  VendorLandingDistance      = 25.0f;
    private const float  VendorGroundApproachDistance = 25.0f;
    private const float  VendorDirectLiveNpcApproachDistance = 20.0f;
    private const float  VendorStagingPointArrivalDistance = 2.5f;
    private const float  VendorApproachCompletionDistance = 1.5f;
    private const float  VendorApproachFloorSearchHeight  = 10.0f;
    private const float  VendorApproachMeshSearchRadius   = 3.0f;
    private const float  VendorApproachMaxHorizontalDrift = 2.5f;
    private const float  VendorStagingMaxHorizontalDrift  = 4.0f;
    private const float  VendorApproachMaxVerticalDrift   = 4.0f;
    private const float  VendorApproachDistanceStep       = 0.5f;
    private const float  VendorApproachMinimumStandOff    = 0.35f;
    private const float  VendorCloseCombinedLandingDistanceMinimum = 4.0f;
    private const float  VendorCloseCombinedLandingDistanceMaximum = 12.0f;
    private const float  VendorCloseCombinedLandingDistanceBuffer  = 2.0f;

    private static readonly float[] VendorApproachCandidateAngles =
    [
        0f,
        MathF.PI / 8f,
        -MathF.PI / 8f,
        MathF.PI / 4f,
        -MathF.PI / 4f,
        3f * MathF.PI / 8f,
        -3f * MathF.PI / 8f,
        MathF.PI / 2f,
        -MathF.PI / 2f,
        3f * MathF.PI / 4f,
        -3f * MathF.PI / 4f,
        MathF.PI,
    ];

    private static readonly float[] VendorLandingCandidateDistances =
    [
        8f,
        10f,
        12.5f,
        15f,
        18f,
        22f,
        25f,
    ];

    private static readonly float[] VendorCloseStagingCandidateDistances =
    [
        3.5f,
        4.5f,
        5.5f,
        6.5f,
        7.5f,
    ];

    private State              _state = State.Idle;
    private VendorNpcLocation? _target;
    private DateTime           _stateStartTime;
    private DateTime           _lastRepathTime;
    private bool               _teleportAttempted;
    private string?            _pendingAethernetName;
    private bool               _pendingAethernetNeedsSourceApproach;
    private bool               _pendingHousingEntry;
    private string?            _pendingHousingShardTeleportName;
    private float              _pendingHousingShardDistanceToVendor;
    private uint               _pendingAethernetParentTerritoryId;
    private Vector3            _navigationDestination;
    private float              _navigationDestinationTolerance;
    private bool               _hasNavigationDestination;
    private bool               _usingLiveNpcDestination;
    private CancellationTokenSource? _pathCancellationTokenSource;
    private Task<List<Vector3>>?     _pathTask;
    private DateTime                 _lastMountAttemptTime;
    private DateTime                 _lastLandingAttemptTime;
    private DateTime                 _lastHousingEntryActionTime;
    private DateTime                 _lastHousingEntryStatusLogTime;
    private bool                     _pathUsesFlight;
    private bool                     _pathUsesCombinedApproach;
    private bool                     _mountingUp;
    private bool                     _waitingForMount;
    private int                      _landingWaypointCount;
    private bool                     _attemptedHousingShardTeleport;
    private uint                     _forcedHousingEntryOriginTerritoryId;
    private bool                     _awaitingForcedHousingOriginExit;
    private string?                  _lastApproachDiagnosticsSummary;
    private DateTime                 _lastApproachDiagnosticsLogTime;
    private DateTime                 _forceGroundApproachUntil;
    private Vector3                  _lastGroundProgressPosition;
    private float                    _lastGroundProgressDistance = float.MaxValue;
    private DateTime                 _lastGroundProgressTime;
    private Vector3                  _lastGroundRetryPosition;
    private DateTime                 _lastGroundRetryTime;
    private int                      _groundRetryCount;
    private DateTime                 _forceInteractionRangeApproachUntil;

    public bool               IsReadyToPurchase => _state == State.ReadyToPurchase;
    public bool               IsFailed          => _state == State.Failed;
    public bool               IsActive          => _state is not (State.Idle or State.ReadyToPurchase or State.Failed);
    public VendorNpcLocation? CurrentTarget     => _target;

    public void PlaceMapMarker(VendorNpcLocation target)
        => PlaceMapFlag(target);

    public void StartNavigation(VendorNpcLocation target, bool continueCurrentVendorInteraction = false)
    {
        Stop();
        _target            = target;
        _teleportAttempted = false;
        _stateStartTime    = DateTime.UtcNow;
        _forcedHousingEntryOriginTerritoryId = 0;
        _awaitingForcedHousingOriginExit     = false;

        GatherBuddy.Log.Information($"[VendorNavigator] Starting navigation to {target.NpcName} (territory {target.TerritoryId})");

        PlaceMapFlag(target);

        var shouldForceFreshHousingEntry = ShouldForceFreshHousingEntry(Dalamud.ClientState.TerritoryType, target.TerritoryId);

        if (!continueCurrentVendorInteraction && shouldForceFreshHousingEntry)
        {
            _forcedHousingEntryOriginTerritoryId = Dalamud.ClientState.TerritoryType;
            _awaitingForcedHousingOriginExit     = true;
            GatherBuddy.Log.Debug($"[VendorNavigator] Starting inside housing district {Dalamud.ClientState.TerritoryType} for housing target {target.TerritoryId}, forcing a fresh city entry route");
            _state = State.Teleporting;
            return;
        }

        var playerPos = Dalamud.Objects.LocalPlayer?.Position ?? Vector3.Zero;
        if (Dalamud.ClientState.TerritoryType == target.TerritoryId && playerPos != Vector3.Zero)
        {
            var liveNpc = FindLiveNpcObject();
            var destination = GetDesiredNavigationDestination(liveNpc, out var usingLiveNpc, out var destinationMode, out _);
            if (destinationMode != NavigationDestinationMode.SafeStagingPoint
             && HasReachedNavigationDestination(playerPos, liveNpc, destination, destinationMode))
            {
                GatherBuddy.Log.Information($"[VendorNavigator] Already adjacent to {GetDisplayName(liveNpc)} ({GetTargetDistance(playerPos, liveNpc):F1}m)");
                _state = State.ReadyToPurchase;
                return;
            }

            if (TryPrepareCurrentTerritoryAethernetShortcut(playerPos))
                return;

            _state = State.WaitingForZoneLoad;
        }
        else if (IsEquivalentTerritory(Dalamud.ClientState.TerritoryType, target.TerritoryId) && playerPos != Vector3.Zero)
        {
            _state = State.WaitingForZoneLoad;
        }
        else
        {
            _state = State.Teleporting;
        }
    }

    private static bool CanIssueTeleportRequest()
        => VendorInteractionHelper.IsReadyToLeaveVendor();

    public void Update()
    {
        if (_target == null || _state is State.Idle or State.ReadyToPurchase or State.Failed)
            return;

        try
        {
            switch (_state)
            {
                case State.Teleporting:        UpdateTeleporting();        break;
                case State.WaitingForTeleport: UpdateWaitingForTeleport(); break;
                case State.WaitingForZoneLoad: UpdateWaitingForZoneLoad(); break;
                case State.Navigating:         UpdateNavigating();         break;
            }
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[VendorNavigator] Error in Update: {ex.Message}");
            _state = State.Failed;
        }
    }

    public void Stop()
    {
        StopPathing();
        _state                              = State.Idle;
        _target                             = null;
        _pendingAethernetName               = null;
        _pendingAethernetNeedsSourceApproach = false;
        _pendingHousingEntry                = false;
        _pendingHousingShardTeleportName    = null;
        _pendingHousingShardDistanceToVendor = 0f;
        _pendingAethernetParentTerritoryId  = 0;
        _navigationDestination              = Vector3.Zero;
        _navigationDestinationTolerance     = 0f;
        _hasNavigationDestination           = false;
        _usingLiveNpcDestination            = false;
        _lastRepathTime                     = DateTime.MinValue;
        _lastMountAttemptTime               = DateTime.MinValue;
        _lastLandingAttemptTime             = DateTime.MinValue;
        _lastHousingEntryActionTime         = DateTime.MinValue;
        _lastHousingEntryStatusLogTime      = DateTime.MinValue;
        _pathUsesFlight                     = false;
        _pathUsesCombinedApproach           = false;
        _mountingUp                         = false;
        _waitingForMount                    = false;
        _landingWaypointCount               = 0;
        _attemptedHousingShardTeleport       = false;
        _forcedHousingEntryOriginTerritoryId = 0;
        _awaitingForcedHousingOriginExit     = false;
        _lastApproachDiagnosticsSummary      = null;
        _lastApproachDiagnosticsLogTime      = DateTime.MinValue;
        _forceGroundApproachUntil            = DateTime.MinValue;
        _lastGroundProgressPosition          = Vector3.Zero;
        _lastGroundProgressDistance          = float.MaxValue;
        _lastGroundProgressTime              = DateTime.MinValue;
        _lastGroundRetryPosition             = Vector3.Zero;
        _lastGroundRetryTime                 = DateTime.MinValue;
        _groundRetryCount                    = 0;
        _forceInteractionRangeApproachUntil  = DateTime.MinValue;
    }

    private static unsafe void PlaceMapFlag(VendorNpcLocation target)
    {
        try
        {
            var agentMap = AgentMap.Instance();
            if (agentMap == null) return;

            var territorySheet = Dalamud.GameData.GetExcelSheet<TerritoryType>();
            if (territorySheet == null || !territorySheet.TryGetRow(target.TerritoryId, out var territory)) return;
            var mapSheet = Dalamud.GameData.GetExcelSheet<Map>();
            Map? map = null;
            if (target.MapRowId != 0 && mapSheet != null && mapSheet.TryGetRow(target.MapRowId, out var selectedMap))
                map = selectedMap;
            else
                map = territory.Map.ValueNullable;
            if (map == null) return;


            agentMap->FlagMarkerCount = 0;
            agentMap->SetFlagMapMarker(target.TerritoryId, target.MapRowId, target.Position.X, target.Position.Z, 60561U);
            agentMap->OpenMap(target.MapRowId, target.TerritoryId, target.NpcName, MapType.FlagMarker);
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Warning($"[VendorNavigator] Failed to place map flag: {ex.Message}");
        }
    }

    private void UpdateTeleporting()
    {
        if (Dalamud.Conditions[ConditionFlag.BetweenAreas] || Dalamud.Conditions[ConditionFlag.BetweenAreas51])
            return;
        if (!CanIssueTeleportRequest())
            return;

        if (_teleportAttempted) return;

        var (aetheryteId, aethernetName, requiresHousingEntry) = FindBestRoute(_target!.TerritoryId, _target.Position);
        if (aetheryteId == 0)
        {
            if (TryGetHousingDistrict(_target.TerritoryId, out _))
                GatherBuddy.Log.Error($"[VendorNavigator] Housing route unavailable for territory {_target.TerritoryId}; the required city aetheryte may be unattuned.");
            GatherBuddy.Log.Error($"[VendorNavigator] No route found to territory {_target.TerritoryId}");
            _state = State.Failed;
            return;
        }
        var parentTerritoryId = (aethernetName != null || requiresHousingEntry)
            ? Dalamud.GameData.GetExcelSheet<Aetheryte>()?.GetRow(aetheryteId).Territory.RowId ?? 0
            : 0;

        if (aethernetName != null
         && parentTerritoryId != 0
         && Dalamud.ClientState.TerritoryType == parentTerritoryId)
        {
            QueueLocalAethernetTeleport(aethernetName, parentTerritoryId);
            return;
        }

        if (Teleporter.Teleport(aetheryteId))
        {
            _teleportAttempted                  = true;
            _pendingAethernetName               = aethernetName;
            _pendingAethernetNeedsSourceApproach = false;
            _pendingHousingEntry                = requiresHousingEntry;
            _pendingAethernetParentTerritoryId  = parentTerritoryId;
            _stateStartTime       = DateTime.UtcNow;
            _state                = State.WaitingForTeleport;
            var followUp = aethernetName != null
                ? $", then aethernet to '{aethernetName}'"
                : requiresHousingEntry
                    ? ", then enter the housing ward from the city aetheryte"
                    : string.Empty;
            GatherBuddy.Log.Debug($"[VendorNavigator] Teleporting to aetheryte {aetheryteId}{followUp}");
        }
        else
        {
            GatherBuddy.Log.Error("[VendorNavigator] Teleport failed");
            _state = State.Failed;
        }
    }

    private void UpdateWaitingForTeleport()
    {
        if (_pendingAethernetName != null && Lifestream.Enabled && Lifestream.IsBusy()) return;
        if ((DateTime.UtcNow - _stateStartTime).TotalSeconds < TeleportCooldown) return;
        if (Dalamud.Conditions[ConditionFlag.BetweenAreas] || Dalamud.Conditions[ConditionFlag.BetweenAreas51]) return;
        if (_awaitingForcedHousingOriginExit)
        {
            if (Dalamud.ClientState.TerritoryType == _forcedHousingEntryOriginTerritoryId)
            {
                LogHousingEntryStatus($"[VendorNavigator] Waiting to leave original housing territory {_forcedHousingEntryOriginTerritoryId} before continuing the fresh city entry route");
                return;
            }

            _awaitingForcedHousingOriginExit = false;
        }
        if (_pendingAethernetName != null
         && _pendingAethernetNeedsSourceApproach
         && HandlePendingLocalAethernetTeleport())
            return;


        if (_pendingAethernetName != null && Lifestream.Enabled && !Lifestream.IsBusy())
        {
            // Only fire once we've confirmed arrival in the parent (city) territory
            if (_pendingAethernetParentTerritoryId != 0 &&
                Dalamud.ClientState.TerritoryType != _pendingAethernetParentTerritoryId)
                return;
            if (!CanIssueTeleportRequest())
                return;
            if (Lifestream.AethernetTeleport(_pendingAethernetName))
            {
                _pendingAethernetName              = null;
                _pendingAethernetNeedsSourceApproach = false;
                _pendingAethernetParentTerritoryId = 0;
                _stateStartTime                    = DateTime.UtcNow;
            }
            else
            {
                _stateStartTime = DateTime.UtcNow;
            }
            return;
        }

        if (IsEquivalentTerritory(Dalamud.ClientState.TerritoryType, _target!.TerritoryId))
        {
            _pendingAethernetName = null;
            _pendingAethernetNeedsSourceApproach = false;
            _pendingHousingEntry = false;
            _pendingAethernetParentTerritoryId = 0;
            _forcedHousingEntryOriginTerritoryId = 0;
            _state                = State.WaitingForZoneLoad;
            _stateStartTime       = DateTime.UtcNow;
            return;
        }
        if (_pendingHousingEntry)
        {
            if (_pendingAethernetParentTerritoryId != 0 &&
                Dalamud.ClientState.TerritoryType != _pendingAethernetParentTerritoryId)
                return;
            TryAdvanceHousingEntry();
            return;
        }

        if ((DateTime.UtcNow - _stateStartTime).TotalSeconds > 45)
        {
            GatherBuddy.Log.Error("[VendorNavigator] Teleport/aethernet timeout");
            _state = State.Failed;
        }
    }

    private void UpdateWaitingForZoneLoad()
    {
        if (Dalamud.Conditions[ConditionFlag.BetweenAreas] || Dalamud.Conditions[ConditionFlag.BetweenAreas51]) return;
        var pos = Dalamud.Objects.LocalPlayer?.Position ?? Vector3.Zero;
        if (pos == Vector3.Zero) return;
        if ((DateTime.UtcNow - _stateStartTime).TotalSeconds < ZoneLoadWait) return;
        if (!VNavmesh.Nav.IsReady()) return;
        var liveNpc = FindLiveNpcObject();
        var destination = GetDesiredNavigationDestination(liveNpc, out var usingLiveNpc, out var destinationMode, out var navigationReason);

        if (destinationMode != NavigationDestinationMode.SafeStagingPoint
         && HasReachedNavigationDestination(pos, liveNpc, destination, destinationMode))
        {
            GatherBuddy.Log.Information($"[VendorNavigator] Already near {GetDisplayName(liveNpc)} after zone load ({GetTargetDistance(pos, liveNpc):F1}m)");
            _state = State.ReadyToPurchase;
            return;
        }

        if (HandlePendingHousingShardTeleport())
            return;

        if (!_attemptedHousingShardTeleport
         && _target != null
         && TryGetHousingDistrict(_target.TerritoryId, out _)
         && Lifestream.Enabled
         && !Lifestream.IsBusy()
         && TryFindNearestShardInTerritory(_target.TerritoryId, _target.Position, out var shardName, out var shardDistanceToVendor, out var shardDistanceToPlayer))
        {
            if (shardDistanceToPlayer <= 10f)
            {
                _attemptedHousingShardTeleport = true;
            }
            else
            {
                _attemptedHousingShardTeleport      = true;
                _pendingHousingShardTeleportName    = shardName;
                _pendingHousingShardDistanceToVendor = shardDistanceToVendor;
                if (HandlePendingHousingShardTeleport())
                    return;
                _pendingHousingShardTeleportName     = null;
                _pendingHousingShardDistanceToVendor = 0f;
            }
        }

        if (TryPrepareCurrentTerritoryAethernetShortcut(pos))
            return;

        StartVNavmesh(destination, usingLiveNpc, destinationMode);
    }

    private bool HandlePendingHousingShardTeleport()
    {
        if (_pendingHousingShardTeleportName == null)
            return false;

        if (!TryGetNearestAethernetSource(out var sourceAetheryte, out var sourceHorizontalDistance, out var sourceDistance) || sourceAetheryte == null)
        {
            LogHousingEntryStatus("[VendorNavigator] Waiting for a residential aetheryte or shard source before housing aethernet teleport");
            return true;
        }

        var interactionDistance = GetAethernetSourceInteractionDistance(sourceAetheryte);
        var horizontalInteractionDistance = GetAethernetSourceHorizontalInteractionDistance(sourceAetheryte);
        if (!IsWithinAethernetSourceInteractionRange(sourceAetheryte, sourceHorizontalDistance, sourceDistance))
        {
            if (VNavmesh.Path.IsRunning() || VNavmesh.SimpleMove.PathfindInProgress())
                return true;

            if (!CanIssueHousingEntryAction())
                return true;

            var started = VNavmesh.SimpleMove.PathfindAndMoveTo(sourceAetheryte.Position, false);
            if (started)
            {
                _lastHousingEntryActionTime = DateTime.UtcNow;
                return true;
            }

            LogHousingEntryStatus($"[VendorNavigator] Failed to start movement toward residential aetheryte '{sourceAetheryte.Name.TextValue}' before housing aethernet teleport (horizontal={sourceHorizontalDistance:F1}m, distance={sourceDistance:F1}m, need <= {horizontalInteractionDistance:F1}m horizontal / {interactionDistance:F1}m distance)");
            return true;
        }

        if (VNavmesh.Path.IsRunning())
            VNavmesh.Path.Stop();

        if (Lifestream.Enabled && Lifestream.IsBusy())
            return true;

        if (!IsLifestreamAethernetSourceReady(sourceAetheryte, out var housingShardSourceState))
        {
            LogHousingEntryStatus($"[VendorNavigator] Waiting for Lifestream to recognize source '{sourceAetheryte.Name.TextValue}' before housing shard '{_pendingHousingShardTeleportName}' ({housingShardSourceState}, distance {sourceDistance:F1}m)");
            return true;
        }

        if (!CanIssueHousingEntryAction())
            return true;

        if (Lifestream.AethernetTeleport(_pendingHousingShardTeleportName))
        {
            GatherBuddy.Log.Debug($"[VendorNavigator] Using housing shard '{_pendingHousingShardTeleportName}' ({_pendingHousingShardDistanceToVendor:F1}m from vendor, horizontal={sourceHorizontalDistance:F1}m, distance={sourceDistance:F1}m from source aetheryte '{sourceAetheryte.Name.TextValue}', kind={sourceAetheryte.ObjectKind}, baseId={sourceAetheryte.BaseId})");
            _pendingHousingShardTeleportName     = null;
            _pendingHousingShardDistanceToVendor = 0f;
            _lastHousingEntryActionTime          = DateTime.UtcNow;
            _stateStartTime                      = DateTime.UtcNow;
            return true;
        }

        LogHousingEntryStatus($"[VendorNavigator] Waiting to use residential aetheryte '{sourceAetheryte.Name.TextValue}' for housing shard '{_pendingHousingShardTeleportName}'");
        return true;
    }

    private bool HandlePendingLocalAethernetTeleport()
    {
        if (_pendingAethernetName == null || !Lifestream.Enabled)
            return false;

        if (_pendingAethernetParentTerritoryId != 0
         && Dalamud.ClientState.TerritoryType != _pendingAethernetParentTerritoryId)
            return true;

        if (!TryGetNearestAethernetSource(out var sourceAetheryte, out var sourceHorizontalDistance, out var sourceDistance) || sourceAetheryte == null)
        {
            LogHousingEntryStatus("[VendorNavigator] Waiting for a city aetheryte or shard source before local aethernet teleport");
            return true;
        }

        var interactionDistance = GetAethernetSourceInteractionDistance(sourceAetheryte);
        var horizontalInteractionDistance = GetAethernetSourceHorizontalInteractionDistance(sourceAetheryte);
        if (!IsWithinAethernetSourceInteractionRange(sourceAetheryte, sourceHorizontalDistance, sourceDistance))
        {
            if (VNavmesh.Path.IsRunning() || VNavmesh.SimpleMove.PathfindInProgress())
                return true;

            if (!CanIssueTeleportRequest())
                return true;

            var started = VNavmesh.SimpleMove.PathfindAndMoveTo(sourceAetheryte.Position, false);
            if (started)
            {
                return true;
            }

            LogHousingEntryStatus($"[VendorNavigator] Failed to start movement toward local aetheryte source '{sourceAetheryte.Name.TextValue}' before aethernet to '{_pendingAethernetName}' (horizontal={sourceHorizontalDistance:F1}m, distance={sourceDistance:F1}m, need <= {horizontalInteractionDistance:F1}m horizontal / {interactionDistance:F1}m distance)");
            return true;
        }

        if (VNavmesh.Path.IsRunning())
            VNavmesh.Path.Stop();

        if (Lifestream.IsBusy() || !CanIssueTeleportRequest())
            return true;

        if (!IsLifestreamAethernetSourceReady(sourceAetheryte, out var localAethernetSourceState))
        {
            LogHousingEntryStatus($"[VendorNavigator] Waiting for Lifestream to recognize source '{sourceAetheryte.Name.TextValue}' before aethernet to '{_pendingAethernetName}' ({localAethernetSourceState}, distance {sourceDistance:F1}m)");
            return true;
        }
        if (Lifestream.AethernetTeleport(_pendingAethernetName))
        {
            _pendingAethernetName                = null;
            _pendingAethernetNeedsSourceApproach = false;
            _pendingAethernetParentTerritoryId   = 0;
            _stateStartTime                      = DateTime.UtcNow;
        }
        else
        {
            _stateStartTime = DateTime.UtcNow;
        }

        return true;
    }

    private bool TryPrepareCurrentTerritoryAethernetShortcut(Vector3 playerPosition)
    {
        if (_target == null || !Lifestream.Enabled)
            return false;

        if (TryGetHousingDistrict(_target.TerritoryId, out _))
            return false;

        var directDistanceToVendor = GetHorizontalDistance(playerPosition, _target.Position);
        if (!TryFindNearestShardInTerritory(_target.TerritoryId, _target.Position, out var shardName, out var shardDistanceToVendor, out var shardDistanceToPlayer))
            return false;

        if (shardDistanceToPlayer <= LocalAethernetDestinationProximityThreshold)
            return false;

        var sourceDistance         = AetheryteSourceInteractionDistance;
        var sourceApproachDistance = AetheryteSourceInteractionDistance;
        if (TryGetNearestAethernetSource(out var sourceAetheryte, out var sourceHorizontalDistance, out sourceDistance) && sourceAetheryte != null)
            sourceApproachDistance = GetAethernetSourceApproachDistance(sourceAetheryte, sourceHorizontalDistance, sourceDistance);
        var alreadyAtSource = sourceApproachDistance <= 2f;
        var estimatedInteractionCost = alreadyAtSource ? 0f : LocalAethernetEstimatedInteractionCost;
        var requiredSavings          = alreadyAtSource ? 0f : LocalAethernetMinimumSavings;
        var estimatedShortcutDistance = sourceApproachDistance + shardDistanceToVendor + estimatedInteractionCost;
        var estimatedSavings          = directDistanceToVendor - estimatedShortcutDistance;
        if (estimatedSavings < requiredSavings)
            return false;

        QueueLocalAethernetTeleport(shardName, _target.TerritoryId);
        GatherBuddy.Log.Debug($"[VendorNavigator] Using local aethernet to '{shardName}' for {_target.NpcName}: direct={directDistanceToVendor:F1}m, sourceApproach={sourceApproachDistance:F1}m, post-teleport={shardDistanceToVendor:F1}m, interactionCost={estimatedInteractionCost:F1}m, estimatedSavings={estimatedSavings:F1}m");
        return true;
    }

    private void QueueLocalAethernetTeleport(string aethernetName, uint parentTerritoryId)
    {
        _teleportAttempted                  = true;
        _pendingAethernetName               = aethernetName;
        _pendingAethernetNeedsSourceApproach = true;
        _pendingHousingEntry                = false;
        _pendingHousingShardTeleportName    = null;
        _pendingHousingShardDistanceToVendor = 0f;
        _pendingAethernetParentTerritoryId  = parentTerritoryId;
        _stateStartTime                     = DateTime.UtcNow - TimeSpan.FromSeconds(TeleportCooldown);
        _state                              = State.WaitingForTeleport;
    }

    private void StartVNavmesh(Vector3 destination, bool usingLiveNpc, NavigationDestinationMode destinationMode)
    {
        var player = Dalamud.Objects.LocalPlayer;
        if (player == null)
        {
            GatherBuddy.Log.Warning("[VendorNavigator] Cannot start navigation without a local player");
            return;
        }

        StopPathing();

        _state                    = State.Navigating;
        _navigationDestination    = destination;
        _hasNavigationDestination = true;
        _usingLiveNpcDestination  = usingLiveNpc;
        _navigationDestinationTolerance = 0f;
        _lastRepathTime           = DateTime.UtcNow;
        _waitingForMount          = false;
        _landingWaypointCount     = 0;

        var forceGroundApproach = IsGroundApproachStabilizing();
        var liveNpc = FindLiveNpcObject();
        var allowCloseRangeLiveNpcFlight = ShouldUseCloseRangeLiveNpcApproach(liveNpc, usingLiveNpc);
        var canMount = !forceGroundApproach
            && (!usingLiveNpc || allowCloseRangeLiveNpcFlight)
            && CanMountForDestination(destination);
        var shouldFly = !forceGroundApproach
            && (!usingLiveNpc || allowCloseRangeLiveNpcFlight)
            && ShouldFly(destination);
        _pathUsesFlight = shouldFly && (canMount || Dalamud.Conditions[ConditionFlag.Mounted]);
        _pathUsesFlight |= Dalamud.Conditions[ConditionFlag.Diving];
        _pathUsesCombinedApproach = false;
        _mountingUp = _pathUsesFlight && !Dalamud.Conditions[ConditionFlag.Mounted] && !Dalamud.Conditions[ConditionFlag.Diving];

        if (!Dalamud.Conditions[ConditionFlag.Mounted] && canMount)
        {
            if (CanRetryMountAttempt())
                TryMountUp();

            if (!GatherBuddy.Config.AutoGatherConfig.MoveWhileMounting)
            {
                _waitingForMount = true;
                return;
            }
        }

        var destinationTolerance = GetPathfindTolerance(liveNpc, destinationMode);
        var landingDistance = GetVendorLandingDistance();
        var combinedPathTarget = destination;
        if (allowCloseRangeLiveNpcFlight && liveNpc != null)
        {
            combinedPathTarget = liveNpc.Position;
            landingDistance = GetCloseRangeCombinedLandingDistance(GetHorizontalDistance(player.Position, liveNpc.Position));
        }
        if (_pathUsesFlight
         && landingDistance > 0f
         && !Dalamud.Conditions[ConditionFlag.Diving]
         && (destinationMode == NavigationDestinationMode.SafeStagingPoint || allowCloseRangeLiveNpcFlight))
        {
            _pathUsesCombinedApproach   = true;
            _pathCancellationTokenSource = new CancellationTokenSource();
            _pathTask = FindCombinedVendorPath(player.Position, combinedPathTarget, landingDistance, Dalamud.Conditions[ConditionFlag.InFlight], _pathCancellationTokenSource.Token);
            return;
        }
        if (!_pathUsesFlight && usingLiveNpc && liveNpc != null && destinationTolerance > 0f)
        {
            _navigationDestinationTolerance = destinationTolerance;
            _pathTask = VNavmesh.Nav.PathfindWithTolerance(player.Position, liveNpc.Position, false, destinationTolerance);
            return;
        }

        _pathCancellationTokenSource = new CancellationTokenSource();
        _pathTask = VNavmesh.Nav.PathfindCancelable(player.Position, destination, _pathUsesFlight, _pathCancellationTokenSource.Token);
    }

    private void UpdateNavigating()
    {
        var playerPos = Dalamud.Objects.LocalPlayer?.Position ?? Vector3.Zero;
        if (playerPos == Vector3.Zero) return;

        if (!IsEquivalentTerritory(Dalamud.ClientState.TerritoryType, _target!.TerritoryId))
        {
            GatherBuddy.Log.Warning("[VendorNavigator] Unexpected territory change during navigation");
            _state             = State.Teleporting;
            _teleportAttempted = false;
            return;
        }

        var liveNpc = FindLiveNpcObject();
        if (liveNpc != null)
            TryTargetLiveNpc(liveNpc);
        var destination = GetDesiredNavigationDestination(liveNpc, out var usingLiveNpc, out var destinationMode, out var navigationReason);
        var dist = GetTargetDistance(playerPos, liveNpc);
        if (usingLiveNpc
         && destinationMode == NavigationDestinationMode.FinalApproachPoint
         && Dalamud.Conditions[ConditionFlag.Mounted]
         && !Dalamud.Conditions[ConditionFlag.InFlight]
         && !Dalamud.Conditions[ConditionFlag.Diving])
        {
            if (CanRetryLandingAttempt())
                TryLand();
            return;
        }

        if (HasReachedNavigationDestination(playerPos, liveNpc, destination, destinationMode))
        {
            if (destinationMode == NavigationDestinationMode.SafeStagingPoint)
            {
                StopPathing();
                if (Dalamud.Conditions[ConditionFlag.InFlight] || Dalamud.Conditions[ConditionFlag.Diving] || Dalamud.Conditions[ConditionFlag.Mounted])
                {
                    if (CanRetryLandingAttempt())
                        TryLand();
                }
                else
                {
                    if (usingLiveNpc && liveNpc != null)
                    {
                        _forceInteractionRangeApproachUntil = DateTime.UtcNow.AddSeconds(VendorInteractionRangeFallbackDurationSeconds);
                        GatherBuddy.Log.Debug($"[VendorNavigator] Reached live NPC staging point near {GetDisplayName(liveNpc)}, falling back to direct interaction-range navigation");
                        StartVNavmesh(liveNpc.Position, true, NavigationDestinationMode.TargetInteractionRange);
                        return;
                    }
                }
                ClearGroundApproachProgress();
                return;
            }
            if (Dalamud.Conditions[ConditionFlag.InFlight] || Dalamud.Conditions[ConditionFlag.Diving])
            {
                if (VNavmesh.Path.IsRunning() || _pathTask != null)
                    return;
                StopPathing();
                if (CanRetryLandingAttempt())
                    TryLand();
                return;
            }

            StopPathing();
            GatherBuddy.Log.Information($"[VendorNavigator] Arrived at {GetDisplayName(liveNpc)} ({dist:F1}m)");
            _state = State.ReadyToPurchase;
            return;
        }

        if (ShouldRepath(destination, usingLiveNpc))
        {
            StartVNavmesh(destination, usingLiveNpc, destinationMode);
            return;
        }
        if (HandleMovementMode(playerPos, destination, usingLiveNpc, destinationMode))
            return;
        if (TryRecoverStalledGroundApproach(playerPos, liveNpc, destination, usingLiveNpc, destinationMode, navigationReason))
            return;

        HandlePathfinding();
        if (_state != State.Navigating || _waitingForMount)
            return;

        if (!VNavmesh.Path.IsRunning() && _pathTask == null)
            StartVNavmesh(destination, usingLiveNpc, destinationMode);
    }

    private IGameObject? FindLiveNpcObject()
    {
        if (_target == null || !IsEquivalentTerritory(Dalamud.ClientState.TerritoryType, _target.TerritoryId))
            return null;

        IGameObject? bestObject = null;
        var bestDistance = float.MaxValue;
        foreach (var obj in Dalamud.Objects)
        {
            if (obj.ObjectKind != ObjectKind.EventNpc || obj.BaseId != _target.NpcId || !obj.IsTargetable)
                continue;

            var distance = Vector3.DistanceSquared(obj.Position, _target.Position);
            if (distance >= bestDistance)
                continue;

            bestDistance = distance;
            bestObject   = obj;
        }

        return bestObject;
    }

    private unsafe void TryTargetLiveNpc(IGameObject liveNpc)
    {
        var targetSystem = TargetSystem.Instance();
        if (targetSystem == null)
            return;

        var target = (GameObjectStruct*)liveNpc.Address;
        if (targetSystem->Target == target)
            return;

        targetSystem->Target = target;
    }

    private Vector3 GetDesiredNavigationDestination(IGameObject? liveNpc, out bool usingLiveNpc, out NavigationDestinationMode destinationMode, out string navigationReason)
    {
        usingLiveNpc   = ShouldUseLiveNpcDestination(liveNpc);
        destinationMode = NavigationDestinationMode.TargetInteractionRange;
        Vector3 destination;
        if (usingLiveNpc && liveNpc != null)
        {
            if (ShouldUseCloseRangeLiveNpcApproach(liveNpc, usingLiveNpc))
            {
                navigationReason = "close live NPC direct approach";
                destination = liveNpc.Position;
            }
            else if (DateTime.UtcNow < _forceInteractionRangeApproachUntil)
            {
                navigationReason = "live NPC interaction range fallback";
                destination = liveNpc.Position;
            }
            else if (TryGetVendorFinalApproachDestination(liveNpc, out destination, out navigationReason))
            {
                destinationMode = NavigationDestinationMode.FinalApproachPoint;
            }
            else if (TryGetVendorCloseApproachDestination(liveNpc, out destination, out navigationReason))
            {
                destinationMode = GetHorizontalDistance(destination, liveNpc.Position) <= LiveNpcInteractionDistance
                    ? NavigationDestinationMode.FinalApproachPoint
                    : NavigationDestinationMode.SafeStagingPoint;
            }
            else
            {
                navigationReason = "live NPC position fallback";
                destination = GetLiveNpcFallbackDestination(liveNpc);
            }
        }
        else if (!usingLiveNpc && TryGetVendorStagingDestination(liveNpc, out destination, out navigationReason))
        {
            destinationMode = NavigationDestinationMode.SafeStagingPoint;
        }
        else
        {
            navigationReason = usingLiveNpc
                ? liveNpc != null
                    ? "live NPC position fallback"
                    : "cached vendor location"
                : liveNpc != null
                    ? "live NPC position fallback"
                    : "cached vendor location";
            destination = usingLiveNpc && liveNpc != null
                ? GetLiveNpcFallbackDestination(liveNpc)
                : liveNpc != null
                    ? GetLiveNpcFallbackDestination(liveNpc)
                    : SnapToMesh(_target!.Position);
        }

        if (_usingLiveNpcDestination != usingLiveNpc)
        {
            GatherBuddy.Log.Debug(usingLiveNpc
                ? $"[VendorNavigator] Switching to final ground approach for {_target!.NpcName} using {navigationReason} at {destination}"
                : liveNpc != null
                    ? $"[VendorNavigator] Using {navigationReason} for {_target!.NpcName} until final ground approach (npc={liveNpc.Position}, rotation={liveNpc.Rotation:F2})"
                    : $"[VendorNavigator] Live NPC object unavailable for {_target!.NpcName}, falling back to cached location");
        }

        return destination;
    }

    private bool ShouldUseLiveNpcDestination(IGameObject? liveNpc)
    {
        if (liveNpc == null)
            return false;

        var playerPosition = Dalamud.Objects.LocalPlayer?.Position ?? Vector3.Zero;
        if (playerPosition == Vector3.Zero)
            return false;
        var shouldUseCloseRangeDirectApproach = ShouldUseCloseRangeLiveNpcApproach(liveNpc, true);
        if ((_pathUsesFlight || _pathUsesCombinedApproach) && !shouldUseCloseRangeDirectApproach)
            return false;

        if (_hasNavigationDestination
         && !_usingLiveNpcDestination
         && !shouldUseCloseRangeDirectApproach
         && GetHorizontalDistance(playerPosition, _navigationDestination) > VendorStagingPointArrivalDistance)
            return false;
        return shouldUseCloseRangeDirectApproach
            || GetHorizontalDistance(playerPosition, liveNpc.Position) <= VendorGroundApproachDistance + VendorStagingPointArrivalDistance;
    }

    private bool IsGroundApproachStabilizing()
        => DateTime.UtcNow < _forceGroundApproachUntil;

    private bool ShouldRepath(Vector3 destination, bool usingLiveNpc)
    {
        if (!_hasNavigationDestination)
            return true;

        if (_usingLiveNpcDestination != usingLiveNpc)
            return true;
        if (!_waitingForMount && _pathUsesFlight != ShouldUseFlightPath(destination, usingLiveNpc))
            return true;

        return Vector3.DistanceSquared(_navigationDestination, destination) >= RepathDistanceThreshold * RepathDistanceThreshold
            && (DateTime.UtcNow - _lastRepathTime).TotalSeconds >= RepathCooldown;
    }

    private bool IsWithinArrivalDistance(Vector3 playerPosition, IGameObject? liveNpc)
        => GetTargetDistance(playerPosition, liveNpc) <= (liveNpc != null ? LiveNpcInteractionDistance : CachedInteractionDistance);

    private bool HasReachedNavigationDestination(Vector3 playerPosition, IGameObject? liveNpc, Vector3 destination, NavigationDestinationMode destinationMode)
    {
        return destinationMode switch
        {
            NavigationDestinationMode.SafeStagingPoint =>
                liveNpc != null && _navigationDestinationTolerance > 0f
                    ? GetHorizontalDistance(playerPosition, liveNpc.Position) <= _navigationDestinationTolerance + VendorStagingPointArrivalDistance
                    : GetHorizontalDistance(playerPosition, destination) <= VendorStagingPointArrivalDistance,
            NavigationDestinationMode.FinalApproachPoint =>
                _navigationDestinationTolerance > 0f
                    ? GetHorizontalDistance(playerPosition, liveNpc?.Position ?? _target!.Position) <= LiveNpcInteractionDistance
                    : GetHorizontalDistance(playerPosition, liveNpc?.Position ?? _target!.Position) <= LiveNpcInteractionDistance
                      && GetHorizontalDistance(playerPosition, destination) <= VendorApproachCompletionDistance,
            _ => IsWithinArrivalDistance(playerPosition, liveNpc),
        };
    }

    private float GetTargetDistance(Vector3 playerPosition, IGameObject? liveNpc)
        => Vector3.Distance(playerPosition, liveNpc?.Position ?? _target!.Position);

    private string GetDisplayName(IGameObject? liveNpc)
        => liveNpc?.Name.TextValue ?? _target?.NpcName ?? "vendor";
    private bool HandleMovementMode(Vector3 playerPosition, Vector3 destination, bool usingLiveNpc, NavigationDestinationMode destinationMode)
    {
        if (_waitingForMount)
        {
            if (Dalamud.Conditions[ConditionFlag.Mounted] || !CanMountForDestination(destination))
            {
                StartVNavmesh(destination, usingLiveNpc, destinationMode);
                return true;
            }
            if (CanRetryMountAttempt())
                TryMountUp();
            return true;
        }

        if (_pathUsesFlight && _mountingUp && Dalamud.Conditions[ConditionFlag.Mounted])
        {
            _mountingUp = false;

            if (VNavmesh.Path.IsRunning())
            {
                var playerXZ = new Vector2(playerPosition.X, playerPosition.Z);
                var path = VNavmesh.Path.ListWaypoints()
                    .SkipWhile(p => Vector2.DistanceSquared(playerXZ, new Vector2(p.X, p.Z)) < 16f)
                    .ToList();

                if (path.Count == 0)
                {
                    StartVNavmesh(destination, usingLiveNpc, destinationMode);
                    return true;
                }

                VNavmesh.Path.Stop();
                VNavmesh.Path.MoveTo(path, true);
                return true;
            }
        }

        if (_pathUsesFlight && !_mountingUp && !Dalamud.Conditions[ConditionFlag.Mounted] && !Dalamud.Conditions[ConditionFlag.InFlight]
         && !Dalamud.Conditions[ConditionFlag.Diving])
        {
            GatherBuddy.Log.Debug("[VendorNavigator] Lost mounted state during flight navigation, recalculating path");
            StartVNavmesh(destination, usingLiveNpc, destinationMode);
            return true;
        }

        return false;
    }

    private void HandlePathfinding()
    {
        if (_pathUsesCombinedApproach && _landingWaypointCount > 0 && !Dalamud.Conditions[ConditionFlag.Diving])
        {
            var remainingPath = VNavmesh.Path.ListWaypoints().ToList();
            if (remainingPath.Count < _landingWaypointCount)
            {
                VNavmesh.Path.Stop();
                if (remainingPath.Count > 0)
                    VNavmesh.Path.MoveTo(remainingPath, false);

                _pathUsesFlight           = false;
                _pathUsesCombinedApproach = false;
                _landingWaypointCount     = 0;
                _forceGroundApproachUntil = DateTime.UtcNow.AddSeconds(VendorGroundApproachStabilizationSeconds);
                if (CanRetryLandingAttempt())
                    TryLand();
                return;
            }
        }
        if (_pathTask == null || !_pathTask.IsCompleted)
            return;

        List<Vector3> path;
        try
        {
            path = _pathTask.Result;
        }
        catch (Exception) when (_pathCancellationTokenSource != null && _pathCancellationTokenSource.IsCancellationRequested)
        {
            ClearPathTask();
            return;
        }
        catch (Exception ex)
        {
            ClearPathTask();
            GatherBuddy.Log.Error($"[VendorNavigator] Pathfinding task failed: {ex.Message}");
            StopPathing();
            _state = State.Failed;
            return;
        }

        ClearPathTask();
        if (path.Count == 0)
        {
            if (_pathUsesCombinedApproach && _pathUsesFlight)
            {
                var player = Dalamud.Objects.LocalPlayer;
                if (player != null)
                {
                    _pathUsesCombinedApproach   = false;
                    _landingWaypointCount       = 0;
                    _pathCancellationTokenSource = new CancellationTokenSource();
                    _pathTask = VNavmesh.Nav.PathfindCancelable(player.Position, _navigationDestination, true, _pathCancellationTokenSource.Token);
                    GatherBuddy.Log.Debug($"[VendorNavigator] Combined pathfinding failed for {_navigationDestination}, falling back to direct flight path");
                    return;
                }
            }
            GatherBuddy.Log.Error($"[VendorNavigator] VNavmesh failed to find a path to {_navigationDestination}");
            StopPathing();
            _state = State.Failed;
            return;
        }

        if (_pathUsesCombinedApproach && path.Count > 1)
        {
            var landingWaypoint = path[^1];
            path.RemoveAt(path.Count - 1);
            var landingIndex = path.FindLastIndex(point => point == landingWaypoint);
            _landingWaypointCount = landingIndex >= 0 ? System.Math.Max(1, path.Count - landingIndex) : 1;
        }
        else
        {
            _landingWaypointCount = 0;
        }

        if (VNavmesh.Path.IsRunning())
            RemovePassedWaypoints(path);

        if (path.Count == 0)
        {
            GatherBuddy.Log.Warning("[VendorNavigator] Path resolved with no remaining waypoints after pruning, recalculating");
            return;
        }

        VNavmesh.Path.Stop();
        VNavmesh.Path.MoveTo(path, _pathUsesFlight && !_mountingUp);
    }

    private void StopPathing()
    {
        if (VNavmesh.Path.IsRunning())
            VNavmesh.Path.Stop();

        _landingWaypointCount     = 0;
        _pathUsesCombinedApproach = false;
        _pathUsesFlight           = false;
        _mountingUp               = false;
        _waitingForMount          = false;
        _navigationDestinationTolerance = 0f;
        ClearGroundApproachProgress();
        if (_pathCancellationTokenSource != null)
            _pathCancellationTokenSource.Cancel();
        ClearPathTask();
    }

    private void ClearPathTask()
    {
        _pathTask = null;
        _pathCancellationTokenSource?.Dispose();
        _pathCancellationTokenSource = null;
    }

    private unsafe bool TryAdvanceHousingEntry()
    {
        if (_target == null || !TryGetHousingDistrict(_target.TerritoryId, out _))
            return false;

        if (!GenericHelpers.IsScreenReady())
            return false;

        if (TryConfirmHousingTravel())
            return true;

        if (TryConfirmHousingWardSelection())
            return true;

        if (TrySelectHousingAetheryteMenuEntry(GoToWardMenuEntries))
            return true;
        if (TrySelectHousingAetheryteMenuEntry(ResidentialDistrictMenuEntries))
            return true;

        if (TryInteractWithHousingEntryAetheryte())
            return true;
        if (TryMoveToHousingEntryAetheryte())
            return true;

        LogHousingEntryStatus("[VendorNavigator] Waiting for city aetheryte/menu state to enter the housing ward");

        return false;
    }

    private unsafe bool TryConfirmHousingTravel()
    {
        if (!GenericHelpers.TryGetAddonByName<AddonSelectYesno>("SelectYesno", out var addon)
         || !addon->AtkUnitBase.IsVisible
         || addon->YesButton == null
         || !MatchesAnyText(ReadSelectYesnoPrompt(addon), HousingTravelConfirmationTexts))
            return false;

        if (!CanIssueHousingEntryAction())
            return false;

        new AddonMaster.SelectYesno((nint)addon).Yes();
        _lastHousingEntryActionTime = DateTime.UtcNow;
        return true;
    }

    private unsafe bool TryConfirmHousingWardSelection()
    {
        if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("HousingSelectBlock", out var addon) || !addon->IsVisible)
            return false;

        var button = addon->GetComponentButtonById(34);
        if (button == null || !button->IsEnabled || button->AtkComponentBase.OwnerNode == null || !button->AtkComponentBase.OwnerNode->AtkResNode.IsVisible())
            return false;

        if (!CanIssueHousingEntryAction())
            return false;

        ClickButton(addon, button);
        _lastHousingEntryActionTime = DateTime.UtcNow;
        return true;
    }

    private unsafe bool TrySelectHousingAetheryteMenuEntry(IReadOnlyList<string> entries)
    {
        if (!GenericHelpers.TryGetAddonByName<AddonSelectString>("SelectString", out var addon) || !addon->AtkUnitBase.IsVisible)
            return false;

        for (var index = 0; index < addon->PopupMenu.PopupMenu.EntryCount; index++)
        {
            var label = MemoryHelper.ReadSeStringNullTerminated((nint)addon->PopupMenu.PopupMenu.EntryNames[index].Value).TextValue.Trim();
            if (!MatchesAnyText(label, entries))
                continue;

            if (!CanIssueHousingEntryAction())
                return false;

            new AddonMaster.SelectString((nint)addon).Entries[index].Select();
            _lastHousingEntryActionTime = DateTime.UtcNow;
            return true;
        }

        return false;
    }

    private unsafe bool TryInteractWithHousingEntryAetheryte()
    {
        if (!TryGetNearestHousingEntryAetheryte(out var aetheryte, out var horizontalDistance, out var distance)
         || aetheryte == null
         || horizontalDistance >= 11f
         || distance >= 15f)
            return false;

        if (!CanIssueHousingEntryAction())
            return false;

        var targetSystem = TargetSystem.Instance();
        if (targetSystem == null)
            return false;
        if (VNavmesh.Path.IsRunning())
            VNavmesh.Path.Stop();

        targetSystem->Target = (GameObjectStruct*)aetheryte.Address;
        targetSystem->OpenObjectInteraction((GameObjectStruct*)aetheryte.Address);
        _lastHousingEntryActionTime = DateTime.UtcNow;
        return true;
    }

    private bool TryMoveToHousingEntryAetheryte()
    {
        if (!TryGetNearestHousingEntryAetheryte(out var aetheryte, out var horizontalDistance, out var distance) || aetheryte == null)
        {
            LogHousingEntryStatus("[VendorNavigator] Waiting for the city aetheryte object to become available for housing entry");
            return false;
        }

        if (horizontalDistance < 11f && distance < 15f)
            return false;

        if (VNavmesh.Path.IsRunning() || VNavmesh.SimpleMove.PathfindInProgress())
            return true;

        if (!CanIssueHousingEntryAction())
            return false;

        var started = VNavmesh.SimpleMove.PathfindAndMoveTo(aetheryte.Position, false);
        if (started)
        {
            _lastHousingEntryActionTime = DateTime.UtcNow;
            return true;
        }

        LogHousingEntryStatus($"[VendorNavigator] Failed to start movement toward city aetheryte '{aetheryte.Name.TextValue}' ({distance:F1}m)");
        return false;
    }

    private bool TryGetNearestHousingEntryAetheryte(out IGameObject? aetheryte, out float horizontalDistance, out float distance)
    {
        aetheryte          = null;
        horizontalDistance = float.MaxValue;
        distance           = float.MaxValue;

        var player = Dalamud.Objects.LocalPlayer;
        if (player == null)
            return false;

        var nearest = Dalamud.Objects
            .Where(obj => obj.ObjectKind == ObjectKind.Aetheryte && obj.IsTargetable)
            .Select(obj => (Object: obj, HorizontalDistance: Vector2.Distance(new Vector2(player.Position.X, player.Position.Z), new Vector2(obj.Position.X, obj.Position.Z)), Distance: Vector3.Distance(player.Position, obj.Position)))
            .OrderBy(entry => entry.Distance)
            .FirstOrDefault();

        if (nearest.Object == null)
            return false;

        aetheryte          = nearest.Object;
        horizontalDistance = nearest.HorizontalDistance;
        distance           = nearest.Distance;
        return true;
    }

    private bool TryGetNearestAethernetSource(out IGameObject? aetheryte, out float horizontalDistance, out float distance)
    {
        aetheryte          = null;
        horizontalDistance = float.MaxValue;
        distance           = float.MaxValue;

        var player = Dalamud.Objects.LocalPlayer;
        if (player == null)
            return false;

        var nearest = Dalamud.Objects
            .Where(IsAethernetSource)
            .Select(obj => (Object: obj, HorizontalDistance: Vector2.Distance(new Vector2(player.Position.X, player.Position.Z), new Vector2(obj.Position.X, obj.Position.Z)), Distance: Vector3.Distance(player.Position, obj.Position)))
            .OrderBy(entry => entry.Distance)
            .FirstOrDefault();

        if (nearest.Object == null)
            return false;

        aetheryte          = nearest.Object;
        horizontalDistance = nearest.HorizontalDistance;
        distance           = nearest.Distance;
        return true;
    }

    private static float GetAethernetSourceInteractionDistance(IGameObject source)
        => source.ObjectKind == ObjectKind.Aetheryte
            ? AetheryteSourceInteractionDistance
            : AethernetShardInteractionDistance;

    private static float GetAethernetSourceHorizontalInteractionDistance(IGameObject source)
        => source.ObjectKind == ObjectKind.Aetheryte
            ? AetheryteSourceHorizontalInteractionDistance
            : AethernetShardInteractionDistance;

    private static bool IsWithinAethernetSourceInteractionRange(IGameObject source, float horizontalDistance, float distance)
        => horizontalDistance <= GetAethernetSourceHorizontalInteractionDistance(source)
        && distance <= GetAethernetSourceInteractionDistance(source);

    private static float GetAethernetSourceApproachDistance(IGameObject source, float horizontalDistance, float distance)
        => MathF.Max(
            0f,
            MathF.Max(
                horizontalDistance - GetAethernetSourceHorizontalInteractionDistance(source),
                distance - GetAethernetSourceInteractionDistance(source)));

    private static bool IsAethernetSource(IGameObject obj)
        => obj.IsTargetable
        && (obj.ObjectKind == ObjectKind.Aetheryte
         || (obj.ObjectKind == ObjectKind.EventObj && IsKnownAethernetShardDataId(obj.BaseId)));

    private static bool IsKnownAethernetShardDataId(uint dataId)
        => GetKnownAethernetShardDataIds().Contains(dataId);

    private static bool IsLifestreamAethernetSourceReady(IGameObject source, out string state)
    {
        var activeAetheryteId            = Lifestream.ActiveAetheryteId;
        var activeCustomAetheryteId      = Lifestream.ActiveCustomAetheryteId;
        var activeResidentialAetheryteId = Lifestream.ActiveResidentialAetheryteId;

        var sourceRecognized = source.ObjectKind == ObjectKind.EventObj
            ? activeResidentialAetheryteId != 0 || activeCustomAetheryteId != 0
            : activeAetheryteId != 0 || activeResidentialAetheryteId != 0 || activeCustomAetheryteId != 0;

        state = $"active={activeAetheryteId}, residential={activeResidentialAetheryteId}, custom={activeCustomAetheryteId}";
        return sourceRecognized;
    }

    private static HashSet<uint> GetKnownAethernetShardDataIds()
    {
        if (_knownAethernetShardDataIds != null)
            return _knownAethernetShardDataIds;

        var shardNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dataIds    = new HashSet<uint>(KnownAethernetShardSeedDataIds);
        var sheet      = Dalamud.GameData.GetExcelSheet<EObjName>();

        CollectAethernetShardSeedNames(sheet, shardNames);
        CollectMatchingAethernetShardDataIds(sheet, shardNames, dataIds);

        try
        {
            var englishSheet = Dalamud.GameData.GetExcelSheet<EObjName>(ClientLanguage.English);
            CollectAethernetShardSeedNames(englishSheet, shardNames);
            CollectMatchingAethernetShardDataIds(englishSheet, shardNames, dataIds);
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Debug($"[VendorNavigator] Failed to load English EObjName sheet while building residential shard ids: {ex.Message}");
        }

        _knownAethernetShardDataIds = dataIds;
        return _knownAethernetShardDataIds;
    }

    private static void CollectAethernetShardSeedNames(ExcelSheet<EObjName>? sheet, HashSet<string> shardNames)
    {
        if (sheet == null)
            return;

        foreach (var rowId in KnownAethernetShardSeedDataIds)
        {
            if (!sheet.TryGetRow(rowId, out var row))
                continue;

            var name = row.Singular.ExtractText().Trim();
            if (!string.IsNullOrWhiteSpace(name))
                shardNames.Add(name);
        }
    }

    private static void CollectMatchingAethernetShardDataIds(ExcelSheet<EObjName>? sheet, HashSet<string> shardNames, HashSet<uint> dataIds)
    {
        if (sheet == null || shardNames.Count == 0)
            return;

        foreach (var row in sheet)
        {
            var name = row.Singular.ExtractText().Trim();
            if (!string.IsNullOrWhiteSpace(name) && shardNames.Contains(name))
                dataIds.Add(row.RowId);
        }
    }

    private bool CanIssueHousingEntryAction()
        => (DateTime.UtcNow - _lastHousingEntryActionTime).TotalSeconds >= 0.5;

    private void LogHousingEntryStatus(string message)
    {
        if ((DateTime.UtcNow - _lastHousingEntryStatusLogTime).TotalSeconds < 2)
            return;

        _lastHousingEntryStatusLogTime = DateTime.UtcNow;
        GatherBuddy.Log.Debug(message);
    }

    private static unsafe string ReadSelectYesnoPrompt(AddonSelectYesno* addon)
        => addon->PromptText == null
            ? string.Empty
            : addon->PromptText->NodeText.ToString();

    private static bool MatchesAnyText(string text, IReadOnlyList<string> candidates)
        => !string.IsNullOrWhiteSpace(text)
         && candidates.Any(candidate => text.Contains(candidate, StringComparison.OrdinalIgnoreCase));

    private static unsafe void ClickButton(AtkUnitBase* addon, AtkComponentButton* button)
    {
        var buttonNode = button->AtkComponentBase.OwnerNode;
        if (addon == null || buttonNode == null)
            return;

        var eventPointer = buttonNode->AtkResNode.AtkEventManager.Event;
        if (eventPointer == null)
            return;

        var atkEvent = (AtkEvent*)eventPointer;
        addon->ReceiveEvent(atkEvent->State.EventType, (int)atkEvent->Param, eventPointer);
    }

    private bool ShouldUseFlightPath(Vector3 destination, bool usingLiveNpc)
    {
        if (IsGroundApproachStabilizing())
            return false;

        if (usingLiveNpc && !ShouldUseCloseRangeLiveNpcApproach(FindLiveNpcObject(), true))
            return false;
        var shouldFly = ShouldFly(destination);
        shouldFly &= CanMountForDestination(destination) || Dalamud.Conditions[ConditionFlag.Mounted];
        shouldFly |= Dalamud.Conditions[ConditionFlag.Diving];
        return shouldFly;
    }

    private bool CanMountForDestination(Vector3 destination)
    {
        var playerPosition = Dalamud.Objects.LocalPlayer?.Position ?? Vector3.Zero;
        if (playerPosition == Vector3.Zero)
            return false;

        var playerXZ = new Vector2(playerPosition.X, playerPosition.Z);
        var targetXZ = new Vector2(destination.X, destination.Z);
        return Vector2.Distance(playerXZ, targetXZ) >= GatherBuddy.Config.AutoGatherConfig.MountUpDistance && CanMount();
    }

    private unsafe bool TryMountUp()
    {
        var actionManager = ActionManager.Instance();
        if (actionManager == null)
            return false;

        var mountId = GatherBuddy.Config.AutoGatherConfig.AutoGatherMountId;
        var success = false;
        if (IsMountUnlocked(mountId) && actionManager->GetActionStatus(ActionType.Mount, mountId) == 0)
        {
            success = actionManager->UseAction(ActionType.Mount, mountId);
        }
        else if (actionManager->GetActionStatus(ActionType.GeneralAction, 24) == 0)
        {
            success = actionManager->UseAction(ActionType.GeneralAction, 24);
        }

        if (success)
            _lastMountAttemptTime = DateTime.UtcNow;
        return success;
    }

    private unsafe bool TryLand()
    {
        var actionManager = ActionManager.Instance();
        if (actionManager == null || actionManager->GetActionStatus(ActionType.GeneralAction, 23) != 0)
            return false;

        var success = actionManager->UseAction(ActionType.GeneralAction, 23);
        if (success)
            _lastLandingAttemptTime = DateTime.UtcNow;
        return success;
    }

    private bool CanRetryMountAttempt()
        => (DateTime.UtcNow - _lastMountAttemptTime).TotalSeconds >= MountRetryCooldown;

    private bool CanRetryLandingAttempt()
        => (DateTime.UtcNow - _lastLandingAttemptTime).TotalSeconds >= LandingRetryCooldown;

    private static unsafe bool CanMount()
    {
        var actionManager = ActionManager.Instance();
        return actionManager != null && actionManager->GetActionStatus(ActionType.Mount, 0) == 0;
    }

    private static float GetVendorLandingDistance()
        => VendorLandingDistance;

    private static unsafe bool IsMountUnlocked(uint mountId)
    {
        var playerState = PlayerState.Instance();
        return playerState != null && playerState->IsMountUnlocked(mountId);
    }

    private static unsafe bool ShouldFly(Vector3 destination)
    {
        if (Dalamud.Conditions[ConditionFlag.InFlight] || Dalamud.Conditions[ConditionFlag.Diving])
            return true;

        var player = Dalamud.Objects.LocalPlayer;
        if (GatherBuddy.Config.AutoGatherConfig.ForceWalking || player == null)
            return false;

        if (Diadem.IsInside)
            return Vector3.Distance(player.Position, destination) >= GatherBuddy.Config.AutoGatherConfig.MountUpDistance;

        var territorySheet = Dalamud.GameData.GetExcelSheet<TerritoryType>();
        if (territorySheet == null || !territorySheet.TryGetRow(Dalamud.ClientState.TerritoryType, out var territory))
            return false;

        var playerState = PlayerState.Instance();
        if (playerState == null)
            return false;

        var aetherCurrentComp = territory.AetherCurrentCompFlgSet.RowId;
        return aetherCurrentComp != 0
            && playerState->IsAetherCurrentZoneComplete(aetherCurrentComp)
            && Vector3.Distance(player.Position, destination) >= GatherBuddy.Config.AutoGatherConfig.MountUpDistance;
    }

    private static Vector3 SnapToMesh(Vector3 position)
        => VNavmesh.Query.Mesh.NearestPoint(position, 3, 10000).GetValueOrDefault(position);

    private Vector3 GetLiveNpcFallbackDestination(IGameObject liveNpc)
    {
        var snapped = SnapToMesh(liveNpc.Position);
        var verticalDrift = MathF.Abs(snapped.Y - liveNpc.Position.Y);
        var horizontalDrift = GetHorizontalDistance(snapped, liveNpc.Position);
        if (verticalDrift <= VendorApproachMaxVerticalDrift && horizontalDrift <= VendorStagingMaxHorizontalDrift)
            return snapped;

        GatherBuddy.Log.Debug($"[VendorNavigator] Rejecting snapped live NPC fallback {snapped} for {_target?.NpcName ?? "vendor"} because it drifted {horizontalDrift:F1}m horizontally and {verticalDrift:F1}m vertically from {liveNpc.Position}, using the live NPC position instead");
        return liveNpc.Position;
    }

    private bool ShouldUseCloseRangeLiveNpcApproach(IGameObject? liveNpc, bool usingLiveNpc)
    {
        if (!usingLiveNpc || liveNpc == null)
            return false;

        var playerPosition = Dalamud.Objects.LocalPlayer?.Position ?? Vector3.Zero;
        return playerPosition != Vector3.Zero
            && GetHorizontalDistance(playerPosition, liveNpc.Position) <= VendorDirectLiveNpcApproachDistance;
    }

    private static float GetCloseRangeCombinedLandingDistance(float liveNpcDistance)
        => System.Math.Clamp(
            liveNpcDistance - VendorCloseCombinedLandingDistanceBuffer,
            VendorCloseCombinedLandingDistanceMinimum,
            VendorCloseCombinedLandingDistanceMaximum);

    private bool TryGetVendorStagingDestination(IGameObject? liveNpc, out Vector3 destination, out string navigationReason)
    {
        if (liveNpc == null)
        {
            destination      = default;
            navigationReason = "cached vendor location";
            return false;
        }

        var minimumDistance = MathF.Max(liveNpc.HitboxRadius + 1.0f, VendorLandingCandidateDistances[0]);
        var preferPlayerSide = !ShouldFly(liveNpc.Position);
        var found = TryFindVendorApproachDestination(liveNpc, VendorLandingCandidateDistances, minimumDistance, VendorLandingDistance,
            VendorStagingMaxHorizontalDrift, preferPlayerSide, out destination, out var outerRingDiagnostics);
        if (found)
        {
            navigationReason = preferPlayerSide
                ? "ground vendor staging point"
                : "safe vendor staging point";
            LogApproachDiagnostics(liveNpc, navigationReason, [outerRingDiagnostics]);
            return true;
        }
        var closeMinimumDistance = MathF.Max(liveNpc.HitboxRadius + VendorApproachMinimumStandOff, 1.5f);
        found = TryFindVendorApproachDestination(liveNpc, VendorCloseStagingCandidateDistances, closeMinimumDistance, VendorLandingCandidateDistances[0],
            VendorApproachMaxHorizontalDrift, false, out destination, out var closeRingDiagnostics);
        if (found)
        {
            navigationReason = "close vendor staging point";
            LogApproachDiagnostics(liveNpc, navigationReason, [$"outer ring: {outerRingDiagnostics}", $"close ring: {closeRingDiagnostics}"]);
            return true;
        }

        navigationReason = "live NPC position fallback";
        LogApproachDiagnostics(liveNpc, navigationReason, [$"outer ring: {outerRingDiagnostics}", $"close ring: {closeRingDiagnostics}"]);
        return false;
    }

    private bool TryGetVendorFinalApproachDestination(IGameObject liveNpc, out Vector3 destination, out string navigationReason)
    {
        var minimumDistance = MathF.Max(liveNpc.HitboxRadius + VendorApproachMinimumStandOff, 0.75f);
        var maximumDistance = LiveNpcInteractionDistance - 0.25f;
        var found = TryFindVendorApproachDestination(liveNpc, GetVendorApproachCandidateDistances(liveNpc), minimumDistance, maximumDistance,
            VendorApproachMaxHorizontalDrift, true, out destination, out var diagnosticsSummary);
        if (found)
        {
            navigationReason = "live NPC final approach point";
            LogApproachDiagnostics(liveNpc, navigationReason, [diagnosticsSummary]);
            return true;
        }

        navigationReason = "live NPC position fallback";
        LogApproachDiagnostics(liveNpc, navigationReason, [diagnosticsSummary]);
        return false;
    }

    private bool TryGetVendorCloseApproachDestination(IGameObject liveNpc, out Vector3 destination, out string navigationReason)
    {
        var minimumDistance = MathF.Max(liveNpc.HitboxRadius + VendorApproachMinimumStandOff, 1.5f);
        var maximumDistance = VendorLandingCandidateDistances[0];
        var found = TryFindVendorApproachDestination(liveNpc, VendorCloseStagingCandidateDistances, minimumDistance, maximumDistance,
            VendorStagingMaxHorizontalDrift, true, out destination, out var diagnosticsSummary);
        if (found)
        {
            navigationReason = "close vendor approach point";
            LogApproachDiagnostics(liveNpc, navigationReason, [diagnosticsSummary]);
            return true;
        }

        navigationReason = "live NPC position fallback";
        LogApproachDiagnostics(liveNpc, navigationReason, [$"close approach: {diagnosticsSummary}"]);
        return false;
    }

    private static float[] GetVendorApproachCandidateDistances(IGameObject liveNpc)
    {
        var interactionLimit = LiveNpcInteractionDistance - 0.25f;
        var hitboxRadius     = MathF.Max(liveNpc.HitboxRadius, 0.5f);
        var firstDistance    = MathF.Min(interactionLimit, hitboxRadius + VendorApproachMinimumStandOff);
        var distances        = new List<float>();

        for (var distance = firstDistance; distance < interactionLimit; distance += VendorApproachDistanceStep)
            distances.Add(distance);

        distances.Add(interactionLimit);
        return distances
            .Distinct()
            .ToArray();
    }

    private void LogApproachDiagnostics(IGameObject liveNpc, string navigationReason, IReadOnlyList<string> diagnostics)
    {
        if (diagnostics.Count == 0 || _target == null)
            return;

        var summary = $"{navigationReason}|{string.Join("; ", diagnostics)}";
        if (summary == _lastApproachDiagnosticsSummary
         && (DateTime.UtcNow - _lastApproachDiagnosticsLogTime).TotalSeconds < VendorApproachDiagnosticsCooldown)
            return;

        _lastApproachDiagnosticsSummary = summary;
        _lastApproachDiagnosticsLogTime = DateTime.UtcNow;
        GatherBuddy.Log.Debug($"[VendorNavigator] {navigationReason} for {_target.NpcName} after {string.Join("; ", diagnostics)} (npc={liveNpc.Position}, rotation={liveNpc.Rotation:F2})");
    }

    private bool TryFindVendorApproachDestination(IGameObject liveNpc, IReadOnlyList<float> distances, float minimumDistance, float maximumDistance, float maximumDrift, bool preferPlayerSide, out Vector3 destination, out string diagnosticsSummary)
    {
        var npcPosition = liveNpc.Position;
        var forward     = GetFacingDirection(liveNpc.Rotation);
        var playerPosition = Dalamud.Objects.LocalPlayer?.Position ?? Vector3.Zero;
        var bestScore   = float.NegativeInfinity;
        var bestForwardDot = float.NegativeInfinity;
        var bestDistance   = 0f;
        var bestCandidate  = Vector3.Zero;
        var validCandidates = 0;
        var projectionFailures = 0;
        string? firstProjectionFailure = null;

        foreach (var distance in distances)
        {
            foreach (var angle in VendorApproachCandidateAngles)
            {
                var candidate = npcPosition + RotateHorizontal(forward * distance, angle);
                if (!TryProjectVendorApproachCandidate(candidate, npcPosition, minimumDistance, maximumDistance, maximumDrift, out var projected, out var projectionFailure))
                {
                    projectionFailures++;
                    firstProjectionFailure ??= projectionFailure;
                    continue;
                }

                validCandidates++;
                var npcDistance = GetHorizontalDistance(projected, npcPosition);
                var forwardDot  = GetForwardDot(projected, npcPosition, forward);
                var playerDot   = GetPlayerDot(projected, npcPosition, playerPosition);
                var score       = ScoreVendorApproachCandidate(npcDistance, forwardDot, playerDot, preferPlayerSide);
                if (_hasNavigationDestination && GetHorizontalDistance(projected, _navigationDestination) <= 1.0f)
                    score += 0.5f;

                if (!(score > bestScore))
                    continue;

                bestScore      = score;
                bestForwardDot = forwardDot;
                bestDistance   = npcDistance;
                bestCandidate  = projected;
            }
        }

        if (bestScore > float.NegativeInfinity)
        {
            destination = bestCandidate;
            diagnosticsSummary = $"selected={bestCandidate}, npcDistance={bestDistance:F1}, forwardDot={bestForwardDot:F2}, valid={validCandidates}, rejected={projectionFailures}";
            return true;
        }

        destination = default;
        diagnosticsSummary = SummarizeApproachFailures(projectionFailures, firstProjectionFailure);
        return false;
    }

    private static string SummarizeApproachFailures(int projectionFailureCount, string? firstProjectionFailure)
        => projectionFailureCount > 0
            ? $"projectionFailures={projectionFailureCount} (first: {firstProjectionFailure})"
            : "no valid approach candidates";

    private bool TryProjectVendorApproachCandidate(Vector3 candidate, Vector3 npcPosition, float minimumDistance, float maximumDistance, float maximumDrift, out Vector3 projected, out string failureReason)
    {
        projected     = default;
        failureReason = string.Empty;

        try
        {
            var pointOnFloor = VNavmesh.Query.Mesh.PointOnFloor;
            Vector3? snapped = pointOnFloor != null
                ? pointOnFloor(candidate, true, VendorApproachFloorSearchHeight)
                : null;
            snapped ??= VNavmesh.Query.Mesh.NearestPoint(candidate, VendorApproachMeshSearchRadius, VendorApproachFloorSearchHeight);
            if (!snapped.HasValue)
            {
                failureReason = $"no floor projection (candidate={candidate})";
                return false;
            }

            var horizontalDrift = GetHorizontalDistance(snapped.Value, candidate);
            if (horizontalDrift > maximumDrift)
            {
                failureReason = $"horizontalDrift={horizontalDrift:F2}>{maximumDrift:F2} (candidate={candidate}, projected={snapped.Value})";
                return false;
            }

            var verticalDrift = MathF.Abs(snapped.Value.Y - npcPosition.Y);
            if (verticalDrift > VendorApproachMaxVerticalDrift)
            {
                failureReason = $"verticalDrift={verticalDrift:F2}>{VendorApproachMaxVerticalDrift:F2} (candidate={candidate}, projected={snapped.Value}, npc={npcPosition})";
                return false;
            }

            var interactionDistance = GetHorizontalDistance(snapped.Value, npcPosition);
            if (interactionDistance > maximumDistance)
            {
                failureReason = $"npcDistance={interactionDistance:F2}>{maximumDistance:F2} (candidate={candidate}, projected={snapped.Value}, npc={npcPosition})";
                return false;
            }
            if (interactionDistance < minimumDistance)
            {
                failureReason = $"npcDistance={interactionDistance:F2}<{minimumDistance:F2} (candidate={candidate}, projected={snapped.Value}, npc={npcPosition})";
                return false;
            }

            projected = snapped.Value;
            return true;
        }
        catch (Exception ex)
        {
            failureReason = $"projection exception for {candidate}: {ex.Message}";
            return false;
        }
    }

    private bool TryRecoverStalledGroundApproach(Vector3 playerPosition, IGameObject? liveNpc, Vector3 destination, bool usingLiveNpc, NavigationDestinationMode destinationMode, string navigationReason)
    {
        if (!usingLiveNpc || destinationMode != NavigationDestinationMode.FinalApproachPoint || _pathUsesFlight || _waitingForMount || _pathTask != null || !VNavmesh.Path.IsRunning())
        {
            ClearGroundApproachProgress();
            return false;
        }

        var destinationDistance = GetHorizontalDistance(playerPosition, destination);
        var movedDistance = _lastGroundProgressPosition == Vector3.Zero
            ? float.MaxValue
            : GetHorizontalDistance(playerPosition, _lastGroundProgressPosition);
        var distanceGain = _lastGroundProgressDistance - destinationDistance;
        if (_lastGroundProgressTime == DateTime.MinValue
         || movedDistance >= VendorGroundProgressMinimumMovement
         || distanceGain >= VendorGroundProgressMinimumDistanceGain)
        {
            UpdateGroundApproachProgress(playerPosition, destination);
            return false;
        }

        var now = DateTime.UtcNow;
        var stalledSeconds = (now - _lastGroundProgressTime).TotalSeconds;
        if (liveNpc != null
         && IsWithinArrivalDistance(playerPosition, liveNpc)
         && stalledSeconds >= VendorGroundInteractionRangeStallTimeout)
        {
            GatherBuddy.Log.Debug($"[VendorNavigator] Ground approach stalled within interaction range of {GetDisplayName(liveNpc)} at {GetTargetDistance(playerPosition, liveNpc):F1}m, stopping pathing and preparing purchase");
            StopPathing();
            _state = State.ReadyToPurchase;
            return true;
        }
        if (stalledSeconds < VendorGroundInteractionRangeFallbackTimeout)
            return false;
        if (liveNpc != null
         && _forceInteractionRangeApproachUntil < now)
        {
            _forceInteractionRangeApproachUntil = now.AddSeconds(VendorInteractionRangeFallbackDurationSeconds);
            GatherBuddy.Log.Debug($"[VendorNavigator] Ground approach stalled for {GetDisplayName(liveNpc)} at {destinationDistance:F1}m from the approach point, falling back to direct interaction-range navigation for {VendorInteractionRangeFallbackDurationSeconds:F1}s");
            UpdateGroundApproachProgress(playerPosition, liveNpc.Position);
            StartVNavmesh(liveNpc.Position, true, NavigationDestinationMode.TargetInteractionRange);
            return true;
        }
        if (stalledSeconds < VendorGroundProgressTimeout
         || (now - _lastGroundRetryTime).TotalSeconds < VendorGroundProgressRetryCooldown)
            return false;

        _groundRetryCount = _lastGroundRetryPosition != Vector3.Zero && GetHorizontalDistance(playerPosition, _lastGroundRetryPosition) <= 3f
            ? _groundRetryCount + 1
            : 1;
        _lastGroundRetryPosition = playerPosition;
        _lastGroundRetryTime     = now;
        GatherBuddy.Log.Debug($"[VendorNavigator] Ground approach stalled for {_target?.NpcName ?? "vendor"} at {destinationDistance:F1}m from the approach point, restarting path using {navigationReason} (retry {_groundRetryCount})");
        UpdateGroundApproachProgress(playerPosition, destination);
        StartVNavmesh(destination, usingLiveNpc, destinationMode);
        return true;
    }

    private void UpdateGroundApproachProgress(Vector3 playerPosition, Vector3 destination)
    {
        _lastGroundProgressPosition = playerPosition;
        _lastGroundProgressDistance = GetHorizontalDistance(playerPosition, destination);
        _lastGroundProgressTime     = DateTime.UtcNow;
    }

    private void ClearGroundApproachProgress()
    {
        _lastGroundProgressPosition = Vector3.Zero;
        _lastGroundProgressDistance = float.MaxValue;
        _lastGroundProgressTime     = DateTime.MinValue;
    }

    private static float GetPlayerDot(Vector3 position, Vector3 npcPosition, Vector3 playerPosition)
    {
        var playerOffset = new Vector2(playerPosition.X - npcPosition.X, playerPosition.Z - npcPosition.Z);
        if (playerOffset.LengthSquared() < 0.01f)
            return 0f;

        var candidateOffset = new Vector2(position.X - npcPosition.X, position.Z - npcPosition.Z);
        if (candidateOffset.LengthSquared() < 0.01f)
            return 0f;

        return Vector2.Dot(Vector2.Normalize(candidateOffset), Vector2.Normalize(playerOffset));
    }

    private static float ScoreVendorApproachCandidate(float npcDistance, float forwardDot, float playerDot, bool preferPlayerSide)
    {
        var score = forwardDot * (preferPlayerSide ? 2.5f : 3.5f) - npcDistance * 0.05f;
        if (preferPlayerSide)
            score += playerDot * 4.0f;
        return score;
    }

    private static Vector3 GetFacingDirection(float rotation)
        => new(MathF.Sin(rotation), 0f, MathF.Cos(rotation));

    private static Vector3 RotateHorizontal(Vector3 vector, float angle)
    {
        var sin = MathF.Sin(angle);
        var cos = MathF.Cos(angle);
        return new Vector3(
            vector.X * cos - vector.Z * sin,
            vector.Y,
            vector.X * sin + vector.Z * cos);
    }

    private static float GetHorizontalDistance(Vector3 first, Vector3 second)
        => Vector2.Distance(new Vector2(first.X, first.Z), new Vector2(second.X, second.Z));

    private static float GetForwardDot(Vector3 position, Vector3 npcPosition, Vector3 forward)
    {
        var horizontalOffset = new Vector2(position.X - npcPosition.X, position.Z - npcPosition.Z);
        if (horizontalOffset.LengthSquared() < 0.01f)
            return float.NegativeInfinity;

        return Vector2.Dot(Vector2.Normalize(horizontalOffset), new Vector2(forward.X, forward.Z));
    }

    private static async Task<List<Vector3>> FindCombinedVendorPath(Vector3 player, Vector3 target, float landingDistance, bool flying, CancellationToken token)
    {
        var pointOnFloor = flying && VNavmesh.Query.Mesh.PointOnFloor != null
            ? VNavmesh.Query.Mesh.PointOnFloor(player, false, 5f)
            : player;
        if (pointOnFloor == null)
            return [];

        var groundPath = await VNavmesh.Nav.PathfindCancelable(pointOnFloor.Value, target, false, token);
        if (groundPath.Count == 0)
            return [];
        if (groundPath.Count == 1)
            return groundPath;

        var intersectionIndex = FindIntersection(groundPath, target, landingDistance);
        var landingWaypoint = GetPointAtRadius(groundPath[intersectionIndex], groundPath[intersectionIndex + 1], target, landingDistance);
        var meshLandingWaypoint = VNavmesh.Query.Mesh.NearestPoint(landingWaypoint, landingDistance, 10f);
        if (meshLandingWaypoint == null || MathF.Abs(target.Y - meshLandingWaypoint.Value.Y) > 10f)
            return [];

        var flyPath = await VNavmesh.Nav.PathfindCancelable(player, meshLandingWaypoint.Value, true, token);
        if (flyPath.Count == 0)
            return [];

        if (flyPath.Count > 1 && Vector3.DistanceSquared(flyPath[^1], flyPath[^2]) < 0.01f)
            flyPath.RemoveAt(flyPath.Count - 1);

        landingWaypoint = flyPath[^1];
        flyPath.AddRange(groundPath.Skip(intersectionIndex + 1));
        flyPath.Add(landingWaypoint);
        return flyPath;

        static int FindIntersection(List<Vector3> waypoints, Vector3 destination, float radius)
        {
            var radiusSquared = radius * radius;
            for (var index = waypoints.Count - 2; index > 0; index--)
            {
                if (Vector3.DistanceSquared(waypoints[index], destination) > radiusSquared)
                    return index;
            }

            return 0;
        }

        static Vector3 GetPointAtRadius(Vector3 first, Vector3 second, Vector3 destination, float radius)
        {
            var direction = new Vector2(second.X - first.X, second.Z - first.Z);
            var relative = new Vector2(first.X - destination.X, first.Z - destination.Z);
            var a = Vector2.Dot(direction, direction);
            var b = 2 * Vector2.Dot(relative, direction);
            var c = Vector2.Dot(relative, relative) - radius * radius;
            var discriminant = b * b - 4 * a * c;
            if (discriminant < 0)
                return first;

            discriminant = MathF.Sqrt(discriminant);
            var firstT = (-b - discriminant) / (2 * a);
            var secondT = (-b + discriminant) / (2 * a);
            var t = firstT >= 0f && firstT <= 1f ? firstT : secondT;
            return first + (second - first) * t;
        }
    }

    private static void RemovePassedWaypoints(List<Vector3> path)
    {
        if (path.Count == 0 || Dalamud.Objects.LocalPlayer == null)
            return;

        var playerPosition = Dalamud.Objects.LocalPlayer.Position;
        var target         = path[^1];
        var forward        = new Vector3(target.X - playerPosition.X, 0f, target.Z - playerPosition.Z);
        if (forward.LengthSquared() < 1f)
            return;

        forward = Vector3.Normalize(forward);
        var removeCount = 0;
        while (removeCount < path.Count)
        {
            var next = new Vector3(path[removeCount].X - playerPosition.X, 0f, path[removeCount].Z - playerPosition.Z);
            if (Vector3.Dot(forward, next) > 0)
                break;

            removeCount++;
        }

        if (removeCount > 0)
            path.RemoveRange(0, removeCount);
    }

    private static (uint AetheryteId, string? AethernetName, bool RequiresHousingEntry) FindBestRoute(uint targetTerritoryId, Vector3 npcPosition)
    {
        if (TryFindHousingRoute(targetTerritoryId, out var housingAetheryteId))
            return (housingAetheryteId, null, housingAetheryteId != 0);
        var aetheryteSheet = Dalamud.GameData.GetExcelSheet<Aetheryte>();
        if (aetheryteSheet == null) return (0, null, false);

        foreach (var a in aetheryteSheet)
            if (a.IsAetheryte && a.Territory.RowId == targetTerritoryId && Teleporter.IsAttuned(a.RowId))
                return (a.RowId, null, false);

        var bestDist              = float.MaxValue;
        uint bestAetheryteId      = 0;
        string? bestShardName     = null;
        uint fallbackAetheryteId  = 0;
        string? fallbackShardName = null;

        var npcXZ = new Vector2(npcPosition.X, npcPosition.Z);

        foreach (var shard in aetheryteSheet)
        {
            if (shard.IsAetheryte || shard.Territory.RowId != targetTerritoryId || shard.AethernetName.RowId == 0)
                continue;

            var shardName = shard.AethernetName.Value.Name.ExtractText();
            if (string.IsNullOrWhiteSpace(shardName)) continue;

            var group   = shard.AethernetGroup;
            uint mainId = 0;
            foreach (var mainA in aetheryteSheet)
            {
                if (mainA.IsAetheryte && mainA.AethernetGroup == group && Teleporter.IsAttuned(mainA.RowId))
                {
                    mainId = mainA.RowId;
                    break;
                }
            }
            if (mainId == 0) continue;

            if (fallbackAetheryteId == 0)
            {
                fallbackAetheryteId = mainId;
                fallbackShardName   = shardName;
            }

            var shardXZ = GetAetheryteXZ(shard);
            if (!shardXZ.HasValue) continue;

            var dist = Vector2.Distance(npcXZ, shardXZ.Value);
            if (dist < bestDist)
            {
                bestDist         = dist;
                bestAetheryteId  = mainId;
                bestShardName    = shardName;
            }
        }

        if (bestAetheryteId != 0)
            return (bestAetheryteId, bestShardName, false);
        return (fallbackAetheryteId, fallbackShardName, false);
    }

    // Port of Lifestream DataStore.GetTinyAetheryte position logic.
    private static Vector2? GetAetheryteXZ(Aetheryte shard)
    {
        if (shard.AethernetName.RowId == 0) return null;

        var mapSheet    = Dalamud.GameData.GetExcelSheet<Map>();
        var markerSheet = Dalamud.GameData.GetSubrowExcelSheet<MapMarker>();
        if (mapSheet == null || markerSheet == null) return null;

        var territoryId = shard.Territory.RowId;
        var scale = 100f;
        foreach (var m in mapSheet)
        {
            if (m.TerritoryType.RowId == territoryId)
            {
                scale = m.SizeFactor;
                break;
            }
        }

        foreach (var markerRow in markerSheet)
        {
            foreach (var m in markerRow)
            {
                if (m.DataType == 4 && m.DataKey.RowId == shard.AethernetName.RowId)
                {
                    var x = (m.X - 1024f) / (scale / 100f);
                    var z = (m.Y - 1024f) / (scale / 100f);
                    return new Vector2(x, z);
                }
            }
        }
        return null;
    }
}

