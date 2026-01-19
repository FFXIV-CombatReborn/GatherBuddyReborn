using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using GatherBuddy.AutoGather.AtkReaders;
using GatherBuddy.AutoGather.Extensions;
using GatherBuddy.AutoGather.Helpers;
using GatherBuddy.AutoGather.Lists;
using GatherBuddy.Classes;
using GatherBuddy.Helpers;
using GatherBuddy.Plugin;
using GatherBuddy.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace GatherBuddy.AutoGather
{
    public partial class AutoGather
    {
        private unsafe void EnqueueNodeInteraction(IGameObject gameObject, Gatherable targetItem)
        {
            var targetSystem = TargetSystem.Instance();
            Dalamud.ToastGui.ErrorToast -= HandleNodeInteractionErrorToast;
            Dalamud.ToastGui.ErrorToast += HandleNodeInteractionErrorToast;

            if (targetSystem == null)
                return;

            // Delay interaction by one frame after stopping movement to avoid the "Unable to execute command while in flight" error.
            TaskManager.Enqueue(() =>
            {
                _lastNodeInteractionTime = Environment.TickCount64;
                targetSystem->OpenObjectInteraction((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)gameObject.Address);
                GatherBuddy.Log.Debug($"Interacting with node, "
                    + $"distanceXZ: {Vector2.Distance(gameObject.Position.ToVector2(), Player.Position.ToVector2())}, "
                    + $"distanceY: {Player.Position.Y - gameObject.Position.Y}, "
                    + $"Conditions: {string.Join(' ', Dalamud.Conditions.AsReadOnlySet())}.");
            });

            // If flying, it takes up to 600 ms for the Mounted condition to fade and up to 500 ms more for the Gathering condition to appear.

            TaskManager.Enqueue(() =>
            {
                if (Dalamud.Conditions[ConditionFlag.Gathering])
                {
                    GatherBuddy.Log.Debug($"Opening node took {Environment.TickCount64 - _lastNodeInteractionTime} ms.");
                    return true;
                }
                return false;
            }, 1100, "Node interaction");

            TaskManager.Enqueue(() =>
            {
                Dalamud.ToastGui.ErrorToast -= HandleNodeInteractionErrorToast;
                if (!Dalamud.Conditions[ConditionFlag.Gathering] && Dalamud.Conditions[ConditionFlag.Mounted] && Dalamud.Conditions[ConditionFlag.InFlight] && !Dalamud.Conditions[ConditionFlag.Diving])
                {
                    ForceLandAndDismount();
                }
            });
        }

        private unsafe void HandleNodeInteractionErrorToast(ref SeString message, ref bool isHandled)
        {
            // With current navigation, getting 'Unable to execute command while in flight' means we are either too high above the ground
            // or even not above traversable ground, so retrying node interaction is pointless, force dismount instead.
            // In the rare cases of 'Unable to execute command while jumping' we are already dismounted, so just Abort() to retry.

            Dalamud.ToastGui.ErrorToast -= HandleNodeInteractionErrorToast;
            var text = message.TextValue;
            GatherBuddy.Log.Debug($"Node interaction error toast detected: {text}.");
            TaskManager.Abort();
            if (Dalamud.Conditions[ConditionFlag.InFlight])
                ForceLandAndDismount();
            else
                TaskManager.DelayNext(400);
        }

        private unsafe void EnqueueSpearfishingNodeInteraction(IGameObject gameObject, Classes.Fish targetFish)
        {
            var targetSystem = TargetSystem.Instance();
            if (targetSystem == null)
                return;

            var fishForSnapshot = targetFish;
            if (fishForSnapshot != null)
            {
                var actualFishToGather = fishForSnapshot;
                bool gatheringPrerequisites = false;
                
                // Check if THIS SPECIFIC target fish has predator requirements
                if (fishForSnapshot.Predators.Any())
                {
                    var requirementFish = fishForSnapshot.Predators.Select(p => p.Item1).ToList();
                    SnapshotSpearfishingInventory(requirementFish);
                    
                    // Only check FIRST predator for shadow node spawning (rest are caught within shadow node)
                    var (firstPredator, requiredCount) = fishForSnapshot.Predators.First();
                    var caughtCount = SpearfishingSessionCatches.TryGetValue(firstPredator.ItemId, out var count) ? count : 0;
                    var firstPredatorMet = caughtCount >= requiredCount;
                    
                    if (!firstPredatorMet)
                    {
                        actualFishToGather = firstPredator;
                        gatheringPrerequisites = true;
                    }
                }
                
                if (gatheringPrerequisites)
                {
                    if (Player.Status.Any(s => Actions.CollectorsGlove.StatusProvide.Contains(s.StatusId)))
                    {
                        TaskManager.Enqueue(() => UseAction(Actions.CollectorsGlove));
                        TaskManager.DelayNext(1000);
                    }
                }
                else if (actualFishToGather.ItemData.IsCollectable && Player.Status.All(s => !Actions.CollectorsGlove.StatusProvide.Contains(s.StatusId)))
                {
                    TaskManager.Enqueue(() => UseAction(Actions.CollectorsGlove));
                    TaskManager.DelayNext(1000);
                }
            }

            TaskManager.Enqueue(() => targetSystem->OpenObjectInteraction((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)gameObject.Address));
            TaskManager.Enqueue(() => Dalamud.Conditions[ConditionFlag.Gathering], 500);
            
            TaskManager.Enqueue(() => {
                if (!Dalamud.Conditions[ConditionFlag.Gathering])
                {
                    targetSystem->OpenObjectInteraction((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)gameObject.Address);
                }
            });
            TaskManager.Enqueue(() => Dalamud.Conditions[ConditionFlag.Gathering], 500);
        }

        private unsafe void EnqueueGatherItem(ItemSlot slot)
        {
            if (slot.Item.ItemData.IsCollectable)
            {
                // Since it's possible that we are not gathering the top item in the list,
                // we need to remember what we are going to gather inside MasterpieceAddon
                CurrentCollectableRotation = new CollectableRotation(MatchConfigPreset(slot.Item), slot.Item, _activeItemList.FirstOrDefault(x => x.Item == slot.Item).Quantity);
            }

            EnqueueActionWithDelay(slot.Gather);

            if (slot.Item.IsTreasureMap)
            {
                TaskManager.Enqueue(() => Dalamud.Conditions[ConditionFlag.ExecutingGatheringAction], 1000);
                TaskManager.Enqueue(() => !Dalamud.Conditions[ConditionFlag.ExecutingGatheringAction]);
                TaskManager.Enqueue(DiscipleOfLand.RefreshNextTreasureMapAllowance);
            }
        }

        /// <summary>
        /// Checks if desired item could or should be gathered and may change it to something more suitable
        /// </summary>
        /// <returns>UseSkills: True if the selected item is in the gathering list; false if we gather a collectable or some unneeded junk
        /// Slot: ItemSlot of item to gather</returns>
        private (bool UseSkills, ItemSlot Slot) GetItemSlotToGather(IEnumerable<GatherTarget> gatherTarget)
        {
            if (GatheringWindowReader == null)
                throw new InvalidOperationException("GatheringWindowReader is null");
            var available = GatheringWindowReader.ItemSlots
                .Where(i => !i.IsEmpty)
                .Where(CheckItemOvercap)
                .ToList();

            if (GatherBuddy.Config.AutoGatherConfig.AlwaysGatherMaps && available.Any(i => i.Item.IsTreasureMap) && DiscipleOfLand.NextTreasureMapAllowance < GatherBuddy.Time.ServerTime.DateTime)
            {
                return (false, available.First(i => i.Item.IsTreasureMap));
            }

            var target = available.FirstOrDefault(a => gatherTarget.Any(i => i.Gatherable?.ItemId == a.Item.ItemId));

            //Gather crystals when using The Giving Land
            if (HasGivingLandBuff && (target == null || !target.Item.IsCrystal))
            {
                var crystal = GetAnyCrystalInNode();
                if (crystal != null)
                    return (true, crystal);
            }

            if (target != null && target.Item.GetInventoryCount() < gatherTarget.First(t => t.Gatherable?.ItemId == target.Item.ItemId).Quantity)
            {
                //The target item is found in the node, would not overcap and we need to gather more of it
                return (!target.IsCollectable, target);
            }

            //Items in the gathering list
            var gatherList = ItemsToGather
                //Join node slots, retaining list order
                .Join(available, i => i.Item, s => s.Item, (i, s) => (Slot: s, i.Quantity))
                //And we need more of them
                .Where(x => x.Slot.Item.GetInventoryCount() < x.Quantity)
                .Select(x => x.Slot);

            //Items in the fallback list
            var fallbackList = _plugin.AutoGatherListsManager.FallbackItems
                //Join node slots, retaining list order
                .Join(available, i => i.Item, s => s.Item, (i, s) => (Slot: s, i.Quantity))
                //And we need more of them
                .Where(x => x.Slot.Item.GetInventoryCount() < x.Quantity)
                .Select(x => x.Slot);

            var fallbackSkills = GatherBuddy.Config.AutoGatherConfig.UseSkillsForFallbackItems;

            //If there is any other item that we want in the node, gather it
            var slot = gatherList.FirstOrDefault();
            if (slot != null)
            {
                return (!slot.IsCollectable, slot);
            }

            //If there is any fallback item, gather it
            slot = fallbackList.FirstOrDefault();
            if (slot != null)
            {
                return (fallbackSkills && !slot.IsCollectable, slot);
            }

            if (Diadem.IsInside && gatherTarget.Any())
            {
                var targetLevels = gatherTarget
                    .Where(gt => gt.Gatherable != null)
                    .Select(gt => gt.Gatherable!.Level)
                    .Distinct()
                    .ToHashSet();

                slot = available
                    .Where(s => s.Item != null && targetLevels.Contains(s.Item.Level))
                    .Where(s => !s.Item!.IsTreasureMap && !s.IsCollectable)
                    .OrderByDescending(s => s.ItemLevel)
                    .FirstOrDefault();

                if (slot != null)
                {
                    return (false, slot);
                }
            }

            //Check if we should and can abandon the node
            if (GatherBuddy.Config.AutoGatherConfig.AbandonNodes)
                throw new NoGatherableItemsInNodeException();

            if (target != null)
            {
                //Gather unneeded target item as a fallback
                return (false, target);
            }

            //Gather any crystals
            slot = GetAnyCrystalInNode();
            if (slot != null)
            {
                return (false, slot);
            }
            //If there are no crystals, gather anything which is not treasure map nor collectable
            slot = available.FirstOrDefault(s => (!s.Item?.IsTreasureMap ?? false) && !s.IsCollectable);
            if (slot != null)
            {
                return (false, slot);
            }
            //Abort if there are no items we can gather
            throw new NoGatherableItemsInNodeException();
        }

        private bool CheckItemOvercap(ItemSlot s)
        {
            if (s.Item == null)
                return false;
            //If it's a treasure map, we can have only one in the inventory
            if (s.Item.IsTreasureMap && GetInventoryItemCount(s.Item.ItemId) != 0)
                return false;
            //If it's a crystal, we can't have more than 9999
            if (s.Item.IsCrystal && GetInventoryItemCount(s.Item.ItemId) > 9999 - s.Yield)
                return false;
            return true;
        }
        
        private ItemSlot? GetAnyCrystalInNode()
        {
            if (GatheringWindowReader == null)
                throw new InvalidOperationException("GatheringWindowReader is null");
            return GatheringWindowReader.ItemSlots
                .Where(s => s.Item != null)
                .Where(s => s.Item!.IsCrystal)
                .Where(CheckItemOvercap)
                //Prioritize crystals in the gathering list
                .GroupJoin(_activeItemList.Where(i => i.Gatherable?.IsCrystal ?? false), s => s.Item, i => i.Item, (s, x) => (Slot: s, Order: x.Any()?1:0))
                .OrderBy(x => x.Order)
                //Prioritize crystals with a lower amount in the inventory
                .ThenBy(x => x.Slot.Item!.GetInventoryCount())
                .Select(x => x.Slot)
                .FirstOrDefault();
        }
    }
}
