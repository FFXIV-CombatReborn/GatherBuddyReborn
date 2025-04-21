using Dalamud.Game.ClientState.Conditions;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using GatherBuddy.Classes;
using GatherBuddy.CustomInfo;
using GatherBuddy.Interfaces;
using GatherBuddy.Plugin;
using System;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using GatherBuddy.SeFunctions;
using GatherBuddy.Data;
using ECommons.MathHelpers;
using GatherBuddy.Enums;

namespace GatherBuddy.AutoGather
{
    public partial class AutoGather
    {
        private unsafe void EnqueueDismount()
        {
            TaskManager.Enqueue(() => Navigation.StopNavigation());

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

            TaskManager.Enqueue(() => Navigation.StopNavigation());
            EnqueueActionWithDelay(doMount);
            TaskManager.Enqueue(() => Svc.Condition[ConditionFlag.Mounted], 2000);
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

            if (hSeparation < 3.5)
            {
                var waitGP = targetItem.ItemData.IsCollectable && Player.Object.CurrentGp < config.CollectableMinGP;
                waitGP |= !targetItem.ItemData.IsCollectable && Player.Object.CurrentGp < config.GatherableMinGP;

                if (Dalamud.Conditions[ConditionFlag.Mounted] && (waitGP || Dalamud.Conditions[ConditionFlag.InFlight] || GetConsumablesWithCastTime(config) > 0))
                {
                    //Try to dismount early. It would help with nodes where it is not possible to dismount at vnavmesh's provided floor point
                    EnqueueDismount();
                    TaskManager.Enqueue(() => {
                        //If early dismount failed, navigate to the nearest floor point
                        if (Dalamud.Conditions[ConditionFlag.Mounted] && Dalamud.Conditions[ConditionFlag.InFlight] && !Dalamud.Conditions[ConditionFlag.Diving])
                        {
                            try
                            {
                                var floor = VNavmesh.Query.Mesh.PointOnFloor(Player.Position, false, 3);
                                VNavmesh.SimpleMove.PathfindAndMoveTo(floor, true);
                                TaskManager.Enqueue(() => !VNavmesh.Nav.PathfindInProgress());
                                TaskManager.DelayNext(50);
                                TaskManager.Enqueue(() => !VNavmesh.Path.IsRunning(), 1000);
                                EnqueueDismount();
                            }
                            catch { }
                            //If even that fails, do advanced unstuck
                            TaskManager.Enqueue(() => { if (Dalamud.Conditions[ConditionFlag.Mounted]) Navigation.ForceUnstuck(); });
                        }
                    });
                }
                else if (waitGP)
                {
                    Navigation.StopNavigation();
                    AutoStatus = "Waiting for GP to regenerate...";
                }
                else
                {
                    // Use consumables with cast time just before gathering a node when player is surely not mounted
                    if (GetConsumablesWithCastTime(config) is var consumable and > 0)
                    {
                        if (VNavmesh.Path.IsRunning())
                            Navigation.StopNavigation();
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

                        if (vSeparation < 3)                        
                            if (targetItem.GatheringType.ToGroup() != JobAsGatheringType && targetItem.GatheringType != GatheringType.Multiple) {
                                if (ChangeGearSet(targetItem.GatheringType.ToGroup(), 0)){
                                    EnqueueNodeInteraction(gameObject, targetItem);
                                } else {
                                    AbortAutoGather();
                                }
                            }
                            else {
                                EnqueueNodeInteraction(gameObject, targetItem);
                            }

                        // The node could be behind a rock or a tree and not be interactable. This happened in the Endwalker, but seems not to be reproducible in the Dawntrail.
                        // Enqueue navigation anyway, just in case.
                        // Also move if vertical separation is too large.
                        if (!Dalamud.Conditions[ConditionFlag.Diving])
                        {
                            TaskManager.Enqueue(() => { if (!Dalamud.Conditions[ConditionFlag.Gathering]) VNavmesh.SimpleMove.PathfindAndMoveTo(gameObject.Position, false); });
                        }
                    }
                }
            }
            else if (hSeparation < Math.Max(GatherBuddy.Config.AutoGatherConfig.MountUpDistance, 5) && !Dalamud.Conditions[ConditionFlag.Diving])
            {
                Navigation.CurrentDestination = gameObject.Position;
            }
            else
            {
                if (!Dalamud.Conditions[ConditionFlag.Mounted])
                {
                    EnqueueMountUp();
                }
                else
                {
                    Navigation.CurrentDestination = gameObject.Position;
                }
            }
        }

        private void MoveToFarNode(Vector3 position)
        {
            var farNode = position;

            if (!Dalamud.Conditions[ConditionFlag.Mounted])
            {
                EnqueueMountUp();
            }
            else
            {
                Navigation.CurrentDestination = farNode;
            }
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

            EnqueueActionWithDelay(() => Teleporter.Teleport(aetheryte.Id));
            TaskManager.Enqueue(() => Svc.Condition[ConditionFlag.BetweenAreas]);
            TaskManager.Enqueue(() => !Svc.Condition[ConditionFlag.BetweenAreas]);
            TaskManager.DelayNext(1500);

            return true;
        }
    }
}
