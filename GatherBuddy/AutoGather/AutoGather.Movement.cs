using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using GatherBuddy.Classes;
using GatherBuddy.CustomInfo;
using GatherBuddy.Data;
using GatherBuddy.Enums;
using GatherBuddy.Helpers;
using GatherBuddy.Interfaces;
using GatherBuddy.Plugin;
using GatherBuddy.SeFunctions;
using GatherBuddy.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Aetheryte = GatherBuddy.Classes.Aetheryte;

namespace GatherBuddy.AutoGather
{
    public partial class AutoGather
    {
        private unsafe void EnqueueDismount()
        {
            TaskManager.Enqueue(StopNavigation);

            var am = ActionManager.Instance();
            TaskManager.Enqueue(() => { if (Dalamud.Conditions[ConditionFlag.Mounted]) am->UseAction(ActionType.Mount, 0); }, "Dismount");

            TaskManager.Enqueue(() => !Dalamud.Conditions[ConditionFlag.InFlight] && CanAct, 1000, "Wait for not in flight");
            TaskManager.Enqueue(() => { if (Dalamud.Conditions[ConditionFlag.Mounted]) am->UseAction(ActionType.Mount, 0); }, "Dismount 2");
            TaskManager.Enqueue(() => !Dalamud.Conditions[ConditionFlag.Mounted] && CanAct, 1000, "Wait for dismount");
            TaskManager.Enqueue(() => { if (!Dalamud.Conditions[ConditionFlag.Mounted]) TaskManager.DelayNextImmediate(500); } );//Prevent "Unable to execute command while jumping."
        }

        private unsafe void EnqueueMountUp()
        {
            var am = ActionManager.Instance();
            var mount = GatherBuddy.Config.AutoGatherConfig.AutoGatherMountId;
            Action doMount;

            if (IsMountUnlocked(mount) && am->GetActionStatus(ActionType.Mount, mount) == 0)
            {
                doMount = () => am->UseAction(ActionType.Mount, mount);
            }
            else
            {
                if (am->GetActionStatus(ActionType.GeneralAction, 24) != 0)
                {
                    return;
                }

                doMount = () => am->UseAction(ActionType.GeneralAction, 24);
            }

            EnqueueActionWithDelay(doMount);
            TaskManager.Enqueue(() => Dalamud.Conditions[ConditionFlag.Mounted], 2000);
        }

        private unsafe bool CanMount()
        {
            var am = ActionManager.Instance();
            return am->GetActionStatus(ActionType.GeneralAction, 24) == 0;
        }

        private unsafe bool IsMountUnlocked(uint mount)
        {
            var instance = PlayerState.Instance();
            if (instance == null)
                return false;

            return instance->IsMountUnlocked(mount);
        }

