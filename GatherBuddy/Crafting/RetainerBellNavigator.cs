using System;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using GatherBuddy.Plugin;

namespace GatherBuddy.Crafting;

public class RetainerBellNavigator
{
    private enum NavigationState
    {
        Idle,
        Navigating,
        Arrived,
        Failed
    }

    private NavigationState _state = NavigationState.Idle;
    private IGameObject? _targetBell;
    private DateTime _stateStartTime;
    private DateTime _nextRestartAttempt = DateTime.MinValue;
    private float _lastDistance = float.MaxValue;
    private int _restartAttempts;
    private const double NavigationTimeout = 60.0;
    private const double RestartCooldownSeconds = 1.0;
    private const int MaxRestartAttempts = 5;
    private const float ArrivalDistance = 2.0f;

    public bool IsComplete => _state == NavigationState.Arrived || _state == NavigationState.Failed;
    public bool IsFailed => _state == NavigationState.Failed;
    public bool IsNavigating => _state == NavigationState.Navigating;

    public bool StartNavigation(IGameObject bell)
    {
        _targetBell = bell;
        var playerPos = Dalamud.Objects.LocalPlayer?.Position ?? Vector3.Zero;

        if (playerPos == Vector3.Zero)
        {
            GatherBuddy.Log.Error("[RetainerBellNavigator] Player position unavailable");
            _state = NavigationState.Failed;
            return false;
        }

        var distance = Vector3.Distance(playerPos, bell.Position);
        if (distance <= ArrivalDistance)
        {
            GatherBuddy.Log.Debug($"[RetainerBellNavigator] Already near bell ({distance:F1}m)");
            _state = NavigationState.Arrived;
            return true;
        }

        try
        {
            VNavmesh.SimpleMove.PathfindAndMoveTo(bell.Position, false);
            _state = NavigationState.Navigating;
            _stateStartTime = DateTime.UtcNow;
            _nextRestartAttempt = DateTime.MinValue;
            _lastDistance = distance;
            _restartAttempts = 0;
            return true;
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[RetainerBellNavigator] Failed to start navigation: {ex.Message}");
            _state = NavigationState.Failed;
            return false;
        }
    }

    public void Update()
    {
        if (_targetBell == null || _state != NavigationState.Navigating)
            return;

        try
        {
            var playerPos = Dalamud.Objects.LocalPlayer?.Position ?? Vector3.Zero;
            if (playerPos == Vector3.Zero)
                return;

            var distance = Vector3.Distance(playerPos, _targetBell.Position);
            if (distance < _lastDistance - 0.5f)
            {
                _restartAttempts = 0;
                _lastDistance = distance;
            }

            if (distance <= ArrivalDistance)
            {
                VNavmesh.Path.Stop();
                _state = NavigationState.Arrived;
                return;
            }

            if ((DateTime.UtcNow - _stateStartTime).TotalSeconds > NavigationTimeout)
            {
                GatherBuddy.Log.Error($"[RetainerBellNavigator] Navigation timeout - still {distance:F1}m away");
                VNavmesh.Path.Stop();
                _state = NavigationState.Failed;
                return;
            }

            if (!VNavmesh.Path.IsRunning())
            {
                if (DateTime.UtcNow < _nextRestartAttempt)
                    return;
                if (_restartAttempts >= MaxRestartAttempts)
                {
                    GatherBuddy.Log.Error($"[RetainerBellNavigator] Navigation failed after {MaxRestartAttempts} restart attempts ({distance:F1}m remaining)");
                    _state = NavigationState.Failed;
                    return;
                }
                _restartAttempts++;
                _nextRestartAttempt = DateTime.UtcNow.AddSeconds(RestartCooldownSeconds);
                try
                {
                    VNavmesh.SimpleMove.PathfindAndMoveTo(_targetBell.Position, false);
                }
                catch (Exception ex)
                {
                    GatherBuddy.Log.Warning($"[RetainerBellNavigator] Failed to restart navigation on attempt {_restartAttempts}/{MaxRestartAttempts}: {ex.Message}");
                    if (_restartAttempts >= MaxRestartAttempts)
                        _state = NavigationState.Failed;
                }
            }
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[RetainerBellNavigator] Error in Update: {ex.Message}");
            _state = NavigationState.Failed;
        }
    }

    public void Stop()
    {
        if (_state == NavigationState.Navigating)
        {
            VNavmesh.Path.Stop();
        }
        _state = NavigationState.Idle;
        _targetBell = null;
        _nextRestartAttempt = DateTime.MinValue;
        _lastDistance = float.MaxValue;
        _restartAttempts = 0;
    }
}
