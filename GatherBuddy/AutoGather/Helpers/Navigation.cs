using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons.MathHelpers;
using GatherBuddy.AutoGather.Movement;
using GatherBuddy.CustomInfo;
using GatherBuddy.Plugin;

namespace GatherBuddy.AutoGather.Helpers;

public class Navigation
{
    public enum NavigationResult
    {
        Success,
        InProgress,
        Failed,
        Stuck,
        Pass,
    }

    private readonly AdvancedUnstuck _advancedUnstuck;

    public Navigation()
    {
        _advancedUnstuck     =  new AdvancedUnstuck();
    }

    public void StartPath()
    {
        if (CurrentPath == null)
        {
            Svc.Log.Error("GBR does not have a path and StartPath() was called!");
            return;
        }
        VNavmesh.Path.MoveTo(CurrentPath, ShouldFly());
    }

    public void ForceUnstuck()
    {
        _advancedUnstuck.Force();
    }

    public NavigationResult Check()
    {
        if (CurrentPathTask != null && CurrentPathTask.Status == TaskStatus.RanToCompletion)
        {
            var result = CurrentPathTask.Result;
            CurrentPath     = result;
            CurrentPathTask = null;
            if (CurrentPath == null || CurrentPath.Count == 0)
            {
                StopNavigation();
                return NavigationResult.Failed;
            }

            return NavigationResult.Success;
        }
        if (CurrentPathTask != null && !CurrentPathTask.IsCompleted)
        {
            return NavigationResult.InProgress;
        }
        var isPathGenerating = VNavmesh.Nav.PathfindInProgress();
        var isPathing        = VNavmesh.Path.IsRunning();
        if (!isPathGenerating && !isPathing)
            return NavigationResult.Pass;

        switch (_advancedUnstuck.Check(CurrentDestination, isPathGenerating, isPathing))
        {
            case AdvancedUnstuckCheckResult.Pass: break;
            case AdvancedUnstuckCheckResult.Wait: return NavigationResult.InProgress;
            case AdvancedUnstuckCheckResult.Fail:
                StopNavigation();
                return NavigationResult.Stuck;
        }
        return NavigationResult.Pass;
    }

    private void Reset()
    {
        CurrentDestination = default;
        CurrentPath        = null;
        CurrentPathTask = null;
    }

    private Vector3 _currentDestination;
    public Vector3 CurrentDestination
    {
        get => _currentDestination;
        set
        {
            if (value == _currentDestination)
                return;
            _currentDestination = value;
            if (value != default)
            {
                Navigate();
            }
        }
    }

    public List<Vector3>?       CurrentPath     { get; private set; } = null;
    public Task<List<Vector3>>? CurrentPathTask { get; private set; } = null;

    public void StopNavigation()
    {
        Reset();
        if (VNavmesh.Enabled)
        {
            VNavmesh.Path.Stop();
        }
    }

    private void Navigate()
    {
        if (VNavmesh.Path.IsRunning())
            VNavmesh.Path.Stop();
        var shouldFly = ShouldFly();
        shouldFly |= Dalamud.Conditions[ConditionFlag.Diving];

        var correctedDestination = GetCorrectedDestination(CurrentDestination);
        GatherBuddy.Log.Debug($"Navigating to {CurrentDestination} (corrected to {correctedDestination})");

        CurrentPathTask = VNavmesh.Nav.Pathfind(Player.Position, correctedDestination, shouldFly);
    }

    private static Vector3 GetCorrectedDestination(Vector3 destination)
    {
        const float MaxHorizontalSeparation = 3.0f;
        const float MaxVerticalSeparation   = 2.5f;

        try
        {
            float separation;
            if (WorldData.NodeOffsets.TryGetValue(destination, out var offset))
            {
                offset = VNavmesh.Query.Mesh.NearestPoint(offset, MaxHorizontalSeparation, MaxVerticalSeparation);
                if ((separation = Vector2.Distance(offset.ToVector2(), destination.ToVector2())) > MaxHorizontalSeparation)
                    GatherBuddy.Log.Warning(
                        $"Offset is ignored because the horizontal separation {separation} is too large after correcting for mesh. Maximum allowed is {MaxHorizontalSeparation}.");
                else if ((separation = Math.Abs(offset.Y - destination.Y)) > MaxVerticalSeparation)
                    GatherBuddy.Log.Warning(
                        $"Offset is ignored because the vertical separation {separation} is too large after correcting for mesh. Maximum allowed is {MaxVerticalSeparation}.");
                else
                    return offset;
            }

            var correctedDestination = VNavmesh.Query.Mesh.NearestPoint(destination, MaxHorizontalSeparation, MaxVerticalSeparation);
            if ((separation = Vector2.Distance(correctedDestination.ToVector2(), destination.ToVector2())) > MaxHorizontalSeparation)
                GatherBuddy.Log.Warning(
                    $"Query.Mesh.NearestPoint() returned a point with too large horizontal separation {separation}. Maximum allowed is {MaxHorizontalSeparation}.");
            else if ((separation = Math.Abs(correctedDestination.Y - destination.Y)) > MaxVerticalSeparation)
                GatherBuddy.Log.Warning(
                    $"Query.Mesh.NearestPoint() returned a point with too large vertical separation {separation}. Maximum allowed is {MaxVerticalSeparation}.");
            else
                return correctedDestination;
        }
        catch (Exception)
        { }

        return destination;
    }

    public bool ShouldFly()
    {
        if (Dalamud.Conditions[ConditionFlag.InFlight] || Dalamud.Conditions[ConditionFlag.Diving])
            return true;

        if (GatherBuddy.Config.AutoGatherConfig.ForceWalking || Dalamud.ClientState.LocalPlayer == null)
        {
            return false;
        }

        return Vector3.Distance(Dalamud.ClientState.LocalPlayer.Position, CurrentDestination)
         >= GatherBuddy.Config.AutoGatherConfig.MountUpDistance;
    }
}