        private void MoveToCloseNode(IGameObject gameObject, Gatherable targetItem, ConfigPreset config)
        {
            // We can open a node with less than 3 vertical and less than 3.5 horizontal separation
            var hSeparation = Vector2.Distance(gameObject.Position.ToVector2(), Player.Position.ToVector2());
            var vSeparation = Math.Abs(gameObject.Position.Y - Player.Position.Y);

            if (gameObject.Position.DistanceToPlayer() < 15 && Dalamud.Targets.Target != gameObject)
                Dalamud.Targets.Target = gameObject;

            if (hSeparation < 3.5)
            {
                var waitGP = targetItem.ItemData.IsCollectable && Player.Object.CurrentGp < config.CollectableMinGP;
                waitGP |= !targetItem.ItemData.IsCollectable && Player.Object.CurrentGp < config.GatherableMinGP;

                if (Dalamud.Conditions[ConditionFlag.Mounted] && (waitGP || GetConsumablesWithCastTime(config) > 0))
                {
                    EnqueueDismount();
                    TaskManager.Enqueue(() => {
                        if (Dalamud.Conditions[ConditionFlag.Mounted] && Dalamud.Conditions[ConditionFlag.InFlight] && !Dalamud.Conditions[ConditionFlag.Diving])
                        {
                            ForceLandAndDismount();
                        }
                    });
                }
                else if (waitGP)
                {
                    StopNavigation();
                    AutoStatus = "Waiting for GP to regenerate...";
                }
                else
                {
                    // Use consumables with cast time just before gathering a node when player is surely not mounted
                    if (GetConsumablesWithCastTime(config) is var consumable and > 0)
                    {
                        if (IsPathing)
                            StopNavigation();
                        else
                            EnqueueActionWithDelay(() => UseItem(consumable));
                    }
                    else
                    {
                        // Check perception requirement before interacting with node
                        if (DiscipleOfLand.Perception < targetItem.GatheringData.PerceptionReq)
                        {
                            Communicator.PrintError($"Insufficient Perception to gather this item. Required: {targetItem.GatheringData.PerceptionReq}, current: {DiscipleOfLand.Perception}");
                            AbortAutoGather();
                            return;
                        }

                        // If flying direct path to offset, complete navigation first, since offset is expected to be on the ground.
                        // Otherwise, stop once in range to interact.
                        if (vSeparation < 3 && !(_navState.offset && Dalamud.Conditions[ConditionFlag.InFlight] && IsPathing))
                        {

                            StopNavigation();

                            var targetGatheringType = targetItem.GatheringType.ToGroup();
                            var isUmbralItem = UmbralNodes.IsUmbralItem(targetItem.ItemId);
                            if (isUmbralItem && Functions.InTheDiadem())
                            {
                                var currentWeather = EnhancedCurrentWeather.GetCurrentWeatherId();
                                if (UmbralNodes.IsUmbralWeather(currentWeather))
                                {
                                    var umbralWeather = (UmbralNodes.UmbralWeatherType)currentWeather;
                                    targetGatheringType = umbralWeather switch
                                    {
                                        UmbralNodes.UmbralWeatherType.UmbralFlare => GatheringType.Miner,
                                        UmbralNodes.UmbralWeatherType.UmbralLevin => GatheringType.Miner,
                                        UmbralNodes.UmbralWeatherType.UmbralDuststorms => GatheringType.Botanist,
                                        UmbralNodes.UmbralWeatherType.UmbralTempest => GatheringType.Botanist,
                                        _ => targetGatheringType
                                    };
                                }
                            }
                            
                            var shouldSkipJobSwitch = Functions.InTheDiadem() && _hasGatheredUmbralThisSession;
                            
                            if (targetGatheringType != JobAsGatheringType && targetGatheringType != GatheringType.Multiple && !shouldSkipJobSwitch) {
                                if (ChangeGearSet(targetGatheringType, 0)){
                                    EnqueueNodeInteraction(gameObject, targetItem);
                                } else {
                                    AbortAutoGather();
                                }
                            }
                            else
                            {
                                if (shouldSkipJobSwitch && targetGatheringType != JobAsGatheringType)
                                {
                                    GatherBuddy.Log.Information($"[Umbral] Skipping job switch at node after umbral gathering (staying on {JobAsGatheringType})");
                                }
                                EnqueueNodeInteraction(gameObject, targetItem);
                            }
                        } 
                        else
                        {
                            Navigate(gameObject.Position, false);
                        }
                    }
                }
            }
            else
            {
                Navigate(gameObject.Position, ShouldFly(gameObject.Position));
            }
        }

        private void ForceLandAndDismount()
        {
            try
            {
                var floor = VNavmesh.Query.Mesh.PointOnFloor(Player.Position, false, 3);
                if (floor != null)
                {
                    Navigate(floor.Value, true, direct: true);
                    TaskManager.Enqueue(() => !IsPathGenerating);
                    TaskManager.DelayNext(50);
                    TaskManager.Enqueue(() => !IsPathing, 1000);
                    EnqueueDismount();
                }
            }
            catch { }
            // If even that fails, do advanced unstuck
            TaskManager.Enqueue(() => { if (Dalamud.Conditions[ConditionFlag.Mounted]) _advancedUnstuck.Force(); });
        }

