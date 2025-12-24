using Dalamud.Game.ClientState.Conditions;
using GatherBuddy.Helpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using GatherBuddy.Classes;
using GatherBuddy.CustomInfo;
using GatherBuddy.Data;
using GatherBuddy.Interfaces;
using GatherBuddy.Plugin;
using GatherBuddy.SeFunctions;
using System;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using GatherBuddy.Enums;
using GatherBuddy.Utilities;
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
                Navigate(floor, true, direct: true);
                TaskManager.Enqueue(() => !IsPathGenerating);
                TaskManager.DelayNext(50);
                TaskManager.Enqueue(() => !IsPathing, 1000);
                EnqueueDismount();
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
            // For example, reinitiate navigation to the destination
            _navState = default;
            if (VNavmesh.Enabled)
            {
                VNavmesh.Path.Stop();
                if (IsPathGenerating)
                    VNavmesh.Nav.PathfindCancelAll();
            }
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

            if (_navState.destination == destination && (IsPathing || IsPathGenerating || _navState.task != null))
                return; 

            StopNavigation();

            shouldFly &= canMount || Dalamud.Conditions[ConditionFlag.Mounted];
            shouldFly |= Dalamud.Conditions[ConditionFlag.Diving];

            var offsettedDestination = GetCorrectedDestination(destination, preferGround);
            var landingDistance = GatherBuddy.Config.AutoGatherConfig.LandingDistance;
            _navState.destination = destination;
            _navState.flying = shouldFly;
            _navState.mountingUp = shouldFly && !Dalamud.Conditions[ConditionFlag.Mounted];
            _navState.directPath = direct || !shouldFly || landingDistance == 0 || destination != offsettedDestination || Dalamud.Conditions[ConditionFlag.Diving];
            _navState.offset = destination != offsettedDestination;

            if (_navState.directPath)
            {
                _navState.task = VNavmesh.Nav.Pathfind(Player.Position, offsettedDestination, shouldFly);
                GatherBuddy.Log.Debug($"Starting direct pathfinding to {offsettedDestination} (original: {destination}), flying: {shouldFly}.");
            }
            else
            {
                var floorPoint = Player.Position;
                if (Dalamud.Conditions[ConditionFlag.InFlight])
                {
                    try
                    {
                        floorPoint = VNavmesh.Query.Mesh.PointOnFloor(Player.Position + Vector3.Create(0, 1, 0), false, 5);
                    }
                    catch { }
                }
                _navState.task = VNavmesh.Nav.Pathfind(floorPoint, destination, false);
                GatherBuddy.Log.Debug($"Starting ground pathfinding to {destination} from floor point {floorPoint}.");
            }
        }

        private void HandlePathfinding()
        {
            if (_navState.destination == default)
                return;

            var landingDistance = GatherBuddy.Config.AutoGatherConfig.LandingDistance;

            if (_navState.flying && !_navState.directPath && !Dalamud.Conditions[ConditionFlag.Diving] && Vector3.Distance(Player.Position, _navState.destination) < landingDistance * 1.1f)
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
                var wp = VNavmesh.Path.ListWaypoints().ToList();
                VNavmesh.Path.Stop();
                VNavmesh.Path.MoveTo(wp, true);
                _navState.mountingUp = false;
                GatherBuddy.Log.Debug($"Switching to flying movement, {wp.Count} waypoints left.");
                return;
            }

            if (_navState.task == null || !_navState.task.IsCompleted)
                return;

            var path = _navState.task.Result;
            var groundPath = _navState.groundPath;
            _navState.task = null;
            _navState.groundPath = null;

            if (path.Count == 0)
            {
                if (_navState.directPath || groundPath != null)
                {
                    GatherBuddy.Log.Error($"VNavmesh failed to find a path.");
                    StopNavigation();
                    _advancedUnstuck.Force();
                }
                else
                {
                    GatherBuddy.Log.Warning($"VNavmesh failed to find a ground path, falling back to flying path.");
                    _navState.directPath = true;
                    _navState.task = VNavmesh.Nav.Pathfind(Player.Position, _navState.destination, _navState.flying);
                }
            }
            else
            {
                if (_navState.directPath)
                {
                    VNavmesh.Path.MoveTo(path, _navState.flying && !_navState.mountingUp);
                    GatherBuddy.Log.Debug($"VNavmesh started moving via direct path, {path.Count} waypoints.");
                }
                else if (groundPath == null)
                {
                    var i = path.Count - 1;
                    while (i >= 0)
                    {
                        if (Vector3.Distance(path[i], _navState.destination) > landingDistance) break;
                        i--;
                    }

                    if (i >= 0 && i < path.Count - 1)
                    {
                        var landWP = GetPointAtRadius(path[i], path[i + 1], _navState.destination, landingDistance);
                        _navState.task = VNavmesh.Nav.Pathfind(Player.Position, landWP, _navState.flying);
                        _navState.groundPath = path.GetRange(i + 1, path.Count - i - 1);
                        GatherBuddy.Log.Debug($"Landing waypoint at {landWP}, {Vector3.Distance(landWP, _navState.destination)}; {_navState.groundPath.Count} ground waypoints total.");
                    }
                    else
                    {
                        // Probably too close to target
                        VNavmesh.Path.MoveTo(path, false);
                        _navState.flying = false;
                        GatherBuddy.Log.Debug($"VNavmesh started moving via ground path, {path.Count} waypoints.");
                    }
                }
                else
                {
                    path.AddRange(groundPath);
                    foreach (var p in path)
                        GatherBuddy.Log.Debug($"Waypoint: {p}, {Vector3.Distance(p, _navState.destination)}");

                    VNavmesh.Path.MoveTo(path, _navState.flying && !_navState.mountingUp);
                    GatherBuddy.Log.Debug($"VNavmesh started moving via combined path, {path.Count} waypoints.");
                }
            }
        }

        private static Vector3 GetPointAtRadius(Vector3 p1, Vector3 p2, Vector3 target, float radius)
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

        private static Vector3 GetCorrectedDestination(Vector3 destination, bool preferGround = false)
        {
            const float MaxHorizontalSeparation = 3.0f;
            const float MaxVerticalSeparation = 2.5f;

            try
            {
                float separation;
                if (WorldData.NodeOffsets.TryGetValue(destination, out var offset))
                {
                    offset = VNavmesh.Query.Mesh.NearestPoint(offset, MaxHorizontalSeparation, MaxVerticalSeparation);
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
                    
                    try
                    {
                        var groundPoint = VNavmesh.Query.Mesh.PointOnFloor(destination, false, GroundSearchRadius);
                        var hDist = Vector2.Distance(groundPoint.ToVector2(), destination.ToVector2());
                        var vDist = Math.Abs(groundPoint.Y - destination.Y);
                        
                        if (hDist <= MaxGroundHorizontalSeparation && vDist <= MaxGroundVerticalSeparation)
                        {
                            return groundPoint;
                        }
                    }
                    catch (Exception)
                    {
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
