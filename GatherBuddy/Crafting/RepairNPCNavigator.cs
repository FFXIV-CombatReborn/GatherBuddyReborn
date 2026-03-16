using System;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using GatherBuddy.Plugin;
using GatherBuddy.SeFunctions;

namespace GatherBuddy.Crafting;

public class RepairNPCNavigator
{
    private enum NavigationState
    {
        Idle,
        Teleporting,
        WaitingForTeleport,
        WaitingForZoneLoad,
        Navigating,
        Arrived,
        Failed
    }

    private NavigationState _state = NavigationState.Idle;
    private RepairNPCData? _targetNPC;
    private DateTime _stateStartTime;
    private bool _teleportAttempted;
    private const double TeleportCooldown = 3.0;
    private const double ZoneLoadWait = 5.0;
    private const double NavigationTimeout = 60.0;

    public bool IsComplete => _state == NavigationState.Arrived || _state == NavigationState.Failed;
    public bool IsFailed => _state == NavigationState.Failed;
    public bool IsNavigating => _state != NavigationState.Idle && !IsComplete;

    public void StartNavigation(RepairNPCData targetNPC)
    {
        _targetNPC = targetNPC;
        _state = NavigationState.Idle;
        _teleportAttempted = false;
        _stateStartTime = DateTime.UtcNow;
        
        GatherBuddy.Log.Information($"[RepairNPCNavigator] Starting navigation to {targetNPC.Name} in territory {targetNPC.TerritoryType}");
        
        if (Dalamud.ClientState.TerritoryType == targetNPC.TerritoryType)
        {
            var playerPos = Dalamud.ClientState.LocalPlayer?.Position ?? Vector3.Zero;
            if (playerPos != Vector3.Zero)
            {
                var distance = Vector3.Distance(playerPos, targetNPC.Position);
                if (distance <= 7f)
                {
                    GatherBuddy.Log.Information($"[RepairNPCNavigator] Already near target NPC ({distance:F1}m)");
                    _state = NavigationState.Arrived;
                    return;
                }
            }
            
            _state = NavigationState.WaitingForZoneLoad;
            _stateStartTime = DateTime.UtcNow;
        }
        else
        {
            _state = NavigationState.Teleporting;
        }
    }

    public void Update()
    {
        if (_targetNPC == null || _state == NavigationState.Idle || IsComplete)
            return;

        try
        {
            switch (_state)
            {
                case NavigationState.Teleporting:
                    UpdateTeleporting();
                    break;
                case NavigationState.WaitingForTeleport:
                    UpdateWaitingForTeleport();
                    break;
                case NavigationState.WaitingForZoneLoad:
                    UpdateWaitingForZoneLoad();
                    break;
                case NavigationState.Navigating:
                    UpdateNavigating();
                    break;
            }
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[RepairNPCNavigator] Error in Update: {ex.Message}");
            _state = NavigationState.Failed;
        }
    }

    private void UpdateTeleporting()
    {
        if (Dalamud.Conditions[ConditionFlag.BetweenAreas] || Dalamud.Conditions[ConditionFlag.BetweenAreas51])
            return;

        if (!_teleportAttempted)
        {
            var aetheryteId = FindNearestAetheryteForTerritory(_targetNPC!.TerritoryType);
            if (aetheryteId == 0)
            {
                GatherBuddy.Log.Error($"[RepairNPCNavigator] Could not find aetheryte for territory {_targetNPC.TerritoryType}");
                _state = NavigationState.Failed;
                return;
            }

            GatherBuddy.Log.Information($"[RepairNPCNavigator] Teleporting to aetheryte {aetheryteId}");
            if (Teleporter.Teleport(aetheryteId))
            {
                _teleportAttempted = true;
                _stateStartTime = DateTime.UtcNow;
                _state = NavigationState.WaitingForTeleport;
            }
            else
            {
                GatherBuddy.Log.Error($"[RepairNPCNavigator] Failed to teleport");
                _state = NavigationState.Failed;
            }
        }
    }

    private void UpdateWaitingForTeleport()
    {
        if (Lifestream.Enabled && Lifestream.IsBusy())
            return;

        if ((DateTime.UtcNow - _stateStartTime).TotalSeconds < TeleportCooldown)
            return;

        if (Dalamud.Conditions[ConditionFlag.BetweenAreas] || Dalamud.Conditions[ConditionFlag.BetweenAreas51])
            return;

        if (Dalamud.ClientState.TerritoryType == _targetNPC!.TerritoryType)
        {
            GatherBuddy.Log.Debug($"[RepairNPCNavigator] Arrived in correct territory, waiting for zone load");
            _state = NavigationState.WaitingForZoneLoad;
            _stateStartTime = DateTime.UtcNow;
        }
        else if ((DateTime.UtcNow - _stateStartTime).TotalSeconds > 30)
        {
            GatherBuddy.Log.Error($"[RepairNPCNavigator] Teleport timeout - still in territory {Dalamud.ClientState.TerritoryType}, expected {_targetNPC.TerritoryType}");
            _state = NavigationState.Failed;
        }
    }