        private void MoveToCloseSpearfishingNode(IGameObject gameObject, Classes.Fish targetFish)
        {
            var hSeparation = Vector2.Distance(gameObject.Position.ToVector2(), Player.Position.ToVector2());
            var vSeparation = Math.Abs(gameObject.Position.Y - Player.Position.Y);

            if (hSeparation < 3.5)
            {
                if (vSeparation < 3)
                {
                    if (Dalamud.Conditions[ConditionFlag.Mounted])
                    {
                        EnqueueDismount();
                    }
                    else
                    {
                        EnqueueSpearfishingNodeInteraction(gameObject, targetFish);
                    }
                }

                if (!Dalamud.Conditions[ConditionFlag.Diving])
                {
                    TaskManager.Enqueue(() => { if (!Dalamud.Conditions[ConditionFlag.Gathering]) Navigate(gameObject.Position, false); });
                }
            }
            else if (hSeparation < Math.Max(GatherBuddy.Config.AutoGatherConfig.MountUpDistance, 5))
            {
                Navigate(gameObject.Position, false);
            }
            else
            {
                Navigate(gameObject.Position, ShouldFly(gameObject.Position));
            }
        }

        private void StopNavigation()
        {
            // Reset navigation logic here
            if (_navState.cts != null && _navState.task != null)
            {
                var cts = _navState.cts;
                cts.Cancel();
                _navState.task.ContinueWith(_ => cts.Dispose());
            }

            _navState = default;
            if (VNavmesh.Enabled)
                VNavmesh.Path.Stop();
        }

        private unsafe void SetRotation(Angle angle)
        {
            var playerObject = (GameObject*)Player.Object.Address;
            GatherBuddy.Log.Debug($"Setting rotation to {angle.Rad}");
            playerObject->SetRotation(angle.Rad);
        }

        private void Navigate(Vector3 destination, bool shouldFly, bool preferGround = false, bool direct = false)
        {
            var canMount = Vector2.Distance(destination.ToVector2(), Player.Position.ToVector2()) >= GatherBuddy.Config.AutoGatherConfig.MountUpDistance && CanMount();
            if (!Dalamud.Conditions[ConditionFlag.Mounted] && canMount)
            {
                EnqueueMountUp();
                if (!GatherBuddy.Config.AutoGatherConfig.MoveWhileMounting)
                {
                    StopNavigation();
                    return;
                }
            }

            var landingDistance = GatherBuddy.Config.AutoGatherConfig.LandingDistance;

            if (_navState.destination == destination && _navState.stage == PathfindingStage.RetryCombinedPathfinding 
                && _navState.task == null && Environment.TickCount64 - _navState.lastTry > 1000)
            {
                _navState.lastTry = Environment.TickCount64;
                _navState.cts = new CancellationTokenSource();
                _navState.task = FindCombinedPath(Player.Position, destination, landingDistance, _navState.cts.Token);
                GatherBuddy.Log.Debug($"Retrying combined pathfinding to {destination}.");
                return;
            }                

            if (_navState.destination == destination && (IsPathing || _navState.task != null))
                return; 

            StopNavigation();

            shouldFly &= canMount || Dalamud.Conditions[ConditionFlag.Mounted];
            shouldFly |= Dalamud.Conditions[ConditionFlag.Diving];

            var offsettedDestination = GetCorrectedDestination(destination, preferGround);
            _navState.destination = destination;
            _navState.flying = shouldFly;
            _navState.mountingUp = shouldFly && !Dalamud.Conditions[ConditionFlag.Mounted];
            _navState.direct = direct || !shouldFly || landingDistance == 0 || destination != offsettedDestination || Dalamud.Conditions[ConditionFlag.Diving];
            _navState.offset = destination != offsettedDestination;
            _navState.cts = new CancellationTokenSource();

            if (_navState.direct)
            {
                _navState.task = VNavmesh.Nav.PathfindCancelable(Player.Position, offsettedDestination, shouldFly, _navState.cts.Token);
                GatherBuddy.Log.Debug($"Starting direct pathfinding to {offsettedDestination} (original: {destination}), flying: {shouldFly}.");
            }
            else
            {
                _navState.lastTry = Environment.TickCount64;
                _navState.stage = PathfindingStage.InitialCombinedPathfinding;
                _navState.task = FindCombinedPath(Player.Position, destination, landingDistance, _navState.cts.Token);
                GatherBuddy.Log.Debug($"Starting combined pathfinding to {destination}.");
            }
        }

