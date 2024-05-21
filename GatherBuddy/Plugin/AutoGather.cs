﻿using ClickLib.Structures;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GatherBuddy.Classes;
using GatherBuddy.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace GatherBuddy.Plugin
{
    public class AutoGather
    {
        public enum AutoStateType
        {
            Idle,
            WaitingForTeleport,
            Pathing,
            WaitingForNavmesh,
            GatheringNode,
            MovingToNode,
            Mounting,
            Dismounting,
            Error,
            Finish,
        }
        private readonly GatherBuddy _plugin;
        public string AutoStatus { get; set; } = "Not Running";
        public AutoStateType AutoState { get; set; } = AutoStateType.Idle;
        private AutoStateType _lastAutoState = AutoStateType.Idle;
        public AutoGather(GatherBuddy plugin)
        {
            _plugin = plugin;
        }

        private DateTime _teleportInitiated = DateTime.MinValue;

        public IEnumerable<GameObject> ValidGatherables = new List<GameObject>();

        public Gatherable? DesiredItem => _plugin.GatherWindowManager.ActiveItems.FirstOrDefault() as Gatherable;
        public bool IsPathing => GatherBuddy.Navmesh.IsPathing();
        public bool NavReady => GatherBuddy.Navmesh.IsReady();

        private void UpdateObjects()
        {
            ValidGatherables = Dalamud.ObjectTable.Where(g => g.ObjectKind == ObjectKind.GatheringPoint)
                        .Where(g => g.IsTargetable)
                        .Where(IsDesiredNode)
                        .OrderBy(g => Vector3.Distance(g.Position, Dalamud.ClientState.LocalPlayer.Position));

        }
        public void DoAutoGather()
        {
            if (!GatherBuddy.Config.AutoGather) return;
            NavmeshStuckCheck();
            InventoryCheck();

            UpdateObjects();
            DetermineAutoState();
        }
        private void PathfindToFarNode(Gatherable desiredItem)
        {
            if (desiredItem == null)
                return;
            var nodeList = desiredItem.NodeList;
            if (nodeList == null)
                return;
            var closestKnownNode = nodeList.SelectMany(n => n.WorldCoords).SelectMany(w => w.Value).OrderBy(n => Vector3.Distance(n, Dalamud.ClientState.LocalPlayer.Position)).FirstOrDefault();
            if (closestKnownNode == null)
                return;
            PathfindToNode(closestKnownNode);
        }
        private void PathfindToNode(Vector3 position)
        {
            if (IsPathing)
                return;
            GatherBuddy.Navmesh.PathfindAndMoveTo(position, true);
        }

        private void DetermineAutoState()
        {
            if (!NavReady)
            {
                AutoState = AutoStateType.WaitingForNavmesh;
                AutoStatus = "Waiting for Navmesh...";
                return;
            }

            if (IsPlayerBusy())
            {
                AutoState = AutoStateType.Idle;
                AutoStatus = "Player is busy...";
                return;
            }

            if (DesiredItem == null)
            {
                AutoState = AutoStateType.Finish;
                AutoStatus = "No active items in shopping list...";
                return;
            }

            var currentTerritory = Dalamud.ClientState.TerritoryType;

            if (!ValidGatherables.Any())
            {
                var location = DesiredItem.Locations.FirstOrDefault();
                if (location == null)
                {
                    AutoState = AutoStateType.Error;
                    AutoStatus = "No locations for item " + DesiredItem.Name[GatherBuddy.Language] + ".";
                    return;
                }

                if (location.Territory.Id != currentTerritory)
                {
                    if (_teleportInitiated < DateTime.Now)
                    {
                        _teleportInitiated = DateTime.Now.AddSeconds(15);
                        AutoState = AutoStateType.WaitingForTeleport;
                        AutoStatus = "Teleporting to " + location.Territory.Name + "...";
                        _plugin.Executor.GatherItem(DesiredItem);
                        return;
                    }
                    else
                    {
                        AutoState = AutoStateType.WaitingForTeleport;
                        AutoStatus = "Waiting for teleport...";
                        return;
                    }
                }

                if (!Dalamud.Conditions[ConditionFlag.Mounted])
                {
                    AutoState = AutoStateType.Mounting;
                    AutoStatus = "Mounting for travel...";
                    MountUp();
                    return;
                }

                AutoState = AutoStateType.Pathing;
                AutoStatus = "Pathing to node...";
                PathfindToFarNode(DesiredItem);
                return;
            }

            if (ValidGatherables.Any())
            {
                var targetGatherable = ValidGatherables.First();
                var distance = Vector3.Distance(targetGatherable.Position, Dalamud.ClientState.LocalPlayer.Position);

                if (distance < 3)
                {
                    if (Dalamud.Conditions[ConditionFlag.Mounted])
                    {
                        AutoState = AutoStateType.Dismounting;
                        AutoStatus = "Dismounting...";
                        Dismount();
                        return;
                    }
                    else if (Dalamud.Conditions[ConditionFlag.Gathering])
                    {
                        // This is where you can handle additional logic when close to the node without being mounted.
                        AutoState = AutoStateType.GatheringNode;
                        AutoStatus = $"Gathering {targetGatherable.Name}...";
                        GatherNode();
                        return;
                    }
                    else
                    {
                        AutoState = AutoStateType.GatheringNode;
                        AutoStatus = $"Targeting {targetGatherable.Name}...";
                        InteractNode(targetGatherable);
                        return;
                    }
                }
                else
                {
                    if (!Dalamud.Conditions[ConditionFlag.Mounted])
                    {
                        AutoState = AutoStateType.Mounting;
                        AutoStatus = "Mounting for travel...";
                        MountUp();
                        return;
                    }

                    if (AutoState != AutoStateType.MovingToNode)
                    {
                        AutoState = AutoStateType.MovingToNode;
                        AutoStatus = $"Moving to node {targetGatherable.Name} at {targetGatherable.Position}";
                        PathfindToNode(targetGatherable.Position);
                        return;
                    }
                }
            }

            AutoState = AutoStateType.Error;
            //AutoStatus = "Nothing to do...";
        }

        private unsafe void GatherNode()
        {
            var gatheringWindow = (AddonGathering*)Dalamud.GameGui.GetAddonByName("Gathering", 1);
            if (gatheringWindow == null) return;

            var ids = new List<uint>()
                    {
                    gatheringWindow->GatheredItemId1,
                    gatheringWindow->GatheredItemId2,
                    gatheringWindow->GatheredItemId3,
                    gatheringWindow->GatheredItemId4,
                    gatheringWindow->GatheredItemId5,
                    gatheringWindow->GatheredItemId6,
                    gatheringWindow->GatheredItemId7,
                    gatheringWindow->GatheredItemId8
                    };

            var itemIndex = ids.IndexOf(DesiredItem?.ItemId ?? 0);
            if (itemIndex < 0) return;
            //gatheringWindow->AtkUnitBase.UldManager.NodeList[25]->GetAsAtkComponentNode()->Component->UldManager.NodeList[21]->GetAsAtkTextNode()->NodeText.ToString();
            //gatheringWindow->AtkUnitBase.UldManager.NodeList[24]->GetAsAtkComponentNode()->Component->UldManager.NodeList[21]->GetAsAtkTextNode()->NodeText.ToString();
            //gatheringWindow->AtkUnitBase.UldManager.NodeList[23]->GetAsAtkComponentNode()->Component->UldManager.NodeList[21]->GetAsAtkTextNode()->NodeText.ToString();
            //gatheringWindow->AtkUnitBase.UldManager.NodeList[22]->GetAsAtkComponentNode()->Component->UldManager.NodeList[21]->GetAsAtkTextNode()->NodeText.ToString();
            //gatheringWindow->AtkUnitBase.UldManager.NodeList[21]->GetAsAtkComponentNode()->Component->UldManager.NodeList[21]->GetAsAtkTextNode()->NodeText.ToString();
            //gatheringWindow->AtkUnitBase.UldManager.NodeList[20]->GetAsAtkComponentNode()->Component->UldManager.NodeList[21]->GetAsAtkTextNode()->NodeText.ToString();
            //gatheringWindow->AtkUnitBase.UldManager.NodeList[19]->GetAsAtkComponentNode()->Component->UldManager.NodeList[21]->GetAsAtkTextNode()->NodeText.ToString();
            //gatheringWindow->AtkUnitBase.UldManager.NodeList[18]->GetAsAtkComponentNode()->Component->UldManager.NodeList[21]->GetAsAtkTextNode()->NodeText.ToString();

            var receiveEventAddress = new nint(gatheringWindow->AtkUnitBase.AtkEventListener.vfunc[2]);
            var eventDelegate = Marshal.GetDelegateForFunctionPointer<ReceiveEventDelegate>(receiveEventAddress);

            var target = AtkStage.GetSingleton();
            var eventData = EventData.ForNormalTarget(target, &gatheringWindow->AtkUnitBase);
            var inputData = InputData.Empty();

            eventDelegate.Invoke(&gatheringWindow->AtkUnitBase.AtkEventListener, ClickLib.Enums.EventType.CHANGE, (uint)itemIndex, eventData.Data, inputData.Data);
        }

        private unsafe delegate nint ReceiveEventDelegate(AtkEventListener* eventListener, ClickLib.Enums.EventType eventType, uint eventParam, void* eventData, void* inputData);

        private static unsafe ReceiveEventDelegate GetReceiveEvent(AtkEventListener* listener)
        {
            var receiveEventAddress = new IntPtr(listener->vfunc[2]);
            return Marshal.GetDelegateForFunctionPointer<ReceiveEventDelegate>(receiveEventAddress)!;
        }

        private bool _isInteracting = false;
        private unsafe void InteractNode(GameObject targetGatherable)
        {
            if (Dalamud.Conditions[ConditionFlag.Jumping])
                return;
            if (IsPlayerBusy()) return;
            var targetSystem = TargetSystem.Instance();
            if (targetSystem == null)
                return;
            if (_isInteracting) return;
            _isInteracting = true;
            Task.Run(() =>
            {
                System.Threading.Thread.Sleep(1000);
                targetSystem->OpenObjectInteraction((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)targetGatherable.Address);
                _isInteracting = false;
            });
        }

        private bool IsPlayerBusy()
        {
            var player = Dalamud.ClientState.LocalPlayer;
            if (player == null)
                return true;
            if (player.IsCasting)
                return true;
            if (player.IsDead)
                return true;

            return false;
        }

        private unsafe void InventoryCheck()
        {
            var presets = _plugin.GatherWindowManager.Presets;
            if (presets == null)
                return;

            var inventory = InventoryManager.Instance();
            if (inventory == null) return;

            foreach (var preset in presets)
            {
                var items = preset.Items;
                if (items == null)
                    continue;

                var indicesToRemove = new List<int>();

                for (int i = 0; i < items.Count; i++)
                {
                    var item = items[i];
                    var itemCount = inventory->GetInventoryItemCount(item.ItemId);
                    if (itemCount >= item.Quantity)
                    {
                        _plugin.GatherWindowManager.RemoveItem(preset, i);
                    }
                }
            }
        }
        private DateTime _lastNavReset = DateTime.MinValue;
        private void NavmeshStuckCheck()
        {
            if (_lastNavReset.AddSeconds(10) < DateTime.Now)
            {
                _lastNavReset = DateTime.Now;
                GatherBuddy.Navmesh.Reload();
            }
        }

        private bool IsDesiredNode(GameObject gameObject)
        {
            return DesiredItem?.NodeList.Any(n => n.WorldCoords.Keys.Any(k => k == gameObject.DataId)) ?? false;
        }


        private unsafe void Dismount()
        {
            var am = ActionManager.Instance();
            am->UseAction(ActionType.Mount, 0);
        }

        private unsafe void MountUp()
        {
            var am = ActionManager.Instance();
            var mount = GatherBuddy.Config.AutoGatherMountId;
            if (am->GetActionStatus(ActionType.Mount, mount) != 0) return;
            am->UseAction(ActionType.Mount, mount);
        }

    }
}