    private void UpdateWaitingForZoneLoad()
    {
        if (Dalamud.Conditions[ConditionFlag.BetweenAreas] || Dalamud.Conditions[ConditionFlag.BetweenAreas51])
            return;

        var playerPos = Dalamud.ClientState.LocalPlayer?.Position ?? Vector3.Zero;
        if (playerPos == Vector3.Zero)
            return;

        if (Lifestream.Enabled && Lifestream.IsBusy())
        {
            GatherBuddy.Log.Debug($"[RepairNPCNavigator] Waiting for Lifestream to finish");
            return;
        }

        if ((DateTime.UtcNow - _stateStartTime).TotalSeconds < ZoneLoadWait)
        {
            GatherBuddy.Log.Debug($"[RepairNPCNavigator] Waiting for zone to fully load...");
            return;
        }

        GatherBuddy.Log.Information($"[RepairNPCNavigator] Zone loaded, starting navigation");
        StartVNavmeshNavigation();
    }

    private void StartVNavmeshNavigation()
    {
        try
        {
            GatherBuddy.Log.Information($"[RepairNPCNavigator] Starting VNavmesh navigation to {_targetNPC!.Position}");
            VNavmesh.SimpleMove.PathfindAndMoveTo(_targetNPC.Position, false);
            _state = NavigationState.Navigating;
            _stateStartTime = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[RepairNPCNavigator] Failed to start VNavmesh: {ex.Message}");
            _state = NavigationState.Failed;
        }
    }

    private void UpdateNavigating()
    {
        var playerPos = Dalamud.ClientState.LocalPlayer?.Position ?? Vector3.Zero;
        if (playerPos == Vector3.Zero)
            return;

        if (Dalamud.ClientState.TerritoryType != _targetNPC!.TerritoryType)
        {
            GatherBuddy.Log.Warning($"[RepairNPCNavigator] Territory changed during navigation, restarting");
            _state = NavigationState.Teleporting;
            _teleportAttempted = false;
            return;
        }

        var distance = Vector3.Distance(playerPos, _targetNPC.Position);

        if (distance <= 7f)
        {
            GatherBuddy.Log.Information($"[RepairNPCNavigator] Arrived at repair NPC ({distance:F1}m)");
            VNavmesh.Path.Stop();
            _state = NavigationState.Arrived;
            return;
        }

        if ((DateTime.UtcNow - _stateStartTime).TotalSeconds > NavigationTimeout)
        {
            GatherBuddy.Log.Error($"[RepairNPCNavigator] Navigation timeout - still {distance:F1}m away");
            VNavmesh.Path.Stop();
            _state = NavigationState.Failed;
            return;
        }

        if (!VNavmesh.Path.IsRunning())
        {
            GatherBuddy.Log.Debug($"[RepairNPCNavigator] Path stopped, restarting navigation ({distance:F1}m remaining)");
            try
            {
                VNavmesh.SimpleMove.PathfindAndMoveTo(_targetNPC.Position, false);
            }
            catch (Exception ex)
            {
                GatherBuddy.Log.Error($"[RepairNPCNavigator] Failed to restart navigation: {ex.Message}");
                _state = NavigationState.Failed;
            }
        }
    }

    private uint FindNearestAetheryteForTerritory(uint territoryId)
    {
        var territorySheet = Dalamud.GameData.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>();
        if (territorySheet == null || !territorySheet.TryGetRow(territoryId, out var territory))
            return 0;

        var aetheryteSheet = Dalamud.GameData.GetExcelSheet<Lumina.Excel.Sheets.Aetheryte>();
        if (aetheryteSheet == null)
            return 0;

        foreach (var aetheryte in aetheryteSheet)
        {
            if (aetheryte.Territory.RowId == territoryId && aetheryte.IsAetheryte)
            {
                return aetheryte.RowId;
            }
        }

        return 0;
    }

    public void Stop()
    {
        if (_state == NavigationState.Navigating)
        {
            VNavmesh.Path.Stop();
        }
        _state = NavigationState.Idle;
        _targetNPC = null;
    }
}