        private void HandlePathfinding()
        {
            if (_navState.destination == default)
                return;

            var landingDistance = GatherBuddy.Config.AutoGatherConfig.LandingDistance;
            var player = Player.Position;

            if (_navState.flying && _navState.stage == PathfindingStage.Done
                && !Dalamud.Conditions[ConditionFlag.Diving]
                && Vector3.Distance(player, _navState.destination) < landingDistance * 1.1f)
            {
                // Switch vnavmesh to no-fly mode when close to landing point
                var wp = VNavmesh.Path.ListWaypoints().ToList();
                VNavmesh.Path.Stop();
                VNavmesh.Path.MoveTo(wp, false);
                _navState.flying = false;
                GatherBuddy.Log.Debug($"Switching to ground movement, {wp.Count} waypoints left.");
                return;
            }

            if (_navState.flying && _navState.mountingUp && Dalamud.Conditions[ConditionFlag.Mounted])
            {
                // Switch vnavmesh to fly mode when mounted up
                var wp = VNavmesh.Path.ListWaypoints()
                    // Remove waypoints that are too close.
                    .SkipWhile(p => Vector2.DistanceSquared(player.AsVector2(), p.AsVector2()) < 16f)
                    .ToList();

                VNavmesh.Path.Stop();
                VNavmesh.Path.MoveTo(wp, true);
                _navState.mountingUp = false;
                GatherBuddy.Log.Debug($"Switching to flying movement, {wp.Count} waypoints left.");
                return;
            }

            if (_navState.task == null || _navState.cts == null || !_navState.task.IsCompleted)
                return;

            List<Vector3> path;
            try
            {
                path = _navState.task.Result;
            } catch (Exception ex) {
                GatherBuddy.Log.Error($"Pathfinding task threw an exception: {ex.Message}");
                StopNavigation();
                _advancedUnstuck.Force();
                return;
            }
            _navState.cts.Dispose();
            _navState.task = null;
            _navState.cts = null;

            if (path.Count == 0)
            {
                if (_navState.direct || _navState.stage == PathfindingStage.FallbackDirectPathfinding)
                {
                    GatherBuddy.Log.Error($"VNavmesh failed to find a path.");
                    StopNavigation();
                    _advancedUnstuck.Force();
                }
                else if (_navState.stage == PathfindingStage.InitialCombinedPathfinding)
                {
                    GatherBuddy.Log.Debug($"VNavmesh failed to find a combined path, falling back to direct path.");
                    _navState.stage++;
                    _navState.cts = new CancellationTokenSource();
                    _navState.task = VNavmesh.Nav.PathfindCancelable(player, _navState.destination, _navState.flying, _navState.cts.Token);
                }
                else if (_navState.stage != PathfindingStage.RetryCombinedPathfinding)
                {
                    GatherBuddy.Log.Error($"BUG: Pathfinding failure at unexpected stage {_navState.stage}.");
                    AbortAutoGather();
                }
            }
            else
            {
                var pathtype = "unknown";
                if (_navState.direct)
                    pathtype = "direct";
                else switch (_navState.stage)
                    {
                        case PathfindingStage.InitialCombinedPathfinding:
                            pathtype = "combined";
                            _navState.stage = PathfindingStage.Done;
                            break;
                        case PathfindingStage.FallbackDirectPathfinding:
                            pathtype = "fallback direct";
                            _navState.stage++;
                            break;
                        case PathfindingStage.RetryCombinedPathfinding:
                            pathtype = "combined";
                            _navState.stage++;
                            RemovePassedWaypoints(path);
                            break;
                    }
                VNavmesh.Path.Stop();
                VNavmesh.Path.MoveTo(path, _navState.flying && !_navState.mountingUp);
                GatherBuddy.Log.Debug($"VNavmesh started moving via {pathtype} path, {path.Count} waypoints.");
            }

            static void RemovePassedWaypoints(List<Vector3> path)
            {
                var p = Player.Position;
                var t = path[^1];
                var fwd = new Vector3(t.X - p.X, 0f, t.Z - p.Z);
                if (fwd.LengthSquared() < 1f) return;
                fwd = Vector3.Normalize(fwd);

                var n = 0;
                while (n < path.Count)
                {
                    var next = new Vector3(path[n].X - p.X, 0f, path[n].Z - p.Z);
                    if (Vector3.Dot(fwd, next) > 0) break;
                    n++;
                }
                path.RemoveRange(0, n);
            }
        }

