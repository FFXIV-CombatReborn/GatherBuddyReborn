using Dalamud.Plugin.Services;
using GatherBuddy.Helpers;
using GatherBuddy.CustomInfo;
using System;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using GatherBuddy.Utilities;
using GatherBuddy.Plugin;

namespace GatherBuddy.AutoGather.Movement
{
    public enum AdvancedUnstuckCheckResult
    {
        Pass,
        Fail
    }
    public sealed class AdvancedUnstuck : IDisposable
    {
        private const double UnstuckDuration = 1.0;
        private const double CheckExpiration = 1.0;
        private const float MinMovementDistance = 2.0f;

        private readonly OverrideMovement _movementController = new();
        private DateTime _lastMovement;
        private DateTime _unstuckStart;
        private DateTime _lastCheck;
        private Vector3 _lastPosition;

        public bool IsRunning => _movementController.Enabled;

        public AdvancedUnstuckCheckResult Check(Vector3 destination, bool isPathing)
        {
            if (IsRunning)
                return AdvancedUnstuckCheckResult.Fail;

            var now = DateTime.Now;

            //On cooldown or not navigating: disable tracking and reset
            if (now.Subtract(_unstuckStart).TotalSeconds < GatherBuddy.Config.AutoGatherConfig.NavResetCooldown
                || destination == default)
            {
                _lastCheck = DateTime.MinValue;
                return AdvancedUnstuckCheckResult.Pass;
            }

            var lastCheck = _lastCheck;
            _lastCheck = now;

            //Tracking wasn't active for 1 second or was reset: restart tracking from the current position
            if (now.Subtract(lastCheck).TotalSeconds > CheckExpiration)
            {
                _lastPosition = Player.Position;
                _lastMovement = now;
                return AdvancedUnstuckCheckResult.Pass;
            }

            //vnavmesh is moving...
            if (isPathing)
            {
                //...and quite fast: update current position
                if (_lastPosition.DistanceToPlayer() >= MinMovementDistance)
                {
                    _lastPosition = Player.Object.Position;
                    _lastMovement = now;
                }
                //...but not fast enough: unstuck
                else if (now.Subtract(_lastMovement).TotalSeconds > GatherBuddy.Config.AutoGatherConfig.NavResetThreshold)
                {
                    GatherBuddy.Log.Warning($"Advanced Unstuck: the character is stuck. Moved {_lastPosition.DistanceToPlayer()} yalms in {now.Subtract(_lastMovement).TotalSeconds} seconds.");
                    Start();
                }
            } 
            else
            {
                //vnavmesh is generating path: update current position.
                //Not checking IsPathGenerating because pathfinding may complete asynchronously, and this is handled by HandlePathfinding()
                _lastPosition = Player.Position;
                _lastMovement = now;
            }
            return IsRunning ? AdvancedUnstuckCheckResult.Fail : AdvancedUnstuckCheckResult.Pass;
        }

        public void Force()
        {
            if (!IsRunning)
            {
                GatherBuddy.Log.Warning("Advanced Unstuck: force start.");
                Start();
            }
        }

        public void ForceFishing()
        {
            if (!IsRunning)
            {
                GatherBuddy.Log.Warning("Advanced Unstuck: force start for fishing (finding landable spot).");
                StartFishing();
            }
        }

        private void Start()
        {
            var rng = new Random();
            float rnd() => (rng.Next(2) == 0 ? -1 : 1) * rng.NextSingle();
            var newPosition = Player.Position + Vector3.Normalize(new Vector3(rnd(), rnd(), rnd())) * 25f;
            _movementController.DesiredPosition = newPosition;
            _movementController.Enabled = true;
            _unstuckStart = DateTime.Now;
            Dalamud.Framework.Update += RunningUpdate;
        }

        private void StartFishing()
        {
            if (!VNavmesh.Enabled || VNavmesh.Query.Mesh.PointOnFloor == null)
            {
                GatherBuddy.Log.Error("vNavmesh mesh query not available, cannot unstuck for fishing safely");
                return;
            }

            var rng = new Random();
            Vector3 landablePosition = default;
            
            for (int distance = 15; distance <= 60; distance += 5)
            {
                for (int angleStep = 0; angleStep < 16; angleStep++)
                {
                    var angle = (float)(angleStep * Math.PI * 2 / 16);
                    var offset = new Vector3(
                        (float)Math.Cos(angle) * distance,
                        0,
                        (float)Math.Sin(angle) * distance
                    );
                    
                    var testPosition = Player.Position + offset;
                    
                    try
                    {
                        var floorPosition = VNavmesh.Query.Mesh.PointOnFloor.Invoke(testPosition, true, 50f);
                        
                        if (floorPosition.HasValue && floorPosition.Value != default)
                        {
                            var distanceToFloor = Vector3.Distance(floorPosition.Value, Player.Position);
                            var heightDiff = Math.Abs(floorPosition.Value.Y - testPosition.Y);
                            
                            if (distanceToFloor > 10f && heightDiff < 30f)
                            {
                                landablePosition = floorPosition.Value;
                                GatherBuddy.Log.Information($"[Fishing Unstuck] Found landable position at {distanceToFloor:F1}y away (height diff: {heightDiff:F1}y)");
                                break;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        GatherBuddy.Log.Debug($"Error querying mesh at distance {distance}, angle {angleStep}: {ex.Message}");
                    }
                }
                
                if (landablePosition != default)
                    break;
            }
            
            if (landablePosition == default)
            {
                GatherBuddy.Log.Error("[Fishing Unstuck] CRITICAL: Could not find any landable position within 60y. Manual intervention required.");
                return;
            }
            
            _movementController.DesiredPosition = landablePosition;
            _movementController.Enabled = true;
            _unstuckStart = DateTime.Now;
            Dalamud.Framework.Update += RunningUpdate;
        }

        private void RunningUpdate(IFramework framework)
        {
            if (DateTime.Now.Subtract(_unstuckStart).TotalSeconds > UnstuckDuration)
            {
                Stop();
            }
        }
        
        private void Stop()
        {
            _movementController.Enabled = false;
            _lastCheck = DateTime.MinValue;
            Dalamud.Framework.Update -= RunningUpdate;
        }

        public void Dispose()
        {
            Stop();
            _movementController.Dispose();
        }
    }
}