        private static async Task<List<Vector3>> FindCombinedPath(Vector3 player, Vector3 target, float landingDistance, CancellationToken token)
        {
            var point = VNavmesh.Query.Mesh.PointOnFloor(player + Vector3.Create(0, 1, 0), false, 15f);
            if (point == null) return [];

            var groundPath = await VNavmesh.Nav.PathfindCancelable(point.Value, target, false, token);
            if (groundPath.Count == 0) return [];

            var n = FindIntersection(groundPath, target, landingDistance);
            var landingWP = GetPointAtRadius(groundPath[n], groundPath[n + 1], target, landingDistance);
            if (Math.Abs(target.Y - landingWP.Y) > 10f) return []; // Diadem sanity check
            var flyPath = await VNavmesh.Nav.PathfindCancelable(player, landingWP, true, token);
            if (flyPath.Count == 0) return [];

            flyPath.AddRange(groundPath.Skip(n + 1));
            return flyPath;

            int FindIntersection(List<Vector3> wp, Vector3 p, float radius)
            {
                var r2 = radius * radius;
                for (var i = wp.Count - 2; i > 0; i--)
                {
                    if (Vector3.DistanceSquared(wp[i], p) > r2)
                        return i;
                }
                return 0;
            }

            Vector3 GetPointAtRadius(Vector3 p1, Vector3 p2, Vector3 target, float radius)
            {
                var d = (p2 - p1).ToVector2();        // Segment direction vector.
                var f = (p1 - target).ToVector2();    // Vector from target to segment start.

                // Quadratic coefficients: at^2 + bt + c = 0.
                var a = Vector2.Dot(d, d);
                var b = 2 * Vector2.Dot(f, d);
                var c = Vector2.Dot(f, f) - radius * radius;

                var discriminant = b * b - 4 * a * c;

                // If discriminant < 0, there is no intersection (math safety).
                if (discriminant < 0) return p1;

                discriminant = (float)Math.Sqrt(discriminant);

                // Calculate the two possible intersection points on the infinite line.
                var t1 = (-b - discriminant) / (2 * a);
                var t2 = (-b + discriminant) / (2 * a);

                // Since one point is inside and one is outside, one 't' will be between 0 and 1.
                var t = (t1 >= 0 && t1 <= 1) ? t1 : t2;

                // Final point on the segment.
                return p1 + (p2 - p1) * t;
            }
        }

        private static Vector3 GetCorrectedDestination(Vector3 destination, bool preferGround = false)
        {
            const float MaxHorizontalSeparation = 3.0f;
            const float MaxVerticalSeparation = 2.5f;

            try
            {
                float separation;
                if (WorldData.NodeOffsets.TryGetValue(destination, out var offset))
                {
                    offset = VNavmesh.Query.Mesh.NearestPoint(offset, MaxHorizontalSeparation, MaxVerticalSeparation).GetValueOrDefault(offset);
                    if ((separation = Vector2.Distance(offset.ToVector2(), destination.ToVector2())) > MaxHorizontalSeparation)
                        GatherBuddy.Log.Warning($"Offset is ignored because the horizontal separation {separation} is too large after correcting for mesh. Maximum allowed is {MaxHorizontalSeparation}.");
                    else if ((separation = Math.Abs(offset.Y - destination.Y)) > MaxVerticalSeparation)
                        GatherBuddy.Log.Warning($"Offset is ignored because the vertical separation {separation} is too large after correcting for mesh. Maximum allowed is {MaxVerticalSeparation}.");
                    else
                        return offset;
                }

                // There was code that corrected the destination to the nearest point on the mesh, but testing showed that
                // navigating directly to the node yields better results, and points within landing distance are on the mesh anyway.

                if (preferGround)
                {
                    const float GroundSearchRadius = 15f;
                    const float MaxGroundHorizontalSeparation = 7.5f;
                    const float MaxGroundVerticalSeparation = 10f;
                    
                    var groundPoint = VNavmesh.Query.Mesh.PointOnFloor(destination, false, GroundSearchRadius).GetValueOrDefault(destination);
                    var hDist = Vector2.Distance(groundPoint.ToVector2(), destination.ToVector2());
                    var vDist = Math.Abs(groundPoint.Y - destination.Y);
                        
                    if (hDist <= MaxGroundHorizontalSeparation && vDist <= MaxGroundVerticalSeparation)
                    {
                        return groundPoint;
                    }
                }
            }
            catch (Exception) { }

            return destination;
        }

        private void MoveToFarNode(Vector3 position)
        {
            var farNode = position;

            Navigate(farNode, ShouldFly(farNode));
        }

        private unsafe void MoveToFishingSpot(Vector3 position, Angle angle)
        {
            Navigate(position, ShouldFly(position), preferGround: true);
        }

        public static Aetheryte? FindClosestAetheryte(ILocation location)
        {
            var aetheryte = location.ClosestAetheryte;

            var territory = location.Territory;
            if (ForcedAetherytes.ZonesWithoutAetherytes.FirstOrDefault(x => x.ZoneId == territory.Id).AetheryteId is var alt && alt > 0)
                territory = GatherBuddy.GameData.Aetherytes[alt].Territory;

            if (aetheryte == null || !Teleporter.IsAttuned(aetheryte.Id) || aetheryte.Territory != territory)
            {
                aetheryte = territory.Aetherytes
                    .Where(a => Teleporter.IsAttuned(a.Id))
                    .OrderBy(a => a.WorldDistance(territory.Id, location.IntegralXCoord, location.IntegralYCoord))
                    .FirstOrDefault();
            }

            return aetheryte;
        }

        private bool MoveToTerritory(ILocation location)
        {
            var aetheryte = FindClosestAetheryte(location);
            if (aetheryte == null)
            {
                Communicator.PrintError("Couldn't find an attuned aetheryte to teleport to.");
                return false;
            }

            GatherBuddy.Log.Debug($"[MoveToTerritory] Teleporting to {aetheryte.Name}");
            EnqueueActionWithDelay(() => Teleporter.Teleport(aetheryte.Id));
            TaskManager.Enqueue(() => Dalamud.Conditions[ConditionFlag.BetweenAreas]);
            TaskManager.Enqueue(() => !Dalamud.Conditions[ConditionFlag.BetweenAreas]);
            TaskManager.DelayNext(1500);

            return true;
        }
    }
}
